#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>Imports the kit's fonts (FORGE_FONTS: TTF paths separated by ';') as dynamic Unity fonts with their data included, builds
/// them into one bundle with Unity 2019.4.21f1 and writes it to FORGE_OUT/ForgeUI-fonts.bundle. Run by build_fonts.ps1. The TextMesh
/// Pro font assets are made from these fonts in the game, by the game's own TextMesh Pro, so no TextMesh Pro asset is serialized here.</summary>
public static class FontBundleBuild
{
    const string Bundle = "ForgeUI-fonts.bundle";

    public static void Build()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Run()
    {
        if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
        var fonts = (Environment.GetEnvironmentVariable("FORGE_FONTS") ?? throw new InvalidOperationException("FORGE_FONTS is not set.")).Split(';');
        var output = Environment.GetEnvironmentVariable("FORGE_OUT") ?? throw new InvalidOperationException("FORGE_OUT is not set.");
        var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        Directory.CreateDirectory(Path.Combine(project, "Assets/Fonts"));
        var assets = new string[fonts.Length];
        for (var i = 0; i < fonts.Length; i++)
        {
            var asset = "Assets/Fonts/" + Path.GetFileName(fonts[i]);
            File.Copy(fonts[i], Path.Combine(project, asset), true);
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TrueTypeFontImporter)AssetImporter.GetAtPath(asset);
            importer.fontTextureCase = FontTextureCase.Dynamic;
            importer.includeFontData = true;
            importer.SaveAndReimport();
            if (AssetDatabase.LoadAssetAtPath<Font>(asset) == null) throw new InvalidOperationException("Unity did not import " + asset + ".");
            assets[i] = asset;
        }
        var work = Path.Combine(project, "bundle-out");
        Directory.CreateDirectory(work);
        var result = BuildPipeline.BuildAssetBundles(work, new[] { new AssetBundleBuild { assetBundleName = Bundle, assetNames = assets } },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);
        if (result == null) throw new InvalidOperationException("The font bundle build failed.");
        Directory.CreateDirectory(output);
        File.Copy(Path.Combine(work, Bundle), Path.Combine(output, Bundle), true);
        Debug.Log("FONTS_BUILT " + new FileInfo(Path.Combine(output, Bundle)).Length + " bytes: " + string.Join(", ", assets));
    }
}
#endif
