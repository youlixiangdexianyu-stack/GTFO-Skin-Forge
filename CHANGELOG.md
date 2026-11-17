# Changelog

## 1.0.1

Needs ForgeCore 1.0.2. In multiplayer the host and every client should update ForgeCore: a client's kills come from the host's ForgeCore.

- New: character models that are not a whole human. A creature without fingers, with one arm or leg, or with unlike sides now works as a character skin: only its hips need a bone. A part without a bone keeps its shape and moves with the part above it, a hand whose arm is incomplete does not reach for the gun, and a model without both upper arms shows the game's own arms in first person. A pack made this way needs ForgeSkin 1.0.1 or later.
- New: sight reticles. A gun skin can replace the reticle in a holographic sight's lens, up to three picture layers, each with its own colour, strength, size, depth and on or off. Only you see it, when you aim down the sight. A skin with a reticle needs ForgeSkin 1.0.1 or later.
- New: inspect your weapon. Press the inspect key (I by default; change it under Forge mods on the game's key settings page) with a gun in your hands, skinned or not, to turn it over and look at it, then bring it back. Each kind of gun has its own motion: pistols, revolvers and compact guns are turned over in the hand, and a revolver spins twice before it goes back; rifles, shotguns, machine guns and snipers are turned with both hands. Firing, aiming, reloading, melee, using, sprinting, opening a menu or being hit ends it, and nothing you press is held back. Teammates see your wrist turn the gun. Known issue: rifles, shotguns, machine guns and snipers are held badly while their right side is shown; this is fixed in a later version.
- Fixed: on a client, kill and damage screens, kill sounds, Kill and Hit flashes and wear with kills counted nothing. They now show in the frame of your hit and are corrected when the host's result arrives (a kill that did not happen is taken off the screen); wear with kills follows the host's result only.
- Fixed: on a client, Miss flashes and the accuracy screen judged most hits as misses; they now follow your own shots, the same as on the host.
- Fixed: a teammate's character skin turned flat pink once the level started.
- Fixed: after joining a lobby that others were already in, everyone in it, you included, stood in their original look until someone changed a skin. Character skins now go on as soon as each figure appears.
- Fixed: hologram rings on your gun (such as Critical Line's) were drawn in the middle of the screen while you danced or did another action seen from behind.
- Two skin packs with the same skin: the newer file is used, and the log says where both are.

## 1.0.0

First release on Thunderstore. Needs ForgeCore 1.0.1, which the mod manager installs with it. Skins are separate mods that depend on this one; install them from Thunderstore or make your own with the ForgeSkin editor.

- Weapon skins on your own gear: replaced textures and materials (precompressed in the pack, so a skin goes on without a stall), models mounted on the gun or replacing its parts, charms that swing on a rope or chain, live screens (ammo, kills, damage dealt and taken, accuracy, health, infection), animated layers, code effects drawn live, holograms, wear stages, and flashes and sounds on game events (kill, hit, miss, fire, reload, aim, low ammo, empty, going down, revived, ...; kill sounds per enemy).
- What you leave in the world wears your skin too: the sentry and mines you place, your gun's dropped magazines, and pickups whose item has a skin.
- Character skins: replace your whole character with an imported humanoid model. It moves with the game's own animations, the original character is hidden, and where you can be hit stays the original's. Hair, skirts and ribbons swing and rest against the body; in first person you see the model's own arms. Each player chooses how toon materials are drawn on their screen (`Toon` in `ForgeSkin.cfg`).
- The SKINS picker in the lobby (button or rebindable key, English and Chinese): gear and character slots, skin cards with the picture each pack carries, colour schemes and player colours, the adjust page for what a skin lets you change, and the settings behind the gear in its title bar.
- Teammates who have the same skin pack see the skin, colours and adjustments you picked, on your gear and your character, and you see theirs; gear whose skin they do not have shows its original look, and the picker says which Thunderstore mod to install. With ForgeReplay, replays show each player's skins.
- Skins come from skin mods in the plugins folder (a `forgeskin.forge` file lists their packs), from the skins folder `BepInEx/ForgeSkin/skins` the editor installs into (followed while the game runs), and from other plugins through `SkinPacks.Register`.
- Errors are written to `BepInEx/LogOutput.log` with a code each; `BepInEx/ForgeSkin/forgeskin.log` is what the editor reads after "Apply to game".
