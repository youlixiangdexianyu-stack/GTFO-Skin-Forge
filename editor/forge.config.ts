import type { ForgeConfig } from '@electron-forge/shared-types';
import { MakerSquirrel } from '@electron-forge/maker-squirrel';
import { VitePlugin } from '@electron-forge/plugin-vite';
import { execFileSync } from 'node:child_process';
import { cpSync } from 'node:fs';
import { join } from 'node:path';

const backend = join(__dirname, '../src/ForgeSkin.Backend');
const publish = join(backend, 'bin/Release/net10.0/win-x64/publish');

const config: ForgeConfig = {
  packagerConfig: {
    asar: true,
    name: 'ForgeSkin',
    executableName: 'ForgeSkin',
    icon: 'assets/icon',
    // Backend single-file exe with the prebuilt plugin in publish/plugin (Install.PluginSource looks there first).
    // Font licences (SIL OFL) ship next to the app with the fonts they cover.
    extraResource: [publish, 'licenses'],
  },
  hooks: {
    // Every package carries a fresh self-contained backend and the committed plugin build (repo prebuilt/plugin/).
    prePackage: async () => {
      execFileSync('dotnet', ['publish', backend, '-c', 'Release', '--self-contained', '-p:PublishSingleFile=true'], { stdio: 'inherit' });
      cpSync(join(__dirname, '../prebuilt/plugin'), join(publish, 'plugin'), { recursive: true });
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
