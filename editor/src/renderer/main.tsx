import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dropzone/styles.css';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { bakeGlyphs } from './frames';
import { useStore } from './store';
import { theme } from './theme';
import { viewRefs } from './viewport/refs';
import './styles.css';

// Dev only: lets the running app be driven from DevTools.
if (import.meta.env.DEV) Object.assign(window, { sf: useStore, sfView: viewRefs, sfBakeGlyphs: bakeGlyphs });

// Renderer errors go to GTFO_UserData/logs/app.log with their stack.
const stackOf = (e: unknown) => (e instanceof Error ? `${e.message}
${e.stack}` : String(e));
window.addEventListener('error', (e) => window.forge.appLog('renderer error', `${e.message} at ${e.filename}:${e.lineno}
${stackOf(e.error)}`));
window.addEventListener('unhandledrejection', (e) => window.forge.appLog('renderer unhandledrejection', stackOf(e.reason)));

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MantineProvider theme={theme} forceColorScheme="dark">
      <Notifications position="bottom-right" limit={4} />
      <App />
    </MantineProvider>
  </StrictMode>,
);
