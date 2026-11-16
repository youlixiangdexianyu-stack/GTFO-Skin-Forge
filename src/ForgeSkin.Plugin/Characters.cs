using CellMenu;
using ForgeCore;
using ForgeSkin.Format;
using ForgeVisual;
using Player;
using SNetwork;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeSkin.Plugin;

// Character models (PLAN §4.10): a player's person swapped for a character skin's model, which has its own humanoid skeleton and copies
// the game's body pose every frame. Everything is ForgeCore's: the model is built by ImportedModels (format 2), the pose copy with its
// fingers and the arms reaching the grip is Models.CopyPose over HumanRig's links, hair and skirts are Chains bound after the copy, and the
// original person is hidden by Persons.Hide (counted, so an exosuit hiding the same pilot does not show them again). The model hangs on
// the body through PlayerLooks.Hang (this look's own hold keeps it, a hold that is not a look, an exosuit worn over the person, hides it with
// the person until it lets go; the own-body view draws it, a copy of the body leaves it out). The local
// player wears their own pick; a teammate the character their look names (a pack installed here with the same content, else the
// original person); a bot always the original. The shared look (Looks, character and gear skins together) is also ForgeCore's player
// look (PlayerLooks): a replay stand-in asks for it, wears the character on its own body and its weapons' skins through Hold. A model
// that cannot be read or does not fit its skeleton leaves the original person, one line. In first person the local player sees the
// model's own arms (Arms below). The lobby's figure behind each player's loadout wears the same character, the way a stand-in does (Lobby).
// Materials the author marked toon wear ForgeCore's toon shader (ToonDress) in the lighting the player chose (ForgeSkinPlugin.Toon, this
// machine only; Off draws them lit), switched on everyone wearing a character at once.
static class Characters
{
    const string Label = "ForgeSkin character";

    // One character model on one body (a player's, or a stand-in's).
    sealed class Worn
    {
        public Skin Skin = null!;
        public GameObject Root = null!, Model = null!;
        public PoseCopy? Copy;
        public ToonDress? Toon; // the author's toon materials, when there are any
        public readonly List<ChainBinding> Chains = new();
        public PersonHide? Hide; // the body's own person
        public FirstPersonWear? Arms; // the local player's own arms in first person
        public OwnBodyCut? Cut; // the local player's model without head and arms in first person
        public bool Released;

        public void Release()
        {
            if (Released) return;
            Released = true;
            Cut?.Dispose();
            Arms?.Destroy();
            foreach (var chain in Chains) chain.Release();
            Copy?.Release();
            Toon?.Dispose();
            Hide?.Release();
            if (!NativeObjects.Gone(Root)) Object.Destroy(Root);
        }
    }

    // A look on a replay stand-in: its character (null: its own person shows) and the skins of the gear it holds.
    sealed class Dressed : ILookDress
    {
        public Worn? Person;
        public readonly Dictionary<string, Looks.Worn> Gear = new();
        public readonly List<SkinnedGear> Held = new();
        public bool Released;

        public void Hold(ItemEquippable item)
        {
            if (Released || item == null) return;
            var key = SkinnedGear.KeyOf(item);
            if (Gear.TryGetValue(key, out var worn)) { Held.Add(SkinnedGear.Dress(item, worn)); return; }
            // Never silent: a gear whose id cannot be worked out here matches no skin of the look; any other key is a gear the look has no skin for.
            if (key == SkinTarget.GearKey(0))
                ForgeSkinPlugin.Fail("skin-look-gear-unknown", $"a stand-in's {item.PublicName}: its gear range matches no gear here, so the look's skins ({string.Join(", ", Gear.Keys)}) cannot be matched to it; shown in its original look");
            else ForgeSkinPlugin.Note($"a stand-in's {item.PublicName} ({key}): the look has no skin for it ({(Gear.Count == 0 ? "none" : string.Join(", ", Gear.Keys))}); shown in its original look");
        }

        public void Release()
        {
            if (Released) return;
            Released = true;
            Person?.Release();
            foreach (var gear in Held) SkinnedGear.Undress(gear);
        }
    }

