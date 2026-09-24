// World-space symbol render-comparison and halo-color GPU/visual acceptance tests. Split by the CS0104
// bare-`Object` collision: SymbolWorldMotionTests.cs holds the System importers, so the two may not merge.
//
// Contents:
//   WorldCurvedAbRenderSnapshotTests  — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
//   WorldPointEmitRenderTests         — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
//   SymbolHaloColorRenderTests        — Unity-only: render test requiring a GPU context (SnapshotRenderer).
//   WorldCurvedMotionTests            — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, GPU render + CPU readback.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using CameraProperties = MapRenderer.Core.Geo.CameraProperties;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Common;
using Unity.Collections;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using Color = UnityEngine.Color;
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Unity.View;

namespace MapRenderer.Tests.Text.Placement
{
    // Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
    // NOT registered in core-tests.csproj. It renders ONLY the world CURVED path (a real Tick through
    // Map/Symbol/TextWorld) and asserts the asymmetric 'F' glyph's centroid and bounding box against goldens.
    //
    // Limitation: a golden catches a later shift or mirror, but cannot prove the rotation SIGN, because a
    // golden minted from a wrong sign enshrines it. A 45° diagonal AND a vertical line are swept; the diagonal
    // alone leaves a sign ambiguity. The fixture borrows the point layout as a quad factory, so a change to
    // TextLayoutOptions.Default's Center anchor moves the goldens by a pure translation.

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldCurvedAbRenderSnapshotTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldCurvedAbRenderSnapshotTests : BaseTestFixture
    {
        private const int Size = 512;
        private const float TextSizePx = 160f;

        // The CENTROID is the robust sign discriminator (a wrong sign moves it by tens of px), so it stays
        // tight. Bounding-box EDGES are AA-fragile and only confirm shape, so they get a looser margin.
        private const float GoldenCentroidTolerancePx = 4f;
        private const float GoldenBboxTolerancePx = 8f;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

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

        // 'F' (70) has no mirror symmetry, so a wrong rotation sense cannot alias to a correct render.
        // Internal so OffLookAtSymbolScene reuses this cell instead of a second decoder/shaper bootstrap.
        internal static (GlyphAtlasTexture texture, SymbolQuad quad) BuildGlyphF()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[70u], 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "F", Metrics = new AtlasMetrics(atlas) });
            // The POINT layout is only a quad factory here, so the cell inherits its Center anchor, which
            // production curved text never uses. A new centre anchor re-mints these goldens and nothing else.
            var layoutQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, layoutQuads);
            Assert.AreEqual(1, layoutQuads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            return (texture, layoutQuads[0]);
        }

        // Mercator, 3 orientations: 0° (horizontal — not decisive alone, kept for a broad sweep), 45°
        // (diagonal), 90° (vertical — resolves the diagonal's residual sign ambiguity, see this file's header).
        [Test]
        public void NewWorldPath_RendersUprightCurvedGlyph_MatchesGolden(
            [Values(0f, 45f, 90f)] float lineAngleDeg)
        {
            var (atlasTexture, quad) = BuildGlyphF();
            try
            {
                var camGo = Track(new GameObject("WorldCurvedAb_TestCamera"));
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(Size, Size, 0);
                uCam.clearFlags = CameraClearFlags.SolidColor;
                uCam.backgroundColor = Color.white;
                var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 12.0, heading: 0.0, tilt: 0.0));
                var frame = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(lookAt),
                    Rebase = float3x3.identity,
                };
                long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14); // a realistic tile, not TileKey=0

                (double3 pathA, double3 pathB) = ShortLineAt(uCam, frame, lineAngleDeg);

                {
                    Color32[] newPixels = RenderNewWorldPath(uCam, mapCamera, in frame, pathA, pathB, quad, atlasTexture, tileKey);

                    // Goldens for the optical-centred cell (docs/road-shields-design.md states the formula).
                    if (lineAngleDeg == 0f)
                        AssertGolden(newPixels, "0°", centroidRow: 234.9f, centroidCol: 250.9f, minRow: 189, maxRow: 302, minCol: 231, maxCol: 294);
                    else if (lineAngleDeg == 45f)
                        AssertGolden(newPixels, "45°", centroidRow: 240.9f, centroidCol: 244.0f, minRow: 180, maxRow: 301, minCol: 200, maxCol: 285);
                    else
                        AssertGolden(newPixels, "90°", centroidRow: 251.3f, centroidCol: 243.6f, minRow: 208, maxRow: 271, minCol: 198, maxCol: 311);
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // Tile-corner placement: TileKey names a NEIGHBOR of the containing tile, so the Level-1 RTC bake is a
        // whole tile span, not the small in-tile offset the main sweep carries.
        [Test]
        public void NewWorldPath_RendersUprightCurvedGlyph_MatchesGolden_NonzeroAnchorLocal()
        {
            var (atlasTexture, quad) = BuildGlyphF();
            try
            {
                var camGo = Track(new GameObject("WorldCurvedAbTileCorner_TestCamera"));
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(Size, Size, 0);
                uCam.clearFlags = CameraClearFlags.SolidColor;
                uCam.backgroundColor = Color.white;
                var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 12.0, heading: 0.0, tilt: 0.0));
                var frame = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(lookAt),
                    Rebase = float3x3.identity,
                };

                TileId containing = TestTileKeys.Containing(lookAt, zoom: 14);
                TileId neighbor = new TileId { X = containing.X + 1, Y = containing.Y, Z = containing.Z };
                long tileKey = SymbolTileKey.Pack(neighbor);

                (double3 pathA, double3 pathB) = ShortLineAt(uCam, frame, 45f);

                {
                    Color32[] newPixels = RenderNewWorldPath(uCam, mapCamera, in frame, pathA, pathB, quad, atlasTexture, tileKey);

                    AssertGolden(newPixels, "NonzeroAnchorLocal", centroidRow: 240.9f, centroidCol: 244.0f, minRow: 180, maxRow: 301, minCol: 200, maxCol: 285);
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // A globe rebase: the RTC cancellation and the shader's Jacobian must still hold once the
        // object-to-world transform carries a real rotation, not just a translation.
        [Test]
        public void NewWorldPath_RendersUprightCurvedGlyph_MatchesGolden_GlobeRebase()
        {
            var (atlasTexture, quad) = BuildGlyphF();
            try
            {
                var camGo = Track(new GameObject("WorldCurvedAbGlobe_TestCamera"));
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(Size, Size, 0);
                uCam.clearFlags = CameraClearFlags.SolidColor;
                uCam.backgroundColor = Color.white;
                var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
                var projection = new SphericalProjection();
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 12.0, heading: 0.0, tilt: 0.0),
                    projection: projection);
                float3x3 rebase = math.transpose(projection.TangentBasisAt(lookAt));
                var frame = new SceneFrame { SceneOriginRender = projection.Project(lookAt), Rebase = rebase };
                long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14);

                (double3 pathA, double3 pathB) = ShortLineAt(uCam, frame, 45f);

                {
                    Color32[] newPixels = RenderNewWorldPath(uCam, mapCamera, in frame, pathA, pathB, quad, atlasTexture, tileKey);

                    AssertGolden(newPixels, "GlobeRebase", centroidRow: 248.1f, centroidCol: 271.6f, minRow: 210, maxRow: 291, minCol: 205, maxCol: 331);
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // A short line across the look-at at `lineAngleDeg` in the render-space XZ plane (east=X, north=Z),
        // spanning altitude*0.02 so it stays on-screen.
        private static (double3, double3) ShortLineAt(Camera uCam, in SceneFrame frame, float lineAngleDeg)
        {
            double altitude = uCam.transform.position.y;
            double3 centre = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);
            double rad = math.radians(lineAngleDeg);
            double3 dir3 = new double3(math.cos(rad), 0.0, math.sin(rad));
            double halfLen = altitude * 0.01;
            return (centre - dir3 * halfLen, centre + dir3 * halfLen);
        }

        // MINT MODE: true prints the ink signature for a fresh golden instead of asserting. MUST be false
        // when committed. static readonly, not const, so the mint block does not raise CS0162.
        private static readonly bool MintGolden = false;

        private static void AssertGolden(Color32[] pixels, string label,
            float centroidRow, float centroidCol, int minRow, int maxRow, int minCol, int maxCol)
        {
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, Size, Size);
            WorldSymbolInkAnalysis.AnalyzeInk(pixels, Size, Size,
                out int actualMinRow, out int actualMaxRow, out int actualMinCol, out int actualMaxCol,
                out float actualCentroidRow, out float actualCentroidCol, out int ink);

            if (MintGolden)
            {
                Assert.Fail($"MINT {label}: centroidRow={actualCentroidRow:F1}f, centroidCol={actualCentroidCol:F1}f, " +
                    $"minRow={actualMinRow}, maxRow={actualMaxRow}, minCol={actualMinCol}, maxCol={actualMaxCol}, ink={ink}");
            }

            Assert.Greater(ink, 30, $"NEW path must render meaningful ink ({label}, not blank/GPU-context-failed).");

            Assert.That(actualCentroidRow, Is.EqualTo(centroidRow).Within(GoldenCentroidTolerancePx), $"ink centroid row must match the committed golden ({label}).");
            Assert.That(actualCentroidCol, Is.EqualTo(centroidCol).Within(GoldenCentroidTolerancePx), $"ink centroid col must match the committed golden ({label}).");
            Assert.That(actualMinRow, Is.EqualTo(minRow).Within(GoldenBboxTolerancePx), $"ink bbox top edge must match the committed golden ({label}).");
            Assert.That(actualMaxRow, Is.EqualTo(maxRow).Within(GoldenBboxTolerancePx), $"ink bbox bottom edge must match the committed golden ({label}).");
            Assert.That(actualMinCol, Is.EqualTo(minCol).Within(GoldenBboxTolerancePx), $"ink bbox left edge must match the committed golden ({label}).");
            Assert.That(actualMaxCol, Is.EqualTo(maxCol).Within(GoldenBboxTolerancePx), $"ink bbox right edge must match the committed golden ({label}).");
        }

        // ── a REAL curved symbol through a REAL SymbolPlacementSystem.Tick (curved routes to the world
        //    sink) → Map/Symbol/TextWorld. ────────────────────────────────────────────────────────────────
        private static Color32[] RenderNewWorldPath(Camera uCam, MapCamera mapCamera, in SceneFrame frame,
            double3 pathA, double3 pathB, in SymbolQuad quad, GlyphAtlasTexture atlasTexture, long tileKey)
        {
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer,
                glyphs: new System.Collections.Generic.List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = quad } },
                anchors: new[] { new LineAnchor(0, 0.5f) },
                path: new[] { pathA, pathB },
                placement: SymbolPlacement.LineCenter,
                paint: SymbolPaint.Default,
                textSizePx: TextSizePx,
                maxAngleDeg: 180f,
                keepUpright: false,
                featureIndex: 0,
                tileKey: tileKey);

            using var system = new SymbolPlacementSystem(mapCamera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the NEW world path must place the label.");

                using var snap = new SnapshotRenderer(Size, Size);
                snap.Render(uCam);
                return (Color32[])snap.Pixels.Pixels.Clone();
            }
        }
    }

    // Unity EditMode only. Point symbols through the REAL Tick render upright, render pixel-equivalent under a
    // zero and a nonzero AnchorLocal (the RTC cancellation), and leak nothing.

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldPointEmitRenderTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldPointEmitRenderTests : BaseTestFixture
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

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

        /// <summary>Internal (not private): <c>TiltFixtureSelfTests.ViewportPitchAlignedSymbol_…</c>
        /// reuses this one-glyph bootstrap under tilt rather than carrying a second copy
        /// (test-code-bloat convention — widen, don't duplicate-and-drag).</summary>
        internal static (GlyphAtlasTexture texture, List<SymbolQuad> quads, TextLayoutBounds bounds) BuildGlyphA()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);
            return (texture, quads, bounds);
        }

        // ── Real-emit upright ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void RealTick_PointSymbol_RendersUpright_ThroughProductionBuildWorldQuad()
        {
            var (atlasTexture, quads, bounds) = BuildGlyphA();

            var camGo = Track(new GameObject("WorldPointEmit_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14); // a realistic tile, not TileKey=0

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default, textSizePx: 220f, sortKey: 0f, featureIndex: 0, tileKey: tileKey);

            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            using var _atlas = atlasTexture;
            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                Assert.AreEqual(1, plan.CollectedCount, "precondition: the collect must yield the label.");
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the label must not be culled.");
                Assert.IsTrue(system.IsWorldSlotVisible(tileKey, 0, SymbolKind.Text), "the world presenter must be showing.");

                snap.Render(uCam);
                Color32[] px = (Color32[])snap.Pixels.Pixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, Size, Size);
                WorldSymbolInkAnalysis.AnalyzeInk(px, Size, Size,
                    out int minRow, out int maxRow, out _, out _, out _, out _, out int inkCount);
                Assert.Greater(inkCount, 50, "must render meaningful ink (not blank/GPU-context-failed).");

                WorldSymbolInkAnalysis.ThirdWidths(px, Size, Size, minRow, maxRow, out float topThird, out float bottomThird);
                Assert.Greater(bottomThird, topThird * 1.3f,
                    "'A' must render UPRIGHT through the REAL production BillboardMath.BuildWorldQuad (A0-F2) — " +
                    "if this fails mirrored (top third wider), the Offset.y negation regressed.");
            }
        }

        // ── nonzero AnchorLocal through the real builder + shader ──────────────────────────────────────

        [Test]
        public void RealTick_NonzeroAnchorLocal_IsPixelEquivalentToZeroAnchorLocalBaseline()
        {
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var projection = new WebMercatorProjection();

            // The anchor is a tile's render-space origin, so that TileKey bakes AnchorLocal == 0 and a
            // neighbour's bakes a nonzero one. Both must land on the SAME world point.
            TileId zeroTile = TestTileKeys.Containing(lookAt, zoom: 14);
            TileId nonzeroTile = new TileId { X = zeroTile.X + 1, Y = zeroTile.Y, Z = zeroTile.Z };
            double3 anchorRender = TileRenderOrigin.Project(zeroTile, projection);
            long zeroTileKey = SymbolTileKey.Pack(zeroTile);
            long nonzeroTileKey = SymbolTileKey.Pack(nonzeroTile);

            var (atlasTexture, quads, bounds) = BuildGlyphA();

            var camGo = Track(new GameObject("WorldPointEmitAnchor_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0),
                projection: projection);
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            Color32[] zeroPixels, nonzeroPixels;
            using var plan = new TestSymbolPlan(projection);
            using var _atlas = atlasTexture;
            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            {
                var zeroBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddPoint(zeroBuffer, anchorRender, quads, bounds.Min, bounds.Max,
                    paint: SymbolPaint.Default, textSizePx: 220f, sortKey: 0f, featureIndex: 0, tileKey: zeroTileKey);
                using (var snapZero = new SnapshotRenderer(Size, Size))
                {
                    // Duplicate Tick — the collision verdict is harvested one Tick late.
                    system.Tick(in frame, plan.Build(zeroBuffer), atlasTexture);
                    system.Tick(in frame, plan.Build(zeroBuffer), atlasTexture);
                    Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition (zero-AnchorLocal case): must not be culled.");
                    snapZero.Render(uCam);
                    zeroPixels = (Color32[])snapZero.Pixels.Pixels.Clone();
                }

                var nonzeroBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddPoint(nonzeroBuffer, anchorRender, quads, bounds.Min, bounds.Max,
                    paint: SymbolPaint.Default, textSizePx: 220f, sortKey: 0f, featureIndex: 0, tileKey: nonzeroTileKey);
                using (var snapNonzero = new SnapshotRenderer(Size, Size))
                {
                    // Duplicate Tick — the collision verdict is harvested one Tick late.
                    system.Tick(in frame, plan.Build(nonzeroBuffer), atlasTexture);
                    system.Tick(in frame, plan.Build(nonzeroBuffer), atlasTexture);
                    Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition (nonzero-AnchorLocal case): must not be culled.");
                    snapNonzero.Render(uCam);
                    nonzeroPixels = (Color32[])snapNonzero.Pixels.Pixels.Clone();
                }
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

            Assert.That(nonzeroCentroidRow, Is.EqualTo(zeroCentroidRow).Within(3f), "ink centroid row must match (RTC cancellation, docs/coordinates-and-projections.md § \"Render space & precision\").");
            Assert.That(nonzeroCentroidCol, Is.EqualTo(zeroCentroidCol).Within(3f), "ink centroid col must match (RTC cancellation, docs/coordinates-and-projections.md § \"Render space & precision\").");

            float changedFraction = WorldSymbolInkAnalysis.ChangedPixelFraction(zeroPixels, nonzeroPixels, Size, Size);
            Assert.Less(changedFraction, 0.03f, $"changed-pixel fraction ({changedFraction:P1}) must stay tiny — same real-world point, different tile bake.");
        }

        // ── No-leak: WorldSymbolRenderer destroys every mesh + presenter GameObject on Dispose ──────────

        [Test]
        public void Dispose_DestroysEveryWorldSlotMeshAndPresenter_NoLeak()
        {
            var (atlasTexture, quads, bounds) = BuildGlyphA();

            var camGo = Track(new GameObject("WorldPointEmitLeak_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var lookAt = new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };

            int meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            // Native buffers only — TestSymbolPlan creates no Mesh, so it cannot perturb the count either side.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // Three DIFFERENT tiles over three Ticks → three distinct world slots created (one mesh + one
                // presenter each) — exercises the dictionary growing, not just one static slot.
                for (int i = 0; i < 3; i++)
                {
                    long tileKey = SymbolTileKey.Pack(new TileId { Z = 12, X = 100 + i, Y = 200 });
                    var buffer = new SymbolTileBuffer();
                    // Non-local invariant: PointFadeId hashes (AnchorRender, MaterialIndex, Text, IconImage), so
                    // with one Text the three symbols share a FadeId. Iterations 1 and 2 would then pass on the
                    // previous iteration's placement. Distinct text keeps each id unique.
                    TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, quads, bounds.Min, bounds.Max,
                        text: "T" + i, paint: SymbolPaint.Default, textSizePx: 40f, sortKey: 0f, featureIndex: i, tileKey: tileKey);
                    // Collision verdicts apply one Tick late, so a second, identical Tick precedes the assert. It
                    // is fade-neutral: deltaTime defaults to +inf.
                    system.Tick(in frame, plan.Build(buffer), atlasTexture);
                    system.Tick(in frame, plan.Build(buffer), atlasTexture);
                    Assert.AreEqual(1, system.LastQuadCount, $"tick {i} must place its label.");
                }

                int meshesWhileLive = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                Assert.Greater(meshesWhileLive, meshesBefore, "3 distinct world slots must have created live meshes.");
            }
            finally
            {
                // NOT a bag candidate: system.Dispose() timing IS the measurement — meshesAfterDispose below
                // must be read AFTER this call, which a deferred `using` disposal (at method end) would break.
                system.Dispose();
                atlasTexture.Dispose();
            }

            int meshesAfterDispose = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            Assert.AreEqual(meshesBefore, meshesAfterDispose,
                "every world slot's mesh must be destroyed by SymbolPlacementSystem.Dispose (via WorldSymbolRenderer.Dispose) — no leak.");
        }
    }

    // Unity EditMode only — real Camera/RenderTexture/Material/Mesh, GPU render + CPU readback. The CURVED
    // mesh is built ONCE and frozen, then the camera PANS and ROTATES and only the object transform updates.
    // The glyph must (a) track its WORLD anchor and (b) RE-ORIENT to the live screen tangent. Non-obvious why:
    // a baked screen rotation passes (a) and fails (b). It uses a nonzero AnchorLocal and a diagonal Tangent.

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldCurvedMotionTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldCurvedMotionTests : BaseTestFixture
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

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

        [Test]
        public void WorldCurvedPath_TracksWorldAnchor_AndReorients_AfterCameraPanAndRotate()
        {
            // 1. Real SDF atlas, real fixture glyph 'F' — asymmetric (see WorldCurvedAbRenderSnapshotTests'
            //    header for why 'A' would be an unsafe choice for an orientation-sensitive tooth).
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[70u], 0);
            using var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "F", Metrics = new AtlasMetrics(atlas) });
            var layoutQuads = new List<SymbolQuad>();
            TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, layoutQuads);
            Assert.AreEqual(1, layoutQuads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            SymbolQuad quad = layoutQuads[0];
            const float textSizePx = 140f;

            var camGo = Track(new GameObject("WorldCurvedMotion_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var lookAt0 = new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt0, zoom: 12.0, heading: 0.0, tilt: 0.0));

            {
                SceneFrame frame0 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude }),
                    Rebase = float3x3.identity,
                };
                double altitude0 = uCam.transform.position.y;

                // The FROZEN anchor: a diagonal world Tangent and a NONZERO AnchorLocal, baked against a
                // NEIGHBOR tile's origin.
                double3 anchorRender = frame0.SceneOriginRender + new double3(0.0, 0.0, altitude0 * 0.02);
                var projection = new WebMercatorProjection();
                TileId containingTile = TestTileKeys.Containing(new GeoCoordinate { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude }, zoom: 14);
                TileId neighborTile = new TileId { X = containingTile.X + 1, Y = containingTile.Y, Z = containingTile.Z };
                double3 tileOriginRender = TileRenderOrigin.Project(neighborTile, projection);
                float3 anchorLocal = new float3(
                    (float)(anchorRender.x - tileOriginRender.x),
                    (float)(anchorRender.y - tileOriginRender.y),
                    (float)(anchorRender.z - tileOriginRender.z));

                float diagRad = math.radians(45f);
                var tangentLocal = math.normalize(new float3(math.cos(diagRad), 0f, math.sin(diagRad)));

                float4 textColor = SymbolPaint.Default.TextColor;
                Mesh worldMesh = Track(BuildOneGlyphWorldMeshCurved(quad, textSizePx, new float3(textColor.x, textColor.y, textColor.z),
                    anchorLocal, tangentLocal));
                Material worldMaterial = Track(new Material(Shader.Find("Map/Symbol/TextWorld")));
                worldMaterial.SetTexture(Shader.PropertyToID("_MainTex"), atlasTexture.Texture);
                double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                worldMaterial.SetVector(Shader.PropertyToID("_ScreenParamsLogical"),
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                GameObject presenterGo = Track(new GameObject("WorldCurvedMotion_Presenter"));
                var meshFilter = presenterGo.AddComponent<MeshFilter>();
                var meshRenderer = presenterGo.AddComponent<MeshRenderer>();
                meshFilter.sharedMesh = worldMesh;
                meshRenderer.sharedMaterial = worldMaterial;

                // ── Pose 0 ──────────────────────────────────────────────────────────────────────────────
                PlacePresenter(presenterGo, tileOriginRender, frame0);
                Color32[] pixels0 = RenderAndReadback(uCam, "world-curved-motion-pose0.png");
                WorldSymbolInkAnalysis.AnalyzeInk(pixels0, Size, Size,
                    out int minRow0, out int maxRow0, out int minCol0, out int maxCol0,
                    out float centroidRow0, out float centroidCol0, out int inkCount0);
                Assert.Greater(inkCount0, 30, "pose-0 render must show meaningful ink (not blank/GPU-context-failed).");
                float aspect0 = (float)(maxCol0 - minCol0 + 1) / (maxRow0 - minRow0 + 1);

                float4x4 viewProj0 = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                        anchorRender, frame0.SceneOriginRender, viewProj0, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen0, out _),
                    "anchor must project in front of the camera at pose 0.");

                // ── Pan AND rotate; the frozen mesh is reused and only its object transform is recomputed. ──
                // 0.5° at zoom 12 would move ~2900 px, off the 512 px frame, so the pan is 0.5° / 2^(12-8).
                var lookAt1 = new GeoCoordinate3D { Latitude = lookAt0.Latitude, Longitude = lookAt0.Longitude + 0.5 / 16.0, Altitude = 0.0 };
                mapCamera.SetProperties(new CameraProperties(lookAt1, zoom: 12.0, heading: 90.0, tilt: 0.0));
                mapCamera.SyncToCamera();
                SceneFrame frame1 = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = lookAt1.Latitude, Longitude = lookAt1.Longitude }),
                    Rebase = float3x3.identity,
                };

                PlacePresenter(presenterGo, tileOriginRender, frame1);
                Color32[] pixels1 = RenderAndReadback(uCam, "world-curved-motion-pose1.png");
                WorldSymbolInkAnalysis.AnalyzeInk(pixels1, Size, Size,
                    out int minRow1, out int maxRow1, out int minCol1, out int maxCol1,
                    out float centroidRow1, out float centroidCol1, out int inkCount1);
                Assert.Greater(inkCount1, 30, "pose-1 render must show meaningful ink (not blank/GPU-context-failed) — a mistracked anchor could also land off-screen.");
                float aspect1 = (float)(maxCol1 - minCol1 + 1) / (maxRow1 - minRow1 + 1);

                float4x4 viewProj1 = math.mul(
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                    SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));
                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                        anchorRender, frame1.SceneOriginRender, viewProj1, viewportLogicalPx, float3x3.identity,
                        out float2 anchorScreen1, out _),
                    "anchor must project in front of the camera at pose 1.");

                // (a) POSITION: the glyph shifts by the anchor's projected screen delta; Offset is a fixed
                //     additive clip-space term that cancels in a delta.
                float2 expectedDeltaScreen = anchorScreen1 - anchorScreen0;
                Assert.Greater(math.abs(expectedDeltaScreen.x) + math.abs(expectedDeltaScreen.y), 50f,
                    "the pan must produce a nontrivial expected screen shift, or this tooth is vacuous.");

                float actualDeltaCol = centroidCol1 - centroidCol0;
                float actualDeltaRowAsScreenY = centroidRow0 - centroidRow1; // row is top-origin, screen Y is bottom-origin
                Assert.That(actualDeltaCol, Is.EqualTo(expectedDeltaScreen.x).Within(20f),
                    $"X: the curved glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.x:F1}px, got {actualDeltaCol:F1}px) — if it stays near 0 instead, the world path is not tracking its anchor.");
                Assert.That(actualDeltaRowAsScreenY, Is.EqualTo(expectedDeltaScreen.y).Within(20f),
                    $"Y: the curved glyph must shift by the anchor's projected screen delta after the pan (expected {expectedDeltaScreen.y:F1}px, got {actualDeltaRowAsScreenY:F1}px).");

                // (b) ORIENTATION: a 90° heading change must swap the glyph's aspect ratio; a baked or absent
                //     rotation ignores the camera bearing and leaves it UNCHANGED.
                float aspectRatioChange = math.abs(aspect1 - aspect0) / math.max(aspect0, 1e-3f);
                Assert.Greater(aspectRatioChange, 0.25f,
                    $"the curved glyph must visibly RE-ORIENT after the heading rotation (aspect ratio {aspect0:F2} -> {aspect1:F2}, " +
                    $"change {aspectRatioChange:P0}) — a shallow implementation that bakes the screen rotation at decision time " +
                    "(or applies none) would leave this ratio unchanged regardless of the live camera bearing.");
            }
        }

        private static Color32[] RenderAndReadback(Camera uCam, string pngName)
        {
            using var snap = new SnapshotRenderer(Size, Size);
            snap.Render(uCam);
            Color32[] pixels = (Color32[])snap.Pixels.Pixels.Clone();
            snap.WritePng(pngName);
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, Size, Size);
            return pixels;
        }

        /// <summary>Places the presenter GameObject at Level-2: the mesh's baked AnchorLocal is
        /// relative to <paramref name="tileOriginRender"/> — recomputed against <paramref name="frame"/> —
        /// so the object's position alone carries the tile placement to its per-frame place. Mirrors exactly
        /// how a real world symbol renderer re-places a FROZEN mesh every frame; the mesh itself is never
        /// touched here.</summary>
        private static void PlacePresenter(GameObject presenterGo, in double3 tileOriginRender, in SceneFrame frame)
        {
            float3 objectPos = FloatingOrigin.TileToSceneRebased(tileOriginRender, frame.SceneOriginRender, frame.Rebase);
            presenterGo.transform.position = new Vector3(objectPos.x, objectPos.y, objectPos.z);
            presenterGo.transform.rotation = Quaternion.identity; // frame.Rebase is float3x3.identity on Mercator
        }

        /// <summary>Builds a one-glyph world-anchored CURVED <see cref="Mesh"/> via the REAL production
        /// <see cref="BillboardMath.BuildWorldQuad"/> (BillboardMathTests pins its corner math). Corners are
        /// unrotated with AlignFlags bit1 set, so the shader rotates <c>Offset</c> live from
        /// <paramref name="tangentLocal"/>'s projected screen angle.</summary>
        private static Mesh BuildOneGlyphWorldMeshCurved(in SymbolQuad quad, float textSizePx, float3 colorRgb,
            in float3 anchorLocal, in float3 tangentLocal)
        {
            BillboardMath.BuildWorldQuad(in quad, in anchorLocal, textSizePx, in colorRgb, 0f, in float2.zero,
                in tangentLocal, float3.zero, alignFlags: 2f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr,
                out WorldBillboardVertex br, out WorldBillboardVertex bl);

            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            vertices[0] = tl; vertices[1] = tr; vertices[2] = br; vertices[3] = bl;

            var opacity = new NativeArray<float>(4, Allocator.Temp);
            opacity[0] = opacity[1] = opacity[2] = opacity[3] = 1f;

            var indices = new NativeArray<int>(6, Allocator.Temp);
            indices[0] = 0; indices[1] = 1; indices[2] = 2;
            indices[3] = 0; indices[4] = 2; indices[5] = 3;

            var mesh = new Mesh { name = "WorldCurvedMotion_OneGlyph" };
            try
            {
                WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh);
            }
            finally
            {
                vertices.Dispose();
                opacity.Dispose();
                indices.Dispose();
            }
            return mesh;
        }
    }
}

