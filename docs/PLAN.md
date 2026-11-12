# ForgeSkin — rewrite plan

The single plan for this repository. Keep only current requirements; delete finished items from §6.

## 1. Product

A Windows desktop tool for GTFO gear skins (guns, melee weapons, tools, resource packs, consumables and large carry items),
plus one BepInEx plugin that applies them in game.

1. **New skins.** Replace each weapon part's textures (Albedo, Normal, MetallicSmoothness, Emissive) and material parameters (emissive colour and intensity, metallic, smoothness, tint), e.g. put an anime character on the gun.
2. **Attachments.** Mount models or parts on the gun (imported GLB), and screens that show live data, e.g. a cyberpunk ammo counter that counts down as the player fires.
3. **Dynamic layers and triggered effects.** Animated images, image sequences, video and text composited onto a part's
   texture, looping or played on a game event (kill, hit, fire, reload, equip, aim, low ammo, ... — §3.1) — e.g. an eye on the gun blinks on
   every kill, like COD weapon effects. Attachments can flash their emission or show for a few seconds on an event.
5. **Holograms, for everyone.** No modelling needed: built-in shapes (line, tube, sphere, ring, arc, box, grid, cylinder,
   cone, panel), solid or as a wireframe, assembled like any attachment; one 全息 switch per attachment draws its model and
   screen as a glowing hologram that flickers; screens get border styles (corners, double line, notched, ticks).

Scope is skins, simple models and these effects. A node's own model can be replaced (it keeps the node's place, aligns
and materials); re-pointing muzzle, sight and hand aligns to a new model is out.
4. **Part layout.** Every part of the weapon (vanilla parts and their sub-transforms, and every attachment) can be moved,
   rotated, scaled and hidden; the game shows the same layout.

The app is the authoring tool. Players never run it: they install the plugin (the base library on Thunderstore) and
skin mods that depend on it. 应用到游戏 is the author's test loop: it installs or updates the plugin in the chosen test
profile, turns hot reload on there and installs the skin, then reports what the game did from the plugin's log. Distribution is a separate step: export a skin pack, a
Thunderstore skin mod, or patched game files.

Every feature of the old Python tool stays (§4).

Skins are loaded by the plugin at runtime; that is the default route and leaves `GTFO_Data` untouched. For players without
BepInEx a skin's material textures and parameters can also be written into the game's own asset files ("替换游戏原版资源",
§3.5), with backups and restore, or exported as a zip of patched asset files ("导出游戏材质包").
First version is local only: other players see vanilla weapons.

## 2. Architecture

```
editor/                     Electron (Electron Forge + Vite) + React + TypeScript + three.js (react-three-fiber)
  src/main/                 window, dialogs, spawns and talks to the backend
  src/preload/              typed contextBridge API
  src/renderer/             editor UI, 3D preview, video/text frame rendering
src/ForgeSkin.Format/       net6.0. SkinDocument (project == pack manifest), SkinPack (zip + validation), ForgeSkinMesh
src/ForgeSkin.Backend/      net10.0 console, self-contained single file, shipped as Electron extraResource
src/ForgeSkin.Plugin/       net6.0 BepInEx 6 IL2CPP plugin
tests/                      dotnet test project
prebuilt/plugin/            committed plugin build (refreshed by every Release build of the plugin)
docs/PLAN.md                this file
```

Packaging: `npm run make` in editor/ builds the Squirrel installer; its prePackage hook publishes the backend
self-contained single file and copies the prebuilt plugin (repo prebuilt/plugin/) to `resources/publish/plugin`.
Release: a GitHub release `v<version>` (the version in editor/package.json, raised every release) carries the
installer, its `RELEASES` and `.nupkg`, and the plugin as a Thunderstore-layout zip. An installed editor updates
itself from these releases (`update-electron-app` through update.electronjs.org: checks at start and hourly,
downloads in the background, offers a restart); the plugin it installs updates with it.

Dependency direction: Plugin → Format; Backend → Format; app → backend over stdio only. The Electron side never parses
Unity files, packs or projects itself; everything about formats goes through the backend. TypeScript types for
SkinDocument are generated from the backend's `schema` command (System.Text.Json `JsonSchemaExporter`) with
json-schema-to-typescript; never hand-written.

Coordinates: everything stored is GTFO weapon space (Unity, left-handed, Y up, metres, 1.0x game scale). GLB exported for
preview converts Unity → glTF by negating Z (positions, normals, tangents.z; winding flipped). The renderer converts
poses with one helper (`unityToThree` / `threeToUnity` in `editor/src/renderer/space.ts`); nothing else converts.

## 3. Interfaces

### 3.1 Skin document and pack (`src/ForgeSkin.Format`)

- `SkinDocument` is both the editor project (`.forgeskinproj`, JSON, file references relative to the project file) and the pack
  manifest (`manifest.json` in a `.forgeskin` zip, references are zip paths). `editor` holds UI state and is dropped when packing.
