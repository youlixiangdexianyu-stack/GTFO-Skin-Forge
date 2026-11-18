using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVfx.EditorTools
{
    /// <summary>The Forge effects, built from source here: every prefab is layered the way the game's own effects are
    /// (an FX root over several particle parts plus a light; an explosion is flash + fireball + smoke + sparks + debris +
    /// light). Every texture is drawn procedurally here (smoke puffs with soft top light, an eroding fire flipbook,
    /// jagged bolts, electric crackle, broken shock rings, muzzle flashes, faceted debris chunks, a roiling plasma
    /// flipbook, an accretion spiral, glows, beam and trail cross-sections); nothing third-party is used. Fire and smoke
    /// use the premultiplied particle shader, so a puff glows while it burns and darkens what is behind it as soot.
    ///
    ///     Unity.exe -batchmode -projectPath Tools/Vfx/ForgeVfx -executeMethod ForgeVfx.EditorTools.ForgeVfxOwnEffects.Create
    ///
    /// Re-running rewrites the own textures, every material, the line mesh and every prefab from this source, and deletes
    /// own textures and materials it no longer writes. The conventions are <see cref="ForgeVfxProject"/>'s: +Z is the
    /// effect's direction; a line member is one metre along +Z, its body layers stretch with the length (Hierarchy
    /// scaling, Local simulation) and its end layers sit at z = 0 or z = 1 with Local scaling so they keep their size; a
    /// sustained member loops at its root.
    ///
    /// A prefab may carry inactive state children (`charging`, `charged`, `launch`, `fizzle`, `storm` on the orb; `ignite`
    /// on the beam and the flame; `open` and `collapse` on the black hole). Playing the member never shows them; the
    /// energy weapons activate one for a phase of a shot. Particle lights are authored here at the brightness the
    /// effect's light should have; the energy weapons switch them off and light the scene with the game's own effect
    /// lights at the same colour, range and timing (`Mods/ForgeCore/Visual/Native/PointLights.cs`).
    ///
    /// Budgets (particle caps per instance, every state included; what is live at once is lower): sparks 63, flame
    /// impact 20, explosion 131, plasma explosion 101, flame 153, beam 132, lightning arc 74 per hop, plasma discharge
    /// 22, blast tracer 40, plasma orb 124, black hole 366. Collision is limited to sparks, debris and the flame's fire
    /// and smoke.</summary>
    public static class ForgeVfxOwnEffects
    {
        private const string Own = ForgeVfxProject.Root + "Own/";

        [MenuItem("Forge VFX/Rebuild self-made effects")]
        public static void Create() => ForgeVfxProject.RunAndExit("Forge VFX self-made effects", Build);

        // ---- shared resources ---------------------------------------------------------------------------------

        private static Material _glow, _beam, _flow, _trail, _disc, _ringThin, _shockRing, _flash, _bolt, _boltTile, _crackle, _spiral, _plasma, _lens;
        private static Material _smoke, _debris, _fire, _fireAdditive;
        private static Mesh _line;
        private static readonly HashSet<string> Written = new HashSet<string>(StringComparer.Ordinal);

        private static void Build()
        {
            foreach (var folder in new[] { "Textures", "Materials", "Meshes" })
                Directory.CreateDirectory(Path.Combine(ForgeVfxProject.ProjectFolder, Own, folder));
            Directory.CreateDirectory(Path.Combine(ForgeVfxProject.ProjectFolder, ForgeVfxProject.EffectsFolder));
            AssetDatabase.Refresh();
            Written.Clear();

            const string additive = "ForgeVfx/Particles Additive", blended = "ForgeVfx/Particles Alpha Blended",
                premultiplied = "ForgeVfx/Particles Premultiplied";
            _glow = WriteMaterial("glow_additive", additive, WriteTexture("glow", 128, 128, Square(128, GlowPixel)), 2f);
            _beam = WriteMaterial("beam_additive", additive, WriteTexture("beam_soft", 64, 64, Square(64, BeamPixel)), 2f);
            _trail = WriteMaterial("trail_additive", additive, WriteTexture("trail_soft", 64, 64, Square(64, (u, v) => BeamPixel(v, u))), 1.8f);
            WriteMaterial("strip_additive", additive, WriteTexture("strip_soft", 64, 64, Square(64, (u, v) => StripPixel(v))), 1.8f);
            _flow = WriteMaterial("flow_additive", additive, WriteTexture("beam_flow_sheet", 256, 256, FlowPixel), 1.6f);
            _disc = WriteMaterial("disc_alpha", blended, WriteTexture("disc", 128, 128, Square(128, DiscPixel)), 1f);
            _ringThin = WriteMaterial("ring_thin_additive", additive, WriteTexture("ring_thin", 256, 256, Square(256, RingPixel)), 2f);
            _shockRing = WriteMaterial("shock_ring_additive", additive, WriteTexture("shock_ring", 256, 256, Square(256, ShockRingPixel)), 1.6f);
            _flash = WriteMaterial("flash_additive", additive, WriteTexture("flash_sheet", 512, 512, Sheet(2, 256, FlashPixel)), 2f);
            _bolt = WriteMaterial("bolt_additive", additive, WriteTexture("bolt_sheet", 512, 512, BoltSheet()), 1.8f);
            _boltTile = WriteMaterial("bolt_tile_additive", additive, WriteTexture("bolt_tile", 2048, 512, BoltTile(), repeat: true), 1.8f);
            _crackle = WriteMaterial("crackle_additive", additive, WriteTexture("crackle_sheet", 512, 512, CrackleSheet()), 1.6f);
            _spiral = WriteMaterial("spiral_additive", additive, WriteTexture("spiral", 256, 256, Square(256, SpiralPixel)), 1.4f);
            _plasma = WriteMaterial("plasma_additive", additive, WriteTexture("plasma_sheet", 512, 512, Sheet(4, 128, PlasmaPixel)), 1.5f);
            _smoke = WriteMaterial("smoke_premultiplied", premultiplied, WriteTexture("smoke_sheet", 512, 512, Sheet(4, 128, SmokePixel)), 1f);
            _debris = WriteMaterial("debris_alpha", blended, WriteTexture("debris_sheet", 256, 256, Sheet(4, 64, DebrisPixel)), 1f);
            var fire = WriteTexture("fire_sheet", 512, 512, Sheet(4, 128, FirePixel));
            _fire = WriteMaterial("fire_premultiplied", premultiplied, fire, 1.4f);
            _fireAdditive = WriteMaterial("fire_additive", additive, fire, 1.2f);
            // Bent light round the black hole: the shape is an annulus (strongest just outside the core), the bend a swirl about the centre,
            // read linear (a normal-like map) so mid-grey bends nothing.
            _lens = WriteMaterial("lens_distort", "ForgeVfx/Particles Distort", WriteTexture("lens_mask", 128, 128, Square(128, LensMaskPixel)), 1f);
            _lens.SetTexture("_DistortTex", Linear(WriteTexture("lens_swirl", 128, 128, Square(128, LensSwirlPixel))));
            _lens.SetFloat("_Strength", 0.06f);
            _lens.SetColor("_TintColor", new Color(0.6f, 0.45f, 1f, 0.3f));
            _lens.SetVector("_Scroll", Vector4.zero);
            EditorUtility.SetDirty(_lens);
            _line = WriteLineMesh();
            DeleteUnwritten("Textures", ".png");
            DeleteUnwritten("Materials", ".mat");

            Save("forge_sparks", Sparks);
            Save("forge_explosion", Explosion);
            Save("forge_flame_impact", FlameImpact);
            Save("forge_flame", Flame);
            Save("forge_black_hole", BlackHole);
            Save("forge_plasma_orb", PlasmaOrb);
            Save("forge_plasma_explosion", PlasmaExplosion);
            Save("forge_energy_beam", EnergyBeam);
            Save("forge_lightning_arc", LightningArc);
            Save("forge_plasma_discharge", PlasmaDischarge);
            Save("forge_blast_tracer", BlastTracer);
            AssetDatabase.SaveAssets();
        }

        private static void Save(string member, Action<GameObject> build)
        {
            ForgeVfxProject.Spec(member);
            var root = new GameObject(member);
            try
            {
                build(root);
                var path = ForgeVfxProject.EffectsFolder + "/" + member + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(root, path, out var saved);
                if (!saved) throw new InvalidOperationException("Could not save " + path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // ---- palettes ------------------------------------------------------------------------------------------

        private static readonly Color Hot = new Color(1f, 0.95f, 0.8f), Warm = new Color(1f, 0.55f, 0.15f), Cool = new Color(0.75f, 0.15f, 0.04f);
        private static readonly Color Amber = new Color(1f, 0.45f, 0.1f), Electric = new Color(0.3f, 0.65f, 1f), Plasma = new Color(0.12f, 0.55f, 1f);
        private static readonly Color Violet = new Color(0.62f, 0.2f, 1f);

        private static Gradient Cooling() => Ramp(new[] { (0f, Hot), (0.35f, Warm), (1f, Cool) }, new[] { (0f, 1f), (0.6f, 1f), (1f, 0f) });

        private static Gradient Flash(Color color) => Ramp(new[] { (0f, Color.white), (0.3f, color), (1f, color) }, new[] { (0f, 1f), (1f, 0f) });

        private static Gradient Hold(Color color, float peak = 1f) => Ramp(new[] { (0f, color), (1f, color) }, new[] { (0f, 0f), (0.2f, peak), (0.75f, peak), (1f, 0f) });

        private static Gradient Fade(float start = 1f) => Ramp(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, start), (1f, 0f) });

        /// <summary>A light carrier's brightness: full at once, a third left by a fifth of its life, out at the end.</summary>
        private static Gradient Decay() => Ramp(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 1f), (0.2f, 0.35f), (1f, 0f) });

        /// <summary>Burning gas for the premultiplied fire material: light (RGB) with almost no cover at first, white-yellow
        /// to orange to dull red, then soot (dark, covering) that thins out. Blue at the very start for a fuel jet.</summary>
        private static Gradient FireToSoot(bool blueStart = false, float soot = 0.42f)
            => Ramp(new[] { (0f, blueStart ? new Color(0.2f, 0.3f, 0.8f) : new Color(1f, 0.9f, 0.7f)), (0.08f, new Color(1f, 0.68f, 0.32f)), (0.3f, new Color(1f, 0.42f, 0.1f)),
                    (0.55f, new Color(0.35f, 0.09f, 0.02f)), (0.72f, new Color(0.05f, 0.045f, 0.04f)), (1f, new Color(0f, 0f, 0f)) },
                new[] { (0f, 0f), (0.05f, 0.1f), (0.35f, 0.18f), (0.65f, soot), (1f, 0f) });

        /// <summary>Smoke for the premultiplied smoke material: grey of the given brightness covering at most
        /// <paramref name="cover"/>; a warm start reads as smoke lit from inside by the fire that made it.</summary>
        private static Gradient Soot(float cover, float grey = 0.16f, bool warm = false)
            => Ramp(warm
                    ? new[] { (0f, new Color(0.9f, 0.45f, 0.12f)), (0.12f, new Color(0.3f, 0.12f, 0.04f)), (0.3f, new Color(grey, grey * 0.97f, grey * 0.94f)), (1f, new Color(grey * 0.6f, grey * 0.6f, grey * 0.6f)) }
                    : new[] { (0f, new Color(grey, grey * 0.98f, grey * 0.96f)), (1f, new Color(grey * 0.6f, grey * 0.6f, grey * 0.6f)) },
                new[] { (0f, 0f), (0.1f, cover), (0.55f, cover * 0.6f), (1f, 0f) });

        /// <summary>The game's burst beams: white, then orange, then deep red, gone in a tenth of a second.</summary>
        private static Gradient Streak() => Ramp(new[] { (0f, Color.white), (0.32f, new Color(1f, 0.55f, 0f)), (1f, new Color(0.6f, 0.15f, 0f)) },
            new[] { (0f, 0f), (0.04f, 1f), (0.72f, 0.3f), (1f, 0f) });

        /// <summary>The game's small sparks: bright orange cooling to dark red.</summary>
        private static Gradient Sparkle() => Ramp(new[] { (0f, new Color(1f, 0.75f, 0.35f)), (0.3f, new Color(1f, 0.45f, 0.1f)), (1f, new Color(0.4f, 0.04f, 0f)) },
            new[] { (0f, 1f), (0.7f, 1f), (1f, 0f) });

        /// <summary>Electric sparks: blue-white, cooling through pale blue to nothing.</summary>
        private static Gradient Arcing() => Ramp(new[] { (0f, Color.white), (0.3f, new Color(0.6f, 0.8f, 1f)), (1f, new Color(0.15f, 0.3f, 1f)) },
            new[] { (0f, 1f), (0.6f, 0.8f), (1f, 0f) });

        // ---- one-shot point effects ----------------------------------------------------------------------------

        /// <summary>A spark burst thrown along +Z, as from a blade striking steel: stretched sparks that fall and bounce,
        /// a ragged white-hot flash, a light, slow embers, a few metal chips and a wisp of smoke.</summary>
        private static void Sparks(GameObject root)
        {
            new Layer(root, _glow, 0.25f, 40).Life(0.25f, 0.6f).Speed(3f, 10f).Size(0.01f, 0.022f).Colors(new Color(1f, 0.92f, 0.7f), new Color(1f, 0.7f, 0.35f))
                .Gravity(1f).Burst(0f, 22, 30).Cone(40f, 0.02f).Over(Cooling()).SizeOver(1f, 0.3f).Collide(0.35f, 0.45f, 0.15f).Stretch(0.035f, 1.5f);
            new Layer(Child(root, "flash"), _flash, 0.1f, 2).Local().Life(0.035f, 0.05f).Size(0.22f, 0.3f).Spin().Frames(4).Colors(new Color(1f, 0.85f, 0.6f))
                .Burst(0f, 1, 1).Over(Flash(Warm));
            LightCarrier(root, "light", 0.12f, new Color(1f, 0.62f, 0.3f), 2.5f, 2f);
            new Layer(Child(root, "embers"), _glow, 0.2f, 12).Life(0.6f, 1.1f).Speed(0.8f, 2.4f).Size(0.01f, 0.018f).Colors(new Color(1f, 0.7f, 0.35f))
                .Gravity(0.35f).Burst(0f, 6, 10).Cone(60f, 0.03f).Over(Cooling()).Noise(0.4f, 1.5f);
            new Layer(Child(root, "chips"), _debris, 0.2f, 6).Life(0.5f, 0.9f).Speed(2f, 5f).Size(0.012f, 0.025f).Spin().Turn(8f).Frames(16)
                .Colors(new Color(0.42f, 0.44f, 0.46f)).Gravity(1.4f).Burst(0f, 3, 5).Cone(50f, 0.02f).Over(Fade()).Collide(0.3f, 0.5f, 0.2f);
            new Layer(Child(root, "smoke"), _smoke, 0.2f, 2).Life(0.8f, 1.2f).Speed(0.15f, 0.4f).Size(0.16f, 0.24f).Spin().Frames(16)
                .Gravity(-0.04f).Burst(0f, 1, 2).Sphere(0.04f).Over(Soot(0.3f, 0.13f)).SizeOver(1f, 2.6f).Turn(0.6f).Fudge(10f);
        }

        /// <summary>A detonation of radius 2.2 m: a short white-hot flash and a ragged flash core, a light that lingers
        /// and dies over half a second, a rolling fireball that burns out into soot within a second, a ring of smoke puffs
        /// lit orange from inside that cool into dark grey and drift up, short radial streaks, a spray of fast sparks,
        /// bouncing sparks, tumbling dark debris that falls and bounces, and embers that drift for a while.</summary>
        private static void Explosion(GameObject root)
        {
            new Layer(root, _glow, 0.1f, 2).Life(0.07f, 0.09f).Size(2.4f, 2.8f).Colors(new Color(1f, 0.8f, 0.5f, 0.8f)).Burst(0f, 1, 1)
                .Over(Flash(Warm)).SizeOver(0.6f, 1.15f);
            LightCarrier(root, "light", 0.5f, new Color(1f, 0.62f, 0.28f), 8f, 3f);
            new Layer(Child(root, "flash_core"), _flash, 0.1f, 2).Life(0.045f, 0.065f).Size(1.5f, 1.9f).Spin().Frames(4).Colors(new Color(1f, 0.88f, 0.65f))
                .Burst(0f, 1, 2).Over(Flash(Warm));
            new Layer(Child(root, "fireball"), _fire, 0.1f, 8).Life(0.5f, 0.75f).Speed(1.5f, 3.5f).Size(1f, 1.45f).Spin().Turn(0.8f).Sphere(0.35f)
                .Burst(0f, 4, 5).Sheet(4, 4, animate: true).Colors(new Color(0.75f, 0.62f, 0.5f)).Over(FireToSoot(soot: 0.5f))
                .SizeCurve(new AnimationCurve(new Keyframe(0f, 0.55f, 0f, 3f), new Keyframe(0.35f, 1.1f), new Keyframe(1f, 1.4f))).Drag(5f).Gravity(-0.15f).Fudge(5f);
            new Layer(Child(root, "smoke"), _smoke, 0.1f, 10).Delay(0.04f).Life(1.8f, 2.8f).Speed(2f, 6f).Size(1f, 1.5f).Spin().Turn(0.3f).Sphere(0.4f)
                .Burst(0f, 6, 8).Frames(16).Over(Soot(0.45f, 0.14f, warm: true))
                .SizeCurve(new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.2f, 0.9f), new Keyframe(1f, 1.6f))).Drag(4f).Gravity(-0.06f).Fudge(15f);
            new Layer(Child(root, "streaks"), _glow, 0.1f, 16).Life(0.06f, 0.12f).Speed(0.4f, 0.8f).Size(0.14f, 0.24f).Sphere(0.9f, 0.4f)
                .Burst(0f, 10, 14).Over(Streak()).Stretch(0f, 4f);
            new Layer(Child(root, "sparks"), _glow, 0.1f, 48).Life(0.15f, 0.45f).Speed(10f, 20f).Size(0.012f, 0.02f).Sphere(0.3f)
                .Burst(0f, 30, 40).Over(Sparkle()).Drag(3f).Gravity(0.6f).Stretch(0.05f, 1f);
            new Layer(Child(root, "bouncers"), _glow, 0.1f, 16).Life(0.3f, 0.7f).Speed(5f, 10f).Size(0.018f, 0.03f).Sphere(0.3f)
                .Burst(0f, 10, 14).Gravity(1.5f).Over(Sparkle()).Collide(0.4f, 0.4f, 0.1f).Stretch(0.04f, 1f);
            new Layer(Child(root, "debris"), _debris, 0.1f, 12).Life(0.9f, 1.5f).Speed(4f, 9f).Size(0.035f, 0.08f).Spin().Turn(9f).Sphere(0.2f).Frames(16)
                .Burst(0f, 8, 12).Colors(new Color(0.3f, 0.29f, 0.28f)).Gravity(1.6f).Over(Fade()).Collide(0.25f, 0.5f, 0.2f);
            new Layer(Child(root, "embers"), _glow, 0.1f, 16).Delay(0.05f).Life(1f, 2f).Speed(0.5f, 2.5f).Size(0.014f, 0.024f).Sphere(0.6f)
                .Burst(0f, 10, 14).Gravity(-0.04f).Noise(0.6f, 1.2f).Over(Cooling());
        }

        /// <summary>Burning fuel landing on a surface, played up to 12 times a second: a small warm flash with a short
        /// light, a lick of fire that burns into soot and rises, a smoke puff, a few sparks and a burning ember or two
        /// left behind.</summary>
        private static void FlameImpact(GameObject root)
        {
            new Layer(root, _glow, 0.1f, 2).Life(0.06f, 0.08f).Size(0.45f, 0.55f).Colors(new Color(1f, 0.6f, 0.25f, 0.7f)).Burst(0f, 1, 1).Over(Flash(Warm));
            LightCarrier(root, "light", 0.2f, new Color(1f, 0.45f, 0.12f), 2.5f, 2f);
            new Layer(Child(root, "fire"), _fire, 0.1f, 3).Life(0.35f, 0.5f).Speed(0.5f, 1.2f).Size(0.35f, 0.5f).Spin().Turn(1.5f).Sphere(0.08f)
                .Burst(0f, 1, 2).Sheet(4, 4, animate: true).Gravity(-0.3f).Over(FireToSoot()).SizeOver(0.6f, 1.3f).Drag(2f).Fudge(5f);
            new Layer(Child(root, "smoke"), _smoke, 0.1f, 2).Delay(0.1f).Life(1f, 1.5f).Speed(0.2f, 0.5f).Size(0.3f, 0.45f).Spin().Turn(0.5f).Sphere(0.06f)
                .Burst(0f, 1, 1).Frames(16).Gravity(-0.12f).Over(Soot(0.35f, 0.1f, warm: true)).SizeOver(0.8f, 2.4f).Fudge(12f);
            new Layer(Child(root, "sparks"), _glow, 0.1f, 8).Life(0.1f, 0.3f).Speed(2.5f, 6f).Size(0.01f, 0.018f).Sphere(0.1f)
                .Burst(0f, 4, 6).Gravity(0.8f).Over(Sparkle()).Drag(2f).Stretch(0.04f, 1f);
            new Layer(Child(root, "embers"), _glow, 0.1f, 4).Life(0.6f, 1.2f).Speed(0.2f, 0.8f).Size(0.012f, 0.02f).Sphere(0.12f)
                .Burst(0f, 1, 3).Gravity(0.15f).Noise(0.3f, 2f).Over(Cooling());
        }

        /// <summary>A plasma burst of radius 3 m: a blue-white flash and ragged flash core, a light that lingers half a
        /// second, a broken shock ring, a roiling plasma core that swells and fades from blue-white to violet, crackling
        /// electric clusters and lashing tendrils for a third of a second, thrown sparks, drifting motes and a dark smoke
        /// cloud that rises.</summary>
        private static void PlasmaExplosion(GameObject root)
        {
            new Layer(root, _glow, 0.15f, 2).Life(0.09f, 0.11f).Size(3f, 3.4f).Colors(new Color(0.6f, 0.85f, 1f, 0.45f)).Burst(0f, 1, 1)
                .Over(Flash(Plasma)).SizeOver(0.5f, 1.1f);
            LightCarrier(root, "light", 0.55f, new Color(0.3f, 0.55f, 1f), 7f, 3f);
            new Layer(Child(root, "flash_core"), _flash, 0.1f, 2).Life(0.04f, 0.06f).Size(2f, 2.4f).Spin().Frames(4).Colors(new Color(0.75f, 0.9f, 1f))
                .Burst(0f, 1, 1).Over(Flash(Plasma));
            new Layer(Child(root, "ring"), _shockRing, 0.1f, 2).Life(0.32f, 0.32f).Size(1f, 1f).Spin().Colors(new Color(0.5f, 0.8f, 1f, 0.45f)).Burst(0f, 1, 1)
                .Over(Ramp(new[] { (0f, Color.white), (1f, Plasma) }, new[] { (0f, 1f), (1f, 0f) }))
                .SizeCurve(new AnimationCurve(new Keyframe(0f, 0.3f, 0f, 20f), new Keyframe(1f, 6f, 1f, 0f)));
            new Layer(Child(root, "core"), _plasma, 0.1f, 5).Life(0.35f, 0.5f).Speed(0.3f, 1f).Size(1.8f, 2.3f).Spin().Turn(0.8f).Sphere(0.25f).Burst(0f, 3, 4)
                .Sheet(4, 4, animate: true).Colors(new Color(0.55f, 0.8f, 1f, 0.6f))
                .Over(Ramp(new[] { (0f, Color.white), (0.3f, new Color(0.45f, 0.7f, 1f)), (1f, new Color(0.45f, 0.25f, 1f)) }, new[] { (0f, 1f), (0.4f, 0.7f), (1f, 0f) }))
                .SizeCurve(new AnimationCurve(new Keyframe(0f, 0.45f, 0f, 3f), new Keyframe(0.4f, 1.05f), new Keyframe(1f, 1.25f))).Drag(3f);
            new Layer(Child(root, "arcs"), _crackle, 0.25f, 12).Life(0.05f, 0.09f).Size(1.2f, 2f).Spin().Sphere(0.8f).Frames(16)
                .Burst(0f, 3, 3).Burst(0.05f, 3, 3).Burst(0.12f, 2, 2).Burst(0.2f, 2, 2).Colors(new Color(0.6f, 0.9f, 1f, 0.6f));
            new Layer(Child(root, "tendrils"), _bolt, 0.2f, 8).Line(_line).Splay(Mathf.PI).Size3(0.35f, 0.45f, 1.4f, 2.2f).Frames(8).Life(0.04f, 0.07f)
                .Burst(0f, 3, 3).Burst(0.06f, 2, 2).Burst(0.14f, 2, 2).Colors(new Color(0.65f, 0.85f, 1f, 0.85f));
            new Layer(Child(root, "sparks"), _glow, 0.1f, 44).Life(0.3f, 0.6f).Speed(5f, 12f).Size(0.016f, 0.03f).Sphere(0.2f).Burst(0f, 30, 40)
                .Gravity(0.4f).Over(Arcing()).Drag(1.5f).Stretch(0.03f, 1.2f);
            new Layer(Child(root, "motes"), _glow, 0.1f, 18).Life(0.8f, 1.4f).Speed(0.5f, 2f).Size(0.03f, 0.05f).Sphere(0.8f).Burst(0f, 12, 16)
                .Colors(new Color(0.35f, 0.7f, 1f, 0.6f)).Over(Hold(Color.white)).Noise(1f, 1.2f);
            new Layer(Child(root, "smoke"), _smoke, 0.1f, 7).Delay(0.1f).Life(1.4f, 2.2f).Speed(1f, 3f).Size(1f, 1.5f).Spin().Turn(0.3f).Sphere(0.4f)
                .Burst(0f, 4, 6).Frames(16).Over(Soot(0.45f, 0.09f)).SizeOver(0.8f, 1.7f).Drag(3f).Gravity(-0.05f).Fudge(15f);
        }

        // ---- sustained point effects ---------------------------------------------------------------------------

        /// <summary>A flamethrower jet along +Z reaching about 8 m: a blue pilot at the nozzle; burning fuel in the
        /// fire flipbook that starts blue, flares yellow-orange, reddens and cools into soot as it slows; brighter
        /// licks of flame inside it; dark smoke that peels off the end of the jet and rises; sparks, falling embers and a
        /// glow carrying the light. Fire and smoke stop at walls. The inactive `ignite` child is the jet catching.</summary>
        private static void Flame(GameObject root)
        {
            new Layer(root, _fire, 1f, 56, loop: true).Rate(80f).Life(0.45f, 0.6f).Speed(12f, 14f).Size(0.45f, 0.6f).Spin().Turn(2f)
                .Cone(5f, 0.04f).Sheet(4, 4, animate: true).Over(FireToSoot(blueStart: true, soot: 0.38f))
                .SizeCurve(AnimationCurve.EaseInOut(0f, 0.5f, 1f, 4.5f)).Drag(0.6f).Collide(0f, 0.7f, 0.4f).Fudge(5f);
            new Layer(Child(root, "jet"), _glow, 1f, 14, loop: true).Rate(70f).Life(0.12f, 0.18f).Speed(12f, 14f).Size(0.06f, 0.1f).Cone(3f, 0.02f)
                .Over(Ramp(new[] { (0f, new Color(0.45f, 0.6f, 1f)), (0.4f, new Color(1f, 0.85f, 0.55f)), (1f, Warm) }, new[] { (0f, 0.15f), (0.3f, 0.3f), (1f, 0f) }))
                .SizeOver(1f, 2.5f).Stretch(0.035f, 1f);
            new Layer(Child(root, "licks"), _fireAdditive, 1f, 16, loop: true).Rate(30f).Life(0.3f, 0.42f).Speed(11f, 13f).Size(0.2f, 0.3f).Spin()
                .Cone(4f, 0.03f).Sheet(4, 4, animate: true)
                .Over(Ramp(new[] { (0f, new Color(1f, 0.9f, 0.7f)), (0.3f, new Color(1f, 0.6f, 0.2f)), (1f, new Color(0.7f, 0.15f, 0.03f)) }, new[] { (0f, 0f), (0.08f, 0.4f), (0.6f, 0.28f), (1f, 0f) }))
                .SizeCurve(AnimationCurve.EaseInOut(0f, 0.6f, 1f, 3.5f)).Drag(0.6f);
            new Layer(Child(root, "core"), _glow, 1f, 4, loop: true).Local().Rate(40f).Life(0.04f, 0.06f).Speed(2f, 4f).Size(0.04f, 0.06f)
                .Cone(3f, 0.02f).Colors(new Color(0.45f, 0.6f, 1f, 0.5f));
            new Layer(Child(root, "smoke"), _smoke, 1f, 18, loop: true).Rate(9f).Life(1.1f, 1.6f).Speed(8f, 10f).Size(0.4f, 0.5f).Spin().Turn(0.6f)
                .Cone(9f, 0.05f).Frames(16).Over(Soot(0.4f, 0.09f, warm: true)).SizeCurve(AnimationCurve.EaseInOut(0f, 0.6f, 1f, 5f)).Drag(1.6f).Gravity(-0.18f)
                .Collide(0f, 0.7f, 0.4f).Fudge(15f);
            new Layer(Child(root, "sparks"), _glow, 1f, 14, loop: true).Rate(18f).Life(0.25f, 0.5f).Speed(8f, 12f).Size(0.012f, 0.02f).Cone(10f, 0.03f)
                .Gravity(0.4f).Over(Sparkle()).Collide(0.3f, 0.5f, 0.3f).Stretch(0.02f, 1f);
            new Layer(Child(root, "embers"), _glow, 1f, 14, loop: true).Rate(10f).Life(0.8f, 1.4f).Speed(5f, 8f).Size(0.012f, 0.02f).Cone(12f, 0.03f)
                .Drag(1.5f).Gravity(0.25f).Noise(0.6f, 1.5f).Over(Cooling());
            new Layer(Child(root, "glow", new Vector3(0f, 0f, 1.2f)), _glow, 1f, 4, loop: true).Local().Rate(12f).Life(0.12f, 0.16f).Size(1.4f, 1.8f)
                .Colors(new Color(1f, 0.5f, 0.15f, 0.12f)).Over(Hold(Color.white)).Light(new Color(1f, 0.45f, 0.12f), 6f, 4f);

            var ignite = Child(root, "ignite");
            new Layer(ignite, _glow, 0.1f, 2).Local().Life(0.06f, 0.06f).Size(0.35f, 0.35f).Colors(new Color(1f, 0.7f, 0.35f, 0.8f)).Burst(0f, 1, 1).Over(Flash(Warm));
            new Layer(Child(ignite, "ignite_fire"), _fire, 0.1f, 3).Life(0.3f, 0.45f).Speed(2f, 4f).Size(0.3f, 0.4f).Spin().Cone(25f, 0.03f)
                .Burst(0f, 2, 3).Sheet(4, 4, animate: true).Over(FireToSoot(blueStart: true)).SizeOver(0.6f, 1.6f).Drag(2f);
            new Layer(Child(ignite, "ignite_sparks"), _glow, 0.1f, 8).Life(0.1f, 0.25f).Speed(4f, 8f).Size(0.01f, 0.016f).Cone(20f, 0.02f)
                .Burst(0f, 5, 8).Gravity(0.5f).Over(Sparkle()).Stretch(0.03f, 1f);
            ignite.SetActive(false);
        }

        /// <summary>A black hole of 8 m pull radius: a black event horizon with a dark shadow round it, drawn over a thin
        /// hot photon ring that carries the violet light; a tilted accretion disc spiralling in, a faint spiral of glowing
        /// gas, violet streaks and dark dust pulled in from 4.5 m and occasional crackles near the horizon.
        /// The inactive `open` child is the well opening (a flash, light and expanding ring at full size whatever the
        /// well's scale) and `collapse` its end (an inward ring and glow, then a flash, light, debris and sparks thrown
        /// out as it vanishes 0.28 s later).</summary>
        private static void BlackHole(GameObject root)
        {
            // A gravitational singularity with volume, round from every side: the black core sits inside a band of bent light (a screen-space warp),
            // under a violet-white event-horizon rim and a soft violet corona; two crossed accretion rings orbit it, streaks and dark dust spiral in from
            // 4.5 m and stretch as they fall, and crackles flicker at the horizon. No gold, no flat decal: every layer is a billboard or a 3D orbit.
            new Layer(root, _disc, 1f, 8, loop: true).Local().Rate(25f).Life(0.2f, 0.2f).Size(0.6f, 0.6f).Colors(new Color(0f, 0f, 0f, 1f))
                .Over(Hold(Color.white)).Fudge(-100f);
            new Layer(Child(root, "lens"), _lens, 1f, 4, loop: true).Local().Rate(12f).Life(0.4f, 0.4f).Size(1.6f, 1.7f).Spin().Turn(0.6f, 1f)
                .Colors(Color.white).Over(Hold(Color.white)).Fudge(-110f);
            new Layer(Child(root, "shadow"), _disc, 1f, 6, loop: true).Local().Rate(20f).Life(0.25f, 0.25f).Size(0.9f, 0.9f).Colors(new Color(0f, 0f, 0f, 0.35f))
                .Over(Hold(Color.white)).Fudge(-90f);
            new Layer(Child(root, "horizon"), _ringThin, 1f, 8, loop: true).Local().Rate(20f).Life(0.25f, 0.25f).Size(0.66f, 0.7f).Spin()
                .Colors(new Color(0.85f, 0.75f, 1f, 0.9f)).Over(Hold(Color.white)).Light(new Color(0.55f, 0.15f, 1f), 6f, 2f);
            new Layer(Child(root, "corona"), _glow, 1f, 6, loop: true).Local().Rate(12f).Life(0.5f, 0.5f).Size(1.5f, 1.7f)
                .Colors(new Color(0.5f, 0.2f, 1f, 0.22f)).Over(Hold(Color.white)).Fudge(10f);
            new Layer(Child(root, "swirl"), _spiral, 1f, 10, loop: true).Local().Rate(6f).Life(1.2f, 1.5f).Size(1.6f, 2f).Spin().Turn(1.6f, 2.4f)
                .Colors(new Color(0.75f, 0.45f, 1f), Violet).Over(Hold(Color.white, 0.09f));
            new Layer(Child(root, "accretion", Vector3.zero, new Vector3(70f, 0f, 0f)), _glow, 1f, 150, loop: true).Local().Rate(90f).Life(1.2f, 1.6f)
                .Size(0.05f, 0.1f).Circle(0.75f, 0.5f).Orbit(0f, 0f, 4f, -0.25f).Colors(new Color(0.9f, 0.8f, 1f), new Color(0.6f, 0.2f, 1f)).Over(Hold(Color.white));
            new Layer(Child(root, "accretion_cross", Vector3.zero, new Vector3(-35f, 60f, 0f)), _glow, 1f, 90, loop: true).Local().Rate(55f).Life(1f, 1.4f)
                .Size(0.035f, 0.07f).Circle(0.6f, 0.4f).Orbit(0f, 0f, -4.5f, -0.2f).Colors(new Color(0.7f, 0.5f, 1f), new Color(0.35f, 0.15f, 0.9f)).Over(Hold(Color.white));
            new Layer(Child(root, "infall"), _glow, 1f, 60, loop: true).Local().Rate(40f).Life(1f, 1.3f).Size(0.02f, 0.035f).Sphere(4.5f, 0f)
                .Orbit(0.8f, 0.8f, 0f, -4.2f).Colors(new Color(0.75f, 0.6f, 1f, 0.7f)).Over(Hold(Color.white, 0.8f)).Stretch(0.12f, 1f);
            new Layer(Child(root, "dust"), _debris, 1f, 30, loop: true).Local().Rate(20f).Life(1f, 1.3f).Size(0.03f, 0.06f).Spin().Turn(4f).Frames(16)
                .Sphere(4f, 0f).Orbit(0.4f, 0.4f, 0f, -3.5f).Colors(new Color(0.25f, 0.22f, 0.3f, 0.9f)).Over(Hold(Color.white));
            new Layer(Child(root, "arcs"), _crackle, 1f, 4, loop: true).Local().Rate(5f).Life(0.04f, 0.06f).Size(0.7f, 0.9f).Spin().Frames(16)
                .Colors(new Color(0.75f, 0.55f, 1f, 0.6f));

            var open = Child(root, "open");
            new Layer(open, _glow, 0.1f, 2).Keep().Life(0.12f, 0.12f).Size(3f, 3f).Colors(new Color(0.75f, 0.55f, 1f, 0.7f)).Burst(0f, 1, 1).Over(Flash(Violet));
            LightCarrier(open, "open_light", 0.4f, new Color(0.55f, 0.2f, 1f), 9f, 5f, keep: true);
            new Layer(Child(open, "open_ring"), _shockRing, 0.1f, 2).Keep().Life(0.35f, 0.35f).Size(1f, 1f).Spin().Colors(new Color(0.75f, 0.55f, 1f, 0.5f))
                .Burst(0f, 1, 1).Over(Fade()).SizeCurve(new AnimationCurve(new Keyframe(0f, 0.5f, 0f, 12f), new Keyframe(1f, 5f, 2f, 0f)));
            new Layer(Child(open, "open_arcs"), _crackle, 0.2f, 4).Keep().Life(0.05f, 0.08f).Size(1.2f, 1.6f).Spin().Frames(16)
                .Burst(0f, 1, 1).Burst(0.06f, 1, 1).Burst(0.12f, 1, 1).Colors(new Color(0.75f, 0.55f, 1f, 0.4f));
            open.SetActive(false);

            var collapse = Child(root, "collapse");
            new Layer(collapse, _glow, 0.1f, 2).Keep().Life(0.3f, 0.3f).Size(2.4f, 2.4f).Colors(new Color(0.7f, 0.5f, 1f, 0.6f)).Burst(0f, 1, 1)
                .Over(Ramp(new[] { (0f, Violet), (1f, Color.white) }, new[] { (0f, 0f), (0.6f, 1f), (1f, 0.5f) })).SizeOver(1f, 0.15f);
            new Layer(Child(collapse, "collapse_ring"), _shockRing, 0.1f, 2).Keep().Life(0.28f, 0.28f).Size(1f, 1f).Spin().Colors(new Color(0.7f, 0.5f, 1f, 0.5f))
                .Burst(0f, 1, 1).Over(Hold(Color.white)).SizeCurve(new AnimationCurve(new Keyframe(0f, 5f), new Keyframe(1f, 0.3f)));
            new Layer(Child(collapse, "collapse_flash"), _glow, 0.4f, 2).Keep().Delay(0.28f).Life(0.1f, 0.1f).Size(3f, 3f).Colors(new Color(0.85f, 0.75f, 1f, 0.8f))
                .Burst(0f, 1, 1).Over(Flash(Violet));
            LightCarrier(collapse, "collapse_light", 0.4f, new Color(0.7f, 0.45f, 1f), 8f, 5f, keep: true, delay: 0.28f);
            new Layer(Child(collapse, "collapse_debris"), _debris, 0.4f, 20).Keep().Delay(0.28f).Life(0.8f, 1.3f).Speed(3f, 8f).Size(0.03f, 0.07f)
                .Spin().Turn(8f).Sphere(0.3f).Frames(16).Burst(0f, 16, 20).Colors(new Color(0.3f, 0.28f, 0.33f)).Gravity(1f).Over(Fade()).Collide(0.25f, 0.5f, 0.2f);
            new Layer(Child(collapse, "collapse_sparks"), _glow, 0.4f, 30).Keep().Delay(0.28f).Life(0.2f, 0.5f).Speed(6f, 12f).Size(0.014f, 0.024f)
                .Sphere(0.3f).Burst(0f, 24, 30).Colors(new Color(0.85f, 0.7f, 1f)).Over(Fade()).Drag(2f).Gravity(0.5f).Stretch(0.04f, 1f);
            collapse.SetActive(false);
        }

        /// <summary>A charged plasma orb of 0.3 m radius: a roiling plasma flipbook carrying the light, a white-hot centre,
        /// a faint ionised shell, surface arcs, a ribbon trail and a haze left in the world behind a moving orb, and shed
        /// motes. Inactive state children, each activated by the player that holds the orb:
        /// `charging` (energy streaming in from a shell and arcs snapping to the core, while it charges at the muzzle);
        /// `charged` (at full charge: a flash with a light, a thin ring out to three diameters, a 2 Hz breath and heavier
        /// arcs until release); `launch` (left at the muzzle when the orb is fired: a flash, a light, a ring, sparks and
        /// ionised smoke); `fizzle` (the orb running out: crackles, sparks and smoke); `storm` (the shock orb: violet
        /// arcs and tendrils lashing out from the orb all the way).</summary>
        private static void PlasmaOrb(GameObject root)
        {
            new Layer(root, _plasma, 1f, 8, loop: true).Local().Rate(16f).Life(0.22f, 0.28f).Size(0.52f, 0.6f).Spin().Sheet(4, 4, animate: true, cycles: 2f)
                .Colors(new Color(0.45f, 0.75f, 1f, 0.5f)).Over(Hold(Color.white)).Light(new Color(0.25f, 0.55f, 1f), 4f, 1.4f);
            new Layer(Child(root, "centre"), _glow, 1f, 4, loop: true).Local().Rate(30f).Life(0.08f, 0.1f).Size(0.14f, 0.18f)
                .Colors(new Color(0.85f, 0.95f, 1f, 0.5f)).Over(Hold(Color.white));
            new Layer(Child(root, "shell"), _glow, 1f, 4, loop: true).Local().Rate(10f).Life(0.25f, 0.25f).Size(0.7f, 0.8f)
                .Colors(new Color(0.2f, 0.5f, 1f, 0.14f)).Over(Hold(Color.white));
            new Layer(Child(root, "arcs"), _crackle, 1f, 4, loop: true).Local().Rate(16f).Life(0.03f, 0.06f).Size(0.5f, 0.65f).Spin().Frames(16)
                .Colors(new Color(0.6f, 0.85f, 1f, 0.7f));
            new Layer(Child(root, "trail"), _trail, 1f, 2, loop: true).Local().Burst(0f, 1, 1).Life(1f, 1f).Size(0.45f, 0.45f)
                .Colors(new Color(1f, 1f, 1f, 0f)).Trail(_trail, 0.16f,
                    Ramp(new[] { (0f, new Color(0.7f, 0.9f, 1f)), (0.4f, new Color(0.25f, 0.55f, 1f)), (1f, new Color(0.4f, 0.2f, 1f)) }, new[] { (0f, 0.55f), (1f, 0f) }));
            new Layer(Child(root, "wake"), _smoke, 1f, 8, loop: true).Rate(10f).Life(0.4f, 0.6f).Size(0.25f, 0.3f).Spin().Frames(16).Sphere(0.1f)
                .Over(Ramp(new[] { (0f, new Color(0.3f, 0.55f, 1f)), (0.4f, new Color(0.08f, 0.1f, 0.2f)), (1f, Color.black) }, new[] { (0f, 0f), (0.15f, 0.1f), (1f, 0f) }))
                .SizeOver(1f, 2.2f).Fudge(10f);
            new Layer(Child(root, "motes"), _glow, 1f, 10, loop: true).Rate(18f).Life(0.3f, 0.5f).Speed(0.3f, 1f).Size(0.018f, 0.03f).Sphere(0.15f)
                .Colors(new Color(0.6f, 0.85f, 1f, 0.7f)).Over(Hold(Color.white)).Noise(0.5f, 2f);

            var charging = Child(root, "charging");
            new Layer(charging, _glow, 0.5f, 16, loop: true).Local().Rate(40f).Life(0.28f, 0.32f).Size(0.03f, 0.045f).Sphere(1.6f, 0f).Orbit(0.6f, 0.6f, 0f, -5f)
                .Colors(new Color(0.65f, 0.88f, 1f)).Over(Ramp(new[] { (0f, Plasma), (1f, Color.white) }, new[] { (0f, 0f), (0.3f, 0.6f), (1f, 1f) })).Stretch(0.06f, 1f);
            new Layer(Child(charging, "charging_arcs"), _crackle, 0.5f, 3, loop: true).Local().Rate(8f).Life(0.03f, 0.05f).Size(0.9f, 1.2f).Spin().Frames(16)
                .Colors(new Color(0.55f, 0.82f, 1f, 0.35f));
            charging.SetActive(false);

            var charged = Child(root, "charged");
            new Layer(charged, _glow, 0.1f, 2).Local().Life(0.1f, 0.1f).Size(0.7f, 0.7f).Colors(new Color(0.7f, 0.88f, 1f, 0.6f)).Burst(0f, 1, 1)
                .Over(Ramp(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 1f), (0.7f, 1f), (1f, 0f) }));
            LightCarrier(charged, "charged_light", 0.2f, new Color(0.25f, 0.55f, 1f), 3f, 2.5f);
            new Layer(Child(charged, "charged_ring"), _shockRing, 0.1f, 2).Local().Life(0.3f, 0.3f).Size(0.67f, 0.67f).Spin()
                .Colors(new Color(0.45f, 0.8f, 1f, 0.35f)).Burst(0f, 1, 1)
                .Over(Ramp(new[] { (0f, Color.white), (1f, Plasma) }, new[] { (0f, 0f), (0.05f, 1f), (0.62f, 0.6f), (1f, 0f) }))
                .SizeCurve(new AnimationCurve(new Keyframe(0f, 1f, 0f, 5f), new Keyframe(0.62f, 2.2f, 1f, 0.4f), new Keyframe(1f, 2.4f)));
            // One pulse every 0.5 s (a burst per loop of a 0.5 s layer), fading in and out over its life: a 2 Hz breath.
            new Layer(Child(charged, "charged_pulse"), _glow, 0.5f, 2, loop: true).Local().Delay(0.15f).Burst(0f, 1, 1).Life(0.5f, 0.5f).Size(0.95f, 0.95f)
                .Colors(new Color(0.4f, 0.75f, 1f, 0.2f))
                .Over(Ramp(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 0f), (0.5f, 1f), (1f, 0f) })).SizeOver(0.9f, 1.15f);
            new Layer(Child(charged, "charged_arcs"), _crackle, 0.5f, 3, loop: true).Local().Rate(10f).Life(0.03f, 0.05f).Size(1f, 1.3f).Spin().Frames(16)
                .Colors(new Color(0.65f, 0.9f, 1f, 0.7f));
            charged.SetActive(false);

            var launch = Child(root, "launch");
            new Layer(launch, _flash, 0.1f, 2).Life(0.04f, 0.055f).Size(1f, 1.2f).Spin().Frames(4).Colors(new Color(0.6f, 0.85f, 1f)).Burst(0f, 1, 1).Over(Flash(Plasma));
            new Layer(Child(launch, "launch_glow"), _glow, 0.1f, 2).Life(0.12f, 0.12f).Size(1.4f, 1.4f).Colors(new Color(0.5f, 0.8f, 1f, 0.3f)).Burst(0f, 1, 1)
                .Over(Flash(Plasma)).Light(new Color(0.25f, 0.55f, 1f), 5f, 3f);
            new Layer(Child(launch, "launch_ring"), _shockRing, 0.1f, 2).Life(0.15f, 0.15f).Size(1f, 1f).Spin().Colors(new Color(0.5f, 0.8f, 1f, 0.3f)).Burst(0f, 1, 1)
                .Over(Fade()).SizeCurve(new AnimationCurve(new Keyframe(0f, 0.4f, 0f, 8f), new Keyframe(1f, 1.6f, 1f, 0f)));
            new Layer(Child(launch, "launch_sparks"), _glow, 0.1f, 14).Life(0.12f, 0.25f).Speed(3f, 6f).Size(0.02f, 0.03f).Sphere(0.3f).Burst(0f, 10, 14)
                .Over(Arcing()).Drag(2f).Stretch(0.04f, 1f);
            new Layer(Child(launch, "launch_smoke"), _smoke, 0.1f, 3).Life(0.6f, 0.9f).Speed(0.3f, 0.8f).Size(0.5f, 0.7f).Spin().Turn(0.6f).Sphere(0.2f).Frames(16)
                .Burst(0f, 2, 3).Over(Ramp(new[] { (0f, new Color(0.35f, 0.6f, 1f)), (0.2f, new Color(0.1f, 0.11f, 0.15f)), (1f, Color.black) }, new[] { (0f, 0f), (0.1f, 0.3f), (1f, 0f) }))
                .SizeOver(0.8f, 2f).Gravity(-0.05f).Fudge(10f);
            launch.SetActive(false);

            var fizzle = Child(root, "fizzle");
            new Layer(fizzle, _crackle, 0.2f, 4).Life(0.04f, 0.07f).Size(0.7f, 0.95f).Spin().Frames(16).Burst(0f, 1, 1).Burst(0.06f, 1, 1).Burst(0.12f, 1, 1)
                .Colors(new Color(0.7f, 0.8f, 1f, 0.8f));
            new Layer(Child(fizzle, "fizzle_glow"), _glow, 0.1f, 2).Life(0.12f, 0.12f).Size(0.9f, 0.9f).Colors(new Color(0.5f, 0.6f, 1f, 0.6f)).Burst(0f, 1, 1).Over(Flash(Violet));
            new Layer(Child(fizzle, "fizzle_sparks"), _glow, 0.1f, 12).Life(0.15f, 0.35f).Speed(2f, 5f).Size(0.015f, 0.025f).Sphere(0.2f).Burst(0f, 8, 12)
                .Gravity(0.5f).Over(Arcing()).Stretch(0.04f, 1f);
            new Layer(Child(fizzle, "fizzle_smoke"), _smoke, 0.1f, 2).Life(0.7f, 1f).Speed(0.2f, 0.5f).Size(0.4f, 0.55f).Spin().Frames(16).Burst(0f, 2, 2)
                .Over(Soot(0.3f, 0.1f)).SizeOver(0.8f, 2f).Gravity(-0.05f).Fudge(10f);
            fizzle.SetActive(false);

            var storm = Child(root, "storm");
            new Layer(storm, _crackle, 1f, 4, loop: true).Local().Rate(22f).Life(0.03f, 0.05f).Size(0.6f, 0.8f).Spin().Frames(16).Colors(new Color(0.75f, 0.6f, 1f, 0.5f));
            new Layer(Child(storm, "storm_tendrils"), _bolt, 1f, 4, loop: true).Line(_line).Splay(Mathf.PI).Size3(0.18f, 0.24f, 0.7f, 1.2f).Frames(8)
                .Rate(12f).Life(0.03f, 0.05f).Colors(new Color(0.8f, 0.7f, 1f, 0.8f));
            new Layer(Child(storm, "storm_glow"), _glow, 1f, 4, loop: true).Local().Rate(8f).Life(0.25f, 0.25f).Size(1.2f, 1.3f)
                .Colors(new Color(0.55f, 0.3f, 1f, 0.12f)).Over(Hold(Color.white));
            storm.SetActive(false);
        }

        // ---- line effects --------------------------------------------------------------------------------------

        /// <summary>A held amber energy beam: a white-hot core, an amber halo, a flowing band and ionised motes drifting
        /// off along the line; a small flickering muzzle glow (small, so the first-person view stays clear); at the far
        /// end a glow carrying the light, a ragged flicker, sparks and molten drops thrown back and falling, and smoke
        /// lit from inside. The end layers are named `impact*` so a player can switch them off while the beam hits
        /// nothing. The inactive `ignite` child is the beam striking: a muzzle flash and a few sparks.</summary>
        private static void EnergyBeam(GameObject root)
        {
            new Layer(root, _beam, 1f, 8, loop: true).Line(_line).Rate(40f).Life(0.05f, 0.07f).Size3(0.035f, 0.05f).Colors(new Color(1f, 0.93f, 0.8f))
                .Over(Hold(Color.white));
            new Layer(Child(root, "halo"), _beam, 1f, 8, loop: true).Line(_line).Rate(35f).Life(0.07f, 0.09f).Size3(0.18f, 0.24f)
                .Colors(new Color(Amber.r, Amber.g, Amber.b, 0.32f)).Over(Hold(Color.white));
            new Layer(Child(root, "flow"), _flow, 1f, 8, loop: true).Line(_line).Rate(30f).Life(0.08f, 0.1f).Size3(0.1f, 0.1f)
                .Colors(new Color(1f, 0.65f, 0.25f, 0.55f)).Over(Hold(Color.white)).Sheet(4, 1, animate: true, cycles: 2f);
            new Layer(Child(root, "ions"), _glow, 1f, 24, loop: true).Along().Rate(30f).Life(0.25f, 0.5f).Speed(0.05f, 0.25f).Size(0.01f, 0.018f)
                .Noise(0.4f, 2f).Over(Ramp(new[] { (0f, new Color(1f, 0.85f, 0.55f)), (1f, Cool) }, new[] { (0f, 0f), (0.15f, 0.8f), (1f, 0f) }));
            new Layer(Child(root, "muzzle", Vector3.zero), _glow, 1f, 4, loop: true).Local().Keep().Rate(30f).Life(0.05f, 0.07f).Size(0.07f, 0.1f).Spin()
                .Colors(new Color(1f, 0.75f, 0.4f, 0.7f)).Over(Hold(Color.white));
            new Layer(Child(root, "muzzle_flash", Vector3.zero), _flash, 1f, 4, loop: true).Local().Keep().Rate(25f).Life(0.03f, 0.05f).Size(0.09f, 0.13f).Spin()
                .Frames(4).Colors(new Color(1f, 0.75f, 0.4f, 0.6f));
            new Layer(Child(root, "impact", Vector3.forward), _glow, 1f, 4, loop: true).Local().Keep().Rate(30f).Life(0.05f, 0.08f).Size(0.32f, 0.42f).Spin()
                .Colors(new Color(1f, 0.6f, 0.2f, 0.85f)).Over(Hold(Color.white)).Light(new Color(1f, 0.5f, 0.15f), 4.5f, 2.5f);
            new Layer(Child(root, "impact_flash", Vector3.forward), _flash, 1f, 4, loop: true).Local().Keep().Rate(25f).Life(0.03f, 0.05f).Size(0.28f, 0.38f).Spin()
                .Frames(4).Colors(new Color(1f, 0.75f, 0.4f, 0.8f));
            new Layer(Child(root, "impact_sparks", Vector3.forward), _glow, 1f, 36, loop: true).Keep().Rate(55f).Life(0.15f, 0.35f).Speed(2f, 5f)
                .Size(0.01f, 0.022f).Cone(55f, 0.02f, new Vector3(0f, 180f, 0f)).Gravity(1f).Over(Cooling()).Collide(0.3f, 0.5f, 0.3f).Stretch(0.03f, 1f);
            new Layer(Child(root, "impact_melt", Vector3.forward), _glow, 1f, 10, loop: true).Keep().Rate(12f).Life(0.4f, 0.8f).Speed(1f, 2.5f)
                .Size(0.02f, 0.032f).Cone(40f, 0.02f, new Vector3(0f, 180f, 0f)).Gravity(1.2f).Colors(new Color(1f, 0.7f, 0.3f)).Over(Cooling())
                .Collide(0.2f, 0.6f, 0.1f);
            new Layer(Child(root, "impact_smoke", Vector3.forward), _smoke, 1f, 12, loop: true).Keep().Rate(8f).Life(0.8f, 1.3f).Speed(0.3f, 0.6f)
                .Size(0.2f, 0.28f).Spin().Turn(0.6f).Frames(16).Cone(30f, 0.02f, new Vector3(0f, 180f, 0f)).Over(Soot(0.35f, 0.1f, warm: true))
                .SizeOver(1f, 3f).Gravity(-0.12f).Fudge(10f);

            var ignite = Child(root, "ignite");
            new Layer(ignite, _flash, 0.1f, 2).Local().Keep().Life(0.05f, 0.06f).Size(0.28f, 0.32f).Spin().Frames(4).Colors(new Color(1f, 0.8f, 0.5f))
                .Burst(0f, 1, 1).Over(Flash(Warm));
            new Layer(Child(ignite, "ignite_sparks"), _glow, 0.1f, 8).Keep().Life(0.08f, 0.2f).Speed(3f, 6f).Size(0.01f, 0.016f).Cone(25f, 0.02f)
                .Burst(0f, 5, 8).Over(Sparkle()).Stretch(0.03f, 1f);
            ignite.SetActive(false);
        }

        /// <summary>A chain-lightning strike, played as a laser line (slot <c>laser</c>: head <c>end</c>, bodies <c>bolts</c> and <c>bolts_thin</c>,
        /// <c>tile</c> 8): the bodies repeat <see cref="BoltTile"/> every eight metres, so the bolt keeps its corners (one every 12-60 cm,
        /// wandering up to about 60 cm off the line, 1.7 m across the main bolt) at any length instead of being stretched straight; re-struck five times
        /// in 0.2 s, each strike a random one of eight bolt shapes, of varying thickness, with forks. At the far end
        /// (the head, hidden when the arc hits nothing) a flash, crackles, blue-white sparks that fall and bounce, and a wisp of
        /// smoke (no light of its own: the caller flashes one at each hop); a small flash and crackle at the start.</summary>
        private static void LightningArc(GameObject root)
        {
            // the bodies are stretched billboards along the line (they always face the camera, so the bolt never turns edge-on into a straight
            // line); one particle drifts along +Z, so its length is its size times the length scale, which the laser line scales with its length
            new Layer(Child(root, "bolts"), _boltTile, 0.22f, 8).Local().Burst(0f, 1, 1).Burst(0.05f, 1, 1).Burst(0.1f, 1, 1).Burst(0.15f, 1, 1).Burst(0.19f, 1, 1)
                .Life(0.035f, 0.06f).Speed(0.001f, 0.001f).Size(1.7f, 1.7f).Stretch(0f, 1f / 1.7f).Sheet(1, 8, animate: false).Colors(new Color(0.85f, 0.93f, 1f));
            new Layer(Child(root, "bolts_thin"), _boltTile, 0.22f, 6).Local().Burst(0.025f, 1, 1).Burst(0.08f, 1, 1).Burst(0.13f, 1, 1)
                .Life(0.03f, 0.05f).Speed(0.001f, 0.001f).Size(1f, 1f).Stretch(0f, 1f).Sheet(1, 8, animate: false).Colors(new Color(0.55f, 0.78f, 1f, 0.75f));
            // a luminous channel round the bolt, held through the strikes: seen from behind the gun the arc is a bright streak from the muzzle
            // to the target, not a few thin lines collapsing towards the crosshair
            new Layer(Child(root, "channel"), _beam, 0.22f, 2).Local().Burst(0f, 1, 1).Life(0.24f, 0.24f).Speed(0.001f, 0.001f).Size(0.32f, 0.32f)
                .Stretch(0f, 1f / 0.32f).Colors(new Color(0.45f, 0.7f, 1f, 0.5f)).Over(Ramp(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 1f), (0.5f, 0.7f), (1f, 0f) }));
            var end = Child(root, "end", Vector3.forward);
            new Layer(Child(end, "end_flash"), _glow, 0.1f, 2).Local().Keep().Burst(0f, 1, 1).Life(0.12f, 0.12f).Size(0.6f, 0.65f)
                .Colors(new Color(0.55f, 0.82f, 1f)).Over(Flash(Electric));
            new Layer(Child(end, "end_core"), _glow, 0.1f, 2).Local().Keep().Burst(0f, 1, 1).Life(0.07f, 0.07f).Size(0.18f, 0.2f)
                .Colors(Color.white).Over(Fade());
            // the strike splashes over the target: jagged bolts thrown out in every direction (stretched along their flight, so never a flat star)
            new Layer(Child(end, "end_splash"), _bolt, 0.15f, 12).Keep().Burst(0f, 5, 6).Burst(0.06f, 3, 4).Burst(0.12f, 2, 3).Life(0.04f, 0.07f)
                .Speed(5f, 9f).Size(0.05f, 0.08f).Sphere(0.05f).Frames(8).Stretch(0.06f, 2f).Colors(new Color(0.65f, 0.88f, 1f, 0.9f));
            new Layer(Child(end, "end_sparks"), _glow, 0.1f, 18).Keep().Burst(0f, 12, 16).Life(0.15f, 0.4f).Speed(2f, 6f)
                .Size(0.01f, 0.02f).Sphere(0.05f).Gravity(0.7f).Over(Arcing()).Collide(0.3f, 0.5f, 0.3f).Stretch(0.03f, 1f);
            new Layer(Child(end, "end_smoke"), _smoke, 0.1f, 3).Keep().Burst(0f, 1, 2).Life(0.7f, 1f).Speed(0.2f, 0.4f).Size(0.15f, 0.22f)
                .Spin().Frames(16).Sphere(0.04f).Over(Soot(0.25f, 0.12f)).SizeOver(1f, 2.5f).Gravity(-0.1f).Fudge(10f);
            // at the muzzle, half a metre from the eye: a small glow and short bolts licking forward along the shot, nothing that fills the view
            new Layer(root, _glow, 0.1f, 2).Local().Keep().Burst(0f, 1, 1).Life(0.06f, 0.06f).Size(0.08f, 0.08f)
                .Colors(new Color(0.5f, 0.8f, 1f, 0.8f)).Over(Flash(Electric));
            new Layer(Child(root, "start_spray", Vector3.zero), _bolt, 0.1f, 6).Keep().Burst(0f, 3, 4).Burst(0.05f, 2, 2).Life(0.03f, 0.05f).Speed(3f, 5f)
                .Size(0.025f, 0.035f).Cone(28f, 0.005f).Frames(8).Stretch(0.04f, 2f).Colors(new Color(0.65f, 0.88f, 1f, 0.9f));
        }

        /// <summary>A plasma discharge from an orb: thinner violet-blue bolts than the lightning arc, a faint glow along
        /// the line, and at the far end a glow with the light, a small crackle and a few sparks.</summary>
        private static void PlasmaDischarge(GameObject root)
        {
            new Layer(root, _bolt, 0.22f, 6).Line(_line).Burst(0f, 2, 2).Burst(0.07f, 1, 1).Burst(0.14f, 1, 1).Life(0.04f, 0.06f).Size3(0.1f, 0.14f)
                .Roll().Frames(8).Colors(new Color(0.7f, 0.75f, 1f));
            new Layer(Child(root, "glow"), _beam, 0.22f, 2).Line(_line).Burst(0f, 1, 1).Life(0.2f, 0.2f).Size3(0.12f, 0.12f)
                .Colors(new Color(0.45f, 0.5f, 1f, 0.3f)).Over(Fade());
            new Layer(Child(root, "end_glow", Vector3.forward), _glow, 0.1f, 2).Local().Keep().Burst(0f, 1, 1).Life(0.15f, 0.15f).Size(0.3f, 0.3f)
                .Colors(new Color(0.5f, 0.6f, 1f)).Over(Flash(Violet)).Light(new Color(0.4f, 0.45f, 1f), 3f, 2f);
            new Layer(Child(root, "end_crackle", Vector3.forward), _crackle, 0.1f, 2).Local().Keep().Burst(0f, 1, 1).Life(0.05f, 0.07f)
                .Size(0.3f, 0.4f).Spin().Frames(16).Colors(new Color(0.65f, 0.7f, 1f));
            new Layer(Child(root, "end_sparks", Vector3.forward), _glow, 0.1f, 10).Keep().Burst(0f, 5, 8).Life(0.1f, 0.25f).Speed(1.5f, 4f)
                .Size(0.01f, 0.016f).Sphere(0.04f).Gravity(0.5f).Over(Arcing()).Stretch(0.03f, 1f);
        }

        /// <summary>A grenade-launcher shot from the muzzle to the contact point: a hot slug of light running the whole
        /// line in 55 ms over a fading core and orange halo, a smoke trail left along it; at the muzzle a ragged flash, a
        /// glow, a short light, a few sparks and a smoke puff lit from inside.</summary>
        private static void BlastTracer(GameObject root)
        {
            new Layer(root, _beam, 0.1f, 2).Line(_line).Burst(0f, 1, 1).Life(0.06f, 0.06f).Size3(0.04f, 0.04f).Colors(new Color(1f, 0.85f, 0.55f, 0.7f))
                .Over(Fade());
            new Layer(Child(root, "slug"), _beam, 0.1f, 2).Line(_line).Burst(0f, 1, 1).Life(0.055f, 0.055f).Speed(16f, 16f).Size3(0.09f, 0.09f, 0.18f)
                .Colors(new Color(1f, 0.9f, 0.7f)).Over(Fade());
            new Layer(Child(root, "halo"), _beam, 0.1f, 2).Line(_line).Burst(0f, 1, 1).Life(0.09f, 0.09f).Size3(0.26f, 0.26f)
                .Colors(new Color(1f, 0.4f, 0.12f, 0.35f)).Over(Fade());
            new Layer(Child(root, "trail"), _smoke, 0.1f, 16).Along().Delay(0.02f).Burst(0f, 12, 16).Life(0.7f, 1.2f).Speed(0f, 0.15f).Size(0.1f, 0.16f)
                .Spin().Turn(0.4f).Frames(16).Over(Soot(0.22f, 0.12f, warm: true)).SizeOver(1f, 3f).Gravity(-0.02f).Fudge(10f);
            new Layer(Child(root, "muzzle", Vector3.zero), _flash, 0.1f, 2).Local().Keep().Burst(0f, 1, 1).Life(0.05f, 0.05f).Size(0.35f, 0.45f).Spin()
                .Frames(4).Colors(new Color(1f, 0.75f, 0.4f)).Over(Flash(Warm));
            new Layer(Child(root, "muzzle_glow", Vector3.zero), _glow, 0.1f, 2).Local().Keep().Burst(0f, 1, 1).Life(0.06f, 0.06f).Size(0.3f, 0.35f)
                .Colors(new Color(1f, 0.7f, 0.35f, 0.7f)).Over(Flash(Warm));
            LightCarrier(Child(root, "muzzle_light", Vector3.zero), null, 0.12f, new Color(1f, 0.55f, 0.2f), 4f, 3f, keep: true);
            new Layer(Child(root, "muzzle_sparks", Vector3.zero), _glow, 0.1f, 10).Keep().Burst(0f, 6, 10).Life(0.08f, 0.18f).Speed(4f, 8f).Size(0.01f, 0.016f)
                .Cone(15f, 0.02f).Over(Sparkle()).Stretch(0.03f, 1f);
            new Layer(Child(root, "muzzle_smoke", Vector3.zero), _smoke, 0.1f, 3).Keep().Burst(0f, 2, 3).Life(0.6f, 0.9f).Speed(0.4f, 1f).Size(0.12f, 0.18f)
                .Spin().Turn(0.6f).Frames(16).Cone(20f, 0.02f).Over(Soot(0.28f, 0.12f, warm: true)).SizeOver(1f, 2.6f).Fudge(10f);
        }

        // ---- the layer builder ---------------------------------------------------------------------------------

        /// <summary>A light that is not seen, only cast: one invisible particle that carries a particle light and fades it
        /// out over <paramref name="life"/> seconds (full at once, a third left by a fifth of its life). A one-shot.</summary>
        // ---- effects declared as data (Tools/Vfx/FxLibrary/fx-own.json) ---------------------------------------

