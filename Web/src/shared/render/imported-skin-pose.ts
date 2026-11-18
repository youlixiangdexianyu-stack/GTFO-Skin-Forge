import {Matrix3, Matrix4, Quaternion, Vector3, type Object3D} from 'three';

// 蒙皮导入模型在网页预览里随宿主骨动：和游戏端 ForgeVisual `PoseCopy`（PoseSolver.Correct/Unframe/Root）同一语义——
// 每根模型骨抄一根宿主骨的转动，保留自己的比例，根（髋）再抄宿主髋的高度与摇摆（按髋高比缩放）。
// 游戏的坐标是 Unity 左手系，预览是 X 取反的右手系；这里的解算全在 Unity 一侧做（进出各取反一次），所以「正面是 +Z」等约定原样成立。

const flipPoint = (v: Vector3) => new Vector3(-v.x, v.y, v.z);
const flipTurn = (q: Quaternion) => new Quaternion(q.x, -q.y, -q.z, q.w);

/** Unity 的 LookRotation：+Z 对准 forward、+Y 尽量朝 up。 */
function look(forward: Vector3, up: Vector3): Quaternion {
    if (forward.lengthSq() < 1e-12) return new Quaternion();
    const f = forward.clone().normalize();
    let r = new Vector3().crossVectors(up, f);
    if (r.lengthSq() < 1e-12) r = new Vector3().crossVectors(Math.abs(f.y) < 0.99 ? new Vector3(0, 1, 0) : new Vector3(1, 0, 0), f);
    r.normalize();
    return new Quaternion().setFromRotationMatrix(new Matrix4().makeBasis(r, new Vector3().crossVectors(f, r), f)).normalize();
}
/** 一根骨在身体静止坐标系里的朝向：沿骨方向，正面（+Z）为上；没有方向的骨取身体自己的坐标系。 */
function boneFrame(along: Vector3): Quaternion {
    if (along.lengthSq() < 1e-8) return new Quaternion();
    const a = along.clone().normalize();
    return look(a, Math.abs(a.z) > 0.8 ? new Vector3(0, 1, 0) : new Vector3(0, 0, 1));
}

export interface SkinPoseLink {
    toward: number;
    from: number;
    offset: readonly [number, number, number];
}
export interface SkinPoseInput {
    links: readonly SkinPoseLink[];
    /** 每个链接的模型骨；调用时模型必须在绑定姿态（静止姿态就是从这里读的）。 */
    modelBones: readonly Object3D[];
    modelRoot: Object3D;
    /** 每个链接的宿主骨；宿主根是它们所属的身体。调用时宿主必须在静止姿态（尚未播动作）。 */
    hostBones: readonly Object3D[];
    hostRoot: Object3D;
    /** 要落点的肢（游戏 `PoseReach`：上段、下段、末端，都是链接下标）：转完之后上下两段弯曲，让末端落在宿主末端相对这条肢上段关节的
     *  位置上（转过、按这条肢两边静止长度之比缩放过），弯法与宿主相同，末端保持抄来的转动，弯曲面取宿主这条肢自己的。 */
    reach?: readonly (readonly [number, number, number])[];
}

