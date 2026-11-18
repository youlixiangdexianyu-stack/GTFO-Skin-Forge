#! Getting started

# What a skin is and where it goes {#what}

ForgeSkin is two things:

- **The ForgeSkin plugin** lives in your r2modman game profile and puts skins on your guns, melee weapons, tools and items in game; it can also swap your character for an imported humanoid model.
- **This editor** makes gun skins and character skins, previews them, installs them into the game in one click and packs them for sharing. **Weapon skins** and **Characters** at the top of the left column switch between the two.

![The finished example skin from this tutorial: Ember Line on the Omneco LRG](shot:example-final)

## What you need

- GTFO installed. The editor finds the game by itself; if it does not, the 3D view asks for the game folder (the one with `GTFO.exe`).
- An r2modman profile with **BepInExPack_GTFO** in it. The ForgeSkin plugin and the ForgeCore it needs are installed by the editor the first time you click **Apply** on a work's card; you don't download them yourself. When the profile already has a ForgeSkin or ForgeCore from r2modman that is newer than the editor's, the editor leaves it as it is, says so once and installs the skin as usual.

## Good to know

- Teammates see your skin, player colours and adjustments on your gun and sentry **only if they installed the same skin pack** (publish it as a Thunderstore mod so they can). Without it, or with another version of it, they see the gun's original look.
- A finished skin is packed into a `.forgeskin` skin pack. The editor installs it into the profile's `BepInEx/ForgeSkin/skins` folder.
- In game, click the **SKINS button in the lobby** to pick a skin for each item, change a skin's player colours and adjust code effects. It does not open inside a level.

## Where to start

