// The gun's hold points in the 3D view, shown while one of them is selected: a dot on each point's node (the selected one solid),
// a line through the muzzle and the aim line along their direction, and a hand on each grip point. Drawn over the gun so a point
// inside a part can be seen; never picked.
//
// The hand is the game's own (gameHands.ts): its glove on the hand point, the fingers in the grip animation the hand takes on this
// gun (the skin's choice, else the gun's own) with the skin's finger turns on top. On the selected hand every finger segment has a
// dot at its far end: dragging it turns the segment's joint so that end follows the pointer, and clicking it points the transform
// gizmo at that joint, to turn it about any axis.
import { useFrame, useThree } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import type { Group } from 'three';
import type { HoldPoint } from '../protocol';
import { useStore } from '../store';
import { buildHand, dragDot, poseHand, storeTurn, useFinger, useGameHands } from './gameHands';
import { newGesture } from './ModalTransform';
import { hitsAt, partTarget, useSceneVersion, viewRefs } from './refs';

/** Each hold point's colour, in the 3D view and in the parts list. */
export const HOLD_COLOR: Record<HoldPoint, string> = { rightHand: '#ff8a3d', leftHand: '#3db8ff', muzzle: '#ff4d6a', sight: '#6dff8f' };

function Hand({ side, color, strong }: { side: 'rightHand' | 'leftHand'; color: string; strong: boolean }) {
  const hands = useGameHands();
  const which = side === 'rightHand' ? 'right' : 'left';
  const model = hands?.[which];
  const built = useMemo(() => (model ? buildHand(model) : null), [model]);
  useEffect(() => () => built?.dispose(), [built]);
  const own = useStore((s) => s.catalog.find((c) => c.id === s.weaponId)?.[side === 'rightHand' ? 'handRight' : 'handLeft']);
  const chosen = useStore((s) => s.doc.hands?.[which]);
  const turns = useStore((s) => s.doc.hands?.[which === 'left' ? 'leftFingers' : 'rightFingers']);
  const picked = useFinger((s) => (s.pick?.side === which ? s.pick.joint : -1));
  // The dots and the picked joint belong to the selected hand only.
  useEffect(() => {
    if (!strong) return;
    return () => {
      if (useFinger.getState().pick?.side === which) useFinger.setState({ pick: null });
    };
  }, [strong, which, built]);
  const shape = hands?.shapes[which][chosen ?? own ?? ''];
  const gl = useThree((s) => s.gl);
  // Pressing a dot picks its joint and starts dragging it. In the capture phase, before anything else takes the press.
  useEffect(() => {
    if (!strong || !built || !model) return;
    const el = gl.domElement;
    const from = (joint: number) => shape?.[joint] ?? model.joints[joint + 1].rotation;
    const down = (e: PointerEvent) => {
      if (e.button !== 0 || e.altKey || viewRefs.modalActive) return;
      const dot = hitsAt(el, e.clientX, e.clientY, built.dots)[0]?.object;
      if (!dot) return;
      // The press is the dot's alone: not the gizmo's (its rings pass close by), the camera's or the part picking's.
      e.stopImmediatePropagation();
      const joint = dot.userData.joint as number;
      const bone = built.joints[joint];
      useFinger.setState({ pick: { side: which, joint, obj: bone, commit: (gesture) => storeTurn(which, joint, bone, from(joint), gesture) } });
      const gesture = newGesture();
      dragDot(el, dot, bone, () => storeTurn(which, joint, bone, from(joint), gesture));
    };
    el.addEventListener('pointerdown', down, { capture: true });
    return () => el.removeEventListener('pointerdown', down, { capture: true });
  }, [strong, built, model, shape, which, gl]);
  if (!built || !model || !hands) return null;
  poseHand(built, model, shape, turns);
  built.material.color.set(color);
  built.material.opacity = strong ? 0.92 : 0.4;
  built.material.depthWrite = strong;
  built.dots.forEach((dot, i) => {
    dot.visible = strong;
    dot.material.color.set(i === picked ? '#ffffff' : '#101418');
    dot.material.opacity = i === picked ? 1 : 0.85;
    dot.scale.setScalar(i === picked ? 1.35 : 1);
  });
  return <primitive object={built.root} />;
}
export function HoldPointMarks() {
  const points = useStore((s) => s.preview?.points) ?? [];
  const selection = useStore((s) => s.selection);
  const group = useRef<Group>(null);
  useSceneVersion();
  const on = (p: { part: string; path: string }) => selection?.kind === 'part' && selection.id === p.part && selection.sub === p.path;
  const shown = points.some(on);
  useFrame(() => {
    group.current?.children.forEach((mark, i) => {
      const node = points[i] && partTarget(points[i].part, points[i].path);
      mark.visible = !!node;
      if (!node) return;
      node.getWorldPosition(mark.position);
      node.getWorldQuaternion(mark.quaternion);
    });
  });
  if (!shown) return null;
  return (
    <group ref={group}>
      {points.map((p) => (
        <group key={p.point} renderOrder={10}>
          <mesh renderOrder={10}>
            <sphereGeometry args={[on(p) ? 0.007 : 0.005, 16, 12]} />
            <meshBasicMaterial color={HOLD_COLOR[p.point]} depthTest={false} transparent opacity={on(p) ? 1 : 0.45} toneMapped={false} />
          </mesh>
          {(p.point === 'muzzle' || p.point === 'sight') && (
            <mesh renderOrder={10} rotation={[Math.PI / 2, 0, 0]}>
              <cylinderGeometry args={[0.0008, 0.0008, 0.12, 6]} />
              <meshBasicMaterial color={HOLD_COLOR[p.point]} depthTest={false} transparent opacity={on(p) ? 0.9 : 0.35} toneMapped={false} />
            </mesh>
          )}
          {(p.point === 'rightHand' || p.point === 'leftHand') && <Hand side={p.point} color={HOLD_COLOR[p.point]} strong={on(p)} />}
        </group>
      ))}
    </group>
  );
}
