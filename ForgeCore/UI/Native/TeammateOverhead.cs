using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// The <see cref="TeammateTagPlace.Overhead"/> placement: one text line above a teammate's own name marker. A <c>NavMarker</c> carries
/// four text meshes (title, player name, distance, sign info) and no more, so the line is a new text-only object parented to the name mesh
/// (never a clone of a native object: that would run unrelated scripts), with the name's typography, standing on the name's top edge. It is
/// drawn only while the native name is drawn, so edge clamping, a downed teammate and the marker's own hide and show keep working; the
/// native name and rows are never touched. The local player has no marker of their own, so nothing is drawn for them.
///
/// The marker's own refresh (<c>PlaceNavMarkerOnGO.UpdateExtraInfo</c>, run for every marker on the game's cadence) asks the overhead
/// sources for that teammate's text and writes it; <c>OnDestroy</c> frees the text with its marker. The patches go on the first time an
/// overhead source is added.
/// </summary>
internal static class TeammateOverheads
{
    private sealed class Line
    {
        internal TextMeshPro? Text;
        internal IntPtr Anchor;
    }

    private static readonly Dictionary<IntPtr, Line> Lines = new();
    private static readonly System.Text.StringBuilder Join = new();

    /// <summary>Destroys every text this placement owns (the last overhead source was removed).</summary>
    internal static void Clear()
    {
        foreach (var line in Lines.Values) Release(line);
        Lines.Clear();
    }

    private static void Refresh(PlaceNavMarkerOnGO marker)
    {
        var agent = marker.Player;
        var name = marker.m_marker?.m_playerName;
        var owner = agent == null || agent.IsLocallyOwned ? null : agent.Owner;
        var key = marker.Pointer;
        Lines.TryGetValue(key, out var line);
        if (name == null || owner == null || owner.Pointer == IntPtr.Zero) { Drop(key, line); return; }

        Join.Clear();
        Color? colour = null;
        foreach (var source in TeammateTags.All)
        {
            if (source.Place != TeammateTagPlace.Overhead || !source.Read(owner, out var tag) || string.IsNullOrEmpty(tag.Text)) continue;
            if (Join.Length > 0) Join.Append('\n');
            Join.Append(tag.Text);
            colour ??= tag.Colour ?? source.Colour;
        }
        if (Join.Length == 0) { Drop(key, line); return; }

        if (!name.gameObject.activeInHierarchy)
        {
            if (line?.Text != null) line.Text.gameObject.SetActive(false);
            return;
        }
        if (line == null) Lines[key] = line = new Line();
        // A rebuilt marker or name must not keep drawing through the old native object.
        if (line.Text != null && line.Anchor != name.Pointer) Release(line);
        var text = line.Text ??= Create(name);
        line.Anchor = name.Pointer;
        var wanted = Join.ToString();
        if (text.text != wanted) text.SetText(wanted);
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        text.color = colour!.Value;
        // Directly above the rendered name, in the name mesh's own local space (textBounds is local to it): no screen or parent
        // coordinate and no icon gutter is assumed.
        var bounds = name.textBounds;
        var position = new Vector3(bounds.center.x, 2f * bounds.center.y - bounds.min.y + 4f, 0f);
        if (text.transform.localPosition != position) text.transform.localPosition = position;
    }

    private static void Drop(IntPtr key, Line? line)
    {
        if (line == null) return;
        Release(line);
        Lines.Remove(key);
    }

    private static void Release(Line line)
    {
        if (line.Text != null) Object.Destroy(line.Text.gameObject);
        line.Text = null;
        line.Anchor = IntPtr.Zero;
    }

    private static TextMeshPro Create(TextMeshPro source)
    {
        var host = new GameObject("ForgeUI.TeammateOverhead");
        try
        {
            host.layer = source.gameObject.layer;
            host.transform.SetParent(source.transform, false);
            var text = host.AddComponent<TextMeshPro>();
            text.font = source.font;
            text.fontSharedMaterial = source.fontSharedMaterial;
            text.fontSize = source.fontSize;
            text.rectTransform.sizeDelta = source.rectTransform.sizeDelta;
            text.color = source.color;
            text.transform.localRotation = Quaternion.identity;
            text.transform.localScale = Vector3.one;
            text.margin = Vector4.zero;
            text.enableAutoSizing = false;
            text.richText = true;
            text.overrideColorTags = false;
            text.enableVertexGradient = false;
            text.alignment = TextAlignmentOptions.Top;
            text.overflowMode = TextOverflowModes.Overflow;
            text.SetText("");
            var rect = text.rectTransform;
            rect.anchorMin = rect.anchorMax = source.rectTransform.pivot;
            rect.pivot = new Vector2(0.5f, 0f);
            return text;
        }
        catch { Object.Destroy(host); throw; }
    }

    [PatchGroup(PatchGroup.Markers)]
    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.UpdateExtraInfo))]
    private static class RefreshPatch
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(PlaceNavMarkerOnGO __instance)
        {
            if (TeammateTags.OverheadCount == 0 || __instance == null) return;
            try { Refresh(__instance); }
            catch (Exception error) { Plugin.Error("ui-drawing-teammate-overhead-line-failed", "Drawing a teammate overhead line failed: " + error); }
        }
    }

    [PatchGroup(PatchGroup.Markers)]
    [HarmonyPatch(typeof(PlaceNavMarkerOnGO), nameof(PlaceNavMarkerOnGO.OnDestroy))]
    private static class DestroyPatch
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(PlaceNavMarkerOnGO __instance)
        {
            if (__instance == null || Lines.Count == 0) return;
            try { if (Lines.Remove(__instance.Pointer, out var line)) Release(line); }
            catch (Exception error) { Plugin.Error("ui-freeing-teammate-overhead-line-failed", "Freeing a teammate overhead line failed: " + error); }
        }
    }
}
