using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Fill;

namespace MapRenderer.Tests.EditMode.Jobs
{
    /// <summary>
    /// The outward boundary band, read at the job's own output — no rendering, so every property below is
    /// about the geometry and the attribute rather than about pixels.
    ///
    /// <para><b>Why a band quad's orientation cannot simply be read.</b> The quad is DEGENERATE in tile
    /// space: its outer vertices carry the same coordinate as their inner twins, because the one device
    /// pixel is added by the vertex shader. Every test here that talks about direction or winding therefore
    /// reconstructs the shader's displacement first — <c>tileOutward = (dirEast, -dirNorth)</c>, undoing the
    /// y-down flip the job applies at its write site.</para>
    ///
    /// <para><b>What a winding tooth cannot catch, deliberately.</b> A band mirrored north↔south is still
    /// consistently wound and passes <see cref="BandTriangles_CarryEarcutsCanonicalWinding_ForEitherRingSign"/>
    /// unchanged. That is
    /// why <see cref="NorthEdgeBand_PointsNorth_NotSouth"/> and
    /// <see cref="OuterRingPointsOutOfTheFill_AndAHoleRingPointsIntoTheHole"/> exist: a band on the wrong
    /// side IS a ramp placed inside the boundary, which is the one placement the mechanism forbids.</para>
    /// </summary>
    public class FillBandJobTests
    {
        /// <summary>Everything one <see cref="FillBandJob"/> run needs, allocated and disposed as a unit.</summary>
        private sealed class BandCase : System.IDisposable
        {
            public NativeList<double2> RingVertices;
            public NativeList<int>     RingOffsets;
            public NativeList<int>     RingFeatureIdx;
            public NativeArray<int>    PolyOuterRingIdx;
            public NativeArray<int>    PolyHoleListStart;
            public NativeArray<int>    PolyHoleCount;
            public NativeArray<int>    HoleRingIdxs;
            public NativeArray<int>    PolyCountArr;

            public NativeList<double2> TileVertices;
            public NativeList<float3> VertexBand;
            public NativeList<double3> VertexEast;
            public NativeList<double3> WorldPositions;
            public NativeList<double3> VertexUp;
            public NativeList<GeoCoordinate> Geo;
            public NativeList<int>     VertexFeatureIdx;
            public NativeList<int>     TriangleIndices;
            public NativeArray<FillGraphCounts> Counts;

            /// <summary>Vertex count of the fake interior this run appends after.</summary>
            public int InteriorVertexCount;

            public void Dispose()
            {
                RingVertices.Dispose(); RingOffsets.Dispose(); RingFeatureIdx.Dispose();
                PolyOuterRingIdx.Dispose(); PolyHoleListStart.Dispose(); PolyHoleCount.Dispose();
                HoleRingIdxs.Dispose(); PolyCountArr.Dispose();
                TileVertices.Dispose(); VertexBand.Dispose(); VertexEast.Dispose();
                WorldPositions.Dispose(); VertexUp.Dispose(); Geo.Dispose();
                VertexFeatureIdx.Dispose(); TriangleIndices.Dispose(); Counts.Dispose();
            }
        }

        /// <summary>Builds one polygon per entry of <paramref name="polygons"/>, each a ring list whose FIRST
        /// ring is the outer and whose rest are holes, plus a stand-in interior of one triangle per polygon so
        /// the job has something to interleave against.</summary>
        /// <param name="polygons">Per polygon: its rings, outer first. Rings are implicitly closed.</param>
        /// <param name="features">Per polygon: the feature index its rings carry.</param>
        /// <returns>An allocated case, ready to <c>Run</c>.</returns>
        private static BandCase Build(List<List<double2[]>> polygons, int[] features)
        {
            var c = new BandCase
            {
                RingVertices   = new NativeList<double2>(Allocator.Persistent),
                RingOffsets    = new NativeList<int>(Allocator.Persistent),
                RingFeatureIdx = new NativeList<int>(Allocator.Persistent),
                TileVertices   = new NativeList<double2>(Allocator.Persistent),
                VertexBand     = new NativeList<float3>(Allocator.Persistent),
                VertexEast     = new NativeList<double3>(Allocator.Persistent),
                WorldPositions = new NativeList<double3>(Allocator.Persistent),
                VertexUp       = new NativeList<double3>(Allocator.Persistent),
                Geo            = new NativeList<GeoCoordinate>(Allocator.Persistent),
                VertexFeatureIdx = new NativeList<int>(Allocator.Persistent),
                TriangleIndices  = new NativeList<int>(Allocator.Persistent),
                Counts = new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory),
            };

