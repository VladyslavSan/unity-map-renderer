// Pitched-camera glyph-size and device-pixel-ratio GPU/visual acceptance tests.
//
// Split by the CS0104 bare-`Object` collision, then by content. No bare-Object user is in this file.
//
// Contents:
//   MapPitchedGlyphSizeTests          — Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real SymbolPlacementSystem.Tick), off-screen GPU render + CPU readback.
//   DevicePixelRatioSnapshotTests     — Unity EditMode only — real MapCamera + Camera/RenderTexture, off-screen GPU render + CPU readback.
//   MapPitchedGlyphSizeTiltZeroTests  — Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader.

using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Text.Placement;
using System;
using System.IO;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Line = MapRenderer.Core.Style.Line;
using Symbol = MapRenderer.Core.Style.Symbol;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Visual
{
    // Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real
    // SymbolPlacementSystem.Tick), off-screen GPU render + CPU readback. NOT registered in
    // Tools/core-tests/core-tests.csproj.
    //
    // THE SIZE TEETH. Under `*-pitch-alignment: map` a glyph's DRAWN SIZE is a world-metre quantity carried by
    // the SAME `arcScale` that spaces the glyph anchors, so size and spacing foreshorten together.
    //
    // Non-obvious why: StageCurved spaces glyphs by `ΔArcCenter · arcScale` and sizes corners by
    // `cornerBaked · arcScale`, so arcScale cancels:
    //      gap_world / cellWidth_world  = ΔArcCenter / cellWidthBaked                              … (5)
    //      gap_screen / glyphSize_screen = ΔArcCenter / cellWidthBaked = CONSTANT AT EVERY DEPTH  … (6)
    // Limitation: that cancellation makes every tooth here blind to a uniform scale error;
    // MapPitchedGlyphSizeTiltZeroTests pins the absolute scale, DPR and ŷ sign. The cross arm is iso-depth,
    // where a per-glyph size equals one constant per symbol, so it serves only as a control.
    //   • rendered ink — separability at both receding depths and on the cross arm at 1.25× and 1.5×; the
    //                    RATIO only where ink runs clear ≈ 4 px (RecedingFar's are 1–2 px).
    //   • world metres at the mesh — the RATIO at 8× depth (OffLookAtSymbolScene disables the far cull).
    //   • pure staging (MapPitchedWorldArcStagingTests.WorldCellToAdvanceRatio_HoldsAtEveryMagnitude) — to 50×.
    // A screen-constant size fails from 1.25× the look-at depth, not at the horizon.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapPitchedGlyphSizeTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapPitchedGlyphSizeTests
    {
        /// <summary>The shipped pose — tilt 55, depth ratio 2, DPR 1, 512 px.</summary>
        private static OffLookAtSymbolSceneConfig ShippedPose() => new OffLookAtSymbolSceneConfig();

        /// <summary>
        /// The ink-reading tests' pose — the SHIPPED one. Limitation: raising
        /// <see cref="OffLookAtSymbolSceneConfig.SizePx"/> does NOT magnify this fixture. Doubling SizePx
        /// doubles both the orbit radius <c>d</c> and the viewport height <c>H</c>, so
        /// <c>MetresPerDevicePixel = 2·d·tan(fov/2)/H</c> and every world length stay the same, and the
        /// projected geometry does not change.
        /// </summary>
        private static OffLookAtSymbolSceneConfig InkRunPose() => new OffLookAtSymbolSceneConfig();

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // THE HEADLINE
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>THE RENDERED HEADLINE, on the DEPTH-SPANNING arm.</b> Proves, from rendered ink alone:
        /// both receding symbols segment into exactly <c>GlyphCount</c> separable ink runs, at two depths a
        /// factor of ≈ 2 apart.
        ///
        /// <para>Non-obvious why: at the look-at the receding anchor gap is 29.07 px against a 32 px cell
        /// (r = 0.91), so a screen-constant size overlaps already. A world-metre cell foreshortens with the
        /// advance, and (6) keeps r fixed at every depth. That requires x̂ to lie IN the ground plane, so it
        /// pins the PLANE too. It reads whether runs are DISJOINT, not an ink width. A per-symbol depth ruler
        /// keeps the near symbol separable and merges the far one.</para>
        /// </summary>
        [Test]
        public void RecedingSymbols_StaySeparable_AtBothDepths()
        {
            using var f = OffLookAtSymbolScene.Create(InkRunPose());

            double nearDepth = 0.0, farDepth = 0.0;
            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                GlyphMeasurement[] glyphs = f.Measure(id).Glyphs;
                OffLookAtInkRuns.AssertRunsVertically(f, id, glyphs);

                (int start, int end)[] runs = OffLookAtInkRuns.Receding(f, id);
                int minGap = int.MaxValue;
                for (int i = 0; i + 1 < runs.Length; i++)
                    minGap = math.min(minGap, runs[i + 1].start - runs[i].end);

                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "receding separability  {0}  runs={1}/{2}  min inter-run gap={3} px  anchor depth={4:F0} m",
                    id, runs.Length, f.Config.GlyphCount, runs.Length > 1 ? minGap : -1,
                    f.Measure(id).AnchorViewDepthMetres));

                Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                    $"receding separability ({id}): a map-pitched label on a RECEDING road must keep all " +
                    $"{f.Config.GlyphCount} letters separable — got {runs.Length} ink runs. A screen-constant " +
                    "glyph size merges this arm already AT the look-at (29.07 px advance against a 32 px " +
                    "cell), so a merged reading here is the defect under test, not a tolerance " +
                    "question.");

                if (id == OffLookAtSymbolId.RecedingNear) nearDepth = f.Measure(id).AnchorViewDepthMetres;
                else                                     farDepth  = f.Measure(id).AnchorViewDepthMetres;
            }

            // The claim is worth nothing unless the two symbols really are at different depths.
            Assert.That(farDepth / nearDepth, Is.GreaterThanOrEqualTo(1.8),
                $"receding separability precondition: the two receding labels must sit at genuinely different depths — " +
                $"{nearDepth:F0} m and {farDepth:F0} m, ratio {farDepth / nearDepth:F3}. Separability at one " +
                "depth cannot distinguish a world-sized glyph from a screen-sized one.");
        }

        /// <summary>
        /// <b>The RATIO of (6), read from ink where the ink is resolvable.</b> Proves:
        /// <c>advance_screen(i) / inkExtent_screen(i)</c> is the SAME number across every glyph pair whose ink
        /// run is large enough to measure — i.e. it does not drift with depth.
        ///
        /// <para>Limitation: <c>RecedingFar</c>'s ink runs are 1–2 px, where ±1 px quantisation biases r
        /// upward, so this reads only the resolvable band.
        /// <see cref="WorldCellSize_AndWorldAdvance_ShareOneArcScale"/> carries the ratio to 8× depth in world
        /// metres. The expected value is not the cell ratio 1.25: ink is narrower than the cell (the 'F' reads
        /// ≈ 2.4), so this asserts only that r is CONSTANT across depth.</para>
        /// </summary>
        [Test]
        public void GapToInkExtentRatio_DoesNotDriftWithDepth_WhereInkIsResolvable()
        {
            using var f = OffLookAtSymbolScene.Create(InkRunPose());

            const double resolvableExtentPx = 4.0; // below this, ±1 px per edge dominates AND biases upward
            var ratios = new List<double>();
            var depths = new List<double>();
            double minExtent = double.MaxValue, minAdvance = double.MaxValue;
            double maxAdjacentDepthRatio = 1.0;
            var table = new System.Text.StringBuilder();
            table.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "gap/ink ratio  SizePx={0}  resolvable extent floor={1:F1} px", f.Config.SizePx, resolvableExtentPx));
            table.AppendLine("label          i  runStart  runEnd  extent  advance     r_i   viewDepth(m)  used");

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                GlyphMeasurement[] glyphs = f.Measure(id).Glyphs;
                OffLookAtInkRuns.AssertRunsVertically(f, id, glyphs);
                (int start, int end)[] runs = OffLookAtInkRuns.Receding(f, id);
                Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                    $"gap/ink ratio precondition ({id}): expected {f.Config.GlyphCount} ink runs, got {runs.Length}.");

                // Runs come out by screen ROW, glyphs along the ROAD. Pair them by row, or the depth column of the
                // table silently inverts while the ratios stay unchanged.
                GlyphMeasurement[] byRow = OrderedByScreenRow(f, glyphs);

                for (int i = 0; i + 1 < runs.Length; i++)
                {
                    double extent  = runs[i].end - runs[i].start;
                    double advance = runs[i + 1].start - runs[i].start;
                    double r = advance / extent;
                    bool used = extent >= resolvableExtentPx;
                    if (used)
                    {
                        ratios.Add(r);
                        depths.Add(byRow[i].ViewDepthMetres);
                        minExtent  = math.min(minExtent, extent);
                        minAdvance = math.min(minAdvance, advance);
                        maxAdjacentDepthRatio = math.max(maxAdjacentDepthRatio,
                            byRow[i + 1].ViewDepthMetres / byRow[i].ViewDepthMetres);
                    }
                    table.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,-13} {1}  {2,8} {3,7} {4,7:F1} {5,8:F1} {6,7:F4} {7,12:F0}  {8}",
                        id, i, runs[i].start, runs[i].end, extent, advance, r,
                        byRow[i].ViewDepthMetres, used ? "yes" : "NO (sub-resolution)"));
                }
            }

            Assert.That(ratios.Count, Is.GreaterThanOrEqualTo(4),
                $"gap/ink ratio precondition: at least 4 glyph pairs must have ink runs of {resolvableExtentPx:F1} px " +
                $"or more, got {ratios.Count}. Below that there is no rendered ratio to read at all and the " +
                "tooth would be vacuous — escalate rather than lowering the floor.");

            double minR = double.MaxValue, maxR = double.MinValue;
            foreach (double r in ratios) { minR = math.min(minR, r); maxR = math.max(maxR, r); }
            double minDepth = double.MaxValue, maxDepth = double.MinValue;
            foreach (double d in depths) { minDepth = math.min(minDepth, d); maxDepth = math.max(maxDepth, d); }
            double resolvedDepthRatio = maxDepth / minDepth;

            // THE BOUND, derived from the achieved geometry: ±1 px per edge gives 2/extent + 2/advance, plus the
            // depth step across one glyph's footprint (1/expected of an advance).
            double expectedCellRatio = f.Config.AdvanceBakedPx / f.GlyphCellWidthBakedPx;
            double quantisation = 2.0 / minExtent + 2.0 / minAdvance;
            double withinGlyph  = (maxAdjacentDepthRatio - 1.0) / expectedCellRatio;
            double bound        = quantisation + withinGlyph;
            double spreadR      = maxR / minR - 1.0;

            table.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "resolvable: n={0}  depths {1:F0}..{2:F0} m (ratio {3:F3})  r in [{4:F4}, {5:F4}]  " +
                "spread {6:P2}  bound {7:P2} (= quantisation {8:P2} + withinGlyph {9:P2})",
                ratios.Count, minDepth, maxDepth, resolvedDepthRatio, minR, maxR, spreadR, bound,
                quantisation, withinGlyph));
            TestContext.WriteLine(table.ToString());

            Assert.That(spreadR, Is.LessThanOrEqualTo(bound),
                $"gap/ink ratio: advance/inkExtent must not drift with depth — it spans [{minR:F4}, {maxR:F4}], " +
                $"spread {spreadR:P2} against a derived bound of {bound:P2}, over {ratios.Count} readings at " +
                $"depths {minDepth:F0}..{maxDepth:F0} m ({resolvedDepthRatio:F3}×). With a screen-constant " +
                "glyph size the ink extent is constant while the advance foreshortens, so this ratio moves " +
                "with depth. Full table above; the depth reach of this claim is carried to 8× in world metres " +
                "by WorldCellSize_AndWorldAdvance_ShareOneArcScale, not here.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // The maintainer's literal complaint, BRACKETING the measured 1.25× threshold
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>"The letters must never overlap", at and past the depth where a screen-constant glyph size
        /// first fails.</b> Proves: a map-pitched curved symbol segments into exactly <c>GlyphCount</c> ink runs,
        /// with a real gap between consecutive runs, at depth ratios <b>1.25</b> and <b>1.5</b> as well as at
        /// the shipped 2.0.
        ///
        /// <para>Non-obvious why: the cell is 32 px at <c>TextSizePx = 48</c> and the cross-arm gap is
        /// 40 px at the look-at, so a screen-constant size first merges at 1.25× depth, not at the horizon. At
        /// ratio 8 the whole symbol is 0.773 px, so a deep cell would be vacuous. Limitation: the iso-depth
        /// cross arm cannot tell a per-glyph size from a per-symbol constant;
        /// <see cref="RecedingSymbols_StaySeparable_AtBothDepths"/> is the discriminator. Each cell asserts
        /// that its anchor advance is at most the cell width, so a screen-constant size would merge there.</para>
        /// </summary>
        [Test]
        public void Letters_StaySeparable_PastTheMergeThreshold(
            [Values(1.25, 1.5)] double targetDepthRatio)
        {
            using var f = OffLookAtSymbolScene.Create(new OffLookAtSymbolSceneConfig
            {
                SizePx = 1024, TargetDepthRatio = targetDepthRatio,
            });

            SymbolMeasurement far = f.Measure(OffLookAtSymbolId.CrossFar);
            double minAdvancePx = double.MaxValue;
            for (int i = 0; i < far.ScreenSpacingPx.Length; i++)
                minAdvancePx = math.min(minAdvancePx, far.ScreenSpacingPx[i]);

            f.RowBandFor(OffLookAtSymbolId.CrossFar, out int rowFrom, out int rowTo);
            (int start, int end)[] runs = OffLookAtInkRuns.AlongColumns(f.InkPixels, f.Config.SizePx, rowFrom, rowTo);

            int minGap = int.MaxValue;
            for (int i = 0; i + 1 < runs.Length; i++)
                minGap = math.min(minGap, runs[i + 1].start - runs[i].end);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "merge threshold  ratio={0:F2} (achieved {1:F4})  CrossFar: runs={2}/{3}  min inter-run gap={4} px  " +
                "min anchor advance={5:F2} px  cell screen width={6:F2} px",
                targetDepthRatio, f.AchievedDepthRatio, runs.Length, f.Config.GlyphCount,
                runs.Length > 1 ? minGap : -1, minAdvancePx, f.GlyphCellScreenWidthPx));

            // NON-VACUITY: a screen-constant cell merges once the advance falls to the cell width. If it has
            // not fallen that far here, this cell tests nothing.
            Assert.That(minAdvancePx, Is.LessThanOrEqualTo(f.GlyphCellScreenWidthPx),
                $"merge threshold non-vacuity (ratio {targetDepthRatio:F2}): CrossFar's smallest anchor advance is " +
                $"{minAdvancePx:F2} px against a {f.GlyphCellScreenWidthPx:F2} px cell. The screen-constant model only " +
                "merges these letters once the advance has fallen to at most the cell width, so above that " +
                "this cell would pass under BOTH models and prove nothing.");

            Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                $"merge threshold (ratio {targetDepthRatio:F2}, achieved {f.AchievedDepthRatio:F4}): a map-pitched " +
                $"label must still segment into {f.Config.GlyphCount} separable ink runs at " +
                $"{f.AchievedDepthRatio:F2}× the look-at depth — got {runs.Length} in row band " +
                $"[{rowFrom}, {rowTo}]. Equation (6) fixes the gap at AdvanceBakedPx/cellWidthBaked cell " +
                "widths at EVERY depth, so a merged label here means the size is not on the world ruler. " +
                "This is the maintainer's literal complaint: 'it won't make the letters move closer together'.");
        }

        /// <summary>
        /// <b>The control BELOW the threshold.</b> The near (look-at) cross-azimuth symbol must
        /// segment into <c>GlyphCount</c> runs. NOT a discriminator: at the look-at the two
        /// competing rulers coincide exactly, so this cell is separable under BOTH models. It is what says
        /// the segmentation machinery works at all before the cells above are believed.
        /// </summary>
        [Test]
        public void Letters_AreSeparable_AtTheLookAt_Control()
        {
            using var f = OffLookAtSymbolScene.Create(InkRunPose());
            f.RowBandFor(OffLookAtSymbolId.CrossNear, out int rowFrom, out int rowTo);
            (int start, int end)[] runs = OffLookAtInkRuns.AlongColumns(f.InkPixels, f.Config.SizePx, rowFrom, rowTo);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "look-at control  CrossNear: runs={0}/{1}  (separable under BOTH models — not a discriminator)",
                runs.Length, f.Config.GlyphCount));

            Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                $"look-at control: the LOOK-AT label must segment into {f.Config.GlyphCount} runs, got " +
                $"{runs.Length}. This cell cannot discriminate the model — it fails only if the ink " +
                "segmentation itself is broken, which is exactly what it is here to rule out.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // The world identity through the REAL emit, at the fixture's ceiling
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Leg (a) — the ABSOLUTE world cell size, and the DPR factor.</b> Proves: the corner offsets
        /// the renderer actually wrote into the slot mesh are WORLD METRES of exactly
        /// <c>cellWidthBaked · TextSizePx/OneEm · MetresPerLogicalPixel</c>, read from
        /// <c>WorldBillboardVertex.Offset</c> on a real built mesh. Non-obvious why: the expectation uses the
        /// fixture's OWN <c>MetresPerLogicalPixel</c>, so the DPR leg is not vacuous. This leg carries the
        /// absolute scale that leg (b) divides away; an omitted DPR factor shows as ×2 at the dpr2 pose.
        /// </summary>
        [Test]
        public void WorldCellSize_IsTheBakedCellOnTheWorldRuler(
            [Values("shipped", "deep", "dpr2")] string poseName)
        {
            using var f = OffLookAtSymbolScene.Create(PoseByName(poseName));

            double emScale = f.Config.TextSizePx / TextQuadLayout.OneEm;
            double expected = f.GlyphCellWidthBakedPx * emScale * f.MetresPerLogicalPixel;

            // EVERY glyph: the corner scale is per VERTEX, and leg (b) divides it away, so a divergence on
            // glyphs 1..N-1 would otherwise go unseen.
            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                int glyphs = GlyphQuadCount(f, id);
                Assert.That(glyphs, Is.EqualTo(f.Config.GlyphCount),
                    $"world cell size precondition ({id}): expected {f.Config.GlyphCount} staged glyphs, got {glyphs}.");

                for (int g = 0; g < glyphs; g++)
                {
                    double measured = WorldCellWidth(f, id, g);
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "world cell size  pose={0}  {1}[{2}]  cellWidth_world={3:F4} m  expected={4:F4} m  " +
                        "(cellWidthBaked={5:F3} × emScale={6:F3} × mppLogical={7:F4})",
                        poseName, id, g, measured, expected, f.GlyphCellWidthBakedPx, emScale,
                        f.MetresPerLogicalPixel));

                    Assert.That(measured, Is.EqualTo(expected).Within(0.01).Percent,
                        $"world cell size ({poseName}, {id}, glyph {g}): the emitted corner offsets must be WORLD " +
                        $"METRES of cellWidthBaked · TextSizePx/OneEm · MetresPerLogicalPixel = " +
                        $"{expected:F4} m; the mesh carries {measured:F4} m. A clean ×2 or ÷2 here is a " +
                        "dropped or doubled DevicePixelRatio; a factor of ~MetresPerLogicalPixel is offsets " +
                        "still in pixels; a value that differs BETWEEN glyphs of one label is a per-glyph " +
                        "ruler.");
                }
            }
        }

        /// <summary>
        /// <b>Leg (b) — THE TOOTH THAT SAYS SIZE AND SPACING CAME FROM ONE CONSTANT.</b> Proves:
        /// <c>worldAdvance / cellWidth_world == AdvanceBakedPx / cellWidthBaked</c> — equation (5), with every
        /// scale cancelled — and that it reads the SAME number at the shipped pose, at DPR 2, and at the
        /// fixture's deepest constructible pose (tilt 72°, ratio 8, far anchor at ≈ 1 084 562 m).
        /// Non-obvious why: a quotient of two WORLD lengths has no legibility limit, so it reads a real mesh at
        /// 8× depth; <c>MapPitchedWorldArcStagingTests.WorldCellToAdvanceRatio_HoldsAtEveryMagnitude</c> goes
        /// further on the CPU. A doubled corner scale cancels here and shows in leg (a); a corner unit left
        /// in pixels fails here.
        /// </summary>
        [Test]
        public void WorldCellSize_AndWorldAdvance_ShareOneArcScale(
            [Values("shipped", "deep", "dpr2")] string poseName)
        {
            using var f = OffLookAtSymbolScene.Create(PoseByName(poseName));

            double expected = f.Config.AdvanceBakedPx / f.GlyphCellWidthBakedPx; // eq. (5), both sides baked

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                double[] worldSpacing = f.Measure(id).WorldSpacingM;
                for (int i = 0; i < worldSpacing.Length; i++)
                {
                    // Gap i's OWN glyph cell, not glyph 0's — so a per-glyph corner scale cannot hide here
                    // either.
                    double cellWidthWorld = WorldCellWidth(f, id, i);
                    double ratio = worldSpacing[i] / cellWidthWorld;
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "shared arc scale  pose={0}  {1} gap {2}  worldAdvance={3:F3} m  cellWidth_world={4:F3} m  " +
                        "ratio={5:F6}  expected={6:F6}",
                        poseName, id, i, worldSpacing[i], cellWidthWorld, ratio, expected));

                    Assert.That(ratio, Is.EqualTo(expected).Within(0.05).Percent,
                        $"shared arc scale ({poseName}, {id}, gap {i}): worldAdvance / cellWidth_world must be the " +
                        $"purely typographic AdvanceBakedPx/cellWidthBaked = {expected:F6}, measured " +
                        $"{ratio:F6}. Both sides are world lengths produced by the SAME arcScale, so every " +
                        "scale cancels — a different number means the drawn size and the anchor spacing did " +
                        "NOT come from one constant, which is the whole of the world-metre model.");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // The SPACING half, fenced at 8×
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>SPAN/ROAD is depth-invariant at 8× the look-at depth.</b> Proves: each receding
        /// symbol's staged world span, as a fraction of its own road length, is the SAME at the deep
        /// tilt-72/ratio-8 pose as at the shipped tilt-55/ratio-2 one. It fences the SPACING half beside the
        /// size teeth, and it shares their tilt-72 pose. Non-obvious why: a ratio of two WORLD lengths ignores
        /// glyph size, so it holds where nothing is legible. The reference is the ratio-2 reading from the same
        /// run, not the literal <c>1/(2·SpillMargin)</c>. A per-symbol SIZE ruler leaves it GREEN; a screen walk
        /// fails it.
        /// </summary>
        [Test]
        public void SymbolSpanPerRoad_IsDepthInvariant_AtEightTimesDepth()
        {
            using var shipped = OffLookAtSymbolScene.Create(ShippedPose());
            using var deep    = OffLookAtSymbolScene.Create(DeepPose());

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                double shippedRatio = SpanOverRoad(shipped, id);
                double deepRatio    = SpanOverRoad(deep, id);
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "span/road  {0}  SPAN/ROAD: shipped(tilt {1:F0}, ratio {2:F2})={3:F8}  " +
                    "deep(tilt {4:F0}, ratio {5:F2})={6:F8}",
                    id, shipped.Config.TiltDegrees, shipped.AchievedDepthRatio, shippedRatio,
                    deep.Config.TiltDegrees, deep.AchievedDepthRatio, deepRatio));

                Assert.That(deepRatio, Is.EqualTo(shippedRatio).Within(0.01).Percent,
                    $"span/road ({id}): the label must occupy the same fraction of its road at " +
                    $"{deep.AchievedDepthRatio:F2}× the look-at depth as at " +
                    $"{shipped.AchievedDepthRatio:F2}× — shipped {shippedRatio:F8}, deep {deepRatio:F8}. " +
                    "Both are ratios of two WORLD lengths, so the drawn glyph size cannot affect them; a " +
                    "difference means the arc WALK became depth-dependent, i.e. a spacing regression, not a size one.");
            }
        }

        /// <summary>
        /// <b>The world residual at 8×.</b> Proves: at the deep pose every staged gap is
        /// <c>AdvanceWorldMetres</c> to within <b>0.01 %</b>.
        ///
        /// <para>Non-obvious why: the 0.01 % bound sits above the fixture's RTC artifact and far below the real
        /// floor. All six symbols take the LOOK-AT's tile key, so far symbols bake <c>float3 AnchorLocal</c>
        /// ≈ 1e6 m from their origin (4.05e-4 % drift at ratio 5; production uses the symbol's own tile). The
        /// floor is the <c>float</c> cumulative arc, which needs a ≈ 2–3 × 10⁹ m road to breach 1 %.</para>
        /// </summary>
        [Test]
        public void WorldGapResidual_StaysBounded_AtEightTimesDepth()
        {
            using var f = OffLookAtSymbolScene.Create(DeepPose());
            double expected = f.AdvanceWorldMetres;

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                double[] gaps = f.Measure(id).WorldSpacingM;
                for (int i = 0; i < gaps.Length; i++)
                {
                    double residualPct = (gaps[i] / expected - 1.0) * 100.0;
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "world gap residual  {0} gap {1}  world={2:F4} m  expected={3:F4} m  residual={4:F6} %",
                        id, i, gaps[i], expected, residualPct));

                    Assert.That(gaps[i], Is.EqualTo(expected).Within(0.01).Percent,
                        $"world gap residual ({id}, gap {i}): the staged world gap is {gaps[i]:F4} m against " +
                        $"{expected:F4} m ({residualPct:F6} %), bound 0.01 %. BEFORE reading this as a size " +
                        "defect: this fixture gives all six labels tile keys derived from the LOOK-AT's z14 " +
                        "tile, so its far labels sit ~1e6 m from their nominal TileOriginRender and the " +
                        "float3 AnchorLocal RTC bake at that distance is a known ~4e-4 % artifact of the " +
                        "FIXTURE, not of production (where a label's tile origin is its own tile's, a few km " +
                        "away). The bound is ~25× that artifact and four orders below the float-cumulative " +
                        "floor.");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Helpers — all fixture-side; no production member exists for these teeth (test-code-bloat rule)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Tilt 72°, ratio 8 — the deep cell this fixture measures at (<c>OffLookAtSymbolScene</c>
        /// disables the far-distance cull that would drop its far anchors). Tilt, not zoom, is the knob: every
        /// world length is a multiple of <c>MetresPerDevicePixel</c>, so the fixture is scale-invariant in
        /// zoom, while tilt brings the horizon into frame.</summary>
        private static OffLookAtSymbolSceneConfig DeepPose()
            => new OffLookAtSymbolSceneConfig { TiltDegrees = 72.0, TargetDepthRatio = 8.0 };

        private static OffLookAtSymbolSceneConfig PoseByName(string poseName)
        {
            switch (poseName)
            {
                case "shipped": return ShippedPose();
                case "deep":    return DeepPose();
                case "dpr2":    return new OffLookAtSymbolSceneConfig { DevicePixelRatio = 2.0 };
                default:        throw new System.ArgumentOutOfRangeException(nameof(poseName), poseName, null);
            }
        }

        /// <summary>One glyph's drawn cell WIDTH, in the unit its <c>Offset</c> carries — world metres for a
        /// map-pitched symbol. Read as <c>topRight.x − topLeft.x</c> off the emitted quad, which is exactly
        /// <c>(Cell.BottomRight.x − Cell.TopLeft.x) · emScale</c> because a curved symbol's per-quad rotation
        /// is forced to 0 (the shader supplies the tangent instead) and its <c>ExtraRotationRadians</c> is 0
        /// for text. Uses the symbol's FIRST glyph and reports which.</summary>
        private static double WorldCellWidth(OffLookAtSymbolScene f, OffLookAtSymbolId id, int glyphIndex)
        {
            WorldBillboardVertex[] v = f.Vertices(id);
            Assert.That(v.Length, Is.GreaterThanOrEqualTo(4 * (glyphIndex + 1)),
                $"WorldCellWidth precondition ({id}): the slot mesh carries {v.Length} vertices, too few for glyph " +
                $"{glyphIndex}.");
            // BuildWorldQuad's winding is topLeft, topRight, bottomRight, bottomLeft.
            return v[4 * glyphIndex + 1].Offset.x - v[4 * glyphIndex + 0].Offset.x;
        }

        /// <summary>The number of glyphs on this symbol's slot mesh (4 vertices each).</summary>
        private static int GlyphQuadCount(OffLookAtSymbolScene f, OffLookAtSymbolId id)
            => f.Vertices(id).Length / 4;

        /// <summary>A symbol's staged WORLD span (first glyph anchor to last) as a fraction of its own road's
        /// total world length. Both sides are world lengths measured off the built mesh and the fixture's own
        /// road construction, so the drawn glyph size cannot enter.</summary>
        private static double SpanOverRoad(OffLookAtSymbolScene f, OffLookAtSymbolId id)
        {
            GlyphMeasurement[] g = f.Measure(id).Glyphs;
            double span = math.length(g[g.Length - 1].WorldUnity - g[0].WorldUnity);
            double2 half = f.RoadHalfLengthsM[id];
            return span / (half.x + half.y);
        }

        /// <summary>The symbol's glyphs ordered by increasing ink-buffer ROW, so element <c>i</c> corresponds
        /// to ink run <c>i</c>. <c>InkPixels</c> is row-flipped (row 0 = top scanline), so the row of a glyph
        /// is <c>SizePx − 1 − ScreenPx.y</c> and a symbol running "up-screen" has its glyph ARRAY order
        /// reversed relative to its run order. Getting this wrong would invert every depth in the printed
        /// table while leaving the ratios untouched — a silent mis-attribution rather than a failure.</summary>
        private static GlyphMeasurement[] OrderedByScreenRow(OffLookAtSymbolScene f, GlyphMeasurement[] glyphs)
        {
            double firstRow = f.Config.SizePx - 1 - glyphs[0].ScreenPx.y;
            double lastRow  = f.Config.SizePx - 1 - glyphs[glyphs.Length - 1].ScreenPx.y;
            if (firstRow <= lastRow) return glyphs;
            var reversed = new GlyphMeasurement[glyphs.Length];
            for (int i = 0; i < glyphs.Length; i++) reversed[i] = glyphs[glyphs.Length - 1 - i];
            return reversed;
        }
    }

    // Unity EditMode only — real MapCamera + Camera/RenderTexture, off-screen GPU render + CPU readback.
    // NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // The RENDERED teeth for the device-pixel-ratio convention. Every quantity is measured at dpr 1 and dpr 2
    // and asserted as a RATIO: a "changed" assertion lets a dpr² error through, and a ratio absorbs AA offsets.
    //
    // Non-obvious why: the fixture shape is load-bearing. The swept variable is a real MapCamera's
    // DevicePixelRatio, and the rendered camera IS that MapCamera's own camera. The physical framebuffer (the
    // camera's RenderTexture and SnapshotRenderer's _rt) stays Size×Size at every dpr, or everything cancels.
    // What moves is the camera: its altitude is framed from the LOGICAL viewport, so at dpr 2 it halves and
    // every world-anchored quantity doubles in device px. That is the control.

    // ───────────────────────────────────────────────────────────────────────────────────
    // DevicePixelRatioSnapshotTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class DevicePixelRatioSnapshotTests : BaseTestFixture
    {
        private const int    Size       = 512;
        private const double SweptZoom  = 8.0;
        private const double LookAtLat  = 30.0;
        private const double LookAtLon  = 30.0;

        /// <summary>The two ratios swept. 1 is the only one the rest of the suite runs at; 2 is where every
        /// division in the codebase stops being the identity.</summary>
        private const double Dpr1 = 1.0;
        private const double Dpr2 = 2.0;

        /// <summary>Ratio tolerance. Generous enough for the ±0.5 px the AA fixture allows at these
        /// magnitudes and for whole-pixel quantisation of the glyph bbox, far tighter than the 1.0 (no
        /// conversion) and 4.0 (dpr² applied twice) it has to reject.</summary>
        private const double RatioTolerance = 0.12;

        /// <summary>Styled line width in LOGICAL px. Well clear of both thin-line clamps at both ratios —
        /// the min-width floor binds below a 1 px half-width — so the ratio stays linear. A 1 px line here
        /// would produce a confusing false RED.</summary>
        private const float StyledLineWidthPx = 16f;

        /// <summary>Device-px width the world-metre "ground feature" is sized to at dpr 1.</summary>
        private const float GroundFeatureDevicePx = 40f;

        /// <summary>Symbol size in LOGICAL px — big enough that a ±1 px bbox quantisation is under 2 %.</summary>
        private const float TextSizePx = 80f;

        private static readonly Color BgColor     = new Color(0.05f, 0.05f, 0.08f, 1f);
        private static readonly Color GroundColor = new Color(0.20f, 0.45f, 0.98f, 1f);
        private static readonly float4 TextInk   = new float4(0.1f, 0.85f, 0.1f, 1f);

        private const string LineStyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""road"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""paint"": { ""line-color"": [""rgba"", 242, 153, 38, 1], ""line-width"": 16 } }
            ]
        }";

        // text-color white: a constant text-color binds _TextColor, and the black default would multiply
        // MeasureTextHeightPx's hand-injected TextInk vertex colour down to black.
        private const string SymbolStyleJson = @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""layout"": { ""text-field"": ""{NAME}"" },
                  ""paint"": { ""text-color"": ""#ffffff"" } }
            ]
        }";

        // ── The swept scene ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A real <see cref="MapCamera"/> whose <see cref="MapCamera.DevicePixelRatio"/> is the swept
        /// variable, wrapping a Unity camera bound to a FIXED Size×Size RenderTexture. Everything rendered
        /// through <see cref="UnityCamera"/> therefore sees this fixture's ratio.
        /// </summary>
        private sealed class SweptScene : IDisposable
        {
            public GameObject    CamGo;
            public Camera        UnityCamera;
            public RenderTexture ViewportRt;   // framebuffer size holder #1 — Size×Size at EVERY dpr
            public MapCamera     MapCam;
            public SceneFrame    Frame;

            /// <summary>World metres per DEVICE pixel at the ground plane. Halves at dpr 2 because the
            /// altitude does — this is the whole reason a world-anchored feature's device span doubles.</summary>
            public double MetresPerDevicePx;

            public void Dispose()
            {
                if (CamGo != null) UnityEngine.Object.DestroyImmediate(CamGo);
                if (ViewportRt != null)
                {
                    ViewportRt.Release();
                    UnityEngine.Object.DestroyImmediate(ViewportRt);
                }
            }
        }

        private static SweptScene BuildScene(double devicePixelRatio)
        {
            var camGo = new GameObject("Dpr_TestCamera");
            var uCam  = camGo.AddComponent<Camera>();

            // The framebuffer. Fixed size, independent of the ratio — see the header.
            var rt = new RenderTexture(Size, Size, 0);
            uCam.targetTexture   = rt;
            uCam.clearFlags      = CameraClearFlags.SolidColor;
            uCam.backgroundColor = BgColor;
            uCam.enabled         = false;

            var props = new CameraProperties(
                new GeoCoordinate3D { Latitude = LookAtLat, Longitude = LookAtLon, Altitude = 0.0 },
                zoom: SweptZoom, heading: 0.0, tilt: 0.0);
            // The ctor's 5th argument IS the swept variable (pinned by CameraTransformTests' 2× altitude tooth).
            var mapCam = new MapCamera(uCam, props, 1f, null, devicePixelRatio);

            // Precondition: the PHYSICAL viewport is identical at every ratio, or the altitude and framebuffer
            // changes cancel and every tooth below passes vacuously.
            Assert.That(mapCam.ViewportPx.x, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport width must stay {Size} at dpr {devicePixelRatio}.");
            Assert.That(mapCam.ViewportPx.y, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport height must stay {Size} at dpr {devicePixelRatio}.");

            // Tilt 0 ⇒ the camera sits straight above the look-at, which camera-relative rendering places at
            // the world origin. Ground half-height = altitude·tan(fov/2), over Size/2 device pixels.
            double altitude = math.length(mapCam.CameraRelativePosition);
            double halfFov  = math.radians(mapCam.CurrentProperties.VerticalFovDeg) * 0.5;
            double metresPerDevicePx = 2.0 * altitude * math.tan(halfFov) / Size;

            // Non-local invariant: the line shader's frame constant is PROCESS state, so an unpushed fixture
            // renders against an earlier fixture's ruler. The MapCamera ctor pushes it; this asserts that,
            // because the failure is silent and looks like a conversion bug elsewhere.
            Assert.That((double)Shader.GetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel),
                Is.EqualTo(metresPerDevicePx).Within(0.1).Percent,
                $"at dpr {devicePixelRatio} the pushed frame constant must be this scene's own metres per " +
                $"device px ({metresPerDevicePx:F6}). A stale value here scales every styled line width by " +
                "exactly its own ratio and nothing else in the frame moves with it.");

            return new SweptScene
            {
                CamGo             = camGo,
                UnityCamera       = uCam,
                ViewportRt        = rt,
                MapCam            = mapCam,
                MetresPerDevicePx = metresPerDevicePx,
                Frame = new SceneFrame
                {
                    SceneOriginRender = mapCam.Projection.Project(
                        new GeoCoordinate { Latitude = LookAtLat, Longitude = LookAtLon }),
                    Rebase = float3x3.identity,
                },
            };
        }

        // Ambient/light setup so a real lit Map/Line material reads back strongly — copied from
        // SymbolLayerOrderSnapshotTests, which took it from LayerOrderSnapshotTests.
        private static (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) SetupLitAmbient()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevMode  = RenderSettings.ambientMode;
            var prevLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);
            return (prevQuality, prevMode, prevLight);
        }

        private static void RestoreAmbient((int quality, UnityEngine.Rendering.AmbientMode mode, Color light) saved)
        {
            QualitySettings.SetQualityLevel(saved.quality, false);
            RenderSettings.ambientMode  = saved.mode;
            RenderSettings.ambientLight = saved.light;
        }

        private static GameObject BuildDirectionalLight()
        {
            var go = new GameObject("Dpr_DirLight");
            go.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = go.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;
            return go;
        }

        // ── Line arms ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A short horizontal ribbon centred on the look-at. SHORT is deliberate: the shader measures
        /// <c>pxToWorld</c> per vertex, so stations far out in a perspective frustum would extrude to a
        /// different width than the frame centre and the cut would not measure the styled width.
        /// </summary>
        private static Mesh BuildCentredRibbon(SweptScene scene)
        {
            double halfLength = 40.0 * scene.MetresPerDevicePx; // ±40 device px — spans the cut column
            return SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-halfLength, 0.0), new double2(halfLength, 0.0) },
                JoinType.Miter, CapType.Butt);
        }

        private static GameObject AttachMesh(Mesh mesh, Material mat, string name)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Σ coverage across a vertical cut through the frame centre — the band's apparent width in
        /// DEVICE pixels. Asserts the band actually rendered first (N9): a ratio over a missing feature reads
        /// as a confusing zero rather than as "the feature is not there".</summary>
        private static float MeasureBandWidthPx(SnapshotRenderer snap, string what)
        {
            Frame frame        = snap.Pixels;
            float3 background = PixelCoverage.BackgroundLinear(frame);

            const int column  = Size / 2;
            const int rowFrom = Size / 2 - 80;
            const int rowTo   = Size / 2 + 80;

            float3 plateau = PixelCoverage.PlateauOnColumn(frame, column, rowFrom, rowTo, background);
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"{what}: the band's plateau {plateau} is indistinguishable from the background {background} — " +
                "it did not render, so no width is measurable and no ratio formed from it would mean anything.");

            float[] profile = PixelCoverage.CoverageProfileOnColumn(
                frame, column, rowFrom, rowTo, background, plateau);
            float measured = PixelCoverage.CoverageIntegral(profile);

            Assert.That(measured, Is.GreaterThan(1f),
                $"{what}: coverage integral is {measured:F3} px. Profile: " +
                PixelCoverage.FormatProfile(profile, rowFrom));
            return measured;
        }

        /// <summary>
        /// The styled <c>line-width</c> arm — bound through the PRODUCTION seam
        /// (<see cref="MaterialFactory.BindLinePaintToApplier"/> + <see cref="ZoomStyleApplier.ApplyZoom"/>),
        /// which is the only place the ratio can enter a line's width.
        /// </summary>
        private static float MeasureStyledLineWidthPx(SweptScene scene, SnapshotRenderer snap)
        {
            StyleDocument style = StyleParser.Parse(LineStyleJson);
            var lineLayer = (Line.StyleLayer)style.Layers[0];

            using var bag = new ObjectDisposalBag();
            Material mat = bag.Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Line base material must be configured for this fixture.");

            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindLinePaintToApplier(lineLayer.Paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(SweptZoom, scene.MapCam.DevicePixelRatio, 0.0));

            Mesh mesh = bag.Track(BuildCentredRibbon(scene));
            GameObject go = bag.Track(AttachMesh(mesh, mat, "Dpr_StyledLine"));
            snap.Render(scene.UnityCamera);
            snap.WritePng($"dpr-styled-line-{scene.MapCam.DevicePixelRatio:F1}.png");
            return MeasureBandWidthPx(snap, $"styled line-width {StyledLineWidthPx} px at dpr {scene.MapCam.DevicePixelRatio}");
        }

        /// <summary>
        /// The GROUND-FEATURE arm — a ribbon whose width is a fixed number of world METRES
        /// (<c>_WidthIsPixels = 0</c>), so no style seam and no ratio touches it. Its on-screen device span
        /// doubles at dpr 2 purely because the camera altitude halves. This is the control the styled arm has
        /// to match.
        /// </summary>
        private static float MeasureGroundSpanPx(SweptScene scene, SnapshotRenderer snap, double groundWidthMetres)
        {
            using var bag = new ObjectDisposalBag();
            Material mat = bag.Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            mat.SetColor("_BaseColor",     GroundColor);
            mat.SetFloat("_Opacity",       1f);
            mat.SetFloat("_Width",         (float)groundWidthMetres);
            mat.SetFloat("_WidthIsPixels", 0f);

            Mesh mesh = bag.Track(BuildCentredRibbon(scene));
            GameObject go = bag.Track(AttachMesh(mesh, mat, "Dpr_GroundFeature"));
            snap.Render(scene.UnityCamera);
            snap.WritePng($"dpr-ground-span-{scene.MapCam.DevicePixelRatio:F1}.png");
            return MeasureBandWidthPx(snap, $"ground feature ({groundWidthMetres:F1} m) at dpr {scene.MapCam.DevicePixelRatio}");
        }

        // ── Symbol arm ────────────────────────────────────────────────────────────────────────────

        private static byte[] LoadGlyphFixture(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;
            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        private static (GlyphAtlasTexture texture, List<SymbolQuad> quads, TextLayoutBounds bounds) BuildGlyphA()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadGlyphFixture("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0); // 'A'
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);
            return (texture, quads, bounds);
        }

        /// <summary>
        /// The <c>text-size</c> arm. The glyph quad's px offsets are divided by <c>_ScreenParamsLogical</c>
        /// in the shader, which <see cref="SymbolPlacementSystem"/> fills from
        /// <see cref="MapCamera.ViewportLogicalPx"/> — so at dpr 2 the LOGICAL viewport halves and the glyph's
        /// DEVICE footprint doubles, with nothing multiplied at the style seam. This arm is what makes T3 a
        /// statement about pixels rather than about a uniform.
        /// </summary>
        private static float MeasureTextHeightPx(SweptScene scene, SnapshotRenderer snap)
        {
            var (glyphAtlas, quads, bounds) = BuildGlyphA();
            StyleDocument style = StyleParser.Parse(SymbolStyleJson);
            var settings = MapMaterialSetTestUtil.Load();
            var renderLayer = SymbolRenderLayer.Create((Symbol.StyleLayer)style.Layers[0], settings, SweptZoom, drawIndex: 0);
            Assert.IsNotNull(renderLayer.Material, "MapMaterialSet.SymbolTextWorld must be assigned.");
            renderLayer.Material.renderQueue = LayerDrawOrder.TransparentQueue + 1;

            var system = new SymbolPlacementSystem(scene.MapCam,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, scene.Frame.SceneOriginRender, quads, bounds.Min, bounds.Max,
                paint: new SymbolPaint { TextColor = TextInk, Opacity = 1f },
                textSizePx: TextSizePx, sortKey: 0f, featureIndex: 0,
                // A realistic containing tile keeps the world-anchored bake float32-safe (TileKey=0 would be
                // ~2e7 m away) — the same note every world-symbol fixture carries.
                tileKey: TestTileKeys.PackedContaining(
                    new GeoCoordinate { Latitude = LookAtLat, Longitude = LookAtLon }, zoom: 14),
                materialIndex: 0, allowOverlap: true);
            var layers = new List<SymbolRenderLayer> { renderLayer };

            using var plan = new TestSymbolPlan(scene.MapCam.Projection);
            try
            {
                // Duplicated Tick — the collision verdict is harvested one Tick late.
                system.Tick(in scene.Frame, plan.Build(buffer), glyphAtlas, float.PositiveInfinity, layers);
                system.Tick(in scene.Frame, plan.Build(buffer), glyphAtlas, float.PositiveInfinity, layers);
                Assert.AreEqual(1, system.LastQuadCount,
                    $"the single 'A' must place at dpr {scene.MapCam.DevicePixelRatio} (precondition, not the tooth).");

                snap.Render(scene.UnityCamera);
                snap.WritePng($"dpr-label-{scene.MapCam.DevicePixelRatio:F1}.png");
                return MeasureInkHeightPx(snap, scene.MapCam.DevicePixelRatio);
            }
            finally
            {
                renderLayer.Dispose(); // before the system disposes its meshes
                system.Dispose();
                glyphAtlas.Dispose();
            }
        }

        /// <summary>Bounding-box height, in device px, of the rendered glyph's ink — rows carrying at least
        /// half coverage on the background→ink axis.</summary>
        private static float MeasureInkHeightPx(SnapshotRenderer snap, double dpr)
        {
            Frame frame        = snap.Pixels;
            float3 background = PixelCoverage.BackgroundLinear(frame);

            // The ink plateau: the pixel furthest from the background anywhere in frame (deep inside the
            // glyph body, never an AA edge).
            float3 plateau  = background;
            float  bestDist = 0f;
            for (int row = 0; row < Size; row++)
            for (int column = 0; column < Size; column++)
            {
                float3 c    = PixelCoverage.SampleLinear(frame, column, row);
                float  dist = math.distancesq(c, background);
                if (dist > bestDist) { bestDist = dist; plateau = c; }
            }
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"dpr {dpr}: no ink distinguishable from the background — the label did not render, so its " +
                "height is not measurable.");

            int minRow = int.MaxValue, maxRow = int.MinValue;
            for (int row = 0; row < Size; row++)
            {
                bool inked = false;
                for (int column = 0; column < Size && !inked; column++)
                    inked = PixelCoverage.CoverageAt(frame, column, row, background, plateau) >= 0.5f;
                if (!inked) continue;
                if (row < minRow) minRow = row;
                if (row > maxRow) maxRow = row;
            }
            Assert.That(maxRow, Is.GreaterThanOrEqualTo(minRow),
                $"dpr {dpr}: no row reached half ink coverage — nothing to measure.");
            return maxRow - minRow + 1;
        }

        // ── T2 ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>T2.</b> Over a dpr 1 → 2 sweep on a FIXED framebuffer, a styled <c>line-width</c> road's
        /// rendered device width and a known ground feature's on-screen device span scale by the SAME factor,
        /// and that factor is exactly 2. A raw <c>_Width</c> with a per-DEVICE-px <c>pxToWorld</c> would keep
        /// the road at W device px (ratio 1.00) while the ground span doubles.
        /// </summary>
        [Test]
        public void LineWidth_AndGroundSpan_ScaleTogetherAcrossDpr()
        {
            var saved   = SetupLitAmbient();
            var lightGo = Track(BuildDirectionalLight());
            using var snap = new SnapshotRenderer(Size, Size); // framebuffer size holder #2 — Size×Size, fixed
            try
            {
                // The ground feature's world size is fixed ONCE, from the dpr-1 camera, and reused verbatim at
                // dpr 2 — that is what makes it a fixed GROUND quantity rather than a re-derived screen one.
                double groundWidthMetres;
                float lineAt1, groundAt1;
                using (var scene1 = BuildScene(Dpr1))
                {
                    groundWidthMetres = GroundFeatureDevicePx * scene1.MetresPerDevicePx;
                    lineAt1   = MeasureStyledLineWidthPx(scene1, snap);
                    groundAt1 = MeasureGroundSpanPx(scene1, snap, groundWidthMetres);
                }

                float lineAt2, groundAt2;
                using (var scene2 = BuildScene(Dpr2))
                {
                    double metresPerDevicePxAt1 = groundWidthMetres / GroundFeatureDevicePx;
                    Assert.That(scene2.MetresPerDevicePx,
                        Is.EqualTo(0.5 * metresPerDevicePxAt1).Within(0.1).Percent,
                        "precondition: the camera altitude must HALVE at dpr 2 (MapCamera frames from the " +
                        "logical viewport). If it does not, the ground arm cannot move and the sweep is inert.");
                    lineAt2   = MeasureStyledLineWidthPx(scene2, snap);
                    groundAt2 = MeasureGroundSpanPx(scene2, snap, groundWidthMetres);
                }

                double lineRatio   = lineAt2   / lineAt1;
                double groundRatio = groundAt2 / groundAt1;
                TestContext.WriteLine(
                    $"T2: styled line {lineAt1:F2} → {lineAt2:F2} px (ratio {lineRatio:F3}); " +
                    $"ground feature {groundAt1:F2} → {groundAt2:F2} px (ratio {groundRatio:F3})");

                Assert.That(groundRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a fixed GROUND feature must double in device px at dpr 2 (measured {groundAt1:F2} → " +
                    $"{groundAt2:F2} px, ratio {groundRatio:F3}). This arm does not touch the style seam — if " +
                    "it fails, the camera/framebuffer setup is wrong, not the conversion.");

                Assert.That(lineRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a styled line-width road must double in device px at dpr 2 (measured {lineAt1:F2} → " +
                    $"{lineAt2:F2} px, ratio {lineRatio:F3}). A ratio of 1.00 is the defect: _Width reaching " +
                    "the shader as raw logical px while pxToWorld measures the PHYSICAL framebuffer, so the " +
                    $"road keeps its literal screen width while the ground under it scales by {groundRatio:F3}.");

                Assert.That(lineRatio, Is.EqualTo(groundRatio).Within(RatioTolerance),
                    $"line ratio {lineRatio:F3} and ground ratio {groundRatio:F3} must agree — roads and the " +
                    "ground they sit on cannot drift apart with panel density.");
            }
            finally
            {
                RestoreAmbient(saved);
            }
        }

        // ── T2b — the ruler the styled arm is sized with ─────────────────────────────────────────

        /// <summary>
        /// <b>T2b.</b> At BOTH ratios the pushed <c>_MapFrameMetersPerDevicePixel</c> equals this
        /// scene's own metres per device pixel, and the pair halves exactly. Non-obvious why: a stale global from
        /// an earlier fixture (e.g. zoom 5 under a zoom-8 camera) scales every width by 2³ and looks like a broken
        /// conversion; this separate test names it directly.
        /// </summary>
        [Test]
        public void FrameConstant_IsTheScenesOwnMetresPerDevicePixel_AtBothRatios()
        {
            int id = ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel;

            double pushedAt1, sceneAt1, pushedAt2, sceneAt2;
            // BuildScene asserts the equality; these read the numbers back so the RATIO clause below has both
            // halves at once.
            using (var scene1 = BuildScene(Dpr1))
            {
                pushedAt1 = Shader.GetGlobalFloat(id);
                sceneAt1  = scene1.MetresPerDevicePx;
            }
            using (var scene2 = BuildScene(Dpr2))
            {
                pushedAt2 = Shader.GetGlobalFloat(id);
                sceneAt2  = scene2.MetresPerDevicePx;
            }

            TestContext.WriteLine(
                $"T2b: dpr 1 pushed {pushedAt1:F6} vs scene {sceneAt1:F6}; " +
                $"dpr 2 pushed {pushedAt2:F6} vs scene {sceneAt2:F6}; " +
                $"stale/pushed at dpr 1 = {2445.985 / pushedAt1:F3}");

            Assert.That(pushedAt1, Is.EqualTo(sceneAt1).Within(0.1).Percent,
                $"dpr 1: the shader's ruler is {pushedAt1:F6} m/device px while the camera's is " +
                $"{sceneAt1:F6}. A ratio of 8.000 means a stale MetersPerPixel(5.0) from another fixture.");
            Assert.That(pushedAt2, Is.EqualTo(sceneAt2).Within(0.1).Percent,
                $"dpr 2: the shader's ruler is {pushedAt2:F6} m/device px while the camera's is " +
                $"{sceneAt2:F6}.");

            Assert.That(pushedAt2, Is.EqualTo(0.5 * pushedAt1).Within(0.1).Percent,
                $"the ruler must HALVE at dpr 2 ({pushedAt1:F6} → {pushedAt2:F6}): the altitude is framed " +
                "from the logical viewport, so a device pixel covers half the ground. That halving is what " +
                "makes a styled px width double on screen, matching the ground arm's 2.000.");
        }

        // ── T3 (load-bearing) ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>T3.</b> A rendered line and a rendered symbol must scale by the SAME factor across the dpr
        /// sweep, and that factor must be 2. The against-2.0 clause catches a second dpr multiply on the
        /// symbol side (<c>textRatio == 4</c>). The two arms render one at a time through the swept MapCamera,
        /// because the ribbon crosses the look-at symbol.
        /// </summary>
        [Test]
        public void LineWidth_AndTextSize_ScaleByTheSameFactorAcrossDpr()
        {
            var saved   = SetupLitAmbient();
            var lightGo = Track(BuildDirectionalLight());
            using var snap = new SnapshotRenderer(Size, Size);
            try
            {
                float lineAt1, textAt1;
                using (var scene1 = BuildScene(Dpr1))
                {
                    lineAt1  = MeasureStyledLineWidthPx(scene1, snap);
                    textAt1 = MeasureTextHeightPx(scene1, snap);
                }

                float lineAt2, textAt2;
                using (var scene2 = BuildScene(Dpr2))
                {
                    lineAt2  = MeasureStyledLineWidthPx(scene2, snap);
                    textAt2 = MeasureTextHeightPx(scene2, snap);
                }

                double lineRatio  = lineAt2  / lineAt1;
                double textRatio = textAt2 / textAt1;
                TestContext.WriteLine(
                    $"T3: line {lineAt1:F2} → {lineAt2:F2} px (ratio {lineRatio:F3}); " +
                    $"label {textAt1:F2} → {textAt2:F2} px (ratio {textRatio:F3})");

                Assert.That(textRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a label's device footprint must double at dpr 2 — measured {textAt1:F2} → " +
                    $"{textAt2:F2} px, ratio {textRatio:F3}. A ratio near 4 means the label side was ALSO " +
                    "multiplied at the style seam on top of the _ScreenParamsLogical division it already has.");

                Assert.That(lineRatio, Is.EqualTo(2.0).Within(RatioTolerance),
                    $"a line's device width must double at dpr 2 — measured {lineAt1:F2} → {lineAt2:F2} px, " +
                    $"ratio {lineRatio:F3}.");

                Assert.That(lineRatio, Is.EqualTo(textRatio).Within(RatioTolerance),
                    $"THE SYMPTOM: line ratio {lineRatio:F3} vs label ratio {textRatio:F3}. Raising the " +
                    "device-pixel ratio must not enlarge the labels while leaving the roads at their literal " +
                    "screen width — that is the drift this tooth exists to catch.");
            }
            finally
            {
                RestoreAmbient(saved);
            }
        }
    }

    // Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL
    // SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader. NOT registered in
    // Tools/core-tests/core-tests.csproj (it renders).
    //
    // THE TILT-ZERO CALIBRATION ARM. Non-obvious why: at tilt 0 every ground point shares ONE view depth, where
    // `MetresPerLogicalPixel` is the ruler, so a map-pitched corner of `cornerPx · mppLogical` metres projects to
    // `cornerPx` logical px, which the viewport branch adds after projection. A map-pitched curved symbol and
    // its viewport twin (differing only in `ShapedSymbol.PitchAlignment`, no shared code) render alike. Teeth:
    //   • ABSOLUTE SCALE (ink count) — the ratio teeth in MapPitchedGlyphSizeTests cancel a uniform error `k`.
    //   • DPR factor (DPR 2 cells) — an omitted or duplicated ratio shows here as ×2.
    //   • ŷ SIGN (ink centroid, 45°/90°) — SYMBOL_WORLD_MAP_Y_SIGN is measured, not derived. At 0° the cell is
    //     symmetric under that mirror, so 0° alone would be vacuous.
    // The centroid is sound only here: at tilt 0 the ground projection is affine and commutes with it.
    // NO METRE LITERALS: every world length is a multiple of `scene.MetresPerDevicePixel`.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapPitchedGlyphSizeTiltZeroTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapPitchedGlyphSizeTiltZeroTests
    {
        private const int   SizePx     = 512;
        private const float TextSizePx = 160f;

        /// <summary>Half-length of the road, as a multiple of the frame ruler. Only has to exceed the chord
        /// probe's half-width (the symbol is ONE glyph, so its arc span is exactly 0 and the spill gate is
        /// trivially satisfied); 200 leaves a wide margin at both ends.</summary>
        private const double RoadHalfLengthRulerUnits = 200.0;

        /// <summary>Ink-count agreement bound, as a fraction. A scale error `k` reads as `k²`, so 2 % brackets
        /// a 1 % scale error; the real failure modes are ×4 (omitted DPR) or ~mpp×. The slack absorbs only
        /// the AA-edge disagreement between two routes to one clip position.</summary>
        private const double InkCountTolerance = 0.02;

        /// <summary>Ink-centroid agreement bound, in pixels. Same reasoning as above; the analogue reads
        /// 22.56 px for a flipped sign against exactly this bound, so the discrimination margin is ~22×.</summary>
        private const double InkCentroidTolerancePx = 1.0;

        /// <summary>An ink reading below this is not a rendered glyph — a blank frame, a GPU-context failure
        /// or a quad collapsed to a point would otherwise pass an agreement test by agreeing about nothing.
        /// Asserted on BOTH arms of every cell.</summary>
        private const int InkFloor = 500;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Ink COUNT: the ONLY clause that can see a uniform scale error
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>The absolute-scale clause (DPR 1 and 2; 0°, 45°, 90°).</b> Proves: at
        /// tilt 0 a map-pitched curved symbol covers the same number of ink pixels as its viewport-pitched
        /// twin, at both device-pixel ratios and at three road angles. It is the one size tooth that sees a
        /// uniform scale error `k` (as `k²`). At DPR 2 a ruler that dropped the ratio would halve the map arm
        /// while the viewport arm stays. Limitation: a mirror preserves ink count, so a flipped ŷ sign shows
        /// only in the separate centroid test.
        /// </summary>
        [Test]
        public void MapPitched_AtTiltZero_MatchesViewport_InkCount(
            [Values(1.0, 2.0)] double devicePixelRatio,
            [Values(0f, 45f, 90f)] float roadAngleDeg)
        {
            Measure(devicePixelRatio, roadAngleDeg, degenerateUp: false,
                out InkReading map, out InkReading viewport);

            double ratio = (double)map.Count / viewport.Count;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "tilt-0 ink count  DPR={0:F1} angle={1:F0}deg  map ink={2}  viewport ink={3}  ratio={4:F5}",
                devicePixelRatio, roadAngleDeg, map.Count, viewport.Count, ratio));

            Assert.That(ratio, Is.EqualTo(1.0).Within(InkCountTolerance),
                $"tilt-0 ink count (DPR {devicePixelRatio}, road {roadAngleDeg}°): at tilt 0 a map-pitched glyph must " +
                $"cover the same ink as its viewport twin — map {map.Count} px, viewport {viewport.Count} px, " +
                $"ratio {ratio:F5}. A uniform scale error k reads here as k²; the ratio teeth in " +
                "MapPitchedGlyphSizeTests cannot see one at all.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Ink CENTROID: the mirror/translation clause, and the SIGN
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>The mirror/translation clause (DPR 1 and 2; 0°, 45°, 90°).</b>
        /// Proves: at tilt 0 the map-pitched glyph's ink CENTROID sits within
        /// <see cref="InkCentroidTolerancePx"/> of its viewport twin's, at both ratios and all three angles.
        /// Non-obvious why: <c>SYMBOL_WORLD_MAP_Y_SIGN</c> cannot be derived on paper, because
        /// <c>WorldBillboardVertex.Offset</c> is y-DOWN, so this pins it. A flipped sign reads 22.56 px at
        /// 45°/90° against the 1.0 px bound; 0° is the control. The centroid is sound only at tilt 0, where
        /// the ground projection is affine (tilt opens a 12.41 px convexity gap).
        /// </summary>
        [Test]
        public void MapPitched_AtTiltZero_MatchesViewport_InkCentroid(
            [Values(1.0, 2.0)] double devicePixelRatio,
            [Values(0f, 45f, 90f)] float roadAngleDeg)
        {
            Measure(devicePixelRatio, roadAngleDeg, degenerateUp: false,
                out InkReading map, out InkReading viewport);

            double dRow = map.CentroidRow - viewport.CentroidRow;
            double dCol = map.CentroidCol - viewport.CentroidCol;
            double delta = math.sqrt(dRow * dRow + dCol * dCol);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "tilt-0 ink centroid  DPR={0:F1} angle={1:F0}deg  map=({2:F2}, {3:F2})  viewport=({4:F2}, {5:F2})  " +
                "delta={6:F3} px  (row, col)=({7:F3}, {8:F3})",
                devicePixelRatio, roadAngleDeg, map.CentroidRow, map.CentroidCol,
                viewport.CentroidRow, viewport.CentroidCol, delta, dRow, dCol));

            Assert.That(delta, Is.LessThan(InkCentroidTolerancePx),
                $"tilt-0 ink centroid (DPR {devicePixelRatio}, road {roadAngleDeg}°): the map-pitched glyph's ink " +
                $"centroid is {delta:F3} px from its viewport twin's — map " +
                $"({map.CentroidRow:F2}, {map.CentroidCol:F2}), viewport " +
                $"({viewport.CentroidRow:F2}, {viewport.CentroidCol:F2}). A flipped " +
                "SYMBOL_WORLD_MAP_Y_SIGN moves this by tens of pixels at 45°/90° while leaving the ink COUNT " +
                "at exactly 1.0000 (a mirror is an isometry). If this is RED at 45°/90° but green at 0°, the " +
                "sign is wrong — 0° cannot discriminate it.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // The degenerate frame degrades in METRES, not into a pixel formula
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>The degenerate-frame branch is observed, not merely written.</b> Proves: a map-pitched
        /// curved symbol whose per-vertex <c>Up</c> is <see cref="float3.zero"/> still renders, and at tilt 0
        /// with a screen-horizontal road it renders as its viewport twin does — i.e. it took
        /// <c>SymbolWorldMapPitchClip</c>'s camera-facing METRE fallback, and did NOT reinterpret its metre
        /// offsets as pixels. Older fixtures and the null-<c>PathUpRender</c> bakers write a zero <c>Up</c>.
        ///
        /// <para>Non-obvious why: the viewport twin is a fair reference, because the fallback is the ground
        /// branch's own expression with the camera-plane normal <c>UNITY_MATRIX_V[2].xyz</c> in place of the
        /// surface normal. At tilt 0 the two normals are equal, and at road angle 0° the viewport tangent
        /// rotation is the identity, so all three frames coincide. Unity's basis gives
        /// <c>cross(V[2], V[0]) == −V[1]</c>, not <c>V[1]</c>; <c>Shaders/Map/Symbol/SymbolWorldPitchAlign.hlsl</c>
        /// is authoritative on this.</para>
        /// </summary>
        [Test]
        public void MapPitchedWithDegenerateUp_AtTiltZero_TakesTheMetreFallback_InkCount()
        {
            Measure(devicePixelRatio: 1.0, roadAngleDeg: 0f, degenerateUp: true,
                out InkReading map, out InkReading viewport);

            double ratio = (double)map.Count / viewport.Count;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "degenerate Up  map(zero Up) ink={0}  viewport ink={1}  ratio={2:F5}", map.Count, viewport.Count, ratio));

            Assert.That(ratio, Is.EqualTo(1.0).Within(InkCountTolerance),
                $"degenerate Up: a map-pitched glyph whose Up is float3.zero must fall back to the CAMERA-FACING " +
                $"METRE frame, which at tilt 0 / road 0° is the same frame the viewport twin renders in — " +
                $"map {map.Count} px, viewport {viewport.Count} px, ratio {ratio:F5}. A ratio far from 1 " +
                "means the fallback handed a METRE magnitude to the pixel formula (a ~mpp× quad); a near-zero " +
                "count means there is no fallback at all and the quad collapsed onto its anchor.");
        }

        /// <summary>
        /// <b>Degenerate-frame branch, second clause (a separate <c>[Test]</c> — NUnit throws on the first failure, so a
        /// multi-clause method never executes its discriminator).</b>
        /// The same degenerate-<c>Up</c> render, read by ink CENTROID: catches a fallback that is the right
        /// SIZE but mirrored or displaced. See the count clause above for the full rationale.
        /// </summary>
        [Test]
        public void MapPitchedWithDegenerateUp_AtTiltZero_TakesTheMetreFallback_InkCentroid()
        {
            Measure(devicePixelRatio: 1.0, roadAngleDeg: 0f, degenerateUp: true,
                out InkReading map, out InkReading viewport);

            double dRow = map.CentroidRow - viewport.CentroidRow;
            double dCol = map.CentroidCol - viewport.CentroidCol;
            double delta = math.sqrt(dRow * dRow + dCol * dCol);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "degenerate Up  map(zero Up) centroid=({0:F2}, {1:F2})  viewport=({2:F2}, {3:F2})  delta={4:F3} px",
                map.CentroidRow, map.CentroidCol, viewport.CentroidRow, viewport.CentroidCol, delta));

            Assert.That(delta, Is.LessThan(InkCentroidTolerancePx),
                $"degenerate Up (centroid): the degenerate-Up fallback renders {delta:F3} px from the viewport twin " +
                "— at tilt 0 with a screen-horizontal road the camera-facing metre frame and the viewport " +
                "frame coincide, so this must be sub-pixel.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Harness
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>One arm's rendered ink signature.</summary>
        private readonly struct InkReading
        {
            public readonly int   Count;
            public readonly float CentroidRow;
            public readonly float CentroidCol;

            public InkReading(int count, float centroidRow, float centroidCol)
            {
                Count       = count;
                CentroidRow = centroidRow;
                CentroidCol = centroidCol;
            }
        }

        /// <summary>
        /// Renders the SAME curved symbol twice through ONE scene, camera and
        /// <see cref="SymbolPlacementSystem"/> — once with <c>PitchAlignment = Map</c>, once with
        /// <see cref="AlignmentMode.Auto"/>, which resolves to the screen path — and returns both ink
        /// signatures. Each Tick is duplicated because the collision verdict is harvested one Tick late.
        /// </summary>
        private static void Measure(double devicePixelRatio, float roadAngleDeg, bool degenerateUp,
            out InkReading map, out InkReading viewport)
        {
            var sceneConfig = new TiltedGroundSceneConfig
            {
                TiltDegrees      = 0.0,   // THE pose — see this file's header.
                SizePx           = SizePx,
                DevicePixelRatio = devicePixelRatio,
                BackgroundColor  = Color.white, // WorldSymbolInkAnalysis.InkThreshold reads dark ink on white.
                LitAmbient       = false,       // the symbol arm needs no lit recipe.
            };

            TiltedGroundScene    scene    = null;
            GlyphAtlasTexture    atlas    = null;
            SymbolPlacementSystem system   = null;
            TestSymbolPlan       plan     = null;
            SnapshotRenderer     snapshot = null;
            try
            {
                scene = TiltedGroundScene.Create(sceneConfig);
                SymbolQuad cell;
                (atlas, cell) = WorldCurvedAbRenderSnapshotTests.BuildGlyphF();

                SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
                double3 origin = frame.SceneOriginRender;
                double mpp = scene.MetresPerDevicePixel;

                // The road in the render-space XZ plane (east = X, north = Z). NO metre literals: every length is
                // a multiple of the frame ruler.
                double rad = math.radians(roadAngleDeg);
                var dir = new double3(math.cos(rad), 0.0, math.sin(rad));
                double halfLen = RoadHalfLengthRulerUnits * mpp;
                double3 pathA = origin - dir * halfLen;
                double3 pathB = origin + dir * halfLen;

                // Web-Mercator up is (0,1,0); the degenerate-Up arm feeds float3.zero to reach
                // SymbolWorldGroundFrame's guard.
                double3 up = degenerateUp ? double3.zero : new double3(0.0, 1.0, 0.0);

                long tileKey = TestTileKeys.PackedContaining(sceneConfig.LookAt.Surface, zoom: 14);

                system = new SymbolPlacementSystem(scene.MapCam,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                plan = new TestSymbolPlan(scene.MapCam.Projection);
                snapshot = new SnapshotRenderer(SizePx, SizePx);

                map      = RenderArm(scene, system, plan, snapshot, atlas, in frame,
                                     pathA, pathB, up, cell, tileKey, AlignmentMode.Map,      "map");
                viewport = RenderArm(scene, system, plan, snapshot, atlas, in frame,
                                     pathA, pathB, up, cell, tileKey, AlignmentMode.Viewport, "viewport");
            }
            finally
            {
                snapshot?.Dispose();
                plan?.Dispose();
                system?.Dispose();
                atlas?.Dispose();
                scene?.Dispose();
            }
        }

        private static InkReading RenderArm(
            TiltedGroundScene scene, SymbolPlacementSystem system, TestSymbolPlan plan,
            SnapshotRenderer snapshot, GlyphAtlasTexture atlas, in SceneFrame frame,
            double3 pathA, double3 pathB, double3 up, in SymbolQuad cell, long tileKey,
            AlignmentMode pitch, string armName)
        {
            var buffer = new SymbolTileBuffer();
            var glyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = cell } };
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, new[] { new LineAnchor(0, 0.5f) },
                new[] { pathA, pathB }, new[] { up, up },
                placement: SymbolPlacement.LineCenter,
                up: up,
                pitchAlignment: pitch,            // THE one field the two arms differ in.
                paint: SymbolPaint.Default,
                text: armName,
                textSizePx: TextSizePx,
                maxAngleDeg: 180f,
                keepUpright: false,
                // At coarse zoom the dedup/collision machinery decides who emits and a
                // fixture silently loses its symbol.
                allowOverlap: true,
                featureIndex: 0,
                tileKey: tileKey);

            // Duplicate Tick — the collision verdict is harvested one Tick late.
            system.Tick(in frame, plan.Build(buffer), atlas);
            system.Tick(in frame, plan.Build(buffer), atlas);
            Assert.That(system.LastQuadCount, Is.EqualTo(1),
                $"tilt-0 precondition ({armName}): the label must stage exactly one quad, got " +
                $"{system.LastQuadCount}. A zero means it spilled its road (StageCurved's centerArc ± halfSpan " +
                "gate) or was culled — nothing measured downstream would mean anything.");

            scene.Render(snapshot);
            var pixels = (Color32[])snapshot.Pixels.Pixels.Clone();
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, SizePx, SizePx);
            WorldSymbolInkAnalysis.AnalyzeInk(pixels, SizePx, SizePx,
                out _, out _, out _, out _,
                out float centroidRow, out float centroidCol, out int ink);

            Assert.That(ink, Is.GreaterThan(InkFloor),
                $"tilt-0 precondition ({armName}): the arm rendered {ink} ink px (floor {InkFloor}) — a blank " +
                "frame, a GPU-context failure or a collapsed quad. Two arms agreeing about nothing is not a " +
                "measurement.");

            return new InkReading(ink, centroidRow, centroidCol);
        }
    }
}
