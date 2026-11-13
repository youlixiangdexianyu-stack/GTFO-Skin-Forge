using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// HUD elements of every mod: one root per registration, a child of the game's weapon list (<c>GuiManager.PlayerLayer.Inventory</c>,
/// a <c>PUI_Inventory</c>), so it is drawn by the same camera, on the same layer, with the same tilt, sway and visibility as the game's
/// own HUD. Texts are copies of the weapon list's item name (<c>PUI_InventoryItem.m_slim_archetypeName</c>: font with the game's CJK
/// fallbacks, material, sorting) and sprites use the weapon list's sprite material and sorting.
/// Nothing is laid out before the screen is settled (<see cref="HudScreen"/>: the median of a few measurements, kept until the resolution,
/// field of view or HUD scale changes). Twice a second the game's weapon list (in its own space) and everything the vitals draw (health bar,
/// pulse row and value, health, infection and shield; projected through the HUD camera onto the list's plane, since they stand on a plane of
/// their own) are measured as occupied; a box moves only when an edge moved by more than <see cref="HudBoxes.Tolerance"/>, so the game's sway
/// moves nothing. <see cref="HudLayout"/> then places every shown element in its region: stacked by priority, clear of each other and of the
/// game's HUD, the lowest priority ones collapsed when a region is full. An element's root is set only when its place changed.
/// </summary>
internal static class Hud
{
    internal const float Gap = HudRegions.Gap;
    private const float Rescan = 0.5f, RendererRescan = 2f;

    private static readonly Registry<HudElement> Registered = new();
    private static readonly HudLayout Layout = new();
    private static PUI_Inventory? _list;
    private static TextMeshPro? _text;
    private static Material? _material;
    private static int _layer, _sortingLayer, _order;
    private static Vector3 _textScale;
    private static bool _healthDrawn;

    private sealed class Anchor
    {
        internal Renderer Renderer = null!;
        internal Bounds Bounds;
        internal bool Visible;
    }

    private static readonly List<Anchor> Anchors = new();
    private static Matrix4x4 _matrix;
    private static float _nextScan;
    private static HudBox? _weaponList, _vitals;
    private static float _barCentre, _barWidth;

    private static float _lastTick;
    private static HudScene _scene;

    internal static PUI_Inventory? List => _list;
    internal static float FontSize => _text != null ? _text.fontSize : 0f;

    /// <summary>The HUD texts' own font and material (the weapon list's item name), read when the HUD is found.</summary>
    internal static TMP_FontAsset? GameFace { get; private set; }
    internal static Material? GameMaterial { get; private set; }

    private static UIKitFonts.Set? _kitFaces;
    private static bool _kitAsked;

    /// <summary>The kit's faces falling back to the HUD font, made on first use; null when the font bundle is missing or failed.</summary>
    internal static UIKitFonts.Set? KitFaces
    {
        get
        {
            if (_kitAsked) return _kitFaces;
            _kitAsked = true;
            if (GameFace != null && GameMaterial != null) _kitFaces = UIKitFonts.For(GameFace, GameMaterial);
            return _kitFaces;
        }
    }

    /// <summary>Changes whenever the settled screen does.</summary>
    internal static int ScreenVersion => HudScreen.Version;

    internal static IReadOnlyList<HudElement> Elements => Registered.Ordered;

    /// <summary>While set, only this mod's elements show (<see cref="UIKitMod.HudAlone"/>).</summary>
    internal static string? AloneMod;

    /// <summary>The screen and the game's HUD as measured this frame (the weapon list's space).</summary>
    internal static HudScene Scene => _scene;

    internal static HudElement? Add(HudElement element)
    {
        var problem = Registered.Add(element.Id, element);
        if (problem == null)
        {
            element.IsRegistered = true;
            Layout.Add(element.Entry);
            return element;
        }
        Plugin.Error("ui-hud-element-not-added", "HUD element not added: " + problem + ".");
        return null;
    }

    internal static T? Add<T>(string id, T widget, HudSlot slot) where T : HudWidget
    {
        var element = Add(new HudElement(id, widget, slot));
        if (element == null) return null;
        widget.Element = element;
        element.Font = widget.DefaultFont;
        return widget;
    }

