namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// Blittable per-layer counts the fill graph's nodes report, the scalars no list length gives. Totals come
    /// off <see cref="FillGraphOutput.TileVertices"/>/<see cref="FillGraphOutput.TriangleIndices"/> after
    /// <see cref="FillGraphOutput.Handle"/> completes; the band's share is here, because nothing else separates
    /// the interior prefix from what <see cref="FillBandJob"/> appends. The error flag is the separate
    /// <see cref="FillGraphOutput.Error"/>, so an error-only writer does not write counts.
    /// </summary>
    public struct FillGraphCounts
    {
        /// <summary>No error.</summary>
        public const int Ok = 0;

        /// <summary>The layer's polygon count exceeds the sizing job's <c>MaxPolygons</c> capacity.</summary>
        public const int ErrorPolygonCapacity = 1;

        /// <summary>The layer's total hole count exceeds the sizing job's <c>MaxHoles</c> capacity.</summary>
        public const int ErrorHoleCapacity = 2;

        /// <summary>A polygon's earcut merged-vertex count exceeded its pre-sized scratch capacity. Never
        /// fires: <see cref="EarcutJob"/>'s split-headroom guard already refuses to write past its scratch,
        /// so this is defence-in-depth, not a live path.</summary>
        public const int ErrorEarcutMergedVertexCapacity = 3;

        /// <summary>One of <see cref="SizingJob"/>'s four offset tables was not strictly increasing for some
        /// polygon. Defence-in-depth against a non-monotonic table, which would otherwise be an Editor-only
        /// <c>GetSubArray</c> bounds throw and undefined behaviour in a player build.</summary>
        public const int ErrorOffsetTableNotDisjoint = 4;

        /// <summary>Total polygons assembled for the layer.</summary>
        public int PolygonCount;

        /// <summary>Total rings the select-or-clip stage emitted — <c>RingOffsets.Length - 1</c>, which
        /// <see cref="SizingJob"/> already has on hand.</summary>
        public int RingCount;

        /// <summary>Total holes assembled for the layer.</summary>
        public int HoleCount;

        /// <summary>Total clean-drop ("force clip") loci across every polygon's earcut.</summary>
        public int ForceClipCount;

        /// <summary>Vertices <see cref="FillBandJob"/> appended: two per ring vertex, zero if it did not run
        /// or found no ring. On the flat arm the interior is the <c>TileVertices.Length - BandVertexCount</c>
        /// prefix. Zero on the curved arm is not "no band": <see cref="GlobeFillScatterJob"/> clears it, since
        /// subdivision interleaves band and interior; <see cref="FillGraphOutput.VertexBand"/> marks it
        /// there.</summary>
        public int BandVertexCount;

        /// <summary>Indices <see cref="FillBandJob"/> appended — six per ring edge. Unlike the vertices these
        /// are NOT a suffix even on the flat arm: band triangles are interleaved after their own feature's
        /// interior triangles (see that job's doc), so a reader separating the two filters by vertex index,
        /// not by position. Zeroed on the curved arm for the reason above.</summary>
        public int BandIndexCount;
    }
}
