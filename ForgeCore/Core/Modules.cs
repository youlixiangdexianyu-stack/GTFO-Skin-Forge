using System;

namespace ForgeCore;

/// <summary>The game methods each module patches. A module's patches go on the first time it is used; at startup ForgeCore only
/// checks that the methods exist, and a module whose method is missing fails its calls explicitly. This is the one table the
/// website manifest will be generated from.</summary>
internal static class Modules
{
    internal readonly record struct Target(string Type, string Method);

    /// <summary>The public entries of the library, one per module, with what each does. The website manifest lists these, and the
    /// manifest export refuses an entry that is no public type of the library.</summary>
    internal static readonly (string Entry, string Summary)[] Public =
    {
        ("Assets", "Loads asset bundles (each file once, reused afterwards, or over several frames for a caller that unloads), returns assets and shader bytes; a bad bundle or a missing asset fails explicitly and is logged."),
        ("Log", "A log source per mod ID; only errors are written, the same error once with counts at 1, 10 and 100; formats a number or a position for a message."),
        ("Settings", "Binds BepInEx configuration entries per mod ID and tells the mod when one changes at run time."),
        ("Pool", "A fixed-capacity object pool that prewarms on demand and allocates nothing on take or return."),
        ("Net", "Per-mod network channels with automatic message-format fingerprints (a mismatching player is refused on both sides), reliable or droppable delivery, host broadcast, point to point, per-player lobby data and late-join resend."),
        ("Store", "Local progress as one JSON file per mod; a file that cannot be read is reported and never overwritten."),
        ("InputLease", "Takes over game input actions for the local player; leases stack and are released at level cleanup and when the player goes down."),
        ("GameEvents", "Main-thread events for host change, text language, end of a level, checkpoint reload, the local player going down, the lobby opening or closing and the expedition being chosen (host and clients)."),
        ("Enemies", "Tracks live enemies, queries nearby enemies (also only those in sight, with their aim point), reads the four alert levels, requests enemy spawns, leases slowdowns and slows every enemy inside a zone for a time."),
        ("ColliderQuery", "Sphere queries over colliders with a reusable buffer; a full buffer is reported as truncated, never as complete."),
        ("Level", "Finds the course node and dimension of a position, finds a free stand spot near an object, finds a walkable path, a line of sight and a blocked move between points, looks up dimensions, zones and areas, finds the in-level agent of a player by lookup, and reads the active expedition."),
        ("HoldPoint", "The game's timed hold point (Interact_Timed): sets up the prompt, duration and callbacks, and shuts a point so it stays shut when the game switches it on again, until it is opened."),
        ("Il2CppArrays", "Copies whole game arrays to and from managed arrays and uploads texture pixels in one write."),
        ("PlayerStats", "The local player's walk speed, carry slowdown, jump, stamina cost and hit flinch through the game's own paths; each hook goes on with its first subscriber and every subscriber is asked."),
        ("CarrySlot", "Moves the heavy object of the in-level carry slot between the local player's hands and back through the game's synced wield request, drops one on the back when the player falls, shows another player's on their body, and sets a heavy object's pick-up hold time."),
        ("PlayerNoise", "Makes sleeping enemies hear a player's movement noise as a multiple of its distance; leases end with the level."),
        ("Combat", "The game's own damage, push, melee, explosion, force and noise requests, reporting what the game actually settled, a bullet kill event, a shot from a point at a collider settled as a stock bullet, the bullet ray that steps over the shooter's own colliders, plus which receiver a collider belongs to."),
        ("Gear", "Swaps the local player's gun with its assets preloaded, writes ammunition back through the game, arbitrates the special weapon slot, and limits which gears players may choose (one patch on the game's slot offer and its local equip entry for every package)."),
        ("PlayerHits", "The one patch on the game's player hit path (OnIncomingDamage, ModifyInfection): mods redirect the damage of a hit, refuse an infection write or read both through registrations instead of patching the methods themselves."),
        ("Compact", "Allocation-free compact encoding: variable-length and zig-zag integers, deltas, range quantization and smallest-three quaternions; a malformed read throws, never guesses."),
        ("ChunkLog", "An append-only file of independently compressed, checksummed chunks with a trailing index; after a crash the reader scans chunk headers and keeps every whole chunk, and another format version is refused with the difference named."),
        ("Items", "Spawns an item pickup the way the game does, in play as well as before it; the Spawned event reports every item the game spawns, so no other package patches the game's spawn method."),
        ("SoloDrop", "Starts one chosen expedition as a private host with chosen seeds, alone or with up to three of the game's bots, through the game's own menu calls; stops at the first unexpected state and never retries."),
        ("InstalledPlugins", "The loaded BepInEx plugins with version and DLL hash, and one fingerprint over them."),
        ("DataBlocks", "Adds new gear and item data blocks to the game's tables; an ID already in use is refused."),
        ("Frame", "Per-frame callbacks for mods (update, late update, quit) run from ForgeCore's one driver; a callback that throws is reported once and never stops the others."),
        ("Patches", "Puts a mod's own Harmony patches on at first use and keeps them, so a session that never uses the feature never pays for them."),
        ("StrictJson", "Strict reading of a JSON object: unknown or repeated fields are refused, a wrong type names its field, a number outside its range is refused, never clamped; also canonical JSON."),
        ("GameHooks", "The game methods that more than one package reads, each patched here once and handed to every subscriber around the game's own body: door, reactor, scan, container, generator, pickup and terminal state callbacks, noise, scout and wave events, a player's damage receive entries, a revive, the four firing bodies and the weapon ray casts, push, sentry placement and every sentry lifecycle step, mine placement, item spawn, level cleanup, marker and prefab placement, survival waves, the end and the failure check of an expedition, enemy setup, death, despawn, destruction, mode change, spawn sync, limb break, received damage and the bullet and melee receive entries, the local player's death, the bullet hit entry, weapon wield and item unwield, dry fire, gear assembly (also a synced gun's), reload start and end, the attached flashlight switch, the C-foam burst, a throwable's throw, the deployed marker, sentry spawn, the objective panel and intel messages, host migration, and the game event log; no package patches these methods itself."),
        ("Puppets", "Holds an enemy or a non-local player agent as a puppet: its own control is switched off and the caller drives place, animation, states and actions through the game's own entries (an enemy's sleep, heartbeat, light sleep and exact wake-up animation among them); an enemy puppet's own projectile and tentacle launches are held off; a take-back by the game is put right and counted."),
        ("Fnv", "FNV-1a 32 and 64 bit over UTF-8 text or bytes, the one hash behind wire formats and saved keys."),
        ("DeterministicRandom", "Random numbers that are a pure function of their keys (SplitMix64): every peer and a replay draw the same value, and the game's own random sequence is untouched."),
        ("Hash", "SHA-256 as lowercase hex over bytes, UTF-8 text or a stream, the one spelling of a content hash."),
    };

