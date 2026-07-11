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

            Sort(boxes, count);

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

        /// <summary>
        /// Grid-accelerated survivor selection — the production per-frame path. Behaviourally IDENTICAL to
        /// the brute-force <see cref="SelectSurvivors(LabelBox[],int,bool[])"/> (same sort, same greedy rule,
        /// same <see cref="Overlaps"/> decision); <paramref name="grid"/> only prunes which already-placed
        /// blockers each candidate is tested against, turning the O(n²) scan into ~O(n·k). A differential
        /// test locks this to the reference over randomized inputs. Pass a caller-owned <paramref name="grid"/>
        /// reused across frames for zero per-frame GC (T4).
        /// </summary>
        public static int SelectSurvivors(LabelBox[] boxes, int count, bool[] survivor, LabelCollisionGrid grid)
        {
            if (grid == null) return SelectSurvivors(boxes, count, survivor); // reference path
            if (boxes == null) throw new ArgumentNullException(nameof(boxes));
            if (survivor == null) throw new ArgumentNullException(nameof(survivor));
            if (count <= 0) return 0;
            if (count > boxes.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "count exceeds the boxes array length");
            if (count > survivor.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "count exceeds the survivor array length");

            Sort(boxes, count);
            grid.Reset(boxes, count);

            int survivors = 0;
            for (int i = 0; i < count; i++)
            {
                // Place if the label ignores collision, OR it overlaps no already-placed blocker (AllowOverlap
                // short-circuits the query — an allow-overlap label is never tested, matching the reference).
                bool place = boxes[i].AllowOverlap || !grid.OverlapsAny(in boxes[i], boxes);
                survivor[i] = place;
                if (place)
                {
                    survivors++;
                    // A placed label blocks later ones UNLESS it ignores placement — so only non-ignore
                    // survivors enter the grid (exactly the reference's `survivor[j] && !IgnorePlacement`).
                    if (!boxes[i].IgnorePlacement) grid.Insert(i, in boxes[i]);
                }
            }
            return survivors;
        }

        /// <summary>
        /// The total placement order for <see cref="LabelCandidate"/>s — identical rule to the
        /// <see cref="LabelBox"/> overload (lower <see cref="LabelCandidate.SortKey"/> first, then
        /// <see cref="LabelCandidate.FeatureIndex"/>, then <see cref="LabelCandidate.TileKey"/>).
        /// </summary>
        public static int ComparePlacementOrder(in LabelCandidate a, in LabelCandidate b)
        {
            if (a.SortKey < b.SortKey) return -1;
            if (a.SortKey > b.SortKey) return 1;
            if (a.FeatureIndex != b.FeatureIndex) return a.FeatureIndex < b.FeatureIndex ? -1 : 1;
            if (a.TileKey != b.TileKey) return a.TileKey < b.TileKey ? -1 : 1;
            return 0;
        }

        /// <summary>
        /// #5 (B3) — multi-box, all-or-nothing survivor selection over a set of <see cref="LabelCandidate"/>s
        /// (each spanning a contiguous range of the flat <paramref name="boxes"/> array), UNIFIED across point
        /// (1-box) and curved along-line (N-box) labels so a road name and a city name collide in the SAME
        /// greedy pass. Sorts <paramref name="candidates"/><c>[0..candidateCount)</c> into placement order in
        /// place (the boxes are NOT reordered — the grid stores absolute box indices), then greedily places a
        /// candidate iff <see cref="LabelCandidate.AllowOverlap"/> OR every one of its boxes overlaps no
        /// already-placed blocker; a placed candidate inserts ALL its boxes (unless it ignores placement).
        /// Writes <paramref name="survivor"/><c>[i]</c> for SORTED candidate position <c>i</c> (identity is
        /// <c>candidates[i].LabelIndex</c>) and returns the survivor count. Zero managed allocation (T4).
        ///
        /// <para><b>All-or-nothing / no self-block:</b> a candidate's boxes are TESTED (all must be free) and
        /// only THEN inserted, so a curved label's adjacent glyph boxes — which naturally overlap — never
        /// block one another. One colliding glyph drops the WHOLE label; a placed label blocks across its
        /// whole run.</para>
        /// </summary>
        /// <param name="candidates">The candidates; reordered in place into placement order.</param>
        /// <param name="candidateCount">Number of valid entries in <paramref name="candidates"/>.</param>
        /// <param name="boxes">The flat box pool every candidate's <see cref="LabelCandidate.BoxStart"/> range
        /// addresses; NOT reordered (stable indices).</param>
        /// <param name="boxCount">Number of valid boxes in <paramref name="boxes"/> (bounds the grid extent).</param>
        /// <param name="survivor">Caller-owned output flags, length &gt;= <paramref name="candidateCount"/>.</param>
        /// <param name="grid">Reused spatial grid (must be non-null on this path — the acceleration is the point).</param>
        public static int SelectSurvivors(
            LabelCandidate[] candidates, int candidateCount,
            LabelBox[] boxes, int boxCount,
            bool[] survivor, LabelCollisionGrid grid)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (boxes == null) throw new ArgumentNullException(nameof(boxes));
            if (survivor == null) throw new ArgumentNullException(nameof(survivor));
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (candidateCount <= 0) return 0;
            if (candidateCount > candidates.Length)
                throw new ArgumentOutOfRangeException(nameof(candidateCount), "count exceeds the candidates array length");
            if (candidateCount > survivor.Length)
                throw new ArgumentOutOfRangeException(nameof(candidateCount), "count exceeds the survivor array length");
            if (boxCount > boxes.Length)
                throw new ArgumentOutOfRangeException(nameof(boxCount), "boxCount exceeds the boxes array length");

            Sort(candidates, candidateCount);
            grid.Reset(boxes, boxCount);

            int survivors = 0;
            for (int i = 0; i < candidateCount; i++)
            {
                LabelCandidate c = candidates[i];
                int start = c.BoxStart;
                int end   = c.BoxStart + c.BoxCount;

                // Place if it ignores collision, OR none of its boxes overlaps an already-placed blocker.
                // Test ALL boxes first (all-or-nothing) — no box is inserted until the whole candidate wins.
                bool place = c.AllowOverlap;
                if (!place)
                {
                    place = true;
                    for (int b = start; b < end; b++)
                    {
                        if (grid.OverlapsAny(in boxes[b], boxes)) { place = false; break; }
                    }
                }

                survivor[i] = place;
                if (place)
                {
                    survivors++;
                    // A placed candidate blocks later ones across its WHOLE run — unless it ignores placement.
                    if (!c.IgnorePlacement)
                        for (int b = start; b < end; b++) grid.Insert(b, in boxes[b]);
                }
            }
            return survivors;
        }

        // In-place heapsort: O(n log n), zero managed allocation, no IComparer by-value copies (compares via
        // ComparePlacementOrder(in,in) directly). Replaces the original O(n²) insertion sort, which collapsed
        // at zoom-14-big-city label counts (thousands, not the "tens to low hundreds" the first cut assumed).
        // Not stable — irrelevant here: ComparePlacementOrder is a TOTAL order (no two distinct labels tie),
        // so the sorted result is unique regardless of stability.
        private static void Sort(LabelBox[] a, int n)
        {
            for (int root = n / 2 - 1; root >= 0; root--) SiftDown(a, root, n); // build max-heap
            for (int end = n - 1; end > 0; end--)
            {
                (a[0], a[end]) = (a[end], a[0]); // move current max to the sorted tail
                SiftDown(a, 0, end);
            }
        }

        // Restore the max-heap property at <paramref name="root"/> over a[0..n). "Max" is the LAST element in
        // ComparePlacementOrder, so heapsort emits ascending placement order (lowest sort-key placed first).
        private static void SiftDown(LabelBox[] a, int root, int n)
        {
            while (true)
            {
                int child = 2 * root + 1;
                if (child >= n) break;
                if (child + 1 < n && ComparePlacementOrder(in a[child], in a[child + 1]) < 0) child++;
                if (ComparePlacementOrder(in a[root], in a[child]) >= 0) break;
                (a[root], a[child]) = (a[child], a[root]);
                root = child;
            }
        }

        // Candidate heapsort — identical structure to the LabelBox Sort/SiftDown above, over the candidate
        // ComparePlacementOrder. (Duplicated rather than generic to keep the compare an `in`-by-ref call with
        // no boxing/IComparer copies — the same reason the box path is hand-rolled.)
        private static void Sort(LabelCandidate[] a, int n)
        {
            for (int root = n / 2 - 1; root >= 0; root--) SiftDown(a, root, n);
            for (int end = n - 1; end > 0; end--)
            {
                (a[0], a[end]) = (a[end], a[0]);
                SiftDown(a, 0, end);
            }
        }

        private static void SiftDown(LabelCandidate[] a, int root, int n)
        {
            while (true)
            {
                int child = 2 * root + 1;
                if (child >= n) break;
                if (child + 1 < n && ComparePlacementOrder(in a[child], in a[child + 1]) < 0) child++;
                if (ComparePlacementOrder(in a[root], in a[child]) >= 0) break;
                (a[root], a[child]) = (a[child], a[root]);
                root = child;
            }
        }
    }
}
