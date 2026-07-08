// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 (the
// namespace-collision trap — see LabelBox.cs's header comment).

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// S20 Slice 2 — the crown-jewel screen-space collision core (stage doc §4 T1): greedy,
    /// permutation-invariant, sort-key-driven survivor selection over a set of <see cref="LabelBox"/>es.
    ///
    /// <para>MapLibre places symbols greedily in <c>(sort-key, tiebreak)</c> order and drops any symbol
    /// whose box overlaps one that is ALREADY placed (order-based, not pairwise-symmetric). Equal sort
    /// keys resolve by a stable total-order tiebreak — <see cref="LabelBox.FeatureIndex"/> then
    /// <see cref="LabelBox.TileKey"/> — so the survivor set is deterministic and independent of the input
    /// order (T1(a),(e)). The overlap flags refine the rule: <see cref="LabelBox.AllowOverlap"/> places
    /// unconditionally; <see cref="LabelBox.IgnorePlacement"/> places but never blocks a later label.</para>
    ///
    /// <para>First cut is brute-force O(n²) over the sorted array (F3: a uniform screen grid is a later
    /// optimization — the T1 teeth are outcome-based, so the acceleration structure can change without
    /// touching them). No managed allocation: the caller owns the reused <c>boxes</c>/<c>survivor</c>
    /// arrays and the sort is in place (T4).</para>
    /// </summary>
    public static class LabelCollision
    {
        /// <summary>
        /// The total placement order: LOWER <see cref="LabelBox.SortKey"/> first (MapLibre priority),
        /// then LOWER <see cref="LabelBox.FeatureIndex"/>, then LOWER <see cref="LabelBox.TileKey"/>. The
        /// tiebreak makes the order TOTAL — no two distinct labels compare equal (feature index + tile key
        /// are unique per label) — so the greedy winner is deterministic even for equal sort keys (T1(e)).
        /// </summary>
        public static int ComparePlacementOrder(in LabelBox a, in LabelBox b)
        {
            if (a.SortKey < b.SortKey) return -1;
            if (a.SortKey > b.SortKey) return 1;
            if (a.FeatureIndex != b.FeatureIndex) return a.FeatureIndex < b.FeatureIndex ? -1 : 1;
            if (a.TileKey != b.TileKey) return a.TileKey < b.TileKey ? -1 : 1;
            return 0;
        }

        /// <summary>Half-open AABB overlap test (touching edges do NOT count as overlapping).</summary>
        public static bool Overlaps(in LabelBox a, in LabelBox b)
            => a.Min.x < b.Max.x && a.Max.x > b.Min.x &&
               a.Min.y < b.Max.y && a.Max.y > b.Min.y;

        /// <summary>
        /// Sorts <paramref name="boxes"/><c>[0..count)</c> into placement order (in place) and greedily
        /// selects survivors: a box is placed unless it overlaps an already-placed BLOCKING box; a placed
        /// box blocks later ones unless it ignores placement. Writes <paramref name="survivor"/><c>[i]</c>
        /// for SORTED position <c>i</c> (survivor identity is <c>boxes[i].LabelIndex</c>) and returns the
        /// survivor count. Zero managed allocation — safe on the per-frame path (T4).
        /// </summary>
        /// <param name="boxes">The candidate boxes; reordered in place into placement order.</param>
        /// <param name="count">Number of valid entries in <paramref name="boxes"/> (may be &lt; its length).</param>
        /// <param name="survivor">Caller-owned output flags, length &gt;= <paramref name="count"/>.</param>
        public static int SelectSurvivors(LabelBox[] boxes, int count, bool[] survivor)
        {
            if (boxes == null) throw new ArgumentNullException(nameof(boxes));
            if (survivor == null) throw new ArgumentNullException(nameof(survivor));
            if (count <= 0) return 0;
            if (count > boxes.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "count exceeds the boxes array length");
            if (count > survivor.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "count exceeds the survivor array length");

            InsertionSort(boxes, count);

            int survivors = 0;
            for (int i = 0; i < count; i++)
            {
                bool place = boxes[i].AllowOverlap;
                if (!place)
                {
                    place = true;
                    for (int j = 0; j < i; j++)
                    {
                        if (!survivor[j]) continue;              // j was itself dropped — cannot block
                        if (boxes[j].IgnorePlacement) continue;  // j placed but does not block others
                        if (Overlaps(in boxes[i], in boxes[j])) { place = false; break; }
                    }
                }
                survivor[i] = place;
                if (place) survivors++;
            }
            return survivors;
        }

        // Insertion sort: in place, zero-alloc, and stable (irrelevant here — ComparePlacementOrder is a
        // total order — but it costs nothing). O(n²) matches the brute-force greedy that follows; visible
        // on-screen label counts are modest (tens to low hundreds), well within budget for the first cut.
        private static void InsertionSort(LabelBox[] a, int n)
        {
            for (int i = 1; i < n; i++)
            {
                LabelBox key = a[i];
                int j = i - 1;
                while (j >= 0 && ComparePlacementOrder(in a[j], in key) > 0)
                {
                    a[j + 1] = a[j];
                    j--;
                }
                a[j + 1] = key;
            }
        }
    }
}
