// TOP-LEVEL `using Unity.Mathematics;` + unqualified types (see SymbolScreenProjection's header for the
// inline-qualification trap this avoids).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The PRE-projection symbol far-distance cull: in a tilted view, symbols near the horizon pile up, lose
    /// collision and jitter, so a symbol farther from the CAMERA than the cull distance skips staging. The caller
    /// passes the distance as a fraction of the camera's far clip. The test is VIEW DEPTH, not straight-line
    /// distance — Unity's <c>farClipPlane</c> bounds view depth, so this matches the GPU clip (a straight-line
    /// radius shrank to a small disc at tilt 0). Valid for plane and globe; the globe also has <see cref="HorizonCull"/>.
    /// </summary>
    public static class SymbolFarPlaneCull
    {
        /// <summary>
        /// True when <paramref name="repAnchorRender"/>'s view depth exceeds <paramref name="cullDistanceMeters"/>
        /// and should be skipped before projection. Forward is <c>-cameraRelative</c> unnormalized
        /// (<c>CameraPoseMath.ComputeRelativePose</c>'s <c>fwd = -pos/|pos|</c> invariant), scaling the comparison
        /// by <c>|cameraRelative|</c> instead — one <c>sqrt</c>, not two. A camera AT the look-at (zero) keeps
        /// everything, the same fallback as a non-positive distance.
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
            double3 toLookAt = -cameraRelative; // ComputeRelativePose's fwd, unnormalized (fwd = toLookAt / |toLookAt|)
            return math.dot(d, toLookAt) > cullDistanceMeters * math.length(toLookAt);
        }
    }
}
