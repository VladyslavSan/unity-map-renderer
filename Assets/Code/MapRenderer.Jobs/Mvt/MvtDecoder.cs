using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Profiling;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Protobuf;
using MapRenderer.Core.Tiles;
namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// Decodes the MVT protobuf into <see cref="MvtTile"/>, built from the open MVT spec. It is the single
    /// point that decodes the full message; the Burst <c>MvtDecodeJob</c> sees only the command stream.
    /// It materializes every layer's geometry and tag words eagerly, into shared native buffers, before
    /// <see cref="Decode"/> returns. The tile address enters the pipeline once, here. See
    /// docs/tile-geometry-ir-design.md § "Why decode is eager and whole-tile".
    /// </summary>
    public static class MvtDecoder
    {
        /// <summary>Profiler marker name constants (SSOT) — referenced by the marker field below and by
        /// <c>ProfilerMarkerTests</c>. The marker brackets the geometry materialization, so it lives
        /// wherever that happens.</summary>
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
                // A malformed tile throws after earlier layers allocated Persistent buffers. Nothing downstream
                // sees this tile, so this is the only place that frees them.
                tile.Dispose();
                throw;
            }
            return tile;
        }

        private static MvtLayer DecodeLayer(TileId id, ProtobufReader r)
        {
            var layer = new MvtLayer();

            // Pre-size every list from a counting pass, so no list grows by doubling and discards arrays to the
            // GC. A miscount only mis-sizes a list; it never changes what is decoded.
            var (featureCount, keyCount, valueCount) = CountLayerElements(r);
            layer.Features.Capacity = featureCount;
            layer.Keys.Capacity     = keyCount;

            // Growable lists, not a valueCount-sized NativeArray: the count is a hint, and a fixed array would
            // turn an undercount into an out-of-bounds crash. The native array is built inside the try below.
            var sortValues = new List<MvtValueNative>(valueCount);
            var stringBuffer = new List<string>(valueCount);

            // Per-feature tag-word byte bounds only; the words are resolved after the full layer is read,
            // because keys and values may follow features in the stream. Lists, because the count is a hint.
            var tagStart = new List<int>(featureCount);
            var tagEnd   = new List<int>(featureCount);
            // Per-feature geometry BYTE BOUNDS only, same shape as the tag bounds above — the command
            // words themselves are never parsed into managed memory. See DecodeFeature's doc.
            var geomStart = new List<int>(featureCount);
            var geomEnd   = new List<int>(featureCount);

            // Per-feature headers wait here: a feature is built complete, with its store, only after the key,
            // value and tag tables exist, so MvtFeature stays construct-once (see MvtFeature.Store).
            var headers = new List<(TileGeometryType Kind, Value Id)>(featureCount);

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
                        sortValues.Add(DecodeValue(r.Slice(s, e), stringBuffer));
                        break;
                    }
                    case LayerFeatures when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        var (fKind, fId, fTagStart, fTagEnd, fGeomStart, fGeomEnd) = DecodeFeature(r.Slice(s, e));
                        headers.Add((fKind, fId));
                        tagStart.Add(fTagStart);
                        tagEnd.Add(fTagEnd);
                        geomStart.Add(fGeomStart);
                        geomEnd.Add(fGeomEnd);
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }

            // Build the key→index map here, not lazily: the tile is published across threads afterwards, and a
            // first-read build would be a write racing concurrent readers.
            var keyIndex = new Dictionary<string, int>(layer.Keys.Count);
            for (int i = 0; i < layer.Keys.Count; i++)
                keyIndex[layer.Keys[i]] = i;

            // Materialize after the read loop: the extent field may appear anywhere in the layer message. The
            // kind column follows the same feature order as the command list.
            var kinds = new List<TileGeometryType>(headers.Count);
            for (int i = 0; i < headers.Count; i++)
                kinds.Add(headers[i].Kind);

            // Non-local invariant: this method owns every buffer below until it hands it off, and `finally`
            // frees whatever it still owns on every exit path. The materializer borrows the geometry buffers.
            // Both flatten calls can throw on a malformed varint, so they sit inside the try, and their `ref`
            // outputs leave each partial allocation visible to `finally`. Disposing a default array is a no-op.
            int featCount = headers.Count;
            var featOffsets = default(NativeArray<int>);
            var featLengths = default(NativeArray<int>);
            var commands    = default(NativeArray<uint>);
            var tagOffsets  = default(NativeArray<int>);
            var tagLengths  = default(NativeArray<int>);
            var tagWords    = default(NativeArray<uint>);
            var values      = default(NativeArray<MvtValueNative>);
            try
            {
                // Materialize the value table from the transient scratch NOW, at the top of the try, so a
                // throw anywhere below still leaves `values` visible to `finally`.
                values = new NativeArray<MvtValueNative>(
                    sortValues.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < sortValues.Count; i++)
                    values[i] = sortValues[i];
                string[] valueStrings = stringBuffer.ToArray();

                FlattenFeatureColumn(r, tagStart, tagEnd, featCount, ref tagOffsets, ref tagLengths, ref tagWords);

                // Non-obvious why: validate here, before AdoptGeometry, because a throw after it leaks the
                // adopted geometry; the layer is not yet in tile.Layers, so Decode's catch cannot free it.
                ValidateTagSliceColumnsMatchFeatureCount(tagOffsets.Length, tagLengths.Length, featCount, layer.Name);

                // The resolver borrows the tag-words, value table and per-feature (offset,count) columns; all
                // are still local here.
                var propertyResolver = new MvtLayerPropertyResolver(
                    layer.Keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);

                // Build every feature complete, so MvtFeature is constructed once and never mutated. Each store
                // holds only its ordinal and reads its slice from the layer's columns through the resolver.
                for (int i = 0; i < headers.Count; i++)
                    layer.Features.Add(new MvtFeature
                    {
                        GeometryType = headers[i].Kind,
                        Id           = headers[i].Id,
                        Store        = new DensePropertyStore(propertyResolver, i),
                    });

                // String→id key hoist: every layer is Dense, so every layer advertises IIndexedFeatureSource
                // capability against this resolver.
                layer.DenseKeyResolver = propertyResolver;

                FlattenFeatureColumn(r, geomStart, geomEnd, featCount, ref featOffsets, ref featLengths, ref commands);

                using (PmDecode.Auto())
                {
                    var materializer = new MvtGeometryMaterializer(id, layer.Extent, kinds, commands, featOffsets, featLengths);
                    // Non-local invariant: nothing between this adopt and the adopts below may throw. The layer
                    // is not yet in tile.Layers, so Decode's catch cannot free a buffer already adopted.
                    layer.AdoptGeometry(materializer.Materialize());
                }

                // Each transfer resets its local to default, so `finally` never frees an adopted buffer. The
                // resolver borrows the (offset,count) columns, so the layer adopts them too.
                layer.AdoptFeatureTagWords(tagWords);
                tagWords = default;
                layer.AdoptFeatureTagColumns(tagOffsets, tagLengths);
                tagOffsets = default;
                tagLengths = default;
                layer.AdoptValues(values, valueStrings);
                values = default;
            }
            finally
            {
                tagWords.Dispose();
                tagOffsets.Dispose();
                tagLengths.Dispose();
                commands.Dispose();
                featOffsets.Dispose();
                featLengths.Dispose();
                values.Dispose();
            }

            return layer;
        }

        /// <summary>
        /// Throws unless both tag-slice column lengths equal <paramref name="featCount"/>, the lockstep
        /// <see cref="MvtLayer.AdoptFeatureTagColumns"/> also enforces. <see cref="DecodeLayer"/> calls it before
        /// <see cref="MvtLayer.AdoptGeometry"/>, while its <c>finally</c> can still free every buffer. It is
        /// <c>internal</c> so a test can drive a synthetic mismatch: <see cref="FlattenFeatureColumn"/> builds
        /// both columns at <paramref name="featCount"/>, so no tile reaches this throw through <see cref="Decode"/>.
        /// </summary>
        internal static void ValidateTagSliceColumnsMatchFeatureCount(
            int tagOffsetsLength, int tagLengthsLength, int featCount, string layerName)
        {
            if (tagOffsetsLength != featCount || tagLengthsLength != featCount)
                throw new InvalidOperationException(
                    $"MVT layer '{layerName}' tag-slice columns ({tagOffsetsLength}/{tagLengthsLength}) must " +
                    $"match its feature count ({featCount}).");
        }

        /// <summary>
        /// Flattens one per-feature byte-bounds column (tag or geometry) into one shared Persistent buffer, in
        /// two passes: count the varints, then fill. It throws on a malformed varint, possibly after it allocates.
        /// Non-local invariant: the outputs are <c>ref</c>, not <c>out</c>, because each assignment reaches the
        /// caller's local at once, so <see cref="DecodeLayer"/>'s <c>finally</c> frees a partial allocation.
        /// The caller owns and disposes all three outputs; this method never disposes them.
        /// </summary>
        /// <param name="r">A reader over the layer's bytes; re-sliced per feature via <c>Slice(start, end)</c>,
        /// never advanced itself.</param>
        /// <param name="starts">Per-feature byte-bound start, index-aligned with <paramref name="ends"/> and
        /// with <paramref name="offsets"/>/<paramref name="lengths"/>.</param>
        /// <param name="ends">Per-feature byte-bound end (exclusive); <c>end &lt;= start</c> means zero words
        /// for that feature (the field was absent).</param>
        /// <param name="featCount">The layer's feature count — the length <paramref name="offsets"/> and
        /// <paramref name="lengths"/> are allocated at.</param>
        /// <param name="offsets">Per-feature start index into <paramref name="words"/>, by ref.</param>
        /// <param name="lengths">Per-feature word count, by ref.</param>
        /// <param name="words">Every feature's varints, concatenated in feature order, by ref.</param>
        private static void FlattenFeatureColumn(
            ProtobufReader r, IReadOnlyList<int> starts, IReadOnlyList<int> ends, int featCount,
            ref NativeArray<int> offsets, ref NativeArray<int> lengths, ref NativeArray<uint> words)
        {
            offsets = new NativeArray<int>(featCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            lengths = new NativeArray<int>(featCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int total = 0;
            for (int i = 0; i < featCount; i++)
            {
                int n = 0;
                if (ends[i] > starts[i])
                {
                    var counter = r.Slice(starts[i], ends[i]); // struct copy — independent cursor
                    while (counter.HasMore) { counter.ReadVarint(); n++; }
                }
                offsets[i] = total;
                lengths[i] = n;
                total += n;
            }
            words = new NativeArray<uint>(total, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < featCount; i++)
            {
                int len = lengths[i];
                if (len == 0) continue;
                var fill = r.Slice(starts[i], ends[i]);
                int pos = offsets[i];
                for (int k = 0; k < len; k++) words[pos + k] = (uint)fill.ReadVarint();
            }
        }

        /// <summary>
        /// Decodes one Value sub-message per MVT spec §4.4. All numeric variants map to
        /// <see cref="MvtValueNative.Number"/> (double); string → <see cref="MvtValueNative.String"/>, its
        /// text appended to <paramref name="stringTable"/> (the transient scratch <see cref="MvtLayer.ValueStrings"/>
        /// is materialized from); bool → <see cref="MvtValueNative.Bool"/>. Unknown fields are skipped.
        /// </summary>
        /// <param name="r">A reader over this Value sub-message's bytes.</param>
        /// <param name="stringTable">The layer's transient string scratch — a string variant appends here
        /// and the result's StringId is the appended index.</param>
        private static MvtValueNative DecodeValue(ProtobufReader r, List<string> stringTable)
        {
            MvtValueNative result = MvtValueNative.Null;
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case ValueString when wt == 2:
                        stringTable.Add(r.ReadString());
                        result = MvtValueNative.String(stringTable.Count - 1);
                        break;
                    case ValueFloat when wt == 5:
                        result = MvtValueNative.Number((double)r.ReadFloat());
                        break;
                    case ValueDouble when wt == 1:
                        result = MvtValueNative.Number(r.ReadDouble());
                        break;
                    case ValueInt when wt == 0:
                        // int64: read as raw varint, reinterpret as signed (two's complement)
                        result = MvtValueNative.Number((double)(long)r.ReadVarint());
                        break;
                    case ValueUint when wt == 0:
                        result = MvtValueNative.Number((double)r.ReadVarint());
                        break;
                    case ValueSint when wt == 0:
                        result = MvtValueNative.Number((double)r.ReadSInt64());
                        break;
                    case ValueBool when wt == 0:
                        result = MvtValueNative.Bool(r.ReadVarint() != 0);
                        break;
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            return result;
        }

        /// <summary>
        /// Decodes one Feature sub-message into its header (kind, id or <see cref="Value.Null"/>) and the byte
        /// bounds of its tag and geometry fields. Neither field is parsed here: the bounds are absolute offsets
        /// into the tile's bytes, which the caller flattens later. An absent field gives <c>(0, 0)</c>.
        /// Limitation: a repeated field keeps only its last bounds, so a malformed earlier occurrence is never
        /// parsed and never rejected.
        /// </summary>
        private static (TileGeometryType kind, Value id, int tagStart, int tagEnd, int geomStart, int geomEnd)
            DecodeFeature(ProtobufReader r)
        {
            TileGeometryType kind = default; // MVT type 0 = UNKNOWN — the default when field 3 is absent.
            Value id = Value.Null;
            int tagStart = 0, tagEnd = 0;
            int geomStart = 0, geomEnd = 0;
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case FeatureId when wt == 0:
                        id = Value.Number((double)r.ReadVarint());
                        break;
                    case FeatureTags when wt == 2:
                        (tagStart, tagEnd) = r.ReadLengthDelimited();
                        break;
                    case FeatureType when wt == 0:
                        kind = (TileGeometryType)r.ReadUInt32();
                        break;
                    case FeatureGeometry when wt == 2:
                        (geomStart, geomEnd) = r.ReadLengthDelimited();
                        break;
                    default:
                        r.SkipField(wt);
                        break;
                }
            }
            return (kind, id, tagStart, tagEnd, geomStart, geomEnd);
        }

        /// <summary>
        /// Counts a layer's feature / key / value entries in a single read-only pass over its bytes, so the
        /// decode can pre-size its lists exactly and skip the doubling reallocations of a streaming build. Runs
        /// on a struct COPY of the reader (an independent cursor over the same slice), so the real decode cursor
        /// is untouched. A miscount is harmless — it only mis-sizes a list, never changes decoded content.
        /// </summary>
        /// <param name="r">A reader positioned at the start of the layer's bytes; copied, not advanced.</param>
        /// <returns>The number of Feature, Keys and Values entries the layer declares.</returns>
        private static (int features, int keys, int values) CountLayerElements(ProtobufReader r)
        {
            var counter = r;                       // struct copy — independent cursor over the same [s,e] slice
            int features = 0, keys = 0, values = 0;
            while (counter.HasMore)
            {
                uint tag = counter.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                if (wt == 2)
                {
                    if (field == LayerFeatures) features++;
                    else if (field == LayerKeys) keys++;
                    else if (field == LayerValues) values++;
                }
                counter.SkipField(wt);
            }
            return (features, keys, values);
        }
    }
}
