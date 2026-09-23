// Pitched-camera fixture-harness self-validation tests.
//
// Both members validate the TEST FIXTURE's own geometry (the tilted-ground ruler, the
// off-look-at scene) against a closed-form reference, before any production code is
// asserted against it.
//
// Contents:
//   TiltFixtureSelfTests         — Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture), off-screen GPU render + CPU readback.
//   OffLookAtSymbolFixtureTests  — Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real SymbolPlacementSystem.Tick), off-screen GPU render + CPU readback.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Text.Placement;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using System.Globalization;

namespace MapRenderer.Tests.Visual
{
    // Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture), off-screen GPU render +
    // CPU readback.
    // NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // The shared tilt-measurement fixture's OWN acceptance teeth, and its two in-stage consumers:
    //   T1 — the ruler itself (no GPU): TiltedGroundScene / GroundRuler foreshorten by cos(tilt) and agree with
    //        the closed form at the look-at.
    //   T2 — a rendered line band's apparent width matches the ruler under tilt (docs/line-rendering-design.md
    //        world-width model). SAME pose as the line-probe symmetry fixture (TiltedGroundSceneConfig's defaults,
    //        by design) — what is new here is the FIXTURE (an east–west road, world-metre width, measured by a
    //        column coverage integral), not the camera pose.
    //   T3 — the general-direction ray-cut primitive (PixelCoverage.CoverageProfileAlongRay /
    //        HalfCrossingDistancePx) resolves the three join types' silhouette reach under tilt — the
    //        measurement `docs/line-rendering-design.md`'s grazing-incidence question will need, though THIS
    //        tooth measures at the look-at, not grazing.
    //   T4 — a REAL production-path symbol (SymbolPlacementSystem.Tick) does not foreshorten under tilt today
    //        (viewport-pitch-aligned), and — the point of building this at all — the fixture can tell that
    //        apart from what a MAP-aligned symbol would read. Wired before `pitch-alignment: map` existed.
    //
    // ONE FILE, BOTH CONSUMERS ON PURPOSE: splitting it would hide the very thing these teeth demonstrate
    // — TiltedGroundScene is CONTENT-AGNOSTIC and serves a line/join consumer and a symbol consumer
    // from the SAME harness. T4's glyph/SymbolPlacementSystem arm lives here, under Visual/, rather than under
    // Text/Placement/, for exactly that reason: it is a TILT-FIXTURE tooth first, a symbol tooth second.
    //
    // COMMON TO ALL FOUR: the scene renders at 55°, the value two green fixtures (LineProbeSymmetrySnapshotTests,
    // LineDashSnapshotTests) already use. NO tooth here asserts an absolute pixel count — every assertion is
    // ratio-to-oracle (measured / GroundRuler projection ≈ 1) or ratio-across-tilts (q(55°) / q(0°) against a
    // derivation that is not the render) — docs/line-rendering-design.md records why "the band is exactly
    // N device pixels" is the wrong assertion at this pose.
    //
    // NO METRE LITERALS: every world size here is `k · scene.MetresPerDevicePixel` — a bare metre literal is
    // sub-pixel at this pose (zoom 8 / lat 30 puts one device px at ~300 m) and would render as nothing.
    //
    // THE MANDATED INJECTION: forcing TiltDegrees = 0 inside TiltedGroundScene.Create must
    // turn every "reads 1.000 if the mechanism is absent" clause below RED. T1(a) and T1(c) are CONTROLS and are
    // expected to stay green under that injection.

    // ───────────────────────────────────────────────────────────────────────────────────
    // TiltFixtureSelfTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TiltFixtureSelfTests
    {
        private const double TiltDeg = 55.0;

        /// <summary>Builds a throwaway, unrendered scene (any tilt — <c>MetresPerDevicePixel</c> is
        /// tilt-invariant, which is exactly T1(a)'s own claim) just to read the frame's ruler, so a fixture
        /// can size itself in device px before building any world geometry.</summary>
        private static double ProbeMetresPerDevicePixel()
        {
            using var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { LitAmbient = false });
            return scene.MetresPerDevicePixel;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T1 — the ruler itself. No GPU: WorldToScreenPoint uses the camera's projection matrix, no draw.
        //
        // FIVE independent [Test] methods, not one. NUnit's Assert.That throws on first failure, so a
        // single method running clauses in sequence lets an early clause's exception SHADOW every later
        // clause: under the mandated tilt=0 injection the convergence loop would throw immediately and the
        // remaining clauses would never execute, leaving their "goes red under injection" claim inferred
        // rather than demonstrated. Splitting makes every clause independently RED-verifiable forever
        // — a future injection (or a future clause added at the top) cannot shadow anything, because NUnit
        // runs each [Test] method regardless of whether a sibling method failed.
        //
        // T1AreaSpan(mpp) is the one non-control span length shared by (b)/(c)/the closed-form check —
        // 30·mpp, chosen from the convergence measurement below (~0.08% predicted deviation from the closed
        // form, a >12× margin under this file's 1% bound). See Ruler_ForeshorteningDeviationFromClosedForm_
        // ConvergesAsSpanShrinks's XML doc for the full derivation of why 120·mpp (this design's original
        // choice) was too coarse.
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        private const double T1SpanMultiplier = 30.0;

        /// <summary>
        /// <b>T1 — CONTROL.</b> Proves: <see cref="MapCamera.MetresPerDevicePixel"/> does not depend on
        /// tilt (the orbit radius it is built from does not). Does NOT prove anything about foreshortening
        /// itself — that is <see cref="Ruler_ForeshortensByCosTilt_AlongTheTiltAxis"/>'s job.
        ///
        /// <para>Expected to STAY GREEN under the mandated tilt=0 injection: both scenes would then read the SAME
        /// mpp, trivially.</para>
        /// </summary>
        [Test]
        public void Ruler_MetresPerDevicePixel_IsTiltInvariant()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double mpp0  = scene0.MetresPerDevicePixel;
            double mpp55 = scene55.MetresPerDevicePixel;

            Assert.That(math.abs(mpp55 / mpp0 - 1.0), Is.LessThan(1e-9),
                $"T1 (a): MetresPerDevicePixel must be tilt-invariant (the orbit radius is) — " +
                $"mpp(0°)={mpp0:R}, mpp(55°)={mpp55:R}.");
        }

        /// <summary>
        /// <b>T1 — CONVERGENCE PROOF</b> that the closed form <see cref="GroundRuler.ClosedFormAcrossAzimuthSpanPx"/>
        /// is a SMALL-SPAN limit, and that <see cref="T1SpanMultiplier"/>·mpp (the span every other T1 tooth
        /// below uses) sits safely inside it — a permanent, asserted property of the harness, not a one-off
        /// diagnostic.
        ///
        /// <para><b>Why this exists at all — L was shrunk from this design's original 120·mpp,
        /// not the tolerance widened.</b> The closed form <c>L/mpp·cosθ</c> is the INFINITESIMAL-span limit
        /// of the exact perspective divide; across a FINITE span the near and far ends sit at different
        /// depths, and the divide contributes a second-order-in-L correction. Solving the pinhole geometry
        /// exactly for a segment of length L centred at the look-at gives
        /// <c>span(θ,L)/span(0,L) = cosθ / (1 − (L/2d)²sin²θ)</c> (d = orbit radius =
        /// <c>|CameraRelativePosition|</c>) — i.e. the closed form is exact only as L→0, and the gap from
        /// cosθ grows as L². At L=120·mpp that gap measures 1.24% — OUTSIDE this file's 1% bound, wider than
        /// this design assumed — so L moved to <see cref="T1SpanMultiplier"/>=30·mpp (~0.08%
        /// measured), not the tolerance.</para>
        ///
        /// <para>Proves: the deviation from cosθ shrinks (roughly quarters per halving of L) as L shrinks —
        /// the falsifiable difference between diagnosing a real second-order term and tuning until green.
        /// Does NOT by itself prove foreshortening exists (a flat 0% at every L would also "converge" in the
        /// sense of not growing) — <see cref="Ruler_ForeshortensByCosTilt_AlongTheTiltAxis"/> is the
        /// DISCRIMINATOR for that, and it is a SEPARATE method so this one's failure cannot hide
        /// it.</para>
        /// </summary>
        [Test]
        public void Ruler_ForeshorteningDeviationFromClosedForm_ConvergesAsSpanShrinks()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double mpp0  = scene0.MetresPerDevicePixel;
            double cos55 = Angle.FromDegrees(TiltDeg).Cos;

