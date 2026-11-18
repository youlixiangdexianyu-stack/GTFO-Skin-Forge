# Driving the ForgeSkin editor with an AI agent

The editor is an Electron app with one window. An agent drives it through the Chromium DevTools protocol (Playwright
`connectOverCDP`, a DevTools MCP server, or plain CDP). Started with `--ai-control` the page offers **`window.forgeAI`**, a
structured interface to everything the editor does: read the whole state as JSON, change any field of the skin, place a
decal by UV, set the camera, render the 3D view to a PNG, read the UV islands. Use it first; the UI route (sections 3 to 9)
is the fallback for anything it does not cover. Both go through the same editor actions, so validation, undo (one step per
command) and autosave behave the same.

## 1. Start and connect

The debugging port opens only when the editor is started with `--ai-control` (or the environment variable
`FORGESKIN_AI_CONTROL`, set to a port or to `1`):

| Copy | Start it with |
|---|---|
| Source checkout | `pnpm start -- -- --ai-control` in `Mods/ForgeSkin/Editor` (its Vite dev server takes port 5190, the next free one when that is taken; `FORGESKIN_DEV_PORT` sets another) |
| Installed (Setup.exe) | `& "$env:LOCALAPPDATA\ForgeSkin\ForgeSkin.exe" --ai-control` (the launcher in the install folder forwards its arguments to the newest `app-<version>`) |
| Installed, from the Start-menu shortcut | set the variable first: `setx FORGESKIN_AI_CONTROL 1`, then start ForgeSkin as usual (clear it again with `setx FORGESKIN_AI_CONTROL ""`) |
| Portable zip | `ForgeSkin.exe --ai-control` inside the unzipped `ForgeSkin-win32-x64` folder |

`--ai-control=9444` picks the port (an integer from 1024 to 65535, default 9333; anything else is ignored and one `ai-control`
line goes to `GTFO_UserData/logs/app.log`). The port listens on 127.0.0.1 only. All copies run the same compiled code; nothing
depends on a dev server.

- The editor has no single-instance lock: a second launch is a separate editor on the same data folder and settings. Close an
  editor that is already open before starting one for the agent.
- Check it is up: `http://127.0.0.1:9333/json/list` has one target of type `page` titled `ForgeSkin`. Connect to that page
  (Playwright: `chromium.connectOverCDP('http://127.0.0.1:9333')`, then `browser.contexts()[0].pages()[0]`).
- The flag keeps the window rendering when it is covered or minimized, and lets you answer file dialogs beforehand
  (section 5).
- Close the editor window (or end the process) when you are done; the port closes with it.

## 2. The control surface: `window.forgeAI`

Call it with `Runtime.evaluate` (`awaitPromise: true, returnByValue: true`) or Playwright `page.evaluate`. Every call resolves
to `{ ok: true, ... }` or `{ ok: false, code, message }`; successful commands also return `undoSteps`, the undo steps they
added (0 or 1). Nothing fails silently.

```js
await forgeAI.state()                                   // open item, skin, selection, parts, problems, camera, the whole skin document
await forgeAI.command('selectWeapon', { id: 'helr' })   // any command; command('help') lists them all with their arguments
await forgeAI.patch([{ op: 'set', path: 'materials/material=Receiver/livery/id=ab12/opacity', value: 0.5 }])
await forgeAI.get('materials/0/livery')                  // one value of the document
```

