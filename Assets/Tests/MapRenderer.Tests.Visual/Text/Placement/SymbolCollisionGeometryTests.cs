// Pitched-camera symbol collision-box and corner geometry GPU/visual acceptance tests (UMR-176 pack: text/placement sub-topic).
//
// Contents:
//   MapPitchedCollisionBoxTests  — Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader.
//   MapPitchedCornerUnitTests    — Unity EditMode only — real TiltedGroundScene + a REAL SymbolPlacementSystem.Tick, read back off the built slot mesh.

using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Visual
{
    // Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL
    // SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader. NOT registered in
    // Tools/core-tests/core-tests.csproj (it renders).
    //
    // Stage W3 — THE RENDERED ARM of the projected-world-corner collision box (W3-T1, T2, T3).
    //
    // THE DEFECT. Since W2 a map-pitched glyph is DRAWN as a world-metre quad lying in the ground plane at its
    // anchor, so its screen size foreshortens with depth. Its collision box was still a SCREEN box sized from
    // `TextSizePx` with no depth term anywhere, so it OVER-reserved — always, and only. At the shipped
    // OffLookAtSymbolScene pose the receding glyph advance is 29.07 px at the look-at and 0.19 px at 8× that
    // depth, so the box reserved roughly 150× the screen area the ink covers. Over-reservation can only ever
    // SUPPRESS a symbol; it can never make letters overlap and it can never misplace one. An eyeball symptom of
    // overlap or misplacement is therefore NOT this stage.
    //
    // THE THREE TEETH HERE, AND WHAT EACH CAN AND CANNOT SEE:
    //   • W3-T1 (headline) — the box IS the drawn quad's screen AABB, at ten view depths spanning >2×. Its
    //     oracle is built from the EMITTED VERTEX STREAM and the LIVE Unity camera, calling no SymbolStagingMath,
    //     no SymbolBox and no SymbolScreenProjection. Its PROJECTION leg is genuinely independent; its ŷ-SENSE leg
    //     is NOT — it re-derives the same sense production uses, so T1 alone cannot catch a shared sign error.
    //   • W3-T2 — the box CONTAINS the rendered ink. Containment also holds under the (strictly larger) pre-W3
    //     box, so T2 is NOT the depth discriminator; its job is the other direction — that W3 did not make the
    //     box too SMALL, and that the mesh-derived oracle T1 leans on lands where the GPU actually put ink.
    //   • W3-T3 — the ABSOLUTE SCALE and the DPR factor, at tilt 0, against the VIEWPORT arm, which shares no
    //     code with the map branch (`SymbolWorldIsMapPitched` sends them down mutually exclusive paths and the
    //     two staging branches are selected by one predicate). It also observes the ruler conjunct (measured by
    //     injection I6). It does NOT pin the ŷ SIGN: this box is an AABB, and a ŷ flip only permutes the corners
    //     of a y-symmetric cell — injection I2 flipped the sign in production and left the whole suite green.
    //     W3-T10 (in MapPitchedWorldArcStagingTests, on a deliberately off-centre cell) is the sign's sole
    //     observer. F-W3-6.
    //
    // NO METRE LITERALS in the tilt-0 harness: every world length is a multiple of `scene.MetresPerDevicePixel`,
    // the same rule TiltFixtureSelfTests, OffLookAtSymbolScene and MapPitchedGlyphSizeTiltZeroTests enforce.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapPitchedCollisionBoxTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapPitchedCollisionBoxTests
    {
        /// <summary>Agreement bound between the staged box and the quad's own screen AABB, in px. Residual
        /// sources: production projects the <c>double3</c> render-space world point while the mesh carries the
        /// float-narrowed Level-1 RTC <c>AnchorLocal</c> (~1 ulp of ~10⁶ m ≈ 0.06 m, and at this pose one
        /// device px is ~306 m, so ≈ 2·10⁻⁴ px); and <c>float4x4</c> arithmetic against Unity's own
        /// <c>WorldToScreenPoint</c> (~10⁻³ px). The bound is ~500× the expected residual and ~60× below the
        /// signal — at <c>RecedingFar</c> the pre-W3 box is ~30 px tall against a true quad of ~1.4 px.</summary>
        private const double BoxAgreementPx = 0.5;

        /// <summary>Rasterisation slack, per edge, for the ink-containment reading.</summary>
        private const int InkSlackPx = 1;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T1 — THE HEADLINE: the box IS the drawn quad's screen AABB, at ten depths on the receding arm
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T1 — the stage's headline tooth.</b> For every glyph of both receding symbols, the staged
        /// <see cref="SymbolBox"/> equals the axis-aligned screen bound of the FOUR WORLD CORNERS the renderer
        /// actually emitted, to <see cref="BoxAgreementPx"/>.
        ///
        /// <para><b>The oracle calls no production placement code.</b> It reads each glyph's four
        /// <c>WorldBillboardVertex</c>es off the built mesh, rebuilds the ground frame from that vertex's OWN
        /// <c>Up</c>/<c>Tangent</c> through the slot transform, displaces by <c>Offset</c> (y-DOWN, per the
        /// A0-F2 negation, so the y-UP amount is <c>−Offset.y</c>), and projects through the LIVE Unity
        /// camera with <c>GroundRuler.ProjectPx</c> — a different code path and a different matrix from
        /// <c>SymbolScreenProjection</c>'s.</para>
        ///
        /// <para><b>Honest reach.</b> The oracle's PROJECTION leg is genuinely independent. Its <b>ŷ-sense leg
        /// is not</b>: it re-derives <c>ŷ = cross(x̂, up)</c>, the same sense production uses, so a shared sign
        /// error would move both sides together and this tooth would stay GREEN. <b>W3-T10 is what closes
        /// that</b> — not W3-T3, whose AABB cannot see the sign at all on a y-symmetric cell (F-W3-6,
        /// measured by injection I2).</para>
        ///
        /// <para><b>The anti-P3c precondition.</b> Within <c>RecedingNear</c> alone the reconstructed quad's
        /// screen height must vary by ≥ 4 px between its first and last glyph. A box scaled by ONE constant
        /// per symbol — the model that got P3c reverted, and the one <c>CrossNear</c>/<c>CrossFar</c> cannot
        /// refute because they are iso-depth by construction — is then provably unable to clear the 0.5 px
        /// bound.</para>
        /// </summary>
        [Test]
        public void MapPitchedBox_IsTheDrawnQuadsScreenAabb_AtEveryDepth()
        {
            using var f = OffLookAtSymbolScene.Create(new OffLookAtSymbolSceneConfig());
            Assert.That(f.Config.DevicePixelRatio, Is.EqualTo(1.0),
                "W3-T1 precondition: SymbolBox is in LOGICAL px and GroundRuler.ProjectPx reports DEVICE px — " +
                "they coincide only at DPR 1. The DPR-2 arm belongs to W3-T3, where the identity is exact by " +
                "construction.");
            UnityEngine.Camera cam = f.UnityCamera;

            var depths = new List<double>();
            var nearHeights = new List<double>();
            double worstResidualPx = 0.0;

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                f.RenderIsolated(id, out SymbolBox[] boxes, out WorldBillboardVertex[] vertices,
                    out Transform slot);
                Assert.That(boxes.Length, Is.EqualTo(f.Config.GlyphCount),
                    $"W3-T1 precondition ({id}): the isolated Tick must stage exactly {f.Config.GlyphCount} " +
                    $"boxes, got {boxes.Length} — the box↔quad pairing this tooth reads would be undefined.");
                Assert.That(vertices.Length, Is.EqualTo(4 * f.Config.GlyphCount),
                    $"W3-T1 precondition ({id}): the slot mesh must carry 4 vertices per glyph, got " +
                    $"{vertices.Length}.");

                GlyphMeasurement[] measured = f.Measure(id).Glyphs;
                TestContext.WriteLine($"-- W3-T1 {id} --");
                TestContext.WriteLine(
                    "   g   viewDepthM      box [minX, minY, maxX, maxY]                quad AABB " +
                    "[minX, minY, maxX, maxY]           residuals (dMinX dMinY dMaxX dMaxY)");

                for (int g = 0; g < boxes.Length; g++)
                {
                    QuadScreenAabb(cam, slot, vertices, g, out double2 quadMin, out double2 quadMax);
                    SymbolBox b = boxes[g];

                    double dMinX = b.Min.x - quadMin.x, dMinY = b.Min.y - quadMin.y;
                    double dMaxX = b.Max.x - quadMax.x, dMaxY = b.Max.y - quadMax.y;
                    worstResidualPx = math.max(worstResidualPx, math.max(
                        math.max(math.abs(dMinX), math.abs(dMinY)),
                        math.max(math.abs(dMaxX), math.abs(dMaxY))));

                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "   {0,-3} {1,11:F1}   [{2,9:F3}, {3,9:F3}, {4,9:F3}, {5,9:F3}]   " +
                        "[{6,9:F3}, {7,9:F3}, {8,9:F3}, {9,9:F3}]   ({10:F4} {11:F4} {12:F4} {13:F4})",
                        g, measured[g].ViewDepthMetres, b.Min.x, b.Min.y, b.Max.x, b.Max.y,
                        quadMin.x, quadMin.y, quadMax.x, quadMax.y, dMinX, dMinY, dMaxX, dMaxY));

                    depths.Add(measured[g].ViewDepthMetres);
                    if (id == OffLookAtSymbolId.RecedingNear) nearHeights.Add(quadMax.y - quadMin.y);

                    Assert.That(math.abs(dMinX), Is.LessThanOrEqualTo(BoxAgreementPx),
                        $"W3-T1 ({id}, glyph {g}): box Min.x is {dMinX:F4} px from the drawn quad's own " +
                        "screen AABB. The map-pitched box must BE that AABB at every depth.");
                    Assert.That(math.abs(dMinY), Is.LessThanOrEqualTo(BoxAgreementPx),
                        $"W3-T1 ({id}, glyph {g}): box Min.y is {dMinY:F4} px from the drawn quad's AABB.");
                    Assert.That(math.abs(dMaxX), Is.LessThanOrEqualTo(BoxAgreementPx),
                        $"W3-T1 ({id}, glyph {g}): box Max.x is {dMaxX:F4} px from the drawn quad's AABB.");
                    Assert.That(math.abs(dMaxY), Is.LessThanOrEqualTo(BoxAgreementPx),
                        $"W3-T1 ({id}, glyph {g}): box Max.y is {dMaxY:F4} px from the drawn quad's AABB.");
                }
            }

            // ── the preconditions that make the reading falsifiable, asserted after the table is printed ──
            int distinct = DistinctCount(depths, relativeSeparation: 0.01);
            double depthRatio = MaxOf(depths) / MinOf(depths);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T1 SUMMARY  {0} glyph depths, {1} distinct (1 % apart), max/min = {2:F4}, " +
                "RecedingNear quad screen height {3:F3} → {4:F3} px, worst residual {5:F5} px",
                depths.Count, distinct, depthRatio, nearHeights[0], nearHeights[nearHeights.Count - 1],
                worstResidualPx));

            Assert.That(distinct, Is.GreaterThanOrEqualTo(6),
                $"W3-T1 precondition: the two labels must present at least 6 distinct view depths (1 % apart) " +
                $"— counted {distinct} of {depths.Count}. A depth-blind box passes trivially on a single depth.");
            Assert.That(depthRatio, Is.GreaterThanOrEqualTo(2.0),
                $"W3-T1 precondition: the depth span must be at least 2× — measured {depthRatio:F4}.");
            double heightSpread = math.abs(nearHeights[nearHeights.Count - 1] - nearHeights[0]);
            Assert.That(heightSpread, Is.GreaterThanOrEqualTo(4.0),
                $"W3-T1 precondition (ANTI-P3c): within RecedingNear alone the reconstructed quad's screen " +
                $"height must vary by ≥ 4 px between its first and last glyph — measured {heightSpread:F3} px " +
                $"({nearHeights[0]:F3} → {nearHeights[nearHeights.Count - 1]:F3}). Without it a box scaled by " +
                "ONE constant per label could clear the 0.5 px bound, and that model is exactly what P3c was " +
                "reverted for.");
        }

        /// <summary>
        /// THE ORACLE: one glyph's four drawn corners, projected, as a screen AABB. Reads only the emitted
        /// vertex stream, the slot transform the renderer itself built, and the LIVE camera — no
        /// <c>SymbolStagingMath</c>, no <c>SymbolBox</c>, no <c>SymbolScreenProjection</c>.
        /// </summary>
        private static void QuadScreenAabb(UnityEngine.Camera cam, Transform slot, WorldBillboardVertex[] vertices,
            int glyphIndex, out double2 min, out double2 max)
        {
            min = new double2(double.MaxValue);
            max = new double2(double.MinValue);
            for (int c = 0; c < 4; c++)
            {
                WorldBillboardVertex v = vertices[4 * glyphIndex + c];
                Vector3 upUnity = slot.TransformDirection(new Vector3(v.Up.x, v.Up.y, v.Up.z));
                Vector3 tanUnity = slot.TransformDirection(new Vector3(v.Tangent.x, v.Tangent.y, v.Tangent.z));
                var up = new double3(upUnity.x, upUnity.y, upUnity.z);
                var tangent = new double3(tanUnity.x, tanUnity.y, tanUnity.z);
                Assert.That(math.lengthsq(up) > 0.5 && math.lengthsq(tangent) > 0.5, Is.True,
                    $"W3-T1 precondition (glyph {glyphIndex}, corner {c}): the emitted frame must be " +
                    $"non-degenerate — Up={up}, Tangent={tangent}. A zero here means the SHADER took its " +
                    "camera-facing fallback and this oracle is reconstructing a quad the GPU never drew.");

                // The SAME frame the shader builds (Gram-Schmidt into the surface, then ŷ = cross(x̂, up)),
                // and the SAME y-DOWN Offset convention (A0-F2), so −Offset.y is the y-UP amount.
                double3 xh = math.normalize(tangent - up * math.dot(tangent, up));
                double3 yh = math.cross(xh, up);
                Vector3 anchorUnity = slot.TransformPoint(
                    new Vector3(v.AnchorLocal.x, v.AnchorLocal.y, v.AnchorLocal.z));
                double3 corner = new double3(anchorUnity.x, anchorUnity.y, anchorUnity.z)
                                 + xh * v.Offset.x + yh * (-v.Offset.y);

                double2 px = GroundRuler.ProjectPx(cam, corner);
                min = math.min(min, px);
                max = math.max(max, px);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T2 — the box contains the RENDERED INK, at both receding depths
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T2 — corroboration, in the other direction.</b> Every per-glyph ink run of both receding
        /// symbols lies INSIDE that glyph's staged box (mapped into ink-buffer coordinates,
        /// <c>row = SizePx − 1 − y</c>), with <see cref="InkSlackPx"/> of rasterisation slack per edge.
        ///
        /// <para><b>What this does and does not prove.</b> Containment ALSO holds under the pre-W3 box, which
        /// is strictly larger — so <b>T2 is not the depth discriminator; W3-T1 is</b>. T2's job is that W3 did
        /// not make the box too SMALL, and that the mesh-derived oracle T1 leans on actually lands where the
        /// GPU put ink. It reaches both receding depths because containment, unlike a ratio, does not need a
        /// resolvable run width — which is precisely the limit W2-T1b measured and recorded
        /// (<c>RecedingFar</c>'s runs are 1–2 px).</para>
        /// </summary>
        [Test]
        public void MapPitchedBox_ContainsTheRenderedInk_AtBothRecedingDepths()
        {
            using var f = OffLookAtSymbolScene.Create(new OffLookAtSymbolSceneConfig());
            Assert.That(f.Config.DevicePixelRatio, Is.EqualTo(1.0),
                "W3-T2 precondition: the box is LOGICAL px and the ink buffer is DEVICE px — they coincide " +
                "only at DPR 1.");
            int size = f.Config.SizePx;

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
            {
                OffLookAtInkRuns.AssertRunsVertically(f, id, f.Measure(id).Glyphs);
                Color32[] pixels = f.RenderIsolated(id, out SymbolBox[] boxes, out _, out _);
                f.ColumnBandFor(id, out int colFrom, out int colTo);
                colFrom = math.max(0, colFrom);
                colTo   = math.min(size - 1, colTo);

                (int start, int end)[] runs = OffLookAtInkRuns.AlongRows(pixels, size, colFrom, colTo);
                Assert.That(runs.Length, Is.EqualTo(f.Config.GlyphCount),
                    $"W3-T2 precondition ({id}): expected {f.Config.GlyphCount} ink runs inside the column " +
                    $"band, got {runs.Length}. Merged or missing runs make the run↔glyph pairing below wrong.");

                // The runs come out ordered by increasing ink-buffer ROW; the boxes are in glyph order along
                // the road, which for an up-screen symbol is the reverse. Pair them by sorting the BOXES by
                // their own centre row — the box-side analogue of MapPitchedGlyphSizeTests' OrderedByScreenRow,
                // and deliberately NOT a search for "whichever box contains this run" (which could not fail).
                int[] order = BoxesByCentreRow(boxes, size);

                for (int i = 0; i < runs.Length; i++)
                {
                    SymbolBox b = boxes[order[i]];
                    double boxRowMin = size - 1 - b.Max.y, boxRowMax = size - 1 - b.Min.y;
                    InkColumnExtent(pixels, size, runs[i], colFrom, colTo, out int inkColMin, out int inkColMax);

                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "W3-T2  {0} run {1}: rows [{2}, {3}] cols [{4}, {5}]   box rows [{6:F2}, {7:F2}] " +
                        "cols [{8:F2}, {9:F2}]",
                        id, i, runs[i].start, runs[i].end, inkColMin, inkColMax,
                        boxRowMin, boxRowMax, b.Min.x, b.Max.x));

                    Assert.That(inkColMin, Is.GreaterThan(colFrom).And.LessThan(colTo),
                        $"W3-T2 precondition ({id}, run {i}): the ink must sit strictly inside the column band " +
                        $"[{colFrom}, {colTo}] — a run clipped by the band would have its column extent " +
                        "artificially shrunk and could satisfy containment vacuously.");
                    Assert.That(inkColMax, Is.GreaterThan(colFrom).And.LessThan(colTo),
                        $"W3-T2 precondition ({id}, run {i}): same, for the run's right edge.");

                    Assert.That(runs[i].start, Is.GreaterThanOrEqualTo(boxRowMin - InkSlackPx),
                        $"W3-T2 ({id}, run {i}): ink starts at row {runs[i].start}, above the box's top row " +
                        $"{boxRowMin:F2}. The box must BOUND the ink — this is the direction W3 could have " +
                        "broken by making the box too small.");
                    Assert.That(runs[i].end, Is.LessThanOrEqualTo(boxRowMax + InkSlackPx),
                        $"W3-T2 ({id}, run {i}): ink ends at row {runs[i].end}, below the box's bottom row " +
                        $"{boxRowMax:F2}.");
                    Assert.That(inkColMin, Is.GreaterThanOrEqualTo(b.Min.x - InkSlackPx),
                        $"W3-T2 ({id}, run {i}): ink starts at column {inkColMin}, left of the box's " +
                        $"{b.Min.x:F2}.");
                    Assert.That(inkColMax, Is.LessThanOrEqualTo(b.Max.x + InkSlackPx),
                        $"W3-T2 ({id}, run {i}): ink ends at column {inkColMax}, right of the box's " +
                        $"{b.Max.x:F2}.");
                }
            }
        }

        /// <summary>Box indices ordered by increasing ink-buffer centre row.</summary>
        private static int[] BoxesByCentreRow(SymbolBox[] boxes, int size)
        {
            var order = new int[boxes.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            System.Array.Sort(order, (a, b) =>
                (size - 1 - 0.5f * (boxes[a].Min.y + boxes[a].Max.y))
                .CompareTo(size - 1 - 0.5f * (boxes[b].Min.y + boxes[b].Max.y)));
            return order;
        }

        /// <summary>The inclusive column extent of the ink inside one row run.</summary>
        private static void InkColumnExtent(Color32[] rgba, int size, (int start, int end) run,
            int colFrom, int colTo, out int colMin, out int colMax)
        {
            colMin = int.MaxValue;
            colMax = int.MinValue;
            for (int row = run.start; row <= run.end; row++)
                for (int col = colFrom; col <= colTo; col++)
                    if (rgba[row * size + col].r < WorldSymbolInkAnalysis.InkThreshold)
                    {
                        if (col < colMin) colMin = col;
                        if (col > colMax) colMax = col;
                    }
            Assert.That(colMax, Is.GreaterThanOrEqualTo(colMin),
                "W3-T2 precondition: an ink run with no inked pixel in it — the segmentation and the " +
                "re-scan disagree, so nothing below is measurable.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T3 — the ABSOLUTE SCALE and the DPR factor, at tilt 0, against the viewport arm
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T3 — the absolute scale and the DPR factor, calibrated against an arm that shares no code
        /// with the one under test.</b> At tilt 0 a map-pitched curved
        /// symbol's collision box must equal its viewport-pitched twin's, edge for edge, at both device-pixel
        /// ratios and three road angles.
        ///
        /// <para><b>Why this is exact and not a tolerance argument.</b> At tilt 0 the ground plane is
        /// perpendicular to the view axis, so every ground point shares ONE view depth — the look-at depth,
        /// where <c>MetresPerLogicalPixel</c> is BY DEFINITION the ruler. A corner displaced
        /// <c>c · MetresPerLogicalPixel</c> metres in that plane therefore projects to exactly <c>c</c> logical
        /// px, which is what the viewport arm adds directly to the anchor. The two arc walks agree too: at
        /// tilt 0 the projection restricted to the ground plane is affine, so world arc length and screen arc
        /// length are proportional and both walks place a glyph at the same point. The two boxes must be
        /// EQUAL.</para>
        ///
        /// <para><b>The reference shares no code with the arm under test.</b> The two symbols differ in exactly
        /// one field, <c>ShapedSymbol.PitchAlignment</c>; that one predicate selects
        /// <c>SymbolStagingMath</c>'s world-arc walk, the metre corner unit, and W3's projected box. P3a's
        /// rebuilt-T2 is the recorded lesson about references drawn from the arm under test.</para>
        ///
        /// <para><b>What each failure mode looks like.</b> A wrong
        /// <c>emScale</c> (the <c>arcScale</c> substitution §1.3 warns about, or a dropped DPR factor) shows as
        /// a proportional SIZE error at DPR 2 while staying invisible at DPR 1 — which is why both ratios run.
        /// A total failure to project reads as the pre-W3 screen box — <b>which at tilt 0 IS the same box</b>,
        /// so this tooth cannot by itself tell "correctly projected" from "never projected at all". That is the
        /// price of the pose that makes the comparison exact, and it is stated rather than papered over: the
        /// MECHANISM's presence is pinned at tilt by W3-T1/T2 and by W3-T4, which all go RED if the projected
        /// branch stops being taken. What T3 adds, and only T3 has, is the absolute-scale and DPR calibration
        /// against an arm that shares no code with the one under test.</para>
        ///
        /// <para><b>⚠ This tooth does NOT pin the ŷ sign — measured, not assumed.</b> The box is an AABB of
        /// the cell's corners, so a ŷ flip merely PERMUTES that set whenever the cell is symmetric about its
        /// anchor in y, and an AABB is invariant under permutation — and W4 centres curved glyph cells on the
        /// path. Injection I2 flipped the sign in production and left the ENTIRE suite green, this tooth
        /// included. <b>W3-T10</b> (<c>MapPitchedWorldArcStagingTests</c>, on a deliberately off-centre cell)
        /// is the sign's sole observer. F-W3-6; W2's 22.56 px flipped-ŷ separation was an ink-CENTROID
        /// reading and does not carry over to an AABB.</para>
        /// </summary>
        [Test]
        public void MapPitchedBox_AtTiltZero_EqualsTheViewportBox(
            [Values(1.0, 2.0)] double devicePixelRatio,
            [Values(0f, 45f, 90f)] float roadAngleDeg)
        {
            StageTiltZeroTwin(devicePixelRatio, roadAngleDeg,
                out SymbolBox[] map, out SymbolBox[] viewport, out bool rulerIsLive);

            Assert.That(map.Length, Is.EqualTo(viewport.Length),
                $"W3-T3 precondition (DPR {devicePixelRatio}, road {roadAngleDeg}°): the twin arms staged " +
                $"{map.Length} and {viewport.Length} boxes — they must be the same label but for one field.");
            Assert.That(map.Length, Is.GreaterThan(0),
                "W3-T3 precondition: nothing staged, so this comparison would pass vacuously.");
            Assert.That(rulerIsLive, Is.True,
                $"W3-T3 precondition (DPR {devicePixelRatio}, road {roadAngleDeg}°): the scene must publish a " +
                "positive metres-per-pixel ruler — the necessary condition for the map arm's box to be the " +
                "projected one at all. (It is necessary, not sufficient; see this tooth's doc for why no " +
                "sufficient condition is observable at tilt 0.)");

            for (int g = 0; g < map.Length; g++)
            {
                double dMinX = map[g].Min.x - viewport[g].Min.x;
                double dMinY = map[g].Min.y - viewport[g].Min.y;
                double dMaxX = map[g].Max.x - viewport[g].Max.x;
                double dMaxY = map[g].Max.y - viewport[g].Max.y;
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "W3-T3  DPR={0:F1} angle={1:F0}deg glyph {2}  map=[{3:F3}, {4:F3}, {5:F3}, {6:F3}]  " +
                    "viewport=[{7:F3}, {8:F3}, {9:F3}, {10:F3}]  Δ=({11:F4}, {12:F4}, {13:F4}, {14:F4})",
                    devicePixelRatio, roadAngleDeg, g,
                    map[g].Min.x, map[g].Min.y, map[g].Max.x, map[g].Max.y,
                    viewport[g].Min.x, viewport[g].Min.y, viewport[g].Max.x, viewport[g].Max.y,
                    dMinX, dMinY, dMaxX, dMaxY));

                Assert.That(math.abs(dMinX), Is.LessThanOrEqualTo(1.0),
                    $"W3-T3 (DPR {devicePixelRatio}, road {roadAngleDeg}°, glyph {g}): Min.x differs by " +
                    $"{dMinX:F4} px. At tilt 0 the map-pitched box and its viewport twin are the same box.");
                Assert.That(math.abs(dMinY), Is.LessThanOrEqualTo(1.0),
                    $"W3-T3 (DPR {devicePixelRatio}, road {roadAngleDeg}°, glyph {g}): Min.y differs by " +
                    $"{dMinY:F4} px. A flipped ŷ moves this edge by 2·|c_y| whenever the cell is not " +
                    "symmetric about its anchor in y.");
                Assert.That(math.abs(dMaxX), Is.LessThanOrEqualTo(1.0),
                    $"W3-T3 (DPR {devicePixelRatio}, road {roadAngleDeg}°, glyph {g}): Max.x differs by " +
                    $"{dMaxX:F4} px. A dropped DevicePixelRatio shows here at DPR 2 and nowhere else.");
                Assert.That(math.abs(dMaxY), Is.LessThanOrEqualTo(1.0),
                    $"W3-T3 (DPR {devicePixelRatio}, road {roadAngleDeg}°, glyph {g}): Max.y differs by " +
                    $"{dMaxY:F4} px.");
            }
        }

        /// <summary>
        /// Stages the SAME one-glyph curved symbol twice through ONE scene and ONE camera at tilt 0 — once with
        /// <c>PitchAlignment = Map</c>, once with <c>Viewport</c> — and returns both arms' staged collision
        /// boxes. No render: the measurand is the staged box, not ink, so this needs no
        /// <c>SnapshotRenderer</c> (which is what keeps it from being a second copy of
        /// <c>MapPitchedGlyphSizeTiltZeroTests.RenderArm</c> — it shares that file's twin CONSTRUCTION, not
        /// its ink machinery).
        /// </summary>
        private static void StageTiltZeroTwin(double devicePixelRatio, float roadAngleDeg,
            out SymbolBox[] map, out SymbolBox[] viewport, out bool rulerIsLive)
        {
            const int   sizePx     = 512;
            const float textSizePx = 160f;
            const double roadHalfLengthRulerUnits = 200.0;

            var sceneConfig = new TiltedGroundSceneConfig
            {
                TiltDegrees      = 0.0,   // THE pose — see this tooth's doc.
                SizePx           = sizePx,
                DevicePixelRatio = devicePixelRatio,
                BackgroundColor  = Color.white,
                LitAmbient       = false,
            };

            TiltedGroundScene    scene  = null;
            GlyphAtlasTexture    atlas  = null;
            SymbolPlacementSystem system = null;
            TestSymbolPlan       plan   = null;
            try
            {
                scene = TiltedGroundScene.Create(sceneConfig);
                SymbolQuad cell;
                (atlas, cell) = WorldCurvedAbRenderSnapshotTests.BuildGlyphF();

                SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
                double3 origin = frame.SceneOriginRender;
                double mpp = scene.MetresPerDevicePixel;
                rulerIsLive = mpp > 0.0;

                // The road, in the render-space XZ plane. NO metre literals — every length is a multiple of
                // the frame ruler.
                double rad = math.radians(roadAngleDeg);
                var dir = new double3(math.cos(rad), 0.0, math.sin(rad));
                double halfLen = roadHalfLengthRulerUnits * mpp;
                double3 pathA = origin - dir * halfLen;
                double3 pathB = origin + dir * halfLen;
                var up = new double3(0.0, 1.0, 0.0); // Web-Mercator scene, so up IS (0,1,0)

                long tileKey = TestTileKeys.PackedContaining(sceneConfig.LookAt.Surface, zoom: 14);

                system = new SymbolPlacementSystem(scene.MapCam,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                plan = new TestSymbolPlan(scene.MapCam.Projection);

                map      = StageArm(system, plan, atlas, in frame, pathA, pathB, up, cell, tileKey,
                                    textSizePx, AlignmentMode.Map, "map");
                viewport = StageArm(system, plan, atlas, in frame, pathA, pathB, up, cell, tileKey,
                                    textSizePx, AlignmentMode.Viewport, "viewport");
            }
            finally
            {
                plan?.Dispose();
                system?.Dispose();
                atlas?.Dispose();
                scene?.Dispose();
            }
        }

        private static SymbolBox[] StageArm(SymbolPlacementSystem system, TestSymbolPlan plan,
            GlyphAtlasTexture atlas, in SceneFrame frame, double3 pathA, double3 pathB, double3 up,
            in SymbolQuad cell, long tileKey, float textSizePx, AlignmentMode pitch, string armName)
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
                textSizePx: textSizePx,
                maxAngleDeg: 180f,
                keepUpright: false,
                // P3a's recorded lesson: at coarse zoom the dedup/collision machinery decides who emits and a
                // fixture silently loses its symbol.
                allowOverlap: true,
                featureIndex: 0,
                tileKey: tileKey);

            // R3: duplicate Tick — the collision verdict is harvested one Tick late.
            system.Tick(in frame, plan.Build(buffer), atlas);
            system.Tick(in frame, plan.Build(buffer), atlas);
            Assert.That(system.LastQuadCount, Is.EqualTo(1),
                $"W3-T3 precondition ({armName}): the label must stage exactly one quad, got " +
                $"{system.LastQuadCount}. A zero means it spilled its road or was culled.");

            NativeArray<SymbolBox> staged = system.LastStagedBoxes();
            var boxes = new SymbolBox[system.LastBoxCount];
            for (int b = 0; b < boxes.Length; b++) boxes[b] = staged[b];
            return boxes;
        }

        // ── small numeric helpers ───────────────────────────────────────────────────────────────────────

        private static int DistinctCount(List<double> values, double relativeSeparation)
        {
            var kept = new List<double>();
            foreach (double v in values)
            {
                bool novel = true;
                foreach (double k in kept)
                    if (math.abs(v - k) <= relativeSeparation * math.abs(k)) { novel = false; break; }
                if (novel) kept.Add(v);
            }
            return kept.Count;
        }

        private static double MaxOf(List<double> values)
        {
            double m = double.MinValue;
            foreach (double v in values) m = math.max(m, v);
            return m;
        }

        private static double MinOf(List<double> values)
        {
            double m = double.MaxValue;
            foreach (double v in values) m = math.min(m, v);
            return m;
        }
    }

    // Unity EditMode only — real TiltedGroundScene + a REAL SymbolPlacementSystem.Tick, read back off the built
    // slot mesh. NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // Stage W2 — the CORNER-UNIT teeth that need the RENDERER (W2-T7, W2-T8).
    //
    // PLACEMENT NOTE, deliberate and worth stating. The W2 plan filed T7 under `BillboardMathTests` and T8 under
    // `MapPitchedWorldArcStagingTests`. Both of those are engine-free files that reach only `MapRenderer.Core`,
    // and both of these teeth are claims about `WorldSymbolRenderer.Emit` — "the four WorldBillboardVertexs
    // produced by the RENDERER", and "the renderer scales the translate delta by the corner unit". Asserting
    // either one without the renderer in the loop would be asserting the test's own arithmetic, which is the
    // self-referential-oracle failure this epic already made once. So they live here, on the real emit path, and
    // the deviation from the plan's filing is recorded rather than silently taken.
    //
    // WHY tilt 0. Neither tooth reads a projected quantity — both read the emitted VERTEX STREAM off the built
    // mesh — so the pose only has to be one where the symbols stage reliably. Tilt 0 is the simplest such pose and
    // it is the one MapPitchedGlyphSizeTiltZeroTests already uses, so the two files share a shape.

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapPitchedCornerUnitTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapPitchedCornerUnitTests
    {
        private const int   SizePx     = 512;
        private const float TextSizePx = 160f;

        /// <summary>`AlignFlags` bit1 — Stage AC's along-line rotation. Spelled out here rather than read from
        /// <c>WorldSymbolRenderer</c>'s own private constant: a tooth that compares a value to itself pins
        /// nothing (the same reason <c>WorldSymbolGroupingTests</c> asserts its hierarchy names literally).</summary>
        private const float AlongLineBit = 2f;

        /// <summary>`AlignFlags` bit2 — W2's map-pitch bit, same rationale.</summary>
        private const float MapPitchBit = 4f;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T7 — the flag and the unit cannot disagree (R3)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T7, clause 1 — the STRUCTURAL statement of R3: the non-map path is BITWISE unchanged.</b>
        /// Proves: for a curved symbol whose <c>CornerMetresPerLogicalPixel</c> is 0 (every viewport- and
        /// auto-pitched symbol, and every point symbol), the four <see cref="WorldBillboardVertex"/>s the
        /// renderer emitted are bit-for-bit equal to a <see cref="BillboardMath.BuildWorldQuad"/> call made
        /// with the PRE-W2 arguments — <c>q.TextSizePx</c> unscaled, <c>emit.TranslateDeltaPx</c> unscaled,
        /// <c>alignFlags = AlongLineAlignFlag</c>.
        ///
        /// <para><b>Compared as raw float BITS, not with a tolerance.</b> The claim W2's stage invariant
        /// makes is not "close enough" — it is that <c>cornerScale</c> is the literal <c>1f</c> on every
        /// non-map path and <c>x * 1f</c> is bitwise identity for every finite float and for ±0/±Inf/NaN
        /// payloads alike, so the vertex stream is EXACTLY what it was before this stage. A tolerance would
        /// let a real unit slip through as rounding.</para>
        ///
        /// <para><b>The reference does not come from the arm under test.</b> The expected quad is built from
        /// the symbol's own baked cell plus the mesh's own <c>AnchorLocal</c>/<c>Tangent</c>/<c>Up</c> — i.e.
        /// from the geometry, not from the corner offsets being checked.</para>
        /// </summary>
        [Test]
        public void NonMapPitchedCorners_AreBitwiseThePreW2Quad()
        {
            WorldBillboardVertex[] v = EmitAndRead(AlignmentMode.Viewport, float2.zero, out SymbolQuad cell, out _);

            // Hoisted into locals: an `in` parameter needs an addressable operand, and several of these are
            // property values or static members rather than fields.
            float3 anchor  = v[0].AnchorLocal;
            float3 colour  = v[0].ColorRGB;
            float3 tangent = v[0].Tangent;
            float3 up      = v[0].Up;
            float2 noTrans = float2.zero;
            BillboardMath.BuildWorldQuad(in cell, in anchor, TextSizePx, in colour,
                rotationRadians: 0f, in noTrans, in tangent, in up, alignFlags: AlongLineBit,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr,
                out WorldBillboardVertex br, out WorldBillboardVertex bl);

            WorldBillboardVertex[] expected = { tl, tr, br, bl };
            string[] corner = { "topLeft", "topRight", "bottomRight", "bottomLeft" };
            for (int c = 0; c < 4; c++)
            {
                AssertBitwise(expected[c].Offset.x, v[c].Offset.x, $"W2-T7 {corner[c]}.Offset.x");
                AssertBitwise(expected[c].Offset.y, v[c].Offset.y, $"W2-T7 {corner[c]}.Offset.y");
                AssertBitwise(expected[c].AlignFlags, v[c].AlignFlags, $"W2-T7 {corner[c]}.AlignFlags");
            }
        }

        /// <summary>
        /// <b>W2-T7, clause 2 — the flag tracks the unit, and the scope FENCE is asserted.</b> Proves: when
        /// the corner unit is metres, every corner's <c>AlignFlags</c> has bit2 set — and bit1 as well,
        /// because in W2 map-pitch never occurs without along-line.
        ///
        /// <para>Bit1 is not decoration here. E7's <c>emit.AlongLine</c> conjunct is the scope fence the plan
        /// drew: point/icon map-pitch is a LATER stage. Asserting that bit2 never appears without bit1 is what
        /// makes that fence observable instead of merely commented — if a future stage lifts it, this tooth
        /// says so.</para>
        ///
        /// <para>A separate <c>[Test]</c> from clause 1 on purpose: NUnit throws on the first failure, so two
        /// clauses in one method means the second never runs. That has already cost this epic two teeth.</para>
        /// </summary>
        [Test]
        public void MapPitchedCorners_CarryBit2_AndNeverWithoutBit1()
        {
            WorldBillboardVertex[] v = EmitAndRead(AlignmentMode.Map, float2.zero, out _, out _);

            for (int c = 0; c < v.Length; c++)
            {
                float flags = v[c].AlignFlags;
                bool bit2 = math.fmod(math.floor(flags * 0.25f), 2f) >= 0.5f;
                bool bit1 = math.fmod(math.floor(flags * 0.5f),  2f) >= 0.5f;
                Assert.That(bit2 && bit1, Is.True,
                    $"W2-T7 (vertex {c}): a map-pitched corner must carry AlignFlags bit2 (metres) AND bit1 " +
                    $"(along-line) — flags read {flags}. Expected {AlongLineBit + MapPitchBit}. bit2 without " +
                    "bit1 would mean a point/icon label had crossed W2's scope fence; bit1 without bit2 means " +
                    "the shader will read metre offsets with the pixel formula.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T8 — the recorded `text-translate` limitation's observer (R4 / followUp F-W2-2)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T8 — the observer for followUp F-W2-2.</b> Proves: under map pitch the <c>text-translate</c>
        /// delta rides the corner offsets in the SAME unit they do — i.e. it is scaled by
        /// <c>CornerMetresPerLogicalPixel</c> and becomes a WORLD translate.
        ///
        /// <para><b>Why this is a recorded limitation and not a design position.</b> The shader has ONE
        /// displacement path, so the corners and the translate must share whichever unit is in force; keeping
        /// the translate in screen px would mean a second, clip-space displacement path, which re-creates the
        /// two-rulers-in-one-shader shape that got three stages reverted. The Style Spec's proper control for
        /// whether a translate is a screen or a map offset is <c>text-translate-anchor</c>, not
        /// <c>text-pitch-alignment</c>, so the behaviour is recorded rather than defended.</para>
        ///
        /// <para><b>It is inert on every shipped style.</b> All seven line-placed symbol layers in
        /// <c>liberty.json</c> (<c>road_one_way_arrow</c>, <c>road_one_way_arrow_opposite</c>,
        /// <c>waterway_line_label</c>, <c>water_name_line_label</c>, <c>highway-name-path</c>,
        /// <c>highway-name-minor</c>, <c>highway-name-major</c>) set no <c>text-translate</c> /
        /// <c>icon-translate</c> / <c>text-translate-anchor</c>, so the delta is exactly <c>float2.zero</c> and
        /// <c>0 · k == 0</c>.</para>
        ///
        /// <para><b>This tooth is the answer to "which test goes RED if this stops being deliberate?"</b>
        /// A recorded limitation needs a tooth that observes it. Without one the scaling is an unobserved
        /// path, and this epic has already shipped one of those.</para>
        ///
        /// <para><b>Method: a DIFFERENCE, so the corner geometry cancels.</b> The same map-pitched symbol is
        /// emitted twice, once with a translate and once without, and the per-corner offset difference must
        /// have the MAGNITUDE of the delta times the corner unit, component by component.</para>
        ///
        /// <para><b>Component MAGNITUDES, not signed values — and that is a scope statement, not a
        /// weakening.</b> The Y sense of a translate is set by <c>SymbolTranslate.ApplyTranslate</c>'s own
        /// convention composed with <c>BuildWorldQuad</c>'s A0-F2 negation, both of which predate W2 and
        /// neither of which this stage touches; measured, the two compose to a POSITIVE Y here. W2's claim is
        /// exclusively about the UNIT, and the unit is what the magnitude states. Re-pinning the sign would
        /// duplicate a convention that already has its own owners, and would make this tooth go RED for a
        /// reason that has nothing to do with the corner unit. The discrimination is unaffected: injection I6
        /// leaves a delta of 7 and 11 raw PIXELS where this expects 2140 and 3363 METRES — a factor of ~306,
        /// against a 1 % bound.</para>
        ///
        /// <para>RED recipe: injection I6 — pass <c>emit.TranslateDeltaPx</c> unscaled (mixed units in one
        /// <c>off</c>). Everything else stays GREEN.</para>
        /// </summary>
        [Test]
        public void MapPitchedTranslate_MovesInMetres_NotPixels()
        {
            var translatePx = new float2(7f, 11f);
            WorldBillboardVertex[] plain      = EmitAndRead(AlignmentMode.Map, float2.zero, out _, out _);
            WorldBillboardVertex[] translated = EmitAndRead(AlignmentMode.Map, translatePx,
                out _, out double metresPerLogicalPixel);

            // Component MAGNITUDES — the Y sense belongs to ApplyTranslate ∘ A0-F2, not to W2 (see the doc).
            var expected = new float2(
                (float)(translatePx.x * metresPerLogicalPixel),
                (float)(translatePx.y * metresPerLogicalPixel));

            for (int c = 0; c < 4; c++)
            {
                float2 signed = translated[c].Offset - plain[c].Offset;
                float2 delta = math.abs(signed);
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "W2-T8  vertex {0}  delta=({1:F3}, {2:F3}) m  |delta|=({3:F3}, {4:F3})  " +
                    "expected magnitude=({5:F3}, {6:F3}) m  (translatePx=({7:F1}, {8:F1}) × mppLogical={9:F4})",
                    c, signed.x, signed.y, delta.x, delta.y, expected.x, expected.y,
                    translatePx.x, translatePx.y, metresPerLogicalPixel));

                Assert.That(math.length(delta - expected), Is.LessThan(0.01 * math.length(expected)),
                    $"W2-T8 (vertex {c}): under map pitch the text-translate delta must ride the corners in " +
                    $"METRES — measured magnitude ({delta.x:F3}, {delta.y:F3}), expected ({expected.x:F3}, " +
                    $"{expected.y:F3}) = |translatePx| × MetresPerLogicalPixel. A delta of the raw pixel " +
                    "magnitude means the corners and the translate are in DIFFERENT units inside one `off`, " +
                    "which the shader's single displacement path cannot represent. This behaviour is recorded " +
                    "as followUp F-W2-2 — a limitation, not a design position — and this tooth exists so it " +
                    "cannot rot into an unobserved path.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Harness
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Stages ONE curved symbol through the real <see cref="SymbolPlacementSystem"/> and reads its
        /// first glyph's four vertices back off the built slot mesh — the same production path
        /// <c>OffLookAtSymbolScene</c> measures through.</summary>
        private static WorldBillboardVertex[] EmitAndRead(
            AlignmentMode pitch, float2 translatePx, out SymbolQuad cell, out double metresPerLogicalPixel)
        {
            var sceneConfig = new TiltedGroundSceneConfig
            {
                TiltDegrees = 0.0, SizePx = SizePx,
                BackgroundColor = Color.white, LitAmbient = false,
            };

            TiltedGroundScene    scene  = null;
            GlyphAtlasTexture    atlas  = null;
            SymbolPlacementSystem system = null;
            TestSymbolPlan       plan   = null;
            try
            {
                scene = TiltedGroundScene.Create(sceneConfig);
                (atlas, cell) = WorldCurvedAbRenderSnapshotTests.BuildGlyphF();
                metresPerLogicalPixel = scene.MetresPerDevicePixel * sceneConfig.DevicePixelRatio;

                SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
                double3 origin = frame.SceneOriginRender;
                double halfLen = 200.0 * scene.MetresPerDevicePixel; // no metre literals — see TiltFixtureSelfTests
                var dir = new double3(1.0, 0.0, 0.0);
                var up  = new double3(0.0, 1.0, 0.0);
                long tileKey = TestTileKeys.PackedContaining(sceneConfig.LookAt.Surface, zoom: 14);

                var buffer = new SymbolTileBuffer();
                var glyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = cell } };
                TestSymbolTileBuffer.AddCurved(buffer, glyphs, new[] { new LineAnchor(0, 0.5f) },
                    new[] { origin - dir * halfLen, origin + dir * halfLen }, new[] { up, up },
                    placement: SymbolPlacement.LineCenter,
                    up: up,
                    pitchAlignment: pitch,
                    paint: SymbolPaint.Default,
                    translatePx: translatePx,
                    text: "T",
                    textSizePx: TextSizePx,
                    maxAngleDeg: 180f,
                    keepUpright: false,
                    allowOverlap: true,
                    featureIndex: 0,
                    tileKey: tileKey);

                system = new SymbolPlacementSystem(scene.MapCam,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                plan = new TestSymbolPlan(scene.MapCam.Projection);

                // R3: duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlas);
                system.Tick(in frame, plan.Build(buffer), atlas);
                Assert.That(system.LastQuadCount, Is.EqualTo(1),
                    $"W2-T7/T8 precondition ({pitch}): the label must stage exactly one quad, got " +
                    $"{system.LastQuadCount}.");

                Assert.That(system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh), Is.True,
                    "W2-T7/T8 precondition: the label emitted no world slot mesh.");
                WorldMeshReadback.Read(mesh, out WorldBillboardVertex[] vertices, out _);
                Assert.That(vertices.Length, Is.EqualTo(4),
                    $"W2-T7/T8 precondition: expected exactly one quad's 4 vertices, got {vertices.Length}.");
                return vertices;
            }
            finally
            {
                plan?.Dispose();
                system?.Dispose();
                atlas?.Dispose();
                scene?.Dispose();
            }
        }

        /// <summary>Compares two floats by their raw 32-bit patterns — the only comparison that can state
        /// "bit-for-bit unchanged" rather than "close".</summary>
        private static void AssertBitwise(float expected, float actual, string what)
        {
            uint e = math.asuint(expected), a = math.asuint(actual);
            Assert.That(a, Is.EqualTo(e),
                $"{what}: 0x{a:X8} against 0x{e:X8} ({actual} vs {expected}). W2's invariant is that the " +
                "non-map path is BIT-identical to the pre-W2 one — cornerScale is the literal 1f there and " +
                "`x * 1f` is bitwise identity, so any difference at all is a real behaviour change.");
        }
    }
}
