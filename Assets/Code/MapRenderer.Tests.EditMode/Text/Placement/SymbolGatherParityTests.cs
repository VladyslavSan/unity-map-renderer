// Unity EditMode only — SymbolGatherPlan / SymbolTileBlock / SymbolPlacementSystem.GatherIntoMirror all use
// Unity.Collections; reached via InternalsVisibleTo("MapRenderer.Tests.EditMode"). NOT in core-tests.csproj.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Is = UnityEngine.TestTools.Constraints.Is; // Is.Not.AllocatingGCMemory() — the tooth-#2 gather alloc guard

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Symbol-symbol perf Phase 1 / Stage 2 (design §5 B) — THE ORDER-PARITY TOOTH (#3). The production per-frame
    /// path replaced a managed SoA build with a NATIVE GATHER (<see cref="SymbolPlacementSystem.GatherIntoMirror"/>)
    /// that compacts each winner's pre-baked <see cref="SymbolTileBlock"/> slice into the placement job's
    /// native mirror. This test proves the gather is BYTE-IDENTICAL to an INDEPENDENT restatement over the SAME
    /// winner plan — the exact invariant the GPU snapshot suite depends on, localized to a field-by-field
    /// comparison so a spine bug (winner order, the <c>(blockId, localIndex)</c> mapping, or a source-offset
    /// remap) surfaces HERE, not as a vague snapshot flip.
    ///
    /// <para><b>The oracle (<see cref="BuildExpectedBatch"/>) is independent of the gather, not of the bake.</b>
    /// It walks the SAME winner plan the gather reads (<c>blockId</c>/<c>localIndex</c>/<c>isDeparting</c>/
    /// <c>decisions</c>) and, for each winner, reads its fields straight off the committed
    /// <see cref="SymbolTileBlock"/>'s own columns (<c>block.Points[block.Detail[localIndex]]</c>, quad/
    /// glyph/anchor/path spans, <c>block.RepAnchor</c>) — a hand-written managed loop, never calling
    /// <see cref="SymbolPlacementSystem.GatherIntoMirror"/> or the Burst <c>SymbolGatherJob</c> it drives. Both
    /// the oracle and the production gather consume the SAME immutable bake output (itself pinned separately by
    /// <c>SymbolTileBlockBakerTests</c>), so a match here proves the gather's SELECTION/ASSEMBLY logic
    /// (which block, which raw slot, which pool offset) is correct — exactly what this tooth exists to pin —
    /// without re-deriving the per-symbol field math the baker already owns (reusing <c>block.Points</c>/
    /// <c>block.Curveds</c> verbatim is the same "shared math, checked assembly" split
    /// <c>SymbolTileBlockBaker</c>'s own doc describes).</para>
    ///
    /// <para>Fixture: a MULTI-TILE set with BOTH curved and point symbols, a tile the coverage filter FADES, a tile
    /// it DROPS (so the active-range compaction genuinely moves elements), and a DEPARTING tile (so the
    /// departing-tail shift runs).</para>
    /// </summary>
    [TestFixture]
    public class SymbolGatherParityTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        // Identity projection matrices: every corner projects (clip.w == 1 > 0) so a HUGE minCoverage forces every
        // tile below threshold deterministically — Keep/Fade/Drop is then driven purely by coverageAbovePrev, no
        // camera framing needed (the filter's classification, not its exact coverage number, is what we exercise).
        private static readonly float4x4 IdViewProj = float4x4.identity;
        private static readonly float3x3 IdRebase = float3x3.identity;
        private static readonly double2 Viewport = new double2(100, 100);
        // Dominates any projected tile-quad coverage (identity projection of z5 Mercator corners tops out ~1e14),
        // so every tile is deterministically "below threshold" → Keep/Fade/Drop is driven purely by coverageAbovePrev.
        private const double HugeMinCoverage = 1e30;

        private static readonly WebMercatorProjection P = new WebMercatorProjection();

        private static SymbolQuad OneQuadCell(float u) => new SymbolQuad
        {
            TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
            UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
        };

        private static List<SymbolQuad> OneQuadList(float u) => new List<SymbolQuad> { OneQuadCell(u) };

        private static void AddPointSymbol(SymbolTileBuffer buffer, double3 anchor, string text, int feature,
            long tileKey, float u)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuadList(u), float2.zero, new float2(18f, 18f),
                text: text, textSizePx: 20f, paddingPx: 2f, sortKey: 0f,
                featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);

        private static void AddCurvedSymbol(SymbolTileBuffer buffer, double3 anchor, string text, int feature,
            long tileKey)
            => TestSymbolTileBuffer.AddCurved(buffer,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 3f, Cell = OneQuadCell(0.3f) },
                    new CurvedGlyph { ArcCenter = 9f, Cell = OneQuadCell(0.5f) },
                },
                anchors: new[] { new LineAnchor(0, 0.5f) },
                path: new[] { anchor, new double3(10, 0, 0) + anchor, new double3(20, 0, 0) + anchor },
                anchorRender: anchor, text: text, textSizePx: 20f, paddingPx: 2f, sortKey: 1f,
                featureIndex: feature, tileKey: tileKey, maxAngleDeg: 45f, keepUpright: true, paint: SymbolPaint.Default);

        // Burst-gather Stage 1 (§6.2 widening): an EMPTY quad list — a point symbol that contributes zero quads,
        // exercising SymbolGatherJob's `quadCount > 0` guard (a zero-length source array can yield a null Ptr).
        private static List<SymbolQuad> ZeroQuadList() => new List<SymbolQuad>();

        // Burst-gather Stage 1 (§6.2 widening): a parameterized curved-symbol append so a test can hand it an EMPTY
        // glyph list or an EMPTY anchor array — exercising the `glyphCount > 0` / `anchorCount > 0` guards and,
        // for the empty-anchor case, the UNGUARDED fade copy (fadeCount = anchorCount + 1 = 1, no guard).
        private static void AddCurvedSymbolCustom(SymbolTileBuffer buffer, double3 anchor, string text, int feature,
            long tileKey, List<CurvedGlyph> glyphs, LineAnchor[] anchors)
            => TestSymbolTileBuffer.AddCurved(buffer, glyphs, anchors,
                path: new[] { anchor, new double3(10, 0, 0) + anchor, new double3(20, 0, 0) + anchor },
                anchorRender: anchor, text: text, textSizePx: 20f, paddingPx: 2f, sortKey: 1f,
                featureIndex: feature, tileKey: tileKey, maxAngleDeg: 45f, keepUpright: true, paint: SymbolPaint.Default);

        private static SymbolTileStore.Key Key(TileId t) => new SymbolTileStore.Key("s", t);

        // Bake + commit one tile's symbols as a native block (the SAME projection/tileOrigin the pipeline resolves).
        private static void SeedTile(SymbolTileStore store, TileId tile, Action<SymbolTileBuffer> build)
        {
            int gen = store.BeginBuild(Key(tile));
            var buffer = new SymbolTileBuffer();
            build(buffer);
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(tile, P), new SymbolStringTable());
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, block), "sanity: block committed");
        }

        private static long Tk(TileId t) => SymbolTileKey.Pack(t);

        // Locates the winner entry for tileKey's raw record at wantLocalIndex — used by the RED-verify (a) test
        // to perturb a KNOWN-real winner without assuming CollectInto's scan order (block/collection order is an
        // implementation detail this test must not bake in).
        private static int FindWinner(List<int> blockId, List<int> localIndex,
            IReadOnlyList<SymbolTileBlock> orderedBlocks, long tileKey, int wantLocalIndex)
        {
            for (int i = 0; i < blockId.Count; i++)
                if (orderedBlocks[blockId[i]].TileKey == tileKey && localIndex[i] == wantLocalIndex) return i;
            return -1;
        }

        // Classify + compact the RAW CollectInto winner-plan arrays: removes every Drop-classified record,
        // mirroring the RETIRED SymbolTileCoverageFilter.FilterActive's physical-compaction behaviour (this test
        // targets winner-ORDER parity — "compaction genuinely moves elements" — not D1's resident-Drop masking,
        // which SymbolGatherPlan's own doc describes as a separate, later concern). Driven by the BLOCK-based
        // SymbolTileCoverageFilter.ClassifyActive so no per-symbol managed carrier list is ever materialized.
        private static void ClassifyAndCompact(IReadOnlyList<SymbolTileBlock> orderedBlocks,
            List<int> blockId, List<int> localIndex, List<byte> isDeparting, HashSet<long> coverageAbovePrev,
            out List<byte> rawDecisions,
            out List<int> compactBlockId, out List<int> compactLocalIndex, out List<byte> compactIsDeparting,
            out List<byte> compactDecisions, out int culled)
        {
            var blockTileKeys = new List<long>(orderedBlocks.Count);
            for (int b = 0; b < orderedBlocks.Count; b++) blockTileKeys.Add(orderedBlocks[b].TileKey);

            rawDecisions = new List<byte>();
            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, P,
                double3.zero, IdViewProj, Viewport, IdRebase, HugeMinCoverage,
                coverageAbovePrev, new HashSet<long>(), new Dictionary<long, double>(), new HashSet<long>(),
                now: 20.0, graceSeconds: 1000.0, new Dictionary<long, byte>(), new List<byte>(),
                rawDecisions, out culled);

            compactBlockId = new List<int>(); compactLocalIndex = new List<int>();
            compactIsDeparting = new List<byte>(); compactDecisions = new List<byte>();
            for (int i = 0; i < blockId.Count; i++)
            {
                if (rawDecisions[i] == SymbolTileCoverageFilter.Drop) continue;
                compactBlockId.Add(blockId[i]); compactLocalIndex.Add(localIndex[i]);
                compactIsDeparting.Add(isDeparting[i]); compactDecisions.Add(rawDecisions[i]);
            }
        }

        // THE INDEPENDENT ORACLE — see the type doc for why this does not call GatherIntoMirror. A line-for-line
        // MANAGED restatement of the same "select this winner's slice from its block, flatten into one SoA" job
        // SymbolGatherJob performs in Burst — written independently here rather than shared, so the two can
        // disagree if either one has a selection/offset bug.
        private static void BuildExpectedBatch(SymbolBatch expected,
            List<int> blockId, List<int> localIndex, List<byte> isDeparting, List<byte> decisions,
            IReadOnlyList<SymbolTileBlock> orderedBlocks)
        {
            expected.Reset();
            int n = blockId.Count;
            for (int i = 0; i < n; i++)
            {
                SymbolTileBlock block = orderedBlocks[blockId[i]];
                int raw = localIndex[i];
                SymbolPlacementKind kind = block.Kinds[raw];
                int detail = block.Detail[raw];
                bool departing = isDeparting[i] != 0;
                bool coverageFading = decisions[i] == SymbolTileCoverageFilter.Fade;

                if (kind == SymbolPlacementKind.Point)
                {
                    int quadStart = block.PointQuadStart[detail], quadCount = block.PointQuadCount[detail];
                    int expectedQuadStart = expected.QuadCount;
                    for (int q = 0; q < quadCount; q++) expected.AddQuad(block.Quads[quadStart + q]);
                    int expectedDetail = expected.AddPoint(block.Points[detail], expectedQuadStart, quadCount);

                    int worldStartSrc = block.WorldStart[raw], worldCount = block.WorldCount[raw];
                    int expectedWorldStart = expected.WorldPointCount;
                    for (int v = 0; v < worldCount; v++)
                    {
                        expected.AddWorldPoint(block.WorldPoints[worldStartSrc + v]);
                        expected.AddWorldUp(block.WorldUps[worldStartSrc + v]);
                    }
                    expected.AddSymbol(SymbolPlacementKind.Point, expectedDetail, expectedWorldStart, worldCount,
                        block.RepAnchor[raw], departing, coverageFading);
                }
                else
                {
                    int glyphStart = block.CurvedGlyphStart[detail], glyphCount = block.CurvedGlyphCount[detail];
                    int expectedGlyphStart = expected.GlyphCount;
                    for (int g = 0; g < glyphCount; g++) expected.AddGlyph(block.Glyphs[glyphStart + g]);

                    int anchorStart = block.CurvedAnchorStart[detail], anchorCount = block.CurvedAnchorCount[detail];
                    int expectedAnchorStart = expected.AnchorCount;
                    for (int a = 0; a < anchorCount; a++) expected.AddAnchor(block.Anchors[anchorStart + a]);

                    int fadeStart = block.CurvedAnchorFadeStart[detail];
                    int expectedFadeStart = expected.AnchorFadeCount;
                    for (int a = 0; a <= anchorCount; a++) expected.AddAnchorFadeId(block.AnchorFadeIds[fadeStart + a]);

                    int expectedDetail = expected.AddCurved(block.Curveds[detail], expectedGlyphStart, glyphCount,
                        expectedAnchorStart, anchorCount, expectedFadeStart);

                    int worldStartSrc = block.WorldStart[raw], worldCount = block.WorldCount[raw];
                    int expectedWorldStart = expected.WorldPointCount;
                    for (int v = 0; v < worldCount; v++)
                    {
                        expected.AddWorldPoint(block.WorldPoints[worldStartSrc + v]);
                        expected.AddWorldUp(block.WorldUps[worldStartSrc + v]);
                    }
                    expected.AddSymbol(SymbolPlacementKind.Curved, expectedDetail, expectedWorldStart, worldCount,
                        block.RepAnchor[raw], departing, coverageFading);
                }
            }
        }

        // Build the whole production winner-plan pipeline into `plan`, and the reference oracle via
        // BuildExpectedBatch over the SAME (compacted) winner plan. Returns the gathered mirror (materialized as
        // a batch) via `gathered`.
        private void RunPipeline(SymbolTileStore store, SymbolPlacementSystem lps, SymbolGatherPlan plan,
            HashSet<long> coverageAbovePrev, bool skipPermute, int perturbWinner, int perturbLocalIndex,
            out SymbolBatch oracle, out SymbolBatch gathered, out int culled)
        {
            var planBlockId = new List<int>();
            var planLocalIndex = new List<int>();
            var planIsDeparting = new List<byte>();
            store.CollectInto(planBlockId, planLocalIndex, planIsDeparting, quantizeMeters: 1.0, out _);

            ClassifyAndCompact(store.OrderedBlocks, planBlockId, planLocalIndex, planIsDeparting, coverageAbovePrev,
                out List<byte> rawDecisions,
                out List<int> compactBlockId, out List<int> compactLocalIndex, out List<byte> compactIsDeparting,
                out List<byte> compactDecisions, out culled);

            // Snapshot BEFORE any perturbation/skip below — the oracle must read the UNPERTURBED winner set so
            // RED-verify (a)/(b) can diverge it from what the (perturbed) plan feeds the gather.
            var oracleBlockId = new List<int>(compactBlockId);
            var oracleLocalIndex = new List<int>(compactLocalIndex);
            var oracleIsDeparting = new List<byte>(compactIsDeparting);
            var oracleDecisions = new List<byte>(compactDecisions);

            // RED-verify (b): skip compaction entirely for the plan feed — the RAW, uncompacted arrays (which
            // still carry the Dropped winner) desync from the oracle's compacted winner set, exactly the class of
            // bug the retired FilterActive parallel-permute used to guard against.
            List<int> feedBlockId = skipPermute ? planBlockId : compactBlockId;
            List<int> feedLocalIndex = skipPermute ? planLocalIndex : compactLocalIndex;
            List<byte> feedIsDeparting = skipPermute ? planIsDeparting : compactIsDeparting;
            List<byte> feedDecisions = skipPermute ? rawDecisions : compactDecisions;

            // RED-verify (a): perturb ONE winner's localIndex in the PLAN feed only — the oracle's snapshot above
            // was already taken, so it still reads the correct record.
            if (perturbWinner >= 0) feedLocalIndex[perturbWinner] = perturbLocalIndex;

            plan.Build(feedBlockId, feedLocalIndex, feedIsDeparting, feedDecisions, store.OrderedBlocks, winnerSetVersion: 0);
            lps.GatherIntoMirror(plan);
            gathered = new SymbolBatch();
            lps.CopyMirrorInto(gathered);

            oracle = new SymbolBatch();
            BuildExpectedBatch(oracle, oracleBlockId, oracleLocalIndex, oracleIsDeparting, oracleDecisions, store.OrderedBlocks);
        }

        // A 4-tile fixture: A (point + curved, faded), B (point, faded), C (point, DROPPED), D (point, DEPARTING).
        private SymbolTileStore Seed(out HashSet<long> coverageAbovePrev,
            out TileId a, out TileId b, out TileId c, out TileId d)
        {
            // Locals (not the out params) so the seeding lambdas can capture them — C# forbids capturing an
            // out/ref parameter inside a lambda (CS1628). The out params are assigned from these below.
            TileId ta = new TileId { Z = 5, X = 16, Y = 16 };
            TileId tb = new TileId { Z = 5, X = 17, Y = 16 };
            TileId tc = new TileId { Z = 5, X = 18, Y = 16 };
            TileId td = new TileId { Z = 5, X = 19, Y = 16 };
            a = ta; b = tb; c = tc; d = td;

            var store = new SymbolTileStore(cacheCap: 16);
            SeedTile(store, ta, buffer =>
            {
                AddPointSymbol(buffer, new double3(100, 0, 200), "a", 1, Tk(ta), 0.1f);
                AddCurvedSymbol(buffer, new double3(120, 0, 220), "ac", 2, Tk(ta));
            });
            SeedTile(store, tb, buffer => AddPointSymbol(buffer, new double3(300, 0, 400), "b", 3, Tk(tb), 0.15f));
            SeedTile(store, tc, buffer => AddPointSymbol(buffer, new double3(500, 0, 600), "c", 4, Tk(tc), 0.2f));
            SeedTile(store, td, buffer => AddPointSymbol(buffer, new double3(700, 0, 800), "d", 5, Tk(td), 0.25f));

            // D leaves cover with a grace window → cached + departing (collected after the active split).
            store.ReconcileActiveSet(
                new List<SymbolTileStore.Key> { Key(ta), Key(tb), Key(tc) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);

            // A, B were above threshold last frame → they FADE; C never was → it DROPS.
            coverageAbovePrev = new HashSet<long> { Tk(ta), Tk(tb) };
            return store;
        }

        // Owns the LPS + the throwaway Unity resources the fixture creates. SymbolPlacementSystem.Dispose frees only
        // its CLONED material, so this disposes the base material + render texture (+ camera GO) explicitly — no leak.
        private sealed class LpsHarness : IDisposable
        {
            public readonly SymbolPlacementSystem Lps;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public LpsHarness()
            {
                _camGo = new GameObject("GatherParity_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(64, 64, 0);
                uCam.targetTexture = _rt;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 0, Longitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                // GatherIntoMirror/CopyMirrorInto touch neither the camera nor the material; a material silences the ctor warn.
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Lps = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Lps.Dispose();                                    // frees the cloned material + native buffer
                UnityEngine.Object.DestroyImmediate(_camGo);      // destroy the Camera FIRST so the RT is no longer
                UnityEngine.Object.DestroyImmediate(_rt);         // its targetTexture (else Unity logs an Error → test fail)
                UnityEngine.Object.DestroyImmediate(_baseMaterial); // the base clone source (Lps.Dispose never sees it)
            }
        }

        [Test]
        public void Gather_MatchesBuildOracle_FieldByField()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: -1, perturbLocalIndex: -1,
                    out SymbolBatch oracle, out SymbolBatch gathered, out int culled);

                Assert.AreEqual(1, culled, "precondition: tile C is Dropped (so the compaction moves elements)");
                Assert.AreEqual(4, oracle.Count, "precondition: A.curved + A.point + B.point (active) + D.point (departing)");
                Assert.AreEqual(1, oracle.CurvedCount, "precondition: exactly the one curved label (A)");
                Assert.AreEqual(3, oracle.PointCount, "precondition: A.point + B.point + D.point");

                string diff = SymbolBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the independent oracle — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // RED-verify (a): perturb ONE winner's localIndex — the gather reads the wrong record; parity MUST break.
        [Test]
        public void Gather_DetectsPerturbedLocalIndex()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out TileId a, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Locate the ACTUAL winner for tile A's point record (raw localIndex 0) — never assume
                // CollectInto's scan order — then perturb it to read A's OTHER raw record (the curved, index 1).
                var planBlockId = new List<int>();
                var planLocalIndex = new List<int>();
                var planIsDeparting = new List<byte>();
                store.CollectInto(planBlockId, planLocalIndex, planIsDeparting, quantizeMeters: 1.0, out _);
                ClassifyAndCompact(store.OrderedBlocks, planBlockId, planLocalIndex, planIsDeparting, above,
                    out _, out List<int> compactBlockId, out List<int> compactLocalIndex, out _, out _, out _);
                int perturbWinner = FindWinner(compactBlockId, compactLocalIndex, store.OrderedBlocks, Tk(a), wantLocalIndex: 0);
                Assert.AreNotEqual(-1, perturbWinner, "sanity: tile A's point record (raw localIndex 0) is a winner");

                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: perturbWinner, perturbLocalIndex: 1,
                    out SymbolBatch oracle, out SymbolBatch gathered, out _);
                Assert.IsNotNull(SymbolBatchDiff.FirstDifference(oracle, gathered),
                    "a perturbed winner localIndex must diverge from the oracle — the parity comparison has teeth");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // RED-verify (b): skip compaction for the plan feed — the plan arrays stay full-length (still carrying the
        // Dropped winner) while the oracle reads the compacted winner set; the gather must diverge from the oracle.
        [Test]
        public void Gather_DetectsSkippedFilterPermute()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                RunPipeline(store, harness.Lps, plan, above, skipPermute: true, perturbWinner: -1, perturbLocalIndex: -1,
                    out SymbolBatch oracle, out SymbolBatch gathered, out _);
                Assert.IsNotNull(SymbolBatchDiff.FirstDifference(oracle, gathered),
                    "an un-compacted plan desyncs from the filtered winner set — the gather must diverge from the oracle");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Tooth #2 (gather path): once warm, a second HEAVY GatherIntoMirror allocates ZERO managed garbage — the
        // mirror native lists + plan are reused (the whole point of moving the SoA to a build-time bake). Mirrors
        // the SymbolSubsystemPumpTests.CurrentBatch_Warm_… idiom, one level down on the gather itself.
        // R1: post-memo, a same-version GatherIntoMirror is a memo HIT, not the heavy path this test's message
        // claims — bump WinnerSetVersion immediately before the measured call to force a real rebuild (the honest
        // "force a rebuild" knob: the field is internal, so the test can assign it). The memo-HIT alloc guard is
        // SymbolGatherMemoTests.GatherIntoMirror_MemoHit_AllocatesNoGCMemory (T6), kept separate.
        [Test]
        public void GatherIntoMirror_Warm_AllocatesNoGCMemory()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Build the plan ONCE (collect + filter + plan.Build may allocate first-touch — outside the measure),
                // then materialize the gather so its native lists reach steady capacity.
                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: -1, perturbLocalIndex: -1,
                    out _, out _, out _);
                harness.Lps.GatherIntoMirror(plan); // extra warm-up (first-touch native growth already done above)

                plan.WinnerSetVersion++; // force the measured call to take the heavy (rebuild) path, not a memo hit
                // §6.4 strengthening: the version bump above is a PRECONDITION this test's own claim ("measures the
                // heavy rebuild path") rests on — nothing previously asserted it actually engaged. A MirrorRebuildCount
                // delta makes that load-bearing, so this test can never silently degrade into measuring a memo hit.
                int rebuildsBefore = harness.Lps.MirrorRebuildCount;
                Assert.That(() => { harness.Lps.GatherIntoMirror(plan); }, Is.Not.AllocatingGCMemory(),
                    "a warm GatherIntoMirror must allocate ZERO managed garbage — native lists + plan are reused");
                Assert.AreEqual(rebuildsBefore + 1, harness.Lps.MirrorRebuildCount,
                    "precondition: the measured call must take the heavy rebuild path (WinnerSetVersion bump), not a memo hit");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // §6.2 — widen the parity fixture: a Burst-only running-offset bug only manifests when MULTIPLE winners
        // share a block, or a ZERO-SIZED slice sits between two non-empty ones — the original 4-tile fixture (one
        // winner per block) cannot catch either. Hand-built (no store/coverage filter — full control over winner
        // order and which raw slot is null), this fixture exercises all four gaps in one shot:
        //  - block A: >= 3 winners, with a NULL slot BETWEEN the first two real ones (the null-slot invariant —
        //    localIndex 1 is never itself a winner, but it must not perturb localIndex 2's block-pool slot);
        //  - a point symbol with ZERO quads (empty quad list) — the `quadCount > 0` guard;
        //  - a curved symbol with ZERO glyphs, and one with ZERO anchors — the `glyphCount > 0` /
        //    `anchorCount > 0` guards AND the unguarded fade copy (fadeCount = anchorCount + 1 = 1, no guard);
        //  - >= 2 blocks interleaved in winner order (A, B, A, B, A) so the per-block switch and the running
        //    mirror cursors are exercised together, not block-at-a-time.
        [Test]
        public void Gather_MatchesBuildOracle_FieldByField_MultiWinnerInterleavedBlocks()
        {
            var a = new TileId { Z = 5, X = 20, Y = 16 };
            var b = new TileId { Z = 5, X = 21, Y = 16 };
            double3 originA = TileRenderOrigin.Project(a, P);
            double3 originB = TileRenderOrigin.Project(b, P);
            long tkA = Tk(a), tkB = Tk(b);

            // Block A raw slots: [0] point ZERO quads — [1] point normal #1, 1 quad — [2] point normal #2,
            // 1 DIFFERENT quad — [3] curved normal #1, 2 glyphs + 1 anchor — [4] curved ZERO glyphs —
            // [5] curved normal #2, 2 DIFFERENT glyphs + 1 DIFFERENT anchor. Two quad-bearing points AND two
            // glyph/anchor-bearing curveds in the SAME block is
            // what makes the SECOND of each pair's source offset within the block's OWN pool genuinely non-zero
            // (each #1 occupies the pool's slot 0; the zero-content records before/between contribute nothing to
            // the running offset) — review found the original point-only widening left the curved arm's three
            // source-offset remaps (glyph/anchor/fade start) untestable, since every fixture in the repo has at
            // most one curved symbol per block. #1 and #2 carry DIFFERENT glyph/anchor content so a swapped
            // source offset reads detectably wrong data, not coincidentally-correct data.
            var bufferA = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(bufferA, new double3(10, 0, 10), ZeroQuadList(), float2.zero, float2.zero,
                text: "a0", textSizePx: 20f, paddingPx: 2f, sortKey: 0f, featureIndex: 10, tileKey: tkA, paint: SymbolPaint.Default);
            AddPointSymbol(bufferA, new double3(30, 0, 30), "a2", 12, tkA, 0.4f);
            AddPointSymbol(bufferA, new double3(35, 0, 35), "a3", 13, tkA, 0.45f);
            AddCurvedSymbolCustom(bufferA, new double3(40, 0, 40), "a4", 16, tkA,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 1f, Cell = OneQuadCell(0.1f) },
                    new CurvedGlyph { ArcCenter = 2f, Cell = OneQuadCell(0.15f) },
                },
                anchors: new[] { new LineAnchor(0, 0.5f) });
            AddCurvedSymbolCustom(bufferA, new double3(41, 0, 41), "a5", 17, tkA,
                glyphs: new List<CurvedGlyph>(), anchors: new[] { new LineAnchor(0, 0.55f) });
            AddCurvedSymbolCustom(bufferA, new double3(42, 0, 42), "a6", 18, tkA,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 5f, Cell = OneQuadCell(0.8f) },
                    new CurvedGlyph { ArcCenter = 6f, Cell = OneQuadCell(0.85f) },
                },
                anchors: new[] { new LineAnchor(0, 0.75f) });

            // Block B raw slots: [0] curved ZERO anchors (real) — [1] point normal (real).
            var bufferB = new SymbolTileBuffer();
            AddCurvedSymbolCustom(bufferB, new double3(50, 0, 50), "b0", 14, tkB,
                glyphs: new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 3f, Cell = OneQuadCell(0.6f) } },
                anchors: Array.Empty<LineAnchor>());
            AddPointSymbol(bufferB, new double3(60, 0, 60), "b1", 15, tkB, 0.7f);

            SymbolTileBlock blockA = SymbolTileBlockBaker.Bake(bufferA, slotCount: 1, originA, new SymbolStringTable());
            SymbolTileBlock blockB = SymbolTileBlockBaker.Bake(bufferB, slotCount: 1, originB, new SymbolStringTable());

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Winner order A(li0), B(li0), A(li1), B(li1), A(li2), A(li3), A(li4), A(li5) — interleaved
                // A,B,A,B,A,A,A,A over block A's six dense slots.
                var blockId = new List<int> { 0, 1, 0, 1, 0, 0, 0, 0 };
                var localIndex = new List<int> { 0, 0, 1, 1, 2, 3, 4, 5 };
                var isDeparting = new List<byte> { 0, 0, 0, 0, 0, 0, 0, 0 };
                var decisions = new List<byte>
                {
                    SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep,
                    SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep,
                    SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep,
                };
                var orderedBlocks = new List<SymbolTileBlock> { blockA, blockB };

                plan.Build(blockId, localIndex, isDeparting, decisions, orderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan);
                var gathered = new SymbolBatch();
                harness.Lps.CopyMirrorInto(gathered);

                var oracle = new SymbolBatch();
                BuildExpectedBatch(oracle, blockId, localIndex, isDeparting, decisions, orderedBlocks);

                Assert.AreEqual(8, oracle.Count, "precondition: 8 winners collected");
                Assert.AreEqual(4, oracle.PointCount, "precondition: aPointZeroQuads + aPointNormal1 + bPointNormal + aPointNormal2");
                Assert.AreEqual(4, oracle.CurvedCount, "precondition: bCurvedZeroAnchors + aCurvedNormal1 + aCurvedZeroGlyphs + aCurvedNormal2");
                Assert.AreEqual(0, oracle.PointQuadCount[0], "precondition: the first-processed point (aPointZeroQuads) has zero quads");
                Assert.AreEqual(0, oracle.CurvedAnchorCount[0], "precondition: the first-processed curved (bCurvedZeroAnchors) has zero anchors");
                Assert.AreEqual(0, oracle.CurvedGlyphCount[2], "precondition: the third-processed curved (aCurvedZeroGlyphs) has zero glyphs");
                // Block-level preconditions (not oracle-derived proxies — see the review's N4 finding): pin the
                // REAL property D3-class defects need to diverge on, not a coincidental stand-in.
                int aPointNormal2Detail = blockA.Detail[2];
                Assert.AreNotEqual(0, blockA.PointQuadStart[aPointNormal2Detail],
                    "precondition: aPointNormal2's source quad offset within block A's own pool is genuinely non-zero");
                int aCurvedNormal2Detail = blockA.Detail[5];
                Assert.AreNotEqual(0, blockA.CurvedGlyphStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source glyph offset within block A's own pool is genuinely non-zero");
                Assert.AreNotEqual(0, blockA.CurvedAnchorStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source anchor offset within block A's own pool is genuinely non-zero");
                Assert.AreNotEqual(0, blockA.CurvedAnchorFadeStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source fade offset within block A's own pool is genuinely non-zero");

                string diff = SymbolBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the independent oracle on the widened fixture — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); blockA.Dispose(); blockB.Dispose(); }
        }

        // ── §10 D8/D9 P12: bake/gather parity holds on a pair-bearing fixture — the baker resolves PairRole
        //    over the TILE list (SymbolTileBlockBaker.Fill); this test pins that the gather's block/detail
        //    SELECTION carries that resolved role through untouched. ──
        [Test]
        public void Gather_MatchesBuildOracle_FieldByField_CentredPair()
        {
            var a = new TileId { Z = 5, X = 22, Y = 16 };
            double3 originA = TileRenderOrigin.Project(a, P);
            long tkA = Tk(a);

            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                },
            };
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, new double3(70, 0, 70), iconQuads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon, iconImage: "shield", paint: SymbolPaint.Default, textSizePx: TextQuadLayout.OneEm,
                sortKey: 0f, featureIndex: 20, tileKey: tkA, pairRole: SymbolPairRole.Owner, pairId: 20);
            TestSymbolTileBuffer.AddPoint(buffer, new double3(70, 0, 70), OneQuadList(0.9f), float2.zero, new float2(18f, 18f),
                text: "42", paint: SymbolPaint.Default, textSizePx: 20f, paddingPx: 2f, sortKey: 0f,
                featureIndex: 21, tileKey: tkA, pairRole: SymbolPairRole.Rider, pairId: 20);

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, slotCount: 1, originA, new SymbolStringTable());

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                var blockId = new List<int> { 0, 0 };
                var localIndex = new List<int> { 0, 1 };
                var isDeparting = new List<byte> { 0, 0 };
                var decisions = new List<byte> { SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep };
                var orderedBlocks = new List<SymbolTileBlock> { block };

                plan.Build(blockId, localIndex, isDeparting, decisions, orderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan);
                var gathered = new SymbolBatch();
                harness.Lps.CopyMirrorInto(gathered);

                var oracle = new SymbolBatch();
                BuildExpectedBatch(oracle, blockId, localIndex, isDeparting, decisions, orderedBlocks);

                Assert.AreEqual(2, oracle.PointCount, "precondition: icon + text, both point-placement");
                Assert.AreEqual(SymbolPairRole.Owner, oracle.Points[0].PairRole, "precondition: the icon resolves as Owner in the baked block");
                Assert.AreEqual(SymbolPairRole.Rider, oracle.Points[1].PairRole, "precondition: the text resolves as Rider in the baked block");

                string diff = SymbolBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the independent oracle on a pair-bearing fixture — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); block.Dispose(); }
        }
    }
}
