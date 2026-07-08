// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float4x4 —
// this file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float4x4` would bind
// to a (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234) since the
// leading `Unity` segment resolves against the CURRENT namespace first. See the S19/S20 namespace-
// collision trap in docs/lessons-learned.md / GlyphAtlasTexture.cs.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// S20 T2: geo → render (floating-origin rebase) → screen, with culling — pure static, Burst-inlinable.
    /// Reproduces <c>Camera.WorldToScreenPoint(local)</c> given the SAME view-projection matrix and a
    /// floating-origin-rebased local position (mirrors the tile placement math: subtract
    /// <c>SceneFrame.SceneOriginRender</c> BEFORE any camera transform — S06/S91).
    /// </summary>
    public static class LabelScreenProjection
    {
        /// <summary>
        /// A label anchor further than this many logical pixels outside the viewport on any edge is culled
        /// (a generous fixed margin — Slice 1 has no collision box yet to size an exact margin from;
        /// Slice 2's per-label AABB replaces this with a precise bound). Internal, not a call-site
        /// parameter, so <see cref="TryProjectAnchor"/>'s signature matches the S20 plan exactly.
        /// </summary>
        private const float ViewportMarginPx = 256f;

        /// <summary>
        /// Projects <paramref name="renderPos"/> (render-space, pre-RTC — the SAME space
        /// <c>projection.Project(geo)</c> emits) to a logical screen pixel, or returns <c>false</c> if the
        /// anchor is behind the camera (<c>clip.w &lt;= 0</c>) or (with a fixed margin) outside the
        /// viewport. <paramref name="sceneOriginRender"/> is the per-frame floating-origin rebase
        /// (<c>SceneFrame.SceneOriginRender</c>) — MANDATORY: skipping it lands at the wrong pixel once the
        /// camera has panned away from the render origin (T2's decisive tooth).
        /// </summary>
        /// <param name="renderPos">The label anchor in render space (pre-RTC).</param>
        /// <param name="sceneOriginRender">The per-frame scene origin the camera orbits (<c>SceneFrame.SceneOriginRender</c>).</param>
        /// <param name="viewProj">The combined view-projection matrix for the CURRENT frame (<c>projectionMatrix * worldToCameraMatrix</c>).</param>
        /// <param name="viewportLogicalPx">The logical (DPR-normalized) viewport size in pixels.</param>
        /// <param name="screenPx">The projected logical screen pixel (y-up, origin bottom-left) — valid only when this method returns <c>true</c>.</param>
        /// <param name="depth">NDC depth (<c>clip.z / clip.w</c>) — valid only when this method returns <c>true</c>.</param>
        public static bool TryProjectAnchor(
            in double3 renderPos,
            in double3 sceneOriginRender,
            in float4x4 viewProj,
            in double2 viewportLogicalPx,
            out float2 screenPx,
            out float depth)
        {
            // Manual double->float per-component narrowing (not an explicit double3->float3 cast): mirrors
            // FloatingOrigin.TileToSceneRebased, which does the same field-by-field for the same reason —
            // keeps this file compiling identically against the core-tests shim's minimal float2/float4.
            double3 local = renderPos - sceneOriginRender;
            float4 clip = math.mul(viewProj, new float4((float)local.x, (float)local.y, (float)local.z, 1f));

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

            if (screenPx.x < -ViewportMarginPx || screenPx.x > viewportX + ViewportMarginPx ||
                screenPx.y < -ViewportMarginPx || screenPx.y > viewportY + ViewportMarginPx)
            {
                return false; // fully outside the viewport (+ margin)
            }

            return true;
        }
    }
}
