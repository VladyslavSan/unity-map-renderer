using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Tests.TestSupport
{
    /// <summary>
    /// Shoelace signed area for a ring of 2D points: 2A = Σ (x_i·y_{i+1} − x_{i+1}·y_i). Non-local
    /// invariant: the sign means opposite things in the coordinate systems this repo uses — positive is
    /// CCW in a right-handed Y-up system, but CW on screen in MVT's Y-down tile space (MVT exterior rings
    /// are positive-shoelace). Returns the raw value; callers interpret the sign for their own space.
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
