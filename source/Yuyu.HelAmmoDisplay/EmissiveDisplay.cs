using System.Globalization;
using Gear;
using UnityEngine;

namespace Yuyu.HelAmmoDisplay;

/// <summary>
/// Draws the live magazine count into the weapon part's emissive texture.
/// The digits become part of the gun's material, so they are glued to the model
/// and follow every animation without any 3D placement maths.
/// </summary>
internal static class EmissiveDisplay
{
    private const string FrontMeshName = "Front_UZI_2";
    private const string TextureName = "Yuyu_EmissiveDisplay";

    private static Texture2D displayTexture;
    private static Material appliedMaterial;
    private static string lastText = string.Empty;
    private static float nextCheckTime;
    private static Rect lastRect;
    private static Color32 lastColor;
    private static bool adjustMode;

    internal static void OnWield(BulletWeapon weapon)
    {
        if (!DisplayConfig.EmissiveDisplay.Value || weapon == null)
        {
            return;
        }

        Apply(weapon, true);
    }

    internal static void OnUpdate(BulletWeapon weapon)
    {
        if (!DisplayConfig.EmissiveDisplay.Value || weapon == null)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.F6))
        {
            adjustMode = !adjustMode;
            HelAmmoDisplayPlugin.LogSource.LogInfo(
                adjustMode
                    ? "Emissive display adjust ON: arrows move the UV position, "
                        + "PgUp/PgDn resize, Shift = fine, F6 = exit and save."
                    : "Emissive display adjust OFF. UV position saved.");
            return;
        }

        if (adjustMode && AdjustUvRect())
        {
            return;
        }

        if (Time.unscaledTime < nextCheckTime)
        {
            return;
        }

        nextCheckTime = Time.unscaledTime + 0.2f;
        Apply(weapon, false);
    }

    private static bool AdjustUvRect()
    {
        float step = 0.002f;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
        {
            step *= 0.1f;
        }

        float u = DisplayConfig.EmissiveU.Value;
        float v = DisplayConfig.EmissiveV.Value;
        float width = DisplayConfig.EmissiveWidth.Value;
        float originalU = u;
        float originalV = v;
        float originalWidth = width;

        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            u -= step;
        }

        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            u += step;
        }

        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            v -= step;
        }

        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            v += step;
        }

        if (Input.GetKeyDown(KeyCode.PageUp))
        {
            width += step;
        }

        if (Input.GetKeyDown(KeyCode.PageDown))
        {
            width -= step;
        }

        if (Mathf.Approximately(u, originalU)
            && Mathf.Approximately(v, originalV)
            && Mathf.Approximately(width, originalWidth))
        {
            return false;
        }

        DisplayConfig.EmissiveU.Value = Mathf.Clamp01(u);
        DisplayConfig.EmissiveV.Value = Mathf.Clamp01(v);
        DisplayConfig.EmissiveWidth.Value = Mathf.Clamp(width, 0.005f, 0.5f);
        lastRect = new Rect(0f, 0f, 0f, 0f);
        try
        {
            HelAmmoDisplayPlugin.Instance?.Config.Save();
        }
        catch (Exception exception)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Could not save emissive display UV: {exception.Message}");
        }

        HelAmmoDisplayPlugin.LogSource.LogInfo(
            "Emissive display uv "
            + $"u={FormatFloat(DisplayConfig.EmissiveU.Value)} "
            + $"v={FormatFloat(DisplayConfig.EmissiveV.Value)} "
            + $"width={FormatFloat(DisplayConfig.EmissiveWidth.Value)}");
        return true;
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

            MeshFilter filter = null;
            foreach (MeshFilter candidate in holder.GetComponentsInChildren<MeshFilter>(true))
            {
                if (candidate?.sharedMesh != null
                    && candidate.sharedMesh.name == FrontMeshName)
                {
                    filter = candidate;
                    break;
                }
            }

            if (filter == null)
            {
                return;
            }

            MeshRenderer renderer = filter.GetComponent<MeshRenderer>()
                ?? filter.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null)
            {
                return;
            }

            Material material = renderer.material;
            if (material == null)
            {
                return;
            }

            Rect rect = CurrentRect();
            Color32 color = ParseColor(DisplayConfig.TextColor.Value);
            string text = Mathf.Max(0, weapon.GetCurrentClip())
                .ToString("D2", CultureInfo.InvariantCulture);

            bool needsApply = !verbose
                && (appliedMaterial != material
                    || displayTexture == null
                    || material.GetTexture("_EmissiveMap") != displayTexture
                    || text != lastText
                    || rect != lastRect
                    || !SameColor(color, lastColor));

            if (!needsApply && !verbose)
            {
                return;
            }

            if (displayTexture == null)
            {
                displayTexture = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
                displayTexture.name = TextureName;
                displayTexture.hideFlags = HideFlags.DontSave;
            }

            DrawDigits(displayTexture, rect, text, color);
            displayTexture.Apply(false, false);

            material.SetTexture("_EmissiveMap", displayTexture);
            if (material.HasProperty("_EmissiveColor"))
            {
                material.SetColor("_EmissiveColor", Color.white);
            }

            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", Color.white);
            }

            material.SetFloat("_EnableEmissive", 1f);
            material.EnableKeyword("ENABLE_EMISSIVE");

            if (verbose)
            {
                HelAmmoDisplayPlugin.LogSource.LogInfo(
                    "Emissive display applied: "
                    + $"material='{material.name}' "
                    + $"uv=({FormatFloat(rect.xMin)}, {FormatFloat(rect.yMin)})"
                    + $"-({FormatFloat(rect.xMax)}, {FormatFloat(rect.yMax)}) "
                    + $"clip={text}");
            }

            appliedMaterial = material;
            lastText = text;
            lastRect = rect;
            lastColor = color;
            if (verbose)
            {
                lastText = string.Empty;
            }
        }
        catch (Exception exception)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Emissive display failed: {exception}");
        }
    }

    private static Rect CurrentRect()
    {
        float width = Mathf.Clamp(DisplayConfig.EmissiveWidth.Value, 0.005f, 0.5f);
        float height = Mathf.Clamp(
            DisplayConfig.EmissiveWidth.Value
                * (DisplayConfig.PanelHeight.Value / Mathf.Max(0.001f, DisplayConfig.PanelWidth.Value)),
            0.002f,
            0.5f);
        float centerU = DisplayConfig.EmissiveU.Value;
        float centerV = DisplayConfig.EmissiveV.Value;
        return new Rect(
            centerU - width * 0.5f,
            centerV - height * 0.5f,
            width,
            height);
    }

    private static void DrawDigits(
        Texture2D texture,
        Rect uvRect,
        string text,
        Color32 color)
    {
        int width = texture.width;
        int height = texture.height;
        var pixels = new Color32[width * height];
        var clear = new Color32(0, 0, 0, 0);
        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = clear;
        }

        int x0 = Mathf.Clamp(Mathf.RoundToInt(uvRect.xMin * width), 0, width - 1);
        int x1 = Mathf.Clamp(Mathf.RoundToInt(uvRect.xMax * width), 0, width - 1);
        int y0 = Mathf.Clamp(Mathf.RoundToInt(uvRect.yMin * height), 0, height - 1);
        int y1 = Mathf.Clamp(Mathf.RoundToInt(uvRect.yMax * height), 0, height - 1);
        int rectWidth = Mathf.Max(1, x1 - x0);
        int rectHeight = Mathf.Max(1, y1 - y0);

        int digitCount = text.Length;
        int digitHeight = Mathf.RoundToInt(rectHeight * 0.86f);
        int digitWidth = Mathf.RoundToInt(digitHeight * 0.62f);
        int gap = Mathf.Max(2, Mathf.RoundToInt(digitWidth * 0.35f));
        int totalWidth = digitCount * digitWidth + (digitCount - 1) * gap;
        int startX = x0 + Mathf.Max(0, (rectWidth - totalWidth) / 2);
        int startY = y0 + Mathf.Max(0, (rectHeight - digitHeight) / 2);
        int thickness = Mathf.Max(2, Mathf.RoundToInt(digitHeight * 0.16f));

        for (int index = 0; index < digitCount; index++)
        {
            int digit = text[index] - '0';
            if (digit < 0 || digit > 9)
            {
                continue;
            }

            int mask = SegmentMasks[digit];
            int baseX = startX + index * (digitWidth + gap);
            int halfW = digitWidth / 2;
            int halfH = digitHeight / 2;
            int quarterH = digitHeight / 4;
            int[] segmentOffsetsX = { 0, halfW, halfW, 0, -halfW, -halfW, 0 };
            int[] segmentOffsetsY = { halfH, quarterH, -quarterH, -halfH, -quarterH, quarterH, 0 };
            for (int segment = 0; segment < 7; segment++)
            {
                if ((mask & (1 << segment)) == 0)
                {
                    continue;
                }

                bool horizontal = segment == 0 || segment == 3 || segment == 6;
                int segmentWidth = horizontal ? digitWidth : thickness;
                int segmentHeight = horizontal ? thickness : digitHeight / 2 + thickness;
                int centerX = baseX + halfW + segmentOffsetsX[segment];
                int centerY = startY + halfH + segmentOffsetsY[segment];
                int left = centerX - segmentWidth / 2;
                int bottom = centerY - segmentHeight / 2;
                for (int y = bottom; y < bottom + segmentHeight; y++)
                {
                    if (y < 0 || y >= height)
                    {
                        continue;
                    }

                    for (int x = left; x < left + segmentWidth; x++)
                    {
                        if (x < 0 || x >= width)
                        {
                            continue;
                        }

                        pixels[y * width + x] = color;
                    }
                }
            }
        }

        texture.SetPixels32(pixels);
    }

    private static readonly int[] SegmentMasks =
    {
        0b0111111,
        0b0000110,
        0b1011011,
        0b1001111,
        0b1100110,
        0b1101101,
        0b1111101,
        0b0000111,
        0b1111111,
        0b1101111
    };

    private static bool SameColor(Color32 a, Color32 b)
    {
        return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    }

    private static Color32 ParseColor(string html)
    {
        if (!string.IsNullOrWhiteSpace(html)
            && ColorUtility.TryParseHtmlString(html.Trim(), out Color color))
        {
            return color;
        }

        return new Color32(185, 251, 255, 255);
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
