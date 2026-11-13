using System.Text.Json.Nodes;

namespace ForgeSkin.Backend;

// Id: the EnemyDataBlock persistentID a kill sound names. Block: the data block's own name. Name, NameZh: what players call
// it (ForgeCore EnemyNames and its language files); a block that table does not know has its block name in both.
public sealed record EnemyInfo(uint Id, string Block, string Name, string NameZh);

// Every enemy of the installed game, read from its EnemyDataBlock; disabled blocks never spawn and are left out.
public static class Enemies
{
    static readonly Dictionary<string, JsonObject> Lang = new[] { "en", "zh" }.ToDictionary(l => l,
        l => JsonNode.Parse(typeof(Enemies).Assembly.GetManifestResourceStream($"lang.{l}.forge")!)!.AsObject());

    public static List<EnemyInfo> List(GameAssets g) =>
        g.Blocks("Enemy").Values.Where(b => (bool?)b["internalEnabled"] != false).Select(b =>
        {
            var block = (string)b["name"]!;
            string Named(string lang) => ForgeCore.EnemyNames.Key(block) is { } key && (string?)Lang[lang][key] is { } name ? name : block;
            return new EnemyInfo((uint)b["persistentID"]!, block, Named("en"), Named("zh"));
        }).OrderBy(e => e.Id).ToList();
}
