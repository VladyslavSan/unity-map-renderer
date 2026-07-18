using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Coordinates the Burst decode → ring assembly → earcut → projection job chain for one tile — the
    /// <b>fill</b> mesh pipeline (Decode → Assemble → Triangulate → Project; the globe Subdivide + the
    /// mesh write happen in <c>StyledFillTileBuilder</c>). Per-kind stage orderings differ:
    /// fills Triangulate before Project; lines the opposite.
    ///
    /// <b>Pipeline output layout:</b>
    /// The managed <c>Earcut.Triangulate</c> returns a polygon-local flat vertex array (outer +
    /// bridge-copy vertices + holes in merged order) and local indices into that array. To maintain
    /// parity with the managed path, the jobified pipeline preserves the same structure:
    ///   - Each polygon produces its own flat vertex array inside EarcutJob scratch (Vx/Vy arrays).
    ///   - Triangle indices are local (0..polyMergedVertCount-1) within each polygon.
    ///   - A global flat vertex array and globally-offset index array are accumulated on the main
    ///     thread after all earcut jobs complete.
    ///   - TileToGeoJob + ProjectPointsJob&lt;TProj&gt; project the merged vertices to world (double3 + up).
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
    public static class FillMeshPipeline
    {
        // Pipeline-stage profiler markers (MapRenderer.Pipeline.*).
        // These sit on the schedule-then-Complete main-thread path — exactly the stall the perf epic measures.
        // Separate path from the live MapView loop; wired for the Profiler window, not for the recorder test.
        private static readonly ProfilerMarker PmPipelineDecode      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Pipeline.Decode");
        private static readonly ProfilerMarker PmPipelineRingAssembly = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Pipeline.RingAssembly");
        private static readonly ProfilerMarker PmPipelineEarcut      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Pipeline.Earcut");
        private static readonly ProfilerMarker PmPipelineProject     = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Pipeline.Project");

        // MVT command IDs (per MVT spec §4.3) — must match MvtDecodeJob's constants.
        private const uint MoveTo = 1;
        private const uint LineTo = 2;

        /// <summary>
        /// Exact pre-count of the rings and vertices <see cref="MvtDecodeJob.Execute"/> will emit for a
        /// layer's flattened command stream — computed by walking every command exactly as the decode job
        /// does (S06 gated follow-up, item a; the fix that closes it for real).
        ///
        /// <b>Why exact (not a heuristic).</b> The decode job emits one ring AND one vertex per MoveTo point,
        /// and one vertex per LineTo point. So:
        ///   - <c>rings    = Σ over all MoveTo commands of (count)</c>
        ///   - <c>vertices = Σ over all MoveTo + LineTo commands of (count)</c>
        /// These are the precise quantities the job writes, for ANY input — including a malformed multi-point
        /// <c>MoveTo</c> (count=N), which starts N rings from a single header. The old heuristic
        /// (<c>maxRings = totalCommands/3 + featureCount + 2</c>) was correct only for spec-compliant polygons
        /// (one MoveTo per ring → ≥3 cmd uints/ring) and UNDER-allocated for a multi-point MoveTo
        /// (counterexample: one MoveTo count=11 → 11 rings, but the heuristic sized maxRings=10), letting
        /// <see cref="MvtDecodeJob"/> write out of range and corrupt adjacent <see cref="NativeArray{T}"/>
        /// memory in a release build (where <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> is stripped). Sizing from
        /// this exact pre-count makes under-allocation impossible, so the OOB write cannot occur in any build.
        ///
        /// <b>Must mirror <see cref="MvtDecodeJob.Execute"/> exactly.</b> The job advances its read cursor by
        /// 2 param uints per MoveTo/LineTo point and reads no params for ClosePath or any unknown command. We
        /// walk the commands the same way here. If you change one, change the other — they desync silently
        /// otherwise and re-introduce the overflow.
        ///
        /// Note (latent, out of scope here): a truncated/malformed param stream can make the decode job read
        /// PAST the feature's command range (an input-read OOB, distinct from the output-write OOB this closes).
        /// The counts computed here still match what the job writes, so the sizing is correct regardless; the
        /// input-read hardening is tracked separately.
        /// </summary>
        public static void PrecountRingsAndVertices(List<uint[]> features, out int rings, out int vertices)
        {
            rings    = 0;
            vertices = 0;
            if (features == null) return;

            for (int fi = 0; fi < features.Count; fi++)
            {
                uint[] geom = features[fi];
                if (geom == null) continue;

                int i = 0;
                int len = geom.Length;
                while (i < len)
                {
                    uint commandInteger = geom[i++];
                    uint command = commandInteger & 0x7u;
                    uint count   = commandInteger >> 3;

                    if (command == MoveTo)
                    {
                        // One ring AND one vertex per point; 2 param uints each.
                        rings    += (int)count;
                        vertices += (int)count;
                        i        += 2 * (int)count;
                    }
                    else if (command == LineTo)
                    {
                        // One vertex per point; 2 param uints each. No new ring.
                        vertices += (int)count;
                        i        += 2 * (int)count;
                    }
                    // ClosePath / unknown: header consumed, no params (matches the job's i++ only).
                }
            }
        }

        /// <summary>
        /// Never-fired capacity backstop for the sizing pre-pass (S06 gated follow-up, item a).
        ///
        /// With the exact <see cref="PrecountRingsAndVertices"/> sizing, the decode/ring-assembly jobs can
        /// never report a count exceeding the buffers they were sized for, so this <c>if</c> never fires. It is
        /// kept as defense-in-depth: a plain <c>if</c> (NOT behind <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, so it
        /// runs in Editor and release alike) that would fail fast — loudly, before any further processing —
        /// should a future sizing miscalculation ever under-allocate.
        /// </summary>
        /// <param name="count">The actual count reported by a job (or computed in the pre-pass).</param>
        /// <param name="capacity">The capacity the buffer was sized to.</param>
        /// <param name="what">A short label naming the quantity, for the exception message.</param>
        public static void EnsureCapacity(int count, int capacity, string what)
        {
            if (count > capacity)
                throw new InvalidOperationException(
                    $"FillMeshPipeline sizing overflow: {what} count {count} exceeds pre-sized " +
                    $"capacity {capacity}. With exact PrecountRingsAndVertices sizing this should be " +
                    "unreachable — it indicates a sizing-vs-decode desync (the pre-count walk no longer " +
                    "mirrors MvtDecodeJob.Execute). Fix the pre-count to match the decode job.");
        }

        /// <summary>
        /// Input descriptor for one layer's polygon features, already decoded from MVT bytes.
        /// </summary>
        public struct LayerInput
        {
            /// <summary>Polygon features: each element is the geometry command array from MvtDecoder.</summary>
            public List<uint[]> FeatureGeometries;
            /// <summary>MVT tile extent (typically 4096).</summary>
            public double Extent;
            /// <summary>The slippy-map address (z/x/y) of the tile being built.</summary>
            public TileId Tile;

            /// <summary>S91-C: the RTC render-space origin (docs §5) the mesh vertices are baked relative to —
            /// the tile's SW corner projected through <see cref="Projection"/>. The single source of the
            /// bake origin, shared with the tile transform (Mercator: <c>(mercX, 0, mercZ)</c>; globe: the
            /// corner's ECEF). Use <c>TileRenderOrigin.Project</c> (Core) to compute it.</summary>
            public double3 OriginRender;

            /// <summary>S91: the projection the geometry is built with (a stateless struct behind
            /// <see cref="MapRenderer.Core.Geo.IProjection"/>). Left <c>null</c> ⇒ Web Mercator (planar).</summary>
            public MapRenderer.Core.Geo.IProjection Projection;
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

            // Exact sizing: walk every command exactly as MvtDecodeJob does to pre-count the rings and
            // vertices it will emit (S06 item a). This makes under-allocation — and thus the in-job OOB write
            // — impossible for ANY input, including a malformed multi-point MoveTo. Each ring is classified
            // exactly once by RingAssemblyJob (outer / hole / skipped), so polygons and holes each number at
            // most `exactRings`; sizing those to exactRings is the tight safe bound (they cannot be exact
            // pre-counted without running the area classification).
            PrecountRingsAndVertices(features, out int exactRings, out int exactVertices);
            int maxRings    = exactRings;
            int maxVertices = exactVertices;
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
            {
                using var sPipelineDecode = PmPipelineDecode.Auto();
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
                }.Run(); // Run (not Schedule) so the pipeline is callable off the main thread (S89 D2 worker path)
            }

            commands.Dispose();
            featOffsets.Dispose();
            featLengths.Dispose();

            int ringCount = ringCountArr[0];
            int decodedVertCount = vertCountArr[0];
            ringCountArr.Dispose();
            vertCountArr.Dispose();

            // Never-fired backstop: with exact PrecountRingsAndVertices sizing the decode job's reported
            // ring/vertex counts equal the buffer capacities, so these cannot trip. Kept as defense-in-depth
            // against a future sizing-vs-decode desync. (S06 gated item a; see EnsureCapacity doc.)
            EnsureCapacity(ringCount, maxRings, "ring");
            EnsureCapacity(decodedVertCount, maxVertices, "decoded vertex");

            // ── Stage 2: ring assembly. ────────────────────────────────────────────────────────────
            var polyOuterIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleStart = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleCount = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var holeRingIdxs  = new NativeArray<int>(maxHoles,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyCountArr  = new NativeArray<int>(1,           Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeCountArr  = new NativeArray<int>(1,           Allocator.Persistent, NativeArrayOptions.ClearMemory);

            {
                using var sPipelineRingAssembly = PmPipelineRingAssembly.Auto();
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
                }.Run();
            }

            int polyCount    = polyCountArr[0];
            int totalHoles   = holeCountArr[0];
            polyCountArr.Dispose();
            holeCountArr.Dispose();

            // Never-fired backstop: each ring is classified exactly once (outer / hole / skipped), so
            // polyCount + totalHoles ≤ ringCount ≤ maxRings = maxPolygons = maxHoles. Cannot trip with exact
            // sizing. (S06 gated item a.)
            EnsureCapacity(polyCount, maxPolygons, "polygon");
            EnsureCapacity(totalHoles, maxHoles, "hole");

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
            var perPolyMergedVertCnt = new NativeArray<int>[polyCount]; // EarcutJob.OutMergedVertexCount (Stage 3)
            int[] perPolyMergedVC    = new int[polyCount];
            int[] perPolyFeatureIdx  = new int[polyCount]; // S89 D2: feature index of each polygon (for per-vertex color)

            for (int pi = 0; pi < polyCount; pi++)
            {
                int outerRi    = polyOuterIdx[pi];
                perPolyFeatureIdx[pi] = ringFeatIdx[outerRi]; // captured before ringFeatIdx is disposed post-earcut
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

                // EarcutJob scratch capacity (mesh-triangulation-robustness Stage 3): baseCap = polyVC +
                // 2 per hole (bridge-copy slots) is the deterministic merged-ring size on clean input —
                // matches managed Earcut's initial `capacity` exactly. The cure → split → clean-drop
                // cascade's SplitPolygon adds 2 verts per split, bounded by EarcutJob.MaxSplits (512,
                // mirrored exactly from managed Earcut.MaxSplits) — but splits are a FAILURE-PATH escape
                // only (0 on the clean corpus; see EarcutJob class doc). Pre-size a bounded, tile-
                // appropriate SPLIT HEADROOM rather than the worst-case 2*MaxSplits (which would double
                // every polygon's scratch footprint for a path that never fires on real input); on
                // exhaustion EarcutJob.TrySplit refuses the split (never writes past these arrays) and the
                // job drops the locus cleanly instead — see EarcutJob's OutForceClipCount doc.
                int baseCap           = polyVC + holeCount * 2;
                int splitBudget       = math.min(EarcutJob.MaxSplits, math.max(8, holeCount * 4));
                int splitHeadroomVerts = splitBudget * 2;
                int scratchCap        = baseCap + splitHeadroomVerts;
                int idxCap            = scratchCap > 2 ? (scratchCap - 2) * 3 : 3;
                perPolyMergedVertCnt[pi] = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

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

            // Run all earcut jobs sequentially (each polygon is independent — Run, not Schedule, so the
            // pipeline is callable off the main thread; per-polygon parallelism is a later throughput knob).
            {
                using var sPipelineEarcut = PmPipelineEarcut.Auto();
                for (int pi = 0; pi < polyCount; pi++)
                {
                    int outerLen  = ringOffsets[polyOuterIdx[pi] + 1] - ringOffsets[polyOuterIdx[pi]];
                    int holeCount = polyHoleCount[pi];
                    new EarcutJob
                    {
                        PolyVertices         = perPolyVerts[pi],
                        OuterCount           = outerLen,
                        SortedHoleCounts     = perPolySortedHoleCnt[pi],
                        HoleCount            = holeCount,
                        OutIndices           = perPolyIdxArrays[pi],
                        OutIndexOffset       = 0,
                        OutIndexCount        = perPolyIdxCount[pi],
                        OutForceClipCount    = perPolyForceClip[pi],
                        OutMergedVertexCount = perPolyMergedVertCnt[pi],
                        Vx                   = scratchVx[pi],
                        Vy                   = scratchVy[pi],
                        Prev                 = scratchPrev[pi],
                        Next                 = scratchNext[pi],
                        IsBridgeCopy         = scratchIsBridge[pi],
                        Removed              = scratchRemoved[pi],
                        IsEar                = scratchIsEar[pi],
                    }.Run();

                    // Read the job's ACTUAL final merged vertex count (base bridged count + any
                    // split-added verts) — never assume the pre-sized scratch capacity, since the split
                    // headroom typically goes unused (scratchVx[pi] tail beyond this is unwritten scratch,
                    // not part of the triangulation). Never-fired backstop: EarcutJob.TrySplit's own
                    // capacity guard makes this exceeding scratchVx[pi].Length unreachable.
                    perPolyMergedVC[pi] = perPolyMergedVertCnt[pi][0];
                    EnsureCapacity(perPolyMergedVC[pi], scratchVx[pi].Length, "earcut merged vertex (per polygon)");
                }
            }

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
            var outWorldPos    = new NativeArray<double3>(totalMergedVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outVertexUp    = new NativeArray<double3>(totalMergedVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outVertexFeat  = new NativeArray<int>(totalMergedVerts > 0 ? totalMergedVerts : 1,
                                                      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var outIndices     = new NativeArray<int>(totalIdxCount > 0 ? totalIdxCount : 1,
                                                      Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            // This buffer is the CANONICAL earcut IR: raw CCW-in-tile-space winding, byte-identical to the managed
            // earcut reference (JobifiedPipelineTests parity), and projection-independent. The Unity-front winding
            // conversion (for stock Cull Back) is applied DOWNSTREAM at the GPU mesh-write boundary — the ribbon in
            // StyledLineTileBuilder and both fill writes in StyledFillTileBuilder — never here; keep this IR
            // convention-neutral so the earcut parity + globe-subdivide parity oracles hash raw winding.
            int globalVertBase = 0;
            int globalIdxBase  = 0;
            for (int pi = 0; pi < polyCount; pi++)
            {
                int mergedVC = perPolyMergedVC[pi];
                int idxCount = perPolyIdxCount[pi][0];
                int featIdx  = perPolyFeatureIdx[pi];

                for (int i = 0; i < mergedVC; i++)
                {
                    outMergedVerts[globalVertBase + i] = new double2(scratchVx[pi][i], scratchVy[pi][i]);
                    outVertexFeat[globalVertBase + i]  = featIdx; // S89 D2: per-vertex feature index for color
                }

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
                perPolyMergedVertCnt[pi].Dispose();
                scratchVx[pi].Dispose(); scratchVy[pi].Dispose();
                scratchPrev[pi].Dispose(); scratchNext[pi].Dispose();
                scratchIsBridge[pi].Dispose(); scratchRemoved[pi].Dispose(); scratchIsEar[pi].Dispose();
            }

            // ── Stage 4: tile→geodetic, then project to world space (S91). ─────────────────────────
            {
                using var sPipelineProject = PmPipelineProject.Auto();

                // 4a: tile-space → geodetic surface points (projection-independent).
                var geo = new NativeArray<GeoCoordinate>(totalMergedVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                new TileToGeoJob
                {
                    Tile = input.Tile, Extent = input.Extent,
                    TileCoords = outMergedVerts, OutGeo = geo,
                }.Run(totalMergedVerts);

                // 4b: project through the chosen projection (struct type dispatch → ProjectPointsJob<TProj>).
                // RTC origin (docs §5) = the caller-supplied SW-corner render origin — the SINGLE source now,
                // shared with the tile transform (S91-C). The caller computes it via TileRenderOrigin.Project
                // with the SAME projection, so origin and vertices share one projection (Mercator: bit-for-bit
                // the old internal recompute; correct for the globe too).
                double3 originWorld = input.OriginRender;
                ProjectionDispatch.Run(input.Projection, originWorld, geo, outWorldPos, outVertexUp, totalMergedVerts);

                geo.Dispose();
            }

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
                VertexUp            = outVertexUp,
                VertexFeatureIdx    = outVertexFeat,
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
        // (The tile's bake origin is TileRenderOrigin.Project — Core, engine-free, shared by every geometry
        //  kind; it is NOT fill-specific, so it does not live on this fill pipeline.)

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