            int polyCount = polygons.Count;
            c.PolyOuterRingIdx  = new NativeArray<int>(polyCount, Allocator.Persistent);
            c.PolyHoleListStart = new NativeArray<int>(polyCount, Allocator.Persistent);
            c.PolyHoleCount     = new NativeArray<int>(polyCount, Allocator.Persistent);
            c.PolyCountArr      = new NativeArray<int>(1, Allocator.Persistent) { [0] = polyCount };

            var holeRingIdxs = new List<int>();
            c.RingOffsets.Add(0);
            for (int p = 0; p < polyCount; p++)
            {
                List<double2[]> rings = polygons[p];
                for (int r = 0; r < rings.Count; r++)
                {
                    int ringIdx = c.RingFeatureIdx.Length;
                    foreach (double2 v in rings[r]) c.RingVertices.Add(v);
                    c.RingOffsets.Add(c.RingVertices.Length);
                    c.RingFeatureIdx.Add(features[p]);
                    if (r == 0) c.PolyOuterRingIdx[p] = ringIdx; else holeRingIdxs.Add(ringIdx);
                }
                c.PolyHoleListStart[p] = holeRingIdxs.Count - (rings.Count - 1);
                c.PolyHoleCount[p]     = rings.Count - 1;
            }

            c.HoleRingIdxs = new NativeArray<int>(math.max(1, holeRingIdxs.Count), Allocator.Persistent);
            for (int i = 0; i < holeRingIdxs.Count; i++) c.HoleRingIdxs[i] = holeRingIdxs[i];

            // A stand-in interior: one degenerate triangle per polygon, carrying that polygon's feature, in
            // polygon order — exactly the shape AggregateJob hands over.
            for (int p = 0; p < polyCount; p++)
            {
                int b = c.TileVertices.Length;
                for (int k = 0; k < 3; k++)
                {
                    c.TileVertices.Add(polygons[p][0][k % polygons[p][0].Length]);
                    c.VertexBand.Add(float3.zero);
                    c.VertexEast.Add(new double3(1, 0, 0));
                    c.WorldPositions.Add(double3.zero);
                    c.VertexUp.Add(new double3(0, 1, 0));
                    c.Geo.Add(default);
                    c.VertexFeatureIdx.Add(features[p]);
                }
                c.TriangleIndices.Add(b); c.TriangleIndices.Add(b + 1); c.TriangleIndices.Add(b + 2);
            }
            c.InteriorVertexCount = c.TileVertices.Length;
            return c;
        }

        /// <summary>Runs the band node over a prepared case, with the clip window off unless one is given —
        /// which is the state every test that is not about clip-edge suppression wants.</summary>
        /// <param name="c">The case to run.</param>
        /// <param name="clipEnabled">Whether the rings reaching this node came through the window below.</param>
        /// <param name="clipMin">The window's inclusive minimum corner, tile units.</param>
        /// <param name="clipMax">Its inclusive maximum corner.</param>
        private static void Run(BandCase c, bool clipEnabled = false, double2 clipMin = default, double2 clipMax = default)
            => new FillBandJob
        {
            RingVertices = c.RingVertices.AsArray(), RingOffsets = c.RingOffsets.AsArray(),
            RingFeatureIdx = c.RingFeatureIdx.AsArray(),
            PolyOuterRingIdx = c.PolyOuterRingIdx, PolyHoleListStart = c.PolyHoleListStart,
            PolyHoleCount = c.PolyHoleCount, HoleRingIdxs = c.HoleRingIdxs, PolyCountArr = c.PolyCountArr,
            TileVertices = c.TileVertices, VertexBand = c.VertexBand, VertexFeatureIdx = c.VertexFeatureIdx,
            TriangleIndices = c.TriangleIndices, VertexEast = c.VertexEast,
            WorldPositions = c.WorldPositions, VertexUp = c.VertexUp, Geo = c.Geo,
            Counts = c.Counts,
            ClipEnabled = clipEnabled, ClipMin = clipMin, ClipMax = clipMax,
        }.Run();

