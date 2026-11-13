# Changelog

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

- Released on Thunderstore before the mod moved into the Forge repository.
