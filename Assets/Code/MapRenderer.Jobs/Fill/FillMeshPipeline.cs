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
    /// What <see cref="FillMeshGraph"/> and its callers share: <see cref="LayerInput"/>, the input
    /// descriptor; <see cref="HoleRingComparer"/>, the hole-sort order the graph's gather node uses; and the
    /// decode-sizing pair <see cref="PrecountRingsAndVertices"/>/<see cref="EnsureCapacity"/>, which
    /// <c>MvtGeometryMaterializer</c> calls upstream of the mesher.
    /// </summary>
    public static class FillMeshPipeline
    {
        // MVT command IDs (per MVT spec §4.3) — must match MvtDecodeJob's constants.
        private const uint MoveTo = 1;
        private const uint LineTo = 2;

        /// <summary>
        /// Exact pre-count of the rings and vertices <see cref="MvtDecodeJob.Execute"/> emits for a layer's
        /// flattened command stream, computed by walking every command the way the decode job does. The
        /// count is exact, not a heuristic: the job emits one ring and one vertex per MoveTo point, and one
        /// vertex per LineTo point, for ANY input — a malformed multi-point <c>MoveTo</c> included. Sizing
        /// from it makes under-allocation impossible, so no build can produce the out-of-range write.
        ///
        /// <b>Must mirror <see cref="MvtDecodeJob.Execute"/> exactly.</b> The job advances its read cursor by
        /// 2 param uints per MoveTo/LineTo point and reads no params for ClosePath or an unknown command. If
        /// you change one, change the other — they desync silently and re-introduce the overflow.
        ///
        /// A truncated param stream can still make the decode job read PAST the feature's command range.
        /// That is an input-read overflow; the counts here match what the job WRITES either way.
        /// </summary>
        /// <remarks>Takes the same native-flat <c>(commands, featureOffsets, featureLengths)</c> shape
        /// <see cref="MvtDecodeJob"/> reads. A feature with <c>featureLengths[fi] == 0</c> is
        /// skipped.</remarks>
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
        /// Never-fired capacity backstop for the sizing pre-pass. With exact
        /// <see cref="PrecountRingsAndVertices"/> sizing no job can report a count past the buffers it was
        /// sized for. The check is a plain <c>if</c>, not behind <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, so
        /// a future sizing mistake fails fast in a release build too.
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
            /// <summary>The shared tile geometry — <b>BORROWED</b>. <see cref="FillMeshGraph.Schedule"/>
            /// never disposes it, never writes into it, and does not retain it past
            /// <c>Handle.Complete()</c>: it derives its own private buffer over the rings
            /// <see cref="RingVisitOrder"/> names, and owns only that. It stays the sole authority for the
            /// tile address and extent, so there is no second copy to route around.</summary>
            public TileGeometryBuffers Geometry;

            /// <summary>Ring indices into <see cref="Geometry"/>, in the exact order this layer wants them
            /// triangulated — the caller's selection AND its draw order in one array, so fill's
            /// <c>fill-sort-key</c> rank lives here. Caller-owned; <see cref="FillMeshGraph.Schedule"/>
            /// only reads it.
            /// <para><b>Must group each feature's rings contiguously</b>: <see cref="RingAssemblyJob"/> resets
            /// its exterior sign on a feature CHANGE, so a feature's rings split across the order would have
            /// its second run re-read as a fresh exterior with a fresh sign.</para></summary>
            public NativeArray<int> RingVisitOrder;

            /// <summary>Suppresses the outward boundary band for this layer. The default <c>false</c> emits
            /// it, which every real fill layer wants. Set by the callers whose geometry has no silhouette to
            /// antialias: <c>FillExtrusionMeshGraph</c>'s roof, whose buildings keep hard edges, and
            /// <c>BackgroundQuad</c>, whose every edge abuts the neighbour tile's identical quad, where a
            /// band is a double-composited rim.
            /// <para>The style's <c>fill-antialias</c> also lands here: a layer that opts out emits no band
            /// geometry at all. <c>StyledFillTileBuilder.BuildLayerInput</c> resolves it.</para></summary>
            public bool SuppressBoundaryBand;

            /// <summary>The RTC render-space origin the mesh vertices are baked relative to — the tile's SW
            /// corner projected through <see cref="Projection"/>. The single source of the bake origin,
            /// shared with the tile transform (Mercator: <c>(mercX, 0, mercZ)</c>; globe: the corner's
            /// ECEF). Use <c>TileRenderOrigin.Project</c> (Core) to compute it.</summary>
            public double3 OriginRender;

            /// <summary>The projection the geometry is built with (a stateless struct behind
            /// <see cref="MapRenderer.Core.Geo.IProjection"/>). Left <c>null</c> ⇒ Web Mercator (planar).</summary>
            public MapRenderer.Core.Geo.IProjection Projection;

            /// <summary>How much of the tile's buffer to keep before triangulating. <c>default</c> ⇒
            /// disabled ⇒ the clip stage is skipped and the geometry reaches assembly as decoded.</summary>
            public MapRenderer.Core.Tiles.TileBufferClip Clip;
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

        /// <summary>
        /// Orders hole ring indices by leftmost-x, then min-y, then ring index — the deterministic bridge
        /// order. A <b>struct</b> comparer, so <c>NativeArray.Sort&lt;int, HoleRingComparer&gt;</c> takes it
        /// by generic constraint with no boxing and the sort allocates nothing.
        /// </summary>
        /// <remarks><c>internal</c>, not <c>private</c>, so a test can measure that sorting through it
        /// allocates no managed memory.</remarks>
        internal readonly struct HoleRingComparer : IComparer<int>
        {
            private readonly NativeArray<double2> _vertices;
            private readonly NativeArray<int>     _ringOffsets;

            /// <summary>Binds the comparer directly to the two arrays the ordering reads. A job field cannot
            /// hold a <see cref="TileGeometryBuffers"/> alongside its own <c>AsArray()</c> views without the
            /// safety system seeing two aliases of the same allocation.</summary>
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
