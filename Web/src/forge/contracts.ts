/** Forge Standard v0.2. Authoring contracts, not evidence of game support. */
import {graphEntityKindParents} from './generated/wire.ts';
import type {
    ExecutionTier,
    GraphCardinality,
    GraphEntityKind,
    GraphEnumSet,
    GraphHandleKind,
    GraphParameterType,
    GraphPortType,
    GraphReadSource,
    GraphResourceKind,
    HandleLifetime,
    RecipientTargetKind,
    ResourceDomain
} from './generated/wire.ts';
import {formatVersion} from './forge-version.ts';
type ProviderKind = 'native' | 'adapter' | 'extension';
export const capabilityKinds = ['trigger', 'selector', 'condition', 'modifier', 'control', 'action', 'variable', 'state', 'event', 'component'] as const;
export type CapabilityKind = (typeof capabilityKinds)[number];
/** The id namespace each kind's canonical ids live in. A separate declaration because the two are
 *  not the same field and neither is derived from the other: the `state` kind's rows are the
 *  read-only value rows, and their ids spell the tier the plan step an author places runs in
 *  (`forge.query.<domain>.<name>`), while the kind stays `state`. Every other kind keeps its own
 *  name, so this table is the whole spelling rule. */
export const capabilityNamespaces: Record<CapabilityKind, readonly string[]> = {
    trigger: ['trigger'],
    selector: ['selector'],
    condition: ['condition'],
    modifier: ['modifier'],
    control: ['control'],
    action: ['action'],
    variable: ['variable'],
    state: ['query'],
    event: ['event'],
    component: ['component']
};
/** Whether one canonical id belongs to a kind's namespace family. */
export function capabilityIdMatchesKind(kind: CapabilityKind, id: string): boolean {
    return capabilityNamespaces[kind].some(namespace => id.startsWith('forge.' + namespace + '.'));
}
/** The kinds a graph evaluates instead of executing — their tier is `pure` or `query`, never an
 *  executing one — so the per-tier role rule admits them only through `evaluate` or `observe`
 *  bindings. `state` answers named facts about the live world instead of a target set or one
 *  boolean, and is read on demand exactly the same way. Which tier a row declares is the separate
 *  axis its ports decide: a `query` carries a world port, a `pure` evaluation carries none. Both
 *  are read on demand and never execute. */
export const evaluatedCapabilityKinds = ['selector', 'condition', 'modifier', 'state'] as const;
type BindingStatus = 'planned' | 'implemented';
export type JsonValue = null | boolean | number | string | JsonValue[] | {[key: string]: JsonValue};

export interface ProviderDefinition {
    id: string;
    kind: ProviderKind;
    version: string;
    /** Exact Thunderstore package-version identifiers, never version ranges. */
    dependencies: readonly string[];
}

/** Why a capability the catalog declares stays authorable and unexportable in the current step.
 *  Authored contract data, never a status: a status is derived from real registrations
 *  (graph-schema.deriveCapabilityStatus), so a row carrying this is withheld for two independent
 *  reasons — nothing registers it, and the step decided not to. The generator refuses a reason on a
 *  row a module registers, so the two can never disagree. */
export interface CapabilityUnavailable {
    /** Stable identity of the reason; one key has one Chinese and one English sentence. */
    key: string;
    /** The player's sentence in both catalog languages, authored in Tools/Forge/capability-copy.json. */
    reasonZh: string;
    reasonEn: string;
    /** Capabilities that do the job instead; every id is a vocabulary row. */
    alternatives?: readonly string[];
}

export interface CapabilityDefinition {
    id: string;
    owner: string;
    kind: CapabilityKind;
    /** The row's own name in the language the registering provider writes. A catalog row carries
     *  both languages, so a reader is served from `labelEn` first and never sees one language's
     *  name in the other language's interface. */
    label: string;
    /** The English side of the row name, present on every definition the catalog builds. */
    labelEn?: string;
    /** Semantic revision; changing this is not a silent provider replacement. */
    version: string;
    /** Editing-time metadata only. Never a status claim; see authoredStatusFields. */
    parameters: {[key: string]: JsonValue};
    graph?: CapabilityGraphContract;
    /** Present only where the step withholds the row; absent on everything a module registers. */
    unavailable?: CapabilityUnavailable;
}

