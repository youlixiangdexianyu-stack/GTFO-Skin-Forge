// Replacing one node's own model with an imported .glb (the node keeps its material, children and layout).
import { Button, Group, Text } from '@mantine/core';
import { IconArrowBackUp, IconReplace } from '@tabler/icons-react';
import { baseName, call } from '../protocol';
import { partTransformOf, useStore } from '../store';
import { ownMeshes, partTarget, useSceneVersion } from '../viewport/refs';
import { Info } from './Features';
import { t } from '../i18n';

/** Whether a node has a model of its own that can be replaced. */
export function hasOwnModel(part: string, path?: string | null): boolean {
  const node = partTarget(part, path);
  return !!node && ownMeshes(node).length > 0;
}

/** Picks a .glb and puts it in place of the node's own model. */
export async function replacePartModel(part: string, path?: string | null): Promise<void> {
  const s = useStore.getState();
  const [glb] = await window.forge.openDialog('files', [{ name: t('actions.k24'), extensions: ['glb', 'gltf'] }]);
  if (!glb) return;
  useStore.setState({ busy: t('actions.k25') });
  try {
    const r = await call('mesh.import', { glb });
    s.setPartMesh(part, path, r);
    s.toast('ok', t('partModel.done'));
  } catch (e) {
    s.fail(e);
  } finally {
    useStore.setState({ busy: null });
  }
}

export function PartModel({ part }: { part: string }) {
  const path = useStore((s) => (s.selection?.kind === 'part' ? (s.selection.sub ?? null) : null));
  const mesh = useStore((s) => partTransformOf(s.doc, part, path)?.mesh);
  useSceneVersion();
  if (!mesh && !hasOwnModel(part, path)) return null;
  return (
    <div className="section">
      <h3>
        <span>{t('partModel.title')}</span>
        <Info label={t('partModel.info')} />
      </h3>
      <Group gap={6} wrap="nowrap">
        <Text size="xs" c={mesh ? undefined : 'dimmed'} truncate style={{ flex: 1 }}>
          {mesh ? t('partModel.replaced', { v0: baseName(mesh) }) : t('partModel.original')}
        </Text>
        <Button size="xs" variant="light" leftSection={<IconReplace size={14} />} onClick={() => void replacePartModel(part, path)}>
          {t('partModel.replace')}
        </Button>
        {mesh && (
          <Button size="xs" variant="subtle" color="gray" leftSection={<IconArrowBackUp size={14} />} onClick={() => useStore.getState().setPartMesh(part, path, null)}>
            {t('partModel.restore')}
          </Button>
        )}
      </Group>
    </div>
  );
}