        /// <summary>How many band triangles are anchored at each edge of the case's single ring. Both
        /// triangles of a quad start with that edge's INNER vertex, so a per-edge count of 2 means the quad
        /// was emitted and 0 means it was suppressed — which names the edge, where a bare index total would
        /// only say how many went missing.</summary>
        /// <param name="c">A run case whose polygons hold exactly one ring.</param>
        /// <param name="ringLength">That ring's vertex count.</param>
        /// <returns>Per edge index, the number of band triangles emitted for it.</returns>
        private static int[] BandTrianglesPerEdge(BandCase c, int ringLength)
        {
            var perEdge = new int[ringLength];
            for (int i = 0; i + 2 < c.TriangleIndices.Length; i += 3)
            {
                int anchor = c.TriangleIndices[i];
                if (anchor < c.InteriorVertexCount) continue;
                int local = anchor - c.InteriorVertexCount;
                if (local % 2 != 0) continue; // an outer vertex never anchors a quad
                perEdge[(local / 2) % ringLength]++;
            }
            return perEdge;
        }

        /// <summary>The outward direction in TILE coordinates, undoing the job's y-down flip.</summary>
        /// <param name="band">A band attribute, <c>(dirEast, dirNorth, side)</c>.</param>
        /// <returns>The tile-space outward vector, miter factor included in its magnitude.</returns>
        private static double2 TileOutward(double3 band) => new double2(band.x, -band.y);

        /// <summary>Shoelace signed area × 2 of a triangle, tile space.</summary>
        /// <param name="a">First vertex.</param>
        /// <param name="b">Second vertex.</param>
        /// <param name="c">Third vertex.</param>
        /// <returns>Twice the signed area; its sign is the winding.</returns>
        private static double SignedArea2(double2 a, double2 b, double2 c)
            => a.x * b.y - b.x * a.y + b.x * c.y - c.x * b.y + c.x * a.y - a.x * c.y;

        /// <summary>A square with an extra mid-edge vertex on the y = 0 side (tile NORTH), wound so its
        /// shoelace area is positive.</summary>
        private static double2[] NorthMidVertexSquare() => new[]
        {
            new double2(0, 0), new double2(2, 0), new double2(4, 0), new double2(4, 4), new double2(0, 4),
        };

