// Minimal Unity.Mathematics shim so the real Core .cs files (and engine-free tests) compile in a plain
// dotnet test project. Core's only Unity.Mathematics surface on the tested paths is double2 (x, y)
// and the math utility class (min/max component-wise operations used in TileId.MercatorBounds).
namespace Unity.Mathematics
{
    public struct double2
    {
        public double x;
        public double y;
        public double2(double x, double y) { this.x = x; this.y = y; }
    }

    /// <summary>
    /// Subset of Unity.Mathematics.math sufficient for the Core files included in core-tests.
    /// Extend as new Core files that use math.* are added to the csproj.
    /// </summary>
#pragma warning disable CS8981 // 'math' name contains only lower-case ASCII — reserved-keyword warning in C# 11+
    public static class math
#pragma warning restore CS8981
    {
        public static double2 min(double2 a, double2 b)
            => new double2(System.Math.Min(a.x, b.x), System.Math.Min(a.y, b.y));

        public static double2 max(double2 a, double2 b)
            => new double2(System.Math.Max(a.x, b.x), System.Math.Max(a.y, b.y));
    }
}
