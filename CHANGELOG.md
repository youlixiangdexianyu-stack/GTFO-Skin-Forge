# Changelog

## 1.0.0

First release on Thunderstore. Needs ForgeCore 1.0.1, which the mod manager installs with it. Skins are separate mods that depend on this one; install them from Thunderstore or make your own with the ForgeSkin editor.

- Weapon skins on your own gear: replaced textures and materials (precompressed in the pack, so a skin goes on without a stall), models mounted on the gun or replacing its parts, charms that swing on a rope or chain, live screens (ammo, kills, damage dealt and taken, accuracy, health, infection), animated layers, code effects drawn live, holograms, wear stages, and flashes and sounds on game events (kill, hit, miss, fire, reload, aim, low ammo, empty, going down, revived, ...; kill sounds per enemy).
- What you leave in the world wears your skin too: the sentry and mines you place, your gun's dropped magazines, and pickups whose item has a skin.
- Character skins: replace your whole character with an imported humanoid model. It moves with the game's own animations, the original character is hidden, and where you can be hit stays the original's. Hair, skirts and ribbons swing and rest against the body; in first person you see the model's own arms. Each player chooses how toon materials are drawn on their screen (`Toon` in `ForgeSkin.cfg`).
- The SKINS picker in the lobby (button or rebindable key, English and Chinese): gear and character slots, skin cards with the picture each pack carries, colour schemes and player colours, the adjust page for what a skin lets you change, and the settings behind the gear in its title bar.
- Teammates who have the same skin pack see the skin, colours and adjustments you picked, on your gear and your character, and you see theirs; gear whose skin they do not have shows its original look, and the picker says which Thunderstore mod to install. With ForgeReplay, replays show each player's skins.
- Skins come from skin mods in the plugins folder (a `forgeskin.forge` file lists their packs), from the skins folder `BepInEx/ForgeSkin/skins` the editor installs into (followed while the game runs), and from other plugins through `SkinPacks.Register`.
- Errors are written to `BepInEx/LogOutput.log` with a code each; `BepInEx/ForgeSkin/forgeskin.log` is what the editor reads after "Apply to game".
