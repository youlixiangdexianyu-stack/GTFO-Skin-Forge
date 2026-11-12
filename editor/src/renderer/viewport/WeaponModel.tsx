import { useGLTF } from '@react-three/drei';
import { useFrame, type ThreeEvent } from '@react-three/fiber';
import { useEffect, useMemo, useRef } from 'react';
import { CanvasTexture, Mesh, Vector3, SRGBColorSpace, type MeshStandardMaterial, type Object3D } from 'three';
import { baseTexturePath, drawLayer, LayerSource, loadImage } from '../frames';
import { hasLivery } from '../livery';
import { LiveryPreview } from './liveryPreview';
import { fileUrl, type DynamicLayer, type TextureChannel, type WeaponPreview } from '../protocol';
import { editorOf, materialOf, useStore } from '../store';
import { poseWith, unityDirToThree, unityToThree } from '../space';
import { holdTextures, releaseTextures } from './textures';
import { editableMaterials, paintMaterial, restoreUnityNames } from './weaponScene';
import { displayOffset, hiddenMeshes, partOf, partTarget, pathChain, playTime, registerTransforms, quiet, SCENE_EVENT, viewRefs } from './refs';
import { t } from '../i18n';
import { withVariant } from '../variants';

/** Live composite of a base texture plus its dynamic layers (the 3D decal preview). */
class Compositor {
  canvas = document.createElement('canvas');
  texture = new CanvasTexture(this.canvas);
  /** Per layer id: its source and what it was loaded from. Kept across edits that do not change what it plays. */
  private sources = new Map<string, { key: string; src: LayerSource }>();
  private order: LayerSource[] = [];
  private basePath: string | null = null;
  private base: CanvasImageSource | null = null;
  private livery: LiveryPreview | null = null;
  private fill = '#ffffff';
  private start = performance.now();
  /** What the canvas shows: the layers' frames and the livery's redraw; `dirty` after an edit or a base load. */
  private drawn: (CanvasImageSource | null)[] = [];
  private drawnLivery = -1;
  private dirty = true;

  constructor() {
    this.texture.flipY = false;
    this.texture.colorSpace = SRGBColorSpace;
    this.canvas.width = this.canvas.height = 1024;
  }

  /** Takes the document's current base (a texture path, or the live livery) and layers; placement and timing edits keep the loaded sources (no reload, no flicker). */
  set(ch: TextureChannel, base: string | LiveryPreview | null, layers: DynamicLayer[]) {
    this.dirty = true;
    // Without a base texture the plugin draws on white (albedo) or black, so the preview does too.
    this.fill = ch === 'albedo' ? '#ffffff' : '#000000';
    this.livery = base instanceof LiveryPreview ? base : null;
    if (typeof base !== 'string') {
      this.basePath = null;
      this.base = this.livery?.albedo ?? null;
    } else if (base !== this.basePath) {
      const basePath = base;
      this.basePath = basePath;
      this.base = null;
      void loadImage(basePath).then((img) => this.basePath === basePath && ((this.base = img), (this.dirty = true)), () => undefined);
    }
    const next = new Map<string, { key: string; src: LayerSource }>();
    this.order = layers.map((l) => {
      const key = JSON.stringify([l.kind, l.sources, l.text]);
      const old = this.sources.get(l.id!);
      const entry = old?.key === key ? old : { key, src: new LayerSource(l) };
      entry.src.layer = l;
      next.set(l.id!, entry);
      return entry.src;
    });
    for (const [id, e] of this.sources) if (next.get(id) !== e) e.src.dispose();
    this.sources = next;
  }

  /** Redraws and uploads only when what it shows changed: an edit, a new frame, a playing video, a livery redraw. */
  draw() {
    const frames = this.order.map((src) => {
      const l = src.layer;
      const time = playTime(l.trigger, l.play, l.playSeconds, src.duration, this.start);
      return time === null ? null : src.at(time);
    });
    const livery = this.livery?.version ?? -1;
    const same = !this.dirty && livery === this.drawnLivery && frames.every((f, i) => f === this.drawn[i] && !(f instanceof HTMLVideoElement));
    if (same && frames.length === this.drawn.length) return;
    this.dirty = false;
    this.drawn = frames;
    this.drawnLivery = livery;
    const ctx = this.canvas.getContext('2d')!;
    const W = this.canvas.width;
    const H = this.canvas.height;
    ctx.fillStyle = this.fill;
    ctx.fillRect(0, 0, W, H);
    if (this.base) ctx.drawImage(this.base, 0, 0, W, H);
    frames.forEach((f, i) => f && drawLayer(ctx, f, this.order[i].layer, W, H));
    this.texture.needsUpdate = true;
  }

