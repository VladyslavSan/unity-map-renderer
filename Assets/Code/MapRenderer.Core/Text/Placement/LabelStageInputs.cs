// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2/4 — this
// file lives in MapRenderer.Core.Text.Placement (see PolylineArcWalker for the namespace-collision trap).
// BLITTABLE: value-only fields so a producer can store these as SoA and a Burst job (Lever C) reads them
// directly — mirrors the LabelBox / PlacedQuad / LabelCandidate blittable-struct pattern.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The per-label staging input for a POINT label — every value <see cref="LabelStagingMath.StagePoint"/> needs
    /// beyond this frame's projected anchor + the label's glyph quads. Stable per label except
    /// <see cref="ScreenPx"/>/<see cref="Depth"/>/<see cref="Projected"/> (this frame's projection) and
    /// <see cref="WasPlacedLastFrame"/> (last frame's collision result); <see cref="Color"/> and
    /// <see cref="FadeId"/> are pre-resolved by the caller (managed sRGB→linear / string hash).
    /// </summary>
    public struct PointStageInput
    {
        public float2 ScreenPx;                     // this frame's projected anchor (logical px)
        public float  Depth;                        // NDC depth carried to the quads
        public bool   Projected;                    // false = behind the camera (skip)

        public float2 BoundsMin, BoundsMax;         // S19 block bbox (baked-px, anchor-relative)
        public float  TextSizePx, PaddingPx, SortKey;
        public int    FeatureIndex;
        public long   TileKey;
        public int    Slot;                         // pre-clamped material/mesh slot

        /// <summary>Epic A / A1 (design §3.4, §11 A1 D2): the Level-1 RTC bake for the world-anchored draw
        /// path — <c>(float3)(AnchorRender − TileOriginRender)</c>, computed ONCE by
        /// the parity oracle's <c>AddPoint</c> against the SAME
        /// <see cref="TileOriginRender"/> the world renderer places its presenter with (so the two RTC
        /// terms cancel exactly). Read only by <see cref="LabelStagingMath.StagePoint"/>'s world-carry
        /// (D2/D6) — NOT used by screen projection/collision, which stay on <see cref="ScreenPx"/>.</summary>
        public float3 AnchorLocal;

        /// <summary>Epic A / A1 (design §3.4, §11 A1 D2): the render-space tile origin
        /// <see cref="AnchorLocal"/> was baked against — resolved null-safe from <c>TileKey</c> alone (NEVER
        /// the coverage <c>tileIndex</c>, which is -1 in the demo/test seam — see D2's BLOCKER note). Carried
        /// to <see cref="CandidateEmit.TileOriginRender"/> so the world renderer can place its presenter
        /// without indexing a batch tile array.</summary>
        public double3 TileOriginRender;
        public bool   AllowOverlap, IgnorePlacement;
        public float2 TranslatePx;
        public TextTranslateAnchor TranslateAnchor;
        public AlignmentMode       RotationAlignment;
        public float4 Color;                        // pre-linearized × opacity
        public long   FadeId;                       // A-4 point identity (pre-hashed)
        public bool   WasPlacedLastFrame;           // A-5 incumbency (pre-resolved)

        /// <summary>I5a — the texture the staged quads sample from (glyph atlas vs. sprite atlas), threaded
        /// from <see cref="LabelInstance.Kind"/>. Default <see cref="LabelKind.Text"/> (blittable enum,
        /// zero value, crosses the Burst boundary like every other field here). NOT YET consumed by the
        /// draw side — this is I5a's data-only thread; I5b partitions the draw by it.</summary>
        public LabelKind AtlasKind;

        /// <summary>Road-shields §10 D8/D10, threaded from <see cref="LabelInstance.PairRole"/> — ALREADY
        /// resolved by the baker/oracle (<see cref="LabelPairing"/>). The stage job needs nothing else: a
        /// paired owner's rider is the NEXT point record (the reconciler emits owner→rider adjacently, the
        /// gather compacts point records in winner order). Default <see cref="LabelPairRole.None"/>.</summary>
        public LabelPairRole PairRole;

        /// <summary>Stage C, threaded from <see cref="LabelInstance.PairOptional"/>: this half may be dropped
        /// while its partner places (<c>icon-optional</c> on the owner, <c>text-optional</c> on the rider).
        /// <see cref="LabelStagingMath.StagePointPair"/> folds the two halves' values into the candidate's
        /// <see cref="LabelCandidate.OptionalBoxMask"/>. Default false ⇒ the spec default (both required).</summary>
        public bool PairOptional;

        /// <summary>P-B <c>icon-rotate</c> in radians, in MapLibre's own sense (positive = clockwise on
        /// screen) — <see cref="LabelBearing.IconRotationRadians"/> converts it into the staging frame's
        /// opposite sense as it is added to the alignment's own billboard rotation. Default 0 ⇒ an exact
        /// <c>x + 0f</c> for every text label and every un-rotated icon.</summary>
        public float IconRotateRadians;
    }

    /// <summary>
    /// The per-label staging input for a CURVED along-line label — the scalars
    /// <see cref="LabelStagingMath.StageCurved"/> needs beyond this frame's projected path, its glyphs/anchors,
    /// and the pre-resolved per-anchor fade id + incumbency spans. <see cref="Color"/> and <see cref="Slot"/> are
    /// pre-resolved by the caller (managed sRGB→linear / slot clamp).
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

        /// <summary>Stage AC (curved-world): the render-space tile origin this label's per-glyph
        /// <see cref="PlacedQuad.AnchorLocal"/> bakes are baked against — resolved by
        /// the parity oracle's <c>AddCurved</c> via the SAME
        /// null-safe <c>ResolveTileOrigin</c> helper <see cref="PointStageInput.TileOriginRender"/> uses, so
        /// the bake and the world renderer's per-tile placement cancel exactly (§3.4).</summary>
        public double3 TileOriginRender;

        /// <summary>P-B — the mirror of <see cref="PointStageInput.AtlasKind"/>: the texture this curved
        /// label's staged quads sample from. Default <see cref="LabelKind.Text"/> (blittable enum, zero
        /// value), so every curved TEXT label is unchanged; a map-aligned line icon carries
        /// <see cref="LabelKind.Icon"/> and routes to the sprite sheet instead of the glyph atlas.</summary>
        public LabelKind AtlasKind;

        /// <summary>P-B <c>icon-rotate</c> in radians, in MapLibre's own sense (positive = clockwise on
        /// screen) — the same carrier convention as <see cref="PointStageInput.IconRotateRadians"/>, converted
        /// by the same <see cref="LabelBearing.IconRotationRadians"/>. The along-line path cannot use
        /// <see cref="PlacedQuad.RotationRadians"/> for it (the renderer forces that to 0 and lets the shader
        /// rotate by the projected tangent instead), so the converted value rides out on
        /// <see cref="CandidateEmit.ExtraRotationRadians"/>. Default 0 ⇒ curved TEXT is byte-identical.</summary>
        public float IconRotateRadians;
    }
}
