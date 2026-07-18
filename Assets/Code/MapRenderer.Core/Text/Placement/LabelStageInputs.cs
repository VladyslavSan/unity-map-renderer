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
    }
}
