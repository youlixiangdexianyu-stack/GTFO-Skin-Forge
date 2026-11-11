import type { ForgeConfig } from '@electron-forge/shared-types';
import { MakerSquirrel } from '@electron-forge/maker-squirrel';
import { MakerZIP } from '@electron-forge/maker-zip';
import { VitePlugin } from '@electron-forge/plugin-vite';

const config: ForgeConfig = {
  packagerConfig: {
    asar: true,
    name: 'GTFO Skin Forge',
    executableName: 'GTFOSkinForge',
    // Backend single-file exe and plugin DLLs; filled by PLAN §6 item 4 packaging.
    // Font licences (SIL OFL) ship next to the app with the fonts they cover.
    extraResource: ['../src/SkinForge.Backend/bin/Release/net10.0/win-x64/publish', 'licenses'],
  },
  makers: [new MakerSquirrel({ name: 'GTFOSkinForge' }), new MakerZIP({}, ['win32'])],
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
