# ForgeSkin

Applies ForgeSkin weapon skins to your own gear in GTFO: replaced textures and materials, models mounted on the gun,
live screens (ammo, kills, damage dealt and taken, accuracy, health, infection — as digits or a bar), animated layers,
flashes and sounds played on game events (kill, hit, miss, fire, reload, aim, low ammo, empty, ...), and a custom part
layout.

You normally don't install this by hand: skin mods made with the ForgeSkin editor list it as a dependency, so your mod
manager installs it with them.

Skins are local: other players see the skins they installed themselves.

## Settings

`BepInEx/config/ForgeSkin.cfg`

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | true | Turn off to apply no skins |
| `Development` | false | Reload skins whenever the skins folder changes (for skin authors testing from the editor) |
| `Readback` | false | Write each gear's vanilla part poses to `BepInEx/ForgeSkin/readback/` for troubleshooting |

Logs go to `BepInEx/LogOutput.log` with the prefix `[ForgeSkin]`.
