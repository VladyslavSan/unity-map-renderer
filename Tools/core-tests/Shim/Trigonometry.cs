namespace Unity.Mathematics
{
    /// <summary>
    /// Trigonometric and transcendental functions — shim delegates to System.Math.
    /// The EditMode gate (real Unity.Mathematics) is the decisive numeric check; the shim cannot
    /// detect divergence between math.log2 and System.Math.Log(x,2.0), etc.
    /// </summary>
#pragma warning disable CS8981
    public static partial class math
#pragma warning restore CS8981
    {
        public static double sin(double x)  => System.Math.Sin(x);
        public static double cos(double x)  => System.Math.Cos(x);

        // float overloads + sincos (Unity.Mathematics parity — used by BillboardMath / LabelTranslate rotation).
        public static float sin(float x) => (float)System.Math.Sin(x);
        public static float cos(float x) => (float)System.Math.Cos(x);
        public static void sincos(float x, out float s, out float c) { s = (float)System.Math.Sin(x); c = (float)System.Math.Cos(x); }
        public static void sincos(double x, out double s, out double c) { s = System.Math.Sin(x); c = System.Math.Cos(x); }
        public static double tan(double x)  => System.Math.Tan(x);
        public static double asin(double x) => System.Math.Asin(x);
        public static double acos(double x) => System.Math.Acos(x);
        public static double atan(double x) => System.Math.Atan(x);
        public static double atan2(double y, double x) => System.Math.Atan2(y, x);
        public static double sinh(double x) => System.Math.Sinh(x);
        public static double cosh(double x) => System.Math.Cosh(x);
        public static double exp(double x)  => System.Math.Exp(x);

        /// <summary>Natural logarithm (base e). Maps Math.Log(x) [1-arg].</summary>
        public static double log(double x)  => System.Math.Log(x);

        /// <summary>Base-2 logarithm. Maps Math.Log(x, 2.0). Shim delegates to System.Math; EditMode uses real math.log2.</summary>
        public static double log2(double x) => System.Math.Log(x, 2.0);

        /// <summary>Base-10 logarithm. Maps Math.Log10(x). Shim delegates to System.Math; EditMode uses real math.log10.</summary>
        public static double log10(double x) => System.Math.Log10(x);

        public static double sqrt(double x) => System.Math.Sqrt(x);
        public static double pow(double x, double y) => System.Math.Pow(x, y);
    }
}
