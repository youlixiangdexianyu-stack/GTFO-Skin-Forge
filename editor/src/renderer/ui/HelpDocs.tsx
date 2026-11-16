// The tutorial: how to make a skin, as pages with a searchable contents list. The pages are docs/zh.md and docs/en.md,
// bundled with the editor; the language follows the editor's. The format is plain Markdown with a few conventions:
//   #! Group             a heading in the contents list for the pages after it
//   # Title {#id}        starts a page; [text](#id) links to it
//   ## Heading           a section of the page, listed under "On this page" and found by the search
//   ![caption](shot:x)   a picture from docs/images (<lang>/x.webp, else common/x.webp), with its caption; click to zoom
//   > [!TIP] / [!NOTE] / [!WARNING]   a callout box
//   [text](editor:livery)   closes the tutorial and opens that tab of the right panel (any tab id of InspectorTabs)
//   [text](editor:character-bones)   closes the tutorial and opens that step of the 人物模型 side (any step of character/state)
//   <!-- ... -->         a note for the authors, not shown (a place kept for a part still to be written)
// The pictures are made by scripts/docs-shots.mjs from the running editor and are not kept in Git (a missing one shows as
// a placeholder with its caption).
import { ActionIcon, Badge, Button, Group, Menu, Modal, NavLink, ScrollArea, Text, TextInput, Tooltip } from '@mantine/core';
import { IconArrowLeft, IconArrowRight, IconBook, IconHelp, IconSchool, IconSearch, IconX } from '@tabler/icons-react';
import { CHAPTERS, type Chapter } from '../tour/steps';
import { Marked, type Tokens } from 'marked';
import { useEffect, useMemo, useRef, useState } from 'react';
import { create } from 'zustand';
import en from '../docs/en.md?raw';
import zh from '../docs/zh.md?raw';
import { t, useLang, type Lang } from '../i18n';
import { useStore } from '../store';
import { STEPS as CHARACTER_STEPS, useCharacter } from '../character/state';
import './help.css';

const IMAGES = import.meta.glob<string>('../docs/images/**/*.webp', { eager: true, query: '?url', import: 'default' });
const image = (lang: Lang, name: string) => IMAGES[`../docs/images/${lang}/${name}.webp`] ?? IMAGES[`../docs/images/common/${name}.webp`];

interface Section {
  id: string;
  title: string;
  text: string;
}
interface Page {
  id: string;
  title: string;
  group: string;
  html: string;
  text: string;
  sections: Section[];
}

const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const CALLOUT = { TIP: 'tip', NOTE: 'note', WARNING: 'warning' } as const;

function render(lang: Lang, md: string): string {
  let n = 0;
  const marked = new Marked({
    renderer: {
      heading({ tokens, depth }: Tokens.Heading) {
        const inner = this.parser.parseInline(tokens);
        return depth === 2 ? `<h2 id="s-${n++}">${inner}</h2>` : `<h${depth}>${inner}</h${depth}>`;
      },
      image({ href, text }: Tokens.Image) {
        const name = href.startsWith('shot:') ? href.slice(5) : null;
        const src = name ? image(lang, name) : href;
        const body = src ? `<img src="${esc(src)}" alt="${esc(text)}" loading="lazy" data-zoom>` : `<div class="help-missing">${esc(name ?? href)}</div>`;
        return `<figure class="help-fig">${body}<figcaption>${esc(text)}</figcaption></figure>`;
      },
      blockquote({ tokens }: Tokens.Blockquote) {
        const html = this.parser.parse(tokens);
        const m = html.match(/^<p>\[!(TIP|NOTE|WARNING)\]\s*/);
        if (!m) return `<blockquote>${html}</blockquote>`;
        const kind = CALLOUT[m[1] as keyof typeof CALLOUT];
        return `<div class="help-callout ${kind}"><b>${esc(t(`help.${kind}`))}</b><p>${html.slice(m[0].length)}</div>`;
      },
    },
  });
  return marked.parse(md, { async: false });
}

