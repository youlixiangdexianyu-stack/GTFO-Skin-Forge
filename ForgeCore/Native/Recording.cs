using System;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>What kind of picture a <see cref="ShownEffect"/> is.</summary>
public enum ShownKind : byte
{
    /// <summary>A one-shot effect at <see cref="ShownEffect.At"/> facing <see cref="ShownEffect.To"/> (a direction; zero unrotated).</summary>
    Play,
    /// <summary>A one-shot line effect from <see cref="ShownEffect.At"/> to <see cref="ShownEffect.To"/>.</summary>
    Line,
    /// <summary>A held effect under <see cref="ShownEffect.Key"/> this frame, at <see cref="ShownEffect.At"/> facing <see cref="ShownEffect.To"/>; told every frame it is held.</summary>
    Hold,
    /// <summary>A held line under <see cref="ShownEffect.Key"/> this frame, from <see cref="ShownEffect.At"/> to <see cref="ShownEffect.To"/>.</summary>
    HeldLine,
    /// <summary>The held effect, line, beam or trail under <see cref="ShownEffect.Key"/> ended at once.</summary>
    Release,
    /// <summary>A beam under <see cref="ShownEffect.Key"/> shown this frame from <see cref="ShownEffect.At"/> to <see cref="ShownEffect.To"/>; <see cref="ShownEffect.Look"/> is its style.</summary>
    Beam,
    /// <summary>A trail under <see cref="ShownEffect.Key"/> whose head is at <see cref="ShownEffect.At"/> this frame, <see cref="ShownEffect.Size"/> its progress; <see cref="ShownEffect.Name"/> its style's name.</summary>
    Trail,
    /// <summary>A mark laid on a surface at <see cref="ShownEffect.At"/> with normal <see cref="ShownEffect.To"/>, of the sprite sheet <see cref="ShownEffect.Name"/>.</summary>
    Decal,
}

/// <summary>One effect a library drew, as the library was asked for it. <see cref="Name"/> is a <see cref="RecordingNames"/> number; <see cref="Key"/>
/// tells a held thing's frames apart from another's (0 for a one-shot); <see cref="Look"/> is the library's own look object where the name alone does not
/// say how it is drawn (a beam's style). Passed by reference: nothing is copied or allocated.</summary>
public readonly struct ShownEffect
{
    internal ShownEffect(ShownKind kind, int name, int key, Vector3 at, Vector3 to, float size, Color colour, bool head = true, float seconds = 0f, float fade = 0f, object? look = null)
    {
        Kind = kind; Name = name; Key = key; At = at; To = to; Size = size; Colour = colour; Head = head; Seconds = seconds; Fade = fade; Look = look;
    }

    public ShownKind Kind { get; }
    public int Name { get; }
    public int Key { get; }
    public Vector3 At { get; }
    public Vector3 To { get; }
    /// <summary>The size asked for (m; NaN: the effect's own), a beam's width factor, a trail's progress.</summary>
    public float Size { get; }
    public Color Colour { get; }
    /// <summary>A line's head is drawn (it hit something).</summary>
    public bool Head { get; }
    /// <summary>A decal's life and its fade at the end (s).</summary>
    public float Seconds { get; }
    public float Fade { get; }
    public object? Look { get; }
}

/// <summary>Where a <see cref="ShownSound"/> was played.</summary>
public enum ShownPlace : byte { TwoD, World, Attached }

/// <summary>One sound a library played: <see cref="Name"/> is the <see cref="RecordingNames"/> number of <c>library/cue</c>; the values are the ones
/// the play used (picked variant and pitch included), so the same play can be made again. <see cref="Loop"/> is a looping play's own number (0 for a
/// one-shot), which <see cref="IShownObserver.SoundEnded"/> names when it ends.</summary>
public readonly struct ShownSound
{
    internal ShownSound(int name, ShownPlace place, Vector3 at, int variant, float pitch, float gain, bool self, long loop)
    {
        Name = name; Place = place; At = at; Variant = variant; Pitch = pitch; Gain = gain; Self = self; Loop = loop;
    }

    public int Name { get; }
    public ShownPlace Place { get; }
    public Vector3 At { get; }
    public int Variant { get; }
    public float Pitch { get; }
    public float Gain { get; }
    public bool Self { get; }
    public long Loop { get; }
}

