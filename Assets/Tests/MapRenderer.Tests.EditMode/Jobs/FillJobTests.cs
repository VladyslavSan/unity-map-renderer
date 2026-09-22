// Jobs/FillJobTests.cs — the fill boundary-band job and the tile geometry buffer/materializer/producer teeth (EditMode).
//
// Two namespace blocks, kept exactly as each file already declared them (MapRenderer.Tests.EditMode.Jobs
// for FillBandJobTests, MapRenderer.Tests.Jobs for the other four) — unifying them would change every
// test's fullname, which a file merge must never do. FillGraphBurstProbeTests.cs and
// GraphDeterminismTests.cs both stay their own files (Burst schedule probe; size).
//
// Contents:
//   FillBandJobTests                   — the outward boundary band at FillBandJob's own output — orientation, winding, no rendering.
//   RingClipJobTests                   — RingClipJob's tile-space clip arithmetic.
//   TileGeometryBuffersTests           — the ownership teeth for TileGeometryBuffers, which now owns the ring-stage buffers FillMeshPipeline.Schedule used to hold as private locals.
//   TileGeometryMaterializerSeamTests  — the teeth on Waist 1's producer seam as seen from FillMeshGraph.Schedule.
//   WaistOneProducerAgreementTests     — Waist 1's two producers agree on the feature count as a property of the type, not of one writer.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Fill;
using System.IO;
using UnityEngine;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.GeoJson;
using MapRenderer.Jobs.Tiles;
using System;


namespace MapRenderer.Tests.EditMode.Jobs
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // FillBandJobTests — the outward boundary band at FillBandJob's own output
    // ───────────────────────────────────────────────────────────────────────────────────

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

