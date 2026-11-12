// What the skin plugin wrote in the test profile since the game started, refreshed while open.
import { Badge, Drawer, ScrollArea, Text } from '@mantine/core';
import { useEffect, useState } from 'react';
import { plainPluginLine, type PluginLine } from '../gameLog';
import { call } from '../protocol';
import { useStore } from '../store';
import { t } from '../i18n';

export function GameLog({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const profile = useStore((s) => s.prefs.profilePath);
  const [lines, setLines] = useState<PluginLine[] | null>(null);
  useEffect(() => {
    if (!opened || !profile) return;
    let live = true;
    const load = () =>
      call('plugin.log', { profile }).then(
        (r) => live && setLines(r.lines),
        () => live && setLines([]),
      );
    void load();
    const id = setInterval(load, 2000);
    return () => {
      live = false;
      clearInterval(id);
    };
  }, [opened, profile]);
  return (
    <Drawer opened={opened} onClose={onClose} position="bottom" size={320} title={t('gameLog.title')} withOverlay={false}>
      {lines?.length === 0 && <Text c="dimmed">{t('gameLog.empty')}</Text>}
      <ScrollArea h={230}>
        {[...(lines ?? [])].reverse().map((l, i) => (
          <div key={i} className={`log-line ${l.level}`}>
            <span className="time">{new Date(l.time).toLocaleTimeString()}</span>
            {l.level === 'warn' && (
              <Badge size="xs" color="yellow" variant="light">
                {t('gameLog.warn')}
              </Badge>
            )}
            <span>{l.level === 'warn' ? plainPluginLine(l.message) : l.message}</span>
          </div>
        ))}
      </ScrollArea>
    </Drawer>
  );
}
