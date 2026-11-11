using System.Globalization;
using System.Text;
using Gear;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Yuyu.HelAmmoDisplay;

/// <summary>
/// Test-only helper: paints a marker into the weapon part's emissive texture so
/// the display can be attached to the gun through the skin material instead of a
/// floating 3D object. Used to verify the UV region that maps onto the slide.
/// </summary>
internal static class EmissiveTest
{
    private const string FrontMeshName = "Front_UZI_2";
    private const string TextureName = "Yuyu_EmissiveTest";

    private static Texture2D markerTexture;
    private static Material appliedMaterial;
    private static float nextApplyTime;

    internal static void OnWield(BulletWeapon weapon)
    {
        if (weapon == null || !DisplayConfig.EmissiveTest.Value)
        {
            return;
        }

        Apply(weapon, true);
    }

    internal static void OnUpdate(BulletWeapon weapon)
    {
        if (weapon == null || !DisplayConfig.EmissiveTest.Value)
        {
            return;
        }

        if (Time.unscaledTime < nextApplyTime)
        {
            return;
        }

        nextApplyTime = Time.unscaledTime + 1f;
        Apply(weapon, false);
    }

    private static void Apply(BulletWeapon weapon, bool verbose)
    {
        try
        {
            if (!weapon.m_isFirstPerson)
            {
                return;
            }

            var archetype = weapon.ArchetypeData;
            if (archetype == null || archetype.persistentID != 66)
            {
                return;
            }

            Transform holder = weapon.m_itemModelHolder;
            if (holder == null)
            {
                return;
            }

            MeshFilter filter = FindFrontMesh(holder);
            if (filter?.sharedMesh == null)
            {
                if (verbose)
                {
                    HelAmmoDisplayPlugin.LogSource.LogWarning(
                        $"Emissive test: '{FrontMeshName}' mesh not found.");
                }

                return;
            }

            MeshRenderer renderer = filter.GetComponent<MeshRenderer>()
                ?? filter.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null)
            {
                if (verbose)
                {
                    HelAmmoDisplayPlugin.LogSource.LogWarning(
                        "Emissive test: front mesh has no MeshRenderer.");
                }

                return;
            }

            Material material = renderer.material;
            if (material == null)
            {
                return;
            }

            if (!verbose
                && appliedMaterial == material
                && markerTexture != null
                && material.GetTexture("_EmissiveMap") == markerTexture)
            {
                return;
            }

            if (verbose)
            {
                LogMaterial(material);
            }

            Rect uvRect = ComputeTopUvRect(filter, holder);
            markerTexture = BuildMarkerTexture(material, uvRect);
            appliedMaterial = material;

            material.SetTexture("_EmissiveMap", markerTexture);
            if (material.HasProperty("_EmissionMap"))
            {
                material.SetTexture("_EmissionMap", markerTexture);
            }

            material.SetFloat("_EnableEmissive", 1f);
            if (material.HasProperty("_EmissiveColor"))
            {
                material.SetColor("_EmissiveColor", Color.white);
            }

            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", Color.white);
            }

            material.EnableKeyword("ENABLE_EMISSIVE");
            material.renderQueue = 2100;

