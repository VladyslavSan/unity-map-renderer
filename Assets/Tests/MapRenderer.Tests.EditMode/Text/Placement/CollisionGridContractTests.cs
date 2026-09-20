// Unity EditMode only — needs the job runtime (NativeArray / IJob). NOT registered in core-tests.csproj.
// NOTE: Burst compiles CollisionJob only when Jobs > Burst > Enable Compilation is on AND it compiles — a
// compile failure falls back to managed IL SILENTLY (FillGraphBurstProbeTests), so the runner alone doesn't
// decide it. In this project's practice ./Tools/run-tests.sh (batch mode, confirmed via its log) is the
// Burst-compiled path; the interactive Editor Test Runner is not verified that way.
//
// This is the SOLE test coverage of CollisionGridSizing (ComputeDims / NodeUpperBound /
// NodeUpperBoundByCandidates, the last of which is the live production call at
// SymbolPlacementSystem.cs:732) — do not delete it as redundant with the placement teeth below.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs.Symbols;
using static MapRenderer.Tests.TestSupport.NativeCollisionRunner;

namespace MapRenderer.Tests.Text.Placement
{
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
}
