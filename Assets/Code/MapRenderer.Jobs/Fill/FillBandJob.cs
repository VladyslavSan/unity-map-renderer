using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's boundary-band node: appends, after <see cref="AggregateJob"/>'s interior geometry, one
    /// quad per assembled ring edge carrying the attribute the fill shaders ramp coverage across
    /// (<c>Fill_BandCoverage.hlsl</c>). Two vertices per ring vertex, six indices per ring edge, into the
    /// SAME vertex columns and the SAME single index buffer — the band is not a second submesh and earcut
    /// never sees it.
    ///
    /// <para><b>Nothing is ever displaced inward, and that is the mechanism.</b> Both of a ring vertex's band
    /// vertices are written at the ring vertex's own tile coordinate, bit-identically; the inner one carries
    /// <c>(0,0,0)</c> and the outer one carries <c>(dirEast, dirNorth, 1)</c>, which the vertex shader turns
    /// into a one-device-pixel outward displacement. So the band lies strictly OUTSIDE the boundary, coverage
    /// still reads 1 everywhere the hard fill already was, and two abutting fills still leave zero background
    /// weight. A ramp placed even partly inside would destroy that at every shared edge and tile seam.</para>
    ///
    /// <para><b>Outward is derived from the polygon's own winding, never assumed.</b> MVT's ring-winding
    /// convention is enforced nowhere in this repo — <see cref="RingAssemblyJob"/> derives an exterior sign
    /// per feature instead. The outward normal of an edge running <c>d</c> is
    /// <c>sign(area2(outer)) * (d.y, -d.x)</c>, and that ONE sign serves the polygon's holes too: a hole is
    /// accepted only when its own signed area has the opposite sign, so "away from the fill" for a hole (into
    /// the void) is the same expression with the same sign — no outer/hole branch, and no ring is reversed.</para>
    ///
    /// <para><b>Tile space is Y-DOWN</b> (<c>TileToGeoJob.GeoAt</c>: <c>+y</c> grows southward), while the
    /// attribute is consumed in the mesh's own surface frame, whose <c>north = cross(east, up)</c>. The sign
    /// flip therefore happens exactly once, here at the write site: <c>dirEast = miter.x</c>,
    /// <c>dirNorth = -miter.y</c>. Baked straight through, every band mirrors north↔south — and a mirrored
    /// band is still consistently wound, so only the ramp-placement teeth see it.</para>
    ///
    /// <para><b>Index order interleaves per feature</b>: a feature's band triangles follow that feature's own
    /// interior triangles, never the whole layer's. These fills are <c>ZWrite Off</c> and painter-ordered, so
    /// appending every band last would put each feature's band over every other feature's interior
    /// irrespective of <c>fill-sort-key</c>. Interior triangles arrive from <see cref="AggregateJob"/> in
    /// polygon — therefore feature — order, and a triangle's feature is read straight off
    /// <see cref="VertexFeatureIdx"/>, so the rebuild needs no per-polygon index table.</para>
    ///
    /// <para><b>Output winding is CANONICAL, never the ring's own.</b> <see cref="EarcutJob"/> normalises
    /// every outer ring to CCW-on-screen (<c>area2 &lt; 0</c> in this Y-down space) BEFORE triangulating
    /// (<c>EarcutJob.cs:142-147</c>), so the interior's winding does not depend on how the source wound its
    /// rings — and MVT and GeoJSON do not agree on that. A band that inherited the ring's sign would be
    /// counter-wound against the interior it borders for one of the two conventions, and back-face culled
    /// wherever culling is on: the fill would look exactly as it did before this stage. The quad's index
    /// order is therefore reversed for a positively-wound ring. Reversed once more at the mesh-write
    /// boundary (<c>StyledFillTileBuilder.WriteJob</c>), with everything else.</para>
    ///
    /// <para>A band quad is DEGENERATE in tile space — its outer vertices share their inner twin's coordinate
    /// — so a winding tooth must reconstruct the shader's displacement before it can read an orientation at
    /// all. The same degeneracy is what lets this node run on the CURVED arm too, upstream of
    /// <see cref="GlobeFillSubdivideJob{TProj}"/>: a band quad's long edges have the same two endpoints as
    /// the interior boundary edge they abut, so both compute the identical subdivision mark and split
    /// conformingly, while the zero-length radial edges subtend no angle and are never marked. The quad's
    /// diagonal (<c>innerHere</c>→<c>outerNext</c>, the two triangles' shared edge) is neither long nor
    /// radial, but its endpoints are that same tile-space pair, so it takes the identical mark and midpoint
    /// — a midpoint whose <c>side</c> lerps to 0.5, which is why the displacement must not be scaled by
    /// it.</para>
    ///
    /// <para><b>The band stops at a tile cut.</b> With clipping on (the shipped
    /// <c>FillTileBufferClip: 0</c> cuts exactly at the tile boundary), a ring edge whose BOTH endpoints lie
    /// on the same window line gets no quad: the neighbouring tile carries the mirrored cut and its fill
    /// abuts exactly there, so a band drawn along that edge is ink laid over a fill that is already
    /// present — an <c>f(1-f)</c> rim, and at the shipped clip every tile seam is such a pair. The equality
    /// is exact rather than epsilon-based because <see cref="RingClipJob"/><c>.Intersect</c> writes the
    /// boundary value verbatim into the clipped axis. Note what the predicate actually says: "both endpoints
    /// on one window line" is a SUPERSET of "clip-introduced" — a genuine feature edge running exactly along
    /// the tile boundary is suppressed too, which is measure-zero and abuts its neighbour in the same way.
    /// The <see cref="ClipEnabled"/> flag is what keeps the unclipped arm out of this entirely.</para>
    ///
    /// <para>Suppression drops the QUAD, never the vertex pair: both band vertices are written for every ring
    /// vertex regardless, so <c>first + 2 * i</c> stays the index of ring vertex <c>i</c>'s pair and
    /// <see cref="Counts"/>' band VERTEX count stays <c>2 x</c> the ring total (the flat arm's interior/band
    /// prefix split reads it). A pair left unreferenced costs a few vertices on the flat arm and nothing at
    /// all on the curved one, where <see cref="GlobeFillSubdivideJob{TProj}"/> emits per triangle. The band
    /// INDEX count is the one that varies, so it is taken from the write cursor rather than computed.</para>
    ///
    /// <para>The two halves of the attribute are INDEPENDENT: <c>(dirEast, dirNorth)</c> is the displacement
    /// in device pixels (miter factor in its magnitude) and <c>side</c> is only the coverage coordinate. The
    /// vertex shader never multiplies one by the other, because subdivision lerps both across a split band
    /// edge and the product would be quadratic in the split parameter.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct FillBandJob : IJob
    {
        /// <summary>Miter factor ceiling. A ring vertex whose two edges meet at a sharp angle would otherwise
        /// throw the outer vertex arbitrarily far out; clamped, a very sharp corner loses a sub-pixel wedge of
        /// band instead. This is a join-geometry limit in the shape <c>RibbonJob.MiterLimit</c> already uses,
        /// NOT a tunable band width — the band is 1 device pixel and that is not a knob.
        /// <para><c>internal</c> so a rendered tooth can DERIVE its reach bound from this number rather than
        /// pick one: band ink lies at most this many device pixels from the geometry it belongs to. See
        /// <c>GlobeFillBandRenderTests.TheGlobeFillsSilhouetteGainsInkOutward_WithinTheMiterLimit</c>.</para></summary>
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

        /// <summary>Written for the band's own slots with the flat constant <c>(1,0,0)</c>
        /// <see cref="AggregateJob"/> writes for the interior — the ONLY column here that is not filled by a
        /// later node ON THE FLAT ARM. <c>WorldPositions</c>/<c>VertexUp</c>/<c>Geo</c> are merely re-sized:
        /// the tile→geo and projection nodes run after this one over the deferred length and cover the band
        /// with the interior, which is what makes a band vertex's world position bit-identical to the ring
        /// vertex it duplicates. The curved arm reads none of these four — <see cref="GlobeFillScatterJob"/>
        /// replaces them with the subdivider's own per-vertex frame.</summary>
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
            // early-return (job-scheduling-design.md §7 rule 2): that path leaves every triangulation column
            // at length 0, so AggregateJob emits no vertices, and PolyCountArr[0] — a borrowed count that
            // early return never shrinks — must not be allowed to drive a loop here.
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

            // (inner_i, outer_i, outer_j) / (inner_i, outer_j, inner_j) winds with the ring's OWN sign.
            // Earcut's output does not — it normalises to CCW-on-screen — so a positively-wound ring's band
            // is emitted reversed to land on the same orientation as the interior it borders.
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
