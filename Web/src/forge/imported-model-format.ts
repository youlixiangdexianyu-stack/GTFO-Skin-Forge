import {isRecord} from './lib/is-record.ts';

// 导入模型的格式本身（合同 §8「玩家上传模型」）：上限、描述符与 mesh 旁文件的读写、PNG 头与拒收码。格式规范归 ForgeVisual
// （`ImportedModelFormat.cs`）：这里写的描述符与 mesh 旁文件必须逐字段、逐字节与它读的一致。本文件只依赖数据，不依赖网站的资源层、
// 文案与页面：网站、服务器与 ForgeSkin 编辑器都直接用它；拒收只给码与参数，文案留给各自的调用方。

/** 格式只有技术上限（`ImportedModelFormat.cs` 的 `Max*`）：mesh 旁文件整份读成一个字节数组、子网格建成一个索引数组（.NET 最大数组长度），
 *  骨骼下标是 16 位，贴图边长是 Unity Direct3D 11 的上限。材质数、贴图数、文件大小没有上限。 */
const formatLimits = {arrayBytes: 0x7fffffc7, textureSide: 16384, bones: 65536};

/** 导入器的界限；转换器、本机库读同一份。`advised` 是网站建议值，超出只给提示（`importedModelAdvice`），不拒绝；
 *  服务器的上传限额是它自己的存储与解码策略（`upload-policy.cts`）。 */
export const importedModelLimits = Object.freeze({
    /** 贴图边长（像素）。 */
    textureSide: formatLimits.textureSide,
    bones: formatLimits.bones,
    /** 变换后任一坐标的绝对值（米）。 */
    extent: 1000,
    nameLength: 80,
    advised: Object.freeze({faces: 100_000, materials: 8, textures: 8, textureSide: 2048})
});

/** 一个模型超出建议值的项（提示码，对应 `community.warning.<码>`）；没超出硬上限，所以不拒绝。 */
export function importedModelAdvice(descriptor: ImportedModelDescriptor): string[] {
    const advised = importedModelLimits.advised,
        out: string[] = [];
    if (importedDrawnIndices(descriptor) / 3 > advised.faces) out.push('model-faces');
    if (descriptor.materials.length > advised.materials) out.push('model-materials');
    if (descriptor.textures.length > advised.textures) out.push('model-textures');
    if (descriptor.textures.some(texture => Math.max(texture.width, texture.height) > advised.textureSide)) out.push('model-texture-size');
    return out;
}

/** 子网格的索引数之和：游戏按它建网格，子网格可以重叠，所以它可以大于 `mesh.indices`；面数与内存都按它算。 */
export const importedDrawnIndices = (descriptor: ImportedModelDescriptor) => descriptor.submeshes.reduce((total, submesh) => total + submesh.count, 0);
export const importedMeshHeaderBytes = 16;
const meshMagic = 0x48534d46;
/** 每顶点：位置 3、法线 3、UV 2 个 float。 */
export const importedMeshVertexBytes = 32;
/** 带蒙皮（格式 2）时索引之后每顶点：4 个 uint16 骨骼下标、4 个 float 权重。 */
export const importedMeshSkinBytes = 24;
export const importedMeshExtension = 'fmesh';

/**
 * `texture` is the base-colour map (sRGB), `normalTexture` the tangent-space normal map (linear, OpenGL +Y, scale 1) and `emissiveTexture` the
 * emission map (sRGB); each is an index into the descriptor's textures or -1. The surface glows by `emissiveColor` (linear, glTF's emission factor times its strength: 1 is the colour at full brightness, above 1 brighter) times its
 * emission map; an all-zero colour is no emission and then has no emission map. A double-sided surface has no field: the converter writes its back faces into the mesh.
 */
export type ImportedModelMaterial = {
    name: string;
    baseColor: [number, number, number, number];
    texture: number;
    metallic: number;
    roughness: number;
    alpha: 'opaque' | 'mask';
    cutoff: number;
    normalTexture: number;
    metalTexture: number;
    emissiveColor: [number, number, number];
    emissiveTexture: number;
};
/** 一根蒙皮骨骼：`parent` 是骨骼表里更靠前的下标（根为 -1），`rest` 是它在模型自身坐标（游戏轴）里的静止姿态，列主序 4×4。 */
export type ImportedModelBone = {name: string; parent: number; rest: number[]};
/** 格式 1 是静态模型；格式 2 多一份蒙皮骨架 `skin`，mesh 旁文件也是格式 2（多每顶点的四骨权重）。 */
export type ImportedModelDescriptor = {
    format: 1 | 2;
    mesh: {sha256: string; vertices: number; indices: number};
    submeshes: {start: number; count: number; material: number}[];
    materials: ImportedModelMaterial[];
    textures: {sha256: string; width: number; height: number}[];
    skin?: {bones: ImportedModelBone[]};
};

