// Unity EditMode only — NativeArray/NativeList, Burst jobs, UnityEngine.Application. NOT registered in
// core-tests.csproj (FillMeshGraph lives in Jobs, which core-tests does not compile).
//
// job-scheduling-design.md §8 stage 4 / §3.7 — the curved-arm subdivide sub-chain. This file checks two
// things: FillMeshGraph.Schedule's curved arm (WorldPositions/VertexUp/VertexEast/TileVertices/
// VertexFeatureIdx/TriangleIndices — §3.7's one-column-set output) over the whole fixture corpus at
// Projection = SphericalProjection; and the vertex budget still binds through the SCHEDULED dispatcher
// (FillMeshGraph.Schedule itself has no budget override — it always reads GlobeFillSubdivideDispatch's own
// Default* constants — so this drives GlobeFillSubdivideDispatch.Schedule directly, the same dispatcher the
// graph uses).
//
// R6's extension (this file is not itself R6's named site, but shares its exact defect shape — see Group B's
// stage brief): the first check used to be a LIVE differential against WriteGlobeSubdivided's own call —
// GlobeFillSubdivideDispatch.Run over FillMeshPipeline.Schedule's output. Group B deletes BOTH
// FillMeshPipeline.Schedule and GlobeFillSubdivideDispatch.Run (the plan's B.3/B.4), so this file cannot even
// compile against them any more — and comparing FillMeshGraph.Schedule's curved arm against itself would be
// exactly the self-referential-oracle shape R6 names for fill's flat arm. A per-stream SHA-256 golden captured
// from the independent oracle BEFORE Group B deleted it (docs/stage4-groupb-goldens-capture-f5e13c19.txt's SITE2 lines, commit
// f5e13c19) replaces the differential below, same reasoning as R6/B.7.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class FillMeshGraphGlobeParityTests
    {
        private static readonly Regex TileIdFromName = new Regex(@"-(\d+)-(\d+)-(\d+)\.pbf\.bytes$");

        // ── (a) Golden parity of the subdivided output, over the whole fixture corpus (R6-shaped fix). ─────

        // Frozen golden (R6-shaped): captured from GlobeFillSubdivideDispatch.Run over
        // FillMeshPipeline.Schedule's output — exactly WriteGlobeSubdivided's own call — before Group B
        // deleted both. Commit f5e13c19; see docs/stage4-groupb-goldens-capture-f5e13c19.txt's SITE2 lines.
        private const string FrozenGoldens =
            "Vertex=AcGbVDk4/mSwr5eI+Cc8ayWxBemQB203q8UtpuRbBo8= Indices=btY5MlGiZZ+zg4jLxLT6c7aVaVtqQUzZgBA8dcb8iWM=";

        [Test]
        public void Schedule_MatchesFrozenSubdivideGoldens_AcrossCorpus()
        {
            string fixturesDir = Path.Combine(Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var fixturesCovered = new HashSet<string>();
            int layersChecked = 0;
            bool anySplitFired = false;
            var vertBytes = new List<byte>();
            var idxBytes  = new List<byte>();

            foreach (string path in allPaths)
            {
                string fileName = Path.GetFileName(path);
                Assert.IsTrue(File.Exists(path), $"fixture missing: {path}");
                byte[] bytes = File.ReadAllBytes(path);

                TileId tileId;
                Match m = TileIdFromName.Match(fileName);
                tileId = m.Success
                    ? new TileId { Z = int.Parse(m.Groups[1].Value), X = int.Parse(m.Groups[2].Value), Y = int.Parse(m.Groups[3].Value) }
                    : new TileId { Z = 0, X = 0, Y = 0 };

                using var mvtTile = MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    TileGeometryBuffers geometry = layer.Geometry;
                    if (!HasPolygonFeature(geometry)) continue;

                    layersChecked++;
                    fixturesCovered.Add(fileName);

                    var (bMin, _) = tileId.MercatorBounds();
                    NativeArray<int> visitOrder = MapRenderer.Tests.TestTileMeshBuilder.FullVisitOrder(geometry);
                    var input = new FillMeshPipeline.LayerInput
                    {
                        Geometry       = geometry,
                        RingVisitOrder = visitOrder,
                        OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                        Projection     = new SphericalProjection(),
                    };

                    // Arm 1: clip disabled (default) — what this sweep drove before.
                    input.Clip = default;
                    if (Accumulate(input, vertBytes, idxBytes))
                        anySplitFired = true;

                    // Arm 2: clip ENABLED — the arm production actually takes on a curved projection
                    // (MapViewConfig.FillTileBufferClip = 0.0 decodes to KeepTileUnits(0.0), not Disabled;
                    // this test uses 64 so TryWindow provably enters). Curved + clip-enabled is exactly what
                    // the only shipped scene (OpenStreetMapLiberty, UseGlobe: 1) runs, and before the §3.7
                    // reshape it had geometry parity coverage only incidentally, through the pre-subdivision
                    // columns FillMeshGraphParityTests used to compare — which no longer exist post-reshape.
                    // Assert the enabled arm was actually ENTERED, not merely configured — the same rule
                    // FillMeshGraphParityTests already applies.
                    input.Clip = TileBufferClip.KeepTileUnits(64.0);
                    Assert.IsTrue(input.Clip.TryWindow(geometry.Extent, out _, out _),
                        $"precondition: [{fileName}/{layer.Name}] the enabled clip arm must actually enter TryWindow");
                    if (Accumulate(input, vertBytes, idxBytes))
                        anySplitFired = true;

                    visitOrder.Dispose();
                }
            }

            Assert.GreaterOrEqual(fixturesCovered.Count, 4,
                "precondition: the corpus sweep must cover at least 4 fixtures (guards a path/glob typo)");
            Assert.Greater(layersChecked, 0, "precondition: the enumerated (fixture, layer) set is non-empty");
            Assert.IsTrue(anySplitFired,
                "at least one layer must genuinely subdivide (subdivided triangle count exceeds source " +
                "triangle count) — the pass-through path already emits 3 verts/triangle with no dedup, so a " +
                "sweep that never exceeds that ratio would be measuring the no-op path only");

            string result = $"Vertex={Sha256(vertBytes)} Indices={Sha256(idxBytes)}";
            Assert.AreEqual(FrozenGoldens, result,
                "the graph's curved-arm subdivided output across the corpus no longer matches the frozen " +
                "GlobeFillSubdivideDispatch.Run/FillMeshPipeline.Schedule goldens — a real regression, not a re-bake candidate.");
        }

        /// <summary>Schedules the graph over <paramref name="input"/> (curved arm), appends its subdivided
        /// World/Up/East/Tile/Feature vertex columns + triangle indices into the running accumulators (same
        /// fixed corpus order the caller iterates in — the golden is a digest over that order), and returns
        /// whether this layer's subdivided triangle count exceeded its SOURCE (pre-subdivision) triangle
        /// count — a genuine split. The source count comes from a second, flat-arm Schedule call over the
        /// SAME geometry: triangulation runs identically before the arm split
        /// (job-scheduling-design.md §3.7), so the flat arm's TriangleIndices.Length IS the curved arm's
        /// pre-subdivision count, with no need for the retired synchronous pipeline as a witness.
        ///
        /// <para>The extra schedule is <b>deliberate, not an oversight</b>: neither <see cref="FillGraphOutput"/>
        /// nor <see cref="FillGraphCounts"/> carries a pre-subdivision triangle count on the curved arm (Counts'
        /// four fields stop at Polygon/Ring/Hole/ForceClip — see <c>FillMeshGraphParityTests</c>'s header), so
        /// there is no cheaper read of that quantity than re-deriving it from a second, flat-projection
        /// Schedule over the identical input.</para></summary>
        private static bool Accumulate(FillMeshPipeline.LayerInput input, List<byte> vertBytes, List<byte> idxBytes)
        {
            FillMeshPipeline.LayerInput flatInput = input;
            flatInput.Projection = new WebMercatorProjection();
            FillGraphOutput flatOutput = FillMeshGraph.Schedule(flatInput);
            flatOutput.Handle.Complete();
            int sourceTriCount = flatOutput.IsCreated ? flatOutput.TriangleIndices.Length / 3 : 0;
            flatOutput.Dispose();

            FillGraphOutput graphOutput = FillMeshGraph.Schedule(input);
            graphOutput.Handle.Complete();
            try
            {
                if (!graphOutput.IsCreated) return false; // nothing to draw

                int subdividedTriCount = graphOutput.TriangleIndices.Length / 3;
                bool split = subdividedTriCount > sourceTriCount;

                int n = graphOutput.WorldPositions.Length;
                for (int i = 0; i < n; i++)
                {
                    double3 world = graphOutput.WorldPositions[i], up = graphOutput.VertexUp[i], east = graphOutput.VertexEast[i];
                    double2 tile  = graphOutput.TileVertices[i];
                    vertBytes.AddRange(BitConverter.GetBytes(world.x)); vertBytes.AddRange(BitConverter.GetBytes(world.y)); vertBytes.AddRange(BitConverter.GetBytes(world.z));
                    vertBytes.AddRange(BitConverter.GetBytes(up.x));    vertBytes.AddRange(BitConverter.GetBytes(up.y));    vertBytes.AddRange(BitConverter.GetBytes(up.z));
                    vertBytes.AddRange(BitConverter.GetBytes(east.x));  vertBytes.AddRange(BitConverter.GetBytes(east.y));  vertBytes.AddRange(BitConverter.GetBytes(east.z));
                    vertBytes.AddRange(BitConverter.GetBytes(tile.x));  vertBytes.AddRange(BitConverter.GetBytes(tile.y));
                    vertBytes.AddRange(BitConverter.GetBytes(graphOutput.VertexFeatureIdx[i]));
                }
                for (int i = 0; i < graphOutput.TriangleIndices.Length; i++)
                    idxBytes.AddRange(BitConverter.GetBytes(graphOutput.TriangleIndices[i]));

                return split;
            }
            finally { graphOutput.Dispose(); }
        }

        // ── (b) The vertex budget still binds, through the SCHEDULED dispatcher. ───────────────────────────

        /// <summary>Mirrors <c>GlobeFillSubdividerTests.Budget_BoundsTheOutput_NoLowZoomExplosion</c>, but
        /// through <c>GlobeFillSubdivideDispatch.Schedule</c> — the same scheduled dispatcher
        /// <c>FillMeshGraph.cs</c> uses. <c>FillMeshGraph.Schedule</c> itself has no budget parameter (it
        /// always reads <c>GlobeFillSubdivideDispatch</c>'s own <c>Default*</c> constants), so this pins the
        /// BOUND on the scheduled path directly rather than through the whole graph. Per design §5.2's
        /// Budget row: the default 200 000 is never reached in production; this is the bound, not a live
        /// production path.</summary>
        [Test]
        public void Budget_StillBinds_ThroughTheScheduledDispatcher()
        {
            const double extent = 4096.0;
            var id = new TileId { Z = 0, X = 0, Y = 0 };

            var tileVertsList = new NativeList<double2>(Allocator.Persistent);
            var triangleIndicesList = new NativeList<int>(Allocator.Persistent);
            var vertexFeatureIdxList = new NativeList<int>(Allocator.Persistent);
            tileVertsList.Add(new double2(0, 0)); tileVertsList.Add(new double2(extent, 0)); tileVertsList.Add(new double2(0, extent));
            triangleIndicesList.Add(0); triangleIndicesList.Add(1); triangleIndicesList.Add(2);
            vertexFeatureIdxList.Add(0); vertexFeatureIdxList.Add(0); vertexFeatureIdxList.Add(0);

            var outVerts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            var outIndices = new NativeList<int>(64, Allocator.Persistent);

            try
            {
                // depth 8 unbounded ≈ 4^8·3 ≈ 196k verts for ONE whole-globe triangle; the 2 000 budget must cap it.
                JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                    new SphericalProjection(), tileVertsList, triangleIndicesList, vertexFeatureIdxList,
                    id, extent, double3.zero,
                    GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, 8, 2000,
                    outVerts, outIndices, default);
                JobHandle.ScheduleBatchedJobs();
                handle.Complete();

                Assert.Less(outVerts.Length, 20000, "the vertex budget must prevent the low-zoom subdivision explosion");
                Assert.Greater(outVerts.Length, 0, "the layer must still produce output even when bounded");
            }
            finally
            {
                tileVertsList.Dispose(); triangleIndicesList.Dispose(); vertexFeatureIdxList.Dispose();
                outVerts.Dispose(); outIndices.Dispose();
            }
        }

        // ── Shared helpers ─────────────────────────────────────────────────────────────────────────────

        private static bool HasPolygonFeature(TileGeometryBuffers geometry)
        {
            for (int fi = 0; fi < geometry.FeatureCount; fi++)
                if (geometry.FeatureGeometryType[fi] == TileGeometryType.Polygon) return true;
            return false;
        }

        private static string Sha256(List<byte> bytes)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }
}
