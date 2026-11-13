using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using Player;
using SNetwork;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeCore;

/// <summary>Takes the game's own agents out of the game's hands: <see cref="Enemy"/> and <see cref="Player"/> hold an enemy or a player
/// agent as a puppet whose position, facing and walk animation come only from the caller, and <see cref="EnemyPuppet.Dispose"/> /
/// <see cref="PlayerPuppet.Dispose"/> put back everything that was changed. Nothing here knows why a puppet is wanted (replays, cut scenes,
/// demonstrations). Main thread only.</summary>
public static class Puppets
{
    /// <summary>True while the level only shows a recording: set by the package that plays it (ForgeReplay) from the moment the
    /// recording's level is asked for until the playback ends. Nothing played in such a level is the game's own: Forge's behaviour does
    /// not run in it (ForgeEngine adopts no plan for it, so no trigger fires and no zone is judged).</summary>
    public static bool Playback { get; set; }

    /// <summary>Whether <paramref name="enemy"/> is held as a puppet now. One set lookup, none while no enemy is held.</summary>
    public static bool IsPuppet(EnemyAgent? enemy) => PuppetAttackHook.Held.Count != 0 && enemy != null && PuppetAttackHook.Held.Contains(enemy.Pointer);

    /// <summary>Whether <paramref name="player"/> is held as a puppet now. One set lookup, none while no player is held.</summary>
    public static bool IsPuppet(PlayerAgent? player) => HeldPlayers.Count != 0 && player != null && HeldPlayers.Contains(player.Pointer);

    internal static readonly System.Collections.Generic.HashSet<IntPtr> HeldPlayers = new();

    /// <summary>Holds <paramref name="enemy"/> as a puppet: its AI is switched off, its nav mesh agent and root motion are disabled, it cannot be
    /// killed by damage (its damage limbs stay, so it can still be hit and aimed at), and the game may not cull its animator. Null for an enemy
    /// that is dead or collected. <paramref name="owner"/> names the caller in error lines.</summary>
    public static EnemyPuppet? Enemy(EnemyAgent enemy, string owner)
    {
        if (NativeObjects.Gone(enemy) || !enemy.Alive || enemy.AI == null || enemy.Anim == null) return null;
        return new EnemyPuppet(enemy, owner);
    }

    /// <summary>Opens up to <paramref name="count"/> empty player slots to bots (<c>SlotPermission.Bot</c>); the game fills them with its own bots
    /// and spawns their player agents. Returns how many slots were opened. Host only.</summary>
    public static int OpenBotSlots(int count)
    {
        var opened = 0;
        for (var i = 0; i < SNet.Slots.PlayerSlots.Length && opened < count; i++)
            if (SNet.Slots.GetPlayerInSlot(i) == null && SNet.Slots.SetSlotPermission(i, SNet_PlayerSlotManager.SlotPermission.Bot)) opened++;
        return opened;
    }

    /// <summary>Holds <paramref name="agent"/> (a player agent that is not the local player: a bot or a remote player) as a puppet: its position,
    /// walk animation and aim come only from <see cref="PlayerPuppet.Drive"/>, and a bot's AI stays off while it is held.</summary>
    public static PlayerPuppet? Player(PlayerAgent agent, string owner)
    {
        if (NativeObjects.Gone(agent) || agent.Locomotion == null || agent.IsLocallyOwned) return null;
        return new PlayerPuppet(agent, owner);
    }
}

/// <summary>An enemy held by <see cref="Puppets.Enemy"/>.</summary>
public sealed class EnemyPuppet : IDisposable
{
    private readonly string _owner;
    private readonly AgentMode _mode;
    private readonly bool _rootMotion, _immortal, _noCulling, _keepState;
    private readonly float _speed;
    private readonly NavMeshAgent? _nav;
    private readonly bool _navEnabled;
    private readonly IntPtr _pointer;
    private bool _disposed;

    internal EnemyPuppet(EnemyAgent enemy, string owner)
    {
        Agent = enemy; _owner = owner;
        _mode = enemy.AI.Mode; _rootMotion = enemy.Anim.applyRootMotion; _immortal = enemy.Damage.IsImortal;
        _noCulling = enemy.ForceNoAnimationCulling; _speed = enemy.Anim.speed; _keepState = enemy.Anim.keepAnimatorControllerStateOnDisable;
        _nav = enemy.GetComponent<NavMeshAgent>(); _navEnabled = _nav != null && _nav.enabled;
        // A frozen animator is switched off (Rate 0) and must come back in the pose it stood in.
        enemy.Anim.keepAnimatorControllerStateOnDisable = true;
        Patches.Ensure(Modules.PuppetAttack, typeof(PuppetAttackHook));
        _pointer = enemy.Pointer;
        PuppetAttackHook.Held.Add(_pointer);
        // The locomotion state decides which animator state the enemy is in: PathMove is the walk blend (not every enemy has StandStill).
        // It is changed while the AI is still as the game left it, then the game's hands come off.
        try { enemy.Locomotion.ChangeState(ES_StateEnum.PathMove); }
        catch (Exception error) { Log.For(owner).ErrorOnce("core-puppet-enemy-could-not-leave-hibernation", "A puppet enemy could not leave its hibernation state: " + error); }
        Hold();
    }

    public EnemyAgent Agent { get; }

    /// <summary>The playback rate of its animation (1 normal, 0 frozen); the enemy's own speed returns on release. At 0 the animator is
    /// switched off as well (keeping its state), so nothing the game does while paused can move the frozen body (11-06: paused enemies
    /// jerked with the speed at 0 alone).</summary>
    public float Rate
    {
        set
        {
            if (!Held) return;
            var anim = Agent.Anim;
            anim.speed = value;
            var run = value != 0f;
            if (anim.enabled != run) anim.enabled = run;
        }
    }

