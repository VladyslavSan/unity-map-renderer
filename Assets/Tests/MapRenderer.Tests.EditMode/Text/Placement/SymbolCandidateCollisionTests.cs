// Unity EditMode only — runs the placement through NativeCollisionRunner (NativeArray/IJob). NOT
// registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs.Symbols;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// #5 (B3) — the multi-box, all-or-nothing UNIFIED collision (<see cref="CollisionJob"/>): a curved
    /// along-line symbol is ONE candidate spanning N glyph boxes; it places iff EVERY box is free and,
    /// when placed, blocks across its WHOLE run — competing with point (1-box) symbols in the SAME greedy
    /// pass. Every assertion is OUTCOME-based (WHICH candidates survive, by <see cref="SymbolCandidate.SymbolIndex"/>).
    /// </summary>
    [TestFixture]
    public class SymbolCandidateCollisionTests
    {
        // A tiny scene builder: each Add() appends one candidate over a contiguous run of the flat box pool.
        private sealed class Scene
        {
            private readonly List<SymbolBox> _boxes = new List<SymbolBox>();
            private readonly List<SymbolCandidate> _candidates = new List<SymbolCandidate>();

            public Scene Add(int symbolIndex, float sortKey, int featureIndex,
                (float minX, float minY, float maxX, float maxY)[] rects,
                bool allowOverlap = false, bool ignorePlacement = false, long tileKey = 0L, long fadeId = 0L)
            {
                int start = _boxes.Count;
                foreach (var r in rects)
                    _boxes.Add(new SymbolBox { Min = new float2(r.minX, r.minY), Max = new float2(r.maxX, r.maxY) });
                _candidates.Add(new SymbolCandidate
                {
                    BoxStart = start,
                    BoxCount = rects.Length,
                    SortKey = sortKey,
                    FeatureIndex = featureIndex,
                    TileKey = tileKey,
                    AllowOverlap = allowOverlap,
                    IgnorePlacement = ignorePlacement,
                    SymbolIndex = symbolIndex,
                    FadeId = fadeId,
                });
                return this;
            }

            // Optional A-5 kept-set: candidates whose SymbolIndex is in it are marked WasPlacedLastFrame (incumbents).
            public HashSet<int> Survivors(HashSet<int> keptSymbolIndices = null)
            {
                SymbolBox[] boxes = _boxes.ToArray();
                SymbolCandidate[] cands = _candidates.ToArray();
                if (keptSymbolIndices != null)
                    for (int i = 0; i < cands.Length; i++)
                        cands[i].WasPlacedLastFrame = keptSymbolIndices.Contains(cands[i].SymbolIndex);
                var flags = new bool[cands.Length];
                int n = NativeCollisionRunner.RunCollision(cands, cands.Length, boxes, boxes.Length, flags);

                var set = new HashSet<int>();
                for (int i = 0; i < cands.Length; i++) if (flags[i]) set.Add(cands[i].SymbolIndex);
                Assert.AreEqual(n, set.Count, "returned survivor count must match the number of set flags");
                return set;
            }
        }

        // Convenience: a rect literal.
        private static (float, float, float, float)[] R(params (float, float, float, float)[] rects) => rects;

        // ── THE decisive tooth: all-or-nothing. ONE colliding glyph drops the WHOLE curved symbol; remove
        //    that one glyph and the identical symbol survives. ──────────────────────────────────────────────
        [Test]
        public void Collision_CurvedSymbol_DropsEntirelyWhenAnySingleGlyphCollides()
        {
            // Point P (best key) at region A; curved C's THIRD glyph reaches into region A, the rest are far away.
            var collides = new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))                 // point, region A
                .Add(symbolIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((100, 0, 110, 10), (112, 0, 122, 10), (5, 5, 15, 15)));                          // curved: glyph3 hits A
            CollectionAssert.AreEquivalent(new[] { 0 }, collides.Survivors(),
                "one curved glyph overlapping the placed point drops the ENTIRE curved label (all-or-nothing)");

            // Identical scene but the offending third glyph is moved clear → the curved symbol now places.
            var clears = new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))
                .Add(symbolIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((100, 0, 110, 10), (112, 0, 122, 10), (200, 0, 210, 10)));                       // glyph3 clear
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, clears.Survivors(),
                "with every glyph free the whole curved label places — proving the drop above was that one glyph");
        }

        // ── A placed curved symbol blocks across its WHOLE run — a later point overlapping ANY glyph drops. ─
        [Test]
        public void Collision_PlacedCurvedSymbol_BlocksAPointOverlappingAnyGlyph()
        {
            var scene = new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0,
                    rects: R((0, 0, 10, 10), (20, 0, 30, 10), (40, 0, 50, 10)))   // curved (best key), 3 glyphs
                .Add(symbolIndex: 1, sortKey: 20f, featureIndex: 1, rects: R((22, 2, 28, 8)))   // point over glyph 2
                .Add(symbolIndex: 2, sortKey: 30f, featureIndex: 2, rects: R((200, 0, 210, 10))); // point, disjoint
            CollectionAssert.AreEquivalent(new[] { 0, 2 }, scene.Survivors(),
                "the curved label blocks via its SECOND glyph (a whole-label single-AABB would miss the gap between glyphs)");
        }

        // ── No self-block: a curved symbol's own adjacent (mutually overlapping) glyph boxes must NOT block
        //    one another. Two such symbols, disjoint from each other, BOTH place. ────────────────────────────
        [Test]
        public void Collision_CurvedSymbol_AdjacentGlyphsDoNotSelfBlock()
        {
            var scene = new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0,
                    rects: R((0, 0, 10, 10), (5, 0, 15, 10), (10, 0, 20, 10)))       // overlapping glyphs
                .Add(symbolIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((100, 0, 110, 10), (105, 0, 115, 10)));                  // overlapping glyphs, disjoint region
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, scene.Survivors(),
                "adjacent glyph boxes of the same label overlap by design — a test-then-insert-all pass must " +
                "never let them self-block (an insert-as-you-go pass would wrongly drop both labels)");
        }

        // ── Curved-vs-point resolves by sort key, both directions. ──────────────────────────────────────────
        [Test]
        public void Collision_CurvedVsPoint_LowerSortKeyWins()
        {
            var curvedWins = new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 10)))   // curved-ish, best key
                .Add(symbolIndex: 1, sortKey: 20f, featureIndex: 1, rects: R((5, 0, 25, 10)));   // point, overlaps
            CollectionAssert.AreEquivalent(new[] { 0 }, curvedWins.Survivors(),
                "the lower-sort-key candidate wins the collision regardless of placement kind");

            var pointWins = new Scene()
                .Add(symbolIndex: 0, sortKey: 20f, featureIndex: 0, rects: R((0, 0, 20, 10)))
                .Add(symbolIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((5, 0, 25, 10)));   // now the point wins
            CollectionAssert.AreEquivalent(new[] { 1 }, pointWins.Survivors(),
                "flipping the sort keys flips the survivor — the greedy order is key-driven, not kind-driven");
        }

        // ── Permutation invariance over MIXED point + curved input (matches the point-only guarantee). ──────
        [Test]
        public void Collision_IsPermutationInvariant_OverMixedInput()
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

        // ── STRICT TOTAL ORDER for a curved feature's repeated anchors: two overlapping candidates sharing
        //    (SortKey, FeatureIndex, TileKey) — a road's adjacent repeat anchors — must resolve DETERMINISTICALLY
        //    by FadeId, independent of input order. Without the FadeId final tiebreak they compare EQUAL; the
        //    unstable heapsort's tie-resolution then flips with input order, and the A-5 incumbency feedback drives
        //    a frame-to-frame limit cycle → a collision loser that never finishes fading ("line symbols, one won't
        //    fade" — stuck even with a still camera). RED against the pre-fix comparator (reversed input flips the
        //    survivor); GREEN once FadeId makes the order total. ──
        [Test]
        public void Collision_SameFeatureAnchors_ResolveDeterministicallyByFadeId()
        {
            // Two overlapping candidates, SAME sort key + feature + tile (one road's two repeat anchors), distinct FadeId.
            Scene Build(bool reversed)
            {
                var s = new Scene();
                if (reversed)
                {
                    s.Add(symbolIndex: 1, sortKey: 10f, featureIndex: 7, rects: R((5, 0, 25, 20)), tileKey: 3L, fadeId: 200L);
                    s.Add(symbolIndex: 0, sortKey: 10f, featureIndex: 7, rects: R((0, 0, 20, 20)), tileKey: 3L, fadeId: 100L);
                }
                else
                {
                    s.Add(symbolIndex: 0, sortKey: 10f, featureIndex: 7, rects: R((0, 0, 20, 20)), tileKey: 3L, fadeId: 100L);
                    s.Add(symbolIndex: 1, sortKey: 10f, featureIndex: 7, rects: R((5, 0, 25, 20)), tileKey: 3L, fadeId: 200L);
                }
                return s;
            }
            // The lower-FadeId candidate (symbolIndex 0, fadeId 100) wins — and it wins REGARDLESS of input order.
            CollectionAssert.AreEquivalent(new[] { 0 }, Build(false).Survivors(),
                "lower FadeId wins the same-(sort,feature,tile) tie");
            CollectionAssert.AreEquivalent(new[] { 0 }, Build(true).Survivors(),
                "…and the survivor is INDEPENDENT of input order — the tie is broken by FadeId, not the unstable " +
                "sort (pre-fix the greedy winner flips with input order, and the A-5 feedback oscillates it frame-to-frame)");
        }

        // ── Flags carry through the multi-box path: allow-overlap places unconditionally; ignore-placement
        //    places but does not block. ─────────────────────────────────────────────────────────────────────
        [Test]
        public void Collision_AllowOverlapCurved_PlacesOverAPoint()
        {
            var scene = new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))              // point, best key
                .Add(symbolIndex: 1, sortKey: 20f, featureIndex: 1,
                    rects: R((2, 2, 8, 8), (5, 5, 15, 15)), allowOverlap: true);                          // curved, allow-overlap
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, scene.Survivors(),
                "an allow-overlap curved label skips the collision test entirely and always places");
        }

        [Test]
        public void Collision_IgnorePlacementCurved_DoesNotBlockALaterPoint()
        {
            var scene = new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0,
                    rects: R((0, 0, 10, 10), (20, 0, 30, 10)), ignorePlacement: true)                     // curved, non-blocking
                .Add(symbolIndex: 1, sortKey: 20f, featureIndex: 1, rects: R((22, 2, 28, 8)));             // point over glyph 2
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, scene.Survivors(),
                "an ignore-placement curved label is placed but none of its glyph boxes block a later label");
        }

        // ── A 1-box candidate is exactly a point symbol: the unified path changed nothing for single-box
        //    symbols. Both the legacy box-only path and the differential this used to run against are gone
        //    (§2), so this is now a direct property — the same overlapping-cluster-plus-disjoint-label shape
        //    that pins the box-only comparator tooth, rebuilt as 1-box candidates through the same job. ──────
        [Test]
        public void Collision_AllSingleBoxCandidates_MatchLegacyPointPath()
        {
            var scene = new Scene()
                .Add(symbolIndex: 0, sortKey: 30f, featureIndex: 0, rects: R((0, 0, 20, 20)))     // region A
                .Add(symbolIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((2, 2, 18, 18)))     // region A (best key)
                .Add(symbolIndex: 2, sortKey: 20f, featureIndex: 2, rects: R((4, 4, 16, 16)))     // region A
                .Add(symbolIndex: 3, sortKey: 99f, featureIndex: 3, rects: R((100, 0, 120, 20))); // region B (disjoint)
            CollectionAssert.AreEquivalent(new[] { 1, 3 }, scene.Survivors(),
                "a 1-box candidate collides exactly like a point symbol: the best-key label in the " +
                "overlapping cluster survives, plus the disjoint label — the unified candidate path changes " +
                "nothing for single-box symbols");
        }

        // ── A-5 sticky-placement hysteresis (anti-flicker). Incumbency (WasPlacedLastFrame) breaks EQUAL-sort-key
        //    ties in favour of last frame's survivor, sitting below SortKey so it never blocks a higher-priority
        //    newcomer. Teeth verify: the fixed-point (no oscillation / B-1-compatible), the killed tiebreak flip,
        //    correct yielding, and within-frame determinism given a fixed kept-set. ──────────────────────────────

        // Helper: a Scene builder for a fixed geometry reused across kept-sets (each test rebuilds it fresh — the
        // Scene mutates its candidate array in Survivors()).
        private static Scene ClusterAB()  // two overlapping equal-sort-key symbols; feature(0) < feature(1)
            => new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))
                .Add(symbolIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((5, 0, 25, 20)));

        // ── FIXED POINT: feeding last frame's survivor set back in reproduces it exactly (idempotent). This is the
        //    anti-oscillation + B-1-compatibility proof: a static frame's survivors don't change under hysteresis. ──
        [Test]
        public void Collision_Hysteresis_IsFixedPoint()
        {
            var cold = ClusterAB().Survivors();                 // S = cold survivors (no history)
            var withHistory = ClusterAB().Survivors(cold);      // feed S back as the kept-set
            CollectionAssert.AreEquivalent(cold, withHistory,
                "kept == last frame's survivors must reproduce that same set (one-step fixed point → no oscillation)");
            // And a third pass over the second result is still S — genuinely stable, not a 2-cycle.
            CollectionAssert.AreEquivalent(cold, ClusterAB().Survivors(withHistory), "stable across a further step");
        }

        // ── KILLS THE TIEBREAK FLIP (the actual flicker): equal sort keys, decided cold by the arbitrary feature
        //    tiebreak — but whichever was placed last frame stays placed. This is the tile-churn/reprojection flip
        //    that A-5 exists to stop. ──
        [Test]
        public void Collision_Hysteresis_OverridesFeatureTiebreak_KeepingTheIncumbent()
        {
            CollectionAssert.AreEquivalent(new[] { 0 }, ClusterAB().Survivors(),
                "cold: the lower feature index (0) wins the equal-sort-key tie");
            CollectionAssert.AreEquivalent(new[] { 1 }, ClusterAB().Survivors(new HashSet<int> { 1 }),
                "kept={1}: incumbency overrides the feature tiebreak — the label placed last frame stays placed");
            CollectionAssert.AreEquivalent(new[] { 0 }, ClusterAB().Survivors(new HashSet<int> { 0 }),
                "kept={0}: the other incumbent likewise holds its slot (no flip either direction)");
        }

        // ── YIELDS TO A GENUINELY HIGHER-PRIORITY NEWCOMER: incumbency sits BELOW SortKey, so a strictly lower-
        //    SortKey newcomer still wins and the stale incumbent drops (no absolute lock). ──
        [Test]
        public void Collision_Hysteresis_YieldsToLowerSortKeyNewcomer()
        {
            var scene = new Scene()
                .Add(symbolIndex: 0, sortKey: 20f, featureIndex: 0, rects: R((0, 0, 20, 20)))   // incumbent, LOWER priority
                .Add(symbolIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((5, 0, 25, 20)));  // newcomer, HIGHER priority
            CollectionAssert.AreEquivalent(new[] { 1 }, scene.Survivors(new HashSet<int> { 0 }),
                "an incumbent must yield to a strictly higher-priority (lower-sort-key) newcomer — incumbency is a " +
                "tiebreak, not a lock, so it never blocks a genuinely higher-priority label");
        }

        // ── WITHIN-FRAME DETERMINISM given a fixed kept-set: the survivor set is independent of candidate input
        //    order (T1 permutation-invariance still holds — it is only the GLOBAL history-independence A-5 trades). ──
        [Test]
        public void Collision_Hysteresis_IsPermutationInvariant_GivenFixedKeptSet()
        {
            var kept = new HashSet<int> { 1 }; // make symbol 1 the incumbent in an equal-sort-key cluster
            Scene BuildOrdered(bool reversed)
            {
                var s = new Scene();
                if (reversed)
                {
                    s.Add(symbolIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((5, 0, 25, 20)));
                    s.Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)));
                }
                else
                {
                    s.Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)));
                    s.Add(symbolIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((5, 0, 25, 20)));
                }
                return s;
            }
            CollectionAssert.AreEquivalent(new[] { 1 }, BuildOrdered(false).Survivors(kept));
            CollectionAssert.AreEquivalent(new[] { 1 }, BuildOrdered(true).Survivors(kept),
                "with a fixed kept-set the survivor set does not depend on input order (incumbent 1 wins either way)");
        }
    }
}
