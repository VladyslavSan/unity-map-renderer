namespace Unity.Mathematics
{
    // S60: float4 needed by LineDash.TryEvaluatePattern (alloc-free packed dasharray).
    public struct float4
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public float4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }

        // S2 (symbol projection): SymbolScreenProjection.TryProjectPoint builds clip-space input from the
        // rebased float3 + homogeneous w. Mirrors Unity.Mathematics.float4(float3, float).
        public float4(float3 xyz, float w) { x = xyz.x; y = xyz.y; z = xyz.z; this.w = w; }

        // Implicit zero value
        public static readonly float4 zero = new float4(0f, 0f, 0f, 0f);
    }
}
