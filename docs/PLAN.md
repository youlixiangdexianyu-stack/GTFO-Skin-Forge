# GTFO Skin Forge — rewrite plan

The single plan for this repository. Keep only current requirements; delete finished items from §6.

## 1. Product

A Windows desktop tool for GTFO gear skins (guns, melee weapons, tools, resource packs, consumables and large carry items),
plus one BepInEx plugin that applies them in game.

1. **New skins.** Replace each weapon part's textures (Albedo, Normal, MetallicSmoothness, Emissive) and material parameters (emissive colour and intensity, metallic, smoothness, tint), e.g. put an anime character on the gun.
2. **Attachments.** Mount models or parts on the gun (imported GLB), and screens that show live data, e.g. a cyberpunk ammo counter that counts down as the player fires.
3. **Dynamic layers and triggered effects.** Animated images, image sequences, video and text composited onto a part's
   texture, looping or played on a game event (kill, hit, fire, reload, equip, aim, low ammo, ... — §3.1) — e.g. an eye on the gun blinks on
   every kill, like COD weapon effects. Attachments can flash their emission or show for a few seconds on an event.

Scope is skins, simple models and these effects; replacing a part with a model (re-pointing muzzle, sight and hand) is out.
4. **Part layout.** Every part of the weapon (vanilla parts and their sub-transforms, and every attachment) can be moved,
   rotated, scaled and hidden; the game shows the same layout.

Every feature of the old Python tool stays (§4).

Skins are loaded by the plugin at runtime. The tool never writes game files (`GTFO_Data` stays untouched).
First version is local only: other players see vanilla weapons.

## 2. Architecture

```
app/                        Electron (Electron Forge + Vite) + React + TypeScript + three.js (react-three-fiber)
  src/main/                 window, dialogs, spawns and talks to the backend
  src/preload/              typed contextBridge API
  src/renderer/             editor UI, 3D preview, video/text frame rendering
src/SkinForge.Format/       net6.0. SkinDocument (project == pack manifest), SkinPack (zip + validation), SfMesh
src/SkinForge.Backend/      net10.0 console, self-contained single file, shipped as Electron extraResource
src/SkinForge.Plugin/       net6.0 BepInEx 6 IL2CPP plugin
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

### 3.1 Skin document and pack (`src/SkinForge.Format`)

- `SkinDocument` is both the editor project (`.sfproj`, JSON, file references relative to the project file) and the pack
  manifest (`manifest.json` in a `.gtfskin` zip, references are zip paths). `editor` holds UI state and is dropped when packing.
- `SkinPack.Open` validates; error codes: `schema`, `no-id`, `no-gear`, `no-manifest`, `no-material`, `bad-path`,
  `missing-file`, `bad-image`, `bad-trigger`, `bad-vector`, `no-atlas`, `bad-atlas`, `no-anchor`, `empty-attachment`, `bad-screen`, `no-glyphs`, `bad-mesh`, `bad-audio`.
- `.sfmesh` is the attachment mesh format (see `SfMesh`).
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
<profile>/BepInEx/plugins/SkinForge/SkinForge.Plugin.dll   (+ SkinForge.Format.dll, NVorbis.dll)
<profile>/BepInEx/plugins/SkinForge/skins/*.gtfskin
<profile>/BepInEx/plugins/SkinForge/skins/active.json        { "targets": { "gear:<gearId>" | "item:<itemId>": "<skinId>" }, "global": ["<skinId>", ...] }
<profile>/BepInEx/config/SkinForge.cfg
<profile>/BepInEx/LogOutput.log                              plugin logs with prefix [SkinForge]
```

`global` skins have no weapon target and apply to every gear that uses their materials (mags, sights, lights, ammo).

### 3.3 Backend protocol (stdio, one JSON object per line, UTF-8)

Request `{"id":1,"method":"catalog.get","params":{...}}` → `{"id":1,"result":...}` or
`{"id":1,"error":{"code":"...","message":"..."}}`. Progress: `{"event":"progress","data":{"id":1,"done":3,"total":10,"text":"..."}}`.
Logs go to stderr and `<dataDir>/logs/backend.log`, never stdout.

| Method | Params → Result |
|---|---|
| `app.info` | → version, dataDir |
| `game.locate` | → `{gameDir, dataDir, found, running}` (Steam registry + libraryfolders.vdf + common dirs) |
| `game.setDir` | `{dir}` → same as locate |
| `profiles.list` | → BepInEx profiles: r2modman / Thunderstore Mod Manager profiles and the game dir; `{name, path, hasBepInEx, pluginVersion}` |
| `catalog.get` | → gear and attachments in official order: `{id, name, nameZh, category, slot (main/special/melee/tool/pack/consumable/carry/attachment/ammo), gearId, itemId, parts:[{id, role, label, materials:[...] , defaultEnabled}]}`; melee and tool parts use the `melee_*` / `tool_*` ids; an item with its own prefabs is one `body` part made of its first-person prefabs |
| `preview.weapon` | `{id}` → `{glb, parts:[{id, nodes:[...]}], textures:{material:{channel:png}}, params:{material:{...}}}`; cached under dataDir/cache, built from the local game |
| `match.folder` | `{weaponId, folder}` → per part per channel `{file, confidence, reason}` + unmatched files |
| `match.files` | `{weaponId, files:[...]}` → same, for drag-and-drop of loose files |
| `project.new/load/save` | SkinDocument in/out; `project.load` also imports legacy `.gtfoskin`, `.gtfdyn` and DLY presets |
| `mesh.import` | `{glb}` → `{sfmesh (cache path), preview glb, bounds}` |
| `pack.build` | `{project, projectPath, out, sizeMode: keep/original/1k/2k/4k}` → `{pack, warnings}`; resizes keeping aspect, bakes layer atlases and glyph atlases from frame PNGs the renderer wrote |
| `pack.install` | `{pack, profile, activate}` → writes skin + active.json (works while the game runs; plugin hot-reloads) |
| `pack.list / pack.remove / pack.activate` | manage installed skins in a profile |
| `plugin.install` | `{profile}` → copies plugin; refuses with `game-running` while GTFO runs and the DLL is locked |
| `export.obj` | `{weaponId, parts, mode: separate/merged, outDir}` → files; folder `YYYY-MM-DD-HHMMSS-<weapon>.OBJ`, MTL + manifest |
| `schema` | → JSON schema of SkinDocument |

