// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Blocker B1 (salvaged from reverted 6e39e282): <see cref="LabelStagingMath.SanitizeSortKey"/> normalizes a
    /// non-finite baked <c>symbol-sort-key</c> to <c>float.MaxValue</c> (sorts LAST) so
    /// <see cref="LabelCollision.ComparePlacementOrder(in LabelCandidate, in LabelCandidate)"/> stays a strict total
    /// order. A NaN key is intransitive → the unstable heapsort's survivor set becomes input-order dependent; this
    /// pins the choke-point normalization. (The internal helper is reached via <c>InternalsVisibleTo</c> in the
    /// EditMode runner, and directly in the same-assembly Tools/core-tests build.)
    /// </summary>
    [TestFixture]
    public class LabelStagingMathSortKeyTests
    {
        [Test]
        public void SanitizeSortKey_NonFinite_NormalizesToMaxValue()
        {
            Assert.AreEqual(float.MaxValue, LabelStagingMath.SanitizeSortKey(float.NaN),
                "NaN must normalize to float.MaxValue (sorts last — never preempts a good label)");
            Assert.AreEqual(float.MaxValue, LabelStagingMath.SanitizeSortKey(float.PositiveInfinity),
                "+Inf must normalize to float.MaxValue");
            Assert.AreEqual(float.MaxValue, LabelStagingMath.SanitizeSortKey(float.NegativeInfinity),
                "-Inf must normalize to float.MaxValue");
        }

        [Test]
        public void SanitizeSortKey_FiniteValues_PassThroughUnchanged()
        {
            // The whole finite range is preserved verbatim — an impl using a strict `< MaxValue` threshold (instead
            // of the correct `<= MaxValue`) would still normalize NaN/Inf above but would re-sentinel the largest
            // finite key. |MaxValue| <= MaxValue is true, so the largest finite key must survive.
            Assert.AreEqual(0f, LabelStagingMath.SanitizeSortKey(0f), "zero unchanged");
            Assert.AreEqual(1.5f, LabelStagingMath.SanitizeSortKey(1.5f), "a finite value unchanged");
            Assert.AreEqual(-42f, LabelStagingMath.SanitizeSortKey(-42f), "a finite negative value unchanged");
            Assert.AreEqual(float.MaxValue, LabelStagingMath.SanitizeSortKey(float.MaxValue),
                "float.MaxValue is finite (|MaxValue| <= MaxValue) — it must pass through, not be re-sentinelled");
            Assert.AreEqual(float.MinValue, LabelStagingMath.SanitizeSortKey(float.MinValue),
                "float.MinValue (most-negative finite) is finite and must pass through unchanged");
        }
    }
}
