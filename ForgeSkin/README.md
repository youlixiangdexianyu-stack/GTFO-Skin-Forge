# ForgeSkin

Applies ForgeSkin weapon skins to your own gear in GTFO: replaced textures and materials, models mounted on the gun,
live screens (ammo, kills, damage dealt and taken, accuracy, health, infection — as digits or a bar), animated layers,
flashes and sounds played on game events (kill, hit, miss, fire, reload, aim, low ammo, empty, ...), and a custom part
layout.

You normally don't install this by hand: skin mods made with the ForgeSkin editor list it as a dependency, so your mod
manager installs it with them.

Skins are local: other players see the skins they installed themselves.

In the lobby, the SKINS button (or `F8`, which the game's Controls settings can change) opens the skin picker: choose a
skin for each gear or the original look, and recolour the skin's player colours from the author's suggestions or a row of
hues. Your choices are kept in `BepInEx/config/ForgeSkin.choices.json` and `ForgeSkin.colours.json`. Skin packs go in
`BepInEx/plugins/ForgeSkin/skins`.

## Settings

`BepInEx/config/ForgeSkin.cfg`

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | true | Turn off to apply no skins |
| `Development` | false | Reload skins whenever the skins folder changes, and write `BepInEx/plugins/ForgeSkin/forgeskin.log` for the editor (for skin authors testing from the editor) |
| `Readback` | false | Write each gear's vanilla part poses to `BepInEx/ForgeSkin/readback/` for troubleshooting |
| `PickerKey` | F8 | The default key that opens and closes the skin picker in the lobby; the game's Controls settings can change it |
| `HologramShader` | true | Draw holograms with the game's hologram shader; off uses a plain glow |

Errors go to `BepInEx/LogOutput.log`, each with a code.