    static readonly Dictionary<IntPtr, (PlayerAgent Agent, Worn Worn)> ByAgent = new(); // by agent
    // The lobby's figures (CM_PlayerLobbyBar.m_playerModel, the person standing behind each player's loadout): by bar, with the animator worn on.
    static readonly Dictionary<IntPtr, (Animator Body, Worn Worn)> InLobby = new();
    static readonly List<Dressed> StandIns = new(); // worn through PlayerLooks.Dress
    static readonly HashSet<Skin> Broken = new(); // models that could not be worn: the original person, not tried again until a reload
    static bool _dirty = true, _waiting;
    static float _next;
    static PlayerLookProvision? _looks;

    public static void Register() => _looks = PlayerLooks.Provide(ForgeSkinPlugin.Guid, new Provider());

    // Something that decides who wears what changed (a pick, a reload, a teammate's look, a skin loaded, a player made, a level state).
    public static void Dirty() => _dirty = true;

    // `player`'s shared look changed: ForgeCore's player looks tell their users.
    public static void LookChanged(SNet_Player player) => _looks?.Changed(player);

    // The local player's: told at the next Reconcile, off the per-frame path.
    public static void OwnLookChanged() => _ownChanged = _dirty = true;

    static bool _ownChanged;

    // The skins the worn models need kept loaded (a stand-in's gear keeps its own, as every gear does), and the characters the players
    // here wear while no body shows them (between the lobby and the level, a dead body): kept for as long as the player picks it or a
    // teammate's look names it, so the drop never frees a model the landing wears again.
    public static void InUse(HashSet<Skin> used)
    {
        if (Skins.Own(CharacterSkin.Target) is { } own) used.Add(own);
        Looks.Wearing(CharacterSkin.Target, used);
        foreach (var (_, worn) in ByAgent.Values) used.Add(worn.Skin);
        foreach (var (_, worn) in InLobby.Values) used.Add(worn.Skin);
        StandIns.RemoveAll(d => d.Released || d.Person == null || NativeObjects.Gone(d.Person.Model));
        foreach (var dressed in StandIns) used.Add(dressed.Person!.Skin);
    }

    // Main thread, every frame: twice a second, looks again when something changed or a worn model was lost (a level ended, the agent
    // or the model is gone, a skin freed).
    public static void Tick(float now)
    {
        if (now < _next) return;
        _next = now + 0.5f;
        if (!_dirty && !_waiting && !Lost()) return;
        _dirty = false;
        Reconcile(now);
    }

    static bool Lost()
    {
        foreach (var (_, worn) in ByAgent.Values)
            if (Loss(worn) != null) return true;
        // A figure the lobby rebuilt; one it has not built yet keeps Lobby waiting.
        foreach (var (body, worn) in InLobby.Values)
            if (NativeObjects.Gone(worn.Model) || NativeObjects.Gone(body) || worn.Hide is { Active: false }) return true;
        return false;
    }

    const string ArmsLost = "its first-person arms are gone";

    // What a player's worn character lost (null: nothing; ArmsLost: only the local player's first-person arms).
    static string? Loss(Worn worn) =>
        NativeObjects.Gone(worn.Model) ? "its model is gone"
        : worn.Hide is not { Active: true } ? "the person's hold ended"
        : worn.Arms is { Alive: false } ? ArmsLost
        : null;

    static readonly List<(IntPtr Bar, Animator Body, SNet_Player Player)> FiguresNow = new();

