import type {ResourceDomain} from './generated/wire.ts';
import {formatVersion} from './forge-version.ts';

/** A revision locks source content, not a mutable preview URL or a display name. */
export interface ResourceReference {
    id: string;
    revision: string;
}
/** Picker provenance belongs to authoring; runtime references remain {id, revision}. */
export type AudioSource = 'mine' | 'community';
export interface AuthorAudioReference extends ResourceReference {
    source: AudioSource;
}
export interface SourceObjectIdentity {
    file: string;
    pathId: string;
}
interface PackageResourceSource {
    kind: 'package';
    packagePin: string;
    archiveSha256: string;
    bundle: string;
    assetPath: string;
    object: SourceObjectIdentity;
}
interface NativeResourceSource {
    kind: 'native';
    /** The processed evidence record is locked separately from its preview bytes. */
    sourceId: string;
    contentSha256: string;
    evidence: {path: string; sha256: string};
    assetPath: string;
    object: SourceObjectIdentity | null;
}
interface AuthorResourceSource {
    kind: 'author';
    documentId: string;
    contentSha256: string;
}
type ResourceSource = PackageResourceSource | NativeResourceSource | AuthorResourceSource;
interface ResourceLocation {
    kind: 'site' | 'repository-evidence';
    path: string;
}
export interface ResourceBytes {
    location: ResourceLocation;
    sha256: string;
    bytes: number;
    /** A verified byte interval in an evidence file; omitted for a whole file. */
    range?: {offset: number; fileSha256: string};
}
export interface ResourceTransform {
    position: readonly [number, number, number];
    rotation: readonly [number, number, number, number];
    scale: readonly [number, number, number];
}
interface ResourceHierarchyNode {
    id: string;
    parent: string | null;
    name: string;
    source: SourceObjectIdentity;
    gameObject: SourceObjectIdentity;
    local: ResourceTransform;
    active: boolean;
    components: readonly string[];
    scripts: readonly string[];
}
export interface ResourceHierarchy {
    space: 'unity-left-handed-meters';
    evidence: {path: string; sha256: string};
    nodes: readonly ResourceHierarchyNode[];
}
interface ResourceGeometryAttribute {
    semantic: string;
    componentType: number;
    shape: string;
    count: number;
    normalized: boolean;
    byteStride: number;
    data: ResourceBytes;
}
export interface ResourcePreviewPart {
    id: string;
    topology: 'triangles';
    /** Explicitly separate from the native hierarchy: extracted root-relative preview matrix. */
    matrix: readonly number[];
    source: SourceObjectIdentity;
    /** Exact source identity, never a material/geometry deduplication key. */
    materialSlots: readonly SourceObjectIdentity[];
    geometry: readonly ResourceGeometryAttribute[];
    material: {sha256: string; description: string};
}
export interface ResourceDefinition {
    schemaVersion: typeof formatVersion;
    reference: ResourceReference;
    source: ResourceSource;
    domains: readonly ResourceDomain[];
    /** Direct, exact original-package requirements; shared bytes do not remove these. */
    dependencies: readonly string[];
    preview: ResourceBytes | null;
    hierarchy: ResourceHierarchy | null;
    /** A preview projection may cover a subset. It never replaces the source hierarchy. */
    previewParts: {space: 'gltf-right-handed-meters'; coverage: 'partial' | 'complete'; parts: readonly ResourcePreviewPart[]} | null;
    blockers: {preview: readonly string[]; runtime: readonly string[]};
    /** Authored definitions use the same locked instance graph as every editor. */
    composition?: ResourceComposition;
}
interface ResourceInstance {
    id: string;
    parent: string | null;
    resource: ResourceReference;
    local: ResourceTransform;
}
/** Rooms remain definitions; a map/room group owns instances of those definitions. */
export interface ResourceComposition {
    schemaVersion: typeof formatVersion;
    domain: ResourceDomain;
    space: 'unity-left-handed-meters';
    instances: readonly ResourceInstance[];
}