export interface BindingDefinition {
    id: string;
    capabilityId: string;
    providerId: string;
    /** An opaque adapter/runtime entry point. Never evaluate this in the browser. */
    handler: string;
    /** Adapters may report canonical triggers; Forge owns their scheduling.
     *  `evaluate` binds a pure (selector/condition/modifier) capability and is read in a
     *  read-only context on demand; it never executes or observes anything. */
    role: 'observe' | 'execute' | 'evaluate';
    status: BindingStatus;
    dependencies: readonly string[];
    /** Other bindings required by this implementation, not every installed adapter. */
    requires: readonly string[];
    /** Equipment mount ownership, independent of graph.subject and effect end_on. */
    equipmentAttachment?: {holder: string; equipment: string};
    /** The provider's declaration that a `wait_event` step may name this trigger binding. Only an `observe` binding of a trigger row. */
    waitable?: true;
}

export interface CapabilityUse {
    capabilityId: string;
    /** Explicit selection prevents silent switching between different runtimes. */
    bindingId: string;
}

export interface ForgeRegistrySeed {
    /** Trusted built-ins or server-reviewed definitions, never raw uploads. */
    providers: readonly ProviderDefinition[];
    capabilities: readonly CapabilityDefinition[];
    bindings: readonly BindingDefinition[];
}

/** One Registry holds contract-only and implemented nodes; status is DERIVED from
 *  real registrations, never authored. A hand-written claim must fail the build. */
const capabilityStatuses = ['contract-only', 'bound-planned', 'bound-implemented'] as const;
export type CapabilityStatus = (typeof capabilityStatuses)[number];
/** Field names a definition may never carry; the old research-catalog fields included. */
export const authoredStatusFields = ['runtimeStatus', 'runtimeBinding', 'status', 'implemented', 'gameReady', 'bindingStatus', 'supportLevel'] as const;

/** 一个种类能不能落进一组已接受的种类：同一个种类，或它的父类在那一组里（窄接宽）。相容判定只有这一处，
 *  `assignableGraphPort` 与宿主端口过滤都问它。 */
export const graphEntityKindAccepted = (kind: GraphEntityKind, accepted: readonly GraphEntityKind[]): boolean => {
    const parent = graphEntityKindParents[kind];
    return accepted.includes(kind) || (parent !== undefined && accepted.includes(parent));
};

/** The fixed frame every trigger delivers. Declared once, never redeclared per node,
 *  never re-expressed as ports or parameters; the runtime reads it at fixed offsets. */
export const graphEventEnvelopeFields = [
    'eventId',
    'eventType',
    'schemaVersion',
    'simulationTick',
    'sequence',
    'sourceRef',
    'instigatorRef',
    'targetRefs',
    'scopeRef',
    'worldEpoch',
    'lifeEpoch',
    'correlationId',
    'causationId',
    'payload'
] as const;
/** The single policy payload schema; its shape lives in target-contracts.ts. */
export const recipientPolicySchema = 'forge.policy.recipient';

export interface GraphPort {
    id: string;
    type: GraphPortType;
    /** This value port exists only for one explicit structural enum choice. Resolution removes
     *  inactive ports and this authoring condition before the node layout enters a plan. */
    when?: {parameter: string; value: string};
    /** Authoring display name and description. Never part of the value contract: two ports with the
     *  same shape are interchangeable whatever their author called them. A composite module keeps
     *  the author's name on the port it exposes, so a caller reads `damage` instead of `in_2`. */
    name?: string;
    label?: string;
    /** A structural enum parameter that types this port: the resolved contract replaces `type` with
     *  the parameter's own member, so one contract covers every value type the node supports
     *  (a comparison's operand, a conversion's result). The declared `type` keeps the port's class
     *  (a typed port is always a value port) and every member the parameter offers is itself a value
     *  port type; the resolved type is the one whose schema, unit, resource kind and handle kind
     *  rules apply, so the declared metadata has to agree with the member the author chose. */
    valueTypeParameter?: string;
    /** A structural enum parameter whose chosen member becomes this port's `schema` at resolution:
     *  the declared schema is the parameter's name in the notation and the resolved contract carries
     *  the concrete set, so one row covers every shared member set without inventing a member list of
     *  its own. The port itself carries no literal `schema` — the parameter is the answer, and only
     *  members that name a shared set are offered. */
    schemaParameter?: string;
    /** The kinds this entity port may carry, as `RuntimeEntitySnapshot.Kind` strings. Absent means
     *  every kind. A declaration names at least one kind and never repeats one. */
    entityKinds?: readonly GraphEntityKind[];
    /** Output preserves the kind bound of this entity input; never narrows from the consumer. */
    entityKindsFrom?: string;
    /** A structural map object address parameter whose category types this entity output: the
     *  resolved contract replaces the (absent) kind bound with the subkind that category is — a
     *  door, a terminal, a generator, or the zone a trigger zone address names. One row therefore
     *  names an object of any category while every consumer still sees the concrete kind its own
     *  port accepts, and a category whose object the consumer cannot take is refused on the wire
     *  rather than reinterpreted. Only an output carries it, only an `object-address` parameter
     *  may be named, and the declaration carries no kind bound of its own. */
    entityKindsFromParameter?: string;
    /** A trigger's single-entity output of another name that is also one of the event's actor roles: the
     *  player a door trigger answers as `actor` is that event's instigator. The role selectors and the
     *  per-player gate read the role from this port; one role has one port. */
    actorRole?: DeclarableActorRole;
    /** Absent means 'one'. An explicit set dimension, not a `-list` type suffix. */
    cardinality?: GraphCardinality;
    /** Payload identity for event/result/policy; the enum set name for enum. */
    schema?: string;
    /** Required on resource ports. Which authored resource kind may be connected. */
    resourceKind?: GraphResourceKind;
    /** Both required on handle ports: who owns it and what releases it. */
    handleKind?: GraphHandleKind;
    lifetime?: HandleLifetime;
    unit?: string;
    /** Integer or number inputs only: the smallest value the runtime accepts on this port, in the
     *  port's own unit. An editor that converts an author's value (seconds → ticks) clamps to it,
     *  so a port whose runtime allows 0 declares 0 instead of inheriting a blanket floor. */
    minimum?: number;
    /** Integer or number inputs only: the largest value the runtime accepts on this port, same unit. */
    maximum?: number;
    nullable?: boolean;
    optional?: boolean;
    /** Result ports only: the I-DIAG domain reason codes this result's `result.reason` may carry. */
    codes?: readonly string[];
    /** Result ports only: the row shape this result writes, four shared columns first. The rows
     *  themselves are validated where a registration is read (runtime-schema.ts). */
    fields?: readonly GraphPortField[];
}

