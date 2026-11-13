// The file dialog of an editor started with --ai-control: the main process asks for a path here instead of opening the
// system's dialog (main.ts pickPaths). Open and folder asks check that the path exists, save asks that its folder does;
// a refused path is explained here and the dialog stays open.
import { Button, Group, Modal, Text, Textarea, TextInput } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import type { PathRequest } from '../../preload/preload';
import { t } from '../i18n';

const TITLE = { folder: 'pathDialog.folder', files: 'pathDialog.files', project: 'pathDialog.file', save: 'pathDialog.save' } as const;

/** One typed path per line; the quotes Explorer's "Copy as path" adds are dropped. */
const parse = (text: string) =>
  text
    .split(/\r?\n/)
    .map((l) => l.trim().replace(/^"(.*)"$/, '$1'))
    .filter(Boolean);

function Ask({ req, done }: { req: PathRequest; done: () => void }) {
  const [text, setText] = useState(req.start);
  const [error, setError] = useState('');
  const input = useRef<HTMLInputElement & HTMLTextAreaElement>(null);
  // The menu that started the request hands focus back to its button as the dialog opens; take it again.
  useEffect(() => {
    const id = setTimeout(() => input.current?.focus(), 150);
    return () => clearTimeout(id);
  }, []);
  const many = req.kind === 'files';
  const types = req.filters.flatMap((f) => f.extensions).filter((e) => e !== '*');
  const answer = async (paths: string[] | null) => {
    const why = await window.forge.answerPath(req.id, paths);
    if (why) setError(t(`pathDialog.${why}` as 'pathDialog.empty'));
    else done();
  };
  const ok = () => void answer(parse(text));
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter' || e.shiftKey) return;
    e.preventDefault();
    ok();
  };
  const label = t(many ? 'pathDialog.paths' : 'pathDialog.path');
  return (
    <Modal opened onClose={() => void answer(null)} title={t(TITLE[req.kind])} centered size="lg" zIndex={500} trapFocus>
      {many ? (
        <Textarea ref={input} label={label} data-autofocus value={text} onChange={(e) => setText(e.currentTarget.value)} onKeyDown={onKeyDown} autosize minRows={3} error={error || undefined} />
      ) : (
        <TextInput ref={input} label={label} data-autofocus value={text} onChange={(e) => setText(e.currentTarget.value)} onKeyDown={onKeyDown} error={error || undefined} />
      )}
      {!!types.length && (
        <Text size="xs" c="dimmed" mt={6}>
          {t('pathDialog.types', { v0: types.join(', ') })}
        </Text>
      )}
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={() => void answer(null)}>
          {t('skins.cancel')}
        </Button>
        <Button onClick={ok}>{t('pathDialog.ok')}</Button>
      </Group>
    </Modal>
  );
}

export function PathDialog() {
  const [queue, setQueue] = useState<PathRequest[]>([]);
  useEffect(() => window.forge.onPathRequest((r) => setQueue((q) => [...q, r])), []);
  const req = queue[0];
  return req ? <Ask key={req.id} req={req} done={() => setQueue((q) => q.slice(1))} /> : null;
}
