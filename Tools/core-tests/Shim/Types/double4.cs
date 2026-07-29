namespace Unity.Mathematics
{
    // Minimal stand-in for Unity.Mathematics.double4, mirroring the double2 shim: only the members Core
    // actually touches (construction + component reads on FillPattern.Resolution.Rect). No swizzles, no
    // arithmetic — add them the day Core needs them, not before.
    public struct double4
    {
        public double x;
        public double y;
        public double z;
        public double w;
        public double4(double x, double y, double z, double w)
        {
            this.x = x; this.y = y; this.z = z; this.w = w;
        }
    }
}
