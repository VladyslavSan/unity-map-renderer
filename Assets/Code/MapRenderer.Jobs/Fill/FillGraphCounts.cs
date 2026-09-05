namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// Blittable per-layer counts the fill graph's nodes report through — the four scalars a surviving output
    /// list's length cannot give (job-scheduling-design.md §3.2): vertex/index counts are read off
    /// <see cref="FillGraphOutput.TileVertices"/>/<see cref="FillGraphOutput.TriangleIndices"/> directly once
    /// <see cref="FillGraphOutput.Handle"/> is completed, so they need no field here. One
    /// <see cref="Unity.Collections.NativeArray{T}"/> allocation shared by its (now few) writers, in place of
    /// the half-dozen 1-element arrays the synchronous <see cref="FillMeshPipeline"/> allocates
    /// (<c>FillMeshPipeline.cs:242</c>'s "count written by a job" idiom, widened to one struct).
    ///
    /// <para>The error flag itself is NOT a member here — it is <see cref="FillGraphOutput.Error"/>, a
    /// standalone <see cref="Unity.Collections.NativeReference{T}"/>: an <c>Error*</c> code below can be set
    /// by a job (<see cref="FillSizingJob"/>, <see cref="FillAggregateJob"/>) that has no other reason to touch
    /// this struct's scalars, so folding it in here would make every error-only writer also a writer of
    /// counts it never reports — one more hidden edge on the shared container, the exact hazard this split
    /// removes. A Burst job cannot propagate an exception to the caller, so the never-fired capacity
    /// backstops <see cref="FillMeshPipeline.EnsureCapacity"/> guards in the synchronous pipeline become a
    /// non-zero error code here — more observable than the throw it replaces, because a sizing job can be
    /// handed an undersized capacity in a unit test and the flag asserted, a path the synchronous throw could
    /// never reach.</para>
    /// </summary>
    public struct FillGraphCounts
    {
        /// <summary>No error.</summary>
        public const int Ok = 0;

        /// <summary>The layer's polygon count exceeds the sizing job's <c>MaxPolygons</c> capacity.</summary>
        public const int ErrorPolygonCapacity = 1;

        /// <summary>The layer's total hole count exceeds the sizing job's <c>MaxHoles</c> capacity.</summary>
        public const int ErrorHoleCapacity = 2;

        /// <summary>A polygon's earcut merged-vertex count exceeded its pre-sized scratch capacity —
        /// mirrors <see cref="FillMeshPipeline.EnsureCapacity"/>'s per-polygon "earcut merged vertex"
        /// backstop. Never fires: <see cref="EarcutJob"/>'s own split-headroom guard already refuses to write
        /// past its scratch, so this is defense-in-depth, not a live path.</summary>
        public const int ErrorEarcutMergedVertexCapacity = 3;

        /// <summary>One of <see cref="FillSizingJob"/>'s four offset tables was not strictly increasing for
        /// some polygon — defence-in-depth, not the parallel earcut's bit-exactness precondition (that is
        /// structural: consecutive-entry slices over ANY table the sizing job can emit cannot overlap — see
        /// <see cref="EarcutBatchJob"/>'s own doc). What this catches instead: a non-monotonic table
        /// surfacing as an Editor-only <c>GetSubArray</c> bounds throw in a player build, where it would
        /// otherwise be undefined behaviour.</summary>
        public const int ErrorOffsetTableNotDisjoint = 4;

        /// <summary>Total polygons assembled for the layer.</summary>
        public int PolygonCount;

        /// <summary>Total rings the derive/select-or-clip stage emitted — <c>RingOffsets.Length - 1</c>, a
        /// value <see cref="FillSizingJob"/> already has on hand (it borrows the same offsets to size every
        /// polygon's outer/hole vertex counts), so this needs no dedicated node.</summary>
        public int RingCount;

        /// <summary>Total holes assembled for the layer.</summary>
        public int HoleCount;

        /// <summary>Total clean-drop ("force clip") loci across every polygon's earcut.</summary>
        public int ForceClipCount;
    }
}
