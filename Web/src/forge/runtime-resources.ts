import {graphResourceKinds, packageLimits, type GraphResourceKind} from './generated/wire.ts';
import type {JsonValue} from './contracts.ts';
import type {ResourceReference} from './resource-contracts.ts';
import {assertForgeJson, isRecord, jsonFileText} from './json.ts';
import {hashResourceContent, requireResource, validateResourceReference} from './resource-schema.ts';
import {formatVersion} from './forge-version.ts';

/** A packaged runtime resource declares one kind of the online (plan) table the mods mirror. */
export interface ForgeEngineResource<T extends JsonValue = JsonValue> {
    format: 'gtfo-forge-engine-resource';
    schemaVersion: typeof formatVersion;
    kind: GraphResourceKind;
    reference: ResourceReference;
    payload: T;
}
export const maxForgeEngineResourceBytes = packageLimits.maximumPublishedBytes;
const resourceId = /^[^\s/\\\u0000-\u001f]{1,512}$/;
function fields(value: unknown, names: readonly string[], path: string): asserts value is Record<string, unknown> {
    requireResource(isRecord(value), `Invalid runtime resource: ${path}`);
    requireResource(
        Object.keys(value).every(key => names.includes(key)),
        `Unknown runtime resource field: ${path}`
    );
}
const isGraphResourceKind = (value: unknown): value is GraphResourceKind =>
    typeof value === 'string' && (graphResourceKinds as readonly string[]).includes(value);
const semantic = (id: string, kind: GraphResourceKind, payload: JsonValue) => ({
    format: 'gtfo-forge-engine-resource' as const,
    schemaVersion: formatVersion,
    id,
    kind,
    payload
});
function checkBudget(value: unknown): void {
    const bytes = new TextEncoder().encode(JSON.stringify(value)).byteLength;
    requireResource(bytes <= maxForgeEngineResourceBytes, 'Runtime resource exceeds the package byte budget');
}
export async function createForgeEngineResource<T extends JsonValue>(id: string, kind: GraphResourceKind, payload: T): Promise<ForgeEngineResource<T>> {
    requireResource(resourceId.test(id), 'Invalid runtime resource id');
    requireResource((graphResourceKinds as readonly string[]).includes(kind), 'Unknown runtime resource kind');
    assertForgeJson(payload, 'runtime resource payload');
    const snapshot = structuredClone(payload);
    const revision = await hashResourceContent(semantic(id, kind, snapshot));
    const resource: ForgeEngineResource<T> = {
        format: 'gtfo-forge-engine-resource',
        schemaVersion: formatVersion,
        kind,
        reference: {id, revision},
        payload: snapshot
    };
    checkBudget(resource);
    return resource;
}
export function readForgeEngineResource(input: unknown): ForgeEngineResource {
    assertForgeJson(input, 'runtime resource');
    checkBudget(input);
    fields(input, ['format', 'schemaVersion', 'kind', 'reference', 'payload'], 'root');
    const value = input;
    requireResource(value.format === 'gtfo-forge-engine-resource' && value.schemaVersion === formatVersion, 'Unsupported runtime resource format');
    requireResource(isGraphResourceKind(value.kind), 'Unknown runtime resource kind');
    validateResourceReference(value.reference);
    assertForgeJson(value.payload, 'runtime resource payload');
    return structuredClone({
        format: 'gtfo-forge-engine-resource',
        schemaVersion: formatVersion,
        kind: value.kind,
        reference: value.reference,
        payload: value.payload
    });
}
export async function verifyForgeEngineResource(input: unknown): Promise<ForgeEngineResource> {
    const resource = readForgeEngineResource(input);
    const revision = await hashResourceContent(semantic(resource.reference.id, resource.kind, resource.payload));
    requireResource(revision === resource.reference.revision, 'Runtime resource revision mismatch');
    return resource;
}
export function runtimeResourceAddress(reference: ResourceReference): string {
    validateResourceReference(reference);
    return `${reference.id}@${reference.revision}`;
}
export function parseRuntimeResourceAddress(value: string): ResourceReference {
    requireResource(typeof value === 'string' && value.length <= 600, 'Invalid runtime resource address');
    const split = value.lastIndexOf('@');
    requireResource(split > 0, 'Invalid runtime resource address');
    const reference = {id: value.slice(0, split), revision: value.slice(split + 1)};
    validateResourceReference(reference);
    return reference;
}
export function runtimeResourceFileName(reference: ResourceReference): string {
    validateResourceReference(reference);
    return `forge/resources/${reference.revision}.resource.forge`;
}
export function writeForgeEngineResource(resource: ForgeEngineResource): string {
    const checked = readForgeEngineResource(resource);
    return jsonFileText(checked);
}
