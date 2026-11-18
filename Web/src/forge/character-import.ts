import {composeMatrix, invertAffine, multiplyMatrices, identityMatrix, transformPoint, type Matrix16} from './affine-matrix.ts';
import {convertGlbModel, splitGlb, type ConvertedGlb, type GlbJson, type ImportedImageCodec} from './imported-model-glb.ts';

// A character model for the ForgeSkin character tab (Docs/forge-skin/PLAN.md §4.10): a `.glb` or `.vrm` turned into the character
// format's model files by the one converter (imported-model-glb.ts, character mode), with what the file itself says about the
// character filled in for the author to check: the humanoid bone map, spring bones and their colliders, MToon settings kept raw, and
// the credit its licence asks for. A VRM 0.x file is first brought to the VRM 1.0 shape in memory (upgradeVrm); everything after that
// reads only the 1.0 shape. A plain `.glb` carries none of it and is matched by bone names in the editor. Data and codes only.

type Vec3 = [number, number, number];

/** The VRM 1.0 shape of what the character tab reads (VRMC_vrm humanoid and meta, VRMC_springBone, VRMC_materials_mtoon). */
export interface VrmCharacter {
    /** VRM 1.0 human bone name to node. */
    humanBones: Record<string, number>;
    springs: {
        name: string;
        joints: {node: number; hitRadius?: number; stiffness?: number; gravityPower?: number; dragForce?: number}[];
        colliderGroups: number[];
    }[];
    /** Offsets and tails in the node's own space (glTF axes). */
    colliders: {node: number; offset: Vec3; radius: number; tail?: Vec3}[];
    colliderGroups: {name: string; colliders: number[]}[];
    meta: {name: string; authors: string[]; licenseUrl: string; creditNotation: 'required' | 'unnecessary'};
    /** glTF material index to its MToon settings as the file has them. */
    mtoon: Record<number, unknown>;
    /** The model faces -Z (VRM 0.x) and is turned half round to face +Z as VRM 1.0 does. */
    turn: boolean;
}

const record = (value: unknown): Record<string, unknown> =>
    value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
const list = (value: unknown): unknown[] => (Array.isArray(value) ? value : []);
const num = (value: unknown): number | undefined => (typeof value === 'number' && Number.isFinite(value) ? value : undefined);
const text = (value: unknown): string => (typeof value === 'string' ? value : '');
const vec = (value: unknown): Vec3 => {
    if (Array.isArray(value)) return [num(value[0]) ?? 0, num(value[1]) ?? 0, num(value[2]) ?? 0];
    const v = record(value);
    return [num(v.x) ?? 0, num(v.y) ?? 0, num(v.z) ?? 0];
};

/** VRM 0.x licence names (meta.licenseName) to the licence page they stand for. */
const licenceUrls: Record<string, string> = {
    CC0: 'https://creativecommons.org/publicdomain/zero/1.0/',
    CC_BY: 'https://creativecommons.org/licenses/by/4.0/',
    CC_BY_NC: 'https://creativecommons.org/licenses/by-nc/4.0/',
    CC_BY_SA: 'https://creativecommons.org/licenses/by-sa/4.0/',
    CC_BY_NC_SA: 'https://creativecommons.org/licenses/by-nc-sa/4.0/',
    CC_BY_ND: 'https://creativecommons.org/licenses/by-nd/4.0/',
    CC_BY_NC_ND: 'https://creativecommons.org/licenses/by-nc-nd/4.0/'
};

/**
 * The file's VRM data in the VRM 1.0 shape, or null for a file without VRM. VRM 0.x (extensions.VRM) is upgraded as three-vrm and the
 * VRM migration notes read it: human bones keep their names except the thumb, whose three 0.x bones (proximal, intermediate, distal) are
 * 1.0's metacarpal, proximal and distal; every root of a 0.x spring group becomes 1.0 springs down its descendants (the first child
 * continues a spring, every other child starts one); `stiffiness` is 1.0's `stiffness`; a collider group's spheres become sphere
 * colliders on its node with the offset's Z negated (0.x writes it mirrored); the licence comes from licenseName (CC0 needs no credit,
 * every other licence is credited unless its permission page says `credit=unnecessary`); and the model, which 0.x builds facing -Z, is turned.
 */