function pages(lang: Lang, md: string): Page[] {
  const out: Page[] = [];
  let group = '';
  let page: { head: string; lines: string[] } | null = null;
  const flush = () => {
    if (!page) return;
    const [, title, id] = page.head.match(/^(.*?)\s*\{#([\w-]+)\}\s*$/) ?? [, page.head.trim(), page.head.trim()];
    const src = page.lines.join('\n').replace(/<!--[\s\S]*?-->\n?/g, '');
    const sections = src
      .split(/^## /m)
      .slice(1)
      .map((chunk, i) => {
        const [head, ...body] = chunk.split('\n');
        return { id: `s-${i}`, title: head.trim(), text: body.join('\n').toLowerCase() };
      });
    out.push({ id: id!, title: title!, group, html: render(lang, src), text: src.toLowerCase(), sections });
    page = null;
  };
  for (const line of md.split(/\r?\n/)) {
    if (line.startsWith('#! ')) {
      flush();
      group = line.slice(3).trim();
    } else if (line.startsWith('# ')) {
      flush();
      page = { head: line.slice(2), lines: [] };
    } else page?.lines.push(line);
  }
  flush();
  return out;
}

const PAGES: Record<Lang, Page[]> = { zh: pages('zh', zh), en: pages('en', en) };

const useHelp = create<{ page: string | null; anchor: string | null }>(() => ({ page: null, anchor: null }));
/** Opens the tutorial, at `page` or the first one. */
export const openHelp = (page?: string) => useHelp.setState({ page: page ?? PAGES.zh[0].id, anchor: null });

/** The help menu: the written tutorial, and each guided tutorial on its own. */
export function HelpButton() {
  return (
    <Menu position="bottom-end" width={240}>
      <Menu.Target>
        <Tooltip label={t('help.title')}>
          <ActionIcon size={30} variant="subtle" color="gray" aria-label={t('help.title')} data-tour="help">
            <IconBook size={19} />
          </ActionIcon>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconBook size={14} />} onClick={() => openHelp()}>
          {t('help.docs')}
        </Menu.Item>
        <Menu.Divider />
        <Menu.Label>{t('help.tours')}</Menu.Label>
        {(Object.keys(CHAPTERS) as Chapter[]).map((c) => (
          <Menu.Item key={c} leftSection={<IconSchool size={14} />} onClick={() => useStore.setState({ tour: c })}>
            {t(`tour.chapter.${c}`)}
          </Menu.Item>
        ))}
      </Menu.Dropdown>
    </Menu>
  );
}

/** `editor:<tab>`: shows that tab of the right panel on the 武器 side; a part's tab selects the first part when no part is
 * selected. `editor:character-<step>`: shows that step of the 人物模型 side. */
const TABS = ['texture', 'livery', 'layers', 'attach', 'place', 'sounds', 'look', 'screen', 'effects', 'overview'] as const;
function openEditorTab(name: string) {
  const step = CHARACTER_STEPS.find((x) => `character-${x}` === name);
  if (step) {
    useHelp.setState({ page: null });
    return useCharacter.setState({ mode: 'character', step });
  }
  const tab = TABS.find((x) => x === name);
  if (!tab) return;
  useHelp.setState({ page: null });
  useCharacter.setState({ mode: 'weapon' });
  const s = useStore.getState();
  const partTab = TABS.indexOf(tab) < 5;
  const first = s.catalog.find((c) => c.id === s.weaponId)?.parts[0]?.id;
  if (partTab && s.selection?.kind !== 'part' && first) s.select({ kind: 'part', id: first });
  setTimeout(() => document.querySelector<HTMLElement>(`.rail-tab[aria-label="${CSS.escape(t(`tabs.${tab}`))}"]`)?.click(), 60);
}

export function HelpDocs() {
  const lang = useLang();
  const { page, anchor } = useHelp();
  const [q, setQ] = useState('');
  const [zoom, setZoom] = useState<{ src: string; alt: string } | null>(null);
  const body = useRef<HTMLDivElement>(null);
  const all = PAGES[lang];
  const needle = q.trim().toLowerCase();
  const list = useMemo(() => (needle ? all.filter((p) => p.title.toLowerCase().includes(needle) || p.text.includes(needle)) : all), [all, needle]);
  const at = Math.max(
    0,
    all.findIndex((p) => p.id === page),
  );
  const current = all[at];
  const show = (id: string, to: string | null = null) => useHelp.setState({ page: id, anchor: to });
  useEffect(() => {
    const el = body.current;
    if (!el) return;
    const target = anchor ? el.querySelector<HTMLElement>(`#${anchor}`) : null;
    el.scrollTo({ top: target ? target.offsetTop - 12 : 0 });
  }, [current?.id, anchor]);
  // Links inside a page: another page, an editor panel; a picture opens large.
  const onClick = (e: React.MouseEvent) => {
    const el = e.target as HTMLElement;
    if (el instanceof HTMLImageElement && el.dataset.zoom !== undefined) return setZoom({ src: el.src, alt: el.alt });
    const href = el.closest('a')?.getAttribute('href');
    if (!href) return;
    e.preventDefault();
    if (href.startsWith('#')) show(href.slice(1));
    else if (href.startsWith('editor:')) openEditorTab(href.slice(7));
  };
  let lastGroup = '';
  return (
    <Modal opened={!!page} onClose={() => useHelp.setState({ page: null })} title={t('help.title')} size="min(1240px, 94vw)" centered padding={0} classNames={{ header: 'help-header' }}>
      <div className="help">
        <nav className="help-nav">
          <TextInput
            leftSection={<IconSearch size={13} />}
            rightSection={
              q ? (
                <ActionIcon size="xs" variant="subtle" color="gray" aria-label={t('leftRail.clearSearch')} onClick={() => setQ('')}>
                  <IconX size={12} />
                </ActionIcon>
              ) : null
            }
            placeholder={t('help.search')}
            value={q}
            onChange={(e) => setQ(e.currentTarget.value)}
          />
          <ScrollArea className="help-list" type="auto">
            {list.map((p) => {
              const head = !needle && p.group !== lastGroup ? (lastGroup = p.group) : null;
              const hits = needle ? p.sections.filter((s) => s.title.toLowerCase().includes(needle) || s.text.includes(needle)) : [];
              return (
                <div key={p.id}>
                  {head && <div className="help-group">{head}</div>}
                  <NavLink label={p.title} active={p.id === current.id} color="flare" variant="light" onClick={() => show(p.id)} />
                  {hits.map((s) => (
                    <NavLink key={s.id} className="help-hit" label={s.title} onClick={() => show(p.id, s.id)} />
                  ))}
                </div>
              );
            })}
            {!list.length && (
              <Text size="xs" c="dimmed" p="xs">
                {t('help.none')}
              </Text>
            )}
          </ScrollArea>
          <Button variant="light" color="cyan" leftSection={<IconHelp size={14} />} onClick={() => (useHelp.setState({ page: null }), useStore.setState({ tour: 'basics' }))}>
            {t('welcome.reopen')}
          </Button>
        </nav>
        <div className="help-body" ref={body}>
          <article className="help-article">
            {current.group && (
              <Badge variant="light" color="gray" radius="sm" mb={6}>
                {current.group}
              </Badge>
            )}
            <h1>{current.title}</h1>
            <div className="help-page" onClick={onClick} dangerouslySetInnerHTML={{ __html: current.html }} />
            <Group justify="space-between" className="help-pager">
              {at > 0 ? (
                <Button variant="subtle" color="gray" leftSection={<IconArrowLeft size={14} />} onClick={() => show(all[at - 1].id)}>
                  {all[at - 1].title}
                </Button>
              ) : (
                <span />
              )}
              {at < all.length - 1 && (
                <Button variant="light" color="flare" rightSection={<IconArrowRight size={14} />} onClick={() => show(all[at + 1].id)}>
                  {all[at + 1].title}
                </Button>
              )}
            </Group>
          </article>
          {current.sections.length > 1 && (
            <aside className="help-toc">
              <div className="help-toc-title">{t('help.onPage')}</div>
              {current.sections.map((s) => (
                <a key={s.id} onClick={() => show(current.id, s.id)}>
                  {s.title}
                </a>
              ))}
            </aside>
          )}
        </div>
      </div>
      <Modal opened={!!zoom} onClose={() => setZoom(null)} size="auto" centered withCloseButton={false} padding={0} classNames={{ body: 'help-zoom' }}>
        {zoom && <img src={zoom.src} alt={zoom.alt} onClick={() => setZoom(null)} />}
      </Modal>
    </Modal>
  );
}
