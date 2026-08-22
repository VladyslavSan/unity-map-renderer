// Unity EditMode only — needs a real Camera/Mesh/Material + the job runtime (NativeArray/IJobParallelFor).
// NOT registered in core-tests.csproj. NOTE: EditMode runs jobs via managed fallback (not Burst-compiled) — this
// validates the numerics + the index mapping; Burst-compile correctness comes only from ./Tools/run-tests.sh.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// B-2: the parallel symbol-projection pass. Every visible symbol's screen geometry (point anchors + line
    /// paths) is projected up front by the Burst <see cref="SymbolProjectionJob"/> — dispatched with .Run()
    /// (Burst inline, no Schedule/Complete, no count threshold) — and the staging pass reads the precomputed
    /// positions. Teeth: (1) the job's per-point output equals the inline
    /// <see cref="LabelScreenProjection.TryProjectPoint"/> (one copy of the math); (2) over an INTERLEAVED scene
    /// (point + curved + B-3-culled + null) the pass places the gathered labels and excludes the ungathered ones
    /// (the far point is culled, the null is skipped) with a STABLE mesh across repeated Ticks — the real risk is
    /// the label→flat-point index mapping drifting when some labels aren't gathered.
    /// </summary>
    [TestFixture]
    public class LabelProjectionJobTests
    {
        // ── Tooth 1: SymbolProjectionJob output == inline TryProjectPoint, per index. ──
        [Test]
        public void SymbolProjectionJob_MatchesInlineProjection_PerPoint()
        {
            const int n = 64;
            var rng = new System.Random(12345);
            double3 origin = new double3(1_000_000.0, 0.0, 2_000_000.0);
            double2 viewport = new double2(1280.0, 720.0);
            float4x4 viewProj = math.mul(
                float4x4.PerspectiveFov(math.radians(60f), (float)(viewport.x / viewport.y), 0.1f, 5000f),
                float4x4.Translate(new float3(0f, 0f, -800f)));

            var points = new NativeArray<double3>(n, Allocator.TempJob);
            var outScreen = new NativeArray<float2>(n, Allocator.TempJob);
            var outDepth = new NativeArray<float>(n, Allocator.TempJob);
            var outValid = new NativeArray<byte>(n, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++)
                    points[i] = origin + new double3(
                        (rng.NextDouble() - 0.5) * 4000.0, (rng.NextDouble() - 0.5) * 4000.0,
                        (rng.NextDouble() - 0.5) * 4000.0);

                new SymbolProjectionJob
                {
                    Points = points, SceneOriginRender = origin, Rebase = float3x3.identity, ViewProj = viewProj, ViewportLogicalPx = viewport,
                    OutScreen = outScreen, OutDepth = outDepth, OutValid = outValid,
                }.Schedule(n, 8).Complete();

                bool anyValid = false, anyInvalid = false;
                for (int i = 0; i < n; i++)
                {
                    bool ok = LabelScreenProjection.TryProjectPoint(points[i], origin, viewProj, viewport,
                        float3x3.identity, out float2 s, out float d);
                    Assert.AreEqual(ok, outValid[i] != 0, $"valid flag mismatch at {i}");
                    if (ok)
                    {
                        Assert.AreEqual(s.x, outScreen[i].x, 1e-4f, $"screen.x mismatch at {i}");
                        Assert.AreEqual(s.y, outScreen[i].y, 1e-4f, $"screen.y mismatch at {i}");
                        Assert.AreEqual(d, outDepth[i], 1e-4f, $"depth mismatch at {i}");
                    }
                    anyValid |= ok; anyInvalid |= !ok;
                }
                Assert.IsTrue(anyValid, "test matrix should project some points in front of the camera");
                Assert.IsTrue(anyInvalid, "…and some behind it, to exercise both branches");
            }
            finally
            {
                points.Dispose(); outScreen.Dispose(); outDepth.Dispose(); outValid.Dispose();
            }
        }

        // ── S2-T2: same parity, but with a NON-identity Rebase (a real globe rebase) — proves the Burst job
        //    carries the rotation identically to the inline (managed) seam, not just the identity fast-path. ──
        [Test]
        public void SymbolProjectionJob_MatchesInlineProjection_PerPoint_NonIdentityRebase()
        {
            const int n = 64;
            var rng = new System.Random(54321);
            var proj = new SphericalProjection();
            var lookAt = new GeoCoordinate { Latitude = 45.0, Longitude = 30.0 };
            double3 origin = proj.Project(lookAt);
            float3x3 rebase = math.transpose(proj.TangentBasisAt(lookAt));
            double2 viewport = new double2(1280.0, 720.0);
            float4x4 viewProj = math.mul(
                float4x4.PerspectiveFov(math.radians(60f), (float)(viewport.x / viewport.y), 0.1f, 5000f),
                float4x4.Translate(new float3(0f, 0f, -800f)));

            var points = new NativeArray<double3>(n, Allocator.TempJob);
            var outScreen = new NativeArray<float2>(n, Allocator.TempJob);
            var outDepth = new NativeArray<float>(n, Allocator.TempJob);
            var outValid = new NativeArray<byte>(n, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++)
                    points[i] = origin + new double3(
                        (rng.NextDouble() - 0.5) * 4000.0, (rng.NextDouble() - 0.5) * 4000.0,
                        (rng.NextDouble() - 0.5) * 4000.0);

                new SymbolProjectionJob
                {
                    Points = points, SceneOriginRender = origin, Rebase = rebase, ViewProj = viewProj, ViewportLogicalPx = viewport,
                    OutScreen = outScreen, OutDepth = outDepth, OutValid = outValid,
                }.Schedule(n, 8).Complete();

                bool anyValid = false, anyInvalid = false;
                for (int i = 0; i < n; i++)
                {
                    bool ok = LabelScreenProjection.TryProjectPoint(points[i], origin, viewProj, viewport,
                        rebase, out float2 s, out float d);
                    Assert.AreEqual(ok, outValid[i] != 0, $"valid flag mismatch at {i}");
                    if (ok)
                    {
                        // Tolerance widened vs Tooth 1's identity-rebase 1e-4f: the Burst-compiled math.mul(rebase, …)
                        // dot-products may FMA/reorder differently than the managed inline path, a legitimate
                        // sub-ULP-scale divergence at these pixel magnitudes (1 float32 ULP at ~1300 is ~1.5e-4) —
                        // NOT present on the identity rebase, where the two paths stay bit-identical (Tooth 1). Kept
                        // far tighter than the RED signal (a dropped/wrong rebase misses by tens of millions of px).
                        Assert.AreEqual(s.x, outScreen[i].x, 1e-2f, $"screen.x mismatch at {i}");
                        Assert.AreEqual(s.y, outScreen[i].y, 1e-2f, $"screen.y mismatch at {i}");
                        Assert.AreEqual(d, outDepth[i], 1e-4f, $"depth mismatch at {i}");
                    }
                    anyValid |= ok; anyInvalid |= !ok;
                }
                Assert.IsTrue(anyValid, "test matrix should project some points in front of the camera");
                Assert.IsTrue(anyInvalid, "…and some behind it, to exercise both branches");
            }
            finally
            {
                points.Dispose(); outScreen.Dispose(); outDepth.Dispose(); outValid.Dispose();
            }
        }

        // ── Tooth 2: over an INTERLEAVED scene (on-screen point + null gap + far-culled point + curved line + on-
        //    screen point) the .Run() projection pass must (a) exclude the ungathered labels — the null is skipped,
        //    the far point is B-3 distance-culled — and (b) still place the on-screen labels, with a mesh that is
        //    STABLE across repeated Ticks. This is the label→flat-point index-mapping tooth: a mapping that drifted
        //    when some labels aren't gathered (point/curved advance the flat cursor by 1 vs N; null/culled advance
        //    by 0) would mis-project the on-screen labels off-screen (quads→0) or corrupt the cull count. The
        //    stability check also guards the UninitializedMemory output buffers against a partial fill. Sort keys
        //    are distinct so A-5 incumbency is a no-op across Ticks, and the default (+inf) deltaTime snaps the fade
        //    both times, so a second Tick over identical inputs is byte-identical iff the fill is deterministic. ──
        [Test]
        public void JobFill_OverInterleavedScene_PlacesGathered_ExcludesUngathered_AndIsStable()
        {
            using var h = new Harness();
            List<LabelInstance> Scene() => new List<LabelInstance>
            {
                h.Point(h.Origin, 0f, "A", 0),                             // on-screen point
                null,                                                      // gap (not gathered)
                h.Point(h.Origin + new double3(1e8, 0, 1e8), 1f, "F", 1),  // far → B-3 distance culled
                h.CurvedAcrossView(2),                                     // line label (N path points)
                h.Point(h.Origin + new double3(50_000, 0, 0), 3f, "B", 3), // another on-screen point
            };

            // R3: the collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick (§2.6) —
            // duplicate the first Tick (same scene content — FadeIds are stable across separate Scene() calls
            // building structurally-identical labels) so the assertions below read a settled state.
            h.System.TickLabels(in h.Frame, Scene(), h.Atlas, h.Camera.Projection);
            h.System.TickLabels(in h.Frame, Scene(), h.Atlas, h.Camera.Projection);

            // Epic A / A1 (hardening round C) + Stage AC: the two POINT labels ("A"/"B") draw through the
            // WORLD path since A1, and the curved line ALSO draws through it since Stage AC — so the
            // index-mapping stability this tooth exists for is pinned entirely on the world surface (points
            // AND the curved label share ONE world text slot: same TileKey=0L/Slot=0/Kind=Text — see
            // Harness.Point/CurvedAcrossView).
            Assert.IsTrue(h.System.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh worldMesh0), "the world text slot must exist (2 on-screen points).");
            WorldMeshReadback.Read(worldMesh0, out WorldBillboardVertex[] firstWorldV, out float[] firstWorldOpacity);
            Assert.Greater(firstWorldV.Length, 0, "the two on-screen points must have emitted world vertices (mapping intact).");

            // (a) the far point is culled and the null contributes nothing; (b) the two on-screen points at minimum
            //     stage as candidates and something places — a broken index mapping would mis-project them off-
            //     screen, dropping candidates below 2 and/or quads to 0.
            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "the far point must be B-3 distance-culled");
            Assert.GreaterOrEqual(h.System.LastCandidateCount, 2, "the two on-screen points must stage (mapping intact)");
            Assert.Greater(h.System.LastQuadCount, 0, "the on-screen labels must place (else the scene is vacuous / mapping broken)");

            // Stability: an identical second Tick must reproduce the world mesh bit-for-bit (deterministic
            // fill over the UninitializedMemory buffers + a stable index mapping).
            h.System.TickLabels(in h.Frame, Scene(), h.Atlas, h.Camera.Projection);

            Assert.IsTrue(h.System.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh worldMesh1), "the world text slot must still exist.");
            WorldMeshReadback.Read(worldMesh1, out WorldBillboardVertex[] secondWorldV, out float[] secondWorldOpacity);
            Assert.AreEqual(firstWorldV, secondWorldV, "WORLD vertex data (AnchorLocal/ColorRGB/Uv/Page/Offset/AlignFlags) drifted across identical Ticks — the two point labels' index mapping is unstable.");
            Assert.AreEqual(firstWorldOpacity, secondWorldOpacity, "WORLD opacity stream drifted across identical Ticks.");
        }

        // ── Harness: a MapCamera + tiny atlas + label builders (point + a curved line spanning the view). ──
        private sealed class Harness : System.IDisposable
        {
            public readonly LabelPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly MapCamera Camera;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("LabelProjectionJob_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                Camera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 });
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                // Epic A / A1: point labels now draw through the world path — needs its own world base
                // material for the stability tooth to observe real world-mesh content.
                System = new LabelPlacementSystem(Camera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public LabelInstance Point(double3 anchor, float sortKey, string text, int feature)
                => new LabelInstance
                {
                    AnchorRender = anchor, Placement = SymbolPlacement.Point, Layout = OneQuad(), Paint = LabelPaint.Default,
                    TextSizePx = 24f, PaddingPx = 2f, SortKey = sortKey, Text = text, FeatureIndex = feature, TileKey = 0L,
                };

            // A straight line spanning the view (real geo endpoints → wide on-screen segment), 3 glyphs centered.
            // Endpoints are kept within the camera far distance (±2° ≈ ±222 km at this zoom, well inside the ~775 km
            // far) so the line is NOT far-distance culled — this tooth asserts only the FAR point "F" is culled.
            public LabelInstance CurvedAcrossView(int feature)
            {
                double3 a = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 18.0 });
                double3 b = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 22.0 });
                var path = new double3[] { a, b };
                return new LabelInstance
                {
                    Placement = SymbolPlacement.LineCenter, PathRender = path,
                    LineAnchors = new[] { new LineAnchor(0, 0.5f) },
                    CurvedGlyphs = new List<CurvedGlyph> { Glyph(0f), Glyph(24f), Glyph(48f) },
                    Paint = LabelPaint.Default, TextSizePx = 24f, PaddingPx = 2f, SortKey = 5f,
                    FeatureIndex = feature, TileKey = 0L, MaxAngleDeg = 45f, KeepUpright = true,
                };
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        private static CurvedGlyph Glyph(float arcCenter) => new CurvedGlyph
        {
            ArcCenter = arcCenter,
            Cell = new SymbolQuad
            {
                TopLeft = new float2(-5f, 8f), BottomRight = new float2(5f, -2f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static TextLayoutResult OneQuad() => new TextLayoutResult
        {
            Quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            },
            BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1,
        };

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }
    }
}
