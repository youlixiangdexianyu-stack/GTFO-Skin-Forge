import { useGLTF } from '@react-three/drei';
import { useFrame, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import { Box3, CanvasTexture, Color, Vector3, Mesh, MeshStandardMaterial, SRGBColorSpace, type Material, type Object3D, type Texture } from 'three';
import { baseTexturePath, drawLayer, LayerSource, loadImage } from '../frames';
import { CHANNELS, fileUrl, type DynamicLayer, type TextureChannel, type WeaponPreview } from '../protocol';
import { editorOf, materialOf, useStore } from '../store';
import { applyDelta, type Vec3 } from '../space';
import { loadTexture } from './textures';
import { commonAncestor, partOf, partTargets, quiet, SCENE_EVENT, viewRefs } from './refs';

const SLOT: Record<TextureChannel, 'map' | 'normalMap' | 'metalnessMap' | 'emissiveMap'> = {
  albedo: 'map',
  normal: 'normalMap',
  metallicSmoothness: 'metalnessMap',
  emissive: 'emissiveMap',
};

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
    const t = (performance.now() - this.start) / 1000;
    for (const [l, src] of this.sources) {
      const f = src.at(t);
      if (f) drawLayer(ctx, f, l, W, H);
    }
    this.texture.needsUpdate = true;
  }

  dispose() {
    this.sources.forEach(([, s]) => s.dispose());
    this.texture.dispose();
  }
}

