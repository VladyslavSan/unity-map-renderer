// Unity EditMode only. It drives SymbolFeatureExtractor.Extract, which since tile-geometry IR B4
// materializes a Waist-1 TileGeometryBuffers and therefore depends on Unity.Collections — so this file left
// Tools/core-tests (no coverage lost, only iteration speed) and must not be re-added to core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S105 Slice 2 (A3): <see cref="SymbolFeatureExtractor.Extract"/> over the committed fixture's
    /// <c>centroids</c> layer (250 Point features with <c>NAME</c>/<c>ABBREV</c>) yields the right count,
    /// the right resolved text for the first feature (<c>"Aruba"</c>), an anchor that is the REAL
    /// tile→geo→project chain (not a stub), and honours the layer filter. Engine-free; both runners.
    /// </summary>
    [TestFixture]
    public class SymbolFeatureExtractorTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        // Walk-up fixture loader (works in Unity batch mode AND dotnet test) — mirrors MvtPropertyDecodeTests.
        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-tile.bytes not found walking up from cwd/AppContext.");
        }

        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static SymbolStyle.StyleLayer CentroidsLayer(string textField = "{NAME}", string filterJson = null)
            => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                LayoutJson = JsonParser.Parse("{\"text-field\":\"" + textField + "\"}"),
                Filter = filterJson != null ? JsonParser.Parse(filterJson) : null,
            };

        [Test]
        public void Extract_CentroidsLayer_Yields250LabelsWithRealAnchors()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(CentroidsLayer(), tile, FixtureTile, 0.0, projection, labels);

            // The fixture has 250 centroids features; one label per point feature with a NON-EMPTY NAME.
            // Two features have an absent/empty NAME → their "{NAME}" resolves empty → skipped (the A2 skip
            // rule proven on real data), so 248 labels. Compute the expectation independently, then pin it.
            MvtLayer centroids = tile.GetLayer("centroids");
            Assert.AreEqual(250, centroids.Features.Count, "fixture pin: 250 centroids features");
            int pointFeaturesWithName = 0;
            foreach (MvtFeature feat in centroids.Features)
            {
                if (feat.GeometryType != TileGeometryType.Point) continue;
                if (feat.Properties.TryGetValue("NAME", out MapRenderer.Core.Expressions.Value name)
                    && !string.IsNullOrWhiteSpace(name.ToDisplayString()))
                    pointFeaturesWithName++;
            }
            Assert.AreEqual(pointFeaturesWithName, labels.Count,
                "one label per point feature with a resolvable NAME (empty/absent NAME is skipped, not blank)");
            Assert.AreEqual(248, labels.Count, "fixture pin: 248 of 250 centroids resolve a non-empty NAME");

            // First feature resolves to "Aruba".
            Assert.AreEqual("Aruba", labels[0].Text, "feature[0]'s NAME is Aruba");

            // Its anchor is the REAL tile→geo→project chain, not a stubbed origin. Recompute independently
            // from the decoded first point and assert equality; also pin the fixture point (1252,1904).
            // IR C1 P3: arm A reads the command stream from the BYTES (a decoded feature carries none).
            List<List<double2>> paths = MvtGeometry.Decode(
                MvtFixtureStreams.ReadLayer(LoadFixture(), "centroids").Commands[0]);
            double2 firstPoint = paths[0][0];
            Assert.AreEqual(1252.0, firstPoint.x, 1e-6, "fixture pin: feature[0].point.x");
            Assert.AreEqual(1904.0, firstPoint.y, 1e-6, "fixture pin: feature[0].point.y");

            double2 lonLat = FixtureTile.ToLonLat(firstPoint.x, firstPoint.y, centroids.Extent);
            double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            Assert.AreEqual(expected.x, labels[0].AnchorRender.x, 1e-6, "anchor.x must be the real projection (not a stub)");
            Assert.AreEqual(expected.y, labels[0].AnchorRender.y, 1e-6);
            Assert.AreEqual(expected.z, labels[0].AnchorRender.z, 1e-6);

            // Stable per-tile ordinal + spec padding default carried through.
            Assert.AreEqual(0, labels[0].FeatureIndex);
            Assert.AreEqual(2f, labels[0].PaddingPx, 1e-6, "text-padding spec default is 2");
        }

        // ── Layer visibility predicate (MapLibre minzoom<=zoom<maxzoom, min inclusive / max EXCLUSIVE, null =
        //    unbounded). This is evaluated at DISPLAY time against the live camera zoom (see the extraction test
        //    below for WHY it is not gated at build time). ──
        [Test]
        public void StyleLayer_IsVisibleAtZoom_MinInclusive_MaxExclusive_NullUnbounded()
        {
            MapRenderer.Core.Style.StyleLayer L(double? min, double? max) =>
                new SymbolStyle.StyleLayer { Id = "x", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, MinZoom = min, MaxZoom = max };

            Assert.IsFalse(L(15.0, null).IsVisibleAtZoom(14.0), "below minzoom → hidden");
            Assert.IsTrue (L(15.0, null).IsVisibleAtZoom(15.0), "at minzoom → visible (inclusive)");
            Assert.IsTrue (L(15.0, null).IsVisibleAtZoom(16.0), "above minzoom → visible");
            Assert.IsFalse(L(null, 8.0).IsVisibleAtZoom(8.0),  "at maxzoom → hidden (exclusive)");
            Assert.IsTrue (L(null, 8.0).IsVisibleAtZoom(7.99), "just below maxzoom → visible");
            Assert.IsTrue (L(null, null).IsVisibleAtZoom(14.0), "unbounded → visible at any zoom");
            Assert.IsTrue (L(15.0, 17.0).IsVisibleAtZoom(16.0), "within [min,max) → visible");
            Assert.IsFalse(L(15.0, 17.0).IsVisibleAtZoom(17.0), "at max of a bounded range → hidden");
        }

        // ── Extraction is deliberately zoom-VISIBILITY-agnostic: it emits a layer's labels regardless of the
        //    layer's minzoom/maxzoom, because tile DATA tops out at a max source zoom (z14 for OpenFreeMap) and is
        //    OVERZOOMED at higher camera zooms without rebuilding. Gating at build time would freeze visibility and
        //    hide layers MapLibre reveals as you zoom past the data level; the gate lives at display time instead
        //    (StyleLayer.IsVisibleAtZoom). This pins that a minzoom-15 layer STILL extracts at z14 (a build-time
        //    gate here would be the regression). ──
        [Test]
        public void Extract_DoesNotGateByLayerZoom_SoOverzoomWorks()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "labels", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "centroids",
                LayoutJson = JsonParser.Parse("{\"text-field\":\"{NAME}\"}"), MinZoom = 15.0, // MapLibre-hidden at z14
            };
            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(layer, tile, FixtureTile, 14.0, projection, labels);
            Assert.AreEqual(248, labels.Count,
                "a minzoom-15 layer must still EXTRACT at z14 (its labels live in the store); display-time " +
                "IsVisibleAtZoom hides them until the camera reaches z15 — so overzoomed data reveals them correctly");
        }

        [Test]
        public void Extract_TextTransform_CaseFoldsResolvedLabel()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            SymbolStyle.StyleLayer Layer(string transform) => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                LayoutJson = JsonParser.Parse(
                    "{\"text-field\":\"{NAME}\",\"text-transform\":\"" + transform + "\"}"),
            };

            var upper = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(Layer("uppercase"), tile, FixtureTile, 0.0, projection, upper);
            Assert.AreEqual("ARUBA", upper[0].Text, "text-transform:uppercase must uppercase the resolved label");

            var lower = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(Layer("lowercase"), tile, FixtureTile, 0.0, projection, lower);
            Assert.AreEqual("aruba", lower[0].Text, "text-transform:lowercase must lowercase the resolved label");

            // Teeth: default (no transform) leaves the mixed-case source untouched — so the two above are
            // genuine transforms, not a fixture that happens to be already-cased.
            var none = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(CentroidsLayer(), tile, FixtureTile, 0.0, projection, none);
            Assert.AreEqual("Aruba", none[0].Text, "no text-transform leaves the source casing as-is");
        }

        [Test]
        public void Extract_LinePlacement_ProducesLineLabelsWithProjectedPath()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            SymbolStyle.StyleLayer LineLayer(string placement) => new SymbolStyle.StyleLayer
            {
                Id = "lines",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "geolines",
                // Literal text-field so every line feature resolves a label regardless of its properties.
                LayoutJson = JsonParser.Parse("{\"text-field\":\"L\",\"symbol-placement\":\"" + placement + "\"}"),
            };

            var lineLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(LineLayer("line-center"), tile, FixtureTile, 0.0, projection, lineLabels);

            Assert.Greater(lineLabels.Count, 0, "the geolines LineString layer yields line labels");
            SymbolStyle.SymbolLabel first = lineLabels[0];
            Assert.AreEqual(MapRenderer.Core.Text.SymbolPlacement.LineCenter, first.Placement);
            Assert.AreEqual(250f, first.SpacingPx, 1e-6, "symbol-spacing default (250) carried onto the line label");
            Assert.IsNotNull(first.PathRender, "a line label carries the projected path");
            Assert.GreaterOrEqual(first.PathRender.Length, 2, "a placeable line has >= 2 vertices");
            // A-2: the extractor computes the zoom-invariant along-line anchors (line-center → exactly one).
            Assert.IsNotNull(first.LineAnchors, "a line label carries its build-time anchors");
            Assert.AreEqual(1, first.LineAnchors.Length, "line-center places a single centred anchor");
            Assert.AreEqual("L", first.Text);

            // The path IS the real tile->geo->project chain (recompute the first line's first vertex).
            MvtLayer geolines = tile.GetLayer("geolines");
            List<List<double2>> paths = MvtGeometry.Decode(
                MvtFixtureStreams.ReadLayer(LoadFixture(), "geolines").Commands[0]);
            double2 lonLat = FixtureTile.ToLonLat(paths[0][0].x, paths[0][0].y, geolines.Extent);
            double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            Assert.AreEqual(expected.x, first.PathRender[0].x, 1e-6, "path vertex is the real projection, not a stub");

            // D2 (road-shields): a POINT-placement layer over the SAME line layer now anchors each path at its
            // mid arc-length (one label per path) instead of skipping it outright — the G2 fix. (Superseded the
            // pre-shields "point placement skips LineString features" assertion, which was the very bug D2 fixes.)
            var pointOverLines = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(LineLayer("point"), tile, FixtureTile, 0.0, projection, pointOverLines);
            Assert.Greater(pointOverLines.Count, 0, "D2: point placement now anchors a LineString path at its mid arc-length");
            foreach (SymbolStyle.SymbolLabel l in pointOverLines)
            {
                Assert.AreEqual(MapRenderer.Core.Text.SymbolPlacement.Point, l.Placement, "a mid-arc anchor is Point-placed");
                Assert.IsNull(l.PathRender, "a point-placed (mid-arc) label carries no curved path");
            }
        }

        // ── S4-T5: Mercator extractor length-identity — the engine-free half of the byte-identity invariant ──

        [Test]
        public void Extract_LinePlacement_Mercator_PathRenderLengthMatchesOriginalVertexCount()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection(); // MaxRefineAngleRad == +infinity

            var lineLayer = new SymbolStyle.StyleLayer
            {
                Id = "lines",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "geolines",
                LayoutJson = JsonParser.Parse("{\"text-field\":\"L\",\"symbol-placement\":\"line-center\"}"),
            };

            var lineLabels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(lineLayer, tile, FixtureTile, 0.0, projection, lineLabels);
            Assert.Greater(lineLabels.Count, 0, "the geolines LineString layer yields line labels");

            List<List<double2>> paths = MvtGeometry.Decode(
                MvtFixtureStreams.ReadLayer(LoadFixture(), "geolines").Commands[0]);
            int originalVertexCount = paths[0].Count;

            Assert.AreEqual(originalVertexCount, lineLabels[0].PathRender.Length,
                "S4: on a flat projection (MaxRefineAngleRad == +infinity) LineCurvatureSubdivision.Subdivide "
                + "must never fire — PathRender length stays the original decoded vertex count");
        }

        // ── S4-T6: globe subdivision + anchor alignment (the crux) ─────────────────────────────────────

        /// <summary>Minimal engine-free <see cref="IFeature"/>/<see cref="ITileLayer"/>/<see cref="IDecodedTile"/>
        /// test doubles for a synthetic tile — mirrors <c>A7TileFeatureSourceTests.FixtureDecodedTile</c>/
        /// <c>FixtureTileLayer</c> (test-only, duplicated locally per convention rather than shared, since both
        /// are private test fixtures, not a production type).</summary>
        /// <summary>Protobuf zigzag ENcode — the inverse of <see cref="MvtGeometry.ZigZag"/> — for hand-building
        /// a synthetic MVT command stream.</summary>
        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        /// <summary>Hand-encodes a single 2-point LineString <c>(0,0) → (extent,extent)</c> as an MVT geometry
        /// command stream: MoveTo(1 point) + LineTo(1 point), zigzag-encoded deltas (mirrors <see cref="MvtGeometry.Decode"/>).</summary>
        private static uint[] DiagonalLineGeometry(uint extent)
            => new uint[]
            {
                (1u) | (1u << 3), 0, 0,                                       // MoveTo (0,0)
                (2u) | (1u << 3), ZigZagEncode(extent), ZigZagEncode(extent), // LineTo (extent,extent)
            };

        [Test]
        public void Extract_GlobeProjection_SubdividesLongLineSegment_AndAnchorStaysAligned()
        {
            const uint extent = 4096;
            var feature = new InMemoryTileFeature
            {
                GeometryType = MapRenderer.Core.Tiles.TileGeometryType.LineString,
                Geometry = DiagonalLineGeometry(extent),
            };
            // A low zoom so the two endpoints' surface normals subtend a large arc — many splits.
            var tileId = new TileId { Z = 1, X = 0, Y = 0 };
            var tile = TestDecodedTiles.Of("lines", tileId, new List<IFeature> { feature }, extent);
            var projection = new SphericalProjection(); // MaxRefineAngleRad ~2 degrees (finite)

            var lineLayer = new SymbolStyle.StyleLayer
            {
                Id = "lines",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "lines",
                LayoutJson = JsonParser.Parse("{\"text-field\":\"L\",\"symbol-placement\":\"line-center\"}"),
            };

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(lineLayer, tile, tileId, 0.0, projection, labels);

            Assert.AreEqual(1, labels.Count, "one line label for the single synthetic feature");
            SymbolStyle.SymbolLabel label = labels[0];

            // (a) tooth #1 — subdivision fired, off the chord.
            Assert.Greater(label.PathRender.Length, 2, "the globe must subdivide the 2-vertex line");
            double3 chordStart = label.PathRender[0];
            double3 chordEnd = label.PathRender[label.PathRender.Length - 1];
            double3 chordMid = (chordStart + chordEnd) * 0.5;
            double3 midVertex = label.PathRender[label.PathRender.Length / 2];
            double distFromChord = math.length(midVertex - chordMid);
            Assert.Greater(distFromChord, 1.0,
                "an inserted mid vertex must sit OFF the straight render chord (curvature, not a facet)");

            // (b) tooth #2 — count/position invariant, index refined.
            Assert.IsNotNull(label.LineAnchors);
            Assert.AreEqual(1, label.LineAnchors.Length, "line-center still places a single anchor (arc-length invariant)");
            Assert.Greater(label.LineAnchors[0].Segment, 0,
                "the anchor's segment index must be refined onto the finer path (0 would mean it never resubdivided)");

            // (c) tooth #3 — resolves to the correct arc position on the RENDER curve. Independently project the
            // geographic midpoint of the (still 2-vertex) tile-local line — the arc-length midpoint of a straight
            // 2-point segment is its linear midpoint — and compare against what the anchor resolves to.
            double2 midTile = new double2(extent * 0.5, extent * 0.5);
            double2 midLonLat = tileId.ToLonLat(midTile.x, midTile.y, extent);
            double3 expectedRenderMid = projection.Project(
                new GeoCoordinate { Latitude = midLonLat.y, Longitude = midLonLat.x });

            LineAnchor anchor = label.LineAnchors[0];
            double3 segStart = label.PathRender[anchor.Segment];
            double3 segEnd = label.PathRender[anchor.Segment + 1];
            double3 resolved = segStart + (segEnd - segStart) * (double)anchor.T; // math.lerp has no double3 overload in the shim
            double resolveError = math.length(resolved - expectedRenderMid);
            Assert.Less(resolveError, 1.0,
                "the anchor must resolve to (approximately) the true midpoint on the finer render curve");
        }

        // ── Stage B: single-world clip — point anchors outside [0, extent) are source world-copies ──────

        /// <summary>Hand-encodes a MultiPoint MVT geometry command stream: a single MoveTo(count=N) followed
        /// by N zigzag-encoded cumulative deltas from a cursor starting at (0,0) — mirrors
        /// <see cref="MvtGeometry.Decode"/>'s MoveTo-repeat semantics, where each repeat starts its own
        /// 1-point path (i.e. a MultiPoint feature decodes to N separate 1-point paths).</summary>
        private static uint[] MultiPointGeometry(params double2[] tilePoints)
        {
            var stream = new List<uint> { 1u | ((uint)tilePoints.Length << 3) }; // MoveTo, count=N
            long cursorX = 0, cursorY = 0;
            foreach (double2 p in tilePoints)
            {
                long x = (long)p.x, y = (long)p.y;
                stream.Add(ZigZagEncode(x - cursorX));
                stream.Add(ZigZagEncode(y - cursorY));
                cursorX = x;
                cursorY = y;
            }
            return stream.ToArray();
        }

        [Test]
        public void Extract_PointPlacement_ClipsOutOfBoundsAnchorsToTile()
        {
            const uint extent = 4096;
            // In-bounds (kept): an interior point, another interior point, the min edge (inclusive), and the
            // max edge (extent - 1, inclusive). Out-of-bounds (dropped): the plan's own examples
            // (extent*1.5, -extent*0.5, extent+1) plus the half-open upper-bound edges x==extent/y==extent
            // (a shared-edge anchor belongs to the NEXT tile, not this one).
            double2 inA = new double2(100, 200);
            double2 outXHigh = new double2(extent * 1.5, 200);
            double2 inB = new double2(300, 300);
            double2 outXNeg = new double2(-(double)extent * 0.5, 200);
            double2 outYHigh = new double2(250, extent + 1);
            double2 inMinEdge = new double2(0, 0);
            double2 inMaxEdge = new double2(extent - 1, extent - 1);
            double2 outXEdge = new double2(extent, 50);
            double2 outYEdge = new double2(50, extent);

            var feature = new InMemoryTileFeature
            {
                GeometryType = MapRenderer.Core.Tiles.TileGeometryType.Point,
                Geometry = MultiPointGeometry(
                    inA, outXHigh, inB, outXNeg, outYHigh, inMinEdge, inMaxEdge, outXEdge, outYEdge),
            };
            var tileId = new TileId { Z = 1, X = 0, Y = 0 };
            var tile = TestDecodedTiles.Of("points", tileId, new List<IFeature> { feature }, extent);
            var projection = new WebMercatorProjection();

            var pointLayer = new SymbolStyle.StyleLayer
            {
                Id = "points",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "points",
                // Literal text-field (no {} interpolation) so resolution doesn't depend on feature properties
                // — InMemoryTileFeature.TryGetProperty always returns false (mirrors the LineLayer literal-
                // text pattern used elsewhere in this file).
                LayoutJson = JsonParser.Parse("{\"text-field\":\"L\"}"),
            };

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(pointLayer, tile, tileId, 0.0, projection, labels);

            Assert.AreEqual(4, labels.Count,
                "only the 4 in-bounds anchors are emitted; the 5 out-of-bounds/edge points are clipped");

            double2[] expectedTilePoints = { inA, inB, inMinEdge, inMaxEdge };
            for (int i = 0; i < expectedTilePoints.Length; i++)
            {
                double2 lonLat = tileId.ToLonLat(expectedTilePoints[i].x, expectedTilePoints[i].y, extent);
                double3 expectedAnchor = projection.Project(
                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                Assert.AreEqual(expectedAnchor.x, labels[i].AnchorRender.x, 1e-6, $"label[{i}].anchor.x");
                Assert.AreEqual(expectedAnchor.y, labels[i].AnchorRender.y, 1e-6, $"label[{i}].anchor.y");
                Assert.AreEqual(expectedAnchor.z, labels[i].AnchorRender.z, 1e-6, $"label[{i}].anchor.z");
                Assert.AreEqual(i, labels[i].FeatureIndex,
                    "ordinal stays contiguous across skipped out-of-bounds points (no gaps from the clip)");
            }
        }

        [Test]
        public void Extract_WithFilter_NarrowsToNamedFeature()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            var labels = new List<SymbolStyle.SymbolLabel>();
            SymbolFeatureExtractor.Extract(
                CentroidsLayer(filterJson: "[\"==\",[\"get\",\"ABBREV\"],\"Afg.\"]"),
                tile, FixtureTile, 0.0, projection, labels);

            Assert.AreEqual(1, labels.Count, "the ABBREV=='Afg.' filter selects exactly one feature");
            Assert.AreEqual("Afghanistan", labels[0].Text);
        }
    }
}
