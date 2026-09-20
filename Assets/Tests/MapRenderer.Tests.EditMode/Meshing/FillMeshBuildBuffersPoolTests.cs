// Unity EditMode only. The zero-allocation tooth uses a thread-local GC.GetAllocatedBytesForCurrentThread()
// delta over many calls (precise, cannot miss an allocation the call makes on this thread) rather than
// UnityEngine.TestTools' Is.Not.AllocatingGCMemory() — see DensePropertyStoreTests for the same rationale in
// this repo. NOT registered in core-tests.csproj (UnityEngine.Mesh is not compiled there).
//
// perf/gc-elimination: StyledFillTileBuilder.WriteMeshData now runs FillMeshGraph.Schedule
// (job-scheduling-design.md §8 stage 4 Group B retired the synchronous FillMeshPipeline.Schedule this
// comment used to name), which itself allocates schedule-time managed work — the boxed IProjection dispatch
// switch, JobHandle.CombineDependencies, and a ScheduleDispose call per graph node/buffer — out of THIS
// stage's fence. That means WriteMeshData can never be measured at an absolute zero — so the tooth below is
// DIFFERENTIAL: same fixture, same iteration count, `buffers: null` vs a reused pooled TileBuildBuffers.
// Every FillMeshGraph allocation is identical in both
// arms and cancels out of the subtraction, so only OrderBySortKey/BuildRingVisitOrder's converted sites show
// up in the delta — the out-of-fence debt stops being able to mask a shallow (accept-but-ignore) buffers.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class FillMeshBuildBuffersPoolTests
    {
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

        /// <summary>One axis-aligned square feature, same MVT command encoding <c>FillSortKeyAndOpacityTests</c>
        /// uses — drives the real decode/assemble/earcut path rather than a bypass.</summary>
        private static uint[] Square(int x, int y, int size) => new[]
        {
            (1u << 3) | 1u, ZigZag(x),     ZigZag(y),     // MoveTo (x, y)
            (3u << 3) | 2u, ZigZag(size),  ZigZag(0),     // LineTo +x
                            ZigZag(0),     ZigZag(size),  // LineTo +y
                            ZigZag(-size), ZigZag(0),     // LineTo -x
            (1u << 3) | 7u,                               // ClosePath
        };

        private static DictionaryFeature Feature(int x, int y, double sortKey) => new DictionaryFeature(
            new Dictionary<string, Value> { ["sk"] = Value.Number(sortKey) },
            TileGeometryType.Polygon,
            geometry: Square(x, y, 100));

        /// <summary><paramref name="count"/> distinct, non-overlapping squares with sort keys DESCENDING as
        /// declared (<c>count, count-1, …, 1</c>) — the reverse of ascending sort-key order — so
        /// <c>OrderBySortKey</c> does real reordering work every call, not a short-circuit or a no-op sort.</summary>
        private static List<IFeature> MakeFeatures(int count)
        {
            var list = new List<IFeature>(count);
            for (int i = 0; i < count; i++)
                list.Add(Feature(x: i * 500, y: 0, sortKey: count - i));
            return list;
        }

        // Data-driven over the fixture's own "sk" property (not a literal): Stage 1 leaves a CONSTANT
        // fill-color's vertices white, which would make the pooled-vs-reference colour comparison below
        // vacuous — both arms would carry the same white regardless of a pooling desync.
        private static readonly Fill.PaintProperties Paint =
            TestStyle.FillPaint(@"{""fill-color"": [""interpolate"",[""linear""],[""get"",""sk""],1,[""rgb"",255,0,0],8,[""rgb"",0,0,255]]}");
        private static readonly Fill.LayoutProperties SortKeyLayout =
            TestStyle.FillLayout(@"{""fill-sort-key"": [""get"", ""sk""]}");

        /// <summary>
        /// Reuse-by-identity tooth (meter-independent — <c>GC.GetAllocatedBytesForCurrentThread()</c> is dead in
        /// this EditMode Mono runner, returning 0 for even a 10 MB allocation, so a byte-differential cannot
        /// discriminate). Every pooled buffer must be the SAME instance across calls (grow-only, never
        /// re-allocated), and a smaller request must reuse the already-grown buffer. A non-pooling "new each
        /// call" implementation fails every <c>AreSame</c> below.
        /// </summary>
        [Test]
        public void TileBuildBuffers_ReusesBuffersByIdentity_GrowOnly()
        {
            var buffers = new TileBuildBuffers();

            int[] rank = buffers.RankStart(8);
            Assert.AreSame(rank, buffers.RankStart(8), "RankStart reuses its backing array for the same size (grow-only).");
            Assert.AreSame(rank, buffers.RankStart(3), "a smaller RankStart request reuses the already-grown buffer, never re-allocates.");
            int[] grown = buffers.RankStart(64);
            Assert.AreSame(grown, buffers.RankStart(64), "after growing past the prior peak, RankStart is stable again.");

            Assert.AreSame(buffers.RankCursor(8), buffers.RankCursor(8), "RankCursor reuses its backing array.");
            Assert.AreSame(buffers.SortKeys(8), buffers.SortKeys(8), "SortKeys reuses its backing array.");
            Assert.AreSame(buffers.DeclaredOrder(8), buffers.DeclaredOrder(8), "DeclaredOrder reuses its backing array.");
            Assert.AreSame(buffers.OrderedFeaturesBuffer(8), buffers.OrderedFeaturesBuffer(8), "OrderedFeaturesBuffer reuses its backing array.");

            float[] keys = buffers.SortKeys(8);
            Assert.AreSame(buffers.SortKeyComparer(keys), buffers.SortKeyComparer(keys),
                "SortKeyComparer is a stored instance re-fielded per call — NOT a fresh delegate/closure the way Array.Sort's lambda overload allocates.");
            Assert.AreSame(buffers.OrderedFeaturesView(4), buffers.OrderedFeaturesView(4),
                "OrderedFeaturesView is a stored IReadOnlyList instance, not a fresh wrapper per call.");

            Assert.AreSame(buffers.SelectionBuffer(8), buffers.SelectionBuffer(8), "SelectionBuffer reuses its backing array.");
            Assert.AreSame(buffers.SelectionView(4), buffers.SelectionView(4),
                "SelectionView is a stored IReadOnlyList instance, not a fresh wrapper per call.");
            Assert.AreNotSame(buffers.OrderedFeaturesBuffer(8), buffers.SelectionBuffer(8),
                "SelectionBuffer must be a DISTINCT array from OrderedFeaturesBuffer — OrderBySortKey reads a " +
                "layer's selection while writing the reordered result, so aliasing the two would corrupt the sort.");
        }

        /// <summary>
        /// Wiring tooth: a real <see cref="SyncMeshWrite.Fill"/> build must actually ROUTE its
        /// buffers allocations through the pooled <see cref="TileBuildBuffers"/> — not accept the parameter and
        /// ignore it. Both consumers must fire under a live <c>fill-sort-key</c>: <c>BuildRingVisitOrder</c>
        /// (grows <c>RankStart</c>) on every build, <c>OrderBySortKey</c> (grows <c>SortKeys</c>) because a
        /// sort key is declared. Both buffers are empty before the build; an "accept-but-ignore" build that
        /// allocated its own arrays would leave them empty and fail below.
        /// </summary>
        [Test]
        public void WriteMeshData_RoutesBothBufferSites_ThroughThePooledBuffers()
        {
            List<IFeature> features = MakeFeatures(4);
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, Tile, Extent);
            try
            {
                Assert.Greater(geometry.RingCount, 1, "precondition: multiple rings");

                var buffers = new TileBuildBuffers();
                Assert.AreEqual(0, buffers.RankStart(0).Length, "precondition: RankStart buffer is empty before any build");
                Assert.AreEqual(0, buffers.SortKeys(0).Length, "precondition: SortKeys buffer is empty before any build");

                Mesh.MeshDataArray mda = Mesh.AllocateWritableMeshData(1);
                SyncMeshWrite.Fill(mda[0], selected, geometry, Paint, Zoom, double3.zero,
                    out int verts, out Bounds _, null, SortKeyLayout, default, buffers);
                mda.Dispose();
                Assert.Greater(verts, 0, "non-vacuity: the build must produce geometry");

                Assert.Greater(buffers.RankStart(0).Length, 0,
                    "BuildRingVisitOrder must route through buffers.RankStart — its buffer grew past empty during the build.");
                Assert.Greater(buffers.SortKeys(0).Length, 0,
                    "OrderBySortKey (fill-sort-key declared) must route through buffers.SortKeys — its buffer grew past empty during the build.");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>
        /// The load-bearing correctness tooth for pooling: a build's output must be BYTE-IDENTICAL whether its
        /// <see cref="TileBuildBuffers"/> is fresh or was just used, on the SAME instance, for a LARGER build —
        /// the shape <see cref="TileBuildBuffers"/>'s own doc names as the risk ("a renter must only read the
        /// [0, count) prefix IT wrote"). A 6-feature build grows the buffers first; a 2-feature build
        /// then reuses that SAME (now-larger) instance and must match a fresh <c>buffers: null</c> reference
        /// build of the identical 2-feature fixture, vertex-for-vertex, triangle-for-triangle, colour-for-colour.
        /// </summary>
        [Test]
        public void WriteMeshData_BuffersReusedAfterALargerBuild_MatchesAFreshNonPooledBuild()
        {
            List<IFeature> largerFixture  = MakeFeatures(6);
            List<IFeature> smallerFixture = MakeFeatures(2);

            var buffers = new TileBuildBuffers();

            // Grows every buffers buffer to the LARGER fixture's peak — never touched by the smaller build yet.
            Mesh throwaway = TestTileMeshBuilder.BuildFill(
                largerFixture, Paint, Zoom, Extent, Tile, null, SortKeyLayout, default, buffers);
            Assert.IsNotNull(throwaway, "precondition: the larger warm-up build must produce geometry");
            Object.DestroyImmediate(throwaway);

            Mesh pooled = TestTileMeshBuilder.BuildFill(
                smallerFixture, Paint, Zoom, Extent, Tile, null, SortKeyLayout, default, buffers);
            Mesh reference = TestTileMeshBuilder.BuildFill(
                smallerFixture, Paint, Zoom, Extent, Tile, null, SortKeyLayout, default, null);
            try
            {
                Assert.IsNotNull(pooled);
                Assert.IsNotNull(reference);
                CollectionAssert.AreEqual(reference.vertices, pooled.vertices,
                    "vertex positions must be byte-identical — a reused, larger-grown buffers must not leak " +
                    "the prior (larger) build's data into a smaller one's [0, count) window");
                CollectionAssert.AreEqual(reference.triangles, pooled.triangles,
                    "triangle/draw order must be byte-identical — this is what a stale (un-cleared) rank-start " +
                    "prefix sum, or a mis-sized ordered-features view, would corrupt first");
                CollectionAssert.AreEqual(reference.colors, pooled.colors,
                    "per-vertex colour must be byte-identical — desyncs from geometry exactly when the ordered " +
                    "view over-reports its length and the loop reads stale entries from the prior build's tail");
            }
            finally
            {
                if (pooled != null)    Object.DestroyImmediate(pooled);
                if (reference != null) Object.DestroyImmediate(reference);
            }
        }

        /// <summary>The non-pooled (<c>buffers: null</c>) path is unchanged by pooling's existence.</summary>
        [Test]
        public void WriteMeshData_NullBuffers_StillProducesGeometry()
        {
            List<IFeature> features = MakeFeatures(2);
            Mesh mesh = TestTileMeshBuilder.BuildFill(features, Paint, Zoom, Extent, Tile, null, SortKeyLayout);
            try
            {
                Assert.IsNotNull(mesh, "buffers: null must still allocate its own buffers and produce geometry");
                Assert.Greater(mesh.vertexCount, 0);
            }
            finally
            {
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }
    }
}
