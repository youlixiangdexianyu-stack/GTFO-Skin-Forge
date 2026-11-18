import type {JsonValue} from './contracts.ts';
import {t} from '../shared/i18n/i18n.ts';
import {isRecord} from './lib/is-record.ts';

const jsonErrorKeys = {
    'Non-JSON value': 'unsupportedValue',
    'Cyclic metadata': 'cyclicContent',
    'Metadata nesting budget exceeded': 'contentTooDeep',
    'Non-JSON object': 'unsupportedContent',
    'Symbol key': 'unsupportedContent',
    'Accessor metadata': 'unsupportedContent',
    'Sparse array': 'missingListEntry',
    'Duplicate JSON key': 'unsupportedContent'
} as const;

// Defined in a leaf module the staged maintenance tools can carry; the callers still import it from this file.
export {isRecord};
/** The shared walk of an authored row list: not a list, a row that is no object and a field outside `fields` are reported here; `check` adds the row's own codes. */
export function rowListProblems(value: unknown, fields: readonly string[], check: (row: Record<string, unknown>, problems: string[]) => void): string[] {
    if (!Array.isArray(value)) return ['list'];
    const problems: string[] = [];
    for (const row of value) {
        if (!isRecord(row)) problems.push('object');
        else {
            if (Object.keys(row).some(key => !fields.includes(key))) problems.push('unknown_field');
            check(row, problems);
        }
    }
    return [...new Set(problems)];
}
export const errorMessage = (error: unknown): string => (error instanceof Error ? error.message : String(error));
export const isFiniteNumber = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value);
/** Two JSON values are the same data: key order does not matter and an undefined field counts as absent, as in JSON text. Stops at the first difference. */
export function jsonEqual(left: unknown, right: unknown): boolean {
    if (left === right) return true;
    if (left === null || right === null || typeof left !== 'object' || typeof right !== 'object') return false;
    if (Array.isArray(left) || Array.isArray(right)) {
        if (!Array.isArray(left) || !Array.isArray(right) || left.length !== right.length) return false;
        return left.every((item, index) => jsonEqual(item, right[index]));
    }
    const a = left as Record<string, unknown>,
        b = right as Record<string, unknown>;
    const keys = Object.keys(a).filter(key => a[key] !== undefined);
    return keys.length === Object.keys(b).filter(key => b[key] !== undefined).length && keys.every(key => jsonEqual(a[key], b[key]));
}
/** The text of a JSON file people may open: two-space indent and a final newline. */
export const jsonFileText = (value: unknown): string => JSON.stringify(value, null, 2) + '\n';

// Only snapshots validated here and frozen at every level enter this table. Object.isFrozen
// alone proves neither JSON validity nor that descendants cannot change, except for a leaf: a frozen
// plain object or dense array whose values are all JSON primitives proves both by itself. Leaves are
// most of every frozen tree, so they are recognized by that shape and never entered here.
const immutableDepths = new WeakMap<object, number>();
function frozenJsonLeaf(value: object): boolean {
    if (!Object.isFrozen(value)) return false;
    const array = Array.isArray(value),
        prototype = Object.getPrototypeOf(value);
    if ((!array && prototype !== Object.prototype && prototype !== null) || Object.getOwnPropertySymbols(value).length) return false;
    const keys = Object.keys(value);
    if (array && keys.length !== value.length) return false;
    for (const key of keys) {
        const descriptor = Object.getOwnPropertyDescriptor(value, key)!;
        if (!('value' in descriptor)) return false;
        const item: unknown = descriptor.value;
        if (!(item === null || typeof item === 'string' || typeof item === 'boolean' || isFiniteNumber(item))) return false;
    }
    return true;
}
const immutableDepth = (value: object): number | undefined => immutableDepths.get(value) ?? (frozenJsonLeaf(value) ? 1 : undefined);
const registerImmutable = (value: object, depth: number) => {
    if (depth > 1) immutableDepths.set(value, depth);
};
export function isImmutableForgeJson(value: unknown): boolean {
    return value !== null && typeof value === 'object' && immutableDepth(value) !== undefined;
}

