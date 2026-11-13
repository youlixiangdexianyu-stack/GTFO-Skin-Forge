// The current item's skins as thumbnail cards in the left panel's skins tab: switch, make, copy, rename, delete, and pick the
// one the game uses. Each card shows the weapon wearing that skin.
import { ActionIcon, Button, Group, Menu, Modal, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconCopy, IconDeviceFloppy, IconDots, IconEraser, IconFileImport, IconFolderOpen, IconHistory, IconPencil, IconPlus, IconTrash } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { activateSkin, importSkinGroup, openProject } from '../actions';
import type { ProjectEntry } from '../protocol';
import { useStore } from '../store';
import { skinThumbnail, skinThumbOf, thumbOf } from '../viewport/thumbnails';
import { useThumbs } from './WeaponGallery';
import { SnapshotsModal } from './Snapshots';
import { t } from '../i18n';

/** Re-renders the edited skin's thumbnail a moment after the last change; the others once each. */
function useSkinThumbs() {
  const weaponId = useStore((s) => s.weaponId);
  const skins = useStore((s) => s.skins);
  const doc = useStore((s) => s.doc);
  const ready = useStore((s) => !!s.preview && !s.previewLoading);
  useEffect(() => {
    if (!weaponId || !ready) return;
    for (const k of skins) if (k.id !== doc.id) void skinThumbnail(weaponId, k.id, { path: k.path });
  }, [weaponId, skins, ready]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!weaponId || !ready || !doc.id) return;
    const id = setTimeout(() => void skinThumbnail(weaponId, doc.id!, { doc, live: true }), 1500);
    return () => clearTimeout(id);
  }, [weaponId, doc, ready]);
}

function SkinCard({ k, onDelete, onSnapshots }: { k: ProjectEntry; onDelete: (k: ProjectEntry) => void; onSnapshots: () => void }) {
  const current = useStore((s) => s.doc.id === k.id);
  const name = useStore((s) => (s.doc.id === k.id ? s.doc.name : k.name)) ?? '';
  const weaponId = useStore((s) => s.weaponId);
  const pack = useStore((s) => s.installed.find((p) => p.id === k.id));
  const [renaming, setRenaming] = useState(false);
  const { switchSkin, newSkin, saveProject, newProject, edit } = useStore.getState();
  const rename = (v: string) => {
    setRenaming(false);
    if (v.trim() && v.trim() !== name) edit((d) => void (d.name = v.trim()));
  };
  const thumb = skinThumbOf(k.id) ?? (weaponId ? thumbOf(weaponId) : null);
  return (
    <div className="skin-card" aria-current={current} onClick={() => !current && void switchSkin(k.id)}>
      <div className="thumb">{thumb && <img src={thumb} alt="" />}</div>
      {pack?.active ? (
        <span className="in-game">{t('skins.inGameNow')}</span>
      ) : (
        pack && (
          <Tooltip label={t('skins.idle')} multiline w={220}>
            <UnstyledButton className="in-game idle" onClick={(e) => (e.stopPropagation(), void activateSkin(k.id, name))}>
              {t('skins.inGame')}
            </UnstyledButton>
          </Tooltip>
        )
      )}
      <div className="row">
        {renaming ? (
          <TextInput
            size="xs"
            autoFocus
            defaultValue={name}
            onClick={(e) => e.stopPropagation()}
            onBlur={(e) => rename(e.currentTarget.value)}
            onKeyDown={(e) => (e.key === 'Enter' ? rename(e.currentTarget.value) : e.key === 'Escape' && setRenaming(false))}
          />
        ) : (
          <span className="name" title={name} onDoubleClick={() => current && setRenaming(true)}>
            {name}
          </span>
        )}
        <Menu position="bottom-end" withinPortal>
          <Menu.Target>
            <ActionIcon variant="subtle" color="gray" size="sm" aria-label={t('skins.more')} onClick={(e) => e.stopPropagation()}>
              <IconDots size={14} />
            </ActionIcon>
          </Menu.Target>
          <Menu.Dropdown onClick={(e) => e.stopPropagation()}>
            {current && (
              <>
                <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => setRenaming(true)}>
                  {t('skins.rename')}
                </Menu.Item>
                <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => void newSkin(true)}>
                  {t('skins.copy')}
                </Menu.Item>
                <Menu.Item leftSection={<IconDeviceFloppy size={14} />} rightSection={<Text size="xs" c="dimmed">Ctrl S</Text>} onClick={() => void saveProject()}>
                  {t('skins.saveFile')}
                </Menu.Item>
                <Menu.Item leftSection={<IconHistory size={14} />} onClick={onSnapshots}>
                  {t('snapshots.menu')}
                </Menu.Item>
                <Menu.Item leftSection={<IconEraser size={14} />} onClick={() => void newProject()}>
                  {t('topBar.k57')}
                </Menu.Item>
                <Menu.Divider />
              </>
            )}
            <Menu.Item color="red" leftSection={<IconTrash size={14} />} onClick={() => onDelete(k)}>
              {t('skins.delete')}
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </div>
    </div>
  );
}

export function SkinStrip() {
  const weaponId = useStore((s) => s.weaponId);
  const skins = useStore((s) => s.skins);
  const currentName = useStore((s) => s.doc.name);
  const current = useStore((s) => s.doc.id);
  const profilePath = useStore((s) => s.prefs.profilePath);
  const [deleting, setDeleting] = useState<ProjectEntry | null>(null);
  const [snapshots, setSnapshots] = useState(false);
  useThumbs();
  useSkinThumbs();
  useEffect(() => void useStore.getState().refreshInstalled(), [profilePath, weaponId]);
  if (!weaponId) return null;
  const { newSkin, deleteSkin } = useStore.getState();
  return (
    <div className="skin-strip">
      <div className="parts-head">
        <span>{t('skins.title')} · {skins.length}</span>
      </div>
      {skins.map((k) => (
        <SkinCard key={k.id} k={k} onDelete={setDeleting} onSnapshots={() => setSnapshots(true)} />
      ))}
      <SnapshotsModal opened={snapshots} onClose={() => setSnapshots(false)} />
      <Menu position="bottom-start" withinPortal>
        <Menu.Target>
          <UnstyledButton className="skin-card add" aria-label={t('skins.add')}>
            <IconPlus size={18} />
            <span>{t('skins.add')}</span>
          </UnstyledButton>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item leftSection={<IconPlus size={14} />} onClick={() => void newSkin(false)}>
            {t('skins.new')}
          </Menu.Item>
          <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => void newSkin(true)}>
            {t('skins.copy')}
          </Menu.Item>
          <Menu.Item leftSection={<IconFolderOpen size={14} />} rightSection={<Text size="xs" c="dimmed">Ctrl O</Text>} onClick={() => void openProject()}>
            {t('topBar.k54')}
          </Menu.Item>
          <Menu.Item leftSection={<IconFileImport size={14} />} onClick={() => void importSkinGroup()}>
            {t('skins.importGroup')}
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
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
