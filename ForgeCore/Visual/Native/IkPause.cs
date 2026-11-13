using Player;
using RootMotion.FinalIK;
using UnityEngine;

namespace ForgeVisual;

/// <summary>A player's IK: the game's FinalIK solvers and <c>PlayerSyncIK</c>, which would pull bones away from a pose a mod writes.
/// They sit on the third-person body, outside the agent's own object. Each solver is bound to the clip joints it moves (an arm's
/// <c>LimbIK</c> its shoulder to hand, the aim and look solvers the spine and head, the biped and grounder solvers everything), so
/// <see cref="Hold"/> stops only those that move a joint the mask covers and <see cref="Release"/> hands them back as they were.
/// <c>PlayerSyncIK</c> solves through those components and its own weights: a masked arm sets that arm's weight to 0, a masked
/// spine or head the aim and look weights, and only a mask over the hips turns its switch off.</summary>
internal sealed class IkPause
{
    internal static readonly IkPause None = new(System.Array.Empty<Behaviour>(), System.Array.Empty<bool[]>(), null);

    private static readonly int[] Spine = { 7, 8, 9, 18, 19 };

    private readonly Behaviour[] _solvers;
    private readonly bool[][] _moves;
    private readonly bool[] _were;
    private readonly bool[] _holdJoints = new bool[ClipRig.Count];
    private readonly PlayerSyncIK? _sync;
    private bool _syncWas, _heldLeft, _heldRight, _heldAim;
    private float _left, _right, _aim, _look;
    private bool _held;

    private IkPause(Behaviour[] solvers, bool[][] moves, PlayerSyncIK? sync)
    {
        _solvers = solvers;
        _moves = moves;
        _were = new bool[solvers.Length];
        _sync = sync;
    }

    /// <summary>The agent's solvers, each bound to the clip joints it moves; <paramref name="bones"/> are the body's bones in
    /// <see cref="ClipRig"/> order, or null for a caller that only ever holds the whole body.</summary>
    internal static IkPause Of(PlayerAgent agent, Transform?[]? bones)
    {
        var solvers = Solvers(agent);
        var moves = new bool[solvers.Length][];
        for (var i = 0; i < solvers.Length; i++)
        {
            var move = moves[i] = new bool[ClipRig.Count];
            if (bones == null) { System.Array.Fill(move, true); continue; }
            var limb = solvers[i].TryCast<LimbIK>();
            if (limb != null && limb.solver != null)
            {
                foreach (var b in new[] { limb.solver.bone1, limb.solver.bone2, limb.solver.bone3 })
                    for (var j = 0; j < ClipRig.Count; j++)
                        if (b?.transform != null && bones[j] != null && b.transform.Pointer == bones[j]!.Pointer) move[j] = true;
                // An arm limb also turns its shoulder's child chain from the shoulder joint up.
                if (move[11] || move[12] || move[13]) move[10] = true;
                if (move[15] || move[16] || move[17]) move[14] = true;
                continue;
            }
            var type = solvers[i].GetIl2CppType().Name;
            if (type is nameof(AimIK) or nameof(LookAtIK)) { foreach (var j in Spine) move[j] = true; continue; }
            // BipedIK, GrounderBipedIK and PlayerSyncIK itself reach the whole body.
            for (var j = 0; j < ClipRig.Count; j++) move[j] = true;
        }
        var sync = agent.SyncIK;
        return new IkPause(solvers, moves, sync != null && !sync.WasCollected ? sync : null);
    }

    /// <summary>Every FinalIK solver and <c>PlayerSyncIK</c> under the agent, its third-person body and its <c>SyncIK</c>'s object.</summary>
    internal static Behaviour[] Solvers(PlayerAgent agent)
    {
        var found = new System.Collections.Generic.List<Behaviour>();
        var seen = new System.Collections.Generic.HashSet<System.IntPtr>();
        void Scan(Component? root)
        {
            if (root == null || root.WasCollected) return;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = behaviour.GetIl2CppType();
                var ns = type.Namespace ?? "";
                if ((ns.StartsWith("RootMotion.FinalIK", System.StringComparison.Ordinal) || type.Name == nameof(PlayerSyncIK)) && seen.Add(behaviour.Pointer))
                    found.Add(behaviour);
            }
        }
        Scan(agent);
        Scan(agent.AnimatorBody);
        Scan(agent.SyncIK);
        return found.ToArray();
    }

    /// <summary>Stops the IK that moves a joint <paramref name="covered"/> marks (per-joint weights above 0), after handing back what
    /// an earlier hold stopped; the joint player holds the union of its playing tracks' masks.</summary>
    internal void Hold(float[] covered)
    {
        // The same joints as the hold already in force: what it stopped stays stopped, nothing is handed back and taken again.
        if (_held && SameJoints(covered)) return;
        Release();
        _held = true;
        for (var j = 0; j < _holdJoints.Length; j++) _holdJoints[j] = covered[j] > 0f;
        var hips = covered[0] > 0f;
        for (var i = 0; i < _solvers.Length; i++)
        {
            var solver = _solvers[i];
            _were[i] = false;
            if (solver == null || solver.WasCollected || !solver.enabled) continue;
            var isSync = _sync != null && solver.Pointer == _sync.Pointer;
            if (isSync ? !hips : !Touches(_moves[i], covered)) continue;
            _were[i] = true;
            solver.enabled = false;
        }
        if (_sync == null || _sync.WasCollected) return;
        _syncWas = hips && _sync.IsEnabled();
        if (_syncWas) _sync.Disable();
        _heldLeft = Any(covered, 10, 13);
        _heldRight = Any(covered, 14, 17);
        _heldAim = Touches(Spine, covered);
        if (_heldLeft) { _left = _sync.m_leftArmWeightTarget; _sync.SetLeftArmWeight(0f); }
        if (_heldRight) { _right = _sync.m_rightArmSolver != null ? _sync.m_rightArmSolver.IKPositionWeight : 0f; _sync.SetRightArmWeight(0f); }
        if (_heldAim) { _aim = _sync.m_aimWeightTarget; _look = _sync.m_lookWeightTarget; _sync.SetAimWeight(0f); _sync.SetLookWeight(0f); }
    }

    private bool SameJoints(float[] covered)
    {
        for (var j = 0; j < _holdJoints.Length; j++) if (_holdJoints[j] != covered[j] > 0f) return false;
        return true;
    }

    internal void Release()
    {
        if (!_held) return;
        _held = false;
        for (var i = 0; i < _solvers.Length; i++)
            if (_were[i] && _solvers[i] != null && !_solvers[i].WasCollected) _solvers[i].enabled = true;
        if (_sync == null || _sync.WasCollected) return;
        if (_heldLeft) _sync.SetLeftArmWeight(_left);
        if (_heldRight) _sync.SetRightArmWeight(_right);
        if (_heldAim) { _sync.SetAimWeight(_aim); _sync.SetLookWeight(_look); }
        if (_syncWas) _sync.Enable();
        _syncWas = _heldLeft = _heldRight = _heldAim = false;
    }

    private static bool Touches(bool[] moves, float[] covered)
    {
        for (var j = 0; j < moves.Length; j++) if (moves[j] && covered[j] > 0f) return true;
        return false;
    }

    private static bool Touches(int[] joints, float[] covered)
    {
        foreach (var j in joints) if (covered[j] > 0f) return true;
        return false;
    }

    private static bool Any(float[] covered, int from, int to)
    {
        for (var j = from; j <= to; j++) if (covered[j] > 0f) return true;
        return false;
    }
}
