using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeVisual;

/// <summary>Why an imported model was refused: a stable <see cref="Code"/> for the caller to branch on and a <see cref="Detail"/> that
/// names the part (a material, a texture, a vertex) for the log.</summary>
public readonly record struct ImportedModelFailure(string Code, string Detail)
{
    public override string ToString() => Code + ": " + Detail;
}

/// <summary>The failure codes of the imported-model loader.</summary>
public static class ImportedModelCodes
{
    /// <summary>The descriptor is not valid JSON, is another format or breaks one of its own rules.</summary>
    public const string Descriptor = "imported-model-descriptor-invalid";
    /// <summary>The mesh bytes are not a format-1 mesh of the size the descriptor declares, or hold a non-finite value, a zero normal or an index past the vertices.</summary>
    public const string Mesh = "imported-model-mesh-invalid";
    /// <summary>The caller handed fewer or more texture byte arrays than the descriptor lists.</summary>
    public const string TextureCount = "imported-model-texture-count";
    /// <summary>A texture is not a PNG of the size the descriptor declares.</summary>
    public const string TextureDecode = "imported-model-texture-decode-failed";
    /// <summary>The model is bigger than the budget the cache was given; nothing was built.</summary>
    public const string OverBudget = "imported-model-over-budget";
    /// <summary>The game's gear shader is not loaded yet (a level has not been built): try again after one is.</summary>
    public const string ShaderUnavailable = "imported-model-shader-unavailable";
    /// <summary>A material asks for a cut-out the game's shader cannot do.</summary>
    public const string CutoutUnsupported = "imported-model-cutout-unsupported";
    /// <summary>Unity refused to build the mesh, a texture or a material.</summary>
    public const string Build = "imported-model-build-failed";
}

/// <summary>One run of a model's indices drawn with one material (one Unity submesh).</summary>
public sealed record ImportedSubmesh(int IndexStart, int IndexCount, int Material);

/// <summary>A model's surface: a base colour (or a base-colour texture), the metal and roughness numbers or a surface map, an optional tangent-space normal
/// map and an optional emission (a colour, times an emission map when it has one).</summary>
/// <param name="Name">Only for logs.</param>
/// <param name="BaseColor">Linear red, green, blue, alpha. With a <paramref name="Texture"/> it must be white: the texture carries the colour.</param>
/// <param name="Texture">Index into the descriptor's textures, or -1 for a flat <paramref name="BaseColor"/>.</param>
/// <param name="Metallic">0 to 1.</param>
/// <param name="Roughness">0 (mirror) to 1.</param>
/// <param name="Cutout">Alpha-tested: pixels of the texture's alpha below <paramref name="Cutoff"/> are not drawn.</param>
/// <param name="Cutoff">0 to 1; only read when <paramref name="Cutout"/>.</param>
/// <param name="NormalTexture">Index into the descriptor's textures of a tangent-space normal map (linear, OpenGL +Y; the glTF scale is already applied), or -1.</param>
/// <param name="MetalTexture">Index into the descriptor's textures of the gear shader's own surface map (linear: metal in red, smoothness in green; the glTF factors are already folded in), or -1 for the flat <paramref name="Metallic"/> and <paramref name="Roughness"/>. With a map those two are 1 and ignored.</param>
/// <param name="EmissiveColor">Linear red, green, blue the surface glows by, each 0 or more: glTF's emission factor times its strength, so 1 is the
/// colour at full brightness and above 1 brighter; all zero is no emission.</param>
/// <param name="Glass">A see-through pane drawn with the game's glass shader (`alpha` `glass`): one flat <paramref name="BaseColor"/> tint whose alpha is its
/// opacity, with a faint sheen, and nothing else (no texture, map or emission). The only see-through surface there is.</param>
/// <param name="EmissiveTexture">Index into the descriptor's textures of the emission map the colour is multiplied by (sRGB), or -1 for an even glow. Only with a non-zero <paramref name="EmissiveColor"/>.</param>
public sealed record ImportedMaterial(string Name, float[] BaseColor, int Texture, float Metallic, float Roughness, bool Cutout, float Cutoff, int NormalTexture, int MetalTexture, float[] EmissiveColor, int EmissiveTexture, bool Glass = false)
{
    /// <summary>Whether the surface glows.</summary>
    public bool Emissive => EmissiveColor[0] > 0f || EmissiveColor[1] > 0f || EmissiveColor[2] > 0f;
}

