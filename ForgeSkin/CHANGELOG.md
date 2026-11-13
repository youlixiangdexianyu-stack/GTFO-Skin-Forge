# Changelog

## Unreleased

- Several sounds can share one event: each firing plays one of them, picked at random by weight (a new Weight slider in the sound inspector shows the chance).
- New events for layers, attachment effects and sounds: Downed and Revived — the local player going down and their revive completing. They belong to the item in their hands (the one held last, when going down stows it).
- Needs ForgeCore, which the mod manager installs with it. `ForgeSkin.Format.dll` and `NVorbis.dll` are no longer shipped; the pack reader is part of `ForgeSkin.Plugin.dll`.
- Skin sounds play through ForgeCore's audio, so the game's sound volume now applies to them.
- Burst guns get the dry-fire event like semi-automatic ones.
- Errors are written to `BepInEx/LogOutput.log` with a code each. The `forgeskin.log` the editor reads is written only with `Development` on.
- The skin picker is the game-styled loadout picker, opened in the lobby from the SKINS button or the picker key (rebindable in the game's Controls page), in English and Chinese; it no longer opens inside a level.
- Player colours: a skin's player colours can be recoloured in the picker; the choices are kept in `BepInEx/config/ForgeSkin.colours.json`.
- The skins folder and `forgeskin.log` are always in `BepInEx/plugins/ForgeSkin/`, also when a mod manager installed the plugin elsewhere.
- A gun whose model another mod mounts (EnergyLab) keeps that model and is not skinned.
- Settings, packs, `active.json`, `forgeskin.forge` markers, `SkinPacks.Register`, the picker choices and the wear counts are unchanged.

## 1.0.3

- Released on Thunderstore before the mod moved into the Forge repository.
