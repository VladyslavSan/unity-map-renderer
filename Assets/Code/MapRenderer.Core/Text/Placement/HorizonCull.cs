// TOP-LEVEL `using Unity.Mathematics;` + unqualified double3/float3x3: an inline qualification hits a
// namespace collision inside MapRenderer.Core.Text.Placement (see SymbolScreenProjection's header).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The globe far-side horizon cull, a fourth <c>GatherSymbolPoints</c> fade-out trigger (peer of the
    /// tile/distance/departing culls) — a symbol anchor hidden behind the earth's own bulk must FADE OUT, not pop,
    /// so it is a gather-time predicate, never a hard drop inside <c>SymbolProjectionJob</c>.
    /// </summary>
    public static class HorizonCull
    {
        /// <summary>
        /// True when <paramref name="repAnchorRender"/> is on the far side of the horizon plane from the camera:
        /// <c>dot(P − centre, cam − centre) &lt; radius²</c>. A polar-plane test, EXACT for on-surface points, not
        /// an occlusion volume (a point inside the sphere on the near side reads visible). It is the
        /// <c>rad = 0</c> case of <c>FrustumTileSelector.TileVisible</c>'s occlusion test
        /// (cull iff <c>centreDot + rad·dc &lt; r²</c>).
        /// </summary>
        /// <param name="repAnchorRender">The symbol's representative anchor, PRE-RTC render space (the space
        /// <c>projection.Project(geo)</c> emits) — <c>SymbolBatch.RepAnchor</c>.</param>
        /// <param name="sceneOriginRender">The per-frame floating-origin rebase (<c>SceneFrame.SceneOriginRender</c>).</param>
        /// <param name="rebase">The per-frame render→look-at-ENU rotation (<c>SceneFrame.Rebase</c>) — applied
        /// AFTER the double subtract, mirroring <c>SymbolScreenProjection.TryProjectPoint</c>'s narrowing so the
        /// cull decision agrees with where the symbol actually projects.</param>
        /// <param name="cameraRelative">The camera's position relative to the floating origin
        /// (<c>SceneFrame.CameraRelativePosition</c> / <c>CameraPoseMath.ComputeRelativePose</c>'s <c>pos</c>) —
        /// already expressed in the rebased look-at frame, no further rebase.</param>
        /// <param name="globeCentreRelative">The occluding sphere's centre, relative to the floating origin
        /// (<c>IProjection.TryGetHorizonOccluder</c>'s <c>renderCentre</c>) — already in the rebased frame.</param>
        /// <param name="globeRadiusSq">The occluding sphere's radius squared; negative ⇒ planar projection (no
        /// horizon) ⇒ this method always returns <c>false</c> with no state read.</param>
        public static bool IsHiddenBeyondHorizon(
            in double3  repAnchorRender,
            in double3  sceneOriginRender,
            in float3x3 rebase,
            in double3  cameraRelative,
            in double3  globeCentreRelative,
            double      globeRadiusSq)
        {
            if (globeRadiusSq < 0.0) return false; // planar: no flag, no state

            // Rebase the anchor into the look-at ENU frame — mirrors SymbolScreenProjection.TryProjectPoint's
            // double-subtract → float-narrow → mul(rebase) seam, so the cull agrees with where the symbol projects.
            double3 local   = repAnchorRender - sceneOriginRender;
            float3  localF  = new float3((float)local.x, (float)local.y, (float)local.z);
            float3  rebased = math.mul(rebase, localF);
            double3 p       = new double3(rebased.x, rebased.y, rebased.z);

            // DOUBLE-precision polar-plane test: p and cam on the same side of the plane through the horizon
            // circle, perpendicular to (cam − centre).
            double3 pc = p - globeCentreRelative;
            double3 cc = cameraRelative - globeCentreRelative;
            return math.dot(pc, cc) < globeRadiusSq;
        }
    }
}
