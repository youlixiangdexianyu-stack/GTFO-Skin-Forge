import { defineConfig, searchForWorkspaceRoot } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
// The code-effect preview compiles ForgeCore's own drawing programs and catalogue (Mods/ForgeCore/Visual), outside this app's folder.
const forgeVisual = fileURLToPath(new URL('../../ForgeCore/Visual', import.meta.url));
// The 人物模型 tab matches bones with the site's own matcher and canonical rig and converts models with the site's own converter
// (Web/src/forge, Web/src/generated/rig) and plays moves with the site's pose copy (Web/src/shared/render); the converter's libraries
// and three are this app's own declared copies (dedupe; tsconfig maps three's types the same way).
const site = fileURLToPath(new URL('../../Web/src', import.meta.url));
// The dev server starts at 5190 (FORGESKIN_DEV_PORT overrides it), leaving 5173 to other programs; a taken port moves on to the next free one.
const port = Number(process.env.FORGESKIN_DEV_PORT) || 5190;
export default defineConfig({
  plugins: [react()],
  resolve: { dedupe: ['@gltf-transform/core', '@gltf-transform/extensions', 'fast-png', 'three'] },
  worker: { format: 'es' },
  // A development run keeps its data in GTFO_UserData inside this folder (main/dataDir.ts). The dev server does not watch it: its
  // watch holds every folder there open, so a work's folders could not be moved to the Recycle Bin (project.delete, work-in-use).
  server: { port, fs: { allow: [searchForWorkspaceRoot(process.cwd()), forgeVisual, site] }, watch: { ignored: ['**/GTFO_UserData/**'] } },
});
