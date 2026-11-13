using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeSkin.Plugin;

// Attachment holograms (PLAN §3.1 Attachment.Hologram): unlit glowing surfaces drawn with the game's hologram shader (the
// mine deployer's display), or with the gear shader's unlit glow where that shader is missing or HologramShader is off.
static class Holograms
{
    const string ShaderName = "Cell/Player/Display_Hologram";
    const string Name = "ForgeSkin hologram";
    internal static bool UseShader = true; // HologramShader in ForgeSkin.cfg
    // Brightness of a hologram that sets none: the default screen glow (Glow 1.5 x GlowUnit 0.2).
    internal const float Glow = 0.3f;
    // Which of these the shader has is logged by Source; the colour goes to them, colour x gain to its glow colour (Glow).
    static readonly string[] ColourProperties = { "_Color", "_TintColor", "_MainColor", "_HoloColor" };
    static Material? _source;
    static bool _looked;

    // Ours, copied from a loaded game material with the shader (its textures come along) or made from the shader; null
    // when the game has no such shader. Looked up once; the first lookup logs the shader's properties.
    static Material? Source()
    {
        if (_looked) return _source;
        _looked = true;
        Material? game = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>()))
            if (o.TryCast<Material>() is { } m && m.shader != null && m.shader.name == ShaderName) { game = m; break; }
        var shader = game?.shader ?? Shader.Find(ShaderName);
        if (shader == null)
        {
            ForgeSkinPlugin.Warn($"hologram: no shader {ShaderName}; holograms use the gear shader's unlit glow");
            return null;
        }
        _source = ForgeCore.Assets.Keep(new Material(game != null ? game : new Material(shader)) { name = Name });
        var lines = new List<string>();
        for (int i = 0; i < shader.GetPropertyCount(); i++)
        {
            var name = shader.GetPropertyName(i);
            var type = shader.GetPropertyType(i);
            string value = game == null ? "" : type switch
            {
                UnityEngine.Rendering.ShaderPropertyType.Color or UnityEngine.Rendering.ShaderPropertyType.Vector => " = " + game.GetVector(name),
                UnityEngine.Rendering.ShaderPropertyType.Texture => " = " + (game.GetTexture(name)?.name ?? "none"),
                _ => " = " + game.GetFloat(name),
            };
            lines.Add($"{name} ({type}){value}");
        }
        ForgeSkinPlugin.Info($"hologram: {ShaderName} from {(game == null ? "Shader.Find" : game.name)}, queue {_source.renderQueue}, " +
            $"keywords [{string.Join(" ", _source.shaderKeywords)}], properties: {string.Join("; ", lines)}");
        return _source;
    }

    // A hologram surface showing `picture` (white when null) in `colour`, glowing by `gain` (EmissionUnit: as itself).
    public static Material Surface(SkinnedGear gear, Material template, Texture? picture, Color colour, float gain)
    {
        var source = UseShader ? Source() : null;
        Material m;
        if (source != null)
        {
            m = gear.Own(new Material(source) { name = Name });
            if (picture != null) m.SetTexture(SkinnedGear.MainTex, picture);
        }
        else
        {
            // As an unlit attachment: black albedo, the picture as emission.
            m = gear.Own(new Material(template) { name = Name });
            m.SetTexture(SkinnedGear.BumpMap, null);
            m.SetTexture(SkinnedGear.MetallicGlossMap, Texture2D.blackTexture);
            m.SetTexture(SkinnedGear.MainTex, Texture2D.blackTexture);
            m.SetTexture(SkinnedGear.EmissiveMap, picture ?? Texture2D.whiteTexture);
        }
        Paint(m, colour, gain);
        return m;
    }

    public static bool Is(Material m) => m.name == Name;

    public static void Paint(Material m, Color colour, float gain)
    {
        if (m.shader.name != ShaderName)
        {
            SkinnedGear.SetEmission(m, colour * gain);
            return;
        }
        foreach (var p in ColourProperties) if (m.HasProperty(p)) m.SetColor(p, colour);
        ForgeVisual.Glow.SetColour(m, colour * gain);
    }
}

// Hologram flicker: the renderers drop out for a few hundredths of a second at random moments, more often with a higher
// amount. forceRenderingOff, so a `show` effect (Renderer.enabled) still decides whether they are there at all.
sealed class Flicker : ITriggered
{
    readonly Renderer[] _renderers;
    readonly float _amount;
    float _next, _until = -1;

    public Flicker(Renderer[] renderers, float amount)
    {
        _renderers = renderers;
        _amount = amount;
    }

    public void Start(float time) { }

    public void Update(float time)
    {
        if (_until >= 0)
        {
            if (time < _until) return;
            _until = -1;
            Set(false);
            // Calm for 2 s down to 0.15 s on average; sometimes a second drop right after.
            _next = time + (UnityEngine.Random.value < 0.3f ? 0.06f : UnityEngine.Random.Range(0.3f, 1.7f) * (2.2f - 2f * _amount));
            return;
        }
        if (time < _next) return;
        _until = time + UnityEngine.Random.Range(0.02f, 0.08f);
        Set(true);
    }

    void Set(bool off)
    {
        foreach (var r in _renderers)
            if (r != null) r.forceRenderingOff = off;
    }
}
