using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's curvature-subdivision node (job-scheduling-design.md §8 stage 5): per ring, the body
    /// of <c>StyledLineTileBuilder.SubdivideCenterline</c> (<c>:473-496</c>) over the flat columns
    /// <see cref="RingGatherJob"/> produced — split count from
    /// <see cref="LineCurvatureSubdivision.SegmentSteps"/> over the per-point surface up
    /// (<see cref="SrcUp"/>), then linear interpolation in tile space. A flat projection's <c>∞</c> tolerance
    /// yields 1 step/segment, so the output is the original ring — the Mercator path is the degenerate value
    /// of the SAME code, no capability flag.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct SubdivideJob : IJob
    {
        // ── Input (borrowed) ───────────────────────────────────────────────────────────────────
        [ReadOnly] public NativeList<double2> SrcTile;
        [ReadOnly] public NativeList<int>     RingSrcOffsets; // sentinel layout, length = ringCount + 1
        [ReadOnly] public NativeList<double3> SrcUp;

        /// <summary>The projection's subdivision tolerance — <c>∞</c> ⇒ flat, never subdivides. A field, read
        /// on the main thread from <c>Projection.MaxRefineAngleRad</c> (a managed property, unreadable inside
        /// a job).</summary>
        public double MaxRefineAngleRad;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Flat subdivided tile-space vertices of every ring, concatenated.</summary>
        public NativeList<double2> OutSubTile;

        /// <summary>Per-ring start offset into <see cref="OutSubTile"/>; length = ring count + 1 (sentinel).
        /// Ring COUNT is unchanged from <see cref="RingSrcOffsets"/> — only each ring's point count
        /// grows.</summary>
        public NativeList<int> OutRingSubOffsets;

        /// <summary>Pre-sized (never written here) so <c>TileToGeoJob</c>'s deferred write has a correctly
        /// sized target.</summary>
        public NativeList<GeoCoordinate> OutSubGeo;

        /// <summary>Pre-sized (never written here) so <c>ProjectionDispatch.Schedule</c>'s deferred WORLD
        /// write has a correctly sized target — the ribbon's actual position column.</summary>
        public NativeList<double3> OutSubWorld;

        /// <summary>Pre-sized (never written here) so <c>ProjectionDispatch.Schedule</c>'s deferred UP write
        /// has a correctly sized target — the ribbon's actual up column.</summary>
        public NativeList<double3> OutSubUp;

        public void Execute()
        {
            OutSubTile.Clear();
            OutRingSubOffsets.Clear();
            OutRingSubOffsets.Add(0);

            int ringCount = RingSrcOffsets.Length - 1;
            for (int r = 0; r < ringCount; r++)
            {
                int rStart = RingSrcOffsets[r];
                int n      = RingSrcOffsets[r + 1] - rStart;

                OutSubTile.Add(SrcTile[rStart]); // the first point, unconditionally
                for (int k = 0; k < n - 1; k++)
                {
                    int     segs = LineCurvatureSubdivision.SegmentSteps(SrcUp[rStart + k], SrcUp[rStart + k + 1], MaxRefineAngleRad);
                    double2 a    = SrcTile[rStart + k];
                    double2 b    = SrcTile[rStart + k + 1];
                    for (int j = 1; j <= segs; j++)
                    {
                        double t = (double)j / segs;
                        OutSubTile.Add(a + (b - a) * t);
                    }
                }

                OutRingSubOffsets.Add(OutSubTile.Length);
            }

            int total = OutSubTile.Length;
            OutSubGeo.ResizeUninitialized(total);
            OutSubWorld.ResizeUninitialized(total);
            OutSubUp.ResizeUninitialized(total);
        }
    }
}
