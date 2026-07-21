// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
// NOT registered in core-tests.csproj.
//
// Epic A / A1 (world-anchored-labels-design.md §11 A1) — the two A0-review findings that must run through
// the REAL production path (not A0's hand-built test scaffold), plus the no-leak tooth:
//
//   A0-F2 (real-emit upright): A0's Y-negation fix lived only in test scaffold (BuildOneGlyphWorldMesh).
//     This renders a point label through the REAL LabelPlacementSystem.Tick (which now produces the world
//     mesh via BillboardMath.BuildWorldQuad) and asserts the SAME upright check WorldSymbolAbRenderSnapshotTests
//     already pins for the scaffold — now against production.
//
//   NEW-F1 (nonzero AnchorLocal through the real builder+shader): emits the SAME glyph twice through the
//     REAL Tick, differing ONLY in TileKey — one whose tile origin equals the anchor (AnchorLocal == 0, the
//     scaffold's degenerate case) and one whose tile origin does NOT (AnchorLocal != 0, a real Level-1 RTC
//     bake) — asserts the two renders are pixel-equivalent (the RTC cancellation, §3.4, running through
//     TransformObjectToHClip for real).
//
// Both reuse WorldSymbolAbRenderSnapshotTests' fixture-glyph + camera setup and WorldSymbolInkAnalysis's ink
// helpers (shared, not duplicated — see that file's header).

