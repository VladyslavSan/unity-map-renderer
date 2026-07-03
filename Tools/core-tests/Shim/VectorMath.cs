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

        // S91-C: SphericalProjection.ScreenToGround/GroundToScreen (globe camera ray-cast).
        public static double  dot(double3 a, double3 b)   => a.x * b.x + a.y * b.y + a.z * b.z;
        public static double  length(double3 a)           => System.Math.Sqrt(dot(a, a));
        public static double3 normalize(double3 a)
        {
            double len = length(a);
            return len > 0.0 ? new double3(a.x / len, a.y / len, a.z / len) : a;
        }
    }
}
