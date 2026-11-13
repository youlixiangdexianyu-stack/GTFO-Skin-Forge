using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>The outcome of <see cref="ImportedModels.Create"/>: the model, or why there is none (a failure is also logged, once).</summary>
public sealed class ImportedModelResult
{
    internal ImportedModelResult(ImportedModel? model, ImportedModelFailure failure)
    {
        Model = model;
        Failure = failure;
    }

    public ImportedModel? Model { get; }
    public ImportedModelFailure Failure { get; }
    public bool Ok => Model != null;
}

/// <summary>A built imported model: one Unity mesh with a submesh per run of its descriptor and one lit material per submesh. Every
/// instance made by <see cref="Spawn"/> shares them. The cache that made it owns it; do not destroy its parts.</summary>
public sealed class ImportedModel
{
    private readonly Mesh _mesh;
    private readonly Material[] _slots;
    private readonly Matrix4x4[]? _rest;
    private readonly List<GameObject> _spawned = new();

    internal ImportedModel(ImportedModelDescriptor descriptor, Mesh mesh, Material[] slots, List<Object> owned, Matrix4x4[]? rest)
    {
        Descriptor = descriptor;
        _mesh = mesh;
        _slots = slots;
        _rest = rest;
        Owned = owned;
    }

    /// <summary>Whether the model is skinned to its own skeleton (descriptor format 2): <see cref="Spawn"/> then builds the bones too.</summary>
    public bool Skinned => _rest != null;

    public ImportedModelDescriptor Descriptor { get; }

    /// <summary>The mesh, for bounds and counts. Shared and no longer readable.</summary>
    public Mesh Mesh => _mesh;

    /// <summary>The materials in submesh order; shared by every instance. <c>Glow</c> and tints are written on copies.</summary>
    public IReadOnlyList<Material> Materials => _slots;

    internal List<Object> Owned { get; }

    /// <summary>A new object (under <paramref name="parent"/> when given) that draws the model, no collider: a mesh filter and a mesh renderer,
    /// or for a skinned model a skinned mesh renderer on the object and its skeleton below it, each bone a child object named as in the
    /// descriptor and standing at its rest pose (so the model stands as it was built; a caller poses the bones from there).</summary>
    public GameObject Spawn(Transform? parent = null, string? name = null)
    {
        var o = new GameObject(name ?? "Imported " + Descriptor.MeshSha256[..8]);
        if (parent != null) o.transform.SetParent(parent, false);
        if (_rest == null)
        {
            o.AddComponent<MeshFilter>().sharedMesh = _mesh;
            o.AddComponent<MeshRenderer>().sharedMaterials = new Il2CppReferenceArray<Material>(_slots);
        }
        else
        {
            var specs = Descriptor.Bones!;
            var bones = new Transform[specs.Count];
            for (var i = 0; i < bones.Length; i++)
            {
                var bone = new GameObject(specs[i].Name).transform;
                var up = specs[i].Parent;
                bone.SetParent(up < 0 ? o.transform : bones[up], false);
                var local = up < 0 ? _rest[i] : _rest[up].inverse * _rest[i];
                bone.localPosition = local.GetColumn(3);
                bone.localRotation = local.rotation;
                bone.localScale = local.lossyScale;
                bones[i] = bone;
            }
            var skin = o.AddComponent<SkinnedMeshRenderer>();
            skin.sharedMesh = _mesh;
            skin.bones = new Il2CppReferenceArray<Transform>(bones);
            skin.rootBone = bones[0];
            skin.sharedMaterials = new Il2CppReferenceArray<Material>(_slots);
        }
        _spawned.RemoveAll(s => s == null || s.WasCollected);
        _spawned.Add(o);
        return o;
    }

    internal void DestroySpawned()
    {
        foreach (var s in _spawned) if (s != null && !s.WasCollected) Object.Destroy(s);
        _spawned.Clear();
    }
}

