import type { ForgeConfig } from '@electron-forge/shared-types';
import { MakerSquirrel } from '@electron-forge/maker-squirrel';
import { VitePlugin } from '@electron-forge/plugin-vite';
import { existsSync } from 'node:fs';
import { basename, join } from 'node:path';

// The folder Mods/ForgeSkin/tools/build-editor-release.ps1 stages: the self-contained backend exe and plugin/ForgeSkin plus
// plugin/ForgeCore (the plugin and the ForgeCore package it depends on; Install.PluginSource reads them). It ships as
// resources/publish, where the app starts the backend.
const publish = process.env.FORGESKIN_PUBLISH_DIR ?? '';

const config: ForgeConfig = {
  packagerConfig: {
    asar: true,
    name: 'ForgeSkin',
    executableName: 'ForgeSkin',
    icon: 'assets/icon',
    // Font licences (SIL OFL) ship next to the app with the fonts they cover.
    extraResource: [publish, 'licenses'],
  },
  hooks: {
    prePackage: async () => {
      const staged = ['ForgeSkin.Backend.exe', 'plugin/ForgeSkin/ForgeSkin.Plugin.dll', 'plugin/ForgeCore/ForgeCore.dll'];
      if (basename(publish) !== 'publish' || staged.some((file) => !existsSync(join(publish, file))))
        throw new Error('Run Mods/ForgeSkin/tools/build-editor-release.ps1: it stages the backend and the plugin and sets FORGESKIN_PUBLISH_DIR.');
    },
  },
  makers: [new MakerSquirrel({ name: 'ForgeSkin', authors: 'NAinfini', description: 'GTFO weapon skin editor', setupIcon: 'assets/icon.ico' })],
  plugins: [
    new VitePlugin({
      build: [
        { entry: 'src/main/main.ts', config: 'vite.main.config.ts', target: 'main' },
        { entry: 'src/preload/preload.ts', config: 'vite.preload.config.ts', target: 'preload' },
      ],
      renderer: [{ name: 'main_window', config: 'vite.renderer.config.ts' }],
    }),
  ],
};

export default config;