const hashPattern = /^[0-9a-f]{64}$/;

export class ImportedModelError extends Error {
    readonly code: string;
    readonly params: Record<string, string | number>;
    constructor(code: string, params: Record<string, string | number> = {}) {
        super(
            code +
                (Object.keys(params).length
                    ? ': ' +
                      Object.entries(params)
                          .map(([key, value]) => key + '=' + String(value))
                          .join(', ')
                    : '')
        );
        this.name = 'ImportedModelError';
        this.code = code;
        this.params = params;
    }
}
export function requireImported(ok: unknown, code: string, params: Record<string, string | number> = {}): asserts ok {
    if (!ok) throw new ImportedModelError(code, params);
}

const inUnit = (value: unknown): value is number => typeof value === 'number' && value >= 0 && value <= 1;
const nonNegative = (value: unknown): value is number => typeof value === 'number' && value >= 0 && Number.isFinite(Math.fround(value)); // the game reads it as a 32-bit float
function fields(value: unknown, names: readonly string[], code: string): asserts value is Record<string, unknown> {
    requireImported(isRecord(value) && Object.keys(value).length === names.length && names.every(name => Object.hasOwn(value, name)), code);
}

/** 描述符的结构读法，规则与 ForgeVisual `ImportedModelDescriptor.Parse` 一致；多余字段、缺字段、越界都拒绝。 */
export function readImportedModelDescriptor(value: unknown): ImportedModelDescriptor {
    requireImported(isRecord(value) && (value.format === 1 || value.format === 2), 'descriptor-shape');
    fields(value, ['format', 'mesh', 'submeshes', 'materials', 'textures', ...(value.format === 2 ? ['skin'] : [])], 'descriptor-shape');
    const mesh = value.mesh;
    fields(mesh, ['sha256', 'vertices', 'indices'], 'descriptor-shape');
    requireImported(typeof mesh.sha256 === 'string' && hashPattern.test(mesh.sha256), 'descriptor-hash');
    const vertices = mesh.vertices,
        indices = mesh.indices;
    requireImported(Number.isSafeInteger(vertices) && (vertices as number) >= 3, 'descriptor-mesh');
    requireImported(Number.isSafeInteger(indices) && (indices as number) >= 3 && (indices as number) % 3 === 0, 'descriptor-mesh');
    requireImported(importedMeshFits(vertices as number, indices as number, value.format === 2), 'descriptor-mesh');
    requireImported(Array.isArray(value.textures), 'descriptor-texture');
    const textures = value.textures.map(texture => {
        fields(texture, ['sha256', 'width', 'height'], 'descriptor-texture');
        requireImported(typeof texture.sha256 === 'string' && hashPattern.test(texture.sha256), 'descriptor-hash');
        requireImported(
            Number.isSafeInteger(texture.width) &&
                Number.isSafeInteger(texture.height) &&
                (texture.width as number) >= 1 &&
                (texture.height as number) >= 1 &&
                (texture.width as number) <= formatLimits.textureSide &&
                (texture.height as number) <= formatLimits.textureSide,
            'descriptor-texture'
        );
        return {sha256: texture.sha256 as string, width: texture.width as number, height: texture.height as number};
    });
    requireImported(Array.isArray(value.materials) && value.materials.length >= 1, 'descriptor-material');
    const materials = value.materials.map(material => {
        fields(
            material,
            ['name', 'baseColor', 'texture', 'metallic', 'roughness', 'alpha', 'cutoff', 'normalTexture', 'metalTexture', 'emissiveColor', 'emissiveTexture'],
            'descriptor-material'
        );
        const color = material.baseColor,
            texture = material.texture,
            normal = material.normalTexture,
            metal = material.metalTexture,
            emissive = material.emissiveColor,
            emissiveTexture = material.emissiveTexture;
        requireImported(
            typeof material.name === 'string' && material.name.length > 0 && material.name.length <= 200 && !/[\u0000-\u001f\u007f]/.test(material.name),
            'descriptor-material'
        );
        requireImported(Array.isArray(color) && color.length === 4 && color.every(inUnit), 'descriptor-material');
        requireImported(Number.isSafeInteger(texture) && (texture as number) >= -1 && (texture as number) < textures.length, 'descriptor-material');
        requireImported((texture as number) < 0 || color.every(channel => channel === 1), 'descriptor-material');
        requireImported(inUnit(material.metallic) && inUnit(material.roughness) && inUnit(material.cutoff), 'descriptor-material');
        requireImported(material.alpha === 'opaque' || material.alpha === 'mask', 'descriptor-material');
        requireImported(material.alpha === 'opaque' || (texture as number) >= 0, 'descriptor-material');
        requireImported(Number.isSafeInteger(normal) && (normal as number) >= -1 && (normal as number) < textures.length, 'descriptor-material');
        requireImported(Number.isSafeInteger(metal) && (metal as number) >= -1 && (metal as number) < textures.length, 'descriptor-material');
        requireImported(
            Number.isSafeInteger(emissiveTexture) && (emissiveTexture as number) >= -1 && (emissiveTexture as number) < textures.length,
            'descriptor-material'
        );
        requireImported(Array.isArray(emissive) && emissive.length === 3 && emissive.every(nonNegative), 'descriptor-material');
        requireImported((emissiveTexture as number) < 0 || emissive.some(channel => channel > 0), 'descriptor-material');
        return {
            name: material.name,
            baseColor: [color[0], color[1], color[2], color[3]] as [number, number, number, number],
            texture: texture as number,
            metallic: material.metallic,
            roughness: material.roughness,
            alpha: material.alpha as ImportedModelMaterial['alpha'],
            cutoff: material.cutoff,
            normalTexture: normal as number,
            metalTexture: metal as number,
            emissiveColor: [emissive[0], emissive[1], emissive[2]] as [number, number, number],
            emissiveTexture: emissiveTexture as number
        };
    });
    requireImported(Array.isArray(value.submeshes) && value.submeshes.length >= 1, 'descriptor-submesh');
    let drawn = 0;
    const submeshes = value.submeshes.map(submesh => {
        fields(submesh, ['start', 'count', 'material'], 'descriptor-submesh');
        const {start, count, material} = submesh as {start: number; count: number; material: number};
        requireImported(
            Number.isSafeInteger(start) &&
                Number.isSafeInteger(count) &&
                Number.isSafeInteger(material) &&
                start >= 0 &&
                count >= 3 &&
                start % 3 === 0 &&
                count % 3 === 0 &&
                start + count <= (indices as number) &&
                material >= 0 &&
                material < materials.length,
            'descriptor-submesh'
        );
        // 同一段索引列两次算两次：游戏把所有子网格建成一个索引数组（与 ForgeVisual `DrawnIndices` 一致）。
        drawn += count;
        requireImported(drawn <= formatLimits.arrayBytes, 'descriptor-submesh');
        return {start, count, material};
    });
    const out: ImportedModelDescriptor = {
        format: value.format as 1 | 2,
        mesh: {sha256: mesh.sha256 as string, vertices: vertices as number, indices: indices as number},
        submeshes,
        materials,
        textures
    };
    if (value.format === 2) out.skin = {bones: readBones(value.skin)};
    return out;
}

