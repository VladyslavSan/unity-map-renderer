// Unity EditMode only — NativeArray + the Burst RingAssemblyJob over the committed .pbf corpus.
// NOT registered in Tools/core-tests (Jobs/Collections).

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR B7a T0 — the property the whole byte-identity argument for earcut's <b>hole-bridge order</b> rests
    /// on: <b>every hole belongs to the same source-layer feature as its polygon's outer ring</b>.
    ///
    /// <para><b>Why it matters.</b> B7 replaces "the materializer receives exactly this layer's features" with
    /// "the buffer is shared and each consumer walks a ring <i>visit order</i>". Fill's visit order groups by
    /// feature and keeps decode order <i>within</i> a feature. The hole sort — <c>FillMeshPipeline.HoleRingComparer</c>,
    /// run from <c>FillGatherJob</c> on the graph (job-scheduling-design.md §8 stage 4 Group B retired the
    /// synchronous <c>FillMeshPipeline.Schedule</c> that used to run it directly) — tiebreaks on <b>ring
    /// index</b>, so bridge order is preserved iff the
    /// holes being compared are always rings whose relative order the derive did not disturb — which is true
    /// exactly when a polygon's holes share its outer ring's feature.</para>
    ///
    /// <para><b>The mechanism</b> (read <c>RingAssemblyJob.Execute</c>): <c>exteriorSign</c> is reset to 0
    /// whenever <c>RingFeatureIdx[ri] != prevFeature</c>, and <c>prevFeature</c> is updated <i>before</i> the
    /// area test — so the first area-surviving ring of a feature always takes the "first valid ring" branch,
    /// becomes an outer, and re-points <c>currentPolyIdx</c> into that feature. A hole can therefore only ever
    /// be attached to an outer of its own feature.</para>
    ///
    /// <para>This test is the <b>positive</b> check of that argument, run over real data rather than argued.
    /// It was run at baseline before B7a's first edit; it is kept because the property is cheap to state and
    /// expensive to rediscover.</para>
    /// </summary>
    [TestFixture]
    public class RingAssemblyHoleAttributionTests
    {
        [Test]
        public void HolesShareTheirOutersFeature_AcrossTheCommittedMvtCorpus()
        {
            string[] fixtures = Directory.GetFiles(
                Path.Combine(Application.dataPath, "Fixtures"), "*.pbf.bytes");
            Assert.Greater(fixtures.Length, 0, "precondition: the committed .pbf corpus must not be empty");

            int totalPolygons          = 0;
            int totalPolygonsWithHoles = 0;
            int totalHoles             = 0;
            int layersExamined         = 0;

            foreach (string path in fixtures)
            {
                TileId tileId = TileIdFromFixtureName(Path.GetFileName(path));
                using MvtTile tile = MvtDecoder.Decode(tileId, File.ReadAllBytes(path));

                foreach (string layerName in LayerNames(tile))
                {
                    ITileLayer layer = tile.GetLayer(layerName);
                    if (layer == null) continue;

                    // Exactly the feature set fill hands the materializer today, and exactly the set B7a's
                    // ring visit order will name: the layer's Polygon features, in layer order.
                    // IR C1 P3: the LAYER's own buffer — the whole layer, exactly what every consumer
                    // borrows. (Pre-P3 this re-materialized the polygon subset; the ring→feature attribution
                    // this tooth measures is per-feature and unaffected by the wider feature set, and the
                    // kind column keeps the non-polygon features out of the ring assembly.)
                    bool anyPolygon = false;
                    foreach (IFeature f in layer.Features)
                        if (f.GeometryType == TileGeometryType.Polygon) { anyPolygon = true; break; }
                    if (!anyPolygon) continue;

                    TileGeometryBuffers geometry = layer.Geometry;
                    if (!geometry.IsCreated) continue;
                    layersExamined++;

                    try
                    {
                        AssembleAndAssert(
                            geometry, $"{Path.GetFileName(path)}/{layerName}",
                            ref totalPolygons, ref totalPolygonsWithHoles, ref totalHoles);
                    }
                    finally
                    {
                        // BORROWED from the decoded layer (IR C1 P3) — the `using` on the tile frees it.
                    }
                }
            }

            // ── Anti-vacuity: the claim above must be about a NON-EMPTY set. ──────────────────────────
            // Without these three, a corpus that produced no polygons at all — or polygons but no holes —
            // would pass this test while saying nothing whatsoever about hole attribution (the
            // inert-injection shape: the fixture cannot express the defect).
            Assert.Greater(layersExamined, 0, "anti-vacuity: no layer in the corpus materialized any rings");
            Assert.Greater(totalPolygons, 0, "anti-vacuity: the corpus assembled zero polygons");
            Assert.Greater(totalPolygonsWithHoles, 0,
                "anti-vacuity: the corpus assembled polygons but NONE with holes — the attribution claim " +
                "would then be about an empty set. If this fires, the corpus changed; find a hole-bearing " +
                "fixture rather than deleting the clause.");
            Assert.Greater(totalHoles, 0, "anti-vacuity: zero holes were classified");

            Debug.Log($"[B7a T0] corpus: {layersExamined} layers, {totalPolygons} polygons, " +
                      $"{totalPolygonsWithHoles} with holes, {totalHoles} holes — all holes shared their " +
                      "outer's feature.");
        }

        private static void AssembleAndAssert(
            TileGeometryBuffers geometry, string where,
            ref int totalPolygons, ref int totalPolygonsWithHoles, ref int totalHoles)
        {
            int maxPolygons = math.max(1, geometry.RingCapacity);

            var polyOuterIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(maxPolygons, Allocator.Persistent);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent);

            try
            {
                new RingAssemblyJob
                {
                    Vertices             = geometry.Vertices,
                    RingOffsets          = geometry.RingOffsets,
                    RingFeatureIdx       = geometry.RingFeatureIdx,
                    RingCount            = geometry.RingCount,
                    FeatureGeometryType  = geometry.FeatureGeometryType,
                    OutPolyOuterRingIdx  = polyOuterIdx,
                    OutPolyHoleListStart = polyHoleStart,
                    OutPolyHoleCount     = polyHoleCount,
                    OutHoleRingIdxs      = holeRingIdxs,
                    OutPolygonCount      = polyCountArr,
                    OutHoleCount         = holeCountArr,
                }.Run();

                int polyCount = polyCountArr[0];
                totalPolygons += polyCount;

                for (int pi = 0; pi < polyCount; pi++)
                {
                    int outerFeature = geometry.RingFeatureIdx[polyOuterIdx[pi]];
                    int holeCount    = polyHoleCount[pi];
                    if (holeCount > 0) totalPolygonsWithHoles++;
                    totalHoles += holeCount;

                    for (int hi = 0; hi < holeCount; hi++)
                    {
                        int holeRi = holeRingIdxs[polyHoleStart[pi] + hi];
                        Assert.AreEqual(outerFeature, geometry.RingFeatureIdx[holeRi],
                            $"{where}: polygon {pi} (outer ring {polyOuterIdx[pi]}, feature {outerFeature}) " +
                            $"was given hole ring {holeRi}, which belongs to feature " +
                            $"{geometry.RingFeatureIdx[holeRi]}. Cross-feature hole attribution breaks the " +
                            "hole-bridge-order half of IR B7's byte-identity argument: the hole sort " +
                            "tiebreaks on ring index, which is only order-preserving within one feature.");
                    }
                }
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose();
            }
        }

        /// <summary>Every layer name the fixture actually carries — no hand-picked list, so a corpus change
        /// widens the check instead of silently narrowing it.</summary>
        private static IEnumerable<string> LayerNames(MvtTile tile)
        {
            foreach (MvtLayer layer in tile.Layers)
                yield return layer.Name;
        }

        /// <summary>The committed fixtures encode their address in the filename (<c>…-z-x-y.pbf.bytes</c>).
        /// The tile address only reaches <c>TileToGeoJob</c>, which this test does not run, but the
        /// materializer requires one and a wrong-but-consistent address would still be misleading.</summary>
        private static TileId TileIdFromFixtureName(string fileName)
        {
            string stem = fileName.Replace(".pbf.bytes", string.Empty);
            string[] parts = stem.Split('-');
            if (parts.Length >= 3 &&
                int.TryParse(parts[parts.Length - 3], out int z) &&
                int.TryParse(parts[parts.Length - 2], out int x) &&
                int.TryParse(parts[parts.Length - 1], out int y))
                return new TileId { Z = z, X = x, Y = y };

            return new TileId { Z = 0, X = 0, Y = 0 };
        }
    }
}
