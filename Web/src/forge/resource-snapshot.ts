import {isRecord} from './json.ts';
import {requireResource} from './resource-schema.ts';
import {readResourceRights} from './resource-rights.ts';
import type {ImportedModelPackage} from './imported-model.ts';
import type {ResourceReference} from './resource-contracts.ts';
import {t as authoringText} from '../shared/i18n/i18n.ts';

// 单件资源引用旁边的快照（合同 U-STORAGE-UI「资源与作品规则」A）：名字、作者、权利声明的来源，模型另带尺寸。
// 写进引用时取一次，此后不更新。资源被删后，引用处靠它显示名字、画占位框、写署名。这不是版本，也没有更新入口。

export type SingleResourceKind = 'model' | 'audio' | 'image' | 'animation';
export interface ResourceRightsNote {
    statement: string;
    source?: string;
}
export interface ResourceSnapshot {
    name: string;
    author?: string;
    rights?: ResourceRightsNote;
    /** 包围盒尺寸（米），只用来画占位框。 */ size?: [number, number, number];
}

const positive = (value: unknown) => typeof value === 'number' && Number.isFinite(Math.fround(value)) && value >= 0;
export function validateResourceSnapshot(value: unknown): asserts value is ResourceSnapshot {
    requireResource(
        isRecord(value) && Object.keys(value).every(field => ['name', 'author', 'rights', 'size'].includes(field)),
        authoringText('authoringMessages.resourceSnapshot.invalid')
    );
    requireResource(
        typeof value.name === 'string' && value.name.length > 0 && value.name.length <= 200,
        authoringText('authoringMessages.resourceSnapshot.invalid')
    );
    if (value.author !== undefined)
        requireResource(typeof value.author === 'string' && value.author.length <= 200, authoringText('authoringMessages.resourceSnapshot.invalid'));
    if (value.rights !== undefined)
        requireResource(
            isRecord(value.rights) &&
                typeof value.rights.statement === 'string' &&
                (value.rights.source === undefined || typeof value.rights.source === 'string'),
            authoringText('authoringMessages.resourceSnapshot.invalid')
        );
    if (value.size !== undefined)
        requireResource(
            Array.isArray(value.size) && value.size.length === 3 && value.size.every(positive),
            authoringText('authoringMessages.resourceSnapshot.invalid')
        );
}
/** 权利声明写进引用与署名的一行：声明项，「已获许可」另带原作者与许可来源（`resource-rights.ts`）。 */
export function rightsNote(rights: unknown): ResourceRightsNote | undefined {
    const read = readResourceRights(rights);
    if (!read) return undefined;
    return read.statement === 'permission'
        ? {statement: read.statement, source: read.originalAuthor + ' · ' + read.permissionSource}
        : {statement: read.statement};
}
export function communityResourceSnapshot(resource: {name: string; author: string; rights: unknown; size?: [number, number, number]}): ResourceSnapshot {
    const rights = rightsNote(resource.rights);
    return {
        name: resource.name,
        ...(resource.author ? {author: resource.author} : {}),
        ...(rights ? {rights} : {}),
        ...(resource.size ? {size: [...resource.size] as [number, number, number]} : {})
    };
}

/** 一件随包的资源的署名：README 与安装说明列它。没有作者的是作者自己本机的资源，署名归包作者。 */
export interface ResourceAttribution {
    kind: SingleResourceKind;
    id: string;
    name: string;
    author?: string;
    rights?: ResourceRightsNote;
}
/** 一件因为被删除而没有打进包的资源。 */
export interface RemovedFromPackage {
    kind: SingleResourceKind;
    id: string;
    name: string;
}

/** 导出一次的账：哪些资源打进了包、哪些因为被删除没有打进去。 */
export class ResourceLedger {
    private readonly shippedRows = new Map<string, ResourceAttribution>();
    private readonly goneRows = new Map<string, RemovedFromPackage>();
    ship(row: ResourceAttribution) {
        this.shippedRows.set(row.id, row);
    }
    gone(row: RemovedFromPackage) {
        this.goneRows.set(row.id, row);
    }
    isGone(id: string) {
        return this.goneRows.has(id);
    }
    /** 打进包的资源的署名：地图导出 README 读这一份。 */
    resourceAttributions(): ResourceAttribution[] {
        return [...this.shippedRows.values()].sort((left, right) => left.kind.localeCompare(right.kind) || left.name.localeCompare(right.name));
    }
    removedResources(): RemovedFromPackage[] {
        return [...this.goneRows.values()].sort((left, right) => left.kind.localeCompare(right.kind) || left.name.localeCompare(right.name));
    }
}

/**
 * 导出时取单件资源字节的一处入口：本机有就用本机，否则按 ID 从社区取。
 * 返回 null 表示这件已被删除（不进包，记进账），网络不通或要登录时抛错（可重试，不算删除）。
 */
export interface ExportResources {
    readonly ledger: ResourceLedger;
    /** Ogg 字节的 base64。 */
    sound(reference: ResourceReference): Promise<string | null>;
    /** 动作文件（glb）字节的 base64。 */
    animation(reference: ResourceReference): Promise<string | null>;
    /** 上传字节的 base64。 */
    image(reference: ResourceReference): Promise<string | null>;
    model(reference: ResourceReference): Promise<ImportedModelPackage | null>;
}

/** 一份内容里引用的、字节不在内容里的单件资源：导入模型零件、只留引用的声音和图片。 */
export interface ResourceUse {
    kind: SingleResourceKind;
    id: string;
    name: string;
}
export function workResourceUses(work: unknown): ResourceUse[] {
    const found = new Map<string, ResourceUse>();
    const visit = (node: unknown): void => {
        if (Array.isArray(node)) {
            node.forEach(visit);
            return;
        }
        if (!isRecord(node)) return;
        const id = isRecord(node.reference) ? node.reference.id : undefined;
        if (typeof id === 'string') {
            const kind =
                node.kind === 'imported' && id.startsWith('imported:')
                    ? 'model'
                    : node.data === '' && id.startsWith('audio:')
                      ? 'audio'
                      : node.data === '' && id.startsWith('image:')
                        ? 'image'
                        : node.data === '' && id.startsWith('animation:')
                          ? 'animation'
                          : null;
            if (kind)
                found.set(id, {
                    kind,
                    id,
                    name:
                        kind === 'model'
                            ? isRecord(node.snapshot) && typeof node.snapshot.name === 'string'
                                ? node.snapshot.name
                                : ''
                            : typeof node.name === 'string'
                              ? node.name
                              : ''
                });
        }
        for (const child of Object.values(node)) visit(child);
    };
    visit(work);
    return [...found.values()];
}
