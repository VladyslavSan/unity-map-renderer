// Engine-free: no UnityEngine dependency. Implemented by stateless structs — the geometry side runs inside
// Burst (as a generic type parameter); the camera side runs managed (through the boxed interface).

using Unity.Mathematics;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// The single projection abstraction (docs/coordinates-and-projections.md §6). Implemented by a
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

        /// <summary>True when this projection's render mapping flips triangle winding vs the planar convention
        /// (the ECEF→render axis-swap `(X,Z,Y)` is a reflection). The tessellation pipeline reverses triangle
        /// order for these so front-faces point outward (back-face culling shows the near hemisphere). Planar
        /// Mercator: false; globe (ECEF): true.</summary>
        bool ReversesWinding { get; }

        // ── Camera interaction (managed side) ───────────────────────────────────────────────────────

        /// <summary>Returns the geodetic ground point under the given screen pixel. Screen convention: +x right, +y up, origin bottom-left. Overhead-correct now; the tilted ray-cast is a future internal upgrade of this method.</summary>
        GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera);

        /// <summary>Returns the screen pixel for the given geodetic ground point. Exact inverse of ScreenToGround at zero tilt.</summary>
        double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera);

        /// <summary>Clamps a latitude to the valid geodetic range for this projection.</summary>
        double ClampValidLatitude(double latitudeDegrees);
    }
}
