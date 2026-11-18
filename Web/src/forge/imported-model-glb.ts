import {
    WebIO,
    type Node as GltfNode,
    type Primitive,
    type Material as GltfMaterial,
    type Skin as GltfSkin,
    type Texture as GltfTexture
} from '@gltf-transform/core';
import {KHRMaterialsEmissiveStrength, KHRMaterialsIOR, KHRMaterialsSpecular, type EmissiveStrength} from '@gltf-transform/extensions';
import {sha256Bytes} from './lib/hash.ts';
import {cofactors3, invertAffine, multiplyMatrices} from './affine-matrix.ts';
import {bakeOcclusion, decodePixels, encodePixels, packMetalSmooth, scaleNormals, tintPixels, type Pixels} from './imported-model-surface.ts';
import {
    importedMeshFits,
    importedModelLimits,
    readPngHeader,
    requireImported,
    stripPngMetadata,
    writeImportedMesh,
    ImportedModelError,
    type ImportedModelBone,
    type ImportedModelDescriptor,
    type ImportedModelMaterial
} from './imported-model-format.ts';

// 导入模型的唯一转换器（合同 §8「玩家上传模型」）：把一个自包含 `.glb` 转成导入模型的描述符、mesh 旁文件与 PNG。只收字节、只回数据与
// 拒收码（ImportedModelError），不碰网站的资源层与文案：网站（imported-model-convert.ts 再加上资源身份）与 ForgeSkin 编辑器（人物模型，
// character-import.ts）都直接调用它。先在原始 JSON 上做全部拒收与边界检查（文件大小、accessor 范围、贴图尺寸），通过之后才用
// gltf-transform 读数据；不合格一律按名字拒收并说明原因，绝不换成白模或默认材质。

/** 允许出现的 glTF 扩展：折射率、镜面强度只带本导入器忽略的参数，不改变基础色、金属度、粗糙度；自发光强度乘进自发光颜色。 */
export const importedModelAllowedExtensions = ['KHR_materials_ior', 'KHR_materials_specular', 'KHR_materials_emissive_strength'] as const;
const compressionExtensions = ['KHR_draco_mesh_compression', 'EXT_meshopt_compression', 'KHR_texture_basisu', 'EXT_texture_webp', 'EXT_texture_avif'];

/** 把一张 PNG 或 JPEG 变成 8 位、非隔行的 PNG 并报告尺寸；浏览器里由画布实现，测试里注入。 */
export type ImportedImageCodec = (bytes: Uint8Array, mime: 'image/png' | 'image/jpeg') => Promise<{png: Uint8Array; width: number; height: number}>;

/**
 * A character (the ForgeSkin character tab): its skins are joined into one skeleton (a bone two skins share must have the same rest pose),
 * `bones` (node indices: humanoid and spring bones) join the skeleton even when no skin lists them, and a rigid mesh follows its nearest
 * bone. What a character does not carry is left out instead of refused: animations, morph targets, a second UV set, the extensions named in
 * `ignore`, an identity KHR_texture_transform; a see-through (BLEND) material is cut out at half alpha when it has a colour map, drawn opaque
 * when its colour is at least half opaque and left out otherwise. `turn` turns the model half round about the vertical axis (a model built
 * facing the other way), so every character faces the same way. `follow` (node index to node index) hangs a bone under another bone than
 * its nearest bone ancestor, keeping its rest pose: a bone the file moves by a constraint rides the bone that constraint follows.
 */
export interface GlbCharacter {
    bones: readonly number[];
    ignore: readonly string[];
    turn: boolean;
    follow?: ReadonlyMap<number, number>;
}

/** The model's three files: the descriptor, the mesh sidecar and the PNGs in the descriptor's order. */
export interface ConvertedGlb {
    descriptor: ImportedModelDescriptor;
    mesh: Uint8Array;
    textures: Uint8Array[];
    stats: {faces: number; vertices: number; materials: number; textures: number};
}

const errorMessage = (error: unknown) => (error instanceof Error ? error.message : String(error));
const isFiniteNumber = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value);

interface Accessor {
    bufferView?: number;
    byteOffset?: number;
    componentType: number;
    count: number;
    type: string;
    sparse?: unknown;
    normalized?: boolean;
}
interface TextureInfo {
    index?: number;
    texCoord?: number;
    extensions?: Record<string, unknown>;
}
/** The glTF JSON chunk, as far as the converter reads it. */
export interface GlbJson {
    asset?: {version?: string};
    extensionsUsed?: string[];
    extensionsRequired?: string[];
    extensions?: Record<string, unknown>;
    scene?: number;
    scenes?: {nodes?: number[]}[];
    nodes?: {
        name?: string;
        children?: number[];
        mesh?: number;
        skin?: number;
        camera?: number;
        weights?: unknown;
        matrix?: number[];
        translation?: number[];
        rotation?: number[];
        scale?: number[];
        extensions?: Record<string, unknown>;
    }[];
    meshes?: {primitives?: {attributes?: Record<string, number>; indices?: number; mode?: number; material?: number; targets?: unknown}[]; weights?: unknown}[];
    materials?: Record<string, unknown>[];
    textures?: {source?: number; extensions?: unknown}[];
    images?: {uri?: string; mimeType?: string; bufferView?: number}[];
    accessors?: Accessor[];
    bufferViews?: {buffer?: number; byteOffset?: number; byteLength?: number; byteStride?: number}[];
    buffers?: {uri?: string; byteLength?: number}[];
    skins?: {joints?: number[]; inverseBindMatrices?: number}[];
    animations?: unknown[];
    cameras?: unknown[];
}

const componentBytes: Record<number, number> = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4};
const typeComponents: Record<string, number> = {SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT2: 4, MAT3: 9, MAT4: 16};
const anyComponent = Object.keys(componentBytes).map(Number),
    anyType = Object.keys(typeComponents);
const allowedAttributes = new Set(['POSITION', 'NORMAL', 'TEXCOORD_0', 'TANGENT', 'JOINTS_0', 'WEIGHTS_0']);
/** 蒙皮骨骼数上限：ForgeVisual 格式 2 的骨骼下标是 16 位。 */
const maximumBones = importedModelLimits.bones;
const isIndex = (value: unknown, length: number): value is number => Number.isSafeInteger(value) && (value as number) >= 0 && (value as number) < length;