export function upgradeVrm(json: GlbJson): VrmCharacter | null {
    const extensions = record(json.extensions),
        nodes = json.nodes ?? [];
    const mtoon: Record<number, unknown> = {};
    if (extensions.VRMC_vrm) {
        const vrm = record(extensions.VRMC_vrm),
            spring = record(extensions.VRMC_springBone),
            meta = record(vrm.meta);
        const humanBones: Record<string, number> = {};
        for (const [name, bone] of Object.entries(record(record(vrm.humanoid).humanBones))) {
            const node = num(record(bone).node);
            if (node !== undefined) humanBones[name] = node;
        }
        (json.materials ?? []).forEach((material, index) => {
            const raw = record(material.extensions).VRMC_materials_mtoon;
            if (raw) mtoon[index] = raw;
        });
        return {
            humanBones,
            springs: list(spring.springs).map(s => ({
                name: text(record(s).name),
                joints: list(record(s).joints).flatMap(j => {
                    const joint = record(j),
                        node = num(joint.node);
                    return node === undefined
                        ? []
                        : [
                              {
                                  node,
                                  hitRadius: num(joint.hitRadius),
                                  stiffness: num(joint.stiffness),
                                  gravityPower: num(joint.gravityPower),
                                  dragForce: num(joint.dragForce)
                              }
                          ];
                }),
                colliderGroups: list(record(s).colliderGroups).flatMap(g => (num(g) === undefined ? [] : [g as number]))
            })),
            colliders: list(spring.colliders).flatMap(c => {
                const node = num(record(c).node),
                    shape = record(record(c).shape),
                    sphere = record(shape.sphere),
                    capsule = record(shape.capsule);
                if (node === undefined) return [];
                if (shape.capsule) return [{node, offset: vec(capsule.offset), radius: num(capsule.radius) ?? 0, tail: vec(capsule.tail)}];
                return [{node, offset: vec(sphere.offset), radius: num(sphere.radius) ?? 0}];
            }),
            colliderGroups: list(spring.colliderGroups).map(g => ({
                name: text(record(g).name),
                colliders: list(record(g).colliders).flatMap(c => (num(c) === undefined ? [] : [c as number]))
            })),
            meta: {
                name: text(meta.name),
                authors: list(meta.authors).map(text).filter(Boolean),
                licenseUrl: text(meta.licenseUrl),
                creditNotation: meta.creditNotation === 'unnecessary' ? 'unnecessary' : 'required'
            },
            mtoon,
            turn: false
        };
    }
    if (!extensions.VRM) return null;
    const vrm = record(extensions.VRM),
        meta = record(vrm.meta),
        secondary = record(vrm.secondaryAnimation);
    const thumbs: Record<string, string> = {Proximal: 'Metacarpal', Intermediate: 'Proximal', Distal: 'Distal'};
    const humanBones: Record<string, number> = {};
    for (const entry of list(record(vrm.humanoid).humanBones)) {
        const bone = text(record(entry).bone),
            node = num(record(entry).node);
        if (!bone || node === undefined) continue;
        humanBones[bone.replace(/^(left|right)Thumb(Proximal|Intermediate|Distal)$/, (_, side: string, part: string) => side + 'Thumb' + thumbs[part])] = node;
    }
    // A collider group's spheres, one collider each, and the group listing them.
    const colliders: VrmCharacter['colliders'] = [],
        colliderGroups: VrmCharacter['colliderGroups'] = [];
    for (const group of list(secondary.colliderGroups)) {
        const node = num(record(group).node),
            members: number[] = [];
        for (const collider of list(record(group).colliders)) {
            if (node === undefined) continue;
            const [x, y, z] = vec(record(collider).offset);
            members.push(colliders.length);
            colliders.push({node, offset: [x, y, 0 - z], radius: num(record(collider).radius) ?? 0});
        }
        colliderGroups.push({name: '', colliders: members});
    }
    // Every root's descendants as springs: the first child goes on with the spring, every other child starts its own.
    const springs: VrmCharacter['springs'] = [];
    for (const group of list(secondary.boneGroups)) {
        const g = record(group);
        const params = {hitRadius: num(g.hitRadius), stiffness: num(g.stiffiness), gravityPower: num(g.gravityPower), dragForce: num(g.dragForce)};
        const groups = list(g.colliderGroups).flatMap(c => (num(c) === undefined ? [] : [c as number]));
        const walk = (start: number) => {
            const joints: number[] = [];
            for (let at: number | undefined = start; at !== undefined && nodes[at]; at = nodes[at]!.children?.[0]) {
                joints.push(at);
                for (const other of (nodes[at]!.children ?? []).slice(1)) walk(other);
            }
            springs.push({name: text(g.comment), joints: joints.map(node => ({node, ...params})), colliderGroups: groups});
        };
        for (const root of list(g.bones)) if (num(root) !== undefined && nodes[root as number]) walk(root as number);
    }
    const properties = list(vrm.materialProperties);
    (json.materials ?? []).forEach((material, index) => {
        const raw = properties.find(p => text(record(p).name) === text(material.name));
        if (raw) mtoon[index] = raw;
    });
    const licence = text(meta.licenseName);
    return {
        humanBones,
        springs,
        colliders,
        colliderGroups,
        meta: {
            name: text(meta.title),
            authors: text(meta.author) ? [text(meta.author)] : [],
            licenseUrl: licenceUrls[licence] ?? text(meta.otherLicenseUrl),
            creditNotation: licence === 'CC0' || /[?&]credit=unnecessary\b/.test(text(meta.otherPermissionUrl)) ? 'unnecessary' : 'required'
        },
        mtoon,
        turn: true
    };
}

