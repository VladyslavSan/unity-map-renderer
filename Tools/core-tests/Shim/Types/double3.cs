namespace Unity.Mathematics
{
    // S06: double3 is needed by MapRenderer.Unity.View.FloatingOrigin and Coordinates.
    public struct double3
    {
        public double x;
        public double y;
        public double z;
        public double3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }

        // Stage AC (curved-world): PolylineArcMath.SampleWorld needs the zero default the real
        // Unity.Mathematics double3 provides.
        public static readonly double3 zero = new double3(0.0, 0.0, 0.0);

        // Arithmetic operators — parity with the real Unity.Mathematics double3 (used by ViewFrustum et al.).
        public static double3 operator +(double3 a, double3 b) => new double3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static double3 operator -(double3 a, double3 b) => new double3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static double3 operator *(double3 a, double s)  => new double3(a.x * s, a.y * s, a.z * s);
        public static double3 operator *(double s,  double3 a) => new double3(a.x * s, a.y * s, a.z * s);
        public static double3 operator -(double3 a)            => new double3(-a.x, -a.y, -a.z);
    }
}
