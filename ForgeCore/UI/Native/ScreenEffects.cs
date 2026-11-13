using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>One effect on the screen effect stack (<see cref="UIKitMod.PushScreenEffect"/>). <c>new ScreenEffectSpec()</c>
/// is a white effect at full strength lasting 0.3 seconds with a 0.25 second fade-out.</summary>
public struct ScreenEffectSpec
{
    public ScreenEffectSpec()
    {
        Strength = 1f;
        Colour = Color.white;
        Seconds = 0.3f;
        FadeIn = 0f;
        FadeOut = 0.25f;
        Priority = 0;
        Rate = 1.2f;
    }

    /// <summary>0..1: how strong the effect is at its peak (for blur and colour split a share of their full width).</summary>
    public float Strength;
    /// <summary>The colour (flash, vignette, low health); its alpha scales the opacity. Ignored by blur and colour split.</summary>
    public Color Colour;
    /// <summary>How long it holds after fading in; 0 holds it until <see cref="ScreenEffect.Withdraw"/>.</summary>
    public float Seconds;
    /// <summary>Seconds to rise from nothing to <see cref="Strength"/>.</summary>
    public float FadeIn;
    /// <summary>Seconds to fade out once its time is up or it is withdrawn; 0 ends it at once.</summary>
    public float FadeOut;
    /// <summary>Against other effects on the channel: only the highest priority present counts.</summary>
    public int Priority;
    /// <summary>Low health only: pulses per second (0 holds it steady).</summary>
    public float Rate;
}

/// <summary>A handle to a pushed screen effect. A default handle (a refused push) is inactive and ignores every call.</summary>
public readonly struct ScreenEffect
{
    internal readonly int Slot, Generation;
    internal ScreenEffect(int slot, int generation) { Slot = slot; Generation = generation + 1; }

    /// <summary>True while the effect is held or fading out.</summary>
    public bool Active => Generation > 0 && ScreenStack.Live(this);

    /// <summary>Starts the fade-out now (immediately when the effect's fade-out is 0).</summary>
    public void Withdraw() { if (Generation > 0) ScreenStack.Withdraw(this); }

    /// <summary>Changes the strength and colour of an effect still holding (one driven every frame, such as a visor shade); false when it
    /// ended or is fading out.</summary>
    public bool Set(float strength, Color colour) => Generation > 0 && ScreenStack.Set(this, strength, colour);
}

/// <summary>
/// The screen effect stack: the merged channels (<see cref="ScreenEffectBook"/>), the hit-edge flashes of every <see cref="HitEdge"/>
/// (the six strongest across all of them) and the parts of the shown <see cref="Visor"/> that are effects (its heat glow, scanline
/// interference and boot sweep; its shade is a vignette effect) drawn by one full-screen quad on the screen layer with
/// <c>ForgeUI/ScreenEffects</c> in one pass, after every Update (LateUpdate), so an effect set this frame shows this frame. The screen is
/// grabbed only while blur or colour split is on. The shader and its textures are made and drawn once during the elevator ride or by
/// <see cref="UIKitMod.WarmNextLayer"/>. A frame allocates nothing and looks nothing up by name.
/// </summary>
internal static class ScreenStack
{
    private const int Bands = EdgeFlashes.Slots, Queue = 3902, LodGrab = 200, LodLayers = 100;
    private const string BuiltIn = "ForgeUI.ScreenEffects";
    private static readonly ScreenEffectBook Book = new();
    private static readonly List<HitEdge> Edges = new();
    private static readonly ScreenLayer.User User = new();
    private static readonly float[] BandAlpha = new float[Bands], BandAzimuth = new float[Bands];
    private static readonly int[] BandIds = new int[Bands];
    private static readonly Vector4[] Shown = new Vector4[16];
    private static readonly int FlashId = Shader.PropertyToID("_Flash"), VignetteId = Shader.PropertyToID("_Vignette"),
        LowHealthId = Shader.PropertyToID("_LowHealth"), HeatId = Shader.PropertyToID("_Heat"), InterferenceId = Shader.PropertyToID("_Interference"),
        SweepId = Shader.PropertyToID("_Sweep"), LayoutId = Shader.PropertyToID("_Layout"),
        SweepAtId = Shader.PropertyToID("_SweepAt"), BandSizeId = Shader.PropertyToID("_BandSize"), BandTexId = Shader.PropertyToID("_BandTex"),
        InterferenceTexId = Shader.PropertyToID("_InterferenceTex");
    private static Shader? _shader;
    private static Material? _material;
    private static GameObject? _piece;
    private static Texture2D? _bandTexture, _interferenceTexture;
    private static int _generation = -1, _warmedGeneration = -1, _lod = -1, _bandDepth;
    private static bool _failed, _shaderTried, _visorShown;
    /// <summary>A mod has pushed an effect, shown a visor or added a hit edge this session: only then is the pass warmed at a landing.</summary>
    private static bool _used;
    private static Color _heat, _interference, _sweep;
    private static float _interferenceShift, _sweepAt;

