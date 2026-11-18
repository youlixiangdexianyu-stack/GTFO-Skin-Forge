using System.Collections;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Testing;

/// <summary>Builds handler contexts in the production typed cell layout, without loading or synthesizing a plan.</summary>
public static class TestContexts
{
    /// <summary>Invokes one direct handler test through the production deferred-result finalization boundary.</summary>
    public static CommandResult Invoke(CommandContext context, CommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var result = handler(context) ?? throw new InvalidOperationException("A command handler returned null.");
        context.DeferredKernel?.kernelScheduler.FinalizeDeferredResults(context, result);
        return result;
    }

    public static RuntimeQuerySession Query(RuntimeKernel kernel, string nodeId, bool available)
        => new(kernel, nodeId, available);

    public static EvaluationContext Evaluate(string nodeId, JsonElement parameters, JsonElement inputs,
        RuntimeQuerySession queries, RuntimeActorContext actors, RuntimeFactionRelations relations, long tick,
        Func<EntityReference, object?> entityInstance, double? ticksPerSecond = null)
        => new(nodeId, parameters, inputs, queries, actors, relations, tick, entityInstance, ticksPerSecond);

    /// <summary>A test double's answer, given as the object a frame would carry, written into the step's outputs by
    /// output id; the kernel checks each value against its port exactly as it checks a provider's.</summary>
    public static void Answer(EvaluationOutputs outputs, object answer)
    {
        foreach (var field in (answer is JsonElement json ? json : RuntimeJson.From(answer)).EnumerateObject())
            if (Array.IndexOf(outputs.Ids, field.Name) is var index and >= 0) outputs.Cells[index] = CommandValue.OfJson(field.Value);
    }

    /// <summary>Runs an evaluator directly over a standalone answer sheet with the given output ids and reads the
    /// answer back as the object a frame carries.</summary>
    public static JsonElement Read(EvaluatorHandler evaluator, EvaluationContext context, params string[] outputIds)
    {
        var outputs = new EvaluationOutputs(outputIds);
        evaluator(context, outputs);
        return outputs.ToJson();
    }

    public static RuntimeEffectContext Effect(JsonElement handle, string reason, int stacks, long tick, long worldEpoch,
        string planId, string nodeId, string bindingId, string capabilityId, string commandId,
        IReadOnlyList<EntityReference> subject, string statusKind = "", IReadOnlyList<string>? applicationIds = null)
        => new(handle, reason, stacks, tick, worldEpoch, planId, nodeId, bindingId, capabilityId, commandId, subject, statusKind, applicationIds);