/// <summary>
/// Imported models (a player's uploaded <c>.glb</c> or a geomorph's own model, converted to the Forge model package) built from the
/// bytes a caller read and checked: the mesh as a Unity mesh with a submesh per material, the PNGs as textures with mip chains, one
/// lit material (<see cref="LitMaterial"/>) per material record. One cache is one level's models: the same descriptor (<see cref="ImportedModelDescriptor.Key"/>,
/// so two models sharing a mesh with other materials stay apart) is built once, a bad one is refused the same way every time, and <see cref="Dispose"/> destroys every instance, mesh, material and texture it made.
/// The library reads no file and computes no hash: the caller (ForgeEngine) found the bytes, matched them to the descriptor's hashes and
/// decided what a level may hold. Only the draw is here, no collider.
/// </summary>
public sealed class ImportedModels : IDisposable
{
    private readonly Dictionary<string, ImportedModel> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportedModelFailure> _failures = new(StringComparer.Ordinal);
    private readonly ImageTextures _images;
    private readonly bool _keep;

    /// <param name="budgetBytes">The most <see cref="ImportedModelDescriptor.EstimatedBytes"/> the cache may hold at once; 0 for no limit. A model
    /// that would pass it is refused with <see cref="ImportedModelCodes.OverBudget"/> and nothing is built.</param>
    /// <param name="keep">For a cache that outlives a level: everything it builds is marked with <see cref="ForgeCore.Assets.Keep"/>, so the game's
    /// unused-asset sweep at a scene change does not unload a mesh, material or texture no instance happens to draw at that moment.</param>
    public ImportedModels(long budgetBytes = 0, bool keep = false)
    {
        BudgetBytes = budgetBytes;
        _keep = keep;
        _images = new ImageTextures(surface: true, keep: keep);
    }

    public long BudgetBytes { get; set; }

    /// <summary>The estimated bytes of the models built so far.</summary>
    public long ResidentBytes { get; private set; }

    public int Count => _models.Count;

    /// <summary>The model of <paramref name="descriptor"/> from its mesh bytes and its PNG textures (in the descriptor's order).</summary>
    public ImportedModelResult Create(ImportedModelDescriptor descriptor, byte[] mesh, IReadOnlyList<byte[]> textures)
    {
        var key = descriptor.Key;
        if (_models.TryGetValue(key, out var built)) return new ImportedModelResult(built, default);
        if (_failures.TryGetValue(key, out var prior)) return new ImportedModelResult(null, prior);
        if (BudgetBytes != 0 && ResidentBytes + descriptor.EstimatedBytes > BudgetBytes)
            return Refuse(descriptor, new ImportedModelFailure(ImportedModelCodes.OverBudget, descriptor.EstimatedBytes + " bytes on top of " + ResidentBytes + " pass the budget of " + BudgetBytes), false);
        if (textures.Count != descriptor.Textures.Count)
            return Refuse(descriptor, new ImportedModelFailure(ImportedModelCodes.TextureCount, "the descriptor lists " + descriptor.Textures.Count + " textures, " + textures.Count + " came"), true);
        var data = ImportedMesh.Read(mesh, descriptor, out var failure);
        if (data == null) return Refuse(descriptor, failure, true);
        if (Shader.Find(LitMaterial.ShaderName) == null)
            return Refuse(descriptor, new ImportedModelFailure(ImportedModelCodes.ShaderUnavailable, "the game's gear shader is not loaded yet; models show once a level has been built"), false);

        var owned = new List<Object>();
        try
        {
            var model = Build(descriptor, data, textures, owned, out failure);
            if (model == null)
            {
                foreach (var o in owned) if (o != null && !o.WasCollected) Object.Destroy(o);
                return Refuse(descriptor, failure, true);
            }
            if (_keep) foreach (var o in owned) ForgeCore.Assets.Keep(o);
            _models.Add(key, model);
            ResidentBytes += descriptor.EstimatedBytes;
            return new ImportedModelResult(model, default);
        }
        catch (Exception error)
        {
            foreach (var o in owned) if (o != null && !o.WasCollected) Object.Destroy(o);
            return Refuse(descriptor, new ImportedModelFailure(ImportedModelCodes.Build, error.Message), true);
        }
    }

