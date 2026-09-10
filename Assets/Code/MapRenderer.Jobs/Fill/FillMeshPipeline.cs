using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// job-scheduling-design.md §8 stage 4 Group B: the synchronous decode → ring assembly → earcut →
    /// projection job CHAIN this class used to coordinate (<c>Schedule</c>) is retired — <see cref="FillMeshGraph.Schedule"/>
    /// is the only mesher left. What survives here is what the graph and its callers still share:
    /// <see cref="LayerInput"/> (the input descriptor both used), <see cref="HoleRingComparer"/> (the hole-sort
    /// order <see cref="FillMeshGraph"/>'s gather node uses, unchanged), and the decode-sizing pair
    /// <see cref="PrecountRingsAndVertices"/>/<see cref="EnsureCapacity"/> (still called from
    /// <c>MvtGeometryMaterializer</c>, upstream of either mesher). ~20 call sites reference
    /// <c>FillMeshPipeline.LayerInput</c>; renaming this class to something that no longer implies "the
    /// pipeline" is a follow-up sweep with no behaviour change (DIV-B1), not done here.
    /// </summary>
    public static class FillMeshPipeline
    {
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
        /// <remarks>2a: takes the same native-flat <c>(commands, featureOffsets, featureLengths)</c> shape
        /// <see cref="MvtDecodeJob"/> reads — the caller (<see cref="MvtGeometryMaterializer.Materialize"/>)
        /// no longer holds a per-feature managed <c>uint[]</c> to precount from. A feature with
        /// <c>featureLengths[fi] == 0</c> is skipped, the flat-buffer equivalent of the old null element.</remarks>
        public static void PrecountRingsAndVertices(
            NativeArray<uint> commands, NativeArray<int> featureOffsets, NativeArray<int> featureLengths,
            out int rings, out int vertices)
        {
            rings    = 0;
            vertices = 0;
            if (!featureOffsets.IsCreated) return;

            for (int fi = 0; fi < featureOffsets.Length; fi++)
            {
                int start = featureOffsets[fi];
                int len   = featureLengths[fi];
                if (len == 0) continue;

                int i = 0;
                while (i < len)
                {
                    uint commandInteger = commands[start + i++];
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
        /// <param name="what">A short symbol naming the quantity, for the exception message.</param>
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
            /// <summary>Waist 1's shared tile geometry — <b>BORROWED</b>. <see cref="FillMeshGraph.Schedule"/>
            /// never disposes it, never writes into it, and does not retain it past
            /// <c>Handle.Complete()</c>: it derives its own private buffer holding exactly the rings
            /// <see cref="RingVisitOrder"/> names, and owns only that. It is still the sole authority for the
            /// tile address and extent, which <see cref="FillMeshGraph.Schedule"/> reads off it, so there is
            /// no second copy for a stage to route around.</summary>
            public TileGeometryBuffers Geometry;

            /// <summary>Ring indices into <see cref="Geometry"/>, in the exact order this layer wants them
            /// triangulated — the caller's selection AND its draw order in one array (fill's
            /// <c>fill-sort-key</c> rank lives here now). Caller-owned; <see cref="FillMeshGraph.Schedule"/>
            /// only reads it.
            /// <para><b>Must group each feature's rings contiguously</b>: <see cref="RingAssemblyJob"/> resets
            /// its exterior sign on a feature CHANGE, so a feature's rings split across the order would have
            /// its second run re-read as a fresh exterior with a fresh sign.</para></summary>
            public NativeArray<int> RingVisitOrder;

            /// <summary>Suppresses the outward boundary band for this layer — <c>false</c> (the default a
            /// caller gets for free) emits it, which is what every real fill layer wants. Set by the two
            /// callers whose geometry has no silhouette to antialias: <c>FillExtrusionMeshGraph</c>'s roof,
            /// whose mesh layout carries no band attribute and whose buildings keep hard edges, and
            /// <c>BackgroundQuad</c>, a full-tile quad whose every edge is a tile seam abutting the
            /// neighbour's identical quad — a band there is a double-composited rim, never antialiasing.
            /// <para>It is also where the style's <c>fill-antialias</c> lands: a layer that opts out emits no
            /// band geometry at all, which is what keeps the property per-layer implementable —
            /// <c>StyledFillTileBuilder.BuildLayerInput</c> is the site that resolves it.</para></summary>
            public bool SuppressBoundaryBand;

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
            private readonly NativeArray<double2> _vertices;
            private readonly NativeArray<int>     _ringOffsets;

            /// <summary>Binds the comparer directly to the two arrays the ordering reads — the graph-node
            /// shape: a job field cannot hold a <see cref="TileGeometryBuffers"/>
            /// alongside its own <c>AsArray()</c> views without the safety system seeing two aliases of the
            /// same allocation.</summary>
            /// <param name="vertices">Ring vertices, tile-space.</param>
            /// <param name="ringOffsets">Per-ring start offsets into <paramref name="vertices"/>.</param>
            public HoleRingComparer(NativeArray<double2> vertices, NativeArray<int> ringOffsets)
            {
                _vertices    = vertices;
                _ringOffsets = ringOffsets;
            }

            /// <summary>Binds the comparer to the tile geometry whose rings it orders.</summary>
            /// <param name="geometry">The tile geometry whose ring vertices and offsets the ordering reads.</param>
            public HoleRingComparer(TileGeometryBuffers geometry) : this(geometry.Vertices, geometry.RingOffsets) { }

            /// <summary>Total order: leftmost-x, then min-y, then the ring index itself as the tiebreak.</summary>
            /// <param name="a">First hole ring index.</param>
            /// <param name="b">Second hole ring index.</param>
            /// <returns>Negative, zero, or positive per <see cref="IComparer{T}"/>.</returns>
            public int Compare(int a, int b)
            {
                NativeArray<int>     offsets = _ringOffsets;
                NativeArray<double2> verts   = _vertices;
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
