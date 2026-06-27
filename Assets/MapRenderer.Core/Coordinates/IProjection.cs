// Engine-free: no UnityEngine dependency. Runs on the main thread, once per gesture.

using Unity.Mathematics;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Managed projection interface for the camera interaction layer.
    /// Converts screen pixels to geodetic ground points and back, given the viewport and camera pose.
    /// The three members here are exactly what zoom-to-cursor and anchored-pan require — nothing speculative.
    /// </summary>
    public interface IProjection
    {
        /// <summary>Returns the geodetic ground point under the given screen pixel. Screen convention: +x right, +y up, origin bottom-left. Overhead-correct now; the tilted ray-cast is a future internal upgrade of this method.</summary>
        GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera);

        /// <summary>Returns the screen pixel for the given geodetic ground point. Exact inverse of ScreenToGround at zero tilt.</summary>
        double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera);

        /// <summary>Clamps a latitude to the valid geodetic range for this projection.</summary>
        double ClampValidLatitude(double latitudeDegrees);
    }
}
