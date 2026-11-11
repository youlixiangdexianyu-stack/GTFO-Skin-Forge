using System.Text.Json.Serialization;

namespace SkinForge.Format;

// One skin document. The same type is the editor project (.sfproj, file references are paths on disk,
// relative to the project file) and the pack manifest (manifest.json inside .gtfskin, references are
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

    // Which weapon this skin is for. Null weapon = applies to every gear that uses the listed materials
    // (attachment skins: mags, sights, lights, ammo).
    public SkinTarget Target { get; set; } = new();

    public List<MaterialOverride> Materials { get; set; } = new();
    public List<Attachment> Attachments { get; set; } = new();
    public List<PartTransform> Parts { get; set; } = new();

    // Editor-only state (camera, selection, light settings). Never read by the plugin; dropped when packing.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Editor { get; set; }
}

public sealed class SkinTarget
{
    // Catalog weapon id (e.g. "auto_pistol"); null for attachment skins.
    public string? Weapon { get; set; }
    // ArchetypeDataBlock persistentID the plugin matches on; filled from the catalog when packing.
    public uint? ArchetypeId { get; set; }
}

// Layout delta on a vanilla part (or a named sub-transform under it): localPosition = vanilla + Position,
// localRotation = vanilla * Euler(Rotation), localScale = vanilla * Scale (component-wise). Hidden = renderers disabled.
public sealed class PartTransform
{
    public string Part { get; set; } = "";       // same fixed ids as Attachment.Part
    public string? Transform { get; set; }       // sub-transform name under the part; null = the part root
    public float[] Position { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float[] Scale { get; set; } = { 1, 1, 1 };
    public bool Visible { get; set; } = true;
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
    public bool Loop { get; set; } = true;
    public float Opacity { get; set; } = 1;
    public float[] Crop { get; set; } = { 0, 0, 1, 1 }; // normalized x, y, w, h of the source
    public float[] UvCenter { get; set; } = { 0.5f, 0.5f };
    public float[] UvSize { get; set; } = { 0.25f, 0.25f };
    public float Rotation { get; set; }

    // Filled when packing: frame atlas inside the zip, and the pixel rect of the target texture it updates.
    // Each frame replaces the target pixels (no blending in game): crop, rotation, opacity and the base texture are baked in.
    public LayerAtlas? Atlas { get; set; }
}

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

    // A model (imported glb -> .sfmesh when packing) and/or a screen.
    public string? Mesh { get; set; }
    public AttachmentMaterial? Material { get; set; }
    public Screen? Screen { get; set; }
}

public sealed class AttachmentMaterial
{
    public Dictionary<TextureChannel, string> Textures { get; set; } = new();
    public float[] Color { get; set; } = { 1, 1, 1, 1 };
    public bool Unlit { get; set; }
    public MaterialParams Params { get; set; } = new();
}

public enum ScreenSource { AmmoClip, AmmoReserve, AmmoTotal, Static }

// A quad in the attachment's local XY plane, `Size` wide/high, visible face toward local -Z. The surface gap is already
// baked into Attachment.Position (the plugin does not add it); the border is drawn inside the quad edges.
public sealed class Screen
{
    public ScreenSource Source { get; set; } = ScreenSource.AmmoClip;
    public int Digits { get; set; } = 2;
    public bool PadZero { get; set; } = true;
    public string? StaticText { get; set; }

    public float[] Size { get; set; } = { 0.03f, 0.015f }; // metres
    public float SurfaceGap { get; set; } = 0.0005f; // editor value; already included in Attachment.Position
    public float[] PanelColor { get; set; } = { 0, 0, 0, 0.85f };
    public float[] BorderColor { get; set; } = { 0.73f, 0.98f, 1, 1 };
    public float BorderWidth { get; set; } = 0.0008f;
    public float[] TextColor { get; set; } = { 0.73f, 0.98f, 1, 1 };
    public float TextScale { get; set; } = 0.8f;
    public float Glow { get; set; } = 1.5f;
    public string? Font { get; set; }

    // Optional low-ammo colour switch.
    public float LowAmmoFraction { get; set; } = 0.2f;
    public float[]? LowAmmoColor { get; set; }

    // Filled when packing: glyph strip "0123456789 -" rendered with the chosen font, white on transparent.
    public string? GlyphAtlas { get; set; }
}
