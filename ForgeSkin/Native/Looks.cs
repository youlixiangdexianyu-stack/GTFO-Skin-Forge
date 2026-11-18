using BepInEx;
using ForgeCore;
using ForgeSkin.Format;
using Player;
using SNetwork;

namespace ForgeSkin.Plugin;

// Every player's look (PLAN §4.11): which skin each gear of theirs wears and how they changed it, as ForgeSkin's lobby value "look"
// (ForgeCore Net: each player writes only their own, ForgeCore sends it to every player with the same ForgeSkin, again to one who joins
// later, and forgets the look of one who left). The local player's is written whole when it changes (a pick, colour or adjustment half
// a second after the last one, the loadout, a wear stage); the same bytes are never sent twice. A teammate's is only shown: a gear whose
// skin is installed here in the same pack shows it with their changes; one whose skin is missing, a different pack, or a change this
// skin does not have shows its original look, one log line per player and skin; a look that cannot be read leaves that player's gear
// all original. Nothing of the vanilla game's own sync is touched.
static class Looks
{
    static NetLobbyValue? _row;
    // ForgeSkin's one Net channel (ForgeCore opens a mod's channel once); Inspect adds its per-player state to it.
    public static NetChannel? Channel;

    // What a teammate's gear wears: the skin, their changes as picks, the wear stage reached, and a number that moves with any change
    // (their coloured copy of the skin is made once per number).
    public sealed class Worn
    {
        public Skin Skin = null!;
        public Dictionary<string, float[]> Picks = null!;
        public byte Wear;
        public int Version;
    }

    sealed class Mate
    {
        public Look? Look; // null: none yet, or malformed
        public byte[]? Raw; // a bot's look as the host sent it
        public readonly Dictionary<string, Worn> ByKey = new(); // gear key -> what it wears
        public readonly List<(string Gear, string Pack, string? Skin, uint Number)> Unshown = new(); // what the picker lists: gear key, Thunderstore mod, skin name if installed; and the skin's number
    }

    static readonly Dictionary<ulong, Mate> Players = new();
    static int _versions;

