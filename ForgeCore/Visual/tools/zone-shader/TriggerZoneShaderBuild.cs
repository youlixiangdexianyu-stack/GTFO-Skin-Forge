#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

/// <summary>Builds the trigger-zone shader (TriggerZone.shader) into a bundle with Unity 2019.4.21f1, writes it to ForgeCore's
/// Assets/Shaders (embedded in ForgeCore.dll) and its reader (Native/ZoneShader.Generated.cs). Run by build_zone_shader.ps1 through
/// ForgeCore's UnityBatch with FORGE_TOOLS (this folder) and FORGE_NATIVE (the Native folder to write into).</summary>
public static class TriggerZoneShaderBuild
{
    public static void Build() => UnityBatch.Run(Run);

    static void Run()
    {
        var tools = UnityBatch.Env("FORGE_TOOLS");
        var generated = UnityBatch.Env("FORGE_NATIVE");
        var project = UnityBatch.Project;
        var source = Path.Combine(tools, "TriggerZone.shader");
        if (!File.Exists(source)) throw new FileNotFoundException("The trigger-zone shader source is missing.", source);
        var output = Path.Combine(project, "shader-out"); Directory.CreateDirectory(output);
        File.Copy(source, Path.Combine(project, "Assets/TriggerZone.shader"), true);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>("Assets/TriggerZone.shader") ?? throw new InvalidOperationException("Unity did not import the trigger-zone shader.");
        if (!shader.isSupported) throw new InvalidOperationException("The trigger-zone shader is not supported on this device.");
        var result = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild
        { assetBundleName = "trigger-zone.bundle", assetNames = new[] { "Assets/TriggerZone.shader" } } },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle, BuildTarget.StandaloneWindows64);
        if (result == null) throw new InvalidOperationException("The shader bundle build failed.");
        var bytes = File.ReadAllBytes(Path.Combine(output, "trigger-zone.bundle"));
        string sha;
        // The source as Git stores its text (CRLF read as LF), so every checkout gives one hash.
        using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(File.ReadAllText(source).Replace("\r\n", "\n")))).Replace("-", "").ToLowerInvariant();
        File.WriteAllBytes(Path.GetFullPath(Path.Combine(generated, "../../Assets/Shaders/forgevisual-zone-shader.shaderbundle")), bytes);
        File.WriteAllText(Path.Combine(generated, "ZoneShader.Generated.cs"),
            "// Generated from tools/zone-shader/TriggerZone.shader with Unity 2019.4.21f1 by tools/zone-shader/build_zone_shader.ps1; do not edit.\nnamespace ForgeVisual;\ninternal static class ZoneShaderBytes\n{\n    internal const string SourceSha256 = \"" + sha + "\";\n    internal static byte[] Read() => ForgeCore.Assets.Embedded(\"ForgeCore.Shaders.forgevisual-zone-shader.shaderbundle\");\n}\n");
        UnityEngine.Debug.Log("SHADER_BUILT TriggerZone " + bytes.Length + " bytes " + sha);
    }
}
#endif
