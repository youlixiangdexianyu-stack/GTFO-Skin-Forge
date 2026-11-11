using SkinForge.Format;
using UnityEngine;
using PlayMode = SkinForge.Format.PlayMode;

namespace SkinForge.Plugin;

// Something a game event starts and the per-frame tick advances. Start and Update never allocate.
interface ITriggered
{
    void Start(float time);
    void Update(float time);
}

// One animated layer: atlas frames copied into the target region on the GPU when the frame changes. An `Always` layer
// plays from the start; a triggered one shows the base pixels until Start, plays, then copies the cached base back.
sealed class Layer : ITriggered
{
    readonly Texture2D _atlas;
    readonly RenderTexture _target;
    readonly RenderTexture? _base; // the region as it was, for a triggered layer
    readonly int _count, _columns, _width, _height, _atlasHeight, _x, _y;
    readonly float _fps, _seconds;
    readonly PlayMode _play;
    float _start;
    bool _playing;
    int _frame = -1;

    public Layer(Texture2D atlas, RenderTexture target, RenderTexture? regionBase, DynamicLayer l, int x, int y)
    {
        var a = l.Atlas!;
        _atlas = atlas; _target = target; _base = regionBase;
        _count = a.FrameCount; _columns = a.Columns; _width = a.FrameWidth; _height = a.FrameHeight;
        _atlasHeight = atlas.height; _x = x; _y = y; _fps = l.Fps; _seconds = l.PlaySeconds; _play = l.Play;
        _playing = regionBase == null;
    }

    public void Start(float time)
    {
        _start = time;
        _playing = true;
        _frame = -1;
    }

    public void Update(float time)
    {
        if (!_playing) return;
        float t = time - _start;
        int frame = _fps > 0 ? (int)(t * _fps) : 0;
        if (_play == PlayMode.Once ? frame >= _count : _play == PlayMode.LoopFor && t >= _seconds)
        {
            _playing = false;
            if (_base == null) return; // an Always layer that played once holds its last frame
            Graphics.CopyTexture(_base, 0, 0, 0, 0, _width, _height, _target, 0, 0, _x, _y);
            _target.GenerateMips();
            return;
        }
        frame %= _count;
        if (frame == _frame) return;
        _frame = frame;
        int col = frame % _columns, row = frame / _columns;
        Graphics.CopyTexture(_atlas, 0, 0, col * _width, _atlasHeight - (row + 1) * _height, _width, _height, _target, 0, 0, _x, _y);
        _target.GenerateMips();
    }
}

// Attachment Flash: emission ramps from its own colour to the peak and back over Seconds.
sealed class Flash : ITriggered
{
    readonly Material[] _materials;
    readonly Color[] _rest;
    readonly Color _peak;
    readonly float _seconds;
    float _start = -1;

    public Flash(Material[] materials, AttachmentEffect e)
    {
        _materials = materials;
        _rest = new Color[materials.Length];
        for (int i = 0; i < materials.Length; i++) _rest[i] = materials[i].GetColor(SkinnedGear.EmissiveColor);
        _peak = new Color(e.Color[0], e.Color[1], e.Color[2]) * (e.Intensity * SkinnedGear.EmissionUnit);
        _seconds = Math.Max(e.Seconds, 0.01f);
    }

    public void Start(float time) => _start = time;

    public void Update(float time)
    {
        if (_start < 0) return;
        float t = (time - _start) / _seconds;
        float k = t >= 1 ? 0 : 1 - Math.Abs(2 * t - 1);
        for (int i = 0; i < _materials.Length; i++) _materials[i].SetColor(SkinnedGear.EmissiveColor, Color.Lerp(_rest[i], _peak, k));
        if (t >= 1) _start = -1;
    }
}

// Attachment Show: its renderers stay off until the event, then show for Seconds.
sealed class Show : ITriggered
{
    readonly Renderer[] _renderers;
    readonly float _seconds;
    float _until = -1;

    public Show(Renderer[] renderers, AttachmentEffect e)
    {
        _renderers = renderers;
        _seconds = e.Seconds;
        Set(false);
    }

    public void Start(float time)
    {
        _until = time + _seconds;
        Set(true);
    }

    public void Update(float time)
    {
        if (_until < 0 || time < _until) return;
        _until = -1;
        Set(false);
    }

    void Set(bool on)
    {
        for (int i = 0; i < _renderers.Length; i++) _renderers[i].enabled = on;
    }
}