    public static void Register()
    {
        try
        {
            Channel = Net.Open(ForgeSkinPlugin.Guid);
            _row = Channel.Lobby("look");
            _row.Changed += Received;
            _bots = Channel.Lobby("bots");
            _bots.Changed += _ => BotsChanged();
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-look-failed", $"looks are not shared with teammates: {e.Message}", e); }
    }

    // `player`'s look as it is shared (their character and their gear's skins), or null when it names nothing: ForgeCore's player look.
    public static byte[]? Describe(SNet_Player player)
    {
        if (_row == null) return null;
        if (player.IsBot) return Players.TryGetValue(Key(player), out var bot) ? bot.Raw : null;
        var bytes = player.IsLocal ? _row.Local : _row.TryGet(player.Lookup, out var theirs) ? theirs : null;
        return bytes != null && Look.Decode(bytes) is { } look && (look.Character != null || look.Gear.Count > 0) ? bytes : null;
    }

    // What `owner`'s gear of `key` wears now (null: its original look).
    public static Worn? WornBy(ulong owner, string key) => Players.TryGetValue(owner, out var p) ? p.ByKey.GetValueOrDefault(key) : null;

    // Adds what every teammate's look has their `key` wear here.
    public static void Wearing(string key, HashSet<Skin> used)
    {
        foreach (var p in Players.Values)
            if (p.ByKey.TryGetValue(key, out var worn)) used.Add(worn.Skin);
    }

    // The skins a teammate's gear wears: their own skin for it, never this player's global skins.
    public static List<Skin> Wanted(ulong owner, string key) => WornBy(owner, key) is { } worn ? new List<Skin> { worn.Skin } : new List<Skin>();

    // The picker's lines: teammates' gear this player sees in its original look because the skin is missing or another version
    // (player name, gear key, Thunderstore mod, skin name if installed). Names are read here, when the picker draws, not on receipt.
    public static IEnumerable<(string Player, string Gear, string Pack, string? Skin)> Unshown()
    {
        foreach (var (lookup, p) in Players)
            foreach (var (gear, pack, skin, _) in p.Unshown) yield return (NameOf(lookup), gear, pack, skin);
    }

    static string NameOf(ulong lookup) => PlayerOf(lookup)?.NickName ?? lookup.ToString();

    static SNet_Player? PlayerOf(ulong lookup)
    {
        // A bot is not in SNet.LobbyPlayers (it lists the humans only): it is the bot in its slot.
        if ((lookup & BotBase) != 0)
        {
            var slot = (int)(lookup & ~BotBase);
            return SNet.Slots != null && slot < SNet.Slots.PlayerSlots.Length && SNet.Slots.GetPlayerInSlot(slot) is { IsBot: true } bot ? bot : null;
        }
        var players = SNet.LobbyPlayers;
        if (players != null)
            foreach (var player in players)
                if (player != null && Key(player) == lookup) return player;
        return null;
    }

    static void Received(ulong lookup)
    {
        try
        {
            if (_row == null || !_row.TryGet(lookup, out var bytes))
            {
                Players.Remove(lookup);
                foreach (var skin in Skins.ById.Values) skin.Forget(lookup);
            }
            else
            {
                var look = Look.Decode(bytes);
                if (look == null)
                    ForgeSkinPlugin.Fail("skin-look-malformed", $"player {lookup}'s look could not be read ({bytes.Length} bytes); their gear shows its original look");
                if (!Players.TryGetValue(lookup, out var player)) Players[lookup] = player = new Mate();
                player.Look = look;
                Resolve(lookup, player);
            }
            SkinnedGear.OwnerChanged(lookup);
            Picker.Changed();
            if (PlayerOf(lookup) is { } who) Characters.LookChanged(who);
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-look-failed", $"look of player {lookup}: {e.Message}", e); }
    }

    // After a reload or a content hash arriving: every teammate's look is matched against the packs again and their gear put on anew.
    public static void ResolveAll(bool refresh = true)
    {
        foreach (var (lookup, player) in Players)
        {
            Resolve(lookup, player);
            if (refresh) SkinnedGear.OwnerChanged(lookup);
        }
    }

    static readonly Dictionary<string, Worn> Before = new();

    // An entry that names what it named before (same skin, wear stage and changes) stays the same object: the gear and character
    // wearing it are left as they are, only what changed is put on again.
    static void Resolve(ulong lookup, Mate player)
    {
        Before.Clear();
        foreach (var (key, worn) in player.ByKey) Before[key] = worn;
        player.ByKey.Clear();
        player.Unshown.Clear();
        if (player.Look != null) Resolve(player.Look, $"player {lookup}", player.ByKey, player.Unshown);
        foreach (var (key, worn) in player.ByKey.ToList())
            if (Before.TryGetValue(key, out var was) && was.Skin == worn.Skin && was.Wear == worn.Wear && SamePicks(was.Picks, worn.Picks)) player.ByKey[key] = was;
        Before.Clear();
        TellMissing(lookup, player);
    }

    // What a teammate wears that is not installed here is said once in the chat (only on this machine), per teammate and per pack
    // (the Thunderstore mod it came in, else the skin): the picker lists the details. Said once the teammate's name is known.
    static readonly HashSet<(ulong Player, string Pack)> Told = new();

    static void TellMissing(ulong lookup, Mate player)
    {
        foreach (var (_, pack, skin, number) in player.Unshown)
        {
            var told = (lookup, pack.Length > 0 ? pack : number.ToString("x8"));
            if (skin != null || Told.Contains(told) || PlayerOf(lookup) is not { } who) continue;
            Told.Add(told);
            var ui = ForgeUI.UIKit.Mod(ForgeSkinPlugin.Guid);
            ForgeUI.UIKit.PostChatNotice(ui.Text("chat.teammateMissing", who.NickName, pack.Length > 0 ? ui.Text("picker.teammateInstall", pack) : ""));
        }
    }

    static bool SamePicks(Dictionary<string, float[]> a, Dictionary<string, float[]> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
            if (!b.TryGetValue(key, out var other) || !value.AsSpan().SequenceEqual(other)) return false;
        return true;
    }

    // What `look` wears here, by gear key (the character model under "character"): the skins installed in the same pack, with the
    // changes it names; the rest is listed in `unshown` and logged under `who`. A skin whose content hash is not known yet is left out
    // (ResolveAll runs again when it arrives).
    public static void Resolve(Look look, string who, Dictionary<string, Worn> byKey, List<(string Gear, string Pack, string? Skin, uint Number)> unshown)
    {
        if (!Skins.Enabled) return;
        var entries = look.Gear.Select(g => (Key: SkinTarget.GearKey(g.GearId), Entry: (LookSkin)g, Wear: g.Wear));
        if (look.Character is { } person) entries = entries.Append((CharacterSkin.Target, person, (byte)0));
        foreach (var (key, g, wear) in entries)
        {
            var skin = Skins.ByNumber.GetValueOrDefault(g.Skin);
            if (skin != null && skin.Doc.Target.Key() != key) skin = null; // the number of a skin made for another gear
            ulong hash = 0;
            if (skin != null)
            {
                if (skin.ContentHash() is not { } known) continue; // read on a worker: ResolveAll runs again when it arrives
                hash = known;
            }
            var fit = Look.Fit(g, skin?.Doc, hash, out var picks, out var why);
            if (fit == LookFit.Fits)
            {
                byKey[key] = new Worn { Skin = skin!, Picks = picks!, Wear = wear, Version = ++_versions };
                continue;
            }
            unshown.Add((key, g.Pack, skin == null ? null : skin.Doc.Name.Length > 0 ? skin.Doc.Name : skin.Doc.Id, g.Skin));
            var pack = g.Pack.Length > 0 ? $" (Thunderstore {g.Pack})" : "";
            switch (fit)
            {
                case LookFit.Missing:
                    ForgeSkinPlugin.Fail("skin-look-missing", $"{who}'s {key} wears skin {g.Skin:x8}{pack}, which is not installed here; shown in its original look");
                    break;
                case LookFit.Mismatch:
                    ForgeSkinPlugin.Fail("skin-look-mismatch", $"{who}'s {key} wears {skin!.Doc.Id}{pack} from another pack than the one installed here; shown in its original look");
                    break;
                default:
                    ForgeSkinPlugin.Fail("skin-look-bad-value", $"{who}'s {key} wears {skin!.Doc.Id} with a change it does not have ({why}); shown in its original look");
                    break;
            }
        }
    }

    // ---- the local player's own look ----

    static int _skinsVersion = -1, _wearVersion = -1;
    static float _due = -1, _nextCheck;
    static bool _pending; // the last build waited for a content hash

    // Main thread, every frame: writes the local look when what it holds moved (choices half a second after the last change).
    public static void Tick(float now)
    {
        if (_row == null) return;
        if (now >= _nextBots)
        {
            _nextBots = now + 1f;
            WriteBots();
            BotsChanged();
        }
        if (Skins.Version != _skinsVersion) { _skinsVersion = Skins.Version; _due = now + 0.5f; }
        if (_due >= 0 && now >= _due) { _due = -1; Write(); return; }
        if (now < _nextCheck) return;
        _nextCheck = now + 1f;
        if (_pending || _gearSpawned || Wear.Version != _wearVersion) Write();
    }

    static bool _gearSpawned;

    // A gear of the local player's was built (a loadout change builds the new gear): the loadout is read again within a second.
    public static void GearSpawned() => _gearSpawned = true;

    // The game's LocalBackpack throws (inside its GetBackpack) until its backpack manager and the local player exist: from the first lobby on.
    static bool _session;

    public static void StateChanged(eGameStateName state) => _session |= state >= eGameStateName.Lobby;

    static bool Loadout(out (uint a, uint b, uint c, uint d) ids)
    {
        ids = default;
        if (!_session) return false;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (backpack == null) return false;
        uint Of(InventorySlot slot) => backpack.TryGetBackpackItem(slot, out var item) && item?.GearIDRange is { } range ? SkinnedGear.GearIdOf(range) : 0;
        var s = SkinnedGear.GearSlots;
        ids = (Of(s[0]), Of(s[1]), Of(s[2]), Of(s[3]));
        return true;
    }

    static void Write()
    {
        try
        {
            _gearSpawned = false;
            if (!Loadout(out var ids)) { _pending = true; return; } // no backpack yet (not in a lobby): tried again each second
            _wearVersion = Wear.Version;
            _pending = false;
            var look = new Look();
            if (Skins.Own(CharacterSkin.Target) is { } person)
            {
                if (person.ContentHash() is not { } own) { _pending = true; return; }
                look.Character = new LookSkin { Skin = person.Number, Hash = own, Pack = person.Pack };
                Look.Tweaks(person.Doc, Skins.ColoursOf(person.Doc.Id), look.Character.Tweaks);
            }
            foreach (var id in new[] { ids.a, ids.b, ids.c, ids.d })
            {
                if (id == 0) continue;
                var key = SkinTarget.GearKey(id);
                if (Skins.Own(key) is not { } skin) continue;
                if (skin.ContentHash() is not { } hash) { _pending = true; return; }
                var gear = new LookGear { GearId = id, Skin = skin.Number, Hash = hash, Pack = skin.Pack, Wear = WearStage(skin, key) };
                Look.Tweaks(skin.Doc, Skins.ColoursOf(skin.Doc.Id), gear.Tweaks);
                look.Gear.Add(gear);
            }
            var bytes = look.Encode();
            var changed = _row!.Local == null || !bytes.AsSpan().SequenceEqual(_row.Local);
            if (changed) ForgeSkinPlugin.Info($"look written: {(look.Character != null ? "a character, " : "")}{look.Gear.Count} gear, {bytes.Length} bytes");
            _row.Set(bytes);
            if (changed) Characters.OwnLookChanged();
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-look-failed", $"own look not written: {e.Message}", e); }
    }

    // The stage the first worn material of the skin reached with the local player's kills on this gear (SkinnedGear.Look counts the same).
    static byte WearStage(Skin skin, string key)
    {
        var stages = skin.Doc.Materials.Find(m => m.Wear is { Count: > 0 })?.Wear;
        if (stages == null) return 0;
        return (byte)Math.Min(byte.MaxValue, stages.IndexOf(Wear.Stage(stages, Wear.Count($"{skin.Doc.Id}|{key}"))));
    }

    // ---- bots (PLAN §4.11): the host picks what each bot wears, every player sees it ----
    // A bot has no machine of its own to write a look, so the host writes all of them in one lobby value, "bots": per bot slot, a look
    // like a player's (character and the skins of the gear the bot carries). Every player, the host too, keeps a bot's look under the
    // bot's key (BotKey), so its gear and person are dressed exactly as a teammate's are. Only the value of the player who is the host
    // now is read. The host's picks are kept per slot in BepInEx/ForgeSkin/bots.json; colours and adjustments are the host's own for
    // that skin.

    static NetLobbyValue? _bots;
    static float _nextBots;
    const ulong BotBase = 1UL << 62; // no Steam id has this bit

    public static ulong BotKey(int slot) => BotBase | (uint)slot;

    // Whose look a player's gear and person wear: a bot's by its slot, a player's by their id.
    public static ulong Key(SNet_Player player) => player.IsBot ? BotKey(player.PlayerSlotIndex()) : player.Lookup;

    static readonly string BotFile = Path.Combine(SkinFolders.Root(Paths.BepInExRootPath), "bots.json");
    static Dictionary<string, Dictionary<string, string>>? _botChoices; // slot -> target key -> skin id

    static Dictionary<string, Dictionary<string, string>> BotChoices() =>
        _botChoices ??= Store.Read<Dictionary<string, Dictionary<string, string>>>(ForgeSkinPlugin.Guid, BotFile).Value ?? new();

    // The skin the host picked for the bot in `slot`'s `key` (null: its original look).
    public static Skin? BotPick(int slot, string key) =>
        BotChoices().TryGetValue(slot.ToString(), out var picks) && picks.TryGetValue(key, out var id) && Skins.ById.TryGetValue(id, out var skin) && skin.Doc.Target.Key() == key ? skin : null;

    // The host's pick for a bot ("" = the original look): kept, and sent with the bots' looks at once.
    public static void ChooseBot(int slot, string key, string id)
    {
        var all = BotChoices();
        if (!all.TryGetValue(slot.ToString(), out var picks)) all[slot.ToString()] = picks = new();
        if (id == "") picks.Remove(key);
        else picks[key] = id;
        ForgeSkinPlugin.Info($"picked for the bot in slot {slot}, {key}: {(id == "" ? "vanilla" : id)}");
        Store.Write(ForgeSkinPlugin.Guid, BotFile, all);
        WriteBots();
        BotsChanged();
    }

    // The host writes every bot's look; any other player clears theirs (a host who stopped being one).
    static void WriteBots()
    {
        try
        {
            if (_bots == null) return;
            // The bots are read from the slots: SNet.LobbyPlayers lists the humans only.
            var slots = _session && SNet.IsMaster && Skins.Enabled && SNet.Slots != null ? SNet.Slots.PlayerSlots.Length : 0;
            var rows = new List<(byte Slot, byte[] Look)>();
            for (var slot = 0; slot < slots; slot++)
            {
                if (SNet.Slots!.GetPlayerInSlot(slot) is not { IsBot: true } player) continue;
                var look = new Look();
                if (BotPick(slot, CharacterSkin.Target) is { } person)
                {
                    if (person.ContentHash() is not { } own) return; // read on a worker: written within a second
                    look.Character = new LookSkin { Skin = person.Number, Hash = own, Pack = person.Pack };
                    Look.Tweaks(person.Doc, Skins.ColoursOf(person.Doc.Id), look.Character.Tweaks);
                }
                if (PlayerBackpackManager.TryGetBackpack(player, out var backpack) && backpack != null)
                    foreach (var gearSlot in SkinnedGear.GearSlots)
                    {
                        if (!backpack.TryGetBackpackItem(gearSlot, out var item) || item?.GearIDRange is not { } range) continue;
                        var id = SkinnedGear.GearIdOf(range);
                        if (id == 0 || BotPick(slot, SkinTarget.GearKey(id)) is not { } skin) continue;
                        if (skin.ContentHash() is not { } hash) return;
                        var gear = new LookGear { GearId = id, Skin = skin.Number, Hash = hash, Pack = skin.Pack };
                        Look.Tweaks(skin.Doc, Skins.ColoursOf(skin.Doc.Id), gear.Tweaks);
                        look.Gear.Add(gear);
                    }
                if (look.Character != null || look.Gear.Count > 0) rows.Add(((byte)slot, look.Encode()));
            }
            byte[]? bytes = null;
            if (rows.Count > 0)
            {
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);
                writer.Write((byte)rows.Count);
                foreach (var (slot, look) in rows)
                {
                    writer.Write(slot);
                    writer.Write((ushort)look.Length);
                    writer.Write(look);
                }
                bytes = stream.ToArray();
            }
            var changed = bytes == null ? _bots.Local != null : _bots.Local == null || !bytes.AsSpan().SequenceEqual(_bots.Local);
            if (changed) ForgeSkinPlugin.Info($"bots' looks written: {rows.Count} bot(s), {bytes?.Length ?? 0} bytes");
            _bots.Set(bytes);
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-look-failed", $"bots' looks not written: {e.Message}", e); }
    }

    static readonly Dictionary<int, byte[]> BotRows = new();
    static byte[]? _botsRead; // the host's value BotRows was read from

    // The host's value read again: each bot's look that changed is resolved against the packs here and its gear and person dressed anew.
    static void BotsChanged()
    {
        try
        {
            if (_bots == null) return;
            var master = SNet.Master;
            var bytes = master == null ? null : master.IsLocal ? _bots.Local : _bots.TryGet(master.Lookup, out var theirs) ? theirs : null;
            if (bytes == _botsRead) return;
            _botsRead = bytes;
            BotRows.Clear();
            if (bytes != null)
                try
                {
                    using var reader = new BinaryReader(new MemoryStream(bytes));
                    for (int i = 0, count = reader.ReadByte(); i < count; i++)
                    {
                        var slot = reader.ReadByte();
                        BotRows[slot] = reader.ReadBytes(reader.ReadUInt16());
                    }
                }
                catch (Exception e)
                {
                    BotRows.Clear();
                    ForgeSkinPlugin.Fail("skin-look-malformed", $"the host's bot looks could not be read ({bytes.Length} bytes: {e.Message}); the bots show their original look");
                }
            var keys = Players.Keys.Where(k => (k & BotBase) != 0).Concat(BotRows.Keys.Select(BotKey)).Distinct().ToList();
            foreach (var key in keys)
            {
                var slot = (int)(key & ~BotBase);
                if (BotRows.TryGetValue(slot, out var raw))
                {
                    if (!Players.TryGetValue(key, out var bot)) Players[key] = bot = new Mate();
                    else if (bot.Raw != null && raw.AsSpan().SequenceEqual(bot.Raw)) continue;
                    bot.Raw = raw;
                    bot.Look = Look.Decode(raw);
                    if (bot.Look == null) ForgeSkinPlugin.Fail("skin-look-malformed", $"the look of the bot in slot {slot} could not be read ({raw.Length} bytes); it shows its original look");
                    Resolve(key, bot);
                }
                else
                {
                    Players.Remove(key);
                    foreach (var skin in Skins.ById.Values) skin.Forget(key);
                }
                SkinnedGear.OwnerChanged(key);
                if (PlayerOf(key) is { } who) Characters.LookChanged(who);
            }
            Picker.Changed();
        }
        catch (Exception e) { ForgeSkinPlugin.Fail("skin-look-failed", $"bots' looks: {e.Message}", e); }
    }
}
