using System.Numerics;
using System.Security.Cryptography;
using ForgeVisual;

namespace ForgeSkin.Format;

// A character skin (PLAN §4.10): the skin document's `character`. This file is the one definition of the format; the plugin, the
// editor's backend (which compiles Core/ too) and the editor's TypeScript types follow it. A document with `character` is a character
// skin: its target is `{ "weapon": "character" }` (no gear or item id) and it has no materials, attachments, parts or sounds of a gear.
// Every name of a model bone is a bone name of the model's skeleton (the descriptor's `skin.bones[].name`); every joint is a name of the
// canonical humanoid (ForgeCore ClipRig.Names: "hip", "spine0", "leftUpperArm", "leftIndex1", ...). Positions are the model's own frame
// (Unity axes, metres). In a pack every path is a file inside the zip; in an editor project a path on disk.
//
//   "character": {
//     "model": { "descriptor": "character/model.json", "mesh": "character/model.forgemesh", "textures": ["character/t0.png"] },
//     "bones": { "hip": "Hips", "spine0": "Spine", ..., "leftIndex1": "LeftHandIndex1", ... },
//     "arms": null,
//     "springs": {
//       "chains": [ { "id": "ponytail", "kind": "longHair", "anchor": "Head", "strands": [["Hair1", "Hair2", "Hair3"]],
//                     "linkSide": false, "loop": false, "weight": 1, "damping": 0.4, "stiffness": 0.35, "gravity": 1, "radius": 0.015 } ],
//       "capsules": [ { "joint": "head", "a": [0, 0.08, 0], "b": [0, 0.08, 0], "radius": 0.1 } ] },
//     "hide": [2],
//     "height": 1,
//     "picture": "character/picture.png",
//     "materials": [ { "name": "Body", "shader": "lit" }, { "name": "Hair", "shader": "toon", "params": { "outlineWidth": [1.5] } } ]
//   }
public sealed class CharacterSkin
{
    /// <summary>The target key of every character skin (SkinTarget.Weapon and the picker's slot).</summary>
    public const string Target = "character";
    public const float MaxRadius = 0.5f;

    // The model: ForgeCore ImportedModels format 2 (a skinned mesh with its own humanoid skeleton). `descriptor` is the JSON descriptor
    // (ImportedModelDescriptor, format 2, with only its technical bounds: MaxBones, MaxTextureSide and MaxArray), `mesh` its
    // mesh file (FMSH format 2), `textures` its PNGs in the descriptor's order. The plugin checks each file's SHA-256 against the
    // descriptor before building the model.
    public CharacterModelFiles Model { get; set; } = new();

    // Canonical joint -> model bone. Required (CharacterRig.Required): hip, spine0, spine1, neck, head, both shoulders, upper arms,
    // lower arms, hands, upper legs, lower legs and feet. spine2 and the 30 finger joints are optional; a hand that lacks any of its
    // 15 finger joints keeps all its fingers in the model's rest pose.
    public Dictionary<string, string> Bones { get; set; } = new();

    // First-person arms: null takes them from the model (the triangles skinned to the upper arms and below); set, another model of
    // the same skeleton, which the plugin does not read yet (it warns and wears the model's own arms).
    public CharacterArms? Arms { get; set; }

    public CharacterSprings Springs { get; set; } = new();

    // Indices of the descriptor's materials not drawn in first person (left out of the first-person arms).
    public List<int>? Hide { get; set; }

    // One per descriptor material, in its order (empty: every material lit). Name is the descriptor material's name (the target of a
    // Tint or Emissive player colour); Shader one of ForgeCore's shader set, "lit" (the game's gear shader, the default) or "toon";
    // Params that shader's parameters by name (ForgeCore Visual/Data/shaders.json), each one to four numbers.
    public List<CharacterMaterial> Materials { get; set; } = new();

    // The author's size on top of the fit: worn on a body, the model is first scaled so its height (feet to head, CharacterRig.Stature)
    // is the body's, then by this. Any positive number; 1 is the body's height.
    public float Height { get; set; } = 1;

    // The card picture (a PNG, the editor's 3D view when it packed the skin): the skin page shows it without loading the model. Null:
    // the plugin photographs the model once it is loaded.
    public string? Picture { get; set; }
}

