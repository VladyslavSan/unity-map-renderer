// Unity EditMode only — NativeArray, Burst jobs. NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Real Burst drivers over a decoded MVT layer: per-polygon (<see cref="RunLayer"/>, the production
    /// RingSelect → RingAssembly → gather → <see cref="EarcutJob"/> chain) or whole-layer raw triangle
    /// arrays from one <see cref="FillMeshGraph"/> dispatch (<see cref="BuildEarcutRootsFromFillGraph"/>).
    /// Shared by the re-homed corpus, parity and full-pipeline teeth.
    /// </summary>
    internal static class EarcutJobGatherHarness
    {
        internal struct GatherState
        {
            public NativeArray<int> PolyOuterIdx, PolyHoleStart, PolyHoleCount, HoleRingIdxs, PolyCountArr;
            public int PolyCount;
            public TriangulationBuffers Buffers;

            public void Dispose()
            {
                PolyOuterIdx.Dispose(); PolyHoleStart.Dispose(); PolyHoleCount.Dispose();
                HoleRingIdxs.Dispose(); PolyCountArr.Dispose();
                Buffers.DisposeAfter(default).Complete();
            }
        }

        /// <summary>Derives the visited-ring buffer (RingSelectJob's no-clip path), assembles polygons, sizes the
        /// flat buffers by hand as <see cref="SizingJob"/> does, then runs <see cref="FillGatherJob{TComparer}"/>.
        /// Caller disposes <paramref name="derived"/> and <paramref name="state"/>. Non-local invariant: the
        /// <c>PerPolyOuterCount</c> pre-sizing is load-bearing, because <see cref="FillGatherJob{TComparer}"/>
        /// bounds its loop by <c>Buffers.PerPolyOuterCount.Length</c>.</summary>
        internal static void BuildGatherState(
            TileGeometryBuffers geometry, NativeArray<int> visitOrder,
            out TileGeometryBuffers derived, out GatherState state)
        {
            var outVerts   = new NativeList<double2>(Allocator.Persistent);
            var outOffsets = new NativeList<int>(Allocator.Persistent);
            var outFeatIdx = new NativeList<int>(Allocator.Persistent);
            new RingSelectJob
            {
                Vertices = geometry.Vertices, RingOffsets = geometry.RingOffsets, RingFeatureIdx = geometry.RingFeatureIdx,
                RingVisitOrder = visitOrder,
                OutVertices = outVerts, OutRingOffsets = outOffsets, OutRingFeatureIdx = outFeatIdx,
            }.Run();
            derived = TileGeometryBuffers.AdoptDerivedLists(
                geometry.Tile, geometry.Extent, geometry.FeatureGeometryType, outVerts, outOffsets, outFeatIdx);
            Assert.Greater(derived.RingCount, 0, "precondition: the derived buffer has rings to assemble");

            int maxPolygons = math.max(1, derived.RingCapacity);
            var polyOuterIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleStart = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleCount = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var holeRingIdxs  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            new RingAssemblyJob
            {
                Vertices = derived.Vertices, RingOffsets = derived.RingOffsets, RingFeatureIdx = derived.RingFeatureIdx,
                RingCount = derived.RingCount, FeatureGeometryType = derived.FeatureGeometryType,
                OutPolyOuterRingIdx = polyOuterIdx, OutPolyHoleListStart = polyHoleStart, OutPolyHoleCount = polyHoleCount,
                OutHoleRingIdxs = holeRingIdxs, OutPolygonCount = polyCountArr, OutHoleCount = holeCountArr,
            }.Run();

            int polyCount = polyCountArr[0];
            holeCountArr.Dispose();
            Assert.Greater(polyCount, 0, "precondition: the layer has polygons");

            TriangulationBuffers buffers = TriangulationBuffers.Allocate();
            NativeList<int> vertexOffsets    = buffers.VertexOffsets;
            NativeList<int> holeCountOffsets = buffers.HoleCountOffsets;
            NativeList<int> workOffsets = buffers.WorkOffsets;
            NativeList<int> indexOffsets     = buffers.IndexOffsets;
            vertexOffsets.Add(0); holeCountOffsets.Add(0); workOffsets.Add(0); indexOffsets.Add(0);

            for (int pi = 0; pi < polyCount; pi++)
            {
                int outerRi   = polyOuterIdx[pi];
                int outerLen  = derived.RingOffsets[outerRi + 1] - derived.RingOffsets[outerRi];
                int holeCount = polyHoleCount[pi];
                int hStart    = polyHoleStart[pi];

                int holeVertTotal = 0;
                for (int hi = 0; hi < holeCount; hi++)
                {
                    int hri = holeRingIdxs[hStart + hi];
                    holeVertTotal += derived.RingOffsets[hri + 1] - derived.RingOffsets[hri];
                }

                int polyVC        = outerLen + holeVertTotal;
                int baseCap       = polyVC + holeCount * 2;
                int splitBudget   = math.min(EarcutJob.MaxSplits, math.max(8, holeCount * 4));
                int workCap       = baseCap + splitBudget * 2;
                int idxCap        = workCap > 2 ? (workCap - 2) * 3 : 3;
                int sortedHoleLen = holeCount > 0 ? holeCount : 1;

                vertexOffsets.Add(vertexOffsets[pi] + polyVC);
                holeCountOffsets.Add(holeCountOffsets[pi] + sortedHoleLen);
                workOffsets.Add(workOffsets[pi] + workCap);
                indexOffsets.Add(indexOffsets[pi] + idxCap);
            }

            buffers.FlatPolyVerts.Resize(vertexOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            buffers.FlatSortedHoleCounts.Resize(holeCountOffsets[polyCount], NativeArrayOptions.ClearMemory);
            buffers.PerPolyFeatureIndex.Resize(polyCount, NativeArrayOptions.UninitializedMemory);
            buffers.PerPolyOuterCount.Resize(polyCount, NativeArrayOptions.UninitializedMemory);

            var comparer = new FillMeshPipeline.HoleRingComparer(derived.Vertices, derived.RingOffsets);
            new FillGatherJob<FillMeshPipeline.HoleRingComparer>
            {
                Vertices = derived.Vertices, RingOffsets = derived.RingOffsets, RingFeatureIdx = derived.RingFeatureIdx,
                PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                HoleRingIdxs = holeRingIdxs,
                Comparer = comparer,
                Buffers = buffers,
            }.Run();

            state = new GatherState
            {
                PolyOuterIdx = polyOuterIdx, PolyHoleStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                HoleRingIdxs = holeRingIdxs, PolyCountArr = polyCountArr, PolyCount = polyCount,
                Buffers = buffers,
            };
        }

        /// <summary>One polygon actually triangulated by <see cref="RunLayer"/>, paired with the input
        /// rings it was built from — reconstructed from the gather state's own columns, NOT a separately
        /// assembled <c>PolygonAssembler</c> list, whose polygon decomposition order is not guaranteed to
        /// match <c>RingAssemblyJob</c>'s (pairing by index against one would be unsound).</summary>
        internal readonly struct PolygonRun
        {
            public readonly double2[] Vertices;
            public readonly int[]     Indices;
            public readonly int       ForceClips;
            public readonly long      CandidateVisits;
            public readonly List<double2>       InputOuter;
            public readonly List<List<double2>> InputHoles;

            public PolygonRun(double2[] vertices, int[] indices, int forceClips, long candidateVisits,
                List<double2> inputOuter, List<List<double2>> inputHoles)
            {
                Vertices = vertices; Indices = indices; ForceClips = forceClips; CandidateVisits = candidateVisits;
                InputOuter = inputOuter; InputHoles = inputHoles;
            }
        }

        /// <summary>Runs every polygon of one named layer — or, with <paramref name="layerName"/> null,
        /// every polygon-bearing layer in the tile — through the real gather chain then
        /// <see cref="EarcutJob"/>.Run(). Layers with no polygon features are skipped rather than
        /// tripping <see cref="BuildGatherState"/>'s polygon-count precondition.</summary>
        internal static List<PolygonRun> RunLayer(
            byte[] mvtBytes, string layerName, in TileId id, bool forceLinearEarScan)
        {
            var results = new List<PolygonRun>();
            using var mvtTile = MvtDecoder.Decode(id, mvtBytes);

            bool anyLayerProcessed = false;
            foreach (var layer in mvtTile.Layers)
            {
                if (layerName != null && layer.Name != layerName) continue;
                if (!HasPolygons(layer)) continue;

                anyLayerProcessed = true;
                RunOneLayer(layer.Geometry, id, forceLinearEarScan, results);
            }

            Assert.IsTrue(anyLayerProcessed,
                $"precondition: at least one polygon-bearing layer matched layerName={layerName ?? "(all)"}");
            return results;
        }

        private static bool HasPolygons(MvtLayer layer)
        {
            if (!layer.Geometry.IsCreated || layer.Geometry.RingCount == 0) return false;
            NativeArray<TileGeometryType> kinds = layer.Geometry.FeatureGeometryType;
            for (int i = 0; i < kinds.Length; i++)
                if (kinds[i] == TileGeometryType.Polygon) return true;
            return false;
        }

        private static void RunOneLayer(
            TileGeometryBuffers geometry, in TileId id, bool forceLinearEarScan, List<PolygonRun> results)
        {
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            BuildGatherState(geometry, visitOrder, out TileGeometryBuffers derived, out GatherState s);
            try
            {
                int polyCount = s.PolyCount;
                NativeArray<double2> flatVertsArr = s.Buffers.FlatPolyVerts.AsArray();
                NativeArray<int>     flatHoleArr  = s.Buffers.FlatSortedHoleCounts.AsArray();

                for (int pi = 0; pi < polyCount; pi++)
                    RunOnePolygon(s, flatVertsArr, flatHoleArr, pi, forceLinearEarScan, results);
            }
            finally
            {
                s.Dispose();
                derived.Dispose();
                visitOrder.Dispose();
            }
        }

        private static void RunOnePolygon(
            in GatherState s, NativeArray<double2> flatVertsArr, NativeArray<int> flatHoleArr, int pi,
            bool forceLinearEarScan, List<PolygonRun> results)
        {
            int vOff = s.Buffers.VertexOffsets[pi], vLen = s.Buffers.VertexOffsets[pi + 1] - vOff;
            int outerCount = s.Buffers.PerPolyOuterCount[pi];
            // HoleCountOffsets holds CAPACITY (SizingJob pads a zero-hole slot to 1 for EarcutJob); read the
            // REAL count from PolyHoleCount, or every zero-hole polygon gains a phantom empty hole.
            int hOff = s.Buffers.HoleCountOffsets[pi], hCap = s.Buffers.HoleCountOffsets[pi + 1] - hOff;
            int holeCount = s.PolyHoleCount[pi];

            var inputOuter = new List<double2>(outerCount);
            for (int i = 0; i < outerCount; i++) inputOuter.Add(flatVertsArr[vOff + i]);

            var inputHoles = new List<List<double2>>(holeCount);
            int vCursor = vOff + outerCount;
            for (int hi = 0; hi < holeCount; hi++)
            {
                int holeLen = flatHoleArr[hOff + hi];
                var holeRing = new List<double2>(holeLen);
                for (int i = 0; i < holeLen; i++) holeRing.Add(flatVertsArr[vCursor + i]);
                inputHoles.Add(holeRing);
                vCursor += holeLen;
            }

            int sOff = s.Buffers.WorkOffsets[pi], sLen = s.Buffers.WorkOffsets[pi + 1] - sOff;
            int idxOff = s.Buffers.IndexOffsets[pi], idxCap = s.Buffers.IndexOffsets[pi + 1] - idxOff;

            var outIdx = new NativeArray<int>(idxCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outIndexCount = new NativeArray<int>(1, Allocator.Persistent);
            var outForce = new NativeArray<int>(1, Allocator.Persistent);
            var outMergedVC = new NativeArray<int>(1, Allocator.Persistent);
            var outVisits = new NativeArray<long>(1, Allocator.Persistent);
            var v = new NativeArray<double2>(sLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var prev = new NativeArray<int>(sLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var next = new NativeArray<int>(sLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var bridge = new NativeArray<bool>(sLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var removed = new NativeArray<bool>(sLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var isEar = new NativeArray<bool>(sLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            try
            {
                new EarcutJob
                {
                    PolyVertices = flatVertsArr.GetSubArray(vOff, vLen),
                    OuterCount = outerCount,
                    SortedHoleCounts = flatHoleArr.GetSubArray(hOff, hCap),
                    HoleCount = holeCount,
                    ForceLinearEarScan = forceLinearEarScan,
                    OutIndices = outIdx, OutIndexOffset = 0,
                    OutIndexCount = outIndexCount, OutForceClipCount = outForce,
                    OutMergedVertexCount = outMergedVC, OutCandidateVisits = outVisits,
                    Verts = v, Prev = prev, Next = next,
                    IsBridgeCopy = bridge, Removed = removed, IsEar = isEar,
                }.Run();

                int mergedVertexCount = outMergedVC[0];
                int indexCount = outIndexCount[0];
                var resultVertices = new double2[mergedVertexCount];
                for (int i = 0; i < mergedVertexCount; i++) resultVertices[i] = v[i];
                var resultIndices = new int[indexCount];
                for (int i = 0; i < indexCount; i++) resultIndices[i] = outIdx[i];

                results.Add(new PolygonRun(
                    resultVertices, resultIndices, outForce[0], outVisits[0], inputOuter, inputHoles));
            }
            finally
            {
                outIdx.Dispose(); outIndexCount.Dispose(); outForce.Dispose(); outMergedVC.Dispose();
                outVisits.Dispose();
                v.Dispose(); prev.Dispose(); next.Dispose();
                bridge.Dispose(); removed.Dispose(); isEar.Dispose();
            }
        }

        /// <summary>Runs the real fill path for one fixture/layer's EARCUT-ONLY tile-space root triangles
        /// (<c>SuppressBoundaryBand = true</c>) and their real per-source-feature index. Non-local invariant:
        /// extraction always uses a flat projection, because a curved one makes
        /// <see cref="FillMeshGraph.Schedule"/> subdivide and return leaves, not roots.</summary>
        internal static (double2[] TileVerts, int[] TriangleIndices, int[] VertexFeatureIdx, double Extent)
            BuildEarcutRootsFromFillGraph(byte[] mvtBytes, string layerName, in TileId id)
        {
            using var mvtTile = MvtDecoder.Decode(id, mvtBytes);
            var layer = mvtTile.GetLayer(layerName);
            Assert.IsNotNull(layer, $"{layerName} layer present");

            double extent = layer.Extent;
            var (bMin, _) = id.MercatorBounds();

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var pipelineInput = new FillMeshPipeline.LayerInput
            {
                Geometry             = geometry,
                RingVisitOrder       = visitOrder,
                OriginRender         = new double3(bMin.x, 0.0, bMin.y),
                Projection           = new WebMercatorProjection(), // non-curved — earcut-only output
                SuppressBoundaryBand = true,
            };

            FillGraphOutput buffers = FillMeshGraph.Schedule(pipelineInput);
            buffers.Handle.Complete();
            try
            {
                Assert.IsTrue(buffers.IsCreated, $"{layerName}: jobified pipeline produced no buffers");
                return (buffers.TileVertices.AsArray().ToArray(), buffers.TriangleIndices.AsArray().ToArray(),
                        buffers.VertexFeatureIdx.AsArray().ToArray(), extent);
            }
            finally
            {
                buffers.Dispose();
                visitOrder.Dispose();
            }
        }
    }
}
