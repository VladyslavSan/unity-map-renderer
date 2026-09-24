// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// One point feature's extracted, PRE-SHAPING symbol — everything <see cref="ShapedSymbol"/> needs
    /// EXCEPT the shaped <c>Layout</c> (produced later, Unity-side, by shaping <see cref="Text"/> into quads
    /// plus a <c>TextLayoutBounds</c>). The anchor + text + paint are resolved here, engine-free and
    /// headless-testable.
    /// </summary>
    public sealed class SymbolFeature
    {
        /// <summary>The point's anchor in render space, PRE-RTC (the same space
        /// <see cref="MapRenderer.Core.Geo.IProjection.Project"/> emits — the per-frame
        /// <c>SceneOriginRender</c> rebase is applied later, not here). Used for
        /// <see cref="Text.SymbolPlacement.Point"/>; a line symbol uses <see cref="PathRender"/> instead.</summary>
        public double3 AnchorRender { get; init; }

        /// <summary>The unit surface normal at <see cref="AnchorRender"/>, from
        /// <see cref="MapRenderer.Core.Geo.IProjection.ProjectPoint"/>'s <c>Up</c> — same render space
        /// (pre-RTC) as <see cref="AnchorRender"/>. It reaches the vertex stream through
        /// <c>CandidateEmit.SurfaceUp</c>, but no shader reads it: a point symbol is never map-pitched.</summary>
        public double3 UpRender { get; init; }

        /// <summary><c>symbol-placement</c>. Default <see cref="Text.SymbolPlacement.Point"/>.</summary>
        public SymbolPlacement Placement { get; init; }

        /// <summary>The line's vertices in render space, PRE-RTC (only for <see cref="Text.SymbolPlacement.Line"/>
        /// / <see cref="Text.SymbolPlacement.LineCenter"/>; null for point symbols). Curved along-line text
        /// walks the per-frame projection of this path.</summary>
        public double3[] PathRender { get; init; }

        /// <summary>Index-parallel to <see cref="PathRender"/> (same length, same vertices) — each
        /// entry is that vertex's unit surface normal from <see cref="MapRenderer.Core.Geo.IProjection.ProjectPoint"/>.
        /// Null for point symbols. <c>SymbolStagingMath.StageCurved</c> samples it per glyph for the
        /// map-pitched glyph box, and the shader's map-pitch branch reads it.</summary>
        public double3[] PathUpRender { get; init; }

        /// <summary>The along-line anchors, computed ONCE at build time in tile space
        /// (<see cref="LineAnchorPlacement.Compute"/>) as zoom-invariant <see cref="LineAnchor"/> topology so
        /// line symbols stay pinned to fixed world positions instead of sliding on zoom. Null/empty for point
        /// symbols; parallel in meaning to <see cref="PathRender"/> (same polyline the anchors index into).</summary>
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
        /// <see cref="Text.SymbolPlacement.Line"/> (spec default 250; ignored for point / line-center).</summary>
        public float SpacingPx { get; init; }

        /// <summary>Evaluated <c>text-max-angle</c> in DEGREES — the max adjacent-glyph curvature a curved line
        /// symbol may bend before it is dropped at that anchor (spec default 45; line placement only).</summary>
        public float MaxAngleDeg { get; init; }

        /// <summary><c>text-keep-upright</c> — flip a right-to-left curved symbol so it reads left-to-right
        /// (default true; line placement only). On an ICON curved symbol this is always
        /// <c>false</c>: <c>icon-keep-upright</c>'s spec default is false, and a one-way arrow that flipped
        /// to stay upright would point against the road's direction of travel.</summary>
        public bool KeepUpright { get; init; }

        /// <summary><c>text-allow-overlap</c>.</summary>
        public bool AllowOverlap { get; init; }

        /// <summary><c>text-ignore-placement</c>.</summary>
        public bool IgnorePlacement { get; init; }

        /// <summary>Stable per-tile symbol ordinal (over selected features, then points) — the first
        /// <c>(SortKey, FeatureIndex, TileKey)</c> tiebreak component.</summary>
        public int FeatureIndex { get; init; }

        /// <summary>Packed owning-tile id (z/x/y → opaque <c>long</c>) — the second tiebreak component.</summary>
        public long TileKey { get; init; }

        /// <summary>Resolved <c>text-color</c>/<c>text-opacity</c>/<c>text-halo-*</c> paint. A CONSTANT
        /// colour rides a per-layer uniform instead and leaves white RGB here — see
        /// <see cref="Text.Placement.SymbolPaint"/>.</summary>
        public SymbolPaint Paint { get; init; }

        /// <summary>Size-independent layout options (anchor/offset/justify/max-width/line-height/letter-spacing/
        /// radial-offset), assembled per feature by <see cref="TextLayoutOptionsBuilder"/>. The Unity builder
        /// feeds this straight into <c>TextQuadLayout</c>, so the parsed <c>text-*</c> layout keys affect
        /// the placed quads.</summary>
        public TextLayoutOptions LayoutOptions { get; init; }

        /// <summary>Evaluated <c>text-translate</c> — the paint-time pixel offset (y-down, as authored)
        /// applied to the placed screen anchor per frame by <c>SymbolPlacementSystem</c>.</summary>
        public float2 TranslatePx { get; init; }

        /// <summary><c>text-translate-anchor</c> — whether <see cref="TranslatePx"/> is a screen-space
        /// (viewport) or map-space (rotates with bearing) offset. Default <see cref="TextTranslateAnchor.Map"/>.</summary>
        public TextTranslateAnchor TranslateAnchor { get; init; }

        /// <summary><c>text-rotation-alignment</c> — whether the billboard rotates with the map bearing
        /// (<c>map</c>) or stays screen-aligned (<c>viewport</c>/<c>auto</c> for point). Default
        /// <see cref="AlignmentMode.Auto"/> (#4).</summary>
        public AlignmentMode RotationAlignment { get; init; }

        /// <summary>The RESOLVED <c>text-pitch-alignment</c> / <c>icon-pitch-alignment</c>
        /// (<see cref="AlignmentResolution.ResolvePitch"/>), not the raw layout value: <c>SymbolFeatureExtractor</c>
        /// collapses the <c>auto</c> chain once, where <c>symbol-placement</c> is in hand. It flows through
        /// <c>ShapedSymbol.PitchAlignment</c> to the curved staging input, whose <c>Map</c> branch lays out in
        /// world metres.</summary>
        public AlignmentMode PitchAlignment { get; init; }

        /// <summary>
        /// Distinguishes a text symbol from an icon symbol; default <see cref="SymbolKind.Text"/>.
        /// Non-local invariant: an icon reuses the text-named fields for its icon-* counterparts
        /// (<see cref="AnchorRender"/>, <see cref="PaddingPx"/>, <see cref="AllowOverlap"/>,
        /// <see cref="IgnorePlacement"/>, <see cref="RotationAlignment"/>, <see cref="Paint"/>.Opacity;
        /// <see cref="Placement"/> is always Point, <see cref="SortKey"/> is shared) and leaves <see cref="Text"/> null.
        /// </summary>
        public SymbolKind Kind { get; init; }

        /// <summary>The laid-out icon quad (sheet-pixel space, anchor-relative) — meaningful only when
        /// <see cref="Kind"/> is <see cref="SymbolKind.Icon"/>; default (all-zero) otherwise. This is the
        /// PADDED quad: it includes <see cref="IconSkirtPx"/> of transparent border on every side.</summary>
        public SymbolQuad IconQuad { get; init; }

        /// <summary>
        /// Baked-px width of the transparent border baked into <see cref="IconQuad"/>, per side — the value
        /// <c>IconQuadLayout.SkirtPx</c> produced for this sprite at this <c>icon-size</c>. Carried rather
        /// than re-derived downstream so the grow (in <c>IconQuadLayout.Layout</c>) and the un-grow (in
        /// <c>IconQuadLayout.ToLayoutResult</c> / <c>SymbolBox.BuildRotatedGlyph</c>) cannot drift apart.
        /// Meaningful only when <see cref="Kind"/> is <see cref="SymbolKind.Icon"/>; <c>0</c> otherwise.
        /// </summary>
        public float IconSkirtPx { get; init; }

        /// <summary><c>icon-rotate</c> in radians, converted once here at extract; positive = clockwise on screen,
        /// kept on every carrier until <c>SymbolBearing.IconRotationRadians</c> enters the staging sense. A
        /// constant offset composed on top of the icon's alignment; 0 on every text symbol and un-rotated icon.
        /// Meaningful only when <see cref="Kind"/> is <see cref="SymbolKind.Icon"/>.</summary>
        public float IconRotateRadians { get; init; }

        /// <summary>The resolved sprite name — the icon's cross-tile identity; null for text. Threaded
        /// into <see cref="ShapedSymbol"/> and folded into <see cref="CrossTileSymbolKey"/> so distinct
        /// co-located icons do not collide.</summary>
        public string IconImage { get; init; }

        /// <summary>Whether this symbol is one half of an icon+text pair — ANY such pair, centred or not —
        /// <see cref="SymbolPairRole.Owner"/> (the icon) or <see cref="SymbolPairRole.Rider"/> (the text), or
        /// <see cref="SymbolPairRole.None"/> for every ordinary symbol. A PROPOSAL: <see cref="Placement.SymbolPairing"/>
        /// resolves whether it actually holds. Default <see cref="SymbolPairRole.None"/> so every pre-pairing
        /// symbol is unaffected.</summary>
        public SymbolPairRole PairRole { get; init; }

        /// <summary>The OWNER's <see cref="FeatureIndex"/>, stamped on BOTH halves of a
        /// proposed pair so <see cref="Placement.SymbolPairing"/> can match them. Only unique within one
        /// <c>SymbolFeatureExtractor.Extract</c> call (per layer, per tile) — the resolver also matches
        /// <c>TileKey</c>/<c>MaterialIndex</c> on the downstream <see cref="ShapedSymbol"/> carrier for that
        /// reason. Meaningless when <see cref="PairRole"/> is <see cref="SymbolPairRole.None"/>.</summary>
        public int PairId { get; init; }

        /// <summary>This half may be DROPPED while its partner places — <c>icon-optional</c> on an icon
        /// symbol, <c>text-optional</c> on a text symbol (each property names the half it makes droppable). The
        /// pair still forms and still stages as ONE candidate; only the collision verdict gains per-half
        /// granularity (<see cref="Placement.SymbolCandidate.OptionalBoxMask"/>). Meaningless unless
        /// <see cref="PairRole"/> is set; default false ⇒ MapLibre's spec default, both halves required.</summary>
        public bool PairOptional { get; init; }
    }
}
