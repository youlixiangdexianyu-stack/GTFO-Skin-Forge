using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Testing;

/// <summary>Reads test registry fixtures and manifest projections into the sole production declaration type.
/// This is test data preparation; the runtime never accepts a JSON registration document.</summary>
public static class TestProviders
{
    // Existing registry fixture assertions inspect the exported wire rows. The views derive from the
    // production bindings; tests do not maintain a second family declaration.
    public static object[] Rows(string providerId, params ProviderBinding[] bindings) =>
        new ProviderDeclaration(providerId, "1.0.0").Bind(bindings).Registry.GetProperty("bindings")
            .EnumerateArray().Cast<object>().ToArray();

    public static BindingSupport[] Support(params ProviderBinding[] bindings) =>
        bindings.Select(row => new BindingSupport(row.Id, row.Verification, row.RequiredPermissions)).ToArray();

    public static IReadOnlyDictionary<string, HandlerShape> Shapes(params ProviderBinding[] bindings) =>
        new ProviderDeclaration("test.fixture", "1.0.0").Bind(bindings).Shapes;

    public static ProviderDeclaration Declaration(string registry, IReadOnlyList<BindingSupport> support,
        IReadOnlyDictionary<string, HandlerShape>? shapes = null, string? package = null)
    {
        using var document = JsonDocument.Parse(registry);
        var seed = document.RootElement;
        var providers = seed.GetProperty("providers").EnumerateArray().ToArray();
        if (providers.Length != 1) throw new ArgumentException("A fixture must identify one provider.");
        var provider = providers[0];
        var declaration = new ProviderDeclaration(provider.GetProperty("id").GetString()!, provider.GetProperty("version").GetString()!, package,
            provider.GetProperty("dependencies").EnumerateArray().Select(value => value.GetString()!).ToArray())
        { Kind = provider.GetProperty("kind").GetString()! }
            .Declare(seed.GetProperty("capabilities").EnumerateArray().Cast<object>());
        foreach (var row in seed.GetProperty("bindings").EnumerateArray())
        {
            string id = row.GetProperty("id").GetString()!, handler = row.GetProperty("handler").GetString()!;
            // Some context-only fixtures supply no verification rows because they never register a module.
            // Their typed binding still has explicit empty permissions; normal registration validates them.
            var evidence = support.SingleOrDefault(item => item.BindingId == id)
                ?? new BindingSupport(id, "implementation-only", Array.Empty<string>());
            HandlerShape? shape = null;
            if (shapes != null) shapes.TryGetValue(handler, out shape);
            var capabilityId = row.GetProperty("capabilityId").GetString()!;
            var capability = declaration.Capabilities.FirstOrDefault(value => value.GetProperty("id").GetString() == capabilityId);
            if (capabilityId.StartsWith("forge.trigger.", StringComparison.Ordinal)
                || capability.ValueKind == JsonValueKind.Object && capability.GetProperty("kind").GetString() == "trigger") shape = null;
            declaration = declaration.Bind(new ProviderBinding(id, capabilityId, handler, row.GetProperty("role").GetString()!,
                evidence.RequiredPermissions, shape)
            {
                Status = row.GetProperty("status").GetString()!,
                Verification = evidence.Verification,
                Dependencies = row.GetProperty("dependencies").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                Requires = row.GetProperty("requires").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                Waitable = row.TryGetProperty("waitable", out var waitable) && waitable.ValueKind == JsonValueKind.True
            });
        }
        return declaration;
    }

    public static RuntimeModule Change(RuntimeModule module, string? registry = null,
        IReadOnlyList<BindingSupport>? support = null, IReadOnlyDictionary<string, HandlerShape>? shapes = null,
        string? package = null) => module with
        {
            Declaration = Declaration(registry ?? module.Declaration.Registry.GetRawText(), support ?? module.BindingSupport,
            shapes ?? module.Shapes, package ?? module.Package)
        };
}
