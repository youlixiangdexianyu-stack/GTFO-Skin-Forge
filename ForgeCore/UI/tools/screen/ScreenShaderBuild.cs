#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

/// <summary>Builds ForgeUI's screen shaders (the power-on panel and the screen effect stack) into bundles with Unity
/// 2019.4.21f1, writes them to ForgeCore's Assets/Shaders (embedded in ForgeCore.dll) and their readers (Native/PowerOnShader.Generated.cs
/// and Native/ScreenEffectsShader.Generated.cs). Run by build_screen_shaders.ps1 with
/// FORGE_TOOLS (this folder) and FORGE_NATIVE (the Native folder to write into).</summary>
public static class ScreenShaderBuild
{
    static readonly (string Name, string Class, string File, string Bundle)[] Shaders =
    {
        ("ForgeUIPowerOn", "PowerOnShaderBytes", "PowerOnShader.Generated.cs", "forgeui-power-on"),
        ("ForgeUIScreenEffects", "ScreenEffectsShaderBytes", "ScreenEffectsShader.Generated.cs", "forgeui-screen-effects"),
    };

    public static void Build()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Run()
    {
        if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
        var tools = Environment.GetEnvironmentVariable("FORGE_TOOLS") ?? throw new InvalidOperationException("FORGE_TOOLS is not set.");
        var native = Environment.GetEnvironmentVariable("FORGE_NATIVE") ?? throw new InvalidOperationException("FORGE_NATIVE is not set.");
        var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var output = Path.Combine(project, "shader-out");
        Directory.CreateDirectory(output);
        var shaders = Path.GetFullPath(Path.Combine(native, "../../Assets/Shaders"));
        foreach (var (name, cls, file, embedded) in Shaders)
        {
            var source = Path.Combine(tools, name + ".shader");
            var asset = "Assets/" + name + ".shader";
            File.Copy(source, Path.Combine(project, asset), true);
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(asset) ?? throw new InvalidOperationException("Unity did not import " + name + ".");
            if (!shader.isSupported) throw new InvalidOperationException(name + " is not supported on this device.");
            var bundleName = name.ToLowerInvariant() + ".bundle";
            var result = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = bundleName, assetNames = new[] { asset } } },
                BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle, BuildTarget.StandaloneWindows64);
            if (result == null) throw new InvalidOperationException("The bundle build of " + name + " failed.");
            var bytes = File.ReadAllBytes(Path.Combine(output, bundleName));
            string sha;
            using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(source))).Replace("-", "").ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(shaders, embedded + ".shaderbundle"), bytes);
            File.WriteAllText(Path.Combine(native, file),
                "// Generated from tools/screen/" + name + ".shader with Unity 2019.4.21f1 by tools/screen/build_screen_shaders.ps1; do not edit.\nnamespace ForgeUI;\n\ninternal static class " + cls + "\n{\n    internal const string SourceSha256 = \"" + sha + "\";\n    internal const string AssetName = \"" + asset + "\";\n    internal static byte[] Read() => ForgeCore.Assets.Embedded(\"ForgeCore.Shaders." + embedded + ".shaderbundle\");\n}\n");
            Debug.Log("SHADER_BUILT " + name + " " + bytes.Length + " bytes " + sha);
        }
    }
}
#endif
