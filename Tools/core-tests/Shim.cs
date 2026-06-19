// Minimal Unity.Mathematics shim so the real Core .cs files (and engine-free tests) compile in a plain
// dotnet test project. Core's only Unity.Mathematics surface on the tested paths is double2 (x, y).
namespace Unity.Mathematics
{
    public struct double2
    {
        public double x;
        public double y;
        public double2(double x, double y) { this.x = x; this.y = y; }
    }
}
