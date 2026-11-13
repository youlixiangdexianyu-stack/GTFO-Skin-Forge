// The spotlight guide: the whole window darkens except the control the current step wants, which keeps its own look and a
// bright ring, with the step's card beside it. Only the lit control takes clicks; the player does each step for real and
// the guide moves on when the editor's state says it is done (steps.ts). It follows the control every frame, so resizing,
// scrolling and menus opening keep it in place.
import { Button, Group, Progress, Text } from '@mantine/core';
import { IconBook, IconCircleCheck, IconPhoto } from '@tabler/icons-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { t } from '../i18n';
import { useStore } from '../store';
import { openHelp } from '../ui/HelpDocs';
import { snapshot, STEPS, putSampleTexture, type Target } from './steps';
import './tour.css';

const PAD = 4;
const GAP = 14;
const EDGE = 12;
/** How long a step waits for its control before saying it cannot find it. */
const WAIT_MS = 10000;

function shown(e: Element): boolean {
  const r = e.getBoundingClientRect();
  return r.width > 0 && r.height > 0 && getComputedStyle(e).visibility !== 'hidden';
}

/** An element's accessible name here: its aria-label, its own text, or the text of one of its innermost elements. */
function named(e: Element, name: string): boolean {
  const label = e.getAttribute('aria-label');
  if (label != null) return label.trim() === name;
  if (e.textContent?.trim() === name) return true;
  return [...e.querySelectorAll('*')].some((c) => !c.childElementCount && c.textContent?.trim() === name);
}

/** The first visible element of the first target that has one. */
export function findTarget(targets: Target[]): Element | null {
  for (const x of targets)
    for (const e of document.querySelectorAll(x.css))
      if (!e.closest('.tour') && (!x.name || named(e, t(x.name))) && shown(e)) return e;
  return null;
}

/** Where the card goes: beside the lit box where it fits (right, left, below, above), else inside its top left. */
function place(box: DOMRect | null, w: number, h: number): [number, number] {
  const W = innerWidth;
  const H = innerHeight;
  const clampX = (x: number) => Math.max(EDGE, Math.min(W - w - EDGE, x));
  const clampY = (y: number) => Math.max(EDGE, Math.min(H - h - EDGE, y));
  if (!box) return [(W - w) / 2, (H - h) / 2];
  if (box.right + GAP + w <= W - EDGE) return [box.right + GAP, clampY(box.top)];
  if (box.left - GAP - w >= EDGE) return [box.left - GAP - w, clampY(box.top)];
  if (box.bottom + GAP + h <= H - EDGE) return [clampX(box.left), box.bottom + GAP];
  if (box.top - GAP - h >= EDGE) return [clampX(box.left), box.top - GAP - h];
  return [clampX(box.left + GAP), clampY(box.top + GAP)];
}

const px = (e: HTMLElement | null, l: number, tp: number, w: number, h: number) => {
  if (!e) return;
  e.style.left = `${l}px`;
  e.style.top = `${tp}px`;
  e.style.width = `${Math.max(0, w)}px`;
  e.style.height = `${Math.max(0, h)}px`;
};