/** Isolate editable snapshot branches while retaining JSON subtrees this module
 * has already validated and deeply frozen. A caller's shallow Object.freeze is
 * never enough. Optional undefined fields retain structuredClone's semantics. */
export function cloneForgeSnapshot<T>(value: T): T {
    return copyForgeJson(value, true);
}
/** A full editable copy, like structuredClone, that also reads an immer edit draft (a Proxy structuredClone rejects). */
export function deepCopyForgeJson<T>(value: T): T {
    return copyForgeJson(value, false);
}
function copyForgeJson<T>(value: T, shareImmutable: boolean): T {
    if (shareImmutable && isImmutableForgeJson(value)) return value;
    const copies = new WeakMap<object, unknown>();
    const copy = (input: unknown): unknown => {
        if (shareImmutable && isImmutableForgeJson(input)) return input;
        if (input === null || typeof input !== 'object') return typeof input === 'function' || typeof input === 'symbol' ? structuredClone(input) : input;
        if (copies.has(input)) return copies.get(input);
        const prototype = Object.getPrototypeOf(input);
        if (!Array.isArray(input) && prototype !== Object.prototype && prototype !== null) {
            const result = structuredClone(input);
            copies.set(input, result);
            return result;
        }
        const result: Record<string, unknown> | unknown[] = Array.isArray(input) ? new Array(input.length) : {};
        copies.set(input, result);
        for (const key of Object.keys(input)) {
            const value = copy((input as Record<string, unknown>)[key]);
            if (key === '__proto__') Object.defineProperty(result, key, {value, writable: true, enumerable: true, configurable: true});
            else (result as Record<string, unknown>)[key] = value;
        }
        return result;
    };
    return copy(value) as T;
}

/** Keep validated snapshots read-only, including their nested arrays and metadata. */
export function freezeForgeJson<T>(value: T): T {
    if (isImmutableForgeJson(value)) return value;
    checkForgeJson(value, 'immutable snapshot', new Set<object>(), true);
    return value;
}

/** Validate and freeze an exclusively owned snapshot in short tasks. The caller must
 * not publish or mutate the value until this promise resolves. */
export async function freezeForgeJsonInSteps<T>(value: T, signal?: AbortSignal): Promise<T> {
    signal?.throwIfAborted();
    if (isImmutableForgeJson(value)) return value;
    await runInSteps(checkForgeJsonSteps(value, 'immutable snapshot', new Set<object>(), true), signal);
    return value;
}

/** parseForgeJson's freeze, in short tasks, of an exclusively owned value that only JSON.parse built, or a structured
 *  clone of a validated JSON value (a worker's answer). Duplicate keys are not looked for: the published documents read
 *  this way were checked once with parseForgeJson when they were published (Tools/Forge/build-editor-catalogs.ts). */
export async function freezeParsedJsonInSteps<T>(value: T, path: string, signal?: AbortSignal): Promise<T> {
    signal?.throwIfAborted();
    await runInSteps(freezeParsedSteps(value, path), signal);
    return value;
}

async function runInSteps(steps: Generator<void>, signal?: AbortSignal): Promise<void> {
    let started = performance.now();
    for (const _ of steps) {
        signal?.throwIfAborted();
        if (performance.now() - started < 4) continue;
        await new Promise<void>(resolve => {
            const channel = new MessageChannel();
            channel.port1.onmessage = () => {
                channel.port1.close();
                channel.port2.close();
                resolve();
            };
            channel.port2.postMessage(null);
        });
        signal?.throwIfAborted();
        started = performance.now();
    }
    signal?.throwIfAborted();
}

/** Only JSON.parse creates this tree: one walk checks budgets and finite numbers,
 * freezes every object and records the same subtree depths as freezeForgeJson.
 * A text scan then rejects the duplicate keys JSON.parse silently collapses. */
export function parseForgeJson(text: string, path = 'resource'): JsonValue {
    const value = JSON.parse(text) as JsonValue;
    for (const _ of freezeParsedSteps(value, path)) {
        /* synchronous callers keep one task */
    }
    rejectDuplicateKeys(text, path);
    return value;
}

