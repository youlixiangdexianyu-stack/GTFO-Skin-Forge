# What a skin is and where it goes {#what}

ForgeSkin is two things:

- **The ForgeSkin plugin** lives in your r2modman game profile and puts skins on your guns, melee weapons, tools and items in game.
- **This editor** makes skins, previews them, installs them into the game in one click and packs them for sharing.

Good to know:

- Skins show **only on your own computer**. Other players see the skins they installed themselves; nothing is synced online.
- A finished skin is packed into a `.forgeskin` skin pack. The editor installs it into the profile's `BepInEx/plugins/ForgeSkin/skins` folder.
- The profile needs **BepInExPack_GTFO** first. The plugin and the ForgeCore it needs are installed by the editor when you click Apply to game; you don't download them yourself.
- In game, click the **SKINS button in the lobby** to pick a skin for each item and change a skin's player colours. It does not open inside a level.

Next: [New skin and picking a weapon](#start).

# New skin and picking a weapon {#start}

**Pick an item**: click the item name at the top left of the 3D view (or press `Ctrl+P`). Every weapon, tool and item is listed; search at the top, filter by slot with the chips. Click a card to switch.

**Each item can have several skins.** Click **Skins** on the left edge to see the item's skin cards:

- Click a card to switch to that skin.
- The **New skin** card: **New blank skin**, **Duplicate current skin**, open a project or old preset (`Ctrl+O`), import a Skins skin group.
- **Import a Skins skin group…**: pick the `manifest.skingroup.json` in the group's folder. Each weapon the group names gets a skin of its own (material settings that fit several weapons go into each); texture macros (such as `@NORMAL`, `@CHECKER(...)`) become small images. A group that names no weapon id (one with only a rundown condition too) goes on no weapon in Skins, so the editor asks which weapon to put it on, or you cancel. After the import a list opens of what did not come across or was changed (mesh replacements, part transforms, first-person settings, Vector properties, other shaders, rundown conditions, missing texture files, repeated settings of one material and so on); open it to see each one.
- A card's **More** menu: rename, duplicate, save to file, snapshots, clear current changes, delete. Double-click the name to rename it too.
- The card marked **In game** is the one the game uses now.

Skins save as you go (see [Save, export and install](#save)).

# Textures {#textures}

Click a part in the **Parts** list on the left (or right in the 3D view), then **Textures** on the right edge. Each material has four slots:

| Slot | What it holds |
| --- | --- |
| Albedo | The surface colour and pattern; the one you change most. |
| Normal | Bumps and fine detail. Make a usual tangent-space normal map; the pack converts it to the layout the game wants. |
| Metal/Smooth | How metallic and glossy it looks: the game material's metallic/smoothness map (the game's gear shader layout: metallic in red, smoothness in green). |
| Emissive | What glows. Once replaced, set the glow colour and strength; **Apply to all parts** sets the whole gun at once. |

**Replacing a texture**

- Click a slot to choose an image, or drop an image on it. The corner badge changes from **Original** to **Replaced**; the × puts the original back.
- The whole gun at once: drop a folder into the window, or click **Whole-gun folder** in **Overview**. File names with the part and channel in them (like `Receiver_BaseColor.png`) are recognised; matches the editor is unsure of turn yellow until you click **Correct**.
- Save an image again in Photoshop or Substance Painter and the editor reads it again by itself.
- The **Publish** menu exports OBJ models and UV templates for painting in other programs.

**Formats and sizes**

- Image types: png, jpg, jpeg, tga, tif, tiff, bmp, webp. They are converted when packed.
- The output size is under **More** next to **Apply to game**: Keep, Original, 1K, 2K, 4K (longest side, aspect always kept).
- **The game cannot read an image over 8192 pixels on a side**, and rejects the whole pack. Prefer power-of-two sizes (1024, 2048 and so on).
- Bigger textures use more video memory. **Overview** shows the skin's estimate in game: up to 256 MB is light, over 512 MB can make the game stutter.

# Livery and animated layers {#layers}

You can colour a part in the editor without painting textures yourself. With a part selected:

**Livery** (right edge **Livery**) works like a racing game's livery editor:

- **Finish**: matte, gloss, metal, chrome, carbon fibre, glow and more; sets how metallic and shiny the part looks.
- **Base coat**: colours the whole part. The **Multiply** blend keeps the detail of the texture below.
- **Decal**: an image or sticker; press on the part in the 3D view and drag, and it slides along the surface.
- **Brush**: paint right in the 3D view, free hand, line, circle or fill a whole piece.
- **Pattern**: dozens of presets, camouflage, marble, honeycomb, flames and more.
- **Wear**: wears the paint away at edges, bumps and random patches. Set **Wear with kills** above 0 and the gun starts clean and wears in four stages with your kills.
- Every layer has opacity, a blend mode and a **Paint mask** to keep areas free. Layers higher in the list sit on top.

Packing bakes the livery into the textures, so the game sees ordinary textures.

**Animated layers** (right edge **Layers**) are moving textures: image, sequence, video or text. Show one always, or play it on a kill, hit, shot, equip and other events; play it looped, once, or looped for N seconds. Videos are split into images at the frame rate, 240 frames at most. Many frames and large layers take the most video memory.

**Code effects** (the part's **+** → **Code effect** → **Animated pattern on this part**) are moving patterns the game draws live: flow lines, scanlines, pulse, noise, rim light and polar pattern, up to 8 per skin. They paint into the part's colour or glow, over its textures and animated layers, or, under **Draw on**, onto an attached model. Each pattern has its own sliders and colours, plus size, rotation, spin, strength and blend; it can also light up for a few seconds after a kill, a shot or another event. Tick **Player** next to a value and players can change it in game on the **ADJUST** page of the SKINS screen; their setting stays on their own computer. The 3D view uses the same pattern code as the game.
# Attachments {#attach}

Attachments are extra things mounted on the gun. Select a part, then right edge **Attach** → **Add attachment**:

- **Ammo screen**: shows a live number; see [Ammo screen](#screen).
- **Import model (.glb)**: any .glb model.
- **Hologram shapes**: line, sphere, ring, arc, box, grid, cylinder, cone, panel.

A new attachment mounts on the selected part; you can move it to another part later. In the 3D view press `G` to move, `R` to rotate, `S` to scale; turn on **Snap to surface** and click the gun to stick it where you clicked.

With an attachment selected, **Effects** adds a **Flash** or **Show**: flash on every kill, say, or show only while aiming. A part's **Place** tab moves or hides the part, or swaps in your own model.

**Sounds**: **Sounds** plays clips on kills, low ammo, going down, being revived and other moments, at most 10 seconds each; only you hear them. **Add sound** asks for the event first, then the file. One event can hold several sounds: each time one plays at random, a higher **Weight** plays more often, and the same sound never plays twice in a row; the shuffle button on a group previews it by the game's rule. Kills can also have a group per enemy: pick it in the sound's **Which enemy** list (every enemy of the game's data by its exact name, type to search); a kill of that enemy plays its group, other kills play the **Any enemy** group. Downed and Revived play only for the item in your hands at that moment.

# Ammo screen {#screen}

An ammo screen is a small screen on the gun. After adding one, set it up under **Screen** on the right:

- **What it shows**: rounds in mag, reserve, total ammo, kills, damage dealt (this weapon or all weapons), damage taken, accuracy %, health %, infection %, or fixed text.
- **Style**: digits or a bar; digits automatic or fixed from 1 to 6, with optional leading zeros.
- **Font**: an installed font, or a font file you choose.
- **Colours**: panel, border, digits and glow; **Glow in game** decides whether it lights up in game.
- **Low ammo colour**: changes colour below a share of ammo (20% by default), or fades towards it as ammo runs low.

Click **Fire**, **Kill** and the other buttons under the 3D view to watch the numbers change.

# Player colours {#colors}

You can let players change your skin's colours themselves in game.

- Click the **person button** next to a colour setting to make it a player colour.
- With nothing selected, **Overview** on the right has the **Player colours** list: name each one, set its default and add a few **Suggested colours**.
- One player colour can drive several places, say the body tint and the screen digits; they always change together. It can drive: tint, glow, attachment colour, screen digits, panel, border, low-ammo colour and effect flash colours.

In game, the **Colours** page of the lobby SKINS picker offers your suggested colours first; without any it shows a row of 12 hues; and it can go back to the skin's own colour.

# Preview {#preview}

The 3D view in the middle is the preview; every change shows right away.

- Drag to rotate, scroll to zoom. `F` or **Frame selection** points the camera at what is selected.
- Numpad: `1` front, `3` right, `7` top, with `Ctrl` for the opposite side; `5` switches perspective and orthographic; `0` frames the selection.
- The **Fire**, **Kill**, **Hit**, **Equip** and other buttons under the view simulate game events: animated layers, attachment effects and sounds play by the game's rules.
- The cube at the top right picks a front, side, top or bottom view; the bottom left sets the lighting and bloom, and turns auto-rotate on.

The preview stays close to the game, but the game has the last word. After installing, **Check against game** under **More** next to **Apply to game** compares the gun the game assembled with the preview.

# Save, export and install {#save}

**Saving**

- Every change is saved to the editor's data folder about 1.5 seconds later; the status bar says **Autosaved**.
- `Ctrl+S` or the card's **More** → **Save to file…** keeps a `.forgeskinproj` project file of your own; `Ctrl+Shift+S` saves it as a new skin.
- **Snapshots** keep named versions to go back to. `Ctrl+Z` / `Ctrl+Y` undo and redo.

**Installing into the game**

1. Choose the **game profile** at the top right (an r2modman profile with BepInExPack_GTFO). If it is not listed, add it with **Choose folder…**.
2. Click **Apply to game** (`Ctrl+Enter`). The editor installs the plugin when needed, packs the skin, installs it into the profile and makes it the skin the game uses for this item.
3. With the game running, the change shows in about a second; otherwise it shows the next time the game starts. Turn on **Apply automatically** under **More** and each change is installed again while the game runs.
4. In game, click **SKINS in the lobby** to see and switch your skins.

**More** → **Installed skins…** lists the skins in the profile, to turn them on or off or delete them.

**Sharing** (in the **Publish** menu)

- **Check**: goes through every skin before export and lists what to fix.
- **Export skin pack…**: one `.forgeskin` file.
- **Export as Thunderstore skin mod…**: a mod ready to upload to Thunderstore, with skins of any items. Three ways:
  - **ForgeSkin**: every effect, applied by the ForgeSkin plugin.
  - **Skins: a group per skin**: for players with the Skins library. Each skin is its own skin group; a weapon may get several, and players pick in game.
  - **Skins gun pack**: the whole mod is one skin group; pick one skin per weapon (or "Not in the pack"). A player who picks the pack on a weapon gets the skin you chose for it. Where several weapons share a material name, the export names the game's textures to tell them apart; ones with the same textures too are listed in the report.
  - Skins carries still textures and material settings only. Dynamic layers, attachments, sounds, part changes, wear stages and player colours are left out; item skins (medipacks and the like) are not supported by Skins and are skipped; a BlendHS material (which samples no base colour texture) is written with a swap to the gear shader, its missing normal and metal maps filled with macros. Everything left out or changed is listed after the export.
  - When a skin of another weapon has livery, the editor switches to that weapon to bake it and switches back after.
- **Edit game files directly**: no plugin; only textures and material settings are written into the game's files (backed up first, can be restored).

# Common problems {#faq}

**The skin looks blurred in game**

- Most often the game's **texture quality is not at full**. The SKINS picker then shows a yellow note on the right that textures show at a fraction of their size. Set texture quality to full in the game's settings.
- Also check the output size under **More** next to **Apply to game** (1K is small), and whether the image itself is small.

**A skin pack is not loaded**

A pack the game cannot read does nothing; the right side of the SKINS picker lists it in red with the reason. Common reasons:

- A file is missing: an image, sound or model the pack names is not there. Apply to game or export again.
- A bad or oversized image: only PNG and JPEG, at most 8192 pixels on a side.
- A bad sound: it must convert to Ogg, at most 10 seconds.
- A bad path: paths in a pack cannot be absolute or contain `..`. Usually the pack was edited by hand; export it again from the editor.
- An old plugin: when the editor says ForgeSkin or ForgeCore is too old, update them in r2modman.

**I clicked Apply to game and nothing changed**

- Check that the profile at the top right is the one you play with.
- If the plugin needs an update while the game runs, close the game once and click Apply to game again.
- Each item uses one skin at a time: see whether the card is marked **In game**, or switch to it in the lobby's SKINS picker.

**Walk through the guide again**: Settings → Restart the guide, or the button at the bottom left of this page. It makes a new practice skin; your other skins are not touched.
