// Unity EditMode only — NativeArray/NativeList + the Burst RingClipJob. The clipping arithmetic lives in
// MapRenderer.Jobs (no managed Core twin exists, and one whose only caller was a test would be production
// bloat), so these are its teeth. The knob's UNIT conversion is engine-free and tested in the fast loop
// (Tools/core-tests/TileBufferClipTests.cs).

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;

namespace MapRenderer.Tests
{
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
            MvtTile mvtTile = MvtDecoder.Decode(bytes);

            bool sawAnyLayer      = false;
            int  bufferedLayers   = 0;
            foreach (string layerName in new[] { "water", "park", "landuse", "landcover" })
            {
                var layer = mvtTile.GetLayer(layerName);
                if (layer == null) continue;

                var geoms = new List<uint[]>();
                foreach (var f in layer.Features)
                    if (f.GeometryType == TileGeometryType.Polygon && f.Geometry != null)
                        geoms.Add(f.Geometry);
                if (geoms.Count == 0) continue;
                sawAnyLayer = true;

                double extent = layer.Extent;
                var (bMin, _) = tileId.MercatorBounds();
                var baseInput = new FillMeshPipeline.LayerInput
                {
                    FeatureGeometries = geoms,
                    Extent            = extent,
                    Tile              = tileId,
                    OriginRender      = new double3(bMin.x, 0.0, bMin.y),
                };

                var unclippedInput = baseInput; unclippedInput.Clip = TileBufferClip.Disabled;
                var clippedInput   = baseInput; clippedInput.Clip   = TileBufferClip.KeepTileUnits(0.0);

                TileMeshBuffers unclipped = FillMeshPipeline.Schedule(unclippedInput);
                TileMeshBuffers clipped   = FillMeshPipeline.Schedule(clippedInput);
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
                    Assert.LessOrEqual(clipped.TotalForceClipCount, unclipped.TotalForceClipCount,
                        $"{fixture}/{layerName}: clipping raised the earcut force-clip count from " +
                        $"{unclipped.TotalForceClipCount} to {clipped.TotalForceClipCount}. That is a finding " +
                        "about the clipper (the degenerate-channel case), not a licence to retune earcut.");

                    Debug.Log($"[RingClipJob T4] {fixture}/{layerName}: verts {unclipped.VertexCount[0]} → " +
                              $"{clipped.VertexCount[0]}, forceClips {unclipped.TotalForceClipCount} → " +
                              $"{clipped.TotalForceClipCount}");
                }
                finally
                {
                    unclipped.Dispose();
                    clipped.Dispose();
                }
            }

            Assert.IsTrue(sawAnyLayer, $"{fixture}: no polygon layer found — the corpus case did not run.");
            Assert.Greater(bufferedLayers, 0,
                $"{fixture}: NO layer's unclipped mesh has an out-of-extent vertex — the fixture is not " +
                "buffered, so assertion (a) above passed without the clip ever having to cut anything.");
        }

        private static int CountOutsideExtent(TileMeshBuffers buffers, double extent)
        {
            const double eps = 1e-9;
            int count = 0;
            int n = buffers.VertexCount[0];
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

        private static ClipResult RunClip(double2[][] rings, TileBufferClip clip)
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
                ringFeatIdx[ri] = 0; // one feature — the hole tooth relies on shared feature grouping
                foreach (var v in rings[ri]) verts[pos++] = v;
            }
            ringOffsets[rings.Length] = pos;

            int scratchCap = math.max(1, maxRingLen * RingClipJob.ScratchLengthMultiplier);
            var scratchA = new NativeArray<double2>(scratchCap, Allocator.Persistent);
            var scratchB = new NativeArray<double2>(scratchCap, Allocator.Persistent);

            var result = new ClipResult
            {
                Vertices       = new NativeList<double2>(math.max(1, totalVerts), Allocator.Persistent),
                RingOffsets    = new NativeList<int>(rings.Length + 1, Allocator.Persistent),
                RingFeatureIdx = new NativeList<int>(math.max(1, rings.Length), Allocator.Persistent),
            };

            new RingClipJob
            {
                Vertices          = verts,
                RingOffsets       = ringOffsets,
                RingFeatureIdx    = ringFeatIdx,
                RingCount         = rings.Length,
                ClipMin           = clipMin,
                ClipMax           = clipMax,
                ScratchA          = scratchA,
                ScratchB          = scratchB,
                OutVertices       = result.Vertices,
                OutRingOffsets    = result.RingOffsets,
                OutRingFeatureIdx = result.RingFeatureIdx,
            }.Run();

            verts.Dispose(); ringOffsets.Dispose(); ringFeatIdx.Dispose();
            scratchA.Dispose(); scratchB.Dispose();
            return result;
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

            new RingAssemblyJob
            {
                Vertices             = verts,
                RingOffsets          = ringOffsets,
                RingFeatureIdx       = ringFeatIdx,
                RingCount            = assembleRings.Length,
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
}
