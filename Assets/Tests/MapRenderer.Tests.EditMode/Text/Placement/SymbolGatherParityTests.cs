// Text/Placement/SymbolGatherParityTests.cs — symbol candidate collision, compaction/cull job parity, deferred collision, fade, far-distance cull, gather memoization/order-parity, and drop-mask teeth.
//
// The collision/compaction/cull job-parity fixtures first, then deferred collision, fade, and the far-distance cull, then the gather memoization/order-parity/drop-mask fixtures, then halo emit.
//
// Contents:
//   SymbolCandidateCollisionTests     — the multi-box, all-or-nothing UNIFIED collision (CollisionJob): a curved along-line symbol is ONE candidate spanning N glyph boxes; it places iff EVERY box is free and, when placed, blocks across its WHOLE run — competing with point (1-box)…
//   SymbolCollisionOrderTests         — the real teeth for SymbolStagingMath.SanitizeSortKey: a NON-FINITE baked symbol-sort-key makes ComparePlacementOrder intransitive (NaN compares false both ways, so the sort-key branch never ties and the…
//   SymbolCompactJobTests             — CompactJob — the Burst port of SymbolPlacementSystem.GatherSymbolPoints's Compact pass — must produce the SAME _stagePointOffset / kept-point pools / _forceFadeOut membership / per-trigger counters, in the SAME record order, as an independent managed reference.
//   SymbolCullJobTests                — CullJob — the Burst port of SymbolPlacementSystem.GatherSymbolPoints's Cull pass — must produce the SAME per-record GatherTrigger verdict, in the SAME chain-priority order (dropped → departing → coverage → zoom → horizon → distance → none), as an…
//   SymbolDeferredCollisionTests      — Deferred collision: the collision is scheduled at the END of a Tick and Completed + re-keyed at the START of the next one, so the main thread never blocks on the single-threaded greedy.
//   SymbolFadeTests                   — the placement layer is a fade state machine.
//   SymbolFarDistanceCullGatherTests  — The harness injects a FIXED far-plane policy (the cull reads MapCamera.CurrentFarMetres, derived from that policy — NOT the raw Camera.farClipPlane, which is Unity's default until a SyncToCamera runs), so the far distance each assertion asserts against is…
//   SymbolGatherMemoTests             — GatherIntoMirror memoizes its heavy compaction on WinnerSetVersion — a same-source, same-version frame runs only the three per-frame masks (Departing/CoverageFading/Dropped), not the full pool rebuild.
//   SymbolGatherParityTests           — THE ORDER-PARITY TOOTH.
//   SymbolGatherPlanDropMaskTests     — the symbol-label native bake: the tile-coverage cull's Drop decision is a per-record MASK (Dropped, stamped onto the native mirror as _mirrorSymbolDropped) rather than a physical compaction — a Dropped winner stays RESIDENT in the mirror.
//   SymbolHaloEmitTests               — The one term that also has a uniform is text-halo-color, whose CONSTANT kind rides _HaloColor so a restyle can ease it (SymbolTextColorCarrier).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs.Symbols;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Core.Text;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;
using UnityEngine.TestTools.Constraints;
using MapRenderer.Core.Style.Symbol;
using Is = UnityEngine.TestTools.Constraints.Is; // Is.Not.AllocatingGCMemory()
using System;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Text.Placement
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolCandidateCollisionTests — a curved along-line symbol is one all-or-nothing candidate
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The multi-box, all-or-nothing UNIFIED collision (<see cref="CollisionJob"/>): a curved
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

            // Optional kept-set: candidates whose SymbolIndex is in it are marked WasPlacedLastFrame (incumbents).
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
        //    unstable heapsort's tie-resolution then flips with input order, and the incumbency feedback drives
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
        //    symbols. The legacy box-only path and its differential are both gone, so this is a direct
        //    property — the same overlapping-cluster-plus-disjoint-label shape
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

        // ── Sticky-placement hysteresis (anti-flicker). Incumbency (WasPlacedLastFrame) breaks EQUAL-sort-key────
        //    ties in favour of last frame's survivor, sitting below SortKey so it never blocks a higher-priority
        //    newcomer. Teeth verify: the fixed-point (no oscillation), the killed tiebreak flip,
        //    correct yielding, and within-frame determinism given a fixed kept-set. ──────────────────────────────

        // Helper: a Scene builder for a fixed geometry reused across kept-sets (each test rebuilds it fresh — the
        // Scene mutates its candidate array in Survivors()).
        private static Scene ClusterAB()  // two overlapping equal-sort-key symbols; feature(0) < feature(1)
            => new Scene()
                .Add(symbolIndex: 0, sortKey: 10f, featureIndex: 0, rects: R((0, 0, 20, 20)))
                .Add(symbolIndex: 1, sortKey: 10f, featureIndex: 1, rects: R((5, 0, 25, 20)));

        // ── FIXED POINT: feeding last frame's survivor set back in reproduces it exactly (idempotent). This is the
        //    anti-oscillation proof: a static frame's survivors don't change under hysteresis. ──
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
        //    that incumbency exists to stop. ──
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
        //    order (permutation-invariance still holds — it is only the GLOBAL history-independence incumbency trades). ──
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolCollisionOrderTests — a non-finite sort-key makes ComparePlacementOrder intransitive
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The real teeth for <c>SymbolStagingMath.SanitizeSortKey</c>:
    /// a NON-FINITE baked <c>symbol-sort-key</c> makes
    /// <see cref="SymbolCollision.ComparePlacementOrder(in SymbolCandidate, in SymbolCandidate)"/> intransitive (NaN
    /// compares false both ways, so the sort-key branch never ties and the fall-through feature/tile order can form a
    /// 3-cycle that contradicts the sort-key order). The unstable heapsort's survivor set then depends on input
    /// order. Staging the SAME symbols through <see cref="SymbolStagingMath.StagePoint"/> in two MIRROR-ORDER
    /// permutations must yield the IDENTICAL survivor set — which only holds once the NaN is normalized to
    /// <c>float.MaxValue</c> at candidate build. RED-verified: feeding raw <c>s.SortKey</c> (fix reverted) makes the
    /// two orders diverge; the sanitizer restores determinism.
    /// </summary>
    [TestFixture]
    public class SymbolCollisionOrderTests
    {
        // One logical, order-independent symbol identity: its FeatureIndex (StagePoint's SymbolIndex = ordinal is
        // NOT stable across the two staging orders, but FeatureIndex is carried verbatim per symbol).
        private readonly struct SymbolSpec
        {
            public readonly float SortKey;
            public readonly int FeatureIndex;
            public SymbolSpec(float sortKey, int featureIndex) { SortKey = sortKey; FeatureIndex = featureIndex; }
        }

        // The intransitive core: two finite candidates whose SortKey order (L_lowKey < L_highKey) CONTRADICTS their
        // FeatureIndex order (feat_highKey < feat_nan < feat_lowKey), with the NaN candidate's FeatureIndex sitting
        // BETWEEN them. With a NaN key the comparator forms a 3-cycle over {lowKey, nan, highKey}. Plus two extra
        // finite candidates so the heapsort has enough elements to reorder differently per input permutation.
        private static readonly SymbolSpec[] Specs =
        {
            new SymbolSpec(sortKey: 1f, featureIndex: 10),          // low key  → highest priority; high feature
            new SymbolSpec(sortKey: float.NaN, featureIndex: 5),    // the poison: non-finite key, mid feature
            new SymbolSpec(sortKey: 5f, featureIndex: 2),           // high key → low priority; low feature
            new SymbolSpec(sortKey: 2f, featureIndex: 8),           // filler
            new SymbolSpec(sortKey: 3f, featureIndex: 0),           // filler
        };

        // Stage `specs` (in the given array order) as fully-overlapping point symbols through StagePoint, then run the
        // production candidate collision pass. Returns the survivor set keyed by the stable FeatureIndex. All symbols
        // share one screen position + bounds ⇒ they all overlap ⇒ exactly the sorted-first survives; so a change in
        // the sorted order (the intransitive-NaN symptom) changes WHICH feature survives.
        private static HashSet<int> SurvivorsByFeature(SymbolSpec[] specs)
        {
            int n = specs.Length;
            var boxes = new SymbolBox[n];
            var quads = new PlacedQuad[n];
            var candidates = new SymbolCandidate[n];
            var emit = new CandidateEmit[n];
            int boxCount = 0, quadCount = 0, emitCount = 0;
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
                int staged = SymbolStagingMath.StagePoint(in s, quad, bearingRadians: 0f,
                    viewportLogicalPx: new double2(1920, 1080), ordinal: i,
                    boxes, ref boxCount, quads, ref quadCount, candidates, emit, ref emitCount);
                Assert.AreEqual(1, staged, "each fully-in-view point label must stage");
            }

            var survivorFlags = new bool[n];
            NativeCollisionRunner.RunCollision(candidates, n, boxes, boxCount, survivorFlags);

            var survivors = new HashSet<int>();
            for (int i = 0; i < n; i++)
                if (survivorFlags[i]) survivors.Add(candidates[i].FeatureIndex);
            return survivors;
        }

        [Test]
        public void NonFiniteSortKey_ProducesDeterministicSurvivors()
        {
            var forward = (SymbolSpec[])Specs.Clone();
            var mirrored = (SymbolSpec[])Specs.Clone();
            System.Array.Reverse(mirrored);

            HashSet<int> forwardSurvivors = SurvivorsByFeature(forward);
            HashSet<int> mirroredSurvivors = SurvivorsByFeature(mirrored);

            CollectionAssert.AreEquivalent(forwardSurvivors, mirroredSurvivors,
                "the survivor set must be independent of input order — the NaN sort-key candidate is sanitized to " +
                "float.MaxValue at StagePoint, so ComparePlacementOrder stays a strict total order (without the fix " +
                "the intransitive NaN comparator makes the unstable heapsort's winner mirror-order dependent)");

            // Positive pin: all symbols overlap ⇒ exactly one survives, and it is the finite highest-priority symbol
            // (lowest SortKey = 1f, FeatureIndex 10). The poison NaN candidate is normalized to sort LAST, never
            // preempting a good symbol.
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolCompactJobTests — CompactJob's Burst port must match the managed Compact pass exactly
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="CompactJob"/> — the Burst port of <c>SymbolPlacementSystem.GatherSymbolPoints</c>'s
    /// Compact pass — must produce the SAME <c>_stagePointOffset</c> / kept-point pools / <c>_forceFadeOut</c>
    /// membership / per-trigger counters, in the SAME record order, as an independent managed reference.
    /// </summary>
    [TestFixture]
    public class SymbolCompactJobTests
    {
        private const float FadeEpsilon = 1e-3f;

        // Record indices — named so the fixture and the expectation table read as one thing.
        private const int KeptNoneA           = 0;
        private const int DroppedRec          = 1;
        private const int DepartingDead       = 2;
        private const int CoverageDead        = 3;
        private const int ZoomDeadA           = 4;
        private const int HorizonDead         = 5;
        private const int DistanceDead        = 6;
        private const int DepartingAlive      = 7;
        private const int CoverageCurvedAlive = 8;
        private const int ZoomCurvedDead      = 9;
        private const int KeptNoneB           = 10;
        private const int SymbolCount         = 11;

        // Detail == record index everywhere (Detail[r] = r) — a deliberate simplification: it still exercises
        // every read path (PointDetails / CurvedAnchorFadeStart / CurvedAnchorCount are all indexed by Detail,
        // not by r directly), while keeping the fixture legible.
        private static readonly GatherTrigger[] Trigger =
        {
            GatherTrigger.None, GatherTrigger.Dropped, GatherTrigger.Departing, GatherTrigger.Coverage,
            GatherTrigger.Zoom, GatherTrigger.Horizon, GatherTrigger.Distance, GatherTrigger.Departing,
            GatherTrigger.Coverage, GatherTrigger.Zoom, GatherTrigger.None,
        };

        private static SymbolPlacementKind[] Kinds()
        {
            var a = new SymbolPlacementKind[SymbolCount]; // Point (0) everywhere by default
            a[CoverageCurvedAlive] = SymbolPlacementKind.Curved;
            a[ZoomCurvedDead]      = SymbolPlacementKind.Curved;
            return a;
        }

        private static int[] Detail()
        {
            var a = new int[SymbolCount];
            for (int r = 0; r < SymbolCount; r++) a[r] = r;
            return a;
        }

        // Point-kind FadeIds, indexed by Detail (== r for point records). Only the "fade-triggered" records
        // (2-7) are ever read — 0/1/10 short-circuit before MarkFadeOutIfAlive; 8/9 are Curved.
        private static long[] PointFadeIds()
        {
            var a = new long[SymbolCount];
            a[DepartingDead]  = 2001;
            a[CoverageDead]   = 3001;
            a[ZoomDeadA]      = 4001;
            a[HorizonDead]    = 5001;
            a[DistanceDead]   = 6001;
            a[DepartingAlive] = 7001;
            return a;
        }

        private static PointStageInput[] PointDetails(long[] pointFadeIds)
        {
            var a = new PointStageInput[SymbolCount];
            for (int r = 0; r < SymbolCount; r++) a[r] = new PointStageInput { FadeId = pointFadeIds[r] };
            return a;
        }

        // Curved-kind anchor slices, indexed by Detail (== r). CoverageCurvedAlive has 2 anchors (+ 1 trailing
        // centred-fallback id = 3 ids read); ZoomCurvedDead has 1 anchor (+ fallback = 2 ids read).
        private static int[] CurvedAnchorFadeStart()
        {
            var a = new int[SymbolCount];
            a[CoverageCurvedAlive] = 100;
            a[ZoomCurvedDead]      = 200;
            return a;
        }

        private static int[] CurvedAnchorCount()
        {
            var a = new int[SymbolCount];
            a[CoverageCurvedAlive] = 2;
            a[ZoomCurvedDead]      = 1;
            return a;
        }

        // Flat FadeIds pool the curved slices above index into. CoverageCurvedAlive: one anchor alive (8101),
        // the other anchor + the centred fallback dead. ZoomCurvedDead: anchor + fallback both dead.
        private static long[] FadeIds()
        {
            var a = new long[210];
            a[100] = 8100; a[101] = 8101; a[102] = 8102; // CoverageCurvedAlive: anchor, anchor(alive), fallback
            a[200] = 9100; a[201] = 9101;                 // ZoomCurvedDead: anchor, fallback
            return a;
        }

        private static Dictionary<long, float> FadeOpacity() => new Dictionary<long, float>
        {
            [7001] = 0.5f, // DepartingAlive's point FadeId — clearly alive
            [8101] = 0.7f, // CoverageCurvedAlive's one live anchor — clearly alive
            // every other FadeId referenced by the fixture is ABSENT ⇒ reads as dead (TryGetValue false).
        };

        // World-point pool: irregular WorldStart per kept record (NOT the cumulative kept-vertex count, and
        // not derivable from r) + a point/up pattern that differs per component (point=(r,v,0), up=(r,v,1)) so
        // a swapped-source or wrong-array read is visible. Only kept records (0,7,8,10) are ever read; the
        // skipped records' spans are never touched, so they are left at the zeroed default.
        private static int[] WorldStart()
        {
            var a = new int[SymbolCount];
            a[KeptNoneA]           = 6;
            a[DepartingAlive]      = 0;
            a[CoverageCurvedAlive] = 10;
            a[KeptNoneB]           = 2;
            return a;
        }

        private static int[] WorldCount()
        {
            var a = new int[SymbolCount];
            a[KeptNoneA]           = 2;
            a[DepartingAlive]      = 1;
            a[CoverageCurvedAlive] = 3;
            a[KeptNoneB]           = 2;
            return a;
        }

        private const int WorldPoolSize = 13;

        private static double3[] WorldPoints()
        {
            var a = new double3[WorldPoolSize];
            a[6] = new double3(0, 0, 0); a[7] = new double3(0, 1, 0);       // KeptNoneA v0/v1
            a[0] = new double3(7, 0, 0);                                    // DepartingAlive v0
            a[10] = new double3(8, 0, 0); a[11] = new double3(8, 1, 0); a[12] = new double3(8, 2, 0); // CoverageCurvedAlive v0-2
            a[2] = new double3(10, 0, 0); a[3] = new double3(10, 1, 0);     // KeptNoneB v0/v1
            return a;
        }

        private static float3[] WorldUps()
        {
            var a = new float3[WorldPoolSize];
            a[6] = new float3(0, 0, 1); a[7] = new float3(0, 1, 1);
            a[0] = new float3(7, 0, 1);
            a[10] = new float3(8, 0, 1); a[11] = new float3(8, 1, 1); a[12] = new float3(8, 2, 1);
            a[2] = new float3(10, 0, 1); a[3] = new float3(10, 1, 1);
            return a;
        }

        // ── Expected sanity table (asserted against the managed reference FIRST — Cull's fixture-bug guard) ────

        private static readonly int[] ExpectedOffset =
        {
            0, -1, -1, -1, -1, -1, -1, 2, 3, -1, 6,
        };

        private static readonly double3[] ExpectedPoints =
        {
            new double3(0, 0, 0), new double3(0, 1, 0),                                // KeptNoneA
            new double3(7, 0, 0),                                                      // DepartingAlive
            new double3(8, 0, 0), new double3(8, 1, 0), new double3(8, 2, 0),           // CoverageCurvedAlive
            new double3(10, 0, 0), new double3(10, 1, 0),                              // KeptNoneB
        };

        private static readonly float3[] ExpectedUps =
        {
            new float3(0, 0, 1), new float3(0, 1, 1),
            new float3(7, 0, 1),
            new float3(8, 0, 1), new float3(8, 1, 1), new float3(8, 2, 1),
            new float3(10, 0, 1), new float3(10, 1, 1),
        };

        private static readonly HashSet<long> ExpectedForceFadeOut = new HashSet<long>
        {
            7001, 8100, 8101, 8102, 9100, 9101,
        };

        private const int ExpectedDeparting = 1; // DepartingDead only — DepartingAlive is kept, not counted
        private const int ExpectedCoverage  = 1; // CoverageDead only — CoverageCurvedAlive is kept, not counted
        private const int ExpectedZoom      = 2; // ZoomDeadA + ZoomCurvedDead
        private const int ExpectedHorizon   = 1; // HorizonDead
        private const int ExpectedDistance  = 1; // DistanceDead

        // ── The independent managed reference — re-implements the Compact loop, NOT a call into the job ────────

        private static bool ManagedTryForceFadeOut(long fadeId, Dictionary<long, float> fadeOpacity,
            HashSet<long> forceFadeOut, float fadeEpsilon)
        {
            if (!(fadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > fadeEpsilon)) return false;
            forceFadeOut.Add(fadeId);
            return true;
        }

        private static bool ManagedMarkFadeOutIfAlive(int r, SymbolPlacementKind[] kinds, int[] detail, long[] pointFadeIds,
            int[] curvedAnchorFadeStart, int[] curvedAnchorCount, long[] fadeIds, Dictionary<long, float> fadeOpacity,
            HashSet<long> forceFadeOut, float fadeEpsilon)
        {
            int d = detail[r];
            if (kinds[r] == SymbolPlacementKind.Point)
                return ManagedTryForceFadeOut(pointFadeIds[d], fadeOpacity, forceFadeOut, fadeEpsilon);

            bool alive = false;
            int fadeStart = curvedAnchorFadeStart[d];
            int fadeCount = curvedAnchorCount[d] + 1; // + trailing centred-fallback fade id
            for (int i = 0; i < fadeCount; i++)
            {
                long fadeId = fadeIds[fadeStart + i];
                forceFadeOut.Add(fadeId);
                if (fadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > fadeEpsilon) alive = true;
            }
            return alive;
        }

        private static void ManagedCompact(SymbolPlacementKind[] kinds, int[] detail, long[] pointFadeIds,
            int[] curvedAnchorFadeStart, int[] curvedAnchorCount, long[] fadeIds, int[] worldStart, int[] worldCount,
            double3[] worldPoints, float3[] worldUps, Dictionary<long, float> fadeOpacity, float fadeEpsilon,
            int[] outOffset, List<double3> outPoints, List<float3> outUps, HashSet<long> outForceFadeOut, int[] outCounts)
        {
            for (int r = 0; r < SymbolCount; r++)
            {
                GatherTrigger t = Trigger[r];
                if (t == GatherTrigger.Dropped) { outOffset[r] = -1; continue; }

                if (t != GatherTrigger.None && !ManagedMarkFadeOutIfAlive(r, kinds, detail, pointFadeIds,
                        curvedAnchorFadeStart, curvedAnchorCount, fadeIds, fadeOpacity, outForceFadeOut, fadeEpsilon))
                {
                    outCounts[(int)t]++;
                    outOffset[r] = -1;
                    continue;
                }

                outOffset[r] = outPoints.Count;
                int ws = worldStart[r], wc = worldCount[r];
                for (int v = 0; v < wc; v++)
                {
                    outPoints.Add(worldPoints[ws + v]);
                    outUps.Add(worldUps[ws + v]);
                }
            }
        }

        // ── The Burst arm — CompactJob.Run() over the same fixture ────────────────────────────────────

        private static (int[] offset, double3[] points, float3[] ups, HashSet<long> forceFadeOut, int[] counts)
            NativeCompact(SymbolPlacementKind[] kinds, int[] detail, PointStageInput[] pointDetails, int[] curvedAnchorFadeStart,
                int[] curvedAnchorCount, long[] fadeIds, int[] worldStart, int[] worldCount, double3[] worldPoints,
                float3[] worldUps, Dictionary<long, float> fadeOpacity)
        {
            const Allocator alloc = Allocator.TempJob;
            NativeArray<T> From<T>(T[] src) where T : unmanaged
            {
                var a = new NativeArray<T>(src.Length, alloc);
                for (int i = 0; i < src.Length; i++) a[i] = src[i];
                return a;
            }

            var nTrigger = From(Trigger);
            var nKinds = From(kinds);
            var nDetail = From(detail);
            var nPointDetails = From(pointDetails);
            var nCurvedAnchorFadeStart = From(curvedAnchorFadeStart);
            var nCurvedAnchorCount = From(curvedAnchorCount);
            var nFadeIds = From(fadeIds);
            var nWorldStart = From(worldStart);
            var nWorldCount = From(worldCount);
            var nWorldPoints = From(worldPoints);
            var nWorldUps = From(worldUps);

            var nFadeOpacity = new NativeHashMap<long, float>(fadeOpacity.Count, alloc);
            foreach (var kv in fadeOpacity) nFadeOpacity.Add(kv.Key, kv.Value);

            var nStageOffset = new NativeArray<int>(SymbolCount, alloc);
            for (int i = 0; i < SymbolCount; i++) nStageOffset[i] = int.MinValue; // poison, NOT zero — see field doc

            var nOutPoints = new NativeList<double3>(alloc);
            var nOutUps = new NativeList<float3>(alloc);
            var nForceFadeOut = new NativeHashSet<long>(16, alloc);
            var nCounts = new NativeArray<int>((int)GatherTrigger.Dropped + 1, alloc); // ClearMemory (default) → fresh zeros

            try
            {
                new CompactJob
                {
                    Trigger = nTrigger, Kinds = nKinds, Detail = nDetail, PointDetails = nPointDetails,
                    CurvedAnchorFadeStart = nCurvedAnchorFadeStart, CurvedAnchorCount = nCurvedAnchorCount,
                    FadeIds = nFadeIds, WorldStart = nWorldStart, WorldCount = nWorldCount,
                    WorldPoints = nWorldPoints, WorldUps = nWorldUps, FadeOpacity = nFadeOpacity,
                    FadeEpsilon = FadeEpsilon, Count = SymbolCount,
                    StageOffset = nStageOffset, OutPoints = nOutPoints, OutUps = nOutUps,
                    ForceFadeOut = nForceFadeOut, Counts = nCounts,
                }.Run();

                var forceFadeOut = new HashSet<long>();
                foreach (long id in nForceFadeOut) forceFadeOut.Add(id);

                return (nStageOffset.ToArray(), nOutPoints.AsArray().ToArray(), nOutUps.AsArray().ToArray(),
                    forceFadeOut, nCounts.ToArray());
            }
            finally
            {
                nTrigger.Dispose(); nKinds.Dispose(); nDetail.Dispose(); nPointDetails.Dispose();
                nCurvedAnchorFadeStart.Dispose(); nCurvedAnchorCount.Dispose(); nFadeIds.Dispose();
                nWorldStart.Dispose(); nWorldCount.Dispose(); nWorldPoints.Dispose(); nWorldUps.Dispose();
                nFadeOpacity.Dispose(); nStageOffset.Dispose(); nOutPoints.Dispose(); nOutUps.Dispose();
                nForceFadeOut.Dispose(); nCounts.Dispose();
            }
        }

        [Test]
        public void Compact_MatchesManagedReference_EveryOutputAndRunningOffset()
        {
            SymbolPlacementKind[] kinds = Kinds();
            int[] detail = Detail();
            long[] pointFadeIds = PointFadeIds();
            PointStageInput[] pointDetails = PointDetails(pointFadeIds);
            int[] curvedAnchorFadeStart = CurvedAnchorFadeStart();
            int[] curvedAnchorCount = CurvedAnchorCount();
            long[] fadeIds = FadeIds();
            int[] worldStart = WorldStart();
            int[] worldCount = WorldCount();
            double3[] worldPoints = WorldPoints();
            float3[] worldUps = WorldUps();
            Dictionary<long, float> fadeOpacity = FadeOpacity();

            var managedOffset = new int[SymbolCount];
            for (int i = 0; i < SymbolCount; i++) managedOffset[i] = int.MinValue; // poison — see NativeCompact
            var managedPoints = new List<double3>();
            var managedUps = new List<float3>();
            var managedForceFadeOut = new HashSet<long>();
            var managedCounts = new int[(int)GatherTrigger.Dropped + 1];

            ManagedCompact(kinds, detail, pointFadeIds, curvedAnchorFadeStart, curvedAnchorCount, fadeIds,
                worldStart, worldCount, worldPoints, worldUps, fadeOpacity, FadeEpsilon,
                managedOffset, managedPoints, managedUps, managedForceFadeOut, managedCounts);

            // Sanity: the managed reference itself must match the fixture's stated expectation table — otherwise
            // a fixture bug (not a job bug) would silently pass by having both arms agree on the WRONG answer.
            CollectionAssert.AreEqual(ExpectedOffset, managedOffset, "managed reference vs. Expected offsets");
            CollectionAssert.AreEqual(ExpectedPoints, managedPoints, "managed reference vs. Expected points");
            CollectionAssert.AreEqual(ExpectedUps, managedUps, "managed reference vs. Expected ups");
            CollectionAssert.AreEquivalent(ExpectedForceFadeOut, managedForceFadeOut, "managed reference vs. Expected forceFadeOut");
            Assert.AreEqual(ExpectedDeparting, managedCounts[(int)GatherTrigger.Departing], "managed Departing count");
            Assert.AreEqual(ExpectedCoverage, managedCounts[(int)GatherTrigger.Coverage], "managed Coverage count");
            Assert.AreEqual(ExpectedZoom, managedCounts[(int)GatherTrigger.Zoom], "managed Zoom count");
            Assert.AreEqual(ExpectedHorizon, managedCounts[(int)GatherTrigger.Horizon], "managed Horizon count");
            Assert.AreEqual(ExpectedDistance, managedCounts[(int)GatherTrigger.Distance], "managed Distance count");

            var (nativeOffset, nativePoints, nativeUps, nativeForceFadeOut, nativeCounts) = NativeCompact(
                kinds, detail, pointDetails, curvedAnchorFadeStart, curvedAnchorCount, fadeIds,
                worldStart, worldCount, worldPoints, worldUps, fadeOpacity);

            CollectionAssert.AreEqual(managedOffset, nativeOffset, "CompactJob vs. managed reference — offsets");
            CollectionAssert.AreEqual(managedPoints, nativePoints, "CompactJob vs. managed reference — points");
            CollectionAssert.AreEqual(managedUps, nativeUps, "CompactJob vs. managed reference — ups");
            CollectionAssert.AreEquivalent(managedForceFadeOut, nativeForceFadeOut, "CompactJob vs. managed reference — forceFadeOut");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Departing], nativeCounts[(int)GatherTrigger.Departing], "native Departing count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Coverage], nativeCounts[(int)GatherTrigger.Coverage], "native Coverage count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Zoom], nativeCounts[(int)GatherTrigger.Zoom], "native Zoom count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Horizon], nativeCounts[(int)GatherTrigger.Horizon], "native Horizon count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Distance], nativeCounts[(int)GatherTrigger.Distance], "native Distance count");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolCullJobTests — CullJob's Burst port must match the managed Cull pass's verdict and order
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="CullJob"/> — the Burst port of <c>SymbolPlacementSystem.GatherSymbolPoints</c>'s Cull pass
    /// — must produce the SAME per-record <see cref="GatherTrigger"/> verdict, in the SAME chain-priority order
    /// (dropped → departing → coverage → zoom → horizon → distance → none), as an independent managed reference.
    /// </summary>
    [TestFixture]
    public class SymbolCullJobTests
    {
        // Shared per-frame scalars for every record in the fixture (one job dispatch = one frame's cull).
        private static readonly double3  SceneOriginRender    = double3.zero;
        private static readonly float3x3 Rebase               = float3x3.identity;
        private static readonly double3  CameraRelative       = double3.zero;
        // A globe centred 1000m "in front of" the camera along -z, radius 500 (radius² = 250,000) — chosen so the
        // Horizon/Distance fixture anchors below sit CLEARLY (not near) the threshold on each side.
        private static readonly double3  GlobeCentreRelative  = new double3(0, 0, -1000);
        private const double GlobeRadiusSq     = 500.0 * 500.0;
        private const double SymbolCullDistance = 500.0;
        private const int    SlotCount = 3;

        // Record indices — named so the fixture and the expectation table read as one thing.
        private const int Dropped        = 0;
        private const int Departing      = 1;
        private const int Coverage       = 2;
        private const int ZoomPoint      = 3;
        private const int ZoomCurved     = 4;
        private const int Horizon        = 5;
        private const int Distance       = 6;
        private const int Kept           = 7;
        private const int OrderDropped   = 8; // Dropped AND Departing both set — pins Dropped-first
        private const int OrderCoverage  = 9; // Coverage AND Zoom both true — pins Coverage-before-Zoom
        private const int GrownListTail  = 10; // Slot >= SlotCount, stale `false` only reachable via SlotVisible.Length
        private const int SymbolCount    = 11;

        private static readonly GatherTrigger[] Expected =
        {
            GatherTrigger.Dropped, GatherTrigger.Departing, GatherTrigger.Coverage,
            GatherTrigger.Zoom, GatherTrigger.Zoom,
            GatherTrigger.Horizon, GatherTrigger.Distance, GatherTrigger.None,
            GatherTrigger.Dropped, GatherTrigger.Coverage, GatherTrigger.None,
        };

        // ── Fixture construction ─────────────────────────────────────────────────────────────────────────────

        private static byte[] SymbolDropped() => Flags(Dropped, OrderDropped);
        private static byte[] SymbolDeparting() => Flags(Departing, OrderDropped);
        private static byte[] SymbolCoverageFading() => Flags(Coverage, OrderCoverage);

        private static byte[] Flags(params int[] set)
        {
            var a = new byte[SymbolCount];
            foreach (int i in set) a[i] = 1;
            return a;
        }

        // RepAnchor: only Horizon/Distance/Kept actually gate on geometry (every other record short-circuits
        // earlier in the chain, so its anchor is never read) — those three are placed CLEARLY on their side of
        // the Horizon/Distance thresholds; everyone else gets the safe (0,0,0) anchor for tidiness.
        private static double3[] RepAnchor()
        {
            var a = new double3[SymbolCount];
            for (int i = 0; i < SymbolCount; i++) a[i] = double3.zero;
            a[Horizon]  = new double3(0, 0, -2000); // far side of the horizon plane — clearly hidden
            a[Distance] = new double3(2000, 0, 0);  // clearly beyond SymbolCullDistance, clearly NOT horizon-hidden
            return a;
        }

        // Kinds/Detail address one of two per-kind detail arrays (Points/Curveds), mirroring the mirror's own
        // Kinds/Detail split. Unused (short-circuited) records get an arbitrary valid index (Point, detail 0).
        private static SymbolPlacementKind[] Kinds()
        {
            var a = new SymbolPlacementKind[SymbolCount]; // 0 == SymbolPlacementKind.Point everywhere by default
            a[ZoomCurved] = SymbolPlacementKind.Curved;
            return a;
        }

        private static int[] Detail()
        {
            var a = new int[SymbolCount];
            a[ZoomPoint]     = 0; // Points[0].Slot == a slot with SlotVisible == false
            a[ZoomCurved]    = 0; // Curveds[0].Slot == a slot with SlotVisible == false
            a[Horizon]       = 1; // Points[1].Slot == a VISIBLE slot (must clear the zoom gate to reach Horizon)
            a[Distance]      = 1;
            a[Kept]          = 1;
            a[OrderCoverage] = 0; // Slot with SlotVisible == false — would ALSO be Zoom if the chain reordered
            a[GrownListTail] = 2; // Points[2].Slot == 4, only in-range if bounded by SlotVisible.Length (bug)
            return a;
        }

        private static PointStageInput[] Points() => new[]
        {
            new PointStageInput { Slot = 0 }, // gated (SlotVisible[0] == false)
            new PointStageInput { Slot = 2 }, // visible (SlotVisible[2] == true)
            new PointStageInput { Slot = 4 }, // out of SlotCount (3) — only reachable via a Length-bound bug
        };

        private static CurvedStageInput[] Curveds() => new[]
        {
            new CurvedStageInput { Slot = 1 }, // gated (SlotVisible[1] == false)
        };

        // Grown-only list LONGER than SlotCount (3): index 4 carries a stale `false` a Length-bound bug would
        // read as "gated", where the correct SlotCount-bound reading never looks at it (slot 4 >= SlotCount).
        private static bool[] SlotVisible() => new[] { false, false, true, true, false };

        // ── The independent managed reference — re-implements the chain, NOT a call into CullJob ─────────

        private static GatherTrigger ManagedVerdict(int r, byte[] dropped, byte[] departing, byte[] coverage,
            double3[] repAnchor, SymbolPlacementKind[] kinds, int[] detail, PointStageInput[] points, CurvedStageInput[] curveds,
            bool[] slotVisible)
        {
            if (dropped[r] != 0) return GatherTrigger.Dropped;
            if (departing[r] != 0) return GatherTrigger.Departing;
            if (coverage[r] != 0) return GatherTrigger.Coverage;
            if (IsOutOfLiveZoom(r, kinds, detail, points, curveds, slotVisible)) return GatherTrigger.Zoom;
            if (HorizonCull.IsHiddenBeyondHorizon(repAnchor[r], SceneOriginRender, Rebase, CameraRelative,
                                                   GlobeCentreRelative, GlobeRadiusSq))
                return GatherTrigger.Horizon;
            if (SymbolFarPlaneCull.IsCulled(repAnchor[r], SceneOriginRender, Rebase, CameraRelative, SymbolCullDistance))
                return GatherTrigger.Distance;
            return GatherTrigger.None;
        }

        // Bound by SlotCount, NOT slotVisible.Length — the correctness point GrownListTail pins.
        private static bool IsOutOfLiveZoom(int r, SymbolPlacementKind[] kinds, int[] detail, PointStageInput[] points,
            CurvedStageInput[] curveds, bool[] slotVisible)
        {
            if (SlotCount == 0) return false;
            int d = detail[r];
            int slot = kinds[r] == SymbolPlacementKind.Point ? points[d].Slot : curveds[d].Slot;
            return slot >= 0 && slot < SlotCount && !slotVisible[slot];
        }

        // ── The Burst arm — CullJob.Run() over the same fixture ───────────────────────────────────────

        private static GatherTrigger[] NativeVerdicts(byte[] dropped, byte[] departing, byte[] coverage,
            double3[] repAnchor, SymbolPlacementKind[] kinds, int[] detail, PointStageInput[] points, CurvedStageInput[] curveds,
            bool[] slotVisible)
        {
            const Allocator alloc = Allocator.TempJob;
            NativeArray<T> From<T>(T[] src) where T : unmanaged
            {
                var a = new NativeArray<T>(src.Length, alloc);
                for (int i = 0; i < src.Length; i++) a[i] = src[i];
                return a;
            }

            var nDropped = From(dropped); var nDeparting = From(departing); var nCoverage = From(coverage);
            var nAnchor = From(repAnchor); var nKinds = From(kinds); var nDetail = From(detail);
            var nPoints = From(points); var nCurveds = From(curveds); var nSlotVisible = From(slotVisible);
            var outTrigger = new NativeArray<GatherTrigger>(SymbolCount, alloc);
            try
            {
                new CullJob
                {
                    SymbolDropped = nDropped, SymbolDeparting = nDeparting, SymbolCoverageFading = nCoverage,
                    RepAnchor = nAnchor, Kinds = nKinds, Detail = nDetail, Points = nPoints, Curveds = nCurveds,
                    SlotVisible = nSlotVisible,
                    SceneOriginRender = SceneOriginRender, Rebase = Rebase, CameraRelative = CameraRelative,
                    GlobeCentreRelative = GlobeCentreRelative, GlobeRadiusSq = GlobeRadiusSq,
                    SymbolCullDistance = SymbolCullDistance, SlotCount = SlotCount,
                    OutTrigger = outTrigger,
                }.Run(SymbolCount);

                return outTrigger.ToArray();
            }
            finally
            {
                nDropped.Dispose(); nDeparting.Dispose(); nCoverage.Dispose(); nAnchor.Dispose(); nKinds.Dispose();
                nDetail.Dispose(); nPoints.Dispose(); nCurveds.Dispose(); nSlotVisible.Dispose(); outTrigger.Dispose();
            }
        }

        [Test]
        public void Cull_MatchesManagedReference_EveryVerdictAndOrder()
        {
            byte[] dropped = SymbolDropped(), departing = SymbolDeparting(), coverage = SymbolCoverageFading();
            double3[] repAnchor = RepAnchor();
            SymbolPlacementKind[] kinds = Kinds();
            int[] detail = Detail();
            PointStageInput[] points = Points();
            CurvedStageInput[] curveds = Curveds();
            bool[] slotVisible = SlotVisible();

            var managed = new GatherTrigger[SymbolCount];
            for (int r = 0; r < SymbolCount; r++)
                managed[r] = ManagedVerdict(r, dropped, departing, coverage, repAnchor, kinds, detail, points, curveds, slotVisible);

            // Sanity: the managed reference itself must match the fixture's stated expectation table — otherwise
            // a fixture bug (not a job bug) would silently pass by having both arms agree on the WRONG answer.
            CollectionAssert.AreEqual(Expected, managed, "managed reference vs. the fixture's expectation table");

            GatherTrigger[] native = NativeVerdicts(dropped, departing, coverage, repAnchor, kinds, detail, points, curveds, slotVisible);
            CollectionAssert.AreEqual(managed, native, "CullJob vs. the independent managed reference");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolDeferredCollisionTests — collision is scheduled at tick-end, completed at the next tick-start
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolDeferredCollisionTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static SymbolPaint PaintOf(float4 color) => new SymbolPaint { TextColor = color, Opacity = 1f };

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, float sortKey, string text, int feature, float4 color)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: PaintOf(color), textSizePx: 24f, paddingPx: 2f, sortKey: sortKey, text: text,
                featureIndex: feature, tileKey: 0L);

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly IProjection Projection;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("DeferredCollision_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Projection = cam.Projection;
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                System = new SymbolPlacementSystem(cam, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        // Reads the WORLD slot mesh's first vertex colour — the same pattern
        // SymbolPlacementDemoProductionFlipTests.cs uses to pin which symbol's own colour reached the mesh.
        private static float3 FirstVertexColor(SymbolPlacementSystem system)
        {
            Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh), "the world slot mesh must exist.");
            WorldMeshReadback.Read(mesh, out WorldBillboardVertex[] vertices, out _);
            Assert.Greater(vertices.Length, 0, "the world mesh must have built vertices.");
            return vertices[0].ColorRGB;
        }

        private static void AssertColorMatches(SymbolPlacementSystem system, SymbolPaint paint, string because)
        {
            float3 actual = FirstVertexColor(system);
            float4 expected = SymbolPlacementSystem.LinearColor(paint);
            float error = math.csum(math.abs(actual - expected.xyz));
            Assert.Less(error, 0.02f,
                $"{because} — sampled ({actual.x:F3},{actual.y:F3},{actual.z:F3}) vs expected ({expected.x:F3},{expected.y:F3},{expected.z:F3}).");
        }

        // ── The primary deferred-collision tooth: the collision verdict a Tick's emit reads is the one HARVESTED at the top
        //    of THAT Tick — i.e. the collision SCHEDULED at the end of the PREVIOUS Tick, over the PREVIOUS
        //    Tick's candidates. A brand-new candidate set therefore takes one extra Tick to be reflected: an
        //    incumbent holds its slot for one more Tick after a newcomer that would beat it appears. ──
        [Test]
        public void Collision_VerdictAppliesOneFrameLate_IncumbentHoldsUntilTheNextTick()
        {
            using var h = new Harness();
            var red = new float4(1f, 0f, 0f, 1f);
            var blue = new float4(0f, 0f, 1f, 1f);
            // A: higher SortKey (lower priority) → the eventual loser once B appears. B: lower SortKey → winner.
            var aOnly = new SymbolTileBuffer();
            AddPoint(aOnly, h.Origin, sortKey: 10f, text: "A", feature: 0, color: red);
            var aAndB = new SymbolTileBuffer();
            AddPoint(aAndB, h.Origin, sortKey: 10f, text: "A", feature: 0, color: red);
            AddPoint(aAndB, h.Origin, sortKey: 5f, text: "B", feature: 1, color: blue);

            // Tick 1: {A} alone — nothing has been harvested yet (no prior scheduled collision) ⇒ nothing shows.
            h.System.TickSymbols(in h.Frame, aOnly, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(0, h.System.LastQuadCount, "tick 1: no pending verdict yet — nothing shows (symbol-label-perf-design.md § \"The collision verdict applies one frame late\").");

            // Tick 2: {A} again — harvests tick 1's scheduled collision over {A} ⇒ A shows.
            h.System.TickSymbols(in h.Frame, aOnly, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "tick 2: A's own collision has now been harvested.");
            AssertColorMatches(h.System, PaintOf(red), "tick 2 must show A");

            // Tick 3: {A, B} — the harvested verdict is still tick 2's, over {A} ALONE (B did not exist when
            // that collision was scheduled) ⇒ A still shows, B does not — the one-Tick verdict latency.
            h.System.TickSymbols(in h.Frame, aAndB, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "tick 3: the deferred verdict is still A-only.");
            AssertColorMatches(h.System, PaintOf(red), "tick 3 must still show A, not B");

            // Tick 4: {A, B} again — harvests tick 3's scheduled collision over {A, B}, where B wins ⇒ B shows,
            // A eases toward 0 with deltaTime = +inf (snaps instantly), so only B is emitted.
            h.System.TickSymbols(in h.Frame, aAndB, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "tick 4: B has now won the harvested verdict.");
            AssertColorMatches(h.System, PaintOf(blue), "tick 4 must show B, A has snapped to 0");
        }

        // ── The first Tick after construction schedules but shows nothing;
        //    the second shows the survivor of that scheduled collision. ──
        [Test]
        public void FirstTick_SchedulesOnly_SecondTickShowsTheSurvivors()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0, color: new float4(1f, 1f, 1f, 1f));

            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(0, h.System.LastQuadCount, "the first Tick has no prior verdict to harvest.");

            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.AreEqual(1, h.System.LastQuadCount, "the second Tick harvests the first Tick's scheduled collision.");
        }

        // ── DoDispose must Complete() a still-pending collision before disposing the buffers it holds
        //    (_stageCandidates/_stageBoxes/_nSurvivors/_survivorCountOut/the grid lists), or the job safety system
        //    throws (a use-after-free). Tick once (schedules a collision, leaving it pending — HarvestCollision
        //    only runs at the START of the NEXT Tick, which never comes here) then Dispose. ──
        [Test]
        public void Dispose_WithPendingCollision_CompletesCleanly()
        {
            var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0, color: new float4(1f, 1f, 1f, 1f));
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity);
            Assert.DoesNotThrow(() => h.Dispose(), "Dispose must Complete() a still-pending collision, not tear down under it.");
        }

        private static SymbolRenderLayer BuildRenderLayer(double? minZoom, double initialZoom)
        {
            string minZoomJson = minZoom.HasValue ? $@", ""minzoom"": {minZoom.Value}" : "";
            string styleJson = $@"{{
                ""version"": 8,
                ""layers"": [
                    {{ ""id"": ""label"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l""{minZoomJson},
                      ""layout"": {{ ""text-field"": ""{{NAME}}"" }} }}
                ]
            }}";
            var settings = MapMaterialSetTestUtil.Load();
            return SymbolRenderLayer.Create((Symbol.StyleLayer)StyleParser.Parse(styleJson).Layers[0], settings, initialZoom, drawIndex: 0);
        }

        // ── The display-time zoom gate (`!cand.Suppressed` in the emit `show` expression) is a SAME-frame
        //    override on top of the deferred verdict: a candidate that WON a previous collision must stop
        //    showing the instant its layer leaves the live zoom range, not linger a Tick until the next
        //    harvest catches up. ──
        [Test]
        public void SuppressedCandidate_HidesInTheSameFrame_NotOneFrameLate()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0, color: new float4(1f, 1f, 1f, 1f));

            // Camera zoom is 5.0 (Harness). Unbounded layer (no minzoom) is visible throughout.
            var visibleLayer = BuildRenderLayer(minZoom: null, initialZoom: 5.0);
            // minzoom above the live camera zoom ⇒ IsVisibleAtZoom(5.0) is false ⇒ Suppressed.
            var suppressedLayer = BuildRenderLayer(minZoom: 10.0, initialZoom: 5.0);
            try
            {
                var visibleLayers = new List<SymbolRenderLayer> { visibleLayer };
                var suppressedLayers = new List<SymbolRenderLayer> { suppressedLayer };

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity, symbolLayers: visibleLayers);
                Assert.AreEqual(0, h.System.LastQuadCount, "tick 1: no prior verdict yet.");

                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity, symbolLayers: visibleLayers);
                Assert.AreEqual(1, h.System.LastQuadCount, "tick 2: the label has won its harvested verdict and shows.");

                // tick 3: the SAME symbol, now under a layer whose minzoom excludes the live zoom. Even though
                // the harvested verdict (from tick 2's scheduled collision) still says "won", Suppressed must
                // hide it THIS Tick — not one Tick later.
                h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: float.PositiveInfinity, symbolLayers: suppressedLayers);
                Assert.AreEqual(0, h.System.LastQuadCount, "tick 3: suppression is a same-frame override, not one-Tick-late.");
            }
            finally
            {
                visibleLayer.Dispose();
                suppressedLayer.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolFadeTests — the placement layer is a fade state machine
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The placement layer is a fade state machine. A symbol eases in/out (its opacity, carried on
    /// <c>PlacedQuad.Color.w</c> → the billboard vertex alpha) instead of popping. Teeth: the default (infinite)
    /// deltaTime snaps to full opacity (byte-parity with the previous behaviour); a real small deltaTime fades IN over frames;
    /// a collision-suppressed symbol fades OUT while still drawn (both visible mid-transition); a stable symbol
    /// keeps its opacity across frames (no per-frame re-fade — the anti-blink property); and the point fade id
    /// is a FIXED-grid identity (independent of camera zoom, unlike the display-zoom dedup key it replaced).
    /// </summary>
    [TestFixture]
    public class SymbolFadeTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        // n copies of OneQuad()'s single glyph, sharing its bounds — identical bounds ⇒ identical SymbolBox ⇒
        // two symbols built from NQuads always overlap, so only the quad COUNT distinguishes them (used to tell
        // which of two co-located symbols won via LastQuadCount).
        private static List<SymbolQuad> NQuads(int n)
        {
            var quads = new List<SymbolQuad>(n);
            for (int i = 0; i < n; i++)
                quads.Add(new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                });
            return quads;
        }

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, float sortKey, string text, int feature, List<SymbolQuad> quads = null)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, quads ?? OneQuad(), float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default, textSizePx: 24f, paddingPx: 2f, sortKey: sortKey, text: text,
                featureIndex: feature, tileKey: 0L);

        // Point symbols draw through the WORLD path — the fade opacity (stream 1) does not
        // ride system.Mesh's vertex-colour alpha (BillboardVertex.Color.a); it lives on the world slot's
        // Opacity stream (WorldMeshReadback.MaxOpacity). tileKey defaults to 0L — every symbol in this file
        // uses it (fade/opacity assertions are position-independent).
        private static float MaxAlpha(SymbolPlacementSystem system, long tileKey = 0L)
            => system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh) ? WorldMeshReadback.MaxOpacity(mesh) : 0f;

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly IProjection Projection;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("SymbolFade_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Projection = cam.Projection;
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                // Point symbols draw through the world path — the demo tick needs its own
                // world base material for a live opacity/visibility read (see MaxAlpha's header).
                System = new SymbolPlacementSystem(cam, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        // ── The default (infinite) deltaTime SNAPS to full opacity — a single-Tick test renders symbols exactly
        //    as before the fade (byte-parity). ──
        // ── Telemetry provider (docs/telemetry-design.md): the placement system OWNS its results as a struct
        //    field, refreshes it at the end of Tick, and hands it out BY REFERENCE. ──
        [Test]
        public void Tick_RefreshesItsOwnTelemetryStruct_HandedOutByReference()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0);
            using var plan = new TestSymbolPlan(h.Projection);

            Assert.AreEqual(0, h.System.Telemetry.PlacedQuadCount,
                "before any Tick the provider's struct is still default — it reports levels, it does not invent them.");

            // Bind ONCE, by reference, then Tick. This is the tooth for the ref-return itself: `live` aliases the
            // provider's own field, so a later refresh is visible through it. Were the accessor to return BY VALUE,
            // `live` would be a snapshot copy taken before the Tick and every assertion below would read 0.
            ref readonly SymbolPlacementTelemetrySnapshot live = ref h.System.Telemetry;

            // TWO ticks before asserting a placed quad: the collision verdict is deferred by one Tick, so the
            // first pass places nothing. Asserting after one tick reads 0 and says nothing about the ref-return.
            h.System.Tick(in h.Frame, plan.Build(buffer), h.Atlas);
            h.System.Tick(in h.Frame, plan.Build(buffer), h.Atlas);

            Assert.AreEqual(1, h.System.LastQuadCount,
                "sanity: the label placed, so there is a non-zero level to carry.");
            Assert.AreEqual(h.System.LastQuadCount, live.PlacedQuadCount,
                "the struct must carry the pass's own results, seen through the reference taken BEFORE any Tick.");
            Assert.AreEqual(h.System.MirrorRebuildCount, live.MirrorRebuildCount);

            // And it keeps tracking: a further pass refreshes the same storage, still visible through `live`.
            int rebuildsSoFar = live.MirrorRebuildCount;
            h.System.Tick(in h.Frame, plan.Build(buffer), h.Atlas);

            Assert.AreEqual(h.System.MirrorRebuildCount, live.MirrorRebuildCount,
                "every Tick refreshes the provider's own field — the reference never goes stale.");
            Assert.GreaterOrEqual(live.MirrorRebuildCount, rebuildsSoFar,
                "MirrorRebuildCount is CUMULATIVE; it must never go backwards.");
        }

        [Test]
        public void Tick_DefaultDeltaTime_SnapsToFullOpacity()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, 0f, "A", 0);
            // The collision verdict applies one Tick late (HarvestCollision consumes the PREVIOUS Tick's
            // scheduled job) — a fade-neutral duplicate tick is needed before placement can be asserted.
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection); // default deltaTime = +inf
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);
            Assert.AreEqual(1, h.System.LastQuadCount, "the label places");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "default deltaTime snaps the fade to full opacity");
        }

        // ── A real small deltaTime fades the symbol IN over successive frames (alpha rises 0 → 1). ──
        [Test]
        public void Tick_SmallDeltaTime_FadesInOverFrames()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, 0f, "A", 0);

            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: 0.05f); // Verdict is one Tick late
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: 0.05f);
            float first = MaxAlpha(h.System);
            Assert.Greater(first, 0f, "it has started to appear");
            Assert.Less(first, 0.5f, "…but is only partway in after one 0.05s step (fade ≈ 0.3s)");

            for (int i = 0; i < 20; i++) h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: 0.05f);
            Assert.Greater(MaxAlpha(h.System), 0.99f, "after > 0.3s of steps it is fully faded in");
        }

        // ── A stable symbol keeps its opacity across frames — it does NOT re-fade every frame (the persistent
        //    record is the anti-blink property; a one-frame flip becomes a sub-perceptual alpha step, not a pop). ──
        [Test]
        public void Tick_StableSymbol_KeepsOpacity_NoReFade()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, 0f, "A", 0);
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection); // Verdict is one Tick late
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection); // snap to full
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection, deltaTime: 0.05f); // same id again, small step
            Assert.Greater(MaxAlpha(h.System), 0.99f, "a persistent label stays at full opacity — no re-fade");
        }

        // ── A collision-suppressed symbol fades OUT while still drawn: mid-transition BOTH the fading-out loser
        //    and the fading-in winner are emitted (2 quads), then it settles to just the winner (1). Before the fade, the
        //    loser popped instantly (1 quad throughout). ──
        [Test]
        public void Tick_CollisionSuppression_FadesOutWhileStillDrawn()
        {
            using var h = new Harness();
            // A wins alone first (lower sort key = higher priority). Then B (higher priority) appears on the SAME
            // anchor → A is suppressed and must fade out, not vanish.
            var aOnly = new SymbolTileBuffer();
            AddPoint(aOnly, h.Origin, 10f, "A", 0);
            var both = new SymbolTileBuffer();
            AddPoint(both, h.Origin, 10f, "A", 0);  // was placed → now the loser (fades out)
            AddPoint(both, h.Origin, 5f, "B", 1);   // higher priority → the winner (fades in)

            // Each candidate-set change needs its own extra Tick before its placement can be asserted —
            // the collision verdict a Tick's emit reads is the one harvested from the PREVIOUS Tick.
            h.System.TickSymbols(in h.Frame, aOnly, h.Atlas, h.Projection);
            h.System.TickSymbols(in h.Frame, aOnly, h.Atlas, h.Projection); // A at full opacity
            Assert.AreEqual(1, h.System.LastQuadCount);

            h.System.TickSymbols(in h.Frame, both, h.Atlas, h.Projection, deltaTime: 0.1f);
            h.System.TickSymbols(in h.Frame, both, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(2, h.System.LastQuadCount, "mid-transition both draw: A fading out + B fading in");

            for (int i = 0; i < 10; i++) h.System.TickSymbols(in h.Frame, both, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "settled: only the winner B remains, A has faded to 0");
        }

        // ── A stable collision loser eases fully to 0 and STAYS hidden (no re-pump, no partial-opacity hover). The
        //    total-order collision tiebreak makes the survivor set a fixed point, so a deterministic loser fades out
        //    cleanly with a plain ease — no sticky/cooldown machinery. (The earlier "stuck bright" oscillation is
        //    fixed at the root in SymbolCandidateCollisionTests.Collision_SameFeatureAnchors_*.) ──
        [Test]
        public void Tick_StableCollisionLoser_FadesFullyOutAndStaysHidden()
        {
            using var h = new Harness();
            var bOnly = new SymbolTileBuffer();
            AddPoint(bOnly, h.Origin, 10f, "B", 1);
            // A deterministically beats B every frame (lower sort key) at the same anchor — a STABLE outcome.
            var aBeatsB = new SymbolTileBuffer();
            AddPoint(aBeatsB, h.Origin, 5f, "A", 0);
            AddPoint(aBeatsB, h.Origin, 10f, "B", 1);

            // Each candidate-set change needs its own extra Tick before its placement can be asserted.
            h.System.TickSymbols(in h.Frame, bOnly, h.Atlas, h.Projection);
            h.System.TickSymbols(in h.Frame, bOnly, h.Atlas, h.Projection); // B alone, snap to full
            Assert.AreEqual(1, h.System.LastQuadCount, "B places alone");

            h.System.TickSymbols(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
            h.System.TickSymbols(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(2, h.System.LastQuadCount, "mid-transition: A fading in, B fading out (both drawn briefly)");

            // B loses every frame → it eases to 0 and stays there; only A remains, and it settles at full opacity.
            for (int i = 0; i < 8; i++) h.System.TickSymbols(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a stable loser fully fades out — only the winner A remains");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…and the winner is at full opacity, not stuck partial");
        }

        // ── The fade map does not accumulate invisible identities. A candidate staged every frame and placed by
        //    none (the stable collision loser above) never parks a record AT ALL — its opacity never leaves 0, so
        //    EaseFade suppresses the store rather than writing a 0 that nothing would collect (the decay sweep
        //    skips ids seen this frame, and a staged candidate is always seen). This is a per-frame COST, not
        //    just memory: DecayUnseenFadeSymbols walks every held record each Tick, so a dense view — where
        //    candidates outrun placed symbols by an order of magnitude — would otherwise pay that sweep over
        //    symbols nobody can see. Retention is invisible on screen, hence a state assertion rather than a
        //    rendered one. (The live-record-then-DROPPED transition is a different path, covered behaviourally
        //    by Tick_DepartingRecord_/Tick_CoverageFadingRecord_FadesOut_InsteadOfPopping.) ──
        [Test]
        public void Tick_StableCollisionLoser_LeavesNoFadeRecordBehind()
        {
            using var h = new Harness();
            // Same fixture as the test above: A beats B at the same anchor every frame (lower sort key), so B is
            // a candidate on every Tick and a survivor on none.
            var aBeatsB = new SymbolTileBuffer();
            AddPoint(aBeatsB, h.Origin, 5f, "A", 0);
            AddPoint(aBeatsB, h.Origin, 10f, "B", 1);

            for (int i = 0; i < 12; i++) h.System.TickSymbols(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);

            // PRECONDITION — without these the count assertion is vacuous. B must still be STAGED (so it still
            // eases, and could still park a record); it must simply never place. If a cull ever removed B from
            // staging instead, the assertion below would pass while proving nothing about retention.
            Assert.AreEqual(2, h.System.LastCandidateCount, "both labels are still staged — B was not culled away");
            Assert.AreEqual(1, h.System.LastQuadCount, "…but only the winner A draws; B lost every collision");

            Assert.AreEqual(1, h.System.LiveFadeSymbolCount,
                "only the VISIBLE label keeps a fade record — a perpetual loser must never park one, because a " +
                "stored 0 is never collected: DecayUnseenFadeSymbols skips ids seen this frame, and a staged " +
                "candidate is always seen.");

            // STABILITY — the count must STAY at 1, not merely reach it once. An implementation that periodically
            // cleared the map would satisfy a single sample and re-accumulate in between; this rejects it.
            for (int i = 0; i < 6; i++)
            {
                h.System.TickSymbols(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
                Assert.AreEqual(1, h.System.LiveFadeSymbolCount,
                    $"the fade map must stay bounded across frames (extra tick {i + 1})");
            }
        }

        // ── A symbol far past the far-plane cull distance is skipped BEFORE projection/collision; the near symbol
        //    (at the look-at, distance 0) still places. (The Core distance math is pinned in SymbolFarPlaneCullTests;
        //    this proves the wiring; the far anchor at 1e8 m dwarfs any plausible far×fraction, so it is robust to
        //    the harness's exact far value.) ──
        [Test]
        public void Tick_FarSymbol_IsDistanceCulled_WhileNearSymbolPlaces()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, 0f, "N", 0);
            AddPoint(buffer, h.Origin + new double3(1e8, 0, 1e8), 0f, "F", 1); // far past any horizon radius
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection); // Verdict is one Tick late
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);
            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "the far label is skipped pre-projection");
            Assert.AreEqual(1, h.System.LastQuadCount, "only the near label places");
        }

        // ── Retain-as-departing: when a tile leaves cover its symbols are flagged DEPARTING (SymbolDeparting, set
        //    from CollectInto's active/departing split) so they FADE OUT in place instead of popping (the B-3
        //    distance / horizon culls' companion fade-out trigger — the tile-coverage pre-cull now runs
        //    upstream of the batch and pops instead, see SymbolTileCoverageFilter). Simulated here by building
        //    the batch with activeCount: the same symbol is active first, then departing (activeCount excludes it). ──
        [Test]
        public void Tick_DepartingRecord_FadesOut_InsteadOfPopping()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0);

            // The DEPARTING flag is the subject, so these ticks go through the plan directly: production
            // carries it as SymbolGatherPlan.Departing (filled from the store's per-record IsDeparting),
            // which is what TestSymbolPlan's departingTiles stamps — the plan-path equivalent of the
            // retired batch builder's activeCount knob.
            using var plan = new TestSymbolPlan(h.Projection);
            var departingTiles = new HashSet<long> { 0L }; // the symbol above carries TileKey = 0L

            // 1) Active (not departing) → it places and snaps to full opacity.
            h.System.Tick(in h.Frame, plan.Build(buffer), h.Atlas); // Verdict is one Tick late
            h.System.Tick(in h.Frame, plan.Build(buffer), h.Atlas); // default dt → snap to full
            Assert.AreEqual(1, h.System.LastQuadCount, "the active label places");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…at full opacity");

            // 2) The tile leaves cover → the SAME symbol is now departing. It must keep drawing while it
            //    fades, not vanish for a frame.
            SymbolGatherPlan departingPlan = plan.Build(buffer, departingTiles: departingTiles);
            Assert.AreEqual(1, departingPlan.Departing[0],
                "sanity: the record is flagged departing — without this the rest of the test would be " +
                "asserting an ordinary fade and could not fail for the reason it names.");
            h.System.Tick(in h.Frame, departingPlan, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a departing-but-visible label keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is dropped — counted as departing (not coverage/distance).
            for (int i = 0; i < 10; i++)
                h.System.Tick(in h.Frame, plan.Build(buffer, departingTiles: departingTiles), h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the departing label is fully skipped");
            Assert.Greater(h.System.LastDepartingCulledCount, 0, "…and its skip is attributed to departing telemetry");
        }

        // ── REVISION 2: coverage-fading (a tile's on-screen coverage crossed below threshold, flagged
        //    SymbolCoverageFading by SymbolTileCoverageFilter/Build — a SEPARATE flag from SymbolDeparting, since
        //    the tile is still ACTIVE, just small on screen) FADES OUT in place instead of popping — mirrors
        //    Tick_DepartingRecord_FadesOut_InsteadOfPopping above, one flag over. ──
        [Test]
        public void Tick_CoverageFadingRecord_FadesOut_InsteadOfPopping()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, sortKey: 0f, text: "A", feature: 0); // TileKey = 0L

            // The coverage-fading FLAG is the subject here, so these ticks go through the plan directly
            // rather than the TickSymbols convenience — the production path carries the flag as a per-record
            // SymbolTileCoverageFilter.Fade decision, which is what TestSymbolPlan's coverageFadingTiles sets.
            using var plan = new TestSymbolPlan(h.Projection);

            // 1) Not coverage-fading (no coverageFadingTiles) → places and snaps to full opacity.
            h.System.Tick(in h.Frame, plan.Build(buffer), h.Atlas); // Verdict is one Tick late
            h.System.Tick(in h.Frame, plan.Build(buffer), h.Atlas);
            Assert.AreEqual(1, h.System.LastQuadCount, "the label places");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…at full opacity");

            // 2) The SAME symbol's tile crosses below the coverage threshold — classified Fade. It
            //    must keep drawing while it fades, not vanish for a frame.
            var fadingTiles = new HashSet<long> { 0L };
            SymbolGatherPlan fadingPlan = plan.Build(buffer, coverageFadingTiles: fadingTiles);
            Assert.AreEqual(1, fadingPlan.CoverageFading[0],
                "sanity: the record is flagged coverage-fading — without this the rest of the test would be " +
                "asserting an ordinary fade and could not fail for the reason it names.");
            h.System.Tick(in h.Frame, fadingPlan, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a coverage-fading-but-visible label keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is dropped — counted as coverage-fading (not departing).
            for (int i = 0; i < 10; i++)
                h.System.Tick(in h.Frame, plan.Build(buffer, coverageFadingTiles: fadingTiles), h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the coverage-fading label is fully skipped");
            Assert.Greater(h.System.LastCoverageFadingCulledCount, 0, "…and its skip is attributed to coverage-fading telemetry");
        }

        // ── The point fade id is a FIXED-grid identity: it collapses anchors within a few metres (a cross-tile
        //    no-op) and separates distinct ones — and it takes NO zoom parameter, so it cannot drift as the
        //    camera zooms (the bug a per-frame display-zoom grid would cause). ──
        [Test]
        public void PointFadeId_FixedGrid_CollapsesNearby_SeparatesFar()
        {
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);
            var table = new SymbolStringTable(); // PointFadeId takes interned ids, not raw strings
            int t = table.Intern("T");
            int u = table.Intern("U");
            Assert.AreEqual(SymbolPlacementSystem.PointFadeId(a, 0, t),
                            SymbolPlacementSystem.PointFadeId(a + new double3(2, 0, -2), 0, t),
                            "anchors within the fixed fade grid share one identity (the cross-tile no-op)");
            Assert.AreNotEqual(SymbolPlacementSystem.PointFadeId(a, 0, t),
                               SymbolPlacementSystem.PointFadeId(a + new double3(50, 0, 0), 0, t),
                               "anchors many metres apart are distinct labels");
            Assert.AreNotEqual(SymbolPlacementSystem.PointFadeId(a, 0, t),
                               SymbolPlacementSystem.PointFadeId(a, 0, u),
                               "different text is a different label even at the same anchor");
        }

        // ── The icon analogue — two co-located icon symbols (text=null, distinct icon-image) must get
        //    DISTINCT fade ids (pre-fix they'd collide: text==null for both). Same icon-image at the same
        //    anchor shares an id (the seamless no-op icons now get too). A text symbol's id is unchanged when
        //    iconImage is omitted/explicitly-null (the #1 invariant: guard-skip, not `?? 0`). ──
        [Test]
        public void PointFadeId_IconIdentity_DistinctIconsSeparate_SameIconShares_TextUnaffected()
        {
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);
            var table = new SymbolStringTable(); // PointFadeId takes interned ids, not raw strings
            int iconA = table.Intern("a");
            int iconB = table.Intern("b");
            int paris = table.Intern("Paris");

            Assert.AreNotEqual(SymbolPlacementSystem.PointFadeId(a, 0, 0, iconA),
                                SymbolPlacementSystem.PointFadeId(a, 0, 0, iconB),
                                "same cell/layer, text=null, different icon-image → distinct fade ids");

            Assert.AreEqual(SymbolPlacementSystem.PointFadeId(a, 0, 0, iconA),
                             SymbolPlacementSystem.PointFadeId(a, 0, 0, iconA),
                             "the same icon-image at the same anchor shares one fade id");

            Assert.AreEqual(SymbolPlacementSystem.PointFadeId(a, 0, paris),
                             SymbolPlacementSystem.PointFadeId(a, 0, paris, 0),
                             "a text label's fade id is unchanged whether iconImageId is omitted or explicitly 0");
        }

        // ── ONE canonical identity: the point fade id partitions symbols IDENTICALLY to the store's
        //    dedup cell — both quantize to CrossTileSymbolKey.CanonicalGridMeters. A pair co-located within the
        //    canonical grid shares BOTH a PointFadeId and a dedup cell (CrossTileSymbolKey.For equality); a pair
        //    further apart than the grid shares NEITHER. This binds the fade identity to the SAME constant the
        //    dedup uses, not merely to "some fixed grid": diverge the fade grid from CanonicalGridMeters and the
        //    beyond-grid pair would agree in fade but split in dedup — which this asserts cannot happen. ──
        [Test]
        public void PointFadeId_AgreesWithDedupCell()
        {
            const double grid = CrossTileSymbolKey.CanonicalGridMeters;
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);
            double3 near = a + new double3(grid * 0.25, 0, -grid * 0.25);   // same canonical cell
            double3 far = a + new double3(grid * 4.0, 0, 0);                // clearly a different cell
            var table = new SymbolStringTable(); // PointFadeId takes interned ids, not raw strings
            int t = table.Intern("T");

            // The dedup cell for a point symbol (same layer/text/icon over these anchors) — the canonical identity
            // the store compares. Equality here IS the dedup grouping.
            static bool SameDedupCell(double3 p, double3 q)
                => CrossTileSymbolKey.For(p, 0, "T", null, grid).Equals(CrossTileSymbolKey.For(q, 0, "T", null, grid));
            bool SameFadeId(double3 p, double3 q)
                => SymbolPlacementSystem.PointFadeId(p, 0, t) == SymbolPlacementSystem.PointFadeId(q, 0, t);

            // Co-located pair: shares BOTH — one canonical identity.
            Assert.IsTrue(SameDedupCell(a, near), "sanity: the near pair is one dedup cell");
            Assert.IsTrue(SameFadeId(a, near), "co-located labels share a fade id (agreeing with the dedup cell)");

            // Beyond-grid pair: shares NEITHER — fade partition tracks the dedup partition.
            Assert.IsFalse(SameDedupCell(a, far), "sanity: the far pair splits across dedup cells");
            Assert.IsFalse(SameFadeId(a, far), "labels beyond the canonical grid get distinct fade ids (no dedup, no shared fade)");

            // The proof, stated as an equivalence: fade grouping ⟺ dedup grouping for every pair.
            Assert.AreEqual(SameDedupCell(a, near), SameFadeId(a, near), "fade grouping == dedup grouping (near)");
            Assert.AreEqual(SameDedupCell(a, far), SameFadeId(a, far), "fade grouping == dedup grouping (far)");
        }

        // ── incumbency plumbing ──────────────────────────────────────────────────────────────────────────────
        // End-to-end coverage for the _placedLastFrame → WasPlacedLastFrame plumbing — untested anywhere else
        // (SymbolCandidateCollisionTests sets WasPlacedLastFrame by hand, never through
        // SymbolPlacementSystem; SymbolStageJobTests feeds it as a raw input; SymbolProjectionJobTests
        // arranges distinct sort keys "so incumbency is a no-op"). X and Y sit at the SAME anchor with an
        // EQUAL SortKey, so SymbolCollision.ComparePlacementOrder falls to its incumbency term (SymbolCollision.cs:144)
        // strictly BEFORE the FeatureIndex term (:145) — if a future change reorders those two lines this test
        // breaks loudly, which is correct. Distinct Text ⇒ distinct PointFadeId even at a shared anchor
        // (SymbolPlacementSystem.cs's PointFadeId folds the interned TextId into the FNV hash). NQuads(n) discriminates
        // the winner via LastQuadCount — a faded-to-0 loser is `continue`d before it can emit (SymbolPlacementSystem.cs:608)
        // and contributes 0 quads to the total (WorldSymbolRenderer.Emit's returned QuadCount, accumulated at :615/626).
        [Test]
        public void Tick_IncumbentKeepsSlot_OverEqualSortKeyNewcomer()
        {
            using var h = new Harness();
            var yOnly = new SymbolTileBuffer();
            AddPoint(yOnly, h.Origin, sortKey: 0f, text: "B", feature: 1, quads: NQuads(2)); // incumbent
            var xAndY = new SymbolTileBuffer();
            AddPoint(xAndY, h.Origin, sortKey: 0f, text: "A", feature: 0, quads: NQuads(1)); // newcomer
            AddPoint(xAndY, h.Origin, sortKey: 0f, text: "B", feature: 1, quads: NQuads(2)); // incumbent

            // Tick 1: Y alone places (default deltaTime snaps it to full opacity) — _placedLastFrame == { fadeId(Y) }.
            // The verdict a Tick's stage/emit reads is one Tick behind — duplicate (same args) so the
            // scheduled collision has been harvested before the assertion. This ALSO matters structurally here:
            // Y must be the harvested incumbent BEFORE the {X,Y} Tick below stages, or StageJob never sees
            // WasPlacedLastFrame=true for Y and the tiebreak this test pins never engages.
            h.System.TickSymbols(in h.Frame, yOnly, h.Atlas, h.Projection);
            h.System.TickSymbols(in h.Frame, yOnly, h.Atlas, h.Projection);
            Assert.AreEqual(2, h.System.LastQuadCount, "Y places alone");

            // Tick 2: X and Y tie on SortKey. Y is the incumbent ⇒ it wins the tiebreak over the newcomer X ⇒
            // X (a brand-new fade id, never above FadeEpsilon) fades to 0 in the same tick and is skipped.
            // The FIRST {X,Y} Tick here only re-emits Y off the PRIOR (Y-alone) harvested verdict — it is the
            // SECOND {X,Y} Tick (duplicated) whose harvest reflects the actual {X,Y} collision staged with Y's
            // incumbency bias, which is the real tiebreak this test proves.
            h.System.TickSymbols(in h.Frame, xAndY, h.Atlas, h.Projection, deltaTime: 0.1f);
            h.System.TickSymbols(in h.Frame, xAndY, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(2, h.System.LastQuadCount, "the incumbent Y keeps its slot over the equal-sort-key newcomer X");

            // Control (falsifies the above): a FRESH harness/system has no incumbents at all, so with the SAME
            // { X, Y } the collision resolves purely on FeatureIndex — X (0 < 1) wins instead of Y. Two ticks:
            // the first only schedules (nothing harvested yet on a virgin system); the second harvests
            // that FeatureIndex-only-tiebreak collision's verdict.
            using var fresh = new Harness();
            var controlBuffer = new SymbolTileBuffer();
            AddPoint(controlBuffer, fresh.Origin, sortKey: 0f, text: "A", feature: 0, quads: NQuads(1));
            AddPoint(controlBuffer, fresh.Origin, sortKey: 0f, text: "B", feature: 1, quads: NQuads(2));
            fresh.System.TickSymbols(in fresh.Frame, controlBuffer, fresh.Atlas, fresh.Projection);
            fresh.System.TickSymbols(in fresh.Frame, controlBuffer, fresh.Atlas, fresh.Projection);
            Assert.AreEqual(1, fresh.System.LastQuadCount,
                "control: with no prior incumbent, FeatureIndex alone decides — X wins, proving tick 2's outcome above was incumbency, not a fixed bias");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolFarDistanceCullGatherTests — the far-plane cull under a fixed, injected far-plane policy
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolFarDistanceCullGatherTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, string text, int feature)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: new SymbolPaint { TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f },
                textSizePx: 24f, paddingPx: 2f, sortKey: 0f, text: text, featureIndex: feature, tileKey: 0L,
                materialIndex: 0);

        // A far-plane policy that returns a FIXED distance regardless of altitude/tilt/FOV — so a test can pin the
        // exact far the cull compares against (MapCamera.CurrentFarMetres reads this) instead of deriving it from
        // the geometry.
        private sealed class FixedFarPlane : IFarPlanePolicy
        {
            private readonly double _far;
            public FixedFarPlane(double far) { _far = far; }
            public double FarMetres(double altitude, Angle tilt, double fovDegVertical, double aspect) => _far;
            public string Name => "fixed";
        }

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly MapCamera Cam;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly IProjection Projection;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("FarDistanceCull_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                Cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = Cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Projection = Cam.Projection;
                // CameraRelativePosition left at (0,0,0): the camera sits at the look-at, so a symbol's distance from
                // the camera equals its render-space offset from the scene origin — the quantity the test controls.
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                System = new SymbolPlacementSystem(Cam, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        /// <summary>THE headline tooth: with the far plane fixed at 10 000 m, a symbol 6 000 m from the camera is
        /// hard-skipped when the cull fraction is 0.5 (cull distance 5 000 m &lt; 6 000 m) — never a collision
        /// candidate, attributed to the distance bucket — but stages normally when the fraction is 0.8 (cull
        /// distance 8 000 m &gt; 6 000 m). Same symbol, same far plane, only the knob moves: pins that
        /// <c>SymbolMaxDistanceFraction</c> scales the cull distance as a fraction of the far plane.</summary>
        [Test]
        public void FarSymbol_CulledOrStaged_TracksTheDistanceFraction()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, "N", 0);                                    // at the look-at ⇒ distance 0
            AddPoint(buffer, h.Origin + new double3(6000.0, 0.0, 0.0), "F", 1);    // 6 000 m from the camera
            h.Cam.FarPlanePolicy = new FixedFarPlane(10000.0); // CurrentFarMetres ≡ 10 000 m

            // Tight fraction: cull distance = 0.5 × 10 000 = 5 000 m ⇒ the 6 000 m symbol is culled.
            h.System.SymbolMaxDistanceFraction = 0.5;
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);

            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "the 6 000 m label is past 0.5 × far (5 000 m) ⇒ culled");
            Assert.AreEqual(1, h.System.LastCandidateCount, "…only the near label stages as a collision candidate");

            // Loose fraction: cull distance = 0.8 × 10 000 = 8 000 m ⇒ the SAME symbol now survives.
            h.System.SymbolMaxDistanceFraction = 0.8;
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);

            Assert.AreEqual(0, h.System.LastDistanceCulledCount, "the 6 000 m label is within 0.8 × far (8 000 m) ⇒ kept");
            Assert.AreEqual(2, h.System.LastCandidateCount, "…both labels stage as collision candidates");
        }

        /// <summary>The far plane is the second lever: with the fraction held at 1.0, the same 6 000 m symbol is
        /// culled when the far plane is 5 000 m and kept when it is 10 000 m. Guards against a regression that read
        /// a constant distance instead of the live <c>Camera.farClipPlane</c>.</summary>
        [Test]
        public void FarSymbol_CulledOrStaged_TracksTheFarPlane()
        {
            using var h = new Harness();
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, h.Origin, "N", 0);
            AddPoint(buffer, h.Origin + new double3(6000.0, 0.0, 0.0), "F", 1);
            h.System.SymbolMaxDistanceFraction = 1.0;

            h.Cam.FarPlanePolicy = new FixedFarPlane(5000.0);
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);
            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "6 000 m > far 5 000 m ⇒ culled");

            h.Cam.FarPlanePolicy = new FixedFarPlane(10000.0);
            h.System.TickSymbols(in h.Frame, buffer, h.Atlas, h.Projection);
            Assert.AreEqual(0, h.System.LastDistanceCulledCount, "6 000 m < far 10 000 m ⇒ kept");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolGatherMemoTests — GatherIntoMirror memoizes compaction on WinnerSetVersion
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SymbolPlacementSystem.GatherIntoMirror"/> memoizes its heavy compaction on
    /// <see cref="SymbolGatherPlan.WinnerSetVersion"/> — a same-source, same-version frame runs only
    /// the three per-frame masks (Departing/CoverageFading/Dropped), not the full
    /// pool rebuild. These are the CONTENT teeth: byte-identity across held frames, invalidation on a real
    /// version change with the winner SET changing, per-frame mask tracking on a held mirror, and the memo-hit
    /// path's own zero-GC guarantee. <see cref="SymbolPlacementSystem.MirrorRebuildCount"/> is the discriminating
    /// signal throughout — without it every test here would pass trivially against an unmemoized implementation.
    ///
    /// <para>A REAL front swap through the production <see cref="MapRenderer.Unity.Text.SymbolSubsystem"/>
    /// (<c>Memo_RealFrontSwap_Invalidates</c>) and a restyle through the subsystem
    /// (<c>Memo_RestyleBetweenTicks_Invalidates</c>) live in <c>SymbolReconcileAsyncTests</c> — that fixture
    /// already owns the async pump harness (UseImmediateGlyphs / DriveTileBytesReady / PumpToQuiescence) both
    /// need, so a second copy of it here would duplicate non-trivial async machinery for no benefit.</para>
    ///
    /// Fixture style follows <c>SymbolGatherParityTests</c> / <c>SymbolGatherPlanDropMaskTests</c>: a real
    /// <see cref="SymbolTileStore"/> seeded via <see cref="SymbolTileBlockBaker"/>, a real
    /// <see cref="SymbolPlacementSystem"/> behind a throwaway camera/material (<see cref="LpsHarness"/> — a plain
    /// camera, no real look-at, since <see cref="SymbolPlacementSystem.GatherIntoMirror"/>/<c>CopyMirrorInto</c>
    /// never touch the camera; <see cref="TickHarness"/> adds a real look-at only for the tests below that drive
    /// a full <c>Tick</c>). The gather/mirror comparison is camera-independent. The content-diff oracle is the
    /// shared <see cref="SymbolBatchDiff.FirstDifference"/>. The REFERENCE gather alternates between TWO
    /// persistent <see cref="SymbolGatherPlan"/> objects fed to ONE reference <see cref="SymbolPlacementSystem"/>
    /// — a different instance identity than the previous call always mismatches <c>_mirrorSource</c>, so the
    /// reference NEVER memo-hits (a trustworthy ground truth) with zero per-tick native allocation (a
    /// fresh-plan-per-tick oracle would leak <c>Allocator.Persistent</c> lists).
    /// </summary>
    [TestFixture]
    public class SymbolGatherMemoTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        private static readonly WebMercatorProjection P = new WebMercatorProjection();

        private static (List<SymbolQuad> Quads, float2 BoundsMin, float2 BoundsMax) OneQuad(float u) => (
            new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            float2.zero, new float2(18f, 18f));

        private static SymbolTileBuffer PointSymbol(double3 anchor, string text, int feature, long tileKey, float u)
        {
            var layout = OneQuad(u);
            return TestSymbolTileBuffer.Point(anchor, layout.Quads, layout.BoundsMin, layout.BoundsMax,
                text: text, textSizePx: 20f, paddingPx: 2f, featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);
        }

        private static SymbolTileStore.Key Key(TileId t) => new SymbolTileStore.Key("s", t);
        private static long Tk(TileId t) => SymbolTileKey.Pack(t);

        private static void SeedTile(SymbolTileStore store, TileId tile, SymbolTileBuffer buffer)
        {
            int gen = store.BeginBuild(Key(tile));
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, block), "sanity: block committed");
        }

        // Fills `plan` from `store`'s current winner set at `version`, with optional per-tile Fade/Drop/Departing
        // overrides (all default Keep/not-departing). Mirrors SymbolGatherPlanDropMaskTests.BuildMaskedPlan/
        // BuildReferencePlan but generalized over which per-frame override applies to which tile, since these tests
        // need to vary EITHER the winner set (a different store) OR just the masks (same store, same records).
        private static void BuildPlan(SymbolTileStore store, SymbolGatherPlan plan, int version,
            long dropTileKey = -1, long fadeTileKey = -1, long departingTileKey = -1)
        {
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, quantizeMeters: 1.0, out _);

            var decisions = new List<byte>(blockId.Count);
            var departing = new List<byte>(blockId.Count);
            for (int i = 0; i < blockId.Count; i++)
            {
                long tk = store.OrderedBlocks[blockId[i]].TileKey;
                byte decision = tk == dropTileKey ? SymbolTileCoverageFilter.Drop
                              : tk == fadeTileKey ? SymbolTileCoverageFilter.Fade
                              : SymbolTileCoverageFilter.Keep;
                decisions.Add(decision);
                departing.Add(tk == departingTileKey ? (byte)1 : isDeparting[i]);
            }
            plan.Build(blockId, localIndex, departing, decisions, store.OrderedBlocks, version);
        }

        // Owns the LPS + the throwaway Unity resources the fixture creates (mirrors SymbolGatherParityTests'
        // LpsHarness) — GatherIntoMirror/CopyMirrorInto touch neither the camera nor the material.
        private sealed class LpsHarness : System.IDisposable
        {
            public readonly SymbolPlacementSystem Lps;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public LpsHarness()
            {
                _camGo = new GameObject("GatherMemo_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(64, 64, 0);
                uCam.targetTexture = _rt;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 0, Longitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Lps = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Lps.Dispose();
                UnityEngine.Object.DestroyImmediate(_camGo);
                UnityEngine.Object.DestroyImmediate(_rt);
                UnityEngine.Object.DestroyImmediate(_baseMaterial);
            }
        }

        // A REAL look-at + atlas (mirrors SymbolGatherPlanDropMaskTests.Harness) — needed by any test that drives
        // a full Tick (projection/staging/collision/emit), not just GatherIntoMirror/CopyMirrorInto.
        private sealed class TickHarness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly double3 Origin;
            public readonly GlyphAtlasTexture Atlas;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public TickHarness()
            {
                _camGo = new GameObject("GatherMemo_TickCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(256, 256, 0);
                uCam.targetTexture = _rt;
                var lookAt = new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt, zoom: 12.0, heading: 0.0, tilt: 0.0), projection: P);
                Origin = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
                var glyphAtlas = new GlyphAtlas();
                glyphAtlas.Append(glyph, 0);
                Atlas = new GlyphAtlasTexture();
                Atlas.Upload(glyphAtlas);
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                System = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                UnityEngine.Object.DestroyImmediate(_camGo);
                UnityEngine.Object.DestroyImmediate(_rt);
                UnityEngine.Object.DestroyImmediate(_baseMaterial);
            }
        }

        // ═══ N successive same-version gathers must stay a memo HIT and byte-match a fresh gather ═══

        [Test]
        public void Memo_NTicksNoTileEvent_MirrorByteIdenticalToFreshGather()
        {
            var tile = new TileId { Z = 6, X = 10, Y = 10 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness(); // ONE reference system, TWO alternating plan objects below
            var plan = new SymbolGatherPlan();
            var refPlanA = new SymbolGatherPlan();
            var refPlanB = new SymbolGatherPlan();
            try
            {
                const int frames = 4;
                for (int f = 0; f < frames; f++)
                {
                    BuildPlan(store, plan, version: 0); // SAME version every frame — no tile event
                    harness.Lps.GatherIntoMirror(plan);
                    Assert.AreEqual(1, harness.Lps.MirrorRebuildCount,
                        $"frame {f}: no tile event ever occurred — the mirror must stay memo-HIT after the first rebuild");

                    SymbolGatherPlan refPlan = (f % 2 == 0) ? refPlanA : refPlanB; // alternating identity ⇒ ref never memo-hits
                    BuildPlan(store, refPlan, version: f);
                    refHarness.Lps.GatherIntoMirror(refPlan);

                    var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                    var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                    Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                        $"frame {f}: a held mirror must stay byte-identical to a fresh gather over the same content");
                }
            }
            finally { plan.Dispose(); refPlanA.Dispose(); refPlanB.Dispose(); store.Clear(); }
        }

        // ═══ Rebuilding the SAME plan object with DIFFERENT content at a FIXED WinnerCount and a bumped
        //          version must invalidate the memo — the coupled constraint: vary the winner SET (a
        //          different tile), never the record count, or the release-build count backstop rescues a broken
        //          version key and this row's RED-verify goes vacuously green. ═══

        [Test]
        public void Memo_VersionChange_Invalidates()
        {
            var tileA = new TileId { Z = 6, X = 20, Y = 20 };
            var tileB = new TileId { Z = 6, X = 21, Y = 20 };
            long keyA = Tk(tileA), keyB = Tk(tileB);
            var storeA = new SymbolTileStore(cacheCap: 8);
            var storeB = new SymbolTileStore(cacheCap: 8);
            SeedTile(storeA, tileA, PointSymbol(new double3(100, 0, 200), "a", 1, keyA, 0.1f));
            SeedTile(storeB, tileB, PointSymbol(new double3(300, 0, 400), "b", 2, keyB, 0.2f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            var refPlanA = new SymbolGatherPlan();
            var refPlanB = new SymbolGatherPlan();
            try
            {
                BuildPlan(storeA, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");
                Assert.AreEqual(1, plan.WinnerCount, "sanity: tile A alone is one winner");

                BuildPlan(storeA, refPlanA, version: 0);
                refHarness.Lps.GatherIntoMirror(refPlanA);
                var got1 = new SymbolBatch(); harness.Lps.CopyMirrorInto(got1);
                var want1 = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want1);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want1, got1), "frame 1 mirror must match tile A's content");

                // SAME plan object, a DIFFERENT store's content (tile A → tile B), WinnerCount fixed at 1, bumped version.
                BuildPlan(storeB, plan, version: 1);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(2, harness.Lps.MirrorRebuildCount, "a version change must trigger a real rebuild, not a memo hit");
                Assert.AreEqual(1, plan.WinnerCount, "coupled constraint: WinnerCount stays fixed at 1 across the swap");

                BuildPlan(storeB, refPlanB, version: 0);
                refHarness.Lps.GatherIntoMirror(refPlanB);
                var got2 = new SymbolBatch(); harness.Lps.CopyMirrorInto(got2);
                var want2 = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want2);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want2, got2),
                    "frame 2 mirror must reflect tile B's content, not a stale memo hit off tile A");
            }
            finally { plan.Dispose(); refPlanA.Dispose(); refPlanB.Dispose(); storeA.Clear(); storeB.Clear(); }
        }

        // ═══ Masks (Departing/CoverageFading) are per-frame inputs, legitimately varying at a FIXED version
        //     — must be tracked on a HELD (memo-hit) mirror, not frozen from the first rebuild. Fixed
        //     WinnerCount throughout (else AssertMemoPlanMatchesMirror fires for the wrong reason). Two
        //     single-mask tests, so a cross-wire between the two masks can't hide behind a "both flipped
        //     together" test. ═══

        // Two independent single-mask flips: a single test that flipped Departing and CoverageFading
        // TOGETHER would let an implementation that copied either source mask into BOTH destinations
        // (e.g. WritePerFrameMasks writing plan.Departing into both _mirrorSymbolDeparting AND
        // _mirrorSymbolCoverageFading) pass — both masks would read true either way.
        // Each test below flips exactly ONE mask and asserts the OTHER stayed at its unflipped value, so a
        // mask-to-mask cross-wire fails on the "unchanged" assertion even though the "changed" one still passes.

        [Test]
        public void Memo_DepartingFlip_TrackedWhilePoolsHeld_CoverageFadingUnchanged()
        {
            var tile = new TileId { Z = 6, X = 40, Y = 40 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            var refPlan = new SymbolGatherPlan();
            try
            {
                BuildPlan(store, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");

                // SAME version (no tile event) — ONLY Departing flips; CoverageFading stays Keep (untouched).
                BuildPlan(store, plan, version: 0, departingTileKey: key);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "a mask-only change at a fixed version must stay a memo HIT");

                BuildPlan(store, refPlan, version: 0, departingTileKey: key);
                refHarness.Lps.GatherIntoMirror(refPlan);

                var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                    "the per-frame masks must be tracked on a HELD mirror, not frozen from the first rebuild");
                Assert.IsTrue(got.SymbolDeparting[0], "Departing must reflect the flip even on a memo-hit frame");
                Assert.IsFalse(got.SymbolCoverageFading[0],
                    "CoverageFading must stay UNCHANGED — a mask cross-wire (e.g. Departing's source copied into both destinations) would wrongly flip this too");
            }
            finally { plan.Dispose(); refPlan.Dispose(); store.Clear(); }
        }

        [Test]
        public void Memo_CoverageFadingFlip_TrackedWhilePoolsHeld_DepartingUnchanged()
        {
            var tile = new TileId { Z = 6, X = 41, Y = 40 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            using var refHarness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            var refPlan = new SymbolGatherPlan();
            try
            {
                BuildPlan(store, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");

                // SAME version (no tile event) — ONLY CoverageFading flips; Departing stays false (untouched).
                BuildPlan(store, plan, version: 0, fadeTileKey: key);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "a mask-only change at a fixed version must stay a memo HIT");

                BuildPlan(store, refPlan, version: 0, fadeTileKey: key);
                refHarness.Lps.GatherIntoMirror(refPlan);

                var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                    "the per-frame masks must be tracked on a HELD mirror, not frozen from the first rebuild");
                Assert.IsTrue(got.SymbolCoverageFading[0], "CoverageFading must reflect the flip even on a memo-hit frame");
                Assert.IsFalse(got.SymbolDeparting[0],
                    "Departing must stay UNCHANGED — a mask cross-wire (e.g. CoverageFading's source copied into both destinations) would wrongly flip this too");
            }
            finally { plan.Dispose(); refPlan.Dispose(); store.Clear(); }
        }

        // ═══ Dropped is invisible through CopyMirrorInto (it hard-skips in GatherSymbolPoints, not the
        //          mirror-comparison surface) — assert it BEHAVIOURALLY, on a memo-HIT frame, via a real Tick +
        //          WorldMeshReadback comparison against a reference that never collected the dropped tile at all
        //          (mirrors SymbolGatherPlanDropMaskTests' pattern), with the masked side's Drop flip held at the
        //          SAME version (a memo hit) instead of a bumped one. ═══

        // Full vertex + opacity byte comparison of two world-slot meshes — mirrors
        // SymbolGatherPlanDropMaskTests.FirstMeshDifference. Returns the first difference, or null if byte-identical.
        private static string FirstMeshDifference(Mesh a, Mesh b)
        {
            WorldMeshReadback.Read(a, out WorldBillboardVertex[] va, out float[] oa);
            WorldMeshReadback.Read(b, out WorldBillboardVertex[] vb, out float[] ob);
            if (va.Length != vb.Length) return $"vertex count {va.Length} vs {vb.Length}";
            for (int i = 0; i < va.Length; i++)
                if (!va[i].Equals(vb[i])) return $"vertex[{i}] differs";
            if (oa.Length != ob.Length) return $"opacity count {oa.Length} vs {ob.Length}";
            for (int i = 0; i < oa.Length; i++)
                if (oa[i] != ob[i]) return $"opacity[{i}] {oa[i]} vs {ob[i]}";
            return null;
        }

        [Test]
        public void Memo_DropFlip_TrackedWhilePoolsHeld()
        {
            var keepTile = new TileId { Z = 12, X = 2500, Y = 1500 };
            var dropTile = new TileId { Z = 12, X = 2501, Y = 1500 };
            long keepKey = Tk(keepTile), dropKey = Tk(dropTile);
            var dropOffset = new double3(0, 0, 1600); // clears collision with KEEP — mirrors DropMaskTests' DropOffset

            using var hMasked = new TickHarness();
            using var hRef = new TickHarness();
            var storeMasked = new SymbolTileStore(cacheCap: 16);
            var storeRef = new SymbolTileStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileStore store in new[] { storeMasked, storeRef })
                {
                    SeedTile(store, keepTile, PointSymbol(hMasked.Origin, "keep", 1, keepKey, 0.1f));
                    SeedTile(store, dropTile, PointSymbol(hMasked.Origin + dropOffset, "drop", 2, dropKey, 0.2f));
                }

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                try
                {
                    // Frame 1: both tiles Keep on both sides — establishes a live fade for the drop tile too.
                    // The collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick —
                    // duplicate (same plan+version, so the second Tick is a memo HIT, not a second rebuild) so
                    // this frame's ticks actually SHOW both tiles before frame 2 masks one of them off. Without
                    // this, frame 1 is a virgin system's first Tick and shows NOTHING — the drop tile's slot
                    // would never be built, so :510's "masked: the Dropped slot must be HIDDEN" would pass
                    // vacuously (never shown ⇒ trivially not visible), proving nothing about the Drop mask.
                    BuildPlan(storeMasked, planMasked, version: 0);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildPlan(storeRef, planRef, version: 0);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.AreEqual(1, hMasked.System.MirrorRebuildCount,
                        "sanity: frame 1 is a heavy rebuild — the duplicate Tick is a memo HIT (same plan+version), not a second rebuild");

                    // Frame 2: MASKED flags the drop tile Dropped at the SAME version (a memo-HIT frame — the point
                    // of this test); REFERENCE excludes the drop tile physically (a different plan/store shape,
                    // separate rebuild — its own memoization is irrelevant here).
                    BuildPlan(storeMasked, planMasked, version: 0, dropTileKey: dropKey);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    Assert.AreEqual(1, hMasked.System.MirrorRebuildCount,
                        "the Drop flip at a fixed version must be a memo HIT — this is what makes the assertions below meaningful");

                    var refBlockId = new List<int>();
                    var refLocalIndex = new List<int>();
                    var refIsDeparting = new List<byte>();
                    var refDecisions = new List<byte>();
                    var allBlockId = new List<int>();
                    var allLocalIndex = new List<int>();
                    var allIsDeparting = new List<byte>();
                    storeRef.CollectInto(allBlockId, allLocalIndex, allIsDeparting, quantizeMeters: 1.0, out _);
                    for (int i = 0; i < allBlockId.Count; i++)
                    {
                        if (storeRef.OrderedBlocks[allBlockId[i]].TileKey == dropKey) continue;
                        refBlockId.Add(allBlockId[i]); refLocalIndex.Add(allLocalIndex[i]);
                        refIsDeparting.Add(allIsDeparting[i]); refDecisions.Add(SymbolTileCoverageFilter.Keep);
                    }
                    planRef.Build(refBlockId, refLocalIndex, refIsDeparting, refDecisions, storeRef.OrderedBlocks, winnerSetVersion: 1);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);

                    Assert.AreEqual(hRef.System.LastQuadCount, hMasked.System.LastQuadCount,
                        "masked-Drop's emitted quad count (on a memo-HIT frame) must equal the reference's");
                    Assert.AreEqual(1, hRef.System.LastQuadCount, "sanity: only the KEEP point ever draws");

                    Assert.IsTrue(hMasked.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshMasked));
                    Assert.IsTrue(hRef.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshRef));
                    Assert.IsNull(FirstMeshDifference(keepMeshRef, keepMeshMasked),
                        "the surviving KEEP point's full vertex+opacity content must be byte-identical between a memo-hit masked Drop and the reference");

                    Assert.IsTrue(hMasked.System.IsWorldSlotVisible(keepKey, 0, SymbolKind.Text), "masked: KEEP must be VISIBLE");
                    Assert.IsFalse(hMasked.System.IsWorldSlotVisible(dropKey, 0, SymbolKind.Text),
                        "masked: the Dropped slot must be HIDDEN even though its mirror pools came from a memo hit");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }

        // ═══ The memo-HIT path (three mask memcpys + a subtraction) allocates ZERO managed garbage — pairs
        //     with SymbolGatherParityTests.GatherIntoMirror_Warm_AllocatesNoGCMemory (the HEAVY-path guard,
        //     which forces a version bump before its measured call). ═══

        [Test]
        public void GatherIntoMirror_MemoHit_AllocatesNoGCMemory()
        {
            var tile = new TileId { Z = 6, X = 50, Y = 50 };
            long key = Tk(tile);
            var store = new SymbolTileStore(cacheCap: 8);
            SeedTile(store, tile, PointSymbol(new double3(100, 0, 200), "a", 1, key, 0.1f));

            using var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                BuildPlan(store, plan, version: 0);
                harness.Lps.GatherIntoMirror(plan); // heavy warm-up — first-touch native growth happens here
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: warm-up is a heavy rebuild");
                harness.Lps.GatherIntoMirror(plan); // extra warm-up memo hit (same version — no rebuild expected)
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the measured call below must be a memo hit");

                Assert.That(() => { harness.Lps.GatherIntoMirror(plan); }, Is.Not.AllocatingGCMemory(),
                    "a memo-hit GatherIntoMirror must allocate ZERO managed garbage — three NativeArray memcpys + a subtraction");
            }
            finally { plan.Dispose(); store.Clear(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolGatherParityTests — the order-parity tooth
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ORDER-PARITY TOOTH. The production per-frame
    /// path replaced a managed SoA build with a NATIVE GATHER (<see cref="SymbolPlacementSystem.GatherIntoMirror"/>)
    /// that compacts each winner's pre-baked <see cref="SymbolTileBlock"/> slice into the placement job's
    /// native mirror. This test proves the gather is BYTE-IDENTICAL to an INDEPENDENT restatement over the SAME
    /// winner plan — the exact invariant the GPU snapshot suite depends on, localized to a field-by-field
    /// comparison so a spine bug (winner order, the <c>(blockId, localIndex)</c> mapping, or a source-offset
    /// remap) surfaces HERE, not as a vague snapshot flip.
    ///
    /// <para><b>The oracle (<see cref="BuildExpectedBatch"/>) is independent of the gather, not of the bake.</b>
    /// It walks the SAME winner plan the gather reads (<c>blockId</c>/<c>localIndex</c>/<c>isDeparting</c>/
    /// <c>decisions</c>) and, for each winner, reads its fields straight off the committed
    /// <see cref="SymbolTileBlock"/>'s own columns (<c>block.Points[block.Detail[localIndex]]</c>, quad/
    /// glyph/anchor/path spans, <c>block.RepAnchor</c>) — a hand-written managed loop, never calling
    /// <see cref="SymbolPlacementSystem.GatherIntoMirror"/> or the Burst <c>SymbolGatherJob</c> it drives. Both
    /// the oracle and the production gather consume the SAME immutable bake output (itself pinned separately by
    /// <c>SymbolTileBlockBakerTests</c>), so a match here proves the gather's SELECTION/ASSEMBLY logic
    /// (which block, which raw slot, which pool offset) is correct — exactly what this tooth exists to pin —
    /// without re-deriving the per-symbol field math the baker already owns (reusing <c>block.Points</c>/
    /// <c>block.Curveds</c> verbatim is the same "shared math, checked assembly" split
    /// <c>SymbolTileBlockBaker</c>'s own doc describes).</para>
    ///
    /// <para>Fixture: a MULTI-TILE set with BOTH curved and point symbols, a tile the coverage filter FADES, a tile
    /// it DROPS (so the active-range compaction genuinely moves elements), and a DEPARTING tile (so the
    /// departing-tail shift runs).</para>
    /// </summary>
    [TestFixture]
    public class SymbolGatherParityTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        // Identity projection matrices: every corner projects (clip.w == 1 > 0) so a HUGE minCoverage forces every
        // tile below threshold deterministically — Keep/Fade/Drop is then driven purely by coverageAbovePrev, no
        // camera framing needed (the filter's classification, not its exact coverage number, is what we exercise).
        private static readonly float4x4 IdViewProj = float4x4.identity;
        private static readonly float3x3 IdRebase = float3x3.identity;
        private static readonly double2 Viewport = new double2(100, 100);
        // Dominates any projected tile-quad coverage (identity projection of z5 Mercator corners tops out ~1e14),
        // so every tile is deterministically "below threshold" → Keep/Fade/Drop is driven purely by coverageAbovePrev.
        private const double HugeMinCoverage = 1e30;

        private static readonly WebMercatorProjection P = new WebMercatorProjection();

        private static SymbolQuad OneQuadCell(float u) => new SymbolQuad
        {
            TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
            UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
        };

        private static List<SymbolQuad> OneQuadList(float u) => new List<SymbolQuad> { OneQuadCell(u) };

        private static void AddPointSymbol(SymbolTileBuffer buffer, double3 anchor, string text, int feature,
            long tileKey, float u)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuadList(u), float2.zero, new float2(18f, 18f),
                text: text, textSizePx: 20f, paddingPx: 2f, sortKey: 0f,
                featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);

        private static void AddCurvedSymbol(SymbolTileBuffer buffer, double3 anchor, string text, int feature,
            long tileKey)
            => TestSymbolTileBuffer.AddCurved(buffer,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 3f, Cell = OneQuadCell(0.3f) },
                    new CurvedGlyph { ArcCenter = 9f, Cell = OneQuadCell(0.5f) },
                },
                anchors: new[] { new LineAnchor(0, 0.5f) },
                path: new[] { anchor, new double3(10, 0, 0) + anchor, new double3(20, 0, 0) + anchor },
                anchorRender: anchor, text: text, textSizePx: 20f, paddingPx: 2f, sortKey: 1f,
                featureIndex: feature, tileKey: tileKey, maxAngleDeg: 45f, keepUpright: true, paint: SymbolPaint.Default);

        // An EMPTY quad list — a point symbol that contributes zero quads,
        // exercising SymbolGatherJob's `quadCount > 0` guard (a zero-length source array can yield a null Ptr).
        private static List<SymbolQuad> ZeroQuadList() => new List<SymbolQuad>();

        // A parameterized curved-symbol append so a test can hand it an EMPTY
        // glyph list or an EMPTY anchor array — exercising the `glyphCount > 0` / `anchorCount > 0` guards and,
        // for the empty-anchor case, the UNGUARDED fade copy (fadeCount = anchorCount + 1 = 1, no guard).
        private static void AddCurvedSymbolCustom(SymbolTileBuffer buffer, double3 anchor, string text, int feature,
            long tileKey, List<CurvedGlyph> glyphs, LineAnchor[] anchors)
            => TestSymbolTileBuffer.AddCurved(buffer, glyphs, anchors,
                path: new[] { anchor, new double3(10, 0, 0) + anchor, new double3(20, 0, 0) + anchor },
                anchorRender: anchor, text: text, textSizePx: 20f, paddingPx: 2f, sortKey: 1f,
                featureIndex: feature, tileKey: tileKey, maxAngleDeg: 45f, keepUpright: true, paint: SymbolPaint.Default);

        private static SymbolTileStore.Key Key(TileId t) => new SymbolTileStore.Key("s", t);

        // Bake + commit one tile's symbols as a native block (the SAME projection/tileOrigin the pipeline resolves).
        private static void SeedTile(SymbolTileStore store, TileId tile, Action<SymbolTileBuffer> build)
        {
            int gen = store.BeginBuild(Key(tile));
            var buffer = new SymbolTileBuffer();
            build(buffer);
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, block), "sanity: block committed");
        }

        private static long Tk(TileId t) => SymbolTileKey.Pack(t);

        // Locates the winner entry for tileKey's raw record at wantLocalIndex — used by the RED-verify (a) test
        // to perturb a KNOWN-real winner without assuming CollectInto's scan order (block/collection order is an
        // implementation detail this test must not bake in).
        private static int FindWinner(List<int> blockId, List<int> localIndex,
            IReadOnlyList<SymbolTileBlock> orderedBlocks, long tileKey, int wantLocalIndex)
        {
            for (int i = 0; i < blockId.Count; i++)
                if (orderedBlocks[blockId[i]].TileKey == tileKey && localIndex[i] == wantLocalIndex) return i;
            return -1;
        }

        // Classify + compact the RAW CollectInto winner-plan arrays: removes every Drop-classified record,
        // mirroring the RETIRED SymbolTileCoverageFilter.FilterActive's physical-compaction behaviour (this test
        // targets winner-ORDER parity — "compaction genuinely moves elements" — not the resident-Drop masking,
        // which SymbolGatherPlan's own doc describes as a separate, later concern). Driven by the BLOCK-based
        // SymbolTileCoverageFilter.ClassifyActive so no per-symbol managed carrier list is ever materialized.
        private static void ClassifyAndCompact(IReadOnlyList<SymbolTileBlock> orderedBlocks,
            List<int> blockId, List<int> localIndex, List<byte> isDeparting, HashSet<long> coverageAbovePrev,
            out List<byte> rawDecisions,
            out List<int> compactBlockId, out List<int> compactLocalIndex, out List<byte> compactIsDeparting,
            out List<byte> compactDecisions, out int culled)
        {
            var blockTileKeys = new List<long>(orderedBlocks.Count);
            for (int b = 0; b < orderedBlocks.Count; b++) blockTileKeys.Add(orderedBlocks[b].TileKey);

            rawDecisions = new List<byte>();
            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, P,
                double3.zero, IdViewProj, Viewport, IdRebase, HugeMinCoverage,
                coverageAbovePrev, new HashSet<long>(), new Dictionary<long, double>(), new HashSet<long>(),
                now: 20.0, graceSeconds: 1000.0, new Dictionary<long, byte>(), new List<byte>(),
                rawDecisions, out culled);

            compactBlockId = new List<int>(); compactLocalIndex = new List<int>();
            compactIsDeparting = new List<byte>(); compactDecisions = new List<byte>();
            for (int i = 0; i < blockId.Count; i++)
            {
                if (rawDecisions[i] == SymbolTileCoverageFilter.Drop) continue;
                compactBlockId.Add(blockId[i]); compactLocalIndex.Add(localIndex[i]);
                compactIsDeparting.Add(isDeparting[i]); compactDecisions.Add(rawDecisions[i]);
            }
        }

        // THE INDEPENDENT ORACLE — see the type doc for why this does not call GatherIntoMirror. A line-for-line
        // MANAGED restatement of the same "select this winner's slice from its block, flatten into one SoA" job
        // SymbolGatherJob performs in Burst — written independently here rather than shared, so the two can
        // disagree if either one has a selection/offset bug.
        private static void BuildExpectedBatch(SymbolBatch expected,
            List<int> blockId, List<int> localIndex, List<byte> isDeparting, List<byte> decisions,
            IReadOnlyList<SymbolTileBlock> orderedBlocks)
        {
            expected.Reset();
            int n = blockId.Count;
            for (int i = 0; i < n; i++)
            {
                SymbolTileBlock block = orderedBlocks[blockId[i]];
                int raw = localIndex[i];
                SymbolPlacementKind kind = block.Kinds[raw];
                int detail = block.Detail[raw];
                bool departing = isDeparting[i] != 0;
                bool coverageFading = decisions[i] == SymbolTileCoverageFilter.Fade;

                if (kind == SymbolPlacementKind.Point)
                {
                    int quadStart = block.PointQuadStart[detail], quadCount = block.PointQuadCount[detail];
                    int expectedQuadStart = expected.QuadCount;
                    for (int q = 0; q < quadCount; q++) expected.AddQuad(block.Quads[quadStart + q]);
                    int expectedDetail = expected.AddPoint(block.Points[detail], expectedQuadStart, quadCount);

                    int worldStartSrc = block.WorldStart[raw], worldCount = block.WorldCount[raw];
                    int expectedWorldStart = expected.WorldPointCount;
                    for (int v = 0; v < worldCount; v++)
                    {
                        expected.AddWorldPoint(block.WorldPoints[worldStartSrc + v]);
                        expected.AddWorldUp(block.WorldUps[worldStartSrc + v]);
                    }
                    expected.AddSymbol(SymbolPlacementKind.Point, expectedDetail, expectedWorldStart, worldCount,
                        block.RepAnchor[raw], departing, coverageFading);
                }
                else
                {
                    int glyphStart = block.CurvedGlyphStart[detail], glyphCount = block.CurvedGlyphCount[detail];
                    int expectedGlyphStart = expected.GlyphCount;
                    for (int g = 0; g < glyphCount; g++) expected.AddGlyph(block.Glyphs[glyphStart + g]);

                    int anchorStart = block.CurvedAnchorStart[detail], anchorCount = block.CurvedAnchorCount[detail];
                    int expectedAnchorStart = expected.AnchorCount;
                    for (int a = 0; a < anchorCount; a++) expected.AddAnchor(block.Anchors[anchorStart + a]);

                    int fadeStart = block.CurvedAnchorFadeStart[detail];
                    int expectedFadeStart = expected.AnchorFadeCount;
                    for (int a = 0; a <= anchorCount; a++) expected.AddAnchorFadeId(block.AnchorFadeIds[fadeStart + a]);

                    int expectedDetail = expected.AddCurved(block.Curveds[detail], expectedGlyphStart, glyphCount,
                        expectedAnchorStart, anchorCount, expectedFadeStart);

                    int worldStartSrc = block.WorldStart[raw], worldCount = block.WorldCount[raw];
                    int expectedWorldStart = expected.WorldPointCount;
                    for (int v = 0; v < worldCount; v++)
                    {
                        expected.AddWorldPoint(block.WorldPoints[worldStartSrc + v]);
                        expected.AddWorldUp(block.WorldUps[worldStartSrc + v]);
                    }
                    expected.AddSymbol(SymbolPlacementKind.Curved, expectedDetail, expectedWorldStart, worldCount,
                        block.RepAnchor[raw], departing, coverageFading);
                }
            }
        }

        // Build the whole production winner-plan pipeline into `plan`, and the reference oracle via
        // BuildExpectedBatch over the SAME (compacted) winner plan. Returns the gathered mirror (materialized as
        // a batch) via `gathered`.
        private void RunPipeline(SymbolTileStore store, SymbolPlacementSystem lps, SymbolGatherPlan plan,
            HashSet<long> coverageAbovePrev, bool skipPermute, int perturbWinner, int perturbLocalIndex,
            out SymbolBatch oracle, out SymbolBatch gathered, out int culled)
        {
            var planBlockId = new List<int>();
            var planLocalIndex = new List<int>();
            var planIsDeparting = new List<byte>();
            store.CollectInto(planBlockId, planLocalIndex, planIsDeparting, quantizeMeters: 1.0, out _);

            ClassifyAndCompact(store.OrderedBlocks, planBlockId, planLocalIndex, planIsDeparting, coverageAbovePrev,
                out List<byte> rawDecisions,
                out List<int> compactBlockId, out List<int> compactLocalIndex, out List<byte> compactIsDeparting,
                out List<byte> compactDecisions, out culled);

            // Snapshot BEFORE any perturbation/skip below — the oracle must read the UNPERTURBED winner set so
            // RED-verify (a)/(b) can diverge it from what the (perturbed) plan feeds the gather.
            var oracleBlockId = new List<int>(compactBlockId);
            var oracleLocalIndex = new List<int>(compactLocalIndex);
            var oracleIsDeparting = new List<byte>(compactIsDeparting);
            var oracleDecisions = new List<byte>(compactDecisions);

            // RED-verify (b): skip compaction entirely for the plan feed — the RAW, uncompacted arrays (which
            // still carry the Dropped winner) desync from the oracle's compacted winner set — the class of
            // bug the permute step exists to prevent.
            List<int> feedBlockId = skipPermute ? planBlockId : compactBlockId;
            List<int> feedLocalIndex = skipPermute ? planLocalIndex : compactLocalIndex;
            List<byte> feedIsDeparting = skipPermute ? planIsDeparting : compactIsDeparting;
            List<byte> feedDecisions = skipPermute ? rawDecisions : compactDecisions;

            // RED-verify (a): perturb ONE winner's localIndex in the PLAN feed only — the oracle's snapshot above
            // was already taken, so it still reads the correct record.
            if (perturbWinner >= 0) feedLocalIndex[perturbWinner] = perturbLocalIndex;

            plan.Build(feedBlockId, feedLocalIndex, feedIsDeparting, feedDecisions, store.OrderedBlocks, winnerSetVersion: 0);
            lps.GatherIntoMirror(plan);
            gathered = new SymbolBatch();
            lps.CopyMirrorInto(gathered);

            oracle = new SymbolBatch();
            BuildExpectedBatch(oracle, oracleBlockId, oracleLocalIndex, oracleIsDeparting, oracleDecisions, store.OrderedBlocks);
        }

        // A 4-tile fixture: A (point + curved, faded), B (point, faded), C (point, DROPPED), D (point, DEPARTING).
        private SymbolTileStore Seed(out HashSet<long> coverageAbovePrev,
            out TileId a, out TileId b, out TileId c, out TileId d)
        {
            // Locals (not the out params) so the seeding lambdas can capture them — C# forbids capturing an
            // out/ref parameter inside a lambda (CS1628). The out params are assigned from these below.
            TileId ta = new TileId { Z = 5, X = 16, Y = 16 };
            TileId tb = new TileId { Z = 5, X = 17, Y = 16 };
            TileId tc = new TileId { Z = 5, X = 18, Y = 16 };
            TileId td = new TileId { Z = 5, X = 19, Y = 16 };
            a = ta; b = tb; c = tc; d = td;

            var store = new SymbolTileStore(cacheCap: 16);
            SeedTile(store, ta, buffer =>
            {
                AddPointSymbol(buffer, new double3(100, 0, 200), "a", 1, Tk(ta), 0.1f);
                AddCurvedSymbol(buffer, new double3(120, 0, 220), "ac", 2, Tk(ta));
            });
            SeedTile(store, tb, buffer => AddPointSymbol(buffer, new double3(300, 0, 400), "b", 3, Tk(tb), 0.15f));
            SeedTile(store, tc, buffer => AddPointSymbol(buffer, new double3(500, 0, 600), "c", 4, Tk(tc), 0.2f));
            SeedTile(store, td, buffer => AddPointSymbol(buffer, new double3(700, 0, 800), "d", 5, Tk(td), 0.25f));

            // D leaves cover with a grace window → cached + departing (collected after the active split).
            store.ReconcileActiveSet(
                new List<SymbolTileStore.Key> { Key(ta), Key(tb), Key(tc) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);

            // A, B were above threshold last frame → they FADE; C never was → it DROPS.
            coverageAbovePrev = new HashSet<long> { Tk(ta), Tk(tb) };
            return store;
        }

        // Owns the LPS + the throwaway Unity resources the fixture creates. SymbolPlacementSystem.Dispose frees only
        // its CLONED material, so this disposes the base material + render texture (+ camera GO) explicitly — no leak.
        private sealed class LpsHarness : IDisposable
        {
            public readonly SymbolPlacementSystem Lps;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public LpsHarness()
            {
                _camGo = new GameObject("GatherParity_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(64, 64, 0);
                uCam.targetTexture = _rt;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 0, Longitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                // GatherIntoMirror/CopyMirrorInto touch neither the camera nor the material; a material silences the ctor warn.
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Lps = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Lps.Dispose();                                    // frees the cloned material + native scratch
                UnityEngine.Object.DestroyImmediate(_camGo);      // destroy the Camera FIRST so the RT is no longer
                UnityEngine.Object.DestroyImmediate(_rt);         // its targetTexture (else Unity logs an Error → test fail)
                UnityEngine.Object.DestroyImmediate(_baseMaterial); // the base clone source (Lps.Dispose never sees it)
            }
        }

        [Test]
        public void Gather_MatchesBuildOracle_FieldByField()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: -1, perturbLocalIndex: -1,
                    out SymbolBatch oracle, out SymbolBatch gathered, out int culled);

                Assert.AreEqual(1, culled, "precondition: tile C is Dropped (so the compaction moves elements)");
                Assert.AreEqual(4, oracle.Count, "precondition: A.curved + A.point + B.point (active) + D.point (departing)");
                Assert.AreEqual(1, oracle.CurvedCount, "precondition: exactly the one curved label (A)");
                Assert.AreEqual(3, oracle.PointCount, "precondition: A.point + B.point + D.point");

                string diff = SymbolBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the independent oracle — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // RED-verify (a): perturb ONE winner's localIndex — the gather reads the wrong record; parity MUST break.
        [Test]
        public void Gather_DetectsPerturbedLocalIndex()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out TileId a, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Locate the ACTUAL winner for tile A's point record (raw localIndex 0) — never assume
                // CollectInto's scan order — then perturb it to read A's OTHER raw record (the curved, index 1).
                var planBlockId = new List<int>();
                var planLocalIndex = new List<int>();
                var planIsDeparting = new List<byte>();
                store.CollectInto(planBlockId, planLocalIndex, planIsDeparting, quantizeMeters: 1.0, out _);
                ClassifyAndCompact(store.OrderedBlocks, planBlockId, planLocalIndex, planIsDeparting, above,
                    out _, out List<int> compactBlockId, out List<int> compactLocalIndex, out _, out _, out _);
                int perturbWinner = FindWinner(compactBlockId, compactLocalIndex, store.OrderedBlocks, Tk(a), wantLocalIndex: 0);
                Assert.AreNotEqual(-1, perturbWinner, "sanity: tile A's point record (raw localIndex 0) is a winner");

                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: perturbWinner, perturbLocalIndex: 1,
                    out SymbolBatch oracle, out SymbolBatch gathered, out _);
                Assert.IsNotNull(SymbolBatchDiff.FirstDifference(oracle, gathered),
                    "a perturbed winner localIndex must diverge from the oracle — the parity comparison has teeth");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // RED-verify (b): skip compaction for the plan feed — the plan arrays stay full-length (still carrying the
        // Dropped winner) while the oracle reads the compacted winner set; the gather must diverge from the oracle.
        [Test]
        public void Gather_DetectsSkippedFilterPermute()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                RunPipeline(store, harness.Lps, plan, above, skipPermute: true, perturbWinner: -1, perturbLocalIndex: -1,
                    out SymbolBatch oracle, out SymbolBatch gathered, out _);
                Assert.IsNotNull(SymbolBatchDiff.FirstDifference(oracle, gathered),
                    "an un-compacted plan desyncs from the filtered winner set — the gather must diverge from the oracle");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Gather path: once warm, a second HEAVY GatherIntoMirror allocates ZERO managed garbage — the
        // mirror native lists + plan are reused (the point of the build-time SoA bake). Mirrors
        // the SymbolSubsystemPumpTests.CurrentBatch_Warm_… idiom, one level down on the gather itself.
        // A same-version GatherIntoMirror is a memo HIT, not the heavy path this test's message
        // claims — bump WinnerSetVersion immediately before the measured call to force a real rebuild (the honest
        // "force a rebuild" knob: the field is internal, so the test can assign it). The memo-HIT alloc guard is
        // SymbolGatherMemoTests.GatherIntoMirror_MemoHit_AllocatesNoGCMemory, kept separate.
        [Test]
        public void GatherIntoMirror_Warm_AllocatesNoGCMemory()
        {
            SymbolTileStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Build the plan ONCE (collect + filter + plan.Build may allocate first-touch — outside the measure),
                // then materialize the gather so its native lists reach steady capacity.
                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: -1, perturbLocalIndex: -1,
                    out _, out _, out _);
                harness.Lps.GatherIntoMirror(plan); // extra warm-up (first-touch native growth already done above)

                plan.WinnerSetVersion++; // force the measured call to take the heavy (rebuild) path, not a memo hit
                // The version bump above is a PRECONDITION this test's own claim ("measures the
                // heavy rebuild path") rests on, and only the MirrorRebuildCount delta below actually proves it
                // engaged, so this test can never silently degrade into measuring a memo hit.
                int rebuildsBefore = harness.Lps.MirrorRebuildCount;
                Assert.That(() => { harness.Lps.GatherIntoMirror(plan); }, Is.Not.AllocatingGCMemory(),
                    "a warm GatherIntoMirror must allocate ZERO managed garbage — native lists + plan are reused");
                Assert.AreEqual(rebuildsBefore + 1, harness.Lps.MirrorRebuildCount,
                    "precondition: the measured call must take the heavy rebuild path (WinnerSetVersion bump), not a memo hit");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Widen the parity fixture: a Burst-only running-offset bug only manifests when MULTIPLE winners
        // share a block, or a ZERO-SIZED slice sits between two non-empty ones — the original 4-tile fixture (one
        // winner per block) cannot catch either. Hand-built (no store/coverage filter — full control over winner
        // order and which raw slot is null), this fixture exercises all four gaps in one shot:
        //  - block A: >= 3 winners, with a NULL slot BETWEEN the first two real ones (the null-slot invariant —
        //    localIndex 1 is never itself a winner, but it must not perturb localIndex 2's block-pool slot);
        //  - a point symbol with ZERO quads (empty quad list) — the `quadCount > 0` guard;
        //  - a curved symbol with ZERO glyphs, and one with ZERO anchors — the `glyphCount > 0` /
        //    `anchorCount > 0` guards AND the unguarded fade copy (fadeCount = anchorCount + 1 = 1, no guard);
        //  - >= 2 blocks interleaved in winner order (A, B, A, B, A) so the per-block switch and the running
        //    mirror cursors are exercised together, not block-at-a-time.
        [Test]
        public void Gather_MatchesBuildOracle_FieldByField_MultiWinnerInterleavedBlocks()
        {
            var a = new TileId { Z = 5, X = 20, Y = 16 };
            var b = new TileId { Z = 5, X = 21, Y = 16 };
            double3 originA = TileRenderOrigin.Project(a, P);
            double3 originB = TileRenderOrigin.Project(b, P);
            long tkA = Tk(a), tkB = Tk(b);

            // Block A raw slots: [0] point ZERO quads — [1] point normal #1, 1 quad — [2] point normal #2,
            // 1 DIFFERENT quad — [3] curved normal #1, 2 glyphs + 1 anchor — [4] curved ZERO glyphs —
            // [5] curved normal #2, 2 DIFFERENT glyphs + 1 DIFFERENT anchor. Two quad-bearing points AND two
            // glyph/anchor-bearing curveds in the SAME block is
            // what makes the SECOND of each pair's source offset within the block's OWN pool genuinely non-zero
            // (each #1 occupies the pool's slot 0; the zero-content records before/between contribute nothing to
            // the running offset) — review found the original point-only widening left the curved arm's three
            // source-offset remaps (glyph/anchor/fade start) untestable, since every fixture in the repo has at
            // most one curved symbol per block. #1 and #2 carry DIFFERENT glyph/anchor content so a swapped
            // source offset reads detectably wrong data, not coincidentally-correct data.
            var bufferA = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(bufferA, new double3(10, 0, 10), ZeroQuadList(), float2.zero, float2.zero,
                text: "a0", textSizePx: 20f, paddingPx: 2f, sortKey: 0f, featureIndex: 10, tileKey: tkA, paint: SymbolPaint.Default);
            AddPointSymbol(bufferA, new double3(30, 0, 30), "a2", 12, tkA, 0.4f);
            AddPointSymbol(bufferA, new double3(35, 0, 35), "a3", 13, tkA, 0.45f);
            AddCurvedSymbolCustom(bufferA, new double3(40, 0, 40), "a4", 16, tkA,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 1f, Cell = OneQuadCell(0.1f) },
                    new CurvedGlyph { ArcCenter = 2f, Cell = OneQuadCell(0.15f) },
                },
                anchors: new[] { new LineAnchor(0, 0.5f) });
            AddCurvedSymbolCustom(bufferA, new double3(41, 0, 41), "a5", 17, tkA,
                glyphs: new List<CurvedGlyph>(), anchors: new[] { new LineAnchor(0, 0.55f) });
            AddCurvedSymbolCustom(bufferA, new double3(42, 0, 42), "a6", 18, tkA,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 5f, Cell = OneQuadCell(0.8f) },
                    new CurvedGlyph { ArcCenter = 6f, Cell = OneQuadCell(0.85f) },
                },
                anchors: new[] { new LineAnchor(0, 0.75f) });

            // Block B raw slots: [0] curved ZERO anchors (real) — [1] point normal (real).
            var bufferB = new SymbolTileBuffer();
            AddCurvedSymbolCustom(bufferB, new double3(50, 0, 50), "b0", 14, tkB,
                glyphs: new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 3f, Cell = OneQuadCell(0.6f) } },
                anchors: Array.Empty<LineAnchor>());
            AddPointSymbol(bufferB, new double3(60, 0, 60), "b1", 15, tkB, 0.7f);

            SymbolTileBlock blockA = SymbolTileBlockBaker.Bake(bufferA, slotCount: 1, originA);
            SymbolTileBlock blockB = SymbolTileBlockBaker.Bake(bufferB, slotCount: 1, originB);

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Winner order A(li0), B(li0), A(li1), B(li1), A(li2), A(li3), A(li4), A(li5) — interleaved
                // A,B,A,B,A,A,A,A over block A's six dense slots.
                var blockId = new List<int> { 0, 1, 0, 1, 0, 0, 0, 0 };
                var localIndex = new List<int> { 0, 0, 1, 1, 2, 3, 4, 5 };
                var isDeparting = new List<byte> { 0, 0, 0, 0, 0, 0, 0, 0 };
                var decisions = new List<byte>
                {
                    SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep,
                    SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep,
                    SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep,
                };
                var orderedBlocks = new List<SymbolTileBlock> { blockA, blockB };

                plan.Build(blockId, localIndex, isDeparting, decisions, orderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan);
                var gathered = new SymbolBatch();
                harness.Lps.CopyMirrorInto(gathered);

                var oracle = new SymbolBatch();
                BuildExpectedBatch(oracle, blockId, localIndex, isDeparting, decisions, orderedBlocks);

                Assert.AreEqual(8, oracle.Count, "precondition: 8 winners collected");
                Assert.AreEqual(4, oracle.PointCount, "precondition: aPointZeroQuads + aPointNormal1 + bPointNormal + aPointNormal2");
                Assert.AreEqual(4, oracle.CurvedCount, "precondition: bCurvedZeroAnchors + aCurvedNormal1 + aCurvedZeroGlyphs + aCurvedNormal2");
                Assert.AreEqual(0, oracle.PointQuadCount[0], "precondition: the first-processed point (aPointZeroQuads) has zero quads");
                Assert.AreEqual(0, oracle.CurvedAnchorCount[0], "precondition: the first-processed curved (bCurvedZeroAnchors) has zero anchors");
                Assert.AreEqual(0, oracle.CurvedGlyphCount[2], "precondition: the third-processed curved (aCurvedZeroGlyphs) has zero glyphs");
                // Block-level preconditions (not oracle-derived proxies): pin the
                // REAL property such defects need to diverge on, not a coincidental stand-in.
                int aPointNormal2Detail = blockA.Detail[2];
                Assert.AreNotEqual(0, blockA.PointQuadStart[aPointNormal2Detail],
                    "precondition: aPointNormal2's source quad offset within block A's own pool is genuinely non-zero");
                int aCurvedNormal2Detail = blockA.Detail[5];
                Assert.AreNotEqual(0, blockA.CurvedGlyphStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source glyph offset within block A's own pool is genuinely non-zero");
                Assert.AreNotEqual(0, blockA.CurvedAnchorStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source anchor offset within block A's own pool is genuinely non-zero");
                Assert.AreNotEqual(0, blockA.CurvedAnchorFadeStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source fade offset within block A's own pool is genuinely non-zero");

                string diff = SymbolBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the independent oracle on the widened fixture — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); blockA.Dispose(); blockB.Dispose(); }
        }

        // ── Bake/gather parity holds on a pair-bearing fixture — the baker resolves PairRole
        //    over the TILE list (SymbolTileBlockBaker.Fill); this test pins that the gather's block/detail
        //    SELECTION carries that resolved role through untouched. ──
        [Test]
        public void Gather_MatchesBuildOracle_FieldByField_CentredPair()
        {
            var a = new TileId { Z = 5, X = 22, Y = 16 };
            double3 originA = TileRenderOrigin.Project(a, P);
            long tkA = Tk(a);

            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                },
            };
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, new double3(70, 0, 70), iconQuads, new float2(-8f, -8f), new float2(8f, 8f),
                kind: SymbolKind.Icon, iconImage: "shield", paint: SymbolPaint.Default, textSizePx: TextQuadLayout.OneEm,
                sortKey: 0f, featureIndex: 20, tileKey: tkA, pairRole: SymbolPairRole.Owner, pairId: 20);
            TestSymbolTileBuffer.AddPoint(buffer, new double3(70, 0, 70), OneQuadList(0.9f), float2.zero, new float2(18f, 18f),
                text: "42", paint: SymbolPaint.Default, textSizePx: 20f, paddingPx: 2f, sortKey: 0f,
                featureIndex: 21, tileKey: tkA, pairRole: SymbolPairRole.Rider, pairId: 20);

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, slotCount: 1, originA);

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                var blockId = new List<int> { 0, 0 };
                var localIndex = new List<int> { 0, 1 };
                var isDeparting = new List<byte> { 0, 0 };
                var decisions = new List<byte> { SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep };
                var orderedBlocks = new List<SymbolTileBlock> { block };

                plan.Build(blockId, localIndex, isDeparting, decisions, orderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan);
                var gathered = new SymbolBatch();
                harness.Lps.CopyMirrorInto(gathered);

                var oracle = new SymbolBatch();
                BuildExpectedBatch(oracle, blockId, localIndex, isDeparting, decisions, orderedBlocks);

                Assert.AreEqual(2, oracle.PointCount, "precondition: icon + text, both point-placement");
                Assert.AreEqual(SymbolPairRole.Owner, oracle.Points[0].PairRole, "precondition: the icon resolves as Owner in the baked block");
                Assert.AreEqual(SymbolPairRole.Rider, oracle.Points[1].PairRole, "precondition: the text resolves as Rider in the baked block");

                string diff = SymbolBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the independent oracle on a pair-bearing fixture — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); block.Dispose(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolGatherPlanDropMaskTests — the tile-coverage cull's Drop decision is now a per-record mask
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The symbol-label native bake: the tile-coverage cull's Drop decision is now a per-record
    /// MASK (<see cref="SymbolGatherPlan.Dropped"/>, stamped onto the native mirror as <c>_mirrorSymbolDropped</c>)
    /// instead of a physical compaction — a Dropped winner stays RESIDENT in the plan/mirror and is hard-skipped
    /// by <c>SymbolPlacementSystem.GatherSymbolPoints</c>'s FIRST, unconditional check. This is the falsifiable
    /// proof that masking a Dropped record is bit-for-bit equivalent to it never having been collected — proven
    /// three ways, each independently RED-verifiable so a single defect can't hide behind another:
    ///
    /// <list type="bullet">
    ///   <item><see cref="MaskedDrop_PointOnly_MatchesReference_SurvivorContentIdentical"/> — a POINT-only
    ///     fixture (no curved symbol anywhere), so a point-path regression can't hide behind curved candidates.</item>
    ///   <item><see cref="MaskedDrop_CurvedOnly_MatchesReference_SurvivorContentIdentical"/> — a CURVED-only
    ///     fixture, with an explicit pre-Drop assertion that the curved record actually SURVIVED with positive
    ///     opacity (a genuinely live fade, not just "no assertion it ever placed") — this is what makes the
    ///     <c>MarkFadeOutIfAlive</c> "still alive ⇒ keep staging" path a real divergence risk if the hard-skip is
    ///     ever folded into that OR-chain instead of preceding it.</item>
    ///   <item><see cref="AllDropped_FadeStaysFrozen_ReappearOpacityMatchesReference"/> — the all-dropped regression:
    ///     a frame where EVERY resident record is Dropped must behave EXACTLY like the empty-after-
    ///     compaction mirror (placement/fade-decay block skipped entirely), so a live fade FREEZES instead of
    ///     decaying — <see cref="SymbolPlacementSystem"/>'s <c>_mirrorNonDroppedCount</c> gate, not raw <c>_mirrorCount</c>.</item>
    /// </list>
    ///
    /// Each test compares the RESIDENT-MASKED production path (a plan carrying every winner, some flagged
    /// Dropped) against a REFERENCE plan that never includes the would-be-Dropped winners in the first place —
    /// not just candidate/quad COUNTS, but the surviving record's full world-mesh vertex + opacity byte content
    /// (<see cref="WorldMeshReadback"/>), so a wrong-candidate-survived defect (same count, different content)
    /// is caught too.
    ///
    /// <para><b>SF8 (vacuous-pass guard).</b> <c>SymbolStagingMath.StagePoint</c> returns 0 candidates on a
    /// projection/viewport-margin failure — the KEEP anchor is the dead-centre point the test camera looks
    /// straight at (mirrors <c>SymbolFadeTests</c>' pattern) and the DROP anchor is offset by <c>DropOffset</c>
    /// but stays well inside the 256px viewport, both guaranteed projectable and inside margin, so a removed
    /// hard-skip is never saved from detection by an unrelated off-viewport reject.</para>
    /// </summary>
    [TestFixture]
    public class SymbolGatherPlanDropMaskTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        private static readonly WebMercatorProjection P = new WebMercatorProjection();

        // Render-space offset separating the DROP symbol from the centre-anchored KEEP symbol so the two do NOT
        // collide (a same-point pair suppresses one to opacity 0). ~1600 render metres — large enough to clear the
        // 18px glyph box across the plausible metres-per-pixel range, small enough to stay inside the 256px viewport.
        private static readonly double3 DropOffset = new double3(0, 0, 1600);

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static (List<SymbolQuad> Quads, float2 BoundsMin, float2 BoundsMax) OneQuad(float u) => (
            new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            float2.zero, new float2(18f, 18f));

        private static SymbolTileBuffer PointSymbol(double3 anchor, string text, int feature, long tileKey)
        {
            var layout = OneQuad(0.1f);
            return TestSymbolTileBuffer.Point(anchor, layout.Quads, layout.BoundsMin, layout.BoundsMax,
                text: text, textSizePx: 20f, paddingPx: 2f, featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);
        }

        private static SymbolTileBuffer CurvedSymbol(double3 anchor, string text, int feature, long tileKey) =>
            TestSymbolTileBuffer.Curved(
                glyphs: new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = OneQuad(0.3f).Quads[0] } },
                anchors: new[] { new LineAnchor(0, 0.5f) },
                // A fixed ~8m path is sub-pixel at z12 and never stages — mirror WorldCurvedAbRenderSnapshotTests'
                // altitude-relative sizing: a ~1600 render-metre span (anchor at path mid via LineAnchor 0.5, glyph
                // at ArcCenter 0 ⇒ at the anchor) places comfortably on-screen at this zoom.
                path: new[] { anchor - new double3(800, 0, 0), anchor + new double3(800, 0, 0) },
                anchorRender: anchor, placement: SymbolPlacement.LineCenter,
                text: text, textSizePx: 20f, paddingPx: 2f, sortKey: 1f,
                maxAngleDeg: 180f, keepUpright: false,
                featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);

        private static SymbolTileStore.Key Key(TileId t) => new SymbolTileStore.Key("s", t);
        private static long Tk(TileId t) => SymbolTileKey.Pack(t);

        private static void SeedTile(SymbolTileStore store, TileId tile, SymbolTileBuffer buffer)
        {
            int gen = store.BeginBuild(Key(tile));
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, block), "sanity: block committed");
        }

        // Owns the LPS + the throwaway Unity resources the fixture creates — mirrors SymbolGatherParityTests'
        // LpsHarness, plus a real look-at so StagePoint's projection/viewport-margin check genuinely passes (SF8).
        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly double3 Origin;
            public readonly GlyphAtlasTexture Atlas;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public Harness()
            {
                _camGo = new GameObject("DropMask_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(256, 256, 0); // headroom so KEEP (centre) + DROP (offset) both stay in-viewport
                uCam.targetTexture = _rt;
                var lookAt = new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt, zoom: 12.0, heading: 0.0, tilt: 0.0),
                    projection: P);
                Origin = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                System = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_camGo);
                Object.DestroyImmediate(_rt);
                Object.DestroyImmediate(_baseMaterial);
            }
        }

        // Fills `plan` from the store's real winner arrays (blockId/localIndex/isDeparting), stamping every winner
        // whose TileKey == dropTileKey Drop and everything else Keep — the RESIDENT-MASKED path (every winner
        // present in the plan, regardless of decision). dropTileKey == -1 (no tile ever packs to -1) ⇒ all Keep.
        // `version` is threaded through to SymbolGatherPlan.Build — every test below rebuilds the SAME plan
        // object across frames, so each call passes a freshly incremented per-test counter (audited by READING
        // this call site, not by which tests happen to go RED).
        private static void BuildMaskedPlan(SymbolTileStore store, SymbolGatherPlan plan, long dropTileKey, int version)
        {
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, quantizeMeters: 1.0, out _);

            var decisions = new List<byte>(blockId.Count);
            for (int i = 0; i < blockId.Count; i++)
                decisions.Add(store.OrderedBlocks[blockId[i]].TileKey == dropTileKey ? SymbolTileCoverageFilter.Drop : SymbolTileCoverageFilter.Keep);

            plan.Build(blockId, localIndex, isDeparting, decisions, store.OrderedBlocks, version);
        }

        // Fills `plan` with ONLY the winners whose TileKey != excludedTileKey — the REFERENCE path (physical
        // absence, as if the excluded tile's build never happened / was never collected). excludedTileKey == -1
        // (no tile ever packs to -1) ⇒ everything included (a plain "Build the whole store" call).
        private static void BuildReferencePlan(SymbolTileStore store, SymbolGatherPlan plan, long excludedTileKey, int version)
        {
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, quantizeMeters: 1.0, out _);

            var refBlockId = new List<int>();
            var refLocalIndex = new List<int>();
            var refIsDeparting = new List<byte>();
            var refDecisions = new List<byte>();
            for (int i = 0; i < blockId.Count; i++)
            {
                if (store.OrderedBlocks[blockId[i]].TileKey == excludedTileKey) continue;
                refBlockId.Add(blockId[i]); refLocalIndex.Add(localIndex[i]);
                refIsDeparting.Add(isDeparting[i]); refDecisions.Add(SymbolTileCoverageFilter.Keep);
            }
            plan.Build(refBlockId, refLocalIndex, refIsDeparting, refDecisions, store.OrderedBlocks, version);
        }

        private static float MaxAlpha(SymbolPlacementSystem system, long tileKey)
            => system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh) ? WorldMeshReadback.MaxOpacity(mesh) : 0f;

        // Full vertex + opacity byte comparison of two world-slot meshes — identity+content, not just a count or
        // a single max-opacity scalar. Returns the first difference, or null if byte-identical.
        private static string FirstMeshDifference(Mesh a, Mesh b)
        {
            WorldMeshReadback.Read(a, out WorldBillboardVertex[] va, out float[] oa);
            WorldMeshReadback.Read(b, out WorldBillboardVertex[] vb, out float[] ob);
            if (va.Length != vb.Length) return $"vertex count {va.Length} vs {vb.Length}";
            for (int i = 0; i < va.Length; i++)
                if (!va[i].Equals(vb[i])) return $"vertex[{i}] differs ({va[i]} vs {vb[i]})";
            if (oa.Length != ob.Length) return $"opacity count {oa.Length} vs {ob.Length}";
            for (int i = 0; i < oa.Length; i++)
                if (oa[i] != ob[i]) return $"opacity[{i}] {oa[i]} vs {ob[i]}";
            return null;
        }

        [Test]
        public void MaskedDrop_PointOnly_MatchesReference_SurvivorContentIdentical()
        {
            var keepTile = new TileId { Z = 12, X = 2200, Y = 1500 };
            var dropTile = new TileId { Z = 12, X = 2201, Y = 1500 };
            long keepKey = Tk(keepTile), dropKey = Tk(dropTile);

            using var hMasked = new Harness();
            using var hRef = new Harness();
            var storeMasked = new SymbolTileStore(cacheCap: 16);
            var storeRef = new SymbolTileStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileStore store in new[] { storeMasked, storeRef })
                {
                    SeedTile(store, keepTile, PointSymbol(hMasked.Origin, "keep", 1, keepKey));
                    // DROP anchor offset so it does NOT collide with KEEP (same-point ⇒ one is suppressed to 0 and the
                    // "genuinely live before Drop" sanity can't hold); still well inside the 256px viewport (SF8).
                    SeedTile(store, dropTile, PointSymbol(hMasked.Origin + DropOffset, "dropPoint", 2, dropKey));
                }

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                int maskedVersion = 0, refVersion = 0; // Each rebuild of the SAME plan object gets a fresh version
                try
                {
                    // Frame 1: both tiles Keep on both harnesses — establishes a live (full-opacity) fade for the
                    // drop tile's point too (default +∞ deltaTime snaps to full). The collision verdict a
                    // Tick's emit reads is harvested from the PREVIOUS Tick — duplicate each harness's
                    // Tick call (SAME plan+args) before an assertion reads placement output.
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.Greater(MaxAlpha(hMasked.System, dropKey), 0.99f, "sanity: the drop tile's point is genuinely live before the Drop");

                    // Frame 2: MASKED flags the drop tile's winner Dropped (resident); REFERENCE excludes it.
                    // Duplicate again — LastSurvivorCount/LastQuadCount otherwise still read frame 1's
                    // harvested (pre-Drop) verdict rather than the collision frame 2 itself just scheduled with
                    // the Drop applied, which would make the comparison below pass without exercising the Drop
                    // mask at all.
                    BuildMaskedPlan(storeMasked, planMasked, dropKey, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildReferencePlan(storeRef, planRef, dropKey, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);

                    Assert.AreEqual(hRef.System.LastCandidateCount, hMasked.System.LastCandidateCount,
                        "masked-Drop's candidate count must equal the reference's (the Dropped point is absent from collision)");
                    Assert.AreEqual(hRef.System.LastSurvivorCount, hMasked.System.LastSurvivorCount,
                        "masked-Drop's survivor count must equal the reference's");
                    Assert.AreEqual(hRef.System.LastQuadCount, hMasked.System.LastQuadCount,
                        "masked-Drop's emitted quad count must equal the reference's");
                    Assert.AreEqual(1, hRef.System.LastQuadCount, "sanity: only the KEEP point ever draws");

                    bool maskedHasKeep = hMasked.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshMasked);
                    bool refHasKeep = hRef.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshRef);
                    Assert.IsTrue(maskedHasKeep && refHasKeep, "the surviving KEEP point's world slot must exist on both sides");
                    Assert.IsNull(FirstMeshDifference(keepMeshRef, keepMeshMasked),
                        "the surviving KEEP point's full vertex+opacity content must be byte-identical whether the Drop tile is masked or absent");

                    // The Dropped point is hard-skipped BEFORE emit (masked) / absent (reference); either way its slot
                    // is not emitted this frame, so WorldSymbolRenderer disables the Renderer but RETAINS the stale
                    // frame-1 mesh until idle-reclaim. Equivalence is therefore masked-drop-slot == reference-drop-slot
                    // (both hidden, both stale-identical), NOT "absolutely invisible" (the retained mesh reads its old
                    // opacity on both sides). This differential still fails loudly if residency changed the slot at all.
                    bool maskedHasDrop = hMasked.System.TryGetWorldSlotMesh(dropKey, 0, SymbolKind.Text, out Mesh dropMeshMasked);
                    bool refHasDrop = hRef.System.TryGetWorldSlotMesh(dropKey, 0, SymbolKind.Text, out Mesh dropMeshRef);
                    Assert.AreEqual(refHasDrop, maskedHasDrop, "the Dropped point's world slot must be present/absent identically masked vs reference");
                    if (maskedHasDrop && refHasDrop)
                        Assert.IsNull(FirstMeshDifference(dropMeshRef, dropMeshMasked),
                            "the Dropped point's world slot content must be byte-identical whether resident-masked or physically absent");

                    // Content equality alone can't catch a KEEP↔DROP swap (both slots hold identical stale/rebuilt
                    // meshes); visibility is carried separately by the presenter's MeshRenderer.enabled. Assert the
                    // RENDERED set — KEEP visible, DROP hidden, on BOTH paths — a swap flips these and fails here.
                    Assert.IsTrue(hMasked.System.IsWorldSlotVisible(keepKey, 0, SymbolKind.Text), "masked: the surviving KEEP slot must be VISIBLE");
                    Assert.IsTrue(hRef.System.IsWorldSlotVisible(keepKey, 0, SymbolKind.Text), "reference: the surviving KEEP slot must be VISIBLE");
                    Assert.IsFalse(hMasked.System.IsWorldSlotVisible(dropKey, 0, SymbolKind.Text), "masked: the Dropped slot must be HIDDEN (Renderer disabled), not merely stale-mesh-identical");
                    Assert.IsFalse(hRef.System.IsWorldSlotVisible(dropKey, 0, SymbolKind.Text), "reference: the absent Drop slot must be HIDDEN");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }

        [Test]
        public void MaskedDrop_CurvedOnly_MatchesReference_SurvivorContentIdentical()
        {
            var keepTile = new TileId { Z = 12, X = 2300, Y = 1500 };
            var dropTile = new TileId { Z = 12, X = 2301, Y = 1500 };
            long keepKey = Tk(keepTile), dropKey = Tk(dropTile);

            using var hMasked = new Harness();
            using var hRef = new Harness();
            var storeMasked = new SymbolTileStore(cacheCap: 16);
            var storeRef = new SymbolTileStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileStore store in new[] { storeMasked, storeRef })
                {
                    SeedTile(store, keepTile, CurvedSymbol(hMasked.Origin, "keepCurved", 1, keepKey));
                    // DROP anchor offset so it does NOT collide with KEEP (see the point-only test); in-viewport (SF8).
                    SeedTile(store, dropTile, CurvedSymbol(hMasked.Origin + DropOffset, "dropCurved", 2, dropKey));
                }

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                int maskedVersion = 0, refVersion = 0; // Each rebuild of the SAME plan object gets a fresh version
                try
                {
                    // Frame 1: both tiles Keep — establishes a LIVE fade for the drop tile's CURVED record.
                    // Explicitly assert it actually survived + placed with positive opacity (the
                    // prior version never proved this) — this is what makes MarkFadeOutIfAlive's "still alive ⇒
                    // keep staging" branch a genuine divergence risk if Drop is ever folded into that OR-chain.
                    // The collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick —
                    // duplicate each harness's Tick call (SAME plan+args) before an assertion reads placement output.
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.Greater(hMasked.System.LastSurvivorCount, 0, "sanity: at least one curved placement survived frame 1");
                    Assert.Greater(MaxAlpha(hMasked.System, dropKey), 0.99f,
                        "the drop tile's curved record must be a genuinely LIVE (placed, positive-opacity) fade before the Drop");

                    // Frame 2: MASKED flags the drop tile's curved winner Dropped (resident); REFERENCE excludes it.
                    // Duplicate again — see the point-only test's identical comment for why (otherwise the
                    // comparison below reads frame 1's stale harvested verdict, not frame 2's own Drop-masked
                    // collision).
                    BuildMaskedPlan(storeMasked, planMasked, dropKey, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildReferencePlan(storeRef, planRef, dropKey, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);

                    Assert.AreEqual(hRef.System.LastCandidateCount, hMasked.System.LastCandidateCount,
                        "masked-Drop's candidate count must equal the reference's (the Dropped curved anchors are absent)");
                    Assert.AreEqual(hRef.System.LastSurvivorCount, hMasked.System.LastSurvivorCount,
                        "masked-Drop's survivor count must equal the reference's");
                    Assert.AreEqual(hRef.System.LastQuadCount, hMasked.System.LastQuadCount,
                        "masked-Drop's emitted quad count must equal the reference's");

                    bool maskedHasKeep = hMasked.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshMasked);
                    bool refHasKeep = hRef.System.TryGetWorldSlotMesh(keepKey, 0, SymbolKind.Text, out Mesh keepMeshRef);
                    Assert.IsTrue(maskedHasKeep && refHasKeep, "the surviving KEEP curved label's world slot must exist on both sides");
                    Assert.IsNull(FirstMeshDifference(keepMeshRef, keepMeshMasked),
                        "the surviving KEEP curved label's full vertex+opacity content must be byte-identical whether the Drop tile is masked or absent");

                    // Same equivalence as the point test, but the drop record had a genuinely LIVE curved fade before
                    // the Drop — so this proves masking a live curved record is bit-identical to its absence (it is NOT
                    // soft-faded via MarkFadeOutIfAlive, because the hard-skip precedes that chain).
                    bool maskedHasDrop = hMasked.System.TryGetWorldSlotMesh(dropKey, 0, SymbolKind.Text, out Mesh dropMeshMasked);
                    bool refHasDrop = hRef.System.TryGetWorldSlotMesh(dropKey, 0, SymbolKind.Text, out Mesh dropMeshRef);
                    Assert.AreEqual(refHasDrop, maskedHasDrop, "the Dropped curved record's world slot must be present/absent identically masked vs reference");
                    if (maskedHasDrop && refHasDrop)
                        Assert.IsNull(FirstMeshDifference(dropMeshRef, dropMeshMasked),
                            "the Dropped curved record's world slot content must be byte-identical whether resident-masked or physically absent");

                    // As in the point test: prove the RENDERED set, not just buffer content — KEEP visible, DROP
                    // hidden on BOTH paths (MeshRenderer.enabled), so a KEEP↔DROP swap can't pass on stale meshes.
                    Assert.IsTrue(hMasked.System.IsWorldSlotVisible(keepKey, 0, SymbolKind.Text), "masked: the surviving KEEP curved slot must be VISIBLE");
                    Assert.IsTrue(hRef.System.IsWorldSlotVisible(keepKey, 0, SymbolKind.Text), "reference: the surviving KEEP curved slot must be VISIBLE");
                    Assert.IsFalse(hMasked.System.IsWorldSlotVisible(dropKey, 0, SymbolKind.Text), "masked: the Dropped curved slot must be HIDDEN (Renderer disabled)");
                    Assert.IsFalse(hRef.System.IsWorldSlotVisible(dropKey, 0, SymbolKind.Text), "reference: the absent Drop curved slot must be HIDDEN");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }

        // ── All-Dropped regression: an ALL-Dropped frame must freeze live fades, not decay them ────────────────
        [Test]
        public void AllDropped_FadeStaysFrozen_ReappearOpacityMatchesReference()
        {
            var soloTile = new TileId { Z = 12, X = 2400, Y = 1500 };
            long soloKey = Tk(soloTile);

            using var hMasked = new Harness();
            using var hRef = new Harness();
            var storeMasked = new SymbolTileStore(cacheCap: 16);
            var storeRef = new SymbolTileStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileStore store in new[] { storeMasked, storeRef })
                    SeedTile(store, soloTile, PointSymbol(hMasked.Origin, "solo", 1, soloKey));

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                int maskedVersion = 0, refVersion = 0; // Each rebuild of the SAME plan object gets a fresh version
                try
                {
                    // Frame 1 (default +∞ deltaTime): visible, opacity snaps to 1.0 on both sides. The
                    // collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick —
                    // duplicate each harness's Tick call (SAME plan+args) before an assertion reads placement
                    // output.
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.Greater(MaxAlpha(hMasked.System, soloKey), 0.99f, "sanity: frame 1 is fully visible");

                    // Frame 2: EVERY resident record is Dropped (the only tile in the universe) — masked side keeps
                    // the winner resident+flagged Dropped; reference excludes it entirely (physically absent, the
                    // ground truth: 0 winners ⇒ _mirrorCount == 0 regardless of any gate, so the reference is
                    // fix-independent). A LARGE deltaTime here makes a wrongly-firing decay unambiguous (a step
                    // large enough to fully zero-and-remove the fade entry).
                    const float bigDeltaTime = 1.0f;
                    BuildMaskedPlan(storeMasked, planMasked, soloKey, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas, bigDeltaTime);
                    BuildReferencePlan(storeRef, planRef, soloKey, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas, bigDeltaTime);

                    // Frame 3: reappear (Keep again on both sides) with a SMALL deltaTime, so a frozen fade (current
                    // == 1.0) and a decayed-then-removed fade (current == 0, fading in fresh) land at visibly
                    // different opacities — not coincidentally re-converged by a single symmetric ease step.
                    // F2 (all-Dropped / zero-winner) never reaches ScheduleCollision — the whole placement
                    // block, collision included, is gated on _mirrorNonDroppedCount > 0 — so nothing
                    // is pending when F3 harvests, and F3's OWN emit reads an EMPTY _placedLastFrame (harvest's
                    // "no pending" branch), easing solo DOWN one smallDeltaTime step before its own newly-scheduled
                    // collision (solo alone, trivial winner) can be harvested. Duplicate F3 so that harvest lands
                    // (SAME args — still fade-neutral: solo is a live candidate throughout, never decays via
                    // DecayUnseenFadeSymbols) — both sides dip identically then recover, so the comparison and the
                    // "reference never decayed" sanity both still hold once the second F3 Tick's harvest lands.
                    const float smallDeltaTime = 0.05f;
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas, smallDeltaTime);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas, smallDeltaTime);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas, smallDeltaTime);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas, smallDeltaTime);

                    float reappearMasked = MaxAlpha(hMasked.System, soloKey);
                    float reappearRef = MaxAlpha(hRef.System, soloKey);
                    Assert.AreEqual(reappearRef, reappearMasked, 1e-6f,
                        "reappear opacity must match the reference (frozen fade) — an all-Dropped frame must not decay a live fade");
                    Assert.Greater(reappearRef, 0.99f, "sanity: the reference's fade never decayed (frozen), so it's still ~full opacity");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolHaloEmitTests — text-halo-color rides a uniform so a restyle can ease it
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolHaloEmitTests
    {
        private static readonly GeoCoordinate Anchor =
            new GeoCoordinate { Latitude = 48.2082, Longitude = 16.3738 };

        // Authored text-halo-*, in the units a style uses: sRGB colour, LOGICAL px.
        private const float HaloWidthLogicalPx = 2.5f;
        private const float HaloBlurLogicalPx  = 1.5f;
        private const float HaloAlpha          = 0.7f;
        private static readonly float3 HaloSrgb = new float3(0.5f, 0.25f, 0.75f); // no channel equal: a
                                                                                  // swapped channel is visible
        private static readonly float3 TextSrgb = new float3(0.1f, 0.9f, 0.2f);

        private static SymbolPaint Paint(float haloWidthPx) => new SymbolPaint
        {
            TextColor   = new float4(TextSrgb, 1f),
            Opacity     = 1f,
            HaloColor   = new float4(HaloSrgb, HaloAlpha),
            HaloWidthPx = haloWidthPx,
            HaloBlurPx  = HaloBlurLogicalPx,
        };

        private static SymbolTileBuffer MakeLabel(in SymbolPaint paint, int glyphCount)
        {
            // At the camera's own anchor: a label anywhere else risks the distance cull, which would read as
            // "the halo did not emit" rather than as the precondition failure it is.
            double3 anchorRender = new WebMercatorProjection().Project(Anchor);
            var quads = new List<SymbolQuad>();
            for (int g = 0; g < glyphCount; g++)
                quads.Add(new SymbolQuad
                {
                    TopLeft       = new float2(-6f + g * 10f, 18f),
                    BottomRight   = new float2(12f + g * 10f, 0f),
                    UvTopLeft     = new float2(0.1f, 0.1f),
                    UvBottomRight = new float2(0.4f, 0.4f),
                    LineIndex     = 0,
                });

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, float2.zero, new float2(18f, 18f),
                up: new double3(0.0, 1.0, 0.0), paint: paint, textSizePx: 24f, sortKey: 0f,
                featureIndex: 0, tileKey: 0L);
            return buffer;
        }

        /// <summary>Runs one real Tick at <paramref name="devicePixelRatio"/> and returns the built world
        /// mesh's stream-0 vertices, its stream-1 opacity and its index buffer.</summary>
        private static void RunTick(SymbolTileBuffer buffer, double devicePixelRatio,
            out WorldBillboardVertex[] vertices, out float[] opacity, out int[] indices)
        {
            var projection = new WebMercatorProjection();
            var camGo = new GameObject("SymbolHaloEmit_TestCamera");
            // Owned so the finally can release them. A camera left ENABLED with a live target keeps
            // rendering on every Editor update after this fixture returns, and the visual fixtures that run
            // later sample real pixels — leaking GPU state out of here shows up as THEIR failure, which is a
            // miserable thing to debug.
            RenderTexture target = null;
            Material textMaterial = null;
            try
            {
                var uCam = camGo.AddComponent<Camera>();
                uCam.enabled = false;
                target = new RenderTexture(320, 240, 0);
                uCam.targetTexture = target;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = Anchor.Latitude, Longitude = Anchor.Longitude, Altitude = 0.0 },
                    zoom: 6.0, heading: 0.0, tilt: 0.0), projection: projection);
                mapCamera.DevicePixelRatio = devicePixelRatio;

                var frame = new SceneFrame
                {
                    SceneOriginRender = mapCamera.Projection.Project(Anchor),
                    Rebase            = float3x3.identity,
                };
                var atlasTexture = BuildTinyAtlasTexture();
                textMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                var system = new SymbolPlacementSystem(mapCamera, worldTextBase: textMaterial);
                try
                {
                    // Collision verdicts apply one Tick late — duplicate before reading placement.
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    system.TickSymbols(in frame, buffer, atlasTexture, projection);
                    Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh),
                        "precondition: the label must place and build a world slot mesh.");
                    WorldMeshReadback.Read(mesh, out vertices, out opacity);
                    indices = mesh.GetIndices(0);
                }
                finally
                {
                    system.Dispose();
                    atlasTexture.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                if (target != null)
                {
                    if (RenderTexture.active == target) RenderTexture.active = null;
                    target.Release();
                    Object.DestroyImmediate(target);
                }
                if (textMaterial != null) Object.DestroyImmediate(textMaterial);
            }
        }

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph
            {
                Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16],
            };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static float3 Linear(in float3 srgb)
        {
            Color c = new Color(srgb.x, srgb.y, srgb.z, 1f).linear;
            return new float3(c.r, c.g, c.b);
        }

        /// <summary>
        /// A label with a halo emits its glyph run TWICE — the text corners then the halo corners — and the
        /// halo's triangles come FIRST in the index buffer. That ordering is the whole point of the change:
        /// with ZWrite Off and one queue, submission order IS the layering, so a halo that indexed second
        /// would paint over the glyphs it is meant to sit behind.
        /// </summary>
        [Test]
        public void HaloedLabel_EmitsBothRuns_WithEveryHaloTriangleBeforeEveryTextTriangle()
        {
            const int glyphs = 3;
            RunTick(MakeLabel(Paint(HaloWidthLogicalPx), glyphs), devicePixelRatio: 1.0,
                out WorldBillboardVertex[] v, out _, out int[] indices);

            Assert.AreEqual(glyphs * 8, v.Length,
                $"{glyphs} glyphs x (4 text corners + 4 halo corners) — a halo that did not emit reads as {glyphs * 4}.");
            Assert.AreEqual(glyphs * 12, indices.Length, "two triangles per run per glyph");

            // Vertices [0, 4g) are the text run and [4g, 8g) the halo run (Emit keeps the text quad at the
            // base of the block it has always been at). The FIRST half of the index buffer must reference
            // only the halo half, and the second half only the text half.
            int textVertEnd = glyphs * 4;
            for (int i = 0; i < glyphs * 6; i++)
                Assert.GreaterOrEqual(indices[i], textVertEnd,
                    $"index {i} is in the first (halo) block but points at a TEXT vertex ({indices[i]}) — the " +
                    "halo would rasterize into the text run's place.");
            for (int i = glyphs * 6; i < indices.Length; i++)
                Assert.Less(indices[i], textVertEnd,
                    $"index {i} is in the second (text) block but points at a HALO vertex ({indices[i]}) — the " +
                    "halo would draw LAST, i.e. over the glyphs it must sit behind.");
        }

        /// <summary>
        /// The halo run carries <c>text-halo-color</c> (linearized exactly once, its own alpha folded onto
        /// the opacity stream) and <c>text-halo-width</c>/<c>-blur</c>; the text run carries
        /// <c>text-color</c> and a ZERO widening, which is what makes the shader's one shading path render a
        /// plain glyph.
        /// </summary>
        [Test]
        public void HaloRun_CarriesHaloColourAndWidening_TextRunCarriesNeither()
        {
            RunTick(MakeLabel(Paint(HaloWidthLogicalPx), glyphCount: 1), devicePixelRatio: 1.0,
                out WorldBillboardVertex[] v, out float[] opacity, out _);
            Assert.AreEqual(8, v.Length, "precondition: one glyph, both runs");

            float3 expectedText = Linear(TextSrgb);
            float3 expectedHalo = Linear(HaloSrgb);

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(0f, v[i].SdfWidenPx.x, 1e-6f, $"text corner {i}: edge widening must be ZERO");
                Assert.AreEqual(0f, v[i].SdfWidenPx.y, 1e-6f, $"text corner {i}: AA widening must be ZERO");
                AssertColor(expectedText, v[i].ColorRGB, $"text corner {i}");
            }

            for (int i = 4; i < 8; i++)
            {
                Assert.AreEqual(HaloWidthLogicalPx, v[i].SdfWidenPx.x, 1e-4f,
                    $"halo corner {i}: text-halo-width must reach the vertex (dpr 1 ⇒ logical == device).");
                Assert.AreEqual(HaloBlurLogicalPx, v[i].SdfWidenPx.y, 1e-4f,
                    $"halo corner {i}: text-halo-blur must reach the vertex.");
                AssertColor(expectedHalo, v[i].ColorRGB,
                    $"halo corner {i} — a value near the authored sRGB means the sRGB→linear convert was " +
                    "dropped; far below linear means it was applied twice");
                Assert.AreEqual(opacity[i - 4] * HaloAlpha, opacity[i], 1e-4f,
                    $"halo corner {i}: text-halo-color's alpha multiplies the text opacity — the shader has " +
                    "no halo alpha of its own to apply it with.");
            }
        }

        private static void AssertColor(in float3 expected, in float3 actual, string what)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f, $"{what}: red");
            Assert.AreEqual(expected.y, actual.y, 1e-4f, $"{what}: green");
            Assert.AreEqual(expected.z, actual.z, 1e-4f, $"{what}: blue");
        }

        /// <summary>
        /// <c>text-halo-width</c>/<c>-blur</c> are authored in LOGICAL px and the SDF shader measures in
        /// DEVICE px, so both are scaled by the live device-pixel ratio — TOGETHER. Scaling one
        /// without the other renders the halo inconsistently on a 2x panel; not scaling at all renders it
        /// half as thick as styled.
        /// </summary>
        [Test]
        public void HaloWidthAndBlur_ScaleTogetherWithDevicePixelRatio()
        {
            RunTick(MakeLabel(Paint(HaloWidthLogicalPx), glyphCount: 1), devicePixelRatio: 2.0,
                out WorldBillboardVertex[] v, out _, out _);
            Assert.AreEqual(8, v.Length, "precondition: one glyph, both runs");

            for (int i = 4; i < 8; i++)
            {
                Assert.AreEqual(HaloWidthLogicalPx * 2f, v[i].SdfWidenPx.x, 1e-4f,
                    $"halo corner {i}: text-halo-width must read {HaloWidthLogicalPx * 2f} device px at dpr 2.");
                Assert.AreEqual(HaloBlurLogicalPx * 2f, v[i].SdfWidenPx.y, 1e-4f,
                    $"halo corner {i}: text-halo-blur takes the SAME ratio as the width.");
            }
        }

        /// <summary>
        /// A zero <c>text-halo-width</c> — the spec default, and most layers — emits no halo run at all, so a
        /// haloless label costs exactly what it did before the halo left the shader.
        /// </summary>
        [Test]
        public void HalolessLabel_EmitsOneRunOnly()
        {
            RunTick(MakeLabel(Paint(haloWidthPx: 0f), glyphCount: 2), devicePixelRatio: 1.0,
                out WorldBillboardVertex[] v, out _, out int[] indices);

            Assert.AreEqual(2 * 4, v.Length, "a zero-width halo must emit NO second run");
            Assert.AreEqual(2 * 6, indices.Length, "…and no second set of triangles");
            for (int i = 0; i < v.Length; i++)
                Assert.AreEqual(float2.zero, v[i].SdfWidenPx, $"corner {i}: the text run never widens");
        }
    }
}
