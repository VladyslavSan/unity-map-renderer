// Unity EditMode only — real MapCamera + Camera/RenderTexture, off-screen GPU render + CPU readback.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// S110 — the RENDERED teeth for world-anchored line dashes.
//
// THE DEFECT, IN ONE LINE: dashU divided by widthWorld, i.e. by MapPixelsToWorld's PER-VERTEX screen
// measurement. That measurement varies four ways, and dashU is the one consumer that INTEGRATES the
// variation along the road instead of being bounded by the styled width:
//   1. with DEPTH                     — the world period grew with distance; the pattern crawled under tilt.
//   2. with DIRECTION                 — measured along `across` while dashes run `along`.
//   3. with the SIGN of the direction — HISTORICAL: S111 removed this term at source. The two ribbon
//                                       vertices of a station share one centreline point and carry opposite
//                                       extrudeN, so MapPixelsToWorld probed ONE-SIDED in opposite
//                                       directions and their rulers differed by (1+e)/(1−e); every dash
//                                       boundary tilted off perpendicular. The parallelograms.
//   4. with how it is SAMPLED         — a plain per-vertex varying, so the GPU rendered the chord of a
//                                       hyperbola and the period stepped at every road vertex.
// ONE TOOTH PER TERM, and each is blind to the others — which is why all of T1/T7/T8 are required. A fix
// that killed only the depth term would pass T1 and still ship the rotation the maintainer reported:
//   T1 measures term 1 and is algebraically ZERO for term 3 at its bearing (across ⊥ fwd on a N–S road).
//   T7 probed term 3, and measures term 2 via its arc-length clause, at CONSTANT depth, so term 1 cannot
//      help it. Since S111 the sign term has no mechanism left in the tree, and its primary discriminator
//      is LineProbeSymmetrySnapshotTests, which measures the helper directly.
//   T8 measures term 4 as a purely RELATIVE statement — "the same road with more vertices renders the same
//      dashes" — which needs no derivation in this file to be believed.
//   T2 measures the dpr basis, is GREEN today, and must stay green: at tilt 0 the per-vertex divisor
//      equalled the frame constant identically, so T2 saw only the basis.
//
// FIXTURE SHAPE IS LOAD-BEARING. Every arm drives the material through the PRODUCTION seam — a style JSON
// with line-dasharray parsed into a RenderLayerSet, then set.ApplyZoom(zoom, dpr). Setting _DashArray on a
// hand-made material would test the shader while leaving the wiring unmeasured. The frame global itself is
// pushed by the real MapCamera (S116 moved it there from ApplyZoom), which BuildScene constructs; with both
// seams in the loop, a missing push renders a uniform HALF-COVERAGE line
// (dashU ≡ 0 ⇒ smoothstep(−dfw,+dfw,0) == 0.5 exactly) and every tooth here fails with "no dash edges".

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
    public class LineDashSnapshotTests
    {
        private const int    Size      = 512;
        private const double Zoom      = 8.0;
        private const double LookAtLat = 30.0;
        private const double LookAtLon = 30.0;

        /// <summary>Styled line-width in LOGICAL px. Clear of both thin-line clamps at both ratios, so the
        /// rendered band stays proportional and T7's probe rows — derived from the band that renders, which
        /// under tilt is <c>cos θ</c> thinner than this — sit inside it.</summary>
        private const float StyledLineWidthPx = 16f;

        /// <summary>The tilt at which terms 1 and 3 are both large. 55° gives across·fwd = sin 55° = 0.8192,
        /// hence e = 0.02·tan30°·0.8192 = 0.0094588 and a 1.910 % ruler difference across the ribbon.</summary>
        private const double TiltDeg = 55.0;

        // Pattern [3,3]: on-run [0,3), off-run [3,6). A falling (ON→OFF) edge is dashU ≡ 3 (mod 6).
        private const double DashOnUnits     = 3.0;
        private const double DashPeriodUnits = 6.0;

        private static readonly Color BgColor = new Color(0.05f, 0.05f, 0.08f, 1f);

        /// <summary>line-width is a plain CONSTANT on purpose: a feature-dependent width is bound as a
        /// constant 1 through the same device-px path (MaterialFactory), which would silently make every
        /// measurement here about something else.</summary>
        private const string DashStyleJson = @"{
            ""version"": 8,
            ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""dashed-road"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""l"",
                  ""paint"": { ""line-color"": [""rgba"", 242, 153, 38, 1], ""line-width"": 16,
                               ""line-dasharray"": [3, 3] } }
            ]
        }";

        /// <summary>
        /// Metres of road per DASH UNIT — the quantity S110 makes frame-constant. It is
        /// <c>_Width(device px) × metresPerDevicePx</c> = <c>(16·dpr) × (mpp(zoom)/dpr)</c>, so the dpr
        /// CANCELS and this is one number for both ratios. That cancellation is exactly why a CPU-only
        /// round-trip tooth cannot see a device-vs-logical basis error, and why T2 has to render.
        /// </summary>
        private static double DashUnitMetres => StyledLineWidthPx * CameraPoseMath.MetersPerPixel(Zoom);

        /// <summary>Arc length of the n-th ON→OFF edge (n from 0): dashU = 3 + 6n.</summary>
        private static double FallingEdgeArc(int n) => (DashOnUnits + DashPeriodUnits * n) * DashUnitMetres;

        // ── The scene ────────────────────────────────────────────────────────────────────────────

        private sealed class DashScene : IDisposable
        {
            public GameObject     CamGo;
            public Camera         UnityCamera;
            public RenderTexture  ViewportRt;
            public MapCamera      MapCam;
            public RenderLayerSet Layers;

            /// <summary>Camera altitude in render metres — the look-at sits at the world origin.</summary>
            public double Altitude;

            /// <summary>The styled dashed-line material, straight off the production render layer.</summary>
            public Material Material => Layers[0].Material;

            public void Dispose()
            {
                Layers?.Dispose();
                if (CamGo != null) UnityEngine.Object.DestroyImmediate(CamGo);
                if (ViewportRt != null)
                {
                    ViewportRt.Release();
                    UnityEngine.Object.DestroyImmediate(ViewportRt);
                }
            }
        }

        private static DashScene BuildScene(double tiltDeg, double devicePixelRatio)
        {
            var camGo = new GameObject("Dash_TestCamera");
            var uCam  = camGo.AddComponent<Camera>();

            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
            uCam.targetTexture   = rt;
            uCam.clearFlags      = CameraClearFlags.SolidColor;
            uCam.backgroundColor = BgColor;
            uCam.enabled         = false;

            var props = new CameraProperties(
                new GeoCoordinate3D { Latitude = LookAtLat, Longitude = LookAtLon, Altitude = 0.0 },
                zoom: Zoom, heading: 0.0, tilt: tiltDeg);
            var mapCam = new MapCamera(uCam, props, 1f, null, devicePixelRatio);

            // The PHYSICAL framebuffer must not move with dpr. If it did, the altitude change and the
            // framebuffer change would cancel and T2 would pass on a broken tree.
            Assert.That(mapCam.ViewportPx.x, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport width must stay {Size} at dpr {devicePixelRatio}.");
            Assert.That(mapCam.ViewportPx.y, Is.EqualTo((double)Size).Within(1e-9),
                $"physical viewport height must stay {Size} at dpr {devicePixelRatio}.");

            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(DashStyleJson), Zoom, MapMaterialSetTestUtil.Load());
            Assert.That(set.Count, Is.EqualTo(1), "the dashed-road style must yield exactly one render layer.");
            Assert.IsNotNull(set[0].Material, "Map/Line base material must be configured for this fixture.");

            // The production per-frame push. This is what sets _MapFrameMetersPerDevicePixel, so it is part
            // of the measurement, not setup.
            set.ApplyZoom(Zoom, devicePixelRatio);

            Material mat = set[0].Material;
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.DashCount), Is.EqualTo(2f),
                "precondition: _DashCount must be 2, or the shader's dash branch is dead and every edge " +
                "count below would be zero for a reason that has nothing to do with this stage.");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.Width),
                Is.EqualTo(StyledLineWidthPx * (float)devicePixelRatio).Within(1e-3f),
                $"precondition: _Width must reach the shader in DEVICE px ({StyledLineWidthPx}×dpr, S107).");
            Assert.That(Shader.GetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel),
                Is.GreaterThan(0f),
                "precondition: the frame constant must have been pushed — by MapCamera.SyncToCamera, which " +
                "the MapCamera ctor above runs (S116; it used to be RenderLayerSet.ApplyZoom). A 0 here means " +
                "the dash divisor is 0, the guard sets dashU = 0, and the line renders at a UNIFORM HALF " +
                "COVERAGE with no dash edges at all.");

            return new DashScene
            {
                CamGo       = camGo,
                UnityCamera = uCam,
                ViewportRt  = rt,
                MapCam      = mapCam,
                Layers      = set,
                Altitude    = math.length(mapCam.CameraRelativePosition),
            };
        }

        // ── Scene chrome (the lit-ambient recipe every line snapshot fixture uses) ───────────────

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
            var go = new GameObject("Dash_DirLight");
            go.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            var light = go.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;
            return go;
        }

        private static GameObject AttachMesh(Mesh mesh, Material mat, string name)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        private static void AssertGpuContext(SnapshotRenderer snap)
        {
            if (!snap.IsAllBlack()) return;
            var blankGo = new GameObject("Dash_BlankCamera");
            try
            {
                var blankCam = blankGo.AddComponent<Camera>();
                blankCam.clearFlags      = CameraClearFlags.SolidColor;
                blankCam.backgroundColor = BgColor;
                using var blank = new SnapshotRenderer(Size, Size);
                blank.Render(blankCam);
                if (blank.IsAllBlack())
                    Assert.Inconclusive("Scene render and blank control are both all-black: no GPU context " +
                                        "in batch EditMode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
            }
            finally { UnityEngine.Object.DestroyImmediate(blankGo); }
        }

        /// <summary>Reset the frame global to 0 — the honest "unset", which restores the fail-safe
        /// half-coverage state rather than a plausible-looking value that would hide a missing push.</summary>
        [TearDown]
        public void ClearFrameGlobal()
            => Shader.SetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel, 0f);

        // ── Measurement ──────────────────────────────────────────────────────────────────────────

        /// <summary>The most saturated pixel anywhere in frame. Exactly one object is drawn, so this is a
        /// fully-covered interior pixel of the ribbon — and the ribbon's normal is constant (+Y), so lit
        /// shading does not vary across it and one plateau serves the whole image.</summary>
        private static float3 GlobalPlateau(byte[] pixels, float3 background)
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

        /// <summary>Coverage at a pixel on the background→plateau axis; the classifier threshold is 0.5,
        /// i.e. mid-way between the two, so the AA/dash feather centres on the edge rather than biasing it
        /// a pixel early.</summary>
        private static float CoverageAtPixel(
            byte[] pixels, int column, int row, float3 background, float3 plateau)
            => PixelCoverage.CoverageAt(pixels, Size, Size, column, row, background, plateau);

        /// <summary>
        /// ON→OFF crossings of a coverage series, as FRACTIONAL indices interpolated on the 0.5 crossing.
        /// Sub-pixel matters: T7 discriminates at 1.0 px against a 3.9 px effect, and integer marching alone
        /// would contribute up to 1 px of quantisation to each of its two probes.
        /// </summary>
        private static List<double> FallingEdges(float[] coverage)
        {
            var edges = new List<double>();
            for (int i = 0; i + 1 < coverage.Length; i++)
                if (coverage[i] >= 0.5f && coverage[i + 1] < 0.5f)
                {
                    double drop = coverage[i] - coverage[i + 1];
                    edges.Add(i + (drop > 1e-6 ? (coverage[i] - 0.5) / drop : 0.5));
                }
            return edges;
        }

        /// <summary>Crossings of 0.5 in EITHER direction. T2 counts these rather than falling edges only:
        /// its second on-screen transition is the RISING dashU = 6 edge, and a falling-edge count would
        /// mis-state what the arm proves.</summary>
        private static List<double> AllTransitions(float[] coverage)
        {
            var edges = new List<double>();
            for (int i = 0; i + 1 < coverage.Length; i++)
            {
                bool onNow  = coverage[i]     >= 0.5f;
                bool onNext = coverage[i + 1] >= 0.5f;
                if (onNow == onNext) continue;
                double span = math.abs(coverage[i] - coverage[i + 1]);
                edges.Add(i + (span > 1e-6 ? math.abs(coverage[i] - 0.5) / span : 0.5));
            }
            return edges;
        }

        private static float[] CoverageAlongRow(
            byte[] pixels, int row, int columnFrom, int columnTo, float3 background, float3 plateau)
        {
            var coverage = new float[columnTo - columnFrom + 1];
            for (int column = columnFrom; column <= columnTo; column++)
                coverage[column - columnFrom] = CoverageAtPixel(pixels, column, row, background, plateau);
            return coverage;
        }

        /// <summary>The rendered band thickness in device px: the largest per-column coverage integral over
        /// a vertical cut through the ribbon. Taking the MAX picks a column inside a dash-ON run without
        /// needing to know the phase, and it is a measurement of what rendered rather than an assumption
        /// about which rows the ribbon occupies.</summary>
        private static float MeasureBandThicknessPx(
            byte[] pixels, int centreRow, int columnFrom, int columnTo, float3 background, float3 plateau)
        {
            int rowFrom = math.max(centreRow - 20, 0);
            int rowTo   = math.min(centreRow + 20, Size - 1);
            float best  = 0f;
            for (int column = columnFrom; column <= columnTo; column++)
            {
                float sum = 0f;
                for (int row = rowFrom; row <= rowTo; row++)
                    sum += CoverageAtPixel(pixels, column, row, background, plateau);
                if (sum > best) best = sum;
            }
            return best;
        }

        // ── T1: the DEPTH term ───────────────────────────────────────────────────────────────────

        private const double T1RoadLengthM  = 110_000.0;
        private const double T1StationM     =   2_000.0; // 56 stations — the divisor genuinely varies per vertex
        private const double T1SampleStepM  =     100.0; // 1101 samples

        /// <summary>Arc-length window at the far end of the road inside which a detected ON→OFF crossing is
        /// the terminating BUTT CAP rather than a dash boundary — see the exclusion in
        /// <c>MeasureCentrelineFallingEdges</c>. 4 km is ≈2.7 screen px there, past the ≈2 px feather.
        ///
        /// <para>No dash boundary of EITHER hypothesis falls inside it. The window is arc
        /// [106 000, 110 000] m, i.e. <c>dashU ∈ (21.67, 22.49)</c>; falling edges sit at <c>dashU ≡ 3 mod
        /// 6</c>, so the nearest is <c>dashU</c> 21 (arc 102 731 m) — <b>3.27 km</b> before the window opens
        /// — and the next, 27, is off the road entirely. Under the un-fixed ruler the road only reaches
        /// <c>dashU</c> 13.5, so its boundaries (3, 9) are nowhere near.</para>
        ///
        /// <para>The clearance is deliberately quoted against the nearest BOUNDARY, not against the road's
        /// far end (110 000 − 102 731 = 7 269 m): that is a different quantity and stating it here would
        /// overstate the margin by 2.2×. The exclusion can only ever DISCARD a crossing, so a window that is
        /// too wide costs a false RED and never a false GREEN — it cannot let a wrong ruler through.</para></summary>
        private const double CapExclusionM = 4_000.0;

        private static Mesh BuildNorthRoad(double stationSpacingM)
        {
            var pts = new List<double2>();
            for (double s = 0.0; s <= T1RoadLengthM + 1e-6; s += stationSpacingM)
                pts.Add(new double2(0.0, s));
            if (math.abs(pts[pts.Count - 1].y - T1RoadLengthM) > 1e-6)
                pts.Add(new double2(0.0, T1RoadLengthM));
            return SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);
        }

        /// <summary>
        /// Walks the road's CENTRELINE in arc length, classifies each sample ON/OFF, and returns every
        /// ON→OFF edge as (arc length, screen row) — both sub-pixel, interpolated on the coverage crossing.
        /// The caller must already have rendered <paramref name="snap"/>.
        /// </summary>
        private static (double[] arc, double[] screenY) MeasureCentrelineFallingEdges(
            DashScene scene, SnapshotRenderer snap, string what)
        {
            byte[] pixels     = snap.RawPixels;
            float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);
            float3 plateau    = GlobalPlateau(pixels, background);
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"{what}: nothing in frame is distinguishable from the background — the road did not render, " +
                "so no dash edge is measurable and any count formed from it would be meaningless.");

            var arcs     = new List<double>();
            var rows     = new List<double>();
            var coverage = new List<float>();
            for (double s = 0.0; s <= T1RoadLengthM + 1e-6; s += T1SampleStepM)
            {
                Vector3 sp = scene.UnityCamera.WorldToScreenPoint(new Vector3(0f, 0f, (float)s));
                if (sp.z <= 0f || sp.x < 0f || sp.x >= Size || sp.y < 0f || sp.y >= Size) break;
                arcs.Add(s);
                rows.Add(sp.y);
                coverage.Add(CoverageAtPixel(
                    pixels, (int)math.round(sp.x), (int)math.round(sp.y), background, plateau));
            }

            Assert.That(arcs.Count, Is.GreaterThanOrEqualTo(900),
                $"{what}: only {arcs.Count} of the 1101 centreline samples were on-screen and in front of " +
                "the camera. The fixture's frustum sanity is a precondition — the road must be fully framed " +
                "with the horizon off-screen, or the edge count measures clipping instead of dashes.");

            float[] cov = coverage.ToArray();
            // Vacuity guard. dashU over the first 2 km is at most 0.41 under every hypothesis this stage
            // weighs, so those samples are inside the first ON run. Not asserted on sample 0 alone: at this
            // depth 100 m is 0.19 screen px, so the first few samples all land on the butt cap's own pixel.
            int onInPrefix = 0;
            for (int i = 0; i < 20 && i < cov.Length; i++) if (cov[i] >= 0.5f) onInPrefix++;
            Assert.That(onInPrefix, Is.GreaterThanOrEqualTo(15),
                $"{what}: only {onInPrefix} of the first 20 centreline samples read ON, but all of them are " +
                "inside the first dash-ON run. The classifier or the plateau is wrong, not the dash.");

            var arcList = new List<double>();
            var rowList = new List<double>();
            foreach (double edge in FallingEdges(cov))
            {
                int    lo = (int)math.floor(edge);
                double t  = edge - lo;
                double arc = math.lerp(arcs[lo], arcs[math.min(lo + 1, arcs.Count - 1)], t);

                // THE ROAD'S OWN END IS NOT A DASH EDGE. The butt cap at the far end is an ON→OFF
                // transition whenever the last run happens to be ON, and before S110 it was: with the
                // per-vertex divisor dashU only reached 13.51 over this road, phase 1.51, inside an ON run
                // — so the un-fixed tree rendered 2 dash edges PLUS the cap. Counting the cap would let a shallow
                // fix that merely got dashU_max past 15 reach a count of 4 without ever crossing 21.
                // The window is ~2.7 screen px at this depth, well past the ≈2 px dash feather and far
                // from any dash boundary either hypothesis puts near the end.
                if (arc > T1RoadLengthM - CapExclusionM) continue;

                arcList.Add(arc);
                rowList.Add(math.lerp(rows[lo], rows[math.min(lo + 1, rows.Count - 1)], t));
            }
            return (arcList.ToArray(), rowList.ToArray());
        }

        /// <summary>
        /// <b>T1 (REQUIRED) — the DEPTH term.</b> A north–south road 110 km long, viewed at 55° of tilt,
        /// must carry FOUR ON→OFF dash edges, and the fourth must sit at the arc length the world-anchored
        /// parameterisation puts it at (dashU = 21).
        ///
        /// <para>RED against the un-fixed tree by arithmetic: with the per-vertex divisor the ruler grows
        /// with view depth, so dashU only reaches 13.51 over the same road and crosses 3 and 9 alone —
        /// <b>2</b> edges, and no fourth edge to locate. Non-knife-edge in both directions: losing the
        /// fourth edge needs dashU_max 6.61 % low, gaining a fifth needs it 20.07 % high.</para>
        ///
        /// <para>The count is a TOPOLOGICAL invariant, which is why term 4 cannot confound it: the un-fixed
        /// tree's per-vertex dashU sequence was strictly increasing, so its chord interpolant was monotone
        /// and exact at the stations, hence a monotone rise from 0 to 13.51 crossed 3 and 9 once each and
        /// never reached 15 — 2 edges at ANY tessellation. The edge POSITIONS did move with tessellation
        /// before S110, which is why a position is asserted only on the fixed side, where dashU is
        /// linear.</para>
        ///
        /// <para>Blind to terms 2 and 3 — deliberately. On a north–south road <c>across ⊥ fwd</c>, so the
        /// sign asymmetry <c>e = 0.02·tan(fov/2)·(across·fwd)</c> is ALGEBRAICALLY ZERO here, not merely
        /// small. That is T7's job.</para>
        /// </summary>
        [Test]
        public void DashEdges_AreWorldAnchoredInDepth_UnderTilt()
        {
            var saved   = SetupLitAmbient();
            var lightGo = BuildDirectionalLight();
            using var snap = new SnapshotRenderer(Size, Size);
            try
            {
                using var scene = BuildScene(TiltDeg, devicePixelRatio: 1.0);
                Mesh mesh = BuildNorthRoad(T1StationM);
                GameObject go = AttachMesh(mesh, scene.Material, "Dash_T1_Road");
                try
                {
                    snap.Render(scene.UnityCamera);
                    AssertGpuContext(snap);
                    snap.WritePng("s110-t1-depth-tilt55.png");

                    var (arc, screenY) = MeasureCentrelineFallingEdges(scene, snap, "T1");
                    TestContext.WriteLine($"T1: altitude {scene.Altitude:F1} m, dash unit {DashUnitMetres:F1} m, " +
                                          $"dashU(110 km) = {T1RoadLengthM / DashUnitMetres:F4}");
                    for (int i = 0; i < arc.Length; i++)
                        TestContext.WriteLine($"T1: falling edge #{i + 1} at arc {arc[i]:F0} m " +
                                              $"(dashU {arc[i] / DashUnitMetres:F3}), screen row {screenY[i]:F2}");

                    Assert.That(arc.Length, Is.EqualTo(4),
                        $"expected 4 ON→OFF dash edges over {T1RoadLengthM / 1000:F0} km at tilt {TiltDeg}°, " +
                        $"found {arc.Length}. The world-anchored parameterisation reaches dashU = " +
                        $"{T1RoadLengthM / DashUnitMetres:F3} and so crosses 3, 9, 15 and 21. TWO edges is the " +
                        "defect: dividing by the per-vertex MapPixelsToWorld makes the ruler grow with view " +
                        "depth, so dashU only reaches 13.51 and never gets past 9 — the dashes stretch with " +
                        "distance and crawl along the road as the camera tilts.");

                    double expected = FallingEdgeArc(3); // dashU = 21
                    Assert.That(arc[3], Is.EqualTo(expected).Within(4.0).Percent,
                        $"the 4th falling edge must sit at dashU = 21, i.e. arc length {expected:F0} m; " +
                        $"measured {arc[3]:F0} m. Tolerance is ±4 %: at this depth 100 m of road is 0.071 " +
                        "screen px, so localisation is bounded by the pixel (±1 px ≈ 1 400 m ≈ 1.36 %) plus " +
                        "the dash feather (2 px ≈ 2.73 %), leaving 1.3 % of headroom.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lightGo);
                RestoreAmbient(saved);
            }
        }

        // ── T7: the SIGN term — cross-ribbon skew, the rotation ──────────────────────────────────

        private const double T7HalfLengthM = 80_000.0;
        private const double T7StationM    =  2_000.0;

        /// <summary>How far inside the styled edge each probe row sits, in device px. The probe OFFSET is
        /// then derived from the band that actually renders (it used to be a hard-coded 6, which encoded
        /// "the band is 16 px" — the premise S116 deleted). Clear of the ±0.5 px AA straddle with 2 px to
        /// spare.</summary>
        private const double T7ProbeInsetPx = 2.0;

        /// <summary>Skew tolerance, expressed PER PIXEL OF PROBE SEPARATION rather than as an absolute — the
        /// quantity the tooth is about is an ANGLE, and a band that renders <c>cos θ</c> thinner puts the two
        /// probes closer together, which would silently scale an absolute bound's discriminating power.
        /// <c>1.0 px over the original 12 px separation</c>, so the historical RED (3.94 px over 12 px =
        /// 0.3283) keeps its 3.9× margin whatever the band width.</summary>
        private const double T7MaxSkewPerSeparationPx = 1.0 / 12.0;

        /// <summary>
        /// The screen→arc-length map for ONE horizontal probe scanline across the east–west ribbon. A probe
        /// row corresponds to a world line offset along <c>across</c> (world ±Z), and under tilt that offset
        /// changes the line's DEPTH — so each probe has its own scale and each must be inverted with its own.
        /// Everything here is read off the real camera; nothing models the projection.
        /// </summary>
        private readonly struct ProbeMapping
        {
            public readonly double Z;          // world z of the line that projects onto this row
            public readonly double OriginX;    // screen x of world (0, 0, Z)
            public readonly double PxPerMetre; // screen px per world metre along the road, at this row

            public ProbeMapping(double z, double originX, double pxPerMetre)
            {
                Z = z; OriginX = originX; PxPerMetre = pxPerMetre;
            }

            /// <summary>Arc length along the road (0 at its west end) for a screen x on this scanline.</summary>
            public double ArcAt(double screenX) => (screenX - OriginX) / PxPerMetre + T7HalfLengthM;
        }

        /// <summary>Solves which world-z line projects onto <paramref name="targetRow"/> (via
        /// <see cref="GroundRowSolver.SolveWorldZForRow"/>), then samples that line's screen scale. Bisection
        /// rather than a closed form on purpose: the extruded half-width is computed in the vertex shader, so
        /// the test has no honest way to derive z — but it can ask where a given z LANDS, which is enough to
        /// invert. ±60 km is far wider than any ribbon half-width at this zoom.</summary>
        private static ProbeMapping MapProbeRow(Camera cam, double targetRow)
        {
            double z = GroundRowSolver.SolveWorldZForRow(cam, targetRow, -60_000.0, +60_000.0);

            double originX = cam.WorldToScreenPoint(new Vector3(0f, 0f, (float)z)).x;
            double refX    = cam.WorldToScreenPoint(new Vector3(10_000f, 0f, (float)z)).x;
            double midX    = cam.WorldToScreenPoint(new Vector3(5_000f,  0f, (float)z)).x;
            double pxPerMetre = (refX - originX) / 10_000.0;
            Assert.That(midX, Is.EqualTo(originX + 5_000.0 * pxPerMetre).Within(0.05),
                "probe map: screen x must be affine in world x at constant depth, or the inversion is wrong.");
            return new ProbeMapping(z, originX, pxPerMetre);
        }

        /// <summary>
        /// <b>T7 (REQUIRED) — the SIGN term, i.e. the rotation the maintainer reported.</b> On an east–west
        /// road under tilt, a dash boundary must be PERPENDICULAR to the road: sampled on two scanlines
        /// 6 px either side of the centreline, each ON→OFF edge must land at the same screen x on both.
        ///
        /// <para>THE MECHANISM, and why no other tooth in this stage can see it. The two ribbon vertices of
        /// a station share ONE centreline position and carry OPPOSITE <c>extrudeN</c>
        /// (LineTessellator: <c>MakeVertex(p2, n1, …, +1)</c> / <c>MakeVertex(p2, Neg(n1), …, −1)</c>), and
        /// the shader handed that signed direction straight to <c>MapPixelsToWorld</c>, which probed
        /// ONE-SIDED and so returned two different rulers for one physical axis. <b>S111 divided the probe's
        /// own foreshortening back out, so the helper is direction-symmetric and this mechanism no longer
        /// exists at source; the sign term's primary discriminator is now
        /// <c>LineProbeSymmetrySnapshotTests</c>, which measures the helper itself.</b> The two rulers
        /// differed by exactly <c>(1+e)/(1−e)</c> with
        /// <c>e = 0.02·tan(fov/2)·(across·fwd)</c> — independent of depth, zoom, altitude, width and screen
        /// position. Here <c>across·fwd = sin 55° = 0.8192</c>, so e = 0.0094588 and the two edges of the
        /// ribbon carried dashU values 1.910 % apart. Since dashU interpolates perspective-correctly, the
        /// iso-dashU contour stayed a straight line but stopped being perpendicular to the road, and the
        /// tilt grew LINEARLY with accumulated dashU. Diagonal parallelograms.</para>
        ///
        /// <para>RED before S110 at 3.94 px of probe-to-probe skew on the dashU = 21 edge (≈18°), with two
        /// more edges above 3.6 px — a 3.9× margin that did not hinge on locating one particular edge. The
        /// assertion takes the MAX and not the mean: the innermost edge sits near the look-at where the
        /// effect was genuinely small (1.13 px) and averaging it in would make the tooth knife-edge.</para>
        ///
        /// <para>Depth is CONSTANT along this road (an east–west line has zero <c>fwd</c> component in x),
        /// so term 1 contributes nothing here and the fix cannot pass by accident through it.</para>
        /// </summary>
        [Test]
        public void DashBoundaries_ArePerpendicularAcrossTheRibbon_UnderTilt()
        {
            var saved   = SetupLitAmbient();
            var lightGo = BuildDirectionalLight();
            using var snap = new SnapshotRenderer(Size, Size);
            try
            {
                using var scene = BuildScene(TiltDeg, devicePixelRatio: 1.0);

                var pts = new List<double2>();
                for (double x = -T7HalfLengthM; x <= T7HalfLengthM + 1e-6; x += T7StationM)
                    pts.Add(new double2(x, 0.0));
                Mesh mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);
                GameObject go = AttachMesh(mesh, scene.Material, "Dash_T7_Road");
                try
                {
                    snap.Render(scene.UnityCamera);
                    AssertGpuContext(snap);
                    snap.WritePng("s110-t7-skew-tilt55.png");

                    byte[] pixels     = snap.RawPixels;
                    float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);
                    float3 plateau    = GlobalPlateau(pixels, background);
                    Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                        "T7: the road did not render — no skew is measurable.");

                    Vector3 originSp = scene.UnityCamera.WorldToScreenPoint(Vector3.zero);
                    Vector3 refSp    = scene.UnityCamera.WorldToScreenPoint(new Vector3(10_000f, 0f, 0f));
                    Assert.That(originSp.y, Is.EqualTo(refSp.y).Within(0.05),
                        "T7 precondition: the road must project onto a single screen ROW (viewY ≡ 0 for " +
                        "points on the x axis), or the ±6 px probes are not symmetric about the ribbon.");

                    int centreRow = (int)math.round(originSp.y);
                    ProbeMapping centre = MapProbeRow(scene.UnityCamera, originSp.y);
                    float thickness = MeasureBandThicknessPx(pixels, centreRow, 0, Size - 1, background, plateau);
                    // RE-DERIVED (S116). This used to demand 16.0 ± 1.5 px — the styled width itself, i.e.
                    // "a px width holds its DEVICE width under tilt", the premise the width model deleted.
                    // A styled px width now fixes a WORLD width at the look-at; this road runs ACROSS the
                    // view azimuth, so its across-axis lies in the ground plane along the tilt direction and
                    // picks up that plane's foreshortening: 16·cos 55° = 9.177 px (measured 9.173).
                    // It is a PRECONDITION for the probe placement below, not the tooth.
                    double expectedThickness = StyledLineWidthPx * math.cos(math.radians(TiltDeg));
                    TestContext.WriteLine($"T7: centre row {originSp.y:F2}, rendered band thickness " +
                                          $"{thickness:F2} px (want {expectedThickness:F3} = " +
                                          $"{StyledLineWidthPx}·cos {TiltDeg}°), " +
                                          $"{centre.PxPerMetre * 1000.0:F4} px per km");
                    Assert.That(thickness, Is.EqualTo(expectedThickness).Within(0.6),
                        $"T7 precondition: the ribbon must render {expectedThickness:F3} device px thick " +
                        $"({StyledLineWidthPx} px styled × cos {TiltDeg}°, the ground plane's foreshortening " +
                        $"along the across-axis); measured {thickness:F2} px. A reading near " +
                        $"{StyledLineWidthPx} would mean the band is holding a constant DEVICE width under " +
                        "tilt — the compensation this repo reverted.");

                    // The probes are placed from the band that RENDERED, so this tooth no longer encodes any
                    // width premise at all. 2 px inside the styled edge, integer rows because the probe is a
                    // scanline.
                    int probeOffset = (int)math.floor(0.5 * thickness - T7ProbeInsetPx);
                    Assert.That(probeOffset, Is.GreaterThanOrEqualTo(2),
                        $"T7: a {thickness:F2} px band leaves a probe offset of {probeOffset} px, too close " +
                        "to the centreline to resolve a cross-ribbon skew. RAISE the styled width and " +
                        "re-derive; do not move the probes onto the AA straddle.");

                    // THE MEASUREMENT SPACE IS ARC LENGTH, NOT SCREEN X — and that is not a detail.
                    // The two ribbon edges are two world lines at DIFFERENT DEPTHS under tilt (the far edge
                    // sits +halfWidth along `across`, the near edge −halfWidth), so the ribbon converges
                    // toward the vanishing point and a world-PERPENDICULAR dash boundary projects as a
                    // SLANTED screen segment. That slant is correct rendering, it is several px at the frame
                    // edges, and it swamps the effect this tooth exists to catch. Each probe therefore gets
                    // its own screen→world map, solved from the real camera, and the boundary is compared
                    // where "perpendicular" is genuinely a null: the arc length the boundary sits at.
                    // PRE-EXISTING, deliberately left: these pass a pixel INDEX where GroundRowSolver documents
                    // a screen-y (index j's centre is at j + 0.5), so each probe is placed half a row off. That
                    // shifts probe PLACEMENT, not a measured quantity, so it is second-order here — but it is
                    // fatal in a tooth where 0.5 px IS the measurement. Fixing it would move T7's probes, which
                    // S111 is fenced from doing; if T7 ever flakes, symmetrise the offsets and fix this together.
                    ProbeMapping upperMap = MapProbeRow(scene.UnityCamera, centreRow + probeOffset);
                    ProbeMapping lowerMap = MapProbeRow(scene.UnityCamera, centreRow - probeOffset);
                    TestContext.WriteLine($"T7: probe world-z {upperMap.Z:F0} / {lowerMap.Z:F0} m, " +
                                          $"px per km {upperMap.PxPerMetre * 1000.0:F4} / " +
                                          $"{lowerMap.PxPerMetre * 1000.0:F4}");

                    float[] upper = CoverageAlongRow(pixels, centreRow + probeOffset, 0, Size - 1, background, plateau);
                    float[] lower = CoverageAlongRow(pixels, centreRow - probeOffset, 0, Size - 1, background, plateau);
                    List<double> upperEdges = FallingEdges(upper);
                    List<double> lowerEdges = FallingEdges(lower);

                    var upperArc = upperEdges.ConvertAll(x => upperMap.ArcAt(x));
                    var lowerArc = lowerEdges.ConvertAll(x => lowerMap.ArcAt(x));
                    TestContext.WriteLine("T7: upper probe edges at x = " +
                                          string.Join(", ", upperEdges.ConvertAll(e => e.ToString("F2"))) +
                                          "  → arc " + string.Join(", ", upperArc.ConvertAll(a => a.ToString("F0"))));
                    TestContext.WriteLine("T7: lower probe edges at x = " +
                                          string.Join(", ", lowerEdges.ConvertAll(e => e.ToString("F2"))) +
                                          "  → arc " + string.Join(", ", lowerArc.ConvertAll(a => a.ToString("F0"))));

                    // Vacuity guard: two probes that both MISSED the ribbon yield empty sets, and a max over
                    // nothing passes trivially.
                    Assert.That(upperEdges.Count, Is.GreaterThanOrEqualTo(4),
                        $"T7: only {upperEdges.Count} falling edges on the upper probe — expected at least 4. " +
                        "Fewer means the probe missed the ribbon or the dash " +
                        "branch is inert, either of which makes the skew assertion vacuous.");
                    Assert.That(lowerEdges.Count, Is.GreaterThanOrEqualTo(4),
                        $"T7: only {lowerEdges.Count} falling edges on the lower probe.");

                    // Paired by ARC PROXIMITY, not by index: the two probes clip the frame at different
                    // world x (again the convergence), so one can carry an outermost edge the other does
                    // not, and index pairing would silently compare edge n against edge n+1.
                    double maxSkew = 0.0, worstArc = 0.0;
                    int    pairs   = 0;
                    for (int i = 0; i < upperArc.Count; i++)
                    {
                        double best = double.MaxValue;
                        for (int j = 0; j < lowerArc.Count; j++)
                            best = math.min(best, math.abs(upperArc[i] - lowerArc[j]));
                        if (best > 0.4 * DashPeriodUnits * DashUnitMetres) continue; // no counterpart in frame
                        pairs++;
                        if (best > maxSkew) { maxSkew = best; worstArc = upperArc[i]; }
                    }
                    double maxSkewPx = maxSkew * centre.PxPerMetre;
                    TestContext.WriteLine($"T7: {pairs} paired edges, max cross-ribbon skew {maxSkew:F0} m " +
                                          $"({maxSkewPx:F3} px along the road) at arc {worstArc:F0} m");

                    Assert.That(pairs, Is.GreaterThanOrEqualTo(4),
                        $"T7: only {pairs} dash boundaries appear on BOTH probes; fewer than 4 makes the " +
                        "max below rest on too little.");

                    // The ANGLE, not the absolute displacement: the probes are now placed from the band that
                    // rendered, and a cos θ-thinner band brings them closer together, which would silently
                    // relax an absolute bound. Threshold = the original 1.0 px over the original 12 px
                    // separation, so the historical RED (3.94 px over 12 px) keeps its 3.9× margin.
                    double skewPerSeparation = maxSkewPx / (2.0 * probeOffset);
                    TestContext.WriteLine(
                        $"T7: probe separation {2 * probeOffset} px, skew {maxSkewPx:F4} px ⇒ " +
                        $"{skewPerSeparation:F5} px per px of separation (limit " +
                        $"{T7MaxSkewPerSeparationPx:F5})");

                    Assert.That(skewPerSeparation, Is.LessThanOrEqualTo(T7MaxSkewPerSeparationPx),
                        $"THE ROTATION: a dash boundary sits at arc length {maxSkew:F0} m " +
                        $"({maxSkewPx:F3} px along the road) apart on the two ±{probeOffset} px probes — a " +
                        $"skew of {skewPerSeparation:F5} px per px of separation, i.e. it " +
                        "is not perpendicular to the road. HISTORICALLY that was the sign term: the two " +
                        "ribbon vertices of a station share one centreline point and carry opposite " +
                        "extrudeN, and the pre-S111 MapPixelsToWorld probed ONE-SIDED, so with the " +
                        "per-vertex divisor they got rulers (1+e)/(1−e) = 1.910 % apart and dashU differed " +
                        "across the ribbon. S111 removed that mechanism at source, and a frame-constant " +
                        "divisor additionally has no direction and no sign — so a reading here is NOT " +
                        "explained by the historical cause. Investigate what it is rather than assuming.");

                    // Term 2, for one line: the same WORLD period T1 measures on a perpendicular road.
                    // Before S110 the east-west edges sat elsewhere entirely, so this clause was RED too.
                    for (int i = 0; i < lowerArc.Count; i++)
                    {
                        double want = FallingEdgeArc(i);
                        TestContext.WriteLine($"T7: edge #{i + 1} arc {lowerArc[i]:F0} m (want {want:F0} m, " +
                                              $"dashU {lowerArc[i] / DashUnitMetres:F3})");
                        Assert.That(lowerArc[i], Is.EqualTo(want).Within(4.0).Percent,
                            $"T7 edge #{i + 1} sits at arc length {lowerArc[i]:F0} m; the world-anchored " +
                            $"period puts it at {want:F0} m (dashU = {DashOnUnits + DashPeriodUnits * i}). " +
                            "This is the DIRECTION term: the per-vertex ruler was measured along `across` " +
                            "while dashes run `along`, so an east–west road got a different period from the " +
                            "north–south road T1 measures at the same depth. After the fix both roads share " +
                            "one period.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lightGo);
                RestoreAmbient(saved);
            }
        }

        // ── T8: the SAMPLING term — vertex-density independence ──────────────────────────────────

        /// <summary>
        /// <b>T8 (REQUIRED) — the SAMPLING term.</b> The SAME road, meshed two ways, must render its dashes
        /// in the same places: a 56-station mesh at 2 km spacing and a bare two-point mesh must put every
        /// dash edge within 1 screen px of each other.
        ///
        /// <para>MECHANISM: dashU is a plain interpolated varying (no <c>nointerpolation</c> anywhere in
        /// <c>Map/Line/</c>), so the GPU renders the perspective-correct CHORD of the per-vertex value.
        /// With the per-vertex divisor that value is a hyperbola in arc length, and a chord of a hyperbola
        /// depends on where its endpoints are — so the period steps at every road vertex and a road's dash
        /// pattern depends on how many vertices its source geometry shipped with.
        /// <c>LineCurvatureSubdivision</c> documents that a flat/Mercator projection "always yields 1 step
        /// per segment", so the two-point case is the NORMAL Mercator case, not a contrived one. After the
        /// fix dashU is exactly linear in arc length, and a chord of a linear function IS the function, so
        /// the dependence vanishes rather than shrinking.</para>
        ///
        /// <para>RED before S110 at ≈12.4 and ≈12.7 px of displacement — the largest margin of any tooth here.
        /// It is also the only purely RELATIVE measurement in the stage: it needs no derivation in this
        /// file to be believed.</para>
        ///
        /// <para>TWO GUARDS, both non-obvious. (1) The sparse mesh is TWO POINTS, not 30 km stations: with
        /// 30 km stations the second edge lands within 0.00 px because the 60 km station sits almost exactly
        /// where dashU = 9 falls, so the chord passes through the true value and the tooth is inert on that
        /// edge. (2) The count is asserted equal and ≥ 2 before pairing — and note that BEFORE S110 BOTH
        /// MESHES PRODUCED 2 EDGES, so a count assertion discriminated nothing. T8's whole signal is
        /// positional, which is exactly what makes it independent of T1 rather than a copy of it.</para>
        /// </summary>
        [Test]
        public void DashEdges_AreIndependentOfRoadVertexDensity()
        {
            var saved   = SetupLitAmbient();
            var lightGo = BuildDirectionalLight();
            using var snap = new SnapshotRenderer(Size, Size);
            try
            {
                using var scene = BuildScene(TiltDeg, devicePixelRatio: 1.0);

                double[] denseY  = RenderAndMeasureRows(scene, snap, BuildNorthRoad(T1StationM),
                    "s110-t8-dense.png", "T8 dense (2 km stations)");
                double[] sparseY = RenderAndMeasureRows(scene, snap,
                    SyntheticLineMesh.BuildFromPoints(
                        new List<double2> { new double2(0.0, 0.0), new double2(0.0, T1RoadLengthM) },
                        JoinType.Miter, CapType.Butt),
                    "s110-t8-sparse.png", "T8 sparse (2 points)");

                TestContext.WriteLine($"T8: dense  edges at rows " +
                                      string.Join(", ", Array.ConvertAll(denseY, y => y.ToString("F2"))));
                TestContext.WriteLine($"T8: sparse edges at rows " +
                                      string.Join(", ", Array.ConvertAll(sparseY, y => y.ToString("F2"))));

                Assert.That(denseY.Length, Is.GreaterThanOrEqualTo(2),
                    $"T8: the dense mesh produced only {denseY.Length} dash edges — too few to pair.");
                Assert.That(sparseY.Length, Is.EqualTo(denseY.Length),
                    $"T8: the two meshes produced different edge counts ({denseY.Length} vs " +
                    $"{sparseY.Length}) and cannot be paired. Note this clause discriminated NOTHING " +
                    "before S110 — both meshes yielded 2 — it exists so the positional comparison below is " +
                    "never taken over mismatched pairs.");

                double maxDelta = 0.0;
                int    worst    = 0;
                for (int i = 0; i < denseY.Length; i++)
                {
                    double delta = math.abs(denseY[i] - sparseY[i]);
                    if (delta > maxDelta) { maxDelta = delta; worst = i; }
                }
                TestContext.WriteLine($"T8: max edge displacement {maxDelta:F3} px at edge #{worst + 1}");

                Assert.That(maxDelta, Is.LessThanOrEqualTo(1.0),
                    $"the same road rendered with a different vertex count moved dash edge #{worst + 1} by " +
                    $"{maxDelta:F3} screen px. Before S110 the tree measured ≈12.4 px on the first edge and " +
                    "≈12.7 px on the second: dashU was a per-vertex varying over a hyperbola, so the " +
                    "rendered chord — and therefore the dash period — depended on the road's tessellation. " +
                    "After the fix dashU " +
                    "is linear in arc length and the chord reproduces it to float noise at any density.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lightGo);
                RestoreAmbient(saved);
            }
        }

        private static double[] RenderAndMeasureRows(
            DashScene scene, SnapshotRenderer snap, Mesh mesh, string png, string what)
        {
            GameObject go = AttachMesh(mesh, scene.Material, what);
            try
            {
                snap.Render(scene.UnityCamera);
                AssertGpuContext(snap);
                snap.WritePng(png);
                var (_, screenY) = MeasureCentrelineFallingEdges(scene, snap, what);
                return screenY;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        // ── T2: the device-vs-logical BASIS ──────────────────────────────────────────────────────

        private const double T2RoadLengthM = 76_000.0;

        /// <summary>
        /// <b>T2 — the basis pin.</b> At tilt 0, an east-running road's first dash transition must sit at
        /// the screen offset the DEVICE-pixel basis puts it at, at dpr 1 AND at dpr 2 — 48 px and 96 px,
        /// a ratio of exactly 2.
        ///
        /// <para>WHY BOTH ARMS. The dash period in world metres is
        /// <c>(w_logical·dpr) × (mpp_logical/dpr) × Σ</c>: the dpr CANCELS. So a Core-only round trip is
        /// vacuous, and any helper written as "width × MetersPerPixel × Σ" is right while mentioning no
        /// ratio at all. The error only exists where the two halves are owned by different files —
        /// <c>_Width</c> is multiplied by dpr at the style seam, so the ruler must be divided by it here.
        /// <b>The dpr-1 arm reads 48 px under BOTH hypotheses</b>: it is the control, not the
        /// discriminator. That is the whole shape of the DPR trap, and it is why the rest of the suite —
        /// which runs only at dpr 1 — cannot see this.</para>
        ///
        /// <para>The logical-basis error puts the dpr-2 transition at 192 px, 96 px away from a ±3 px
        /// assertion, and leaves only ONE transition inside the frame instead of two.</para>
        ///
        /// <para>GREEN on today's tree, and it must be: at tilt 0 the ground is perpendicular to the view
        /// axis, so depth ≡ altitude everywhere and the per-vertex divisor EQUALS the frame constant
        /// identically. T2 measures the basis exactly, not approximately — and nothing else.</para>
        /// </summary>
        [Test]
        public void DashPeriod_UsesTheDevicePixelBasis_AcrossDpr()
        {
            var saved   = SetupLitAmbient();
            var lightGo = BuildDirectionalLight();
            using var snap = new SnapshotRenderer(Size, Size);
            try
            {
                (double measured, double expected, int count) at1 = MeasureFirstTransition(snap, 1.0);
                (double measured, double expected, int count) at2 = MeasureFirstTransition(snap, 2.0);

                TestContext.WriteLine($"T2: dpr 1 first transition {at1.measured:F2} px " +
                                      $"(expected {at1.expected:F2}, {at1.count} transitions in frame); " +
                                      $"dpr 2 {at2.measured:F2} px (expected {at2.expected:F2}, " +
                                      $"{at2.count} transitions in frame)");

                // The derived expectations are fixture preconditions: if the camera framing moves, this says
                // so rather than blaming the dash.
                Assert.That(at1.expected, Is.EqualTo(48.0).Within(3.0),
                    "T2 precondition: at dpr 1 the dashU = 3 boundary is 48 px from centre by construction.");
                Assert.That(at2.expected, Is.EqualTo(96.0).Within(3.0),
                    "T2 precondition: at dpr 2 the camera altitude halves, so the same GROUND distance is " +
                    "96 px from centre.");

                Assert.That(at1.measured, Is.EqualTo(at1.expected).Within(3.0),
                    $"dpr 1: the first dash transition must sit {at1.expected:F1} px from frame centre; " +
                    $"measured {at1.measured:F1} px. THIS ARM IS THE CONTROL — it reads the same under the " +
                    "device basis and the logical one, so a failure here is a fixture problem.");

                Assert.That(at2.measured, Is.EqualTo(at2.expected).Within(3.0),
                    $"dpr 2: the first dash transition must sit {at2.expected:F1} px from frame centre; " +
                    $"measured {at2.measured:F1} px. A reading near 192 px is the LOGICAL-basis error: " +
                    "_Width arrives doubled (device px, S107) while the ruler was left in metres per " +
                    "LOGICAL px, so the dash unit doubles and the pattern is twice as coarse on a dense " +
                    "panel — the identity at dpr 1, hence invisible to every other test.");

                double ratio = at2.measured / at1.measured;
                Assert.That(ratio, Is.EqualTo(2.0).Within(0.1),
                    $"the dash pattern must be dpr-INVARIANT in ground terms, so its on-screen offset scales " +
                    $"with the camera exactly like the ground does — ratio 2.0, measured {ratio:F3}. A ratio " +
                    "near 4 means the ratio was applied twice; near 1, not at all.");

                Assert.That(at2.count, Is.GreaterThanOrEqualTo(2),
                    $"dpr 2 must show at least two dash transitions inside 256 px of centre (the falling " +
                    $"dashU = 3 at 96 px and the RISING dashU = 6 at 192 px); found {at2.count}. The " +
                    "logical-basis error shows exactly one, its next transition landing at 384 px, " +
                    "off-screen. Count transitions in EITHER direction here — falling edges alone would " +
                    "mis-state what this clause proves.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(lightGo);
                RestoreAmbient(saved);
            }
        }

        private static (double measured, double expected, int count) MeasureFirstTransition(
            SnapshotRenderer snap, double devicePixelRatio)
        {
            using var scene = BuildScene(tiltDeg: 0.0, devicePixelRatio: devicePixelRatio);

            var pts = new List<double2> { new double2(0.0, 0.0), new double2(T2RoadLengthM, 0.0) };
            Mesh mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);
            GameObject go = AttachMesh(mesh, scene.Material, "Dash_T2_Road");
            try
            {
                snap.Render(scene.UnityCamera);
                AssertGpuContext(snap);
                snap.WritePng($"s110-t2-basis-dpr{devicePixelRatio:F1}.png");

                byte[] pixels     = snap.RawPixels;
                float3 background = PixelCoverage.BackgroundLinear(pixels, Size, Size);
                float3 plateau    = GlobalPlateau(pixels, background);
                Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                    $"T2 at dpr {devicePixelRatio}: the road did not render.");

                Vector3 centreSp = scene.UnityCamera.WorldToScreenPoint(Vector3.zero);
                // The dashU = 3 boundary's screen position, taken from the REAL camera rather than from a
                // re-derived projection — the expectation this arm is asserted against.
                Vector3 edgeSp = scene.UnityCamera.WorldToScreenPoint(
                    new Vector3((float)FallingEdgeArc(0), 0f, 0f));
                double expected = edgeSp.x - centreSp.x;

                // Start three px right of the cap so the butt cap's own edge is never read as a transition.
                int from = (int)math.round(centreSp.x) + 3;
                int row  = (int)math.round(centreSp.y);
                float[] coverage = CoverageAlongRow(pixels, row, from, Size - 1, background, plateau);
                Assert.That(coverage[0], Is.GreaterThanOrEqualTo(0.5f),
                    $"T2 at dpr {devicePixelRatio}: the road is OFF at its own start (dashU ≈ 0), which " +
                    "means the scan row or the plateau is wrong, not the dash.");

                List<double> transitions = AllTransitions(coverage);
                Assert.That(transitions.Count, Is.GreaterThanOrEqualTo(1),
                    $"T2 at dpr {devicePixelRatio}: no dash transition found along the road at all. A " +
                    "UNIFORM line means the frame constant was never pushed (dashU ≡ 0 ⇒ half coverage).");

                double measured = from + transitions[0] - centreSp.x;
                return (measured, expected, transitions.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}
#endif