Error codes: `game-not-found`, `game-running`, `bad-request`, `not-found`, plus the format codes in §3.1.

### 3.4 Plugin behaviour

- Loads every pack in `skins/` at start; `FileSystemWatcher` reloads changed packs and `active.json` within ~0.5 s and re-applies to
  live weapons.
- Applies after the game finished building a gear (all parts spawned and `GearMaterialFeeder` set up). Overrides go through each
  renderer's `MaterialPropertyBlock` (merged with the feeder's block) or a per-renderer material instance; never the shared material,
  so other guns that use the same part are untouched. Re-applies when the feeder rewrites its blocks.
- Applies to the local player's gear, first and third person.
- Attachments are parented to the named transform under the anchor part and follow animation, recoil and ADS.
- Part layout is written once per assembled gear (absolute local values, only the fields set); no per-frame work. Hidden
  parts disable their renderers only. Hand IK and sight aligns that are children of a moved part move with it.
- Applies to every gear the local player sees as their own: first person, third person and the lobby loadout preview.
  Items with their own prefabs (packs, consumables, carry items, hacking tool) have no gear holder: they are skinned on the
  local player's first-person item when it is wielded (the wielded item is compared each frame, one pointer); their third
  person and world copies stay vanilla.
- Readback: with `Readback = true` in SkinForge.cfg the plugin writes, for every assembled gear, the local TRS of every part
  root and descendant relative to the gear root to `BepInEx/SkinForge/readback/<gearId>.json` (vanilla, before any skin).
  The backend compares it with its own assembly (`assembly.verify`); any transform off by more than 1 mm or 0.1° fails.
- Screens: a quad panel + border + digits drawn from the glyph atlas; updates only when the value changes; `AmmoClip` reads the
  weapon's current clip, `AmmoReserve` / `AmmoTotal` the player's ammo storage.
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
  Loudness is the sound's own volume, set in the app and hot-reloaded like every other edit; there is no second volume setting.
- Nothing in the product uses the network: the app, backend and plugin work offline.
- Textures are decoded once per pack and cached; no per-frame allocation, string or dictionary lookups on the update path.
- Every failure logs one `[SkinForge]` line and leaves the weapon vanilla.

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
- Log panel; log file under the data dir; portable data dir next to the exe (`GTFO_UserData`).

Layout (every mode)
- Select any part, sub-part (from the 3D view or the part tree) or attachment and move/rotate/scale it with the same gizmos,
  G/R/S keys and numeric fields; hide it; reset to vanilla.

Attachment and screen workspace (replaces DLY, generalised to every weapon)
- 3D weapon view; click a part to select; per-part hide (eye icon); select the screen even when fully transparent.
- Screen position (mm), rotation, size, scale, surface gap, text/panel/border colours, separate panel and border opacity, low-ammo colour.
- Edit outline, corner handles and RGB axes; "focus selection".
- Modal G/R/S with X/Y/Z axis lock in part-local axes; click/Enter confirm, right-click/Esc cancel; rotation rings; scale handle;
  numeric fields stay in sync.
- Fixed views (front/back/left/right/top/bottom/iso), numpad 1/3/7, 5 perspective/ortho, Alt+right-drag snaps to nearest view.
- "Snap to surface": click places the screen at the hit point facing the hit triangle.
- Import GLB models as attachments.
- Presets autosave; "apply to test profile" hot-reloads in game.

Dynamic layer workspace
- Layers: image, natural-sorted image sequence, MP4 (frames extracted), text; per layer fps, loop, opacity, crop, rotation, scale,
  UV position; 3D decal preview and 2D UV canvas; build and install.
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
- Assembly integrity: `assembly.verify` over every readback file must pass (editor assembly == game assembly); a deliberately
  shifted readback must be caught.

## 6. Remaining work

1. Backend: game locate, catalog, extraction (meshes → GLB, textures → PNG, material params), weapon assembly from game data,
   matching, projects + legacy import, pack build/install, OBJ export, schema.
2. Plugin: loading, hot reload, material overrides, attachments, screens, dynamic layers.
3. App: all workspaces in §4 with a simpler flow.
4. Packaging: Electron Forge installer bundling the backend and the plugin.
5. Remove the old Python tool and the two HEL plugins once §4 is verified.