    internal static void Remove(HudElement element)
    {
        if (!Registered.Remove(element.Id, element)) return;
        element.IsRegistered = false;
        Layout.Remove(element.Entry);
        element.Discard();
        // The text caches are keyed by native pointers: a new object may land on a freed one's address, so every entry is relearned once.
        HudDraw.Forget();
    }

    /// <summary>Changes an element's slot (its priority or declared size); the layout follows from the next frame.</summary>
    internal static void SetSlot(HudElement element, HudSlot slot)
    {
        var priority = element.Entry.Slot.Priority;
        element.Entry.Slot = slot;
        if (slot.Priority != priority) Layout.Reorder();
    }

    /// <summary>Per frame: builds elements for a new HUD, lays out those that are shown and places them.</summary>
    internal static void Update()
    {
        var elements = Registered.Ordered;
        if (elements.Count == 0) return;
        var now = ForgeCore.Frame.GameTime;
        try
        {
            // A game screen (map, terminal, menu) is open: nothing of the kit shows over it.
            if (HudFocus.Away) { HideAll(elements); return; }
            var layer = GuiManager.PlayerLayer;
            var list = layer != null ? layer.Inventory : null;
            if (list == null || list.WasCollected) { HideAll(elements); return; }
            if ((_list == null || _list.WasCollected || _list.Pointer != list.Pointer) && !Capture(list, now)) { HideAll(elements); return; }
            var status = layer!.m_playerStatus;
            if (status != null && status.WasCollected) status = null;
            var any = false;
            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                if (!element.IsRegistered) continue;
                if (!element.BuiltFor(list)) Build(element, list);
                // The build callback may remove this or any registration.
                if (!element.IsRegistered) continue;
                element.Prepare(now);
                any |= element.Visible && !element.Faulted && element.RootObject != null;
            }
            // Nothing to show (for example in the lobby): nothing is measured.
            if (!any) { HideAll(elements); return; }
            var dt = Math.Clamp(now - _lastTick, 0f, 0.1f);
            _lastTick = now;
            // Nothing is laid out on an unsettled screen: elements wait hidden.
            if (!HudScreen.Measure(list)) { HideAll(elements); return; }
            // The health bar showing or hiding is followed at once; everything else twice a second.
            var healthDrawn = status != null && HealthDrawn(status);
            if (healthDrawn != _healthDrawn) { _healthDrawn = healthDrawn; _nextScan = 0f; }
            if (now >= _nextScan)
            {
                _nextScan = now + Rescan;
                var hidden = GameHud.Hidden;
                // A part a mod hides keeps its last measured box: it anchors its region but is no longer occupied.
                if ((hidden & GameHudPart.WeaponList) == 0) _weaponList = HudBoxes.Settle(_weaponList, Locate(list, now));
                if ((hidden & GameHudPart.Vitals) == 0) _vitals = HudBoxes.Settle(_vitals, status != null ? MeasureVitals(status, now) : null);
            }
            _scene = new HudScene
            {
                Screen = new HudBox(HudScreen.CentreX - HudScreen.HalfWidth, HudScreen.CentreY - HudScreen.HalfHeight,
                    HudScreen.CentreX + HudScreen.HalfWidth, HudScreen.CentreY + HudScreen.HalfHeight),
                Margin = Plugin.HudSafeMargin,
                OffsetX = Plugin.HudOffsetX,
                OffsetY = Plugin.HudOffsetY,
                WeaponList = _weaponList,
                Vitals = _vitals,
                BarCentre = _barCentre,
                WeaponListFree = (GameHud.Hidden & GameHudPart.WeaponList) != 0,
                VitalsFree = (GameHud.Hidden & GameHudPart.Vitals) != 0,
            };
            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                // The pointer is the library's own cursor for whichever mod holds it: a mod that has the screen alone still needs it drawn.
                var live = element.IsRegistered && element.Visible && !element.Faulted && element.RootObject != null && (AloneMod == null || element.Mod == AloneMod || HudPointer.Draws(element));
                element.Entry.Visible = live;
                if (!live) continue;
                element.MeasureDrawn(now);
            }
            Layout.Run(_scene);
            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                if (!element.IsRegistered) continue;
                var entry = element.Entry;
                var root = element.RootObject;
                var show = root != null && entry.State is LayoutState.Placed or LayoutState.Free;
                if (root != null && root.activeSelf != show) root.SetActive(show);
                if (!show) continue;
                element.HalfWidth = HudScreen.HalfWidth;
                element.HalfHeight = HudScreen.HalfHeight;
                element.BarWidth = _barWidth;
                var position = new Vector3(entry.RootX, entry.RootY, 0f);
                if (root!.transform.localPosition != position) root.transform.localPosition = position;
                element.Tick(now, dt);
            }
        }
        catch (Exception error) { Plugin.Error("ui-hud-elements-failed-update", "HUD elements failed to update: " + error); }
    }

    internal static string N(float n) => HudBox.N(n);

    /// <summary>The renderers under the game's player status (everything the vitals draw), collected when the status object changes and
    /// every <see cref="RendererRescan"/> seconds (texts and bars come and go).</summary>
    private static readonly List<Renderer> VitalRenderers = new();
    private static IntPtr _vitalsFor;
    private static float _nextVitalScan;

    private static bool HealthDrawn(PUI_LocalPlayerStatus status) => Drawn(status.m_health1) || Drawn(status.m_health2);

    private static bool Drawn(SpriteRenderer? sprite) => sprite != null && !sprite.WasCollected && sprite.enabled && sprite.gameObject.activeInHierarchy;

    /// <summary>Measures the game's vitals on the weapon list's plane as the HUD camera shows them: the health bar from its two sprites (its
    /// centre and width), and every renderer of the player status that draws (the bar, the PULSE row and its value, health, infection,
    /// shield) as the occupied box. Null while the health sprites are not drawn.</summary>
    private static HudBox? MeasureVitals(PUI_LocalPlayerStatus status, float now)
    {
        float left = float.MaxValue, bottom = float.MaxValue, right = float.MinValue, top = float.MinValue;
        var any = false;
        for (var i = 0; i < 2; i++)
        {
            var sprite = i == 0 ? status.m_health1 : status.m_health2;
            if (!Drawn(sprite)) continue;
            any |= HudScreen.Project(sprite!.bounds, ref left, ref bottom, ref right, ref top);
        }
        if (!any || right <= left) return null;
        _barCentre = (left + right) / 2f;
        _barWidth = right - left;
        if (_vitalsFor != status.Pointer || now >= _nextVitalScan)
        {
            _vitalsFor = status.Pointer;
            _nextVitalScan = now + RendererRescan;
            VitalRenderers.Clear();
            foreach (var renderer in status.GetComponentsInChildren<Renderer>(false)) VitalRenderers.Add(renderer);
        }
        foreach (var renderer in VitalRenderers)
        {
            if (renderer == null || renderer.WasCollected || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            float l = float.MaxValue, bo = float.MaxValue, r = float.MinValue, t = float.MinValue;
            if (!HudScreen.Project(renderer.bounds, ref l, ref bo, ref r, ref t)) continue;
            left = Math.Min(left, l); right = Math.Max(right, r); bottom = Math.Min(bottom, bo); top = Math.Max(top, t);
        }
        return new HudBox(left, bottom, right, top);
    }

    /// <summary>Widens the box to the corners of world <paramref name="bounds"/> in the space <paramref name="m"/> maps into. The interop
    /// matrix methods call IL2CPP; the copied fields are multiplied here. Empty bounds add nothing.</summary>
    internal static void Extend(Bounds bounds, Matrix4x4 m, ref float left, ref float bottom, ref float right, ref float top)
    {
        var min = bounds.min;
        var max = bounds.max;
        if (max.x - min.x <= 1e-6f && max.y - min.y <= 1e-6f) return;
        for (var corner = 0; corner < 8; corner++)
        {
            var wx = (corner & 1) == 0 ? min.x : max.x;
            var wy = (corner & 2) == 0 ? min.y : max.y;
            var wz = (corner & 4) == 0 ? min.z : max.z;
            var lx = m.m00 * wx + m.m01 * wy + m.m02 * wz + m.m03;
            var ly = m.m10 * wx + m.m11 * wy + m.m12 * wz + m.m13;
            left = Math.Min(left, lx);
            right = Math.Max(right, lx);
            bottom = Math.Min(bottom, ly);
            top = Math.Max(top, ly);
        }
    }

    private static void HideAll(IReadOnlyList<HudElement> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            var root = elements[i].RootObject;
            if (root != null && root.activeSelf) root.SetActive(false);
        }
    }

    /// <summary>Takes the text and sprite settings from the game's weapon list. False while it has neither yet.</summary>
    private static bool Capture(PUI_Inventory list, float now)
    {
        TextMeshPro? text = null;
        SpriteRenderer? sprite = null;
        foreach (var pair in list.m_inventorySlots)
        {
            var item = pair.Value;
            if (item == null) continue;
            if (text == null && item.m_slim_archetypeName != null) text = item.m_slim_archetypeName;
            sprite ??= item.GetComponentInChildren<SpriteRenderer>(true);
        }
        if (text == null || sprite == null)
        {
            return false;
        }
        foreach (var element in Registered.Ordered) element.Discard();
        HudDraw.Forget();
        HudScreen.Reset();
        _list = list;
        _text = text;
        GameFace = text.font;
        GameMaterial = text.fontSharedMaterial;
        _kitFaces = null;
        _kitAsked = false;
        _layer = text.gameObject.layer;
        var textRenderer = text.GetComponent<MeshRenderer>();
        _sortingLayer = textRenderer != null ? textRenderer.sortingLayerID : sprite.sortingLayerID;
        _order = textRenderer != null ? textRenderer.sortingOrder : sprite.sortingOrder;
        _material = sprite.sharedMaterial;
        var parentScale = list.transform.lossyScale;
        var textScale = text.transform.lossyScale;
        _textScale = new Vector3(textScale.x / parentScale.x, textScale.y / parentScale.y, textScale.z / parentScale.z);
        Anchors.Clear();
        _nextScan = 0f;
        _weaponList = _vitals = null;
        _vitalsFor = IntPtr.Zero;
        _healthDrawn = false;
        return true;
    }

    private static void Build(HudElement element, PUI_Inventory list)
    {
        element.Discard();
        HudDraw.Forget();
        var go = new GameObject("ForgeUI.Hud." + element.Id) { layer = _layer };
        go.transform.SetParent(list.transform, false);
        GameHud.Own(go);
        go.SetActive(false);
        element.Attach(go, list);
        try { element.BuildCallback(element); }
        catch (Exception error)
        {
            Plugin.Error("ui-hud-element-failed-build-stays-off", "HUD element '" + element.Id + "' failed to build and stays off on this HUD: " + error);
            element.Fail(list);
            return;
        }
    }

    // ---- pieces --------------------------------------------------------------------------------------------

    /// <summary>A copy of the weapon list's item name under <paramref name="parent"/>: same font, size and look, empty, left-aligned
    /// with its pivot on its left edge (<see cref="HudCanvas"/> places right and centred texts by their measured width), one line,
    /// 600 × 40 HUD units.</summary>
    internal static TextMeshPro Text(Transform parent)
    {
        var source = _text ?? throw new InvalidOperationException("the HUD has no text to copy");
        var go = Object.Instantiate(source.gameObject, parent);
        go.name = "Text";
        NativeUi.StripLocalization(go);
        var text = go.GetComponent<TextMeshPro>() ?? throw new InvalidOperationException("the HUD text copy has no TextMeshPro");
        StripExtraLayers(go, text);
        text.richText = true;
        text.enableAutoSizing = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.SetText("");
        text.enabled = true;
        // The copy takes the source's renderer switch, which is off while the game's weapon list is hidden (a cockpit HUD), so a
        // text made then would never draw.
        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.enabled = true;
        if (!go.activeSelf) go.SetActive(true);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localPosition = Vector3.zero;
        go.transform.localScale = _textScale;
        text.rectTransform.pivot = new Vector2(0f, 0.5f);
        text.rectTransform.sizeDelta = new Vector2(600f / _textScale.x, 40f / _textScale.y);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    /// <summary>The game's text objects can carry a second text object as a child (an offset, faded copy of the label). A copy is
    /// set through its own text component only, so every such layer would stay behind as a stale duplicate of the label; they are
    /// removed from the copy at once (not at the end of the frame, so nothing is drawn from them even once). (The font material's own
    /// shadow, if any, is the game's look and stays.)</summary>
    internal static void StripExtraLayers(GameObject copy, TextMeshPro keep)
    {
        for (var i = copy.transform.childCount - 1; i >= 0; i--)
        {
            var child = copy.transform.GetChild(i).gameObject;
            child.SetActive(false);
            Object.DestroyImmediate(child);
        }
        // The game's text object can carry scripts that make the shadow or ghost layer themselves (a child they create when they wake,
        // or a second pass they drive from the text), so every behaviour but the text itself goes before any of them runs.
        foreach (var behaviour in copy.GetComponents<Behaviour>())
        {
            if (behaviour == null || behaviour.Pointer == keep.Pointer) continue;
            behaviour.enabled = false;
            Object.DestroyImmediate(behaviour);
        }
    }

    /// <summary>A sprite under <paramref name="parent"/> drawn like the weapon list's: its material, layer and sorting, with
    /// <paramref name="order"/> added to the sorting order (negative goes behind the texts).</summary>
    internal static SpriteRenderer Sprite(Transform parent, Sprite sprite, int order)
    {
        var go = new GameObject("Sprite") { layer = _layer };
        go.transform.SetParent(parent, false);
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        if (_material != null) renderer.sharedMaterial = _material;
        renderer.sortingLayerID = _sortingLayer;
        renderer.sortingOrder = _order + order;
        return renderer;
    }

    internal static bool HasGlyph(char c)
    {
        var font = _text != null ? _text.font : null;
        return font != null && font.HasCharacter(c, true, false);
    }

    // ---- measuring the weapon list -------------------------------------------------------------------------

    /// <summary>The box of everything the weapon list draws, in the list's own space (its pieces are its children, so the list's sway does
    /// not move them in it). The list's renderers are collected twice a second.</summary>
    private static HudBox? Locate(PUI_Inventory list, float now)
    {
        var count = 0;
        foreach (var pair in list.m_inventorySlots)
        {
            if (pair.Value == null) continue;
            foreach (var renderer in pair.Value.GetComponentsInChildren<Renderer>(true))
            {
                if (count == Anchors.Count) Anchors.Add(new Anchor { Renderer = renderer });
                else if (Anchors[count].Renderer.Pointer != renderer.Pointer) Anchors[count] = new Anchor { Renderer = renderer };
                count++;
            }
        }
        if (count != Anchors.Count) Anchors.RemoveRange(count, Anchors.Count - count);
        _matrix = list.transform.worldToLocalMatrix;
        float left = float.MaxValue, bottom = float.MaxValue, right = float.MinValue, top = float.MinValue;
        foreach (var anchor in Anchors)
        {
            var renderer = anchor.Renderer;
            anchor.Visible = renderer != null && !renderer.WasCollected && renderer.enabled && renderer.gameObject.activeInHierarchy;
            if (!anchor.Visible) continue;
            anchor.Bounds = renderer!.bounds;
            Extend(anchor.Bounds, _matrix, ref left, ref bottom, ref right, ref top);
        }
        return right > left ? new HudBox(left, bottom, right, top) : null;
    }

    /// <summary>The weapon list's space (world to list), as of the last measure.</summary>
    internal static Matrix4x4 ListSpace => _list != null ? _list.transform.worldToLocalMatrix : _matrix;
}

