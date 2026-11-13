// The pattern picker of a pattern layer: a button showing the current pattern's picture and name, opening a grid of every
// pattern drawn in the layer's own colours. The pictures come from the livery's own fragment program (viewport/liveryGpu
// patternSwatch), a few per frame so opening never stalls; they are kept per pattern and colours.
import { Popover, UnstyledButton } from '@mantine/core';
import { IconChevronDown } from '@tabler/icons-react';
import { useCallback, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { t } from '../i18n';
import type { Pattern } from '../protocol';
import { patternSwatch } from '../viewport/liveryGpu';
import { PATTERNS } from '../viewport/uvPrograms';

const COLUMNS = 3;
const PER_FRAME = 4;

/** Pictures of the patterns in `colors`: the wanted pattern first, the rest a few per frame while `all` is set. */
function useSwatches(colors: (number[] | null)[], first: Pattern, all: boolean) {
  const [urls, setUrls] = useState<Partial<Record<Pattern, string>>>({});
  const key = JSON.stringify(colors);
  useEffect(() => {
    const queue = [first, ...(all ? PATTERNS.filter((p) => p !== first) : [])];
    let frame = 0;
    const step = () => {
      const made: Partial<Record<Pattern, string>> = {};
      for (const p of queue.splice(0, PER_FRAME)) {
        const url = patternSwatch(p, colors);
        if (url) made[p] = url;
      }
      if (Object.keys(made).length) setUrls((u) => ({ ...u, ...made }));
      if (queue.length) frame = requestAnimationFrame(step);
    };
    step();
    return () => cancelAnimationFrame(frame);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `colors` is read through its key
  }, [key, first, all]);
  return urls;
}

const Swatch = ({ url }: { url?: string }) => <i className="pattern-swatch" style={url ? { backgroundImage: `url(${url})` } : undefined} />;

export function PatternPicker({ value, colors, onChange }: { value: Pattern; colors: (number[] | null)[]; onChange: (p: Pattern) => void }) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const urls = useSwatches(colors, value, open);
  const id = useId();
  const typed = useRef({ text: '', at: 0 });
  const names = useMemo(() => PATTERNS.map((p) => t(`livery.p.${p}`)), []);
  const current = Math.max(0, PATTERNS.indexOf(value));

  useEffect(() => {
    if (!open) return;
    setActive(current);
  }, [open, current]);
  // The list is mounted a moment after opening (portal), so it takes focus when it appears.
  const focusList = useCallback((el: HTMLDivElement | null) => el?.focus({ preventScroll: true }), []);
  useEffect(() => {
    if (open) document.getElementById(`${id}-${active}`)?.scrollIntoView({ block: 'nearest' });
  }, [open, active, id]);

  const choose = (i: number) => {
    onChange(PATTERNS[i]);
    setOpen(false);
  };
  const onKey = (e: KeyboardEvent) => {
    const n = PATTERNS.length;
    const move = (to: number) => (e.preventDefault(), setActive(Math.min(n - 1, Math.max(0, to))));
    if (e.key === 'Escape') {
      // Closes only the picker, not the layer selection behind it.
      e.stopPropagation();
      setOpen(false);
    } else if (e.key === 'ArrowRight') move(active + 1);
    else if (e.key === 'ArrowLeft') move(active - 1);
    else if (e.key === 'ArrowDown') move(active + COLUMNS);
    else if (e.key === 'ArrowUp') move(active - COLUMNS);
    else if (e.key === 'Home') move(0);
    else if (e.key === 'End') move(n - 1);
    else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      choose(active);
    }
    else if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
      // Type-ahead: letters typed in quick succession make one prefix.
      const now = Date.now();
      const text = (now - typed.current.at > 800 ? '' : typed.current.text) + e.key.toLowerCase();
      typed.current = { text, at: now };
      const from = text.length === 1 ? active + 1 : active;
      const hit = [...names.keys()].map((k) => (k + from) % n).find((k) => names[k].toLowerCase().startsWith(text));
      if (hit !== undefined) move(hit);
    }
  };

  return (
    <Popover opened={open} onChange={setOpen} position="bottom-start" width="target" shadow="md" withinPortal>
      <Popover.Target>
        <UnstyledButton className="pattern-trigger" aria-haspopup="listbox" aria-expanded={open} aria-label={`${t('livery.patternKind')}: ${names[current]}`} onClick={() => setOpen((o) => !o)}>
          <Swatch url={urls[value]} />
          <span>{names[current]}</span>
          <IconChevronDown size={14} />
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown p={6}>
        <div ref={focusList} className="pattern-grid" role="listbox" tabIndex={0} aria-label={t('livery.patternKind')} aria-activedescendant={`${id}-${active}`} onKeyDown={onKey}>
          {PATTERNS.map((p, i) => (
            <div key={p} id={`${id}-${i}`} role="option" aria-selected={p === value} data-active={i === active || undefined} className="pattern-option" title={names[i]} onClick={() => choose(i)} onMouseMove={() => active !== i && setActive(i)}>
              <Swatch url={urls[p]} />
              <span>{names[i]}</span>
            </div>
          ))}
        </div>
      </Popover.Dropdown>
    </Popover>
  );
}