namespace MapRenderer.Tests.Jobs
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // RingClipJobTests — RingClipJob's tile-space clip arithmetic
    // ───────────────────────────────────────────────────────────────────────────────────

    // ───────────────────────────────────────────────────────────────────────────────────
    // RingClipJobTests — RingClipJob's tile-space clip arithmetic
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class RingClipJobTests
    {
        private const double Extent = 4096.0;

        // The exact buffered rectangle every real fixture produces: [-64,-64]..[4160,4160], the 64-unit
        // OpenMapTiles buffer. Wound CCW in tile space (positive shoelace) — the canonical convention.
        private static readonly double2[] BufferedRect =
        {
            new double2(  -64.0,   -64.0),
            new double2( 4160.0,   -64.0),
            new double2( 4160.0,  4160.0),
            new double2(  -64.0,  4160.0),
        };

        // ── T1: the clip actually cuts, and only what it should ───────────────────────────────────

        [Test]
        public void Clip_AtZeroMargin_CutsTheBufferedRectToTheTileSquare()
        {
            var result = RunClip(new[] { BufferedRect }, TileBufferClip.KeepTileUnits(0.0));
            try
            {
                Assert.AreEqual(1, result.RingCount, "the straddling ring must survive as exactly one ring.");
                double2[] outRing = result.Ring(0);

                Assert.AreEqual(4, outRing.Length,
                    "clipping the buffered rect to [0,4096] must give exactly the 4 tile corners. 5+ means an " +
                    "on-boundary vertex was emitted twice (or the boundary test is exclusive); fewer means a " +
                    "half-plane collapsed the ring.\n  got: " + Describe(outRing));

                AssertCyclicallyEqual(
                    new[]
                    {
                        new double2(   0.0,    0.0),
                        new double2(4096.0,    0.0),
                        new double2(4096.0, 4096.0),
                        new double2(   0.0, 4096.0),
                    },
                    outRing);

                // Winding: Sutherland–Hodgman is orientation-preserving. A flip here silently breaks earcut,
                // the parity oracles and stock Cull Back.
                Assert.Greater(Shoelace2(BufferedRect), 0.0, "fixture sanity: the input ring is CCW.");
                Assert.Greater(Shoelace2(outRing), 0.0,
                    "the clipped ring must keep the input's CCW-in-tile-space winding.");
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Clip_LeavesAnInteriorRingBitIdentical()
        {
            // Deliberately NOT axis-aligned or round: a fast path that "reconstructed" the ring rather than
            // copying it would drift on these.
            var interior = new[]
            {
                new double2( 100.0,  100.0),
                new double2(3000.0,  137.5),
                new double2(2871.3, 2999.0),
                new double2( 512.25, 1024.125),
            };

            var result = RunClip(new[] { interior }, TileBufferClip.KeepTileUnits(0.0));
            try
            {
                Assert.AreEqual(1, result.RingCount);
                double2[] outRing = result.Ring(0);
                Assert.AreEqual(interior.Length, outRing.Length);
                for (int i = 0; i < interior.Length; i++)
                {
                    Assert.AreEqual(interior[i].x, outRing[i].x, 0.0, $"vertex {i}.x must be BIT-identical.");
                    Assert.AreEqual(interior[i].y, outRing[i].y, 0.0, $"vertex {i}.y must be BIT-identical.");
                }
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Clip_DropsARingThatLiesWhollyOutsideTheWindow()
        {
            var outside = new[]
            {
                new double2(4100.0, 4100.0),
                new double2(4150.0, 4100.0),
                new double2(4150.0, 4150.0),
            };

            var result = RunClip(new[] { outside }, TileBufferClip.KeepTileUnits(0.0));
            try
            {
                Assert.AreEqual(0, result.RingCount,
                    "a ring wholly outside the window must be dropped, never emitted as a degenerate sliver.");
                Assert.AreEqual(0, result.VertexCount);
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Clip_AtTheStandardBufferMargin_LeavesTheBufferedRectUntouched()
        {
            // The negative control for the whole stage: at b = 64 the standard buffer is exactly the window,
            // so today's geometry survives verbatim. If THIS cuts, every "byte-identical at b >= 8" claim is false.
            var result = RunClip(new[] { BufferedRect }, TileBufferClip.KeepTileUnits(64.0));
            try
            {
                Assert.AreEqual(1, result.RingCount);
                double2[] outRing = result.Ring(0);
                Assert.AreEqual(BufferedRect.Length, outRing.Length);
                for (int i = 0; i < BufferedRect.Length; i++)
                {
                    Assert.AreEqual(BufferedRect[i].x, outRing[i].x, 0.0);
                    Assert.AreEqual(BufferedRect[i].y, outRing[i].y, 0.0);
                }
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Clip_AtZeroMargin_LeavesTheFullExtentBackgroundRingUntouched()
        {
            // The plan calls A6NonMvtDecoderTests + TileBackgroundQuadProjectionTests "free falsifiers" for
            // this claim, but BOTH build a TileLayerProcessContext with BufferClip UNSET — i.e. disabled — so
            // neither is armed once the default margin is enabled. This is the armed version: the synthetic
            // full-extent ring sits exactly ON the b=0 window and must come out as the same 4 corners.
            //
            // What it does NOT test, so nobody credits it with more than it carries: this ring's bbox EQUALS
            // the window, so BboxInsideWindow short-circuits and no half-plane arithmetic runs. It cannot
            // catch a duplicated on-boundary vertex or an exclusive-vs-inclusive slip — those live in
            // Clip_DoesNotDuplicateAVertexLyingExactlyOnTheWindowEdge, whose ring straddles the boundary.
            // What this pins is the end-to-end claim the background quad depends on: at b = 0 the full-extent
            // ring survives untouched, by whichever path.
            var fullExtent = new[]
            {
                new double2(   0.0,    0.0),
                new double2(4096.0,    0.0),
                new double2(4096.0, 4096.0),
                new double2(   0.0, 4096.0),
            };

            var result = RunClip(new[] { fullExtent }, TileBufferClip.KeepTileUnits(0.0));
            try
            {
                Assert.AreEqual(1, result.RingCount);
                double2[] outRing = result.Ring(0);
                Assert.AreEqual(4, outRing.Length,
                    "the background quad must stay 4 vertices. 5–8 means an on-boundary point was duplicated " +
                    "or the boundary test is exclusive.\n  got: " + Describe(outRing));
                for (int i = 0; i < fullExtent.Length; i++)
                {
                    Assert.AreEqual(fullExtent[i].x, outRing[i].x, 0.0);
                    Assert.AreEqual(fullExtent[i].y, outRing[i].y, 0.0);
                }
            }
            finally { result.Dispose(); }
        }

        [Test]
        public void Clip_DoesNotDuplicateAVertexLyingExactlyOnTheWindowEdge()
        {
            // The duplicate hazard, isolated. `onEdge` sits exactly on x = 4096 with an INSIDE predecessor and
            // an OUTSIDE successor: textbook Sutherland–Hodgman emits it once as an inside vertex and then
            // AGAIN as the exit intersection (t = 0). The bbox exceeds the window, so the fast path cannot
            // mask it — this ring really does go through the arithmetic.
            var ring = new[]
            {
                new double2(2048.0, 2048.0), // inside
                new double2(4096.0, 2048.0), // exactly on the window edge
                new double2(5000.0, 3000.0), // outside
                new double2(2048.0, 4000.0), // inside
            };

            var result = RunClip(new[] { ring }, TileBufferClip.KeepTileUnits(0.0));
            try
            {
                Assert.AreEqual(1, result.RingCount);
                double2[] outRing = result.Ring(0);
                Assert.AreEqual(4, outRing.Length,
                    "the on-edge vertex must appear ONCE. 5 vertices means it was emitted both as an inside " +
                    "vertex and as the zero-length exit intersection.\n  got: " + Describe(outRing));

                for (int i = 0; i < outRing.Length; i++)
                    Assert.IsFalse(outRing[i].Equals(outRing[(i + 1) % outRing.Length]),
                        $"vertices {i} and {(i + 1) % outRing.Length} are identical — a zero-length edge.\n" +
                        "  got: " + Describe(outRing));

                Assert.Greater(Shoelace2(ring), 0.0, "fixture sanity: the input ring is CCW.");
                Assert.Greater(Shoelace2(outRing), 0.0, "winding must survive the clip.");
            }
            finally { result.Dispose(); }
        }

        // ── T1e: the RING INDIRECTION itself ──────────────────────────────────────────────────────

        /// <summary>
        /// The job reads <b>the rings <c>RingVisitOrder</c> names, in the order it names them</b> — not
        /// <c>0..RingVisitOrder.Length</c>. A sparse, permuted order (<c>[2, 0]</c> over three rings) must
        /// produce ring 2's geometry first, then ring 0's, each carrying <b>its own</b> feature index, and
        /// ring 1 must not appear at all.
        ///
        /// <para><b>Why it exists — this loop was unobserved on the branch that actually runs.</b> The shared buffer replaced
        /// <c>for (ri = 0; ri &lt; RingCount; ri++)</c> with <c>for (k…) { int ri = RingVisitOrder[k]; … }</c>.
        /// Every other clip fixture in the repo — the rest of this file, <c>RingWindowClipperParityTests</c>,
        /// <c>Jobs/TileGeometryMaterializerSeamTests</c>, <c>Jobs/TileGeometryStoreTests</c>,
        /// <c>Visual/TileSeamSnapshotTests</c> — supplies an <b>identity</b> visit order, under which
        /// <c>ri == k</c> is true by construction, and the one fixture with a genuinely permuted, subsetted
        /// order (<c>Meshing/FillSharedBufferTests</c>) ran only with the clip DISABLED, i.e. down the
        /// <c>RingSelectJob</c> branch. Collapsing the indirection back to <c>int ri = k;</c> was therefore
        /// inert against the whole gate — while <c>MapViewConfig.FillTileBufferClip = 0.0</c> makes the clip
        /// branch the PRODUCTION path for every fill layer.</para>
        ///
        /// <para>Both output branches are exercised on purpose: ring 2 straddles the window (so the visited
        /// index feeds Sutherland–Hodgman via <c>rStart</c>) and ring 0 lies wholly inside it (so the visited
        /// index feeds the verbatim bbox fast path).</para>
        /// </summary>
        [Test]
        public void Clip_VisitsTheRingsTheVisitOrderNames_InThatOrder_WithTheirOwnFeatureIndices()
        {
            // Ring 0 — wholly inside, deliberately irregular so a "reconstructed" copy would drift.
            var interior = new[]
            {
                new double2(100.0, 100.0),
                new double2(300.0, 120.0),
                new double2(280.0, 340.0),
                new double2(120.0, 320.0),
            };
            // Ring 1 — the DECOY. It is never named by the visit order, so not one of its vertices may appear.
            var decoy = new[]
            {
                new double2(1000.0, 1000.0),
                new double2(1200.0, 1000.0),
                new double2(1200.0, 1200.0),
                new double2(1000.0, 1200.0),
            };
            // Ring 2 — straddles x = 4096, so it goes through the half-plane arithmetic rather than the copy.
            var straddling = new[]
            {
                new double2(4000.0, 2000.0),
                new double2(4200.0, 2000.0),
                new double2(4200.0, 2200.0),
                new double2(4000.0, 2200.0),
            };

            var result = RunClip(
                new[] { interior, decoy, straddling },
                TileBufferClip.KeepTileUnits(0.0),
                visitOrder:     new[] { 2, 0 },
                ringFeatureIdx: new[] { 7, 8, 9 }); // distinguishable, and NOT equal to the ring index
            try
            {
                Assert.AreEqual(2, result.RingCount,
                    "the visit order names two rings, so exactly two survive — three means the loop ignored " +
                    "the order's LENGTH and walked the buffer.");

                // Slot 0 = ring 2, clipped to the window. Under `int ri = k;` this slot would hold ring 0
                // (the interior quad) instead — different vertices, different count, different feature.
                double2[] first = result.Ring(0);
                Assert.AreEqual(4, first.Length,
                    "visit slot 0 must be ring 2 clipped to x <= 4096.\n  got: " + Describe(first));
                AssertCyclicallyEqual(
                    new[]
                    {
                        new double2(4000.0, 2000.0),
                        new double2(4096.0, 2000.0),
                        new double2(4096.0, 2200.0),
                        new double2(4000.0, 2200.0),
                    },
                    first);
                Assert.AreEqual(9, result.RingFeatureIdx[0],
                    "the feature index must be read at the VISITED ring index (2 ⇒ feature 9), not at the " +
                    "visit slot (0 ⇒ feature 7). A slot-indexed read paints every ring with a neighbour's " +
                    "per-feature data.");

                // Slot 1 = ring 0, verbatim (bbox fast path). Bit-identical, in input order.
                double2[] second = result.Ring(1);
                Assert.AreEqual(interior.Length, second.Length,
                    "visit slot 1 must be ring 0, copied verbatim.\n  got: " + Describe(second));
                for (int i = 0; i < interior.Length; i++)
                {
                    Assert.AreEqual(interior[i].x, second[i].x, 0.0, $"vertex {i}.x must be BIT-identical.");
                    Assert.AreEqual(interior[i].y, second[i].y, 0.0, $"vertex {i}.y must be BIT-identical.");
                }
                Assert.AreEqual(7, result.RingFeatureIdx[1], "ring 0 carries feature 7.");

                // The decoy is absent — the visit order SUBSETS the buffer, it does not merely reorder it.
                for (int i = 0; i < result.VertexCount; i++)
                    foreach (double2 d in decoy)
                        Assert.IsFalse(result.Vertices[i].Equals(d),
                            $"output vertex {i} is {result.Vertices[i]}, which belongs to ring 1 — a ring the " +
                            "visit order never names.");
            }
            finally { result.Dispose(); }
        }

        // ── T2: holes and the exterior-sign invariant ─────────────────────────────────────────────

        [Test]
        public void Clip_DropsAHoleThatFallsOutsideTheWindow_WithoutPromotingItToAnOuterRing()
        {
            // One feature: a CCW outer straddling the boundary, plus a CW hole sitting entirely in the buffer
            // corner — inside the outer (so assembly WOULD accept it), outside the [0,4096] window.
            var hole = new[]
            {
                new double2(4100.0, 4100.0),
                new double2(4100.0, 4150.0),
                new double2(4150.0, 4150.0),
                new double2(4150.0, 4100.0),
            };
            Assert.Less(Shoelace2(hole), 0.0, "fixture sanity: the hole must wind opposite to the outer.");

            // Arm 1 (the non-vacuity control): unclipped, the hole IS accepted as a hole.
            var unclipped = RunClipThenAssemble(new[] { BufferedRect, hole }, TileBufferClip.Disabled);
            Assert.AreEqual(1, unclipped.PolygonCount, "unclipped: one polygon.");
            Assert.AreEqual(1, unclipped.HoleCount,
                "fixture sanity: unclipped, the buffer-corner ring must be classified as a HOLE — otherwise " +
                "arm 2 below proves nothing.");

            // Arm 2: clipped at the tile boundary, the hole is gone and the outer is still the outer.
            var clipped = RunClipThenAssemble(new[] { BufferedRect, hole }, TileBufferClip.KeepTileUnits(0.0));
            Assert.AreEqual(1, clipped.PolygonCount,
                "clipped: exactly one polygon. Two means the dropped hole promoted something to an outer ring " +
                "— it would render solid.");
            Assert.AreEqual(0, clipped.HoleCount,
                "clipped: the hole lies wholly outside the window and must not survive.");
            Assert.Greater(clipped.OuterShoelace2(0), 0.0,
                "the surviving polygon's outer ring must keep the feature's exterior (CCW) sign.");
        }

        [Test]
        public void Clip_DropsAHoleWhoseOuterAlsoClipsAway()
        {
            // The exterior-sign worry stated plainly: a feature whose FIRST ring vanishes must not leave its
            // hole behind to become the exterior. A hole is contained in its outer, so both go together.
            var outerOutside = new[]
            {
                new double2(4100.0, 4100.0),
                new double2(4160.0, 4100.0),
                new double2(4160.0, 4160.0),
                new double2(4100.0, 4160.0),
            };
            var holeOutside = new[]
            {
                new double2(4110.0, 4110.0),
                new double2(4110.0, 4150.0),
                new double2(4150.0, 4150.0),
                new double2(4150.0, 4110.0),
            };

            var clipped = RunClipThenAssemble(new[] { outerOutside, holeOutside }, TileBufferClip.KeepTileUnits(0.0));
            Assert.AreEqual(0, clipped.PolygonCount,
                "both rings are outside the window: the feature must vanish entirely, not leave the hole " +
                "behind as a solid polygon.");
        }

        // ── T4: clipping does not make triangulation worse ────────────────────────────────────────

        [Test]
        public void Clip_OverTheBufferedCorpus_KeepsGeometryInExtent_AndDoesNotWorsenTriangulation()
        {
            RunCorpusCase("water-6-32-20.pbf.bytes", new TileId { Z = 6, X = 32, Y = 20 });
            RunCorpusCase("water-real-croatia-dalmatia-9-279-187.pbf.bytes",
                          new TileId { Z = 9, X = 279, Y = 187 });
        }

        private static void RunCorpusCase(string fixture, TileId tileId)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture));
            Assert.IsNotNull(bytes);
            using MvtTile mvtTile = MvtDecoder.Decode(tileId, bytes);

            bool sawAnyLayer      = false;
            int  bufferedLayers   = 0;
            foreach (string layerName in new[] { "water", "park", "landuse", "landcover" })
            {
                var layer = mvtTile.GetLayer(layerName);
                if (layer == null) continue;

                double extent = layer.Extent;
                bool anyPolygon = false;
                foreach (var f in layer.Features)
                    if (f.GeometryType == TileGeometryType.Polygon) { anyPolygon = true; break; }
                if (!anyPolygon) continue;
                sawAnyLayer = true;

                var (bMin, _) = tileId.MercatorBounds();

                // ONE buffer, owned by the decoded LAYER and BORROWED by both Schedule calls
                // (Schedule derives its own private copy of the rings it visits and disposes only that).
                TileGeometryBuffers geometry = layer.Geometry;
                NativeArray<int> visitOrder  = TestTileMeshBuilder.FullVisitOrder(geometry);
                var baseInput = new FillMeshPipeline.LayerInput
                {
                    Geometry       = geometry,
                    RingVisitOrder = visitOrder,
                    OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                    Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                };

                var unclippedInput = baseInput; unclippedInput.Clip = TileBufferClip.Disabled;
                var clippedInput   = baseInput; clippedInput.Clip   = TileBufferClip.KeepTileUnits(0.0);

                FillGraphOutput unclipped = FillMeshGraph.Schedule(unclippedInput);
                FillGraphOutput clipped   = FillMeshGraph.Schedule(clippedInput);
                unclipped.Handle.Complete();
                clipped.Handle.Complete();
                try
                {
                    Assert.IsTrue(unclipped.IsCreated, $"{fixture}/{layerName}: unclipped produced no buffers.");
                    Assert.IsTrue(clipped.IsCreated, $"{fixture}/{layerName}: clipped produced no buffers.");

                    // Non-vacuity is a claim about the FIXTURE, not about each of its layers: a small layer
                    // (croatia-dalmatia's `park`) can legitimately sit wholly inside the tile. Counted here
                    // and asserted once per fixture below.
                    if (CountOutsideExtent(unclipped, extent) > 0) bufferedLayers++;

                    // (a) after the clip, nothing is drawn outside the tile.
                    Assert.AreEqual(0, CountOutsideExtent(clipped, extent),
                        $"{fixture}/{layerName}: {CountOutsideExtent(clipped, extent)} output vertices lie " +
                        $"outside [0,{extent}] after clipping at the tile boundary.");

                    // (b) the S–H degenerate-channel risk: zero-width channels along the clip edge must not
                    // drive EarcutJob into its force-clip escape more often than the unclipped input did.
                    int unclippedForceClips = unclipped.Counts[0].ForceClipCount;
                    int clippedForceClips   = clipped.Counts[0].ForceClipCount;
                    Assert.LessOrEqual(clippedForceClips, unclippedForceClips,
                        $"{fixture}/{layerName}: clipping raised the earcut force-clip count from " +
                        $"{unclippedForceClips} to {clippedForceClips}. That is a finding " +
                        "about the clipper (the degenerate-channel case), not a licence to retune earcut.");

                    Debug.Log($"[RingClipJob T4] {fixture}/{layerName}: verts {unclipped.TileVertices.Length} → " +
                              $"{clipped.TileVertices.Length}, forceClips {unclippedForceClips} → " +
                              $"{clippedForceClips}");
                }
                finally
                {
                    unclipped.Dispose();
                    clipped.Dispose();
                    // The shared buffer outlived BOTH Schedule calls — that is the borrow contract. It is
                    // NOT disposed here: the decoded tile owns it and frees it with `using`.
                    visitOrder.Dispose();
                }
            }

            Assert.IsTrue(sawAnyLayer, $"{fixture}: no polygon layer found — the corpus case did not run.");
            Assert.Greater(bufferedLayers, 0,
                $"{fixture}: NO layer's unclipped mesh has an out-of-extent vertex — the fixture is not " +
                "buffered, so assertion (a) above passed without the clip ever having to cut anything.");
        }

        private static int CountOutsideExtent(FillGraphOutput buffers, double extent)
        {
            const double eps = 1e-9;
            int count = 0;
            int n = buffers.TileVertices.Length;
            for (int i = 0; i < n; i++)
            {
                double2 v = buffers.TileVertices[i];
                if (v.x < -eps || v.y < -eps || v.x > extent + eps || v.y > extent + eps) count++;
            }
            return count;
        }

        // ── Harness ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The clip job's output, materialised as managed arrays so assertions read plainly.</summary>
        private struct ClipResult
        {
            public NativeList<double2> Vertices;
            public NativeList<int>     RingOffsets;
            public NativeList<int>     RingFeatureIdx;

            public int RingCount   => RingOffsets.Length - 1;
            public int VertexCount => Vertices.Length;

            public double2[] Ring(int ri)
            {
                int start = RingOffsets[ri];
                var ring  = new double2[RingOffsets[ri + 1] - start];
                for (int i = 0; i < ring.Length; i++) ring[i] = Vertices[start + i];
                return ring;
            }

            public void Dispose()
            {
                Vertices.Dispose();
                RingOffsets.Dispose();
                RingFeatureIdx.Dispose();
            }
        }

        /// <param name="visitOrder">The ring indices this pass visits, in order. <c>null</c> ⇒ the
        /// identity order (every ring, in decode order), which is what <c>RingCount</c> used to mean here.</param>
        /// <param name="ringFeatureIdx">Which feature each ring belongs to. <c>null</c> ⇒ all rings share
        /// feature 0, which is what the hole teeth need (they rely on shared feature grouping).</param>
        private static ClipResult RunClip(
            double2[][] rings, TileBufferClip clip, int[] visitOrder = null, int[] ringFeatureIdx = null)
        {
            Assert.IsTrue(clip.TryWindow(Extent, out double2 clipMin, out double2 clipMax),
                "the test's clip must be enabled — a Disabled knob never reaches the job.");

            int totalVerts = 0, maxRingLen = 0;
            foreach (var r in rings) { totalVerts += r.Length; maxRingLen = math.max(maxRingLen, r.Length); }

            var verts       = new NativeArray<double2>(totalVerts, Allocator.Persistent);
            var ringOffsets = new NativeArray<int>(rings.Length + 1, Allocator.Persistent);
            var ringFeatIdx = new NativeArray<int>(rings.Length, Allocator.Persistent);
            int pos = 0;
            for (int ri = 0; ri < rings.Length; ri++)
            {
                ringOffsets[ri] = pos;
                // Default: one feature — the hole tooth relies on shared feature grouping.
                ringFeatIdx[ri] = ringFeatureIdx != null ? ringFeatureIdx[ri] : 0;
                foreach (var v in rings[ri]) verts[pos++] = v;
            }
            ringOffsets[rings.Length] = pos;

            int bufferCap = math.max(1, maxRingLen * RingClipJob.BufferLengthMultiplier);
            var bufferA = new NativeArray<double2>(bufferCap, Allocator.Persistent);
            var bufferB = new NativeArray<double2>(bufferCap, Allocator.Persistent);

            var result = new ClipResult
            {
                Vertices       = new NativeList<double2>(math.max(1, totalVerts), Allocator.Persistent),
                RingOffsets    = new NativeList<int>(rings.Length + 1, Allocator.Persistent),
                RingFeatureIdx = new NativeList<int>(math.max(1, rings.Length), Allocator.Persistent),
            };

            // The visit order IS the ring set. Default = identity (every ring, in decode order).
            int[] order = visitOrder ?? IdentityOrder(rings.Length);
            var visitOrderArr = new NativeArray<int>(order.Length, Allocator.Persistent);
            for (int i = 0; i < order.Length; i++) visitOrderArr[i] = order[i];

            new RingClipJob
            {
                Vertices          = verts,
                RingOffsets       = ringOffsets,
                RingFeatureIdx    = ringFeatIdx,
                RingVisitOrder    = visitOrderArr,
                ClipMin           = clipMin,
                ClipMax           = clipMax,
                BufferA          = bufferA,
                BufferB          = bufferB,
                OutVertices       = result.Vertices,
                OutRingOffsets    = result.RingOffsets,
                OutRingFeatureIdx = result.RingFeatureIdx,
            }.Run();

            verts.Dispose(); ringOffsets.Dispose(); ringFeatIdx.Dispose();
            bufferA.Dispose(); bufferB.Dispose(); visitOrderArr.Dispose();
            return result;
        }

        private static int[] IdentityOrder(int count)
        {
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            return order;
        }

        /// <summary>Polygon structure after clip + the real <see cref="RingAssemblyJob"/> — the stage order
        /// under test (clip BEFORE assembly).</summary>
        private struct AssemblyResult
        {
            public int PolygonCount;
            public int HoleCount;
            public double[] OuterShoelace2Values;
            public double OuterShoelace2(int pi) => OuterShoelace2Values[pi];
        }

        private static AssemblyResult RunClipThenAssemble(double2[][] rings, TileBufferClip clip)
        {
            double2[][] assembleRings;
            if (clip.IsEnabled)
            {
                var clipped = RunClip(rings, clip);
                try
                {
                    assembleRings = new double2[clipped.RingCount][];
                    for (int ri = 0; ri < clipped.RingCount; ri++) assembleRings[ri] = clipped.Ring(ri);
                }
                finally { clipped.Dispose(); }
            }
            else
            {
                assembleRings = rings;
            }

            if (assembleRings.Length == 0)
                return new AssemblyResult { PolygonCount = 0, HoleCount = 0, OuterShoelace2Values = new double[0] };

            int totalVerts = 0;
            foreach (var r in assembleRings) totalVerts += r.Length;

            var verts       = new NativeArray<double2>(math.max(1, totalVerts), Allocator.Persistent);
            var ringOffsets = new NativeArray<int>(assembleRings.Length + 1, Allocator.Persistent);
            var ringFeatIdx = new NativeArray<int>(assembleRings.Length, Allocator.Persistent);
            int pos = 0;
            for (int ri = 0; ri < assembleRings.Length; ri++)
            {
                ringOffsets[ri] = pos;
                ringFeatIdx[ri] = 0;
                foreach (var v in assembleRings[ri]) verts[pos++] = v;
            }
            ringOffsets[assembleRings.Length] = pos;

            int maxRings      = assembleRings.Length;
            var polyOuterIdx  = new NativeArray<int>(maxRings, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(maxRings, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(maxRings, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(maxRings, Allocator.Persistent);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent);

            // The assembler is kind-gated. Every ring here belongs to the single synthetic feature 0,
            // and every one of these fixtures is a polygon fixture.
            var featureKinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            featureKinds[0] = TileGeometryType.Polygon;

            new RingAssemblyJob
            {
                Vertices             = verts,
                RingOffsets          = ringOffsets,
                RingFeatureIdx       = ringFeatIdx,
                RingCount            = assembleRings.Length,
                FeatureGeometryType  = featureKinds,
                OutPolyOuterRingIdx  = polyOuterIdx,
                OutPolyHoleListStart = polyHoleStart,
                OutPolyHoleCount     = polyHoleCount,
                OutHoleRingIdxs      = holeRingIdxs,
                OutPolygonCount      = polyCountArr,
                OutHoleCount         = holeCountArr,
            }.Run();

            var result = new AssemblyResult
            {
                PolygonCount = polyCountArr[0],
                HoleCount    = holeCountArr[0],
            };
            result.OuterShoelace2Values = new double[result.PolygonCount];
            for (int pi = 0; pi < result.PolygonCount; pi++)
                result.OuterShoelace2Values[pi] = Shoelace2(assembleRings[polyOuterIdx[pi]]);

            verts.Dispose(); ringOffsets.Dispose(); ringFeatIdx.Dispose();
            polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
            holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose();
            featureKinds.Dispose();
            return result;
        }

        private static double Shoelace2(IReadOnlyList<double2> ring)
        {
            double area = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % ring.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        /// <summary>Asserts the rings match as a CYCLE — same order, any starting vertex. The order is the
        /// tooth (winding + vertex sequence); which half-plane runs first is not.</summary>
        private static void AssertCyclicallyEqual(double2[] expected, double2[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length);
            int start = -1;
            for (int i = 0; i < actual.Length; i++)
                if (actual[i].Equals(expected[0])) { start = i; break; }
            Assert.GreaterOrEqual(start, 0,
                $"expected vertex {expected[0]} is absent.\n  got: {Describe(actual)}");

            for (int i = 0; i < expected.Length; i++)
                Assert.IsTrue(actual[(start + i) % actual.Length].Equals(expected[i]),
                    $"vertex {i} of the cycle: expected {expected[i]}, got " +
                    $"{actual[(start + i) % actual.Length]}.\n  got: {Describe(actual)}");
        }

        private static string Describe(double2[] ring)
        {
            var parts = new List<string>(ring.Length);
            foreach (var v in ring) parts.Add($"({v.x},{v.y})");
            return string.Join(" ", parts);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileGeometryBuffersTests — ownership teeth for the struct that owns the ring-stage buffers
    // ───────────────────────────────────────────────────────────────────────────────────

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileGeometryBuffersTests — ownership teeth for TileGeometryBuffers
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ownership teeth for <see cref="TileGeometryBuffers"/> — the struct that now owns the
    /// ring-stage buffers <c>FillMeshPipeline.Schedule</c> used to hold as private locals.
    ///
    /// <para>Every case asserts a <b>non-vacuity precondition</b> first (the buffers really were allocated,
    /// really were readable) so the post-<c>Dispose</c> assertions cannot pass over a buffer that was never
    /// created.</para>
    ///
    /// <para><b>How "the backing list was freed" is observed.</b> <c>NativeList&lt;T&gt;.IsCreated</c> reads a
    /// pointer stored <i>in the struct copy</i>, and <c>Dispose()</c> nulls it only on the copy it was called
    /// on — so a test-side copy of a list the buffer disposed still reports <c>IsCreated == true</c>. The
    /// observable that <i>does</i> cross copies is the shared atomic safety handle: once the list is freed the
    /// handle is released, and any access through any copy faults. The exact exception type is a Collections-
    /// package detail (an <c>ObjectDisposedException</c> today), so the assertion is "it faults", paired with a
    /// pre-Dispose read that must succeed.</para>
    /// </summary>
    [TestFixture]
    public class TileGeometryBuffersTests
    {
        private static readonly TileId SampleTile = new TileId { Z = 8, X = 135, Y = 80 };
        private const double SampleExtent = 4096.0;

        /// <summary>T4a: the array-backed mode owns its three arrays and frees all of them, once, on
        /// Dispose — and a second Dispose is a no-op rather than a double free.</summary>
        [Test]
        public void Allocate_ThenDispose_FreesEveryArray_AndIsIdempotent()
        {
            var buffers = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 2, maxRings: 4, maxVertices: 16);

            // Non-vacuity: a zero-length NativeArray can report !IsCreated from birth, which would make the
            // post-Dispose assertions below trivially true. These lengths are all non-zero.
            Assert.IsTrue(buffers.IsCreated, "the buffer must report IsCreated immediately after Allocate");
            Assert.IsTrue(buffers.Vertices.IsCreated, "Vertices must be allocated");
            Assert.IsTrue(buffers.RingOffsets.IsCreated, "RingOffsets must be allocated");
            Assert.IsTrue(buffers.RingFeatureIdx.IsCreated, "RingFeatureIdx must be allocated");
            Assert.AreEqual(16, buffers.Vertices.Length, "Vertices is sized to maxVertices");
            Assert.AreEqual(5, buffers.RingOffsets.Length, "RingOffsets is sized to maxRings + 1 (sentinel)");
            Assert.AreEqual(4, buffers.RingFeatureIdx.Length, "RingFeatureIdx is sized to maxRings");

            buffers.Dispose();

            Assert.IsFalse(buffers.IsCreated, "Dispose must flip the struct's IsCreated");
            Assert.IsFalse(buffers.Vertices.IsCreated, "Dispose must free Vertices");
            Assert.IsFalse(buffers.RingOffsets.IsCreated, "Dispose must free RingOffsets");
            Assert.IsFalse(buffers.RingFeatureIdx.IsCreated, "Dispose must free RingFeatureIdx");

            Assert.DoesNotThrow(() => buffers.Dispose(),
                "Dispose must be idempotent — the IsCreated flip guard makes the second call a no-op, not a " +
                "double free");
        }

        /// <summary>T4b: the list-backed mode frees the backing <b>lists</b> and never the <c>AsArray()</c>
        /// views over them. This is the tooth that catches a wrong backing-mode discriminator — and it is
        /// the <b>only</b> one.
        /// <para><b>Measured, not assumed:</b> disposing an <c>AsArray()</c> view is a <b>silent no-op</b>
        /// under Collections 6.5.0 — <i>not</i> a throw. A RED sweep set the discriminator so
        /// <c>Dispose()</c> freed the views instead of the lists: no exception was raised, the backing lists
        /// simply leaked, and the <b>entire behavioural clip corpus stayed green</b>; this test was the single
        /// failure. Do not assume the collections safety system catches view-vs-list ownership mistakes, and
        /// do not weaken this test on the belief that a behavioural test backs it up. Nothing does.</para></summary>
        [Test]
        public void AdoptDerivedLists_ThenDispose_FreesTheBackingLists_AndNeverTheViews()
        {
            var vertexList = new NativeList<double2>(4, Allocator.Persistent);
            var offsetList = new NativeList<int>(2, Allocator.Persistent);
            var featureList = new NativeList<int>(1, Allocator.Persistent);

            vertexList.Add(new double2(0.0, 0.0));
            vertexList.Add(new double2(10.0, 0.0));
            vertexList.Add(new double2(10.0, 10.0));
            offsetList.Add(0);
            offsetList.Add(3);
            featureList.Add(0);

            var sourceKinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            var buffers = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, sourceKinds, vertexList, offsetList, featureList);
            sourceKinds.Dispose(); // COPIED into the buffer, so this one is still the caller's

            // Non-vacuity: the views really do window onto live, non-empty lists right now.
            Assert.IsTrue(buffers.IsCreated, "the buffer must report IsCreated immediately after adopting");
            Assert.AreEqual(3, buffers.Vertices.Length, "the vertex view spans the whole backing list");
            Assert.AreEqual(2, buffers.RingOffsets.Length, "the offset view spans the whole backing list");
            Assert.AreEqual(1, buffers.RingFeatureIdx.Length, "the feature view spans the whole backing list");
            Assert.DoesNotThrow(() => { var _ = vertexList[0]; },
                "precondition: the backing vertex list is readable before Dispose");
            Assert.DoesNotThrow(() => { var _ = offsetList[0]; },
                "precondition: the backing offset list is readable before Dispose");
            Assert.DoesNotThrow(() => { var _ = featureList[0]; },
                "precondition: the backing feature list is readable before Dispose");

            Assert.DoesNotThrow(() => buffers.Dispose(),
                "Dispose must free the backing lists — disposing an AsArray() view instead throws, because a " +
                "view is an Allocator.None array");

            // The lists are gone: their shared safety handle is released, so any access through the test's
            // own copies faults.
            Assert.That(() => { var _ = vertexList[0]; }, Throws.Exception,
                "the backing vertex list must have been freed by Dispose");
            Assert.That(() => { var _ = offsetList[0]; }, Throws.Exception,
                "the backing offset list must have been freed by Dispose");
            Assert.That(() => { var _ = featureList[0]; }, Throws.Exception,
                "the backing feature list must have been freed by Dispose");

            // The views themselves were never disposed — that is the whole point of the discriminator.
            Assert.IsTrue(buffers.Vertices.IsCreated,
                "the Vertices view must NOT be disposed — only its backing list is freed");
            Assert.IsTrue(buffers.RingOffsets.IsCreated,
                "the RingOffsets view must NOT be disposed — only its backing list is freed");
            Assert.IsTrue(buffers.RingFeatureIdx.IsCreated,
                "the RingFeatureIdx view must NOT be disposed — only its backing list is freed");

            Assert.IsFalse(buffers.IsCreated, "Dispose must flip the struct's IsCreated");
            Assert.DoesNotThrow(() => buffers.Dispose(),
                "Dispose must be idempotent in the list-backed mode too");
        }

        /// <summary>T4c: adopting derives the counts from the list lengths exactly as the clip handover in
        /// <c>FillMeshPipeline.Schedule</c> used to — ring count is <c>RingOffsets.Length - 1</c> because the
        /// offsets carry a trailing sentinel.</summary>
        [Test]
        public void AdoptDerivedLists_DerivesCountsFromTheListLengths()
        {
            var vertexList = new NativeList<double2>(8, Allocator.Persistent);
            var offsetList = new NativeList<int>(3, Allocator.Persistent);
            var featureList = new NativeList<int>(2, Allocator.Persistent);

            for (int i = 0; i < 7; i++)
                vertexList.Add(new double2(i, i));
            offsetList.Add(0);
            offsetList.Add(4);
            offsetList.Add(7);   // 3 entries ⇒ 2 rings + sentinel
            featureList.Add(0);
            featureList.Add(0);

            var sourceKinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            var buffers = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, sourceKinds, vertexList, offsetList, featureList);
            sourceKinds.Dispose();

            Assert.AreEqual(3, offsetList.Length,
                "precondition: the offsets list holds 2 ring starts plus the trailing sentinel");
            Assert.AreEqual(2, buffers.RingCount,
                "RingCount is RingOffsets.Length - 1 — dropping the sentinel would read one ring past the end");
            Assert.AreEqual(7, buffers.VertexCount,
                "VertexCount is the adopted vertex list's length");

            buffers.Dispose();
        }

        /// <summary>T4d: the counts are the producing job's reported values, <b>stored</b>, not derived from
        /// the buffer capacity. Deriving them would make <c>FillMeshPipeline.EnsureCapacity</c>'s
        /// count-vs-capacity comparison tautological and silently disarm the sizing-vs-decode backstop — a
        /// regression no behavioural test can see, because exact pre-count sizing makes the two values equal
        /// on every fixture in the repo.</summary>
        [Test]
        public void RingCount_IsTheJobReportedCount_NotDerivedFromBufferCapacity()
        {
            var buffers = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 2, maxRings: 8, maxVertices: 32);

            Assert.AreEqual(0, buffers.RingCount, "a freshly allocated buffer has reported no rings yet");
            Assert.AreEqual(0, buffers.VertexCount, "a freshly allocated buffer has reported no vertices yet");

            buffers.RingCount = 3;
            buffers.VertexCount = 11;

            Assert.AreEqual(3, buffers.RingCount,
                "RingCount must be the stored reported count, not RingOffsets.Length - 1");
            Assert.AreEqual(9, buffers.RingOffsets.Length,
                "precondition: the capacity (maxRings + 1 = 9) differs from the reported count, so a derived " +
                "count would be observably wrong here");
            Assert.AreEqual(11, buffers.VertexCount,
                "VertexCount must be the stored reported count, not Vertices.Length");
            Assert.AreEqual(32, buffers.Vertices.Length,
                "precondition: the vertex capacity differs from the reported vertex count");

            buffers.Dispose();
        }

        /// <summary>T4e: the provenance metadata is carried, not dropped — the pipeline reads
        /// <see cref="TileGeometryBuffers.Extent"/> back off the buffer when it sizes the clip window.</summary>
        [Test]
        public void Allocate_CarriesTheTileAndExtentItWasGiven()
        {
            var buffers = TileGeometryBuffers.Allocate(
                SampleTile, 8192.0, featureCount: 1, maxRings: 2, maxVertices: 8);

            Assert.AreEqual(SampleTile.Z, buffers.Tile.Z, "the buffer carries the tile it was minted for");
            Assert.AreEqual(SampleTile.X, buffers.Tile.X, "the buffer carries the tile it was minted for");
            Assert.AreEqual(SampleTile.Y, buffers.Tile.Y, "the buffer carries the tile it was minted for");
            Assert.AreEqual(8192.0, buffers.Extent,
                "the buffer carries the extent it was minted with — deliberately not the 4096 every fill " +
                "fixture uses, so a hardcoded default would be visible here");

            buffers.Dispose();
        }

        /// <summary>
        /// T2b — the per-feature kind column must survive a <b>derive</b>, and the derived buffer's
        /// <c>Dispose</c> must not reach into the buffer it was derived from.
        ///
        /// <para>This is the direct replacement for the clip-handover tooth. The mechanism inverted: the
        /// column used to be <b>transferred</b> (released from the pre-clip buffer, adopted by the clipped
        /// one), because the source was about to be disposed. The source is now <b>borrowed</b> — it is
        /// the store's shared buffer, several fill layers derive from it — so it must be left completely
        /// intact, and the derived buffer gets a <b>copy</b>.</para>
        ///
        /// <para>Why this needs a test at all: an <c>AdoptDerivedLists</c> that <i>took</i> the array instead
        /// of copying it would pass every behavioural fill test in the repo. The first layer to derive would
        /// work; the second would read a freed column, or the store's <c>Dispose</c> would double-free — and
        /// the collections safety system does <b>not</b> reliably surface either (measured: a whole green
        /// corpus over a leaking-view discriminator). The observable difference is here, in the source
        /// buffer's state after the derived one dies.</para></summary>
        [Test]
        public void AdoptDerivedLists_CopiesTheKindColumn_SoTheSourceSurvivesTheDerivedBuffersDispose()
        {
            var source = TileGeometryBuffers.Allocate(
                SampleTile, SampleExtent, featureCount: 3, maxRings: 2, maxVertices: 8);

            // Non-vacuity: at least two DISTINCT kinds, so an implementation that carried nothing (or carried
            // a cleared default) cannot pass by accident.
            source.FeatureGeometryType[0] = TileGeometryType.Polygon;
            source.FeatureGeometryType[1] = TileGeometryType.LineString;
            source.FeatureGeometryType[2] = TileGeometryType.Polygon;
            Assert.AreEqual(3, source.FeatureCount, "precondition: FeatureCount is derived from the column");

            var vertexList  = new NativeList<double2>(4, Allocator.Persistent);
            var offsetList  = new NativeList<int>(2, Allocator.Persistent);
            var featureList = new NativeList<int>(1, Allocator.Persistent);
            vertexList.Add(new double2(0.0, 0.0));
            vertexList.Add(new double2(10.0, 0.0));
            vertexList.Add(new double2(10.0, 10.0));
            offsetList.Add(0);
            offsetList.Add(3);
            featureList.Add(1);

            var derived = TileGeometryBuffers.AdoptDerivedLists(
                SampleTile, SampleExtent, source.FeatureGeometryType, vertexList, offsetList, featureList);

            // (a) the source is untouched — this is the whole borrow contract, and it is what a "take"
            //     implementation breaks.
            Assert.IsTrue(source.FeatureGeometryType.IsCreated,
                "the derive must NOT null or steal the source's column — the source is BORROWED");
            Assert.AreEqual(3, source.FeatureCount, "…so the source still reports its own feature count");

            // (b) the derived buffer carries the values, not a cleared default.
            Assert.AreEqual(3, derived.FeatureCount,
                "the derived buffer carries the whole column — the derive renumbers no feature index");
            Assert.AreEqual(TileGeometryType.Polygon,    derived.FeatureGeometryType[0], "values carried over");
            Assert.AreEqual(TileGeometryType.LineString, derived.FeatureGeometryType[1], "…including the discriminating one");
            Assert.AreEqual(TileGeometryType.Polygon,    derived.FeatureGeometryType[2], "values carried over");
            Assert.AreEqual(TileGeometryType.LineString,
                derived.FeatureGeometryType[derived.RingFeatureIdx[0]],
                "the ring→kind join still resolves through the derived RingFeatureIdx");

            // (c) it really is a COPY, not an alias: writing through one must not be visible through the
            //     other. A take-instead-of-copy passes (a) and (b) but fails here.
            derived.FeatureGeometryType[1] = TileGeometryType.Point;
            Assert.AreEqual(TileGeometryType.LineString, source.FeatureGeometryType[1],
                "the derived column must be a distinct allocation — an alias would show the write here, and " +
                "the two buffers would then double-free it");

            // (d) disposing the derived buffer leaves the source fully usable. Under Collections 6.5.0 a
            //     two-owner mistake is a SILENT no-op rather than a throw, so read the value back.
            derived.Dispose();
            Assert.IsTrue(source.FeatureGeometryType.IsCreated,
                "the source's column must survive the derived buffer's Dispose");
            Assert.DoesNotThrow(() => { var _ = source.FeatureGeometryType[1]; },
                "…and still be readable");
            Assert.AreEqual(TileGeometryType.LineString, source.FeatureGeometryType[1],
                "…with its value intact");

            source.Dispose();
            Assert.DoesNotThrow(() => derived.Dispose(), "a second Dispose is a no-op, not a double free");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileGeometryMaterializerSeamTests — Waist 1's producer seam as seen from FillMeshGraph.Schedule
    // ───────────────────────────────────────────────────────────────────────────────────

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileGeometryMaterializerSeamTests — Waist 1's producer seam from FillMeshGraph.Schedule
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The teeth on Waist 1's producer seam as seen from <c>FillMeshGraph.Schedule</c>.
    ///
    /// <para>The fixture is one GeoJSON polygon <b>with a hole</b>, authored by inverting a chosen tile's own
    /// <c>ToLonLat</c> so both rings land on exact tile-local integers well inside the tile, and sliced by the
    /// production <c>GeoJsonParser</c> → <c>GeoJsonProjectedDataset</c> → <c>GeoJsonTileSlicer</c> stack. The
    /// extent is <b>8192, never 4096</b>: every fill fixture in the repo uses 4096, so an implementation that
    /// substituted that literal for the buffer's own extent would be undiscriminated by all of them.</para>
    ///
    /// <para><b>The geojson arm drives the PRODUCTION decoder</b>, <see cref="GeoJsonTileDecoder"/>, not
    /// a test-side double: a second implementation of the same join can drift, and every test using
    /// the double would then go green against a shape production does not produce.</para>
    /// </summary>
    [TestFixture]
    public class TileGeometryMaterializerSeamTests
    {
        // z14 puts the whole fixture inside one tile at metre-scale resolution; x/y are arbitrary land.
        private static readonly TileId FixtureTile = new TileId { Z = 14, X = 8000, Y = 5000 };

        private const double ExtentHigh = 8192.0;
        private const double ExtentLow  = 4096.0;

        // Tile-local corners the fixture is authored to hit, at ExtentHigh. All even, so halving them for
        // ExtentLow lands on exact integers too — no quantization collapse between the two arms.
        private const double OuterMin = 1000.0, OuterMax = 7000.0;
        private const double HoleMin  = 3000.0, HoleMax  = 5000.0;

        /// <summary>
        /// T1 — the pipeline is <b>producer-blind</b>: a GeoJSON tile and an MVT tile carrying the same
        /// tile-local rings produce bit-identical mesh buffers. A differential over producers, so it cannot
        /// pass by both arms being equally wrong about the pipeline.
        /// </summary>
        [Test]
        public void Materializer_GeoJsonAndMvt_SameTileLocalRings_ProduceBitIdenticalMeshBuffers()
        {
            TileSlice slice = SliceFixture(ExtentHigh);
            AssertFixtureShape(slice, ExtentHigh);

            FillGraphOutput geoJson = RunGeoJson(ExtentHigh, TileBufferClip.Disabled, slice);
            FillGraphOutput mvt     = RunMvt(slice, TileBufferClip.Disabled);

            try
            {
                // Non-vacuity: both arms really produced a triangulated polygon WITH a hole, at an extent
                // that is not the one every other fill fixture uses.
                Assert.AreNotEqual(4096.0, slice.Extent,
                    "the fixture must not sit at the extent every other fill fixture uses");
                Assert.IsTrue(geoJson.IsCreated, "the GeoJSON arm produced no buffers");
                Assert.IsTrue(mvt.IsCreated, "the MVT arm produced no buffers");
                Assert.Greater(geoJson.TileVertices.Length, 0, "the GeoJSON arm produced no vertices");
                Assert.GreaterOrEqual(geoJson.TriangleIndices.Length, 3, "the GeoJSON arm produced no triangles");
                Assert.AreEqual(1, geoJson.Counts[0].PolygonCount, "precondition: exactly one polygon");
                Assert.AreEqual(1, geoJson.Counts[0].HoleCount,
                    "precondition: the hole path in RingAssemblyJob + EarcutJob really ran");
                Assert.AreEqual(2, geoJson.Counts[0].RingCount, "precondition: exterior + hole reached assembly");

                Assert.AreEqual(geoJson.TileVertices.Length, mvt.TileVertices.Length, "vertex counts must match");
                Assert.AreEqual(geoJson.Counts[0].PolygonCount, mvt.Counts[0].PolygonCount, "polygon counts must match");
                Assert.AreEqual(geoJson.Counts[0].RingCount, mvt.Counts[0].RingCount, "ring counts must match");
                Assert.AreEqual(geoJson.Counts[0].HoleCount, mvt.Counts[0].HoleCount, "hole counts must match");
                Assert.AreEqual(geoJson.TriangleIndices.Length, mvt.TriangleIndices.Length, "index counts must match");

                for (int i = 0; i < geoJson.TileVertices.Length; i++)
                {
                    Assert.AreEqual(geoJson.TileVertices[i], mvt.TileVertices[i], $"TileVertices[{i}]");
                    Assert.AreEqual(geoJson.WorldPositions[i].x, mvt.WorldPositions[i].x, $"WorldPositions[{i}].x");
                    Assert.AreEqual(geoJson.WorldPositions[i].y, mvt.WorldPositions[i].y, $"WorldPositions[{i}].y");
                    Assert.AreEqual(geoJson.WorldPositions[i].z, mvt.WorldPositions[i].z, $"WorldPositions[{i}].z");
                }

                for (int i = 0; i < geoJson.TriangleIndices.Length; i++)
                    Assert.AreEqual(geoJson.TriangleIndices[i], mvt.TriangleIndices[i], $"TriangleIndices[{i}]");
            }
            finally
            {
                geoJson.Dispose();
                mvt.Dispose();
            }
        }

        /// <summary>
        /// T2a — the clip window is derived from the <b>buffer's own</b> extent. At extent 8192 the
        /// standard 64-unit buffer gives <c>[−128, 8320]²</c>, which contains the whole fixture, so enabling
        /// the clip is a provable no-op. A window sized from the 4096 reference literal would be
        /// <c>[−64, 4224]²</c> and would cut the fixture in half.
        /// </summary>
        [Test]
        public void ClipWindow_UsesTheBuffersOwnExtent_NotTheReferenceExtent()
        {
            TileSlice slice = SliceFixture(ExtentHigh);
            AssertFixtureShape(slice, ExtentHigh);

            // Non-vacuity, all three required: the RIGHT window contains the fixture, the WRONG window would
            // cut it, and the fixture really does reach past the wrong window's edge.
            var clip = TileBufferClip.KeepTileUnits(64.0);
            Assert.IsTrue(clip.TryWindow(ExtentHigh, out _, out double2 rightMax),
                "precondition: the clip is enabled at the fixture's extent");
            Assert.Greater(rightMax.x, OuterMax, "the correct window must contain the fixture");
            Assert.IsTrue(clip.TryWindow(TileBufferClip.ReferenceExtent, out _, out double2 wrongMax),
                "precondition: the reference-extent window is computable");
            Assert.Less(wrongMax.x, OuterMax, "the wrong window really would cut the fixture");
            Assert.Greater(MaxTileLocalX(slice), wrongMax.x,
                "the fixture must reach past the wrong window's edge, or no window could discriminate");

            FillGraphOutput unclipped = RunGeoJson(ExtentHigh, TileBufferClip.Disabled, slice);
            FillGraphOutput clipped   = RunGeoJson(ExtentHigh, clip, slice);

            try
            {
                Assert.IsTrue(unclipped.IsCreated, "the unclipped arm produced no buffers");
                Assert.IsTrue(clipped.IsCreated, "the clipped arm produced no buffers");
                Assert.Greater(unclipped.TileVertices.Length, 0, "precondition: there is geometry to compare");
                Assert.AreEqual(1, unclipped.Counts[0].HoleCount, "precondition: the hole survived the unclipped arm");

                Assert.AreEqual(unclipped.TileVertices.Length, clipped.TileVertices.Length,
                    "clipping at the buffer's own extent is a no-op for a fixture wholly inside the window");
                Assert.AreEqual(unclipped.TriangleIndices.Length, clipped.TriangleIndices.Length, "index counts must match");
                Assert.AreEqual(unclipped.Counts[0].HoleCount, clipped.Counts[0].HoleCount, "hole counts must match");

                for (int i = 0; i < unclipped.TileVertices.Length; i++)
                    Assert.AreEqual(unclipped.TileVertices[i], clipped.TileVertices[i], $"TileVertices[{i}]");
                for (int i = 0; i < unclipped.TriangleIndices.Length; i++)
                    Assert.AreEqual(unclipped.TriangleIndices[i], clipped.TriangleIndices[i],
                        $"TriangleIndices[{i}]");
            }
            finally
            {
                unclipped.Dispose();
                clipped.Dispose();
            }
        }

        /// <summary>
        /// T2b — the tile→geodetic conversion reads the <b>buffer's own</b> extent too, which T2a
        /// cannot see. The same geodetic polygon sliced into the same tile at 4096 and at 8192 must land in
        /// the same place in world space; a <c>TileToGeoJob</c> reading a 4096 literal would place the 8192
        /// arm at twice tile-local scale — an error of order a whole tile edge.
        /// </summary>
        [Test]
        public void TileToGeo_UsesTheBuffersOwnExtent_SoTwoExtentsAgreeInWorldSpace()
        {
            TileSlice lowSlice  = SliceFixture(ExtentLow);
            TileSlice highSlice = SliceFixture(ExtentHigh);
            AssertFixtureShape(lowSlice, ExtentLow);
            AssertFixtureShape(highSlice, ExtentHigh);

            FillGraphOutput low  = RunGeoJson(ExtentLow,  TileBufferClip.Disabled, lowSlice);
            FillGraphOutput high = RunGeoJson(ExtentHigh, TileBufferClip.Disabled, highSlice);

            try
            {
                // Quantization is at most 0.5 tile units per arm, and one tile unit at extent E is
                // (tile edge in world metres) / E. So the two arms can disagree by at most half a tile unit
                // of each — no magic number, and nothing about the pipeline is assumed.
                double tileEdgeWorld = 2.0 * WebMercator.WorldExtent / math.pow(2.0, FixtureTile.Z);
                double tolerance     = 0.5 * tileEdgeWorld / ExtentLow + 0.5 * tileEdgeWorld / ExtentHigh;

                // Non-vacuity: the comparison is element-to-element and not vacuously empty, and the
                // tolerance cannot pass a whole-tile-scale error.
                Assert.Greater(low.TileVertices.Length, 0, "precondition: the 4096 arm produced vertices");
                Assert.AreEqual(low.TileVertices.Length, high.TileVertices.Length,
                    "both extents must triangulate to the same vertex count, or the comparison is not " +
                    "element-to-element (move the fixture corners, never loosen the tolerance)");
                Assert.AreEqual(low.TriangleIndices.Length, high.TriangleIndices.Length, "index counts must match");
                Assert.Less(tolerance * 100.0, tileEdgeWorld,
                    "the tolerance must be at least 100x smaller than a tile edge, or it could pass by being loose");

                for (int i = 0; i < low.TileVertices.Length; i++)
                {
                    Assert.AreEqual(low.WorldPositions[i].x, high.WorldPositions[i].x, tolerance,
                        $"WorldPositions[{i}].x disagrees between extents 4096 and 8192");
                    Assert.AreEqual(low.WorldPositions[i].y, high.WorldPositions[i].y, tolerance,
                        $"WorldPositions[{i}].y disagrees between extents 4096 and 8192");
                    Assert.AreEqual(low.WorldPositions[i].z, high.WorldPositions[i].z, tolerance,
                        $"WorldPositions[{i}].z disagrees between extents 4096 and 8192");
                }
            }
            finally
            {
                low.Dispose();
                high.Dispose();
            }
        }

        // ── Fixture ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The polygon-with-hole, authored in lon/lat by inverting the tile's own
        /// <c>ToLonLat</c> at <see cref="ExtentHigh"/> so it lands on the tile-local integers above. The RFC
        /// winding argument is irrelevant — <c>GeoJsonParser</c> re-encodes ring role as winding — so the
        /// hole is authored reversed only to keep the JSON honest to the RFC.</summary>
        private static string FixtureJson()
        {
            double2 outerNw = FixtureTile.ToLonLat(OuterMin, OuterMin, ExtentHigh);
            double2 outerSe = FixtureTile.ToLonLat(OuterMax, OuterMax, ExtentHigh);
            double2 holeNw  = FixtureTile.ToLonLat(HoleMin, HoleMin, ExtentHigh);
            double2 holeSe  = FixtureTile.ToLonLat(HoleMax, HoleMax, ExtentHigh);

            // Tile-local Y grows SOUTHWARD, so the small-Y corner carries the NORTH latitude.
            string exterior = GeoJsonTestFixtures.RectangleRing(outerNw.x, outerSe.y, outerSe.x, outerNw.y);
            string hole     = GeoJsonTestFixtures.RectangleRing(holeNw.x, holeSe.y, holeSe.x, holeNw.y,
                                                                rfcWound: false);

            return GeoJsonTestFixtures.Collection(
                GeoJsonTestFixtures.Feature("Polygon", $"[{exterior},{hole}]"));
        }

        private static TileSlice SliceFixture(double extent)
            => GeoJsonTestFixtures.Slice(
                FixtureJson(), FixtureTile, GeoJsonTestFixtures.Options(extent, 64.0));

        /// <summary>Pins that the production slicer really put the fixture where the tests reason it is —
        /// one feature, two four-point rings, on the exact tile-local integers, inside the tile it was
        /// authored for.</summary>
        private static void AssertFixtureShape(TileSlice slice, double extent)
        {
            Assert.AreEqual(extent, slice.Extent, "the slice carries the extent it was sliced at");
            Assert.AreEqual(1, slice.Features.Count, "the fixture is exactly one feature");
            Assert.AreEqual(2, slice.Features[0].Paths.Count, "exterior + hole");
            Assert.AreEqual(4, slice.Features[0].Paths[0].Count, "the exterior is a 4-point ring");
            Assert.AreEqual(4, slice.Features[0].Paths[1].Count, "the hole is a 4-point ring");

            double scale = extent / ExtentHigh;
            Assert.AreEqual(OuterMax * scale, MaxTileLocalX(slice), 1e-9,
                "the fixture must quantize onto the exact tile-local integer it was authored for");
        }

        private static double MaxTileLocalX(TileSlice slice)
        {
            double maxX = double.NegativeInfinity;
            foreach (SlicedFeature feature in slice.Features)
                foreach (IReadOnlyList<double2> path in feature.Paths)
                    for (int i = 0; i < path.Count; i++)
                        maxX = math.max(maxX, path[i].x);
            return maxX;
        }

        /// <summary>Re-encodes the slice's OWN tile-local integer rings as an MVT command stream and
        /// materializes it, so the two arms differ only in which producer put the identical numbers into the
        /// buffer.</summary>
        private static TileGeometryBuffers MvtArm(TileSlice slice)
        {
            var kinds    = new List<TileGeometryType>(slice.Features.Count);
            var commands = new List<uint[]>(slice.Features.Count);
            foreach (SlicedFeature feature in slice.Features)
            {
                var rings = new IReadOnlyList<double2>[feature.Paths.Count];
                for (int p = 0; p < feature.Paths.Count; p++)
                    rings[p] = feature.Paths[p];
                kinds.Add(feature.Source.GeometryType);
                commands.Add(MvtCommandStream.Feature(rings));
            }

            return MvtGeometryMaterializerTestFactory.Materialize(slice.Tile, slice.Extent, kinds, commands);
        }

        /// <summary>The GeoJSON arm, driven through the <b>production</b> decoder — parse → project → slice →
        /// materialize → layer, exactly as a live tile takes it. The decoded tile OWNS the buffer, so it is
        /// disposed here and never by <see cref="Run"/>.</summary>
        private static FillGraphOutput RunGeoJson(double extent, TileBufferClip clip, TileSlice expected)
        {
            var decoder = new GeoJsonTileDecoder(
                GeoJsonProjectedDataset.Project(GeoJsonParser.Parse(FixtureJson())),
                GeoJsonTestFixtures.Options(extent, 64.0));

            using IDecodedTile tile = decoder.Decode(FixtureTile, null);
            ITileLayer layer = tile.GetLayer(null);

            Assert.IsNotNull(layer,
                "precondition: the production decoder must yield a layer for a tile the fixture is inside — " +
                "a null layer would make every comparison below vacuous");
            Assert.AreEqual(expected.Features.Count, layer.Features.Count,
                "precondition: the decoder must carry exactly the features the slicer produced (slicing is a " +
                "pure function of dataset/tile/options, so the two runs must agree)");
            Assert.AreEqual((uint)extent, layer.Extent, "precondition: the layer reports the sliced extent");

            return Run(layer.Geometry, clip);
        }

        /// <summary>The MVT arm: the slice's own tile-local integers re-encoded as a command stream, so the
        /// two arms differ only in which producer put identical numbers into the buffer. This one mints the
        /// buffer, so this one frees it.</summary>
        private static FillGraphOutput RunMvt(TileSlice slice, TileBufferClip clip)
        {
            TileGeometryBuffers geometry = MvtArm(slice);
            try     { return Run(geometry, clip); }
            finally { geometry.Dispose(); }
        }

        /// <summary>Visit every ring in decode order, schedule, and free what this harness owns.
        /// <c>Schedule</c> BORROWS the buffer — it derives its own — so the caller keeps ownership and this
        /// never disposes it.</summary>
        private static FillGraphOutput Run(TileGeometryBuffers geometry, TileBufferClip clip)
        {
            var (boundsMin, _) = FixtureTile.MercatorBounds();
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            try
            {
                FillGraphOutput output = FillMeshGraph.Schedule(new FillMeshPipeline.LayerInput
                {
                    Geometry       = geometry,
                    RingVisitOrder = visitOrder,
                    OriginRender   = new double3(boundsMin.x, 0.0, boundsMin.y),
                    Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
                    Clip           = clip,
                });
                output.Handle.Complete();
                return output;
            }
            finally
            {
                visitOrder.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // WaistOneProducerAgreementTests — the two producers agree on feature count
    // ───────────────────────────────────────────────────────────────────────────────────

    // ───────────────────────────────────────────────────────────────────────────────────
    // WaistOneProducerAgreementTests — Waist 1's two producers agree on feature count
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Waist 1's two producers agree on the feature count, and the lockstep is a
    /// property of the type rather than of one writer.</b>
    ///
    /// <para>Three consumers size a per-feature column from a count and then index it by
    /// <c>SelectedTileFeature.Ordinal</c>. That only works while the buffer's feature column and
    /// <c>ITileLayer.Features</c> hold the same number of entries. <see cref="MvtGeometryMaterializer"/>
    /// guaranteed it (it early-outs on the FEATURE count); <see cref="PathGeometryMaterializer"/> did not
    /// (it early-outed on the RING total, reachable with features present), and <c>MvtLayer.Geometry</c> was
    /// a public mutable field that anything could desync. Both are closed here.</para>
    /// </summary>
    [TestFixture]
    public class WaistOneProducerAgreementTests
    {
        private static readonly TileId Tile = new TileId { Z = 3, X = 4, Y = 5 };

        // ── Root cause · the two producers early-out on the same thing ────────────────────────────────

        /// <summary>Three features, none of which carries a path — a GeoJSON feature list sliced away at
        /// this tile is the realistic source. The old <c>ringTotal == 0</c> early-out returned
        /// <c>default</c>, i.e. <c>FeatureCount == 0</c> beside three features.</summary>
        [Test]
        public void PathProducer_FeaturesWithNoPaths_StillMintsAFeatureColumn()
        {
            var kinds = new List<TileGeometryType>
            {
                TileGeometryType.LineString, TileGeometryType.Point, TileGeometryType.Polygon,
            };
            var noPaths = new List<IReadOnlyList<IReadOnlyList<double2>>>
            {
                new List<IReadOnlyList<double2>>(), null, new List<IReadOnlyList<double2>>(),
            };

            TileGeometryBuffers geometry =
                new PathGeometryMaterializer(Tile, 4096.0, kinds, noPaths).Materialize();
            try
            {
                Assert.IsTrue(geometry.IsCreated,
                    "features present ⇒ a buffer, even with no rings. Returning `default` here is what puts " +
                    "FeatureCount == 0 next to a non-empty ITileLayer.Features.");
                Assert.AreEqual(3, geometry.FeatureCount, "one kind slot per feature");
                Assert.AreEqual(0, geometry.RingCount,    "no paths ⇒ no rings");
                Assert.AreEqual(0, geometry.VertexCount,  "no paths ⇒ no vertices");

                // The kind column is not merely present, it is CORRECT — a buffer that carried the right
                // length and the wrong kinds would satisfy the count assertions above and still mis-classify
                // every consumer's ring gate.
                Assert.AreEqual(TileGeometryType.LineString, geometry.FeatureGeometryType[0]);
                Assert.AreEqual(TileGeometryType.Point,      geometry.FeatureGeometryType[1]);
                Assert.AreEqual(TileGeometryType.Polygon,    geometry.FeatureGeometryType[2]);

                Assert.AreEqual(Tile, geometry.Tile,   "the producer is the sole authority for the address");
                Assert.AreEqual(4096.0, geometry.Extent, 0.0, "…and for the extent");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>The agreement itself, stated as a comparison rather than as two separate numbers: given
        /// the same three features carrying no geometry, the MVT producer and the path producer must report
        /// the same feature count. This is the property the consumer change relies on.</summary>
        [Test]
        public void BothWaistOneProducers_ReportTheSameFeatureCount_ForFeaturesWithNoGeometry()
        {
            var kinds = new List<TileGeometryType>
            {
                TileGeometryType.LineString, TileGeometryType.Point, TileGeometryType.Polygon,
            };

            TileGeometryBuffers fromMvt = MvtGeometryMaterializerTestFactory.Materialize(
                Tile, 4096.0, kinds, new List<uint[]> { null, null, null });
            TileGeometryBuffers fromPaths = new PathGeometryMaterializer(
                Tile, 4096.0, kinds,
                new List<IReadOnlyList<IReadOnlyList<double2>>> { null, null, null }).Materialize();
            try
            {
                Assert.AreEqual(fromMvt.IsCreated, fromPaths.IsCreated,
                    "the two producers must make the same allocate-or-not decision for equivalent input");
                Assert.AreEqual(fromMvt.FeatureCount, fromPaths.FeatureCount,
                    "…and report the same feature count, which is what every ordinal-indexed consumer sizes from");
                Assert.AreEqual(3, fromMvt.FeatureCount, "anti-vacuity: neither may agree at zero");
                Assert.AreEqual(fromMvt.RingCount, fromPaths.RingCount, "…and the same ring count");
            }
            finally { fromMvt.Dispose(); fromPaths.Dispose(); }
        }

        /// <summary>The early-out that remains: no features at all allocates nothing, in BOTH producers.
        /// Pinned so "align the early-outs" cannot drift into "always allocate".</summary>
        [Test]
        public void BothWaistOneProducers_NoFeatures_AllocateNothing()
        {
            TileGeometryBuffers fromMvt = MvtGeometryMaterializerTestFactory.Materialize(
                Tile, 4096.0, new List<TileGeometryType>(), new List<uint[]>());
            TileGeometryBuffers fromPaths = new PathGeometryMaterializer(
                Tile, 4096.0, new List<TileGeometryType>(),
                new List<IReadOnlyList<IReadOnlyList<double2>>>()).Materialize();

            Assert.IsFalse(fromMvt.IsCreated,   "no features ⇒ no buffer (MVT)");
            Assert.IsFalse(fromPaths.IsCreated, "no features ⇒ no buffer (paths)");
            Assert.AreEqual(0, fromMvt.FeatureCount);
            Assert.AreEqual(0, fromPaths.FeatureCount);
        }

        // ── The lockstep is enforced by MvtLayer, not by MvtDecoder's discipline ───────────────────────

        private static MvtLayer LayerWith(int featureCount)
        {
            var layer = new MvtLayer { Name = "probe", Extent = 4096 };
            for (int i = 0; i < featureCount; i++)
                layer.Features.Add(new MvtFeature { GeometryType = TileGeometryType.Point });
            return layer;
        }

        private static TileGeometryBuffers BufferFor(int featureCount)
        {
            var kinds    = new List<TileGeometryType>(featureCount);
            var commands = new List<uint[]>(featureCount);
            for (int i = 0; i < featureCount; i++) { kinds.Add(TileGeometryType.Point); commands.Add(null); }
            return MvtGeometryMaterializerTestFactory.Materialize(Tile, 4096.0, kinds, commands);
        }

        [Test]
        public void MvtLayer_RejectsABufferWhoseFeatureColumnDoesNotMatchItsFeatures()
        {
            MvtLayer layer = LayerWith(2);
            TileGeometryBuffers mismatched = BufferFor(3);

            Assert.AreEqual(3, mismatched.FeatureCount, "precondition: the buffer really does hold 3");
            Assert.AreEqual(2, layer.Features.Count,    "precondition: the layer really does hold 2");

            ArgumentException ex = Assert.Throws<ArgumentException>(() => layer.AdoptGeometry(mismatched),
                "a layer must refuse a buffer it is not in lockstep with — silently accepting it is what " +
                "mis-buckets every ordinal-indexed consumer");
            StringAssert.Contains("3", ex.Message);
            StringAssert.Contains("2", ex.Message);

            Assert.IsFalse(layer.Geometry.IsCreated, "a rejected buffer must not be adopted");

            // Cleanup runs AFTER the assertions, deliberately not in a `finally`. If the guard is gone the
            // layer adopts `mismatched`, and a finally disposing both the local and the layer would free the
            // same three NativeArrays twice — an ObjectDisposedException that REPLACES the assertion failure
            // and hides which property actually broke. A failing run leaking two buffers is the cheaper
            // trade (batch leak detection is off, and a red gate is not a shipping state).
            mismatched.Dispose();
            layer.Dispose();
        }

        [Test]
        public void MvtLayer_AdoptsItsGeometryExactlyOnce()
        {
            MvtLayer layer = LayerWith(2);
            TileGeometryBuffers first  = BufferFor(2);
            TileGeometryBuffers second = BufferFor(2);

            layer.AdoptGeometry(first);
            Assert.IsTrue(layer.Geometry.IsCreated, "precondition: the first adopt takes");

            Assert.Throws<InvalidOperationException>(() => layer.AdoptGeometry(second),
                "a second adopt would orphan the first buffer — MvtTile.Dispose frees only what the " +
                "layer currently holds, so the first allocation would leak with nothing able to reach it");

            // After the assertions, not in a `finally` — see the sibling test above for why (a finally would
            // double-free `second` once the guard is gone and mask the assertion with ObjectDisposedException).
            second.Dispose();
            layer.Dispose();
        }

        /// <summary>A feature-less layer legitimately adopts a <c>default</c> buffer (the materializer's
        /// own zero-feature result). Without this arm the two guards above could be satisfied by a rule that
        /// simply rejects everything falsy.</summary>
        [Test]
        public void MvtLayer_AdoptsTheEmptyBuffer_ForAFeatureLessLayer()
        {
            MvtLayer layer = LayerWith(0);
            Assert.DoesNotThrow(() => layer.AdoptGeometry(default));
            Assert.IsFalse(layer.Geometry.IsCreated, "an empty layer owns nothing to free");
            layer.Dispose();
        }

        /// <summary>The structural half: <c>Geometry</c> is no longer a writable field, so the only
        /// way in is the guarded one above. A behavioural tooth alone cannot see this — a second writer
        /// would simply bypass both guards.</summary>
        [Test]
        public void MvtLayer_Geometry_HasNoPubliclyWritableSetterOrField()
        {
            Assert.IsNull(typeof(MvtLayer).GetField("Geometry"),
                "Geometry must not be a public field — a field is assignable by anything and the lockstep " +
                "guard would be bypassable");

            System.Reflection.PropertyInfo prop = typeof(MvtLayer).GetProperty("Geometry");
            Assert.IsNotNull(prop, "Geometry must still be publicly READABLE — every consumer borrows it");
            Assert.IsNull(prop.GetSetMethod(nonPublic: false),
                "Geometry must have no public setter; AdoptGeometry is the guarded, set-once way in");
        }

        /// <summary>Disposal must survive the field→property change. <c>TileGeometryBuffers.Dispose</c>
        /// clears its own <c>IsCreated</c> to be idempotent, and a property getter hands out a COPY — so a
        /// naive <c>Geometry.Dispose()</c> frees the arrays and leaves the layer still claiming to own them,
        /// which the tile's second (documented-idempotent) dispose turns into a double free.</summary>
        [Test]
        public void MvtLayer_Dispose_IsIdempotent_AndForgetsTheBuffer()
        {
            MvtLayer layer = LayerWith(2);
            layer.AdoptGeometry(BufferFor(2));
            Assert.IsTrue(layer.Geometry.IsCreated, "precondition: the layer owns a real buffer");

            layer.Dispose();
            Assert.IsFalse(layer.Geometry.IsCreated,
                "after Dispose the layer must no longer claim ownership — otherwise the next Dispose double-frees");

            Assert.DoesNotThrow(() => layer.Dispose(), "Dispose is documented idempotent");
        }
    }
}
