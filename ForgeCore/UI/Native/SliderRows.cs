using System;
using System.Collections.Generic;
using CellMenu;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>The rows of <see cref="SettingsPages.AddAudioSliders"/>: a heading row and one slider row per slider, each a copy of the game's slider
/// row (<c>CM_SettingsItem</c> with its <c>CM_SettingScrollReceiver</c> and <c>CM_ScrollBarHandle</c>). The copy's slider is registered (by
/// instance) before any game code runs for it, so it looks, drags and scrolls like the game's; its handle gets a menu id as the game's
/// setup gives one (<c>CM_PageBase.NextCellItemID</c>). A drag moves the handle and writes the value at once. For the copies alone (decided
/// per instance, the game's own rows run untouched) the game's slider code never runs: their <c>DoScrollDelta</c>, <c>UpdateText</c> and
/// <c>ResetValue</c> and the row's <c>UpdateText</c> are skipped (each a native body of its own in this build).
/// </summary>
internal sealed class SliderRows : PageRows
{
    private sealed class Row
    {
        internal SliderRows Group = null!;
        internal CM_SettingsItem Item = null!;
        internal CM_SettingScrollReceiver? Slider;
        /// <summary>Null for the heading.</summary>
        internal SettingsSlider? Source;
    }

    private const float RefreshInterval = .5f;
    private static readonly Dictionary<IntPtr, Row> BySlider = new();
    private static readonly HashSet<IntPtr> Copies = new();

    private readonly eSettingsSubMenuId _page;
    private readonly eCellSettingID _template;
    private readonly string _headingKey;
    private readonly IReadOnlyList<SettingsSlider> _sliders;
    private readonly Action? _onPageReset;
    private readonly List<Row> _rows = new();
    private CM_ScrollWindow? _window;
    private int _titlesVersion = -1;
    private float _nextRefresh, _draggedAt = -10f;

    internal SliderRows(string id, eSettingsSubMenuId page, eCellSettingID template, string headingKey,
        IReadOnlyList<SettingsSlider> sliders, Action? onPageReset)
    {
        Id = id;
        _page = page;
        _template = template;
        _headingKey = headingKey;

        _sliders = sliders;
        _onPageReset = onPageReset;
    }

    internal string Id { get; }
    internal override PageBuilder Builder => PageBuilder.Submenu;
    internal override eSettingsSubMenuId Page => _page;
    internal override string Owner => "the sliders of " + Id;


    internal override void Update()
    {
        if (_window == null || _window.WasCollected || _rows.Count == 0) return;
        var now = ForgeCore.Frame.UnscaledTime;
        if (now < _nextRefresh) return;
        _nextRefresh = now + RefreshInterval;
        if (!_window.gameObject.activeInHierarchy) return;
        try
        {
            if (_titlesVersion != UITexts.Version) Titles();
            // A value changed elsewhere (a config file, the page reset) shows here; not while the player drags.
            if (now - _draggedAt > RefreshInterval) foreach (var row in _rows) Show(row);
        }
        catch (Exception error) { Plugin.Error("ui-slider-rows-failed-update", "The slider rows of " + Id + " failed to update: " + error); }
    }

    internal override void Append(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content)
    {
        Release();
        CM_SettingsItem? template = null;
        for (var i = 0; i < content.Count; i++)
        {
            var row = content[i]?.TryCast<CM_SettingsItem>();
            var slider = row == null ? null : row.GetComponentInChildren<CM_SettingScrollReceiver>(true);
            if (row == null || slider == null || slider.m_settingId != _template || slider.m_handle == null) continue;
            template = row;
            break;
        }
        if (template == null)
        {
            Plugin.Error("ui-games-settings-page-has-no-slider", "The game's " + _page + " settings page has no " + _template + " slider row to add " + Owner + " to.");
            return;
        }
        _window = window;
        SettingsPages.CopyRows(this, template, "ForgeUI.SliderRows." + Id, content, holder =>
        {
            _rows.Add(Copy(template, holder, null));
            foreach (var slider in _sliders) _rows.Add(Copy(template, holder, slider));
            return _rows.ConvertAll(row => row.Item);
        });
        Titles();
        foreach (var row in _rows) Show(row);
    }

    internal override void PageReset()
    {
        _onPageReset?.Invoke();
        foreach (var row in _rows) Show(row);
    }

    private Row Copy(CM_SettingsItem template, Transform holder, SettingsSlider? source)
    {
        var go = Object.Instantiate(template.gameObject, holder);
        go.name = "ForgeUI.Slider." + Id + "." + (source?.Id ?? "heading");
        // The game's localized-text components would write the game's own title back over ours.
        NativeUi.StripLocalization(go);
        var item = go.GetComponent<CM_SettingsItem>();
        var slider = go.GetComponentInChildren<CM_SettingScrollReceiver>(true);
        var row = new Row { Group = this, Item = item, Slider = source == null ? null : slider, Source = source };
        Copies.Add(item.Pointer);
        if (slider != null)
        {
            BySlider[slider.Pointer] = row;
            // The game gives the slider's handle its menu id in its own setup, which never runs for a copy (the id is not copied either).
            // The page's cursor setup calls SetupCMItem on every cursor item still without an id, and the handle's only throws.
            if (slider.m_handle != null) slider.m_handle.ID = CM_PageBase.NextCellItemID();
        }
        if (source == null)
        {
            if (item.m_inputAlign != null) item.m_inputAlign.gameObject.SetActive(false);
            else if (slider != null) slider.gameObject.SetActive(false);
        }
        return row;
    }

