// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Top-level `using Unity.Mathematics;` + unqualified float2 (namespace-collision trap —
// see LabelBox.cs's header comment).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// #5 (B3) — the multi-box, all-or-nothing UNIFIED collision
    /// (<see cref="LabelCollision.SelectSurvivors(LabelCandidate[],int,LabelBox[],int,bool[],LabelCollisionGrid)"/>):
    /// a curved along-line label is ONE candidate spanning N glyph boxes; it places iff EVERY box is free and,
    /// when placed, blocks across its WHOLE run — competing with point (1-box) labels in the SAME greedy pass.
    /// Every assertion is OUTCOME-based (WHICH candidates survive, by <see cref="LabelCandidate.LabelIndex"/>).
    /// </summary>
    [TestFixture]
    public class LabelCandidateCollisionTests
    {
        // A tiny scene builder: each Add() appends one candidate over a contiguous run of the flat box pool.
        private sealed class Scene
        {
            private readonly List<LabelBox> _boxes = new List<LabelBox>();
            private readonly List<LabelCandidate> _candidates = new List<LabelCandidate>();

            public Scene Add(int labelIndex, float sortKey, int featureIndex,
                (float minX, float minY, float maxX, float maxY)[] rects,
                bool allowOverlap = false, bool ignorePlacement = false, long tileKey = 0L)
            {
                int start = _boxes.Count;
                foreach (var r in rects)
                    _boxes.Add(new LabelBox { Min = new float2(r.minX, r.minY), Max = new float2(r.maxX, r.maxY) });
                _candidates.Add(new LabelCandidate
                {
                    BoxStart = start,
                    BoxCount = rects.Length,
                    SortKey = sortKey,
                    FeatureIndex = featureIndex,
                    TileKey = tileKey,
                    AllowOverlap = allowOverlap,
                    IgnorePlacement = ignorePlacement,
                    LabelIndex = labelIndex,
                });
                return this;
            }

            public HashSet<int> Survivors()
            {
                LabelBox[] boxes = _boxes.ToArray();
                LabelCandidate[] cands = _candidates.ToArray();
                var flags = new bool[cands.Length];
                var grid = new LabelCollisionGrid();
                int n = LabelCollision.SelectSurvivors(cands, cands.Length, boxes, boxes.Length, flags, grid);

                var set = new HashSet<int>();
                for (int i = 0; i < cands.Length; i++) if (flags[i]) set.Add(cands[i].LabelIndex);
                Assert.AreEqual(n, set.Count, "returned survivor count must match the number of set flags");
                return set;
            }
        }

        // Convenience: a rect literal.
        private static (float, float, float, float)[] R(params (float, float, float, float)[] rects) => rects;

        // ── THE decisive tooth: all-or-nothing. ONE colliding glyph drops the WHOLE curved label; remove
        //    that one glyph and the identical label survives. ──────────────────────────────────────────────
        [Test]
        public void SelectSurvivors_CurvedLabel_DropsEntirelyWhenAnySingleGlyphCollides()
        {
            // Point P (best key) at region A; curved C's THIRD glyph reaches into region A, the rest are far away.
            var collides = new Scene()
                .Add(labelIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))                 // point, region A
                .Add(labelIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((100, 0, 110, 10), (112, 0, 122, 10), (5, 5, 15, 15)));                          // curved: glyph3 hits A
            CollectionAssert.AreEquivalent(new[] { 0 }, collides.Survivors(),
                "one curved glyph overlapping the placed point drops the ENTIRE curved label (all-or-nothing)");

            // Identical scene but the offending third glyph is moved clear → the curved label now places.
            var clears = new Scene()
                .Add(labelIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))
                .Add(labelIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((100, 0, 110, 10), (112, 0, 122, 10), (200, 0, 210, 10)));                       // glyph3 clear
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, clears.Survivors(),
                "with every glyph free the whole curved label places — proving the drop above was that one glyph");
        }

        // ── A placed curved label blocks across its WHOLE run — a later point overlapping ANY glyph drops. ─
        [Test]
        public void SelectSurvivors_PlacedCurvedLabel_BlocksAPointOverlappingAnyGlyph()
        {
            var scene = new Scene()
                .Add(labelIndex: 0, sortKey: 10f, featureIndex: 0,
                    rects: R((0, 0, 10, 10), (20, 0, 30, 10), (40, 0, 50, 10)))   // curved (best key), 3 glyphs
                .Add(labelIndex: 1, sortKey: 20f, featureIndex: 1, rects: R((22, 2, 28, 8)))   // point over glyph 2
                .Add(labelIndex: 2, sortKey: 30f, featureIndex: 2, rects: R((200, 0, 210, 10))); // point, disjoint
            CollectionAssert.AreEquivalent(new[] { 0, 2 }, scene.Survivors(),
                "the curved label blocks via its SECOND glyph (a whole-label single-AABB would miss the gap between glyphs)");
        }

        // ── No self-block: a curved label's own adjacent (mutually overlapping) glyph boxes must NOT block
        //    one another. Two such labels, disjoint from each other, BOTH place. ────────────────────────────
        [Test]
        public void SelectSurvivors_CurvedLabel_AdjacentGlyphsDoNotSelfBlock()
        {
            var scene = new Scene()
                .Add(labelIndex: 0, sortKey: 10f, featureIndex: 0,
                    rects: R((0, 0, 10, 10), (5, 0, 15, 10), (10, 0, 20, 10)))       // overlapping glyphs
                .Add(labelIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((100, 0, 110, 10), (105, 0, 115, 10)));                  // overlapping glyphs, disjoint region
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, scene.Survivors(),
                "adjacent glyph boxes of the same label overlap by design — a test-then-insert-all pass must " +
                "never let them self-block (an insert-as-you-go pass would wrongly drop both labels)");
        }

        // ── Curved-vs-point resolves by sort key, both directions. ──────────────────────────────────────────
        [Test]
        public void SelectSurvivors_CurvedVsPoint_LowerSortKeyWins()
        {
            var curvedWins = new Scene()
                .Add(labelIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 10)))   // curved-ish, best key
                .Add(labelIndex: 1, sortKey: 20f, featureIndex: 1, rects: R((5, 0, 25, 10)));   // point, overlaps
            CollectionAssert.AreEquivalent(new[] { 0 }, curvedWins.Survivors(),
                "the lower-sort-key candidate wins the collision regardless of placement kind");

            var pointWins = new Scene()
                .Add(labelIndex: 0, sortKey: 20f, featureIndex: 0, rects: R((0, 0, 20, 10)))
                .Add(labelIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((5, 0, 25, 10)));   // now the point wins
            CollectionAssert.AreEquivalent(new[] { 1 }, pointWins.Survivors(),
                "flipping the sort keys flips the survivor — the greedy order is key-driven, not kind-driven");
        }

        // ── Permutation invariance over MIXED point + curved input (matches the point-only guarantee). ──────
        [Test]
        public void SelectSurvivors_IsPermutationInvariant_OverMixedInput()
        {
            // region A cluster: C1 curved (best key) + C2 curved + P0 point all overlap; P3 point disjoint (region B).
            Scene Build(int order)
            {
                var p0 = (0, 30f, 0, R((0f, 0f, 20f, 20f)), false, false);
                var c1 = (1, 10f, 1, R((2f, 2f, 8f, 8f), (10f, 2f, 16f, 8f)), false, false);
                var c2 = (2, 20f, 2, R((4f, 4f, 10f, 10f)), false, false);
                var p3 = (3, 99f, 3, R((100f, 0f, 120f, 20f)), false, false);
                var items = new[] { p0, c1, c2, p3 };
                // three deterministic orderings
                int[][] orders = { new[] { 0, 1, 2, 3 }, new[] { 3, 2, 1, 0 }, new[] { 2, 0, 3, 1 } };
                var s = new Scene();
                foreach (int idx in orders[order])
                {
                    var it = items[idx];
                    s.Add(it.Item1, it.Item2, it.Item3, it.Item4, it.Item5, it.Item6);
                }
                return s;
            }

            var expected = new[] { 1, 3 }; // C1 wins region A; C2 + P0 overlap it and drop; P3 alone.
            CollectionAssert.AreEquivalent(expected, Build(0).Survivors());
            CollectionAssert.AreEquivalent(expected, Build(1).Survivors());
            CollectionAssert.AreEquivalent(expected, Build(2).Survivors(),
                "the survivor set must not depend on candidate insertion order (mixed point + curved)");
        }

        // ── Flags carry through the multi-box path: allow-overlap places unconditionally; ignore-placement
        //    places but does not block. ─────────────────────────────────────────────────────────────────────
        [Test]
        public void SelectSurvivors_AllowOverlapCurved_PlacesOverAPoint()
        {
            var scene = new Scene()
                .Add(labelIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))              // point, best key
                .Add(labelIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((2, 2, 8, 8), (5, 5, 15, 15)), allowOverlap: true);                          // curved, allow-overlap
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, scene.Survivors(),
                "an allow-overlap curved label skips the collision test entirely and always places");
        }

        [Test]
        public void SelectSurvivors_IgnorePlacementCurved_DoesNotBlockALaterPoint()
        {
            var scene = new Scene()
                .Add(labelIndex: 0, sortKey: 10f, featureIndex: 0,
                    rects: R((0, 0, 10, 10), (20, 0, 30, 10)), ignorePlacement: true)                     // curved, non-blocking
                .Add(labelIndex: 1, sortKey: 20f, featureIndex: 1, rects: R((22, 2, 28, 8)));             // point over glyph 2
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, scene.Survivors(),
                "an ignore-placement curved label is placed but none of its glyph boxes block a later label");
        }

        // ── DIFFERENTIAL: the unified path over ALL 1-box candidates reproduces the legacy single-box
        //    SelectSurvivors EXACTLY — so wrapping point labels as candidates changed nothing for them. ──────
        [Test]
        public void SelectSurvivors_AllSingleBoxCandidates_MatchLegacyPointPath()
        {
            var grid = new LabelCollisionGrid();
            int[] counts = { 1, 2, 3, 50, 500 };
            int[] seeds = { 1, 7, 42, 999 };
            foreach (int count in counts)
            foreach (int seed in seeds)
            {
                LabelBox[] baseBoxes = RandomBoxes(count, seed);

                // Legacy point path.
                var legacyBoxes = (LabelBox[])baseBoxes.Clone();
                var legacyFlags = new bool[count];
                LabelCollision.SelectSurvivors(legacyBoxes, count, legacyFlags, grid);
                var legacy = new HashSet<int>();
                for (int i = 0; i < count; i++) if (legacyFlags[i]) legacy.Add(legacyBoxes[i].LabelIndex);

                // Unified path: one 1-box candidate per box (boxes stay put, candidates carry the keys).
                var candBoxes = (LabelBox[])baseBoxes.Clone();
                var cands = new LabelCandidate[count];
                for (int i = 0; i < count; i++)
                    cands[i] = new LabelCandidate
                    {
                        BoxStart = i, BoxCount = 1,
                        SortKey = candBoxes[i].SortKey, FeatureIndex = candBoxes[i].FeatureIndex,
                        TileKey = candBoxes[i].TileKey, AllowOverlap = candBoxes[i].AllowOverlap,
                        IgnorePlacement = candBoxes[i].IgnorePlacement, LabelIndex = candBoxes[i].LabelIndex,
                    };
                var candFlags = new bool[count];
                LabelCollision.SelectSurvivors(cands, count, candBoxes, count, candFlags, grid);
                var unified = new HashSet<int>();
                for (int i = 0; i < count; i++) if (candFlags[i]) unified.Add(cands[i].LabelIndex);

                CollectionAssert.AreEquivalent(legacy, unified,
                    $"unified 1-box-candidate path must equal the legacy point path (count={count} seed={seed})");
            }
        }

        // Deterministic random boxes with unique LabelIndex/FeatureIndex; wide enough to span several grid
        // cells; small sort-key range so ties are common (feature index still totally orders them).
        private static LabelBox[] RandomBoxes(int count, int seed)
        {
            var rng = new System.Random(seed);
            var boxes = new LabelBox[count];
            for (int i = 0; i < count; i++)
            {
                float x = (float)(rng.NextDouble() * 2000.0);
                float y = (float)(rng.NextDouble() * 1200.0);
                float w = 30f + (float)(rng.NextDouble() * 270.0);
                float h = 10f + (float)(rng.NextDouble() * 40.0);
                boxes[i] = new LabelBox
                {
                    Min = new float2(x, y),
                    Max = new float2(x + w, y + h),
                    SortKey = rng.Next(0, 6),
                    FeatureIndex = i,
                    TileKey = rng.Next(0, 4),
                    LabelIndex = i,
                    AllowOverlap = false,
                    IgnorePlacement = false,
                };
            }
            return boxes;
        }
    }
}
