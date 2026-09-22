namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// Error codes the line graph's nodes report through <see cref="LineGraphOutput.Error"/>. A SEPARATE
    /// type from <see cref="FillGraphCounts"/>, whose four scalar fields are all fill-specific and none of
    /// which a line graph reports. The two share only the <see cref="Ok"/> = 0 convention.
    /// </summary>
    public struct LineGraphCounts
    {
        /// <summary>No error.</summary>
        public const int Ok = 0;

        /// <summary>The layer's total ribbon vertex count would exceed
        /// <see cref="LayerInput.MaxOutputVertices"/> — the always-bound-loops backstop
        /// <see cref="RibbonAggregateJob"/> enforces per-ring, stopping the append rather than
        /// overrunning its output buffer.</summary>
        public const int ErrorLineVertexCapacity = 1;

        /// <summary>One of <see cref="RibbonSizingJob"/>'s two offset tables was not strictly increasing
        /// for some ring. Defence-in-depth, the line twin of
        /// <see cref="FillGraphCounts.ErrorOffsetTableNotDisjoint"/>.</summary>
        public const int ErrorOffsetTableNotDisjoint = 2;
    }
}