    private ImportedModel? Build(ImportedModelDescriptor d, ImportedMesh data, IReadOnlyList<byte[]> pngs, List<Object> owned, out ImportedModelFailure failure)
    {
        failure = default;
        // The mesh file carries no tangents: they are computed for a normal map only.
        var runs = new (int, int)[d.Submeshes.Count];
        for (var i = 0; i < runs.Length; i++) runs[i] = (d.Submeshes[i].IndexStart, d.Submeshes[i].IndexCount);
        var mesh = MeshArrays.Build("Forge imported " + d.MeshSha256[..8], data.Positions, data.Normals, data.Uvs, null, data.Indices,
            runs, computeTangents: d.HasNormalMap);
        owned.Add(mesh);
        Matrix4x4[]? rest = null;
        if (d.Bones != null)
        {
            rest = new Matrix4x4[d.Bones.Count];
            var binds = new Matrix4x4[rest.Length];
            for (var b = 0; b < rest.Length; b++)
            {
                var m = d.Bones[b].Rest;
                rest[b] = new Matrix4x4(new Vector4(m[0], m[1], m[2], m[3]), new Vector4(m[4], m[5], m[6], m[7]), new Vector4(m[8], m[9], m[10], m[11]), new Vector4(m[12], m[13], m[14], m[15]));
                binds[b] = rest[b].inverse;
            }
            mesh.boneWeights = new Il2CppStructArray<BoneWeight>(Weights(data));
            mesh.bindposes = new Il2CppStructArray<Matrix4x4>(binds);
        }
        // A skinned mesh stays readable: the game may skin it on the CPU.
        mesh.UploadMeshData(rest == null);

        Texture2D? Decode(ImportedMaterial spec, int index, bool linear, out ImportedModelFailure fault, bool normal = false)
        {
            fault = default;
            var t = d.Textures[index];
            if (_images.TryGet(t.Sha256, pngs[index], t.Width, t.Height, out var decoded, out var code, out var error, linear, normal)) return decoded;
            fault = new ImportedModelFailure(ImportedModelCodes.TextureDecode, spec.Name + ": texture " + index + " (" + code + (error == null ? "" : ", " + error.Message) + ")");
            return null;
        }

        var made = new Material[d.Materials.Count];
        for (var m = 0; m < made.Length; m++)
        {
            var spec = d.Materials[m];
            if (spec.Glass)
            {
                var glass = LitMaterial.Glass("Forge imported " + spec.Name, new Color(spec.BaseColor[0], spec.BaseColor[1], spec.BaseColor[2], spec.BaseColor[3]));
                if (glass == null) { failure = new ImportedModelFailure(ImportedModelCodes.ShaderUnavailable, spec.Name + ": the game's glass shader is not loaded yet"); return null; }
                owned.Add(glass);
                made[m] = glass;
                continue;
            }
            Texture albedo;
            if (spec.Texture >= 0)
            {
                var decoded = Decode(spec, spec.Texture, false, out failure);
                if (decoded == null) return null;
                albedo = decoded;
            }
            else
            {
                albedo = Flat(spec.Name + " colour", linear: false,
                    ImportedColour.LinearToSrgbByte(spec.BaseColor[0]), ImportedColour.LinearToSrgbByte(spec.BaseColor[1]), ImportedColour.LinearToSrgbByte(spec.BaseColor[2]), ImportedColour.UnitToByte(spec.BaseColor[3]));
                owned.Add(albedo);
            }
            // The gear shader reads metal from red and smoothness from green of one map.
            Texture surface;
            if (spec.MetalTexture >= 0)
            {
                var map = Decode(spec, spec.MetalTexture, true, out failure);
                if (map == null) return null;
                surface = map;
            }
            else
            {
                surface = Flat(spec.Name + " surface", linear: true, ImportedColour.UnitToByte(spec.Metallic), ImportedColour.UnitToByte(1f - spec.Roughness), 255, 255);
                owned.Add(surface);
            }
            var material = LitMaterial.Create("Forge imported " + spec.Name, albedo, surface, spec.Cutout, spec.Cutoff, out var why);
            if (material == null)
            {
                failure = new ImportedModelFailure(spec.Cutout ? ImportedModelCodes.CutoutUnsupported : ImportedModelCodes.ShaderUnavailable, spec.Name + ": " + why);
                return null;
            }
            owned.Add(material);
            if (spec.NormalTexture >= 0)
            {
                var normal = Decode(spec, spec.NormalTexture, true, out failure, normal: true);
                if (normal == null) return null;
                LitMaterial.SetRelief(material, normal);
            }
            if (spec.Emissive)
            {
                Texture2D? map = Texture2D.whiteTexture;
                if (spec.EmissiveTexture >= 0)
                {
                    map = Decode(spec, spec.EmissiveTexture, false, out failure);
                    if (map == null) return null;
                }
                const float gain = LitMaterial.EmissionPerUnit;
                Glow.Write(material, new Color(spec.EmissiveColor[0] * gain, spec.EmissiveColor[1] * gain, spec.EmissiveColor[2] * gain, 1f), map);
            }
            made[m] = material;
        }
        var slots = new Material[d.Submeshes.Count];
        for (var i = 0; i < slots.Length; i++) slots[i] = made[d.Submeshes[i].Material];
        return new ImportedModel(d, mesh, slots, owned, rest);
    }

