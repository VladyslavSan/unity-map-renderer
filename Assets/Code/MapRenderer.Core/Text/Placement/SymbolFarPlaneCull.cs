// Engine-free. TOP-LEVEL `using Unity.Mathematics;` + unqualified double3/float3x3 — this file lives in
// MapRenderer.Core.Text.Placement (see SymbolScreenProjection's header for the inline-qualification trap this
// avoids).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The PRE-projection symbol far-distance cull. In a tilted view the far half of the frustum compresses a huge
    /// ground area into a thin band at the horizon, so most symbols there pile up, get collision-discarded, and
    /// jitter (their projection is numerically unstable as depth → the far plane). Testing a symbol's distance from
    /// the CAMERA against a cull distance BEFORE projecting it lets the placement pass skip its whole staging
    /// (project + collision-box build) for symbols that would be discarded or unstable anyway.
    ///
    /// <para>The cull distance is supplied by the caller as a fraction of the camera's far-clip distance — the
    /// value the GPU clips at, so the cull agrees with what renders. A fraction of 1 culls only at the far
    /// distance (near-inert, since tile selection already frustum-bounds tiles by the same far); a smaller
    /// fraction pulls symbols in closer than the full frustum depth. Distance is 3-D render-space length,
    /// well-defined for the planar atlas and the globe alike (the globe gets its own tighter occlusion cull
    /// separately, <see cref="HorizonCull"/>).</para>
    ///
    /// <para>The bound is tied to the real camera geometry, so one knob behaves sensibly at every tilt and
    /// zoom. A fixed viewport-span radius around the LOOK-AT would not: at high tilt it sits several times
    /// looser than the frustum reaches, so it culls almost nothing.</para>
    /// </summary>
    public static class SymbolFarPlaneCull
    {
        /// <summary>
        /// True when <paramref name="repAnchorRender"/> is farther from the camera than
        /// <paramref name="cullDistanceMeters"/> and should be skipped before projection. A non-positive distance
        /// disables the cull (returns false for every symbol) — the safe fallback for a mis-wired caller (degrades
        /// to "cull nothing" rather than culling everything). Compares squared distances (no sqrt) via <c>dot</c>.
        /// </summary>
        /// <param name="repAnchorRender">The symbol's representative anchor, PRE-RTC render space (the space
        /// <c>projection.Project(geo)</c> emits) — <c>SymbolBatch.RepAnchor</c>.</param>
        /// <param name="sceneOriginRender">The per-frame floating-origin rebase (<c>SceneFrame.SceneOriginRender</c>).</param>
        /// <param name="rebase">The per-frame render→look-at-ENU rotation (<c>SceneFrame.Rebase</c>) — applied
        /// AFTER the double subtract, mirroring <see cref="HorizonCull"/> / <c>SymbolScreenProjection</c> so the
        /// cull decision agrees with where the symbol projects. A rotation, so it preserves the camera→anchor
        /// distance; it only puts the anchor in the SAME frame as <paramref name="cameraRelative"/>.</param>
        /// <param name="cameraRelative">The camera's position relative to the floating origin
        /// (<c>SceneFrame.CameraRelativePosition</c>) — already expressed in the rebased look-at frame, no
        /// further rebase.</param>
        /// <param name="cullDistanceMeters">The cull distance in render metres (a fraction of the far plane);
        /// non-positive disables the cull.</param>
        public static bool IsCulled(
            in double3  repAnchorRender,
            in double3  sceneOriginRender,
            in float3x3 rebase,
            in double3  cameraRelative,
            double      cullDistanceMeters)
        {
            if (cullDistanceMeters <= 0.0) return false; // non-positive → cull disabled (keep all)

            // Rebase the anchor into the look-at ENU frame — the double-subtract → float-narrow → mul(rebase) seam
            // HorizonCull / SymbolScreenProjection use, so the cull agrees with where the symbol projects. cameraRelative
            // is already in that frame, so p − cameraRelative is the true camera→anchor separation (rebase is a
            // rotation, so it does not change the length — it only aligns the two into one frame).
            double3 local   = repAnchorRender - sceneOriginRender;
            float3  localF  = new float3((float)local.x, (float)local.y, (float)local.z);
            float3  rebased = math.mul(rebase, localF);
            double3 p       = new double3(rebased.x, rebased.y, rebased.z);

            double3 d = p - cameraRelative;
            return math.dot(d, d) > cullDistanceMeters * cullDistanceMeters;
        }
    }
}
