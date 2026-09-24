// Tiles/FullPipelineTests.cs — fill/fill-extrusion job-graph scheduling and parity, the full decode-assemble-earcut pipeline, the jobified pipeline, profiler markers, shared-disposable sharing, and the visible-tile-selector/decode-dispatch diagnostics.
//
// The four job-graph scheduling/parity fixtures first, then the two whole-pipeline fixtures (full, jobified), then profiler markers, disposable sharing, worker-pass, tile-selector diagnostics and decode-dispatch, in roughly pipeline order.
//
// Contents:
//   FillExtrusionMeshGraphSchedulingTests  — job-scheduling-design.md, tooth (c) — every buffer FillExtrusionMeshGraph.Schedule allocates gets exactly one matching Dispose(handle) node.
//   FillMeshGraphGlobeParityTests          — The first check used to be a LIVE differential against WriteGlobeSubdivided's own call — GlobeFillSubdivideDispatch.Run over…
//   FillMeshGraphParityTests               — This tooth is RED-verified by perturbing the aggregate's index rebase by one and by dropping the gather's hole-ring tie-break — executed manually, one at a time, and reverted; not left in this file.
//   FillMeshGraphSchedulingTests           — job-scheduling-design.md: not-completed-at-return, and scratch/output dispose-balance.
//   FullPipelineTests                      — Full-pipeline headless test: decode → assemble → earcut over all 239 country features.
//   JobifiedPipelineTests                  — Parity and integration tests for the jobified decode + mesh pipeline.
//   ProfilerMarkerTests                    — ProfilerRecorder notes:   - Use ProfilerCategory.Scripts to match the explicit category on each ProfilerMarker constructor.
//   SharedDisposableSharingTests           — Acceptance teeth, carried onto the reference-counted SharedDisposable{T}: the mesh and symbol cadences of ONE kick observe the SAME IDecodedTile instance, in either arrival order.
//   TileSymbolWorkerPassTests              — proves RunSymbolWorkerPass decodes the fetched bytes exactly once, shares that same IDecodedTile reference across every processor in dense order, runs NO tail (the tail is the caller's main-thread step), rejects a…
//   VisibleTileSelectorDiagnosticTests     — Hard gates: (3) must equal (2) tile-for-tile (proves the engine-free frustum matches the real camera — the linchpin).
//   WorkSchedulerDecodeDispatchTests       — Stage-1 acceptance: TileDecodeDispatch.DecodeAsync driven directly with an injected IWorkScheduler, over the real committed MVT fixture.

