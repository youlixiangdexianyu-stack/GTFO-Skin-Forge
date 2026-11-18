using System;
using System.Collections.Generic;
using CellMenu;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>One slider a mod adds to a game settings page: <see cref="Id"/> names it, <see cref="TextKey"/> is the key of its label in
/// ForgeUI's language files (shown in the current language), <see cref="Get"/> and <see cref="Set"/> read and write its value, 0 to 1.</summary>
public sealed record SettingsSlider(string Id, string TextKey, Func<float> Get, Action<float> Set);

/// <summary>Which game method builds a settings page: the key pages by <c>CreateKeyMappingSubMenu</c>, every other page by <c>CreateSubmenu</c>.</summary>
internal enum PageBuilder { KeyMapping, Submenu }

/// <summary>A group of rows one owner adds to one game settings page. <see cref="Items"/> is what it appended to the page's list.</summary>
internal abstract class PageRows
{
    internal abstract PageBuilder Builder { get; }
    internal abstract eSettingsSubMenuId Page { get; }
    /// <summary>What the rows are, for messages ("the mods' key actions").</summary>
    internal abstract string Owner { get; }
    internal readonly List<CM_SettingsItem> Items = new();

    /// <summary>The game is about to lay out the page: drop the rows of an earlier build, copy new ones with <see cref="SettingsPages.CopyRows"/>.</summary>
    internal abstract void Append(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content);
    /// <summary>The game's reset of the page.</summary>
    internal virtual void PageReset() { }
    /// <summary>Per frame.</summary>
    internal virtual void Update() { }
}

/// <summary>Adds rows to the game's own settings pages. The rows join the game's own build of the page: while the game builds a page, the
/// list it hands to its window (<c>CM_ScrollWindow.SetContentItems</c>) gets the rows after the game's, so the game lays out, scrolls,
/// hovers and registers cursor items for them in its single call exactly as for its own rows. The window is never given a list again: the
/// game's <c>SetContentItems</c> destroys every child of the content container before it lays out the new list, so a second call destroyed
/// the game's rows with ours (an empty page and a NullReferenceException on every hover). Each row is a copy of one of the game's rows
/// made while inactive. Rows are registered in a package's Load, before the game builds its settings; one added after that is refused
/// and logged.
/// </summary>
public static class SettingsPages
{
    private static readonly List<PageRows> Groups = new() { KeyBindSettings.Group };
    private static readonly HashSet<(PageBuilder, eSettingsSubMenuId)> Built = new();
    private static eSettingsSubMenuId? _keyMapping, _submenu;
    private static (PageBuilder Builder, eSettingsSubMenuId Page) _laying;

    /// <summary>Adds a heading and one slider row per entry of <paramref name="sliders"/> to the game's audio settings page, after the game's
    /// rows. The rows are copies of the game's sound-effects slider row (<c>Audio_SFXVolume</c>), so they look, drag and scroll like the
    /// game's. A drag writes through <see cref="SettingsSlider.Set"/> at once; the rows show <see cref="SettingsSlider.Get"/> again when it
    /// changes elsewhere. <paramref name="onPageReset"/> runs when the player resets the page. A <paramref name="sectionId"/> already added
    /// is ignored, so several packages may add the same section; false (and a logged error) when the game already built the page. The heading and the labels are keys of ForgeUI's own language files.</summary>
    /// <example><c>SettingsPages.AddAudioSliders("ForgeAudio.volumes", AudioVolumes.HeadingKey, rows, AudioVolumes.ResetAll);</c></example>
    public static bool AddAudioSliders(string sectionId, string headingKey, IReadOnlyList<SettingsSlider> sliders, Action? onPageReset = null)
    {
        const eSettingsSubMenuId page = eSettingsSubMenuId.Audio;
        if (string.IsNullOrWhiteSpace(sectionId)) throw new ArgumentException("Name the section.", nameof(sectionId));
        if (sliders == null || sliders.Count == 0) throw new ArgumentException("Give at least one slider.", nameof(sliders));
        foreach (var group in Groups)
            if (group is SliderRows existing && existing.Id == sectionId) return false;
        if (Built.Contains((PageBuilder.Submenu, page)))
        {
            Plugin.Error("ui-sliders-were-added-after-game-built", "The sliders of " + sectionId + " were added after the game built its audio settings page and are not listed there; add them in the package's Load.");
            return false;
        }
        PatchGroups.Ensure(PatchGroup.Settings);
        Groups.Add(new SliderRows(sectionId, page, eCellSettingID.Audio_SFXVolume, headingKey, sliders, onPageReset));
        return true;
    }

    /// <summary>Per frame.</summary>
    internal static void Update()
    {
        foreach (var group in Groups) group.Update();
    }