  dispose() {
    this.sources.forEach((e) => e.src.dispose());
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
  const edited = useStore((s) => s.doc);
  // The selected colour variant's look (variants.ts).
  const doc = useMemo(() => withVariant(edited), [edited]);
  const fileEpoch = useStore((s) => s.fileEpoch);
  const select = useStore((s) => s.select);
  const materials = useRef(new Map<string, MeshStandardMaterial>());
  const compositors = useRef(new Map<string, Compositor>());
  const liveries = useRef(new Map<string, LiveryPreview>());
  /** Texture keys of each paint still held (holdTextures), oldest first; the last finished one is what the materials show. */
  const painted = useRef<Set<string>[]>([]);
  /** Meshes the part layout hides (restored before each re-apply). */
  const layoutHidden = useRef(new Set<Mesh>());

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
    layOutAmmo(preview.parts);
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
          v2: unmapped.join(', '),
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
    holdTextures(keep);
    painted.current.push(keep);
    const loads: Promise<unknown>[] = [];
    const used = new Set<string>();
    // Livery redraws follow the part layout too (a decal sits on its part's root).
    const layout = JSON.stringify(doc.parts ?? []);
    for (const [name, mat] of materials.current) {
      const m = materialOf(doc, name);
      let livery = liveries.current.get(name);
      if (hasLivery(m)) {
        if (!livery) liveries.current.set(name, (livery = new LiveryPreview(name)));
        livery.update(m!, baseTexturePath(doc, preview, name, 'albedo'), baseTexturePath(doc, preview, name, 'metallicSmoothness'), `${layout}|${fileEpoch}`);
      } else if (livery) {
        livery.dispose();
        liveries.current.delete(name);
        livery = undefined;
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
            const key = `${name}|${ch}`;
            let c = compositors.current.get(key);
            if (!c) compositors.current.set(key, (c = new Compositor()));
            c.set(ch, ch === 'albedo' && livery ? livery : base, layers);
            used.add(key);
            return c.texture;
          },
          livery && ((ch) => livery.texture(ch)),
        ),
      );
    }
    for (const [key, c] of compositors.current)
      if (!used.has(key)) {
        c.dispose();
        compositors.current.delete(key);
      }
    // Once the materials hold this document's textures, let go of the earlier paints: their textures that nothing uses any
    // more (replaced or undone images) are freed.
    void Promise.allSettled(loads).then(() => !cancelled && releaseTextures(...painted.current.splice(0, painted.current.indexOf(keep))));
    return () => {
      cancelled = true;
    };
  }, [doc, preview, fileEpoch]);

  useEffect(
    () => () => {
      compositors.current.forEach((c) => c.dispose());
      compositors.current.clear();
      liveries.current.forEach((l) => l.dispose());
      releaseTextures(...painted.current.splice(0));
    },
    [],
  );

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
    layoutHidden.current.forEach((m) => (m.visible = true));
    layoutHidden.current.clear();
    scene.traverse((o) => {
      const v = viewRefs.display.get(o);
      if (!v) return;
      o.position.copy(v.position);
      o.quaternion.copy(v.quaternion);
      o.scale.copy(v.scale);
      if (o.userData.sfNode && !o.userData.sfUnmapped && !o.userData.sfExtraAmmo) o.visible = true;
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
      if (t.visible === false)
        for (const m of hiddenMeshes(t.part ?? '', o)) {
          m.visible = false;
          layoutHidden.current.add(m);
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
    if ((!compositors.current.size && !liveries.current.size) || now - lastDraw.current < 1000 / 30) return;
    lastDraw.current = now;
    // An animated decal redraws its livery when its frame changes; the composites over it follow its version.
    liveries.current.forEach((l) => l.tick());
    compositors.current.forEach((c) => c.draw());
  });

  const onClick = (e: ThreeEvent<MouseEvent>) => {
    if (e.delta > 4 || quiet()) return;
    e.stopPropagation();
    const s = useStore.getState();
    if (s.snapToSurface && s.selection?.kind === 'attachment') {
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

// Ammo as the backend lays it out (Catalog.AmmoLayout, shared with the OBJ export): one piece per ammo part in a row
// below the gun, the other rounds hidden. Display only: the offset is baked into `viewRefs.display` at load, never stored
// in the document and never applied in game. A part with its own root transform (the ejected casing) moves by that root,
// so the gizmo and layout edits sit on the piece shown.
function layOutAmmo(parts: WeaponPreview['parts']) {
  const root = viewRefs.root;
  if (!root) return;
  root.updateMatrixWorld(true);
  for (const { ammo } of parts) {
    if (!ammo) continue;
    for (const name of ammo.hidden) {
      const n = root.getObjectByName(name);
      if (!n) continue;
      n.userData.sfExtraAmmo = true;
      n.visible = false;
    }
    const n = root.getObjectByName(ammo.node);
    if (!n?.parent) continue;
    n.position.copy(n.parent.worldToLocal(n.getWorldPosition(new Vector3()).add(unityDirToThree(ammo.offset))));
    n.updateMatrixWorld(true);
  }
}
