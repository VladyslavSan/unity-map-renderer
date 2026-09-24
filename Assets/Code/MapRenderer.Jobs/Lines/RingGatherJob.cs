using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ring-gather node: filters the borrowed tile-geometry rings by selection, LineString
    /// kind and length <c>&gt;= 2</c>, and appends survivors' tile-space vertices in source order. Line has no
    /// visit-order caller, so the filter lives here. Non-local invariant: it resizes
    /// <see cref="OutSrcGeo"/>/<see cref="OutSrcWorld"/>/<see cref="OutSrcUp"/> to <see cref="OutSrcTile"/>'s
    /// length, so the deferred nodes after it find their write targets sized.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RingGatherJob : IJob
    {
        // ── Input (borrowed — never written, never disposed here) ──────────────────────────────
        /// <summary>Flat tile-space ring vertices of the borrowed source layer.</summary>
        [ReadOnly] public NativeArray<double2> Vertices;

        /// <summary>Per-ring start offset into <see cref="Vertices"/>; length <see cref="RingCount"/> + 1
        /// (trailing sentinel).</summary>
        [ReadOnly] public NativeArray<int> RingOffsets;

        /// <summary>Per-ring index into the source layer's feature list.</summary>
        [ReadOnly] public NativeArray<int> RingFeatureIdx;

        /// <summary>Per-feature geometry kind, indexed by <see cref="RingFeatureIdx"/>.</summary>
        [ReadOnly] public NativeArray<TileGeometryType> FeatureGeometryType;

        /// <summary>Number of rings to walk — <b>NOT</b> derivable as <c>RingOffsets.Length - 1</c> here.
        /// <see cref="Vertices"/>/<see cref="RingOffsets"/>/<see cref="RingFeatureIdx"/> are the BORROWED
        /// source buffer, which in its array-backed mode is CAPACITY-sized; see
        /// <see cref="TileGeometryBuffers.RingCount"/> for why deriving from length there disarms the
        /// sizing-vs-decode desync check. Read this field, never re-derive it.</summary>
        [ReadOnly] public int RingCount;

        /// <summary>This layer's per-feature membership column (<see cref="LayerInput.FeatureSelected"/>).</summary>
        [ReadOnly] public NativeArray<bool> FeatureSelected;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Flat tile-space vertices of every surviving ring, concatenated.</summary>
        public NativeList<double2> OutSrcTile;

        /// <summary>Per-accepted-ring start offset into <see cref="OutSrcTile"/>; length = accepted ring
        /// count + 1 (trailing sentinel) — the same start+sentinel layout the decode stage produces.</summary>
        public NativeList<int> OutRingSrcOffsets;

        /// <summary>Per-accepted-ring feature ordinal (length = accepted ring count).</summary>
        public NativeList<int> OutRingFeature;

        /// <summary>Pre-sized (never written here) so <c>TileToGeoJob</c>'s deferred write has a correctly
        /// sized target — see the type doc.</summary>
        public NativeList<GeoCoordinate> OutSrcGeo;

        /// <summary>Pre-sized (never written here) so <c>ProjectionDispatch.Schedule</c>'s deferred WORLD
        /// write has a correctly sized target. Nothing downstream reads this column — the subdivision metric
        /// is <see cref="OutSrcUp"/> only. Kept because <c>ProjectionDispatch.Schedule</c> always produces
        /// both.</summary>
        public NativeList<double3> OutSrcWorld;

        /// <summary>Pre-sized (never written here) so <c>ProjectionDispatch.Schedule</c>'s deferred UP write
        /// has a correctly sized target — the subdivision metric <see cref="SubdivideJob"/> reads.</summary>
        public NativeList<double3> OutSrcUp;

        public void Execute()
        {
            OutSrcTile.Clear();
            OutRingSrcOffsets.Clear();
            OutRingFeature.Clear();
            OutRingSrcOffsets.Add(0);

            for (int r = 0; r < RingCount; r++)
            {
                int featIdx = RingFeatureIdx[r];

                // Two independent gates — a selected Polygon must pass the first and fail the second.
                if (!FeatureSelected[featIdx]) continue;
                if (FeatureGeometryType[featIdx] != TileGeometryType.LineString) continue;

                int rStart = RingOffsets[r];
                int n      = RingOffsets[r + 1] - rStart;

                // Line's OWN length filter — `< 2`, never fill's `< 3`.
                if (n < 2) continue;

                for (int k = 0; k < n; k++)
                    OutSrcTile.Add(Vertices[rStart + k]);

                OutRingSrcOffsets.Add(OutSrcTile.Length);
                OutRingFeature.Add(featIdx);
            }

            int total = OutSrcTile.Length;
            OutSrcGeo.ResizeUninitialized(total);
            OutSrcWorld.ResizeUninitialized(total);
            OutSrcUp.ResizeUninitialized(total);
        }
    }
}
