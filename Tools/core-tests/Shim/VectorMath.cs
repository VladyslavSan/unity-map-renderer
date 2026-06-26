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
    }
}