/** GLB 容器：12 字节头、一个 JSON 段、可选的一个 BIN 段；长度处处核对，不读到外面。 */
export function splitGlb(bytes: Uint8Array): {json: GlbJson; bin: Uint8Array} {
    requireImported(bytes.length >= 20, 'not-glb');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    requireImported(view.getUint32(0, true) === 0x46546c67, 'not-glb');
    requireImported(view.getUint32(4, true) === 2, 'not-glb');
    requireImported(view.getUint32(8, true) === bytes.length, 'glb-length');
    let at = 12,
        json: GlbJson | undefined,
        bin: Uint8Array | undefined;
    while (at < bytes.length) {
        requireImported(at + 8 <= bytes.length, 'glb-length');
        const length = view.getUint32(at, true),
            type = view.getUint32(at + 4, true),
            start = at + 8;
        requireImported(length <= bytes.length - start, 'glb-length');
        if (type === 0x4e4f534a && !json) {
            try {
                json = JSON.parse(new TextDecoder('utf-8', {fatal: true}).decode(bytes.subarray(start, start + length))) as GlbJson;
            } catch {
                requireImported(false, 'glb-json');
            }
        } else if (type === 0x004e4942 && !bin && json) bin = bytes.subarray(start, start + length);
        else requireImported(false, 'glb-length');
        at = start + length + ((4 - (length % 4)) % 4);
    }
    requireImported(json && typeof json === 'object' && !Array.isArray(json) && json.asset?.version === '2.0', 'not-glb');
    return {json, bin: bin ?? new Uint8Array(0)};
}

function accessorFits(json: GlbJson, index: unknown, bin: number, component: readonly number[], types: readonly string[]): Accessor {
    const accessors = json.accessors ?? [];
    requireImported(isIndex(index, accessors.length), 'accessor');
    const accessor = accessors[index]!;
    requireImported(!accessor.sparse, 'sparse');
    requireImported(component.includes(accessor.componentType) && types.includes(accessor.type), 'accessor-type');
    requireImported(Number.isSafeInteger(accessor.count) && accessor.count > 0, 'accessor');
    const view = isIndex(accessor.bufferView, (json.bufferViews ?? []).length) ? json.bufferViews![accessor.bufferView]! : undefined;
    requireImported(view !== undefined && (view.buffer ?? 0) === 0 && Number.isSafeInteger(view.byteLength), 'accessor');
    const element = componentBytes[accessor.componentType]! * typeComponents[accessor.type]!,
        stride = view!.byteStride ?? element,
        offset = view!.byteOffset ?? 0;
    requireImported(
        Number.isSafeInteger(stride) && stride >= element && Number.isSafeInteger(offset) && offset >= 0 && offset + view!.byteLength! <= bin,
        'accessor'
    );
    requireImported(
        Number.isSafeInteger(accessor.byteOffset ?? 0) && (accessor.byteOffset ?? 0) + (accessor.count - 1) * stride + element <= view!.byteLength!,
        'accessor'
    );
    return accessor;
}

/** A texture transform that changes nothing (a character's files often carry one). */
function identityTransform(value: unknown): boolean {
    const t = value as {offset?: number[]; scale?: number[]; rotation?: number; texCoord?: number} | undefined;
    return !!t && (t.offset ?? [0, 0]).every(v => v === 0) && (t.scale ?? [1, 1]).every(v => v === 1) && (t.rotation ?? 0) === 0 && t.texCoord === undefined;
}

/** Alpha at which a character's see-through material with a colour map is cut out. */
export const characterBlendCutoff = 0.5;

/** How a character draws a see-through (BLEND) material, which the game cannot blend: cut out at half alpha with a colour map, opaque
 * when its colour is at least half opaque, left out otherwise. The converter packs it so and the editor's view of the source draws it so. */
export function characterBlend(colourMap: boolean, alpha: number): 'mask' | 'opaque' | 'drop' {
    return colourMap ? 'mask' : alpha >= characterBlendCutoff ? 'opaque' : 'drop';
}

/** A see-through material a character leaves out. */
function dropped(material: Record<string, unknown>, character: GlbCharacter | undefined): boolean {
    if (!character || material.alphaMode !== 'BLEND') return false;
    const pbr = (material.pbrMetallicRoughness ?? {}) as {baseColorTexture?: unknown; baseColorFactor?: number[]};
    return characterBlend(pbr.baseColorTexture !== undefined, pbr.baseColorFactor?.[3] ?? 1) === 'drop';
}

