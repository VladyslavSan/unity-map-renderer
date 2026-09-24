// Unity EditMode only — needs the job runtime (NativeArray / IJob). NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs.Symbols;

namespace MapRenderer.Tests.TestSupport
{
    /// <summary>
    /// Runs <see cref="CollisionJob"/> over managed <see cref="SymbolCandidate"/>/<see cref="SymbolBox"/>
    /// arrays — the one native-collision entry point every collision fixture drives. Sizes the
    /// pre-allocated grid via <see cref="CollisionGridSizing"/>, schedules the job, and copies the sorted
    /// candidates and survivor flags back into the caller's arrays — the job sorts <c>Candidates</c> in
    /// place, so the caller's array order changes too.
    /// </summary>
    public static class NativeCollisionRunner
    {
        /// <summary>
        /// Runs <see cref="CollisionJob"/> over <paramref name="cands"/>/<paramref name="boxes"/>, writing
        /// the sorted candidate order back into <paramref name="cands"/> and the per-sorted-position
        /// survivor flag into <paramref name="survivor"/>. Returns the survivor count.
        /// </summary>
        public static int RunCollision(SymbolCandidate[] cands, int candCount, SymbolBox[] boxes, int boxCount,
            bool[] survivor)
        {
            var nc = new NativeArray<SymbolCandidate>(candCount, Allocator.TempJob);
            var nb = new NativeArray<SymbolBox>(boxCount, Allocator.TempJob);
            var ns = new NativeArray<byte>(candCount, Allocator.TempJob);
            var outCount = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int i = 0; i < candCount; i++) nc[i] = cands[i];
                for (int i = 0; i < boxCount; i++) nb[i] = boxes[i];

