# ForgeSkin for developers

A standalone mod: it depends on ForgeCore and BepInExPack_GTFO only. Game events that other packages also watch come from
ForgeCore `GameHooks` (gear parts spawned, gear spawn completed, enemy damage, reload start and end, dry fire, unwield,
flashlight, glue fire, throw, resource pack, game state); only methods no other package patches are ForgeSkin's own Harmony
patches in `Native/Hooks.cs` (fire animation, melee attack, mine and sentry placed, item used, equip, aim, the spawn-complete
overrides of melee, mine, sentry and scanner, item layer, first-person rendering). Each patch class installs on its own and
skips a target whose function start ForgeCore refuses (`Hooks.Unsupported`); one that fails logs `skin-hook-unavailable` and
the rest keep working. The frame tick is ForgeCore `Frame.OnUpdate`; a pack's sounds are one ForgeAudio library
(`AudioKit.Load` from the pack's Ogg bytes, at most 10 s a clip); kept textures and meshes go through `Assets.Keep`; the
settings, picker choices, player colours, wear counts and readback files go through `Settings` and `Store.WriteText`.

What ForgeSkin draws goes through ForgeCore's visual and UI parts: a pack's PNG or JPEG through `ImageTextures.Decode`
(normal maps in the game's DXT5nm layout), a pack's SFM1 mesh through `MeshArrays.Build` (the reader of the format stays in
`Core/`), every emission write through `Glow` (`Write`, `SetColour`, `Clear`), gear icons through `GearIcons.Redraw`, and
the picker through `UIKit` (`PickerSource`, a lobby button, a key action listed in the game's Controls page). Texts are in
`lang/en.forge` and `lang/zh.forge` beside the DLL. What stays here is ForgeSkin's own: the pack format, the skin targets,
live screens, animated layers, trigger effects, casing pools and the part layout.

## Picker and player colours

The picker opens in the lobby's loadout pop-up, from the SKINS lobby button or the picker key (`PickerKey` of
`ForgeSkin.cfg` is its default; the Controls page rebinds it). One slot per gear or item that has a loaded skin; the SKINS
tab picks one of its skins or the original look (kept in `BepInEx/config/ForgeSkin.choices.json`), the COLOURS tab sets the
player colours (`playerColors`) of the skin it wears: per colour the skin's own value, the author's palette, or, with no
palette, twelve hues at the default's saturation and brightness. Colours are kept per skin ID in
`BepInEx/config/ForgeSkin.colours.json`; a pick paints a copy of the skin document (`Skin.Painted`) and live gear takes it on
the next tick, as a skin pick does. The picker does not open inside a level.

## Other packages on the same gun

- A gun whose model a package mounts with ForgeCore `GunMount` (EnergyLab's guns, Workshop gun models) is not skinned:
  `GunMount.Of(holder)` names it, and `GunMount.Mounting` puts a skinned gun back to its own look before the mount reads
  its poses and hides its parts. Part mounts (`GunPartMount`) are not excluded; a skin and a mounted part share the gun.
- Gear icons: ForgeSkin redraws only the icons of gear whose skin or colours changed (every gear when a global skin did); a
  package that supplies its own icons (`GearIcons.Supply`) draws them again itself.
- Ejected casings: ForgeSkin appends its own pools after the game's in `WeaponShellManager.SetupPools` and points a skinned
  gun's casing type at its pool while it ejects; ForgePerformance's casing limit counts and drops those casings like the
  game's, before any casing is taken from a pool.
- ForgeSkin installs no patch on a method another package patches; shared game events come from `GameHooks`.

## Build

```
dotnet build Mods/ForgeSkin/ForgeSkin.csproj -c Release --artifacts-path artifacts/dotnet -p:GTFOBepInExPath=<profile>/BepInEx
python Mods/ForgeSkin/tools/package_release.py --artifacts-path artifacts/dotnet --output <folder>
```

The pack reader (`Core/`) is compiled into `ForgeSkin.Plugin.dll`; the zip holds that one DLL, `lang/` and the metadata.

## Adding packs

- Put `.forgeskin` packs in `BepInEx/plugins/ForgeSkin/skins`, wherever the DLL is installed (a mod manager puts it in
  `plugins/NAinfini-ForgeSkin/`); the editor installs there, and `active.json` there picks the skin per target.
- Or ship a mod folder directly under `BepInEx/plugins` with a `forgeskin.forge` marker (`SkinPacks.Marker`) listing its packs,
  one path per line relative to that folder; blank lines and `#` comments are skipped.
- Or call `ForgeSkin.Plugin.SkinPacks.Register(path)` from your plugin's `Load`.

A pack the reader refuses is not loaded and logs `skin-pack-rejected` with the reason code of `SkinFormatException`
(`bad-audio`, `bad-image`, `bad-mesh`, `missing-file` and the others).

## Logs

Errors go to `BepInEx/LogOutput.log` through ForgeCore `Log` under their registered codes. With `Development` on, the plugin
also writes `BepInEx/plugins/ForgeSkin/forgeskin.log` (`<UTC time> info|warn <message>` per line, cleared at launch), which the editor reads
as its game feed; with it off no file is written and info lines are dropped.

## Editor release

One release is the plugin's Thunderstore zip and the editor's Squirrel installer, both at the version of the ForgeSkin row
of `Mods/Release/release.json` (`pnpm ts Tools/Forge/set-package-version.ts ForgeSkin <x.y.z>` writes it everywhere,
`Editor/package.json` included). Build it from the repository root with a BepInEx IL2CPP profile for the game references:

```powershell
pwsh Mods/ForgeSkin/tools/build-editor-release.ps1 -BepInEx "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-Multiplayer\BepInEx" [-Output <dir>] [-SkipMake]
```

The script, all under `<Output>` (default `artifacts/forge-skin-release`):

1. builds `ForgeSkin.csproj` in Release (ForgeCore builds with it) into `dotnet/`;
2. writes the ForgeCore and ForgeSkin Thunderstore zips into `thunderstore/` with each package's `tools/package_release.py`
   and checks them with `Mods/Build/verify_release.py`;
3. publishes the backend (`Backend/ForgeSkin.Backend.csproj`, .NET 10, self-contained single file) into `publish/`;
4. stages the plugin the installer carries: `publish/plugin/ForgeSkin/` (`ForgeSkin.Plugin.dll` and `lang/`) and `publish/plugin/ForgeCore/`
   (the ForgeCore zip unpacked);
5. runs `pnpm install --frozen-lockfile` and `pnpm make` in `Editor/` with `FORGESKIN_PUBLISH_DIR` set to `publish/`, which
   `forge.config.ts` ships as the app's `resources/publish` (the prePackage hook refuses to package without it);
   Squirrel writes `ForgeSkin-<version> Setup.exe`, `ForgeSkin-<version>-full.nupkg` and `RELEASES` into
   `Editor/out/make/squirrel.windows/x64/`;
6. copies the files to upload into `upload/` and lists their SHA-256 in `upload/upload.sha256`.

`-SkipMake` stops after step 4. The installer is not code-signed. Nothing is uploaded; the upload steps and the fixed update
feed (`ForgeSkin-update` release of NAinfini/GTFO-Forge-Mods) are in `Mods/Release/README.md`.

In a checkout the backend installs the plugin from `artifacts/forge-skin-release/publish/plugin` (`Install.PluginSource`),
so run the script, at least with `-SkipMake`, after changing the plugin. Installing into a profile deletes the
`ForgeSkin.Format.dll` and `NVorbis.dll` that 1.0.3 and earlier left in `BepInEx/plugins/ForgeSkin/`. When the mod manager
installed the plugin (a `ForgeSkin.Plugin.dll` elsewhere under `BepInEx/plugins`), the editor keeps that copy and deletes
its own `ForgeSkin.Plugin.dll` and `lang/` from `plugins/ForgeSkin/`, so one copy loads; a managed copy older than the
shipped one makes the install fail with `forge-skin-old`. Without a managed copy it copies `ForgeSkin.Plugin.dll` and
`lang/` to `plugins/ForgeSkin/`. ForgeCore is left to the mod manager: a profile that has `ForgeCore.dll` anywhere under
`BepInEx/plugins` keeps it, and one older than the shipped copy makes the install fail with `forge-core-old`; a profile
without ForgeCore gets the shipped package in `BepInEx/plugins/NAinfini-ForgeCore/`. Uninstalling removes only the
editor's own plugin copy and its `lang/`; the skins folder and a managed copy stay.
