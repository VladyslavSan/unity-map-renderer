using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Coordinates
{
    /// <summary>
    /// A slippy-map tile address (z/x/y) plus conversions from tile-local feature coordinates to
    /// lon/lat and Web Mercator. Tile-local origin is top-left; the lon/lat formula already encodes
    /// the Y-down convention, so callers must NOT pre-flip Y (see docs §4).
    /// </summary>
    public readonly struct TileId : IEquatable<TileId>
    {
        public readonly int Z, X, Y;

        public TileId(int z, int x, int y)
        {
            Z = z; X = x; Y = y;
        }

        /// <summary>(px,py) in [0,extent], origin top-left → lon/lat in degrees.</summary>
        public double2 ToLonLat(double px, double py, double extent)
        {
            double n = Math.Pow(2.0, Z);
            double u = (X + px / extent) / n;
            double v = (Y + py / extent) / n;
            double lon = u * 360.0 - 180.0;
            double lat = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * v))) * 180.0 / Math.PI;
            return new double2(lon, lat);
        }

        /// <summary>(px,py) in [0,extent] → Web Mercator meters.</summary>
        public double2 ToMercator(double px, double py, double extent)
        {
            double2 ll = ToLonLat(px, py, extent);
            return WebMercator.FromLonLat(ll.x, ll.y);
        }

        /// <summary>The tile's Mercator bounding box (min/max corners). Useful for sanity checks.</summary>
        public (double2 min, double2 max) MercatorBounds()
        {
            // Use extent = 1 so px/py in {0,1} address the tile's own corners.
            double2 a = ToMercator(0.0, 0.0, 1.0);
            double2 b = ToMercator(1.0, 1.0, 1.0);
            return (math.min(a, b), math.max(a, b));
        }

        // -----------------------------------------------------------------------------------------
        // IEquatable<TileId> — required for use as Dictionary/HashSet key without boxing.
        // -----------------------------------------------------------------------------------------

        public bool Equals(TileId other) => Z == other.Z && X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is TileId other && Equals(other);

        public override int GetHashCode()
        {
            // FNV-1a-inspired combine — cheap and low-collision for small z/x/y values.
            unchecked
            {
                int h = 17;
                h = h * 31 + Z;
                h = h * 31 + X;
                h = h * 31 + Y;
                return h;
            }
        }

        public static bool operator ==(TileId a, TileId b) => a.Equals(b);
        public static bool operator !=(TileId a, TileId b) => !a.Equals(b);

        public override string ToString() => $"{Z}/{X}/{Y}";
    }
}
