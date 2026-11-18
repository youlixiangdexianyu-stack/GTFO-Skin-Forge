using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTestInputs;

/// <summary>Test-only reads share the tools' declared roots. This API never selects a write destination.</summary>
internal static class LocalInputs
{
    internal static string Repository
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("FORGE_TEST_REPOSITORY");
            if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Tools", "resource-roots.json"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Cannot locate Tools/resource-roots.json for local test inputs");
        }
    }

    internal static string Resolve(string input)
    {
        var repository = Repository;
        var local = Path.GetFullPath(Path.IsPathRooted(input) ? input : Path.Combine(repository, input));
        if (File.Exists(local) || Directory.Exists(local)) return local;
        var relative = Path.GetRelativePath(repository, local).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) throw new FileNotFoundException("Test input is missing outside the repository");
        using var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "Tools", "resource-roots.json")));
        foreach (var row in table.RootElement.EnumerateArray())
        {
            var root = row.GetProperty("path").GetString()!;
            if (relative != root && !relative.StartsWith(root + "/", StringComparison.Ordinal)) continue;
            var tail = relative.Length == root.Length ? "" : relative[(root.Length + 1)..];
            if (tail.Length > 0 && row.TryGetProperty("include", out var include) && !include.EnumerateArray().Any(pattern => Matches(tail, pattern.GetString()!))) continue;
            var variable = row.GetProperty("variable").GetString()!;
            var shared = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(shared))
            {
                var environmentRoot = row.TryGetProperty("environmentPath", out var environmentPath) ? environmentPath.GetString()! : root;
                var candidate = Path.GetFullPath(Path.Combine(shared, Path.GetRelativePath(environmentRoot, root), tail));
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("需要本机资源，未运行：" + relative + " (" + variable + ")");
        }
        throw new FileNotFoundException("Missing undeclared test input: " + relative);
    }

    private static bool Matches(string value, string pattern)
    {
        var expression = Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return Regex.IsMatch(value, "^" + expression + "$") || pattern.EndsWith("/**", StringComparison.Ordinal) && Matches(value, pattern[..^3]);
    }
}