/** 蒙皮骨架：1 到 65536 根（骨骼下标 16 位），名字唯一，父骨在前，静止姿态 16 个有限数。规则与 ForgeVisual 一致。 */
function readBones(value: unknown): ImportedModelBone[] {
    fields(value, ['bones'], 'descriptor-skin');
    const rows = value.bones;
    requireImported(Array.isArray(rows) && rows.length >= 1 && rows.length <= formatLimits.bones, 'descriptor-skin');
    const names = new Set<string>();
    return rows.map((bone, index) => {
        fields(bone, ['name', 'parent', 'rest'], 'descriptor-skin');
        const {name, parent, rest} = bone;
        requireImported(
            typeof name === 'string' && name.length > 0 && name.length <= 200 && !/[\u0000-\u001f\u007f]/.test(name) && !names.has(name),
            'descriptor-skin'
        );
        names.add(name);
        requireImported(Number.isSafeInteger(parent) && (parent as number) >= -1 && (parent as number) < index, 'descriptor-skin');
        requireImported(
            Array.isArray(rest) && rest.length === 16 && rest.every(entry => typeof entry === 'number' && Number.isFinite(Math.fround(entry))),
            'descriptor-skin'
        );
        return {name, parent: parent as number, rest: [...(rest as number[])]};
    });
}

const pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
export interface PngHeader {
    width: number;
    height: number;
    bitDepth: number;
    colorType: number;
    interlace: number;
}
/** PNG 的 IHDR：只读前 29 字节，不解码；不是 PNG 或 IHDR 不对返回 null。 */
export function readPngHeader(bytes: Uint8Array): PngHeader | null {
    if (bytes.length < 29 || !pngSignature.every((byte, index) => bytes[index] === byte)) return null;
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (view.getUint32(8) !== 13 || String.fromCharCode(bytes[12]!, bytes[13]!, bytes[14]!, bytes[15]!) !== 'IHDR') return null;
    const width = view.getUint32(16),
        height = view.getUint32(20);
    if (width < 1 || height < 1) return null;
    return {width, height, bitDepth: bytes[24]!, colorType: bytes[25]!, interlace: bytes[28]!};
}

/** 服务端准入对带文字、时间与 EXIF 块的 PNG 只告警、不拒绝（它不能改字节：revision 是这些字节的哈希），所以转换时在客户端去掉这些块，
 *  让告警不出现；像素流（IDAT）和其余块原样保留。块长越界时不动，交给准入报错。 */
export function stripPngMetadata(bytes: Uint8Array): Uint8Array {
    const drop = new Set(['eXIf', 'tEXt', 'zTXt', 'iTXt', 'tIME']),
        view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength),
        kept: Uint8Array[] = [bytes.subarray(0, 8)];
    let at = 8,
        dropped = false;
    while (at + 12 <= bytes.length) {
        const end = at + 12 + view.getUint32(at);
        if (end > bytes.length) return bytes;
        if (drop.has(String.fromCharCode(bytes[at + 4]!, bytes[at + 5]!, bytes[at + 6]!, bytes[at + 7]!))) dropped = true;
        else kept.push(bytes.subarray(at, end));
        at = end;
    }
    if (!dropped || at !== bytes.length) return bytes;
    const out = new Uint8Array(kept.reduce((total, part) => total + part.length, 0));
    let cursor = 0;
    for (const part of kept) {
        out.set(part, cursor);
        cursor += part.length;
    }
    return out;
}

/** 每顶点四根骨骼的下标与权重（权重和为 1）。 */
export interface ImportedMeshSkin {
    joints: Uint16Array;
    weights: Float32Array;
}
const meshBytes = (vertices: number, indices: number, skinned: boolean) =>
    importedMeshHeaderBytes + vertices * importedMeshVertexBytes + indices * 4 + (skinned ? vertices * importedMeshSkinBytes : 0);
/** 这些顶点与索引的 mesh 旁文件能否被游戏整份读成一个字节数组。 */
export const importedMeshFits = (vertices: number, indices: number, skinned: boolean) => meshBytes(vertices, indices, skinned) <= formatLimits.arrayBytes;
/** mesh 旁文件：`FMSH`、格式（1 静态、2 蒙皮）、顶点数、索引数（各 uint32 小端），然后每顶点 8 个 float，然后 uint32 索引；
 *  格式 2 再接每顶点 4 个 uint16 骨骼下标与 4 个 float 权重。 */
export function writeImportedMesh(
    positions: Float32Array,
    normals: Float32Array,
    uvs: Float32Array,
    indices: Uint32Array,
    skin?: ImportedMeshSkin
): Uint8Array {
    const vertices = positions.length / 3,
        bytes = new Uint8Array(meshBytes(vertices, indices.length, !!skin)),
        view = new DataView(bytes.buffer);
    view.setUint32(0, meshMagic, true);
    view.setUint32(4, skin ? 2 : 1, true);
    view.setUint32(8, vertices, true);
    view.setUint32(12, indices.length, true);
    let at = importedMeshHeaderBytes;
    for (let vertex = 0; vertex < vertices; vertex++) {
        for (let k = 0; k < 3; k++, at += 4) view.setFloat32(at, positions[vertex * 3 + k]!, true);
        for (let k = 0; k < 3; k++, at += 4) view.setFloat32(at, normals[vertex * 3 + k]!, true);
        for (let k = 0; k < 2; k++, at += 4) view.setFloat32(at, uvs[vertex * 2 + k]!, true);
    }
    for (let index = 0; index < indices.length; index++, at += 4) view.setUint32(at, indices[index]!, true);
    if (skin)
        for (let vertex = 0; vertex < vertices; vertex++) {
            for (let k = 0; k < 4; k++, at += 2) view.setUint16(at, skin.joints[vertex * 4 + k]!, true);
            for (let k = 0; k < 4; k++, at += 4) view.setFloat32(at, skin.weights[vertex * 4 + k]!, true);
        }
    return bytes;
}

