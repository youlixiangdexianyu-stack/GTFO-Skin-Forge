import { Edges, useGLTF } from '@react-three/drei';
import { createPortal, useFrame, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef, useState } from 'react';
import { CanvasTexture, DoubleSide, Group, MeshBasicMaterial, SRGBColorSpace, type Mesh, type MeshStandardMaterial, type Object3D } from 'three';
import { glyphCanvas, GLYPHS } from '../frames';
import { fileUrl, type Attachment, type Screen } from '../protocol';
import { unityToThree } from '../space';
import { useStore } from '../store';
import { anchorObject, quiet, SCENE_EVENT, viewRefs } from './refs';

const rgb = (c?: number[] | null) => `rgb(${(c?.[0] ?? 1) * 255},${(c?.[1] ?? 1) * 255},${(c?.[2] ?? 1) * 255})`;

/** Screen preview: panel, inner border and a sample value drawn with the glyph strip. */
function ScreenMesh({ screen, selected, onPick }: { screen: Screen; selected: boolean; onPick: (e: ThreeEvent<MouseEvent>) => void }) {
  const [w, h] = screen.size ?? [0.03, 0.015];
  const sample = useRef(0);
  const canvas = useMemo(() => document.createElement('canvas'), []);
  const texture = useMemo(() => {
    const t = new CanvasTexture(canvas);
    t.colorSpace = SRGBColorSpace;
    return t;
  }, [canvas]);
  const glyphs = useMemo(() => glyphCanvas(screen.font), [screen.font]);

  // Redraws the face: counts down so the low-ammo colour is visible too.
  const draw = (value: number) => {
    const px = 512;
    canvas.width = px;
    canvas.height = Math.max(8, Math.round((px * h) / w));
    const ctx = canvas.getContext('2d')!;
    const H = canvas.height;
    ctx.clearRect(0, 0, px, H);
    const panel = screen.panelColor ?? [0, 0, 0, 0.85];
    if ((panel[3] ?? 1) > 0.01) {
      ctx.globalAlpha = panel[3] ?? 1;
      ctx.fillStyle = rgb(panel);
      ctx.fillRect(0, 0, px, H);
    }
    const border = screen.borderColor ?? [1, 1, 1, 1];
    const bw = ((screen.borderWidth ?? 0.0008) / w) * px;
    if ((border[3] ?? 1) > 0.01 && bw > 0) {
      ctx.globalAlpha = border[3] ?? 1;
      ctx.strokeStyle = rgb(border);
      ctx.lineWidth = bw;
      ctx.strokeRect(bw / 2, bw / 2, px - bw, H - bw);
    }
    const digits = Math.max(1, screen.digits ?? 2);
    const text = screen.source === 'static' ? (screen.staticText ?? '') : screen.padZero !== false ? String(value).padStart(digits, '0') : String(value);
    const low = screen.lowAmmoColor && value / 30 <= (screen.lowAmmoFraction ?? 0.2);
    const gh = H * (screen.textScale ?? 0.8);
    const cellW = glyphs.width / GLYPHS.length;
    const gw = (gh * cellW) / glyphs.height;
    const x0 = (px - gw * text.length) / 2;
    const tmp = document.createElement('canvas');
    tmp.width = Math.max(1, Math.ceil(gw * text.length));
    tmp.height = Math.max(1, Math.ceil(gh));
    const t = tmp.getContext('2d')!;
    [...text].forEach((ch, i) => {
      const gi = GLYPHS.indexOf(ch);
      if (gi >= 0) t.drawImage(glyphs, gi * cellW, 0, cellW, glyphs.height, i * gw, 0, gw, gh);
    });
    t.globalCompositeOperation = 'source-in';
    t.fillStyle = rgb(low ? screen.lowAmmoColor : screen.textColor);
    t.fillRect(0, 0, tmp.width, tmp.height);
    ctx.globalAlpha = 1;
    ctx.shadowColor = rgb(low ? screen.lowAmmoColor : screen.textColor);
    ctx.shadowBlur = 6 * (screen.glow ?? 1);
    ctx.drawImage(tmp, x0, (H - gh) / 2);
    texture.needsUpdate = true;
  };

  useEffect(() => draw(30), [screen, glyphs]); // eslint-disable-line react-hooks/exhaustive-deps
  const last = useRef(0);
  useFrame(({ clock }) => {
    if (screen.source === 'static' || clock.elapsedTime - last.current < 0.35) return;
    last.current = clock.elapsedTime;
    sample.current = (sample.current + 1) % 31;
    draw(30 - sample.current);
  });

  return (
    <group>
      <mesh onClick={onPick} renderOrder={2}>
        <planeGeometry args={[w, h]} />
        <meshBasicMaterial map={texture} transparent toneMapped={false} side={DoubleSide} depthWrite={false} />
        {selected && <Edges color="#ff7a2f" renderOrder={3} />}
      </mesh>
    </group>
  );
}