/// <summary>
/// A registered HUD element. Its pieces live under <see cref="Root"/>, which the library places in the element's region (as a child of
/// the game's weapon list) and rebuilds (calling your build again) whenever the game creates a new HUD. <see cref="Remove"/> takes it off.
/// </summary>
public sealed class HudElement
{
    private const float MeasureEvery = 0.25f;
    private GameObject? _root;
    private PUI_Inventory? _builtFor, _failedFor;
    private float _nextDrawn;

    internal HudElement(string id, Action<HudElement> build, HudSlot slot)
    {
        Id = id;
        (Mod, Name) = ModIds.Split(id);
        BuildCallback = build;
        Entry = new LayoutEntry(id, slot);
    }

    internal HudElement(string id, HudWidget widget, HudSlot slot) : this(id, widget.Build, slot)
    {
        Widget = widget;
    }

    /// <summary>The full ID, <c>mod/name</c>.</summary>
    public string Id { get; }

    /// <summary>The mod that registered it.</summary>
    public string Mod { get; }

    /// <summary>Its name inside the mod.</summary>
    public string Name { get; }

    private UITexts? _texts;

    /// <summary>The registering mod's language files: the text keys this element shows are looked up there.</summary>
    internal UITexts Texts => _texts ??= UIKit.Mod(Mod).Texts;

