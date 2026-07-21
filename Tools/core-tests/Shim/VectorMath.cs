namespace Unity.Mathematics
{
    /// <summary>
    /// Vector math operations.
    /// </summary>
#pragma warning disable CS8981
    public static partial class math
#pragma warning restore CS8981
    {
        // S61: Ecef.TangentBasis uses math.cross for the ENU north vector.
        public static double3 cross(double3 a, double3 b)
            => new double3(a.y * b.z - a.z * b.y,
                           a.z * b.x - a.x * b.z,
                           a.x * b.y - a.y * b.x);

        // S91-C: FloatingOrigin.TileToSceneRebased rotates a render-space delta into the look-at ENU frame.
        // Matrix-vector product with column-major float3x3 (columns are the basis images): m·v = c0·x + c1·y + c2·z.
        public static float3 mul(float3x3 a, float3 b)
            => new float3(a.c0.x * b.x + a.c1.x * b.y + a.c2.x * b.z,
                          a.c0.y * b.x + a.c1.y * b.y + a.c2.y * b.z,
                          a.c0.z * b.x + a.c1.z * b.y + a.c2.z * b.z);

        // S2 (symbol projection): SceneFrame.Rebase = transpose(TangentBasisAt(lookAt)). Transpose a
        // column-major float3x3: new column i = old row i. Mirrors Unity.Mathematics.transpose so the fast
        // gate agrees with the EditMode/Burst gate.
        public static float3x3 transpose(float3x3 m)
            => new float3x3(new float3(m.c0.x, m.c1.x, m.c2.x),
                            new float3(m.c0.y, m.c1.y, m.c2.y),
                            new float3(m.c0.z, m.c1.z, m.c2.z));

        // S20 T2: LabelScreenProjection.TryProjectAnchor's view-projection apply. Column-major float4x4,
        // same convention as the float3x3 overload above: m·v = c0·x + c1·y + c2·z + c3·w.
        public static float4 mul(float4x4 a, float4 b)
            => new float4(
                a.c0.x * b.x + a.c1.x * b.y + a.c2.x * b.z + a.c3.x * b.w,
                a.c0.y * b.x + a.c1.y * b.y + a.c2.y * b.z + a.c3.y * b.w,
                a.c0.z * b.x + a.c1.z * b.y + a.c2.z * b.z + a.c3.z * b.w,
                a.c0.w * b.x + a.c1.w * b.y + a.c2.w * b.z + a.c3.w * b.w);

        // S91-C: SphericalProjection.ScreenToGround/GroundToScreen (globe camera ray-cast).
        public static double  dot(double3 a, double3 b)   => a.x * b.x + a.y * b.y + a.z * b.z;
        public static double  length(double3 a)           => System.Math.Sqrt(dot(a, a));
        public static double  lengthsq(double3 a)         => dot(a, a);
        // Stage AC (curved-world): PolylineArcMath.SampleWorld's world-point lerp.
        public static double3 lerp(double3 a, double3 b, double t)
            => new double3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);

        // A-2: LineAnchorPlacement (tile-space arc lengths) + the test world-anchor recovery use double2 metrics.
        public static double  dot(double2 a, double2 b)   => a.x * b.x + a.y * b.y;
        public static double  length(double2 a)           => System.Math.Sqrt(a.x * a.x + a.y * a.y);
        public static double2 lerp(double2 a, double2 b, double t)
            => new double2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);

        // float2 vector helpers (Unity.Mathematics parity).
        public static float   dot(float2 a, float2 b)      => a.x * b.x + a.y * b.y;
        public static float   length(float2 a)             => (float)System.Math.Sqrt(a.x * a.x + a.y * a.y);
        public static float   lengthsq(float2 a)           => a.x * a.x + a.y * a.y;
        public static float   distance(float2 a, float2 b) => length(a - b);
        public static float2  lerp(float2 a, float2 b, float t) => new float2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        public static float   lerp(float a, float b, float t)   => a + (b - a) * t;
        public static double3 normalize(double3 a)
        {
            double len = length(a);
            return len > 0.0 ? new double3(a.x / len, a.y / len, a.z / len) : a;
        }
    }
}