function MeshModel({ glb, attachment, onPick }: { glb: string; attachment: Attachment; onPick: (e: ThreeEvent<MouseEvent>) => void }) {
  const gltf = useGLTF(fileUrl(glb));
  const scene = useMemo(() => gltf.scene.clone(true), [gltf.scene]);
  useEffect(() => {
    const m = attachment.material;
    const c = m?.color ?? [1, 1, 1, 1];
    scene.traverse((o) => {
      const mesh = o as Mesh;
      if (!mesh.isMesh) return;
      const src = mesh.material as MeshStandardMaterial;
      const original = (mesh.userData.original ??= src) as MeshStandardMaterial;
      const next = m?.unlit ? new MeshBasicMaterial({ map: original.map, toneMapped: false }) : original.clone();
      next.color.setRGB(c[0], c[1], c[2]);
      mesh.material = next;
    });
  }, [scene, attachment.material]);
  return <primitive object={scene} onClick={onPick} />;
}

function AttachmentNode({ a, anchor }: { a: Attachment; anchor: Object3D }) {
  const group = useRef<Group>(null);
  const selection = useStore((s) => s.selection);
  const meshPreviews = useStore((s) => (s.doc.editor as Record<string, Record<string, string>> | undefined)?.meshPreviews);
  const selected = selection?.kind === 'attachment' && selection.id === a.id;
  const pose = unityToThree({
    position: (a.position ?? [0, 0, 0]) as [number, number, number],
    rotation: (a.rotation ?? [0, 0, 0]) as [number, number, number],
    scale: (a.scale ?? [1, 1, 1]) as [number, number, number],
  });

  useEffect(() => {
    const g = group.current;
    if (!g) return;
    if (viewRefs.modalActive && selected) return; // the modal transform drives the group directly
    g.position.copy(pose.position);
    g.quaternion.copy(pose.quaternion);
    g.scale.copy(pose.scale);
  });
  useFrame(({ camera }) => {
    if (a.faceCamera && group.current) group.current.lookAt(camera.position); // visible face (three +Z) toward camera
  });
  useEffect(() => {
    const g = group.current;
    if (g) viewRefs.attachments.set(a.id!, g);
    return () => void viewRefs.attachments.delete(a.id!);
  }, [a.id]);

  const onPick = (e: ThreeEvent<MouseEvent>) => {
    if (e.delta > 4 || quiet()) return;
    e.stopPropagation();
    useStore.getState().select({ kind: 'attachment', id: a.id! });
  };

  if (a.visible === false) return null;
  const glb = meshPreviews?.[a.id!];
  return createPortal(
    <group ref={group} name={`attachment:${a.id}`}>
      {a.screen && <ScreenMesh screen={a.screen} selected={selected} onPick={onPick} />}
      {glb && <MeshModel glb={glb} attachment={a} onPick={onPick} />}
    </group>,
    anchor,
  );
}

export function Attachments() {
  const attachments = useStore((s) => s.doc.attachments);
  const [, force] = useState(0);
  // Anchors exist only after the weapon model mounted.
  useEffect(() => {
    const f = () => force((n) => n + 1);
    window.addEventListener(SCENE_EVENT, f);
    return () => window.removeEventListener(SCENE_EVENT, f);
  }, []);
  return (
    <>
      {(attachments ?? []).map((a) => {
        if (!a) return null;
        const anchor = anchorObject(a.part ?? '', a.transform);
        return anchor ? <AttachmentNode key={a.id} a={a} anchor={anchor} /> : null;
      })}
    </>
  );
}