/** 在任何数据被读出前，对原始 JSON 做全部拒收与边界检查，并算出模型的面数与解码后的贴图字节。 */
function inspect(json: GlbJson, bin: Uint8Array, character?: GlbCharacter): void {
    const allowed: readonly string[] = character
        ? [...importedModelAllowedExtensions, 'KHR_texture_transform', ...character.ignore]
        : importedModelAllowedExtensions;
    const used = json.extensionsUsed ?? [],
        required = json.extensionsRequired ?? [];
    requireImported(Array.isArray(used) && Array.isArray(required), 'glb-json');
    for (const name of [...required, ...used]) {
        requireImported(typeof name === 'string', 'glb-json');
        requireImported(!compressionExtensions.includes(name), 'compressed', {name});
        requireImported(allowed.includes(name), 'extension', {name});
    }
    // One skin at most (one skeleton the whole model is bound to); its own animations are not carried. A character joins its skins.
    const skins = json.skins ?? [];
    requireImported(Array.isArray(skins) && (character || skins.length <= 1), 'skin-count');
    for (const skin of skins) {
        const joints = skin.joints;
        requireImported(
            Array.isArray(joints) &&
                joints.length >= 1 &&
                joints.length <= maximumBones &&
                new Set(joints).size === joints.length &&
                joints.every(joint => isIndex(joint, (json.nodes ?? []).length)),
            'skin-bones',
            {limit: maximumBones}
        );
        requireImported(accessorFits(json, skin.inverseBindMatrices, bin.length, [5126], ['MAT4']).count === joints.length, 'skin-bones', {
            limit: maximumBones
        });
    }
    requireImported(character || !json.animations?.length, 'animation');
    requireImported(!json.cameras?.length, 'camera');
    requireImported((json.buffers ?? []).length <= 1 && (json.buffers ?? []).every(buffer => buffer.uri === undefined), 'external');
    requireImported(
        (json.images ?? []).every(image => image.uri === undefined),
        'external'
    );
    // gltf-transform reads every accessor, used or not, and copies interleaved, sparse and buffer-view-less ones into arrays of the claimed
    // count: each one must lie inside the binary chunk, and a sparse one (a character's morph target, left out) claims no more elements
    // than the largest accessor the file holds data for, so a damaged count cannot ask for memory the file does not back.
    const accessors = json.accessors ?? [];
    requireImported(Array.isArray(accessors), 'glb-json');
    let backed = 0;
    const sparse: Accessor[] = [];
    for (let index = 0; index < accessors.length; index++) {
        const accessor = character && accessors[index]?.sparse ? accessors[index]! : undefined;
        if (accessor) sparse.push(accessor);
        else backed = Math.max(backed, accessorFits(json, index, bin.length, anyComponent, anyType).count);
    }
    for (const accessor of sparse)
        requireImported(
            anyComponent.includes(accessor.componentType) &&
                anyType.includes(accessor.type) &&
                Number.isSafeInteger(accessor.count) &&
                accessor.count > 0 &&
                accessor.count <= backed,
            'accessor'
        );
    const nodes = json.nodes ?? [],
        meshes = json.meshes ?? [],
        materials = json.materials ?? [];
    // 实例化次数：同一网格被多个节点引用就按多次算面数。
    const sceneIndex = json.scene ?? 0,
        scene = json.scenes?.[sceneIndex];
    requireImported(scene !== undefined, 'empty');
    const meshInstances = new Map<number, number>(),
        visited = new Set<number>(),
        skinnedMeshes = new Set<number>(),
        rigidMeshes = new Set<number>();
    const visit = (index: unknown) => {
        requireImported(isIndex(index, nodes.length) && !visited.has(index), 'glb-json');
        visited.add(index);
        const node = nodes[index]!;
        requireImported(node.camera === undefined && (character || node.weights === undefined), node.camera !== undefined ? 'camera' : 'morph');
        requireImported(
            node.skin === undefined || ((character ? isIndex(node.skin, skins.length) : node.skin === 0 && skins.length === 1) && node.mesh !== undefined),
            'skin-count'
        );
        if (node.mesh !== undefined) (node.skin === undefined ? rigidMeshes : skinnedMeshes).add(node.mesh);
        requireImported(!node.extensions || Object.keys(node.extensions).every(name => allowed.includes(name)), 'extension', {
            name: Object.keys(node.extensions ?? {})[0] ?? ''
        });
        if (node.mesh !== undefined) {
            requireImported(isIndex(node.mesh, meshes.length), 'glb-json');
            meshInstances.set(node.mesh, (meshInstances.get(node.mesh) ?? 0) + 1);
        }
        for (const child of node.children ?? []) visit(child);
    };
    for (const root of scene.nodes ?? []) visit(root);
    requireImported(meshInstances.size > 0, 'empty');
    // A skinned model is skinned all through: every mesh follows the one skeleton, so every vertex has its bones. A character's rigid mesh follows its nearest bone.
    requireImported(character || !skinnedMeshes.size || (!rigidMeshes.size && skins.length === 1), 'skin-mixed');
    requireImported(![...skinnedMeshes].some(mesh => rigidMeshes.has(mesh)), 'skin-mixed');

    let faces = 0,
        vertices = 0;
    const usedMaterials: number[] = [];
    for (const [meshIndex, count] of meshInstances) {
        const mesh = meshes[meshIndex]!;
        requireImported(
            (character || mesh.weights === undefined) && Array.isArray(mesh.primitives) && mesh.primitives.length > 0,
            mesh.weights !== undefined && !character ? 'morph' : 'empty'
        );
        for (const primitive of mesh.primitives!) {
            requireImported(character || primitive.targets === undefined, 'morph');
            requireImported((primitive.mode ?? 4) === 4, 'primitive-mode');
            const attributes = primitive.attributes ?? {};
            for (const name of Object.keys(attributes)) {
                if (/^(JOINTS|WEIGHTS)_[1-9]/.test(name)) requireImported(false, 'skin-influences');
                if (/^TEXCOORD_[1-9]/.test(name)) {
                    requireImported(character, 'texcoord-second');
                    continue;
                }
                requireImported(allowedAttributes.has(name), 'attribute', {name});
            }
            const position = accessorFits(json, attributes.POSITION, bin.length, [5126], ['VEC3']);
            const skinned = skinnedMeshes.has(meshIndex);
            requireImported((attributes.JOINTS_0 !== undefined) === skinned && (attributes.WEIGHTS_0 !== undefined) === skinned, 'skin-mixed');
            if (skinned) {
                requireImported(accessorFits(json, attributes.JOINTS_0, bin.length, [5121, 5123], ['VEC4']).count === position.count, 'accessor');
                const weights = accessorFits(json, attributes.WEIGHTS_0, bin.length, [5126, 5121, 5123], ['VEC4']);
                requireImported(weights.count === position.count && (weights.componentType === 5126 || weights.normalized === true), 'accessor');
            }
            requireImported(attributes.NORMAL !== undefined, 'normal-missing');
            const normal = accessorFits(json, attributes.NORMAL, bin.length, [5126], ['VEC3']);
            requireImported(normal.count === position.count, 'accessor');
            if (attributes.TEXCOORD_0 !== undefined)
                requireImported(accessorFits(json, attributes.TEXCOORD_0, bin.length, [5126], ['VEC2']).count === position.count, 'accessor');
            let indexCount = position.count;
            if (primitive.indices !== undefined) indexCount = accessorFits(json, primitive.indices, bin.length, [5121, 5123, 5125], ['SCALAR']).count;
            requireImported(indexCount % 3 === 0, 'accessor');
            requireImported(isIndex(primitive.material, materials.length), 'no-material');
            if (dropped(materials[primitive.material]!, character)) continue;
            if (!usedMaterials.includes(primitive.material)) usedMaterials.push(primitive.material);
            // A double-sided surface is written twice (front and reversed back), so it counts twice.
            const sides = materials[primitive.material]!.doubleSided === true ? 2 : 1;
            faces += (sides * count * indexCount) / 3;
            // A character's primitives often share one vertex buffer and keep only the vertices they draw: at most one per index.
            vertices += sides * count * (character ? Math.min(position.count, indexCount) : position.count);
        }
    }
    // The one bound: the mesh file the game reads as one byte array (an upper estimate here; the written mesh is checked again).
    requireImported(importedMeshFits(vertices, faces * 3, skinnedMeshes.size > 0), 'mesh-too-large');

    const images = new Set<number>(),
        textures = json.textures ?? [];
    /** One texture reference of a material (colour, normal or emission): first UV set, no per-texture extension, a PNG or JPEG inside the binary chunk within the size limits. */
    const checkTexture = (info: TextureInfo, name: string) => {
        const extensions = Object.entries(info.extensions ?? {});
        requireImported(
            (info.texCoord ?? 0) === 0 &&
                (info.extensions === undefined ||
                    (character && extensions.every(([key, value]) => key === 'KHR_texture_transform' && identityTransform(value)))),
            'texcoord-second'
        );
        requireImported(
            isIndex(info.index, textures.length) &&
                isIndex(textures[info.index]!.source, (json.images ?? []).length) &&
                textures[info.index]!.extensions === undefined,
            'texture-source',
            {name}
        );
        const sourceIndex = textures[info.index]!.source!;
        if (images.has(sourceIndex)) return;
        const image = json.images![sourceIndex]!;
        requireImported(isIndex(image.bufferView, (json.bufferViews ?? []).length), 'texture-source', {name});
        const view = json.bufferViews![image.bufferView]!,
            offset = view.byteOffset ?? 0;
        requireImported(
            (view.buffer ?? 0) === 0 && Number.isSafeInteger(view.byteLength) && offset >= 0 && offset + view.byteLength! <= bin.length,
            'accessor'
        );
        const data = bin.subarray(offset, offset + view.byteLength!),
            png = readPngHeader(data),
            jpeg = png ? null : readJpegSize(data);
        requireImported(png !== null || jpeg !== null, 'image-format', {name});
        const size = png ?? jpeg!;
        requireImported(size.width <= importedModelLimits.textureSide && size.height <= importedModelLimits.textureSide, 'image-too-large', {
            name,
            size: size.width + 'x' + size.height,
            limit: importedModelLimits.textureSide
        });
        images.add(sourceIndex);
    };
    for (const materialIndex of usedMaterials) {
        const material = materials[materialIndex]!,
            name = typeof material.name === 'string' && material.name ? material.name : String(materialIndex);
        const pbr = (material.pbrMetallicRoughness ?? {}) as Record<string, unknown>;
        requireImported(character || material.alphaMode !== 'BLEND', 'material-blend', {name});
        const emissive = material.emissiveFactor;
        requireImported(
            emissive === undefined ||
                (Array.isArray(emissive) && emissive.length === 3 && emissive.every(channel => typeof channel === 'number' && channel >= 0 && channel <= 1)),
            'material-emissive',
            {name}
        );
        const normal = material.normalTexture as (TextureInfo & {scale?: number}) | undefined,
            occlusion = material.occlusionTexture as (TextureInfo & {strength?: number}) | undefined;
        // The gear shader has no normal-map strength and no occlusion input: the strength is folded into the map and the occlusion into the colour.
        requireImported(normal === undefined || normal.scale === undefined || isFiniteNumber(normal.scale), 'material-surface', {name});
        requireImported(
            occlusion === undefined ||
                occlusion.strength === undefined ||
                (typeof occlusion.strength === 'number' && occlusion.strength >= 0 && occlusion.strength <= 1),
            'material-surface',
            {name}
        );
        for (const factor of [pbr.metallicFactor, pbr.roughnessFactor])
            requireImported(factor === undefined || (typeof factor === 'number' && factor >= 0 && factor <= 1), 'material-surface', {name});
        requireImported(
            material.extensions === undefined || Object.keys(material.extensions as object).every(extension => allowed.includes(extension)),
            'extension',
            {name: Object.keys((material.extensions ?? {}) as object)[0] ?? ''}
        );
        const strength = ((material.extensions ?? {}) as Record<string, {emissiveStrength?: unknown} | undefined>).KHR_materials_emissive_strength
            ?.emissiveStrength;
        requireImported(strength === undefined || (isFiniteNumber(strength) && strength >= 0), 'material-emissive', {name});
        const info = pbr.baseColorTexture as TextureInfo | undefined;
        const factor = pbr.baseColorFactor;
        requireImported(
            factor === undefined ||
                (Array.isArray(factor) && factor.length === 4 && factor.every(channel => typeof channel === 'number' && channel >= 0 && channel <= 1)),
            'material-color',
            {name}
        );
        requireImported(info !== undefined || material.alphaMode !== 'MASK', 'material-mask-texture', {name});
        if (info !== undefined) {
            requireImported(character || factor === undefined || (factor as number[]).every(channel => channel === 1), 'material-tinted-texture', {name});
            checkTexture(info, name);
        }
        if (normal !== undefined) checkTexture(normal, name);
        if (occlusion !== undefined) checkTexture(occlusion, name);
        if (pbr.metallicRoughnessTexture !== undefined) checkTexture(pbr.metallicRoughnessTexture as TextureInfo, name);
        // glTF multiplies the emission map by the factor, and the factor defaults to black: a map under a black factor shows nothing and is not carried.
        if (material.emissiveTexture !== undefined && Array.isArray(emissive) && emissive.some(channel => channel > 0))
            checkTexture(material.emissiveTexture as TextureInfo, name);
    }
}

