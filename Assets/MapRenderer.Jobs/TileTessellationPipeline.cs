using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Coordinates the Burst decode → ring assembly → earcut → projection job chain for one tile.
    ///
    /// <b>Pipeline output layout:</b>
    /// The managed <c>Earcut.Triangulate</c> returns a polygon-local flat vertex array (outer +
    /// bridge-copy vertices + holes in merged order) and local indices into that array. To maintain
    /// parity with the managed path, the jobified pipeline preserves the same structure:
    ///   - Each polygon produces its own flat vertex array inside EarcutJob scratch (Vx/Vy arrays).
    ///   - Triangle indices are local (0..polyMergedVertCount-1) within each polygon.
    ///   - A global flat vertex array and globally-offset index array are accumulated on the main
    ///     thread after all earcut jobs complete.
    ///   - ProjectTileVerticesJob projects the merged polygon vertices to world space.
    ///
    /// This gives bit-identical output to the managed path (integer tile-space coords are exact;
    /// same earcut algorithm produces same indices).
    ///
    /// <b>GC note:</b> the pre-pass allocates managed arrays once per newly-fetched tile. The
    /// hot-path steady state (mesh build + job completion) is GC-free.
    ///
    /// <b>NativeArray lifetime:</b> all buffers use <see cref="Allocator.Persistent"/> (tiles live
    /// multiple frames). <see cref="TileMeshBuffers.Dispose()"/> must be called after the pipeline
    /// handle completes — never dispose while jobs are in-flight.
    /// </summary>
    public static class TileTessellationPipeline
    {
        /// <summary>
        /// Input descriptor for one layer's polygon features, already decoded from MVT bytes.
        /// </summary>
        public struct LayerInput
        {
            /// <summary>Polygon features: each element is the geometry command array from MvtDecoder.</summary>
            public List<uint[]> FeatureGeometries;
            /// <summary>MVT tile extent (typically 4096).</summary>
            public double Extent;
            public int TileZ, TileX, TileY;
            public double OriginMercX, OriginMercY;
        }

        /// <summary>
        /// Schedule the full pipeline for one tile. Returns a <see cref="TileMeshBuffers"/> whose
        /// data is fully computed (all jobs already Complete'd).
        ///
        /// Thread: must be called from the main thread (NativeArray allocation + job scheduling).
        ///
        /// The caller owns the returned <see cref="TileMeshBuffers"/> and must call Dispose().
        /// </summary>
        public static TileMeshBuffers Schedule(LayerInput input)
        {
            var features     = input.FeatureGeometries;
            int featureCount = features == null ? 0 : features.Count;

            if (featureCount == 0)
                return default;

            // ── Pre-pass: flatten polygon commands → NativeArrays. ─────────────────────────────
            int totalCommands = 0;
            for (int fi = 0; fi < featureCount; fi++)
                totalCommands += features[fi]?.Length ?? 0;

            int maxRings    = totalCommands / 3 + featureCount + 2;
            int maxVertices = totalCommands + 4;
            int maxPolygons = maxRings;
            int maxHoles    = maxRings;

            // Note: not using 'using var' because C# 8+ makes 'using var' NativeArrays read-only
            // (CS1654), preventing index assignment. Dispose manually below.
            var commands    = new NativeArray<uint>(totalCommands, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featOffsets = new NativeArray<int>(featureCount,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featLengths = new NativeArray<int>(featureCount,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            int cmdPos = 0;
            for (int fi = 0; fi < featureCount; fi++)
            {
                uint[] geom = features[fi];
                int len = geom?.Length ?? 0;
                featOffsets[fi] = cmdPos;
                featLengths[fi] = len;
                if (geom != null)
                    for (int k = 0; k < len; k++)
                        commands[cmdPos + k] = geom[k];
                cmdPos += len;
            }

            // ── Allocate decode output buffers. ───────────────────────────────────────────────────
            var tileVerts   = new NativeArray<double2>(maxVertices, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var ringOffsets = new NativeArray<int>(maxRings + 1,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var ringFeatIdx = new NativeArray<int>(maxRings,        Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var ringCountArr = new NativeArray<int>(1,              Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var vertCountArr = new NativeArray<int>(1,              Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // ── Stage 1: decode. ───────────────────────────────────────────────────────────────────
            new MvtDecodeJob
            {
                Commands            = commands,
                FeatureOffsets      = featOffsets,
                FeatureLengths      = featLengths,
                OutVertices         = tileVerts,
                OutRingOffsets      = ringOffsets,
                OutRingFeatureIndex = ringFeatIdx,
                OutRingCount        = ringCountArr,
                OutVertexCount      = vertCountArr,
            }.Schedule().Complete();

            commands.Dispose();
            featOffsets.Dispose();
            featLengths.Dispose();

            int ringCount = ringCountArr[0];
            ringCountArr.Dispose();
            vertCountArr.Dispose();

            // ── Stage 2: ring assembly. ────────────────────────────────────────────────────────────
            var polyOuterIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleStart = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleCount = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var holeRingIdxs  = new NativeArray<int>(maxHoles,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyCountArr  = new NativeArray<int>(1,           Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeCountArr  = new NativeArray<int>(1,           Allocator.Persistent, NativeArrayOptions.ClearMemory);

            new RingAssemblyJob
            {
                Vertices             = tileVerts,
                RingOffsets          = ringOffsets,
                RingFeatureIdx       = ringFeatIdx,
                RingCount            = ringCount,
                OutPolyOuterRingIdx  = polyOuterIdx,
                OutPolyHoleListStart = polyHoleStart,
                OutPolyHoleCount     = polyHoleCount,
                OutHoleRingIdxs      = holeRingIdxs,
                OutPolygonCount      = polyCountArr,
                OutHoleCount         = holeCountArr,
            }.Schedule().Complete();

            int polyCount    = polyCountArr[0];
            int totalHoles   = holeCountArr[0];
            polyCountArr.Dispose();
            holeCountArr.Dispose();

            if (polyCount == 0)
            {
                tileVerts.Dispose(); ringOffsets.Dispose(); ringFeatIdx.Dispose();
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose();
                return default;
            }

            // ── Stage 3: per-polygon earcut jobs. ──────────────────────────────────────────────────
            var perPolyVerts         = new NativeArray<double2>[polyCount];
            var perPolySortedHoleCnt = new NativeArray<int>[polyCount];
            var perPolyIdxCount      = new NativeArray<int>[polyCount];
            var perPolyForceClip     = new NativeArray<int>[polyCount];
            var scratchVx            = new NativeArray<double>[polyCount];
            var scratchVy            = new NativeArray<double>[polyCount];
            var scratchPrev          = new NativeArray<int>[polyCount];
            var scratchNext          = new NativeArray<int>[polyCount];
            var scratchIsBridge      = new NativeArray<bool>[polyCount];
            var scratchRemoved       = new NativeArray<bool>[polyCount];
            var scratchIsEar         = new NativeArray<bool>[polyCount];
            var perPolyIdxArrays     = new NativeArray<int>[polyCount];
            int[] perPolyMergedVC    = new int[polyCount];

            for (int pi = 0; pi < polyCount; pi++)
            {
                int outerRi    = polyOuterIdx[pi];
                int outerStart = ringOffsets[outerRi];
                int outerLen   = ringOffsets[outerRi + 1] - outerStart;
                int holeCount  = polyHoleCount[pi];
                int hStart     = polyHoleStart[pi];

                // Collect and sort hole ring indices for deterministic bridge order.
                // Sort: (leftmost-x, min-y, ring-index) — must match managed validHoles.Sort.
                int[] holeRIs = new int[holeCount];
                for (int hi = 0; hi < holeCount; hi++)
                    holeRIs[hi] = holeRingIdxs[hStart + hi];

                Array.Sort(holeRIs, (a, b) =>
                {
                    double ax = LeftmostX(tileVerts, ringOffsets[a], ringOffsets[a + 1] - ringOffsets[a]);
                    double bx = LeftmostX(tileVerts, ringOffsets[b], ringOffsets[b + 1] - ringOffsets[b]);
                    int cmp = ax.CompareTo(bx);
                    if (cmp != 0) return cmp;
                    double ay = MinY(tileVerts, ringOffsets[a], ringOffsets[a + 1] - ringOffsets[a]);
                    double by = MinY(tileVerts, ringOffsets[b], ringOffsets[b + 1] - ringOffsets[b]);
                    cmp = ay.CompareTo(by);
                    if (cmp != 0) return cmp;
                    return a.CompareTo(b);
                });

                // Build flat poly verts: outer + holes in sorted order.
                int polyVC = outerLen;
                for (int hi = 0; hi < holeCount; hi++)
                    polyVC += ringOffsets[holeRIs[hi] + 1] - ringOffsets[holeRIs[hi]];

                var polyVerts        = new NativeArray<double2>(polyVC, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var sortedHoleCounts = new NativeArray<int>(holeCount > 0 ? holeCount : 1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

                for (int i = 0; i < outerLen; i++)
                    polyVerts[i] = tileVerts[outerStart + i];

                int vPos = outerLen;
                for (int hi = 0; hi < holeCount; hi++)
                {
                    int hri    = holeRIs[hi];
                    int hBegin = ringOffsets[hri];
                    int hLen   = ringOffsets[hri + 1] - hBegin;
                    sortedHoleCounts[hi] = hLen;
                    for (int i = 0; i < hLen; i++)
                        polyVerts[vPos++] = tileVerts[hBegin + i];
                }

                perPolyVerts[pi]         = polyVerts;
                perPolySortedHoleCnt[pi] = sortedHoleCounts;

                // EarcutJob scratch capacity: polyVC + 2 per hole (bridge-copy slots).
                int scratchCap = polyVC + holeCount * 2;
                int idxCap     = scratchCap > 2 ? (scratchCap - 2) * 3 : 3;
                perPolyMergedVC[pi] = scratchCap;   // full merged ring size (with bridge copies)

                perPolyIdxArrays[pi] = new NativeArray<int>(idxCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                perPolyIdxCount[pi]  = new NativeArray<int>(1,      Allocator.Persistent, NativeArrayOptions.ClearMemory);
                perPolyForceClip[pi] = new NativeArray<int>(1,      Allocator.Persistent, NativeArrayOptions.ClearMemory);
                scratchVx[pi]        = new NativeArray<double>(scratchCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                scratchVy[pi]        = new NativeArray<double>(scratchCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                scratchPrev[pi]      = new NativeArray<int>(scratchCap,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                scratchNext[pi]      = new NativeArray<int>(scratchCap,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                scratchIsBridge[pi]  = new NativeArray<bool>(scratchCap,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                scratchRemoved[pi]   = new NativeArray<bool>(scratchCap,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                scratchIsEar[pi]     = new NativeArray<bool>(scratchCap,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            // Schedule all earcut jobs (parallel across polygons).
            var earcutHandles = new NativeArray<JobHandle>(polyCount, Allocator.Temp);
            for (int pi = 0; pi < polyCount; pi++)
            {
                int outerLen  = ringOffsets[polyOuterIdx[pi] + 1] - ringOffsets[polyOuterIdx[pi]];
                int holeCount = polyHoleCount[pi];
                earcutHandles[pi] = new EarcutJob
                {
                    PolyVertices      = perPolyVerts[pi],
                    OuterCount        = outerLen,
                    SortedHoleCounts  = perPolySortedHoleCnt[pi],
                    HoleCount         = holeCount,
                    OutIndices        = perPolyIdxArrays[pi],
                    OutIndexOffset    = 0,
                    OutIndexCount     = perPolyIdxCount[pi],
                    OutForceClipCount = perPolyForceClip[pi],
                    Vx                = scratchVx[pi],
                    Vy                = scratchVy[pi],
                    Prev              = scratchPrev[pi],
                    Next              = scratchNext[pi],
                    IsBridgeCopy      = scratchIsBridge[pi],
                    Removed           = scratchRemoved[pi],
                    IsEar             = scratchIsEar[pi],
                }.Schedule();
            }
            JobHandle.CompleteAll(earcutHandles);
            earcutHandles.Dispose();

            // No longer need ring/assembly data.
            tileVerts.Dispose(); ringOffsets.Dispose(); ringFeatIdx.Dispose();
            polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
            holeRingIdxs.Dispose();

            // ── Aggregate: build global merged-vertex and index arrays. ───────────────────────────
            // The EarcutJob writes vertices into scratchVx/Vy[0..mergedVC-1] and indices into
            // perPolyIdxArrays[pi][0..idxCount-1]. We concatenate these per-polygon arrays into
            // global arrays, offsetting indices by the running global vertex base.
            int totalMergedVerts = 0;
            int totalIdxCount    = 0;
            int totalForceClips  = 0;
            for (int pi = 0; pi < polyCount; pi++)
            {
                totalMergedVerts += perPolyMergedVC[pi];
                totalIdxCount    += perPolyIdxCount[pi][0];
                totalForceClips  += perPolyForceClip[pi][0];
            }

            var outMergedVerts = new NativeArray<double2>(totalMergedVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outWorldPos    = new NativeArray<float3>(totalMergedVerts,  Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outIndices     = new NativeArray<int>(totalIdxCount > 0 ? totalIdxCount : 1,
                                                      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            int globalVertBase = 0;
            int globalIdxBase  = 0;
            for (int pi = 0; pi < polyCount; pi++)
            {
                int mergedVC = perPolyMergedVC[pi];
                int idxCount = perPolyIdxCount[pi][0];

                for (int i = 0; i < mergedVC; i++)
                    outMergedVerts[globalVertBase + i] = new double2(scratchVx[pi][i], scratchVy[pi][i]);

                for (int i = 0; i < idxCount; i++)
                    outIndices[globalIdxBase + i] = perPolyIdxArrays[pi][i] + globalVertBase;

                globalVertBase += mergedVC;
                globalIdxBase  += idxCount;
            }

            // Dispose per-polygon scratch.
            for (int pi = 0; pi < polyCount; pi++)
            {
                perPolyVerts[pi].Dispose();
                perPolySortedHoleCnt[pi].Dispose();
                perPolyIdxArrays[pi].Dispose();
                perPolyIdxCount[pi].Dispose();
                perPolyForceClip[pi].Dispose();
                scratchVx[pi].Dispose(); scratchVy[pi].Dispose();
                scratchPrev[pi].Dispose(); scratchNext[pi].Dispose();
                scratchIsBridge[pi].Dispose(); scratchRemoved[pi].Dispose(); scratchIsEar[pi].Dispose();
            }

            // ── Stage 4: project merged vertices to world space. ──────────────────────────────────
            new ProjectTileVerticesJob
            {
                TileZ          = input.TileZ,
                TileX          = input.TileX,
                TileY          = input.TileY,
                Extent         = input.Extent,
                OriginMercX    = input.OriginMercX,
                OriginMercY    = input.OriginMercY,
                TileCoords     = outMergedVerts,
                WorldPositions = outWorldPos,
            }.Schedule(totalMergedVerts, 64).Complete();

            // ── Build output TileMeshBuffers. ─────────────────────────────────────────────────────
            var vertCountFinal = new NativeArray<int>(1, Allocator.Persistent);
            var polyCountFinal = new NativeArray<int>(1, Allocator.Persistent);
            var ringCountFinal = new NativeArray<int>(1, Allocator.Persistent);
            var holeCountFinal = new NativeArray<int>(1, Allocator.Persistent);
            vertCountFinal[0]  = totalMergedVerts;
            polyCountFinal[0]  = polyCount;
            ringCountFinal[0]  = ringCount;
            holeCountFinal[0]  = totalHoles;

            return new TileMeshBuffers
            {
                TileVertices        = outMergedVerts,
                WorldPositions      = outWorldPos,
                TriangleIndices     = outIndices,
                VertexCount         = vertCountFinal,
                PolygonCount        = polyCountFinal,
                RingCount           = ringCountFinal,
                HoleCount           = holeCountFinal,
                TotalIndexCount     = totalIdxCount,
                TotalForceClipCount = totalForceClips,
                PipelineHandle      = default,
                IsCreated           = true,
                // Fields not used in this output path:
                RingOffsets         = default,
                RingFeatureIdx      = default,
                PolyOuterRingIdx    = default,
                PolyHoleListStart   = default,
                PolyHoleCount       = default,
                HoleRingIdxs        = default,
            };
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        private static double LeftmostX(NativeArray<double2> verts, int start, int len)
        {
            double minX = double.MaxValue;
            for (int i = 0; i < len; i++)
                if (verts[start + i].x < minX) minX = verts[start + i].x;
            return minX;
        }

        private static double MinY(NativeArray<double2> verts, int start, int len)
        {
            double minY = double.MaxValue;
            for (int i = 0; i < len; i++)
                if (verts[start + i].y < minY) minY = verts[start + i].y;
            return minY;
        }
    }
}
