// Engine-free. TOP-LEVEL `using Unity.Mathematics;` + unqualified float4x4 — this file lives in
// MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float4x4` would bind to a (nonexistent)
// `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234) since the leading `Unity` segment
// resolves against the CURRENT namespace first. See the namespace-collision trap in GlyphAtlasTexture.cs.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Geo → render (floating-origin rebase) → screen, with culling — pure static, Burst-inlinable.
    /// Reproduces <c>Camera.WorldToScreenPoint(local)</c> given the SAME view-projection matrix and a
    /// floating-origin-rebased local position (mirrors the tile placement math: subtract
    /// <c>SceneFrame.SceneOriginRender</c> BEFORE any camera transform).
    /// </summary>
    public static class SymbolScreenProjection
    {
        /// <summary>
        /// A symbol anchor further than this many logical pixels outside the viewport on any edge is culled
        /// (a generous fixed margin, not an exact per-symbol bound). Internal, not a call-site parameter.
        /// </summary>
        private const float ViewportMarginPx = 256f;

        /// <summary>Magnitude past which a projected screen coordinate is treated as a near-plane blow-up
        /// rather than a position. A point just in front of the camera plane has a tiny positive <c>clip.w</c>,
        /// which survives the behind-camera test in <see cref="TryProjectPoint"/> and then divides into an
        /// arbitrarily large screen coordinate — finite, so no NaN guard catches it, but useless as geometry.
        /// <para>Lives here, not on a caller, because BOTH consumers of a projected point need the same
        /// threshold: <c>SymbolStagingMath.StageCurved</c> bounds a path VERTEX (a blow-up there would explode
        /// the arc walk), and <see cref="SymbolBox.TryBuildProjectedWorldGlyph"/> bounds a projected CORNER (a
        /// blow-up there would make the collision AABB unbounded, where <see cref="SymbolBox.BuildRotatedGlyph"/>'s
        /// screen box is bounded by the cell). Two copies of one threshold is how they drift apart.</para></summary>
        internal const float MaxProjectedPx = 1e5f;

        /// <summary>
        /// Projects <paramref name="renderPos"/> (render-space, pre-RTC — the SAME space
        /// <c>projection.Project(geo)</c> emits) to a logical screen pixel, or returns <c>false</c> if the
        /// anchor is behind the camera (<c>clip.w &lt;= 0</c>) or (with a fixed margin) outside the
        /// viewport. <paramref name="sceneOriginRender"/> is the per-frame floating-origin rebase
        /// (<c>SceneFrame.SceneOriginRender</c>) — MANDATORY: skipping it lands at the wrong pixel once the
        /// camera has panned away from the render origin.
        /// </summary>
        /// <param name="renderPos">The symbol anchor in render space (pre-RTC).</param>
        /// <param name="sceneOriginRender">The per-frame scene origin the camera orbits (<c>SceneFrame.SceneOriginRender</c>).</param>
        /// <param name="viewProj">The combined view-projection matrix for the CURRENT frame (<c>projectionMatrix * worldToCameraMatrix</c>).</param>
        /// <param name="viewportLogicalPx">The logical (DPR-normalized) viewport size in pixels.</param>
        /// <param name="rebase">The per-frame render→look-at-ENU rotation (<c>SceneFrame.Rebase</c>, identity on
        /// Mercator) — applied AFTER the double subtract (mirrors <c>FloatingOrigin.TileToSceneRebased</c>).</param>
        /// <param name="screenPx">The projected logical screen pixel (y-up, origin bottom-left) — valid only when this method returns <c>true</c>.</param>
        /// <param name="depth">NDC depth (<c>clip.z / clip.w</c>) — valid only when this method returns <c>true</c>.</param>
        public static bool TryProjectAnchor(
            in double3 renderPos,
            in double3 sceneOriginRender,
            in float4x4 viewProj,
            in double2 viewportLogicalPx,
            in float3x3 rebase,
            out float2 screenPx,
            out float depth)
        {
            if (!TryProjectPoint(renderPos, sceneOriginRender, viewProj, viewportLogicalPx, rebase, out screenPx, out depth))
                return false; // behind the camera

            return IsWithinViewportMargin(screenPx, viewportLogicalPx); // fully outside the viewport (+ margin)?
        }

        /// <summary>
        /// True if <paramref name="screenPx"/> lies within the viewport plus the fixed <see cref="ViewportMarginPx"/>
        /// margin — the point-anchor viewport cull, factored OUT of <see cref="TryProjectAnchor"/> so the
        /// projection job can do pure (behind-camera-only) projection while the serial staging pass applies this
        /// cheap screen-bounds cull. <see cref="TryProjectAnchor"/> = <see cref="TryProjectPoint"/> + this, so the
        /// two paths stay bit-identical.
        /// </summary>
        public static bool IsWithinViewportMargin(in float2 screenPx, in double2 viewportLogicalPx)
        {
            float viewportX = (float)viewportLogicalPx.x;
            float viewportY = (float)viewportLogicalPx.y;
            return screenPx.x >= -ViewportMarginPx && screenPx.x <= viewportX + ViewportMarginPx &&
                   screenPx.y >= -ViewportMarginPx && screenPx.y <= viewportY + ViewportMarginPx;
        }

        /// <summary>
        /// Projects <paramref name="renderPos"/> to a logical screen pixel, culling ONLY behind-camera
        /// (<c>clip.w &lt;= 0</c>) — no viewport-margin cull. Used by the curved line-text path: a line
        /// vertex may be far off-screen while the symbol's visible portion is on-screen, so the margin cull
        /// (correct for a point anchor) would wrongly drop the whole line.
        /// </summary>
        /// <param name="rebase">The per-frame render→look-at-ENU rotation (<c>SceneFrame.Rebase</c>, identity on
        /// Mercator). Applied AFTER the double subtract + float-narrow (mirrors
        /// <c>FloatingOrigin.TileToSceneRebased</c> — rebasing before the subtract would rotate full-scale world
        /// coordinates and blow the float32 precision budget).</param>
        public static bool TryProjectPoint(
            in double3 renderPos,
            in double3 sceneOriginRender,
            in float4x4 viewProj,
            in double2 viewportLogicalPx,
            in float3x3 rebase,
            out float2 screenPx,
            out float depth)
        {
            // Manual double->float per-component narrowing (not an explicit double3->float3 cast): mirrors
            // FloatingOrigin.TileToSceneRebased, which does the same field-by-field for the same reason —
            // keeps this file compiling identically against the core-tests shim's minimal float2/float4.
            double3 local = renderPos - sceneOriginRender;
            float3 localF = new float3((float)local.x, (float)local.y, (float)local.z);
            float3 rebased = math.mul(rebase, localF); // rotate into the look-at ENU frame (identity on Mercator)
            float4 clip = math.mul(viewProj, new float4(rebased, 1f));

            if (clip.w <= 0f)
            {
                screenPx = float2.zero;
                depth = 0f;
                return false; // behind the camera
            }

            float ndcX = clip.x / clip.w;
            float ndcY = clip.y / clip.w;
            float ndcZ = clip.z / clip.w;
            float viewportX = (float)viewportLogicalPx.x;
            float viewportY = (float)viewportLogicalPx.y;

            screenPx = new float2((ndcX * 0.5f + 0.5f) * viewportX, (ndcY * 0.5f + 0.5f) * viewportY);
            depth = ndcZ;
            return true;
        }
    }
}
