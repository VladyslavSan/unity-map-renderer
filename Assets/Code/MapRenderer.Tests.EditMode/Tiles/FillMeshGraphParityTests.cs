// Unity EditMode only — NativeArray/NativeList, Burst jobs, UnityEngine.Application. NOT registered in
// core-tests.csproj (FillMeshGraph lives in Jobs, which core-tests does not compile).
//
// job-scheduling-design.md §8 stage 4 Group B (R6): this used to be a LIVE differential —
// FillMeshGraph.Schedule(...).Complete() checked against FillMeshPipeline.Schedule(...) — across all five
// output arrays (vertices, world positions, up, per-vertex feature index, indices) plus the ring/hole/
// polygon/force-clip counts, over the whole fixture corpus AND a synthetic hole-bearing layer (so the
// gather's sort is actually exercised), over BOTH the disabled and the enabled tile-buffer-clip arm (production
// runs the ENABLED arm — MapViewConfig.FillTileBufferClip = 0.0 decodes to KeepTileUnits(0.0), not Disabled).
// Group B deletes FillMeshPipeline.Schedule — the synchronous pipeline FillMeshGraph.cs's own doc named as the
// thing that "stays the parity oracle" — so comparing the graph against itself here would be a self-referential
// oracle (green forever, proves nothing). Per-stream SHA-256 digests captured from FillMeshPipeline.Schedule's
// output BEFORE Group B deleted it are pinned below as frozen goldens instead: a regression pin, not a live
// comparison. Provenance: captured on commit f5e13c19 (stage 4 Group A; 973afaac is a docs-only descendant),
// via a throwaway capture harness run once and discarded — see docs/stage4-groupb-goldens-capture-f5e13c19.txt's
// SITE1 lines (the values below are those lines, copied in).
//
// Per-stream (N1): nine separate digests for the flat (WebMercator) arm — one per output array plus one per
// count — so a red names WHICH stream moved, not just "fill geometry changed". The spherical arm compares only
// the four counts: AggregateJob/SizingJob compute them from the PRE-subdivision earcut output on both
// arms (unaffected by §3.7's curved-arm reshape), but the five geometry arrays are POST-subdivision there and
// no longer the same quantity FillMeshPipeline.Schedule returns — this file never compared them on the curved
// arm even before Group B (see the old AssertParity's `if (curved) return;`).
//
// This tooth is RED-verified by perturbing the aggregate's index rebase by one and by dropping the gather's
// hole-ring tie-break — executed manually, one at a time, and reverted; not left in this file.

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
using MapRenderer.Jobs.Projection;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class FillMeshGraphParityTests
    {
        // Matches "<...>-<z>-<x>-<y>.pbf.bytes" — every committed fixture's naming convention.
        internal static readonly Regex TileIdFromName = new Regex(@"-(\d+)-(\d+)-(\d+)\.pbf\.bytes$");

        // WebMercator / Spherical — the two REAL projections ProjectionDispatch.Schedule's switch
        // enumerates. `null` used to be a third case here (the switch's own `case null` default), but null
        // is no longer a valid dispatch arm — ProjectionDispatch now throws on it (the default now lives
        // once, in TileManager.TickCore) — so a parity SWEEP has nothing to compare there any more; the
        // throw-on-null contract itself is asserted separately, by
        // ProjectionDispatch_Schedule_RejectsNull below.
        internal static readonly IProjection[] ProjectionCases = { new WebMercatorProjection(), new SphericalProjection() };

        private static string ProjectionName(IProjection p) => p.GetType().Name;

        // ── Frozen goldens (R6) — captured from FillMeshPipeline.Schedule on commit f5e13c19, before Group B
        // deleted it. See this file's header comment for the capture methodology and provenance.
        internal const string FrozenGoldensMercator =
            "Vertex=BGT9vCbMRHS/Ni3PF1gWcHP4HK8NZKoEh+0AX5qcjyY= World=e5Kjjs7aiA1F+5QY8M53IoLBxLSTuEKehkCzPIMXCO8= " +
            "Up=8j1uwFahQZ2mr/d02vWtnxyHtzu+DFiN5jMA/6NRO/k= FeatIdx=FufcsnPOzP5M4IKJAwSFCfn8TPyaCN/bm14hxU4Cs3Q= " +
            "Indices=NHdZriRpUzF7iiWCExTHR6Iz5GwlLlBkZiyA/dG8Cdk= PolyCount=GPT25e2QoKuZlvyLJr217Hg+BzNu4tsF/xTSGD8EEno= " +
            "RingCount=dQChKlRF3Q9rhXNaIn6SihAPsssTMDHD5yXoYuxCvLE= HoleCount=aerOkFJ48oBOi7mWf7Zq9gT/lhB5U+vlf3fz+yjaZeQ= " +
            "ForceClip=vZan4ovogL6+dDbAOpmv82Z1gd89/Piwz4fRI1hmd8w=";
        internal const string FrozenGoldensSpherical =
            "PolyCount=GPT25e2QoKuZlvyLJr217Hg+BzNu4tsF/xTSGD8EEno= RingCount=dQChKlRF3Q9rhXNaIn6SihAPsssTMDHD5yXoYuxCvLE= " +
            "HoleCount=aerOkFJ48oBOi7mWf7Zq9gT/lhB5U+vlf3fz+yjaZeQ= ForceClip=vZan4ovogL6+dDbAOpmv82Z1gd89/Piwz4fRI1hmd8w=";

        /// <summary>Walks the same (corpus fixture × 2 clip arms) + 1 synthetic-hole-layer sequence, in the
        /// same fixed order, that <see cref="Schedule_MatchesFrozenSynchronousPipelineGoldens_AcrossCorpusAndSyntheticLayer"/>
        /// captured its frozen goldens against — extracted so <c>GraphDeterminismTests</c> (job-scheduling-
        /// design.md §8 stage 6, B.5) can reproduce the EXACT same input sequence its own golden-equality
        /// assertion needs, rather than a second hand-typed copy that could silently drift from this one.
        /// <paramref name="visit"/> receives each case's <see cref="FillMeshPipeline.LayerInput"/> in
        /// accumulation order; the two corpus-coverage preconditions (at least 4 fixtures, at least one
        /// layer) are asserted here, once, for every caller.</summary>
        internal static void WalkCorpusAndSynthetic(IProjection projection, Action<FillMeshPipeline.LayerInput> visit)
        {
            string fixturesDir = Path.Combine(Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var fixturesCovered = new HashSet<string>();
            int layersChecked = 0;

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
                    var baseInput = new FillMeshPipeline.LayerInput
                    {
                        Geometry       = geometry,
                        RingVisitOrder = visitOrder,
                        OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                        Projection     = projection,
                    };

                    // Arm 1: clip disabled (default) — every prior version of this tooth ran only this arm.
                    baseInput.Clip = default;
                    visit(baseInput);

                    // Arm 2: clip ENABLED — the arm production actually takes (MapViewConfig.cs:98 +
                    // TileBufferClip.FromInspectorUnits: 0.0 decodes to KeepTileUnits(0.0), not Disabled).
                    // Assert the enabled arm truly entered TryWindow, not merely that Clip was configured —
                    // a corpus sweep that never enters the branch it claims to cover is this repo's
                    // most-catalogued false green.
                    baseInput.Clip = TileBufferClip.KeepTileUnits(64.0);
                    Assert.IsTrue(baseInput.Clip.TryWindow(geometry.Extent, out _, out _),
                        $"precondition: [{fileName}/{layer.Name}] the enabled clip arm must actually enter TryWindow");
                    visit(baseInput);

                    visitOrder.Dispose();
                }
            }

            Assert.GreaterOrEqual(fixturesCovered.Count, 4,
                "precondition: the corpus sweep must cover at least 4 fixtures (guards a path/glob typo)");
            Assert.Greater(layersChecked, 0, "precondition: the enumerated (fixture, layer) set is non-empty");

            // ── Synthetic hole-bearing layer — guarantees a >=2-hole polygon regardless of the corpus. Last
            // in accumulation order, fixed — see the callers' own docs. ─────────────────────────────────────
            TileGeometryBuffers synthetic = SyntheticHoleBearingLayer(new TileId { Z = 0, X = 0, Y = 0 });
            try
            {
                Assert.IsTrue(AnyPolygonHasAtLeastTwoHoles(synthetic), "precondition: the synthetic layer has a >=2-hole polygon");

                NativeArray<int> visitOrder = MapRenderer.Tests.TestTileMeshBuilder.FullVisitOrder(synthetic);
                var input = new FillMeshPipeline.LayerInput
                {
                    Geometry       = synthetic,
                    RingVisitOrder = visitOrder,
                    OriginRender   = new double3(0.0, 0.0, 0.0),
                    Projection     = projection,
                };
                visit(input);
                visitOrder.Dispose();
            }
            finally { synthetic.Dispose(); }
        }

        [Test]
        public void Schedule_MatchesFrozenSynchronousPipelineGoldens_AcrossCorpusAndSyntheticLayer(
            [ValueSource(nameof(ProjectionCases))] IProjection projection)
        {
            bool curved = !double.IsInfinity(projection.MaxRefineAngleRad);

            var vertexBytes = new List<byte>(); var worldBytes = new List<byte>(); var upBytes = new List<byte>();
            var featBytes = new List<byte>(); var idxBytes = new List<byte>();
            var polyBytes = new List<byte>(); var ringBytes = new List<byte>(); var holeBytes = new List<byte>(); var fcBytes = new List<byte>();

            // Accumulates one case's per-stream bytes, in the SAME fixed order WalkCorpusAndSynthetic visits
            // cases — the golden is a running digest over that order, so the order itself is part of the pin.
            void Accumulate(FillMeshPipeline.LayerInput input)
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                output.Handle.Complete();
                try
                {
                    // FillMeshGraph.Schedule only returns !IsCreated when Geometry/RingVisitOrder aren't
                    // created or RingVisitOrder is empty (FillGraphOutput.cs's own doc). Every caller of
                    // Accumulate has already ruled that out — HasPolygonFeature (corpus arm) and
                    // AnyPolygonHasAtLeastTwoHoles (synthetic arm) both require a surviving polygon feature,
                    // which means a non-empty RingVisitOrder — so Counts is always populated here. Asserted,
                    // not silently normalized to zero: an uncreated output reaching this line would mean one
                    // of those preconditions broke, which is worth a loud failure, not a quietly-absorbed one.
                    Assert.IsTrue(output.IsCreated, "precondition: Accumulate's callers guarantee non-empty geometry");
                    polyBytes.AddRange(BitConverter.GetBytes(output.Counts[0].PolygonCount));
                    ringBytes.AddRange(BitConverter.GetBytes(output.Counts[0].RingCount));
                    holeBytes.AddRange(BitConverter.GetBytes(output.Counts[0].HoleCount));
                    fcBytes.AddRange(BitConverter.GetBytes(output.Counts[0].ForceClipCount));
                    if (curved) return; // §3.7: the curved arm's five geometry arrays are POST-subdivision —
                                         // not the same quantity FillMeshPipeline.Schedule returned; never pinned here.

                    // The INTERIOR prefix only, never the whole column set. FillBandJob appends the outward
                    // boundary band to these same columns, and the frozen goldens below are the digest of what
                    // the retired synchronous pipeline produced — the interior. Narrowing keeps every constant
                    // byte-identical (a digest is the one artifact a re-bake cannot be reviewed), and turns
                    // this tooth into the stronger claim: the band perturbed NOTHING the interior owns.
                    // Band vertices are always the suffix; band triangles are interleaved per feature, so
                    // they are filtered by vertex index rather than by position.
                    int vc = output.TileVertices.Length - output.Counts[0].BandVertexCount;
                    int ic = output.TriangleIndices.Length;
                    Assert.GreaterOrEqual(vc, 0, "the band cannot claim more vertices than the layer has");
                    for (int i = 0; i < vc; i++)
                    {
                        vertexBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].x));
                        vertexBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].y));
                        worldBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].x));
                        worldBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].y));
                        worldBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].z));
                        upBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].x));
                        upBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].y));
                        upBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].z));
                        featBytes.AddRange(BitConverter.GetBytes(output.VertexFeatureIdx[i]));
                    }
                    for (int i = 0; i + 2 < ic; i += 3)
                    {
                        if (output.TriangleIndices[i] >= vc || output.TriangleIndices[i + 1] >= vc || output.TriangleIndices[i + 2] >= vc)
                            continue;
                        idxBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i]));
                        idxBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i + 1]));
                        idxBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i + 2]));
                    }
                }
                finally { output.Dispose(); }
            }

            WalkCorpusAndSynthetic(projection, Accumulate);

            string countsResult =
                $"PolyCount={Sha256(polyBytes)} RingCount={Sha256(ringBytes)} HoleCount={Sha256(holeBytes)} ForceClip={Sha256(fcBytes)}";
            if (curved)
            {
                Assert.AreEqual(FrozenGoldensSpherical, countsResult,
                    $"[proj={ProjectionName(projection)}] the graph's counts across the corpus + synthetic layer " +
                    "no longer match the frozen FillMeshPipeline.Schedule goldens — a real regression, not a re-bake candidate.");
                return;
            }

            string geometryResult =
                $"Vertex={Sha256(vertexBytes)} World={Sha256(worldBytes)} Up={Sha256(upBytes)} FeatIdx={Sha256(featBytes)} " +
                $"Indices={Sha256(idxBytes)} {countsResult}";
            Assert.AreEqual(FrozenGoldensMercator, geometryResult,
                $"[proj={ProjectionName(projection)}] the graph's output across the corpus + synthetic layer no " +
                "longer matches the frozen FillMeshPipeline.Schedule goldens — a real regression, not a re-bake candidate.");
        }

        // ── (e) Projection dispatch coverage — job-scheduling-design.md §8 stage 4. ─────────────────────────
        //
        // Deliberately NOT a corpus sweep: this is about ProjectionDispatch.Schedule's DISPATCH being wired
        // to the right struct, not about coverage breadth — Tiles/FillMeshGraphGlobeParityTests.cs's corpus
        // parity check already sweeps the whole corpus under SphericalProjection for the (separate) subdivide
        // dispatcher. One small layer, a real non-zero origin (a zero origin would leave the sphere check
        // vacuous — World is already origin-relative, so "add the origin back" only matters when it moves
        // the point).
        //
        // Two assertions, two subjects, neither borrowing the other's machinery: a null projection reaching
        // FillMeshGraph.Schedule mid-way (after RingSelect/RingClip/RingAssembly/sizing/gather/earcut/
        // aggregate have already scheduled, all holding the caller's geometry as a live [ReadOnly] input)
        // would strand those jobs when the dispatch throws — no terminal handle is ever constructed to
        // Complete() them, so the caller's own cleanup then throws trying to dispose geometry the safety
        // system still considers in flight (the bystander-fault signature, not the real defect — see
        // FillMeshGraph.Schedule's own up-front null guard, which exists precisely so this never reaches the
        // dispatch that way). So the null-rejection claim below is tested at the unit level, directly against
        // ProjectionDispatch.Schedule, with no graph and nothing to strand.

        [Test]
        public void ProjectionDispatch_Schedule_RejectsNull()
        {
            // The null check is the FIRST statement in Schedule's switch, before any list is touched, so
            // this needs no allocation and no cleanup — nothing is ever scheduled.
            var ex = Assert.Throws<System.NotSupportedException>(
                () => ProjectionDispatch.Schedule(null, double3.zero, default, default, default, default),
                "ProjectionDispatch.Schedule must reject a null projection outright.");
            Assert.That(ex.Message, Does.Contain("null"),
                "the exception must name WHY it was rejected (a null projection), not just that dispatch " +
                "failed for some unspecified reason.");
        }

        [Test]
        public void ProjectionDispatch_SphericalArmIsEntered()
        {
            TileGeometryBuffers geometry = SyntheticHoleBearingLayer(new TileId { Z = 3, X = 3, Y = 3 });
            try
            {
                NativeArray<int> visitOrder = MapRenderer.Tests.TestTileMeshBuilder.FullVisitOrder(geometry);
                try
                {
                    var origin = new double3(1000.0, 0.0, 2000.0);
                    var baseInput = new FillMeshPipeline.LayerInput
                    {
                        Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = origin,
                    };
                    var mercatorInput = baseInput; mercatorInput.Projection = new WebMercatorProjection();
                    var sphericalInput = baseInput; sphericalInput.Projection = new SphericalProjection();

                    FillGraphOutput mercatorOut = default, sphericalOut = default;
                    try
                    {
                        mercatorOut = FillMeshGraph.Schedule(mercatorInput);
                        sphericalOut = FillMeshGraph.Schedule(sphericalInput);
                        mercatorOut.Handle.Complete(); sphericalOut.Handle.Complete();

                        string mercatorHash  = HashDouble3ArrayFromList(mercatorOut.WorldPositions, mercatorOut.WorldPositions.Length);
                        string sphericalHash = HashDouble3ArrayFromList(sphericalOut.WorldPositions, sphericalOut.WorldPositions.Length);

                        // Precondition: an empty spherical arm would make the AreNotEqual below fail for an
                        // uninformative reason (two empty-list hashes trivially match) instead of naming the
                        // real problem. Checked before it fires, not after.
                        int n = sphericalOut.WorldPositions.Length;
                        Assert.Greater(n, 0, "precondition: the spherical arm produced vertices");

                        // The spherical branch was ENTERED, not merely configured.
                        Assert.AreNotEqual(mercatorHash, sphericalHash,
                            "the spherical arm's world positions must differ from the Mercator arm's — a " +
                            "dispatch that silently fell through to Web Mercator would still pass this check");

                        for (int i = 0; i < n; i++)
                        {
                            double3 absolute = sphericalOut.WorldPositions[i] + origin;
                            Assert.That(math.length(absolute), Is.EqualTo(SphericalProjection.Radius).Within(SphericalProjection.Radius * 1e-6),
                                $"vertex {i}: every absolute spherical vertex must lie on the sphere of SphericalProjection.Radius");
                        }
                    }
                    finally
                    {
                        mercatorOut.Dispose(); sphericalOut.Dispose();
                    }
                }
                finally { visitOrder.Dispose(); }
            }
            finally { geometry.Dispose(); }
        }

        // ── Synthetic layer builder — TileGeometryBuffers.Allocate + element writes (no MVT authoring). ────

        /// <summary>One outer square + three holes at distinct leftmost-x (10, 40, 70), all strictly inside
        /// the outer ring — exercises <c>FillGatherJob</c>'s hole sort (holeCount &gt; 1).</summary>
        private static TileGeometryBuffers SyntheticHoleBearingLayer(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 4, maxVertices: 16);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;

            int vi = 0, ri = 0;
            void Ring(double2 a, double2 b, double2 c, double2 d)
            {
                g.RingOffsets[ri] = vi;
                g.Vertices[vi++] = a; g.Vertices[vi++] = b; g.Vertices[vi++] = c; g.Vertices[vi++] = d;
                g.RingFeatureIdx[ri] = 0;
                ri++;
            }

            // Outer: CW-in-Y-down (positive shoelace area2) — the MVT exterior sign.
            Ring(new double2(0, 0), new double2(100, 0), new double2(100, 100), new double2(0, 100));
            // Holes: opposite sign (negative area2), fully inside the outer square, distinct leftmost-x.
            Ring(new double2(10, 10), new double2(10, 20), new double2(20, 20), new double2(20, 10));
            Ring(new double2(40, 10), new double2(40, 20), new double2(50, 20), new double2(50, 10));
            Ring(new double2(70, 10), new double2(70, 20), new double2(80, 20), new double2(80, 10));
            g.RingOffsets[ri] = vi; // trailing sentinel

            g.RingCount   = ri;
            g.VertexCount = vi;
            return g;
        }

        // ── Corpus predicates ──────────────────────────────────────────────────────────────────────────

        internal static bool HasPolygonFeature(TileGeometryBuffers geometry)
        {
            for (int fi = 0; fi < geometry.FeatureCount; fi++)
                if (geometry.FeatureGeometryType[fi] == TileGeometryType.Polygon) return true;
            return false;
        }

        /// <summary>Reads <c>RingAssemblyJob</c>'s own per-polygon hole count directly (a LAYER total from
        /// <see cref="FillGraphCounts.HoleCount"/> cannot distinguish "many 1-hole polygons" from "one 3-hole
        /// polygon" — only this per-polygon read can).</summary>
        private static bool AnyPolygonHasAtLeastTwoHoles(TileGeometryBuffers geometry)
        {
            if (!geometry.IsCreated || geometry.RingCount == 0) return false;

            int maxPolygons = math.max(1, geometry.RingCapacity);
            var polyOuterIdx  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleStart = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyHoleCount = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var holeRingIdxs  = new NativeArray<int>(maxPolygons, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            new RingAssemblyJob
            {
                Vertices = geometry.Vertices, RingOffsets = geometry.RingOffsets, RingFeatureIdx = geometry.RingFeatureIdx,
                RingCount = geometry.RingCount, FeatureGeometryType = geometry.FeatureGeometryType,
                OutPolyOuterRingIdx = polyOuterIdx, OutPolyHoleListStart = polyHoleStart, OutPolyHoleCount = polyHoleCount,
                OutHoleRingIdxs = holeRingIdxs, OutPolygonCount = polyCountArr, OutHoleCount = holeCountArr,
            }.Run();

            bool result = false;
            int polyCount = polyCountArr[0];
            for (int pi = 0; pi < polyCount; pi++)
                if (polyHoleCount[pi] > 1) { result = true; break; }

            polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
            holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose();
            return result;
        }

        // ── Hashing ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Used only by <see cref="ProjectionDispatch_SphericalArmIsEntered"/> — the corpus sweep
        /// above accumulates its own per-stream bytes inline (<c>Accumulate</c>) rather than hashing per
        /// case, since its golden is one running digest over the whole corpus.</summary>
        private static string HashDouble3ArrayFromList(NativeList<double3> list, int count)
        {
            var bytes = new List<byte>(count * 24);
            for (int i = 0; i < count; i++)
            {
                bytes.AddRange(BitConverter.GetBytes(list[i].x));
                bytes.AddRange(BitConverter.GetBytes(list[i].y));
                bytes.AddRange(BitConverter.GetBytes(list[i].z));
            }
            return Sha256(bytes);
        }

        internal static string Sha256(List<byte> bytes)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }
}
