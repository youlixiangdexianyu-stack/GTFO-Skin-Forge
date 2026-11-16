// What a skin can contain, each with one colour and icon used wherever it appears (tiles, section heads, tree rows).
import { Collapse, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { IconBrush, IconChevronLeft, IconChevronRight, IconDeviceDesktop, IconInfoCircle, IconMovie, IconPhoto, IconSparkles, IconSphere, IconVolume, type Icon } from '@tabler/icons-react';
import { useState, type ComponentProps, type ReactNode } from 'react';

export type Feature = 'texture' | 'attachment' | 'layer' | 'sound' | 'livery' | 'hologram' | 'fx';

export const FEATURE: Record<Feature, { color: string; icon: Icon }> = {
  texture: { color: 'var(--flare)', icon: IconPhoto },
  attachment: { color: 'var(--scan)', icon: IconDeviceDesktop },
  layer: { color: 'var(--violet)', icon: IconMovie },
  sound: { color: 'var(--ok)', icon: IconVolume },
  livery: { color: 'var(--paint)', icon: IconBrush },
  hologram: { color: 'var(--holo)', icon: IconSphere },
  fx: { color: 'var(--violet)', icon: IconSparkles },
};

/** Explanation behind a small info icon, instead of a paragraph on screen. */
export function Info({ label }: { label: ReactNode }) {
  return (
    <Tooltip label={label} multiline w={280} withArrow>
      <IconInfoCircle size={14} className="info" />
    </Tooltip>
  );
}

const KEY = 'sf.section.';
const stored = (id: string) => {
  try {
    return localStorage.getItem(KEY + id);
  } catch {
    return null;
  }
};

/** An inspector section: a heading (icon in its feature's colour, title, info tooltip, actions on the right) over its
 *  controls. With an `id` the heading folds the section away, and the choice is kept for the next time it shows. */
export function Section({
  id,
  title,
  feature,
  icon,
  color,
  info,
  actions,
  children,
}: {
  id?: string;
  title: ReactNode;
  feature?: Feature;
  icon?: Icon;
  color?: string;
  info?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
}) {
  const [open, setOpen] = useState(() => !id || stored(id) !== 'closed');
  const F = feature && FEATURE[feature];
  const Ico = icon ?? F?.icon;
  const toggle = () => {
    setOpen(!open);
    try {
      localStorage.setItem(KEY + id, open ? 'closed' : 'open');
    } catch {
      // a per-viewer convenience only
    }
  };
  const head = (
    <>
      {id && <IconChevronRight size={13} className="chev" data-open={open || undefined} />}
      {Ico && <Ico size={15} stroke={1.8} className="ico" />}
      <h3>{title}</h3>
    </>
  );
  return (
    <section className="section" data-section={id} style={{ ['--accent' as string]: color ?? F?.color ?? 'var(--dust)' }}>
      {(title || actions) && (
        <div className="section-head">
          {id ? (
            <UnstyledButton className="section-toggle" aria-expanded={open} onClick={toggle}>
              {head}
            </UnstyledButton>
          ) : (
            <div className="section-toggle">{head}</div>
          )}
          {info && <Info label={info} />}
          <span className="grow" />
          {actions}
        </div>
      )}
      <Collapse in={open} transitionDuration={120}>
        <div className="section-body">{children}</div>
      </Collapse>
    </section>
  );
}

/** The inspector's sticky header: what is selected (kind, in its colour, which goes back up when `onBack` is given), its
 *  name (`title`, or an editable field passed as `name`), a line of detail and actions on the right. */
export function InspHead({
  kind,
  icon: Ico,
  color,
  onBack,
  title,
  name,
  meta,
  actions,
  children,
}: {
  kind: ReactNode;
  icon: Icon;
  color: string;
  onBack?: () => void;
  title?: ReactNode;
  name?: { value: string; label?: string; placeholder?: string; onChange: (v: string) => void };
  meta?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <header className="insp-head" style={{ ['--accent' as string]: color }}>
      <div className="insp-top">
        <span className="insp-badge">
          <Ico size={18} stroke={1.7} />
        </span>
        <div className="insp-titles">
          {onBack ? (
            <UnstyledButton className="kind" onClick={onBack}>
              <IconChevronLeft size={12} stroke={2.2} />
              {kind}
            </UnstyledButton>
          ) : (
            <div className="kind">{kind}</div>
          )}
          {name ? (
            <TextInput className="insp-name" size="sm" aria-label={name.label} placeholder={name.placeholder} value={name.value} onChange={(e) => name.onChange(e.currentTarget.value)} />
          ) : (
            <div className="title">{title}</div>
          )}
        </div>
        {actions && <div className="insp-actions">{actions}</div>}
      </div>
      {meta && <div className="meta">{meta}</div>}
      {children}
    </header>
  );
}

/** Nothing here yet: a short line in a dashed box. */
export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

/** A button per kind (one thing to add, one motion to play): icon over name, in the given colour; `tip` explains it. */
export function KindGrid<K extends string>({ kinds, color, onPick }: { kinds: { id: K; label: string; icon: Icon; tip?: string }[]; color: string; onPick: (k: K) => void }) {
  return (
    <div className="kind-grid" style={{ ['--accent' as string]: color }}>
      {kinds.map((k) => {
        const button = (
          <UnstyledButton key={k.id} className="kind-btn" onClick={() => onPick(k.id)}>
            <k.icon size={17} stroke={1.7} />
            <span>{k.label}</span>
          </UnstyledButton>
        );
        return k.tip ? (
          <Tooltip key={k.id} label={k.tip} multiline w={240}>
            {button}
          </Tooltip>
        ) : (
          button
        );
      })}
    </div>
  );
}

/** A big entry point for one feature: icon, name, how many there are; clicking adds one. */
export function FeatureTile({ feature, title, count, ...rest }: { feature: Feature; title: string; count: number } & ComponentProps<'button'>) {
  const F = FEATURE[feature];
  return (
    <UnstyledButton {...rest} className="tile" style={{ ['--accent' as string]: F.color }}>
      <F.icon size={20} stroke={1.6} className="ico" />
      <span className="count">{count}</span>
      <b>{title}</b>
    </UnstyledButton>
  );
}