/** The walk of a tree only JSON.parse (or a structured clone of a validated value) built: plain objects and dense arrays
 *  with no accessors or symbol keys, so only the nesting budget and the leaves are checked. Each object is frozen once its
 *  children are, with the subtree depth freezeForgeJson records. Yields every 1024 objects. */
function* freezeParsedSteps(value: unknown, path: string): Generator<void> {
    interface Frame {
        item: object;
        keys: string[] | null;
        length: number;
        index: number;
        depth: number;
    }
    const stack: Frame[] = [];
    function fail(message: keyof typeof jsonErrorKeys): never {
        const segments = stack.map(frame => (frame.keys ? frame.keys[frame.index - 1]! : String(frame.index - 1)));
        const cause = new Error(`${message}: ${path}${segments.length ? '.' + segments.join('.') : ''}`);
        throw new Error(t('authoringMessages.englisherrors.' + jsonErrorKeys[message]), {cause});
    }
    function visit(item: unknown): void {
        if (item === null || typeof item === 'string' || typeof item === 'boolean') return;
        if (typeof item === 'number') {
            if (!Number.isFinite(item)) fail('Non-JSON value');
            return;
        }
        if (typeof item !== 'object') fail('Non-JSON value');
        if (stack.length >= 64) fail('Metadata nesting budget exceeded');
        const keys = Array.isArray(item) ? null : Object.keys(item);
        stack.push({item, keys, length: keys ? keys.length : (item as unknown[]).length, index: 0, depth: 1});
    }
    visit(value);
    let frozen = 0;
    while (stack.length) {
        const frame = stack[stack.length - 1]!;
        if (frame.index < frame.length) {
            const index = frame.index++;
            visit(frame.keys ? (frame.item as Record<string, unknown>)[frame.keys[index]!] : (frame.item as unknown[])[index]);
            continue;
        }
        stack.pop();
        Object.freeze(frame.item);
        registerImmutable(frame.item, frame.depth);
        const parent = stack[stack.length - 1];
        if (parent && parent.depth <= frame.depth) parent.depth = frame.depth + 1;
        if (++frozen % 1024 === 0) yield;
    }
}

function rejectDuplicateKeys(text: string, path: string): void {
    scanJsonKeys(text, problem => {
        const message = problem === 'depth' ? 'Metadata nesting budget exceeded' : 'Duplicate JSON key';
        throw new Error(t('authoringMessages.englisherrors.' + jsonErrorKeys[message]), {cause: new Error(`${message}: ${path}`)});
    });
}

/** Text `JSON.parse` already accepted: refuse more than 64 nested containers or a key repeated in one object. */
export function scanJsonKeys(text: string, fail: (problem: 'depth' | 'duplicate') => never): void {
    const containers: (Set<string> | null)[] = [];
    // JSON.parse accepted the text, so a linear scan finds string ends without a
    // backtracking pattern (multi-megabyte strings overflow the regexp stack).
    for (let at = 0; at < text.length; at++) {
        const code = text.charCodeAt(at);
        if (code === 0x7b || code === 0x5b) {
            if (containers.length >= 64) fail('depth');
            containers.push(code === 0x7b ? new Set() : null);
        } else if (code === 0x7d || code === 0x5d) containers.pop();
        else if (code === 0x22) {
            const start = at;
            for (at++; text.charCodeAt(at) !== 0x22; at += text.charCodeAt(at) === 0x5c ? 2 : 1);
            let next = at + 1;
            while (next < text.length) {
                const c = text.charCodeAt(next);
                if (c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d) next++;
                else break;
            }
            if (text.charCodeAt(next) === 0x3a) {
                const keys = containers.at(-1),
                    key = JSON.parse(text.slice(start, at + 1)) as string;
                if (keys?.has(key)) fail('duplicate');
                keys?.add(key);
            }
        }
    }
}

/** Reject non-JSON metadata instead of silently dropping functions or NaN. */
export function assertForgeJson(value: unknown, path: string, ancestors?: Set<object>): asserts value is JsonValue {
    if (value === null || typeof value === 'string' || typeof value === 'boolean' || isFiniteNumber(value)) return;
    const depth = typeof value === 'object' && value !== null ? immutableDepth(value) : undefined;
    if (depth !== undefined && (!ancestors || (!ancestors.has(value as object) && ancestors.size + depth <= 64))) return;
    checkForgeJson(value, path, ancestors ?? new Set<object>(), false);
}

