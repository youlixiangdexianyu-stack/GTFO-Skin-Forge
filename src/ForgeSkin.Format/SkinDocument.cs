using System.Text.Json.Serialization;

namespace ForgeSkin.Format;

// One skin document. The same type is the editor project (.forgeskinproj, file references are paths on disk,
// relative to the project file) and the pack manifest (manifest.json inside .forgeskin, references are
// paths inside the zip). All positions are GTFO weapon space: Unity left-handed, Y up, metres, relative
// to the anchor transform. Rotations are Unity Euler degrees (Quaternion.Euler order).
public sealed class SkinDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";

    // Which gear (gun, melee weapon or tool) this skin is for. Null weapon = applies to every gear that uses the listed materials
    // (attachment skins: mags, sights, lights, ammo).
    public SkinTarget Target { get; set; } = new();

    public List<MaterialOverride> Materials { get; set; } = new();
    public List<Attachment> Attachments { get; set; } = new();
    public List<PartTransform> Parts { get; set; } = new();
    public List<SkinSound> Sounds { get; set; } = new();

    // Editor-only state (camera, selection, light settings). Never read by the plugin; dropped when packing.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Editor { get; set; }
}

public sealed class SkinTarget
{
    // Catalog gear id (e.g. "auto_pistol", "sledgehammer", "bio_tracker"); null for attachment skins.
    public string? Weapon { get; set; }
    // What the plugin matches on, filled from the catalog when packing; exactly one is set for a gear skin. GearId: the
    // PlayerOfflineGearDataBlock persistentID of a gear built from parts (guns, melee weapons, tools). ItemId: the
    // ItemDataBlock persistentID of an item with its own prefabs (resource packs, consumables, carry items, hacking tool).
    public uint? GearId { get; set; }
    public uint? ItemId { get; set; }

    // The key in active.json: "gear:<GearId>" or "item:<ItemId>"; null for attachment skins.
    public string? Key() => GearId is { } gear ? GearKey(gear) : ItemId is { } item ? ItemKey(item) : null;
    public static string GearKey(uint id) => "gear:" + id;
    public static string ItemKey(uint id) => "item:" + id;
}

// Layout of a vanilla part root or a child under it, as absolute local values (Unity space, metres, Euler degrees).
// Every value is optional: null keeps what the game built. Written once when the gear finishes assembling, so applying
// it again changes nothing. Visible = false disables the renderers at and under the transform.
public sealed class PartTransform
{
    public string Part { get; set; } = "";       // same fixed ids as Attachment.Part
    public string? Path { get; set; }            // child path under the part root as Transform.Find takes it; null = the part root
    public float[]? Position { get; set; }       // localPosition
    public float[]? Rotation { get; set; }       // localEulerAngles
    public float[]? Scale { get; set; }          // localScale
    public bool? Visible { get; set; }
    // Replaces this node's own model (its MeshFilter mesh) in its local space; the node keeps its game materials, so the
    // skin's textures still apply by the new model's UVs. Imported glb in a project, `meshes/*.forgeskinmesh` in a pack.
    public string? Mesh { get; set; }
}

public enum TextureChannel { Albedo, Normal, MetallicSmoothness, Emissive }

public sealed class MaterialOverride
{
    // Game material name, e.g. "Front_UZI_2". The plugin matches renderers whose shared material has this name.
    public string Material { get; set; } = "";
    public string? Part { get; set; }

    // Channel -> texture reference. Missing channel = keep the game's texture.
    public Dictionary<TextureChannel, string> Textures { get; set; } = new();

    public MaterialParams Params { get; set; } = new();

    // Animated layers composited into one channel of this material.
    public List<DynamicLayer> Layers { get; set; } = new();

    // Livery (editor only): a paint finish and paint layers, bottom first. The editor bakes them into the Albedo (and, for
    // a finish, MetallicSmoothness and Emissive) textures when packing, so a pack never carries them.
    public Finish? Finish { get; set; }
    public List<LiveryLayer>? Livery { get; set; }
}

public enum Finish { Matte, Gloss, Satin, Metal, BrushedMetal, Chrome, Rubber, Plastic, CarbonFibre, PaintedSteel, WornMetal, Glow }
public enum LiveryKind { Fill, Decal, Brush }
public enum BlendMode { Normal, Multiply, Screen, Overlay, Add }

