// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One label's per-frame placement input, produced from real point features + a parsed <c>Symbol</c>
    /// style layer by <c>StyledSymbolTileBuilder</c> (tests hand-build them directly). A plain
    /// <c>sealed class</c> (not a blittable struct) because <see cref="Layout"/> is a
    /// managed <see cref="TextLayoutResult"/> (owns a <c>List&lt;SymbolQuad&gt;</c>) — this type never
    /// crosses the Jobs boundary itself; <see cref="MapRenderer.Core.Text.Placement.PlacedQuad"/> is the
    /// blittable per-quad record the job actually consumes.
    /// </summary>
    public sealed class LabelInstance
    {
        /// <summary>The label's feature anchor, already projected to render space (pre-RTC) — the SAME
        /// space tile geometry projects into (<c>camera.Projection.Project(geo)</c>).</summary>
        public double3 AnchorRender { get; init; }

        /// <summary>S19's size-independent, baked-px quads (<c>OneEm</c> = 24) + block bbox for this label.
        /// Point labels only; a line label carries <see cref="CurvedGlyphs"/> + <see cref="PathRender"/> instead.</summary>
        public TextLayoutResult Layout { get; init; }

        /// <summary><c>symbol-placement</c>. Default <see cref="Text.SymbolPlacement.Point"/> — selects the
        /// per-frame placement path (point anchor vs. curved along-line walk, #5).</summary>
        public SymbolPlacement Placement { get; init; }

        /// <summary>The line's render-space vertices (PRE-RTC), for <see cref="Text.SymbolPlacement.Line"/> /
        /// <see cref="Text.SymbolPlacement.LineCenter"/>; null for point labels. Projected + walked per frame.</summary>
        public double3[] PathRender { get; init; }

        /// <summary>A-2: the zoom-invariant along-line anchors (tile-space <see cref="LineAnchor"/> topology,
        /// computed once at build) the per-frame walk places the label at — one per repeat for
        /// <see cref="Text.SymbolPlacement.Line"/>, one centred for <see cref="Text.SymbolPlacement.LineCenter"/>.
        /// Null for point labels. Indexes into <see cref="PathRender"/>. Replaces the old fixed screen-px-from-start
        /// anchor walk (which slid + popped on zoom).</summary>
        public LineAnchor[] LineAnchors { get; init; }

        /// <summary>Per-glyph curved layout (<see cref="CurvedTextLayout"/>) for a line label; null for point
        /// labels (which use <see cref="Layout"/>). Placed along <see cref="PathRender"/> each frame (#5).</summary>
        public IReadOnlyList<CurvedGlyph> CurvedGlyphs { get; init; }

        /// <summary>Resolved `text-*` paint for this label.</summary>
        public LabelPaint Paint { get; init; }

        /// <summary>A-3: the resolved label text — folded into the <see cref="CrossTileLabelKey"/> cross-tile
        /// identity (so two different labels sharing a quantized cell never merge). Also handy for debugging.</summary>
        public string Text { get; init; }

        /// <summary>`text-size` in pixels — the zoom-dependent style value S20 scales the baked-px quads by
        /// (<c>screenQuad = anchorScreen + bakedQuad * (TextSizePx / TextQuadLayout.OneEm)</c>, S19 T8a).</summary>
        public float TextSizePx { get; init; }

        /// <summary>`symbol-sort-key` — greedy placement order (Slice 2). LOWER is placed FIRST (MapLibre
        /// priority: a lower sort key wins a collision against a higher one).</summary>
        public float SortKey { get; init; }

        // A-2: symbol-spacing is no longer a per-frame placement input — anchors are pre-computed at build time
        // into LineAnchors (from SymbolLabel.SpacingPx), so LabelInstance carries no SpacingPx.

        /// <summary>`text-max-angle` in DEGREES — a curved line label whose adjacent-glyph line curvature
        /// exceeds this at any pair is dropped at that anchor (spec default 45; line placement only). #6.</summary>
        public float MaxAngleDeg { get; init; }

        /// <summary>`text-keep-upright` — flip a right-to-left curved label so it still reads left-to-right
        /// (default true; line placement only). #6.</summary>
        public bool KeepUpright { get; init; }

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

        /// <summary>I5a — distinguishes a text label from an icon label, threaded from
        /// <see cref="MapRenderer.Core.Style.Symbol.SymbolLabel.Kind"/>. Default <see cref="LabelKind.Text"/>
        /// so every pre-I5a label (which never sets this) is unaffected. An icon label's single quad rides
        /// inside <see cref="Layout"/> (via <c>IconQuadLayout.ToLayoutResult</c>) — this carrier gains no
        /// separate icon-quad field, it just relabels the SAME point-placement path with a Kind tag (the
        /// §5.4 decision: ride <see cref="LabelRecordKind.Point"/> + this discriminator, not a parallel icon path). NOT yet
        /// consumed by the draw side (I5b).</summary>
        public LabelKind Kind { get; init; }

        /// <summary>I6 icon identity, threaded from <see cref="MapRenderer.Core.Style.Symbol.SymbolLabel.IconImage"/>;
        /// null for text. Folded into <see cref="CrossTileLabelKey"/> (guard-skip — a null value leaves a text
        /// key's hash/equality unchanged) so distinct co-located icons no longer collide in cross-tile dedup
        /// / the A-4 point fade id.</summary>
        public string IconImage { get; init; }
    }
}
