// Text/Placement/MapPitchedWorldArcStagingTests.cs — collision-grid/job placement, cross-tile store, horizon cull, icon-skirt carrier chain, curved world-arc staging under a pitched camera, and the shaped-symbol blittability/tile-builder teeth.
//
// No single production area dominates; kept in the order the topic-and-lane pack assembled them, each fixture independent of its neighbours.
//
// Contents:
//   CollisionGridContractTests      — Unit-level contract teeth for the collision grid, kept independently of the placement tests that also exercise it (see the note at SymbolPlacementSystem.cs:732).
//   CollisionJobPlacementTests      — CollisionJob's placement teeth: named/permutation-invariant/sort-key-driven survivor sets over hand-built scenes, plus AssertGreedyContract exercised over adversarial random scenes (wide boxes, the MaxGridDim cell-enlargement path, dense clusters, and…
//   CrossTileIdentityStoreTests     — cross-tile point-symbol identity — SymbolTileStore's dedup (the store-level half of CrossTileIdentityTests — see that file's header).
//   HorizonCullGatherTests          — the globe far-side horizon cull as a GatherSymbolPoints fade trigger (peer of the tile/ distance/departing culls) — a two-sided EditMode proof over a REAL SphericalProjection MapCamera.
//   IconSkirtCarrierChainTests      — The icon skirt's CARRIER CHAIN, driven end to end from a genuinely padded SpriteAtlasView: SymbolFeatureExtractor (computes IconQuadLayout.SkirtPx) → SymbolFeature.IconSkirtPx → StyledSymbolTileBuilder → the point symbol's Layout.Bounds* and the along-line…
//   MapPitchedWorldArcStagingTests  — curved (along-line) symbol staging under a genuinely pitched camera and, for GLOBE-A, a non-zero-axial on-sphere arc — catches a metres-vs-pixels sign confusion a straight path is blind to.
//   ShapedSymbolBlittabilityTests   — ShapedSymbol must live in a NativeArray{T} — the whole point of interning its Text/IconImage strings into TextId/IconImageId ints.
//   StyledSymbolTileBuilderTests    — THE decisive test: a parsed symbol layer + the real fixture tile, run through StyledSymbolTileBuilder produces the expected set of shaped ShapedSymbols.

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
        // Non-obvious why: the node pool is sized on the main thread but filled in Burst, where a boundary
        // coordinate can truncate one cell wider, and a Burst job cannot grow a NativeArray. So CollisionJob.Insert
        // guards every write against NodeBox.Length; a starved pool must COMPLETE, not throw. Disjoint boxes
        // keep the survivors correct.
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

        // The node-storage bound carries a ±1-cell margin for Mono/Burst truncation drift, so it must exceed
        // the exact per-box cell count for multi-cell boxes.
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

        // Non-obvious why: candidate box ranges can overlap, and the job inserts a SHARED box once PER candidate.
        // A per-unique-box bound (NodeUpperBound) under-counts even with its margin; NodeUpperBoundByCandidates
        // counts per reference, as the job does.
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

        // An under-sized node pool silently drops blocker inserts, so wrong survivors follow with no crash.
        // CellHead/NodeNext are public on CollisionJob, so a test counts linked nodes after Complete().
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

                // MaxGridDim (512) caps CELL SIZE: invCell = 512/span on the max axis, so W/H can reach 513,
                // span-dependently; a <= 512 bound would be flaky.
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

        // Runs CollisionJob with a pool of exactly `poolLength`; returns survivor flags and the linked node
        // count, walked with a pool-length bound so a corrupt chain fails instead of looping.
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
    /// clusters, and the per-box optional mask). Every named-set assertion is OUTCOME-based (WHICH
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

        // Three overlapping boxes over [0,20]² plus one DISJOINT box: order L1(10), L2(20), L0(30), L3(99),
        // so L1 wins, L2/L0 drop, L3 is alone. Survivors = {1, 3}.
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

        // Boxes come from the REAL SymbolBox.Build math (anchor + bounds*scale +/- padding), so this tests
        // the padding plumbing, not a hand-inflated AABB.
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

        // ── Adversarial scenarios, checked by NativeCollisionRunner.AssertGreedyContract over the job's
        //    own output, never a second greedy pass. ──────────────────────

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

        // Multi-box candidates among point candidates (all-or-nothing ranges); the hand-built scene also
        // pins the NAMED survivor set.
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
            // Order 0, 2, 1, 3, 4: 0 and 2 place; 1 hits 0's SECOND glyph and 3 hits 2's FIRST glyph, so both
            // drop; 4 is far away and places.
            CollectionAssert.AreEquivalent(new[] { 0, 2, 4 }, survivors,
                "the two curved symbols place (disjoint regions); the two points overlapping their glyphs are dropped; the far point always places");
        }

        // Overlapping two-box PAIR candidates with random OptionalBoxMask among single-box candidates;
        // AssertGreedyContract checks DroppedBoxMask. Inserting one half before testing the other drops every pair.
        private static (SymbolCandidate[], SymbolBox[]) RandomMaskedPairScene(int pairCount, int singleCount, int seed)
        {
            var rng = new System.Random(seed);
            var boxes = new List<SymbolBox>();
            var cands = new List<SymbolCandidate>();
            int symbol = 0;

            // A SUPPRESSED candidate, never placed, never a blocker; its box overlaps the -1f singleton below,
            // so inserting a suppressed candidate's boxes would drop that singleton.
            boxes.Add(new SymbolBox { Min = new float2(1035, 995), Max = new float2(1065, 1005),
                SortKey = -2f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol });
            cands.Add(new SymbolCandidate
            {
                BoxStart = boxes.Count - 1, BoxCount = 1, EmitStart = boxes.Count - 1, EmitCount = 1,
                SortKey = -2f, FeatureIndex = symbol, TileKey = 2, SymbolIndex = symbol,
                Suppressed = true,
            });
            symbol++;

            // A DETERMINISTIC rider-optional pair whose rider box a higher-priority single covers, so at least
            // one half drops for every (count, seed).
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
                // test-all-then-insert load-bearing. The world is kept small so these actually contend.
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
    /// Cross-tile point-symbol identity: <see cref="SymbolTileStore"/>'s dedup keys on the fixed
    /// <see cref="CrossTileSymbolKey.CanonicalGridMeters"/> (4 m) and ignores the gate <c>q</c>'s magnitude. A
    /// symbol in a parent and a child tile dedups to ONE (finest wins); different text in one cell does NOT
    /// merge; line symbols are not deduped.
    /// </summary>
    [TestFixture]
    public class CrossTileIdentityStoreTests
    {
        // A leaked SymbolTileBlock keeps DebugLiveAllocCount raised: only Dispose decrements it, not a
        // finalizer, so the delta does not depend on GC timing.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        // Appends one point symbol into `buffer` (the TestSymbolTileBuffer idiom) and returns the record, so
        // a caller can also hold it for a later assertion.
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
            const double q = 50.0; // The store IGNORES this magnitude — it grids on the fixed 4 m; q only gates dedup ON.
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
            // ShapedSymbol is a struct, so TileKey identifies the winner: it is the only field that differs
            // between the parent and child copies.
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

        // ── CrossTileSymbolKey.For (string) and DedupKey.For (int, the fade-id identity) grid an anchor to the
        //    IDENTICAL (GridX, GridZ, GridY): both call QuantizeAnchor, and this fails if the two ever fork. ──
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
    /// The globe far-side horizon cull as a <c>GatherSymbolPoints</c> fade trigger (peer of the tile/
    /// distance/departing culls), over a REAL <see cref="SphericalProjection"/> <see cref="MapCamera"/>: a
    /// fresh far-side anchor is hard-skipped; a visible anchor rotating behind the horizon EASES OUT; a fixed
    /// off-axis anchor pins the East/North axes. Frames come from <see cref="MapView.BuildSceneFrame"/>, not the
    /// 2-arg ctor, whose camera at the sphere centre would misfire the cull.
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

        // Point symbols draw through the WORLD path: fade opacity rides the world slot's stream-1 Opacity,
        // not system.Mesh's vertex-colour alpha (as SymbolFadeTests.MaxAlpha).
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
                // Point symbols draw through the world path — the demo tick needs its own
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

        /// <summary>A fresh far anchor yields no quad and no candidate, counted in the horizon telemetry bucket.
        /// Limitation: the antipode rebases to <c>(0,−2R,0)</c> for ANY heading, so it cannot pin the
        /// East/North wiring; <see cref="OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes"/> does.</summary>
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
            Assert.AreEqual(1, h.System.LastHorizonCulledCount, "…attributed to the horizon cull");
            Assert.AreEqual(0, h.System.LastDistanceCulledCount, "the B-3 radius must not preempt the horizon trigger here");
        }

        /// <summary>
        /// THE frame-consistency pin: RED under an East↔North swap or a px/pz sign flip between
        /// <c>CameraPoseMath.ComputeRelativePose</c> and <c>Ecef.TangentBasis</c>'s East/North columns.
        /// Non-obvious why: hidden ⟺ cos(H−β) &gt; K, and each defect only shifts the phase, so two headings
        /// 180° apart or β=45° cannot separate them. β=20° over headings {0°, 90°, 150°} reads {1, 1, 0}, and
        /// every defect changes one entry. The signal is <c>LastHorizonCulledCount</c>, not a drawn quad.
        /// </summary>
        [Test]
        public void OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes()
        {
            var lookAt = new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 };
            const double bearingDeg = 20.0, distanceDeg = 50.0; // oblique β so a swap/px-flip is visible (β≠0,45,90)
            GeoCoordinate anchorGeo = Destination(bearingDeg, distanceDeg);

            // The correct pattern is {1,1,0}; tilt=45° makes the off-axis anchor's occlusion depend on heading
            // through px/pz.
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
            // Duplicate — the collision verdict is harvested one Tick late.
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
    /// Only this tooth sees a lost skirt: the others call the two ends directly, and snapshots draw the PADDED quad.
    /// </summary>
    [TestFixture]
    public class IconSkirtCarrierChainTests
    {

        /// <summary>A synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this fixture exists to catch.</summary>
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
        /// Stages one symbol. <paramref name="view"/> defaults to <c>default(SymbolViewTransform)</c> — the
        /// "no camera was supplied" state, which keeps the SCREEN collision box byte-identical, so every
        /// caller above still describes the same code.
        /// <paramref name="worldUpPathOverride"/> likewise defaults to the all-zero up path these fixtures
        /// have always passed (a degenerate ground frame, Projected-T6's subject).
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
        // Arc-T6 — R5a's observing tooth: the screen point of a WORLD parameter is the AFFINE lerp.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pins a recorded limitation: under the world arc walk <c>AtWithSegment</c> maps <c>(seg, t)</c> to the
        /// screen with an AFFINE lerp, not the perspective-correct <c>t' = t·w₁ / ((1−t)·w₀ + t·w₁)</c>.
        /// Limitation: clip w is not recoverable from <c>SymbolProjectionJob.OutDepth</c> (NDC depth), so the
        /// fixture declares w₀ = 300, w₁ = 600. A single segment isolates the mapping; implementing <c>t'</c>
        /// reds this, and the limitation in <c>StageCurved</c>'s doc must then go.
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

            // Non-obvious why: the w's also go into the depth span, so an implementation that wrongly reads
            // depthPath as clip w produces the perspective-correct point and reds, instead of reading zeros.
            const double w0 = 300.0, w1 = 600.0;
            var depthPathCarryingW = new[] { (float)w0, (float)w1 };

            int staged = Stage(in s, screenPath, worldPath, glyphs, anchors, ref p, depthPathCarryingW);
            Assert.That(staged, Is.EqualTo(1), "Arc-T6 precondition: the single-glyph label must stage.");

            // The two candidate answers, both computed HERE from the fixture's own constants.
            double2 a0 = new double2(screenPath[0].x, screenPath[0].y);
            double2 a1 = new double2(screenPath[1].x, screenPath[1].y);
            double2 affine = math.lerp(a0, a1, 0.5);                       // t = 0.5
            // At t = 0.5, t' = w₁/(w₀+w₁) = 2/3. NOT w₀/(w₀+w₁): that inverse (screen→attribute) map points
            // the correction at the camera.
            double tPrime = w1 / (w0 + w1);
            double2 perspectiveCorrect = math.lerp(a0, a1, tPrime);
            double separationPx = math.length(perspectiveCorrect - affine);

            // The DIRECTION, pinned apart from the magnitude, which is symmetric about ½: on a receding segment
            // the world midpoint projects PAST the screen midpoint.
            Assert.That(tPrime, Is.GreaterThan(0.5),
                $"Arc-T6 precondition: on a receding segment (w₀={w0:F0} < w₁={w1:F0}) the perspective-correct " +
                $"parameter must exceed the affine ½ — reads {tPrime:F6}. A value below ½ means the formula " +
                "has been transposed into the inverse screen→attribute map.");

            double2 staged0 = new double2(p.Quads[0].AnchorScreenPx.x, p.Quads[0].AnchorScreenPx.y);
            Assert.That(math.length(staged0 - affine), Is.LessThan(1e-3),
                $"Arc-T6: the staged screen anchor must be the AFFINE lerp at the world parameter — staged " +
                $"({staged0.x:F4}, {staged0.y:F4}) vs affine ({affine.x:F4}, {affine.y:F4}). This is the " +
                "RECORDED R5a limitation; if you implemented the perspective-correct t', delete the " +
                "limitation from StageCurved's doc rather than widening this bound.");
            Assert.That(separationPx, Is.EqualTo(50.0).Within(1e-6),
                $"Arc-T6 precondition: the perspective-correct answer must be a STATED distance away, or this " +
                $"tooth discriminates nothing — t'={tPrime:F6} against 0.5 on a 300 px segment is " +
                $"{separationPx:F6} px (expected 50).");
            Assert.That(math.length(staged0 - perspectiveCorrect), Is.EqualTo(50.0).Within(1e-3),
                $"Arc-T6: the staged anchor must differ from the perspective-correct answer " +
                $"({perspectiveCorrect.x:F4}, {perspectiveCorrect.y:F4}) by the full 50 px — measured " +
                $"{math.length(staged0 - perspectiveCorrect):F4} px.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Arc-T7 — R5b's observing tooth: the max-angle gate stays on the SCREEN tangent.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pins a recorded limitation: a map-pitched symbol whose SCREEN path is kinked past
        /// <c>text-max-angle</c> is DROPPED, though its WORLD path bends only 2°. The same world path with a
        /// STRAIGHT screen path stages, so the drop comes from the screen kink. A world-tangent gate reds this.
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

            // Two glyphs at world arc 500 m (segment 0) and 1500 m (segment 1), either side of the interior
            // vertex, so the gate's g > 0 comparison is BETWEEN the two segments' tangents.
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
                $"Arc-T7: the max-angle gate must read the SCREEN tangent — the 90°-kinked screen path staged " +
                $"{stagedKinked} candidates (expected 0, dropped) and the straight screen path over the SAME " +
                $"world polyline staged {stagedStraight} (expected 1). The world path bends only 2°, so a " +
                "gate moved onto world curvature would keep BOTH. If that move was deliberate, delete R5b " +
                "from StageCurved's doc rather than relaxing this.");
            Assert.That(kinkedPools.QuadCount, Is.EqualTo(0),
                $"Arc-T7: a dropped anchor must roll its partial appends back — {kinkedPools.QuadCount} quads " +
                "were left behind.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Arc-T8 / Arc-T9 — the stage invariant and the degradation guard, as code properties.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// THE STAGE INVARIANT: a symbol whose pitch alignment is not <see cref="AlignmentMode.Map"/> cannot
        /// observe the per-frame ruler — its boxes and quads are BIT-identical with
        /// <c>MetresPerLogicalPixel</c> at 0, 1 and 10⁶. Run for <see cref="AlignmentMode.Viewport"/> and for
        /// <see cref="AlignmentMode.Auto"/>, the zero value every hand-built fixture carries. The
        /// <c>CornerMetresPerLogicalPixel_*</c> teeth watch the same <c>worldArc</c> bool through the emit.
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
                        $"Arc-T8 precondition ({mode}, ruler {rulers[r]}): the reference label must stage.");
                    quadBits[r] = Bits(p.Quads, p.QuadCount);
                    boxBits[r]  = Bits(p.Boxes, p.BoxCount);
                }

                for (int r = 1; r < rulers.Length; r++)
                {
                    AssertBitIdentical(quadBits[0], quadBits[r],
                        $"Arc-T8 ({mode}): PlacedQuads at MetresPerLogicalPixel={rulers[r]} differ from those " +
                        $"at {rulers[0]}");
                    AssertBitIdentical(boxBits[0], boxBits[r],
                        $"Arc-T8 ({mode}): SymbolBoxes at MetresPerLogicalPixel={rulers[r]} differ from those " +
                        $"at {rulers[0]}");
                }
            }
        }

        /// <summary>
        /// The degradation guard: a <see cref="AlignmentMode.Map"/> symbol with no ruler
        /// (<c>MetresPerLogicalPixel == 0</c>) stages BIT-identically to <see cref="AlignmentMode.Viewport"/>,
        /// the screen walk. Without the <c>&gt; 0</c> conjunct <c>arcScale</c> is 0 and every glyph collapses
        /// onto the anchor, silently.
        /// </summary>
        [Test]
        public void MapPitchedSymbol_WithNoRuler_DegradesToTheScreenWalk()
        {
            var mapPools = Pools.New();
            int stagedMap = StageReferenceSymbol(AlignmentMode.Map, 0f, ref mapPools);
            var viewportPools = Pools.New();
            int stagedViewport = StageReferenceSymbol(AlignmentMode.Viewport, 0f, ref viewportPools);

            Assert.That(stagedMap == 1 && stagedViewport == 1, Is.True,
                $"Arc-T9 precondition: both references must stage — map {stagedMap}, viewport {stagedViewport}.");
            // The glyphs must not collapse onto one point, asserted apart from the bit-comparison in case both
            // sides are degenerate.
            float spreadPx = math.length(
                mapPools.Quads[mapPools.QuadCount - 1].AnchorScreenPx - mapPools.Quads[0].AnchorScreenPx);
            Assert.That(spreadPx, Is.GreaterThan(1f),
                $"Arc-T9: an unpatched map-pitched label must still spread its glyphs along the path — first " +
                $"and last anchors are {spreadPx:F6} px apart, i.e. the label collapsed to a point.");
            AssertBitIdentical(Bits(mapPools.Quads, mapPools.QuadCount),
                Bits(viewportPools.Quads, viewportPools.QuadCount),
                "Arc-T9: a map-pitched label with MetresPerLogicalPixel=0 must stage exactly as a viewport one");
            AssertBitIdentical(Bits(mapPools.Boxes, mapPools.BoxCount),
                Bits(viewportPools.Boxes, viewportPools.BoxCount),
                "Arc-T9: same, for the collision boxes");
        }

        /// <summary>The symbol Arc-T8/Arc-T9 compare across rulers: three glyphs on a plain two-vertex path, with the
        /// world span UNRELATED to the screen span (100 m vs 400 px) so that if the world walk
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
        // Arc-T10 — the named trap: the chord probe's half-width is an ARC quantity.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// On a map-pitched BENT path, a glyph straddling the interior vertex rotates to the CHORD across its
        /// footprint, not a raw segment angle. Non-obvious why: <c>halfWidthArc</c> must be in METRES; on the px
        /// scale both probes land in one segment and the chord collapses, which a straight road cannot show.
        /// Segments of 1000 m and 2000 m (equal on screen) make the walks differ; anchor (seg 1, t 0.1), half
        /// width 500 m, so the probes sit at t = 0.7 on segment 0 and t = 0.35 on segment 1.
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
            Assert.That(staged, Is.EqualTo(1), "Arc-T10 precondition: the straddling glyph must stage.");

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
                $"Arc-T10 precondition: the chord answer ({math.degrees(expectedRad):F3}°) must be well clear " +
                "of segment 0's raw angle, or the tooth cannot discriminate.");
            Assert.That(math.abs(expectedRad - segment1Rad), Is.GreaterThan(math.radians(15.0)),
                $"Arc-T10 precondition: the chord answer ({math.degrees(expectedRad):F3}°) must be well clear " +
                "of segment 1's raw angle, or the tooth cannot discriminate.");
            Assert.That(stagedRad, Is.EqualTo(expectedRad).Within(math.radians(0.05)),
                $"Arc-T10: the straddling glyph must rotate to the CHORD across its own world footprint — " +
                $"staged {math.degrees(stagedRad):F4}°, chord {math.degrees(expectedRad):F4}°, segment " +
                $"angles {math.degrees(segment0Rad):F1}° / {math.degrees(segment1Rad):F1}°. A staged value " +
                $"of exactly {math.degrees(segment1Rad):F1}° means the chord probe COLLAPSED — halfWidthArc " +
                "is on the px scale while the walk runs in metres (the named trap).");
        }

        // ── bit-identity comparison ─────────────────────────────────────────────────────────────────────
        // BIT-identity (NaN/−0.0 differ), flattened reflectively, so a new PlacedQuad/SymbolBox field is compared.

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
        // Ruler-T6 — the CORNER UNIT's producer: set exactly when the world walk runs ═════════
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A <see cref="AlignmentMode.Map"/> symbol with a live ruler emits
        /// <c>CandidateEmit.CornerMetresPerLogicalPixel</c> equal to EXACTLY that ruler, the one the arc walk
        /// spaces anchors with. Limitation: rulers above ≈ 1.6 overflow the reference symbol's 100 m road and
        /// <c>StageCurved</c> emits nothing, so the values straddle 1.
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsTheRuler_WhenMapPitchedAndRulerIsLive()
        {
            foreach (float ruler in new[] { 0.5f, 1f, 1.25f })
            {
                var p = Pools.New();
                int staged = StageReferenceSymbol(AlignmentMode.Map, ruler, ref p);
                Assert.That(staged, Is.EqualTo(1),
                    $"Ruler-T6 precondition (ruler {ruler}): the reference label must stage — a 0 means its " +
                    $"world arc span ({60f * ruler:F1} m) outgrew its 100 m road at the spill gate.");
                Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(ruler),
                    $"Ruler-T6: a map-pitched label with MetresPerLogicalPixel = {ruler} must carry exactly " +
                    $"{ruler} as its corner unit, got {p.Emit[0].CornerMetresPerLogicalPixel}. Any other " +
                    "value means the corner scale and the arc scale were derived separately — the state this " +
                    "stage exists to make inexpressible.");
            }
        }

        /// <summary>
        /// <b>Ruler-T6, case 2.</b> Proves: a NON-map-pitched symbol emits <c>0f</c> as its corner unit whatever
        /// the ruler reads — so its <c>Offset</c> stays LOGICAL PIXELS and every existing path is untouched.
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
                        $"Ruler-T6 precondition ({mode}, ruler {ruler}): the reference label must stage.");
                    Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(0f),
                        $"Ruler-T6 ({mode}, ruler {ruler}): a non-map-pitched label must carry 0 as its corner " +
                        $"unit — i.e. logical pixels, the screen behaviour — got " +
                        $"{p.Emit[0].CornerMetresPerLogicalPixel}. A non-zero here would make every viewport " +
                        "label's corners metres and reinterpret the whole screen render.");
                }
        }

        /// <summary>
        /// The two halves degrade TOGETHER: a <see cref="AlignmentMode.Map"/> symbol with no ruler emits
        /// <c>0f</c> as its corner unit too, the corner-side half of
        /// <c>MapPitchedSymbol_WithNoRuler_DegradesToTheScreenWalk</c>. Otherwise spacing would be screen px
        /// while the corners are metres, a silently mixed pair of rulers.
        /// </summary>
        [Test]
        public void CornerMetresPerLogicalPixel_IsZero_WhenMapPitchedButTheRulerIsMissing()
        {
            var p = Pools.New();
            int staged = StageReferenceSymbol(AlignmentMode.Map, 0f, ref p);
            Assert.That(staged, Is.EqualTo(1), "Ruler-T6 precondition: the reference label must stage.");
            Assert.That(p.Emit[0].CornerMetresPerLogicalPixel, Is.EqualTo(0f),
                "Ruler-T6: a map-pitched label with no ruler must degrade its CORNER unit to logical px exactly " +
                "as Arc-T9 shows it degrades its ARC ruler to the screen walk — got " +
                $"{p.Emit[0].CornerMetresPerLogicalPixel}. The two must degrade together; a half-converted " +
                "label is worse than either whole behaviour.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Ruler-T3b — equation (5) past the fixture's cull ceiling, with NO camera
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// For a map-pitched curved symbol, <c>worldAdvance / cellWidth_world</c> equals the typographic
        /// <c>ΔArcCenter / cellWidthBaked</c> through production <see cref="SymbolStagingMath.StageCurved"/> and
        /// <see cref="BillboardMath.BuildWorldQuad"/>. Limitation: CPU half only, not the shader. The arc walk has
        /// no depth input, so the sweep varies distance from the tile origin ALONG the road, the one thing
        /// that can degrade it numerically.
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
                $"Ruler-T3b precondition (magnitude {magnitude}): the label must stage; a 0 means the road is " +
                "too short for its world span at this magnitude, which would make the reading vacuous.");

            float cornerScale = p.Emit[0].CornerMetresPerLogicalPixel;
            Assert.That(cornerScale, Is.GreaterThan(0f),
                "Ruler-T3b precondition: the emit must carry a metre corner unit, or there is no world cell " +
                "width to compare against.");

            // Production BuildWorldQuad with the emit's OWN scale, as WorldSymbolRenderer.Emit composes it.
            // The operands are locals because an `in` parameter cannot bind the init-only CurvedGlyph.Cell property.
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
                    "Ruler-T3b  magnitude={0:F0}  gap {1}  advanceWorld={2:F3} m  cellWidthWorld={3:F3} m  " +
                    "ratio={4:F8}  expected={5:F8}",
                    magnitude, g, advanceWorld, cellWidthWorld, ratio, expected));

                Assert.That(ratio, Is.EqualTo(expected).Within(0.01).Percent,
                    $"Ruler-T3b (magnitude {magnitude}, gap {g}): worldAdvance / cellWidth_world must be the " +
                    $"baked {advanceBaked}/{cellWidthBaked} = {expected:F8} at EVERY magnitude, measured " +
                    $"{ratio:F8}. Both sides are produced by the SAME arcScale, so it cancels identically; a " +
                    "drift here at large magnitude is a float-precision floor, not a model failure — the " +
                    "horizon measurement puts that floor at roughly 50 Earth circumferences of road.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // The projected-world-corner collision box. The view transform is HAND-BUILT (no camera object),
        // so every expected pixel number is arithmetic over the viewport and the field of view.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The projected-box teeth's viewport, logical px. Square, so the projection's aspect ratio is 1.</summary>
        private const double ViewportPx = 512.0;

        /// <summary>Vertical field of view, radians (60°).</summary>
        private const float FovRadians = 1.0471975511965976f;

        /// <summary>The camera-facing surface normal used by most projected-box teeth: with a road along <c>+X̂</c> this
        /// makes <c>x̂ = +X̂</c> and <c>ŷ = cross(x̂, up) = +Ŷ</c>, i.e. the glyph quad lies in the plane
        /// PERPENDICULAR to the view axis, so a corner's projection is a plain uniform scale in BOTH screen
        /// axes and every expectation below is arithmetic instead of a solve.</summary>
        private static readonly float3 CameraFacingUp = new float3(0f, 0f, -1f);

        /// <summary>The ordinary GROUND normal: with a road along <c>+X̂</c> this makes
        /// <c>ŷ = cross(x̂, up) = +Ẑ</c>, i.e. the quad's own y axis runs ALONG the view axis, which is how
        /// Projected-T7 pushes a corner behind the camera without moving the anchor.</summary>
        private static readonly float3 GroundUp = new float3(0f, 1f, 0f);

        /// <summary>Screen px per metre of world offset, per metre of view depth:
        /// <c>½·viewport·cot(fov/2)</c>. A projected offset is <c>this · offsetMetres / depthMetres</c>.
        /// Derived here from the two constants above; never read back out of production.</summary>
        private static readonly double PxPerMetreAtUnitDepth = 0.5 * ViewportPx / math.tan(FovRadians * 0.5);

        /// <summary>
        /// The world→view matrix for a camera at <paramref name="eye"/> looking at <paramref name="target"/>:
        /// right-handed, looking down <b>−Z</b>, as <c>float4x4.PerspectiveFov</c>'s <c>w = −z_view</c> expects.
        /// Non-obvious why: <c>inverse(float4x4.LookAt(...))</c> puts the target at view <b>+Z</b>, so every
        /// corner would get <c>clip.w ≤ 0</c>.
        /// <c>MapPitched_SuppressionFixture_IsSizedSoTheTwoBoxesStraddleTheRoadGap</c> proves this matrix projects.
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
        /// polyline below the TRUE projection of its world polyline, so the screen box these teeth
        /// compare against is the one the shipped code would really have built.</summary>
        private static float2 ProjectPx(double3 world)
            => new float2(
                (float)(0.5 * ViewportPx + PxPerMetreAtUnitDepth * world.x / world.z),
                (float)(0.5 * ViewportPx + PxPerMetreAtUnitDepth * world.y / world.z));

        /// <summary>
        /// The staging fixture: ONE glyph, centred on a straight road that runs along <c>+X̂</c> at view depth
        /// <paramref name="depthM"/> and lateral offset <paramref name="lateralM"/>. The screen polyline is the
        /// TRUE projection of the world polyline (<see cref="ProjectPx"/>), so both the screen box and
        /// the projected box are the boxes the shipped code builds for a real pose of this shape.
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

        // ── the Projected-T4 suppression pair's sizing, all derived, none typed ────────────────────────────────

        /// <summary>`text-size` for the suppression pair: 2.5 em, so the 6-baked-px cell half-height becomes a
        /// 15 logical-px SCREEN box half-height.</summary>
        private const float SuppressionTextSizePx = 2.5f * TextQuadLayout.OneEm;

        private const float  SuppressionCellHalfHeightBaked = 6f;   // what Cell(...) bakes
        private const float  SuppressionCellHalfWidthBaked  = 10f;
        private const float  SuppressionMpp = 1f;                   // metres per logical px
        private const double SuppressionTargetHalfHeightPx = 2.0;   // the projected box's designed half-height
        private const double SuppressionTargetSeparationPx = 8.0;   // the roads' designed projected gap

        /// <summary>The screen box's half-height, in logical px: the cell half-height scaled by
        /// <c>TextSizePx / OneEm</c>, with no depth term at all — which is the defect the projected box removes.</summary>
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

        /// <summary>Stages the Projected-T4 pair — two map-pitched one-glyph symbols on PARALLEL roads at the same
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
                $"Projected-T4 precondition: both labels of the pair must stage — staged {a} and {b}. A zero means " +
                "the road is too short for the label's world span or the anchor spilled.");
            Assert.That(p.BoxCount, Is.EqualTo(2),
                $"Projected-T4 precondition: the shared pool must hold exactly one box per label, got {p.BoxCount}.");
            return p;
        }

        private static double HalfHeightPx(in SymbolBox b) => 0.5 * (b.Max.y - b.Min.y);
        private static double HalfWidthPx(in SymbolBox b)  => 0.5 * (b.Max.x - b.Min.x);
        private static double CentreY(in SymbolBox b)      => 0.5 * (b.Max.y + b.Min.y);

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T4-pre — the suppression fixture's three sizing rows, asserted directly
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The preconditions of <c>MapPitched_ProjectedBox_PlacesBothSymbols_WhereTheScreenBoxSuppressesOne</c>,
        /// so a re-tuned constant reds HERE instead of making that tooth vacuous. It also proves the hand-built
        /// view transform projects: the projected half-height reads ≈2 px, not the screen box's 15 px.
        /// </summary>
        [Test]
        public void MapPitched_SuppressionFixture_IsSizedSoTheTwoBoxesStraddleTheRoadGap()
        {
            Pools screen = StageSuppressionPair(default);            // no view transform ⇒ the screen box
            Pools projected = StageSuppressionPair(OriginView());     // the projected box

            double screenHalfHeight = HalfHeightPx(screen.Boxes[0]);
            double projectedHalfHeight = HalfHeightPx(projected.Boxes[0]);
            double screenSeparation = CentreY(screen.Boxes[1]) - CentreY(screen.Boxes[0]);
            double projectedSeparation = CentreY(projected.Boxes[1]) - CentreY(projected.Boxes[0]);

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Projected-T4-pre  depth={0:F2} m  lateral={1:F2} m  screen half-height={2:F4} px  " +
                "projected half-height={3:F4} px  separation: screen={4:F4} px projected={5:F4} px",
                SuppressionDepthM, SuppressionLateralM, screenHalfHeight, projectedHalfHeight,
                screenSeparation, projectedSeparation));

            Assert.That(screenHalfHeight, Is.EqualTo(SuppressionScreenHalfHeightPx).Within(1e-3),
                $"Projected-T4-pre row 1: the SCREEN box's half-height must be the depth-free " +
                $"cellHalfBaked·TextSizePx/OneEm = {SuppressionScreenHalfHeightPx:F4} px, read " +
                $"{screenHalfHeight:F4}.");
            Assert.That(projectedHalfHeight, Is.EqualTo(SuppressionTargetHalfHeightPx).Within(0.05),
                $"Projected-T4-pre row 2: the projected box's half-height must foreshorten to the designed " +
                $"{SuppressionTargetHalfHeightPx:F2} px at {SuppressionDepthM:F1} m, read " +
                $"{projectedHalfHeight:F4}. A reading of {SuppressionScreenHalfHeightPx:F1} px means every " +
                "corner failed to project and the box fell back — check ViewMatrix's Z sense.");
            Assert.That(projectedSeparation, Is.EqualTo(SuppressionTargetSeparationPx).Within(0.05),
                $"Projected-T4-pre row 3: the roads' projected lateral separation must be the designed " +
                $"{SuppressionTargetSeparationPx:F2} px, read {projectedSeparation:F4}.");
            Assert.That(screenSeparation, Is.EqualTo(SuppressionTargetSeparationPx).Within(0.05),
                $"Projected-T4-pre row 3 (screen arm): the screen boxes ride the SAME projected anchors, so their " +
                $"separation must also be {SuppressionTargetSeparationPx:F2} px, read {screenSeparation:F4} " +
                "— otherwise the two arms differ in more than the box construction.");

            // The discriminator, stated as arithmetic: the separation must sit STRICTLY BETWEEN the two box
            // heights, or the two arms cannot disagree about placement.
            Assert.That(projectedSeparation, Is.GreaterThan(2.0 * projectedHalfHeight),
                $"Projected-T4-pre: the projected boxes must CLEAR each other — separation {projectedSeparation:F4} " +
                $"px against a full height of {2.0 * projectedHalfHeight:F4} px.");
            Assert.That(screenSeparation, Is.LessThan(2.0 * screenHalfHeight),
                $"Projected-T4-pre: the screen boxes must OVERLAP — separation {screenSeparation:F4} px against a " +
                $"full height of {2.0 * screenHalfHeight:F4} px.");
            Assert.That(HalfWidthPx(projected.Boxes[0]), Is.GreaterThan(0.5),
                "Projected-T4-pre: the projected box must have a real width too — a collapsed box would clear its " +
                "neighbour for the wrong reason.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T4 — SUPPRESSION. The old box drops a symbol the new one places.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Why the projected box exists: two map-pitched symbols on parallel roads whose PROJECTED boxes clear
        /// by 8 px while their SCREEN boxes overlap. With a view transform both place; with the reachable
        /// <c>default(SymbolViewTransform)</c> only ONE does. Both arms run the same code, so no injection is needed.
        /// </summary>
        [Test]
        public void MapPitched_ProjectedBox_PlacesBothSymbols_WhereTheScreenBoxSuppressesOne()
        {
            int projectedSurvivors = Survivors(StageSuppressionPair(OriginView()), "projected");
            int screenSurvivors    = Survivors(StageSuppressionPair(default),      "screen");

            Assert.That(projectedSurvivors, Is.EqualTo(2),
                $"Projected-T4: with the four world corners projected, the two labels' boxes clear each other and " +
                $"BOTH must place — {projectedSurvivors} survived. This is the label the screen box was " +
                "silently eating at tilt.");
            Assert.That(screenSurvivors, Is.EqualTo(1),
                $"Projected-T4 (control): the SCREEN box has no depth term, so at this depth it over-reserves " +
                $"and one of the two must be suppressed — {screenSurvivors} survived. If this reads 2 the " +
                "fixture has stopped discriminating; Projected-T4-pre says which row moved.");
        }

        /// <summary>Runs the shared collision pass over a staged pair and reports the survivor set, printing
        /// both boxes so a fixture that stops discriminating fails with its numbers.</summary>
        private static int Survivors(Pools p, string armName)
        {
            var survivor = new bool[2];
            int n = NativeCollisionRunner.RunCollision(p.Candidates, 2, p.Boxes, p.BoxCount, survivor);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Projected-T4  {0,-16} boxA=[{1:F3}, {2:F3}]×[{3:F3}, {4:F3}]  boxB=[{5:F3}, {6:F3}]×[{7:F3}, " +
                "{8:F3}]  survivors={9} ({10}, {11})",
                armName, p.Boxes[0].Min.x, p.Boxes[0].Max.x, p.Boxes[0].Min.y, p.Boxes[0].Max.y,
                p.Boxes[1].Min.x, p.Boxes[1].Max.x, p.Boxes[1].Min.y, p.Boxes[1].Max.y,
                n, survivor[0], survivor[1]));
            return n;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T5 — the non-map path cannot observe the view transform, at any magnitude
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A symbol whose pitch alignment is not <see cref="AlignmentMode.Map"/> cannot observe the view
        /// transform: its <see cref="SymbolBox"/>es and <see cref="PlacedQuad"/>s are BIT-identical with none and
        /// with three different real ones, for Viewport and Auto. Non-obvious why: the fixture is a camera-facing
        /// glyph that projects, not <c>StageReferenceSymbol</c>, whose zero normal fails projection anyway.
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
                        $"Projected-T5 precondition ({mode}, view {v}): the reference label must stage.");
                    quadBits[v] = Bits(p.Quads, p.QuadCount);
                    boxBits[v]  = Bits(p.Boxes, p.BoxCount);
                }

                for (int v = 1; v < views.Length; v++)
                {
                    AssertBitIdentical(quadBits[0], quadBits[v],
                        $"Projected-T5 ({mode}): PlacedQuads under view transform {v} differ from those with none");
                    AssertBitIdentical(boxBits[0], boxBits[v],
                        $"Projected-T5 ({mode}): SymbolBoxes under view transform {v} differ from those with none");
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
        // Projected-T6 — a degenerate ground frame falls back to the screen box, bit-identically
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A map-pitched symbol with a zero per-vertex <c>Up</c> (as <c>SymbolTileBlockBaker</c> writes for a null
        /// <c>PathUpRender</c>) takes the SCREEN box, BIT-identically. Limitation: the shader's degenerate
        /// fallback is a camera-facing METRE frame, so ink and box disagree in this unreachable state. First,
        /// a real <c>Up</c> must give a DIFFERENT box, so the transform is known to be usable.
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
                "Projected-T6  projected half-height={0:F4} px  screen half-height={1:F4} px  degenerate-Up " +
                "half-height={2:F4} px", projectedHalfHeight, screenHalfHeight, HalfHeightPx(zeroUp.Boxes[0])));

            Assert.That(math.abs(projectedHalfHeight - screenHalfHeight), Is.GreaterThan(1.0),
                $"Projected-T6 precondition (non-vacuity): with a REAL surface normal this transform must produce a " +
                $"genuinely different box — projected {projectedHalfHeight:F4} px against screen " +
                $"{screenHalfHeight:F4} px. Without this the bit-identity below would only say the transform " +
                "was never usable.");
            AssertBitIdentical(Bits(noView.Boxes, noView.BoxCount), Bits(zeroUp.Boxes, zeroUp.BoxCount),
                "Projected-T6: a map-pitched label with a ZERO surface normal must fall back to the screen box");
        }

        /// <summary>Projected-T6/T7's probe symbol: the camera-facing fixture at the suppression pair's own depth, so
        /// its projected box is the well-understood ≈2 px one and the fallback is unmistakably different.</summary>
        private static void StageDegenerateProbe(ref Pools p, SymbolViewTransform view, float3 surfaceUp)
        {
            int staged = StageW3Glyph(ref p, in view, in surfaceUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 42, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "Projected-T6/T7 precondition: the probe label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T7 — a corner that fails to project falls back, and does not emit a half-built box
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// When ONE world corner lands behind the camera, the whole box falls back to the screen box
        /// BIT-identically, never a three-corner box. A 6000 m half-height on a road 100 m away puts the
        /// up-screen corner at <c>z = −5900 m</c>. Limitation: no shipped pose reaches this state; the deep arm,
        /// where all corners project, shows the fallback comes from the corner rejection.
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
                "Projected-T7  deep: projected=[{0:F3}, {1:F3}] screen=[{2:F3}, {3:F3}]  shallow: projected=" +
                "[{4:F3}, {5:F3}] screen=[{6:F3}, {7:F3}]",
                deepProjected.Boxes[0].Min.x, deepProjected.Boxes[0].Max.x,
                deepScreen.Boxes[0].Min.x, deepScreen.Boxes[0].Max.x,
                shallowProjected.Boxes[0].Min.x, shallowProjected.Boxes[0].Max.x,
                shallowScreen.Boxes[0].Min.x, shallowScreen.Boxes[0].Max.x));

            // Non-vacuity first: at a depth where every corner projects, the SAME transform and the SAME cell
            // give a genuinely different box — so the fallback below is attributable to the rejected corner.
            Assert.That(BitsEqual(Bits(deepProjected.Boxes, deepProjected.BoxCount),
                                  Bits(deepScreen.Boxes, deepScreen.BoxCount)), Is.False,
                "Projected-T7 precondition (non-vacuity): with every corner in front of the camera the projected box " +
                "must differ from the screen box, or the bit-identity below says nothing about the corner guard.");

            AssertBitIdentical(Bits(shallowScreen.Boxes, shallowScreen.BoxCount),
                Bits(shallowProjected.Boxes, shallowProjected.BoxCount),
                "Projected-T7: with one corner behind the camera the box must be the screen box");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T11 — a corner that projects to a near-plane BLOW-UP falls back, so the AABB is bounded
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A corner just IN FRONT of the camera plane passes <c>clip.w &gt; 0</c> and divides into a huge but
        /// FINITE screen coordinate; it takes the screen-box fallback, BIT-identically, not an unbounded AABB.
        /// Unlike the behind-camera tooth, <c>TryProjectPoint</c> accepts this corner, so only the magnitude
        /// guard rejects it (anchor at 6001 m, half-height 6000 m). Limitation: no shipped pose reaches it.
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
                "Projected-T11  deep: [{0:F3}, {1:F3}]  blow-up projected: [{2:F3}, {3:F3}]  blow-up screen: [{4:F3}, {5:F3}]",
                deepProjected.Boxes[0].Min.x, deepProjected.Boxes[0].Max.x,
                blowUpProjected.Boxes[0].Min.x, blowUpProjected.Boxes[0].Max.x,
                blowUpScreen.Boxes[0].Min.x, blowUpScreen.Boxes[0].Max.x));

            // Non-vacuity 1: the deep arm must take the PROJECTED branch, or "falls back" below says nothing.
            Assert.That(BitsEqual(Bits(deepProjected.Boxes, deepProjected.BoxCount),
                                  Bits(blowUpScreen.Boxes, blowUpScreen.BoxCount)), Is.False,
                "Projected-T11 precondition: at a depth where every corner projects sanely the projected box must " +
                "differ from the screen box.");

            // Non-vacuity 2: the near corner's view depth (anchor − halfHeight) is POSITIVE, so TryProjectPoint
            // accepts it and only the magnitude test can reject it.
            const double cellHalfHeightM = 6.0 * hugeRulerMpp;         // 6 baked px at 1 em, in metres
            const double nearCornerDepthM = blowUpDepthM - cellHalfHeightM;
            Assert.That(nearCornerDepthM, Is.GreaterThan(0.0),
                $"Projected-T11 precondition: the near corner must be IN FRONT of the camera (depth " +
                $"{nearCornerDepthM} m) — otherwise this duplicates Projected-T7's behind-camera rejection.");

            // Non-vacuity 3: the fallback the tooth asserts must itself be bounded — that is the whole point.
            float guardedWidth = blowUpScreen.Boxes[0].Max.x - blowUpScreen.Boxes[0].Min.x;
            Assert.That(guardedWidth, Is.LessThan(SymbolScreenProjectionMaxProjectedPx),
                $"Projected-T11 precondition: the fallback box must be BOUNDED — got width {guardedWidth}.");

            AssertBitIdentical(Bits(blowUpScreen.Boxes, blowUpScreen.BoxCount),
                Bits(blowUpProjected.Boxes, blowUpProjected.BoxCount),
                "Projected-T11: a corner projecting past MaxProjectedPx must take the screen box");
        }

        /// <summary>Mirror of <c>SymbolScreenProjection.MaxProjectedPx</c>. Re-stated here rather than read
        /// back, so a change to the production threshold does not silently move this tooth's expectation.</summary>
        private const float SymbolScreenProjectionMaxProjectedPx = 1e5f;

        /// <summary>Projected-T7's probe: the GROUND-normal fixture, whose ŷ runs along the view axis so a corner
        /// offset moves the corner in DEPTH.</summary>
        private static void StageCornerProbe(ref Pools p, SymbolViewTransform view, float mpp, double depthM)
        {
            int staged = StageW3Glyph(ref p, in view, GroundUp, Cell(SuppressionCellHalfWidthBaked),
                cellSkirt: 0f, textSizePx: TextQuadLayout.OneEm, mpp,
                depthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 43, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "Projected-T7 precondition: the probe label must stage.");
        }

        private static bool BitsEqual(uint[] a, uint[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T8 — `icon-rotate` is INSIDE the map-pitched box
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The map-pitched box includes the constant <c>icon-rotate</c>: a NON-SQUARE icon cell at 0° and 90°
        /// swaps its projected half-extents, derived from the cell's own corners. A centred cell gives the same
        /// AABB at ±90°, so this pins that the rotation is applied, not its sign. The shipped 180° cannot
        /// discriminate: on a centred cell its AABB is unchanged.
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
                "Projected-T8  upright half-extents=({0:F4}, {1:F4}) px  turned=({2:F4}, {3:F4}) px  expected " +
                "upright=({4:F4}, {5:F4})",
                HalfWidthPx(upright.Boxes[0]), HalfHeightPx(upright.Boxes[0]),
                HalfWidthPx(turned.Boxes[0]), HalfHeightPx(turned.Boxes[0]),
                expectedHalfWidth, expectedHalfHeight));

            Assert.That(math.abs(expectedHalfWidth - expectedHalfHeight), Is.GreaterThan(1.0),
                $"Projected-T8 precondition: the cell must be far from square in projection — half-extents " +
                $"{expectedHalfWidth:F4} and {expectedHalfHeight:F4} px — or a 90° rotation changes nothing " +
                "and this tooth discriminates nothing.");
            Assert.That(HalfWidthPx(upright.Boxes[0]), Is.EqualTo(expectedHalfWidth).Within(0.05),
                "Projected-T8: at icon-rotate 0 the box's projected half-WIDTH must be the cell's own half-width.");
            Assert.That(HalfHeightPx(upright.Boxes[0]), Is.EqualTo(expectedHalfHeight).Within(0.05),
                "Projected-T8: at icon-rotate 0 the box's projected half-HEIGHT must be the cell's own half-height.");
            Assert.That(HalfWidthPx(turned.Boxes[0]), Is.EqualTo(expectedHalfHeight).Within(0.05),
                $"Projected-T8: at icon-rotate 90° the half-WIDTH must become the cell's half-HEIGHT " +
                $"({expectedHalfHeight:F4} px), read {HalfWidthPx(turned.Boxes[0]):F4}. An unchanged " +
                "half-width means the box omits icon-rotate and no longer bounds what the renderer draws.");
            Assert.That(HalfHeightPx(turned.Boxes[0]), Is.EqualTo(expectedHalfWidth).Within(0.05),
                $"Projected-T8: at icon-rotate 90° the half-HEIGHT must become the cell's half-WIDTH " +
                $"({expectedHalfWidth:F4} px), read {HalfHeightPx(turned.Boxes[0]):F4}.");
        }

        private static void StageRotationProbe(ref Pools p, SymbolViewTransform view,
            float cellHalfWidthBaked, float iconRotateRadians)
        {
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, Cell(cellHalfWidthBaked),
                cellSkirt: 0f, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians, sortKey: 0f, featureIndex: 44, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "Projected-T8 precondition: the icon label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T9 — the skirt is removed from the map-pitched box too
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The projected box bounds the icon's INK, not its <c>CurvedGlyph.CellSkirt</c>: it equals the box of the
        /// pre-shrunk cell with a zero skirt, as
        /// <c>SymbolStagingMathCurvedVertexTests.BuildRotatedGlyph_WithACellSkirt_EqualsTheSameCellPreShrunkByIt</c>
        /// asserts for the screen box. The padded cell with a zero skirt must give a strictly larger box.
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
                "Projected-T9  withSkirt half-extents=({0:F4}, {1:F4})  preShrunk=({2:F4}, {3:F4})  unshrunk=" +
                "({4:F4}, {5:F4}) px",
                HalfWidthPx(withSkirt.Boxes[0]), HalfHeightPx(withSkirt.Boxes[0]),
                HalfWidthPx(preShrunk.Boxes[0]), HalfHeightPx(preShrunk.Boxes[0]),
                HalfWidthPx(unshrunk.Boxes[0]), HalfHeightPx(unshrunk.Boxes[0])));

            Assert.That(HalfWidthPx(unshrunk.Boxes[0]), Is.GreaterThan(HalfWidthPx(withSkirt.Boxes[0]) + 1e-4),
                "Projected-T9 precondition (non-vacuity): a non-zero cell skirt must SHRINK the projected box — " +
                "otherwise this tooth proves nothing.");
            AssertBitIdentical(Bits(preShrunk.Boxes, preShrunk.BoxCount),
                Bits(withSkirt.Boxes, withSkirt.BoxCount),
                "Projected-T9: the projected box of a skirted cell must equal that of the same cell pre-shrunk by it");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Projected-T10 — the ŷ SENSE of the projected box, on a cell that can actually see it
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>TryBuildProjectedWorldGlyph</c>'s <c>ŷ = cross(x̂, up)</c> puts a positive y-UP cell coordinate
        /// ABOVE the anchor. Non-obvious why: a flipped ŷ only permutes the corners of a cell symmetric in y, so
        /// the AABB hides it; this cell sits wholly above its anchor (y +6 to +18 baked). Limitation: the oracle
        /// re-derives the sense, so a SHARED convention error passes (see <c>MapPitchedGlyphSizeTiltZeroTests</c>).
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
            Assert.That(staged, Is.EqualTo(1), "Projected-T10 precondition: the off-centre-cell label must stage.");

            double metresPerBaked = SuppressionTextSizePx * SuppressionMpp / TextQuadLayout.OneEm;
            double pxPerBaked = PxPerMetreAtUnitDepth * metresPerBaked / SuppressionDepthM;
            double anchorY = 0.5 * ViewportPx;                  // the road is at lateral 0 ⇒ screen centre
            double expectedMinY = anchorY + cellBottomBaked * pxPerBaked;
            double expectedMaxY = anchorY + cellTopBaked * pxPerBaked;
            double flipSeparationPx = (cellTopBaked + cellBottomBaked) * pxPerBaked;

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Projected-T10  anchorY={0:F3}  box y=[{1:F4}, {2:F4}]  expected=[{3:F4}, {4:F4}]  a flipped ŷ would " +
                "move it by {5:F4} px", anchorY, p.Boxes[0].Min.y, p.Boxes[0].Max.y, expectedMinY, expectedMaxY,
                flipSeparationPx));

            Assert.That(flipSeparationPx, Is.GreaterThan(1.0),
                $"Projected-T10 precondition: the cell must be genuinely OFF-CENTRE in y — a flip would move the box " +
                $"by {flipSeparationPx:F4} px, and below ~1 px this tooth stops discriminating the sign it " +
                "exists to read.");
            Assert.That(p.Boxes[0].Min.y, Is.EqualTo(expectedMinY).Within(0.05),
                $"Projected-T10: the box's LOWER edge must be the cell's own +{cellBottomBaked} baked px ABOVE the " +
                $"anchor ({expectedMinY:F4}), read {p.Boxes[0].Min.y:F4}. A value BELOW the anchor means " +
                "ŷ = cross(x̂, up) has been flipped and the box sits on the wrong side of the road.");
            Assert.That(p.Boxes[0].Max.y, Is.EqualTo(expectedMaxY).Within(0.05),
                $"Projected-T10: the box's UPPER edge must be the cell's own +{cellTopBaked} baked px above the " +
                $"anchor ({expectedMaxY:F4}), read {p.Boxes[0].Max.y:F4}.");
            Assert.That(p.Boxes[0].Min.y, Is.GreaterThan(anchorY),
                $"Projected-T10: a cell lying entirely above its anchor must produce a box lying entirely above the " +
                $"anchor's projection ({anchorY:F3}) — read a lower edge of {p.Boxes[0].Min.y:F4}.");
        }

        private static void StageSkirtProbe(ref Pools p, SymbolViewTransform view, SymbolQuad cell, float skirt)
        {
            int staged = StageW3Glyph(ref p, in view, CameraFacingUp, in cell,
                skirt, SuppressionTextSizePx, SuppressionMpp,
                SuppressionDepthM, lateralM: 0.0, roadHalfM: 1000.0,
                iconRotateRadians: 0f, sortKey: 0f, featureIndex: 45, ordinal: 0);
            Assert.That(staged, Is.EqualTo(1), "Projected-T9 precondition: the icon label must stage.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // The CPU ground frame with a NON-ZERO axial term: axial = dot(tangent, up); x̂ = normalize(tangent −
        // up·axial); ŷ = cross(x̂, up). Everywhere else axial is 0 (Mercator up is constant).
        // Non-obvious why: axial is 0 at the chord midpoint and at an interior vertex, so the fixture is ONE
        // segment of a hand-built sphere anchored off its midpoint. Limitation: the shader's copy is not observed.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The globe fixture's sphere radius — production's own, so the fixture cannot drift onto a
        /// toy sphere whose arc-to-metre relationship is not the one the renderer ships.</summary>
        private const double GlobeRadiusM = SphericalProjection.Radius;

        /// <summary>The great-circle arc ONE path segment subtends: production's own subdivision cap
        /// (<c>SphericalProjection.MaxCurveSegmentRad</c>, 2°). This is the WORST case a realistically built
        /// spherical path can present to the ground frame, since <c>SymbolFeatureExtractor</c> subdivides to
        /// this bound — so the fixture is not inflating the angle. Narrowing the cap will red GA-T1's
        /// |axial| floor: the achieved signal is a function of it.</summary>
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

        /// <summary>The globe teeth's glyph cell: y-OFF-CENTRE, entirely above its anchor. A measurement showed
        /// that an AABB is invariant under a ŷ flip on a y-symmetric cell, so a symmetric cell here would
        /// leave the ŷ leg of the frame unobserved for free. Projected-T10's shape, reused for the same reason.</summary>
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
        /// the origin) — always exactly <see cref="GlobeRadiusM"/> from the centre.</summary>
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
        /// screen box the control arm reads is the box the shipped code would really have built.</summary>
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
        /// <paramref name="frameAngleRad"/> within the arc plane. <c>GlobeSampledUpAngle(t)</c> gives the CORRECT
        /// frame (the sampled normal rotated 90° in the arc plane, not a re-evaluation of production);
        /// <c>GlobeSegmentRad/2</c> gives the frame with the orthogonalisation dropped, to size the separation.
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
        /// Stages the globe fixture TWICE — once with no camera (<c>view: default</c>, the no-transform state ⇒ the
        /// SCREEN box) and once through <see cref="OriginView"/> (the projected box) — and asserts the
        /// two differ substantially. Non-obvious why: five gates in front of <c>TryBuildProjectedWorldGlyph</c>
        /// each SILENTLY yield the screen box, so a globe tooth must check which branch ran.
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
                "{0}  control arm: projected half-height={1:F4} px  screen half-height={2:F4} px  " +
                "separation={3:F4} px", toothId, HalfHeightPx(projected), HalfHeightPx(screen), separation));

            Assert.That(separation, Is.GreaterThan(5.0),
                $"{toothId} precondition (control arm): the projected box must differ from the screen " +
                $"box by well over a pixel, measured {separation:F4} px. If they agree, one of " +
                "TryBuildProjectedWorldGlyph's five gates rejected this fixture and the box below is the " +
                "SCREEN box — the assertions would then be testing BuildRotatedGlyph, not the ground frame.");
            return projected;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // GA-T1 — the fixture IS spherical, and its axial term IS non-zero.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The precondition of the two globe teeth below: the path is ON a sphere of production's radius with
        /// radial ups; production resolved the stated normal and tangent; and <c>|axial|</c> clears an ABSOLUTE
        /// floor of 0.010 and matches <c>sin(α(t) − θ/2)</c>. The floor is needed because the closed form also
        /// agrees at zero. At <c>t = 0.5</c> the same fixture measures ~0.
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
        /// Anchored OFF the chord midpoint, the projected AABB edges are where an in-surface x̂ puts them, not
        /// the raw chord tangent. Limitation: at 16 px a dropped subtraction moves a corner ≈0.19 px, so this
        /// mainly guards operand ORDER (≈120 px off); a 7 em text-size makes the subtraction itself visible.
        /// The oracle uses only the stated geometry and <see cref="ProjectPx"/>, never production's formula.
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
        /// The midpoint arm, INERT by design: at <c>t = 0.5</c> the axial term vanishes. Non-obvious why: a
        /// dropped subtraction reds the off-midpoint tooth but leaves this one GREEN, while swapped operands red
        /// both, which shows the off-midpoint anchoring is what gives coverage.
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
    /// <see cref="ShapedSymbol"/> must live in a <see cref="NativeArray{T}"/> — the whole point of
    /// interning its <c>Text</c>/<c>IconImage</c> strings into <c>TextId</c>/<c>IconImageId</c> ints; a managed
    /// <c>string</c> field makes <c>NativeArray&lt;ShapedSymbol&gt;</c> construction throw. Non-obvious why: not
    /// <c>UnsafeUtility.IsBlittable&lt;T&gt;()</c>, which reads false for any <c>bool</c> field, as
    /// <see cref="IsBlittable_IsTheWrongPredicate_PointStageInputAlsoReadsFalse"/> shows.
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
    /// THE decisive test: a parsed symbol layer + the real fixture tile, run through
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

            // (b) The specific named feature — Aruba — is the first, shaped to 5 glyphs, at its cross-tile anchor.
            Assert.AreEqual("Aruba", extracted[0].Text, "feature[0] is Aruba");
            Assert.AreEqual(5, buffer.Symbols[0].QuadCount, "Aruba → 5 glyph quads");

            // (c) At least two OTHER named symbols match (so a single hard-coded symbol cannot pass).
            AssertNamedSymbol(extracted, buffer, "Afghanistan", 11);
            AssertNamedSymbol(extracted, buffer, "Angola", 6);
        }

        // ── The layout options reach StyledSymbolTileBuilder (s.LayoutOptions, not TextLayoutOptions.Default);
        //    the Extract/TextQuadLayout seam tests stay green without it. ──

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

            // Center anchor in both, so the delta isolates the offset: ems -> baked px is x24 and y is NEGATED
            // (y-down text-offset -> y-up layout), so every quad shifts by (24, -48).
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

            // Justify stays center, so the delta is the anchor alone: Left (hAlign=0) vs Center (0.5) moves
            // the block +x by 0.5*blockWidth, with no vertical change.
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

        // ── Per-symbol build isolation: one symbol whose build throws (e.g. a deferred mixed-direction
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

            // Two LTR symbols around one MIXED symbol ('A' U+0041 + Arabic beh U+0628), which CodepointTextShaper
            // rejects (single-run bidi). The missing Arabic range caches empty in Pass 1; Pass 2's shaper throws.
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
            // A glyph source that OBSERVES the token (FromRanges discards it), so a cancelled ensure propagates
            // an OperationCanceledException. SymbolSubsystemWorkSchedulerTests pins that Shape then does not run.
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

            // CatchAsync accepts any OCE SUBTYPE: Unity/Mono surfaces TaskCanceledException and dotnet a plain
            // OperationCanceledException, and production's `ex is OCE` filter accepts both.
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await builder.EnsureGlyphRangesAsync(ranges, cts.Token));
        }

        // ── Icon symbols ride the same Shape loop as text, but must never touch the
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

        // ── A MAP-aligned LINE icon must build as a ONE-GLYPH CURVED instance, not a point one.
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
            // A point-shaped build would leave GlyphCount 0 and Placement Point.
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
            // AppendPath/AppendAnchors COPY into the buffer's pools, so "carried, not rebuilt" is a VALUE check
            // that the values are copied verbatim.
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

        // ── Every layer processor of ONE build writes into the SAME SymbolTileBuffer, or SymbolPairing's
        //    owner-at-i+1 breaks: an owner ends one Shape call and its rider starts the next. ──
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
