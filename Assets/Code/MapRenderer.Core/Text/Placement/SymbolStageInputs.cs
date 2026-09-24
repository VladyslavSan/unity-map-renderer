// TOP-LEVEL `using Unity.Mathematics;` (see SymbolScreenProjection for the namespace-collision trap).
// BLITTABLE: value-only fields, so a producer stores these as SoA and a Burst job reads them directly.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The per-symbol staging input for a POINT symbol — every value <see cref="SymbolStagingMath.StagePoint"/> needs
    /// beyond this frame's projected anchor + the symbol's glyph quads. Stable per symbol except
    /// <see cref="ScreenPx"/>/<see cref="Depth"/>/<see cref="Projected"/> (this frame's projection) and
    /// <see cref="WasPlacedLastFrame"/> (last frame's collision result); <see cref="Color"/> and
    /// <see cref="FadeId"/> are pre-resolved by the caller (managed sRGB→linear / string hash).
    /// </summary>
    public struct PointStageInput
    {
        public float2 ScreenPx;                     // this frame's projected anchor (logical px)
        public float  Depth;                        // NDC depth carried to the quads
        public bool   Projected;                    // false = behind the camera (skip)

        /// <summary>The unit surface normal at this anchor, from <c>IProjection.ProjectPoint(...).Up</c>.
        /// PER-FRAME PATCHED by <see cref="StageJob"/> — like <see cref="ScreenPx"/>/<see cref="Depth"/>/
        /// <see cref="Projected"/> — NOT a stable baked field (the baker/<c>BuildPointInput</c> never sets it).
        /// Written, but not yet consumed by any placement math.</summary>
        public float3 SurfaceUp;

        public float2 BoundsMin, BoundsMax;         // block bbox (baked-px, anchor-relative)
        public float  TextSizePx, PaddingPx, SortKey;
        public int    FeatureIndex;
        public long   TileKey;
        public int    Slot;                         // pre-clamped material/mesh slot

        /// <summary>The Level-1 RTC bake for the world-anchored draw path —
        /// <c>(float3)(AnchorRender − TileOriginRender)</c>, computed ONCE against the SAME
        /// <see cref="TileOriginRender"/> the world renderer places its presenter with (so the two RTC
        /// terms cancel exactly). Read only by <see cref="SymbolStagingMath.StagePoint"/>'s world-carry —
        /// NOT used by screen projection/collision, which stay on <see cref="ScreenPx"/>.</summary>
        public float3 AnchorLocal;

        /// <summary>The render-space tile origin
        /// <see cref="AnchorLocal"/> was baked against — resolved null-safe from <c>TileKey</c> alone (NEVER
        /// the coverage <c>tileIndex</c>, which is -1 in the demo/test seam). Carried
        /// to <see cref="CandidateEmit.TileOriginRender"/> so the world renderer can place its presenter
        /// without indexing a batch tile array.</summary>
        public double3 TileOriginRender;
        public bool   AllowOverlap, IgnorePlacement;
        public float2 TranslatePx;
        public TextTranslateAnchor TranslateAnchor;
        public AlignmentMode       RotationAlignment;
        public float4 Color;                        // pre-linearized × opacity
        // text-halo-*, per feature. HaloColor is pre-linearized; .w is the halo's own alpha (emit applies
        // text-opacity). Width/blur stay LOGICAL px and scale at emit, so a dpr change needs no re-bake.
        public float4 HaloColor;
        public float  HaloWidthPx;
        public float  HaloBlurPx;
        public long   FadeId;                       // point fade identity (pre-hashed)
        public bool   WasPlacedLastFrame;           // incumbency (pre-resolved)

        /// <summary>The texture the staged quads sample from (glyph atlas vs. sprite atlas), threaded
        /// from <see cref="ShapedSymbol.Kind"/>. Default <see cref="SymbolKind.Text"/> (blittable enum,
        /// zero value, crosses the Burst boundary like every other field here).</summary>
        public SymbolKind AtlasKind;

        /// <summary>Threaded from <see cref="ShapedSymbol.PairRole"/> — ALREADY
        /// resolved by the baker/oracle (<see cref="SymbolPairing"/>). The stage job needs nothing else: a
        /// paired owner's rider is the NEXT point record (the reconciler emits owner→rider adjacently, the
        /// gather compacts point records in winner order). Default <see cref="SymbolPairRole.None"/>.</summary>
        public SymbolPairRole PairRole;

        /// <summary>Threaded from <see cref="ShapedSymbol.PairOptional"/>: this half may be dropped
        /// while its partner places (<c>icon-optional</c> on the owner, <c>text-optional</c> on the rider).
        /// <see cref="SymbolStagingMath.StagePointPair"/> folds the two halves' values into the candidate's
        /// <see cref="SymbolCandidate.OptionalBoxMask"/>. Default false ⇒ the spec default (both required).</summary>
        public bool PairOptional;

        /// <summary><c>icon-rotate</c> in radians, in MapLibre's own sense (positive = clockwise on
        /// screen) — <see cref="SymbolBearing.IconRotationRadians"/> converts it into the staging frame's
        /// opposite sense as it is added to the alignment's own billboard rotation. Default 0 ⇒ an exact
        /// <c>x + 0f</c> for every text symbol and every un-rotated icon.</summary>
        public float IconRotateRadians;
    }

    /// <summary>
    /// The per-symbol staging input for a CURVED along-line symbol — the scalars
    /// <see cref="SymbolStagingMath.StageCurved"/> needs beyond this frame's projected path, its glyphs/anchors,
    /// and the pre-resolved per-anchor fade id + incumbency spans; the caller pre-resolves <see cref="Color"/>
    /// and <see cref="Slot"/>. Stable per symbol except <see cref="MetresPerLogicalPixel"/>, which
    /// <see cref="StageJob"/> patches per frame, as it does <see cref="PointStageInput.ScreenPx"/>.
    /// </summary>
    public struct CurvedStageInput
    {
        public float  TextSizePx, PaddingPx, SortKey;
        public int    FeatureIndex;
        public long   TileKey;
        public int    Slot;                         // pre-clamped material/mesh slot
        public bool   AllowOverlap, IgnorePlacement;
        public float2 TranslatePx;
        public TextTranslateAnchor TranslateAnchor;
        public float  MaxAngleDeg;                  // text-max-angle
        public bool   KeepUpright;                  // text-keep-upright
        public float4 Color;                        // pre-linearized × opacity
        // text-halo-*, per feature. HaloColor is pre-linearized; .w is the halo's own alpha (emit applies
        // text-opacity). Width/blur stay LOGICAL px and scale at emit, so a dpr change needs no re-bake.
        public float4 HaloColor;
        public float  HaloWidthPx;
        public float  HaloBlurPx;

        /// <summary>The render-space tile origin this symbol's per-glyph
        /// <see cref="PlacedQuad.AnchorLocal"/> bakes are baked against — resolved via the SAME null-safe
        /// <c>ResolveTileOrigin</c> helper <see cref="PointStageInput.TileOriginRender"/> uses, so the bake
        /// and the world renderer's per-tile placement cancel exactly.</summary>
        public double3 TileOriginRender;

        /// <summary>The mirror of <see cref="PointStageInput.AtlasKind"/>: the texture this curved
        /// symbol's staged quads sample from. Default <see cref="SymbolKind.Text"/> (blittable enum, zero
        /// value), so every curved TEXT symbol is unchanged; a map-aligned line icon carries
        /// <see cref="SymbolKind.Icon"/> and routes to the sprite sheet instead of the glyph atlas.</summary>
        public SymbolKind AtlasKind;

        /// <summary><c>icon-rotate</c> in radians, positive = clockwise on screen, converted like
        /// <see cref="PointStageInput.IconRotateRadians"/>. It rides out on
        /// <see cref="CandidateEmit.ExtraRotationRadians"/>, because the renderer forces the along-line
        /// <see cref="PlacedQuad.RotationRadians"/> to 0. 0 for curved text.</summary>
        public float IconRotateRadians;

        /// <summary>
        /// The RESOLVED <c>*-pitch-alignment</c> (<see cref="AlignmentResolution.ResolvePitch"/>):
        /// <see cref="AlignmentMode.Map"/> selects <see cref="SymbolStagingMath.StageCurved"/>'s world-metre arc
        /// walk, anything else the screen-px walk. Production never stores <c>Auto</c>; a hand-built fixture
        /// that leaves the zero value gets the screen walk.
        /// </summary>
        public AlignmentMode PitchAlignment;

        /// <summary>
        /// This frame's world ruler: METRES per LOGICAL screen pixel at the camera's reference depth
        /// (<c>MapCamera.MetresPerDevicePixel × MapCamera.DevicePixelRatio</c>, combined once in
        /// <c>SymbolPlacementSystem.Tick</c>), because screen positions, <see cref="TextSizePx"/> and
        /// <see cref="CurvedGlyph.ArcCenter"/> are logical px. <see cref="StageJob"/> patches it per frame; only
        /// the <see cref="AlignmentMode.Map"/> branch reads it, and 0 degrades that branch to the screen walk.
        /// </summary>
        public float MetresPerLogicalPixel;
    }
}
