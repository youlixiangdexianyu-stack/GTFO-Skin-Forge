using ForgeSkin.Format;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using PlayMode = ForgeSkin.Format.PlayMode;

namespace ForgeSkin.Plugin;

// Something a game event starts and the per-frame tick advances. Start and Update never allocate.
interface ITriggered
{
    void Start(float time);
    void Update(float time);
}

// The GPU copy of one material channel that layers draw into. A layer that wrote to it marks it dirty; the gear rebuilds
// its mips once that frame, after all its layers ran.
sealed class LayerTarget
{
    public readonly Material Material;
    public readonly int Property;
    public readonly RenderTexture Texture;
    public bool Dirty;

    public LayerTarget(Material material, int property, RenderTexture texture) { Material = material; Property = property; Texture = texture; }
}

// One animated layer: atlas frames copied into the target region on the GPU when the frame changes. An `Always` layer
// plays from the start; a triggered one shows the base pixels until Start, plays, then copies the cached base back.
// Frames stored smaller than the region (they did not fit the atlas at full size) are stretched onto `_scaled`, a
// region-sized copy, and copied from there.
sealed class Layer : ITriggered
{
    readonly Texture2D _atlas;
    readonly LayerTarget _target;
    readonly RenderTexture? _base; // the region as it was, for a triggered layer
    readonly RenderTexture? _scaled;
    readonly int _count, _columns, _width, _height, _regionWidth, _regionHeight, _atlasHeight, _x, _y;
    readonly float _fps, _seconds;
    readonly PlayMode _play;
    float _start;
    bool _playing;
    int _frame = -1;

    public Layer(Texture2D atlas, LayerTarget target, RenderTexture? regionBase, RenderTexture? scaled, DynamicLayer l, int x, int y)
    {
        var a = l.Atlas!;
        _atlas = atlas; _target = target; _base = regionBase; _scaled = scaled;
        _count = a.FrameCount; _columns = a.Columns; _width = a.FrameWidth; _height = a.FrameHeight;
        _regionWidth = a.TargetRect[2]; _regionHeight = a.TargetRect[3];
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
            Graphics.CopyTexture(_base, 0, 0, 0, 0, _regionWidth, _regionHeight, _target.Texture, 0, 0, _x, _y);
            _target.Dirty = true;
            return;
        }
        frame %= _count;
        if (frame == _frame) return;
        _frame = frame;
        int col = frame % _columns, row = frame / _columns, top = _atlasHeight - (row + 1) * _height;
        if (_scaled == null) Graphics.CopyTexture(_atlas, 0, 0, col * _width, top, _width, _height, _target.Texture, 0, 0, _x, _y);
        else
        {
            // The frame's texel centres from edge to edge, so filtering never reads the next frame.
            float w = _atlas.width, h = _atlas.height;
            Graphics.Blit(_atlas, _scaled, new Vector2((_width - 1) / w, (_height - 1) / h), new Vector2((col * _width + 0.5f) / w, (top + 0.5f) / h));
            Graphics.CopyTexture(_scaled, 0, 0, 0, 0, _regionWidth, _regionHeight, _target.Texture, 0, 0, _x, _y);
        }
        _target.Dirty = true;
    }
}

// Attachment Flash: emission ramps from its own colour to the peak and back over Seconds. A surface that does not glow
// (a screen with GlowInGame off) has emission switched off, which would ignore the colour: it emits while flashing.
sealed class Flash : ITriggered
{
    readonly Material[] _materials;
    readonly Color[] _rest;
    readonly bool[] _emissive;
    readonly Color _peak;
    readonly float _seconds;
    float _start = -1;

    public Flash(Material[] materials, AttachmentEffect e)
    {
        _materials = materials;
        _rest = new Color[materials.Length];
        _emissive = new bool[materials.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            _rest[i] = materials[i].GetColor(SkinnedGear.EmissiveColor);
            _emissive[i] = materials[i].IsKeywordEnabled(SkinnedGear.EmissiveKeyword);
        }
        _peak = new Color(e.Color[0], e.Color[1], e.Color[2]) * (e.Intensity * MaterialParams.EmissionUnit);
        _seconds = Math.Max(e.Seconds, 0.01f);
    }