/** JPEG 的 SOF 段尺寸：不解码就知道边长；找不到返回 null。 */
function readJpegSize(bytes: Uint8Array): {width: number; height: number} | null {
    if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== 0xd8) return null;
    let at = 2;
    while (at + 9 < bytes.length) {
        if (bytes[at] !== 0xff) {
            at++;
            continue;
        }
        const marker = bytes[at + 1]!;
        if (marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
            at += 2;
            continue;
        }
        const length = (bytes[at + 2]! << 8) | bytes[at + 3]!;
        if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
            const height = (bytes[at + 5]! << 8) | bytes[at + 6]!,
                width = (bytes[at + 7]! << 8) | bytes[at + 8]!;
            return width > 0 && height > 0 ? {width, height} : null;
        }
        if (length < 2) return null;
        at += 2 + length;
    }
    return null;
}

/** 世界矩阵（列主序）对点和法线的作用；镜像（行列式为负）时三角形绕向翻转。 */
function normalMatrix(m: ArrayLike<number>): {n: number[]; flip: boolean} {
    // 余子式矩阵就是 det·M⁻ᵀ；行列式为负时乘 -1，法线方向才不反。
    const {det, c} = cofactors3(m);
    return {n: det < 0 ? c.map(v => -v) : c, flip: det < 0};
}

