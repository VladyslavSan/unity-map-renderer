// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 —
// this file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to
// a (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See the
// namespace-collision trap in docs/lessons-learned.md / GlyphAtlasTexture.cs.
// BLITTABLE: kept to blittable fields only so a future Burst collision job (F3) can take it as a
// NativeArray<LabelBox> element without change (mirrors PlacedQuad / LineRibbonVertex). The Slice-2 first
// cut runs the greedy pass managed over a reused LabelBox[] (still zero per-frame GC — T4), so no job yet.

using Unity.Mathematics;
using MapRenderer.Core.Text;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// S20 Slice 2: one label's screen-space collision record — the axis-aligned bounding box
    /// <see cref="LabelCollision.SelectSurvivors"/> tests for overlap, plus the greedy placement-order key
    /// (<see cref="SortKey"/> + the <see cref="FeatureIndex"/>/<see cref="TileKey"/> stable tiebreak) and
    /// the per-label overlap flags. <see cref="Min"/>/<see cref="Max"/> are in logical screen pixels with
    /// <c>text-padding</c> ALREADY applied (see <see cref="Build"/>); <see cref="LabelIndex"/> is the
    /// back-reference to the source label list that identifies a survivor after the box array is sorted
    /// in place.
    /// </summary>
    public struct LabelBox
    {
        /// <summary>Collision AABB minimum corner, logical screen px (padding applied).</summary>
        public float2 Min;

        /// <summary>Collision AABB maximum corner, logical screen px (padding applied).</summary>
        public float2 Max;

        /// <summary>`symbol-sort-key` — greedy placement order. LOWER is placed FIRST (MapLibre priority:
        /// a lower sort key wins a collision against a higher one).</summary>
        public float SortKey;

        /// <summary>Feature index within its tile — the first stable tiebreak when <see cref="SortKey"/>s are equal.</summary>
        public int FeatureIndex;

        /// <summary>Owning tile id (opaque key) — the second stable tiebreak, guaranteeing a total order.</summary>
        public long TileKey;

        /// <summary>Index back into the caller's label list — survivor identity after the box array is sorted.</summary>
        public int LabelIndex;

        /// <summary>`text-allow-overlap` — skip the collision test and always place this label.</summary>
        public bool AllowOverlap;

        /// <summary>`text-ignore-placement` — place this label but do NOT let it block later ones.</summary>
        public bool IgnorePlacement;

        /// <summary>
        /// Builds the screen-space collision box for a label from its projected anchor, S19 block bbox
        /// (baked-px, anchor-relative — <see cref="TextLayoutResult.BoundsMin"/>/<c>BoundsMax</c>), the
        /// resolved <c>text-size</c>, and <c>text-padding</c>. Applies the SAME <c>textSizePx / OneEm</c>
        /// scale <see cref="BillboardMath.BuildQuad"/> uses for the visible quads (so the collision box
        /// tracks the rendered glyphs exactly), then grows it by <paramref name="paddingPx"/> on every
        /// edge. Pure + engine-free so <see cref="LabelPlacementSystem"/> and the T1(d) padding tooth
        /// share one implementation.
        /// </summary>
        public static LabelBox Build(
            in float2 anchorScreenPx,
            in float2 boundsMin,
            in float2 boundsMax,
            float textSizePx,
            float paddingPx,
            float sortKey,
            int featureIndex,
            long tileKey,
            int labelIndex,
            bool allowOverlap,
            bool ignorePlacement)
        {
            float scale = textSizePx / TextQuadLayout.OneEm;
            var pad = new float2(paddingPx, paddingPx);
            return new LabelBox
            {
                Min = anchorScreenPx + boundsMin * scale - pad,
                Max = anchorScreenPx + boundsMax * scale + pad,
                SortKey = sortKey,
                FeatureIndex = featureIndex,
                TileKey = tileKey,
                LabelIndex = labelIndex,
                AllowOverlap = allowOverlap,
                IgnorePlacement = ignorePlacement,
            };
        }
    }
}
