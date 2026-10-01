using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Jobs.Fill
{
    /// <summary>
    /// The fill graph's boundary-band node (docs/fill-boundary-antialiasing-design.md): after
    /// <see cref="AggregateJob"/>'s interior, it appends two vertices per ring vertex and one quad per ring edge
    /// to the same columns and index buffer. Both band vertices sit at the ring vertex's own tile coordinate;
    /// only the outer one carries <c>(dirEast, dirNorth, 1)</c>, which the shader turns into a one-pixel OUTWARD
    /// displacement. Non-local invariant: outward is <c>sign(area2(outer)) * (d.y, -d.x)</c>, one sign for the
    /// polygon and its holes, and tile space is Y-down, so <c>dirNorth = -miter.y</c>. Band indices follow each
    /// feature's own interior (painter order under <c>ZWrite Off</c>) and are reversed for a positively-wound ring
    /// to match earcut's canonical winding. A band quad is degenerate in tile space, so it subdivides conformingly
    /// on the curved arm, and the shader must not scale displacement by the interpolated <c>side</c>. With
    /// <see cref="ClipEnabled"/>, an edge with both endpoints exactly on one window line gets no quad (exact
    /// because <see cref="RingClipJob"/> writes the boundary verbatim), but both band vertices are still written,
    /// so the band vertex count stays at least twice the ring total and the index count comes from the write cursor.
    /// Such an edge also gives no normal to the miter at its endpoints, so the outer vertex there does not lean
    /// across the seam. An edge whose outer vertices can cross also gets its first outer triangle a second time, wound
    /// the other way, so Cull Back draws the quad whole at any width. A convex corner that clips the miter, and a 180 degree spike,
    /// get a fan of outer vertices instead: a round tip.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct FillBandJob : IJob
    {
        /// <summary>Miter factor ceiling: a sharp convex corner gets a round tip instead of an outer vertex thrown arbitrarily far, as
        /// <c>RibbonJob.MiterLimit</c> does for joins; not a band-width knob.
        /// <c>internal</c> so a rendered test derives its reach bound: band ink lies within this many device px.</summary>
        internal const double MiterLimit = 4.0;

        /// <summary>Edges whose miter faces them no more than this are left out: a 180 degree spike has no outward height.</summary>
        private const double MinFacing = 1e-6;

        /// <summary>Most segments of one round tip. Each covers up to <see cref="FanStepRadians"/>, so a 180 degree spike needs all six.</summary>
        internal const int MaxFanSegments = 6;

        /// <summary>Largest turn one fan segment spans, in radians (30 degrees).</summary>
        private const double FanStepRadians = math.PI_DBL / 6.0;

        // ── Input: the clipped ring columns, plus RingAssemblyJob's polygon descriptors ─────────────
        [ReadOnly] public NativeArray<double2> RingVertices;
        [ReadOnly] public NativeArray<int>     RingOffsets;
        [ReadOnly] public NativeArray<int>     RingFeatureIdx;
        [ReadOnly] public NativeArray<int>     PolyOuterRingIdx;
        [ReadOnly] public NativeArray<int>     PolyHoleListStart;
        [ReadOnly] public NativeArray<int>     PolyHoleCount;
        [ReadOnly] public NativeArray<int>     HoleRingIdxs;
        [ReadOnly] public NativeArray<int>     PolyCountArr;

        /// <summary>Whether the rings above reached this node through <see cref="RingClipJob"/> rather than
        /// <c>RingSelectJob</c> — i.e. whether <see cref="ClipMin"/>/<see cref="ClipMax"/> hold a window at
        /// all. The flag is not redundant with the window's value: <c>default(double2)</c> is <c>(0,0)</c>,
        /// which is the tile's own origin corner, so an unset window silently reads as "suppress everything
        /// along x = 0 and y = 0".</summary>
        public bool ClipEnabled;

        /// <summary>The inclusive clip window, in the same tile units the ring columns carry — verbatim what
        /// <c>TileBufferClip.TryWindow</c> handed <see cref="RingClipJob"/>.</summary>
        public double2 ClipMin;
        public double2 ClipMax;

        // ── Output: the aggregate's own columns, extended in place ──────────────────────────────────
        public NativeList<double2> TileVertices;
        public NativeList<float3>  VertexBand;
        public NativeList<int>     VertexFeatureIdx;
        public NativeList<int>     TriangleIndices;

        /// <summary>The flat <c>(1,0,0)</c> for the band's own slots, as <see cref="AggregateJob"/> writes for the
        /// interior: the one column no later flat-arm node fills. <c>WorldPositions</c>/<c>VertexUp</c>/<c>Geo</c>
        /// are only resized; later nodes fill them, so a band vertex's world position is bit-identical to its
        /// ring vertex. The curved arm reads none of these four.</summary>
        public NativeList<double3> VertexEast;

        public NativeList<double3> WorldPositions;
        public NativeList<double3> VertexUp;
        public NativeList<GeoCoordinate> Geo;

        /// <summary>[0]'s band vertex/index totals are written here — the two scalars that let a reader
        /// separate the interior prefix from the band, which no surviving list's length gives on its own. The
        /// prefix property is this node's output only: <see cref="GlobeFillScatterJob"/> clears both after
        /// subdivision has interleaved band and interior.</summary>
        public NativeArray<FillGraphCounts> Counts;

        public void Execute()
        {
            int interiorVerts = TileVertices.Length;
            int interiorIdx   = TriangleIndices.Length;

            FillGraphCounts counts = Counts[0];
            counts.BandVertexCount = 0;
            counts.BandIndexCount  = 0;
            Counts[0] = counts;

            // Nothing aggregated ⇒ nothing to band. This is also the guard that inherits SizingJob's capacity
            // early-return, which leaves PolyCountArr[0] stale and must not be allowed to drive a loop here.
            if (interiorVerts == 0) return;

            int polyCount = PolyCountArr[0];

            int totalRingVerts = 0;
            for (int p = 0; p < polyCount; p++)
            {
                totalRingVerts += RingLength(PolyOuterRingIdx[p]);
                int holeStart = PolyHoleListStart[p];
                int holeCount = PolyHoleCount[p];
                for (int h = 0; h < holeCount; h++)
                    totalRingVerts += RingLength(HoleRingIdxs[holeStart + h]);
            }
            if (totalRingVerts == 0) return;

            int fanSegments = CountFanSegments(polyCount);
            int bandVerts = 2 * totalRingVerts + fanSegments;     // a round tip adds one outer vertex per segment
            int bandIdx   = 9 * totalRingVerts + 3 * fanSegments; // at most 9 per ring vertex (6 for the quad, 3 for the twist cover), 3 per fan segment

            TileVertices.Resize(interiorVerts + bandVerts, NativeArrayOptions.UninitializedMemory);
            VertexBand.Resize(interiorVerts + bandVerts, NativeArrayOptions.UninitializedMemory);
            VertexFeatureIdx.Resize(interiorVerts + bandVerts, NativeArrayOptions.UninitializedMemory);
            VertexEast.Resize(interiorVerts + bandVerts, NativeArrayOptions.UninitializedMemory);
            WorldPositions.Resize(interiorVerts + bandVerts, NativeArrayOptions.UninitializedMemory);
            VertexUp.Resize(interiorVerts + bandVerts, NativeArrayOptions.UninitializedMemory);
            Geo.Resize(interiorVerts + bandVerts, NativeArrayOptions.UninitializedMemory);

            // The interior index array is read while the same list is rewritten interleaved, so it is copied
            // aside first. Allocator.Temp is legal here: this is a local inside Execute, not a job field.
            var interior = new NativeArray<int>(math.max(1, interiorIdx), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < interiorIdx; i++) interior[i] = TriangleIndices[i];
            TriangleIndices.Resize(interiorIdx + bandIdx, NativeArrayOptions.UninitializedMemory);

            int vertexCursor = interiorVerts;
            int indexCursor  = 0;
            int interiorCursor = 0;
            int poly = 0;

            while (poly < polyCount)
            {
                int feature = RingFeatureIdx[PolyOuterRingIdx[poly]];

                // This feature's interior triangles first — they are contiguous, because polygons are
                // assembled in ring order and a feature's rings are contiguous in the visit order.
                while (interiorCursor + 2 < interiorIdx &&
                       VertexFeatureIdx[interior[interiorCursor]] == feature)
                {
                    TriangleIndices[indexCursor++] = interior[interiorCursor++];
                    TriangleIndices[indexCursor++] = interior[interiorCursor++];
                    TriangleIndices[indexCursor++] = interior[interiorCursor++];
                }

                while (poly < polyCount && RingFeatureIdx[PolyOuterRingIdx[poly]] == feature)
                {
                    int outerRing = PolyOuterRingIdx[poly];
                    double outwardSign = SignedArea2(outerRing) > 0.0 ? 1.0 : -1.0;

                    EmitRing(outerRing, outwardSign, feature, ref vertexCursor, ref indexCursor);

                    int holeStart = PolyHoleListStart[poly];
                    int holeCount = PolyHoleCount[poly];
                    for (int h = 0; h < holeCount; h++)
                        EmitRing(HoleRingIdxs[holeStart + h], outwardSign, feature, ref vertexCursor, ref indexCursor);

                    poly++;
                }
            }

            while (interiorCursor < interiorIdx)
                TriangleIndices[indexCursor++] = interior[interiorCursor++];

            interior.Dispose();

            // The index list was sized for every edge; a suppressed one leaves a hole at the end rather than
            // in the middle, because the cursor simply never advanced over it.
            TriangleIndices.Resize(indexCursor, NativeArrayOptions.UninitializedMemory);

            counts.BandVertexCount = bandVerts;
            counts.BandIndexCount  = indexCursor - interiorIdx;
            Counts[0] = counts;
        }

        /// <summary>Vertex count of ring <paramref name="ring"/> — rings are implicitly closed (the first
        /// vertex is not repeated, <c>MvtDecodeJob</c>'s ClosePath is a no-op), so this is also its edge
        /// count.</summary>
        /// <param name="ring">Ring index into <see cref="RingOffsets"/>.</param>
        /// <returns>The ring's vertex count.</returns>
        private int RingLength(int ring) => RingOffsets[ring + 1] - RingOffsets[ring];

        /// <summary>Shoelace signed area × 2 of one ring, in the SAME coordinates the perpendicular below is
        /// taken in — which is why the tile frame's y-down-ness does not enter the outward derivation at
        /// all, only the <c>dirNorth</c> flip at the write site.</summary>
        /// <param name="ring">Ring index into <see cref="RingOffsets"/>.</param>
        /// <returns>Twice the ring's signed area; its SIGN is what this job uses.</returns>
        private double SignedArea2(int ring)
        {
            int start = RingOffsets[ring];
            int len   = RingLength(ring);
            double area = 0.0;
            for (int i = 0; i < len; i++)
            {
                double2 a = RingVertices[start + i];
                double2 b = RingVertices[start + (i + 1) % len];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        /// <summary>Total fan segments of every ring: the band's vertex and index budget beyond two vertices per ring vertex.</summary>
        /// <param name="polyCount">The number of polygons.</param>
        private int CountFanSegments(int polyCount)
        {
            int total = 0;
            for (int p = 0; p < polyCount; p++)
            {
                int outerRing = PolyOuterRingIdx[p];
                double outwardSign = SignedArea2(outerRing) > 0.0 ? 1.0 : -1.0;
                total += CountRingFanSegments(outerRing, outwardSign);
                int holeStart = PolyHoleListStart[p];
                int holeCount = PolyHoleCount[p];
                for (int h = 0; h < holeCount; h++)
                    total += CountRingFanSegments(HoleRingIdxs[holeStart + h], outwardSign);
            }
            return total;
        }

        private int CountRingFanSegments(int ring, double outwardSign)
        {
            int start = RingOffsets[ring];
            int len   = RingLength(ring);
            int total = 0;
            for (int i = 0; i < len; i++) total += FanSegmentsAt(start, len, i, outwardSign);
            return total;
        }

        /// <summary>The outward normals of the two edges that meet at ring vertex <paramref name="vertex"/>; a cut edge on the window
        /// line gives zero, because it has no band.</summary>
        private void JoinNormals(int start, int len, int vertex, double outwardSign, out double2 incoming, out double2 outgoing)
        {
            double2 current  = RingVertices[start + vertex];
            double2 previous = RingVertices[start + (vertex + len - 1) % len];
            double2 next     = RingVertices[start + (vertex + 1) % len];
            incoming = LiesAlongOneWindowLine(previous, current) ? double2.zero : Outward(current - previous, outwardSign);
            outgoing = LiesAlongOneWindowLine(current, next) ? double2.zero : Outward(next - current, outwardSign);
        }

        /// <summary>True iff the corner between two outward normals turns the way the ring turns, so its normals diverge.</summary>
        private static bool IsConvex(double2 incoming, double2 outgoing, double outwardSign)
            => (incoming.x * outgoing.y - incoming.y * outgoing.x > 0.0) == (outwardSign > 0.0);

        /// <summary>The segments of the round tip at ring vertex <paramref name="vertex"/>, or 0 for a plain miter. A tip is a convex
        /// corner whose miter the limit clips, or a 180 degree spike, which has no side and is read as a needle. A reflex corner keeps
        /// a miter, which <see cref="MiterLimit"/> clips.</summary>
        private int FanSegmentsAt(int start, int len, int vertex, double outwardSign)
        {
            JoinNormals(start, len, vertex, outwardSign, out double2 incoming, out double2 outgoing);
            if (math.lengthsq(incoming) == 0.0 || math.lengthsq(outgoing) == 0.0) return 0;

            double2 sum = incoming + outgoing;
            double sumLength = math.length(sum);
            if (sumLength >= 1e-12)
            {
                if (!IsConvex(incoming, outgoing, outwardSign) || math.dot(sum / sumLength, outgoing) >= 1.0 / MiterLimit) return 0;
            }

            double turn = math.acos(math.clamp(math.dot(incoming, outgoing), -1.0, 1.0));
            return (int)math.clamp(math.ceil(turn / FanStepRadians), 1.0, (double)MaxFanSegments);
        }

        /// <summary>Appends one ring's band: per ring vertex an inner vertex, then either one outer vertex or, at a round tip, one per
        /// fan direction; then the quad of each edge and the fan triangles.</summary>
        /// <param name="ring">Ring index into <see cref="RingOffsets"/>.</param>
        /// <param name="outwardSign">The owning polygon's outer-ring area sign — see the type doc for why
        /// one sign serves the holes too.</param>
        /// <param name="feature">Feature index every vertex of this ring carries.</param>
        /// <param name="vertexCursor">Write cursor into the vertex columns.</param>
        /// <param name="indexCursor">Write cursor into <see cref="TriangleIndices"/>.</param>
        private void EmitRing(int ring, double outwardSign, int feature, ref int vertexCursor, ref int indexCursor)
        {
            int start = RingOffsets[ring];
            int len   = RingLength(ring);

            var miters   = new NativeArray<double2>(len, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var segments = new NativeArray<int>(len, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var inners   = new NativeArray<int>(len, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < len; i++)
            {
                // A cut edge on the window line has no band, so it contributes no normal: at a vertex that
                // joins it, the miter is the other edge's own normal and does not lean across the seam.
                JoinNormals(start, len, i, outwardSign, out double2 incoming, out double2 outgoing);
                miters[i]   = Miter(incoming, outgoing, outwardSign);
                segments[i] = FanSegmentsAt(start, len, i, outwardSign);
            }

            for (int i = 0; i < len; i++)
            {
                double2 current = RingVertices[start + i];
                int segmentCount = segments[i];
                inners[i] = vertexCursor;

                // Inner: the ring vertex itself, bitwise zero band. Outer: the SAME coordinate — the one
                // device pixel is added by the vertex shader, so nothing here moves.
                WriteBandVertex(vertexCursor++, current, float3.zero, feature);
                if (segmentCount == 0)
                {
                    double2 miter = miters[i];
                    WriteBandVertex(vertexCursor++, current, new float3((float)miter.x, (float)-miter.y, 1f), feature);
                    continue;
                }

                // A round tip: unit directions from the incoming normal to the outgoing one, turning the way the ring turns.
                JoinNormals(start, len, i, outwardSign, out double2 from, out double2 to);
                double turn = math.acos(math.clamp(math.dot(from, to), -1.0, 1.0)) * (outwardSign > 0.0 ? 1.0 : -1.0);
                for (int k = 0; k <= segmentCount; k++)
                {
                    double angle = turn * k / segmentCount;
                    double sin = math.sin(angle);
                    double cos = math.cos(angle);
                    double2 direction = new double2(from.x * cos - from.y * sin, from.x * sin + from.y * cos);
                    WriteBandVertex(vertexCursor++, current, new float3((float)direction.x, (float)-direction.y, 1f), feature);
                }
            }

            // These triangles wind with the ring's own sign, while earcut normalises to CCW-on-screen, so a
            // positively-wound ring's band is reversed to match the interior it borders.
            bool reverse = outwardSign > 0.0;

            for (int i = 0; i < len; i++)
            {
                int j = (i + 1) % len;

                // A round tip: its fan triangles run from the incoming normal to the outgoing one.
                for (int k = 0; k < segments[i]; k++)
                {
                    TriangleIndices[indexCursor++] = inners[i];
                    TriangleIndices[indexCursor++] = reverse ? inners[i] + 2 + k : inners[i] + 1 + k;
                    TriangleIndices[indexCursor++] = reverse ? inners[i] + 1 + k : inners[i] + 2 + k;
                }

                if (LiesAlongOneWindowLine(RingVertices[start + i], RingVertices[start + j])) continue;

                int innerHere = inners[i];
                int outerHere = innerHere + 1 + segments[i];   // the last outer vertex of a tip faces the next edge
                int innerNext = inners[j];
                int outerNext = innerNext + 1;                 // the first outer vertex of a tip faces the previous edge

                TriangleIndices[indexCursor++] = innerHere;
                TriangleIndices[indexCursor++] = reverse ? outerNext : outerHere;
                TriangleIndices[indexCursor++] = reverse ? outerHere : outerNext;

                TriangleIndices[indexCursor++] = innerHere;
                TriangleIndices[indexCursor++] = reverse ? innerNext : outerNext;
                TriangleIndices[indexCursor++] = reverse ? outerNext : innerNext;

                // The outer edge can cross: the first triangle then winds backwards and Cull Back drops its wedge of
                // band. The same triangle, wound the other way, draws exactly then.
                if (CanTwist(start, len, i, outwardSign, miters, segments))
                {
                    TriangleIndices[indexCursor++] = innerHere;
                    TriangleIndices[indexCursor++] = reverse ? outerHere : outerNext;
                    TriangleIndices[indexCursor++] = reverse ? outerNext : outerHere;
                }
            }

            miters.Dispose();
            segments.Dispose();
            inners.Dispose();
        }

        private void WriteBandVertex(int index, double2 tile, float3 band, int feature)
        {
            TileVertices[index]     = tile;
            VertexBand[index]       = band;
            VertexFeatureIdx[index] = feature;
            VertexEast[index]       = new double3(1, 0, 0);
        }

        /// <summary>True iff the quad of edge <paramref name="edge"/> can twist at some zoom: its outer vertices lean along the
        /// edge so that their offset rays can cross. Non-local invariant: with outer heights <c>d</c> and leans <c>s</c> (the miter
        /// along the edge over its outward part), the quad winds backwards iff <c>L &lt; d_end * (s_start - s_end)</c>. So it needs
        /// <c>s_start &gt; s_end</c>, which only a reflex corner gives. <c>d</c> is the pixel scale, which the mesh does not know.</summary>
        /// <param name="start">Index of the ring's first vertex in <see cref="RingVertices"/>.</param>
        /// <param name="len">The ring's vertex count.</param>
        /// <param name="edge">The edge's start vertex within the ring.</param>
        /// <param name="outwardSign">The owning polygon's outer-ring area sign.</param>
        /// <param name="miters">The outward miter per ring vertex, tile space.</param>
        /// <param name="segments">The round-tip segments per ring vertex.</param>
        private bool CanTwist(int start, int len, int edge, double outwardSign, NativeArray<double2> miters, NativeArray<int> segments)
        {
            int next = (edge + 1) % len;
            double2 along = RingVertices[start + next] - RingVertices[start + edge];
            double2 outward = Outward(along, outwardSign);
            if (math.lengthsq(outward) == 0.0) return false;

            // A round tip ends the quad square, with no lean.
            double2 outerFrom = segments[edge] > 0 ? outward : miters[edge];
            double2 outerTo   = segments[next] > 0 ? outward : miters[next];

            double2 direction  = math.normalize(along);
            double  facingFrom = math.dot(outerFrom, outward);
            double  facingTo   = math.dot(outerTo, outward);
            if (facingFrom < MinFacing || facingTo < MinFacing) return false;

            return math.dot(outerFrom, direction) / facingFrom > math.dot(outerTo, direction) / facingTo;
        }

        /// <summary>Unit outward normal of an edge running <paramref name="edge"/> — <c>(d.y, -d.x)</c> signed
        /// by the polygon's winding. Returns zero for a degenerate (repeated-vertex) edge, which
        /// <see cref="Miter"/> then ignores.</summary>
        /// <param name="edge">The edge vector, tile space.</param>
        /// <param name="outwardSign">The owning polygon's outer-ring area sign.</param>
        /// <returns>A unit outward normal, or zero.</returns>
        private static double2 Outward(double2 edge, double outwardSign)
        {
            double lengthSq = math.lengthsq(edge);
            if (lengthSq < 1e-24) return double2.zero;
            return outwardSign * new double2(edge.y, -edge.x) / math.sqrt(lengthSq);
        }

        /// <summary>Whether an edge lies along one of the clip window's four lines, both endpoints on the
        /// same one — the band-suppression predicate. Always false with no window
        /// (<see cref="ClipEnabled"/>), because an unset window is <c>(0,0)</c> and would read the tile's own
        /// origin corner as a cut.</summary>
        /// <param name="a">The edge's first endpoint, tile space.</param>
        /// <param name="b">Its second.</param>
        /// <returns>True when the band must stop here.</returns>
        private bool LiesAlongOneWindowLine(double2 a, double2 b)
        {
            if (!ClipEnabled) return false;
            return (a.x == ClipMin.x && b.x == ClipMin.x)
                || (a.x == ClipMax.x && b.x == ClipMax.x)
                || (a.y == ClipMin.y && b.y == ClipMin.y)
                || (a.y == ClipMax.y && b.y == ClipMax.y);
        }

        /// <summary>Join bisector scaled by <c>1/cos(θ/2)</c> and clamped at <see cref="MiterLimit"/>, so the
        /// PERPENDICULAR band width stays one pixel through a corner rather than pinching — the factor rides
        /// in the vector's magnitude, which is what the vertex shader multiplies the pixel scale by.</summary>
        /// <param name="incoming">Outward normal of the edge arriving at the vertex; zero if degenerate.</param>
        /// <param name="outgoing">Outward normal of the edge leaving it; zero if degenerate.</param>
        /// <param name="outwardSign">The owning polygon's outer-ring area sign.</param>
        /// <returns>The outward miter vector, or zero when neither edge gave a direction.</returns>
        private static double2 Miter(double2 incoming, double2 outgoing, double outwardSign)
        {
            double2 sum = incoming + outgoing;
            double sumLength = math.length(sum);
            if (sumLength < 1e-12)
            {
                // A 180° spike (or both edges degenerate): there is no bisector. Fall back to whichever
                // normal exists, unscaled — the corner loses its miter, not its band.
                return math.lengthsq(outgoing) > 0.0 ? outgoing : incoming;
            }

            double2 bisector = sum / sumLength;
            double cosHalf = math.dot(bisector, math.lengthsq(outgoing) > 0.0 ? outgoing : incoming);
            if (cosHalf < 1.0 / MiterLimit) return bisector * MiterLimit;
            return bisector / cosHalf;
        }
    }
}