    /// <summary>What <paramref name="text"/> shows now, a key looked up in the registering mod's language files.</summary>
    internal string Show(UIText? text) => text == null ? "" : text.In(Texts);

    internal LayoutEntry Entry { get; }

    /// <summary>Whether it is in the registry now; a flag the per-frame pass reads instead of looking the ID up.</summary>
    internal bool IsRegistered { get; set; }

    /// <summary>The face a <see cref="HudCanvas"/> on this element draws its texts in unless the canvas sets its own
    /// (<see cref="UIKitFont.Game"/> by default).</summary>
    public UIKitFont Font { get; set; }

    /// <summary>Whether a <see cref="HudCanvas"/> on this element draws the shared glow and colour fringe
    /// (<see cref="UIKit.HudGlow"/>, <see cref="UIKit.HudFringe"/>) round its lit pieces. Default true.</summary>
    public bool Effects { get; set; } = true;

    /// <summary>Perspective of a side element: its outer side (the one towards the screen edge it grows from) is drawn this share
    /// taller than its inner side, as if the panel were turned towards the viewer (0.035 reads as a cockpit side pane). Heights and
    /// vertical positions are stretched from the element's anchor; texts keep their size. No effect on a horizontally centred element.
    /// Default 0.</summary>
    public float Tilt { get; set; }

