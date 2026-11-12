// Replaced part models in the 3D view: the node's own meshes stop drawing and the imported model is drawn in its place,
// with the node's (painted) material, as the plugin does in game (mesh swap, same material, children untouched).
import { useGLTF } from '@react-three/drei';
import { createPortal } from '@react-three/fiber';
import { useEffect, useMemo, useState } from 'react';
import type { Material, Mesh, Object3D } from 'three';
import { fileUrl } from '../protocol';
import { partMeshKey, useStore } from '../store';
import { ownMeshes, partTarget, SCENE_EVENT } from './refs';

function Replacement({ glb, node }: { glb: string; node: Object3D }) {
  const gltf = useGLTF(fileUrl(glb));
  const scene = useMemo(() => gltf.scene.clone(true), [gltf.scene]);
  useEffect(() => {
    const own = ownMeshes(node);
    const material = own[0]?.material as Material | undefined;
    // Per mesh, so a material other meshes share keeps drawing them.
    const hidden = own.map((m) => {
      const original = m.material;
      const off = (Array.isArray(original) ? original[0] : original).clone();
      off.visible = false;
      m.material = off;
      return [m, original] as const;
    });
    scene.traverse((o) => {
      if ((o as Mesh).isMesh && material) (o as Mesh).material = material;
    });
    return () => hidden.forEach(([m, original]) => ((m.material as Material).dispose(), (m.material = original)));
  }, [scene, node]);
  return createPortal(<primitive object={scene} />, node);
}

export function PartMeshes() {
  const parts = useStore((s) => s.doc.parts);
  const previews = useStore((s) => (s.doc.editor as Record<string, Record<string, string> | undefined> | undefined)?.partMeshPreviews);
  const [, force] = useState(0);
  // Nodes exist only after the weapon model mounted.
  useEffect(() => {
    const f = () => force((n) => n + 1);
    window.addEventListener(SCENE_EVENT, f);
    return () => window.removeEventListener(SCENE_EVENT, f);
  }, []);
  return (
    <>
      {(parts ?? []).map((p) => {
        if (!p?.mesh || !p.part) return null;
        const glb = previews?.[partMeshKey(p.part, p.path)];
        const node = partTarget(p.part, p.path);
        return glb && node ? <Replacement key={partMeshKey(p.part, p.path)} glb={glb} node={node} /> : null;
      })}
    </>
  );
}