- `SkinPack.Open` validates; error codes: `schema`, `no-id`, `no-gear`, `no-manifest`, `no-material`, `bad-path`,
  `missing-file`, `bad-image`, `bad-trigger`, `bad-vector`, `no-atlas`, `bad-atlas`, `no-anchor`, `empty-attachment`, `bad-screen`, `no-glyphs`, `bad-mesh`, `bad-audio`, `duplicate-material` (two
  overrides of one material name). Colours, sizes and other vectors must have their length and be finite; fractions and volumes
  lie in 0–1. A screen styled `bar` needs a source with a maximum (`bad-screen`). A mesh entry must start with the `.forgeskinmesh` magic; `ForgeSkinMesh.Read` checks the counts against the data
  length before allocating, and a broken GLB is `bad-mesh` when packing or importing.
- `.forgeskinmesh` is the attachment mesh format (see `ForgeSkinMesh`).
- `SkinDocument.Parts` (`PartTransform`): part id (same ids as `Attachment.Part`), optional child path under it (as
  `Transform.Find` takes it), and absolute local values — `position`, `rotation` (Euler degrees), `scale`, `visible` — each
  nullable; null keeps what the game built. Written once when the gear finishes assembling, so a respawn or reload writes the
  same values again and nothing accumulates.
- Attachment poses are local to their anchor (part root or child path), so in game they always sit on that part.
- `SkinTarget`: `weapon` (catalog id) and exactly one of `gearId` (PlayerOfflineGearDataBlock persistentID: guns, melee
  weapons and tools built from gear parts) and `itemId` (ItemDataBlock persistentID: items with their own prefabs — resource
  packs, consumables, carry items, hacking tool), filled from the catalog when packing; all null for attachment skins.
  `Key()` is the target key: `gear:<gearId>` or `item:<itemId>`.
- Triggers (`Trigger`: always, kill, hit, fire, dryFire, reloadStart, reloadEnd, equip, unequip, aim, aimEnd, lowAmmo, empty,
  ammoPickup, flashlightOn, flashlightOff, miss; every one but `always` is an event). `DynamicLayer.Trigger` + `Play` (loop / once /
  loopFor seconds): a non-`always` layer shows the base texture until its event, then plays. `Attachment.Effects`: list of
  {trigger, action: flash (emission colour, intensity, seconds) / show (seconds)}.
- `Attachment.Hologram` (`Hologram`: flicker 0–1), null = normal surfaces: the attachment's model and screen are drawn
  unlit in their own colours, glowing, with the game's `Cell/Player/Display_Hologram` (the mine deployer's display; a
  loaded game material with it is copied so its textures come along), or the gear shader's plain unlit glow where that
  shader is missing or `HologramShader` is off in ForgeSkin.cfg. Flicker hides the renderers for brief random moments
  (`forceRenderingOff`, so `show` effects keep working). The first hologram logs the shader's properties.
