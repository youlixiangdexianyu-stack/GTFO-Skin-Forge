#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>The batch-mode frame every Forge bundle build shares (ForgeCore's fonts and screen shaders, ForgeOrdnance's Carrier
/// and Arsenal bundles): the game's Unity version, the editor's exit code, and the build's settings read from the environment.
/// UnityBatch.ps1 copies it into the project's Assets/Editor beside the build script. Unity 2019.4 compiles it as C# 7.3.</summary>
public static class UnityBatch
{
    /// <summary>Runs one build in the game's Unity 2019.4.21f1 and exits the editor with 0, or logs the failure and exits with 1.</summary>
    public static void Run(Action build)
    {
        try
        {
            if (Application.unityVersion != "2019.4.21f1") throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
            build();
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    /// <summary>A setting the calling script passes in the environment; an unset or blank one stops the build.</summary>
    public static string Env(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(variable + " is not set.");
        return value;
    }

    /// <summary>The Unity project folder the build runs in.</summary>
    public static string Project => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}
#endif
