namespace ForgeAudio.Tests;

/// <summary>The folder convention and the cues.forge format: buses map to global categories, every cue plays on a bus.</summary>
internal static class CatalogChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var none = CueCatalog.Build(new[] { "click.ogg" }, null);
        check(none.Error is { Code: AudioError.InvalidSettings } && none.Error.Value.Detail.Contains("no cues.forge") && none.Error.Value.Detail.Contains("vehicle")
            && none.Cues.Count == 0, "a folder without cues.forge fails and names the categories a bus can map to");

        var plain = CueCatalog.Build(new[] { "click.ogg", "ui\\beep.OGG", "stray.ogg", "LICENSE.txt", "cues.forge" },
            "{\"buses\":{\"menu\":{\"category\":\"ui\"}},\"cues\":{\"click\":{\"bus\":\"menu\"},\"ui/beep\":{\"bus\":\"menu\"}}}");
        check(plain.Error == null && plain.Cues.Count == 2, "a cue named like a file plays that file; other files are ignored");
        check(plain.Failures.TryGetValue("stray", out var stray) && stray.Code == AudioError.InvalidSettings && stray.Detail.Contains("not a cue"),
            "a file no cue names is reported instead of played without a category");
        check(plain.Cues.TryGetValue("click", out var click) && click.Layers.Length == 1 && click.Layers[0].Clips.Single() == "click.ogg" && click.IsDirect
            && click.Bus == "menu" && plain.Buses["menu"].Category == AudioCategory.Ui && click.Gain == 1 && click.PitchMin == 1 && click.PitchMax == 1 && !click.Loop
            && click.Space.Near == 2 && click.Space.Far == 30 && click.Space.Curve.Shape == DistanceCurve.Kind.Linear && click.Space.Blend == 1
            && click.Space.Cull && click.Priority == 100 && click.MaxVoices == 8 && click.MaxPerEmitter == 8 && click.MinInterval == 0
            && click.Environment && click.SelfPlace == Place.Auto && click.OtherPlace == Place.Auto && click.MaxSeconds == 0,
            "a cue with only its bus uses the documented defaults and its bus's category");
        check(plain.Cues.ContainsKey("ui/beep") && plain.Cues.ContainsKey("UI/BEEP"), "subfolder cue is sub/name and lookup ignores case");

        var settings = """
        {
          // comments and trailing commas are allowed
          "buses": {
            "boom": { "category": "explosion", "gainDb": -6, "seats": 2, "duck": { "categories": ["debris", "ambience"], "db": 9 } },
            "debris": { "category": "debris" },
            "bed": { "category": "ambience" },
          },
          "cues": {
            "boom": { "bus": "boom", "gain": 0.9, "pitch": [0.95, 1.05], "priority": 220, "maxVoices": 4, "maxPerEmitter": 1,
                      "minInterval": 0.1, "maxSeconds": 1.8, "place": { "self": "2d", "other": "world" },
                      "space": { "near": 8, "far": 90, "curve": "log", "blend": 0.5, "spread": 30, "doppler": 1, "cull": false },
                      "layers": [
                        { "clips": ["boom-01.ogg", "boom-02.ogg"], "distance": { "max": 40 }, "gainDb": -3, "range": [0.1, 1.5],
                          "offsetMs": 20, "fadeInMs": 5, "fadeOutMs": 100, "pitch": [0.97, 1.03] },
                        { "clips": ["far.ogg"], "distance": { "min": 40 }, "lowpassHz": 600 } ] },
            "hum": { "bus": "bed", "loop": true, "environment": false, "place": "follow", "layers": [ { "clips": ["hum.ogg"], "gain": 0.5 } ], "pitch": 1.2 },
            "step": { "bus": "debris" },
          },
        }
        """;
        var tuned = CueCatalog.Build(new[] { "boom-01.ogg", "boom-02.ogg", "far.ogg", "hum.ogg", "step.ogg" }, settings);
        check(tuned.Error == null && tuned.Failures.Count == 0, "valid cues.forge accepted");
        check(tuned.Cues.Keys.OrderBy(x => x).SequenceEqual(new[] { "boom", "hum", "step" }),
            "files used by another cue's layers are not cues of their own");
        var boom = tuned.Cues["boom"];
        check(boom.Layers.Length == 2 && boom.Layers[0].Clips.SequenceEqual(new[] { "boom-01.ogg", "boom-02.ogg" }) && boom.Gain == .9f
            && boom.PitchMin == .95f && boom.PitchMax == 1.05f && boom.Priority == 220 && boom.MaxVoices == 4 && boom.MaxPerEmitter == 1
            && boom.MinInterval == .1f && boom.MaxSeconds == 1.8f && boom.Bus == "boom" && !boom.IsDirect, "cue settings parsed");
        check(boom.SelfPlace == Place.TwoD && boom.OtherPlace == Place.World && boom.Space.Near == 8 && boom.Space.Far == 90
            && boom.Space.Curve.Shape == DistanceCurve.Kind.Log && boom.Space.Blend == .5f && boom.Space.Spread == 30 && boom.Space.Doppler == 1 && !boom.Space.Cull,
            "placement and space parsed");
        var near = boom.Layers[0]; var far = boom.Layers[1];
        check(MathF.Abs(near.Gain - .708f) < .001f && near.RangeStart == .1f && near.RangeEnd == 1.5f && near.OffsetMs == 20 && near.FadeInMs == 5
            && near.FadeOutMs == 100 && near.PitchMin == .97f && near.PitchMax == 1.03f && near.DistanceMin == 0 && near.DistanceMax == 40
            && far.DistanceMin == 40 && float.IsPositiveInfinity(far.DistanceMax) && far.LowpassHz == 600, "layer settings parsed");
        var bus = tuned.Buses["boom"];
        check(bus.Category == AudioCategory.Explosion && MathF.Abs(bus.Gain - .501f) < .001f && bus.Seats == 2
            && bus.Duck.SequenceEqual(new[] { AudioCategory.Debris, AudioCategory.Ambience }) && bus.DuckDb == 9
            && bus.DuckMask == (AudioCategories.Bit(AudioCategory.Debris) | AudioCategories.Bit(AudioCategory.Ambience))
            && tuned.Buses["debris"].Seats == 0 && tuned.Buses["debris"].Gain == 1 && tuned.Buses["debris"].DuckMask == 0,
            "bus category, gain, seats and the categories it ducks parsed");
        var hum = tuned.Cues["hum"];
        check(hum.Loop && tuned.Buses[hum.Bus].Category == AudioCategory.Ambience && !hum.Environment && hum.SelfPlace == Place.Follow && hum.OtherPlace == Place.Follow
            && hum.PitchMin == 1.2f && hum.Layers[0].Gain == .5f, "loop cue, single placement and flags parsed");
        check(tuned.Files.SequenceEqual(new[] { "boom-01.ogg", "boom-02.ogg", "far.ogg", "hum.ogg", "step.ogg" }), "distinct clip files in stable order");
        check(tuned.MixedFiles.SetEquals(new[] { "boom-01.ogg", "boom-02.ogg", "far.ogg", "hum.ogg" }) && tuned.DirectFiles.SetEquals(new[] { "step.ogg" }),
            "a layered or capped cue's files are mixed, a plain cue's files play as they are");

        var curved = CueCatalog.Build(new[] { "step.ogg" },
            "{\"buses\":{\"b\":{\"category\":\"weapon\"}},\"cues\":{\"step\":{\"bus\":\"b\",\"space\":{\"near\":2,\"far\":50,\"curve\":[[0,1],[10,0.5],[30,0.1]]}}}}");
        check(curved.Cues["step"].Space.Curve is { Shape: DistanceCurve.Kind.Points } points && points.Distances.SequenceEqual(new[] { 0f, 10f, 30f }), "custom curve points parsed");

        // One bad entry fails only that cue, with the reason; the rest load.
        Bad("{\"gian\":1}", "unknown cue setting", "misspelled setting rejected and the known settings named");
        Bad("{\"gain\":5}", "gain must be from 0 to 4", "gain range enforced");
        Bad("{\"gain\":1,\"gainDb\":-3}", "cannot both be set", "linear and decibel gain are exclusive");
        Bad("{\"pitch\":[1.1,1.0]}", "pitch", "reversed pitch range rejected");
        Bad("{\"space\":{\"near\":30,\"far\":30}}", "far must be greater than near", "empty distance range rejected");
        Bad("{\"space\":{\"curve\":\"steep\"}}", "curve must be", "unknown curve rejected");
        Bad("{\"space\":{\"curve\":[[5,1],[5,0]]}}", "must increase", "curve points must increase");
        Bad("{\"maxVoices\":2,\"maxPerEmitter\":3}", "maxPerEmitter", "per-emitter limit cannot exceed the cue limit");
        Bad("{\"place\":\"sky\"}", "place must be", "invalid placement rejected");
        Bad("{\"category\":\"ui\"}", "unknown cue setting", "a cue has no category of its own: its bus has");
        Bad("{\"bus\":\"nope\"}", "not defined", "an undefined bus is rejected");
        Bad("{\"loop\":true,\"maxSeconds\":1}", "loop has no maxSeconds", "a loop cannot be capped");
        Bad("{\"loop\":true,\"layers\":[{\"clips\":[\"step.ogg\"],\"offsetMs\":5}]}", "exactly one layer", "a loop's layer cannot be shaped");
        Bad("{\"layers\":[{\"clips\":[\"../step.ogg\"]}]}", "inside the audio folder", "clip outside the folder rejected");
        Bad("{\"layers\":[{\"clips\":[\"nope.ogg\"]}]}", "was not found", "missing clip file rejected");
        Bad("{\"layers\":[{\"clips\":[\"step.ogg\",\"STEP.ogg\"]}]}", "listed twice", "duplicate clip rejected");
        Bad("{\"layers\":[]}", "layers must list", "a cue needs at least one layer");
        Bad("{\"gain\":1}", "needs a bus", "a cue without a bus is rejected", withBus: false);
        var missing = CueCatalog.Build(new[] { "step.ogg", "other.ogg" },
            "{\"buses\":{\"b\":{\"category\":\"ui\"}},\"cues\":{\"missing\":{\"bus\":\"b\"},\"other\":{\"bus\":\"b\"}}}");
        check(missing.Failures.TryGetValue("missing", out var absent) && absent.Detail.Contains("no file missing.ogg") && missing.Cues.ContainsKey("other"),
            "settings for a cue without a file rejected");

        // A bad bus fails the folder: a cue on it would have no category.
        BadBus("{\"gain\":1}", "needs a category", "a bus without a category fails the folder");
        BadBus("{\"category\":\"sfx\"}", "category must be one of", "an unknown category fails the folder");
        BadBus("{\"category\":\"ui\",\"duck\":{\"buses\":[\"x\"]}}", "unknown duck setting", "ducking names categories, not buses");

        var broken = CueCatalog.Build(new[] { "step.ogg" }, "{\"cues\":{\"step\":{\"gain\":1}}");
        check(broken.Error is { Code: AudioError.InvalidSettings } && broken.Cues.Count == 0, "unreadable cues.forge fails the whole folder explicitly");
        var duplicate = CueCatalog.Build(new[] { "step.ogg" }, "{\"cues\":{\"step\":{},\"STEP\":{}}}");
        check(duplicate.Error is { Code: AudioError.InvalidSettings }, "a cue named twice in cues.forge is rejected");
        var array = CueCatalog.Build(new[] { "step.ogg" }, "[]");
        check(array.Error is { Code: AudioError.InvalidSettings }, "cues.forge must be an object");
        var flat = CueCatalog.Build(new[] { "step.ogg" }, "{\"step\":{}}");
        check(flat.Error is { Code: AudioError.InvalidSettings } && flat.Error.Value.Detail.Contains("unknown section"), "the old flat format is not read");
        check(CueCatalog.Build(new[] { "step.ogg" }, new string(' ', CueCatalog.MaxSettingsBytes + 1)).Error != null, "cues.forge size limit enforced");

        var unconverted = CueCatalog.Build(new[] { "shot.wav", "music.mp3", "music.ogg" }, "{\"buses\":{\"m\":{\"category\":\"music\"}},\"cues\":{\"music\":{\"bus\":\"m\"}}}");
        check(unconverted.Failures.TryGetValue("shot", out var wav) && wav.Code == AudioError.UnsupportedCodec && wav.Detail.Contains("convert_audio.py"),
            "an unconverted WAV is reported with the conversion command");
        check(unconverted.Cues.ContainsKey("music") && !unconverted.Failures.ContainsKey("music"), "a source file next to its converted Ogg is not an error");

        void Bad(string cueJson, string message, string name, bool withBus = true)
        {
            var cue = withBus && !cueJson.Contains("\"bus\"") ? "{\"bus\":\"b\"," + cueJson.Substring(1) : cueJson;
            var result = CueCatalog.Build(new[] { "step.ogg", "other.ogg" }, "{\"buses\":{\"b\":{\"category\":\"ui\"}},\"cues\":{\"other\":{\"bus\":\"b\"},\"step\":" + cue + "}}");
            check(result.Error == null && !result.Cues.ContainsKey("step") && result.Cues.ContainsKey("other")
                && result.Failures.TryGetValue("step", out var failure) && failure.Code == AudioError.InvalidSettings
                && failure.Detail.Contains(message), name);
        }

        void BadBus(string busJson, string message, string name)
        {
            var result = CueCatalog.Build(new[] { "step.ogg" }, "{\"buses\":{\"b\":" + busJson + "},\"cues\":{\"step\":{\"bus\":\"b\"}}}");
            check(result.Error is { Code: AudioError.InvalidSettings } error && error.Detail.Contains("bus 'b'") && error.Detail.Contains(message) && result.Cues.Count == 0, name);
        }
    }
}
