// The motion preview of the 人物模型 tab: a clip plays on a hidden body (the canonical player skeleton the action file itself
// carries, the rig every clip is written for) and the imported model copies that body's pose each frame, as the game copies the
// player's body (ForgeCore PoseCopy): the links come from ForgeCore's HumanRig through the backend (character.links) and the copy
// is the site's port of PoseCopy (imported-skin-pose.ts). Two groups ship with the editor (Tools/Models/stage-skin-motions.ts):
// the game's own player actions and Mixamo clips ForgeEmote converted, both on the same rig and read by the same loader.
import { AnimationMixer, type AnimationAction, type Object3D } from 'three';
import { GLTFLoader, type GLTF } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { createSkinPose } from '../../../../../../Web/src/shared/render/imported-skin-pose';
import { friendly } from '../errors';
import { call } from '../protocol';
import { useStore } from '../store';
import { boneOf, modelScene, restPose } from './model';
import motions from './motions.json';
import { matchOf, useCharacter } from './state';

export type MotionGroup = 'game' | 'mixamo';
export interface Motion {
  id: string;
  group: MotionGroup;
  loop: boolean;
}
export const MOTIONS: Motion[] = [...motions.game.map((m) => ({ ...m, group: 'game' as const })), ...motions.mixamo.map((m) => ({ ...m, group: 'mixamo' as const }))];

const BUNDLED = import.meta.glob<string>('../motions/*.glb', { eager: true, query: '?url', import: 'default' });
export const motionShipped = (id: string) => !!BUNDLED[`../motions/${id}.glb`];

/** A clip ready to play on the current model: its length, and `sample` poses the model at a moment of it. */
export interface ClipPlayer {
  id: string;
  duration: number;
  /** The hidden body the clip plays on (the canonical joints by name). */
  body: Object3D;
  sample(time: number): void;
}

// The file's bytes once; each player reads its own copy, so its hidden body starts at rest.
const files = new Map<string, Promise<ArrayBuffer>>();
const fileOf = (id: string): Promise<GLTF> => {
  let bytes = files.get(id);
  if (!bytes) {
    bytes = fetch(BUNDLED[`../motions/${id}.glb`]).then((r) => r.arrayBuffer());
    files.set(id, bytes);
    bytes.catch(() => files.delete(id));
  }
  // An action file is plain glTF: the canonical skeleton's nodes and one clip, as the site's player reads it.
  return bytes.then((b) => new GLTFLoader().parseAsync(b, ''));
};

/** The clip `id` on the model as it is now (the match in the state, the model at rest); null with a toast when it cannot play. */
export async function clipPlayer(id: string): Promise<ClipPlayer | null> {
  const model = modelScene();
  if (!model) return null;
  try {
    const file = await fileOf(id);
    const body = file.scene;
    const fingers = !!body.getObjectByName('leftThumb1');
    const match = matchOf(useCharacter.getState());
    const { links, reach } = await call('character.links', { bones: match, fingers });
    // The links the copy keeps, renumbered among themselves (a link never points at one it leaves out).
    const kept = links.flatMap((l, j) => (l.skip ? [] : [j]));
    const index = new Map(kept.map((j, k) => [j, k]));
    const modelBones: Object3D[] = [];
    const hostBones: Object3D[] = [];
    for (const j of kept) {
      const bone = boneOf(match[links[j].joint]);
      const host = body.getObjectByName(links[j].joint);
      if (!bone || !host) throw new Error(`no bone for ${links[j].joint}`);
      modelBones.push(bone);
      hostBones.push(host);
    }
    restPose();
    const pose = createSkinPose({
      links: kept.map((j) => ({
        toward: links[j].toward >= 0 ? index.get(links[j].toward)! : -1,
        from: links[j].from >= 0 ? index.get(links[j].from)! : -1,
        offset: [0, 0, 0] as const,
      })),
      modelBones,
      modelRoot: model,
      hostBones,
      hostRoot: body,
      reach: reach.filter((r) => r.every((j) => index.has(j))).map((r) => r.map((j) => index.get(j)!) as [number, number, number, number]),
    });
    if (!pose) throw new Error('the hips stand too low');
    const mixer = new AnimationMixer(body);
    const clip = file.animations[0];
    const action: AnimationAction = mixer.clipAction(clip);
    action.play();
    return {
      id,
      duration: clip.duration,
      body,
      sample(time) {
        action.time = Math.min(Math.max(time, 0), clip.duration);
        mixer.update(0);
        body.updateMatrixWorld(true);
        pose.apply();
      },
    };
  } catch (e) {
    useStore.getState().toast('error', friendly(e));
    return null;
  }
}
