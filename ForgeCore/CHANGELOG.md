# Changelog

## 1.0.0

ForgeUI, ForgeAudio and ForgeVisual are now part of ForgeCore: one package (`NAinfini-ForgeCore`), one plugin (`NAinfini.ForgeCore`) and one `ForgeCore.dll`. The three are no longer separate packages; remove them when you update. Their namespaces and settings files (`NAinfini.ForgeUI.cfg`, `NAinfini.ForgeUI.keys.cfg`, `NAinfini.ForgeAudio.cfg`, `NAinfini.ForgeVisual.cfg`) keep their names, so your settings stay. A mod declares only `[BepInDependency("NAinfini.ForgeCore")]` and the dependency `NAinfini-ForgeCore-1.0.0`; `UIKit.PluginId` and `AudioKit.PluginId` are gone. Interface changes: `FxSlots.Find` returns the empty handle (`FxHandle.IsNothing`) instead of null for a slot the table does not list; `GameHooks.SentryGearSpawned`, `SentryDespawned` and `ReloadAnimationStarted` are gone (use `SentryLifecycle`, filtered on the step, and `ReloadTriggered`), and so are `GameContext.Unsubscribe` and `GameSounds.UnsubscribeGameSounds`; types and members nothing outside the library used are now internal. Added: `Vec.Finite`, `Gear.ArchetypeId`, `Ease.Smoothstep`, and `Frame.GameTime`, `ScreenWidth` and `ScreenHeight`.

- Interface: `SettingsSlider` takes a language `TextKey` instead of English and Chinese labels, and `SettingsPages.AddAudioSliders` takes one `headingKey` instead of two heading texts; `VolumeControl` no longer carries `English` and `Chinese` labels and `AudioVolumes.HeadingEnglish` / `HeadingChinese` are gone (use `VolumeControl.TextKey` and `AudioVolumes.HeadingKey`). The audio volume labels live in the UI language files.
- Item pictures wait for the game to build an item's model and fall back to the model prefab, so a consumable still in a backpack no longer loses its picture.
- A borrowed objective timer is taken down when released even if the game or another mod changed its text meanwhile.
- The heavy grenade launcher's explosion ships with the library: Arsenal's launcher and Energy Lab's Blast Gun draw the same shared blast.
- A gun model no longer needs a left-hand socket: the hand is derived from the game's own hold and pulled back along the gun into the arm's reach at the hip; a missing right grip is the model's origin.
- The kept material copies of a level's own materials are destroyed when the level ends.

Version number only: the first release was 0.1.0, and 1.0.0 is the version the Forge packages share from now on. Packages that depend on this library need 1.0.0 or later. Fixes: `Enemies.Generation` no longer restarts at 1 for a slot an earlier level used, and `Enemies.Version` changes when the level is cleared; `Store.WriteText` and `ChunkWriter` flush to the disk before they finish; a peer refused by the handshake no longer keeps its `PlayerStateSlot` value.

Added after that entry: `Combat.AreaDamage` with `BlastLine` (a host radius blast by body, the line of sight as a parameter), and an optional trigger mode on `SweepQuery.Cast`; `Enemies.Limbs` (every living enemy limb in a sphere with its enemy, never truncated) and `ModLog.RaiseEach` (every handler of an event, one that throws written once). ForgeCore lists the audio volume sliders in the game's settings itself, so a package no longer adds them. A lobby value cleared while the send queue was full is cleared on the other players too once the queue empties.

Further fixes:

- `AgentModifiers` uses the game's local modifier entry while the synced packet is missing, and `Ready` says whether the modifier manager exists.
- `Players.Warp` reads the player's live body, so a warp that landed is reported as landed.
- `WorldStates.Cluster` returns false for a cluster the level never set up, and restoring a cluster no longer starts the next scan or solves the chain; it no longer throws.
- A network channel no longer ends itself on a level cleanup for a world it never started.

## 0.1.0

First release.

- Modules: `Log`, `Settings`, `Store`, `Pool`, `Assets`, `Frame`, `Patches` (first-use and lazy patching), `Net` (handshake by format fingerprint, `NetDelivery`, lobby values, world epoch), `InputLease`, `CameraLease`, `GameEvents`, `GameHooks` / `Bracket`, `Enemies`, `EnemyAlert`, `EnemySpawn`, `Level`, `Combat`, `Gear`, `Items`, `SoloDrop`, `DataBlocks`, `PlayerHits`, `PlayerNoise`, `Puppets`, `WorldStates`, `HoldPoint`, `Compact`, `ChunkLog`, fingerprints and hashes, the size a PNG header declares (`Png`).
- `GameHooks` patches the game methods several packages read once and hands each call to every subscriber (`Before`, `After`, `Around`, `Skip`); each subscription is an `IDisposable`, and only the patch kinds the current subscriptions need are on the method. `GameHooks.SentryLifecycle` covers the seven sentry methods that move a sentry in or out of its owner's backpack and the world (`SentryStep`, `SentryCall`).
- `Session` (`IsHost`, `HostLookup`, `LocalLookup`), `Players.Local` / `LocalAlive` / `Each` / `Joined` / `Left`, `Players.Warp` with `WarpMode`, `WarpRefusal` and `WarpResult`, `Level.ActiveKey`, `Store.WriteText`, and `Fnv.Mix32` / `Mix64` over integers and `MixUtf8`.
- `Level.PlayerByLookup` / `Level.IsPlayer` find the in-level player agent of a network player.
- `Combat.ShooterRay` is the bullet ray that steps over the shooter's own colliders.
- `HoldPoint.Disable` / `Enable` shut a timed hold point and keep it shut when the game switches it on again, without a per-frame check.
- `GameEvents.ExpeditionSelected` is raised on the host and on clients.
- `Net.Traffic`: messages and payload bytes every package sent, counted in the game transport.
- Logging writes errors only.
- `ModLog.ErrorOnce(message)` writes an exact message once; errors log the whole exception.
