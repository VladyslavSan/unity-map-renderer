using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Shoelace (surveyor's formula) signed area for a ring of 2D points.
    ///
    /// Uses the standard cross-product shoelace:
    ///   2A = Σ (x_i * y_{i+1} − x_{i+1} * y_i)
    ///
    /// Sign convention (standard math / right-handed 2D):
    ///   Positive → CCW in a right-handed (Y-up) system.
    ///   Negative → CW  in a right-handed (Y-up) system.
    ///
    /// In MVT tile space (Y-down / top-left origin), the sign is FLIPPED relative to Y-up:
    ///   Positive → CW  on screen  (exterior rings in MVT spec).
    ///   Negative → CCW on screen  (hole rings in MVT spec).
    ///
    /// This class returns the raw shoelace value; callers must interpret the sign for their
    /// coordinate system.
    /// </summary>
    public static class SignedArea
    {
        /// <summary>
        /// Returns 2× the signed area of the ring (standard cross-product shoelace).
        /// Positive = CCW in Y-up = CW on screen in Y-down tile space.
        /// </summary>
        public static double Compute(List<double2> ring)
        {
            double area = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % n];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        /// <summary>
        /// Returns the absolute (unsigned) area of the ring.
        /// </summary>
        public static double AbsArea(List<double2> ring)
        {
            return math.abs(Compute(ring)) * 0.5;
        }
    }
}
