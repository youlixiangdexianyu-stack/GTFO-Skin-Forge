using ForgeSkin.Format;
using Sprite = UnityEngine.Sprite;
using Gear;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

// The picker's pictures of skins: each pack's own picture, drawn by the editor when it packed the skin (a gear skin's `picture`, a
// character's `character.picture`). Read from the pack and decoded off the main thread (ForgeCore Images), only uploaded here; the
// skin itself is not loaded for it. The picture is what the author packed: colours the player picks show on the gear and the
// character in the lobby, not here. A pack without a picture (packed by an older editor) has none; the picker shows a placeholder.
// Pictures are kept per pack (a rebuilt pack is a new Skin) for as long as it is installed, at most Keep of them.
static class Previews
{
    const int Keep = 24;

    sealed class Picture
    {
        public Sprite? Sprite;
        public bool Done; // read and decoded, or failed (no sprite)
        public float Used;
        public System.Threading.Tasks.Task<byte[]>? Reading;
        public ForgeCore.ImageJob? Decoding;
    }

    static readonly Dictionary<Skin, Picture> Made = new();
    static readonly List<(Skin Skin, Picture Picture)> Reading = new(); // being read, not yet handed to the decoder

    static string? PathOf(Skin skin) => skin.Doc.Picture ?? skin.Doc.Character?.Picture;

    // The pack's own picture of `skin`, or null: with `coming` while it is being read (Picker.Changed follows when it is), else the
    // pack has none, or it could not be read (logged once).
    public static Sprite? Of(Skin skin, out bool coming)
    {
        coming = false;
        if (PathOf(skin) is not { } path) return null;
        if (!Made.TryGetValue(skin, out var p))
        {
            var file = skin.File;
            p = new Picture { Reading = System.Threading.Tasks.Task.Run(() => { using var pack = SkinPack.Open(file); return pack.ReadBytes(path); }) };
            Made[skin] = p;
            Reading.Add((skin, p));
        }
        p.Used = Time.unscaledTime;
        coming = !p.Done;
        return p.Sprite;
    }

    // Main thread, every frame: hands every picture read meanwhile to the decoder.
    public static void Tick()
    {
        for (var i = Reading.Count - 1; i >= 0; i--)
        {
            var (skin, p) = Reading[i];
            if (p.Reading is not { IsCompleted: true } reading) continue;
            Reading.RemoveAt(i);
            if (reading.IsFaulted)
            {
                ForgeSkinPlugin.Warn($"picture of {skin.Doc.Id}: not read ({reading.Exception!.InnerException!.Message})");
                p.Done = true;
                Picker.Changed();
                continue;
            }
            p.Decoding = ForgeCore.Images.Queue(reading.Result, (texture, failure) => Decoded(skin, p, texture, failure), maxSide: 2048);
        }
    }

    // The picker closed: the pictures of packs no longer installed go.
    public static void Drop()
    {
        foreach (var (skin, p) in Made.ToList())
            if (skin.Retired) { Made.Remove(skin); Free(p); }
    }

    static void Decoded(Skin skin, Picture p, Texture2D? texture, string failure)
    {
        p.Done = true;
        if (!Made.TryGetValue(skin, out var current) || current != p) { if (texture != null) Object.Destroy(texture); return; }
        if (texture == null) ForgeSkinPlugin.Warn($"picture of {skin.Doc.Id}: not decoded ({failure})");
        else p.Sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        while (Made.Count > Keep)
        {
            var oldest = Made.MinBy(x => x.Value.Used);
            Made.Remove(oldest.Key);
            Free(oldest.Value);
        }
        Picker.Changed();
    }

    static void Free(Picture p)
    {
        p.Decoding?.Cancel();
        if (p.Sprite == null) return;
        Object.Destroy(p.Sprite.texture);
        Object.Destroy(p.Sprite);
    }

    // The game's own loadout icon of the gear behind `key` (it wears what the gear wears now: Icons draws it again when that changes),
    // or null when the game has not drawn it yet.
    public static Sprite? GameIcon(string key) =>
        SkinnedGear.RangeOf(key) is { } range && GearIconRendering.TryGetGearIconSprite(range.GetChecksum(), out var icon) ? icon : null;
}

sealed partial class SkinnedGear
{
    // The game's gear behind a gear target key, from its own gear lists; null for an item key or gear not in them.
    public static GearIDRange? RangeOf(string key)
    {
        if (!key.StartsWith("gear:", StringComparison.Ordinal) || !uint.TryParse(key.AsSpan(5), out var gear)) return null;
        foreach (var slot in GearSlots)
            if (GearManager.GetAllGearForSlot(slot) is { } all)
                foreach (var range in all)
                    if (range != null && Offline(range.PlayfabItemInstanceId) == gear) return range;
        return null;
    }
}