    /// <summary>How many times the game had taken the enemy back (AI mode, nav mesh, root motion or immortality changed) and it was
    /// put under control again. Only counted, never logged: the game doing this to a puppet is expected. Zero in a clean run.</summary>
    public int Retaken { get; private set; }

    /// <summary>True while the enemy exists, lives and has not been released.</summary>
    public bool Held => !_disposed && !NativeObjects.Gone(Agent) && Agent.Alive;

    private void Hold()
    {
        var enemy = Agent;
        enemy.AI.Mode = AgentMode.Off;
        enemy.AI.SetNavMeshAgent(false);
        if (_nav != null) _nav.enabled = false;
        enemy.Anim.applyRootMotion = false;
        enemy.Damage.IsImortal = true;
        enemy.ForceNoAnimationCulling = true;
    }

    // The game's attack states turn root motion on themselves, so while an action plays that is not the game taking the enemy back;
    // the position is written every frame anyway, and root motion is switched off again when the action ends.
    private bool Free(EnemyAI ai, Animator anim, Dam_EnemyDamageBase damage) => ai.Mode != AgentMode.Off || (!_acting && anim.applyRootMotion) || !damage.IsImortal || (_nav != null && _nav.enabled);

    /// <summary>Puts the enemy at <paramref name="position"/> and <paramref name="rotation"/> and animates it as walking with
    /// <paramref name="velocity"/> (world metres per second). Call it after the game's animation each frame (<see cref="Frame.OnLateUpdate"/>),
    /// so the animator's own movement and the game's own writes of the inputs do not stay; the inputs are written as they are, not damped.</summary>
    public void Drive(Vector3 position, Quaternion rotation, Vector3 velocity)
    {
        if (!Held) return;
        // Every frame per puppet: the AI and animator are read once (each read is a native call and a new wrapper).
        var enemy = Agent;
        var ai = enemy.AI;
        var anim = enemy.Anim;
        var damage = enemy.Damage;
        // The game's hit reaction, attack and scream states send the enemy back to walking themselves when their own timer ends, and walking's
        // entry turns the nav mesh agent on again: the action is over, it is not the game taking the enemy back.
        if (_acting && enemy.Locomotion.CurrentStateEnum == _walkState) { _acting = false; Rehold(false); }
        // An action's state that wakes the AI (a hit reaction sets it to aggressive), and the wake-up pose (the game's HibernateWakeUp state
        // sets it to aggressive the same way), are what this puppet played, not the game taking the enemy back.
        if ((_acting || _pose == EnemyPose.Wake) && ai.Mode != AgentMode.Off) ai.Mode = AgentMode.Off;
        if (Free(ai, anim, damage))
        {
            Retaken++;
            Hold();
        }
        EndAction();
        if (!_acting) anim.applyRootMotion = false;
        // Asleep the body stays in the game's hibernation state; a state that left it by itself is put back.
        if (_pose == EnemyPose.Sleep && !_acting && enemy.Locomotion.CurrentStateEnum != ES_StateEnum.Hibernate)
        {
            Retaken++;
            Sleep();
        }
        enemy.transform.SetPositionAndRotation(position, rotation);
        // The game's own movement writes the agent's position and its moving culler (EnemyAgent.set_Position, C_MovingCuller.UpdatePosition):
        // the attack and hit reaction states start from that position, and the culler decides which lights see the body and cast its
        // shadow. A puppet left both at its spawn place.
        if (Vec.SqrLength(position - _placed) > 0.0001f)
        {
            _placed = position;
            enemy.Position = position;
            enemy.MovingCuller?.UpdatePosition(enemy.DimensionIndex, position);
        }
        // Asleep, waking, on a ladder or in a leap the body plays its own animation: the walk inputs would pull it back to the walk blend.
        if (_pose != EnemyPose.Walk && !_acting) return;
        var facing = rotation * Vec.Forward;
        PuppetMotion.Local(velocity.x, velocity.z, facing.x, facing.z, out var forward, out var right);
        anim.SetBool(EnemyLocomotion.s_hashMoveOnGround, true);
        anim.SetFloat(EnemyLocomotion.s_hashForward, forward);
        anim.SetFloat(EnemyLocomotion.s_hashRight, right);
    }

    // An action's state is the game's own (the state a client puts a remote enemy in when told); it runs for a while and the enemy is then
    // sent back to walking, the way the host's next state change would.
    private float _actionEnds;
    private bool _acting;
    private Vector3 _placed = new(float.PositiveInfinity, 0f, 0f);

    /// <summary>The enemy is playing an action started by <see cref="Attack"/>, <see cref="Hitreact"/> or <see cref="Scream"/>.</summary>
    public bool Acting => _acting;

    private enum EnemyPose : byte { Walk, Sleep, Wake, Climb, Leap }
    private EnemyPose _pose;
    private ES_StateEnum _walkState = ES_StateEnum.PathMove;

