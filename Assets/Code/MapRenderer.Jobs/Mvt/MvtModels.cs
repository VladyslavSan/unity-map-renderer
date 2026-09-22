using System;
using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Expressions;
using MapRenderer.Jobs.Tiles;

using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// A decoded MVT feature: the geometry type, the decoded property bag (resolved from the layer's
    /// key/value tables via the feature's tag pairs) and the optional feature id (field 1, uint64).
    ///
    /// Properties are stored as <see cref="Value"/> using the Expressions type system so the filter
    /// and expression layers can consume them directly without an extra conversion step.
    ///
    /// <para><b>A feature carries NO geometry at all.</b> Coordinates belong to the LAYER
    /// (<see cref="MvtLayer.Geometry"/>, materialized eagerly inside the decode), and a feature is purely an
    /// evaluation surface — filters and expressions, nothing else. The command words are consumed inside
    /// <c>MvtDecoder</c> and never outlive it.</para>
    /// </summary>
    public sealed class MvtFeature : IFeature, IIndexedFeature
    {
        /// <summary>Geometry type (MVT Feature.type field).</summary>
        public TileGeometryType GeometryType { get; init; }

        /// <summary>
        /// Feature id decoded from MVT Feature field 1 (uint64 varint), or <see cref="Value.Null"/> when
        /// field 1 was absent. Note: id=0 is a valid feature id per the MVT spec — an absent id is
        /// <see cref="Value.Null"/>, never a zero <see cref="Value.Number"/>.
        ///
        /// Precision note: uint64 values larger than 2^53 lose precision when narrowed to the
        /// <c>double</c> a <see cref="Value.Number"/> stores — accepted, since every real consumer already
        /// reads the id as a double.
        /// </summary>
        public Value Id { get; init; } = Value.Null;

        /// <summary>
        /// The property store backing <see cref="Properties"/> and <see cref="IFeature.TryGetProperty"/>.
        /// Set once at construction (<see cref="MvtDecoder"/> builds each feature complete, with its store,
        /// once the layer's key/value tables exist); defaults to <see cref="EmptyPropertyStore.Instance"/>
        /// so it is never null — a feature with no properties holds the Null Object, not <c>null</c>.
        /// </summary>
        internal IMvtPropertyStore Store { get; init; } = EmptyPropertyStore.Instance;

        /// <summary>Decoded properties, materialized from <see cref="Store"/> (empty for a feature whose
        /// store is the <see cref="EmptyPropertyStore"/>).</summary>
        public IReadOnlyDictionary<string, Value> Properties => Store.AsDictionary();

        // ── IFeature — the retired MvtFeatureAdapter's semantics, folded verbatim ────────────────────

        TileGeometryType IFeature.GeometryType => GeometryType;

        bool IFeature.TryGetProperty(string name, out Value value) => Store.TryGet(name, out value);

        IReadOnlyDictionary<string, Value> IFeature.Properties => Properties;

        /// <summary>The string→id key hoist's int-keyed read: forwards to <see cref="Store"/>.</summary>
        bool IIndexedFeature.TryGetPropertyByKeyIndex(int keyIndex, out Value value)
            => Store.TryGetByKeyIndex(keyIndex, out value);
    }

    /// <summary>
    /// A decoded MVT layer. Carries the layer name, extent, version, the decoded key table
    /// (field 3, ordered), the decoded value table (field 4, ordered), the feature list, this layer's
    /// decoded geometry (<see cref="Geometry"/>) and its flattened tag words
    /// (<see cref="FeatureTagWords"/>).
    /// </summary>
    public sealed class MvtLayer : ITileLayer, IIndexedFeatureSource, INativeFilterSource, IDisposable
    {
        public string Name;
        public uint Extent = 4096;
        public uint Version = 1;
        public readonly List<MvtFeature> Features = new List<MvtFeature>();

        /// <summary>This layer's rings, materialized EAGERLY by <see cref="MvtDecoder"/> from its
        /// features' command streams and owned by this layer for its whole life. One buffer per source-layer
        /// per DECODE — not per pass and not per consumer.
        ///
        /// <para><b>BORROWED by every consumer.</b> A consumer must never dispose it, never mutate it, and
        /// never retain it past the decode's scope; the layer frees it in <see cref="Dispose"/>, which
        /// <see cref="MvtTile.Dispose"/> drives. Getting this wrong is <b>loud, not quiet</b>: this buffer is
        /// array-backed (<c>TileGeometryBuffers.Allocate</c>), so a second free is a real double free of three
        /// <c>NativeArray</c>s. (The silent-no-op failure belongs to the <i>list-backed</i>
        /// <c>AdoptDerivedLists</c> mode, which no consumer borrows — see that type's doc.)</para>
        ///
        /// <para><c>default</c> (<c>IsCreated == false</c>) for a layer with no features, allocating
        /// nothing — the same "empty layer allocated nothing" result every consumer already handles.</para>
        ///
        /// <para><b>Set once, through <see cref="AdoptGeometry"/></b>, which is what makes
        /// <c>FeatureCount == Features.Count</c> structural. Three consumers size their per-feature columns
        /// from that lockstep and index them by <c>SelectedTileFeature.Ordinal</c>.</para></summary>
        public TileGeometryBuffers Geometry { get; private set; }

        private bool _geometryAdopted;

        /// <summary>Takes ownership of this layer's decoded buffer. <b>Callable exactly once</b>, and only
        /// with a buffer whose feature column matches <see cref="Features"/> — the two guards together are
        /// what makes the lockstep a property of the TYPE rather than of <c>MvtDecoder</c> remembering.
        /// A second call would silently orphan the first buffer (a native leak the tile's
        /// <see cref="Dispose"/> could no longer reach), and a mismatched column is the mis-bucketing every
        /// ordinal-indexed consumer would then commit — so both fail loudly, before either can happen.
        /// <para>A <c>default</c> buffer (<c>IsCreated == false</c>) is legal: the materializer returns one
        /// for a feature-less layer, and its zero count matches an empty <see cref="Features"/>.</para>
        /// <para>The two guards themselves live once, in <see cref="LayerGeometryAdoption"/> — the second
        /// <see cref="ITileLayer"/> now exists, and an invariant three ordinal-indexed consumers rely on must
        /// have one statement rather than two that can drift.</para></summary>
        internal void AdoptGeometry(TileGeometryBuffers geometry)
        {
            LayerGeometryAdoption.Validate(
                $"MvtLayer '{Name}'", _geometryAdopted, geometry, Features.Count);

            _geometryAdopted = true;
            Geometry         = geometry;
        }

        TileGeometryBuffers ITileLayer.Geometry => Geometry;

        /// <summary>This layer's flattened (keyIdx,valIdx) tag words, one shared <c>Allocator.Persistent</c>
        /// buffer for every feature in the layer — the buffer <see cref="DensePropertyStore"/> and
        /// <see cref="MvtLayerPropertyResolver.TagWords"/> borrow a <c>(offset, count)</c> view into.
        ///
        /// <para><b>BORROWED by every store/resolver in this layer.</b> A reader must never dispose it, never
        /// mutate it, and never retain it past the decode's scope; the layer frees it in <see cref="Dispose"/>,
        /// which <see cref="MvtTile.Dispose"/> drives. Reading through a store after that point is a
        /// use-after-free on this buffer — the same borrowed-lifetime contract <see cref="Geometry"/> already
        /// carries.</para>
        ///
        /// <para>For a feature-less (or tag-less) layer this is a <b>zero-length</b> buffer, not
        /// <c>default</c> — <see cref="MvtDecoder"/>'s flatten constructs it unconditionally (a zero total
        /// still allocates a zero-length <c>NativeArray</c>), so it is owned and freed here like any other.
        /// Consumers gate on <c>.Length</c>, not on <c>IsCreated</c>.</para></summary>
        internal NativeArray<uint> FeatureTagWords { get; private set; }

        private bool _featureTagsAdopted;

        /// <summary>Takes ownership of this layer's flattened tag-word buffer. <b>Callable exactly once</b> —
        /// unlike <see cref="AdoptGeometry"/> there is no feature-column lockstep check here, because
        /// <see cref="FeatureTagWords"/>'s length is a WORD count, not a feature count. The per-feature
        /// <c>(offset, count)</c> slice INTO this buffer lives in the separate feature-count columns
        /// <see cref="FeatureTagOffsets"/>/<see cref="FeatureTagLengths"/> (adopted via
        /// <see cref="AdoptFeatureTagColumns"/>), which a store reads by ordinal — so the words buffer
        /// itself carries no per-feature column for a mismatch to corrupt.</summary>
        internal void AdoptFeatureTagWords(NativeArray<uint> tagWords)
        {
            if (_featureTagsAdopted)
                throw new InvalidOperationException(
                    $"MvtLayer '{Name}' already owns its tag words. A layer's buffer is minted exactly " +
                    "once, inside the decode; adopting a second would orphan the first (the decoded " +
                    "tile's Dispose frees only what the layer currently holds).");

            _featureTagsAdopted = true;
            FeatureTagWords = tagWords;
        }

        /// <summary>Per-feature start index into <see cref="FeatureTagWords"/>, by layer ordinal — the
        /// (offset,count) columns a <see cref="DensePropertyStore"/> and the native filter VM both read a
        /// feature's tag slice from (via <see cref="MvtLayerPropertyResolver.TryGetFeatureSlice"/>), so the
        /// slice lives in one place instead of being copied onto 495 per-feature stores. BORROWED by the
        /// resolver; freed here in <see cref="Dispose"/>.</summary>
        internal NativeArray<int> FeatureTagOffsets { get; private set; }

        /// <summary>Per-feature word count into <see cref="FeatureTagWords"/>, by layer ordinal — the twin
        /// of <see cref="FeatureTagOffsets"/>.</summary>
        internal NativeArray<int> FeatureTagLengths { get; private set; }

        private bool _featureTagColumnsAdopted;

        /// <summary>Takes ownership of this layer's per-feature (offset,count) columns. <b>Callable exactly
        /// once</b>, and — unlike <see cref="AdoptFeatureTagWords"/> — with a feature-count lockstep check:
        /// these ARE per-feature columns indexed by ordinal, so a length mismatch would mis-slice every
        /// store, the same corruption <see cref="AdoptGeometry"/> guards against.</summary>
        internal void AdoptFeatureTagColumns(NativeArray<int> offsets, NativeArray<int> lengths)
        {
            if (_featureTagColumnsAdopted)
                throw new InvalidOperationException(
                    $"MvtLayer '{Name}' already owns its tag-slice columns — minted exactly once inside the decode.");
            if (offsets.Length != Features.Count || lengths.Length != Features.Count)
                throw new InvalidOperationException(
                    $"MvtLayer '{Name}' tag-slice columns ({offsets.Length}/{lengths.Length}) must match its " +
                    $"feature count ({Features.Count}) — a store indexes them by ordinal.");

            _featureTagColumnsAdopted = true;
            FeatureTagOffsets = offsets;
            FeatureTagLengths = lengths;
        }

        /// <summary>Frees this layer's buffers. Idempotent — but only because the disposed struct/array is
        /// written BACK: <see cref="TileGeometryBuffers.Dispose"/> clears its own <c>IsCreated</c> to make the
        /// second call a no-op, and each property getter hands out a COPY, so disposing the getter's result
        /// directly would free the underlying buffer and then leave this layer still claiming to own it — a
        /// double free on the next call.</summary>
        public void Dispose()
        {
            TileGeometryBuffers geometry = Geometry;
            geometry.Dispose();
            Geometry = geometry;

            NativeArray<uint> tags = FeatureTagWords;
            if (tags.IsCreated) tags.Dispose();
            FeatureTagWords = tags;

            NativeArray<int> offsets = FeatureTagOffsets;
            if (offsets.IsCreated) offsets.Dispose();
            FeatureTagOffsets = offsets;

            NativeArray<int> lengths = FeatureTagLengths;
            if (lengths.IsCreated) lengths.Dispose();
            FeatureTagLengths = lengths;

            NativeArray<MvtValueNative> values = Values;
            if (values.IsCreated) values.Dispose();
            Values = values;
        }

        /// <summary>Layer key table (MVT Layer field 3): string keys in declaration order.</summary>
        public readonly List<string> Keys = new List<string>();

        /// <summary>Layer value table (MVT Layer field 4): decoded variant values in declaration order, one
        /// shared <c>Allocator.Persistent</c> buffer per layer — mirrors <see cref="FeatureTagWords"/>'s
        /// ownership idiom exactly. String, float, double, int, uint, sint, bool variants are all mapped to
        /// the blittable <see cref="MvtValueNative"/> (String → a <see cref="ValueStrings"/> index; numerics
        /// → Number; bool → Bool) — reconstituted to the shared expression <see cref="Value"/> at the read
        /// boundary via <see cref="MvtValueNative.ToValue"/>.
        ///
        /// <para><b>BORROWED by every resolver/store in this layer</b> — same borrowed-lifetime contract as
        /// <see cref="FeatureTagWords"/>: never dispose, never mutate, never retain past the decode's scope;
        /// the layer frees it in <see cref="Dispose"/>. For a value-less layer this is a <b>zero-length</b>
        /// buffer, not <c>default</c> — <see cref="MvtDecoder"/> materializes it unconditionally; consumers
        /// gate on <c>.Length</c>, not <c>IsCreated</c>.</para></summary>
        public NativeArray<MvtValueNative> Values { get; private set; }

        /// <summary>The per-layer value-string side table: the index space a <see cref="ValueType.String"/>
        /// entry in <see cref="Values"/> resolves against. A plain GC field — no <c>Dispose</c> — adopted
        /// in lockstep with <see cref="Values"/> via <see cref="AdoptValues"/> so the id↔table pairing is a
        /// property of the type, not of the decoder remembering.</summary>
        internal string[] ValueStrings { get; private set; }

        private bool _valuesAdopted;

        /// <summary>Takes ownership of this layer's value table. <b>Callable exactly once</b> — mirrors
        /// <see cref="AdoptFeatureTagWords"/>: no feature-column lockstep check, because
        /// <see cref="Values"/>'s length is a value-table count, not a feature count (same reasoning
        /// <see cref="AdoptFeatureTagWords"/> documents).</summary>
        internal void AdoptValues(NativeArray<MvtValueNative> values, string[] valueStrings)
        {
            if (_valuesAdopted)
                throw new InvalidOperationException(
                    $"MvtLayer '{Name}' already owns its value table. A layer's buffer is minted exactly " +
                    "once, inside the decode; adopting a second would orphan the first (the decoded " +
                    "tile's Dispose frees only what the layer currently holds).");

            _valuesAdopted = true;
            Values = values;
            ValueStrings = valueStrings;
        }

        // ── ITileLayer — zero-copy: List<MvtFeature> satisfies IReadOnlyList<IFeature> by
        // IReadOnlyList<out T> covariance (MvtFeature : IFeature), so this is a forward, not a copy. ──
        string                       ITileLayer.Name    => Name;
        uint                         ITileLayer.Extent  => Extent;
        IReadOnlyList<IFeature>      ITileLayer.Features => Features;

        /// <summary>The string→id key hoist's capability carrier: this layer's key resolver, set by
        /// <see cref="MvtDecoder"/> once, alongside every feature's <see cref="MvtFeature.Store"/>.
        /// </summary>
        internal MvtLayerPropertyResolver DenseKeyResolver { get; set; }

        IFeatureKeyResolver IIndexedFeatureSource.KeyResolver => DenseKeyResolver;

        /// <summary>The native-filter capability seam's implementation — forwarded to
        /// <see cref="NativeFilterSelection.TryBind"/> so this layer stays a thin forwarder, matching how
        /// <see cref="IIndexedFeatureSource.KeyResolver"/> is forwarded above.</summary>
        INativeFeatureMatcher INativeFilterSource.TryBindNativeFilter(NativeFilterProgram program) =>
            NativeFilterSelection.TryBind(this, program);
    }

    public sealed class MvtTile : IDecodedTile
    {
        public readonly List<MvtLayer> Layers = new List<MvtLayer>();

        /// <summary>The layer of that name, or null. <b>An empty or null name is never a match</b>, even
        /// against a layer whose own name is empty or absent (a malformed tile can hold one — the MVT
        /// <c>name</c> field is required, and this decoder accepts its absence rather than rejecting the
        /// layer).
        ///
        /// <para>The guard exists because <c>SourceLayerResolver</c> no longer short-circuits on an empty
        /// <c>source-layer</c>: that accommodation moved into the tiles, so that a GeoJSON tile can answer
        /// "my sole layer" (the Style Spec says <c>source-layer</c> is unused for geojson sources) while an
        /// MVT tile keeps answering exactly what it answered before. Without it, a background or raster style
        /// layer — which carries no <c>source-layer</c> at all — could select a nameless vector layer's
        /// features.</para></summary>
        public MvtLayer GetLayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var l in Layers)
                if (l.Name == name) return l;
            return null;
        }

        ITileLayer IDecodedTile.GetLayer(string name) => GetLayer(name);

        /// <summary>Frees every layer's geometry. The tile owns its layers, the layers own their
        /// buffers, and this is the one place the chain is released — driven by the decode-provisioning
        /// reference count at its last reference (<c>SharedDisposable{IDecodedTile}.Release</c>), never by a
        /// consumer. Idempotent.</summary>
        public void Dispose()
        {
            for (int i = 0; i < Layers.Count; i++)
                Layers[i].Dispose();
        }
    }
}