/// <summary>What happened to a model a library put in the world.</summary>
public enum ShownModelKind : byte
{
    /// <summary><see cref="ShownModel.Instance"/> of model <see cref="ShownModel.Name"/> was made under <see cref="ShownModel.On"/> (null: in the world).
    /// A model worn on a first-person body is not reported: only its owner's view draws it.</summary>
    Spawned,
    /// <summary>Renderers under <see cref="ShownModel.On"/> were turned off; <see cref="ShownModel.Token"/> is the list that holds them.</summary>
    Hidden,
    /// <summary>The renderers in <see cref="ShownModel.Token"/> are drawn again.</summary>
    Shown,
}

/// <summary>One model report (<see cref="ShownModelKind"/>).</summary>
public readonly struct ShownModel
{
    internal ShownModel(ShownModelKind kind, int name, GameObject? instance, Transform? on, object? token)
    {
        Kind = kind; Name = name; Instance = instance; On = on; Token = token;
    }

    public ShownModelKind Kind { get; }
    public int Name { get; }
    public GameObject? Instance { get; }
    public Transform? On { get; }
    public object? Token { get; }
}

/// <summary>Whether a posed clip began or ended (<see cref="ShownPose"/>).</summary>
public enum ShownPoseKind : byte
{
    /// <summary>The clip <see cref="ShownPose.Name"/> began on track <see cref="ShownPose.Track"/> of <see cref="ShownPose.Anchor"/>, <see cref="ShownPose.Elapsed"/> seconds into it.</summary>
    Start,
    /// <summary>The clip on that track ended, fading out over <see cref="ShownPose.FadeOut"/> seconds (0: at once).</summary>
    Stop,
}

/// <summary>A mod declaring that it poses a player or enemy body with a named joint clip: ForgeVisual reports every <c>JointPlayer</c> clip that has a name, so a
/// mod that plays its clips there declares nothing itself; a mod that poses a body some other way declares here (<see cref="Recording.Pose"/>). Only the clip's
/// name, its start and its play options travel, never its bones: whoever replays it finds the clip by the same name (<c>JointClip.Find</c>). A track is one of the
/// body's few concurrent clips, numbered from 0; <see cref="Joints"/> holds one weight per canonical joint (0: the clip leaves the bone alone) and is only valid during the call.</summary>
public readonly struct ShownPose
{
    internal ShownPose(ShownPoseKind kind, ShownAnchor anchor, int track, int name, float elapsed, bool loop, float weight, float fadeIn, float fadeOut, float[]? joints)
    {
        Kind = kind; Anchor = anchor; Track = track; Name = name; Elapsed = elapsed; Loop = loop; Weight = weight; FadeIn = fadeIn; FadeOut = fadeOut; Joints = joints;
    }

    public ShownPoseKind Kind { get; }
    public ShownAnchor Anchor { get; }
    public int Track { get; }
    /// <summary>A <see cref="RecordingNames"/> number of the clip's name.</summary>
    public int Name { get; }
    public float Elapsed { get; }
    public bool Loop { get; }
    public float Weight { get; }
    public float FadeIn { get; }
    public float FadeOut { get; }
    public float[]? Joints { get; }
}

/// <summary>Hears what the libraries draw and play (<see cref="Recording.Observe"/>). Called on the main thread inside the library call; must not throw
/// (one that throws is logged once and stays subscribed) and should not allocate.</summary>
public interface IShownObserver
{
    void Effect(in ShownEffect effect);
    void Sound(in ShownSound sound);
    void SoundEnded(long loop);
    void Model(in ShownModel model);
    void Pose(in ShownPose pose);
}

/// <summary>What a transform hangs on: a player (<see cref="Player"/>), an enemy (<see cref="Enemy"/>) or nothing either (<see cref="AnchorKind.World"/>).</summary>
public enum AnchorKind : byte { World, Player, Enemy }

/// <summary>The thing a transform belongs to (<see cref="Recording.Anchor"/>). <see cref="Node"/> is the transform itself when it is not the thing's root
/// (a bone, a socket), so a recorder can name it and a replay can find the same-named node on its own copy of the thing.</summary>
public readonly struct ShownAnchor
{
    public ShownAnchor(AnchorKind kind, PlayerAgent? player, EnemyAgent? enemy, Transform? node)
    {
        Kind = kind; Player = player; Enemy = enemy; Node = node;
    }

    public AnchorKind Kind { get; }
    public PlayerAgent? Player { get; }
    public EnemyAgent? Enemy { get; }
    public Transform? Node { get; }
}

