// Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL
// SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader. NOT registered in
// Tools/core-tests/core-tests.csproj (it renders).
//
// Stage W2 — THE TILT-ZERO CALIBRATION ARM (W2-T4, T5, T9).
//
// WHY TILT 0 IS THE ONLY POSE THAT CAN SAY THIS. At tilt 0 the ground plane is perpendicular to the view
// axis, so every ground point shares ONE view depth d, and `MetresPerLogicalPixel` is BY DEFINITION the
// metres-per-logical-pixel ruler at d. A map-pitched corner displaced by `cornerPx · mppLogical` METRES
// therefore projects to exactly `cornerPx` LOGICAL PIXELS — which is precisely what the viewport branch adds
// to clip.xy after projection. So at tilt 0 a map-pitched curved symbol and a viewport-pitched twin must
// render PIXEL-IDENTICALLY (up to AA).
//
// That single identity carries three things at once, and each is a separate tooth below:
//   • the ABSOLUTE SCALE (T4a) — every ratio tooth in MapPitchedGlyphSizeTests is blind to a uniform scale
//     error `k`, because `k` cancels in a quotient of two lengths. Only an absolute comparison can see it,
//     and this is the only pose where an absolute comparison has a reference that shares no code with the
//     arm under test.
//   • the DPR factor (T4c) — an omitted or duplicated `DevicePixelRatio` shows up here as a ×2 and nowhere
//     else. P3a's T4b was this tooth, and it is the one that actually pinned it.
//   • the SIGN of the ground frame's ŷ (T4b/T4d = W2-T5) — SYMBOL_WORLD_MAP_Y_SIGN is this stage's one
//     un-derived constant. It was MEASURED, by gating both values on one tree; see the W2 dev report.
//
// THE REFERENCE IS THE VIEWPORT ARM, AND THAT IS NOT AN ACCIDENT. It shares NO code with the map branch:
// `SymbolWorldIsMapPitched` sends the two down mutually exclusive paths in the vertex stage. P3a's rebuilt-T2
// is the recorded lesson — a reference drawn from the arm under test cancels the very defect it is meant to
// expose. The two symbols here differ in EXACTLY ONE FIELD, `ShapedSymbol.PitchAlignment`.
//
// WHY 45° AND 90° ARE SWEPT AND 0° ALONE WOULD BE VACUOUS FOR THE SIGN. A road at 0° is horizontal on
// screen, and the 'F' cell's displacement about its anchor is then symmetric under the mirror the sign
// controls — P3b measured a FALSE AGREEMENT at 0° and 22.56 px of disagreement at 45°/90° against a 1.0 px
// bound. A sign constant must be read where the code is not inert.
//
// WHY THE CENTROID IS LEGITIMATE HERE, AND ONLY HERE. At tilt 0 the projection RESTRICTED TO THE GROUND
// PLANE is affine, so it commutes with the centroid and an ink centroid is a faithful position reading. It
// is NOT under tilt: P3a measured a 12.41 px convexity gap the moment tilt was non-zero. No tooth outside
// this file takes a centroid.
//
// NO METRE LITERALS: every world length here is a multiple of `scene.MetresPerDevicePixel`, the same rule
// TiltFixtureSelfTests and OffLookAtSymbolScene enforce — a bare metre literal is sub-pixel at this pose.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
using MapRenderer.Tests.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Visual
{
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

        /// <summary>Ink-centroid agreement bound, in pixels. Same reasoning as above; P3b's analogue read
        /// 22.56 px for a flipped sign against exactly this bound, so the discrimination margin is ~22×.</summary>
        private const double InkCentroidTolerancePx = 1.0;

        /// <summary>An ink reading below this is not a rendered glyph — a blank frame, a GPU-context failure
        /// or a quad collapsed to a point would otherwise pass an agreement test by agreeing about nothing.
        /// Asserted on BOTH arms of every cell.</summary>
        private const int InkFloor = 500;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T4a / T4c / T4d — ink COUNT: the ONLY clause that can see a uniform scale error
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T4a (DPR 1) + W2-T4c (DPR 2) + W2-T4d (45°/90°) — the absolute-scale clause.</b> Proves: at
        /// tilt 0 a map-pitched curved symbol covers the same number of ink pixels as its viewport-pitched
        /// twin, at both device-pixel ratios and at three road angles.
        ///
        /// <para><b>This is the only tooth in W2 that is not blind to a uniform scale error.</b>
        /// <c>MapPitchedGlyphSizeTests</c>' W2-T1/T3(b)/T10 all read a QUOTIENT of two lengths, in which
        /// <c>arcScale</c>, <c>TextSizePx</c>, <c>MetresPerLogicalPixel</c>, DPR and <c>OneEm</c> cancel
        /// identically — that cancellation is what makes them depth-independent, and it is also what makes
        /// them unable to see a factor `k` applied to everything. The count reads such a `k` as `k²`.</para>
        ///
        /// <para><b>The DPR cell (T4c) is where an omitted DevicePixelRatio shows.</b> The scene's world
        /// geometry is DPR-invariant (the orbit radius and <c>MetresPerDevicePixel</c> halve together, so
        /// <c>MetresPerLogicalPixel</c> is the same number of metres at both ratios), and both arms then land
        /// on twice as many DEVICE pixels at DPR 2. A production ruler that dropped the ratio — i.e. used
        /// metres-per-DEVICE-pixel — would halve the map arm's world size at DPR 2 while the viewport arm,
        /// which divides by <c>_ScreenParamsLogical</c>, would not move. P3a's injection 3 was exactly this
        /// shape.</para>
        ///
        /// <para><b>Blind to a mirror, by construction — predict that, do not discover it.</b> A mirror is an
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
        // W2-T4b / T4c / T4d = W2-T5 — ink CENTROID: the mirror/translation clause, and the SIGN
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T4b (DPR 1) + W2-T4c (DPR 2) + W2-T4d = W2-T5 (45°/90°) — the mirror/translation clause.</b>
        /// Proves: at tilt 0 the map-pitched glyph's ink CENTROID sits within
        /// <see cref="InkCentroidTolerancePx"/> of its viewport twin's, at both ratios and all three angles.
        ///
        /// <para><b>W2-T5 lives here.</b> <c>SYMBOL_WORLD_MAP_Y_SIGN</c> is the stage's one constant that is
        /// not derivable on paper: <c>WorldBillboardVertex.Offset</c> arrives in a y-DOWN frame (the A0-F2
        /// negation in <c>BillboardMath.BuildWorldQuad</c>), and that class's own doc states outright that the
        /// convention must not be re-derived on paper because the previous paper reading was
        /// self-contradictory. So which of <c>±cross(upWS, x̂)</c> is "downward on screen" was MEASURED — both
        /// values gated on one tree, the readings recorded in the W2 dev report, and the one matching the
        /// viewport arm kept.</para>
        ///
        /// <para><b>The 45° and 90° cells are the discriminating ones; 0° cannot see a mirror across the road
        /// axis.</b> At 0° the road is screen-horizontal and the flip the sign controls maps the cell onto a
        /// near-symmetric image of itself. P3b read a false agreement at 0° and 22.56 px at 45°/90° against
        /// this same 1.0 px bound — a sign constant must be read where the code is not inert. 0° is retained as
        /// the control, labelled as such — it is what says the pair agrees at all before the sign is asked
        /// about.</para>
        ///
        /// <para><b>Why a centroid is sound at this pose and at no other.</b> Restricted to the ground plane
        /// at tilt 0 the projection is AFFINE, so it commutes with the centroid. Under tilt it does not:
        /// P3a measured a 12.41 px convexity gap. Nothing else in W2 reads a centroid.</para>
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
        // W2-T9 — the degenerate frame degrades in METRES, not into a pixel formula
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T9 — the degenerate-frame branch is observed, not merely written.</b> Proves: a map-pitched
        /// curved symbol whose per-vertex <c>Up</c> is <see cref="float3.zero"/> still renders, and at tilt 0
        /// with a screen-horizontal road it renders as its viewport twin does — i.e. it took
        /// <c>SymbolWorldMapPitchClip</c>'s camera-facing METRE fallback, and did NOT reinterpret its metre
        /// offsets as pixels.
        ///
        /// <para><b>The P2 hazard this exists for.</b> Roughly ten older fixtures write <c>float3.zero</c> for
        /// <c>Up</c>, and both <c>SymbolTileBlockBaker</c> and the parity oracle do so whenever
        /// <c>PathUpRender</c> is null. Those feed a zero-length normal straight into a tangent-frame
        /// construction. Without this tooth the fallback is an unobserved branch, and this epic has already
        /// shipped one of those (<c>round-caps-never-rendered</c>: a <c>normalize(0)</c> NaN that made round
        /// caps never render at all, undetected).</para>
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
        /// ground frame, so exactly one of this tooth and W2-T4b could ever be green, whichever sign was
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
        /// <b>W2-T9, second clause (a separate <c>[Test]</c> — NUnit throws on the first failure, so a
        /// multi-clause method never executes its discriminator; that has already cost this epic two teeth).</b>
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
        /// (<see cref="AlignmentMode.Auto"/>, which resolves to the pre-W2 screen path) — and returns both
        /// ink signatures.
        ///
        /// <para><b>The pair differs in EXACTLY ONE FIELD.</b> Same tile key, same feature index, same glyph
        /// cell, same <c>TextSizePx</c>, same road, same camera, same frame. Everything a scale error could
        /// hide behind is shared, so what survives the comparison is the branch itself.</para>
        ///
        /// <para>Both arms are rendered from one <see cref="SymbolPlacementSystem"/>, re-Ticked between them —
        /// the same two-pass discipline <c>OffLookAtSymbolScene</c> uses, and for the same reason: one camera
        /// means one projection, so the two frames are comparable by construction rather than by assumption.
        /// Each Tick is duplicated because the collision verdict is harvested one Tick late (R3).</para>
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

                // P2's per-vertex surface normal. This scene is Web-Mercator, so up IS (0,1,0) — except on
                // the W2-T9 arm, which deliberately feeds the float3.zero that the older fixtures and the
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
                // P3a's recorded lesson: at coarse zoom the dedup/collision machinery decides who emits and a
                // fixture silently loses its symbol.
                allowOverlap: true,
                featureIndex: 0,
                tileKey: tileKey);

            // R3: duplicate Tick — the collision verdict is harvested one Tick late.
            system.Tick(in frame, plan.Build(buffer), atlas);
            system.Tick(in frame, plan.Build(buffer), atlas);
            Assert.That(system.LastQuadCount, Is.EqualTo(1),
                $"W2-T4 precondition ({armName}): the label must stage exactly one quad, got " +
                $"{system.LastQuadCount}. A zero means it spilled its road (StageCurved's centerArc ± halfSpan " +
                "gate) or was culled — nothing measured downstream would mean anything.");

            scene.Render(snapshot);
            var pixels = (byte[])snapshot.RawPixels.Clone();
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
#endif
