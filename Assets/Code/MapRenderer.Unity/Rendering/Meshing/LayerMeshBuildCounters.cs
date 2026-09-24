using System.Threading;

namespace MapRenderer.Unity.Rendering.Meshing
{
    /// <summary>
    /// Process-wide debug counters shared by every <see cref="ILayerMeshBuild"/> implementer
    /// (job-scheduling-design.md). A DELTA meter for teeth: absolute values are meaningless — this is a
    /// process-wide static and an EditMode batch run is one process. Every build funnels through
    /// <see cref="LayerMeshBuildPool{T}.Rent"/>, so this counter has no excluded construction path.
    /// </summary>
    internal static class LayerMeshBuildCounters
    {
        private static long _liveBuilds;
        private static long _totalBuildsCreated;
        private static long _negativeObservations;

        /// <summary>Net live builds holding columns.</summary>
        internal static long DebugLiveBuilds => Interlocked.Read(ref _liveBuilds);

        /// <summary>Monotonic count of builds ever rented with real columns — never decremented. A tooth
        /// asserting only that a delta returned to zero cannot tell "balanced" from "nothing ever happened";
        /// this is the non-vacuity witness that separates them.</summary>
        internal static long DebugTotalBuildsCreated => Interlocked.Read(ref _totalBuildsCreated);

        /// <summary>Non-zero iff <see cref="RecordDisposed"/>'s decrement ever took <see cref="DebugLiveBuilds"/>
        /// below zero, as <see cref="Tile.Processing.TileBuildGraph.DebugNegativeObservations"/> does. Pooling
        /// adds this hazard: <c>_disposed</c> guards a second dispose of the same lease, but not a stale
        /// reference disposing a re-rented instance, whose <c>_disposed</c> <c>Reset</c> cleared.</summary>
        internal static long DebugNegativeObservations => Interlocked.Read(ref _negativeObservations);

        /// <summary>Records one build renting real columns — called by each implementer's own <c>Rent</c>
        /// factory. Every call corresponds to owned columns, so there is nothing to gate here: a render layer
        /// reaches <c>Rent</c> only past its emptiness gate (<c>ITileMeshRenderLayer.BuildGraphRequest</c>),
        /// and <c>TileManager.KickSourcelessBackground</c> and test fixtures call it directly.</summary>
        internal static void RecordRented()
        {
            Interlocked.Increment(ref _liveBuilds);
            Interlocked.Increment(ref _totalBuildsCreated);
        }

        /// <summary>Records one build's columns freed — called once by each implementer's own
        /// <see cref="ILayerMeshBuild.Dispose"/>.</summary>
        internal static void RecordDisposed()
        {
            long after = Interlocked.Decrement(ref _liveBuilds);
            if (after < 0) Interlocked.Increment(ref _negativeObservations);
        }
    }
}
