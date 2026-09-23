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
    /// Decodes the Mapbox Vector Tile protobuf into <see cref="MvtTile"/>. Clean-room, built from the
    /// open MVT spec.
    ///
    /// Single decode point: the Burst <c>MvtDecodeJob</c> in <c>MapRenderer.Jobs</c> operates only on the
    /// pre-extracted command stream (NativeArray-compatible) and never sees the protobuf or managed
    /// strings/dictionaries. Therefore this managed decoder is the single point that decodes the full MVT
    /// message — feature properties, id, geometry type, and geometry are all decoded here.
    ///
    /// <para><b>The decode takes the <see cref="TileId"/> and materializes EAGERLY.</b> Each layer's rings
    /// are flattened into its own <c>TileGeometryBuffers</c> before <see cref="Decode"/> returns, and no
    /// per-feature <c>uint[]</c> command array is minted on the way: each feature's geometry field is
    /// captured as a byte range only, and every feature's commands are flattened directly into one shared
    /// <c>Allocator.Persistent</c> <c>NativeArray&lt;uint&gt;</c>. Feature tag words are flattened the same
    /// way, into <c>MvtLayer.FeatureTagWords</c>.</para>
    ///
    /// <para><b>Why eager, and why the id is a parameter.</b> Lazy per-layer materialization would mutate
    /// the tile on a second thread <i>after</i> the wrapping <c>SharedDisposable{IDecodedTile}</c> publishes
    /// it, breaking that class's safe-publication argument; eager keeps every write inside the decode. And
    /// the tile address enters the pipeline exactly ONCE, here, at the fetch, rather than coming from
    /// whichever caller happened to want geometry.</para>
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

            // Pre-size every per-feature / per-table list from a read-only counting pass over the layer bytes
            // (a struct COPY of the cursor). MVT decode is streaming — the true counts are only known after
            // the read loop — so without this the Features / Keys / Values lists and the four per-feature
            // scratch lists grow by doubling, discarding a chain of backing arrays per layer. The extra scan
            // is off-main CPU traded for less GC (the goal). A miscount could only mis-SIZE a list, never
            // change what is decoded, so this cannot alter behaviour.
            var (featureCount, keyCount, valueCount) = CountLayerElements(r);
            layer.Features.Capacity = featureCount;
            layer.Keys.Capacity     = keyCount;

            // Transient GROWABLE scratch, not a valueCount-sized NativeArray — CountLayerElements' counts are
            // hints (a miscount only mis-SIZES a list, never changes what is decoded, see its doc); a fixed
            // native array sized at valueCount would turn an undercount into an out-of-bounds crash. The
            // native array is materialized below, inside the try, once the true count is known.
            var sortValues = new List<MvtValueNative>(valueCount);
            var stringBuffer = new List<string>(valueCount);

            // Per-feature tag-word BYTE BOUNDS only (two small int lists, not a uint[] per feature) — the
            // words themselves are never parsed into managed memory; resolved to Properties after the full
            // layer is read (order-independent: keys/values may follow features in the serialised stream).
            // See DecodeFeature's doc. Lists, not fixed arrays: CountLayerElements's count is a hint (a
            // miscount only mis-sizes the capacity, never breaks decoding — see its doc), so the real loop
            // must not assume the counted and actual feature counts are identical.
            var tagStart = new List<int>(featureCount);
            var tagEnd   = new List<int>(featureCount);
            // Per-feature geometry BYTE BOUNDS only, same shape as the tag bounds above — the command
            // words themselves are never parsed into managed memory. See DecodeFeature's doc.
            var geomStart = new List<int>(featureCount);
            var geomEnd   = new List<int>(featureCount);

            // Per-feature header data (geometry kind + id) buffered here rather than written onto an
            // MvtFeature yet: a feature is built COMPLETE — with its property store — only after the layer's
            // key/value/tag tables exist (below), which is what lets MvtFeature be an immutable, construct-once
            // value instead of a two-phase one (see MvtFeature.Store).
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

            // The key→index map is built HERE — once per layer, inside the decode, before any store exists —
            // never lazily on first read: a decoded tile is published across threads afterwards, and a
            // first-read build would be a write racing concurrent readers (the same publication hazard
            // MvtLayer.Geometry documents for lazy per-layer geometry).
            var keyIndex = new Dictionary<string, int>(layer.Keys.Count);
            for (int i = 0; i < layer.Keys.Count; i++)
                keyIndex[layer.Keys[i]] = i;

            // Materialize this layer's rings and tag words NOW, from the byte bounds collected above, and
            // let the scratch fall out of scope. `layer.Extent` is fully resolved by this point —
            // the extent field may appear anywhere in the layer message, which is why this runs after the
            // read loop and not inside it. The kind column is read off the same features, in the same order,
            // as the command list.
            var kinds = new List<TileGeometryType>(headers.Count);
            for (int i = 0; i < headers.Count; i++)
                kinds.Add(headers[i].Kind);

            // Flatten tags, then geometry — both through FlattenFeatureColumn — into ONE shared
            // Allocator.Persistent NativeArray<uint> apiece.
            // The geometry buffers are BORROWED by the materializer (never disposed by it — see its ctor
            // doc); the tag-words buffer is adopted by the layer (see AdoptFeatureTagWords below) — this
            // method remains the sole owner of every buffer until it hands ownership off, and frees whatever
            // is still its own in `finally`, on every exit path.
            //
            // Both flatten calls re-read raw varints from the tile's own bytes (ReadVarint throws on a
            // truncated/over-long varint — reachable on a malformed tile), so BOTH sit inside the try: a
            // throw there must not leak whichever buffers already exist. Each of the six locals below is
            // declared `default` first; FlattenFeatureColumn takes its three outputs by `ref`, not `out`
            // (see its doc), so a throw partway through either call still leaves THESE locals pointing at
            // whatever that call had already allocated — visible to `finally` below and disposed there under
            // no IsCreated guard: NativeArray.Dispose() early-returns on a default value, so the finally
            // frees whatever was allocated and no-ops on the rest.
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

                // Validate the tag-slice columns' feature-count lockstep HERE — before AdoptGeometry — not
                // only inside AdoptFeatureTagColumns (which keeps its own copy as defence-in-depth). By the
                // time AdoptFeatureTagColumns runs, AdoptGeometry has already handed the layer its geometry; a
                // throw at that point would leak it, because the layer is not yet reachable from tile.Layers
                // and Decode's catch can only free what IS reachable.
                // Unreachable — FlattenFeatureColumn always builds tagOffsets/tagLengths at featCount — but
                // this converts "unreachable in practice" into "structurally cannot fire after the adopt".
                // See ValidateTagSliceColumnsMatchFeatureCount's regression tooth.
                ValidateTagSliceColumnsMatchFeatureCount(tagOffsets.Length, tagLengths.Length, featCount, layer.Name);

                // Resolver takes the tag-words, value-table AND per-feature (offset,count) columns here (all
                // borrowed, all local) — nothing between this point and the adopts below depends on the
                // resolver, so it can move as late as the buffers it needs.
                var propertyResolver = new MvtLayerPropertyResolver(
                    layer.Keys, values, valueStrings, keyIndex, tagWords, tagOffsets, tagLengths);

                // Build every feature COMPLETE now — geometry kind + id from its parsed header, and its
                // property store — so MvtFeature is constructed once and never mutated (its fields are
                // init-only). Each store holds only its ordinal; its (offset,count) slice is read from the
                // layer's columns through the resolver, so the slice lives in one place, not copied per store.
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
                    // INVARIANT: nothing between this AdoptGeometry and the AdoptFeatureTagWords/AdoptValues
                    // below may throw. All three adopts must run, in order, before the method returns — the
                    // layer is not yet in tile.Layers, so Decode's catch cannot free any buffer if a throw
                    // lands between them (it would only free buffers already reachable from an added layer).
                    // A future edit that inserts a throwing statement here would leak whatever was already
                    // adopted.
                    layer.AdoptGeometry(materializer.Materialize());
                }

                // Adopt LAST, after the geometry has been adopted — see the invariant comment above. Null
                // each local on transfer (the "transfer nulls the source" double-free guard): the `finally`
                // below still runs each Dispose(), but on a default array that is a no-op, so a just-adopted
                // buffer is never freed out from under the layer. The (offset,count) columns are adopted here
                // too — the resolver borrows them, so they must outlive the transient decode scope.
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
        /// Throws unless <paramref name="tagOffsetsLength"/> and <paramref name="tagLengthsLength"/>
        /// both equal <paramref name="featCount"/> — the lockstep <see cref="MvtLayer.AdoptFeatureTagColumns"/>
        /// also enforces, called here <b>before <see cref="MvtLayer.AdoptGeometry"/></b> so a mismatch throws
        /// while every decode buffer is still local to <see cref="DecodeLayer"/> and its <c>finally</c> can
        /// free them (see the call site's comment for why a throw after <c>AdoptGeometry</c> would leak it).
        ///
        /// <para>Broadened from <c>private</c> to <c>internal</c> so the regression tooth can drive it
        /// directly with a synthetic mismatch — a REAL mismatch is unreachable through <see cref="Decode"/>
        /// (<see cref="FlattenFeatureColumn"/> builds both columns at <paramref name="featCount"/> by
        /// construction), so there is no malformed-tile input that reaches this check from the public
        /// decode entry point.</para>
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
        /// Flattens one per-feature byte-bounds column — <paramref name="starts"/>/<paramref name="ends"/>,
        /// as recorded by <see cref="DecodeFeature"/> for either the tag or the geometry field — into ONE
        /// shared <c>Allocator.Persistent</c> native buffer. Two-pass count-then-fill: a first pass over
        /// each feature's <c>[start, end)</c> slice on a throwaway cursor copy counts its varints (so
        /// <paramref name="words"/> can be allocated at its exact total size), then a second pass re-slices
        /// and fills it. Both passes re-read raw varints from the tile's own bytes via
        /// <c>ProtobufReader.ReadVarint</c>, which throws on a truncated/over-long varint — reachable on a
        /// malformed tile — so this method can throw after allocating <paramref name="offsets"/>/
        /// <paramref name="lengths"/> and/or <paramref name="words"/>.
        ///
        /// <para><b>Why the three outputs are <c>ref</c>, not <c>out</c>.</b> An <c>out</c> parameter is
        /// copied back to the caller only on NORMAL return, so a throw mid-body would leave the caller's
        /// local unchanged — stranding whatever this method had already allocated, invisible to any
        /// <c>finally</c> the caller wraps the call in. A <c>ref</c> parameter IS the caller's own storage:
        /// each assignment here (<c>offsets = …</c>, then <c>words = …</c>) is visible to the caller the
        /// instant it executes, so a throw between the two still leaves the caller holding a valid reference
        /// to whichever buffers this method finished allocating before the throw — exactly what
        /// <see cref="DecodeLayer"/>'s enclosing <c>try</c>/<c>finally</c> depends on to free every buffer on
        /// every exit path (see <see cref="DecodeLayer"/>'s comment at the call sites).</para>
        ///
        /// <para>Callers own everything written into <paramref name="offsets"/>/<paramref name="lengths"/>/
        /// <paramref name="words"/> and are responsible for disposal — this method never disposes, on the
        /// success path or the throw path.</para>
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
        /// Decodes one Feature sub-message. Returns the feature HEADER (geometry type + id, the latter
        /// <see cref="Value.Null"/> when field 1 was absent), the tag-word stream's byte bounds (to be
        /// flattened after the layer's key/value tables are fully read) and the geometry command stream's
        /// byte bounds — all as OUT-OF-BAND results the caller consumes to build the feature once,
        /// complete, later: none of them belongs on the feature, and <see cref="MvtFeature"/> is
        /// construct-once, so it is not built until its store exists.
        ///
        /// <para><b>Neither the tag field nor the geometry field is parsed here.</b>
        /// <c>ReadLengthDelimited</c> returns <c>[start,end)</c> as absolute offsets into the tile's root byte
        /// buffer (every <see cref="ProtobufReader"/> slice shares the same backing array — see
        /// <c>ProtobufReader.Slice</c>), so the caller can re-open that exact byte range later with its OWN
        /// reader and flatten every feature's tag words / commands straight into one shared
        /// <c>NativeArray&lt;uint&gt;</c> apiece — no per-feature managed <c>uint[]</c> ever exists. A second
        /// (or later) occurrence of either field overwrites its bounds, so the last occurrence wins. Absent
        /// field ⇒ <c>(0, 0)</c>, meaning "zero words".</para>
        ///
        /// <para><b>A repeated tag or geometry field is also lazily parsed.</b> Only the LAST occurrence's bounds
        /// survive here, and those bytes are parsed once, later, by the caller — so a malformed EARLIER
        /// occurrence (e.g. an unterminated packed varint) is never parsed and is not rejected.</para>
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