        [Test]
        public void InnerBandVertices_AreTheRingVertexWithABitwiseZeroAttribute()
        {
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { NorthMidVertexSquare() } }, new[] { 0 });
            Run(c);

            double2[] ring = NorthMidVertexSquare();
            Assert.AreEqual(2 * ring.Length, c.Counts[0].BandVertexCount,
                "two band vertices per ring vertex — the count the sizing tooth downstream also reads.");

            for (int i = 0; i < ring.Length; i++)
            {
                int inner = c.InteriorVertexCount + 2 * i;
                int outer = inner + 1;

                Assert.AreEqual(ring[i].x, c.TileVertices[inner].x,
                    "an inner band vertex must sit at the ring vertex BITWISE — nothing is ever displaced inward, " +
                    "and a coincident-but-not-identical edge would put a hairline along every polygon boundary.");
                Assert.AreEqual(ring[i].y, c.TileVertices[inner].y);
                Assert.AreEqual(0.0, c.VertexBand[inner].x); Assert.AreEqual(0.0, c.VertexBand[inner].y);
                Assert.AreEqual(0.0, c.VertexBand[inner].z,
                    "side must be exactly 0 on the inner ring, or its coverage stops reading 1 and the fill's " +
                    "own interior loses ink.");

                Assert.AreEqual(ring[i].x, c.TileVertices[outer].x,
                    "an outer band vertex carries the SAME coordinate — the pixel is added in the vertex shader.");
                Assert.AreEqual(ring[i].y, c.TileVertices[outer].y);
                Assert.AreEqual(1.0, c.VertexBand[outer].z);
                Assert.AreEqual(new double3(1, 0, 0), c.VertexEast[outer],
                    "the band's own tangent slots must be filled — AggregateJob only ever sized the interior, " +
                    "so an unwritten slot is uninitialised memory reaching the shader as the surface frame.");
            }
        }

        [Test]
        public void NorthEdgeBand_PointsNorth_NotSouth()
        {
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { NorthMidVertexSquare() } }, new[] { 0 });
            Run(c);

            // Ring vertex 1 is (2,0): both its edges run along +x, so its miter is the edge normal exactly.
            // Tile +y is SOUTH, so the outward normal of the y = 0 edge is tile -y, which is NORTH — and the
            // attribute is consumed in a frame whose north is cross(east, up). dirNorth must therefore be +1.
            float3 band = c.VertexBand[c.InteriorVertexCount + 2 * 1 + 1];
            Assert.AreEqual(0.0, band.x, 1e-12, "a due-north band has no east component");
            Assert.AreEqual(1.0, band.y, 1e-12,
                "dirNorth must be +1 on the tile's y = 0 edge. Reading -1 means the tile y-down flip was not " +
                "applied and every band in the product is mirrored — a defect no winding tooth can see.");
        }

        [Test]
        public void OuterRingPointsOutOfTheFill_AndAHoleRingPointsIntoTheHole()
        {
            var outer = new[] { new double2(0, 0), new double2(8, 0), new double2(8, 8), new double2(0, 8) };
            // Opposite winding, as RingAssemblyJob requires of a hole, with a mid-edge vertex at (2,4).
            var hole = new[]
            {
                new double2(2, 2), new double2(2, 4), new double2(2, 6), new double2(6, 6), new double2(6, 2),
            };
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { outer, hole } }, new[] { 0 });
            Run(c);

            var centre = new double2(4, 4);
            int outerBase = c.InteriorVertexCount;
            for (int i = 0; i < outer.Length; i++)
            {
                double2 direction = TileOutward(c.VertexBand[outerBase + 2 * i + 1]);
                Assert.Greater(math.dot(direction, outer[i] - centre), 0.0,
                    $"outer-ring vertex {i}'s band must grow AWAY from the polygon's interior.");
            }

            int holeBase = outerBase + 2 * outer.Length;
            for (int i = 0; i < hole.Length; i++)
            {
                double2 direction = TileOutward(c.VertexBand[holeBase + 2 * i + 1]);
                Assert.Greater(math.dot(direction, centre - hole[i]), 0.0,
                    $"hole-ring vertex {i}'s band must grow INTO the hole. Pointing the other way puts the ramp " +
                    "inside the filled region, which is the one placement the mechanism forbids.");
            }
        }

        // Earcut normalises every outer ring to CCW-on-screen (area2 < 0 in this Y-down space) before it
        // triangulates, so the interior's winding is the SAME whichever way the source wound its rings — and
        // MVT (positive) and GeoJSON (negative) do not agree on that. The band must land on earcut's
        // orientation for BOTH, or it is back-face culled against the interior it borders for one of them and
        // the fill renders exactly as it did before this stage. Both signs are driven here for that reason.
        //
        // RED: drop the reversal in FillBandJob.EmitRing and the positively-wound arm reds while the
        // negatively-wound one stays green — which is precisely the asymmetry that hid the defect.
        [Test]
        public void BandTriangles_CarryEarcutsCanonicalWinding_ForEitherRingSign([Values(false, true)] bool reversedRing)
        {
            double2[] ring = NorthMidVertexSquare();
            if (reversedRing) System.Array.Reverse(ring);
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { ring } }, new[] { 0 });
            Run(c);

            const double displacement = 0.01; // stands in for the shader's one device pixel
            double2 Position(int index)
            {
                float3 band = c.VertexBand[index];
                return c.TileVertices[index] + TileOutward(band) * (displacement * band.z);
            }

            int bandTriangles = 0;
            for (int i = 0; i + 2 < c.TriangleIndices.Length; i += 3)
            {
                if (c.TriangleIndices[i] < c.InteriorVertexCount) continue;
                double area = SignedArea2(
                    Position(c.TriangleIndices[i]), Position(c.TriangleIndices[i + 1]), Position(c.TriangleIndices[i + 2]));
                Assert.Less(area, 0.0,
                    "a band triangle must carry earcut's canonical CCW-on-screen orientation (area2 < 0 in " +
                    "this Y-down space) whichever way the ring was wound, or it is back-face culled against " +
                    "the interior it borders and the whole band silently disappears.");
                bandTriangles++;
            }
            Assert.AreEqual(2 * ring.Length, bandTriangles,
                "two triangles per ring edge — a count that reconciles with the ring, not merely a non-zero one.");
        }

        [Test]
        public void BandTrianglesFollowTheirOwnFeaturesInteriorTriangles()
        {
            var first  = new[] { new double2(0, 0), new double2(4, 0), new double2(4, 4) };
            var second = new[] { new double2(10, 0), new double2(14, 0), new double2(14, 4) };
            using BandCase c = Build(
                new List<List<double2[]>>
                {
                    new List<double2[]> { first },
                    new List<double2[]> { second },
                },
                new[] { 0, 1 });
            Run(c);

            // Read the feature of every triangle in emission order. Feature 1's interior must never appear
            // before feature 0's band: these fills are painter-ordered, so a whole-layer band appended last
            // would draw each feature's band over every other feature's interior regardless of fill-sort-key.
            var order = new List<(int Feature, bool IsBand)>();
            for (int i = 0; i + 2 < c.TriangleIndices.Length; i += 3)
            {
                int v = c.TriangleIndices[i];
                order.Add((c.VertexFeatureIdx[v], v >= c.InteriorVertexCount));
            }

            int firstFeatureOne = order.FindIndex(e => e.Feature == 1);
            int lastFeatureZero = order.FindLastIndex(e => e.Feature == 0);
            Assert.Greater(firstFeatureOne, lastFeatureZero,
                "every triangle of feature 0 — interior AND band — must precede every triangle of feature 1.");
            Assert.IsTrue(order[lastFeatureZero].IsBand,
                "feature 0's last triangle must be one of its own band's.");
        }

        [Test]
        public void AMiterKeepsThePerpendicularWidthThroughARightAngle()
        {
            var square = new[] { new double2(0, 0), new double2(4, 0), new double2(4, 4), new double2(0, 4) };
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { square } }, new[] { 0 });
            Run(c);

            for (int i = 0; i < square.Length; i++)
            {
                double2 direction = TileOutward(c.VertexBand[c.InteriorVertexCount + 2 * i + 1]);
                Assert.AreEqual(math.sqrt(2.0), math.length(direction), 1e-12,
                    $"corner {i} turns 90°, so the miter factor is 1/cos(45°) = √2. A unit-length direction " +
                    "here would pinch the band to 1/√2 px measured perpendicular to each edge.");
            }
        }

        // ── Clip-edge suppression ─────────────────────────────────────────────────────────────────────
        //
        // At the shipped FillTileBufferClip: 0, RingClipJob cuts every fill ring exactly at the tile boundary
        // and the neighbouring tile carries the mirrored cut — so a band drawn along a clip edge paints a rim
        // over a fill that already abuts there. The predicate is exact rather than epsilon-based because
        // RingClipJob.Intersect writes the boundary value VERBATIM into the clipped axis.
        //
        // What the predicate actually tests is "both endpoints on the same window line", which is a superset
        // of "clip-introduced": a genuine feature edge that happens to run exactly along the tile boundary is
        // suppressed too. That population is measure-zero, and it abuts the neighbour's own boundary in the
        // same way, so it wants the same treatment.

        /// <summary>A ring already cut at the east window line: the shape RingClipJob hands over for a
        /// feature that overran the tile. Edge 1 is the clip-introduced one; edges 0 and 2 each have exactly
        /// ONE endpoint on the window, which is what separates the shipped predicate from the "either
        /// endpoint" relaxation.</summary>
        private static double2[] EastClippedRing() => new[]
        {
            new double2(3000, 1000), new double2(4096, 1000), new double2(4096, 3000), new double2(3000, 3000),
        };

        [Test]
        public void AClipIntroducedEdgeGetsNoBand_WhileItsNeighboursWithOneEndpointOnTheWindowKeepTheirs()
        {
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { EastClippedRing() } }, new[] { 0 });
            Run(c, clipEnabled: true, clipMin: new double2(0, 0), clipMax: new double2(4096, 4096));

            int[] perEdge = BandTrianglesPerEdge(c, EastClippedRing().Length);

            Assert.AreEqual(0, perEdge[1],
                "edge 1 runs (4096,1000)->(4096,3000): both endpoints on the east window line, so it is the " +
                "tile cut the neighbour mirrors. A band there paints a rim over a fill that already abuts.");
            Assert.AreEqual(2, perEdge[0],
                "edge 0 runs (3000,1000)->(4096,1000): a REAL feature edge with one endpoint on the window. " +
                "Suppressing it would strip the band off every polygon that merely reaches the tile edge.");
            Assert.AreEqual(2, perEdge[2], "edge 2 runs (4096,3000)->(3000,3000) — the same case as edge 0.");
            Assert.AreEqual(2, perEdge[3], "edge 3 touches no window line at all.");

            Assert.AreEqual(3 * 6, c.Counts[0].BandIndexCount,
                "three of the ring's four edges keep their quad, so the band index count must drop with them " +
                "— it is no longer 6 x ring length.");
            Assert.AreEqual(2 * EastClippedRing().Length, c.Counts[0].BandVertexCount,
                "the VERTEX count is unchanged: both band vertices are still written for every ring vertex, " +
                "deliberately. See FillBandJob's own note on why suppression drops quads and not vertices.");
        }

        [Test]
        public void WithNoClipWindow_NothingIsSuppressed_EvenOnEdgesLyingOnTheDefaultWindowValue()
        {
            double2[] ring = NorthMidVertexSquare();
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { ring } }, new[] { 0 });
            Run(c); // clipEnabled: false — RingSelectJob's arm, where there is no window at all

            int[] perEdge = BandTrianglesPerEdge(c, ring.Length);
            for (int i = 0; i < ring.Length; i++)
                Assert.AreEqual(2, perEdge[i],
                    $"edge {i} lost its band with clipping OFF. Three of this ring's edges lie on x = 0 or " +
                    "y = 0, which is what a DEFAULT double2 window reads as — so a predicate that runs " +
                    "without checking the flag suppresses them against a window that was never computed.");

            Assert.AreEqual(6 * ring.Length, c.Counts[0].BandIndexCount,
                "with no window, every edge keeps its quad.");
        }

        [Test]
        public void ARepeatedRingVertexProducesNoNaN()
        {
            var spike = new[]
            {
                new double2(0, 0), new double2(4, 0), new double2(4, 0), new double2(4, 4), new double2(0, 4),
            };
            using BandCase c = Build(
                new List<List<double2[]>> { new List<double2[]> { spike } }, new[] { 0 });
            Run(c);

            for (int i = c.InteriorVertexCount; i < c.VertexBand.Length; i++)
                Assert.IsFalse(math.any(math.isnan(c.VertexBand[i])),
                    $"band vertex {i} is NaN — a zero-length edge must degrade to no miter, not to a normalize(0).");
        }
    }
}
