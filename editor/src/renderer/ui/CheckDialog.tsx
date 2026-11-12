// 导出前体检: every skin of the current item through the build checks. A problem of the edited skin selects what it is
// about; one of another skin switches to that skin first.
import { Badge, Group, Loader, Modal, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { checkSkins, type Problem, type SkinCheck } from '../actions';
import { useStore } from '../store';
import { t } from '../i18n';

const close = () => useStore.setState({ checkOpen: false });

async function goTo(k: SkinCheck, p: Problem) {
  close();
  if (k.id !== useStore.getState().doc.id) await useStore.getState().switchSkin(k.id);
  const s = useStore.getState();
  if (p.select && s.doc.id === k.id) s.select(p.select);
}

export function CheckDialog() {
  const open = useStore((s) => s.checkOpen);
  const current = useStore((s) => s.doc.id);
  const [result, setResult] = useState<SkinCheck[] | null>(null);
  useEffect(() => {
    if (!open) return;
    setResult(null);
    checkSkins().then(setResult, (e) => (useStore.getState().fail(e), close()));
  }, [open]);
  return (
    <Modal opened={open} onClose={close} title={t('check.title')} centered size="lg">
      {!result ? (
        <Group justify="center" py="md">
          <Loader size="sm" />
        </Group>
      ) : (
        <Stack gap="md">
          {result.map((k) => (
            <div key={k.id}>
              <Group gap={8} mb={4}>
                <Text fw={600} size="sm">
                  {k.name}
                </Text>
                {k.id === current && (
                  <Badge size="xs" variant="light" color="gray">
                    {t('check.editing')}
                  </Badge>
                )}
                {!k.problems.length && (
                  <Badge size="xs" variant="light" color="green">
                    {t('check.ok')}
                  </Badge>
                )}
              </Group>
              <Stack gap={4}>
                {k.problems.map((p, i) => (
                  <UnstyledButton key={i} disabled={k.id === current && !p.select} onClick={() => void goTo(k, p)}>
                    <Group gap={6} wrap="nowrap" align="flex-start">
                      <IconAlertTriangle size={14} color="var(--mantine-color-yellow-5)" style={{ flex: 'none', marginTop: 3 }} />
                      <Text size="sm" style={{ wordBreak: 'break-all' }}>
                        {p.text}
                      </Text>
                    </Group>
                  </UnstyledButton>
                ))}
              </Stack>
            </div>
          ))}
          <Text size="xs" c="dimmed">
            {t('check.hint')}
          </Text>
        </Stack>
      )}
    </Modal>
  );
}
