using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>Registration of the game's own screen widgets (its world markers and its interaction prompt) and of picture files, so
/// that a mod never reaches into <c>GuiManager</c> or builds sprites itself.</summary>
public static partial class UIKit
{
    private const float IconUnits = 2.56f;
    private static readonly Dictionary<string, Sprite?> Icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts one of the game's own world markers (the text and distance the game draws for objectives) on
    /// <paramref name="anchor"/>. The marker follows the anchor object; change its text with <see cref="NavMarkerHandle.Title"/>
    /// and take it off with <see cref="NavMarkerHandle.Remove"/>. Returns null (and logs why) when the game's marker layer is not
    /// available or refuses the marker.</summary>
    /// <example><c>var marker = UIKit.AddNavMarker(anchorObject, "BEACON 5.0s");</c></example>
    public static NavMarkerHandle? AddNavMarker(GameObject anchor, string title, bool showDistance = true)
    {
        if (anchor == null) throw new ArgumentNullException(nameof(anchor));
        try
        {
            if (!ForgeCore.NavMarkers.Available) throw new InvalidOperationException("the nav marker layer is not available");
            var options = showDistance ? NavMarkerOption.Title | NavMarkerOption.Distance : NavMarkerOption.Title;
            var marker = ForgeCore.NavMarkers.Place(options, anchor, title ?? "")
                ?? throw new InvalidOperationException("the nav marker layer refused the marker");
            return new NavMarkerHandle(marker, title ?? "");
        }
        catch (Exception error)
        {
            Plugin.Error("ui-world-marker-could-not-be-placed", "The world marker '" + title + "' could not be placed: " + error);
            return null;
        }
    }

    /// <summary>Whether the game's world-marker layer exists now.</summary>
    public static bool NavMarkersAvailable => ForgeCore.NavMarkers.Available;

    /// <summary>Puts the game's enemy world marker (its title-and-distance tag, with no text of its own) on <paramref name="anchor"/>,
    /// the model object of an enemy, in <paramref name="colour"/>. The marker follows the anchor and is local to this machine; take it
    /// off with <see cref="NavMarkerHandle.Remove"/>. Returns null (and logs why) when the marker layer is not available or refuses it.</summary>
    public static NavMarkerHandle? AddEnemyMarker(GameObject anchor, Color colour)
    {
        if (anchor == null) throw new ArgumentNullException(nameof(anchor));
        try
        {
            if (!ForgeCore.NavMarkers.Available) throw new InvalidOperationException("the nav marker layer is not available");
            var marker = ForgeCore.NavMarkers.Place(NavMarkerOption.EnemyTitleDistance, anchor, "")
                ?? throw new InvalidOperationException("the nav marker layer refused the marker");
            marker.SetColor(colour);
            return new NavMarkerHandle(marker, "");
        }
        catch (Exception error)
        {
            Plugin.Error("ui-enemy-marker-could-not-be-placed", "The enemy marker could not be placed: " + error);
            return null;
        }
    }

    /// <summary>Posts one line into the local player's own chat box, shown only on this machine (nothing is sent to other players),
    /// for a notice that is not tied to a screen: for example that a teammate's files differ. Returns false (and logs why) when the
    /// text is empty, there is no local player yet or the game refuses the line.</summary>
    /// <example><c>UIKit.PostChatNotice("Teammate's call-in files differ; call-ins are off.");</c></example>
    public static bool PostChatNotice(string text)
    {
        if (string.IsNullOrEmpty(text)) { Plugin.Error("ui-chat-notice-needs-text", "A chat notice needs a text."); return false; }
        try
        {
            var player = SNetwork.SNet.LocalPlayer;
            if (player == null) throw new InvalidOperationException("there is no local player");
            PlayerChatManager.PostChatMessageLocaly(player, text);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Error("ui-chat-notice-could-not-be-posted", "The chat notice '" + text + "' could not be posted: " + error);
            return false;
        }
    }

    /// <summary>A claim on the game's weapon list: while <see cref="WeaponListLine.Hidden"/> is set, the gear-name line of any slot whose
    /// text is exactly <paramref name="name"/> stays switched off (for example the marker name of a lent weapon); cleared, or once the
    /// slot shows something else, the line comes back. Other slots and their texts are never touched.</summary>
    /// <example><c>var line = UIKit.ClaimWeaponListLine("My Gun Marker"); line.Hidden = lent;</c></example>
    public static WeaponListLine ClaimWeaponListLine(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("A weapon list line needs the text it hides.", nameof(name));
        return new WeaponListLine(name, null);
    }

