import { Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';

export type NotifyKind = 'info' | 'ok' | 'error';
export interface NotifyAction {
  label: string;
  run: () => void;
}

const COLOR: Record<NotifyKind, string> = { info: 'cyan', ok: 'green', error: 'red' };

/** The toasts on screen, by kind and text: the same one asked for again while it shows is not stacked a second time. */
const open = new Set<string>();

/** Plain-language toast; every kind, errors included, closes after 5 s (hovering keeps it open). */
export function notify(kind: NotifyKind, text: string, action?: NotifyAction) {
  const key = `${kind}|${text}`;
  if (open.has(key)) return;
  open.add(key);
  const id = notifications.show({
    color: COLOR[kind],
    autoClose: 5000,
    withBorder: true,
    onClose: () => open.delete(key),
    message: (
      <Group gap="sm" wrap="nowrap" align="flex-start">
        <Text size="sm" style={{ flex: 1 }}>
          {text}
        </Text>
        {action && (
          <Button size="compact-xs" variant="light" onClick={() => (action.run(), notifications.hide(id))}>
            {action.label}
          </Button>
        )}
      </Group>
    ),
  });
}
