using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Geometry
{
    /// <summary>
    /// Burst job: clips decoded MVT rings, in tile space, to the axis-aligned window
    /// <c>[ClipMin, ClipMax]</c> — the tile plus however much of its buffer the caller chose to keep
    /// (<c>MapRenderer.Core.Tiles.TileBufferClip</c>). Runs between <see cref="MvtDecodeJob"/> and
    /// <see cref="RingAssemblyJob"/>: rings are still just rings here, with no polygon/hole structure yet,
    /// which is exactly what makes clipping simple.
    ///
    /// <para><b>Coordinate space and winding (producer declaration).</b> Input and output are both raw
    /// <b>tile space</b> — no projection, no rescaling. Sutherland–Hodgman is orientation-preserving, so a
    /// ring's output winding EQUALS its input winding (the canonical CCW-in-tile-space convention,
    /// <c>docs/coordinates-and-projections.md</c> §7.1). The Unity-front reversal for stock Cull Back stays
    /// where it is, at the mesh-write boundary in <c>StyledFillTileBuilder</c>.</para>
    ///
    /// <para><b>Boundary is inclusive</b> (<c>&gt;= min</c>, <c>&lt;= max</c>): a vertex exactly on the
    /// window edge is INSIDE. Combined with the bbox fast path this means the synthetic full-extent
    /// background ring passes through untouched at any margin ≥ 0 — the free falsifiers
    /// (<c>A6NonMvtDecoderTests</c>, <c>TileBackgroundQuadProjectionTests</c>) assert exactly 4 vertices for
    /// it, and would red on an exclusive test or a duplicated on-boundary point.</para>
    ///
    /// <para><b>Fast path (structural, not an optimisation).</b> A ring whose bbox already lies inside the
    /// window is copied verbatim, so "geometry that was already inside is bit-identical" is a property of
    /// the control flow rather than of the clipping arithmetic.</para>
    ///
    /// <para><b>Degenerate output is expected and fine.</b> A concave ring crossing the boundary several
    /// times emits one ring with zero-width channels running along the clip edge; a ring wholly outside
    /// emits nothing and is dropped here. <see cref="RingAssemblyJob"/>'s two filters (<c>rLen &lt; 3</c>,
    /// <c>|area2| &lt; 1</c>) clear the fully-degenerate cases, and <see cref="EarcutJob"/>'s cure → split →
    /// clean-drop cascade already handles collinear/zero-area input.</para>
    ///
    /// <para>Does NOT reference <c>MapRenderer.Core</c> — the window arrives as plain <c>double2</c>s, so
    /// Core's <c>System.Math</c> stays off the Burst path (same rule as <see cref="RingAssemblyJob"/>).</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct RingClipJob : IJob
    {
        /// <summary>Working-buffer sizing, as a multiple of the LONGEST input ring — <c>BufferA</c>/<c>BufferB</c>
        /// must each be at least this. Deliberately an OVER-estimate: in Burst an overrun corrupts memory
        /// silently instead of throwing, so the safe direction is the only direction.
        ///
        /// <para>16 comes from the loose per-plane bound (2 vertices per input edge ⇒ 2⁴). The <b>tight</b>
        /// bound is 1.5⁴ ≈ 5.06×: the alternating in/out case that maximises output also forces
        /// <c>#entering == #exiting</c>, which caps the entering-edge share at 50% and yields 1.5·len per
        /// plane, not 2·len. Left at 16 because the cost is transient scratch and the risk of being wrong in
        /// the other direction is silent corruption — but the headroom is ~3×, which is where to look first
        /// if the allocation ever matters (it is ~26 MB for a 50 k-vertex coastline ring).</para></summary>
        public const int BufferLengthMultiplier = 16;

        // ── Input ──────────────────────────────────────────────────────────────────────────────
        [ReadOnly] public NativeArray<double2> Vertices;
        [ReadOnly] public NativeArray<int>     RingOffsets;    // length = RingCount + 1 (sentinel)
        [ReadOnly] public NativeArray<int>     RingFeatureIdx; // which feature each ring belongs to

        /// <summary>IR B7: ring indices into <see cref="RingOffsets"/>, in the order the consumer wants them
        /// visited — <b>not</b> <c>0..ringCount</c>. The buffer is shared across consumers now, so which rings
        /// this clip pass reads, and in what order, is the caller's decision rather than the buffer's extent.
        /// Its length is the number of rings considered (survivors may be fewer).</summary>
        [ReadOnly] public NativeArray<int> RingVisitOrder;

        /// <summary>Inclusive window corners in tile units — <c>TileBufferClip.TryWindow</c>'s output.</summary>
        public double2 ClipMin;
        public double2 ClipMax;

        // ── Ping-pong working buffers, one ring at a time ────────────────────────────────────────────
        // Sized BufferLengthMultiplier × the longest input ring; never read across rings.
        public NativeArray<double2> BufferA;
        public NativeArray<double2> BufferB;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        // Length-authoritative lists: OutRingOffsets holds the same start+sentinel layout the decode stage
        // produces (so RingAssemblyJob consumes them unchanged), with OutRingOffsets.Length - 1 rings.
        public NativeList<double2> OutVertices;
        public NativeList<int>     OutRingOffsets;
        public NativeList<int>     OutRingFeatureIdx;

        public void Execute()
        {
            OutVertices.Clear();
            OutRingOffsets.Clear();
            OutRingFeatureIdx.Clear();
            OutRingOffsets.Add(0);

            for (int k = 0; k < RingVisitOrder.Length; k++)
            {
                int ri     = RingVisitOrder[k];
                int rStart = RingOffsets[ri];
                int rLen   = RingOffsets[ri + 1] - rStart;
                if (rLen <= 0) continue;

                if (BboxInsideWindow(rStart, rLen))
                {
                    for (int i = 0; i < rLen; i++)
                        OutVertices.Add(Vertices[rStart + i]);
                    OutRingOffsets.Add(OutVertices.Length);
                    OutRingFeatureIdx.Add(RingFeatureIdx[ri]);
                    continue;
                }

                // A sub-triangle ring that is not wholly inside cannot survive assembly (rLen < 3 filter);
                // skip the clip rather than run Sutherland–Hodgman over a non-ring.
                if (rLen < 3) continue;

                int clippedLen = ClipRing(rStart, rLen);
                if (clippedLen == 0) continue; // wholly outside the window — drop, never reaches assembly

                for (int i = 0; i < clippedLen; i++)
                    OutVertices.Add(BufferA[i]);
                OutRingOffsets.Add(OutVertices.Length);
                OutRingFeatureIdx.Add(RingFeatureIdx[ri]);
            }
        }

        // ── Clipping ──────────────────────────────────────────────────────────────────────────

        private bool BboxInsideWindow(int rStart, int rLen)
        {
            double2 lo = Vertices[rStart];
            double2 hi = lo;
            for (int i = 1; i < rLen; i++)
            {
                double2 v = Vertices[rStart + i];
                lo = math.min(lo, v);
                hi = math.max(hi, v);
            }
            return lo.x >= ClipMin.x && lo.y >= ClipMin.y && hi.x <= ClipMax.x && hi.y <= ClipMax.y;
        }

        /// <summary>
        /// Clips one input ring against the window's four half-planes, ping-ponging between the scratch
        /// buffers. Returns the surviving vertex count; the survivors always end up in <see cref="BufferA"/>
        /// — EVERY pass ends by swapping, so that holds for any number of planes, not because four is even.
        /// (Stated precisely because a fifth plane added on the "even swaps" reading would break it.)
        /// </summary>
        private int ClipRing(int rStart, int rLen)
        {
            for (int i = 0; i < rLen; i++)
                BufferA[i] = Vertices[rStart + i];
            int len = rLen;

            // axis 0 = x, axis 1 = y; keepAbove = the >= min half-plane, else the <= max one.
            len = ClipAgainstPlane(len, axis: 0, boundary: ClipMin.x, keepAbove: true);
            if (len == 0) return 0;
            len = ClipAgainstPlane(len, axis: 0, boundary: ClipMax.x, keepAbove: false);
            if (len == 0) return 0;
            len = ClipAgainstPlane(len, axis: 1, boundary: ClipMin.y, keepAbove: true);
            if (len == 0) return 0;
            len = ClipAgainstPlane(len, axis: 1, boundary: ClipMax.y, keepAbove: false);
            return len;
        }

        /// <summary>
        /// One Sutherland–Hodgman pass: reads <paramref name="len"/> vertices from <see cref="BufferA"/>,
        /// writes the survivors to <see cref="BufferB"/>, then swaps so the result is back in
        /// <see cref="BufferA"/>. Returns the new length.
        /// </summary>
        private int ClipAgainstPlane(int len, int axis, double boundary, bool keepAbove)
        {
            int outLen = 0;
            double2 prev = BufferA[len - 1];
            bool prevInside = Inside(prev, axis, boundary, keepAbove);

            for (int i = 0; i < len; i++)
            {
                double2 cur = BufferA[i];
                bool curInside = Inside(cur, axis, boundary, keepAbove);

                if (curInside)
                {
                    if (!prevInside)
                        outLen = Emit(outLen, Intersect(prev, cur, axis, boundary));
                    outLen = Emit(outLen, cur);
                }
                else if (prevInside)
                {
                    outLen = Emit(outLen, Intersect(prev, cur, axis, boundary));
                }

                prev       = cur;
                prevInside = curInside;
            }

            // Close the ring: an exit crossing whose entry vertex sat exactly ON the plane emits that vertex
            // a second time, and the wrap-around can leave the first and last equal. Both are zero-length
            // edges, and both are what turn an on-boundary quad into a 5-vertex ring.
            if (outLen > 1 && BufferB[outLen - 1].Equals(BufferB[0]))
                outLen--;

            NativeArray<double2> consumed = BufferA;
            BufferA = BufferB;
            BufferB = consumed;
            return outLen;
        }

        /// <summary>Appends unless it would repeat the previous vertex (a zero-length edge).</summary>
        private int Emit(int outLen, double2 v)
        {
            if (outLen > 0 && BufferB[outLen - 1].Equals(v)) return outLen;
            BufferB[outLen] = v;
            return outLen + 1;
        }

        private static bool Inside(double2 p, int axis, double boundary, bool keepAbove)
        {
            double c = axis == 0 ? p.x : p.y;
            return keepAbove ? c >= boundary : c <= boundary;
        }

        /// <summary>
        /// Where segment <paramref name="a"/>→<paramref name="b"/> meets the plane. The clipped axis is
        /// assigned the boundary EXACTLY (not the interpolated value) so a later inclusive-boundary test on
        /// this vertex — the next plane's, or the neighbouring tile's — cannot disagree by a rounding step.
        /// </summary>
        private static double2 Intersect(double2 a, double2 b, int axis, double boundary)
        {
            double ca = axis == 0 ? a.x : a.y;
            double cb = axis == 0 ? b.x : b.y;
            double d  = cb - ca;
            double t  = d != 0.0 ? (boundary - ca) / d : 0.0;
            double2 p = a + t * (b - a);
            if (axis == 0) p.x = boundary; else p.y = boundary;
            return p;
        }
    }
}
