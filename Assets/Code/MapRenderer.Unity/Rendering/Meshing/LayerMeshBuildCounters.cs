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
        /// below zero — the hazard pooling adds: each instance's own
        /// <c>_disposed</c> flag only guards a SECOND dispose of the SAME lease, not a stale reference
        /// disposing an instance the pool has since re-rented to someone else (that lease's own
        /// <c>_disposed</c> was already reset to <c>false</c> by <c>Reset</c>). Mirrors
        /// <see cref="Tile.Processing.TileBuildGraph.DebugNegativeObservations"/>'s own idiom.</summary>
        internal static long DebugNegativeObservations => Interlocked.Read(ref _negativeObservations);

        /// <summary>Records one build renting real columns — called by each implementer's own <c>Rent</c>
        /// factory. A source layer only reaches <c>Rent</c> once its render layer's own emptiness gate has
        /// already passed (<c>ITileMeshRenderLayer.BuildGraphRequest</c> returns <c>null</c> otherwise), but
        /// that gate is not the only path here: <c>TileManager.KickSourcelessBackground</c> and test fixtures
        /// call <c>Rent</c> directly, with no render layer or gate in between. Either way, every call really
        /// does correspond to owned columns, with nothing left to gate a second time.</summary>
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