/** VRM 1.0 human bones to the canonical humanoid (Web/src/generated/rig HumanJoints, HumanFingerJoints). */
const canonicalBody: Record<string, string> = {hips: 'hip', spine: 'spine0', chest: 'spine1', upperChest: 'spine2', neck: 'neck', head: 'head'};
for (const side of ['left', 'right'])
    for (const part of ['Shoulder', 'UpperArm', 'LowerArm', 'Hand', 'UpperLeg', 'LowerLeg', 'Foot']) canonicalBody[side + part] = side + part;
const fingerParts: Record<string, [string, string[]]> = {
    Thumb: ['Thumb', ['Metacarpal', 'Proximal', 'Distal']],
    Index: ['Index', ['Proximal', 'Intermediate', 'Distal']],
    Middle: ['Middle', ['Proximal', 'Intermediate', 'Distal']],
    Ring: ['Ring', ['Proximal', 'Intermediate', 'Distal']],
    Little: ['Pinky', ['Proximal', 'Intermediate', 'Distal']]
};
for (const side of ['left', 'right'])
    for (const [vrm, [ours, parts]] of Object.entries(fingerParts)) parts.forEach((part, at) => (canonicalBody[side + vrm + part] = side + ours + (at + 1)));
/** What the file's humanoid says: each canonical joint it names, with its node. */
export const vrmJoints = (vrm: VrmCharacter): [joint: string, node: number][] =>
    Object.entries(vrm.humanBones).flatMap(([name, node]) => (canonicalBody[name] ? [[canonicalBody[name], node] as [string, number]] : []));

/** A spring chain as the character document holds it (Mods/ForgeSkin/Core/Character.cs SpringChain). */
export interface CharacterSpringPrefill {
    id: string;
    kind: 'longHair' | 'shortHair' | 'skirt' | 'tail';
    anchor: string;
    strands: string[][];
    linkSide: boolean;
    loop: boolean;
    stiffness?: number;
    damping?: number;
    gravity?: number;
    radius?: number;
}
/** A collider as the character document holds it (SpringCapsule): on a canonical joint's bone, ends in that bone's own space (Unity axes). */
export interface CharacterCapsulePrefill {
    joint: string;
    a: Vec3;
    b: Vec3;
    radius: number;
}
export interface CharacterImport {
    model: ConvertedGlb;
    /** The file was VRM (0.x or 1.0); a plain glTF fills nothing below. */
    vrm: '0' | '1' | null;
    turned: boolean;
    springs: CharacterSpringPrefill[];
    capsules: CharacterCapsulePrefill[];
    /** Descriptor material name to its MToon settings as the file has them. */
    mtoon: Record<string, unknown>;
    /** The credit the licence asks for, null when it asks none. */
    credit: string | null;
}

// The character format's radius range (Core/Character.cs).
const maxRadius = 0.5,
    minRadius = 0.005;
const clamp = (value: number, low: number, high: number) => Math.min(high, Math.max(low, value));