    internal const string SpecialSlot = "Gear.SpecialSlot";
    internal const string GearReload = "Gear.Reload";
    internal const string GearOffers = "Gear.Offers";
    internal const string GearParts = "Gear.Parts";
    internal const string PlayerNoise = "PlayerNoise";
    internal const string PlayerHits = "PlayerHits";
    internal const string PlayerWalk = "PlayerStats.Walk";
    internal const string PlayerJump = "PlayerStats.Jump";
    internal const string PlayerRise = "PlayerStats.Rise";
    internal const string PlayerStamina = "PlayerStats.Stamina";
    internal const string PlayerFlinch = "PlayerStats.Flinch";
    internal const string Language = "GameEvents.Language";
    internal const string ExpeditionSelected = "GameEvents.ExpeditionSelected";
    internal const string ItemSpawned = "Items.Spawned";
    internal const string ReplicationMisses = "Net.ReplicationMisses";
    public const string PuppetMovement = "Puppets.MovementHold";
    internal const string PuppetAttack = "Puppets.AttackHold";
    internal const string HoldPointShut = "HoldPoint.Shut";
    internal const string StackedVolumes = "Level.StackedVolumes";
    internal const string InteractionSwitches = "InputLease.Interactions";

    internal static readonly (string Module, Target[] Targets)[] Patched =
    {
        (SpecialSlot, new[] { new Target("Player.PlayerAmmoStorage", "PickupAmmo"), new Target("Player.PlayerAmmoStorage", "NeedWeaponAmmo") }),
        (GearReload, new[] { new Target("Gear.BulletWeapon", "get_ReloadTime") }),
        (GearOffers, new[] { new Target("Gear.GearManager", "GetAllGearForSlot"), new Target("Player.PlayerBackpackManager", "EquipLocalGear") }),
        (GearParts, new[] { new Target("Gear.GearPartSpawner+_SpawnAllParts_d__15", "MoveNext") }),
        (PlayerHits, new[] { new Target("Dam_PlayerDamageBase", "OnIncomingDamage"), new Target("Dam_PlayerDamageBase", "ModifyInfection") }),
        (PlayerNoise, new[] { new Target("Enemies.EnemyDetection", "UpdateDirectionDistanceToTarget_Hibernate") }),
        (PlayerWalk, new[] { new Target("PLOC_Base", "GetHorizontalVelocityFromInput") }),
        (PlayerJump, new[] { new Target("PLOC_Jump", "Enter") }),
        (PlayerRise, new[] { new Target("PLOC_Base", "UpdateVerticalVelocityInAir") }),
        (PlayerStamina, new[] { new Target("PlayerStamina", "UseStamina") }),
        (PlayerFlinch, new[] { new Target("Dam_PlayerDamageLocal", "Hitreact") }),
        ("GameHooks.EnemyBulletReceived", new[] { new Target("Dam_EnemyDamageBase", "ReceiveBulletDamage") }),
        ("GameHooks.EnemyMeleeReceived", new[] { new Target("Dam_EnemyDamageBase", "ReceiveMeleeDamage") }),
        (Language, new[] { new Target("Localization.GameDataTextLocalizationService", "SetCurrentLanguage") }),
        (ExpeditionSelected, new[] { new Target("RundownManager", "MasterSelectActiveExpedition"), new Target("RundownManager", "SetActiveExpedition") }),
        (HoldPointShut, new[] { new Target("Interact_Timed", "SetActive") }),
        ("GameHooks.WeakDoorState", new[] { new Target("LevelGeneration.LG_WeakDoor", "OnSyncDoorStateChange") }),
        ("GameHooks.ReactorState", new[] { new Target("LevelGeneration.LG_WardenObjective_Reactor", "OnStateChange") }),
        ("GameHooks.ReactorMessage", new[] { new Target("LevelGeneration.LG_WardenObjective_Reactor", "SetGUIMessage") }),
        ("GameHooks.PuzzleState", new[] { new Target("ChainedPuzzles.ChainedPuzzleInstance", "OnStateChange") }),
        ("GameHooks.ResourceContainerState", new[] { new Target("LevelGeneration.LG_ResourceContainer_Sync", "OnStateChange") }),
        ("GameHooks.GeneratorState", new[] { new Target("LevelGeneration.LG_PowerGenerator_Core", "OnStateChange") }),
        ("GameHooks.PickupItemState", new[] { new Target("LevelGeneration.LG_PickupItem_Sync", "OnStateChange") }),
        ("GameHooks.TerminalCommand", new[] { new Target("LevelGeneration.LG_ComputerTerminalCommandInterpreter", "ReceiveCommand") }),
        ("GameHooks.NoiseMade", new[] { new Target("NoiseManager", "MakeNoise") }),
        ("GameHooks.ScoutScreamActivated", new[] { new Target("Enemies.ES_ScoutScream", "ActivateState") }),
        ("GameHooks.ScoutDetectionEntered", new[] { new Target("ES_ScoutDetection", "CommonEnter") }),
        ("GameHooks.EnemyExplosionReceived", new[] { new Target("Dam_EnemyDamageBase", "ReceiveExplosionDamage") }),
        ("GameHooks.WaveStopped", new[] { new Target("SurvivalWave", "StopEvent") }),
        ("GameHooks.FirstPersonHolderLateUpdate", new[] { new Target("FirstPersonItemHolder", "LateUpdate") }),
        ("GameHooks.SyncIkSolved", new[] { new Target("Player.PlayerSyncIK", "UpdateIK") }),
        ("GameHooks.EnemyAttackStarted", new[] { new Target("Enemies.ES_EnemyAttackBase", "DoStartAttack") }),
        ("GameHooks.EnemyHitreacted", new[] { new Target("ES_Hitreact", "DoHitReact") }),
        ("GameHooks.AnimatorCrossFaded", new[] { new Target("UnityEngine.Animator", "CrossFadeInFixedTime") }),
        ("GameHooks.HibernateWakeActivated", new[] { new Target("Enemies.ES_HibernateWakeUp", "ActivateState") }),
        ("GameHooks.EnemyScreamStarted", new[] { new Target("Enemies.ES_Scream", "ActivateState") }),
        ("GameHooks.EnemyScreamed", new[] { new Target("Enemies.ES_Scream", "OnStateShouldBeActivated") }),
        ("GameHooks.PlayerReceived", new[] { new Target("Dam_PlayerDamageBase", "ReceiveBulletDamage"), new Target("Dam_PlayerDamageBase", "ReceiveMeleeDamage"), new Target("Dam_PlayerDamageBase", "ReceiveExplosionDamage"), new Target("Dam_PlayerDamageBase", "ReceiveTentacleAttackDamage"), new Target("Dam_PlayerDamageBase", "ReceiveShooterProjectileDamage"), new Target("Dam_PlayerDamageBase", "ReceiveFallDamage"), new Target("Dam_PlayerDamageLocal", "ReceiveBulletDamage"), new Target("Dam_PlayerDamageLocal", "ReceiveMeleeDamage"), new Target("Dam_PlayerDamageLocal", "ReceiveExplosionDamage"), new Target("Dam_PlayerDamageLocal", "ReceiveTentacleAttackDamage"), new Target("Dam_PlayerDamageLocal", "ReceiveShooterProjectileDamage"), new Target("Dam_PlayerDamageLocal", "ReceiveFallDamage") }),
        ("GameHooks.PlayerRevived", new[] { new Target("PLOC_Downed", "OnPlayerRevived") }),
        ("GameHooks.HealthSetReceived", new[] { new Target("Dam_PlayerDamageBase", "ReceiveSetHealth"), new Target("Dam_EnemyDamageBase", "ReceiveSetHealth") }),
        (PuppetMovement, new[] { new Target("Player.PlayerLocomotion", "UpdateMovementFromSync") }),
        (PuppetAttack, new[] { new Target("EAB_ProjectileShooter", "FireBurst"), new Target("EAB_MovingEnemeyTentacle", "Attack"), new Target("EAB_MovingEnemyTentacleMultiple", "Attack") }),
        ("GameHooks.Fire.BulletWeapon", new[] { new Target("Gear.BulletWeapon", "Fire") }),
        ("GameHooks.Fire.Shotgun", new[] { new Target("Gear.Shotgun", "Fire") }),
        ("GameHooks.Fire.BulletWeaponSynced", new[] { new Target("Gear.BulletWeaponSynced", "Fire") }),
        ("GameHooks.Fire.ShotgunSynced", new[] { new Target("Gear.ShotgunSynced", "Fire") }),
        ("GameHooks.MuzzleRay", new[] { new Target("Weapon", "CastWeaponRay") }),
        ("GameHooks.AlignedRay", new[] { new Target("Weapon", "CastWeaponRay") }),
        ("GameHooks.PushEntered", new[] { new Target("Gear.MWS_Push", "Enter") }),
        ("GameHooks.SentryFired", new[] { new Target("SentryGunInstance_Firing_Bullets", "FireBullet") }),
        ("GameHooks.SentryLifecycle", new[]
        {
            new Target("SentryGunInstance", "OnGearSpawnComplete"), new Target("SentryGunInstance", "PickUp"), new Target("SentryGunInstance", "SyncedPickup"),
            new Target("SentryGunInstance", "OnDespawn"), new Target("SentryGunInstance", "OnDestroy"),
            new Target("SentryGunInstance", "OnBackpackCleanupCallback"), new Target("SentryGunInstance", "CleanUp"),
        }),
        ("GameHooks.MineSpawned", new[] { new Target("MineDeployerInstance", "OnSpawn") }),
        ("GameHooks.MinePickedUp", new[] { new Target("MineDeployerInstance", "SyncedPickup") }),
        ("GameHooks.LevelCleanup", new[] { new Target("LevelGeneration.Builder", "OnLevelCleanup") }),
        ("GameHooks.MarkerSpawned", new[] { new Target("LG_MarkerSpawner", "TrySpawnRandomPrefab") }),
        ("GameHooks.PrefabBuilt", new[] { new Target("LevelGeneration.LG_PrefabSpawner", "OnBuild") }),
        ("GameHooks.SurvivalWaveStarted", new[] { new Target("Mastermind", "TriggerSurvivalWave") }),
        ("GameHooks.ExpeditionEnded", new[] { new Target("RundownManager", "OnExpeditionEnded") }),
        ("GameHooks.ExpeditionFailedChecked", new[] { new Target("WardenObjectiveManager", "CheckExpeditionFailed") }),
        ("GameHooks.ExpeditionSuccessCheck", new[] { new Target("WardenObjectiveManager", "CheckExpeditionSuccess") }),
        ("GameHooks.ChainedPuzzleInteract", new[] { new Target("ChainedPuzzles.ChainedPuzzleInstance", "AttemptInteract") }),
        ("GameHooks.PlayerDespawned", new[] { new Target("Player.PlayerManager", "OnPlayerDespawned") }),
        ("GameHooks.EnemySetup", new[] { new Target("Enemies.EnemyAgent", "Setup") }),
        ("GameHooks.EnemyDead", new[] { new Target("Enemies.EnemyAgent", "OnDead") }),
        ("GameHooks.EnemyDespawned", new[] { new Target("Enemies.EnemyAgent", "OnDeSpawn") }),
        ("GameHooks.EnemyDestroyed", new[] { new Target("Enemies.EnemyAgent", "OnDestroy") }),
        ("GameHooks.EnemyModeChanged", new[] { new Target("Enemies.EnemyAI", "ModeChange") }),
        ("GameHooks.EnemySyncSpawned", new[] { new Target("Enemies.EnemySync", "OnSpawn") }),
        ("GameHooks.EnemySyncDespawned", new[] { new Target("Enemies.EnemySync", "OnDespawn") }),
        ("GameHooks.EnemyDamageReceived", new[] { new Target("Dam_EnemyDamageBase", "ProcessReceivedDamage") }),
        ("GameHooks.EnemyLimbDestroyed", new[] { new Target("Dam_EnemyDamageLimb", "DestroyLimb") }),
        ("GameHooks.EnemyCustomLimbDestroyed", new[] { new Target("Dam_EnemyDamageLimb_Custom", "DestroyLimb") }),
        ("GameHooks.LocalPlayerDied", new[] { new Target("Dam_PlayerDamageLocal", "ReceiveSetDead") }),
        ("GameHooks.BulletHit", new[] { new Target("Gear.BulletWeapon", "BulletHit") }),
        ("GameHooks.BulletWeaponWielded", new[] { new Target("Gear.BulletWeapon", "OnWield") }),
        ("GameHooks.BulletWeaponUnwielded", new[] { new Target("Gear.BulletWeapon", "OnUnWield") }),
        ("GameHooks.MagazineVisibility", new[] { new Target("Gear.BulletWeapon", "SetMagazineVisibile") }),
        ("GameHooks.AutoDryFired", new[] { new Target("Gear.BWA_Auto", "OnFireShotEmptyClip") }),
        ("GameHooks.SemiDryFired", new[] { new Target("Gear.BWA_Semi", "OnFireShotEmptyClip") }),
        ("GameHooks.GearPartsSpawned", new[] { new Target("Gear.GearPartHolder", "OnAllPartsSpawned") }),
        ("GameHooks.GearSpawnCompleted", new[] { new Target("ItemEquippable", "OnGearSpawnComplete") }),
        ("GameHooks.ResourcePackApplied", new[] { new Target("Gear.ResourcePackFirstPerson", "ApplyPack") }),
        ("GameHooks.ReloadTriggered", new[] { new Target("ItemEquippable", "TryTriggerReloadAnimationSequence") }),
        ("GameHooks.ReloadDone", new[] { new Target("ItemEquippable", "_TryTriggerReloadAnimationSequence_b__145_0") }),
        ("GameHooks.BackpackDeployed", new[] { new Target("Player.PlayerBackpack", "SetDeployed") }),
        ("GameHooks.SentrySpawned", new[] { new Target("SentryGunInstance", "OnSpawn") }),
        ("GameHooks.ItemUnwielded", new[] { new Target("ItemEquippable", "OnUnWield") }),
        ("GameHooks.SyncedGearSpawnCompleted", new[] { new Target("Gear.BulletWeaponSynced", "OnGearSpawnComplete") }),
        ("GameHooks.FlashlightSet", new[] { new Target("ItemEquippable", "SetAttachedFlashlightEnabled") }),
        ("GameHooks.GlueBurstFired", new[] { new Target("GlueGun", "FireBurst") }),
        ("GameHooks.ThrowableThrown", new[] { new Target("ThrowingWeapon", "Throw") }),
        ("GameHooks.ProgressionObjectiveSet", new[] { new Target("PUI_GameObjectives", "SetProgressionObjective") }),
        ("GameHooks.ProgressionObjectiveRemoved", new[] { new Target("PUI_GameObjectives", "RemoveProgressionObjective") }),
        ("GameHooks.ObjectivesCleaned", new[] { new Target("PUI_GameObjectives", "OnLevelCleanup") }),
        ("GameHooks.SubObjectiveShown", new[] { new Target("PUI_WardenIntel", "ShowSubObjectiveMessage") }),
        ("GameHooks.PrioritySubObjectiveShown", new[] { new Target("PUI_WardenIntel", "ShowPrioritySubObjectiveMessage") }),
        ("GameHooks.ExpeditionWindowVisible", new[] { new Target("CellMenu.CM_ExpeditionWindow", "SetVisible") }),
        ("GameHooks.MasterMigrationFound", new[] { new Target("SNetwork.SNet_SyncManager", "OnFoundNewMasterDuringMigration") }),
        ("GameHooks.MasterFound", new[] { new Target("SNetwork.SNet_SyncManager", "OnFoundMaster") }),
        ("GameHooks.GameEventPosted", new[] { new Target("GameEvent.GameEventManager", "PostEvent") }),
        ("GameHooks.PlayerReplicationSpawned", new[] { new Target("Player.PlayerReplicationManager", "OnSpawn") }),
        ("GameHooks.PlayerReplicationDespawned", new[] { new Target("Player.PlayerReplicationManager", "OnDeSpawn") }),
        ("GameHooks.PlayerManagerSpawned", new[] { new Target("Player.PlayerManager", "OnPlayerSpawned") }),
        ("GameHooks.GameLogAdded", new[] { new Target("PUI_GameEventLog", "AddLogItem") }),
        (ItemSpawned, new[] { new Target("ItemReplicationManager", "OnItemSpawn") }),
        (ReplicationMisses, new[] { new Target("SNetwork.SNet_Replication", "RecieveBytes"), new Target("SNetwork.SNet_Replication", "AssignReplicatorKey") }),
        (StackedVolumes, new[] { new Target("AIGraph.AIG_GeomorphNodeVolume", "TryGetGeomorphVolume") }),
        (InteractionSwitches, new[] { new Target("PlayerInteraction", "set_CameraRayInteractionEnabled"), new Target("PlayerInteraction", "set_SphereCheckInteractionEnabled"), new Target("PlayerInteraction", "set_LadderInteractionEnabled") }),
    };
}

