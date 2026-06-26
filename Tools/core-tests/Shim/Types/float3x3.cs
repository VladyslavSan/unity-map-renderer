namespace Unity.Mathematics
{
    // S61: float3x3 is needed by WebMercator.TangentBasis and Ecef.TangentBasis return type.
    // Column convention: c0=East, c1=Up, c2=North (matching the projection math modules).
    public struct float3x3
    {
        public float3 c0;
        public float3 c1;
        public float3 c2;

        public float3x3(float3 c0, float3 c1, float3 c2)
        {
            this.c0 = c0;
            this.c1 = c1;
            this.c2 = c2;
        }
    }
}
