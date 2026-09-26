// Jobs/GraphDeterminismTests.cs — Burst job-graph teeth: off-main scheduling, decode ownership, sizing, graph determinism, and ring-assembly invariants.
//
// Ordered by pipeline stage: off-main scheduling spike, decode ownership, fill sizing, GeoJSON decode, graph determinism/instrumentation, line sizing, the shared MvtCommandStream test-fixture builder, MVT materialization, ordinal domain, managed-vs-Burst projection parity, then the three ring-assembly fixtures together.
//
// Contents:
//   BurstJobRunOffMainSpikeTests       — (Established as a spike: the geometry kernels take CALLER-provided Persistent scratch — no internal Allocator.Temp — so allocating NativeArrays and .Run()-ing them off the main thread is safe.
//   DecodedLayerGeometryTests          — the decoded layer's two store teeth: a source-layer's geometry belongs to the layer.
//   DecodedTileOwnershipTests          — the ownership relation: a decoded tile cannot be paired with another tile's geometry.
//   FillSizingJobTests                 — job-scheduling-design.md: the offset-table monotonicity assertion.
//   GeoJsonTileDecoderTests            — the GeoJSON decoder half: the source-layer answer, and the sliced layer's ordinal domain.
//   GraphDeterminismTests              — One arm per batch constant, each reading THAT node's OWN constant — a developer restoring one tuned constant to 1<<20 must red exactly that arm, and no other.
//   JobGraphInstrumentTests            — job-scheduling-design.md — the probes the substrate stage is planned against, plus the delay-job instrument E2 settled on (option iii: FillMeshGraph.Schedule's existing deps parameter, not a test-only production hook).
//   LineRibbonSizingJobTests           — Arm A catches a borrowed-count out-of-bounds read in RibbonAggregateJob; Arm B is the parity check and catches only a non-monotonic offset table (a sizing-only arm cannot catch the out-of-bounds read — see FillSizingJobTests.cs's header).
//   MvtCommandStream                   — Test-only helper (not a fixture): authors MVT geometry command streams so a test can hand MvtGeometryMaterializer an exact ring layout.
//   MvtGeometryMaterializerTests       — the teeth on the MVT end of Waist 1's producer seam — what the materializer puts in the buffer, and who owns the buffer afterwards.
//   OrdinalDomainTests                 — the selection/buffer relation (the TileId/buffer one is above).
//   ProjectionManagedVersusBurstTests  — RED-verified by perturbing one arm alone, run and reverted by hand — never left in this file:   (i)  feed one arm Extent + 1.0 (a COARSE injection — a single-ULP TileCoords bump cannot red this probe;        see the file header for the magnitude…
//   RingAssemblyDeferredCountTests     — The load-bearing claim: RingAssemblyJob.RingOffsets, taken as list.AsDeferredJobArray() BEFORE the list is populated, resolves to the list's EXECUTE-time length (after a preceding scheduled job populates it), not its length at the moment…
//   RingAssemblyHoleAttributionTests   — The property the whole byte-identity argument for earcut's hole-bridge order rests on: every hole belongs to the same source-layer feature as its polygon's outer ring.
//   RingAssemblyKindGateTests          — RingAssemblyJob's kind gate: a ring whose feature is not a Polygon is never classified, whatever its area.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Projection;
using MapRenderer.Unity.Rendering.Tile;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using System.IO;
using System.Reflection;
using UnityEngine;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Unity.Burst;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Style;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine.Rendering;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Jobs
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // BurstJobRunOffMainSpikeTests — geometry kernels take caller-provided scratch, safe to run off-main
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class BurstJobRunOffMainSpikeTests
    {
        private static T RunOnWorker<T>(Func<T> body, out int workerThreadId, out Exception workerEx)
        {
            Exception ex = null;
            int tid = -1;
            T result = default;
            var task = UniTask.RunOnThreadPool(() =>
            {
                tid = Thread.CurrentThread.ManagedThreadId;
                try { result = body(); }
                catch (Exception e) { ex = e; }
            }, configureAwait: false).Preserve();

            task.WaitOffPlayerLoop(20000);

            workerThreadId = tid;
            workerEx = ex;
            return result;
        }

        [Test]
        public void ProjectJob_RunOnThreadPoolWorker_ProducesSameOutputAsMainThread()
        {
            int mainTid = Thread.CurrentThread.ManagedThreadId;

            // Reference: run on the main thread.
            var refOut = new NativeArray<double3>(3, Allocator.Persistent);
            var refUp  = new NativeArray<double3>(3, Allocator.Persistent);
            try
            {
                var pts = new NativeArray<GeoCoordinate>(3, Allocator.Persistent);
                pts[0] = new GeoCoordinate { Latitude = 0,   Longitude = 0 };
                pts[1] = new GeoCoordinate { Latitude = 45,  Longitude = 30 };
                pts[2] = new GeoCoordinate { Latitude = -60, Longitude = 179 };
                new ProjectPointsJob<WebMercatorProjection>
                {
                    Projection = new WebMercatorProjection(),
                    OriginWorld = new double3(0.0, 0.0, 0.0),
                    Points = pts, WorldPositions = refOut, Normals = refUp,
                }.Run(3);
                pts.Dispose();

                var workerOut = RunOnWorker(() =>
                {
                    var outArr = new NativeArray<double3>(3, Allocator.Persistent);
                    var outUp  = new NativeArray<double3>(3, Allocator.Persistent);
                    var wpts = new NativeArray<GeoCoordinate>(3, Allocator.Persistent);
                    wpts[0] = new GeoCoordinate { Latitude = 0,   Longitude = 0 };
                    wpts[1] = new GeoCoordinate { Latitude = 45,  Longitude = 30 };
                    wpts[2] = new GeoCoordinate { Latitude = -60, Longitude = 179 };
                    new ProjectPointsJob<WebMercatorProjection>
                    {
                        Projection = new WebMercatorProjection(),
                        OriginWorld = new double3(0.0, 0.0, 0.0),
                        Points = wpts, WorldPositions = outArr, Normals = outUp,
                    }.Run(3);
                    wpts.Dispose();
                    outUp.Dispose();
                    return outArr;
                }, out int workerTid, out Exception ex);

                Assert.IsNull(ex, $"ProjectPointsJob<WebMercatorProjection>.Run() THREW off a RunOnThreadPool worker: {ex}");
                Assert.AreNotEqual(mainTid, workerTid, "spike must actually run off the main thread");

                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.AreEqual(refOut[i].x, workerOut[i].x, 1e-9f, $"v[{i}].x off-main == main");
                        Assert.AreEqual(refOut[i].y, workerOut[i].y, 1e-9f, $"v[{i}].y off-main == main");
                        Assert.AreEqual(refOut[i].z, workerOut[i].z, 1e-9f, $"v[{i}].z off-main == main");
                    }
                }
                finally { workerOut.Dispose(); }
            }
            finally { refOut.Dispose(); refUp.Dispose(); }
        }

        [Test]
        public void EarcutJob_RunOnThreadPoolWorker_TriangulatesSquare()
        {
            int mainTid = Thread.CurrentThread.ManagedThreadId;

            var (idxCount, workerTid, ex) = RunOnWorkerEarcut();

            Assert.IsNull(ex, $"EarcutJob.Run() THREW off a RunOnThreadPool worker: {ex}");
            Assert.AreNotEqual(mainTid, workerTid, "spike must actually run off the main thread");
            Assert.AreEqual(6, idxCount, "a CCW square triangulates to 2 triangles (6 indices) off-main");
        }

        [Test]
        public void RibbonJob_RunOnThreadPoolWorker_UsesInternalTempSafely()
        {
            // RibbonJob allocates Allocator.Temp INTERNALLY, unlike the fill kernels; this proves Temp works on
            // a raw threadpool thread, as the off-main line path needs.
            int mainTid = Thread.CurrentThread.ManagedThreadId;

            int vc = -1, ic = -1;
            var result = RunOnWorker(() =>
            {
                var pts   = new NativeArray<double3>(3, Allocator.Persistent);
                var ups   = new NativeArray<double3>(3, Allocator.Persistent);
                var outV  = new NativeArray<LineRibbonVertex>(RibbonJob.MaxVertexCount(3, 4), Allocator.Persistent);
                var outI  = new NativeArray<int>(RibbonJob.MaxIndexCount(3, 4), Allocator.Persistent);
                var vcArr = new NativeArray<int>(1, Allocator.Persistent);
                var icArr = new NativeArray<int>(1, Allocator.Persistent);
                try
                {
                    pts[0] = new double3(0, 0, 0);
                    pts[1] = new double3(10, 0, 0);
                    pts[2] = new double3(20, 0, 5);
                    for (int i = 0; i < 3; i++) ups[i] = new double3(0, 1, 0);
                    new RibbonJob
                    {
                        Points = pts, Ups = ups, PointCount = 3,
                        Join = JoinType.Miter, Cap = CapType.Butt, MiterLimit = 2.0, RoundSegments = 4,
                        OutVertices = outV, OutIndices = outI,
                        OutVertexCount = vcArr, OutIndexCount = icArr,
                    }.Run();
                    return (vcArr[0], icArr[0]);
                }
                finally
                {
                    pts.Dispose(); ups.Dispose(); outV.Dispose(); outI.Dispose(); vcArr.Dispose(); icArr.Dispose();
                }
            }, out int workerTid, out Exception ex);

            vc = result.Item1; ic = result.Item2;
            Assert.IsNull(ex, $"RibbonJob.Run() THREW off a worker (Allocator.Temp off-thread?): {ex}");
            Assert.AreNotEqual(mainTid, workerTid, "spike must actually run off the main thread");
            Assert.Greater(vc, 0, "3-point polyline must produce line vertices off-main");
            Assert.Greater(ic, 0, "3-point polyline must produce indices off-main");
        }

        private static (int idxCount, int workerTid, Exception ex) RunOnWorkerEarcut()
        {
            int idxCount = -1;
            var result = RunOnWorker(() =>
            {
                // CCW unit square, no holes: polyVC=4 → workCap=4, idxCap=6.
                var verts   = new NativeArray<double2>(4, Allocator.Persistent);
                var sortedHoleCounts = new NativeArray<int>(1, Allocator.Persistent);
                var outIdx  = new NativeArray<int>(6, Allocator.Persistent);
                var outIndexCount  = new NativeArray<int>(1, Allocator.Persistent);
                var force   = new NativeArray<int>(1, Allocator.Persistent);
                var mergedVC = new NativeArray<int>(1, Allocator.Persistent);
                var candidateVisits = new NativeArray<long>(1, Allocator.Persistent);
                var v = new NativeArray<double2>(4, Allocator.Persistent);
                var prev = new NativeArray<int>(4, Allocator.Persistent);
                var next = new NativeArray<int>(4, Allocator.Persistent);
                var isBridge = new NativeArray<bool>(4, Allocator.Persistent);
                var removed  = new NativeArray<bool>(4, Allocator.Persistent);
                var isEar    = new NativeArray<bool>(4, Allocator.Persistent);
                try
                {
                    verts[0] = new double2(0, 0);
                    verts[1] = new double2(1, 0);
                    verts[2] = new double2(1, 1);
                    verts[3] = new double2(0, 1);

                    new EarcutJob
                    {
                        PolyVertices = verts, OuterCount = 4,
                        SortedHoleCounts = sortedHoleCounts, HoleCount = 0,
                        OutIndices = outIdx, OutIndexOffset = 0,
                        OutIndexCount = outIndexCount, OutForceClipCount = force,
                        OutMergedVertexCount = mergedVC, OutCandidateVisits = candidateVisits,
                        Verts = v, Prev = prev, Next = next,
                        IsBridgeCopy = isBridge, Removed = removed, IsEar = isEar,
                    }.Run();

                    return outIndexCount[0];
                }
                finally
                {
                    verts.Dispose(); sortedHoleCounts.Dispose(); outIdx.Dispose(); outIndexCount.Dispose(); force.Dispose();
                    mergedVC.Dispose(); candidateVisits.Dispose();
                    v.Dispose(); prev.Dispose(); next.Dispose();
                    isBridge.Dispose(); removed.Dispose(); isEar.Dispose();
                }
            }, out int workerTid, out Exception ex);

            idxCount = result;
            return (idxCount, workerTid, ex);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DecodedLayerGeometryTests — the two store teeth, rehomed to the decoded layer
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A source-layer's geometry is materialized <b>once</b> and consumers only <b>borrow</b> it, so it can
    /// be read twice and stays intact. The decoded layer holds the one buffer and the tile's Dispose frees
    /// it. The first test catches a <c>Geometry</c> getter that re-materializes on each read.
    /// </summary>
    [TestFixture]
    public class DecodedLayerGeometryTests
    {
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        private static readonly TileId SampleTile = new TileId { Z = 8, X = 135, Y = 80 };
        private const double SampleExtent = 4096.0;

        // ── the memo ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Two reads of the SAME <see cref="ITileLayer.Geometry"/> hand back the same allocation; two layers of
        /// one tile do not share one. <c>NativeArray&lt;T&gt;</c> equality is pointer + length, so this is an
        /// identity test: a getter that re-materializes identical contents fails it and passes every output test.
        /// </summary>
        [Test]
        public void LayerGeometry_IsOneBufferPerSourceLayer_ByReference()
        {
            InMemoryDecodedTile tile = TestDecodedTiles.OfLayers(
                Layer("water", Square(10)), Layer("roads", Square(20)), Layer("empty"));

            TileGeometryBuffers a1 = tile.GetLayer("water").Geometry;
            TileGeometryBuffers a2 = tile.GetLayer("water").Geometry;
            TileGeometryBuffers b1 = tile.GetLayer("roads").Geometry;

            // Non-vacuity: both really decoded, so the comparisons below are between live allocations
            // and not between two `default`s.
            Assert.IsTrue(a1.IsCreated, "precondition: the first layer materialized");
            Assert.IsTrue(b1.IsCreated, "precondition: the second layer materialized");
            Assert.AreEqual(4, a1.VertexCount, "precondition: the first layer's square really decoded");
            Assert.AreEqual(4, b1.VertexCount, "precondition: the second layer's square really decoded");

            Assert.AreEqual(a1.Vertices, a2.Vertices,
                "the SECOND read of one layer's Geometry must return the SAME allocation — NativeArray " +
                "equality is pointer+length, so this fails for a re-materialization even though its contents " +
                "would be identical. This is the whole point: N style layers naming one source-layer decode " +
                "it once, across both cadences of a kick.");
            Assert.AreNotEqual(a1.Vertices, b1.Vertices,
                "…and two DIFFERENT source layers must not share a buffer");

            // A layer with nothing in it allocates nothing — the same empty result every consumer handles.
            Assert.IsFalse(tile.GetLayer("empty").Geometry.IsCreated,
                "a feature-less layer must materialize to default, not to an allocation");
            Assert.IsNull(tile.GetLayer("absent"), "an unresolved source-layer name must resolve to null");
        }

        /// <summary>The TILE frees what its layers lent. A borrower that had disposed its loan would
        /// double-free here — and on the array-backed buffer a consumer borrows that is a LOUD double free,
        /// not a silent no-op, so the contract is stated as "the tile's Dispose is the one that works".</summary>
        [Test]
        public void TileDispose_FreesEveryLayersBuffer()
        {
            var tile = new InMemoryDecodedTile(Layer("water", Square(10)));
            TileGeometryBuffers lent = tile.GetLayer("water").Geometry;

            Assert.IsTrue(lent.IsCreated, "precondition: the layer holds a live buffer");
            Assert.DoesNotThrow(() => { var _ = lent.Vertices[0]; },
                "precondition: the borrowed buffer is readable while the tile is alive");

            tile.Dispose();

            Assert.That(() => { var _ = lent.Vertices[0]; }, Throws.Exception,
                "the tile's Dispose must free every layer's buffer — which is also why a borrower must " +
                "never retain one past the decode scope");
            Assert.DoesNotThrow(() => tile.Dispose(), "a second Dispose must be a no-op, not a double free");
        }

        // ── borrow, not transfer ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>FillMeshGraph.Schedule</c> may run <b>twice over the same buffer</b>, as several fill layers of one
        /// source-layer do. The second run produces output, bit-identical to a run over a FRESH buffer, and the
        /// shared buffer's four arrays keep their contents. It catches an input <c>Dispose()</c>, an
        /// <c>AdoptDerivedLists</c> that moves the kind column, and a clip that writes back into the input.
        /// </summary>
        [Test]
        public void Schedule_TwiceOverOneBorrowedBuffer_IsIdenticalAndLeavesItIntact()
        {
            InMemoryDecodedTile tile = TestDecodedTiles.OfLayers(Layer("water", Square(100)));

            TileGeometryBuffers shared = tile.GetLayer("water").Geometry;
            Assert.IsTrue(shared.IsCreated, "precondition: the layer holds a live buffer");

            // Snapshot the shared buffer's contents so "unchanged afterwards" is a content claim, not just
            // an IsCreated claim.
            var beforeVerts  = new double2[shared.VertexCount];
            var beforeOffs   = new int[shared.RingCount + 1];
            var beforeFeat   = new int[shared.RingCount];
            var beforeKinds  = new TileGeometryType[shared.FeatureCount];
            for (int i = 0; i < beforeVerts.Length; i++) beforeVerts[i] = shared.Vertices[i];
            for (int i = 0; i < beforeOffs.Length;  i++) beforeOffs[i]  = shared.RingOffsets[i];
            for (int i = 0; i < beforeFeat.Length;  i++) beforeFeat[i]  = shared.RingFeatureIdx[i];
            for (int i = 0; i < beforeKinds.Length; i++) beforeKinds[i] = shared.FeatureGeometryType[i];

            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(shared);

            FillGraphOutput unclipped = Run(shared, visitOrder, TileBufferClip.Disabled);
            FillGraphOutput clipped   = Run(shared, visitOrder, TileBufferClip.KeepTileUnits(0.0));

            // The control arm: the SAME clipped run over a buffer nothing has touched.
            TileGeometryBuffers fresh = TestTileMeshBuilder.Materialize(
                new List<IFeature> { Square(100) }, SampleTile, SampleExtent);
            NativeArray<int> freshOrder = TestTileMeshBuilder.FullVisitOrder(fresh);
            FillGraphOutput control = Run(fresh, freshOrder, TileBufferClip.KeepTileUnits(0.0));

            try
            {
                // (a)
                Assert.IsTrue(unclipped.IsCreated, "the FIRST run over the borrowed buffer produced no mesh");
                Assert.IsTrue(clipped.IsCreated,
                    "the SECOND run over the SAME buffer produced no mesh — the first run consumed its input");
                Assert.Greater(unclipped.TileVertices.Length, 0, "precondition: there is geometry to compare");

                // (b)
                Assert.AreEqual(control.TileVertices.Length, clipped.TileVertices.Length,
                    "the second run over a REUSED buffer must match the same run over a fresh one");
                Assert.AreEqual(control.TriangleIndices.Length, clipped.TriangleIndices.Length, "…index counts too");
                for (int i = 0; i < control.TileVertices.Length; i++)
                    Assert.AreEqual(control.TileVertices[i], clipped.TileVertices[i], $"TileVertices[{i}]");
                for (int i = 0; i < control.TriangleIndices.Length; i++)
                    Assert.AreEqual(control.TriangleIndices[i], clipped.TriangleIndices[i], $"TriangleIndices[{i}]");

                // (c) — the borrowed buffer is byte-for-byte what it was.
                Assert.IsTrue(shared.Vertices.IsCreated,            "the borrowed Vertices must survive");
                Assert.IsTrue(shared.RingOffsets.IsCreated,         "the borrowed RingOffsets must survive");
                Assert.IsTrue(shared.RingFeatureIdx.IsCreated,      "the borrowed RingFeatureIdx must survive");
                Assert.IsTrue(shared.FeatureGeometryType.IsCreated,
                    "the borrowed kind column must survive — an AdoptDerivedLists that TOOK it instead of " +
                    "copying it would have moved it into the first derived buffer and freed it there");
                for (int i = 0; i < beforeVerts.Length; i++)
                    Assert.AreEqual(beforeVerts[i], shared.Vertices[i], $"borrowed Vertices[{i}] was mutated");
                for (int i = 0; i < beforeOffs.Length; i++)
                    Assert.AreEqual(beforeOffs[i], shared.RingOffsets[i], $"borrowed RingOffsets[{i}] was mutated");
                for (int i = 0; i < beforeFeat.Length; i++)
                    Assert.AreEqual(beforeFeat[i], shared.RingFeatureIdx[i], $"borrowed RingFeatureIdx[{i}] was mutated");
                for (int i = 0; i < beforeKinds.Length; i++)
                    Assert.AreEqual(beforeKinds[i], shared.FeatureGeometryType[i], $"borrowed kind[{i}] was mutated");
            }
            finally
            {
                unclipped.Dispose();
                clipped.Dispose();
                control.Dispose();
                visitOrder.Dispose();
                freshOrder.Dispose();
                fresh.Dispose();
            }
        }

        // ── Fixture ───────────────────────────────────────────────────────────────────────────────

        private static FillGraphOutput Run(
            TileGeometryBuffers geometry, NativeArray<int> visitOrder, TileBufferClip clip)
        {
            var (bMin, _) = SampleTile.MercatorBounds();
            FillGraphOutput output = FillMeshGraph.Schedule(new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                Clip           = clip,
            });
            output.Handle.Complete();
            return output;
        }

        /// <summary>One axis-aligned square polygon feature, well inside the tile.</summary>
        private static IFeature Square(int origin) => new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false, geometry: MvtCommandStream.Feature(MvtCommandStream.Ring(
                origin, origin, origin + 300, origin, origin + 300, origin + 300, origin, origin + 300)));

        private static InMemoryTileLayer Layer(string name, params IFeature[] features)
            => new InMemoryTileLayer(name, SampleTile, features, (uint)SampleExtent);
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DecodedTileOwnershipTests — a decoded tile cannot be paired with another tile's geometry
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A decoded tile cannot be paired with another tile's geometry. Structurally, no production method takes
    /// a <c>TileGeometryBuffers</c> and a <c>TileId</c> together, the mispairing shape. Behaviourally, each
    /// buffer carries the address its own decode was given. Non-local invariant: the id enters the pipeline
    /// once, through <c>ITileDecoder.Decode(TileId, byte[])</c>.
    /// </summary>
    [TestFixture]
    public class DecodedTileOwnershipTests
    {
        // ── B1 — structural: the mispairing shape does not exist in any signature ──────────────────────

        [Test]
        public void NoProductionSignature_TakesBothATileGeometryBuffersAndATileId()
        {
            Assembly[] production =
            {
                typeof(TileGeometryBuffers).Assembly,   // MapRenderer.Jobs
                typeof(ITileFeatureSource).Assembly,    // MapRenderer.Unity
            };

            var offenders   = new List<string>();
            int bufferSites = 0;

            foreach (Assembly assembly in production)
                foreach (Type t in assembly.GetTypes())
                    foreach (MethodBase m in Methods(t))
                    {
                        bool hasBuffer = false, hasTileId = false;
                        foreach (ParameterInfo p in m.GetParameters())
                        {
                            Type pt = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                            if (pt == typeof(TileGeometryBuffers)) hasBuffer = true;
                            if (pt == typeof(TileId))              hasTileId = true;
                        }
                        if (hasBuffer) bufferSites++;
                        if (hasBuffer && hasTileId) offenders.Add($"{t.FullName}.{m.Name}");
                    }

            // Non-vacuity: the scan really sees TileGeometryBuffers in real signatures. Without this a
            // reflection load that returned nothing would report "no offenders" identically.
            Assert.GreaterOrEqual(bufferSites, 3,
                "precondition: TileGeometryBuffers must appear in at least 3 production method signatures, " +
                "or this scan is looking at nothing");

            Assert.IsEmpty(offenders,
                "no production method may take BOTH a TileGeometryBuffers and a TileId. That parameter pair " +
                "is the mispairing shape itself: it lets a caller hand one tile's geometry an unrelated " +
                "tile's address. The address is read off the buffer (`geometry.Tile`), which " +
                "the decoder stamped from the id the fetch already had. " +
                $"Offenders: {string.Join(", ", offenders)}");

            // …and the positive half: the layer is where geometry lives, so there is a legitimate place to
            // read it from. Without this clause the negative above is satisfiable by having no geometry at all.
            PropertyInfo geometry = typeof(ITileLayer).GetProperty(nameof(ITileLayer.Geometry));
            Assert.IsNotNull(geometry, "ITileLayer must expose Geometry");
            Assert.AreEqual(typeof(TileGeometryBuffers), geometry.PropertyType);
        }

        // ── B2 — behavioural, at the decode seam ──────────────────────────────────────────────────────

        /// <summary>
        /// Two DIFFERENT real multi-layer fixtures, decoded at ids differing in <b>z, x and y</b>: every layer's
        /// buffer carries its own decode's address. Multi-layer, because <c>DecodeLayer</c> stamps per layer. It
        /// catches a stamp of <c>default(TileId)</c>, a constant, the previous decode's id, or only one of
        /// z/x/y.
        /// </summary>
        [Test]
        public void EveryLayersBuffer_CarriesTheAddressItsOwnDecodeWasGiven()
        {
            var a = new TileId { Z = 6, X = 32,  Y = 20  };
            var b = new TileId { Z = 9, X = 274, Y = 168 };
            Assert.AreNotEqual(a.Z, b.Z, "precondition: the two ids must differ in z");
            Assert.AreNotEqual(a.X, b.X, "precondition: …and in x");
            Assert.AreNotEqual(a.Y, b.Y, "precondition: …and in y — a z-only pair cannot see a z-only decoder");

            using MvtTile first  = MvtDecoder.Decode(a, Fixture("water-6-32-20.pbf.bytes"));
            using MvtTile second = MvtDecoder.Decode(b, Fixture("boundary-9-274-168.pbf.bytes"));

            int checkedLayers = AssertEveryLayerStamped(first, a, "water-6-32-20");
            checkedLayers    += AssertEveryLayerStamped(second, b, "boundary-9-274-168");

            // Non-vacuity: real multi-layer tiles with real geometry, or the loop above ran over nothing.
            Assert.Greater(first.Layers.Count, 1,
                "precondition: the first fixture must be MULTI-layer — a one-layer tile cannot see a decoder " +
                "that stamps only the first layer");
            Assert.Greater(checkedLayers, 2,
                "precondition: at least three layers across the two tiles must actually carry a buffer");

            // …and the two tiles' stamps really differ, so "equals its own id" is not satisfiable by a
            // constant that happens to be both.
            Assert.AreNotEqual(
                first.Layers[0].Geometry.Tile, second.Layers[0].Geometry.Tile,
                "the two decodes must produce DIFFERENT tile addresses — equal ones would make the " +
                "per-decode assertions above pass for a constant stamper");
        }

        private static int AssertEveryLayerStamped(MvtTile tile, TileId expected, string fixtureName)
        {
            int stamped = 0;
            foreach (MvtLayer layer in tile.Layers)
            {
                if (!layer.Geometry.IsCreated) continue; // a feature-less layer allocates nothing
                stamped++;
                Assert.AreEqual(expected, layer.Geometry.Tile,
                    $"{fixtureName}/{layer.Name}: the buffer's Tile must be the id THIS decode was given. " +
                    "It is the only copy of the address below the fetch, so a wrong one silently places the " +
                    "layer's whole geometry on another tile.");
                Assert.AreEqual((double)layer.Extent, layer.Geometry.Extent,
                    $"{fixtureName}/{layer.Name}: the buffer's Extent must be the LAYER's own extent — the " +
                    "quantization range its coordinates are expressed in travels with them or not at all.");
            }
            return stamped;
        }

        /// <summary>
        /// The "one buffer per source-layer" claim of
        /// <c>DecodedLayerGeometryTests.LayerGeometry_IsOneBufferPerSourceLayer_ByReference</c>, over a REAL
        /// decoded <see cref="MvtLayer"/>. The sibling runs over the <c>InMemoryTileLayer</c> double, so it
        /// stays green when <c>MvtLayer</c>'s getter re-materializes.
        /// </summary>
        [Test]
        public void ARealDecodedLayer_HandsBackTheSameAllocationOnEveryRead()
        {
            var id = new TileId { Z = 6, X = 32, Y = 20 };
            using MvtTile tile = MvtDecoder.Decode(id, Fixture("water-6-32-20.pbf.bytes"));

            int compared = 0;
            foreach (MvtLayer layer in tile.Layers)
            {
                ITileLayer neutral = layer; // read through the INTERFACE — the surface every consumer uses
                TileGeometryBuffers first  = neutral.Geometry;
                TileGeometryBuffers second = neutral.Geometry;
                if (!first.IsCreated) continue;
                compared++;

                // NativeArray<T>.Equals is pointer + length: an IDENTITY check that a re-materialized copy
                // with equal contents fails.
                Assert.IsTrue(first.Vertices.Equals(second.Vertices),
                    $"layer '{layer.Name}': two reads of ITileLayer.Geometry must return the SAME " +
                    "allocation. A getter that materializes afresh per read is the 108-materializations-" +
                    "per-tile shape wearing the layer's name — and it is output-neutral.");
                Assert.IsTrue(first.RingOffsets.Equals(second.RingOffsets), $"layer '{layer.Name}': RingOffsets");
                Assert.IsTrue(first.RingFeatureIdx.Equals(second.RingFeatureIdx), $"layer '{layer.Name}': RingFeatureIdx");
            }

            Assert.Greater(compared, 1,
                "precondition: at least two real layers must carry a buffer, or this compared nothing");
        }

        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }

        private static IEnumerable<MethodBase> Methods(Type t)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodInfo m in t.GetMethods(All)) yield return m;
            foreach (ConstructorInfo c in t.GetConstructors(All)) yield return c;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillSizingJobTests — the offset-table monotonicity assertion
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillSizingJobTests
    {
        [Test]
        public void MaxPolygonsSmallerThanHandedPolyCount_SetsErrorFlag_AndWritesNothing()
        {
            var polyOuterIdx  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyHoleStart = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyHoleCount = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeRingIdxs  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            polyCountArr[0] = 2; // handed 2 polygons...
            var holeCountArr = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var ringOffsets  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory); // never read on the error path

            TriangulationBuffers buffers = TriangulationBuffers.Allocate();
            var counts = new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var error = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new SizingJob
                {
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs, PolyCountArr = polyCountArr, HoleCountArr = holeCountArr,
                    RingOffsets = ringOffsets,
                    MaxPolygons = 1, // smaller than the handed polygon count (2)
                    MaxHoles = 10,
                    Buffers = buffers,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.ErrorPolygonCapacity, error.Value,
                    "a polygon count exceeding MaxPolygons must set ErrorPolygonCapacity");

                Assert.AreEqual(0, buffers.VertexOffsets.Length, "VertexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.HoleCountOffsets.Length, "HoleCountOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.WorkOffsets.Length, "WorkOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.IndexOffsets.Length, "IndexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.FlatPolyVerts.Length);
                Assert.AreEqual(0, buffers.FlatSortedHoleCounts.Length);
                Assert.AreEqual(0, buffers.FlatIndexArrays.Length);
                Assert.AreEqual(0, buffers.FlatWorkVerts.Length);
                Assert.AreEqual(0, buffers.FlatPreviousIndex.Length);
                Assert.AreEqual(0, buffers.FlatNextIndex.Length);
                Assert.AreEqual(0, buffers.FlatIsBridge.Length);
                Assert.AreEqual(0, buffers.FlatRemoved.Length);
                Assert.AreEqual(0, buffers.FlatIsEar.Length);
                Assert.AreEqual(0, buffers.PerPolyIndexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyForceClip.Length);
                Assert.AreEqual(0, buffers.PerPolyMergedVertexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyFeatureIndex.Length);
                Assert.AreEqual(0, buffers.PerPolyOuterCount.Length);
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose(); ringOffsets.Dispose();
                buffers.DisposeAfter(default(JobHandle)).Complete();
                counts.Dispose(); error.Dispose();
            }
        }

        /// <summary>Two real no-hole polygons within capacity produce four strictly increasing offset tables and
        /// <see cref="FillGraphCounts.Ok"/>. A <c>SizingJob</c> that gives polygon 1 a zero-length slice flips
        /// it to <see cref="FillGraphCounts.ErrorOffsetTableNotDisjoint"/>.</summary>
        [Test]
        public void TwoValidPolygons_ProduceStrictlyIncreasingOffsetTables_NoErrorFlag()
        {
            // Two single-ring, no-hole polygons: ring 0 = [0,3), ring 1 = [3,6).
            var polyOuterIdx  = new NativeArray<int>(new[] { 0, 1 }, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyCountArr  = new NativeArray<int>(new[] { 2 }, Allocator.Persistent);
            var holeCountArr  = new NativeArray<int>(new[] { 0 }, Allocator.Persistent);
            var ringOffsets   = new NativeArray<int>(new[] { 0, 3, 6 }, Allocator.Persistent);

            TriangulationBuffers buffers = TriangulationBuffers.Allocate();
            var counts = new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var error = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new SizingJob
                {
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs, PolyCountArr = polyCountArr, HoleCountArr = holeCountArr,
                    RingOffsets = ringOffsets,
                    MaxPolygons = 2, MaxHoles = 1,
                    Buffers = buffers,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.Ok, error.Value,
                    "two real, in-capacity polygons must produce strictly increasing offset tables");

                int[] vertexOffsets = buffers.VertexOffsets.AsArray().ToArray();
                int[] workOffsets   = buffers.WorkOffsets.AsArray().ToArray();
                for (int pi = 0; pi < 2; pi++)
                {
                    Assert.Greater(vertexOffsets[pi + 1], vertexOffsets[pi], $"VertexOffsets[{pi}] must be strictly increasing");
                    Assert.Greater(workOffsets[pi + 1], workOffsets[pi], $"WorkOffsets[{pi}] must be strictly increasing");
                }
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose(); ringOffsets.Dispose();
                buffers.DisposeAfter(default(JobHandle)).Complete();
                counts.Dispose(); error.Dispose();
            }
        }

        /// <summary>After <see cref="SizingJob"/>'s capacity early return, the real
        /// <see cref="FillGatherJob{TComparer}"/> and <see cref="AggregateJob"/> run IN GRAPH ORDER over the SAME
        /// buffers and find nothing to do. Non-local invariant: each downstream node must bound its loop by a
        /// sizing-owned column, which the early return leaves empty. Real descriptors rule out a bystander fault.
        /// </summary>
        /// <remarks>
        /// Limitation: an out-of-bounds gather write goes RED only through the Burst bounds check (see
        /// <see cref="BurstSafetyChecks_AreEnabled"/>); the state assertions pin the contract.
        /// </remarks>
        [Test]
        public void SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Fail("ENABLE_UNITY_COLLECTIONS_CHECKS is not defined in this test assembly build — " +
                "without it, neither FillGatherJob's nor AggregateJob's managed-IL bounds check can fire, " +
                "and this tooth cannot detect the release-build OOB it exists to catch.");
