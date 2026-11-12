import { Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';

export type NotifyKind = 'info' | 'ok' | 'error';
export interface NotifyAction {
  label: string;
  run: () => void;
}

const COLOR: Record<NotifyKind, string> = { info: 'cyan', ok: 'green', error: 'red' };

/** Plain-language toast; every kind, errors included, closes after 5 s (hovering keeps it open). */
export function notify(kind: NotifyKind, text: string, action?: NotifyAction) {
  const id = notifications.show({
    color: COLOR[kind],
    autoClose: 5000,
    withBorder: true,
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