- `Screen.BorderStyle`: solid (the plugin's ring), corners, double, notched, ticks. A styled border is drawn by the editor
  in white at the screen's aspect (`.frames/<id>-border.png`, `frames.ts` `borderCanvas`, also the preview) and traced
  into `Screen.BorderMesh` when packing (height 1, width the screen's aspect, facing -Z), as static text is.
- Shapes (editor only): an attachment made from a built-in shape keeps its kind and parameters in `editor.shapes[id]`;
  the editor builds it with three.js geometry (a wireframe as thin tubes along the edges), writes it as a GLB and imports
  it like any GLB, again whenever a parameter changes. The pack sees an ordinary mesh attachment.
- `ScreenSource`: ammoClip, ammoReserve, ammoTotal, static, and for the local player over the current level (reset when the
  next level starts generating): kills and damageDealt with this weapon, damageDealtAll with all their weapons, damageTaken,
  accuracy (percent of this gun's shots that hit an enemy; 0 before the first shot); health and infection are their current
  percent. `Screen.Style`: `digits` or `bar`; a bar only for a source with a maximum (ammo against its maximum,
  percentages against 100), never for counts or static text. The glyph strip is `Screen.Glyphs` "0123456789 -%"; a pack
  baked with the earlier 12-glyph strip (no '%') stays valid and its percentages show without '%'.
- `SkinDocument.Sounds` (`SkinSound`): {trigger (not always), file, volume 0–1, lowAmmoFraction}. The file is Ogg Vorbis, mono
  or stereo, at most 10 s; the editor converts any audio the author picks (WAV, MP3, FLAC, OGG, M4A) to Ogg Vorbis locally with
  `wasm-media-encoders` and keeps an existing Ogg Vorbis file as is. Packed as `sounds/<id>.ogg`.
- Livery (editor only, never in a pack): `MaterialOverride.Finish` (matte, gloss, satin, metal, brushedMetal, chrome, rubber,
  plastic, carbonFibre, paintedSteel, wornMetal, glow) and `MaterialOverride.Livery` (`LiveryLayer`, bottom first: fill, decal
  or brush; colour/tint, opacity, blend normal/multiply/screen/overlay/add, mask strokes). A decal is a frame in the part
  root's space (Unity) projected along its -Z, with size, angle, depth and mirror; brush and mask strokes are UV polylines
  (u, v, width). The renderer composites it (`livery.ts` `composeLivery`, one implementation for the live 3D view and the
  pack): decals are drawn by rendering each mesh of the material at its UVs and taking every fragment's position into the
  decal frame (inside its width, height and depth, on faces looking at it). Before `pack.build` it writes the result as the
  material's Albedo, a finish's MetallicSmoothness, and for glow the Emissive, so packs and game-file replacement carry
  ordinary textures and the plugin needs nothing new.
- Animated decal: a decal whose image is a GIF with more than one frame has the playback of a dynamic layer (`Fps`, null =
  the GIF's own frame delays; `Trigger`, `Play`, `PlaySeconds`, `LowAmmoFraction`; defaults always / loop). The static bake
  shows its first frame. The build then takes the texels its projection of an opaque image covers, grown by the 2-texel
  gutter, and for every frame (at most 240) composes the whole livery with that decal at that frame (other animated decals at
  their first), cuts it to that rect and writes it as a dynamic layer of the material (albedo, its id, through the same
  frame writer as layers), so paint above and below the decal is in every frame. A rect whose frames do not fit a
  4096×4096 atlas fails with `bad-atlas`. In game only the albedo animates (a glow finish's emissive keeps the first frame),
  and where it overlaps another dynamic layer the last copy wins. Game-file replacement keeps the first frame (warning).
  The 3D view plays it the same way (redrawn only when its frame changes), including 触发预览.

### 3.2 Install layout (in a BepInEx profile)

```
<profile>/BepInEx/plugins/ForgeSkin/ForgeSkin.Plugin.dll   (+ ForgeSkin.Format.dll, NVorbis.dll)
<profile>/BepInEx/plugins/ForgeSkin/skins/*.forgeskin
<profile>/BepInEx/plugins/ForgeSkin/skins/active.json        { "targets": { "gear:<gearId>" | "item:<itemId>": "<skinId>" }, "global": ["<skinId>", ...] }
<profile>/BepInEx/config/ForgeSkin.cfg
<profile>/BepInEx/LogOutput.log                              plugin logs with prefix [ForgeSkin]
```

Skin mods (Thunderstore packages that depend on the base library) unpack to `<profile>/BepInEx/plugins/<Team>-<Name>/`
with a `forgeskin.forge` marker at their top level listing their packs (one path per line relative to that folder, `#`
comments). The marker is plain text, not JSON, so mod managers do not take it for a config file.

`global` skins have no weapon target and apply to every gear that uses their materials (mags, sights, lights, ammo).

### 3.3 Backend protocol (stdio, one JSON object per line, UTF-8)

Request `{"id":1,"method":"catalog.get","params":{...}}` → `{"id":1,"result":...}` or
`{"id":1,"error":{"code":"...","message":"..."}}`. Progress: `{"event":"progress","data":{"id":1,"done":3,"total":10,"text":"..."}}`.
Logs go to stderr and `<dataDir>/logs/backend.log`, never stdout.

At start the backend moves pre-rename files in the data dir (`.sfproj`, `.gtfskin`, `.sfmesh`) to `.forgeskinproj`,
`.forgeskin` and `.forgeskinmesh` and points the projects and `game-assets/*/state.json` that name them at the new names; the
app moves its pre-rename Electron profile (`%APPDATA%/GTFO Skin Forge`) to `%APPDATA%/ForgeSkin` (when the new one has no Local Storage yet) before the window opens.

| Method | Params → Result |
|---|---|
| `app.info` | → version, dataDir |
| `game.locate` | → `{gameDir, dataDir, found, running}` (Steam registry + libraryfolders.vdf + common dirs) |
| `game.setDir` | `{dir}` → same as locate |
| `profiles.list` | → BepInEx profiles: r2modman / Thunderstore Mod Manager profiles and the game dir; `{name, path, hasBepInEx, pluginVersion}` |
| `catalog.get` | → gear and attachments in official order: `{id, name, nameZh, category, slot (main/special/melee/tool/pack/consumable/carry/attachment/ammo), gearId, itemId, clipSize, maxReserve (guns: magazine and reserve bullets from the fire mode's archetype, the mag's clip multiplier and the slot's ammo cap), parts:[{id, role, label, materials:[...] , defaultEnabled}]}`; melee and tool parts use the `melee_*` / `tool_*` ids; an item with its own prefabs is one `body` part made of its first-person prefabs |
| `preview.weapon` | `{id}` → `{glb, parts:[{id, nodes:[...]}], textures:{material:{channel:png}}, params:{material:{...}}}`; cached under dataDir/cache, built from the local game |
| `match.folder` | `{weaponId, folder}` → per part per channel `{file, confidence, reason}` + unmatched files |
| `match.files` | `{weaponId, files:[...]}` → same, for drag-and-drop of loose files |
| `project.new/load/save` | SkinDocument in/out; `project.load` also imports legacy `.gtfoskin`, `.gtfdyn` and DLY presets |
| `project.list` | `{dir}` → `[{path, id, name, modified}]`, newest first; a file that does not parse is listed under its file name |
| `project.delete` | `{path}` → deletes one `.forgeskinproj` and its `<name>.snapshots/` folder |
| `project.missing` | `{project}` → `{files}`: the files the document names (every reference `project.save` maps; a font only when it is a font file, not a family name) that are not on disk |
| `mesh.import` | `{glb}` → `{forgeskinmesh (cache path), glb (the preview written back from it), bounds}` |
| `pack.build` | `{project, projectPath, out, sizeMode: keep/original/1k/2k/4k}` → `{pack, warnings}`; resizes keeping aspect, bakes layer atlases from frame PNGs the renderer wrote and traces screen glyph strips and static texts into meshes |
| `pack.install` | `{pack, profile, activate}` → writes skin + active.json (a running game picks it up only with the development switch on) |
| `pack.list / pack.remove / pack.activate` | manage installed skins in a profile |
| `pack.exportMod` | `{packs, out, name, version, description, icon}` → `{mod}`: a Thunderstore skin mod zip (manifest.json, icon.png scaled to 256x256, README.md, `forgeskin.forge`, `skins/*`), depending on the base library |
| `plugin.log` | `{profile, since?}` → `{lines:[{time, level, message}]}` from the plugin's own `forgeskin.log`, newer than `since` |
| `plugin.install` | `{profile}` → copies plugin; refuses with `game-running` while GTFO runs and the DLL is locked. First moves a pre-rename install: `plugins/SkinForge/skins/*` (packs renamed to `.forgeskin`, `active.json` kept) into `plugins/ForgeSkin/skins`, `config/SkinForge.cfg` to `ForgeSkin.cfg`, `BepInEx/SkinForge` to `BepInEx/ForgeSkin`, then deletes the old DLLs and log and the old folder once empty |
| `plugin.setDevelopment` | `{profile, on}` → writes `Development` in `BepInEx/config/ForgeSkin.cfg`; `{development, restartNeeded}`. `profiles.list` reports it as `development` |
| `export.obj` | `{weaponId, parts, mode: separate/merged, outDir}` → files; folder `YYYY-MM-DD-HHMMSS-<weapon>.OBJ`, MTL + manifest |
| `assets.apply` | `{pack, gameDir?}` → `{skin, buildId, files, materials, warnings}`: writes the pack's material textures and parameters into the game's `.assets` files (§3.5); refuses with `game-running` while GTFO runs |
| `assets.restore` | `{skinId?, discardStale?, gameDir?}` → `{restored, skipped:[{file, reason}], stale}`: without `skinId` every original back; with it that skin removed and its files rebuilt from the originals with the other applied skins; `discardStale` deletes records of older game builds |
| `assets.status` | `{gameDir?}` → `{gameDir, buildId, running, files:[{file, state: patched/original/changed/missing, backupOk, skins}], skins:[{id, name, version, appliedAt, materials, warnings}], stale:[{buildId, files, skins}]}` |
| `assets.export` | `{pack, out, gameDir?}` → `{zip, buildId, files, warnings}`: `GTFO_Data/<file>` patched copies of the files the pack changes plus README.txt (zh + en: game build, install steps, backup/restore warning, limitations); the game folder is not touched |
| `schema` | → JSON schema of SkinDocument |

Error codes: `game-not-found`, `game-running`, `bad-request`, `not-found`, `no-plugin` (no plugin build to install),
`nothing-to-apply` (the pack has no material texture or parameter a game file can hold), `game-changed` (a patched file changed
since it was written: game update or Steam file check), `stale-patch` (a game file still holds a patch written on an older game
build), `bad-backup` (a backup is missing or its SHA-256 does not match), `file-in-use` (a file is locked by another program),
`internal` (anything unexpected; logged with its stack trace), plus the format codes in §3.1. `bad-request` is only for a
malformed request or a missing or mistyped param; a missing file is `not-found`. `gameDir` overrides the
located game for `assets.*` (tests run on a copied folder, never on the real install); it is read under the same game-reader
gate and never touches the located game's cache.

Requests run in parallel. Methods that read, change and write a file (settings, `active.json`, projects, plugin files) run one
at a time; so do the long writers (`pack.build`, `pack.exportMod`, `assets.apply/restore/export`). Every durable file is written
to `<file>.tmp` and moved over the target; a stale temp is deleted first and a failed write leaves the target untouched.

Plugin source for `plugin.install`: `plugin/` next to the backend exe in a packaged app; in a checkout the newest of the plugin
project's Release/Debug output and the committed prebuilt copy in the repo's `prebuilt/plugin/` folder. Every Release build of the plugin
refreshes that folder, so machines that cannot build the plugin (no BepInEx interop assemblies) still install the current one.
The app checks `pluginCurrent` whenever a profile is chosen or listed and installs or updates the plugin on its own when the
game is not running.

### 3.4 Plugin behaviour

- Loads, once at start: every pack in its own `skins/`, the packs listed by the `forgeskin.forge` marker of each folder directly
  under `BepInEx/plugins` (only that file is looked for, one level deep), and packs other plugins register from their Load with
  `SkinPacks.Register(path)`. A mod's or registered skin is active for its target unless `active.json` picks one; among them
  the first by path wins and the others are logged as unused. Hot reload only with the development switch (`Development = true` in
  ForgeSkin.cfg, set from the app; read at game start): then a `FileSystemWatcher` reloads changed packs and `active.json`
  within ~0.5 s and re-applies to live weapons; only skin inputs (`*.forgeskin`, `active.json`, `forgeskin.forge`) count, never
  the plugin's own log or other mods' files. Off (default), nothing watches the folder and changes apply on the next launch.
- Hooks: IL2CPP folds identical native bodies, so a hook can run for unrelated objects. A target whose native entry starts
  with a return or jump (an empty override compiled to a shared `ret` stub, e.g. `MeleeWeaponFirstPerson.OnGearSpawnComplete`)
  is skipped: its detour hangs the game at startup. Handlers that read their arguments check the object's real class first.
- Applies after the game finished building a gear (all parts spawned and `GearMaterialFeeder` set up). Overrides go through each
  renderer's `MaterialPropertyBlock` (merged with the feeder's block) or a per-renderer material instance; never the shared material,
  so other guns that use the same part are untouched. The game's first-person switch (`SetFPSRendering`) gives each renderer a fresh
  copy of its material; ours is put back right after it with the keyword the game set, and the copy is destroyed.
- Applies to the local player's gear, first and third person.
- Attachments are parented to the named transform under the anchor part and follow animation, recoil and ADS.
- Part layout is written once per assembled gear (absolute local values, only the fields set); no per-frame work. Hiding a
  node disables only renderers: its own, its descendants' and attachments mounted under it, stopping at another part's root
  (a sight on the receiver's sight align is hidden only from its own part); the preview hides the same set. A node's
  `mesh` replaces its own model (MeshFilter mesh, not its children's) in its local space, keeping the game materials so
  the skin's textures apply by the new UVs; nodes with skinned meshes or no model of their own are refused with a log line. Hand IK and sight aligns that are children of a moved part move with it.
- Applies to every gear the local player sees as their own: first person, third person and the lobby loadout preview.
  Items with their own prefabs (packs, consumables, carry items, hacking tool) have no gear holder: they are skinned on the
  local player's first-person item when it is wielded (the wielded item is compared each frame, one pointer); their third
  person and world copies stay vanilla.
- In-game skin picker: a hotkey (`PickerKey` in ForgeSkin.cfg, default F8) opens a small on-screen list of every target that
  has a loaded skin: its skins by name plus 原版 (vanilla). Picking one applies it to live gear at once (the same re-apply as a
  hot reload) and is kept in `BepInEx/config/ForgeSkin.choices.json` (`{ "<target key>": "<skinId>" | "" }`, "" = vanilla).
  Precedence per target: the player's choice, then `active.json`, then the first by path. A choice whose skin is no longer
  loaded is ignored (kept in the file). While the list is open the cursor is free; closing it (hotkey, Esc) gives the cursor
  back. Drawn with Unity IMGUI by a picker component next to the runner, disabled while the list is closed, so
  closed it costs one key check a frame.
- Readback: debug only; with it off the plugin reads nothing on the game's spawn path for it. With `Readback = true` in ForgeSkin.cfg the plugin writes, for every assembled gear, the local TRS of every part
  root and descendant relative to the gear root to `BepInEx/ForgeSkin/readback/<gearId>.json` (vanilla, before any skin).
  The backend compares it with its own assembly (`assembly.verify`); any transform off by more than 1 mm or 0.1° fails.
- Screens: everything is opaque geometry with the gear shader (it has no transparency and other shaders break the first-person
  projection): a border ring `BorderWidth` wide, a panel quad inside it (each left out only when fully transparent), and the
  text as real glyph meshes. The pack carries one mesh per glyph of `Screen.Glyphs` (blank has none) traced from the strip the app
  renders with the chosen font (marching squares at alpha 0.5, simplified, triangulated with holes by LibTessDotNet); a static
  screen carries its whole text, any characters, as one mesh the same way. Text shrinks to fit 95% of the inner width.
  Colours: albedo is the colour (a 1×1 texture); with `GlowInGame` text and border also emit the colour at `Glow` × 0.2,
  else no emission. The 0.2 step is set from the game's own glowing gear materials (_EmissiveColor × map: flashlight lens
  1.0, HSU lights 0.54, tool screens 0.45–0.74, shotgun lights 0.07), so the default 1.5 glows clearly and the slider's top
  (5) matches the flashlight lens. The panel emits its colour at the shader's unit 0.01 (shown as itself, no bloom). Below the low-ammo threshold the text's albedo and emission both switch to `LowAmmoColor`; with `LowAmmoGradient` on the tint blends in as the clip falls from `LowAmmoGradientStart` down to the threshold.
  `Digits` 0 is automatic: the digits of the largest value the source can show (clip size, the storage's bullet max cap, or
  both); automatic counts show the value's own digits, centred (kills up to 4, damage up to 6); percentages 3 digits then a
  '%' cell; `PadZero` pads to that width. Damage is shown as an integer. Updates only when the value changes; `AmmoClip`
  reads the weapon's current clip, `AmmoReserve` / `AmmoTotal` the player's ammo storage. Damage dealt is the health the
  enemy lost in the enemy-damage hook (before minus after, never below 0). Damage taken, health and infection are read
  each frame from the local player's `Dam_PlayerDamageBase` fields (no allocation): a health drop is damage taken, a rise
  (heal, revive) is not. Style `bar`: one quad in the text colour (with the low-ammo tint for ammo), pivot on the border's
  inner left edge, as high as the text, scaled in X to the fraction when the value changes.
  After creating a screen the plugin logs one line with the text and border colours and emission actually applied and the
  template material's shader and keywords.
- Dynamic layers: atlas frames are copied into the target texture region with `Graphics.CopyTexture` (GPU), never a full re-upload.
- The plugin identifies a gear by its offline gear id: `GearIDRange.PlayfabItemInstanceId` ("OfflineGear_ID_<id>"), or for a
  range rebuilt from a packet the game's own gear (`GearManager.GetAllGearForSlot`) with the same components.
- Events come from the local player's own gear only:
  - kill / hit: the local player's damage made an enemy die / hit it and it lived, while this gear was wielded (guns and melee);
  - miss: a gun's (`BulletWeapon`) fire with no hit or kill from that frame to the end of the next one (hitscan damage of
    the local shooter is applied in the shot's frame; a shotgun's pellets are one shot). The same shot/hit tracking counts
    `accuracy`; melee weapons and tools have no shots;
  - fire: a gun's fire animation, a melee attack, a C-foam burst, a mine or sentry placed, a resource pack applied,
    a throwable thrown, a syringe or lock melter used, a carry item inserted; dryFire: a gun's trigger on an empty clip;
  - reloadStart / reloadEnd: the reload sequence started / its completion callback ran;
  - equip: the equip animation; unequip: the item's unwield (base or override, once per frame); for items without holder
    both come from the wielded-item change (a carry item dropped or placed is its unequip);
  - aim: the aim animation; aimEnd: the first-person aim input released while this gear is wielded (polled);
  - lowAmmo: the clip falls under the screen/layer threshold; empty: the clip reaches 0; ammoPickup: the reserve in the ammo
    storage rises (all polled with the clip read, edges only);
  - flashlightOn / flashlightOff: the item's attached flashlight changes state.
  A triggered layer restarts on a repeat event. The bio tracker has no fire or reload event in the game.
- Sounds (ported from ForgeAudio): each Ogg Vorbis file is checked and decoded once per pack with NVorbis (bounded, so a bad
  file fails instead of hanging) into a Unity `AudioClip`, and played on its event as a 2D one-shot on the plugin's own
  `AudioSource`, not through the game's Wwise. Only the local player hears them; nothing is sent over the network. While no
  other `AudioListener` is active the plugin enables its own. First-person gear only, so a sound plays once.
  Loudness is the sound's own volume, set in the app like every other edit; there is no second volume setting.
- Nothing in the product uses the network: the app, backend and plugin work offline.
- Textures are decoded once per pack and cached; no per-frame allocation, string or dictionary lookups on the update path.
  Material textures are compressed to DXT on load (normal maps first swizzled to the game's DXT5nm layout, x in A and y in G)
  and their CPU copy dropped; layer atlases (copied with `CopyTexture` into raw render targets) stay raw.
  Sizes not a multiple of 4 stay raw with a warning.
- Every failure logs one `[ForgeSkin]` line and leaves the weapon vanilla.
- Every line is also written, flushed at once, to `<plugin folder>/forgeskin.log` (cleared at launch), which the app reads
  after 应用到游戏 to report what the game did; it survives a crash that loses BepInEx's buffered log.

### 3.5 Asset replacement (`assets.*`, `AssetPatch.cs`)

- Targets: the Material objects the catalog's renderers use under the override's material name (the target gear's renderers; for a
  skin without a gear every catalog item that lists the material). A Material object is shared by every gear that uses it, so the
  other catalog items with the same material are reported as a warning.
- Each texture override becomes a new Texture2D, image data inline, in the Material's own file, and that Material's slot points
  at it; the game's textures and `.resS` streams are never changed, so only Material files change and an export never needs a
  `.resS`. The new texture copies the slot's texture (else the material's albedo; colour space then linear for normal and
  metallic-smoothness): same format (crunched DXT as plain DXT, unknown formats as RGBA32 with a warning), full mip chain when it
  had mips, the pack image's size (block formats rounded to a multiple of 4). Normal maps use the game's DXT5nm layout when the
  format has alpha. Encoding: BCnEncoder.Net (BC1/3/4/5/7 and raw RGBA, managed, MIT/Unlicense, maintained); AssetsTools.NET.Texture
  only decodes, and hand-written block encoders would be a second implementation. Replaceable by any BCn encoder behind `Encode`.
- Parameters follow the plugin's rules (emissive switch and `ENABLE_EMISSIVE`, emissive colour times intensity, metallic,
  smoothness, tint); a property the Material does not hold is a warning, never added. Attachments, screens, dynamic layers, sounds
  and part layout need the plugin and come back as warnings, as does alpha dropped by a format without alpha.
- Records: `<dataDir>/game-assets/<buildId>-<game dir hash>/` holds `originals/` (each file copied once before its first change,
  SHA-256 checked), `packs/` (a copy of every applied pack) and `state.json` (per file the original SHA-256, size and write time
  and the last written SHA-256; per skin its targets and warnings). Never inside the game folder. The record is saved after every
  file, and a backup is deleted only once the saved record no longer names it, so an apply or restore that stops halfway is
  continued by the next one; a skin being removed stays on record until all its files are rebuilt, and a failed re-apply puts
  the skin's previous pack copy back.
- Every apply or removal rebuilds the touched files from their originals with all applied skins in order (a later skin wins per
  channel), writes a temp file beside the target, swaps it in with the shared game reader closed, and puts the original write time
  back so the game's build id stays the same. Before writing, a recorded file must still match what was written (or the original,
  after a Steam file check); anything else is `game-changed` and nothing is overwritten. A game file that still matches a patch
  recorded for an older build is `stale-patch`: those backups are never restored onto a newer build.

## 4. Feature parity with the old Python tool (all required)

Skin workspace
- Locate Steam/GTFO automatically; reselect directory; show connection state.
- Catalog: main weapons, special weapons, melee weapons (head, neck, handle, pommel), tools (bio tracker, C-foam launcher,
  mine deployer, sentry guns: main, grip, delivery, payload, targeting, screen; the sentry's gun parts on its turret mount),
  the hacking tool, resource packs, consumables and large carry items (every ItemDataBlock item of those slots with a
  first-person prefab; key items and commodities have none, and an unreleased item whose visible renderers all use
  Unity's Default-Material placeholder is left out) and attachments (mags, lights, sights, lenses, ammo: 9mm, .308, 12G, .45 ACP, revolver,
  whole bullet and empty shell as separate parts, revolver speed loader); official order; "official type · English name"; calibre labels.
- Default gun light per weapon (Gun Light A–D or PEQ-1), off by default; every part has an enable checkbox (lights and ammo on).
- Choose a texture folder (or drop files): channel and part recognition with confidence; low-confidence items highlighted and
  must be confirmed; any cell can be replaced manually; "allow missing parts/channels to keep vanilla".
- Four channels; emissive colour (RGB, two decimals, ±0.1 steppers) and intensity shown only when an emissive texture exists;
  emissive mask; apply to all parts.
- 2D channel previews and 3D view of the assembled weapon from the local game; native emissive preview (only bright emissive pixels
  glow); ambient light intensity and colour, 0 = only emissive; auto-rotate ~30°/s; all persisted.
- Output size: keep user size (default), follow original, 1K/2K/4K; always keep aspect ratio.
- Undo (Ctrl+Z) for mapping, part toggles and emissive edits.
- OBJ export: per part or merged (continuous indices), original material names, MTL and manifest; timestamped folders.
- Save/load projects; autosave.
- 版本快照 (snapshots), per skin: save the edited skin under a name (default the time), list them (name, time), restore one
  (after a confirm; the current state is saved as a snapshot first and the restore is one undo step; the skin keeps its id
  and name), delete one (after a confirm). Stored with `project.save` in `<dataDir>/autosave/<item>/<skinId>.snapshots/<time>.forgeskinproj`,
  the snapshot's name in the document's `name`; its paths are relative to that folder (`../sounds/...`), so they resolve
  after restore. `project.list` of the item folder does not look into subfolders, so snapshots are not skins. Deleting the
  skin deletes them.
- 导出前体检 (check): every skin of the item through the build checks (unconfirmed matches, numbers the pack refuses, an
  empty skin, missing channels when not allowed) plus the files it names that are gone (`project.missing`); OK or each
  problem in plain words. A problem of the edited skin selects what it is about; another skin's opens that skin. Apply and
  export point to it when their check fails. Baked layer atlases are checked only by the build.
- Several skins per item: create (blank or a copy of the current one), rename, delete (with confirmation; also removes it
  from the chosen profile), switch which one is edited, and choose which one the game uses. Each skin is its own project
  with its own skin id, autosaved to `<dataDir>/autosave/<item>/<skinId>.forgeskinproj`. "Apply to game" installs the edited skin
  and makes it the item's active one; the item's other installed skins stay installed but inactive (one active per item).
- Right panel: the selection's inspector split into categories shown one at a time, picked from a vertical icon bar on its
  right edge with counts (skin: overview, sounds; part: texture, livery, layers, attachments, placement, sounds;
  attachment: placement, look, screen, effects, sounds); the last category per kind is remembered. Sounds belong to the
  whole skin and are reached there, not from the part tree. The part tree has a search over parts, layers and attachments.
- The 3D view's 模拟 buttons play the event as the game would: layers, attachment effects and the skin's sounds for it.
- 开发者模式 (settings, off by default): shows the log panel and the game log; off, logs still go to the log file and only
  errors show. Settings also has 检查更新 (the installed copy's update check on demand, with the current version).
- Log panel; log file under the data dir; data dir `GTFO_UserData` next to the exe, or for an installed copy (which runs
  from `app-<version>/`, replaced by every update) in the install folder above it.

Layout (every mode)
- Select any part, sub-part (from the 3D view or the part tree) or attachment and move/rotate/scale it with the same gizmos,
  G/R/S keys and numeric fields; hide it; reset to vanilla.

Attachment and screen workspace (replaces DLY, generalised to every weapon)
- 3D weapon view; click a part to select; picking an attachment in the list focuses the view on it; per-part hide (eye icon);
  select the screen even when fully transparent.
- Screen position (mm), rotation, size, scale, surface gap, text/panel/border colours (a part is drawn opaque unless fully
  transparent, as in game), low-ammo colour (text and glow), a gradient switch and the clip percentage where the tint
  starts, glow strength and 游戏里发光 (glow in game)
  on/off; digits 自动
  (automatic, the default) or 1–6, the info tip saying what automatic picks for the weapon or that its ammo limit is unknown;
  static text takes any characters and fonts: bundled, installed on the computer, or a font file. Sources: ammo (mag,
  reserve, total), kills, damage dealt (this weapon / all weapons), damage taken, accuracy, health, infection, fixed text;
  a 数字 / 进度条 (digits / bar) switch only for sources with a maximum. The preview draws the face as the plugin builds it,
  with sample values: ammo counts down, damage counts up, percentages show 87%.
- Edit outline, corner handles and RGB axes; "focus selection".
- Modal G/R/S with X/Y/Z axis lock in part-local axes; click/Enter confirm, right-click/Esc cancel; rotation rings; scale handle;
  numeric fields stay in sync.
- Fixed views (front/back/left/right/top/bottom/iso), numpad 1/3/7, 5 perspective/ortho, Alt+right-drag snaps to nearest view.
- "Snap to surface": click places the screen at the hit point facing the hit triangle.
- Import GLB models as attachments.
- 添加形状: line, tube, sphere, ring, arc, box, grid, cylinder, cone, panel; a few plain parameters each (length, radius,
  thickness, segments, 线框 wireframe). 全息 switch with 闪烁 (flicker) on every attachment; screen 边框样式.
- Presets autosave; "apply to test profile" hot-reloads in game with the development switch on.

Dynamic layer workspace
- Layers: image, natural-sorted image sequence (an animated GIF counts as its frames), MP4 (frames extracted), text; per layer
  fps, loop, opacity, crop, rotation, scale, UV position; 3D decal preview and 2D UV canvas that follow a drag live (one undo
  step per gesture); build and install. A GTFO/BlendHS material (vertex-coloured, no albedo texture) is packed with the
  editor's baked albedo and metallic map and drawn in game with the gear's textured shader, so layers and textures show on it.
- New: per layer trigger and play mode; per attachment trigger effects; a "触发预览" button per event that plays it in the 3D view.
- A livery decal from an animated GIF has the same trigger, play mode and fps controls (only GIF decals show them; fps
  defaults to the GIF's own rate) and plays in the 3D view (§3.1 Animated decal).

Sounds (new)
- Add sounds to the skin on kill, low ammo or another weapon event; pick any audio file (converted to Ogg locally), volume,
  low-ammo threshold, preview playback.

Install
- Install plugin and skins into a chosen profile; uninstall; list installed skins; GTFO-running check only where the DLL is replaced.

## 5. Tests

- Format: pack round trip; a deliberately broken pack is rejected with its code (one per code family).
- Matching algorithm on a fixture folder.
- Main flow on the local game (local-resource layer, reports "not run" without GTFO): locate → catalog → preview HEL → build pack with
  an albedo override, an ammo screen and a GLB attachment → install into a profile.
- In-game: plugin loads the pack and logs applied renderers, attachment, screen and sounds for the wielded weapon.
- Asset replacement round trip on a copy of the game files the HEL needs (local-resource layer): apply an albedo override, reopen
  the patched file (every original object byte-identical except the Material, the added texture decodes to the pack image within
  DXT1 tolerance), restore gives back the original bytes.
- Assembly integrity: `assembly.verify` over every readback file must pass (editor assembly == game assembly); a deliberately
  shifted readback must be caught.

## 6. Remaining work

1. Livery: check a baked livery in game (plugin load and game-file replacement); weapon-list thumbnails do not show it yet.
2. In-game skin picker (§3.4): verify in game.
3. Run ForgeSkin together with ForgeCore and the other Forge mods in one profile; if they conflict (both patch the same
   game methods or replace the same gun meshes), move ForgeSkin into the Forge repository as a standalone mod on its libraries.
4. Holograms: check in game how `Cell/Player/Display_Hologram` draws a shape and a screen (the plugin logs its properties
   on the first hologram) and set the colour properties it really has; compare with `HologramShader` off; check styled
   borders and flicker.
