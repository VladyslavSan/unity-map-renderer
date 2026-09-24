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
    /// A decoded MVT feature: the geometry type, the property bag (as expression <see cref="Value"/>s, so
    /// filters read them without conversion) and the optional feature id (field 1, uint64).
    /// A feature carries no geometry: coordinates belong to the layer (<see cref="MvtLayer.Geometry"/>), and
    /// a feature is only an evaluation surface for filters and expressions.
    /// </summary>
    public sealed class MvtFeature : IFeature, IIndexedFeature
    {
        /// <summary>Geometry type (MVT Feature.type field).</summary>
        public TileGeometryType GeometryType { get; init; }

        /// <summary>
        /// Feature id from MVT Feature field 1 (uint64 varint), or <see cref="Value.Null"/> when absent.
        /// id=0 is a valid id per the MVT spec, so an absent id is never a zero <see cref="Value.Number"/>.
        /// Limitation: an id above 2^53 loses precision in the <c>double</c>; every consumer reads a double.
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

        // ── IFeature implementation ─────────────────────────────────────────────────────────────────

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

        /// <summary>This layer's rings, materialized once per decode by <see cref="MvtDecoder"/> and owned by
        /// the layer. Non-local invariant: every consumer borrows it and never disposes, mutates or retains it;
        /// <see cref="Dispose"/> frees it, and a second free is a real double free. It is <c>default</c> for a
        /// feature-less layer. <see cref="AdoptGeometry"/> sets it once, which keeps
        /// <c>FeatureCount == Features.Count</c> for the consumers that index by ordinal.</summary>
        public TileGeometryBuffers Geometry { get; private set; }

        private bool _geometryAdopted;

        /// <summary>Takes ownership of this layer's decoded buffer. It throws on a second call, which would
        /// orphan the first buffer, and on a feature column that does not match <see cref="Features"/>, which
        /// would mis-bucket every ordinal-indexed consumer. A <c>default</c> buffer is legal for a feature-less
        /// layer. Both guards live once, in <see cref="LayerGeometryAdoption"/>, shared by both
        /// <see cref="ITileLayer"/> implementations.</summary>
        internal void AdoptGeometry(TileGeometryBuffers geometry)
        {
            LayerGeometryAdoption.Validate(
                $"MvtLayer '{Name}'", _geometryAdopted, geometry, Features.Count);

            _geometryAdopted = true;
            Geometry         = geometry;
        }

        TileGeometryBuffers ITileLayer.Geometry => Geometry;

        /// <summary>This layer's flattened (keyIdx,valIdx) tag words, one shared Persistent buffer that every
        /// <see cref="DensePropertyStore"/> views. Non-local invariant: every store and resolver borrows it
        /// under the same contract as <see cref="Geometry"/>; a read after <see cref="Dispose"/> is a
        /// use-after-free. For a tag-less layer it is zero-length, not <c>default</c>, so consumers gate on
        /// <c>.Length</c>.</summary>
        internal NativeArray<uint> FeatureTagWords { get; private set; }

        private bool _featureTagsAdopted;

        /// <summary>Takes ownership of this layer's flattened tag-word buffer; it throws on a second call.
        /// It has no feature-count check because the length is a word count. The per-feature slices live in
        /// <see cref="FeatureTagOffsets"/>/<see cref="FeatureTagLengths"/>, which
        /// <see cref="AdoptFeatureTagColumns"/> checks.</summary>
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

        /// <summary>Layer value table (MVT Layer field 4) in declaration order, as blittable
        /// <see cref="MvtValueNative"/>s that <see cref="MvtValueNative.ToValue"/> converts at the read
        /// boundary. Non-local invariant: every store and resolver borrows it under the same contract as
        /// <see cref="FeatureTagWords"/>. For a value-less layer it is zero-length, not <c>default</c>, so
        /// consumers gate on <c>.Length</c>.</summary>
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

        /// <summary>The layer of that name, or null. An empty or null name never matches, even a nameless layer
        /// in a malformed tile. Non-obvious why: <c>SourceLayerResolver</c> does not short-circuit on an empty
        /// <c>source-layer</c>, so without this guard a background or raster style layer could select a
        /// nameless vector layer's features.</summary>
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
