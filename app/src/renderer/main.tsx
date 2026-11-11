import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/dropzone/styles.css';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { useStore } from './store';
import { theme } from './theme';
import { viewRefs } from './viewport/refs';
import './styles.css';

// Dev only: lets the running app be driven from DevTools.
if (import.meta.env.DEV) Object.assign(window, { sf: useStore, sfView: viewRefs });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MantineProvider theme={theme} forceColorScheme="dark">
      <Notifications position="bottom-right" limit={4} />
      <App />
    </MantineProvider>
  </StrictMode>,
);