                CollisionGridSizing.Dims dims = CollisionGridSizing.ComputeDims(nb, boxCount);
                int cells = dims.W * dims.H;
                int nodeCap = math.max(1,
                    CollisionGridSizing.NodeUpperBoundByCandidates(nc, candCount, nb, boxCount, in dims));
                var cellHead = new NativeArray<int>(cells, Allocator.TempJob);
                var nodeBox = new NativeArray<int>(nodeCap, Allocator.TempJob);
                var nodeNext = new NativeArray<int>(nodeCap, Allocator.TempJob);
                try
                {
                    for (int c = 0; c < cells; c++) cellHead[c] = -1;
                    new CollisionJob
                    {
                        Candidates = nc, CandidateCount = candCount, Boxes = nb, BoxCount = boxCount,
                        Survivors = ns, OutSurvivorCount = outCount,
                        CellHead = cellHead, NodeBox = nodeBox, NodeNext = nodeNext,
                        GridMinX = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                        GridW = dims.W, GridH = dims.H,
                    }.Schedule().Complete();

                    int survivorCount = 0;
                    for (int i = 0; i < candCount; i++)
                    {
                        cands[i] = nc[i];
                        survivor[i] = ns[i] != 0;
                        if (survivor[i]) survivorCount++;
                    }
                    Assert.AreEqual(survivorCount, outCount[0], "OutSurvivorCount must match the flags");
                    return outCount[0];
                }
                finally { cellHead.Dispose(); nodeBox.Dispose(); nodeNext.Dispose(); }
            }
            finally { nc.Dispose(); nb.Dispose(); ns.Dispose(); outCount.Dispose(); }
        }

        /// <summary>
        /// Independent O(n²) verifier of the greedy collision contract over a JOB OUTPUT — never a second
        /// greedy pass. Over the sorted candidates and survivor flags from <see cref="RunCollision"/>, it
        /// checks distinct <see cref="SymbolCandidate.FeatureIndex"/> (the permutation check), the order,
        /// and per candidate: suppressed, soundness, completeness and
        /// <see cref="SymbolCandidate.DroppedBoxMask"/>.
        /// </summary>
        public static void AssertGreedyContract(SymbolCandidate[] sortedCands, int candCount, SymbolBox[] boxes,
            bool[] survivor, string what)
        {
            // Pass 1 — distinct FeatureIndex, which is also the permutation check: a swap-based heapsort
            // loses an element only by duplicating another, and generators give each candidate its own index.
            var seen = new HashSet<int>();
            for (int i = 0; i < candCount; i++)
                Assert.IsTrue(seen.Add(sortedCands[i].FeatureIndex),
                    $"duplicate FeatureIndex {sortedCands[i].FeatureIndex} at sorted index {i} ({what})");

            // Pass 2 — Order: every adjacent pair is non-decreasing under the live comparator.
            for (int i = 0; i + 1 < candCount; i++)
                Assert.LessOrEqual(
                    SymbolCollision.ComparePlacementOrder(in sortedCands[i], in sortedCands[i + 1]), 0,
                    $"sorted position {i}/{i + 1} out of placement order ({what})");

            // Pass 3 — per candidate, in index order: Suppressed -> Soundness -> Completeness -> Mask.
            for (int i = 0; i < candCount; i++)
            {
                SymbolCandidate c = sortedCands[i];
                int start = c.BoxStart, end = c.BoxStart + c.BoxCount;
                bool placed = survivor[i];

                if (c.Suppressed)
                    Assert.IsFalse(placed, $"suppressed candidate {c.SymbolIndex} must never place ({what})");

                if (placed && !c.AllowOverlap)
                {
                    for (int b = start; b < end; b++)
                    {
                        int bit = 1 << (b - start);
                        if ((c.OptionalBoxMask & bit) != 0) continue; // optional half — Mask covers it
                        Assert.IsFalse(OverlapsAnyBlocker(in boxes[b], sortedCands, boxes, survivor, i),
                            $"candidate {c.SymbolIndex} placed with required box {b - start} overlapping a blocker ({what})");
                    }
                }
                else if (!placed && !c.Suppressed && !c.AllowOverlap)
                {
                    bool anyRequiredOverlap = false;
                    for (int b = start; b < end; b++)
                    {
                        int bit = 1 << (b - start);
                        if ((c.OptionalBoxMask & bit) != 0) continue;
                        if (OverlapsAnyBlocker(in boxes[b], sortedCands, boxes, survivor, i)) { anyRequiredOverlap = true; break; }
                    }
                    Assert.IsTrue(anyRequiredOverlap,
                        $"candidate {c.SymbolIndex} dropped with no required box overlapping any blocker ({what})");
                }

                if (c.OptionalBoxMask == 0)
                    Assert.AreEqual(0, c.DroppedBoxMask,
                        $"candidate {c.SymbolIndex} has no optional half but a nonzero DroppedBoxMask ({what})");
                if (c.Suppressed) continue; // no mask write for a suppressed candidate

                if (placed && c.AllowOverlap)
                {
                    Assert.AreEqual(0, c.DroppedBoxMask,
                        $"allow-overlap candidate {c.SymbolIndex} must never record a dropped optional half ({what})");
                }
                else if (placed)
                {
                    for (int b = start; b < end; b++)
                    {
                        int bit = 1 << (b - start);
                        bool overlaps = OverlapsAnyBlocker(in boxes[b], sortedCands, boxes, survivor, i);
                        if ((c.OptionalBoxMask & bit) == 0)
                            Assert.IsFalse(overlaps,
                                $"candidate {c.SymbolIndex} placed with required box {b - start} overlapping a blocker ({what})");
                        else
                            Assert.AreEqual(overlaps, (c.DroppedBoxMask & bit) != 0,
                                $"candidate {c.SymbolIndex} box {b - start}: DroppedBoxMask bit must equal whether the box overlapped a blocker ({what})");
                    }
                }
                else
                {
                    Assert.AreEqual(0, c.DroppedBoxMask,
                        $"dropped candidate {c.SymbolIndex} must record no per-half verdict ({what})");
                }
            }
        }

        // The blocker set at sorted position `uptoExclusive`: boxes inserted by earlier placed,
        // non-ignoring candidates, excluding their dropped optional halves.
        private static bool OverlapsAnyBlocker(in SymbolBox target, SymbolCandidate[] sortedCands, SymbolBox[] boxes,
            bool[] survivor, int uptoExclusive)
        {
            for (int j = 0; j < uptoExclusive; j++)
            {
                if (!survivor[j] || sortedCands[j].IgnorePlacement) continue;
                SymbolCandidate cj = sortedCands[j];
                for (int b = cj.BoxStart; b < cj.BoxStart + cj.BoxCount; b++)
                {
                    int bit = 1 << (b - cj.BoxStart);
                    if ((cj.DroppedBoxMask & bit) != 0) continue;
                    if (SymbolCollision.Overlaps(in target, in boxes[b])) return true;
                }
            }
            return false;
        }

        // 1-box (point-like) candidates exercise the grid. The box size range lets a case force wide boxes
        // (many cells) or a giant span (cell enlargement).
        internal static (SymbolCandidate[], SymbolBox[]) RandomScene(int count, int seed, float worldW, float worldH,
            float wMin, float wMax, float hMin, float hMax)
        {
            var rng = new System.Random(seed);
            var cands = new SymbolCandidate[count];
            var boxes = new SymbolBox[count];
            for (int i = 0; i < count; i++)
            {
                float x = (float)(rng.NextDouble() * worldW);
                float y = (float)(rng.NextDouble() * worldH);
                float w = wMin + (float)(rng.NextDouble() * (wMax - wMin));
                float h = hMin + (float)(rng.NextDouble() * (hMax - hMin));
                boxes[i] = new SymbolBox
                {
                    Min = new float2(x, y), Max = new float2(x + w, y + h),
                    SortKey = rng.Next(0, 6), FeatureIndex = i, TileKey = rng.Next(0, 4), SymbolIndex = i,
                };
                cands[i] = new SymbolCandidate
                {
                    BoxStart = i, BoxCount = 1, SortKey = boxes[i].SortKey, FeatureIndex = i,
                    TileKey = boxes[i].TileKey, SymbolIndex = i,
                };
            }
            return (cands, boxes);
        }
    }
}
