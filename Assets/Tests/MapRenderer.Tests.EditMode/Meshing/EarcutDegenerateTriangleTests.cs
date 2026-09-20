using NUnit.Framework;
using MapRenderer.Jobs.Fill;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Unit tests for <see cref="EarcutJob.PointInTriangle"/>'s degenerate-candidate branch (UMR-106
    /// Stage 1). A degenerate (collinear) candidate triangle is a segment, not the whole plane through
    /// it — these pin the bounding-box containment test that replaces the bare cross-product sign check
    /// on that branch.
    ///
    /// This calls the managed-IL copy of the job's method directly (a direct static call is not
    /// Burst-compiled), so it is not itself a Burst-kernel tooth. The Burst-compiled half is covered by
    /// <c>FillGraphBurstProbeTests</c> plus the <c>run-tests.sh</c> log grep for Burst compile errors
    /// (docs/job-scheduling-design.md §7).
    /// </summary>
    public class EarcutDegenerateTriangleTests
    {
        [Test]
        public void DegenerateTriangle_PointFarOnSharedLine_IsNotContained()
        {
            // A=(3,-8) B=(3,-8) C=(-3,-8): collinear, so the point set is the segment [-3,3] x {-8}.
            // P=(-11,-8) lies on that line but well outside the segment — the lead's witness.
            Assert.IsFalse(EarcutJob.PointInTriangle(3, -8, 3, -8, -3, -8, -11, -8));
        }

        [Test]
        public void DegenerateTriangle_PointOnTheHullSegment_IsContained()
        {
            // Same degenerate candidate as above; P=(0,-8) lies ON the segment [-3,3] x {-8}. This is the
            // clause that distinguishes the exact fix from the "a degenerate candidate is always an ear"
            // shortcut — without it, the fix collapses into that shortcut.
            Assert.IsTrue(EarcutJob.PointInTriangle(3, -8, 3, -8, -3, -8, 0, -8));
        }

        [Test]
        public void NonDegenerateTriangle_InsideAndOutsidePoints_AreUnchanged()
        {
            // A proper triangle (0,0) (4,0) (0,4): the fix must not touch the ordinary path.
            Assert.IsTrue(EarcutJob.PointInTriangle(0, 0, 4, 0, 0, 4, 1, 1));
            Assert.IsFalse(EarcutJob.PointInTriangle(0, 0, 4, 0, 0, 4, 5, 5));
        }

        [Test]
        public void DegenerateTriangle_OnDiagonalHull_BoundingBoxIsTwoDimensional()
        {
            // A=(0,0) B=(2,2) C=(4,4): collinear on y=x, so both the x- and y-bounds of the box are live —
            // an axis-aligned witness alone can't catch a bug in either comparison.
            Assert.IsFalse(EarcutJob.PointInTriangle(0, 0, 2, 2, 4, 4, 10, 10));
            Assert.IsTrue(EarcutJob.PointInTriangle(0, 0, 2, 2, 4, 4, 1, 1));
        }
    }
}
