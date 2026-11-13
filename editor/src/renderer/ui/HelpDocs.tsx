// The tutorial: how to make a skin, as pages with a contents list. The pages are docs/zh.md and docs/en.md, bundled
// with the editor; each `# Title {#id}` starts a page and `[text](#id)` links to one. The language follows the editor's.
import { ActionIcon, Button, Modal, NavLink, ScrollArea, Text, TextInput, Tooltip } from '@mantine/core';
import { IconBook, IconHelp, IconSearch } from '@tabler/icons-react';
import { marked } from 'marked';
import { useMemo, useRef, useState } from 'react';
import { create } from 'zustand';
import en from '../docs/en.md?raw';
import zh from '../docs/zh.md?raw';
import { t, useLang, type Lang } from '../i18n';
import { useStore } from '../store';
import './help.css';

interface Page {
  id: string;
  title: string;
  html: string;
  text: string;
}

function pages(md: string): Page[] {
  return md
    .split(/^# /m)
    .slice(1)
    .map((chunk) => {
      const [head, ...body] = chunk.split('\n');
      const [, title, id] = head.match(/^(.*?)\s*\{#([\w-]+)\}\s*$/) ?? [, head.trim(), head.trim()];
      const src = body.join('\n');
      return { id: id!, title: title!, html: marked.parse(src, { async: false }), text: src.toLowerCase() };
    });
}

const PAGES: Record<Lang, Page[]> = { zh: pages(zh), en: pages(en) };

const useHelp = create<{ page: string | null }>(() => ({ page: null }));
/** Opens the tutorial, at `page` or the first one. */
export const openHelp = (page?: string) => useHelp.setState({ page: page ?? PAGES.zh[0].id });

export function HelpButton() {
  return (
    <Tooltip label={t('help.title')}>
      <ActionIcon size={30} variant="subtle" color="gray" aria-label={t('help.title')} onClick={() => openHelp()}>
        <IconBook size={19} />
      </ActionIcon>
    </Tooltip>
  );
}

export function HelpDocs() {
  const lang = useLang();
  const page = useHelp((s) => s.page);
  const [q, setQ] = useState('');
  const body = useRef<HTMLDivElement>(null);
  const all = PAGES[lang];
  const needle = q.trim().toLowerCase();
  const list = useMemo(() => (needle ? all.filter((p) => p.title.toLowerCase().includes(needle) || p.text.includes(needle)) : all), [all, needle]);
  const current = all.find((p) => p.id === page) ?? all[0];
  const show = (id: string) => {
    useHelp.setState({ page: id });
    body.current?.scrollTo({ top: 0 });
  };
  // Links inside a page go to another page.
  const onClick = (e: React.MouseEvent) => {
    const a = (e.target as HTMLElement).closest('a');
    const href = a?.getAttribute('href');
    if (!href) return;
    e.preventDefault();
    if (href.startsWith('#')) show(href.slice(1));
  };
  return (
    <Modal opened={!!page} onClose={() => useHelp.setState({ page: null })} title={t('help.title')} size="min(1100px, 92vw)" centered padding={0} classNames={{ header: 'help-header' }}>
      <div className="help">
        <nav className="help-nav">
          <TextInput leftSection={<IconSearch size={13} />} placeholder={t('help.search')} value={q} onChange={(e) => setQ(e.currentTarget.value)} mb={8} />
          <ScrollArea className="help-list">
            {list.map((p) => (
              <NavLink key={p.id} label={p.title} active={p.id === current.id} color="flare" variant="light" onClick={() => show(p.id)} />
            ))}
            {!list.length && (
              <Text size="xs" c="dimmed" p="xs">
                {t('help.none')}
              </Text>
            )}
          </ScrollArea>
          <Button
            variant="light"
            color="cyan"
            leftSection={<IconHelp size={14} />}
            onClick={() => (useHelp.setState({ page: null }), useStore.getState().setPrefs({ welcomed: false }))}
          >
            {t('welcome.reopen')}
          </Button>
        </nav>
        <div className="help-body" ref={body}>
          <h1>{current.title}</h1>
          <div className="help-page" onClick={onClick} dangerouslySetInnerHTML={{ __html: current.html }} />
        </div>
      </div>
    </Modal>
  );
}
