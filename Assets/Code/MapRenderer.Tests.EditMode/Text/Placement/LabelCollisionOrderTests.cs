// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Blocker B1 (salvaged from reverted 6e39e282) — the real teeth for <c>LabelStagingMath.SanitizeSortKey</c>:
    /// a NON-FINITE baked <c>symbol-sort-key</c> makes
    /// <see cref="LabelCollision.ComparePlacementOrder(in LabelCandidate, in LabelCandidate)"/> intransitive (NaN
    /// compares false both ways, so the sort-key branch never ties and the fall-through feature/tile order can form a
    /// 3-cycle that contradicts the sort-key order). The unstable heapsort's survivor set then depends on input
    /// order. Staging the SAME labels through <see cref="LabelStagingMath.StagePoint"/> in two MIRROR-ORDER
    /// permutations must yield the IDENTICAL survivor set — which only holds once the NaN is normalized to
    /// <c>float.MaxValue</c> at candidate build. RED-verified: feeding raw <c>s.SortKey</c> (fix reverted) makes the
    /// two orders diverge; the sanitizer restores determinism.
    /// </summary>
    [TestFixture]
    public class LabelCollisionOrderTests
    {
        // One logical, order-independent label identity: its FeatureIndex (StagePoint's LabelIndex = ordinal is
        // NOT stable across the two staging orders, but FeatureIndex is carried verbatim per label).
        private readonly struct LabelSpec
        {
            public readonly float SortKey;
            public readonly int FeatureIndex;
            public LabelSpec(float sortKey, int featureIndex) { SortKey = sortKey; FeatureIndex = featureIndex; }
        }

        // The intransitive core: two finite candidates whose SortKey order (L_lowKey < L_highKey) CONTRADICTS their
        // FeatureIndex order (feat_highKey < feat_nan < feat_lowKey), with the NaN candidate's FeatureIndex sitting
        // BETWEEN them. With a NaN key the comparator forms a 3-cycle over {lowKey, nan, highKey}. Plus two extra
        // finite candidates so the heapsort has enough elements to reorder differently per input permutation.
        private static readonly LabelSpec[] Specs =
        {
            new LabelSpec(sortKey: 1f, featureIndex: 10),          // low key  → highest priority; high feature
            new LabelSpec(sortKey: float.NaN, featureIndex: 5),    // the poison: non-finite key, mid feature
            new LabelSpec(sortKey: 5f, featureIndex: 2),           // high key → low priority; low feature
            new LabelSpec(sortKey: 2f, featureIndex: 8),           // filler
            new LabelSpec(sortKey: 3f, featureIndex: 0),           // filler
        };

        // Stage `specs` (in the given array order) as fully-overlapping point labels through StagePoint, then run the
        // production candidate collision pass. Returns the survivor set keyed by the stable FeatureIndex. All labels
        // share one screen position + bounds ⇒ they all overlap ⇒ exactly the sorted-first survives; so a change in
        // the sorted order (the intransitive-NaN symptom) changes WHICH feature survives.
        private static HashSet<int> SurvivorsByFeature(LabelSpec[] specs)
        {
            int n = specs.Length;
            var boxes = new LabelBox[n];
            var quads = new PlacedQuad[n];
            var candidates = new LabelCandidate[n];
            var emit = new CandidateEmit[n];
            int boxCount = 0, quadCount = 0;
            var quad = new[] { UnitCell() };

            for (int i = 0; i < n; i++)
            {
                var s = new PointStageInput
                {
                    ScreenPx = new float2(100, 100), Depth = 0f, Projected = true,
                    BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                    TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f,
                    SortKey = specs[i].SortKey, FeatureIndex = specs[i].FeatureIndex, TileKey = 0L, Slot = 0,
                    TranslateAnchor = TextTranslateAnchor.Viewport, RotationAlignment = AlignmentMode.Viewport,
                    Color = new float4(1, 1, 1, 1),
                };
                int staged = LabelStagingMath.StagePoint(in s, quad, bearingRadians: 0f,
                    viewportLogicalPx: new double2(1920, 1080), ordinal: i,
                    boxes, ref boxCount, quads, ref quadCount, candidates, emit);
                Assert.AreEqual(1, staged, "each fully-in-view point label must stage");
            }

            var survivorFlags = new bool[n];
            var grid = new LabelCollisionGrid();
            LabelCollision.SelectSurvivors(candidates, n, boxes, boxCount, survivorFlags, grid);

            var survivors = new HashSet<int>();
            for (int i = 0; i < n; i++)
                if (survivorFlags[i]) survivors.Add(candidates[i].FeatureIndex);
            return survivors;
        }

        [Test]
        public void NonFiniteSortKey_ProducesDeterministicSurvivors()
        {
            var forward = (LabelSpec[])Specs.Clone();
            var mirrored = (LabelSpec[])Specs.Clone();
            System.Array.Reverse(mirrored);

            HashSet<int> forwardSurvivors = SurvivorsByFeature(forward);
            HashSet<int> mirroredSurvivors = SurvivorsByFeature(mirrored);

            CollectionAssert.AreEquivalent(forwardSurvivors, mirroredSurvivors,
                "the survivor set must be independent of input order — the NaN sort-key candidate is sanitized to " +
                "float.MaxValue at StagePoint, so ComparePlacementOrder stays a strict total order (without the fix " +
                "the intransitive NaN comparator makes the unstable heapsort's winner mirror-order dependent)");

            // Positive pin: all labels overlap ⇒ exactly one survives, and it is the finite highest-priority label
            // (lowest SortKey = 1f, FeatureIndex 10). The poison NaN candidate is normalized to sort LAST, never
            // preempting a good label.
            Assert.AreEqual(1, forwardSurvivors.Count, "all overlap ⇒ a single survivor");
            CollectionAssert.AreEquivalent(new[] { 10 }, forwardSurvivors,
                "the lowest finite SortKey (highest priority) wins; the NaN candidate sorts last and loses");
        }

        private static SymbolQuad UnitCell() => new SymbolQuad
        {
            TopLeft = new float2(-6, 6), BottomRight = new float2(6, -6),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };
    }
}