#pragma warning disable 0649 // JsonUtility fills these fields.
        /// <summary>An own effect as data: its layers, each a child of the root (the converter gives the root the silent system the game plays), so every layer has its own
        /// <c>position</c> and can be left out by name. Read by ForgeVfxConvert from the manifest fx_import.py writes.</summary>
        [Serializable] internal sealed class Recipe { public RecipeLayer[] layers; }

        /// <summary>A key of a colour/alpha (<c>c</c> "#rrggbb", <c>a</c>) or size (<c>v</c>) curve at life fraction <c>t</c>.</summary>
        [Serializable] internal sealed class RecipeKey { public float t, a = 1f, v = 1f; public string c; }

        [Serializable] internal sealed class RecipeBurst { public float t; public int min = 1, max; }

        /// <summary>One particle layer. <c>material</c> "own:&lt;Own/Materials name&gt;" or <c>texture</c> (a staged library texture) with <c>blend</c> and <c>tint</c>;
        /// <c>grid</c> [columns, rows] with <c>animate</c> (play the frames) or a random frame; <c>render</c> billboard | stretch (<c>length</c>, <c>velocity</c>) | horizontal;
        /// <c>life</c>, <c>size</c>, <c>speed</c> [min, max]; <c>shape</c> point | sphere | hemisphere | cone (<c>angle</c>) | circle with <c>radius</c>; <c>colour</c> and
        /// <c>sizeCurve</c> keys; <c>spin</c> degrees a second; <c>position</c> of the layer; <c>rotation</c> (degrees) turns a cone (+Z by default) or a circle (in the XY plane by default).</summary>
        [Serializable]
        internal sealed class RecipeLayer
        {
            public string name, material, texture, blend, render, shape, start;
            public int[] grid;
            public bool animate, loop, local, randomRotation;
            public int max;
            public float duration, delay, gravity, drag, rate, radius, angle, length = 1f, velocity, spin, tint;
            public float[] life, size, speed, position, rotation, noise;
            public RecipeBurst[] bursts;
            public RecipeKey[] colour, sizeCurve;
        }