/// <summary>A PNG texture the model uses, by the content hash the caller keeps it under and the size it must decode to.</summary>
public sealed record ImportedTexture(string Sha256, int Width, int Height);

/// <summary>One bone of a skinned model (format 2): its name, its parent (an earlier bone, or -1 for a root) and its rest pose in the
/// model's own frame (Unity axes) as a column-major 4×4.</summary>
public sealed record ImportedBone(string Name, int Parent, float[] Rest);

/// <summary>
/// The description of an imported model, the part of the Forge model package that is JSON: which mesh bytes it draws (by
/// content hash, vertex and index counts), how the indices split into submeshes, the material of each submesh and the textures the
/// materials use. Format 1 is a still model; format 2 adds the skeleton its mesh is skinned to (<c>"skin": { "bones": [ { "name",
/// "parent", "rest" } ] }</c>, parents first), and its mesh carries four bone weights per vertex. The mesh itself and the PNGs travel
/// beside it as bytes the caller read and checked against these hashes; this library reads no file and computes no hash.
/// <code>
/// { "format": 1,
///   "mesh": { "sha256": "…", "vertices": 5124, "indices": 11718 },
///   "submeshes": [ { "start": 0, "count": 11718, "material": 0 } ],
///   "materials": [ { "name": "Body", "baseColor": [1,1,1,1], "texture": 0, "metallic": 0.5, "roughness": 0.5, "alpha": "opaque",
///                    "normalTexture": 1, "metalTexture": 3, "emissiveColor": [1,0.2,0], "emissiveTexture": 2 } ],
///   "textures": [ { "sha256": "…", "width": 1024, "height": 1024 } ] }
/// </code>
/// </summary>
public sealed class ImportedModelDescriptor
{
    /// <summary>The still format; <see cref="SkinnedFormat"/> adds a skeleton.</summary>
    public const int Format = 1, SkinnedFormat = 2;
    // Limits of the format, not of any policy: they keep every size below inside 32-bit arithmetic and a texture inside the GPU's reach.
    public const int MaxVertices = 1_000_000, MaxIndices = 3_000_000, MaxMaterials = 32, MaxTextures = 32, MaxTextureSide = 4096, MaxBones = 256;

    private ImportedModelDescriptor(string key, string meshSha256, int vertices, int indices, ImportedSubmesh[] submeshes, ImportedMaterial[] materials, ImportedTexture[] textures, ImportedBone[]? bones)
    {
        Key = key;
        Bones = bones;
        MeshSha256 = meshSha256;
        Vertices = vertices;
        Indices = indices;
        Submeshes = submeshes;
        foreach (var submesh in submeshes) DrawnIndices += submesh.IndexCount;
        Materials = materials;
        Textures = textures;
        foreach (var material in materials) HasNormalMap |= material.NormalTexture >= 0;
    }

    /// <summary>Every value the descriptor holds, as one canonical text: what a cache keys the built model by. Two models may share a mesh
    /// and differ in materials or textures, so the mesh hash alone is not the model.</summary>
    public string Key { get; }

    /// <summary>The mesh's content hash (lower-case hex SHA-256).</summary>
    public string MeshSha256 { get; }
    public int Vertices { get; }
    public int Indices { get; }

    /// <summary>The indices the built mesh holds: the sum of every submesh's count. Submeshes may overlap, so this can pass <see cref="Indices"/>;
    /// it is what the model costs and what <see cref="MaxIndices"/> bounds.</summary>
    public int DrawnIndices { get; }
    public IReadOnlyList<ImportedSubmesh> Submeshes { get; }
    public IReadOnlyList<ImportedMaterial> Materials { get; }
    public IReadOnlyList<ImportedTexture> Textures { get; }

    /// <summary>The skeleton of a skinned model (format 2), parents first; null for a still one.</summary>
    public IReadOnlyList<ImportedBone>? Bones { get; }

    /// <summary>Whether any material has a normal map: the built mesh then carries tangents.</summary>
    public bool HasNormalMap { get; }