export interface ImportedMeshData {
    positions: Float32Array;
    normals: Float32Array;
    uvs: Float32Array;
    indices: Uint32Array;
    skin?: ImportedMeshSkin;
}
/** 读 mesh 旁文件并做 ForgeVisual 同样的检查：长度、文件头、有限数、可用法线、索引不越过顶点；蒙皮的骨骼下标在骨骼表内、权重非负且和为 1。 */
export function readImportedMesh(bytes: Uint8Array, descriptor: ImportedModelDescriptor): ImportedMeshData {
    const {vertices, indices} = descriptor.mesh,
        bones = descriptor.skin?.bones.length ?? 0;
    requireImported(bytes.length === meshBytes(vertices, indices, bones > 0), 'mesh-size');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    requireImported(view.getUint32(0, true) === meshMagic && view.getUint32(4, true) === descriptor.format, 'mesh-header');
    requireImported(view.getUint32(8, true) === vertices && view.getUint32(12, true) === indices, 'mesh-header');
    const positions = new Float32Array(vertices * 3),
        normals = new Float32Array(vertices * 3),
        uvs = new Float32Array(vertices * 2),
        triangles = new Uint32Array(indices);
    let at = importedMeshHeaderBytes;
    for (let vertex = 0; vertex < vertices; vertex++) {
        for (let k = 0; k < 3; k++, at += 4) positions[vertex * 3 + k] = view.getFloat32(at, true);
        for (let k = 0; k < 3; k++, at += 4) normals[vertex * 3 + k] = view.getFloat32(at, true);
        for (let k = 0; k < 2; k++, at += 4) uvs[vertex * 2 + k] = view.getFloat32(at, true);
        const [nx, ny, nz] = [normals[vertex * 3]!, normals[vertex * 3 + 1]!, normals[vertex * 3 + 2]!];
        requireImported(
            [...positions.subarray(vertex * 3, vertex * 3 + 3), nx, ny, nz, uvs[vertex * 2]!, uvs[vertex * 2 + 1]!].every(Number.isFinite),
            'mesh-non-finite',
            {vertex}
        );
        requireImported(nx * nx + ny * ny + nz * nz >= 0.25, 'mesh-normal', {vertex});
    }
    for (let index = 0; index < indices; index++, at += 4) {
        const value = view.getUint32(at, true);
        requireImported(value < vertices, 'mesh-index', {index});
        triangles[index] = value;
    }
    if (!bones) return {positions, normals, uvs, indices: triangles};
    const joints = new Uint16Array(vertices * 4),
        weights = new Float32Array(vertices * 4);
    for (let vertex = 0; vertex < vertices; vertex++) {
        for (let k = 0; k < 4; k++, at += 2) {
            joints[vertex * 4 + k] = view.getUint16(at, true);
            requireImported(joints[vertex * 4 + k]! < bones, 'mesh-skin', {vertex});
        }
        let sum = 0;
        for (let k = 0; k < 4; k++, at += 4) {
            const weight = view.getFloat32(at, true);
            requireImported(Number.isFinite(weight) && weight >= 0, 'mesh-skin', {vertex});
            weights[vertex * 4 + k] = weight;
            sum += weight;
        }
        requireImported(Math.abs(sum - 1) <= 0.01, 'mesh-skin', {vertex});
    }
    return {positions, normals, uvs, indices: triangles, skin: {joints, weights}};
}

/** 包围盒尺寸（米）：单件资源引用的快照带它，模型被删后用来画占位框。 */
export function importedMeshSize(bytes: Uint8Array, descriptor: ImportedModelDescriptor): [number, number, number] {
    const {positions} = readImportedMesh(bytes, descriptor),
        low = [Infinity, Infinity, Infinity],
        high = [-Infinity, -Infinity, -Infinity];
    for (let at = 0; at < positions.length; at++) {
        const axis = at % 3,
            value = positions[at]!;
        if (value < low[axis]!) low[axis] = value;
        if (value > high[axis]!) high[axis] = value;
    }
    return [high[0]! - low[0]!, high[1]! - low[1]!, high[2]! - low[2]!];
}

/** 网格的每个三角（每个九个数，网格自身坐标），换回预览的轴与绕向（X 取反、绕向反过来，与 imported-model-scene.ts 同），给 model-hit-mesh.ts 做受击网格。 */
export function importedMeshTriangles(bytes: Uint8Array, descriptor: ImportedModelDescriptor): number[] {
    const {positions, indices} = readImportedMesh(bytes, descriptor),
        out: number[] = [];
    for (let at = 0; at < indices.length; at += 3)
        for (const index of [indices[at]!, indices[at + 2]!, indices[at + 1]!])
            out.push(-positions[3 * index]!, positions[3 * index + 1]!, positions[3 * index + 2]!);
    return out;
}
