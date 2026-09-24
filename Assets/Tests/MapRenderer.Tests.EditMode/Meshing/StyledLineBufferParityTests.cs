// Meshing/StyledLineBufferParityTests.cs — styled-line buffer parity, the fill-layer-kind gate, wall-quad job parity, water triangulation, and a line GC-allocation regression pin.
//
// No single production area dominates this file; kept in the order the topic-and-lane pack assembled them, each fixture independent of its neighbours.
//
// Contents:
//   StyledLineBufferParityTests  — the teeth on moving StyledLineTileBuilder off its own managed MvtGeometry.Decode and onto the shared TileGeometryBuffers.
//   FillLayerKindGateTests       — the observer for the precondition the RingAssemblyJob kind-gate deferral rests on.
//   WallQuadJobParityTests       — Two-part comparison, the same shape as StyledFillExtrusionGraphWriteTests and grounded in the same measurement (docs/job-scheduling-design.md).
//   WaterTriangulationTests      — Mesh-triangulation testbench over real OpenFreeMap water tiles (many-holed polygons — the case that breaks a hand-rolled ear-clipper).
//   LineBuildAllocTests          — with the three per-layer attribution columns (featColors/featWidths/featSelected) native rather than managed T[], a CONSTANT style (paint.Width/Opacity.DependsOnFeature both false) makes the line build allocation-free.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Tests.Jobs;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Core.Expressions;
using System.Globalization;
using MapRenderer.Core.Json;
using MapRenderer.Jobs.Fill;
using MapRenderer.Unity.Rendering.Meshing;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;


