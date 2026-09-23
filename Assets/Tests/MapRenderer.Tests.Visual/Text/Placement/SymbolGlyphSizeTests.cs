// Pitched-camera glyph-size and device-pixel-ratio GPU/visual acceptance tests.
//
// Split by the bare-`Object` using collision (System.Object vs UnityEngine.Object,
// CS0104) within the pitched-camera sub-area, then by content: fixture-harness
// self-validation, glyph-size measurement, and collision-box geometry.
// DevicePixelRatioSnapshotTests imports System; the other two are neutral —
// no bare-Object user is in this file.
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
    // THE SIZE TEETH (T1, T2, T3, T10), on the off-look-at multi-depth fixture.
    //
    // THE CLAIM, in one line: under `*-pitch-alignment: map` a glyph's DRAWN SIZE is a world metre quantity
    // carried by the SAME `arcScale` that already spaces the glyph anchors, so size and spacing foreshorten
    // together and their RATIO is depth-independent.
    //
    // THE ALGEBRA THESE TEETH READ. `SymbolStagingMath.StageCurved` spaces glyph g at world arc
    // `centerArc + (ArcCenter[g] − centre) · arcScale`, so the world gap between consecutive anchors is
    // `ΔArcCenter · arcScale`. The drawn corner offset is `cornerBaked · arcScale` too. Therefore
    //
    //      gap_world / cellWidth_world = (ΔArcCenter · arcScale) / (cellWidthBaked · arcScale)
    //                                  =  ΔArcCenter / cellWidthBaked                              … (5)
    //
    // `arcScale` CANCELS. The right-hand side is a quotient of two BAKED layout constants — independent of
    // TextSizePx, of MetresPerLogicalPixel, of DPR, of zoom, of tilt and of depth. Both quantities are world
    // lengths lying in the same ground plane at (locally) the same view depth, so both project through the same
    // perspective divide, and
    //
    //      gap_screen / glyphSize_screen = ΔArcCenter / cellWidthBaked = CONSTANT AT EVERY DEPTH … (6)
    //
    // EVERY TOOTH HERE IS BLIND TO A UNIFORM SCALE ERROR, AND SAYS SO. That blindness is the same cancellation
    // that makes them depth-independent; it is not a weakness that can be fixed here. The ABSOLUTE scale (and the
    // DPR factor, and the ŷ sign) are pinned by `MapPitchedGlyphSizeTiltZeroTests` against the VIEWPORT arm,
    // which shares no code with the map branch.
    //
    // WHY THE RECEDING ARM CARRIES THE CLAIM. `CrossNear`/`CrossFar` are ISO-DEPTH inherently, and for
    // an iso-depth symbol a true per-glyph world size and a size scaled by ONE constant per symbol are IDENTICAL —
    // that second thing is the reverted screen-ruler model. So a cross-arm reading cannot
    // discriminate the model, and every reading here that claims to is on, or spans, the RECEDING arm. Cross-arm
    // readings appear only as controls and are labelled as such.
    //
    // THREE ARMS, THREE REACHES — stated up front so no tooth over-claims:
    //   • rendered ink — SEPARABILITY (T1a, T2) reaches both receding depths, a factor of ≈ 2 apart, and the
    //                    cross arm at 1.25× and 1.5×. The RATIO read from ink (T1b) reaches only the band where
    //                    a glyph's ink run clears ≈ 4 px: MEASURED, RecedingFar's runs are 1–2 px at the shipped
    //                    pose, so ±1 px quantisation there is not just noise but an upward BIAS. Raising SizePx
    //                    does not help (the fixture is scale-invariant in it — see InkRunPose) and raising
    //                    TextSizePx enough drives the receding road's near end behind the camera.
    //   • world metres at the mesh (T3, T10) — 8×, the deep pose, and the arm that carries the RATIO claim to
    //                    depth. The fixture DISABLES the production far-distance cull (SymbolMaxDistanceFraction =
    //                    +inf in OffLookAtSymbolScene) so the deep pose's far anchors — which sit beyond the camera
    //                    far distance — survive to be measured; the cull is not this fixture's subject.
    //   • pure staging, no camera (T3b, in MapPitchedWorldArcStagingTests) — every magnitude regime to 50×.
    //
    // AND NOTE WHERE THE FAILURE BEGINS: 1.25× the look-at depth, not the horizon (the horizon is merely where
    // the symptom is loudest). T2 brackets 1.25× for exactly that reason.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapPitchedGlyphSizeTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapPitchedGlyphSizeTests
    {
        /// <summary>The shipped pose — tilt 55, depth ratio 2, DPR 1, 512 px.</summary>
        private static OffLookAtSymbolSceneConfig ShippedPose() => new OffLookAtSymbolSceneConfig();

        /// <summary>
        /// T1/T2's pose — the SHIPPED one.
        ///
        /// <para><b>MEASURED FINDING: raising <see cref="OffLookAtSymbolSceneConfig.SizePx"/> does NOT magnify
        /// this fixture, so it is not a remedy for a sub-pixel ink reading.</b> It was tried at 1024 and the
        /// projected geometry did not change: the altitude framing uses the LOGICAL viewport, so doubling
        /// SizePx doubles the orbit radius <c>d</c> AND the viewport height <c>H</c>, leaving
        /// <c>MetresPerDevicePixel = 2·d·tan(fov/2)/H</c> — and therefore every world length in the fixture,
        /// which is a multiple of it — unchanged, while every symbol sits at twice the view depth. The two
        /// cancel exactly. Measured at 1024: <c>RecedingNear</c>'s per-glyph ink advance read 21–25 px, the
        /// same band it reads at 512. This is the same scale-invariance the horizon measurement found for
        /// ZOOM, for the same reason, and it rules out magnifying the fixture as a remedy. Do not spend a
        /// round rediscovering it.</para>
        /// </summary>
        private static OffLookAtSymbolSceneConfig InkRunPose() => new OffLookAtSymbolSceneConfig();

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // T1 — THE HEADLINE
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>T1a — THE RENDERED HEADLINE, on the DEPTH-SPANNING arm.</b> Proves, from rendered ink alone:
        /// both receding symbols segment into exactly <c>GlyphCount</c> separable ink runs, at two depths a
        /// factor of ≈ 2 apart — <b>where the shipped code merges them already AT the look-at.</b>
        ///
        /// <para><b>Why this is a real discriminator and not a formality.</b> On a receding road the world
        /// advance foreshortens hard: measured, the anchor gap is 29.07 px at the look-at against a 32 px
        /// cell, i.e. <c>r = 0.91</c> where separability needs <c>r &gt; 1</c>. So under the earlier
        /// screen-constant size the RECEDING arm is ALREADY overlapping at zero extra depth, and it only gets
        /// worse with distance. Under the world-metre model the cell foreshortens with the advance, so (6) fixes the ratio at
        /// <c>AdvanceBakedPx / cellWidthBaked</c> at EVERY depth and all five letters stay apart. This is the
        /// maintainer's acceptance criterion in its literal form — "when you move away it gets harder to read,
        /// but it won't make the letters move closer to each other" — read off the arm where moving away
        /// actually happens.</para>
        ///
        /// <para><b>It pins the PLANE, not just the unit.</b> <c>r</c> can only stay above 1 as the symbol
        /// recedes if the glyph CELL foreshortens by the same factor the advance does, which requires x̂ to be
        /// the road tangent lying IN the ground plane. A camera-facing or screen-aligned cell would keep its
        /// width while the advance shrank, and the letters would merge exactly as they do today.</para>
        ///
        /// <para><b>Relationship to M10, which refuses to read spacing from ink.</b> M10's doc warns that an
        /// ink-WIDTH reading conflates CPU spacing with shader size. This tooth does not read a width
        /// against an expectation; it reads
        /// whether consecutive runs are DISJOINT, which is a property of the two together and is exactly the
        /// user-visible claim. No tension.</para>
        ///
        /// <para>RED recipe: I1 (mechanism off) — the receding runs merge and the count drops below
        /// <c>GlyphCount</c>. I4 (a per-LABEL depth ruler, the reverted shape) leaves the near symbol
        /// separable and merges the far one.</para>
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
                    "W2-T1a  {0}  runs={1}/{2}  min inter-run gap={3} px  anchor depth={4:F0} m",
                    id, runs.Length, f.Config.GlyphCount, runs.Length > 1 ? minGap : -1,
                    f.Measure(id).AnchorViewDepthMetres));

                Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                    $"W2-T1a ({id}): a map-pitched label on a RECEDING road must keep all " +
                    $"{f.Config.GlyphCount} letters separable — got {runs.Length} ink runs. The shipped " +
                    "pre-W2 code merges this arm already AT the look-at (29.07 px advance against a 32 px " +
                    "cell), so a merged reading here is the defect under test, not a tolerance " +
                    "question.");

                if (id == OffLookAtSymbolId.RecedingNear) nearDepth = f.Measure(id).AnchorViewDepthMetres;
                else                                     farDepth  = f.Measure(id).AnchorViewDepthMetres;
            }

            // The claim is worth nothing unless the two symbols really are at different depths.
            Assert.That(farDepth / nearDepth, Is.GreaterThanOrEqualTo(1.8),
                $"W2-T1a precondition: the two receding labels must sit at genuinely different depths — " +
                $"{nearDepth:F0} m and {farDepth:F0} m, ratio {farDepth / nearDepth:F3}. Separability at one " +
                "depth cannot distinguish a world-sized glyph from a screen-sized one.");
        }

        /// <summary>
        /// <b>T1b — the RATIO of (6), read from ink where the ink is resolvable.</b> Proves:
        /// <c>advance_screen(i) / inkExtent_screen(i)</c> is the SAME number across every glyph pair whose ink
        /// run is large enough to measure — i.e. it does not drift with depth.
        ///
        /// <para><b>HONEST REACH, and the measurement that fixes it.</b> The plan designed
        /// this tooth to POOL both receding symbols for a 2.37× depth span. That is not constructible: at the
        /// shipped pose <c>RecedingFar</c>'s per-glyph ink runs measure <b>1–2 px</b>, so ±1 px of edge
        /// quantisation is a ±50–100 % error AND a systematic upward bias on <c>r</c> (a 1.4 px extent reads
        /// as 1 or 2, never as 1.4). Measured, pooled: <c>RecedingNear</c> gave r = 2.333 / 2.333 / 2.400 /
        /// 2.500 while <c>RecedingFar</c> gave 5.0 / 3.0 / 3.0 / 6.0 — the far readings are quantisation, not
        /// signal. Raising <c>SizePx</c> does not help (see <see cref="InkRunPose"/>: the fixture is
        /// scale-invariant in it), and raising <c>TextSizePx</c> enough to fix it drives the receding road's
        /// near end behind the camera, which the fixture asserts against. So this tooth reads only the
        /// resolvable band, says so, and the depth reach of the RATIO claim is carried instead by
        /// T3(b) — which measures the identical quantity in WORLD METRES off the real mesh, at the shipped
        /// pose, at DPR 2, and at <b>8×</b> the look-at depth.</para>
        ///
        /// <para><b>Why the expected value is NOT <c>AdvanceBakedPx / cellWidthBaked</c>.</b> That constant
        /// (1.25) is the advance over the CELL width; an ink run measures the glyph's INK, which is narrower
        /// than its cell box by the side bearings and by the letterform itself. Measured, the 'F' reads
        /// ≈ 2.4 rather than 1.25 — a property of the glyph, not of the model. This tooth therefore asserts
        /// only that the ratio is CONSTANT ACROSS DEPTH, which is the part (6) actually claims about a
        /// rendered ink reading; the absolute constant is asserted where it is exact, against the CELL, by
        /// T3(b). Asserting 1.25 here would be a tooth that pins the letterform, not the renderer.</para>
        ///
        /// <para>RED recipe: I1 — earlier the extent is constant while the advance foreshortens, so <c>r</c>
        /// moves with depth across the resolvable band.</para>
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
                "W2-T1b  SizePx={0}  resolvable extent floor={1:F1} px", f.Config.SizePx, resolvableExtentPx));
            table.AppendLine("label          i  runStart  runEnd  extent  advance     r_i   viewDepth(m)  used");

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                GlyphMeasurement[] glyphs = f.Measure(id).Glyphs;
                OffLookAtInkRuns.AssertRunsVertically(f, id, glyphs);
                (int start, int end)[] runs = OffLookAtInkRuns.Receding(f, id);
                Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                    $"W2-T1b precondition ({id}): expected {f.Config.GlyphCount} ink runs, got {runs.Length}.");

                // The runs come out ordered by increasing screen ROW; the glyph array is ordered along the
                // ROAD. Pair them by matching that order — reading depths off the wrong end would invert the
                // whole table while leaving the ratios unchanged, which is exactly the kind of silent
                // mis-attribution a printed table is supposed to make impossible.
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
                $"W2-T1b precondition: at least 4 glyph pairs must have ink runs of {resolvableExtentPx:F1} px " +
                $"or more, got {ratios.Count}. Below that there is no rendered ratio to read at all and the " +
                "tooth would be vacuous — escalate rather than lowering the floor.");

            double minR = double.MaxValue, maxR = double.MinValue;
            foreach (double r in ratios) { minR = math.min(minR, r); maxR = math.max(maxR, r); }
            double minDepth = double.MaxValue, maxDepth = double.MinValue;
            foreach (double d in depths) { minDepth = math.min(minDepth, d); maxDepth = math.max(maxDepth, d); }
            double resolvedDepthRatio = maxDepth / minDepth;

            // THE BOUND, DERIVED from the achieved geometry — not rounded to a convenient number:
            //  (a) ±1 px on each measured edge ⇒ ±2/extent on the extent and ±2/advance on the advance, both
            //      of which enter r = advance/extent linearly;
            //  (b) a glyph's own footprint is a fraction 1/expected of one advance, so the view depth — and
            //      therefore the local scale — varies across it by about the adjacent-glyph depth step scaled
            //      by that fraction.
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
                $"W2-T1b: advance/inkExtent must not drift with depth — it spans [{minR:F4}, {maxR:F4}], " +
                $"spread {spreadR:P2} against a derived bound of {bound:P2}, over {ratios.Count} readings at " +
                $"depths {minDepth:F0}..{maxDepth:F0} m ({resolvedDepthRatio:F3}×). Pre-W2 the ink extent is " +
                "constant while the advance foreshortens, so this ratio moves with depth by construction. " +
                "Full table above; the depth reach of this claim is carried to 8× in world metres by " +
                "W2-T3(b), not here.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // T2 — the maintainer's literal complaint, BRACKETING the measured 1.25× threshold
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>T2 — "the letters must never overlap", at and past the depth where today's code first
        /// fails.</b> Proves: a map-pitched curved symbol segments into exactly <c>GlyphCount</c> ink runs,
        /// with a real gap between consecutive runs, at depth ratios <b>1.25</b> and <b>1.5</b> as well as at
        /// the shipped 2.0.
        ///
        /// <para><b>Where 1.25× comes from, and why bracketing it matters more than going deep.</b> The
        /// horizon measurement: the glyph cell is 32 screen px at <c>TextSizePx = 48</c> and the
        /// cross-arm anchor gap is 40 px at the look-at, so under the screen-constant size model the gap
        /// reaches the cell width at exactly <b>1.25× the look-at depth</b> (measured there: 32.0 px). That is
        /// the FIRST depth at which the shipped code fails — not the horizon. On a tilted map most of the
        /// frame is past 1.25×, which is why the maintainer sees the blob everywhere. A tooth that only
        /// asserted at extreme depth would leave that whole band unobserved.</para>
        ///
        /// <para><b>Why T2 does NOT go deep.</b> At ratio 8 the far symbol's entire 5-glyph screen extent is
        /// 0.773 px — under the world-metre model the glyphs shrink with it, so there is no ink to count under EITHER model.
        /// Chasing depth here would make the tooth vacuous, which is the opposite of what the headline asks.</para>
        ///
        /// <para><b>SCOPE CAVEAT — read this before citing T2 as evidence for the model.</b> These cells read
        /// the CROSS-AZIMUTH arm, which is ISO-DEPTH inherently and therefore CANNOT distinguish a
        /// per-glyph world size from a per-symbol constant. That is fine here, because T2's claim is "letters
        /// do not merge", not "the size is per-glyph" — and the 32.0 px threshold was measured on the cross
        /// arm and is exact only there. <b>T1 on the receding arm is the discriminator; T2 is the
        /// threshold observer.</b></para>
        ///
        /// <para><b>Non-vacuity is asserted per cell, not assumed.</b> The cell only means something if the
        /// earlier model WOULD have merged these letters, i.e. if the measured anchor advance has fallen to at
        /// most the cell's own screen width. Both numbers are computed from fixture constants and measured
        /// anchor positions, printed, and asserted.</para>
        ///
        /// <para>RED recipe: I1. Expected pre-fix reading — run count below <c>GlyphCount</c> on
        /// <c>CrossFar</c> at both cells.</para>
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
                "W2-T2  ratio={0:F2} (achieved {1:F4})  CrossFar: runs={2}/{3}  min inter-run gap={4} px  " +
                "min anchor advance={5:F2} px  cell screen width={6:F2} px",
                targetDepthRatio, f.AchievedDepthRatio, runs.Length, f.Config.GlyphCount,
                runs.Length > 1 ? minGap : -1, minAdvancePx, f.GlyphCellScreenWidthPx));

            // NON-VACUITY. Under the earlier screen-constant size the cell keeps its full width while the
            // anchor advance shrinks with depth, so the letters merge as soon as the advance falls to the
            // cell width. If that has not happened at this cell, the cell is not testing anything.
            Assert.That(minAdvancePx, Is.LessThanOrEqualTo(f.GlyphCellScreenWidthPx),
                $"W2-T2 non-vacuity (ratio {targetDepthRatio:F2}): CrossFar's smallest anchor advance is " +
                $"{minAdvancePx:F2} px against a {f.GlyphCellScreenWidthPx:F2} px cell. The pre-W2 model only " +
                "merges these letters once the advance has fallen to at most the cell width, so above that " +
                "this cell would pass under BOTH models and prove nothing.");

            Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                $"W2-T2 (ratio {targetDepthRatio:F2}, achieved {f.AchievedDepthRatio:F4}): a map-pitched " +
                $"label must still segment into {f.Config.GlyphCount} separable ink runs at " +
                $"{f.AchievedDepthRatio:F2}× the look-at depth — got {runs.Length} in row band " +
                $"[{rowFrom}, {rowTo}]. Equation (6) fixes the gap at AdvanceBakedPx/cellWidthBaked cell " +
                "widths at EVERY depth, so a merged label here means the size is not on the world ruler. " +
                "This is the maintainer's literal complaint: 'it won't make the letters move closer together'.");
        }

        /// <summary>
        /// <b>T2, the control BELOW the threshold.</b> The near (look-at) cross-azimuth symbol must
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
                "W2-T2 control  CrossNear: runs={0}/{1}  (separable under BOTH models — not a discriminator)",
                runs.Length, f.Config.GlyphCount));

            Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                $"W2-T2 control: the LOOK-AT label must segment into {f.Config.GlyphCount} runs, got " +
                $"{runs.Length}. This cell cannot discriminate the model — it fails only if the ink " +
                "segmentation itself is broken, which is exactly what it is here to rule out.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // T3 — the world identity through the REAL emit, at the fixture's ceiling
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>T3 leg (a) — the ABSOLUTE world cell size, and the DPR factor.</b> Proves: the corner offsets
        /// the renderer actually wrote into the slot mesh are WORLD METRES of exactly
        /// <c>cellWidthBaked · TextSizePx/OneEm · MetresPerLogicalPixel</c>.
        ///
        /// <para>Read from <c>WorldBillboardVertex.Offset</c> — production staging, production
        /// <c>BuildWorldQuad</c>, a real built mesh. The expectation is built from the fixture's OWN
        /// <c>MetresPerLogicalPixel = MetresPerDevicePixel × Config.DevicePixelRatio</c>, NOT
        /// from any production field that already carries the product: if both sides sourced the ratio from
        /// one place they would drop it together and the DPR leg would be vacuous.</para>
        ///
        /// <para><b>This is the leg that carries the absolute scale.</b> Leg (b) below divides it away. Run
        /// at three poses — the shipped one, the deep tilt-72/ratio-8 one, and DPR 2 — because an omitted DPR
        /// factor shows here as a clean ×2 and cancels in (b).</para>
        ///
        /// <para>RED recipe: multiply <c>cornerMetresPerLogicalPixel</c> by 2 in <c>StageCurved</c> ⇒ (a) RED,
        /// (b) GREEN, which also puts on the record which leg carries the absolute scale.</para>
        /// </summary>
        [Test]
        public void WorldCellSize_IsTheBakedCellOnTheWorldRuler(
            [Values("shipped", "deep", "dpr2")] string poseName)
        {
            using var f = OffLookAtSymbolScene.Create(PoseByName(poseName));

            double emScale = f.Config.TextSizePx / TextQuadLayout.OneEm;
            double expected = f.GlyphCellWidthBakedPx * emScale * f.MetresPerLogicalPixel;

            // EVERY glyph, not just glyph 0: the corner scale is a per-VERTEX quantity, so reading one glyph
            // would leave a per-glyph divergence on glyphs 1..N-1 invisible to this leg — and leg (b) could
            // not see it either, since it divides the scale away. Reading them all costs nothing.
            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                int glyphs = GlyphQuadCount(f, id);
                Assert.That(glyphs, Is.EqualTo(f.Config.GlyphCount),
                    $"W2-T3a precondition ({id}): expected {f.Config.GlyphCount} staged glyphs, got {glyphs}.");

                for (int g = 0; g < glyphs; g++)
                {
                    double measured = WorldCellWidth(f, id, g);
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "W2-T3a  pose={0}  {1}[{2}]  cellWidth_world={3:F4} m  expected={4:F4} m  " +
                        "(cellWidthBaked={5:F3} × emScale={6:F3} × mppLogical={7:F4})",
                        poseName, id, g, measured, expected, f.GlyphCellWidthBakedPx, emScale,
                        f.MetresPerLogicalPixel));

                    Assert.That(measured, Is.EqualTo(expected).Within(0.01).Percent,
                        $"W2-T3a ({poseName}, {id}, glyph {g}): the emitted corner offsets must be WORLD " +
                        $"METRES of cellWidthBaked · TextSizePx/OneEm · MetresPerLogicalPixel = " +
                        $"{expected:F4} m; the mesh carries {measured:F4} m. A clean ×2 or ÷2 here is a " +
                        "dropped or doubled DevicePixelRatio; a factor of ~MetresPerLogicalPixel is offsets " +
                        "still in pixels; a value that differs BETWEEN glyphs of one label is a per-glyph " +
                        "ruler, which is the reverted P3c shape.");
                }
            }
        }

        /// <summary>
        /// <b>T3 leg (b) — THE TOOTH THAT SAYS SIZE AND SPACING CAME FROM ONE CONSTANT.</b> Proves:
        /// <c>worldAdvance / cellWidth_world == AdvanceBakedPx / cellWidthBaked</c> — equation (5), with every
        /// scale cancelled — and that it reads the SAME number at the shipped pose, at DPR 2, and at the
        /// fixture's deepest constructible pose (tilt 72°, ratio 8, far anchor at ≈ 1 084 562 m).
        ///
        /// <para>This is the direct analogue of the horizon measurement's SPAN/ROAD reading: a quotient of two
        /// WORLD lengths, so it is immune to the legibility limit that stops T1 and T2 at ≈ 2.4×. It is also
        /// a real end-to-end reading — production staging, production <c>BuildWorldQuad</c>, a real mesh — at
        /// 8× depth, which no other tooth in the suite reaches.</para>
        ///
        /// <para><b>Reach, stated so it is not over-read:</b> 8× is the deep pose this fixture measures
        /// at. The production far-distance cull is DISABLED for this fixture (<c>SymbolMaxDistanceFraction = +inf</c>
        /// in <c>OffLookAtSymbolScene</c>) so the deep pose's far anchors — which sit beyond the camera far distance
        /// — survive to be measured; the cull is not this fixture's subject. The identity is carried further still, on the
        /// CPU with no camera, by T3(b) in <c>MapPitchedWorldArcStagingTests</c>.</para>
        ///
        /// <para>RED recipe: multiply <c>cornerMetresPerLogicalPixel</c> by 2 ⇒ this leg stays GREEN (it
        /// cancels) while (a) goes RED. I1/I8 (the corner unit never becomes metres) reds it hard.</para>
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
                    // either (N6).
                    double cellWidthWorld = WorldCellWidth(f, id, i);
                    double ratio = worldSpacing[i] / cellWidthWorld;
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "W2-T3(b)  pose={0}  {1} gap {2}  worldAdvance={3:F3} m  cellWidth_world={4:F3} m  " +
                        "ratio={5:F6}  expected={6:F6}",
                        poseName, id, i, worldSpacing[i], cellWidthWorld, ratio, expected));

                    Assert.That(ratio, Is.EqualTo(expected).Within(0.05).Percent,
                        $"W2-T3(b) ({poseName}, {id}, gap {i}): worldAdvance / cellWidth_world must be the " +
                        $"purely typographic AdvanceBakedPx/cellWidthBaked = {expected:F6}, measured " +
                        $"{ratio:F6}. Both sides are world lengths produced by the SAME arcScale, so every " +
                        "scale cancels — a different number means the drawn size and the anchor spacing did " +
                        "NOT come from one constant, which is the whole of W2.");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // T10 — the SPACING half, fenced at 8×
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>T10 (a) — SPAN/ROAD is depth-invariant at 8× the look-at depth.</b> Proves: each receding
        /// symbol's staged world span, as a fraction of its own road length, is the SAME at the deep
        /// tilt-72/ratio-8 pose as at the shipped tilt-55/ratio-2 one.
        ///
        /// <para><b>This is the spacing guarantee, and that is why it belongs beside the size teeth.</b> It is the regression
        /// fence that keeps the SPACING half honest beside the SIZE half, and it closes
        /// the "correct at 2×, wrong at 20×" gap that prompted the horizon measurement.
        /// It may read more naturally beside <c>MapPitchedWorldArcLayoutTests</c>;
        /// it lives here for now because it shares the tilt-72 pose with T3 and the fixture should pay for
        /// that construction once.</para>
        ///
        /// <para><b>A ratio of two WORLD lengths, so the drawn glyph size cannot touch it</b> — which is what
        /// makes it survivable at a depth where nothing is legible (at ratio 8 the far symbol's whole 5-glyph
        /// screen extent is 0.773 px). The reference is the ratio-2 cell measured IN THE SAME TEST RUN, never
        /// a hard-coded 0.3333333: the designed value is <c>1/(2·SpillMargin)</c>, so a literal would track a
        /// fixture constant instead of the invariant and would go RED for the wrong reason if
        /// <c>SpillMargin</c> ever moved.</para>
        ///
        /// <para><b>RED recipe — and note what it is NOT.</b> Injection I4 (a per-symbol depth ruler, the
        /// reverted screen-ruler shape) leaves this GREEN, because SPAN/ROAD is a spacing reading and I4 is a SIZE
        /// defect. That is the point: T10 fences the spacing half so a SIZE regression cannot be misread as
        /// a SPACING one. Its own RED comes from re-injecting the earlier screen walk (<c>worldArc = false</c>),
        /// which reds it hard.</para>
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
                    "W2-T10a  {0}  SPAN/ROAD: shipped(tilt {1:F0}, ratio {2:F2})={3:F8}  " +
                    "deep(tilt {4:F0}, ratio {5:F2})={6:F8}",
                    id, shipped.Config.TiltDegrees, shipped.AchievedDepthRatio, shippedRatio,
                    deep.Config.TiltDegrees, deep.AchievedDepthRatio, deepRatio));

                Assert.That(deepRatio, Is.EqualTo(shippedRatio).Within(0.01).Percent,
                    $"W2-T10a ({id}): the label must occupy the same fraction of its road at " +
                    $"{deep.AchievedDepthRatio:F2}× the look-at depth as at " +
                    $"{shipped.AchievedDepthRatio:F2}× — shipped {shippedRatio:F8}, deep {deepRatio:F8}. " +
                    "Both are ratios of two WORLD lengths, so the drawn glyph size cannot affect them; a " +
                    "difference means the arc WALK became depth-dependent, i.e. a W1 regression, not a W2 one.");
            }
        }

        /// <summary>
        /// <b>T10 (b) — the world residual at 8×.</b> Proves: at the deep pose every staged gap is
        /// <c>AdvanceWorldMetres</c> to within <b>0.01 %</b>.
        ///
        /// <para><b>Where the bound comes from, and what it must clear.</b> It is set ABOVE the fixture's own
        /// RTC artifact at this pose and below the real precision floor, and BOTH are named here so a future
        /// RED is diagnosed as a bookkeeping question before it is diagnosed as a size defect:
        /// <list type="bullet">
        /// <item><b>The artifact (not a defect).</b> <c>OffLookAtSymbolScene</c> derives all six symbols' tile
        /// keys from the LOOK-AT's z14 tile, so its far symbols sit ≈ 1e6 m from their nominal
        /// <c>TileOriginRender</c>, and the <c>float3 AnchorLocal</c> RTC bake at that distance is the ENTIRE
        /// source of the drift: 4.2e-5 % at ratio 2, 4.05e-4 % at ratio 5. In production a symbol's tile origin
        /// is its OWN tile's, a few km away, so this never bites. 0.01 % is ≈ 25× the ratio-5 artifact.</item>
        /// <item><b>The floor.</b> <c>PolylineArcMath.BuildCumulativeWorld</c> accumulates in <c>double</c> and
        /// narrows per entry, so the real floor is the <c>float</c> storage of the cumulative — it needs a
        /// ≈ 2–3 × 10⁹ m road (≈ 50 Earth circumferences) to breach 1 %. 0.01 % is four orders below it.</item>
        /// </list>
        /// A bound tightened below the artifact goes RED on fixture bookkeeping and gets misdiagnosed; one
        /// widened far past it stops discriminating.</para>
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
                        "W2-T10b  {0} gap {1}  world={2:F4} m  expected={3:F4} m  residual={4:F6} %",
                        id, i, gaps[i], expected, residualPct));

                    Assert.That(gaps[i], Is.EqualTo(expected).Within(0.01).Percent,
                        $"W2-T10b ({id}, gap {i}): the staged world gap is {gaps[i]:F4} m against " +
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

        /// <summary>Tilt 72°, ratio 8 — the deep cell this fixture measures at. The production far-distance cull,
        /// which would otherwise drop the far anchors at this depth, is DISABLED for this fixture
        /// (<c>SymbolMaxDistanceFraction = +inf</c> in <c>OffLookAtSymbolScene</c>) so they survive to be measured.
        /// Tilt, not zoom, is the knob: the fixture is scale-invariant in zoom (every world length in it is a
        /// multiple of <c>MetresPerDevicePixel</c>, which zoom rescales along with the reference depth), while
        /// tilt is what brings the horizon into frame.</summary>
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
                $"W2-T3 precondition ({id}): the slot mesh carries {v.Length} vertices, too few for glyph " +
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
    // The RENDERED teeth for the device-pixel-ratio convention. Every quantity below is
    // measured at dpr 1 and dpr 2 and asserted as a RATIO, never as "it changed": a "changed" assertion is
    // exactly what lets a dpr² error through, and ratios also absorb the constant AA-straddle offset.
    //
    // FIXTURE SHAPE IS LOAD-BEARING. The swept variable is a real MapCamera's DevicePixelRatio and the camera
    // rendered IS that MapCamera's own UnityEngine.Camera (the SymbolLayerOrderSnapshotTests shape). The two
    // nearest precedents would both make these teeth vacuous: LineAaSnapshotTests builds its own orthographic
    // camera and never mentions dpr, so a line arm taken from it would only restate the material uniform;
    // MapViewSnapshotTests renders a separate SnapCam framed from scene bounds, which is dpr-blind by
    // construction, so its ground span would not move at all. Only the pure pixel helpers are shared, via
    // PixelCoverage.
    //
    // THE PHYSICAL FRAMEBUFFER MUST NOT MOVE WITH DPR — if it scaled with the ratio everything would cancel
    // and these tests would pass on the broken tree. Two objects hold a size here, and both are Size×Size at
    // every dpr: (1) the RenderTexture assigned to the MapCamera's camera, which is what MapCamera.ViewportPx
    // reads, and (2) SnapshotRenderer's own _rt, which is what _ScreenParams reads during the draw. This
    // fixture does NOT use MapViewTestExtensions.WithTestCamera, so that class's shared static
    // RenderTexture is not involved at all.
    //
    // What SHOULD move at dpr 2 is the camera: the altitude is framed from the LOGICAL viewport height
    // (MapCamera.SyncToCamera), so it halves, the visible ground halves, and every world-anchored quantity
    // doubles in device px. That is the control the line family is measured against.

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

        // text-color: white — a CONSTANT text-color binds _TextColor (style-transitions epic); leaving it
        // at the spec default (black) would multiply MeasureTextHeightPx's hand-injected TextInk vertex
        // colour (bypassing SymbolFeatureExtractor.EvaluatePaint) down to black.
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

            // The experiment's precondition, asserted rather than assumed: the PHYSICAL viewport must be
            // identical at every ratio. If it scaled with dpr, the altitude change and the framebuffer change
            // would cancel and every tooth below would pass on the broken tree.
            Assert.That(mapCam.ViewportPx.x, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport width must stay {Size} at dpr {devicePixelRatio}.");
            Assert.That(mapCam.ViewportPx.y, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport height must stay {Size} at dpr {devicePixelRatio}.");

            // Tilt 0 ⇒ the camera sits straight above the look-at, which camera-relative rendering places at
            // the world origin. Ground half-height = altitude·tan(fov/2), over Size/2 device pixels.
            double altitude = math.length(mapCam.CameraRelativePosition);
            double halfFov  = math.radians(mapCam.CurrentProperties.VerticalFovDeg) * 0.5;
            double metresPerDevicePx = 2.0 * altitude * math.tan(halfFov) / Size;

            // The frame constant the line shader sizes every px-valued width with. It is PROCESS state: a
            // fixture that never pushes it renders against whatever ruler an earlier fixture in the batch
            // left behind. The MapCamera ctor syncs and therefore pushes, so the constant arrives with the
            // scene — asserted here rather than trusted, because the failure is silent and reads as a
            // conversion bug three subsystems away.
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
        /// and that factor is exactly 2.
        ///
        /// <para>RED against the un-fixed tree by arithmetic, not by assertion: <c>_Width</c> is handed to
        /// the shader raw and <c>pxToWorld</c> is metres per DEVICE pixel, so <c>widthWorld·(d/mpp) = W</c>
        /// device px at every ratio — the road keeps its literal screen width (ratio 1.00) while the ground
        /// span doubles. That divergence IS the reported symptom.</para>
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
        /// scene's own metres per device pixel, and the pair halves exactly.
        ///
        /// <para>Named separately from the render arms because it is the tooth that would have made the
        /// investigation one step long. The symptom was a styled 16 px road rendering 128 px at dpr 1 and
        /// 161 px at dpr 2 — a ratio of 1.258 that looks like a broken conversion and sent the search to the
        /// projection subsystem. The cause was neither: this fixture never pushed the global, so the shader
        /// read <c>MetersPerPixel(5.0) = 2445.985</c> left behind by an earlier fixture while the camera stood
        /// at zoom 8 (<c>305.748113</c>). 2445.985 / 305.748113 = 8.000 = 2³, three whole zoom levels — and a
        /// 16 px band × 8 is exactly the 128 px measured. A globe-vs-Mercator mismatch at latitude 30 would
        /// have been 1.1547, and this fixture is Mercator anyway.</para>
        ///
        /// <para>No render, so it cannot go Inconclusive on a headless GPU.</para>
        /// </summary>
        [Test]
        public void FrameConstant_IsTheScenesOwnMetresPerDevicePixel_AtBothRatios()
        {
            int id = ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel;

            double pushedAt1, sceneAt1, pushedAt2, sceneAt2;
            // BuildScene's own precondition asserts the equality; these read the numbers back out so the
            // RATIO clause below has both halves at once, which is what names the 8.000 rather than a
            // per-ratio "it does not match".
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
        /// <b>T3 — the reported symptom, pinned.</b> A rendered line and a rendered symbol must scale by the
        /// SAME factor across the dpr sweep, and that factor must be 2.
        ///
        /// <para>Both are asserted against 2.0 AND against each other. "Both moved" would pass
        /// on a build that multiplied the LABEL side at the style seam as well — the symbol path already
        /// divides by the logical viewport, so a second multiply gives <c>textRatio == 4</c>, which the
        /// against-2.0 clause is the only thing that catches.</para>
        ///
        /// <para>Two arms over the SAME sweep with the SAME fixed framebuffer, rendered one at a time: the
        /// symbol is anchored at the look-at and the ribbon crosses it, so a combined frame would put the two
        /// measurements on top of each other. Both render through the swept MapCamera's own camera, which is
        /// the part that matters.</para>
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
    // THE TILT-ZERO CALIBRATION ARM (T4, T5, T9).
    //
    // WHY TILT 0 IS THE ONLY POSE THAT CAN SAY THIS. At tilt 0 the ground plane is perpendicular to the view
    // axis, so every ground point shares ONE view depth d, and `MetresPerLogicalPixel` is BY DEFINITION the
    // metres-per-logical-pixel ruler at d. A map-pitched corner displaced by `cornerPx · mppLogical` METRES
    // therefore projects to exactly `cornerPx` LOGICAL PIXELS — which is what the viewport branch adds
    // to clip.xy after projection. So at tilt 0 a map-pitched curved symbol and a viewport-pitched twin must
    // render PIXEL-IDENTICALLY (up to AA).
    //
    // That single identity carries three things at once, and each is a separate tooth below:
    //   • the ABSOLUTE SCALE (T4a) — every ratio tooth in MapPitchedGlyphSizeTests is blind to a uniform scale
    //     error `k`, because `k` cancels in a quotient of two lengths. Only an absolute comparison can see it,
    //     and this is the only pose where an absolute comparison has a reference that shares no code with the
    //     arm under test.
    //   • the DPR factor (T4c) — an omitted or duplicated `DevicePixelRatio` shows up here as a ×2 and nowhere
    //     else.
    //   • the SIGN of the ground frame's ŷ (T4b/T4d = T5) — SYMBOL_WORLD_MAP_Y_SIGN is the one constant
    //     here that is not derived: it is MEASURED, by gating both values on one tree.
    //
    // THE REFERENCE IS THE VIEWPORT ARM, AND THAT IS NOT AN ACCIDENT. It shares NO code with the map branch:
    // `SymbolWorldIsMapPitched` sends the two down mutually exclusive paths in the vertex stage. A rebuilt reference
    // is the recorded lesson — a reference drawn from the arm under test cancels the very defect it is meant to
    // expose. The two symbols here differ in EXACTLY ONE FIELD, `ShapedSymbol.PitchAlignment`.
    //
    // WHY 45° AND 90° ARE SWEPT AND 0° ALONE WOULD BE VACUOUS FOR THE SIGN. A road at 0° is horizontal on
    // screen, and the 'F' cell's displacement about its anchor is then symmetric under the mirror the sign
    // controls — a FALSE AGREEMENT is measurable at 0° and 22.56 px of disagreement at 45°/90° against a 1.0 px
    // bound. A sign constant must be read where the code is not inert.
    //
    // WHY THE CENTROID IS LEGITIMATE HERE, AND ONLY HERE. At tilt 0 the projection RESTRICTED TO THE GROUND
    // PLANE is affine, so it commutes with the centroid and an ink centroid is a faithful position reading. It
    // is NOT under tilt: a 12.41 px convexity gap appears the moment tilt is non-zero. No tooth outside
    // this file takes a centroid.
    //
    // NO METRE LITERALS: every world length here is a multiple of `scene.MetresPerDevicePixel`, the same rule
    // TiltFixtureSelfTests and OffLookAtSymbolScene enforce — a bare metre literal is sub-pixel at this pose.

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

        /// <summary>Ink-count agreement bound, as a fraction. A uniform scale error `k` reads here as `k²`
        /// (the glyph is a 2D patch), so 2 % brackets a 1 % scale error — and the failure modes this tooth
        /// exists for are not marginal: reinterpreting metres as pixels is a ~mpp× quad, an omitted DPR
        /// factor is ×2, i.e. ×4 in count. The slack absorbs only the sub-pixel disagreement between two
        /// arithmetically different routes to the same clip position (world displace → MVP, versus
        /// MVP → clip add), which lands on the glyph's AA boundary and nowhere else.</summary>
        private const double InkCountTolerance = 0.02;

        /// <summary>Ink-centroid agreement bound, in pixels. Same reasoning as above; the analogue reads
        /// 22.56 px for a flipped sign against exactly this bound, so the discrimination margin is ~22×.</summary>
        private const double InkCentroidTolerancePx = 1.0;

        /// <summary>An ink reading below this is not a rendered glyph — a blank frame, a GPU-context failure
        /// or a quad collapsed to a point would otherwise pass an agreement test by agreeing about nothing.
        /// Asserted on BOTH arms of every cell.</summary>
        private const int InkFloor = 500;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // T4a / T4c / T4d — ink COUNT: the ONLY clause that can see a uniform scale error
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>T4a (DPR 1) + T4c (DPR 2) + T4d (45°/90°) — the absolute-scale clause.</b> Proves: at
        /// tilt 0 a map-pitched curved symbol covers the same number of ink pixels as its viewport-pitched
        /// twin, at both device-pixel ratios and at three road angles.
        ///
        /// <para><b>This is the only size tooth that is not blind to a uniform scale error.</b>
        /// <c>MapPitchedGlyphSizeTests</c>' T1/T3(b)/T10 all read a QUOTIENT of two lengths, in which
        /// <c>arcScale</c>, <c>TextSizePx</c>, <c>MetresPerLogicalPixel</c>, DPR and <c>OneEm</c> cancel
        /// identically — that cancellation is what makes them depth-independent, and it is also what makes
        /// them unable to see a factor `k` applied to everything. The count reads such a `k` as `k²`.</para>
        ///
        /// <para><b>The DPR cell (T4c) is where an omitted DevicePixelRatio shows.</b> The scene's world
        /// geometry is DPR-invariant (the orbit radius and <c>MetresPerDevicePixel</c> halve together, so
        /// <c>MetresPerLogicalPixel</c> is the same number of metres at both ratios), and both arms then land
        /// on twice as many DEVICE pixels at DPR 2. A production ruler that dropped the ratio — i.e. used
        /// metres-per-DEVICE-pixel — would halve the map arm's world size at DPR 2 while the viewport arm,
        /// which divides by <c>_ScreenParamsLogical</c>, would not move. Injection 3 is exactly this
        /// shape.</para>
        ///
        /// <para><b>Blind to a mirror, inherently — predict that, do not discover it.</b> A mirror is an
        /// ISOMETRY, so it preserves ink count exactly. Injection I2 (flip
        /// <c>SYMBOL_WORLD_MAP_Y_SIGN</c>) leaves this clause at ratio 1.0000 and reds only the centroid
        /// clause below. That is why the two are separate <c>[Test]</c>s and not two asserts in one method.</para>
        ///
        /// <para>RED recipe: I1 (<c>SymbolWorldIsMapPitched</c> → <c>return false</c>) is catastrophic here —
        /// metres reinterpreted as logical px draws a quad ~<c>mpp</c>× oversized, so the map arm's ink is
        /// either the whole frame or nothing.</para>
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
                "W2-T4a  DPR={0:F1} angle={1:F0}deg  map ink={2}  viewport ink={3}  ratio={4:F5}",
                devicePixelRatio, roadAngleDeg, map.Count, viewport.Count, ratio));

            Assert.That(ratio, Is.EqualTo(1.0).Within(InkCountTolerance),
                $"W2-T4a (DPR {devicePixelRatio}, road {roadAngleDeg}°): at tilt 0 a map-pitched glyph must " +
                $"cover the same ink as its viewport twin — map {map.Count} px, viewport {viewport.Count} px, " +
                $"ratio {ratio:F5}. A uniform scale error k reads here as k²; the ratio teeth in " +
                "MapPitchedGlyphSizeTests cannot see one at all.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // T4b / T4c / T4d = T5 — ink CENTROID: the mirror/translation clause, and the SIGN
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>T4b (DPR 1) + T4c (DPR 2) + T4d = T5 (45°/90°) — the mirror/translation clause.</b>
        /// Proves: at tilt 0 the map-pitched glyph's ink CENTROID sits within
        /// <see cref="InkCentroidTolerancePx"/> of its viewport twin's, at both ratios and all three angles.
        ///
        /// <para><b>T5 lives here.</b> <c>SYMBOL_WORLD_MAP_Y_SIGN</c> is the one constant here that is
        /// not derivable on paper: <c>WorldBillboardVertex.Offset</c> arrives in a y-DOWN frame (the
        /// negation in <c>BillboardMath.BuildWorldQuad</c>), and that class's own doc states outright that the
        /// convention must not be re-derived on paper because the previous paper reading was
        /// self-contradictory. So which of <c>±cross(upWS, x̂)</c> is "downward on screen" was MEASURED — both
        /// values gated on one tree, and the one matching the
        /// viewport arm kept.</para>
        ///
        /// <para><b>The 45° and 90° cells are the discriminating ones; 0° cannot see a mirror across the road
        /// axis.</b> At 0° the road is screen-horizontal and the flip the sign controls maps the cell onto a
        /// near-symmetric image of itself. A false agreement reads at 0° and 22.56 px at 45°/90° against
        /// this same 1.0 px bound — a sign constant must be read where the code is not inert. 0° is retained as
        /// the control, labelled as such — it is what says the pair agrees at all before the sign is asked
        /// about.</para>
        ///
        /// <para><b>Why a centroid is sound at this pose and at no other.</b> Restricted to the ground plane
        /// at tilt 0 the projection is AFFINE, so it commutes with the centroid. Under tilt it does not:
        /// A 12.41 px convexity gap appears under tilt. Nothing else here reads a centroid.</para>
        ///
        /// <para>RED recipe: I2 (flip the sign) — RED here, GREEN on the count clause above. Also RED under
        /// I1, catastrophically.</para>
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
                "W2-T4b  DPR={0:F1} angle={1:F0}deg  map=({2:F2}, {3:F2})  viewport=({4:F2}, {5:F2})  " +
                "delta={6:F3} px  (row, col)=({7:F3}, {8:F3})",
                devicePixelRatio, roadAngleDeg, map.CentroidRow, map.CentroidCol,
                viewport.CentroidRow, viewport.CentroidCol, delta, dRow, dCol));

            Assert.That(delta, Is.LessThan(InkCentroidTolerancePx),
                $"W2-T4b/T5 (DPR {devicePixelRatio}, road {roadAngleDeg}°): the map-pitched glyph's ink " +
                $"centroid is {delta:F3} px from its viewport twin's — map " +
                $"({map.CentroidRow:F2}, {map.CentroidCol:F2}), viewport " +
                $"({viewport.CentroidRow:F2}, {viewport.CentroidCol:F2}). A flipped " +
                "SYMBOL_WORLD_MAP_Y_SIGN moves this by tens of pixels at 45°/90° while leaving the ink COUNT " +
                "at exactly 1.0000 (a mirror is an isometry). If this is RED at 45°/90° but green at 0°, the " +
                "sign is wrong — 0° cannot discriminate it.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // T9 — the degenerate frame degrades in METRES, not into a pixel formula
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>T9 — the degenerate-frame branch is observed, not merely written.</b> Proves: a map-pitched
        /// curved symbol whose per-vertex <c>Up</c> is <see cref="float3.zero"/> still renders, and at tilt 0
        /// with a screen-horizontal road it renders as its viewport twin does — i.e. it took
        /// <c>SymbolWorldMapPitchClip</c>'s camera-facing METRE fallback, and did NOT reinterpret its metre
        /// offsets as pixels.
        ///
        /// <para><b>The hazard this exists for.</b> Roughly ten older fixtures write <c>float3.zero</c> for
        /// <c>Up</c>, and both <c>SymbolTileBlockBaker</c> and the parity oracle do so whenever
        /// <c>PathUpRender</c> is null. Those feed a zero-length normal straight into a tangent-frame
        /// construction. Without this tooth the fallback is an unobserved branch — the shape that let a
        /// <c>normalize(0)</c> NaN stop round caps rendering at all, undetected.</para>
        ///
        /// <para><b>Why comparing against the VIEWPORT twin is legitimate here, rather than merely checking a
        /// magnitude.</b> The fallback is not a second, separately-invented convention: it is the GROUND
        /// branch's own expression, <c>SIGN · _ProjectionParams.x · cross(up, x̂)</c>, with the CAMERA-PLANE
        /// normal <c>UNITY_MATRIX_V[2].xyz</c> substituted for the surface normal. At tilt 0 the camera looks
        /// straight down, so the surface normal IS <c>V[2].xyz</c> — the two expressions are then literally the
        /// same number, and <b>no handedness claim is involved</b>. At a road angle of 0° the viewport arm's own
        /// tangent rotation is additionally the identity (the projected road direction is screen +x), so all
        /// three frames coincide and the arms must render alike. The road angle is FIXED at 0° here for that
        /// reason, not for convenience.</para>
        ///
        /// <para><b>⚠ Do NOT restate this as "<c>cross(V[2], V[0]) == V[1]</c>, so the fallback is
        /// camera-up".</b> That identity is FALSE — Unity's world basis is left-handed under the standard cross
        /// product, so <c>cross(V[2], V[0]) == −V[1]</c> — and believing it is the one real shader defect this
        /// stage produced and then caught by measurement (it gave the fallback the opposite screen sense to the
        /// ground frame, so exactly one of this tooth and T4b could ever be green, whichever sign was
        /// chosen). <b><c>Shaders/Map/Symbol/SymbolWorldPitchAlign.hlsl</c> is authoritative</b> on this; its
        /// ⚠ DO-NOT block in <c>SymbolWorldMapPitchClip</c> carries the derivation and the measurement. Keep the
        /// two sites in agreement.</para>
        ///
        /// <para><b>What each failure mode looks like.</b> Fallback missing entirely (x̂ = ŷ = 0) ⇒ the quad
        /// collapses to a point and the ink floor fails. Fallback routed to the viewport pixel formula ⇒ a
        /// metre magnitude in a pixel expression, a quad ~<c>mpp</c>× oversized. Both are caught by the count
        /// clause; the centroid clause additionally catches a mirrored fallback.</para>
        /// </summary>
        [Test]
        public void MapPitchedWithDegenerateUp_AtTiltZero_TakesTheMetreFallback_InkCount()
        {
            Measure(devicePixelRatio: 1.0, roadAngleDeg: 0f, degenerateUp: true,
                out InkReading map, out InkReading viewport);

            double ratio = (double)map.Count / viewport.Count;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W2-T9  map(zero Up) ink={0}  viewport ink={1}  ratio={2:F5}", map.Count, viewport.Count, ratio));

            Assert.That(ratio, Is.EqualTo(1.0).Within(InkCountTolerance),
                $"W2-T9: a map-pitched glyph whose Up is float3.zero must fall back to the CAMERA-FACING " +
                $"METRE frame, which at tilt 0 / road 0° is the same frame the viewport twin renders in — " +
                $"map {map.Count} px, viewport {viewport.Count} px, ratio {ratio:F5}. A ratio far from 1 " +
                "means the fallback handed a METRE magnitude to the pixel formula (a ~mpp× quad); a near-zero " +
                "count means there is no fallback at all and the quad collapsed onto its anchor.");
        }

        /// <summary>
        /// <b>T9, second clause (a separate <c>[Test]</c> — NUnit throws on the first failure, so a
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
                "W2-T9  map(zero Up) centroid=({0:F2}, {1:F2})  viewport=({2:F2}, {3:F2})  delta={4:F3} px",
                map.CentroidRow, map.CentroidCol, viewport.CentroidRow, viewport.CentroidCol, delta));

            Assert.That(delta, Is.LessThan(InkCentroidTolerancePx),
                $"W2-T9 (centroid): the degenerate-Up fallback renders {delta:F3} px from the viewport twin " +
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
        /// Renders the SAME curved symbol twice through ONE scene and ONE camera — once with
        /// <c>PitchAlignment = Map</c>, once with the field left at the enum's zero value
        /// (<see cref="AlignmentMode.Auto"/>, which resolves to the earlier screen path) — and returns both
        /// ink signatures.
        ///
        /// <para><b>The pair differs in EXACTLY ONE FIELD.</b> Same tile key, same feature index, same glyph
        /// cell, same <c>TextSizePx</c>, same road, same camera, same frame. Everything a scale error could
        /// hide behind is shared, so what survives the comparison is the branch itself.</para>
        ///
        /// <para>Both arms are rendered from one <see cref="SymbolPlacementSystem"/>, re-Ticked between them —
        /// the same two-pass discipline <c>OffLookAtSymbolScene</c> uses, and for the same reason: one camera
        /// means one projection, so the two frames are comparable inherently rather than by assumption.
        /// Each Tick is duplicated because the collision verdict is harvested one Tick late.</para>
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

                // The road, in the render-space XZ plane (east = X, north = Z; the flat local approximation is
                // exact enough at zero tilt and zero heading). NO metre literals — every length is a multiple
                // of the frame ruler.
                double rad = math.radians(roadAngleDeg);
                var dir = new double3(math.cos(rad), 0.0, math.sin(rad));
                double halfLen = RoadHalfLengthRulerUnits * mpp;
                double3 pathA = origin - dir * halfLen;
                double3 pathB = origin + dir * halfLen;

                // The per-vertex surface normal. This scene is Web-Mercator, so up IS (0,1,0) — except on
                // the T9 arm, which feeds the float3.zero that the older fixtures and the
                // null-PathUpRender bakers write, to reach SymbolWorldGroundFrame's guard.
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
                $"W2-T4 precondition ({armName}): the label must stage exactly one quad, got " +
                $"{system.LastQuadCount}. A zero means it spilled its road (StageCurved's centerArc ± halfSpan " +
                "gate) or was culled — nothing measured downstream would mean anything.");

            scene.Render(snapshot);
            var pixels = (Color32[])snapshot.Pixels.Pixels.Clone();
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, SizePx, SizePx);
            WorldSymbolInkAnalysis.AnalyzeInk(pixels, SizePx, SizePx,
                out _, out _, out _, out _,
                out float centroidRow, out float centroidCol, out int ink);

            Assert.That(ink, Is.GreaterThan(InkFloor),
                $"W2-T4 precondition ({armName}): the arm rendered {ink} ink px (floor {InkFloor}) — a blank " +
                "frame, a GPU-context failure or a collapsed quad. Two arms agreeing about nothing is not a " +
                "measurement.");

            return new InkReading(ink, centroidRow, centroidCol);
        }
    }
}
