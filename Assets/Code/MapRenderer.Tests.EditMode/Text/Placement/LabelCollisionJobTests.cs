// Unity EditMode only — needs the job runtime (NativeArray / IJob). NOT registered in core-tests.csproj.
// NOTE: EditMode batch runs the job Burst-compiled; this differential validates the native port against the
// managed reference. The greedy survivor decision is integer/branch logic (no reassociated float math), so the
// two must be BIT-IDENTICAL — hence exact-set equality, not tolerance.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// B-4a: <see cref="LabelCollisionJob"/> (the Burst native port of the grid-accelerated greedy collision) must
    /// produce the EXACT same survivor set as the managed
    /// <see cref="LabelCollision.SelectSurvivors(LabelCandidate[],int,LabelBox[],int,bool[],LabelCollisionGrid)"/>
    /// reference. The real hazard is the caller pre-sizing the grid node storage (a Burst job cannot grow it): an
    /// under-count silently drops blocker inserts → a candidate isn't blocked → wrong survivors. So the differential
    /// runs over ADVERSARIAL inputs — wide boxes spanning many cells, spans that force the cell-enlargement /
    /// MaxGridDim path, and dense clusters — not just uniform random.
    /// </summary>
    [TestFixture]
    public class LabelCollisionJobTests
    {
        // Stage C: the comparable verdict is no longer just "who survived" — it is, per candidate identity,
        // (placed, DroppedBoxMask). Rendered as a sorted "label:placed:mask" list so an asymmetry in either
        // field shows up as a readable diff.
        private static List<string> Verdicts(LabelCandidate[] cands, int candCount, bool[] flags)
        {
            var rows = new List<string>(candCount);
            for (int i = 0; i < candCount; i++)
                rows.Add($"{cands[i].LabelIndex}:{(flags[i] ? 1 : 0)}:{cands[i].DroppedBoxMask}");
            rows.Sort(System.StringComparer.Ordinal);
            return rows;
        }

        private static List<string> ManagedVerdicts(LabelCandidate[] cands, int candCount, LabelBox[] boxes,
            int boxCount, out List<int> survivorIds)
        {
            var work = (LabelCandidate[])cands.Clone(); // SelectSurvivors sorts in place
            var flags = new bool[candCount];
            var grid = new LabelCollisionGrid();
            LabelCollision.SelectSurvivors(work, candCount, boxes, boxCount, flags, grid);
            survivorIds = new List<int>();
            for (int i = 0; i < candCount; i++) if (flags[i]) survivorIds.Add(work[i].LabelIndex);
            return Verdicts(work, candCount, flags);
        }

        private static List<string> NativeVerdicts(LabelCandidate[] cands, int candCount, LabelBox[] boxes,
            int boxCount, out List<int> survivorIds)
        {
            var nc = new NativeArray<LabelCandidate>(candCount, Allocator.TempJob);
            var nb = new NativeArray<LabelBox>(boxCount, Allocator.TempJob);
            var ns = new NativeArray<byte>(candCount, Allocator.TempJob);
            var outCount = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < candCount; i++) nc[i] = cands[i];
                for (int i = 0; i < boxCount; i++) nb[i] = boxes[i];

                LabelCollisionGridSizing.Dims dims = LabelCollisionGridSizing.ComputeDims(nb, boxCount);
                int cells = dims.W * dims.H;
                int nodeCap = math.max(1, LabelCollisionGridSizing.NodeUpperBound(nb, boxCount, in dims));
                var cellHead = new NativeArray<int>(cells, Allocator.TempJob);
                var nodeBox = new NativeArray<int>(nodeCap, Allocator.TempJob);
                var nodeNext = new NativeArray<int>(nodeCap, Allocator.TempJob);
                try
                {
                    for (int c = 0; c < cells; c++) cellHead[c] = -1;
                    new LabelCollisionJob
                    {
                        Candidates = nc, CandidateCount = candCount, Boxes = nb, BoxCount = boxCount,
                        Survivors = ns, OutSurvivorCount = outCount,
                        CellHead = cellHead, NodeBox = nodeBox, NodeNext = nodeNext,
                        GridMinX = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                        GridW = dims.W, GridH = dims.H,
                    }.Schedule().Complete();

                    var sorted = new LabelCandidate[candCount];
                    var flags = new bool[candCount];
                    survivorIds = new List<int>();
                    for (int i = 0; i < candCount; i++)
                    {
                        sorted[i] = nc[i];
                        flags[i] = ns[i] != 0;
                        if (flags[i]) survivorIds.Add(nc[i].LabelIndex);
                    }
                    Assert.AreEqual(survivorIds.Count, outCount[0], "OutSurvivorCount must match the flags");
                    return Verdicts(sorted, candCount, flags);
                }
                finally { cellHead.Dispose(); nodeBox.Dispose(); nodeNext.Dispose(); }
            }
            finally { nc.Dispose(); nb.Dispose(); ns.Dispose(); outCount.Dispose(); }
        }

        private static void AssertSame(LabelCandidate[] cands, LabelBox[] boxes, string what)
        {
            List<string> managed = ManagedVerdicts(cands, cands.Length, boxes, boxes.Length, out List<int> managedIds);
            List<string> native = NativeVerdicts(cands, cands.Length, boxes, boxes.Length, out List<int> nativeIds);
            CollectionAssert.AreEquivalent(managedIds, nativeIds,
                $"native LabelCollisionJob survivor set must equal the managed reference ({what})");
            // Stage C: and the per-half DroppedBoxMask verdict too — the survivor set alone cannot see an
            // asymmetry in WHICH optional half each implementation dropped.
            CollectionAssert.AreEqual(managed, native,
                $"native LabelCollisionJob (placed, DroppedBoxMask) verdicts must equal the managed reference ({what})");
        }

        // 1-box candidates (point-like) — fully exercises the grid, which is the B-4a risk. Box size range is a
        // parameter so a case can force wide boxes (many cells) or a giant span (cell enlargement).
        private static (LabelCandidate[], LabelBox[]) RandomScene(int count, int seed, float worldW, float worldH,
            float wMin, float wMax, float hMin, float hMax)
        {
            var rng = new System.Random(seed);
            var cands = new LabelCandidate[count];
            var boxes = new LabelBox[count];
            for (int i = 0; i < count; i++)
            {
                float x = (float)(rng.NextDouble() * worldW);
                float y = (float)(rng.NextDouble() * worldH);
                float w = wMin + (float)(rng.NextDouble() * (wMax - wMin));
                float h = hMin + (float)(rng.NextDouble() * (hMax - hMin));
                boxes[i] = new LabelBox
                {
                    Min = new float2(x, y), Max = new float2(x + w, y + h),
                    SortKey = rng.Next(0, 6), FeatureIndex = i, TileKey = rng.Next(0, 4), LabelIndex = i,
                };
                cands[i] = new LabelCandidate
                {
                    BoxStart = i, BoxCount = 1, SortKey = boxes[i].SortKey, FeatureIndex = i,
                    TileKey = boxes[i].TileKey, LabelIndex = i,
                };
            }
            return (cands, boxes);
        }

        [Test]
        public void NativeCollision_MatchesManaged_SmallBoxes([Values(1, 2, 3, 50, 500)] int count,
                                                              [Values(1, 7, 42, 999)] int seed)
        {
            var (cands, boxes) = RandomScene(count, seed, 2000f, 1200f, 30f, 300f, 10f, 50f);
            AssertSame(cands, boxes, $"small boxes count={count} seed={seed}");
        }

        // Wide boxes each spanning MANY 64px grid cells — the case where one box lands in a large cell block, so a
        // node-storage under-count would drop inserts.
        [Test]
        public void NativeCollision_MatchesManaged_WideBoxes([Values(20, 200)] int count, [Values(3, 88) ] int seed)
        {
            var (cands, boxes) = RandomScene(count, seed, 3000f, 2000f, 400f, 900f, 300f, 700f);
            AssertSame(cands, boxes, $"wide boxes count={count} seed={seed}");
        }

        // A span far larger than MaxGridDim*TargetCellPx (512*64 = 32768 px) — forces the cell-enlargement path, a
        // different grid dim / node distribution the sizing must still bound exactly.
        [Test]
        public void NativeCollision_MatchesManaged_HugeSpan_ForcesCellEnlargement([Values(50, 400)] int count)
        {
            var (cands, boxes) = RandomScene(count, 5, 200000f, 150000f, 50f, 400f, 20f, 80f);
            AssertSame(cands, boxes, $"huge span count={count}");
        }

        // A DENSE cluster: many overlapping boxes packed into a tiny region (one grid cell), so most drop — stresses
        // the greedy blocking + the single-cell node chain.
        [Test]
        public void NativeCollision_MatchesManaged_DenseCluster()
        {
            var (cands, boxes) = RandomScene(300, 17, 100f, 100f, 40f, 60f, 20f, 30f);
            AssertSame(cands, boxes, "dense cluster (300 boxes in ~2 cells)");
        }

        // Multi-box (curved-like) candidates interleaved with point candidates — the all-or-nothing range logic
        // over the native grid must match the managed reference too.
        [Test]
        public void NativeCollision_MatchesManaged_MultiBoxCandidates()
        {
            // 2 curved (3 boxes each) + 3 points, overlapping in a shared region so collisions actually occur.
            var boxes = new List<LabelBox>();
            var cands = new List<LabelCandidate>();
            void Add(int label, float sortKey, params (float, float, float, float)[] rects)
            {
                int start = boxes.Count;
                foreach (var r in rects)
                    boxes.Add(new LabelBox { Min = new float2(r.Item1, r.Item2), Max = new float2(r.Item3, r.Item4),
                        SortKey = sortKey, FeatureIndex = label, TileKey = 0, LabelIndex = label });
                cands.Add(new LabelCandidate { BoxStart = start, BoxCount = rects.Length, SortKey = sortKey,
                    FeatureIndex = label, TileKey = 0, LabelIndex = label });
            }
            Add(0, 10f, (0, 0, 30, 12), (40, 0, 70, 12), (80, 0, 110, 12));   // curved (best key)
            Add(1, 20f, (50, 2, 60, 10));                                     // point over curved-0 glyph 2
            Add(2, 15f, (200, 0, 230, 12), (240, 0, 270, 12), (280, 0, 310, 12)); // curved, disjoint region
            Add(3, 25f, (205, 2, 215, 10));                                   // point over curved-2 glyph 1
            Add(4, 30f, (1000, 1000, 1020, 1012));                            // point, far away (always places)
            AssertSame(cands.ToArray(), boxes.ToArray(), "multi-box + point candidates");
        }

        // ── C6 (stage C) ──────────────────────────────────────────────────────────────────────────────────
        // Two-box PAIR candidates whose halves OVERLAP BY CONSTRUCTION (what a centred icon+text pair is),
        // each carrying a random OptionalBoxMask, interleaved with ordinary single-box candidates in a
        // congested region so most halves actually contend. The differential now compares the per-half
        // DroppedBoxMask as well as the survivor set, so any divergence between LabelCollision.SelectSurvivors
        // and this job's mirrored loop — a different bit, a different insert-skip — fails here. The overlapping
        // halves are also the self-block tripwire: an implementation that inserted one half before testing the
        // other would drop every pair, in one runner or both.
        private static (LabelCandidate[], LabelBox[]) RandomMaskedPairScene(int pairCount, int singleCount, int seed)
        {
            var rng = new System.Random(seed);
            var boxes = new List<LabelBox>();
            var cands = new List<LabelCandidate>();
            int label = 0;

            // A DETERMINISTIC contended pair, off in its own region, so "at least one half is dropped" holds for
            // every (count, seed) rather than depending on the random draw: a rider-optional pair whose rider box
            // is covered by a higher-priority single, and whose owner box is free.
            boxes.Add(new LabelBox { Min = new float2(980, 980), Max = new float2(1020, 1020),
                SortKey = 5f, FeatureIndex = label, TileKey = 2, LabelIndex = label });
            boxes.Add(new LabelBox { Min = new float2(1030, 992), Max = new float2(1070, 1008),
                SortKey = 5f, FeatureIndex = label, TileKey = 2, LabelIndex = label });
            cands.Add(new LabelCandidate
            {
                BoxStart = 0, BoxCount = 2, EmitStart = 0, EmitCount = 2,
                SortKey = 5f, FeatureIndex = label, TileKey = 2, LabelIndex = label,
                OptionalBoxMask = 0b10,
            });
            label++;
            boxes.Add(new LabelBox { Min = new float2(1035, 995), Max = new float2(1065, 1005),
                SortKey = -1f, FeatureIndex = label, TileKey = 2, LabelIndex = label });
            cands.Add(new LabelCandidate
            {
                BoxStart = boxes.Count - 1, BoxCount = 1, EmitStart = boxes.Count - 1, EmitCount = 1,
                SortKey = -1f, FeatureIndex = label, TileKey = 2, LabelIndex = label,
            });
            label++;

            for (int i = 0; i < pairCount; i++)
            {
                float x = (float)(rng.NextDouble() * 260.0);
                float y = (float)(rng.NextDouble() * 180.0);
                float sortKey = rng.Next(0, 5);
                int start = boxes.Count;
                // Owner box and rider box share the anchor and overlap — the pair geometry that makes
                // test-all-then-insert load-bearing. The world is deliberately small so these actually contend.
                boxes.Add(new LabelBox { Min = new float2(x - 20, y - 20), Max = new float2(x + 20, y + 20),
                    SortKey = sortKey, FeatureIndex = label, TileKey = 0, LabelIndex = label });
                boxes.Add(new LabelBox { Min = new float2(x - 12, y - 8), Max = new float2(x + 34, y + 8),
                    SortKey = sortKey, FeatureIndex = label, TileKey = 0, LabelIndex = label });
                cands.Add(new LabelCandidate
                {
                    BoxStart = start, BoxCount = 2, EmitStart = start, EmitCount = 2,
                    SortKey = sortKey, FeatureIndex = label, TileKey = 0, LabelIndex = label,
                    OptionalBoxMask = (byte)rng.Next(0, 4), // 0 = today's all-or-nothing, 1/2 = one half, 3 = both
                });
                label++;
            }
            for (int i = 0; i < singleCount; i++)
            {
                float x = (float)(rng.NextDouble() * 260.0);
                float y = (float)(rng.NextDouble() * 180.0);
                float sortKey = rng.Next(0, 5);
                boxes.Add(new LabelBox { Min = new float2(x, y), Max = new float2(x + 30, y + 14),
                    SortKey = sortKey, FeatureIndex = label, TileKey = 1, LabelIndex = label });
                cands.Add(new LabelCandidate
                {
                    BoxStart = boxes.Count - 1, BoxCount = 1, EmitStart = boxes.Count - 1, EmitCount = 1,
                    SortKey = sortKey, FeatureIndex = label, TileKey = 1, LabelIndex = label,
                });
                label++;
            }
            return (cands.ToArray(), boxes.ToArray());
        }

        [Test]
        public void NativeCollision_MatchesManaged_OptionalMaskedPairs(
            [Values(4, 30, 120)] int pairCount, [Values(11, 404)] int seed)
        {
            var (cands, boxes) = RandomMaskedPairScene(pairCount, pairCount, seed);
            AssertSame(cands, boxes, $"masked pairs pairs={pairCount} seed={seed}");

            // A masked scene must actually EXERCISE the new branch — otherwise this tooth could pass on a
            // no-op. At least one candidate has to place while dropping a half.
            List<string> managed = ManagedVerdicts(cands, cands.Length, boxes, boxes.Length, out _);
            bool anyPartial = managed.Exists(row => row.EndsWith(":1") || row.EndsWith(":2") || row.EndsWith(":3"));
            Assert.IsTrue(anyPartial,
                "precondition: this scene must produce at least one partially-placed pair, or the differential " +
                "is only re-checking the mask-0 path");
        }

        // Mask 0 everywhere must be byte-identical to the pre-stage-C behaviour: an all-or-nothing pair scene
        // records NO per-half verdict, and one blocked box still drops the whole candidate.
        [Test]
        public void UnmaskedPairs_StayAllOrNothing_AndRecordNoDroppedMask()
        {
            var (cands, boxes) = RandomMaskedPairScene(40, 40, 7);
            for (int i = 0; i < cands.Length; i++) cands[i].OptionalBoxMask = 0;
            AssertSame(cands, boxes, "unmasked pairs (the pre-stage-C reference shape)");

            List<string> managed = ManagedVerdicts(cands, cands.Length, boxes, boxes.Length, out List<int> survivors);
            Assert.IsTrue(managed.TrueForAll(row => row.EndsWith(":0")),
                "a candidate with no optional half must never record a DroppedBoxMask");
            Assert.Greater(survivors.Count, 0, "sanity: the scene is not degenerate — something placed");
        }

        // Regression (live-demo crash at a dense scene): the node pool is pre-sized on the MAIN thread
        // (LabelCollisionGridSizing, managed float) but filled by the job in BURST — a coordinate on a cell
        // boundary can truncate one cell wider in Burst than the managed sizing counted, so the job needs one
        // more node than the pool holds. A Burst job CANNOT grow a NativeArray (the retired managed grid could,
        // so it never overflowed), and an under-count was an out-of-range WRITE → IndexOutOfRangeException from
        // LabelCollisionJob.Insert, crashing the frame every time at that scene. Insert now guards every write
        // against NodeBox.Length. This forces the under-count directly (a starved pool over a scene that places
        // many boxes) and asserts the job COMPLETES instead of throwing. RED without the guard: NodeBox[cap]
        // write throws. Survivors stay correct here because the boxes are disjoint (a dropped node only removes
        // a blocker prefilter entry — disjoint boxes never block anyway).
        [Test]
        public void StarvedNodePool_GuardsInsteadOfThrowing()
        {
            // 12 disjoint single-cell boxes on a coarse grid → all place, each inserts ≥1 node (≥12 total).
            const int n = 12;
            var cands = new LabelCandidate[n];
            var boxes = new LabelBox[n];
            for (int i = 0; i < n; i++)
            {
                float x = i * 500f, y = i * 500f; // far apart → disjoint → all survive
                boxes[i] = new LabelBox { Min = new float2(x, y), Max = new float2(x + 20f, y + 12f),
                    SortKey = 0, FeatureIndex = i, TileKey = 0, LabelIndex = i };
                cands[i] = new LabelCandidate { BoxStart = i, BoxCount = 1, SortKey = 0,
                    FeatureIndex = i, TileKey = 0, LabelIndex = i };
            }

            var nc = new NativeArray<LabelCandidate>(n, Allocator.TempJob);
            var nb = new NativeArray<LabelBox>(n, Allocator.TempJob);
            var ns = new NativeArray<byte>(n, Allocator.TempJob);
            var outCount = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < n; i++) { nc[i] = cands[i]; nb[i] = boxes[i]; }
                LabelCollisionGridSizing.Dims dims = LabelCollisionGridSizing.ComputeDims(nb, n);
                int cells = dims.W * dims.H;
                var cellHead = new NativeArray<int>(cells, Allocator.TempJob);
                var nodeBox  = new NativeArray<int>(3, Allocator.TempJob); // STARVED: 3 nodes for ≥12 inserts
                var nodeNext = new NativeArray<int>(3, Allocator.TempJob);
                try
                {
                    for (int c = 0; c < cells; c++) cellHead[c] = -1;
                    Assert.DoesNotThrow(() =>
                        new LabelCollisionJob
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
            var nb = new NativeArray<LabelBox>(boxes.Length, Allocator.TempJob);
            try
            {
                for (int i = 0; i < boxes.Length; i++) nb[i] = boxes[i];
                LabelCollisionGridSizing.Dims dims = LabelCollisionGridSizing.ComputeDims(nb, boxes.Length);
                int bound = LabelCollisionGridSizing.NodeUpperBound(nb, boxes.Length, in dims);

                int tight = 0;
                for (int i = 0; i < boxes.Length; i++)
                {
                    LabelBox b = nb[i];
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
        // be referenced by more than one candidate. The job (LabelCollisionJob.Insert) inserts every box in every
        // placed candidate's [BoxStart,BoxStart+BoxCount) range, so a SHARED box is inserted once PER candidate.
        // The old per-UNIQUE-box bound (NodeUpperBound) counts it once → under-count → pool overflow. Even the
        // ±1-cell margin only raises the overflow THRESHOLD; enough sharing still overflows it (proven here).
        // NodeUpperBoundByCandidates counts per reference (matches the job), so it scales with the sharing.
        [Test]
        public void SharedBox_PerCandidateBound_CoversJobInserts_PerUniqueUndercounts()
        {
            // One 1-cell box referenced by MANY AllowOverlap candidates → the job inserts it once per candidate.
            const int shares = 10;
            var boxes = new NativeArray<LabelBox>(1, Allocator.TempJob);
            var cands = new NativeArray<LabelCandidate>(shares, Allocator.TempJob);
            var ns = new NativeArray<byte>(shares, Allocator.TempJob);
            var outCount = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                boxes[0] = new LabelBox { Min = new float2(10f, 10f), Max = new float2(30f, 22f),
                    SortKey = 0, FeatureIndex = 0, TileKey = 0, LabelIndex = 0 }; // < 64px → 1 cell
                for (int i = 0; i < shares; i++)
                    cands[i] = new LabelCandidate { BoxStart = 0, BoxCount = 1, AllowOverlap = true,
                        SortKey = 0, FeatureIndex = i, TileKey = 0, LabelIndex = i };

                LabelCollisionGridSizing.Dims dims = LabelCollisionGridSizing.ComputeDims(boxes, 1);
                int perUnique = LabelCollisionGridSizing.NodeUpperBound(boxes, 1, in dims);
                int perCand   = LabelCollisionGridSizing.NodeUpperBoundByCandidates(cands, shares, boxes, 1, in dims);
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
                    new LabelCollisionJob
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
    }
}
