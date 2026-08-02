using System;

namespace Unity.Mathematics
{
    // S18: int2 is needed by MapRenderer.Core.Text.SdfGlyph (padded SDF cell dimensions).
    // Padded sprite repack: SpriteSheetPadder/ShelfRectPacker add component-wise arithmetic and value
    // equality — both mirrored from the real Unity.Mathematics.int2 so the same Core source compiles and
    // behaves identically under both runners.
    public struct int2 : IEquatable<int2>
    {
        public int x;
        public int y;

        public static readonly int2 zero = new int2(0, 0);

        public int2(int x, int y) { this.x = x; this.y = y; }

        public static int2 operator +(int2 a, int2 b) => new int2(a.x + b.x, a.y + b.y);
        public static int2 operator -(int2 a, int2 b) => new int2(a.x - b.x, a.y - b.y);
        public static int2 operator *(int2 a, int s) => new int2(a.x * s, a.y * s);
        public static int2 operator *(int s, int2 a) => new int2(a.x * s, a.y * s);

        public static bool operator ==(int2 a, int2 b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(int2 a, int2 b) => !(a == b);

        public bool Equals(int2 other) => x == other.x && y == other.y;
        public override bool Equals(object obj) => obj is int2 other && Equals(other);
        public override int GetHashCode() => (x * 397) ^ y;
        public override string ToString() => $"int2({x}, {y})";
    }
}