    static ScreenStack()
    {
        for (int i = 0; i < Bands; i++) BandIds[i] = Shader.PropertyToID("_Band" + i);
    }

    internal static void AddEdge(HitEdge edge) { _used = true; Edges.Add(edge); }

    internal static ScreenEffect Push(string mod, ScreenChannel channel, in ScreenEffectSpec spec)
    {
        var c = spec.Colour;
        if (ScreenEffectBook.Problem(channel, spec.Strength, c.r, c.g, c.b, c.a, spec.Seconds, spec.FadeIn, spec.FadeOut, spec.Rate) is { } problem)
        {
            Plugin.Error("ui-screen-effect-refused", mod + ": screen effect refused: " + problem + ".");
            return default;
        }
        _used = true;
        var slot = Book.Push(channel, spec.Strength, c.r, c.g, c.b, c.a, spec.Seconds, spec.FadeIn, spec.FadeOut, spec.Priority, spec.Rate, ForgeCore.Frame.GameTime);
        if (slot < 0) return default;
        return new ScreenEffect(slot, Book.Generation(slot));
    }

    /// <summary>The effects pushed through <see cref="UIKitMod.PushScreenEffect"/> (the visor's own shade and the hit edges are not here): they end with the level.</summary>
    private static readonly List<ScreenEffect> Owned = new();

    internal static ScreenEffect PushOwned(string mod, ScreenChannel channel, in ScreenEffectSpec spec)
    {
        var effect = Push(mod, channel, spec);
        if (!effect.Active) return effect;
        for (int i = Owned.Count - 1; i >= 0; i--) if (!Owned[i].Active) Owned.RemoveAt(i);
        Owned.Add(effect);
        return effect;
    }

    /// <summary>Level over: every effect a mod pushed ends at once, however long its hold or fade was.</summary>
    internal static void LevelEnded()
    {
        for (int i = 0; i < Owned.Count; i++) Book.Drop(Owned[i].Slot, Owned[i].Generation - 1);
        Owned.Clear();
    }

    internal static bool Live(in ScreenEffect effect) => Book.Live(effect.Slot, effect.Generation - 1);

    internal static void Withdraw(in ScreenEffect effect) => Book.Withdraw(effect.Slot, effect.Generation - 1, ForgeCore.Frame.GameTime);

    internal static bool Set(in ScreenEffect effect, float strength, Color colour)
        => Book.Set(effect.Slot, effect.Generation - 1, strength, colour.r, colour.g, colour.b, colour.a);

    /// <summary>The shown visor's effect parts: its heat glow, scanline interference (shifted by <paramref name="shift"/> screen heights)
    /// and boot sweep (its line <paramref name="sweepAt"/> screen heights from the middle). The visor shown last wins.</summary>
    internal static void Visor(Color heat, Color interference, float shift, Color sweep, float sweepAt)
    {
        _visorShown = _used = true;
        _heat = heat;
        _interference = interference;
        _interferenceShift = shift;
        _sweep = sweep;
        _sweepAt = sweepAt;
    }

    internal static void HideVisor() => _visorShown = false;

