# GTFO Forge Mods: ForgeSkin

Source code of NAinfini's ForgeSkin (BepInEx 6 IL2CPP plugin and desktop skin editor) and of ForgeCore, the shared library it builds against. This repository uses the same folder layout as [NAinfini/GTFO-Forge-Mods](https://github.com/NAinfini/GTFO-Forge-Mods).

| Package | What it is | Thunderstore |
|---|---|---|
| [ForgeCore](ForgeCore/README.md) | The one shared library: asset bundles, logging, settings, pools, networking, input leases and local saves, plus its interface (lobby buttons, pickers, menu pages, HUD), audio (Ogg Vorbis cues, positioned playback) and world-visuals (effects, markers, model loading) parts. | [NAinfini/ForgeCore](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeCore/) |
| [ForgeSkin](ForgeSkin/README.md) | Applies ForgeSkin packs (textures, attachments, live ammo screens, animated layers) to your own weapons; includes the desktop skin editor. | [NAinfini/ForgeSkin](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeSkin/) |

## Layout

- One folder per package: the plugin source, tests, build tools, README, CHANGELOG and, in each folder's DEVELOPERS.md, how to build against and extend it. Sibling packages reference each other as ../<Package>, so keep the folders side by side.
- Build/ and Directory.Build.props: the shared MSBuild settings (GTFO-API and BepInEx references, warnings as errors) and the release-check scripts.
- Testing/: a test double used by the ForgeCore tests.
- Tools/Vfx/ForgeVfx/: the Unity 2019.4.21f1 project that generates Forge Visual's own effect textures, materials and prefabs from code.
- Large binary assets (model and audio bundles, Unity sources) are not stored here; the Thunderstore packages contain the finished files.

- Web/: the site code the skin editor imports (exactly the files it reaches), at the same relative paths as in the main repository.
- ForgeCore's sight meshes are local media and are not in the repository: build with `-p:ForgeModsRoot=<a Mods folder that holds the media, trailing slash>`.

Each package's licence is in its own folder.

