using System.Text;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Testing;

/// <summary>Drives the real host lifecycle for SDK tests and loads prepared TypeScript compiler output.
/// This test-only API never constructs a runtime plan or substitutes an execution path.</summary>
public static class TestRuntime
{
    /// <summary>The host's immutable content carrier, for source-linked native consumer tests. No parser or
    /// registration result is replaced; dispatch below uses the production dependency-ordering boundary.</summary>
    public static RuntimeContentEntry CreateContentEntry(string id, string domain, string name,
        IReadOnlyList<string> references, string contentJson, string path, uint persistentId,
        IReadOnlyDictionary<string, uint>? blockIds = null)
        => new(id, domain, name, references, contentJson, path, persistentId, blockIds ?? new Dictionary<string, uint> { [id] = persistentId });

    public static void DispatchContent(this RuntimeKernel kernel, IReadOnlyList<RuntimeContentEntry> entries,
        Action<RuntimeContentEntry, ContentRegistrationResult> report)
    {
        kernel.BeginContentDiscovery();
        kernel.DispatchContent(entries, report);
    }

    /// <summary>Stable lookup key for a parameterized fixture; it does not compile or alter a plan.</summary>
    public static string VariantKey(object arguments)
        => ForgeCore.Hash.Sha256Hex(RuntimeJson.From(arguments).GetRawText());

