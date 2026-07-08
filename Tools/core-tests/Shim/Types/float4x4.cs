namespace Unity.Mathematics
{
    // S20 T2: float4x4 is needed by MapRenderer.Core.Text.Placement.LabelScreenProjection (the combined
    // view-projection matrix). Column-major (c0..c3 are the matrix's columns), matching the real
    // Unity.Mathematics.float4x4 / HLSL mul(matrix, columnVector) convention — see VectorMath.mul below.
    public struct float4x4
    {
        public float4 c0;
        public float4 c1;
        public float4 c2;
        public float4 c3;

        public float4x4(float4 c0, float4 c1, float4 c2, float4 c3)
        {
            this.c0 = c0;
            this.c1 = c1;
            this.c2 = c2;
            this.c3 = c3;
        }

        public static readonly float4x4 identity = new float4x4(
            new float4(1f, 0f, 0f, 0f),
            new float4(0f, 1f, 0f, 0f),
            new float4(0f, 0f, 1f, 0f),
            new float4(0f, 0f, 0f, 1f));
    }
}
