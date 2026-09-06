// Unity EditMode only — drives StyledLineTileBuilder and the Burst Waist-1 materializer over native
// containers. NOT registered in Tools/core-tests.

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

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// IR stage B3: the teeth on moving <c>StyledLineTileBuilder</c> off its own managed
    /// <c>MvtGeometry.Decode</c> and onto the shared <see cref="TileGeometryBuffers"/>.
    ///
    /// <para><b>Why a differential oracle rather than a snapshot.</b> B1 and B2 could argue byte-identity by
    /// construction — only a call path moved. B3 cannot: two different decoder implementations
    /// (managed <c>List&lt;List&lt;double2&gt;&gt;</c> vs Burst flat <c>NativeArray</c>), a different ring
    /// iteration shape, and a new per-feature kind gate. Equality is therefore <b>measured</b>: the same tile,
    /// the same features, decoded both ways in the same test run and compared element-wise at the exact seam
    /// that moved. A snapshot could only say "a number moved"; this says "these two decoders disagree, at ring
    /// k, at point i".</para>
    /// </summary>
    [TestFixture]
    public class StyledLineBufferParityTests
    {
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double FixtureExtent = 4096.0;
        private const double TestZoom      = 0.0;

        // A degenerate-area ring under RingAssemblyJob's own threshold — mirrored here so T5 can state, in
        // the test, that its fixture really is the case fill would drop.
        private const double FillDegenerateThreshold = 1.0;

        // ── T1: the differential oracle ────────────────────────────────────────────────────────────

        /// <summary>
        /// T1 — the ring list the line consumer iterates, read out of the shared buffer, is element-wise
        /// identical to the one the OLD managed decoder produces for the same features, running live in the
        /// same process. Everything downstream of this ring list is untouched code, so ring-level equality is
        /// mesh-level equality; the line pixel suites are the end-to-end confirmation of that implication.
        /// <para>Exact comparison, no tolerance: both decoders accumulate <c>long</c> deltas and emit
        /// <c>(double)</c> of integer magnitudes far inside <c>double</c>'s exact range, so the vertices are
        /// bit-identical. A tolerance here would be a weakened tooth.</para>
        /// </summary>
        [Test]
        public void LineRings_FromTheSharedBuffer_MatchTheManagedDecodeOracle()
        {
            ITileLayer geolines = GeolinesLayer();
            IReadOnlyList<IFeature> features = geolines.Features;
            // Arm A's input comes from the BYTES, through the independent fixture reader — not from the
            // decoded features, which carry no command stream since IR C1 P3 (and reading production's own
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
            // IR C1 P3: arm B reads the DECODED LAYER's own buffer — the very object production consumes —
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

        // ── T2: the coexistence gate (a gate that cannot mis-classify is inert) ────────────────────

        /// <summary>
        /// T2 — with polygon rings and line rings genuinely coexisting in one buffer, the line layer ribbons
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

        // ── T3: line's OWN length threshold — `< 2`, never fill's `< 3` ────────────────────────────

        /// <summary>
        /// T3 — a two-point polyline is the boundary value of line's own filter and must still render. B2's
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

        // ── T4: ownership on the empty paths ───────────────────────────────────────────────────────

        /// <summary>
        /// T4 — the paths where the layer produces nothing must produce nothing <i>and not throw</i>, and the
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
            var nullGeometry = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.LineString,
                Geometry     = null,
            };
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

        // ── T5: the fused-RingAssemblyJob fence, behaviourally ────────────────────────────────────

        /// <summary>
        /// T5 — a perfectly straight polyline has <b>exactly zero</b> signed area, i.e. strictly inside
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

        /// <summary>IR C1 P3: the decoded fixture tile owns Allocator.Persistent buffers.</summary>
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
            => new InMemoryTileFeature
            {
                GeometryType = kind,
                Geometry     = MvtCommandStream.Feature(rings),
            };

        private static int LineVertexCount(IReadOnlyList<IFeature> features)
        {
            StyleLayer styleLayer = LineStyleLayer();
            return TestTileMeshBuilder.LineVertexCount(
                features, new Line.PaintProperties(styleLayer), new Line.LayoutProperties(styleLayer),
                TestZoom, FixtureExtent, FixtureTile, double2.zero);
        }

        private static StyleLayer LineStyleLayer() => new StyleLayer
        {
            Id          = "b3-line",
            LayerType   = StyleLayerType.Line,
            SourceLayer = "b3",
            PaintJson   = MapRenderer.Core.Json.JsonParser.Parse("{\"line-color\":\"#ff0000\",\"line-width\":4}"),
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

    /// <summary>
    /// IR stage B3, T10: the observer for the precondition B3's <b>deferral</b> rests on.
    ///
    /// <para>B3 deliberately does NOT gate <c>RingAssemblyJob</c> on <c>FeatureGeometryType</c>: every
    /// production path into it is polygon-filtered upstream by <c>StyledFillTileBuilder</c>, so the gate would
    /// be dead on every live path. That deferral is only defensible while the upstream filter actually holds —
    /// and until now the filter had <b>no observer at all</b>, which is the "recorded limitation with no
    /// observing tooth" failure family. This is the observer.</para>
    /// </summary>
    [TestFixture]
    public class FillLayerKindGateTests
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

            var polygon = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                Geometry     = MvtCommandStream.Feature(polygonRing),
            };
            var line = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.LineString,
                Geometry     = MvtCommandStream.Feature(lineRing),
            };

            Mesh control = BuildFill(new List<IFeature> { polygon });
            Mesh mixed   = BuildFill(new List<IFeature> { polygon, line });
            try
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
            finally
            {
                if (control != null) UnityEngine.Object.DestroyImmediate(control);
                if (mixed != null)   UnityEngine.Object.DestroyImmediate(mixed);
            }
        }

        private static Mesh BuildFill(IReadOnlyList<IFeature> features)
        {
            var styleLayer = new StyleLayer
            {
                Id          = "b3-fill",
                LayerType   = StyleLayerType.Fill,
                SourceLayer = "b3",
                PaintJson   = MapRenderer.Core.Json.JsonParser.Parse("{\"fill-color\":\"#00ff00\"}"),
            };
            return TestTileMeshBuilder.BuildFill(
                features, new Fill.PaintProperties(styleLayer), 0.0, FixtureExtent, FixtureTile);
        }
    }
}