using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Unity.Rendering.Meshing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Projection;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Tests.Meshing;
using MapRenderer.Core.Expressions;
using System.Collections;
using Cysharp.Threading.Tasks;
using UnityEngine.TestTools;
using Unity.Profiling;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using TileDecodeDispatch   = MapRenderer.Unity.Rendering.Tile.Processing.TileDecodeDispatch;
using MvtDecoder           = MapRenderer.Jobs.Mvt.MvtDecoder;
using FillRenderLayer      = MapRenderer.Unity.Rendering.Style.FillRenderLayer;
using LineRenderLayer      = MapRenderer.Unity.Rendering.Style.LineRenderLayer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using System.Threading;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Jobs.Tiles;
using System.Linq;
using System.Text;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;
using System.Threading.Tasks;
using MapRenderer.Unity.Concurrency;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Tiles
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // FillExtrusionMeshGraphSchedulingTests — every buffer allocated gets exactly one matching Dispose
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillExtrusionMeshGraphSchedulingTests
    {
        private static TileGeometryBuffers SingleTrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingOffsets[0] = 0;
            g.Vertices[0] = new double2(0, 0);
            g.Vertices[1] = new double2(10, 0);
            g.Vertices[2] = new double2(10, 10);
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[1] = 3;
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        /// <summary>Pairs <see cref="FillExtrusionGraphOutput.DebugBuffersAllocated"/> with
        /// <see cref="FillExtrusionGraphOutput.DebugBufferDisposeNodes"/>, the idiom <c>FillGraphOutput</c> and
        /// <c>LineGraphOutput</c> carry. A worker-side <c>Dispose(handle)</c> node cannot decrement a managed
        /// counter, so balance is observable only as this pairing. The statics are process-wide and monotonic, so a
        /// non-vacuity clause proves nothing; RED-verify by deleting one <c>ScheduleDispose</c> call.</summary>
        /// <param name="clipped"><c>true</c> runs the clip arm and its dispose nodes. It adds no counted buffer,
        /// and on this in-tile fixture it takes <c>RingClipJob</c>'s verbatim fast path.</param>
        [Test]
        public void Dispose_BufferDisposeNodesPairWithAllocations([Values(false, true)] bool clipped)
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = default;
            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake = default;
            try
            {
                visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
                colors = new NativeArray<Vector4>(geometry.FeatureCount, Allocator.Persistent);
                bake   = new NativeArray<Vector2>(geometry.FeatureCount, Allocator.Persistent);
                var input = new FillMeshPipeline.LayerInput
                {
                    Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                    Projection = new WebMercatorProjection(),
                    Clip = clipped ? TileBufferClip.KeepTileUnits(0.0) : TileBufferClip.Disabled,
                };

                long allocBefore    = FillExtrusionGraphOutput.DebugBuffersAllocated;
                long disposedBefore = FillExtrusionGraphOutput.DebugBufferDisposeNodes;

                FillExtrusionGraphOutput ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                JobHandle.ScheduleBatchedJobs();
                ext.Dispose(); // completes internally (Handle.Complete()), then frees Roof + Walls

                long allocAfter    = FillExtrusionGraphOutput.DebugBuffersAllocated;
                long disposedAfter = FillExtrusionGraphOutput.DebugBufferDisposeNodes;
                Assert.AreEqual(allocAfter - allocBefore, disposedAfter - disposedBefore,
                    "every scratch NativeList this call allocated must get exactly one Dispose(handle) node");
            }
            finally
            {
                if (visitOrder.IsCreated) visitOrder.Dispose();
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                geometry.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshGraphGlobeParityTests — the defect shape, extended to the globe arm
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillMeshGraphGlobeParityTests
    {
        private static readonly Regex TileIdFromName = new Regex(@"-(\d+)-(\d+)-(\d+)\.pbf\.bytes$");

        // ── (a) Golden parity of the subdivided output, over the whole fixture corpus. ─────

        // Frozen golden of the retired synchronous subdivide over FillMeshPipeline.Schedule output. It
        // includes the degenerate-candidate ear-predicate fix in EarcutJob.PointInTriangle.
        private const string FrozenGoldens =
            "TriangleStream=rGzWbOAGcl9bEQyzuS336mSlkYFucl2qqTZO2cO5zdk=";

        [Test]
        public void Schedule_MatchesFrozenSubdivideGoldens_AcrossCorpus()
        {
            string fixturesDir = Path.Combine(Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var fixturesCovered = new HashSet<string>();
            int layersChecked = 0;
            bool anySplitFired = false;
            var vertBytes = new List<byte>();

            foreach (string path in allPaths)
            {
                string fileName = Path.GetFileName(path);
                FileAssert.Exists(path);
                byte[] bytes = File.ReadAllBytes(path);

                TileId tileId;
                Match m = TileIdFromName.Match(fileName);
                tileId = m.Success
                    ? new TileId { Z = int.Parse(m.Groups[1].Value), X = int.Parse(m.Groups[2].Value), Y = int.Parse(m.Groups[3].Value) }
                    : new TileId { Z = 0, X = 0, Y = 0 };

                using var mvtTile = MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    TileGeometryBuffers geometry = layer.Geometry;
                    if (!HasPolygonFeature(geometry)) continue;

                    layersChecked++;
                    fixturesCovered.Add(fileName);

                    var (bMin, _) = tileId.MercatorBounds();
                    NativeArray<int> visitOrder = MapRenderer.Tests.TestTileMeshBuilder.FullVisitOrder(geometry);
                    var input = new FillMeshPipeline.LayerInput
                    {
                        Geometry       = geometry,
                        RingVisitOrder = visitOrder,
                        OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                        Projection     = new SphericalProjection(),
                    };

                    // Arm 1: clip disabled (default) — what this sweep drove before.
                    input.Clip = default;
                    if (Accumulate(input, vertBytes))
                        anySplitFired = true;

                    // Arm 2: clip ENABLED, the arm the shipped globe scene takes (FillTileBufferClip 0.0 decodes to
                    // KeepTileUnits(0.0)). 64 makes TryWindow enter; assert the arm is ENTERED, not only configured.
                    input.Clip = TileBufferClip.KeepTileUnits(64.0);
                    Assert.IsTrue(input.Clip.TryWindow(geometry.Extent, out _, out _),
                        $"precondition: [{fileName}/{layer.Name}] the enabled clip arm must actually enter TryWindow");
                    if (Accumulate(input, vertBytes))
                        anySplitFired = true;

                    visitOrder.Dispose();
                }
            }

            Assert.GreaterOrEqual(fixturesCovered.Count, 4,
                "precondition: the corpus sweep must cover at least 4 fixtures (guards a path/glob typo)");
            Assert.Greater(layersChecked, 0, "precondition: the enumerated (fixture, layer) set is non-empty");
            Assert.IsTrue(anySplitFired,
                "at least one layer must genuinely subdivide (subdivided triangle count exceeds source " +
                "triangle count) — the pass-through path already emits 3 verts/triangle with no dedup, so a " +
                "sweep that never exceeds that ratio would be measuring the no-op path only");

            string result = $"TriangleStream={Sha256(vertBytes)}";
            Assert.AreEqual(FrozenGoldens, result,
                "the graph's curved-arm subdivided output across the corpus no longer matches the frozen " +
                "GlobeFillSubdivideDispatch.Run/FillMeshPipeline.Schedule goldens — a real regression, not a re-bake candidate.");
        }

        /// <summary>Schedules the curved-arm graph over <paramref name="input"/>, appends its de-indexed vertex
        /// stream in the caller's fixed corpus order, and returns whether it subdivided past the source triangle
        /// count. The source count comes from a flat-arm Schedule over the same geometry: triangulation runs before
        /// the arm split (job-scheduling-design.md), so the flat <c>TriangleIndices.Length</c> is that count.
        /// Neither <see cref="FillGraphOutput"/> nor <see cref="FillGraphCounts"/> carries it.</summary>
        private static bool Accumulate(FillMeshPipeline.LayerInput input, List<byte> vertBytes)
        {
            // Both builds suppress the boundary band: the digest pins subdivision without it, and the flat build
            // stays a valid source-triangle oracle. GlobeFillBandTests and GlobeFillBandRenderTests cover the band.
            input.SuppressBoundaryBand = true;
            FillMeshPipeline.LayerInput flatInput = input;
            flatInput.Projection = new WebMercatorProjection();
            FillGraphOutput flatOutput = FillMeshGraph.Schedule(flatInput);
            flatOutput.Handle.Complete();
            int sourceTriCount = flatOutput.IsCreated ? flatOutput.TriangleIndices.Length / 3 : 0;
            flatOutput.Dispose();

            FillGraphOutput graphOutput = FillMeshGraph.Schedule(input);
            graphOutput.Handle.Complete();
            try
            {
                if (!graphOutput.IsCreated) return false; // nothing to draw

                int subdividedTriCount = graphOutput.TriangleIndices.Length / 3;
                bool split = subdividedTriCount > sourceTriCount;

                // DE-INDEXED: walk the index buffer and dereference. Vertex sharing moves storage order and index
                // values between identical vertices; the triangle stream a reader sees does not move.
                for (int i = 0; i < graphOutput.TriangleIndices.Length; i++)
                {
                    int vi = graphOutput.TriangleIndices[i];
                    double3 world = graphOutput.WorldPositions[vi], up = graphOutput.VertexUp[vi], east = graphOutput.VertexEast[vi];
                    double2 tile  = graphOutput.TileVertices[vi];
                    vertBytes.AddRange(BitConverter.GetBytes(world.x)); vertBytes.AddRange(BitConverter.GetBytes(world.y)); vertBytes.AddRange(BitConverter.GetBytes(world.z));
                    vertBytes.AddRange(BitConverter.GetBytes(up.x));    vertBytes.AddRange(BitConverter.GetBytes(up.y));    vertBytes.AddRange(BitConverter.GetBytes(up.z));
                    vertBytes.AddRange(BitConverter.GetBytes(east.x));  vertBytes.AddRange(BitConverter.GetBytes(east.y));  vertBytes.AddRange(BitConverter.GetBytes(east.z));
                    vertBytes.AddRange(BitConverter.GetBytes(tile.x));  vertBytes.AddRange(BitConverter.GetBytes(tile.y));
                    vertBytes.AddRange(BitConverter.GetBytes(graphOutput.VertexFeatureIdx[vi]));
                }

                return split;
            }
            finally { graphOutput.Dispose(); }
        }

        // ── (b) The vertex budget still binds, through the SCHEDULED dispatcher. ───────────────────────────

        /// <summary>Mirrors <c>GlobeFillSubdividerTests.Budget_BoundsTheOutput_NoLowZoomExplosion</c> through
        /// <c>GlobeFillSubdivideDispatch.Schedule</c>, the dispatcher <c>FillMeshGraph</c> uses, at a small budget.
        /// <c>FillMeshGraph.Schedule</c> has no budget parameter (it reads the <c>Default*</c> constants), so the
        /// test drives the dispatcher directly. The default is not headroom; its measurement lives on
        /// <c>GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices</c>.</summary>
        [Test]
        public void Budget_StillBinds_ThroughTheScheduledDispatcher()
        {
            const double extent = 4096.0;
            var id = new TileId { Z = 0, X = 0, Y = 0 };

            var tileVertsList = new NativeList<double2>(Allocator.Persistent);
            var triangleIndicesList = new NativeList<int>(Allocator.Persistent);
            var vertexFeatureIdxList = new NativeList<int>(Allocator.Persistent);
            tileVertsList.Add(new double2(0, 0)); tileVertsList.Add(new double2(extent, 0)); tileVertsList.Add(new double2(0, extent));
            triangleIndicesList.Add(0); triangleIndicesList.Add(1); triangleIndicesList.Add(2);
            vertexFeatureIdxList.Add(0); vertexFeatureIdxList.Add(0); vertexFeatureIdxList.Add(0);
            var vertexBandList = new NativeList<float3>(3, Allocator.Persistent);
            vertexBandList.Resize(3, NativeArrayOptions.ClearMemory);

            var outVerts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            var outIndices = new NativeList<int>(64, Allocator.Persistent);

            try
            {
                // depth 8 unbounded ≈ 4^8·3 ≈ 196k verts for ONE whole-globe triangle; the 2 000 budget must cap it.
                JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                    new SphericalProjection(), tileVertsList, triangleIndicesList, vertexFeatureIdxList, vertexBandList,
                    id, extent, double3.zero,
                    GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, 8, 2000,
                    GlobeFillSubdivideDispatch.DefaultMaxTotalVertices,
                    outVerts, outIndices, default);
                JobHandle.ScheduleBatchedJobs();
                handle.Complete();

                Assert.Less(outVerts.Length, 20000, "the vertex budget must prevent the low-zoom subdivision explosion");
                Assert.Greater(outVerts.Length, 0, "the layer must still produce output even when bounded");
            }
            finally
            {
                tileVertsList.Dispose(); triangleIndicesList.Dispose(); vertexFeatureIdxList.Dispose();
                vertexBandList.Dispose();
                outVerts.Dispose(); outIndices.Dispose();
            }
        }

        // ── Shared helpers ─────────────────────────────────────────────────────────────────────────────

        private static bool HasPolygonFeature(TileGeometryBuffers geometry)
        {
            for (int fi = 0; fi < geometry.FeatureCount; fi++)
                if (geometry.FeatureGeometryType[fi] == TileGeometryType.Polygon) return true;
            return false;
        }

        private static string Sha256(List<byte> bytes)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshGraphParityTests — RED-verified by perturbing the aggregate index rebase, by hand
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillMeshGraphParityTests
    {
        // Matches "<...>-<z>-<x>-<y>.pbf.bytes" — every committed fixture's naming convention.
        internal static readonly Regex TileIdFromName = new Regex(@"-(\d+)-(\d+)-(\d+)\.pbf\.bytes$");

        // The two real projections ProjectionDispatch.Schedule's switch enumerates. A null projection throws there
        // (TileManager resolves the default); ProjectionDispatch_Schedule_RejectsNull pins that.
        internal static readonly IProjection[] ProjectionCases = { new WebMercatorProjection(), new SphericalProjection() };

        private static string ProjectionName(IProjection p) => p.GetType().Name;

        // ── Frozen goldens of the retired FillMeshPipeline.Schedule. They include the degenerate-candidate
        // ear-predicate fix in EarcutJob.PointInTriangle.
        internal const string FrozenGoldensMercator =
            "Vertex=abw1RVTEKTX0q2SAH1frMmR5klneemBfwyLltWhcC9Q= World=ix/06MAQIOpWn5DSnMXHtCoVJJOMR5ZlDZ1RPmZ4jos= " +
            "Up=ewzYSZfuaKsiX83I/hDgjTGtdNN/hOAoc/9I01wENsQ= FeatIdx=9LQBKkxJtx4HO+DNPANmTtZ4ZXhWRF4muz+8lUw4UQw= " +
            "Indices=ibY64qOJPV0HG+vpmH423n2jBLqjxYDgNT6EoQxbnMs= PolyCount=GPT25e2QoKuZlvyLJr217Hg+BzNu4tsF/xTSGD8EEno= " +
            "RingCount=dQChKlRF3Q9rhXNaIn6SihAPsssTMDHD5yXoYuxCvLE= HoleCount=aerOkFJ48oBOi7mWf7Zq9gT/lhB5U+vlf3fz+yjaZeQ= " +
            "ForceClip=Vpd9xrW9d4LB+WVE++TrZdW/iYFu85X89YP3j2yGX7M=";
        // Re-captured for the same cause as FrozenGoldensMercator above.
        internal const string FrozenGoldensSpherical =
            "PolyCount=GPT25e2QoKuZlvyLJr217Hg+BzNu4tsF/xTSGD8EEno= RingCount=dQChKlRF3Q9rhXNaIn6SihAPsssTMDHD5yXoYuxCvLE= " +
            "HoleCount=aerOkFJ48oBOi7mWf7Zq9gT/lhB5U+vlf3fz+yjaZeQ= ForceClip=Vpd9xrW9d4LB+WVE++TrZdW/iYFu85X89YP3j2yGX7M=";

        /// <summary>Walks (corpus fixture × 2 clip arms) + 1 synthetic hole layer in the fixed order the goldens of
        /// <see cref="Schedule_MatchesFrozenSynchronousPipelineGoldens_AcrossCorpusAndSyntheticLayer"/> pin.
        /// <c>GraphDeterminismTests</c> shares it, so the two input sequences cannot drift apart. It asserts the
        /// corpus-coverage preconditions (at least 4 fixtures, at least one layer) once for every caller.</summary>
        /// <param name="visit">Receives each case's <see cref="FillMeshPipeline.LayerInput"/>, in order.</param>
        internal static void WalkCorpusAndSynthetic(IProjection projection, Action<FillMeshPipeline.LayerInput> visit)
        {
            string fixturesDir = Path.Combine(Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var fixturesCovered = new HashSet<string>();
            int layersChecked = 0;

            foreach (string path in allPaths)
            {
                string fileName = Path.GetFileName(path);
                FileAssert.Exists(path);
                byte[] bytes = File.ReadAllBytes(path);

                TileId tileId;
                Match m = TileIdFromName.Match(fileName);
                tileId = m.Success
                    ? new TileId { Z = int.Parse(m.Groups[1].Value), X = int.Parse(m.Groups[2].Value), Y = int.Parse(m.Groups[3].Value) }
                    : new TileId { Z = 0, X = 0, Y = 0 };

                using var mvtTile = MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    TileGeometryBuffers geometry = layer.Geometry;
                    if (!HasPolygonFeature(geometry)) continue;

                    layersChecked++;
                    fixturesCovered.Add(fileName);

                    var (bMin, _) = tileId.MercatorBounds();
                    NativeArray<int> visitOrder = MapRenderer.Tests.TestTileMeshBuilder.FullVisitOrder(geometry);
                    var baseInput = new FillMeshPipeline.LayerInput
                    {
                        Geometry       = geometry,
                        RingVisitOrder = visitOrder,
                        OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                        Projection     = projection,
                    };

                    // Arm 1: clip disabled (default) — every prior version of this tooth ran only this arm.
                    baseInput.Clip = default;
                    visit(baseInput);

                    // Arm 2: clip ENABLED, the arm production takes (TileBufferClip.FromInspectorUnits decodes 0.0 to
                    // KeepTileUnits(0.0)). Assert the arm truly enters TryWindow, not only that Clip is configured.
                    baseInput.Clip = TileBufferClip.KeepTileUnits(64.0);
                    Assert.IsTrue(baseInput.Clip.TryWindow(geometry.Extent, out _, out _),
                        $"precondition: [{fileName}/{layer.Name}] the enabled clip arm must actually enter TryWindow");
                    visit(baseInput);

                    visitOrder.Dispose();
                }
            }

            Assert.GreaterOrEqual(fixturesCovered.Count, 4,
                "precondition: the corpus sweep must cover at least 4 fixtures (guards a path/glob typo)");
            Assert.Greater(layersChecked, 0, "precondition: the enumerated (fixture, layer) set is non-empty");

            // ── Synthetic hole-bearing layer — guarantees a >=2-hole polygon regardless of the corpus. Last
            // in accumulation order, fixed — see the callers' own docs. ─────────────────────────────────────
            TileGeometryBuffers synthetic = SyntheticHoleBearingLayer(new TileId { Z = 0, X = 0, Y = 0 });
            try
            {
                Assert.IsTrue(AnyPolygonHasAtLeastTwoHoles(synthetic), "precondition: the synthetic layer has a >=2-hole polygon");

                NativeArray<int> visitOrder = MapRenderer.Tests.TestTileMeshBuilder.FullVisitOrder(synthetic);
                var input = new FillMeshPipeline.LayerInput
                {
                    Geometry       = synthetic,
                    RingVisitOrder = visitOrder,
                    OriginRender   = new double3(0.0, 0.0, 0.0),
                    Projection     = projection,
                };
                visit(input);
                visitOrder.Dispose();
            }
            finally { synthetic.Dispose(); }
        }

        [Test]
        public void Schedule_MatchesFrozenSynchronousPipelineGoldens_AcrossCorpusAndSyntheticLayer(
            [ValueSource(nameof(ProjectionCases))] IProjection projection)
        {
            bool curved = !double.IsInfinity(projection.MaxRefineAngleRad);

            var vertexBytes = new List<byte>(); var worldBytes = new List<byte>(); var upBytes = new List<byte>();
            var featBytes = new List<byte>(); var idxBytes = new List<byte>();
            var polyBytes = new List<byte>(); var ringBytes = new List<byte>(); var holeBytes = new List<byte>(); var fcBytes = new List<byte>();

            // Accumulates one case's per-stream bytes, in the SAME fixed order WalkCorpusAndSynthetic visits
            // cases — the golden is a running digest over that order, so the order itself is part of the pin.
            void Accumulate(FillMeshPipeline.LayerInput input)
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                output.Handle.Complete();
                try
                {
                    // Both walk arms pass only layers with a polygon feature, so RingVisitOrder is non-empty and
                    // the output is created. An uncreated output here means that precondition broke; fail on it.
                    Assert.IsTrue(output.IsCreated, "precondition: Accumulate's callers guarantee non-empty geometry");
                    polyBytes.AddRange(BitConverter.GetBytes(output.Counts[0].PolygonCount));
                    ringBytes.AddRange(BitConverter.GetBytes(output.Counts[0].RingCount));
                    holeBytes.AddRange(BitConverter.GetBytes(output.Counts[0].HoleCount));
                    fcBytes.AddRange(BitConverter.GetBytes(output.Counts[0].ForceClipCount));
                    if (curved) return; // The curved arm's five geometry arrays are POST-subdivision —
                                         // not the same quantity FillMeshPipeline.Schedule returned; never pinned here.

                    // The interior prefix only: FillBandJob appends the band as a vertex suffix, and the goldens pin
                    // the interior. Band triangles interleave per feature, so they are filtered by vertex index.
                    int vc = output.TileVertices.Length - output.Counts[0].BandVertexCount;
                    int ic = output.TriangleIndices.Length;
                    Assert.GreaterOrEqual(vc, 0, "the band cannot claim more vertices than the layer has");
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

            WalkCorpusAndSynthetic(projection, Accumulate);

            string countsResult =
                $"PolyCount={Sha256(polyBytes)} RingCount={Sha256(ringBytes)} HoleCount={Sha256(holeBytes)} ForceClip={Sha256(fcBytes)}";
            if (curved)
            {
                Assert.AreEqual(FrozenGoldensSpherical, countsResult,
                    $"[proj={ProjectionName(projection)}] the graph's counts across the corpus + synthetic layer " +
                    "no longer match the frozen FillMeshPipeline.Schedule goldens — a real regression, not a re-bake candidate.");
                return;
            }

            string geometryResult =
                $"Vertex={Sha256(vertexBytes)} World={Sha256(worldBytes)} Up={Sha256(upBytes)} FeatIdx={Sha256(featBytes)} " +
                $"Indices={Sha256(idxBytes)} {countsResult}";
            Assert.AreEqual(FrozenGoldensMercator, geometryResult,
                $"[proj={ProjectionName(projection)}] the graph's output across the corpus + synthetic layer no " +
                "longer matches the frozen FillMeshPipeline.Schedule goldens — a real regression, not a re-bake candidate.");
        }

        // ── (e) Projection dispatch coverage — job-scheduling-design.md. ────────────────────────────────────

        /// <summary>Tests null rejection on <c>ProjectionDispatch.Schedule</c> alone, with no graph. A null that
        /// reached the dispatch inside <c>FillMeshGraph.Schedule</c> would strand the jobs already scheduled on the
        /// caller's geometry, and the caller's cleanup would then throw a misleading fault. That is why
        /// <c>FillMeshGraph.Schedule</c> guards null up front.</summary>
        [Test]
        public void ProjectionDispatch_Schedule_RejectsNull()
        {
            // The null check is the FIRST statement in Schedule's switch, before any list is touched, so
            // this needs no allocation and no cleanup — nothing is ever scheduled.
            var ex = Assert.Throws<System.NotSupportedException>(
                () => ProjectionDispatch.Schedule(null, double3.zero, default, default, default, default),
                "ProjectionDispatch.Schedule must reject a null projection outright.");
            Assert.That(ex.Message, Does.Contain("null"),
                "the exception must name WHY it was rejected (a null projection), not just that dispatch " +
                "failed for some unspecified reason.");
        }

        /// <summary>Checks the dispatch reaches the spherical struct, not coverage breadth; one small layer is
        /// enough. The origin is non-zero because World is origin-relative, so a zero origin leaves the sphere
        /// check vacuous.</summary>
        [Test]
        public void ProjectionDispatch_SphericalArmIsEntered()
        {
            TileGeometryBuffers geometry = SyntheticHoleBearingLayer(new TileId { Z = 3, X = 3, Y = 3 });
            try
            {
                NativeArray<int> visitOrder = MapRenderer.Tests.TestTileMeshBuilder.FullVisitOrder(geometry);
                try
                {
                    var origin = new double3(1000.0, 0.0, 2000.0);
                    var baseInput = new FillMeshPipeline.LayerInput
                    {
                        Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = origin,
                    };
                    var mercatorInput = baseInput; mercatorInput.Projection = new WebMercatorProjection();
                    var sphericalInput = baseInput; sphericalInput.Projection = new SphericalProjection();

                    FillGraphOutput mercatorOut = default, sphericalOut = default;
                    try
                    {
                        mercatorOut = FillMeshGraph.Schedule(mercatorInput);
                        sphericalOut = FillMeshGraph.Schedule(sphericalInput);
                        mercatorOut.Handle.Complete(); sphericalOut.Handle.Complete();

                        string mercatorHash  = HashDouble3ArrayFromList(mercatorOut.WorldPositions, mercatorOut.WorldPositions.Length);
                        string sphericalHash = HashDouble3ArrayFromList(sphericalOut.WorldPositions, sphericalOut.WorldPositions.Length);

                        // Precondition: an empty spherical arm would make AreNotEqual below fail for an
                        // uninformative reason (two empty-list hashes match).
                        int n = sphericalOut.WorldPositions.Length;
                        Assert.Greater(n, 0, "precondition: the spherical arm produced vertices");

                        // The spherical branch was ENTERED, not merely configured.
                        Assert.AreNotEqual(mercatorHash, sphericalHash,
                            "the spherical arm's world positions must differ from the Mercator arm's — a " +
                            "dispatch that silently fell through to Web Mercator would still pass this check");

                        for (int i = 0; i < n; i++)
                        {
                            double3 absolute = sphericalOut.WorldPositions[i] + origin;
                            Assert.That(math.length(absolute), Is.EqualTo(SphericalProjection.Radius).Within(SphericalProjection.Radius * 1e-6),
                                $"vertex {i}: every absolute spherical vertex must lie on the sphere of SphericalProjection.Radius");
                        }
                    }
                    finally
                    {
                        mercatorOut.Dispose(); sphericalOut.Dispose();
                    }
                }
                finally { visitOrder.Dispose(); }
            }
            finally { geometry.Dispose(); }
        }

        // ── Synthetic layer builder — TileGeometryBuffers.Allocate + element writes (no MVT authoring). ────

        /// <summary>One outer square + three holes at distinct leftmost-x (10, 40, 70), all strictly inside
        /// the outer ring — exercises <c>FillGatherJob</c>'s hole sort (holeCount &gt; 1).</summary>
        private static TileGeometryBuffers SyntheticHoleBearingLayer(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 4, maxVertices: 16);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;

            int vi = 0, ri = 0;
            void Ring(double2 a, double2 b, double2 c, double2 d)
            {
                g.RingOffsets[ri] = vi;
                g.Vertices[vi++] = a; g.Vertices[vi++] = b; g.Vertices[vi++] = c; g.Vertices[vi++] = d;
                g.RingFeatureIdx[ri] = 0;
                ri++;
            }

            // Outer: CW-in-Y-down (positive shoelace area2) — the MVT exterior sign.
            Ring(new double2(0, 0), new double2(100, 0), new double2(100, 100), new double2(0, 100));
            // Holes: opposite sign (negative area2), fully inside the outer square, distinct leftmost-x.
            Ring(new double2(10, 10), new double2(10, 20), new double2(20, 20), new double2(20, 10));
            Ring(new double2(40, 10), new double2(40, 20), new double2(50, 20), new double2(50, 10));
            Ring(new double2(70, 10), new double2(70, 20), new double2(80, 20), new double2(80, 10));
            g.RingOffsets[ri] = vi; // trailing sentinel

            g.RingCount   = ri;
            g.VertexCount = vi;
            return g;
        }

        // ── Corpus predicates ──────────────────────────────────────────────────────────────────────────

        internal static bool HasPolygonFeature(TileGeometryBuffers geometry)
        {
            for (int fi = 0; fi < geometry.FeatureCount; fi++)
                if (geometry.FeatureGeometryType[fi] == TileGeometryType.Polygon) return true;
            return false;
        }

        /// <summary>Reads <c>RingAssemblyJob</c>'s own per-polygon hole count directly (a LAYER total from
        /// <see cref="FillGraphCounts.HoleCount"/> cannot distinguish "many 1-hole polygons" from "one 3-hole
        /// polygon" — only this per-polygon read can).</summary>
        private static bool AnyPolygonHasAtLeastTwoHoles(TileGeometryBuffers geometry)
        {
            if (!geometry.IsCreated || geometry.RingCount == 0) return false;

            int maxPolygons = math.max(1, geometry.RingCapacity);
            var polyOuterIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleStart = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleCount = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var holeRingIdxs  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            new RingAssemblyJob
            {
                Vertices = geometry.Vertices, RingOffsets = geometry.RingOffsets, RingFeatureIdx = geometry.RingFeatureIdx,
                RingCount = geometry.RingCount, FeatureGeometryType = geometry.FeatureGeometryType,
                OutPolyOuterRingIdx = polyOuterIdx, OutPolyHoleListStart = polyHoleStart, OutPolyHoleCount = polyHoleCount,
                OutHoleRingIdxs = holeRingIdxs, OutPolygonCount = polyCountArr, OutHoleCount = holeCountArr,
            }.Run();

            bool result = false;
            int polyCount = polyCountArr[0];
            for (int pi = 0; pi < polyCount; pi++)
                if (polyHoleCount[pi] > 1) { result = true; break; }

            polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
            holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose();
            return result;
        }

        // ── Hashing ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Used only by <see cref="ProjectionDispatch_SphericalArmIsEntered"/> — the corpus sweep
        /// above accumulates its own per-stream bytes inline (<c>Accumulate</c>) rather than hashing per
        /// case, since its golden is one running digest over the whole corpus.</summary>
        private static string HashDouble3ArrayFromList(NativeList<double3> list, int count)
        {
            var bytes = new List<byte>(count * 24);
            for (int i = 0; i < count; i++)
            {
                bytes.AddRange(BitConverter.GetBytes(list[i].x));
                bytes.AddRange(BitConverter.GetBytes(list[i].y));
                bytes.AddRange(BitConverter.GetBytes(list[i].z));
            }
            return Sha256(bytes);
        }

        internal static string Sha256(List<byte> bytes)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshGraphSchedulingTests — not-completed-at-return, and scratch/output dispose-balance
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillMeshGraphSchedulingTests
    {
        private static TileGeometryBuffers SingleTrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingOffsets[0] = 0;
            g.Vertices[0] = new double2(0, 0);
            g.Vertices[1] = new double2(10, 0);
            g.Vertices[2] = new double2(10, 10);
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[1] = 3;
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        /// <summary>A whole-tile triangle (unlike <see cref="SingleTrianglePolygon"/>'s tiny sliver) — large
        /// enough in angular extent that <see cref="SphericalProjection"/> genuinely subdivides it, so the
        /// curved arm's sub-chain is actually exercised, not merely entered.</summary>
        private static TileGeometryBuffers LargeTrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingOffsets[0] = 0;
            g.Vertices[0] = new double2(0, 0);
            g.Vertices[1] = new double2(4096, 0);
            g.Vertices[2] = new double2(0, 4096);
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[1] = 3;
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        // ── (b) Not-completed-at-return ─────────────────────────────────────────────────────────────

        /// <summary>An unflushed job cannot have started, so this proves only that nothing completed
        /// SYNCHRONOUSLY, and it cannot false-red on a small fixture. The poll must come before
        /// <c>ScheduleBatchedJobs()</c>. A builder slow enough to miss the poll also passes, so
        /// <c>FillMeshGraphStructureTests</c> carries the structural complement.</summary>
        [Test]
        public void Schedule_ReturnsWithHandleNotCompleted_BeforeAnyFlush()
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
            };
            try
            {
                FillGraphOutput o = FillMeshGraph.Schedule(input);
                Assert.IsFalse(o.Handle.IsCompleted,
                    "the graph must not complete synchronously inside Schedule — a builder that Complete()s " +
                    "internally settles the tile in one step, defeating the whole point of a graph");
                JobHandle.ScheduleBatchedJobs();
                o.Handle.Complete();
                Assert.IsTrue(o.IsCreated);
                o.Dispose();
            }
            finally
            {
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── (d) Scratch freed ────────────────────────────────────────────────────────────────────────

        /// <summary>Baseline/delta against the three process-wide static counters, the idiom
        /// <c>DisposalLeakGuardTests</c> uses. <see cref="FillGraphOutput.DebugBufferDisposeNodes"/> vs.
        /// <see cref="FillGraphOutput.DebugBuffersAllocated"/> is a PAIRING check, not a live count: a worker-side
        /// <c>Dispose(handle)</c> node runs off the main thread and cannot decrement a managed counter.</summary>
        [Test]
        public void Dispose_ReturnsLiveOutputsToBaseline_AndBufferDisposeNodesPairWithAllocations()
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
            };
            try
            {
                long liveBefore     = FillGraphOutput.DebugLiveCount;
                long allocBefore    = FillGraphOutput.DebugBuffersAllocated;
                long disposedBefore = FillGraphOutput.DebugBufferDisposeNodes;

                FillGraphOutput o = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                o.Dispose(); // completes internally (Handle.Complete()), then frees every field

                Assert.AreEqual(liveBefore, FillGraphOutput.DebugLiveCount,
                    "every output container this call allocated must be freed by Dispose()");

                long allocAfter    = FillGraphOutput.DebugBuffersAllocated;
                long disposedAfter = FillGraphOutput.DebugBufferDisposeNodes;
                Assert.Greater(allocAfter, allocBefore,
                    "precondition: this Schedule call must actually have allocated scratch, or the pairing " +
                    "assertion below is vacuous");
                Assert.AreEqual(allocAfter - allocBefore, disposedAfter - disposedBefore,
                    "every scratch NativeList this call allocated must get exactly one Dispose(handle) node");
            }
            finally
            {
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── (g) Counters still balance on a CURVED layer — job-scheduling-design.md. ───────────────────────

        /// <summary>Same baseline/delta idiom as the flat-arm test above, over a layer that genuinely takes
        /// the curved sub-chain (<see cref="FillMeshGraphSchedulingTests.LargeTrianglePolygon"/> +
        /// <see cref="SphericalProjection"/>) — the flat-arm test alone cannot catch a balance bug specific
        /// to the subdivide sub-chain, since that code path never runs there.</summary>
        [Test]
        public void Dispose_ReturnsLiveOutputsToBaseline_OnACurvedLayer()
        {
            TileGeometryBuffers geometry = LargeTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new SphericalProjection(),
            };
            try
            {
                long liveBefore     = FillGraphOutput.DebugLiveCount;
                long allocBefore    = FillGraphOutput.DebugBuffersAllocated;
                long disposedBefore = FillGraphOutput.DebugBufferDisposeNodes;

                FillGraphOutput o = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                o.Handle.Complete();
                // job-scheduling-design.md: TileVertices IS the post-subdivision column on the curved
                // arm now, so "genuinely subdivided" reads as "more vertices than the raw 3-vertex triangle".
                Assert.Greater(o.TileVertices.Length, 3, "precondition: the layer must genuinely subdivide");
                o.Dispose();

                Assert.AreEqual(liveBefore, FillGraphOutput.DebugLiveCount,
                    "every output container this call allocated — including the scattered post-subdivision " +
                    "columns — must be freed by Dispose()");

                long allocAfter    = FillGraphOutput.DebugBuffersAllocated;
                long disposedAfter = FillGraphOutput.DebugBufferDisposeNodes;
                Assert.AreEqual(allocAfter - allocBefore, disposedAfter - disposedBefore,
                    "every scratch NativeList this call allocated must get exactly one Dispose(handle) node, " +
                    "on the curved arm too");
            }
            finally
            {
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FullPipelineTests — decode -> assemble -> earcut over all 239 country features
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full-pipeline headless test: decode → assemble → earcut over all 239 country features, on the Burst
    /// arm (<see cref="EarcutJobGatherHarness.RunLayer"/>). Each result pairs with its OWN input rings
    /// (<c>PolygonRun.InputOuter</c>/<c>InputHoles</c>), because the gather and <c>PolygonAssembler</c> can
    /// order polygons differently. It pins termination, the index range, and area conservation within 1% for
    /// simple and holed polygons. A holed polygon skips only when every ring self-intersects.
    /// <para>Non-obvious why: the pinned skip count rests on these sample-tile rings. KNOWN SKIPS, all
    /// forceClips=0 and degenerate before earcut (ratio = triArea / shoelace):</para>
    /// <code>
    ///   (3265,1333)(3273,1328)(3274,1326)(3270,1333)             1.92x  proper edge crossing
    ///   (1432,1432)(1433,1430)(1433,1429)(1433,1430)(1433,1433)  1.67x  repeated vertex (1433,1430)
    ///   (1140,1300)(1142,1298)(1149,1297)(1147,1297)             3.00x  proper edge crossing
    ///   (3515,1651)(3517,1649)(3517,1648)(3516,1652)             2.33x  proper edge crossing
    /// </code>
    /// <para>KNOWN NON-SKIPS: HasSelfIntersection flags these near-collinear rings, but they triangulate at
    /// 1.00x, so the skip gate also needs <c>triArea &gt; outerArea * 1.5</c>:</para>
    /// <code>
    ///   (2804,890)(2804,891)(2805,900)(2804,897)       (434,922)(435,923)(438,925)(439,927)
    ///   (1171,1627)(1173,1627)(1175,1626)(1176,1627)   (585,1340)(586,1338)(586,1337)(586,1341)
    ///   (3601,2179)(3604,2175)(3604,2174)(3604,2176)
    /// </code>
    /// <para>Before changing the pinned count, verify each new skip self-intersects AND inflates area.</para>
    /// </summary>
    public class FullPipelineTests
    {
        private static readonly TileId SampleTileId = new TileId { Z = 0, X = 0, Y = 0 };

        private static byte[] LoadSampleTile()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }

        // -----------------------------------------------------------------------------------------
        // (a,b,c,c2) Real-data full pipeline: decode → assemble → earcut, 239 features
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Countries_FullPipeline_Terminates_IndexesValid_AreaConserved()
        {
            byte[] mvtBytes = LoadSampleTile();

            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer);
            Assert.AreEqual(239, layer.Kinds.Count, "Expected 239 country features.");

            var runs = EarcutJobGatherHarness.RunLayer(mvtBytes, "countries", SampleTileId, forceLinearEarScan: false);

            int totalTriangles = 0;
            int simpleAreaChecks = 0;
            int holedAreaChecks  = 0;
            double totalSimpleRelError = 0.0;
            double totalHoledRelError  = 0.0;
            // Self-intersecting polygons that skip area conservation; HasSelfIntersection() proves each one
            // before it counts. The total is pinned below.
            int skipCount = 0;
            int totalForceClips = 0;

            foreach (var run in runs)
            {
                Assert.GreaterOrEqual(run.InputOuter.Count, 3, "Outer ring must have >= 3 verts.");

                // (a) Triangulate — if this hangs, the test times out.
                // (Triangulation already ran inside RunLayer; here we just consume the result.)
                totalForceClips += run.ForceClips;

                // (b) Index range.
                foreach (int idx in run.Indices)
                {
                    Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(run.Vertices.Length),
                        $"Index {idx} out of range [0, {run.Vertices.Length}) " +
                        $"for polygon with {run.InputOuter.Count} outer verts.");
                }

                bool hasHoles = run.InputHoles != null && run.InputHoles.Count > 0;

                if (!hasHoles && run.Indices.Length >= 3)
                {
                    // (c) Area conservation for simple polygons. A bowtie's shoelace area differs from its triangle
                    // area, so a skip first proves self-intersection; it cannot hide a defect on a clean ring.
                    double outerArea = AbsArea(run.InputOuter);
                    double triArea = ComputeTriArea(run.Vertices, run.Indices);
                    if (outerArea > 1.0) // skip near-zero areas (degenerate slivers of <1 sq tile-unit)
                    {
                        bool likelySelfIntersecting = triArea > outerArea * 1.5;
                        if (likelySelfIntersecting)
                        {
                            // Prove self-intersection before counting the skip. A clean ring that fails
                            // area conservation is a triangulator defect.
                            Assert.IsTrue(
                                HasSelfIntersection(run.InputOuter),
                                $"Area skip for {run.InputOuter.Count}-vert polygon (triArea={triArea:F2}, " +
                                $"outerArea={outerArea:F2}, forceClips={run.ForceClips}) but ring has " +
                                $"no self-intersection (proper crossing, repeated vertex, or T-junction) — " +
                                $"this is a triangulator defect, not a degenerate input.");
                            skipCount++;
                        }
                        else
                        {
                            double relErr = Math.Abs(triArea - outerArea) / outerArea;
                            totalSimpleRelError += relErr;
                            simpleAreaChecks++;

                            Assert.That(relErr, Is.LessThan(0.01),
                                $"Area conservation error {relErr:F6} exceeds 1% for simple polygon with " +
                                $"{run.InputOuter.Count} verts. triArea={triArea:F2}, expected={outerArea:F2}, " +
                                $"forceClips={run.ForceClips}");
                        }
                    }
                }
                else if (hasHoles && run.Indices.Length >= 3)
                {
                    // (c2) Area conservation for holed polygons, required only when every ring is clean. A holed
                    // polygon with a flagged ring falls through uncounted; skipCount pins only (c)'s skips.
                    double outerArea = AbsArea(run.InputOuter);
                    if (outerArea > 1.0)
                    {
                        bool allRingsClean = !HasSelfIntersection(run.InputOuter);
                        if (allRingsClean)
                        {
                            foreach (var hole in run.InputHoles)
                            {
                                if (HasSelfIntersection(hole))
                                {
                                    allRingsClean = false;
                                    break;
                                }
                            }
                        }

                        if (allRingsClean)
                        {
                            double holeAreasSum = 0.0;
                            foreach (var hole in run.InputHoles)
                                holeAreasSum += AbsArea(hole);

                            double expectedArea = outerArea - holeAreasSum;
                            if (expectedArea > 1.0) // skip near-zero expected areas
                            {
                                double triArea = ComputeTriArea(run.Vertices, run.Indices);
                                double relErr  = Math.Abs(triArea - expectedArea) / expectedArea;
                                totalHoledRelError += relErr;
                                holedAreaChecks++;

                                Assert.That(relErr, Is.LessThan(0.01),
                                    $"Hole area conservation error {relErr:F6} exceeds 1% for polygon with " +
                                    $"{run.InputOuter.Count} outer verts, {run.InputHoles.Count} hole(s). " +
                                    $"triArea={triArea:F2}, expected={expectedArea:F2} " +
                                    $"(outerArea={outerArea:F2} - holeSum={holeAreasSum:F2}), " +
                                    $"forceClips={run.ForceClips}. allRingsClean=true so this is a " +
                                    $"triangulator defect on well-formed input.");
                            }
                        }
                    }
                }

                totalTriangles += run.Indices.Length / 3;
            }

            // Pinned polygon count: RunLayer has no per-feature counter to match against the 239 features, so
            // this is the proxy. A dropped feature, or dropped or merged polygons, moves it.
            Assert.AreEqual(3218, runs.Count, "Countries fixture's assembled polygon count (Burst arm) moved.");
            Assert.Greater(totalTriangles, 0, "Should have produced at least one triangle.");

            // Pinned skip count, measured with RingAssemblyJob upstream. Before changing it, verify each new skip
            // self-intersects AND inflates area (see the class doc's KNOWN NON-SKIPS).
            Assert.AreEqual(4, skipCount,
                $"Expected exactly 4 self-intersecting polygon skips in the countries fixture, " +
                $"got {skipCount}. If a triangulator change caused this, verify each new skip is a real " +
                $"degenerate ring (HasSelfIntersection returns true) before updating the pinned count.");

            if (simpleAreaChecks > 0)
                Debug.Log($"[FullPipeline] Simple-polygon area conservation: avg relErr={totalSimpleRelError / simpleAreaChecks:F8} over {simpleAreaChecks} polygons.");
            if (holedAreaChecks > 0)
                Debug.Log($"[FullPipeline] Holed-polygon area conservation: avg relErr={totalHoledRelError / holedAreaChecks:F8} over {holedAreaChecks} clean polygons.");
            Debug.Log($"[FullPipeline] {runs.Count} polygons → {totalTriangles} triangles; {skipCount} degenerate skips; {totalForceClips} total force-clips.");
        }

        // -----------------------------------------------------------------------------------------
        // Regression test: the worst-ratio CLEAN holed polygon must conserve area. Non-obvious why: tile-boundary
        // clip artefacts make opposite-wound rings outside their exterior; an assembler that nested them as holes
        // (sign-only, without RingContainedIn) would make earcut bridge to a far ring and inflate area. This
        // guards that nesting and hole-bridging.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void HoledPolygon_WorstCase_AreaIsConserved()
        {
            byte[] mvtBytes = LoadSampleTile();
            var runs = EarcutJobGatherHarness.RunLayer(mvtBytes, "countries", SampleTileId, forceLinearEarScan: false);

            // Worst holed polygon (largest triArea/expected ratio) among ALL clean holed polygons, with no
            // outer-vert-count restriction: the worst real case has a many-vert outer.
            double worstRatio = 0.0;
            EarcutJobGatherHarness.PolygonRun worstRun = default;
            double worstExpected = 0.0, worstTri = 0.0;
            bool found = false;

            foreach (var run in runs)
            {
                if (run.InputHoles == null || run.InputHoles.Count < 1) continue;

                // Only consider clean polygons (all rings non-self-intersecting).
                if (HasSelfIntersection(run.InputOuter)) continue;
                bool clean = true;
                foreach (var hole in run.InputHoles)
                    if (HasSelfIntersection(hole)) { clean = false; break; }
                if (!clean) continue;

                double outerArea = AbsArea(run.InputOuter);
                double holeSum   = 0.0;
                foreach (var hole in run.InputHoles) holeSum += AbsArea(hole);
                double expected  = outerArea - holeSum;
                if (expected < 1.0) continue;

                if (run.Indices.Length < 3) continue;

                double triArea = ComputeTriArea(run.Vertices, run.Indices);
                double ratio   = triArea / expected;

                if (ratio > worstRatio)
                {
                    worstRatio    = ratio;
                    worstRun      = run;
                    worstExpected = expected;
                    worstTri      = triArea;
                    found         = true;
                }
            }

            Assert.IsTrue(found,
                "No clean holed polygon (all rings non-self-intersecting, hole contained) found in " +
                "the fixture. The regression test needs updating if the fixture changed.");

            double worstRelErr = Math.Abs(worstTri - worstExpected) / worstExpected;

            Debug.Log(
                $"[Regression] Worst clean holed polygon: outerV={worstRun.InputOuter.Count}, " +
                $"holes={worstRun.InputHoles.Count}, expected={worstExpected:F2}, triArea={worstTri:F2}, " +
                $"ratio={worstRatio:F4}, relErr={worstRelErr:F6}, forceClips={worstRun.ForceClips}.");

            Assert.That(worstRelErr, Is.LessThan(0.01),
                $"Worst clean holed polygon area conservation error {worstRelErr:F6} exceeds 1% " +
                $"(outerV={worstRun.InputOuter.Count}, holes={worstRun.InputHoles.Count}, " +
                $"triArea={worstTri:F2}, expected={worstExpected:F2}, ratio={worstRatio:F4}, " +
                $"forceClips={worstRun.ForceClips}). allRingsClean=true → a disjoint hole slipped " +
                $"through RingAssemblyJob/FillGatherJob's nesting, or hole-bridging regressed.");
        }

        // -----------------------------------------------------------------------------------------
        // (d) UInt32 index format and counts: MapViewAsyncMeshBuildTests and StyledFillTileBuilder tests.
        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// O(n²) degeneracy test: true iff (1) two non-adjacent edges properly cross, (2) two non-adjacent
        /// vertices coincide, or (3) a vertex lies on a non-adjacent edge (T-junction). It proves each area
        /// skip comes from degenerate input; a skipped ring it calls clean is a triangulator defect.
        /// </summary>
        private static bool HasSelfIntersection(List<double2> ring)
        {
            int n = ring.Count;
            if (n < 3) return false;

            // (2) Repeated vertices: non-adjacent coincidence creates backtracking overlap. An adjacent
            // pair is a zero-length edge and is not checked here.
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // adjacent (wrap-around), skip
                    if (ring[i].x == ring[j].x && ring[i].y == ring[j].y)
                        return true; // repeated vertex → degenerate ring
                }
            }

            if (n < 4) return false; // need at least 4 verts for edge tests

            // (1) Proper edge crossing: non-adjacent edges cross in their interiors.
            // (3) Vertex-on-non-adjacent-edge: checked inside the edge-pair loop below.
            for (int i = 0; i < n; i++)
            {
                double2 a0 = ring[i];
                double2 a1 = ring[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // adjacent (wrap-around), skip
                    double2 b0 = ring[j];
                    double2 b1 = ring[(j + 1) % n];
                    // (1) Proper crossing.
                    if (SegmentsProperlyIntersect(a0, a1, b0, b1))
                        return true;
                    // (3) Vertex of edge a on edge b, or vertex of edge b on edge a.
                    if (PointOnSegment(b0, a0, a1) || PointOnSegment(b1, a0, a1) ||
                        PointOnSegment(a0, b0, b1) || PointOnSegment(a1, b0, b1))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true iff segments (p0,p1) and (q0,q1) properly intersect (interiors cross;
        /// shared endpoints are NOT counted as intersections).
        /// </summary>
        private static bool SegmentsProperlyIntersect(double2 p0, double2 p1, double2 q0, double2 q1)
        {
            double d1 = CrossScalar(q0, q1, p0);
            double d2 = CrossScalar(q0, q1, p1);
            double d3 = CrossScalar(p0, p1, q0);
            double d4 = CrossScalar(p0, p1, q1);
            // Proper intersection: each segment straddles the other's line.
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;
            return false;
        }

        /// <summary>
        /// Returns true iff point p lies strictly on segment (a,b) — including at the endpoints.
        /// Uses the collinearity + bounding-box test.
        /// </summary>
        private static bool PointOnSegment(double2 p, double2 a, double2 b)
        {
            // Must be collinear: cross product == 0.
            double cross = CrossScalar(a, b, p);
            if (Math.Abs(cross) > 1e-10) return false;
            // Must be within the bounding box of the segment.
            return p.x >= Math.Min(a.x, b.x) && p.x <= Math.Max(a.x, b.x) &&
                   p.y >= Math.Min(a.y, b.y) && p.y <= Math.Max(a.y, b.y);
        }

        /// <summary>Cross product of (b-a) × (p-a): sign indicates which side of line ab p is on.</summary>
        private static double CrossScalar(double2 a, double2 b, double2 p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static double AbsArea(List<double2> ring)
        {
            if (ring == null || ring.Count < 3) return 0.0;
            double area = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % n];
                area += (b.x - a.x) * (b.y + a.y);
            }
            return Math.Abs(area) * 0.5;
        }

        private static double ComputeTriArea(double2[] verts, int[] indices)
        {
            double total = 0.0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                double2 a = verts[indices[i]];
                double2 b = verts[indices[i + 1]];
                double2 c = verts[indices[i + 2]];
                total += Math.Abs(0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)));
            }
            return total;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // JobifiedPipelineTests — parity and integration for the jobified decode + mesh pipeline
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parity tests for the jobified decode + mesh pipeline: (1) the decode job matches managed
    /// <c>MvtGeometry.Decode</c> by ring count and vertex hash; (2) ring assembly matches <c>PolygonAssembler</c>
    /// polygon/hole counts; (3) the end-to-end vertex+index hash matches with no tolerance, since tile-space
    /// integer coordinates are exact; (4) N tiles give N × the single-tile vertex count.
    /// </summary>
    [TestFixture]
    public class JobifiedPipelineTests
    {
        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // ── (1) Decode job vs managed MvtGeometry.Decode ─────────────────────────────────────

        [Test]
        public void DecodeJob_RingCountAndVertexHash_MatchManagedReference()
        {
            FileAssert.Exists(FixturePath);
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);

            // The command streams come from the independent fixture reader — a decoded feature
            // carries none, and reading production's own buffer would make this parity self-referential.
            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer);

            var polyGeoms = new List<uint[]>();
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
                if (layer.Kinds[fi] == TileGeometryType.Polygon && layer.Commands[fi] != null)
                    polyGeoms.Add(layer.Commands[fi]);

            Assert.Greater(polyGeoms.Count, 0, "Expected polygon features");

            // ── Managed reference.
            var managedRings = new List<List<double2>>();
            foreach (var geom in polyGeoms)
            {
                var rings = MvtGeometry.Decode(geom);
                if (rings != null) managedRings.AddRange(rings);
            }

            // ── Job path.
            int featureCount  = polyGeoms.Count;
            int totalCommands = 0;
            for (int fi = 0; fi < featureCount; fi++)
                totalCommands += polyGeoms[fi].Length;

            int maxRings    = totalCommands / 3 + featureCount + 2;
            int maxVertices = totalCommands + 4;

            var commands       = new NativeArray<uint>(totalCommands,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featOffsets    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featLengths    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outVerts       = new NativeArray<double2>(maxVertices,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingOffsets = new NativeArray<int>(maxRings + 1,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingFeat    = new NativeArray<int>(maxRings,          Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var outVertCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);

            try
            {
                int cmdPos = 0;
                for (int fi = 0; fi < featureCount; fi++)
                {
                    featOffsets[fi] = cmdPos;
                    featLengths[fi] = polyGeoms[fi].Length;
                    for (int k = 0; k < polyGeoms[fi].Length; k++)
                        commands[cmdPos + k] = polyGeoms[fi][k];
                    cmdPos += polyGeoms[fi].Length;
                }

                new MvtDecodeJob
                {
                    Commands            = commands,
                    FeatureOffsets      = featOffsets,
                    FeatureLengths      = featLengths,
                    OutVertices         = outVerts,
                    OutRingOffsets      = outRingOffsets,
                    OutRingFeatureIndex = outRingFeat,
                    OutRingCount        = outRingCount,
                    OutVertexCount      = outVertCount,
                }.Schedule().Complete();

                int jobRingCount = outRingCount[0];

                Assert.AreEqual(managedRings.Count, jobRingCount,
                    $"Decode job ring count {jobRingCount} != managed {managedRings.Count}");

                string managedHash = HashRings(managedRings);
                string jobHash     = HashRingsNative(outVerts, outRingOffsets, jobRingCount);

                Assert.AreEqual(managedHash, jobHash,
                    "Decode job ring vertex content hash must match managed MvtGeometry.Decode. " +
                    "A mismatch means the Burst decode path has different zigzag, cursor, or ring-boundary logic.");
            }
            finally
            {
                commands.Dispose(); featOffsets.Dispose(); featLengths.Dispose();
                outVerts.Dispose(); outRingOffsets.Dispose(); outRingFeat.Dispose();
                outRingCount.Dispose(); outVertCount.Dispose();
            }
        }

        // ── (2) Ring assembly job vs managed PolygonAssembler ─────────────────────────────────

        [Test]
        public void RingAssemblyJob_PolygonAndHoleCounts_MatchManagedReference()
        {
            FileAssert.Exists(FixturePath);
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);

            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer);

            int managedPolyCount = 0;
            int managedHoleCount = 0;
            var polyGeoms        = new List<uint[]>();
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.Polygon || layer.Commands[fi] == null) continue;
                polyGeoms.Add(layer.Commands[fi]);
                var rings = MvtGeometry.Decode(layer.Commands[fi]);
                var polys = PolygonAssembler.Assemble(rings);
                managedPolyCount += polys.Count;
                foreach (var p in polys) managedHoleCount += (p.Holes?.Count ?? 0);
            }

            int featureCount  = polyGeoms.Count;
            int totalCommands = 0;
            foreach (var g in polyGeoms) totalCommands += g.Length;
            int maxRings    = totalCommands / 3 + featureCount + 2;
            int maxVertices = totalCommands + 4;
            int maxPolygons = maxRings;
            int maxHoles    = maxRings;

            var commands       = new NativeArray<uint>(totalCommands,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featOffsets    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featLengths    = new NativeArray<int>(featureCount,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outVerts       = new NativeArray<double2>(maxVertices,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingOffsets = new NativeArray<int>(maxRings + 1,      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingFeat    = new NativeArray<int>(maxRings,          Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outRingCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var outVertCount   = new NativeArray<int>(1,                 Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyOuterIdx   = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleStart  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleCount  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var holeRingIdxs   = new NativeArray<int>(maxHoles,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outPolyCount   = new NativeArray<int>(1,            Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var outHoleCount2  = new NativeArray<int>(1,            Allocator.Persistent, NativeArrayOptions.ClearMemory);

            try
            {
                int cmdPos = 0;
                for (int fi = 0; fi < featureCount; fi++)
                {
                    featOffsets[fi] = cmdPos;
                    featLengths[fi] = polyGeoms[fi].Length;
                    for (int k = 0; k < polyGeoms[fi].Length; k++)
                        commands[cmdPos + k] = polyGeoms[fi][k];
                    cmdPos += polyGeoms[fi].Length;
                }

                new MvtDecodeJob
                {
                    Commands = commands, FeatureOffsets = featOffsets, FeatureLengths = featLengths,
                    OutVertices = outVerts, OutRingOffsets = outRingOffsets,
                    OutRingFeatureIndex = outRingFeat,
                    OutRingCount = outRingCount, OutVertexCount = outVertCount,
                }.Schedule().Complete();

                int ringCount = outRingCount[0];

                // The assembler is kind-gated, and no materializer runs here, so supply its column: every
                // feature is a polygon, as in the managed reference arm.
                var featureKinds = new NativeArray<TileGeometryType>(
                    featureCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int fi = 0; fi < featureCount; fi++) featureKinds[fi] = TileGeometryType.Polygon;

                new RingAssemblyJob
                {
                    Vertices = outVerts, RingOffsets = outRingOffsets, RingFeatureIdx = outRingFeat,
                    RingCount = ringCount, FeatureGeometryType = featureKinds,
                    OutPolyOuterRingIdx = polyOuterIdx, OutPolyHoleListStart = polyHoleStart,
                    OutPolyHoleCount = polyHoleCount, OutHoleRingIdxs = holeRingIdxs,
                    OutPolygonCount = outPolyCount, OutHoleCount = outHoleCount2,
                }.Schedule().Complete();

                int jobPolyCount = outPolyCount[0];
                int jobHoleCount = outHoleCount2[0];

                Assert.AreEqual(managedPolyCount, jobPolyCount,
                    $"Ring assembly job polygon count {jobPolyCount} != managed {managedPolyCount}.");
                Assert.AreEqual(managedHoleCount, jobHoleCount,
                    $"Ring assembly job hole count {jobHoleCount} != managed {managedHoleCount}.");

                featureKinds.Dispose();
            }
            finally
            {
                commands.Dispose(); featOffsets.Dispose(); featLengths.Dispose();
                outVerts.Dispose(); outRingOffsets.Dispose(); outRingFeat.Dispose();
                outRingCount.Dispose(); outVertCount.Dispose();
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); outPolyCount.Dispose(); outHoleCount2.Dispose();
            }
        }

        // ── (4) Multi-tile throughput ──────────────────────────────────────────────────────────

        [Test]
        public void MultiTile_NTiles_AllProduceSameHashAndCorrectTotalVerts()
        {
            const int N = 4;

            FileAssert.Exists(FixturePath);
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);

            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer);

            double extent = layer.Extent;
            var polygonKinds    = new List<TileGeometryType>();
            var polygonCommands = new List<uint[]>();
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
                if (layer.Kinds[fi] == TileGeometryType.Polygon && layer.Commands[fi] != null)
                { polygonKinds.Add(layer.Kinds[fi]); polygonCommands.Add(layer.Commands[fi]); }

            var (bMin, _)  = new TileId { Z = 0, X = 0, Y = 0 }.MercatorBounds();
            // ONE buffer, borrowed by all N+1 Schedule calls below — each call used to consume its
            // own mint, so this is also a live demonstration that Schedule no longer consumes its input.
            TileGeometryBuffers geometry = MvtGeometryMaterializerTestFactory.Materialize(
                new TileId { Z = 0, X = 0, Y = 0 }, extent, polygonKinds, polygonCommands);
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var singleInput = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                // The managed reference this is hashed against predates the outward boundary band and emits
                // none; the claim here is about earcut's merged-vertex ORDER, so both arms must be band-free.
                SuppressBoundaryBand = true,
            };

            // Get single-tile reference.
            int    singleVertCount  = 0;
            int    singleIndexCount = 0;
            string singleVertHash   = null;
            string singleIdxHash    = null;
            FillGraphOutput singleBuffers = FillMeshGraph.Schedule(singleInput);
            singleBuffers.Handle.Complete();
            try
            {
                singleVertCount  = singleBuffers.TileVertices.Length;
                singleIndexCount = singleBuffers.TriangleIndices.Length;
                singleVertHash   = HashDouble2ArrayFromList(singleBuffers.TileVertices, singleVertCount);
                singleIdxHash    = HashIntArrayFromList(singleBuffers.TriangleIndices, singleIndexCount);
            }
            finally { singleBuffers.Dispose(); }

            // Schedule N tiles.
            var allBuffers = new FillGraphOutput[N];
            for (int i = 0; i < N; i++)
            {
                allBuffers[i] = FillMeshGraph.Schedule(singleInput);
                allBuffers[i].Handle.Complete();
            }

            int totalVerts = 0;
            try
            {
                for (int i = 0; i < N; i++)
                {
                    int verts   = allBuffers[i].TileVertices.Length;
                    int indices = allBuffers[i].TriangleIndices.Length;
                    string vh   = HashDouble2ArrayFromList(allBuffers[i].TileVertices, verts);
                    string ih   = HashIntArrayFromList(allBuffers[i].TriangleIndices, indices);
                    totalVerts += verts;

                    Assert.AreEqual(singleVertHash, vh,
                        $"Multi-tile tile[{i}] vertex hash differs from single-tile reference.");
                    Assert.AreEqual(singleIdxHash, ih,
                        $"Multi-tile tile[{i}] index hash differs from single-tile reference.");
                }

                Assert.AreEqual(singleVertCount * N, totalVerts,
                    $"Total multi-tile vertex count {totalVerts} != {singleVertCount} * {N}.");
            }
            finally
            {
                for (int i = 0; i < N; i++) allBuffers[i].Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        private static string HashRings(List<List<double2>> rings)
        {
            var bytes = new List<byte>();
            foreach (var ring in rings)
                foreach (var v in ring)
                {
                    bytes.AddRange(BitConverter.GetBytes(v.x));
                    bytes.AddRange(BitConverter.GetBytes(v.y));
                }
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static string HashRingsNative(NativeArray<double2> verts, NativeArray<int> ringOffsets, int ringCount)
        {
            var bytes = new List<byte>();
            for (int ri = 0; ri < ringCount; ri++)
            {
                int start = ringOffsets[ri];
                int end   = ringOffsets[ri + 1];
                for (int i = start; i < end; i++)
                {
                    bytes.AddRange(BitConverter.GetBytes(verts[i].x));
                    bytes.AddRange(BitConverter.GetBytes(verts[i].y));
                }
            }
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static string HashDouble2Array(NativeArray<double2> arr, int count)
        {
            var bytes = new List<byte>(count * 16);
            for (int i = 0; i < count; i++)
            {
                bytes.AddRange(BitConverter.GetBytes(arr[i].x));
                bytes.AddRange(BitConverter.GetBytes(arr[i].y));
            }
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static string HashIntArray(NativeArray<int> arr, int count)
        {
            var bytes = new List<byte>(count * 4);
            for (int i = 0; i < count; i++)
                bytes.AddRange(BitConverter.GetBytes(arr[i]));
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        // The oracle reads FillMeshGraph.Schedule's
        // NativeList output — same byte layout as the NativeArray hashers above, over a NativeList view.
        private static string HashDouble2ArrayFromList(NativeList<double2> list, int count)
        {
            var bytes = new List<byte>(count * 16);
            for (int i = 0; i < count; i++)
            {
                bytes.AddRange(BitConverter.GetBytes(list[i].x));
                bytes.AddRange(BitConverter.GetBytes(list[i].y));
            }
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static string HashIntArrayFromList(NativeList<int> list, int count)
        {
            var bytes = new List<byte>(count * 4);
            for (int i = 0; i < count; i++)
                bytes.AddRange(BitConverter.GetBytes(list[i]));
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ProfilerMarkerTests — ProfilerRecorder category/marker conventions
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class ProfilerMarkerTests : BaseTestFixture
    {
        /// <summary>
        /// Ring capacity requested from every <see cref="ProfilerRecorder"/> here, and therefore the only
        /// safe upper bound when reading samples back — <c>Count</c> is NOT one once the ring has wrapped.
        /// </summary>
        private const int RecorderCapacity = 64;

        // ── Helpers (mirrors MapViewLiveLoopTests; duplicated to keep test file self-contained) ──
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": {
                        ""fill-color"": [""rgba"", 200, 50, 50, 1]
                    }
                }
            ]
        }");


        private static void PumpUntilSettled(MapViewComponent view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        /// <summary>A compile-time check: every entry is a <c>const</c> in its owner's nested
        /// <c>ProfilerMarkerNames</c>, so deleting or renaming a production marker breaks this file's compile.
        /// Never add a bare string literal: it compiles whatever production does, so it can outlive its marker.
        /// </summary>
        [Test]
        public void ProfilerMarkers_AllExpectedNamesAreReachable()
        {
            string[] expectedNames =
            {
                MapView.ProfilerMarkerNames.CameraAdvance,
                TileManager.ProfilerMarkerNames.CoverSelect,
                TileManager.ProfilerMarkerNames.FetchPoll,
                TileManager.ProfilerMarkerNames.SchedulerRequest,
                TileDecodeDispatch.ProfilerMarkerNames.TileDecode,
                StyledFillTileBuilder.ProfilerMarkerNames.BuildLayerInput,
                TileManager.ProfilerMarkerNames.MeshUpload,
                MvtDecoder.ProfilerMarkerNames.Decode, // The marker follows the decode it brackets
                // FillMeshGraph's nodes carry no marker; BuildLayerInput above covers the fill graph's prologue.
                // Per-frame MapView.LateUpdate sub-phases + EG-drive split.
                MapView.ProfilerMarkerNames.LateUpdate,
                MapView.ProfilerMarkerNames.SceneFrame,
                MapView.ProfilerMarkerNames.SymbolCollect,
                MapView.ProfilerMarkerNames.SymbolBatch,
                // Symbol-label markers below read their names from each type's nested ProfilerMarkerNames const
                // (SSOT), reached via InternalsVisibleTo — renaming a marker is a one-line edit at its source.
                SymbolSubsystem.ProfilerMarkerNames.SymbolExtract,
                SymbolSubsystem.ProfilerMarkerNames.AtlasUpload,
                SymbolSubsystem.ProfilerMarkerNames.BatchCollect,
                SymbolSubsystem.ProfilerMarkerNames.BatchCollectClassify,
                SymbolSubsystem.ProfilerMarkerNames.BatchCollectDedup,
                SymbolSubsystem.ProfilerMarkerNames.BatchSoA,
                SymbolPlacementSystem.ProfilerMarkerNames.Gather,
                SymbolPlacementSystem.ProfilerMarkerNames.Tick,
                SymbolPlacementSystem.ProfilerMarkerNames.Project,
                SymbolPlacementSystem.ProfilerMarkerNames.ProjectPositions,
                SymbolPlacementSystem.ProfilerMarkerNames.Stage,
                SymbolPlacementSystem.ProfilerMarkerNames.Collide,
                SymbolPlacementSystem.ProfilerMarkerNames.CollideHarvest,
                SymbolPlacementSystem.ProfilerMarkerNames.Emit,
                SymbolPlacementSystem.ProfilerMarkerNames.EmitLoop,
                SymbolPlacementSystem.ProfilerMarkerNames.EmitDecay,
                WorldSymbolRenderer.ProfilerMarkerNames.EndFrame,
                MapView.ProfilerMarkerNames.ApplyZoom,
                FillRenderLayer.ProfilerMarkerNames.ApplyZoomFills,
                LineRenderLayer.ProfilerMarkerNames.ApplyZoomLines,
                LineRenderLayer.ProfilerMarkerNames.ApplyZoomLineDash,
                MapView.ProfilerMarkerNames.InstancedRebuild,
                MapView.ProfilerMarkerNames.ManagerTick,
                TileManager.ProfilerMarkerNames.MeshDataAllocate,
                TileManager.ProfilerMarkerNames.AddTileLayer,
                EntitiesTileRenderer.ProfilerMarkerNames.AddLayerRoot,
                EntitiesTileRenderer.ProfilerMarkerNames.AddLayerRegister,
                EntitiesTileRenderer.ProfilerMarkerNames.AddLayerParent,
                EntitiesTileRenderer.ProfilerMarkerNames.RootTransforms,
                EntitiesTileRenderer.ProfilerMarkerNames.InitGroup,
                EntitiesTileRenderer.ProfilerMarkerNames.SimGroup,
                EntitiesTileRenderer.ProfilerMarkerNames.PresGroup,
            };

            foreach (string name in expectedNames)
            {
                Assert.IsTrue(name.StartsWith("MapRenderer."),
                    $"Marker name '{name}' must be under the MapRenderer.* namespace.");
            }

            // Belt-and-suspenders: construct each marker (would throw if Unity.Profiling not available).
            foreach (string name in expectedNames)
            {
                // ProfilerMarker construction is allocation-free (struct init).
                var marker = new ProfilerMarker(ProfilerCategory.Scripts, name);
                // No assertion on the marker object itself — the compile-time reference to
                // ProfilerMarker is the real check. This loop just exercises the constructor.
                _ = marker;
            }
        }

        /// <summary>Wired-not-dead: the recorder reads samples of <c>BuildLayerInput</c>, the marker a fill
        /// layer's graph prologue fires, after a tile load. The graph's write step is a Burst job with no marker
        /// of its own. The coroutine yields frames so the profiler commits the samples.</summary>
        [UnityTest]
        public IEnumerator ProfilerRecorder_BuildMarker_HasSamplesAfterTileLoad()
        {
            const string markerName    = StyledFillTileBuilder.ProfilerMarkerNames.BuildLayerInput;
            const string bogusName     = "MapRenderer.__NoSuchMarker__";

            var go   = Track(new GameObject("MapView_ProfilerTest"));
            var view = go.AddComponent<MapViewComponent>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            // Start both recorders BEFORE the tile load, in the markers' Scripts category. The bogus name is a
            // negative control: it proves the metric counts firings, not frames.
            using var recorder      = ProfilerRecorder.StartNew(
                ProfilerCategory.Scripts, markerName, capacity: RecorderCapacity,
                options: ProfilerRecorderOptions.SumAllSamplesInFrame);
            using var bogusRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Scripts, bogusName, capacity: RecorderCapacity,
                options: ProfilerRecorderOptions.SumAllSamplesInFrame);

            try
            {
                view.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(0, 0, 0.0),
                    style: MinimalStyle());

                // Drive the tile load synchronously (FixtureSource returns immediately).
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must be settled before yielding — otherwise the marker may not have fired yet.");
                // NIT 1 follow-up: the marker sample count alone cannot tell "fired" from "fired and produced
                // nothing" — pin the OTHER end too, that the settled tile actually registered a draw item.
                Assert.Greater(view.EntitiesRenderer().DrawItemCount(), 0,
                    "the settled tile must have registered at least one draw item — a marker firing with no " +
                    "geometry reaching the backend would still pass the sample-count check below.");

                // Yield a couple of frames so the profiler can commit accumulated samples.
                yield return null;
                yield return null;

                // Sum each sample's Count (firings per frame); recorder.Count counts frames. Clamp to CAPACITY:
                // once the fixed-size ring wraps, Count is no valid index bound and GetSample() throws.
                long realHits  = 0;
                for (int i = 0; i < math.min(recorder.Count, RecorderCapacity); i++)
                    realHits += recorder.GetSample(i).Count;

                long bogusHits = 0;
                for (int i = 0; i < math.min(bogusRecorder.Count, RecorderCapacity); i++)
                    bogusHits += bogusRecorder.GetSample(i).Count;

                // Negative control: a non-existent marker must read 0 invocations.
                // If bogusHits > 0 here, then `Count` counts frames, not firings — the metric is wrong.
                Assert.AreEqual(0L, bogusHits,
                    $"Negative-control recorder for '{bogusName}' should report 0 marker invocations " +
                    $"(got {bogusHits}). If non-zero, the sampled metric counts frames, not marker firings.");

                // Real marker: must have fired at least once during the tile load.
                Assert.Greater(realHits, 0L,
                    $"ProfilerRecorder for '{markerName}' collected {realHits} marker invocations after a tile load. " +
                    "Expected > 0 — the marker must be wired on the live MapView → StyledFillTileBuilder path. " +
                    "If this fails with 0: (a) check bogusHits == 0 (metric is discriminating), " +
                    "(b) confirm AllTilesSettled() returned true (load actually ran), " +
                    "(c) the profiler may not commit in this runner — run with PlayMode for reliable frame commits.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SharedDisposableSharingTests — mesh and symbol cadences observe the same instance, either order
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The mesh and symbol cadences of ONE kick observe the SAME <see cref="IDecodedTile"/> instance through the
    /// reference-counted <see cref="SharedDisposable{T}"/>, in either arrival order. The eager decode makes this
    /// sharing structural, but nothing else would notice a per-pass decode, which breaks the per-source-layer
    /// buffer sharing the design rests on. <c>TileLayerProcessorRunnerTests</c>, <c>TileFeatureSourceGetTileTests</c>
    /// and <c>EagerDecodeOwnershipTests</c> pin fault handling; <c>SharedDisposableTests</c> pins the refcount race.
    /// </summary>
    [TestFixture]
    public class SharedDisposableSharingTests
    {
        /// <summary>The address these teeth decode at — the same one <see cref="MakeContext"/> processes at,
        /// because the decode's id IS the buffers' id and a mismatch would be a mispairing.</summary>
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = new TileId { Z = 0, X = 0, Y = 0 },
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        /// <summary>The production mint shape, minus the pool hop.</summary>
        private static SharedDisposable<IDecodedTile> Mint() =>
            new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(Tile, SampleTileFixture.Bytes()));

        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>Captures the observed <see cref="IDecodedTile"/> reference into a shared box, so a test
        /// can compare it across cadences.</summary>
        private sealed class CapturingMeshProcessor : ITileMeshLayerProcessor
        {
            private readonly IDecodedTile[] _box;
            public LayerPhase Phase => LayerPhase.WorkerOnly;
            public CapturingMeshProcessor(IDecodedTile[] box) => _box = box;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context) => _box[0] = tile;
            public bool TryTakeGraphRequest(out ILayerMeshBuild build) { build = null; return false; }
            public void Release() { }
        }

        private sealed class CapturingSymbolProcessor : ITileWorkerThenMainLayerProcessor
        {
            private readonly IDecodedTile[] _box;
            public LayerPhase Phase => LayerPhase.WorkerThenMain;
            public CapturingSymbolProcessor(IDecodedTile[] box) => _box = box;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context) => _box[0] = tile;
            public void CompleteOnMain(CancellationToken ct) { }
        }

        // ── F-1: cross-cadence ReferenceEquals, both arrival orders ──────────────────────────────────────

        [Test]
        public void OneKick_MeshFirstThenSymbol_BothCadencesObserveTheSameDecodedTile()
        {
            SharedDisposable<IDecodedTile> decode = Mint();
            var context = MakeContext();

            var meshBox = new IDecodedTile[1];
            var symbolBox = new IDecodedTile[1];

            // ONE reference around BOTH passes — the exact shape of TileManager.KickMeshBuild's pool lambda,
            // which wraps RunWorkerPass and symbolPass.RunWorkerAndHandoff together and releases in a finally.
            try
            {
                TileLayerProcessorRunner.RunWorkerPass(decode, in context, new ITileMeshLayerProcessor[] { new CapturingMeshProcessor(meshBox) });
                TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, new ITileWorkerThenMainLayerProcessor[] { new CapturingSymbolProcessor(symbolBox) });
            }
            finally { decode.Release(); }

            Assert.IsNotNull(meshBox[0], "the mesh pass must observe a decoded tile");
            Assert.AreSame(meshBox[0], symbolBox[0],
                "two independent MvtDecoder.Decode calls can never return the same instance — reference " +
                "identity across cadences IS the decode-count-1 proof for ONE KICK. Both cadences read the " +
                "same tile, hence the same per-source-layer geometry buffers.");
        }

        [Test]
        public void OneKick_SymbolFirstThenMesh_BothCadencesObserveTheSameDecodedTile()
        {
            SharedDisposable<IDecodedTile> decode = Mint();
            var context = MakeContext();

            var symbolBox = new IDecodedTile[1];
            var meshBox = new IDecodedTile[1];

            try
            {
                TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, new ITileWorkerThenMainLayerProcessor[] { new CapturingSymbolProcessor(symbolBox) });
                TileLayerProcessorRunner.RunWorkerPass(decode, in context, new ITileMeshLayerProcessor[] { new CapturingMeshProcessor(meshBox) });
            }
            finally { decode.Release(); }

            Assert.IsNotNull(symbolBox[0], "the symbol pass must observe a decoded tile");
            Assert.AreSame(symbolBox[0], meshBox[0],
                "reference identity across cadences IS the decode-count-1 proof for one kick — arrival order " +
                "must not change it.");
        }

        // ── Anti-vacuity for the two above: the fixture's bytes really do decode, and to a FRESH instance ──

        /// <summary>
        /// The <c>AreSame</c> assertions above are only meaningful if two decodes of these bytes would
        /// genuinely produce two instances. Stated here rather than assumed, because a decoder that
        /// memoized by tile id would make identity true for the wrong reason and silently retire both teeth.
        /// </summary>
        [Test]
        public void TwoDecodesOfTheSameBytes_ProduceDistinctTiles()
        {
            var decoder = new MvtTileDecoder();
            byte[] bytes = SampleTileFixture.Bytes();
            IDecodedTile a = decoder.Decode(Tile, bytes);
            IDecodedTile b = decoder.Decode(Tile, bytes);
            try
            {
                Assert.AreNotSame(a, b,
                    "precondition for the identity teeth above: the decoder is not memoized, so observing the " +
                    "SAME instance across two passes really does prove they shared one decode");
            }
            finally { a.Dispose(); b.Dispose(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileSymbolWorkerPassTests — decodes fetched bytes exactly once, shared across every processor
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins that <see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/> hands the same
    /// <see cref="IDecodedTile"/> reference to every processor in dense order, runs NO tail (the tail is the
    /// caller's main-thread step), and rejects a <see cref="LayerPhase.WorkerOnly"/> processor. It is the only
    /// tooth that sees each processor borrow the same per-source-layer buffer; a per-get copy is byte-identical.
    /// Each pass runs against a live lease reference, released in a <c>finally</c> like the production sites.
    /// </summary>
    [TestFixture]
    public class TileSymbolWorkerPassTests
    {
        private static readonly TileId ContextTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = ContextTile,
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>Logs each ProcessOnWorker call (order, decoded-tile reference, and the source-layer buffer
        /// read through the consumers' <c>GetLayer(...).Geometry</c> expression) into a SHARED log, and counts
        /// CompleteOnMain calls. The buffer column catches a layer that re-materializes per get: that hands each
        /// processor a different backing pointer.</summary>
        private sealed class RecordingWorkerThenMainProcessor : ITileWorkerThenMainLayerProcessor
        {
            private const string ProbeSourceLayer = "countries"; // present in the committed fixture

            private readonly int _order;
            private readonly List<(int order, IDecodedTile tile, NativeArray<double2> buffer)> _log;

            public int CompleteOnMainCallCount { get; private set; }

            public LayerPhase Phase { get; }

            public RecordingWorkerThenMainProcessor(int order,
                List<(int order, IDecodedTile tile, NativeArray<double2> buffer)> log,
                LayerPhase phase = LayerPhase.WorkerThenMain)
            {
                _order = order;
                _log   = log;
                Phase  = phase;
            }

            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
                => _log.Add((_order, tile, tile?.GetLayer(ProbeSourceLayer)?.Geometry.Vertices ?? default));

            public void CompleteOnMain(CancellationToken ct)
            {
                CompleteOnMainCallCount++;
            }
        }

        [Test]
        public void RunSymbolWorkerPass_InvokesEveryProcessorInOrder_WithTheSameDecodedTile_AndRunsNoTail()
        {
            var log = new List<(int order, IDecodedTile tile, NativeArray<double2> buffer)>();
            var p0 = new RecordingWorkerThenMainProcessor(0, log);
            var p1 = new RecordingWorkerThenMainProcessor(1, log);
            var p2 = new RecordingWorkerThenMainProcessor(2, log);
            var processors = new ITileWorkerThenMainLayerProcessor[] { p0, p1, p2 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            try { TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, processors); }
            finally { decode.Release(); }

            Assert.AreEqual(3, log.Count, "every processor must be invoked exactly once");
            Assert.AreEqual(0, log[0].order, "dense order 0 first");
            Assert.AreEqual(1, log[1].order, "dense order 1 second");
            Assert.AreEqual(2, log[2].order, "dense order 2 third");

            Assert.IsNotNull(log[0].tile, "the decoded tile must be non-null");
            Assert.AreSame(log[0].tile, log[1].tile, "every processor must observe the SAME decoded tile reference");
            Assert.AreSame(log[0].tile, log[2].tile, "every processor must observe the SAME decoded tile reference");

            // One materialization per source layer, shared by every symbol layer of the pass. NativeArray<T>.Equals
            // compares pointer and length, so this checks buffer IDENTITY, which a per-get copy fails.
            Assert.IsTrue(log[0].buffer.IsCreated,
                "precondition: the probe source-layer must really carry geometry, or the identity clauses " +
                "below compare two default(NativeArray)s and assert nothing");
            Assert.IsTrue(log[0].buffer.Equals(log[1].buffer),
                "every symbol processor must borrow the SAME source-layer buffer — a per-get materialization " +
                "is the retired per-layer mint, wearing the layer's name");
            Assert.IsTrue(log[0].buffer.Equals(log[2].buffer),
                "every symbol processor must borrow the SAME source-layer buffer");

            Assert.AreEqual(0, p0.CompleteOnMainCallCount, "the runner must never invoke the main-thread tail");
            Assert.AreEqual(0, p1.CompleteOnMainCallCount, "the runner must never invoke the main-thread tail");
            Assert.AreEqual(0, p2.CompleteOnMainCallCount, "the runner must never invoke the main-thread tail");
        }

        [Test]
        public void RunSymbolWorkerPass_WorkerOnlyProcessor_Throws()
        {
            var log = new List<(int order, IDecodedTile tile, NativeArray<double2> buffer)>();
            var p0 = new RecordingWorkerThenMainProcessor(0, log, phase: LayerPhase.WorkerOnly);
            var processors = new ITileWorkerThenMainLayerProcessor[] { p0 };
            var context = MakeContext();

            var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(ContextTile, SampleTileFixture.Bytes()));
            try
            {
                Assert.Throws<NotSupportedException>(
                    () => TileLayerProcessorRunner.RunSymbolWorkerPass(decode, in context, processors),
                    "a WorkerOnly processor has no tail — the symbol pass exists to feed tails, so this is the " +
                    "mirrored programming error of the guard.");
            }
            finally
            {
                // This test mints the lease, so it owes one release on the throwing path too. A skipped release
                // strands Persistent buffers silently: NativeLeakDetection is off in the batch gate.
                decode.Release();
            }
        }

        // Limitation: no test here drives the propagate-don't-settle fault policy. A SharedDisposable<T> read after
        // its last Release() does not throw (only a DEBUG assertion fires), so no read fault reaches this runner.
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // VisibleTileSelectorDiagnosticTests — the engine-free frustum matches the real camera, tile-for-tile
    // ───────────────────────────────────────────────────────────────────────────────────

    public class VisibleTileSelectorDiagnosticTests : BaseTestFixture
    {
        // Sweep tilt at heading 0 up past 60°, where the frustum top grazes the horizon, then sweep heading at a
        // fixed steep tilt, where tilt and heading compound.
        [TestCase(0.0,  0.0)]
        [TestCase(30.0, 0.0)]
        [TestCase(45.0, 0.0)]
        [TestCase(60.0, 0.0)]   // ← the screenshot
        [TestCase(70.0, 0.0)]
        [TestCase(60.0, 30.0)]  // tilt + heading
        [TestCase(60.0, 45.0)]
        [TestCase(60.0, 90.0)]
        public void Diagnose_TiltedView_FlatAndLodCovers_vs_FrustumTraversal(double tiltDeg, double headingDeg)
        {
            // ── Scenario (matches the bug screenshot) ────────────────────────────────────────────────
            double2 vp   = new double2(1600, 900); // logical framing viewport (DPR=1 for the test)
            double  fov  = 60.0;
            var     cam  = new CameraProperties(
                new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52, Altitude = 0 },
                zoom: 13, heading: headingDeg, tilt: tiltDeg, verticalFovDeg: fov);
            var     proj = new WebMercatorProjection();

            // Selection zoom the pipeline uses (offset 0 under the 512 convention), clamped like the selector.
            const int minZoom = 0, maxZoom = 14, onScreenTilePx = 512;
            int offset  = (int)math.round(math.log2(WebMercator.TilePixelSize / onScreenTilePx));
            int targetZ = math.clamp(cam.IntegerZoom + offset, minZoom, maxZoom);

            // ── (1) CURRENT selectors — flat cover and screen-space LOD cover, same ViewContext ────────
            var viewContext = new ViewContext { Camera = cam, ViewportPx = vp, Projection = proj };

            var selector = new FrustumTileSelector(minZoom: minZoom, maxZoom: maxZoom,
                                                          onScreenTilePx: onScreenTilePx);
            var current = new List<TileId>();
            selector.SelectVisibleTiles(viewContext, current);

            var lodSelector = new FrustumTileSelector(minZoom: minZoom, maxZoom: maxZoom,
                                                      onScreenTilePx: onScreenTilePx,
                                                      lod: new ScreenSpaceLodStrategy(),
                                                      farPolicy: new GeometryAwareFarPlane());
            var lodCover = new List<TileId>();
            lodSelector.SelectVisibleTiles(viewContext, lodCover);

            // ── Real Unity camera, posed EXACTLY as MapCamera.SyncToCamera (DPR=1) ──────────────────────
            double altitude = CameraPoseMath.AltitudeForZoom(cam.Zoom, vp.y, fov);
            CameraPoseMath.ComputeRelativePose(altitude, cam.Heading.Value, cam.Tilt.Value,
                out double3 pos, out double3 fwd, out double3 up);

            var go   = new GameObject("TileDiagCam");
            var ucam = go.AddComponent<Camera>();
            var rt   = new RenderTexture((int)vp.x, (int)vp.y, 24);
            try
            {
                Track(go); // disposed at this block's end, BEFORE rt below — go/ucam must go first, or
                               // Unity logs an Error for a live camera whose targetTexture was destroyed.
                ucam.targetTexture     = rt;
                ucam.aspect            = (float)(vp.x / vp.y);
                ucam.fieldOfView       = (float)fov;
                ucam.nearClipPlane     = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
                ucam.farClipPlane      = (float)CameraPoseMath.FarClip(altitude);
                ucam.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
                ucam.transform.rotation = Quaternion.LookRotation(
                    new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                    new Vector3((float)up.x,  (float)up.y,  (float)up.z));
                ucam.enabled = false;

                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(ucam);

                // Render-space scene frame (look-at at the origin), matching MapView.BuildSceneFrame.
                var lookAt = new GeoCoordinate
                {
                    Latitude  = proj.ClampValidLatitude(cam.LookAt.Latitude),
                    Longitude = cam.LookAt.Longitude,
                };
                double3  origin = proj.Project(lookAt);
                float3x3 basis  = proj.TangentBasisAt(lookAt); // rebase = transpose(basis) ⇒ dot with columns

                // Shared render-space AABB of a tile's flat ground quad (thin vertical slab).
                void TileAabb(TileId t, out double3 min, out double3 max)
                {
                    min = new double3(double.MaxValue, double.MaxValue, double.MaxValue);
                    max = new double3(double.MinValue, double.MinValue, double.MinValue);
                    ReadOnlySpanCorners(out var corners);
                    for (int i = 0; i < corners.Length; i++)
                    {
                        double2 ll    = t.ToLonLat(corners[i].x, corners[i].y, 1.0);
                        double3 world = proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
                        double3 rel   = world - origin;
                        double rx = basis.c0.x * rel.x + basis.c0.y * rel.y + basis.c0.z * rel.z;
                        double ry = basis.c1.x * rel.x + basis.c1.y * rel.y + basis.c1.z * rel.z;
                        double rz = basis.c2.x * rel.x + basis.c2.y * rel.y + basis.c2.z * rel.z;
                        min = new double3(math.min(min.x, rx), math.min(min.y, ry), math.min(min.z, rz));
                        max = new double3(math.max(max.x, rx), math.max(max.y, ry), math.max(max.z, rz));
                    }
                    min = new double3(min.x, min.y - 2.0, min.z); // thin slab so the flat quad isn't degenerate
                    max = new double3(max.x, max.y + 2.0, max.z);
                }

                // The quadtree traversal, parameterised by the visibility oracle.
                List<TileId> Traverse(System.Func<TileId, bool> visible, out int testedCount)
                {
                    var outp = new List<TileId>();
                    var st = new Stack<TileId>();
                    st.Push(new TileId { Z = 0, X = 0, Y = 0 });
                    int n = 0;
                    while (st.Count > 0)
                    {
                        TileId t = st.Pop();
                        n++;
                        if (!visible(t)) continue;
                        if (t.Z >= targetZ) { outp.Add(t); continue; }
                        for (int dx = 0; dx < 2; dx++)
                            for (int dy = 0; dy < 2; dy++)
                                st.Push(new TileId { Z = t.Z + 1, X = t.X * 2 + dx, Y = t.Y * 2 + dy });
                    }
                    testedCount = n;
                    return outp;
                }

                // Exact visibility oracle: the tile's ground quad, clipped against the six Unity frustum planes, is
                // non-empty. TestPlanesAABB tests the AABB instead, a superset with false positives.
                Vector3[] TileQuad(TileId t)
                {
                    var q = new Vector3[4];
                    var uv = new (double x, double y)[] { (0, 0), (1, 0), (1, 1), (0, 1) };
                    for (int i = 0; i < 4; i++)
                    {
                        double2 ll = t.ToLonLat(uv[i].x, uv[i].y, 1.0);
                        double3 w  = proj.Project(new GeoCoordinate { Latitude = ll.y, Longitude = ll.x });
                        double3 rl = w - origin;
                        q[i] = new Vector3((float)(basis.c0.x*rl.x+basis.c0.y*rl.y+basis.c0.z*rl.z),
                                           (float)(basis.c1.x*rl.x+basis.c1.y*rl.y+basis.c1.z*rl.z),
                                           (float)(basis.c2.x*rl.x+basis.c2.y*rl.y+basis.c2.z*rl.z));
                    }
                    return q;
                }
                bool QuadMeetsFrustum(TileId t)
                {
                    var poly = new List<Vector3>(TileQuad(t));
                    foreach (Plane pl in planes) // Sutherland-Hodgman against each inward plane
                    {
                        var clip = new List<Vector3>();
                        for (int i = 0; i < poly.Count; i++)
                        {
                            Vector3 a = poly[i], b = poly[(i + 1) % poly.Count];
                            float da = pl.GetDistanceToPoint(a), db = pl.GetDistanceToPoint(b);
                            if (da >= 0) clip.Add(a);
                            if ((da >= 0) != (db >= 0)) clip.Add(Vector3.Lerp(a, b, da / (da - db)));
                        }
                        poly = clip;
                        if (poly.Count == 0) return false;
                    }
                    return true;
                }

                // ── (2) GROUND TRUTHS: exactTruth = visible (quad clip); truth = Unity's 6-plane AABB superset.
                // ViewFrustum sits between: it covers every visible tile; a reverse pre-cull drops false positives.
                var exactTruth = Traverse(QuadMeetsFrustum, out int tested);
                var truth = Traverse(t =>
                {
                    TileAabb(t, out double3 min, out double3 max);
                    var b = new Bounds();
                    b.SetMinMax(new Vector3((float)min.x, (float)min.y, (float)min.z),
                                new Vector3((float)max.x, (float)max.y, (float)max.z));
                    return GeometryUtility.TestPlanesAABB(planes, b);
                }, out _);

                // ── LINCHPIN — the engine-free ViewFrustum has no false negatives and never exceeds Unity's
                //    6-plane test. ────────────────────────────────────────────────────────────────────────────
                var frustum = ViewFrustum.FromPose(pos, fwd, up, fov, (double)ucam.aspect,
                                                   ucam.nearClipPlane, ucam.farClipPlane);
                var truthEF = Traverse(t =>
                {
                    TileAabb(t, out double3 min, out double3 max);
                    return frustum.IntersectsAabb(min, max);
                }, out _);

                CollectionAssert.IsSubsetOf(exactTruth, truthEF,
                    $"engine-free ViewFrustum dropped a genuinely-visible tile (false negative) " +
                    $"(tilt={tiltDeg}° heading={headingDeg}°) — exactVisible={exactTruth.Count}, ViewFrustum={truthEF.Count}");
                CollectionAssert.IsSubsetOf(truthEF, truth,
                    $"engine-free ViewFrustum selected a tile beyond Unity's 6-plane frustum " +
                    $"(tilt={tiltDeg}° heading={headingDeg}°) — ViewFrustum={truthEF.Count}, Unity={truth.Count}");

                // ── Diff + logs ─────────────────────────────────────────────────────────────────────────
                var cs      = new HashSet<TileId>(current);
                var es      = new HashSet<TileId>(exactTruth);
                var missing = exactTruth.Where(t => !cs.Contains(t)).ToList(); // visible but NOT selected → gaps
                var extra   = current.Where(t => !es.Contains(t)).ToList();    // selected but NOT visible → wasted

                // ── LOD partition check — each ground-truth tile needs exactly one ANCESTOR-OR-SELF in the LOD
                // cover, walked to z == 0. No cover tile is below g: targetZ caps the cover and g.Z == targetZ.
                var lodSet     = new HashSet<TileId>(lodCover);
                var holes      = new List<TileId>();
                var overlapped = new List<TileId>();
                foreach (TileId g in exactTruth)
                {
                    int n = 0;
                    for (int z = g.Z; z >= 0; z--)
                    {
                        int s = g.Z - z;
                        if (lodSet.Contains(new TileId { Z = z, X = g.X >> s, Y = g.Y >> s })) n++;
                    }
                    if (n == 0) holes.Add(g);
                    else if (n >= 2) overlapped.Add(g);
                }

                Debug.Log($"[TILEDIAG] scenario: Berlin z={cam.Zoom} tilt={cam.Tilt.Degrees}° heading={cam.Heading.Degrees}° " +
                          $"fov={fov} vp={vp.x}×{vp.y} → targetZ={targetZ}, altitude={altitude:F0}m, quadtree tested={tested} tiles");
                Debug.Log($"[TILEDIAG] CURRENT selector : {current.Count,4} tiles  {Fmt(current)}");
                Debug.Log($"[TILEDIAG] EXACT visible    : {exactTruth.Count,4} tiles (Unity 6-plane={truth.Count})  {Fmt(exactTruth)}");
                Debug.Log($"[TILEDIAG] MISSING (visible, NOT selected → white/gaps): {missing.Count,4}  {Fmt(missing)}");
                Debug.Log($"[TILEDIAG] EXTRA   (selected, NOT visible → wasted)    : {extra.Count,4}  {Fmt(extra)}");
                Debug.Log($"[TILEDIAG] extent — current X:[{Range(current, ti => ti.X)}] Y:[{Range(current, ti => ti.Y)}]  " +
                          $"exact X:[{Range(exactTruth, ti => ti.X)}] Y:[{Range(exactTruth, ti => ti.Y)}]");
                Debug.Log($"[TILEDIAG] LOD selector     : {lodCover.Count,4} tiles (z {Range(lodCover, ti => ti.Z)})  " +
                          $"holes={holes.Count} overlap={overlapped.Count}  {Fmt(lodCover)}");

                // ACCEPTANCE (the hard gate): the selector must request EVERY genuinely-visible tile — zero gaps
                // — at any tilt/heading. EXTRA (residual AABB false positives) is only logged, not asserted.
                Assert.AreEqual(0, missing.Count,
                    $"selector MUST cover every visible tile (tilt={tiltDeg}° heading={headingDeg}°); " +
                    $"{missing.Count} MISSING: {Fmt(missing)}");

                // LOD ACCEPTANCE: a coarse ancestor stands in for its children, so assert one ancestor-or-self per
                // truth tile. Holes (white gaps) and overlaps (double cover) are different defects; assert each.
                Assert.AreEqual(0, holes.Count,
                    $"screen-space LOD left {holes.Count} visible tile(s) uncovered (white gaps) at " +
                    $"tilt={tiltDeg}° heading={headingDeg}°: {Fmt(holes)}");
                // Tripwire: FrustumTileSelector emits a tile or descends, never both, so n >= 2 is unreachable.
                // A retain-until-replaced TileLodStrategy would make it reachable.
                Assert.AreEqual(0, overlapped.Count,
                    $"screen-space LOD covers {overlapped.Count} visible tile(s) more than once (a cover " +
                    $"tile and its own ancestor are both emitted) at tilt={tiltDeg}° heading={headingDeg}°: " +
                    $"{Fmt(overlapped)}");

                // ANTI-VACUITY 1, at every pose: a uniformly too-COARSE cover has no holes or overlaps, so the
                // near field must reach full detail.
                Assert.AreEqual(targetZ, lodCover.Max(t => t.Z),
                    $"screen-space LOD never reaches full detail at tilt={tiltDeg}° " +
                    $"heading={headingDeg}° — the near field must hit the target zoom z={targetZ}");

                // ANTI-VACUITY 2: the LOD arm must not be a second flat arm. Only at tilt >= 45, because below
                // that the shipped cover is single-zoom.
                if (tiltDeg >= 45.0)
                    Assert.Less(lodCover.Min(t => t.Z), lodCover.Max(t => t.Z),
                        $"screen-space LOD cover is single-zoom at tilt={tiltDeg}° heading={headingDeg}° — " +
                        $"the LOD arm is not exercising level-of-detail (did the wrong strategy get wired in?)");
            }
            finally
            {
                Object.DestroyImmediate(rt);
            }
        }

        private static void ReadOnlySpanCorners(out (double x, double y)[] corners)
            => corners = new (double, double)[] { (0, 0), (1, 0), (0, 1), (1, 1) };

        // Compact "z/x/y z/x/y …" (sorted, capped) for the log.
        private static string Fmt(List<TileId> tiles)
        {
            const int cap = 120;
            var sb = new StringBuilder();
            var sorted = tiles.OrderBy(t => t.Z).ThenBy(t => t.X).ThenBy(t => t.Y).ToList();
            for (int i = 0; i < sorted.Count && i < cap; i++)
                sb.Append(sorted[i].Z).Append('/').Append(sorted[i].X).Append('/').Append(sorted[i].Y).Append(' ');
            if (sorted.Count > cap) sb.Append("… (+").Append(sorted.Count - cap).Append(" more)");
            return sb.ToString();
        }

        private static string Range(List<TileId> tiles, System.Func<TileId, int> sel)
        {
            if (tiles.Count == 0) return "empty";
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var t in tiles) { int v = sel(t); if (v < lo) lo = v; if (v > hi) hi = v; }
            return $"{lo}..{hi}";
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorkSchedulerDecodeDispatchTests — TileDecodeDispatch.DecodeAsync over the real MVT fixture
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorkSchedulerDecodeDispatchTests
    {
        private static readonly TileId SomeTile = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>A decoder whose <see cref="Decode"/> always throws — the fault-path regression: a
        /// decoder throw must fault the returned <c>UniTask</c> with <see cref="TileDecodeException"/> and
        /// mint no <see cref="SharedDisposable{T}"/>, under EITHER scheduler policy.</summary>
        private sealed class ThrowingDecoder : ITileDecoder
        {
            public IDecodedTile Decode(TileId id, byte[] bytes) => throw new InvalidOperationException("boom");
        }

        // The two thread tests below do NOT await: an async test resumes wherever the runner's continuation
        // lands, so `caller` after an await would measure the runner. They park on WaitOffPlayerLoop instead.

        [Test]
        public void InlineScheduler_RunsDecodeOnCallingThread()
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            var probe = new LeaseProbeDecoder();

            UniTask<SharedDisposable<IDecodedTile>> task = TileDecodeDispatch.DecodeAsync(
                SomeTile, SampleTileFixture.Bytes(), probe, new InlineWorkScheduler()).Preserve();
            Assert.IsTrue(task.WaitOffPlayerLoop(10000), "sanity: Inline must already be terminal by return");
            SharedDisposable<IDecodedTile> handle = task.GetAwaiter().GetResult();

            Assert.IsNotNull(handle, "sanity: a real fixture must decode to a non-null handle");
            Assert.AreEqual(caller, probe.ThreadIdOfDecode(0),
                "Inline must run the decode body ON THE CALLING THREAD, with zero dispatch — the WebGL-" +
                "correct behaviour. A dispatch to any other thread (e.g. via ThreadPool.QueueUserWorkItem) " +
                "would record a different thread id here.");
            handle.Release();
        }

        [Test]
        public void ThreadPoolScheduler_RunsDecodeOffCallingThread()
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            var probe = new LeaseProbeDecoder();

            UniTask<SharedDisposable<IDecodedTile>> task = TileDecodeDispatch.DecodeAsync(
                SomeTile, SampleTileFixture.Bytes(), probe, new ThreadPoolWorkScheduler()).Preserve();
            Assert.IsTrue(task.WaitOffPlayerLoop(10000), "sanity: the decode must complete within the timeout");
            SharedDisposable<IDecodedTile> handle = task.GetAwaiter().GetResult();

            Assert.IsNotNull(handle, "sanity: a real fixture must decode to a non-null handle");
            Assert.AreNotEqual(caller, probe.ThreadIdOfDecode(0),
                "ThreadPool must NOT run the decode body on the calling thread — reproducing today's " +
                "UniTask.RunOnThreadPool parallelism. A decode recorded on the caller's own thread id means " +
                "the scheduler dispatched nowhere.");
            handle.Release();
        }

        [Test]
        public void ThreadPoolScheduler_CompletesOffThePlayerLoop()
        {
            var probe = new LeaseProbeDecoder();

            UniTask<SharedDisposable<IDecodedTile>> task = TileDecodeDispatch.DecodeAsync(
                SomeTile, SampleTileFixture.Bytes(), probe, new ThreadPoolWorkScheduler()).Preserve();

            // The wait TileManager.DrainMeshBuilds/DoDispose use, with NO PlayerLoop pumping. A bridge that
            // marshalled completion to the main thread would post it to the PlayerLoop, and this would time out.
            bool completed = task.WaitOffPlayerLoop(10000);

            Assert.IsTrue(completed,
                "the decode must complete OFF the PlayerLoop — TileManager's drain/dispose spins wait exactly " +
                "this way, with the PlayerLoop never pumped, and would deadlock against a bridge that " +
                "marshalled completion to the main thread.");
            task.GetAwaiter().GetResult().Release();
        }

        [TestCase(false, TestName = "DecoderFault_UnderInlineScheduler_FaultsWithTileDecodeException_AndMintsNoHandle")]
        [TestCase(true, TestName = "DecoderFault_UnderThreadPoolScheduler_FaultsWithTileDecodeException_AndMintsNoHandle")]
        public async Task DecoderFault_FaultsWithTileDecodeException_AndMintsNoHandle(bool useThreadPool)
        {
            IWorkScheduler scheduler = useThreadPool ? new ThreadPoolWorkScheduler() : new InlineWorkScheduler();

            Exception thrown = null;
            SharedDisposable<IDecodedTile> handle = null;
            try
            {
                handle = await TileDecodeDispatch.DecodeAsync(SomeTile, null, new ThrowingDecoder(), scheduler);
            }
            catch (Exception ex) { thrown = ex; }

            Assert.IsNull(handle,
                "a decoder throw must fault the TASK and mint no SharedDisposable — a failed decode can " +
                "never leak a reference.");
            Assert.IsInstanceOf<TileDecodeException>(thrown,
                "the fault must be wrapped as a TileDecodeException, unchanged by the scheduler policy.");
            Assert.IsInstanceOf<InvalidOperationException>(thrown.InnerException,
                "…with the decoder's own exception preserved underneath.");
        }
    }
}
