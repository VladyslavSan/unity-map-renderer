// Measurement harness for the fill boundary band's cost. NOT a tooth — every test here is [Explicit], so
// an unfiltered gate run never touches it (an [Ignore] would set result=Skipped and red the gate instead).
//
// Written because every argument about the band so far has been made from vertex COUNTS against
// GlobeFillSubdivider.DefaultMaxInteriorVertices — a constant this project chose for itself — and nobody
// had measured what those vertices cost to draw.
//
// WHAT THIS REPORTS IS A PROXY, NOT GPU FRAME TIME. Do not quote a number from here as one. Batch EditMode
// has a live Metal device but no player loop, and it was measured here (Probe_WhichTimingInstrumentsExist-
// Headless) that SystemInfo.supportsGpuRecorder is False and FrameTimingManager returns gpuFrameTime=0.000
// on every frame — a 'GPU Frame Time' recorder is LISTED among the available stats, but nothing feeds it.
// So the clock below is wall time around a batch of Camera.Render() calls with one terminal readback that
// blocks on the GPU. It resolves an ARM-TO-ARM DIFFERENCE, because the fixed per-render overhead (~0.55 ms
// here) is common to both arms and cancels; the absolute medians are not a frame time and do not transfer
// to a real frame, which draws many tiles and many layers per pass rather than one mesh.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using Fill = MapRenderer.Core.Style.Fill;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using Debug = UnityEngine.Debug;
// Aliased at file scope on purpose: inside `namespace MapRenderer.Tests.Visual`, a `Unity.Profiling...`
// qualification binds `Unity` to MapRenderer.Unity and fails to resolve. Out here it binds globally.
using ProfilerRecorderHandle = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Measures the fill boundary band's cost, band-on versus band-off, over real MVT fixtures.
    ///
    /// <para><b>What the arms are.</b> Both arms run the identical shader and the identical camera; the only
    /// difference is <c>FillMeshPipeline.LayerInput.SuppressBoundaryBand</c>, reached through the existing
    /// test-only knob on <see cref="TestTileMeshBuilder.BuildFillFromLayer"/>. No production code is
    /// modified to make this measurable.</para>
    ///
    /// <para><b>What the clock is, precisely.</b> Batch EditMode has a live Metal device but no player loop,
    /// so there is no GPU frame timer (<see cref="Probe_WhichTimingInstrumentsExistHeadless"/> records what
    /// is actually available). The number below is therefore a labelled PROXY: wall clock around
    /// <see cref="RendersPerSample"/> bare <c>Camera.Render()</c> calls followed by ONE terminal readback
    /// that drains the GPU, divided by the render count. Readback is outside the per-render divisor's
    /// numerator only to the extent of one drain per sample, and it is identical in both arms, so it
    /// cancels in the arm-to-arm difference — which is the quantity reported.</para>
    /// </summary>
    [TestFixture]
    [Explicit("Measurement harness, not a tooth — run by name.")]
    public class FillBandFrameCostDiagnostic
    {
        /// <summary>Side of the square off-screen target every timed render draws into. The band is one
        /// DEVICE pixel wide (Fill_VertexModify.hlsl), so the target's pixel size is what sets the band's
        /// fragment count — it is a parameter of the measurement, not a free choice.</summary>
        private const int RtPx = 1024;

        /// <summary>Bare <c>Camera.Render()</c> calls per timed sample. One terminal GPU drain per sample is
        /// amortised over this many renders; 20 puts the drain well under the per-render cost.</summary>
        private const int RendersPerSample = 20;

        /// <summary>Samples discarded before recording — shader-variant compilation and first-touch GPU
        /// resource creation land here, not in the reported distribution.</summary>
        private const int WarmupSamples = 5;

        /// <summary>Recorded samples per arm. Odd, so the median is an observed value rather than a mean of
        /// two.</summary>
        private const int Samples = 25;

        /// <summary>World-unit size the tile mesh is fitted to, and the ortho camera's full height — so the
        /// tile exactly fills the frame at magnification 1.</summary>
        private const float ViewSize = 100f;

        /// <summary>Real MVT tiles spanning the density range the band's cost is claimed to scale over: one
        /// whole-world polygon set at z0, and real coastline/archipelago water tiles at z6–z9 chosen (by
        /// <c>WaterTriangulationTests</c>, whose corpus this is) for pathological ring and hole counts.</summary>
        private static readonly (string File, int Z, int X, int Y, string Layer)[] Corpus =
        {
            ("sample-tile.bytes",                                    0,   0,   0, "countries"),
            ("water-6-32-20.pbf.bytes",                              6,  32,  20, "water"),
            ("water-8-135-80.pbf.bytes",                             8, 135,  80, "water"),
            ("water-real-norway-fjords-8-132-72.pbf.bytes",          8, 132,  72, "water"),
            ("water-real-stockholm-archipelago-9-282-150.pbf.bytes", 9, 282, 150, "water"),
            ("water-real-croatia-dalmatia-9-279-187.pbf.bytes",      9, 279, 187, "water"),
        };

        /// <summary>Prefix on every reported line, so the numbers can be pulled out of Logs/test-run.log
        /// with a single grep.</summary>
        private const string Tag = "BANDCOST|";

        // ── Probe: what timing instruments exist here at all ───────────────────────────────────────────

        /// <summary>
        /// Records which timing instruments batch EditMode actually offers, so the choice of clock below is
        /// evidence rather than assertion. Asserts nothing — an instrument being absent is a result.
        /// </summary>
        [Test]
        public void Probe_WhichTimingInstrumentsExistHeadless()
        {
            Report($"device={SystemInfo.graphicsDeviceType} name='{SystemInfo.graphicsDeviceName}' " +
                   $"supportsGpuRecorder={SystemInfo.supportsGpuRecorder}");

            // FrameTimingManager reads the PLAYER LOOP's frame history. EditMode batch drives no player
            // loop, so a zero here is the expected answer, not a misconfiguration.
            var timings = new FrameTiming[4];
            FrameTimingManager.CaptureFrameTimings();
            uint got = FrameTimingManager.GetLatestTimings(4, timings);
            Report($"FrameTimingManager.GetLatestTimings -> {got} frames");
            for (uint i = 0; i < got; i++)
                Report($"  frame[{i}] cpuFrameTime={timings[i].cpuFrameTime:F3}ms " +
                       $"gpuFrameTime={timings[i].gpuFrameTime:F3}ms");

            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            int shown = 0;
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (d.Name == null) continue;
                if (d.Name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Report($"  recorder '{d.Name}' category={d.Category}");
                shown++;
            }
            Report($"available profiler stats={handles.Count}, of which GPU-named={shown}");
        }

        // ── Calibration: does this clock see the quantity the band changes? ────────────────────────────

        /// <summary>
        /// Ordinal zero, before any band number is trusted: duplicate ONE arm's mesh into 1/2/4/8 renderers
        /// and confirm the clock grows with the draw load. An instrument that reads flat here reads flat for
        /// the band too, and a null band result from it would mean nothing.
        ///
        /// <para>Two ladders, because they separate what the band actually adds. <b>Full</b> copies sit on
        /// top of each other — vertex work AND fragment work multiply (the painter contract leaves ZWrite
        /// off, so there is no early-z rejection to hide the overdraw). <b>Pinhead</b> copies are shrunk to
        /// about one pixel — their vertices are still transformed and submitted while they cover almost no
        /// fragments, which is the vertex-only sensitivity the band's 3x vertex count depends on.</para>
        ///
        /// <para>The band-OFF arm is the one duplicated, deliberately: a pinhead-scaled band-ON mesh would
        /// displace its outer ring by one DEVICE pixel measured in world metres, which at that scale is
        /// enormous relative to the object and would not be the same geometry at all.</para>
        /// </summary>
        [Test]
        public void Calibrate_ClockSeesDrawLoad()
        {
            var f = Corpus[0];
            using var scene = Scene.Create();
            using var mesh = MeshArm.Build(f, suppressBand: true);
            Report($"calibration fixture={f.File} verts={mesh.VertexCount} tris={mesh.TriangleCount}");

            foreach (bool pinhead in new[] { false, true })
            {
                foreach (int copies in new[] { 1, 2, 4, 8 })
                {
                    using var placed = scene.Place(mesh, copies, pinhead);
                    double[] s = scene.Sample(placed);
                    Report($"calibrate {(pinhead ? "pinhead" : "full   ")} copies={copies,2} " +
                           $"median={Median(s):F4}ms min={Min(s):F4} max={Max(s):F4}");
                }
            }
        }

        // ── The measurement ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Band on versus band off, ABAB-interleaved, over the whole fixture corpus at two magnifications.
        ///
        /// <para>Interleaved rather than run-one-arm-then-the-other because this machine hosts other agents:
        /// a monotonic drift from someone else's build would otherwise land entirely on whichever arm ran
        /// second and read as a band cost. The paired per-sample difference reported alongside the two
        /// medians is what survives that.</para>
        ///
        /// <para>Magnification 1 frames the whole tile; magnification 4 crops to a quarter of it in each
        /// axis, which lengthens the on-screen boundary the band's one-pixel strip has to cover while the
        /// submitted vertex count stays identical — the two halves of the band's cost, separated.</para>
        /// </summary>
        [Test]
        public void Measure_BandOnVersusBandOff()
        {
            using var scene = Scene.Create();

            foreach (var f in Corpus)
            {
                using var withBand = MeshArm.Build(f, suppressBand: false);
                using var without  = MeshArm.Build(f, suppressBand: true);

                Report($"fixture={f.File} z={f.Z} layer={f.Layer} " +
                       $"verts_band={withBand.VertexCount} verts_noband={without.VertexCount} " +
                       $"ratio={(double)withBand.VertexCount / math.max(1, without.VertexCount):F3} " +
                       $"tris_band={withBand.TriangleCount} tris_noband={without.TriangleCount}");

                foreach (float mag in new[] { 1f, 4f })
                {
                    using var onPlaced  = scene.Place(withBand, copies: 1, pinhead: false);
                    using var offPlaced = scene.Place(without,  copies: 1, pinhead: false);
                    scene.SetMagnification(mag);

                    // Precondition, before any timing: the two arms must actually RENDER differently. If
                    // they do not, the band never reached the framebuffer and every number below would be a
                    // measurement of nothing.
                    int differing = scene.CountDifferingPixels(onPlaced, offPlaced, out bool bothBlank);
                    if (bothBlank)
                    {
                        Report($"  mag={mag} SKIPPED: both arms render an all-black frame — no GPU context");
                        continue;
                    }
                    if (differing == 0)
                    {
                        Report($"  mag={mag} SKIPPED: arms are pixel-identical — the band is not rendering");
                        continue;
                    }

                    var on = new List<double>(Samples);
                    var off = new List<double>(Samples);
                    var paired = new List<double>(Samples);
                    for (int s = 0; s < WarmupSamples + Samples; s++)
                    {
                        double tOn  = scene.SampleOnce(onPlaced);
                        double tOff = scene.SampleOnce(offPlaced);
                        if (s < WarmupSamples) continue;
                        on.Add(tOn);
                        off.Add(tOff);
                        paired.Add(tOn - tOff);
                    }

                    double[] onA = on.ToArray(), offA = off.ToArray(), pairA = paired.ToArray();
                    Report($"  mag={mag} differingPx={differing} " +
                           $"on_median={Median(onA):F4}ms on_max={Max(onA):F4} " +
                           $"off_median={Median(offA):F4}ms off_max={Max(offA):F4}");
                    Report($"  mag={mag} paired_delta median={Median(pairA):F4}ms " +
                           $"p25={Percentile(pairA, 0.25):F4} p75={Percentile(pairA, 0.75):F4} " +
                           $"min={Min(pairA):F4} max={Max(pairA):F4}");
                }
            }
        }

        /// <summary>
        /// The band's CPU mesh-build cost, reported separately and in its own unit: this is paid once per
        /// tile BUILD, not once per frame, so folding it into a frame-time answer would be wrong.
        /// <see cref="TestTileMeshBuilder.BuildFillFromLayer"/> completes the whole fill graph synchronously,
        /// so the wall clock around it is the graph's cost plus the mesh write.
        /// </summary>
        [Test]
        public void Measure_BandMeshBuildCost()
        {
            const int Builds = 12;
            foreach (var f in Corpus)
            {
                var on = new List<double>(Builds);
                var off = new List<double>(Builds);
                for (int i = 0; i < Builds + 2; i++)
                {
                    var swOn = Stopwatch.StartNew();
                    using (MeshArm.Build(f, suppressBand: false)) { }
                    swOn.Stop();
                    var swOff = Stopwatch.StartNew();
                    using (MeshArm.Build(f, suppressBand: true)) { }
                    swOff.Stop();
                    if (i < 2) continue;   // warmup: Burst/job first-touch
                    on.Add(swOn.Elapsed.TotalMilliseconds);
                    off.Add(swOff.Elapsed.TotalMilliseconds);
                }
                Report($"build {f.File} band_median={Median(on.ToArray()):F3}ms " +
                       $"noband_median={Median(off.ToArray()):F3}ms " +
                       $"delta={Median(on.ToArray()) - Median(off.ToArray()):F3}ms");
            }
        }

        // ── Scene: camera, lit ambient, off-screen target, and the clock ───────────────────────────────

        /// <summary>
        /// The timed scene: an off-screen target, a top-down orthographic camera framing the fitted tile,
        /// and the lit-ambient recipe (quality level 0, flat ambient, one directional light) that
        /// <c>TiltedGroundScene.Create</c> established — restored on dispose, because it is process-global
        /// state that would otherwise corrupt every lit render for the rest of the batch process.
        ///
        /// <para>Orthographic and untilted on purpose: under an orthographic projection
        /// <c>MapPixelsToWorld</c>'s w-ratio is exactly 1, so the band is exactly one device pixel wide
        /// everywhere in frame and the fragment count it adds is a clean function of on-screen perimeter.
        /// </para>
        /// </summary>
        private sealed class Scene : IDisposable
        {
            public Camera Cam;
            private GameObject _camGo, _lightGo;
            private RenderTexture _rt;
            private Texture2D _drain;      // 1x1 readback target — the terminal GPU drain
            private Texture2D _full;       // full-frame readback, for the arms-differ precondition only
            private (int quality, UnityEngine.Rendering.AmbientMode mode, Color light) _savedAmbient;

            public static Scene Create()
            {
                var s = new Scene();
                s._savedAmbient = (QualitySettings.GetQualityLevel(),
                                   RenderSettings.ambientMode, RenderSettings.ambientLight);
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

                s._lightGo = new GameObject("BandCost_DirLight");
                s._lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                var light = s._lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;

                s._rt = new RenderTexture(RtPx, RtPx, 24, RenderTextureFormat.ARGB32);
                s._rt.Create();
                s._drain = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                s._full  = new Texture2D(RtPx, RtPx, TextureFormat.RGBA32, false);

                s._camGo = new GameObject("BandCost_Camera");
                s.Cam = s._camGo.AddComponent<Camera>();
                s.Cam.enabled         = false;      // manual Render() only
                s.Cam.targetTexture   = s._rt;
                s.Cam.clearFlags      = CameraClearFlags.SolidColor;
                s.Cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
                s.Cam.orthographic    = true;
                s.Cam.nearClipPlane   = 0.1f;
                s.Cam.farClipPlane    = 2000f;
                s._camGo.transform.position = new Vector3(0f, 500f, 0f);
                s._camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                s.SetMagnification(1f);
                return s;
            }

            /// <summary>Frames 1/<paramref name="mag"/> of the tile in each axis. Vertex submission is
            /// unchanged by this; on-screen boundary length is not.</summary>
            public void SetMagnification(float mag) => Cam.orthographicSize = ViewSize * 0.5f / mag;

            /// <summary>Instantiates <paramref name="copies"/> renderers sharing one mesh and material,
            /// fitted to <see cref="ViewSize"/>.
            ///
            /// <para><paramref name="pinhead"/> shrinks every copy to a near-zero on-screen footprint
            /// instead of moving it out of frame. Moving it out would be wrong: Unity culls a renderer whose
            /// BOUNDS miss the frustum, so an off-frustum copy submits no vertices at all and that ladder
            /// would measure culling and read flat. A pinhead copy stays inside the frustum, so every one of
            /// its vertices goes through the vertex shader while covering about one pixel — which isolates
            /// the vertex half of the band's cost.</para></summary>
            public Placed Place(MeshArm arm, int copies, bool pinhead)
            {
                var root = new GameObject("BandCost_Root");
                Bounds b = arm.Mesh.bounds;
                float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z), 1e-6f);
                float scale  = ViewSize / maxDim;

                for (int i = 0; i < copies; i++)
                {
                    var go = new GameObject($"copy{i}");
                    go.transform.SetParent(root.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = arm.Mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = arm.Material;
                    float s = pinhead ? scale * 0.0005f : scale;
                    go.transform.localScale    = Vector3.one * s;
                    go.transform.localPosition = -b.center * s;
                }
                root.SetActive(false);
                return new Placed(root);
            }

            /// <summary>One timed sample: <see cref="RendersPerSample"/> bare renders and one terminal
            /// readback that blocks until the GPU has finished them, in milliseconds per render.</summary>
            public double SampleOnce(Placed placed)
            {
                placed.Root.SetActive(true);
                try
                {
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < RendersPerSample; i++) Cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = _rt;
                    _drain.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);   // blocks on the GPU
                    _drain.Apply(false);
                    RenderTexture.active = prev;
                    sw.Stop();
                    return sw.Elapsed.TotalMilliseconds / RendersPerSample;
                }
                finally { placed.Root.SetActive(false); }
            }

            /// <summary>Warmed distribution for one placement — used by the calibration ladder, which has no
            /// second arm to interleave against.</summary>
            public double[] Sample(Placed placed)
            {
                var acc = new List<double>(Samples);
                for (int s = 0; s < WarmupSamples + Samples; s++)
                {
                    double t = SampleOnce(placed);
                    if (s >= WarmupSamples) acc.Add(t);
                }
                return acc.ToArray();
            }

            /// <summary>Pixels that differ between the two arms' rendered frames — the precondition that
            /// makes a timing comparison meaningful at all. <paramref name="bothBlank"/> distinguishes "no
            /// GPU context" from "the band changes nothing".</summary>
            public int CountDifferingPixels(Placed a, Placed b, out bool bothBlank)
            {
                byte[] pa = Capture(a), pb = Capture(b);
                bool blankA = true, blankB = true;
                int differing = 0;
                for (int i = 0; i < pa.Length; i += 4)
                {
                    if (pa[i] != 0 || pa[i + 1] != 0 || pa[i + 2] != 0) blankA = false;
                    if (pb[i] != 0 || pb[i + 1] != 0 || pb[i + 2] != 0) blankB = false;
                    if (pa[i] != pb[i] || pa[i + 1] != pb[i + 1] || pa[i + 2] != pb[i + 2]) differing++;
                }
                bothBlank = blankA && blankB;
                return differing;
            }

            private byte[] Capture(Placed placed)
            {
                placed.Root.SetActive(true);
                try
                {
                    Cam.Render();
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = _rt;
                    _full.ReadPixels(new Rect(0, 0, RtPx, RtPx), 0, 0, false);
                    _full.Apply(false);
                    RenderTexture.active = prev;
                    return _full.GetRawTextureData();
                }
                finally { placed.Root.SetActive(false); }
            }

            public void Dispose()
            {
                if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
                if (_lightGo != null) UnityEngine.Object.DestroyImmediate(_lightGo);
                if (_rt != null) { _rt.Release(); UnityEngine.Object.DestroyImmediate(_rt); }
                if (_drain != null) UnityEngine.Object.DestroyImmediate(_drain);
                if (_full != null) UnityEngine.Object.DestroyImmediate(_full);
                QualitySettings.SetQualityLevel(_savedAmbient.quality, false);
                RenderSettings.ambientMode  = _savedAmbient.mode;
                RenderSettings.ambientLight = _savedAmbient.light;
            }
        }

        /// <summary>A placed set of renderers, inactive except while it is being rendered.</summary>
        private sealed class Placed : IDisposable
        {
            public readonly GameObject Root;
            public Placed(GameObject root) => Root = root;
            public void Dispose() { if (Root != null) UnityEngine.Object.DestroyImmediate(Root); }
        }

        // ── One arm's mesh + material ──────────────────────────────────────────────────────────────────

        /// <summary>One arm: the fill mesh a fixture produces with the band emitted or suppressed, plus the
        /// material it draws with. The material is a PLAIN material on the committed fill shader with the
        /// painter contract applied — the same construction <c>FillSceneHelper.BuildFillGo</c> uses, and for
        /// the same reason (a runtime Material Variant would not take local keyword changes).</summary>
        private sealed class MeshArm : IDisposable
        {
            public Mesh Mesh;
            public Material Material;
            public int VertexCount;
            public int TriangleCount;

            public static MeshArm Build((string File, int Z, int X, int Y, string Layer) f, bool suppressBand)
            {
                var id = new TileId { Z = f.Z, X = f.X, Y = f.Y };
                byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", f.File));
                using MvtTile tile = MvtDecoder.Decode(id, bytes);

                StyleDocument style = StyleParser.Parse(StyleJson(f.Layer));
                StyleLayer styleLayer = style.Layers[0];
                ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, tile);
                Assert.IsNotNull(mvtLayer, $"{f.File}: source-layer '{f.Layer}' must resolve");

                var selected = TestTileMeshBuilder.Select(styleLayer, mvtLayer, f.Z);
                Mesh mesh = TestTileMeshBuilder.BuildFillFromLayer(
                    mvtLayer, selected, ((Fill.StyleLayer)styleLayer).Paint, f.Z, id,
                    suppressBoundaryBand: suppressBand);
                Assert.IsNotNull(mesh, $"{f.File}: '{f.Layer}' produced no fill geometry");

                var shader = MapMaterialSetTestUtil.Load().FillMaterial.shader;
                var mat = new Material(shader) { name = "BandCost_Fill" };
                FillMaterialTweaker.ApplyPainterContract(mat);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Opacity", 1f);

                return new MeshArm
                {
                    Mesh = mesh,
                    Material = mat,
                    VertexCount = mesh.vertexCount,
                    TriangleCount = (int)(mesh.GetIndexCount(0) / 3),
                };
            }

            public void Dispose()
            {
                if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
                if (Material != null) UnityEngine.Object.DestroyImmediate(Material);
            }
        }

        /// <summary>Minimal one-fill-layer style so <c>SourceLayerResolver</c> binds the fixture's source
        /// layer. Mirrors <c>FillSceneHelper.BuildStyleLayerJson</c>, which is private to that helper.</summary>
        private static string StyleJson(string layerName) => @"{
  ""version"": 8,
  ""name"": ""BandCost"",
  ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
  ""layers"": [ {
      ""id"": """ + layerName + @""",
      ""type"": ""fill"",
      ""source"": ""maplibre"",
      ""source-layer"": """ + layerName + @""",
      ""paint"": { ""fill-color"": [""rgba"",200,200,200,1] }
  } ]
}";

        // ── Reporting + statistics ─────────────────────────────────────────────────────────────────────

        private static void Report(string line)
        {
            Debug.Log(Tag + line);
            TestContext.Out.WriteLine(Tag + line);
        }

        private static double Median(double[] xs) => Percentile(xs, 0.5);

        private static double Min(double[] xs) { var c = Sorted(xs); return c[0]; }

        private static double Max(double[] xs) { var c = Sorted(xs); return c[c.Length - 1]; }

        private static double Percentile(double[] xs, double p)
        {
            double[] c = Sorted(xs);
            int i = (int)math.clamp(math.round(p * (c.Length - 1)), 0, c.Length - 1);
            return c[i];
        }

        private static double[] Sorted(double[] xs)
        {
            var c = (double[])xs.Clone();
            Array.Sort(c);
            return c;
        }
    }
}
#endif
