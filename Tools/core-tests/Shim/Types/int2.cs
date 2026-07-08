namespace Unity.Mathematics
{
    // S18: int2 is needed by MapRenderer.Core.Text.SdfGlyph (padded SDF cell dimensions).
    public struct int2
    {
        public int x;
        public int y;
        public int2(int x, int y) { this.x = x; this.y = y; }
    }
}
