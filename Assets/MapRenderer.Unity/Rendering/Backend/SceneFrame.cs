using Unity.Mathematics;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>
    /// The per-frame scene frame the render backends place tiles relative to (S91-C, Level-2 of the two-level
    /// RTC — see <see cref="MapRenderer.Core.View.FloatingOrigin"/>). It bundles the two projection-derived
    /// quantities a backend's per-frame <c>Rebuild</c> needs so ONE camera-orbit pose works for BOTH the plane
    /// and the globe: the look-at projected into render space, and the render→look-at-local-ENU rotation.
    ///
    /// <list type="bullet">
    ///   <item><see cref="SceneOriginRender"/> = <c>projection.Project(lookAt)</c> — the render-space point the
    ///     camera orbits (Mercator: <c>(mercX, 0, mercZ)</c>; globe: the look-at's ECEF).</item>
    ///   <item><see cref="Rebase"/> = <c>transpose(projection.TangentBasisAt(lookAt))</c> — rotates a render
    ///     delta into the look-at's local ENU frame; the same rotation is applied as every tile's orientation.
    ///     Mercator's tangent basis is the identity, so <see cref="Rebase"/> is <c>float3x3.identity</c>.</item>
    /// </list>
    ///
    /// <para>A backend places each tile at <c>FloatingOrigin.TileToSceneRebased(tileOriginRender,
    /// SceneOriginRender, Rebase)</c> with orientation <c>Rebase</c>. For Mercator this reduces bit-for-bit to
    /// the pre-S91 translation-only placement (identity rebase ⇒ identity rotation). Passed by <c>in</c>
    /// (readonly struct &gt; 16 bytes) per the large-read-only-struct convention.</para>
    /// </summary>
    internal readonly struct SceneFrame
    {
        /// <summary>The look-at projected into render space — the point the camera orbits this frame.</summary>
        public readonly double3 SceneOriginRender;

        /// <summary>Render→look-at-local-ENU rotation (identity for Mercator); also each tile's orientation.</summary>
        public readonly float3x3 Rebase;

        public SceneFrame(double3 sceneOriginRender, float3x3 rebase)
        {
            SceneOriginRender = sceneOriginRender;
            Rebase            = rebase;
        }

        /// <summary>
        /// The identity-rebase frame for a planar Web-Mercator scene origin: render origin
        /// <c>(mercX, 0, mercZ)</c>, <c>Rebase = float3x3.identity</c>. This is the frame that makes the
        /// backends' rebased placement collapse to the pre-S91 <c>TileLocalToScene</c> translation.
        /// </summary>
        public static SceneFrame Mercator(double2 sceneOriginMerc)
            => new SceneFrame(new double3(sceneOriginMerc.x, 0.0, sceneOriginMerc.y), float3x3.identity);
    }
}
