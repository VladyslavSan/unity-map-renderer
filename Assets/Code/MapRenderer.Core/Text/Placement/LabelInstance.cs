// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members (see docs/conventions.md).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// S20 Slice 1: one label's per-frame placement input — a synthetic carrier today (hand-built by
    /// <c>SyntheticLabelSource</c> and, in every headless Bucket-A tooth, by the test itself); S105 will
    /// produce these from real point features + a parsed <c>Symbol</c> style layer (§6 F6 of the stage
    /// doc). A plain <c>sealed class</c> (not a blittable struct) because <see cref="Layout"/> is a
    /// managed <see cref="TextLayoutResult"/> (owns a <c>List&lt;SymbolQuad&gt;</c>) — this type never
    /// crosses the Jobs boundary itself; <see cref="MapRenderer.Core.Text.Placement.PlacedQuad"/> is the
    /// blittable per-quad record the job actually consumes.
    /// </summary>
    public sealed class LabelInstance
    {
        /// <summary>The label's feature anchor, already projected to render space (pre-RTC) — the SAME
        /// space tile geometry projects into (<c>camera.Projection.Project(geo)</c>).</summary>
        public double3 AnchorRender { get; init; }

        /// <summary>S19's size-independent, baked-px quads (<c>OneEm</c> = 24) + block bbox for this label.</summary>
        public TextLayoutResult Layout { get; init; }

        /// <summary>Resolved `text-*` paint for this label.</summary>
        public LabelPaint Paint { get; init; }

        /// <summary>`text-size` in pixels — the zoom-dependent style value S20 scales the baked-px quads by
        /// (<c>screenQuad = anchorScreen + bakedQuad * (TextSizePx / TextQuadLayout.OneEm)</c>, S19 T8a).</summary>
        public float TextSizePx { get; init; }

        /// <summary>`symbol-sort-key` — greedy placement order (Slice 2). LOWER is placed FIRST (MapLibre
        /// priority: a lower sort key wins a collision against a higher one).</summary>
        public float SortKey { get; init; }

        /// <summary>Feature index within its tile — the first Slice-2 stable tiebreak
        /// (<c>(SortKey, FeatureIndex, TileKey)</c>) when sort keys are equal.</summary>
        public int FeatureIndex { get; init; }

        /// <summary>Owning tile id (opaque key) — the second Slice-2 stable tiebreak.</summary>
        public long TileKey { get; init; }

        /// <summary>S105 F1: the owning symbol style layer's index — routes a surviving label to its layer's
        /// material (per-layer <c>text-halo-*</c>) in <c>LabelPlacementSystem</c>'s per-material draw
        /// grouping. Collision stays GLOBAL across all layers (all symbols share one collision index — the
        /// correct MapLibre semantics); only the draw is partitioned by this index. Default 0 (the demo /
        /// single-material path).</summary>
        public int MaterialIndex { get; init; }

        /// <summary>`text-padding` in logical pixels — grows this label's Slice-2 collision box on every
        /// edge (MapLibre style default is 2px; the object-initializer default here is 0 — the caller that
        /// resolves the style must apply the spec default, S105's job).</summary>
        public float PaddingPx { get; init; }

        /// <summary>`text-allow-overlap` — Slice-2 collision skips this label (always placed).</summary>
        public bool AllowOverlap { get; init; }

        /// <summary>`text-ignore-placement` — Slice-2 places this label but never lets it block others.</summary>
        public bool IgnorePlacement { get; init; }

        /// <summary>`text-translate` — the paint-time pixel offset (y-down, as authored) applied to this
        /// label's projected screen anchor each frame (Slice C, via
        /// <see cref="LabelTranslate.ApplyTranslate"/>). Default [0, 0].</summary>
        public float2 TranslatePx { get; init; }

        /// <summary>`text-translate-anchor` — whether <see cref="TranslatePx"/> is a screen-space (viewport)
        /// or map-space (rotates with bearing) offset. Default <see cref="TextTranslateAnchor.Map"/> (#4).</summary>
        public TextTranslateAnchor TranslateAnchor { get; init; }

        /// <summary>`text-rotation-alignment` — whether the billboard rotates with the map bearing (<c>map</c>)
        /// or stays screen-aligned (<c>viewport</c>/<c>auto</c> for point). Default
        /// <see cref="AlignmentMode.Auto"/> (#4).</summary>
        public AlignmentMode RotationAlignment { get; init; }
    }
}