    /// <summary>A picture file (PNG, up to 8192 pixels a side) as a sprite sized for the picker's cards and details: the long side is about
    /// 2.56 units like the game's own gear icons. Loaded once per file and kept for the session; null (and one logged error)
    /// when the file is missing or is not an image the game can decode.</summary>
    public static Sprite? LoadIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("An icon needs a file path.", nameof(path));
        if (Icons.TryGetValue(path, out var known)) return known;
        try
        {
            var texture = ForgeCore.Images.Decode(File.ReadAllBytes(path), out var failure) ?? throw new InvalidDataException(failure);
            texture.name = "ForgeUI.Icon." + Path.GetFileName(path);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), Math.Max(texture.width, texture.height) / IconUnits);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Icons[path] = sprite;
            return sprite;
        }
        catch (Exception error)
        {
            Icons[path] = null;
            Plugin.Error("ui-icon-could-not-be-loaded", "Icon " + path + " could not be loaded; no picture is shown: " + error);
            return null;
        }
    }
}

public sealed partial class UIKitMod
{
    private static readonly Registry<InteractPrompt> Prompts = new();

    /// <summary>Like <see cref="UIKit.ClaimWeaponListLine"/>, with <see cref="WeaponListLine.Text"/> keys looked up in this mod's language files.</summary>
    /// <example><c>var line = ui.ClaimWeaponListLine("My Gun Marker"); line.Text = UIText.Key("gun.twin");</c></example>
    public WeaponListLine ClaimWeaponListLine(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("A weapon list line needs the text it claims.", nameof(name));
        return new WeaponListLine(name, Texts);
    }

    /// <summary>A claim on the game's interaction prompt (the line near the middle of the screen with a key in brackets). Call
    /// <see cref="InteractPrompt.Show"/> every frame the prompt should be up (the game's own prompts replace it between frames) and
    /// <see cref="InteractPrompt.Hide"/> when it should go. Null (logged) when the name cannot be used or is taken.</summary>
    /// <example><c>var prompt = ui.AddInteractPrompt("Rack"); prompt.Show("PICK UP RACK", "[E]");</c></example>
    public InteractPrompt? AddInteractPrompt(string name)
    {
        var id = Full(name, "interaction prompt");
        if (id == null) return null;
        var prompt = new InteractPrompt(id);
        var problem = Prompts.Add(id, prompt);
        if (problem == null) return prompt;
        Plugin.Error("ui-interaction-prompt-not-added", "Interaction prompt not added: " + problem + ".");
        return null;
    }

    /// <summary>One of the game's world markers that counts down to <paramref name="arriveAt"/> (<c>ForgeCore.Frame.GameTime</c>) at
    /// <paramref name="position"/>: <paramref name="name"/> small and pale, then the seconds left in large digits (one decimal) in
    /// <paramref name="accent"/> and, over the last three seconds, red and larger. Call <see cref="NavMarkerHandle.Update"/> every
    /// frame: it keeps the marker beside the position and changes the text only when a tenth of a second passes. Null (logged) when the
    /// game's marker layer refuses the marker.</summary>
    /// <example><c>var marker = ui.AddCountdownMarker(landing, UIText.Key("beacon.name"), ForgeCore.Frame.GameTime + 5f, Color.cyan);</c></example>
    public NavMarkerHandle? AddCountdownMarker(Vector3 position, UIText name, float arriveAt, Color accent)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        var anchor = new GameObject("ForgeUI.CountdownMarker");
        anchor.transform.position = position;
        var countdown = new MarkerCountdown(position, name, Texts, arriveAt, accent);
        var marker = UIKit.AddNavMarker(anchor, countdown.Label());
        if (marker == null)
        {
            Object.Destroy(anchor);
            return null;
        }
        marker.Attach(anchor, countdown);
        return marker;
    }
}

/// <summary>One of the game's world markers placed by <see cref="UIKit.AddNavMarker"/>.</summary>
public sealed class NavMarkerHandle
{
    private NavMarker? _marker;
    private string _title;
    private GameObject? _anchor;
    private MarkerCountdown? _countdown;

    /// <summary>For a countdown marker: stand beside the marked position, to the camera's right (the default), so the text clears a
    /// beam or a column drawn there; false puts the marker straight above it.</summary>
    public bool Beside { get; set; } = true;

    /// <summary>For a countdown marker: how far beside the position the marker stands, in metres per metre of distance (0.24 by
    /// default: about half the label's width plus a gap on screen, near and far).</summary>
    public float BesideGap { get; set; } = 0.24f;

    /// <summary>For a countdown marker: the seconds left, never below 0.</summary>
    public float Remaining => _countdown?.Remaining ?? 0f;

    internal NavMarkerHandle(NavMarker marker, string title)
    {
        _marker = marker;
        _title = title;
    }

    /// <summary>The game's own marker object, for a caller that reads it back; null once removed.</summary>
    public NavMarker? Native => _marker;