/**
 * The skeleton the mesh is bound to: `skinBones` maps each skin's joint list to its bones, `nodeBone` every bone's node to its bone.
 * A joint a later skin binds in another pose than the first skin keeps the first pose; `fixes` holds, per skin and joint, the matrix
 * that moves that skin's vertices into it (null: none needed).
 */
interface Skeleton {
    bones: ImportedModelBone[];
    skinBones: Map<GltfSkin, number[]>;
    fixes: Map<GltfSkin, (number[] | null)[] | null>;
    nodeBone: Map<GltfNode, number>;
}

/** Vertex attributes gathered so far; `skeleton` is null for a model without a skin. */
interface Collected {
    positions: number[];
    normals: number[];
    uvs: number[];
    indices: Map<number, number[]>;
    joints: number[];
    weights: number[];
    skeleton: Skeleton | null;
}

const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
function collect(node: GltfNode, out: Collected, materialIds: Map<GltfMaterial, number>, character: GlbCharacter | undefined): void {
    const mesh = node.getMesh();
    if (mesh) {
        const skin = node.getSkin();
        // glTF places a skinned mesh by its joints alone: the node's own transform does not apply. A character's rigid mesh rides its nearest bone.
        const world = skin ? identity : node.getWorldMatrix(),
            {n, flip} = normalMatrix(world);
        let bones: number[] | null = null;
        if (out.skeleton && skin) bones = out.skeleton.skinBones.get(skin)!;
        else if (out.skeleton) {
            let bone = 0;
            for (let at: GltfNode | null = node; at; at = at.getParentNode())
                if (out.skeleton.nodeBone.has(at)) {
                    bone = out.skeleton.nodeBone.get(at)!;
                    break;
                }
            bones = [bone];
        }
        for (const primitive of mesh.listPrimitives()) {
            const material = primitive.getMaterial();
            if (
                material &&
                character &&
                material.getAlphaMode() === 'BLEND' &&
                characterBlend(!!material.getBaseColorTexture(), material.getBaseColorFactor()[3]!) === 'drop'
            )
                continue;
            append(primitive, world, n, flip, out, materialIds, bones, !skin, !!character, skin ? (out.skeleton?.fixes.get(skin) ?? null) : null);
        }
    }
    for (const child of node.listChildren()) collect(child, out, materialIds, character);
}

/**
 * The model's skeleton from its skins (and a character's other bones): each joint's rest pose is the inverse of its inverse bind matrix (in
 * the mesh's space), a bone no skin lists its node's world matrix, turned into Unity's axes as the vertices are (X negated: S·M·S). Bones
 * are listed parents first; a bone's parent is its nearest ancestor that is a bone, or the bone `follow` names for it (unless that bone hangs
 * below it).
 */
function readSkeleton(skins: GltfSkin[], extra: GltfNode[], follow: ReadonlyMap<GltfNode, GltfNode> = new Map()): Skeleton {
    const nodes: GltfNode[] = [],
        rests = new Map<GltfNode, number[]>(),
        fixes = new Map<GltfSkin, (number[] | null)[] | null>();
    for (const skin of skins) {
        const joints = skin.listJoints(),
            inverse = skin.getInverseBindMatrices()!,
            fix: (number[] | null)[] = joints.map(() => null);
        joints.forEach((joint, index) => {
            const rest = invertAffine(inverse.getElement(index, []));
            requireImported(rest !== null, 'skin-bones', {limit: maximumBones});
            requireImported(rest.every(Number.isFinite), 'non-finite');
            const seen = rests.get(joint);
            if (!seen) {
                rests.set(joint, rest);
                nodes.push(joint);
            } else if (seen.some((value, at) => Math.abs(value - rest[at]!) > 1e-6)) fix[index] = multiplyMatrices(seen, inverse.getElement(index, []));
        });
        fixes.set(skin, fix.some(Boolean) ? fix : null);
    }
    for (const node of extra)
        if (!rests.has(node)) {
            rests.set(node, [...node.getWorldMatrix()]);
            nodes.push(node);
        }
    requireImported(nodes.length <= maximumBones, 'skin-bones', {limit: maximumBones});
    const set = new Set(nodes);
    const nearest = (node: GltfNode) => {
        for (let at = node.getParentNode(); at; at = at.getParentNode()) if (set.has(at)) return nodes.indexOf(at);
        return -1;
    };
    const parents = nodes.map(nearest);
    // A followed bone may not hang under its own descendant (the walk up from the new parent must not meet it).
    follow.forEach((under, node) => {
        const at = nodes.indexOf(node),
            to = nodes.indexOf(under);
        if (at < 0 || to < 0) return;
        for (let up = to; up >= 0; up = parents[up]!) if (up === at) return;
        parents[at] = to;
    });
    const depth = (node: number): number => (parents[node]! < 0 ? 0 : 1 + depth(parents[node]!));
    const order = nodes.map((_, index) => index).sort((left, right) => depth(left) - depth(right) || left - right),
        place = new Array<number>(nodes.length);
    order.forEach((node, at) => {
        place[node] = at;
    });
    const names = new Set<string>();
    const bones = order.map(index => {
        const name = nodes[index]!.getName();
        requireImported(name.length > 0 && name.length <= 200 && !/[\u0000-\u001f\u007f]/.test(name) && !names.has(name), 'skin-bone-name', {name});
        names.add(name);
        return {
            name,
            parent: parents[index]! < 0 ? -1 : place[parents[index]!]!,
            rest: rests.get(nodes[index]!)!.map((value, at) => ((at % 4 === 0) !== (Math.floor(at / 4) === 0) ? -value : value) + 0)
        };
    });
    const nodeBone = new Map(nodes.map((node, index) => [node, place[index]!]));
    return {bones, nodeBone, fixes, skinBones: new Map(skins.map(skin => [skin, skin.listJoints().map(joint => nodeBone.get(joint)!)]))};
}

