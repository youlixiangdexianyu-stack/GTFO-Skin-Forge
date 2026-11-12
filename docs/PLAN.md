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
app/                        Electron (Electron Forge + Vite) + React + TypeScript + three.js (react-three-fiber)
  src/main/                 window, dialogs, spawns and talks to the backend
  src/preload/              typed contextBridge API
  src/renderer/             editor UI, 3D preview, video/text frame rendering
src/ForgeSkin.Format/       net6.0. SkinDocument (project == pack manifest), SkinPack (zip + validation), ForgeSkinMesh
src/ForgeSkin.Backend/      net10.0 console, self-contained single file, shipped as Electron extraResource
src/ForgeSkin.Plugin/       net6.0 BepInEx 6 IL2CPP plugin
tests/                      dotnet test project(s) + the app's own tests
docs/PLAN.md                this file
```

Dependency direction: Plugin → Format; Backend → Format; app → backend over stdio only. The Electron side never parses
Unity files, packs or projects itself; everything about formats goes through the backend. TypeScript types for
SkinDocument are generated from the backend's `schema` command (System.Text.Json `JsonSchemaExporter`) with
json-schema-to-typescript; never hand-written.

Coordinates: everything stored is GTFO weapon space (Unity, left-handed, Y up, metres, 1.0x game scale). GLB exported for
preview converts Unity → glTF by negating Z (positions, normals, tangents.z; winding flipped). The renderer converts
poses with one helper (`unityToThree` / `threeToUnity` in `app/src/renderer/space.ts`); nothing else converts.

## 3. Interfaces

### 3.1 Skin document and pack (`src/ForgeSkin.Format`)

- `SkinDocument` is both the editor project (`.forgeskinproj`, JSON, file references relative to the project file) and the pack
  manifest (`manifest.json` in a `.forgeskin` zip, references are zip paths). `editor` holds UI state and is dropped when packing.
- `SkinPack.Open` validates; error codes: `schema`, `no-id`, `no-gear`, `no-manifest`, `no-material`, `bad-path`,
  `missing-file`, `bad-image`, `bad-trigger`, `bad-vector`, `no-atlas`, `bad-atlas`, `no-anchor`, `empty-attachment`, `bad-screen`, `no-glyphs`, `bad-mesh`, `bad-audio`.
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
  ammoPickup, flashlightOn, flashlightOff; every one but `always` is an event). `DynamicLayer.Trigger` + `Play` (loop / once /
  loopFor seconds): a non-`always` layer shows the base texture until its event, then plays. `Attachment.Effects`: list of
  {trigger, action: flash (emission colour, intensity, seconds) / show (seconds)}. `ScreenSource.Kills` counts the local
  player's kills with this weapon this level.
- `SkinDocument.Sounds` (`SkinSound`): {trigger (not always), file, volume 0–1, lowAmmoFraction}. The file is Ogg Vorbis, mono
  or stereo, at most 10 s; the editor converts any audio the author picks (WAV, MP3, FLAC, OGG, M4A) to Ogg Vorbis locally with
  `wasm-media-encoders` and keeps an existing Ogg Vorbis file as is. Packed as `sounds/<id>.ogg`.

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
| `project.delete` | `{path}` → deletes one `.forgeskinproj` |
| `mesh.import` | `{glb}` → `{forgeskinmesh (cache path), preview glb, bounds}` |
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
build), `bad-backup` (a backup is missing or its SHA-256 does not match), plus the format codes in §3.1. `gameDir` overrides the
located game for `assets.*` (tests run on a copied folder, never on the real install).

Plugin source for `plugin.install`: `plugin/` next to the backend exe in a packaged app; in a checkout the newest of the plugin
project's Release/Debug output and the committed prebuilt copy in the repo's `plugin/` folder. Every Release build of the plugin
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
  so other guns that use the same part are untouched. Re-applies when the feeder rewrites its blocks.
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
- Readback: debug only; with it off the plugin reads nothing on the game's spawn path for it. With `Readback = true` in ForgeSkin.cfg the plugin writes, for every assembled gear, the local TRS of every part
  root and descendant relative to the gear root to `BepInEx/ForgeSkin/readback/<gearId>.json` (vanilla, before any skin).
  The backend compares it with its own assembly (`assembly.verify`); any transform off by more than 1 mm or 0.1° fails.
- Screens: everything is opaque geometry with the gear shader (it has no transparency and other shaders break the first-person
  projection): a border ring `BorderWidth` wide, a panel quad inside it (each left out only when fully transparent), and the
  text as real glyph meshes. The pack carries one mesh per glyph "0123456789 -" (blank has none) traced from the strip the app
  renders with the chosen font (marching squares at alpha 0.5, simplified, triangulated with holes by LibTessDotNet); a static
  screen carries its whole text, any characters, as one mesh the same way. Text shrinks to fit 95% of the inner width.
  Colours: albedo is the colour (a 1×1 texture); with `GlowInGame` text and border also emit the colour at `Glow` × 0.2,
  else no emission. The 0.2 step is set from the game's own glowing gear materials (_EmissiveColor × map: flashlight lens
  1.0, HSU lights 0.54, tool screens 0.45–0.74, shotgun lights 0.07), so the default 1.5 glows clearly and the slider's top
  (5) matches the flashlight lens. The panel emits its colour at the shader's unit 0.01 (shown as itself, no bloom). Below the low-ammo threshold the text's albedo and emission both switch to `LowAmmoColor`.
  `Digits` 0 is automatic: the digits of the largest value the source can show (clip size, the storage's bullet max cap, or
  both); automatic kills show the value's own digits, centred, up to 4; `PadZero` pads to that width. Updates only when the
  value changes; `AmmoClip` reads the weapon's current clip, `AmmoReserve` / `AmmoTotal` the player's ammo storage.
  After creating a screen the plugin logs one line with the text and border colours and emission actually applied and the
  template material's shader and keywords.
- Dynamic layers: atlas frames are copied into the target texture region with `Graphics.CopyTexture` (GPU), never a full re-upload.
- The plugin identifies a gear by its offline gear id: `GearIDRange.PlayfabItemInstanceId` ("OfflineGear_ID_<id>"), or for a
  range rebuilt from a packet the game's own gear (`GearManager.GetAllGearForSlot`) with the same components.
- Events come from the local player's own gear only:
  - kill / hit: the local player's damage made an enemy die / hit it and it lived, while this gear was wielded (guns and melee);
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
  and the last written SHA-256; per skin its targets and warnings). Never inside the game folder.
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
- Several skins per item: create (blank or a copy of the current one), rename, delete (with confirmation; also removes it
  from the chosen profile), switch which one is edited, and choose which one the game uses. Each skin is its own project
  with its own skin id, autosaved to `<dataDir>/autosave/<item>/<skinId>.forgeskinproj`. "Apply to game" installs the edited skin
  and makes it the item's active one; the item's other installed skins stay installed but inactive (one active per item).
- Log panel; log file under the data dir; portable data dir next to the exe (`GTFO_UserData`).

Layout (every mode)
- Select any part, sub-part (from the 3D view or the part tree) or attachment and move/rotate/scale it with the same gizmos,
  G/R/S keys and numeric fields; hide it; reset to vanilla.

Attachment and screen workspace (replaces DLY, generalised to every weapon)
- 3D weapon view; click a part to select; per-part hide (eye icon); select the screen even when fully transparent.
- Screen position (mm), rotation, size, scale, surface gap, text/panel/border colours (a part is drawn opaque unless fully
  transparent, as in game), low-ammo colour (text and glow), glow strength and 游戏里发光 (glow in game) on/off; digits 自动
  (automatic, the default) or 1–6, the info tip saying what automatic picks for the weapon or that its ammo limit is unknown;
  static text takes any characters and fonts: bundled, installed on the computer, or a font file. The preview draws the face
  as the plugin builds it.
- Edit outline, corner handles and RGB axes; "focus selection".
- Modal G/R/S with X/Y/Z axis lock in part-local axes; click/Enter confirm, right-click/Esc cancel; rotation rings; scale handle;
  numeric fields stay in sync.
- Fixed views (front/back/left/right/top/bottom/iso), numpad 1/3/7, 5 perspective/ortho, Alt+right-drag snaps to nearest view.
- "Snap to surface": click places the screen at the hit point facing the hit triangle.
- Import GLB models as attachments.
- Presets autosave; "apply to test profile" hot-reloads in game with the development switch on.

Dynamic layer workspace
- Layers: image, natural-sorted image sequence (an animated GIF counts as its frames), MP4 (frames extracted), text; per layer
  fps, loop, opacity, crop, rotation, scale, UV position; 3D decal preview and 2D UV canvas that follow a drag live (one undo
  step per gesture); build and install. A GTFO/BlendHS material (vertex-coloured, no albedo texture) is packed with the
  editor's baked albedo and metallic map and drawn in game with the gear's textured shader, so layers and textures show on it.
- New: per layer trigger and play mode; per attachment trigger effects; a "触发预览" button per event that plays it in the 3D view.

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

1. Packaging: Electron Forge installer bundling the backend and the plugin.
2. Remove the old Python tool and the two HEL plugins once §4 is verified.
3. Livery painting (after the editor redesign), like a racing game's livery editor:
   - basic material presets for a part's paint (metallic/smoothness looks): matte, gloss, satin, metal, brushed metal,
     chrome, rubber, plastic, carbon fibre, painted steel, worn metal, glow (emissive);
   - fill: a part painted one colour (the base coat, over or instead of its albedo);
   - image decals placed on the part's surface in the 3D view, following its curves (position, size, rotation, mirror);
   - a brush on the surface: straight lines, circles and free-hand strokes;
   - per layer: tint, opacity, blend mode (normal, multiply, screen, overlay, add) and a mask (painted with the brush,
     hiding parts of that layer).
   Decals and strokes are layers under their part in an explicit order that decides what is on top. Preview live; when packing,
   bake every decal into the part's albedo by rendering the mesh in UV space and projecting each fragment's position
   into the decal frame, with a depth limit so a decal does not reach the far side. The result is an ordinary texture:
   no plugin change, and it works for game-file replacement too.
4. In-game skin picker: choose the active skin per item from the loadout screen, built on ForgeCore's UI once its library
   merge has landed (the plugin then depends on ForgeCore).
