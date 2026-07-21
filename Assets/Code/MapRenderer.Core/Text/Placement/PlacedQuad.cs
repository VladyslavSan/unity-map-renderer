// Engine-free: no UnityEngine dependency.
// BLITTABLE: this struct crosses into the S20 Jobs boundary as a NativeArray<PlacedQuad> element (mirrors the
// Core-defines-the-struct/Jobs-creates-the-NativeArray pattern LineRibbonVertex/GlyphAtlasEntry already use).
// Keep it to blittable fields only.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One label-local <see cref="SymbolQuad"/> paired with the per-LABEL placement values it needs to
    /// become 4 <c>WorldBillboardVertex</c>s (<see cref="MapRenderer.Core.Text.Placement.BillboardMath.BuildWorldQuad"/>)
    /// — <see cref="LabelPlacementSystem"/> expands each surviving <see cref="LabelInstance"/>'s
    /// <see cref="TextLayoutResult.Quads"/> into a flat <c>NativeArray&lt;PlacedQuad&gt;</c> (one entry per
    /// glyph quad, anchor/size/color repeated across every quad of the same label) so the Burst stage/emit
    /// jobs stay a simple per-element map — no per-label indirection.
    /// </summary>
    public struct PlacedQuad
    {
        /// <summary>The label-local, anchor-relative baked-px quad (S19).</summary>
        public SymbolQuad Quad;

        /// <summary>The label's projected anchor, in logical screen pixels (<see cref="LabelScreenProjection.TryProjectAnchor"/>).
        /// Write-only-dead in production since the screen render path retired — see <see cref="Depth"/>'s note.</summary>
        public float2 AnchorScreenPx;

        /// <summary>`text-size` in pixels — scales the baked quad by <c>TextSizePx / TextQuadLayout.OneEm</c>.</summary>
        public float TextSizePx;

        /// <summary>NDC depth carried through from staging. Write-only-dead in production after the screen
        /// render path's removal (its only reader) — kept because pruning it cascades into the live staging
        /// decision (<c>LabelStagingMath</c>/<c>LabelStageJob</c>) that still writes it; deferred, not an
        /// oversight (a small follow-up once the staging inputs are revisited).</summary>
        public float Depth;

        /// <summary>Per-label vertex color (<see cref="LabelPaint.TextColor"/> × <see cref="LabelPaint.Opacity"/>).</summary>
        public float4 Color;

        /// <summary>Screen-space rotation (radians) applied to the quad about its anchor — 0 for the upright/
        /// viewport billboard, the map bearing for <c>text-rotation-alignment:map</c> (#4).</summary>
        public float RotationRadians;

        /// <summary>Stage AC (curved-world): this glyph's world anchor, tile-local render space
        /// (<c>worldPoint − CurvedStageInput.TileOriginRender</c>, Level-1 RTC bake) — sampled from the
        /// world polyline at the SAME <c>(segment, t)</c> the screen arc walk placed this glyph at. Default
        /// <see cref="float3.zero"/> for a point label (which carries its single anchor on
        /// <see cref="CandidateEmit.AnchorLocal"/> instead).</summary>
        public float3 AnchorLocal;

        /// <summary>Stage AC: this glyph's tile-local world tangent along the line (unit-normalized, the
        /// keep-upright negation baked in) — the world-space direction <see cref="MapRenderer.Unity.Text.Placement.WorldLabelRenderer"/>
        /// projects live to derive the on-screen rotation. Default <see cref="float3.zero"/> for a point
        /// label (unread there).</summary>
        public float3 Tangent;
    }
}
