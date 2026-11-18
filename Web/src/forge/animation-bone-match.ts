// 认不在 `animation-skeletons.json` 里的人形骨架：名字拆成词，认出左右和部位，再沿父子链走出手臂、腿和脊柱。
// 只看名字和层级，不看来源，所以 Bip01、Unity 人形、UE、VRM、Daz 这类命名走同一条路；认不出就报缺什么，不猜。
interface Bone {
    name: string;
    parent: Bone | null;
    children: readonly Bone[];
}
type Side = 'left' | 'right';
type Part = 'hand' | 'foot' | 'head' | 'neck' | 'hip';

/** 名字拆成小写词：`Bip01 L UpperArm` → bip01 l upper arm，`thigh_l` → thigh l，`lThighBend` → l thigh bend。命名空间前缀不算。 */
function words(name: string): string[] {
    const own = name.slice(name.lastIndexOf(':') + 1).replace(/^mixamorig\d*/i, '');
    return own
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .toLowerCase()
        .split(/[^a-z0-9]+/)
        .filter(Boolean);
}
/** 这些词说明是辅助骨，不是身体的一段：走链时跳过。 */
const helper = /^(twist\d*|roll\d*|ik|fk|ctrl|ctl|pole|target|tgt|mch|end|nub|helper|attach|weapon|prop|socket|hair|skirt|breast|bust|tail|cloth)$/;
const digit = /^(thumb|index|middle|mid|ring|pinky|pink|little|finger\d*|toe\d*|toes|ball)$/;
const named: [Part, RegExp][] = [
    ['hand', /^(hand|wrist)$/],
    ['foot', /^(foot|ankle)$/],
    ['head', /^head$/],
    ['neck', /^neck\d*$/],
    ['hip', /^(hip|hips|pelvis)$/]
];
const isHelper = (bone: Bone) => words(bone.name).some(word => helper.test(word));
function sideOf(bone: Bone): Side | null {
    const all = words(bone.name);
    return all.includes('left') || all.includes('l') ? 'left' : all.includes('right') || all.includes('r') ? 'right' : null;
}
/** 编号之外的词，同一段拆成几根时（`forearm.L`、`forearm.L.001`）它们相同。 */
const stem = (bone: Bone) =>
    words(bone.name)
        .filter(word => !/^\d+$/.test(word))
        .join(' ');
function partOf(bone: Bone): Part | null {
    const all = words(bone.name);
    if (isHelper(bone) || all.some(word => digit.test(word))) return null;
    for (const [part, pattern] of named) if (all.some(word => pattern.test(word))) return part;
    return null;
}
/** 上一根身体骨，辅助骨跳过。 */
function above(bone: Bone): Bone | null {
    let at = bone.parent;
    while (at && isHelper(at)) at = at.parent;
    return at;
}
/** 四肢的上一段：同一段拆成的几根走到最上面。 */
function segmentAbove(bone: Bone): Bone | null {
    let at = above(bone);
    while (at?.parent && stem(at.parent) === stem(at)) at = at.parent;
    return at;
}
function allBones(root: Bone): Bone[] {
    const out: Bone[] = [root];
    for (let at = 0; at < out.length; at++) out.push(...out[at]!.children);
    return out;
}
/** 手下面最浅的那根某手指。 */
function fingerOf(hand: Bone, names: RegExp): Bone | undefined {
    const level = [...hand.children];
    for (let at = 0; at < level.length; at++) {
        const bone = level[at]!;
        if (words(bone.name).some(word => names.test(word))) return bone;
        level.push(...bone.children);
    }
    return undefined;
}