namespace MapRenderer.Tests.Visual
{
    // Unity-only: render test requiring a GPU context (SnapshotRenderer).
    // NOT included in Tools/core-tests/core-tests.csproj.
    //
    // Non-local invariant: text-halo-color reaches the screen converted sRGB→linear exactly ONCE, on one of two
    // carriers. A CONSTANT rides the Color-typed `_HaloColor` uniform, which Unity converts on upload, so
    // BindColorTint must NOT pre-convert. Every other kind bakes into the vertex COLOR stream, which Unity does
    // not convert, so LinearHaloColor MUST. The fragment multiplies the two, so one is always WHITE; one arm per
    // carrier. SDF overrides drive coverage to a flat 1, so the centre pixel is the pure halo colour.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolHaloColorRenderTests — Unity-only: render test requiring a GPU context (SnapshotRenderer).
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolHaloColorRenderTests
    {
        private const int SnapSize = 128;

        // 0.5 grey — the discriminating value: ~17.6% of correct if the sRGB->linear conversion is applied
        // twice, ~2.2x if it is skipped entirely.
        private const string AuthoredHaloHex = "#808080";

        // The zoom the layer's constant halo is evaluated at. Any value: the fixture's halo is a constant.
        private const double Zoom = 8.0;

        private static float4 HaloSrgbFloat4(MapRenderer.Core.Expressions.Color c)
            => new float4((float)c.R, (float)c.G, (float)c.B, (float)c.A);

        /// <summary>What <c>SymbolFeatureExtractor.StreamRgba</c> bakes for a CONSTANT text-halo-color: the
        /// colour rides <c>_HaloColor</c>, so the vertex stream is white at this fixture's opaque alpha.</summary>
        private static readonly float4 StreamWhite = new float4(1f, 1f, 1f, 1f);

        // A ZOOM-kind text-halo-color that evaluates to AuthoredHaloHex at every zoom — the value is held
        // constant so the arms differ in CARRIER alone, never in the colour being measured.
        private const string ZoomHaloColorJson =
            @"[""interpolate"",[""linear""],[""zoom""],0,""" + AuthoredHaloHex + @""",22,""" + AuthoredHaloHex + @"""]";

        /// <param name="haloColorJson">The raw <c>text-halo-color</c> JSON value — a quoted hex for the
        /// CONSTANT arm, an expression for the vertex-stream arm.</param>
        private static Symbol.StyleLayer BuildSymbolLayer(string haloColorJson)
        {
            string styleJson = @"{
                ""version"": 8,
                ""layers"": [
                    { ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
                      ""layout"": { ""text-field"": ""{NAME}"" },
                      ""paint"": { ""text-halo-color"": " + haloColorJson + @", ""text-halo-width"": 20, ""text-halo-blur"": 0 } }
                ]
            }";
            StyleDocument style = StyleParser.Parse(styleJson);
            return (Symbol.StyleLayer)style.Layers[0];
        }

        /// <summary>A degenerate screen-space billboard carrying one halo run's vertex payload: every corner
        /// shares one <c>AnchorLocal</c> (world position is irrelevant — the corner offset is added in clip
        /// space); only <see cref="WorldBillboardVertex.Offset"/> (logical px) gives it screen size. UV is
        /// constant so the fragment's sampled texel plays no part (see this file's header).</summary>
        private static Mesh BuildHaloQuad(float halfSizePx, in float4 haloColorLinear, float haloWidthPx)
        {
            var corners = new[]
            {
                new float2(-halfSizePx,  halfSizePx),
                new float2( halfSizePx,  halfSizePx),
                new float2( halfSizePx, -halfSizePx),
                new float2(-halfSizePx, -halfSizePx),
            };

            var vertices = new NativeArray<WorldBillboardVertex>(4, Allocator.Temp);
            for (int i = 0; i < 4; i++)
                vertices[i] = new WorldBillboardVertex
                {
                    AnchorLocal = float3.zero,
                    ColorRGB    = haloColorLinear.xyz,    // the value under test
                    Uv          = float2.zero,
                    Page        = 0f,
                    Offset      = corners[i],
                    AlignFlags  = 0f,
                    Tangent     = float3.zero,
                    Up          = float3.zero,
                    SdfWidenPx  = new float2(haloWidthPx, 0f),
                };
            var opacity = new NativeArray<float>(4, Allocator.Temp);
            for (int i = 0; i < 4; i++) opacity[i] = haloColorLinear.w;
            var indices = new NativeArray<int>(new[] { 0, 1, 2, 0, 2, 3 }, Allocator.Temp);

            var mesh = new Mesh { name = "SymbolHaloColorRender_Quad" };
            try { WorldBillboardMeshBuilder.Build(vertices, opacity, indices, mesh); }
            finally { vertices.Dispose(); opacity.Dispose(); indices.Dispose(); }
            return mesh;
        }

        /// <summary>
        /// Renders one halo run: the production material (<see cref="SymbolRenderLayer.Create"/>) over a quad
        /// whose vertex COLOR is <paramref name="streamSrgb"/> through <c>LinearHaloColor</c>; the caller names
        /// the carrier explicitly. Optionally restyles to <paramref name="restyleToHex"/> first. Returns the
        /// centre sample in linear RGB.
        /// </summary>
        private static double3 RenderHalo(string haloColorJson, in float4 streamSrgb,
            string restyleToHex = null, double duration = 0.0)
        {
            Symbol.StyleLayer layer    = BuildSymbolLayer(haloColorJson);
            MapMaterialSet    settings = MapMaterialSetTestUtil.Load();
            SymbolRenderLayer renderLayer = SymbolRenderLayer.Create(layer, settings, initialZoom: Zoom, drawIndex: 0);
            Assert.IsNotNull(renderLayer.WorldTextMaterial, "MapMaterialSet.SymbolTextWorld must be assigned.");
            Material mat = renderLayer.WorldTextMaterial;

            // Flat coverage 1: _SdfRangeTexels(0) floors screenPxRange to 1.0, so screenDist is -_SdfEdge, and
            // the 20 px halo widening swamps _SdfEdge(3) whatever the atlas holds.
            mat.SetFloat(Shader.PropertyToID("_SdfRangeTexels"), 0f);
            mat.SetFloat(Shader.PropertyToID("_SdfEdge"), 3f);
            mat.SetVector(Shader.PropertyToID("_ScreenParamsLogical"), new Vector4(SnapSize, SnapSize, 0f, 0f));

            // The restyle seam, before the render so the sample is the settled colour. Restyle touches only
            // _TextColor and _HaloColor, so the SDF overrides survive.
            if (restyleToHex != null)
            {
                Symbol.StyleLayer newLayer = BuildSymbolLayer("\"" + restyleToHex + "\"");
                renderLayer.Restyle(newLayer, StyleTransition.Default, nowSeconds: 0.0);
                renderLayer.ApplyZoom(new StyleFrameInputs(8.0, 1.0, duration));
            }

            // An unbound Texture2DArray _MainTex renders nothing on this backend. Coverage ignores its content,
            // so a single-texel placeholder is enough.
            using var bag = new ObjectDisposalBag();
            var blankAtlas = bag.Track(new Texture2DArray(1, 1, 1, TextureFormat.R8, false));
            blankAtlas.SetPixels(new[] { UnityEngine.Color.black }, 0);
            blankAtlas.Apply();
            mat.SetTexture(Shader.PropertyToID("_MainTex"), blankAtlas);

            // The production chain: the stream colour through LinearHaloColor, where its one sRGB→linear
            // convert lives. At dpr 1 text-halo-width's logical px are device px.
            var paint = new SymbolPaint
            {
                HaloColor = streamSrgb,
                Opacity   = 1f,
            };
            Mesh mesh = bag.Track(BuildHaloQuad(halfSizePx: 40f,
                SymbolPlacementSystem.LinearHaloColor(paint),
                (float)layer.Paint.HaloWidth.Evaluate(Zoom)));

            var quadGo = bag.Track(new GameObject("SymbolHaloColorRender_Quad"));
            quadGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            quadGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var camGo  = bag.Track(new GameObject("SymbolHaloColorRender_Camera"));
            var camera = camGo.AddComponent<Camera>();
            camera.orthographic      = true;
            camera.orthographicSize  = 1f;
            camera.nearClipPlane     = 0.1f;
            camera.farClipPlane      = 100f;
            camera.clearFlags        = CameraClearFlags.SolidColor;
            camera.backgroundColor   = Color.black;
            camera.enabled           = false;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.transform.rotation = Quaternion.identity;

            using var snap = new SnapshotRenderer(SnapSize, SnapSize);
            try
            {
                snap.Render(camera);
                snap.WritePng($"symbol-halo-{(restyleToHex ?? haloColorJson).Trim('"', '#')}.png");
                return SampleCenterLinear(snap);
            }
            finally
            {
                // mat is renderLayer.WorldTextMaterial (not constructed here) — left as an explicit destroy,
                // not bag-tracked, since this method never owns/constructs it directly.
                Object.DestroyImmediate(mat);
            }
        }

        /// <summary>Mean linear RGB of the centre sample box. The render target is sRGB-encoded on
        /// readback, so the bytes are decoded through <see cref="Color.linear"/> before averaging — mirrors
        /// <c>PaintColorRenderTests.SampleLinear</c>.</summary>
        private static double3 SampleCenterLinear(SnapshotRenderer snap)
        {
            const int half = 6;
            double3 sum = double3.zero;
            int n = 0;
            for (int y = SnapSize / 2 - half; y <= SnapSize / 2 + half; y++)
            for (int x = SnapSize / 2 - half; x <= SnapSize / 2 + half; x++)
            {
                Color32 px = snap.Pixels[x, y];
                Color lin = new Color(px.r / 255f, px.g / 255f, px.b / 255f, 1f).linear;
                sum += new double3(lin.r, lin.g, lin.b);
                n++;
            }
            return sum / n;
        }

        /// <summary>
        /// The rendered albedo of a halo whose <c>text-halo-color</c> is a NON-WHITE constant (0.5 grey) must
        /// be the authored colour, converted sRGB->linear exactly ONCE — now by
        /// <c>SymbolPlacementSystem.LinearHaloColor</c>, because the vertex COLOR stream it rides is one Unity
        /// does not convert. See this file's header for the two carriers and where each converts.
        /// </summary>
        [Test]
        public void HaloColor_RenderedPixel_MatchesAuthored()
        {
            double3 measured = RenderHalo($"\"{AuthoredHaloHex}\"", StreamWhite);

            Assert.IsTrue(ColorUtility.TryParseHtmlString(AuthoredHaloHex, out Color authored));
            Color   expected = authored.linear;
            double3 expect3  = new double3(expected.r, expected.g, expected.b);

            Debug.Log($"[SymbolHaloColorRender] measured={measured} authored(linear)={expect3}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: text-halo-color must reach the fragment converted sRGB->linear ONCE. " +
                    $"measured={measured} authored(linear)={expect3}. Far BELOW authored means " +
                    $"BindColorTint pre-converted on top of Unity's own upload conversion of the " +
                    $"Color-typed _HaloColor; far ABOVE means the uniform never reached the " +
                    $"fragment and the white vertex stream is all that is left.");
        }

        /// <summary>
        /// The SECOND carrier. A non-Constant <c>text-halo-color</c> bakes into the vertex COLOR stream, and
        /// the uniform stays white — so the one sRGB->linear conversion is
        /// <c>SymbolPlacementSystem.LinearHaloColor</c>'s. Without this arm the fixture above would leave
        /// that conversion unobserved: its stream payload is white, on which any conversion is a no-op.
        /// </summary>
        [Test]
        public void StreamCarriedHaloColor_RenderedPixel_MatchesAuthored()
        {
            Symbol.StyleLayer layer = BuildSymbolLayer(ZoomHaloColorJson);
            Assert.That(SymbolTextColorCarrier.RidesUniform(layer.Paint.HaloColor), Is.False,
                "precondition: this arm's text-halo-color must ride the vertex stream, not the uniform — " +
                "otherwise it silently duplicates the constant arm.");

            double3 measured = RenderHalo(ZoomHaloColorJson,
                HaloSrgbFloat4(layer.Paint.HaloColor.Evaluate(Zoom)));

            Assert.IsTrue(ColorUtility.TryParseHtmlString(AuthoredHaloHex, out Color authored));
            Color   expected = authored.linear;
            double3 expect3  = new double3(expected.r, expected.g, expected.b);

            Debug.Log($"[SymbolHaloColorRender] stream measured={measured} authored(linear)={expect3}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: a stream-carried text-halo-color must reach the fragment converted " +
                    $"sRGB->linear ONCE. measured={measured} authored(linear)={expect3}. Far ABOVE " +
                    $"authored means LinearHaloColor's `.linear` was dropped, leaving sRGB bytes in a " +
                    $"stream Unity never converts; far BELOW means it was applied twice.");
        }

        // #808080 -> #4099C0: no shared channel, none at 0/1.
        private const string RestyledHaloHex = "#4099C0";

        /// <summary>
        /// A RESTYLED <c>text-halo-color</c> must reach the fragment as the NEW
        /// authored colour, converted sRGB->linear exactly once — <see cref="HaloColor_RenderedPixel_MatchesAuthored"/>
        /// only guards the initial <c>Create</c> write; this is the missing assertion over a restyle
        /// re-bind. RED-verify: <c>Restyle</c> omits the halo re-bind — the pixel stays <c>#808080</c> and
        /// the R assertion fires first (linear 0.2159 vs expected 0.0513).
        /// </summary>
        [Test]
        public void RestyledHaloColor_RenderedPixel_MatchesTheNewAuthored()
        {
            double3 measured = RenderHalo($"\"{AuthoredHaloHex}\"", StreamWhite,
                RestyledHaloHex, StyleTransition.Default.DurationSeconds);

            Assert.IsTrue(ColorUtility.TryParseHtmlString(RestyledHaloHex, out Color authored));
            Color   expected = authored.linear;
            double3 expect3  = new double3(expected.r, expected.g, expected.b);

            Debug.Log($"[SymbolHaloColorRender] restyled measured={measured} authored(linear)={expect3}");

            for (int c = 0; c < 3; c++)
                Assert.That(measured[c], Is.EqualTo(expect3[c]).Within(0.02),
                    $"channel {c}: a RESTYLED text-halo-color must reach the fragment converted sRGB->linear " +
                    $"ONCE, at the NEW authored value. measured={measured} authored(linear)={expect3}. " +
                    "Landing at the OLD authored value means Restyle never re-bound the halo.");
        }
    }
}
