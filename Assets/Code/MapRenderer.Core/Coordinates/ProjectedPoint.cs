// Engine-free: no UnityEngine dependency. Blittable value type — safe to embed in a Burst job.

using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// The output of <see cref="IProjection.ProjectPoint"/>: the render-space position plus the surface up
    /// (extrusion + lighting normal). Blittable (two <c>double3</c>). Returned by value so both the OOP path
    /// and the Burst projection job compute position and up in one call.
    /// </summary>
    public struct ProjectedPoint
    {
        /// <summary>Render-space position (pre-RTC; east=+X, up=+Y, north=+Z).</summary>
        public double3 World;

        /// <summary>Unit surface up at the point. Constant +Y for planar Mercator; geodetic normal for the globe.</summary>
        public double3 Up;
    }
}