// One paint layer of a material: a fill over the whole surface, an image decal projected onto the surface, or brush strokes.
public sealed class LiveryLayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public LiveryKind Kind { get; set; }
    public bool Visible { get; set; } = true;
    public float[] Color { get; set; } = { 1, 1, 1, 1 }; // fill and brush colour; a decal's tint (multiplies its image)
    public float Opacity { get; set; } = 1;
    public BlendMode Blend { get; set; }

    // Decal: the image projected along the frame's -Z (frame +Z is the surface normal where it was placed). The frame is in
    // the part root's local space (Unity, metres, Euler degrees), then turned by Angle about its Z; Size is width and height,
    // Depth how far in front of and behind the frame plane it reaches, so it does not paint the far side.
    public string? Image { get; set; }
    public float[]? Position { get; set; }
    public float[]? Rotation { get; set; }
    public float Angle { get; set; }
    public float[] Size { get; set; } = { 0.04f, 0.04f };
    public float Depth { get; set; } = 0.01f;
    public bool Mirror { get; set; }
    // An animated GIF decal plays in game: the editor bakes its frames into a dynamic layer of this material, with these
    // settings (as `DynamicLayer`). Fps null = the GIF's own frame timing.
    public float? Fps { get; set; }
    public Trigger Trigger { get; set; } = Trigger.Always;
    public PlayMode Play { get; set; } = PlayMode.Loop;
    public float PlaySeconds { get; set; }
    public float LowAmmoFraction { get; set; } = 0.2f;

    // Brush strokes and the mask (strokes that hide this layer): each a polyline in texture UV space as u, v, width
    // triples (UV 0..1, top-left origin; width in UV units).
    public List<float[]> Strokes { get; set; } = new();
    public List<float[]> Mask { get; set; } = new();
}

public sealed class MaterialParams
{
    public bool? EmissiveEnabled { get; set; }
    public float[]? EmissiveColor { get; set; } // linear RGB
    public float? EmissiveIntensity { get; set; }
    public float? Metallic { get; set; }
    public float? Smoothness { get; set; }
    public float[]? Tint { get; set; } // RGBA multiplier on albedo
}

public enum LayerKind { Image, Sequence, Video, Text }

public sealed class DynamicLayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public LayerKind Kind { get; set; }
    public TextureChannel Channel { get; set; } = TextureChannel.Albedo;
    public bool Visible { get; set; } = true;

    // Editor sources: image path, ordered frame paths, video path, or text.
    public List<string> Sources { get; set; } = new();
    public TextStyle? Text { get; set; }
    public float Fps { get; set; } = 12;
    // Always plays from the start; any other trigger shows the base texture until its event, then plays (a repeat event restarts it).
    public Trigger Trigger { get; set; } = Trigger.Always;
    public PlayMode Play { get; set; } = PlayMode.Loop;
    public float PlaySeconds { get; set; }          // LoopFor: how long it loops after the event
    public float LowAmmoFraction { get; set; } = 0.2f; // LowAmmo: fires when clip / clip size falls below this
    public float Opacity { get; set; } = 1;
    public float[] Crop { get; set; } = { 0, 0, 1, 1 }; // normalized x, y, w, h of the source
    public float[] UvCenter { get; set; } = { 0.5f, 0.5f };
    public float[] UvSize { get; set; } = { 0.25f, 0.25f };
    public float Rotation { get; set; }

    // Filled when packing: frame atlas inside the zip, and the pixel rect of the target texture it updates.
    // Each frame replaces the target pixels (no blending in game): crop, rotation, opacity and the base texture are baked in.
    public LayerAtlas? Atlas { get; set; }
}

// A sound only the local player hears when the trigger fires (PLAN §3.4). Plain audio played by the plugin through Unity,
// not the game's Wwise, and never sent over the network. The editor converts any audio file to Ogg Vorbis before saving.
public sealed class SkinSound
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Trigger Trigger { get; set; } = Trigger.Kill;
    public string File { get; set; } = "";             // Ogg Vorbis, mono or stereo, at most 10 seconds
    public float Volume { get; set; } = 1;              // 0-1
    public float LowAmmoFraction { get; set; } = 0.2f;  // LowAmmo: plays when clip / clip size falls below this
}

// Events of the local player's own gear (PLAN §3.4).
public enum Trigger
{
    Always, Kill, Hit, Fire, DryFire, ReloadStart, ReloadEnd, Equip, Unequip, Aim, AimEnd, LowAmmo, Empty, AmmoPickup,
    FlashlightOn, FlashlightOff,
    Miss, // a gun's shot that hit no enemy
}
public enum PlayMode { Loop, Once, LoopFor }

public sealed class TextStyle
{
    public string Content { get; set; } = "";
    public string? Font { get; set; }
    public float SizePx { get; set; } = 64;
    public float[] Color { get; set; } = { 1, 1, 1, 1 };
}

// Frames are laid out row by row from the top-left of the atlas image, `Columns` per row.
public sealed class LayerAtlas
{
    public string Path { get; set; } = "";
    public int FrameCount { get; set; }
    public int Columns { get; set; }
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public int[] TargetRect { get; set; } = { 0, 0, 0, 0 }; // x, y, w, h in target texture pixels, top-left image origin; w/h == FrameWidth/FrameHeight
}

public sealed class Attachment
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;

    // Anchor: catalog part id, plus the transform name under that part the attachment is parented to.
    // Part ids are fixed (GearPartHolder slots): front, front_attachment_a, front_attachment_b, receiver, receiver_attachment,
    // stock, sight, mag, flashlight, tool_main, tool_main_attachment, tool_grip, tool_delivery, tool_delivery_attachment,
    // tool_payload, tool_targeting, tool_screen, melee_head, melee_neck, melee_handle, melee_pommel.
    public string Part { get; set; } = "";
    public string? Transform { get; set; }

    public float[] Position { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float[] Scale { get; set; } = { 1, 1, 1 };
    public bool FaceCamera { get; set; }

    // A model (imported glb -> .forgeskinmesh when packing) and/or a screen.
    public string? Mesh { get; set; }
    public AttachmentMaterial? Material { get; set; }
    public Screen? Screen { get; set; }
    public List<AttachmentEffect> Effects { get; set; } = new();
}

