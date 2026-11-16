// The picture picker (stickers.pickDecalImage, pickTileImage): for a decal the built-in stickers and the author's imported
// ones, for a tile layer the imported tile pictures; either can import more into the library or take any image file.
import { ActionIcon, Button, Group, Modal, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconFileImport, IconPhoto, IconX } from '@tabler/icons-react';
import { useEffect } from 'react';
import { useStore } from '../store';
import { STICKERS, stickerFile, stickerUrl, usePickMode } from '../stickers';
import { IMAGE_TYPES, importImages, loadLibrary, removeImage, useLibrary } from '../library';
import { fileUrl } from '../protocol';
import { t } from '../i18n';

export function StickerDialog() {
  const pick = useStore((s) => s.stickerPick);
  const mode = usePickMode((m) => m.mode);
  const own = useLibrary((l) => l[mode]);
  useEffect(() => void (pick && loadLibrary()), [pick]);
  const done = (file: string | null) => {
    useStore.setState({ stickerPick: null });
    pick?.(file);
  };
  return (
    <Modal opened={!!pick} onClose={() => done(null)} title={t(mode === 'tiles' ? 'library.tileTitle' : 'stickers.title')} size="lg">
      {mode === 'stickers' && (
        <div className="sticker-grid">
          {Object.keys(STICKERS).map((id) => (
            <UnstyledButton key={id} className="sticker" title={id} onClick={async () => done(await stickerFile(id))}>
              <img src={stickerUrl(id)} alt={id} draggable={false} />
            </UnstyledButton>
          ))}
        </div>
      )}
      <Group justify="space-between" mt="md" mb={6}>
        <Text size="sm" fw={600}>
          {t(mode === 'tiles' ? 'library.ownTiles' : 'library.ownStickers')}
        </Text>
        <Button size="compact-sm" variant="light" color="grape" leftSection={<IconFileImport size={14} />} onClick={() => void importImages(mode)}>
          {t('library.import')}
        </Button>
      </Group>
      {own.length ? (
        <div className="sticker-grid">
          {own.map((x) => (
            <div key={x.id} className="sticker own">
              <UnstyledButton title={x.name} onClick={() => done(x.file)}>
                <img src={fileUrl(x.file)} alt={x.name} draggable={false} />
              </UnstyledButton>
              <Tooltip label={t('library.remove')}>
                <ActionIcon className="remove" size="xs" variant="filled" color="dark" aria-label={t('library.remove')} onClick={() => removeImage(mode, x.id)}>
                  <IconX size={11} />
                </ActionIcon>
              </Tooltip>
            </div>
          ))}
        </div>
      ) : (
        <p className="hint">{t(mode === 'tiles' ? 'library.noTiles' : 'library.noStickers')}</p>
      )}
      <Button
        mt="md"
        variant="default"
        leftSection={<IconPhoto size={14} />}
        onClick={async () => {
          const [f] = await window.forge.openDialog('file', [{ name: t('library.images'), extensions: IMAGE_TYPES }]);
          done(f ?? null);
        }}
      >
        {t('stickers.file')}
      </Button>
    </Modal>
  );
}