    public void Start(float time)
    {
        if (_start < 0)
            for (int i = 0; i < _materials.Length; i++)
                if (!_emissive[i]) SkinnedGear.SetEmissive(_materials[i], true);
        _start = time;
    }

    public void Update(float time)
    {
        if (_start < 0) return;
        float t = (time - _start) / _seconds;
        float k = t >= 1 ? 0 : 1 - Math.Abs(2 * t - 1);
        for (int i = 0; i < _materials.Length; i++)
        {
            ForgeVisual.Glow.SetColour(_materials[i], Color.Lerp(_rest[i], _peak, k));
            if (t >= 1 && !_emissive[i]) SkinnedGear.SetEmissive(_materials[i], false);
        }
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

// A charm (Attachment.Charm): Core CharmRope's rope as one mesh under the attachment's root (vertices in its local space),
// and the pendant (the attachment's model and screen) on the rope's end. Built at rest; Update moves it, called only for the
// local player's wielded first-person gear, so everything else hangs still along the attachment's -Y.
sealed class CharmLook
{
    readonly Transform _root, _pendant;
    readonly CharmRope _rope;
    readonly Mesh _mesh;
    readonly Il2CppStructArray<Vector3> _vertices, _normals;
    readonly float[] _v, _n;
    readonly double[] _matrix = new double[16], _pose = new double[7], _body = new double[3];

    // `anchor`: the root's localToWorldMatrix as Read writes it, already read by the caller.
    public CharmLook(CharmRope rope, Transform root, double[] anchor, Transform pendant, Mesh mesh)
    {
        _rope = rope;
        _root = root;
        _pendant = pendant;
        _mesh = mesh;
        int count = rope.VertexCount;
        _v = new float[count * 3];
        _n = new float[count * 3];
        _vertices = new Il2CppStructArray<Vector3>(count);
        _normals = new Il2CppStructArray<Vector3>(count);
        Array.Copy(anchor, _matrix, 16);
        rope.Rest(_matrix);
        Write();
        mesh.triangles = new Il2CppStructArray<int>(rope.Triangles());
    }

    public static void Read(Matrix4x4 m, double[] o)
    {
        o[0] = m.m00; o[1] = m.m10; o[2] = m.m20; o[3] = m.m30;
        o[4] = m.m01; o[5] = m.m11; o[6] = m.m21; o[7] = m.m31;
        o[8] = m.m02; o[9] = m.m12; o[10] = m.m22; o[11] = m.m32;
        o[12] = m.m03; o[13] = m.m13; o[14] = m.m23; o[15] = m.m33;
    }

    // `body`: the local player's camera, the player's body for the rope.
    public void Update(float time, Vector3 body)
    {
        Read(_root.localToWorldMatrix, _matrix);
        _body[0] = body.x; _body[1] = body.y; _body[2] = body.z;
        _rope.Update(time, _matrix, _body);
        Write();
    }

    void Write()
    {
        _rope.Pendant(_pose);
        _pendant.SetPositionAndRotation(new Vector3((float)_pose[0], (float)_pose[1], (float)_pose[2]),
            new Quaternion((float)_pose[3], (float)_pose[4], (float)_pose[5], (float)_pose[6]));
        _rope.Mesh(_matrix, _v, _n);
        for (int i = 0, o = 0; o < _v.Length; i++, o += 3)
        {
            _vertices[i] = new Vector3(_v[o], _v[o + 1], _v[o + 2]);
            _normals[i] = new Vector3(_n[o], _n[o + 1], _n[o + 2]);
        }
        _mesh.vertices = _vertices;
        _mesh.normals = _normals;
    }
}
