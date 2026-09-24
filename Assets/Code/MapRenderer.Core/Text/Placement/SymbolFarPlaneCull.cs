// TOP-LEVEL `using Unity.Mathematics;` + unqualified types (see SymbolScreenProjection's header for the
// inline-qualification trap this avoids).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The PRE-projection symbol far-distance cull: in a tilted view, symbols near the horizon pile up, lose
    /// collision and jitter, so a symbol farther from the CAMERA than the cull distance skips staging. The caller
    /// passes the distance as a fraction of the camera's far clip, so one knob works at every tilt and zoom (a
    /// fixed radius around the look-at would cull almost nothing at high tilt). Distance is 3-D render-space
    /// length, valid for plane and globe; the globe also has <see cref="HorizonCull"/>.
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
        /// <param name="rebase">The per-frame render→look-at-ENU rotation (<c>SceneFrame.Rebase</c>), applied after
        /// the double subtract as in <see cref="HorizonCull"/>, so cull and projection agree.</param>
        /// <param name="cameraRelative">The camera position relative to the floating origin
        /// (<c>SceneFrame.CameraRelativePosition</c>), already in the rebased look-at frame.</param>
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

            // The same subtract → narrow → rebase seam as SymbolScreenProjection; rebase is a rotation, so
            // p − cameraRelative is the true camera→anchor separation.
            double3 local   = repAnchorRender - sceneOriginRender;
            float3  localF  = new float3((float)local.x, (float)local.y, (float)local.z);
            float3  rebased = math.mul(rebase, localF);
            double3 p       = new double3(rebased.x, rebased.y, rebased.z);

            double3 d = p - cameraRelative;
            return math.dot(d, d) > cullDistanceMeters * cullDistanceMeters;
        }
    }
}