function checkForgeJson(value: unknown, path: string, ancestors: Set<object>, freeze: boolean): void {
    for (const _ of checkForgeJsonSteps(value, path, ancestors, freeze)) {
        /* synchronous callers keep one task */
    }
}

function* checkForgeJsonSteps(value: unknown, path: string, ancestors: Set<object>, freeze: boolean): Generator<void> {
    // Successful validation needs no diagnostic strings. Keep the path segments on one stack
    // and materialize the same full path only on rejection, even for large external documents.
    const segments: string[] = [];
    // Shared subtrees in a single read need one walk. This table ends with this invocation:
    // mutable caller values are checked again on the next read, including rejected edits.
    const checkedDepths = new WeakMap<object, number>();
    // Validate the complete input before changing it. The commit only freezes objects already
    // visited by this walk; no second traversal of their keys or values is needed.
    const toFreeze: object[] = [];
    function fail(message: keyof typeof jsonErrorKeys): never {
        const cause = new Error(`${message}: ${path}${segments.length ? '.' + segments.join('.') : ''}`);
        throw new Error(t('authoringMessages.englisherrors.' + jsonErrorKeys[message]), {cause});
    }
    interface Frame {
        item: object;
        keys: string[];
        index: number;
        depth: number;
        array: boolean;
        immutable: boolean;
    }
    function enter(item: unknown): number | Frame {
        if (item === null || typeof item === 'string' || typeof item === 'boolean') return 0;
        if (isFiniteNumber(item)) return 0;
        if (typeof item !== 'object' || item === null) fail('Non-JSON value');
        if (ancestors.has(item)) fail('Cyclic metadata');
        const knownDepth = immutableDepth(item);
        if (knownDepth !== undefined) {
            if (ancestors.size + knownDepth > 64) fail('Metadata nesting budget exceeded');
            return knownDepth;
        }
        const checkedDepth = checkedDepths.get(item);
        // A later occurrence may be deeper. Rewalk it to preserve the exact rejection path.
        if (checkedDepth !== undefined && ancestors.size + checkedDepth <= 64) return checkedDepth;
        const array = Array.isArray(item),
            prototype = Object.getPrototypeOf(item);
        if (!array && prototype !== Object.prototype && prototype !== null) fail('Non-JSON object');
        if (Object.getOwnPropertySymbols(item).length !== 0) fail('Symbol key');
        if (ancestors.size >= 64) fail('Metadata nesting budget exceeded');
        ancestors.add(item);
        return {item, keys: Object.keys(item), index: 0, depth: 1, array, immutable: !freeze && Object.isFrozen(item)};
    }
    const root = enter(value),
        stack: Frame[] = typeof root === 'number' ? [] : [root];
    let visited = 0;
    while (stack.length) {
        if (++visited % 1024 === 0) yield;
        const frame = stack[stack.length - 1]!,
            item = frame.item;
        if (frame.index < frame.keys.length) {
            const key = frame.keys[frame.index++]!;
            const descriptor = Object.getOwnPropertyDescriptor(item, key);
            if (!descriptor || !('value' in descriptor)) {
                segments.push(key);
                fail('Accessor metadata');
            }
            const leaf = descriptor.value;
            // A finite number, string, boolean or null adds no depth and nothing to freeze or walk.
            if (leaf === null || typeof leaf === 'string' || typeof leaf === 'boolean' || isFiniteNumber(leaf)) continue;
            segments.push(key);
            const child = enter(leaf);
            if (typeof child === 'number') {
                frame.depth = Math.max(frame.depth, 1 + child);
                if (frame.immutable && descriptor.value !== null && typeof descriptor.value === 'object' && !isImmutableForgeJson(descriptor.value))
                    frame.immutable = false;
                segments.pop();
            } else stack.push(child);
            continue;
        }
        if (frame.array)
            for (let i = 0; i < (item as unknown[]).length; i++) {
                if (!(i in item)) fail('Sparse array');
                if (++visited % 1024 === 0) yield;
            }
        ancestors.delete(item);
        checkedDepths.set(item, frame.depth);
        if (freeze) toFreeze.push(item);
        else if (frame.immutable) registerImmutable(item, frame.depth);
        stack.pop();
        const parent = stack[stack.length - 1];
        if (parent) {
            parent.depth = Math.max(parent.depth, 1 + frame.depth);
            if (parent.immutable && !isImmutableForgeJson(item)) parent.immutable = false;
            segments.pop();
        }
    }
    yield; // validation is complete before the first object is changed
    for (const item of toFreeze) {
        Object.freeze(item);
        registerImmutable(item, checkedDepths.get(item)!);
        if (++visited % 1024 === 0) yield;
    }
}

