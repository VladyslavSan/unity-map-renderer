// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Top-level `using Unity.Mathematics;` + unqualified float2 (namespace-collision trap
// — see LabelBox.cs's header comment).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 Slice 2 — THE decisive test (stage doc §4 T1): <see cref="LabelCollision.SelectSurvivors"/>
    /// produces a NAMED, permutation-invariant, sort-key-driven survivor set. Every assertion is
    /// OUTCOME-based (WHICH labels survive, by <see cref="LabelBox.LabelIndex"/> — not a count), so the
    /// F3 acceleration-structure choice (brute force now, uniform grid later) cannot invalidate it.
    /// </summary>
    [TestFixture]
    public class LabelCollisionTests
    {
        // A box at [minX,maxX]x[minY,maxY] with the given placement key/flags. LabelIndex == featureIndex
        // so survivor identity reads back as the feature index in the assertions.
        private static LabelBox Box(
            float minX, float minY, float maxX, float maxY,
            float sortKey, int featureIndex, long tileKey = 0L,
            bool allowOverlap = false, bool ignorePlacement = false)
            => new LabelBox
            {
                Min = new float2(minX, minY),
                Max = new float2(maxX, maxY),
                SortKey = sortKey,
                FeatureIndex = featureIndex,
                TileKey = tileKey,
                LabelIndex = featureIndex,
                AllowOverlap = allowOverlap,
                IgnorePlacement = ignorePlacement,
            };

        // Runs collision over a COPY (so the caller's array order is preserved across permutations) and
        // returns the set of surviving LabelIndex values.
        private static HashSet<int> Survivors(IReadOnlyList<LabelBox> input)
        {
            var boxes = new LabelBox[input.Count];
            for (int i = 0; i < input.Count; i++) boxes[i] = input[i];
            var flags = new bool[boxes.Length];
            int n = LabelCollision.SelectSurvivors(boxes, boxes.Length, flags);

            var set = new HashSet<int>();
            for (int i = 0; i < boxes.Length; i++) if (flags[i]) set.Add(boxes[i].LabelIndex);
            Assert.AreEqual(n, set.Count, "returned survivor count must match the number of set flags");
            return set;
        }

        // A cluster of three mutually-overlapping boxes over region A (all cover [0,20]x[0,20]) with
        // distinct sort keys, plus one DISJOINT box over region B ([100,120]) that can never collide.
        // Placement order (sort key asc): L1(10) -> L2(20) -> L0(30) -> L3(99). L1 wins region A; L2/L0
        // collide with it and drop; L3 is alone. Survivors = {1, 3}.
        private static List<LabelBox> NamedScenario() => new List<LabelBox>
        {
            Box(0, 0, 20, 20, sortKey: 30f, featureIndex: 0),   // region A
            Box(2, 2, 18, 18, sortKey: 10f, featureIndex: 1),   // region A (best key)
            Box(4, 4, 16, 16, sortKey: 20f, featureIndex: 2),   // region A
            Box(100, 0, 120, 20, sortKey: 99f, featureIndex: 3) // region B (disjoint — always survives)
        };

        // ── THE named survivor set: exact ids, not a count ─────────────────────────────────────────
        [Test]
        public void SelectSurvivors_GreedyOverCluster_KeepsExactNamedSet()
        {
            CollectionAssert.AreEquivalent(new[] { 1, 3 }, Survivors(NamedScenario()),
                "greedy (sort-key asc) keeps the best-key label in the overlapping cluster plus the " +
                "disjoint label — NOT cull-all ({}), cull-none ({0,1,2,3}), or an insertion-order pick.");
        }

        // ── (a) permutation invariance: shuffling the input yields the IDENTICAL survivor set ──────────
        [Test]
        public void SelectSurvivors_IsPermutationInvariant()
        {
            var forward = NamedScenario();
            var reversed = new List<LabelBox>(forward);
            reversed.Reverse();
            var rotated = new List<LabelBox> { forward[2], forward[0], forward[3], forward[1] };

            var expected = new[] { 1, 3 };
            CollectionAssert.AreEquivalent(expected, Survivors(forward));
            CollectionAssert.AreEquivalent(expected, Survivors(reversed));
            CollectionAssert.AreEquivalent(expected, Survivors(rotated),
                "an insertion-order-dependent impl would place a different cluster winner under reordering");
        }

        // ── (b) sort-key drives the winner: swapping two overlapping labels' keys flips the survivor ───
        [Test]
        public void SelectSurvivors_LowerSortKeyWins_AndSwappingKeysFlipsIt()
        {
            // Two overlapping boxes (same region) + a disjoint control that always survives.
            var control = Box(100, 0, 120, 20, sortKey: 5f, featureIndex: 9);

            var l0Wins = new List<LabelBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 0), // lower key -> placed first -> wins
                Box(5, 5, 25, 25, sortKey: 20f, featureIndex: 1),
                control,
            };
            CollectionAssert.AreEquivalent(new[] { 0, 9 }, Survivors(l0Wins));

            var l1Wins = new List<LabelBox>
            {
                Box(0, 0, 20, 20, sortKey: 20f, featureIndex: 0),
                Box(5, 5, 25, 25, sortKey: 10f, featureIndex: 1), // now the lower key -> wins
                control,
            };
            CollectionAssert.AreEquivalent(new[] { 1, 9 }, Survivors(l1Wins),
                "reversing the two overlapping labels' sort keys must flip which one survives");
        }

        // ── (c) text-allow-overlap: an overlapping pair, both allow-overlap -> BOTH survive ────────────
        [Test]
        public void SelectSurvivors_AllowOverlap_KeepsBoth()
        {
            var pair = new List<LabelBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 0, allowOverlap: true),
                Box(5, 5, 25, 25, sortKey: 20f, featureIndex: 1, allowOverlap: true),
            };
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, Survivors(pair),
                "text-allow-overlap skips the collision test — both overlapping labels are placed");
        }

        // ── text-ignore-placement: a placed-but-non-blocking label lets a later overlapping one through ─
        [Test]
        public void SelectSurvivors_IgnorePlacement_DoesNotBlockLaterLabels()
        {
            var boxes = new List<LabelBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 0, ignorePlacement: true), // placed, non-blocking
                Box(5, 5, 25, 25, sortKey: 20f, featureIndex: 1),                        // overlaps 0 but 0 doesn't block
            };
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, Survivors(boxes),
                "an ignore-placement label is placed but must not block a later overlapping label");
        }

        // ── (d) text-padding widens a borderline-adjacent pair from both-survive to one-survives ───────
        //    Boxes are built through the REAL LabelBox.Build math (anchor + bounds*scale +/- padding), so
        //    this is the padding PLUMBING under test, not a hand-inflated AABB.
        [Test]
        public void SelectSurvivors_Padding_TurnsAdjacentPairIntoAColission()
        {
            // Two 20px-wide boxes (bounds [0,0]..[20,20], scale 1) anchored 21px apart on x -> a 1px gap.
            var boundsMin = float2.zero;
            var boundsMax = new float2(20f, 20f);
            const float scaleSize = TextQuadLayout.OneEm; // textSizePx == OneEm -> scale 1

            LabelBox A(float padding) => LabelBox.Build(
                new float2(0f, 0f), boundsMin, boundsMax, scaleSize, padding,
                sortKey: 10f, featureIndex: 0, tileKey: 0L, labelIndex: 0,
                allowOverlap: false, ignorePlacement: false);
            LabelBox B(float padding) => LabelBox.Build(
                new float2(21f, 0f), boundsMin, boundsMax, scaleSize, padding,
                sortKey: 20f, featureIndex: 1, tileKey: 0L, labelIndex: 1,
                allowOverlap: false, ignorePlacement: false);

            // No padding: A=[0,20], B=[21,41] -> 1px gap -> both survive.
            CollectionAssert.AreEquivalent(new[] { 0, 1 },
                Survivors(new List<LabelBox> { A(0f), B(0f) }),
                "with no padding the 1px-separated pair does not collide — both survive");

            // Padding 1px each edge: A=[-1,21], B=[20,42] -> overlap -> only the lower-key A survives.
            CollectionAssert.AreEquivalent(new[] { 0 },
                Survivors(new List<LabelBox> { A(1f), B(1f) }),
                "1px padding closes the 1px gap — the pair now collides and only the better-key label survives");
        }

        // ── (e) equal sort keys resolve to a deterministic winner via the stable tiebreak ──────────────
        [Test]
        public void SelectSurvivors_EqualSortKeys_ResolveByFeatureIndexTiebreak()
        {
            var boxes = new List<LabelBox>
            {
                Box(0, 0, 20, 20, sortKey: 10f, featureIndex: 7), // equal key, higher feature index
                Box(5, 5, 25, 25, sortKey: 10f, featureIndex: 3), // equal key, LOWER feature index -> wins
            };
            CollectionAssert.AreEquivalent(new[] { 3 }, Survivors(boxes),
                "equal sort keys must resolve deterministically to the lower feature index (stable tiebreak)");
        }

        // ── (e) structural: the placement order key is (sortKey, featureIndex, tileKey), not bare sortKey ─
        [Test]
        public void ComparePlacementOrder_IsSortKeyThenFeatureIndexThenTileKey()
        {
            // Sort key dominates.
            Assert.Less(LabelCollision.ComparePlacementOrder(
                Box(0, 0, 1, 1, sortKey: 1f, featureIndex: 9, tileKey: 9),
                Box(0, 0, 1, 1, sortKey: 2f, featureIndex: 0, tileKey: 0)), 0,
                "a lower sort key must order first regardless of the tiebreak fields");

            // Equal sort key -> feature index breaks the tie.
            Assert.Less(LabelCollision.ComparePlacementOrder(
                Box(0, 0, 1, 1, sortKey: 5f, featureIndex: 0, tileKey: 9),
                Box(0, 0, 1, 1, sortKey: 5f, featureIndex: 1, tileKey: 0)), 0,
                "equal sort keys must order by feature index (a bare-sortKey compare would return 0 here)");

            // Equal sort key AND feature index -> tile key breaks the tie.
            Assert.Less(LabelCollision.ComparePlacementOrder(
                Box(0, 0, 1, 1, sortKey: 5f, featureIndex: 3, tileKey: 100),
                Box(0, 0, 1, 1, sortKey: 5f, featureIndex: 3, tileKey: 200)), 0,
                "equal sort keys and feature indices must order by tile key (guaranteeing a total order)");

            // Fully equal -> 0.
            Assert.AreEqual(0, LabelCollision.ComparePlacementOrder(
                Box(0, 0, 1, 1, sortKey: 5f, featureIndex: 3, tileKey: 100),
                Box(0, 0, 1, 1, sortKey: 5f, featureIndex: 3, tileKey: 100)));
        }

        // ── DIFFERENTIAL: grid-accelerated SelectSurvivors == brute-force reference ────────────────────
        // The grid is a pure prefilter, so its survivor set must be BIT-IDENTICAL to brute force over
        // randomized inputs. This is the linchpin: the hand-built T1 cases above cannot expose the grid's
        // real failure mode — a blocker missed because a candidate's cell coverage and the blocker's cell
        // coverage disagree. So randomize seed × density × flag-mix, and deliberately include WIDE boxes
        // that span many cells (the one dangerous case) plus tie-heavy sort keys (exercise the tiebreak).

        private enum FlagMix { None, AllowSome, IgnoreSome, Both }

        // count boxes with unique LabelIndex/FeatureIndex; positions/sizes chosen so wide boxes cover several
        // 64px grid cells; sortKey drawn from a small set so ties are common (feature index still totally
        // orders them). Deterministic per seed.
        private static LabelBox[] RandomBoxes(int count, int seed, FlagMix mix)
        {
            var rng = new System.Random(seed);
            var boxes = new LabelBox[count];
            for (int i = 0; i < count; i++)
            {
                float x = (float)(rng.NextDouble() * 2000.0);
                float y = (float)(rng.NextDouble() * 1200.0);
                float w = 30f + (float)(rng.NextDouble() * 270.0); // up to 300px → spans many cells
                float h = 10f + (float)(rng.NextDouble() * 40.0);
                bool allow = (mix == FlagMix.AllowSome || mix == FlagMix.Both) && rng.NextDouble() < 0.2;
                bool ignore = (mix == FlagMix.IgnoreSome || mix == FlagMix.Both) && rng.NextDouble() < 0.2;
                boxes[i] = new LabelBox
                {
                    Min = new float2(x, y),
                    Max = new float2(x + w, y + h),
                    SortKey = rng.Next(0, 6),          // small range → frequent ties
                    FeatureIndex = i,
                    TileKey = rng.Next(0, 4),
                    LabelIndex = i,
                    AllowOverlap = allow,
                    IgnorePlacement = ignore,
                };
            }
            return boxes;
        }

        private static HashSet<int> SurvivorSet(LabelBox[] boxes, bool[] flags, int count)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < count; i++) if (flags[i]) set.Add(boxes[i].LabelIndex);
            return set;
        }

        [Test]
        public void SelectSurvivorsGrid_MatchesBruteForce_OverRandomizedInputs()
        {
            var grid = new LabelCollisionGrid();
            int[] counts = { 1, 2, 3, 50, 500, 2000 };
            int[] seeds = { 1, 7, 42, 101, 999, 31337 };
            var mixes = new[] { FlagMix.None, FlagMix.AllowSome, FlagMix.IgnoreSome, FlagMix.Both };

            foreach (int count in counts)
            foreach (int seed in seeds)
            foreach (FlagMix mix in mixes)
            {
                LabelBox[] input = RandomBoxes(count, seed, mix);
                // SelectSurvivors sorts IN PLACE, so each path gets its own copy.
                var bruteBoxes = (LabelBox[])input.Clone();
                var gridBoxes = (LabelBox[])input.Clone();
                var bruteFlags = new bool[count];
                var gridFlags = new bool[count];

                int nBrute = LabelCollision.SelectSurvivors(bruteBoxes, count, bruteFlags);
                int nGrid = LabelCollision.SelectSurvivors(gridBoxes, count, gridFlags, grid);

                string ctx = $"(count={count} seed={seed} mix={mix})";
                Assert.AreEqual(nBrute, nGrid, $"survivor COUNT diverged {ctx}");
                CollectionAssert.AreEquivalent(
                    SurvivorSet(bruteBoxes, bruteFlags, count),
                    SurvivorSet(gridBoxes, gridFlags, count),
                    $"grid survivor SET must be identical to brute force {ctx} — a mismatch means the grid " +
                    "missed a blocker (cell-coverage bug) or placed one it should have dropped");
            }
        }

        // Grid instance is REUSED across calls with wildly different sizes/counts (as it is per-frame): a
        // stale-state bug (uncleared cells, leftover nodes) would surface as a divergence on the second run.
        [Test]
        public void SelectSurvivorsGrid_ReusedInstance_StaysCorrectAcrossVaryingInputs()
        {
            var grid = new LabelCollisionGrid();
            int[] sequence = { 2000, 3, 800, 1, 1500, 50 };
            foreach (int count in sequence)
            {
                LabelBox[] input = RandomBoxes(count, seed: 2024 + count, mix: FlagMix.Both);
                var bruteBoxes = (LabelBox[])input.Clone();
                var gridBoxes = (LabelBox[])input.Clone();
                var bruteFlags = new bool[count];
                var gridFlags = new bool[count];

                LabelCollision.SelectSurvivors(bruteBoxes, count, bruteFlags);
                LabelCollision.SelectSurvivors(gridBoxes, count, gridFlags, grid);

                CollectionAssert.AreEquivalent(
                    SurvivorSet(bruteBoxes, bruteFlags, count),
                    SurvivorSet(gridBoxes, gridFlags, count),
                    $"reused grid diverged at count={count} — stale cell/node state not reset");
            }
        }
    }
}
