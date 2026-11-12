// The right panel's categories: an inspector with many sections splits them into tabs, shown one at a time, picked from
// a vertical icon bar on the panel's right edge (the app renders the bar's host next to the scrolling inspector, so the
// bar stays put while the content scrolls). The last tab per inspector is kept for the next selection of its kind.
import { Tooltip, UnstyledButton } from '@mantine/core';
import type { Icon } from '@tabler/icons-react';
import { createContext, useContext, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

export interface InspectorTab {
  id: string;
  label: string;
  icon: Icon;
  color?: string;
  /** A number shown on the icon (how many things the tab holds); 0 or undefined shows none. */
  count?: number;
  content: ReactNode;
}

/** The element the icon bar is drawn into (App provides it). */
export const RailHost = createContext<HTMLElement | null>(null);

const KEY = 'sf.inspectorTab.';
const read = (id: string) => {
  try {
    return localStorage.getItem(KEY + id);
  } catch {
    return null;
  }
};

/** `side`: which edge of the window the bar is on (its tooltips open inwards). */
export function InspectorTabs({ id, tabs, side = 'right' }: { id: string; tabs: InspectorTab[]; side?: 'left' | 'right' }) {
  const host = useContext(RailHost);
  const [chosen, setChosen] = useState(() => read(id));
  const active = tabs.find((x) => x.id === chosen) ?? tabs[0];
  const pick = (tab: string) => {
    setChosen(tab);
    try {
      localStorage.setItem(KEY + id, tab);
    } catch {
      // a per-viewer convenience only
    }
  };
  return (
    <>
      {host &&
        createPortal(
          tabs.map((x) => (
            <Tooltip key={x.id} label={x.label} position={side === 'right' ? 'left' : 'right'} withArrow>
              <UnstyledButton
                className="rail-tab"
                aria-label={x.label}
                aria-selected={x.id === active.id}
                style={{ ['--accent' as string]: x.color ?? 'var(--flare)' }}
                onClick={() => pick(x.id)}
              >
                <x.icon size={19} stroke={1.6} />
                <span className="rail-label">{x.label}</span>
                {!!x.count && <span className="rail-count">{x.count}</span>}
              </UnstyledButton>
            </Tooltip>
          )),
          host,
        )}
      <div key={active.id}>{active.content}</div>
    </>
  );
}