    /// <summary>Unity's bone weights: each vertex's four, heaviest first.</summary>
    private static BoneWeight[] Weights(ImportedMesh data)
    {
        var index = data.BoneIndices!;
        var weight = data.BoneWeights!;
        var rows = new BoneWeight[index.Length / 4];
        Span<int> order = stackalloc int[4];
        for (var v = 0; v < rows.Length; v++)
        {
            for (var k = 0; k < 4; k++) order[k] = k;
            for (var a = 1; a < 4; a++)
                for (var b = a; b > 0 && weight[v * 4 + order[b]] > weight[v * 4 + order[b - 1]]; b--) (order[b], order[b - 1]) = (order[b - 1], order[b]);
            rows[v] = new BoneWeight
            {
                boneIndex0 = index[v * 4 + order[0]],
                weight0 = weight[v * 4 + order[0]],
                boneIndex1 = index[v * 4 + order[1]],
                weight1 = weight[v * 4 + order[1]],
                boneIndex2 = index[v * 4 + order[2]],
                weight2 = weight[v * 4 + order[2]],
                boneIndex3 = index[v * 4 + order[3]],
                weight3 = weight[v * 4 + order[3]],
            };
        }
        return rows;
    }

    private static Texture2D Flat(string name, bool linear, byte r, byte g, byte b, byte a)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear) { name = "Forge imported " + name };
        var pixel = new Color32(r, g, b, a);
        texture.SetPixels32(new Il2CppStructArray<Color32>(new[] { pixel, pixel, pixel, pixel }));
        texture.Apply(false, true);
        return texture;
    }

    private ImportedModelResult Refuse(ImportedModelDescriptor descriptor, ImportedModelFailure failure, bool remember)
    {
        if (remember) _failures[descriptor.Key] = failure;
        Plugin.Logger.ErrorOnce("visual-imported-model-not-built", "ForgeVisual imported model " + descriptor.MeshSha256[..8] + " was not built, " + failure + ".");
        return new ImportedModelResult(null, failure);
    }

    /// <summary>Destroys every instance and everything the cache built, and forgets the failures.</summary>
    public void Dispose()
    {
        foreach (var model in _models.Values)
        {
            model.DestroySpawned();
            foreach (var o in model.Owned) if (o != null && !o.WasCollected) Object.Destroy(o);
        }
        _models.Clear();
        _failures.Clear();
        ResidentBytes = 0;
        _images.Dispose();
    }
}