    // The lobby's figures shown now: each lobby bar with a player whose figure is spawned and drawn, and the figure's animator. In the
    // lobby, a player whose figure is not built or drawn yet keeps Reconcile looking (_waiting).
    static List<(IntPtr Bar, Animator Body, SNet_Player Player)> Figures()
    {
        FiguresNow.Clear();
        var page = CM_PageLoadout.Current;
        var bars = page == null || page.WasCollected ? null : page.m_playerLobbyBars;
        if (bars == null) return FiguresNow;
        var inLobby = GameStateManager.CurrentStateName == eGameStateName.Lobby;
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar == null || bar.WasCollected || bar.m_player is not { } player) continue;
            var figure = bar.m_playerModelSpawned ? bar.m_playerModel : null;
            if (figure == null || figure.WasCollected || !figure.activeInHierarchy || figure.GetComponentInChildren<Animator>() is not { } body)
            {
                _waiting |= inLobby && Wanted(player) != null;
                continue;
            }
            FiguresNow.Add((bar.Pointer, body, player));
        }
        return FiguresNow;
    }

    // A lobby figure that shows the game's own person now (no character worn on it), for the picker's picture of the original look; null
    // when every figure wears one or none is drawn.
    internal static Animator? OriginalFigure()
    {
        foreach (var (bar, body, _) in Figures())
            if (!InLobby.ContainsKey(bar)) return body;
        return null;
    }

    // Any lobby figure's body, animated by the game whether it shows its person or wears a character: the stance a character's picture copies.
    internal static Animator? AnyFigure()
    {
        foreach (var (_, body, _) in Figures()) return body;
        return null;
    }

    // The lobby figures put right: each wears the character its player wears (the local pick, a teammate's look), else its own person.
    static void Lobby(float now)
    {
        var seen = new HashSet<IntPtr>();
        foreach (var (bar, body, player) in Figures())
        {
            seen.Add(bar);
            var want = Wanted(player);
            if (InLobby.TryGetValue(bar, out var had))
            {
                if (had.Worn.Skin == want && had.Body.Pointer == body.Pointer && !NativeObjects.Gone(had.Worn.Model) && had.Worn.Hide is not { Active: false }) continue;
                had.Worn.Release();
                InLobby.Remove(bar);
            }
            if (want == null) continue;
            if (!Skins.Ready(new List<Skin> { want }, now)) { _waiting = true; continue; }
            if (Wear(want, body, null, "in the lobby") is not { } worn) { Broken.Add(want); continue; }
            InLobby[bar] = (body, worn);
        }
        foreach (var gone in InLobby.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            InLobby[gone].Worn.Release();
            InLobby.Remove(gone);
        }
    }

    // Who wears what, put right: runs once per change, not per frame.
    static void Reconcile(float now)
    {
        _waiting = false;
        if (_ownChanged && SNet.LocalPlayer is { } local)
        {
            _ownChanged = false;
            LookChanged(local);
        }
        var seen = new HashSet<IntPtr>();
        for (int i = 0; ForgeCore.Players.Readable && i < ForgeCore.Players.Count; i++)
        {
            var agent = ForgeCore.Players.At(i);
            if (agent == null) continue;
            seen.Add(agent.Pointer);
            var want = agent.Owner is { } owner ? Wanted(owner) : null;
            if (ByAgent.TryGetValue(agent.Pointer, out var had))
            {
                var loss = had.Worn.Skin == want ? Loss(had.Worn) : "another character is worn now";
                if (loss == null) continue;
                // Only the first-person arms went (the game built that body again): those alone are worn anew, the model stays.
                if (loss == ArmsLost)
                {
                    had.Worn.Arms!.Destroy();
                    had.Worn.Arms = Arms(had.Worn.Skin, agent, Label + " " + had.Worn.Skin.Doc.Id);
                    ForgeSkinPlugin.Info($"character {had.Worn.Skin.Doc.Id} by {agent.Owner?.NickName}: {loss}, worn on the new ones");
                    continue;
                }
                ForgeSkinPlugin.Info($"character {had.Worn.Skin.Doc.Id} by {agent.Owner?.NickName} taken off: {loss}");
                had.Worn.Release();
                ByAgent.Remove(agent.Pointer);
            }
            if (want == null) continue;
            if (!Skins.Ready(new List<Skin> { want }, now) || agent.AnimatorBody == null) { _waiting = true; continue; }
            if (Wear(want, agent.AnimatorBody, agent, $"by {agent.Owner?.NickName}") is not { } worn) { Broken.Add(want); continue; }
            ByAgent[agent.Pointer] = (agent, worn);
        }
        foreach (var gone in ByAgent.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            ByAgent[gone].Worn.Release();
            ByAgent.Remove(gone);
        }
        Lobby(now);
    }

    // The character skin `player` wears: their own pick, a teammate's look, none for a bot (null: the original person).
    static Skin? Wanted(SNet_Player player)
    {
        if (player.IsBot) return null;
        var skin = player.IsLocal ? Skins.Own(CharacterSkin.Target) : Looks.WornBy(player.Lookup, CharacterSkin.Target)?.Skin;
        return skin == null || skin.Retired || Broken.Contains(skin) ? null : skin;
    }

    // `skin`'s model on `body`, following its pose, the body's own person hidden (a player's through Persons, a stand-in's or a lobby
    // figure's meshes directly). Null, with one line, when it does not fit: nothing is left half done. `where` is for the log line.
    static Worn? Wear(Skin skin, Animator body, PlayerAgent? agent, string where)
    {
        var character = skin.Doc.Character!;
        var worn = new Worn { Skin = skin, Root = new GameObject(Label + " " + skin.Doc.Id) };
        try
        {
            var root = worn.Root.transform;
            PlayerLooks.Hang(worn.Root, body);
            // The model stands as tall as the body (feet to head, both measured on their bones at rest), times the author's height.
            var size = Fit(skin, body);
            root.localScale = Vector3.one * (size / body.transform.lossyScale.x);
            worn.Model = skin.Character!.Spawn(root, Label);
            var renderer = worn.Model.GetComponent<SkinnedMeshRenderer>();
            // Drawn as the body it replaces is (the local player's third-person body only casts the shadow): a mesh shown now, not one of
            // the inactive spares the lobby's figure keeps for the other characters (on a layer its camera does not draw).
            var original = body.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.gameObject.activeInHierarchy && !r.transform.IsChildOf(root));
            if (original != null)
            {
                worn.Model.layer = original.gameObject.layer;
                renderer.shadowCastingMode = original.shadowCastingMode;
            }
            var label = Label + " " + skin.Doc.Id;
            var target = Models.ReadPoseTarget(worn.Model.transform, renderer, skin.Links!, label) ?? throw new InvalidOperationException("the model's skeleton does not take the pose (ForgeCore logged why)");
            var bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var bone in renderer.bones) if (bone != null) bones.TryAdd(bone.name, bone);
            // Bound at rest, before the first pose copy: each chain's shape is the model's own.
            // The chains step in the world: their radii and gravity, given in the model's metres, grow with the model's world size.
            var capsules = new List<ChainCapsule>();
            foreach (var k in character.Springs.Capsules)
                if (target.Bone(Array.IndexOf(ClipRig.Names, k.Joint)) is { } on)
                    capsules.Add(new ChainCapsule(on, new Vector3(k.A[0], k.A[1], k.A[2]), new Vector3(k.B[0], k.B[1], k.B[2]), k.Radius * size));
            foreach (var chain in character.Springs.Chains)
            {
                var spec = chain.Spec();
                spec.Radius *= size;
                spec.Gravity *= size;
                worn.Chains.Add(Chains.Bind(bones[chain.Anchor], chain.Strands.Select(s => s.Select(n => bones[n]).ToArray()).ToList(), spec, capsules,
                    chain.LinkSide, chain.Loop, agent != null ? agent.transform : body.transform, renderer));
            }
            worn.Copy = Models.CopyPose(body, target, HumanRig.ArmReach, label) ?? throw new InvalidOperationException("the body's pose cannot be copied (ForgeCore logged why)");
            worn.Copy.Follow();
            // The hung model is not the person: Persons.Hide leaves it drawn, and what the person holds; a recording takes this look from PlayerLooks (Look).
            var hide = new PersonHideOptions { Held = false, Look = true };
            worn.Hide = agent != null ? Persons.Hide(agent, Label, hide) : Persons.Hide(body, Label, hide);
            renderer.enabled = true; // whatever the game's own switch did to the person's renderers
            if (character.Materials.Exists(m => m.Shader == CharacterMaterial.Toon))
            {
                // The renderer's slots are the descriptor's submeshes; each draws its material's CharacterMaterial.
                var submeshes = skin.Character!.Descriptor.Submeshes;
                var toon = new bool[submeshes.Count];
                var values = new IReadOnlyDictionary<string, float[]>?[submeshes.Count];
                for (int i = 0; i < toon.Length; i++)
                    if (submeshes[i].Material < character.Materials.Count && character.Materials[submeshes[i].Material] is { Shader: CharacterMaterial.Toon } m)
                        (toon[i], values[i]) = (true, m.Params);
                worn.Toon = ToonDress.Wear(renderer, toon, values);
                ShowToon(worn, label);
            }
            if (agent != null && agent.Owner is { IsLocal: true })
            {
                worn.Arms = Arms(skin, agent, label);
                // As the game draws its own body in first person: no head (its hair with it) and no arms but the first-person ones.
                var leftOut = new[] { "head", "leftUpperArm", "rightUpperArm" }.Select(j => target.Bone(Array.IndexOf(ClipRig.Names, j))).OfType<Transform>().ToList();
                worn.Cut = Models.LeaveOutInFirstPerson(renderer, leftOut);
            }
            ForgeSkinPlugin.Info($"character {skin.Doc.Id} worn {where}:{target.Links.Count} links, {worn.Chains.Count} spring chains, scale {worn.Copy.Scale:0.###} (height {character.Height:0.###}){(worn.Arms != null ? ", own first-person arms" : "")}");
            return worn;
        }
        catch (Exception e)
        {
            worn.Release();
            ForgeSkinPlugin.Fail("skin-character-failed", $"character {skin.Doc.Id}: {e.Message}; the original person shows");
            return null;
        }
    }

    // World units per model metre for `skin` on `body`: the body's height at rest (its humanoid head over its feet, from its bind
    // poses: the game's own player model, whatever its scale) over the model's (CharacterRig.Stature), times the author's Height. A body
    // or model whose height cannot be read keeps the model at its own size (the body's own scale taken off), times Height, one line.
    static float Fit(Skin skin, Animator body)
    {
        var height = skin.Doc.Character!.Height;
        var model = CharacterRig.Stature(skin.Doc.Character, skin.Character!.Descriptor);
        var rest = SkinRebind.BindRest(body.transform);
        float? Y(HumanBodyBones bone) => body.GetBoneTransform(bone) is { } t && rest.TryGetValue(t.Pointer, out var m) ? m.m13 : null;
        if (model > 0 && Y(HumanBodyBones.Head) is { } head && Y(HumanBodyBones.LeftFoot) is { } left && Y(HumanBodyBones.RightFoot) is { } right && head - (left + right) / 2 > 0)
            return (head - (left + right) / 2) * body.transform.lossyScale.y / model * height;
        ForgeSkinPlugin.Warn($"character {skin.Doc.Id}: the {(model > 0 ? "body's" : "model's")} height cannot be measured; the model shows at its own size");
        var own = body.transform.lossyScale.x / Math.Max(1e-4f, body.transform.localScale.x);
        return own * height;
    }

    // The local player's own arms in first person: a second instance of the model cut to its arms, worn on the game's first-person arms
    // (ForgeCore Models.WearFirstPerson), the game's arms hidden. Its hands and fingers are the game's, so they hold whatever is held at
    // any proportions; the materials in `hide` are left out. Null (ForgeCore logged why): the game's own arms stay.
    static FirstPersonWear? Arms(Skin skin, PlayerAgent agent, string label)
    {
        var character = skin.Doc.Character!;
        var model = skin.Character!;
        if (character.Arms?.Model != null) ForgeSkinPlugin.Warn($"{skin.Doc.Id}: a separate first-person arms model is not read yet; the model's own arms are worn");
        var leaveOut = new List<int>();
        for (int i = 0; i < model.Descriptor.Submeshes.Count; i++)
            if (character.Hide?.Contains(model.Descriptor.Submeshes[i].Material) == true) leaveOut.Add(i);
        var arms = Models.WearFirstPerson(agent, model.Spawn(null, label + " arms"), new FirstPersonWearOptions
        {
            Bones = HumanRig.ArmWear(CharacterRig.Joints(character)),
            Hide = BodyParts.Arms,
            Descendants = true,
            LeaveOut = leaveOut,
            Label = label + " arms",
        });
        arms?.Activate();
        return arms;
    }

    // The player changed the toon setting: everyone wearing a character is drawn the new way at once.
    public static void ToonChanged()
    {
        foreach (var (_, worn) in ByAgent.Values) ShowToon(worn, Label + " " + worn.Skin.Doc.Id);
        foreach (var (_, worn) in InLobby.Values) ShowToon(worn, Label + " " + worn.Skin.Doc.Id + " in the lobby");
        foreach (var dressed in StandIns) if (!dressed.Released && dressed.Person is { } person) ShowToon(person, Label + " " + person.Skin.Doc.Id + " on a stand-in");
    }

    /// <summary>The toon setting by name (forward, deferred, off), written as the picker's choice writes it: for
    /// <c>drive_cli.py call static:ForgeSkin.Plugin.Characters Toon deferred</c>.</summary>
    internal static string Toon(string mode)
    {
        if (!Enum.TryParse<ToonMode>(mode, true, out var value)) return "toon: forward, deferred or off";
        ForgeSkinPlugin.Toon.Value = value;
        return "toon " + value;
    }

    static void ShowToon(Worn worn, string label)
    {
        if (worn.Released || worn.Toon is not { } toon) return;
        ToonLighting? lighting = ForgeSkinPlugin.Toon.Value switch { ToonMode.Forward => ToonLighting.Forward, ToonMode.Deferred => ToonLighting.Deferred, _ => null };
        if (!toon.Set(lighting)) ForgeSkinPlugin.Fail("skin-character-failed", $"{label}: ForgeCore's toon shader is not loaded; its toon materials draw lit");
        else if (toon.Unknown.Count > 0) ForgeSkinPlugin.Warn($"{label}: toon parameters the shader does not have: {string.Join(", ", toon.Unknown)}");
    }

    // ForgeCore's player looks: what a player looks like is their shared look (Looks: character and gear skins), and that look on another
    // body is resolved against the packs installed here as a teammate's is. A look whose skins are still being read or loaded is not worn
    // yet (null: the caller asks again; the load has started); once it can be, the dress holds what fits (possibly nothing: the body stays
    // the game's), so a dress is never half of a look that would have fitted a moment later.
    sealed class Provider : IPlayerLookProvider
    {
        public byte[]? Describe(SNet_Player player) => player.IsBot ? null : Looks.Describe(player);

        public ILookDress? Dress(byte[] description, Animator body)
        {
            if (Look.Decode(description) is not { } look || Pending(look)) return null;
            var dressed = new Dressed();
            Looks.Resolve(look, "a stand-in", dressed.Gear, new());
            if (dressed.Gear.Remove(CharacterSkin.Target, out var person) && person.Skin is var skin && skin.Doc.Character != null && !Broken.Contains(skin) && skin.Character != null)
                dressed.Person = Wear(skin, body, null, "on a stand-in");
            if (dressed.Person != null) StandIns.Add(dressed);
            return dressed;
        }

        // A skin of the look installed here whose content is still being read (Looks.Resolve leaves it out until then), or the look's
        // character not loaded yet (asking starts its load).
        static bool Pending(Look look)
        {
            foreach (var gear in look.Gear)
                if (Skins.ByNumber.GetValueOrDefault(gear.Skin) is { } skin && skin.ContentHash() == null) return true;
            if (look.Character is not { } person || Skins.ByNumber.GetValueOrDefault(person.Skin) is not { } character) return false;
            if (character.ContentHash() == null) return true;
            return character.Doc.Character != null && !Broken.Contains(character) && !Skins.Ready(new List<Skin> { character }, Frame.UnscaledTime);
        }
    }
}
