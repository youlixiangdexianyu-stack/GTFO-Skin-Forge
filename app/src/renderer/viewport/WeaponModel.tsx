import { useGLTF } from '@react-three/drei';
import { useFrame, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import { Box3, CanvasTexture, Mesh, Vector3, SRGBColorSpace, type MeshStandardMaterial, type Object3D } from 'three';
import { drawLayer, LayerSource, loadImage } from '../frames';
import { CHANNELS, fileUrl, type DynamicLayer, type WeaponPreview } from '../protocol';
import { editorOf, useStore } from '../store';
import { poseWith, unityToThree } from '../space';
import { releaseTextures } from './textures';
import { editableMaterials, paintMaterial, restoreUnityNames } from './weaponScene';
import { displayOffset, partOf, partTarget, pathChain, playTime, registerTransforms, quiet, SCENE_EVENT, viewRefs } from './refs';
import { t } from '../i18n';

/** Live composite of a base texture plus its dynamic layers (the 3D decal preview). */
class Compositor {
  canvas = document.createElement('canvas');
  texture = new CanvasTexture(this.canvas);
  private sources: [DynamicLayer, LayerSource][] = [];
  private base: HTMLImageElement | null = null;
  private start = performance.now();

  constructor(basePath: string | null, layers: DynamicLayer[]) {
    this.texture.flipY = false;
    this.texture.colorSpace = SRGBColorSpace;
    this.sources = layers.map((l) => [l, new LayerSource(l)]);
    if (basePath) void loadImage(basePath).then((img) => (this.base = img));
    this.canvas.width = this.canvas.height = 1024;
  }

  draw() {
    const ctx = this.canvas.getContext('2d')!;
    const W = this.canvas.width;
    const H = this.canvas.height;
    ctx.fillStyle = '#808080';
    ctx.fillRect(0, 0, W, H);
    if (this.base) ctx.drawImage(this.base, 0, 0, W, H);
    for (const [l, src] of this.sources) {
      const time = playTime(l.trigger, l.play, l.playSeconds, src.duration, this.start);
      const f = time === null ? null : src.at(time);
      if (f) drawLayer(ctx, f, l, W, H);
    }
    this.texture.needsUpdate = true;
  }

  dispose() {
    this.sources.forEach(([, s]) => s.dispose());
    this.texture.dispose();
  }
}

let shownGlb: string | null = null;

export function WeaponModel({ preview }: { preview: WeaponPreview }) {
  const gltf = useGLTF(fileUrl(preview.glb));
  const scene = useMemo(() => {
    restoreUnityNames(gltf);
    return gltf.scene.clone(true);
  }, [gltf]);
  const doc = useStore((s) => s.doc);
  const mode = useStore((s) => s.mode);
  const select = useStore((s) => s.select);
  const materials = useRef(new Map<string, MeshStandardMaterial>());
  const compositors = useRef(new Map<string, Compositor>());

  // Part <-> node mapping and one editable material per game material.
  useEffect(() => {
    viewRefs.root = scene;
    viewRefs.partNodes.clear();
    viewRefs.transforms.clear();
    viewRefs.transformKey.clear();
    viewRefs.nodeToPart.clear();
    for (const p of preview.parts) {
      const nodes = p.nodes.map((n) => scene.getObjectByName(n)).filter(Boolean) as Object3D[];
      viewRefs.partNodes.set(p.id, nodes);
      nodes.forEach((n) => viewRefs.nodeToPart.set(n, p.id));
      registerTransforms(
        p.id,
        (p.transforms ?? []).flatMap((t) => {
          const obj = scene.getObjectByName(t.node);
          return obj ? [{ path: t.path, obj }] : [];
        }),
      );
    }
    const pose = (o: Object3D) => ({
      position: o.position.clone(),
      quaternion: o.quaternion.clone(),
      scale: o.scale.clone(),
    });
    // Captured once per model; an effect re-run (StrictMode, preview refresh) restores it before laying out ammo again.
    scene.traverse((o) => {
      const v = viewRefs.vanilla.get(o);
      if (!v) return void viewRefs.vanilla.set(o, pose(o));
      o.position.copy(v.position);
      o.quaternion.copy(v.quaternion);
      o.scale.copy(v.scale);
    });
    layOutAmmo();
    scene.traverse((o) => viewRefs.display.set(o, pose(o)));
    // Every drawn mesh belongs to a part and can be selected; anything else is hidden and named once in the log.
    const unmapped: string[] = [];
    scene.traverse((o) => {
      if ((o as Mesh).isMesh && !partOf(o)) {
        o.userData.sfUnmapped = true;
        o.visible = false;
        unmapped.push(o.name || t('weaponModel.k1'));
      }
    });
    if (unmapped.length)
      useStore.getState().log(
        t('weaponModel.k2', {
          v0: useStore.getState().weaponId,
          v1: unmapped.length,
          v2: unmapped.join('、'),
        }),
      );
    // Sub-parts: named transforms under each part that carry geometry.
    const subParts: Record<string, { path: string; name: string; depth: number }[]> = {};
    for (const p of preview.parts)
      subParts[p.id] = (p.transforms ?? []).flatMap((t) =>
        t.path
          ? [
              {
                path: t.path,
                name: t.name,
                depth: t.path.split('/').length - 1,
              },
            ]
          : [],
      );
    useStore.setState({ subParts });
    editableMaterials(scene, materials.current);
    window.dispatchEvent(new Event(SCENE_EVENT));
    return () => {
      viewRefs.root = null;
    };
  }, [scene, preview]);

  // Apply textures and parameters from the document over the game's own.
  useEffect(() => {
    let cancelled = false;
    const keep = new Set<string>();
    const loads: Promise<unknown>[] = [];
    for (const [name, mat] of materials.current) {
      for (const ch of CHANNELS) {
        compositors.current.get(`${name}|${ch}`)?.dispose();
        compositors.current.delete(`${name}|${ch}`);
      }
      loads.push(
        ...paintMaterial(
          mat,
          name,
          doc,
          preview,
          keep,
          () => cancelled,
          (path) => useStore.getState().log(t('weaponModel.k3', { v0: path })),
          (ch, base, layers) => {
            const c = new Compositor(base, layers);
            compositors.current.set(`${name}|${ch}`, c);
            return c.texture;
          },
        ),
      );
    }
    // Once the materials hold this document's textures, free the ones nothing uses any more (replaced or undone images).
    void Promise.allSettled(loads).then(() => !cancelled && releaseTextures(keep));
    return () => {
      cancelled = true;
    };
  }, [doc, preview]);

  useEffect(() => () => compositors.current.forEach((c) => c.dispose()), []);

  // Leaving this weapon frees its GPU copies (geometry, the editable materials) and drops the previous glTF from the
  // loader cache; a later visit reloads it. Disposed objects that are drawn again are simply re-uploaded (StrictMode).
  useEffect(() => {
    if (shownGlb && shownGlb !== preview.glb) useGLTF.clear(fileUrl(shownGlb));
    shownGlb = preview.glb;
    const mats = materials.current;
    return () => {
      scene.traverse((o) => (o as Mesh).geometry?.dispose());
      mats.forEach((m) => m.dispose());
    };
  }, [scene, preview.glb]);

  // Part layout (absolute local values; null keeps the game's) and visibility: hidden (layout) and switched-off parts.
  useEffect(() => {
    scene.traverse((o) => {
      const v = viewRefs.display.get(o);
      if (!v) return;
      o.position.copy(v.position);
      o.quaternion.copy(v.quaternion);
      o.scale.copy(v.scale);
      if (o.userData.sfNode && !o.userData.sfUnmapped) o.visible = true;
    });
    for (const t of doc.parts ?? []) {
      if (!t) continue;
      const o = partTarget(t.part ?? '', t.path);
      const vanilla = o && viewRefs.vanilla.get(o);
      if (!o || !vanilla) continue;
      const r = unityToThree(poseWith(vanilla, t));
      o.position.copy(r.position).add(displayOffset(o));
      o.quaternion.copy(r.quaternion);
      o.scale.copy(r.scale);
      if (t.visible === false) o.visible = false;
    }
    const ed = editorOf(doc);
    for (const [part, nodes] of viewRefs.partNodes) {
      if (ed.enabled[part] === false) nodes.forEach((n) => (n.visible = false));
    }
  }, [doc, scene]);

  const lastDraw = useRef(0);
  useFrame(() => {
    const now = performance.now();
    if (!compositors.current.size || now - lastDraw.current < 1000 / 30) return;
    lastDraw.current = now;
    compositors.current.forEach((c) => c.draw());
  });

  const onClick = (e: ThreeEvent<MouseEvent>) => {
    if (e.delta > 4 || quiet()) return;
    e.stopPropagation();
    const s = useStore.getState();
    if (s.snapToSurface && mode === 'attach' && s.selection?.kind === 'attachment') {
      window.dispatchEvent(new CustomEvent('sf:surface', { detail: e }));
      return;
    }
    const part = partOf(e.object);
    if (!part) return;
    // Clicking the selected part again drills one level down into the child transform under the cursor.
    const chain = pathChain(part, e.object);
    const sel = s.selection;
    let sub: string | null = null;
    if (sel?.kind === 'part' && sel.id === part && chain.length) sub = chain[Math.min(chain.indexOf(sel.sub ?? null) + 1, chain.length - 1)];
    select({ kind: 'part', id: part, sub });
  };

  return <primitive object={scene} onClick={onClick} />;
}

// Ammo parts sit inside the gun, and some of their nodes rest wherever the game's animator leaves them (an ejected
// shell's bone can rest well away from the gun). The preview lines every ammo piece up in one row just below the gun
// instead, each piece on its own. Display only: the offset is baked into `viewRefs.display` at load, never stored in
// the document and never applied in game. From the muzzle backwards: speed loader, whole bullets, empty shells.
const AMMO_ORDER = ['ammo_speedloader', 'ammo_bullet', 'ammo_shell'];

function layOutAmmo() {
  viewRefs.root?.updateMatrixWorld(true);
  const gun = new Box3();
  for (const [part, nodes] of viewRefs.partNodes) if (!AMMO_ORDER.includes(part)) nodes.forEach((n) => gun.expandByObject(n));
  if (gun.isEmpty()) return;
  const gap = Math.min(0.03, Math.max(0.01, gun.getSize(new Vector3()).length() * 0.04));
  // One item per outermost node: a node inside another moved node moves with it.
  const items = AMMO_ORDER.flatMap((part) => {
    const nodes = viewRefs.partNodes.get(part) ?? [];
    const set = new Set(nodes);
    return nodes
      .filter((n) => {
        for (let p = n.parent; p; p = p.parent) if (set.has(p)) return false;
        return true;
      })
      .map((n) => ({ n, box: new Box3().expandByObject(n) }))
      .filter((r) => !r.box.isEmpty());
  });
  const height = Math.max(0, ...items.map((r) => r.box.max.y - r.box.min.y));
  const bottom = gun.min.y - gap - height;
  const centerX = (gun.min.x + gun.max.x) / 2;
  let cursor = gun.min.z; // three -Z is the muzzle (Unity +Z)
  for (const { n, box } of items) {
    const delta = new Vector3(centerX - (box.min.x + box.max.x) / 2, bottom - box.min.y, cursor - box.min.z);
    cursor += box.max.z - box.min.z + gap / 2;
    const target = n.getWorldPosition(new Vector3()).add(delta);
    n.position.copy(n.parent!.worldToLocal(target));
    n.updateMatrixWorld(true);
  }
}
