namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// Error codes the line graph's nodes report through <see cref="LineGraphOutput.Error"/>
    /// (job-scheduling-design.md §8 stage 5) — a SEPARATE type from <see cref="FillGraphCounts"/>, not a
    /// value added to it: <see cref="FillGraphCounts"/> is fill-specific by name, by its doc and by its four
    /// scalar fields (<c>PolygonCount</c>/<c>RingCount</c>/<c>HoleCount</c>/<c>ForceClipCount</c>), none of
    /// which a line graph reports. This type shares only the <see cref="Ok"/> = 0 convention.
    /// </summary>
    public struct LineGraphCounts
    {
        /// <summary>No error.</summary>
        public const int Ok = 0;

        /// <summary>The layer's total ribbon vertex count would exceed
        /// <see cref="LineLayerInput.MaxOutputVertices"/> — the always-bound-loops backstop
        /// <see cref="LineRibbonAggregateJob"/> enforces per-ring, stopping the append rather than
        /// overrunning its output buffer.</summary>
        public const int ErrorLineVertexCapacity = 1;

        /// <summary>One of <see cref="LineRibbonSizingJob"/>'s two offset tables was not strictly increasing
        /// for some ring — defence-in-depth, not the parallel ribbon's bit-exactness precondition (that is
        /// structural — see <see cref="LineRibbonBatchJob"/>'s own doc). Mirrors
        /// <see cref="FillGraphCounts.ErrorOffsetTableNotDisjoint"/>'s reasoning exactly, for the line
        /// graph's own offset tables.</summary>
        public const int ErrorOffsetTableNotDisjoint = 2;
    }
}
