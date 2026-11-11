// Layout block of the current selection, shown in every mode.
import { IDENTITY, overridesFor, poseWith, type UnityPose, type Vec3 } from '../space';
import { partTransformOf, useStore } from '../store';
import { layoutTarget, viewRefs } from '../viewport/refs';
import { TransformBlock } from './TransformBlock';
import { t } from '../i18n';

const v3 = (a: number[] | undefined, d: Vec3): Vec3 => (a && a.length === 3 ? (a as Vec3) : d);

function PartLayout({ part: selected, path: selectedPath }: { part: string; path?: string | null }) {
  useStore((s) => s.subParts); // re-render once the model (and its vanilla poses) is loaded
  const target = layoutTarget(selected, selectedPath);
  const part = target?.part ?? selected;
  const path = target?.path ?? null;
  const entry = useStore((s) => partTransformOf(s.doc, part, path));
  const vanilla = target && viewRefs.vanilla.get(target.obj);
  if (!vanilla) return null;
  const { setPartTransform, resetPartTransform } = useStore.getState();
  return (
    <TransformBlock
      title={path ? t('selectionLayout.k1', { v0: path.split('/').pop() }) : t('selectionLayout.k2')}
      pose={poseWith(vanilla, entry)}
      onChange={(pose, key) => setPartTransform(part, path, (x) => void Object.assign(x, overridesFor(vanilla, pose)), key)}
      onReset={(row) => resetPartTransform(part, path, row)}
      visible={entry?.visible !== false}
      onVisible={(v) => setPartTransform(part, path, (x) => void (x.visible = v ? null : false))}
    />
  );
}

function AttachmentLayout({ id }: { id: string }) {
  const a = useStore((s) => s.doc.attachments?.find((x) => x?.id === id));
  if (!a) return null;
  const { updateAttachment } = useStore.getState();
  const pose: UnityPose = { position: v3(a.position, [0, 0, 0]), rotation: v3(a.rotation, [0, 0, 0]), scale: v3(a.scale, [1, 1, 1]) };
  return (
    <TransformBlock
      title={t('selectionLayout.k2')}
      pose={pose}
      onChange={(p, key) =>
        updateAttachment(id, (x) => {
          x.position = p.position;
          x.rotation = p.rotation;
          x.scale = p.scale;
        }, key)
      }
      onReset={(row) =>
        updateAttachment(id, (x) => {
          if (!row || row === 'position') x.position = [...IDENTITY.position];
          if (!row || row === 'rotation') x.rotation = [...IDENTITY.rotation];
          if (!row || row === 'scale') x.scale = [...IDENTITY.scale];
        })
      }
      visible={a.visible !== false}
      onVisible={(v) => updateAttachment(id, (x) => void (x.visible = v))}
    />
  );
}

export function SelectionLayout() {
  const sel = useStore((s) => s.selection);
  if (sel?.kind === 'part') return <PartLayout key={`${sel.id}|${sel.sub ?? ''}`} part={sel.id} path={sel.sub} />;
  if (sel?.kind === 'attachment') return <AttachmentLayout key={sel.id} id={sel.id} />;
  return null;
}
