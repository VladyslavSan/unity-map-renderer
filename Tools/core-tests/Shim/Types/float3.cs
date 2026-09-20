namespace Unity.Mathematics
{
    // S06: float3 is needed by MapRenderer.Unity.View.FloatingOrigin (render-space offsets).
    public struct float3
    {
        public float x;
        public float y;
        public float z;
        public float3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}