/** `intro`: what the first card shows; `example`: makes the example skin from the last card. */
export function Tour({ intro, example }: { intro: ReactNode; example: () => void }) {
  const state = useStore();
  const [i, setI] = useState(0);
  const [confirm, setConfirm] = useState(false);
  const [target, setTarget] = useState<Element | null>(null);
  const [missing, setMissing] = useState(false);
  const start = useRef(snapshot(useStore.getState()));
  const hole = useRef<HTMLDivElement>(null);
  const blocks = useRef<(HTMLDivElement | null)[]>([]);
  const card = useRef<HTMLDivElement>(null);
  const primary = useRef<HTMLButtonElement>(null);
  const step = STEPS[i];
  const last = i === STEPS.length - 1;
  const done = !!step.done?.(state, start.current);
  const canNext = missing || (step.ready ? step.ready(state) : step.done ? done : true);
  const warn = step.warn?.(state);

  const go = (n: number) => {
    start.current = snapshot(useStore.getState());
    setTarget(null);
    setMissing(false);
    setConfirm(false);
    setI(Math.max(0, Math.min(STEPS.length - 1, n)));
  };
  const close = () => useStore.getState().setPrefs({ welcomed: true });

  // Done by the player: on to the next step.
  useEffect(() => {
    if (done && !last) go(i + 1);
  }, [done]); // eslint-disable-line react-hooks/exhaustive-deps

  // Looks for the step's control; it may appear later (a tab opening, a menu, a weapon loading).
  useEffect(() => {
    if (!step.targets.length) return;
    let since = Date.now();
    const id = setInterval(() => {
      const e = findTarget(step.targets);
      if (e) since = Date.now();
      setTarget((old) => {
        if (e && e !== old) e.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        return e;
      });
      setMissing(!e && Date.now() - since > WAIT_MS);
    }, 150);
    return () => clearInterval(id);
  }, [i]); // eslint-disable-line react-hooks/exhaustive-deps

  // Follows the control every frame: the lit hole, the four click blockers around it and the card.
  useEffect(() => {
    let frame = 0;
    const tick = () => {
      const r = target?.isConnected ? target.getBoundingClientRect() : null;
      const box = r && new DOMRect(r.left - PAD, r.top - PAD, r.width + PAD * 2, r.height + PAD * 2);
      const [l, tp, w, h] = box ? [box.left, box.top, box.width, box.height] : [innerWidth / 2, innerHeight / 2, 0, 0];
      px(hole.current, l, tp, w, h);
      if (hole.current) {
        hole.current.dataset.lit = String(!!box);
        hole.current.style.borderRadius = target ? `${(parseFloat(getComputedStyle(target).borderTopLeftRadius) || 0) + PAD}px` : '0';
      }
      const [b0, b1, b2, b3] = blocks.current;
      px(b0 ?? null, 0, 0, innerWidth, tp);
      px(b1 ?? null, 0, tp + h, innerWidth, innerHeight - tp - h);
      px(b2 ?? null, 0, tp, l, h);
      px(b3 ?? null, l + w, tp, innerWidth - l - w, h);
      if (card.current) {
        const [x, y] = place(box, card.current.offsetWidth, card.current.offsetHeight);
        card.current.style.transform = `translate(${Math.round(x)}px, ${Math.round(y)}px)`;
      }
      frame = requestAnimationFrame(tick);
    };
    tick();
    return () => cancelAnimationFrame(frame);
  }, [target]);

  // Cards without a control to click take the keyboard focus.
  useEffect(() => {
    if (!step.targets.length || confirm) primary.current?.focus();
  }, [i, confirm]); // eslint-disable-line react-hooks/exhaustive-deps

  // Esc asks whether to leave (and again goes back); arrows step when no text field has the focus.
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLElement && e.target.matches('input, textarea, [contenteditable="true"]');
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        setConfirm((c) => !c);
      } else if (!typing && !confirm && e.key === 'ArrowRight' && canNext && !last) go(i + 1);
      else if (!typing && !confirm && e.key === 'ArrowLeft' && i > 0) go(i - 1);
    };
    window.addEventListener('keydown', key, true);
    return () => window.removeEventListener('keydown', key, true);
  });

  const waiting = step.targets.length > 0 && !target;
  return createPortal(
    <div className="tour">
      <div className="tour-hole" ref={hole} />
      {[0, 1, 2, 3].map((n) => (
        <div key={n} className="tour-block" ref={(e) => void (blocks.current[n] = e)} />
      ))}
      <div className="tour-card" ref={card} role="dialog" aria-label={t(`tour.${step.id}`)}>
        <Group justify="space-between" gap="xs" mb={8}>
          <Text size="xs" c="dimmed">
            {t('tour.step', { v0: i + 1, v1: STEPS.length })}
          </Text>
          <Progress w={110} size="sm" color="flare" value={((i + 1) / STEPS.length) * 100} />
        </Group>
        {confirm ? (
          <>
            <Text fw={650} mb={6}>
              {t('tour.skipTitle')}
            </Text>
            <Text size="sm" c="dimmed">
              {t('tour.skipAsk')}
            </Text>
            <Group justify="flex-end" mt="md" gap="xs">
              <Button variant="default" ref={primary} onClick={() => setConfirm(false)}>
                {t('tour.skipNo')}
              </Button>
              <Button color="red" variant="light" onClick={close}>
                {t('tour.skipYes')}
              </Button>
            </Group>
          </>
        ) : (
          <>
            <Text className="tour-title" fw={650} mb={6}>
              {t(`tour.${step.id}`)}
            </Text>
            {step.id === 'intro' ? intro : <Text size="sm">{t(`tour.${step.id}Info`)}</Text>}
            {warn && (
              <Text size="xs" c="yellow" mt={8}>
                {warn}
              </Text>
            )}
            {waiting && (
              <Text size="xs" c={missing ? 'yellow' : 'dimmed'} mt={8}>
                {missing ? t('tour.missing') : t('tour.waiting')}
              </Text>
            )}
            {!waiting && step.done && canNext && !last && (
              <Group gap={6} mt={8} wrap="nowrap">
                <IconCircleCheck size={15} color="var(--ok)" />
                <Text size="xs" c="var(--ok)">
                  {t('tour.ready')}
                </Text>
              </Group>
            )}
            {step.sample && (
              <Button mt="sm" variant="light" color="cyan" leftSection={<IconPhoto size={14} />} onClick={() => void putSampleTexture()}>
                {t('tour.sample')}
              </Button>
            )}
            <Group justify="space-between" mt="md" gap="xs" wrap="nowrap">
              {last ? (
                <Button variant="subtle" color="gray" onClick={() => (close(), example())}>
                  {t('welcome.example')}
                </Button>
              ) : (
                <Button variant="subtle" color="gray" onClick={() => setConfirm(true)}>
                  {t('tour.skip')}
                </Button>
              )}
              <Group gap="xs" wrap="nowrap">
                {i > 0 && !last && (
                  <Button variant="default" onClick={() => go(i - 1)}>
                    {t('tour.back')}
                  </Button>
                )}
                {last && (
                  <Button variant="light" color="cyan" leftSection={<IconBook size={14} />} onClick={() => (close(), openHelp())}>
                    {t('tour.openHelp')}
                  </Button>
                )}
                {last ? (
                  <Button color="flare" ref={primary} onClick={close}>
                    {t('tour.done')}
                  </Button>
                ) : (
                  <Button color="flare" ref={primary} disabled={!canNext} onClick={() => go(i + 1)}>
                    {i === 0 ? t('tour.begin') : t('tour.next')}
                  </Button>
                )}
              </Group>
            </Group>
          </>
        )}
      </div>
    </div>,
    document.body,
  );
}