    /// <summary>Its region, priority and declared size.</summary>
    public HudSlot Slot => Entry.Slot;

    public HudRegion Region => Entry.Slot.Region;

    /// <summary>Which way to draw from the root, on each axis: -1 left (or down), 1 right (or up), 0 centred on it.</summary>
    public int FlowX => HudRegions.Flow(Region).X;

    /// <summary>See <see cref="FlowX"/>.</summary>
    public int FlowY => HudRegions.Flow(Region).Y;

    /// <summary>Declares a new expected size (HUD units); the region is laid out again from the next frame.</summary>
    public void Resize(float width, float height) => Hud.SetSlot(this, Entry.Slot.With(Entry.Slot.Priority, width, height));

    /// <summary>Where the last layout put it: placed, collapsed, free, hidden or waiting for its region.</summary>
    public LayoutState State => Entry.State;

    /// <summary>The box it was laid out in, in the weapon list's space, while placed.</summary>
    public HudBox Box => Entry.Box;

    /// <summary>Half the screen's width and height in HUD units, as settled (0 until the screen has been measured).
    /// The screen runs from -<c>HalfWidth</c> to <c>HalfWidth</c> around its middle.</summary>
    public float HalfWidth { get; internal set; }

    /// <summary>See <see cref="HalfWidth"/>.</summary>
    public float HalfHeight { get; internal set; }

