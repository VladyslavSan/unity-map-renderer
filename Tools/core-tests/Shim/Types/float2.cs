namespace Unity.Mathematics
{
    // S19: float2 is needed by MapRenderer.Core.Text.SymbolQuad / TextLayoutOptions / TextQuadLayout
    // (label-local glyph-quad corners + UV rects in baked-pixel space). API surface mirrored from the
    // real Unity.Mathematics.float2 (reflected off UnityEngine.MathematicsModule.dll) so this one shim
    // type keeps the real Core source compiling identically under both runners: fields, the `zero`
    // static, the implicit int2->float2 conversion, and the arithmetic operators actually used by S19.
    public struct float2
    {
        public float x;
        public float y;

        public static readonly float2 zero = new float2(0f, 0f);

        public float2(float x, float y) { this.x = x; this.y = y; }

        public static implicit operator float2(int2 v) => new float2(v.x, v.y);

        public static float2 operator +(float2 a, float2 b) => new float2(a.x + b.x, a.y + b.y);
        public static float2 operator -(float2 a, float2 b) => new float2(a.x - b.x, a.y - b.y);
        public static float2 operator -(float2 a) => new float2(-a.x, -a.y);
        public static float2 operator *(float2 a, float s) => new float2(a.x * s, a.y * s);
        public static float2 operator *(float s, float2 a) => new float2(a.x * s, a.y * s);
        public static float2 operator /(float2 a, float2 b) => new float2(a.x / b.x, a.y / b.y);
        public static float2 operator /(float2 a, float s) => new float2(a.x / s, a.y / s);
    }
}