            HelAmmoDisplayPlugin.LogSource.LogInfo(
                "Emissive test applied: "
                + $"material='{material.name}' shader='{material.shader?.name}' "
                + $"texture={markerTexture.width}x{markerTexture.height} "
                + $"uvRect=({FormatFloat(uvRect.xMin)}, {FormatFloat(uvRect.yMin)})"
                + $"-({FormatFloat(uvRect.xMax)}, {FormatFloat(uvRect.yMax)})");
        }
        catch (Exception exception)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Emissive test failed: {exception}");
        }
    }

    private static MeshFilter FindFrontMesh(Transform holder)
    {
        foreach (MeshFilter filter in holder.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter?.sharedMesh != null
                && filter.sharedMesh.name == FrontMeshName)
            {
                return filter;
            }
        }

        return null;
    }

    private static void LogMaterial(Material material)
    {
        var text = new StringBuilder();
        text.AppendLine(
            $"Emissive test material '{material.name}' shader='{material.shader?.name}'");
        try
        {
            var shader = material.shader;
            int count = shader != null ? shader.GetPropertyCount() : 0;
            for (int index = 0; index < count && index < 60; index++)
            {
                string name = shader.GetPropertyName(index);
                var type = shader.GetPropertyType(index);
                string extra = string.Empty;
                if (type == UnityEngine.Rendering.ShaderPropertyType.Texture)
                {
                    Texture texture = material.GetTexture(name);
                    extra = texture != null
                        ? $" tex={texture.name} {texture.width}x{texture.height}"
                        : " tex=<null>";
                }
                else if (type == UnityEngine.Rendering.ShaderPropertyType.Float
                    || type == UnityEngine.Rendering.ShaderPropertyType.Range)
                {
                    extra = $" = {FormatFloat(material.GetFloat(name))}";
                }

                text.AppendLine($"  {type} '{name}'{extra}");
            }
        }
        catch (Exception exception)
        {
            text.AppendLine($"  (property dump failed: {exception.Message})");
        }

        HelAmmoDisplayPlugin.LogSource.LogInfo(text.ToString());
    }

    private static Rect ComputeTopUvRect(MeshFilter filter, Transform holder)
    {
        Mesh mesh = filter.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        Vector2[] uvs = mesh.uv;
        Transform meshTransform = filter.transform;

        Bounds modelBounds = new Bounds();
        bool first = true;
        var modelPoints = new Vector3[vertices.Length];
        for (int index = 0; index < vertices.Length; index++)
        {
            Vector3 point = holder.InverseTransformPoint(
                meshTransform.TransformPoint(vertices[index]));
            modelPoints[index] = point;
            if (first)
            {
                modelBounds = new Bounds(point, Vector3.zero);
                first = false;
            }
            else
            {
                modelBounds.Encapsulate(point);
            }
        }

        Vector3 center = modelBounds.center;
        float top = modelBounds.max.y;
        float uMin = 1f;
        float uMax = 0f;
        float vMin = 1f;
        float vMax = 0f;
        int selected = 0;
        for (int index = 0; index < vertices.Length; index++)
        {
            Vector3 point = modelPoints[index];
            if (Mathf.Abs(point.x - center.x) > 0.02f
                || Mathf.Abs(point.z - center.z) > 0.03f
                || point.y < top - 0.02f)
            {
                continue;
            }

            Vector2 uv = uvs[index];
            uMin = Mathf.Min(uMin, uv.x);
            uMax = Mathf.Max(uMax, uv.x);
            vMin = Mathf.Min(vMin, uv.y);
            vMax = Mathf.Max(vMax, uv.y);
            selected++;
        }

        if (selected < 3 || uMax - uMin < 0.0005f || vMax - vMin < 0.0005f)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Emissive test: UV selection degenerate (verts={selected}), "
                + "falling back to the texture centre.");
            return new Rect(0.45f, 0.45f, 0.1f, 0.1f);
        }

        var rect = new Rect(
            Mathf.Clamp01(uMin),
            Mathf.Clamp01(vMin),
            Mathf.Clamp01(uMax - uMin),
            Mathf.Clamp01(vMax - vMin));
        HelAmmoDisplayPlugin.LogSource.LogInfo(
            $"Emissive test UV selection: verts={selected} "
            + $"modelCenter={FormatVector(center)} top={FormatFloat(top)}");
        return rect;
    }

    private static Texture2D BuildMarkerTexture(Material material, Rect uvRect)
    {
        Texture source = material.GetTexture("_EmissiveMap");
        int width = 1024;
        int height = 1024;
        if (source is Texture2D sourceTexture)
        {
            width = Mathf.Clamp(sourceTexture.width, 256, 2048);
            height = Mathf.Clamp(sourceTexture.height, 256, 2048);
        }

        var pixels = new Color32[width * height];
        var black = new Color32(0, 0, 0, 255);
        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = black;
        }

        var cyan = new Color32(0, 255, 255, 255);
        var magenta = new Color32(255, 0, 255, 255);
        int x0 = Mathf.Clamp(Mathf.RoundToInt(uvRect.xMin * width), 0, width - 1);
        int x1 = Mathf.Clamp(Mathf.RoundToInt(uvRect.xMax * width), 0, width - 1);
        int y0 = Mathf.Clamp(Mathf.RoundToInt(uvRect.yMin * height), 0, height - 1);
        int y1 = Mathf.Clamp(Mathf.RoundToInt(uvRect.yMax * height), 0, height - 1);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                pixels[y * width + x] = cyan;
            }
        }

        // A magenta cross at the texture centre as a reference point.
        int cx = width / 2;
        int cy = height / 2;
        for (int offset = -40; offset <= 40; offset++)
        {
            int x = cx + offset;
            int y = cy + offset;
            if (x >= 0 && x < width)
            {
                pixels[cy * width + x] = magenta;
            }

            if (y >= 0 && y < height)
            {
                pixels[y * width + cx] = magenta;
            }
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = TextureName;
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.DontSave;
        return texture;
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatVector(Vector3 value)
    {
        return "("
            + $"{FormatFloat(value.x)}, {FormatFloat(value.y)}, {FormatFloat(value.z)})";
    }
}