/// <summary>
/// The observation port of the libraries: ForgeVisual and ForgeAudio report here every effect, line, held effect, beam, trail, decal, model and sound they
/// put in the world at the point every call of their kind passes, and any number of observers (a recorder, a census) hear them. While nobody observes, a
/// report site costs one field read (<see cref="Watched"/>): no struct is built, nothing is looked up or allocated. Names travel as <see cref="RecordingNames"/>
/// numbers that each handle takes once. Named joint clips played on a body (ForgeVisual's JointPlayer, or a mod's own <see cref="Pose"/> declaration) are reported as a name, a start and play options;
/// a pose copied from the game's body every frame (PoseCopy) is derived and not reported. Main thread only.
/// </summary>
public static class Recording
{
    private static readonly Observers<IShownObserver> All = new();

    /// <summary>True while anyone observes: a library builds a report only then.</summary>
    internal static bool Watched => All.Any;

    /// <summary>Adds <paramref name="observer"/>; disposing the result removes it. Several observers hear every report, in the order they were added.</summary>
    public static IDisposable Observe(IShownObserver observer) => All.Add(observer);

    /// <summary>Writes a note into the recording any recorder is making (ForgeReplay's, on host and client alike, since each machine records what its own mods note); nothing happens while no recorder listens. <paramref name="mark"/> asks the replay to show the note as a point on its timeline. A mod needs no reference to the recorder.</summary>
    public static void Note(string modId, string kind, string payload = "", bool mark = false)
    {
        if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(kind)) throw new ArgumentException("A recording note names its mod and its kind.");
        RecordingNotes.Send(new RecordingNote(modId, kind, payload ?? "", mark), Failed);
    }

    /// <summary>Hears every <see cref="Note"/> from now; disposing the result removes the listener. Called on the main thread inside <see cref="Note"/>; one that throws is logged once and stays.</summary>
    public static IDisposable ObserveNotes(Action<RecordingNote> listener) => RecordingNotes.Add(listener);

    internal static void Effect(in ShownEffect effect)
    {
        var all = All.Items;
        for (var i = 0; i < all.Length; i++)
        {
            try { all[i].Effect(effect); }
            catch (Exception error) { Failed(error); }
        }
    }

    internal static void Sound(in ShownSound sound)
    {
        var all = All.Items;
        for (var i = 0; i < all.Length; i++)
        {
            try { all[i].Sound(sound); }
            catch (Exception error) { Failed(error); }
        }
    }

    internal static void SoundEnded(long loop)
    {
        var all = All.Items;
        for (var i = 0; i < all.Length; i++)
        {
            try { all[i].SoundEnded(loop); }
            catch (Exception error) { Failed(error); }
        }
    }

    internal static void Model(in ShownModel model)
    {
        var all = All.Items;
        for (var i = 0; i < all.Length; i++)
        {
            try { all[i].Model(model); }
            catch (Exception error) { Failed(error); }
        }
    }

    internal static void Pose(in ShownPose pose)
    {
        var all = All.Items;
        for (var i = 0; i < all.Length; i++)
        {
            try { all[i].Pose(pose); }
            catch (Exception error) { Failed(error); }
        }
    }

    /// <summary>What <paramref name="at"/> hangs on: the player agent above it, or the enemy agent above it, or a player whose third-person body (which need not
    /// hang under its agent) holds it; else the world. A few native reads per call: ask when a thing is made, not every frame.</summary>
    public static ShownAnchor Anchor(Transform? at)
    {
        if (at == null || at.WasCollected) return default;
        var player = at.GetComponentInParent<PlayerAgent>();
        if (player != null) return new ShownAnchor(AnchorKind.Player, player, null, at.Pointer == player.transform.Pointer ? null : at);
        var enemy = at.GetComponentInParent<EnemyAgent>();
        if (enemy != null) return new ShownAnchor(AnchorKind.Enemy, null, enemy, at.Pointer == enemy.transform.Pointer ? null : at);
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
        {
            var body = agent == null ? null : agent.AnimatorBody;
            if (body != null && at.IsChildOf(body.transform)) return new ShownAnchor(AnchorKind.Player, agent, null, at);
        }
        return default;
    }

    private static void Failed(Exception error) => Plugin.Logger.ErrorOnce("core-recording-observer-failed", "A recording observer failed: " + error);
}