/** One column of a result row: a name, a value type, and the metadata that type allows. The first
 *  column of a row an action owns is not a free declaration: it is one recipient's complete identity,
 *  so it declares the recipient kind and copies that kind's own metadata — `entityKinds` on an entity,
 *  `resourceKind` + `schema` on a resource, `handleKind` + `lifetime` on a handle — and never a
 *  cardinality or a nullability of its own. `graph-schema.ts` derives and checks it against the
 *  action's `recipients` declaration; a later column declares none of these keys, because a second
 *  world reference in a row would have no recipient to be isomorphic with. */
interface GraphPortField {
    id: string;
    type: string;
    unit?: string;
    schema?: string;
    nullable?: boolean;
    entityKinds?: readonly GraphEntityKind[];
    resourceKind?: GraphResourceKind;
    handleKind?: GraphHandleKind;
    lifetime?: HandleLifetime;
}

/** 'value' may be promoted to an input port and computed by another node.
 *  'structural' fixes the compiled shape and can only ever be an editing constant. */
export const graphParameterRoles = ['value', 'structural'] as const;
type GraphParameterRole = (typeof graphParameterRoles)[number];
export interface GraphParameter {
    id: string;
    type: GraphParameterType;
    role: GraphParameterRole;
    required: boolean;
    minimum?: number;
    maximum?: number;
    /** With `set`: an ordered member subset, structural enums only. Without `set`: inline members
     *  for a structural vocabulary no shared set covers. A 'value' enum names a whole set. */
    values?: readonly string[];
    set?: GraphEnumSet;
    /** Carried onto the promoted port so a computed value keeps the same unit. */
    unit?: string;
    /** A structural resource reference names the resource kind it selects and, like a resource
     *  port, the finer schema identity of that kind. */
    resourceKind?: GraphResourceKind;
    schema?: string;
}

export interface ActionRecipientContract {
    /** Required, non-null input port. Never an implicit source, owner or instigator. */
    input: string;
    target: RecipientTargetKind;
    cardinality: GraphCardinality;
    /** Receiver features, not entity kinds or factions. */
    requires: readonly string[];
    /** Required result output; a request is never reported as a commit. */
    result: string;
    /** Handle output of a persistent effect, released with its declared lifetime. */
    handle?: string;
}
/** Context roles are explicit inputs. A port with one of these ids is never optional. */
export const contextRolePorts = ['self', 'source', 'owner', 'instigator', 'event_target'] as const;
/** The roles a trigger output of another name may declare itself to be (`GraphPort.actorRole`). */
export const declarableActorRoles = ['instigator', 'owner'] as const;
type DeclarableActorRole = (typeof declarableActorRoles)[number];

/** One repeated tuple of ports resolved from a single structural count parameter.
 *  Members of one slot share a value contract; different slots do not. */
