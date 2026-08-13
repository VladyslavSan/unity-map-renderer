using System.Collections.Generic;
using Unity.Profiling;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Protobuf;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// Decodes the Mapbox Vector Tile protobuf into <see cref="MvtTile"/>. Clean-room, built from the
    /// open MVT spec.
    ///
    /// Single decode point: the Burst <c>MvtDecodeJob</c> in <c>MapRenderer.Jobs</c> operates only on the
    /// pre-extracted command stream (NativeArray-compatible) and never sees the protobuf or managed
    /// strings/dictionaries. Therefore this managed decoder is the single point that decodes the full MVT
    /// message — feature properties, id, geometry type, and geometry are all decoded here.
    ///
    /// <para><b>IR C1 P3 — the decode takes the <see cref="TileId"/> and materializes EAGERLY.</b> Each
    /// layer's rings are flattened into its own <c>TileGeometryBuffers</c> before <see cref="Decode"/>
    /// returns, and the per-feature <c>uint[]</c> command arrays are dropped on the way out — they exist only
    /// inside this call now.</para>
    ///
    /// <para><b>Why eager, and why the id is a parameter.</b> Lazy per-layer materialization would mutate the
    /// tile on a second thread <i>after</i> the wrapping <c>SharedDisposable{IDecodedTile}</c> publishes it,
    /// retroactively invalidating that class's documented safe-publication argument; eager keeps every write
    /// inside the decode. And the
    /// tile address enters the pipeline exactly ONCE, here, at the fetch — it is no longer supplied by
    /// whichever caller happened to want geometry, which is precisely the mispairing the retired
    /// <c>TileGeometryStore</c> made possible.</para>
    /// </summary>
    public static class MvtDecoder
    {
        /// <summary>Profiler marker name constants (SSOT) — referenced by the marker field below and by
        /// <c>ProfilerMarkerTests</c>. The marker brackets the geometry materialization, so it lives wherever
        /// that happens: it moved from <c>FillMeshPipeline</c> to <c>TileGeometryStore</c> in IR B7, and here
        /// in IR C1 P3 when the store was retired and the decoder took the work over.</summary>
        public static class ProfilerMarkerNames
        {
            public const string Decode = "MapRenderer.Pipeline.Decode";
        }

        private static readonly ProfilerMarker PmDecode =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Decode);

        // Tile message
        private const int TileLayers = 3;

        // Layer message fields
        private const int LayerName     = 1;
        private const int LayerFeatures = 2;
        private const int LayerKeys     = 3;
        private const int LayerValues   = 4;
        private const int LayerExtent   = 5;
        private const int LayerVersion  = 15;

        // Feature message fields
        private const int FeatureId       = 1;
        private const int FeatureTags     = 2;
        private const int FeatureType     = 3;
        private const int FeatureGeometry = 4;

        // Value sub-message fields (MVT spec §4.4)
        private const int ValueString = 1;   // wire type 2 (length-delimited)
        private const int ValueFloat  = 2;   // wire type 5 (fixed32)
        private const int ValueDouble = 3;   // wire type 1 (fixed64)
        private const int ValueInt    = 4;   // wire type 0 (varint, int64 semantics)
        private const int ValueUint   = 5;   // wire type 0 (varint, uint64)
        private const int ValueSint   = 6;   // wire type 0 (varint, zigzag sint64)
        private const int ValueBool   = 7;   // wire type 0 (varint, 0=false)

        /// <param name="id">The slippy-map address these tile-local coordinates belong to. Stamped into every
        /// layer's buffer, and thereafter the only copy — see the type doc.</param>
        /// <param name="data">The MVT protobuf bytes.</param>
        public static MvtTile Decode(TileId id, byte[] data)
        {
            var tile = new MvtTile();
            try
            {
                var r = new ProtobufReader(data);
                while (r.HasMore)
                {
                    uint tag = r.ReadTag();
                    int field = ProtobufReader.FieldNumber(tag);
                    int wt = ProtobufReader.WireType(tag);
                    if (field == TileLayers && wt == 2)
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        tile.Layers.Add(DecodeLayer(id, r.Slice(s, e)));
                    }
                    else
                    {
                        r.SkipField(wt);
                    }
                }
            }
            catch
            {
                // A malformed tile throws part-way, after N layers have already minted Allocator.Persistent
                // buffers. Nothing downstream ever sees this MvtTile (the throw propagates), so this is the
                // ONLY place those N buffers can be freed — without it a decode fault is a silent native leak.
                tile.Dispose();
                throw;
            }
            return tile;
        }

        private static MvtLayer DecodeLayer(TileId id, ProtobufReader r)
        {
            var layer = new MvtLayer();
            // Keep raw tag arrays per-feature; resolve to Properties after the full layer is read.
            // This is order-independent: keys/values may follow features in the serialised stream.
            var rawTagsList = new List<uint[]>();
            // IR C1 P3: the per-feature command streams live HERE, in a local, for the duration of this
            // decode only. They are consumed by the materializer below and then dropped — a decoded feature
            // carries no geometry at all.
            var geometryList = new List<uint[]>();

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
                    case LayerKeys when wt == 2:
                        layer.Keys.Add(r.ReadString());
                        break;
                    case LayerValues when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        layer.Values.Add(DecodeValue(r.Slice(s, e)));
                        break;
                    }
                    case LayerFeatures when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        var (feature, rawTags, geometry) = DecodeFeature(r.Slice(s, e));
                        layer.Features.Add(feature);
                        rawTagsList.Add(rawTags);
                        geometryList.Add(geometry);
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }

            // Two-pass resolve: keys/values are now complete; resolve each feature's raw tags
            // into its Properties dictionary.
            for (int i = 0; i < layer.Features.Count; i++)
                ResolveProperties(layer.Features[i], rawTagsList[i], layer.Keys, layer.Values);

            // IR C1 P3: materialize this layer's rings NOW, from the command streams collected above, and let
            // them fall out of scope. `layer.Extent` is fully resolved by this point — the extent field may
            // appear anywhere in the layer message, which is why this runs after the read loop and not inside
            // it. The kind column is read off the same features, in the same order, as the command list.
            var kinds = new List<TileGeometryType>(layer.Features.Count);
            for (int i = 0; i < layer.Features.Count; i++)
                kinds.Add(layer.Features[i].GeometryType);

            using (PmDecode.Auto())
                layer.AdoptGeometry(new MvtGeometryMaterializer(id, layer.Extent, kinds, geometryList).Materialize());

            return layer;
        }

        /// <summary>
        /// Decodes one Value sub-message per MVT spec §4.4. All numeric variants map to
        /// <see cref="Value.Number"/> (double); string → <see cref="Value.String"/>;
        /// bool → <see cref="Value.Bool"/>. Unknown fields are skipped.
        /// </summary>
        private static Value DecodeValue(ProtobufReader r)
        {
            Value result = Value.Null;
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case ValueString when wt == 2:
                        result = Value.String(r.ReadString());
                        break;
                    case ValueFloat when wt == 5:
                        result = Value.Number((double)r.ReadFloat());
                        break;
                    case ValueDouble when wt == 1:
                        result = Value.Number(r.ReadDouble());
                        break;
                    case ValueInt when wt == 0:
                        // int64: read as raw varint, reinterpret as signed (two's complement)
                        result = Value.Number((double)(long)r.ReadVarint());
                        break;
                    case ValueUint when wt == 0:
                        result = Value.Number((double)r.ReadVarint());
                        break;
                    case ValueSint when wt == 0:
                        result = Value.Number((double)r.ReadSInt64());
                        break;
                    case ValueBool when wt == 0:
                        result = Value.Bool(r.ReadVarint() != 0);
                        break;
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            return result;
        }

        /// <summary>
        /// Decodes one Feature sub-message. Returns the feature (geometry type + id), the raw tag uint array
        /// (to be resolved after the layer's key/value tables are fully read) and the geometry command
        /// stream — both of the latter as OUT-OF-BAND results the caller consumes and then drops, because
        /// neither belongs on the feature (IR C1 P3). Feature id (field 1) is set on the feature directly.
        /// </summary>
        private static (MvtFeature feature, uint[] rawTags, uint[] geometry) DecodeFeature(ProtobufReader r)
        {
            var f = new MvtFeature();
            uint[] rawTags  = null;
            uint[] geometry = null;
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case FeatureId when wt == 0:
                        f.Id = r.ReadVarint();
                        f.HasId = true;
                        break;
                    case FeatureTags when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        rawTags = ReadPackedUInt32(r.Slice(s, e));
                        break;
                    }
                    case FeatureType when wt == 0:
                        f.GeometryType = (TileGeometryType)r.ReadUInt32();
                        break;
                    case FeatureGeometry when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        geometry = ReadPackedUInt32(r.Slice(s, e));
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            return (f, rawTags ?? new uint[0], geometry);
        }

        /// <summary>
        /// Resolves a feature's raw tag pairs into its <see cref="MvtFeature.Properties"/> dictionary.
        /// Tag pairs are (keyIndex, valueIndex) in the layer's key/value tables.
        ///
        /// Skip-tolerant: an odd-length tag array stops at the last complete pair; an out-of-range
        /// key or value index skips that pair without throwing, matching the decoder's overall
        /// skip-tolerant style for malformed input.
        /// </summary>
        private static void ResolveProperties(
            MvtFeature feature, uint[] rawTags, List<string> keys, List<Value> values)
        {
            if (rawTags == null || rawTags.Length == 0) return;
            // Walk tag pairs; stop before last element if odd count (last pair is incomplete).
            int pairCount = rawTags.Length / 2;
            for (int i = 0; i < pairCount; i++)
            {
                int keyIdx = (int)rawTags[i * 2];
                int valIdx = (int)rawTags[i * 2 + 1];
                // Defensive: skip out-of-range indices rather than throwing.
                if (keyIdx < 0 || keyIdx >= keys.Count) continue;
                if (valIdx < 0 || valIdx >= values.Count) continue;
                feature.Properties[keys[keyIdx]] = values[valIdx];
            }
        }

        private static uint[] ReadPackedUInt32(ProtobufReader r)
        {
            var list = new List<uint>();
            while (r.HasMore) list.Add((uint)r.ReadVarint());
            return list.ToArray();
        }
    }
}
