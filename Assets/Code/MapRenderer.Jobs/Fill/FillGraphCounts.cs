namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// Blittable per-layer counts the fill graph's nodes report through — the scalars a surviving output
    /// list's length cannot give. The layer's TOTAL vertex and index counts come off
    /// <see cref="FillGraphOutput.TileVertices"/>/<see cref="FillGraphOutput.TriangleIndices"/> once
    /// <see cref="FillGraphOutput.Handle"/> completes, so they need no field here. The boundary band's
    /// SHARE of those totals does, because nothing else separates the interior prefix from the band that
    /// <see cref="FillBandJob"/> appends to the same columns.
    ///
    /// <para>The error flag is NOT a member here — it is <see cref="FillGraphOutput.Error"/>, a standalone
    /// <see cref="Unity.Collections.NativeReference{T}"/>. A job can set an <c>Error*</c> code below with no
    /// other reason to touch these scalars, so folding it in would make every error-only writer a writer of
    /// counts it never reports.</para>
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

        /// <summary>Vertices <see cref="FillBandJob"/> appended for the boundary band — two per ring vertex,
        /// zero when that node did not run or found no ring to follow. The interior's own vertices are
        /// therefore <c>TileVertices.Length - BandVertexCount</c>, and they are the array's PREFIX: the band
        /// only ever appends.
        /// <para><b>ZERO on the curved arm, and that is not "no band".</b>
        /// <see cref="GlobeFillScatterJob"/> clears both band scalars because subdivision re-emits every
        /// vertex in traversal order — band and interior interleave and no prefix split survives. Band-ness
        /// there is the per-vertex <see cref="FillGraphOutput.VertexBand"/> attribute.</para></summary>
        public int BandVertexCount;

        /// <summary>Indices <see cref="FillBandJob"/> appended — six per ring edge. Unlike the vertices these
        /// are NOT a suffix even on the flat arm: band triangles are interleaved after their own feature's
        /// interior triangles (see that job's doc), so a reader separating the two filters by vertex index,
        /// not by position. Zeroed on the curved arm for the reason above.</summary>
        public int BandIndexCount;
    }
}
