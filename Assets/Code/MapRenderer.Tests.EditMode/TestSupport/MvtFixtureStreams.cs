// A measurement instrument, so it lives in the test assembly. Engine-free (ProtobufReader + uint[]).

using System.Collections.Generic;
using MapRenderer.Core.Protobuf;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.TestSupport
{
    /// <summary>
    /// Reads a committed <c>.pbf</c> fixture's <b>per-feature MVT geometry command streams</b> straight out of
    /// the bytes, independently of production's decoder.
    ///
    /// <para><b>Why this exists (IR C1 P3).</b> Until P3 the differential oracles
    /// (<c>StyledLineBufferParityTests</c>, <c>SymbolBufferParityTests</c>, <c>JobifiedPipelineTests</c>'
    /// decode parity, <c>LineRibbonJobTests</c>' fixture sweep) reached the command words through
    /// <c>MvtFeature.Geometry</c> and ran <see cref="MvtGeometry.Decode"/> over them. P3 deleted that field —
    /// geometry belongs to the layer now, and the words are consumed and dropped inside
    /// <c>MvtDecoder.Decode</c>. Reading the buffer those same oracles are checking would <b>disarm</b> them
    /// (arm A and arm B would become the same measurement), so arm A gets its own reader.</para>
    ///
    /// <para>That is a strict improvement, not a workaround: the oracle no longer shares ANY code with the
    /// decoder it audits. It is deliberately minimal — layer name, extent, per-feature geometry type and
    /// geometry field, everything else skipped — and it reuses only <see cref="ProtobufReader"/>, the generic
    /// varint primitive that the glyph decoder also uses and that is not MVT-specific.</para>
    /// </summary>
    public static class MvtFixtureStreams
    {
        private const int TileLayers = 3;
        private const int LayerName = 1, LayerFeatures = 2, LayerExtent = 5;
        private const int FeatureType = 3, FeatureGeometry = 4;

        /// <summary>One layer's features, as (kind, command stream) pairs in decode order — the exact order
        /// <c>RingFeatureIdx</c> indexes.</summary>
        public sealed class Layer
        {
            public string Name;
            public uint Extent = 4096;
            public readonly List<TileGeometryType> Kinds = new List<TileGeometryType>();
            public readonly List<uint[]> Commands = new List<uint[]>();
        }

        /// <summary>Every layer in the tile, in wire order.</summary>
        public static List<Layer> ReadLayers(byte[] data)
        {
            var layers = new List<Layer>();
            var r = new ProtobufReader(data);
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                if (field == TileLayers && wt == 2)
                {
                    var (s, e) = r.ReadLengthDelimited();
                    layers.Add(ReadLayer(r.Slice(s, e)));
                }
                else r.SkipField(wt);
            }
            return layers;
        }

        /// <summary>The named layer, or null.</summary>
        public static Layer ReadLayer(byte[] data, string name)
        {
            foreach (Layer l in ReadLayers(data))
                if (l.Name == name) return l;
            return null;
        }

        private static Layer ReadLayer(ProtobufReader r)
        {
            var layer = new Layer();
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
                    case LayerFeatures when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        ReadFeature(r.Slice(s, e), layer);
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            return layer;
        }

        private static void ReadFeature(ProtobufReader r, Layer layer)
        {
            var kind = TileGeometryType.Unknown;
            uint[] geometry = null;
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case FeatureType when wt == 0:
                        kind = (TileGeometryType)r.ReadUInt32();
                        break;
                    case FeatureGeometry when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        geometry = ReadPacked(r.Slice(s, e));
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            layer.Kinds.Add(kind);
            layer.Commands.Add(geometry);
        }

        private static uint[] ReadPacked(ProtobufReader r)
        {
            var list = new List<uint>();
            while (r.HasMore) list.Add((uint)r.ReadVarint());
            return list.ToArray();
        }
    }
}