/** Object keys use ordinal UTF-16 order, including numeric-looking keys; array order stays authored. */
export function canonicalResourceJson(value: unknown): string {
    assertForgeJson(value, 'resource content');
    const write = (current: JsonValue): string => {
        if (current === null) return 'null';
        switch (typeof current) {
            case 'string':
                return JSON.stringify(current);
            case 'number':
                return String(current);
            case 'boolean':
                return current ? 'true' : 'false';
        }
        if (Array.isArray(current)) {
            let text = '[';
            for (let index = 0; index < current.length; index++) text += (index ? ',' : '') + write(current[index]!);
            return text + ']';
        }
        const keys = Object.keys(current);
        if (keys.length > 1) keys.sort();
        let text = '{';
        for (let index = 0; index < keys.length; index++) {
            const key = keys[index]!;
            text += (index ? ',' : '') + JSON.stringify(key) + ':' + write(current[key]!);
        }
        return text + '}';
    };
    return write(value);
}

/** Computed projections omit unset object fields; an unset root remains distinct from null.
 *  One walk writes exactly what canonicalResourceJson(JSON.parse(JSON.stringify(value))) writes: toJSON, skipped
 *  undefined/function/symbol fields, null for those in arrays and for non-finite numbers. The inputs it does not cover
 *  (BigInt, boxed primitives, a cycle, a root JSON.stringify cannot write, more than 64 nested containers) take that
 *  three-step path, so their result or rejection is the same. */
export function canonicalProjectionJson(value: unknown): string {
    if (value === undefined) return 'undefined';
    try {
        const text = writeProjection(value, '', 0);
        if (text !== undefined) return text;
    } catch (error) {
        if (error !== projectionFallback) throw error;
    }
    return canonicalResourceJson(JSON.parse(JSON.stringify(value)));
}
const projectionFallback = new Error('canonical projection fallback');
function writeProjection(input: unknown, key: string, containers: number): string | undefined {
    let value = input;
    if (value !== null && typeof value === 'object') {
        const toJSON: unknown = (value as {toJSON?: unknown}).toJSON;
        if (typeof toJSON === 'function') value = toJSON.call(value, key);
    }
    if (value === null) return 'null';
    switch (typeof value) {
        case 'string':
            return JSON.stringify(value);
        case 'number':
            return Number.isFinite(value) ? String(value) : 'null';
        case 'boolean':
            return value ? 'true' : 'false';
        case 'object':
            break;
        case 'bigint':
            throw projectionFallback;
        default:
            return undefined;
    }
    const prototype = Object.getPrototypeOf(value);
    if (
        containers >= 64 ||
        (prototype !== Object.prototype &&
            prototype !== Array.prototype &&
            prototype !== null &&
            (value instanceof Number || value instanceof String || value instanceof Boolean || value instanceof BigInt))
    )
        throw projectionFallback;
    if (Array.isArray(value)) {
        let text = '[';
        for (let index = 0; index < value.length; index++)
            text += (index ? ',' : '') + (writeProjection(value[index], String(index), containers + 1) ?? 'null');
        return text + ']';
    }
    const keys = Object.keys(value);
    if (keys.length > 1) keys.sort();
    let text = '{';
    for (const name of keys) {
        const item = writeProjection((value as Record<string, unknown>)[name], name, containers + 1);
        if (item !== undefined) text += (text.length > 1 ? ',' : '') + JSON.stringify(name) + ':' + item;
    }
    return text + '}';
}
