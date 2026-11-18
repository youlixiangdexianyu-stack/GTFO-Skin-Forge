// 检查: every skin of the current item through the build checks, and the game's own assembly compared with the preview. A problem of
// the edited skin selects what it is about; one of another skin switches to that skin first.
import { Badge, Button, Group, Loader, Modal, Stack, Table, Text, UnstyledButton } from '@mantine/core';
import { IconAlertTriangle, IconChecklist } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { checkSkins, type Problem, type SkinCheck } from '../actions';
import { detail, friendly } from '../errors';
import { call, type BackendError, type VerifyResult } from '../protocol';
import { useStore } from '../store';
import { localName, t } from '../i18n';

const close = () => useStore.setState({ checkOpen: false });

async function goTo(k: SkinCheck, p: Problem) {
  close();
  if (k.id !== useStore.getState().doc.id) await useStore.getState().switchSkin(k.id);
  const s = useStore.getState();
  if (p.select && s.doc.id === k.id) s.select(p.select);
}

/** Compares the game's own weapon assembly (read back by the plugin) with this app's preview, per weapon. */
function VerifyModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const profile = useStore((s) => s.profiles.find((p) => p.path === s.prefs.profilePath));
  const catalog = useStore((s) => s.catalog);
  const [rows, setRows] = useState<VerifyResult[] | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  useEffect(() => {
    if (!opened || !profile) return;
    setRows(null);
    setProblem(null);
    call('assembly.verify', { profile: profile.path }).then(setRows, (e: BackendError) => {
      useStore.getState().log(`assembly.verify ${detail(e)}`);
      setProblem(e?.code === 'not-found' ? t('topBar.k33') : friendly(e));
    });
  }, [opened, profile?.path]); // eslint-disable-line react-hooks/exhaustive-deps
  const name = (r: VerifyResult) => {
    const w = catalog.find((c) => c.id === r.weapon);
    return w ? localName(w) : t('topBar.k34', { v0: r.gearId });
  };
  const why = (r: VerifyResult) => {
    if (r.pass) return t('topBar.k35', { v0: r.compared });
    const w = r.worst[0];
    if (!w) return t('topBar.k36');
    if (w.problem) return t('topBar.k37');
    return t('topBar.k38', { v0: w.positionMm.toFixed(1), v1: w.rotationDeg.toFixed(1) });
  };
  return (
    <Modal opened={opened} onClose={onClose} title={t('topBar.k39', { v0: profile?.name ?? t('topBar.noProfile') })} size="lg">
      {problem && <Text size="sm">{problem}</Text>}
      {!problem && rows === null && <Text c="dimmed">{t('topBar.k40')}</Text>}
      {rows?.length === 0 && <Text c="dimmed">{t('topBar.k41')}</Text>}
      {!!rows?.length && (
        <Table verticalSpacing="xs">
          <Table.Tbody>
            {rows.map((r) => (
              <Table.Tr key={r.file}>
                <Table.Td w={88}>
                  <Badge color={r.pass ? 'teal' : 'red'} variant="light" style={{ overflow: 'visible' }} miw={64}>
                    {r.pass ? t('topBar.k42') : t('topBar.k43')}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{name(r)}</Text>
                  <Text size="xs" c="dimmed">
                    {why(r)}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Modal>
  );
}

export function CheckDialog() {
  const open = useStore((s) => s.checkOpen);
  const current = useStore((s) => s.doc.id);
  const [result, setResult] = useState<SkinCheck[] | null>(null);
  const [verify, setVerify] = useState(false);
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
          <Group justify="flex-end">
            <Button variant="default" size="xs" leftSection={<IconChecklist size={14} />} onClick={() => setVerify(true)}>
              {t('topBar.k48')}
            </Button>
          </Group>
        </Stack>
      )}
      <VerifyModal opened={verify} onClose={() => setVerify(false)} />
    </Modal>
  );
}
