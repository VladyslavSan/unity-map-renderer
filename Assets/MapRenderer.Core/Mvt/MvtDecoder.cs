using System.Collections.Generic;

namespace MapRenderer.Core.Mvt
{
    /// <summary>
    /// Decodes the Mapbox Vector Tile protobuf into <see cref="MvtTile"/>. Clean-room, built from the
    /// open MVT spec — only the field numbers fills need are read; everything else is skipped.
    /// </summary>
    public static class MvtDecoder
    {
        // Tile message
        private const int TileLayers = 3;
        // Layer message
        private const int LayerName = 1, LayerFeatures = 2, LayerExtent = 5, LayerVersion = 15;
        // Feature message
        private const int FeatureType = 3, FeatureGeometry = 4;

        public static MvtTile Decode(byte[] data)
        {
            var tile = new MvtTile();
            var r = new ProtobufReader(data);
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                if (field == TileLayers && wt == 2)
                {
                    var (s, e) = r.ReadLengthDelimited();
                    tile.Layers.Add(DecodeLayer(r.Slice(s, e)));
                }
                else
                {
                    r.SkipField(wt);
                }
            }
            return tile;
        }

        private static MvtLayer DecodeLayer(ProtobufReader r)
        {
            var layer = new MvtLayer();
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case LayerName when wt == 2:
                        layer.Name = r.ReadString();
                        break;
                    case LayerExtent when wt == 0:
                        layer.Extent = r.ReadUInt32();
                        break;
                    case LayerVersion when wt == 0:
                        layer.Version = r.ReadUInt32();
                        break;
                    case LayerFeatures when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        layer.Features.Add(DecodeFeature(r.Slice(s, e)));
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            return layer;
        }

        private static MvtFeature DecodeFeature(ProtobufReader r)
        {
            var f = new MvtFeature();
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case FeatureType when wt == 0:
                        f.GeometryType = (MvtGeometryType)r.ReadUInt32();
                        break;
                    case FeatureGeometry when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        f.Geometry = ReadPackedUInt32(r.Slice(s, e));
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            return f;
        }

        private static uint[] ReadPackedUInt32(ProtobufReader r)
        {
            var list = new List<uint>();
            while (r.HasMore) list.Add((uint)r.ReadVarint());
            return list.ToArray();
        }
    }
}
