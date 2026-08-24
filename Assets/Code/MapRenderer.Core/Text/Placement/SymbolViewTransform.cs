// Engine-free: no UnityEngine dependency. Pure blittable data carrier. TOP-LEVEL
// `using Unity.Mathematics;` + unqualified float4x4/double3 — this file lives in
// MapRenderer.Core.Text.Placement, where an inline `Unity.Mathematics.float4x4` would bind to a
// (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See
// SymbolScreenProjection.cs's header for the same trap.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// W3 — the four per-frame values <see cref="SymbolScreenProjection.TryProjectPoint"/> needs, carried as
    /// ONE parameter so the staging math can project an arbitrary render-space point (not just the anchors
    /// the projection job already resolved).
    ///
    /// <para><b>Why the MATRIX and not clip <c>w</c>.</b> <see cref="SymbolStagingMath.StageCurved"/>'s doc
    /// records that the exact world→screen parameter needs the endpoint clip <c>w</c>s, which the staging
    /// inputs do not carry. W3 needs something strictly stronger — the exact projection of a point that is
    /// OFF the polyline (a glyph's corner) — and a <c>w</c> per path vertex cannot produce that at all,
    /// while the view transform produces both it and <c>w</c> at any point. All four values are already
    /// local at the one call site (<c>SymbolPlacementSystem.TickCore</c>) and already flow into the projection
    /// pass beside it, so this carries no new frame state.</para>
    ///
    /// <para><b>The default value is a REACHABLE state and it means "no camera was supplied".</b> A
    /// default-constructed transform has a zero viewport, which <see cref="IsUsable"/> reports as false and
    /// every consumer must degrade on — see its doc. This mirrors W1's <c>MetresPerLogicalPixel &gt; 0</c>
    /// degradation guard and, before it, <see cref="AlignmentMode"/>'s zero value being <c>Auto</c>: a
    /// hand-built staging fixture that never heard of this type keeps the pre-W3 behaviour by construction
    /// rather than by anyone remembering to update it.</para>
    ///
    /// <para><c>readonly struct</c> so it is passed <c>in</c> without a defensive copy (the convention's
    /// <c>in ⟺ readonly struct</c> gate), and blittable throughout so it is legal as a Burst job field.</para>
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
        /// non-positive one identifies the default-constructed value — the state every hand-built staging
        /// fixture carries, which must reproduce the pre-W3 screen-space box EXACTLY rather than project
        /// through a zero matrix.
        /// <para>Defence in depth, not the sole guard: a zero <see cref="ViewProj"/> also makes every
        /// projected <c>clip.w</c> zero, which <see cref="SymbolScreenProjection.TryProjectPoint"/> rejects on
        /// its own. Checked first anyway, because "no camera" is a statement about the INPUT and reading it
        /// off a downstream numerical accident is how a guard rots.</para>
        /// </summary>
        public bool IsUsable => ViewportLogicalPx.x > 0.0 && ViewportLogicalPx.y > 0.0;
    }
}