    /// <summary>What the model holds once built, in bytes: 32 per vertex (48 with tangents), 4 per index, and each texture as RGBA with its mip chain.
    /// What a caller with a per-level budget checks before it hands the bytes over.</summary>
    public long EstimatedBytes
    {
        get
        {
            var total = Vertices * (HasNormalMap ? 48L : 32L) + DrawnIndices * 4L + (Bones == null ? 0L : Vertices * 32L);
            foreach (var texture in Textures) total += (long)texture.Width * texture.Height * 16L / 3L;
            return total;
        }
    }

    private sealed class Document
    {
        public int format { get; set; }
        public MeshEntry? mesh { get; set; }
        public SubmeshEntry[]? submeshes { get; set; }
        public MaterialEntry[]? materials { get; set; }
        public TextureEntry[]? textures { get; set; }
        public SkinEntry? skin { get; set; }
    }

    private sealed class SkinEntry { public BoneEntry[]? bones { get; set; } }
    private sealed class BoneEntry { public string? name { get; set; } public int parent { get; set; } public float[]? rest { get; set; } }

    private sealed class MeshEntry { public string? sha256 { get; set; } public int vertices { get; set; } public int indices { get; set; } }
    private sealed class SubmeshEntry { public int start { get; set; } public int count { get; set; } public int material { get; set; } }
    private sealed class TextureEntry { public string? sha256 { get; set; } public int width { get; set; } public int height { get; set; } }

    private sealed class MaterialEntry
    {
        public string? name { get; set; }
        public float[]? baseColor { get; set; }
        public int texture { get; set; } = -1;
        public float metallic { get; set; }
        public float roughness { get; set; } = 1f;
        public string? alpha { get; set; }
        public float cutoff { get; set; } = 0.5f;
        public int normalTexture { get; set; } = -1;
        public int metalTexture { get; set; } = -1;
        public float[]? emissiveColor { get; set; }
        public int emissiveTexture { get; set; } = -1;
    }

