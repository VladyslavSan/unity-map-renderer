using System.Collections.Generic;

namespace MapRenderer.Core.Mvt
{
    /// <summary>MVT geometry type (Feature.type field values per the MVT spec).</summary>
    public enum MvtGeometryType
    {
        Unknown = 0,
        Point = 1,
        LineString = 2,
        Polygon = 3
    }

    /// <summary>
    /// A decoded feature. For Step 0 we keep only what fills need: the geometry type and the raw
    /// command/parameter integer stream (tile-local coordinates). Tags/attributes are not decoded.
    /// </summary>
    public sealed class MvtFeature
    {
        public MvtGeometryType GeometryType;
        public uint[] Geometry; // raw command stream; decode with MvtGeometry.Decode
    }

    public sealed class MvtLayer
    {
        public string Name;
        public uint Extent = 4096;
        public uint Version = 1;
        public readonly List<MvtFeature> Features = new List<MvtFeature>();
    }

    public sealed class MvtTile
    {
        public readonly List<MvtLayer> Layers = new List<MvtLayer>();

        public MvtLayer GetLayer(string name)
        {
            foreach (var l in Layers)
                if (l.Name == name) return l;
            return null;
        }
    }
}