    /// <summary>Makes the rows of <paramref name="owner"/> as copies of <paramref name="template"/> under an inactive holder (so none of the
    /// copies' game components runs before <paramref name="copy"/> has set them up), then moves them under the template's parent and appends
    /// them to the list the game is about to lay out.</summary>
    internal static void CopyRows(PageRows owner, CM_SettingsItem template, string holderName,
        Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content, Func<Transform, IEnumerable<CM_SettingsItem>> copy)
    {
        var parent = template.transform.parent;
        var holder = new GameObject(holderName);
        holder.SetActive(false);
        holder.transform.SetParent(parent, false);
        try
        {
            owner.Items.AddRange(copy(holder.transform));
            foreach (var row in owner.Items)
            {
                row.transform.SetParent(parent, false);
                content.Add(new iScrollWindowContent(row.Pointer));
            }
        }
        finally { Object.Destroy(holder); }
    }

    /// <summary>The game's list after its own layout must hold every row of the group, alive, under the window's content container.</summary>
    private static void CheckLaidOut(PageRows group, CM_ScrollWindow window)
    {
        var items = window.m_contentItems;
        var container = window.m_contentContainer == null ? null : window.m_contentContainer.transform;
        var listed = 0;
        if (items != null)
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i]?.TryCast<Component>() is not { } component || component == null)
                {
                    Plugin.Error("ui-games-settings-page-holds-empty", "The game's " + group.Page + " settings page holds an empty or destroyed row at " + i + " after laying out " + group.Owner + ".");
                    continue;
                }
                foreach (var row in group.Items)
                    if (!row.WasCollected && row.Pointer == component.Pointer) { listed++; break; }
            }
        var misplaced = 0;
        foreach (var row in group.Items)
            if (row.WasCollected || container == null || row.transform.parent != container) misplaced++;
        if (listed != group.Items.Count || misplaced > 0)
            Plugin.Error("ui-games-settings-page-laid-out-rows", "The game's " + group.Page + " settings page laid out " + listed + " of " + group.Items.Count + " rows of " + group.Owner + " (" + misplaced + " outside its content).");
    }

    /// <summary>Marks the build of a key page (the gamepad page is built by the same method with its own id).</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.CreateKeyMappingSubMenu))]
    private static class KeyPageBuild
    {
        [HarmonyPrefix]
        private static void Prefix(eSettingsSubMenuId subMenuId) => _keyMapping = subMenuId;

        [HarmonyPostfix]
        private static void Postfix() => _keyMapping = null;
    }

    /// <summary>Marks the build of every other page (every one is built by the same method with its own id).</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.CreateSubmenu))]
    private static class SubmenuBuild
    {
        [HarmonyPrefix]
        private static void Prefix(eSettingsSubMenuId subMenuId) => _submenu = subMenuId;

        [HarmonyPostfix]
        private static void Postfix() => _submenu = null;
    }

    /// <summary>The game lays out each page once: the groups' rows go into that same list, then the result is checked.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_ScrollWindow), nameof(CM_ScrollWindow.SetContentItems))]
    private static class PageLayout
    {
        [HarmonyPrefix]
        private static void Prefix(CM_ScrollWindow __instance, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> contentItems, out bool __state)
        {
            __state = false;
            if (contentItems == null) return;
            if (_keyMapping is { } key) _laying = (PageBuilder.KeyMapping, key);
            else if (_submenu is { } sub) _laying = (PageBuilder.Submenu, sub);
            else return;
            _keyMapping = _submenu = null;
            __state = true;
            var built = _laying;
            Built.Add(built);
            foreach (var group in Groups)
            {
                if (group.Builder != built.Builder || group.Page != built.Page) continue;
                group.Items.Clear();
                try { group.Append(__instance, contentItems); }
                catch (Exception error) { Plugin.Error("ui-adding-games-settings-page-failed", "Adding " + group.Owner + " to the game's " + group.Page + " settings page failed: " + error); }
            }
        }

        [HarmonyPostfix]
        private static void Postfix(CM_ScrollWindow __instance, bool __state)
        {
            if (!__state) return;
            var built = _laying;
            foreach (var group in Groups)
            {
                if (group.Builder != built.Builder || group.Page != built.Page || group.Items.Count == 0) continue;
                try { CheckLaidOut(group, __instance); }
                catch (Exception error) { Plugin.Error("ui-checking-games-settings-page-failed", "Checking " + group.Owner + " on the game's " + group.Page + " settings page failed: " + error); }
            }
        }
    }

    /// <summary>The game's reset of a page also resets what the groups on it hold.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.ResetPageSettings))]
    private static class PageReset
    {
        [HarmonyPostfix]
        private static void Postfix(eSettingsSubMenuId settingsMenuId)
        {
            foreach (var group in Groups)
            {
                if (group.Page != settingsMenuId) continue;
                try { group.PageReset(); }
                catch (Exception error) { Plugin.Error("ui-resetting-failed", "Resetting " + group.Owner + " failed: " + error); }
            }
        }
    }
}