    /// <summary>The descriptor in <paramref name="json"/>, or null with the first rule it breaks.</summary>
    public static ImportedModelDescriptor? Parse(string json, out ImportedModelFailure failure)
    {
        failure = default;
        Document? d;
        try { d = JsonSerializer.Deserialize<Document>(json); }
        catch (JsonException error) { failure = Refuse("the descriptor is not valid JSON: " + error.Message); return null; }
        if (d == null) { failure = Refuse("the descriptor is empty"); return null; }
        if (d.format != Format && d.format != SkinnedFormat) { failure = Refuse("descriptor format " + d.format + " is not " + Format + " or " + SkinnedFormat); return null; }
        if ((d.skin != null) != (d.format == SkinnedFormat)) { failure = Refuse("a skeleton comes with format " + SkinnedFormat + " and only with it"); return null; }
        if (d.mesh == null || !IsHash(d.mesh.sha256)) { failure = Refuse("the mesh has no SHA-256"); return null; }
        if (d.mesh.vertices is < 3 or > MaxVertices) { failure = Refuse("the mesh has " + d.mesh.vertices + " vertices (3 to " + MaxVertices + ")"); return null; }
        if (d.mesh.indices < 3 || d.mesh.indices > MaxIndices || d.mesh.indices % 3 != 0) { failure = Refuse("the mesh has " + d.mesh.indices + " indices (a multiple of 3 up to " + MaxIndices + ")"); return null; }

        var textures = new ImportedTexture[d.textures?.Length ?? 0];
        if (textures.Length > MaxTextures) { failure = Refuse("more than " + MaxTextures + " textures"); return null; }
        for (var i = 0; i < textures.Length; i++)
        {
            var t = d.textures![i];
            if (t == null || !IsHash(t.sha256)) { failure = Refuse("texture " + i + " has no SHA-256"); return null; }
            if (t.width is < 1 or > MaxTextureSide || t.height is < 1 or > MaxTextureSide) { failure = Refuse("texture " + i + " is " + t.width + "x" + t.height + " (1 to " + MaxTextureSide + " a side)"); return null; }
            textures[i] = new ImportedTexture(t.sha256!, t.width, t.height);
        }

        var materials = new ImportedMaterial[d.materials?.Length ?? 0];
        if (materials.Length is < 1 or > MaxMaterials) { failure = Refuse("the model needs 1 to " + MaxMaterials + " materials, it lists " + materials.Length); return null; }
        for (var i = 0; i < materials.Length; i++)
        {
            var m = d.materials![i];
            if (m == null) { failure = Refuse("material " + i + " is empty"); return null; }
            var name = string.IsNullOrEmpty(m.name) ? "material " + i : m.name!;
            if (m.baseColor is not { Length: 4 } colour || !Unit(colour)) { failure = Refuse(name + ": baseColor must be four numbers from 0 to 1"); return null; }
            if (m.texture < -1 || m.texture >= textures.Length) { failure = Refuse(name + ": texture " + m.texture + " is not listed"); return null; }
            if (m.texture >= 0 && (colour[0] != 1f || colour[1] != 1f || colour[2] != 1f || colour[3] != 1f)) { failure = Refuse(name + ": a texture carries the colour, so baseColor must be white"); return null; }
            if (!Unit(m.metallic) || !Unit(m.roughness)) { failure = Refuse(name + ": metallic and roughness must be from 0 to 1"); return null; }
            bool cutout = m.alpha == "mask", glass = m.alpha == "glass";
            if (m.alpha is not (null or "opaque" or "mask" or "glass")) { failure = Refuse(name + ": alpha '" + m.alpha + "' is not opaque, mask or glass"); return null; }
            if (glass && (m.texture >= 0 || m.normalTexture >= 0 || m.metalTexture >= 0 || m.emissiveColor != null || m.emissiveTexture >= 0)) { failure = Refuse(name + ": a glass pane is one flat tint, with no texture, map or emission"); return null; }
            if (!Unit(m.cutoff)) { failure = Refuse(name + ": cutoff must be from 0 to 1"); return null; }
            if (cutout && m.texture < 0) { failure = Refuse(name + ": a cut-out needs a texture to cut by"); return null; }
            if (m.normalTexture < -1 || m.normalTexture >= textures.Length) { failure = Refuse(name + ": normal texture " + m.normalTexture + " is not listed"); return null; }
            if (m.metalTexture < -1 || m.metalTexture >= textures.Length) { failure = Refuse(name + ": metal texture " + m.metalTexture + " is not listed"); return null; }
            var emissive = m.emissiveColor ?? new float[3];
            if (emissive.Length != 3 || !Array.TrueForAll(emissive, v => v >= 0f && float.IsFinite(v))) { failure = Refuse(name + ": emissiveColor must be three finite numbers of 0 or more"); return null; }
            if (m.emissiveTexture < -1 || m.emissiveTexture >= textures.Length) { failure = Refuse(name + ": emissive texture " + m.emissiveTexture + " is not listed"); return null; }
            if (m.emissiveTexture >= 0 && emissive[0] <= 0f && emissive[1] <= 0f && emissive[2] <= 0f) { failure = Refuse(name + ": an emission map needs a non-zero emissiveColor"); return null; }
            materials[i] = new ImportedMaterial(name, (float[])colour.Clone(), m.texture, m.metallic, m.roughness, cutout, m.cutoff, m.normalTexture, m.metalTexture, (float[])emissive.Clone(), m.emissiveTexture, glass);
        }

        var submeshes = new ImportedSubmesh[d.submeshes?.Length ?? 0];
        if (submeshes.Length < 1) { failure = Refuse("the model has no submesh"); return null; }
        long drawn = 0;
        for (var i = 0; i < submeshes.Length; i++)
        {
            var s = d.submeshes![i];
            if (s == null) { failure = Refuse("submesh " + i + " is empty"); return null; }
            if (s.start < 0 || s.count < 3 || s.count % 3 != 0 || s.start % 3 != 0 || (long)s.start + s.count > d.mesh.indices) { failure = Refuse("submesh " + i + " (" + s.start + ", " + s.count + ") is not whole triangles inside the " + d.mesh.indices + " indices"); return null; }
            if (s.material < 0 || s.material >= materials.Length) { failure = Refuse("submesh " + i + " names the unknown material " + s.material); return null; }
            submeshes[i] = new ImportedSubmesh(s.start, s.count, s.material);
            drawn += s.count;
            if (drawn > MaxIndices) { failure = Refuse("the submeshes draw " + drawn + " indices in all (up to " + MaxIndices + "); a range listed twice counts twice"); return null; }
        }
        ImportedBone[]? bones = null;
        if (d.skin != null)
        {
            var rows = d.skin.bones;
            if (rows is not { Length: >= 1 } || rows.Length > MaxBones) { failure = Refuse("a skeleton needs 1 to " + MaxBones + " bones"); return null; }
            bones = new ImportedBone[rows.Length];
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rows.Length; i++)
            {
                var b = rows[i];
                if (b == null || string.IsNullOrEmpty(b.name) || !names.Add(b.name)) { failure = Refuse("bone " + i + " has no name of its own"); return null; }
                if (b.parent < -1 || b.parent >= i) { failure = Refuse(b.name + ": its parent " + b.parent + " is not an earlier bone"); return null; }
                if (b.rest is not { Length: 16 } rest || !Array.TrueForAll(rest, float.IsFinite)) { failure = Refuse(b.name + ": rest must be sixteen finite numbers"); return null; }
                bones[i] = new ImportedBone(b.name, b.parent, (float[])rest.Clone());
            }
        }
        return new ImportedModelDescriptor(JsonSerializer.Serialize(d), d.mesh.sha256!, d.mesh.vertices, d.mesh.indices, submeshes, materials, textures, bones);
    }

    private static ImportedModelFailure Refuse(string detail) => new(ImportedModelCodes.Descriptor, detail);

    private static bool Unit(float value) => value >= 0f && value <= 1f;

    private static bool Unit(float[] values)
    {
        foreach (var v in values) if (!Unit(v)) return false;
        return true;
    }

    private static bool IsHash(string? text)
    {
        if (text == null || text.Length != 64) return false;
        foreach (var c in text) if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) return false;
        return true;
    }
}

