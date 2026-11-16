# ForgeSkin

Applies ForgeSkin weapon skins to your own gear in GTFO: replaced textures and materials, models mounted on the gun,
live screens (ammo, kills, damage dealt and taken, accuracy, health, infection — as digits or a bar), animated layers,
flashes and sounds played on game events (kill, hit, miss, fire, reload, aim, low ammo, empty, going down, revived, ...;
several sounds on one event play one at random, and kills can have their own sounds per enemy), and a custom part
layout. Skins can also hang charms on a cord or chain that sway with gravity and swing as you move (on the gun in your
own hands).

Character skins replace your whole character with an imported humanoid model. It moves with the game's own animations
(walking, running, crouching, jumping, holding the gun, going down, being revived), its hair, skirt and ribbons swing as you
move, and in first person you see the model's own arms; where you can be hit stays the original character's. The lobby
figure wears it too.

You normally don't install this by hand: skin mods made with the ForgeSkin editor list it as a dependency, so your mod
manager installs it with them.

Please also update ForgeCore to 1.0.2 (请同时更新 ForgeCore 到 1.0.2).

Your skins show on your gear: in your hands, on a sentry or mine you placed, on the magazines your gun drops, and on
pickups lying in the level whose item has a skin (they keep it when you pick them up). Teammates who have the same skin mod
installed see the character, skins, colours and adjustments you picked, and you see theirs; their skin's shot,
wield and down/revive sounds play at their gun. A teammate's gear whose skin you don't have (or have in another version)
keeps its original look, and the skin picker says which mod to install. Bots' guns and teammates' mines keep their
original look.

In the lobby, the SKINS button opens the skin picker: choose a character and a
skin for each gear, or the original look (a gear with several skins installed lists them all, each with the preview picture
its author exported with it; a pack from an older editor shows none until it is exported again), and recolour the skin's
player colours with one of the author's colour schemes or a row of hues. Your choices are kept in `BepInEx/config/ForgeSkin.choices.json` and `ForgeSkin.colours.json`. Skin packs go in
`BepInEx/ForgeSkin/skins`. Skins installed by the editor of ForgeSkin 1.1.0 or earlier are still in
`BepInEx/plugins/ForgeSkin/skins`, which the game no longer reads: open the editor once and it moves them over (the game's log
says so while skins are left there).

## Settings

`BepInEx/config/ForgeSkin.cfg`. `Enabled`, `HologramShader` and `Toon` are also behind the gear in the skin picker's title bar
(Show skins, Game hologram effect, toon rendering); a change there applies at once and is saved to this file.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | true | Turn off to apply no skins (your picks and colours are kept) |
| `Development` | false | For skin mod authors: also reload skins whenever a skin mod in the plugins folder changes, and write every detail to `BepInEx/ForgeSkin/forgeskin.log` (the skins folder is always followed) |
| `Readback` | false | Write each gear's vanilla part poses to `BepInEx/ForgeSkin/readback/` for troubleshooting |
| `HologramShader` | true | Draw holograms with the game's hologram shader; off uses a plain glow |
| `Toon` | Deferred | How a character's toon materials are drawn on your screen only: `Deferred` lit by the game and seen by the thermal sight, `Forward` a cleaner toon look the thermal sight does not see, `Off` like every other material |

Errors go to `BepInEx/LogOutput.log`, each with a code.
