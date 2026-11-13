using System;
using System.Collections.Generic;
using System.IO;
using Gear;
using UnityEngine;

namespace ForgeUI;

/// <summary>What a mod answers each frame while the picture of a gear it supplies waits for its model: the model to photograph, nothing yet
/// (<paramref name="failure"/> stays null: asked again next frame), or nothing with a <paramref name="failure"/> text (the picture is drawn
/// empty and the text is logged).</summary>
public delegate GameObject? GearIconModel(GearIDRange gear, out string? failure);

/// <summary>
/// Weapon icons of a mod's own gear, drawn by the game's own icon renderer (<c>GearIconRendering</c>): the same camera, the
/// "Hidden/GearIconShading" pass of the gear shader lit by the menu light, the icon post material and the framing by the model's bounds
/// that every vanilla gun gets, for any frame size the lobby or the cockpit asks for. The renderer works one job at a time and, when a
/// job's turn comes, would assemble the donor gun for the mod's gear; <see cref="Supply"/> takes that load over: the job gets the results
/// the game's load makes and the mod's own model as its loaded gear, and the renderer does the rest. The model may load in the
/// background; the job waits for it, and a model that cannot be had completes the job with an empty picture and one logged error,
/// never the donor gun. Loading the model stays with the mod: this takes over the job, not the model.
/// </summary>
public static class GearIcons
{
    private const float GiveUpSeconds = 20f;
    private sealed class Waiting
    {
        internal GearIconRendering.IconRenderJob Job = null!;
        internal uint Category;
        internal GearIconModel Model = null!;
        internal float Since;
    }
    private static readonly Dictionary<uint, GearIconModel> Supplied = new();
    private static readonly List<Waiting> Jobs = new();
    private static readonly HashSet<uint> Cleared = new();

    /// <summary>Takes over the icon jobs of every gear of <paramref name="category"/> (the gear category's persistent ID): while such a job
    /// waits, <paramref name="model"/> is asked once a frame for the model to photograph. A category another mod supplied is refused
    /// (logged); the same category from the same provider again keeps the first.</summary>
    /// <example><c>GearIcons.Supply(categoryId, (GearIDRange gear, out string? failure) => Models.Of(gear, out failure));</c></example>
    public static void Supply(uint category, GearIconModel model)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        PatchGroups.Ensure(PatchGroup.GearIcons);
        if (Supplied.TryGetValue(category, out var existing))
        {
            if (existing != model) Plugin.Error("ui-gear-icons-category-not-supplied-another", "Gear icons of category " + category + " not supplied: another provider already supplies them.");
            return;
        }
        Supplied[category] = model;
    }

    /// <summary>The game keeps each drawn icon as a file named by the gun's checksum and never draws it again; a picture drawn from an older
    /// model, or an empty one after a failure, must not outlive the session. Call this with a supplied gear's packet before its first
    /// request: the file is removed once per run for each checksum.</summary>
    public static void ClearCached(GearIDRange range)
    {
        var checksum = range.GetChecksum();
        if (Cleared.Add(checksum)) DeleteFile(checksum);
    }

    /// <summary>Draws the icons of <paramref name="range"/> (any gear, vanilla too) again, for a gear whose look a mod changed after the game
    /// drew them: the game's posted jobs, sprites, icon textures and the file of that one checksum are dropped and its jobs posted again.
    /// Needs the game's gear manager and icon renderer (false while either is missing).</summary>
    public static bool Redraw(GearIDRange range)
    {
        if (GearManager.Current == null || GearIconRendering.Current == null) return false;
        var checksum = range.GetChecksum();
        GearManager.m_allGearWithPostedIconJobs?.Remove(checksum);
        GearIconRendering.s_cachedSprites?.Remove(checksum);
        var perKey = GearManager.Current.m_allGearIconTexturesPerInstanceKey;
        if (perKey != null)
            foreach (var textures in perKey) textures?.Remove(checksum);
        DeleteFile(checksum);
        GearManager.PostIconJobs(range);
        return true;
    }

    private static void DeleteFile(uint checksum)
    {
        try
        {
            if (GearIconRendering.TryGetGearIconFilePath(checksum, out var path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) { Plugin.Error("ui-gear-icon-file", "Gear icon file could not be cleared: " + error); }
    }

    /// <summary>The renderer is about to load the gear of <paramref name="job"/>: a supplied category's job is loaded here (true), any other
    /// gear's is left to the game (false).</summary>
    internal static bool Load(GearIconRendering.IconRenderJob job)
    {
        var gear = job.gearToRender;
        if (gear == null) return false;
        var category = gear.GetCompID(eGearComponent.Category);
        if (!Supplied.TryGetValue(category, out var model)) return false;
        IconJobLoad.Begin(job);
        Jobs.Add(new Waiting { Job = job, Category = category, Model = model, Since = Time.realtimeSinceStartup });
        return true;
    }

    internal static bool Pending => Jobs.Count != 0;

    /// <summary>Once a frame while a job waits: completes every job whose model is ready, failed or timed out.</summary>
    internal static void Update()
    {
        if (Jobs.Count != 0) Tick(Time.realtimeSinceStartup);
    }

    internal static void Tick(float now)
    {
        for (var i = 0; i < Jobs.Count;)
        {
            var waiting = Jobs[i];
            GameObject? model = null;
            string? failure = null;
            try { model = waiting.Model(waiting.Job.gearToRender, out failure); }
            catch (Exception error) { failure = error.ToString(); }
            if (model == null && failure == null)
            {
                if (now - waiting.Since < GiveUpSeconds) { i++; continue; }
                failure = "the model did not load in " + GiveUpSeconds + " s";
            }
            if (failure != null)
            {
                Plugin.Error("ui-gear-icon-category-empty", "Gear icon of category " + waiting.Category + " is empty: " + failure);
                model = new GameObject("Gear icon (no model)");
            }
            IconJobLoad.Complete(waiting.Job, model!);
            Jobs.RemoveAt(i);
        }
    }
}
