// The decal image picker (stickers.pickDecalImage): the sticker library, or an image file.
import { Button, Modal, UnstyledButton } from '@mantine/core';
import { IconPhoto } from '@tabler/icons-react';
import { useStore } from '../store';
import { STICKERS, stickerFile, stickerUrl } from '../stickers';
import { t } from '../i18n';

export function StickerDialog() {
  const pick = useStore((s) => s.stickerPick);
  const done = (file: string | null) => {
    useStore.setState({ stickerPick: null });
    pick?.(file);
  };
  return (
    <Modal opened={!!pick} onClose={() => done(null)} title={t('stickers.title')} size="lg">
      <div className="sticker-grid">
        {Object.keys(STICKERS).map((id) => (
          <UnstyledButton key={id} className="sticker" title={id} onClick={async () => done(await stickerFile(id))}>
            <img src={stickerUrl(id)} alt={id} draggable={false} />
          </UnstyledButton>
        ))}
      </div>
      <Button mt="md" variant="default" leftSection={<IconPhoto size={14} />} onClick={async () => {
        const [f] = await window.forge.openDialog('files', [{ name: t('livery.decal'), extensions: ['png', 'jpg', 'jpeg', 'webp', 'bmp', 'gif'] }]);
        done(f ?? null);
      }}>
        {t('stickers.file')}
      </Button>
    </Modal>
  );
}
