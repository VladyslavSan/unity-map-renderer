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

        // Implicit zero value
        public static readonly float4 zero = new float4(0f, 0f, 0f, 0f);
    }
}
