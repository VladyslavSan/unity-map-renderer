// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members (see docs/conventions.md).

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
        /// <c>SceneOriginRender</c> rebase is applied later, not here).</summary>
        public double3 AnchorRender { get; init; }

        /// <summary>The resolved <c>text-field</c> string (never null/empty — empty resolutions are skipped).</summary>
        public string Text { get; init; }

        /// <summary>Evaluated <c>text-size</c> in pixels.</summary>
        public float TextSizePx { get; init; }

        /// <summary>Evaluated <c>text-padding</c> in pixels (spec default 2 when absent).</summary>
        public float PaddingPx { get; init; }

        /// <summary>Evaluated <c>symbol-sort-key</c> (greedy placement priority; lower placed first).</summary>
        public float SortKey { get; init; }

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
    }
}
