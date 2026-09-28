using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Unity.Jobs.Geometry;

namespace MapRenderer.Unity.Jobs.Fill
{
    /// <summary>
    /// What <see cref="FillMeshGraph"/> and its callers share: <see cref="LayerInput"/>, the input
    /// descriptor, and <see cref="HoleRingComparer"/>, the hole-sort order the graph's gather node uses.
    /// </summary>
    internal static class FillMeshPipeline
    {
        /// <summary>
        /// Input descriptor for one layer's polygon features, already decoded from MVT bytes. The per-feature
        /// paint columns are not part of this struct: the layer build owns them, and the write step reads
        /// them. The fill-extrusion wall chain also reads colour and bake at measure time, as parameters of
        /// <see cref="Rendering.Meshing.FillExtrusionMeshGraph.Schedule"/>.
        /// </summary>
        internal struct LayerInput
        {
            /// <summary>The shared tile geometry — <b>BORROWED</b>. <see cref="FillMeshGraph.Schedule"/>
            /// never disposes it, never writes into it, and does not retain it past
            /// <c>Handle.Complete()</c>: it derives its own private buffer over the rings
            /// <see cref="RingVisitOrder"/> names, and owns only that. It stays the sole authority for the
            /// tile address and extent, so there is no second copy to route around.</summary>
            public TileGeometryBuffers Geometry;

            /// <summary>Ring indices into <see cref="Geometry"/>, in the exact order this layer wants them
            /// triangulated, so it carries the <c>fill-sort-key</c> rank. Caller-owned and read-only here.
            /// It must group each feature's rings contiguously: <see cref="RingAssemblyJob"/> resets its
            /// exterior sign on a feature change, so a split feature's second run is a new exterior.</summary>
            public NativeArray<int> RingVisitOrder;

            /// <summary>Suppresses the outward boundary band for this layer. The default <c>false</c> emits
            /// it. Set for geometry with no silhouette to antialias: <c>FillExtrusionMeshGraph</c>'s roof (hard
            /// building edges) and <c>BackgroundQuad</c> (each edge abuts the neighbour's identical quad, so a
            /// band is a double rim). <c>StyledFillTileBuilder.BuildLayerInput</c> also sets it from
            /// <c>fill-antialias</c>.</summary>
            public bool SuppressBoundaryBand;

            /// <summary>The RTC render-space origin the mesh vertices are baked relative to — the tile's SW
            /// corner projected through <see cref="Projection"/>. The single source of the bake origin,
            /// shared with the tile transform (Mercator: <c>(mercX, 0, mercZ)</c>; globe: the corner's
            /// ECEF). Use <c>TileRenderOrigin.Project</c> (Core) to compute it.</summary>
            public double3 OriginRender;

            /// <summary>The projection the geometry is built with (a stateless struct behind
            /// <see cref="MapRenderer.Core.Geo.IProjection"/>). Never null by the time this reaches a graph:
            /// every graph that consumes it throws on null, and the caller resolves the null-means-Mercator
            /// default.</summary>
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
