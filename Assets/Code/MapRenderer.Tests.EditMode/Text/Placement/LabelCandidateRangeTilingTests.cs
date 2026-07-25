// Engine-free (pure Core types + NUnit) — shared verbatim between the Unity EditMode runner and the fast
// dotnet core-tests project. Covers LabelCandidate.TryFindRangeTilingViolation, the debug-invariant check that
// LabelPlacementSystem.ScheduleCollision asserts each frame: the staged candidate box-ranges must tile
// [0,boxCount) contiguously (StagePoint/StageCurvedAnchor append one candidate's boxes at a time — never sharing
// or skipping).

using System;
using NUnit.Framework;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class LabelCandidateRangeTilingTests
    {
        private static LabelCandidate Cand(int boxStart, int boxCount) =>
            new LabelCandidate { BoxStart = boxStart, BoxCount = boxCount };

        [Test]
        public void ContiguousTiling_PointAndCurvedMix_NoViolation()
        {
            // point(1) + curved(3) + point(1) + curved(2) → tiles [0,7) with no gap/overlap.
            var cands = new[] { Cand(0, 1), Cand(1, 3), Cand(4, 1), Cand(5, 2) };
            Assert.IsFalse(LabelCandidate.TryFindRangeTilingViolation(cands, 7, out int i, out int expected),
                $"contiguous ranges must not report a violation (got index {i}, expected {expected})");
        }

        [Test]
        public void Empty_NoViolation()
        {
            Assert.IsFalse(LabelCandidate.TryFindRangeTilingViolation(ReadOnlySpan<LabelCandidate>.Empty, 0, out _, out _));
        }

        [Test]
        public void OverRangeByOne_ReportedAtOffendingCandidate()
        {
            // The original live signature: the last candidate's range ends at boxCount+? — references a box
            // index == boxCount, one past the written pool [0,boxCount).
            var cands = new[] { Cand(0, 1), Cand(1, 1), Cand(2, 1) }; // claims boxes [0,3) but pool has 2
            Assert.IsTrue(LabelCandidate.TryFindRangeTilingViolation(cands, 2, out int i, out int expected));
            Assert.AreEqual(2, i, "the over-range candidate is the third one");
            Assert.AreEqual(2, expected, "it should have started at offset 2 (which it does) but overruns boxCount=2");
        }

        [Test]
        public void SharedBox_TwoCandidatesSameStart_ReportedAsNonContiguous()
        {
            // Two candidates referencing box 0 (the 'shared box' theory) — the second breaks contiguity: its
            // BoxStart(0) != the running offset(1).
            var cands = new[] { Cand(0, 1), Cand(0, 1) };
            Assert.IsTrue(LabelCandidate.TryFindRangeTilingViolation(cands, 2, out int i, out int expected));
            Assert.AreEqual(1, i);
            Assert.AreEqual(1, expected, "the second candidate should have started at offset 1, not 0");
        }

        [Test]
        public void Undercover_WellFormedButLeavesGap_ReportedAtLength()
        {
            // Ranges are individually valid and contiguous but stop short of boxCount → a gap.
            var cands = new[] { Cand(0, 1), Cand(1, 1) };
            Assert.IsTrue(LabelCandidate.TryFindRangeTilingViolation(cands, 5, out int i, out int expected));
            Assert.AreEqual(cands.Length, i, "an under-cover is flagged at index == candidate count");
            Assert.AreEqual(2, expected, "coverage reached only offset 2 of boxCount 5");
        }

        [Test]
        public void NegativeBoxCount_Reported()
        {
            var cands = new[] { Cand(0, 1), Cand(1, -1) };
            Assert.IsTrue(LabelCandidate.TryFindRangeTilingViolation(cands, 1, out int i, out _));
            Assert.AreEqual(1, i);
        }

        [Test]
        public void FirstCandidateNotAtZero_Reported()
        {
            var cands = new[] { Cand(1, 1) };
            Assert.IsTrue(LabelCandidate.TryFindRangeTilingViolation(cands, 2, out int i, out int expected));
            Assert.AreEqual(0, i);
            Assert.AreEqual(0, expected, "the first candidate must start at box 0");
        }
    }
}
