// What a skin can contain, each with one colour and icon used wherever it appears (tiles, section heads, tree rows).
import { Tooltip, UnstyledButton } from '@mantine/core';
import { IconBrush, IconDeviceDesktop, IconInfoCircle, IconMovie, IconPhoto, IconVolume, type Icon } from '@tabler/icons-react';
import type { ComponentProps, ReactNode } from 'react';

export type Feature = 'texture' | 'attachment' | 'layer' | 'sound' | 'livery';

export const FEATURE: Record<Feature, { color: string; icon: Icon }> = {
  texture: { color: 'var(--flare)', icon: IconPhoto },
  attachment: { color: 'var(--scan)', icon: IconDeviceDesktop },
  layer: { color: 'var(--violet)', icon: IconMovie },
  sound: { color: 'var(--ok)', icon: IconVolume },
  livery: { color: 'var(--paint)', icon: IconBrush },
};

/** Explanation behind a small info icon, instead of a paragraph on screen. */
export function Info({ label }: { label: ReactNode }) {
  return (
    <Tooltip label={label} multiline w={280} withArrow>
      <IconInfoCircle size={14} className="info" />
    </Tooltip>
  );
}

/** Section heading with the feature's icon and colour, an optional info tooltip and actions on the right. */
export function FeatureHead({ feature, title, info, children }: { feature: Feature; title: string; info?: ReactNode; children?: ReactNode }) {
  const F = FEATURE[feature];
  return (
    <h3 className="feature-head" style={{ ['--accent' as string]: F.color }}>
      <F.icon size={14} className="ico" />
      <span>{title}</span>
      {info && <Info label={info} />}
      <span className="grow" />
      {children}
    </h3>
  );
}

/** A big entry point for one feature: icon, name, how many there are; clicking adds one. */
export function FeatureTile({ feature, title, count, ...rest }: { feature: Feature; title: string; count: number } & ComponentProps<'button'>) {
  const F = FEATURE[feature];
  return (
    <UnstyledButton {...rest} className="tile" style={{ ['--accent' as string]: F.color }}>
      <F.icon size={20} stroke={1.6} className="ico" />
      <b>{title}</b>
      <span className="count">{count}</span>
    </UnstyledButton>
  );
}