| Command | What it does |
|---|---|
| `state` | `weaponId`, `skinId`, `selection`, `busy`, `undoSteps`, `redoSteps`, `item` (parts with id, role, label, materials), `skins`, `problems` (what blocks a build), `camera`, `doc` (the skin document, the JSON of the project file) |
| `help` | every command with its arguments |
| `listWeapons {query?}`, `selectWeapon {id}` | the catalog; open an item and wait for its 3D view |
| `switchSkin {id}`, `newSkin {copy?}` | skins of the open item |
| `select {kind, id, material?, sub?}` or `{clear: true}` | selection: `part`, `attachment`, `layer`, `livery`, `sound`, `fx` |
| `get {path}`, `patch {ops, key?}` | read or change **any** field of the skin document; see below |
| `pickNext {paths}` | answers the next file dialog, which then does not open (section 5) |
| `undo`, `redo` | |
| `addLivery {material, kind, part?, image?}` | paint layer: `fill`, `decal` (needs `image`, an absolute path), `brush`, `pattern`, `wear`, `trim` (panel lines), `text`, `gradient`, `tile`; returns its `id` |
| `removeLivery`, `moveLivery {by}`, `setFinish {material, finish}` | |
| `addLayer {material, layer}`, `removeLayer` | animated layer (`kind`, `sources`, `text`, ...) |
| `addAttachment {kind, glb?, shape?, params?, wire?, charm?}`, `removeAttachment {id}` | `kind` is `screen`, `mesh`, `shape` or `image` (next row); `mesh` imports an absolute `.glb`/`.gltf` path; `shape` adds a built-in shape as the **Shapes** menu does (a hologram in the skin's colour, or a solid charm with `charm`): `shape` is `line`, `sphere`, `ring`, `arc`, `box`, `grid`, `cylinder`, `cone`, `panel`, `hexPanel`, `reticle` or `tickArc`, `params` changes its defaults (`radius`, `width`, `height`, `depth`, `length`, `thickness` in metres; `segments`, `cells`, `ticks` as counts; `angle` in degrees; `help` and a wrong name list each shape's own), `wire` makes a box, sphere, cylinder, cone or panel a wireframe or solid. A `mesh` keeps the model's own pictures: the base colour, and where the model has them its emissive, normal and metallic-roughness pictures, go onto the attachment's material as PNGs at their own size (scaled down only past 16384, the largest texture the game makes), shown in the 3D view and in game; the attachment colour tints them, and as a hologram the base-colour picture is what glows, in the attachment colour. Returns the attachment `id` and its `textures` (channel to PNG path) |
| `addAttachment {kind: 'image', image, width?}` | a picture (absolute path of a png, jpg, jpeg or webp) as a hologram screen, as the menu's **Picture hologram…** does: an image plane shape (`shape` `image`, `params` `width`, `height` in metres, editable like any shape) `width` wide (default 0.03) with the picture's proportions; the picture is copied as a PNG at its own size (scaled down only past 16384) into `images/` beside the skin and becomes the attachment's base-colour picture, shown as the hologram's glow; colour white. Fails with `bad-image` when the file is not a readable picture |
| `addHoloSet {set}` | a matching set of hologram shapes placed from the open item's parts, as **Hologram sets** does: `sight` (a tick arc and a ring in front of the sight on its aim line), `barrel` (three tick rings at the barrel's end, smaller towards and past the muzzle), `strip` (a wire panel and two bars on the receiver's left side). One line width, tick length, colour fade, flicker and edge glow; each element is an ordinary shape attachment named `<set> · <shape>`. Edge glow costs one code effect per element: when the skin has too few of its 8 left the set comes without it (`outline: false`). Returns `ids` |
| `placeOnSurface {id, view: [x, y]}` or `{id, point, normal, up?}` | lays an attachment flush on the gear as a click in the **Snap to surface** mode does: its back on the surface plus the gap (a screen's own, else 0.5 mm), its face along the normal. `view` is a point of the 3D view (0..1 across and down; the image's top follows the camera's up, attachments are not hit); `point` and `normal` are world coordinates, `up` the direction the top points to (default world +Y). Fails with `no-hit` when no gear is under `view`. Returns `position`, `rotation`, `worldPoint`, `worldNormal`, `part` |
| `duplicate {kind, id, material?, toMaterials?}` | copy a `livery` layer, an animated `layer` or an `attachment`; `toMaterials` copies a layer onto several materials |
| `setTexture {material, channel, path}`, `setPartEnabled {part, on}`, `setPartTransform {part, path?, position?, rotation?, scale?, visible?}` | |
| `placeDecal {material, id, uv, size?, angle?, depth?}` or `{..., point, normal, up?}` | puts a decal, panel-lines or text layer on the surface exactly as dragging in the 3D view does. `uv` is `[u, v]` of the material's UV layout (texture orientation, v 0 = top row); `point` and `normal` are world coordinates; `up` is the direction the image's top points to (default world +Y). Fails with `no-hit` when the material has no surface at that UV; where UVs overlap it takes a spot nothing covers, and `covered: true` in the result means every spot is hidden behind other geometry |
| `uvIslands {material}` | the material's UV pieces, largest first: `triangles`, `area`, `bbox [u0,v0,u1,v1]`, `sample` (a point certainly inside the island), `outlines` (polylines of u,v pairs) |
| `importStrokes {material, id, strokes?, mask?, fills?, maskFills?, append?}` | brush strokes and masks as data: polylines of `(u, v, width)` triples in UV space; `fills` are `[u, v]` island seeds |
| `camera {view?, yaw?, pitch?, distance?, target?}` | sets the 3D camera without animation (`view`: front, back, left, right, top, bottom, iso; `yaw` and `pitch` in degrees; `target` in three.js world coordinates); without arguments it reads it. Returns the camera |
| `render {width?, height?, path?}` | the 3D view as a PNG: `dataUrl`, `width`, `height`; `path` also writes it (inside the data folder). `width` alone scales the view as it is; `width` and `height` render the view at that size, so its camera frames that shape without stretching |
| `call {method, params}` | any backend method (`Editor/src/renderer/protocol.ts`, `Methods`) |
| `check`, `buildPack {out}`, `applyToGame` | the pre-build check, a pack build, install into the chosen test profile |
| `batch {commands: [{command, args}]}` | runs commands in order, each with its own undo step; stops at the first failure (`batch-stopped`, with every result so far) |

**Document paths** (`get`, `patch`): segments joined with `/` (or an array). A number indexes an array; `key=value` picks the
array element whose field has that value (`materials/material=Receiver_Machinegun_2/livery/id=ab12`); `-` after an array appends.
`patch` ops are `set`, `add`, `remove` and `merge` (an object into an object), all applied in one edit: one undo step, one
autosave, and a bad op leaves the document untouched. A value with NaN or Infinity is refused (`bad-value`). The skin id
cannot be changed. A file the skin names (picture, model, font file, sound) is an absolute path; a relative one is refused
(`bad-request`). Never paste a project or snapshot file's text into the document: on disk it holds paths relative to that
file; read `state().doc` instead, or open the file (`call project.load`), which gives them absolute. The document's shape
is `state().doc`, with the fields of section 8; a field or layer kind added to the editor later is reachable through `patch`
with no change to this interface.

**Colours** are lists of numbers 0 to 1 whose length is fixed by the field; a wrong length fails the check (`bad-vector`).
Three numbers (sRGB, as the colour pickers show them): an attachment effect's `color` (`attachments/…/effects/…/color`), `charm.color`, `hologram.color2`,
`params.emissiveColor`, a sight reticle layer's `params.reticles/<reticleA|reticleB|reticleC>/color`, and a player colour's `default` and each `palette` entry. Four numbers (sRGB and alpha): `attachments/…/material/color`
(its fourth number is the hologram's or model's alpha), `params.tint`, a livery layer's `color`, and every colour of a code effect
(`codeEffects/…/colors`, sRGB with alpha).

**Reason codes:** `bad-args`, `bad-path`, `bad-value`, `not-found`, `unknown-command`, `not-ready`, `no-view` (no open item or
3D view), `no-hit`, `bad-image`, `import-failed`, `batch-stopped`, `failed` (anything else, with the editor's own message).

A decal by UV, end to end:

```js
const { item } = await forgeAI.state();                                  // item.parts[].materials
const { islands } = await forgeAI.command('uvIslands', { material });    // each island has a `sample` point inside it
const { id } = await forgeAI.command('addLivery', { material, kind: 'decal', image: 'C:/art/logo.png' });
await forgeAI.command('placeDecal', { material, id, uv: islands[0].sample, size: [0.05, 0.03] });
await forgeAI.command('camera', { view: 'iso' });
const { dataUrl } = await forgeAI.command('render', { width: 1024 });   // look at it; no window screenshot needed
```

The decal sits on the side of the surface its normal points to (`placeDecal` returns `worldNormal`); put the camera on that
side to see it.

## 3. UI route: first-start guide and language

With no work open the 3D view offers **Make a weapon skin**, **Make a character** and the guided tour. The book button in
the top bar (named **Tutorial**) is a menu: the written tutorial pages, then five guided tours (first skin, character
models, profiles, hot reload, colour schemes). A guided tour darkens the window and only the lit control takes clicks; to
work freely, press Escape, then **Leave**.


The UI is Chinese or English. A fresh editor follows the system language. All control names below are the English ones.
Switch to English first: **Settings** (gear button, top right) then the **English** option of the language switch, then press
Escape. Controls are found by accessible name (`aria-label`, or the button text); the English names below are exact.

## 4. Screen map

| Area | What it is | How to reach it |
|---|---|---|
| Top bar | **Tutorial** menu (book), **Settings** (language, **Apply automatically**, game and data folders, updates), **Export** menu | always visible |
| Left panel, top | The open work (name, gear, state), then the profile row: **Apply to:** with the chosen game profile (its menu shows whether the game runs and the profile's skin plugin version against the editor's, installs or updates the plugin, lists the profiles and **Choose folder…**), the count of skins in it | always visible |
| Left panel, tab **Works** | the **Apply to** profile picker (each profile with its pack count in its menu), **Weapon skins N / Characters N** tabs, search, **New** (gear gallery or a character model; its arrow: **Copy the open one**, **Open project or old preset…**, **Import a Skins skin group…**, **Add packs…**), a folded bar **N more by others in this profile** with **Move all** that opens to their cards (the same **Apply**, no editing), then the work cards (weapon skins grouped by gear, characters two to a row), in use first. Each card: picture on top with its state over the corner (**Only in the editor** grey, **In the profile** blue, **In use** green with a green frame), name and gear (or **Changes not applied**) beside **Apply** (none on a card in use and unchanged; under the name on a character card); its **⋯** (top right of the picture on hover, or a right click at the pointer) holds **Put in profile**, **Take out of profile**, **Copy**, **Rename**, **Export pack…**, **Snapshots**, **Save to file…**, **Clear current changes**, **Delete work** | button **Works** on the left edge |
| Left panel, tab **Parts** (characters: **Steps**) | The part tree of the open item: a row per part (fold arrow, enable checkbox, name, texture squares; on hover its eye and the **+** menu with the part's add menu), with the part's paint layers, animated layers, code effects and attachments indented below it (each with its eye on hover; an eye stays shown while the thing is hidden). Search box **Search parts, attachments, layers** | button **Parts** on the left edge |
| Centre | The 3D view. Title button **Change item (Ctrl+P)** opens the weapon gallery; the event bar keeps the simulated kill, hit, fire and reload start and puts the other events under **More** | |
| Right panel | Inspector of what is selected: a header that stays on top (its kind, its name, and icon buttons such as **Visible**, **Duplicate**, **Apply to other parts** and the delete button, named by their tooltip), then sections. A section heading with an arrow folds the section (a button with `aria-expanded`; folded sections stay folded next time). Its categories are the icon buttons on the right edge (named by their label) | select a row on the left |
| Status bar | Log button, hot-reload state (game running: an apply shows within seconds; game closed: next start), busy text, `Autosaved`, undo and redo (**Undo · Ctrl+Z**, **Redo · Ctrl+Y**) | always visible |

Right-edge tabs by selection: the whole gun (row **Whole gun** at the top of the part tree, also what a newly opened work
selects: **Look** with the whole-gun texture folder, **Colour schemes** and player colours, **Sounds**, **Info** with
the counts and **Keep missing parts and channels as they are**); a part (**Textures**, **Livery**, **Layers**,
**Attach**, **Place**, **Sounds**); an attachment (**Place**, **Look**, **Screen**, **Effects**, **Sounds**).

Part rows, layer rows and attachment rows are `role="option"` elements that carry `aria-selected`; skin cards are
`role="button"`; each texture slot is a `role="group"` named after its channel (**Albedo**, **Normal**, **Metal/Smooth**,
**Emissive**; on a holographic sight's lens also **Reticle layer A** to **C**, channels `reticleA`..`reticleC`, set with
`setTexture` like the others).

## 5. Files: answering dialogs and drops

Every button that asks for a file or folder (open, import, save, export, add a profile folder) opens the system's dialog,
which a protocol client cannot drive. Answer it before the click instead:

- `forgeAI.command('pickNext', { paths: ['C:\\work\\in\\camo.png'] })`, then click the button: the next dialog does not
  open and takes these paths as if they were chosen there (one path for a single file, a folder or a save; several where
  the button takes several files). Answers queue up, one per dialog, in order.
- The paths must be absolute and are not checked against the dialog: give a file that exists for an open, a folder for a
  folder, and for a save a full file name with its extension in a folder that exists (an existing file is replaced).

Two more ways to bring files in without a dialog:

- **Drop files on the window.** Dispatch a real drag with CDP `Input.dispatchDragEvent` (`dragEnter`, `dragOver`, `drop`)
  whose `data.files` is a list of absolute paths on this machine.
  - Dropped anywhere: image files (or one folder) are matched to parts and channels by file name (section 7); a
    `.forgeskinproj` / `.gtfoskin` / `.gtfdyn` file opens as a project; with a part selected, video or gif files become
    animated layers on it. `.glb` / `.gltf` dropped on the window are refused with a hint (use **Import model (.glb)**).
  - Dropped on a texture slot in the **Textures** tab: that image becomes that channel of the selected part. Use the
    slot's on-screen position (`getBoundingClientRect` of the `role="group"` element) for `x` and `y`.
  Playwright's `setInputFiles` is not enough for these (they are drops, not file inputs); use a CDP session.
- **Apply** on a work's card installs the built pack into the chosen game profile without any dialog (section 6, step 10).
## 6. Main flow

Each step lists the control and what tells you it worked.

1. **Pick an item: a new work.** Left edge **Works**, **New** (or **Make a weapon skin** in the empty window); the gear
   gallery has a search box and slot chips; click a card. Success: a new card named `<item> skin` is the current one
   (`aria-current="true"`, state **Only in the editor**), the 3D view's title shows the item name and the whole gun is
   selected. **Change item (Ctrl+P)** on the 3D view's title switches the item of the open work. Rename: the card's **⋯**
   menu, **Rename**, type, Enter.
2. **Another work on the same item.** **New**'s arrow, **Copy the open one**; or **New** again and the same item.
3. **Select a part.** Click the gun in the 3D view, or left edge **Parts** and the part row by its name. Success: the row has `aria-selected="true"`, the
   right panel's header says **Part** with the part name, and the right-edge tabs include **Textures**.
4. **Replace a texture.** Right edge **Textures**; drop an image on the channel slot (section 5). Success: the slot's badge
   changes from `ORIGINAL` to `REPLACED` and its caption shows `Albedo · <file name>`; the part's row shows a lit channel
   square. A folder of images named after parts and channels can be dropped on the window instead; a toast says how many
   textures were matched, and unmatched files are listed in the whole gun's **Look** tab under the section headed by their count. Matches the
   editor is unsure of are listed as yellow cells to confirm: **Confirm** on a slot, or the confirm-all button in
   that tab. Remove a replaced texture with the **x** on the slot.
5. **Add a paint layer.** Right edge **Livery**; pick a finish, or click **Base coat**, **Decal**, **Brush**, **Pattern** or
   **Wear**. Success: a row with that layer's name appears under the part in the tree and the right panel switches to
   **Paint layer · <kind>** with its settings (colour, opacity, blend, mask). Select the part row again to go back.
6. **Add an attachment.** Right edge **Attach**, **Add attachment**, then **Ammo screen** (a live screen), **Import model
   (.glb)** (a file dialog: `pickNext` first) or **Shapes** (hologram shapes). Success: a row such as `Ammo screen 1` appears under the part and
   the right panel shows **Screen** with **Mount**, **Position & size** fields; its tabs **Look**, **Screen**, **Effects**
   configure it. Delete it with **Delete this attachment**. The part's **+** button in the tree opens the same add menu plus
   the paint-layer and animated-layer kinds (**Livery** and **Animated textures** are submenus). A new attachment starts just
   above its part. **Charm** (submenu next to **Shapes**) adds an imported model or a shape already hung as a charm, selected,
   with the **Place** tab open. At the top of **Place**, the **Charm** switch hangs any attachment on a rope from its
   hanging point (its origin, an orange dot drawn over the gun; a new charm's sits on the middle of its part's underside;
   move it with the 3D view's gizmo or the position fields): style **Cord / Chain / Beads**, sliders **Length**,
   **Thickness**, **Weight**, **Settles**, **Stiffness**, **Bends**, a **Colour**, the switch **Stays out of the gun**, and
   the buttons **Shake**, **Run**, **Turn** that play a motion in the 3D view. The switch is disabled once the gear has 4
   charms. Under the heading **Code effect** the same **+** menu has **Animated pattern on this part**: a row
   `Code effect 1` appears under the part and the right panel shows **Code effect** with **Where it draws** (**Draw on**: a
   material of the item or an attached model; **Paint into**: Albedo or Emissive), **Pattern** (a select, one slider per
   number, one colour field per colour), **Size and motion** (sliders **Scale**, **Rotation**, **Spin**, **Intensity**;
   **Blend** **Paint over / Add light**), **When it plays** (the event select; for an event **Shows for (seconds)** and
   **Play the event in the 3D view**) and **Delete effect**. Each slider and colour has a person button beside it (`role="checkbox"`, named **Players can change <value>**): checked, players
   may change that value in game. Success: the pattern moves on the part in the 3D view and follows the sliders.
7. **Preview.** The 3D view shows the textures, paint layers and attachments as you edit. **Kill / Hit / Fire / Equip** play
   the skin's effects and sounds on a simulated event; **Frame selection**, **Perspective / Orthographic** and the view
   cube change the camera.
8. **Save.** The skin is autosaved to the editor's data folder about 1.5 s after each change; the status bar says
   `Autosaved`. To keep a project file elsewhere press `Ctrl+S` (or the card's **⋯** menu, **Save to file…**): the first
   time a save dialog asks where; answer it with `pickNext` first, e.g.
   `C:\work\skins\mine.forgeskinproj`. Success: a toast says `Project saved` and the status bar's right side shows the file
   name. Later saves overwrite that file without asking. **Snapshots** (card **⋯** menu) keep named copies in the data
   folder.
9. **Check.** **Export** menu, **Check**. A dialog lists, per skin, what must be fixed before a build (missing files, pending
   confirmations, a part with fewer than four channels while replacing textures — **Keep missing parts and channels as
   they are** on the whole gun's **Info** tab relaxes that). Success: it says there is nothing to fix. **Check against
   game** at its bottom compares the gun the game assembled with the preview.
10. **Build and install into a game profile.** The profile row at the top of the left column (**Apply to:**) names the
    game profile; its menu lists the profiles found (greyed ones have no BepInEx) and **Choose folder…** adds one through a
    folder dialog. Click **Apply** on the work's card (`Ctrl+Enter` for the open work). It installs or updates the
    profile's skin plugin when it is missing or not the editor's version (while the game holds an old one, the left column
    says the update is pending and it installs once the game is closed), builds the pack, installs it into the profile's
    `BepInEx/ForgeSkin/skins` and makes it the gear's skin in game (a one-shot `.pick` file the plugin takes). Success:
    the card says **In use** and a toast says when the game will show it; with the game running, the game reloads it
    within seconds and the editor reports whether it loaded. Going back to the original look is done in the game's skin
    picker (the pack stays); the card's **⋯** menu, **Put in profile**, installs
    without wearing. A work in use that was changed says **Changes not applied** until **Apply** again; with **Apply
    automatically** on in **Settings**, that happens 2 s after the last change while the game runs. Failure reasons are
    in a red toast; the log button at the bottom left shows the full log. Applying writes real files into that profile:
    use a profile meant for testing.
11. **Export a skin pack.** **Export**, **Export pack…** (or the card's **⋯** menu, **Export pack…**) opens a save dialog;
    answer it with `pickNext`, e.g. `C:\work\out\mine.forgeskin`. Success: the editor builds (status bar text, a
    few seconds) and a toast says Skin pack exported with a **Show file** button; the file exists at that
    path. If the check finds something to fix, a red toast says so first (step 9). **Export**, **Export as Thunderstore
    skin mod…** opens a dialog (format, name, version, description, icon, skins). The format is **ForgeSkin**, **Skins: a
    group per skin** (checkboxes of every item's skins, grouped under item names) or **Skins gun pack** (one select per item
    with skins, named after the item; **Not in the pack** leaves it out). Name and version must be valid and an icon is
    needed; **Choose image…** opens a file dialog for an image (the item's picture is the default icon).
    **Export** then asks for the zip path in a save dialog. When the Skins format left something out, a dialog titled **Skin
    mod exported** opens; its accordion **N things left out or changed** lists them.
12. **Import a Skins skin group.** **New**'s arrow, **Import a Skins skin group…**, then a
    `manifest.skingroup.json` in the file dialog. Success: the editor opens the group's skin of the open item (else its
    first) and a dialog titled **Skins skin group imported** lists the items that got a skin (buttons that open them) and
    the accordion of what was left out or changed; **OK** closes it. A group that names no gear opens **This skin group
    names no weapon** instead: pick an item in **Put it on** and click **Import**, or **Cancel**. The **Export** menu's OBJ
    model, UV template and game asset pack entries ask for a folder or file the same way.
## 7. Texture file names and limits

- Accepted image types for texture slots and folder drops: png, jpg, jpeg, tga, tif, tiff, bmp, webp.
- A name is matched to a channel by words in it (any case): `albedo`, `base colour`/`basecolor`, `diffuse`, `colour`
  → Albedo; `normal`, `nrm` → Normal; `metallic`, `metalness`, `smoothness`, `gloss`, `roughness` → Metal/Smooth; `emissive`,
  `emission`, `glow` → Emissive. The part is recognised from the part's id or role words in the name
  (`<part>_BaseColor.png`) and, failing that, from the image content compared with the game's own texture. A match below
  82 % confidence waits for confirmation.
- Sizes: a material's `textureSize` (**Texture resolution** under the texture slots and under the finish; 256, 512, 1024,
  2048 or 4096, longest side, aspect kept) is the size of all its own textures in the preview, the pack and the game; the
  livery is baked at it. Unset (**Auto**): a painted material at least 2048 (its base texture's size when larger), an
  unpainted one keeps each image's own size. Set it with `patch` (`materials/material=<name>/textureSize`, a number or
  null). A texture over 16384 pixels on a side is rejected by the game plugin, so never supply one; keep power-of-two sizes.
  Textures cost video memory in game: the field shows the material's estimate, and the whole gun's header shows the skin's
  after a build and warns above 256 MB (light) and 512 MB (heavy).
- The normal map is read in the usual tangent-space layout; the pack converts it for the game.
- Sounds: any common audio file (wav, mp3, ogg, flac, m4a, aac, opus, webm), at most 10 seconds, converted to Ogg Vorbis
  in the pack. **Add sound** (sound tab of any selection) is a menu: pick the event, then the file in a file dialog; the
  **Sounds** tile on the whole gun's **Info** tab adds a kill sound. A kill sound's **Which enemy** list (searchable, every enemy of the
  game's data by its exact name and data block name) moves it to that enemy's own group; **Any enemy** plays for every kill
  of an enemy without its own group. Sounds of one event (for kills, one enemy) form a group that plays one at random by
  **Weight**; each group head has "Play one of …" and "Add a sound to …" buttons, each row "Play …" and "Open …".

## 8. What is in a skin and in a pack

- **Project file** (`.forgeskinproj`, and the autosave next to it in the data folder): the skin document as JSON. It holds
  the skin id and name, the target item, per-material texture paths (relative to the project file for files under its folder
  or the data folder, else absolute; the editor's document always holds them absolute), paint layers,
  animated layers, attachments, part layout, sounds and player colours. Paths in a project point at your files; the editor
  watches them and re-reads a file that another program rewrites, so an agent can regenerate a texture on disk and the
  view updates.
- **Pack** (`.forgeskin`): a zip with `manifest.json` at the root (the same document, with paths relative to the zip) and
  the files it names: `textures/<material>_<channel>.dds` (textures at their material's `textureSize`, compressed for the game to upload as they
  are; `.png` when a side is not a multiple of 4), `layers/<id>.png` (baked animated-layer
  atlases), `meshes/*.forgeskinmesh` (attachment and part models), `glyphs/*.forgeskinmesh` (screen digits and borders) and
  the sounds as Ogg files. Every referenced file must exist and be a PNG or JPEG (a material texture may also be a DDS), an
  Ogg file or a `.forgeskinmesh`; paths may not be absolute or contain `..`. The pack is not a source: open the project to
  get the original pictures back.
- **Code effects** (`codeEffects` in the document, any number): patterns the game draws live with ForgeCore's
  drawing programs. Fields of each entry: `id`, `name`, `visible`; the target, exactly one of `material` (a material that has
  an override in the skin; `channel` `albedo` or `emissive`, painted into that texture by its UV, over the textures and
  animated layers) and `attachment` (the id of an attachment with a model; drawn on a copy of the model just outside it);
  `program`; `params` (number name → value); `colors` (colour name → sRGB RGBA, each 0..1); `scale`, `rotation`
  (degrees), `spin` (degrees per second), `intensity`; `blend` `alpha` or `additive`; `trigger` (`always` or an event, with
  `seconds` it shows for, and `lowAmmoFraction` for `lowAmmo`); `playerTweaks` with the lists `params`, `colors`,
  `transform` naming the values players may change. The programs, their numbers (name, range, step, default) and colours
  (name, default) are the catalogue `Mods/ForgeCore/Visual/Data/fx-programs.json`, the same file the game checks packs
  against: read it there, the editor's sliders show the same ranges and labels. A program, number or colour the catalogue
  lacks, a value out of range or a wrong target fails the pack with `bad-fx-program`, `bad-fx-value`, `bad-fx-target`,
  `bad-fx-tweak` or `bad-trigger`. The pack needs no file for a code effect. What players change is stored
  on their computer, never in the pack; the Skins formats cannot carry code effects (the export report lists them).
- What an agent can generate directly: the image files that go into texture slots and decals, and audio files. Hand-editing a
  pack or the autosave bypasses the editor's checks and bake step; make changes in the editor instead.

## 9. Known limits and things not to do

- Without `--ai-control` there is no `forgeAI` and no `pickNext`; always start the editor with the flag.
- A click that opens a dialog without a `pickNext` answer waiting opens the system's dialog, which only a person can close.
- Do not edit the game profile's `BepInEx/ForgeSkin` folder or `active.json` by hand; the cards' **Apply**, **Stop
  using** and **⋯** menu manage them.
- Do not run two editors at once; they share one data folder and settings.
- A change made through the UI is one undo step each; do not rely on keyboard shortcuts while a text field has focus.
- Applying into a profile writes real files there; use a profile meant for testing.
- Colour inputs and sliders are custom controls: set them with the on-screen picker or type in the number field next to
  them; setting the hidden input value directly does not update the skin.
