// Unity EditMode only — drives the Burst fill pipeline over native containers. NOT registered in
// Tools/core-tests.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Collections;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR stage B2: the teeth on Waist 1's producer seam as seen from <c>FillMeshPipeline.Schedule</c>.
    ///
    /// <para>The fixture is one GeoJSON polygon <b>with a hole</b>, authored by inverting a chosen tile's own
    /// <c>ToLonLat</c> so both rings land on exact tile-local integers well inside the tile, and sliced by the
    /// production <c>GeoJsonParser</c> → <c>GeoJsonProjectedDataset</c> → <c>GeoJsonTileSlicer</c> stack. The
    /// extent is <b>8192, never 4096</b>: every fill fixture in the repo uses 4096, so a stage that
    /// substituted that literal for the buffer's own extent would be undiscriminated by all of them.</para>
    ///
    /// <para><b>GeoJSON S2: the arm is now the PRODUCTION decoder.</b> These teeth used to drive a test-side
    /// <c>GeoJsonSliceMaterializer</c> that flattened a <c>TileSlice</c> onto the producer seam. S2 promoted
    /// that flatten into <see cref="GeoJsonTileDecoder"/>, and the test-side copy was deleted rather than
    /// kept: a second implementation of the same join is one that can drift, and every test using the double
    /// would then go green against a shape production does not produce.</para>
    /// </summary>
    [TestFixture]
    public class TileGeometryMaterializerSeamTests
    {
        // z14 puts the whole fixture inside one tile at metre-scale resolution; x/y are arbitrary land.
        private static readonly TileId FixtureTile = new TileId { Z = 14, X = 8000, Y = 5000 };

        private const double ExtentHigh = 8192.0;
        private const double ExtentLow  = 4096.0;

        // Tile-local corners the fixture is authored to hit, at ExtentHigh. All even, so halving them for
        // ExtentLow lands on exact integers too — no quantization collapse between the two arms.
        private const double OuterMin = 1000.0, OuterMax = 7000.0;
        private const double HoleMin  = 3000.0, HoleMax  = 5000.0;

        /// <summary>
        /// T1 — the pipeline is <b>producer-blind</b>: a GeoJSON tile and an MVT tile carrying the same
        /// tile-local rings produce bit-identical mesh buffers. A differential over producers, so it cannot
        /// pass by both arms being equally wrong about the pipeline.
        /// </summary>
        [Test]
        public void Materializer_GeoJsonAndMvt_SameTileLocalRings_ProduceBitIdenticalMeshBuffers()
        {
            TileSlice slice = SliceFixture(ExtentHigh);
            AssertFixtureShape(slice, ExtentHigh);

            TileMeshBuffers geoJson = RunGeoJson(ExtentHigh, TileBufferClip.Disabled, slice);
            TileMeshBuffers mvt     = RunMvt(slice, TileBufferClip.Disabled);

            try
            {
                // Non-vacuity: both arms really produced a triangulated polygon WITH a hole, at an extent
                // that is not the one every other fill fixture uses.
                Assert.AreNotEqual(4096.0, slice.Extent,
                    "the fixture must not sit at the extent every other fill fixture uses");
                Assert.IsTrue(geoJson.IsCreated, "the GeoJSON arm produced no buffers");
                Assert.IsTrue(mvt.IsCreated, "the MVT arm produced no buffers");
                Assert.Greater(geoJson.VertexCount[0], 0, "the GeoJSON arm produced no vertices");
                Assert.GreaterOrEqual(geoJson.TotalIndexCount, 3, "the GeoJSON arm produced no triangles");
                Assert.AreEqual(1, geoJson.PolygonCount[0], "precondition: exactly one polygon");
                Assert.AreEqual(1, geoJson.HoleCount[0],
                    "precondition: the hole path in RingAssemblyJob + EarcutJob really ran");
                Assert.AreEqual(2, geoJson.RingCount[0], "precondition: exterior + hole reached assembly");

                Assert.AreEqual(geoJson.VertexCount[0], mvt.VertexCount[0], "vertex counts must match");
                Assert.AreEqual(geoJson.PolygonCount[0], mvt.PolygonCount[0], "polygon counts must match");
                Assert.AreEqual(geoJson.RingCount[0], mvt.RingCount[0], "ring counts must match");
                Assert.AreEqual(geoJson.HoleCount[0], mvt.HoleCount[0], "hole counts must match");
                Assert.AreEqual(geoJson.TotalIndexCount, mvt.TotalIndexCount, "index counts must match");

                for (int i = 0; i < geoJson.VertexCount[0]; i++)
                {
                    Assert.AreEqual(geoJson.TileVertices[i], mvt.TileVertices[i], $"TileVertices[{i}]");
                    Assert.AreEqual(geoJson.WorldPositions[i].x, mvt.WorldPositions[i].x, $"WorldPositions[{i}].x");
                    Assert.AreEqual(geoJson.WorldPositions[i].y, mvt.WorldPositions[i].y, $"WorldPositions[{i}].y");
                    Assert.AreEqual(geoJson.WorldPositions[i].z, mvt.WorldPositions[i].z, $"WorldPositions[{i}].z");
                }

                for (int i = 0; i < geoJson.TotalIndexCount; i++)
                    Assert.AreEqual(geoJson.TriangleIndices[i], mvt.TriangleIndices[i], $"TriangleIndices[{i}]");
            }
            finally
            {
                geoJson.Dispose();
                mvt.Dispose();
            }
        }

        /// <summary>
        /// T2a — Stage 1b's clip window is derived from the <b>buffer's own</b> extent. At extent 8192 the
        /// standard 64-unit buffer gives <c>[−128, 8320]²</c>, which contains the whole fixture, so enabling
        /// the clip is a provable no-op. A window sized from the 4096 reference literal would be
        /// <c>[−64, 4224]²</c> and would cut the fixture in half.
        /// </summary>
        [Test]
        public void ClipWindow_UsesTheBuffersOwnExtent_NotTheReferenceExtent()
        {
            TileSlice slice = SliceFixture(ExtentHigh);
            AssertFixtureShape(slice, ExtentHigh);

            // Non-vacuity, all three required: the RIGHT window contains the fixture, the WRONG window would
            // cut it, and the fixture really does reach past the wrong window's edge.
            var clip = TileBufferClip.KeepTileUnits(64.0);
            Assert.IsTrue(clip.TryWindow(ExtentHigh, out _, out double2 rightMax),
                "precondition: the clip is enabled at the fixture's extent");
            Assert.Greater(rightMax.x, OuterMax, "the correct window must contain the fixture");
            Assert.IsTrue(clip.TryWindow(TileBufferClip.ReferenceExtent, out _, out double2 wrongMax),
                "precondition: the reference-extent window is computable");
            Assert.Less(wrongMax.x, OuterMax, "the wrong window really would cut the fixture");
            Assert.Greater(MaxTileLocalX(slice), wrongMax.x,
                "the fixture must reach past the wrong window's edge, or no window could discriminate");

            TileMeshBuffers unclipped = RunGeoJson(ExtentHigh, TileBufferClip.Disabled, slice);
            TileMeshBuffers clipped   = RunGeoJson(ExtentHigh, clip, slice);

            try
            {
                Assert.IsTrue(unclipped.IsCreated, "the unclipped arm produced no buffers");
                Assert.IsTrue(clipped.IsCreated, "the clipped arm produced no buffers");
                Assert.Greater(unclipped.VertexCount[0], 0, "precondition: there is geometry to compare");
                Assert.AreEqual(1, unclipped.HoleCount[0], "precondition: the hole survived the unclipped arm");

                Assert.AreEqual(unclipped.VertexCount[0], clipped.VertexCount[0],
                    "clipping at the buffer's own extent is a no-op for a fixture wholly inside the window");
                Assert.AreEqual(unclipped.TotalIndexCount, clipped.TotalIndexCount, "index counts must match");
                Assert.AreEqual(unclipped.HoleCount[0], clipped.HoleCount[0], "hole counts must match");

                for (int i = 0; i < unclipped.VertexCount[0]; i++)
                    Assert.AreEqual(unclipped.TileVertices[i], clipped.TileVertices[i], $"TileVertices[{i}]");
                for (int i = 0; i < unclipped.TotalIndexCount; i++)
                    Assert.AreEqual(unclipped.TriangleIndices[i], clipped.TriangleIndices[i],
                        $"TriangleIndices[{i}]");
            }
            finally
            {
                unclipped.Dispose();
                clipped.Dispose();
            }
        }

        /// <summary>
        /// T2b — Stage 4a's tile→geodetic conversion reads the <b>buffer's own</b> extent too, which T2a
        /// cannot see. The same geodetic polygon sliced into the same tile at 4096 and at 8192 must land in
        /// the same place in world space; a <c>TileToGeoJob</c> reading a 4096 literal would place the 8192
        /// arm at twice tile-local scale — an error of order a whole tile edge.
        /// </summary>
        [Test]
        public void TileToGeo_UsesTheBuffersOwnExtent_SoTwoExtentsAgreeInWorldSpace()
        {
            TileSlice lowSlice  = SliceFixture(ExtentLow);
            TileSlice highSlice = SliceFixture(ExtentHigh);
            AssertFixtureShape(lowSlice, ExtentLow);
            AssertFixtureShape(highSlice, ExtentHigh);

            TileMeshBuffers low  = RunGeoJson(ExtentLow,  TileBufferClip.Disabled, lowSlice);
            TileMeshBuffers high = RunGeoJson(ExtentHigh, TileBufferClip.Disabled, highSlice);

            try
            {
                // Quantization is at most 0.5 tile units per arm, and one tile unit at extent E is
                // (tile edge in world metres) / E. So the two arms can disagree by at most half a tile unit
                // of each — no magic number, and nothing about the pipeline is assumed.
                double tileEdgeWorld = 2.0 * WebMercator.WorldExtent / math.pow(2.0, FixtureTile.Z);
                double tolerance     = 0.5 * tileEdgeWorld / ExtentLow + 0.5 * tileEdgeWorld / ExtentHigh;

                // Non-vacuity: the comparison is element-to-element and not vacuously empty, and the
                // tolerance cannot pass a whole-tile-scale error.
                Assert.Greater(low.VertexCount[0], 0, "precondition: the 4096 arm produced vertices");
                Assert.AreEqual(low.VertexCount[0], high.VertexCount[0],
                    "both extents must triangulate to the same vertex count, or the comparison is not " +
                    "element-to-element (move the fixture corners, never loosen the tolerance)");
                Assert.AreEqual(low.TotalIndexCount, high.TotalIndexCount, "index counts must match");
                Assert.Less(tolerance * 100.0, tileEdgeWorld,
                    "the tolerance must be at least 100x smaller than a tile edge, or it could pass by being loose");

                for (int i = 0; i < low.VertexCount[0]; i++)
                {
                    Assert.AreEqual(low.WorldPositions[i].x, high.WorldPositions[i].x, tolerance,
                        $"WorldPositions[{i}].x disagrees between extents 4096 and 8192");
                    Assert.AreEqual(low.WorldPositions[i].y, high.WorldPositions[i].y, tolerance,
                        $"WorldPositions[{i}].y disagrees between extents 4096 and 8192");
                    Assert.AreEqual(low.WorldPositions[i].z, high.WorldPositions[i].z, tolerance,
                        $"WorldPositions[{i}].z disagrees between extents 4096 and 8192");
                }
            }
            finally
            {
                low.Dispose();
                high.Dispose();
            }
        }

        // ── Fixture ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The polygon-with-hole, authored in lon/lat by inverting the tile's own
        /// <c>ToLonLat</c> at <see cref="ExtentHigh"/> so it lands on the tile-local integers above. The RFC
        /// winding argument is irrelevant — <c>GeoJsonParser</c> re-encodes ring role as winding — so the
        /// hole is authored reversed only to keep the JSON honest to the RFC.</summary>
        private static string FixtureJson()
        {
            double2 outerNw = FixtureTile.ToLonLat(OuterMin, OuterMin, ExtentHigh);
            double2 outerSe = FixtureTile.ToLonLat(OuterMax, OuterMax, ExtentHigh);
            double2 holeNw  = FixtureTile.ToLonLat(HoleMin, HoleMin, ExtentHigh);
            double2 holeSe  = FixtureTile.ToLonLat(HoleMax, HoleMax, ExtentHigh);

            // Tile-local Y grows SOUTHWARD, so the small-Y corner carries the NORTH latitude.
            string exterior = GeoJsonTestFixtures.RectangleRing(outerNw.x, outerSe.y, outerSe.x, outerNw.y);
            string hole     = GeoJsonTestFixtures.RectangleRing(holeNw.x, holeSe.y, holeSe.x, holeNw.y,
                                                                rfcWound: false);

            return GeoJsonTestFixtures.Collection(
                GeoJsonTestFixtures.Feature("Polygon", $"[{exterior},{hole}]"));
        }

        private static TileSlice SliceFixture(double extent)
            => GeoJsonTestFixtures.Slice(
                FixtureJson(), FixtureTile, GeoJsonTestFixtures.Options(extent, 64.0));

        /// <summary>Pins that the production slicer really put the fixture where the tests reason it is —
        /// one feature, two four-point rings, on the exact tile-local integers, inside the tile it was
        /// authored for.</summary>
        private static void AssertFixtureShape(TileSlice slice, double extent)
        {
            Assert.AreEqual(extent, slice.Extent, "the slice carries the extent it was sliced at");
            Assert.AreEqual(1, slice.Features.Count, "the fixture is exactly one feature");
            Assert.AreEqual(2, slice.Features[0].Paths.Count, "exterior + hole");
            Assert.AreEqual(4, slice.Features[0].Paths[0].Count, "the exterior is a 4-point ring");
            Assert.AreEqual(4, slice.Features[0].Paths[1].Count, "the hole is a 4-point ring");

            double scale = extent / ExtentHigh;
            Assert.AreEqual(OuterMax * scale, MaxTileLocalX(slice), 1e-9,
                "the fixture must quantize onto the exact tile-local integer it was authored for");
        }

        private static double MaxTileLocalX(TileSlice slice)
        {
            double maxX = double.NegativeInfinity;
            foreach (SlicedFeature feature in slice.Features)
                foreach (IReadOnlyList<double2> path in feature.Paths)
                    for (int i = 0; i < path.Count; i++)
                        maxX = math.max(maxX, path[i].x);
            return maxX;
        }

        /// <summary>Re-encodes the slice's OWN tile-local integer rings as an MVT command stream and
        /// materializes it, so the two arms differ only in which producer put the identical numbers into the
        /// buffer.</summary>
        private static TileGeometryBuffers MvtArm(TileSlice slice)
        {
            var kinds    = new List<TileGeometryType>(slice.Features.Count);
            var commands = new List<uint[]>(slice.Features.Count);
            foreach (SlicedFeature feature in slice.Features)
            {
                var rings = new IReadOnlyList<double2>[feature.Paths.Count];
                for (int p = 0; p < feature.Paths.Count; p++)
                    rings[p] = feature.Paths[p];
                kinds.Add(feature.Source.GeometryType);
                commands.Add(MvtCommandStream.Feature(rings));
            }

            return MvtGeometryMaterializerTestFactory.Materialize(slice.Tile, slice.Extent, kinds, commands);
        }

        /// <summary>The GeoJSON arm, driven through the <b>production</b> decoder — parse → project → slice →
        /// materialize → layer, exactly as a live tile takes it. The decoded tile OWNS the buffer, so it is
        /// disposed here and never by <see cref="Run"/>.</summary>
        private static TileMeshBuffers RunGeoJson(double extent, TileBufferClip clip, TileSlice expected)
        {
            var decoder = new GeoJsonTileDecoder(
                GeoJsonProjectedDataset.Project(GeoJsonParser.Parse(FixtureJson())),
                GeoJsonTestFixtures.Options(extent, 64.0));

            using IDecodedTile tile = decoder.Decode(FixtureTile, null);
            ITileLayer layer = tile.GetLayer(null);

            Assert.IsNotNull(layer,
                "precondition: the production decoder must yield a layer for a tile the fixture is inside — " +
                "a null layer would make every comparison below vacuous");
            Assert.AreEqual(expected.Features.Count, layer.Features.Count,
                "precondition: the decoder must carry exactly the features the slicer produced (slicing is a " +
                "pure function of dataset/tile/options, so the two runs must agree)");
            Assert.AreEqual((uint)extent, layer.Extent, "precondition: the layer reports the sliced extent");

            return Run(layer.Geometry, clip);
        }

        /// <summary>The MVT arm: the slice's own tile-local integers re-encoded as a command stream, so the
        /// two arms differ only in which producer put identical numbers into the buffer. This one mints the
        /// buffer, so this one frees it.</summary>
        private static TileMeshBuffers RunMvt(TileSlice slice, TileBufferClip clip)
        {
            TileGeometryBuffers geometry = MvtArm(slice);
            try     { return Run(geometry, clip); }
            finally { geometry.Dispose(); }
        }

        /// <summary>IR B7: visit every ring in decode order, schedule, and free what this harness owns.
        /// <c>Schedule</c> BORROWS the buffer — it derives its own — so the caller keeps ownership and this
        /// never disposes it.</summary>
        private static TileMeshBuffers Run(TileGeometryBuffers geometry, TileBufferClip clip)
        {
            var (boundsMin, _) = FixtureTile.MercatorBounds();
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            try
            {
                return FillMeshPipeline.Schedule(new FillMeshPipeline.LayerInput
                {
                    Geometry       = geometry,
                    RingVisitOrder = visitOrder,
                    OriginRender   = new double3(boundsMin.x, 0.0, boundsMin.y),
                    Clip           = clip,
                });
            }
            finally
            {
                visitOrder.Dispose();
            }
        }
    }
}
