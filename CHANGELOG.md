# Changelog

## 1.1.1

- The skins folder and `forgeskin.log` moved to `BepInEx/ForgeSkin/` (next to the wear counts and readback), out of the plugins
  folder. The editor moves skins it installed before into the new folder the next time it opens; until then the game does not
  load them and its log says so once per launch. Skins put by hand into `BepInEx/plugins/ForgeSkin/skins` are moved the same way.
- Editor livery: panel lines and borders (cut-corner, double, notched, ticks, brackets, chevrons, hazard stripes, seams, panel grid) with presets, sharing the drawing with the ammo screen's borders; text layers with font, letter height and spacing; gradient layers (linear or from the centre, 2–4 colours) and a fade mask on any layer; tiled images. Any layer can glow (it is drawn into the material's emission too).
- Editor placement: decals, text and panel lines take typed position, rotation, size and depth, and can be mirrored onto the other side of the gun.
- Editor: duplicate a layer, apply a layer to other parts in one step, import your own stickers and tile images into a library, and save and apply colour palettes (with swatches under each colour field).
- Holograms: brightness, a two-colour fade, scanlines and edge glow, shown the same in the editor and the game; new hex shield, reticle ring and tick arc shapes; a new hologram shape starts in the skin's own colour. A hologram picture now goes on the game's hologram shader where it reads it.
- The picker's skin pictures come from the pack: the editor draws a gear skin's picture (`picture.png`) when it packs the skin, as it already did a character's, and the game shows it without loading the skin or photographing anything (no more black pictures). The picture is the author's: picked colours show on your gear in the lobby. A pack exported by an older editor has no picture until it is exported again.
- A skinned gear's icon in the lobby's weapon list and the picker is framed as without the skin: a charm, screen or other attachment no longer shrinks the gun in it.
- An imported model keeps its own textures (base colour, normal, metallic and smoothness, glow), in the editor's 3D view and in the game.
- Texture size is set per material (`textureSize`, longest side 256–4096): the livery is baked, previewed, packed and shown in the game at that size, and the editor shows about how much video memory the material takes. The output size in the "Apply to game" menu is gone.
- Editor: the data folder can be moved in the settings (the files are copied and checked before the old folder is removed); the installed editor keeps it in `Documents\ForgeSkin` by default and moves it there on first start. New portable zip, which keeps its data next to the program and does not update itself.
- Editor: drop a picture on a part's texture slot to replace that one channel.
- Editor: a `settings.json` that cannot be read is reported and left untouched, instead of being treated as empty settings and written over.
- Player colours and adjust-page changes made just before the game closes are saved instead of lost.
- The picker says in plain words why a skin pack cannot be read (made for another version, file in use, file damaged); the error code goes to the log.
- Teammates see your skins: a teammate who has the same skin pack sees the skin, colours and adjustments you picked on your gear in your hands and on your sentry, and you see theirs; two players with the same gun each show their own. Their skin's shot, wield and down/revive sounds play at their gun. Gear whose skin you do not have (or have in another version) shows its original look, and the picker's details say whose it is and which Thunderstore mod to install. Bots keep the original look.
- A skin can have at most 8 player colours; a pack with more is refused (`too-many-player-colors`), and the editor stops at 8.

## 1.1.0

