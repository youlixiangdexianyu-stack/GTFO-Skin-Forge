// The skin's colour variants in its overview: pick the one the 3D view shows and colour edits change, add, rename, delete.
import { ActionIcon, Button, Group, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import { useStore } from '../store';
import { activeVariant, addVariant, removeVariant, renameVariant, selectVariant } from '../variants';
import { Info } from './Features';
import { t } from '../i18n';

export function Variants() {
  const variants = useStore((s) => s.doc.variants);
  const active = useStore((s) => activeVariant(s.doc));
  const list = (variants ?? []).filter((v) => !!v);
  return (
    <div className="section">
      <Group gap={6} mb={6}>
        <h3 style={{ margin: 0 }}>{t('variants.title')}</h3>
        <Info label={t('variants.info')} />
      </Group>
      <div className="variant-list">
        <UnstyledButton className="variant" aria-selected={!active} onClick={() => selectVariant(null)}>
          {t('variants.own')}
        </UnstyledButton>
        {list.map((v) => (
          <UnstyledButton key={v!.id} className="variant" aria-selected={active?.id === v!.id} onClick={() => selectVariant(v!.id ?? null)}>
            {v!.name}
          </UnstyledButton>
        ))}
        <Button size="compact-xs" variant="subtle" leftSection={<IconPlus size={12} />} onClick={addVariant}>
          {t('variants.add')}
        </Button>
      </div>
      {active && (
        <Group gap={6} mt={8} wrap="nowrap">
          <TextInput style={{ flex: 1 }} size="xs" aria-label={t('variants.rename')} value={active.name ?? ''} onChange={(e) => renameVariant(active.id ?? '', e.currentTarget.value)} />
          <Tooltip label={t('variants.remove')}>
            <ActionIcon variant="default" color="red" onClick={() => removeVariant(active.id ?? '')}>
              <IconTrash size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>
      )}
    </div>
  );
}
