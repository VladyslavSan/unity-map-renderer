// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One raw symbol slot inside a
    /// <see cref="SymbolTileBuffer"/> build buffer, minus the managed quad/glyph/anchor/path lists (which
    /// live in the scratch's own pools; see the <c>*Start</c>/<c>*Count</c> span fields below). A
    /// <c>readonly struct</c> (not a class), taken by <see langword="in"/> at every call site, so a scratch build accumulates zero
    /// per-symbol managed allocation (the 3b win) — <see cref="SymbolTileBuffer.Symbols"/> is a
    /// <c>List&lt;ShapedSymbol&gt;</c>, so appending one is an in-place struct copy, not a heap allocation.
    /// A build appends one record per successfully-shaped symbol: a per-symbol build failure is SKIPPED outright
    /// (<c>StyledSymbolTileBuilder.ShapeAsync</c>), so the list is dense — no gap/placeholder element.
    /// </summary>
    public readonly struct ShapedSymbol
    {
        /// <summary><c>symbol-placement</c> — selects the point vs. curved along-line bake branch.</summary>
        public SymbolPlacement Placement { get; init; }

        /// <summary>I5a: text vs. icon — the <c>AtlasKind</c> discriminator source.</summary>
        public SymbolKind Kind { get; init; }

        /// <summary>S105 F1: the owning symbol style layer's index (per-layer material routing).</summary>
        public int MaterialIndex { get; init; }

        /// <summary>The resolved symbol text (point/curved text only; null for an icon). A-3 cross-tile identity.</summary>
        public string Text { get; init; }

        /// <summary>I6: the icon identity (null for text).</summary>
        public string IconImage { get; init; }

        /// <summary>Point placement only — the projected (pre-RTC) feature anchor.</summary>
        public double3 AnchorRender { get; init; }

        /// <summary>Point placement only — P2's unit surface normal at <see cref="AnchorRender"/>.</summary>
        public double3 UpRender { get; init; }

        /// <summary>Point placement only — the laid-out block's anchor-relative bounding box min corner.</summary>
        public float2 BoundsMin { get; init; }

        /// <summary>Point placement only — the laid-out block's anchor-relative bounding box max corner.</summary>
        public float2 BoundsMax { get; init; }

        /// <summary>Point placement only — this record's quad span start into <see cref="SymbolTileBuffer.Quads"/>.</summary>
        public int QuadStart { get; init; }

        /// <summary>Point placement only — this record's quad span length (0 is a valid, hazard-tested case).</summary>
        public int QuadCount { get; init; }

        /// <summary>Curved placement only — this record's glyph span start into <see cref="SymbolTileBuffer.Glyphs"/>.</summary>
        public int GlyphStart { get; init; }

        /// <summary>Curved placement only — this record's glyph span length.</summary>
        public int GlyphCount { get; init; }

        /// <summary>Curved placement only — this record's along-line anchor span start into
        /// <see cref="SymbolTileBuffer.Anchors"/> (UNCLAMPED — see that pool's doc).</summary>
        public int AnchorStart { get; init; }

        /// <summary>Curved placement only — this record's along-line anchor span length (UNCLAMPED).</summary>
        public int AnchorCount { get; init; }

        /// <summary>Curved placement only — this record's world path span start into
        /// <see cref="SymbolTileBuffer.Path"/>/<see cref="SymbolTileBuffer.PathUp"/>.</summary>
        public int PathStart { get; init; }

        /// <summary>Curved placement only — this record's world path span length.</summary>
        public int PathCount { get; init; }

        /// <summary><c>text-size</c> in pixels.</summary>
        public float TextSizePx { get; init; }

        /// <summary><c>text-padding</c> in logical pixels.</summary>
        public float PaddingPx { get; init; }

        /// <summary><c>symbol-sort-key</c> — lower placed first.</summary>
        public float SortKey { get; init; }

        /// <summary>Feature index within its tile — the first Slice-2 stable tiebreak.</summary>
        public int FeatureIndex { get; init; }

        /// <summary>Owning tile id (opaque key) — the second Slice-2 stable tiebreak.</summary>
        public long TileKey { get; init; }

        /// <summary><c>text-allow-overlap</c>.</summary>
        public bool AllowOverlap { get; init; }

        /// <summary><c>text-ignore-placement</c>.</summary>
        public bool IgnorePlacement { get; init; }

        /// <summary><c>text-translate</c> (y-down, as authored).</summary>
        public float2 TranslatePx { get; init; }

        /// <summary><c>text-translate-anchor</c>.</summary>
        public TextTranslateAnchor TranslateAnchor { get; init; }

        /// <summary><c>text-rotation-alignment</c> — point placement only.</summary>
        public AlignmentMode RotationAlignment { get; init; }

        /// <summary><c>text-max-angle</c> in degrees — curved placement only.</summary>
        public float MaxAngleDeg { get; init; }

        /// <summary><c>text-keep-upright</c> — curved placement only.</summary>
        public bool KeepUpright { get; init; }

        /// <summary>P-B <c>icon-rotate</c> in radians (MapLibre's own sense).</summary>
        public float IconRotateRadians { get; init; }

        /// <summary>W1: the resolved <c>*-pitch-alignment</c> — curved placement only.</summary>
        public AlignmentMode PitchAlignment { get; init; }

        /// <summary>Resolved <c>text-*</c> paint.</summary>
        public SymbolPaint Paint { get; init; }

        /// <summary>Road-shields §10 D8/D10 — the PROPOSED pair role (resolved by <see cref="SymbolPairing"/>).</summary>
        public SymbolPairRole PairRole { get; init; }

        /// <summary>Road-shields §10 D10 — the owner's <c>FeatureIndex</c>, stamped on both halves.</summary>
        public int PairId { get; init; }

        /// <summary>Stage C — this half may be dropped while its partner places.</summary>
        public bool PairOptional { get; init; }
    }
}