namespace MapRenderer.Tests.Meshing
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // StyledLineBufferParityTests — moving StyledLineTileBuilder onto the shared TileGeometryBuffers
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>StyledLineTileBuilder</c> reads the shared <see cref="TileGeometryBuffers"/>, checked against the
    /// managed <c>MvtGeometry.Decode</c>. Non-obvious why: two different decoders and ring shapes cannot argue
    /// byte-identity, so both run in the same test and compare element-wise, which names the ring and point
    /// where they disagree; a snapshot would only say that a number moved.
    /// </summary>
    [TestFixture]
    public class StyledLineBufferParityTests
    {
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double FixtureExtent = 4096.0;
        private const double TestZoom      = 0.0;

        // A degenerate-area ring under RingAssemblyJob's own threshold — mirrored here so
        // StraightZeroAreaPolyline_StillRenders can state that its fixture really is the case fill would drop.
        private const double FillDegenerateThreshold = 1.0;

        // ── the differential oracle ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The ring list the line consumer reads from the shared buffer is element-wise identical to the
        /// managed decoder's; nothing downstream depends on its source, so ring-level equality is mesh-level
        /// equality. The comparison is exact: both decoders accumulate <c>long</c> deltas and emit integer
        /// magnitudes far inside <c>double</c>'s exact range.
        /// </summary>
        [Test]
        public void LineRings_FromTheSharedBuffer_MatchTheManagedDecodeOracle()
        {
            ITileLayer geolines = GeolinesLayer();
            IReadOnlyList<IFeature> features = geolines.Features;
            // Arm A's input comes from the BYTES, through the independent fixture reader — not from the
            /// decoded features, which carry no command stream (and reading production's own
            // buffer would make the differential compare production with itself).
            MvtFixtureStreams.Layer fixture = MvtFixtureStreams.ReadLayer(LoadFixture(), "geolines");
            Assert.AreEqual(fixture.Kinds.Count, features.Count,
                "precondition: arm A and arm B must index the same feature ordinals");

            // ── Arm A — the OLD implementation, executing now.
            var oracle = new List<(int featureIdx, double2[] points)>();
            for (int fi = 0; fi < features.Count; fi++)
            {
                IFeature feature = features[fi];
                if (feature.GeometryType != TileGeometryType.LineString) continue;

                List<List<double2>> paths = MvtGeometry.Decode(fixture.Commands[fi]);
                if (paths == null) continue;
                foreach (List<double2> path in paths)
                {
                    if (path == null || path.Count < 2) continue;
                    oracle.Add((fi, path.ToArray()));
                }
            }

            // ── Arm B — the new path, with EXACTLY the production gates.
            var actual = new List<(int featureIdx, double2[] points)>();
            // Arm B reads the DECODED LAYER's own buffer — the very object production consumes —
            // rather than re-materializing. Borrowed: the tile owns it and the TearDown frees it.
            TileGeometryBuffers geometry = geolines.Geometry;
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the layer carries a materialized buffer");

                for (int r = 0; r < geometry.RingCount; r++)
                {
                    int featIdx = geometry.RingFeatureIdx[r];
                    if (geometry.FeatureGeometryType[featIdx] != TileGeometryType.LineString) continue;

                    int start = geometry.RingOffsets[r];
                    int n     = geometry.RingOffsets[r + 1] - start;
                    if (n < 2) continue;

                    var points = new double2[n];
                    for (int k = 0; k < n; k++) points[k] = geometry.Vertices[start + k];
                    actual.Add((featIdx, points));
                }
            }

            // ── Non-vacuity, all four required: a fixture that could not tell the arms apart would make
            //    every equality below true for the wrong reason.
            Assert.GreaterOrEqual(oracle.Count, 6,
                "precondition: the fixture must contribute at least 6 rings to compare");
            int totalPoints = 0;
            var ringsPerFeature = new Dictionary<int, int>();
            foreach ((int featureIdx, double2[] points) entry in oracle)
            {
                totalPoints += entry.points.Length;
                ringsPerFeature.TryGetValue(entry.featureIdx, out int c);
                ringsPerFeature[entry.featureIdx] = c + 1;
            }
            Assert.Greater(totalPoints, 50,
                "precondition: the fixture must carry real geometry, not a handful of points");
            bool anyMultiRingFeature = false;
            foreach (KeyValuePair<int, int> kv in ringsPerFeature)
                if (kv.Value > 1) anyMultiRingFeature = true;
            Assert.IsTrue(anyMultiRingFeature,
                "precondition: at least one feature must contribute MORE THAN ONE ring, or ring ordering " +
                "WITHIN a feature is not pinned — only ordering across features would be");
            bool anyNonZeroFeatureIdx = false;
            foreach ((int featureIdx, double2[] points) entry in oracle)
                if (entry.featureIdx != 0) anyNonZeroFeatureIdx = true;
            Assert.IsTrue(anyNonZeroFeatureIdx,
                "precondition: the ring→feature join must be exercised by a non-zero index");

            // ── The comparison.
            Assert.AreEqual(oracle.Count, actual.Count,
                "the shared buffer must yield exactly the same number of line rings as the managed decoder");

            for (int i = 0; i < oracle.Count; i++)
            {
                Assert.AreEqual(oracle[i].featureIdx, actual[i].featureIdx,
                    $"ring {i} must belong to the same feature in both arms — feature-then-ring order is the " +
                    "contract RingFeatureIdx joins through");
                Assert.AreEqual(oracle[i].points.Length, actual[i].points.Length,
                    $"ring {i} must have the same point count in both arms");

                double2[] expected = oracle[i].points;
                double2[] got      = actual[i].points;
                for (int k = 0; k < expected.Length; k++)
                {
                    Assert.AreEqual(expected[k].x, got[k].x,
                        $"ring {i} point {k} x must be BIT-identical between the managed and Burst decoders");
                    Assert.AreEqual(expected[k].y, got[k].y,
                        $"ring {i} point {k} y must be BIT-identical between the managed and Burst decoders");
                }
            }
        }

        // ── the coexistence gate (a gate that cannot mis-classify is inert) ────────────────────────

        /// <summary>
        /// With polygon rings and line rings genuinely coexisting in one buffer, the line layer ribbons
        /// <b>only</b> the LineString. Ring kind is not recoverable from the coordinates, so this is the tooth
        /// that would catch a consumer classifying by area instead of by the declared kind.
        /// </summary>
        [Test]
        public void LineLayer_WithPolygonAndLineFeaturesSelected_RibbonsOnlyTheLines()
        {
            List<double2> polygonRing   = MvtCommandStream.Ring(1000, 1000, 2000, 1000, 2000, 2000, 1000, 2000);
            List<double2> lineRing      = MvtCommandStream.Ring(2600, 1200, 2800, 1600, 2900, 2400);
            List<double2> exteriorRing  = MvtCommandStream.Ring(1200, 2600, 2200, 2600, 2200, 2900, 1200, 2900);
            List<double2> holeRing      = MvtCommandStream.Ring(1400, 2700, 1400, 2800, 1900, 2800, 1900, 2700);

            var polygonA = Feature(TileGeometryType.Polygon,    polygonRing);
            var line     = Feature(TileGeometryType.LineString, lineRing);
            var polygonB = Feature(TileGeometryType.Polygon,    exteriorRing, holeRing);

            var mixed     = new List<IFeature> { polygonA, line, polygonB };
            var lineOnly  = new List<IFeature> { line };
            var polysOnly = new List<IFeature> { polygonA, polygonB };

            // ── Non-vacuity: the fixture really does mix kinds in ONE buffer, and the polygon rings are
            //    rings fill would genuinely classify — so "they were dropped as degenerate anyway" is out.
            TileGeometryBuffers geometry = Materialize(mixed);
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the mixed selection materialized");
                Assert.AreEqual(4, geometry.RingCount,
                    "precondition: 1 polygon ring + 1 line ring + (exterior + hole) = 4 rings in ONE buffer");

                bool sawPolygon = false, sawLineString = false;
                for (int f = 0; f < geometry.FeatureCount; f++)
                {
                    if (geometry.FeatureGeometryType[f] == TileGeometryType.Polygon)    sawPolygon = true;
                    if (geometry.FeatureGeometryType[f] == TileGeometryType.LineString) sawLineString = true;
                }
                Assert.IsTrue(sawPolygon && sawLineString,
                    "precondition: the kind column must contain BOTH kinds — a constant column would make " +
                    "the gate inert and this whole test vacuous");

                for (int r = 0; r < geometry.RingCount; r++)
                {
                    if (geometry.FeatureGeometryType[geometry.RingFeatureIdx[r]] != TileGeometryType.Polygon)
                        continue;
                    int start = geometry.RingOffsets[r];
                    int n     = geometry.RingOffsets[r + 1] - start;
                    Assert.Greater(math.abs(Shoelace2(geometry.Vertices, start, n)), FillDegenerateThreshold,
                        $"precondition: polygon ring {r} must have a non-degenerate area, so 'the polygons " +
                        "were dropped as degenerate anyway' cannot explain a pass");
                }
            }
            finally
            {
                geometry.Dispose();
            }

            int mixedVerts    = LineVertexCount(mixed);
            int lineOnlyVerts = LineVertexCount(lineOnly);
            int polysVerts    = LineVertexCount(polysOnly);

            Assert.Greater(lineOnlyVerts, 0,
                "(b) the LineString feature really does produce a ribbon on its own");
            Assert.AreEqual(lineOnlyVerts, mixedVerts,
                "(a) adding two polygon features to the selection must contribute EXACTLY nothing — the " +
                "kind gate, not an area test, is what keeps them out");
            Assert.AreEqual(0, polysVerts,
                "(c) a selection of polygons only must produce no line geometry at all");
        }

        // ── line's OWN length threshold — `< 2`, never fill's `< 3` ────────────────────────────────

        /// <summary>
        /// A two-point polyline is the boundary value of line's own filter and must still render. The
        /// seam tooth observes that the shared <i>buffer</i> stays unfiltered; this observes <b>the line
        /// consumer's own threshold</b>, which is the direction that is actually observable downstream:
        /// <c>RibbonJob.Execute</c> already early-returns on <c>PointCount &lt; 2</c>, so a short ring
        /// filtered upstream is behaviourally invisible either way. The mis-threshold direction is not.
        /// </summary>
        [Test]
        public void LineRing_TwoPointSegment_StillRenders()
        {
            var twoPoint = Feature(TileGeometryType.LineString, MvtCommandStream.Ring(1000, 1000, 3000, 2000));
            var onePoint = Feature(TileGeometryType.LineString, MvtCommandStream.Ring(1000, 1000));

            // Non-vacuity: the tooth is provably testing the boundary value, not a longer ring.
            TileGeometryBuffers geometry = Materialize(new List<IFeature> { twoPoint });
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the two-point feature materialized");
                Assert.AreEqual(1, geometry.RingCount, "precondition: exactly one ring");
                Assert.AreEqual(2, geometry.RingOffsets[1] - geometry.RingOffsets[0],
                    "precondition: the ring span is exactly 2 — the boundary value line's filter admits");
            }
            finally
            {
                geometry.Dispose();
            }

            Assert.Greater(LineVertexCount(new List<IFeature> { twoPoint }), 0,
                "a 2-point polyline must render — line's threshold is `< 2`, NOT fill's `< 3`; a shared " +
                "filter would starve one consumer or the other");

            // The control pins WHICH side of the boundary each length falls on.
            Assert.AreEqual(0, LineVertexCount(new List<IFeature> { onePoint }),
                "a 1-point ring has no segment and must produce nothing");
        }

        // ── ownership on the empty paths ───────────────────────────────────────────────────────────

        /// <summary>
        /// The paths where the layer produces nothing must produce nothing <i>and not throw</i>, and the
        /// zero must come from the consumer's kind gate rather than from an empty buffer.
        /// </summary>
        [Test]
        public void LineLayer_EmptyAndAllPolygonSelections_ProduceNoGeometryAndDoNotThrow()
        {
            // (1) Empty selection — the builder returns before materializing at all.
            int emptyVerts = 0;
            Assert.DoesNotThrow(() => emptyVerts = LineVertexCount(new List<IFeature>()));
            Assert.AreEqual(0, emptyVerts, "an empty selection produces no geometry");

            // (2) One Polygon feature — the buffer is NOT empty; the consumer's gate is what yields zero.
            var polygon = Feature(TileGeometryType.Polygon,
                MvtCommandStream.Ring(1000, 1000, 2000, 1000, 2000, 2000, 1000, 2000));
            var polygonOnly = new List<IFeature> { polygon };

            TileGeometryBuffers geometry = Materialize(polygonOnly);
            try
            {
                Assert.IsTrue(geometry.IsCreated,
                    "non-vacuity: the polygon selection really did materialize a buffer");
                Assert.Greater(geometry.RingCount, 0,
                    "non-vacuity: the buffer holds rings, so a zero vertex count below can ONLY come from " +
                    "the consumer's kind gate");
            }
            finally
            {
                geometry.Dispose();
            }

            int polygonVerts = 0;
            Assert.DoesNotThrow(() => polygonVerts = LineVertexCount(polygonOnly));
            Assert.AreEqual(0, polygonVerts, "an all-polygon selection produces no line geometry");

            // (3) A LineString feature whose Geometry is null — zero commands, so zero rings. The buffer is
            //     still minted (the feature list is non-empty), and disposing it is the builder's job.
            var nullGeometry = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: null);
            var nullOnly = new List<IFeature> { nullGeometry };

            TileGeometryBuffers nullBuffer = Materialize(nullOnly);
            try
            {
                Assert.IsTrue(nullBuffer.IsCreated,
                    "a non-empty feature list mints a buffer even when every stream is null — only a ZERO " +
                    "feature count materializes to default");
                Assert.AreEqual(0, nullBuffer.RingCount, "a null command stream contributes no rings");
            }
            finally
            {
                nullBuffer.Dispose();
            }

            int nullVerts = 0;
            Assert.DoesNotThrow(() => nullVerts = LineVertexCount(nullOnly));
            Assert.AreEqual(0, nullVerts, "a null command stream produces no geometry");
        }

        // ── the fused-RingAssemblyJob fence, behaviourally ────────────────────────────────────────

        /// <summary>
        /// A perfectly straight polyline has <b>exactly zero</b> signed area, i.e. strictly inside
        /// <c>RingAssemblyJob</c>'s degenerate band. It must still render. This is the falsifier an area
        /// filter cannot survive: it is the single most likely form of the "just reuse RingAssemblyJob's
        /// filter" shortcut.
        /// </summary>
        [Test]
        public void StraightZeroAreaPolyline_StillRenders()
        {
            List<double2> collinear = MvtCommandStream.Ring(1000, 1000, 2000, 1000, 3000, 1000);

            // Non-vacuity: the fixture really is the case fill's filter would drop.
            double area2 = 0.0;
            for (int i = 0; i < collinear.Count; i++)
            {
                double2 a = collinear[i];
                double2 b = collinear[(i + 1) % collinear.Count];
                area2 += a.x * b.y - b.x * a.y;
            }
            Assert.AreEqual(0.0, area2,
                "precondition: the fixture's shoelace must be EXACTLY zero — strictly below " +
                "RingAssemblyJob's degenerate threshold, so an area filter would drop it");

            var line = Feature(TileGeometryType.LineString, collinear);
            int vertexCount = LineVertexCount(new List<IFeature> { line });

            Assert.Greater(vertexCount, 0,
                "a straight polyline must render — line filters on a COUNT threshold, never an area one");

            // Pin that a RIBBON was built, not that some stray vertex appeared: a 3-point ribbon needs at
            // least two vertices per centerline point, and cannot exceed the job's own sized bound.
            Assert.GreaterOrEqual(vertexCount, 6,
                "a 3-point ribbon carries at least two vertices per centerline point");
            Assert.LessOrEqual(vertexCount, RibbonJob.MaxVertexCount(collinear.Count, 4),
                "…and no more than RibbonJob's own sized bound for a 3-point polyline");
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────────────

        private static ITileLayer GeolinesLayer()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            ITileLayer layer = tile.GetLayer("geolines");
            Assert.IsNotNull(layer, "geolines layer must be present in the fixture");
            return layer;
        }

        /// <summary>The decoded fixture tile owns Allocator.Persistent buffers.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException("sample-tile.bytes not found walking up from " + AppContext.BaseDirectory);
        }

        /// <summary>Materializes a selection through the SAME seam the line builder uses, so a fixture
        /// precondition and the production path cannot disagree about what the buffer holds.</summary>
        private static TileGeometryBuffers Materialize(IReadOnlyList<IFeature> features)
            => TestTileMeshBuilder.Materialize(features, FixtureTile, FixtureExtent);

        private static IFeature Feature(TileGeometryType kind, params IReadOnlyList<double2>[] rings)
            => new DictionaryFeature(properties: null, geometryType: kind, hasId: false, geometry: MvtCommandStream.Feature(rings));

        private static int LineVertexCount(IReadOnlyList<IFeature> features)
        {
            Line.StyleLayer styleLayer = LineStyleLayer();
            return TestTileMeshBuilder.LineVertexCount(
                features, styleLayer.Paint, styleLayer.Layout,
                TestZoom, FixtureExtent, FixtureTile, double2.zero);
        }

        private static Line.StyleLayer LineStyleLayer() => new Line.StyleLayer
        {
            Id          = "b3-line",
            LayerType   = StyleLayerType.Line,
            SourceLayer = "b3",
            Paint       = TestStyle.LinePaint("{\"line-color\":\"#ff0000\",\"line-width\":4}"),
            Layout      = TestStyle.LineLayout(),
        };

        private static double Shoelace2(NativeArray<double2> verts, int start, int len)
        {
            double sum = 0.0;
            for (int i = 0; i < len; i++)
            {
                double2 a = verts[start + i];
                double2 b = verts[start + (i + 1) % len];
                sum += a.x * b.y - b.x * a.y;
            }
            return sum;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillLayerKindGateTests — the observer for the precondition the kind-gate deferral rests on
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The observer for the precondition the kind-gate <b>deferral</b> rests on.
    ///
    /// <para><c>RingAssemblyJob</c> is NOT gated on <c>FeatureGeometryType</c>: every
    /// production path into it is polygon-filtered upstream by <c>StyledFillTileBuilder</c>, so the gate would
    /// be dead on every live path. That deferral is only defensible while the upstream filter holds, so
    /// this is the filter's observer.</para>
    /// </summary>
    [TestFixture]
    public class FillLayerKindGateTests : BaseTestFixture
    {
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double FixtureExtent = 4096.0;

        [Test]
        public void FillLayer_WithALineStringSelected_TriangulatesOnlyThePolygons()
        {
            List<double2> polygonRing = MvtCommandStream.Ring(1000, 1000, 3000, 1000, 3000, 3000, 1000, 3000);
            // >= 3 points AND a non-zero shoelace, so RingAssemblyJob WOULD classify it as a ring if it ever
            // reached it — otherwise this tooth would pass for the wrong reason.
            List<double2> lineRing = MvtCommandStream.Ring(200, 200, 800, 200, 800, 700);

            double lineArea2 = 0.0;
            for (int i = 0; i < lineRing.Count; i++)
            {
                double2 a = lineRing[i];
                double2 b = lineRing[(i + 1) % lineRing.Count];
                lineArea2 += a.x * b.y - b.x * a.y;
            }
            Assert.GreaterOrEqual(lineRing.Count, 3,
                "precondition: the LineString's ring must be long enough for fill's rLen filter to pass it");
            Assert.AreNotEqual(0.0, lineArea2,
                "precondition: the LineString's ring must have a non-zero area, or RingAssemblyJob would " +
                "have dropped it as degenerate and this tooth would pass for the wrong reason");

            var polygon = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false, geometry: MvtCommandStream.Feature(polygonRing));
            var line = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: MvtCommandStream.Feature(lineRing));

            Mesh control = Track(BuildFill(new List<IFeature> { polygon }));
            Mesh mixed   = Track(BuildFill(new List<IFeature> { polygon, line }));
            {
                Assert.IsNotNull(control, "non-vacuity: the polygon-only control must produce a mesh");
                Assert.Greater(control.vertexCount, 0, "non-vacuity: the control has vertices");
                Assert.GreaterOrEqual(control.triangles.Length, 3, "non-vacuity: the control has triangles");

                Assert.IsNotNull(mixed, "the mixed selection must still produce the polygon's mesh");
                Assert.AreEqual(control.vertexCount, mixed.vertexCount,
                    "adding a LineString feature to a FILL layer's selection must contribute nothing — the " +
                    "guard in StyledFillTileBuilder is the only thing keeping it out of RingAssemblyJob, " +
                    "which classifies purely by signed area");
                Assert.AreEqual(control.triangles.Length, mixed.triangles.Length,
                    "…including its triangles: a LineString ring reaching earcut is silent corruption, " +
                    "not a crash");
            }
        }

        private static Mesh BuildFill(IReadOnlyList<IFeature> features)
        {
            var styleLayer = new Fill.StyleLayer
            {
                Id          = "b3-fill",
                LayerType   = StyleLayerType.Fill,
                SourceLayer = "b3",
                Paint       = TestStyle.FillPaint("{\"fill-color\":\"#00ff00\"}"),
                Layout      = TestStyle.FillLayout(),
            };
            return TestTileMeshBuilder.BuildFill(
                features, styleLayer.Paint, 0.0, FixtureExtent, FixtureTile);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WallQuadJobParityTests — two-part comparison, same shape as StyledFillExtrusionGraphWriteTests
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WallQuadJobParityTests
    {
        private const double Extent = 4096.0;
        private static readonly TileId ModerateTile = new TileId { Z = 10, X = 300, Y = 380 };
        private static readonly TileId EquatorTile0 = new TileId { Z = 0, X = 0, Y = 0 };

        // ── Fixture builders — mirrors _CaptureWallGoldens.cs's own copies (that harness is deleted; these ──
        // ── recreate the SAME four fixtures the goldens were captured from, byte-for-byte). ──────────────────
        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));
        private static void AppendMoveTo(List<uint> cmds, ref int cx, ref int cy, int x, int y)
        { cmds.Add((1u << 3) | 1u); cmds.Add(ZigZag(x - cx)); cmds.Add(ZigZag(y - cy)); cx = x; cy = y; }
        private static void AppendLineTo(List<uint> cmds, ref int cx, ref int cy, params (int x, int y)[] pts)
        {
            cmds.Add(((uint)pts.Length << 3) | 2u);
            foreach (var p in pts) { cmds.Add(ZigZag(p.x - cx)); cmds.Add(ZigZag(p.y - cy)); cx = p.x; cy = p.y; }
        }
        private static uint[] SquareRing(int x0, int y0, int size)
        {
            var cmds = new List<uint>(); int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            return cmds.ToArray();
        }
        private static uint[] SquareWithHoleRing(int x0, int y0, int size, int holeX0, int holeY0, int holeSize)
        {
            var cmds = new List<uint>(); int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            AppendMoveTo(cmds, ref cx, ref cy, holeX0, holeY0);
            AppendLineTo(cmds, ref cx, ref cy, (holeX0, holeY0 + holeSize), (holeX0 + holeSize, holeY0 + holeSize), (holeX0 + holeSize, holeY0));
            return cmds.ToArray();
        }
        private static uint[] TwoPointLine(int x0, int y0, int x1, int y1)
        {
            var cmds = new List<uint>(); int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x1, y1));
            return cmds.ToArray();
        }
        private static IFeature SquareFeature(int x0, int y0, int size, IReadOnlyDictionary<string, Value> props = null)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon, geometry: SquareRing(x0, y0, size));
        private static IFeature CourtyardFeature(int x0, int y0, int size, int holeInset, int holeSize)
            => new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon,
                geometry: SquareWithHoleRing(x0, y0, size, x0 + holeInset, y0 + holeInset, holeSize));
        private static IFeature LineFeature(int x0, int y0, int x1, int y1)
            => new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, geometry: TwoPointLine(x0, y0, x1, y1));

        private static FillExtrusion.PaintProperties ConstantHeightPaint(double height)
            => TestStyle.FillExtrusionPaint($"{{\"fill-extrusion-height\":{height.ToString(CultureInfo.InvariantCulture)}}}");
        private static FillExtrusion.PaintProperties DataDrivenPaint()
            => TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":[\"get\",\"h\"],\"fill-extrusion-base\":[\"get\",\"b\"],\"fill-extrusion-color\":[\"get\",\"c\"]}");

        private struct FixtureCase
        {
            public string Name;
            public IReadOnlyList<IFeature> Features;
            public FillExtrusion.PaintProperties Paint;
            public TileId Tile;
        }

        private static FixtureCase Square() => new FixtureCase
        {
            Name = "square", Tile = ModerateTile, Paint = ConstantHeightPaint(50),
            Features = new[] { SquareFeature(1000, 1000, 500) },
        };
        private static FixtureCase Courtyard() => new FixtureCase
        {
            Name = "courtyard", Tile = ModerateTile, Paint = ConstantHeightPaint(50),
            Features = new[] { CourtyardFeature(2000, 1000, 600, holeInset: 150, holeSize: 300) },
        };
        private static FixtureCase HighLatitude() => new FixtureCase
        {
            Name = "high-latitude", Tile = EquatorTile0, Paint = ConstantHeightPaint(10),
            Features = new[] { SquareFeature(1800, 400, 100) },
        };
        private static FixtureCase Interleaved() => new FixtureCase
        {
            Name = "interleaved", Tile = ModerateTile, Paint = DataDrivenPaint(),
            Features = new IFeature[]
            {
                LineFeature(0, 0, 100, 100),
                SquareFeature(1000, 1000, 500, new Dictionary<string, Value>
                {
                    ["h"] = Value.Number(30.0), ["b"] = Value.Number(2.0),
                    ["c"] = Value.OfColor(new MapRenderer.Core.Expressions.Color(1.0, 0.0, 0.0, 1.0)),
                }),
                SquareFeature(2200, 1000, 400, new Dictionary<string, Value>
                {
                    ["h"] = Value.Number(75.0), ["b"] = Value.Number(9.0),
                    ["c"] = Value.OfColor(new MapRenderer.Core.Expressions.Color(0.0, 1.0, 0.0, 1.0)),
                }),
            },
        };

        // The ULP ceiling measured by StyledFillExtrusionGraphWriteTests on the same square+courtyard wall
        // chain. See docs/job-scheduling-design.md § "Invariants that constrain what is built next".
        private const int WallNormalTangentMaxUlp = 5;

        [TestCase("square", false), TestCase("square", true)]
        [TestCase("courtyard", false), TestCase("courtyard", true)]
        [TestCase("high-latitude", false), TestCase("high-latitude", true)]
        [TestCase("interleaved", false), TestCase("interleaved", true)]
        public void WallStreams_AreBitIdenticalOrWithinMeasuredBound_ToTheManagedGoldens(string fixtureName, bool spherical)
        {
            FixtureCase fc = fixtureName switch
            {
                "square" => Square(),
                "courtyard" => Courtyard(),
                "high-latitude" => HighLatitude(),
                "interleaved" => Interleaved(),
                _ => throw new ArgumentException(fixtureName),
            };
            string label = spherical ? "Spherical" : "WebMercator";
            IProjection projection = spherical ? (IProjection)new SphericalProjection() : new WebMercatorProjection();

            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(fc.Features);
            double3 renderOrigin = TileRenderOrigin.Project(fc.Tile, projection);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(fc.Features, fc.Tile, Extent);

            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, fc.Paint, 0.0, renderOrigin,
                    out colors, out bake, projection, clip: default);
                ringVisitOrder = input.RingVisitOrder;
                Assert.IsTrue(input.RingVisitOrder.IsCreated, $"{fixtureName}/{label}: fixture must select real work.");
                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                StyledFillExtrusionTileBuilder.WallColumns walls = ext.Walls;
                Assert.Greater(walls.VertexCount, 0, $"{fixtureName}/{label}: expected wall geometry.");

                var golden = LoadWallGolden(fixtureName, label);
                Assert.AreEqual(golden.VertexCount, walls.VertexCount,
                    $"{fixtureName}/{label}: golden vertex count ({golden.VertexCount}) != build's ({walls.VertexCount}) — a topology change.");
                Assert.AreEqual(golden.Indices.Length, walls.IndexCount,
                    $"{fixtureName}/{label}: golden index count ({golden.Indices.Length}) != build's ({walls.IndexCount}).");

                for (int i = 0; i < walls.IndexCount; i++)
                    Assert.AreEqual(golden.Indices[i], walls.Indices[i], $"{fixtureName}/{label}: Indices[{i}] diverges — a real regression.");

                for (int i = 0; i < walls.VertexCount; i++)
                {
                    StyledFillExtrusionTileBuilder.PositionNormal pn = walls.PositionNormal[i];
                    StyledFillExtrusionTileBuilder.ExtrudeAndBake eb = walls.Extrude[i];
                    Vector4 tan = walls.Tangent[i];
                    Vector4 col = walls.Color[i];

                    AssertBitExact(pn.Position.x, golden.PosHex[i * 3 + 0], fixtureName, label, i, "Position.x");
                    AssertBitExact(pn.Position.y, golden.PosHex[i * 3 + 1], fixtureName, label, i, "Position.y");
                    AssertBitExact(pn.Position.z, golden.PosHex[i * 3 + 2], fixtureName, label, i, "Position.z");

                    AssertBounded(pn.Normal.x, golden.NormHex[i * 3 + 0], WallNormalTangentMaxUlp, fixtureName, label, i, "Normal.x");
                    AssertBounded(pn.Normal.y, golden.NormHex[i * 3 + 1], WallNormalTangentMaxUlp, fixtureName, label, i, "Normal.y");
                    AssertBounded(pn.Normal.z, golden.NormHex[i * 3 + 2], WallNormalTangentMaxUlp, fixtureName, label, i, "Normal.z");

                    // ExtrudeUpAndT.xyz is BOUNDED, not bit-exact: math.normalize diverges from managed on some
                    // inputs (1 ULP at .x[0] on high-latitude/Spherical). .w stays bit-exact (the constant 0f/1f).
                    AssertBounded(eb.ExtrudeUpAndT.x, golden.ExtrudeHex[i * 4 + 0], WallNormalTangentMaxUlp, fixtureName, label, i, "ExtrudeUpAndT.x");
                    AssertBounded(eb.ExtrudeUpAndT.y, golden.ExtrudeHex[i * 4 + 1], WallNormalTangentMaxUlp, fixtureName, label, i, "ExtrudeUpAndT.y");
                    AssertBounded(eb.ExtrudeUpAndT.z, golden.ExtrudeHex[i * 4 + 2], WallNormalTangentMaxUlp, fixtureName, label, i, "ExtrudeUpAndT.z");
                    AssertBitExact(eb.ExtrudeUpAndT.w, golden.ExtrudeHex[i * 4 + 3], fixtureName, label, i, "ExtrudeUpAndT.w");
                    AssertBitExact(eb.BakedBaseHeight.x, golden.BakeHex[i * 2 + 0], fixtureName, label, i, "BakedBaseHeight.x");
                    AssertBitExact(eb.BakedBaseHeight.y, golden.BakeHex[i * 2 + 1], fixtureName, label, i, "BakedBaseHeight.y");

                    AssertBounded(tan.x, golden.TanHex[i * 4 + 0], WallNormalTangentMaxUlp, fixtureName, label, i, "Tangent.x");
                    AssertBounded(tan.y, golden.TanHex[i * 4 + 1], WallNormalTangentMaxUlp, fixtureName, label, i, "Tangent.y");
                    AssertBounded(tan.z, golden.TanHex[i * 4 + 2], WallNormalTangentMaxUlp, fixtureName, label, i, "Tangent.z");
                    AssertBitExact(tan.w, golden.TanHex[i * 4 + 3], fixtureName, label, i, "Tangent.w"); // always the constant 1f

                    AssertBitExact(col.x, golden.ColorHex[i * 4 + 0], fixtureName, label, i, "Color.x");
                    AssertBitExact(col.y, golden.ColorHex[i * 4 + 1], fixtureName, label, i, "Color.y");
                    AssertBitExact(col.z, golden.ColorHex[i * 4 + 2], fixtureName, label, i, "Color.z");
                    AssertBitExact(col.w, golden.ColorHex[i * 4 + 3], fixtureName, label, i, "Color.w");
                }
            }
            finally
            {
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        /// <summary>
        /// The wall chain honours the tile-buffer clip: it takes the SAME select-or-clip branch on
        /// <c>LayerInput.Clip</c> the roof takes, so a building crossing the tile-buffer window is extruded
        /// from the CLIPPED footprint. The shipped <c>FillTileBufferClip: 0</c> decodes to the ENABLED
        /// <c>KeepTileUnits(0.0)</c>; walls that skipped the clip would be raised by both neighbouring tiles.
        /// </summary>
        /// <remarks>
        /// Clipped: <c>whollyOutside</c> drops and <c>straddling</c> keeps 4 edges, so 16 wall vertices / 24
        /// indices; unclipped: 32 / 48, the control that pins the <c>RingSelectJob</c> arm. The differing roof
        /// counts prove the drop branch is reached. 16, not 8: every clipped edge gets a wall, cut edges too.
        /// See docs/job-scheduling-design.md § "Invariants that constrain what is built next".
        /// </remarks>
        [Test]
        public void Walls_HonourTheTileBufferClip()
        {
            var straddling = SquareFeature(3900, 3900, 500);
            var whollyOutside = SquareFeature(5000, 5000, 300);
            IReadOnlyList<IFeature> features = new[] { straddling, whollyOutside };
            FillExtrusion.PaintProperties paint = ConstantHeightPaint(50);
            IProjection projection = new WebMercatorProjection();

            BuildCounts enabled = BuildWallCounts(features, paint, projection, TileBufferClip.KeepTileUnits(0.0));
            BuildCounts disabled = BuildWallCounts(features, paint, projection, TileBufferClip.Disabled);

            Assert.AreNotEqual(disabled.RoofVertexCount, enabled.RoofVertexCount,
                "precondition: the two arms' ROOF vertex counts must differ — if they don't, the " +
                "wholly-outside feature's ring was not dropped by RingClipJob and this fixture is not " +
                "exercising the clip branch it exists to observe.");

            // (1) The clipped arm: whollyOutside dropped entirely, straddling clipped to 4 edges.
            Assert.AreEqual(16, enabled.WallVertexCount,
                "clipped walls: whollyOutside's ring is dropped (0 walls) and straddling clips to a 4-vertex " +
                "ring ⇒ 4 edges × 4 vertices = 16. 32 means the walls ignored the clip; 8 means the cut " +
                "edges were suppressed (the rejected alternative — see this test's doc).");
            Assert.AreEqual(24, enabled.WallIndexCount, "clipped walls: 4 edges × 6 indices = 24.");

            // (2) The disabled-arm control: the RingSelectJob arm is untouched by this fix.
            Assert.AreEqual(32, disabled.WallVertexCount,
                "unclipped walls: both squares survive at 4 edges each ⇒ 8 edges × 4 vertices = 32.");
            Assert.AreEqual(48, disabled.WallIndexCount, "unclipped walls: 8 edges × 6 indices = 48.");
        }

        /// <summary>Roof and wall counts of one <see cref="FillExtrusionMeshGraph.Schedule"/> build — the
        /// per-arm measurement <see cref="Walls_HonourTheTileBufferClip"/> compares.</summary>
        private struct BuildCounts
        {
            public int RoofVertexCount, WallVertexCount, WallIndexCount;
        }

        /// <summary>Runs the extrusion graph over one fixture at one clip setting and returns its counts.</summary>
        /// <param name="features">Tile features to build.</param>
        /// <param name="paint">The layer's fill-extrusion paint.</param>
        /// <param name="projection">Projection to build under.</param>
        /// <param name="clip">The tile-buffer clip the graph's roof AND wall chains both read.</param>
        /// <returns>Roof vertex count plus wall vertex/index counts.</returns>
        private static BuildCounts BuildWallCounts(
            IReadOnlyList<IFeature> features, FillExtrusion.PaintProperties paint,
            IProjection projection, TileBufferClip clip)
        {
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            double3 renderOrigin = TileRenderOrigin.Project(ModerateTile, projection);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);
            NativeArray<Vector4> colors = default; NativeArray<Vector2> bake = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, paint, 0.0, renderOrigin, out colors, out bake, projection, clip);
                Assert.IsTrue(input.RingVisitOrder.IsCreated, "precondition: the fixture must select real work.");
                // Capture it: BuildLayerInput hands ownership to the caller, so the finally below disposes
                // nothing unless this assignment happens.
                ringVisitOrder = input.RingVisitOrder;
                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                return new BuildCounts
                {
                    RoofVertexCount = ext.Roof.TileVertices.Length,
                    WallVertexCount = ext.Walls.VertexCount,
                    WallIndexCount  = ext.Walls.IndexCount,
                };
            }
            finally
            {
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        private struct WallGolden
        {
            public uint[] PosHex, NormHex, ExtrudeHex, BakeHex, TanHex, ColorHex;
            public int[] Indices;
            public int VertexCount;
        }

        private static WallGolden LoadWallGolden(string fixtureName, string projectionLabel)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", $"extrusion-wall-golden-{fixtureName}-{projectionLabel}.json");
            FileAssert.Exists(path);
            JsonValue root = JsonParser.Parse(File.ReadAllText(path));
            return new WallGolden
            {
                VertexCount = root.Get("observedWallVertexCount").AsInt(),
                PosHex = ParseHexArray(root.Get("positionHex").AsString(null)),
                NormHex = ParseHexArray(root.Get("normalHex").AsString(null)),
                ExtrudeHex = ParseHexArray(root.Get("extrudeUpAndTHex").AsString(null)),
                BakeHex = ParseHexArray(root.Get("bakedBaseHeightHex").AsString(null)),
                TanHex = ParseHexArray(root.Get("tangentHex").AsString(null)),
                ColorHex = ParseHexArray(root.Get("colorHex").AsString(null)),
                Indices = ParseIntArray(root.Get("indices")),
            };
        }

        private static uint[] ParseHexArray(string csv)
        {
            string[] parts = csv.Split(',');
            var result = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = Convert.ToUInt32(parts[i], 16);
            return result;
        }

        private static int[] ParseIntArray(JsonValue arr)
        {
            var items = arr.Items;
            var result = new int[items.Count];
            for (int i = 0; i < items.Count; i++) result[i] = items[i].AsInt();
            return result;
        }

        private static void AssertBitExact(float actual, uint goldenHex, string fixtureName, string label, int index, string field)
        {
            uint actualHex = math.asuint(actual);
            Assert.AreEqual(goldenHex, actualHex,
                $"{fixtureName}/{label}: {field}[{index}] diverges from the bit-exact golden — " +
                $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}). A real regression.");
        }

        private static void AssertBounded(float actual, uint goldenHex, int maxUlp, string fixtureName, string label, int index, string field)
        {
            uint actualHex = math.asuint(actual);
            ulong delta = UlpDistance(actualHex, goldenHex);
            Assert.LessOrEqual(delta, (ulong)maxUlp,
                $"{fixtureName}/{label}: {field}[{index}] exceeds its {maxUlp}-ULP ceiling — " +
                $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}) delta={delta} ULP.");
        }

        private static ulong ToUlpOrder(uint bits) => (bits & 0x80000000U) != 0 ? ~bits : (bits | 0x80000000U);
        private static ulong UlpDistance(uint a, uint b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WaterTriangulationTests — many-holed real water tiles — the case that breaks a hand-rolled ear-clipper
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Triangulation testbench over real many-holed OpenFreeMap water tiles, on the Burst fill path
    /// (<see cref="FillMeshGraph.Schedule"/>, <see cref="EarcutJob"/>). The boundary band stays ON: its
    /// triangles have ~zero area, so <see cref="MeshCoverageValidator.ValidateTriangulation"/> ignores them.
    /// <c>JobifiedWaterTriangulationTests</c> covers water-8-135-80.
    /// See docs/mesh-triangulation-robustness-design.md.
    /// </summary>
    public class WaterTriangulationTests
    {
        private static byte[] LoadFixture(string name)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", name);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException($"{name} not found walking up from {AppContext.BaseDirectory}");
        }

        private static readonly string[] Corpus = { "water-8-135-80.pbf.bytes", "water-6-32-20.pbf.bytes" };

        /// <summary>Converts an <see cref="EarcutJobPolygonRunner.Result"/> into the flat triangle list
        /// <see cref="MeshCoverageValidator.ValidateTriangulation"/> takes.</summary>
        private static List<(double2 a, double2 b, double2 c)> ToTriangles(EarcutJobPolygonRunner.Result res)
        {
            var tris = new List<(double2, double2, double2)>(res.Indices.Length / 3);
            for (int i = 0; i + 2 < res.Indices.Length; i += 3)
                tris.Add((res.Vertices[res.Indices[i]], res.Vertices[res.Indices[i + 1]], res.Vertices[res.Indices[i + 2]]));
            return tris;
        }

        /// <summary>Drives the REAL jobified fill path over one fixture/layer, band ON (see class doc), and
        /// validates the output against the ground-truth polygons the fixture's own command streams decode
        /// to. Mirrors <c>JobifiedWaterTriangulationTests</c>' body — the template this sweep applies to the
        /// remaining 7 corpus tiles.</summary>
        private static MeshCoverageValidator.Report RunOnBurstArm(string fixtureName, TileId tileId, string layerName)
        {
            byte[] mvtBytes = LoadFixture(fixtureName);
            using var mvtTile = MvtDecoder.Decode(tileId, mvtBytes);
            var layer = mvtTile.GetLayer(layerName);
            Assert.IsNotNull(layer, $"{fixtureName}: {layerName} layer present");

            var oracle = MvtFixtureStreams.ReadLayer(mvtBytes, layerName);
            var groundTruthPolys = new List<Polygon>();
            for (int fi = 0; fi < oracle.Kinds.Count; fi++)
            {
                if (oracle.Kinds[fi] != TileGeometryType.Polygon || oracle.Commands[fi] == null) continue;
                groundTruthPolys.AddRange(PolygonAssembler.Assemble(MvtGeometry.Decode(oracle.Commands[fi])));
            }
            Assert.Greater(groundTruthPolys.Count, 0, $"{fixtureName}: {layerName} layer has polygons");

            double extent = layer.Extent;
            var (bMin, _) = tileId.MercatorBounds();

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var pipelineInput = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(),
            };

            FillGraphOutput buffers = FillMeshGraph.Schedule(pipelineInput);
            buffers.Handle.Complete();
            try
            {
                Assert.IsTrue(buffers.IsCreated, $"{fixtureName}: jobified pipeline produced no buffers");

                int indexCount = buffers.TriangleIndices.Length;
                var tris = new List<(double2 a, double2 b, double2 c)>(indexCount / 3);
                for (int i = 0; i + 2 < indexCount; i += 3)
                {
                    double2 a = buffers.TileVertices[buffers.TriangleIndices[i]];
                    double2 b = buffers.TileVertices[buffers.TriangleIndices[i + 1]];
                    double2 c = buffers.TileVertices[buffers.TriangleIndices[i + 2]];
                    tris.Add((a, b, c));
                }

                return MeshCoverageValidator.ValidateTriangulation(
                    groundTruthPolys, tris, buffers.Counts[0].ForceClipCount, (int)extent);
            }
            finally
            {
                buffers.Dispose();
                visitOrder.Dispose();
            }
        }

        // ---- always-on guards -----------------------------------------------------------------------

        [Test]
        public void Validator_CleanSquareWithHole_IsPerfect()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(1000, 0), new double2(1000, 1000), new double2(0, 1000),
            };
            var hole = new List<double2>
            {
                new double2(400, 400), new double2(400, 600), new double2(600, 600), new double2(600, 400),
            };

            var res = EarcutJobPolygonRunner.Run(outer, hole);
            var groundTruth = new[] { new Polygon(outer) };
            groundTruth[0].Holes.Add(hole);

            var rep = MeshCoverageValidator.ValidateTriangulation(groundTruth, ToTriangles(res), res.ForceClips, extent: 1000);
            Assert.AreEqual(0, rep.ForceClips, "clean square+hole must not force-clip");
            Assert.AreEqual(0, rep.WindingFlips, "no folded triangles");
            Assert.Less(rep.AreaRelError, 0.001, "area = outer − hole");
            Assert.Less(rep.MismatchPct, 0.5, "coverage matches source; the hole is subtracted");
        }

        [Test]
        public void Corpus_DecodesAssemblesAndOuterRingsTriangulateClean()
        {
            foreach (string name in Corpus)
            {
                // The command streams come from the test-side fixture reader: reading production's buffer
                // would make this oracle audit itself.
                var layer = MvtFixtureStreams.ReadLayer(LoadFixture(name), "water");
                Assert.IsNotNull(layer, $"{name}: water layer present");
                int polys = 0;
                for (int fi = 0; fi < layer.Kinds.Count; fi++)
                {
                    if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;
                    foreach (var poly in PolygonAssembler.Assemble(MvtGeometry.Decode(layer.Commands[fi])))
                    {
                        polys++;
                        var res = EarcutJobPolygonRunner.Run(poly.Outer); // outer alone
                        Assert.AreEqual(0, res.ForceClips, $"{name}: outer ring should ear-clip cleanly");
                        double outerA = SignedArea.AbsArea(poly.Outer);
                        if (outerA > 1.0)
                        {
                            double tri = 0;
                            for (int i = 0; i + 2 < res.Indices.Length; i += 3)
                            {
                                var a = res.Vertices[res.Indices[i]]; var b = res.Vertices[res.Indices[i + 1]]; var c = res.Vertices[res.Indices[i + 2]];
                                tri += math.abs(0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)));
                            }
                            Assert.Less(math.abs(tri - outerA) / outerA, 0.005, $"{name}: outer-alone area conserved");
                        }
                    }
                }
                Assert.Greater(polys, 0, $"{name}: has water polygons");
            }
        }

        // ---- acceptance teeth for the hole-handling fix — Burst arm ----------------------------------
        // water-8-135-80 is proven by JobifiedWaterTriangulationTests (not duplicated here).

        [Test]
        public void Corpus_Water_6_32_20_TriangulatesFaithfully()
        {
            // Non-obvious why: water-6-32-20's poly 0 drops ONE sub-pixel locus the cure -> split cascade
            // cannot resolve, below the validator's raster resolution, so ForceClips is its only observer and
            // the count is pinned EXACTLY.
            var rep = RunOnBurstArm("water-6-32-20.pbf.bytes", new TileId { Z = 6, X = 32, Y = 20 }, "water");
            TestContext.WriteLine($"water-6-32-20 (Burst arm): {rep.Summary}");
            Assert.AreEqual(0, rep.WindingFlips, "water z6/32/20: NO folds/inversions allowed - " + rep.Summary);
            Assert.LessOrEqual(rep.AreaRelError, 0.01, "water z6/32/20: area conserved within 1% - " + rep.Summary);
            Assert.LessOrEqual(rep.MismatchPct, 1.0, "water z6/32/20: coverage matches the source - " + rep.Summary);
            Assert.AreEqual(1, rep.ForceClips,
                "water z6/32/20's clean-drop count (Burst arm) moved off its pinned value. " +
                "Reading 0 means the locus was RESOLVED - that is an IMPROVEMENT, not a regression: re-pin this " +
                "to 0 and delete the justification comment above. A higher count means a NEW drop appeared and " +
                "must be investigated before this pin is touched. Either way, do not widen this to an inequality - " +
                "ForceClips is the only instrument in this suite that can see a sub-cell drop. " + rep.Summary);
        }

        // ---- real coastline-dense tiles (fjords / archipelagos) with pathological hole counts ----------
        // 4 CLEAN ones triangulate perfectly; 2 HARD ones drop a bounded, counted locus, never a fold.

        // CLEAN real tiles — strict bar: ForceClips==0, WindingFlips==0, area+coverage within 1%.
        private static readonly (string File, TileId Id)[] CleanRealCorpus =
        {
            ("water-real-aegean-islands-8-145-99.pbf.bytes", new TileId { Z = 8, X = 145, Y = 99 }),
            ("water-real-norway-fjords-8-132-72.pbf.bytes", new TileId { Z = 8, X = 132, Y = 72 }),
            ("water-real-philippines-palawan-8-212-120.pbf.bytes", new TileId { Z = 8, X = 212, Y = 120 }),
            ("water-real-stockholm-archipelago-9-282-150.pbf.bytes", new TileId { Z = 9, X = 282, Y = 150 }),
        };

        [Test]
        public void Corpus_RealCleanTiles_TriangulateFaithfully()
        {
            foreach (var (name, id) in CleanRealCorpus)
            {
                var rep = RunOnBurstArm(name, id, "water");
                TestContext.WriteLine($"{name} (Burst arm): {rep.Summary}");
                Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                    $"{name} water triangulation is broken: {rep.Summary}\n{rep.AsciiMap}");
            }
        }

        // HARD real tiles: WindingFlips==0 (no fold), area conserved to <1%, and ForceClips <= 1, the
        // design's own tolerance. Both tiles sit at the bound; a clean drop is counted, never silent.
        private static readonly (string File, TileId Id)[] HardRealCorpus =
        {
            ("water-real-croatia-dalmatia-9-279-187.pbf.bytes", new TileId { Z = 9, X = 279, Y = 187 }),
            ("water-real-indonesia-rajaampat-8-220-128.pbf.bytes", new TileId { Z = 8, X = 220, Y = 128 }),
        };

        [Test]
        public void Corpus_RealHardTiles_DegradeGracefullyNeverFold()
        {
            foreach (var (name, id) in HardRealCorpus)
            {
                var rep = RunOnBurstArm(name, id, "water");
                TestContext.WriteLine($"{name} (Burst arm): {rep.Summary}");
                Assert.AreEqual(0, rep.WindingFlips, $"{name}: NO folds/inversions allowed — {rep.Summary}");
                Assert.Less(rep.AreaRelError, 0.01, $"{name}: area conserved within 1% — {rep.Summary}");
                Assert.LessOrEqual(rep.ForceClips, 1,
                    $"{name}: at most one bounded, surfaced clean drop — {rep.Summary}");
            }
        }

        // A reversed concave quad: no fold (WindingFlips==0) and area conserved. Limitation: the outer is
        // winding-normalised first, so this ring does not reproduce the reversed-residual overlap itself.
        [Test]
        public void Unit_ReversedConcaveQuad_NoFold_AreaConserved()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(4, 0), new double2(1, 1), new double2(0, 4),
            };
            var res = EarcutJobPolygonRunner.Run(outer);
            var rep = MeshCoverageValidator.ValidateTriangulation(
                new[] { new Polygon(outer) }, ToTriangles(res), res.ForceClips, extent: 4);
            Assert.AreEqual(0, rep.WindingFlips, "reversed-concave quad must not fold: " + rep.Summary);
            Assert.Less(rep.AreaRelError, 0.01, "reversed-concave quad area must be conserved: " + rep.Summary);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineBuildAllocTests — a constant style makes per-layer attribution columns allocate nothing
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LineBuildAllocTests
    {
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        // CONSTANT paint: Width/Opacity do not depend on the feature, so the per-feature TryEvaluate branches
        // do not run and the tooth measures only the three attribution arrays.
        private const string PaintJson = @"{""line-color"": ""#ff0000"", ""line-width"": 4}";

        /// <summary>
        /// A multi-ring, multi-feature line selection built with a CONSTANT paint. Measures a warmed
        /// <see cref="MapRenderer.Unity.Rendering.Meshing.StyledLineTileBuilder.WriteMeshData"/> call
        /// against a FRESH <c>Mesh.MeshDataArray</c> (never touched before), so a residual cannot be
        /// misattributed to <c>SetVertexBufferParams</c>/<c>SetIndexBufferParams</c> being called a
        /// second time on an already-declared <c>MeshData</c>.
        /// </summary>
        [Test]
        public void WriteMeshData_OverAConstantStyle_AllocatesNoGCMemory()
        {
            List<IFeature> features = SyntheticLineLayer();
            IReadOnlyList<SelectedTileFeature> selection = TestTileMeshBuilder.Selection(features);

            var styleLayer = new Line.StyleLayer
            {
                Id          = "alloc-line",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "alloc",
                Paint       = TestStyle.LinePaint(PaintJson),
                Layout      = TestStyle.LineLayout(),
            };
            var paint  = styleLayer.Paint;
            var layout = styleLayer.Layout;

            // Non-vacuity: a constant style bypasses both per-feature bake branches, so the tooth measures the
            // three attribution arrays and not the expression-eval path.
            Assert.IsFalse(paint.Width.DependsOnFeature,
                "precondition: line-width must be a CONSTANT — a data-driven width would exercise the " +
                "TryEvaluate bake branch, which is out of this stage's fence");
            Assert.IsFalse(paint.Opacity.DependsOnFeature,
                "precondition: line-opacity must be a CONSTANT for the same reason");

            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, Tile, Extent);
            try
            {
                Assert.Greater(geometry.RingCount, 1, "precondition: a multi-ring fixture — a single ring " +
                    "would not exercise the per-feature attribution columns meaningfully");

                double3 origin = double3.zero;

                // Warm-up (outside the measured region): JIT + one-time native growth. Its own MeshData —
                // never reused for the measured call.
                Mesh.MeshDataArray warmMda = Mesh.AllocateWritableMeshData(1);
                SyncMeshWrite.Line(warmMda[0], selection, geometry, paint, layout, Zoom,
                    origin, out int warmVertexCount, out Bounds _);
                warmMda.Dispose();
                Assert.Greater(warmVertexCount, 0,
                    "non-vacuity: the warm-up call must actually produce line geometry");

                // Measured call — a FRESH MeshDataArray (never SetVertexBufferParams'd before), so a
                // residual cannot be the reused-MeshData artifact the plan's discriminator names.
                Mesh.MeshDataArray measuredMda = Mesh.AllocateWritableMeshData(1);
                try
                {
                    Assert.That(() =>
                    {
                        SyncMeshWrite.Line(measuredMda[0], selection, geometry, paint, layout,
                            Zoom, origin, out int _, out Bounds _);
                    },
                    Is.Not.AllocatingGCMemory(),
                    "WriteMeshData must not allocate managed memory for a constant-style, multi-ring build " +
                    "once the three per-layer attribution columns are NativeArray<T> instead of T[]");
                }
                finally
                {
                    measuredMda.Dispose();
                }
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>Three LineString features, two of them multi-ring — real geometry through the shared
        /// <see cref="MvtCommandStream"/> encoder, not a hand-rolled buffer.</summary>
        private static List<IFeature> SyntheticLineLayer() => new List<IFeature>
        {
            new DictionaryFeature(
                properties:   new Dictionary<string, Value> { ["cls"] = Value.String("a") },
                geometryType: TileGeometryType.LineString,
                geometry:     MvtCommandStream.Feature(
                    MvtCommandStream.Ring(400, 500, 1200, 500, 2000, 500))),
            new DictionaryFeature(
                properties:   new Dictionary<string, Value> { ["cls"] = Value.String("b") },
                geometryType: TileGeometryType.LineString,
                geometry:     MvtCommandStream.Feature(
                    MvtCommandStream.Ring(300, 1500, 1100, 1500, 2100, 1500),
                    MvtCommandStream.Ring(300, 1800, 1100, 1800, 2100, 1800))),
            new DictionaryFeature(
                properties:   new Dictionary<string, Value> { ["cls"] = Value.String("c") },
                geometryType: TileGeometryType.LineString,
                geometry:     MvtCommandStream.Feature(
                    MvtCommandStream.Ring(400, 2000, 1600, 2000),
                    MvtCommandStream.Ring(700, 2500, 2400, 2500))),
        };
    }
}
