namespace Unity.Mathematics
{
    public struct double2
    {
        public double x;
        public double y;
        public double2(double x, double y) { this.x = x; this.y = y; }

        // Arithmetic operators needed by LineOffset, LineTessellator tests, and other Core code.
        public static double2 operator +(double2 a, double2 b) => new double2(a.x + b.x, a.y + b.y);
        public static double2 operator -(double2 a, double2 b) => new double2(a.x - b.x, a.y - b.y);
        public static double2 operator *(double2 a, double s)  => new double2(a.x * s,   a.y * s);
        public static double2 operator *(double s,  double2 a) => new double2(a.x * s,   a.y * s);
        public static double2 operator *(double2 a, float s)   => new double2(a.x * s,   a.y * s);
        public static double2 operator *(float s,   double2 a) => new double2(a.x * s,   a.y * s);
        // Component-wise, matching real Unity.Mathematics exactly — DeviceScaling.DeviceToLogicalPx divides
        // rather than multiplying by a reciprocal, so the shim must divide component-wise too or the fast
        // project would measure different bits than the Editor runner.
        public static double2 operator /(double2 a, double s)  => new double2(a.x / s,   a.y / s);
        public static double2 operator -(double2 a)            => new double2(-a.x, -a.y);
    }
}