    /// <summary>True while the marker is on the game's marker layer (not removed and not destroyed by the game).</summary>
    public bool IsPlaced => _marker != null && !_marker.WasCollected;

    /// <summary>The marker's text; setting the text it already shows does nothing.</summary>
    public string Title
    {
        get => _title;
        set
        {
            value ??= "";
            if (value == _title) return;
            _title = value;
            if (!IsPlaced) return;
            try { _marker!.SetTitle(value); }
            catch (Exception error) { Plugin.Error("ui-changing-text-world-marker-failed", "Changing the text of the world marker failed: " + error); }
        }
    }

    internal void Attach(GameObject anchor, MarkerCountdown countdown)
    {
        _anchor = anchor;
        _countdown = countdown;
    }

    /// <summary>For a countdown marker, every frame: puts the marker where <see cref="Beside"/> says and sets the text again when the
    /// tenth of a second, the urgency or the language changed. Does nothing for a marker that is not a countdown or is gone.</summary>
    public void Update()
    {
        var anchor = _anchor;
        var countdown = _countdown;
        if (anchor == null || countdown == null || anchor.WasCollected || !IsPlaced) return;
        anchor.transform.position = countdown.Anchor(Beside, BesideGap);
        if (countdown.Changed) Title = countdown.Label();
    }

    /// <summary>Takes the marker off the game's marker layer. Safe to call twice.</summary>
    public void Remove()
    {
        var anchor = _anchor;
        _anchor = null;
        _countdown = null;
        if (anchor != null && !anchor.WasCollected) Object.Destroy(anchor);
        var marker = _marker;
        _marker = null;
        if (marker == null || marker.WasCollected) return;
        try { ForgeCore.NavMarkers.Remove(marker); }
        catch (Exception error) { Plugin.Error("ui-removing-world-marker-failed", "Removing a world marker failed: " + error); }
    }
}

/// <summary>A gear-name line of the game's weapon list, claimed by <see cref="UIKit.ClaimWeaponListLine"/> or
/// <see cref="UIKitMod.ClaimWeaponListLine"/>: hidden, or shown with another text, while you hold it.</summary>
public sealed class WeaponListLine
{
    private const float Interval = 0.1f;
    private static readonly List<WeaponListLine> Claims = new();
    private readonly string _name;
    private readonly UITexts? _texts;
    private readonly List<TMPro.TextMeshPro> _hidden = new(), _renamed = new();
    private float _next;
    private bool _hide;
    private UIText? _text;
    private string _shown = "";

    internal WeaponListLine(string name, UITexts? texts)
    {
        _name = name;
        _texts = texts;
        Claims.Add(this);
    }

    /// <summary>The text a claim put into <paramref name="line"/> in place of the game's own, or null when no claim renamed it.</summary>
    internal static string? RenamedTo(TMPro.TextMeshPro line)
    {
        foreach (var claim in Claims)
            if (claim._renamed.Contains(line)) return claim._shown;
        return null;
    }

    /// <summary>Set it in every frame it should hold (the game switches the line back on when it redraws the list); the list is looked at
    /// ten times a second. Clearing it switches every line it hid back on at once. Hiding wins over <see cref="Text"/>.</summary>
    public bool Hidden
    {
        get => _hide;
        set
        {
            if (!value) { if (_hide || _hidden.Count > 0) Restore(); _hide = false; return; }
            _hide = true;
            Scan();
        }
    }

    /// <summary>Shows this text in the line instead of the claimed one (for example a display name for a marker name, or a translation:
    /// a key from the claiming mod's language files follows the language). Set it in every frame it should hold; null puts the game's text
    /// back at once.</summary>
    public UIText? Text
    {
        get => _text;
        set
        {
            if (value == null)
            {
                if (_text != null || _renamed.Count > 0) PutNamesBack();
                _text = null;
                return;
            }
            _text = value;
            Scan();
        }
    }

    private void Scan()
    {
        var now = ForgeCore.Frame.UnscaledTime;
        if (now < _next) return;
        _next = now + Interval;
        var slots = GuiManager.PlayerLayer?.Inventory?.m_inventorySlots;
        if (slots == null) return;
        var shown = _text?.In(_texts) ?? "";
        foreach (var pair in slots)
        {
            var text = pair.Value?.m_selected_detailedName;
            if (text == null || text.WasCollected) continue;
            var current = text.text;
            var ours = _renamed.Contains(text);
            var match = current == _name || (ours && current == _shown);
            if (_hide)
            {
                if (match && text.gameObject.activeSelf)
                {
                    text.gameObject.SetActive(false);
                    if (!_hidden.Contains(text)) _hidden.Add(text);
                }
                else if (!match && _hidden.Remove(text) && !text.gameObject.activeSelf) text.gameObject.SetActive(true);
            }
            if (_text == null) continue;
            if (match)
            {
                if (current != shown) { text.SetText(shown); UIKit.Inventory.MarkNamesDirty(); }
                if (!ours) _renamed.Add(text);
            }
            else if (ours) _renamed.Remove(text);
        }
        _shown = shown;
    }