public enum EffectAction { Flash, Show }

// On Trigger: Flash lights the attachment's emission with Color x Intensity for Seconds; Show makes a hidden attachment
// visible for Seconds. A repeat event restarts it.
public sealed class AttachmentEffect
{
    public Trigger Trigger { get; set; } = Trigger.Kill;
    public EffectAction Action { get; set; } = EffectAction.Flash;
    public float[] Color { get; set; } = { 1, 1, 1 }; // linear RGB
    public float Intensity { get; set; } = 1;
    public float Seconds { get; set; } = 0.3f;
}

public sealed class AttachmentMaterial
{
    public Dictionary<TextureChannel, string> Textures { get; set; } = new();
    public float[] Color { get; set; } = { 1, 1, 1, 1 };
    public bool Unlit { get; set; }
    public MaterialParams Params { get; set; } = new();
}

// Counted for the local player over the current level: Kills / DamageDealt with this weapon, DamageDealtAll with all
// their weapons, DamageTaken by them; Accuracy is the percent of this gun's shots that hit an enemy. Health and
// Infection are the local player's, in percent.
public enum ScreenSource { AmmoClip, AmmoReserve, AmmoTotal, Static, Kills, DamageDealt, DamageDealtAll, DamageTaken, Accuracy, Health, Infection }

// Digits: the value as text; Bar: a bar filling from the left, only for sources with a maximum (ScreenSources.Bounded).
public enum ScreenStyle { Digits, Bar }

public static class ScreenSources
{
    // Ammo counts: tinted by the low-ammo colour.
    public static bool IsAmmo(this ScreenSource s) => s is ScreenSource.AmmoClip or ScreenSource.AmmoReserve or ScreenSource.AmmoTotal;
    // Shown as 0-100 with a trailing '%'.
    public static bool IsPercent(this ScreenSource s) => s is ScreenSource.Accuracy or ScreenSource.Health or ScreenSource.Infection;
    // Has a maximum, so it can fill a bar.
    public static bool Bounded(this ScreenSource s) => s.IsAmmo() || s.IsPercent();
}

// A quad in the attachment's local XY plane, `Size` wide/high, visible face toward local -Z. The surface gap is already
// baked into Attachment.Position (the plugin does not add it); the border is drawn inside the quad edges.
public sealed class Screen
{
    // The glyph strip the app renders and the pack carries one mesh per glyph of. Packs made before '%' carry 12.
    public const string Glyphs = "0123456789 -%";

    public ScreenSource Source { get; set; } = ScreenSource.AmmoClip;
    public ScreenStyle Style { get; set; }
    // 0: automatic, as many digits as the largest value the source can show (magazine: clip size; reserve: its maximum;
    // total: both; kills: the value's own digits, up to 4; damage: up to 6; percentages: 3, then '%').
    // PadZero pads up to that width.
    public int Digits { get; set; }
    public bool PadZero { get; set; } = true;
    public string? StaticText { get; set; } // any characters, drawn with Font


    public float[] Size { get; set; } = { 0.03f, 0.015f }; // metres
    public float SurfaceGap { get; set; } = 0.0005f; // editor value; already included in Attachment.Position
    public float[] PanelColor { get; set; } = { 0, 0, 0, 0.85f };
    public float[] BorderColor { get; set; } = { 0.73f, 0.98f, 1, 1 };
    public float BorderWidth { get; set; } = 0.0008f;
    public float[] TextColor { get; set; } = { 0.73f, 0.98f, 1, 1 };
    public float TextScale { get; set; } = 0.8f;
    public float Glow { get; set; } = 1.5f;
    // On: digits and border are emissive (brightness from Glow) and bloom in game; off: plain lit colours.
    public bool GlowInGame { get; set; } = true;
    public string? Font { get; set; }

    // Optional low-ammo colour: replaces the text colour and its glow below the threshold. With LowAmmoGradient on the
    // tint blends in as the clip falls from LowAmmoGradientStart to LowAmmoFraction (both fractions of the full clip);
    // off switches at LowAmmoFraction.
    public float LowAmmoFraction { get; set; } = 0.2f;
    public float[]? LowAmmoColor { get; set; }
    public bool LowAmmoGradient { get; set; }
    public float LowAmmoGradientStart { get; set; } = 0.4f;

    // Filled when packing from the glyph strip the app renders with the chosen font (.frames/<id>-glyphs.png): one mesh
    // per glyph of Glyphs (null for the blank), in cell units (height 1, width GlyphAspect), facing -Z.
    public string?[]? GlyphMeshes { get; set; }
    public float GlyphAspect { get; set; }
    // Filled when packing a static screen from the text the app renders with the chosen font (.frames/<id>-text.png):
    // the whole text as one mesh, height 1, width TextAspect, facing -Z.
    public string? TextMesh { get; set; }
    public float TextAspect { get; set; }
}
