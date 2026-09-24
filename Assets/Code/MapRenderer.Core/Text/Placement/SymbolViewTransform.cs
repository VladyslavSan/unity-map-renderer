// TOP-LEVEL `using Unity.Mathematics;`: inside this namespace an inline `Unity.Mathematics.float4x4` binds
// to a nonexistent nested namespace (CS0234; see SymbolScreenProjection).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The four per-frame values <see cref="SymbolScreenProjection.TryProjectPoint"/> needs, as ONE blittable
    /// <c>readonly struct</c>, so the staging math can project any render-space point. Non-obvious why: the
    /// projected collision box needs points off the polyline (glyph corners), which a per-vertex clip <c>w</c>
    /// cannot give. The default value means "no camera": <see cref="IsUsable"/> is false and consumers fall back
    /// to the screen-space box, so a hand-built staging fixture needs no update.
    /// </summary>
    public readonly struct SymbolViewTransform
    {
        /// <summary>The per-frame floating-origin rebase (<c>SceneFrame.SceneOriginRender</c>) — subtracted
        /// from a render-space position BEFORE any camera transform.</summary>
        public double3 SceneOriginRender { get; init; }

        /// <summary>The per-frame render→look-at-ENU rotation (<c>SceneFrame.Rebase</c>, identity on
        /// Mercator) — applied AFTER the double subtract and float-narrow.</summary>
        public float3x3 Rebase { get; init; }

        /// <summary>This frame's combined view-projection matrix
        /// (<c>projectionMatrix * worldToCameraMatrix</c>).</summary>
        public float4x4 ViewProj { get; init; }

        /// <summary>The logical (DPR-normalized) viewport size in pixels.</summary>
        public double2 ViewportLogicalPx { get; init; }

        /// <summary>
        /// Whether this transform came from a real frame. A camera always has a positive viewport, so a
        /// non-positive one is the default value, which must get the screen-space box
        /// (<see cref="SymbolBox.BuildRotatedGlyph"/>) rather than project through a zero matrix. A zero
        /// <c>clip.w</c> is also rejected downstream, but this checks the input itself.
        /// </summary>
        public bool IsUsable => ViewportLogicalPx.x > 0.0 && ViewportLogicalPx.y > 0.0;
    }
}
