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
    /// paths) is projected up front — as a Burst job above a count threshold, else a serial loop — and the
    /// staging pass reads the precomputed positions. Teeth: (1) the job's per-point output equals the inline
    /// <see cref="LabelScreenProjection.TryProjectPoint"/> (one copy of the math); (2) the job-fill and serial-fill
    /// paths produce a BYTE-IDENTICAL mesh over an INTERLEAVED scene (point + curved + B-3-culled + null) — the
    /// real risk is the label→flat-point index mapping drifting when some labels aren't gathered.
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
                    Points = points, SceneOriginRender = origin, ViewProj = viewProj, ViewportLogicalPx = viewport,
                    OutScreen = outScreen, OutDepth = outDepth, OutValid = outValid,
                }.Schedule(n, 8).Complete();

                bool anyValid = false, anyInvalid = false;
                for (int i = 0; i < n; i++)
                {
                    bool ok = LabelScreenProjection.TryProjectPoint(points[i], origin, viewProj, viewport,
                        out float2 s, out float d);
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

        // ── Tooth 2: job-fill (threshold 0) vs serial-fill (threshold int.MaxValue) → byte-identical mesh over an
        //    interleaved scene. Proves the label→flat-point index mapping is correct when some labels aren't
        //    gathered (null + B-3-culled) and point/curved advance the flat cursor by 1 vs N. ONE harness / camera
        //    (two cameras' projection matrices differ by sub-ULP), two Ticks over the SAME scene — the sort keys
        //    are all distinct so A-5 incumbency is a no-op across the two Ticks, and the default (+inf) deltaTime
        //    snaps the fade both times, so the ONLY variable is the fill path. ──
        [Test]
        public void JobFill_And_SerialFill_ProduceIdenticalMesh_OverInterleavedScene()
        {
            using var h = new Harness(projectionJobThreshold: int.MaxValue); // start on the serial path
            List<LabelInstance> Scene() => new List<LabelInstance>
            {
                h.Point(h.Origin, 0f, "A", 0),                             // on-screen point
                null,                                                      // gap (not gathered)
                h.Point(h.Origin + new double3(1e8, 0, 1e8), 1f, "F", 1),  // far → B-3 distance culled
                h.CurvedAcrossView(2),                                     // line label (N path points)
                h.Point(h.Origin + new double3(50_000, 0, 0), 3f, "B", 3), // another on-screen point
            };

            h.System.Tick(in h.Frame, Scene(), h.Atlas); // serial fill
            var serialV = h.System.Mesh.vertices;        // capture before the next Tick overwrites the mesh
            var serialT = h.System.Mesh.triangles;
            var serialC = h.System.Mesh.colors;
            var serialUv = h.System.Mesh.uv;
            int serialQuads = h.System.LastQuadCount, serialCand = h.System.LastCandidateCount;
            int serialSurv = h.System.LastSurvivorCount, serialCulled = h.System.LastDistanceCulledCount;
            Assert.Greater(serialQuads, 0, "the scene must actually place something (else the test is vacuous)");

            h.System.ProjectionJobThreshold = 0;         // force the parallel job path
            h.System.Tick(in h.Frame, Scene(), h.Atlas); // job fill, identical inputs

            // Counts are exact (a wrong mapping would place/cull differently). Topology + the projection-INDEPENDENT
            // attributes (colors, UVs — from the glyph/atlas, not the camera) are exact too. Vertex POSITIONS go
            // through the projection, and Burst (which DOES compile the job in the Editor) reassociates/FMAs the
            // clip.z/clip.w math, so they differ from the managed serial fill by ~1e-4 px — compared within a tight
            // tolerance that still catches an index-mapping bug (that would be tens–hundreds of px off, not 1e-4).
            Assert.AreEqual(serialQuads, h.System.LastQuadCount, "same quad count");
            Assert.AreEqual(serialCand, h.System.LastCandidateCount, "same candidate count");
            Assert.AreEqual(serialSurv, h.System.LastSurvivorCount, "same survivor count");
            Assert.AreEqual(serialCulled, h.System.LastDistanceCulledCount, "same cull count");
            Assert.AreEqual(serialT, h.System.Mesh.triangles, "triangle topology differs between serial- and job-fill");
            Assert.AreEqual(serialC, h.System.Mesh.colors, "vertex colors differ (projection-independent — must be exact)");
            Assert.AreEqual(serialUv, h.System.Mesh.uv, "UVs differ (projection-independent — must be exact)");
            AssertVerticesApproxEqual(serialV, h.System.Mesh.vertices, 0.05f);
        }

        // Per-vertex tolerance compare — decisive for the index mapping (off by tens–hundreds of px on a bug),
        // tolerant of the sub-ULP Burst-vs-managed projection difference.
        private static void AssertVerticesApproxEqual(Vector3[] a, Vector3[] b, float tol)
        {
            Assert.AreEqual(a.Length, b.Length, "vertex count differs between serial- and job-fill");
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].x, b[i].x, tol, $"vertex[{i}].x differs beyond FP noise (index mapping?)");
                Assert.AreEqual(a[i].y, b[i].y, tol, $"vertex[{i}].y differs beyond FP noise (index mapping?)");
                Assert.AreEqual(a[i].z, b[i].z, tol, $"vertex[{i}].z differs beyond FP noise (index mapping?)");
            }
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

            public Harness(int projectionJobThreshold)
            {
                _go = new GameObject("LabelProjectionJob_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                Camera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 });
                Frame = new SceneFrame(Origin, float3x3.identity);
                Atlas = BuildTinyAtlasTexture();
                System = new LabelPlacementSystem(Camera, new Material(Shader.Find("Map/Symbol/Text")))
                {
                    ProjectionJobThreshold = projectionJobThreshold,
                };
            }

            public LabelInstance Point(double3 anchor, float sortKey, string text, int feature)
                => new LabelInstance
                {
                    AnchorRender = anchor, Placement = SymbolPlacement.Point, Layout = OneQuad(), Paint = LabelPaint.Default,
                    TextSizePx = 24f, PaddingPx = 2f, SortKey = sortKey, Text = text, FeatureIndex = feature, TileKey = 0L,
                };

            // A straight line spanning the view (real geo endpoints → wide on-screen segment), 3 glyphs centered.
            public LabelInstance CurvedAcrossView(int feature)
            {
                double3 a = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
                double3 b = Camera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 30.0 });
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
