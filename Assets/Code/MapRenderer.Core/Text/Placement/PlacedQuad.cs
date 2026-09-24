// BLITTABLE: this struct crosses into the Jobs boundary as a NativeArray<PlacedQuad> element, so keep it
// to blittable fields only.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// One symbol-local <see cref="SymbolQuad"/> paired with the per-symbol placement values it needs to
    /// become 4 <c>WorldBillboardVertex</c>s (<see cref="MapRenderer.Core.Text.Placement.BillboardMath.BuildWorldQuad"/>).
    /// It is one flat entry per glyph quad, with the symbol's values repeated, so the Burst stage/emit jobs
    /// are a per-element map with no per-symbol indirection.
    /// </summary>
    public struct PlacedQuad
    {
        /// <summary>The symbol-local, anchor-relative baked-px quad.</summary>
        public SymbolQuad Quad;

        /// <summary>The symbol's projected anchor, in logical screen pixels (<see cref="SymbolScreenProjection.TryProjectAnchor"/>).
        /// Write-only in production — no current reader.</summary>
        public float2 AnchorScreenPx;

        /// <summary>`text-size` in pixels — scales the baked quad by <c>TextSizePx / TextQuadLayout.OneEm</c>.</summary>
        public float TextSizePx;

        /// <summary>NDC depth carried through from staging. Write-only in production — no current reader.</summary>
        public float Depth;

        /// <summary>Per-symbol vertex color (<see cref="SymbolPaint.TextColor"/> × <see cref="SymbolPaint.Opacity"/>).</summary>
        public float4 Color;

        /// <summary>Screen-space rotation (radians) applied to the quad about its anchor — 0 for the upright/
        /// viewport billboard, the map bearing for <c>text-rotation-alignment:map</c>.</summary>
        public float RotationRadians;

        /// <summary>Curved arm: this glyph's world anchor, tile-local render space
        /// (<c>worldPoint − CurvedStageInput.TileOriginRender</c>, Level-1 RTC bake) — sampled from the
        /// world polyline at the SAME <c>(segment, t)</c> the screen arc walk placed this glyph at. Default
        /// <see cref="float3.zero"/> for a point symbol (which carries its single anchor on
        /// <see cref="CandidateEmit.AnchorLocal"/> instead).</summary>
        public float3 AnchorLocal;

        /// <summary>Curved arm: this glyph's tile-local world tangent along the line (unit-normalized, the
        /// keep-upright negation baked in) — the world-space direction <see cref="MapRenderer.Unity.Text.Placement.WorldSymbolRenderer"/>
        /// projects live to derive the on-screen rotation. Default <see cref="float3.zero"/> for a point
        /// symbol (unread there).</summary>
        public float3 Tangent;

        /// <summary>Curved arm: this glyph's unit surface normal, pre-RTC render-space DIRECTION —
        /// sampled from the world polyline's per-vertex ups at the SAME <c>(segment, t)</c>
        /// <see cref="AnchorLocal"/> was sampled at (<see cref="PolylineArcMath.SampleUp"/>). Default
        /// <see cref="float3.zero"/> for a point symbol (which carries its anchor's up on
        /// <see cref="CandidateEmit.SurfaceUp"/> instead).</summary>
        public float3 SurfaceUp;
    }
}