- A gun in your hands no longer turns magenta when a skin is applied again from the editor after a drop: the game's own first-person materials are kept while the skin covers them.
- A code effect on an attachment of the gun in your hands no longer draws a small second copy of it somewhere else in the world.
- The editor's 3D view draws imported charms and replaced part models again when a skin or project is opened: a missing preview is made again from the model file, and a model file that is gone is named.
- File paths inside a pack follow one rule shared with the other Forge mods: a name like `a..b.png` now loads, while a backslash, a drive, a Windows-reserved name (CON, NUL) or a name ending in a dot or space is refused, and the editor refuses it when it builds the pack.
- Packs made by this editor store material textures precompressed (DDS), so a skin goes on without a stall; ForgeSkin 1.0.3 cannot read these packs. Hand-made packs with PNG or JPEG textures still load.
- Skins load when a gear first wears them and are freed 30 s after nothing shows them, instead of all at game start.
- A material's skin also covers the moving parts that share it (bolt, charging handle, revolver drum, slide).
- What you leave in the world wears your skin too: the sentry and mines you place, your gun's dropped magazines, and pickups whose item has a skin.
- Charms: added from the editor's + menu, hanging point shown and draggable; the rope no longer jitters on uneven frame times or quick moves. Charms have inertia and real gravity: they lag behind the gun, swing out and back, and a heavier charm swings longer while a light one settles sooner.
- Code effects: animated patterns drawn live on a material or an attachment; a skin can let players adjust some of their values on the picker's Adjust page (kept in `ForgeSkin.colours.json`).
- The picker lists every skin installed for a gear and picks per gear, shows a preview of the focused skin, the author, and whether it can be adjusted; the slots show the game's own gear icons. Every skin card has its picture, a preview that could not be made says why, and colour choices show their colour.
- Gear icons the game draws (the GEAR menu) keep the skin; a skin with an ammo screen is no longer taken off them.
- The editor refuses, when it builds or installs a pack, a sound the game would refuse, naming it; "Apply to game" says in one message which step stopped and why, and the skin it applies goes before the player's pick in the in-game picker for that gear (the player can pick again afterwards). A pack that fails to load is not read again and again, and an applied skin kept off by the player's pick in the picker is noted in the log.
- An animated layer or GIF decal whose frames do not fit one 4096 animation image at full size is no longer refused: its frames are scaled down evenly to fit (the editor's log says to what size) and the game stretches them back over their area. A skin with such frames needs this version.
- The editor waits for the 3D view before baking a livery, names the layer and picture when a picture a skin uses is gone, keeps file references outside the project's own and the data folder absolute (a copied project still finds them), offers metallic, smoothness and tint only where the game material has them, and marks a code effect whose attached model is hidden. Its log panel merges repeated lines with a count and puts the game's warnings in plain words; a request error is listed once.
- A code effect on a hidden attachment is skipped quietly, and a gear whose `active.json` skin did not load keeps its own look without an error at every apply; a refused sound is named as in the editor.
- A part's glow in the game is as bright as the editor shows it (it was about a hundred times brighter and washed the gun out in white), and finishes and wear keep their gloss in the game (smoothness now goes where the game's gear shader reads it). A plain attachment without a picture takes its colour on the game's gear shader. Settings the game material has no place for stay out of the pack, the game notes them once in its log instead of warning on every apply, and "Apply to game" reports only the applied skin's problems. A pack built by an earlier editor keeps smoothness where the game does not read it, so its finishes look matte until it is built again in this editor. A game part's own glow also shows in the editor as bright as in the game, and the glow colour and strength controls start from it.
- The editor imports and exports the Skins mod's formats, and exports gun packs.
- New events for layers, attachment effects and sounds: Downed and Revived, for the item in your hands when you go down or your revive completes (the item held last, since going down stows it).
- Several sounds can share one event: each time one of them plays, picked at random by weight, never the same one twice in a row.
- Kill sounds can be set per enemy (any enemy of the game's data, a custom rundown's too); kills of an enemy without its own sounds play the plain kill sounds.
- Needs ForgeCore 1.0.1, which the mod manager installs with it. `ForgeSkin.Format.dll` and `NVorbis.dll` are no longer shipped; the pack reader is part of `ForgeSkin.Plugin.dll`.
- Skin sounds play through ForgeCore's audio, so the game's sound volume now applies to them.
- Burst guns get the dry-fire event like semi-automatic ones.
- Fixed the game closing when a skinned gun's skin was changed or reloaded, or while it fired: the game could free the skinned casing pool while it was still in use.
- Errors are written to `BepInEx/LogOutput.log` with a code each. The `forgeskin.log` the editor reads is written only with `Development` on.
- The skin picker is the game-styled loadout picker, opened in the lobby from the SKINS button or the picker key (rebindable in the game's Controls page), in English and Chinese; it no longer opens inside a level.
- Player colours: a skin's player colours can be recoloured in the picker; the choices are kept in `BepInEx/config/ForgeSkin.colours.json`.
- The skins folder and `forgeskin.log` are always in `BepInEx/plugins/ForgeSkin/`, also when a mod manager installed the plugin elsewhere.
- A gun whose model another mod mounts (EnergyLab) keeps that model and is not skinned.
- Settings, packs, `active.json`, `forgeskin.forge` markers, `SkinPacks.Register`, the picker choices and the wear counts are unchanged.

## 1.0.3

- Released on GitHub before the mod moved into the Forge repository.
