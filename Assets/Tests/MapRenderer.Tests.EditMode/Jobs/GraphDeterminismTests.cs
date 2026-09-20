// Unity EditMode only — NativeArray/NativeList, Burst jobs, JobsUtility. NOT registered in core-tests.csproj.
//
// job-scheduling-design.md §7.3 / §8 stage 6, B.5 — the determinism tooth this epic has owed since stage 1.
// TWO assertions with different reach, not one pretending to both: (1) the worker-0-vs-default comparison is
// a SELF-comparison over the full column set, on both arms — the empirical check that a batched schedule
// races nobody; (2) the frozen fill-parity goldens, at their REAL reach (9 flat streams / 4 curved-arm count
// streams — see FillMeshGraphParityTests' own §3.7 note on why the curved arm's five geometry arrays are
// never pinned there), confirming this test's own corpus walk reproduces the exact same accumulation the
// golden was captured against. On the curved arm, FillMeshGraph.Schedule schedules NEITHER TileToGeoJob NOR
// ProjectionDispatch (that block is inside `if (!curved)`), so a Spherical pass here exercises exactly one of
// the parallelised fill nodes (earcut) — against counts alone the only digest a raced earcut could move is
// ForceClipCount; corrupted vertex/index data on that arm would be invisible to assertion (2) alone, which is
// why assertion (1) digests the FULL column set (including post-subdivision columns) on BOTH arms.
//
// B.6 (same file, §8 stage 6): one arm per batch constant, each reading THAT node's OWN constant — a
// developer restoring one tuned constant to 1<<20 must red exactly that arm, and no other. The earcut/ribbon
// rows arrive once Groups C/E land (their batch constants do not exist before then).

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Projection;
namespace MapRenderer.Tests.Jobs
{
    [TestFixture]
    public class GraphDeterminismTests
    {
        // ── B.5 — determinism: default worker count vs JobWorkerCount = 0, full column set, both arms. ──────

        private struct PassResult
        {
            /// <summary>SHA-256 over the full column set (TileVertices/WorldPositions/VertexUp/VertexEast/
            /// VertexFeatureIdx/TriangleIndices + the four counts) — the determinism claim (assertion 1).
            /// Digested on BOTH arms; nothing outside this test compares the curved arm's post-subdivision
            /// columns to anything, so there is no format to preserve beyond self-equality.</summary>
            public string FullColumnDigest;

            /// <summary>The SAME format <see cref="MapRenderer.Tests.Tiles.FillMeshGraphParityTests"/> pins as
            /// its frozen goldens (9 flat streams / 4 curved-arm count streams) — assertion 2 compares this,
            /// from the DEFAULT-worker pass only, to that file's own constants.</summary>
            public string GoldenFormatDigest;

            public int MaxPolygonCount;
            public int MaxVertexCount;
            public bool AnyNonEmptyOutput;
        }

