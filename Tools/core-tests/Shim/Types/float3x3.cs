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

        // Row-major argument order, column-major storage — matches Unity.Mathematics' own 9-scalar
        // constructor (verified against UnityEngine.MathematicsModule.dll: m00,m01,m02 is row 0).
        public float3x3(float m00, float m01, float m02,
                         float m10, float m11, float m12,
                         float m20, float m21, float m22)
        {
            c0 = new float3(m00, m10, m20);
            c1 = new float3(m01, m11, m21);
            c2 = new float3(m02, m12, m22);
        }

        // S2 (symbol projection): SceneFrame.Rebase defaults to identity on Mercator.
        public static readonly float3x3 identity =
            new float3x3(new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), new float3(0f, 0f, 1f));
    }
}