function append(
    primitive: Primitive,
    world: ArrayLike<number>,
    n: number[],
    flip: boolean,
    out: Collected,
    materialIds: Map<GltfMaterial, number>,
    bones: number[] | null,
    rigid: boolean,
    compact: boolean,
    fix: (number[] | null)[] | null
): void {
    let position = primitive.getAttribute('POSITION')!.getArray()!,
        normal = primitive.getAttribute('NORMAL')!.getArray()!;
    const uv = primitive.getAttribute('TEXCOORD_0')?.getArray() ?? null;
    // Vertices bound in another pose than the skeleton's: each moved by its joints' fixes, blended by its weights.
    if (fix) {
        const moved = Float32Array.from(position),
            turned = Float32Array.from(normal),
            jointAccessor = primitive.getAttribute('JOINTS_0')!,
            weightAccessor = primitive.getAttribute('WEIGHTS_0')!,
            joint: number[] = [],
            weight: number[] = [];
        for (let vertex = 0; vertex < moved.length / 3; vertex++) {
            jointAccessor.getElement(vertex, joint);
            weightAccessor.getElement(vertex, weight);
            const sum = weight.reduce((total, value) => total + value, 0) || 1,
                m = new Array<number>(16).fill(0);
            for (let k = 0; k < 4; k++) {
                const by = (weight[k] ?? 0) / sum;
                if (by > 0) (fix[joint[k]!] ?? identity).forEach((value, at) => (m[at]! += by * value));
            }
            const [x, y, z] = [position[vertex * 3]!, position[vertex * 3 + 1]!, position[vertex * 3 + 2]!],
                [a, b, c] = [normal[vertex * 3]!, normal[vertex * 3 + 1]!, normal[vertex * 3 + 2]!];
            for (let row = 0; row < 3; row++) {
                moved[vertex * 3 + row] = m[row]! * x + m[4 + row]! * y + m[8 + row]! * z + m[12 + row]!;
                turned[vertex * 3 + row] = m[row]! * a + m[4 + row]! * b + m[8 + row]! * c;
            }
        }
        position = moved;
        normal = turned;
    }
    const source = primitive.getIndices()?.getArray() ?? null,
        full = position.length / 3;
    // A character keeps only the vertices its triangles use (its primitives often share one vertex buffer): keep lists them, slot renumbers.
    let keep: number[] | null = null,
        slot: Int32Array | null = null;
    if (compact && source) {
        slot = new Int32Array(full).fill(-1);
        keep = [];
        for (const index of source) {
            requireImported(index < full, 'index-range');
            if (slot[index]! < 0) {
                slot[index] = keep.length;
                keep.push(index);
            }
        }
    }
    const count = keep ? keep.length : full,
        base = out.positions.length / 3,
        limit = importedModelLimits.extent;
    if (bones && rigid)
        for (let vertex = 0; vertex < count; vertex++) {
            out.joints.push(bones[0]!, 0, 0, 0);
            out.weights.push(1, 0, 0, 0);
        }
    else if (bones) {
        // Four influences per vertex in the skeleton's bone order, weights scaled to sum to 1 (a normalised integer weight reads as its fraction either way).
        const jointAccessor = primitive.getAttribute('JOINTS_0')!,
            weightAccessor = primitive.getAttribute('WEIGHTS_0')!,
            joint: number[] = [],
            weight: number[] = [];
        for (let vertex = 0; vertex < count; vertex++) {
            jointAccessor.getElement(keep ? keep[vertex]! : vertex, joint);
            weightAccessor.getElement(keep ? keep[vertex]! : vertex, weight);
            const sum = weight.reduce((total, value) => total + value, 0);
            requireImported(weight.every(value => Number.isFinite(value) && value >= 0) && sum > 0, 'skin-weights');
            for (let k = 0; k < 4; k++) {
                const used = weight[k]! > 0;
                requireImported(!used || (Number.isSafeInteger(joint[k]) && joint[k]! < bones.length), 'skin-weights');
                out.joints.push(used ? bones[joint[k]!]! : 0);
                out.weights.push(weight[k]! / sum);
            }
        }
    }
    for (let item = 0; item < count; item++) {
        const vertex = keep ? keep[item]! : item;
        const x = position[vertex * 3]!,
            y = position[vertex * 3 + 1]!,
            z = position[vertex * 3 + 2]!;
        requireImported(Number.isFinite(x) && Number.isFinite(y) && Number.isFinite(z), 'non-finite');
        // glTF 右手系到 Unity 左手系：X 取反。
        const wx = -(world[0]! * x + world[4]! * y + world[8]! * z + world[12]!),
            wy = world[1]! * x + world[5]! * y + world[9]! * z + world[13]!,
            wz = world[2]! * x + world[6]! * y + world[10]! * z + world[14]!;
        requireImported(Math.abs(wx) <= limit && Math.abs(wy) <= limit && Math.abs(wz) <= limit, 'extent', {limit});
        out.positions.push(wx, wy, wz);
        const nx = normal[vertex * 3]!,
            ny = normal[vertex * 3 + 1]!,
            nz = normal[vertex * 3 + 2]!;
        requireImported(Number.isFinite(nx) && Number.isFinite(ny) && Number.isFinite(nz), 'non-finite');
        let tx = n[0]! * nx + n[3]! * ny + n[6]! * nz,
            ty = n[1]! * nx + n[4]! * ny + n[7]! * nz,
            tz = n[2]! * nx + n[5]! * ny + n[8]! * nz;
        const length = Math.hypot(tx, ty, tz);
        // A character's file now and then carries a zero normal (hidden geometry): it points up instead.
        requireImported(length > 1e-8 || compact, 'degenerate-normal');
        if (length > 1e-8) {
            tx /= length;
            ty /= length;
            tz /= length;
        } else [tx, ty, tz] = [0, 1, 0];
        out.normals.push(-tx, ty, tz);
        if (uv) {
            const u = uv[vertex * 2]!,
                v = uv[vertex * 2 + 1]!;
            requireImported(Number.isFinite(u) && Number.isFinite(v), 'non-finite');
            out.uvs.push(u, 1 - v);
        } else out.uvs.push(0, 0);
    }
    const material = primitive.getMaterial();
    requireImported(material !== null, 'no-material');
    if (!materialIds.has(material!)) materialIds.set(material!, materialIds.size);
    const list = out.indices.get(materialIds.get(material!)!) ?? [];
    out.indices.set(materialIds.get(material!)!, list);
    // 游戏的装备着色器固定剔除背面、没有双面开关：双面材质在这里补一份背面——同样的顶点与 UV、法线取反、绕向反过来。
    const back = material!.getDoubleSided() ? count : 0;
    if (back) {
        for (let k = base * 3; k < (base + count) * 3; k++) {
            out.positions.push(out.positions[k]!);
            out.normals.push(0 - out.normals[k]!);
        }
        for (let k = base * 2; k < (base + count) * 2; k++) out.uvs.push(out.uvs[k]!);
        if (bones)
            for (let k = base * 4; k < (base + count) * 4; k++) {
                out.joints.push(out.joints[k]!);
                out.weights.push(out.weights[k]!);
            }
    }
    const total = source ? source.length : count;
    // Unity 绕向与 glTF 相反；镜像节点在 glTF 里本来就再翻一次，两次抵消。
    const reverse = !flip;
    for (let at = 0; at < total; at += 3) {
        const a = source ? source[at]! : at,
            b = source ? source[at + 1]! : at + 1,
            c = source ? source[at + 2]! : at + 2;
        requireImported(a < full && b < full && c < full, 'index-range');
        const first = slot ? slot[a]! : a,
            second = slot ? slot[reverse ? c : b]! : reverse ? c : b,
            third = slot ? slot[reverse ? b : c]! : reverse ? b : c;
        list.push(base + first, base + second, base + third);
        if (back) list.push(base + back + first, base + back + third, base + back + second);
    }
}

