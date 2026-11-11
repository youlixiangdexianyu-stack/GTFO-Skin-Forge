// Layout block of the current selection, shown in every mode.
import { deltaForPose, IDENTITY, resultingPose, type UnityPose, type Vec3 } from '../space';
import { partTransformOf, useStore } from '../store';
import { partTargets, viewRefs } from '../viewport/refs';
import { TransformBlock } from './TransformBlock';

const v3 = (a: number[] | undefined, d: Vec3): Vec3 => (a && a.length === 3 ? (a as Vec3) : d);

function PartLayout({ part, sub }: { part: string; sub?: string | null }) {
  const t = useStore((s) => partTransformOf(s.doc, part, sub));
  useStore((s) => s.subParts); // re-render once the model (and its vanilla poses) is loaded
  const obj = partTargets(part, sub)[0];
  const vanilla = obj && viewRefs.vanilla.get(obj);
  if (!vanilla) return null;
  const { setPartTransform, resetPartTransform } = useStore.getState();
  const delta: UnityPose = { position: v3(t?.position, [0, 0, 0]), rotation: v3(t?.rotation, [0, 0, 0]), scale: v3(t?.scale, [1, 1, 1]) };
  return (
    <TransformBlock
      title={sub ? `位置与大小 · ${sub}` : '位置与大小'}
      pose={resultingPose(vanilla, delta)}
      onChange={(pose, key) => {
        const d = deltaForPose(vanilla, pose);
        setPartTransform(part, sub, (x) => {
          x.position = d.position;
          x.rotation = d.rotation;
          x.scale = d.scale;
        }, key);
      }}
      onReset={(row) => resetPartTransform(part, sub, row)}
      visible={t?.visible !== false}
      onVisible={(v) => setPartTransform(part, sub, (x) => void (x.visible = v))}
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
      title="位置与大小"
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
  if (sel?.kind === 'part') return <PartLayout key={`${sel.id}|${sel.sub ?? ''}`} part={sel.id} sub={sel.sub} />;
  if (sel?.kind === 'attachment') return <AttachmentLayout key={sel.id} id={sel.id} />;
  return null;
}