    /// <summary>Follows the game's locomotion state <paramref name="state"/> of the enemy this puppet shows, read each frame so that a jump in
    /// time lands in the right pose: asleep (the game's own hibernation state, with its breathing), waking (the game's wake-up animation), on a
    /// ladder and in a leap; any other state is walking, driven by <see cref="Drive"/>, and an action started by <see cref="Attack"/>,
    /// <see cref="Hitreact"/> or <see cref="Scream"/> plays to its end first. Waking, the ladder and the leap are the animator's states
    /// played directly, not the game's locomotion states: those would wake the sleepers around it (<c>ES_HibernateWakeUp</c>), hold on to
    /// the ladder or move the enemy themselves. <paramref name="variant"/> picks which of the wake-up animations: the game's own index when
    /// the caller knows it, with <paramref name="turn"/> for the turning set (<c>ES_HibernateWakeUp.DoWakeup</c> plays
    /// <c>s_hashHibernationWakeupTurns[index]</c> or <c>s_hashHibernationWakeups[index]</c>).</summary>
    public void Stance(ES_StateEnum state, int variant, bool turn = false)
    {
        if (!Held || _acting) return;
        var pose = state switch
        {
            ES_StateEnum.Hibernate => EnemyPose.Sleep,
            ES_StateEnum.HibernateWakeUp => EnemyPose.Wake,
            ES_StateEnum.ClimbLadder => EnemyPose.Climb,
            ES_StateEnum.Jump or ES_StateEnum.ShortcutJump => EnemyPose.Leap,
            _ => EnemyPose.Walk,
        };
        var walk = state == ES_StateEnum.PathMoveFlyer ? ES_StateEnum.PathMoveFlyer : _walkState;
        if (pose == _pose && walk == _walkState) return;
        var from = _pose;
        _pose = pose; _walkState = walk;
        try
        {
            var anim = Agent.Anim;
            if (pose == EnemyPose.Sleep) Sleep();
            else
            {
                // Out of the hibernation state, or back to walking: the walk state's entry plays the walk blend.
                if (pose == EnemyPose.Walk || from == EnemyPose.Sleep) Agent.Locomotion.ChangeState(walk);
                if (pose != EnemyPose.Walk)
                {
                    anim.SetBool(EnemyLocomotion.s_hashMoveOnGround, false);
                    var wakes = turn ? EnemyLocomotion.s_hashHibernationWakeupTurns : EnemyLocomotion.s_hashHibernationWakeups;
                    var hash = pose == EnemyPose.Climb ? EnemyLocomotion.s_hashClimbLadder
                        : pose == EnemyPose.Leap ? EnemyLocomotion.s_hashJumpStart
                        : wakes != null && wakes.Length > 0 ? wakes[Math.Abs(variant) % wakes.Length] : 0;
                    if (hash != 0) anim.CrossFadeInFixedTime(hash, 0.2f);
                }
                Rehold(false);
            }
            // Paused the animator is off at speed 0; a jump in time must still show the pose it lands in.
            if (anim.speed == 0f) { anim.enabled = true; anim.speed = 1f; anim.Update(3f); anim.speed = 0f; anim.enabled = false; }
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-take-pose", "A puppet enemy could not take the pose of " + state + ": " + error); }
    }

    private void Sleep()
    {
        Agent.Locomotion.ChangeState(ES_StateEnum.Hibernate);
        _detecting = false;
        Rehold(false);
    }

    private bool _detecting;

    /// <summary>A sleeper's heartbeat as the game plays it where it arrives (<c>ES_Hibernate.DoHeartbeat</c>: the glow, the beat's sound and
    /// animation, of <paramref name="strength"/> towards <paramref name="sensed"/>). Nothing while the puppet is not asleep.</summary>
    public void Heartbeat(float strength, Vector3 sensed)
    {
        if (!Held || _pose != EnemyPose.Sleep || _acting) return;
        try { Agent.Locomotion.Hibernate?.DoHeartbeat(strength, sensed); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-play-heartbeat", "A puppet enemy could not play a heartbeat: " + error); }
    }

    /// <summary>A sleeper sensing someone, or no longer (<c>ES_Hibernate.DoStartDetection</c> / <c>DoEndDetection</c>, as a client is told:
    /// the light sleep's glow, sound and stir). Only a change is passed on, and nothing while the puppet is not asleep.</summary>
    public void Detecting(bool on)
    {
        if (!Held || _pose != EnemyPose.Sleep || _acting || on == _detecting) return;
        _detecting = on;
        try
        {
            var hibernate = Agent.Locomotion.Hibernate;
            if (hibernate == null) return;
            if (on) hibernate.DoStartDetection(); else hibernate.DoEndDetection();
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-show-light", "A puppet enemy could not show its light sleep: " + error); }
    }

    private bool Begin(ES_StateEnum state, float seconds)
    {
        try
        {
            Agent.Locomotion.ChangeState(state);
            _acting = true;
            _pose = EnemyPose.Walk;
            // Game time: a time scale of 0 (a paused replay) holds the action where it is, as it holds the animation and the state's effects.
            _actionEnds = Time.time + Mathf.Clamp(seconds, 0.3f, 6f);
            return true;
        }
        catch (Exception error)
        {
            Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-enter", "A puppet enemy could not enter " + state + ": " + error);
            return false;
        }
    }

    /// <summary>Plays an attack the way a client sees one: the attack state is entered and told the attack start (the animation index it
    /// plays and where it aims). <paramref name="target"/> is the player puppet the attack is aimed at (null: none).</summary>
    public void Attack(ES_StateEnum state, Vector3 aim, int animIndex, float seconds, PlayerAgent? target)
    {
        if (!Held || !AbilityReady(state, out var ability) || !Begin(state, seconds)) return;
        try
        {
            var locomotion = Agent.Locomotion;
            ES_EnemyAttackBase? attack = state switch
            {
                ES_StateEnum.StrikerAttack => locomotion.StrikerAttack,
                ES_StateEnum.TankAttack => locomotion.TankAttack,
                ES_StateEnum.TankMultiTargetAttack => locomotion.TankMultiTargetAttack,
                ES_StateEnum.ShooterAttack => locomotion.ShooterAttack,
                _ => null,
            };
            if (attack == null) { Rehold(true); return; }
            var data = new pES_EnemyAttackData { Position = Agent.Position, TargetPosition = aim, AnimIndex = (byte)animIndex, Duration = seconds };
            if (target != null) data.TargetAgent.Set(target);
            // The state looks its ability up from the start data's type and index (as the host's message carries them) and overwrites the field with the result.
            if (ability != null) { data.AbilityType = ability.m_abilityType; data.AbilityIndex = (byte)ability.AbilityIndex; }
            attack.RecieveAttackStart(data);
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-start-attack", "A puppet enemy could not start its " + state + " attack: " + error); }
        Rehold(true);
    }

    // The game gives an attack state its ability when the enemy's AI is set up, which a puppet may never have had: the state's own code and whatever hooks it read that ability without looking.
    // The ability is the enemy's own component; an enemy with none does not enter the state.
    private bool AbilityReady(ES_StateEnum state, out EnemyAbility? ability)
    {
        var locomotion = Agent.Locomotion;
        ability = null;
        if (state == ES_StateEnum.StrikerAttack && locomotion.StrikerAttack?.TryCast<ES_StrikerAttack>() is { } striker)
        {
            if (striker.m_tentacleAbility == null) striker.m_tentacleAbility = Agent.GetComponentInChildren<EAB_MovingEnemeyTentacle>(true);
            ability = striker.m_tentacleAbility;
        }
        else if (state == ES_StateEnum.ShooterAttack && locomotion.ShooterAttack?.TryCast<ES_ShooterAttack>() is { } shooter)
        {
            if (shooter.m_projectileAbility == null) shooter.m_projectileAbility = Agent.GetComponentInChildren<EAB_ProjectileShooter>(true);
            ability = shooter.m_projectileAbility;
        }
        else return true;
        return ability != null;
    }

    /// <summary>Plays a hit reaction (the game's reaction type, side and animation index).</summary>
    public void Hitreact(ES_HitreactType type, ImpactDirection direction, int animIndex)
    {
        if (!Held || !Begin(ES_StateEnum.Hitreact, 1f)) return;
        try
        {
            var react = Agent.Locomotion.Hitreact?.TryCast<ES_Hitreact>();
            if (react == null) return;
            var forward = Agent.transform.forward;
            react.RecieveStateData(new pES_HitreactData
            {
                StartPosition = Agent.Position,
                RotationDir = forward,
                ReactionType = type,
                ReactionDirection = direction,
                AnimationIndex = (byte)animIndex,
                DeathDelay = 0,
                Propagated = false,
                DamagePos = Agent.Position + forward,
            });
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-play-hit", "A puppet enemy could not play a hit reaction: " + error); }
        Rehold(true);
    }

    /// <summary>Plays a scream animation (<paramref name="animIndex"/> picks which).</summary>
    public void Scream(int animIndex, float seconds)
    {
        if (!Held || !Begin(ES_StateEnum.Scream, seconds)) return;
        try { Agent.Locomotion.Scream?.TryCast<ES_Scream>()?.OnStateShouldBeActivated(new pES_EnemyScreamData { AnimIndex = (byte)animIndex }); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-scream", "A puppet enemy could not scream: " + error); }
        Rehold(true);
    }

    /// <summary>A scout's feelers out (<paramref name="spread"/>) or back. Out spawns the feelers the way the scout's own detection state does
    /// (<c>ES_ScoutDetection.SpawnAntennaDetection</c>, which places them at the head with a random spread) and gives them nobody to find,
    /// so they reach out, touch nothing and come back by themselves; back pulls them in as the state's exit does (<c>SetState(PullingBack)</c>).
    /// <paramref name="remove"/> takes them away at once (a jump). Nothing for an enemy that is not a scout.</summary>
    public void Feelers(bool spread, bool remove = false)
    {
        if (!Held) return;
        try
        {
            var scout = Agent.Locomotion.ScoutDetection;
            if (scout == null) return;
            var feelers = scout.m_antennaDetection;
            if (remove || spread) { if (feelers != null) feelers.Despawn(); }
            else if (feelers != null && feelers.State is eDetectionState.WaitingOut) feelers.SetState(eDetectionState.PullingBack, false);
            if (remove || !spread) return;
            scout.SpawnAntennaDetection();
            scout.m_antennaDetection?.SetTargets(new Il2CppSystem.Collections.Generic.List<Agents.AgentTarget>());
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-scout-could-not-show-feelers", "A puppet scout could not show its feelers: " + error); }
    }

    /// <summary>Shows damage limb <paramref name="limbId"/> destroyed the way a client is told of it: the game's
    /// <c>Dam_EnemyDamageBase.ReceiveDestroyLimb</c> hands the packet to the limb's own <c>DestroyLimb</c>, which is called here, so the
    /// limb's destruction (its mesh cut away) and its destroy effect show and no damage is settled. With <paramref name="instant"/> the
    /// limb's destroy effect (<c>m_effectOnDestroy</c> of a custom limb) is left out, for a state set at once. True when the limb is
    /// destroyed now (also when it already was); false when the enemy has no such limb or the game's call failed (one error line).</summary>
    public bool DestroyLimb(int limbId, bool instant)
    {
        if (!Held) return false;
        try
        {
            var limbs = Agent.Damage.DamageLimbs;
            if (limbs == null || limbId < 0 || limbId >= limbs.Length || limbs[limbId] is not { } limb) return false;
            if (limb.IsDestroyed) return true;
            var custom = instant ? limb.TryCast<Dam_EnemyDamageLimb_Custom>() : null;
            var effect = custom?.m_effectOnDestroy;
            if (effect != null) custom!.m_effectOnDestroy = null;
            try { limb.DestroyLimb(default); }
            finally { if (effect != null) custom!.m_effectOnDestroy = effect; }
            return true;
        }
        catch (Exception error)
        {
            Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-show-limb", "A puppet enemy could not show limb " + limbId + " destroyed: " + error);
            return false;
        }
    }

    private void EndAction()
    {
        if (_acting && Time.time >= _actionEnds) Settle();
    }

    // A state this puppet enters itself (an action, back to walking) may switch the AI, the nav mesh agent or root motion on in the state's
    // own entry; that is the puppet's doing, not the game taking the enemy back, so the hold is put back at once (an action keeps the root
    // motion its state set).
    private void Rehold(bool keepRootMotion)
    {
        if (!Held) return;
        var root = Agent.Anim.applyRootMotion;
        Hold();
        if (keepRootMotion) Agent.Anim.applyRootMotion = root;
    }

    /// <summary>Ends an action (<see cref="Attack"/>, <see cref="Hitreact"/>, <see cref="Scream"/>) at once and goes back to walking, for a
    /// jump in time: the moment it lands in has no action going.</summary>
    public void Settle()
    {
        if (!Held || !_acting) return;
        _acting = false;
        try { Agent.Locomotion.ChangeState(_walkState); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-enemy-could-not-go-back", "A puppet enemy could not go back to walking: " + error); }
        Rehold(false);
    }

    /// <summary>Kills the enemy through the game's own death (its death animation or ragdoll); the puppet ends. False when it was not held or still lives after it, for the caller to take out.</summary>
    public bool Die()
    {
        if (!Held) return false;
        PuppetAttackHook.Held.Remove(_pointer);
        Agent.Anim.enabled = true;
        Agent.Damage.IsImortal = false;
        // The game's instant death is a melee hit of 1000: an enemy with more health (a Tank) would keep standing, its death sound played.
        Agent.Damage.Health = 0.01f;
        Agent.Damage.InstantDead(true);
        _disposed = true;
        return !Agent.Alive;
    }

    /// <summary>Gives the enemy back to the game with the settings it had when it was taken.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PuppetAttackHook.Held.Remove(_pointer);
        if (NativeObjects.Gone(Agent) || !Agent.Alive) return;
        Agent.Damage.IsImortal = _immortal;
        Agent.Anim.enabled = true; Agent.Anim.keepAnimatorControllerStateOnDisable = _keepState;
        Agent.Anim.applyRootMotion = _rootMotion; Agent.Anim.speed = _speed;
        Agent.ForceNoAnimationCulling = _noCulling;
        if (_nav != null) _nav.enabled = _navEnabled;
        Agent.AI.Mode = _mode;
    }
}

/// <summary>A player agent held by <see cref="Puppets.Player"/>.</summary>
public sealed class PlayerPuppet : IDisposable
{
    private readonly string _owner;
    private bool _disposed;
    private readonly float _speed;
    private readonly CullingSystem.C_MovingCuller? _culler;
    private readonly AnimatorCullingMode _culling;
    private readonly PlayerAIBot? _bot;
    private readonly PlayerLocomotion _locomotion;
    private readonly PlayerSync _sync;
    private readonly Animator? _body;
    private readonly IntPtr _pointer, _locomotionPointer;
    private InventorySlot _wielded;
    private bool _hasWielded;
    private float _nextWield;

    internal PlayerPuppet(PlayerAgent agent, string owner)
    {
        Agent = agent; _owner = owner; _speed = agent.AnimatorBody != null ? agent.AnimatorBody.speed : 1f;
        _locomotion = agent.Locomotion; _sync = agent.Sync; _bot = agent.GetComponent<PlayerAIBot>(); _body = agent.AnimatorBody;
        // The game draws a player's clothes only while the node it thinks the player stands in is visible; a puppet's body is always shown (its gear never hides).
        _culler = agent.GetComponentInChildren<CullingSystem.C_MovingCuller>(true);
        if (_culler != null && _culler.Culler != null) { _culler.Culler.Hidable = false; _culler.Culler.Show(); }
        if (agent.AnimatorBody != null) { _culling = agent.AnimatorBody.cullingMode; agent.AnimatorBody.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
        // The game's own switch for the bots' decision making. It does not last: see Drive.
        PlayerManager.Current.DisableBotAI();
        // The synced-player update writes the agent's place, walk animation and aim from the last network data each frame; Drive is the only writer here.
        Patches.Ensure(Modules.PuppetMovement, typeof(PuppetMovementHook));
        _locomotionPointer = _locomotion.Pointer;
        PuppetMovementHook.Held.Add(_locomotionPointer);
        _pointer = agent.Pointer;
        Puppets.HeldPlayers.Add(_pointer);
        // A player still in the elevator state may not wield (PLOC_InElevator.CommonExit opens that) and plays the seated animation.
        if (_locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.InElevator) _locomotion.ChangeState(PlayerLocomotion.PLOC_State.Stand);
    }

    public PlayerAgent Agent { get; }

    public bool Held => !_disposed && !NativeObjects.Gone(Agent);

    /// <summary>The playback rate of the body's animation (1 normal, 0 frozen); the body's own speed returns on release.</summary>
    public float Rate { set { if (Held && Agent.AnimatorBody != null) Agent.AnimatorBody.speed = value; } }

    /// <summary>Puts the player at <paramref name="position"/>, looking along <paramref name="lookDirection"/>, animated with the game's walk
    /// values <paramref name="forward"/> and <paramref name="right"/> (metres per second in the player's own frame, reached at the game's own
    /// rate) and crouched by <paramref name="crouch"/> (0 standing, 1 crouched).</summary>
    public void Drive(Vector3 position, Vector3 lookDirection, float forward, float right, float crouch)
    {
        if (!Held) return;
        // PLOC_Stand.SyncEnter turns a bot's AI back on on the host whenever the agent enters Stand, and a running AI feeds its own
        // stale state and place through PlayerSync.IncomingLocomotion. SetEnabled does nothing while the AI is already off.
        _bot?.SetEnabled(false);
        // The hands hold what was asked: a wield the game refused (not allowed yet) or undid (new gear coming up) is asked for again.
        // The slot alone is not enough: gear put into the slot in the hands leaves the hands holding the item it replaced.
        if (_hasWielded && _heldItem == null && _special == PlayerLocomotion.PLOC_State.Stand && Frame.UnscaledTime >= _nextWield && Agent.Inventory != null)
        {
            _nextWield = Frame.UnscaledTime + 0.5f;
            if (!HoldsWielded()) _sync.WantsToWieldSlot(_wielded, false);
        }
        // What the game's update held off by PuppetMovementHook does first each frame, while walking.
        if (_body != null && _special == PlayerLocomotion.PLOC_State.Stand) { _body.applyRootMotion = false; _body.enabled = true; }
        _locomotion.SyncedSetPosition(position);
        if (Vec.SqrLength(lookDirection) > 1e-6f)
        {
            _locomotion.SyncedSetRotation(Quaternion.LookRotation(new Vector3(lookDirection.x, 0f, lookDirection.z)));
            if (_special == PlayerLocomotion.PLOC_State.Stand)
            {
                // The aim is the game's own: it reads the look direction of the network data.
                var data = _sync.m_locomotionData;
                data.LookDir.Value = lookDirection.normalized;
                _sync.m_locomotionData = data;
                _locomotion.UpdateLookDirAimSync(false);
            }
        }
        _locomotion.UpdateFullBodyAnimValue(forward, right);
        _locomotion.SetCrouch(crouch);
        Keep();
        if (_special == PlayerLocomotion.PLOC_State.Downed && _body != null)
        {
            if (_body.GetCurrentAnimatorStateInfo(BodyMoveLayer).shortNameHash != LyingState) LieDown();
        }
    }

    private const int BodyMoveLayer = 1;

    /// <summary>The state of the game's player body controller a downed player lies in, on its move layer, read back from a downed player
    /// (the states named Die and Dead do not hold: playing the first does nothing and the second leaves at once).</summary>
    public const int LyingState = 83611764;

    /// <summary>A downed player lies on the floor at once, on the last frame of the fall (a jump lands there, it does not play it). Nothing while the player is not down.</summary>
    public void LieDown()
    {
        if (!Held || _special != PlayerLocomotion.PLOC_State.Downed || _body == null) return;
        _body.Play(LyingState, BodyMoveLayer, 1f);
        SettleWhilePaused();
    }

    // The downed state State put the player in, left by the game itself: the puppet's health never ran out, so the game's own rules can send
    // it back to standing while the recording still has it on the floor. It is entered again, at most twice a second, and only counted.
    private float _keepAt;
    private void Keep()
    {
        if (_special != PlayerLocomotion.PLOC_State.Downed || _locomotion.m_currentStateEnum == _special || Frame.UnscaledTime < _keepAt) return;
        _keepAt = Frame.UnscaledTime + 0.5f;
        Reentered++;
        try { _locomotion.ChangeState(_special); LieDown(); _bot?.SetEnabled(false); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-enter-again", "A puppet player could not enter " + _special + " again: " + error); }
    }

    /// <summary>How many times a state <see cref="State"/> put the player in had been left by the game and was entered again.</summary>
    public int Reentered { get; private set; }

    // The hands hold the slot asked for, and the item there is the one the backpack has in that slot now.
    private bool HoldsWielded()
    {
        var inventory = Agent.Inventory;
        if (inventory.WieldedSlot != _wielded) return false;
        var held = inventory.WieldedItem;
        if (Agent.Owner == null || !PlayerBackpackManager.TryGetBackpack(Agent.Owner, out var backpack) || backpack == null
            || !backpack.TryGetBackpackItem(_wielded, out var item) || item?.Instance == null) return true;
        return held != null && held.Pointer == item.Instance.Pointer;
    }

    /// <summary>Puts the inventory slot <paramref name="slot"/> in the player's hands for everyone to see (no gear is changed). Asked for
    /// again while the game has not done it. <c>PlayerSync.WantsToWieldSlot</c> with its broadcast-only flag sends the packet and does
    /// nothing on this machine, where the puppet stands.</summary>
    public void Wield(InventorySlot slot)
    {
        if (!Held) return;
        _wielded = slot; _hasWielded = true;
        _sync.WantsToWieldSlot(slot, false);
    }

    /// <summary>Whether the game's inventory of this body is in the middle of putting an item in the hands (the item still hangs on the body
    /// where it is kept). The equip runs on the game's clock, so a time scale of 0 never ends it.</summary>
    public bool IsEquipping => Held && Agent.Inventory?.TryCast<PlayerInventorySynced>() is { IsEquiping: true };

    /// <summary>Ends the equip the game is in the middle of at once (<c>PlayerInventorySynced.FinishOngoingAction</c>, what the game itself runs
    /// before it starts the next one), so the item is in the hands while the world is held at time scale 0. Nothing when no equip runs.</summary>
    public void FinishEquip()
    {
        if (!Held) return;
        try { if (Agent.Inventory?.TryCast<PlayerInventorySynced>() is { IsEquiping: true } inventory) inventory.FinishOngoingAction(); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-finish-equip", "A puppet player's equip could not be finished: " + error); }
    }

    private ItemEquippable? _heldItem;

    /// <summary>Puts <paramref name="item"/>, a third-person item the backpack does not have (a resource pack, a consumable), in the hands the way the game does for a remote player: its model in
    /// the hand and the body's animation for it. Null: back to what <see cref="Wield"/> asked for. False when the game refused it.</summary>
    public bool HoldItem(ItemEquippable? item)
    {
        if (!Held) return false;
        if (item == null)
        {
            if (_heldItem == null) return true;
            _heldItem = null;
            if (_hasWielded) _sync.WantsToWieldSlot(_wielded, false);
            return true;
        }
        try { Agent.Inventory.SyncWieldItem(item); _heldItem = item; return true; }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-play", "A puppet player could not hold " + item.name + ": " + error); return false; }
    }

    /// <summary>The player's flashlight on or off, silently.</summary>
    public void Flashlight(bool on)
    {
        if (!Held || Agent.Inventory == null || Agent.Inventory.FlashlightEnabled == on) return;
        try { Agent.Inventory.SetFlashlightEnabled(on, true, false); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-players-flashlight", "A puppet player's flashlight could not be set: " + error); }
    }

    /// <summary>Plays the third-person animation of a generic interaction (reload, shove, place a mine, point, pick up, use a resource)
    /// the way the game does for a remote player.</summary>
    public void Interact(pGenericInteractAnimation.TypeEnum type)
    {
        if (!Held) return;
        try { Agent.Inventory.SyncGenericInteract(type); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-play", "A puppet player could not play " + type + ": " + error); }
    }

    /// <summary>Plays the melee weapon's swing the way the game does for a remote player (its third-person animation and swing sound), from a
    /// charge first when <paramref name="charged"/>. False when the hands hold no melee weapon.</summary>
    public bool MeleeSwing(bool charged)
    {
        if (!Held) return false;
        try
        {
            var melee = Agent.Inventory?.WieldedItem?.TryCast<global::Gear.MeleeWeaponThirdPerson>();
            if (melee == null) return false;
            if (charged) melee.ReceiveClassItemSync(new PlayerInventoryBase.pSimpleItemSyncData { inAimMode = true });
            melee.ReceiveClassItemSync(new PlayerInventoryBase.pSimpleItemSyncData { inFireMode = true });
            return true;
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-swing-melee", "A puppet player could not swing its melee weapon: " + error); return false; }
    }

    /// <summary>Starts the melee weapon's charge, the way the game does for a remote player; <see cref="MeleeSwing"/> releases it.</summary>
    public void MeleeCharge()
    {
        if (!Held) return;
        try { Agent.Inventory?.WieldedItem?.TryCast<global::Gear.MeleeWeaponThirdPerson>()?.ReceiveClassItemSync(new PlayerInventoryBase.pSimpleItemSyncData { inAimMode = true }); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-charge-melee", "A puppet player could not charge its melee weapon: " + error); }
    }

    /// <summary>The player says one of the game's voice lines, through its own voice (<c>PlayerVoice.SayLine</c>, what every machine runs when told).</summary>
    public void Say(uint eventId, uint seed, uint inDialog, uint startDialog, uint subtitle)
    {
        if (!Held || Agent.Voice == null) return;
        try { Agent.Voice.SayLine(eventId, seed, inDialog, startDialog, subtitle); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-say-line", "A puppet player could not say a line: " + error); }
    }

    /// <summary>Plays the throw animation (charging, thrown).</summary>
    public void Throw(pPlayerThrowStatus.StatusEnum status)
    {
        if (!Held) return;
        try { Agent.Inventory.SyncThrowState(status); }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-play-throw", "A puppet player could not play a throw: " + error); }
    }

    /// <summary>Shows the muzzle flash of the weapon in the player's hands (no shot, no sound). False when the hands hold no weapon with one.</summary>
    public bool MuzzleFlash(out Vector3 muzzle)
    {
        muzzle = Agent.Position;
        if (!Held) return false;
        try
        {
            var weapon = Agent.Inventory.WieldedItem?.TryCast<Weapon>();
            if (weapon == null || weapon.m_muzzleFlash == null) return false;
            weapon.m_muzzleFlash.Play();
            if (weapon.MuzzleAlign != null) muzzle = weapon.MuzzleAlign.position;
            return true;
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-players-muzzle-flash", "A puppet player's muzzle flash could not play: " + error); return false; }
    }

    // Paused the body's animator stands at speed 0; a jump in time must still show the pose of the state it lands in.
    private void SettleWhilePaused()
    {
        var body = Agent.AnimatorBody;
        if (body == null || body.speed != 0f) return;
        body.speed = 1f; body.Update(3f); body.speed = 0f;
    }
    private PlayerLocomotion.PLOC_State _special = PlayerLocomotion.PLOC_State.Stand;

    /// <summary>Puts the player in a state of the game's own (jump, fall, land, downed, at a terminal, held by a tank's tentacle, on the ladder
    /// nearest to it) and takes it out again when <paramref name="state"/> is <c>Stand</c>; the player's walking and crouching are not states
    /// entered here. A ladder goes through the game's own remote-player entry, which needs a ladder of the level near the player; without one the
    /// player keeps standing.</summary>
    public void State(PlayerLocomotion.PLOC_State state)
    {
        if (!Held || state == _special) return;
        var downed = _special == PlayerLocomotion.PLOC_State.Downed;
        _special = state;
        try
        {
            if (state != PlayerLocomotion.PLOC_State.ClimbLadder) _locomotion.ChangeState(state);
            else if (NearestLadder(Agent.Position) is { } ladder) _sync.IncomingClimbLadder(new pPlayerClimbLadder { LadderID = ladder.SyncID });
            else
            {
                Log.For(_owner).ErrorOnce("A puppet player could not climb: the level has no ladder within " + LadderReach + " m of it.");
                _special = PlayerLocomotion.PLOC_State.Stand; return;
            }
            _bot?.SetEnabled(false);
            // PLOC_Downed.CommonEnter unwields and SyncEnter zeroes the weapon layers. A remote player's machine sends the wield again after the
            // revive; here that is this call, done on this machine. Empty hands fall back to Default_Movement, the arms-out pose.
            if (downed && _hasWielded) _sync.WantsToWieldSlot(_wielded, false);
            SettleWhilePaused();
        }
        catch (Exception error) { Log.For(_owner).ErrorOnce("core-puppet-player-could-not-enter", "A puppet player could not enter " + state + ": " + error); }
    }

    private const float LadderReach = 6f;

    // The ladder a player is on: nearest on the flat, since a ladder's centre is far above or below a climber.
    private static global::LG_Ladder? NearestLadder(Vector3 at)
    {
        global::LG_Ladder? best = null; var nearest = LadderReach * LadderReach;
        foreach (var ladder in LevelLadders())
        {
            if (NativeObjects.Gone(ladder)) { _ladders = null; continue; }
            var to = ladder.transform.position - at; to.y = 0f;
            if (Vec.SqrLength(to) < nearest) { nearest = Vec.SqrLength(to); best = ladder; }
        }
        return best;
    }

    // The level's ladders, found once per level (the scan is the whole scene); a destroyed entry or the end of the level drops the list.
    private static global::LG_Ladder[]? _ladders;

    private static global::LG_Ladder[] LevelLadders()
    {
        if (_ladders == null)
        {
            GameEvents.LevelEnded -= ForgetLadders; GameEvents.LevelEnded += ForgetLadders;
            var found = new List<global::LG_Ladder>();
            foreach (var ladder in UnityEngine.Object.FindObjectsOfType<global::LG_Ladder>())
                if (ladder != null && !ladder.m_enemyClimbingOnly) found.Add(ladder);
            _ladders = found.ToArray();
        }
        return _ladders;
    }

    private static void ForgetLadders() => _ladders = null;

    /// <summary>Gives the agent's movement back. The bot AI stays off: a released puppet is only ever one whose level is ending.</summary>
    public void Dispose()
    {
        if (Held && Agent.AnimatorBody != null) { Agent.AnimatorBody.speed = _speed; Agent.AnimatorBody.cullingMode = _culling; }
        if (Held && _culler != null && _culler.Culler != null) _culler.Culler.Hidable = true;
        // Also when the game destroyed the player first: a freed address must not keep skipping a later player's movement.
        PuppetMovementHook.Held.Remove(_locomotionPointer);
        Puppets.HeldPlayers.Remove(_pointer);
        _disposed = true;
    }
}

/// <summary>Holds the synced-player movement off for a puppet: called from the update of the walking states, it would put the agent at the
/// place of the last network data and pull its walk animation to that data's values, against <see cref="PlayerPuppet.Drive"/>.</summary>
[HarmonyLib.HarmonyPatch(typeof(PlayerLocomotion), nameof(PlayerLocomotion.UpdateMovementFromSync))]
internal static class PuppetMovementHook
{
    internal static readonly System.Collections.Generic.HashSet<IntPtr> Held = new();

    private static bool Prefix(PlayerLocomotion __instance) => !Held.Contains(__instance.Pointer);
}

/// <summary>Holds a puppet enemy's own launches off: whoever holds puppets is the host, so the attack states a puppet plays
/// (<see cref="EnemyPuppet.Attack"/>) run the host's part of the attack, which fires the shooter's projectiles (<c>FireBurst</c>) and sends
/// the striker's and the tank's tentacles (<c>EAB_MovingEnemeyTentacle.Attack</c>) at whoever is near. A caller shows its own launches
/// through the tentacle itself (<c>MovingEnemyTentacleBase.Attack</c>) or as projectiles of its own. Skipped, <c>FireBurst</c> answers 0.</summary>
[HarmonyLib.HarmonyPatch]
internal static class PuppetAttackHook
{
    internal static readonly System.Collections.Generic.HashSet<IntPtr> Held = new();

    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return HarmonyLib.AccessTools.Method(typeof(EAB_ProjectileShooter), nameof(EAB_ProjectileShooter.FireBurst));
        yield return HarmonyLib.AccessTools.Method(typeof(EAB_MovingEnemeyTentacle), nameof(EAB_MovingEnemeyTentacle.Attack), new[] { typeof(Agent) });
        yield return HarmonyLib.AccessTools.Method(typeof(EAB_MovingEnemeyTentacle), nameof(EAB_MovingEnemeyTentacle.Attack), new[] { typeof(Vector3) });
        yield return HarmonyLib.AccessTools.Method(typeof(EAB_MovingEnemyTentacleMultiple), nameof(EAB_MovingEnemyTentacleMultiple.Attack), new[] { typeof(int), typeof(Agent), typeof(int) });
        yield return HarmonyLib.AccessTools.Method(typeof(EAB_MovingEnemyTentacleMultiple), nameof(EAB_MovingEnemyTentacleMultiple.Attack), new[] { typeof(int), typeof(Vector3), typeof(int) });
    }

    private static bool Prefix(EnemyAbility __instance) => Held.Count == 0 || __instance.m_owner is not { } owner || !Held.Contains(owner.Pointer);
}