    /// <summary>The game's health bar's width in HUD units, as of the last frame (0 while it is not drawn): what a
    /// <see cref="HudRegion.Vitals"/> element lines up with.</summary>
    public float BarWidth { get; internal set; }

    /// <summary>Changes whenever the settled screen does (resolution, field of view or HUD scale); lay out again when it differs from the value you last laid out for.</summary>
    public int ScreenVersion => Hud.ScreenVersion;

    /// <summary>Where a point in the world appears on the screen, in HUD units from the middle of the screen (the root of a
    /// <see cref="HudRegion.World"/> element). <paramref name="behind"/> is true when the point is behind the camera (the position is
    /// then mirrored; see <see cref="ScreenMath.EdgeMarker"/>). False while there is no world camera or HUD screen.</summary>
    public bool WorldToHud(Vector3 world, out Vector2 point, out bool behind) => HudScreen.WorldToHud(this, world, out point, out behind);

    internal HudWidget? Widget { get; set; }

    /// <summary>Counts the times this element's pieces were built (the game's HUD was created); a piece pool made for an older one is void.</summary>
    internal int Generation { get; private set; }

    /// <summary>True once a template's per-frame update threw: it stays off.</summary>
    internal bool Faulted { get; private set; }

    internal void Prepare(float now)
    {
        if (Widget == null || Faulted) return;
        try { Widget.Prepare(this, now); }
        catch (Exception error) { Fault(error); }
    }