export function WeaponModel({ preview }: { preview: WeaponPreview }) {
  const gltf = useGLTF(fileUrl(preview.glb));
  const scene = useMemo(() => {
    // Mark real glTF nodes (Unity transforms), as opposed to the extra meshes the loader makes for multi-material primitives.
    gltf.scene.traverse((o) => void (o.userData.sfNode = gltf.parser.associations.get(o)?.nodes !== undefined));
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
    viewRefs.partRoots.clear();
    viewRefs.nodeToPart.clear();
    for (const p of preview.parts) {
      const nodes = p.nodes.map((n) => scene.getObjectByName(n)).filter(Boolean) as Object3D[];
      viewRefs.partNodes.set(p.id, nodes);
      nodes.forEach((n) => viewRefs.nodeToPart.set(n, p.id));
      const root = commonAncestor(nodes);
      if (root) viewRefs.partRoots.set(p.id, root);
    }
    const pose = (o: Object3D) => ({ position: o.position.clone(), quaternion: o.quaternion.clone(), scale: o.scale.clone() });
    scene.traverse((o) => viewRefs.vanilla.set(o, pose(o)));
    layOutAmmo();
    scene.traverse((o) => viewRefs.display.set(o, pose(o)));
    // Sub-parts: named transforms under each part that carry geometry.
    const hasMesh = (o: Object3D) => {
      let found = false;
      o.traverse((c) => void (found ||= (c as Mesh).isMesh));
      return found;
    };
    const subParts: Record<string, { name: string; depth: number }[]> = {};
    for (const [part, root] of viewRefs.partRoots) {
      const list: { name: string; depth: number }[] = [];
      const walk = (o: Object3D, depth: number) => {
        for (const c of o.children) {
          if (!c.userData.sfNode || !c.name || !hasMesh(c)) continue;
          list.push({ name: c.name, depth });
          walk(c, depth + 1);
        }
      };
      walk(root, 0);
      subParts[part] = list;
    }
    useStore.setState({ subParts });
    const mats = materials.current;
    scene.traverse((o) => {
      const mesh = o as Mesh;
      if (!mesh.isMesh) return;
      const list = (Array.isArray(mesh.material) ? mesh.material : [mesh.material]) as Material[];
      const mine = list.map((m) => {
        let mat = mats.get(m.name);
        if (!mat) {
          mat = new MeshStandardMaterial({ name: m.name, color: 0xffffff, metalness: 1, roughness: 1 });
          mats.set(m.name, mat);
        }
        return mat;
      });
      mesh.material = Array.isArray(mesh.material) ? mine : mine[0];
      mesh.castShadow = mesh.receiveShadow = false;
    });
    window.dispatchEvent(new Event(SCENE_EVENT));
    return () => {
      viewRefs.root = null;
    };
  }, [scene, preview]);

  // Apply textures and parameters from the document over the game's own.
  useEffect(() => {
    let cancelled = false;
    for (const [name, mat] of materials.current) {
      const override = materialOf(doc, name);
      const params = { ...preview.params[name], ...Object.fromEntries(Object.entries(override?.params ?? {}).filter(([, v]) => v != null)) };
      const layers = (override?.layers ?? []).filter((l): l is DynamicLayer => !!l && l.visible !== false);
      for (const ch of CHANNELS) {
        const path = baseTexturePath(doc, preview, name, ch);
        const chLayers = layers.filter((l) => (l.channel ?? 'albedo') === ch);
        const key = `${name}|${ch}`;
        compositors.current.get(key)?.dispose();
        compositors.current.delete(key);
        if (chLayers.length) {
          const c = new Compositor(path, chLayers);
          compositors.current.set(key, c);
          setSlot(mat, ch, c.texture);
          continue;
        }
        if (!path) {
          setSlot(mat, ch, null);
          continue;
        }
        loadTexture(path, ch).then(
          (t) => !cancelled && setSlot(mat, ch, t),
          () => useStore.getState().log(`贴图读不了：${path}`),
        );
      }
      const tint = params.tint ?? [1, 1, 1, 1];
      mat.color.setRGB(tint[0], tint[1], tint[2]);
      // With a MetallicSmoothness map the scalars multiply it; without one, fall back to a neutral finish.
      const hasMs = !!baseTexturePath(doc, preview, name, 'metallicSmoothness');
      mat.metalness = params.metallic ?? (hasMs ? 1 : 0.4);
      mat.roughness = params.smoothness != null ? 1 - params.smoothness : hasMs ? 1 : 0.55;
      const on = params.emissiveEnabled ?? !!mat.emissiveMap;
      const c = params.emissiveColor ?? [1, 1, 1];
      const k = on ? (params.emissiveIntensity ?? 1) : 0;
      mat.emissive = new Color(c[0], c[1], c[2]);
      mat.emissiveIntensity = k;
      mat.needsUpdate = true;
    }
    return () => {
      cancelled = true;
    };
  }, [doc, preview]);

  useEffect(() => () => compositors.current.forEach((c) => c.dispose()), []);

  // Part layout (deltas on the vanilla pose) and visibility: hidden (layout) and switched-off parts.
  useEffect(() => {
    scene.traverse((o) => {
      const v = viewRefs.display.get(o);
      if (!v) return;
      o.position.copy(v.position);
      o.quaternion.copy(v.quaternion);
      o.scale.copy(v.scale);
      if (o.userData.sfNode) o.visible = true;
    });
    for (const t of doc.parts ?? []) {
      if (!t) continue;
      const delta = { position: (t.position ?? [0, 0, 0]) as Vec3, rotation: (t.rotation ?? [0, 0, 0]) as Vec3, scale: (t.scale ?? [1, 1, 1]) as Vec3 };
      for (const o of partTargets(t.part ?? '', t.transform)) {
        const r = applyDelta(viewRefs.display.get(o)!, delta);
        o.position.copy(r.position);
        o.quaternion.copy(r.quaternion);
        o.scale.copy(r.scale);
        if (t.visible === false) o.visible = false;
      }
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
    // Clicking the selected part again drills one level down into the sub-part under the cursor.
    const path: Object3D[] = [];
    const root = viewRefs.partRoots.get(part);
    for (let o: Object3D | null = e.object; o && o !== root; o = o.parent) if (o.userData.sfNode && o.name) path.unshift(o);
    const sel = s.selection;
    let sub: string | null = null;
    if (sel?.kind === 'part' && sel.id === part && path.length) {
      const i = sel.sub ? path.findIndex((o) => o.name === sel.sub) : -1;
      sub = path[Math.min(i + 1, path.length - 1)].name;
    }
    select({ kind: 'part', id: part, sub });
  };

  return <primitive object={scene} onClick={onClick} />;
}

// Ammo parts sit inside the gun; the preview lines them up just below the front of the gun instead. Display only: the
// offset is baked into `viewRefs.display` at load, never stored in the document and never applied in game.
// From the muzzle backwards: speed loader, whole bullet, empty shell, so in the side view the shell is on the left and
// the bullet on the right, bottoms aligned.
const AMMO_ORDER = ['ammo_speedloader', 'ammo_bullet', 'ammo_shell'];

function layOutAmmo() {
  viewRefs.root?.updateMatrixWorld(true);
  const gun = new Box3();
  for (const [part, nodes] of viewRefs.partNodes) if (!AMMO_ORDER.includes(part)) nodes.forEach((n) => gun.expandByObject(n));
  if (gun.isEmpty()) return;
  const gap = Math.min(0.03, Math.max(0.01, gun.getSize(new Vector3()).length() * 0.04));
  const rows = AMMO_ORDER.map((part) => {
    const nodes = viewRefs.partNodes.get(part) ?? [];
    const box = new Box3();
    nodes.forEach((n) => box.expandByObject(n));
    return { nodes, box };
  }).filter((r) => !r.box.isEmpty());
  const height = Math.max(0, ...rows.map((r) => r.box.max.y - r.box.min.y));
  const bottom = gun.min.y - gap - height;
  const centerX = (gun.min.x + gun.max.x) / 2;
  let cursor = gun.min.z; // three -Z is the muzzle (Unity +Z)
  for (const { nodes, box } of rows) {
    const delta = new Vector3(centerX - (box.min.x + box.max.x) / 2, bottom - box.min.y, cursor - box.min.z);
    cursor += box.max.z - box.min.z + gap / 2;
    const moved = new Set(nodes);
    for (const n of nodes) {
      let nested = false;
      for (let p = n.parent; p && !nested; p = p.parent) nested = moved.has(p);
      if (nested) continue;
      const target = n.getWorldPosition(new Vector3()).add(delta);
      n.position.copy(n.parent!.worldToLocal(target));
    }
  }
}

function setSlot(mat: MeshStandardMaterial, ch: TextureChannel, t: Texture | null) {
  const slot = SLOT[ch];
  mat[slot] = t;
  if (ch === 'metallicSmoothness') mat.roughnessMap = t;
  mat.needsUpdate = true;
}
