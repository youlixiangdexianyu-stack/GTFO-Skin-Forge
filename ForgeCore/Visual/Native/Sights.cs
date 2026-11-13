using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>The sights ForgeVisual ships (<see cref="GunSights.Kinds"/>), built once from their embedded Forge model packages, and how one is stood on
/// a gun model: in its place go the model's replaced sight parts, and the model's <c>SightLook</c> moves to the sight's look point.</summary>
internal static class Sights
{
    // Lives as long as the game: three small meshes shared by every gun, kept through every level's unused-asset sweep.
    private static readonly ImportedModels Built = new(keep: true);

    /// <summary>Stands <paramref name="fit"/>'s sight on the model under <paramref name="root"/> (turned by <paramref name="front"/>) and returns it, or
    /// null with the model as it was when the sight cannot be built yet (the game's gear shader is not loaded; one error line).</summary>
    internal static GameObject? Attach(Transform root, GunSockets sockets, GunSightFit fit, Quaternion front, string name, out SightKind kind)
    {
        kind = GunSights.Kinds[fit.Sight];
        var model = Model(fit.Sight);
        if (model == null) return null;
        foreach (var part in fit.Replace)
        {
            var old = Models.Find(root, part) ?? throw new System.IO.InvalidDataException("The gun model " + name + " has no part " + part + " for its sight to replace.");
            // Out of every lookup at once (Destroy is deferred), so a caller that finds parts by name no longer sees it.
            old.gameObject.SetActive(false);
            old.SetParent(null, false);
            Object.Destroy(old.gameObject);
        }
        var sight = model.Spawn(root, "ForgeVisual.Sight." + fit.Sight);
        sight.transform.localPosition = new Vector3(fit.At.X, fit.At.Y, fit.At.Z);
        // The sight faces +Z; the model's front is where Front turns its +Z.
        sight.transform.localRotation = Quaternion.Inverse(front);
        if (sockets.Sight == null)
        {
            var look = new GameObject(GunNodes.SightSocket).transform;
            look.SetParent(root, false);
            sockets.Sight = look;
        }
        sockets.Sight.position = sight.transform.TransformPoint(new Vector3(kind.Look.X, kind.Look.Y, kind.Look.Z));
        return sight;
    }

    private static ImportedModel? Model(string id)
    {
        var descriptor = ImportedModelDescriptor.Parse(GunSights.Text(typeof(Sights).Assembly, "ForgeVisual.Sights." + id + ".model.json"), out var failure)
            ?? throw new System.IO.InvalidDataException("ForgeVisual's sight " + id + " has a bad descriptor: " + failure + ".");
        var textures = new List<byte[]>();
        for (var i = 0; i < descriptor.Textures.Count; i++) textures.Add(Bytes(id + ".t" + i + ".png", descriptor.Textures[i].Sha256));
        // A failure is logged once by the cache; the gun keeps its own sight.
        return Built.Create(descriptor, Bytes(id + ".forgemesh", descriptor.MeshSha256), textures).Model;
    }

    // The cache takes bytes as given (the caller matches them to the descriptor), so a texture packed under the wrong index is refused here
    // instead of drawing its surface map as colour.
    private static byte[] Bytes(string file, string sha256)
    {
        using var stream = typeof(Sights).Assembly.GetManifestResourceStream("ForgeVisual.Sights." + file)
            ?? throw new System.IO.InvalidDataException("ForgeVisual is built without its sight file " + file + ".");
        using var bytes = new System.IO.MemoryStream();
        stream.CopyTo(bytes);
        var data = bytes.ToArray();
        if (!string.Equals(System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)), sha256, System.StringComparison.OrdinalIgnoreCase))
            throw new System.IO.InvalidDataException("ForgeVisual's sight file " + file + " is not the one its descriptor names (SHA-256 " + sha256[..8] + ").");
        return data;
    }
}
