using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ForgeVfx.EditorTools
{
    /// <summary>The paths and conventions the build and preview entries share.
    ///
    /// Every prefab in <see cref="EffectsFolder"/> is one effect and is named after the `effect_play` member that plays
    /// it (`forge_sparks.prefab` is played by `forge_sparks`). A prefab plays at the request's position, faces the
    /// request's direction along its local +Z (or the far end, for a line member), and is scaled by `size` over the
    /// member's default size with Hierarchy scaling; a line member is authored one metre long along +Z and stretched
    /// to the far end. A sustained member loops at its root while its player keeps it alive and is stopped (emission
    /// off, live particles finish) when the player lets it go; every other member is one-shot.
    ///
    /// <see cref="Members"/> mirrors the mod's member table (`Mods/ForgeEngine/GameBindings/Presentation/EffectMembers.cs`, sustain in
    /// `VfxBundle.cs`); the manifest records these facts per effect and the mod's tests hold the two sides together.</summary>
    internal static class ForgeVfxProject
    {
        internal const string Root = "Assets/ForgeVfx/";
        internal const string EffectsFolder = Root + "Effects";
        internal const string SourcesFile = Root + "sources.json";
        internal const string BundleName = "forge-vfx.bundle";
        /// <summary>The game layers a colliding particle layer (flame puffs, sparks, embers) stops at: every layer but
        /// Unity's Ignore Raycast (2) and UI (5) and GTFO's PlayerMover (10, the players' own movement capsules) and
        /// InvisibleWall (13). The game's interop cannot reach the collision module, so the mask is baked into the
        /// prefabs here. GTFO's layer numbers were read from the game's TagManager (globalgamemanagers).</summary>
        internal const int ParticleCollisionMask = ~((1 << 2) | (1 << 5) | (1 << 10) | (1 << 13));

        private static readonly Regex MemberName = new Regex("^forge_[a-z]+(_[a-z]+)*$");

        internal sealed class Member
        {
            internal Member(string name, bool line = false, bool sustained = false, float size = 0f, float extent = 1.5f)
            {
                Name = name;
                Line = line;
                Sustained = sustained;
                Size = size;
                Extent = extent;
            }

            internal string Name { get; }
            /// <summary>Drawn from the request position to its far end.</summary>
            internal bool Line { get; }
            internal bool Sustained { get; }
            /// <summary>The member's default size (the prefab is authored at it), or 0 for a member without a size.</summary>
            internal float Size { get; }
            /// <summary>Roughly how far the effect reaches from its origin, in metres; the preview frames it.</summary>
            internal float Extent { get; }
        }

        internal static readonly Member[] Members =
        {
            new Member("forge_energy_beam", line: true, sustained: true),
            new Member("forge_lightning_arc", line: true),
            new Member("forge_blast_tracer", line: true),
            new Member("forge_plasma_discharge", line: true),
            new Member("forge_plasma_orb", sustained: true, size: 0.3f, extent: 1f),
            new Member("forge_plasma_explosion", size: 3f, extent: 3.5f),
            new Member("forge_explosion", size: 2.2f, extent: 3f),
            new Member("forge_flame", sustained: true, size: 8f, extent: 3.5f),
            new Member("forge_flame_impact", extent: 1f),
            new Member("forge_sparks", extent: 1.2f),
            new Member("forge_black_hole", sustained: true, size: 8f, extent: 4f)
        };

        internal static Member Spec(string name)
            => Members.FirstOrDefault(member => member.Name == name)
               ?? throw new InvalidOperationException(name + " is not a Forge effect member; add it to ForgeVfxProject.Members with the mod's table.");

        /// <summary>The Unity project folder (the parent of Assets).</summary>
        internal static string ProjectFolder => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>The repository root: the project lives at Tools/Vfx/ForgeVfx.</summary>
        internal static string RepositoryRoot => Path.GetFullPath(Path.Combine(ProjectFolder, "..", "..", ".."));

        internal static string[] EffectPrefabs()
        {
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { EffectsFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => Path.GetDirectoryName(path)?.Replace('\\', '/') == EffectsFolder)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            foreach (var path in paths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!MemberName.IsMatch(name))
                    throw new InvalidOperationException(path + ": an effect prefab is named after its effect_play member (forge_<name>).");
            }
            return paths;
        }

        /// <summary>The value after a `-name` command-line argument, or null.</summary>
        internal static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index++)
                if (string.Equals(args[index], name, StringComparison.Ordinal)) return args[index + 1];
            return null;
        }

        /// <summary>Runs a batchmode entry and exits with 0 on success and 1 on any failure, with the reason logged.</summary>
        internal static void RunAndExit(string label, Action body)
        {
            try
            {
                body();
                Debug.Log(label + ": done");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogError(label + " failed: " + error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw;
            }
        }
    }
}
