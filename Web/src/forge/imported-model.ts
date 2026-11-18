import {sha256Bytes} from './lib/hash.ts';
import {
    importedMeshExtension,
    readImportedMesh,
    readImportedModelDescriptor,
    readPngHeader,
    requireImported,
    type ImportedModelDescriptor
} from './imported-model-format.ts';
import type {ResourceReference} from './resource-contracts.ts';
import {validateResourceReference} from './resource-schema.ts';
import {
    createForgeEngineResource,
    runtimeResourceFileName,
    verifyForgeEngineResource,
    writeForgeEngineResource,
    type ForgeEngineResource
} from './runtime-resources.ts';
import type {ExportResources, ResourceSnapshot} from './resource-snapshot.ts';

// 导入模型（合同 §8「玩家上传模型」）：一份描述符（运行资源，kind `model`）加一个 mesh 旁文件和若干 PNG 旁文件。
// 格式本身（上限、描述符与 mesh 旁文件的读写）在 imported-model-format.ts；这里是网站这一层：包的资源身份、核对与写进内容包。
// 本文件不依赖浏览器，转换器、本机库、导出与服务器校验共用同一份读法。

/** 一个导入模型的全部字节：`textures` 与描述符的贴图表同序。 */
export interface ImportedModelPackage {
    reference: ResourceReference;
    name: string;
    descriptor: ImportedModelDescriptor;
    mesh: Uint8Array;
    textures: Uint8Array[];
}

export const importedModelIdPattern = /^imported:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function newImportedModelId(): string {
    return 'imported:' + crypto.randomUUID();
}

/** 描述符加字节还原成一份已核对的包：每个旁文件的哈希、PNG 尺寸、mesh 内容与 revision 都重算一遍，任何一项不符都拒绝。 */
export async function verifyImportedModelPackage(input: ImportedModelPackage): Promise<ImportedModelPackage> {
    validateResourceReference(input.reference);
    requireImported(importedModelIdPattern.test(input.reference.id), 'model-id');
    const descriptor = readImportedModelDescriptor(input.descriptor);
    requireImported((await sha256Bytes(input.mesh)) === descriptor.mesh.sha256, 'mesh-hash');
    readImportedMesh(input.mesh, descriptor);
    requireImported(input.textures.length === descriptor.textures.length, 'texture-count');
    for (const [index, texture] of descriptor.textures.entries()) {
        const bytes = input.textures[index]!,
            header = readPngHeader(bytes);
        requireImported((await sha256Bytes(bytes)) === texture.sha256, 'texture-hash', {texture: index});
        requireImported(header !== null && header.width === texture.width && header.height === texture.height, 'texture-size', {texture: index});
    }
    const resource = await importedModelResource(input.reference.id, descriptor);
    requireImported(resource.reference.revision === input.reference.revision, 'revision-mismatch');
    return {reference: {id: input.reference.id, revision: input.reference.revision}, name: input.name, descriptor, mesh: input.mesh, textures: input.textures};
}

/** 描述符的运行资源：id 是导入模型自己的身份，revision 由描述符内容（含 mesh 与贴图哈希）决定，作者改名不动它。 */
export function importedModelResource(id: string, descriptor: ImportedModelDescriptor): Promise<ForgeEngineResource<ImportedModelDescriptor>> {
    return createForgeEngineResource(id, 'model', descriptor);
}

export const importedMeshFile = (sha256: string) => `forge/resources/${sha256}.${importedMeshExtension}`;
export const importedTextureFile = (sha256: string) => `forge/resources/${sha256}.png`;

/** 一个包写进内容包的四类文件：`forge/resources/<revision>.resource.forge`、mesh 旁文件和各 PNG（同一哈希只写一份）。 */
export async function importedModelFiles(input: ImportedModelPackage, pluginFolder: string): Promise<Record<string, string | Uint8Array>> {
    const checked = await verifyImportedModelPackage(input),
        resource = await verifyForgeEngineResource(await importedModelResource(checked.reference.id, checked.descriptor));
    const files: Record<string, string | Uint8Array> = {
        [pluginFolder + '/' + runtimeResourceFileName(checked.reference)]: writeForgeEngineResource(resource),
        [pluginFolder + '/' + importedMeshFile(checked.descriptor.mesh.sha256)]: checked.mesh
    };
    for (const [index, texture] of checked.descriptor.textures.entries())
        files[pluginFolder + '/' + importedTextureFile(texture.sha256)] = checked.textures[index]!;
    return files;
}

export const importedModelKey = (reference: ResourceReference) => reference.id + '\0' + reference.revision;

/** One imported model a package uses; its snapshot gives the name, author and rights the package credits. */
export interface ImportedModelUse {
    reference: ResourceReference;
    snapshot?: ResourceSnapshot;
}
/** The imported models compiled model attachments use (enemy variants, weapons and map placements alike). */
export function compiledImportedModelUses(
    contents: readonly {modelAssemblies: readonly {importedParts?: readonly {resource: ResourceReference}[]}[]}[]
): ImportedModelUse[] {
    return contents.flatMap(content => content.modelAssemblies.flatMap(assembly => (assembly.importedParts ?? []).map(part => ({reference: part.resource}))));
}
/** The descriptor, mesh and texture files of every imported model a package uses, each written once: every workshop's export (map package,
 *  weapon and enemy content packs) writes its imported models here. `resources` gets the bytes (this device, else the community by id); a model
 *  that has been deleted stays out of the package and goes on the ledger's list. Without `resources` (the dependency preview) no file is written. */
export async function importedModelUseFiles(
    uses: Iterable<ImportedModelUse>,
    pluginFolder: string,
    resources?: ExportResources
): Promise<Record<string, string | Uint8Array>> {
    const wanted = new Map<string, ImportedModelUse>();
    for (const use of uses) {
        const key = importedModelKey(use.reference);
        if (!wanted.get(key)?.snapshot) wanted.set(key, use);
    }
    const files: Record<string, string | Uint8Array> = {};
    if (!resources) return files;
    for (const {reference, snapshot} of wanted.values()) {
        const model = await resources.model(reference);
        if (!model) {
            resources.ledger.gone({kind: 'model', id: reference.id, name: snapshot?.name ?? reference.id});
            continue;
        }
        Object.assign(files, await importedModelFiles(model, pluginFolder));
        resources.ledger.ship({
            kind: 'model',
            id: reference.id,
            name: snapshot?.name ?? model.name,
            ...(snapshot?.author ? {author: snapshot.author} : {}),
            ...(snapshot?.rights ? {rights: snapshot.rights} : {})
        });
    }
    return files;
}