/// <summary>A model's vertices and indices as the mesh sidecar holds them, in Unity's space (left-handed, the converter flips the
/// glTF axis and the winding): interleaved position, normal and UV per vertex; a skinned mesh (format 2) then has, after the indices,
/// four bone indices (16-bit) and four weights (summing to 1) per vertex.</summary>
internal sealed class ImportedMesh
{
    /// <summary>Bytes of the sidecar header: the magic <c>FMSH</c>, the format (1), the vertex count and the index count, each a little-endian 32-bit number.</summary>
    public const int HeaderBytes = 16;
    public const uint Magic = 0x48534D46;
    private const float MinNormalSquared = 0.25f;

    private ImportedMesh(float[] positions, float[] normals, float[] uvs, int[] indices, int[]? boneIndices, float[]? boneWeights)
    {
        Positions = positions;
        Normals = normals;
        Uvs = uvs;
        Indices = indices;
        BoneIndices = boneIndices;
        BoneWeights = boneWeights;
    }

    /// <summary>Four per vertex, into the descriptor's bones; null for a still mesh.</summary>
    public int[]? BoneIndices { get; }
    /// <summary>Four per vertex, matching <see cref="BoneIndices"/>; null for a still mesh.</summary>
    public float[]? BoneWeights { get; }

    /// <summary>Three floats per vertex.</summary>
    public float[] Positions { get; }
    /// <summary>Three floats per vertex.</summary>
    public float[] Normals { get; }
    /// <summary>Two floats per vertex.</summary>
    public float[] Uvs { get; }
    public int[] Indices { get; }

