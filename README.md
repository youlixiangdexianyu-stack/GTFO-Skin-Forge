# ForgeSkin

Weapon skins for GTFO: a desktop editor to make skins, and a BepInEx plugin that puts them on your own gear in game.

Players only need the plugin and a skin mod (a mod manager installs the plugin and its dependency ForgeCore for you).
Skin authors use the editor to build textures, painted finishes, mounted models, live screens, animated layers and sounds,
test them in game, and export a skin mod for Thunderstore.

## Two places, one source

This repository and the `ForgeSkin/` folder of [NAinfini/GTFO-Forge-Mods](https://github.com/NAinfini/GTFO-Forge-Mods) hold the
same source and are updated together; releases and the editor's updates come from the GTFO-Forge-Mods releases.
Pull requests and issues are welcome here: changes made here are carried into the other place, and the other way round.

## Layout

```
ForgeSkin.sln
editor/                       the editor (Electron, React, TypeScript, three.js)
src/ForgeSkin.Format/         the skin pack format, compiled into both the plugin and the backend
src/ForgeSkin.Backend/        the editor's backend (.NET 10): reads game assets, builds packs, installs
src/ForgeSkin.Plugin/         the game plugin (net6.0, BepInEx 6 IL2CPP); thunderstore/ holds its package files
tests/ForgeSkin.Tests/        tests
tools/stage-plugin.ps1        builds the plugin and stages it where the editor installs it from
```

## Build

You need Windows, the .NET 10 SDK, Node.js with pnpm, GTFO, and a mod manager profile that has BepInExPack_GTFO and
NAinfini-ForgeCore installed (the plugin builds against that profile's game assemblies and `ForgeCore.dll`).

```powershell
dotnet build ForgeSkin.sln -c Release -p:GTFOBepInExPath="<profile>\BepInEx"
cd editor; pnpm install; pnpm start
pwsh tools/stage-plugin.ps1 -BepInEx "<profile>\BepInEx"
dotnet test tests/ForgeSkin.Tests
```

`pnpm start` runs the editor from source. Run `tools/stage-plugin.ps1` once first so "Apply to game" has a plugin to install.
Installers are built and published from the GTFO-Forge-Mods release process, not from here.
