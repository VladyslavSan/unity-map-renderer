// Unity EditMode only — real MapCamera + Camera/RenderTexture, off-screen GPU render + CPU readback.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// S111 — the RENDERED teeth for MapPixelsToWorld's DIRECTION SYMMETRY, measured at the source.
//
// THE DEFECT, IN ONE LINE: MapPixelsToWorld probes ONE-SIDED — it steps refMag metres along the SIGNED
// dirWS and measures the pixel span — so the probe travels to a different depth and the span it measures
// carries the probe's OWN foreshortening. Flipping dirWS flips which way it travels, so one physical axis
// gets two rulers, (1+e)/(1−e) apart with e = 0.02·tan(fov/2)·dot(dirWS, fwd). The two ribbon vertices of a
// station share one centreline point and carry opposite extrudeN, so they hit exactly that.
//
// WHY THESE TEETH ARE WORLD-SPACE AND NOT SCREEN-SPACE. The ribbon's two edge vertices are offset
// +H·k(1+e) and −H·k(1−e) in WORLD metres, so their world SEPARATION is exactly 2Hk — the errors cancel
// before the perspective divide — while the band's world CENTRE sits e·H·k off the centreline. Screen
// position is rational in world offset, so projecting the two endpoints is nonlinear and the rendered screen
// width is NOT a null: it moves by +0.008 px on a 16 px band and +0.48 px on a 120 px one. A screen-width
// measurement would therefore be measuring the projection, not the defect. The world offsets are the clean
// observable, and their RATIO is the null this file asserts.
//
// FIXTURE SHAPE IS LOAD-BEARING, exactly as in LineDashSnapshotTests: every arm drives the material through
// the PRODUCTION seam — a style JSON parsed into a RenderLayerSet, then set.ApplyZoom(zoom, dpr) — so
// _Width and _LineOffset arrive in DEVICE px by the real MaterialFactory.BindDevicePixelFloat path. Setting
// the properties on a hand-made material would test the shader while leaving the wiring unmeasured. The
// frame constant the widths convert with comes from the real MapCamera (S116), which BuildScene constructs.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class LineProbeSymmetrySnapshotTests
    {
        private const int    Size      = 512;
        private const double Zoom      = 8.0;

        /// <summary>T7's pose. across·fwd = sin 55° = 0.81915, so e = 0.02·tan30°·0.81915 = 0.009458753 and
        /// the two rulers sit (1+e)/(1−e) = 1.0190982 apart.</summary>
        private const double TiltDeg = 55.0;

        private const double RoadHalfLengthM = 80_000.0;
        private const double RoadStationM    =  2_000.0;

        // T4's receding road. It must reach both frame edges at tilt 55: the bottom of the frame cuts the
        // ground at z ≈ −75 km and the top at z ≈ +890 km (fov 60, so the frame's top ray is still 5° below
        // horizontal and the horizon is off-screen — every row is ground). Stations are cosmetic under the
        // world-width model, where widthWorld is constant and a chord of a constant IS the constant.
        private const double RecedingRoadFromM    =  -90_000.0;
        private const double RecedingRoadToM      = 950_000.0;
        private const double RecedingRoadStationM =   10_000.0;

        // ── The scene ────────────────────────────────────────────────────────────────────────────

        private sealed class ProbeScene : IDisposable
        {
            public TiltedGroundScene Scene;
            public RenderLayerSet    Layers;

            public Camera    UnityCamera => Scene.UnityCamera;
            public MapCamera MapCam      => Scene.MapCam;

            /// <summary>The styled line material, straight off the production render layer.</summary>
            public Material Material => Layers[0].Material;

            public void Dispose()
            {
                Layers?.Dispose();
                Scene?.Dispose();
            }
        }

        /// <summary>line-width and line-offset are plain CONSTANTS on purpose: a feature-dependent value is
        /// bound as a constant 1 through the same device-px path, which would silently make every
        /// measurement here about something else.</summary>
        private static string StyleJson(double styledWidthPx, double lineOffsetPx)
        {
            string width  = styledWidthPx.ToString("R", CultureInfo.InvariantCulture);
            string offset = lineOffsetPx.ToString("R", CultureInfo.InvariantCulture);
            return
                "{ \"version\": 8," +
                "  \"sources\": { \"s\": { \"type\": \"vector\", \"tiles\": [\"https://x/{z}/{x}/{y}.pbf\"] } }," +
                "  \"layers\": [" +
                "    { \"id\": \"probe-road\", \"type\": \"line\", \"source\": \"s\", \"source-layer\": \"l\"," +
                "      \"paint\": { \"line-color\": [\"rgba\", 242, 153, 38, 1]," +
                "                   \"line-width\": " + width + "," +
                "                   \"line-offset\": " + offset + " } }" +
                "  ] }";
        }

        /// <summary>Builds the styled <see cref="RenderLayerSet"/> and its ten preconditions FIRST, and
        /// <see cref="TiltedGroundScene.Create"/> LAST — deliberately, not incidentally. <c>Create</c>
        /// mutates PROCESS-GLOBAL state (ambient mode, quality level) that only <c>Dispose</c> restores; if
        /// any precondition below fired while the scene already existed, an assertion failure would abort
        /// this method with no <c>try</c>/<c>finally</c> in scope and leak that global state for the REST OF
        /// THE BATCH — every lit snapshot fixture after this one would render under Flat 0.9 ambient at
        /// quality 0 and fail pointing nowhere near the cause (exactly the stale-shader-global genre
        /// <c>docs/line-rendering-design.md</c> §1.1 already recorded once). None of these preconditions
        /// read <c>scene</c>, so ordering `Create` last removes the window instead of handling it.</summary>
        private static ProbeScene BuildScene(double tiltDeg, double styledWidthPx, double lineOffsetPx)
        {
            const double Dpr = 1.0;

            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(StyleJson(styledWidthPx, lineOffsetPx)), Zoom,
                      MapMaterialSetTestUtil.Load());
            Assert.That(set.Count, Is.EqualTo(1), "the probe-road style must yield exactly one render layer.");
            Assert.IsNotNull(set[0].Material, "Map/Line base material must be configured for this fixture.");

            set.ApplyZoom(Zoom, Dpr);

            Material mat = set[0].Material;
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.DashCount), Is.EqualTo(0f),
                "precondition: _DashCount must be 0. A dashed band has ON/OFF runs, and the coverage " +
                "integral below would sum a dash boundary as if it were the ribbon edge.");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.Width),
                Is.EqualTo((float)(styledWidthPx * Dpr)).Within(1e-3f),
                $"precondition: _Width must reach the shader in DEVICE px ({styledWidthPx}×dpr, S107).");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.LineOffset),
                Is.EqualTo((float)(lineOffsetPx * Dpr)).Within(1e-3f),
                $"precondition: _LineOffset must reach the shader in DEVICE px ({lineOffsetPx}×dpr).");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels), Is.EqualTo(1f),
                "precondition: _WidthIsPixels must be 1, or pxToWorld is a literal 1.0 and MapPixelsToWorld " +
                "is never called for the width family — the whole measurement would be inert.");

            // The AA straddle must be LIVE: §5.2's estimator is exact for a clamped ONE-DEVICE-PIXEL linear
            // ramp. With AA off the edge is a step and the coverage integral recovers a different surface;
            // with a hairline keyword the ramp is re-shaped or the band is clamped.
            Assert.That(mat.IsKeywordEnabled("_EDGE_ANTIALIASING_OFF"), Is.False,
                "precondition: the AA straddle must be live — the estimator assumes a 1 device-px ramp.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_SOLID_CORE"), Is.False,
                "precondition: _HAIRLINE_SOLID_CORE clamps the rendered band and rescales coverage.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_HARD"), Is.False,
                "precondition: _HAIRLINE_HARD narrows the ramp toward a step.");

            var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { TiltDegrees = tiltDeg });

            return new ProbeScene
            {
                Scene  = scene,
                Layers = set,
            };
        }

        /// <summary>Builds the east–west road, renders it once, and hands the caller the scene and the raw
        /// pixels. Everything the measurement needs is read off that one render.
        ///
        /// <para>ORDERING NOTE (Stage T extraction): before Stage T, the lit ambient was set up BEFORE
        /// <see cref="BuildScene"/> ran; <see cref="TiltedGroundScene.Create"/> now sets it up INSIDE the
        /// scene construction that <c>BuildScene</c> calls. Nothing between the two reads ambient state, so
        /// this reordering is behaviour-preserving — noted because it is the one non-mechanical part of the
        /// extraction.</para></summary>
        private static void WithRenderedRoad(
            double tiltDeg, double styledWidthPx, double lineOffsetPx, string pngName,
            Action<ProbeScene, byte[]> measure)
        {
            using var snap  = new SnapshotRenderer(Size, Size);
            using var probe = BuildScene(tiltDeg, styledWidthPx, lineOffsetPx);

            var pts = new List<double2>();
            for (double x = -RoadHalfLengthM; x <= RoadHalfLengthM + 1e-6; x += RoadStationM)
                pts.Add(new double2(x, 0.0));
            Mesh mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);
            var go = new GameObject("Probe_Road");
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = probe.Material;
            try
            {
                probe.Scene.Render(snap);
                snap.WritePng(pngName);

                // The ribbon edges are lines of constant world z, so a probe row inverts to one z. That
                // holds because the camera has heading 0: a rotation about world X leaves screen-y a
                // function of world z alone. Asserted rather than assumed.
                Vector3 originSp = probe.UnityCamera.WorldToScreenPoint(Vector3.zero);
                Vector3 refSp    = probe.UnityCamera.WorldToScreenPoint(new Vector3(10_000f, 0f, 0f));
                Assert.That(originSp.y, Is.EqualTo(refSp.y).Within(0.05),
                    "precondition: the road's centreline must project onto a single screen ROW, or a " +
                    "silhouette screen-y does not invert to a single world z.");

                measure(probe, snap.RawPixels);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>The T4 variant: a road running NORTH–SOUTH, i.e. away from the camera, so one render
        /// carries a wide range of view depths on a band whose across-axis is world east — perpendicular to
        /// the view azimuth, hence unforeshortened and vertical on screen at the centre column. Shares
        /// <see cref="BuildScene"/> and the lighting recipe with <see cref="WithRenderedRoad"/>; only the
        /// geometry and the centreline precondition differ.</summary>
        private static void WithRenderedRecedingRoad(
            double tiltDeg, double styledWidthPx, string pngName, Action<ProbeScene, byte[]> measure)
        {
            using var snap  = new SnapshotRenderer(Size, Size);
            using var probe = BuildScene(tiltDeg, styledWidthPx, lineOffsetPx: 0.0);

            var pts = new List<double2>();
            for (double z = RecedingRoadFromM; z <= RecedingRoadToM + 1e-6; z += RecedingRoadStationM)
                pts.Add(new double2(0.0, z));
            Mesh mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);
            var go = new GameObject("Probe_RecedingRoad");
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = probe.Material;
            try
            {
                probe.Scene.Render(snap);
                snap.WritePng(pngName);

                // At heading 0 a road on the world x = 0 plane projects to the vertical centre column,
                // so a HORIZONTAL cut is exactly perpendicular to the band and its coverage integral is
                // the rendered width. Asserted, not assumed.
                Vector3 nearSp = probe.UnityCamera.WorldToScreenPoint(new Vector3(0f, 0f, 0f));
                Vector3 farSp  = probe.UnityCamera.WorldToScreenPoint(new Vector3(0f, 0f, 200_000f));
                Assert.That(nearSp.x, Is.EqualTo(farSp.x).Within(0.05),
                    "precondition: the receding road must project onto a single screen COLUMN, or a " +
                    "horizontal cut is not perpendicular to it and the integral is not the width.");

                measure(probe, snap.RawPixels);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        // ── The measurement primitive: a per-column coverage integral on a PINNED lattice ─────────
        //
        // Sample j of RawPixels reports the shader's coverage at SCREEN-Y j + 0.5 (RawPixels is bottom-up,
        // row 0 = the bottom scanline, and Unity screen-y grows upward too — no flip, only the half pixel).
        // HIGHER ROW INDEX = UP-SCREEN = NORTH = the FAR edge.
        //
        // Per column, per edge, define an interior anchor ~4 px inside the edge (coverage ≥ 0.99), an
        // exterior sample ~4 px outside (coverage ≤ 0.01), and S = Σ coverage over the CLOSED window between
        // them, THE ANCHOR INCLUDED. For a clamped one-device-pixel linear ramp on a unit lattice, exactly
        // and at every sub-pixel phase:
        //
        //     paddedSilhouetteScreenY(far)  = (jAnchor + 0.5) + S_far
        //     paddedSilhouetteScreenY(near) = (jAnchor + 0.5) − S_near
        //
        // Derivation, in one line, for the far edge: coverage(y) = clamp(P − y, 0, 1) with P the padded
        // silhouette, so Σ_{j≥a} clamp(P − j − 0.5, 0, 1) telescopes to P − a − 0.5 whatever the sub-pixel
        // phase. The anchor-inclusion convention IS the difference between the three candidate surfaces —
        // including it lands on the padded silhouette, excluding it lands half a pixel short of the styled
        // edge, and a full cut counting each row once sums to the APPARENT/styled width. All three are true
        // of different quantities; T-S1c clause 1 pins which one this estimator recovers, by measurement.
        //
        // The 0.5-CROSSING estimator is rejected on the record: the ramp is exactly one device pixel wide and
        // samples are one pixel apart, so at most one sample is ever strictly interior and the crossing is
        // biased by up to 0.086 px. Column averaging removes NONE of it — at heading 0 every column lands on
        // the identical sub-pixel phase, so the bias is systematic, not noise.
        //
        // The plateau is taken LOCALLY, per edge, per column. PixelCoverage.CoverageAt is exactly the
        // rendered alpha only when the plateau is a fully-covered sample AT THE SAME SHADING LEVEL; this
        // shader is real PBR with a live viewDirectionWS, and T-S1's two edges sit at ~78 km and ~200 km of
        // view depth. With a GLOBAL plateau the sum would integrate alpha·q(y) rather than alpha, and a 0.5 %
        // shading error summed across a 60 px half-band is 0.30 px — the entire error budget. A tight window
        // running inward from the anchor keeps it under 0.03 px: PlateauOnColumn returns the MAX, so the
        // anchor itself reads ≈1.000, and CoverageAt saturates, so an under-reading plateau clamps to 1
        // rather than biasing the sum.

        /// <summary>Default anchor inset — inside / outer sample outside, from the detected edge.
        ///
        /// <para>NOT a free parameter, and NOT the same for every arm since S116. The two half-sums must not
        /// share a row, so the band has to be thicker than <c>2·inset</c>; under the world-width model a band
        /// running across the view azimuth renders <c>cos θ</c> thinner than its styled width, and T-S2's
        /// offset band is now 8 rows, which 4 cannot inset. Each arm derives its own from the band it
        /// MEASURES (<see cref="InsetForBand"/>) rather than assuming one — the alternative, widening the
        /// disjointness assertion, would destroy the estimator's validity condition and make every number the
        /// arm reports meaningless.</para></summary>
        private const int EdgeInsetPx = 4;
        private const int PlateauRows = 6;  // tight, running INWARD from the anchor, the anchor included

        /// <summary>The largest inset (capped at <see cref="EdgeInsetPx"/>) that still leaves the two
        /// half-sum windows DISJOINT on a band spanning <paramref name="bottomRow"/>..<paramref name="topRow"/>:
        /// disjointness needs <c>bottom + inset &lt; top − inset</c>, i.e. <c>2·inset &lt; top − bottom</c>.
        /// The window's other two conditions (anchor fully covered, outer sample fully clear) are checked by
        /// <see cref="AssertWindowIsWellFormed"/>, so an inset that comes out too small to contain the ramp
        /// fails loudly rather than silently biasing the sum.</summary>
        private static int InsetForBand(int bottomRow, int topRow)
            => math.min(EdgeInsetPx, (topRow - bottomRow - 1) / 2);

        private const int CentreColumn = 256;

        // ── HOW WIDE THE COLUMN SWEEP MAY BE, and why it is not the whole band ───────────────────
        //
        // A tilted arm sweeps only ±10 columns about the screen centre. NOT a convenience: at heading 0 the
        // columns are NOT geometrically identical, because MapPixelsToWorld returns the length of a 2D ndc
        // delta. A world step along ±Z at a vertex whose screen-x is `sx` pixels off centre changes that
        // vertex's DEPTH, so the projected point slides radially as well as vertically, and refPx gains a
        // component ≈ |sx|·e px on top of the ~2.9 px vertical span. That is CORRECT — the total screen
        // displacement really is larger off-centre, so a road that must stay N px wide needs fewer world
        // metres there — but it means the silhouettes bow inward with |sx| and the derived expectations,
        // which are stated at sx = 0, do not hold across the frame.
        //
        // MEASURED at the T-S1 pose over the central 200 columns: the absolute silhouettes spread 2.80 px
        // (far) / 2.95 px (near), and R itself spreads 0.0343 — 7× the tooth's whole tolerance — varying
        // very nearly quadratically in sx with its maximum at the centre (R = 1.0191 at sx = 0 falling to
        // 0.9848 at sx = ±100, i.e. a curvature of 3.4e-6 px⁻²). R would be exactly column-independent if
        // both edges of a column belonged to the same road position, but they do not: the two silhouettes
        // sit at different world z, so one screen column cuts them at different road x and therefore at
        // different sx. Within ±10 columns the residual is 3.4e-4, and the mean's own bias is 1.1e-4 —
        // both far under the ±0.005 the tooth discriminates at. T-S2's curvature is 6.6e-5 px⁻², giving a
        // 6.6e-3 residual on a ratio of 11.4.
        //
        // The TOP-DOWN arm keeps the full 200-column sweep: the radial term is proportional to e, which is
        // identically zero there — and it measures 0.0000 px of spread, which is that mechanism's own
        // corroboration rather than an assumption about it.
        private const int TiltedSweepHalfWidth = 10;
        private const int TopDownSweepFrom     = 156;  // the central 200 columns, clear of the butt caps
        private const int TopDownSweepTo       = 355;

        private readonly struct ColumnSilhouettes
        {
            /// <summary>Padded-silhouette SCREEN-Y of the far (up-screen, north) edge.</summary>
            public readonly double FarScreenY;

            /// <summary>Padded-silhouette SCREEN-Y of the near (down-screen, south) edge.</summary>
            public readonly double NearScreenY;

            public ColumnSilhouettes(double farScreenY, double nearScreenY)
            {
                FarScreenY = farScreenY; NearScreenY = nearScreenY;
            }

            public double SeparationPx => FarScreenY - NearScreenY;
        }

        /// <summary>The most saturated pixel anywhere in frame — a coarse plateau, used ONLY to find the
        /// band's rows. Every summed coverage value uses a local per-edge plateau instead.</summary>
        private static float3 CoarsePlateau(byte[] pixels, float3 background)
        {
            float3 best     = background;
            float  bestDist = 0f;
            for (int row = 0; row < Size; row++)
            for (int column = 0; column < Size; column++)
            {
                float3 c    = PixelCoverage.SampleLinear(pixels, Size, Size, column, row);
                float  dist = math.distancesq(c, background);
                if (dist > bestDist) { bestDist = dist; best = c; }
            }
            return best;
        }

        private static ColumnSilhouettes MeasureColumn(
            byte[] pixels, int column, float3 background, float3 coarsePlateau)
            => MeasureColumn(pixels, column, background, coarsePlateau, EdgeInsetPx);

        /// <summary>The band's contiguous ≥0.5-coverage row run on one column. Split out of
        /// <see cref="MeasureColumn"/> so an arm can size its anchor inset from the band it is about to
        /// measure, with one implementation of "where the band is".</summary>
        private static (int bottomRow, int topRow) BandRowSpan(
            byte[] pixels, int column, float3 background, float3 coarsePlateau)
        {
            int topRow = -1, bottomRow = -1, covered = 0;
            for (int row = 0; row < Size; row++)
            {
                if (PixelCoverage.CoverageAt(pixels, Size, Size, column, row, background, coarsePlateau) < 0.5f)
                    continue;
                if (bottomRow < 0) bottomRow = row;
                topRow = row;
                covered++;
            }
            Assert.That(bottomRow, Is.GreaterThanOrEqualTo(0),
                $"column {column}: no rows reach 0.5 coverage — the band did not render here.");
            Assert.That(covered, Is.EqualTo(topRow - bottomRow + 1),
                $"column {column}: the ≥0.5 coverage run must be CONTIGUOUS ({covered} covered rows spanning " +
                $"{bottomRow}..{topRow}). A gap means something other than one solid band is in frame.");
            return (bottomRow, topRow);
        }

        private static ColumnSilhouettes MeasureColumn(
            byte[] pixels, int column, float3 background, float3 coarsePlateau, int edgeInsetPx)
        {
            var (bottomRow, topRow) = BandRowSpan(pixels, column, background, coarsePlateau);

            int anchorFar  = topRow    - edgeInsetPx;
            int outerFar   = topRow    + edgeInsetPx;
            int anchorNear = bottomRow + edgeInsetPx;
            int outerNear  = bottomRow - edgeInsetPx;

            // No sample may be claimed by both half-sums, or the two S's double-count and the recovered
            // separation gains a pixel per shared row. The inset is DERIVED from the band the arm measures
            // (InsetForBand), so this fires only when a band is too thin for any inset at all — never
            // because a hard-coded 4 outgrew the band.
            Assert.That(anchorNear, Is.LessThan(anchorFar),
                $"column {column}: the two ±{edgeInsetPx} px windows must be DISJOINT; the band spans " +
                $"{bottomRow}..{topRow}, which is too thin to inset both anchors.");

            float3 plateauFar = PixelCoverage.PlateauOnColumn(
                pixels, Size, Size, column,
                math.max(anchorFar - (PlateauRows - 1), bottomRow + 1), anchorFar, background);
            float3 plateauNear = PixelCoverage.PlateauOnColumn(
                pixels, Size, Size, column,
                anchorNear, math.min(anchorNear + (PlateauRows - 1), topRow - 1), background);

            double sFar = 0.0;
            for (int row = anchorFar; row <= outerFar; row++)
                sFar += PixelCoverage.CoverageAt(pixels, Size, Size, column, row, background, plateauFar);

            double sNear = 0.0;
            for (int row = outerNear; row <= anchorNear; row++)
                sNear += PixelCoverage.CoverageAt(pixels, Size, Size, column, row, background, plateauNear);

            AssertWindowIsWellFormed(pixels, column, "far",  anchorFar,  outerFar,  background, plateauFar);
            AssertWindowIsWellFormed(pixels, column, "near", anchorNear, outerNear, background, plateauNear);

            return new ColumnSilhouettes(
                farScreenY:  (anchorFar  + 0.5) + sFar,
                nearScreenY: (anchorNear + 0.5) - sNear);
        }

        /// <summary>The estimator's two standing assumptions, checked rather than assumed: the anchor is
        /// fully covered and the outer sample is fully clear, so the whole ramp lies inside the window.</summary>
        private static void AssertWindowIsWellFormed(
            byte[] pixels, int column, string edge, int anchor, int outer, float3 background, float3 plateau)
        {
            float atAnchor = PixelCoverage.CoverageAt(pixels, Size, Size, column, anchor, background, plateau);
            float atOuter  = PixelCoverage.CoverageAt(pixels, Size, Size, column, outer,  background, plateau);
            if (atAnchor >= 0.99f && atOuter <= 0.01f) return;

            int from = math.min(anchor, outer), to = math.max(anchor, outer);
            float[] profile = PixelCoverage.CoverageProfileOnColumn(
                pixels, Size, Size, column, from, to, background, plateau);
            Assert.Fail(
                $"column {column}, {edge} edge: the coverage window [{from}..{to}] is not well formed — " +
                $"anchor[{anchor}] = {atAnchor:F4} (want ≥ 0.99), outer[{outer}] = {atOuter:F4} " +
                $"(want ≤ 0.01). The half-sum estimator is exact only when the whole ramp lies strictly " +
                $"inside the window. Profile: {PixelCoverage.FormatProfile(profile, from)}");
        }

        /// <summary>World z of the ground line that projects onto <paramref name="screenY"/>, plus the local
        /// scale there in metres per screen pixel (a central difference across one pixel).</summary>
        private static (double worldZ, double metresPerPixel) SolveEdge(
            Camera cam, double screenY, double loMetres, double hiMetres)
        {
            double z     = GroundRowSolver.SolveWorldZForRow(cam, screenY,        loMetres, hiMetres);
            double above = GroundRowSolver.SolveWorldZForRow(cam, screenY + 0.5,  loMetres, hiMetres);
            double below = GroundRowSolver.SolveWorldZForRow(cam, screenY - 0.5,  loMetres, hiMetres);
            return (z, above - below);
        }

        // ── T-S1: the ribbon's two edges must be equidistant from the centreline ─────────────────

        private const double TS1WidthPx = 120.0;

        /// <summary>
        /// <b>T-S1 (REQUIRED) — the SIGN term, at its source.</b> An unoffset ribbon's two padded
        /// silhouettes must sit at equal WORLD distance either side of the centreline:
        /// <c>R = zFar / |zNear| == 1</c>.
        ///
        /// <para>MECHANISM. The two vertices of a station share one <c>centerWS</c> and carry opposite
        /// <c>extrudeN</c>, so the shader hands <c>MapPixelsToWorld</c> the same axis with opposite signs.
        /// The one-sided probe steps refMag metres ALONG that signed direction, so it travels away from the
        /// camera for one vertex and toward it for the other, and the span it measures carries the probe's
        /// own foreshortening: <c>zFar = H·k·(1+e)</c>, <c>zNear = −H·k·(1−e)</c>, hence
        /// <c>R = (1+e)/(1−e)</c>. Note what R is free of — h, k, the AA pad and the depth all cancel — so
        /// this reads 1.0190982 whatever the styled width or the camera altitude.</para>
        ///
        /// <para>The FAR (north, up-screen) edge is the wider one, because <c>across·fwd = +sin 55°</c>
        /// there: its probe steps away from the camera, into a longer ruler.</para>
        ///
        /// <para>RED at 1.019098 against a ±0.005 tolerance — a 3.8× margin — with the four step-5
        /// injections reading 1.019098 / 0.981260 / 1.038561 / 1.000000.</para>
        ///
        /// <para><b>THE ORIGINAL INVARIANT IS RETIRED (S116). Everything above describes what this arm USED
        /// to catch; read it as history.</b> The styled WIDTH no longer calls <c>MapPixelsToWorld</c> at all —
        /// both edges take the one frame constant — so <c>R == 1</c> now holds for the width BY CONSTRUCTION
        /// and cannot fail for the reason the arm was written.</para>
        ///
        /// <para><b>And the survivor is weak — stated with the arithmetic, because "it still guards the AA
        /// pad" would be over-claiming.</b> The pad is legitimately per-vertex and is still handed the two
        /// opposite signs, so a w-ratio regression does still move R — but only through the pad, at
        /// <c>2·e·p/(H+p)</c> with <c>p = 0.5·K/cos θ ≈ 266.5 m</c> and <c>H = 60·K ≈ 18 345 m</c>, i.e.
        /// <b>2.7e-4</b> against this assertion's ±0.005. It is a factor of 18 UNDER the tolerance: at this
        /// styled width the R clause <b>cannot</b> catch the mechanism, and no honest tightening reaches it
        /// either (the measured 5.5e-5 is estimator noise, so the discriminating band is thinner than the
        /// noise floor). Restoring amplitude needs <c>p/H</c> of order 1, i.e. a 1–2 px band — which this
        /// half-sum estimator cannot measure, since it needs ±4 px anchor windows. The two are structurally
        /// incompatible in one fixture.</para>
        ///
        /// <para><b>So what is this arm for now?</b> Two live clauses, and the R assertion demoted to a
        /// gross-error bound:
        /// <list type="number">
        /// <item>the <b>framing</b> precondition, which pins the <c>cos θ</c> world-width behaviour and is
        /// RED-verified — it reads 125.77 px against [68, 73] when a per-vertex width is injected;</item>
        /// <item>the <b>uniformity</b> clause, a precondition that the band is uniform where it is
        /// measured;</item>
        /// <item>R itself, kept only as a coarse sanity bound — it would still catch a catastrophic
        /// asymmetry (a sign flip, a dropped correction of order 1), and it costs nothing on a render this
        /// arm performs anyway. <b>It is NOT the direction-symmetry discriminator any more.</b></item>
        /// </list>
        /// Direction symmetry at pad amplitude is currently pinned NOWHERE in a render —
        /// <c>Estimator_…_TopDown</c> is a control with <c>e ≡ 0</c> by construction, so it cannot see it
        /// either. Recorded as a gap rather than papered over; closing it needs a fixture that can resolve a
        /// pad-sized effect, which is not this one.</para>
        /// </summary>
        [Test]
        public void RibbonEdges_AreEquidistantFromTheCentreline_UnderTilt()
        {
            WithRenderedRoad(TiltDeg, TS1WidthPx, lineOffsetPx: 0.0, "s111-ts1-symmetry-tilt55.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);
                    float3 coarse     = CoarsePlateau(pixels, background);
                    Assert.That(math.distance(coarse, background), Is.GreaterThan(0.02f),
                        "T-S1: the road did not render — nothing is measurable.");

                    var centre = MeasureColumn(pixels, CentreColumn, background, coarse);
                    var (zFar,  farMetresPerPx)  =
                        SolveEdge(scene.UnityCamera, centre.FarScreenY,  -60_000.0, +60_000.0);
                    var (zNear, nearMetresPerPx) =
                        SolveEdge(scene.UnityCamera, centre.NearScreenY, -60_000.0, +60_000.0);

                    TestContext.WriteLine(
                        $"T-S1: centre column silhouettes screen-y {centre.FarScreenY:F4} / " +
                        $"{centre.NearScreenY:F4} (separation {centre.SeparationPx:F4} px); world z " +
                        $"{zFar:F1} / {zNear:F1} m; local scale {farMetresPerPx:F1} / {nearMetresPerPx:F1} m/px");

                    // RE-DERIVED for the world-width model (S116). It used to read [122, 129] — i.e. the
                    // styled 120 px plus the pad, the CONSTANT-DEVICE-WIDTH premise this epic deleted. A
                    // styled px width now fixes a WORLD width at the look-at, and this road runs ACROSS the
                    // view azimuth, so its across-axis lies in the ground plane along the tilt direction and
                    // picks up that plane's foreshortening: 120·cos 55° + 1 = 69.83 px to first order.
                    // Measured 70.73, ~0.9 px over, and that residual is expected rather than slack: the two
                    // silhouettes sit at different DEPTHS (~78 km and ~200 km of view depth here), so neither
                    // edge's own foreshortening is exactly the look-at's cos θ, and the AA pad each edge
                    // carries is measured at its own depth too.
                    Assert.That(centre.SeparationPx, Is.InRange(68.0, 73.0),
                        $"T-S1 framing: the padded band renders {centre.SeparationPx:F2} px thick, expected " +
                        "in [68, 73] (120·cos 55° + 1 = 69.83 to first order, 70.73 measured). A reading " +
                        "near 121 would mean the band is holding a constant DEVICE width under tilt — the " +
                        "compensation this epic reverted. If the fixture will not frame, RE-DERIVE from the " +
                        "pose; never widen the window to reach it.");

                    // Only ±10 columns about the screen centre — see TiltedSweepHalfWidth for why the frame
                    // is not column-uniform under tilt. The sweep is a PRECONDITION that the band is uniform
                    // where it is measured, not an error bar: averaging cannot reduce a systematic, because
                    // every column lands on the identical sub-pixel phase.
                    var ratios = new List<double>();
                    double minFar = double.MaxValue, maxFar = double.MinValue;
                    double minNear = double.MaxValue, maxNear = double.MinValue;
                    for (int column = CentreColumn - TiltedSweepHalfWidth;
                         column <= CentreColumn + TiltedSweepHalfWidth; column++)
                    {
                        var s = MeasureColumn(pixels, column, background, coarse);
                        double far  = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.FarScreenY,  -60_000.0, +60_000.0);
                        double near = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.NearScreenY, -60_000.0, +60_000.0);
                        ratios.Add(far / math.abs(near));
                        minFar  = math.min(minFar,  s.FarScreenY);  maxFar  = math.max(maxFar,  s.FarScreenY);
                        minNear = math.min(minNear, s.NearScreenY); maxNear = math.max(maxNear, s.NearScreenY);
                    }

                    double minRatio = double.MaxValue, maxRatio = double.MinValue, sum = 0.0;
                    foreach (double r in ratios)
                    {
                        minRatio = math.min(minRatio, r);
                        maxRatio = math.max(maxRatio, r);
                        sum += r;
                    }
                    double ratio = sum / ratios.Count;

                    TestContext.WriteLine(
                        $"T-S1: {ratios.Count} columns swept; R ∈ [{minRatio:F6}, {maxRatio:F6}], mean " +
                        $"{ratio:F6}; absolute silhouette spread far {maxFar - minFar:F3} px, near " +
                        $"{maxNear - minNear:F3} px");

                    Assert.That(maxRatio - minRatio, Is.LessThan(0.002),
                        $"T-S1 uniformity: R varies {maxRatio - minRatio:F6} over ±{TiltedSweepHalfWidth} " +
                        "columns, where the modelled residual of the radial refPx term is 3.4e-4 and " +
                        "per-column quantisation is ~3e-4. More than that means the band is not uniform " +
                        "where it is being measured, and the derived expectation does not apply.");

                    // A COARSE SANITY BOUND, not the sign-asymmetry discriminator it once was — see the
                    // arithmetic in the summary: through the pad alone the mechanism is worth 2.7e-4 here,
                    // 18x under this tolerance. Do not read a pass as evidence the w-ratio is intact.
                    Assert.That(ratio, Is.EqualTo(1.0).Within(0.005),
                        $"GROSS ASYMMETRY (the width path can no longer cause this — see the summary): on " +
                        $"the CENTRE COLUMN the ribbon's far (north) silhouette sits " +
                        $"{zFar:F1} m from the centreline and its near (south) silhouette " +
                        $"{math.abs(zNear):F1} m, a ratio of {zFar / math.abs(zNear):F6}; the asserted value " +
                        $"is the MEAN over the swept columns, {ratio:F6} — do not divide the two world " +
                        "figures above and expect it. The two edges of one band must be equidistant. " +
                        "MapPixelsToWorld " +
                        "probes ONE-SIDED along the signed dirWS, so the far edge's probe steps AWAY from " +
                        "the camera and measures a span carrying its own foreshortening (ruler ×(1+e)) " +
                        "while the near edge's steps toward it (×(1−e)); e = 0.02·tan(fov/2)·(across·fwd) = " +
                        "0.0094588 here, so R = (1+e)/(1−e) = 1.019098. Multiplying the measured span by " +
                        "clipRef.w/clipCenter.w divides that factor back out identically for both signs.");
                });
        }

        // ── T-S1c: what surface the estimator recovers, and that the fix is inert where e ≡ 0 ────

        /// <summary>
        /// <b>T-S1c (REQUIRED — calibration + control).</b> The same fixture TOP-DOWN, carrying two clauses
        /// on one render.
        ///
        /// <para><b>Clause 1, CALIBRATION</b> — the load-bearing one, because it settles by measurement which
        /// of three candidate surfaces §5.2's half-sum recovers. At tilt 0 the ground is parallel to the
        /// image plane, so the scale is uniform and the two padded silhouettes sit at exactly ±(W/2 + 0.5)
        /// device px. Three readings, three different meanings:
        /// <c>121.0</c> — the estimator recovers the PADDED SILHOUETTE (correct);
        /// <c>120.0</c> — it recovered the styled / 0.5-coverage edge (the anchor dropped from one half-sum,
        /// or a stray −0.5); <c>122.0</c> — an anchor was counted in BOTH half-sums.
        /// Both wrong readings are RED-verified.</para>
        ///
        /// <para><b>Clause 2, THIS ARM IS THE CONTROL.</b> <c>across ⊥ fwd</c> top-down, so e ≡ 0 and the
        /// S111 correction is exactly 1.0 — R reads 1.000 before AND after the fix. CAVEAT, stated so the
        /// arm is not over-read: at tilt 0 both edges sit at equal depth, so a COMMON error — the
        /// index→screen-y +0.5 included — cancels in both clauses. Clause 2 pins that the fix is inert where
        /// e ≡ 0; clause 1 pins the surface and the partition; neither pins the +0.5, which is pinned by
        /// construction and by review.</para>
        /// </summary>
        [Test]
        public void Estimator_RecoversThePaddedSilhouette_AndTheFixIsInert_TopDown()
        {
            WithRenderedRoad(0.0, TS1WidthPx, lineOffsetPx: 0.0, "s111-ts1c-calibration-topdown.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);
                    float3 coarse     = CoarsePlateau(pixels, background);
                    Assert.That(math.distance(coarse, background), Is.GreaterThan(0.02f),
                        "T-S1c: the road did not render — nothing is measurable.");

                    var separations = new List<double>();
                    var ratios      = new List<double>();
                    double minFar = double.MaxValue, maxFar = double.MinValue;
                    double minNear = double.MaxValue, maxNear = double.MinValue;
                    for (int column = TopDownSweepFrom; column <= TopDownSweepTo; column++)
                    {
                        var s = MeasureColumn(pixels, column, background, coarse);
                        separations.Add(s.SeparationPx);
                        double far  = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.FarScreenY,  -60_000.0, +60_000.0);
                        double near = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.NearScreenY, -60_000.0, +60_000.0);
                        ratios.Add(far / math.abs(near));
                        minFar  = math.min(minFar,  s.FarScreenY);  maxFar  = math.max(maxFar,  s.FarScreenY);
                        minNear = math.min(minNear, s.NearScreenY); maxNear = math.max(maxNear, s.NearScreenY);
                    }

                    double separation = Mean(separations);
                    double ratio      = Mean(ratios);
                    TestContext.WriteLine(
                        $"T-S1c: {separations.Count} columns; padded separation mean {separation:F4} px " +
                        $"(styled width {TS1WidthPx}); R mean {ratio:F6}; absolute silhouette spread far " +
                        $"{maxFar - minFar:F4} px, near {maxNear - minNear:F4} px");

                    // Top-down, e ≡ 0 exactly, so unlike T-S1 the absolute silhouettes ARE column-uniform:
                    // the radial refPx term is proportional to e. A spread here is estimator noise.
                    Assert.That(maxFar - minFar, Is.LessThan(0.05),
                        $"T-S1c uniformity: the far silhouette varies {maxFar - minFar:F4} px across columns " +
                        "where the geometry is identical. A PRECONDITION that the band is horizontal and " +
                        "uniform — not an error bar.");
                    Assert.That(maxNear - minNear, Is.LessThan(0.05),
                        $"T-S1c uniformity: the near silhouette varies {maxNear - minNear:F4} px.");

                    Assert.That(separation, Is.EqualTo(121.0).Within(0.15),
                        $"CALIBRATION: the estimator recovered a {separation:F4} px separation on a " +
                        $"{TS1WidthPx} px styled band viewed top-down, where the two PADDED silhouettes sit " +
                        "at exactly ±(W/2 + 0.5) = ±60.5 px. 121.0 means the half-sum lands on the padded " +
                        "silhouette, which is the surface T-S1 and T-S2 derive their expectations on. 120.0 " +
                        "would mean it landed on the styled / 0.5-coverage edge (the anchor dropped from one " +
                        "half-sum, or a stray −0.5); 122.0 would mean an anchor was counted in BOTH " +
                        "half-sums. Those are convention faults, not tolerance problems — do not widen this.");

                    Assert.That(ratio, Is.EqualTo(1.0).Within(0.005),
                        $"THIS ARM IS THE CONTROL: top-down, across ⊥ fwd, so e ≡ 0, the w-ratio is exactly " +
                        $"1.0 and the ribbon is symmetric before AND after S111. R = {ratio:F6}. A move here " +
                        "means the fix is not inert where it must be.");
                });
        }

        // ── T-S2: line-offset must not leak into the half-width ──────────────────────────────────

        private const double TS2WidthPx  =  24.0;
        private const double TS2OffsetPx = 160.0;

        /// <summary>
        /// <b>T-S2 (REQUIRED) — the UNBOUNDED consumer.</b> <c>_LineOffset</c> is the one consumer of
        /// <c>pxToWorld</c> that is NOT paired across the ribbon: both station vertices take the SAME common
        /// offset, each measured with its OWN ruler, so the sign asymmetry lands in the ribbon's HALF-WIDTH
        /// instead of cancelling there.
        ///
        /// <para>With <c>zFar = k(1+e)(L+H)</c> and <c>zNear = k(1−e)(L−H)</c>:
        /// <c>centre = k(L + e·H)</c>, <c>half = k(H + e·L)</c>, and their ratio
        /// <c>(L + e·H)/(H + e·L)</c> is dimensionless — k cancels, so no projection number enters the
        /// expectation. The claim in words: THE RIBBON'S WORLD HALF-WIDTH MUST NOT DEPEND ON ITS OFFSET.
        /// <c>e·L</c> leaking into it was 12.11 % at L = 160, and UNBOUNDED in L, because nothing bounds
        /// it by the styled width — while the centre errs only <c>e·H/L</c> = 0.074 %.</para>
        ///
        /// <para><b>S116 — the expectation moves from L/(W/2 + 0.5) to L/(W/2), and why that is a
        /// re-derivation and not a re-bake.</b> Width and offset now convert with the frame constant while the
        /// AA pad keeps its per-vertex measurement, so the two no longer share one <c>k</c> and the pad stops
        /// cancelling: the recovered PADDED half-width is <c>K·W/2 + 0.5·k</c>, mixing rulers that differ by
        /// <c>1/cos θ</c> under tilt (~7 % of the half-width on a 24 px band). The measurement subtracts the
        /// pad — read off the live camera at the UNSHIFTED centreline, which is where the shader measures it —
        /// and the assertion is then on the STYLED half-width, which is what the claim was always about. The
        /// dimensionless form survives; only the pad term leaves it.</para>
        ///
        /// <para>NAME AND SCOPE, deliberately narrow: this tooth measures a WORLD-space coupling about the
        /// original centreline. It neither claims nor delivers screen-width preservation — the band is at a
        /// different depth from the centreline the width was fixed at, so its rendered device width is
        /// smaller and MOVES with the offset by construction. Whether line-offset should re-measure at the
        /// SHIFTED position is a spec question about what line-offset means under perspective, filed and out
        /// of scope.</para>
        ///
        /// <para>RED at 11.426080 against 12.800 ± 2.5 % — a 4.3× margin — under the pre-S116 formulation.
        /// The same injection against the current formulation is larger, not smaller: the leak adds
        /// <c>e·L·K</c> to a half-width that no longer carries the pad, ~29 %.</para>
        /// </summary>
        [Test]
        public void OffsetRibbon_HalfWidthDoesNotTrackTheOffset_UnderTilt()
        {
            WithRenderedRoad(TiltDeg, TS2WidthPx, TS2OffsetPx, "s111-ts2-offset-halfwidth-tilt55.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);
                    float3 coarse     = CoarsePlateau(pixels, background);
                    Assert.That(math.distance(coarse, background), Is.GreaterThan(0.02f),
                        "T-S2: the road did not render — nothing is measurable.");

                    // The bracket is a PAIR, not a symmetric ±: the offset band sits ~90 km NORTH, while the
                    // camera plane crosses the ground at z ≈ −165 501 m and WorldToScreenPoint returns a
                    // mirrored, non-monotone y beyond it — a symmetric ±200 km bracket would void the
                    // bisection silently.
                    const double Lo = -60_000.0, Hi = +200_000.0;

                    // The anchor inset is DERIVED from the band this arm actually renders, not assumed.
                    // Under the world-width model a 24 px styled band running across the view azimuth
                    // renders ~cos 55° thinner (8 rows here, against 12 before), and the default ±4 px
                    // windows would then overlap. Widening the disjointness assertion instead is forbidden:
                    // that assertion IS the half-sum estimator's validity condition, and loosening it makes
                    // every world figure this arm reports meaningless. Nothing pinned moves — the asserted
                    // ratio below is L/H, dimensionless, and the inset does not appear in it.
                    var (bandBottom, bandTop) = BandRowSpan(pixels, CentreColumn, background, coarse);
                    int inset = InsetForBand(bandBottom, bandTop);
                    TestContext.WriteLine(
                        $"T-S2: centre band spans rows {bandBottom}..{bandTop} ({bandTop - bandBottom + 1} " +
                        $"rows); anchor inset {inset} px (default {EdgeInsetPx})");
                    Assert.That(inset, Is.GreaterThanOrEqualTo(2),
                        $"T-S2: the band spans only {bandTop - bandBottom + 1} rows, leaving an anchor inset " +
                        $"of {inset} px — too thin to contain the one-device-pixel ramp with any margin. " +
                        "RAISE the styled width in this arm and re-derive; do not shrink the inset further.");

                    var centre = MeasureColumn(pixels, CentreColumn, background, coarse, inset);
                    var (zFarCentre,  farMetresPerPx)  = SolveEdge(scene.UnityCamera, centre.FarScreenY,  Lo, Hi);
                    var (zNearCentre, nearMetresPerPx) = SolveEdge(scene.UnityCamera, centre.NearScreenY, Lo, Hi);

                    // ── The AA pad is no longer on the width's ruler, so it must be subtracted (S116) ──
                    // The estimator recovers the PADDED silhouette (T-S1c pins that), and the pad is half a
                    // device pixel measured PER VERTEX by MapPixelsToWorld at the UNSHIFTED centreline —
                    // whereas the styled half-width is now W/2 × the frame constant. Under tilt those two
                    // rulers differ by 1/cos θ (a ground across-axis foreshortens; the frame constant does
                    // not), so on a 24 px band the pad is ~7 % of the half-width and no longer cancels the way
                    // it did when width and pad shared one measurement. Subtracting it recovers the STYLED
                    // half-width, which is what the claim below is actually about.
                    //
                    // MapPixelsToWorld along `across` at a ground point IS d(world z)/d(screen y) there, which
                    // SolveEdge already computes — so this is read off the same live camera as everything
                    // else, not modelled.
                    double centreScreenY = scene.UnityCamera.WorldToScreenPoint(Vector3.zero).y;
                    var (_, centrelineMetresPerPx) = SolveEdge(scene.UnityCamera, centreScreenY, Lo, Hi);
                    double halfPadWorld = 0.5 * math.abs(centrelineMetresPerPx);

                    TestContext.WriteLine(
                        $"T-S2: centre column silhouettes screen-y {centre.FarScreenY:F4} / " +
                        $"{centre.NearScreenY:F4} (separation {centre.SeparationPx:F4} px); world z " +
                        $"{zFarCentre:F1} / {zNearCentre:F1} m; local scale {farMetresPerPx:F1} / " +
                        $"{nearMetresPerPx:F1} m/px");

                    Assert.That(centre.SeparationPx, Is.InRange(6.5, 11.0),
                        $"T-S2 framing: the padded band renders {centre.SeparationPx:F2} px thick, expected " +
                        "in [6.5, 11]. RE-DERIVED, with the arithmetic, for the world-width model (S116): " +
                        "the styled half-width is a fixed 24/2 × K = 12 K metres (K = 305.75 m/device px at " +
                        "this pose) and the pad ~0.5 × K/cos 55° , so the band is ~3.93 km wide in the world " +
                        "wherever it sits. It is offset 160 K = 48.9 km NORTH, which at tilt 55° puts it at " +
                        "view depth 135.6 + 0.819×48.9 = 175.6 km against the look-at's 135.6 km — a factor " +
                        "of 1.295 further away — so it renders 14.8/1.295 ≈ 11.4 px before the ground " +
                        "plane's own cos 55° foreshortening of the across-axis brings it to ~8.8 (measured " +
                        "8.794). The window brackets that; it MOVES with the offset by construction, which " +
                        "is why the tooth below measures a dimensionless ratio instead. " +
                        "If the fixture will not frame, change L and RE-DERIVE; never widen the tolerance.");

                    var ratios = new List<double>();
                    for (int column = CentreColumn - TiltedSweepHalfWidth;
                         column <= CentreColumn + TiltedSweepHalfWidth; column++)
                    {
                        var s = MeasureColumn(pixels, column, background, coarse, inset);
                        double far  = GroundRowSolver.SolveWorldZForRow(scene.UnityCamera, s.FarScreenY,  Lo, Hi);
                        double near = GroundRowSolver.SolveWorldZForRow(scene.UnityCamera, s.NearScreenY, Lo, Hi);
                        ratios.Add(0.5 * (far + near) / (0.5 * (far - near) - halfPadWorld));
                    }

                    double minRatio = double.MaxValue, maxRatio = double.MinValue;
                    foreach (double r in ratios)
                    {
                        minRatio = math.min(minRatio, r);
                        maxRatio = math.max(maxRatio, r);
                    }
                    double ratio = Mean(ratios);

                    double centreWorld     = 0.5 * (zFarCentre + zNearCentre);
                    double paddedHalfWorld = 0.5 * (zFarCentre - zNearCentre);
                    double halfWorld       = paddedHalfWorld - halfPadWorld;
                    double expected        = TS2OffsetPx / (TS2WidthPx * 0.5);
                    TestContext.WriteLine(
                        $"T-S2: {ratios.Count} columns; centre/half ∈ [{minRatio:F6}, {maxRatio:F6}], mean " +
                        $"{ratio:F6} (want {expected:F6}); centre column world centre {centreWorld:F1} m, " +
                        $"padded half {paddedHalfWorld:F1} m − pad {halfPadWorld:F1} m = styled half " +
                        $"{halfWorld:F1} m");

                    Assert.That(maxRatio - minRatio, Is.LessThan(0.05),
                        $"T-S2 uniformity: centre/half varies {maxRatio - minRatio:F6} over " +
                        $"±{TiltedSweepHalfWidth} columns, where the radial refPx term contributes 6.6e-3 " +
                        "and edge-localisation noise on a 7.5 km half-width contributes ~0.015. A " +
                        "PRECONDITION that the band is uniform where it is measured, not an error bar.");

                    Assert.That(ratio, Is.EqualTo(expected).Within(2.5).Percent,
                        $"THE OFFSET LEAK: the offset ribbon's world centre sits {centreWorld:F1} m out with " +
                        $"a STYLED half-width of {halfWorld:F1} m, a ratio of {ratio:F6} where L/(W/2) = " +
                        $"{expected:F3} — the half-width must not depend on the offset at all. _LineOffset is " +
                        "applied as a COMMON world vector to both station vertices; if each were to measure " +
                        "its own signed ruler, an offset of L device px would leak e·L into the HALF-width — " +
                        "12.11 % at this pose, and unbounded in L, because nothing bounds it by the styled " +
                        "width. This is the consumer whose error is NOT bounded by the styled width.");
                });
        }

        // ── T4: renderedWidthPx × depth is constant along a receding road ────────────────────────

        private const double T4WidthPx = 40.0;

        /// <summary>The rows swept. Chosen from the pose rather than by eye: at tilt 55 with fov 60 the frame
        /// spans view depths of roughly 74 km (bottom) to 890 km (top), and rows 140…380 cover a ≥ 2× range
        /// while keeping the widest band (bottom, ~55 px) and the narrowest (top, ~24 px) both comfortably
        /// measurable. The sweep straddles the look-at row (256), where the rendered width is the styled
        /// width exactly.</summary>
        private static readonly int[] T4Rows = { 140, 200, 260, 320, 380 };

        /// <summary>Rendered band width in device px on one screen ROW: the coverage integral across the
        /// whole row. For a ramp that is one device pixel wide and CENTRED on the styled edge, the integral
        /// is the styled (apparent) width whatever the sub-pixel phase — the AA pad contributes nothing, so
        /// this measures the extrusion and not the straddle.
        ///
        /// <para>The plateau is taken LOCALLY, on this row: the shader is real PBR with a live
        /// viewDirectionWS, and this fixture's rows are at wildly different depths, so one global plateau
        /// would fold a shading gradient into every width.</para></summary>
        private static double MeasureRowWidthPx(byte[] pixels, int row, float3 background)
        {
            float3 plateau = background;
            float  best    = 0f;
            for (int column = 0; column < Size; column++)
            {
                float3 sample = PixelCoverage.SampleLinear(pixels, Size, Size, column, row);
                float  dist   = math.distancesq(sample, background);
                if (dist > best) { best = dist; plateau = sample; }
            }
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"row {row}: nothing on this row is distinguishable from the background — the road did not " +
                "render here, so no width is measurable.");

            double sum = 0.0;
            for (int column = 0; column < Size; column++)
                sum += PixelCoverage.CoverageAt(pixels, Size, Size, column, row, background, plateau);
            return sum;
        }

        /// <summary>View depth (== <c>clip.w</c> for a standard perspective projection) of the ground point
        /// that projects onto <paramref name="screenY"/> on the road's own column.</summary>
        private static double DepthAtRow(Camera cam, double screenY, out double worldZ)
        {
            worldZ = GroundRowSolver.SolveWorldZForRow(cam, screenY, -120_000.0, +2_000_000.0);
            Vector3 p = new Vector3(0f, 0f, (float)worldZ);
            return Vector3.Dot(p - cam.transform.position, cam.transform.forward);
        }

        /// <summary>
        /// <b>T4 (REQUIRED) — the epic's central claim, pinned.</b> On a road RECEDING from a tilted camera,
        /// <c>renderedWidthPx × depth</c> is CONSTANT along its length.
        ///
        /// <para>That product is the signature of a fixed WORLD width under the perspective divide, and
        /// nothing else: a world half-width <c>h</c> at view depth <c>d</c> renders
        /// <c>h · (viewportPx.y / (2·d·tan(fov/2)))</c> device px, so <c>widthPx · d</c> is
        /// <c>h · viewportPx.y / (2·tan(fov/2))</c> — free of depth, of the row, and of where in the frame
        /// the station lands. `line-width: N px` fixes that <c>h</c> once, at the look-at, and the divide
        /// does the rest.</para>
        ///
        /// <para><b>What it rejects.</b> Any per-vertex px→world conversion for the WIDTH — the four reverted
        /// stages, and the <c>MapPixelsToWorld</c> fallback branch the shader keeps only as a missing-push
        /// backstop — holds <c>widthPx</c> itself constant instead, so the product tracks depth and spreads by
        /// the sweep's full depth ratio (&gt; 2×) rather than ~1 %. There is no way to pass both. A tooth
        /// asserting "the band is N device px at every depth" would assert exactly the thing this rejects,
        /// which is how four stages measured green against a visibly wrong render.</para>
        ///
        /// <para>The near/far WIDTH ratio is recorded rather than gated: it is the independent cross-check
        /// against the reference renderer's ~3.375 at maximum pitch, and it is a property of this fixture's
        /// depth range, not a target to tune to.</para>
        /// </summary>
        [Test]
        public void RenderedWidthTimesDepth_IsConstantAlongARecedingRoad_UnderTilt()
        {
            WithRenderedRecedingRoad(TiltDeg, T4WidthPx, "s116-t4-width-times-depth-tilt55.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);

                    var widths   = new List<double>();
                    var depths   = new List<double>();
                    var products = new List<double>();
                    var report   = new System.Text.StringBuilder();

                    foreach (int row in T4Rows)
                    {
                        // RawPixels row j reports the shader at SCREEN-Y j + 0.5 (bottom-up, no flip).
                        double depth = DepthAtRow(scene.UnityCamera, row + 0.5, out double worldZ);
                        double width = MeasureRowWidthPx(pixels, row, background);
                        widths.Add(width);
                        depths.Add(depth);
                        products.Add(width * depth);
                        report.Append(
                            $"[row {row}] z {worldZ / 1000.0:F1} km, depth {depth / 1000.0:F1} km, " +
                            $"width {width:F3} px, w·d {width * depth / 1000.0:F1} → ");
                    }

                    double minProduct = double.MaxValue, maxProduct = double.MinValue;
                    double minDepth   = double.MaxValue, maxDepth   = double.MinValue;
                    double minWidth   = double.MaxValue, maxWidth   = double.MinValue;
                    for (int i = 0; i < products.Count; i++)
                    {
                        minProduct = math.min(minProduct, products[i]);
                        maxProduct = math.max(maxProduct, products[i]);
                        minDepth   = math.min(minDepth,   depths[i]);
                        maxDepth   = math.max(maxDepth,   depths[i]);
                        minWidth   = math.min(minWidth,   widths[i]);
                        maxWidth   = math.max(maxWidth,   widths[i]);
                    }
                    double productSpread = maxProduct / minProduct;
                    double depthSpread   = maxDepth   / minDepth;
                    double widthSpread   = maxWidth   / minWidth;

                    TestContext.WriteLine(
                        $"T4: {report}depth spread {depthSpread:F3}×, WIDTH spread {widthSpread:F3}× " +
                        $"(near/far — cross-check against the reference's ~3.375 at max pitch), " +
                        $"w·d spread {productSpread:F5}×");

                    // Vacuity guards. Both are preconditions on the FIXTURE, not results.
                    Assert.That(depthSpread, Is.GreaterThan(2.0),
                        $"T4: the swept rows span only a {depthSpread:F3}× depth range. Below 2× the " +
                        "constant-device-width model and the constant-world-width model are too close to " +
                        "tell apart, and the tooth would pass under both.");
                    Assert.That(minWidth, Is.GreaterThan(4.0),
                        $"T4: the narrowest measured band is {minWidth:F2} px. Below a few pixels the " +
                        "coverage integral stops resolving the silhouette and the product is noise.");

                    // 1.005 against a MEASURED 1.00022 — a 22× margin under the bound, and the injected
                    // defect reads the full depth spread (2.290×), so the tooth discriminates by 3 orders of
                    // magnitude. Tight on purpose: a PARTIAL compensation would sit between the two, and a
                    // loose bound is exactly how the reverted stages measured green.
                    Assert.That(productSpread, Is.LessThan(1.005),
                        $"THE WIDTH MODEL: renderedWidthPx × depth varies by {productSpread:F5}× over a " +
                        $"{depthSpread:F3}× depth range. It must be constant to ~1 %, because a styled px " +
                        $"width fixes a WORLD width once and the perspective divide alone renders it. A " +
                        $"spread approaching the depth spread ({depthSpread:F3}×) means the width is being " +
                        "held constant in DEVICE pixels at every depth — a per-vertex px→world conversion, " +
                        "which is the defect four reverted stages were built on. Measured widths: " +
                        $"{minWidth:F2}…{maxWidth:F2} px. {report}");
                });
        }

        private static double Mean(List<double> values)
        {
            double sum = 0.0;
            foreach (double v in values) sum += v;
            return sum / values.Count;
        }
    }
}
#endif // UNITY_EDITOR