    /// <summary>Constructs a typed handler frame. External capabilities require their declaration registry
    /// (the exported manifest's registry property) explicitly; absent ports remain Missing.</summary>
    public static CommandContext Create(RuntimeModule module, string handler, RuntimeEvent origin, long tick,
        string commandId, string planId, string resourceId, string resourceRevision, string nodeId,
        IReadOnlyDictionary<string, object?> parameters, IReadOnlyDictionary<string, object?> inputs,
        bool isHost, Func<EntityReference, object?> entityInstance, double? ticksPerSecond = null,
        JsonElement? effectHandle = null, int? effectStacks = null, bool effectAddLayer = false,
        JsonElement? declarations = null, RuntimeKernel? runtime = null, RuntimeModuleHandle? owner = null)
    {
        var registry = module.Declaration.Registry;
        var bindings = RuntimeJson.Rows(registry, "bindings").Where(row =>
            row.TryGetProperty("handler", out var name) && name.GetString() == handler).ToArray();
        if (bindings.Length == 0) throw new ArgumentException("Unknown handler: " + handler, nameof(handler));
        var capabilityId = RuntimeJson.Text(bindings[0], "capabilityId");
        if (bindings.Any(row => RuntimeJson.Text(row, "capabilityId") != capabilityId))
            throw new ArgumentException("Handler describes multiple capabilities: " + handler, nameof(handler));
        if ((runtime == null) != (owner == null))
            throw new ArgumentException("Deferred-result test contexts require both runtime and owner.", nameof(runtime));
        if (owner != null && (!owner.IsRegistered || owner.ProviderId != RuntimeJson.Text(bindings[0], "providerId")))
            throw new ArgumentException("Deferred-result owner does not own this handler.", nameof(owner));
        var capability = RuntimeJson.Rows(declarations ?? registry, "capabilities")
            .Single(row => RuntimeJson.Text(row, "id") == capabilityId);
        var graph = capability.GetProperty("graph");
        if (module.Shapes == null || !module.Shapes.TryGetValue(handler, out var shape))
            throw new ArgumentException("Handler has no declared shape: " + handler, nameof(handler));
        shape.Resolve(graph);
        var parameterJson = RuntimeJson.From(parameters.ToDictionary(pair => pair.Key, pair => Wire(pair.Value)));
        var expanded = RuntimeGraphContracts.Resolve(graph, parameterJson);
        var parameterDeclarations = RuntimeJson.Rows(graph, "parameters");
        var parameterNames = parameterDeclarations.Select(row => RuntimeJson.Text(row, "id")).ToArray();
        RejectUnknown(parameters.Keys, parameterNames, "parameter");
        parameterJson = RuntimeJson.ResolveEnumParameters(parameterJson, capability);
        var handlerParameters = parameterJson.EnumerateObject().ToDictionary(row => row.Name, row => (object?)row.Value);
        var parameterCells = parameterDeclarations.Select(row => Cell(row, handlerParameters, parameter: true)).ToArray();
        var names = new List<string>();
        var offsets = new List<int>();
        var dense = new List<int>();
        var cells = new List<CommandValue>();
        foreach (var port in RuntimeJson.Rows(expanded, "inputs"))
        {
            var width = RuntimeGraphContracts.FramePort(port).Width;
            if (width == 0) continue;
            names.Add(RuntimeJson.Text(port, "id")); offsets.Add(dense.Count);
            dense.Add(cells.Count);
            for (var i = 1; i < width; i++) dense.Add(-1);
            cells.Add(Cell(port, inputs, parameter: false));
        }
        RejectUnknown(inputs.Keys, names, "input");
        var frame = new RuntimeKernel.StepInputs(names.ToArray(), offsets.ToArray(), dense.ToArray(), cells.ToArray(),
            parameterCells);
        var resultPort = RuntimeGraphContracts.ResultPortName(expanded);
        ResultRowDescriptor? resultDescriptor = null;
        if (runtime != null)
        {
            var resultDeclaration = RuntimeGraphContracts.PortOf(expanded, "outputs", resultPort)
                ?? throw new ArgumentException("Deferred-result handler has no result row.", nameof(handler));
            resultDescriptor = ResultRowAbi.Compile(resultDeclaration, expanded, 0, 1);
        }
        var context = new CommandContext(origin, tick, commandId, new CommandPlan(planId, resourceId, resourceRevision),
            nodeId, parameterJson, frame, isHost, entityInstance, ticksPerSecond,
            runtime, owner?.ProviderId ?? "", owner?.Generation ?? 0, nodeId,
            RuntimeJson.Text(bindings[0], "id"), resultPort, resultDescriptor, expanded,
            detachedDeferredContext: true);
        SetEffect(ref context, effectHandle, effectStacks, effectAddLayer);
        return context;
    }

    /// <summary>Makes a direct-test context one that may answer later, as the kernel's owner entry point does for an
    /// owner step with no effect and no returned handle.</summary>
    public static void AllowOwnerDeferral(ref CommandContext context, RuntimeKernel runtime)
        => context.OwnerDeferralKernel = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public static void SetEffect(ref CommandContext context, JsonElement? handle, int? stacks = null, bool addLayer = false,
        string? applicationId = null)
    {
        context.EffectHandleId = handle is { } wire && RuntimeHandle.TryRead(wire, out var id) ? id : null;
        context.EffectStacks = stacks;
        context.EffectAddLayer = addLayer;
        context.EffectApplicationId = applicationId ?? (handle == null ? null : context.CommandId);
    }

    private static CommandValue Cell(JsonElement declaration, IReadOnlyDictionary<string, object?> supplied, bool parameter)
    {
        if (!supplied.TryGetValue(RuntimeJson.Text(declaration, "id"), out var value)) return default;
        var json = Wire(value);
        if (parameter) return CommandValue.Read(json, ResolvedStep.ParameterKind(declaration), many: false);
        var port = PortView.Of(new[] { declaration })[0];
        return CommandValue.Read(port.HandlerValue(json), port.HandlerKind, port.Many);
    }

    private static JsonElement Wire(object? value)
    {
        if (value is JsonElement json) return json;
        if (value is ValueTuple<double, double, double> vector)
            return RuntimeJson.From(new[] { vector.Item1, vector.Item2, vector.Item3 });
        if (value is IEnumerable sequence && value is not string && value is not IDictionary)
            return RuntimeJson.From(sequence.Cast<object?>().Select(Wire).ToArray());
        return RuntimeJson.From(value!);
    }

    private static void RejectUnknown(IEnumerable<string> actual, IEnumerable<string> declared, string side)
    {
        var known = new HashSet<string>(declared, StringComparer.Ordinal);
        foreach (var name in actual)
            if (!known.Contains(name)) throw new ArgumentException("Unknown handler " + side + ": " + name);
    }
}
