# ForgeSkin

Applies ForgeSkin weapon skins to your own gear in GTFO: replaced textures and materials, models mounted on the gun,
live screens (ammo, kills, damage dealt and taken, accuracy, health, infection — as digits or a bar), animated layers,
flashes and sounds played on game events (kill, hit, miss, fire, reload, aim, low ammo, empty, going down, revived, ...;
several sounds on one event play one at random, and kills can have their own sounds per enemy), and a custom part
layout. Skins can also hang charms on a cord or chain that sway with gravity and swing as you move (on the gun in your
own hands).

You normally don't install this by hand: skin mods made with the ForgeSkin editor list it as a dependency, so your mod
manager installs it with them.

Skins are local: other players see the skins they installed themselves. You see skins on your own gear only: in your
hands, on a sentry or mine you placed, on the magazines your gun drops, and on pickups lying in the level whose item has a
skin (they keep it when you pick them up). Other players' and bots' guns keep their original look.

In the lobby, the SKINS button opens the skin picker: choose a
skin for each gear or the original look (a gear with several skins installed lists them all, and the skin in focus shows a
preview of the gear wearing it), and recolour the skin's player colours from the author's suggestions or a row of
hues. Your choices are kept in `BepInEx/config/ForgeSkin.choices.json` and `ForgeSkin.colours.json`. Skin packs go in
`BepInEx/plugins/ForgeSkin/skins`.

## Settings

`BepInEx/config/ForgeSkin.cfg`

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | true | Turn off to apply no skins |
| `Development` | false | Reload skins whenever the skins folder changes, and write `BepInEx/plugins/ForgeSkin/forgeskin.log` for the editor (for skin authors testing from the editor) |
| `Readback` | false | Write each gear's vanilla part poses to `BepInEx/ForgeSkin/readback/` for troubleshooting |
| `HologramShader` | true | Draw holograms with the game's hologram shader; off uses a plain glow |

Errors go to `BepInEx/LogOutput.log`, each with a code.
