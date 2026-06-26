namespace Unity.Mathematics
{
    /// <summary>
    /// Mathematical constants. PI_DBL/E_DBL MUST be const (not static readonly) because they are
    /// referenced in const field initializers in production Core code (e.g. WebMercator.WorldExtent).
    /// </summary>
#pragma warning disable CS8981 // 'math' name contains only lower-case ASCII
    public static partial class math
#pragma warning restore CS8981
    {
        /// <summary>Double-precision pi. Use this in production Core/Unity code (NOT math.PI which is float).</summary>
        public const double PI_DBL = 3.141592653589793;

        /// <summary>Double-precision e. Use this in production Core/Unity code (NOT math.E which is float).</summary>
        public const double E_DBL = 2.718281828459045;

        /// <summary>Single-precision pi (float). Avoid in Core — use PI_DBL for double contexts.</summary>
        public const float PI = 3.14159274f;

        /// <summary>Single-precision epsilon.</summary>
        public const float EPSILON = 1.1920929e-7f;
    }
}