    internal void Tick(float now, float dt)
    {
        if (Widget == null) return;
        try { Widget.Tick(this, now, dt); }
        catch (Exception error) { Fault(error); }
    }

    private void Fault(Exception error)
    {
        Faulted = true;
        Visible = false;
        Plugin.Error("ui-hud-element-failed-while-updating-stays", "HUD element '" + Id + "' failed while updating and stays off: " + error);
    }

    /// <summary>Yours to switch; the element is laid out and shows only while this is true and the game shows its HUD. A hidden element
    /// takes no room.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>The element comes online now (<see cref="HudReveal"/>, <see cref="HudReveal.Seconds"/> long): what a
    /// <see cref="HudCanvas"/> on it draws is traced round, swept in behind a scan line (texts typing on) and locked with a flash. Call it
    /// as you show the element; a cockpit power-on calls it for each part in its own order.</summary>
    public void Reveal() => RevealedAt = ForgeCore.Frame.GameTime;

    /// <summary>When <see cref="Reveal"/> was last called (game clock).</summary>
    internal float RevealedAt = float.NegativeInfinity;

    /// <summary>The element's root: the corner it grows from in its region (see <see cref="FlowX"/>), the middle of the screen for a
    /// <see cref="HudRegion.World"/> element. Units are HUD units (about pixels at 1080p); x grows right and y up. Null before the first
    /// build and after the HUD is gone: use your pieces only while it is set.</summary>
    public Transform? Root => RootObject?.transform;

    /// <summary>The game's weapon list this element is under, while built.</summary>
    public PUI_Inventory? WeaponList => RootObject != null ? _builtFor : null;

    /// <summary>The font size of the weapon list's item names; scale your texts' <c>fontSize</c> from it.</summary>
    public float FontSize => Hud.FontSize;

    /// <summary>The font size for a text <paramref name="px"/> tall in 1080p pixels, the weapon names' 20 px being the reference.</summary>
    public float Px(float px) => Hud.FontSize * px / 20f;

    /// <summary>A copy of the game's HUD text under <paramref name="parent"/> (default <see cref="Root"/>): its left edge at its
    /// position (pivot on the left, middle line), one line, empty. Rich text is allowed.</summary>
    public TextMeshPro AddText(Transform? parent = null) => Hud.Text(Parent(parent));

    /// <summary>A sprite drawn like the game's HUD sprites under <paramref name="parent"/> (default <see cref="Root"/>);
    /// <paramref name="order"/> is added to the HUD's sorting order (negative goes behind texts).</summary>
    public SpriteRenderer AddSprite(Sprite sprite, int order = 0, Transform? parent = null) => Hud.Sprite(Parent(parent), sprite, order);

    /// <summary>Whether the HUD font or its fallbacks can draw <paramref name="c"/> (for example ∞), so you can show a fallback.</summary>
    public bool HasGlyph(char c) => Hud.HasGlyph(c);

    public void Remove() => Hud.Remove(this);

    internal Action<HudElement> BuildCallback { get; }

    internal GameObject? RootObject => _root != null && !_root.WasCollected ? _root : null;

    private Transform Parent(Transform? parent) =>
        parent ?? Root ?? throw new InvalidOperationException("HUD element '" + Id + "' has no HUD yet; add its pieces from its build callback.");

    /// <summary>True when this element was built, or failed to build, for <paramref name="list"/>.</summary>
    internal bool BuiltFor(PUI_Inventory list) =>
        (_failedFor != null && _failedFor.Pointer == list.Pointer) ||
        (RootObject != null && _builtFor != null && _builtFor.Pointer == list.Pointer);

    internal void Attach(GameObject root, PUI_Inventory list)
    {
        _root = root;
        _builtFor = list;
        _failedFor = null;
        Generation++;
        _nextDrawn = 0f;
        _drawnExact = false;
        Entry.DrawnWidth = Entry.DrawnHeight = 0f;
    }

