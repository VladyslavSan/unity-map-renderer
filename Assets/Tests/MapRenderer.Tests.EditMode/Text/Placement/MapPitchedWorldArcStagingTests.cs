// Text/Placement/MapPitchedWorldArcStagingTests.cs — collision-grid/job placement, cross-tile store, horizon cull, icon-skirt carrier chain, curved world-arc staging under a pitched camera, and the shaped-symbol blittability/tile-builder teeth.
//
// No single production area dominates; kept in the order the topic-and-lane pack assembled them, each fixture independent of its neighbours.
//
// Contents:
//   CollisionGridContractTests      — Unit-level contract teeth for the collision grid, kept independently of the placement tests that also exercise it (see the note at SymbolPlacementSystem.cs:732).
//   CollisionJobPlacementTests      — CollisionJob's placement teeth: named/permutation-invariant/sort-key-driven survivor sets over hand-built scenes, plus AssertGreedyContract exercised over adversarial random scenes (wide boxes, the MaxGridDim cell-enlargement path, dense clusters, and…
//   CrossTileIdentityStoreTests     — A-3: cross-tile point-symbol identity — SymbolTileStore's dedup (the store-level half of CrossTileIdentityTests, moved here at the reader cutover, 4.2 — see that file's header).
//   HorizonCullGatherTests          — S3: the globe far-side horizon cull as a GatherSymbolPoints fade trigger (peer of the tile/ distance/departing culls) — a two-sided EditMode proof over a REAL SphericalProjection MapCamera.
//   IconSkirtCarrierChainTests      — The icon skirt's CARRIER CHAIN, driven end to end from a genuinely padded SpriteAtlasView: SymbolFeatureExtractor (computes IconQuadLayout.SkirtPx) → SymbolFeature.IconSkirtPx → StyledSymbolTileBuilder → the point symbol's Layout.Bounds* and the along-line…
//   MapPitchedWorldArcStagingTests  — W3 / Stage AC / GLOBE-A: curved (along-line) symbol staging under a genuinely pitched camera and, for GLOBE-A, a non-zero-axial on-sphere arc — catches a metres-vs-pixels sign confusion a straight path is blind to.
//   ShapedSymbolBlittabilityTests   — UMR-87: ShapedSymbol must live in a NativeArray{T} — the whole point of interning its Text/IconImage strings into TextId/IconImageId ints.
//   StyledSymbolTileBuilderTests    — S105 Slice 3 (A4) — THE decisive test: a parsed symbol layer + the real fixture tile, run through StyledSymbolTileBuilder produces the expected set of shaped ShapedSymbols.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs.Symbols;
using System.Collections.Generic;
using MapRenderer.Core.Text;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using UnityEngine;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using System.Threading.Tasks;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Expressions;
using System.Globalization;
using Unity.Collections.LowLevel.Unsafe;
using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests; // TestGlyphSource
using Object = UnityEngine.Object;
using TextAnchor = MapRenderer.Core.Text.TextAnchor;
using static MapRenderer.Tests.TestSupport.NativeCollisionRunner;


