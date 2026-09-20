// Unity EditMode only — needs the job runtime (NativeArray / IJob). NOT registered in core-tests.csproj.
// NOTE: Burst compiles CollisionJob only when Jobs > Burst > Enable Compilation is on AND it compiles — a
// compile failure falls back to managed IL SILENTLY (FillGraphBurstProbeTests), so the runner alone doesn't
// decide it. In this project's practice ./Tools/run-tests.sh (batch mode, confirmed via its log) is the
// Burst-compiled path; the interactive Editor Test Runner is not verified that way.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Text.Placement
{
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
}
