import { Button, Checkbox, Group, Kbd, Select, Switch, TextInput } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import type { Attachment, Screen, ScreenSource } from '../protocol';
import { useItem, useStore } from '../store';
import { Color, Field, Num, Slider, Vec } from './fields';
import { SelectionLayout } from './SelectionLayout';

const SOURCES: { value: ScreenSource; label: string }[] = [
  { value: 'ammoClip', label: '弹匣内子弹' },
  { value: 'ammoReserve', label: '备弹' },
  { value: 'ammoTotal', label: '总弹药' },
  { value: 'static', label: '固定文字' },
];

const MM = 1000;

function AttachmentPanel({ a }: { a: Attachment }) {
  const item = useItem();
  const { updateAttachment, removeAttachment } = useStore.getState();
  const up = (fn: (a: Attachment) => void, key?: string) => updateAttachment(a.id!, fn, key && `${a.id}-${key}`);
  const upScreen = (fn: (s: Screen) => void, key?: string) => up((x) => void (x.screen && fn(x.screen)), key);
  const s = a.screen;
  return (
    <>
      <div className="insp-head">
        <div className="kind">{s ? '屏幕' : '模型挂件'}</div>
        <TextInput mt={4} size="sm" fw={650} value={a.name ?? ''} onChange={(e) => up((x) => void (x.name = e.currentTarget.value), 'name')} />
      </div>
      <div className="section">
        <h3>挂载</h3>
        <Field label="挂在部件">
          <Select value={a.part ?? ''} onChange={(v) => v && up((x) => void (x.part = v))} data={item?.parts.map((p) => ({ value: p.id, label: p.label })) ?? []} />
        </Field>
        <Switch mt="sm" label="始终朝向镜头" checked={!!a.faceCamera} onChange={(e) => up((x) => void (x.faceCamera = e.currentTarget.checked))} />
      </div>
      <SelectionLayout />
      {s && (
        <div className="section">
          <h3>屏幕</h3>
          <Field label="显示">
            <Select value={s.source ?? 'ammoClip'} onChange={(v) => v && upScreen((x) => void (x.source = v as ScreenSource))} data={SOURCES} />
          </Field>
          {s.source === 'static' ? (
            <Field label="文字">
              <TextInput
                value={s.staticText ?? ''}
                maxLength={8}
                placeholder="只支持数字、空格和 -"
                onChange={(e) => {
                  const t = e.currentTarget.value.replace(/[^0-9 -]/g, '');
                  upScreen((x) => void (x.staticText = t), 'text');
                }}
              />
            </Field>
          ) : (
            <Field label="位数">
              <Group gap="sm" wrap="nowrap">
                <Num w={90} value={s.digits ?? 2} step={1} decimalScale={0} min={1} max={4} onChange={(v) => upScreen((x) => void (x.digits = Math.round(v)), 'digits')} />
                <Checkbox size="xs" label="补零" checked={s.padZero !== false} onChange={(e) => upScreen((x) => void (x.padZero = e.currentTarget.checked))} />
              </Group>
            </Field>
          )}
          <Field label="尺寸 mm">
            <Vec value={s.size ?? [0.03, 0.015]} labels={['宽', '高']} scale={MM} step={0.5} decimalScale={1} min={0.5} onChange={(v) => upScreen((x) => void (x.size = v), 'size')} />
          </Field>
          <Field label="离表面 mm">
            <Num value={s.surfaceGap ?? 0.0005} scale={MM} step={0.1} min={0} onChange={(v) => upScreen((x) => void (x.surfaceGap = v), 'gap')} />
          </Field>
          <Field label="字号">
            <Slider value={s.textScale ?? 0.8} min={0.2} max={1} onChange={(v) => upScreen((x) => void (x.textScale = v), 'ts')} />
          </Field>
          <Field label="辉光">
            <Slider value={s.glow ?? 1.5} min={0} max={5} step={0.05} onChange={(v) => upScreen((x) => void (x.glow = v), 'glow')} />
          </Field>
          <Field label="字体">
            <TextInput value={s.font ?? ''} placeholder="默认等宽字体" onChange={(e) => {
              const f = e.currentTarget.value;
              upScreen((x) => void (x.font = f || null), 'font');
            }} />
          </Field>
        </div>
      )}
      {s && (
        <div className="section">
          <h3>颜色</h3>
          <Field label="文字">
            <Color value={s.textColor ?? [1, 1, 1, 1]} onChange={(c) => upScreen((x) => void (x.textColor = [c[0], c[1], c[2], 1]), 'tc')} />
          </Field>
          <Field label="底板">
            <Color alpha value={s.panelColor ?? [0, 0, 0, 0.85]} onChange={(c) => upScreen((x) => void (x.panelColor = c), 'pc')} />
          </Field>
          <Field label="边框">
            <Color alpha value={s.borderColor ?? [1, 1, 1, 1]} onChange={(c) => upScreen((x) => void (x.borderColor = c), 'bc')} />
          </Field>
          <Field label="边框粗 mm">
            <Num value={s.borderWidth ?? 0.0008} scale={MM} step={0.1} min={0} onChange={(v) => upScreen((x) => void (x.borderWidth = v), 'bw')} />
          </Field>
          <Field label="低弹药变色">
            <Group gap="sm" wrap="nowrap">
              <Switch checked={!!s.lowAmmoColor} onChange={(e) => {
                const on = e.currentTarget.checked;
                upScreen((x) => void (x.lowAmmoColor = on ? [1, 0.35, 0.25, 1] : null));
              }} />
              {s.lowAmmoColor && (
                <div style={{ flex: 1 }}>
                  <Color value={s.lowAmmoColor} onChange={(c) => upScreen((x) => void (x.lowAmmoColor = [c[0], c[1], c[2], 1]), 'lc')} />
                </div>
              )}
            </Group>
          </Field>
          {s.lowAmmoColor && (
            <Field label="低于">
              <Slider value={s.lowAmmoFraction ?? 0.2} min={0.05} max={0.9} onChange={(v) => upScreen((x) => void (x.lowAmmoFraction = v), 'lf')} />
            </Field>
          )}
          <p className="hint" style={{ marginTop: 8 }}>
            游戏里底板和边框只有显示和隐藏两种：透明度低于 1% 会隐藏。
          </p>
        </div>
      )}
      {a.material && (
        <div className="section">
          <h3>模型外观</h3>
          <Field label="颜色">
            <Color value={a.material.color ?? [1, 1, 1, 1]} onChange={(c) => up((x) => void (x.material && (x.material.color = [c[0], c[1], c[2], 1])), 'mc')} />
          </Field>
          <Switch mt="xs" label="不受光照（自身发亮）" checked={!!a.material.unlit} onChange={(e) => {
            const on = e.currentTarget.checked;
            up((x) => void (x.material && (x.material.unlit = on)));
          }} />
        </div>
      )}
      <div className="section">
        <Button variant="default" color="red" leftSection={<IconTrash size={14} />} onClick={() => removeAttachment(a.id!)}>
          删除这个挂件
        </Button>
      </div>
    </>
  );
}

