using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

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
        /// <summary>Profiler marker name constants (SSOT) for the pipeline stages — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c>. Public rather than
        /// internal because this assembly grants no <c>InternalsVisibleTo</c> (same shape as
        /// <c>StyledFillTileBuilder.ProfilerMarkerNames</c>). Hierarchical names so the Profiler flat search
        /// groups them.</summary>
        public static class ProfilerMarkerNames
        {
            // MapRenderer.Pipeline.Decode moved OUT of this class with IR B7 (to TileGeometryStore) and on
            // to MvtDecoder with IR C1 P3 — the marker follows the decode it brackets, and one left here
            // would bracket no decode at all.
            public const string Clip         = "MapRenderer.Pipeline.Clip";
            public const string RingAssembly = "MapRenderer.Pipeline.RingAssembly";
            public const string Earcut       = "MapRenderer.Pipeline.Earcut";
            public const string Project      = "MapRenderer.Pipeline.Project";
        }

        // Pipeline-stage profiler markers (MapRenderer.Pipeline.*).
        // These sit on the schedule-then-Complete main-thread path — exactly the stall the perf epic measures.
        // Separate path from the live MapView loop; wired for the Profiler window, not for the recorder test.
        private static readonly ProfilerMarker PmPipelineClip =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Clip);
        private static readonly ProfilerMarker PmPipelineRingAssembly =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.RingAssembly);
        private static readonly ProfilerMarker PmPipelineEarcut =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Earcut);
        private static readonly ProfilerMarker PmPipelineProject =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Project);

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
            /// <summary>Waist 1's shared tile geometry — <b>BORROWED</b>. <see cref="Schedule"/> never
            /// disposes it, never writes into it, and does not retain it past the call: it derives its own
            /// private buffer holding exactly the rings <see cref="RingVisitOrder"/> names, and owns only
            /// that. It is still the sole authority for the tile address and extent, which
            /// <see cref="Schedule"/> reads off it, so there is no second copy for a stage to route
            /// around.</summary>
            public TileGeometryBuffers Geometry;

            /// <summary>Ring indices into <see cref="Geometry"/>, in the exact order this layer wants them
            /// triangulated — the caller's selection AND its draw order in one array (fill's
            /// <c>fill-sort-key</c> rank lives here now). Caller-owned; <see cref="Schedule"/> only reads it.
            /// <para><b>Must group each feature's rings contiguously</b>: <see cref="RingAssemblyJob"/> resets
            /// its exterior sign on a feature CHANGE, so a feature's rings split across the order would have
            /// its second run re-read as a fresh exterior with a fresh sign.</para></summary>
            public NativeArray<int> RingVisitOrder;

            /// <summary>S91-C: the RTC render-space origin (docs §5) the mesh vertices are baked relative to —
            /// the tile's SW corner projected through <see cref="Projection"/>. The single source of the
            /// bake origin, shared with the tile transform (Mercator: <c>(mercX, 0, mercZ)</c>; globe: the
            /// corner's ECEF). Use <c>TileRenderOrigin.Project</c> (Core) to compute it.</summary>
            public double3 OriginRender;

            /// <summary>S91: the projection the geometry is built with (a stateless struct behind
            /// <see cref="MapRenderer.Core.Geo.IProjection"/>). Left <c>null</c> ⇒ Web Mercator (planar).</summary>
            public MapRenderer.Core.Geo.IProjection Projection;

            /// <summary>How much of the tile's buffer to keep before triangulating (Stage 1b). <c>default</c>
            /// ⇒ disabled ⇒ the clip stage is skipped entirely and the geometry reaches assembly exactly as
            /// decoded — the behaviour-preserving state every unset caller gets.</summary>
            public MapRenderer.Core.Tiles.TileBufferClip Clip;
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
            // ── Stage 1: derive this layer's private ring buffer from the shared one. ──────────────
            // input.Geometry is BORROWED (see LayerInput.Geometry): the decoded LAYER owns it (IR C1 P3),
            // and several fill layers — plus the symbol pass of the same kick — run against the same one.
            // Nothing below may dispose or write to it.
            if (!input.Geometry.IsCreated || !input.RingVisitOrder.IsCreated || input.RingVisitOrder.Length == 0)
                return default;   // nothing to draw. (Pre-B7 this allocated the Stage-2 arrays first, found
                                  // polyCount == 0 and returned the same `default` — same output, fewer allocations.)

            // Read off the shared buffer, once: Tile/Extent are the producer's declaration, not a
            // caller-supplied second copy.
            TileId tile   = input.Geometry.Tile;
            double extent = input.Geometry.Extent;

            TileGeometryBuffers geometry = DeriveVisitedRings(input);

            // Stage 2's sizing bound. Each ring is classified exactly once by RingAssemblyJob (outer / hole /
            // skipped), so polygons and holes each number at most the derived ring count (they cannot be
            // exact pre-counted without running the area classification). This is the derived buffer's
            // capacity — list-backed, hence equal to its count — where pre-B7 it was the decode capacity;
            // both bound polyCount + totalHoles by the same argument, so the never-fired EnsureCapacity
            // backstop keeps its meaning and stays non-tautological (polyCount is still a job-reported number
            // compared against a caller-computed capacity).
            int maxPolygons = math.max(1, geometry.RingCapacity);
            int maxHoles    = maxPolygons;

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
            }

            int polyCount    = polyCountArr[0];
            int totalHoles   = holeCountArr[0];
            polyCountArr.Dispose();
            holeCountArr.Dispose();

            // Never-fired backstop: each ring is classified exactly once (outer / hole / skipped), so
            // polyCount + totalHoles ≤ ringCount ≤ maxRings = maxPolygons = maxHoles. Cannot trip with exact
            // sizing. (S06 gated item a.)
            //
            // The buffer and four Stage-2 arrays are already live when these run, so an unguarded throw would
            // strand all five. The path is unreachable by construction, hence no behavioural test can force
            // it — the catch exists so the "owner on every exit path" contract holds by READING the code
            // rather than by arguing reachability. (Mirrors MvtGeometryMaterializer's twin.)
            try
            {
                EnsureCapacity(polyCount, maxPolygons, "polygon");
                EnsureCapacity(totalHoles, maxHoles, "hole");
            }
            catch
            {
                geometry.Dispose();
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose();
                throw;
            }

            if (polyCount == 0)
            {
                geometry.Dispose();
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
            // Plain int scratch → NativeArray (off the GC heap, allocation ladder rung 2). Unlike the 13
            // NativeArray<T>[] handle-arrays above — managed arrays OF native handles, which can't nest — these
            // hold plain ints, so the native form is a straight swap. Disposed after the aggregation below.
            var perPolyMergedVC   = new NativeArray<int>(polyCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var perPolyFeatureIdx = new NativeArray<int>(polyCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); // S89 D2: feature index of each polygon (for per-vertex color)

            // Per-polygon allocation kill: the hole-ring sort used a `new int[holeCount]` MANAGED array per
            // polygon (a GC alloc every iteration, including `new int[0]` for hole-less polygons). Sort in a
            // single REUSED NativeArray instead — off the GC heap entirely (allocation ladder rung 2) — via a
            // struct comparer passed by generic constraint to NativeSortExtension.Sort (no boxing). (The inline
            // lambda it replaced was already hoisted+cached to one delegate per call by the compiler, so it
            // was never the per-polygon cost the array was.)
            var holeComparer = new HoleRingComparer(geometry);
            int maxHoleCount = 0;
            for (int pi = 0; pi < polyCount; pi++) maxHoleCount = math.max(maxHoleCount, polyHoleCount[pi]);
            var holeRIs = new NativeArray<int>(math.max(1, maxHoleCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            for (int pi = 0; pi < polyCount; pi++)
            {
                int outerRi    = polyOuterIdx[pi];
                perPolyFeatureIdx[pi] = geometry.RingFeatureIdx[outerRi]; // captured before the ring stage is disposed post-earcut
                int outerStart = geometry.RingOffsets[outerRi];
                int outerLen   = geometry.RingOffsets[outerRi + 1] - outerStart;
                int holeCount  = polyHoleCount[pi];
                int hStart     = polyHoleStart[pi];

                // Collect and sort hole ring indices for deterministic bridge order into the reused buffer.
                // Sort: (leftmost-x, min-y, ring-index) — must match managed validHoles.Sort.
                for (int hi = 0; hi < holeCount; hi++)
                    holeRIs[hi] = holeRingIdxs[hStart + hi];

                // Sort only [0, holeCount) via a view over the reused native buffer — the tail holds stale
                // indices from a prior polygon and is never read (every consumer below indexes hi < holeCount).
                // Skip the trivial 0/1 cases.
                if (holeCount > 1)
                    holeRIs.GetSubArray(0, holeCount).Sort(holeComparer);

                // Build flat poly verts: outer + holes in sorted order.
                int polyVC = outerLen;
                for (int hi = 0; hi < holeCount; hi++)
                    polyVC += geometry.RingOffsets[holeRIs[hi] + 1] - geometry.RingOffsets[holeRIs[hi]];

                var polyVerts        = new NativeArray<double2>(polyVC, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var sortedHoleCounts = new NativeArray<int>(holeCount > 0 ? holeCount : 1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

                for (int i = 0; i < outerLen; i++)
                    polyVerts[i] = geometry.Vertices[outerStart + i];

                int vPos = outerLen;
                for (int hi = 0; hi < holeCount; hi++)
                {
                    int hri    = holeRIs[hi];
                    int hBegin = geometry.RingOffsets[hri];
                    int hLen   = geometry.RingOffsets[hri + 1] - hBegin;
                    sortedHoleCounts[hi] = hLen;
                    for (int i = 0; i < hLen; i++)
                        polyVerts[vPos++] = geometry.Vertices[hBegin + i];
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

            holeRIs.Dispose(); // reused only within the collect/sort loop above; the earcut stage never reads it.

            // Run all earcut jobs sequentially (each polygon is independent — Run, not Schedule, so the
            // pipeline is callable off the main thread; per-polygon parallelism is a later throughput knob).
            {
                using var sPipelineEarcut = PmPipelineEarcut.Auto();
                for (int pi = 0; pi < polyCount; pi++)
                {
                    int outerLen  = geometry.RingOffsets[polyOuterIdx[pi] + 1] - geometry.RingOffsets[polyOuterIdx[pi]];
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

            // No longer need ring/assembly data. Capture the ring count first — the output buffers below
            // report it, and reading anything off a disposed buffer is a trap for the next reader.
            int finalRingCount = geometry.RingCount;
            geometry.Dispose();
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

            perPolyMergedVC.Dispose();
            perPolyFeatureIdx.Dispose(); // both are fully read by the aggregation above; the project stage never touches them.

            // ── Stage 4: tile→geodetic, then project to world space (S91). ─────────────────────────
            {
                using var sPipelineProject = PmPipelineProject.Auto();

                // 4a: tile-space → geodetic surface points (projection-independent).
                var geo = new NativeArray<GeoCoordinate>(totalMergedVerts, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                new TileToGeoJob
                {
                    Tile = tile, Extent = extent,
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
            ringCountFinal[0]  = finalRingCount;
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

        /// <summary>
        /// Builds the private, <b>owned</b> ring buffer this call triangulates: exactly the rings
        /// <c>input.RingVisitOrder</c> names, in that order, copied out of the borrowed shared buffer.
        ///
        /// <para>Two branches, one output shape. With the tile-buffer clip enabled the copy is done by
        /// <see cref="RingClipJob"/> (which cuts each ring to the window as it goes); with it disabled by
        /// <see cref="RingSelectJob"/>, which is that job's bbox-inside fast path with the clipping removed.
        /// The clip is NOT a no-op on a boundary-touching ring — its emit dedups repeated vertices and closes
        /// the ring — so running it with a full-extent window instead of taking the select branch would move
        /// geometry.</para>
        ///
        /// <para><b>Why fill derives at all, when line and symbol just read the shared buffer.</b> Fill is the
        /// only consumer that reorders (<c>fill-sort-key</c>), and the only one that honours the clip — every
        /// other kind accepts the clip and ignores it by decision. A single pre-clipped shared buffer would
        /// silently start clipping line's input.</para>
        /// </summary>
        private static TileGeometryBuffers DeriveVisitedRings(LayerInput input)
        {
            TileGeometryBuffers source = input.Geometry;
            NativeArray<int>    visit  = input.RingVisitOrder;

            int maxRingLen = 0;
            int totalVerts = 0;
            for (int k = 0; k < visit.Length; k++)
            {
                int ri  = visit[k];
                int len = source.RingOffsets[ri + 1] - source.RingOffsets[ri];
                maxRingLen  = math.max(maxRingLen, len);
                totalVerts += len;
            }

            // Length-authoritative outputs: whichever job below runs, it reports the ring/vertex counts by
            // filling these, so the derived buffer is sized exactly. Capacities are hints only.
            var outVerts   = new NativeList<double2>(math.max(1, totalVerts),  Allocator.Persistent);
            var outOffsets = new NativeList<int>(visit.Length + 1,             Allocator.Persistent);
            var outFeatIdx = new NativeList<int>(math.max(1, visit.Length),    Allocator.Persistent);

            if (input.Clip.TryWindow(source.Extent, out double2 clipMin, out double2 clipMax))
            {
                // MVT tiles carry geometry past [0, extent) so neighbours join seamlessly; drawing all of it
                // makes adjacent tiles double-paint the overlap strip (a brighter band under the fill's
                // translucent ZWrite-off blend). Cutting HERE — before assembly — means RingAssemblyJob
                // classifies the geometry that will actually be drawn, and its rLen/degenerate-area filters
                // clean up the clipped-to-nothing rings for free.
                using var sPipelineClip = PmPipelineClip.Auto();

                // Per-RING ping-pong scratch at Sutherland–Hodgman's provable bound — not per tile, and sized
                // over the VISITED rings, which is the exact set this pass will feed it.
                int scratchCap = math.max(1, maxRingLen * RingClipJob.ScratchLengthMultiplier);
                var scratchA = new NativeArray<double2>(scratchCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var scratchB = new NativeArray<double2>(scratchCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                new RingClipJob
                {
                    Vertices          = source.Vertices,
                    RingOffsets       = source.RingOffsets,
                    RingFeatureIdx    = source.RingFeatureIdx,
                    RingVisitOrder    = visit,
                    ClipMin           = clipMin,
                    ClipMax           = clipMax,
                    ScratchA          = scratchA,
                    ScratchB          = scratchB,
                    OutVertices       = outVerts,
                    OutRingOffsets    = outOffsets,
                    OutRingFeatureIdx = outFeatIdx,
                }.Run(); // Run, like every other stage — the pipeline must stay callable off the main thread

                scratchA.Dispose();
                scratchB.Dispose();
            }
            else
            {
                new RingSelectJob
                {
                    Vertices          = source.Vertices,
                    RingOffsets       = source.RingOffsets,
                    RingFeatureIdx    = source.RingFeatureIdx,
                    RingVisitOrder    = visit,
                    OutVertices       = outVerts,
                    OutRingOffsets    = outOffsets,
                    OutRingFeatureIdx = outFeatIdx,
                }.Run();
            }

            // The kind column is COPIED, not taken: the source is borrowed and must be left owning everything
            // it owns. Neither job renumbers a feature index, so the copy is valid as-is.
            return TileGeometryBuffers.AdoptDerivedLists(
                source.Tile, source.Extent, source.FeatureGeometryType, outVerts, outOffsets, outFeatIdx);
        }

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

        /// <summary>
        /// Orders hole ring indices by (leftmost-x, then min-y, then ring index) — the deterministic bridge
        /// order that must match managed <c>validHoles.Sort</c>. A <b>struct</b> comparer so
        /// <c>NativeArray.Sort&lt;int, HoleRingComparer&gt;</c> takes it by generic constraint with no boxing —
        /// the sort of the reused native hole buffer allocates nothing.
        /// </summary>
        /// <remarks><c>internal</c> (not <c>private</c>) so <c>FillHoleRingComparerAllocationTests</c> can
        /// measure that sorting a <see cref="Unity.Collections.NativeArray{T}"/> through it allocates no
        /// managed memory — the property this stage creates (versus the retired managed <c>int[]</c> + managed
        /// <c>Array.Sort</c>). Jobs grants <c>InternalsVisibleTo("MapRenderer.Tests.EditMode")</c>.</remarks>
        internal readonly struct HoleRingComparer : IComparer<int>
        {
            private readonly TileGeometryBuffers _geometry;

            /// <summary>Binds the comparer to the tile geometry whose rings it orders.</summary>
            /// <param name="geometry">The tile geometry whose ring vertices and offsets the ordering reads.</param>
            public HoleRingComparer(TileGeometryBuffers geometry) => _geometry = geometry;

            /// <summary>Total order: leftmost-x, then min-y, then the ring index itself as the tiebreak.</summary>
            /// <param name="a">First hole ring index.</param>
            /// <param name="b">Second hole ring index.</param>
            /// <returns>Negative, zero, or positive per <see cref="IComparer{T}"/>.</returns>
            public int Compare(int a, int b)
            {
                NativeArray<int>     offsets = _geometry.RingOffsets;
                NativeArray<double2> verts   = _geometry.Vertices;
                double ax = LeftmostX(verts, offsets[a], offsets[a + 1] - offsets[a]);
                double bx = LeftmostX(verts, offsets[b], offsets[b + 1] - offsets[b]);
                int cmp = ax.CompareTo(bx);
                if (cmp != 0) return cmp;
                double ay = MinY(verts, offsets[a], offsets[a + 1] - offsets[a]);
                double by = MinY(verts, offsets[b], offsets[b + 1] - offsets[b]);
                cmp = ay.CompareTo(by);
                if (cmp != 0) return cmp;
                return a.CompareTo(b);
            }
        }
    }
}
