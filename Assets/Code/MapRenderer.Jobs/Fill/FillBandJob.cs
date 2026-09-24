using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Fill
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
    /// so the band vertex count stays twice the ring total and the index count comes from the write cursor.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct FillBandJob : IJob
    {
        /// <summary>Miter factor ceiling: a sharp corner loses a sub-pixel wedge of band instead of throwing the
        /// outer vertex arbitrarily far, as <c>RibbonJob.MiterLimit</c> does for joins; not a band-width knob.
        /// <c>internal</c> so a rendered test derives its reach bound: band ink lies within this many device px.</summary>
        internal const double MiterLimit = 4.0;

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

            int bandVerts = 2 * totalRingVerts;
            int bandIdx   = 6 * totalRingVerts;

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

        /// <summary>Appends one ring's inner/outer vertex pairs and its quad indices.</summary>
        /// <param name="ring">Ring index into <see cref="RingOffsets"/>.</param>
        /// <param name="outwardSign">The owning polygon's outer-ring area sign — see the type doc for why
        /// one sign serves the holes too.</param>
        /// <param name="feature">Feature index every vertex of this ring carries.</param>
        /// <param name="vertexCursor">Write cursor into the vertex columns; advanced by <c>2 × ring length</c>.</param>
        /// <param name="indexCursor">Write cursor into <see cref="TriangleIndices"/>; advanced by
        /// <c>6 × ring length</c>.</param>
        private void EmitRing(int ring, double outwardSign, int feature, ref int vertexCursor, ref int indexCursor)
        {
            int start = RingOffsets[ring];
            int len   = RingLength(ring);
            int first = vertexCursor;

            for (int i = 0; i < len; i++)
            {
                double2 current = RingVertices[start + i];
                double2 previous = RingVertices[start + (i + len - 1) % len];
                double2 next     = RingVertices[start + (i + 1) % len];

                double2 miter = Miter(
                    Outward(current - previous, outwardSign),
                    Outward(next - current, outwardSign));

                // Inner: the ring vertex itself, bitwise zero band. Outer: the SAME coordinate — the one
                // device pixel is added by the vertex shader, so nothing here moves.
                TileVertices[vertexCursor]     = current;
                VertexBand[vertexCursor]       = float3.zero;
                VertexFeatureIdx[vertexCursor] = feature;
                VertexEast[vertexCursor]       = new double3(1, 0, 0);
                vertexCursor++;

                TileVertices[vertexCursor]     = current;
                VertexBand[vertexCursor]       = new float3((float)miter.x, (float)-miter.y, 1f);
                VertexFeatureIdx[vertexCursor] = feature;
                VertexEast[vertexCursor]       = new double3(1, 0, 0);
                vertexCursor++;
            }

            // These triangles wind with the ring's own sign, while earcut normalises to CCW-on-screen, so a
            // positively-wound ring's band is reversed to match the interior it borders.
            bool reverse = outwardSign > 0.0;

            for (int i = 0; i < len; i++)
            {
                int j = (i + 1) % len;
                if (LiesAlongOneWindowLine(RingVertices[start + i], RingVertices[start + j])) continue;

                int innerHere = first + 2 * i;
                int outerHere = innerHere + 1;
                int innerNext = first + 2 * j;
                int outerNext = innerNext + 1;

                TriangleIndices[indexCursor++] = innerHere;
                TriangleIndices[indexCursor++] = reverse ? outerNext : outerHere;
                TriangleIndices[indexCursor++] = reverse ? outerHere : outerNext;

                TriangleIndices[indexCursor++] = innerHere;
                TriangleIndices[indexCursor++] = reverse ? innerNext : outerNext;
                TriangleIndices[indexCursor++] = reverse ? outerNext : innerNext;
            }
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
        /// <returns>The outward miter vector, or zero when neither edge gave a direction.</returns>
        private static double2 Miter(double2 incoming, double2 outgoing)
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