export interface BoneMatch<T> {
    bones: Map<string, T>;
    missing: string[];
}
/** 人形的 20 个规范关节（`HumanJoints`）加手掌的三根手指（有就带上，转换用它们量手掌朝向）。认不出的关节列在 `missing`。 */
export function matchHumanBones<T extends Bone>(root: T): BoneMatch<T> {
    const bones = allBones(root),
        found = new Map<string, Bone>(),
        missing = new Set<string>();
    const limb = (side: Side, end: Part, upper: string, lower: string, tip: string) => {
        const last = bones.find(bone => sideOf(bone) === side && partOf(bone) === end);
        const low = last && segmentAbove(last),
            high = low && segmentAbove(low);
        // 手往上两根就是小臂和上臂，脚往上两根就是小腿和大腿，都在同一侧：叫什么都行，链上的位置说了算。
        if (!last || !low || !high || sideOf(low) !== side || sideOf(high) !== side) {
            for (const joint of [upper, lower, tip]) missing.add(joint);
            return undefined;
        }
        found.set(tip, last);
        found.set(lower, low);
        found.set(upper, high);
        return high;
    };
    const arms = (['left', 'right'] as const).map(side => limb(side, 'hand', side + 'UpperArm', side + 'LowerArm', side + 'Hand'));
    const legs = (['left', 'right'] as const).map(side => limb(side, 'foot', side + 'UpperLeg', side + 'LowerLeg', side + 'Foot'));
    for (const side of ['left', 'right'] as const) {
        const hand = found.get(side + 'Hand');
        if (!hand) continue;
        for (const [joint, names] of [
            ['Index1', /^index$/],
            ['Middle1', /^(middle|mid)$/],
            ['Pinky1', /^(pinky|pink|little)$/]
        ] as const) {
            const bone = fingerOf(hand, names);
            if (bone) found.set(side + joint, bone);
        }
    }
    const legTop = legs[0] ?? legs[1];
    const hip = bones.find(bone => sideOf(bone) === null && partOf(bone) === 'hip') ?? (legTop && above(legTop)) ?? undefined;
    if (hip) found.set('hip', hip);
    else missing.add('hip');
    // 肩骨是手臂上面还带左右的那一根；没有肩骨的骨架，手臂直接挂在胸上，肩就算胸。
    let chest: Bone | undefined;
    (['left', 'right'] as const).forEach((side, index) => {
        const shoulder = arms[index] && above(arms[index]!);
        if (!shoulder) {
            missing.add(side + 'Shoulder');
            return;
        }
        found.set(side + 'Shoulder', shoulder);
        const under = sideOf(shoulder) ? above(shoulder) : shoulder;
        if (under && !chest) chest = under;
    });
    // 肩挂在脖子上的（Biped）：胸是脖子下面那根。
    if (chest && partOf(chest) === 'neck') chest = above(chest) ?? chest;
    const spine: Bone[] = [];
    for (let at = chest ?? null; at && at !== hip && !isHelper(at); at = at.parent) spine.unshift(at);
    if (!hip || !spine.length || spine[0]!.parent !== hip) for (const joint of ['spine0', 'spine1', 'spine2']) missing.add(joint);
    else {
        found.set('spine0', spine[0]!);
        found.set('spine1', spine[Math.floor((spine.length - 1) / 2)]!);
        found.set('spine2', spine[spine.length - 1]!);
    }
    const head = bones.find(bone => sideOf(bone) === null && partOf(bone) === 'head');
    const neck = head && above(head);
    if (head && neck) {
        found.set('head', head);
        found.set('neck', neck);
    } else {
        missing.add('head');
        missing.add('neck');
    }
    return {bones: found as Map<string, T>, missing: [...missing]};
}

const fingerNames = [
    ['Thumb', /^thumb$/],
    ['Index', /^index$/],
    ['Middle', /^(middle|mid)$/],
    ['Ring', /^ring$/],
    ['Pinky', /^(pinky|pink|little)$/]
] as const;
/** 一只手的规范手指关节（`HumanFingerJoints`，拇指到小指、每指三节）：每指取手下面最浅的那根，再沿子骨往下取两节，辅助骨跳过。
 * 某指不够三节就不给这根手指；手指全不全由调用方按自己的规则定。 */
export function matchHumanFingers<T extends Bone>(hand: T, side: Side): Map<string, T> {
    const found = new Map<string, Bone>();
    for (const [finger, names] of fingerNames) {
        const chain: Bone[] = [];
        for (let at = fingerOf(hand, names); at && chain.length < 3; at = at.children.find(child => !isHelper(child))) chain.push(at);
        if (chain.length === 3) chain.forEach((bone, index) => found.set(side + finger + (index + 1), bone));
    }
    return found as Map<string, T>;
}
