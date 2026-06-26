namespace Unity.Mathematics
{
    // S06: double3 is needed by MapRenderer.Core.View.FloatingOrigin and Coordinates.
    public struct double3
    {
        public double x;
        public double y;
        public double z;
        public double3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    }
}
