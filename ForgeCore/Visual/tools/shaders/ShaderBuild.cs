#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>Builds ForgeVisual's three shaders (particle, marker, outline) into one small bundle with Unity 2019.4.21f1, writes it to ForgeCore's Assets/Shaders
/// (embedded in ForgeCore.dll) and its reader (Native/ShaderBundle.Generated.cs). Run by build_shaders.ps1 with FORGE_TOOLS (this folder) and FORGE_NATIVE (the Native folder).</summary>
public static class ForgeVisualShaderBuild
{
    const string BundleName = "forgevisual-shaders";
    static readonly string[] Sources = { "ForgeVisualParticle.shader", "ForgeVisualMarker.shader", "ForgeVisualOutline.shader" };

    public static void Build()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is not set.");

    static void Run()
    {
        if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
        var tools = Env("FORGE_TOOLS");
        var native = Env("FORGE_NATIVE");
        var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var assets = new string[Sources.Length];
        var all = new StringBuilder();
        for (var i = 0; i < Sources.Length; i++)
        {
            var source = Path.Combine(tools, Sources[i]);
            assets[i] = "Assets/" + Sources[i];
            File.Copy(source, Path.Combine(project, assets[i]), true);
            all.Append(File.ReadAllText(source).Replace("\r\n", "\n"));
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (var asset in assets)
        {
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(asset) ?? throw new InvalidOperationException("Unity did not import " + asset + ".");
            if (!shader.isSupported) throw new InvalidOperationException(asset + " is not supported on this device.");
        }
        var output = Path.Combine(project, "shader-out");
        Directory.CreateDirectory(output);
        var result = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets } },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle, BuildTarget.StandaloneWindows64);
        if (result == null) throw new InvalidOperationException("The shader bundle build failed.");
        var bytes = File.ReadAllBytes(Path.Combine(output, BundleName));
        string sha;
        using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(all.ToString()))).Replace("-", "").ToLowerInvariant();
        File.WriteAllBytes(Path.GetFullPath(Path.Combine(native, "../../Assets/Shaders", BundleName + ".shaderbundle")), bytes);
        File.WriteAllText(Path.Combine(native, "ShaderBundle.Generated.cs"),
            "// Generated from tools/shaders/*.shader with Unity 2019.4.21f1 by tools/shaders/build_shaders.ps1; do not edit.\nnamespace ForgeVisual;\n\n"
            + "internal static class ShaderBundle\n{\n    internal const string SourceSha256 = \"" + sha + "\";\n"
            + "    internal const string ParticleAsset = \"" + assets[0] + "\";\n    internal const string MarkerAsset = \"" + assets[1] + "\";\n"
            + "    internal const string OutlineAsset = \"" + assets[2] + "\";\n"
            + "    internal static byte[] Read() => ForgeCore.Assets.Embedded(\"ForgeCore.Shaders." + BundleName + ".shaderbundle\");\n}\n");
        Debug.Log("SHADER_BUILT " + bytes.Length + " bytes " + sha);
    }
}
#endif
