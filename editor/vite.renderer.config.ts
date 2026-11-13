import { defineConfig, searchForWorkspaceRoot } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
// The code-effect preview compiles ForgeCore's own drawing programs and catalogue (Mods/ForgeCore/Visual), outside this app's folder.
const forgeVisual = fileURLToPath(new URL('../../ForgeCore/Visual', import.meta.url));
export default defineConfig({ plugins: [react()], server: { fs: { allow: [searchForWorkspaceRoot(process.cwd()), forgeVisual] } } });
