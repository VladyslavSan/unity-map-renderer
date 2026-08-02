// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// S105 Slice 2: one point feature's extracted, PRE-SHAPING label — everything
    /// <see cref="LabelInstance"/> needs EXCEPT the shaped <c>Layout</c> (which is Unity-side, produced by
    /// <c>GlyphManager</c>/<c>TextQuadLayout</c> in the Slice-3 <c>StyledSymbolTileBuilder</c>). The Core
    /// extractor (<see cref="SymbolFeatureExtractor"/>) resolves the anchor + text + paint here, engine-free
    /// and headless-testable; the Unity builder then shapes <see cref="Text"/> into a
    /// <c>TextLayoutResult</c> and emits the final <see cref="LabelInstance"/>.
    /// </summary>
    public sealed class SymbolLabel
    {
        /// <summary>The point's anchor in render space, PRE-RTC (the same space
        /// <see cref="MapRenderer.Core.Geo.IProjection.Project"/> emits — S20's per-frame
        /// <c>SceneOriginRender</c> rebase is applied later, not here). Used for
        /// <see cref="Text.SymbolPlacement.Point"/>; a line label uses <see cref="PathRender"/> instead.</summary>
        public double3 AnchorRender { get; init; }

        /// <summary><c>symbol-placement</c>. Default <see cref="Text.SymbolPlacement.Point"/>.</summary>
        public SymbolPlacement Placement { get; init; }

        /// <summary>The line's vertices in render space, PRE-RTC (only for <see cref="Text.SymbolPlacement.Line"/>
        /// / <see cref="Text.SymbolPlacement.LineCenter"/>; null for point labels). Curved along-line text (#5)
        /// walks the per-frame projection of this path.</summary>
        public double3[] PathRender { get; init; }

        /// <summary>A-2: the along-line anchors, computed ONCE at build time in tile space
        /// (<see cref="LineAnchorPlacement.Compute"/>) as zoom-invariant <see cref="LineAnchor"/> topology so
        /// line labels stay pinned to fixed world positions instead of sliding on zoom. Null/empty for point
        /// labels; parallel in meaning to <see cref="PathRender"/> (same polyline the anchors index into).</summary>
        public LineAnchor[] LineAnchors { get; init; }

        /// <summary>The resolved <c>text-field</c> string (never null/empty — empty resolutions are skipped).</summary>
        public string Text { get; init; }

        /// <summary>Evaluated <c>text-size</c> in pixels.</summary>
        public float TextSizePx { get; init; }

        /// <summary>Evaluated <c>text-padding</c> in pixels (spec default 2 when absent).</summary>
        public float PaddingPx { get; init; }

        /// <summary>Evaluated <c>symbol-sort-key</c> (greedy placement priority; lower placed first).</summary>
        public float SortKey { get; init; }

        /// <summary>Evaluated <c>symbol-spacing</c> in pixels — the along-line repeat distance for
        /// <see cref="Text.SymbolPlacement.Line"/> (spec default 250; ignored for point / line-center). #5 B4.</summary>
        public float SpacingPx { get; init; }

        /// <summary>Evaluated <c>text-max-angle</c> in DEGREES — the max adjacent-glyph curvature a curved line
        /// label may bend before it is dropped at that anchor (spec default 45; line placement only). #6.</summary>
        public float MaxAngleDeg { get; init; }

        /// <summary><c>text-keep-upright</c> — flip a right-to-left curved label so it reads left-to-right
        /// (default true; line placement only). #6. On an ICON curved label (P-B) this is always
        /// <c>false</c>: <c>icon-keep-upright</c>'s spec default is false, and a one-way arrow that flipped
        /// to stay upright would point against the road's direction of travel.</summary>
        public bool KeepUpright { get; init; }

        /// <summary><c>text-allow-overlap</c>.</summary>
        public bool AllowOverlap { get; init; }

        /// <summary><c>text-ignore-placement</c>.</summary>
        public bool IgnorePlacement { get; init; }

        /// <summary>Stable per-tile label ordinal (over selected features, then points) — the first S20
        /// <c>(SortKey, FeatureIndex, TileKey)</c> tiebreak component.</summary>
        public int FeatureIndex { get; init; }

        /// <summary>Packed owning-tile id (z/x/y → opaque <c>long</c>) — the second S20 tiebreak component.</summary>
        public long TileKey { get; init; }

        /// <summary>Resolved <c>text-color</c>/<c>text-opacity</c>/<c>text-halo-*</c> paint.</summary>
        public LabelPaint Paint { get; init; }

        /// <summary>Size-independent layout options (anchor/offset/justify/max-width/line-height/letter-spacing/
        /// radial-offset), assembled per feature by <see cref="TextLayoutOptionsBuilder"/>. The Unity builder
        /// feeds this straight into <c>TextQuadLayout</c> — the wiring that makes the parsed <c>text-*</c>
        /// layout keys actually affect the placed quads (Slice A).</summary>
        public TextLayoutOptions LayoutOptions { get; init; }

        /// <summary>Evaluated <c>text-translate</c> — the paint-time pixel offset (y-down, as authored)
        /// applied to the placed screen anchor per frame by <c>LabelPlacementSystem</c> (Slice C).</summary>
        public float2 TranslatePx { get; init; }

        /// <summary><c>text-translate-anchor</c> — whether <see cref="TranslatePx"/> is a screen-space
        /// (viewport) or map-space (rotates with bearing) offset. Default <see cref="TextTranslateAnchor.Map"/>.</summary>
        public TextTranslateAnchor TranslateAnchor { get; init; }

        /// <summary><c>text-rotation-alignment</c> — whether the billboard rotates with the map bearing
        /// (<c>map</c>) or stays screen-aligned (<c>viewport</c>/<c>auto</c> for point). Default
        /// <see cref="AlignmentMode.Auto"/> (#4).</summary>
        public AlignmentMode RotationAlignment { get; init; }

        /// <summary>
        /// I3 — distinguishes a text label from an icon label. Default <see cref="LabelKind.Text"/> so
        /// every pre-I3 label (which never sets this) is unaffected. An icon label reuses this same carrier's
        /// text-named fields for its icon-* counterparts rather than duplicating a parallel set:
        /// <see cref="AnchorRender"/> (icon-anchor point), <see cref="Placement"/> (always
        /// <see cref="SymbolPlacement.Point"/> — icons are point-placement only, I3), <see cref="SortKey"/>
        /// (<c>symbol-sort-key</c>, shared with text), <see cref="PaddingPx"/> (<c>icon-padding</c>),
        /// <see cref="AllowOverlap"/>/<see cref="IgnorePlacement"/> (<c>icon-allow-overlap</c>/<c>icon-ignore-placement</c>),
        /// <see cref="RotationAlignment"/> (<c>icon-rotation-alignment</c>), <see cref="Paint"/>.Opacity
        /// (<c>icon-opacity</c>). <see cref="Text"/> stays null on an icon label.
        /// </summary>
        public LabelKind Kind { get; init; }

        /// <summary>The laid-out icon quad (sheet-pixel space, anchor-relative) — meaningful only when
        /// <see cref="Kind"/> is <see cref="LabelKind.Icon"/>; default (all-zero) otherwise. This is the
        /// PADDED quad: it includes <see cref="IconSkirtPx"/> of transparent border on every side.</summary>
        public SymbolQuad IconQuad { get; init; }

        /// <summary>
        /// Baked-px width of the transparent border baked into <see cref="IconQuad"/>, per side — the value
        /// <c>IconQuadLayout.SkirtPx</c> produced for this sprite at this <c>icon-size</c>. Carried rather
        /// than re-derived downstream so the grow (in <c>IconQuadLayout.Layout</c>) and the un-grow (in
        /// <c>IconQuadLayout.ToLayoutResult</c> / <c>LabelBox.BuildRotatedGlyph</c>) cannot drift apart.
        /// Meaningful only when <see cref="Kind"/> is <see cref="LabelKind.Icon"/>; <c>0</c> otherwise.
        /// </summary>
        public float IconSkirtPx { get; init; }

        /// <summary>P-B: <c>icon-rotate</c> in RADIANS, positive = clockwise on screen (MapLibre's sense, kept
        /// verbatim on every carrier — the staging frame's opposite sense is entered once, far downstream, at
        /// <c>LabelBearing.IconRotationRadians</c>) — the degrees→radians
        /// conversion happens ONCE here, at extract (the <see cref="MapRenderer.Core.Geo.Angle"/> rule). A
        /// constant angular offset composed ON TOP of whatever the icon's alignment produced; 0 (the default)
        /// on every text label and every un-rotated icon. Meaningful only when <see cref="Kind"/> is
        /// <see cref="LabelKind.Icon"/> — <c>icon-rotate</c> never rotates text.</summary>
        public float IconRotateRadians { get; init; }

        /// <summary>I6: the resolved sprite name — the icon's cross-tile identity; null for text. Threaded
        /// into <see cref="LabelInstance"/> and folded into <see cref="CrossTileLabelKey"/> so distinct
        /// co-located icons no longer collide (I5b's deferred gap).</summary>
        public string IconImage { get; init; }

        /// <summary>Road-shields §10 D8/D10: whether this label is one half of a centred icon+text pair —
        /// <see cref="LabelPairRole.Owner"/> (the icon) or <see cref="LabelPairRole.Rider"/> (the text), or
        /// <see cref="LabelPairRole.None"/> for every ordinary label. A PROPOSAL: <see cref="Placement.LabelPairing"/>
        /// resolves whether it actually holds. Default <see cref="LabelPairRole.None"/> so every pre-pairing
        /// label is unaffected.</summary>
        public LabelPairRole PairRole { get; init; }

        /// <summary>Road-shields §10 D10: the OWNER's <see cref="FeatureIndex"/>, stamped on BOTH halves of a
        /// proposed pair so <see cref="Placement.LabelPairing"/> can match them. Only unique within one
        /// <see cref="SymbolFeatureExtractor.Extract"/> call (per layer, per tile) — the resolver also matches
        /// <c>TileKey</c>/<c>MaterialIndex</c> on the downstream <see cref="LabelInstance"/> carrier for that
        /// reason. Meaningless when <see cref="PairRole"/> is <see cref="LabelPairRole.None"/>.</summary>
        public int PairId { get; init; }

        /// <summary>Stage C: this half may be DROPPED while its partner places — <c>icon-optional</c> on an icon
        /// label, <c>text-optional</c> on a text label (each property names the half it makes droppable). The
        /// pair still forms and still stages as ONE candidate; only the collision verdict gains per-half
        /// granularity (<see cref="Placement.LabelCandidate.OptionalBoxMask"/>). Meaningless unless
        /// <see cref="PairRole"/> is set; default false ⇒ MapLibre's spec default, both halves required.</summary>
        public bool PairOptional { get; init; }
    }
}