/** Every node's world matrix (glTF axes, column-major) and parent. */
function nodeWorld(json: GlbJson): {world: Matrix16[]; parent: number[]} {
    const nodes = json.nodes ?? [],
        parent = nodes.map(() => -1),
        world: Matrix16[] = nodes.map(() => identityMatrix());
    nodes.forEach((node, index) => (node.children ?? []).forEach(child => (parent[child] = index)));
    const local = (index: number): Matrix16 => {
        const node = nodes[index]!;
        if (node.matrix?.length === 16) return [...node.matrix];
        return composeMatrix(node.translation ?? [0, 0, 0], node.rotation ?? [0, 0, 0, 1], node.scale ?? [1, 1, 1]);
    };
    const done = new Set<number>();
    const visit = (index: number): Matrix16 => {
        if (done.has(index)) return world[index]!;
        done.add(index);
        world[index] = parent[index]! < 0 ? local(index) : multiplyMatrices(visit(parent[index]!), local(index));
        return world[index]!;
    };
    nodes.forEach((_, index) => visit(index));
    return {world, parent};
}

/**
 * The springs as chains: springs of one name hang together from the nearest bone above all their roots. A set of three or more strands
 * whose roots stand round that bone at one height (no gap over 150 degrees) is a skirt, linked side to side and round, one chain per
 * height; a set hanging from the head is hair; anything else a tail. Values: stiffness, damping (VRM drag), gravity and radius averaged
 * over the set's first joints and brought into the format's ranges.
 */
function chains(json: GlbJson, vrm: VrmCharacter, named: (node: number) => string, head: number | undefined): CharacterSpringPrefill[] {
    const {world, parent} = nodeWorld(json);
    const ancestors = (node: number) => {
        const out: number[] = [];
        for (let at = parent[node]!; at >= 0; at = parent[at]!) out.push(at);
        return out;
    };
    const byName = new Map<string, VrmCharacter['springs']>();
    for (const spring of vrm.springs) if (spring.joints.length >= 2) byName.set(spring.name, [...(byName.get(spring.name) ?? []), spring]);
    const out: CharacterSpringPrefill[] = [];
    const at = (node: number): Vec3 => [world[node]![12]!, world[node]![13]!, world[node]![14]!];
    for (const [name, springs] of byName) {
        const roots = springs.map(s => s.joints[0]!.node);
        // The nearest bone above every root.
        let anchor = parent[roots[0]!]!;
        for (const root of roots.slice(1)) {
            const above = new Set(ancestors(root));
            while (anchor >= 0 && !above.has(anchor)) anchor = parent[anchor]!;
        }
        if (anchor < 0) continue;
        const hair = head !== undefined && (anchor === head || ancestors(anchor).includes(head));
        const centre = at(anchor);
        const values = (set: VrmCharacter['springs']) => {
            const mean = (pick: (j: VrmCharacter['springs'][number]['joints'][number]) => number | undefined) => {
                const known = set.map(s => pick(s.joints[0]!)).filter((v): v is number => v !== undefined);
                return known.length ? known.reduce((a, b) => a + b, 0) / known.length : undefined;
            };
            const stiffness = mean(j => j.stiffness),
                drag = mean(j => j.dragForce),
                gravity = mean(j => j.gravityPower),
                radius = mean(j => j.hitRadius);
            return {
                ...(stiffness !== undefined ? {stiffness: clamp(0.15 + stiffness * 0.2, 0, 1)} : {}),
                ...(drag !== undefined ? {damping: clamp(drag, 0, 1)} : {}),
                ...(gravity !== undefined ? {gravity: clamp(gravity, 0, 3)} : {}),
                ...(radius !== undefined ? {radius: clamp(radius, 0, maxRadius)} : {})
            };
        };
        const strand = (s: VrmCharacter['springs'][number]) => s.joints.map(j => named(j.node));
        // Skirt layers: a root standing on another one's place (within 2 cm) starts the next layer (a coat over a skirt).
        const layers: VrmCharacter['springs'][] = [];
        const near = (a: Vec3, b: Vec3) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]) <= 0.02;
        if (!hair && springs.length >= 3)
            for (const spring of springs) {
                const p = at(spring.joints[0]!.node);
                const layer = layers.find(l => !l.some(s => near(at(s.joints[0]!.node), p)));
                if (layer) layer.push(spring);
                else layers.push([spring]);
            }
        const angle = (s: VrmCharacter['springs'][number]) => {
            const p = at(s.joints[0]!.node);
            return Math.atan2(p[0] - centre[0], p[2] - centre[2]);
        };
        const round = (layer: VrmCharacter['springs']) => {
            if (layer.length < 3) return false;
            const angles = layer.map(angle).sort((a, b) => a - b);
            const gaps = angles.map((a, i) => (i ? a - angles[i - 1]! : a + 2 * Math.PI - angles[angles.length - 1]!));
            return Math.max(...gaps) < (150 * Math.PI) / 180;
        };
        if (layers.length && layers.every(round)) {
            layers.forEach((layer, index) =>
                out.push({
                    id: (name || 'skirt') + (layers.length > 1 ? ' ' + (index + 1) : ''),
                    kind: 'skirt',
                    anchor: named(anchor),
                    strands: [...layer].sort((a, b) => angle(a) - angle(b)).map(strand),
                    linkSide: true,
                    loop: true,
                    ...values(layer)
                })
            );
            continue;
        }
        const strands = springs.map(strand);
        const kind: CharacterSpringPrefill['kind'] = hair
            ? Math.max(...strands.map(s => s.length)) >= 4
                ? 'longHair'
                : 'shortHair'
            : /skirt/i.test(name)
              ? 'skirt'
              : 'tail';
        out.push({
            id: name || (hair ? 'hair' : 'spring'),
            kind,
            anchor: named(anchor),
            strands,
            linkSide: kind === 'skirt' && strands.length > 1,
            loop: false,
            ...values(springs)
        });
    }
    // Unique ids.
    const seen = new Map<string, number>();
    for (const chain of out) {
        const n = (seen.get(chain.id) ?? 0) + 1;
        seen.set(chain.id, n);
        if (n > 1) chain.id += ' ' + n;
    }
    return out;
}

