// Unity EditMode only — an off-screen GPU render of the REAL text draw path. NOT registered in
// core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D).
//
// The teeth the TEXT path was owed for sub-pixel stability, the sibling of SymbolIconResamplingTests.
// Every other text render fixture draws ONE static frame, so none of them can see a defect whose whole
// signature is "the render changes when it should not". That is why the regression below shipped.
//
// MSAA is off project-wide (Assets/Settings/RPAsset.asset m_MSAA: 1, ProjectSettings/QualitySettings.asset
// antiAliasing: 0), so the SDF fragment's own coverage ramp is the ONLY antialiasing text has. A linear
// ramp of exactly ONE device pixel is the unique width whose sampled ink is invariant to sub-pixel phase:
// its integer shifts are a partition of unity, so the ink a stroke deposits is the same wherever the pixel
// grid falls. Narrow the ramp and the grid starts to matter — the ink freezes for several sub-pixel steps
// and then jumps, which on screen is a label whose glyphs morph as the map pans.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolTextResamplingTests
    {
        private const int Size = 256;

        /// <summary>Phase step of the sweep, device px. Eight steps span one FULL device pixel — a partial
        /// sweep could sit entirely inside one tread of the staircase and read smooth.</summary>
        private const float PhaseStepPx = 0.125f;

        /// <summary>
        /// The two-sided bound's lower half, device px: every step of the sweep must advance the ink by at
        /// least a QUARTER of the ideal <see cref="PhaseStepPx"/>. This is the staircase's TREAD — the glyph
        /// frozen against a map that is still panning.
        ///
        /// <para>Measured smallest step: <c>0.0091px</c> at band 0.4 (the regression), <c>0.0883px</c>
        /// at band 1.0. Ideal is a uniform 0.125px.</para>
        /// </summary>
        private const float MinCentroidStepPx = 0.25f * PhaseStepPx;

        /// <summary>
        /// The upper half of the same two-sided bound — the staircase's RISER, all the motion of a whole
        /// tread released in one step.
        ///
        /// <para>Measured largest step: <c>0.2873px</c> at band 0.4, <c>0.2111px</c> at band 1.0.
        /// Both bounds are a fixed ratio of the sample pitch, not values fitted to a run — widening
        /// either to pass would defeat the tooth.</para>
        /// </summary>
        private const float MaxCentroidStepPx = 3f * PhaseStepPx;

        // ── the probe glyph ───────────────────────────────────────────────────────────────────────────

        /// <summary>Cell size of the synthetic glyph, in atlas texels. Its own metrics are this less the SDF
        /// buffer border on each side.</summary>
        private const int ProbeCellPx = 24;

        /// <summary>Width of the probe's vertical stem, in texels. Wide enough that the two edge ramps never
        /// meet, narrow enough that the whole cell holds the field's full range.</summary>
        private const int ProbeStemPx = 6;

        /// <summary>The probe's codepoint — 'I', so the real shaper resolves it like any other glyph.</summary>
        private const uint ProbeCodepoint = 'I';

        [Test]
        public void GlyphEdge_TracksSubPixelPhase_DoesNotFreezeAndJump()
        {
            var atlas = new GlyphAtlas();
            atlas.Append(BuildStemGlyph(), fontId: 0);
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);
            try
            {
                Assert.IsNotNull(atlasTexture.Texture,
                    "precondition: the glyph atlas texture must exist or nothing places.");
                AssertProbeFieldIsLinear(atlas);

                var phasesPx = new float[8];
                for (int i = 0; i < phasesPx.Length; i++) phasesPx[i] = i * PhaseStepPx;
                var centroids = new float[phasesPx.Length];
                for (int i = 0; i < phasesPx.Length; i++)
                    centroids[i] = RenderAndMeasureInkCentroidX(atlas, atlasTexture, phasesPx[i]);

                float smallestStep = float.MaxValue, largestStep = float.MinValue;
                int smallestStepAt = 0, largestStepAt = 0;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float step = i == 0 ? float.NaN : centroids[i] - centroids[i - 1];
                    report.Append($"\n  phase {phasesPx[i]:F3}px → centroid {centroids[i]:F4}px" +
                                  (i == 0 ? "" : $" (step {step:+0.0000;-0.0000})"));
                    if (i > 0 && step < smallestStep) { smallestStep = step; smallestStepAt = i; }
                    if (i > 0 && step > largestStep) { largestStep = step; largestStepAt = i; }
                }

                // Record the sweep unconditionally: the PASSING margins are what tell the next person whether
                // these bounds are comfortable or a flake waiting for a different GPU.
                TestContext.Out.WriteLine($"glyph-edge phase sweep:{report}");
                TestContext.Out.WriteLine(
                    $"  smallest step {smallestStep:F4}px (bound {MinCentroidStepPx}) | " +
                    $"largest step {largestStep:F4}px (bound {MaxCentroidStepPx}) | ideal {PhaseStepPx}");

                Assert.Greater(smallestStep, MinCentroidStepPx,
                    $"a glyph's ink must ADVANCE for every sub-pixel shift of its quad. Smallest advance was " +
                    $"{smallestStep:F4}px at phase {phasesPx[smallestStepAt]:F3}px (bound " +
                    $"{MinCentroidStepPx}px, ideal {PhaseStepPx}px). A step at or near ZERO means the coverage " +
                    $"ramp is too narrow for a pixel centre to land in it, so the ink holds still — on screen, " +
                    $"a label whose strokes freeze and then snap as the map pans.{report}");

                Assert.Less(largestStep, MaxCentroidStepPx,
                    $"a glyph's ink must advance SMOOTHLY, not in jumps. Largest advance was " +
                    $"{largestStep:F4}px at phase {phasesPx[largestStepAt]:F3}px (bound {MaxCentroidStepPx}px, " +
                    $"ideal {PhaseStepPx}px). A large step is the other half of the same staircase — the ink " +
                    $"releases a whole tread's worth of motion in one frame.{report}");
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        /// <summary>
        /// The band width lives in TWO places — the shader's declared default and the committed material —
        /// and only the material's value ships. The sweep above renders a material built straight from the
        /// shader, so it pins the default; this pins the committed asset to the same value. A value fixed in
        /// one copy and left stale in the other is what produced the regression.
        /// </summary>
        [Test]
        public void ShippedMaterial_CarriesTheSameAaBandAsTheShaderDefault()
        {
            int band = Shader.PropertyToID("_SdfAaDevicePx");
            Material shipped = MapMaterialSetTestUtil.Load().SymbolTextWorld;
            var fresh = new Material(Shader.Find("Map/Symbol/TextWorld"));
            try
            {
                Assert.AreEqual(fresh.GetFloat(band), shipped.GetFloat(band), 1e-4f,
                    $"the committed MapSymbolTextWorld material's _SdfAaDevicePx ({shipped.GetFloat(band)}) " +
                    $"must match the shader's declared default ({fresh.GetFloat(band)}) — the sweep above only " +
                    $"proves the DEFAULT antialiases, and the material is what the map actually draws with.");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }

        // ── fixture ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A synthetic glyph whose field is an EXACT linear ramp in x: a vertical stem
        /// <see cref="ProbeStemPx"/> texels wide, centred in a <see cref="ProbeCellPx"/> cell, every row
        /// identical. The shipped fragment recovers a signed device-px distance as
        /// <c>(sample - iso) * screenPxRange</c>, so authoring the field with the matching slope makes the
        /// rendered edge position analytic — and a stem is horizontally symmetric, which is what keeps the
        /// ink centroid independent of the readback's transfer curve.
        /// </summary>
        private static SdfGlyph BuildStemGlyph()
        {
            var bitmap = new byte[ProbeCellPx * ProbeCellPx];
            for (int x = 0; x < ProbeCellPx; x++)
            {
                byte value = FieldByteAt(x + 0.5f);
                for (int y = 0; y < ProbeCellPx; y++) bitmap[y * ProbeCellPx + x] = value;
            }

            return new SdfGlyph
            {
                Codepoint = ProbeCodepoint,
                Width = ProbeCellPx - 2 * GlyphSdf.Buffer,
                Height = ProbeCellPx - 2 * GlyphSdf.Buffer,
                Left = 0,
                Top = -(ProbeCellPx - 2 * GlyphSdf.Buffer),
                Advance = ProbeCellPx,
                Bitmap = bitmap,
            };
        }

        /// <summary>The field's encoded value at a cell coordinate: the iso level offset by the signed
        /// distance to the stem edge, scaled by the bake's texel range.</summary>
        private static byte FieldByteAt(float cellX)
        {
            float signedPx = 0.5f * ProbeStemPx - math.abs(cellX - 0.5f * ProbeCellPx);
            float value = ProbeIso + signedPx / ProbeRangeTexels;
            return (byte)math.clamp(math.round(value * 255f), 0f, 255f);
        }

        /// <summary>The material's <c>_SdfEdge</c> — the on-disk fontnik iso (191/255), which the shader's
        /// own default carries.</summary>
        private const float ProbeIso = 0.75f;

        /// <summary>The material's <c>_SdfRangeTexels</c> — how many texels one unit of field value spans.</summary>
        private const float ProbeRangeTexels = 8f;

        /// <summary>
        /// Fixture guard: proves the authored field really does cross the iso where this test believes, and
        /// really is linear around the crossing. Without it a mis-scaled field would present as a resampling
        /// verdict rather than a broken probe.
        /// </summary>
        private static void AssertProbeFieldIsLinear(GlyphAtlas atlas)
        {
            Assert.IsTrue(atlas.TryGetEntry(0, ProbeCodepoint, out GlyphAtlasEntry entry),
                "the probe glyph must be in the atlas.");
            Assert.AreEqual(new int2(ProbeCellPx, ProbeCellPx), entry.CellSize,
                "the probe cell must be exactly the authored size — every distance below is in its texels.");

            byte[] page = atlas.PagePixels(entry.Page);
            int row = entry.AtlasOrigin.y + ProbeCellPx / 2;
            int left = entry.AtlasOrigin.x;
            int isoTexel = (ProbeCellPx - ProbeStemPx) / 2; // the texel whose LEFT boundary is the stem edge

            // The two texels straddling the stem's left edge sit half a texel either side of it, so a linear
            // field puts their mean exactly on the iso.
            int below = page[row * atlas.Size.x + left + isoTexel - 1];
            int above = page[row * atlas.Size.x + left + isoTexel];
            Assert.AreEqual(math.round(ProbeIso * 255f), 0.5f * (below + above), 1f,
                $"the field must cross the iso at the stem's edge — the straddling texels read {below}/{above}.");
            Assert.AreEqual(0, page[row * atlas.Size.x + left],
                "the field must have reached its outside clamp by the cell's edge — otherwise the quad's own " +
                "polygon edge, not the field, bounds the ink.");
        }

        // ── render + measure ──────────────────────────────────────────────────────────────────────────

        /// <summary>Minimal metrics over an already-appended atlas — enough to drive the real
        /// <see cref="CodepointTextShaper"/>/<see cref="TextQuadLayout"/> pipeline for one glyph.</summary>
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

        /// <summary>
        /// Renders the probe glyph shifted by <paramref name="phasePx"/> device px and returns the X centroid
        /// of its ink, weighted by per-pixel darkness against the white background. A centroid over a
        /// symmetric probe is preferred over an ink MASS: the readback is gamma-encoded, and a monotone
        /// transfer curve maps a symmetric profile to a symmetric profile but changes a raw sum.
        /// </summary>
        private static float RenderAndMeasureInkCentroidX(
            GlyphAtlas atlas, GlyphAtlasTexture atlasTexture, float phasePx)
        {
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest
            {
                Text = char.ConvertFromUtf32((int)ProbeCodepoint),
                Metrics = new AtlasMetrics(atlas),
            });

            // text-offset is in EMS and the render below is at scale 1 (textSizePx == OneEm), so dividing the
            // device-px phase by OneEm lands an exact device-px shift. dpr 1 is asserted at the render.
            var options = new TextLayoutOptions
            {
                Anchor = MapRenderer.Core.Text.TextAnchor.Center,
                Offset = new float2(phasePx / TextQuadLayout.OneEm, 0f),
                Justify = TextJustify.Auto,
                MaxWidthEm = TextLayoutOptions.Default.MaxWidthEm,
                LineHeightEm = TextLayoutOptions.Default.LineHeightEm,
            };
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, options, quads);
            Assert.AreEqual(1, quads.Count, "the probe must lay out exactly one glyph quad.");

            byte[] px = RenderText(atlasTexture, quads, bounds, out int width);

            double weighted = 0.0, total = 0.0;
            for (int i = 0, p = 0; i + 3 < px.Length; i += 4, p++)
            {
                double ink = (255.0 - px[i]) / 255.0; // black ink on white
                if (ink <= 0.004) continue;           // ignore readback noise
                weighted += ink * (p % width);
                total += ink;
            }

            Assert.Greater(total, 1.0, "the glyph must actually have drawn — no ink found in the frame.");
            return (float)(weighted / total);
        }

        /// <summary>
        /// Drives one text symbol through the real SymbolPlacementSystem → Map/Symbol/TextWorld path and
        /// returns the raw RGBA32 readback. The readback is the vertical mirror of on-screen (Unity's
        /// render-to-texture Y-flip), which is irrelevant here — the measurement is horizontal.
        /// </summary>
        private static byte[] RenderText(
            GlyphAtlasTexture atlasTexture, List<SymbolQuad> quads, TextLayoutBounds bounds, out int width)
        {
            var camGo = new GameObject("TextResampling_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));

            // The sweep converts a logical-px text-offset into an exact DEVICE-px phase, which only holds at
            // dpr 1. Assert it rather than assume it: a fixture default of 2 would halve every phase step and
            // quietly weaken the tooth into a sweep of half a pixel.
            Assert.AreEqual(1.0, mapCamera.DevicePixelRatio, 1e-9,
                "this fixture converts logical px to device px 1:1 — it requires dpr 1.");

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(
                    new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // A realistic containing tile keeps the world-anchored bake float32-safe (TileKey=0 is ~2e7 m
            // away) — the same note SymbolIconResamplingTests carries.
            long tileKey = TestTileKeys.PackedContaining(
                new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, quads, bounds.Min, bounds.Max,
                kind: SymbolKind.Text,
                paint: SymbolPaint.Default, // opaque black text, halo width 0 — no halo run is emitted
                textSizePx: TextQuadLayout.OneEm, // scale 1: one atlas texel draws at one device px
                allowOverlap: true,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: tileKey);

            var system = new SymbolPlacementSystem(
                mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // The collision verdict is harvested one Tick late (§2.6) — hence the duplicate tick.
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity);
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity);
                Assert.AreEqual(1, system.LastQuadCount, "the single glyph quad must place (not culled).");

                snap.Render(uCam);
                if (snap.IsAllBlack())
                    Assert.Inconclusive("render is all-black — no GPU context in this batch session " +
                                        "(see SnapshotRenderer.IsAllBlack).");

                width = snap.Width;
                return (byte[])snap.RawPixels.Clone();
            }
            finally
            {
                snap.Dispose();
                system.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