    internal void Fail(PUI_Inventory list)
    {
        Discard();
        _failedFor = list;
    }

    /// <summary>What the element draws, measured from its pieces four times a second while it shows, as its size along its region's
    /// directions from the root; the layout uses the larger of this and the declared size.</summary>
    internal void MeasureDrawn(float now)
    {
        var root = RootObject;
        if (_drawnExact || root == null || !root.activeSelf || now < _nextDrawn) return;
        _nextDrawn = now + MeasureEvery;
        float left = float.MaxValue, bottom = float.MaxValue, right = float.MinValue, top = float.MinValue;
        var matrix = Hud.ListSpace;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled) continue;
            Hud.Extend(renderer.bounds, matrix, ref left, ref bottom, ref right, ref top);
        }
        if (right < left) { Entry.DrawnWidth = Entry.DrawnHeight = 0f; return; }
        var position = root.transform.localPosition;
        // Stepped, so a value growing by a digit does not move the region on every change.
        Entry.DrawnWidth = HudBoxes.Step(Extent(FlowX, left - position.x, right - position.x));
        Entry.DrawnHeight = HudBoxes.Step(Extent(FlowY, bottom - position.y, top - position.y));
    }

    private bool _drawnExact;

    /// <summary>A template that knows exactly what it draws this frame (sizes from its root along its region's directions) reports it
    /// before the layout runs, in place of the stepped measure: the layout, and everything stacked or kept clear of it, then follows its
    /// size in the same frame and as smoothly as it changes.</summary>
    internal void SetDrawn(float width, float height)
    {
        _drawnExact = true;
        Entry.DrawnWidth = Math.Max(0f, width);
        Entry.DrawnHeight = Math.Max(0f, height);
    }

    private static float Extent(int flow, float low, float high) =>
        flow < 0 ? Math.Max(0f, -low) : flow > 0 ? Math.Max(0f, high) : 2f * Math.Max(Math.Max(0f, -low), Math.Max(0f, high));

    internal void Discard()
    {
        if (_root != null && !_root.WasCollected)
        {
            _root.SetActive(false);
            Object.Destroy(_root);
        }
        _root = null;
        _builtFor = null;
    }
}

/// <summary>A ready-made HUD template (<see cref="HudSkillSlot"/>, <see cref="HudKeyHint"/>,
/// <see cref="HudHintLine"/>, <see cref="HudCommandList"/>, <see cref="HudWaypoints"/>). Created by the matching <c>UIKitMod.Add…</c> call, which registers it as a HUD element: its pieces
/// are rebuilt with every HUD, it follows its data every frame it is shown, and it is hidden with the HUD.</summary>
public abstract class HudWidget
{
    internal HudWidget() { }

    internal HudElement Element { get; set; } = null!;

    /// <summary>The full registration ID.</summary>
    public string Id => Element.Id;

    /// <summary>Yours to switch; the template shows only while this is true and the game shows its HUD.</summary>
    public bool Visible
    {
        get => Element.Visible;
        set => Element.Visible = value;
    }

    /// <summary>The HUD element behind it (region, state, size).</summary>
    public HudElement HudElement => Element;

    /// <summary>The template comes online now (<see cref="HudElement.Reveal"/>).</summary>
    public void Reveal() => Element.Reveal();

    /// <summary>Takes it off the HUD.</summary>
    public void Remove() => Element.Remove();

    /// <summary>The face of its texts: the cockpit templates start on <see cref="UIKitFont.Tech"/>, the others on
    /// <see cref="UIKitFont.Game"/>. Can be changed at any time.</summary>
    public UIKitFont Font
    {
        get => Element.Font;
        set => Element.Font = value;
    }

    internal virtual UIKitFont DefaultFont => UIKitFont.Game;

    /// <summary>What <paramref name="text"/> shows now, in the registering mod's language.</summary>
    private protected string Show(UIText? text) => Element.Show(text);

    internal abstract void Build(HudElement hud);

    /// <summary>Every frame before the layout, shown or not (a shared view decides here whether it has anything to show).</summary>
    internal virtual void Prepare(HudElement hud, float now) { }

    internal abstract void Tick(HudElement hud, float now, float dt);
}