/**
 * The colliders as capsules on canonical joints: a collider on a bone of the humanoid stays on it; one on another bone moves to its
 * nearest humanoid bone above. Several spheres on one bone become one capsule between the two farthest apart, at the largest radius.
 * Ends are in the bone's own space in Unity's axes (X mirrored), largest first.
 */
function capsules(json: GlbJson, vrm: VrmCharacter, jointOf: Map<number, string>): CharacterCapsulePrefill[] {
    const {world, parent} = nodeWorld(json);
    const perBone = new Map<number, {points: Vec3[]; radius: number; capsules: CharacterCapsulePrefill[]}>();
    const unity = ([x, y, z]: Vec3): Vec3 => [0 - x, y, z];
    for (const collider of vrm.colliders) {
        let bone = collider.node;
        while (bone >= 0 && !jointOf.has(bone)) bone = parent[bone]!;
        if (bone < 0) continue;
        // From the collider's node into its bone's space.
        const into = (point: Vec3): Vec3 =>
            bone === collider.node ? point : transformPoint(invertAffine(world[bone]!) ?? identityMatrix(), transformPoint(world[collider.node]!, point));
        const entry = perBone.get(bone) ?? {points: [], radius: 0, capsules: []};
        perBone.set(bone, entry);
        const radius = clamp(collider.radius, minRadius, maxRadius);
        if (collider.tail) entry.capsules.push({joint: jointOf.get(bone)!, a: unity(into(collider.offset)), b: unity(into(collider.tail)), radius});
        else {
            entry.points.push(into(collider.offset));
            entry.radius = Math.max(entry.radius, radius);
        }
    }
    const out: CharacterCapsulePrefill[] = [];
    for (const [bone, entry] of perBone) {
        out.push(...entry.capsules);
        if (!entry.points.length) continue;
        let a = entry.points[0]!,
            b = a,
            far = -1;
        for (const p of entry.points)
            for (const q of entry.points) {
                const d = Math.hypot(p[0] - q[0], p[1] - q[1], p[2] - q[2]);
                if (d > far) [far, a, b] = [d, p, q];
            }
        out.push({joint: jointOf.get(bone)!, a: unity(a), b: unity(b), radius: entry.radius});
    }
    out.sort((x, y) => y.radius - x.radius);
    return out;
}