            int[] convergenceKs = { 120, 60, 30, 15 };
            double[] convergenceDeviationPercent = new double[convergenceKs.Length];
            for (int i = 0; i < convergenceKs.Length; i++)
            {
                double Lk = convergenceKs[i] * mpp0;
                double spanZ55k = GroundRuler.GroundSegmentSpanPx(
                    scene55.UnityCamera, double3.zero, new double2(0.0, 1.0), Lk);
                double spanZ0k = GroundRuler.GroundSegmentSpanPx(
                    scene0.UnityCamera, double3.zero, new double2(0.0, 1.0), Lk);
                double ratioK = spanZ55k / spanZ0k;
                convergenceDeviationPercent[i] = 100.0 * math.abs(ratioK / cos55 - 1.0);
                TestContext.WriteLine(
                    $"T1 convergence: L={convergenceKs[i]}·mpp ({Lk:F1} m): span55={spanZ55k:F4}px, " +
                    $"span0={spanZ0k:F4}px, ratio={ratioK:F6}, deviation from cos55°=" +
                    $"{convergenceDeviationPercent[i]:F4}%");
            }
            for (int i = 1; i < convergenceKs.Length; i++)
            {
                Assert.That(convergenceDeviationPercent[i], Is.LessThan(convergenceDeviationPercent[i - 1]),
                    $"T1 CONVERGENCE: deviation from cos55° must shrink as L shrinks — at L=" +
                    $"{convergenceKs[i]}·mpp it read {convergenceDeviationPercent[i]:F4}%, at L=" +
                    $"{convergenceKs[i - 1]}·mpp it read {convergenceDeviationPercent[i - 1]:F4}%. A flat or " +
                    "growing deviation as L shrinks would mean the second-order-term diagnosis above is " +
                    "WRONG, not that a smaller L is safe to use.");
            }
        }

        /// <summary>
        /// <b>T1 — DISCRIMINATOR.</b> Proves: the projective ruler
        /// (<see cref="GroundRuler.GroundSegmentSpanPx"/>) foreshortens by cos(tilt) along the tilt axis
        /// (world ẑ, heading 0) at <see cref="T1SpanMultiplier"/>·mpp. Does NOT prove anything about
        /// rendering, any shader, any material — nothing here touches a render.
        ///
        /// <para>READS 1.000 (not cos 55° = 0.5736) if the harness is silently at tilt 0 — a factor of
        /// 1.744.</para>
        /// </summary>
        [Test]
        public void Ruler_ForeshortensByCosTilt_AlongTheTiltAxis()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double L = T1SpanMultiplier * scene0.MetresPerDevicePixel;
            double cos55 = Angle.FromDegrees(TiltDeg).Cos;

            double spanZ55 = GroundRuler.GroundSegmentSpanPx(
                scene55.UnityCamera, double3.zero, new double2(0.0, 1.0), L);
            double spanZ0 = GroundRuler.GroundSegmentSpanPx(
                scene0.UnityCamera, double3.zero, new double2(0.0, 1.0), L);
            double ratioZ = spanZ55 / spanZ0;
            Assert.That(ratioZ, Is.EqualTo(cos55).Within(1).Percent,
                $"T1 (b): the tilt-axis span must foreshorten by cos 55° = {cos55:F6}; measured " +
                $"{ratioZ:F6} (span55={spanZ55:F3}px, span0={spanZ0:F3}px). If this exceeds 1%, SHRINK " +
                $"{nameof(T1SpanMultiplier)} and report both numbers — do not widen the tolerance.");
        }

        /// <summary>
        /// <b>T1 — CONTROL.</b> Proves: the perpendicular (world x̂) span does NOT foreshorten — so
        /// <see cref="Ruler_ForeshortensByCosTilt_AlongTheTiltAxis"/> caught a genuine per-axis tilt effect,
        /// not a global scale change. Does NOT prove anything about the tilt axis itself.
        ///
        /// <para>Expected to STAY GREEN under the mandated tilt=0 injection: with no foreshortening on EITHER axis,
        /// this ratio trivially stays 1.</para>
        /// </summary>
        [Test]
        public void Ruler_DoesNotForeshorten_AlongThePerpendicularAxis()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double L = T1SpanMultiplier * scene0.MetresPerDevicePixel;

            double spanX55 = GroundRuler.GroundSegmentSpanPx(
                scene55.UnityCamera, double3.zero, new double2(1.0, 0.0), L);
            double spanX0 = GroundRuler.GroundSegmentSpanPx(
                scene0.UnityCamera, double3.zero, new double2(1.0, 0.0), L);
            double ratioX = spanX55 / spanX0;
            Assert.That(ratioX, Is.EqualTo(1.0).Within(1).Percent,
                $"T1 (c): the perpendicular (x̂) span must NOT foreshorten; measured {ratioX:F6}.");
        }

        /// <summary>
        /// <b>T1 — closed-form cross-check.</b> Proves the projective ruler agrees with the closed form
        /// at the look-at, at <see cref="T1SpanMultiplier"/>·mpp. Does NOT prove anything off the look-at —
        /// the closed form is valid only there, which is why every OTHER T tooth uses the projective ruler
        /// instead of this closed form.
        ///
        /// <para>The ONE T1 tooth without the camera-positioning blind spot the rest of T shares: this is
        /// bare trigonometry on <c>mpp</c> and <c>Angle</c> — it never calls
        /// <c>CameraPoseMath.ComputeRelativePose</c>, so it would PASS a bug in that pose math (a wrong
        /// camera position that still yields a self-consistent projective ruler). Every other T tooth reads
        /// the live camera and would catch such a bug; this one exists to catch drift in the closed form
        /// itself, not in the camera.</para>
        /// </summary>
        [Test]
        public void Ruler_AgreesWithTheClosedForm_AtTheLookAt()
        {
            using var scene0  = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = 0.0, LitAmbient = false });
            using var scene55 = TiltedGroundScene.Create(
                new TiltedGroundSceneConfig { TiltDegrees = TiltDeg, LitAmbient = false });

            double L = T1SpanMultiplier * scene0.MetresPerDevicePixel;
            double mpp55 = scene55.MetresPerDevicePixel;

            double spanZ55 = GroundRuler.GroundSegmentSpanPx(
                scene55.UnityCamera, double3.zero, new double2(0.0, 1.0), L);
            double closedForm55 = GroundRuler.ClosedFormAcrossAzimuthSpanPx(L, mpp55, Angle.FromDegrees(TiltDeg));
            Assert.That(spanZ55, Is.EqualTo(closedForm55).Within(1).Percent,
                $"T1: the projective ruler ({spanZ55:F3}px) must agree with the closed form " +
                $"({closedForm55:F3}px) at the look-at.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T2 — a rendered line band's apparent width matches the ruler under tilt.
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        // Derived from mpp, never a bare metre literal. 260·mpp / 6·mpp is the same order as the line-probe fixture's
        // east–west road (80,000 m / 2,000 m) at this file's default pose, but stays correct at any zoom.
        private const double T2RoadHalfLengthMultiplier = 260.0;
        private const double T2RoadStationMultiplier    =   6.0;
        private const int    T2SweepHalfWidth           =  10; // ±10 columns — see LineProbeSymmetrySnapshotTests'
                                                                 // TiltedSweepHalfWidth for why not wider under tilt.

        private readonly struct RoadBandMeasurement
        {
            public readonly double MeasuredWidthPx;
            public readonly double RulerWidthPx;
            public RoadBandMeasurement(double measuredWidthPx, double rulerWidthPx)
            {
                MeasuredWidthPx = measuredWidthPx;
                RulerWidthPx    = rulerWidthPx;
            }
        }

        /// <summary>Rendered band width in device px on one screen COLUMN: the coverage integral down the
        /// whole column. The ray/column analogue of <c>LineProbeSymmetrySnapshotTests.MeasureRowWidthPx</c> —
        /// that one cuts a NORTH–SOUTH (receding) road with a horizontal row; this cuts an EAST–WEST road
        /// with a vertical column.</summary>
        private static double MeasureColumnWidthPx(Frame frame, int column, float3 background)
        {
            float3 plateau = background;
            float  best    = 0f;
            for (int row = 0; row < frame.Height; row++)
            {
                float3 sample = PixelCoverage.SampleLinear(frame, column, row);
                float  dist   = math.distancesq(sample, background);
                if (dist > best) { best = dist; plateau = sample; }
            }
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"T2 column {column}: nothing distinguishable from the background — the road did not " +
                "render here, so no width is measurable.");

            double sum = 0.0;
            for (int row = 0; row < frame.Height; row++)
                sum += PixelCoverage.CoverageAt(frame, column, row, background, plateau);
            return sum;
        }

        /// <summary>Renders an east–west road (world ±X at z = 0) with a WORLD-metre width — its across-axis
        /// is world ±Z, the foreshortened direction at heading 0 — and measures the apparent band width as
        /// the coverage-integral-per-column, averaged over the centre ±<see cref="T2SweepHalfWidth"/>
        /// columns. Returns that measurement alongside the projective ruler's prediction for the SAME world
        /// width, on the SAME camera.</summary>
        private static RoadBandMeasurement MeasureRoadBand(double tiltDeg, double worldWidthM)
        {
            using var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { TiltDegrees = tiltDeg });
            using var snap  = new SnapshotRenderer(scene.Config.SizePx, scene.Config.SizePx);

            double mpp = scene.MetresPerDevicePixel;
            double roadHalfLengthM = T2RoadHalfLengthMultiplier * mpp;
            double roadStationM    = T2RoadStationMultiplier * mpp;

            using var bag = new ObjectDisposalBag();
            var pts = new List<double2>();
            for (double x = -roadHalfLengthM; x <= roadHalfLengthM + 1e-6; x += roadStationM)
                pts.Add(new double2(x, 0.0));
            Mesh mesh = bag.Track(SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt));

            var shader = Shader.Find("Map/Line");
            Assert.IsNotNull(shader, "Map/Line shader must be present — T2 measures ITS coverage.");
            var mat = bag.Track(new Material(shader) { name = "T2RoadFixtureMat" });
            mat.SetFloat(ShaderProperties.Line.PropertyId.Width, (float)worldWidthM);
            mat.SetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels, 0f);
            mat.SetColor(ShaderProperties.PropertyId.BaseColor, new Color(0.95f, 0.60f, 0.15f, 1f));
            mat.SetFloat(ShaderProperties.PropertyId.Opacity, 1f);

            // Preconditions, asserted not assumed (LineProbeSymmetrySnapshotTests.cs:138–159 is the template).
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.DashCount), Is.EqualTo(0f),
                "T2 precondition: _DashCount must be 0 — a dash boundary would be summed as a ribbon edge.");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels), Is.EqualTo(0f),
                "T2 precondition: _WidthIsPixels must be 0 — the styled-PIXEL width model is deliberately " +
                "OUT of this tooth; it uses world metres so the no-literal rule is meaningful.");
            Assert.That(mat.IsKeywordEnabled("_EDGE_ANTIALIASING_OFF"), Is.False,
                "T2 precondition: the AA straddle must be live.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_SOLID_CORE"), Is.False,
                "T2 precondition: _HAIRLINE_SOLID_CORE must be off.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_HARD"), Is.False,
                "T2 precondition: _HAIRLINE_HARD must be off.");

            var go = bag.Track(new GameObject("T2RoadFixture"));
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            {
                scene.Render(snap);

                // The road's centreline must project onto a single screen ROW (heading 0), or a silhouette
                // screen-y does not invert to a single world z — LineProbeSymmetrySnapshotTests.cs:243–247.
                Vector3 originSp = scene.UnityCamera.WorldToScreenPoint(Vector3.zero);
                Vector3 refSp    = scene.UnityCamera.WorldToScreenPoint(new Vector3(10_000f, 0f, 0f));
                Assert.That(originSp.y, Is.EqualTo(refSp.y).Within(0.05),
                    "T2 precondition: the road's centreline must project onto a single screen ROW.");

                float3 background = PixelCoverage.BackgroundLinear(snap.Pixels);

                int centreColumn = scene.Config.SizePx / 2;
                double sum = 0.0;
                int    count = 0;
                for (int c = centreColumn - T2SweepHalfWidth; c <= centreColumn + T2SweepHalfWidth; c++)
                {
                    sum += MeasureColumnWidthPx(snap.Pixels, c, background);
                    count++;
                }
                double measuredWidthPx = sum / count;

                // SYMMETRIC about the centreline — GroundSegmentSpanPx, not a one-sided probe (that IS the
                // foreshortening defect).
                double rulerWidthPx = GroundRuler.GroundSegmentSpanPx(
                    scene.UnityCamera, double3.zero, new double2(0.0, 1.0), worldWidthM);

                return new RoadBandMeasurement(measuredWidthPx, rulerWidthPx);
            }
        }

        /// <summary>
        /// <b>T2.</b> Proves: a rendered band's apparent extent equals the projection of its intended WORLD
        /// extent under tilt, and the coverage-integral measurement is calibrated against the ruler.
        ///
        /// <para>Does NOT prove the styled-PIXEL width model — excluded by using world metres.
        /// Nothing at depths other than the look-at row. Nothing about a spherical projection: this scene is
        /// Web-Mercator and the ground is the plane y = 0.</para>
        /// </summary>
        [Test]
        public void RenderedBandWidth_MatchesTheRuler_UnderTilt()
        {
            double mpp     = ProbeMetresPerDevicePixel();
            double worldWidthM = 120.0 * mpp;

            RoadBandMeasurement m55 = MeasureRoadBand(TiltDeg, worldWidthM);
            RoadBandMeasurement m0  = MeasureRoadBand(0.0, worldWidthM);

            // (a) calibration. ≠ 1 if the coverage integral is not measuring the band's apparent width.
            double ratioCalibration = m55.MeasuredWidthPx / m55.RulerWidthPx;
            Assert.That(ratioCalibration, Is.EqualTo(1.0).Within(3).Percent,
                $"T2 (a) calibration: measured {m55.MeasuredWidthPx:F3}px vs ruler {m55.RulerWidthPx:F3}px " +
                $"at 55° (ratio {ratioCalibration:F4}).");

            // (b) DISCRIMINATION. Reads 1.000 at tilt 0 — (a) alone cannot see this, because the ruler
            // foreshortens WITH the render, so their ratio stays 1 regardless of tilt. This clause is what
            // makes T2 a tilt tooth.
            //
            // KNOWN SYSTEMATIC BIAS in the comparand, not in the render: cos55° is T1's INFINITESIMAL-span
            // limit (see T1's convergence tooth), but this band is 120·mpp wide — the exact L at which T1
            // measured a 1.2439% departure from cos55°. So the expected value here is ~1.24% low by
            // construction, consuming roughly 41% of this clause's 3% budget before any real measurement
            // error. A reading up to ~2% off is this bias, not a render defect — read the message's own
            // numbers, don't assume the tolerance is slack. (Not corrected here: the fix belongs to the
            // comparand — cos55°/(1-(W/2d)²sin²55°) — a decision for every T-family tooth at once, not a
            // per-tooth change.)
            double cos55 = Angle.FromDegrees(TiltDeg).Cos;
            double ratioTilt = m55.MeasuredWidthPx / m0.MeasuredWidthPx;
            Assert.That(ratioTilt, Is.EqualTo(cos55).Within(3).Percent,
                $"T2 (b) DISCRIMINATION: measured(55°)/measured(0°) must equal cos 55° = {cos55:F6}; " +
                $"got {ratioTilt:F6} (measured55={m55.MeasuredWidthPx:F3}px, " +
                $"measured0={m0.MeasuredWidthPx:F3}px). NOTE: cos55° is the L→0 limit and this band is " +
                "120·mpp wide, so the comparand itself is ~1.24% low by construction (see T1's convergence " +
                "tooth) — a reading in that neighbourhood is this known bias, not necessarily a defect.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T3 — the general-direction ray-cut primitive resolves the three join types' silhouette reach.
        // ═══════════════════════════════════════════════════════════════════════════════════════════

        private readonly struct JoinReachMeasurement
        {
            public readonly double ReachPx;
            public readonly double RulerPx;
            public JoinReachMeasurement(double reachPx, double rulerPx)
            {
                ReachPx = reachPx;
                RulerPx = rulerPx;
            }
        }

        /// <summary>Builds the apex-V fixture SHAPE <c>LineAaSnapshotTests.BuildApexFixture</c> established
        /// (that file's own test is NOT touched — only its shape is reused here as a T subject): apex at the
        /// world origin (the look-at), arms opening toward world −Z so the OUTWARD (convex) bisector points
        /// world +Z — the foreshortened direction under this scene's heading-0 tilt. Renders one join type on
        /// one tilt, marches <see cref="PixelCoverage.CoverageProfileAlongRay"/> from the apex along the
        /// bisector, and returns both the measured reach and the SAME-CAMERA ruler for
        /// <paramref name="analyticReachMetres"/> (a one-sided <see cref="GroundRuler.ScreenSpanPx"/> — the
        /// reach is measured one-sided FROM a fixed point, not the symmetric-about-centre quantity
        /// <see cref="GroundRuler.GroundSegmentSpanPx"/> models).</summary>
        private static JoinReachMeasurement MeasureJoinReach(
            JoinType join, double tiltDeg, double armM, double widthM, double analyticReachMetres)
        {
            using var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { TiltDegrees = tiltDeg });
            using var snap  = new SnapshotRenderer(scene.Config.SizePx, scene.Config.SizePx);

            var pts = new List<double2>
            {
                new double2( armM, -armM),
                new double2( 0.0,   0.0),
                new double2(-armM, -armM),
            };
            using var bag = new ObjectDisposalBag();
            Mesh mesh = bag.Track(SyntheticLineMesh.BuildFromPoints(pts, join, CapType.Butt));

            var shader = Shader.Find("Map/Line");
            Assert.IsNotNull(shader, "Map/Line shader must be present — T3 measures ITS silhouette.");
            var mat = bag.Track(new Material(shader) { name = "T3ApexFixtureMat" });
            mat.SetFloat(ShaderProperties.Line.PropertyId.Width, (float)widthM);
            mat.SetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels, 0f);
            mat.SetColor(ShaderProperties.PropertyId.BaseColor, new Color(0.95f, 0.60f, 0.15f, 1f));
            mat.SetFloat(ShaderProperties.PropertyId.Opacity, 1f);

            var go = bag.Track(new GameObject("T3ApexFixture"));
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            {
                scene.Render(snap);

                float3 background = PixelCoverage.BackgroundLinear(snap.Pixels);

                double2 originPx = GroundRuler.ProjectPx(scene.UnityCamera, double3.zero);
                // Baseline is armM, not a 1 m step: at this pose (d ~135km) a 1 m step differences two
                // ~1.35e5-magnitude floats whose ulp is ~0.03 m — a several-percent budget on the step
                // itself, which can tilt the resolved bisector direction by 1-2° and drift the marched reach
                // by a few px. armM (~100·mpp, tens of km) keeps the float precision budget negligible.
                double2 dirPx = GroundRuler.ScreenDirection(
                    scene.UnityCamera, double3.zero, new double3(0.0, 0.0, armM));

                const int    steps  = 200; // generous: max analytic reach here is ~85px (1.41421·60·mpp / mpp).
                const double stepPx = 1.0;

                float3 plateau = PixelCoverage.PlateauAlongRay(
                    snap.Pixels, originPx, dirPx, steps, stepPx, background);
                Assert.That(math.length(plateau - background), Is.GreaterThan(0.05f),
                    $"T3 {join} @ {tiltDeg}°: no covered sample found along the bisector ray — the fixture " +
                    "did not render where this tooth looks.");

                float[] profile = PixelCoverage.CoverageProfileAlongRay(
                    snap.Pixels, originPx, dirPx, steps, stepPx, background, plateau);
                Assert.That(profile[0], Is.GreaterThan(0.9f),
                    $"T3 {join} @ {tiltDeg}°: the apex sample itself must be covered (got {profile[0]:F3}) " +
                    "— the fixture is not where this tooth thinks it is.");

                double reachPx = PixelCoverage.HalfCrossingDistancePx(profile, stepPx);
                double rulerPx = GroundRuler.ScreenSpanPx(
                    scene.UnityCamera, double3.zero, new double3(0.0, 0.0, analyticReachMetres));

                return new JoinReachMeasurement(reachPx, rulerPx);
            }
        }

        /// <summary>
        /// <b>T3.</b> Proves: the general-direction ray cut is valid under tilt; the three join types stay
        /// separable at 55°; the harness serves a line-GEOMETRY consumer, not only a band-width one.
        ///
        /// <para>Does NOT prove anything at GRAZING incidence — this is the look-at at 55°, and grazing is
        /// the open question <c>docs/line-rendering-design.md</c> owns. Nothing about caps. Nothing
        /// about the short-segment fold régime — these arms are two orders above it.</para>
        /// </summary>
        [Test]
        public void JoinSilhouetteReach_MatchesTheRuler_UnderTilt()
        {
            double mpp    = ProbeMetresPerDevicePixel();
            double h      = 60.0 * mpp;
            double armM   = 100.0 * mpp;
            double widthM = 2.0 * h;

            var joins = new[] { JoinType.Miter, JoinType.Round, JoinType.Bevel };
            var analyticMultiplier = new Dictionary<JoinType, double>
            {
                [JoinType.Miter] = 1.41421,
                [JoinType.Round] = 1.00000,
                [JoinType.Bevel] = 0.70711,
            };

            var at55 = new Dictionary<JoinType, JoinReachMeasurement>();
            var at0  = new Dictionary<JoinType, JoinReachMeasurement>();
            foreach (JoinType join in joins)
            {
                double r = analyticMultiplier[join] * h;
                at55[join] = MeasureJoinReach(join, TiltDeg, armM, widthM, r);
                at0[join]  = MeasureJoinReach(join, 0.0,     armM, widthM, r);
            }

            // (a) calibration, per join. ≠ 1 if the ray cut is not measuring the silhouette. 6% (not T2's
            // 3%): the round join is a 4-segment chord approximation to the arc and the crossing estimator
            // is coarser than the coverage integral.
            foreach (JoinType join in joins)
            {
                double ratio = at55[join].ReachPx / at55[join].RulerPx;
                Assert.That(ratio, Is.EqualTo(1.0).Within(6).Percent,
                    $"T3 (a) {join}: reach(55°)={at55[join].ReachPx:F2}px, ruler={at55[join].RulerPx:F2}px, " +
                    $"ratio={ratio:F4}.");
            }

            // (b) DISCRIMINATION. At tilt 0 this ratio is 1.000 for every join.
            foreach (JoinType join in joins)
            {
                double bound = 0.75 * at0[join].ReachPx;
                Assert.That(at55[join].ReachPx, Is.LessThan(bound),
                    $"T3 (b) DISCRIMINATION {join}: reach(55°)={at55[join].ReachPx:F2}px must be < 0.75×" +
                    $"reach(0°)={bound:F2}px (reach(0°)={at0[join].ReachPx:F2}px).");
            }

            // (c) separability survives tilt — a hard floor, not a fudge: a join type degrading into another
            // must fail this however loose the absolute tolerances in (a) are.
            double hReachPx55 = at55[JoinType.Round].RulerPx; // round's analytic reach IS 1.0·h.
            double separabilityFloor = 0.15 * hReachPx55;
            Assert.That(at55[JoinType.Miter].ReachPx - at55[JoinType.Round].ReachPx, Is.GreaterThan(separabilityFloor),
                $"T3 (c): miter must out-reach round by more than {separabilityFloor:F2}px at 55° " +
                $"(miter {at55[JoinType.Miter].ReachPx:F2}px, round {at55[JoinType.Round].ReachPx:F2}px).");
            Assert.That(at55[JoinType.Round].ReachPx - at55[JoinType.Bevel].ReachPx, Is.GreaterThan(separabilityFloor),
                $"T3 (c): round must out-reach bevel by more than {separabilityFloor:F2}px at 55° " +
                $"(round {at55[JoinType.Round].ReachPx:F2}px, bevel {at55[JoinType.Bevel].ReachPx:F2}px).");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        // T4 — a REAL production-path symbol does not foreshorten under tilt, and the fixture can see that a
        // map-aligned one would. Wired before `pitch-alignment: map` existed.
        // ═══════════════════════════════════════════════════════════════════════════════════════════
        //
        // The one-glyph bootstrap is WorldPointEmitRenderTests.BuildGlyphA, widened private → internal there
        // (test-code-bloat convention: widen and reuse, never duplicate-and-drag) rather than copied here.

        private readonly struct SymbolArmResult
        {
            public readonly double  InkHeightPx;
            public readonly int     QuadCount;
            public readonly double2 AnchorScreenPx;
            public readonly double  MetresPerDevicePixel;
            public readonly double  MapAlignedExpectationPx;

            public SymbolArmResult(double inkHeightPx, int quadCount, double2 anchorScreenPx,
                double metresPerDevicePixel, double mapAlignedExpectationPx)
            {
                InkHeightPx              = inkHeightPx;
                QuadCount                = quadCount;
                AnchorScreenPx           = anchorScreenPx;
                MetresPerDevicePixel     = metresPerDevicePixel;
                MapAlignedExpectationPx  = mapAlignedExpectationPx;
            }
        }

        /// <summary>Renders the glyph 'A' through the REAL <c>SymbolPlacementSystem.Tick</c> — exactly
        /// <c>WorldPointEmitRenderTests.cs:104–116</c>'s recipe (double Tick: the collision verdict is
        /// harvested one Tick late) — anchored at <c>frame.SceneOriginRender</c> (the look-at itself, so it
        /// projects to screen centre at BOTH tilts and the two ink heights are comparable).
        ///
        /// <para>When <paramref name="inkHeight0PxForMapExpectation"/> is supplied (the 55° call), also
        /// computes the MAP-aligned expectation for a quad of that height: <c>GroundSegmentSpanPx</c> of a
        /// world-plane segment <c>inkHeight(0°) · mpp</c> metres tall, on THIS scene's own camera.</para></summary>
        private static SymbolArmResult RenderSymbolArm(
            double tiltDeg, GlyphAtlasTexture atlasTexture, List<SymbolQuad> quads, TextLayoutBounds bounds,
            double? inkHeight0PxForMapExpectation)
        {
            var config = new TiltedGroundSceneConfig
            {
                TiltDegrees     = tiltDeg,
                BackgroundColor = Color.white, // WorldSymbolInkAnalysis.InkThreshold reads dark ink on white.
                LitAmbient      = false,       // the symbol arm needs no lit recipe.
            };
            using var scene = TiltedGroundScene.Create(config);
            using var snap  = new SnapshotRenderer(config.SizePx, config.SizePx);

            SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
            double3 anchorRender = frame.SceneOriginRender; // the look-at, per T4's screen-centre requirement.

            long tileKey = TestTileKeys.PackedContaining(config.LookAt.Surface, zoom: 14);
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default, textSizePx: 220f, sortKey: 0f, featureIndex: 0, tileKey: tileKey);

            using var system = new SymbolPlacementSystem(scene.MapCam,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var plan = new TestSymbolPlan(scene.MapCam.Projection);
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                int quadCount = system.LastQuadCount;

                scene.Render(snap);
                Color32[] px = (Color32[])snap.Pixels.Pixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, config.SizePx, config.SizePx);
                WorldSymbolInkAnalysis.AnalyzeInk(px, config.SizePx, config.SizePx,
                    out int minRow, out int maxRow, out _, out _, out _, out _, out int inkCount);
                Assert.That(inkCount, Is.GreaterThan(50),
                    $"T4 @ {tiltDeg}°: must render meaningful ink (not blank/GPU-context-failed).");

                double inkHeightPx = maxRow - minRow + 1;

                // The oracle projects the CAMERA-RELATIVE point the render actually lands at (Unity world
                // origin — the look-at, under camera-relative rendering), NOT `anchorRender` itself, which is
                // the PRE-RTC absolute render-space coordinate the shader never sees directly (SceneFrame's
                // Rebase/RTC cancels it out — see WorldPointEmitRenderTests' NEW-F1 tooth for the proof that
                // this cancellation is exact regardless of which real tile the symbol nominally belongs to).
                double2 anchorScreenPx = GroundRuler.ProjectPx(scene.UnityCamera, double3.zero);
                double  mpp            = scene.MetresPerDevicePixel;

                double mapAlignedExpectationPx = 0.0;
                if (inkHeight0PxForMapExpectation.HasValue)
                {
                    double quadHeightWorld = inkHeight0PxForMapExpectation.Value * mpp;
                    mapAlignedExpectationPx = GroundRuler.GroundSegmentSpanPx(
                        scene.UnityCamera, double3.zero, new double2(0.0, 1.0), quadHeightWorld);
                }

                return new SymbolArmResult(inkHeightPx, quadCount, anchorScreenPx, mpp, mapAlignedExpectationPx);
            }
        }

        /// <summary>Half of <see cref="TiltedGroundSceneConfig.SizePx"/>'s default (512) — the frame centre
        /// in device px, for T4 (a)'s screen-centre precondition.</summary>
        private const double SymbolFrameCentrePx = 256.0;

        /// <summary>
        /// <b>T4.</b> Proves: the harness renders a PRODUCTION-PATH symbol at tilt, and resolves screen extent
        /// finely enough to separate a map-aligned expectation from a viewport-aligned measurement by ~74% —
        /// i.e. map pitch alignment has a real acceptance criterion.
        ///
        /// <para>Does NOT prove anything about <c>pitch-alignment: map</c>, which is UNIMPLEMENTED — clause
        /// (b) is a NEGATIVE control, expected to INVERT under map pitch alignment. Nothing about the spherical <c>Up</c>
        /// path: this scene is Web-Mercator (<c>Up</c> is the constant (0,1,0), <c>Rebase</c> is
        /// identity), so this arm cannot distinguish a correct per-anchor frame from a hard-coded one — that
        /// is <c>SymbolUpCarrierChainTests</c>' job, over <c>SphericalProjection</c>. Nothing about icons,
        /// collision or fade.</para>
        /// </summary>
        [Test]
        public void ViewportPitchAlignedSymbol_DoesNotForeshorten_AndTheFixtureCanSeeThatItWould()
        {
            (GlyphAtlasTexture atlasTexture, List<SymbolQuad> quads, TextLayoutBounds bounds) = WorldPointEmitRenderTests.BuildGlyphA();
            using var _ = atlasTexture;
            {
                SymbolArmResult r0  = RenderSymbolArm(0.0,     atlasTexture, quads, bounds, null);
                SymbolArmResult r55 = RenderSymbolArm(TiltDeg, atlasTexture, quads, bounds, r0.InkHeightPx);

                // (a) precondition. 0 ⇒ the symbol is culled under tilt → escalate.
                Assert.That(r55.QuadCount, Is.EqualTo(1),
                    "T4 (a): the label must not be culled at 55° — LastQuadCount == 0 here means the symbol " +
                    "is culled under tilt (STOP and report; do not lower the tilt or hand-build the mesh unreported).");
                double centreDistPx = math.length(
                    r55.AnchorScreenPx - new double2(SymbolFrameCentrePx, SymbolFrameCentrePx));
                Assert.That(centreDistPx, Is.LessThan(8.0),
                    $"T4 (a): the anchor must project within 8px of frame centre at 55° (measured " +
                    $"{centreDistPx:F2}px) — AnchorRender is the look-at exactly so the two tilts are " +
                    "screen-centre comparable.");

                // (b) today's behaviour: the current path is viewport-pitch-aligned, so it must not
                // foreshorten.
                double ratio = r55.InkHeightPx / r0.InkHeightPx;
                Assert.That(ratio, Is.EqualTo(1.0).Within(2).Percent,
                    $"T4 (b): inkHeight(55°)/inkHeight(0°) must be ≈1.000 (viewport-pitch-aligned does not " +
                    $"foreshorten); got {ratio:F4} (inkHeight55={r55.InkHeightPx}px, inkHeight0={r0.InkHeightPx}px).");

                // (c) THE FIXTURE CAN DISCRIMINATE. At tilt 0 the map expectation collapses onto the
                // measurement and this clause fails — the pitch-0-inertness guard map pitch alignment inherits.
                double deviation = math.abs(r55.MapAlignedExpectationPx - r55.InkHeightPx) / r55.InkHeightPx;
                Assert.That(deviation, Is.GreaterThan(0.30),
                    $"T4 (c) DISCRIMINATION: the map-ALIGNED expectation ({r55.MapAlignedExpectationPx:F2}px) " +
                    $"must differ from the measured viewport-aligned ink height ({r55.InkHeightPx:F2}px) by " +
                    $"more than 30% at 55° (measured {deviation:P1}), or the fixture cannot tell a map-aligned " +
                    "label from a viewport-aligned one once P3 lands.");
            }
        }
    }

    // Unity EditMode only — real OffLookAtSymbolScene (MapCamera + Camera/RenderTexture + a real
    // SymbolPlacementSystem.Tick), off-screen GPU render + CPU readback.
    // NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // The off-look-at fixture's OWN acceptance teeth (M1–M13). Read OffLookAtSymbolScene's header
    // first: it states what the fixture is, why the CROSS-AZIMUTH arm is the headline one and the RECEDING arm
    // is soundness-only, and why the headline ratio is immune to a uniform miscalibration.
    //
    // THIRTEEN INDEPENDENT [Test] METHODS, not one. NUnit's Assert.That throws on the FIRST failure, so a method
    // running clauses in sequence lets an early clause's exception SHADOW every later clause. Where a tooth checks
    // the same property at BOTH depths, it computes both readings first and asserts the WORSE one in a single
    // clause whose message carries both numbers, rather than asserting twice.
    //
    // THESE TEETH CALIBRATE THE INSTRUMENT; THEY DO NOT PIN THE DEFECT. Every one of M1–M13 is about the
    // frame geometry, the oracle, or a claim true under BOTH the screen walk and the world walk, so a change
    // of walk moves none of them. THE FAR ASSERTIONS THIS FILE DEFERS LIVE IN
    // `MapPitchedWorldArcLayoutTests` (T1…T5) — "far/near screen spacing == 0.500", the per-gap world-metre
    // reading, and the DPR tooth, with their RED verifications. M13 still PRINTS the whole table; its
    // `far measured/oracle` row reads ≈ 1.00.
    //
    // ANTI-TEETH, intentionally absent (writing any of them would be a defect):
    //   • any tooth asserting far/near spacing ≈ 1.0 — that PINS the old defect;
    //   • any tooth whose expected value is derived from the near symbol's MEASURED spacing scaled by anything —
    //     that re-imports the self-referential-oracle failure.
    //
    // THE RED-verifications named on the teeth BELOW are injections into the FIXTURE or the ORACLE, because
    // these teeth are about the apparatus. The world-arc teeth are RED-verified against PRODUCTION.

    // ───────────────────────────────────────────────────────────────────────────────────
    // OffLookAtSymbolFixtureTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class OffLookAtSymbolFixtureTests
    {
        private static OffLookAtSymbolScene CreateFixture()
            => OffLookAtSymbolScene.Create(new OffLookAtSymbolSceneConfig());

        /// <summary>Ink is separable from the white background at this threshold; a band containing a whole
        /// five-glyph symbol carries thousands of ink pixels, so this floor only asks "did anything render
        /// here at all".</summary>
        private const int InkFloor = 200;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M1–M5 — the frame geometry and the ORACLE itself. No mesh, no symbol, no render is read here.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M1.</b> Proves: the fixture's two anchors really do sit at the designed far/near VIEW-depth
        /// ratio — the property that makes every later reading a two-depth reading rather than the
        /// single-depth reading a look-at-anchored fixture takes. The far anchor is SOLVED (view depth
        /// is affine along ĝ, so two samples determine it exactly), then the achieved ratio is asserted.
        ///
        /// <para>Does NOT prove anything about symbols, staging, spacing or rendering.</para>
        ///
        /// <para>RED-verify: break the affine solve (drop the <c>/ depthSlope</c>, or scale <c>s_far</c>) ⇒ the
        /// achieved ratio no longer matches the configured one. NOT by setting
        /// <c>TargetDepthRatio = 1.0</c>: the second clause below compares the ACHIEVED ratio against the
        /// CONFIGURED one, so lowering the config moves expectation and measurement together and reads green —
        /// the plan's recipe for this tooth is vacuous, which is why the 1.8 floor clause exists.</para>
        /// </summary>
        [Test]
        public void TwoAnchors_SitAtTheDesignedViewDepthRatio()
        {
            using var f = CreateFixture();
            // The floor, not decoration: the whole stage exists to read the two competing rulers apart, and
            // below ~1.8 they are too close to. It is also what makes the clause below falsifiable by a
            // config change — stop rule 2 says to lower the ZOOM, never the ratio, so a config that shrank
            // the ratio to keep the far anchor on-screen must fail HERE.
            Assert.That(f.Config.TargetDepthRatio, Is.GreaterThanOrEqualTo(1.8),
                $"M1: the designed far/near view-depth ratio must be at least 1.8 — configured " +
                $"{f.Config.TargetDepthRatio:F3}. STOP RULE 2: if the far anchor will not fit on-screen, " +
                "lower the zoom and report; do not shrink this.");
            Assert.That(f.AchievedDepthRatio, Is.EqualTo(f.Config.TargetDepthRatio).Within(2).Percent,
                $"M1: the solved far anchor must sit at {f.Config.TargetDepthRatio:F3}× the near anchor's " +
                $"view depth — measured {f.AchievedDepthRatio:F4} (w_near={f.NearAnchorViewDepthMetres:F1} m, " +
                $"w_far={f.FarAnchorViewDepthMetres:F1} m).");
        }

        /// <summary>
        /// <b>M2.</b> Proves: BOTH anchors project in front of the camera and land inside the frame with a
        /// 32 px margin — i.e. the designed depth ratio is actually reachable on-screen at this pose, so the
        /// far arm measures something the renderer would really draw.
        ///
        /// <para>Does NOT prove that the far LABEL fits (that is M8's spill check), nor anything about depth.</para>
        ///
        /// <para><b>STOP RULE 2.</b> If this fails because the far anchor leaves the frame, LOWER THE ZOOM
        /// (more ground per pixel) and report both numbers — do NOT shrink the depth ratio below 1.8, which
        /// is what makes every reading on this fixture a two-depth reading.</para>
        ///
        /// <para>RED-verify: double the solved <c>s_far</c> ⇒ the far anchor leaves the margin.</para>
        /// </summary>
        [Test]
        public void TwoAnchors_ProjectOnScreen_WithMargin()
        {
            using var f = CreateFixture();
            const double marginPx = 32.0;
            double2 nearPx = f.Measure(OffLookAtSymbolId.CrossNear).AnchorScreenPx;
            double2 farPx  = f.Measure(OffLookAtSymbolId.CrossFar).AnchorScreenPx;
            double lo = marginPx, hi = f.Config.SizePx - marginPx;

            double worst = math.min(
                math.min(math.min(nearPx.x - lo, hi - nearPx.x), math.min(nearPx.y - lo, hi - nearPx.y)),
                math.min(math.min(farPx.x - lo, hi - farPx.x),   math.min(farPx.y - lo, hi - farPx.y)));
            Assert.That(worst, Is.GreaterThan(0.0),
                $"M2: both anchors must project inside [{lo:F0}, {hi:F0}]² device px — near=({nearPx.x:F1}, " +
                $"{nearPx.y:F1}), far=({farPx.x:F1}, {farPx.y:F1}); worst margin {worst:F1} px. STOP RULE 2: " +
                "if the FAR anchor is the offender, lower the zoom and report — do not shrink the depth ratio.");
        }

        /// <summary>
        /// <b>M3 — THE ORACLE'S OWN ACCEPTANCE TEST.</b> Proves: the depth-general closed form
        /// <see cref="GroundRuler.ClosedFormPerpendicularSpanPx"/> agrees with the LIVE camera's projection of
        /// the SAME world segment at BOTH depths, within 1 %. That is the property a look-at-anchored fixture
        /// cannot have — a comparand known to be DEPTH-CORRECT, not merely correct at the look-at.
        ///
        /// <para>Does NOT prove that the closed form is the right MODEL for glyph spacing — only that it
        /// computes the projection of a given world length correctly wherever that length sits.</para>
        ///
        /// <para>RED-verify: evaluate the FAR probe with <c>w_near</c> ⇒ the far reading is ≈ 2× off.</para>
        /// </summary>
        [Test]
        public void ClosedFormSpan_AgreesWithTheLiveProjection_AtBothDepths()
        {
            using var f = CreateFixture();
            double probeM = 30.0 * f.MetresPerDevicePixel;
            var cXZ = new double2(f.CrossAzimuthDir.x, f.CrossAzimuthDir.z);

            double nearProjective = GroundRuler.GroundSegmentSpanPx(
                f.UnityCamera, f.NearAnchorWorldUnity, cXZ, probeM);
            double farProjective = GroundRuler.GroundSegmentSpanPx(
                f.UnityCamera, f.FarAnchorWorldUnity, cXZ, probeM);
            double nearClosed = GroundRuler.ClosedFormPerpendicularSpanPx(
                probeM, f.NearAnchorViewDepthMetres, f.AbsP11, f.ViewportHeightPx);
            double farClosed = GroundRuler.ClosedFormPerpendicularSpanPx(
                probeM, f.FarAnchorViewDepthMetres, f.AbsP11, f.ViewportHeightPx);

            double worstErrorPercent = 100.0 * math.max(
                math.abs(nearProjective / nearClosed - 1.0), math.abs(farProjective / farClosed - 1.0));
            Assert.That(worstErrorPercent, Is.LessThan(1.0),
                $"M3: the depth-general closed form must match the live projection at BOTH depths — " +
                $"near: projective {nearProjective:F4} px vs closed {nearClosed:F4} px; " +
                $"far: projective {farProjective:F4} px vs closed {farClosed:F4} px; " +
                $"worst error {worstErrorPercent:F4} %. A far-only failure means the closed form is carrying " +
                "a look-at-only ruler, which is exactly the blind spot these fixtures exist to cover.");
        }

        /// <summary>
        /// <b>M4 — THE RULER IDENTITY (stop rule 1).</b> Proves:
        /// <see cref="MapRenderer.Unity.Rendering.Map.MapCamera.MetresPerDevicePixel"/> IS the lateral
        /// px-per-metre ruler at the look-at — a segment of <c>30·mpp</c> metres laid along ĉ at the look-at
        /// projects to 30 device px. The settled model's "X px TOP-DOWN" enters the oracle at exactly this one
        /// point; nothing else in the repo pins it.
        ///
        /// <para>Does NOT prove anything at any OTHER depth — the identity holds only here.</para>
        ///
        /// <para><b>STOP RULE 1.</b> If this reads outside 1 %, DO NOT WIDEN IT. Report both numbers and
        /// STOP: every oracle downstream would then be calibrated against the wrong ruler.</para>
        ///
        /// <para>RED-verify: probe with <c>60·mpp</c> ⇒ reads 60 px against a 30 px expectation.</para>
        /// </summary>
        [Test]
        public void MetresPerDevicePixel_IsTheLateralRulerAtTheLookAt()
        {
            using var f = CreateFixture();
            const double probeMultiplier = 30.0;
            double probeM = probeMultiplier * f.MetresPerDevicePixel;
            double spanPx = GroundRuler.GroundSegmentSpanPx(
                f.UnityCamera, f.NearAnchorWorldUnity,
                new double2(f.CrossAzimuthDir.x, f.CrossAzimuthDir.z), probeM);

            // Printed every run, not only on failure: this is STOP RULE 1, so the number every downstream
            // oracle is calibrated on should be legible in the results without re-deriving it.
            TestContext.WriteLine(
                $"M4 ruler check (stop rule 1): {probeMultiplier:F0}·mpp along ĉ at the look-at projects to " +
                $"{spanPx:F6} px against an expectation of {probeMultiplier:F0} px — " +
                $"{100.0 * (spanPx / probeMultiplier - 1.0):F5} % (bound ±1 %). " +
                $"mpp={f.MetresPerDevicePixel:F4} m, |P11|={f.AbsP11:F6}, H={f.ViewportHeightPx:F0}, " +
                $"w_near={f.NearAnchorViewDepthMetres:F1} m.");

            Assert.That(spanPx, Is.EqualTo(probeMultiplier).Within(1).Percent,
                $"M4 (STOP RULE 1): {probeMultiplier:F0}·mpp of ground laid ACROSS the view axis at the " +
                $"look-at must project to {probeMultiplier:F0} device px — measured {spanPx:F4} px " +
                $"({100.0 * (spanPx / probeMultiplier - 1.0):F3} % off; mpp={f.MetresPerDevicePixel:F3} m). " +
                "If this is a genuine failure, DO NOT widen the tolerance — report both numbers and STOP, " +
                "because the fix stage's oracle would then be calibrated against the wrong ruler.");
        }

        /// <summary>
        /// <b>M5 — THE DISCRIMINATOR the whole stage rests on.</b> Proves: a world length perpendicular to the
        /// view axis projects to HALF as many pixels when its view depth doubles — the 1/w law, measured on
        /// the live camera at the fixture's own two anchors.
        ///
        /// <para>Does NOT prove anything about symbols or layout — this is the projection alone. It is the
        /// tooth that says the fixture's two depths are far enough apart to tell a world-welded ruler from a
        /// screen-constant one.</para>
        ///
        /// <para>RED-verify: place BOTH probes at the near anchor ⇒ reads 1.00 instead of 0.50.</para>
        /// </summary>
        [Test]
        public void PerpendicularSpan_HalvesWhenTheViewDepthDoubles()
        {
            using var f = CreateFixture();
            double probeM = 30.0 * f.MetresPerDevicePixel;
            var cXZ = new double2(f.CrossAzimuthDir.x, f.CrossAzimuthDir.z);

            double nearPx = GroundRuler.GroundSegmentSpanPx(f.UnityCamera, f.NearAnchorWorldUnity, cXZ, probeM);
            double farPx  = GroundRuler.GroundSegmentSpanPx(f.UnityCamera, f.FarAnchorWorldUnity, cXZ, probeM);
            double expected = f.NearAnchorViewDepthMetres / f.FarAnchorViewDepthMetres;

            Assert.That(farPx / nearPx, Is.EqualTo(expected).Within(2).Percent,
                $"M5: the SAME world length must project to w_near/w_far = {expected:F4} of its near size at " +
                $"the far anchor — measured {farPx / nearPx:F4} (near {nearPx:F3} px, far {farPx:F3} px).");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M6–M9 — the staged symbol geometry, read back from the built meshes.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M6 — THE TOOTH THAT VALIDATES THE WHOLE READBACK PATH.</b> Proves: the world anchor the
        /// production staging baked for the near symbol's CENTRE glyph, read back through
        /// <c>WorldSlotTransform(...).TransformPoint(AnchorLocal)</c>, lands where the fixture intended its
        /// anchor — within 2 device px projected. If this holds, the fixture is measuring the renderer's own
        /// geometry, with no RTC, tile origin or floating-origin rebase re-derived on the test side.
        ///
        /// <para>Checked at the LOOK-AT on purpose: on a constant-depth line the projection is affine, so the
        /// <c>(seg, t) = (0, 0.5)</c> anchor resolves to the same point whether the arc walk runs on the screen
        /// polyline (today) or the world polyline (after a fix) — so this tooth does NOT presuppose either
        /// layout model and survives the fix unchanged.</para>
        ///
        /// <para>Does NOT prove anything about glyph SPACING. Run for BOTH arms — see the far twin below.</para>
        ///
        /// <para>RED-verify: offset the intended anchor by <c>20·mpp</c> along ĉ ⇒ fails by ~20 px.</para>
        /// </summary>
        [Test]
        public void GlyphWorldAnchors_LandWhereTheFixtureIntended_AtTheLookAt()
            => AssertCentreGlyphLandsAtTheIntendedAnchor(OffLookAtSymbolId.CrossNear, "M6 near");

        /// <summary>
        /// <b>M6b — the FAR twin.</b> `CrossFar`
        /// has its OWN tile key and its own RTC bake, and nothing else in the suite pins it against a known
        /// ABSOLUTE position: M10 bounds it to only ±45 rows (≈±14 % of <c>w_far</c>), and M7–M9 pin spacing
        /// and span, not placement. A far readback that were systematically displaced would leave every
        /// headline RATIO intact, because the ratios are differences within one symbol — so this is the one
        /// clause standing between the far arm and a silently wrong absolute anchor.
        ///
        /// <para>The look-at is NOT special to M6's argument: the walk-invariance that makes this
        /// model-independent is a property of a CONSTANT-VIEW-DEPTH (cross-azimuth) line, which
        /// <c>CrossFar</c> is inherently. So this survives the fix unchanged, exactly as M6 does.</para>
        ///
        /// <para>RED-verify: same injection as M6, applied to the far arm.</para>
        /// </summary>
        [Test]
        public void GlyphWorldAnchors_LandWhereTheFixtureIntended_AwayFromTheLookAt()
            => AssertCentreGlyphLandsAtTheIntendedAnchor(OffLookAtSymbolId.CrossFar, "M6b far");

        private static void AssertCentreGlyphLandsAtTheIntendedAnchor(OffLookAtSymbolId id, string what)
        {
            using var f = CreateFixture();
            SymbolMeasurement m = f.Measure(id);
            int centre = f.Config.GlyphCount / 2;
            double2 intendedPx = m.AnchorScreenPx;
            double2 stagedPx = m.Glyphs[centre].ScreenPx;
            double errorPx = math.length(stagedPx - intendedPx);

            Assert.That(errorPx, Is.LessThan(2.0),
                $"{what}: the label's centre glyph (index {centre} of {f.Config.GlyphCount}) staged at " +
                $"({stagedPx.x:F2}, {stagedPx.y:F2}) px but the fixture anchored the label at " +
                $"({intendedPx.x:F2}, {intendedPx.y:F2}) px — {errorPx:F2} px apart. Either the readback path " +
                "(slot transform → AnchorLocal) is not what this fixture thinks it is, or the label is not " +
                "where it was placed.");
        }

        /// <summary>
        /// <b>M7.</b> Proves: both cross-azimuth symbols really do lie at CONSTANT view depth — every glyph of
        /// a symbol is within 0.5 % of that symbol's mean depth, at both depths. This is the property the
        /// headline measurement rests on: it is what makes the far/near comparison a pure 1/w law with no
        /// within-symbol foreshortening to correct for.
        ///
        /// <para>Does NOT prove that ĉ is the cross-azimuth direction in any absolute sense, and nothing about
        /// spacing.</para>
        ///
        /// <para>RED-verify: point the first reading at <c>RecedingNear</c>, a symbol that genuinely spans a
        /// depth range ⇒ the spread reads ~36 %, 70× the bound. NOT by rebuilding the cross symbols along ĝ:
        /// that turns the whole fixture red at a construction precondition (the cross arm's
        /// linear-agreement check, then the staging count), so the tooth's own red would not be
        /// attributable to the tooth.</para>
        /// </summary>
        [Test]
        public void CrossAzimuthSymbols_LieAtConstantViewDepth()
        {
            using var f = CreateFixture();
            double nearSpread = RelativeDepthSpread(f.Measure(OffLookAtSymbolId.CrossNear));
            double farSpread  = RelativeDepthSpread(f.Measure(OffLookAtSymbolId.CrossFar));

            Assert.That(math.max(nearSpread, farSpread), Is.LessThan(0.005),
                $"M7: a cross-azimuth label's glyphs must all sit at one view depth — relative spread " +
                $"near {nearSpread:P4}, far {farSpread:P4} (bound 0.5000 %). A large spread means ĉ is not " +
                "perpendicular to the view axis and the far/near comparison is no longer a pure 1/w law.");
        }

        /// <summary>
        /// <b>M8.</b> Proves: both cross-azimuth symbols staged EVERY glyph — <c>4 · GlyphCount</c> stream-0
        /// vertices on each slot mesh. A curved symbol whose road is too short returns 0 from
        /// <c>StageCurved</c>'s spill check and silently disappears; that must be a loud failure, not a
        /// missing measurement.
        ///
        /// <para>(That all four corners of each glyph share ONE <c>AnchorLocal</c> — the precondition that
        /// makes "the glyph's position" a single well-defined point — is asserted by the fixture itself at
        /// readback, on every symbol.)</para>
        ///
        /// <para>Does NOT prove the glyphs are in the right PLACE — that is M6/M9's job.</para>
        ///
        /// <para>RED-verify: set <c>SpillMargin = 0.5</c> ⇒ the roads are shorter than the symbols and
        /// neither stages.</para>
        /// </summary>
        [Test]
        public void CurvedSymbols_StageEveryGlyph_AtBothDepths()
        {
            using var f = CreateFixture();
            int expected = 4 * f.Config.GlyphCount;
            int nearCount = f.VertexCount(OffLookAtSymbolId.CrossNear);
            int farCount  = f.VertexCount(OffLookAtSymbolId.CrossFar);

            Assert.That(math.min(nearCount, farCount) == expected && math.max(nearCount, farCount) == expected,
                Is.True,
                $"M8: each cross-azimuth slot mesh must carry {expected} vertices (4 per glyph × " +
                $"{f.Config.GlyphCount}) — near {nearCount}, far {farCount}.");
        }

        /// <summary>
        /// <b>M9 — EXTRACTION CALIBRATION.</b> Proves: the per-gap measurement genuinely resolves INDIVIDUAL
        /// glyph gaps — within each cross-azimuth symbol the gaps are uniform to better than 1 %, as uniform
        /// baked advances on a constant-depth line require. A measurement that silently reported one averaged
        /// number, or mis-paired glyphs, could not show this.
        ///
        /// <para>True under BOTH the screen-constant and the world-welded model (uniform advances on a
        /// constant-depth line are uniform either way), so this tooth survives the fix unchanged — it
        /// calibrates the instrument, it does not pin the defect.</para>
        ///
        /// <para>Does NOT prove the gaps are the right SIZE. That is what is at issue, and this file makes
        /// no such claim for the far symbol.</para>
        ///
        /// <para>RED-verify: perturb one glyph's <c>ArcCenter</c> in the fixture ⇒ non-uniform.</para>
        /// </summary>
        [Test]
        public void GlyphSpacing_IsUniformWithinEachSymbol()
        {
            using var f = CreateFixture();
            double nearNonUniformity = ScreenSpacingNonUniformity(f.Measure(OffLookAtSymbolId.CrossNear));
            double farNonUniformity  = ScreenSpacingNonUniformity(f.Measure(OffLookAtSymbolId.CrossFar));

            Assert.That(math.max(nearNonUniformity, farNonUniformity), Is.LessThan(0.01),
                $"M9: within one cross-azimuth label the glyph gaps must be uniform — max/min − 1 reads " +
                $"{nearNonUniformity:P4} (near) and {farNonUniformity:P4} (far), bound 1.0000 %. A failure " +
                "means the per-gap extraction is not resolving individual gaps.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M10–M12 — the render, the receding soundness arm, the point arm.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M10 — RENDER CORROBORATION.</b> Proves: both cross-azimuth symbols actually RENDER, each inside
        /// the disjoint row band its own projected anchor defines — so the geometry the mesh readback measured
        /// is the geometry that reaches the screen, at both depths.
        ///
        /// <para>Does NOT measure spacing from ink: an ink-width reading conflates the CPU's
        /// glyph spacing with the shader's screen-px glyph SIZE, which is how a screen-ruler model reads as
        /// correct.</para>
        ///
        /// <para><b>THE FAR FLOOR IS DERIVED, NOT A FLAT CONSTANT, AND THAT IS A CONSEQUENCE OF THE
        /// FIX RATHER THAN A CONCESSION TO IT.</b> <see cref="InkFloor"/> was calibrated when a glyph was a
        /// fixed SCREEN size at every depth, so both bands carried comparable ink. A map-pitched
        /// glyph is a fixed WORLD size — the settled model, <c>text-size</c> under <c>pitch-alignment: map</c>
        /// is X px TOP-DOWN — so the far symbol's screen area falls as <c>1/w²</c> BY DESIGN. Keeping one flat
        /// floor for both bands would assert that the far symbol is NOT foreshortened, i.e. it would pin the
        /// defect the world-metre quad removes. The far band therefore gets <see cref="FarInkFloor"/>, the same floor divided by
        /// the fixture's OWN achieved depth ratio squared.</para>
        ///
        /// <para><b>The four readings this was derived from, all MEASURED on this fixture at its shipped pose</b>
        /// (tilt 55, ratio 2). The earlier pair was taken by forcing <c>WorldSymbolRenderer</c>'s
        /// <c>mapPitchCorners</c> to <c>false</c> and restoring it:
        /// <code>
        ///            near band    far band    far/near
        ///   earlier      1200 px     1262 px     1.052    &lt;- screen-constant size: the far symbol renders the
        ///                                                   SAME size as the near one. The defect, in ink.
        ///   world-metre 801 px       98 px     0.122    &lt;- welded to the ground, and foreshortened.
        /// </code></para>
        ///
        /// <para><b>The NEAR band moves too, and that is correct rather than a regression.</b> A reader
        /// expects it not to, because at the look-at depth the two rulers coincide exactly — but that identity
        /// is about the RULER, not about the PLANE. A map-pitched cell's x̂ arm lies along the road
        /// (cross-azimuth here, perpendicular to the view axis, so it projects 1:1 and does not move), while
        /// its ŷ arm lies along <c>cross(up, x̂)</c> — the RECEDING ground direction — which at tilt 55 is
        /// foreshortened by roughly <c>cos 55° = 0.574</c>. The glyph is lying flat on the ground instead of
        /// facing the camera, so it loses height: 1200 → 801 px is a factor of 0.667 against 0.574 for the
        /// height alone. That IS the headline behaviour, seen from the ink side.</para>
        ///
        /// <para><b>Why the far band falls FASTER than the 1/w² area law</b> (0.122 of the near band where a
        /// pure area model predicts 0.250) — stated so a future reader does not diagnose a size defect. Two
        /// mechanisms, both real and both expected: (1) the far anchor is viewed at a MORE GRAZING angle than
        /// the near one, so its ŷ arm foreshortens harder still — a per-anchor effect that no single global
        /// cosine captures; and (2) ink is COUNTED against a hard threshold
        /// (<c>WorldSymbolInkAnalysis.InkThreshold</c>) while the text shader ramps coverage over ~1 screen px
        /// at the glyph edge, so a stroke whose width has halved loses a larger FRACTION of itself to the
        /// transition. Neither is a property of the glyph's world size. <c>1/ratio²</c> is therefore used only
        /// as a CONSERVATIVE lower bound on the shrinkage; the true falloff is faster, and the achieved margin
        /// is 98/50 = 1.96×.</para>
        ///
        /// <para>That conservatism is also why this tooth stays a PRESENCE check ("did the far symbol reach the
        /// screen at all") instead of being promoted into a size assertion. The size law is asserted where it
        /// can be read without either confound — in WORLD METRES, off the built mesh — by T3 in
        /// <c>MapPitchedGlyphSizeTests</c>.</para>
        ///
        /// <para>Still does NOT measure spacing from ink (see above), and now also asserts that the far band
        /// CONTAINS its cell rather than clipping it — a shrinking cell could in principle have drifted out of
        /// a band sized for the old one, and an ink count that ROSE would mean the band had caught a
        /// neighbour. Verified, not assumed.</para>
        ///
        /// <para>RED-verify: move the far anchor 400 px up-screen ⇒ its band is empty.</para>
        /// </summary>
        [Test]
        public void RenderedInk_AppearsInBothProjectedBands()
        {
            using var f = CreateFixture();
            f.RowBandFor(OffLookAtSymbolId.CrossNear, out int nearFrom, out int nearTo);
            f.RowBandFor(OffLookAtSymbolId.CrossFar,  out int farFrom,  out int farTo);
            Assert.That(farTo < nearFrom || nearTo < farFrom, Is.True,
                $"M10 precondition: the two labels' row bands must be DISJOINT — near [{nearFrom}, {nearTo}], " +
                $"far [{farFrom}, {farTo}]. Overlapping bands cannot attribute ink to a label.");

            WorldSymbolInkAnalysis.AnalyzeInk(f.InkPixels, f.Config.SizePx, f.Config.SizePx, nearFrom, nearTo,
                out _, out _, out _, out _, out _, out _, out int nearInk);
            WorldSymbolInkAnalysis.AnalyzeInk(f.InkPixels, f.Config.SizePx, f.Config.SizePx, farFrom, farTo,
                out int farMinRow, out int farMaxRow, out _, out _, out _, out _, out int farInk);

            int farFloor = FarInkFloor(f);
            // M13's pattern: the numbers reproduce from the COMMITTED suite, not from a throwaway probe.
            // These are the readings the far floor's derivation was built on.
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "M10  nearInk={0} px (band [{1}, {2}], floor {3})  farInk={4} px (band [{5}, {6}], derived " +
                "floor {7})  achieved depth ratio={8:F4}  far/near={9:F4}  1/ratio²={10:F4}",
                nearInk, nearFrom, nearTo, InkFloor, farInk, farFrom, farTo, farFloor,
                f.AchievedDepthRatio, (double)farInk / nearInk,
                1.0 / (f.AchievedDepthRatio * f.AchievedDepthRatio)));

            // The far cell is SMALLER than the band that was sized for the old one, so it must sit
            // strictly inside it. Touching an edge means the band is clipping the measurement; ink at or
            // above the near band's level would mean the band caught a neighbouring symbol instead.
            Assert.That(farMinRow > farFrom && farMaxRow < farTo, Is.True,
                $"M10 precondition: the far band must CONTAIN its cell, not clip it — far ink rows " +
                $"[{farMinRow}, {farMaxRow}] inside band [{farFrom}, {farTo}]. Touching a band edge means the " +
                "reading is clipped, so neither the count nor the floor below means what it says.");

            // The far symbol must be FORESHORTENED, not merely present. The bound is the depth ratio, not 1:
            // `farInk < nearInk` alone separates the world-metre reading (0.122) from the screen-sized one (1.052) by
            // only 5 %, which is not a margin. Dividing by the achieved ratio puts the threshold at ~400 px
            // against a measured 98 — roughly 4× either way, and it is still a full ratio× looser than the
            // 1/ratio² area law, so it cannot fail for the "falls faster than the area model" reason this
            // tooth's doc explains.
            double farInkCeiling = nearInk / f.AchievedDepthRatio;
            Assert.That(farInk, Is.LessThan(farInkCeiling),
                $"M10: the far label must be FORESHORTENED — it carries {farInk} px against a ceiling of " +
                $"{farInkCeiling:F0} px (near {nearInk} px / achieved depth ratio {f.AchievedDepthRatio:F3}). " +
                "Pre-W2 the far band read ~1.05× the near band's ink because the glyph was a fixed SCREEN " +
                "size; a reading anywhere near the near band's means the world ruler is not reaching the " +
                "drawn size. Ink at or above the near band would additionally mean the band caught a neighbour.");

            // Each band against ITS OWN floor; the worse MARGIN is the single asserted clause (this file's
            // header: compute both readings, assert the worse one, carry both numbers in the message).
            double nearMargin = nearInk / (double)InkFloor;
            double farMargin  = farInk  / (double)farFloor;
            Assert.That(math.min(nearMargin, farMargin), Is.GreaterThan(1.0),
                $"M10: each label must render meaningful ink inside its OWN row band — near band " +
                $"[{nearFrom}, {nearTo}] carries {nearInk} px against floor {InkFloor} ({nearMargin:F2}×), " +
                $"far band [{farFrom}, {farTo}] carries {farInk} px against derived floor {farFloor} " +
                $"({farMargin:F2}×). An empty far band means the far label did not reach the screen, so " +
                "nothing measured about it corroborates. The far floor is InkFloor / achievedDepthRatio² " +
                "because since W2 a map-pitched glyph is a fixed WORLD size and its screen area falls as " +
                "1/w² by design — see this tooth's doc before widening anything.");
        }

        /// <summary>The far band's ink floor: <see cref="InkFloor"/> scaled by the fixture's OWN achieved
        /// far/near view-depth ratio squared, because a map-pitched glyph's screen AREA falls as
        /// <c>1/w²</c>. Derived from the fixture's measured pose rather than written as a literal, so it
        /// tracks a re-tuned <c>TargetDepthRatio</c> instead of silently becoming wrong — and so it cannot be
        /// mistaken for a number chosen to make the tooth pass.
        ///
        /// <para><b>The integer truncation is a real vacuity hazard, so it is ASSERTED and not clamped.</b>
        /// <c>(int)(200 / ratio²)</c> reaches <b>0</b> at <c>AchievedDepthRatio ≳ 14.1</c>, and
        /// <c>farInk / 0.0</c> is <c>+Inf</c>, which would PASS SILENTLY — a tooth that cannot fail. That is
        /// reachable by exactly the edit this doc invites (re-tuning <c>TargetDepthRatio</c>), so the failure
        /// must be loud. Clamping to 1 instead would keep the tooth alive but reduce it to "any ink at all",
        /// which is a different and much weaker claim than the one documented above; better to stop and make
        /// the maintainer choose a floor explicitly.</para></summary>
        private static int FarInkFloor(OffLookAtSymbolScene f)
        {
            int floor = (int)(InkFloor / (f.AchievedDepthRatio * f.AchievedDepthRatio));
            Assert.That(floor, Is.GreaterThanOrEqualTo(1),
                $"M10 precondition: the derived far ink floor truncated to {floor} at an achieved depth ratio " +
                $"of {f.AchievedDepthRatio:F3} (InkFloor {InkFloor} / ratio²). A floor of 0 makes the margin " +
                "check +Inf and the tooth VACUOUS — it could never fail. Choose a floor deliberately for this " +
                "pose rather than letting the truncation decide.");
            return floor;
        }

        /// <summary>
        /// <b>M11 — the DEPTH-SPANNING arm's precondition.</b> Proves: the fixture really does build a symbol
        /// that spans a RANGE of view depths (strictly increasing along the glyph run, by at least 15 % end
        /// to end) — the contrast that makes the cross-azimuth arm's constant-depth claim (M7) meaningful
        /// rather than vacuous, and the property T2/T3/T4 need in order to discriminate a per-glyph world
        /// walk from a screen walk scaled by one per-symbol constant.
        ///
        /// <para><b>Stays on <c>RecedingNear</c>.</b> The receding roads are symmetric
        /// in world metres, which puts <c>RecedingNear</c>'s span ratio comfortably above this bound;
        /// <c>RecedingFar</c>'s sits much closer to it (its symbol is at ~2× the depth, so the same world span
        /// is a smaller relative spread) and asserting a bound with no margin is not a tooth. That number is
        /// REPORTED by M13's table instead. <c>RecedingFar</c> is still a full participant in T2/T3/T4,
        /// where even a modest depth spread gives large discrimination against a 1 % bound.</para>
        ///
        /// <para>Asserts nothing about SPACING — this file never did and still does not; the spacing
        /// assertions on this arm live in <c>MapPitchedWorldArcLayoutTests</c>.</para>
        ///
        /// <para>RED-verify: rebuild the receding symbols along ĉ ⇒ the depth span collapses to 1.00 and the
        /// monotonicity is noise.</para>
        /// </summary>
        [Test]
        public void RecedingSymbol_SpansAMonotonicDepthRange()
        {
            using var f = CreateFixture();
            GlyphMeasurement[] glyphs = f.Measure(OffLookAtSymbolId.RecedingNear).Glyphs;

            double smallestStep = double.MaxValue;
            for (int g = 0; g + 1 < glyphs.Length; g++)
                smallestStep = math.min(smallestStep,
                    glyphs[g + 1].ViewDepthMetres - glyphs[g].ViewDepthMetres);
            double spanRatio = glyphs[glyphs.Length - 1].ViewDepthMetres / glyphs[0].ViewDepthMetres;

            Assert.That(smallestStep, Is.GreaterThan(0.0),
                $"M11: view depth must increase STRICTLY along the receding label's glyph run — smallest " +
                $"step {smallestStep:F3} m (first {glyphs[0].ViewDepthMetres:F1} m, last " +
                $"{glyphs[glyphs.Length - 1].ViewDepthMetres:F1} m).");
            Assert.That(spanRatio, Is.GreaterThan(1.15),
                $"M11: the receding label must span a real depth RANGE — last/first depth reads " +
                $"{spanRatio:F4} (bound 1.15). A ratio near 1.00 means this arm is not receding at all and " +
                "M7's constant-depth claim has no contrast.");
        }

        /// <summary>
        /// <b>M12 — the POINT arm.</b> Proves: the point-placement path also stages at both depths, and each
        /// point symbol's single staged quad carries a world anchor that projects within 2 px of where the
        /// fixture put it. Point and curved reach the world mesh by different routes
        /// (<c>CandidateEmit.AnchorLocal</c> vs the per-quad one); this pins that both routes land where they
        /// were aimed, so a later stage touching either has a two-depth reference.
        ///
        /// <para>Does NOT prove anything about spacing (a point symbol has one quad) or about ink — the point
        /// symbols are excluded from the rendered frame.</para>
        ///
        /// <para>RED-verify: offset one intended point anchor by <c>20·mpp</c> ⇒ fails by ~20 px (near) or
        /// ~10 px (far).</para>
        /// </summary>
        [Test]
        public void PointSymbols_StageAtBothDepths_WhereTheOracleProjectsThem()
        {
            using var f = CreateFixture();
            int nearVerts = f.VertexCount(OffLookAtSymbolId.PointNear);
            int farVerts  = f.VertexCount(OffLookAtSymbolId.PointFar);
            Assert.That(nearVerts == 4 && farVerts == 4, Is.True,
                $"M12 precondition: each point label must stage exactly one quad (4 vertices) — near " +
                $"{nearVerts}, far {farVerts}.");

            SymbolMeasurement near = f.Measure(OffLookAtSymbolId.PointNear);
            SymbolMeasurement far  = f.Measure(OffLookAtSymbolId.PointFar);
            double nearErrorPx = math.length(near.Glyphs[0].ScreenPx - near.AnchorScreenPx);
            double farErrorPx  = math.length(far.Glyphs[0].ScreenPx - far.AnchorScreenPx);

            Assert.That(math.max(nearErrorPx, farErrorPx), Is.LessThan(2.0),
                $"M12: each point label's staged anchor must project where the fixture aimed it — near off by " +
                $"{nearErrorPx:F2} px, far off by {farErrorPx:F2} px (bound 2 px).");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // M13 — the measurement tooth.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>M13 — THE EVIDENCE.</b> Proves: every gap at both depths is finite and positive, and the NEAR
        /// (look-at) symbol's mean screen spacing agrees with the model oracle within 3 %.
        ///
        /// <para><b>The near clause is the LOOK-AT CONTROL, and it is the whole trap in one line.</b> At the
        /// look-at, a screen-constant ruler and the world-welded ("X px TOP-DOWN") model coincide EXACTLY, so
        /// this reads ≈ 1.00 under BOTH — which is why a fixture anchored here discriminates nothing.</para>
        ///
        /// <para><b>It asserts NOTHING about the far symbol — intentionally, and still.</b> The far assertion
        /// ("far/near screen spacing == 0.500") is T1 in <c>MapPitchedWorldArcLayoutTests</c>, RED-verified
        /// there against the pre-fix production code. What this tooth does instead is PRINT the full per-gap
        /// table for all six symbols and the four headline ratios, so the evidence is reproducible from the
        /// committed suite rather than from a throwaway probe. Read the printed <c>far measured/oracle</c>
        /// row: it is the number the world-arc walk moved, from ≈ 2.00 to ≈ 1.00.</para>
        ///
        /// <para>The headline ratios are ratios of two readings from ONE frame and ONE symbol pair, so any
        /// uniform scale error (OneEm, TextSizePx, mpp, DPR, atlas scale, a wrong P11) multiplies both and
        /// CANCELS; only the depth dependence survives.</para>
        ///
        /// <para>RED-verify: scale <c>TextSizePx</c> on the ORACLE SIDE ONLY by 1.2 ⇒ the near residual blows
        /// past 3 %.</para>
        /// </summary>
        [Test]
        public void CurvedGlyphSpacing_MatchesTheModelAtTheLookAt_AndReportsBothDepths()
        {
            using var f = CreateFixture();
            TestContext.WriteLine(f.FormatMeasurementTable());

            double smallestGapPx = double.MaxValue;
            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.CrossNear, OffLookAtSymbolId.CrossFar })
            {
                double[] gaps = f.Measure(id).ScreenSpacingPx;
                for (int g = 0; g < gaps.Length; g++)
                {
                    Assert.That(double.IsNaN(gaps[g]) || double.IsInfinity(gaps[g]), Is.False,
                        $"M13: {id} gap {g} is not finite ({gaps[g]}) — no ratio taken from it means anything.");
                    smallestGapPx = math.min(smallestGapPx, gaps[g]);
                }
            }
            Assert.That(smallestGapPx, Is.GreaterThan(0.0),
                $"M13: every measured gap must be positive — smallest reads {smallestGapPx:F6} px.");

            double nearMeasured = OffLookAtSymbolScene.Mean(f.Measure(OffLookAtSymbolId.CrossNear).ScreenSpacingPx);
            double nearOracle   = f.ModelPredictedScreenSpacingPx(OffLookAtSymbolId.CrossNear);
            Assert.That(nearMeasured, Is.EqualTo(nearOracle).Within(3).Percent,
                $"M13 (the LOOK-AT CONTROL): the near label's mean screen spacing ({nearMeasured:F4} px) must " +
                $"match the model oracle ({nearOracle:F4} px) within 3 % — measured " +
                $"{100.0 * (nearMeasured / nearOracle - 1.0):F3} % off. This clause reads ≈ 1.00 under BOTH " +
                "competing models; the discriminating reading is the FAR row of the printed table, which this " +
                "tooth deliberately does not assert.");
        }

        // ── shared reductions ────────────────────────────────────────────────────────────────────────────

        private static double RelativeDepthSpread(SymbolMeasurement m)
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0.0;
            for (int g = 0; g < m.Glyphs.Length; g++)
            {
                double w = m.Glyphs[g].ViewDepthMetres;
                min = math.min(min, w);
                max = math.max(max, w);
                sum += w;
            }
            return (max - min) / (sum / m.Glyphs.Length);
        }

        private static double ScreenSpacingNonUniformity(SymbolMeasurement m)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int g = 0; g < m.ScreenSpacingPx.Length; g++)
            {
                min = math.min(min, m.ScreenSpacingPx[g]);
                max = math.max(max, m.ScreenSpacingPx[g]);
            }
            return max / min - 1.0;
        }
    }
}