#endif
            // Two real polygons over a MaxPolygons too small for them: the early return leaves every Buffers
            // column at length 0 while the descriptors still report polyCount==2.
            var polyOuterIdx   = new NativeArray<int>(new[] { 0, 1 }, Allocator.Persistent);
            var polyHoleStart  = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var polyHoleCount  = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var holeRingIdxs   = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyCountArr   = new NativeArray<int>(new[] { 2 }, Allocator.Persistent);
            var holeCountArr   = new NativeArray<int>(new[] { 0 }, Allocator.Persistent);
            var ringOffsets    = new NativeArray<int>(new[] { 0, 3, 6 }, Allocator.Persistent);
            var vertices       = new NativeArray<double2>(6, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var ringFeatureIdx = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);

            TriangulationBuffers buffers = TriangulationBuffers.Allocate();
            var counts = new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var error = new NativeReference<int>(Allocator.Persistent);

            var tileVertices     = new NativeList<double2>(Allocator.Persistent);
            var worldPositions   = new NativeList<double3>(Allocator.Persistent);
            var vertexUp         = new NativeList<double3>(Allocator.Persistent);
            var vertexEast       = new NativeList<double3>(Allocator.Persistent);
            var vertexBand       = new NativeList<float3>(Allocator.Persistent);
            var vertexFeatureIdx = new NativeList<int>(Allocator.Persistent);
            var triangleIndices  = new NativeList<int>(Allocator.Persistent);
            var geo              = new NativeList<GeoCoordinate>(Allocator.Persistent);

            try
            {
                new SizingJob
                {
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs, PolyCountArr = polyCountArr, HoleCountArr = holeCountArr,
                    RingOffsets = ringOffsets,
                    MaxPolygons = 1, // smaller than the handed polygon count (2)
                    MaxHoles = 10,
                    Buffers = buffers,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.ErrorPolygonCapacity, error.Value,
                    "precondition: sizing's own capacity check must fire so the downstream nodes below see a " +
                    "genuinely post-early-return buffers state");

                var comparer = new FillMeshPipeline.HoleRingComparer(vertices, ringOffsets);
                new FillGatherJob<FillMeshPipeline.HoleRingComparer>
                {
                    Vertices = vertices, RingOffsets = ringOffsets, RingFeatureIdx = ringFeatureIdx,
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs,
                    Comparer = comparer,
                    Buffers = buffers,
                }.Run();

                new AggregateJob
                {
                    Buffers = buffers,
                    TileVertices = tileVertices, WorldPositions = worldPositions, VertexUp = vertexUp, VertexEast = vertexEast,
                    VertexBand = vertexBand,
                    VertexFeatureIdx = vertexFeatureIdx, TriangleIndices = triangleIndices, Geo = geo,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.ErrorPolygonCapacity, error.Value,
                    "sizing's own verdict must survive both downstream nodes untouched");

                Assert.AreEqual(0, buffers.VertexOffsets.Length, "VertexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.HoleCountOffsets.Length, "HoleCountOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.WorkOffsets.Length, "WorkOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.IndexOffsets.Length, "IndexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.FlatPolyVerts.Length);
                Assert.AreEqual(0, buffers.FlatSortedHoleCounts.Length);
                Assert.AreEqual(0, buffers.FlatIndexArrays.Length);
                Assert.AreEqual(0, buffers.FlatWorkVerts.Length);
                Assert.AreEqual(0, buffers.FlatPreviousIndex.Length);
                Assert.AreEqual(0, buffers.FlatNextIndex.Length);
                Assert.AreEqual(0, buffers.FlatIsBridge.Length);
                Assert.AreEqual(0, buffers.FlatRemoved.Length);
                Assert.AreEqual(0, buffers.FlatIsEar.Length);
                Assert.AreEqual(0, buffers.PerPolyIndexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyForceClip.Length);
                Assert.AreEqual(0, buffers.PerPolyMergedVertexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyFeatureIndex.Length);
                Assert.AreEqual(0, buffers.PerPolyOuterCount.Length);

                Assert.AreEqual(0, tileVertices.Length, "AggregateJob must produce no vertices");
                Assert.AreEqual(0, worldPositions.Length);
                Assert.AreEqual(0, vertexUp.Length);
                Assert.AreEqual(0, vertexEast.Length);
                Assert.AreEqual(0, vertexBand.Length);
                Assert.AreEqual(0, vertexFeatureIdx.Length);
                Assert.AreEqual(0, triangleIndices.Length, "AggregateJob must produce no indices");
                Assert.AreEqual(0, geo.Length);

                // Non-vacuity witness: the borrowed count sizing read is still 2 — without this the test would
                // pass identically on an input where there was nothing to iterate in the first place.
                Assert.AreEqual(2, polyCountArr[0]);
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose(); ringOffsets.Dispose();
                vertices.Dispose(); ringFeatureIdx.Dispose();
                buffers.DisposeAfter(default(JobHandle)).Complete();
                counts.Dispose(); error.Dispose();
                tileVertices.Dispose(); worldPositions.Dispose(); vertexUp.Dispose(); vertexEast.Dispose(); vertexBand.Dispose();
                vertexFeatureIdx.Dispose(); triangleIndices.Dispose(); geo.Dispose();
            }
        }

        /// <summary>Burst safety checks are on.
        /// <see cref="SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo"/>
        /// goes RED only through a bounds exception from <see cref="FillGatherJob{TComparer}"/>'s Burst body, whose
        /// safety setting is independent of <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>; with it off, that test is
        /// vacuous.</summary>
        [Test]
        public void BurstSafetyChecks_AreEnabled()
        {
            Assert.IsTrue(BurstCompiler.Options.EnableBurstSafetyChecks,
                "Jobs ▸ Burst ▸ Safety Checks is OFF — FillGatherJob's compiled body no longer raises the " +
                "bounds exception SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo relies on to " +
                "detect the release-build OOB, so that tooth passes vacuously while this reads false.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GeoJsonTileDecoderTests — the GeoJSON decoder half: source-layer answer + ordinal domain
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The GeoJSON decoder: a geojson tile answers with its sole layer whatever <c>source-layer</c> says, an
    /// MVT tile does not, and the sliced layer's ordinals (one per feature) ADDRESS the buffer. The MVT arm
    /// needs a hand-encoded layer with an ABSENT <c>name</c>: it decodes to null, which a style layer with no
    /// <c>source-layer</c> would match, while no real fixture layer is named null or "".
    /// </summary>
    [TestFixture]
    public class GeoJsonTileDecoderTests
    {
        private static readonly TileId WorldTile = new TileId { Z = 0, X = 0, Y = 0 };
        private static readonly TileId MvtTileId = new TileId { Z = 4, X = 3, Y = 6 };

        // Two disjoint rectangles in tile-local units at the default extent, WEST first. The gap between them
        // is what makes "which ring is filed under which ordinal" answerable from the coordinates alone.
        private const double WestMin = 512.0,  WestMax = 1536.0;
        private const double EastMin = 2560.0, EastMax = 3584.0;

        // The tile the clipping fixture slices, and the two z1 neighbours that hold the features it discards.
        private static readonly TileId SliceTile      = new TileId { Z = 1, X = 0, Y = 0 };
        private static readonly TileId EastNeighbour  = new TileId { Z = 1, X = 1, Y = 0 };
        private static readonly TileId SouthNeighbour = new TileId { Z = 1, X = 1, Y = 1 };

        private const string ClippedEast  = "clipped-east";
        private const string SurvivingWest = "west";
        private const string ClippedSouth  = "clipped-south-east";
        private const string SurvivingEast = "east";

        // ── Fixture builders ──────────────────────────────────────────────────────────────────────────

        /// <summary>An axis-aligned rectangle spanning the given tile-local range of the world tile, authored
        /// in lon/lat (so the test states its input the way a style document would).</summary>
        private static string RectangleAt(double min, double max)
        {
            double2 nw = WorldTile.ToLonLat(min, min, GeoJsonSliceOptions.DefaultExtent);
            double2 se = WorldTile.ToLonLat(max, max, GeoJsonSliceOptions.DefaultExtent);
            // Tile-local Y grows SOUTHWARD, so the small-Y corner carries the NORTH latitude.
            return GeoJsonTestFixtures.Feature(
                "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(nw.x, se.y, se.x, nw.y)}]");
        }

        /// <summary>The same rectangle, in the tile-local units of an arbitrary tile and carrying a
        /// <c>name</c> property. The name is what makes "which features survived" answerable: a decoder that
        /// took a PREFIX of the dataset instead of the slice keeps every count right and every ordinal in
        /// range, and only the identity of the surviving features gives it away.</summary>
        private static string NamedRectangleIn(TileId tile, double min, double max, string name)
        {
            double2 nw = tile.ToLonLat(min, min, GeoJsonSliceOptions.DefaultExtent);
            double2 se = tile.ToLonLat(max, max, GeoJsonSliceOptions.DefaultExtent);
            return GeoJsonTestFixtures.Feature(
                "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(nw.x, se.y, se.x, nw.y)}]",
                properties: $"{{\"name\":\"{name}\"}}");
        }

        /// <summary>The <c>name</c> a fixture feature carries, or a loud placeholder.</summary>
        private static string NameOf(IFeature feature)
            => feature.TryGetProperty("name", out Value name) ? name.AsString() : "<no name>";

        private static GeoJsonTileDecoder DecoderOver(string collectionJson)
            => new GeoJsonTileDecoder(
                GeoJsonProjectedDataset.Project(GeoJsonParser.Parse(collectionJson)),
                GeoJsonSliceOptions.Default);

        /// <summary>The two-rectangle dataset: feature 0 WEST, feature 1 EAST, in authoring order.</summary>
        private static GeoJsonTileDecoder TwoRectangleDecoder()
            => DecoderOver(GeoJsonTestFixtures.Collection(
                RectangleAt(WestMin, WestMax), RectangleAt(EastMin, EastMax)));

        /// <summary>
        /// The CLIPPING fixture: four features, of which <see cref="SliceTile"/> keeps the second and fourth, a
        /// <b>proper, non-prefix</b> subset. A decoder that took <c>Features</c> from the first <i>n</i> of the
        /// dataset gets counts and ordinals right and features wrong. The survivors are WEST then EAST; the
        /// discards sit a full tile away, far outside the 64/4096 buffer.
        /// </summary>
        private static GeoJsonTileDecoder ClippingDecoder()
            => DecoderOver(GeoJsonTestFixtures.Collection(
                NamedRectangleIn(EastNeighbour,  WestMin,  WestMax,  ClippedEast),
                NamedRectangleIn(SliceTile,      WestMin,  WestMax,  SurvivingWest),
                NamedRectangleIn(SouthNeighbour, EastMin,  EastMax,  ClippedSouth),
                NamedRectangleIn(SliceTile,      EastMin,  EastMax,  SurvivingEast)));

        private static StyleLayer LayerWithSourceLayer(string sourceLayer)
            => new StyleLayer { Id = "probe", Source = "s", SourceLayer = sourceLayer };

        // ── a geojson tile has one layer and does not match names ─────────────────────────────────────

        /// <summary>
        /// The Style Spec makes <c>source-layer</c> "required for vector sources" and unused for geojson, so
        /// absent, empty and bogus names all answer with the sole layer. A style that names a
        /// <c>source-layer</c> over a geojson source therefore still renders.
        /// </summary>
        [Test]
        public void AGeoJsonTileIgnoresTheSourceLayerNameEntirely()
        {
            using IDecodedTile tile = TwoRectangleDecoder().Decode(WorldTile, null);

            // Fetched by the layer's OWN name: it is the one argument a name-matching tile
            // would also answer, so each arm below fails on its own claim rather than on this precondition.
            ITileLayer sole = tile.GetLayer(GeoJsonTileLayer.WellKnownName);
            Assert.IsNotNull(sole, "precondition: the fixture must slice to a layer at all");
            Assert.AreEqual(2, sole.Features.Count, "precondition: both authored rectangles fall in the tile");

            Assert.AreSame(sole, tile.GetLayer(null),
                "an ABSENT source-layer must resolve the sole layer. This is the spec-conformant shape for a " +
                "geojson source — the key is unused — and a short-circuiting resolver makes it select nothing.");
            Assert.AreSame(sole, tile.GetLayer(""),
                "…and so must an EMPTY one: a geojson tile has no sub-layers to disambiguate, so there is " +
                "nothing for a name to select between");
            Assert.AreSame(sole, tile.GetLayer("anything-at-all"),
                "…and so must a BOGUS one. The name is not consulted; returning null for an unrecognised " +
                "name would invent a match-or-nothing rule the spec does not have.");
        }

        /// <summary>
        /// <b>The GeoJSON side</b> — the recorded bug, at the production seam: a spec-conformant geojson style layer
        /// omits <c>source-layer</c>, and <see cref="SourceLayerResolver.ResolveTileLayer"/> must still
        /// resolve it. A resolver that short-circuits a null/empty <c>source-layer</c> to null makes
        /// such a layer select zero features and render NOTHING, silently.
        /// </summary>
        [Test]
        public void AStyleLayerWithNoSourceLayer_ResolvesAGeoJsonTilesSoleLayer()
        {
            using IDecodedTile tile = TwoRectangleDecoder().Decode(WorldTile, null);

            Assert.IsNotNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(null), tile),
                "a spec-conformant geojson style layer omits source-layer, and must still resolve. " +
                "Returning null here is the recorded bug: the layer renders NOTHING, silently.");
            Assert.IsNotNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(string.Empty), tile),
                "…and an explicitly empty one is the same claim through the other half of the old " +
                "short-circuit's condition");
        }

        /// <summary>
        /// <b>The MVT side, the anti-vacuity half.</b> A NAMED <c>source-layer</c> still resolves on an MVT
        /// tile. Without it, its sibling below is satisfied by "MVT resolves nothing, ever" — which would be a
        /// total regression of the vector path passing as a guard working.
        /// </summary>
        [Test]
        public void ANamedSourceLayer_StillResolvesOnAnMvtTile()
        {
            byte[] bytes = MvtBytes.Tile(MvtBytes.Layer("places", MvtBytes.PointFeature(10, 20)));
            using MvtTile tile = MvtDecoder.Decode(MvtTileId, bytes);

            ITileLayer resolved = SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer("places"), tile);
            Assert.IsNotNull(resolved, "a named source-layer must still resolve on an MVT tile — the whole " +
                                       "vector path depends on it");
            Assert.AreEqual("places", resolved.Name, "…and it must be the layer that was named");
            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer("absent"), tile),
                "…while a name no layer carries still selects nothing");
        }

        /// <summary>
        /// Over an MVT tile, "no <c>source-layer</c>" must not select a nameless layer for a background or raster
        /// style layer. The tile is hand-encoded with the two discriminating layers: one with an ABSENT
        /// <c>name</c> (decodes to null) and one with an EMPTY name. No real fixture has either.
        /// </summary>
        [Test]
        public void AStyleLayerWithNoSourceLayer_StillSelectsNothingFromAnMvtTile()
        {
            byte[] bytes = MvtBytes.Tile(
                MvtBytes.NamelessLayer(MvtBytes.PointFeature(10, 20)),   // name field ABSENT ⇒ Name == null
                MvtBytes.Layer("", MvtBytes.PointFeature(30, 40)));      // name present and EMPTY

            using MvtTile tile = MvtDecoder.Decode(MvtTileId, bytes);

            // Precondition: the tile really carries the two shapes. Without this the arms below could pass
            // because the decoder dropped the layers, not because the guard held.
            Assert.AreEqual(2, tile.Layers.Count, "precondition: both hand-encoded layers must decode");
            Assert.IsNull(tile.Layers[0].Name,
                "precondition: an absent `name` field must leave Name NULL — that is the input that " +
                "discriminates, and a decoder defaulting it to \"\" would make this test inert");
            Assert.AreEqual(string.Empty, tile.Layers[1].Name, "precondition: …and the empty-named sibling");

            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(null), tile),
                "an MVT tile must answer NULL for an absent source-layer. The resolver does not " +
                "short-circuit, so this is the TILE's claim — moving it must not turn 'no " +
                "source-layer' into 'the nameless layer' for a background or raster style layer.");
            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(string.Empty), tile),
                "…and the same through the empty-name half of the old condition");
        }

        // ── the ordinal domain of a sliced layer ──────────────────────────────────────────────────────

        /// <summary>
        /// Over a tile that CLIPS features away, the layer lists exactly the SURVIVORS, and its ordinals are
        /// positions in that list. Only the named-survivor arm can fail on <c>Features</c> taken from the first
        /// <i>n</i> of the dataset. Limitation: the count and ordinal-range arms cannot fail, because
        /// <c>LayerGeometryAdoption.Validate</c> throws during <c>Decode</c> first; its own tests are
        /// <c>WaistOneProducerAgreementTests</c>.
        /// </summary>
        [Test]
        public void ASlicedGeoJsonLayer_ListsOnlyTheSurvivingFeatures_AndItsOrdinalsAddressTheBuffer()
        {
            GeoJsonTileDecoder decoder = ClippingDecoder();
            using IDecodedTile tile = decoder.Decode(SliceTile, null);
            ITileLayer layer = tile.GetLayer(GeoJsonTileLayer.WellKnownName);
            Assert.IsNotNull(layer, "precondition: the fixture slices to a layer");
            Assert.AreEqual(2, layer.Features.Count,
                "precondition: exactly two of the four authored features fall in this tile — the list must " +
                "be a PROPER subset, or nothing below can tell a slice from a dataset");

            // The whole point of the fixture: the survivors are the dataset's features 1 and 3, so a decoder
            // taking a PREFIX of the dataset would produce the same count and different features.
            Assert.AreEqual(SurvivingWest, NameOf(layer.Features[0]),
                "the layer's feature 0 must be the WESTERN survivor. The dataset's feature 0 is clipped " +
                "away, so a Features list built from the dataset (or from its first n) puts a feature this " +
                "tile does not contain at ordinal 0 — every per-feature bake then lands on a neighbour, " +
                "with the counts and the ordinal range still perfectly in order.");
            Assert.AreEqual(SurvivingEast, NameOf(layer.Features[1]),
                "…and feature 1 the EASTERN survivor. Asserting only the first would accept a list that " +
                "starts right and then drifts — which is exactly what a prefix of the dataset does here.");

            TileGeometryBuffers geometry = layer.Geometry;
            Assert.IsTrue(geometry.IsCreated, "precondition: the decode really materialized a buffer");
            Assert.AreEqual(2, geometry.RingCount, "precondition: one exterior ring per surviving rectangle");
            Assert.AreEqual(layer.Features.Count, geometry.FeatureCount,
                "the per-feature column must have exactly one slot per feature of the layer. STRUCTURALLY " +
                "PINNED: the adoption guard throws on this mismatch during Decode, so this line states the " +
                "domain rather than testing it — see WaistOneProducerAgreementTests for the guard's teeth.");

            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(LayerWithSourceLayer(null), layer, zoom: 0.0, selected);
            Assert.AreEqual(2, selected.Count, "precondition: a filter-less style layer selects every feature");

            foreach (SelectedTileFeature s in selected)
            {
                Assert.GreaterOrEqual(s.Ordinal, 0,
                    $"ordinal {s.Ordinal} must be a position in Features, and a negative one indexes " +
                    "nothing — stated for the same reason as the upper bound below, and equally structural");
                Assert.Less(s.Ordinal, geometry.FeatureCount,
                    $"ordinal {s.Ordinal} must address the buffer this layer owns (FeatureCount " +
                    $"{geometry.FeatureCount}). STRUCTURALLY PINNED as well: FeatureSelector assigns " +
                    "Ordinal = i over the same list the guard sized the column against. The claim that " +
                    "actually discriminates is the named-survivor pair above, and WHICH ring sits under " +
                    "which ordinal is the sibling test.");
            }
        }

        /// <summary>
        /// An empty slice yields a tile with <b>zero</b> layers, not a layer with zero features.
        ///
        /// <para><b>This is the tooth that observes the DECODER.</b> Its sibling in <c>GeoJsonSourceTests</c>
        /// (<c>AnEmptyInlineDataset_RendersNothing</c>) is satisfied upstream, by the source's emptiness
        /// probe, and stays GREEN against a decoder injected to emit a full-extent quad whenever its slice is
        /// empty — measured. This one reds on it. Do not delete it as a duplicate of that arm.</para>
        /// </summary>
        [Test]
        public void AnEmptySlice_YieldsATileWithNoLayerAtAll()
        {
            // A real dataset wholly outside the asked tile: the slice is empty, but the decoder still has
            // features to reject.
            GeoJsonTileDecoder decoder = TwoRectangleDecoder();
            var farAway = new TileId { Z = 4, X = 15, Y = 15 };

            using IDecodedTile tile = decoder.Decode(farAway, null);

            Assert.IsNull(tile.GetLayer(null),
                "an empty slice must yield ZERO layers. A layer holding zero features would still be handed " +
                "to every consumer and would make FeatureCount == Features.Count true vacuously — and, more " +
                "to the point, 'my sole layer' must actually exist to be returned.");
            Assert.IsNull(tile.GetLayer(GeoJsonTileLayer.WellKnownName), "…by any name, since none is matched");
        }

        /// <summary>
        /// Each ring is filed under the ordinal of the feature that produced it; a permuted path column keeps
        /// every count right. Survivor 0 lies WEST of survivor 1 and they are disjoint, so the ring under
        /// ordinal 1 must lie strictly east of the ring under ordinal 0.
        /// </summary>
        [Test]
        public void EveryRingIsFiledUnderItsOwnFeaturesOrdinal()
        {
            using IDecodedTile tile = ClippingDecoder().Decode(SliceTile, null);
            ITileLayer layer = tile.GetLayer(GeoJsonTileLayer.WellKnownName);
            Assert.IsNotNull(layer, "precondition: the fixture slices to a layer");

            // The CLIPPING fixture: a non-prefix subset whose two survivors are still west then east, disjoint.
            Assert.AreEqual(SurvivingWest, NameOf(layer.Features[0]),
                "precondition: ordinal 0 is the western survivor…");
            Assert.AreEqual(SurvivingEast, NameOf(layer.Features[1]),
                "precondition: …and ordinal 1 the eastern one, which is what makes an out-of-order X below " +
                "a filing error rather than a fixture that authored them the other way round");

            TileGeometryBuffers geometry = layer.Geometry;
            Assert.AreEqual(2, geometry.RingCount, "precondition: exactly one ring per surviving feature");
            Assert.AreEqual(2, geometry.FeatureCount, "precondition: …over two features");

            double westernmostOfOrdinal0 = MinRingX(geometry, ordinal: 0);
            double westernmostOfOrdinal1 = MinRingX(geometry, ordinal: 1);

            // Derived from the fixture, not from a run: the authored gap between the two rectangles.
            Assert.Greater(westernmostOfOrdinal1, WestMax,
                "the ring under ordinal 1 must be east of the one under ordinal 0, because feature 1 is the " +
                "EASTERN rectangle and the two are disjoint. A permuted path column shows up here as an " +
                "out-of-order X while every count stays correct.");
            Assert.Less(westernmostOfOrdinal0, EastMin,
                "…and symmetrically, the ring under ordinal 0 must be the western one — asserting only one " +
                "side would accept a producer that filed both rings under the same feature");
        }

        /// <summary>The smallest tile-local X over every ring filed under <paramref name="ordinal"/>. Fails
        /// loudly when no ring is: a producer that renumbered ring→feature densely over the features that
        /// happen to carry rings leaves the counts right and the addressing one slot out.</summary>
        private static double MinRingX(TileGeometryBuffers geometry, int ordinal)
        {
            double min = double.PositiveInfinity;
            for (int r = 0; r < geometry.RingCount; r++)
            {
                if (geometry.RingFeatureIdx[r] != ordinal) continue;
                for (int v = geometry.RingOffsets[r]; v < geometry.RingOffsets[r + 1]; v++)
                    min = math.min(min, geometry.Vertices[v].x);
            }

            Assert.IsFalse(double.IsPositiveInfinity(min),
                $"no ring is filed under ordinal {ordinal}. RingFeatureIdx names the feature's position in " +
                "ITileLayer.Features, so a ring-bearing feature always has one.");
            return min;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GraphDeterminismTests — one arm per batch constant, each reading only THAT node's own constant
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class GraphDeterminismTests
    {
        // ── Determinism: default worker count vs JobWorkerCount = 0, full column set, both arms. ──────

        private struct PassResult
        {
            /// <summary>SHA-256 over the full column set (TileVertices/WorldPositions/VertexUp/VertexEast/
            /// VertexFeatureIdx/TriangleIndices + the four counts) — the determinism claim (assertion 1).
            /// Digested on BOTH arms; nothing outside this test compares the curved arm's post-subdivision
            /// columns to anything, so there is no format to preserve beyond self-equality.</summary>
            public string FullColumnDigest;

            /// <summary>The SAME format <see cref="MapRenderer.Tests.Tiles.FillMeshGraphParityTests"/> pins as
            /// its frozen goldens (9 flat streams / 4 curved-arm count streams) — assertion 2 compares this,
            /// from the DEFAULT-worker pass only, to that file's own constants.</summary>
            public string GoldenFormatDigest;

            public int MaxPolygonCount;
            public int MaxVertexCount;
            public bool AnyNonEmptyOutput;
        }

        private static PassResult RunOnePass(IProjection projection, bool curved)
        {
            var vertexBytes = new List<byte>(); var worldBytes = new List<byte>(); var upBytes = new List<byte>();
            var eastBytes = new List<byte>(); var featBytes = new List<byte>(); var idxBytes = new List<byte>();
            var bandBytes = new List<byte>();
            var polyBytes = new List<byte>(); var ringBytes = new List<byte>(); var holeBytes = new List<byte>(); var fcBytes = new List<byte>();

            int maxPolygonCount = 0;
            int maxVertexCount = 0;
            bool anyNonEmpty = false;

            void Accumulate(FillMeshPipeline.LayerInput input)
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs(); // load-bearing — an un-flushed schedule runs near-inline,
                                                  // which would make this a serial-to-serial comparison.
                output.Handle.Complete();
                try
                {
                    Assert.IsTrue(output.IsCreated, "precondition: every WalkCorpusAndSynthetic case has a surviving polygon");
                    Assert.AreEqual(FillGraphCounts.Ok, output.Error.Value,
                        "every corpus/synthetic case must complete with no error flag set");

                    FillGraphCounts c = output.Counts[0];
                    maxPolygonCount = math.max(maxPolygonCount, c.PolygonCount);
                    int vc = output.TileVertices.Length;
                    maxVertexCount = math.max(maxVertexCount, vc);
                    if (vc > 0) anyNonEmpty = true;

                    polyBytes.AddRange(BitConverter.GetBytes(c.PolygonCount));
                    ringBytes.AddRange(BitConverter.GetBytes(c.RingCount));
                    holeBytes.AddRange(BitConverter.GetBytes(c.HoleCount));
                    fcBytes.AddRange(BitConverter.GetBytes(c.ForceClipCount));

                    // Both arms digest the full columns here for the determinism claim; `curved` matters only
                    // in the golden-format branch below.
                    int ic = output.TriangleIndices.Length;

                    // The golden digests the INTERIOR PREFIX only, as FillMeshGraphParityTests does; the band's
                    // bytes go into bandBytes, appended to the full-column digest only.
                    int interiorCount = vc - c.BandVertexCount;
                    for (int i = interiorCount; i < vc; i++)
                    {
                        bandBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].z));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].z));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].z));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexFeatureIdx[i]));
                    }
                    for (int i = 0; i < vc; i++)
                    {
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexBand[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexBand[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexBand[i].z));
                    }
                    for (int i = 0; i < ic; i++) bandBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i]));

                    vc = interiorCount;
                    for (int i = 0; i < vc; i++)
                    {
                        vertexBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].x));
                        vertexBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].y));
                        worldBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].x));
                        worldBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].y));
                        worldBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].z));
                        upBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].x));
                        upBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].y));
                        upBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].z));
                        eastBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].x));
                        eastBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].y));
                        eastBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].z));
                        featBytes.AddRange(BitConverter.GetBytes(output.VertexFeatureIdx[i]));
                    }
                    for (int i = 0; i + 2 < ic; i += 3)
                    {
                        if (output.TriangleIndices[i] >= vc || output.TriangleIndices[i + 1] >= vc || output.TriangleIndices[i + 2] >= vc)
                            continue;
                        idxBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i]));
                        idxBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i + 1]));
                        idxBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i + 2]));
                    }
                }
                finally { output.Dispose(); }
            }

            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(projection, Accumulate);

            string countsResult =
                $"PolyCount={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(polyBytes)} " +
                $"RingCount={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(ringBytes)} " +
                $"HoleCount={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(holeBytes)} " +
                $"ForceClip={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(fcBytes)}";
            string goldenFormat = curved
                ? countsResult
                : $"Vertex={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(vertexBytes)} " +
                  $"World={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(worldBytes)} " +
                  $"Up={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(upBytes)} " +
                  $"FeatIdx={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(featBytes)} " +
                  $"Indices={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(idxBytes)} {countsResult}";

            string fullColumnDigest =
                $"{goldenFormat} East={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(eastBytes)}" +
                $" Band={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(bandBytes)}";

            return new PassResult
            {
                FullColumnDigest = fullColumnDigest,
                GoldenFormatDigest = goldenFormat,
                MaxPolygonCount = maxPolygonCount,
                MaxVertexCount = maxVertexCount,
                AnyNonEmptyOutput = anyNonEmpty,
            };
        }

        [Test]
        public void Schedule_IsDeterministic_DefaultVsZeroWorkerCount_FullColumnSet(
            [ValueSource(typeof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests), nameof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests.ProjectionCases))]
            IProjection projection)
        {
            bool curved = !double.IsInfinity(projection.MaxRefineAngleRad);

            // Vacuity guard 1 — a single-worker machine would make every "parallel" pass below run on the
            // one worker Editor's own thread anyway, silently vacuous.
            Assert.Greater(JobsUtility.JobWorkerCount, 1,
                "JobsUtility.JobWorkerCount must exceed 1 or every tooth in this stage is vacuous");

            PassResult defaultPass = RunOnePass(projection, curved);

            int original = JobsUtility.JobWorkerCount;
            JobsUtility.JobWorkerCount = 0;
            PassResult zeroPass;
            try
            {
                zeroPass = RunOnePass(projection, curved);
            }
            finally
            {
                // Restore before any assertion can throw: a JobWorkerCount left at 0 starves every later test
                // in the run.
                JobsUtility.JobWorkerCount = original;
            }

            // Vacuity guard 2 — an enumeration that matched zero polygon layers would hash the empty string on
            // both passes, which compares equal to itself forever.
            Assert.IsTrue(defaultPass.AnyNonEmptyOutput,
                $"[proj={projection.GetType().Name}] precondition: at least one corpus/synthetic case must " +
                "produce a non-empty FillGraphOutput, or the digests below compare two empty hashes");

            // Contention guard sized to the machine: with fewer than max(8, JobWorkerCount) batches the
            // two-pass equality below could run uncontended and prove nothing.
            int minBatches = math.max(8, JobsUtility.JobWorkerCount);
            Assert.GreaterOrEqual(defaultPass.MaxPolygonCount, minBatches,
                $"[proj={projection.GetType().Name}] the corpus's largest case must offer >= {minBatches} " +
                "polygons or the earcut node (once parallel) never has more than one worker contending — a " +
                "corpus question, not a lowered guard.");
            // Flat arm only: the curved arm schedules neither TileToGeoJob nor ProjectionDispatch, and its
            // MaxVertexCount is a post-subdivision count.
            if (!curved)
                Assert.GreaterOrEqual(defaultPass.MaxVertexCount, minBatches * FillMeshGraph.VertexBatch,
                    $"[proj={projection.GetType().Name}] the corpus's largest case must offer >= {minBatches} * " +
                    $"{FillMeshGraph.VertexBatch} vertices or the tile→geo/project nodes never have more than one " +
                    "worker contending — a corpus question, not a lowered guard, and NOT a lowered VertexBatch " +
                    "(which would clear this guard and B.6's fan-out tooth in one move).");

            // Assertion 1 — the determinism claim: full column set, both arms, self-compared.
            Assert.AreEqual(defaultPass.FullColumnDigest, zeroPass.FullColumnDigest,
                $"[proj={projection.GetType().Name}] default-worker-count and zero-worker-count schedules of " +
                "the SAME graph over the SAME corpus produced different bytes — a race, not merely a batching " +
                "difference (job-scheduling-design.md § \"Safety — making the Editor's check sufficient\": every node's " +
                "bytes are a pure function of its own input, independent of which worker runs it or how many run at once).");

            // Assertion 2 — the frozen fill-parity goldens, at their real reach, from the default-worker pass.
            string expectedGolden = curved
                ? MapRenderer.Tests.Tiles.FillMeshGraphParityTests.FrozenGoldensSpherical
                : MapRenderer.Tests.Tiles.FillMeshGraphParityTests.FrozenGoldensMercator;
            Assert.AreEqual(expectedGolden, defaultPass.GoldenFormatDigest,
                $"[proj={projection.GetType().Name}] this test's own corpus walk no longer reproduces " +
                "FillMeshGraphParityTests' frozen goldens — WalkCorpusAndSynthetic has diverged from what it " +
                "was captured against.");
        }

        // ── Line graph determinism: the same worker-0-vs-default self-comparison over LineGraphOutput ─────
        // No golden here: the committed line-graphwrite goldens already pin the exact bytes.

        private static (string Digest, int MaxRingCount, bool AnyNonEmpty) RunOneLinePass(IProjection projection)
        {
            string fixturesDir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(System.IO.Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { System.IO.Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var vertBytes = new List<byte>(); var featBytes = new List<byte>(); var idxBytes = new List<byte>();
            int maxRingCount = 0;
            bool anyNonEmpty = false;

            foreach (string path in allPaths)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var tileId = new MapRenderer.Core.Geo.TileId { Z = 0, X = 0, Y = 0 };
                using var mvtTile = MapRenderer.Jobs.Mvt.MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    var geometry = layer.Geometry;
                    int ringCount = 0;
                    for (int ri = 0; ri < geometry.RingCount; ri++)
                    {
                        int fi = geometry.RingFeatureIdx[ri];
                        if (geometry.FeatureGeometryType[fi] == MapRenderer.Core.Tiles.TileGeometryType.LineString) ringCount++;
                    }
                    if (ringCount == 0) continue;
                    maxRingCount = math.max(maxRingCount, ringCount);

                    var selected = new NativeArray<bool>(geometry.FeatureCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    for (int i = 0; i < geometry.FeatureCount; i++) selected[i] = true;
                    try
                    {
                        var input = new LayerInput
                        {
                            Geometry = geometry, FeatureSelected = selected, OriginRender = default, Projection = projection,
                            Join = JoinType.Miter, Cap = CapType.Butt, MiterLimit = 2.0, RoundSegments = 8, RoundLimit = 0.25,
                            MaxOutputVertices = LineMeshGraph.DefaultMaxOutputVertices,
                        };
                        LineGraphOutput output = LineMeshGraph.Schedule(input);
                        JobHandle.ScheduleBatchedJobs();
                        output.Handle.Complete();
                        try
                        {
                            if (!output.IsCreated) continue;
                            Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value,
                                "every corpus case must complete with no error flag set");
                            int vc = output.Vertices.Length;
                            if (vc > 0) anyNonEmpty = true;
                            for (int i = 0; i < vc; i++)
                            {
                                LineRibbonVertex v = output.Vertices[i];
                                vertBytes.AddRange(BitConverter.GetBytes(v.Position.x));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Position.y));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Position.z));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Across.x));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Across.y));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Across.z));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Up.x));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Up.y));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Up.z));
                                vertBytes.AddRange(BitConverter.GetBytes(v.DistanceAlong));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Side));
                                vertBytes.AddRange(BitConverter.GetBytes(v.WidthScale));
                                featBytes.AddRange(BitConverter.GetBytes(output.VertexFeatureIdx[i]));
                            }
                            for (int i = 0; i < output.Indices.Length; i++) idxBytes.AddRange(BitConverter.GetBytes(output.Indices[i]));
                        }
                        finally { output.Dispose(); }
                    }
                    finally { selected.Dispose(); }
                }
            }

            string digest =
                $"Vertex={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(vertBytes)} " +
                $"FeatIdx={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(featBytes)} " +
                $"Indices={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(idxBytes)}";
            return (digest, maxRingCount, anyNonEmpty);
        }

        [Test]
        public void LineSchedule_IsDeterministic_DefaultVsZeroWorkerCount(
            [ValueSource(typeof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests), nameof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests.ProjectionCases))]
            IProjection projection)
        {
            Assert.Greater(JobsUtility.JobWorkerCount, 1,
                "JobsUtility.JobWorkerCount must exceed 1 or every tooth in this stage is vacuous");

            var defaultPass = RunOneLinePass(projection);

            int original = JobsUtility.JobWorkerCount;
            JobsUtility.JobWorkerCount = 0;
            (string Digest, int MaxRingCount, bool AnyNonEmpty) zeroPass;
            try { zeroPass = RunOneLinePass(projection); }
            finally { JobsUtility.JobWorkerCount = original; } // unconditional — see the fill arm's own note

            Assert.IsTrue(defaultPass.AnyNonEmpty,
                $"[proj={projection.GetType().Name}] precondition: at least one corpus case must produce a " +
                "non-empty LineGraphOutput, or the digests below compare two empty hashes");

            int minRings = math.max(8, JobsUtility.JobWorkerCount);
            Assert.GreaterOrEqual(defaultPass.MaxRingCount, minRings,
                $"[proj={projection.GetType().Name}] the corpus's largest case must offer >= {minRings} rings " +
                "or the ribbon node never has more than one worker contending — a corpus question, not a " +
                "lowered guard, and NOT a lowered RibbonRingBatch.");

            Assert.AreEqual(defaultPass.Digest, zeroPass.Digest,
                $"[proj={projection.GetType().Name}] default-worker-count and zero-worker-count schedules of " +
                "the SAME line graph over the SAME corpus produced different bytes — a race, not merely a " +
                "batching difference.");
        }

        // ── B.6 — fan-out: each batch constant, read from ITS OWN declaration, gives >= 2 batches on the corpus ─
        // The failure message says why lowering a constant is wrong.

        private const string RefusalMessage =
            "red here means the corpus changed or a constant was restored — do not lower the constant; see " +
            "the design doc's recorded corpus sizes.";

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_FillTileToGeo()
        {
            int maxVertices = LargestFillVertexCount(new WebMercatorProjection());
            Assert.GreaterOrEqual(maxVertices / FillMeshGraph.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_Project()
        {
            // ProjectionDispatch.VertexBatch is shared by all three graphs; the fill flat-arm vertex count is what
            // ProjectPointsJob processes.
            int maxVertices = LargestFillVertexCount(new WebMercatorProjection());
            Assert.GreaterOrEqual(maxVertices / ProjectionDispatch.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_LineTileToGeo()
        {
            int maxPoints = LargestLineSubdividedPointCount();
            Assert.GreaterOrEqual(maxPoints / LineMeshGraph.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_ExtrusionTileToGeo()
        {
            // The wall chain's TileToGeoJob processes the fill pre-pass's totalVerts (RingOffsets summed over the
            // same visit order), so measure that directly instead of scheduling the whole wall chain.
            int maxVertices = LargestRawVisitedRingVertexCount();
            Assert.GreaterOrEqual(maxVertices / MapRenderer.Unity.Rendering.Meshing.FillExtrusionMeshGraph.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_Earcut()
        {
            // EarcutPolygonBatch == 1, so count/batch >= 2 reduces to "at least 2 polygons" — the measured
            // corpus (maxPolygonCount=3218) clears this by three orders of magnitude.
            int maxPolygonCount = LargestFillPolygonCount(new WebMercatorProjection());
            Assert.GreaterOrEqual(maxPolygonCount / FillMeshGraph.EarcutPolygonBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_Ribbon()
        {
            // RibbonRingBatch == 1, so count/batch >= 2 reduces to "at least 2 rings".
            int maxRingCount = LargestLineRingCount();
            Assert.GreaterOrEqual(maxRingCount / LineMeshGraph.RibbonRingBatch, 2, RefusalMessage);
        }

        private static int LargestFillPolygonCount(IProjection projection)
        {
            int max = 0;
            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(projection, input =>
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                output.Handle.Complete();
                try { if (output.IsCreated) max = math.max(max, output.Counts[0].PolygonCount); }
                finally { output.Dispose(); }
            });
            return max;
        }

        private static int LargestLineRingCount()
        {
            string fixturesDir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(System.IO.Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { System.IO.Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            int max = 0;
            foreach (string path in allPaths)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var tileId = new MapRenderer.Core.Geo.TileId { Z = 0, X = 0, Y = 0 };
                using var mvtTile = MapRenderer.Jobs.Mvt.MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    var geometry = layer.Geometry;
                    int ringCount = 0;
                    for (int ri = 0; ri < geometry.RingCount; ri++)
                    {
                        int featIdx = geometry.RingFeatureIdx[ri];
                        if (geometry.FeatureGeometryType[featIdx] == MapRenderer.Core.Tiles.TileGeometryType.LineString)
                            ringCount++;
                    }
                    max = math.max(max, ringCount);
                }
            }
            return max;
        }

        private static int LargestFillVertexCount(IProjection projection)
        {
            int max = 0;
            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(projection, input =>
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                output.Handle.Complete();
                try { if (output.IsCreated) max = math.max(max, output.TileVertices.Length); }
                finally { output.Dispose(); }
            });
            return max;
        }

        private static int LargestRawVisitedRingVertexCount()
        {
            int max = 0;
            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(new WebMercatorProjection(), input =>
            {
                NativeArray<int> visit = input.RingVisitOrder;
                var source = input.Geometry;
                int totalVerts = 0;
                for (int k = 0; k < visit.Length; k++)
                {
                    int ri = visit[k];
                    totalVerts += source.RingOffsets[ri + 1] - source.RingOffsets[ri];
                }
                max = math.max(max, totalVerts);
            });
            return max;
        }

        /// <summary>Runs <see cref="LineMeshGraph.ScheduleTyped{TProj}"/>'s gather→tile-geo→project→subdivide
        /// sub-chain to read the SUBDIVIDED centerline point count, which the graph's second
        /// <c>TileToGeoJob</c> processes. It walks the corpus directly, because <c>WalkCorpusAndSynthetic</c>
        /// keeps only Polygon layers, which hold almost no LineStrings here.</summary>
        private static int LargestLineSubdividedPointCount()
        {
            string fixturesDir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(System.IO.Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { System.IO.Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var projections = new IProjection[] { new WebMercatorProjection(), new SphericalProjection() };
            int max = 0;

            foreach (string path in allPaths)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var tileId = new MapRenderer.Core.Geo.TileId { Z = 0, X = 0, Y = 0 };
                using var mvtTile = MapRenderer.Jobs.Mvt.MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    var geometry = layer.Geometry;
                    bool hasLine = false;
                    for (int fi = 0; fi < geometry.FeatureCount; fi++)
                        if (geometry.FeatureGeometryType[fi] == MapRenderer.Core.Tiles.TileGeometryType.LineString) { hasLine = true; break; }
                    if (!hasLine) continue;

                    var selected = new NativeArray<bool>(geometry.FeatureCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    for (int i = 0; i < geometry.FeatureCount; i++) selected[i] = true;

                    foreach (IProjection projection in projections)
                    {
                        var srcTile = new NativeList<double2>(64, Allocator.Persistent);
                        var ringSrcOffsets = new NativeList<int>(8, Allocator.Persistent);
                        var ringFeature = new NativeList<int>(8, Allocator.Persistent);
                        var srcGeo = new NativeList<GeoCoordinate>(64, Allocator.Persistent);
                        var srcWorld = new NativeList<double3>(64, Allocator.Persistent);
                        var srcUp = new NativeList<double3>(64, Allocator.Persistent);
                        var subTile = new NativeList<double2>(64, Allocator.Persistent);
                        var ringSubOffsets = new NativeList<int>(8, Allocator.Persistent);
                        var subGeo = new NativeList<GeoCoordinate>(64, Allocator.Persistent);
                        var subWorld = new NativeList<double3>(64, Allocator.Persistent);
                        var subUp = new NativeList<double3>(64, Allocator.Persistent);
                        try
                        {
                            new RingGatherJob
                            {
                                Vertices = geometry.Vertices, RingOffsets = geometry.RingOffsets, RingFeatureIdx = geometry.RingFeatureIdx,
                                FeatureGeometryType = geometry.FeatureGeometryType, RingCount = geometry.RingCount,
                                FeatureSelected = selected,
                                OutSrcTile = srcTile, OutRingSrcOffsets = ringSrcOffsets, OutRingFeature = ringFeature,
                                OutSrcGeo = srcGeo, OutSrcWorld = srcWorld, OutSrcUp = srcUp,
                            }.Run();

                            if (srcTile.Length == 0) continue; // no LineString rings survived selection

                            new TileToGeoJob
                            {
                                Tile = geometry.Tile, Extent = geometry.Extent,
                                TileCoords = srcTile.AsArray(), OutGeo = srcGeo.AsArray(),
                            }.Run(srcTile.Length);

                            DispatchProjectionForMeasurement(projection, double3.zero, srcGeo, srcWorld, srcUp);

                            new SubdivideJob
                            {
                                SrcTile = srcTile, RingSrcOffsets = ringSrcOffsets, SrcUp = srcUp,
                                MaxRefineAngleRad = projection.MaxRefineAngleRad,
                                OutSubTile = subTile, OutRingSubOffsets = ringSubOffsets,
                                OutSubGeo = subGeo, OutSubWorld = subWorld, OutSubUp = subUp,
                            }.Run();

                            max = math.max(max, subTile.Length);
                        }
                        finally
                        {
                            srcTile.Dispose(); ringSrcOffsets.Dispose(); ringFeature.Dispose();
                            srcGeo.Dispose(); srcWorld.Dispose(); srcUp.Dispose();
                            subTile.Dispose(); ringSubOffsets.Dispose();
                            subGeo.Dispose(); subWorld.Dispose(); subUp.Dispose();
                        }
                    }

                    selected.Dispose();
                }
            }
            return max;
        }

        private static void DispatchProjectionForMeasurement(
            IProjection projection, double3 originWorld,
            NativeList<GeoCoordinate> points, NativeList<double3> world, NativeList<double3> normals)
        {
            switch (projection)
            {
                case WebMercatorProjection wm:
                    new ProjectPointsJob<WebMercatorProjection>
                    {
                        Projection = wm, OriginWorld = originWorld,
                        Points = points.AsArray(), WorldPositions = world.AsArray(), Normals = normals.AsArray(),
                    }.Run(points.Length);
                    break;
                case SphericalProjection sp:
                    new ProjectPointsJob<SphericalProjection>
                    {
                        Projection = sp, OriginWorld = originWorld,
                        Points = points.AsArray(), WorldPositions = world.AsArray(), Normals = normals.AsArray(),
                    }.Run(points.Length);
                    break;
                default:
                    throw new NotSupportedException($"no measurement dispatch for {projection.GetType().Name}");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // JobGraphInstrumentTests — the substrate-stage probes plus the delay-job instrument
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class JobGraphInstrumentTests : BaseTestFixture
    {
        [BurstCompile(CompileSynchronously = true)]
        private struct WriteOneJob : IJob
        {
            public NativeArray<int> Out;
            public void Execute() => Out[0] = 42;
        }

        // ── JobWorkerCount = 0 does NOT hold a scheduled job incomplete ───────────────────────────────────
        // Non-obvious why: this test records why the delay instrument below uses a deps job, not this knob.
        // Complete() and Dispose() run before any assertion, or a dispose-time exception in finally replaces
        // the assertion failure (docs/lessons-learned.md).
        [Test]
        public void ZeroWorkerCount_DoesNotHoldAScheduledJobIncomplete()
        {
            int original = JobsUtility.JobWorkerCount;
            JobsUtility.JobWorkerCount = 0;

            var result = new NativeArray<int>(1, Allocator.Persistent);
            JobHandle handle = new WriteOneJob { Out = result }.Schedule();
            JobHandle.ScheduleBatchedJobs();

            bool completedBeforeComplete = handle.IsCompleted; // captured before any risk of an early throw
            handle.Complete(); // unconditional — releases the safety handle regardless of what we find above
            int value = result[0];
            result.Dispose();
            JobsUtility.JobWorkerCount = original; // unconditional, same reason

            Assert.IsTrue(completedBeforeComplete,
                "JobWorkerCount == 0 does NOT hold a scheduled job incomplete — IsCompleted reads true " +
                "immediately after Schedule(), even for a trivial one-field job. This is why the substrate's " +
                "delay instrument uses FillMeshGraph.Schedule's existing `deps` parameter instead of this knob.");
            Assert.AreEqual(42, value);
        }

        // ── The MeshData-view probe — decides FillStreamWriteJob's field set: a SCHEDULED job over a
        // MeshData-derived view (job-scheduling-design.md). ─────────────────────────────

        [BurstCompile(CompileSynchronously = true)]
        private struct FillPositionsJob : IJob
        {
            public NativeArray<Vector3> Positions;
            public void Execute()
            {
                for (int i = 0; i < Positions.Length; i++)
                    Positions[i] = new Vector3(i, i, i);
            }
        }

        /// <summary>This probe's own claim does not depend on the worker-count finding above: whether a
        /// scheduled job can safely touch a MeshData-derived view is orthogonal to whether any particular
        /// knob holds it incomplete. Complete()/Apply run unconditionally before any assertion, for the same
        /// masked-exception reason noted above.</summary>
        [Test]
        public void ScheduledJobCanWriteMeshDataStreamViews_AndBeReadBackAfterComplete()
        {
            Mesh.MeshDataArray mda = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData md = mda[0];
            md.SetVertexBufferParams(4,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3));
            NativeArray<Vector3> positions = md.GetVertexData<Vector3>(0);

            JobHandle handle = new FillPositionsJob { Positions = positions }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            var mesh = Track(new Mesh());
            Mesh.ApplyAndDisposeWritableMeshData(mda, mesh);
            Vector3[] verts = mesh.vertices;
            Assert.AreEqual(4, verts.Length);
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(new Vector3(i, i, i), verts[i], $"vertex {i}");
        }

        // ── The deps-seam delay instrument ─────────────────────────────────────────────────────────────────
        // A test passes its own delay job as FillMeshGraph.Schedule's production `deps`, holding the chain
        // incomplete. The calibration catches a spin Burst folds to a closed form and a hoisted gate read.
        // Non-obvious why: either one makes the delay instant or endless, so tests pass while proving nothing.

        [Test]
        public void DelayJob_HeldByGate_IsGenuinelyInFlight_UnderDefaultWorkerCount()
        {
            // Does NOT touch JobsUtility.JobWorkerCount — the finding above is why.
            const int maxIterations = 2_000_000_000;
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            try
            {
                JobHandle handle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = maxIterations }.Schedule();
                JobHandle.ScheduleBatchedJobs();

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(handle.IsCompleted,
                    "the gated job must still be spinning right after it signalled it started — if Burst " +
                    "folded the loop or hoisted the gate read, this would already be true");

                gate[0] = 1; // release
                handle.Complete();

                Assert.Greater(outVals[0], 0, "the job must have run at least one real iteration");
                Assert.Less(outVals[0], maxIterations,
                    "the job must have been stopped by the GATE, not by exhausting MaxIterations — otherwise " +
                    "Burst folded the loop, or the ceiling is too low for this machine");
            }
            finally
            {
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary>A <see cref="FillMeshGraph"/> scheduled with <c>deps</c> set to a spinning delay job stays
        /// incomplete, as <c>TileBuildGraph.ScheduleMeasure</c> uses it. It drives the production arm: SPHERICAL
        /// with the clip ENABLED (<c>FillTileBufferClip = 0.0</c> is <c>KeepTileUnits(0.0)</c>); an unset
        /// <c>Clip</c> would drive the disabled arm production never takes.</summary>
        [Test]
        public void DelayJobAsDeps_HoldsAFillMeshGraph_GenuinelyIncomplete()
        {
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);

            TileGeometryBuffers geometry = default;
            NativeArray<int> visitOrder = default;
            FillGraphOutput graphOutput = default;
            try
            {
                geometry = TileGeometryBuffers.Allocate(
                    new TileId { Z = 0, X = 0, Y = 0 }, extent: 4096.0,
                    featureCount: 1, maxRings: 1, maxVertices: 3);
                geometry.FeatureGeometryType[0] = TileGeometryType.Polygon;
                geometry.RingOffsets[0] = 0;
                geometry.Vertices[0] = new double2(0, 0);
                geometry.Vertices[1] = new double2(10, 0);
                geometry.Vertices[2] = new double2(10, 10);
                geometry.RingFeatureIdx[0] = 0;
                geometry.RingOffsets[1] = 3;
                geometry.RingCount = 1;
                geometry.VertexCount = 3;

                visitOrder = new NativeArray<int>(1, Allocator.Persistent);
                visitOrder[0] = 0;

                var input = new FillMeshPipeline.LayerInput
                {
                    Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                    Projection = new SphericalProjection(), Clip = TileBufferClip.KeepTileUnits(0.0),
                };

                JobHandle delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();

                graphOutput = FillMeshGraph.Schedule(input, delayHandle);
                JobHandle.ScheduleBatchedJobs();

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(graphOutput.Handle.IsCompleted,
                    "a graph scheduled with deps = a still-spinning delay job must itself read incomplete");

                gate[0] = 1; // release
                graphOutput.Handle.Complete();

                Assert.IsTrue(graphOutput.Handle.IsCompleted);
                Assert.Greater(graphOutput.TileVertices.Length, 0, "the graph must still have produced real output");
            }
            finally
            {
                graphOutput.Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineRibbonSizingJobTests — Arm B, the parity check honestly claimed (see file for the correction)
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LineRibbonSizingJobTests
    {
        /// <summary>The real <see cref="RibbonAggregateJob"/> over a <see cref="RibbonBuffers"/> with every column
        /// at length 0, while a real <c>RingFeature</c> still holds 2 rings: it must bound its loop by its own
        /// column, not a borrowed one. The state is constructed, not driven through sizing, but the aggregate
        /// reads <c>perRingVertexCount[r]</c> first, and that column is empty either way.</summary>
        [Test]
        public void AggregateOverUnsizedBuffers_ProducesEmptyOutput_AndDoesNotIndexPastThem()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Fail("ENABLE_UNITY_COLLECTIONS_CHECKS is not defined in this test assembly build — " +
                "without it this tooth cannot detect a borrowed-count OOB read in a release-configured build.");
#endif
            RibbonBuffers buffers = RibbonBuffers.Allocate();
            var ringFeature = new NativeList<int>(Allocator.Persistent);
            ringFeature.Add(0);
            ringFeature.Add(0);

            var outVertices         = new NativeList<LineRibbonVertex>(Allocator.Persistent);
            var outVertexFeatureIdx = new NativeList<int>(Allocator.Persistent);
            var outIndices          = new NativeList<int>(Allocator.Persistent);
            var error               = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new RibbonAggregateJob
                {
                    RingFeature = ringFeature,
                    Buffers = buffers,
                    MaxOutputVertices = 1000,
                    OutVertices = outVertices, OutVertexFeatureIdx = outVertexFeatureIdx, OutIndices = outIndices,
                    Error = error,
                }.Run();

                Assert.AreEqual(LineGraphCounts.Ok, error.Value,
                    "an empty (post-early-return) buffers state must not itself be reported as an error");
                Assert.AreEqual(0, outVertices.Length, "no ring to append means no output vertices");
                Assert.AreEqual(0, outVertexFeatureIdx.Length);
                Assert.AreEqual(0, outIndices.Length, "no ring to append means no output indices");

                // Non-vacuity: a borrowed-column bound would still read 2 rings here.
                Assert.AreEqual(2, ringFeature.Length);
            }
            finally
            {
                buffers.DisposeAfter(default).Complete();
                ringFeature.Dispose();
                outVertices.Dispose(); outVertexFeatureIdx.Dispose(); outIndices.Dispose();
                error.Dispose();
            }
        }

        /// <summary>The line twin of <c>TwoValidPolygons_ProduceStrictlyIncreasingOffsetTables_NoErrorFlag</c>: two
        /// real rings within capacity give four strictly increasing offset tables and
        /// <see cref="LineGraphCounts.Ok"/>; a zero-length ring slice gives
        /// <see cref="LineGraphCounts.ErrorOffsetTableNotDisjoint"/>. It cannot catch an out-of-bounds
        /// read; <see cref="AggregateOverUnsizedBuffers_ProducesEmptyOutput_AndDoesNotIndexPastThem"/> does.
        /// </summary>
        [Test]
        public void TwoValidRings_ProduceStrictlyIncreasingOffsetTables_NoErrorFlag()
        {
            var ringSubOffsets = new NativeList<int>(Allocator.Persistent);
            ringSubOffsets.Add(0); ringSubOffsets.Add(4); ringSubOffsets.Add(9);

            RibbonBuffers buffers = RibbonBuffers.Allocate();
            var error = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new RibbonSizingJob
                {
                    RingSubOffsets = ringSubOffsets,
                    RoundSegments = 8,
                    Buffers = buffers,
                    Error = error,
                }.Run();

                Assert.AreEqual(LineGraphCounts.Ok, error.Value,
                    "two real, in-capacity rings must produce strictly increasing offset tables");

                int[] ringVertexOffsets = buffers.RingVertexOffsets.AsArray().ToArray();
                int[] ringIndexOffsets  = buffers.RingIndexOffsets.AsArray().ToArray();
                for (int r = 0; r < 2; r++)
                {
                    Assert.Greater(ringVertexOffsets[r + 1], ringVertexOffsets[r], $"RingVertexOffsets[{r}] must be strictly increasing");
                    Assert.Greater(ringIndexOffsets[r + 1], ringIndexOffsets[r], $"RingIndexOffsets[{r}] must be strictly increasing");
                }

                Assert.AreEqual(ringVertexOffsets[2], buffers.FlatVertices.Length, "FlatVertices must be resized to the offset table's total");
                Assert.AreEqual(ringIndexOffsets[2], buffers.FlatIndices.Length, "FlatIndices must be resized to the offset table's total");
                Assert.AreEqual(2, buffers.PerRingVertexCount.Length);
                Assert.AreEqual(2, buffers.PerRingIndexCount.Length);
            }
            finally
            {
                ringSubOffsets.Dispose();
                buffers.DisposeAfter(default).Complete();
                error.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MvtCommandStream — test-only helper: authors MVT geometry command streams
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Authors MVT geometry command streams (spec §4.3: <c>command = id &amp; 0x7</c>,
    /// <c>count = id &gt;&gt; 3</c>; MoveTo=1, LineTo=2, ClosePath=7; parameters are zigzag deltas against a
    /// running cursor) so a test can hand <see cref="MvtGeometryMaterializer"/> an exact ring layout.
    /// The encoding is the one <c>MvtDecodeJob</c> decodes; production hand-authors no such stream
    /// (<c>FullExtentRingCommandStream</c> keeps a frozen full-extent ring as test data).
    /// </summary>
    internal static class MvtCommandStream
    {
        private const uint MoveTo    = 1;
        private const uint LineTo    = 2;
        private const uint ClosePath = 7;

        /// <summary>One feature's geometry: each ring becomes MoveTo×1 + LineTo×(n−1) + ClosePath, with the
        /// cursor carrying across rings exactly as the spec requires.</summary>
        public static uint[] Feature(params IReadOnlyList<double2>[] rings)
        {
            var commands = new List<uint>();
            long cursorX = 0, cursorY = 0;

            foreach (IReadOnlyList<double2> ring in rings)
            {
                if (ring.Count == 0) continue;

                commands.Add((1u << 3) | MoveTo);
                AppendDelta(commands, ring[0], ref cursorX, ref cursorY);

                if (ring.Count > 1)
                {
                    commands.Add(((uint)(ring.Count - 1) << 3) | LineTo);
                    for (int i = 1; i < ring.Count; i++)
                        AppendDelta(commands, ring[i], ref cursorX, ref cursorY);
                }

                commands.Add((1u << 3) | ClosePath);
            }

            return commands.ToArray();
        }

        public static List<double2> Ring(params double[] xyPairs)
        {
            var ring = new List<double2>(xyPairs.Length / 2);
            for (int i = 0; i < xyPairs.Length; i += 2)
                ring.Add(new double2(xyPairs[i], xyPairs[i + 1]));
            return ring;
        }

        private static void AppendDelta(List<uint> commands, double2 point, ref long cursorX, ref long cursorY)
        {
            long x = (long)point.x;
            long y = (long)point.y;
            commands.Add(ZigZag(x - cursorX));
            commands.Add(ZigZag(y - cursorY));
            cursorX = x;
            cursorY = y;
        }

        /// <summary>Protobuf zigzag encode — the exact inverse of <c>MvtDecodeJob.ZigZag</c>.</summary>
        private static uint ZigZag(long n) => (uint)((n << 1) ^ (n >> 63));
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MvtGeometryMaterializerTests — MVT end of Waist 1's producer seam
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The MVT end of Waist 1's producer seam: what the materializer puts in the buffer, and who owns it.
    /// <c>RingCapacity</c> is the producer's sized bound; test harnesses size <c>SizingJob</c> from it.
    /// </summary>
    [TestFixture]
    public class MvtGeometryMaterializerTests
    {
        private static readonly TileId SampleTile = new TileId { Z = 8, X = 135, Y = 80 };
        private const double SampleExtent = 8192.0;

        /// <summary>
        /// The shared buffer carries <b>every</b> ring the source expresses, including rings too short
        /// to be a polygon. Fill's <c>rLen &lt; 3</c> filter belongs to <c>RingAssemblyJob</c>; a filter that
        /// crept into the shared decode stage would starve the line consumer, and a fill mesher's own output
        /// cannot see it (ring assembly re-filters, and nothing downstream reports the pre-filter ring
        /// count). So the materializer is driven directly.
        /// </summary>
        [Test]
        public void Materialize_CarriesShortRingsUnfiltered()
        {
            uint[] stream = MvtCommandStream.Feature(
                MvtCommandStream.Ring(10, 10, 20, 10),                        // 2 points
                MvtCommandStream.Ring(30, 30),                                // 1 point
                MvtCommandStream.Ring(100, 100, 200, 100, 200, 200, 100, 200) // 4 points
            );

            TileGeometryBuffers geometry =
                MaterializeFeatures(SampleTile, SampleExtent, Carrier(TileGeometryType.Polygon, stream));
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the materializer produced a buffer");
                Assert.AreEqual(3, geometry.RingCount,
                    "all three rings must survive — a < 3-point filter here starves the line consumer");
                Assert.AreEqual(7, geometry.VertexCount, "2 + 1 + 4 vertices, none dropped");

                int[] spans =
                {
                    geometry.RingOffsets[1] - geometry.RingOffsets[0],
                    geometry.RingOffsets[2] - geometry.RingOffsets[1],
                    geometry.RingOffsets[3] - geometry.RingOffsets[2],
                };
                Assert.AreEqual(new[] { 2, 1, 4 }, spans,
                    "ring order and length must be exactly as authored — this pins WHICH rings survived, " +
                    "not merely how many");

                // Non-vacuity: the surviving 4-point ring really is the authored one, and really is last.
                Assert.AreEqual(new double2(100.0, 100.0), geometry.Vertices[geometry.RingOffsets[2]],
                    "the last ring must be the 4-point ring, at its authored position");
                Assert.AreEqual(new[] { 0, 0, 0 },
                    new[] { geometry.RingFeatureIdx[0], geometry.RingFeatureIdx[1], geometry.RingFeatureIdx[2] },
                    "all three rings belong to the single authored feature");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>
        /// Ownership TRANSFERS on return: every call mints a fresh buffer, so one materializer may be
        /// materialized more than once (<c>RingClipJobTests</c> does exactly that) and disposing one result
        /// cannot touch another. A cached buffer would double-free instead.
        /// </summary>
        [Test]
        public void Materialize_TransfersOwnership_AndMintsAFreshBufferPerCall()
        {
            uint[] stream = MvtCommandStream.Feature(
                MvtCommandStream.Ring(100, 100, 200, 100, 200, 200, 100, 200));

            // The materializer borrows `flat`, so a second call still works; `flat` is disposed once at the end.
            var materializer = MakeMaterializer(
                SampleTile, SampleExtent, out var flat, Carrier(TileGeometryType.Polygon, stream));
            try
            {
                TileGeometryBuffers first  = materializer.Materialize();
                TileGeometryBuffers second = materializer.Materialize();

                // Non-vacuity: two `default` results would satisfy every claim below trivially.
                Assert.IsTrue(first.IsCreated, "the first call must mint a buffer");
                Assert.IsTrue(second.IsCreated, "the second call must mint a buffer");
                Assert.AreEqual(4, first.VertexCount, "precondition: the first buffer really holds the decode");
                Assert.AreEqual(4, second.VertexCount, "precondition: the second buffer really holds the decode");

                // Distinct allocations: writing through one must not be visible through the other.
                first.Vertices[0] = new double2(-1.0, -1.0);
                Assert.AreEqual(new double2(100.0, 100.0), second.Vertices[0],
                    "the two calls must return distinct allocations — a cached buffer would alias here and " +
                    "double-free on the second Dispose");

                first.Dispose();
                Assert.DoesNotThrow(() => { var _ = second.Vertices[0]; },
                    "disposing one result must leave the other usable");

                second.Dispose();
                Assert.DoesNotThrow(() => first.Dispose(), "re-disposing is a no-op, not a double free");
                Assert.DoesNotThrow(() => second.Dispose(), "re-disposing is a no-op, not a double free");
            }
            finally { flat.Dispose(); }

            // The empty-input exit path allocates nothing and hands back a buffer nobody has to free.
            var empty = MakeMaterializer(SampleTile, SampleExtent, out var emptyFlat);
            try
            {
                TileGeometryBuffers nothing = empty.Materialize();
                Assert.IsFalse(nothing.IsCreated, "an empty feature list must materialize to default, not to an allocation");
                Assert.DoesNotThrow(() => nothing.Dispose(), "disposing the empty result must be a no-op");
            }
            finally { emptyFlat.Dispose(); }
        }

        /// <summary>
        /// <c>RingCapacity</c> is the producer's sized upper bound, never the decoded count.
        /// <c>Schedule</c> sizes the per-polygon arrays from it, so a value that tracked
        /// <c>RingCount</c> would under-allocate the moment the two differ.
        /// </summary>
        [Test]
        public void Allocate_RingCapacity_IsTheSizedBound_NotTheDecodedCount()
        {
            var geometry = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 2, maxRings: 7, maxVertices: 30);

            Assert.AreEqual(7, geometry.RingCapacity, "RingCapacity is the maxRings Allocate was given");
            Assert.AreEqual(0, geometry.RingCount,
                "precondition: capacity and count differ here, or the test could not tell them apart");

            geometry.RingCount = 3;
            Assert.AreEqual(7, geometry.RingCapacity,
                "reporting a decoded count must not shrink the capacity the buffers were sized to");

            geometry.Dispose();

            // List-backed: a length-authoritative stage sizes exactly, so capacity IS the count.
            var vertexList  = new NativeList<double2>(4, Allocator.Persistent);
            var offsetList  = new NativeList<int>(4, Allocator.Persistent);
            var featureList = new NativeList<int>(3, Allocator.Persistent);
            for (int i = 0; i < 3; i++)
            {
                vertexList.Add(new double2(i, i));
                offsetList.Add(i);
                featureList.Add(0);
            }
            offsetList.Add(3); // trailing sentinel ⇒ 3 rings

            var kinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            var adopted = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, kinds, vertexList, offsetList, featureList);
            kinds.Dispose(); // AdoptDerivedLists COPIES the column; this one is still ours

            Assert.AreEqual(3, adopted.RingCount, "precondition: the adopted lists describe 3 rings");
            Assert.AreEqual(3, adopted.RingCapacity,
                "the list-backed mode is sized exactly, so capacity equals the count");

            adopted.Dispose();
        }

        /// <summary>
        /// The kind column is filled from <b>each feature's own declared kind</b>, never a literal.
        /// One feature list replaces two positionally-joined columns, so the two cannot desync. What still
        /// needs an observer is the kind itself: a materializer that wrote a constant <c>Polygon</c> would
        /// make every consumer's ring gate inert, and every downstream stage would keep passing.
        /// </summary>
        [Test]
        public void Materialize_FillsTheKindColumnFromEachFeature_NotALiteral()
        {
            uint[] first  = MvtCommandStream.Feature(MvtCommandStream.Ring(10, 10, 20, 10, 20, 20));
            uint[] second = MvtCommandStream.Feature(MvtCommandStream.Ring(30, 30, 40, 30, 40, 40));

            TileGeometryBuffers geometry = MaterializeFeatures(SampleTile, SampleExtent, Carrier(TileGeometryType.LineString, first), Carrier(TileGeometryType.Polygon, second));
            try
            {
                // Non-vacuity: the buffer really decoded, so the column below is not merely a zeroed
                // allocation, and the two kinds DIFFER — a literal-writing implementation could not pass.
                Assert.IsTrue(geometry.IsCreated, "precondition: the materializer produced a buffer");
                Assert.Greater(geometry.RingCount, 0, "precondition: the streams really decoded");
                Assert.AreEqual(2, geometry.FeatureCount, "the column is one entry per feature");

                Assert.AreEqual(TileGeometryType.LineString, geometry.FeatureGeometryType[0],
                    "feature 0's kind must come from the feature — a literal Polygon would show here");
                Assert.AreEqual(TileGeometryType.Polygon, geometry.FeatureGeometryType[1],
                    "…and feature 1's too, so an off-by-one or a constant fill is visible either way");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>
        /// The MVT materializer reads <see cref="IMvtGeometryCarrier"/>. A kind column whose length does not match
        /// the command column is a <b>wiring error</b> and throws before anything is allocated. A carrier whose
        /// <c>Geometry</c> is <c>null</c> is NOT an error but zero commands: line and symbol hand the
        /// materializer every selected feature, geometry or not.
        /// </summary>
        [Test]
        public void Materialize_KindColumnLengthMismatch_ThrowsBeforeAllocating_ButANullStreamIsZeroCommands()
        {
            uint[] stream = MvtCommandStream.Feature(MvtCommandStream.Ring(10, 10, 20, 10, 20, 20));

            // The kind and command columns join by POSITION. A length mismatch must throw BEFORE Allocate, or
            // four Persistent arrays strand with no owner.
            using (var flat = MvtGeometryMaterializerTestFactory.Flatten(new List<uint[]> { stream, stream })) // 2 command streams
            {
                var ex = Assert.Throws<ArgumentException>(
                    () => new MvtGeometryMaterializer(
                            SampleTile, SampleExtent,
                            new List<TileGeometryType> { TileGeometryType.Polygon },      // 1 kind
                            flat.Commands, flat.FeatureOffsets, flat.FeatureLengths).Materialize(),
                    "a kind column that does not span every feature must throw, not silently mis-classify rings");
                StringAssert.Contains("2", ex.Message,
                    "the message must name the feature count the column had to match");
            }

            // The contract's other half, in the SAME test: a carrier with no stream is zero commands.
            TileGeometryBuffers geometry = MaterializeFeatures(SampleTile, SampleExtent, Carrier(TileGeometryType.Polygon, stream), Carrier(TileGeometryType.Point, null));
            try
            {
                Assert.IsTrue(geometry.IsCreated, "a null stream must not suppress the other feature's rings");
                Assert.AreEqual(1, geometry.RingCount, "exactly the one real feature's ring");
                Assert.AreEqual(0, geometry.RingFeatureIdx[0],
                    "…still attributed to feature 0 — the null-geometry feature keeps its ordinal");
                Assert.AreEqual(TileGeometryType.Point, geometry.FeatureGeometryType[1],
                    "the geometry-less feature is still in the column, at its own position");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        // ── Fixture helpers ────────────────────────────────────────────────────────────────────────

        /// <summary>The materializer takes (tile, extent, kinds, commands) rather than a feature
        /// list. This adapter keeps the fixtures authored as features, which is still the readable
        /// shape, and splits the two columns here.
        /// It flattens the split columns via <see cref="MvtGeometryMaterializerTestFactory"/> and
        /// materializes once — the shape almost every test in this file wants.</summary>
        private static TileGeometryBuffers MaterializeFeatures(
            TileId tile, double extent, params IFeature[] features)
        {
            SplitFeatures(features, out List<TileGeometryType> kinds, out List<uint[]> commands);
            return MvtGeometryMaterializerTestFactory.Materialize(tile, extent, kinds, commands);
        }

        /// <summary>Low-level counterpart of <see cref="MaterializeFeatures"/> for a test that must hold the
        /// materializer across more than one <c>Materialize()</c> call: hands back both the materializer and
        /// the flattened input buffers it borrows, so the caller disposes <paramref name="flat"/> once, after
        /// every call that needed it has run (see <c>Materialize_TransfersOwnership_AndMintsAFreshBufferPerCall</c>).</summary>
        private static MvtGeometryMaterializer MakeMaterializer(
            TileId tile, double extent, out MvtGeometryMaterializerTestFactory.FlatGeometry flat,
            params IFeature[] features)
        {
            SplitFeatures(features, out List<TileGeometryType> kinds, out List<uint[]> commands);
            return MvtGeometryMaterializerTestFactory.Create(tile, extent, kinds, commands, out flat);
        }

        private static void SplitFeatures(
            IFeature[] features, out List<TileGeometryType> kinds, out List<uint[]> commands)
        {
            kinds    = new List<TileGeometryType>(features.Length);
            commands = new List<uint[]>(features.Length);
            foreach (IFeature f in features)
            {
                kinds.Add(f.GeometryType);
                commands.Add((f as ITileCommandStreamFeature)?.Geometry);
            }
        }

        private static IFeature Carrier(TileGeometryType kind, uint[] geometry)
            => new DictionaryFeature(properties: null, geometryType: kind, hasId: false, geometry: geometry);
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // OrdinalDomainTests — the selection/buffer relation
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The selection/buffer relation. Fill and line size per-feature arrays by <c>geometry.FeatureCount</c>,
    /// symbol by <c>tileLayer.Features.Count</c>, and all index by <see cref="SelectedTileFeature.Ordinal"/>.
    /// Non-local invariant: <c>MvtDecoder.DecodeLayer</c> appends a feature and its command stream in the
    /// same arm, so the counts move in lockstep; a bound check alone would pass a shorter sibling layer's
    /// selection. A feature with NO rings is the case that tells the two counts apart.
    /// </summary>
    [TestFixture]
    public class OrdinalDomainTests
    {
        private static readonly TileId Tile = new TileId { Z = 4, X = 3, Y = 6 };

        // ── A — a feature that contributes no rings still OWNS its ordinal ─────────────────────────────

        /// <summary>
        /// A hand-encoded MVT layer of four features, the first TWO ring-less, through the real decoder,
        /// materializer, layer and selector. Ordinal 1 has a present, empty <c>geometry</c> (MVT 2.1 §4.2
        /// conformant); ordinal 0 omits it (accepted, not conformant) and reaches the materializer as null. Each
        /// takes its own path through <c>?.Length ?? 0</c>. A compaction of ring-less features would slide
        /// later rings onto lower ordinals; no committed fixture has either shape.
        /// </summary>
        [Test]
        public void AFeatureWithNoGeometry_StillOccupiesItsOrdinalSlot()
        {
            byte[] bytes = MvtBytes.Tile(MvtBytes.Layer("places",
                MvtBytes.AttributeOnlyPointFeature(),           // ordinal 0 — geometry field ABSENT  ⇒ null
                MvtBytes.EmptyGeometryPointFeature(),           // ordinal 1 — geometry field EMPTY   ⇒ uint[0]
                MvtBytes.PointFeature(tileX: 10, tileY: 20),    // ordinal 2
                MvtBytes.PointFeature(tileX: 30, tileY: 40)));  // ordinal 3

            using MvtTile tile = MvtDecoder.Decode(Tile, bytes);
            MvtLayer layer = tile.GetLayer("places");
            Assert.IsNotNull(layer, "precondition: the hand-encoded layer must decode at all");

            // Precondition — the decoder kept both ring-less features as FEATURES. If it dropped them here
            // the two counts would agree at 2 and the clause below would be vacuous.
            Assert.AreEqual(4, layer.Features.Count,
                "precondition: all four features must survive the decode, including the two that carry no " +
                "rings — each is still a filterable, expression-evaluable record");

            TileGeometryBuffers geometry = layer.Geometry;
            Assert.AreEqual(4, geometry.FeatureCount,
                "the ordinal domain must span EVERY feature of the layer, not just the ones that produced " +
                "rings. `Ordinal` counts positions in ITileLayer.Features; FeatureGeometryType is the column " +
                "that count is joined against, and fill/line size their per-feature arrays by its length.");
            Assert.AreEqual(2, geometry.RingCount,
                "precondition: exactly the two point features produced a ring — a third would mean one of " +
                "the ring-less features is contributing geometry after all");

            // …and the ordinals are not merely COUNTED right, they ADDRESS right: the last feature's ring
            // must be filed under ordinal 3, not under the 1 a compacting decoder would give it.
            double2 atOrdinal3 = FirstVertexOfFeature(geometry, ordinal: 3);
            Assert.AreEqual(30.0, atOrdinal3.x, 1e-9,
                "the ring of the feature at ordinal 3 must be filed under RingFeatureIdx == 3. A decoder " +
                "that compacted the ring-less features away would file it under 1 — the counts would " +
                "still look plausible and every feature's paint would silently shift two neighbours over.");
            Assert.AreEqual(40.0, atOrdinal3.y, 1e-9, "…and its y");

            double2 atOrdinal2 = FirstVertexOfFeature(geometry, ordinal: 2);
            Assert.AreEqual(10.0, atOrdinal2.x, 1e-9, "…and its neighbour under ordinal 2");
            Assert.AreEqual(20.0, atOrdinal2.y, 1e-9, "…and its y");

            Assert.IsFalse(HasAnyRing(geometry, ordinal: 0),
                "…and the absent-geometry feature owns a slot with no rings in it — a slot is not a ring");
            Assert.IsFalse(HasAnyRing(geometry, ordinal: 1),
                "…nor does the empty-geometry one, which is the SPEC-CONFORMANT half of the same claim");

            // The selection walks the same list the ordinals index, so the bound the recorded finding asked
            // about falls out. Asserted through the real selector, not by arithmetic on the counts above.
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(MatchAllLayer("places"), layer, zoom: 4.0, selected);
            Assert.AreEqual(4, selected.Count, "precondition: a filter-less style layer selects every feature");
            AssertOrdinalsAddressTheBuffer(selected, geometry, "places");
        }

        // ── B — the invariant over the real corpus ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>Geometry.FeatureCount == Features.Count</c> for <b>every</b> layer of two real multi-layer fixtures,
        /// with no escape for an uncreated buffer: <see cref="MvtGeometryMaterializer"/>'s only early-out is
        /// <c>featureCount == 0 ⇒ default</c>, whose <c>FeatureCount</c> is 0 too.
        /// </summary>
        [Test]
        public void EveryRealDecodedLayer_HasOneOrdinalSlotPerFeature()
        {
            var addresses = new (TileId id, string fixture)[]
            {
                (new TileId { Z = 6, X = 32,  Y = 20  }, "water-6-32-20.pbf.bytes"),
                (new TileId { Z = 9, X = 274, Y = 168 }, "boundary-9-274-168.pbf.bytes"),
            };

            int checkedLayers = 0;
            int checkedNonEmpty = 0;
            foreach ((TileId id, string fixture) in addresses)
            {
                using MvtTile tile = MvtDecoder.Decode(id, Fixture(fixture));
                foreach (MvtLayer layer in tile.Layers)
                {
                    checkedLayers++;
                    Assert.AreEqual(layer.Features.Count, layer.Geometry.FeatureCount,
                        $"{fixture}/{layer.Name}: the buffer's per-feature column must have exactly one slot " +
                        "per feature of the layer. These are the two counts SelectedTileFeature.Ordinal is " +
                        "used against — symbol sizes by the left one, fill and line by the right one — so " +
                        "the moment they disagree the same ordinal means two different things.");

                    if (layer.Features.Count == 0) continue;
                    checkedNonEmpty++;

                    var selected = new List<SelectedTileFeature>();
                    FeatureSelector.SelectFeatures(MatchAllLayer(layer.Name), layer, id.Z, selected);
                    AssertOrdinalsAddressTheBuffer(selected, layer.Geometry, $"{fixture}/{layer.Name}");
                }
            }

            // Non-vacuity: real multi-layer tiles were really walked, not an empty layer list.
            Assert.Greater(checkedLayers, 4,
                "precondition: at least five layers across the two fixtures, or this loop asserted nothing");
            Assert.Greater(checkedNonEmpty, 3,
                "precondition: …and at least four of them carry features, so the selector arm ran");
        }

        // ── C — the pairing, at the production consumer ────────────────────────────────────────────────

        /// <summary>
        /// The real <see cref="TileMeshLayerProcessor"/>, the mesh path's only caller of
        /// <c>BuildGraphRequest</c>, hands a layer the buffer of the <b>same</b> source-layer its ordinals came
        /// from. "roads" has 2 features and "places" 5, so a wrong buffer shows as a different
        /// <c>FeatureCount</c>. Limitation: <c>SymbolFeatureExtractor.Extract</c> pairs the two as well and is
        /// not covered here.
        /// </summary>
        [Test]
        public void TheMeshProcessor_PairsASelectionWithItsOwnLayersBuffer()
        {
            byte[] bytes = MvtBytes.Tile(
                MvtBytes.Layer("roads",
                    MvtBytes.PointFeature(11, 12),
                    MvtBytes.PointFeature(13, 14)),
                MvtBytes.Layer("places",
                    MvtBytes.PointFeature(21, 22),
                    MvtBytes.PointFeature(23, 24),
                    MvtBytes.PointFeature(25, 26),
                    MvtBytes.PointFeature(27, 28),
                    MvtBytes.PointFeature(29, 30)));

            using MvtTile tile = MvtDecoder.Decode(Tile, bytes);
            Assert.AreEqual(2, tile.GetLayer("roads").Features.Count, "precondition: the two layers must differ");
            Assert.AreEqual(5, tile.GetLayer("places").Features.Count, "…in feature count, or C cannot discriminate");

            var renderLayer = new RecordingTileMeshRenderLayer(
                new StyleLayer { Id = "roads-line", Source = "s", SourceLayer = "roads" });
            var context = new TileLayerProcessContext
            {
                Tile             = Tile,
                Zoom             = 4.0,
                TileOriginRender = double3.zero,
                Projection       = new WebMercatorProjection(),
            };

            TileMeshLayerProcessor processor = TileMeshLayerProcessor.AllocateForKick(renderLayer, materialIndex: 0);
            try
            {
                processor.ProcessOnWorker(tile, in context);

                Assert.AreEqual(1, renderLayer.BuildGraphRequestCallCount,
                    "precondition: BuildGraphRequest must have been reached — an unreached layer records " +
                    "nothing and every assertion below would be comparing defaults");
                Assert.AreEqual(2, renderLayer.ObservedFeatureCount,
                    "the buffer handed to BuildGraphRequest must be the ROADS layer's (2 features), not the " +
                    "places layer's (5). The ordinals in the selection index the source-layer the style layer " +
                    "names; a buffer from any other layer makes every per-feature array a mis-attribution — " +
                    "and, when the other layer is the shorter one, an in-range and therefore silent one.");
                AssertOrdinalsAddressTheBuffer(
                    renderLayer.ObservedSelection, renderLayer.ObservedGeometry, "roads via TileMeshLayerProcessor");
            }
            finally
            {
                processor.Release();
            }
        }

        // ── Shared assertion ──────────────────────────────────────────────────────────────────────────

        private static void AssertOrdinalsAddressTheBuffer(
            IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry, string where)
        {
            Assert.Greater(selected.Count, 0, $"{where}: precondition — an empty selection asserts nothing");

            int maxOrdinal = -1;
            for (int i = 0; i < selected.Count; i++)
                if (selected[i].Ordinal > maxOrdinal) maxOrdinal = selected[i].Ordinal;

            Assert.Less(maxOrdinal, geometry.FeatureCount,
                $"{where}: every selected ordinal must be a valid index into the buffer it is used against. " +
                $"Highest ordinal {maxOrdinal}, buffer FeatureCount {geometry.FeatureCount}. Fill and line " +
                "write `featureColors[selected.Ordinal]` into an array of exactly that length.");
        }

        // ── Buffer readers ────────────────────────────────────────────────────────────────────────────

        private static double2 FirstVertexOfFeature(TileGeometryBuffers geometry, int ordinal)
        {
            for (int r = 0; r < geometry.RingCount; r++)
                if (geometry.RingFeatureIdx[r] == ordinal)
                    return geometry.Vertices[geometry.RingOffsets[r]];

            Assert.Fail(
                $"no ring is filed under ordinal {ordinal}. RingFeatureIdx must name the feature's position " +
                "in ITileLayer.Features, so a ring-bearing feature always has one. A producer that " +
                "renumbered ring→feature densely over the features that happen to carry rings would leave " +
                "the count right and the addressing one slot out — every per-feature bake landing on a " +
                $"neighbour. RingFeatureIdx over {geometry.RingCount} rings: {RingFeatureIdxOf(geometry)}");
            return default;
        }

        private static string RingFeatureIdxOf(TileGeometryBuffers geometry)
        {
            var values = new List<int>(geometry.RingCount);
            for (int r = 0; r < geometry.RingCount; r++) values.Add(geometry.RingFeatureIdx[r]);
            return string.Join(", ", values);
        }

        private static bool HasAnyRing(TileGeometryBuffers geometry, int ordinal)
        {
            for (int r = 0; r < geometry.RingCount; r++)
                if (geometry.RingFeatureIdx[r] == ordinal) return true;
            return false;
        }

        private static StyleLayer MatchAllLayer(string sourceLayer) =>
            new StyleLayer { Id = $"select-all-{sourceLayer}", Source = "s", SourceLayer = sourceLayer };

        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }

        // ── Test doubles ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Captures what the processor actually handed <c>BuildGraphRequest</c> — the selection and
        /// the buffer, the two things clause C exists to compare — and builds no request —
        /// <c>selected</c>/<c>geometry</c> are BuildGraphRequest parameters too, so recording them needs no
        /// new production observability.</summary>
        private sealed class RecordingTileMeshRenderLayer : ITileMeshRenderLayer
        {
            public int                       BuildGraphRequestCallCount { get; private set; }
            public IReadOnlyList<SelectedTileFeature> ObservedSelection { get; private set; }
            public TileGeometryBuffers       ObservedGeometry     { get; private set; }
            public int                       ObservedFeatureCount => ObservedGeometry.FeatureCount;

            public RecordingTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public StyleLayer       StyleLayer      { get; }
            public int              DrawIndex       => 0;
            public LayerSubSlot     MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material         Material        => null;
            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
            {
                BuildGraphRequestCallCount++;
                ObservedSelection = selected;
                ObservedGeometry  = geometry;
                return null;
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ProjectionManagedVersusBurstTests — RED-verified managed-vs-Burst projection parity
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class ProjectionManagedVersusBurstTests
    {
        private static readonly IProjection[] Projections =
        {
            new WebMercatorProjection(),
            new SphericalProjection(),
        };

        // ── The Burst precondition: with compilation off, the projection probes compare managed to managed ──
        // RED by hand: toggle Jobs ▸ Burst ▸ Enable Compilation off.
        [Test]
        public void Burst_IsEnabled_OrTheProjectionProbesAreVacuous()
        {
            Assert.IsTrue(BurstCompiler.Options.EnableBurstCompilation,
                "Jobs ▸ Burst ▸ Enable Compilation is OFF — TileToGeoJob/ProjectPointsJob execute their " +
                "managed IL directly under [BurstCompile], so the two probes below compare one managed " +
                "method against itself and prove nothing while this reads false.");
        }

        // ── (i) TileToGeoJob.GeoAt vs the Burst job. Projection-independent — both projections exercise the ─
        // ── same code path here, which is expected (see the file header). ────────────────────────────────
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void TileToGeo_ManagedGeoAt_MatchesBurstJob(string fixture, int z, int x, int y)
        {
            foreach (IProjection proj in Projections)
                RunTileToGeoProbe(fixture, z, x, y, proj);
        }

        private static void RunTileToGeoProbe(string fixture, int z, int x, int y, IProjection proj)
        {
            var id = new TileId { Z = z, X = x, Y = y };
            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            MvtLayer layer = tile.GetLayer("boundary");
            Assert.IsNotNull(layer, "the \"boundary\" MVT source-layer (backing the boundary_3 style layer) must be present in this fixture");

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED — owned by `tile`, not disposed here
            int n = geometry.VertexCount;
            Assert.Greater(n, 0, $"expected boundary_3 ring vertices in {fixture} ({proj.GetType().Name})");

            var tileCoords = new NativeArray<double2>(n, Allocator.TempJob);
            var burstGeo   = new NativeArray<GeoCoordinate>(n, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++) tileCoords[i] = geometry.Vertices[i];

                var managedGeo = new GeoCoordinate[n];
                for (int i = 0; i < n; i++)
                    managedGeo[i] = TileToGeoJob.GeoAt(geometry.Tile, geometry.Extent, tileCoords[i]);

                new TileToGeoJob
                {
                    Tile = geometry.Tile, Extent = geometry.Extent,
                    TileCoords = tileCoords, OutGeo = burstGeo,
                }.Run(n);

                for (int i = 0; i < n; i++)
                {
                    // Longitude is LINEAR in the inputs (a single scale + offset) — bit-exact, per the design doc's table.
                    AssertBitEqualDouble(managedGeo[i].Longitude, burstGeo[i].Longitude, fixture, proj, i, "Longitude");
                    // Latitude is downstream of atan/sinh — bounded at its measured worst case.
                    AssertUlpBound(managedGeo[i].Latitude, burstGeo[i].Latitude, LatitudeMaxUlp, fixture, proj, i, "Latitude");
                }
            }
            finally
            {
                tileCoords.Dispose();
                burstGeo.Dispose();
            }
        }

        // ── (ii) IProjection.ProjectPoint vs ProjectPointsJob<TProj>, compared as double3 ──────────────────
        // A float3 cast would hide a managed↔Burst difference below a float ulp. It schedules on the main thread.
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void ProjectPoint_Managed_MatchesProjectPointsJob(string fixture, int z, int x, int y)
        {
            foreach (IProjection proj in Projections)
                RunProjectPointProbe(fixture, z, x, y, proj);
        }

        private static void RunProjectPointProbe(string fixture, int z, int x, int y, IProjection proj)
        {
            var id = new TileId { Z = z, X = x, Y = y };
            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            MvtLayer layer = tile.GetLayer("boundary");
            Assert.IsNotNull(layer, "the \"boundary\" MVT source-layer (backing the boundary_3 style layer) must be present in this fixture");

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED
            int n = geometry.VertexCount;
            Assert.Greater(n, 0, $"expected boundary_3 ring vertices in {fixture} ({proj.GetType().Name})");

            var geo = new GeoCoordinate[n];
            for (int i = 0; i < n; i++)
                geo[i] = TileToGeoJob.GeoAt(geometry.Tile, geometry.Extent, geometry.Vertices[i]);

            // Both arms MUST use the same origin — one local, passed to both, so a mismatch here can never
            // be mistaken for the managed-vs-Burst divergence this probe exists to catch.
            double3 origin = double3.zero;

            var managedWorld = new double3[n];
            var managedUp    = new double3[n];
            for (int i = 0; i < n; i++)
            {
                ProjectedPoint pp = proj.ProjectPoint(in geo[i]);
                managedWorld[i] = pp.World - origin;
                managedUp[i]    = pp.Up;
            }

            using var points  = new NativeList<GeoCoordinate>(n, Allocator.TempJob);
            using var world   = new NativeList<double3>(n, Allocator.TempJob);
            using var normals = new NativeList<double3>(n, Allocator.TempJob);
            points.ResizeUninitialized(n);
            world.ResizeUninitialized(n);
            normals.ResizeUninitialized(n);
            NativeArray<GeoCoordinate> pointsW = points.AsArray();
            for (int i = 0; i < n; i++) pointsW[i] = geo[i];

            ProjectionDispatch.Schedule(proj, origin, points, world, normals, default).Complete();

            // The bound is per-projection AND per-component — WebMercator's World.x/y and every Up
            // component are linear (bit-exact); everything else is downstream of sin/cos or sinh/atan.
            bool mercator = proj is WebMercatorProjection;
            ulong worldXMaxUlp = mercator ? 0UL : SphericalWorldXMaxUlp;
            ulong worldYMaxUlp = mercator ? 0UL : SphericalWorldYMaxUlp;
            ulong worldZMaxUlp = mercator ? WebMercatorWorldZMaxUlp : SphericalWorldZMaxUlp;
            ulong upXMaxUlp    = mercator ? 0UL : SphericalUpXMaxUlp;
            ulong upYMaxUlp    = mercator ? 0UL : SphericalUpYMaxUlp;
            ulong upZMaxUlp    = mercator ? 0UL : SphericalUpZMaxUlp;

            for (int i = 0; i < n; i++)
            {
                AssertUlpBound(managedWorld[i].x, world[i].x,   worldXMaxUlp, fixture, proj, i, "World.x");
                AssertUlpBound(managedWorld[i].y, world[i].y,   worldYMaxUlp, fixture, proj, i, "World.y");
                AssertUlpBound(managedWorld[i].z, world[i].z,   worldZMaxUlp, fixture, proj, i, "World.z");
                AssertUlpBound(managedUp[i].x,     normals[i].x, upXMaxUlp,   fixture, proj, i, "Up.x");
                AssertUlpBound(managedUp[i].y,     normals[i].y, upYMaxUlp,   fixture, proj, i, "Up.y");
                AssertUlpBound(managedUp[i].z,     normals[i].z, upZMaxUlp,   fixture, proj, i, "Up.z");
            }
        }

        // ── The measured bounds (job-scheduling-design.md) — worst case across ───────────────────────────────
        // ── both boundary fixtures, both projections, measured 2026-09-04 against these two exact jobs. ─────
        private const ulong LatitudeMaxUlp           = 3;
        private const ulong WebMercatorWorldZMaxUlp  = 4;
        private const ulong SphericalWorldXMaxUlp    = 4;
        private const ulong SphericalWorldYMaxUlp    = 1;
        private const ulong SphericalWorldZMaxUlp    = 4;
        private const ulong SphericalUpXMaxUlp       = 2;
        private const ulong SphericalUpYMaxUlp       = 1;
        private const ulong SphericalUpZMaxUlp       = 3;

        // ── Bitwise comparison — asulong so a NaN or a signed zero cannot pass as equal. Used only for the ──
        // ── 0-ULP (linear) fields; a genuine bit divergence there is a formula error, not float noise. ─────
        private static void AssertBitEqualDouble(
            double managed, double burst, string fixture, IProjection proj, int index, string label)
        {
            ulong m = math.asulong(managed), b = math.asulong(burst);
            Assert.AreEqual(m, b,
                $"{label}[{index}] diverges (expected bit-exact, per the design doc's table) — {fixture} ({proj.GetType().Name}): " +
                $"managed=0x{m:X16} ({managed:R}) burst=0x{b:X16} ({burst:R})");
        }

        // ── ULP distance via the IEEE-754 total-order mapping (a raw bit subtraction is wrong across zero) ──
        // Positives map to the upper half of ulong, bit-inverted negatives to the lower, so |a - b| is the ULP gap.
        private static ulong ToUlpOrder(double d)
        {
            long bits = System.BitConverter.DoubleToInt64Bits(d);
            ulong ubits = unchecked((ulong)bits);
            return (ubits & 0x8000000000000000UL) != 0 ? ~ubits : (ubits | 0x8000000000000000UL);
        }

        private static ulong UlpDistance(double a, double b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }

        private static void AssertUlpBound(
            double managed, double burst, ulong maxUlp, string fixture, IProjection proj, int index, string label)
        {
            ulong delta = UlpDistance(managed, burst);
            Assert.LessOrEqual(delta, maxUlp,
                $"{label}[{index}] exceeds its measured {maxUlp}-ULP bound (job-scheduling-design.md's measured " +
                $"bounds) — {fixture} ({proj.GetType().Name}): managed=0x{math.asulong(managed):X16} " +
                $"({managed:R}) burst=0x{math.asulong(burst):X16} ({burst:R}) delta={delta} ULP");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RingAssemblyDeferredCountTests — RingOffsets deferred array resolves at EXECUTE time, not schedule time
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class RingAssemblyDeferredCountTests
    {
        /// <summary>Trivial preceding job: populates the ring-offsets list AFTER the test has already taken
        /// its <c>AsDeferredJobArray()</c> view and built the (already-scheduled) consumer job around it.</summary>
        private struct SeedRingOffsetsJob : IJob
        {
            public NativeList<int> RingOffsets;
            public void Execute()
            {
                RingOffsets.Add(0);
                RingOffsets.Add(3); // one ring spanning [0, 3) — a single triangle
            }
        }

        [Test]
        public void RingCountFromOffsetsLength_ResolvesAtExecuteTime_NotAtDeferredViewCaptureTime()
        {
            var vertices = new NativeArray<double2>(3, Allocator.Persistent);
            vertices[0] = new double2(0.0, 0.0);
            vertices[1] = new double2(10.0, 0.0);
            vertices[2] = new double2(10.0, 10.0);

            var ringFeatureIdx = new NativeArray<int>(1, Allocator.Persistent);
            ringFeatureIdx[0] = 0;
            var featureKinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            featureKinds[0] = TileGeometryType.Polygon;

            // Empty at construction time — RingAssemblyJob's deferred view is captured over THIS emptiness.
            var ringOffsetsList = new NativeList<int>(Allocator.Persistent);
            Assert.AreEqual(0, ringOffsetsList.Length, "precondition: the list is empty when the deferred view is taken");

            var polyOuterIdx  = new NativeArray<int>(1, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(1, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(1, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(1, Allocator.Persistent);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            JobHandle seedHandle = new SeedRingOffsetsJob { RingOffsets = ringOffsetsList }.Schedule();

            var assemblyJob = new RingAssemblyJob
            {
                Vertices = vertices,
                RingOffsets = ringOffsetsList.AsDeferredJobArray(), // captured while the list is still empty
                RingFeatureIdx = ringFeatureIdx,
                RingCount = -1, // wrong sentinel: proves the bool steers away from this field
                RingCountFromOffsetsLength = true,
                FeatureGeometryType = featureKinds,
                OutPolyOuterRingIdx = polyOuterIdx, OutPolyHoleListStart = polyHoleStart, OutPolyHoleCount = polyHoleCount,
                OutHoleRingIdxs = holeRingIdxs, OutPolygonCount = polyCountArr, OutHoleCount = holeCountArr,
            };

            JobHandle handle = assemblyJob.Schedule(seedHandle);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            Assert.AreEqual(1, polyCountArr[0],
                "the single seeded triangle ring must assemble into exactly one polygon — a count of 0 " +
                "means RingOffsets.Length resolved to the list's EMPTY capture-time state, not its " +
                "execute-time state after SeedRingOffsetsJob ran; a crash/garbage count means it read " +
                "RingCount (-1) instead of deriving from RingOffsets.Length.");
            Assert.AreEqual(0, holeCountArr[0], "no holes in a single-ring layer");

            vertices.Dispose(); ringFeatureIdx.Dispose(); featureKinds.Dispose(); ringOffsetsList.Dispose();
            polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose(); holeRingIdxs.Dispose();
            polyCountArr.Dispose(); holeCountArr.Dispose();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RingAssemblyHoleAttributionTests — every hole belongs to the same feature as its outer ring
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Every hole belongs to the same source-layer feature as its polygon's outer ring</b>, checked over real
    /// data. Non-local invariant: <c>FillMeshPipeline.HoleRingComparer</c> tiebreaks on ring index, so earcut's
    /// hole-bridge order is byte-identical only while a polygon's holes share its outer's feature.
    /// <c>RingAssemblyJob.Execute</c> resets <c>exteriorSign</c> on each new feature before the area test, so
    /// a feature's first surviving ring is always an outer.
    /// </summary>
    [TestFixture]
    public class RingAssemblyHoleAttributionTests
    {
        [Test]
        public void HolesShareTheirOutersFeature_AcrossTheCommittedMvtCorpus()
        {
            string[] fixtures = Directory.GetFiles(
                Path.Combine(Application.dataPath, "Fixtures"), "*.pbf.bytes");
            Assert.Greater(fixtures.Length, 0, "precondition: the committed .pbf corpus must not be empty");

            int totalPolygons          = 0;
            int totalPolygonsWithHoles = 0;
            int totalHoles             = 0;
            int layersExamined         = 0;

            foreach (string path in fixtures)
            {
                TileId tileId = TileIdFromFixtureName(Path.GetFileName(path));
                using MvtTile tile = MvtDecoder.Decode(tileId, File.ReadAllBytes(path));

                foreach (string layerName in LayerNames(tile))
                {
                    ITileLayer layer = tile.GetLayer(layerName);
                    if (layer == null) continue;

                    // The layer's whole buffer, as every consumer borrows it; the kind column keeps non-polygon
                    // features out of ring assembly, so per-feature attribution is unaffected.
                    bool anyPolygon = false;
                    foreach (IFeature f in layer.Features)
                        if (f.GeometryType == TileGeometryType.Polygon) { anyPolygon = true; break; }
                    if (!anyPolygon) continue;

                    TileGeometryBuffers geometry = layer.Geometry;
                    if (!geometry.IsCreated) continue;
                    layersExamined++;

                    try
                    {
                        AssembleAndAssert(
                            geometry, $"{Path.GetFileName(path)}/{layerName}",
                            ref totalPolygons, ref totalPolygonsWithHoles, ref totalHoles);
                    }
                    finally
                    {
                        // BORROWED from the decoded layer — the `using` on the tile frees it.
                    }
                }
            }

            // ── Anti-vacuity: the claim above must be about a NON-EMPTY set. ──────────────────────────
            // A corpus with no polygons, or no holes, would pass while saying nothing about attribution.
            Assert.Greater(layersExamined, 0, "anti-vacuity: no layer in the corpus materialized any rings");
            Assert.Greater(totalPolygons, 0, "anti-vacuity: the corpus assembled zero polygons");
            Assert.Greater(totalPolygonsWithHoles, 0,
                "anti-vacuity: the corpus assembled polygons but NONE with holes — the attribution claim " +
                "would then be about an empty set. If this fires, the corpus changed; find a hole-bearing " +
                "fixture rather than deleting the clause.");
            Assert.Greater(totalHoles, 0, "anti-vacuity: zero holes were classified");

            Debug.Log($"[hole attribution] corpus: {layersExamined} layers, {totalPolygons} polygons, " +
                      $"{totalPolygonsWithHoles} with holes, {totalHoles} holes — all holes shared their " +
                      "outer's feature.");
        }

        private static void AssembleAndAssert(
            TileGeometryBuffers geometry, string where,
            ref int totalPolygons, ref int totalPolygonsWithHoles, ref int totalHoles)
        {
            int maxPolygons = math.max(1, geometry.RingCapacity);

            var polyOuterIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent);

            try
            {
                new RingAssemblyJob
                {
                    Vertices             = geometry.Vertices,
                    RingOffsets          = geometry.RingOffsets,
                    RingFeatureIdx       = geometry.RingFeatureIdx,
                    RingCount            = geometry.RingCount,
                    FeatureGeometryType  = geometry.FeatureGeometryType,
                    OutPolyOuterRingIdx  = polyOuterIdx,
                    OutPolyHoleListStart = polyHoleStart,
                    OutPolyHoleCount     = polyHoleCount,
                    OutHoleRingIdxs      = holeRingIdxs,
                    OutPolygonCount      = polyCountArr,
                    OutHoleCount         = holeCountArr,
                }.Run();

                int polyCount = polyCountArr[0];
                totalPolygons += polyCount;

                for (int pi = 0; pi < polyCount; pi++)
                {
                    int outerFeature = geometry.RingFeatureIdx[polyOuterIdx[pi]];
                    int holeCount    = polyHoleCount[pi];
                    if (holeCount > 0) totalPolygonsWithHoles++;
                    totalHoles += holeCount;

                    for (int hi = 0; hi < holeCount; hi++)
                    {
                        int holeRi = holeRingIdxs[polyHoleStart[pi] + hi];
                        Assert.AreEqual(outerFeature, geometry.RingFeatureIdx[holeRi],
                            $"{where}: polygon {pi} (outer ring {polyOuterIdx[pi]}, feature {outerFeature}) " +
                            $"was given hole ring {holeRi}, which belongs to feature " +
                            $"{geometry.RingFeatureIdx[holeRi]}. Cross-feature hole attribution breaks the " +
                            "hole-bridge-order half of the byte-identity argument: the hole sort " +
                            "tiebreaks on ring index, which is only order-preserving within one feature.");
                    }
                }
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose();
            }
        }

        /// <summary>Every layer name the fixture actually carries — no hand-picked list, so a corpus change
        /// widens the check instead of silently narrowing it.</summary>
        private static IEnumerable<string> LayerNames(MvtTile tile)
        {
            foreach (MvtLayer layer in tile.Layers)
                yield return layer.Name;
        }

        /// <summary>The committed fixtures encode their address in the filename (<c>…-z-x-y.pbf.bytes</c>).
        /// The tile address only reaches <c>TileToGeoJob</c>, which this test does not run, but the
        /// materializer requires one and a wrong-but-consistent address would still be misleading.</summary>
        private static TileId TileIdFromFixtureName(string fileName)
        {
            string stem = fileName.Replace(".pbf.bytes", string.Empty);
            string[] parts = stem.Split('-');
            if (parts.Length >= 3 &&
                int.TryParse(parts[parts.Length - 3], out int z) &&
                int.TryParse(parts[parts.Length - 2], out int x) &&
                int.TryParse(parts[parts.Length - 1], out int y))
                return new TileId { Z = z, X = x, Y = y };

            return new TileId { Z = 0, X = 0, Y = 0 };
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RingAssemblyKindGateTests — a non-Polygon ring is never classified, whatever its area
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="RingAssemblyJob"/>'s <b>kind gate</b>: a ring whose feature is not a Polygon is never
    /// classified, whatever its area. Non-obvious why: the job has its own gate because it classifies by
    /// signed area alone, so an ungated road ring becomes a spurious exterior or hole. The caller's
    /// visit-order filter is a second guard, and it is easy to lose.
    /// </summary>
    [TestFixture]
    public class RingAssemblyKindGateTests
    {
        /// <summary>
        /// The gate, with its <b>anti-vacuity twin in the same test</b>: the SAME 4-vertex ring with a large
        /// signed area assembles to <b>1</b> polygon when its feature is a Polygon and <b>0</b> when it is a
        /// LineString. Without the positive arm this could not tell "the gate works" from "this fixture never
        /// produces a polygon at all" — the inert-injection shape this suite has produced once.
        /// </summary>
        [Test]
        public void KindGate_ALineStringRingIsNeverClassified_ButTheSameRingAsAPolygonIs()
        {
            Assert.AreEqual(0, AssembleOneRing(TileGeometryType.LineString).polygons,
                "a LineString feature's ring must NOT become a polygon — the assembler classifies by signed " +
                "area alone, so without the kind gate this road-shaped ring is read as an exterior");
            Assert.AreEqual(0, AssembleOneRing(TileGeometryType.Unknown).polygons,
                "…and an unfilled kind column reads Unknown, which must also be rejected: a producer that " +
                "forgot to fill the column renders NOTHING (loud) rather than something WRONG (silent)");

            // Anti-vacuity: the identical geometry DOES assemble when its kind says Polygon, so the zeros
            // above are the gate's doing and not the fixture's.
            Assert.AreEqual(1, AssembleOneRing(TileGeometryType.Polygon).polygons,
                "anti-vacuity: the same ring, declared a Polygon, must assemble to exactly one polygon");
        }

        /// <summary>
        /// A LineString ring BEFORE a polygon's must leave the classifier state untouched, so the polygon is still
        /// an outer with its hole. A gate placed after the sign/area work lets the LineString set the exterior
        /// sign and gives 2 polygons / 0 holes instead of 1 / 1.
        /// </summary>
        [Test]
        public void KindGate_ALineStringBeforeAPolygon_DoesNotDisturbTheClassifierState()
        {
            // Feature 0: a LineString ring wound like the outer, so if let through it steals `currentPolyIdx`.
            // Feature 1: a polygon outer + its hole, wound oppositely as the format requires.
            double2[][] rings =
            {
                Ring(3000, 3000, 3600),          // feature 0 — LineString, CCW
                Ring(0, 0, 1000),                // feature 1 — outer, CCW
                RingReversed(300, 300, 400),     // feature 1 — hole, CW
            };
            int[] ringFeature = { 0, 1, 1 };
            var kinds = new[] { TileGeometryType.LineString, TileGeometryType.Polygon };

            (int polygons, int holes, int outerRing) gated = Assemble(rings, ringFeature, kinds);

            Assert.AreEqual(1, gated.polygons,
                "exactly one polygon — the LineString must contribute none, and must not turn the real " +
                "outer into a candidate hole of itself");
            Assert.AreEqual(1, gated.holes, "…and the polygon must keep its hole");
            Assert.AreEqual(1, gated.outerRing,
                "the OUTER must be ring 1 (the polygon feature's exterior), not ring 0 — this is what a " +
                "gate placed after the sign/area work gets wrong while still reporting one polygon");

            // Anti-vacuity: declare feature 0 a Polygon and the answer really does change, so the assertions
            // above discriminate rather than restating a fixture that could only ever produce one polygon.
            (int polygons, int holes, int outerRing) ungated =
                Assemble(rings, ringFeature, new[] { TileGeometryType.Polygon, TileGeometryType.Polygon });
            Assert.AreEqual(2, ungated.polygons,
                "anti-vacuity: with feature 0 declared a Polygon the same rings assemble to TWO polygons, " +
                "so the gated result above is the gate's doing");
        }

        // ── Fixture ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A CCW square of side <paramref name="size"/> at (<paramref name="x"/>,
        /// <paramref name="y"/>) — |shoelace| far above the assembler's 1.0 degenerate threshold.</summary>
        private static double2[] Ring(double x, double y, double size) => new[]
        {
            new double2(x, y),
            new double2(x + size, y),
            new double2(x + size, y + size),
            new double2(x, y + size),
        };

        private static double2[] RingReversed(double x, double y, double size)
        {
            double2[] ring = Ring(x, y, size);
            return new[] { ring[0], ring[3], ring[2], ring[1] };
        }

        private static (int polygons, int holes, int outerRing) AssembleOneRing(TileGeometryType kind)
            => Assemble(new[] { Ring(0, 0, 1000) }, new[] { 0 }, new[] { kind });

        private static (int polygons, int holes, int outerRing) Assemble(
            double2[][] rings, int[] ringFeature, TileGeometryType[] featureKinds)
        {
            int totalVerts = 0;
            foreach (double2[] r in rings) totalVerts += r.Length;

            var verts       = new NativeArray<double2>(totalVerts, Allocator.Persistent);
            var ringOffsets = new NativeArray<int>(rings.Length + 1, Allocator.Persistent);
            var ringFeatIdx = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var kinds       = new NativeArray<TileGeometryType>(featureKinds.Length, Allocator.Persistent);

            var polyOuterIdx  = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(rings.Length, Allocator.Persistent);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent);

            try
            {
                int pos = 0;
                for (int ri = 0; ri < rings.Length; ri++)
                {
                    ringOffsets[ri] = pos;
                    ringFeatIdx[ri] = ringFeature[ri];
                    foreach (double2 v in rings[ri]) verts[pos++] = v;
                }
                ringOffsets[rings.Length] = pos;
                for (int fi = 0; fi < featureKinds.Length; fi++) kinds[fi] = featureKinds[fi];

                new RingAssemblyJob
                {
                    Vertices             = verts,
                    RingOffsets          = ringOffsets,
                    RingFeatureIdx       = ringFeatIdx,
                    RingCount            = rings.Length,
                    FeatureGeometryType  = kinds,
                    OutPolyOuterRingIdx  = polyOuterIdx,
                    OutPolyHoleListStart = polyHoleStart,
                    OutPolyHoleCount     = polyHoleCount,
                    OutHoleRingIdxs      = holeRingIdxs,
                    OutPolygonCount      = polyCountArr,
                    OutHoleCount         = holeCountArr,
                }.Run();

                return (polyCountArr[0], holeCountArr[0],
                        polyCountArr[0] > 0 ? polyOuterIdx[0] : -1);
            }
            finally
            {
                verts.Dispose(); ringOffsets.Dispose(); ringFeatIdx.Dispose(); kinds.Dispose();
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose();
            }
        }
    }
}
