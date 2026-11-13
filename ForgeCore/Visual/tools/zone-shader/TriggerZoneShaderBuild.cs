#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;

// Copy this file into a temporary Unity 2019.4 project's Assets/Editor; Build copies TriggerZone.shader into Assets itself.
// Invoke -batchmode -executeMethod TriggerZoneShaderBuild.Build --zonefx-root <repository-root>. No game is launched.
public static class TriggerZoneShaderBuild
{
    public static void Build()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "--zonefx-root");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("--zonefx-root is required.");
        var root = Path.GetFullPath(args[at + 1]);
        var native = Path.Combine(root, "Mods/ForgeCore/Visual/tools/zone-shader"); var generated = Path.Combine(root, "Mods/ForgeCore/Visual/Native");
        var source = Path.Combine(native, "TriggerZone.shader");
        if (!File.Exists(source)) throw new FileNotFoundException("The trigger-zone shader source is missing.", source);
        var output = Path.GetFullPath("../shader-out"); Directory.CreateDirectory(output);
        File.Copy(source, Path.GetFullPath("Assets/TriggerZone.shader"), true);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>("Assets/TriggerZone.shader") ?? throw new InvalidOperationException("Unity did not import the trigger-zone shader.");
        if (!shader.isSupported) throw new InvalidOperationException("The trigger-zone shader is not supported on this device.");
        var result = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild
        { assetBundleName = "trigger-zone.bundle", assetNames = new[] { "Assets/TriggerZone.shader" } } },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle, BuildTarget.StandaloneWindows64);
        if (result == null) throw new InvalidOperationException("The shader bundle build failed.");
        var bytes = File.ReadAllBytes(Path.Combine(output, "trigger-zone.bundle"));
        string sha;
        using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(source))).Replace("-", "").ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(root, "Mods/ForgeCore/Assets/Shaders/forgevisual-zone-shader.shaderbundle"), bytes);
        File.WriteAllText(Path.Combine(generated, "ZoneShader.Generated.cs"),
            "// Generated from tools/zone-shader/TriggerZone.shader with Unity 2019.4.21f1; shader-build.log records the build.\nnamespace ForgeVisual;\ninternal static class ZoneShaderBytes\n{\n    internal const string SourceSha256 = \"" + sha + "\";\n    internal static byte[] Read() => ForgeCore.Assets.Embedded(\"ForgeCore.Shaders.forgevisual-zone-shader.shaderbundle\");\n}\n");
    }
}
#endif
