# Changelog

## 1.0.3

In multiplayer the host and every client should run this version.

- Fixed: a hit on an enemy is not counted twice when packets are lost or late, and a client that lags behind no longer subtracts the same hit again from the health the host reports.
- Fixed: a large state sent to a player who just joined no longer holds back the updates every other player gets meanwhile.
- Fixed: a player who joined late could miss what the others had already shared (a picked skin, a chosen loadout, enemy health) until someone changed it again; a player who asks again is now sent everything again. A player who joins a level after a checkpoint reload stays in the host's world for that level.
- Fixed: character models (ForgeSkin) could stop moving with their players after the game removed one player's body, for example when someone left.
- Fixed: changing a gun's hand shape in first person (ForgeSkin's hand pose) plays through the game's own grip animation; playing it directly on the hand layer changed nothing.
- Added: `HumanRig.Links(bones, rest)`, `HumanRig.ArmWear(bones, rest)` and `HumanRig.Unposed(links)`. A model mapped onto the humanoid needs a bone only for its hips: any other joint without one is left out of the pose copy and keeps its rest pose on the bone above it, so a model without fingers, without an arm or with unlike sides follows a body (ForgeSkin's character models). A limb with a joint left out is not bent onto the body's, and a model whose hips rest on its own floor keeps its rest height; each is logged once.
- Added: `JointClip.FromTurns(rate, joints, turns, hips, name)`, a clip built in code with a name, so recordings show it and `JointClip.Find` finds it (ForgeSkin's weapon inspect).
- Added: the lobby button option `WithoutApparel`: the button also shows on a bot's bar (ForgeSkin's SKINS button for bots).
- Removed or changed public interfaces: none.

## 1.0.2

In multiplayer the host and every client should run this version: with an older ForgeCore on the host, a client shows only basic hit feedback.

- Fixed: on a client, enemy health bars, damage numbers and kill marks showed nothing (the game settles a client's hits on the host only). They now show in the frame of the hit and are corrected by the host's result: a kill that did not happen is taken back and the bar gives back what it lost. The host sends enemy health to clients that have this version.
- Fixed: a teammate's character model (ForgeSkin) turned flat pink in the level.
- Fixed: a part a mod switches on and off every frame (a skin's flickering hologram) stays hidden while the first-person item is hidden; before, it was drawn in the middle of the screen during an action seen from behind.
- Fixed: your body in the view from behind (Forge Emote's dance) stands on your feet; it sank into the floor after crouching and floated after a jump.
- Added: `Combat.OwnHitPredicted`, `Combat.OwnHitSettled` and `Combat.OwnHitCorrected`, the local player's hits on enemies the same on host and client: shown at once, the host's result, and the difference between the two.
- Added: `GameHooks.EnemyHitSent`, the local player's hit request to an enemy with its damage.
- Logs: a missing toon shader, a library shader unloaded after loading and a person with no ghost material are each written once.


## 1.0.1

Everything since 1.0.0. This version stays at 1.0.x although it removes and changes public interfaces; each one is listed below under "Breaking" and "Interface", and mods that use them need to be recompiled. From this version on, 1.x only adds to the interface and removes nothing.

- Breaking: `HookFanout<T>`, `HookLease` and `HookDispatch` are removed; there is no replacement.
- Breaking: `ModelPictureView` is removed; there is no replacement (ForgeSkin's picker shows the picture each pack carries).
- Breaking: `Glow.Srgb` and `Glow.LinearToSrgb` are removed; use `ColourSpace.ToSrgb`, `ColourSpace.ToLinear` and `ColourSpace.Glow`. `Glow.Value` and `Glow.Write(material, srgb, gain)` convert a glow colour once, by whether the shader property is HDR (`Glow.IsHdr`).
- Breaking: `FirstPersonWear.Follow` is removed: the library follows a worn model's visibility every frame itself. `FirstPersonWearOptions` adds `Descendants` and `LeaveOut`, and the wear accepts the caller's own model instance.
- Interface: `MenuScreenWindow.AddButton` takes an optional `why`: a disabled button fades with its frame and text, takes no press or hover, and shows that one sentence under it. Recompile mods that call it.
- Added: player looks for a whole character. `PlayerLooks` (one provider per player look: `Provide`, `Describe`, `Dress`, `Hang`, `Worn`, with `IPlayerLookProvider`, `ILookDress` and `PlayerLook`), `Persons.Hide` for a player's agent or for a body without one (a lobby figure, a replay stand-in), several holders on one person not fighting, and `PersonHideOptions` (`ThirdPerson`, `FirstPerson`, `Held`, `Keep`, `Look`).
- Added: humanoid rigs. `HumanRig` (the canonical humanoid links, arm reach and first-person arm wear), `PoseFingers` and `PoseLink.Optional` (pose copy covers 30 finger joints; a missing bone is skipped), `RigMap.Check`, `ImportedModelDescriptor.CheckFiles` / `IsMeshFile`, and `ImportedModels.Prepare` / `ImageTextures.Prepare`, which decode a model's textures over several frames before it is built.
- Added: chain physics for charms, hair, skirts and ribbons: `Chains.Bind` with `ChainSpec` (presets `Charm`, `LongHair`, `ShortHair`, `Skirt`, `Tail`), `ChainState`, `ChainShape` and `ChainCapsule`; chains settle at rest, are culled by distance and visibility, and survive turning and being knocked back.
- Added: toon materials, `Toon` and `ToonDress` with `ToonLighting` (forward, deferred or off, per player).
- Added: `Models.Drawn` (enabled and not turned off by `forceRenderingOff`; outlines collect only what is drawn), `Models.LeaveOutInFirstPerson`, `OwnBodyCut`.
- Added: replayable objects for recordings: `Recording.Track` (pose, scale and visibility recorded when they change), `Recording.State`, `Recording.Provide` / `IRecordingProvider`, `Recording.Find`, `Recording.Apply` and `Recording.Placed`.
- Added: `Combat.Kill` kills an enemy whatever its health, melee resistance or not-dying flag (on the host, through the game's own damage entry); `EnemyDamageCall.Unruled` marks that hit so Forge damage rules leave it alone.
- Added: local rundown progress for custom rundowns. `Progression` records the zones cleared in each level of a custom rundown (a Forge rundown, or every rundown when MTFO loads its own game data) on this machine, and the game's rundown screen unlocks its levels from that by the rundown's own rules, without asking the game's server. The game's own rundowns are left alone and keep your own progress.
- Added: `PickerSource.Layout`, `Panel`, `PickScheme`, `Settings`, `SettingsInfo` and `NumberSlots`, with `PickerPanel` and `PickerScheme` (a picker's colour schemes and its settings behind the title bar's gear); `UIKitSprites.Gear` and `Tick`.
- Added: `NativeObjects.Same` and `Identity` (compare game objects by the object, not the wrapper), `GameText.Read` and `TryColumn`, `ColourSpace`, `WorldBackdrop.Queue`, `Png.TryDecode`.
- Fixed: outlines draw every part of a model with many sub-meshes (a character model was outlined only at the face); a hidden original body no longer enters the outline.
- Fixed: a pose copy that follows a body is applied after that body's own arm and aim solving, so teammates' character models and replay stand-ins hold the gun where the game holds it.
- Logs: a save that cannot be written, a damaged save file and an error in the sentry tool-slot guard are written once instead of at every save or deployment.
- Fixed: `GearIcons.Redraw` no longer fails while the game's icon renderer has removed itself between jobs.
- Added: `GearIcons.Unframed`, which leaves a mod's additions on a gear out of the framing of its game icons.
- Added: `Store.Flush()` writes out the values still waiting for the background writer; ForgeCore calls it when the game closes, so a setting changed just before quitting is kept.
- Added: `GameHooks.HibernatingNoiseHeard`, `HibernateWakeupStarted`, `HibernateDetectionStarted`, `HibernateDetectionEnded` and `EnemyTargetInjected`, for sleeping enemies hearing a noise, waking and being handed a target, shared by every package that reads them.
- Added: `Players.SimulatedHere(player)` (whether this machine moves that player: the local player, and on the host its bots), `LimbReach.Bend` (a two-bone reach to a target point), `NativeObjects.FieldOffset<T>(name)` (a game field's offset, resolved once) and `FileHashes.Sha256Hex(path)`.
- Fixed: a claimed weapon-list line (`WeaponListLine`) recognises the line it renamed or hid on later passes, so a renamed line keeps its claim, a hidden line that stops matching shows again at once, and the inventory reads the renamed text.
- Logs: by default only errors are written. `[Logging] DeveloperLogs` in `NAinfini.ForgeCore.cfg` (off by default, always on with ForgeDevelopment installed) brings back the warnings and information lines; `Log.Developer` and `Log.DevelopmentInstalled` tell a mod which applies.
- Settings: the interface, sound and visual settings moved from `NAinfini.ForgeUI.cfg`, `NAinfini.ForgeAudio.cfg` and `NAinfini.ForgeVisual.cfg` into `NAinfini.ForgeCore.cfg`, and changed keys from `NAinfini.ForgeUI.keys.cfg` into `NAinfini.ForgeCore.keys.cfg`; the old files are no longer read, so set them again once.
- The hash of the game's `GameAssembly.dll` is kept between starts in `BepInEx/cache/ForgeCore/file-sha256.txt` and computed again only when the file changes.
- Fixed: the lobby buttons no longer all hide when the lobby opens (the space was measured while the player bar was still sliding in).
- Fixed: a `Net` request whose handler throws is answered as already run when it is asked again, instead of staying stuck.
- Fixed: the grid check behind `Borken cell` no longer throws.
- Added: `NetEvent.BroadcastExcept(message, player)` (to every other player but one: a host passing a player's message on without sending it back) and `NetTable.Refresh` / `NetByteTable.Refresh` (rewrite a row for the player who just joined only, without sending it to the others).
- `PlayerState.Publish` of the value already held with the same start sends nothing, and a player state is checked for impossible numbers without reflection on each arrival.
- Added: `Store.WriteBytes` (the atomic write of `Store.WriteText` for bytes). `Store.Write<T>` now writes on a background thread (the newest value of a path wins; `Store.Read<T>` sees it at once) and checks an existing file only the first time a path is read or written, so a save no longer stalls the game; temporary files are named `.<name>.<random>.tmp`, those a crashed game left are removed, and a replace held by a reader or virus scanner is retried briefly.
- The heavy blast no longer ships with this library (`forge-visual-fx.bundle`, `fx-slots.forge` and `fx-licences.forge` are gone from the package): each package that fires it ships its own copy and registers it with the new `HeavyBlast.Register`; `HeavyBlast.Load` is public and is called by those packages where they load their own effects, no longer at every landing for every player. Its effect name is unchanged, so recordings still play it; without such a package installed the name is reported missing once and nothing is drawn.
- Breaking: the one-method handler interfaces are gone; pass a delegate instead. `AudioKit.ClaimWeaponActions` takes an `Action<WeaponAction>` (was `IWeaponActionHandler`), `AudioKit.ClaimBodySounds` a `Func<BodySound, Vector3, bool>` (was `IBodySoundHandler`), `AudioKit.ReserveGameEvent` an `Action<uint, IntPtr, Vector3, bool>` (was `IReservedEventHandler`). `PlayerHits.Observe(owner, before, after, faulted)` takes the three steps of the hit reader (`PlayerHits.HitBefore`, then `after` or `faulted` with the `HitState` it answered) instead of an `IPlayerHitObserver`, and `PlayerHits.ObserveInfection(owner, before, after)` two delegates instead of an `IInfectionObserver`; a null `before` removes the registration. Recompile mods that used them.
- `UIToggle` without its own words shows the library's "on" and "off" in the player's language (was "ON"/"OFF" in English), and the loadout's reserve label is translated too. `HitEdge.Seconds`, unless a mod set it, follows the player's `Screen / HitFlashSeconds` as it changes. A sprite whose `FlipPhase` is negative no longer stops every mod's sprites.
- `PlayerHits` puts on only the patches a registration needs: a redirect alone no longer puts the reading prefix, postfix and finalizer on `OnIncomingDamage`. Priorities and the order of the callbacks are unchanged.
- Interface: `GameHooks.WieldedSlotBlocked.Release`, `GameHooks.LocalItemHeld.Hide` and the five `PlayerStats` subscriptions (`Walk`, `CarryUnslowed`, `Jump`, `NoStamina`, `NoFlinch`) return an `IDisposable` that ends the subscription (they returned nothing); the hook stays on. Recompile mods that call them.
- Added: the full `Combat.Noise` overload takes an optional `maker` (a `PlayerAgent`), handed to the game as the noise's maker. Recompile mods that call that overload.
- Interface: `UIKit.AddLobbyButton` takes the button's area (`LobbyArea`: below, right, above) and width (`LobbyWidth`: full, half, third); buttons of one area fill rows in order and move to the next area only when they do not fit. Under APPAREL the buttons' rows and the lobby strips' rows close up and then get lower (not below 0.6 of their height, always as wide as APPAREL) to stay above the screen's bottom before a button moves elsewhere.
- Added: `Images.Queue` and `Images.QueueRaw` (images decoded and uploaded over several frames within a frame budget, raw DDS-style blocks uploaded directly); `Store.Read` / `Store.Write` with an explicit path, which never overwrite a file they could not read; `StrictJson.NamespacedId`; `EnemyNames` and `UIKit.EnemyName` (the game's enemy names in English and Chinese).
- Added: the procedural pattern library (`Procedural`, `ProceduralCatalog`, `ProceduralPainter`, with blend modes and `SetTime`), and the adjust-page controls `UISlider`, `UIToggle`, `UIChoice` and `UISwatches`.
- Added: `Il2CppArrays.Store` and `Il2CppArrays.ToGameReferences`, which store into the game's arrays of references with the collector's write barrier (ForgeSkin needs them).
- Added: `AudioKit.CheckClip`, the container check `AudioKit.Load` applies to each clip, for one clip on any thread (null when it passes, else why not).
- Added: `PickerCard.Swatch` and `PickerInfo.Swatch` show a colour choice as a tile on the card and large in the details. Picker titles too long for one line take two; an adjust page draws its controls as soon as it opens; list rows never draw past the list's frame.
- Full-screen lists (`MenuScreen` entries) centre each line of a row in the row's box, and hovering a row no longer scrolls the list.
- Resource bundles read their dependencies from the text manifest Unity writes (the game build has no `AssetBundleManifest`), so a bundle with dependencies loads in game; bundle digests, module fingerprints and content hashes are computed once, and model bundles load on first use.
- A grid cell without a room makes the game write `Borken cell` only once per level (`[Logs] BorkenCellOnce`, on by default); the answer for that cell is unchanged.
- Range rings draw the author's opacity (0 to 1) and glow (0 to 4) without a cap and only their outward faces.

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
