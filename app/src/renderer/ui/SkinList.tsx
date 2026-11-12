import { ActionIcon, Badge, Button, Group, Modal, Text, TextInput, Tooltip } from '@mantine/core';
import { IconCopy, IconDeviceGamepad2, IconPencil, IconPlus, IconTrash } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { activateSkin } from '../actions';
import type { ProjectEntry } from '../protocol';
import { useStore } from '../store';
import { t } from '../i18n';

/** The current item's skins: switch which one is edited, make, rename, delete, and pick the one the game uses. */
export function SkinList() {
  const weaponId = useStore((s) => s.weaponId);
  const skins = useStore((s) => s.skins);
  const current = useStore((s) => s.doc.id);
  const currentName = useStore((s) => s.doc.name);
  const installed = useStore((s) => s.installed);
  const profilePath = useStore((s) => s.prefs.profilePath);
  const [renaming, setRenaming] = useState(false);
  const [deleting, setDeleting] = useState<ProjectEntry | null>(null);
  useEffect(() => void useStore.getState().refreshInstalled(), [profilePath, weaponId]);
  if (!weaponId) return null;
  const { switchSkin, newSkin, deleteSkin, edit } = useStore.getState();
  const rename = (name: string) => {
    setRenaming(false);
    if (name.trim() && name.trim() !== currentName) edit((d) => void (d.name = name.trim()));
  };
  return (
    <div className="skins">
      <div className="parts-head">
        <span style={{ flex: 1 }}>
          {t('skins.title')} · {skins.length}
        </span>
        <Tooltip label={t('skins.new')}>
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => void newSkin(false)} aria-label={t('skins.new')}>
            <IconPlus size={14} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('skins.copy')}>
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={() => void newSkin(true)} aria-label={t('skins.copy')}>
            <IconCopy size={14} />
          </ActionIcon>
        </Tooltip>
      </div>
      <div className="list">
        {skins.map((k) => {
          const mine = k.id === current;
          const name = mine ? currentName : k.name;
          const pack = installed.find((p) => p.id === k.id);
          return (
            <div key={k.id} className="part skin" aria-selected={mine} onClick={() => void switchSkin(k.id)}>
              <Tooltip label={pack?.active ? t('skins.inGame') : pack ? t('skins.idle') : t('skins.notInstalled')} multiline w={240}>
                <ActionIcon
                  variant={pack?.active ? 'light' : 'subtle'}
                  color={pack?.active ? 'green' : 'gray'}
                  size="sm"
                  data-disabled={!pack || pack.active || undefined}
                  onClick={(e) => (e.stopPropagation(), pack && !pack.active && void activateSkin(k.id, name ?? ''))}
                  aria-label={t('skins.inGame')}
                >
                  <IconDeviceGamepad2 size={14} />
                </ActionIcon>
              </Tooltip>
              {mine && renaming ? (
                <TextInput
                  size="xs"
                  autoFocus
                  defaultValue={name}
                  onClick={(e) => e.stopPropagation()}
                  onBlur={(e) => rename(e.currentTarget.value)}
                  onKeyDown={(e) => (e.key === 'Enter' ? rename(e.currentTarget.value) : e.key === 'Escape' && setRenaming(false))}
                />
              ) : (
                <span className="label" title={name} onDoubleClick={() => mine && setRenaming(true)}>
                  {name}
                </span>
              )}
              {pack?.active ? (
                <Badge size="xs" color="green" variant="light">
                  {t('skins.inGame')}
                </Badge>
              ) : (
                <span />
              )}
              <span className="row-actions">
                {mine && (
                  <Tooltip label={t('skins.rename')}>
                    <ActionIcon variant="subtle" color="gray" size="sm" onClick={(e) => (e.stopPropagation(), setRenaming(true))} aria-label={t('skins.rename')}>
                      <IconPencil size={13} />
                    </ActionIcon>
                  </Tooltip>
                )}
                <Tooltip label={t('skins.delete')}>
                  <ActionIcon variant="subtle" color="red" size="sm" onClick={(e) => (e.stopPropagation(), setDeleting(k))} aria-label={t('skins.delete')}>
                    <IconTrash size={13} />
                  </ActionIcon>
                </Tooltip>
              </span>
            </div>
          );
        })}
      </div>
      <Modal opened={!!deleting} onClose={() => setDeleting(null)} title={t('skins.deleteTitle')} centered>
        <Text size="sm">{t('skins.deleteBody', { v0: deleting?.id === current ? currentName : deleting?.name })}</Text>
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setDeleting(null)}>
            {t('skins.cancel')}
          </Button>
          <Button color="red" onClick={() => (void deleteSkin(deleting!.id), setDeleting(null))}>
            {t('skins.delete')}
          </Button>
        </Group>
      </Modal>
    </div>
  );
}