export interface GraphPortGroup {
    id: string;
    side: 'inputs' | 'outputs';
    parameter: string;
    minimum: number;
    maximum: number;
    /** Base block IDs in tuple-major order; exactly minimum * slots.length entries when declared.
     *  Omitted bases and all additional tuples use `${slot.id}_${index}`, with indices starting at 1. */
    basePortIds?: readonly string[];
    slots: readonly GraphPort[];
}

export interface CapabilityGraphContract {
    domains: readonly ResourceDomain[];
    /** `pure` reads no world state at all; `query` reads it only through the budgeted query
     *  interface and writes nothing. A query node is evaluated on demand by its consumers. */
    execution: ExecutionTier;
    inputs: readonly GraphPort[];
    outputs: readonly GraphPort[];
    parameters: readonly GraphParameter[];
    /** What this capability reads besides its ports: `world` state through the budgeted query
     *  interface, `kernel` services, or `telemetry`. A declaration is what makes a portless
     *  observation belong to the `query` tier; it is not the document field of the same idea
     *  (`effectRequirements`), which stays an action's research note. */
    reads?: readonly GraphReadSource[];
    /** The identity this trigger's own fact carries besides being an event, if any. Only a trigger may declare
     *  one, and the one member today is `shot`: a gate whose `scope` is `shot` books its threshold, cap and clock
     *  against this identity, and a trigger that declares none refuses such a gate at load. */
    factIdentity?: GraphFactIdentity;
    /** The existing base entity output that names this trigger's event subject. Only a trigger may
     *  declare one; its type and entity kinds must be static, independent of parameters or inputs.
     *  One or many subjects, including optional or nullable outputs, are allowed. A trigger with no
     *  subject still fires normally but cannot be used to end a recipient's persistent effect. */
    subject?: string;
    recipients?: ActionRecipientContract;
    /** At most one group per side. */
    portGroups?: readonly GraphPortGroup[];
}
/** `pure` and `query` are evaluated, never executed: a step of either tier drives no execution
 *  and is read on demand by the step that consumes it. */
export function isEvaluatedTier(execution: CapabilityGraphContract['execution']): boolean {
    return execution === 'pure' || execution === 'query';
}
/** The port types that only exist in a live world. A selector, condition or modifier that
 *  declares one of them reads the world and therefore belongs to the `query` tier; a node whose
 *  ports are all plain values stays `pure` and keeps folding. Declared once: the capability
 *  contract rule, the registry check and the plan compiler all ask this instead of restating it. */
export const worldPortTypes = ['entity', 'resource', 'handle'] as const;

export interface ForgeGraphNode extends CapabilityUse {
    id: string;
    capabilityVersion: string;
    /** Empty only for an unbound authoring node; runtime validation must reject it. */
    bindingId: string;
    parameters: {[key: string]: JsonValue};
    /** Parameters this instance turned into input ports; each must have role 'value'. */
    promotions?: readonly string[];
    /** Literals typed into unconnected inputs, keyed by resolved input port id. A wired input never has one. */
    inputs?: {[port: string]: JsonValue};
    /** A trigger card's unified options, compiled to its entrypoint's `gate` (FORGE-FRAMEWORK §3.2). */
    gate?: GraphGate;
    /** An action card's duration options, compiled to its step's `effect` (FORGE-FRAMEWORK §3.2). */
    effect?: GraphEffect;
    /** An action card's own cost group, compiled to its step's `cost_policy`/`cost` (FORGE-FRAMEWORK §3.2). */
    cost?: GraphCost;
}
/** The `gate` block's spellings, in the order the runtime reads them (`TriggerGateContract`). Every
 *  option is optional; times are simulation ticks. */
export const graphGateFields = [
    'accumulate',
    'accumulate_amount',
    'threshold_offset',
    'reset_delay',
    'skip_count',
    'max_count',
    'cooldown',
    'debounce',
    'scope',
    'probability',
    'once'
] as const;
/** Which subject `accumulate`, `skip_count`, `max_count` and `cooldown` are booked against. `shot` books against the shot the
 *  triggering fact belongs to, so it is only offered by a trigger whose own fact carries that identity
 *  (`CapabilityGraphContract.factIdentity`); a card that declares none is refused at load instead of being
 *  booked against its mount instance. */
export const graphGateScopes = ['player', 'level', 'instance', 'shot'] as const;
export type GraphGateScope = (typeof graphGateScopes)[number];
/** The identity a trigger's own fact carries besides being an event. `shot` is the one member today: the fact
 *  belongs to one shot, which is the identity `forge.trigger.combat.hit_candidate` already publishes as its own
 *  event scope. A trigger that declares none publishes no such identity, so a `shot`-scoped gate on it is
 *  refused rather than silently booked against something else. */