    /// <summary>The mesh in <paramref name="bytes"/>, which must be exactly what <paramref name="descriptor"/> declares; null with the first
    /// fault otherwise.</summary>
    public static ImportedMesh? Read(byte[] bytes, ImportedModelDescriptor descriptor, out ImportedModelFailure failure)
    {
        failure = default;
        var vertices = descriptor.Vertices;
        var indices = descriptor.Indices;
        var bones = descriptor.Bones?.Count ?? 0;
        var version = (uint)(bones == 0 ? ImportedModelDescriptor.Format : ImportedModelDescriptor.SkinnedFormat);
        var size = HeaderBytes + vertices * 32L + indices * 4L + (bones == 0 ? 0L : vertices * 24L);
        if (bytes.Length != size)
        {
            failure = Bad("the mesh is " + bytes.Length + " bytes, the descriptor's " + vertices + " vertices and " + indices + " indices make " + size);
            return null;
        }
        if (BitConverter.ToUInt32(bytes, 0) != Magic || BitConverter.ToUInt32(bytes, 4) != version) { failure = Bad("the mesh is not a format-" + version + " Forge mesh"); return null; }
        if (BitConverter.ToUInt32(bytes, 8) != (uint)vertices || BitConverter.ToUInt32(bytes, 12) != (uint)indices) { failure = Bad("the mesh header disagrees with the descriptor's counts"); return null; }
        var positions = new float[vertices * 3];
        var normals = new float[vertices * 3];
        var uvs = new float[vertices * 2];
        var at = HeaderBytes;
        for (var v = 0; v < vertices; v++)
        {
            for (var k = 0; k < 3; k++, at += 4) positions[v * 3 + k] = BitConverter.ToSingle(bytes, at);
            for (var k = 0; k < 3; k++, at += 4) normals[v * 3 + k] = BitConverter.ToSingle(bytes, at);
            for (var k = 0; k < 2; k++, at += 4) uvs[v * 2 + k] = BitConverter.ToSingle(bytes, at);
            if (!float.IsFinite(positions[v * 3]) || !float.IsFinite(positions[v * 3 + 1]) || !float.IsFinite(positions[v * 3 + 2])
                || !float.IsFinite(normals[v * 3]) || !float.IsFinite(normals[v * 3 + 1]) || !float.IsFinite(normals[v * 3 + 2])
                || !float.IsFinite(uvs[v * 2]) || !float.IsFinite(uvs[v * 2 + 1]))
            { failure = Bad("vertex " + v + " holds a number that is not finite"); return null; }
            var n = normals[v * 3] * normals[v * 3] + normals[v * 3 + 1] * normals[v * 3 + 1] + normals[v * 3 + 2] * normals[v * 3 + 2];
            if (n < MinNormalSquared) { failure = Bad("vertex " + v + " has no usable normal"); return null; }
        }
        var triangles = new int[indices];
        for (var i = 0; i < indices; i++, at += 4)
        {
            var index = BitConverter.ToUInt32(bytes, at);
            if (index >= (uint)vertices) { failure = Bad("index " + i + " is " + index + ", past the " + vertices + " vertices"); return null; }
            triangles[i] = (int)index;
        }
        if (bones == 0) return new ImportedMesh(positions, normals, uvs, triangles, null, null);
        var boneIndices = new int[vertices * 4];
        var boneWeights = new float[vertices * 4];
        for (var v = 0; v < vertices; v++)
        {
            for (var k = 0; k < 4; k++, at += 2)
                if ((boneIndices[v * 4 + k] = BitConverter.ToUInt16(bytes, at)) >= bones) { failure = Bad("vertex " + v + " names bone " + boneIndices[v * 4 + k] + ", past the " + bones + " bones"); return null; }
            var sum = 0f;
            for (var k = 0; k < 4; k++, at += 4)
            {
                var w = boneWeights[v * 4 + k] = BitConverter.ToSingle(bytes, at);
                if (!float.IsFinite(w) || w < 0f) { failure = Bad("vertex " + v + " has a bad bone weight"); return null; }
                sum += w;
            }
            if (Math.Abs(sum - 1f) > 0.01f) { failure = Bad("vertex " + v + " has bone weights that sum to " + sum); return null; }
        }
        return new ImportedMesh(positions, normals, uvs, triangles, boneIndices, boneWeights);
    }

    private static ImportedModelFailure Bad(string detail) => new(ImportedModelCodes.Mesh, detail);
}

/// <summary>Colour numbers for the flat colours a model without a texture is given.</summary>
internal static class ImportedColour
{
    /// <summary>The 8-bit sRGB value of a linear component from 0 to 1 (glTF base colours are linear; a texture holds sRGB).</summary>
    internal static byte LinearToSrgbByte(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        var s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)MathF.Round(s * 255f, MidpointRounding.AwayFromZero);
    }

    /// <summary>The 8-bit value of a number from 0 to 1 stored as it is (the metal and smoothness channels are not colours).</summary>
    internal static byte UnitToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f, MidpointRounding.AwayFromZero);

}
