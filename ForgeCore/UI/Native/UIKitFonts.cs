using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// The kit's own fonts. Unity fonts are read from <c>fonts/ForgeUI-fonts.bundle</c> in the kit's folder (built by
/// <c>tools/fonts/build_fonts.ps1</c>); the TextMesh Pro font assets are made from them here by the game's own TextMesh Pro, with
/// dynamic atlases filled as characters are first drawn, and a material copied from the game's HUD text so they draw the same way.
/// Each chain falls back to the next: Rajdhani, then ForgeUI Symbols (the kit's own ∞, which neither Rajdhani nor Glow Sans has), then
/// Glow Sans, then the game's font (and its own fallbacks). Without the bundle the game's font is used and one line is logged.
/// </summary>
internal static class UIKitFonts
{
    internal sealed class Set
    {
        internal TMP_FontAsset Regular = null!, Bold = null!;
    }

    private const int Sampling = 48, Padding = 5, Atlas = 1024;
    /// <summary>By the game font's address; the entry keeps that font so an address a destroyed font left behind is not taken for it.</summary>
    private static readonly Dictionary<IntPtr, (TMP_FontAsset Game, Set? Set)> ByGameFont = new();
    private static Font? _latin, _latinBold, _symbols, _symbolsBold, _chinese, _chineseBold;
    private static bool _loaded, _missing;

    /// <summary>The kit's faces falling back to <paramref name="game"/>, drawn like <paramref name="material"/>; null when the fonts are
    /// not there (logged once).</summary>
    internal static Set? For(TMP_FontAsset game, Material material)
    {
        if (ByGameFont.TryGetValue(game.Pointer, out var known) && known.Game != null
            && (known.Set == null || known.Set.Regular != null && known.Set.Bold != null)) return known.Set;
        Set? set = null;
        try
        {
            if (Load())
            {
                var chinese = Make(_chinese!, material, game, "Chinese");
                var chineseBold = Make(_chineseBold!, material, game, "ChineseBold");
                var symbols = Make(_symbols!, material, chinese, "Symbols");
                var symbolsBold = Make(_symbolsBold!, material, chineseBold, "SymbolsBold");
                set = new Set
                {
                    Regular = Make(_latin!, material, symbols, "Latin"),
                    Bold = Make(_latinBold!, material, symbolsBold, "LatinBold"),
                };
            }
        }
        catch (Exception error)
        {
            set = null;
            Plugin.Error("ui-kit-fonts-could-not-be-made", "Kit fonts could not be made; the game's font is used: " + error);
        }
        ByGameFont[game.Pointer] = (game, set);
        return set;
    }

    private static bool Load()
    {
        if (_loaded) return !_missing;
        _loaded = true;
        var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "fonts", "ForgeUI-fonts.bundle");
        if (!File.Exists(path))
        {
            _missing = true;
            Plugin.Error("ui-no-text-that-asks-kits-font", "No " + path + "; text that asks for the kit's font uses the game's font.");
            return false;
        }
        var bundle = ForgeCore.Assets.Load(Plugin.Id, path) ?? throw new InvalidOperationException("the font bundle cannot be opened");
        _latin = Font(bundle, "Rajdhani-Medium.ttf");
        _latinBold = Font(bundle, "Rajdhani-SemiBold.ttf");
        _symbols = Font(bundle, "ForgeUISymbols-Medium.ttf");
        _symbolsBold = Font(bundle, "ForgeUISymbols-SemiBold.ttf");
        _chinese = Font(bundle, "GlowSansSC-Condensed-Medium-common.ttf");
        _chineseBold = Font(bundle, "GlowSansSC-Condensed-Bold-common.ttf");
        return true;
    }

    private static Font Font(AssetBundle bundle, string file)
    {
        var font = ForgeCore.Assets.Get<Font>(Plugin.Id, bundle, "Assets/Fonts/" + file) ?? throw new InvalidOperationException("the font bundle has no " + file);
        font.hideFlags = HideFlags.HideAndDontSave;
        return font;
    }

    /// <summary>A dynamic font asset of <paramref name="font"/> whose material copies the game's HUD text material, falling back to
    /// <paramref name="fallback"/>.</summary>
    private static TMP_FontAsset Make(Font font, Material material, TMP_FontAsset fallback, string name)
    {
        var asset = TMP_FontAsset.CreateFontAsset(font, Sampling, Padding, GlyphRenderMode.SDFAA, Atlas, Atlas, AtlasPopulationMode.Dynamic, true)
                    ?? throw new InvalidOperationException("TextMesh Pro could not make a font asset of " + font.name);
        asset.name = "ForgeUI." + name;
        asset.hideFlags = HideFlags.HideAndDontSave;
        var atlas = asset.atlasTexture;
        var copy = new Material(material) { name = "ForgeUI." + name + ".Material", hideFlags = HideFlags.HideAndDontSave };
        copy.SetTexture("_MainTex", atlas);
        copy.SetFloat("_TextureWidth", atlas.width);
        copy.SetFloat("_TextureHeight", atlas.height);
        copy.SetFloat("_GradientScale", Padding + 1);
        var generated = asset.material;
        asset.material = copy;
        if (generated != null && !generated.WasCollected) Object.Destroy(generated);
        var fallbacks = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
        fallbacks.Add(fallback);
        asset.fallbackFontAssetTable = fallbacks;
        return asset;
    }
}