function Overview() {
  return (
    <>
      <div className="insp-head">
        <div className="kind">挂件与屏幕</div>
        <div className="title">给枪加东西</div>
        <div className="meta">屏幕会实时显示弹药数；模型可以是任何 .glb 文件。在左边列表选中部件，再点列表底部的「添加挂件」，挂件就挂在那个部件上。</div>
      </div>
      <div className="section">
        <p className="hint">
          选中挂件后：<Kbd>G</Kbd> 移动 <Kbd>R</Kbd> 旋转 <Kbd>S</Kbd> 缩放，再按 <Kbd>X</Kbd>/<Kbd>Y</Kbd>/<Kbd>Z</Kbd> 锁定到部件的轴；单击或 <Kbd>Enter</Kbd> 确认，右键或 <Kbd>Esc</Kbd> 取消。打开工具条上的 <b>贴到表面</b> 后，点枪身就能把挂件贴上去。
        </p>
      </div>
    </>
  );
}

export function AttachInspector() {
  const selection = useStore((s) => s.selection);
  const a = useStore((s) => (selection?.kind === 'attachment' ? s.doc.attachments?.find((x) => x?.id === selection.id) : undefined));
  if (a) return <AttachmentPanel a={a} />;
  return (
    <>
      <Overview />
      <SelectionLayout />
    </>
  );
}