    private void Restore()
    {
        foreach (var text in _hidden)
            if (text != null && !text.WasCollected && !text.gameObject.activeSelf) text.gameObject.SetActive(true);
        _hidden.Clear();
    }

    private void PutNamesBack()
    {
        foreach (var text in _renamed)
            if (text != null && !text.WasCollected) text.SetText(_name);
        _renamed.Clear();
        _shown = "";
        UIKit.Inventory.MarkNamesDirty();
    }
}
/// <summary>The game's interaction prompt, claimed by one mod feature. See <see cref="UIKitMod.AddInteractPrompt"/>.</summary>
public sealed class InteractPrompt
{
    internal InteractPrompt(string id) => Id = id;

    public string Id { get; }

    /// <summary>True between a <see cref="Show"/> and the next <see cref="Hide"/>.</summary>
    public bool IsShown { get; private set; }

    /// <summary>Shows <paramref name="text"/> with <paramref name="key"/> (for example <c>[E]</c>, or empty for none). Call it
    /// every frame the prompt should stay up: the game's own prompts replace it between frames and this takes it back.</summary>
    public void Show(string text, string key = "")
    {
        var layer = GuiManager.InteractionLayer;
        if (layer == null) return;
        layer.SetInteractPrompt(text ?? "", key ?? "", ePUIMessageStyle.Default);
        layer.InteractPromptVisible = true;
        IsShown = true;
    }

    /// <summary>The game's hold-to-use ring under the prompt, filled to <paramref name="fraction"/> (0..1); a negative fraction takes the
    /// ring away. Call it after <see cref="Show"/> in every frame the key is held.</summary>
    public void Progress(float fraction)
    {
        var layer = GuiManager.InteractionLayer;
        if (layer == null) return;
        if (fraction < 0f) { if (_timer) { _timer = false; layer.TimerVisible = false; } return; }
        _timer = true;
        layer.TimerVisible = true;
        layer.SetTimer(Mathf.Clamp01(fraction));
    }

    private bool _timer;

    /// <summary>Takes the prompt (and its hold ring) down if this claim put it up; leaves the game's own prompts alone otherwise.</summary>
    public void Hide()
    {
        if (!IsShown) return;
        IsShown = false;
        var layer = GuiManager.InteractionLayer;
        if (layer == null) return;
        layer.InteractPromptVisible = false;
        if (_timer) { _timer = false; layer.TimerVisible = false; }
    }
}

/// <summary>The state of one countdown marker: what it counts to and what it last showed.</summary>
internal sealed class MarkerCountdown
{
    private readonly Vector3 _position;
    private readonly UIText _name;
    private readonly UITexts _texts;
    private readonly float _arriveAt;
    private readonly string _accentHex;
    private string _namePart = "";
    private int _version = -1;
    private decimal _shownTenth = -1;
    private bool _shownUrgent;

    internal MarkerCountdown(Vector3 position, UIText name, UITexts texts, float arriveAt, Color accent)
    {
        _position = position;
        _name = name;
        _texts = texts;
        _arriveAt = arriveAt;
        _accentHex = ColorUtility.ToHtmlStringRGB(Color.Lerp(accent, Color.white, 0.3f));
    }

    internal float Remaining => Math.Max(0f, _arriveAt - ForgeCore.Frame.GameTime);

    /// <summary>True when the label text differs from what <see cref="Label"/> last returned.</summary>
    internal bool Changed => Countdown.Tenth(Remaining) != _shownTenth || Countdown.Urgent(Remaining) != _shownUrgent || _version != UITexts.Version;

    internal string Label()
    {
        var remaining = Remaining;
        if (_version != UITexts.Version)
        {
            _version = UITexts.Version;
            _namePart = Countdown.NamePart(_name.In(_texts));
        }
        _shownTenth = Countdown.Tenth(remaining);
        _shownUrgent = Countdown.Urgent(remaining);
        return Countdown.Label(_namePart, remaining, _accentHex, UIKit.Kit.Text("countdown.seconds", Countdown.Digits(remaining)));
    }

    internal Vector3 Anchor(bool beside, float gap)
    {
        var camera = HudScreen.WorldCamera();
        if (!beside || camera == null) return _position + Vector3.up * 1.7f;
        var eye = camera.transform;
        var at = eye.position;
        var forward = eye.forward;
        var (x, y, z) = Countdown.Beside(at.x, at.y, at.z, forward.x, forward.z, _position.x, _position.y, _position.z, gap);
        return new Vector3(x, y, z);
    }
}