export const graphFactIdentities = ['shot'] as const;
type GraphFactIdentity = (typeof graphFactIdentities)[number];
export interface GraphGate {
    accumulate?: number;
    accumulate_amount?: string;
    threshold_offset?: number;
    reset_delay?: number;
    skip_count?: number;
    max_count?: number;
    cooldown?: number;
    debounce?: number;
    scope?: GraphGateScope;
    probability?: number;
    once?: boolean;
}
/** The `effect` block's spellings, in the order the runtime reads them (`EffectOptions`). `reapply`
 *  is spelled with the shared `stack_policy` set and nothing else; `status` with `status_kind` and
 *  `removal` with `status_removal`, so a card declares the state instance it writes and how that
 *  instance ends instead of leaving the runtime to guess. */
export const graphEffectFields = ['duration', 'interval', 'interval_immediate', 'reapply', 'max_stacks', 'end_on', 'cancel', 'status', 'removal'] as const;
export interface GraphEffect {
    duration?: number;
    interval?: number;
    interval_immediate?: boolean;
    reapply?: string;
    max_stacks?: number;
    end_on?: string[];
    cancel?: boolean;
    /** The declared status this write is: a member of the shared `status_kind` set. */
    status?: string;
    /** How that status instance ends (`dispellable` / `until-expiry`); needs `status`. */
    removal?: string;
}
/** The cost group's spellings, in the order the runtime reads them (`CostOptions`): `cost_policy`
 *  says whether the card pays at all, and `cost` is the list it pays, in declaration order. */
export const graphCostFields = ['cost_policy', 'cost'] as const;
/** One declared resource a step pays: a compile-time reference plus the amount, never a value
 *  another node computes. The kind travels with the reference because a cost item is wired to
 *  nothing. */
export interface GraphCostItem {
    resource: {resourceKind: GraphResourceKind; id: string; revision: string};
    amount: number;
}
export interface GraphCost {
    /** `require` pays the list; `none` spends nothing and carries no list. */
    cost_policy?: 'none' | 'require';
    cost?: readonly GraphCostItem[];
}
export interface ForgeGraphEdge {
    from: {node: string; port: string};
    to: {node: string; port: string};
}
/** UI coordinates/comments are not part of the semantic graph. */
export interface ForgeGraph {
    schemaVersion: typeof formatVersion;
    domain: ResourceDomain;
    authority: 'host' | 'owner' | 'presentation';
    nodes: readonly ForgeGraphNode[];
    edges: readonly ForgeGraphEdge[];
    entrypoints: readonly string[];
}

/** Compile output. Variable ports are expanded, ids/schema/unit strings are gone and
 *  every remaining field is a dense integer index or a folded constant. */
export interface CompiledPortSlot {
    /** Position in the node's input or output frame. */
    index: number;
    /** Index into graphPortTypes / graphCardinalities. */
    type: number;
    cardinality: number;
    /** Index into graphEnumSets / graphResourceKinds / graphHandleKinds, else -1. */
    valueSet: number;
    /** Index into handleLifetimes for handle slots, else -1. */
    lifetime: number;
    optional: boolean;
    nullable: boolean;
}
export interface CompiledNodeLayout {
    inputs: readonly CompiledPortSlot[];
    outputs: readonly CompiledPortSlot[];
    /** Every parameter in declaration order; positional, never keyed. null is unset or promoted. */
    constants: readonly JsonValue[];
    /** Declaration indices of promoted parameters, strictly increasing. Their input slots follow
     *  the resolved inputs in this order, so a loader rebuilds the frame without port names. */
    promoted: readonly number[];
}

/** The per-tier role rule in one place: which binding role a node may carry, asked with the
 *  execution tier the capability itself declares. Registration, graph authorization, document
 *  edits and the editors all ask this instead of restating it. An evaluated capability never
 *  executes: a `query` reads the live world through the budgeted query interface (`observe` or
 *  `evaluate`), a `pure` evaluation reads nothing (`evaluate` only). A trigger observes (or
 *  natively executes) its own event source and never evaluates; every other executable kind runs
 *  `execute`. Which provider may observe a canonical trigger stays a separate, provider-scoped
 *  rule. */
export function capabilityAcceptsBindingRole(kind: CapabilityKind, execution: CapabilityGraphContract['execution'], role: BindingDefinition['role']): boolean {
    if (!isEvaluatedTier(execution)) return kind === 'trigger' ? role !== 'evaluate' : role === 'execute';
    return role === 'evaluate' || (role === 'observe' && execution === 'query');
}