    /// <summary>Once a frame, after every mod's Update.</summary>
    internal static void Tick()
    {
        var now = ForgeCore.Frame.GameTime;
        AutoWarm();
        var effects = Book.Evaluate(now);
        var bands = GatherBands(now);
        var visor = _visorShown && (_heat.a > 0.003f || _interference.a > 0.003f || _sweep.a > 0.003f);
        if (!effects && bands == 0 && !visor)
        {
            Park();
            return;
        }
        if (!Build()) return;
        var aspect = ScreenLayer.Aspect;
        var height = (float)Math.Max(1, ScreenLayer.Height);
        var blur = Book.Strength(ScreenChannel.Blur);
        var split = Book.Strength(ScreenChannel.Chromatic);
        UseLod(blur >= ScreenEffectBook.Off || split >= ScreenEffectBook.Off ? LodGrab : LodLayers);
        Colour(0, FlashId, Merged(ScreenChannel.Flash, 1f));
        Colour(1, VignetteId, Merged(ScreenChannel.Vignette, 1f));
        Colour(2, LowHealthId, Merged(ScreenChannel.LowHealth, ScreenEffectBook.Pulse(Book.Rate(ScreenChannel.LowHealth), now)));
        Colour(3, HeatId, visor ? _heat : default);
        Colour(4, InterferenceId, visor ? _interference : default);
        Colour(5, SweepId, visor ? _sweep : default);
        Vector(6, LayoutId, new Vector4(aspect, blur * ScreenEffectBook.MaxBlur1080 / 1080f, split * ScreenEffectBook.MaxSplit1080 / 1080f,
            visor ? _interferenceShift : 0f));
        Vector(7, SweepAtId, new Vector4(_sweepAt, 0f, 0f, 0f));
        for (int i = 0; i < Bands; i++) Vector(8 + i, BandIds[i], i < bands ? Band(BandAzimuth[i], BandAlpha[i], aspect, height) : default);
        ScreenOverlays.Active(_piece, true);
        User.Use(true);
    }

    /// <summary>The merged colour of <paramref name="channel"/> with its strength (times <paramref name="scale"/>) in the alpha.</summary>
    private static Color Merged(ScreenChannel channel, float scale)
    {
        var strength = Book.Strength(channel);
        if (strength < ScreenEffectBook.Off) return default;
        return new Color(Book.R(channel), Book.G(channel), Book.B(channel), Ease.Clamp01(Book.A(channel) * strength * scale));
    }

    /// <summary>The six strongest flashes of every hit edge, strongest first; returns how many.</summary>
    private static int GatherBands(float now)
    {
        int n = 0;
        var peak = Plugin.HitFlashPeak;
        for (int e = 0; e < Edges.Count; e++)
        {
            var flashes = Edges[e].Flashes;
            for (int i = 0; i < EdgeFlashes.Slots; i++)
            {
                var alpha = flashes.Level(i, now) * peak;
                if (alpha <= 0.003f) continue;
                if (n == Bands && alpha <= BandAlpha[n - 1]) continue;
                int at = n < Bands ? n++ : n - 1;
                while (at > 0 && BandAlpha[at - 1] < alpha) { BandAlpha[at] = BandAlpha[at - 1]; BandAzimuth[at] = BandAzimuth[at - 1]; at--; }
                BandAlpha[at] = alpha;
                BandAzimuth[at] = flashes.Azimuth(i);
            }
        }
        return n;
    }

    /// <summary>A hit band's place: centred half its depth inside the screen edge toward <paramref name="azimuth"/>, turned to face inward.</summary>
    private static Vector4 Band(float azimuth, float alpha, float aspect, float height)
    {
        var (u, v, normal) = Bearing.EdgePoint(azimuth, aspect);
        var inward = normal * Mathf.Deg2Rad;
        var depth = _bandDepth / height;
        var p = ScreenLayer.At(u, v);
        return new Vector4(p.x + Mathf.Cos(inward) * depth / 2f, p.y + Mathf.Sin(inward) * depth / 2f, (normal - 90f) * Mathf.Deg2Rad, alpha);
    }

    private static void Colour(int index, int id, Color colour)
    {
        var v = new Vector4(colour.r, colour.g, colour.b, colour.a);
        if (Same(Shown[index], v)) return;
        Shown[index] = v;
        _material!.SetColor(id, colour);
    }

    private static void Vector(int index, int id, Vector4 value)
    {
        if (Same(Shown[index], value)) return;
        Shown[index] = value;
        _material!.SetVector(id, value);
    }