1. Read [The editor at a glance](#editor) once.
2. For a gun skin, follow the [worked example](#example): a full sci-fi rifle skin, step by step.
3. For a character, read [Characters at a glance](#character), then follow [a VRM character](#char-example).
4. Look things up in the other pages when you need them: textures, livery, colours, moving effects and attachments for gun skins are in the groups further down. The search box at the top left also finds words inside the pages.

# The editor at a glance {#editor}

![The editor with a part selected. The numbers match the list below.](shot:editor-map)

1. **Left panel.** The **Parts** list of the open item: every part with its paint layers, animated layers, code effects and attachments underneath. The tick fits or leaves off a part, the eye hides a row (also in game), the **+** on a row adds anything to that part. **Works** on the left edge switches to the works list.
2. **Item name.** Click it (or press <kbd>Ctrl</kbd>+<kbd>P</kbd>) to pick another weapon, tool or item.
3. **Event buttons.** Kill, Hit, Fire, Equip and the rest play the skin's effects, animated layers and sounds the way the game would.
4. **Right panel.** Settings of whatever is selected. With nothing selected it shows the skin's **Overview**.
5. **Right edge tabs.** The categories of the right panel. A part has **Textures**, **Livery**, **Layers**, **Attach**, **Place** and **Sounds**; an attachment has **Place**, **Look**, **Screen** (ammo screens only), **Effects** and **Sounds**. A small number on a tab says how many things it holds.
6. **Publish.** Check, export a skin pack or a Thunderstore mod, export models and UV templates, write game files directly.
7. **Apply.** On a work's card: builds the skin, installs it into the profile picked under **Apply to:** at the top of the left column and has your gear wear it in game.

This is the window with **Weapons** picked at the left of the top bar; for **Character**, see [Characters at a glance](#character).

## Moving around

- Drag in the 3D view to turn the gun, scroll to zoom, right-drag to pan.
- Click a part or attachment in the 3D view to select it; a click a few pixels off a thin ring or charm chain still counts. Hovering a row of the left list lights up its thing in the 3D view, and hovering a thing in the 3D view lights up its row.
- The round, casing or speed loader on its own below the gun is the gun's ammo: inside the gun it cannot be seen, so the editor lays out one of each to select and skin and hides the rest. Only the preview places them there; in game they stay in the gun and the skin applies to every one.
- <kbd>F</kbd> or **Frame selection** points the camera at what is selected.
- Numpad <kbd>1</kbd> front, <kbd>3</kbd> right, <kbd>7</kbd> top; with <kbd>Ctrl</kbd> the opposite side; <kbd>5</kbd> switches perspective and orthographic; <kbd>0</kbd> frames the selection. The cube at the top right does the same with a click.
- <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes, <kbd>Ctrl</kbd>+<kbd>Y</kbd> redoes. Every change is one undo step.
- A section heading with an arrow folds its section; folded sections stay folded next time.

> [!TIP]
> Hover a small **i** next to a heading or a setting for a short explanation of it.

# Pick an item and make a skin {#start}

## Pick an item

1. Click the item name at the top left of the 3D view, or press <kbd>Ctrl</kbd>+<kbd>P</kbd>.
2. Search at the top, or narrow the list with the slot chips (Primary, Special, Melee, Tools and so on).
3. Click a card. The 3D view loads the item; opening one for the first time takes a few seconds.

![The item gallery](shot:gallery)

## Skins of an item

Each item can have several skins. Click **Works** on the left edge to see the work cards; the works of one gear are grouped together.

![The works list](shot:skins-panel)

- Click a card to switch to that work.
- **+ New** makes a new work on the gear you pick; the arrow beside it holds **Copy the open one**, **Open project or old preset…** (<kbd>Ctrl</kbd>+<kbd>O</kbd>) and **Import a Skins skin group…**.
- A card's **⋯** menu: **Put in profile**, **Copy**, **Rename**, **Export pack…**, **Snapshots**, **Save to file…**, **Clear current changes**, **Delete work**. Double-click a name to rename it.
- A card says what the work is in the chosen profile (Only in the editor, In the profile, In use); see [Install](#save).

## Import a Skins skin group

**Import a Skins skin group…** asks for the `manifest.skingroup.json` in the group's folder. Each weapon the group names gets a skin of its own; material settings that fit several weapons go into each, and texture macros (such as `@NORMAL` or `@CHECKER(...)`) become small images. A group that names no weapon id goes on no weapon in Skins, so the editor asks which weapon to put it on (or you cancel). After the import a list opens of what did not come across or was changed; open it to see each one.

#! Make a gun skin

# Worked example: a sci-fi rifle {#example}

This page makes one gun skin from start to finish: **Ember Line**, a clean sci-fi skin for the **Omneco LRG** (the HEL rifle): white and grey body, black magazine and sight, glowing orange panel seams, a hazard strip, a serial number, an ammo screen, a hologram reticle ring in front of the scope and a small hologram charm, then into the game and out to other players. Every feature it uses has its own page further on; follow the links for details.

![Ember Line, finished](shot:example-final)

## 1. Open the rifle and make a new skin

1. Make sure **Weapons** is picked at the left of the top bar.
2. Press <kbd>Ctrl</kbd>+<kbd>P</kbd>, type `LRG` and click the **Omneco LRG**.
3. **Skins** on the left edge → **New skin** → **New blank skin**. Rename it **Ember Line** (double-click the name).

## 2. Base coats and finishes

Palette: white-grey `#949699`, grey `#5C6166`, black `#0D0E0F`, orange `#FF6B14`.

1. Select **Front**, open [Livery](editor:livery), click **Base coat**. Colour `#949699`, **Opacity** 0.85 so the game's texture details show through. Under **Finish** pick **Painted steel**.
2. Do the same on **Receiver**. (Or select the base coat and use **Apply to other parts**.)
3. **Stock**: base coat `#5C6166`, opacity 0.92, finish **Satin**.
4. **Magazine** and **Sight**: base coat `#0D0E0F`, opacity 0.92; finish **Rubber** on the magazine, **Satin** on the sight.

> [!TIP]
> Save the four colours as a palette now: **Overview** → **Palettes**, type `Ember`, click save. Then click it, and every colour picker offers the four colours.

![After step 2](shot:example-base)

## 3. Glowing panel lines

1. Select **Front**, **Livery** → **Panel lines**, then the **Armour panels** preset.
2. Click the long flat side of the front part in the 3D view, and drag until the panels sit between the vents.
3. Under **Placement**, size about 220 × 50 mm; turn on **Both sides**.
4. Under **Panel lines**: **Line width** 0.6 mm, **Corner cut** 3 mm, **Gap between panels** 2 mm, **Count** 4.
5. Under **Look**: colour `#FF6B14`, **Glow** on.

![Left: before. Right: the panel lines with Glow on.](shot:panel-lines-before-after)

## 4. Hazard strip, serial and stock fade

1. On **Front**, add **Panel lines** again with the **Hazard strip** preset, colour orange, Glow off. Place it near the muzzle, 70 × 12 mm, **Both sides** on.
2. On **Receiver**, add **Text**: `EMBER-07`, font Orbitron, **Letter height** 8 mm, **Letter spacing** 0.12, colour near black. Place it on the side above the trigger, **Both sides** on.
3. On **Stock**, add **Gradient**. Colours: grey at 15 %, black at 85 %. **Direction**: **Along**. Opacity 0.9.

![After step 4](shot:example-lines)

## 5. Ammo screen

1. Select **Receiver**, **Attach** → **Add attachment** → **Ammo screen**.
2. Move it onto the left side of the receiver, below the rail, and turn it to face out (<kbd>R</kbd>, or **Rotation °** 0, 90, 0 under **Place**).
3. **Screen**: **Show** Rounds in mag, **Digits** 2, size 40 × 18 mm.
4. **Look** → **Colour**: text orange, background black at 90 %, border orange, **Border style** **Corners**. Keep **Low ammo colour** on.
5. Click **Fire** under the view a few times to watch it count down.

## 6. Hologram reticle ring

1. Select **Sight**, **Attach** → **Add attachment** → **Shapes** → **Reticle ring**. It is orange already (the skin's first colourful layer).
2. Move it just in front of the scope lens.
3. **Look**: **Radius** 16 mm, **Ticks** 36.
4. **Hologram**: **Flicker** 0.15, **Brightness** 0.9, **Two-colour fade** on with a warm white top colour, **Scanlines** 0.5, **Edge glow** 0.6.

![The reticle ring: flicker only (left) and with fade, scanlines and edge glow (right)](shot:hologram-before-after)

> [!NOTE]
> Scanlines and edge glow are two code effects on the hologram.

## 7. A small hologram charm

1. Select **Front**, **Add attachment** → **Charm** → **Hex shield**. It hangs from the underside of the front part.
2. In **Place**, set the rope: **Chain**, **Length** 3.5 cm, dark grey colour. Click **Run** and **Turn** to see it swing.
3. Open **Look** and turn on **Hologram** (flicker 0.3) so it glows like the reticle.

![Close-up of the sight with the reticle ring and the ammo screen](shot:example-sight)

## 8. Check and put it in the game

1. **Publish** → **Check**: it should say there is nothing to fix.
2. Click **Apply** on Ember Line's card. Start the game: the rifle already wears Ember Line, and a running game changes within seconds. More in [Save, snapshots and install](#save).
3. Take a **Snapshot** (card **More** → **Snapshots**) before trying other ideas, such as a night colour variant.

## 9. Share it

1. **Publish** → **Export as Thunderstore skin mod…**, tick Ember Line, fill in the mod name, version and description, and keep the **ForgeSkin** format.
2. Upload the zip to Thunderstore yourself. Teammates see Ember Line on your gun once they install that mod. More in [Publish and share](#publish).

#! Make a character

# Characters at a glance {#character}

Besides guns, you can swap your whole character in the game for an imported humanoid model. In game it walks, runs, crouches, jumps, holds the gun and goes down with the game's own moves, and the original character is hidden; where you can be hit stays the original's, the model only changes the look. A character skin is like a gun skin: one skin in one pack, which you can try on in the game or export as a Thunderstore mod.

![The character side: 1 the six steps on the left, 2 the move bar, 3 the settings of the step you are on](shot:character-map)

## Switch to characters

Click **Characters** at the top of the left column (next to **Weapon skins**). The window changes to the character side; **Weapon skins** takes you back to gun skins. The two sides do not touch each other.

## What is in the window

- **Left**: six steps, top to bottom: **Import model**, **Bones**, **First-person arms**, **Hair and skirt**, **Materials**, **Check**. The mark beside a step: a tick means it is done, a red number is how many things still need fixing, the number beside **Hair and skirt** is how many sets of bones swing, and a step marked **Later** cannot be used in this version yet.
- **Middle**: the 3D view. Drag to turn the model, scroll to zoom, right-drag to pan. Under it is the [move bar](#char-motion); **Show bones** at the bottom left draws the skeleton on the model.
- **Right**: the settings of the step picked on the left.
- **Work cards in the left column**: **Apply** on a character's card installs the character skin, not a gun skin.

Changes save by themselves. The next time you switch to **Character**, the character you worked on last opens again.

## In what order

1. [Import the model](#char-import)
2. [Bones](#char-bones): once the hips have a bone you can go on; other parts may stay empty.
3. [Move preview](#char-motion): play a few moves to see the model move right.
4. [Hair, skirt and body colliders](#char-springs)
5. [Materials and first-person arms](#char-materials)
6. [Try it on and export](#char-export)

The first time, the [worked example](#char-example) is the quickest way through.

# Import the model {#char-import}

## What model works

- A skinned `.glb` or `.vrm`. Exports from Mixamo, VRoid and Blender all work. It need not be a whole human: a creature without fingers, with one arm or leg, or with unlike sides works too, as long as one bone can serve as the hips.
- PNG or JPEG textures, at most 4 bones per vertex, exported as one uncompressed file.
- A `.vrm` is the easiest: the bones, swinging hair and skirt, colliders and any credit its author asks for are filled in on import.

## Import

1. Switch to **Character**.
2. Click **Choose model file** in the middle of the 3D view and pick your model, or drag the `.glb` or `.vrm` file into the window.
3. Wait a few seconds. On success the bottom right says how many bones came in; for a `.vrm` it also says how many swinging sets and colliders it filled in from the file.
4. The editor moves on to **Bones** by itself.

![The Import model step after an import](shot:character-import)

Done right: your model stands in the 3D view and **Import model** on the left has a tick.

## What the Import model step holds

| Setting | What it does |
| --- | --- |
| Skin name | The name of this character skin; the mod name on export starts from it. |
| Credit | Only there when the model's licence asks for credit. Export writes it into the pack's CREDITS.txt and the mod description. |
| Bones, Vertices, Materials | How many the model has. |
| Use another model | Swap in another file. |

## When an import fails

The bottom right says why. The usual ones:

| Message | What to do |
| --- | --- |
| The model is not skinned | Rig it in Blender and export the `.glb` again. |
| The model has bones with the same name | Give every bone its own name and export again. |
| The model is compressed | Export without compression. |
| A vertex has more than 4 bones | Limit it to 4 bones per vertex on export. |
| A texture is not PNG or JPEG | Save it as PNG or JPEG and export again. |

# Bones {#char-bones}

Your character follows the game's own body, so the editor needs to know which bone of your model is the hips, which the hand. The import already matched them by name; this step only deals with what it missed or got wrong. Open [Bones](editor:character-bones).

![Bones: the figure, the finger grids and the bone for a joint](shot:character-bones)

## The figure

- Every dot on the little figure on the right is a joint: **green** has a bone, **grey** has none, **red** must be fixed (only the hips can be red).
- The two grids below are the 15 finger joints of each hand; beside each it says whether that hand's fingers **Move**, how many move, or **Still**. Match each finger from its first joint to the tip: from the first joint without a bone on, that finger stays still.
- In the 3D view the matched joints are green dots too; click one to pick it.

## What must have a bone

Only the hips: the model follows the body by them. If your model has no hip bone, pick the one nearest the middle of the body. While they have none, a red line at the top says so; click it to pick the hips.

Every other part may go without. A part without a bone does not move on its own in game and goes with the bone above it, so all of these work:

| The model | In game |
| --- | --- |
| No separate fingers | The fingers do not move; each hand goes with its wrist. |
| One arm missing, or an upper arm, forearm or hand not matched | That hand does not reach for the gun; the gun stays where the original holds it. |
| Both upper arms not matched | First person shows the game's own arms. |
| One leg missing, no neck or no shoulders | That part does not move on its own and goes with the part above. |
| Unlike left and right | Each side takes its own bones; neither affects the other. |

The grey lines above the figure say these same things. They tell you what the game will do and do not stop you.

## Pick a joint's bone yourself

1. Click the joint on the figure or in a finger grid.
2. In the drop-down of the **Pick a joint** box below, choose the model's bone for it; you can type to search. A bone serves one part only: pick a bone another part uses, and that part is left without one.
3. To leave this part without a bone, click **Leave this part out**.
4. To take back just this one, click **Use the automatic match**; to start over, click **Match again** at the top.

Done right: the top says **Ready to use.** and **Bones** on the left has a tick. Until the hips have a bone the move bar is grey and you cannot try the character on or export it.

> [!NOTE]
> When the whole model faces backwards, a line says the game turns it round. Nothing to do.

# Move preview {#char-motion}

The move bar under the 3D view makes the model move, so you can see whether it is rigged right.

![The move bar: in-game moves on top, try-out moves below](shot:character-motion)

## Two sets of moves

| Set | Moves | What it is for |
| --- | --- | --- |
| In-game moves | Idle, Walk, Run, Crouch, Crouch walk, Jump, Hold gun, Fire, Reload, Melee, Go down, Revived | The game's own player moves. In the game your character moves exactly like this; what you see here is the real result. |
| Try-out moves | Hip-hop, Twerk, Zombie walk, Backflip, Pray, Handshake, Lying shrug, Lying spasm | Big movements only for checking the rig (arms twisting, clothes tearing open). They never show in the game. |

## Playing

1. Click a move and it starts right away.
2. **Play** / **Pause** stops on the current frame so you can look closely.
3. **Loop** on repeats the move; off plays it once.
4. **Speed** 0.25×, 0.5× or 1× slows it down to watch hands and feet.
5. **Rest pose** stops the move and the model goes back to the pose it was imported in.

While the hips have no bone the whole bar is grey; hover it to see that the bones come first.

## What to look for

- Walking and running, the feet land on the ground and the knees bend forward.
- Holding the gun and reloading, the arms do not twist and the fingers close.
- Hair and skirt swing with the move and stay out of the body.
- With no move playing, hair and skirt should be still; if they keep swaying, something is wrong.

## Jolt, Run, Turn

Under the 3D view, and next to the title of the **Hair and skirt** step, the **Jolt**, **Run** and **Turn** buttons give the model a push by hand to see how hair and skirt swing. They work once at least one set of bones swings.

# Hair, skirt and body colliders {#char-springs}

Make the bones of hair, ponytails, skirts and ribbons swing, and in game they follow your character's movement. Open [Hair and skirt](editor:character-springs). A `.vrm` comes with these filled in from the file.

![Hair and skirt: swinging sets and body colliders](shot:character-springs)

## Add a swinging set

1. Click **Pick a bone to make it swing** and choose the top bone of the chain (the root of a ponytail, say); you can type to search.
2. Every chain of bones under it counts as one strand, and they swing together (a skirt is a ring of several). A bone with nothing under it says it cannot swing; pick one higher up.
3. The new card guesses its **Kind** from the bone names; change it if it is wrong.
4. Click **Jolt**, **Run** or **Turn** to see the result, then adjust the sliders.

Each set is drawn in the 3D view as violet dots and lines that follow the moves. Point at a set (in the 3D view or on its card on the right) and it turns orange with its name; click to select it, and the right side scrolls to its card. Grey dots are bones that can still be made to swing: click one to add it. With **Show bones** off none of these are drawn.

## Settings of a set

The card's title is the name of the chain's top bone, with ×N for N strands.

| Setting | Range | What it does |
| --- | --- | --- |
| Kind | Long hair, ponytail / Short hair / Skirt / Tail, ribbon | Starting values. Changing the kind resets Stiffness, Settles and Thickness to that kind's. |
| Stiffness | 0 to 1 | How much the chain keeps its own shape. |
| Settles | 0 to 1 | How fast it comes to rest after a swing. |
| Weight | 0.1 to 3 | Heavier swings slower and further. |
| Gravity | 0 to 3 | How hard it pulls down. |
| Thickness | 0 to 10 cm | How thick the chain counts when it touches the body. |

The bin (**Stop swinging**) removes the set.

## Body colliders

On this step blue capsules show in the 3D view. Hair and skirt that touch them are pushed off instead of going into the body.

- Without colliders from the file, seven automatic ones are used: head, chest, hips, both thighs and both shins. Each one turns on and off, and its slider sets its thickness (0.5 to 2 times).
- When the `.vrm` brings colliders, each is listed with its thickness and the bin removes one; **Use automatic colliders** switches to the seven above.

Done right: on **Run** and **Turn**, hair and skirt swing and slide along the body instead of into it.

> [!NOTE]
> In game your own character and your teammates' characters all swing, each worked out on its own computer, so the swing is not exactly the same for everyone.

# Materials and first-person arms {#char-materials}

## Materials

Open [Materials](editor:character-materials): one row per material of the model, and **Shader** decides how it takes light. For now there is one, **Game lighting**, lit the same way as the game's gear.

<!-- To do: once the toon shader is in the editor, add its entry in the Shader drop-down and its settings here. -->

## First-person arms

The game makes the arms you see in first person from your model itself: it keeps what is skinned to the upper arms and below (forearms, hands, fingers, sleeves) and puts each joint on the game's own arm, so the hands hold every weapon where the original hands do. You do not make a second model. Open the **First-person arms** step (it works once both upper arms have a bone; a model without them shows the game's own arms in first person):

1. Click **Show the first-person arms**, or switch **Whole model** to **First-person arms** above the 3D view. The view keeps only the part the game keeps, cut by the same code as in the game.
2. For a material that would cover the sights (a wide cuff, say), switch on **Left out in first person** in its row below; it disappears from the arms view at once, and the game leaves it out too.
3. The moves below play in the arms view as well, to check fingers and sleeves while holding and reloading.

When nothing of the model is skinned to the arms the step says so, and first person keeps the original arms. Toon materials are drawn with game lighting on the first-person arms.

# Try it on and export {#char-export}

## Try it on in the game

1. Once the hips have a bone, click **Apply** on the character's card in the left column.
2. The editor packs the character, installs it into the profile picked under **Apply to:** and has you wear it in game; the card turns **In use**.
3. If the game is running, it shows from the next level.
4. To go back to the original character, change the **CHARACTER** cell of the lobby's **SKINS**; the pack stays in the profile.

While the hips have no bone, **Apply** says to match the bones first.

## Toon rendering setting

Each player picks for themselves how materials marked toon (`toon`) in a character pack are drawn. In the lobby click **SKINS**, then the gear in the picker's title bar; under **Skin settings · all gear**, **Toon rendering** has three choices:

| Choice | How it draws | Thermal sight |
| --- | --- | --- |
| **Deferred** (default) | Lit by the game's own lights, shadows and fog; the toon shading steps are approximate. | Shows them. |
| **Forward** | Works out the shading steps itself from the game's lights, for a cleaner toon look. | Does not show these toon parts. |
| **Off** | Lit like every other surface. | Shows them. |

A change applies at once to every character already worn (the lobby figures too), is written to `ForgeSkin.cfg` and kept for the next game. It only changes your own screen and is not sent to teammates; each picks their own. Deferred is the default because with Forward the thermal sight does not show the toon parts on teammates.

> [!WARNING]
> Applying writes real files into that profile. Use a profile meant for testing.

## Export

On the character side, click **Export** → **Export as Thunderstore skin mod…** in the top bar. The last step on the left, [Check](editor:character-check), lists the same checks.

![Export: the checks and the mod details](shot:character-export)

1. The check at the top needs a tick: **The hips have a bone**.
2. A model with a credit gets a line saying the pack carries it.
3. Fill in **Mod name** (English letters, digits and underscores only; it starts from the skin name), **Version** (like 1.0.0) and **Description** (up to 250 characters). The **Icon** starts as a picture of the 3D view; **Choose image…** uses one of your own.
4. Click **Export Thunderstore mod…** and pick where to save: you get a zip ready for Thunderstore. For the skin pack alone, click **Export skin pack only…** and you get a `.forgeskin`.
5. You upload it to Thunderstore yourself. Teammates see your character once they install the mod; players without it see the original character.

# Worked example: a VRM character {#char-example}

This page takes a `.vrm` character made in VRoid all the way into the game.

1. **Switch.** Click **Characters** at the top of the left column.
2. **Import.** Drag the `.vrm` into the window. The bottom right says how many bones came in and how many swinging sets and colliders were filled in; the editor moves on to **Bones**.
3. **Check the bones.** A VRM's bones are usually all green, and the top says **Ready to use.** For a red dot or a wrong bone, see [Bones](#char-bones).
4. **Play moves.** On the move bar click **Walk**, **Run**, **Hold gun** and **Reload** to check feet and hands; then a few **Try-out moves** to see that big movements do not tear the model. Click **Rest pose**.
5. **Tune hair and skirt.** Click **Hair and skirt** on the left, then **Run** and **Turn**. Hair too floppy: raise **Stiffness** a little. Skirt going into the legs: make the thigh and shin colliders thicker.
6. **Name it.** Click **Import model** on the left and change the **Skin name**.
7. **Try it on.** Click **Apply** on the character's card; in game you wear it.
8. **Export.** **Export** → **Export as Thunderstore skin mod…** in the top bar, fill in the mod name and version, and click **Export Thunderstore mod…**.

![The imported VRM character with its move bar](shot:character-map)

#! Paint the gun

# Textures {#textures}

Select a part (in the **Parts** list, or click it in the 3D view), then [Textures](editor:texture) on the right edge.

![Textures tab of the receiver: four slots, glow and material settings](shot:textures-tab)

| Slot | What it holds |
| --- | --- |
| Albedo | The surface colour and pattern; the one you change most. |
| Normal | Bumps and fine detail. Make a usual tangent-space normal map; the pack converts it to the layout the game wants. |
| Metal/Smooth | How metallic and glossy it looks: metallic in red, smoothness in green (the game's gear shader layout). |
| Emissive | What glows. Once replaced, set the glow **Colour** and **Strength**; **Apply to all parts** sets the whole gun at once. |

A part with several materials shows a **Material** list at the top of the tab; pick which one you are editing.

## Replace one texture

1. Click a slot and choose an image, or drag one image onto it from File Explorer. While it hovers, the slot lights up and names the channel.
2. The corner badge changes from **Original** to **Replaced**. The **×** puts the original back.

Several files at once, or a file that is not an image, turn the slot red and say why.

## Replace the whole gun at once

Drop a folder of textures (for example a Substance Painter export) anywhere in the window, or click **Whole-gun folder**. Files with the part and channel in the name, such as `Receiver_BaseColor.png`, are matched by themselves. Matches the editor is unsure of turn yellow; click **Correct** on each, or **All correct** in **Overview**. Files it could not match are listed in **Overview**.

Save an image again in Photoshop or Substance Painter and the editor reads it again by itself.

## Material settings

Under **Material settings**: **Metallic**, **Smoothness** and **Colour tint**, where the game's material has them. Some materials have none; use a finish or paint layers under [Livery](#livery) for those.

## Sight reticle

Select the sight part and pick its lens material (only a holographic lens has this): below the texture slots is **Reticle**. A reticle has up to three layers, each with:

- A picture slot: a PNG with a transparent background, whose lines are the reticle; transparent parts show nothing. A picture on a layer the game leaves off switches it on.
- A **Show** switch, **Colour** and **Strength** (the reticle glows by itself), **Scale** (larger draws it smaller) and **Depth** (how far beyond the lens it seems).

The 3D view draws the three layers on the lens with these values (depth is not shown there). In game only you see it, when you aim down the sight; teammates with the same skin never look through your lens.

## Paint in another program

The **Publish** menu has **Export OBJ model (by part)…**, **Export OBJ model (merged)…** and **Export UV templates…**. Paint on the OBJ in Substance Painter or Blender, or over the UV template in an image editor, then drop the result back in.

## Formats and sizes

- Image types: png, jpg, jpeg, tga, tif, tiff, bmp, webp. They are converted when packed.
- Each part's **Texture resolution** (under the texture slots on the **Textures** tab, and under the finish on the **Livery** tab: one setting) sets how large the part's textures are in game: **Auto**, 256, 512, 1024, 2048, 4096 (longest side, aspect always kept). The editor preview, the pack and the game all use this size. **Auto** gives a painted part at least 2048 (so text and panel lines stay sharp even on small parts such as a magazine, whose game texture is 256) and keeps the image's own size on a part without paint. Beside it is about how much video memory the part's textures take in game. Only a part with its own textures or paint has the setting.
- **The game cannot read an image over 16384 pixels on a side**, and rejects the whole pack. Prefer power-of-two sizes (1024, 2048 and so on).
- Bigger textures use more video memory. **Overview** shows the skin's estimate after a build: up to 256 MB is light, over 512 MB can make the game stutter.

# Livery: finish and paint layers {#livery}

You can colour a part without painting textures yourself. Select a part and open [Livery](editor:livery). Packing bakes the livery into the part's textures, so the game sees ordinary textures.

![The Livery tab: 1 add a layer, 2 the layer stack (top of the list is painted on top), 3 finish](shot:livery-tab)

## Add a layer

1. Click a kind in the grid (or use the part's **+** in the left list, **Livery** submenu).
2. The new layer is selected and its settings open on the right. Its row shows up in the stack and in the left list under the part.
3. Click the part row again (or the arrow next to the layer kind at the top of the right panel) to go back to the part.

| Kind | What it does | Page |
| --- | --- | --- |
| Base coat | One colour over the whole part | [Colour, brush, pattern, wear](#paint) |
| Decal | A picture or sticker you drag onto the surface | [Decals, stickers and text](#decals) |
| Brush | Strokes you paint in the 3D view | [Colour, brush, pattern, wear](#paint) |
| Pattern | Camouflage and other patterns, 33 presets | [Colour, brush, pattern, wear](#paint) |
| Wear | Paint worn off at edges, bumps and patches | [Colour, brush, pattern, wear](#paint) |
| Panel lines | Sci-fi frames, seams, ticks, hazard stripes drawn in code | [Panel lines](#panel-lines) |
| Text | Words in any font, placed like a decal | [Decals, stickers and text](#decals) |
| Gradient | A colour ramp of 2 to 4 colours along the part | [Gradient and tiled image](#gradient) |
| Tiled image | A picture repeated over the part | [Gradient and tiled image](#gradient) |

## The layer stack

- Layers higher in the list sit on top. The arrows on a row move it up or down.
- The eye hides a layer; a hidden layer is not packed.
- **Duplicate** (on the row, or the copy icon at the top of the right panel) puts a copy right above it and selects the copy.
- **Apply to other parts** (share icon at the top of the right panel): tick parts and click **Add to N parts**; each gets a copy in one undo step. Decals, panel lines and text need one click on their new part to be placed. Brush strokes belong to one part's texture and cannot be copied.

![Apply to other parts](shot:copy-to-parts)

## Look: colour, opacity, blend, glow

Every layer has a **Look** section.

![The Look section of a panel-lines layer](shot:layer-look)

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Colour / Tint | any colour | white (base coat: red) | The layer's colour. For decals and tiled images it tints the picture. Pattern, wear and gradient have their own colours instead. |
| Opacity | 0 to 1 | 1 | How much it covers. |
| Blend | Normal, Multiply, Screen, Overlay, Add | Normal | Normal covers what is below. Multiply only darkens and keeps the detail below (good for a base coat that keeps the game's texture). Screen only lightens. Overlay boosts contrast. Add brightens like light. |
| Glow | on / off | off (panel lines: on) | Also draws this layer into the emissive texture, so it lights up in game. Not on wear layers. |
| Glow strength | 0 to 10 | 1 | Shown with Glow on. Shared by all glowing layers of the part. |

## Finish

The finish sets how metallic and glossy the whole part looks. It writes the part's metal/smoothness texture.

| Finish | Metallic | Smoothness | Extra |
| --- | --- | --- | --- |
| As is | game's own | game's own | |
| Matte | 0 | 0.08 | |
| Gloss | 0 | 0.88 | |
| Satin | 0 | 0.45 | |
| Metal | 1 | 0.62 | |
| Brushed metal | 1 | 0.5 | brushed lines |
| Chrome | 1 | 0.97 | |
| Rubber | 0 | 0.04 | |
| Plastic | 0 | 0.6 | |
| Carbon fibre | 0 | 0.78 | weave pattern |
| Painted steel | 0.35 | 0.55 | |
| Worn metal | 0.75 | 0.4 | worn patches |
| Glow | 0 | 0.5 | the paint itself lights up; **Glow strength** 0 to 10 |

# Colour, brush, pattern, wear {#paint}

## Base coat

Colours the whole part. A new base coat is red, Normal blend, opacity 1. Set **Blend** to Multiply to keep the detail of the texture below, or lower **Opacity** a little so the game's texture shows through. Turn on **Paint mask** to keep areas free (see [Masks](#masks)).

## Brush

Paint straight onto the part in the 3D view. With a brush layer selected, a bar appears over the view:

| Setting | Range | What it does |
| --- | --- | --- |
| Shape | Free hand, Line, Circle, Fill a piece | Fill a piece paints a whole UV island with one click. |
| Width | 0.5 to 30 mm | Stroke width. |
| Mirror | on / off | Also paints the mirror image on the other side of the gun (across its centre plane); fills too. |
| Paint mask | on / off | Drags paint the mask instead of the colour. |

**Clear strokes** in the right panel removes every stroke of the layer.

## Pattern

![A pattern layer: presets, pattern, four colours, size, rotation, seed](shot:pattern-layer)

1. Add a **Pattern** layer. It starts with US woodland.
2. Click a preset (33 of them: digital camos, tiger, splinter, hydro dip, marble, damascus, honeycomb, circuit, flames, starfield, lava and more), or pick a **Pattern** and set its **Colours** yourself.
3. Set the size and seed until it sits well on the part.

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Pattern | 24 patterns | Woodland | Digital, Woodland, Tiger stripe, Splinter, Flecktarn, Multi-terrain, Hydro dip, Anodised gradient, Marble, Damascus, Honeycomb, Hex grid, Contour lines, Circuit, Zebra, Leopard, Scales, Stripes, Plaid, Polka dots, Rust and grime, Flames, Starfield, Crackle |
| Colours | 4 colours | from the preset | Click a swatch to change it. |
| Pattern size | 5 to 300 mm | 50 mm | Size of one repeat on the gun. |
| Pattern rotation | X, Y, Z in degrees, step 5 | 0, 0, 0 | Turns the pattern in 3D, so it can run along or across the gun. |
| Seed | 0 to 9999 | random | Another arrangement of the same pattern; the dice rolls a new one. |

## Wear

Wears the paint below away at the model's edges, bumps and random patches, showing the game's texture and metal. Edges go first.

![A wear layer](shot:wear-layer)

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Amount | 0 to 1 | 0.4 | How much is worn off. |
| Edges first | 0 to 1 | 0.7 | How much the wear prefers edges and bumps. |
| Scratches | 0 to 1 | 0.3 | Fine scratches on top. |
| Patch size | 3 to 200 mm | 30 mm | Size of the worn patches. |
| Bare metal colour | any colour | light grey | The metal that shows through. |
| Metallic | 0 to 1 | 0.85 | Of the bare metal. |
| Smoothness | 0 to 1 | 0.55 | Of the bare metal. |
| Seed | 0 to 9999 | random | Another arrangement. |
| Wear with kills | 0 to 100000 kills, step 50 | 0 | 0 = fixed wear. Above 0 the gun starts clean and wears in four stages with your kills with it, reaching the amount above at this many kills. Kills are counted per skin and gun. |

> [!NOTE]
> Wear stages, like everything else in the livery, are baked when you apply or export. A skin with wear that grows with kills bakes four versions of the part's textures.

# Decals, stickers and text {#decals}

Decals, panel lines and text are **projected**: they sit in a box on the part and print onto the surface, sliding along it and turning with its curves.

## Put a decal on the gun

1. Select a part, [Livery](editor:livery), **Decal**.
2. Pick a sticker from the library, one of your own, or **Image file…**.
3. Click the part in the 3D view where the decal should go, then drag to slide it along the surface.
4. Fine-tune under **Placement** by number.

![The sticker library: 28 built-in stickers, your own stickers below](shot:sticker-dialog)

The built-in stickers are white shapes; the decal's **Tint** colours them. **Import images** copies your own pictures into the editor's data folder, so every skin can use them later (png, jpg, jpeg, webp, bmp, gif). The **×** on one of your stickers takes it out of the library; skins that use it keep working.

## Placement

![Placement of a projected layer](shot:placement)

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Position (mm) | X, Y, Z | where you clicked | The same values dragging sets, in the part's own space. |
| Rotation (°) | X, Y, Z | from the surface | Which way the box faces. |
| Size (mm) | W, H, at least 1 | 40 wide, height from the picture | Not for text: its width follows the letters. |
| Rotation | −180 to 180° | 0 | Turns the picture on the surface. |
| Reach (mm) | at least 0.5 | 10 | How far it prints into and out from the surface. Too much prints on the far side of the part; too little cuts it off on curves. |
| Mirror | on / off | off | Decals only: flips the picture. |
| Both sides | on / off | off | Also places it at the mirror position on the other side of the gun. Text reads the right way round there too. |

A layer that shows **Not placed yet** needs one click on its part in the 3D view.

## GIF decals

A `.gif` decal with more than one frame gets an **Animation** section and plays in game like an animated layer: pick when it plays, how (**Loop**, **Play once**, **Loop N s**) and the **Frame rate** (1 to 60; its own rate unless you set one). Until it plays it shows its first frame. Paint above and below it is baked into every frame. When many large frames do not fit one animation image in the game, each is scaled down and the editor says so.

## Text

![A text layer: words, font, letter height, letter spacing](shot:text-layer)

1. [Livery](editor:livery), **Text**. It starts as `FORGE` in Orbitron.
2. Type the words (up to 4 lines show in the box; Enter starts a new line).
3. Pick a font (installed fonts, the bundled tech and display fonts, or a font file), set **Letter height** and **Letter spacing**.
4. Click the part in the 3D view to place it, then drag.

| Setting | Range | Default |
| --- | --- | --- |
| Letter height | 1 to 60 mm | 8 mm |
| Letter spacing | −0.1 to 0.6 | 0.08 |

Text has the same **Look**, **Placement** and **Mask** sections as a decal.

# Panel lines {#panel-lines}

Panel lines are sci-fi frames, seams, ticks, corner brackets, chevrons and hazard stripes drawn in code, so they stay sharp at any size. They are placed like a decal. With **Glow** on (the default) they light up in game: glowing seams.

![Left: the front without panel lines. Right: the Armour panels preset in orange with Glow on.](shot:panel-lines-before-after)

## Add panel lines

1. Select a part, [Livery](editor:livery), **Panel lines**. A new layer starts as the Sci-fi plate preset, cyan, with Glow on.
2. Click a preset (1 in the picture below). It sets the style, the line lengths and the box size.
3. Click the part in the 3D view where the lines go, drag to slide them.
4. Set the colour under **Look**. Turn **Both sides** on under **Placement** to put the same lines on the other side.

![Panel lines: 1 presets, 2 style; the sliders below change with the style](shot:panel-lines)

## The 11 presets

| Preset | Style | Box (mm) |
| --- | --- | --- |
| Sci-fi plate | Cut-corner frame | 60 × 30 |
| Octagon | Octagon | 40 × 25 |
| Double frame | Double frame | 50 × 30 |
| Corner brackets | Corner brackets | 40 × 30 |
| Ruler | Ruler frame, 12 ticks | 60 × 12 |
| Armour panels | Panel grid, 3 across | 90 × 30 |
| Single seam | Seams, 1 line | 120 × 4 |
| Dashed seams | Seams, 2 dashed lines | 100 × 6 |
| Chevrons | Chevrons, 3 | 30 × 12 |
| Hazard strip | Hazard stripes, 8 | 60 × 12 |
| Dashed frame | Frame, dashed | 50 × 30 |

## Styles and their settings

Every style has **Line width** and **Inset**; the others show only where the style uses them. All lengths are in millimetres.

| Style | What it draws | Settings it uses |
| --- | --- | --- |
| Frame | A plain rectangle | width, inset, dash |
| Cut-corner frame | Two opposite corners cut at 45° | width, corner cut, inset, dash |
| Double frame | An outer frame and a thinner inner one, all corners cut | width, corner cut, inset, dash |
| Octagon | All four corners cut | width, corner cut, inset, dash |
| Ruler frame | A thin frame with ticks along top and bottom, every third longer | width, inset, dash, count (ticks) |
| Corner brackets | An L in each corner | width, bracket length, inset, dash |
| Chevrons | Arrow heads pointing right | width, inset, dash, count |
| Hazard stripes | Diagonal stripes inside a frame | width, inset, count |
| Seams | Lines across, evenly spaced | width, inset, dash, count |
| Panel grid | Panels about square, corners cut, a gap between them | width, corner cut, inset, gap between panels, count (across) |

| Setting | Range | What it does |
| --- | --- | --- |
| Line width | 0 to 5 mm | Thickness of the lines. |
| Corner cut / Bracket length | 0 to 20 mm | Size of the 45° cut; for corner brackets the length of each arm. |
| Inset | 0 to 10 mm | Moves the lines in from the box edge. |
| Dash length | 0 to 10 mm | 0 draws solid lines; above 0 the lines are dashed. |
| Dash gap / Gap between panels | 0 to 10 mm | The space between dashes, or between panels of the panel grid. |
| Count | 1 to 16 (Ruler frame up to 48) | Ticks, chevrons, stripes, seams or panels across. |

> [!TIP]
> For glowing seams keep **Glow** on and set **Glow strength** in the same **Look** section. All glowing layers of one part share that strength.

# Gradient and tiled image {#gradient}

## Gradient

A colour ramp over the part, linear or from a centre outwards. It starts along the part's longest side, from dark blue to cyan.

![A gradient layer: the ramp, its colours, shape and direction](shot:gradient-layer)

1. [Livery](editor:livery), **Gradient**.
2. Click a colour square to change it, drag its slider to move it along the ramp. **Add a colour** adds one halfway along the widest gap (2 to 4 colours); the **×** removes one. Colours can be partly transparent.
3. Pick **Shape** (**Linear** or **Radial**) and a **Direction**: **Along** the gun, **Top down**, **Across**, or **From centre**.
4. Fine-tune **Start (mm)** and **End (mm)**, the two ends in the part's space.

## Tiled image

A picture repeated over the part at the size you set, projected from three sides so it runs across texture seams. Good for carbon weaves, grip textures and logos repeated over a stock.

1. [Livery](editor:livery), **Tiled image**, and pick a picture: from **My tile images** (import them once with **Import images**) or any file.
2. Set **Tile size** (2 to 300 mm, default 30 mm) and **Pattern rotation** (X, Y, Z).
3. **Tint** under **Look** colours the picture.

# Masks {#masks}

Every paint layer has a **Mask** section: wherever the mask is painted, the layer is hidden.

## Paint a mask

1. Select the layer and turn on **Paint mask** (in the **Mask** section or in the bar over the 3D view).
2. Drag on the part to mask: **Free hand**, **Line**, **Circle**, or **Fill a piece** (one click masks a whole UV island). **Width** is 0.5 to 30 mm; **Mirror** masks both sides at once.
3. Turn **Paint mask** off to go back to placing or painting. **Clear mask** removes all mask strokes; the count of strokes is shown next to it.

## Fade mask

**Fade mask** fades the layer out along a direction or outwards from a centre, with no painting.

![A fade mask on the hazard strip](shot:mask-fade)

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Shape | Linear, Radial | Linear | |
| Direction | Along, Top down, Across, From centre | along the longest side | Sets Start and End from the part's size. |
| Start / End (mm) | X, Y, Z | the part's two ends | The two ends of the fade. |
| Shown to gone | 0 to 100 % | 30 % to 70 % | Where the layer is still fully shown and where it is gone. |
| Reverse | button | | Swaps the shown and the gone end. |

#! Colours

# Colour variants, palettes and player colours {#colors}

With nothing selected, **Overview** on the right has three sections for the skin's colours.

![Overview: colour variants, palettes and player colours](shot:overview)

## Colour variants

Several colourings of one skin, such as a night version. Variants appear once the skin has paint layers.

1. Click **New variant**. It starts with the colours shown now and is selected.
2. Change livery colours. With a variant selected, colour edits change only that variant; the layers' own colours stay.
3. Click **Original colours** to go back. Rename a variant in the field under the list; the bin deletes it.

**Apply** on the card uses the selected variant. An exported mod has each variant as a skin of its own, named "skin name · variant name". The Skins gun pack format takes only the original colours.

## Palettes

A palette is a set of up to 8 colours kept in the editor's data folder, for every skin.

- **Save this skin's colours**: type a name (or leave it empty) and click the save icon. It saves the first 8 distinct colours of the paint layers.
- Click a palette's name: every colour picker offers its colours as swatches. Click again to stop.
- **Recolour this skin with it** (wand icon): the skin's n-th colour becomes the palette's n-th, transparency kept. One undo step; with a variant selected it goes into the variant.

## Player colours

You can let players change your skin's colours themselves in game.

1. Click the **person button** next to a colour setting to make it a player colour, or join it to an existing one so they always change together.
2. In **Player colours**, name each one and set its default.
3. To give players ready-made sets, click **Whole gun** at the top of the parts list, set the colours under **Colour schemes** and click **Save as scheme**; save as many as you like. **Original** is every colour's default.

One player colour can drive tint, glow, attachment colour, screen digits, panel, border, low-ammo colour and effect flash colours. In game, the colour page of the lobby SKINS picker shows your colour schemes as cards that players switch with one click, and under each player colour its default and a row of hues.

#! Moving effects

# Animated layers {#layers}

Animated layers are moving textures on a part: **Image**, **Sequence** (several images ordered by the number in the file name), **Video** (MP4) or **Text**. Add one from [Layers](editor:layers), from the part's **+** (**Animated textures**), or drop an image, sequence or video on the window with a part selected.

1. Drag the layer in the 2D preview of the part's texture to move it, drag the corners to scale, the top dot to rotate. The 3D view plays along.
2. Under **Placement**: **Channel** (Albedo or Emissive), **UV position**, **Size**, **Rotation °**, **Opacity**, **Crop**.
3. Under **Playback**: **Trigger**, **Play** and **Frame rate**.

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Trigger | Always, or an event | Always | Events: Kill, Hit, Miss, Fire, Dry fire, Reload start and end, Equip, Unequip, Aim, Aim end, Low ammo, Clip empty, Ammo pickup, Flashlight on and off, Downed, Revived; items have Use, Take out and Put away. Melee weapons and tools list only what they can do. |
| Play | Loop, Play once, Loop N s | Loop | For an event: shows the normal texture until the event, then plays; a repeat event restarts it. |
| Loop N s | at least 0.1 s | 3 s | |
| Mag below | 5 to 90 % | 20 % | For Low ammo. |
| Frame rate | 1 to 60 | 12 | Videos are split into images at this rate, 240 frames at most. |

Text layers have **Font**, **Font size px** (at least 8, default 64), **Colour** and letter spacing.

> [!WARNING]
> Frames cost video memory in game. Many frames and large layers take the most; **Overview** shows the estimate after a build.

# Code effects {#codefx}

Code effects are moving patterns the game draws live with ForgeCore's drawing programs: flow lines, scanlines, pulse, noise, rim light and polar pattern. A skin can have any number. They need no picture in the pack, and players can be allowed to change their values in game.

![A new code effect on the front](shot:code-effect)

## Add one

1. Click the **+** on a part row in the left list, then **Animated pattern on this part** under **Code effect**.
2. Under **Where it draws**: **Draw on** the part's own material (painted into its texture, over its pictures and animated layers) or an attached model; **Paint into** Albedo or Emissive.
3. Pick a **Pattern** and set its numbers and colours.
4. Set **Size and motion** and **When it plays**. **Play the event in the 3D view** shows an event effect once.
5. Turn on the person button next to a value to let players change it on the **ADJUST** page of the SKINS picker. Their setting stays on their own computer.

The 3D view uses the same pattern code as the game.

## Size and motion (every pattern)

| Setting | Range | Default |
| --- | --- | --- |
| Size | 0.1 to 8 × | 1 |
| Rotation | −180 to 180° | 0 |
| Spin | −360 to 360 °/s | 0 |
| Intensity | 0 to 4 × | 1 |
| Blend | Paint over, Add light | |
| When it plays | Always, or an event with **Shows for (seconds)** | Always |

## The patterns

| Pattern | Numbers (range, default) | Colours (default) |
| --- | --- | --- |
| Flow lines | Density 1–64 (12); Speed −4–4 (0.5); Width 0.01–0.5 (0.08); Angle −180–180° (0); Glow 0–1 (0.5) | Line (cyan), Base (transparent) |
| Scanlines | Lines 4–512 (120); Speed −4–4 (0.2); Thickness 0.05–0.95 (0.4); Sweep width 0–1 (0.15); Sweep speed −4–4 (0.5) | Line (green), Sweep (white) |
| Pulse | Rate 0.05–8 Hz (1); Sharpness 0.1–16 (2); Floor 0–1 (0.1); Rings 0–32 (0) | Low (dark blue), High (orange red) |
| Noise | Grain 0.5–64 (6); Speed −4–4 (0.3); Detail 1–5 (3); Contrast 0.1–8 (1.5); Object space 0/1 (1) | Low (black), High (violet) |
| Rim light | Falloff 0.1–16 (3); Base 0–1 (0); Flicker 0–1 (0); Flicker rate 0–30 Hz (8) | Rim (light blue), Core (transparent) |
| Polar pattern | Petals 1–32 (6); Rings 0–32 (4); Twist −8–8 turns (0); Sharpness 0.1–16 (2); Radius 0.05–1 (0.5); Ring drift −4–4 (0.25) | Petals (yellow), Rings (orange), Base (transparent) |

> [!NOTE]
> Hologram scanlines and edge glow are code effects too (see [Shapes and holograms](#holo)). The Skins export formats cannot carry code effects; the export report lists them.

#! Attachments

# Attachments and part layout {#attach}

Attachments are extra things mounted on the gun: ammo screens, your own models, built-in shapes and charms. Select a part, then [Attach](editor:attach), **Add attachment**:

- **Ammo screen**: a live number or bar; see [Ammo screen](#screen).
- **Import model (.glb / .gltf)**: any .glb or .gltf model. Its own pictures (base colour, and its emissive, normal and metallic-roughness ones if it has them) come along at their own size and show in the editor and in game (only a picture over 16384 is scaled down); **Colour** tints them, and as a hologram the base-colour picture is what glows.
- **Picture hologram…**: pick a picture (png, jpg, webp) and get a hologram screen showing it; see [Picture hologram](#holo-image).
- **Shapes**: built-in hologram shapes; see [Shapes and holograms](#holo).
- **Hologram sets**: several matching holograms at once, placed on the gun's parts; see [Hologram sets](#holo-sets).
- **Charm**: an imported model or a shape hung on a cord; see [Charms](#charms).

![The Attach tab of the receiver](shot:attach-tab)

The same items are in the **+** menu of a part row in the left list:

![The + menu of a part: everything that can be added to it](shot:add-menu)

## Move, turn, scale

A new attachment mounts just above the selected part; **Mount** → **On part** moves it to another part later.

- In the 3D view press <kbd>G</kbd> to move, <kbd>R</kbd> to rotate, <kbd>S</kbd> to scale, or drag the handles. While moving, <kbd>X</kbd>, <kbd>Y</kbd>, <kbd>Z</kbd> lock an axis; click or <kbd>Enter</kbd> confirms, right-click or <kbd>Esc</kbd> cancels.
- Turn on **Snap to surface** (in the bar over the view) and click the gun: the selected attachment lies flush on the surface you clicked, its back on the surface, its front facing out, upright as the view sees it. Ammo screens, hologram shapes and picture holograms all place this way. Clicking an attachment does not select it then; <kbd>Esc</kbd> or the button again leaves the mode.
- **Place** has the numbers: **Position mm**, **Rotation °**, **Scale ×** (the lock keeps it uniform). Drag a number left or right to change it (<kbd>Shift</kbd> fine, <kbd>Ctrl</kbd> snap). **Always face the camera** turns it towards the viewer.
- **Duplicate** on an attachment's row in the left list copies it; the bin on the right panel deletes it.

## Look and effects of an attachment

- **Look** → **Model look**: **Colour** and **Unlit (glows by itself)**.
- **Effects** → **Add effect**: **Flash** lights the attachment up in a colour and **Brightness** for a moment; **Show** shows it only for a while (for example only while aiming). Each effect has its event and its seconds (at least 0.05; a new one flashes for 0.4 s on Kill, brightness 4).

## Change a part itself

A part's [Place](editor:place) tab moves, turns, scales or hides the part (**Hide (also in game)**); **Reset all** and **Restore original** put it back. A part with its own model shows **Replace model…** under **Attach** → **Model**: your .glb replaces only that part's model; child parts stay, textures and materials still apply, and position, rotation and scale stay editable. **Restore original model** undoes it.

The **Grip and aim** group at the end of the parts list has this gun's right-hand grip, left-hand grip, muzzle and aim line. The game puts both hands on the grip points, shots leave from the muzzle point, and aiming looks along the aim line; when resizing or moving parts leaves the hands off the gun or the aim off, click a row and drag that point into place in the 3D view (while one is selected all four show as coloured dots, the muzzle and the aim line with a line along their direction, and the game's own hand is drawn on each grip point: its wrist on the point, its fingers the way that hand holds this gun). The arrow on a row puts it back where the game has it. Points this gun does not have are not listed. Under the grip points, **Right hand shape** and **Left hand shape** change how the fingers hold: pick the grip of another of the game's guns (listed by the guns that use it), and the hand stays on this gun's grip point. Only you see a hand shape, in first person. While a grip point is selected, every finger segment of that hand has a dot at its far end in the 3D view: drag a dot and the segment turns about its joint to follow the pointer; click a dot and rings appear on its joint, to turn the segment about any axis (to twist it, say). A hand whose fingers were moved gets a **Fingers** row under its grip point, and the arrow on it puts all its fingers back on the hand shape; every step can be undone. You and other players both see the finger changes; while the gun reloads the hands go to the magazine, so the changes fade out and come back afterwards.

## Sounds

[Sounds](editor:sounds) (on any selection) plays clips on kills, low ammo, going down, being revived and other moments, at most 10 seconds each. Teammates who have the skin hear only its fire, equip and unequip, going down and revived sounds, from your gun. Volume is set only there.

- **Add sound** asks for the event first, then the file (wav, mp3, ogg, flac, m4a, aac, opus, webm). It is converted to Ogg Vorbis in the pack.
- One event holds several sounds: each time one plays at random by **Weight** (chance = its weight ÷ the group's total), never the same one twice in a row.
- Kills can have a group per enemy: pick it in **Which enemy** (every enemy of the game's data, type to search). Other kills play the **Any enemy** group.
- Downed and Revived play only for the item in your hands at that moment.

# Shapes and holograms {#holo}

Shapes are built-in models made from a few numbers: lines, rings, reticles, grids, hex shields and more. Added from **Shapes**, they start as a **hologram** in the skin's colour (its first player colour, else its first colourful paint layer, else cyan); solid shapes start as wireframes.

![Look of a shape: 1 the shape's numbers, 2 the hologram switch and its settings](shot:hologram-look)

## Add a hologram shape

1. Select a part, then [Attach](editor:attach), **Add attachment**, **Shapes**, and a shape. (Or **Hologram shapes** in **Overview**.)
2. Move it into place with <kbd>G</kbd> / <kbd>R</kbd> / <kbd>S</kbd> or the **Place** numbers.
3. Open **Look**: set the shape's numbers, then the **Hologram** settings.

## Picture hologram {#holo-image}

**Add attachment** → **Picture hologram…**, then a png, jpg or webp picture. The editor copies the picture next to the skin at its own size (scaled down only over 16384) and makes a hologram screen 30 mm wide with the picture's proportions, named after the file. The picture is what glows: black is see-through, a white **Colour** keeps the picture's own colours and any other colour tints it. Change **Width** and **Height** under **Picture** in **Look**; use **Snap to surface** to lay it on the gun.

## Hologram sets {#holo-sets}

**Add attachment** → **Hologram sets** adds several holograms in one style: the same line width and tick length, the same fade from the skin's colour to a lighter one, the same flicker and edge glow. The editor places them from the open item's parts:

| Set | What is in it | Where it goes |
| --- | --- | --- |
| Sight ticks | A tick arc open at the bottom, a ring inside it | Just in front of the sight (above the receiver's front when there is no sight) |
| Barrel tick rings | 3 reticle rings, smaller towards the muzzle | Around the front of the barrel |
| Info strip | A wire panel with two data bars | The receiver's left side (the side a player sees), facing out |

Afterwards each one is an ordinary shape attachment named "set · shape": move it, change its numbers or delete it on its own. Each hologram comes with edge glow; turn it off in **Look**.

## The shapes

All lengths in millimetres; the smallest length is 0.1 mm.

| Shape | Numbers (default, range) | Wireframe |
| --- | --- | --- |
| Line | Length 50, Thickness 0.8 | |
| Sphere | Radius 10, Segments 12 (4–48), Thickness 0.8 | yes |
| Ring | Radius 12, Thickness 0.8 | |
| Arc | Radius 12, Angle 120° (5–360), Thickness 0.8 | |
| Box | Width, Height, Depth 20, Thickness 0.8 | yes |
| Grid | Width 40, Height 30, Cells 6 (1–32), Thickness 0.4 | |
| Cylinder | Radius 8, Height 20, Segments 16 (3–64), Thickness 0.8 | yes |
| Cone | Radius 8, Height 20, Segments 16 (3–64), Thickness 0.8 | yes |
| Panel | Width 30, Height 20 | yes (starts solid) |
| Hex shield | Radius 4 (one cell), Cells 2 rings around the centre (1–6), Thickness 0.4 | |
| Reticle ring | Radius 12, Ticks 24 (4–72), Length 2, Thickness 0.8 | |
| Tick arc | Radius 15, Angle 90° (10–360), Ticks 18 (2–72), Length 2, Thickness 0.8 | |

**Wireframe** draws only the edges as thin bars, **Thickness** thick. On the reticle ring and tick arc every third tick is longer.

## Hologram settings

Any attachment can be a hologram (the **Hologram** switch in **Look**); the last four settings need a model attachment (a shape or an imported model).

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Flicker | 0 to 1 | 0.3 | How much it flickers. |
| Brightness | 0 to 1 | 1 | How much light it gives. |
| Two-colour fade | on / off, **Top colour** | off (pink when turned on) | Fades from the model's colour at the bottom to the top colour. Built-in shapes fade bottom to top; an imported model follows its own texture layout. |
| Scanlines | 0 to 1 | 0 | Moving scanlines over the model. |
| Edge glow | 0 to 1 | 0 | A lit outline. |

![Left: flicker only. Right: two-colour fade, scanlines 0.5, edge glow 0.6.](shot:hologram-before-after)

## In game and in the editor

The game draws holograms with its own hologram shader, which has no colour ramp, scanlines or outline of its own. So:

- The **two-colour fade** is baked into a small picture on the model when the skin is packed; in game it looks as in the editor.
- **Scanlines** and **edge glow** become two code effects on the attachment (ForgeCore's scanlines and rim light patterns).
- Flicker and brightness are the shader's own and can look a little different in game lighting. Check in game once.

# Charms {#charms}

A charm hangs an attachment on a cord, chain or beads from a point on the gun. In game it sways with gravity and swings as you run, turn, jump and shoot, only on the gun in your own hands.

![Charm settings in the Place tab](shot:charm-place)

## Hang a charm

1. Select a part, then **Add attachment** → **Charm** → **Import model (.glb / .gltf)** or a shape. The new charm is selected with **Place** open. (Any attachment can become a charm: turn on the **Charm** switch at the top of **Place**.)
2. The orange dot in the 3D view is where the rope is fixed; a new charm's sits under the middle of its part. Move it with the gizmo or the position fields; the model hangs at the rope's end.
3. Pick **Cord**, **Chain** or **Beads** and set the sliders.
4. Click **Shake**, **Run** or **Turn** to see it move.

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Style | Cord, Chain, Beads | Chain | |
| Length | 0.5 to 30 cm | 5 cm | Rope length. |
| Thickness | 0.3 to 10 mm | 2 mm | |
| Colour | any colour | light grey | Of the rope. |
| Weight | 0.1 to 3 | 1 | Heavier swings slower and further. |
| Settles | 0 to 1 | 0.3 | How fast it stops swinging. |
| Stiffness | 0 to 1 | 0.25 | How much the rope keeps its shape. |
| Bends | 1 to 12 | 6 | Joints in the rope. |
| Stays out of the gun | on / off | on | The rope and model do not pass through the gun. |

**Shake** jolts the gun sideways, **Run** runs forward for two seconds and stops, **Turn** turns around and back.

# Ammo screen {#screen}

An ammo screen is a small screen on the gun that shows a live number or a bar. Add one with **Add attachment** → **Ammo screen**, then set it up under **Screen** and **Look**.

![The Screen tab of an ammo screen](shot:screen-tab)

| Setting | Range | Default | What it does |
| --- | --- | --- | --- |
| Show | Rounds in mag, Reserve, Total ammo, Kills, Damage dealt (this weapon), Damage dealt (all weapons), Damage taken, Accuracy %, Health %, Infection %, Fixed text | Rounds in mag | What it shows. Damage is counted for the current level. |
| Style | Digits, Bar | Digits | Bar only for ammo and the % values. |
| Digits | Auto, 1 to 6 | Auto | Auto: as many as the gun's ammo needs; kills up to 4, damage up to 6, % as 3 digits and %. |
| Leading zeros | on / off | on | |
| Size mm | W, H, at least 0.5 | 30 × 15 | |
| Offset mm | 0 or more | 0.5 | Gap from the surface. |
| Font size | 0.2 to 1 | 0.8 | Share of the screen height. |
| Glow | 0 to 5 | 1.5 | |
| Glow in game | on / off | on | Whether it lights up in game. |
| Font | installed fonts or a font file | | |

## Colours and border

![Colours of the screen, with the border style](shot:screen-look)

| Setting | Range | Default |
| --- | --- | --- |
| Text | any colour | light cyan |
| Background | colour with transparency | black, 85 % |
| Border | colour with transparency | light cyan |
| Border style | Solid, Corners, Double, Notched, Ticks | Solid |
| Border mm | 0 or more | 0.8 |
| Low ammo colour | on / off and a colour | on, red |
| Below | 5 to 90 % | 20 % |
| Gradient near low ammo | on / off | off |
| Gradient from | just above Below to 100 % | 40 % |

In game the background and border are only shown or hidden: below 1 % opacity they are hidden. Click **Fire**, **Kill** and the other buttons under the 3D view to watch the numbers change.

#! Finish and share

# Preview {#preview}

The 3D view is the preview; every change shows right away.

- The event buttons under the view (Kill, Hit, Miss, Fire, Reload start, Equip, Aim, Low ammo and so on) play animated layers, attachment effects, code effects and sounds by the game's rules.
- The sun button at the bottom left sets **Ambient** light (0 shows only glow), the **Key light colour** and **Bright glowing pixels bloom**; the arrow button turns **Auto-rotate** on.
- **Perspective** / **Orthographic** and **Frame selection** sit at the bottom right.

The preview stays close to the game, but the game has the last word. After installing, **Check against game** at the bottom of **Export** → **Check** compares the gun the game assembled with the preview (turn on Readback in the profile's ForgeSkin settings and take each weapon out once in game first).

# Save, snapshots and install {#save}

## Saving

- Every change is saved to the editor's data folder about 1.5 seconds later; the status bar says **Autosaved**.
- The data folder is `Documents\ForgeSkin` for the installed app and `GTFO_UserData` next to the program for the portable copy. **Settings** → **Data folder** opens it, or **Change…** moves it: pick an empty folder and your skins and caches are copied, checked, and only then deleted from the old place. If the move cannot finish, the data stays where it was.
- <kbd>Ctrl</kbd>+<kbd>S</kbd> or the card's **More** → **Save to file…** keeps a `.forgeskinproj` project file of your own; later saves overwrite it. <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> saves it as a new skin.

## Snapshots

A snapshot keeps the skin as it is now; you can go back to it any time, even after closing the app. Card **More** → **Snapshots**: type a name (or leave it empty for the time), **Save snapshot**. **Restore** first saves the current state as a snapshot, and <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes the restore.

## Install into the game

1. Choose the profile under **Apply to:** at the top of the works list on the left (an r2modman profile with BepInExPack_GTFO). If it is not listed, add it with **Choose folder…**.
2. Click **Apply** on the work's card (for the open work, <kbd>Ctrl</kbd>+<kbd>Enter</kbd> does the same). The editor installs the plugin when needed, packs the skin, installs it into the profile and makes it the skin the game uses for this item.
3. With the game running, the change shows within seconds; otherwise next time the game starts.
4. In game, click **SKINS in the lobby** to see and switch skins.

The top left of each work's picture says where it stands in this profile (in each group, works in use come first, then those in the profile, then those only in the editor):

- **Only in the editor**: the game cannot see it.
- **In the profile**: it can be picked on the game's skin page, but is not worn.
- **In use**: the game wears it on this gear; its mark is green, the card has a green frame and no button. To go back to the original look, change it in the game's skin picker; the pack stays in the profile.
- **Changes not applied**: changed after it went into the profile, so the game still has the old one; the line under the card's name says so. Click **Apply** to update.

Only one work per gear is in use; applying another puts the one before back to "In the profile". Point at a card and **⋯** shows at the top right of its picture; a right click on the card opens the same menu, which also has **Put in profile**, which puts it in without wearing it, and **Take out of profile**.

Packs by others in this profile are folded into one bar at the top of the list (it says how many; open it to see their cards); **Move all** makes them your works. Character packs move too, with their bone match and settings; the model itself cannot be imported again. To install a pack someone sent you, use **Add packs…** under the arrow next to **New**. Drag the left column's right edge to widen it.

**Apply automatically** in **Settings** (off by default): on, while the game runs, the work in use is applied 2 seconds after each change, speaking up only when something goes wrong.

> [!WARNING]
> Applying writes real files into that profile. Use a profile meant for testing; to give the skin to others, use **Publish**.

# Publish and share {#publish}

![The Publish menu](shot:publish-menu)

- **Check**: goes through every skin before export and lists what to fix. Click a problem to go to it.
- **Export skin pack…**: one `.forgeskin` file with everything in it.
- **Export as Thunderstore skin mod…**: a mod ready to upload to Thunderstore, with skins of any items. Give a name (letters, digits and _), a version like 1.0.0, a description (up to 250 characters) and an icon (fitted whole into a 256 × 256 grey square, so a long gun keeps its muzzle and stock; the skin's card picture by default). Three formats:
  - **ForgeSkin**: every effect, applied by the ForgeSkin plugin.
  - **Skins: a group per skin**: for players with the Skins library. Each skin is its own skin group; a weapon may get several, and players pick in game.
  - **Skins gun pack**: the whole mod is one skin group; pick one skin per weapon (or "Not in the pack").
  - Skins carries still textures and material settings only. Animated layers, attachments, sounds, part changes, wear stages, code effects and player colours are left out, item skins are skipped, and everything left out or changed is listed after the export.
- **Export OBJ model (by part)…**, **Export OBJ model (merged)…**, **Export UV templates…**: for painting in other programs.

## Edit game files directly

No plugin: textures and material settings (metallic, smoothness, glow, colour) are written straight into the game's own asset files, after the originals are backed up. Attachments, screens, animated layers, sounds and layout changes cannot go in.

- **Replace original game assets…** shows what will be written and what is skipped, then writes on confirm. Close the game first; it shows the next time the game starts.
- **Restore original game assets…** puts the backups back, per skin or all.
- **Export game asset pack…** saves a zip of the changed game files plus a README with install steps, for players without this app. The game folder is not touched. A pack fits only the game build it was made for.

A game update or a Steam file check puts the original files back.

#! Help

# Common problems {#faq}

## The skin looks blurred in game

- Most often the game's **texture quality is not at full**. The SKINS picker then shows a yellow note that textures show at a fraction of their size. Set texture quality to full in the game's settings.
- Check the part's **Texture resolution** (**Textures** or **Livery** tab) and raise it a step; then check whether the image itself is small.

## A skin pack is not loaded

A pack the game cannot read does nothing; the right side of the SKINS picker lists it in red with the reason. Common reasons:

- A file is missing: an image, sound or model the pack names is not there. Apply or export again.
- A bad or oversized image: the game reads only PNG, JPEG and the DDS the editor compresses, at most 16384 pixels on a side. The editor converts pictures itself when it applies or exports, so this usually means the pack was edited by hand: export it again.
- A bad sound: it must convert to Ogg, at most 10 seconds.
- A bad path: paths in a pack cannot be absolute or contain `..`. Usually the pack was edited by hand; export it again.
- An old plugin: when the editor says ForgeSkin or ForgeCore is too old, update them in r2modman.

## I clicked Apply and nothing changed

- Check that the profile under **Apply to:** at the top of the left column is the one you play with.
- When the profile's plugin needs an update while the game runs, the left column says the update is pending: close the game, the editor installs it by itself, then click **Apply** again.
- Each item wears one skin at a time: see whether the card says **In use**, and click **Apply** if not.

## Apply on a character's card says the bones are not matched

The hips have no bone yet. Open **Bones**, click the red hips and pick a bone, until it says **Ready to use.** (see [Bones](#char-bones)).

## My character still looks original in game

- Check under **SKINS** → **CHARACTER** in the lobby that your character is picked.
- A character installed while the game runs shows from the next level.
- Teammates without your character mod, or with another version of it, see the original character.

## "The plugin files the editor installs are missing"

The editor carries the plugin it installs into your profile. This message means those files are missing from the editor itself; it names each missing file with its full path. Fix it by how you run the editor:

| How you run it | Fix |
| --- | --- |
| Installed (Setup.exe) | Run the latest Setup.exe again. The plugin sits in the install folder under `app-<version>\resources\publish\plugin`; your data folder (`Documents\ForgeSkin`) is not touched. |
| Portable zip | Unzip the whole zip again over the same folder. `resources\publish\plugin\ForgeSkin\ForgeSkin.Plugin.dll` and `resources\publish\plugin\ForgeCore\ForgeCore.dll` must be there; `GTFO_UserData` next to the program is kept. |
| From the source code | The editor run from source installs a plugin only when you ask: **Build the plugin from source and install it** in the profile menu, or **Apply** when the profile's plugin is missing or out of date; either builds the plugin from the checkout first. The first time, run `pwsh Mods/ForgeSkin/tools/build-editor-release.ps1 -PluginOnly -ForgeCore <NAinfini-ForgeCore zip>` (the ForgeCore zip of the release set) once from the repository root; later builds reuse that ForgeCore zip. If it fails, the reason is usually the BepInEx profile path: pass `-BepInEx <profile>/BepInEx` or set `GTFO_BEPINEX_PATH`. |

## The log

![Settings: language, apply automatically, game and data folders](shot:settings-menu)

The log button in the status bar at the bottom left opens the log panel, with the messages of the editor and of the game plugin. While the game runs, problems in game (a part left without the skin, a pack that did not load, a code effect that was skipped) go into the log and pop up at the bottom right.

## Guided tours

The book button in the top bar holds five guided tours: **Make your first skin**, **Character models**, **Profiles**, **Hot reload** and **Colour schemes**, each opened on its own; click where it lights up. The button at the bottom left of this tutorial restarts the first one, which makes a new practice work and deletes it (and takes it out of the profile) when the guide ends or you leave it; your other works are not touched.

# Control the editor with AI {#ai}

An AI agent (Claude, Codex or a script) can drive the editor through the Chromium DevTools protocol. Started with `--ai-control`, the editor opens a debugging port on 127.0.0.1 and offers `window.forgeAI`, a structured interface to everything the editor does: read the skin as JSON, change any field, place decals by UV, set the camera, render the 3D view. To choose a file, the agent gives the path with `pickNext` first and then clicks the button: the next file dialog does not open and uses that path. Without it, the system's dialog opens, which only a person can close.

## Start the editor for an agent

| Copy | Start it with |
| --- | --- |
| Installed | `& "$env:LOCALAPPDATA\ForgeSkin\ForgeSkin.exe" --ai-control` |
| Installed, from the Start-menu shortcut | `setx FORGESKIN_AI_CONTROL 1` once, then start ForgeSkin as usual (`setx FORGESKIN_AI_CONTROL ""` turns it off again) |
| Portable | `ForgeSkin.exe --ai-control` in the unzipped `ForgeSkin-win32-x64` folder |
| Source code | `pnpm start -- -- --ai-control` in `Mods/ForgeSkin/Editor` |

`--ai-control=9444` picks the port (1024 to 65535, default 9333). Check `http://127.0.0.1:9333/json/list`: it lists one page titled ForgeSkin.

> [!WARNING]
> Close the editor you have open first: two editors share one data folder and settings. And use a test profile; **Apply** writes real files.

## What to give the agent

Point it at **AI-GUIDE.md** in the ForgeSkin source code (`Mods/ForgeSkin/AI-GUIDE.md`). It lists every command of `window.forgeAI`, the document paths, the path dialog and the main flow, with what tells the agent that each step worked. A first call to try:

```js
await forgeAI.state()                                   // the open item, skin, selection and the whole skin
await forgeAI.command('selectWeapon', { id: 'helr' })   // open the Omneco LRG
await forgeAI.command('help')                           // every command with its arguments
```