/** The credit line a licence asks for: the work's name, its authors and the licence page. */
function credit(meta: VrmCharacter['meta']): string | null {
    if (meta.creditNotation !== 'required') return null;
    const parts = [meta.name ? '"' + meta.name + '"' : 'Character model', meta.authors.length ? 'by ' + meta.authors.join(', ') : ''].filter(Boolean);
    return parts.join(' ') + (meta.licenseUrl ? ' (' + meta.licenseUrl + ')' : '');
}

/**
 * The bones a VRM aim constraint (VRMC_node_constraint) turns toward a source bone: each rides the source's parent, the bone that points
 * along the same line (a shoulder or thigh helper aimed at the elbow or knee rides the upper arm or thigh). The game copies only the
 * humanoid pose and runs no constraint, so a helper left on its own parent stays at rest and drags its skin there. Roll and rotation
 * constraints are not followed: those helpers stay on their parent, which leaves out only the share of twist they spread.
 */
export function aimFollow(json: GlbJson): Map<number, number> {
    const nodes = json.nodes ?? [],
        parent = new Map<number, number>();
    nodes.forEach((node, index) => (node.children ?? []).forEach(child => parent.set(child, index)));
    const out = new Map<number, number>();
    nodes.forEach((node, index) => {
        const aim = record(record(record(record(node.extensions).VRMC_node_constraint).constraint).aim);
        const source = aim.source;
        if (typeof source === 'number' && parent.has(source) && parent.get(source) !== index) out.set(index, parent.get(source)!);
    });
    return out;
}

/** Converts a character `.glb` or `.vrm` and fills in what its VRM data says. Refusals are the converter's codes (ImportedModelError). */
export async function importCharacter(bytes: Uint8Array, codec: ImportedImageCodec): Promise<CharacterImport> {
    const {json} = splitGlb(bytes);
    const vrm = upgradeVrm(json);
    const used = json.extensionsUsed ?? [];
    const springNodes = vrm ? [...vrm.springs.flatMap(s => s.joints.map(j => j.node)), ...vrm.colliders.map(c => c.node)] : [];
    const model = await convertGlbModel(bytes, codec, {
        bones: vrm ? [...Object.values(vrm.humanBones), ...springNodes] : [],
        ignore: used.filter(name => /^VRM/.test(name) || name === 'KHR_materials_unlit'),
        turn: vrm?.turn ?? false,
        follow: aimFollow(json)
    });
    const nodes = json.nodes ?? [];
    const named = (node: number) => nodes[node]?.name ?? '';
    const known = new Set(model.descriptor.skin?.bones.map(bone => bone.name) ?? []);
    const jointOf = new Map<number, string>();
    for (const [joint, node] of vrm ? vrmJoints(vrm) : []) if (known.has(named(node))) jointOf.set(node, joint);
    const mtoon: Record<string, unknown> = {};
    for (const [index, raw] of Object.entries(vrm?.mtoon ?? {})) {
        const name = (json.materials ?? [])[Number(index)]?.name;
        const material = model.descriptor.materials.find(m => m.name === (typeof name === 'string' && name ? name : 'material ' + index));
        if (material) mtoon[material.name] = raw;
    }
    // A toon (MToon, VRM 0's unlit shaders) or unlit material has no metal or gloss, but its glTF fallback usually leaves the metallic
    // factor out, which glTF reads as fully metal: drawn by the gear shader it turns into a mirror. Those are matte dielectrics here; a
    // material the file shades as PBR (VRM 0's "Standard", plain glTF) keeps its own values.
    (json.materials ?? []).forEach((source, index) => {
        const raw = vrm?.mtoon[index];
        const unshaded = !!record(source.extensions).KHR_materials_unlit || (raw !== undefined && text(record(raw).shader) !== 'Standard');
        const material = model.descriptor.materials.find(m => m.name === (typeof source.name === 'string' && source.name ? source.name : 'material ' + index));
        if (unshaded && material && material.metalTexture < 0) [material.metallic, material.roughness] = [0, 1];
    });
    return {
        model,
        vrm: vrm ? (vrm.turn ? '0' : '1') : null,
        turned: vrm?.turn ?? false,
        springs: vrm ? chains(json, vrm, named, vrm.humanBones.head) : [],
        capsules: vrm ? capsules(json, vrm, jointOf) : [],
        mtoon,
        credit: vrm ? credit(vrm.meta) : null
    };
}