    // Field by field: the interop's struct operators may go through the game's boxing invoke.
    private static bool Same(Vector4 a, Vector4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

    private static void UseLod(int lod)
    {
        if (_lod == lod) return;
        _lod = lod;
        _shader!.maximumLOD = lod;
    }

    private static void Park()
    {
        if (_generation == ScreenLayer.Generation) ScreenOverlays.Active(_piece, false);
        User.Use(false);
    }

    /// <summary>Builds the pass and draws both of its variants (with and without the screen grab) once off screen, so the first effect
    /// shown creates and compiles nothing. False (logged) when it cannot be drawn.</summary>
    internal static bool Warm()
    {
        if (!Build()) return false;
        if (_warmedGeneration == ScreenLayer.Generation) return true;
        _warmedGeneration = ScreenLayer.Generation;
        var wasActive = _piece!.activeSelf;
        _piece.SetActive(true);
        _shader!.maximumLOD = LodGrab;
        ScreenLayer.RenderOnce();
        _shader.maximumLOD = LodLayers;
        ScreenLayer.RenderOnce();
        _lod = LodLayers;
        _piece.SetActive(wasActive);
        return true;
    }

    private static int _autoGeneration = int.MinValue;

    /// <summary>Once a mod has used the stack this session, the pass is warmed when the elevator has stopped (or on the first level frame) and again
    /// after the screen size changed; a session that never uses it never builds it. Building it (shader, textures, the layer) and drawing it are two
    /// steps of ForgeCore <c>LandingWork</c>, a frame apart and apart from the other warm-ups.</summary>
    private static void AutoWarm()
    {
        if (!_used || _failed || _warmedGeneration == ScreenLayer.Generation && _warmedGeneration >= 0 || _autoGeneration == ScreenLayer.Generation) return;
        var state = GameStateManager.CurrentStateName;
        if (state != eGameStateName.StopElevatorRide && state != eGameStateName.InLevel) return;
        _autoGeneration = ScreenLayer.Generation;
        ForgeCore.LandingWork.Add(Plugin.Id, "screen-effects-build", () => { if (!_failed) Build(); });
        ForgeCore.LandingWork.Add(Plugin.Id, "screen-effects-draw", () => { if (!_failed) Warm(); });
    }

    private static bool Build()
    {
        if (_failed || !ScreenLayer.Ensure()) return false;
        if (_generation == ScreenLayer.Generation) return true;
        try
        {
            if (!_shaderTried)
            {
                _shaderTried = true;
                _shader = UIKit.LoadShader(BuiltIn, ScreenEffectsShaderBytes.Read, ScreenEffectsShaderBytes.AssetName);
            }
            if (_shader == null) throw new InvalidOperationException("its shader is not loaded");
            ScreenOverlays.Destroy(_material);
            ScreenOverlays.Destroy(_bandTexture);
            ScreenOverlays.Destroy(_interferenceTexture);
            User.Reset();
            var height = Math.Max(2, ScreenLayer.Height);
            // The hit band at one texel per screen pixel, as wide as 45 % and as deep as 11 % of the screen height; row 0 on the edge.
            int w = Math.Max(16, (int)(height * 0.45f)), h = _bandDepth = Math.Max(8, (int)(height * 0.11f));
            _bandTexture = ScreenOverlays.Texture("ForgeUI.ScreenEffects.Band", w, h, (x, y) => ScreenPatterns.EdgeFlash(x, y, w, h));
            _interferenceTexture = ScreenOverlays.Texture("ForgeUI.ScreenEffects.Interference", 128, height,
                (x, y) => ScreenPatterns.Interference(x / 127f, y / (float)(height - 1), y));
            _bandTexture.wrapMode = TextureWrapMode.Clamp;
            _interferenceTexture.wrapMode = TextureWrapMode.Clamp;
            _material = ScreenLayer.Material(_shader, Color.white, Queue);
            _material.SetTexture(BandTexId, _bandTexture);
            _material.SetTexture(InterferenceTexId, _interferenceTexture);
            _material.SetVector(BandSizeId, new Vector4(w / (float)height, h / (float)height, 0f, 0f));
            for (int i = 0; i < Shown.Length; i++) Shown[i] = new Vector4(-1f, -1f, -1f, -1f);
            _piece = ScreenLayer.Piece("ForgeUI.ScreenEffects", ScreenLayer.Quad, _material);
            _piece.transform.localScale = new Vector3(ScreenLayer.Aspect, 1f, 1f);
            _piece.SetActive(false);
            _lod = -1;
            _generation = ScreenLayer.Generation;
        }
        catch (Exception error)
        {
            _failed = true;
            Plugin.Error("ui-screen-effect-stack", "The screen effect stack could not be built and is off (hit edges, visor shade and glow, pushed screen effects): " + error);
            return false;
        }
        return true;
    }
}
