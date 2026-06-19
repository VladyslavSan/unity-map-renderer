using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// A simple polygon with an outer ring and zero or more hole rings, all in the same coordinate
    /// space (tile space double2). Holes are stored as the MVT-decoded ring points (not yet bridged).
    /// </summary>
    public sealed class Polygon
    {
        /// <summary>The exterior ring. Points are in tile space (origin top-left, Y down).</summary>
        public List<double2> Outer;

        /// <summary>
        /// Interior rings (holes). Each hole is a ring in tile space. May be empty.
        /// </summary>
        public List<List<double2>> Holes = new List<List<double2>>();

        public Polygon(List<double2> outer)
        {
            Outer = outer;
        }
    }
}
