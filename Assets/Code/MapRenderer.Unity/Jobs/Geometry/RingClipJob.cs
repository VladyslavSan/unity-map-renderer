using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Unity.Jobs.Geometry
{
    /// <summary>
    /// Burst job: Sutherland–Hodgman clip of rings, in tile space, to the inclusive window
    /// <c>[ClipMin, ClipMax]</c>, before <see cref="RingAssemblyJob"/>. Output keeps the input winding (CCW).
    /// A ring whose bbox lies inside is copied verbatim, so inside geometry stays bit-identical and the
    /// full-extent background ring keeps 4 vertices. A ring wholly outside is dropped; zero-width channels
    /// from concave rings are left to ring assembly's filters and earcut.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RingClipJob : IJob
    {
        /// <summary>Working-buffer sizing, as a multiple of the LONGEST input ring — <c>BufferA</c>/<c>BufferB</c>
        /// must each be at least this. Non-obvious why: it over-estimates, because a Burst overrun corrupts
        /// memory silently. 16 is the loose bound 2⁴ (2 vertices per edge per plane); the tight bound is
        /// 1.5⁴ ≈ 5.06, so the ~3× headroom is the first place to look if this allocation ever matters.</summary>
        public const int BufferLengthMultiplier = 16;

        // ── Input ──────────────────────────────────────────────────────────────────────────────
        [ReadOnly] public NativeArray<double2> Vertices;
        [ReadOnly] public NativeArray<int>     RingOffsets;    // length = RingCount + 1 (sentinel)
        [ReadOnly] public NativeArray<int>     RingFeatureIdx; // which feature each ring belongs to

        /// <summary>Ring indices into <see cref="RingOffsets"/>, in the order the consumer wants them
        /// visited — <b>not</b> <c>0..ringCount</c>. The buffer is shared across consumers, so which rings
        /// this clip pass reads, and in what order, is the caller's decision. Its length is the number of
        /// rings considered; survivors may be fewer.</summary>
        [ReadOnly] public NativeArray<int> RingVisitOrder;

        /// <summary>Inclusive window corners in tile units — <c>TileBufferClip.TryWindow</c>'s output.</summary>
        public double2 ClipMin;
        public double2 ClipMax;

        // ── Ping-pong working buffers, one ring at a time ────────────────────────────────────────────
        // Sized BufferLengthMultiplier × the longest input ring; never read across rings.
        public NativeArray<double2> BufferA;
        public NativeArray<double2> BufferB;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        // Length-authoritative lists in the decode stage's start+sentinel layout: Length - 1 rings.
        public NativeList<double2> OutVertices;
        public NativeList<int>     OutRingOffsets;
        public NativeList<int>     OutRingFeatureIdx;

        /// <summary>Parallel to <see cref="OutVertices"/>: 1 when the edge that starts at that vertex was introduced
        /// by the clip and lies on the window line, 0 when it comes from the input ring. A ring copied verbatim,
        /// and an input edge that already lies on the window line, read 0. Always constructed; written only
        /// when <see cref="WriteEdgeCut"/> is set.</summary>
        public NativeList<byte> OutEdgeCut;

        /// <summary>Scratch flags parallel to <see cref="BufferA"/>/<see cref="BufferB"/> (same capacity when
        /// <see cref="WriteEdgeCut"/> is set), swapped with them.</summary>
        public NativeArray<byte> CutA;

        /// <summary>The flag counterpart of <see cref="BufferB"/>: the buffer the current clip pass writes.</summary>
        public NativeArray<byte> CutB;

        /// <summary>True when the caller reads <see cref="OutEdgeCut"/>.</summary>
        public bool WriteEdgeCut;

        public void Execute()
        {
            OutVertices.Clear();
            OutRingOffsets.Clear();
            OutRingFeatureIdx.Clear();
            OutRingOffsets.Add(0);
            if (WriteEdgeCut) OutEdgeCut.Clear();

            for (int k = 0; k < RingVisitOrder.Length; k++)
            {
                int ri     = RingVisitOrder[k];
                int rStart = RingOffsets[ri];
                int rLen   = RingOffsets[ri + 1] - rStart;
                if (rLen <= 0) continue;

                if (BboxInsideWindow(rStart, rLen))
                {
                    for (int i = 0; i < rLen; i++)
                    {
                        OutVertices.Add(Vertices[rStart + i]);
                        if (WriteEdgeCut) OutEdgeCut.Add(0);
                    }
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
                {
                    OutVertices.Add(BufferA[i]);
                    if (WriteEdgeCut) OutEdgeCut.Add(CutA[i]);
                }
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
            {
                BufferA[i] = Vertices[rStart + i];
                if (WriteEdgeCut) CutA[i] = 0; // every input edge is genuine
            }
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
            byte prevCut = WriteEdgeCut ? CutA[len - 1] : (byte)0;

            for (int i = 0; i < len; i++)
            {
                double2 cur = BufferA[i];
                bool curInside = Inside(cur, axis, boundary, keepAbove);
                byte curCut = WriteEdgeCut ? CutA[i] : (byte)0;

                if (curInside)
                {
                    // Entry point: its outgoing edge is the inside part of prev→cur, so it inherits prev's flag.
                    if (!prevInside)
                        outLen = Emit(outLen, Intersect(prev, cur, axis, boundary), prevCut);
                    outLen = Emit(outLen, cur, curCut);
                }
                else if (prevInside)
                {
                    // Exit point: the edge that leaves it runs along the window line to the next entry — a cut.
                    outLen = Emit(outLen, Intersect(prev, cur, axis, boundary), 1);
                }

                prev       = cur;
                prevInside = curInside;
                prevCut    = curCut;
            }

            // Close the ring: Emit drops a repeated on-plane vertex, but the wrap-around can still leave
            // first == last. Dropping that zero-length edge keeps an on-boundary quad at 4 vertices.
            if (outLen > 1 && BufferB[outLen - 1].Equals(BufferB[0]))
                outLen--;

            NativeArray<double2> consumed = BufferA;
            BufferA = BufferB;
            BufferB = consumed;
            if (WriteEdgeCut)
            {
                NativeArray<byte> consumedCut = CutA;
                CutA = CutB;
                CutB = consumedCut;
            }
            return outLen;
        }

        /// <summary>Appends unless it would repeat the previous vertex (a zero-length edge). A repeat takes over
        /// the previous vertex's outgoing flag, because the zero-length edge disappears and this vertex's
        /// edge is the one that remains.</summary>
        private int Emit(int outLen, double2 v, byte cut)
        {
            if (outLen > 0 && BufferB[outLen - 1].Equals(v))
            {
                if (WriteEdgeCut) CutB[outLen - 1] = cut;
                return outLen;
            }
            BufferB[outLen] = v;
            if (WriteEdgeCut) CutB[outLen] = cut;
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
