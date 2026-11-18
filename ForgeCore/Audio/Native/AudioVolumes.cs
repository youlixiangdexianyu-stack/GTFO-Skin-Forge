using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace ForgeAudio;

/// <summary>One of the kit's volumes: the master or a global category. <see cref="Id"/> is <c>master</c> or the category's cues.forge name;
/// <see cref="TextKey"/> is the language key of its settings row label (ForgeUI's language files hold the words); <see cref="Get"/> and
/// <see cref="Set"/> read and write the value, 0 to 1.</summary>
public sealed record VolumeControl(string Id, Func<float> Get, Action<float> Set)
{
    public string TextKey => AudioVolumes.KeyPrefix + Id;
}

/// <summary>Reads and sets the kit's volumes (the master and one per global category, the same for every mod's sounds). The values live in
/// the kit's configuration file and take effect the same frame; a change is written to the file half a second after the last one.
/// ForgeCore lists <see cref="Controls"/> in the game's audio settings itself (<c>ForgeUI.SettingsPages.AddAudioSliders</c>).</summary>
public static class AudioVolumes
{
    private const float SaveDelay = .5f;
    /// <summary>The start of every <see cref="VolumeControl.TextKey"/>: the key of a row is this and its <see cref="VolumeControl.Id"/>.</summary>
    public const string KeyPrefix = "volume.";
    private static float _saveAt = -1f;

    /// <summary>The master first, then the nine categories in cues.forge order.</summary>
    public static IReadOnlyList<VolumeControl> Controls { get; } = BuildControls();

    /// <summary>The language key of the heading of the group of <see cref="Controls"/> on a settings page.</summary>
    public const string HeadingKey = "volume.heading";

    /// <summary>The master volume of every sound the kit plays, 0 to 1.</summary>
    public static float Master { get => Plugin.MasterVolume?.Value ?? 1f; set => Write(Plugin.MasterVolume, value); }

    /// <summary>The volume of a global category (its cues.forge name: ui, weapon, explosion, debris, ambience, voice, vehicle, device, music).</summary>
    public static float Get(string category) => Entry(category)?.Value ?? 1f;

    /// <summary>Sets the volume of a global category; an unknown name throws.</summary>
    public static void Set(string category, float value) => Write(Entry(category), value);

    /// <summary>Puts the master and every category back to full and writes the file at once.</summary>
    public static void ResetAll()
    {
        Plugin.SetVolume(Plugin.MasterVolume, 1f);
        for (var c = 0; c < AudioCategories.Count; c++) Plugin.SetVolume(Plugin.CategoryVolume(c), 1f);
        _saveAt = -1f;
        Plugin.SaveVolumes();
    }

    /// <summary>Per frame: writes the file once a change has rested for <see cref="SaveDelay"/>.</summary>
    internal static void Tick()
    {
        if (_saveAt < 0f || Time.unscaledTime < _saveAt) return;
        _saveAt = -1f;
        Plugin.SaveVolumes();
    }

    private static void Write(ConfigEntry<float>? entry, float value)
    {
        Plugin.SetVolume(entry, value);
        _saveAt = Time.unscaledTime + SaveDelay;
    }

    private static ConfigEntry<float>? Entry(string category)
    {
        var index = Array.IndexOf(AudioCategories.Names, category);
        if (index < 0) throw new ArgumentException("Unknown category '" + category + "'; the categories are " + AudioCategories.Known + ".", nameof(category));
        return Plugin.CategoryVolume(index);
    }

    private static VolumeControl[] BuildControls()
    {
        var controls = new VolumeControl[AudioCategories.Count + 1];
        controls[0] = new VolumeControl("master", () => Master, value => Master = value);
        for (var c = 0; c < AudioCategories.Count; c++)
        {
            var name = AudioCategories.Names[c];
            controls[c + 1] = new VolumeControl(name, () => Get(name), value => Set(name, value));
        }
        return controls;
    }
}