public sealed class CharacterModelFiles
{
    public string Descriptor { get; set; } = "";
    public string Mesh { get; set; } = "";
    public List<string> Textures { get; set; } = new();
}

public sealed class CharacterArms
{
    public CharacterModelFiles? Model { get; set; }
}

public sealed class CharacterSprings
{
    public List<SpringChain> Chains { get; set; } = new();
    // Shared by every chain: what they are kept out of.
    public List<SpringCapsule> Capsules { get; set; } = new();
}

// ForgeCore ChainSpec's presets, the solver's starting point: long hair or a ponytail, short hair, a skirt, a tail or ribbon.
public enum SpringKind { LongHair, ShortHair, Skirt, Tail }

// Spring bones moved by ForgeCore's chain physics after the pose copy. Anchor is the model bone the strands hang from (the head, the
// hips); each strand its bones from the root down, at least two, each a descendant of Anchor; LinkSide links neighbouring strands
// node to node (a skirt), Loop the last to the first too. The author values override the preset's when set: weight 0.1 to 3, damping and
// stiffness 0 to 1, gravity 0 to 3 times real gravity, radius 0 to MaxRadius metres.
public sealed class SpringChain
{
    public string Id { get; set; } = "";
    public SpringKind Kind { get; set; }
    public string Anchor { get; set; } = "";
    public List<List<string>> Strands { get; set; } = new();
    public bool LinkSide { get; set; }
    public bool Loop { get; set; }
    public float? Weight { get; set; }
    public float? Damping { get; set; }
    public float? Stiffness { get; set; }
    public float? Gravity { get; set; }
    public float? Radius { get; set; }

    // The ForgeCore spec: the preset with the author's values over it.
    public ChainSpec Spec()
    {
        var spec = Kind switch { SpringKind.ShortHair => ChainSpec.ShortHair(), SpringKind.Skirt => ChainSpec.Skirt(), SpringKind.Tail => ChainSpec.Tail(), _ => ChainSpec.LongHair() };
        if (Weight is { } w) spec.Weight = w;
        if (Damping is { } d) spec.Damping = d;
        if (Stiffness is { } s) spec.Stiffness = s;
        if (Gravity is { } g) spec.Gravity = g;
        if (Radius is { } r) spec.Radius = r;
        return spec;
    }
}

// A capsule on the model bone of `joint` from A to B in that bone's own space (a sphere when A is B), radius 0.005 to MaxRadius metres.
public sealed class SpringCapsule
{
    public string Joint { get; set; } = "";
    public float[] A { get; set; } = { 0, 0, 0 };
    public float[] B { get; set; } = { 0, 0, 0 };
    public float Radius { get; set; } = 0.08f;
}

public sealed class CharacterMaterial
{
    public const string Lit = "lit", Toon = "toon";
    public string Name { get; set; } = "";
    public string Shader { get; set; } = Lit;
    public Dictionary<string, float[]>? Params { get; set; }
}

// The bone map against the canonical humanoid, and the model files against their descriptor. Pure: the plugin, the backend and the tests.
public static class CharacterRig
{
    public static readonly string[] Required =
    {
        "hip", "spine0", "spine1", "neck", "head", "leftShoulder", "rightShoulder", "leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm",
        "leftHand", "rightHand", "leftUpperLeg", "rightUpperLeg", "leftLowerLeg", "rightLowerLeg", "leftFoot", "rightFoot",
    };

    // The model bone of every canonical joint, in ClipRig order (null: not mapped). A hand with only some finger joints maps none.
    public static string?[] Joints(CharacterSkin character)
    {
        var joints = new string?[ClipRig.Count];
        for (int j = 0; j < ClipRig.Count; j++)
            joints[j] = character.Bones.TryGetValue(ClipRig.Names[j], out var bone) && !string.IsNullOrWhiteSpace(bone) ? bone : null;
        foreach (var first in new[] { ClipRig.BodyCount, ClipRig.BodyCount + 15 })
        {
            int have = 0;
            for (int j = first; j < first + 15; j++) if (joints[j] != null) have++;
            if (have is not (0 or 15)) for (int j = first; j < first + 15; j++) joints[j] = null;
        }
        return joints;
    }