using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldPointEmitRenderTests
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        private static (GlyphAtlasTexture texture, TextLayoutResult layout) BuildGlyphA()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u]);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            TextLayoutResult layout = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default);
            return (texture, layout);
        }

        // ── A0-F2: real-emit upright ────────────────────────────────────────────────────────────────────

        [Test]
        public void RealTick_PointLabel_RendersUpright_ThroughProductionBuildWorldQuad()
        {
            var (atlasTexture, layout) = BuildGlyphA();

            var camGo = new GameObject("WorldPointEmit_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(mapCamera.Projection.Project(lookAt), float3x3.identity);

            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14); // Risk R1: a realistic tile, not TileKey=0

            var label = new LabelInstance
            {
                AnchorRender = anchorRender, Layout = layout, Paint = LabelPaint.Default,
                TextSizePx = 220f, SortKey = 0f, FeatureIndex = 0, TileKey = tileKey,
            };

            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var snap = new SnapshotRenderer(Size, Size);
            try
            {
                system.Tick(in frame, new[] { label }, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the label must not be culled.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, LabelKind.Text), "the world presenter must be showing.");

                snap.Render(uCam);
                byte[] px = (byte[])snap.RawPixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, Size, Size);
                WorldSymbolInkAnalysis.AnalyzeInk(px, Size, Size,
                    out int minRow, out int maxRow, out _, out _, out _, out _, out int inkCount);
                Assert.Greater(inkCount, 50, "must render meaningful ink (not blank/GPU-context-failed).");

                WorldSymbolInkAnalysis.ThirdWidths(px, Size, Size, minRow, maxRow, out float topThird, out float bottomThird);
                Assert.Greater(bottomThird, topThird * 1.3f,
                    "'A' must render UPRIGHT through the REAL production BillboardMath.BuildWorldQuad (A0-F2) — " +
                    "if this fails mirrored (top third wider), the OffsetPx.y negation regressed.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── NEW-F1: nonzero AnchorLocal through the real builder + shader ──────────────────────────────

        [Test]
        public void RealTick_NonzeroAnchorLocal_IsPixelEquivalentToZeroAnchorLocalBaseline()
        {
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var projection = new WebMercatorProjection();

            // The label's anchor is chosen to be EXACTLY a tile's render-space origin (SW corner) — so a
            // label whose TileKey names THAT tile bakes AnchorLocal == 0 (the scaffold's degenerate case).
            // A DIFFERENT (neighbour) tile's origin differs from the anchor, so the SAME label's AnchorLocal
            // is genuinely nonzero there — the two must still land on the SAME real-world point (§3.4's RTC
            // cancellation), hence pixel-equivalent renders.
            TileId zeroTile = TestTileKeys.Containing(lookAt, zoom: 14);
            TileId nonzeroTile = new TileId { X = zeroTile.X + 1, Y = zeroTile.Y, Z = zeroTile.Z };
            double3 anchorRender = TileRenderOrigin.Project(zeroTile, projection);
            long zeroTileKey = SymbolFeatureExtractor.PackTileKey(zeroTile);
            long nonzeroTileKey = SymbolFeatureExtractor.PackTileKey(nonzeroTile);

            var (atlasTexture, layout) = BuildGlyphA();

            var camGo = new GameObject("WorldPointEmitAnchor_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0),
                projection: projection);
            var frame = new SceneFrame(mapCamera.Projection.Project(lookAt), float3x3.identity);

            byte[] zeroPixels, nonzeroPixels;
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                var zeroLabel = new LabelInstance
                {
                    AnchorRender = anchorRender, Layout = layout, Paint = LabelPaint.Default,
                    TextSizePx = 220f, SortKey = 0f, FeatureIndex = 0, TileKey = zeroTileKey,
                };
                using (var snapZero = new SnapshotRenderer(Size, Size))
                {
                    system.Tick(in frame, new[] { zeroLabel }, atlasTexture);
                    Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition (zero-AnchorLocal case): must not be culled.");
                    snapZero.Render(uCam);
                    zeroPixels = (byte[])snapZero.RawPixels.Clone();
                }

                var nonzeroLabel = new LabelInstance
                {
                    AnchorRender = anchorRender, Layout = layout, Paint = LabelPaint.Default,
                    TextSizePx = 220f, SortKey = 0f, FeatureIndex = 0, TileKey = nonzeroTileKey,
                };
                using (var snapNonzero = new SnapshotRenderer(Size, Size))
                {
                    system.Tick(in frame, new[] { nonzeroLabel }, atlasTexture);
                    Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition (nonzero-AnchorLocal case): must not be culled.");
                    snapNonzero.Render(uCam);
                    nonzeroPixels = (byte[])snapNonzero.RawPixels.Clone();
                }
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }

            WorldSymbolInkAnalysis.FlipRowsVertically(zeroPixels, Size, Size);
            WorldSymbolInkAnalysis.FlipRowsVertically(nonzeroPixels, Size, Size);
            WorldSymbolInkAnalysis.AnalyzeInk(zeroPixels, Size, Size,
                out _, out _, out _, out _, out float zeroCentroidRow, out float zeroCentroidCol, out int zeroInk);
            WorldSymbolInkAnalysis.AnalyzeInk(nonzeroPixels, Size, Size,
                out _, out _, out _, out _, out float nonzeroCentroidRow, out float nonzeroCentroidCol, out int nonzeroInk);

            Assert.Greater(zeroInk, 50, "zero-AnchorLocal baseline must render meaningful ink.");
            Assert.Greater(nonzeroInk, 50,
                "nonzero-AnchorLocal render must render meaningful ink — this is the tooth's whole point: a " +
                "stream-0 offset/stride bug that put POSITION on an all-zero neighbor would slip past an " +
                "all-zero baseline, but real nonzero position data (this render) runs it through TransformObjectToHClip.");

            Assert.That(nonzeroCentroidRow, Is.EqualTo(zeroCentroidRow).Within(3f), "ink centroid row must match (RTC cancellation, §3.4).");
            Assert.That(nonzeroCentroidCol, Is.EqualTo(zeroCentroidCol).Within(3f), "ink centroid col must match (RTC cancellation, §3.4).");

            float changedFraction = WorldSymbolInkAnalysis.ChangedPixelFraction(zeroPixels, nonzeroPixels, Size, Size);
            Assert.Less(changedFraction, 0.03f, $"changed-pixel fraction ({changedFraction:P1}) must stay tiny — same real-world point, different tile bake.");
        }

        // ── No-leak: WorldLabelRenderer destroys every mesh + presenter GameObject on Dispose ──────────

        [Test]
        public void Dispose_DestroysEveryWorldSlotMeshAndPresenter_NoLeak()
        {
            var (atlasTexture, layout) = BuildGlyphA();

            var camGo = new GameObject("WorldPointEmitLeak_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(mapCamera.Projection.Project(lookAt), float3x3.identity);

            int meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // Three DIFFERENT tiles over three Ticks → three distinct world slots created (one mesh + one
                // presenter each) — exercises the dictionary growing, not just one static slot.
                for (int i = 0; i < 3; i++)
                {
                    long tileKey = SymbolFeatureExtractor.PackTileKey(new TileId { Z = 12, X = 100 + i, Y = 200 });
                    var label = new LabelInstance
                    {
                        AnchorRender = frame.SceneOriginRender, Layout = layout, Paint = LabelPaint.Default,
                        TextSizePx = 40f, SortKey = 0f, FeatureIndex = i, TileKey = tileKey,
                    };
                    system.Tick(in frame, new[] { label }, atlasTexture);
                    Assert.AreEqual(1, system.LastQuadCount, $"tick {i} must place its label.");
                }

                int meshesWhileLive = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                Assert.Greater(meshesWhileLive, meshesBefore, "3 distinct world slots must have created live meshes.");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }

            int meshesAfterDispose = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            Assert.AreEqual(meshesBefore, meshesAfterDispose,
                "every world slot's mesh must be destroyed by LabelPlacementSystem.Dispose (via WorldLabelRenderer.Dispose) — no leak.");
        }
    }
}