    public static string ReadPlan(string fixtureId, string? variantKey = null)
    {
        var root = FindFixtures();
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "index.json")));
        if (index.RootElement.GetProperty("schemaVersion").GetString() != ForgeCore.FormatVersion.Current)
            throw new InvalidDataException("Unsupported compiled test fixture index.");
        var item = index.RootElement.GetProperty("cases").GetProperty(fixtureId)
            .GetProperty("variants").GetProperty(variantKey ?? "default");
        var path = Path.GetFullPath(Path.Combine(root, item.GetProperty("path").GetString()!));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Compiled fixture path leaves its directory.");
        var bytes = File.ReadAllBytes(path);
        var hash = ForgeCore.Hash.Sha256Hex(bytes);
        if (hash != item.GetProperty("sha256").GetString())
            throw new InvalidDataException("Compiled fixture changed; run pnpm generate:test-plans: " + fixtureId);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    public static bool StartRuntime(this RuntimeKernel kernel, Action initialization) => kernel.StartRuntime(initialization);
    public static void StopRuntime(this RuntimeKernel kernel) => kernel.StopRuntime();
    public static void SetFactAdmissionSuspended(this RuntimeKernel kernel, bool suspended) => kernel.SetFactAdmissionSuspended(suspended);
    public static void SetDiagnosticClock(this RuntimeKernel kernel, Func<long> monotonicMilliseconds) => kernel.SetDiagnosticClock(monotonicMilliseconds);
    public static void SetNativeStateSource(this RuntimeKernel kernel, Func<JsonElement>? capture,
        Action<JsonElement>? restore, Action<JsonElement>? validate = null)
    {
        kernel.NativeStateCapture = capture == null ? null : _ => capture();
        kernel.NativeStateRestore = restore;
        kernel.NativeStateValidate = validate;
    }
    public static void PollDiagnosticSummaries(this RuntimeKernel kernel) => kernel.PollDiagnosticSummaries();
    public static void FlushDiagnosticSummaries(this RuntimeKernel kernel) => kernel.FlushDiagnosticSummaries();

    public static void BeginWorld(this RuntimeKernel kernel, long worldEpoch, long? gateSeed = null)
        => kernel.BeginWorld(worldEpoch, gateSeed);
    public static TickResult Advance(this RuntimeKernel kernel, long simulationTick, bool isHost)
        => kernel.Advance(simulationTick, isHost);
    public static TickResult Advance(this RuntimeKernel kernel, long simulationTick, bool isHost, bool receipts)
        => kernel.Advance(simulationTick, isHost, receipts);
    public static IReadOnlyList<PlanLoadOutcome> LoadPlans(this RuntimeKernel kernel, IReadOnlyList<PlanCandidate> files)
        => kernel.LoadPlans(files);
    public static bool UnloadPlan(this RuntimeKernel kernel, string planId) => kernel.UnloadPlan(planId);
    public static void BeginHostMigration(this RuntimeKernel kernel) => kernel.BeginHostMigration();
    public static string CaptureHostMigration(this RuntimeKernel kernel) => kernel.CaptureHostMigration();
    public static long RestoreHostMigration(this RuntimeKernel kernel, string json, long newWorldEpoch, bool isHost)
        => kernel.RestoreHostMigration(json, newWorldEpoch, isHost);
    public static string ExportVariables(this RuntimeKernel kernel) => kernel.ExportVariables();
    public static void ApplyVariables(this RuntimeKernel kernel, string json) => kernel.ApplyVariables(json);
    public static string CaptureCheckpoint(this RuntimeKernel kernel) => kernel.CaptureCheckpoint();
    public static void RestoreCheckpoint(this RuntimeKernel kernel, string json) => kernel.RestoreCheckpoint(json);
    public static IReadOnlyList<PlanLoadOutcome> RestoreCheckpoint(this RuntimeKernel kernel, string json,
        IReadOnlyList<PlanCandidate> plans) => kernel.RestoreCheckpoint(json, plans);
    public static CommandResult ExecuteOwnerCommand(this RuntimeKernel kernel, string planId, int stepIndex,
        string commandId, string eventId, string scopeId, long simulationTick, JsonElement inputs)
        => kernel.ExecuteOwnerCommand(planId, stepIndex, commandId, eventId, scopeId, simulationTick, inputs);
    public static CommandResult ExecutePresentationCommand(this RuntimeKernel kernel, string planId, int stepIndex,
        string commandId, string eventId, string scopeId, long worldEpoch, long simulationTick, JsonElement inputs)
        => kernel.ExecutePresentationCommand(planId, stepIndex, commandId, eventId, scopeId, worldEpoch, simulationTick, inputs);
    public static void ElevateLogging(this RuntimeKernel kernel) => kernel.ElevateLogging();
    public static void LogSuspended(this RuntimeKernel kernel, string code, string? detail = null)
        => kernel.LogSuspended(code, detail);
    public static void ReportTickRate(this RuntimeKernel kernel, double fixedDeltaTime, double ticksPerSecond)
        => kernel.ReportTickRate(fixedDeltaTime, ticksPerSecond);

    private static string FindFixtures()
    {
        var prepared = Environment.GetEnvironmentVariable("FORGE_COMPILED_TEST_PLANS");
        if (!string.IsNullOrEmpty(prepared))
        {
            var root = Path.GetFullPath(prepared);
            if (!File.Exists(Path.Combine(root, "index.json")))
                throw new TestInputsUnavailableException("FORGE_COMPILED_TEST_PLANS has no compiled-plans/index.json (run build-test-plans.ts --prepare).");
            return root;
        }
        var index = FindInCheckout(Path.Combine("Tests", "Forge", "fixtures", "compiled-plans", "index.json"));
        if (index != null) return Path.GetDirectoryName(index)!;
        throw new TestInputsUnavailableException("no compiled-plans/index.json in the checkout and FORGE_COMPILED_TEST_PLANS is not set (run build-test-plans.ts --prepare).");
    }

    /// <summary>The checkout file at <paramref name="relative"/>, found walking up from the working directory; null when there is none.</summary>
    public static string? FindInCheckout(string relative)
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>The prepared compiled-plan fixtures (a local, generated resource) are absent. A suite reports the case as "needs local
/// resources, not run" and counts it neither as a pass nor as a failure of the code under test.</summary>
public sealed class TestInputsUnavailableException(string reason) : Exception(reason);
