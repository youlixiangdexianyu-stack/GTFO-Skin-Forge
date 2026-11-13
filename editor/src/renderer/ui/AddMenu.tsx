// Everything that can be added to one part, in one menu (the "+" on a part row): textures, paint layers, dynamic layers, screens,
// models and a replacement model for the part itself. New kinds of per-part content get their item here.
import { Menu } from '@mantine/core';
import { IconBox, IconBrush, IconDeviceDesktop, IconLetterT, IconMovie, IconPhoto, IconReplace, IconStack2 } from '@tabler/icons-react';
import type { ReactElement } from 'react';
import { importMesh } from '../actions';
import type { CatalogPart, LayerKind } from '../protocol';
import { useStore } from '../store';
import { addLayer } from './DynamicInspector';
import { FEATURE } from './Features';
import { addLiveryLayer, LIVERY_KINDS } from './LiveryPanel';
import { hasOwnModel, replacePartModel } from './PartModel';
import { ShapeMenuItems, SubMenu } from './ShapePanel';
import { t } from '../i18n';
import { addCodeEffect } from '../codeFx';

const LAYERS: { kind: LayerKind; label: () => string; icon: typeof IconPhoto }[] = [
  { kind: 'image', label: () => t('dynamicInspector.k1'), icon: IconPhoto },
  { kind: 'sequence', label: () => t('dynamicInspector.k2'), icon: IconStack2 },
  { kind: 'video', label: () => t('dynamicInspector.k3'), icon: IconMovie },
  { kind: 'text', label: () => t('dynamicInspector.k4'), icon: IconLetterT },
];

export function PartAddMenu({ part, path, children }: { part: CatalogPart; path?: string | null; children: ReactElement }) {
  const { select, addAttachment } = useStore.getState();
  const here = () => select({ kind: 'part', id: part.id, sub: path ?? null });
  const material = part.materials[0];
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>{children}</Menu.Target>
      <Menu.Dropdown onClick={(e) => e.stopPropagation()}>
        <Menu.Item leftSection={<IconPhoto size={14} color={FEATURE.texture.color} />} onClick={here}>
          {t('addMenu.texture')}
        </Menu.Item>
        {material && (
          <>
            <SubMenu icon={<IconBrush size={14} color={FEATURE.livery.color} />} label={t('livery.title')}>
              {LIVERY_KINDS.map((k) => (
                <Menu.Item key={k.id} leftSection={<k.icon size={14} color={FEATURE.livery.color} />} onClick={() => (here(), addLiveryLayer(material, part.id, k.id))}>
                  {k.label()}
                </Menu.Item>
              ))}
            </SubMenu>
            <SubMenu icon={<IconMovie size={14} color={FEATURE.layer.color} />} label={t('dynamicInspector.k33')}>
              {LAYERS.map((l) => (
                <Menu.Item key={l.kind} leftSection={<l.icon size={14} color={FEATURE.layer.color} />} onClick={() => (here(), void addLayer(material, part.id, l.kind))}>
                  {l.label()}
                </Menu.Item>
              ))}
            </SubMenu>
            <Menu.Label>{t('codeFx.title')}</Menu.Label>
            <Menu.Item leftSection={<FEATURE.fx.icon size={14} color={FEATURE.fx.color} />} onClick={() => (here(), addCodeEffect({ material, part: part.id }))}>
              {t('codeFx.addOnPart')}
            </Menu.Item>
          </>
        )}
        <Menu.Label>{t('skinInspector.partAttachments')}</Menu.Label>
        <Menu.Item leftSection={<IconDeviceDesktop size={14} color={FEATURE.attachment.color} />} onClick={() => (here(), addAttachment('screen'))}>
          {t('leftRail.k18')}
        </Menu.Item>
        <Menu.Item leftSection={<IconBox size={14} color={FEATURE.attachment.color} />} onClick={() => (here(), void importMesh())}>
          {t('leftRail.k19')}
        </Menu.Item>
        <ShapeMenuItems color={FEATURE.attachment.color} before={here} />
        {hasOwnModel(part.id, path) && (
          <>
            <Menu.Divider />
            <Menu.Item leftSection={<IconReplace size={14} />} onClick={() => (here(), void replacePartModel(part.id, path))}>
              {t('partModel.replace')}
            </Menu.Item>
          </>
        )}
      </Menu.Dropdown>
    </Menu>
  );
}