#pragma warning restore 0649

        /// <summary>The effect a recipe declares, built with the same layer tools as the hand-written own effects. <paramref name="material"/> gives each layer's material.</summary>
        internal static GameObject FromRecipe(string name, Recipe recipe, Func<RecipeLayer, int, Material> material)
        {
            var root = new GameObject(name);
            for (var i = 0; i < recipe.layers.Length; i++)
            {
                var l = recipe.layers[i];
                var go = Child(root, string.IsNullOrEmpty(l.name) ? "layer" + i : l.name, V3(l.position));
                if (l.render == "line")
                {
                    RecipeLine(go, material(l, i), l);
                    continue;
                }
                var layer = new Layer(go, material(l, i), l.duration > 0f ? l.duration : 1f, l.max > 0 ? l.max : 64, l.loop);
                layer.Life(Lo(l.life, 1f), Hi(l.life, 1f)).Size(Lo(l.size, 1f), Hi(l.size, 1f)).Speed(Lo(l.speed, 0f), Hi(l.speed, 0f)).Gravity(l.gravity);
                if (!string.IsNullOrEmpty(l.start) && ColorUtility.TryParseHtmlString(l.start, out var start)) layer.Colors(start);
                if (l.delay > 0f) layer.Delay(l.delay);
                if (l.local) layer.Local();
                if (l.rate > 0f) layer.Rate(l.rate);
                foreach (var b in l.bursts ?? new RecipeBurst[0]) layer.Burst(b.t, (short)b.min, (short)Mathf.Max(b.min, b.max));
                switch (l.shape)
                {
                    case null: case "": case "point": break;
                    case "sphere": layer.Sphere(Mathf.Max(0.0001f, l.radius)); break;
                    case "hemisphere": layer.Sphere(Mathf.Max(0.0001f, l.radius)).Shape(ParticleSystemShapeType.Hemisphere); break;
                    case "cone": layer.Cone(l.angle, Mathf.Max(0.0001f, l.radius), V3(l.rotation)); break;
                    case "circle": layer.Circle(Mathf.Max(0.0001f, l.radius), 1f, V3(l.rotation)); break;
                    default: throw new InvalidOperationException("recipe layer " + l.name + " has an unknown shape " + l.shape);
                }
                if (l.colour != null && l.colour.Length > 0)
                {
                    var colours = new List<(float, Color)>();
                    var alphas = new List<(float, float)>();
                    foreach (var k in l.colour)
                    {
                        if (!string.IsNullOrEmpty(k.c) && ColorUtility.TryParseHtmlString(k.c, out var c)) colours.Add((k.t, c));
                        alphas.Add((k.t, k.a));
                    }
                    if (colours.Count == 0) colours.Add((0f, Color.white));
                    layer.Over(Ramp(colours.ToArray(), alphas.ToArray()));
                }
                if (l.sizeCurve != null && l.sizeCurve.Length > 0)
                {
                    var curve = new AnimationCurve();
                    foreach (var k in l.sizeCurve) curve.AddKey(k.t, k.v);
                    layer.SizeCurve(curve);
                }
                if (l.randomRotation) layer.Spin();
                if (Mathf.Abs(l.spin) > 0f) layer.Turn(Mathf.Abs(l.spin) * Mathf.Deg2Rad);
                if (l.drag > 0f) layer.Drag(l.drag);
                if (l.noise != null && l.noise.Length == 2) layer.Noise(l.noise[0], l.noise[1]);
                if (l.grid != null && l.grid.Length == 2) layer.Sheet(l.grid[0], l.grid[1], l.animate);
                switch (l.render)
                {
                    case null: case "": case "billboard": break;
                    case "stretch": layer.Stretch(l.velocity, l.length); break;
                    case "horizontal": layer.Align(ParticleSystemRenderMode.HorizontalBillboard); break;
                    default: throw new InvalidOperationException("recipe layer " + l.name + " has an unknown render " + l.render);
                }
            }
            return root;
        }

        /// <summary>`render: line`: a camera-facing strip from z = 0 to z = 1 (a LineRenderer in local space), so a line effect stretched along +Z is a ribbon that
        /// never shows its edge or a crossing, and is culled by the whole line's bounds. <c>size</c> [start, end] is its width in metres (ForgeVisual widens it by the
        /// copy's scale), <c>colour</c> keys run along it, <c>start</c> tints it.</summary>
        private static void RecipeLine(GameObject go, Material material, RecipeLayer l)
        {
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = false;
            line.positionCount = 2;
            line.SetPositions(new[] { Vector3.zero, Vector3.forward });
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.widthMultiplier = 1f;
            line.widthCurve = AnimationCurve.Linear(0f, Lo(l.size, 0.1f), 1f, Hi(l.size, 0.1f));
            var colours = new List<GradientColorKey>();
            var alphas = new List<GradientAlphaKey>();
            foreach (var k in l.colour ?? new RecipeKey[0])
            {
                if (!string.IsNullOrEmpty(k.c) && ColorUtility.TryParseHtmlString(k.c, out var c)) colours.Add(new GradientColorKey(c, k.t));
                alphas.Add(new GradientAlphaKey(k.a, k.t));
            }
            var tint = !string.IsNullOrEmpty(l.start) && ColorUtility.TryParseHtmlString(l.start, out var s) ? s : Color.white;
            if (colours.Count == 0) colours.Add(new GradientColorKey(tint, 0f));
            if (alphas.Count == 0) alphas.Add(new GradientAlphaKey(1f, 0f));
            line.colorGradient = new Gradient { colorKeys = colours.ToArray(), alphaKeys = alphas.ToArray() };
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private static float Lo(float[] range, float fallback) => range == null || range.Length == 0 ? fallback : range[0];
        private static float Hi(float[] range, float fallback) => range == null || range.Length == 0 ? fallback : range[range.Length - 1];
        private static Vector3 V3(float[] v) => v != null && v.Length == 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;

        private static void LightCarrier(GameObject parent, string name, float life, Color color, float range, float intensity, bool keep = false, float delay = 0f)
        {
            var go = name == null ? parent : Child(parent, name);
            var layer = new Layer(go, _glow, 0.1f, 1).Local().Life(life, life).Size(0.001f, 0.001f).Colors(new Color(1f, 1f, 1f, 1f)).Burst(0f, 1, 1)
                .Over(Decay()).Light(color, range, intensity);
            if (keep) layer.Keep();
            if (delay > 0f) layer.Delay(delay);
        }

        /// <summary>One particle layer: a ParticleSystem on its own GameObject, configured fluently. Defaults: World
        /// simulation, Hierarchy scaling, no shape, no emission, a billboard renderer without shadows or probes.</summary>
        private sealed class Layer
        {
            private readonly GameObject _go;
            private readonly ParticleSystem _system;
            private readonly ParticleSystemRenderer _renderer;
            private readonly List<ParticleSystem.Burst> _bursts = new List<ParticleSystem.Burst>();

            internal Layer(GameObject go, Material material, float duration, int maxParticles, bool loop = false)
            {
                _go = go;
                _system = go.AddComponent<ParticleSystem>();
                _renderer = go.GetComponent<ParticleSystemRenderer>();
                _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = _system.main;
                main.duration = duration;
                main.loop = loop;
                main.playOnAwake = false;
                main.maxParticles = maxParticles;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.stopAction = ParticleSystemStopAction.None;
                main.startSpeed = 0f;
                var emission = _system.emission;
                emission.rateOverTime = 0f;
                var shape = _system.shape;
                shape.enabled = false;
                _renderer.renderMode = ParticleSystemRenderMode.Billboard;
                _renderer.sharedMaterial = material;
                _renderer.shadowCastingMode = ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
                _renderer.lightProbeUsage = LightProbeUsage.Off;
                _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                _renderer.maxParticleSize = 4f;
            }

            internal Layer Local() { var main = _system.main; main.simulationSpace = ParticleSystemSimulationSpace.Local; return this; }

            /// <summary>Local scaling: an end layer of a line member keeps its own size however long the line is.</summary>
            internal Layer Keep() { var main = _system.main; main.scalingMode = ParticleSystemScalingMode.Local; return this; }

            internal Layer Delay(float seconds) { var main = _system.main; main.startDelay = seconds; return this; }

            internal Layer Life(float minimum, float maximum) { var main = _system.main; main.startLifetime = Range(minimum, maximum); return this; }

            internal Layer Speed(float minimum, float maximum) { var main = _system.main; main.startSpeed = Range(minimum, maximum); return this; }

            internal Layer Size(float minimum, float maximum) { var main = _system.main; main.startSize = Range(minimum, maximum); return this; }

            /// <summary>A line mesh's width (x and y) and length (z, one metre unless given).</summary>
            internal Layer Size3(float minimum, float maximum, float length = 1f) => Size3(minimum, maximum, length, length);

            internal Layer Size3(float minimum, float maximum, float lengthMinimum, float lengthMaximum)
            {
                var main = _system.main;
                main.startSize3D = true;
                main.startSizeX = Range(minimum, maximum);
                main.startSizeY = Range(minimum, maximum);
                main.startSizeZ = Range(lengthMinimum, lengthMaximum);
                return this;
            }

            internal Layer Spin() { var main = _system.main; main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f); return this; }

            /// <summary>A line mesh rolled to a random angle about the line, so re-strikes do not overlap.</summary>
            internal Layer Roll()
            {
                var main = _system.main;
                main.startRotation3D = true;
                main.startRotationX = 0f;
                main.startRotationY = 0f;
                main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                return this;
            }

            /// <summary>Side branches: a line mesh tilted off the line by up to the given angle (radians); π points it
            /// anywhere, for tendrils thrown out from a point.</summary>
            internal Layer Splay(float radians)
            {
                var main = _system.main;
                main.startRotation3D = true;
                main.startRotationX = new ParticleSystem.MinMaxCurve(-radians, radians);
                main.startRotationY = new ParticleSystem.MinMaxCurve(-radians, radians);
                main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                return this;
            }

            internal Layer Colors(Color color) { var main = _system.main; main.startColor = color; return this; }

            internal Layer Colors(Color a, Color b) { var main = _system.main; main.startColor = new ParticleSystem.MinMaxGradient(a, b); return this; }

            internal Layer Gravity(float modifier) { var main = _system.main; main.gravityModifier = modifier; return this; }

            internal Layer Rate(float perSecond) { var emission = _system.emission; emission.rateOverTime = perSecond; return this; }

            internal Layer Burst(float time, short minimum, short maximum)
            {
                _bursts.Add(new ParticleSystem.Burst(time, minimum, maximum));
                var emission = _system.emission;
                emission.SetBursts(_bursts.ToArray());
                return this;
            }

            internal Layer Cone(float angle, float radius, Vector3 rotation = default)
            {
                var shape = _system.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = angle;
                shape.radius = radius;
                shape.rotation = rotation;
                return this;
            }

            internal Layer Sphere(float radius, float thickness = 1f)
            {
                var shape = _system.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = radius;
                shape.radiusThickness = thickness;
                return this;
            }

            internal Layer Circle(float radius, float thickness, Vector3 rotation = default)
            {
                var shape = _system.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = radius;
                shape.radiusThickness = thickness;
                shape.rotation = rotation;
                return this;
            }

            /// <summary>Emits anywhere along a line member's length: a thin box from z = 0 to z = 1 whose shape alone is
            /// scaled to the line, so the particles keep their own size.</summary>
            internal Layer Along()
            {
                var shape = _system.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.position = new Vector3(0f, 0f, 0.5f);
                shape.scale = new Vector3(0f, 0f, 1f);
                var main = _system.main;
                main.scalingMode = ParticleSystemScalingMode.Shape;
                return this;
            }

            /// <summary>The body of a line member: the one-metre line mesh along +Z, simulated in local space so it
            /// stretches with the line.</summary>
            internal Layer Line(Mesh mesh)
            {
                Local();
                _renderer.renderMode = ParticleSystemRenderMode.Mesh;
                _renderer.mesh = mesh;
                _renderer.alignment = ParticleSystemRenderSpace.Local;
                return this;
            }

            internal Layer Over(Gradient gradient)
            {
                var color = _system.colorOverLifetime;
                color.enabled = true;
                color.color = new ParticleSystem.MinMaxGradient(gradient);
                return this;
            }

            internal Layer SizeOver(float start, float end) => SizeCurve(AnimationCurve.Linear(0f, start, 1f, end));

            internal Layer SizeCurve(AnimationCurve curve)
            {
                var size = _system.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, curve);
                return this;
            }

            internal Layer Turn(float radiansPerSecond) => Turn(-radiansPerSecond, radiansPerSecond);

            internal Layer Turn(float minimum, float maximum)
            {
                var rotation = _system.rotationOverLifetime;
                rotation.enabled = true;
                rotation.z = new ParticleSystem.MinMaxCurve(minimum, maximum);
                return this;
            }

            internal Layer Drag(float drag)
            {
                var limit = _system.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.limit = 1000f;
                limit.drag = drag;
                return this;
            }

            internal Layer Orbit(float x, float y, float z, float radial)
            {
                var velocity = _system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.Local;
                velocity.x = 0f;
                velocity.y = 0f;
                velocity.z = 0f;
                velocity.orbitalX = x;
                velocity.orbitalY = y;
                velocity.orbitalZ = z;
                velocity.radial = radial;
                return this;
            }

            internal Layer Noise(float strength, float frequency)
            {
                var noise = _system.noise;
                noise.enabled = true;
                noise.strength = strength;
                noise.frequency = frequency;
                noise.quality = ParticleSystemNoiseQuality.Medium;
                return this;
            }

            internal Layer Collide(float bounce, float dampen, float lifetimeLoss)
            {
                var collision = _system.collision;
                collision.enabled = true;
                collision.type = ParticleSystemCollisionType.World;
                collision.mode = ParticleSystemCollisionMode.Collision3D;
                collision.quality = ParticleSystemCollisionQuality.Medium;
                collision.bounce = bounce;
                collision.dampen = dampen;
                collision.lifetimeLoss = lifetimeLoss;
                collision.radiusScale = 0.5f;
                collision.collidesWith = ForgeVfxProject.ParticleCollisionMask;
                return this;
            }

            /// <summary>A flipbook: a random fixed frame per particle, or the frames played over each particle's life.</summary>
            internal Layer Sheet(int columns, int rows, bool animate, float cycles = 1f)
            {
                var sheet = _system.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.numTilesX = columns;
                sheet.numTilesY = rows;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.frameOverTime = animate ? new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f)) : new ParticleSystem.MinMaxCurve(0f, 0f);
                if (!animate) sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 0.999f);
                sheet.cycleCount = Mathf.Max(1, Mathf.RoundToInt(cycles));
                return this;
            }

            /// <summary>A random variant per particle from a square sheet of <paramref name="count"/> (4 = 2 x 2, 16 =
            /// 4 x 4) or a strip of 8.</summary>
            internal Layer Frames(int count)
            {
                if (count == 8) return Sheet(8, 1, animate: false);
                var side = Mathf.RoundToInt(Mathf.Sqrt(count));
                return Sheet(side, side, animate: false);
            }

            internal Layer Stretch(float velocityScale, float lengthScale)
            {
                _renderer.renderMode = ParticleSystemRenderMode.Stretch;
                _renderer.velocityScale = velocityScale;
                _renderer.lengthScale = lengthScale;
                return this;
            }

            /// <summary>A ribbon each particle draws behind it in the world for <paramref name="seconds"/> (the particle's
            /// life is one second), as wide as the particle and tapering to nothing, coloured by <paramref name="color"/>
            /// rather than the particle; the ribbon outlives its particle so a loop restart leaves no gap.</summary>
            internal Layer Trail(Material material, float seconds, Gradient color)
            {
                var trails = _system.trails;
                trails.enabled = true;
                trails.mode = ParticleSystemTrailMode.PerParticle;
                trails.ratio = 1f;
                trails.lifetime = seconds;
                trails.minVertexDistance = 0.04f;
                trails.worldSpace = true;
                trails.dieWithParticles = false;
                trails.textureMode = ParticleSystemTrailTextureMode.Stretch;
                trails.sizeAffectsWidth = true;
                trails.inheritParticleColor = false;
                trails.colorOverTrail = new ParticleSystem.MinMaxGradient(color);
                trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
                _renderer.trailMaterial = material;
                return this;
            }

            internal Layer Fudge(float fudge) { _renderer.sortingFudge = fudge; return this; }

            internal Layer Shape(ParticleSystemShapeType type) { var shape = _system.shape; shape.shapeType = type; return this; }

            internal Layer Align(ParticleSystemRenderMode mode) { _renderer.renderMode = mode; return this; }

            /// <summary>A particle light: a disabled Light template under this layer that the lights module copies onto
            /// its first particle; the light fades with the particle's alpha.</summary>
            internal Layer Light(Color color, float range, float intensity)
            {
                var template = Child(_go, _go.name + "_light").AddComponent<Light>();
                template.type = LightType.Point;
                template.range = range;
                template.intensity = intensity;
                template.color = color;
                template.shadows = LightShadows.None;
                template.enabled = false;
                var lights = _system.lights;
                lights.enabled = true;
                lights.light = template;
                lights.ratio = 1f;
                lights.maxLights = 1;
                lights.useParticleColor = false;
                lights.alphaAffectsIntensity = true;
                lights.sizeAffectsRange = false;
                lights.intensityMultiplier = 1f;
                lights.rangeMultiplier = 1f;
                return this;
            }

            private static ParticleSystem.MinMaxCurve Range(float minimum, float maximum)
                => Mathf.Approximately(minimum, maximum) ? new ParticleSystem.MinMaxCurve(minimum) : new ParticleSystem.MinMaxCurve(minimum, maximum);
        }

        private static GameObject Child(GameObject parent, string name) => Child(parent, name, Vector3.zero);

        private static GameObject Child(GameObject parent, string name, Vector3 position, Vector3 euler = default)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = position;
            child.transform.localEulerAngles = euler;
            return child;
        }

        private static Gradient Ramp((float Time, Color Color)[] colors, (float Time, float Alpha)[] alphas)
        {
            var gradient = new Gradient();
            var colorKeys = new GradientColorKey[colors.Length];
            for (var index = 0; index < colors.Length; index++) colorKeys[index] = new GradientColorKey(colors[index].Color, colors[index].Time);
            var alphaKeys = new GradientAlphaKey[alphas.Length];
            for (var index = 0; index < alphas.Length; index++) alphaKeys[index] = new GradientAlphaKey(alphas[index].Alpha, alphas[index].Time);
            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        // ---- assets --------------------------------------------------------------------------------------------

        private static Material WriteMaterial(string name, string shaderName, Texture2D texture, float tint)
        {
            var path = Own + "Materials/" + name + ".mat";
            var shader = Shader.Find(shaderName) ?? throw new InvalidOperationException("Shader " + shaderName + " is not imported.");
            var material = new Material(shader) { mainTexture = texture };
            material.SetColor("_TintColor", new Color(tint, tint, tint, 1f));
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(material, path);
            Written.Add(path);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        /// <summary>Deletes the own textures or materials this run did not write: a superseded asset is not kept.</summary>
        private static void DeleteUnwritten(string folder, string extension)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(ForgeVfxProject.ProjectFolder, Own, folder), "*" + extension))
            {
                var path = Own + folder + "/" + Path.GetFileName(file);
                if (!Written.Contains(path)) AssetDatabase.DeleteAsset(path);
            }
        }

        /// <summary>Two crossed quads from z = 0 to z = 1, one metre wide: u runs across, v along the line.</summary>
        private static Mesh WriteLineMesh()
        {
            var path = Own + "Meshes/line_cross.asset";
            var mesh = new Mesh { name = "line_cross" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(-0.5f, 0f, 1f), new Vector3(0.5f, 0f, 1f),
                new Vector3(0f, -0.5f, 0f), new Vector3(0f, 0.5f, 0f), new Vector3(0f, -0.5f, 1f), new Vector3(0f, 0.5f, 1f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f)
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        private static Texture2D WriteTexture(string name, int width, int height, Func<int, int, Color> pixel, bool repeat = false)
        {
            var path = Own + "Textures/" + name + ".png";
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    pixels[y * width + x] = pixel(x, y);
                texture.SetPixels(pixels);
                texture.Apply(false);
                File.WriteAllBytes(Path.Combine(ForgeVfxProject.ProjectFolder, path), texture.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Written.Add(path);
            return Configure(path, alphaFromGray: false, repeat);
        }

        private static Texture2D Configure(string path, bool alphaFromGray, bool repeat = false)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter
                ?? throw new InvalidOperationException(path + " is not an imported texture.");
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = alphaFromGray ? TextureImporterAlphaSource.FromGrayScale : TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.sRGBTexture = true;
            importer.wrapModeU = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---- procedural textures -------------------------------------------------------------------------------

        /// <summary>A square texture of the given size drawn from (u, v) in -1..1.</summary>
        private static Func<int, int, Color> Square(int size, Func<float, float, Color> pixel)
            => (x, y) => pixel((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f);

        /// <summary>A sheet of <paramref name="side"/> x <paramref name="side"/> tiles of <paramref name="tile"/> pixels,
        /// each drawn from (u, v) in -1..1 and its frame index (frame 0 top left, as Unity's sheet animation reads it).</summary>
        private static Func<int, int, Color> Sheet(int side, int tile, Func<float, float, int, Color> pixel)
            => (x, y) =>
            {
                var column = x / tile;
                var row = side - 1 - y / tile;
                return pixel((x % tile + 0.5f) / tile * 2f - 1f, (y % tile + 0.5f) / tile * 2f - 1f, row * side + column);
            };

        private static Color GlowPixel(float u, float v)
        {
            var d = Mathf.Sqrt(u * u + v * v);
            var glow = Mathf.Exp(-d * d * 6f) * (1f - Edge(0.8f, 1f, d));
            var core = Mathf.Exp(-d * d * 40f);
            return new Color(1f, 1f, 1f, Mathf.Clamp01(glow * 0.75f + core));
        }

        /// <summary>A soft beam cross-section: bright centre, wide falloff; constant along the line.</summary>
        private static Color BeamPixel(float u, float v)
        {
            var alpha = Mathf.Exp(-(u / 0.12f) * (u / 0.12f)) + 0.35f * Mathf.Exp(-(u / 0.5f) * (u / 0.5f));
            return new Color(1f, 1f, 1f, Mathf.Clamp01(alpha * (1f - Edge(0.85f, 1f, Mathf.Abs(u)))));
        }

        /// <summary>A strip soft across <paramref name="v"/> and the same all along: a core and a halo that both reach nothing with no slope at the edge, so a wide
        /// additive strip (a `render: line` recipe layer) shows no edge even in linear colour, where a faint tail cut off reads as a box.</summary>
        private static Color StripPixel(float v)
        {
            var c = Mathf.Cos(Mathf.Clamp01(Mathf.Abs(v)) * Mathf.PI * 0.5f);
            return new Color(1f, 1f, 1f, Mathf.Clamp01(0.6f * Mathf.Exp(-(v / 0.2f) * (v / 0.2f)) + 0.4f * Mathf.Pow(c, 6f)));
        }

        private static Color DiscPixel(float u, float v)
        {
            var d = Mathf.Sqrt(u * u + v * v);
            return new Color(1f, 1f, 1f, 1f - Edge(0.82f, 0.98f, d));
        }

        /// <summary>A thin soft ring at nine tenths of the radius, its brightness wavering round it.</summary>
        /// <summary>The black hole's lens: an annulus from the core's edge outwards, strongest at 0.45 of the radius, gone at the rim.</summary>
        private static Color LensMaskPixel(float u, float v)
        {
            var d = Mathf.Sqrt(u * u + v * v);
            var a = Edge(0.3f, 0.45f, d) * (1f - Edge(0.45f, 1f, d));
            return new Color(1f, 1f, 1f, a);
        }

        /// <summary>A swirl about the centre (RG round the tangent with a little pull inwards), 0.5 = no bend.</summary>
        private static Color LensSwirlPixel(float u, float v)
        {
            var d = Mathf.Max(0.0001f, Mathf.Sqrt(u * u + v * v));
            var tx = -v / d * 0.8f - u / d * 0.45f;
            var ty = u / d * 0.8f - v / d * 0.45f;
            return new Color(0.5f + 0.5f * tx, 0.5f + 0.5f * ty, 0.5f, 1f);
        }

        private static Texture2D Linear(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.alphaIsTransparency = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Color RingPixel(float u, float v)
        {
            var angle = Mathf.Atan2(v, u);
            var d = (Mathf.Sqrt(u * u + v * v) - 0.9f) / 0.03f;
            var waver = 0.55f + 0.45f * Fbm(Mathf.Cos(angle) * 2.5f + 7f, Mathf.Sin(angle) * 2.5f, 3);
            return new Color(1f, 1f, 1f, Mathf.Exp(-d * d) * waver);
        }

        /// <summary>A shock front: a thin ring whose radius and brightness break up round it, with a faint haze inside.</summary>
        private static Color ShockRingPixel(float u, float v)
        {
            var angle = Mathf.Atan2(v, u);
            var cx = Mathf.Cos(angle) * 3f;
            var cy = Mathf.Sin(angle) * 3f;
            var radius = 0.8f + (Fbm(cx + 11f, cy, 3) - 0.5f) * 0.08f;
            var d = Mathf.Sqrt(u * u + v * v);
            var front = Mathf.Exp(-Mathf.Pow((d - radius) / 0.045f, 2f)) * Mathf.Clamp01(Fbm(cx * 2f, cy * 2f + 5f, 4) * 1.8f - 0.2f);
            var haze = d < radius ? 0.3f * Mathf.Exp(-Mathf.Pow((d - radius) / 0.18f, 2f)) : 0f;
            return new Color(1f, 1f, 1f, Mathf.Clamp01(front + haze) * (1f - Edge(0.92f, 1f, d)));
        }

        /// <summary>A muzzle flash seen from the front: a hot core and five to eight ragged petals of uneven length.</summary>
        private static Color FlashPixel(float u, float v, int frame)
        {
            var d = Mathf.Sqrt(u * u + v * v);
            var angle = Mathf.Atan2(v, u);
            var petals = 5 + frame % 4;
            var offset = Hash(frame, 3) * Mathf.PI * 2f;
            var sector = Mathf.Repeat((angle + offset) / (Mathf.PI * 2f) * petals, petals);
            var index = Mathf.FloorToInt(sector);
            var across = sector - index - 0.5f;
            var length = 0.5f + 0.45f * Hash(frame * 31 + index, 7);
            var width = 0.3f + 0.15f * Hash(frame * 17 + index, 11);
            var ragged = 0.7f + 0.6f * Fbm(u * 7f + frame * 5.3f, v * 7f, 3);
            var petal = Mathf.Exp(-(across * across) / (width * width * (0.25f + d * ragged))) * (1f - Edge(length * 0.3f, length * ragged, d));
            var breakup = 0.45f + 0.55f * Fbm(u * 6f + frame * 3.1f, v * 6f, 4);
            var core = Mathf.Exp(-d * d * 20f);
            var alpha = Mathf.Clamp01(core + petal * breakup + 0.3f * Mathf.Exp(-d * d * 5f)) * (1f - Edge(0.9f, 1f, d));
            return new Color(1f, 1f, 1f, alpha);
        }

        /// <summary>Billowing smoke with a baked top light: a lumpy domain-warped noise ball whose upper side is lighter
        /// and whose lower side and folds are darker; sixteen variants. RGB is premultiplied by the coverage, for the
        /// premultiplied material.</summary>
        private static Color SmokePixel(float u, float v, int frame)
        {
            var seed = frame * 7.13f;
            float Density(float x, float y)
            {
                var wx = Fbm(x * 1.4f + seed, y * 1.4f, 3) - 0.5f;
                var wy = Fbm(x * 1.4f, y * 1.4f + seed + 3.3f, 3) - 0.5f;
                var n = Fbm(x * 2.4f + wx * 1.6f + seed, y * 2.4f + wy * 1.6f, 5);
                var r = Mathf.Sqrt(x * x + y * y) + (n - 0.5f) * 0.6f;
                return (1f - Edge(0.05f, 1f, r)) * (0.3f + n);
            }
            var density = Density(u, v);
            var alpha = Mathf.Clamp01((density - 0.2f) * 1.7f) * (1f - Edge(0.85f, 1f, Mathf.Max(Mathf.Abs(u), Mathf.Abs(v))));
            // Broad, soft lighting from a low-octave field: lighter above, darker below, no creases.
            var broad = Fbm(u * 1.2f + seed, v * 1.2f + 5.1f, 2);
            var shade = Mathf.Clamp01(0.62f + (broad - 0.5f) * 0.5f + v * 0.22f) * alpha;
            return new Color(shade, shade, shade, alpha);
        }

        /// <summary>A chunk of rubble or torn metal: an irregular outline, flat facets lit from above, dark; sixteen variants.</summary>
        private static Color DebrisPixel(float u, float v, int frame)
        {
            var angle = Mathf.Atan2(v, u);
            var d = Mathf.Sqrt(u * u + v * v);
            var outline = 0.5f + 0.3f * (Fbm(Mathf.Cos(angle) * 1.5f + frame * 3.7f, Mathf.Sin(angle) * 1.5f, 2) - 0.5f) * 2f;
            var inside = 1f - Edge(outline - 0.05f, outline, d);
            // Flat facets: the nearest of five points scattered over the chunk, each lit at its own angle to the top light.
            var best = 10f;
            var cell = 0;
            for (var point = 0; point < 5; point++)
            {
                var px = (Hash(frame * 11 + point, 21) - 0.5f) * 1.1f;
                var py = (Hash(frame * 11 + point, 23) - 0.5f) * 1.1f;
                var distance = (u - px) * (u - px) + (v - py) * (v - py);
                if (distance < best) { best = distance; cell = point; }
            }
            var light = 0.25f + 0.5f * Hash(frame * 11 + cell, 29) + 0.15f * v;
            var grain = 0.85f + 0.15f * Fbm(u * 8f + frame, v * 8f, 2);
            var shade = Mathf.Clamp01(light * grain);
            return new Color(shade, shade, shade, inside);
        }

        /// <summary>Burning gas in sixteen frames played over a particle's life: a turbulent puff whose bright core
        /// thins and breaks up into wisps as it burns out. Grey; RGB is the heat (brightest in the dense core)
        /// premultiplied by the coverage, so the premultiplied material glows where it burns and the additive one
        /// draws its light.</summary>
        private static Color FirePixel(float u, float v, int frame)
        {
            var t = frame / 15f;
            var wx = Fbm(u * 1.8f + 3.1f, v * 1.8f - t * 1.5f, 3) - 0.5f;
            var wy = Fbm(u * 1.8f + 8.7f, v * 1.8f - t * 1.5f + 4.2f, 3) - 0.5f;
            var n = Fbm(u * 2.6f + wx * 1.8f, v * 2.6f + wy * 1.8f - t * 2.2f, 5);
            var r = Mathf.Sqrt(u * u + v * v * 0.85f) + (n - 0.5f) * 0.75f;
            var density = (1f - Edge(0.25f, 1f + 0.1f * t, r)) * (0.3f + n);
            var threshold = 0.12f + 0.42f * t;
            var alpha = Mathf.Clamp01((density - threshold) * 2.6f) * (1f - Edge(0.88f, 1f, Mathf.Max(Mathf.Abs(u), Mathf.Abs(v))));
            var heat = Mathf.Clamp01((density - threshold - 0.08f) * 2.2f);
            var light = (0.3f + 0.7f * heat) * alpha;
            return new Color(light, light, light, alpha);
        }

        /// <summary>Roiling plasma: a sphere of ridged, swirling filaments brightest at its heart and limb, in sixteen
        /// frames that loop.</summary>
        private static Color PlasmaPixel(float u, float v, int frame)
        {
            var d = Mathf.Sqrt(u * u + v * v) / 0.82f;
            if (d >= 1.15f) return new Color(1f, 1f, 1f, 0f);
            var phase = frame / 16f * Mathf.PI * 2f;
            var swirl = 0.7f * (1f - Mathf.Clamp01(d));
            var cs = Mathf.Cos(swirl + phase * 0.25f);
            var sn = Mathf.Sin(swirl + phase * 0.25f);
            var x = u * cs - v * sn;
            var y = u * sn + v * cs;
            var ox = Mathf.Cos(phase) * 0.7f;
            var oy = Mathf.Sin(phase) * 0.7f;
            var ridge = 1f - Mathf.Abs(2f * Fbm(x * 2.6f + ox, y * 2.6f + oy, 4) - 1f);
            ridge = Mathf.Pow(ridge, 4f);
            var sphere = 1f - Edge(0.85f, 1.05f, d);
            var heart = Mathf.Exp(-d * d * 4f);
            var limb = Mathf.Exp(-Mathf.Pow((d - 0.92f) / 0.1f, 2f)) * 0.5f;
            var corona = d > 1f ? 0.25f * (1f - Edge(1f, 1.15f, d)) * ridge : 0f;
            return new Color(1f, 1f, 1f, Mathf.Clamp01((0.12f + 0.45f * heart + 0.85f * ridge + limb * 0.8f) * sphere * 0.85f + corona));
        }

        /// <summary>Two arms of glowing gas winding into the centre, streaked along the spiral, faded at the eye and rim.</summary>
        private static Color SpiralPixel(float u, float v)
        {
            var d = Mathf.Sqrt(u * u + v * v);
            if (d < 0.001f) return new Color(1f, 1f, 1f, 0f);
            var angle = Mathf.Atan2(v, u);
            var wind = angle + 2.6f * Mathf.Log(d);
            var arms = Mathf.Pow(0.5f + 0.5f * Mathf.Cos(2f * wind), 3f);
            var streaks = 0.5f + 0.5f * Fbm(Mathf.Cos(wind) * 3f + d * 2f, Mathf.Sin(wind) * 3f, 4);
            var window = Edge(0.12f, 0.35f, d) * (1f - Edge(0.7f, 1f, d));
            return new Color(1f, 1f, 1f, Mathf.Clamp01(arms * streaks * window * 1.2f));
        }

        /// <summary>A 4-frame flow band (64 x 256 frames side by side): bright bands along the line that move one
        /// quarter period per frame, inside a soft cross-section.</summary>
        private static Color FlowPixel(int x, int y)
        {
            var frame = x / 64;
            var u = (x % 64 + 0.5f) / 64f * 2f - 1f;
            var v = (y + 0.5f) / 256f;
            var phase = 2f * Mathf.PI * (3f * v - frame / 4f) + 1.5f * (ValueNoise(u * 2f, v * 6f) - 0.5f);
            var band = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(phase), 3f);
            return new Color(1f, 1f, 1f, Mathf.Clamp01(band * Mathf.Exp(-(u / 0.35f) * (u / 0.35f))));
        }

        /// <summary>Eight jagged lightning bolts side by side (64 x 512 each), each running the full length of its strip
        /// along v with a few forks: a thin white core inside a soft glow.</summary>
        private static Func<int, int, Color> BoltSheet()
        {
            const int strips = 8, points = 129;
            var paths = new List<float[]>[strips];
            for (var strip = 0; strip < strips; strip++)
            {
                var random = new System.Random(4101 + strip);
                var list = new List<float[]> { Displace(random, points, 0f, 0f, 0.45f) };
                var forks = 2 + strip % 2;
                for (var fork = 0; fork < forks; fork++)
                {
                    // A fork leaves the main path at a random point and drifts off to one side, fading as it goes.
                    var main = list[0];
                    var from = 10 + random.Next(points - 50);
                    var length = 18 + random.Next(26);
                    var side = random.NextDouble() < 0.5 ? -1f : 1f;
                    var branch = new float[points];
                    for (var i = 0; i < points; i++) branch[i] = float.NaN;
                    var drift = Displace(random, 33, 0f, side * (0.35f + (float)random.NextDouble() * 0.3f), 0.12f);
                    for (var i = 0; i <= length && from + i < points; i++) branch[from + i] = main[from] + drift[Mathf.Min(32, i * 32 / length)];
                    list.Add(branch);
                }
                paths[strip] = list;
            }
            // Each path as pixel points of its strip: x across (32 at the centre), y along.
            var pixels = new List<Vector2[]>[strips];
            for (var strip = 0; strip < strips; strip++)
            {
                pixels[strip] = new List<Vector2[]>();
                foreach (var line in paths[strip])
                {
                    var at = new Vector2[points];
                    for (var i = 0; i < points; i++) at[i] = new Vector2(32f + line[i] * 28f, i * 511f / (points - 1));
                    pixels[strip].Add(at);
                }
            }
            return (x, y) =>
            {
                var strip = x / 64;
                var p = new Vector2(x % 64 + 0.5f, y + 0.5f);
                var index = Mathf.Clamp(Mathf.FloorToInt(y / 511f * (points - 1)), 0, points - 2);
                var core = 0f;
                var glow = 0f;
                for (var path = 0; path < pixels[strip].Count; path++)
                {
                    var line = pixels[strip][path];
                    var best = 100f;
                    for (var i = Mathf.Max(0, index - 6); i <= Mathf.Min(points - 2, index + 6); i++)
                    {
                        if (float.IsNaN(line[i].x) || float.IsNaN(line[i + 1].x)) continue;
                        best = Mathf.Min(best, SegmentDistance(p, line[i], line[i + 1]));
                    }
                    var weight = path == 0 ? 1f : 0.7f;
                    core = Mathf.Max(core, weight * Mathf.Exp(-Mathf.Pow(best / 1.3f, 2f)));
                    glow = Mathf.Max(glow, weight * Mathf.Exp(-Mathf.Pow(best / 6f, 2f)));
                }
                var edge = 1f - Edge(26f, 32f, Mathf.Abs(p.x - 32f));
                return new Color(1f, 1f, 1f, Mathf.Clamp01(core + glow * 0.45f) * edge);
            };
        }

        private sealed class BoltSegment
        {
            internal Vector2 A, B;
            internal float Sigma, Weight;
        }

        /// <summary>Eight tiling lightning bolts stacked across (2048 along u x 64 across each), the laser bodies of
        /// <see cref="LightningArc"/>: each row's path starts and ends on the centre line, so it repeats along u without a seam,
        /// and zigzags off it at irregular corners on a slow wander, its core thickening and thinning along the way,
        /// with three forks drifting off to either side and fading.</summary>
        private static Func<int, int, Color> BoltTile()
        {
            const int width = 2048, rows = 8, rowHeight = 64, corners = 64, step = width / corners;
            var buckets = new List<BoltSegment>[rows, corners];
            for (var row = 0; row < rows; row++)
            {
                for (var b = 0; b < corners; b++) buckets[row, b] = new List<BoltSegment>();
                var random = new System.Random(9301 + row);
                // corners one to five steps apart, mostly turning back to the other side by a random amount (sometimes going on the
                // same way), on a slow two-wave wander over the whole tile, with a small jitter between; the last corner is the first,
                // so the row repeats without a seam
                var main = new float[corners + 1];
                var cornerAt = new List<int> { 0 };
                while (cornerAt[cornerAt.Count - 1] < corners - 2) cornerAt.Add(Mathf.Min(corners - 1, cornerAt[cornerAt.Count - 1] + 1 + random.Next(5)));
                cornerAt.Add(corners);
                var heading = 1f;
                var turn = new float[cornerAt.Count];
                for (var k = 0; k < cornerAt.Count - 1; k++)
                {
                    if (random.NextDouble() < 0.75) heading = -heading;
                    turn[k] = heading * (0.08f + (float)random.NextDouble() * 0.42f);
                }
                turn[cornerAt.Count - 1] = turn[0];
                float phase1 = (float)random.NextDouble() * Mathf.PI * 2f, phase2 = (float)random.NextDouble() * Mathf.PI * 2f;
                for (var k = 0; k < cornerAt.Count - 1; k++)
                    for (var i = cornerAt[k]; i <= cornerAt[k + 1]; i++)
                    {
                        var t = (i - cornerAt[k]) / (float)(cornerAt[k + 1] - cornerAt[k]);
                        var jitter = t <= 0f || t >= 1f ? 0f : ((float)random.NextDouble() * 2f - 1f) * 0.05f;
                        var wander = 0.3f * Mathf.Sin(phase1 + i * Mathf.PI * 2f / corners) + 0.18f * Mathf.Sin(phase2 + i * Mathf.PI * 4f / corners);
                        main[i] = Mathf.Lerp(turn[k], turn[k + 1], t) + wander + jitter;
                    }
                var thick = new float[corners + 1];
                for (var i = 0; i <= corners; i++) thick[i] = 0.7f + (float)random.NextDouble() * 1.1f;
                Vector2 At(int i, float lateral) => new Vector2(i * step, rowHeight / 2f + Mathf.Clamp(lateral, -1f, 1f) * 23f);
                void Add(Vector2 a, Vector2 b, float sigma, float weight)
                {
                    var segment = new BoltSegment { A = a, B = b, Sigma = sigma, Weight = weight };
                    var from = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, b.x) / step), 0, corners - 1);
                    var to = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(a.x, b.x) / step), 0, corners - 1);
                    for (var bucket = from; bucket <= to; bucket++) buckets[row, bucket].Add(segment);
                }
                for (var i = 0; i < corners; i++)
                    Add(At(i, main[i]), At(i + 1, main[i + 1]), 0.5f * (thick[i] + thick[i + 1]), 1f);
                for (var fork = 0; fork < 3; fork++)
                {
                    var from = 2 + random.Next(corners - 12);
                    var length = 3 + random.Next(5);
                    var side = random.NextDouble() < 0.5 ? -1f : 1f;
                    var drift = Displace(random, 9, 0f, side * (0.35f + (float)random.NextDouble() * 0.3f), 0.15f);
                    for (var k = 0; k < length; k++)
                    {
                        var fade = 1f - 0.6f * k / length;
                        Add(At(from + k, main[from] + drift[k * 8 / length]), At(from + k + 1, main[from] + drift[(k + 1) * 8 / length]), 0.6f * fade, 0.75f * fade);
                    }
                }
            }
            return (x, y) =>
            {
                var row = y / rowHeight;
                var p = new Vector2(x + 0.5f, y % rowHeight + 0.5f);
                var bucket = x / step;
                var core = 0f;
                var glow = 0f;
                for (var near = bucket - 1; near <= bucket + 1; near++)
                {
                    // the row repeats along u: the buckets past either end are those of the other end, a tile away
                    var wrapped = (near + corners) % corners;
                    var at = new Vector2(p.x + (near < 0 ? width : near >= corners ? -width : 0f), p.y);
                    foreach (var segment in buckets[row, wrapped])
                    {
                        var d = SegmentDistance(at, segment.A, segment.B);
                        core = Mathf.Max(core, segment.Weight * Mathf.Exp(-Mathf.Pow(d / segment.Sigma, 2f)));
                        glow = Mathf.Max(glow, segment.Weight * Mathf.Exp(-Mathf.Pow(d / 3f, 2f)));
                    }
                }
                var edge = 1f - Edge(26f, 32f, Mathf.Abs(p.y - rowHeight / 2f));
                return new Color(1f, 1f, 1f, Mathf.Clamp01(core + glow * 0.3f) * edge);
            };
        }

        /// <summary>Midpoint displacement from <paramref name="start"/> to <paramref name="end"/> over
        /// <paramref name="count"/> points (2^n + 1), jitter halving each level.</summary>
        private static float[] Displace(System.Random random, int count, float start, float end, float amplitude)
        {
            var line = new float[count];
            line[0] = start;
            line[count - 1] = end;
            for (var step = count - 1; step > 1; step /= 2)
            {
                for (var at = step / 2; at < count - 1; at += step)
                {
                    var a = line[at - step / 2];
                    var b = line[Mathf.Min(count - 1, at + step / 2)];
                    line[at] = (a + b) * 0.5f + ((float)random.NextDouble() * 2f - 1f) * amplitude;
                }
                amplitude *= 0.55f;
            }
            for (var i = 0; i < count; i++) line[i] = Mathf.Clamp(line[i], -0.8f, 0.8f);
            return line;
        }

        /// <summary>Sixteen electric crackle clusters (4 x 4 of 128): three to six jagged arcs thrown out from the
        /// centre with small forks, a white core in a soft glow.</summary>
        private static Func<int, int, Color> CrackleSheet()
        {
            var segments = new List<Vector4>[16];
            for (var frame = 0; frame < 16; frame++)
            {
                var random = new System.Random(7307 + frame);
                var list = new List<Vector4>();
                var arcs = 3 + frame % 4;
                for (var arc = 0; arc < arcs; arc++)
                {
                    var angle = (arc + (float)random.NextDouble() * 0.6f) / arcs * Mathf.PI * 2f;
                    var start = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (0.05f + (float)random.NextDouble() * 0.1f);
                    var end = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (0.55f + (float)random.NextDouble() * 0.35f);
                    var path = Jagged(random, start, end, 0.18f);
                    for (var i = 0; i + 1 < path.Count; i++) list.Add(new Vector4(path[i].x, path[i].y, path[i + 1].x, path[i + 1].y));
                    var forkAt = path[path.Count / 2];
                    var forkTo = forkAt + Rotate(end - start, (random.NextDouble() < 0.5 ? -1f : 1f) * 0.7f) * 0.35f;
                    var fork = Jagged(random, forkAt, forkTo, 0.1f);
                    for (var i = 0; i + 1 < fork.Count; i++) list.Add(new Vector4(fork[i].x, fork[i].y, fork[i + 1].x, fork[i + 1].y));
                }
                segments[frame] = list;
            }
            var sheet = Sheet(4, 128, (u, v, frame) =>
            {
                var p = new Vector2(u, v);
                var best = 10f;
                foreach (var segment in segments[frame])
                    best = Mathf.Min(best, SegmentDistance(p, new Vector2(segment.x, segment.y), new Vector2(segment.z, segment.w)));
                var alpha = Mathf.Exp(-Mathf.Pow(best / 0.025f, 2f)) + 0.45f * Mathf.Exp(-Mathf.Pow(best / 0.09f, 2f));
                var d = p.magnitude;
                return new Color(1f, 1f, 1f, Mathf.Clamp01(alpha + 0.3f * Mathf.Exp(-d * d * 30f)) * (1f - Edge(0.9f, 1f, d)));
            });
            return sheet;
        }

        private static List<Vector2> Jagged(System.Random random, Vector2 start, Vector2 end, float amplitude)
        {
            var offsets = Displace(random, 17, 0f, 0f, amplitude);
            var normal = new Vector2(-(end - start).y, (end - start).x).normalized * (end - start).magnitude;
            var path = new List<Vector2>(17);
            for (var i = 0; i < 17; i++) path.Add(Vector2.Lerp(start, end, i / 16f) + normal * offsets[i] * 0.5f);
            return path;
        }

        private static Vector2 Rotate(Vector2 value, float radians)
            => new Vector2(value.x * Mathf.Cos(radians) - value.y * Mathf.Sin(radians), value.x * Mathf.Sin(radians) + value.y * Mathf.Cos(radians));

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>The smoothstep edge: 0 below <paramref name="from"/>, 1 above <paramref name="to"/>, eased between.
        /// (Unity's Mathf.SmoothStep interpolates between its first two arguments instead.)</summary>
        private static float Edge(float from, float to, float x)
        {
            var t = Mathf.Clamp01((x - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        private static float Fbm(float x, float y, int octaves)
        {
            var sum = 0f;
            var amplitude = 0.5f;
            var norm = 0f;
            for (var octave = 0; octave < octaves; octave++)
            {
                sum += ValueNoise(x, y) * amplitude;
                norm += amplitude;
                x = x * 2.03f + 17.1f;
                y = y * 2.03f + 9.7f;
                amplitude *= 0.5f;
            }
            return sum / norm;
        }

        private static float ValueNoise(float x, float y)
        {
            var xi = Mathf.FloorToInt(x);
            var yi = Mathf.FloorToInt(y);
            var fx = x - xi;
            var fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            var a = Mathf.Lerp(Hash(xi, yi), Hash(xi + 1, yi), fx);
            var b = Mathf.Lerp(Hash(xi, yi + 1), Hash(xi + 1, yi + 1), fx);
            return Mathf.Lerp(a, b, fy);
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                var h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h ^ (h >> 16)) / (float)uint.MaxValue;
            }
        }
    }
}
