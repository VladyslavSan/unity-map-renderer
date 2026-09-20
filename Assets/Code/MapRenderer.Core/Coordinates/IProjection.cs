// Engine-free: no UnityEngine dependency. Implemented by stateless structs — the geometry side runs inside
// Burst (as a generic type parameter); the camera side runs managed (through the boxed interface).

using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// The single projection abstraction. Implemented by a
    /// STATELESS <b>struct</b> per projection (<see cref="WebMercatorProjection"/> : planar;
    /// <see cref="SphericalProjection"/> : globe) so one type serves BOTH sides with no parallel surface:
    /// <list type="bullet">
    ///   <item><b>Geometry</b> (build side): <see cref="ProjectPoint"/> (position + up), plus the
    ///     <see cref="Project"/> / <see cref="UpAt"/> / <see cref="MetersPerUnit"/> conveniences. The Burst
    ///     projection job <c>ProjectPointsJob&lt;TProj&gt;</c> takes the concrete struct as a generic type
    ///     parameter (<c>where TProj : struct, IProjection</c>) and calls <see cref="ProjectPoint"/> — Burst
    ///     devirtualises + inlines it (no boxing, no enum). The struct IS the discriminator.</item>
    ///   <item><b>Camera interaction</b> (managed side): <see cref="ScreenToGround"/> /
    ///     <see cref="GroundToScreen"/> / <see cref="ClampValidLatitude"/> — screen↔ground given the
    ///     viewport + camera pose. Used through the boxed interface (one box per session); the camera only
    ///     needs these few things from a projection.</item>
    /// </list>
    /// </summary>
    public interface IProjection
    {
        // ── Geometry (build side; docs §6) ────────────────────────────────────────────────────────

        /// <summary>The projection kernel: a geodetic SURFACE point (no elevation) → render-space position
        /// (pre-RTC; east=+X, up=+Y, north=+Z, docs §7) + the surface up. The single shared math — called from
        /// Burst via the generic <c>ProjectPointsJob&lt;TProj&gt;</c> constraint AND from the OOP conveniences
        /// below. Input is <see cref="GeoCoordinate"/> (2D): today's geometry is on the datum surface. Elevated
        /// geometry (<see cref="GeoCoordinate3D"/> with altitude) is a future, more-complex path — out of scope
        /// until there is elevation data.</summary>
        ProjectedPoint ProjectPoint(in GeoCoordinate geo);

        /// <summary>Projects a geodetic surface point to render space (pre-RTC). Forwards to <see cref="ProjectPoint"/>.</summary>
        double3 Project(in GeoCoordinate geo);

        /// <summary>The local up (surface normal) at a geodetic point — extrusion + lighting direction. Constant +Y for the planar Mercator; the geodetic normal for a globe.</summary>
        double3 UpAt(in GeoCoordinate geo);

        /// <summary>The render-space ENU tangent basis at a geodetic point (columns c0=east, c1=up, c2=north). Constant identity for the planar Mercator; the local ENU frame for a globe. Used for camera-relative rendering — the scene rebases tiles into the look-at's frame (its transpose), so the same camera pose works for both projections.</summary>
        float3x3 TangentBasisAt(in GeoCoordinate geo);

        /// <summary>Render-space metres per world unit (scale bookkeeping). 1.0 when render units are metres.</summary>
        double MetersPerUnit { get; }

        // ── Render-space geometry the universal tile selector needs (planar vs globe) ───────────────

        /// <summary>If the projected surface self-occludes (a closed convex body — the globe), outputs the
        /// occluder sphere in the LOOK-AT render frame (look-at at the origin) and returns true; the tile-cover
        /// then drops tiles whose bounding sphere is entirely beyond that sphere's horizon (back-face culling in
        /// the selector). A planar projection does not self-occlude and returns false.</summary>
        bool TryGetHorizonOccluder(out double3 renderCentre, out double radius);

        /// <summary>The projection's <b>subdivision policy</b>: the maximum surface-normal rotation (radians)
        /// one primitive edge may subtend before the build side must split it, so a straight chord never sags
        /// visibly off a curved surface. The build side always runs subdivision driven by this value — no
        /// capability flag gates it: a flat projection returns <see cref="double.PositiveInfinity"/>, so the
        /// split count falls out to zero and the flat case is the degenerate value, not a separate branch.
        /// <para>Planar Mercator: <see cref="double.PositiveInfinity"/> (a flat sheet never subdivides). Globe
        /// (ECEF sphere): a small tolerance (~2°). This replaces the former curvature/handedness capability
        /// flags: winding is now derived by construction (line <c>across = cross(along, up)</c> ties
        /// the ribbon to the same <c>up</c> its centerline was projected with — one frame, correct for every
        /// projection — so no per-projection winding fact is needed; see <c>GlobeLineWindingTests</c> /
        /// <c>docs §7.1</c>).</para></summary>
        double MaxRefineAngleRad { get; }

        // ── Camera interaction (managed side) ───────────────────────────────────────────────────────

        /// <summary>Returns the geodetic ground point under the given screen pixel. Screen convention: +x right, +y up, origin bottom-left. Overhead-correct now; the tilted ray-cast is a future internal upgrade of this method.</summary>
        GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera);

        /// <summary>Returns the screen pixel for the given geodetic ground point. Exact inverse of ScreenToGround at zero tilt.</summary>
        double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera);

        /// <summary>Clamps a latitude to the valid geodetic range for this projection.</summary>
        double ClampValidLatitude(double latitudeDegrees);

        /// <summary>True if the projection maps the world onto a FINITE planar sheet with hard edges (Web
        /// Mercator): the camera then clamps pan/zoom so the viewport never leaves the sheet, and the
        /// min-zoom floor FILLS the viewport. False for a cyclic/closed world (the globe wraps — no edges
        /// to clamp, min-zoom FITS with margin). Distinct from <see cref="TryGetHorizonOccluder"/>
        /// (self-occlusion) and <see cref="MaxRefineAngleRad"/> (subdivision).</summary>
        bool IsFinitePlanarWorld { get; }

        /// <summary>Clamps <c>camera.LookAt</c> so the visible viewport stays within the finite world
        /// sheet. Cyclic projections return <c>camera.LookAt</c> unchanged. v1 is heading/tilt-conservative:
        /// it clamps by the axis-aligned viewport half-span (<c>vp·0.5·metresPerPixel(zoom)</c>); on the axis
        /// where the world exactly fills the viewport (min-zoom floor) the range collapses and the look-at
        /// locks to world-centre.</summary>
        GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in CameraProperties camera);
    }
}
