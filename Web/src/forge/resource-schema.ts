import {resourceDomains} from './generated/wire.ts';
import {sha256Bytes} from './lib/hash.ts';
import {isRelative} from './lib/posix-path.ts';
import {t as englishErrorText} from '../shared/i18n/i18n.ts';
import type {JsonValue} from './contracts.ts';
import {assertForgeJson, canonicalResourceJson, isImmutableForgeJson, isRecord, errorMessage, isFiniteNumber} from './json.ts';
import type {
    ResourceBytes,
    ResourceComposition,
    ResourceDefinition,
    ResourceReference,
    AuthorAudioReference,
    ResourceTransform,
    SourceObjectIdentity
} from './resource-contracts.ts';
import {assetPaths} from './asset-paths.ts';
import {formatVersion} from './forge-version.ts';

export const resourceHash = /^[a-f0-9]{64}$/;
const resourcePackagePin = /^[A-Za-z0-9_]+-[A-Za-z0-9_]+-(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/;

const ordinarySnapshots = new WeakMap<object, boolean>();
const snapshotComparisons = new WeakMap<object, WeakMap<object, boolean>>();
// The JSON gate visits enumerable keys, whereas Array.map also visits hidden indices.
// Preserve the serializer for those rare shapes and shadowed array methods.
function ordinaryArrays(value: JsonValue, seen?: WeakSet<object>): boolean {
    if (value === null || typeof value !== 'object') return true;
    const immutable = isImmutableForgeJson(value),
        known = immutable ? ordinarySnapshots.get(value) : undefined;
    if (known !== undefined) return known;
    seen ??= new WeakSet<object>();
    if (seen.has(value)) return true;
    seen.add(value);
    let ordinary: boolean;
    if (Array.isArray(value)) {
        const keys = Object.keys(value);
        if (Object.getPrototypeOf(value) !== Array.prototype || keys.length !== value.length || Object.getOwnPropertyNames(value).length !== value.length + 1)
            ordinary = false;
        else ordinary = keys.every((key, index) => key === String(index) && ordinaryArrays(value[index], seen));
    } else ordinary = Object.values(value).every(child => ordinaryArrays(child, seen));
    if (immutable) ordinarySnapshots.set(value, ordinary);
    return ordinary;
}
function equalResourceValues(a: JsonValue, b: JsonValue): boolean {
    if (a === b) return true; // JSON numbers deliberately treat -0 and 0 alike.
    if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return false;
    if (Array.isArray(a)) {
        if (!Array.isArray(b) || a.length !== b.length) return false;
        for (let index = 0; index < a.length; index++) if (!equalResourceValues(a[index], b[index])) return false;
        return true;
    }
    if (Array.isArray(b)) return false;
    const keys = Object.keys(a).sort(),
        rightKeys = Object.keys(b).sort();
    return keys.length === rightKeys.length && keys.every((key, index) => key === rightKeys[index] && equalResourceValues(a[key], b[key]));
}

/** Equal references still cross the JSON gate. Only immutable, previously compared content
 * can skip the walk; two different frozen snapshots are not necessarily equal. */
export function sameResourceContent(left: unknown, right: unknown): boolean {
    const immutable = isImmutableForgeJson(left) && isImmutableForgeJson(right);
    if (immutable) {
        const known = snapshotComparisons.get(left as object)?.get(right as object);
        if (known !== undefined) return known;
    }
    assertForgeJson(left, 'resource content');
    if (!ordinaryArrays(left)) return canonicalResourceJson(left) === canonicalResourceJson(right);
    assertForgeJson(right, 'resource content');
    if (!ordinaryArrays(right)) return canonicalResourceJson(left) === canonicalResourceJson(right);
    if (left === right) return true;
    const equal = equalResourceValues(left, right);
    if (isImmutableForgeJson(left) && isImmutableForgeJson(right)) {
        let compared = snapshotComparisons.get(left as object);
        if (!compared) {
            compared = new WeakMap<object, boolean>();
            snapshotComparisons.set(left as object, compared);
        }
        compared.set(right as object, equal);
    }
    return equal;
}

export async function hashResourceContent(value: unknown): Promise<string> {
    return sha256Bytes(new TextEncoder().encode(canonicalResourceJson(value)));
}

export function requireResource(ok: unknown, message: string): asserts ok {
    if (!ok) throw Error(message);
}

const text = (value: unknown): value is string => typeof value === 'string' && Boolean(value.trim()) && value.length <= 2048 && !/[\x00-\x1f]/.test(value);

export function resourcePath(value: unknown): value is string {
    return text(value) && !/[:?#]/.test(value) && isRelative(value);
}

function object(value: unknown, fields: readonly string[], path: string): asserts value is Record<string, unknown> {
    requireResource(isRecord(value), 'Invalid resource object: ' + path);
    requireResource(
        Object.keys(value).every(key => fields.includes(key)),
        'Unknown resource field: ' + path
    );
}

function strings(value: unknown, path: string): asserts value is string[] {
    requireResource(Array.isArray(value) && value.every(text) && new Set(value).size === value.length, 'Invalid resource list: ' + path);
}

export function validateSourceObject(value: unknown, path = 'source'): asserts value is SourceObjectIdentity {
    object(value, ['file', 'pathId'], path);
    const file = value.file,
        pathId = value.pathId;
    requireResource(resourcePath(file) && !file.includes('/'), 'Invalid Unity source file: ' + path);
    requireResource(
        typeof pathId === 'string' && /^(?:0|-?[1-9]\d*)$/.test(pathId) && pathId.length <= 20,
        'Unity pathId must be an exact decimal string: ' + path
    );
    const numeric = BigInt(pathId);
    requireResource(numeric >= -(1n << 63n) && numeric < 1n << 63n, 'Unity pathId exceeds int64: ' + path);
}

export function validateResourceReference(value: unknown): asserts value is ResourceReference {
    object(value, ['id', 'revision'], 'reference');
    const id = value.id,
        revision = value.revision;
    requireResource(
        text(id) && id.length <= 512 && !/[\s/\\]/.test(id) && typeof revision === 'string' && resourceHash.test(revision),
        'Invalid locked resource reference'
    );
}

export function validateAuthorAudioReference(value: unknown, path = 'author audio reference'): asserts value is AuthorAudioReference {
    requireResource(isRecord(value) && Object.hasOwn(value, 'source'), englishErrorText('logic.soundSource.chooseAgain') + ': ' + path);
    object(value, ['id', 'revision', 'source'], path);
    requireResource(value.source === 'mine' || value.source === 'community', englishErrorText('logic.soundSource.chooseAgain') + ': ' + path);
    try {
        validateResourceReference({id: value.id, revision: value.revision});
    } catch (error) {
        throw Error(errorMessage(error) + ': ' + path);
    }
}

/** Picker provenance is author data; all runtime identities use this projection. */
export function runtimeAudioReference(value: unknown, path = 'author audio reference'): ResourceReference {
    validateAuthorAudioReference(value, path);
    return {id: value.id, revision: value.revision};
}

function finiteVector(value: unknown, size: number, label: string): asserts value is number[] {
    requireResource(Array.isArray(value) && value.length === size && value.every(entry => isFiniteNumber(entry)), 'Invalid resource ' + label);
}

export function validateResourceTransform(value: unknown): asserts value is ResourceTransform {
    object(value, ['position', 'rotation', 'scale'], 'transform');
    const position = value.position,
        rotation = value.rotation,
        scale = value.scale;
    finiteVector(position, 3, 'position');
    finiteVector(rotation, 4, 'rotation');
    finiteVector(scale, 3, 'scale');
    requireResource(Math.abs(Math.hypot(...rotation) - 1) <= 0.0001, 'Resource quaternion is not normalized');
}

function validateResourceBytes(value: unknown): asserts value is ResourceBytes {
    object(value, ['location', 'sha256', 'bytes', 'range'], 'bytes');
    const location = value.location,
        sha256 = value.sha256,
        bytes = value.bytes;
    object(location, ['kind', 'path'], 'bytes.location');
    const kind = location.kind,
        path = location.path;
    requireResource((kind === 'site' || kind === 'repository-evidence') && resourcePath(path), 'Unsafe resource location');
    if (kind === 'site')
        requireResource(
            Object.values(assetPaths).some(root => path.startsWith(root + '/')) || path.startsWith('api/'),
            'Unsupported site resource location: ' + String(path)
        );
    requireResource(
        typeof sha256 === 'string' && resourceHash.test(sha256) && typeof bytes === 'number' && Number.isSafeInteger(bytes) && bytes > 0,
        'Invalid resource byte identity'
    );
    if (value.range !== undefined) {
        const range = value.range;
        object(range, ['offset', 'fileSha256'], 'bytes.range');
        const offset = range.offset,
            fileSha256 = range.fileSha256;
        requireResource(
            typeof offset === 'number' &&
                Number.isSafeInteger(offset) &&
                offset >= 0 &&
                Number.isSafeInteger(offset + bytes) &&
                typeof fileSha256 === 'string' &&
                resourceHash.test(fileSha256),
            'Invalid resource byte interval'
        );
    }
}

interface ResourceParentNode {
    id: string;
    parent: string | null;
}

function validateResourceParents(value: unknown, budget: number): asserts value is ResourceParentNode[] {
    requireResource(Array.isArray(value) && value.length > 0 && value.length <= budget, 'Resource node budget exceeded or empty');
    const nodes: ResourceParentNode[] = [];
    const byId = new Map<string, ResourceParentNode>();
    for (const candidate of value) {
        object(candidate, ['id', 'parent', 'name', 'source', 'gameObject', 'local', 'active', 'components', 'scripts', 'resource'], 'resource parent node');
        const id = candidate.id,
            rawParent = candidate.parent;
        requireResource(text(id) && id.length <= 512 && !byId.has(id), 'Duplicate or invalid resource node: ' + String(id));
        let parent: string | null;
        if (rawParent === null) parent = null;
        else if (text(rawParent)) parent = rawParent;
        else throw Error(englishErrorText('authoringMessages.englisherrors.resourceData'));
        const node = {id, parent};
        nodes.push(node);
        byId.set(id, node);
    }
    const finished = new Set<string>();
    for (const node of nodes) {
        const visiting = new Set<string>();
        let cursor: ResourceParentNode | undefined = node;
        while (cursor && !finished.has(cursor.id)) {
            requireResource(!visiting.has(cursor.id), 'Resource hierarchy cycle: ' + cursor.id);
            visiting.add(cursor.id);
            if (cursor.parent === null) break;
            requireResource(byId.has(cursor.parent), 'Missing resource parent: ' + cursor.parent);
            cursor = byId.get(cursor.parent);
        }
        for (const id of visiting) finished.add(id);
    }
}

const sourceFields = ['kind', 'packagePin', 'archiveSha256', 'bundle', 'assetPath', 'object', 'sourceId', 'contentSha256', 'evidence', 'documentId'] as const;

export function validateResourceDefinition(value: unknown): asserts value is ResourceDefinition {
    assertForgeJson(value, 'resource');
    object(
        value,
        ['schemaVersion', 'reference', 'source', 'domains', 'dependencies', 'preview', 'hierarchy', 'previewParts', 'blockers', 'composition'],
        'resource'
    );
    requireResource(value.schemaVersion === formatVersion, 'Unsupported resource schema');

    const reference = value.reference;
    validateResourceReference(reference);

    const source = value.source;
    object(source, sourceFields, 'source');
    if (source.kind === 'package') {
        object(source, ['kind', 'packagePin', 'archiveSha256', 'bundle', 'assetPath', 'object'], 'source');
        const packagePin = source.packagePin,
            archiveSha256 = source.archiveSha256,
            bundle = source.bundle,
            assetPath = source.assetPath;
        requireResource(
            typeof packagePin === 'string' &&
                resourcePackagePin.test(packagePin) &&
                typeof archiveSha256 === 'string' &&
                resourceHash.test(archiveSha256) &&
                resourcePath(bundle) &&
                resourcePath(assetPath),
            'Invalid resource source'
        );
        requireResource(reference.revision === archiveSha256, 'Imported resource revision must lock its original archive');
        validateSourceObject(source.object);
    } else if (source.kind === 'native') {
        object(source, ['kind', 'sourceId', 'contentSha256', 'evidence', 'assetPath', 'object'], 'source');
        const sourceId = source.sourceId,
            contentSha256 = source.contentSha256,
            evidence = source.evidence,
            assetPath = source.assetPath;
        object(evidence, ['path', 'sha256'], 'source.evidence');
        requireResource(
            text(sourceId) &&
                resourcePath(assetPath) &&
                resourcePath(evidence.path) &&
                typeof evidence.sha256 === 'string' &&
                resourceHash.test(evidence.sha256),
            'Invalid native source evidence'
        );
        requireResource(
            typeof contentSha256 === 'string' && resourceHash.test(contentSha256) && reference.revision === contentSha256,
            'Native resource revision must lock source content'
        );
        if (source.object !== null) validateSourceObject(source.object);
    } else if (source.kind === 'author') {
        object(source, ['kind', 'documentId', 'contentSha256'], 'source');
        const documentId = source.documentId,
            contentSha256 = source.contentSha256;
        requireResource(
            text(documentId) &&
                documentId === reference.id &&
                typeof contentSha256 === 'string' &&
                resourceHash.test(contentSha256) &&
                reference.revision === contentSha256,
            'Authored resource revision must lock its document'
        );
        requireResource(!/^forge\.(?:native[.:]|imported:)/.test(reference.id), 'Authored resource cannot use a reserved source identity');
    } else throw Error(englishErrorText('authoringMessages.englisherrors.resourceData'));

    const domains = value.domains;
    strings(domains, 'domains');
    requireResource(domains.length > 0 && domains.every(domain => resourceDomains.some(candidate => candidate === domain)), 'Unknown resource domain');

    const dependencies = value.dependencies;
    strings(dependencies, 'dependencies');
    requireResource(
        dependencies.every(pin => resourcePackagePin.test(pin)),
        'Resource dependency is not exactly pinned'
    );

    const blockers = value.blockers;
    object(blockers, ['preview', 'runtime'], 'blockers');
    const previewBlockers = blockers.preview,
        runtimeBlockers = blockers.runtime;
    strings(previewBlockers, 'preview blockers');
    strings(runtimeBlockers, 'runtime blockers');
    if (source.kind !== 'package')
        requireResource(runtimeBlockers.includes('native-resource-binding-unverified'), 'Unbound resource must retain its runtime blocker');

    if (value.composition !== undefined) {
        const composition = value.composition;
        validateResourceComposition(composition);
        requireResource(
            domains.some(domain => domain === composition.domain),
            'Resource composition domain mismatch'
        );
    }

    if (value.preview !== null) validateResourceBytes(value.preview);

    if (value.hierarchy !== null) {
        const hierarchy = value.hierarchy;
        object(hierarchy, ['space', 'evidence', 'nodes'], 'hierarchy');
        requireResource(hierarchy.space === 'unity-left-handed-meters', 'Unsupported source coordinate space');
        const evidence = hierarchy.evidence;
        object(evidence, ['path', 'sha256'], 'hierarchy.evidence');
        requireResource(
            resourcePath(evidence.path) && typeof evidence.sha256 === 'string' && resourceHash.test(evidence.sha256),
            'Missing source hierarchy evidence'
        );
        const nodes = hierarchy.nodes;
        validateResourceParents(nodes, 16384);
        requireResource(nodes.filter(node => node.parent === null).length === 1, 'Imported prefab must have one source root');
        for (const node of nodes) {
            object(node, ['id', 'parent', 'name', 'source', 'gameObject', 'local', 'active', 'components', 'scripts'], 'hierarchy.node');
            requireResource(text(node.name) && typeof node.active === 'boolean', 'Invalid resource node metadata');
            validateSourceObject(node.source);
            validateSourceObject(node.gameObject);
            validateResourceTransform(node.local);
            strings(node.components, 'components');
            strings(node.scripts, 'scripts');
        }
    }

    if (value.previewParts !== null) {
        const previewParts = value.previewParts;
        object(previewParts, ['space', 'coverage', 'parts'], 'previewParts');
        const parts = previewParts.parts;
        requireResource(
            previewParts.space === 'gltf-right-handed-meters' &&
                (previewParts.coverage === 'partial' || previewParts.coverage === 'complete') &&
                Array.isArray(parts) &&
                parts.length <= 32768,
            'Invalid preview part projection'
        );
        const ids = new Set<string>();
        for (const part of parts) {
            object(part, ['id', 'topology', 'matrix', 'source', 'materialSlots', 'geometry', 'material'], 'preview part');
            const id = part.id;
            requireResource(text(id) && !ids.has(id), 'Duplicate preview part');
            ids.add(id);
            requireResource(part.topology === 'triangles', 'Unsupported preview primitive topology');
            const matrix = part.matrix;
            requireResource(
                Array.isArray(matrix) &&
                    matrix.length === 16 &&
                    matrix.every(entry => isFiniteNumber(entry)) &&
                    matrix[3] === 0 &&
                    matrix[7] === 0 &&
                    matrix[11] === 0 &&
                    matrix[15] === 1,
                'Invalid affine preview matrix'
            );
            validateSourceObject(part.source);
            const materialSlots = part.materialSlots;
            requireResource(Array.isArray(materialSlots), 'Missing source material slots');
            for (const slot of materialSlots) validateSourceObject(slot);

            const material = part.material;
            object(material, ['sha256', 'description'], 'material');
            requireResource(
                typeof material.sha256 === 'string' && resourceHash.test(material.sha256) && text(material.description),
                'Invalid material identity'
            );

            const geometry = part.geometry;
            requireResource(Array.isArray(geometry) && geometry.length > 0 && geometry.length <= 32, 'Invalid geometry attributes');
            const semantics = new Set<string>();
            for (const attribute of geometry) {
                object(attribute, ['semantic', 'componentType', 'shape', 'count', 'normalized', 'byteStride', 'data'], 'geometry attribute');
                const semantic = attribute.semantic;
                requireResource(
                    typeof semantic === 'string' &&
                        /^(?:POSITION|NORMAL|TANGENT|INDICES|(?:TEXCOORD|COLOR|JOINTS|WEIGHTS)_\d+)$/.test(semantic) &&
                        !semantics.has(semantic),
                    'Unsupported or duplicate geometry attribute'
                );
                semantics.add(semantic);

                const componentType = attribute.componentType,
                    shape = attribute.shape,
                    count = attribute.count;
                const normalized = attribute.normalized,
                    byteStride = attribute.byteStride;
                const componentBytes: Record<number, number> = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4};
                const widths: Record<string, number> = {SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4};
                const componentSize = typeof componentType === 'number' ? componentBytes[componentType] : undefined;
                const componentWidth = typeof shape === 'string' ? widths[shape] : undefined;
                requireResource(
                    componentSize !== undefined &&
                        componentWidth !== undefined &&
                        typeof count === 'number' &&
                        Number.isSafeInteger(count) &&
                        count > 0 &&
                        typeof normalized === 'boolean',
                    'Invalid geometry accessor'
                );
                const element = componentSize * componentWidth;
                requireResource(
                    typeof byteStride === 'number' && Number.isSafeInteger(byteStride) && byteStride >= element && byteStride % componentSize === 0,
                    'Invalid geometry stride'
                );
                const data = attribute.data;
                validateResourceBytes(data);
                requireResource(data.bytes === (count - 1) * byteStride + element, 'Geometry interval does not match accessor');
            }
        }
    }
}

const validatedCompositions = new WeakSet<object>();
export function validateResourceComposition(value: unknown): asserts value is ResourceComposition {
    if (isImmutableForgeJson(value) && validatedCompositions.has(value as object)) return;
    assertForgeJson(value, 'composition');
    object(value, ['schemaVersion', 'domain', 'space', 'instances'], 'composition');
    const domain = value.domain,
        instances = value.instances;
    requireResource(
        value.schemaVersion === formatVersion &&
            typeof domain === 'string' &&
            resourceDomains.some(candidate => candidate === domain) &&
            value.space === 'unity-left-handed-meters',
        'Unsupported resource composition'
    );
    requireResource(Array.isArray(instances), 'Missing resource instances');
    if (instances.length) validateResourceParents(instances, domain === 'room' || domain === 'map' ? 65536 : 8192);
    for (const instance of instances) {
        object(instance, ['id', 'parent', 'resource', 'local'], 'instance');
        validateResourceReference(instance.resource);
        validateResourceTransform(instance.local);
    }
    if (isImmutableForgeJson(value)) validatedCompositions.add(value);
}
