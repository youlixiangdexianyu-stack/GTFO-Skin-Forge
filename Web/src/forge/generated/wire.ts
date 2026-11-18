// Generated from Docs/forge-contract/wire/wire.json by Tools/Forge/build-wire.ts. Do not edit; not tracked in Git (pnpm generate:ephemeral).
/** The wire vocabulary the website compiler and the game runtime both read (contract §3.3). Tools/Forge/build-wire.ts generates the TypeScript and C# tables from this file; neither side writes a second copy. A list's position is a compiled index wherever a plan stores one: a member is appended, never inserted, and a member is removed only in the change that reshapes every row still naming it. */
export const forgeEngineApiVersion = "1.0.0";
/** Port value classes; position is a layout slot's compiled `type`. Cardinality is its own axis, and a timer or reservation is a handle with an owner kind and a lifetime. */
export const graphPortTypes = ["execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy"] as const;
export type GraphPortType = (typeof graphPortTypes)[number];
/** `many` is an ordered, identity-deduplicated sequence, never an unordered bag. */
export const graphCardinalities = ["one", "many"] as const;
export type GraphCardinality = (typeof graphCardinalities)[number];
/** The value types a step frame may carry, in the runtime ValueKind order: entry i is ValueKind i + 2, asserted at every plan load. `handle` and `resource` are typed world identities, `event` carries its declared dispatch identity; `policy` has no frame form. */
export const forgeEngineValuePortTypes = ["boolean", "integer", "number", "string", "vector3", "entity", "enum", "handle", "resource", "event"] as const;
/** The value types a `many` port may carry: the ones with an element form. An event or a result row is a dispatch identity or a row, not a value, so neither has a collection form. */
export const forgeEngineManyPortTypes = ["boolean", "integer", "number", "string", "vector3", "entity", "enum", "handle", "resource"] as const;
/** The value classes a structural `value_type` member may type a port as. `execution` is control flow, `event` a dispatch identity, `result` a row identity and `policy` has no frame form, so none of them is a member. A guarded handle (`handle:<kind>[:<lifetime>]`, see guardedHandleLifetimes) or a named resource kind (`resource:<kind>`) is also a member. */
export const valuePortTypes = ["boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle"] as const;
/** The types a parameter may declare; a membership list, its order carries nothing. A value parameter can be promoted to an input port with an identical value contract; `resource` and `object-address` are compile-time constants and never promotable. */
export const graphParameterTypes = ["boolean", "integer", "number", "string", "enum", "vector3", "recipient-policy", "resource", "object-address"] as const;
export type GraphParameterType = (typeof graphParameterTypes)[number];
/** What an action recipient may be. Not every receiver is an entity: pools, instances and handles are recipients too, never implicit ones. */
export const recipientTargetKinds = ["entity", "resource", "handle"] as const;
export type RecipientTargetKind = (typeof recipientTargetKinds)[number];
/** The kinds an entity port may carry, spelled as the runtime reports an observed entity's kind. A port without kinds accepts every kind. Object subkinds are port types only: snapshots and the instance table report the parent kind. */
export const graphEntityKinds = ["gtfo.player", "gtfo.enemy", "gtfo.map_object", "gtfo.level_object", "gtfo.level", "gtfo.zone", "gtfo.equipment", "gtfo.map_object.door", "gtfo.map_object.terminal", "gtfo.map_object.generator", "gtfo.map_object.trigger_zone", "gtfo.level_object.container", "gtfo.map_object.portal", "gtfo.map_object.item_slot", "gtfo.map_object.shuttlebox"] as const;
export type GraphEntityKind = (typeof graphEntityKinds)[number];
/** Position is a resource slot's compiled `valueSet`. `text` sits before `generator`, which the runtime's frame-shape check keeps last. */
export const graphResourceKinds = ["map", "room", "enemy", "weapon", "tool", "consumable", "item", "objective", "encounter", "wave", "spawn_pool", "behavior_graph", "ability", "path", "area_field", "animation", "audio", "effect", "material", "model", "profile", "pool", "chained-puzzle", "zone", "text", "generator"] as const;
export type GraphResourceKind = (typeof graphResourceKinds)[number];
/** Position is a handle slot's compiled `valueSet`. Every handle has exactly one owning source; nothing releases another source's handle. */
export const graphHandleKinds = ["timer", "subscription", "status", "effect", "audio", "animation", "deployment", "charge", "pool_membership", "pool", "request", "projectile", "sound"] as const;
export type GraphHandleKind = (typeof graphHandleKinds)[number];
/** The scope whose end cancels, expires or releases a handle; position is a handle slot's compiled `lifetime`. Matches the control scopes. */
export const handleLifetimes = ["invocation", "resource_instance", "entity_life", "encounter", "expedition", "session"] as const;
export type HandleLifetime = (typeof handleLifetimes)[number];
/** The resource domains a capability graph and a behavior graph may declare. */
export const resourceDomains = ["map", "room", "enemy", "weapon", "tool", "consumable", "player", "session", "logic", "editor"] as const;
export type ResourceDomain = (typeof resourceDomains)[number];
/** `pure` reads no world state; `query` reads it through the budgeted query interface and writes nothing; `host`, `owner` and `presentation` have side effects and run on their owning authority. */
export const executionTiers = ["pure", "query", "host", "owner", "presentation"] as const;
export type ExecutionTier = (typeof executionTiers)[number];
/** What a capability reads besides its ports: `world` is live game state, `kernel` a runtime service, `telemetry` the recorded diagnostics stream. Each one is a reason the capability cannot be `pure`. */
export const graphReadSources = ["world", "kernel", "telemetry"] as const;
export type GraphReadSource = (typeof graphReadSources)[number];
/** A plan's mount target kinds, in the ordinal order the plan's attachment list is sorted by. */
export const forgePlanAttachmentKinds = ["level", "map-object", "gear-block", "enemy-type", "player"] as const;
export type ForgePlanAttachmentKind = (typeof forgePlanAttachmentKinds)[number];
/** Each subkind's parent. A subkind output reaches a parent input, a parent output never reaches a subkind input; a kind not listed is a top kind. */
export const graphEntityKindParents: Readonly<Partial<Record<GraphEntityKind, GraphEntityKind>>> = {"gtfo.map_object.door":"gtfo.map_object","gtfo.map_object.terminal":"gtfo.map_object","gtfo.map_object.generator":"gtfo.map_object","gtfo.map_object.trigger_zone":"gtfo.map_object","gtfo.level_object.container":"gtfo.level_object","gtfo.map_object.portal":"gtfo.map_object","gtfo.map_object.item_slot":"gtfo.map_object","gtfo.map_object.shuttlebox":"gtfo.map_object"};
/** The handle members a structural `value_type` parameter may name and the lifetime each resolves to. A handle contract is its kind and its lifetime, so a handle of another lifetime is another member. The order is the compiled order of the parameter's own list. */
export const graphGuardedHandleLifetimes: Readonly<Record<string, HandleLifetime>> = Object.freeze({"timer":"encounter","subscription":"encounter","effect":"encounter","sound":"encounter","effect:entity_life":"entity_life"});
/** Closed member lists. A set's position is an enum slot's compiled `valueSet` and a member's position its compiled value; the runtime never compares the strings. `{"sameAs": key}` is the same list as that top-level key. */
export const graphEnumSets = {
    /** Numeric only: every comparison node compares numbers. */
    compare_operator: ["eq", "ne", "lt", "lte", "gt", "gte"],
    boundary_mode: ["inclusive", "exclusive"],
    rounding_mode: ["floor", "ceil", "nearest", "truncate"],
    /** Pre-commit request lifecycle; distinct from the post-commit outcome. */
    command_phase: ["requested", "accepted", "committed", "rejected", "cancelled"],
    execution_outcome: ["succeeded", "partial", "rejected", "failed", "cancelled", "expired"],
    interaction_phase: ["requested", "started", "completed", "cancelled", "failed"],
    damage_kind: ["direct", "melee", "explosion", "dot", "shrapnel", "collision", "fall", "environment", "reflection", "tentacle", "tank_grab", "trap_grab", "projectile"],
    ai_state: ["sleeping", "waking", "idle", "investigating", "alerted", "pursuing", "attacking", "recovering", "disabled", "dead", "patrolling", "hibernating"],
    status_kind: ["slow", "haste", "root", "stun", "paralysis", "freeze", "foam", "blind", "deaf", "silence", "disarm", "weaken", "vulnerability", "burn", "bleed", "poison", "corrosion", "infection", "regeneration", "shield", "cloak", "taunt", "fear", "reveal", "mark"],
    stack_policy: ["refresh", "extend", "add", "independent"],
    query_shape: ["room", "area", "zone", "sphere", "cone", "box", "cylinder", "capsule", "path"],
    /** What a selector answers when nothing matches: the empty set, or a refusal. */
    empty_policy: ["emit-empty", "fail"],
    equipment_action: ["primary", "secondary", "reload", "interact", "recall", "alternate", "custom-binding"],
    lifetime_scope: handleLifetimes,
    /** Stays a subset of valueTypeParameterTypes. */
    variable_value_type: ["boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle"],
    recipient_sort: ["stable-id", "nearest", "farthest"],
    /** The five actor roles, spelled the way the actor context names them; the capability port spells `event-target` as `event_target`. */
    recipient_anchor: ["self", "source", "owner", "instigator", "event-target"],
    recipient_relation: ["self", "ally", "hostile", "neutral", "unknown"],
    recipient_life_state: ["alive", "downed", "dead"],
    /** Union of what the operation nodes implement; each structural parameter narrows it to its own subset. */
    value_operation: ["set", "add", "subtract", "multiply", "minimum", "maximum"],
    coordinate_space: ["world", "local", "view"],
    pulse_start: ["immediate", "after_interval"],
    commit_state: ["none", "confirmed", "unknown", "partial"],
    /** Native `AgentModifier` (Modules-ASM), each member name lower-cased with an underscore or a case boundary becoming a hyphen. */
    agent_modifier: ["none", "regeneration-cap", "regeneration-speed", "heal-support", "revive-speed-support", "revive-start-health-support", "melee-resistance", "projectile-resistance", "infection-resistance", "damage-over-time", "nanoswarm-shield", "nanoswarm-weakness", "explosion-resistance", "pistol-damage", "smg-damage", "dmr-damage", "assault-rifle-damage", "carbine-damage", "auto-pistol-damage", "hel-damage", "shotgun-damage", "revolver-damage", "sniper-damage", "burst-cannon-damage", "machine-gun-damage", "machine-pistol-damage", "rifle-damage", "burst-rifle-damage", "double-tap-rifle", "bullpup-rifle-damage", "combat-shotgun-damage", "choke-mod-shotgun-damage", "standard-weapon-damage", "special-weapon-damage", "glue-strength", "glue-efficiency", "sentry-gun-speed", "sentry-gun-damage", "sentry-gun-long-range-damage", "sentry-gun-short-range-damage", "trip-mine-damage", "scanner-recharge-speed", "ammo-support", "hacking-proficiency", "computer-processing-speed", "initial-ammo-standard", "initial-ammo-special", "initial-ammo-tool", "fog-repeller-effect", "glowstick-effect", "bioscan-speed", "melee-damage", "movement-speed", "movement-acceleration"],
    /** Native `eRundownTier` TierA..TierE are 1..5; `overload` is the third layer's own spelling. */
    rundown_tier: ["A", "B", "C", "D", "E", "overload"],
    /** Native `eDoorStatus`, lower-cased member names, as the door reader publishes it. */
    door_state: ["none", "closed", "closed_broken_cant_open", "closed_locked_with_key_item", "closed_locked_with_chained_puzzle_alarm", "closed_locked_with_chained_puzzle", "closed_locked_with_power_generator", "closed_locked_with_no_key", "chained_puzzle_activated", "unlocked", "open", "destroyed", "glued_max", "try_open_stuck_in_glue", "try_open_stuck_broken", "closed_locked_with_bulkhead_dc", "opening"],
    /** A door read's answer: the native status folded into five states, where `unlocked` counts as closed. */
    door_query_state: ["closed", "open", "locked", "needs_scan", "broken"],
    /** The weak door's own callbacks: `attacked` is a hit that did not break it, `broken` is the break. */
    door_phase: ["attacked", "broken"],
    /** Native deployable kinds, in the order the placement provider indexes them. */
    equipment_kind: ["sentry_gun", "mine", "glue_gun"],
    /** The supplies a player can apply, verbatim from the game's apply events. */
    supply_kind: ["medikit", "ammokit", "disinfection", "tool_refill"],
    /** Every pickup event the game posts, its three commodity sizes kept apart because the game keeps them apart. */
    pickup_kind: ["medikit", "ammokit", "tool_refill", "artifact", "commodity_small", "commodity_medium", "commodity_large", "consumable", "keycard"],
    /** A scan's own state, lower-cased native member names. */
    scan_state: ["disabled", "active", "solved"],
    /** A container's own status, lower-cased native member names. */
    container_state: ["not-setup", "locked", "closed", "open", "player-close", "player-far"],
    /** The weak lock's own cause of opening: a hackable lock hacked or a melee lock smashed. */
    door_lock_cause: ["hacked", "smashed"],
    /** Native `Enemies.EGS`, the state a live enemy group replicates, in declaration order. */
    enemy_group_state: ["idle", "hunters_spawn", "hunters_hunt", "hunters_search", "guards_spawn", "guard_respawn", "guards_idle", "guards_hunting", "patrol_spawn", "patrol_move", "patrol_idle", "patrol_search", "patrol_combat", "survival_spawn", "survival_hunt", "debug_spawn", "debug_idle"],
    /** The weapon charge state machine's phases. */
    charge_phase: ["start", "progress", "end", "swing"],
    aim_phase: ["enter", "exit"],
    /** What a shot ended against. */
    shot_outcome: ["hit", "world", "miss"],
    /** Native `PlayerLocomotion` state, in declaration order. */
    player_movement_state: ["stand", "crouch", "run", "jump", "fall", "land", "stunned", "downed", "climb_ladder", "on_terminal", "melee", "empty", "grabbed_by_trap", "grabbed_by_tank", "testing", "in_elevator", "grabbed_by_pouncer", "stand_still"],
    /** Native `GameData.eEnemyGroupType` in declaration order: a group's authored type, not its live state. */
    enemy_group_type: ["hibernate", "pure_sneak", "detect", "pure_detect", "patrol", "awake", "hunter"],
    /** Whether an immune receiver respects or ignores a new application of a declared status. */
    status_immunity: ["respect", "ignore"],
    /** Whether a status instance may end early by a dispel or only at its own expiry. */
    status_removal: ["dispellable", "until-expiry"],
    /** Native `UnityEngine.KeyCode` in the engine's declaration order, kebab-cased; the runtime maps each member back to the KeyCode it names. */
    key_code: ["backspace", "delete", "tab", "clear", "return", "pause", "escape", "space", "keypad0", "keypad1", "keypad2", "keypad3", "keypad4", "keypad5", "keypad6", "keypad7", "keypad8", "keypad9", "keypad-period", "keypad-divide", "keypad-multiply", "keypad-minus", "keypad-plus", "keypad-enter", "keypad-equals", "up-arrow", "down-arrow", "right-arrow", "left-arrow", "insert", "home", "end", "page-up", "page-down", "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12", "f13", "f14", "f15", "alpha0", "alpha1", "alpha2", "alpha3", "alpha4", "alpha5", "alpha6", "alpha7", "alpha8", "alpha9", "exclaim", "double-quote", "hash", "dollar", "percent", "ampersand", "quote", "left-paren", "right-paren", "asterisk", "plus", "comma", "minus", "period", "slash", "colon", "semicolon", "less", "equals", "greater", "question", "at", "left-bracket", "backslash", "right-bracket", "caret", "underscore", "back-quote", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "left-curly-bracket", "pipe", "right-curly-bracket", "tilde", "numlock", "caps-lock", "scroll-lock", "right-shift", "left-shift", "right-control", "left-control", "right-alt", "left-alt", "left-command", "left-windows", "right-command", "right-windows", "alt-gr", "help", "print", "sys-req", "break", "menu", "mouse0", "mouse1", "mouse2", "mouse3", "mouse4", "mouse5", "mouse6"],
    /** The two edges of one key press. */
    key_phase: ["pressed", "released"],
    /** Native `Agents.AgentMode` at spawn folded into three answers: `hibernating` is Hibernate and Scout, `aggressive` is Agressive, `other` every other mode. */
    enemy_spawn_origin: ["hibernating", "aggressive", "other"],
    /** Which reading of an objective layer moved: the status, the chain position or the item counters. */
    objective_change: ["state", "phase", "progress"],
    /** Why an ammunition pool changed: a supply refill, a floor pickup or rounds spent. */
    ammo_change_reason: ["refill", "pickup", "consume"],
    /** One member per player receive entry whose native body is its own; the no-air and parasite entries share one folded body and have no member. */
    player_attack_kind: ["tentacle", "shooter_projectile", "tank_grab", "trap_grab", "bullet", "melee", "explosion", "fire", "sticky"],
    /** Native `pInfectionEffect` in declaration order. */
    infection_cause: ["none", "disinfection_pack", "disinfection_station"],
    /** The attack state's two callbacks (`OnAttackWindUp`, `OnAttackPerform`). */
    enemy_attack_phase: ["windup", "perform"],
    /** Native `NM_NoiseType` in declaration order. */
    enemy_noise_type: ["insta_detect", "detectable", "investigate", "pulse_only"],
    /** `EAB_Alarm.StartChargeUp` and `EAB_Alarm.StartAlarm`. */
    enemy_alarm_phase: ["charge_up", "alarm"],
    /** The snatcher's consume state and the tank tentacle grab. */
    enemy_grab_kind: ["pouncer", "tank_tentacle"],
    /** The stagger members of native `ES_HitreactType`. */
    enemy_hitreact: ["micro", "light", "heavy"],
    /** Native `AgentTargetDetectionType` from `Hibernation` on, in declaration order. */
    enemy_detection_type: ["hibernation", "ability", "damage", "propagated"],
    /** Native `ES_ScoutScream.ScoutScreamState` in declaration order. */
    scout_scream_phase: ["setup", "chargeup", "scream", "response", "done"],
    /** The attack state an enemy ability started. */
    enemy_attack_kind: ["shooter", "shooter_flyer", "striker", "tank", "tank_multi_target"],
    /** Native `Agents.AgentMode` in declaration order. */
    agent_mode: ["off", "aggressive", "patrolling", "scout", "hibernate"],
    /** The device categories `device_disable` can hold off, in the player provider's order. */
    device_category: ["environment_light", "flashlight", "player_hud", "map", "biotracker"],
    /** Native `eLimbDamageType` in declaration order. */
    enemy_limb_type: ["normal", "weakspot", "armor"],
    /** Native `eInventoryItemStatus` in declaration order. */
    backpack_item_status: ["in_backpack", "deployed"],
    native_marker_style: ["LocationBeacon", "PlayerPingLookat", "PlayerPingEnemy", "PlayerPingAmmo", "PlayerPingHealth", "PlayerPingLoot", "PlayerPingTerminal", "PlayerPingCaution", "PlayerPingHSU", "PlayerPingDoor", "PlayerPingResourceLocker", "PlayerPingResourceBox", "PlayerInCompass", "PlayerPingSign", "LocationBeaconNoText", "TerminalPing", "PlayerPingGenerator", "PlayerPingDisinfection", "PlayerPingCarryItem", "PlayerPingConsumable", "PlayerPingPickupObjectiveItem", "PlayerPingToolRefill", "PlayerPingSecurityDoor", "PlayerPingBulkheadDoor", "PlayerPingApexDoor", "PlayerPingBloodDoor", "PlayerPingSecurityCheckpointDoor", "PlayerPingBulkheadCheckpointDoor", "PlayerPingApexCheckpointDoor", "PlayerPingBloodCheckpointDoor", "PlayerPingBulkheadDC"],
    objective_status: ["not_discovered", "discovered", "started", "partially_solved", "item_solved"],
    /** The pickup class of a level item: carry, key, small pickup, resource pack, consumable, artifact, commodity, or another class. */
    item_kind: ["carry", "key", "small_pickup", "resource_pack", "consumable", "artifact", "commodity", "other"],
    /** Native `Localization.Language` in declaration order. */
    text_language: ["English", "French", "Italian", "German", "Spanish", "Russian", "Portuguese_Brazil", "Polish", "Japanese", "Korean", "Chinese_Traditional", "Chinese_Simplified"],
    /** The text_language values plus the authoring-only instruction that clears a client's override. */
    text_language_setting: ["English", "French", "Italian", "German", "Spanish", "Russian", "Portuguese_Brazil", "Polish", "Japanese", "Korean", "Chinese_Traditional", "Chinese_Simplified", "follow_game"],
    /** How an ability a card started ended early: replaced by a later ability card, or its enemy left while it ran. */
    enemy_ability_end_reason: ["superseded", "despawn"],
    /** A melee push's three moments: `MWS_Push` entered, the push released, and the push reaching a target. */
    melee_push_phase: ["prepare", "release", "hit"],
    /** Native `eWardenObjectiveType`, snake-cased member names, in declaration order. */
    objective_type: ["hsu_find_take_sample", "reactor_startup", "reactor_shutdown", "gather_small_items", "clear_a_path", "special_terminal_command", "retrieve_big_items", "power_cell_distribution", "terminal_uplink", "central_generator_cluster", "activate_small_hsu", "survival", "gather_terminal", "corrupted_terminal_uplink", "empty", "timed_terminal_sequence"],
    /** Native `eWardenSubObjectiveStatus`, snake-cased member names, in declaration order. */
    objective_sub_status: ["find_location_info", "find_location_info_help", "go_to_zone", "go_to_zone_help", "in_zone_find_item", "in_zone_find_item_help", "solve_item", "solve_item_help", "go_to_win_condition", "go_to_win_condition_help"],
    /** How an expedition ended: cleared, the team wiped, or the players left. */
    expedition_end: ["success", "fail", "abort"],
    /** The refusal branches of a use request the native bodies take: an empty consumable, a throw still cooling down, a throw not ready, and a resource pack the target does not need. */
    use_failure_reason: ["empty-consumable", "throw-cooldown", "throw-not-ready", "resource-not-needed"],
    /** The top kind an observed entity reports, spelled as entityKinds spells it, in this fixed order: player, enemy, map object (doors, terminals and the other map subkinds report this parent), level object, equipment. A zone or the level itself is not an observed entity and has no member. */
    object_kind: ["gtfo.player", "gtfo.enemy", "gtfo.map_object", "gtfo.level_object", "gtfo.equipment"],
    /** Why a deployed sentry or mine left the world, in this fixed order: its owner took it back, it was removed without being taken back or destroyed, or it was destroyed. */
    deployable_removal_reason: ["recalled", "despawned", "destroyed"],
    /** Native `Enemies.EnemyGroupType` in declaration order, snake-cased: the kind a live group was spawned as (`debug_spawn_unit` is a single placed enemy), not its authored `enemy_group_type`. */
    enemy_group_kind: ["hibernating", "patrolling", "hunters", "survival", "debug_spawn_unit"],
    /** Why a declared status instance ended: its duration ran out, a cancel named its handle, a dispel removed it, or its world, plan, module or life went away. */
    status_end_reason: ["expired", "cancelled", "dispelled", "reclaimed"]
} as const;
export type GraphEnumSet = keyof typeof graphEnumSets;
/** Elements one collection port holds, head slot excluded: the cap on a `many` literal and on a frame segment. */
export const forgeEngineSetWidth = 256;
/** Shared runtime ceilings, independent of an author's configured lower budgets. The runtime manifest exports the same keys with no value above these. */
export const forgeEngineLimits: {readonly maxEntrypoints: number; readonly maxStepsPerEntrypoint: number; readonly maxTotalSteps: number; readonly maxEventsPerTick: number; readonly maxCommandsPerTick: number; readonly maxQueuedEvents: number; readonly maxCausalDepth: number; readonly maxAttachmentsPerPlan: number; readonly maxControlIterations: number; readonly maxStepExecutionsPerDispatch: number; readonly maxLiveHandles: number; readonly maxValueSlotsPerStep: number; readonly maxDispatchFrameBytes: number} = Object.freeze({"maxEntrypoints":32,"maxStepsPerEntrypoint":128,"maxTotalSteps":512,"maxEventsPerTick":128,"maxCommandsPerTick":512,"maxQueuedEvents":1024,"maxCausalDepth":16,"maxAttachmentsPerPlan":256,"maxControlIterations":1024,"maxStepExecutionsPerDispatch":2048,"maxLiveHandles":1024,"maxValueSlotsPerStep":66,"maxDispatchFrameBytes":4194304});
export type ForgeEngineLimits = {-readonly [K in keyof typeof forgeEngineLimits]: number};
/** Cold reads of untrusted community packages are bounded (contract §3.2 I-PACK): maximumPublishedBytes for content, resource and settings payloads, maximumPlanBytes for one execution plan. Plan sets have no total limit. */
export const packageLimits: {readonly maximumPublishedBytes: number; readonly maximumPlanBytes: number} = Object.freeze({"maximumPublishedBytes":67108864,"maximumPlanBytes":4194304});
/** Bounds of one plan document: JSON nesting depth and the length of one string value. Its byte size is packageLimits.maximumPlanBytes. */
export const forgeEngineWireLimits: {readonly maxJsonBytes: number; readonly maxJsonDepth: number; readonly maxStringValueLength: number} = Object.freeze({"maxJsonBytes":4194304,"maxJsonDepth":32,"maxStringValueLength":4096});
/** The tables the game carries in 16 bits although persistentID is UInt32: the enemy group id, the survival wave settings and population ids, and every gear component table (`Gear.GearIDRange.m_comps` is a UInt16 array; Item fills its BaseItem slot). */
export const uint16DataBlockTypes: readonly string[] = ["EnemyGroup", "SurvivalWaveSettings", "SurvivalWavePopulation", "GearCategory", "Item", "ItemFPSSettings", "WeaponAudio", "WeaponMuzzleFlash", "WeaponShellCasing", "GearPattern", "GearPalette", "GearDecal", "GearFrontPart", "GearPartAttachment", "GearPerk", "GearReceiverPart", "GearStockPart", "GearSightPart", "GearMagPart", "GearFlashlightPart", "GearToolMainPart", "GearToolGripPart", "GearToolDeliveryPart", "GearToolPayloadPart", "GearToolTargetingPart", "GearToolScreenPart", "GearMeleeHeadPart", "GearMeleeNeckPart", "GearMeleeHandlePart", "GearMeleePommelPart"];
/** The pool of a 16-bit table. */
export const uint16ContentPersistentIdRange = {first: 0x1000, last: 0xffff} as const;
/** The pool of every other table and of the non-native content number, without reservedRange and the table's vanilla ids. Vanilla ids lie below it except Text and LevelLayout, whose vanilla ids are hashes the issuer skips; 0xFFFFFFFF is the UInt32 empty value. */
export const contentPersistentIdRange = {first: 0x1000000, last: 0xfffffffe} as const;
/** Map-compile identities (`nativeBlockIdentity`); never issued. */
export const reservedContentPersistentIdRange = {first: 0x40000000, last: 0x7fffffff} as const;
/** Where the allocator starts in a 16-bit table: the FNV-1a hash of the stable id lands here, and the first free number at or after it, wrapping inside the pool, is issued. */
export const seedWindow16 = {first: 0x8000, last: 0xffff} as const;
/** Where the allocator starts in every other table. */
export const seedWindowWide = {first: 0x20000000, last: 0x3fffffff} as const;