    /// <summary>Takes the rows of an earlier build of the page out of the window's list and destroys them.</summary>
    private void Release()
    {
        if (_window != null && !_window.WasCollected && _window.m_contentItems != null)
        {
            var kept = new Il2CppSystem.Collections.Generic.List<iScrollWindowContent>();
            var items = _window.m_contentItems;
            for (var i = 0; i < items.Count; i++)
                if (items[i] != null && !Copies.Contains(items[i].Pointer)) kept.Add(items[i]);
            if (kept.Count != items.Count) _window.m_contentItems = kept;
        }
        foreach (var row in _rows)
        {
            if (row.Item.WasCollected) continue;
            Copies.Remove(row.Item.Pointer);
            if (row.Slider != null && !row.Slider.WasCollected) BySlider.Remove(row.Slider.Pointer);
            Object.Destroy(row.Item.gameObject);
        }
        _rows.Clear();
        _window = null;
    }

    /// <summary>The handle and the number of a row from its current value.</summary>
    private static void Show(Row row)
    {
        var slider = row.Slider;
        if (slider == null || slider.WasCollected || row.Source == null) return;
        var value = row.Source.Get();
        var ends = slider.m_handleLocalXPosMinMax;
        if (slider.m_handle != null && !slider.m_handle.WasCollected)
        {
            var transform = slider.m_handle.transform;
            var position = transform.localPosition;
            var x = SliderMath.PositionOf(value, ends.x, ends.y);
            if (Math.Abs(position.x - x) > 1e-4f) { position.x = x; transform.localPosition = position; }
        }
        if (slider.m_progress != null) NativeUi.SetText(slider.m_progress, SliderMath.Text(value));
    }

    private void Titles()
    {
        _titlesVersion = UITexts.Version;
        var texts = UIKit.Kit.Texts;
        foreach (var row in _rows)
            if (row.Item != null && !row.Item.WasCollected && row.Item.m_title != null)
                NativeUi.SetText(row.Item.m_title, texts.Text(row.Source == null ? _headingKey : row.Source.TextKey));
    }

    /// <summary>A drag or step on a copied slider: the handle moves as the game's own slider does and the value follows.</summary>
    private void Scroll(Row row, float delta)
    {
        var slider = row.Slider!;
        if (row.Source == null || slider.m_handle == null) return;
        var ends = slider.m_handleLocalXPosMinMax;
        var transform = slider.m_handle.transform;
        var position = transform.localPosition;
        position.x = SliderMath.Step(position.x, delta, ends.x, ends.y);
        transform.localPosition = position;
        var value = SliderMath.ValueAt(position.x, ends.x, ends.y);
        row.Source.Set(value);
        if (slider.m_progress != null) NativeUi.SetText(slider.m_progress, SliderMath.Text(value));
        _draggedAt = ForgeCore.Frame.UnscaledTime;
    }

    /// <summary>A drag (<c>ScrollFromHandle</c>) or step (<c>ScrollDelta</c>) on a copied slider: both are one native body that jumps to
    /// <c>DoScrollDelta</c>, which is hooked here alone.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_SettingScrollReceiver), nameof(CM_SettingScrollReceiver.DoScrollDelta))]
    private static class SliderScroll
    {
        [HarmonyPrefix]
        private static bool Prefix(CM_SettingScrollReceiver __instance, float delta)
        {
            if (!BySlider.TryGetValue(__instance.Pointer, out var row)) return true;
            try { row.Group.Scroll(row, delta); }
            catch (Exception error) { Plugin.Error("ui-slider-failed", "A slider of " + row.Group.Id + " failed: " + error); }
            return false;
        }
    }

    /// <summary>The game's own text refresh and reset of a slider never run for the copies (they would read a game setting the copy
    /// does not hold).</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch]
    private static class SliderSkip
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CM_SettingScrollReceiver), nameof(CM_SettingScrollReceiver.UpdateText));
            yield return AccessTools.Method(typeof(CM_SettingScrollReceiver), nameof(CM_SettingScrollReceiver.ResetValue));
        }

        [HarmonyPrefix]
        private static bool Prefix(CM_SettingScrollReceiver __instance) => !BySlider.ContainsKey(__instance.Pointer);
    }

    /// <summary>The copied rows keep the titles written here.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch]
    private static class RowSkip
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CM_SettingsItem), nameof(CM_SettingsItem.UpdateText));
        }

        [HarmonyPrefix]
        private static bool Prefix(CM_SettingsItem __instance) => !Copies.Contains(__instance.Pointer);
    }
}
