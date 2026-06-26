namespace Unity.Mathematics
{
    /// <summary>
    /// Common scalar and component-wise math operations.
    /// </summary>
#pragma warning disable CS8981
    public static partial class math
#pragma warning restore CS8981
    {
        // ── double2 component-wise (moved from the old Shim.cs) ─────────────────────────
        public static double2 min(double2 a, double2 b)
            => new double2(System.Math.Min(a.x, b.x), System.Math.Min(a.y, b.y));

        public static double2 max(double2 a, double2 b)
            => new double2(System.Math.Max(a.x, b.x), System.Math.Max(a.y, b.y));

        // ── scalar double ────────────────────────────────────────────────────────────────
        public static double abs(double x)   => System.Math.Abs(x);
        public static double max(double a, double b) => System.Math.Max(a, b);
        public static double min(double a, double b) => System.Math.Min(a, b);
        public static double floor(double x) => System.Math.Floor(x);
        public static double ceil(double x)  => System.Math.Ceiling(x);

        /// <summary>
        /// Round half-to-even (banker's rounding), matching System.Math.Round default and Unity.Mathematics.math.round.
        /// </summary>
        public static double round(double x) => System.Math.Round(x);

        /// <summary>Returns the sign of x as a double: -1.0, 0.0, or 1.0.</summary>
        public static double sign(double x)  => (double)System.Math.Sign(x);

        public static double clamp(double x, double lo, double hi)
            => x < lo ? lo : (x > hi ? hi : x);

        // ── scalar int ───────────────────────────────────────────────────────────────────
        public static int max(int a, int b) => System.Math.Max(a, b);
        public static int min(int a, int b) => System.Math.Min(a, b);
        public static int abs(int x)        => System.Math.Abs(x);
    }
}