/**
 * 把 `.glb` 字节转成导入模型的三样文件。`codec` 只在贴图不是「8 位、非隔行的 PNG」时用到（JPEG 或别的 PNG 变体），其余 PNG 原字节入包。
 * 同一份字节、同一个 codec 总是得到同样的 mesh 与描述符。
 */
export async function convertGlbModel(bytes: Uint8Array, codec: ImportedImageCodec, character?: GlbCharacter): Promise<ConvertedGlb> {
    const {json, bin} = splitGlb(bytes);
    inspect(json, bin, character);
    const io = new WebIO().registerExtensions([KHRMaterialsIOR, KHRMaterialsSpecular, KHRMaterialsEmissiveStrength]);
    const document = await io.readBinary(bytes).catch(error => {
        throw new ImportedModelError('glb-json', {detail: errorMessage(error)});
    });
    const root = document.getRoot(),
        scene = root.getDefaultScene() ?? root.listScenes()[0];
    requireImported(scene, 'empty');
    const skins = character ? root.listSkins() : root.listSkins().slice(0, 1),
        extra = character ? character.bones.map(index => root.listNodes()[index]).filter((node): node is GltfNode => !!node) : [],
        nodeList = root.listNodes(),
        follow = new Map(
            [...(character?.follow ?? [])].flatMap(([bone, under]) => (nodeList[bone] && nodeList[under] ? [[nodeList[bone], nodeList[under]] as const] : []))
        ),
        skeleton = skins.length || extra.length ? readSkeleton(skins, [...extra, ...follow.values()], follow) : null;
    const out: Collected = {positions: [], normals: [], uvs: [], indices: new Map(), joints: [], weights: [], skeleton},
        materialIds = new Map<GltfMaterial, number>();
    for (const node of scene!.listChildren()) collect(node, out, materialIds, character);
    requireImported(out.positions.length > 0, 'empty');
    // Turned half round about the vertical axis: X and Z change sign, for the points, the normals and every bone's rest pose (rows 0 and 2).
    if (character?.turn) {
        for (const list of [out.positions, out.normals])
            for (let at = 0; at < list.length; at += 3) {
                list[at] = 0 - list[at]!;
                list[at + 2] = 0 - list[at + 2]!;
            }
        for (const bone of skeleton?.bones ?? []) bone.rest = bone.rest.map((value, at) => (at % 4 === 0 || at % 4 === 2 ? 0 - value : value));
    }

    // 材质与贴图：按首次出现的顺序编号；相同的贴图字节只存一份。
    const materials: ImportedModelMaterial[] = [],
        textures: ImportedModelDescriptor['textures'] = [],
        textureBytes: Uint8Array[] = [],
        textureIndex = new Map<string, number>();
    /** A material's texture as an 8-bit, non-interlaced PNG: a PNG that already is one goes through as it is, anything else through the codec. */
    const normalise = async (texture: GltfTexture, label: string): Promise<{png: Uint8Array; width: number; height: number}> => {
        const raw = texture.getImage();
        requireImported(raw, 'texture-source', {name: label});
        const header = readPngHeader(raw!),
            mime = header ? ('image/png' as const) : ('image/jpeg' as const);
        let png: Uint8Array = header ? stripPngMetadata(raw!) : raw!,
            width = header?.width ?? 0,
            height = header?.height ?? 0;
        if (!header || header.bitDepth !== 8 || header.interlace !== 0 || (header.colorType !== 2 && header.colorType !== 6)) {
            try {
                ({png, width, height} = await codec(raw!, mime));
            } catch (error) {
                throw new ImportedModelError('image-decode', {name: label, detail: errorMessage(error)});
            }
            const after = readPngHeader(png);
            requireImported(
                after !== null &&
                    after.width === width &&
                    after.height === height &&
                    width <= importedModelLimits.textureSide &&
                    height <= importedModelLimits.textureSide,
                'image-decode',
                {name: label}
            );
        }
        return {png, width, height};
    };
    /** Stores PNG bytes in the package (equal bytes once) and returns the index. */
    const store = async (png: Uint8Array, width: number, height: number): Promise<number> => {
        const hash = await sha256Bytes(png);
        if (!textureIndex.has(hash)) {
            textureIndex.set(hash, textures.length);
            textures.push({sha256: hash, width, height});
            textureBytes.push(png);
        }
        return textureIndex.get(hash)!;
    };
    /** The package index of a material's texture, stored as it is; -1 without one. */
    const pack = async (texture: GltfTexture | null, info: object | null, label: string): Promise<number> => {
        if (!texture || !info) return -1;
        const {png, width, height} = await normalise(texture, label);
        return store(png, width, height);
    };
    const decoded = new Map<GltfTexture, Pixels>();
    /** The pixels of a material's texture, decoded once. */
    const pixelsOf = async (texture: GltfTexture, label: string): Promise<Pixels> => {
        if (!decoded.has(texture)) decoded.set(texture, decodePixels((await normalise(texture, label)).png));
        return decoded.get(texture)!;
    };
    const storePixels = (pixels: Pixels) => store(encodePixels(pixels), pixels.width, pixels.height);
    for (const [material, id] of [...materialIds].sort((left, right) => left[1] - right[1])) {
        const label = material.getName() || String(id),
            factor = material.getBaseColorFactor();
        const colorTexture = material.getBaseColorTexture(),
            occlusionTexture = material.getOcclusionTexture(),
            occlusionStrength = material.getOcclusionStrength();
        // The gear shader has no occlusion input: the occlusion (red) is multiplied into the colour. A strength of 0 changes nothing.
        // A character's tinted colour map is multiplied out (the gear shader has no tint under a map).
        const tinted = !!character && !!colorTexture && factor.some(channel => channel !== 1);
        const albedoOf = async (texture: GltfTexture) => (tinted ? tintPixels(await pixelsOf(texture, label), factor) : pixelsOf(texture, label));
        let index: number;
        if (occlusionTexture && occlusionStrength > 0) {
            const occlusion = await pixelsOf(occlusionTexture, label),
                albedo = colorTexture ? await albedoOf(colorTexture) : null;
            index = await storePixels(bakeOcclusion(albedo, [factor[0]!, factor[1]!, factor[2]!], occlusion, occlusionStrength));
        } else if (tinted) index = await storePixels(await albedoOf(colorTexture!));
        else index = await pack(colorTexture, material.getBaseColorTextureInfo(), label);
        // The normal map carries its glTF scale: the shader has no strength setting.
        const normalTexture = material.getNormalTexture(),
            normalScale = material.getNormalScale();
        const normalIndex =
            normalTexture && normalScale !== 1
                ? await storePixels(scaleNormals(await pixelsOf(normalTexture, label), normalScale))
                : await pack(normalTexture, material.getNormalTextureInfo(), label);
        // The gear shader reads metal in red and smoothness (1 - roughness) in green of one map; the glTF factors are folded in.
        const metalTexture = material.getMetallicRoughnessTexture();
        const metalIndex = metalTexture
            ? await storePixels(packMetalSmooth(await pixelsOf(metalTexture, label), material.getMetallicFactor(), material.getRoughnessFactor()))
            : -1;
        // 自发光颜色 = glTF 的 emissiveFactor × KHR_materials_emissive_strength（线性，1 为原色全亮，可大于 1）。
        const strength = material.getExtension<EmissiveStrength>('KHR_materials_emissive_strength')?.getEmissiveStrength() ?? 1;
        const [red, green, blue] = material.getEmissiveFactor().map(channel => channel * strength) as [number, number, number],
            glows = red > 0 || green > 0 || blue > 0;
        const emissiveTexture = glows ? await pack(material.getEmissiveTexture(), material.getEmissiveTextureInfo(), label) : -1;
        const blend = !!character && material.getAlphaMode() === 'BLEND',
            mask = material.getAlphaMode() === 'MASK' || (blend && characterBlend(index >= 0, factor[3]!) === 'mask');
        materials.push({
            name: material.getName() || 'material ' + id,
            baseColor: index >= 0 ? [1, 1, 1, 1] : [factor[0]!, factor[1]!, factor[2]!, mask ? factor[3]! : 1],
            texture: index,
            metallic: metalIndex >= 0 ? 1 : material.getMetallicFactor(),
            roughness: metalIndex >= 0 ? 1 : material.getRoughnessFactor(),
            alpha: mask ? 'mask' : 'opaque',
            cutoff: blend ? characterBlendCutoff : material.getAlphaCutoff(),
            normalTexture: normalIndex,
            metalTexture: metalIndex,
            emissiveColor: glows ? [red!, green!, blue!] : [0, 0, 0],
            emissiveTexture
        });
    }
    // 索引按材质分组：每个材质一个子网格，起点连续。
    const submeshes: ImportedModelDescriptor['submeshes'] = [],
        indices: number[] = [];
    for (const material of materials.keys()) {
        const list = out.indices.get(material);
        if (!list?.length) continue;
        submeshes.push({start: indices.length, count: list.length, material});
        for (const value of list) indices.push(value);
    }
    const faces = indices.length / 3,
        vertices = out.positions.length / 3;
    requireImported(importedMeshFits(vertices, indices.length, !!skeleton), 'mesh-too-large');
    const mesh = writeImportedMesh(
        Float32Array.from(out.positions),
        Float32Array.from(out.normals),
        Float32Array.from(out.uvs),
        Uint32Array.from(indices),
        skeleton ? {joints: Uint16Array.from(out.joints), weights: Float32Array.from(out.weights)} : undefined
    );
    const shape = {mesh: {sha256: await sha256Bytes(mesh), vertices, indices: indices.length}, submeshes, materials, textures};
    const descriptor: ImportedModelDescriptor = skeleton ? {format: 2, ...shape, skin: {bones: skeleton.bones}} : {format: 1, ...shape};
    return {descriptor, mesh, textures: textureBytes, stats: {faces, vertices, materials: materials.length, textures: textures.length}};
}
