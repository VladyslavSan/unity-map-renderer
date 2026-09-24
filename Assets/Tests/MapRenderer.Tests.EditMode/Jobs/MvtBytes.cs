// Unity EditMode only — a test-side MVT encoder. Engine-free in itself, but it lives beside the fixtures
// that consume it and is NOT registered in core-tests.csproj.

using System.Collections.Generic;
using System.Text;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A minimal MVT protobuf writer — the inverse of the slice of the spec <c>MvtDecoder</c> reads, and the
    /// only way to obtain decoded layers whose shapes no committed fixture holds: a ring-less feature
    /// (<see cref="OrdinalDomainTests"/> clause A) and a layer with no usable <c>name</c>
    /// (<see cref="GeoJsonTileDecoderTests.AStyleLayerWithNoSourceLayer_StillSelectsNothingFromAnMvtTile"/>).
    /// Non-obvious why: it shares nothing with production, because an encoder that reused the decoder's field
    /// numbers would agree with it about a wrong one.
    /// </summary>
    internal static class MvtBytes
    {
        // MVT spec §4.1 field numbers, transcribed independently of MvtDecoder's private constants.
        private const int TileLayers    = 3;
        private const int LayerName     = 1;
        private const int LayerFeatures = 2;
        private const int LayerExtent   = 5;
        private const int LayerVersion  = 15;
        private const int FeatureType     = 3;
        private const int FeatureGeometry = 4;

        private const int WireVarint          = 0;
        private const int WireLengthDelimited = 2;

        /// <summary>MVT command integer for a single MoveTo: <c>(count &lt;&lt; 3) | id</c>, id 1.</summary>
        private const uint MoveToOnce = (1u << 3) | 1u;

        public static byte[] Tile(params byte[][] layers)
        {
            var tile = new List<byte>();
            foreach (byte[] layer in layers) WriteLengthDelimited(tile, TileLayers, layer);
            return tile.ToArray();
        }

        public static byte[] Layer(string name, params byte[][] features)
        {
            var layer = new List<byte>();
            WriteLengthDelimited(layer, LayerName, Encoding.UTF8.GetBytes(name));
            WriteTag(layer, LayerVersion, WireVarint); WriteVarint(layer, 2);
            WriteTag(layer, LayerExtent,  WireVarint); WriteVarint(layer, 4096);
            foreach (byte[] feature in features) WriteLengthDelimited(layer, LayerFeatures, feature);
            return layer.ToArray();
        }

        /// <summary>One POINT feature at <paramref name="tileX"/>/<paramref name="tileY"/> (tile-local,
        /// absolute — the cursor starts at the origin, so the single MoveTo delta IS the position).</summary>
        public static byte[] PointFeature(int tileX, int tileY)
        {
            var geometry = new List<byte>();
            WriteVarint(geometry, MoveToOnce);
            WriteVarint(geometry, ZigZag(tileX));
            WriteVarint(geometry, ZigZag(tileY));

            var feature = new List<byte>();
            WriteTag(feature, FeatureType, WireVarint);
            WriteVarint(feature, (ulong)TileGeometryType.Point);
            WriteLengthDelimited(feature, FeatureGeometry, geometry.ToArray());
            return feature.ToArray();
        }

        /// <summary>A POINT feature whose <c>geometry</c> field is <b>present and empty</b> (a
        /// zero-length packed field). Spec-conformant — MVT 2.1 §4.2 requires the field, not a minimum
        /// length — and it reaches the materializer as <c>new uint[0]</c>, so it produces no rings by the
        /// <c>Length</c> arm of <c>?.Length ?? 0</c>.</summary>
        public static byte[] EmptyGeometryPointFeature()
        {
            var feature = new List<byte>();
            WriteTag(feature, FeatureType, WireVarint);
            WriteVarint(feature, (ulong)TileGeometryType.Point);
            WriteLengthDelimited(feature, FeatureGeometry, System.Array.Empty<byte>());
            return feature.ToArray();
        }

        /// <summary>A POINT feature with the <c>geometry</c> field <b>absent</b>. MVT 2.1 §4.2 requires the
        /// field, but <c>DecodeFeature</c> accepts the feature and leaves the stream <c>null</c>. It reaches the
        /// materializer by the <c>?.</c> arm, not the <c>Length</c> arm, so it and
        /// <see cref="EmptyGeometryPointFeature"/> are not interchangeable.</summary>
        public static byte[] AttributeOnlyPointFeature()
        {
            var feature = new List<byte>();
            WriteTag(feature, FeatureType, WireVarint);
            WriteVarint(feature, (ulong)TileGeometryType.Point);
            return feature.ToArray();
        }

        /// <summary>A layer whose <c>name</c> field is <b>ABSENT</b>. MVT 2.1 §4.1 requires it, but
        /// <c>DecodeLayer</c> accepts the layer with a <b>null</b> name. Only this input makes "a style layer
        /// with no <c>source-layer</c> selects nothing" discriminating: <c>l.Name == null</c> is TRUE here, so
        /// without the guard a background or raster layer would acquire its features.</summary>
        public static byte[] NamelessLayer(params byte[][] features)
        {
            var layer = new List<byte>();
            WriteTag(layer, LayerVersion, WireVarint); WriteVarint(layer, 2);
            WriteTag(layer, LayerExtent,  WireVarint); WriteVarint(layer, 4096);
            foreach (byte[] feature in features) WriteLengthDelimited(layer, LayerFeatures, feature);
            return layer.ToArray();
        }

        private static void WriteTag(List<byte> into, int field, int wireType) =>
            WriteVarint(into, (ulong)((field << 3) | wireType));

        private static void WriteLengthDelimited(List<byte> into, int field, byte[] payload)
        {
            WriteTag(into, field, WireLengthDelimited);
            WriteVarint(into, (ulong)payload.Length);
            into.AddRange(payload);
        }

        private static void WriteVarint(List<byte> into, ulong value)
        {
            while (value >= 0x80) { into.Add((byte)(value | 0x80)); value >>= 7; }
            into.Add((byte)value);
        }

        // The (uint) hop is load-bearing: a negative int cast straight to ulong SIGN-EXTENDS and emits a
        // 10-byte varint instead of the intended small one.
        private static ulong ZigZag(int value) => (ulong)(uint)((value << 1) ^ (value >> 31));
    }
}