/** 两段肢的中间关节放在哪里，末端才落在 `target` 上（游戏 `PoseSolver.Elbow`）：够不着或折不到那么近时末端停在朝目标的线上，`end` 是它实际落点。 */
function elbow(root: Vector3, target: Vector3, upper: number, lower: number, bend: Vector3, end: Vector3): Vector3 {
    const to = target.clone().sub(root),
        distance = to.length(),
        dir = distance > 1e-5 ? to.divideScalar(distance) : new Vector3(0, 1, 0),
        d = Math.min(Math.max(distance, Math.abs(upper - lower) + 1e-4), upper + lower - 1e-4);
    end.copy(root).addScaledVector(dir, d);
    let side = bend.clone().addScaledVector(dir, -bend.dot(dir));
    if (side.lengthSq() < 1e-10) side = new Vector3().crossVectors(dir, Math.abs(dir.y) < 0.99 ? new Vector3(0, 1, 0) : new Vector3(1, 0, 0));
    const along = (upper * upper + d * d - lower * lower) / (2 * d),
        height = Math.sqrt(Math.max(0, upper * upper - along * along));
    return root.clone().addScaledVector(dir, along).addScaledVector(side.normalize(), height);
}
/** 骨的世界转动设成 `world`（游戏里直接写 Transform.rotation）。 */
function setWorldTurn(bone: Object3D, world: Quaternion) {
    bone.parent!.updateWorldMatrix(true, false);
    bone.quaternion.copy(bone.parent!.getWorldQuaternion(new Quaternion()).invert().multiply(world));
    bone.updateMatrixWorld(true);
}
/** 游戏 `LimbReach.Bend`：上段与下段转到让末端落在 `target` 上，中间关节朝 `bend` 弯。 */
function bendLimb(upper: Object3D, lower: Object3D, end: Object3D, target: Vector3, bend: Vector3) {
    const a = upper.getWorldPosition(new Vector3()),
        b = lower.getWorldPosition(new Vector3()),
        c = end.getWorldPosition(new Vector3()),
        reached = new Vector3(),
        knee = elbow(a, target, b.distanceTo(a), c.distanceTo(b), bend, reached),
        swing = (from: Vector3, to: Vector3) =>
            from.lengthSq() < 1e-12 || to.lengthSq() < 1e-12 ? new Quaternion() : new Quaternion().setFromUnitVectors(from.normalize(), to.normalize());
    setWorldTurn(upper, swing(b.clone().sub(a), knee.sub(a)).multiply(upper.getWorldQuaternion(new Quaternion())));
    lower.getWorldPosition(b);
    setWorldTurn(lower, swing(end.getWorldPosition(new Vector3()).sub(b), reached.sub(b)).multiply(lower.getWorldQuaternion(new Quaternion())));
}