namespace MapRenderer.Tests.Text.Placement
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // CollisionGridContractTests — unit-level contract teeth for the collision grid
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class CollisionGridContractTests
    {
        // Regression (live-demo crash at a dense scene): the node pool is pre-sized on the MAIN thread
        // (CollisionGridSizing, managed float) but filled by the job in BURST — a coordinate on a cell
        // boundary can truncate one cell wider in Burst than the managed sizing counted, so the job needs one
        // more node than the pool holds. A Burst job CANNOT grow a NativeArray (the retired managed grid could,
        // so it never overflowed), and an under-count was an out-of-range WRITE → IndexOutOfRangeException from
        // CollisionJob.Insert, crashing the frame every time at that scene. Insert now guards every write
        // against NodeBox.Length. This forces the under-count directly (a starved pool over a scene that places
        // many boxes) and asserts the job COMPLETES instead of throwing. RED without the guard: NodeBox[cap]
        // write throws. Survivors stay correct here because the boxes are disjoint (a dropped node only removes
        // a blocker prefilter entry — disjoint boxes never block anyway).
        [Test]
        public void StarvedNodePool_GuardsInsteadOfThrowing()
        {
            // 12 disjoint single-cell boxes on a coarse grid → all place, each inserts ≥1 node (≥12 total).
            const int n = 12;
            var cands = new SymbolCandidate[n];
            var boxes = new SymbolBox[n];
            for (int i = 0; i < n; i++)
            {
                float x = i * 500f, y = i * 500f; // far apart → disjoint → all survive
                boxes[i] = new SymbolBox { Min = new float2(x, y), Max = new float2(x + 20f, y + 12f),
                    SortKey = 0, FeatureIndex = i, TileKey = 0, SymbolIndex = i };
                cands[i] = new SymbolCandidate { BoxStart = i, BoxCount = 1, SortKey = 0,
                    FeatureIndex = i, TileKey = 0, SymbolIndex = i };
            }

            var nc = new NativeArray<SymbolCandidate>(n, Allocator.TempJob);
            var nb = new NativeArray<SymbolBox>(n, Allocator.TempJob);
            var ns = new NativeArray<byte>(n, Allocator.TempJob);
            var outCount = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++) { nc[i] = cands[i]; nb[i] = boxes[i]; }
                CollisionGridSizing.Dims dims = CollisionGridSizing.ComputeDims(nb, n);
                int cells = dims.W * dims.H;
                var cellHead = new NativeArray<int>(cells, Allocator.TempJob);
                var nodeBox  = new NativeArray<int>(3, Allocator.TempJob); // STARVED: 3 nodes for ≥12 inserts
                var nodeNext = new NativeArray<int>(3, Allocator.TempJob);
                try
                {
                    for (int c = 0; c < cells; c++) cellHead[c] = -1;
                    Assert.DoesNotThrow(() =>
                        new CollisionJob
                        {
                            Candidates = nc, CandidateCount = n, Boxes = nb, BoxCount = n,
                            Survivors = ns, OutSurvivorCount = outCount,
                            CellHead = cellHead, NodeBox = nodeBox, NodeNext = nodeNext,
                            GridMinX = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                            GridW = dims.W, GridH = dims.H,
                        }.Schedule().Complete(),
                        "Insert must guard writes against a starved node pool (Burst cannot grow it), not throw IndexOutOfRange");
                    Assert.AreEqual(n, outCount[0], "disjoint boxes all survive even when node inserts are dropped by the guard");
                }
                finally { cellHead.Dispose(); nodeBox.Dispose(); nodeNext.Dispose(); }
            }
            finally { nc.Dispose(); nb.Dispose(); ns.Dispose(); outCount.Dispose(); }
        }

        // The node-storage bound carries a ±1-cell margin (each axis, each side) so a Mono/Burst boundary-cell
        // truncation drift can never overflow the pre-sized pool. Assert the margin is present: the bound must
        // exceed the tight (exact) per-box cell count for a scene of multi-cell boxes.
        [Test]
        public void NodeUpperBound_CarriesDriftMargin()
        {
            var (_, boxes) = RandomScene(40, 9, 3000f, 2000f, 100f, 300f, 100f, 300f);
            var nb = new NativeArray<SymbolBox>(boxes.Length, Allocator.TempJob);
            try
            {
                for (int i = 0; i < boxes.Length; i++) nb[i] = boxes[i];
                CollisionGridSizing.Dims dims = CollisionGridSizing.ComputeDims(nb, boxes.Length);
                int bound = CollisionGridSizing.NodeUpperBound(nb, boxes.Length, in dims);

                int tight = 0;
                for (int i = 0; i < boxes.Length; i++)
                {
                    SymbolBox b = nb[i];
                    int cx0 = (int)math.clamp((b.Min.x - dims.MinX) * dims.InvCell, 0, dims.W - 1);
                    int cx1 = (int)math.clamp((b.Max.x - dims.MinX) * dims.InvCell, 0, dims.W - 1);
                    int cy0 = (int)math.clamp((b.Min.y - dims.MinY) * dims.InvCell, 0, dims.H - 1);
                    int cy1 = (int)math.clamp((b.Max.y - dims.MinY) * dims.InvCell, 0, dims.H - 1);
                    tight += (cx1 - cx0 + 1) * (cy1 - cy0 + 1);
                }
                Assert.Greater(bound, tight, "NodeUpperBound must exceed the tight per-box cell count (the ±1-cell drift margin)");
            }
            finally { nb.Dispose(); }
        }

        // ROOT CAUSE of the live dense-scene crash: candidate box ranges are NOT guaranteed disjoint — a box can
        // be referenced by more than one candidate. The job (CollisionJob.Insert) inserts every box in every
        // placed candidate's [BoxStart,BoxStart+BoxCount) range, so a SHARED box is inserted once PER candidate.
        // The old per-UNIQUE-box bound (NodeUpperBound) counts it once → under-count → pool overflow. Even the
        // ±1-cell margin only raises the overflow THRESHOLD; enough sharing still overflows it (proven here).
        // NodeUpperBoundByCandidates counts per reference (matches the job), so it scales with the sharing.
        [Test]
        public void SharedBox_PerCandidateBound_CoversJobInserts_PerUniqueUndercounts()
        {
            // One 1-cell box referenced by MANY AllowOverlap candidates → the job inserts it once per candidate.
            const int shares = 10;
            var boxes = new NativeArray<SymbolBox>(1, Allocator.TempJob);
            var cands = new NativeArray<SymbolCandidate>(shares, Allocator.TempJob);
            var ns = new NativeArray<byte>(shares, Allocator.TempJob);
            var outCount = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                boxes[0] = new SymbolBox { Min = new float2(10f, 10f), Max = new float2(30f, 22f),
                    SortKey = 0, FeatureIndex = 0, TileKey = 0, SymbolIndex = 0 }; // < 64px → 1 cell
                for (int i = 0; i < shares; i++)
                    cands[i] = new SymbolCandidate { BoxStart = 0, BoxCount = 1, AllowOverlap = true,
                        SortKey = 0, FeatureIndex = i, TileKey = 0, SymbolIndex = i };

                CollisionGridSizing.Dims dims = CollisionGridSizing.ComputeDims(boxes, 1);
                int perUnique = CollisionGridSizing.NodeUpperBound(boxes, 1, in dims);
                int perCand   = CollisionGridSizing.NodeUpperBoundByCandidates(cands, shares, boxes, 1, in dims);
                const int jobInserts = shares; // box 0 is one cell → one node per referencing candidate

                Assert.Less(perUnique, jobInserts,
                    "the per-UNIQUE-box bound (even with the ±1 margin) under-counts a box shared by many candidates — the overflow bug");
                Assert.GreaterOrEqual(perCand, jobInserts,
                    "the per-CANDIDATE bound counts the shared box per reference, covering every insert — the fix");

                // Functional: sized by the per-candidate bound, the job fits and every AllowOverlap candidate places.
                var cellHead = new NativeArray<int>(dims.W * dims.H, Allocator.TempJob);
                var nodeBox  = new NativeArray<int>(perCand, Allocator.TempJob);
                var nodeNext = new NativeArray<int>(perCand, Allocator.TempJob);
                try
                {
                    for (int c = 0; c < cellHead.Length; c++) cellHead[c] = -1;
                    new CollisionJob
                    {
                        Candidates = cands, CandidateCount = shares, Boxes = boxes, BoxCount = 1,
                        Survivors = ns, OutSurvivorCount = outCount,
                        CellHead = cellHead, NodeBox = nodeBox, NodeNext = nodeNext,
                        GridMinX = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                        GridW = dims.W, GridH = dims.H,
                    }.Schedule().Complete();
                    Assert.AreEqual(shares, outCount[0], "every AllowOverlap candidate places");
                }
                finally { cellHead.Dispose(); nodeBox.Dispose(); nodeNext.Dispose(); }
            }
            finally { boxes.Dispose(); cands.Dispose(); ns.Dispose(); outCount.Dispose(); }
        }

        // The hazard the differential used to hold implicitly (§3.1.1): the caller pre-sizes the grid node
        // storage and a Burst job cannot grow it, so an under-count silently drops blocker inserts — a
        // candidate isn't blocked, and wrong survivors follow with no crash to notice. Directly observable
        // with no mirroring of the job's cell mapping: CellHead/NodeBox/NodeNext are plain public
        // NativeArray<int> on CollisionJob, so a test can walk each cell's CellHead -> NodeNext chain and
        // count linked nodes after Complete().
        public enum SceneShape { Small, Wide, HugeSpan, Dense }

        [Test]
        public void NodeConsumption_StaysWithinBound_AndABoundSizedPoolSkipsNoInsert([Values] SceneShape shape)
        {
            (SymbolCandidate[] cands, SymbolBox[] boxes) = shape switch
            {
                SceneShape.Small    => RandomScene(500, 1, 2000f, 1200f, 30f, 300f, 10f, 50f),
                SceneShape.Wide     => RandomScene(200, 3, 3000f, 2000f, 400f, 900f, 300f, 700f),
                SceneShape.HugeSpan => RandomScene(400, 5, 200000f, 150000f, 50f, 400f, 20f, 80f),
                SceneShape.Dense    => RandomScene(300, 17, 100f, 100f, 40f, 60f, 20f, 30f),
                _ => throw new System.ArgumentOutOfRangeException(nameof(shape)),
            };
            int n = cands.Length;

            var nb = new NativeArray<SymbolBox>(boxes.Length, Allocator.TempJob);
            var nc = new NativeArray<SymbolCandidate>(n, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++) nc[i] = cands[i];
                for (int i = 0; i < boxes.Length; i++) nb[i] = boxes[i];
                CollisionGridSizing.Dims dims = CollisionGridSizing.ComputeDims(nb, boxes.Length);
                int bound = CollisionGridSizing.NodeUpperBoundByCandidates(nc, n, nb, boxes.Length, in dims);

                // MaxGridDim (512) caps CELL SIZE, not grid dimension: ComputeDims sets invCell = 512/span on
                // the max axis, so W/H can land at 513 (verified numerically for the HugeSpan shape; the
                // boundary is also span-dependent, so a tight <= 512 bound would be flaky, not just wrong).
                Assert.LessOrEqual(dims.W, 513, $"grid W must never exceed 513 ({shape})");
                Assert.LessOrEqual(dims.H, 513, $"grid H must never exceed 513 ({shape})");

                (int[] boundSurvivors, int linkedBound) = RunSized(cands, boxes, dims, bound);
                (int[] generousSurvivors, int linkedGenerous) = RunSized(cands, boxes, dims, bound * 4 + 16);

                Assert.Greater(linkedGenerous, 0, $"non-degeneracy: the generous run must link at least one node ({shape})");
                Assert.LessOrEqual(linkedGenerous, bound, $"the bound must actually bound consumption ({shape})");
                Assert.AreEqual(linkedGenerous, linkedBound,
                    $"a bound-sized pool must skip no insert the generous pool made — CollisionJob.Insert's guard must never have to fire ({shape})");
                CollectionAssert.AreEqual(generousSurvivors, boundSurvivors,
                    $"a bound-sized pool must produce the identical survivor set as a generous one ({shape})");
            }
            finally { nc.Dispose(); nb.Dispose(); }
        }

        // Runs CollisionJob with a node pool of exactly `poolLength`, returning the per-sorted-position
        // survivor flags (0/1) and the number of nodes actually linked — walked via CellHead -> NodeNext,
        // bounded by the pool length so a corrupt chain fails loudly rather than looping forever.
        private static (int[] Survivors, int Linked) RunSized(SymbolCandidate[] cands, SymbolBox[] boxes,
            CollisionGridSizing.Dims dims, int poolLength)
        {
            int n = cands.Length;
            var nc = new NativeArray<SymbolCandidate>(n, Allocator.TempJob);
            var nb = new NativeArray<SymbolBox>(boxes.Length, Allocator.TempJob);
            var ns = new NativeArray<byte>(n, Allocator.TempJob);
            var outCount = new NativeArray<int>(1, Allocator.TempJob);
            var cellHead = new NativeArray<int>(dims.W * dims.H, Allocator.TempJob);
            var nodeBox  = new NativeArray<int>(math.max(1, poolLength), Allocator.TempJob);
            var nodeNext = new NativeArray<int>(math.max(1, poolLength), Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++) nc[i] = cands[i];
                for (int i = 0; i < boxes.Length; i++) nb[i] = boxes[i];
                for (int c = 0; c < cellHead.Length; c++) cellHead[c] = -1;
                new CollisionJob
                {
                    Candidates = nc, CandidateCount = n, Boxes = nb, BoxCount = boxes.Length,
                    Survivors = ns, OutSurvivorCount = outCount,
                    CellHead = cellHead, NodeBox = nodeBox, NodeNext = nodeNext,
                    GridMinX = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                    GridW = dims.W, GridH = dims.H,
                }.Schedule().Complete();

                var survivors = new int[n];
                for (int i = 0; i < n; i++) survivors[i] = ns[i];

                int linked = 0;
                for (int cell = 0; cell < cellHead.Length; cell++)
                {
                    int node = cellHead[cell];
                    int guard = 0;
                    while (node != -1)
                    {
                        linked++;
                        node = nodeNext[node];
                        guard++;
                        Assert.LessOrEqual(guard, nodeBox.Length, "a corrupt node chain must not exceed the pool length");
                    }
                }
                return (survivors, linked);
            }
            finally
            {
                nc.Dispose(); nb.Dispose(); ns.Dispose(); outCount.Dispose();
                cellHead.Dispose(); nodeBox.Dispose(); nodeNext.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // CollisionJobPlacementTests — CollisionJob's placement teeth over hand-built and adversarial scenes
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="CollisionJob"/>'s placement teeth: named/permutation-invariant/sort-key-driven survivor
    /// sets over hand-built scenes, plus <see cref="NativeCollisionRunner.AssertGreedyContract"/> exercised
    /// over adversarial random scenes (wide boxes, the <c>MaxGridDim</c> cell-enlargement path, dense
    /// clusters, and Stage C's per-box optional mask). Every named-set assertion is OUTCOME-based (WHICH
    /// symbols survive, by <see cref="SymbolCandidate.SymbolIndex"/> — not a count).
    /// </summary>
    [TestFixture]
    public class CollisionJobPlacementTests
    {
        // ── Hand-built scenes, moved from SymbolCollisionTests.cs (point symbols as 1-box candidates) ──────

        private static SymbolBox Box(
            float minX, float minY, float maxX, float maxY,
            float sortKey, int featureIndex, long tileKey = 0L,
            bool allowOverlap = false, bool ignorePlacement = false)
            => new SymbolBox
            {
                Min = new float2(minX, minY),
                Max = new float2(maxX, maxY),
                SortKey = sortKey,
                FeatureIndex = featureIndex,
                TileKey = tileKey,
                SymbolIndex = featureIndex,
                AllowOverlap = allowOverlap,
                IgnorePlacement = ignorePlacement,
            };

        // Runs collision over a COPY (so the caller's array order is preserved across permutations) and
        // returns the set of surviving SymbolIndex values, as 1-box candidates through CollisionJob.
        private static HashSet<int> Survivors(IReadOnlyList<SymbolBox> input)
        {
            var boxes = new SymbolBox[input.Count];
            for (int i = 0; i < input.Count; i++) boxes[i] = input[i];
            var cands = new SymbolCandidate[boxes.Length];
            for (int i = 0; i < boxes.Length; i++)
                cands[i] = new SymbolCandidate
                {
                    BoxStart = i, BoxCount = 1, SortKey = boxes[i].SortKey, FeatureIndex = boxes[i].FeatureIndex,
                    TileKey = boxes[i].TileKey, SymbolIndex = boxes[i].SymbolIndex,
                    AllowOverlap = boxes[i].AllowOverlap, IgnorePlacement = boxes[i].IgnorePlacement,
                };
            var flags = new bool[cands.Length];
            int n = NativeCollisionRunner.RunCollision(cands, cands.Length, boxes, boxes.Length, flags);

            var set = new HashSet<int>();
            for (int i = 0; i < cands.Length; i++) if (flags[i]) set.Add(cands[i].SymbolIndex);
            Assert.AreEqual(n, set.Count, "returned survivor count must match the number of set flags");
            return set;
        }

        // A cluster of three mutually-overlapping boxes over region A (all cover [0,20]x[0,20]) with
        // distinct sort keys, plus one DISJOINT box over region B ([100,120]) that can never collide.
        // Placement order (sort key asc): L1(10) -> L2(20) -> L0(30) -> L3(99). L1 wins region A; L2/L0
        // collide with it and drop; L3 is alone. Survivors = {1, 3}.
        private static List<SymbolBox> NamedScenario() => new List<SymbolBox>
        {
            Box(0, 0, 20, 20, sortKey: 30f, featureIndex: 0),   // region A
            Box(2, 2, 18, 18, sortKey: 10f, featureIndex: 1),   // region A (best key)
            Box(4, 4, 16, 16, sortKey: 20f, featureIndex: 2),   // region A
            Box(100, 0, 120, 20, sortKey: 99f, featureIndex: 3) // region B (disjoint — always survives)
        };

        [Test]
        public void GreedyOverCluster_KeepsExactNamedSet()
        {
            CollectionAssert.AreEquivalent(new[] { 1, 3 }, Survivors(NamedScenario()),
                "greedy (sort-key asc) keeps the best-key label in the overlapping cluster plus the " +
                "disjoint label — NOT cull-all ({}), cull-none ({0,1,2,3}), or an insertion-order pick.");
        }

        [Test]
        public void IsPermutationInvariant()
        {
            var forward = NamedScenario();
            var reversed = new List<SymbolBox>(forward);
            reversed.Reverse();
            var rotated = new List<SymbolBox> { forward[2], forward[0], forward[3], forward[1] };

            var expected = new[] { 1, 3 };
            CollectionAssert.AreEquivalent(expected, Survivors(forward));
            CollectionAssert.AreEquivalent(expected, Survivors(reversed));
            CollectionAssert.AreEquivalent(expected, Survivors(rotated),
                "an insertion-order-dependent impl would place a different cluster winner under reordering");
        }

        [Test]
        public void LowerSortKeyWins_AndSwappingKeysFlipsIt()
        {
            var control = Box(100, 0, 120, 20, sortKey: 5f, featureIndex: 9);

            var l0Wins = new List<SymbolBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 0), // lower key -> placed first -> wins
                Box(5, 5, 25, 25, sortKey: 20f, featureIndex: 1),
                control,
            };
            CollectionAssert.AreEquivalent(new[] { 0, 9 }, Survivors(l0Wins));

            var l1Wins = new List<SymbolBox>
            {
                Box(0, 0, 20, 20, sortKey: 20f, featureIndex: 0),
                Box(5, 5, 25, 25, sortKey: 10f, featureIndex: 1), // now the lower key -> wins
                control,
            };
            CollectionAssert.AreEquivalent(new[] { 1, 9 }, Survivors(l1Wins),
                "reversing the two overlapping labels' sort keys must flip which one survives");
        }

        [Test]
        public void AllowOverlap_KeepsBoth()
        {
            var pair = new List<SymbolBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 0, allowOverlap: true),
                Box(5, 5, 25, 25, sortKey: 20f, featureIndex: 1, allowOverlap: true),
            };
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, Survivors(pair),
                "text-allow-overlap skips the collision test — both overlapping labels are placed");
        }

        [Test]
        public void IgnorePlacement_DoesNotBlockLaterSymbols()
        {
            var boxes = new List<SymbolBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 0, ignorePlacement: true), // placed, non-blocking
                Box(5, 5, 25, 25, sortKey: 20f, featureIndex: 1),                        // overlaps 0 but 0 doesn't block
            };
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, Survivors(boxes),
                "an ignore-placement label is placed but must not block a later overlapping label");
        }

        // Boxes are built through the REAL SymbolBox.Build math (anchor + bounds*scale +/- padding), so
        // this is the padding PLUMBING under test, not a hand-inflated AABB. Typo in the name ("AColission")
        // is pre-existing; kept verbatim.
        [Test]
        public void Padding_TurnsAdjacentPairIntoAColission()
        {
            var boundsMin = float2.zero;
            var boundsMax = new float2(20f, 20f);
            const float scaleSize = TextQuadLayout.OneEm; // textSizePx == OneEm -> scale 1

            SymbolBox A(float padding) => SymbolBox.Build(
                new float2(0f, 0f), boundsMin, boundsMax, scaleSize, padding,
                sortKey: 10f, featureIndex: 0, tileKey: 0L, symbolIndex: 0,
                allowOverlap: false, ignorePlacement: false);
            SymbolBox B(float padding) => SymbolBox.Build(
                new float2(21f, 0f), boundsMin, boundsMax, scaleSize, padding,
                sortKey: 20f, featureIndex: 1, tileKey: 0L, symbolIndex: 1,
                allowOverlap: false, ignorePlacement: false);

            // No padding: A=[0,20], B=[21,41] -> 1px gap -> both survive.
            CollectionAssert.AreEquivalent(new[] { 0, 1 },
                Survivors(new List<SymbolBox> { A(0f), B(0f) }),
                "with no padding the 1px-separated pair does not collide — both survive");

            // Padding 1px each edge: A=[-1,21], B=[20,42] -> overlap -> only the lower-key A survives.
            CollectionAssert.AreEquivalent(new[] { 0 },
                Survivors(new List<SymbolBox> { A(1f), B(1f) }),
                "1px padding closes the 1px gap — the pair now collides and only the better-key label survives");
        }

        [Test]
        public void EqualSortKeys_ResolveByFeatureIndexTiebreak()
        {
            var boxes = new List<SymbolBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 7), // equal key, higher feature index
                Box(5, 5, 25, 25, sortKey: 10f, featureIndex: 3), // equal key, LOWER feature index -> wins
            };
            CollectionAssert.AreEquivalent(new[] { 3 }, Survivors(boxes),
                "equal sort keys must resolve deterministically to the lower feature index (stable tiebreak)");
        }

        // ── Adversarial scenarios, re-created from the dissolved SymbolCollisionJobTests.cs. Parity ended —
        //    these now drive NativeCollisionRunner.AssertGreedyContract, an independent verifier of the
        //    greedy contract over the job's own output, never a second greedy pass. ──────────────────────

        private static void RunAndVerify(SymbolCandidate[] cands, SymbolBox[] boxes, string what)
        {
            var flags = new bool[cands.Length];
            NativeCollisionRunner.RunCollision(cands, cands.Length, boxes, boxes.Length, flags);
            NativeCollisionRunner.AssertGreedyContract(cands, cands.Length, boxes, flags, what);
        }

        [Test]
        public void Collision_SmallBoxes([Values(1, 2, 3, 50, 500)] int count, [Values(1, 7, 42, 999)] int seed)
        {
            var (cands, boxes) = NativeCollisionRunner.RandomScene(count, seed, 2000f, 1200f, 30f, 300f, 10f, 50f);
            RunAndVerify(cands, boxes, $"small boxes count={count} seed={seed}");
        }

        // Wide boxes each spanning MANY 64px grid cells — the case where one box lands in a large cell block, so a
        // node-storage under-count would drop inserts.
        [Test]
        public void Collision_WideBoxes([Values(20, 200)] int count, [Values(3, 88)] int seed)
        {
            var (cands, boxes) = NativeCollisionRunner.RandomScene(count, seed, 3000f, 2000f, 400f, 900f, 300f, 700f);
            RunAndVerify(cands, boxes, $"wide boxes count={count} seed={seed}");
        }

        // A span far larger than MaxGridDim*TargetCellPx (512*64 = 32768 px) — forces the cell-enlargement path, a
        // different grid dim / node distribution the sizing must still bound exactly.
        [Test]
        public void Collision_HugeSpan_ForcesCellEnlargement([Values(50, 400)] int count)
        {
            var (cands, boxes) = NativeCollisionRunner.RandomScene(count, 5, 200000f, 150000f, 50f, 400f, 20f, 80f);
            RunAndVerify(cands, boxes, $"huge span count={count}");
        }

        // A DENSE cluster: many overlapping boxes packed into a tiny region (one grid cell), so most drop — stresses
        // the greedy blocking + the single-cell node chain.
        [Test]
        public void Collision_DenseCluster()
        {
            var (cands, boxes) = NativeCollisionRunner.RandomScene(300, 17, 100f, 100f, 40f, 60f, 20f, 30f);
            RunAndVerify(cands, boxes, "dense cluster (300 boxes in ~2 cells)");
        }

        // Multi-box (curved-like) candidates interleaved with point candidates — the all-or-nothing range logic.
        // The scene is hand-built with known geometry, so this asserts the NAMED expected survivor set as well
        // as the contract.
        [Test]
        public void Collision_MultiBoxCandidates()
        {
            // 2 curved (3 boxes each) + 3 points, overlapping in a shared region so collisions actually occur.
            var boxes = new List<SymbolBox>();
            var cands = new List<SymbolCandidate>();
            void Add(int symbol, float sortKey, params (float, float, float, float)[] rects)
            {
                int start = boxes.Count;
                foreach (var r in rects)
                    boxes.Add(new SymbolBox { Min = new float2(r.Item1, r.Item2), Max = new float2(r.Item3, r.Item4),
                        SortKey = sortKey, FeatureIndex = symbol, TileKey = 0, SymbolIndex = symbol });
                cands.Add(new SymbolCandidate { BoxStart = start, BoxCount = rects.Length, SortKey = sortKey,
                    FeatureIndex = symbol, TileKey = 0, SymbolIndex = symbol });
            }
            Add(0, 10f, (0, 0, 30, 12), (40, 0, 70, 12), (80, 0, 110, 12));   // curved (best key)
            Add(1, 20f, (50, 2, 60, 10));                                     // point over curved-0 glyph 2
            Add(2, 15f, (200, 0, 230, 12), (240, 0, 270, 12), (280, 0, 310, 12)); // curved, disjoint region
            Add(3, 25f, (205, 2, 215, 10));                                   // point over curved-2 glyph 1
            Add(4, 30f, (1000, 1000, 1020, 1012));                            // point, far away (always places)

            var candArr = cands.ToArray();
            var boxArr = boxes.ToArray();
            var flags = new bool[candArr.Length];
            NativeCollisionRunner.RunCollision(candArr, candArr.Length, boxArr, boxArr.Length, flags);
            NativeCollisionRunner.AssertGreedyContract(candArr, candArr.Length, boxArr, flags, "multi-box + point candidates");

            var survivors = new HashSet<int>();
            for (int i = 0; i < candArr.Length; i++) if (flags[i]) survivors.Add(candArr[i].SymbolIndex);
            // Derived by hand from the fixture: placement order (sort key asc) is 0(10), 2(15), 1(20), 3(25),
            // 4(30). Symbol 0 places first (no blockers yet). Symbol 2 is disjoint from 0's region, so it
            // places too. Symbol 1's single box overlaps symbol 0's SECOND glyph (40,0,70,12) -> dropped.
            // Symbol 3's single box overlaps symbol 2's FIRST glyph (200,0,230,12) -> dropped. Symbol 4 is
            // far away and always places.
            CollectionAssert.AreEquivalent(new[] { 0, 2, 4 }, survivors,
                "the two curved symbols place (disjoint regions); the two points overlapping their glyphs are dropped; the far point always places");
        }

        // ── C6 (stage C) ──────────────────────────────────────────────────────────────────────────────────
        // Two-box PAIR candidates whose halves OVERLAP BY CONSTRUCTION (what a centred icon+text pair is),
        // each carrying a random OptionalBoxMask, interleaved with ordinary single-box candidates in a
        // congested region so most halves actually contend. AssertGreedyContract's exact DroppedBoxMask
        // characterisation is what now guards this — a different bit, a different insert-skip, fails there.
        // The overlapping halves are also the self-block tripwire: an implementation that inserted one half
        // before testing the other would drop every pair, in one runner or both.
        private static (SymbolCandidate[], SymbolBox[]) RandomMaskedPairScene(int pairCount, int singleCount, int seed)
        {
            var rng = new System.Random(seed);
            var boxes = new List<SymbolBox>();
            var cands = new List<SymbolCandidate>();
            int symbol = 0;

            // RED injection 5's target — a SUPPRESSED candidate, never placed, never a blocker. Its box
            // exactly overlaps the box of the next-to-sort candidate (the -1f singleton below) so the
            // "never a blocker" half is non-vacuous: a job that wrongly inserted a suppressed candidate's
            // boxes would drop that singleton.
            boxes.Add(new SymbolBox { Min = new float2(1035, 995), Max = new float2(1065, 1005),
                SortKey = -2f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol });
            cands.Add(new SymbolCandidate
            {
                BoxStart = boxes.Count - 1, BoxCount = 1, EmitStart = boxes.Count - 1, EmitCount = 1,
                SortKey = -2f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol,
                Suppressed = true,
            });
            symbol++;

            // A DETERMINISTIC contended pair, off in its own region, so "at least one half is dropped" holds for
            // every (count, seed) rather than depending on the random draw: a rider-optional pair whose rider box
            // is covered by a higher-priority single, and whose owner box is free.
            boxes.Add(new SymbolBox { Min = new float2(980, 980), Max = new float2(1020, 1020),
                SortKey = 5f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol });
            boxes.Add(new SymbolBox { Min = new float2(1030, 992), Max = new float2(1070, 1008),
                SortKey = 5f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol });
            cands.Add(new SymbolCandidate
            {
                BoxStart = boxes.Count - 2, BoxCount = 2, EmitStart = boxes.Count - 2, EmitCount = 2,
                SortKey = 5f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol,
                OptionalBoxMask = 0b10,
            });
            symbol++;
            boxes.Add(new SymbolBox { Min = new float2(1035, 995), Max = new float2(1065, 1005),
                SortKey = -1f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol });
            cands.Add(new SymbolCandidate
            {
                BoxStart = boxes.Count - 1, BoxCount = 1, EmitStart = boxes.Count - 1, EmitCount = 1,
                SortKey = -1f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol,
            });
            symbol++;

            for (int i = 0; i < pairCount; i++)
            {
                float x = (float)(rng.NextDouble() * 260.0);
                float y = (float)(rng.NextDouble() * 180.0);
                float sortKey = rng.Next(0, 5);
                int start = boxes.Count;
                // Owner box and rider box share the anchor and overlap — the pair geometry that makes
                // test-all-then-insert load-bearing. The world is deliberately small so these actually contend.
                boxes.Add(new SymbolBox { Min = new float2(x - 20, y - 20), Max = new float2(x + 20, y + 20),
                    SortKey = sortKey, FeatureIndex = symbol, TileKey = 0, SymbolIndex = symbol });
                boxes.Add(new SymbolBox { Min = new float2(x - 12, y - 8), Max = new float2(x + 34, y + 8),
                    SortKey = sortKey, FeatureIndex = symbol, TileKey = 0, SymbolIndex = symbol });
                cands.Add(new SymbolCandidate
                {
                    BoxStart = start, BoxCount = 2, EmitStart = start, EmitCount = 2,
                    SortKey = sortKey, FeatureIndex = symbol, TileKey = 0, SymbolIndex = symbol,
                    OptionalBoxMask = (byte)rng.Next(0, 4), // 0 = today's all-or-nothing, 1/2 = one half, 3 = both
                });
                symbol++;
            }
            for (int i = 0; i < singleCount; i++)
            {
                float x = (float)(rng.NextDouble() * 260.0);
                float y = (float)(rng.NextDouble() * 180.0);
                float sortKey = rng.Next(0, 5);
                boxes.Add(new SymbolBox { Min = new float2(x, y), Max = new float2(x + 30, y + 14),
                    SortKey = sortKey, FeatureIndex = symbol, TileKey = 1, SymbolIndex = symbol });
                cands.Add(new SymbolCandidate
                {
                    BoxStart = boxes.Count - 1, BoxCount = 1, EmitStart = boxes.Count - 1, EmitCount = 1,
                    SortKey = sortKey, FeatureIndex = symbol, TileKey = 1, SymbolIndex = symbol,
                });
                symbol++;
            }
            return (cands.ToArray(), boxes.ToArray());
        }

        [Test]
        public void Collision_OptionalMaskedPairs(
            [Values(4, 30, 120)] int pairCount, [Values(11, 404)] int seed)
        {
            var (cands, boxes) = RandomMaskedPairScene(pairCount, pairCount, seed);
            var flags = new bool[cands.Length];
            NativeCollisionRunner.RunCollision(cands, cands.Length, boxes, boxes.Length, flags);
            NativeCollisionRunner.AssertGreedyContract(cands, cands.Length, boxes, flags,
                $"masked pairs pairs={pairCount} seed={seed}");

            // A masked scene must actually EXERCISE the new branch — otherwise this tooth could pass on a
            // no-op. At least one candidate has to place while dropping a half.
            bool anyPartial = false;
            for (int i = 0; i < cands.Length; i++)
                if (flags[i] && cands[i].DroppedBoxMask != 0) { anyPartial = true; break; }
            Assert.IsTrue(anyPartial,
                "precondition: this scene must produce at least one partially-placed pair, or the check " +
                "is only re-checking the mask-0 path");
        }

        // Mask 0 everywhere must be byte-identical to the pre-stage-C behaviour: an all-or-nothing pair scene
        // records NO per-half verdict, and one blocked box still drops the whole candidate.
        [Test]
        public void UnmaskedPairs_StayAllOrNothing_AndRecordNoDroppedMask()
        {
            var (cands, boxes) = RandomMaskedPairScene(40, 40, 7);
            for (int i = 0; i < cands.Length; i++) cands[i].OptionalBoxMask = 0;

            var flags = new bool[cands.Length];
            NativeCollisionRunner.RunCollision(cands, cands.Length, boxes, boxes.Length, flags);
            NativeCollisionRunner.AssertGreedyContract(cands, cands.Length, boxes, flags,
                "unmasked pairs (the pre-stage-C reference shape)");

            int survivorCount = 0;
            for (int i = 0; i < cands.Length; i++)
            {
                Assert.AreEqual(0, cands[i].DroppedBoxMask,
                    "a candidate with no optional half must never record a DroppedBoxMask");
                if (flags[i]) survivorCount++;
            }
            Assert.Greater(survivorCount, 0, "sanity: the scene is not degenerate — something placed");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // CrossTileIdentityStoreTests — SymbolTileStore's dedup, the store-level half of cross-tile identity
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A-3: cross-tile point-symbol identity — <see cref="SymbolTileStore"/>'s dedup (the store-level half of
    /// <c>CrossTileIdentityTests</c>, moved here at the reader cutover, 4.2 — see that file's header).
    ///
    /// <para><b>Stage 3:</b> the store dedup no longer takes a caller grid — it keys on the fixed
    /// <see cref="CrossTileSymbolKey.CanonicalGridMeters"/> (4 m), so the cases below pass a gate <c>q</c> whose
    /// MAGNITUDE the store ignores; their anchors are spaced to merge/split under the fixed 4 m grid: (3) a
    /// symbol present in both a parent and child tile dedups to ONE, finest-zoom wins; (4) different text in the
    /// same cell does NOT merge; (5) line symbols are not deduped.</para>
    ///
    /// <para><b>Retired (4.2), not converted:</b> the pre-cutover file also had a <c>Store_QuantizeDisabled_EmitsEverything</c>
    /// case pinning "<c>quantizeMeters</c> ≤ 0 disables dedup, both copies emitted". The reader cutover's
    /// <see cref="SymbolTileStore.CollectInto(List{int},List{int},List{byte},double,out int)"/> is the ONLY
    /// surviving overload and it has no no-dedup branch left (the plan-aware shim already always ran the
    /// reconciler before this stage) — "fixing" that case to assert 1 instead of 2 would assert the OPPOSITE of
    /// its own name, so it is retired rather than repurposed.</para>
    /// </summary>
    [TestFixture]
    public class CrossTileIdentityStoreTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        // Appends one point symbol straight into `buffer` (the direct-buffer-builder idiom — see
        // Assets/Tests/MapRenderer.Tests.EditMode/Text/Placement/TestSymbolTileBuffer.cs) and returns the resulting
        // record, so a caller can both group it into its tile's buffer AND hold it for a later assertion.
        private static ShapedSymbol AddPointSymbol(SymbolTileBuffer buffer, double3 anchor, int layer, string text, TileId tile)
        {
            TestSymbolTileBuffer.AddPoint(buffer, anchor, null, float2.zero, float2.zero,
                text: text, materialIndex: layer, tileKey: SymbolTileKey.Pack(tile));
            return buffer.Symbols[buffer.Symbols.Count - 1];
        }

        // Test-only: a baked block's source buffer, so Collect() can read back the SAME ShapedSymbols the
        // pre-cutover managed-list CollectInto overload did (as records, not managed-object references).
        private readonly Dictionary<SymbolTileBlock, SymbolTileBuffer> _blockSources = new();

        private bool Commit(SymbolTileStore store, SymbolTileStore.Key key, int gen, SymbolTileBuffer buffer)
        {
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, slotCount: 1, double3.zero);
            bool committed = store.CompleteBuild(key, gen, block);
            _blockSources[block] = buffer;
            return committed;
        }

        private List<ShapedSymbol> Collect(SymbolTileStore store, double q)
        {
            var blockId = new List<int>(); var localIndex = new List<int>(); var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, q, out _);
            var output = new List<ShapedSymbol>(blockId.Count);
            for (int i = 0; i < blockId.Count; i++)
                output.Add(_blockSources[store.OrderedBlocks[blockId[i]]].Symbols[localIndex[i]]);
            return output;
        }

        // ── (3) THE seamless-swap dedup: the same symbol active in a parent + child tile collapses to ONE,
        //    keeping the finest (child) zoom. ──
        [Test]
        public void Store_ParentAndChildSameSymbol_DedupToFinest()
        {
            const double q = 50.0; // Stage 3: the store IGNORES this magnitude — it grids on the fixed 4 m; q only gates dedup ON.
            double3 anchor = new double3(5000.0, 0, 5000.0); // a CanonicalGridMeters=4 cell centre (5000 = 4·1250)
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };

            var parentBuffer = new SymbolTileBuffer();
            AddPointSymbol(parentBuffer, anchor + new double3(1, 0, 1), 0, "Metropolis", parent); // 1 m — well inside the 4 m cell
            var childBuffer = new SymbolTileBuffer();
            AddPointSymbol(childBuffer, anchor, 0, "Metropolis", child);

            var store = new SymbolTileStore(cacheCap: 8);
            var kParent = new SymbolTileStore.Key("src", parent);
            var kChild = new SymbolTileStore.Key("src", child);
            Commit(store, kParent, store.BeginBuild(kParent), parentBuffer);
            Commit(store, kChild, store.BeginBuild(kChild), childBuffer);

            List<ShapedSymbol> output = Collect(store, q);
            Assert.AreEqual(1, output.Count, "the duplicate parent+child symbol collapses to one");
            // ShapedSymbol is a struct — no reference identity (the pre-migration Assert.AreSame here compared
            // managed-object references). TileKey is the distinguishing field: it is the only one that
            // differs between the parent and child copies (both carry the same text/layer/anchor cell).
            Assert.AreEqual(SymbolTileKey.Pack(child), output[0].TileKey, "the finest (child) tile's label wins");
            store.Clear();
        }

        // ── (4) two GENUINELY distinct nearby symbols (different cells) both survive. ──
        [Test]
        public void Store_DistinctSymbols_BothSurvive()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };

            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, new double3(q * 10.5, 0, q * 10.5), 0, "A", tile);
            AddPointSymbol(buffer, new double3(q * 40.5, 0, q * 10.5), 0, "B", tile);

            var store = new SymbolTileStore(cacheCap: 8);
            var key = new SymbolTileStore.Key("src", tile);
            Commit(store, key, store.BeginBuild(key), buffer);
            Assert.AreEqual(2, Collect(store, q).Count, "distinct-cell symbols are not merged");
            store.Clear();
        }

        // ── (5) line symbols are excluded from dedup in v1 (two coincident line symbols both pass through). ──
        [Test]
        public void Store_LineSymbols_AreNotDeduped()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer, null, null, null,
                placement: SymbolPlacement.Line, text: "Main St", materialIndex: 0, tileKey: SymbolTileKey.Pack(tile));
            TestSymbolTileBuffer.AddCurved(buffer, null, null, null,
                placement: SymbolPlacement.LineCenter, text: "Main St", materialIndex: 0, tileKey: SymbolTileKey.Pack(tile));

            var store = new SymbolTileStore(cacheCap: 8);
            var key = new SymbolTileStore.Key("src", tile);
            Commit(store, key, store.BeginBuild(key), buffer);
            Assert.AreEqual(2, Collect(store, q).Count, "line labels pass through undeduped (per-anchor identity is a follow-up)");
            store.Clear();
        }

        // ── UMR-87: CrossTileSymbolKey.cs's own claim, observed directly (not just via the reconciler's
        //    winner-selection parity above, which proves it only indirectly). CrossTileSymbolKey.For (string-
        //    keyed) and DedupKey.For (int-keyed, UMR-87's PointFadeId/reconciler identity) MUST grid to the
        //    IDENTICAL (GridX, GridZ, GridY) for the same anchor — both call the ONE shared
        //    CrossTileSymbolKey.QuantizeAnchor. This is a STRUCTURAL guarantee today (one code path), but a
        //    future edit could silently fork the two `For` implementations; this pins the field values so that
        //    would fail loudly here instead of only surfacing as a mysterious dedup/fade-id mismatch elsewhere. ──
        [Test]
        public void QuantizeAnchor_GridsIdenticallyForStringAndIntKeyedIdentity()
        {
            double3 anchor = new double3(123_456.789, 0.0, -98_765.4321);
            const int layerId = 3;
            const double grid = CrossTileSymbolKey.CanonicalGridMeters;

            var stringKey = CrossTileSymbolKey.For(anchor, layerId, "T", "icon", grid);
            var intKey = DedupKey.For(anchor, layerId, 1, 2, grid); // textId=1, iconImageId=2 — arbitrary, irrelevant to grid math

            Assert.AreEqual(stringKey.GridX, intKey.GridX, "GridX");
            Assert.AreEqual(stringKey.GridZ, intKey.GridZ, "GridZ");
            Assert.AreEqual(stringKey.GridY, intKey.GridY, "GridY");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // HorizonCullGatherTests — the globe far-side horizon cull as a gather fade trigger
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S3: the globe far-side horizon cull as a <c>GatherSymbolPoints</c> fade trigger (peer of the tile/
    /// distance/departing culls) — a two-sided EditMode proof over a REAL <see cref="SphericalProjection"/>
    /// <see cref="MapCamera"/>. Teeth:
    /// <list type="bullet">
    ///   <item>(a) a FRESH (never-seen) far-side anchor is hard-skipped and produces no collision candidate
    ///     — a simple antipode sanity check, heading-independent (see its own header for why);</item>
    ///   <item>(b) a PREVIOUSLY-VISIBLE anchor that rotates behind the horizon EASES OUT (stays staged, fading,
    ///     its fade id force-faded) — never pops;</item>
    ///   <item>(c) the frame-consistency regression pin: a FIXED off-axis (oblique-bearing) anchor's horizon-cull
    ///     firing over THREE headings must match the normal {1,1,0} pattern — an East↔North swap OR a px/pz
    ///     sign flip between <c>ComputeRelativePose</c> and <c>TangentBasisAt</c> changes at least one entry
    ///     (numeric coverage table in the method's header), as does the East/North-dropped degeneracy; an
    ///     antipode can't move at all, so it can't stand in for this
    ///     — <see cref="OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes"/>;</item>
    ///   <item>Mercator is byte-identical: <see cref="IProjection.TryGetHorizonOccluder"/> returns false, so
    ///     <c>globeRadiusSq &lt; 0</c> makes the trigger an unconditional no-op (proven by every unmoved
    ///     Mercator symbol snapshot elsewhere — nothing to re-prove here).</item>
    /// </list>
    ///
    /// <para><b>Footgun avoided (Stage-U carry-over both reviewers flagged):</b> the harness builds its
    /// <see cref="SceneFrame"/> via <see cref="MapView.BuildSceneFrame"/> — the REAL 3-arg path wired off the
    /// live <see cref="MapCamera.CameraRelativePosition"/> — NOT the 2-arg ctor / <c>SceneFrame.Mercator</c>,
    /// which defaults <c>CameraRelativePosition</c> to <c>(0,0,0)</c> (camera at the sphere centre) and would
    /// silently misfire the cull.</para>
    /// </summary>
    [TestFixture]
    public class HorizonCullGatherTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, string text, int feature)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default, textSizePx: 24f, paddingPx: 2f, sortKey: 0f, text: text,
                featureIndex: feature, tileKey: 0L);

        // Epic A / A1: point symbols draw through the WORLD path now — see SymbolFadeTests.MaxAlpha's identical
        // header for the full rationale (fade opacity rides the world slot's stream-1 Opacity, not
        // system.Mesh's vertex-colour alpha).
        private static float MaxAlpha(SymbolPlacementSystem system, long tileKey = 0L)
            => system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh) ? WorldMeshReadback.MaxOpacity(mesh) : 0f;

        /// <summary>Great-circle destination point from the equator/prime-meridian (0,0) — the fixed look-at
        /// every test in this fixture uses — at compass <paramref name="bearingDeg"/> (CW from north) and
        /// angular <paramref name="distanceDeg"/>. Standard destination-point formula specialized to lat0=0:
        /// <c>lat = asin(sin(d)·cos(b))</c>, <c>lon = atan2(sin(b)·sin(d), cos(d))</c>.</summary>
        private static GeoCoordinate Destination(double bearingDeg, double distanceDeg)
        {
            double bearing  = bearingDeg   * math.PI_DBL / 180.0;
            double distance = distanceDeg  * math.PI_DBL / 180.0;
            double lat = math.asin(math.sin(distance) * math.cos(bearing));
            double lon = math.atan2(math.sin(bearing) * math.sin(distance), math.cos(distance));
            return new GeoCoordinate { Latitude = lat * 180.0 / math.PI_DBL, Longitude = lon * 180.0 / math.PI_DBL };
        }

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly MapCamera Camera;
            public readonly MapView View;
            public readonly GlyphAtlasTexture Atlas;
            private readonly GameObject _rootGo, _camGo;

            public Harness(CameraProperties initial)
            {
                _rootGo = new GameObject("HorizonCull_TestMapView");
                var component = _rootGo.AddComponent<MapViewComponent>();

                _camGo = new GameObject("HorizonCull_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);

                Camera = new MapCamera(uCam, initial, projection: new SphericalProjection());
                component.SetCamera(Camera);
                View = component.View;

                Atlas = BuildTinyAtlasTexture();
                // Epic A / A1: point symbols now draw through the world path — the demo tick needs its own
                // world base material for a live opacity read (see MaxAlpha's header).
                System = new SymbolPlacementSystem(Camera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            /// <summary>The REAL 3-arg <see cref="MapView.BuildSceneFrame"/> path — see the class header's
            /// "footgun avoided" note. Re-read every call: <see cref="SetProperties"/> changes it.</summary>
            public SceneFrame Frame() => View.BuildSceneFrame(Camera.CurrentProperties);

            /// <summary>Mirrors <c>MapView.LateUpdate</c>'s load-bearing order: <c>SyncToCamera</c> BEFORE
            /// <c>BuildSceneFrame</c>, so <see cref="MapCamera.CameraRelativePosition"/> is fresh when
            /// <see cref="Frame"/> is next called.</summary>
            public void SetProperties(CameraProperties props)
            {
                Camera.SetProperties(props);
                Camera.SyncToCamera();
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_rootGo);
                Object.DestroyImmediate(_camGo);
            }
        }

        /// <summary>Simple far-side sanity check — NOT a frame-consistency pin (see
        /// <see cref="OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes"/> for that). The
        /// antipode of the look-at rebases to exactly <c>(0,−2R,0)</c> for ANY heading — its render-X and
        /// render-Z land on zero identically, so <c>HorizonCull</c>'s <c>dot(pc,cc)</c> reduces to the Y term
        /// alone (heading never enters it). It still proves the hard-skip mechanics (fresh far anchor ⇒ no
        /// quad, no candidate, attributed to the horizon telemetry bucket), just not the East/North wiring.</summary>
        [Test]
        public void FreshFarSideAnchor_IsHardSkipped_AbsentFromCollision()
        {
            var lookAt = new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 };
            var initial = new CameraProperties(lookAt, zoom: 2.0, heading: 45.0, tilt: 45.0);
            using var h = new Harness(initial);

            IProjection proj = h.Camera.Projection;
            double3 farAnchor = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 180.0 }); // antipode

            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, farAnchor, "F", 0);
            SceneFrame frame = h.Frame();
            h.System.TickSymbols(in frame, buffer, h.Atlas, h.Camera.Projection); // fresh — never seen, no live fade to ease out

            Assert.AreEqual(0, h.System.LastQuadCount, "a fresh far-side anchor produces no geometry (hard-skip)");
            Assert.AreEqual(0, h.System.LastCandidateCount, "…and never enters the collision pass");
            Assert.AreEqual(1, h.System.LastHorizonCulledCount, "…attributed to the S3 horizon cull");
            Assert.AreEqual(0, h.System.LastDistanceCulledCount, "the B-3 radius must not preempt the horizon trigger here");
        }

        /// <summary>
        /// THE frame-consistency regression pin — a real one: it goes RED under an East↔North axis swap OR a
        /// single-axis (px or pz) sign flip between <c>CameraPoseMath.ComputeRelativePose</c>'s pose and
        /// <c>Ecef.TangentBasis</c>'s East/North columns, not just the gross "East/North dropped" degeneracy.
        ///
        /// <para><b>Why three headings, not the obvious two.</b> For a FIXED anchor (bearing β, arc-distance θ)
        /// and camera at heading H / tilt T, <c>HorizonCull</c>'s dot reduces to
        /// <c>dot(pc,cc) = R·[cosθ·cy − s·sinθ·cos(H−β)]</c> (<c>s = alt·sinT</c>, <c>cy = alt·cosT + R</c>), so
        /// <b>hidden ⟺ cos(H−β) &gt; K</b>, <c>K = (cosθ·cy − R)/(s·sinθ)</c>. Each frame defect rewrites only the
        /// phase/argument: an E↔N swap (either side) → <c>sin(H+β) &gt; K</c>; a px flip → <c>cos(H+β) &gt; K</c>;
        /// a pz flip → <c>cos(H+β) &lt; −K</c>; East/North dropped → constant. Two headings 180° apart CANNOT
        /// separate all of these (the swap and px flip survive that flip — that was an earlier, weaker version of
        /// this test), and β=45° is degenerate (<c>sin(H+45)≡cos(H−45)</c>, so a swap is invisible). An oblique
        /// β plus THREE headings does separate them.</para>
        ///
        /// <para><b>Config &amp; numeric coverage (simulated against the real production formulas; the normal row
        /// also matches the live gate — see <see cref="FreshFarSideAnchor_IsHardSkipped_AbsentFromCollision"/>'s
        /// β=35 datapoint).</b> β=20°, θ=50°, tilt=45°, zoom=2 ⇒ K≈−0.195, R²≈4.07×10¹³. Horizon-fires
        /// (<c>LastHorizonCulledCount</c>) over headings {0°, 90°, 150°}:
        /// <list type="table">
        ///   <item><term>normal  </term><description>{1, 1, 0}  ← asserted; margins dot−R² = −1.6e13 / −7.5e12 / +6.3e12 (all ≥6e12, non-flaky)</description></item>
        ///   <item><term>E↔N swap</term><description>{1, 1, 1}  differs at 150° → RED</description></item>
        ///   <item><term>px flip </term><description>{1, 0, 0}  differs at 90°  → RED</description></item>
        ///   <item><term>pz flip </term><description>{0, 1, 1}  differs at 0°   → RED</description></item>
        ///   <item><term>E/N drop</term><description>{1, 1, 1}  differs at 150° → RED</description></item>
        /// </list>
        /// So every East/North wiring defect changes at least one of the three asserted outcomes.</para>
        ///
        /// <para>The signal is the horizon-cull decision (<c>LastHorizonCulledCount</c>), not a drawn quad: some
        /// configs leave the anchor behind the tilted camera (a separate downstream cull) which would confound a
        /// quad-count assertion. Frames are built through the REAL <see cref="MapView.BuildSceneFrame"/> path so
        /// the live <c>ComputeRelativePose</c>→<c>TangentBasisAt</c> integration is what's under test.</para>
        /// </summary>
        [Test]
        public void OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes()
        {
            var lookAt = new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 };
            const double bearingDeg = 20.0, distanceDeg = 50.0; // oblique β so a swap/px-flip is visible (β≠0,45,90)
            GeoCoordinate anchorGeo = Destination(bearingDeg, distanceDeg);

            // Normal-code horizon-fire pattern {1,1,0} over these headings; ANY East↔North swap or px/pz sign flip
            // changes at least one entry (see the coverage table in the doc). tilt=45° so the camera leans and the
            // fixed off-axis anchor's occlusion is genuinely heading-dependent through px/pz.
            var cases = new (double headingDeg, int expectedHorizonCulled)[] { (0.0, 1), (90.0, 1), (150.0, 0) };
            foreach (var (headingDeg, expected) in cases)
            {
                using var h = new Harness(new CameraProperties(lookAt, zoom: 2.0, heading: headingDeg, tilt: 45.0));
                double3 anchor = h.Camera.Projection.Project(anchorGeo);
                var buffer = new SymbolTileBuffer();
                AddPoint(buffer, anchor, "A", 0);
                SceneFrame frame = h.Frame();
                h.System.TickSymbols(in frame, buffer, h.Atlas, h.Camera.Projection);

                Assert.AreEqual(expected, h.System.LastHorizonCulledCount,
                    $"heading {headingDeg}°: horizon-cull fire must match the normal {{1,1,0}} pattern — a swap or " +
                    "px/pz sign flip in the ComputeRelativePose↔TangentBasis frame changes this (see coverage table)");
            }
        }

        [Test]
        public void PreviouslyVisibleAnchor_RotatedBehindHorizon_EasesOut_NotPops()
        {
            var origin = new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 };
            var initial = new CameraProperties(origin, zoom: 2.0, heading: 0.0, tilt: 0.0);
            using var h = new Harness(initial);

            IProjection proj = h.Camera.Projection;
            // The SAME geo anchor throughout — only the CAMERA orbits (LookAt moves to the antipode), so the
            // fade id (hashed off this fixed render-space anchor) stays stable across the transition.
            double3 anchor = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 0.0 });
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, anchor, "A", 0);

            // 1) The camera looks straight at the anchor — visible, snaps to full opacity (default deltaTime).
            // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
            SceneFrame frame1 = h.Frame();
            h.System.TickSymbols(in frame1, buffer, h.Atlas, h.Camera.Projection);
            h.System.TickSymbols(in frame1, buffer, h.Atlas, h.Camera.Projection);
            Assert.AreEqual(1, h.System.LastQuadCount, "the anchor places while the camera looks at it");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…at full opacity");

            // 2) The camera rotates to look at the ANCHOR'S ANTIPODE — the same anchor is now on the far side.
            //    It must keep drawing while it fades, not vanish for a frame.
            var rotated = new GeoCoordinate3D { Latitude = 0.0, Longitude = 180.0, Altitude = 0.0 };
            h.SetProperties(new CameraProperties(rotated, zoom: 2.0, heading: 0.0, tilt: 0.0));
            SceneFrame frame2 = h.Frame();
            h.System.TickSymbols(in frame2, buffer, h.Atlas, h.Camera.Projection, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a horizon-occluded-but-visible anchor keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is finally dropped — attributed to horizon telemetry.
            for (int i = 0; i < 10; i++)
            {
                SceneFrame frame3 = h.Frame();
                h.System.TickSymbols(in frame3, buffer, h.Atlas, h.Camera.Projection, deltaTime: 0.1f);
            }
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the horizon-occluded anchor is fully skipped");
            Assert.Greater(h.System.LastHorizonCulledCount, 0, "…and its skip is attributed to horizon telemetry");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // IconSkirtCarrierChainTests — the icon skirt's carrier chain end to end from a padded atlas
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The icon skirt's CARRIER CHAIN, driven end to end from a genuinely padded
    /// <see cref="SpriteAtlasView"/>: <c>SymbolFeatureExtractor</c> (computes
    /// <c>IconQuadLayout.SkirtPx</c>) → <c>SymbolFeature.IconSkirtPx</c> → <see cref="StyledSymbolTileBuilder"/>
    /// → the point symbol's <c>Layout.Bounds*</c> and the along-line symbol's <c>CurvedGlyph.CellSkirt</c>.
    ///
    /// <para><b>Why this exists as its own tooth.</b> Every other skirt test calls the two ends directly —
    /// <c>ToLayoutResult(quad, SkirtPx(...))</c> or <c>BuildRotatedGlyph(..., skirt: 3f)</c> — so all of them
    /// stay green against an implementation that never computes the skirt during extraction, drops one of
    /// the <c>IconSkirtPx</c> assignments, or emits <c>CellSkirt = 0</c>. The render snapshots cannot see it
    /// either: they draw the PADDED quad, which is unchanged by a lost skirt. Only the collision footprint
    /// moves, and only a test that starts at extraction can observe that.</para>
    ///
    /// <para>The atlas is built by running the real <c>SpriteSheetPadder</c> over a raw parsed index rather
    /// than by hand-setting <c>Padding</c>, so the entries under test are the ones production would bind.</para>
    /// </summary>
    [TestFixture]
    public class IconSkirtCarrierChainTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private const int Padding = 1;
        private const int SpriteExtent = 16;
        private const float IconSize = 2f;
        private static readonly int2 SourceSheetSize = new int2(64, 64);
        private static readonly TileId TileId0 = new TileId { Z = 1, X = 0, Y = 0 };
        private const uint Extent = 4096;

        /// <summary>Two 16×16 abutting sprites — the real sheet's shape — run through the production planner.</summary>
        private static SpritePadPlan PaddedPlan() => SpriteSheetPadder.Plan(
            SpriteIndex.Parse(
                "{\"marker\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"arrow\":{\"x\":16,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}"),
            SourceSheetSize, Padding);

        private static SpriteAtlasView PaddedAtlas(SpritePadPlan plan)
            => new SpriteAtlasView { Index = plan.Index, Size = plan.Size };

        /// <summary>The same sprite as it would be WITHOUT the repack — the reference the collision footprint
        /// must still equal. Only Width/Height/PixelRatio reach the box (X/Y are UV-only), so this is the
        /// content-rect entry, byte-identical to a raw parse.</summary>
        private static readonly SpriteEntry BareEntry = new SpriteEntry
        {
            X = 0, Y = 0, Width = SpriteExtent, Height = SpriteExtent, PixelRatio = 1f,
        };

        // ── The point path: Layout.Bounds must be the UNPADDED content box ────────────────────────────

        [Test]
        public async Task PointIcon_ThroughRealExtraction_CollidesOnTheContentBox_NotThePaddedQuad()
        {
            SpritePadPlan plan = PaddedPlan();
            SpriteAtlasView atlas = PaddedAtlas(plan);
            Assert.IsTrue(atlas.Index.TryGetSprite("marker", out SpriteEntry padded));
            Assert.AreEqual(Padding, padded.Padding,
                "precondition: the planner must actually have padded this sprite, or the tooth is vacuous");

            var buffer = new SymbolTileBuffer();
            using (GlyphManager manager = IconOnlyGlyphManager())
            {
                var builder = new StyledSymbolTileBuilder(manager);
                await builder.BuildAsync(
                    OnePointTile(new double2(100, 200)), TileId0,
                    new[] { PointIconLayer() }, 0.0, new WebMercatorProjection(), buffer,
                    spriteAtlas: atlas);
            }

            Assert.AreEqual(1, buffer.Symbols.Count, "the icon-only feature must emit exactly one label");
            ShapedSymbol icon = buffer.Symbols[0];
            Assert.AreEqual(SymbolKind.Icon, icon.Kind);

            // The reference: the very same layout with NO border at all. That box is what collision saw
            // before the repack and must still see after it.
            SymbolQuad bareQuad = IconQuadLayout.Layout(
                BareEntry, atlas.Size, IconSize, TextAnchor.Center, float2.zero);

            const float eps = 1e-5f;
            Assert.AreEqual(math.min(bareQuad.TopLeft.x, bareQuad.BottomRight.x), icon.BoundsMin.x, eps, "BoundsMin.x");
            Assert.AreEqual(math.min(bareQuad.TopLeft.y, bareQuad.BottomRight.y), icon.BoundsMin.y, eps, "BoundsMin.y");
            Assert.AreEqual(math.max(bareQuad.TopLeft.x, bareQuad.BottomRight.x), icon.BoundsMax.x, eps, "BoundsMax.x");
            Assert.AreEqual(math.max(bareQuad.TopLeft.y, bareQuad.BottomRight.y), icon.BoundsMax.y, eps, "BoundsMax.y");

            // Non-vacuity: the DRAWN quad must be strictly bigger than the collision box, by exactly the
            // skirt. Without this, an atlas that silently lost its padding would satisfy everything above.
            float expectedSkirt = IconQuadLayout.SkirtPx(padded, IconSize);
            Assert.AreEqual(Padding * IconSize, expectedSkirt, eps, "precondition: a 1-texel border at icon-size 2 is 2px");
            SymbolQuad drawn = buffer.Quads[icon.QuadStart];
            Assert.AreEqual(icon.BoundsMin.x - expectedSkirt, drawn.TopLeft.x, eps,
                "the RENDER quad must keep the skirt the collision box removed — the two representations " +
                "part company here, and only here.");
            Assert.AreEqual(icon.BoundsMax.x + expectedSkirt, drawn.BottomRight.x, eps);
        }

        // ── The along-line path: CurvedGlyph.CellSkirt must reach the rotated collision box ────────────

        [Test]
        public async Task AlongLineIcon_ThroughRealExtraction_RotatedBoxEqualsTheUnpaddedCell()
        {
            SpritePadPlan plan = PaddedPlan();
            SpriteAtlasView atlas = PaddedAtlas(plan);
            Assert.IsTrue(atlas.Index.TryGetSprite("arrow", out SpriteEntry padded));
            Assert.AreEqual(Padding, padded.Padding, "precondition: the planner must actually have padded this sprite");

            var buffer = new SymbolTileBuffer();
            using (GlyphManager manager = IconOnlyGlyphManager())
            {
                var builder = new StyledSymbolTileBuilder(manager);
                await builder.BuildAsync(
                    OneLineTile(new double2(500, 500), new double2(3500, 3500)), TileId0,
                    new[] { AlongLineIconLayer() }, 0.0, new WebMercatorProjection(), buffer,
                    spriteAtlas: atlas);
            }

            Assert.AreEqual(1, buffer.Symbols.Count, "the map-aligned line icon must emit exactly one curved label");
            ShapedSymbol icon = buffer.Symbols[0];
            Assert.AreEqual(SymbolKind.Icon, icon.Kind);
            Assert.AreEqual(1, icon.GlyphCount, "an along-line icon is a ONE-glyph curved label");
            CurvedGlyph glyph = buffer.Glyphs[icon.GlyphStart];

            float expectedSkirt = IconQuadLayout.SkirtPx(padded, IconSize);
            Assert.Greater(expectedSkirt, 0f, "precondition: a padded sprite has a non-zero skirt");
            Assert.AreEqual(expectedSkirt, glyph.CellSkirt, 1e-5f,
                "the extractor's skirt must reach CurvedGlyph.CellSkirt — an emit that hard-codes 0 leaves " +
                "every along-line icon colliding on its transparent border.");

            // The consequence, not just the carried number: the rotated collision box built from the padded
            // cell + its skirt must equal the one built from the BARE cell with no skirt at all.
            SymbolQuad bareCell = IconQuadLayout.Layout(
                BareEntry, atlas.Size, IconSize, TextAnchor.Center, float2.zero);
            var anchor = new float2(120f, -40f);
            const float rotation = 0.7f;

            SymbolBox actual = SymbolBox.BuildRotatedGlyph(
                anchor, glyph.Cell, TextQuadLayout.OneEm, rotation, paddingPx: 0f, cellSkirt: glyph.CellSkirt);
            SymbolBox expected = SymbolBox.BuildRotatedGlyph(
                anchor, bareCell, TextQuadLayout.OneEm, rotation, paddingPx: 0f, cellSkirt: 0f);

            const float eps = 1e-4f;
            Assert.AreEqual(expected.Min.x, actual.Min.x, eps, "rotated Min.x");
            Assert.AreEqual(expected.Min.y, actual.Min.y, eps, "rotated Min.y");
            Assert.AreEqual(expected.Max.x, actual.Max.x, eps, "rotated Max.x");
            Assert.AreEqual(expected.Max.y, actual.Max.y, eps, "rotated Max.y");

            // Non-vacuity: the padded cell with skirt 0 must be a DIFFERENT box, or the comparison above
            // could not discriminate a lost skirt.
            SymbolBox unshrunk = SymbolBox.BuildRotatedGlyph(
                anchor, glyph.Cell, TextQuadLayout.OneEm, rotation, paddingPx: 0f, cellSkirt: 0f);
            Assert.Greater(math.abs(unshrunk.Min.x - expected.Min.x), 10f * eps,
                "precondition: dropping the skirt must visibly change the box, or this tooth is vacuous");
        }

        // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>A glyph manager whose source serves nothing. Legitimate here: an icon-only layer never
        /// requests a range (pass 1 skips <c>SymbolKind.Icon</c>) and never builds a font-stack resolver, so
        /// touching it at all would itself be the defect.</summary>
        private static GlyphManager IconOnlyGlyphManager()
            => new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));

        private static SymbolStyle.StyleLayer PointIconLayer()
            => new SymbolStyle.StyleLayer
            {
                Id = "points",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"icon-image\":\"marker\",\"icon-size\":2}"),
            };

        /// <summary>`symbol-placement: line` with `icon-rotation-alignment` unset ⇒ resolves `auto → map`,
        /// which is the P-B one-glyph-curved-symbol emit shape.</summary>
        private static SymbolStyle.StyleLayer AlongLineIconLayer()
            => new SymbolStyle.StyleLayer
            {
                Id = "roads",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "roads",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"icon-image\":\"arrow\",\"icon-size\":2,\"symbol-placement\":\"line\"}"),
            };

        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        private static IDecodedTile OnePointTile(double2 point)
        {
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Point, hasId: false, geometry: new[] { 1u | (1u << 3), ZigZagEncode((long)point.x), ZigZagEncode((long)point.y) });
            return TestDecodedTiles.Of("points", TileId0, new List<IFeature> { feature }, Extent);
        }

        private static IDecodedTile OneLineTile(double2 from, double2 to)
        {
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: new[]
                {
                    1u | (1u << 3), ZigZagEncode((long)from.x), ZigZagEncode((long)from.y),
                    2u | (1u << 3), ZigZagEncode((long)(to.x - from.x)), ZigZagEncode((long)(to.y - from.y)),
                });
            return TestDecodedTiles.Of("roads", TileId0, new List<IFeature> { feature }, Extent);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapPitchedWorldArcStagingTests — curved world-arc staging under a pitched camera
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapPitchedWorldArcStagingTests
    {
        private struct Pools
        {
            public SymbolBox[] Boxes; public int BoxCount;
            public PlacedQuad[] Quads; public int QuadCount;
            public SymbolCandidate[] Candidates; public CandidateEmit[] Emit; public int EmitCount;
            public static Pools New() => new Pools
            {
                Boxes = new SymbolBox[64], Quads = new PlacedQuad[64],
                Candidates = new SymbolCandidate[64], Emit = new CandidateEmit[64],
            };
        }

        /// <summary>A glyph cell of the given half-width in baked px, 12 baked px tall, no skirt.</summary>
        private static SymbolQuad Cell(float halfWidthBakedPx) => new SymbolQuad
        {
            TopLeft = new float2(-halfWidthBakedPx, 6f), BottomRight = new float2(halfWidthBakedPx, -6f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1f, 1f), LineIndex = 0,
        };

        private static CurvedStageInput Input(AlignmentMode pitch, float metresPerLogicalPixel,
            float textSizePx, float maxAngleDeg, int featureIndex) => new CurvedStageInput
        {
            TextSizePx = textSizePx, PaddingPx = 0f, SortKey = 0f,
            FeatureIndex = featureIndex, TileKey = 7, Slot = 0,
            TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
            MaxAngleDeg = maxAngleDeg, KeepUpright = false, Color = new float4(1f, 1f, 1f, 1f),
            PitchAlignment = pitch, MetresPerLogicalPixel = metresPerLogicalPixel,
        };

        /// <summary>
        /// Stages one symbol. <paramref name="view"/> defaults to <c>default(SymbolViewTransform)</c> — W3's
        /// "no camera was supplied" state, which keeps the pre-W3 SCREEN collision box byte-identical — so
        /// every W1/W2 caller above is untouched by W3 and its expectations still describe the same code.
        /// <paramref name="worldUpPathOverride"/> likewise defaults to the all-zero up path these fixtures
        /// have always passed (a degenerate ground frame, W3-T6's subject).
        /// </summary>
        private static int Stage(in CurvedStageInput s, float2[] screenPath, double3[] worldPath,
            CurvedGlyph[] glyphs, LineAnchor[] anchors, ref Pools p, float[] depthPathOverride = null,
            SymbolViewTransform view = default, float3[] worldUpPathOverride = null, int ordinal = 0)
        {
            int n = screenPath.Length;
            var depthPath = depthPathOverride ?? new float[n];
            var validPath = new byte[n];
            for (int v = 0; v < n; v++) validPath[v] = 1;
            var worldUpPath = worldUpPathOverride ?? new float3[n];
            var fadeIds = new long[anchors.Length + 1];
            for (int a = 0; a < fadeIds.Length; a++)
                fadeIds[a] = SymbolStagingMath.LineFadeId(s.TileKey, 0, s.FeatureIndex, a == anchors.Length ? -1 : a);
            return SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath,
                glyphs, anchors, fadeIds, new byte[anchors.Length + 1], new float2[n], new float[n],
                bearingRadians: 0f, view: view, ordinal: ordinal,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T6 — R5a's observing tooth: the screen point of a WORLD parameter is the AFFINE lerp.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T6 — the observer for the recorded R5a limitation.</b> Proves: under the world arc walk,
        /// <c>AtWithSegment</c> maps a resolved <c>(seg, t)</c> to the screen with a plain AFFINE
        /// <c>lerp(path[seg], path[seg+1], t)</c>, and NOT with the perspective-correct screen parameter
        /// <c>t' = t·w₁ / ((1−t)·w₀ + t·w₁)</c> — the WORLD→SCREEN map, whose inverse (the familiar
        /// screen→attribute form, with the w's transposed) is a different function and is not what this site
        /// would need.
        ///
        /// <para>The fixture DECLARES the endpoint clip w's (300 m and 600 m, a 2:1 receding segment); it does
        /// not read them from production, because production cannot have them —
        /// <c>SymbolProjectionJob.OutDepth</c> is NDC depth (<c>clip.z / clip.w</c>) and clip <c>w</c> is not
        /// recoverable from it without the projection's m22/m23. That is the whole reason the limitation is
        /// recorded rather than fixed: it is not implementable from today's inputs.</para>
        ///
        /// <para>At the world midpoint (<c>t = 0.5</c>) the perspective-correct parameter collapses to
        /// <c>w₁/(w₀+w₁) = 2/3</c> — PAST the affine ½, toward the far endpoint, because a receding segment's
        /// far half is compressed on screen. It is a sixth of the segment away from the affine answer, which
        /// on this 300-px screen segment is exactly 50 px.</para>
        ///
        /// <para><b>The 50 px is symmetric about ½, so do not re-derive the bound from the sign of the
        /// error.</b> <c>|2/3 − ½|</c> and <c>|1/3 − ½|</c> are both 1/6, so the separation this tooth asserts
        /// is the same whichever direction the perspective correction runs — which is exactly why an inverted
        /// formula survived here undetected until review. The direction is pinned by the doc above and by the
        /// <c>tPrime &gt; 0.5</c> precondition below, not by the 50 px. Both numbers are asserted, so the
        /// tooth cannot pass by coincidence on a degenerate geometry.</para>
        ///
        /// <para>Single-segment on purpose: a lone segment resolves <c>t = 0.5</c> under BOTH walks, so this
        /// tooth pins the <c>(seg,t) → screen</c> MAPPING alone and is deliberately unaffected by injection I1
        /// (kill the world branch). Anyone who implements <c>t'</c> turns it RED and must delete the recorded
        /// limitation from <c>StageCurved</c>'s doc.</para>
        /// </summary>
        [Test]
        public void MapPitched_ScreenPointOfAWorldParameter_IsAffine_NotPerspectiveCorrect()
        {
            var screenPath = new[] { new float2(100f, 300f), new float2(400f, 300f) }; // 300 px long
            var worldPath  = new[] { new double3(0, 0, 0), new double3(0, 0, 2000) };  // 2000 m long
            var glyphs  = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(10f) } };
            var anchors = new[] { new LineAnchor(0, 0.5f) };                            // world arc 1000 m
            CurvedStageInput s = Input(AlignmentMode.Map, metresPerLogicalPixel: 10f,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 6);
            var p = Pools.New();

            // The declared endpoint clip w's are ALSO written into the depth span — not because production
            // reads it (StageCurved uses depthPath for the per-symbol sort depth and nothing else, and this
            // tooth's GREEN result is independent of what is in it), but because reaching for `depthPath` as
            // if it held clip w is the precise wrong move StageCurved's doc warns about. Putting real w's
            // there means an implementation that makes that mistake produces the perspective-correct point
            // and this tooth catches it, instead of silently reading zeros and staying green.
            const double w0 = 300.0, w1 = 600.0;
            var depthPathCarryingW = new[] { (float)w0, (float)w1 };

            int staged = Stage(in s, screenPath, worldPath, glyphs, anchors, ref p, depthPathCarryingW);
            Assert.That(staged, Is.EqualTo(1), "W1-T6 precondition: the single-glyph label must stage.");

            // The two candidate answers, both computed HERE from the fixture's own constants.
            double2 a0 = new double2(screenPath[0].x, screenPath[0].y);
            double2 a1 = new double2(screenPath[1].x, screenPath[1].y);
            double2 affine = math.lerp(a0, a1, 0.5);                       // t = 0.5
            // t' = t·w₁ / ((1−t)·w₀ + t·w₁), which at t = 0.5 collapses to w₁/(w₀+w₁) — 2/3 for a 2:1
            // segment. NOT w₀/(w₀+w₁): that is the inverse (screen→attribute) map and points the correction
            // at the camera instead of at the far endpoint.
            double tPrime = w1 / (w0 + w1);
            double2 perspectiveCorrect = math.lerp(a0, a1, tPrime);
            double separationPx = math.length(perspectiveCorrect - affine);

            // The DIRECTION, pinned separately from the magnitude: the separation below is symmetric about
            // ½, so it alone cannot tell w₁/(w₀+w₁) from w₀/(w₀+w₁). On a receding segment (w₁ > w₀) the
            // world midpoint must project PAST the screen midpoint, toward the far endpoint.
            Assert.That(tPrime, Is.GreaterThan(0.5),
                $"W1-T6 precondition: on a receding segment (w₀={w0:F0} < w₁={w1:F0}) the perspective-correct " +
                $"parameter must exceed the affine ½ — reads {tPrime:F6}. A value below ½ means the formula " +
                "has been transposed into the inverse screen→attribute map.");

            double2 staged0 = new double2(p.Quads[0].AnchorScreenPx.x, p.Quads[0].AnchorScreenPx.y);
            Assert.That(math.length(staged0 - affine), Is.LessThan(1e-3),
                $"W1-T6: the staged screen anchor must be the AFFINE lerp at the world parameter — staged " +
                $"({staged0.x:F4}, {staged0.y:F4}) vs affine ({affine.x:F4}, {affine.y:F4}). This is the " +
                "RECORDED R5a limitation; if you implemented the perspective-correct t', delete the " +
                "limitation from StageCurved's doc rather than widening this bound.");
            Assert.That(separationPx, Is.EqualTo(50.0).Within(1e-6),
                $"W1-T6 precondition: the perspective-correct answer must be a STATED distance away, or this " +
                $"tooth discriminates nothing — t'={tPrime:F6} against 0.5 on a 300 px segment is " +
                $"{separationPx:F6} px (expected 50).");
            Assert.That(math.length(staged0 - perspectiveCorrect), Is.EqualTo(50.0).Within(1e-3),
                $"W1-T6: the staged anchor must differ from the perspective-correct answer " +
                $"({perspectiveCorrect.x:F4}, {perspectiveCorrect.y:F4}) by the full 50 px — measured " +
                $"{math.length(staged0 - perspectiveCorrect):F4} px.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T7 — R5b's observing tooth: the max-angle gate stays on the SCREEN tangent.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T7 — the observer for the recorded R5b limitation.</b> Proves: a map-pitched symbol whose
        /// SCREEN path is kinked past <c>text-max-angle</c> is DROPPED, even though its WORLD path is very
        /// nearly straight. The gate answers "is the projected path too kinky to place a symbol on", and W1
        /// deliberately leaves it there.
        ///
        /// <para>The control clause is what makes it non-vacuous: the SAME world path with a STRAIGHT screen
        /// path stages. So the drop is attributable to the screen kink and not to any other precondition of
        /// this geometry.</para>
        ///
        /// <para>RED-verify: move the gate to the world tangent (injection I6) — the world bend is 2°, well
        /// inside the 30° limit, so the kinked case would stage and this goes RED.</para>
        /// </summary>
        [Test]
        public void MapPitched_MaxAngleGate_ReadsTheScreenTangent_NotTheWorldTangent()
        {
            // World: two 1000 m segments bent by 2° — a real polyline, comfortably inside a 30° gate.
            double worldBendRad = math.radians(2.0);
            var worldPath = new[]
            {
                new double3(0, 0, 0),
                new double3(0, 0, 1000),
                new double3(1000 * math.sin(worldBendRad), 0, 1000 + 1000 * math.cos(worldBendRad)),
            };
            // Screen: the SAME three vertices, kinked 90° — five times the gate.
            var kinkedScreen   = new[] { new float2(0f, 200f), new float2(200f, 200f), new float2(200f, 400f) };
            var straightScreen = new[] { new float2(0f, 200f), new float2(200f, 200f), new float2(400f, 200f) };

            // Two glyphs, one either side of the interior vertex: with arcScale = 10 m/baked-px and a 100
            // baked-px advance, they sit at world arc 500 m (segment 0) and 1500 m (segment 1), so the gate's
            // g > 0 comparison is genuinely BETWEEN the two segments' tangents.
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f,   Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(1, 0f) }; // world arc 1000 m — the interior vertex
            CurvedStageInput s = Input(AlignmentMode.Map, metresPerLogicalPixel: 10f,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 30f, featureIndex: 7);

            var kinkedPools = Pools.New();
            int stagedKinked = Stage(in s, kinkedScreen, worldPath, glyphs, anchors, ref kinkedPools);
            var straightPools = Pools.New();
            int stagedStraight = Stage(in s, straightScreen, worldPath, glyphs, anchors, ref straightPools);

            Assert.That(stagedKinked == 0 && stagedStraight == 1, Is.True,
                $"W1-T7: the max-angle gate must read the SCREEN tangent — the 90°-kinked screen path staged " +
                $"{stagedKinked} candidates (expected 0, dropped) and the straight screen path over the SAME " +
                $"world polyline staged {stagedStraight} (expected 1). The world path bends only 2°, so a " +
                "gate moved onto world curvature would keep BOTH. If that move was deliberate, delete R5b " +
                "from StageCurved's doc rather than relaxing this.");
            Assert.That(kinkedPools.QuadCount, Is.EqualTo(0),
                $"W1-T7: a dropped anchor must roll its partial appends back — {kinkedPools.QuadCount} quads " +
                "were left behind.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T8 / W1-T9 — the stage invariant and the degradation guard, as code properties.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T8 — THE STAGE INVARIANT, AS A TEST.</b> Proves: a symbol whose resolved pitch alignment is
        /// not <see cref="AlignmentMode.Map"/> cannot observe the new per-frame ruler AT ALL — its staged
        /// boxes and quads are BIT-identical with <c>MetresPerLogicalPixel</c> at 0, 1 and 10⁶, four orders of
        /// magnitude apart. Run for <see cref="AlignmentMode.Viewport"/> and for
        /// <see cref="AlignmentMode.Auto"/> (the enum's zero value, which is what every hand-built pre-W1
        /// fixture carries — the hinge the whole 2099-test invariant hangs on).
        ///
        /// <para>This is what turns §1's invariant from a claim about the current test suite into a property
        /// of the code: a future symbol that resolves to viewport pitch cannot start moving because someone
        /// changed the ruler.</para>
        ///
        /// <para>RED-verify: injection I7 (drop the <c>== AlignmentMode.Map</c> conjunct, so every symbol takes
        /// the world walk).</para>
        ///
        /// <para><b>W2 discharged W1's followUp 1 — this tooth is no longer alone.</b> It used to be the SOLE
        /// observer of the <c>PitchAlignment == Map</c> conjunct in the whole suite, so weakening it would
        /// have left the predicate unobserved. W2 added a SECOND, independent consumer of the same
        /// <c>worldArc</c> bool (<c>CandidateEmit.CornerMetresPerLogicalPixel</c>), watched by W2-T6's three
        /// cases below and by W2-T7. They read the same predicate through a different output, so I7 now reds
        /// several teeth rather than one.</para>
        /// </summary>
        [Test]
        public void NonMapPitchedSymbol_CannotObserveTheRuler_AtAnyMagnitude()
        {
            foreach (AlignmentMode mode in new[] { AlignmentMode.Viewport, AlignmentMode.Auto })
            {
                float[] rulers = { 0f, 1f, 1e6f };
                var quadBits = new uint[rulers.Length][];
                var boxBits  = new uint[rulers.Length][];
                for (int r = 0; r < rulers.Length; r++)
                {
                    var p = Pools.New();
                    int staged = StageReferenceSymbol(mode, rulers[r], ref p);
                    Assert.That(staged, Is.EqualTo(1),
                        $"W1-T8 precondition ({mode}, ruler {rulers[r]}): the reference label must stage.");
                    quadBits[r] = Bits(p.Quads, p.QuadCount);
                    boxBits[r]  = Bits(p.Boxes, p.BoxCount);
                }

                for (int r = 1; r < rulers.Length; r++)
                {
                    AssertBitIdentical(quadBits[0], quadBits[r],
                        $"W1-T8 ({mode}): PlacedQuads at MetresPerLogicalPixel={rulers[r]} differ from those " +
                        $"at {rulers[0]}");
                    AssertBitIdentical(boxBits[0], boxBits[r],
                        $"W1-T8 ({mode}): SymbolBoxes at MetresPerLogicalPixel={rulers[r]} differ from those " +
                        $"at {rulers[0]}");
                }
            }
        }

        /// <summary>
        /// <b>W1-T9 — the degradation guard.</b> Proves: a <see cref="AlignmentMode.Map"/> symbol whose
        /// per-frame ruler was never patched (<c>MetresPerLogicalPixel == 0</c>) stages BIT-identically to the
        /// same symbol under <see cref="AlignmentMode.Viewport"/> — i.e. it falls back to the pre-W1 screen
        /// walk rather than collapsing every glyph onto the anchor.
        ///
        /// <para>Without the <c>&gt; 0</c> conjunct, <c>arcScale</c> would be 0, every glyph's <c>arc</c>
        /// would equal <c>centerArc</c>, and the symbol would silently become a point — the ugliest possible
        /// failure for a path that has no channel to report. This tooth is what stops that guard from rotting
        /// into an unobserved branch.</para>
        ///
        /// <para>RED-verify: delete the <c>&amp;&amp; s.MetresPerLogicalPixel &gt; 0f</c> conjunct — the
        /// glyphs pile up on one point and the quads stop matching the viewport reference.</para>
        /// </summary>
        [Test]
        public void MapPitchedSymbol_WithNoRuler_DegradesToTheScreenWalk()
        {
            var mapPools = Pools.New();
            int stagedMap = StageReferenceSymbol(AlignmentMode.Map, 0f, ref mapPools);
            var viewportPools = Pools.New();
            int stagedViewport = StageReferenceSymbol(AlignmentMode.Viewport, 0f, ref viewportPools);

            Assert.That(stagedMap == 1 && stagedViewport == 1, Is.True,
                $"W1-T9 precondition: both references must stage — map {stagedMap}, viewport {stagedViewport}.");
            // The glyphs must not have collapsed onto one point: that is the failure this guard prevents, and
            // asserting it separately means a bit-comparison that somehow matched a degenerate symbol still
            // fails here.
            float spreadPx = math.length(
                mapPools.Quads[mapPools.QuadCount - 1].AnchorScreenPx - mapPools.Quads[0].AnchorScreenPx);
            Assert.That(spreadPx, Is.GreaterThan(1f),
                $"W1-T9: an unpatched map-pitched label must still spread its glyphs along the path — first " +
                $"and last anchors are {spreadPx:F6} px apart, i.e. the label collapsed to a point.");
            AssertBitIdentical(Bits(mapPools.Quads, mapPools.QuadCount),
                Bits(viewportPools.Quads, viewportPools.QuadCount),
                "W1-T9: a map-pitched label with MetresPerLogicalPixel=0 must stage exactly as a viewport one");
            AssertBitIdentical(Bits(mapPools.Boxes, mapPools.BoxCount),
                Bits(viewportPools.Boxes, viewportPools.BoxCount),
                "W1-T9: same, for the collision boxes");
        }

        /// <summary>The symbol T8/T9 compare across rulers: three glyphs on a plain two-vertex path, with the
        /// world span deliberately UNRELATED to the screen span (100 m vs 400 px) so that if the world walk
        /// ever did run here the difference would be enormous, not marginal.</summary>
        private static int StageReferenceSymbol(AlignmentMode pitch, float metresPerLogicalPixel, ref Pools p,
            SymbolViewTransform view = default)
        {
            var screenPath = new[] { new float2(50f, 250f), new float2(450f, 250f) };
            var worldPath  = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f,  Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 30f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            CurvedStageInput s = Input(pitch, metresPerLogicalPixel,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 8);
            return Stage(in s, screenPath, worldPath, glyphs, anchors, ref p, view: view);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W1-T10 — R3's named trap: the chord probe's half-width is an ARC quantity.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W1-T10 — REQUIRED, and the only observer of R3's named trap.</b> Proves: on a map-pitched BENT
        /// path, a glyph whose footprint straddles the interior vertex is rotated to the CHORD across that
        /// footprint — not to either raw segment angle.
        ///
        /// <para><b>Why it is mandatory.</b> The chord probe offsets by <c>halfWidthArc</c>, which must be
        /// scaled by <c>arcScale</c> and is therefore METRES under the world walk. Left on the px scale it
        /// spans a few units of a multi-thousand-metre advance, both probes land on the SAME segment, and the
        /// chord collapses to that segment's raw direction — silently undoing the vertex-straddling fix. On a
        /// STRAIGHT road the collapse is a no-op (the chord is collinear with the segment anyway), so the
        /// entire rendered fixture and every existing curved test are blind to it.</para>
        ///
        /// <para><b>The geometry, and why the expected value is not a re-implementation.</b> The two segments
        /// are the same 100 screen px but DIFFERENT world lengths — 1000 m and 2000 m, i.e. 10 and 20 metres
        /// per screen px, which is what a receding road looks like once projected. That asymmetry is
        /// deliberate and does two jobs at once:
        /// <list type="bullet">
        /// <item>it makes the world walk and the screen walk resolve genuinely DIFFERENT <c>(seg, t)</c> for
        /// the same anchor, so this tooth also fails if the world branch is killed outright (a tooth whose
        /// world path were merely a scaled copy of its screen path could not tell the two walks apart at
        /// all);</item>
        /// <item>it puts the glyph 200 m PAST the interior vertex, so the straddle is asymmetric — and that
        /// is what makes the px-scaled defect visible, because a small px half-width puts BOTH probes inside
        /// segment 1 and the chord then reads segment 1's raw 60° exactly.</item>
        /// </list>
        /// Every parameter is plain arithmetic: world cumulative <c>[0, 1000, 3000]</c>, anchor
        /// <c>(seg 1, t 0.1)</c> ⇒ world arc <c>1000 + 0.1·2000 = 1200 m</c>; half-width
        /// <c>50 baked px · arcScale(20 m per baked px) · 0.5 = 500 m</c>; so the probes sit at 700 m
        /// (segment 0, <c>t = 0.7</c>) and 1700 m (segment 1, <c>t = (1700−1000)/2000 = 0.35</c>), and the
        /// chord across <c>lerp(P₀,P₁,0.7) → lerp(P₁,P₂,0.35)</c> is the answer.</para>
        ///
        /// <para>RED-verify: injection I3 (leave <c>halfWidthArc</c> on the px scale) — confirm the injected
        /// build returns segment 1's raw angle, i.e. that the chord genuinely COLLAPSED rather than merely
        /// shifting. Also RED under I1 (kill the world branch), per the geometry note above.</para>
        /// </summary>
        [Test]
        public void MapPitched_GlyphStraddlingAVertex_RotatesToTheChord_NotARawSegmentAngle()
        {
            double bendRad = math.radians(60.0);
            var screenPath = new[]
            {
                new float2(-100f, 0f),
                new float2(0f, 0f),
                new float2(100f * (float)math.cos(bendRad), 100f * (float)math.sin(bendRad)),
            };
            // Both screen segments are 100 px, but the world ones are 1000 m and 2000 m — the second is
            // twice as compressed on screen, as a receding road's farther half is. See this tooth's doc.
            var worldPath = new[]
            {
                new double3(screenPath[0].x, 0.0, screenPath[0].y) * 10.0,
                new double3(screenPath[1].x, 0.0, screenPath[1].y) * 10.0,
                new double3(screenPath[2].x, 0.0, screenPath[2].y) * 20.0,
            };

            // arcScale = TextSizePx/OneEm × metresPerLogicalPixel = 2 × 10 = 20 m per baked px, so
            // halfWidthArc = cellWidth(50 baked px) × 20 × 0.5 = 500 m.
            var glyphs  = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(25f) } };
            var anchors = new[] { new LineAnchor(1, 0.1f) };   // world arc 1000 + 0.1×2000 = 1200 m
            CurvedStageInput s = Input(AlignmentMode.Map, metresPerLogicalPixel: 10f,
                textSizePx: 2f * TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 10);
            var p = Pools.New();

            int staged = Stage(in s, screenPath, worldPath, glyphs, anchors, ref p);
            Assert.That(staged, Is.EqualTo(1), "W1-T10 precondition: the straddling glyph must stage.");

            // The expected chord, derived here from the design constants (see this tooth's doc) — plain
            // lerps at the hand-derived parameters 0.7 and 0.35, no call into the arc math under test.
            float2 probeLeft  = math.lerp(screenPath[0], screenPath[1], 0.7f);
            float2 probeRight = math.lerp(screenPath[1], screenPath[2], 0.35f);
            float2 chord = probeRight - probeLeft;
            double expectedRad = math.atan2(chord.y, chord.x);
            double segment0Rad = 0.0;
            double segment1Rad = bendRad;
            double stagedRad = p.Quads[0].RotationRadians;

            Assert.That(math.abs(expectedRad - segment0Rad), Is.GreaterThan(math.radians(15.0)),
                $"W1-T10 precondition: the chord answer ({math.degrees(expectedRad):F3}°) must be well clear " +
                "of segment 0's raw angle, or the tooth cannot discriminate.");
            Assert.That(math.abs(expectedRad - segment1Rad), Is.GreaterThan(math.radians(15.0)),
                $"W1-T10 precondition: the chord answer ({math.degrees(expectedRad):F3}°) must be well clear " +
                "of segment 1's raw angle, or the tooth cannot discriminate.");
            Assert.That(stagedRad, Is.EqualTo(expectedRad).Within(math.radians(0.05)),
                $"W1-T10: the straddling glyph must rotate to the CHORD across its own world footprint — " +
                $"staged {math.degrees(stagedRad):F4}°, chord {math.degrees(expectedRad):F4}°, segment " +
                $"angles {math.degrees(segment0Rad):F1}° / {math.degrees(segment1Rad):F1}°. A staged value " +
                $"of exactly {math.degrees(segment1Rad):F1}° means the chord probe COLLAPSED — halfWidthArc " +
                "is on the px scale while the walk runs in metres (R3's named trap).");
        }

        // ── bit-identity comparison ─────────────────────────────────────────────────────────────────────
        //
        // Field-by-field EQUALITY is not what T8/T9 claim; they claim BIT-identity, and NaN/−0.0 make those
        // different statements. So each struct is flattened to its raw bit patterns by walking its value-type
        // fields reflectively — which also means a field added to PlacedQuad/SymbolBox later is compared
        // automatically instead of silently escaping the check.

        private static uint[] Bits<T>(T[] items, int count) where T : struct
        {
            var bits = new List<uint>();
            for (int i = 0; i < count; i++) FlattenBits(items[i], bits);
            return bits.ToArray();
        }

        private static void FlattenBits(object value, List<uint> bits)
        {
            switch (value)
            {
                case float f:  bits.Add(math.asuint(f)); return;
                case int i:    bits.Add(unchecked((uint)i)); return;
                case uint u:   bits.Add(u); return;
                case bool b:   bits.Add(b ? 1u : 0u); return;
                case byte y:   bits.Add(y); return;
                case long l:
                    bits.Add(unchecked((uint)l));
                    bits.Add(unchecked((uint)((ulong)l >> 32)));
                    return;
                case double d:
                {
                    ulong u64 = unchecked((ulong)System.BitConverter.DoubleToInt64Bits(d));
                    bits.Add(unchecked((uint)u64));
                    bits.Add(unchecked((uint)(u64 >> 32)));
                    return;
                }
            }

            System.Type type = value.GetType();
            if (type.IsEnum)
            {
                FlattenBits(System.Convert.ChangeType(value, System.Enum.GetUnderlyingType(type)), bits);
                return;
            }
            Assert.That(type.IsValueType, Is.True,
                $"the bit comparer walks value types only — {type} is a reference type, so the staged " +
                "structs are no longer blittable and this comparison would be meaningless.");
            System.Reflection.FieldInfo[] fields = type.GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.GreaterThan(0),
                $"the bit comparer found no fields on {type} — it would compare nothing and pass vacuously.");
            for (int f = 0; f < fields.Length; f++)
                FlattenBits(fields[f].GetValue(value), bits);
        }

        private static void AssertBitIdentical(uint[] expected, uint[] actual, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length),
                $"{what}: {actual.Length} words against {expected.Length} — a different number of staged " +
                "records, so nothing further is comparable.");
            Assert.That(expected.Length, Is.GreaterThan(0),
                $"{what}: nothing was staged, so this comparison would pass vacuously.");
            for (int i = 0; i < expected.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]),
                    $"{what}: first difference at 32-bit word {i} of {expected.Length} — 0x{actual[i]:X8} " +
                    $"against 0x{expected[i]:X8}. These outputs must be BIT-identical, not merely close.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T6 — the CORNER UNIT's producer: set exactly when the world walk is (R3 + R4.1)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T6, case 1.</b> Proves: a <see cref="AlignmentMode.Map"/> symbol with a live ruler emits
        /// <c>CandidateEmit.CornerMetresPerLogicalPixel</c> equal to EXACTLY that ruler — the same value the
        /// arc walk is spacing its anchors with, so the drawn size and the spacing cannot come from different
        /// constants.
        ///
        /// <para><b>This also discharges W1's followUp 1.</b> Before W2, <c>W1-T8</c> was the SOLE observer of
        /// the <c>PitchAlignment == Map</c> conjunct in a 2109-test suite — injection I7 (drop the conjunct)
        /// reddened T8 and nothing else. After W2 the same <c>worldArc</c> bool is observed through a SECOND,
        /// independent output by these three cases and by W2-T7, so weakening W1-T8 no longer leaves the
        /// predicate unobserved. W1-T8's doc carries the reciprocal cross-reference.</para>
        ///
        /// <para><b>Ruler magnitudes are bounded by the reference symbol's own road, deliberately.</b> Under the
        /// world walk the reference symbol's arc span is <c>60 · ruler</c> metres against a 100 m world path, so
        /// a ruler above ≈ 1.6 makes <c>StageCurved</c> return 0 at its <c>symbolSpanArc &gt; total</c> spill
        /// gate and there is no emit to read. W1-T8 and W1-T9 never hit that because they run this symbol
        /// either non-map-pitched or with a zero ruler. The values below straddle 1 so the assertion is that
        /// the emit carries the ruler EXACTLY, not merely that it is non-zero.</para>
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsTheRuler_WhenMapPitchedAndRulerIsLive()
        {
            foreach (float ruler in new[] { 0.5f, 1f, 1.25f })
            {
                var p = Pools.New();
                int staged = StageReferenceSymbol(AlignmentMode.Map, ruler, ref p);
                Assert.That(staged, Is.EqualTo(1),
                    $"W2-T6 precondition (ruler {ruler}): the reference label must stage — a 0 means its " +
                    $"world arc span ({60f * ruler:F1} m) outgrew its 100 m road at the spill gate.");
                Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(ruler),
                    $"W2-T6: a map-pitched label with MetresPerLogicalPixel = {ruler} must carry exactly " +
                    $"{ruler} as its corner unit, got {p.Emit[0].CornerMetresPerLogicalPixel}. Any other " +
                    "value means the corner scale and the arc scale were derived separately — the state this " +
                    "stage exists to make inexpressible.");
            }
        }

        /// <summary>
        /// <b>W2-T6, case 2.</b> Proves: a NON-map-pitched symbol emits <c>0f</c> as its corner unit whatever
        /// the ruler reads — so its <c>Offset</c> stays LOGICAL PIXELS and every pre-W2 path is untouched.
        /// The zero value is the struct's default, which is why no point emit and no hand-built fixture emit
        /// had to be edited by this stage.
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsZero_WhenNotMapPitched()
        {
            foreach (AlignmentMode mode in new[] { AlignmentMode.Viewport, AlignmentMode.Auto })
                foreach (float ruler in new[] { 0f, 1f, 1e6f })
                {
                    var p = Pools.New();
                    int staged = StageReferenceSymbol(mode, ruler, ref p);
                    Assert.That(staged, Is.EqualTo(1),
                        $"W2-T6 precondition ({mode}, ruler {ruler}): the reference label must stage.");
                    Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(0f),
                        $"W2-T6 ({mode}, ruler {ruler}): a non-map-pitched label must carry 0 as its corner " +
                        $"unit — i.e. logical pixels, the pre-W2 behaviour — got " +
                        $"{p.Emit[0].CornerMetresPerLogicalPixel}. A non-zero here would make every viewport " +
                        "label's corners metres and reinterpret the whole pre-W2 render.");
                }
        }

        /// <summary>
        /// <b>W2-T6, case 3 — the two halves must degrade TOGETHER.</b> Proves: a
        /// <see cref="AlignmentMode.Map"/> symbol whose per-frame ruler was never patched
        /// (<c>MetresPerLogicalPixel == 0</c>) emits <c>0f</c> as its corner unit too.
        ///
        /// <para>W1-T9 pins that such a symbol falls back to the pre-W1 SCREEN arc walk rather than collapsing
        /// to a point. This is the corner-side half of the same guard: the arc ruler and the corner unit come
        /// from ONE predicate, so an unpatched symbol degrades wholly to the pre-W2 behaviour rather than into
        /// a half-converted state where the spacing is screen px and the corners are metres. A test that shows
        /// them degrading together is the point — the failure this prevents is not a crash, it is a silently
        /// mixed pair of rulers, which is the exact shape of the bug this epic kept re-landing.</para>
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsZero_WhenMapPitchedButTheRulerIsMissing()
        {
            var p = Pools.New();
            int staged = StageReferenceSymbol(AlignmentMode.Map, 0f, ref p);
            Assert.That(staged, Is.EqualTo(1), "W2-T6 precondition: the reference label must stage.");
            Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(0f),
                "W2-T6: a map-pitched label with no ruler must degrade its CORNER unit to logical px exactly " +
                "as W1-T9 shows it degrades its ARC ruler to the screen walk — got " +
                $"{p.Emit[0].CornerMetresPerLogicalPixel}. The two must degrade together; a half-converted " +
                "label is worse than either whole behaviour.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T3b — equation (5) past the fixture's cull ceiling, with NO camera
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T3b — the CPU half of equation (6), carried to 50× and beyond.</b> Proves: for a map-pitched
        /// curved symbol, <c>worldAdvance / cellWidth_world</c> equals the purely typographic
        /// <c>ΔArcCenter / cellWidthBaked</c> at every magnitude regime, computed through production
        /// <see cref="SymbolStagingMath.StageCurved"/> and production <see cref="BillboardMath.BuildWorldQuad"/>
        /// with the emit's OWN corner scale.
        ///
        /// <para><b>Why it exists, and exactly what it does and does not add.</b> W2-T3 stops at 8× because
        /// the renderer's B-3 pre-projection distance cull removes the far symbols — the horizon measurement
        /// proved that by observing that <c>PointFar</c>, which has no road and no arc walk at all, disappears
        /// at the same ratio. Equation (5) needs no camera, so this tooth carries the identity past that wall.
        /// <b>It pins the CPU half only and does NOT exercise the shader.</b> The three reaches, named so
        /// nothing is over-read (followUp F-W2-5): rendered ink to ≈ 2.4× (W2-T1/T2), real-mesh world metres
        /// to 8× (W2-T3, W2-T10), CPU identity to 50× (here). The gap above 8× in the RENDERED arms is a
        /// recorded limitation, not an implied claim.</para>
        ///
        /// <para><b>What "depth ratio" means with no camera.</b> Nothing — and that is the structural point.
        /// The world arc walk has NO depth input: a glyph's arc is
        /// <c>centerArc + (ArcCenter − centre)·arcScale</c> and <c>arcScale</c> is a per-FRAME constant, so
        /// spacing CANNOT depend on distance from the camera; it can only fail NUMERICALLY. What the sweep
        /// below varies is therefore the MAGNITUDE regime — the symbol's distance from its tile origin, along
        /// the road axis, which is what a deep pose actually produces — which is the one thing that can
        /// degrade. That is the horizon measurement's §5 method, reused. Displacing ACROSS the road axis would
        /// measure nothing: the large offset lands in a component identical for every glyph and cancels
        /// exactly in the gap.</para>
        /// </summary>
        [Test]
        public void WorldCellToAdvanceRatio_HoldsAtEveryMagnitude(
            [Values(2.0, 5.0, 10.0, 20.0, 50.0)] double magnitude)
        {
            const float advanceBaked   = 30f;
            const float halfWidthBaked = 12f;   // ⇒ cellWidthBaked = 24
            const float ruler          = 305.748f; // metres per logical px, the fixture's own shipped value
            double cellWidthBaked = 2.0 * halfWidthBaked;
            double expected = advanceBaked / cellWidthBaked;

            // The road runs along +x and the whole thing is displaced ALONG that axis (see the doc).
            double baseOffset = 1.0e5 * magnitude;
            double roadLen    = 1.0e5 * magnitude;
            var screenPath = new[] { new float2(50f, 250f), new float2(450f, 250f) };
            var worldPath  = new[]
            {
                new double3(baseOffset, 0, 0),
                new double3(baseOffset + roadLen, 0, 0),
            };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f,                  Cell = Cell(halfWidthBaked) },
                new CurvedGlyph { ArcCenter = advanceBaked,        Cell = Cell(halfWidthBaked) },
                new CurvedGlyph { ArcCenter = 2f * advanceBaked,   Cell = Cell(halfWidthBaked) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            CurvedStageInput s = Input(AlignmentMode.Map, ruler,
                textSizePx: TextQuadLayout.OneEm, maxAngleDeg: 180f, featureIndex: 11);

            var p = Pools.New();
            int staged = Stage(in s, screenPath, worldPath, glyphs, anchors, ref p);
            Assert.That(staged, Is.EqualTo(1),
                $"W2-T3b precondition (magnitude {magnitude}): the label must stage; a 0 means the road is " +
                "too short for its world span at this magnitude, which would make the reading vacuous.");

            float cornerScale = p.Emit[0].CornerMetresPerLogicalPixel;
            Assert.That(cornerScale, Is.GreaterThan(0f),
                "W2-T3b precondition: the emit must carry a metre corner unit, or there is no world cell " +
                "width to compare against.");

            // Production BuildWorldQuad, with the emit's OWN scale — exactly the composition
            // WorldSymbolRenderer.Emit performs. The operands are hoisted into locals because CurvedGlyph.Cell
            // is an init-only PROPERTY (the data-carrier convention) and a property value has no address to
            // bind an `in` parameter to.
            SymbolQuad cell0    = glyphs[0].Cell;
            var        white    = new float3(1f, 1f, 1f);
            float2     noTrans  = p.Emit[0].TranslateDeltaPx;
            float3     tangent0 = p.Quads[0].Tangent;
            float3     up0      = p.Quads[0].SurfaceUp;
            float3     anchor0  = p.Quads[0].AnchorLocal;
            BillboardMath.BuildWorldQuad(in cell0, in anchor0,
                p.Quads[0].TextSizePx * cornerScale, in white,
                rotationRadians: 0f, in noTrans, in tangent0,
                in up0, alignFlags: 6f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out _, out _);
            double cellWidthWorld = tr.Offset.x - tl.Offset.x;

            for (int g = 0; g + 1 < glyphs.Length; g++)
            {
                double advanceWorld = math.length(p.Quads[g + 1].AnchorLocal - p.Quads[g].AnchorLocal);
                double ratio = advanceWorld / cellWidthWorld;
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "W2-T3b  magnitude={0:F0}  gap {1}  advanceWorld={2:F3} m  cellWidthWorld={3:F3} m  " +
                    "ratio={4:F8}  expected={5:F8}",
                    magnitude, g, advanceWorld, cellWidthWorld, ratio, expected));

                Assert.That(ratio, Is.EqualTo(expected).Within(0.01).Percent,
                    $"W2-T3b (magnitude {magnitude}, gap {g}): worldAdvance / cellWidth_world must be the " +
                    $"baked {advanceBaked}/{cellWidthBaked} = {expected:F8} at EVERY magnitude, measured " +
                    $"{ratio:F8}. Both sides are produced by the SAME arcScale, so it cancels identically; a " +
                    "drift here at large magnitude is a float-precision floor, not a model failure — the " +
                    "horizon measurement puts that floor at roughly 50 Earth circumferences of road.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3 — the projected-world-corner collision box. Shared apparatus first, then W3-T4…T9.
        //
        // NO CAMERA OBJECT: the view transform is HAND-BUILT here, so every expected pixel number below is
        // plain arithmetic over two constants (the viewport and the field of view) rather than a reading
        // taken off a live scene.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The W3 teeth's viewport, logical px. Square, so the projection's aspect ratio is 1.</summary>
        private const double ViewportPx = 512.0;

        /// <summary>Vertical field of view, radians (60°).</summary>
        private const float FovRadians = 1.0471975511965976f;

        /// <summary>The camera-facing surface normal used by most W3 teeth: with a road along <c>+X̂</c> this
        /// makes <c>x̂ = +X̂</c> and <c>ŷ = cross(x̂, up) = +Ŷ</c>, i.e. the glyph quad lies in the plane
        /// PERPENDICULAR to the view axis, so a corner's projection is a plain uniform scale in BOTH screen
        /// axes and every expectation below is arithmetic instead of a solve.</summary>
        private static readonly float3 CameraFacingUp = new float3(0f, 0f, -1f);

        /// <summary>The ordinary GROUND normal: with a road along <c>+X̂</c> this makes
        /// <c>ŷ = cross(x̂, up) = +Ẑ</c>, i.e. the quad's own y axis runs ALONG the view axis, which is how
        /// W3-T7 pushes a corner behind the camera without moving the anchor.</summary>
        private static readonly float3 GroundUp = new float3(0f, 1f, 0f);

        /// <summary>Screen px per metre of world offset, per metre of view depth:
        /// <c>½·viewport·cot(fov/2)</c>. A projected offset is <c>this · offsetMetres / depthMetres</c>.
        /// Derived here from the two constants above; never read back out of production.</summary>
        private static readonly double PxPerMetreAtUnitDepth = 0.5 * ViewportPx / math.tan(FovRadians * 0.5);

        /// <summary>
        /// The world→view matrix for a camera at <paramref name="eye"/> looking at <paramref name="target"/>:
        /// right-handed, camera looking down <b>−Z</b> — the convention <c>float4x4.PerspectiveFov</c>'s
        /// <c>w = −z_view</c> bottom row expects, and the one Unity's own <c>worldToCameraMatrix</c> uses (it
        /// negates Z on the way out of Unity's left-handed world).
        ///
        /// <para><b>Written out rather than composed as <c>inverse(float4x4.LookAt(eye, target, up))</c>.</b>
        /// <c>float4x4.LookAt</c> returns a camera→WORLD transform whose <c>+Z</c> IS the forward direction, so
        /// its inverse places the target at view <b>+Z</b> — the opposite sense. Composed that way every corner
        /// comes back with <c>clip.w ≤ 0</c>, every projection fails, BOTH arms of W3-T4 silently take the
        /// screen-box fallback, and the tooth fails in a shape that reads like a logic bug rather than like a
        /// convention mismatch. <b>W3-T4-pre is the tooth that proves this matrix actually projects</b> — a
        /// ≈2 px projected half-height against a 15 px screen one is only reachable if it does.</para>
        /// </summary>
        private static float4x4 ViewMatrix(float3 eye, float3 target, float3 up)
        {
            float3 f = math.normalize(target - eye);
            float3 r = math.normalize(math.cross(up, f));
            float3 u = math.cross(f, r);
            return new float4x4(
                 r.x,  r.y,  r.z, -math.dot(r, eye),
                 u.x,  u.y,  u.z, -math.dot(u, eye),
                -f.x, -f.y, -f.z,  math.dot(f, eye),
                 0f,   0f,   0f,   1f);
        }

        /// <summary>A usable <see cref="SymbolViewTransform"/> for a camera at the render-space ORIGIN looking
        /// along <c>+Ẑ</c>, with an identity rebase and a zero scene origin. A world point <c>(x, y, z)</c> with
        /// <c>z &gt; 0</c> then projects exactly where <see cref="ProjectPx"/> says it does.</summary>
        private static SymbolViewTransform OriginView(double viewportPx = ViewportPx)
            => new SymbolViewTransform
            {
                SceneOriginRender = double3.zero,
                Rebase            = float3x3.identity,
                ViewProj          = math.mul(
                    float4x4.PerspectiveFov(FovRadians, 1f, 1f, 1e7f),
                    ViewMatrix(float3.zero, new float3(0f, 0f, 1f), new float3(0f, 1f, 0f))),
                ViewportLogicalPx = new double2(viewportPx, viewportPx),
            };

        /// <summary>Where <see cref="OriginView"/> puts a render-space point, in logical screen px — the
        /// FIXTURE's own projection, calling no production code. It is what makes each synthetic screen
        /// polyline below the TRUE projection of its world polyline, so the pre-W3 screen box these teeth
        /// compare against is the one the shipped code would really have built.</summary>
        private static float2 ProjectPx(double3 world)
            => new float2(
                (float)(0.5 * ViewportPx + PxPerMetreAtUnitDepth * world.x / world.z),
                (float)(0.5 * ViewportPx + PxPerMetreAtUnitDepth * world.y / world.z));

        /// <summary>
        /// W3's staging fixture: ONE glyph, centred on a straight road that runs along <c>+X̂</c> at view depth
        /// <paramref name="depthM"/> and lateral offset <paramref name="lateralM"/>. The screen polyline is the
        /// TRUE projection of the world polyline (<see cref="ProjectPx"/>), so both the pre-W3 screen box and
        /// the W3 projected box are the boxes the shipped code builds for a real pose of this shape.
        /// </summary>
        private static int StageW3Glyph(ref Pools p, in SymbolViewTransform view, in float3 surfaceUp,
            in SymbolQuad cell, float cellSkirt, float textSizePx, float mpp,
            double depthM, double lateralM, double roadHalfM,
            float iconRotateRadians, float sortKey, int featureIndex, int ordinal,
            AlignmentMode pitch = AlignmentMode.Map)
        {
            var worldPath = new[]
            {
                new double3(-roadHalfM, lateralM, depthM),
                new double3( roadHalfM, lateralM, depthM),
            };
            var screenPath = new[] { ProjectPx(worldPath[0]), ProjectPx(worldPath[1]) };
            var ups = new[] { surfaceUp, surfaceUp };
            var glyphs = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = cell, CellSkirt = cellSkirt } };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            CurvedStageInput s = Input(pitch, mpp, textSizePx, maxAngleDeg: 180f, featureIndex);
            s.SortKey = sortKey;
            s.IconRotateRadians = iconRotateRadians;
            return Stage(in s, screenPath, worldPath, glyphs, anchors, ref p,
                view: view, worldUpPathOverride: ups, ordinal: ordinal);
        }

        // ── the W3-T4 suppression pair's sizing, all derived, none typed ────────────────────────────────

        /// <summary>`text-size` for the suppression pair: 2.5 em, so the 6-baked-px cell half-height becomes a
        /// 15 logical-px PRE-W3 box half-height.</summary>
        private const float SuppressionTextSizePx = 2.5f * TextQuadLayout.OneEm;

        private const float  SuppressionCellHalfHeightBaked = 6f;   // what Cell(...) bakes
        private const float  SuppressionCellHalfWidthBaked  = 10f;
        private const float  SuppressionMpp = 1f;                   // metres per logical px
        private const double SuppressionTargetHalfHeightPx = 2.0;   // the W3 box's designed half-height
        private const double SuppressionTargetSeparationPx = 8.0;   // the roads' designed projected gap

        /// <summary>The pre-W3 box's half-height, in logical px: the cell half-height scaled by
        /// <c>TextSizePx / OneEm</c>, with no depth term at all — which is the defect W3 removes.</summary>
        private const double SuppressionScreenHalfHeightPx =
            SuppressionCellHalfHeightBaked * SuppressionTextSizePx / TextQuadLayout.OneEm;

        /// <summary>The same half-height in WORLD METRES — the renderer's own association,
        /// <c>cellHalfBaked · (TextSizePx · mpp) / OneEm</c>.</summary>
        private const double SuppressionHalfHeightWorldM =
            SuppressionCellHalfHeightBaked * (SuppressionTextSizePx * SuppressionMpp) / TextQuadLayout.OneEm;

        /// <summary>The view depth at which that metre half-height foreshortens to the designed 2 px.</summary>
        private static readonly double SuppressionDepthM =
            PxPerMetreAtUnitDepth * SuppressionHalfHeightWorldM / SuppressionTargetHalfHeightPx;

        /// <summary>The world lateral offset whose projection at that depth is the designed 8 px.</summary>
        private static readonly double SuppressionLateralM =
            SuppressionTargetSeparationPx * SuppressionDepthM / PxPerMetreAtUnitDepth;

        /// <summary>Stages the W3-T4 pair — two map-pitched one-glyph symbols on PARALLEL roads at the same
        /// depth, both <c>AllowOverlap = false</c>, with distinct sort keys and feature indices (a total
        /// placement order) — into ONE shared box/candidate pool at ordinals 0 and 1.</summary>
        private static Pools StageSuppressionPair(SymbolViewTransform view)
        {
            Pools p = Pools.New();
            int a = StageW3Glyph(ref p, in view, CameraFacingUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 40, ordinal: 0);
            int b = StageW3Glyph(ref p, in view, CameraFacingUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, SuppressionLateralM, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 1f, featureIndex: 41, ordinal: 1);
            Assert.That(a == 1 && b == 1, Is.True,
                $"W3-T4 precondition: both labels of the pair must stage — staged {a} and {b}. A zero means " +
                "the road is too short for the label's world span or the anchor spilled.");
            Assert.That(p.BoxCount, Is.EqualTo(2),
                $"W3-T4 precondition: the shared pool must hold exactly one box per label, got {p.BoxCount}.");
            return p;
        }

        private static double HalfHeightPx(in SymbolBox b) => 0.5 * (b.Max.y - b.Min.y);
        private static double HalfWidthPx(in SymbolBox b)  => 0.5 * (b.Max.x - b.Min.x);
        private static double CentreY(in SymbolBox b)      => 0.5 * (b.Max.y + b.Min.y);

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T4-pre — the suppression fixture's three sizing rows, asserted directly
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T4-pre — the preconditions of W3-T4, as their own test.</b> A re-tuned constant must red HERE,
        /// with its numbers, rather than quietly making the headline tooth vacuous (a pair whose boxes no
        /// longer straddle the road separation would return the same survivor count on both arms and pass).
        ///
        /// <para>It also proves the <b>hand-built view transform actually projects</b>. If
        /// <c>ViewMatrix</c>/<c>PerspectiveFov</c> disagreed about which way the camera looks, every corner
        /// would come back <c>clip.w ≤ 0</c>, <c>TryBuildProjectedWorldGlyph</c> would return false, and the
        /// "projected" arm would read the pre-W3 15 px rather than 2 px. See <see cref="ViewMatrix"/>.</para>
        /// </summary>
        [Test]
        public void MapPitched_SuppressionFixture_IsSizedSoTheTwoBoxesStraddleTheRoadGap()
        {
            Pools screen = StageSuppressionPair(default);            // D5 ⇒ the pre-W3 screen box
            Pools projected = StageSuppressionPair(OriginView());     // the W3 box

            double screenHalfHeight = HalfHeightPx(screen.Boxes[0]);
            double projectedHalfHeight = HalfHeightPx(projected.Boxes[0]);
            double screenSeparation = CentreY(screen.Boxes[1]) - CentreY(screen.Boxes[0]);
            double projectedSeparation = CentreY(projected.Boxes[1]) - CentreY(projected.Boxes[0]);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T4-pre  depth={0:F2} m  lateral={1:F2} m  screen half-height={2:F4} px  " +
                "projected half-height={3:F4} px  separation: screen={4:F4} px projected={5:F4} px",
                SuppressionDepthM, SuppressionLateralM, screenHalfHeight, projectedHalfHeight,
                screenSeparation, projectedSeparation));

            Assert.That(screenHalfHeight, Is.EqualTo(SuppressionScreenHalfHeightPx).Within(1e-3),
                $"W3-T4-pre row 1: the PRE-W3 box's half-height must be the depth-free " +
                $"cellHalfBaked·TextSizePx/OneEm = {SuppressionScreenHalfHeightPx:F4} px, read " +
                $"{screenHalfHeight:F4}.");
            Assert.That(projectedHalfHeight, Is.EqualTo(SuppressionTargetHalfHeightPx).Within(0.05),
                $"W3-T4-pre row 2: the W3 box's half-height must foreshorten to the designed " +
                $"{SuppressionTargetHalfHeightPx:F2} px at {SuppressionDepthM:F1} m, read " +
                $"{projectedHalfHeight:F4}. A reading of {SuppressionScreenHalfHeightPx:F1} px means every " +
                "corner failed to project and the box fell back — check ViewMatrix's Z sense.");
            Assert.That(projectedSeparation, Is.EqualTo(SuppressionTargetSeparationPx).Within(0.05),
                $"W3-T4-pre row 3: the roads' projected lateral separation must be the designed " +
                $"{SuppressionTargetSeparationPx:F2} px, read {projectedSeparation:F4}.");
            Assert.That(screenSeparation, Is.EqualTo(SuppressionTargetSeparationPx).Within(0.05),
                $"W3-T4-pre row 3 (screen arm): the pre-W3 boxes ride the SAME projected anchors, so their " +
                $"separation must also be {SuppressionTargetSeparationPx:F2} px, read {screenSeparation:F4} " +
                "— otherwise the two arms differ in more than the box construction.");

            // The discriminator, stated as arithmetic: the separation must sit STRICTLY BETWEEN the two box
            // heights, or the two arms cannot disagree about placement.
            Assert.That(projectedSeparation, Is.GreaterThan(2.0 * projectedHalfHeight),
                $"W3-T4-pre: the projected boxes must CLEAR each other — separation {projectedSeparation:F4} " +
                $"px against a full height of {2.0 * projectedHalfHeight:F4} px.");
            Assert.That(screenSeparation, Is.LessThan(2.0 * screenHalfHeight),
                $"W3-T4-pre: the screen boxes must OVERLAP — separation {screenSeparation:F4} px against a " +
                $"full height of {2.0 * screenHalfHeight:F4} px.");
            Assert.That(HalfWidthPx(projected.Boxes[0]), Is.GreaterThan(0.5),
                "W3-T4-pre: the projected box must have a real width too — a collapsed box would clear its " +
                "neighbour for the wrong reason.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T4 — R2: SUPPRESSION. The old box drops a symbol the new one places.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T4 — THE STAGE'S REASON TO EXIST.</b> Two map-pitched symbols on parallel roads, sized by
        /// W3-T4-pre so their PROJECTED boxes clear each other by 8 px while their pre-W3 SCREEN boxes (15 px
        /// half-height, no depth term) overlap. With a usable view transform the collision pass places BOTH;
        /// with <c>default(SymbolViewTransform)</c> — which D5 makes a reachable state of the SHIPPED code, not
        /// something only an edit can produce — it places ONE.
        ///
        /// <para><b>The tooth contains its own before/after, so it needs no injection.</b> Both arms run the
        /// same shipped binary over the same geometry; the only difference is whether a camera was supplied.
        /// That is exactly the over-reservation W3 removes: the screen box reserved ~150× the ink's area at
        /// depth, and over-reservation can only ever SUPPRESS a symbol, never misplace one.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_PlacesBothSymbols_WhereTheScreenBoxSuppressesOne()
        {
            int projectedSurvivors = Survivors(StageSuppressionPair(OriginView()), "projected");
            int screenSurvivors    = Survivors(StageSuppressionPair(default),      "screen (pre-W3)");

            Assert.That(projectedSurvivors, Is.EqualTo(2),
                $"W3-T4: with the four world corners projected, the two labels' boxes clear each other and " +
                $"BOTH must place — {projectedSurvivors} survived. This is the label the pre-W3 box was " +
                "silently eating at tilt.");
            Assert.That(screenSurvivors, Is.EqualTo(1),
                $"W3-T4 (control): the pre-W3 SCREEN box has no depth term, so at this depth it over-reserves " +
                $"and one of the two must be suppressed — {screenSurvivors} survived. If this reads 2 the " +
                "fixture has stopped discriminating; W3-T4-pre says which row moved.");
        }

        /// <summary>Runs the shared collision pass over a staged pair and reports the survivor set, printing
        /// both boxes so a fixture that stops discriminating fails with its numbers.</summary>
        private static int Survivors(Pools p, string armName)
        {
            var survivor = new bool[2];
            int n = NativeCollisionRunner.RunCollision(p.Candidates, 2, p.Boxes, p.BoxCount, survivor);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T4  {0,-16} boxA=[{1:F3}, {2:F3}]×[{3:F3}, {4:F3}]  boxB=[{5:F3}, {6:F3}]×[{7:F3}, " +
                "{8:F3}]  survivors={9} ({10}, {11})",
                armName, p.Boxes[0].Min.x, p.Boxes[0].Max.x, p.Boxes[0].Min.y, p.Boxes[0].Max.y,
                p.Boxes[1].Min.x, p.Boxes[1].Max.x, p.Boxes[1].Min.y, p.Boxes[1].Max.y,
                n, survivor[0], survivor[1]));
            return n;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T5 — R3: the non-map path cannot observe the view transform, at any magnitude
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T5 — the stage invariant's structural half.</b> Proves: a symbol whose resolved pitch alignment
        /// is not <see cref="AlignmentMode.Map"/> cannot observe the new per-frame view transform AT ALL — its
        /// staged <see cref="SymbolBox"/>es and <see cref="PlacedQuad"/>s are BIT-identical with no transform and
        /// with three genuinely different real ones (different eye, target, field of view, aspect, scene origin
        /// and viewport). Run for <see cref="AlignmentMode.Viewport"/> and for <see cref="AlignmentMode.Auto"/>,
        /// the enum's zero value that every hand-built pre-W1 fixture carries.
        ///
        /// <para>The empirical half of the same claim is the full gate at CP-1 (2154/2154 unmoved); the
        /// sensitivity half is injection I6, which drops the <c>cornerMetresPerLogicalPixel &gt; 0f</c>
        /// conjunct.</para>
        ///
        /// <para><b>The fixture is the W3 camera-facing glyph, NOT <c>StageReferenceSymbol</c>, and that choice
        /// is load-bearing.</b> The reference symbol carries an all-zero surface normal and sits at the camera
        /// origin, so its projection would fail at the ground-frame guard and at <c>clip.w</c> — this tooth
        /// would then be bit-identical across transforms for a reason that has nothing to do with the pitch
        /// predicate, and injection I6 would leave it GREEN. It did, on the first version of this tooth; the
        /// fixture below is what fixes that. Here the symbol has a real normal, a live ruler and a depth at
        /// which every corner projects, so the ONLY thing keeping it off the projected branch is its pitch
        /// alignment.</para>
        /// </summary>
        [Test]
        public void NonMapPitchedSymbol_CannotObserveTheViewTransform()
        {
            SymbolViewTransform[] views = ViewSweep();
            foreach (AlignmentMode mode in new[] { AlignmentMode.Viewport, AlignmentMode.Auto })
            {
                var quadBits = new uint[views.Length][];
                var boxBits  = new uint[views.Length][];
                for (int v = 0; v < views.Length; v++)
                {
                    var p = Pools.New();
                    int staged = StageW3Glyph(ref p, views[v], CameraFacingUp,
                        Cell(SuppressionCellHalfWidthBaked), cellSkirt: 0f, SuppressionTextSizePx,
                        SuppressionMpp, SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                        iconRotateRadians: 0f, sortKey: 0f, featureIndex: 46, ordinal: 0, pitch: mode);
                    Assert.That(staged, Is.EqualTo(1),
                        $"W3-T5 precondition ({mode}, view {v}): the reference label must stage.");
                    quadBits[v] = Bits(p.Quads, p.QuadCount);
                    boxBits[v]  = Bits(p.Boxes, p.BoxCount);
                }

                for (int v = 1; v < views.Length; v++)
                {
                    AssertBitIdentical(quadBits[0], quadBits[v],
                        $"W3-T5 ({mode}): PlacedQuads under view transform {v} differ from those with none");
                    AssertBitIdentical(boxBits[0], boxBits[v],
                        $"W3-T5 ({mode}): SymbolBoxes under view transform {v} differ from those with none");
                }
            }
        }

        /// <summary>The default transform followed by three genuinely different real ones — different eye,
        /// target, field of view, aspect, scene origin and viewport, so a leak through any one of the four
        /// carried values shows up.</summary>
        private static SymbolViewTransform[] ViewSweep() => new[]
        {
            default(SymbolViewTransform),
            OriginView(),
            new SymbolViewTransform
            {
                SceneOriginRender = new double3(1.0e5, 2.0e5, -3.0e5),
                Rebase            = float3x3.identity,
                ViewProj          = math.mul(
                    float4x4.PerspectiveFov(0.6f, 1.7778f, 1f, 1.0e7f),
                    ViewMatrix(new float3(50f, 400f, -900f), float3.zero, new float3(0f, 1f, 0f))),
                ViewportLogicalPx = new double2(1920.0, 1080.0),
            },
            OriginView(2048.0),
        };

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T6 — D10 / F-W3-1: a degenerate ground frame falls back to the screen box, bit-identically
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T6 — the observing tooth for followUp F-W3-1.</b> Proves: a map-pitched symbol whose
        /// per-vertex <c>Up</c> is <see cref="float3.zero"/> — the exact state ~10 older fixtures and
        /// <c>SymbolTileBlockBaker</c> (null <c>PathUpRender</c>) still write, and which that site's own
        /// comment calls "a bug signal, not a supported state" — stages its box BIT-identically to the same
        /// symbol with no view transform at all. It takes the pre-W3 SCREEN box.
        ///
        /// <para><b>This is a KNOWING divergence from the shader, recorded, not fixed.</b>
        /// <c>SymbolWorldMapPitchClip</c>'s degenerate fallback is a camera-facing METRE frame, so the ink and
        /// the box disagree in this state. Reproducing that frame on the CPU needs the view basis in render
        /// space and a fresh handedness derivation, to serve a case unreachable in production. This tooth is
        /// what stops the divergence becoming an unobserved branch — the same role W1-T9 plays for the ruler
        /// guard.</para>
        ///
        /// <para>The non-vacuity clause runs FIRST: with a real <c>Up</c> the very same fixture and the very
        /// same transform must produce a DIFFERENT box, or "bit-identical to the screen box" would be a claim
        /// about a transform that was never usable.</para>
        /// </summary>
        [Test]
        public void MapPitched_WithADegenerateGroundFrame_TakesTheScreenBox_BitIdentically()
        {
            SymbolViewTransform view = OriginView();

            var withUp = Pools.New();
            StageDegenerateProbe(ref withUp, view, CameraFacingUp);
            var noView = Pools.New();
            StageDegenerateProbe(ref noView, default, CameraFacingUp);
            var zeroUp = Pools.New();
            StageDegenerateProbe(ref zeroUp, view, float3.zero);

            double projectedHalfHeight = HalfHeightPx(withUp.Boxes[0]);
            double screenHalfHeight    = HalfHeightPx(noView.Boxes[0]);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T6  projected half-height={0:F4} px  screen half-height={1:F4} px  degenerate-Up " +
                "half-height={2:F4} px", projectedHalfHeight, screenHalfHeight, HalfHeightPx(zeroUp.Boxes[0])));

            Assert.That(math.abs(projectedHalfHeight - screenHalfHeight), Is.GreaterThan(1.0),
                $"W3-T6 precondition (non-vacuity): with a REAL surface normal this transform must produce a " +
                $"genuinely different box — projected {projectedHalfHeight:F4} px against screen " +
                $"{screenHalfHeight:F4} px. Without this the bit-identity below would only say the transform " +
                "was never usable.");
            AssertBitIdentical(Bits(noView.Boxes, noView.BoxCount), Bits(zeroUp.Boxes, zeroUp.BoxCount),
                "W3-T6: a map-pitched label with a ZERO surface normal must fall back to the pre-W3 screen box");
        }

        /// <summary>W3-T6/T7's probe symbol: the camera-facing fixture at the suppression pair's own depth, so
        /// its projected box is the well-understood ≈2 px one and the fallback is unmistakably different.</summary>
        private static void StageDegenerateProbe(ref Pools p, SymbolViewTransform view, float3 surfaceUp)
        {
            int staged = StageW3Glyph(ref p, in view, in surfaceUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 42, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T6/T7 precondition: the probe label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T7 — a corner that fails to project falls back, and does not emit a half-built box
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T7 — the per-corner guard.</b> Proves: when ONE of the four world corners lands behind the
        /// camera, the whole box falls back to the pre-W3 screen box BIT-identically — never a box built from
        /// the three corners that did project.
        ///
        /// <para><b>The geometry.</b> The surface normal here is the ordinary GROUND one, so
        /// <c>ŷ = cross(x̂, up)</c> runs ALONG the view axis and a corner offset moves the corner in DEPTH
        /// without moving the anchor. At <c>text-size</c> 1 em with a ruler of 1000 m per logical px the cell's
        /// 6-baked-px half-height is 6000 m, so on a road 100 m from the camera the up-screen corner sits at
        /// <c>z = −5900 m</c> and <c>TryProjectPoint</c> rejects it on <c>clip.w ≤ 0</c>.</para>
        ///
        /// <para><b>Reachability, reported not assumed (followUp F-W2-8).</b> The construction needs a ruler of
        /// 1000 metres per logical pixel at a view depth of 100 metres — a glyph 120× taller than its distance
        /// to the camera. No pose this renderer produces is anywhere near it: the shipped fixture's ruler is
        /// ~306 m/px at a look-at depth of tens of kilometres. So this stays a guard for an unreachable state,
        /// and F-W2-8 is NOT shown to be reachable by it. The deep arm below is the same fixture pushed to a
        /// depth where all four corners DO project, which is what makes the fallback attributable to the corner
        /// rejection rather than to the extreme scale.</para>
        /// </summary>
        [Test]
        public void MapPitched_WhenACornerIsBehindTheCamera_TakesTheScreenBox_BitIdentically()
        {
            SymbolViewTransform view = OriginView();
            const float hugeRulerMpp = 1000f;
            const double shallowDepthM = 100.0;      // corner half-height 6000 m ⇒ one corner at z = −5900 m
            const double deepDepthM = 100000.0;      // both corners in front (94 km / 106 km)

            var deepProjected = Pools.New();
            StageCornerProbe(ref deepProjected, view, hugeRulerMpp, deepDepthM);
            var deepScreen = Pools.New();
            StageCornerProbe(ref deepScreen, default, hugeRulerMpp, deepDepthM);
            var shallowProjected = Pools.New();
            StageCornerProbe(ref shallowProjected, view, hugeRulerMpp, shallowDepthM);
            var shallowScreen = Pools.New();
            StageCornerProbe(ref shallowScreen, default, hugeRulerMpp, shallowDepthM);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T7  deep: projected=[{0:F3}, {1:F3}] screen=[{2:F3}, {3:F3}]  shallow: projected=" +
                "[{4:F3}, {5:F3}] screen=[{6:F3}, {7:F3}]",
                deepProjected.Boxes[0].Min.x, deepProjected.Boxes[0].Max.x,
                deepScreen.Boxes[0].Min.x, deepScreen.Boxes[0].Max.x,
                shallowProjected.Boxes[0].Min.x, shallowProjected.Boxes[0].Max.x,
                shallowScreen.Boxes[0].Min.x, shallowScreen.Boxes[0].Max.x));

            // Non-vacuity first: at a depth where every corner projects, the SAME transform and the SAME cell
            // give a genuinely different box — so the fallback below is attributable to the rejected corner.
            Assert.That(BitsEqual(Bits(deepProjected.Boxes, deepProjected.BoxCount),
                                  Bits(deepScreen.Boxes, deepScreen.BoxCount)), Is.False,
                "W3-T7 precondition (non-vacuity): with every corner in front of the camera the projected box " +
                "must differ from the screen box, or the bit-identity below says nothing about the corner guard.");

            AssertBitIdentical(Bits(shallowScreen.Boxes, shallowScreen.BoxCount),
                Bits(shallowProjected.Boxes, shallowProjected.BoxCount),
                "W3-T7: with one corner behind the camera the box must be the pre-W3 screen box");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T11 — F-W3-8: a corner that projects to a near-plane BLOW-UP falls back, so the AABB is bounded
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T11 — the magnitude guard, F-W3-8.</b> A corner just IN FRONT of the camera plane passes the
        /// behind-camera test (<c>clip.w &gt; 0</c>) and then divides into an arbitrarily large screen
        /// coordinate. It is FINITE, so no NaN check sees it, and before this guard it made the collision AABB
        /// unbounded — where the pre-W3 screen box was bounded by the cell. Proves: such a corner takes the
        /// screen-box fallback, BIT-identically.
        ///
        /// <para><b>Why this is a different state from W3-T7's</b>, and why one tooth cannot cover both: T7's
        /// corner is BEHIND the camera and is rejected by <c>TryProjectPoint</c> itself. This corner
        /// PROJECTS — successfully, to a real finite number — and is rejected only by the magnitude test.
        /// Removing the magnitude guard leaves T7 green (see the RED sweep), which is precisely why this tooth
        /// exists.</para>
        ///
        /// <para><b>The geometry.</b> Same GROUND-normal probe as W3-T7, so ŷ runs along the view axis and a
        /// corner offset moves the corner in DEPTH. The cell's half-height is 6000 m at this ruler, so an
        /// anchor at 6001 m puts the near corner at <c>z ≈ 1 m</c> — in front, so it projects, but with a
        /// <c>clip.w</c> three orders of magnitude below the corner's own lateral offset.</para>
        ///
        /// <para><b>Reachability: NONE, same as W3-T7.</b> This needs 1000 m per logical px at a 6 km depth.
        /// The shipped fixture's ruler is ~306 m/px at a look-at depth of tens of km. It is a guard for an
        /// unreachable state, kept because "unbounded" is not a state this system should be able to enter at
        /// all — not because a pose produces it.</para>
        /// </summary>
        [Test]
        public void MapPitched_WhenACornerBlowsUpNearThePlane_TakesTheScreenBox_BitIdentically()
        {
            SymbolViewTransform view = OriginView();
            const float hugeRulerMpp = 1000f;             // cell half-height 6 baked px ⇒ 6000 m
            const double blowUpDepthM = 6001.0;           // near corner at z ≈ 1 m: in FRONT, but barely
            const double deepDepthM = 100000.0;           // both corners comfortably in front

            var deepProjected = Pools.New();
            StageCornerProbe(ref deepProjected, view, hugeRulerMpp, deepDepthM);
            var blowUpProjected = Pools.New();
            StageCornerProbe(ref blowUpProjected, view, hugeRulerMpp, blowUpDepthM);
            var blowUpScreen = Pools.New();
            StageCornerProbe(ref blowUpScreen, default, hugeRulerMpp, blowUpDepthM);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T11  deep: [{0:F3}, {1:F3}]  blow-up projected: [{2:F3}, {3:F3}]  blow-up screen: [{4:F3}, {5:F3}]",
                deepProjected.Boxes[0].Min.x, deepProjected.Boxes[0].Max.x,
                blowUpProjected.Boxes[0].Min.x, blowUpProjected.Boxes[0].Max.x,
                blowUpScreen.Boxes[0].Min.x, blowUpScreen.Boxes[0].Max.x));

            // Non-vacuity 1: the deep arm must take the PROJECTED branch, or "falls back" below says nothing.
            Assert.That(BitsEqual(Bits(deepProjected.Boxes, deepProjected.BoxCount),
                                  Bits(blowUpScreen.Boxes, blowUpScreen.BoxCount)), Is.False,
                "W3-T11 precondition: at a depth where every corner projects sanely the projected box must " +
                "differ from the screen box.");

            // Non-vacuity 2: this must be a DIFFERENT state from W3-T7's, or the tooth is a duplicate that the
            // behind-camera rejection would satisfy on its own. The near corner sits one cell half-height
            // up-axis of the anchor, so its view depth is (anchor − halfHeight) — assert that is POSITIVE, i.e.
            // the corner is in FRONT of the camera and TryProjectPoint ACCEPTS it. Only the magnitude test can
            // reject it.
            const double cellHalfHeightM = 6.0 * hugeRulerMpp;         // 6 baked px at 1 em, in metres
            const double nearCornerDepthM = blowUpDepthM - cellHalfHeightM;
            Assert.That(nearCornerDepthM, Is.GreaterThan(0.0),
                $"W3-T11 precondition: the near corner must be IN FRONT of the camera (depth " +
                $"{nearCornerDepthM} m) — otherwise this duplicates W3-T7's behind-camera rejection.");

            // Non-vacuity 3: the fallback the tooth asserts must itself be bounded — that is the whole point.
            float guardedWidth = blowUpScreen.Boxes[0].Max.x - blowUpScreen.Boxes[0].Min.x;
            Assert.That(guardedWidth, Is.LessThan(SymbolScreenProjectionMaxProjectedPx),
                $"W3-T11 precondition: the fallback box must be BOUNDED — got width {guardedWidth}.");

            AssertBitIdentical(Bits(blowUpScreen.Boxes, blowUpScreen.BoxCount),
                Bits(blowUpProjected.Boxes, blowUpProjected.BoxCount),
                "W3-T11: a corner projecting past MaxProjectedPx must take the pre-W3 screen box");
        }

        /// <summary>Mirror of <c>SymbolScreenProjection.MaxProjectedPx</c>. Re-stated here rather than read
        /// back, so a change to the production threshold does not silently move this tooth's expectation.</summary>
        private const float SymbolScreenProjectionMaxProjectedPx = 1e5f;

        /// <summary>W3-T7's probe: the GROUND-normal fixture, whose ŷ runs along the view axis so a corner
        /// offset moves the corner in DEPTH.</summary>
        private static void StageCornerProbe(ref Pools p, SymbolViewTransform view, float mpp, double depthM)
        {
            int staged = StageW3Glyph(ref p, in view, GroundUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, textSizePx: TextQuadLayout.OneEm, mpp,
                depthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 43, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T7 precondition: the probe label must stage.");
        }

        private static bool BitsEqual(uint[] a, uint[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T8 — D7: `icon-rotate` is INSIDE the map-pitched box
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T8 — the tooth behind E10's KL-B1 amendment.</b> Proves: the map-pitched box includes the
        /// constant <c>icon-rotate</c> the renderer rotates the drawn corners by. A one-glyph map-pitched icon
        /// on a NON-SQUARE cell is staged at <c>icon-rotate</c> 0 and at 90°, and the box's projected
        /// half-extents SWAP.
        ///
        /// <para><b>The expected numbers are derived from the CELL's own corners</b> — half-extent
        /// <c>= PxPerMetreAtUnitDepth · (cellHalfBaked · TextSizePx · mpp / OneEm) / depth</c> — and
        /// <c>SymbolBearing.IconRotationRadians</c> is deliberately NOT read back. Because the cell is centred on
        /// its anchor, +90° and −90° produce the SAME axis-aligned bound, so this tooth is independent of the
        /// sign that conversion applies; what it pins is that the rotation is APPLIED AT ALL.</para>
        ///
        /// <para><b>Why 90° and not the shipped 180°.</b> 180° is the only <c>icon-rotate</c> any shipped
        /// map-pitched layer carries, and on a centre-anchored cell it is its own inverse — the AABB is
        /// identical, so it CANNOT discriminate. That is also why D7 is numerically a no-op on every shipped
        /// style even though it is a real change to the box.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_IncludesIconRotate()
        {
            SymbolViewTransform view = OriginView();
            const float cellHalfWidthBaked = 20f; // ⇒ a 40 × 12 baked cell: wide, so the swap is visible

            var upright = Pools.New();
            StageRotationProbe(ref upright, view, cellHalfWidthBaked, 0f);
            var turned = Pools.New();
            StageRotationProbe(ref turned, view, cellHalfWidthBaked, (float)(0.5 * math.PI_DBL));

            double metresPerBaked = SuppressionTextSizePx * SuppressionMpp / TextQuadLayout.OneEm;
            double pxPerBaked = PxPerMetreAtUnitDepth * metresPerBaked / SuppressionDepthM;
            double expectedHalfWidth  = cellHalfWidthBaked * pxPerBaked;
            double expectedHalfHeight = SuppressionCellHalfHeightBaked * pxPerBaked;

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T8  upright half-extents=({0:F4}, {1:F4}) px  turned=({2:F4}, {3:F4}) px  expected " +
                "upright=({4:F4}, {5:F4})",
                HalfWidthPx(upright.Boxes[0]), HalfHeightPx(upright.Boxes[0]),
                HalfWidthPx(turned.Boxes[0]), HalfHeightPx(turned.Boxes[0]),
                expectedHalfWidth, expectedHalfHeight));

            Assert.That(math.abs(expectedHalfWidth - expectedHalfHeight), Is.GreaterThan(1.0),
                $"W3-T8 precondition: the cell must be far from square in projection — half-extents " +
                $"{expectedHalfWidth:F4} and {expectedHalfHeight:F4} px — or a 90° rotation changes nothing " +
                "and this tooth discriminates nothing.");
            Assert.That(HalfWidthPx(upright.Boxes[0]), Is.EqualTo(expectedHalfWidth).Within(0.05),
                "W3-T8: at icon-rotate 0 the box's projected half-WIDTH must be the cell's own half-width.");
            Assert.That(HalfHeightPx(upright.Boxes[0]), Is.EqualTo(expectedHalfHeight).Within(0.05),
                "W3-T8: at icon-rotate 0 the box's projected half-HEIGHT must be the cell's own half-height.");
            Assert.That(HalfWidthPx(turned.Boxes[0]), Is.EqualTo(expectedHalfHeight).Within(0.05),
                $"W3-T8: at icon-rotate 90° the half-WIDTH must become the cell's half-HEIGHT " +
                $"({expectedHalfHeight:F4} px), read {HalfWidthPx(turned.Boxes[0]):F4}. An unchanged " +
                "half-width means the box omits icon-rotate and no longer bounds what the renderer draws.");
            Assert.That(HalfHeightPx(turned.Boxes[0]), Is.EqualTo(expectedHalfWidth).Within(0.05),
                $"W3-T8: at icon-rotate 90° the half-HEIGHT must become the cell's half-WIDTH " +
                $"({expectedHalfWidth:F4} px), read {HalfHeightPx(turned.Boxes[0]):F4}.");
        }

        private static void StageRotationProbe(ref Pools p, SymbolViewTransform view,
            float cellHalfWidthBaked, float iconRotateRadians)
        {
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, Cell(cellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians, sortKey: 0f, featureIndex: 44, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T8 precondition: the icon label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T9 — D9: the skirt is removed from the map-pitched box too
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T9 — the skirt contract, carried onto the projected box.</b> Proves: a map-pitched icon whose
        /// cell carries a transparent border (<c>CurvedGlyph.CellSkirt</c>) gets the SAME projected box as the
        /// same symbol with that border already removed from the cell and a zero skirt — i.e. the box bounds the
        /// icon's INK, not its skirt. The exact shape
        /// <c>SymbolStagingMathCurvedVertexTests.BuildRotatedGlyph_WithACellSkirt_EqualsTheSameCellPreShrunkByIt</c>
        /// already asserts for the screen box, now for the projected one.
        ///
        /// <para>Non-vacuity: against the SAME padded cell with a zero skirt the box must be strictly larger,
        /// so "equal to the pre-shrunk cell" cannot pass by the skirt simply being ignored on both sides.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_RemovesTheCellSkirt()
        {
            SymbolViewTransform view = OriginView();
            const float skirt = 3f;
            SymbolQuad padded = Cell(20f);
            var content = new SymbolQuad
            {
                TopLeft     = padded.TopLeft     + new float2(skirt, -skirt),
                BottomRight = padded.BottomRight - new float2(skirt, -skirt),
            };

            var withSkirt = Pools.New();
            StageSkirtProbe(ref withSkirt, view, padded, skirt);
            var preShrunk = Pools.New();
            StageSkirtProbe(ref preShrunk, view, content, 0f);
            var unshrunk = Pools.New();
            StageSkirtProbe(ref unshrunk, view, padded, 0f);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T9  withSkirt half-extents=({0:F4}, {1:F4})  preShrunk=({2:F4}, {3:F4})  unshrunk=" +
                "({4:F4}, {5:F4}) px",
                HalfWidthPx(withSkirt.Boxes[0]), HalfHeightPx(withSkirt.Boxes[0]),
                HalfWidthPx(preShrunk.Boxes[0]), HalfHeightPx(preShrunk.Boxes[0]),
                HalfWidthPx(unshrunk.Boxes[0]), HalfHeightPx(unshrunk.Boxes[0])));

            Assert.That(HalfWidthPx(unshrunk.Boxes[0]), Is.GreaterThan(HalfWidthPx(withSkirt.Boxes[0]) + 1e-4),
                "W3-T9 precondition (non-vacuity): a non-zero cell skirt must SHRINK the projected box — " +
                "otherwise this tooth proves nothing.");
            AssertBitIdentical(Bits(preShrunk.Boxes, preShrunk.BoxCount),
                Bits(withSkirt.Boxes, withSkirt.BoxCount),
                "W3-T9: the projected box of a skirted cell must equal that of the same cell pre-shrunk by it");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W3-T10 — the ŷ SENSE of the projected box, on a cell that can actually see it
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W3-T10 — the observer for <c>TryBuildProjectedWorldGlyph</c>'s <c>ŷ = cross(x̂, up)</c>.</b>
        /// Proves: a positive y-UP cell coordinate lands ABOVE the anchor on screen, by the amount the cell's
        /// own corners predict.
        ///
        /// <para><b>Why it exists, and why nothing else in the stage does this job.</b> The box is an
        /// <b>AABB</b> of the four cell corners, and flipping ŷ maps corner <c>(cx, cy) → (cx, −cy)</c> — a
        /// PERMUTATION of the corner set whenever the cell is symmetric about its anchor in y, and the AABB of
        /// a set is invariant under permutation. The shipped <c>'F'</c> cell is block-centred and W4 centres
        /// curved cells optically on the path, and every other synthetic cell here is <c>Cell(hw)</c> =
        /// <c>(±hw, ±6)</c> — so a flipped ŷ was measured (injection I2) to leave the ENTIRE suite green,
        /// W3-T1, W3-T2 and W3-T3 included. W2's 22.56 px flipped-sign separation was an ink-CENTROID reading
        /// and does not carry to an AABB. A sign must be
        /// read where the code is not inert, and for an AABB that means a cell whose y extent does NOT
        /// straddle its anchor.</para>
        ///
        /// <para>The cell here sits entirely ABOVE its anchor (y from +6 to +18 baked), so a flip translates
        /// the whole box by <c>|top + bottom| · pxPerBaked</c> — asserted as a precondition to be well over a
        /// pixel, against edge expectations derived from the cell's own corners.</para>
        ///
        /// <para><b>What this does NOT pin.</b> Like W3-T1's oracle it re-derives the sense rather than
        /// importing it from an independent reference, so it catches a production-only flip, not a SHARED
        /// convention error. The independent reference for the shader's own sign remains W2's tilt-0 ink
        /// centroid (<c>MapPitchedGlyphSizeTiltZeroTests</c>), and for the CPU box the closest thing is W3-T2's
        /// ink containment — which, being a containment, only bites once the flip exceeds the box.</para>
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_PutsAPositiveCellYAboveTheAnchor()
        {
            SymbolViewTransform view = OriginView();
            // Entirely ABOVE the anchor — the whole point (see this tooth's doc).
            const float cellTopBaked = 18f, cellBottomBaked = 6f, cellHalfWidthBaked = 20f;
            var offCentreCell = new SymbolQuad
            {
                TopLeft     = new float2(-cellHalfWidthBaked, cellTopBaked),
                BottomRight = new float2( cellHalfWidthBaked, cellBottomBaked),
            };

            var p = Pools.New();
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, in offCentreCell,
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 47, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T10 precondition: the off-centre-cell label must stage.");

            double metresPerBaked = SuppressionTextSizePx * SuppressionMpp / TextQuadLayout.OneEm;
            double pxPerBaked = PxPerMetreAtUnitDepth * metresPerBaked / SuppressionDepthM;
            double anchorY = 0.5 * ViewportPx;                  // the road is at lateral 0 ⇒ screen centre
            double expectedMinY = anchorY + cellBottomBaked * pxPerBaked;
            double expectedMaxY = anchorY + cellTopBaked * pxPerBaked;
            double flipSeparationPx = (cellTopBaked + cellBottomBaked) * pxPerBaked;

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W3-T10  anchorY={0:F3}  box y=[{1:F4}, {2:F4}]  expected=[{3:F4}, {4:F4}]  a flipped ŷ would " +
                "move it by {5:F4} px", anchorY, p.Boxes[0].Min.y, p.Boxes[0].Max.y, expectedMinY, expectedMaxY,
                flipSeparationPx));

            Assert.That(flipSeparationPx, Is.GreaterThan(1.0),
                $"W3-T10 precondition: the cell must be genuinely OFF-CENTRE in y — a flip would move the box " +
                $"by {flipSeparationPx:F4} px, and below ~1 px this tooth stops discriminating the sign it " +
                "exists to read.");
            Assert.That(p.Boxes[0].Min.y, Is.EqualTo(expectedMinY).Within(0.05),
                $"W3-T10: the box's LOWER edge must be the cell's own +{cellBottomBaked} baked px ABOVE the " +
                $"anchor ({expectedMinY:F4}), read {p.Boxes[0].Min.y:F4}. A value BELOW the anchor means " +
                "ŷ = cross(x̂, up) has been flipped and the box sits on the wrong side of the road.");
            Assert.That(p.Boxes[0].Max.y, Is.EqualTo(expectedMaxY).Within(0.05),
                $"W3-T10: the box's UPPER edge must be the cell's own +{cellTopBaked} baked px above the " +
                $"anchor ({expectedMaxY:F4}), read {p.Boxes[0].Max.y:F4}.");
            Assert.That(p.Boxes[0].Min.y, Is.GreaterThan(anchorY),
                $"W3-T10: a cell lying entirely above its anchor must produce a box lying entirely above the " +
                $"anchor's projection ({anchorY:F3}) — read a lower edge of {p.Boxes[0].Min.y:F4}.");
        }

        private static void StageSkirtProbe(ref Pools p, SymbolViewTransform view, SymbolQuad cell, float skirt)
        {
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, in cell,
                skirt, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 45, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "W3-T9 precondition: the icon label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GLOBE-A — the CPU ground frame with a NON-ZERO Gram-Schmidt axial term (GA-T1…GA-T3)
        //
        // SymbolBox.TryBuildProjectedWorldGlyph builds the glyph's ground frame as
        //
        //     axial = dot(tangent, up);   x̂ = normalize(tangent − up·axial);   ŷ = cross(x̂, up)
        //
        // On EVERY other fixture in this repo `axial` is EXACTLY ZERO — Mercator's surface normal is the
        // constant (0,1,0) and every baked road tangent is horizontal, so the tangent already lies IN the
        // surface and the subtraction is a no-op. The orthogonalisation has therefore never been exercised
        // by anything (recorded as followUp F-W3-3, and F-W2-3 for the shader's own copy). These teeth are
        // the first that reach it.
        //
        // SCOPE. The CPU copy ONLY. The shader's copy (SymbolWorldPitchAlign.hlsl:95-106) is unreachable
        // from a staging test — WorldBillboardVertex.Up is carried but never written back, and
        // ShaderStructureTests parses source text rather than running it — so nothing below claims to
        // observe it. That half needs a rendered ink measurement and is a separate stage.
        //
        // THE TRAP THIS SECTION EXISTS TO AVOID, TWICE OVER.
        //   1. `axial ≈ (t − ½)·θ` is EXACTLY ZERO at the chord midpoint. Every existing curved fixture
        //      anchors its symbol at the arc midpoint, so a spherical fixture built the obvious way is
        //      exactly as blind as Mercator while LOOKING like coverage. GA-T2 anchors at t = 0.9 and
        //      GA-T1 asserts the achieved |axial| against an absolute floor with its measured value
        //      printed; GA-T3 stages the midpoint deliberately, as the contrast that proves the placement
        //      is load-bearing rather than decorative.
        //   2. Anchoring at an interior POLYLINE VERTEX is a second, unrecorded inert shape.
        //      StageCurvedAnchor orients the glyph by the CHORD across its own footprint, not by the raw
        //      segment direction; at an interior vertex that probe straddles the bend symmetrically and
        //      reproduces the true surface tangent, so `axial` collapses to ~0 again. Hence a SINGLE
        //      segment with the anchor off its midpoint: the probe then stays inside the one chord, so the
        //      tangent is the chord direction at every t while the sampled up swings with t.
        //
        // HAND-BUILT SPHERE, NOT SphericalProjection.ProjectPoint. The frame math consumes exactly two
        // geometric inputs — tangentRender and surfaceUp — and is indifferent to where they came from; that
        // the projection emits a genuinely varying radial up along a curved feature is already pinned by
        // SymbolUpCarrierChainTests. Building the arc here buys the thing that matters: the expectations
        // below call NO production code at all (no ProjectPoint, no TangentBasisAt), so a shared error
        // cannot hide inside the oracle. What is not hand-waved is the sphere itself — the radius and the
        // segment arc are production's own constants, and GA-T1 asserts both on-sphere invariants directly.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The globe fixture's sphere radius — production's own, so the fixture cannot drift onto a
        /// toy sphere whose arc-to-metre relationship is not the one the renderer ships.</summary>
        private const double GlobeRadiusM = SphericalProjection.Radius;

        /// <summary>The great-circle arc ONE path segment subtends: production's own subdivision cap
        /// (<c>SphericalProjection.MaxCurveSegmentRad</c>, 2°). This is the WORST case a realistically built
        /// spherical path can present to the ground frame, since <c>SymbolFeatureExtractor</c> subdivides to
        /// this bound — so the fixture is not inflating the angle. Narrowing the cap will red GA-T1's
        /// |axial| floor, deliberately: the achieved signal is a function of it.</summary>
        private const double GlobeSegmentRad = SphericalProjection.MaxCurveSegmentRad;

        /// <summary>The map tilt: the angle between the surface normal at the anchor and the direction back
        /// to the camera. The screen consequence of a mis-built x̂ scales with <c>sin(tilt)</c> — it is pure
        /// DEPTH error at tilt 0, which is why <see cref="CameraFacingUp"/> could never observe this — so a
        /// tilt-0 arm would be inert however non-zero the axial term was. 60° is a realistic upper-ish tilt
        /// and costs only 13% of the grazing-case signal.</summary>
        private static readonly double GlobeTiltRad = math.radians(60.0);

        /// <summary>The arc's "east": the direction the road runs at the start of the arc. With
        /// <see cref="OriginView"/>'s camera looking down <c>+Ẑ</c> this puts the symbol across the screen.</summary>
        private static readonly double3 GlobeEast = new double3(1.0, 0.0, 0.0);

        /// <summary>The surface normal at arc angle 0 — tilted <see cref="GlobeTiltRad"/> away from facing
        /// the camera. Together with <see cref="GlobeEast"/> it spans the arc's plane.</summary>
        private static readonly double3 GlobeUpAtArcStart =
            new double3(0.0, math.sin(GlobeTiltRad), -math.cos(GlobeTiltRad));

        /// <summary>The arc plane's normal, <c>ê₁ × ê₂</c>. This is EXACTLY the ground frame's ŷ at every
        /// sample: for x̂ = cos α·ê₁ − sin α·ê₂ and up = sin α·ê₁ + cos α·ê₂,
        /// <c>cross(x̂, up) = (cos²α + sin²α)·(ê₁ × ê₂) = ê₃</c>, independent of α. Stated here as fixture
        /// geometry so the expectation does not have to evaluate production's cross product.</summary>
        private static readonly double3 GlobeAcross = math.cross(GlobeEast, GlobeUpAtArcStart);

        /// <summary>`text-size` for the globe teeth — 7 em. <b>A DELIBERATE AMPLIFICATION, and the tooth
        /// says so.</b> The omitted-orthogonalisation error is <c>cellHalfWidthBaked/OneEm · TextSizePx ·
        /// axial · sin(tilt)</c> px: ≈1.6 px here, but ≈0.19 px at an ordinary 16 px text size, which is
        /// structurally invisible. See GA-T2's doc for what that means about the tooth's claim.</summary>
        private const float GlobeTextSizePx = 7f * TextQuadLayout.OneEm;

        /// <summary>The per-frame ruler. With <see cref="GlobeDepthM"/> derived from it below, one world
        /// metre at the anchor is exactly this many logical px — so the fixture's ruler and its pose agree
        /// and the drawn glyph is the size `text-size` says it is.</summary>
        private const float GlobeMpp = 50f;

        /// <summary>The anchor's view depth, chosen so <see cref="GlobeMpp"/> is the TRUE metres-per-pixel
        /// there (<c>depth = mpp · pxPerMetreAtUnitDepth</c>).</summary>
        private static readonly double GlobeDepthM = GlobeMpp * PxPerMetreAtUnitDepth;

        /// <summary>The parametric position of the anchor along the single chord. <b>0.9, not 0.5</b> — see
        /// this section's header. At the chord midpoint the axial term is exactly zero.</summary>
        private const double GlobeAnchorT = 0.9;

        private const float GlobeCellHalfWidthBaked = 20f;
        private const float GlobeCellTopBaked       = 18f;
        private const float GlobeCellBottomBaked    = 6f;

        /// <summary>The globe teeth's glyph cell: y-OFF-CENTRE, entirely above its anchor. F-W3-6 measured
        /// that an AABB is invariant under a ŷ flip on a y-symmetric cell, so a symmetric cell here would
        /// leave the ŷ leg of the frame unobserved for free. W3-T10's shape, reused for the same reason.</summary>
        private static SymbolQuad GlobeCell() => new SymbolQuad
        {
            TopLeft     = new float2(-GlobeCellHalfWidthBaked, GlobeCellTopBaked),
            BottomRight = new float2( GlobeCellHalfWidthBaked, GlobeCellBottomBaked),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1f, 1f), LineIndex = 0,
        };

        /// <summary>The unit radial normal at arc angle <paramref name="phi"/>, in the arc's own plane.</summary>
        private static double3 GlobeSurfaceUp(double phi)
            => math.sin(phi) * GlobeEast + math.cos(phi) * GlobeUpAtArcStart;

        /// <summary>The on-sphere point at arc angle <paramref name="phi"/>, in the SPHERE's frame (centre at
        /// the origin) — exactly <see cref="GlobeRadiusM"/> from the centre, by construction.</summary>
        private static double3 GlobeSurfacePoint(double phi) => GlobeRadiusM * GlobeSurfaceUp(phi);

        /// <summary>
        /// Builds the fixture's ONE on-sphere segment, translated so the anchor at <paramref name="t"/>
        /// lands at render <c>(0, 0, GlobeDepthM)</c> — dead centre of <see cref="OriginView"/>'s viewport.
        /// The path vertices are on the sphere and <paramref name="worldUps"/> are their exact radial
        /// normals (narrowed to <c>float3</c>, which is the type the carrier chain uses).
        /// </summary>
        private static void BuildGlobeArc(double t, out double3[] worldPath, out float3[] worldUps,
            out double3 sphereCentreRender, out double3 anchorRender)
        {
            double3 startPoint = GlobeSurfacePoint(0.0);
            double3 endPoint   = GlobeSurfacePoint(GlobeSegmentRad);

            anchorRender = new double3(0.0, 0.0, GlobeDepthM);
            sphereCentreRender = anchorRender - math.lerp(startPoint, endPoint, t);

            worldPath = new[] { startPoint + sphereCentreRender, endPoint + sphereCentreRender };
            worldUps  = new[] { NarrowToFloat3(GlobeSurfaceUp(0.0)), NarrowToFloat3(GlobeSurfaceUp(GlobeSegmentRad)) };
        }

        private static float3 NarrowToFloat3(in double3 v) => new float3((float)v.x, (float)v.y, (float)v.z);

        /// <summary>Stages ONE map-pitched glyph on the globe arc, anchored at <paramref name="t"/>. The
        /// screen polyline is the TRUE projection of the world polyline (<see cref="ProjectPx"/>), so the
        /// pre-W3 screen box the control arm reads is the box the shipped code would really have built.</summary>
        private static int StageGlobeGlyph(ref Pools p, in SymbolViewTransform view, double t, int featureIndex)
        {
            BuildGlobeArc(t, out double3[] worldPath, out float3[] worldUps, out _, out _);
            var screenPath = new[] { ProjectPx(worldPath[0]), ProjectPx(worldPath[1]) };
            var glyphs  = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = GlobeCell() } };
            var anchors = new[] { new LineAnchor(0, (float)t) };
            CurvedStageInput s = Input(AlignmentMode.Map, GlobeMpp, GlobeTextSizePx,
                maxAngleDeg: 180f, featureIndex);
            return Stage(in s, screenPath, worldPath, glyphs, anchors, ref p,
                view: view, worldUpPathOverride: worldUps);
        }

        /// <summary>The angle, within the arc's plane, of the surface normal <c>PolylineArcMath.SampleUp</c>
        /// produces at <paramref name="t"/> — <c>normalize(lerp(up₀, up₁, t))</c> written out in closed form.
        /// At <c>t = ½</c> the half-angle identity makes this exactly <c>θ/2</c>, which is the chord's own
        /// angle: that identity IS the midpoint trap.</summary>
        private static double GlobeSampledUpAngle(double t)
            => math.atan2(t * math.sin(GlobeSegmentRad), (1.0 - t) + t * math.cos(GlobeSegmentRad));

        /// <summary>The chord direction of the single segment — the tangent production resolves at EVERY
        /// <c>t</c> on it. Its angle within the arc plane is <c>θ/2</c>, the arc's midpoint tangent.</summary>
        private static double3 GlobeChordDirection()
            => math.cos(GlobeSegmentRad * 0.5) * GlobeEast - math.sin(GlobeSegmentRad * 0.5) * GlobeUpAtArcStart;

        /// <summary>
        /// The screen AABB the fixture's own geometry predicts, built with the ground frame whose x̂ lies at
        /// <paramref name="frameAngleRad"/> within the arc plane. Two callers, and the difference between
        /// them is the whole tooth:
        /// <list type="bullet">
        /// <item><c>GlobeSampledUpAngle(t)</c> — the CORRECT in-surface frame. x̂ is characterised here as
        /// "the sampled surface normal rotated 90° within the arc's plane, on the +ê₁ side", which is a
        /// statement about the fixture's geometry and NOT a second evaluation of
        /// <c>normalize(tangent − up·axial)</c>. ŷ is <see cref="GlobeAcross"/>, exactly.</item>
        /// <item><c>GlobeSegmentRad/2</c> — the chord's own angle, i.e. what the frame degenerates to if the
        /// orthogonalisation is DROPPED and x̂ is just the normalized tangent. Used only to size the
        /// separation GA-T2 asserts as its non-vacuity floor. (It approximates the dropped-subtraction ŷ as
        /// ê₃ too; the real one is <c>cross(tangent, up)</c>, shorter by <c>1 − cos ε ≈ 1e-4</c>, which
        /// moves no pixel that matters here.)</item>
        /// </list>
        /// </summary>
        private static void GlobeExpectedBox(double frameAngleRad, out double2 min, out double2 max)
        {
            double3 anchorRender = new double3(0.0, 0.0, GlobeDepthM);
            double3 alongSurface = math.cos(frameAngleRad) * GlobeEast - math.sin(frameAngleRad) * GlobeUpAtArcStart;
            double3 acrossSurface = GlobeAcross;

            // The same metres-per-baked-px the renderer applies: TextSizePx · metresPerLogicalPixel / OneEm.
            double metresPerBaked = GlobeTextSizePx * (double)GlobeMpp / TextQuadLayout.OneEm;
            double halfWidthM = GlobeCellHalfWidthBaked * metresPerBaked;
            double topM       = GlobeCellTopBaked       * metresPerBaked;
            double bottomM    = GlobeCellBottomBaked    * metresPerBaked;

            double2 topLeft     = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface, -halfWidthM, topM);
            double2 topRight    = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface,  halfWidthM, topM);
            double2 bottomRight = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface,  halfWidthM, bottomM);
            double2 bottomLeft  = GlobeProjectCorner(anchorRender, alongSurface, acrossSurface, -halfWidthM, bottomM);

            min = math.min(math.min(topLeft, topRight), math.min(bottomRight, bottomLeft));
            max = math.max(math.max(topLeft, topRight), math.max(bottomRight, bottomLeft));
        }

        /// <summary>Displaces ONE y-up corner in the stated ground frame and projects it with the fixture's
        /// own <see cref="ProjectPx"/> arithmetic — <c>½·viewport + pxPerMetreAtUnitDepth · offset / depth</c>,
        /// two constants and a divide, reading nothing back out of production.</summary>
        private static double2 GlobeProjectCorner(in double3 anchorRender, in double3 alongSurface,
            in double3 acrossSurface, double cornerX, double cornerY)
        {
            double3 corner = anchorRender + alongSurface * cornerX + acrossSurface * cornerY;
            return new double2(
                0.5 * ViewportPx + PxPerMetreAtUnitDepth * corner.x / corner.z,
                0.5 * ViewportPx + PxPerMetreAtUnitDepth * corner.y / corner.z);
        }

        /// <summary>
        /// Stages the globe fixture TWICE — once with no camera (<c>view: default</c>, W3's D5 state ⇒ the
        /// pre-W3 SCREEN box) and once through <see cref="OriginView"/> (the projected box) — and asserts the
        /// two differ substantially.
        ///
        /// <para><b>This is the control arm, and it is not optional.</b>
        /// <c>TryBuildProjectedWorldGlyph</c> sits behind five gates — <c>cornerMetresPerLogicalPixel &gt; 0</c>,
        /// <c>view.IsUsable</c>, a zero up, a zero tangent, and <c>|axial| &gt; 1 − 1e-3</c> — and every one
        /// of them SILENTLY yields the screen box instead. W3-T5 in this epic held for exactly that reason
        /// (an all-zero surface normal tripped guard 1 long before the quantity it claimed to test mattered),
        /// and it was found only because an injection failed to red it. A globe tooth that never checks which
        /// branch ran would be the same test.</para>
        /// </summary>
        private static SymbolBox StageGlobeProjectedBox(double t, int featureIndex, string toothId)
        {
            var projectedPool = Pools.New();
            int projectedStaged = StageGlobeGlyph(ref projectedPool, OriginView(), t, featureIndex);
            var screenPool = Pools.New();
            int screenStaged = StageGlobeGlyph(ref screenPool, default, t, featureIndex);

            Assert.That(projectedStaged == 1 && screenStaged == 1, Is.True,
                $"{toothId} precondition: both arms must stage exactly one label — staged " +
                $"{projectedStaged} (projected) and {screenStaged} (screen).");

            SymbolBox projected = projectedPool.Boxes[0];
            SymbolBox screen = screenPool.Boxes[0];
            double separation = math.abs(HalfHeightPx(projected) - HalfHeightPx(screen));

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}  control arm: projected half-height={1:F4} px  screen(pre-W3) half-height={2:F4} px  " +
                "separation={3:F4} px", toothId, HalfHeightPx(projected), HalfHeightPx(screen), separation));

            Assert.That(separation, Is.GreaterThan(5.0),
                $"{toothId} precondition (control arm): the projected box must differ from the pre-W3 screen " +
                $"box by well over a pixel, measured {separation:F4} px. If they agree, one of " +
                "TryBuildProjectedWorldGlyph's five gates rejected this fixture and the box below is the " +
                "SCREEN box — the assertions would then be testing BuildRotatedGlyph, not the ground frame.");
            return projected;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GA-T1 — the fixture IS spherical, and its axial term IS non-zero. R1's precondition.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>GA-T1 — the precondition that stops GA-T2/GA-T3 from passing vacuously.</b> Asserts, with every
        /// value printed:
        /// <list type="number">
        /// <item>the path is genuinely ON a sphere of production's radius, and the supplied ups are its exact
        /// radial normals — the entire content of the claim "this is a spherical fixture";</item>
        /// <item>the surface normal and tangent PRODUCTION resolved (read back off the staged
        /// <see cref="PlacedQuad"/>, not recomputed) are the ones this fixture's geometry states;</item>
        /// <item><b>the achieved <c>|axial| = |dot(tangent, up)|</c> clears an ABSOLUTE floor of 0.010</b>,
        /// and separately matches the closed form <c>sin(α(t) − θ/2)</c>.</item>
        /// </list>
        ///
        /// <para><b>Why the floor is a typed literal and not "agrees with the closed form".</b> Agreement is
        /// satisfied with both sides at ZERO — which is precisely what happens if the anchor drifts back
        /// toward the chord midpoint, and it is the failure this whole section exists to prevent. The two
        /// assertions answer different questions: the floor says the fixture still has signal, the closed
        /// form says the signal is the one we think it is.</para>
        ///
        /// <para>The final arm stages the SAME fixture at <c>t = 0.5</c> and measures ~0, which is the
        /// midpoint trap demonstrated in-suite rather than asserted in prose: at the chord midpoint
        /// <c>α = θ/2</c> exactly (half-angle identity), so a spherical fixture anchored there is exactly as
        /// blind as Mercator.</para>
        /// </summary>
        [Test]
        public void MapPitched_SphericalArcOffTheChordMidpoint_HasANonZeroGroundFrameAxialTerm()
        {
            BuildGlobeArc(GlobeAnchorT, out double3[] worldPath, out float3[] worldUps,
                out double3 sphereCentreRender, out _);

            // (1) on-sphere invariants — stated, not assumed.
            for (int v = 0; v < worldPath.Length; v++)
            {
                double radius = math.length(worldPath[v] - sphereCentreRender);
                double radialAgreement = math.dot(
                    new double3(worldUps[v].x, worldUps[v].y, worldUps[v].z),
                    (worldPath[v] - sphereCentreRender) / GlobeRadiusM);
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "GA-T1  vertex {0}: |P − C| = {1:F6} m (R = {2:F6})  up·radial = {3:F9}",
                    v, radius, GlobeRadiusM, radialAgreement));
                Assert.That(radius, Is.EqualTo(GlobeRadiusM).Within(1e-3),
                    $"GA-T1: path vertex {v} must lie ON the sphere of production's own radius.");
                Assert.That(radialAgreement, Is.EqualTo(1.0).Within(1e-6),
                    $"GA-T1: the up supplied for vertex {v} must be its exact radial normal — a non-radial " +
                    "up would make this a curved-path fixture, not a spherical one.");
            }

            // (2) what production actually resolved, read back off the staged quad.
            var p = Pools.New();
            int staged = StageGlobeGlyph(ref p, OriginView(), GlobeAnchorT, featureIndex: 60);
            Assert.That(staged, Is.EqualTo(1), "GA-T1 precondition: the globe label must stage.");

            float3 sampledUp = p.Quads[0].SurfaceUp;
            float3 sampledTangent = p.Quads[0].Tangent;
            double3 expectedUp = GlobeSurfaceUp(GlobeSampledUpAngle(GlobeAnchorT));
            double3 expectedTangent = GlobeChordDirection();

            double upError = math.length(new double3(sampledUp.x, sampledUp.y, sampledUp.z) - expectedUp);
            double tangentError =
                math.length(new double3(sampledTangent.x, sampledTangent.y, sampledTangent.z) - expectedTangent);

            // (3) the achieved axial term — measured off production's own two vectors.
            double measuredAxial = math.dot(sampledTangent, sampledUp);
            double predictedAxial = math.sin(GlobeSampledUpAngle(GlobeAnchorT) - GlobeSegmentRad * 0.5);

            var midpointPool = Pools.New();
            int midpointStaged = StageGlobeGlyph(ref midpointPool, OriginView(), 0.5, featureIndex: 61);
            Assert.That(midpointStaged, Is.EqualTo(1), "GA-T1 precondition: the midpoint arm must stage.");
            double midpointAxial =
                math.dot(midpointPool.Quads[0].Tangent, midpointPool.Quads[0].SurfaceUp);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "GA-T1  segment arc θ={0:F6} rad ({1:F4}°)  anchor t={2}\n" +
                "GA-T1  sampled up=({3:F9}, {4:F9}, {5:F9})  |error|={6:E3}\n" +
                "GA-T1  sampled tangent=({7:F9}, {8:F9}, {9:F9})  |error|={10:E3}\n" +
                "GA-T1  MEASURED |axial| = {11:F9}   (closed form {12:F9}, floor {13:F3})\n" +
                "GA-T1  same fixture at the chord midpoint t=0.5: |axial| = {14:E3}  ← the inert case",
                GlobeSegmentRad, math.degrees(GlobeSegmentRad), GlobeAnchorT,
                sampledUp.x, sampledUp.y, sampledUp.z, upError,
                sampledTangent.x, sampledTangent.y, sampledTangent.z, tangentError,
                math.abs(measuredAxial), math.abs(predictedAxial), GlobeMinimumAxial,
                math.abs(midpointAxial)));

            Assert.That(upError, Is.LessThan(1e-6),
                $"GA-T1: the surface normal production sampled must be the fixture's own radial normal at " +
                $"t = {GlobeAnchorT} — measured error {upError:E3}.");
            Assert.That(tangentError, Is.LessThan(1e-6),
                $"GA-T1: the tangent production resolved must be the segment's chord direction — measured " +
                $"error {tangentError:E3}. A different value means the chord probe left this segment, which " +
                "would change what the axial term below even means.");

            Assert.That(math.abs(measuredAxial), Is.GreaterThan(GlobeMinimumAxial),
                $"GA-T1: the achieved |axial| is {math.abs(measuredAxial):F9}, at or below the {GlobeMinimumAxial:F3} " +
                "floor. The ground frame's Gram-Schmidt is then INERT and GA-T2 proves nothing — this is the " +
                "chord-midpoint trap. Move the anchor away from t = 0.5, or widen the segment arc.");
            Assert.That(math.abs(measuredAxial), Is.EqualTo(math.abs(predictedAxial)).Within(1e-5),
                $"GA-T1: |axial| must be the closed form sin(α(t) − θ/2) = {math.abs(predictedAxial):F9}, " +
                $"measured {math.abs(measuredAxial):F9}.");
            Assert.That(math.abs(midpointAxial), Is.LessThan(1e-5),
                $"GA-T1: at the CHORD MIDPOINT the axial term must vanish ({math.abs(midpointAxial):E3} " +
                "measured). If it does not, the closed form above is wrong and GA-T3's whole premise — that " +
                "the midpoint is the inert case — goes with it.");
        }

        /// <summary>The absolute floor GA-T1 holds the achieved <c>|axial|</c> to. Chosen just under the
        /// 0.013963 this fixture achieves at the production 2° cap, so ordinary float noise cannot trip it
        /// while a drift back toward the midpoint (or a halved segment arc) will.</summary>
        private const double GlobeMinimumAxial = 0.010;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GA-T2 — the headline: the projected box uses the IN-SURFACE frame, not the raw tangent
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>GA-T2 — the first tooth anywhere in this repo that observes
        /// <c>x̂ = normalize(tangent − up·axial)</c> doing any work.</b> Proves: with the symbol anchored OFF
        /// the chord midpoint of an on-sphere segment, the four edges of the projected collision AABB are
        /// where a frame whose x̂ lies IN the surface puts them — and measurably not where the raw chord
        /// tangent would.
        ///
        /// <para><b>What this tooth discriminates, stated honestly.</b> At the production subdivision cap
        /// (2°) the achieved <c>|axial|</c> is 0.0140, so <b>omitting</b> the orthogonalisation tilts x̂ out
        /// of the surface by 0.0140 rad — which at an ordinary 16 px text size moves a corner by ≈0.19 px,
        /// i.e. is structurally invisible. <b>This tooth is therefore NOT guarding a visible production
        /// defect at ordinary text sizes.</b> It guards two things. First, and mainly, a wrong OPERAND ORDER
        /// (<c>normalize(up − tangent·axial)</c> and friends), which does not perturb x̂ — it replaces it
        /// with a different vector entirely, ≈120 px away here. Second, the presence of the subtraction at
        /// all, which is observable only because this fixture deliberately amplifies <c>text-size</c> to
        /// 7 em; the separation at that size is ≈1.6 px per y edge, asserted below as a non-vacuity floor
        /// rather than assumed.</para>
        ///
        /// <para><b>Why the oracle cannot share production's error.</b> Every expected pixel comes from the
        /// fixture's stated geometry — an explicit on-sphere arc, its explicit radial normals, x̂ as "the
        /// sampled normal rotated 90° in the arc's plane", ŷ as the arc plane's normal, and
        /// <see cref="ProjectPx"/>'s two-constant projection. Nothing here evaluates
        /// <c>normalize(tangent − up·axial)</c> or <c>cross(x̂, up)</c>. (W3-T1's ŷ leg and F-W3-6 are what
        /// this rule was learned from.)</para>
        ///
        /// <para><b>Scope.</b> The CPU copy only. The shader's identical expression
        /// (<c>SymbolWorldPitchAlign.hlsl</c>) is NOT observed here and is not claimed to be: no vertex stage
        /// runs in a staging test, and the per-vertex <c>Up</c> is carried unconsumed on readback.</para>
        /// </summary>
        [Test]
        public void MapPitched_SphericalArcOffTheChordMidpoint_ProjectedBoxUsesTheInSurfaceGroundFrame()
        {
            SymbolBox box = StageGlobeProjectedBox(GlobeAnchorT, featureIndex: 62, toothId: "GA-T2");

            GlobeExpectedBox(GlobeSampledUpAngle(GlobeAnchorT), out double2 expectedMin, out double2 expectedMax);
            GlobeExpectedBox(GlobeSegmentRad * 0.5, out double2 rawTangentMin, out double2 rawTangentMax);

            double lowerEdgeSeparation = math.abs(expectedMin.y - rawTangentMin.y);
            double upperEdgeSeparation = math.abs(expectedMax.y - rawTangentMax.y);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "GA-T2  box      x=[{0:F4}, {1:F4}]  y=[{2:F4}, {3:F4}]\n" +
                "GA-T2  expected x=[{4:F4}, {5:F4}]  y=[{6:F4}, {7:F4}]   (in-surface x̂)\n" +
                "GA-T2  dropping the orthogonalisation would give y=[{8:F4}, {9:F4}] — a separation of " +
                "{10:F4} px (lower edge) and {11:F4} px (upper edge)",
                box.Min.x, box.Max.x, box.Min.y, box.Max.y,
                expectedMin.x, expectedMax.x, expectedMin.y, expectedMax.y,
                rawTangentMin.y, rawTangentMax.y, lowerEdgeSeparation, upperEdgeSeparation));

            Assert.That(math.min(lowerEdgeSeparation, upperEdgeSeparation), Is.GreaterThan(1.0),
                $"GA-T2 precondition (non-vacuity): dropping the orthogonalisation must move BOTH y edges by " +
                $"well over the 0.05 px tolerance below — measured {lowerEdgeSeparation:F4} px and " +
                $"{upperEdgeSeparation:F4} px. Below ~1 px this tooth stops discriminating the expression it " +
                "exists to read, however non-zero the axial term is.");

            Assert.That(box.Min.y, Is.EqualTo(expectedMin.y).Within(0.05),
                $"GA-T2: the box's LOWER edge must be where an IN-SURFACE x̂ puts the cell's bottom corners " +
                $"({expectedMin.y:F4}), read {box.Min.y:F4}. {rawTangentMin.y:F4} would mean x̂ is the raw " +
                "chord tangent — the Gram-Schmidt subtraction is gone.");
            Assert.That(box.Max.y, Is.EqualTo(expectedMax.y).Within(0.05),
                $"GA-T2: the box's UPPER edge must be {expectedMax.y:F4}, read {box.Max.y:F4} " +
                $"(raw-tangent frame would give {rawTangentMax.y:F4}).");
            Assert.That(box.Min.x, Is.EqualTo(expectedMin.x).Within(0.05),
                $"GA-T2: the box's LEFT edge must be {expectedMin.x:F4}, read {box.Min.x:F4}.");
            Assert.That(box.Max.x, Is.EqualTo(expectedMax.x).Within(0.05),
                $"GA-T2: the box's RIGHT edge must be {expectedMax.x:F4}, read {box.Max.x:F4}.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GA-T3 — the contrast arm: the SAME fixture at the chord midpoint is inert
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>GA-T3 — the midpoint arm, and it is INERT ON PURPOSE.</b> Stages the identical globe fixture at
        /// <c>t = 0.5</c>, where <c>α = θ/2</c> exactly and the axial term vanishes, and asserts the box
        /// against the same oracle.
        ///
        /// <para><b>Its job is not coverage — it is the RED asymmetry.</b> Read on its own this tooth adds
        /// nothing GA-T2 does not already say. Read as a PAIR with GA-T2 it is the in-suite proof that
        /// GA-T2's off-midpoint anchoring is load-bearing rather than decorative:</para>
        /// <list type="bullet">
        /// <item>DROP the subtraction (<c>x̂ = normalize(tangent)</c>) ⇒ GA-T2 reds, <b>GA-T3 stays
        /// GREEN</b> — at the midpoint the in-surface x̂ and the raw tangent are the same vector.</item>
        /// <item>SWAP the operands (<c>normalize(up − tangent·axial)</c>) ⇒ <b>both</b> red, because at
        /// <c>axial = 0</c> that expression collapses to <c>up</c>, which is not the tangent.</item>
        /// </list>
        /// <para>That asymmetry is a property a reviewer can re-measure, and it is the reason a globe fixture
        /// built the obvious way — at the arc midpoint, as every other curved fixture in this repo is — would
        /// have looked exactly like coverage while observing nothing (F-W3-3, still open for the shader).</para>
        /// </summary>
        [Test]
        public void MapPitched_SphericalArcAtTheChordMidpoint_IsBlindToTheOrthogonalisation()
        {
            const double midpointT = 0.5;
            SymbolBox box = StageGlobeProjectedBox(midpointT, featureIndex: 63, toothId: "GA-T3");

            GlobeExpectedBox(GlobeSampledUpAngle(midpointT), out double2 expectedMin, out double2 expectedMax);
            GlobeExpectedBox(GlobeSegmentRad * 0.5, out double2 rawTangentMin, out double2 rawTangentMax);

            double inertness = math.max(
                math.abs(expectedMin.y - rawTangentMin.y), math.abs(expectedMax.y - rawTangentMax.y));

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "GA-T3  box      x=[{0:F4}, {1:F4}]  y=[{2:F4}, {3:F4}]\n" +
                "GA-T3  expected x=[{4:F4}, {5:F4}]  y=[{6:F4}, {7:F4}]\n" +
                "GA-T3  in-surface x̂ vs raw chord tangent differ by {8:E3} px here — the midpoint is inert",
                box.Min.x, box.Max.x, box.Min.y, box.Max.y,
                expectedMin.x, expectedMax.x, expectedMin.y, expectedMax.y, inertness));

            Assert.That(inertness, Is.LessThan(1e-6),
                $"GA-T3 premise: at the chord midpoint the in-surface frame and the raw chord tangent must be " +
                $"indistinguishable ({inertness:E3} px apart). If they are not, this arm is no longer the " +
                "control GA-T2's doc claims it is and the stated RED asymmetry does not hold.");

            Assert.That(box.Min.y, Is.EqualTo(expectedMin.y).Within(0.05),
                $"GA-T3: LOWER edge expected {expectedMin.y:F4}, read {box.Min.y:F4}.");
            Assert.That(box.Max.y, Is.EqualTo(expectedMax.y).Within(0.05),
                $"GA-T3: UPPER edge expected {expectedMax.y:F4}, read {box.Max.y:F4}.");
            Assert.That(box.Min.x, Is.EqualTo(expectedMin.x).Within(0.05),
                $"GA-T3: LEFT edge expected {expectedMin.x:F4}, read {box.Min.x:F4}.");
            Assert.That(box.Max.x, Is.EqualTo(expectedMax.x).Within(0.05),
                $"GA-T3: RIGHT edge expected {expectedMax.x:F4}, read {box.Max.x:F4}.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ShapedSymbolBlittabilityTests — ShapedSymbol must live in a NativeArray
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UMR-87: <see cref="ShapedSymbol"/> must live in a <see cref="NativeArray{T}"/> — the whole point of
    /// interning its <c>Text</c>/<c>IconImage</c> strings into <c>TextId</c>/<c>IconImageId</c> ints. Before
    /// that change this struct held two managed <c>string</c> fields, so <c>NativeArray&lt;ShapedSymbol&gt;</c>
    /// construction threw at runtime (Collections' safety checks reject a non-blittable T) — the failure this
    /// tooth pins as fixed.
    ///
    /// <para><b>NOT <c>UnsafeUtility.IsBlittable&lt;T&gt;()</c>.</b> That API answers a STRICTER, CLR-marshaling
    /// question — it returns <c>false</c> for any struct containing a <c>bool</c> field, which
    /// <see cref="ShapedSymbol"/> has four of (<c>AllowOverlap</c>/<c>IgnorePlacement</c>/<c>KeepUpright</c>/
    /// <c>PairOptional</c>) and always did, even before UMR-87. <see cref="IsBlittable_IsTheWrongPredicate_PointStageInputAlsoReadsFalse"/>
    /// below RUNS that API against <c>PointStageInput</c> — already a <c>NativeArray</c> element in production,
    /// also with <c>bool</c> fields — to prove it reads the SAME false there, so it is the wrong predicate for
    /// "can this live in a NativeArray", not a regression this tooth should chase.</para>
    /// </summary>
    [TestFixture]
    public class ShapedSymbolBlittabilityTests
    {
        [Test]
        public void ShapedSymbol_LivesInANativeArray()
        {
            // Not `using var` — CS1654 forbids an indexed WRITE through a using-variable;
            // Dispose explicitly instead.
            var array = new NativeArray<ShapedSymbol>(1, Allocator.Temp);
            try
            {
                array[0] = new ShapedSymbol { TextId = 7, IconImageId = 3, FeatureIndex = 1 };
                Assert.AreEqual(7, array[0].TextId);
                Assert.AreEqual(3, array[0].IconImageId);
            }
            finally { array.Dispose(); }
        }

        /// <summary>The control for the type doc's claim: <c>PointStageInput</c> is an EXISTING, already-shipped
        /// <c>NativeArray</c> element (<c>SymbolTileBlock.Points</c>) with <c>bool</c> fields of its own, so if
        /// <c>IsBlittable</c> also reads false for it, the API — not <see cref="ShapedSymbol"/> — is what
        /// disagrees with reality.</summary>
        [Test]
        public void IsBlittable_IsTheWrongPredicate_PointStageInputAlsoReadsFalse()
        {
            Assert.IsFalse(UnsafeUtility.IsBlittable<PointStageInput>(),
                "control: a bool-bearing struct ALREADY living in NativeArray<PointStageInput> in production " +
                "still reads false here — confirms IsBlittable is not the right predicate for ShapedSymbol either");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // StyledSymbolTileBuilderTests — a parsed symbol layer produces the expected shaped symbols
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S105 Slice 3 (A4) — THE decisive test: a parsed symbol layer + the real fixture tile, run through
    /// <see cref="StyledSymbolTileBuilder"/> produces the expected set of
    /// shaped <see cref="ShapedSymbol"/>s. Real style + real tile → correct symbols, no synthetic stand-in.
    /// </summary>
    [TestFixture]
    public class StyledSymbolTileBuilderTests
    {
        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, Path.Combine(relative));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("fixture not found: " + Path.Combine(relative));
        }

        private const string FontName = "LatinFont";
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        // 4.4c: Shape/BuildAsync now write a SymbolTileBuffer instead of a per-symbol managed carrier list —
        // this helper reads back one symbol's quad span (the buffer analogue of `symbol.Layout.Quads`).
        private static List<SymbolQuad> QuadsOf(SymbolTileBuffer buffer, int i)
        {
            ShapedSymbol symbol = buffer.Symbols[i];
            return buffer.Quads.GetRange(symbol.QuadStart, symbol.QuadCount);
        }

        private static GlyphManager BuildGlyphManager()
        {
            byte[] latin = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = latin };
            return new GlyphManager(TestGlyphSource.FromRanges(ranges));
        }

        private static SymbolStyle.StyleLayer CentroidsLayer(string extraLayoutJson = "")
            => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\",\"text-size\":16,\"text-font\":[\"" + FontName + "\"]" + extraLayoutJson + "}"),
            };

        [Test]
        public async Task Build_CentroidsLayer_ShapesRealSymbols_NoSyntheticSource()
        {
            using MvtTile tile = MvtDecoder.Decode(FixtureTile, LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var projection = new WebMercatorProjection();
            SymbolStyle.StyleLayer layer = CentroidsLayer();

            // Independent extractor pass (Slice 2) gives the ground-truth text/anchor/ordinal per symbol.
            var extracted = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, FixtureTile, 0.0, projection, extracted);

            using var manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var buffer = new SymbolTileBuffer();
            await builder.BuildAsync(tile, FixtureTile, new[] { layer }, 0.0, projection, buffer);

            // (a) one ShapedSymbol per extracted symbol, in the same order (FeatureIndex tiebreak preserved).
            Assert.AreEqual(extracted.Count, buffer.Symbols.Count, "one shaped symbol per extracted point label");
            Assert.AreEqual(248, buffer.Symbols.Count, "fixture pin: 248 centroids resolve a non-empty NAME");
            Assert.AreEqual(0, builder.SkippedSymbolCount, "a clean all-LTR build skips nothing (happy-path no-op)");

            for (int i = 0; i < buffer.Symbols.Count; i++)
            {
                ShapedSymbol symbol = buffer.Symbols[i];
                Assert.AreEqual(extracted[i].AnchorRender, symbol.AnchorRender, $"anchor preserved at {i}");
                Assert.AreEqual(extracted[i].FeatureIndex, symbol.FeatureIndex, $"ordinal preserved at {i}");
                Assert.AreEqual(extracted[i].TileKey, symbol.TileKey, $"tile key preserved at {i}");
                Assert.AreEqual(16f, symbol.TextSizePx, 1e-6, $"text-size 16 at {i}");
                Assert.AreEqual(2f, symbol.PaddingPx, 1e-6, $"text-padding default 2 at {i}");

                // Every pure-ASCII, space-free name shapes to exactly one glyph quad per character (all
                // present in the Latin fixture) — a strong tooth that shaping is REAL, not stubbed empty.
                string t = extracted[i].Text;
                if (IsAsciiNoSpace(t))
                    Assert.AreEqual(t.Length, symbol.QuadCount,
                        $"'{t}' must shape to {t.Length} glyph quads");
            }

            // (b) The specific named feature — Aruba — is the first, shaped to 5 glyphs, at its A3 anchor.
            Assert.AreEqual("Aruba", extracted[0].Text, "feature[0] is Aruba");
            Assert.AreEqual(5, buffer.Symbols[0].QuadCount, "Aruba → 5 glyph quads");

            // (c) At least two OTHER named symbols match (so a single hard-coded symbol cannot pass).
            AssertNamedSymbol(extracted, buffer, "Afghanistan", 11);
            AssertNamedSymbol(extracted, buffer, "Angola", 6);
        }

        // ── Slice A: the layout-options wiring is LIVE through the builder (guards StyledSymbolTileBuilder's
        //    TextLayoutOptions.Default -> s.LayoutOptions switch — NOT just the Extract/TextQuadLayout seams,
        //    which the engine tests already cover and which stay green even if line 105 is reverted). ──

        private static async Task<SymbolTileBuffer> BuildSymbols(SymbolStyle.StyleLayer layer, GlyphManager manager)
        {
            using MvtTile tile = MvtDecoder.Decode(FixtureTile, LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var builder = new StyledSymbolTileBuilder(manager);
            var buffer = new SymbolTileBuffer();
            await builder.BuildAsync(tile, FixtureTile, new[] { layer }, 0.0, new WebMercatorProjection(), buffer);
            return buffer;
        }

        [Test]
        public async Task Build_TextOffset_ShiftsEveryQuad_ByEmsTimes24_YDownFlippedToYUp()
        {
            using var manager = BuildGlyphManager();

            SymbolTileBuffer baseline = await BuildSymbols(CentroidsLayer(), manager);
            // text-offset [1,2] ems in MapLibre's y-DOWN convention.
            SymbolTileBuffer shifted = await BuildSymbols(CentroidsLayer(",\"text-offset\":[1,2]"), manager);

            Assert.AreEqual(baseline.Symbols.Count, shifted.Symbols.Count, "same label set");
            Assert.Greater(baseline.Symbols.Count, 0, "sanity: fixture yields labels");

            // Center anchor in both (default), so the anchor term cancels and the per-quad delta isolates the
            // offset. ems -> baked px is x24; the y is NEGATED (y-down text-offset -> y-up layout). So every
            // quad shifts by exactly (1*24, -2*24) = (24, -48). A revert of line 105 to Default makes the
            // "shifted" build ignore text-offset -> delta 0 -> this fails. It also pins the y-flip sign.
            var expected = new float2(24f, -48f);
            List<SymbolQuad> baseQuads = QuadsOf(baseline, 0);   // Aruba
            List<SymbolQuad> shiftQuads = QuadsOf(shifted, 0);
            Assert.AreEqual(baseQuads.Count, shiftQuads.Count);
            Assert.Greater(baseQuads.Count, 0, "Aruba must shape to >0 quads");
            for (int i = 0; i < baseQuads.Count; i++)
            {
                Assert.AreEqual(expected.x, shiftQuads[i].TopLeft.x - baseQuads[i].TopLeft.x, 1e-3f, $"quad {i} TopLeft.x");
                Assert.AreEqual(expected.y, shiftQuads[i].TopLeft.y - baseQuads[i].TopLeft.y, 1e-3f, $"quad {i} TopLeft.y");
                Assert.AreEqual(expected.x, shiftQuads[i].BottomRight.x - baseQuads[i].BottomRight.x, 1e-3f, $"quad {i} BottomRight.x");
                Assert.AreEqual(expected.y, shiftQuads[i].BottomRight.y - baseQuads[i].BottomRight.y, 1e-3f, $"quad {i} BottomRight.y");
            }
        }

        [Test]
        public async Task Build_TextAnchor_TranslatesBlock_ThroughTheBuilder()
        {
            using var manager = BuildGlyphManager();

            // justify held constant (center) across both so the per-line justify term cancels and the delta
            // isolates the pure anchor translation. Left anchor (hAlign=0) vs Center (hAlign=0.5) pushes the
            // block +x by 0.5*blockWidth, with no vertical change (both vAlign=0.5).
            SymbolTileBuffer center = await BuildSymbols(CentroidsLayer(",\"text-justify\":\"center\""), manager);
            SymbolTileBuffer left = await BuildSymbols(CentroidsLayer(",\"text-anchor\":\"left\",\"text-justify\":\"center\""), manager);

            List<SymbolQuad> centerQuads = QuadsOf(center, 0);   // Aruba, single line
            List<SymbolQuad> leftQuads = QuadsOf(left, 0);
            Assert.AreEqual(centerQuads.Count, leftQuads.Count);
            Assert.Greater(centerQuads.Count, 0);

            float dx0 = leftQuads[0].TopLeft.x - centerQuads[0].TopLeft.x;
            Assert.Greater(dx0, 0f, "a Left anchor must push the block +x vs Center (anchor is threaded, not dropped)");
            for (int i = 0; i < centerQuads.Count; i++)
            {
                // block-wide translation: same dx for every quad, and no vertical move.
                Assert.AreEqual(dx0, leftQuads[i].TopLeft.x - centerQuads[i].TopLeft.x, 1e-3f, $"quad {i} dx constant");
                Assert.AreEqual(0f, leftQuads[i].TopLeft.y - centerQuads[i].TopLeft.y, 1e-3f, $"quad {i} no vertical move");
            }
        }

        // ── Per-symbol build isolation: one symbol whose build throws (e.g. S18's deferred mixed-direction
        //    bidi NotSupportedException) must be SKIPPED, never abort the whole tile's symbols. ──

        private static SymbolStyle.SymbolFeature PointSymbol(string text) => new SymbolStyle.SymbolFeature
        {
            Text = text,
            Placement = SymbolPlacement.Point,
            LayoutOptions = TextLayoutOptions.Default,
            TextSizePx = 16f,
        };

        [Test]
        public void Shape_MixedDirectionSymbol_IsSkipped_OtherSymbolsSurvive()
        {
            using var manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);

            // Two plain-LTR symbols straddling one MIXED strong-direction symbol: Latin 'A' (U+0041, strong LTR)
            // + Arabic beh (U+0628, strong RTL) — which CodepointTextShaper rejects (single-run bidi, decision 8).
            // The Arabic range is absent from the Latin fixture ⇒ cached empty in Pass 1 (no throw); the throw
            // lands in Pass 2's shaper exactly as in production.
            var symbols = new List<SymbolStyle.SymbolFeature>
            {
                PointSymbol("Aruba"),
                PointSymbol("Aب"),
                PointSymbol("Angola"),
            };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { FontName } }, symbols);

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            // The whole tile is NOT aborted: the two LTR symbols build; only the mixed one is skipped.
            Assert.AreEqual(2, output.Symbols.Count, "the two LTR labels survive; the mixed label is skipped");
            Assert.AreEqual(builder.StringTable.Intern("Aruba"), output.Symbols[0].TextId);
            Assert.AreEqual(builder.StringTable.Intern("Angola"), output.Symbols[1].TextId);
            Assert.AreEqual(1, builder.SkippedSymbolCount, "exactly one label skipped");
            Assert.IsNotNull(builder.LastSkipReason, "skip reason recorded for the throttled diagnostic");
            StringAssert.Contains("NotSupportedException", builder.LastSkipReason);
        }

        [Test]
        public void EnsureGlyphRanges_Cancelled_PropagatesCancellation()
        {
            // A glyph source that OBSERVES the token (FromRanges discards it), so the ensure step's await
            // surfaces the cancel. Pins that a cancelled ensure propagates an OperationCanceledException.
            // (Shape never running as a consequence is pinned by T1/T5c, not here — this body never calls
            // Shape, so an assertion about its output would be true under any implementation.)
            var source = new TestGlyphSource((fontStack, rangeStart, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return UniTask.FromResult(GlyphRangeResponse.Absent());
            });
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            var symbols = new List<SymbolStyle.SymbolFeature> { PointSymbol("Aruba") };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { FontName } }, symbols);
            var extractedLayers = new List<StyledSymbolTileBuilder.ExtractedLayer> { layer };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ranges = new List<(string FontName, int RangeStart)>();
            var seen = new HashSet<(string FontName, int RangeStart)>();
            builder.CollectRequiredRanges(extractedLayers, ranges, seen);

            // CatchAsync (not ThrowsAsync) so the assertion accepts any OperationCanceledException SUBTYPE: the
            // Unity/Mono UniTask path surfaces cancellation as TaskCanceledException (an OCE subclass), the
            // dotnet path as a plain OperationCanceledException. The production filter uses `ex is OCE`, so it
            // correctly excludes both from the per-symbol skip — the test must be equally subtype-tolerant.
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await builder.EnsureGlyphRangesAsync(ranges, cts.Token));
        }

        // ── I5a: icon symbols ride the same Shape loop as text, but must never touch the
        //    shaper/resolver/glyph-fetch machinery (an icon-only layer may carry no text-font at all). ──

        private static SymbolStyle.SymbolFeature Icon(in SymbolQuad iconQuad) => new SymbolStyle.SymbolFeature
        {
            Kind = SymbolKind.Icon,
            IconQuad = iconQuad,
            Placement = SymbolPlacement.Point,
            AnchorRender = default,
            PaddingPx = 3f,
            SortKey = 0f,
        };

        private static readonly SymbolQuad SampleIconQuad = new SymbolQuad
        {
            TopLeft = new float2(-8, 8), BottomRight = new float2(8, -8),
            UvTopLeft = new float2(0.1f, 0.2f), UvBottomRight = new float2(0.3f, 0.4f),
        };

        [Test]
        public void Shape_IconOnlyLayer_YieldsOneIcon_NoGlyphFetch_NoShaping()
        {
            // A glyph source that THROWS if ever asked — an icon-only layer must never reach Pass 1's fetch.
            var source = new TestGlyphSource((fontStack, rangeStart, ct) =>
                throw new InvalidOperationException("icon-only layer must never request a glyph range"));
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            var symbols = new List<SymbolStyle.SymbolFeature> { Icon(SampleIconQuad) };
            // No text-font at all — FontStack.Names left default/empty, mirroring an icon-only style layer.
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(0, new FontStack(), symbols);

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            Assert.AreEqual(1, output.Symbols.Count, "the icon label must still be emitted");
            Assert.AreEqual(0, builder.SkippedSymbolCount, "an icon build must never be skipped");
            ShapedSymbol symbol = output.Symbols[0];
            Assert.AreEqual(SymbolKind.Icon, symbol.Kind);
            Assert.AreEqual(1, symbol.QuadCount, "a sprite is exactly one quad");
            Assert.AreEqual(TextQuadLayout.OneEm, symbol.TextSizePx, 1e-6, "icon scale must be 1 (OneEm/OneEm)");
            Assert.AreEqual(0, symbol.TextId, "an icon label carries no text id");
        }

        [Test]
        public void Shape_MixedTextAndIconLayer_YieldsBothKinds_InOriginalOrder()
        {
            using var manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);

            var symbols = new List<SymbolStyle.SymbolFeature>
            {
                PointSymbol("Aruba"),
                Icon(SampleIconQuad),
                PointSymbol("Angola"),
            };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { FontName } }, symbols);

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            Assert.AreEqual(3, output.Symbols.Count, "text + icon + text, all three survive");
            Assert.AreEqual(0, builder.SkippedSymbolCount);
            Assert.AreEqual(SymbolKind.Text, output.Symbols[0].Kind); Assert.AreEqual(builder.StringTable.Intern("Aruba"), output.Symbols[0].TextId);
            Assert.AreEqual(SymbolKind.Icon, output.Symbols[1].Kind); Assert.AreEqual(0, output.Symbols[1].TextId);
            Assert.AreEqual(SymbolKind.Text, output.Symbols[2].Kind); Assert.AreEqual(builder.StringTable.Intern("Angola"), output.Symbols[2].TextId);
        }

        // ── A3 (P-B): a MAP-aligned LINE icon must build as a ONE-GLYPH CURVED instance, not a point one.
        //    The point-icon branch above stays byte-identical (its own tooth is the pair above). ──
        [Test]
        public void Shape_AlongLineIcon_BuildsOneGlyphCurvedInstance_NotAPointInstance()
        {
            var source = new TestGlyphSource((fontStack, rangeStart, ct) =>
                throw new InvalidOperationException("an icon-only layer must never request a glyph range"));
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            var pathRender = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            var alongLine = new SymbolStyle.SymbolFeature
            {
                Kind = SymbolKind.Icon,
                Placement = SymbolPlacement.Line,
                IconQuad = SampleIconQuad,
                PathRender = pathRender,
                LineAnchors = anchors,
                IconImage = "arrow",
                PaddingPx = 3f,
                SortKey = 1.5f,
                MaxAngleDeg = 45f,
                KeepUpright = false,
                IconRotateRadians = math.PI,
                FeatureIndex = 7,
                TileKey = 42L,
            };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(0, new FontStack(),
                new List<SymbolStyle.SymbolFeature> { alongLine });

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            Assert.AreEqual(1, output.Symbols.Count);
            ShapedSymbol built = output.Symbols[0];
            // A point-shaped build (the pre-P-B behaviour) would leave GlyphCount 0 and Placement Point.
            Assert.AreEqual(SymbolPlacement.Line, built.Placement, "an along-line icon keeps LINE placement");
            Assert.AreEqual(1, built.GlyphCount, "exactly ONE glyph — the icon quad IS the whole run");
            Assert.AreEqual(0, built.QuadCount, "a curved instance carries no point quads");
            Assert.AreEqual(SymbolKind.Icon, built.Kind, "still an icon (routes to the sprite atlas)");
            CurvedGlyph glyph = output.Glyphs[built.GlyphStart];
            Assert.AreEqual(0f, glyph.ArcCenter, 1e-6f, "a lone cell sits at arc 0");

            // Field-for-field: the cell IS the extractor's icon quad, unmodified.
            SymbolQuad cell = glyph.Cell;
            Assert.AreEqual(SampleIconQuad.TopLeft, cell.TopLeft, "cell TopLeft == the icon quad's");
            Assert.AreEqual(SampleIconQuad.BottomRight, cell.BottomRight, "cell BottomRight == the icon quad's");
            Assert.AreEqual(SampleIconQuad.UvTopLeft, cell.UvTopLeft, "cell UvTopLeft == the icon quad's");
            Assert.AreEqual(SampleIconQuad.UvBottomRight, cell.UvBottomRight, "cell UvBottomRight == the icon quad's");

            Assert.AreEqual(TextQuadLayout.OneEm, built.TextSizePx, 1e-6f,
                "scale 1 — IconQuadLayout already baked icon-size in (matches the point-icon branch)");
            // 4.4c: AppendPath/AppendAnchors COPY into the buffer's own pools (never hold the caller's array
            // reference), so "carried, not rebuilt" is now a VALUE check — still proves the values are copied
            // verbatim, not recomputed from scratch by some other path.
            CollectionAssert.AreEqual(pathRender, output.Path.GetRange(built.PathStart, built.PathCount),
                "the projected path is carried, not rebuilt");
            CollectionAssert.AreEqual(anchors, output.Anchors.GetRange(built.AnchorStart, built.AnchorCount),
                "the build-time anchors are carried, not recomputed");
            Assert.IsFalse(built.KeepUpright, "icon-keep-upright's spec default is false");
            Assert.AreEqual(builder.StringTable.Intern("arrow"), built.IconImageId);
            Assert.AreEqual(math.PI, built.IconRotateRadians, 1e-6f, "icon-rotate is carried onto the curved instance");
            Assert.AreEqual(7, built.FeatureIndex);
            Assert.AreEqual(42L, built.TileKey);
        }

        // ── 4.4c pairing-adjacency tooth: every layer processor of ONE build must write into the SAME
        //    SymbolTileBuffer, or SymbolPairing's owner-at-i+1 resolution breaks.
        //    TileSymbolLayerProcessor.CompleteOnMain calls Shape ONCE PER LAYER — this
        //    reproduces that shape directly: two Shape calls sharing one buffer, an owner tailing the
        //    FIRST call and its rider heading the SECOND. RED-verify: give the second call its OWN fresh
        //    buffer instead (the violation) — Bake would then see the owner alone (PairRoles[0] dissolves
        //    to None, its rider never in the same block) rather than a resolved pair. ──
        [Test]
        public void Shape_TwoCallsShareOneBuffer_OwnerLastOfFirstCall_RiderFirstOfSecondCall_ResolveAsPair()
        {
            using var manager = new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));
            var builder = new StyledSymbolTileBuilder(manager);

            const int materialIndex = 3; // shared — SymbolPairing's ShapedSymbol overload also requires this to match
            const long tileKey = 99L;
            const int pairId = 7;

            var ownerSymbol = new SymbolStyle.SymbolFeature
            {
                Kind = SymbolKind.Icon, IconQuad = SampleIconQuad, Placement = SymbolPlacement.Point,
                AnchorRender = default, PaddingPx = 3f, SortKey = 0f, TileKey = tileKey,
                PairRole = SymbolPairRole.Owner, PairId = pairId,
            };
            var riderSymbol = new SymbolStyle.SymbolFeature
            {
                Kind = SymbolKind.Icon, IconQuad = SampleIconQuad, Placement = SymbolPlacement.Point,
                AnchorRender = default, PaddingPx = 3f, SortKey = 0f, TileKey = tileKey,
                PairRole = SymbolPairRole.Rider, PairId = pairId,
            };
            var layer1 = new StyledSymbolTileBuilder.ExtractedLayer(
                materialIndex, new FontStack(), new List<SymbolStyle.SymbolFeature> { ownerSymbol });
            var layer2 = new StyledSymbolTileBuilder.ExtractedLayer(
                materialIndex, new FontStack(), new List<SymbolStyle.SymbolFeature> { riderSymbol });

            var buffer = new SymbolTileBuffer();
            // "processor 1" and "processor 2" — mirrors TileSymbolLayerProcessor's one-Shape-call-per-layer
            // shape, both fed the SAME shared buffer (the rule TileSymbolLayerProcessor/TryBeginBuild wire up).
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer1 }, buffer);
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer2 }, buffer);

            Assert.AreEqual(2, buffer.Symbols.Count, "both calls must land in the SAME buffer");

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, slotCount: 1, double3.zero);
            try
            {
                Assert.AreEqual(SymbolPairRole.Owner, block.PairRoles[0], "cross-call adjacency: owner resolves");
                Assert.AreEqual(SymbolPairRole.Rider, block.PairRoles[1], "cross-call adjacency: rider resolves");
            }
            finally { block.Dispose(); }
        }

        private static void AssertNamedSymbol(List<SymbolStyle.SymbolFeature> extracted, SymbolTileBuffer buffer,
            string name, int expectedQuads)
        {
            int idx = extracted.FindIndex(e => e.Text == name);
            Assert.Greater(idx, -1, $"fixture must contain '{name}'");
            Assert.AreEqual(expectedQuads, buffer.Symbols[idx].QuadCount, $"'{name}' → {expectedQuads} glyph quads");
        }

        private static bool IsAsciiNoSpace(string s)
        {
            foreach (char c in s)
                if (c <= 32 || c > 126) return false;
            return true;
        }
    }
}
