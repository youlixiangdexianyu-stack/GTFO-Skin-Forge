using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// What the visual part makes ready once the elevator has stopped, so no first use pays for it in a fight: the shared surface marks, the
/// two effect bundles, and one off-screen draw of each of the library's shaders. Loading a shader does not create its GPU programs; only a
/// draw does, on the render thread, and the main thread then waits for it at the next present: on 10-28 the frame right after the first
/// beacon marker was drawn waited 119 ms there. A temporary camera renders the library's unit quad with every material into a 16 x 16
/// texture and is destroyed; nothing is shown. Each is a step of ForgeCore <c>LandingWork</c>, one per frame, so none shares a frame with
/// another warm-up.
/// </summary>
internal static class ShaderWarm
{
    private const int Layer = 31;

    internal static void Queue()
    {
        ForgeCore.LandingWork.Add(Plugin.Id, "decal-marks", Decals.Prepare);
        ForgeCore.LandingWork.Add(Plugin.Id, "forge-effects", EffectBundle.Load);
        ForgeCore.LandingWork.Add(Plugin.Id, "shared-blast", HeavyBlast.Load);
        ForgeCore.LandingWork.Add(Plugin.Id, "shaders", Draw);
    }

    private static void Draw()
    {
        GameObject? holder = null;
        RenderTexture? target = null;
        Material? additive = null, alpha = null;
        try
        {
            var marker = Shaders.Marker();
            additive = Shaders.Make(Texture2D.whiteTexture, FxBlend.Additive, "ForgeVisual warm additive");
            alpha = Shaders.Make(Texture2D.whiteTexture, FxBlend.Alpha, "ForgeVisual warm alpha");
            if (marker == null || additive == null || alpha == null) return;
            holder = new GameObject("ForgeVisual shader warm") { hideFlags = HideFlags.HideAndDontSave };
            var camera = holder.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;
            camera.cullingMask = 1 << Layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.useOcclusionCulling = false;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            holder.transform.position = new Vector3(0f, -10000f, 0f);
            target = RenderTexture.GetTemporary(16, 16, 16);
            camera.targetTexture = target;
            var quad = Shaders.Quad();
            var at = Matrix4x4.TRS(holder.transform.position + new Vector3(0f, 0f, 2f), Quaternion.Euler(0f, 180f, 0f), Vector3.one);
            foreach (var material in new[] { marker, additive, alpha })
                Graphics.DrawMesh(quad, at, material, Layer, camera);
            camera.Render();
            camera.targetTexture = null;
        }
        catch (Exception error) { Plugin.Logger.Error("visual-shaders-could-not-be-drawn-ahead", "ForgeVisual shaders could not be drawn ahead of use (their first real draw pays for it): " + error); }
        finally
        {
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (holder != null) Object.Destroy(holder);
            if (additive != null) Object.Destroy(additive);
            if (alpha != null) Object.Destroy(alpha);
        }
    }
}
