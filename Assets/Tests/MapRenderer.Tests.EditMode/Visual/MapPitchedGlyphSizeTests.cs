// Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real
// SymbolPlacementSystem.Tick), off-screen GPU render + CPU readback. NOT registered in
// Tools/core-tests/core-tests.csproj.
//
// Stage W2 — THE SIZE TEETH (W2-T1, T2, T3, T10), on the off-look-at multi-depth fixture.
//
// WHAT W2 CLAIMS, in one line: under `*-pitch-alignment: map` a glyph's DRAWN SIZE is a world metre quantity
// carried by the SAME `arcScale` that already spaces the glyph anchors (W1), so size and spacing foreshorten
// together and their RATIO is depth-independent.
//
// THE ALGEBRA THESE TEETH READ. `SymbolStagingMath.StageCurved` spaces glyph g at world arc
// `centerArc + (ArcCenter[g] − centre) · arcScale`, so the world gap between consecutive anchors is
// `ΔArcCenter · arcScale`. W2 makes the drawn corner offset `cornerBaked · arcScale` as well. Therefore
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
// R2 — WHY THE RECEDING ARM CARRIES THE STAGE. `CrossNear`/`CrossFar` are ISO-DEPTH by construction, and for
// an iso-depth symbol a true per-glyph world size and a size scaled by ONE constant per symbol are IDENTICAL —
// that second thing is the reverted P3c model, which passed two review arms. So a cross-arm reading cannot
// discriminate the model, and every reading here that claims to is on, or spans, the RECEDING arm. Cross-arm
// readings appear only as controls and are labelled as such.
//
// THREE ARMS, THREE REACHES — stated up front so no tooth over-claims (followUps F-W2-5, F-W2-7):
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

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Text.Placement;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class MapPitchedGlyphSizeTests
    {
        /// <summary>The shipped pose — tilt 55, depth ratio 2, DPR 1, 512 px.</summary>
        private static OffLookAtSymbolSceneConfig ShippedPose() => new OffLookAtSymbolSceneConfig();

        /// <summary>
        /// W2-T1/T2's pose — the SHIPPED one.
        ///
        /// <para><b>MEASURED FINDING: raising <see cref="OffLookAtSymbolSceneConfig.SizePx"/> does NOT magnify
        /// this fixture, so it is not a remedy for a sub-pixel ink reading.</b> It was tried at 1024 and the
        /// projected geometry did not change: the altitude framing uses the LOGICAL viewport, so doubling
        /// SizePx doubles the orbit radius <c>d</c> AND the viewport height <c>H</c>, leaving
        /// <c>MetresPerDevicePixel = 2·d·tan(fov/2)/H</c> — and therefore every world length in the fixture,
        /// which is a multiple of it — unchanged, while every symbol sits at twice the view depth. The two
        /// cancel exactly. Measured at 1024: <c>RecedingNear</c>'s per-glyph ink advance read 21–25 px, the
        /// same band it reads at 512. This is the same scale-invariance the horizon measurement found for
        /// ZOOM (its §2.2), for the same reason, and it rules out the plan's escalation option (b). Recorded
        /// as followUp F-W2-7; do not spend a gate round rediscovering it.</para>
        /// </summary>
        private static OffLookAtSymbolSceneConfig InkRunPose() => new OffLookAtSymbolSceneConfig();

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T1 — THE HEADLINE (R1)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T1a — THE RENDERED HEADLINE, on the DEPTH-SPANNING arm.</b> Proves, from rendered ink alone:
        /// both receding symbols segment into exactly <c>GlyphCount</c> separable ink runs, at two depths a
        /// factor of ≈ 2 apart — <b>where the shipped code merges them already AT the look-at.</b>
        ///
        /// <para><b>Why this is a real discriminator and not a formality.</b> On a receding road the world
        /// advance foreshortens hard: measured, the anchor gap is 29.07 px at the look-at against a 32 px
        /// cell, i.e. <c>r = 0.91</c> where separability needs <c>r &gt; 1</c>. So under the pre-W2
        /// screen-constant size the RECEDING arm is ALREADY overlapping at zero extra depth, and it only gets
        /// worse with distance. Under W2 the cell foreshortens with the advance, so (6) fixes the ratio at
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
        /// ink-WIDTH reading conflates CPU spacing with shader size, "which is how the reverted P3c was
        /// approved by two review arms". This tooth does not read a width against an expectation; it reads
        /// whether consecutive runs are DISJOINT, which is a property of the two together and is exactly the
        /// user-visible claim. No tension.</para>
        ///
        /// <para>RED recipe: I1 (mechanism off) — the receding runs merge and the count drops below
        /// <c>GlyphCount</c>. I4 (a per-LABEL depth ruler, the reverted P3c shape) leaves the near symbol
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
                    "cell), so a merged reading here is the defect this stage removes, not a tolerance " +
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
        /// <b>W2-T1b — the RATIO of (6), read from ink where the ink is resolvable.</b> Proves:
        /// <c>advance_screen(i) / inkExtent_screen(i)</c> is the SAME number across every glyph pair whose ink
        /// run is large enough to measure — i.e. it does not drift with depth.
        ///
        /// <para><b>HONEST REACH, and the measurement that fixes it (followUp F-W2-7).</b> The plan designed
        /// this tooth to POOL both receding symbols for a 2.37× depth span. That is not constructible: at the
        /// shipped pose <c>RecedingFar</c>'s per-glyph ink runs measure <b>1–2 px</b>, so ±1 px of edge
        /// quantisation is a ±50–100 % error AND a systematic upward bias on <c>r</c> (a 1.4 px extent reads
        /// as 1 or 2, never as 1.4). Measured, pooled: <c>RecedingNear</c> gave r = 2.333 / 2.333 / 2.400 /
        /// 2.500 while <c>RecedingFar</c> gave 5.0 / 3.0 / 3.0 / 6.0 — the far readings are quantisation, not
        /// signal. Raising <c>SizePx</c> does not help (see <see cref="InkRunPose"/>: the fixture is
        /// scale-invariant in it), and raising <c>TextSizePx</c> enough to fix it drives the receding road's
        /// near end behind the camera, which the fixture asserts against. So this tooth reads only the
        /// resolvable band, says so, and the depth reach of the RATIO claim is carried instead by
        /// W2-T3(b) — which measures the identical quantity in WORLD METRES off the real mesh, at the shipped
        /// pose, at DPR 2, and at <b>8×</b> the look-at depth.</para>
        ///
        /// <para><b>Why the expected value is NOT <c>AdvanceBakedPx / cellWidthBaked</c>.</b> That constant
        /// (1.25) is the advance over the CELL width; an ink run measures the glyph's INK, which is narrower
        /// than its cell box by the side bearings and by the letterform itself. Measured, the 'F' reads
        /// ≈ 2.4 rather than 1.25 — a property of the glyph, not of the model. This tooth therefore asserts
        /// only that the ratio is CONSTANT ACROSS DEPTH, which is the part (6) actually claims about a
        /// rendered ink reading; the absolute constant is asserted where it is exact, against the CELL, by
        /// W2-T3(b). Asserting 1.25 here would be a tooth that pins the letterform, not the renderer.</para>
        ///
        /// <para>RED recipe: I1 — pre-W2 the extent is constant while the advance foreshortens, so <c>r</c>
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
        // W2-T2 — the maintainer's literal complaint, BRACKETING the measured 1.25× threshold
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T2 — "the letters must never overlap", at and past the depth where today's code first
        /// fails.</b> Proves: a map-pitched curved symbol segments into exactly <c>GlyphCount</c> ink runs,
        /// with a real gap between consecutive runs, at depth ratios <b>1.25</b> and <b>1.5</b> as well as at
        /// the shipped 2.0.
        ///
        /// <para><b>Where 1.25× comes from, and why bracketing it matters more than going deep.</b> The
        /// horizon measurement §4.1: the glyph cell is 32 screen px at <c>TextSizePx = 48</c> and the
        /// cross-arm anchor gap is 40 px at the look-at, so under the screen-constant size model the gap
        /// reaches the cell width at exactly <b>1.25× the look-at depth</b> (measured there: 32.0 px). That is
        /// the FIRST depth at which the shipped code fails — not the horizon. On a tilted map most of the
        /// frame is past 1.25×, which is why the maintainer sees the blob everywhere. A tooth that only
        /// asserted at extreme depth would leave that whole band unobserved.</para>
        ///
        /// <para><b>Why T2 does NOT go deep.</b> At ratio 8 the far symbol's entire 5-glyph screen extent is
        /// 0.773 px — under W2 the glyphs shrink with it, so there is no ink to count under EITHER model.
        /// Chasing depth here would make the tooth vacuous, which is the opposite of what R1 asks.</para>
        ///
        /// <para><b>SCOPE CAVEAT — read this before citing T2 as evidence for the model.</b> These cells read
        /// the CROSS-AZIMUTH arm, which is ISO-DEPTH by construction and therefore CANNOT distinguish a
        /// per-glyph world size from a per-symbol constant. That is fine here, because T2's claim is "letters
        /// do not merge", not "the size is per-glyph" — and the 32.0 px threshold was measured on the cross
        /// arm and is exact only there. <b>W2-T1 on the receding arm is the R1/R2 discriminator; T2 is the
        /// threshold observer.</b></para>
        ///
        /// <para><b>Non-vacuity is asserted per cell, not assumed.</b> The cell only means something if the
        /// pre-W2 model WOULD have merged these letters, i.e. if the measured anchor advance has fallen to at
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

            // NON-VACUITY. Under the pre-W2 screen-constant size the cell keeps its full width while the
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
        /// <b>W2-T2, the control BELOW the threshold.</b> The near (look-at) cross-azimuth symbol must
        /// segment into <c>GlyphCount</c> runs. Deliberately NOT a discriminator: at the look-at the two
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
        // W2-T3 — the world identity through the REAL emit, at the fixture's ceiling
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T3 leg (a) — the ABSOLUTE world cell size, and the DPR factor.</b> Proves: the corner offsets
        /// the renderer actually wrote into the slot mesh are WORLD METRES of exactly
        /// <c>cellWidthBaked · TextSizePx/OneEm · MetresPerLogicalPixel</c>.
        ///
        /// <para>Read from <c>WorldBillboardVertex.Offset</c> — production staging, production
        /// <c>BuildWorldQuad</c>, a real built mesh. The expectation is built from the fixture's OWN
        /// <c>MetresPerLogicalPixel = MetresPerDevicePixel × Config.DevicePixelRatio</c>, deliberately NOT
        /// from any production field that already carries the product: if both sides sourced the ratio from
        /// one place they would drop it together and the DPR leg would be vacuous, which is this epic's
        /// signature failure.</para>
        ///
        /// <para><b>This is the leg that carries the absolute scale.</b> Leg (b) below divides it away. Run
        /// at three poses — the shipped one, the deep tilt-72/ratio-8 one, and DPR 2 — because an omitted DPR
        /// factor shows here as a clean ×2 and cancels in (b) (P3a's injection 3 was exactly that shape).</para>
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
        /// <b>W2-T3 leg (b) — THE TOOTH THAT SAYS SIZE AND SPACING CAME FROM ONE CONSTANT.</b> Proves:
        /// <c>worldAdvance / cellWidth_world == AdvanceBakedPx / cellWidthBaked</c> — equation (5), with every
        /// scale cancelled — and that it reads the SAME number at the shipped pose, at DPR 2, and at the
        /// fixture's deepest constructible pose (tilt 72°, ratio 8, far anchor at ≈ 1 084 562 m).
        ///
        /// <para>This is the direct analogue of the horizon measurement's SPAN/ROAD reading: a quotient of two
        /// WORLD lengths, so it is immune to the legibility limit that stops T1 and T2 at ≈ 2.4×. It is also
        /// a real end-to-end reading — production staging, production <c>BuildWorldQuad</c>, a real mesh — at
        /// 8× depth, which no other tooth in the suite reaches.</para>
        ///
        /// <para><b>Reach, stated so it is not over-read (F-W2-5):</b> 8× is the deep pose this fixture measures
        /// at. The production far-distance cull is DISABLED for this fixture (<c>SymbolMaxDistanceFraction = +inf</c>
        /// in <c>OffLookAtSymbolScene</c>) so the deep pose's far anchors — which sit beyond the camera far distance
        /// — survive to be measured; the cull is not W2's subject. The identity is carried further still, on the
        /// CPU with no camera, by W2-T3(b) in <c>MapPitchedWorldArcStagingTests</c>.</para>
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
        // W2-T10 — the SPACING half, fenced at 8× (a W1 guarantee living in W2's file; followUp F-W2-6)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T10 (a) — SPAN/ROAD is depth-invariant at 8× the look-at depth.</b> Proves: each receding
        /// symbol's staged world span, as a fraction of its own road length, is the SAME at the deep
        /// tilt-72/ratio-8 pose as at the shipped tilt-55/ratio-2 one.
        ///
        /// <para><b>This is W1's guarantee, and that is exactly why it belongs in W2.</b> It is the regression
        /// fence that keeps the SPACING half honest while W2 rewrites the SIZE half around it, and it closes
        /// the "correct at 2×, wrong at 20×" gap that prompted the horizon measurement. Recorded as followUp
        /// F-W2-6: at the merge step it may read more naturally beside <c>MapPitchedWorldArcLayoutTests</c>;
        /// it lives here for now because it shares the tilt-72 pose with W2-T3 and the fixture should pay for
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
        /// reverted P3c shape) leaves this GREEN, because SPAN/ROAD is a spacing reading and I4 is a SIZE
        /// defect. That is the point: T10 fences the spacing half so a W2 regression cannot be misread as a
        /// W1 one. Its own RED comes from re-injecting the pre-W1 screen walk (<c>worldArc = false</c>),
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
        /// <b>W2-T10 (b) — the world residual at 8×.</b> Proves: at the deep pose every staged gap is
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
}
#endif