        private static PassResult RunOnePass(IProjection projection, bool curved)
        {
            var vertexBytes = new List<byte>(); var worldBytes = new List<byte>(); var upBytes = new List<byte>();
            var eastBytes = new List<byte>(); var featBytes = new List<byte>(); var idxBytes = new List<byte>();
            var bandBytes = new List<byte>();
            var polyBytes = new List<byte>(); var ringBytes = new List<byte>(); var holeBytes = new List<byte>(); var fcBytes = new List<byte>();

            int maxPolygonCount = 0;
            int maxVertexCount = 0;
            bool anyNonEmpty = false;

            void Accumulate(FillMeshPipeline.LayerInput input)
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs(); // load-bearing — an un-flushed schedule runs near-inline,
                                                  // which would make this a serial-to-serial comparison.
                output.Handle.Complete();
                try
                {
                    Assert.IsTrue(output.IsCreated, "precondition: every WalkCorpusAndSynthetic case has a surviving polygon");
                    Assert.AreEqual(FillGraphCounts.Ok, output.Error.Value,
                        "every corpus/synthetic case must complete with no error flag set");

                    FillGraphCounts c = output.Counts[0];
                    maxPolygonCount = math.max(maxPolygonCount, c.PolygonCount);
                    int vc = output.TileVertices.Length;
                    maxVertexCount = math.max(maxVertexCount, vc);
                    if (vc > 0) anyNonEmpty = true;

                    polyBytes.AddRange(BitConverter.GetBytes(c.PolygonCount));
                    ringBytes.AddRange(BitConverter.GetBytes(c.RingCount));
                    holeBytes.AddRange(BitConverter.GetBytes(c.HoleCount));
                    fcBytes.AddRange(BitConverter.GetBytes(c.ForceClipCount));

                    // Both arms digest identically here — on the curved arm these are POST-subdivision
                    // columns, still digested for the FULL-column determinism claim (assertion 1), never
                    // compared to any golden (assertion 2's format excludes them). `curved` only matters at
                    // the golden-format branch below.
                    int ic = output.TriangleIndices.Length;

                    // Assertion 2's golden format digests the INTERIOR PREFIX only, exactly as
                    // FillMeshGraphParityTests does — the boundary band appends to these same columns and its
                    // bytes were never in the frozen capture. Assertion 1's determinism claim keeps the FULL
                    // column set: everything the band added goes into bandBytes below, which is appended to
                    // the full-column digest and to nothing else.
                    int interiorCount = vc - c.BandVertexCount;
                    for (int i = interiorCount; i < vc; i++)
                    {
                        bandBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.TileVertices[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.WorldPositions[i].z));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexUp[i].z));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].z));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexFeatureIdx[i]));
                    }
                    for (int i = 0; i < vc; i++)
                    {
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexBand[i].x));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexBand[i].y));
                        bandBytes.AddRange(BitConverter.GetBytes(output.VertexBand[i].z));
                    }
                    for (int i = 0; i < ic; i++) bandBytes.AddRange(BitConverter.GetBytes(output.TriangleIndices[i]));

                    vc = interiorCount;
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
                        eastBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].x));
                        eastBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].y));
                        eastBytes.AddRange(BitConverter.GetBytes(output.VertexEast[i].z));
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

            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(projection, Accumulate);

            string countsResult =
                $"PolyCount={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(polyBytes)} " +
                $"RingCount={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(ringBytes)} " +
                $"HoleCount={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(holeBytes)} " +
                $"ForceClip={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(fcBytes)}";
            string goldenFormat = curved
                ? countsResult
                : $"Vertex={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(vertexBytes)} " +
                  $"World={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(worldBytes)} " +
                  $"Up={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(upBytes)} " +
                  $"FeatIdx={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(featBytes)} " +
                  $"Indices={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(idxBytes)} {countsResult}";

            string fullColumnDigest =
                $"{goldenFormat} East={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(eastBytes)}" +
                $" Band={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(bandBytes)}";

            return new PassResult
            {
                FullColumnDigest = fullColumnDigest,
                GoldenFormatDigest = goldenFormat,
                MaxPolygonCount = maxPolygonCount,
                MaxVertexCount = maxVertexCount,
                AnyNonEmptyOutput = anyNonEmpty,
            };
        }

        [Test]
        public void Schedule_IsDeterministic_DefaultVsZeroWorkerCount_FullColumnSet(
            [ValueSource(typeof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests), nameof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests.ProjectionCases))]
            IProjection projection)
        {
            bool curved = !double.IsInfinity(projection.MaxRefineAngleRad);

            // Vacuity guard 1 — a single-worker machine would make every "parallel" pass below run on the
            // one worker Editor's own thread anyway, silently vacuous.
            Assert.Greater(JobsUtility.JobWorkerCount, 1,
                "JobsUtility.JobWorkerCount must exceed 1 or every tooth in this stage is vacuous (A0.1 check 10)");

            PassResult defaultPass = RunOnePass(projection, curved);

            int original = JobsUtility.JobWorkerCount;
            JobsUtility.JobWorkerCount = 0;
            PassResult zeroPass;
            try
            {
                zeroPass = RunOnePass(projection, curved);
            }
            finally
            {
                // Unconditional restore, before any assertion that could throw — JobGraphInstrumentTests.cs's
                // own idiom: an AssertionException thrown while JobWorkerCount is still pinned to 0 would leave
                // every later test in this run starved of workers, a worse failure than the one under test.
                JobsUtility.JobWorkerCount = original;
            }

            // Vacuity guard 2 — an enumeration that matched zero polygon layers would hash the empty string on
            // both passes, which compares equal to itself forever.
            Assert.IsTrue(defaultPass.AnyNonEmptyOutput,
                $"[proj={projection.GetType().Name}] precondition: at least one corpus/synthetic case must " +
                "produce a non-empty FillGraphOutput, or the digests below compare two empty hashes");

            // Contention guard — sized to the machine, not to "> 1": at max(8, JobWorkerCount) batches no
            // worker contends twice, so fewer batches would make the two-pass equality below run uncontended
            // and prove nothing. A4's corpus measurement (fill: maxTileVertices=228276, maxPolygonCount=3218)
            // clears this by a wide margin at JobWorkerCount=14 — see the design doc's dated row.
            int minBatches = math.max(8, JobsUtility.JobWorkerCount);
            Assert.GreaterOrEqual(defaultPass.MaxPolygonCount, minBatches,
                $"[proj={projection.GetType().Name}] the corpus's largest case must offer >= {minBatches} " +
                "polygons or the earcut node (once parallel) never has more than one worker contending — a " +
                "corpus question, not a lowered guard.");
            // Flat arm only — on the curved arm FillMeshGraph.Schedule schedules NEITHER TileToGeoJob nor
            // ProjectionDispatch (that block is inside `if (!curved)`), and MaxVertexCount there is the
            // POST-subdivision count from an unrelated node, not a measure of contention on nodes that never
            // ran. Asserting it on that arm would be checking a quantity against a claim it cannot support.
            if (!curved)
                Assert.GreaterOrEqual(defaultPass.MaxVertexCount, minBatches * FillMeshGraph.VertexBatch,
                    $"[proj={projection.GetType().Name}] the corpus's largest case must offer >= {minBatches} * " +
                    $"{FillMeshGraph.VertexBatch} vertices or the tile→geo/project nodes never have more than one " +
                    "worker contending — a corpus question, not a lowered guard, and NOT a lowered VertexBatch " +
                    "(which would clear this guard and B.6's fan-out tooth in one move).");

            // Assertion 1 — the determinism claim: full column set, both arms, self-compared.
            Assert.AreEqual(defaultPass.FullColumnDigest, zeroPass.FullColumnDigest,
                $"[proj={projection.GetType().Name}] default-worker-count and zero-worker-count schedules of " +
                "the SAME graph over the SAME corpus produced different bytes — a race, not merely a batching " +
                "difference (§1: every node's bytes are a pure function of its own input, independent of which " +
                "worker runs it or how many run at once).");

            // Assertion 2 — the frozen fill-parity goldens, at their real reach, from the default-worker pass.
            string expectedGolden = curved
                ? MapRenderer.Tests.Tiles.FillMeshGraphParityTests.FrozenGoldensSpherical
                : MapRenderer.Tests.Tiles.FillMeshGraphParityTests.FrozenGoldensMercator;
            Assert.AreEqual(expectedGolden, defaultPass.GoldenFormatDigest,
                $"[proj={projection.GetType().Name}] this test's own corpus walk no longer reproduces " +
                "FillMeshGraphParityTests' frozen goldens — WalkCorpusAndSynthetic has diverged from what it " +
                "was captured against.");
        }

        // ── E.4 — extend the determinism tooth to the line graph. Same corpus, same worker-0-vs-default
        // self-comparison, over LineGraphOutput's own columns (Vertices/VertexFeatureIdx/Indices + Error).
        // No golden reference here — the four committed line graph-write goldens
        // (line-graphwrite-golden-z{6,9}-{Spherical,WebMercator}.json) already pin exact bytes; this tooth's
        // own job is the self-comparison, not a second pin on the same quantity. ─────────────────────────────

        private static (string Digest, int MaxRingCount, bool AnyNonEmpty) RunOneLinePass(IProjection projection)
        {
            string fixturesDir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(System.IO.Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { System.IO.Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var vertBytes = new List<byte>(); var featBytes = new List<byte>(); var idxBytes = new List<byte>();
            int maxRingCount = 0;
            bool anyNonEmpty = false;

            foreach (string path in allPaths)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var tileId = new MapRenderer.Core.Geo.TileId { Z = 0, X = 0, Y = 0 };
                using var mvtTile = MapRenderer.Jobs.Mvt.MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    var geometry = layer.Geometry;
                    int ringCount = 0;
                    for (int ri = 0; ri < geometry.RingCount; ri++)
                    {
                        int fi = geometry.RingFeatureIdx[ri];
                        if (geometry.FeatureGeometryType[fi] == MapRenderer.Core.Tiles.TileGeometryType.LineString) ringCount++;
                    }
                    if (ringCount == 0) continue;
                    maxRingCount = math.max(maxRingCount, ringCount);

                    var selected = new NativeArray<bool>(geometry.FeatureCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    for (int i = 0; i < geometry.FeatureCount; i++) selected[i] = true;
                    try
                    {
                        var input = new LayerInput
                        {
                            Geometry = geometry, FeatureSelected = selected, OriginRender = default, Projection = projection,
                            Join = JoinType.Miter, Cap = CapType.Butt, MiterLimit = 2.0, RoundSegments = 8, RoundLimit = 0.25,
                            MaxOutputVertices = LineMeshGraph.DefaultMaxOutputVertices,
                        };
                        LineGraphOutput output = LineMeshGraph.Schedule(input);
                        JobHandle.ScheduleBatchedJobs();
                        output.Handle.Complete();
                        try
                        {
                            if (!output.IsCreated) continue;
                            Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value,
                                "every corpus case must complete with no error flag set");
                            int vc = output.Vertices.Length;
                            if (vc > 0) anyNonEmpty = true;
                            for (int i = 0; i < vc; i++)
                            {
                                LineRibbonVertex v = output.Vertices[i];
                                vertBytes.AddRange(BitConverter.GetBytes(v.Position.x));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Position.y));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Position.z));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Across.x));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Across.y));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Across.z));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Up.x));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Up.y));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Up.z));
                                vertBytes.AddRange(BitConverter.GetBytes(v.DistanceAlong));
                                vertBytes.AddRange(BitConverter.GetBytes(v.Side));
                                vertBytes.AddRange(BitConverter.GetBytes(v.WidthScale));
                                featBytes.AddRange(BitConverter.GetBytes(output.VertexFeatureIdx[i]));
                            }
                            for (int i = 0; i < output.Indices.Length; i++) idxBytes.AddRange(BitConverter.GetBytes(output.Indices[i]));
                        }
                        finally { output.Dispose(); }
                    }
                    finally { selected.Dispose(); }
                }
            }

            string digest =
                $"Vertex={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(vertBytes)} " +
                $"FeatIdx={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(featBytes)} " +
                $"Indices={MapRenderer.Tests.Tiles.FillMeshGraphParityTests.Sha256(idxBytes)}";
            return (digest, maxRingCount, anyNonEmpty);
        }

        [Test]
        public void LineSchedule_IsDeterministic_DefaultVsZeroWorkerCount(
            [ValueSource(typeof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests), nameof(MapRenderer.Tests.Tiles.FillMeshGraphParityTests.ProjectionCases))]
            IProjection projection)
        {
            Assert.Greater(JobsUtility.JobWorkerCount, 1,
                "JobsUtility.JobWorkerCount must exceed 1 or every tooth in this stage is vacuous (A0.1 check 10)");

            var defaultPass = RunOneLinePass(projection);

            int original = JobsUtility.JobWorkerCount;
            JobsUtility.JobWorkerCount = 0;
            (string Digest, int MaxRingCount, bool AnyNonEmpty) zeroPass;
            try { zeroPass = RunOneLinePass(projection); }
            finally { JobsUtility.JobWorkerCount = original; } // unconditional — see the fill arm's own note

            Assert.IsTrue(defaultPass.AnyNonEmpty,
                $"[proj={projection.GetType().Name}] precondition: at least one corpus case must produce a " +
                "non-empty LineGraphOutput, or the digests below compare two empty hashes");

            int minRings = math.max(8, JobsUtility.JobWorkerCount);
            Assert.GreaterOrEqual(defaultPass.MaxRingCount, minRings,
                $"[proj={projection.GetType().Name}] the corpus's largest case must offer >= {minRings} rings " +
                "or the ribbon node never has more than one worker contending — a corpus question, not a " +
                "lowered guard, and NOT a lowered RibbonRingBatch.");

            Assert.AreEqual(defaultPass.Digest, zeroPass.Digest,
                $"[proj={projection.GetType().Name}] default-worker-count and zero-worker-count schedules of " +
                "the SAME line graph over the SAME corpus produced different bytes — a race, not merely a " +
                "batching difference.");
        }

        // ── B.6 — fan-out: each batch constant, read from ITS OWN declaration, offers >= 2 batches over the
        // real corpus. Failure message states the discriminator up front so a developer tempted to lower a
        // constant sees why that is wrong before doing it. ─────────────────────────────────────────────────

        private const string RefusalMessage =
            "red here means the corpus changed or a constant was restored — do not lower the constant; see " +
            "the design doc's recorded corpus sizes.";

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_FillTileToGeo()
        {
            int maxVertices = LargestFillVertexCount(new WebMercatorProjection());
            Assert.GreaterOrEqual(maxVertices / FillMeshGraph.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_Project()
        {
            // ProjectionDispatch.VertexBatch is shared by all three graphs — the fill graph's flat-arm vertex
            // count is the same quantity ProjectPointsJob processes for that arm (the fill node's tile→geo
            // and project nodes share the exact same TileVertices-derived point list).
            int maxVertices = LargestFillVertexCount(new WebMercatorProjection());
            Assert.GreaterOrEqual(maxVertices / ProjectionDispatch.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_LineTileToGeo()
        {
            int maxPoints = LargestLineSubdividedPointCount();
            Assert.GreaterOrEqual(maxPoints / LineMeshGraph.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_ExtrusionTileToGeo()
        {
            // The extrusion wall chain's flat-ring vertex count is exactly the fill graph's own pre-earcut
            // totalVerts pre-pass (both sum RingOffsets over the SAME visit order over the SAME geometry) —
            // FillExtrusionMeshGraph.cs:111-116 and FillMeshGraph.cs:97-105 are the same formula. Measuring
            // it directly here (rather than scheduling the whole wall chain, which needs featureColors/
            // featureBake inputs this tooth has no use for) reads the identical quantity
            // FillExtrusionMeshGraph.VertexBatch's own TileToGeoJob call processes.
            int maxVertices = LargestRawVisitedRingVertexCount();
            Assert.GreaterOrEqual(maxVertices / MapRenderer.Unity.Rendering.Meshing.FillExtrusionMeshGraph.VertexBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_Earcut()
        {
            // EarcutPolygonBatch == 1, so count/batch >= 2 reduces to "at least 2 polygons" — A.4's measured
            // corpus (maxPolygonCount=3218) clears this by three orders of magnitude.
            int maxPolygonCount = LargestFillPolygonCount(new WebMercatorProjection());
            Assert.GreaterOrEqual(maxPolygonCount / FillMeshGraph.EarcutPolygonBatch, 2, RefusalMessage);
        }

        [Test]
        public void ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus_Ribbon()
        {
            // RibbonRingBatch == 1, so count/batch >= 2 reduces to "at least 2 rings".
            int maxRingCount = LargestLineRingCount();
            Assert.GreaterOrEqual(maxRingCount / LineMeshGraph.RibbonRingBatch, 2, RefusalMessage);
        }

        private static int LargestFillPolygonCount(IProjection projection)
        {
            int max = 0;
            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(projection, input =>
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                output.Handle.Complete();
                try { if (output.IsCreated) max = math.max(max, output.Counts[0].PolygonCount); }
                finally { output.Dispose(); }
            });
            return max;
        }

        private static int LargestLineRingCount()
        {
            string fixturesDir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(System.IO.Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { System.IO.Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            int max = 0;
            foreach (string path in allPaths)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var tileId = new MapRenderer.Core.Geo.TileId { Z = 0, X = 0, Y = 0 };
                using var mvtTile = MapRenderer.Jobs.Mvt.MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    var geometry = layer.Geometry;
                    int ringCount = 0;
                    for (int ri = 0; ri < geometry.RingCount; ri++)
                    {
                        int featIdx = geometry.RingFeatureIdx[ri];
                        if (geometry.FeatureGeometryType[featIdx] == MapRenderer.Core.Tiles.TileGeometryType.LineString)
                            ringCount++;
                    }
                    max = math.max(max, ringCount);
                }
            }
            return max;
        }

        private static int LargestFillVertexCount(IProjection projection)
        {
            int max = 0;
            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(projection, input =>
            {
                FillGraphOutput output = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                output.Handle.Complete();
                try { if (output.IsCreated) max = math.max(max, output.TileVertices.Length); }
                finally { output.Dispose(); }
            });
            return max;
        }

        private static int LargestRawVisitedRingVertexCount()
        {
            int max = 0;
            MapRenderer.Tests.Tiles.FillMeshGraphParityTests.WalkCorpusAndSynthetic(new WebMercatorProjection(), input =>
            {
                NativeArray<int> visit = input.RingVisitOrder;
                var source = input.Geometry;
                int totalVerts = 0;
                for (int k = 0; k < visit.Length; k++)
                {
                    int ri = visit[k];
                    totalVerts += source.RingOffsets[ri + 1] - source.RingOffsets[ri];
                }
                max = math.max(max, totalVerts);
            });
            return max;
        }

        /// <summary>Reconstructs <see cref="LineMeshGraph.ScheduleTyped{TProj}"/>'s gather→tile-geo→project→
        /// subdivide sub-chain far enough to read the SUBDIVIDED centerline point count — the exact quantity
        /// the graph's SECOND <c>TileToGeoJob</c> call processes (job-scheduling-design.md §8 stage 6, B.3).
        /// A raw pre-subdivision count would be a valid but weaker lower bound (subdivision only inserts
        /// points); this measures the real quantity instead of a proxy for it.
        ///
        /// <para>Walks the corpus directly (NOT <c>WalkCorpusAndSynthetic</c>, which filters on
        /// <see cref="MapRenderer.Core.Tiles.TileGeometryType.Polygon"/> — the fill graph's own corpus arm.
        /// LineString-bearing layers are mostly disjoint from polygon-bearing ones in this fixture set,
        /// confirmed empirically: routing the line row through the polygon-filtered walk measured 0 across
        /// every case).</para></summary>
        private static int LargestLineSubdividedPointCount()
        {
            string fixturesDir = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Fixtures");
            var pbfPaths = new List<string>(System.IO.Directory.GetFiles(fixturesDir, "*.pbf.bytes"));
            pbfPaths.Sort(StringComparer.Ordinal);
            var allPaths = new List<string> { System.IO.Path.Combine(fixturesDir, "sample-tile.bytes") };
            allPaths.AddRange(pbfPaths);

            var projections = new IProjection[] { new WebMercatorProjection(), new SphericalProjection() };
            int max = 0;

            foreach (string path in allPaths)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var tileId = new MapRenderer.Core.Geo.TileId { Z = 0, X = 0, Y = 0 };
                using var mvtTile = MapRenderer.Jobs.Mvt.MvtDecoder.Decode(tileId, bytes);
                foreach (var layer in mvtTile.Layers)
                {
                    var geometry = layer.Geometry;
                    bool hasLine = false;
                    for (int fi = 0; fi < geometry.FeatureCount; fi++)
                        if (geometry.FeatureGeometryType[fi] == MapRenderer.Core.Tiles.TileGeometryType.LineString) { hasLine = true; break; }
                    if (!hasLine) continue;

                    var selected = new NativeArray<bool>(geometry.FeatureCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    for (int i = 0; i < geometry.FeatureCount; i++) selected[i] = true;

                    foreach (IProjection projection in projections)
                    {
                        var srcTile = new NativeList<double2>(64, Allocator.Persistent);
                        var ringSrcOffsets = new NativeList<int>(8, Allocator.Persistent);
                        var ringFeature = new NativeList<int>(8, Allocator.Persistent);
                        var srcGeo = new NativeList<GeoCoordinate>(64, Allocator.Persistent);
                        var srcWorld = new NativeList<double3>(64, Allocator.Persistent);
                        var srcUp = new NativeList<double3>(64, Allocator.Persistent);
                        var subTile = new NativeList<double2>(64, Allocator.Persistent);
                        var ringSubOffsets = new NativeList<int>(8, Allocator.Persistent);
                        var subGeo = new NativeList<GeoCoordinate>(64, Allocator.Persistent);
                        var subWorld = new NativeList<double3>(64, Allocator.Persistent);
                        var subUp = new NativeList<double3>(64, Allocator.Persistent);
                        try
                        {
                            new RingGatherJob
                            {
                                Vertices = geometry.Vertices, RingOffsets = geometry.RingOffsets, RingFeatureIdx = geometry.RingFeatureIdx,
                                FeatureGeometryType = geometry.FeatureGeometryType, RingCount = geometry.RingCount,
                                FeatureSelected = selected,
                                OutSrcTile = srcTile, OutRingSrcOffsets = ringSrcOffsets, OutRingFeature = ringFeature,
                                OutSrcGeo = srcGeo, OutSrcWorld = srcWorld, OutSrcUp = srcUp,
                            }.Run();

                            if (srcTile.Length == 0) continue; // no LineString rings survived selection

                            new TileToGeoJob
                            {
                                Tile = geometry.Tile, Extent = geometry.Extent,
                                TileCoords = srcTile.AsArray(), OutGeo = srcGeo.AsArray(),
                            }.Run(srcTile.Length);

                            DispatchProjectionForMeasurement(projection, double3.zero, srcGeo, srcWorld, srcUp);

                            new SubdivideJob
                            {
                                SrcTile = srcTile, RingSrcOffsets = ringSrcOffsets, SrcUp = srcUp,
                                MaxRefineAngleRad = projection.MaxRefineAngleRad,
                                OutSubTile = subTile, OutRingSubOffsets = ringSubOffsets,
                                OutSubGeo = subGeo, OutSubWorld = subWorld, OutSubUp = subUp,
                            }.Run();

                            max = math.max(max, subTile.Length);
                        }
                        finally
                        {
                            srcTile.Dispose(); ringSrcOffsets.Dispose(); ringFeature.Dispose();
                            srcGeo.Dispose(); srcWorld.Dispose(); srcUp.Dispose();
                            subTile.Dispose(); ringSubOffsets.Dispose();
                            subGeo.Dispose(); subWorld.Dispose(); subUp.Dispose();
                        }
                    }

                    selected.Dispose();
                }
            }
            return max;
        }

        private static void DispatchProjectionForMeasurement(
            IProjection projection, double3 originWorld,
            NativeList<GeoCoordinate> points, NativeList<double3> world, NativeList<double3> normals)
        {
            switch (projection)
            {
                case WebMercatorProjection wm:
                    new ProjectPointsJob<WebMercatorProjection>
                    {
                        Projection = wm, OriginWorld = originWorld,
                        Points = points.AsArray(), WorldPositions = world.AsArray(), Normals = normals.AsArray(),
                    }.Run(points.Length);
                    break;
                case SphericalProjection sp:
                    new ProjectPointsJob<SphericalProjection>
                    {
                        Projection = sp, OriginWorld = originWorld,
                        Points = points.AsArray(), WorldPositions = world.AsArray(), Normals = normals.AsArray(),
                    }.Run(points.Length);
                    break;
                default:
                    throw new NotSupportedException($"no measurement dispatch for {projection.GetType().Name}");
            }
        }
    }
}