/** 每帧在宿主动画更新之后调用 `apply`；解不出（宿主髋高过低）时返回 null，模型留在绑定姿态。 */
export function createSkinPose(input: SkinPoseInput): {apply(): void} | null {
    const {links, modelBones, modelRoot, hostBones, hostRoot, reach = []} = input,
        count = links.length;
    hostRoot.updateMatrixWorld(true);
    modelRoot.updateMatrixWorld(true);
    const bodyInverse = hostRoot.matrixWorld.clone().invert(),
        bodyOf = modelRoot.matrixWorld.clone().invert();
    const hostPosition: Vector3[] = [],
        hostRotation: Quaternion[] = [],
        modelPosition: Vector3[] = [],
        modelRotation: Quaternion[] = [];
    for (let at = 0; at < count; at++) {
        const rest = bodyInverse.clone().multiply(hostBones[at]!.matrixWorld),
            p = new Vector3(),
            q = new Quaternion();
        rest.decompose(p, q, new Vector3());
        hostPosition.push(flipPoint(p));
        hostRotation.push(flipTurn(q));
        const own = bodyOf.clone().multiply(modelBones[at]!.matrixWorld),
            op = new Vector3(),
            oq = new Quaternion();
        own.decompose(op, oq, new Vector3());
        modelPosition.push(flipPoint(op));
        modelRotation.push(flipTurn(oq));
    }
    // 游戏 PoseSolver.Ratio：宿主髋低于 0.1 米解不出；模型的髋在它自己的地面上或以下时没有高度可比，比值取 0，根留在静止高度。
    const ratioOf = (model: number, body: number) => (body < 0.1 ? null : Math.max(0, model) / body);
    const ratio = ratioOf(modelPosition[0]!.y, hostPosition[0]!.y);
    if (ratio === null) return null;
    // 每条肢在模型上对宿主的长度比（游戏 PoseSolver.LimbScale，肢没有长度时取髋高比），乘两边根的世界缩放之比。
    const world = modelRoot.getWorldScale(new Vector3()).y / Math.max(1e-4, hostRoot.getWorldScale(new Vector3()).y);
    const length = (position: Vector3[], [upper, lower, end]: readonly [number, number, number]) =>
        position[lower]!.distanceTo(position[upper]!) + position[end]!.distanceTo(position[lower]!);
    const limb = reach.map(r => {
        const body = length(hostPosition, r),
            model = length(modelPosition, r);
        return (body < 1e-4 || model < 1e-4 ? ratio : model / body) * world;
    });
    const direction = (position: Vector3[], at: number) => {
        const link = links[at]!;
        return link.toward >= 0
            ? position[link.toward]!.clone().sub(position[at]!)
            : link.from >= 0
              ? position[at]!.clone().sub(position[link.from]!)
              : new Vector3();
    };
    const correction = links.map((_, at) => {
        const unframe = boneFrame(direction(modelPosition, at)).invert().multiply(modelRotation[at]!);
        return hostRotation[at]!.clone()
            .invert()
            .multiply(boneFrame(direction(hostPosition, at)))
            .multiply(unframe);
    });
    // 作者的骨位置偏移只在静止时挪一次，整棵子树跟着走（和游戏里一样按世界位置加）。
    links.forEach((link, at) => {
        if (!link.offset.some(value => value !== 0)) return;
        const bone = modelBones[at]!;
        modelRoot.updateMatrixWorld(true);
        const world = bone
            .getWorldPosition(new Vector3())
            .add(flipPoint(new Vector3(...link.offset)).applyMatrix3(new Matrix3().setFromMatrix4(modelRoot.matrixWorld)));
        bone.position.copy(bone.parent!.worldToLocal(world));
    });
    const turnOf = new Quaternion(),
        hipsAt = new Vector3(),
        turn = new Quaternion(),
        source = new Quaternion(),
        target = new Quaternion(),
        parent = new Quaternion(),
        worldTurn = new Quaternion();
    return {
        apply() {
            hostRoot.updateMatrixWorld(true);
            modelRoot.updateMatrixWorld(true);
            hostRoot.getWorldQuaternion(turnOf);
            modelRoot.getWorldQuaternion(turn);
            turn.multiply(turnOf.invert());
            worldTurn.copy(turn);
            turn.copy(flipTurn(turn));
            const hips = flipPoint(hostRoot.worldToLocal(hostBones[0]!.getWorldPosition(hipsAt)));
            const root = modelPosition[0]!.clone().add(hips.sub(hostPosition[0]!).multiplyScalar(ratio));
            const rootBone = modelBones[0]!;
            rootBone.position.copy(rootBone.parent!.worldToLocal(modelRoot.localToWorld(flipPoint(root))));
            for (let at = 0; at < count; at++) {
                const bone = modelBones[at]!;
                hostBones[at]!.getWorldQuaternion(source);
                target.copy(turn).multiply(flipTurn(source)).multiply(correction[at]!);
                bone.parent!.updateWorldMatrix(true, false);
                bone.parent!.getWorldQuaternion(parent);
                bone.quaternion.copy(parent.invert().multiply(flipTurn(target)));
            }
            modelRoot.updateMatrixWorld(true);
            const at = (bone: Object3D) => bone.getWorldPosition(new Vector3());
            reach.forEach(([upper, lower, end], i) => {
                const keep = modelBones[end]!.getWorldQuaternion(new Quaternion());
                const goal = at(modelBones[upper]!).add(at(hostBones[end]!).sub(at(hostBones[upper]!)).applyQuaternion(worldTurn).multiplyScalar(limb[i]!));
                const bend = at(hostBones[lower]!).sub(at(hostBones[upper]!)).applyQuaternion(worldTurn);
                bendLimb(modelBones[upper]!, modelBones[lower]!, modelBones[end]!, goal, bend);
                setWorldTurn(modelBones[end]!, keep);
            });
            if (reach.length) modelRoot.updateMatrixWorld(true);
        }
    };
}