    // What is wrong with the character against its model's skeleton, one sentence each; empty when it fits: a mapped bone or a spring
    // bone the skeleton lacks, a capsule on an unmapped joint, materials that do not match the descriptor's, and the rest pose against the
    // canonical rig (ForgeCore RigMap.Check: sides swapped, facing backwards, legs or spine more than 60 degrees off).
    public static string Check(CharacterSkin character, ImportedModelDescriptor model)
    {
        var wrong = "";
        if (model.Bones is not { } skeleton) return "the model has no skeleton (descriptor format 2).";
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < skeleton.Count; i++) index[skeleton[i].Name] = i;
        var joints = Joints(character);
        var present = new bool[ClipRig.Count];
        var rest = new Vector3[ClipRig.Count];
        for (int j = 0; j < ClipRig.Count; j++)
        {
            if (joints[j] is not { } bone) continue;
            if (!index.TryGetValue(bone, out var b)) { wrong += $" {ClipRig.Names[j]}: the skeleton has no bone {bone}."; continue; }
            var m = skeleton[b].Rest;
            present[j] = true;
            rest[j] = new Vector3(m[12], m[13], m[14]);
        }
        wrong += RigMap.Check(present, rest, RigShape.Body);
        foreach (var chain in character.Springs.Chains)
            foreach (var bone in chain.Strands.SelectMany(s => s).Append(chain.Anchor))
                if (!index.ContainsKey(bone)) wrong += $" spring {chain.Id}: the skeleton has no bone {bone}.";
        foreach (var capsule in character.Springs.Capsules)
            if (Array.IndexOf(ClipRig.Names, capsule.Joint) is var j && (j < 0 || !present[j])) wrong += $" capsule on {capsule.Joint}: that joint is not mapped.";
        if (character.Materials.Count > 0 && character.Materials.Count != model.Materials.Count)
            wrong += $" {character.Materials.Count} materials for the model's {model.Materials.Count}.";
        foreach (var i in character.Hide ?? new()) if (i < 0 || i >= model.Materials.Count) wrong += $" hide: the model has no material {i}.";
        if (!float.IsFinite(character.Height) || character.Height <= 0) wrong += $" height {character.Height} is not a positive number.";
        else if (present[Array.IndexOf(ClipRig.Names, "head")] && Stature(character, model) <= 0) wrong += " the head is not above the feet, so the model's height cannot be measured.";
        return wrong.Trim();
    }

    // Null when the mesh and textures are the ones the descriptor names and ForgeCore can build them (its one file check,
    // ImportedModelDescriptor.CheckFiles, which the game runs again when it builds the model); else which is not. The editor's export
    // read-back and the plugin's load both ask this, so a pack the export accepts is one the game builds.
    public static string? Matches(ImportedModelDescriptor model, byte[] mesh, IReadOnlyList<byte[]> textures)
    {
        if (Sha256(mesh) != model.MeshSha256) return "the mesh is not the one the descriptor names";
        if (textures.Count != model.Textures.Count) return $"{textures.Count} textures for the descriptor's {model.Textures.Count}";
        for (int i = 0; i < textures.Count; i++)
            if (Sha256(textures[i]) != model.Textures[i].Sha256) return $"texture {i} is not the one the descriptor names";
        return model.CheckFiles(mesh, textures, out var failure) ? null : failure.ToString();
    }

    // The model's height at rest, feet to head: the head joint's height over the mean of the two feet's, in the model's own units (0 when
    // a joint is not mapped). Bones, not bounds: hair, hats and raised arms do not count.
    public static float Stature(CharacterSkin character, ImportedModelDescriptor model)
    {
        if (model.Bones is not { } skeleton) return 0;
        float? Y(string joint) => character.Bones.TryGetValue(joint, out var bone) && skeleton.FirstOrDefault(b => b.Name == bone) is { } b ? b.Rest[13] : null;
        return Y("head") is { } head && Y("leftFoot") is { } left && Y("rightFoot") is { } right ? head - (left + right) / 2 : 0;
    }

    static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
