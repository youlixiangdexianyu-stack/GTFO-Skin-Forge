import { Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';

export type NotifyKind = 'info' | 'ok' | 'error';
export interface NotifyAction {
  label: string;
  run: () => void;
}

const COLOR: Record<NotifyKind, string> = { info: 'cyan', ok: 'green', error: 'red' };

/** Plain-language toast; errors stay until closed. */
export function notify(kind: NotifyKind, text: string, action?: NotifyAction) {
  const id = notifications.show({
    color: COLOR[kind],
    autoClose: kind === 'error' ? false : 4500,
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
