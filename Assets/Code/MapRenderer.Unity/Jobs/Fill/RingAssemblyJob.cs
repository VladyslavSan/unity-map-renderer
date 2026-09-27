using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Jobs.Geometry;

namespace MapRenderer.Unity.Jobs.Fill
{
    /// <summary>
    /// Burst job: classifies decoded rings into polygon descriptors (outer + holes), through
    /// <see cref="RingExteriorClassifier"/>. Rings of non-Polygon features are skipped, because a LineString
    /// ring looks like a polygon ring by area alone. Descriptor arrays are sized for one polygon per ring.
    /// The Burst path calls no Core code.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RingAssemblyJob : IJob
    {
        // ── Input ──────────────────────────────────────────────────────────────────────────────
        [ReadOnly] public NativeArray<double2> Vertices;
        [ReadOnly] public NativeArray<int>     RingOffsets;   // length = ringCount + 1 (sentinel)
        [ReadOnly] public NativeArray<int>     RingFeatureIdx;// which feature each ring belongs to
        [ReadOnly] public int                  RingCount;

        /// <summary>Selects which of two ways to read the ring count — a plain <c>bool</c>, not a container,
        /// because a default <see cref="NativeArray{T}"/> field fails schedule-time validation even unread.
        /// <c>false</c> uses <see cref="RingCount"/>, for a capacity-sized <see cref="RingOffsets"/>.
        /// <c>true</c> (the scheduled path) uses <c>RingOffsets.Length - 1</c> of a deferred list view, which
        /// at execute time is the ring count left after <c>RingClipJob</c>.</summary>
        public bool RingCountFromOffsetsLength;

        /// <summary>Per-FEATURE geometry kind (see <c>TileGeometryBuffers.FeatureGeometryType</c>) — ring
        /// <c>r</c>'s kind is <c>FeatureGeometryType[RingFeatureIdx[r]]</c>. Read by the kind gate in
        /// <see cref="Execute"/>. An unfilled element reads <c>Unknown</c>, which the gate rejects, so a
        /// producer that forgot to fill the column assembles NOTHING (loud) rather than something wrong.</summary>
        [ReadOnly] public NativeArray<TileGeometryType> FeatureGeometryType;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        // Not [WriteOnly] where Execute reads the field back: the safety system throws on that read.
        public            NativeArray<int>    OutPolyOuterRingIdx;    // ring index for outer
        [WriteOnly] public NativeArray<int>   OutPolyHoleListStart;   // start in OutHoleRingIdxs
        public            NativeArray<int>    OutPolyHoleCount;       // hole count for polygon p
        [WriteOnly] public NativeArray<int>   OutHoleRingIdxs;        // flat list of hole ring idxs
        public            NativeArray<int>    OutPolygonCount;        // [0] = total polygons
        public            NativeArray<int>    OutHoleCount;           // [0] = total holes

        public void Execute()
        {
            int ringCount = RingCountFromOffsetsLength ? RingOffsets.Length - 1 : RingCount;

            int polyCount     = 0;
            int holeListCount = 0;

            int prevFeature    = -1;
            int currentPolyIdx = -1;
            var classifier     = default(RingExteriorClassifier.State);

            for (int ri = 0; ri < ringCount; ri++)
            {
                int rStart = RingOffsets[ri];
                int rEnd   = RingOffsets[ri + 1];
                int rLen   = rEnd - rStart;

                if (rLen < 3) continue; // degenerate

                int featureIdx = RingFeatureIdx[ri];

                // Kind gate, before the sign reset and the area work, so a non-polygon feature can never
                // establish or flip the exterior sign.
                if (FeatureGeometryType[featureIdx] != TileGeometryType.Polygon) continue;

                // New feature resets exterior sign.
                if (featureIdx != prevFeature)
                {
                    classifier.Reset();
                    prevFeature = featureIdx;
                }

                switch (classifier.Classify(Vertices, rStart, rLen))
                {
                    case RingExteriorClassifier.Role.Outer:
                        OutPolyOuterRingIdx[polyCount]  = ri;
                        OutPolyHoleListStart[polyCount] = holeListCount;
                        OutPolyHoleCount[polyCount]     = 0;
                        currentPolyIdx = polyCount;
                        polyCount++;
                        break;

                    case RingExteriorClassifier.Role.Hole:
                        if (currentPolyIdx >= 0)
                        {
                            OutHoleRingIdxs[holeListCount] = ri;
                            holeListCount++;
                            OutPolyHoleCount[currentPolyIdx]++;
                        }
                        break;

                    // Role.Dropped: a degenerate or disjoint-artefact ring — nothing to record.
                }
            }

            OutPolygonCount[0] = polyCount;
            OutHoleCount[0]    = holeListCount;
        }
    }
}
