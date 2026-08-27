using System;
using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// A decoded MVT feature: the geometry type, the decoded property bag (resolved from the layer's
    /// key/value tables via the feature's tag pairs) and the optional feature id (field 1, uint64).
    ///
    /// Properties are stored as <see cref="Value"/> using the Expressions type system so the filter
    /// and expression layers can consume them directly without an extra conversion step.
    ///
    /// Epic A / A6: implements <see cref="IFeature"/> explicitly — the fold of the retired
    /// <c>MvtFeatureAdapter</c>, verbatim (design §B-2). (IR C1 collapsed the zero-member
    /// <c>ITileFeature</c> that used to sit between the two.)
    ///
    /// <para><b>IR C1 P3: a feature carries NO geometry at all.</b> The <c>uint[] Geometry</c> command
    /// stream and the <c>IMvtGeometryCarrier</c> sidecar it hung off are both gone. Coordinates belong to
    /// the LAYER (<see cref="MvtLayer.Geometry"/>, materialized eagerly inside the decode), and a feature is
    /// purely an evaluation surface — filters and expressions, nothing else. The command words are consumed
    /// inside <c>MvtDecoder</c> and never outlive it.</para>
    /// </summary>
    public sealed class MvtFeature : IFeature, IIndexedFeature
    {
        /// <summary>Geometry type (MVT Feature.type field).</summary>
        public TileGeometryType GeometryType;

        /// <summary>
        /// Feature id decoded from MVT Feature field 1 (uint64 varint). Valid only when
        /// <see cref="HasId"/> is true. Note: id=0 is a valid feature id per the MVT spec —
        /// use <see cref="HasId"/> for presence, never compare Id to 0.
        ///
        /// Precision note: uint64 values larger than 2^53 lose precision when exposed as a
        /// <c>double</c> via <see cref="IFeature.Id"/> at the expression layer. The raw <c>ulong</c>
        /// here preserves the full value for any future consumer that reads it directly.
        /// </summary>
        public ulong Id;

        /// <summary>True when field 1 was present in the encoded feature message.</summary>
        public bool HasId;

        /// <summary>
        /// The property store backing <see cref="Properties"/> and <see cref="IFeature.TryGetProperty"/> —
        /// set by <see cref="MvtDecoder"/> once the layer's key/value tables are complete. Internal:
        /// production code goes through the decoder, never hand-assembles a store.
        /// </summary>
        internal IMvtPropertyStore Store { get; set; }

        /// <summary>Decoded properties, materialized from <see cref="Store"/>. Always non-null — an empty
        /// dictionary when <see cref="Store"/> is unset.</summary>
        public IReadOnlyDictionary<string, Value> Properties => Store?.AsDictionary() ?? EmptyProperties;

        private static readonly Dictionary<string, Value> EmptyProperties = new Dictionary<string, Value>();

        // ── IFeature — the retired MvtFeatureAdapter's semantics, folded verbatim ────────────────────

        TileGeometryType IFeature.GeometryType => GeometryType;
        bool             IFeature.HasId        => HasId;

        /// <remarks>Returns <see cref="Value.Number"/> of the uint64 id cast to double. Values larger
        /// than 2^53 lose precision at this boundary; the raw <see cref="Id"/> field preserves the full
        /// value for any future non-double consumer. (MvtFeatureAdapter.cs:42, folded verbatim.)</remarks>
        Value IFeature.Id => HasId ? Value.Number((double)Id) : Value.Null;

        bool IFeature.TryGetProperty(string name, out Value value)
        {
            if (Store != null) return Store.TryGet(name, out value);
            value = Value.Null;
            return false;
        }

        IReadOnlyDictionary<string, Value> IFeature.Properties => Properties;

        /// <summary>The string→id key hoist's int-keyed read: forwards to <see cref="Store"/>.</summary>
        bool IIndexedFeature.TryGetPropertyByKeyIndex(int keyIndex, out Value value)
        {
            if (Store != null) return Store.TryGetByKeyIndex(keyIndex, out value);
            value = Value.Null;
            return false;
        }
    }

    /// <summary>
    /// A decoded MVT layer. Carries the layer name, extent, version, the decoded key table
    /// (field 3, ordered), the decoded value table (field 4, ordered), the feature list, this layer's
    /// decoded geometry (<see cref="Geometry"/>, since IR C1 P3) and its flattened tag words
    /// (<see cref="FeatureTagWords"/>).
    /// </summary>
    public sealed class MvtLayer : ITileLayer, IIndexedFeatureSource, IDisposable
    {
        public string Name;
        public uint Extent = 4096;
        public uint Version = 1;
        public readonly List<MvtFeature> Features = new List<MvtFeature>();

        /// <summary>IR C1 P3: this layer's rings, materialized EAGERLY by <see cref="MvtDecoder"/> from its
        /// features' command streams and owned by this layer for its whole life. One buffer per source-layer
        /// per DECODE — not per pass and not per consumer — which is what retired <c>TileGeometryStore</c>:
        /// the memo the store kept is now the field it memoized into.
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
        /// <para><b>Set once, through <see cref="AdoptGeometry"/> — IR C1 fix stage.</b> This used to be a
        /// public mutable field, so <c>FeatureCount == Features.Count</c> — the lockstep three consumers size
        /// their per-feature columns from and index by <c>SelectedTileFeature.Ordinal</c> — was enforced by
        /// one writer's discipline and by nothing structural.</para></summary>
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
        /// <para>The two guards themselves live once, in <see cref="TileLayerGeometryAdoption"/> — the second
        /// <see cref="ITileLayer"/> now exists, and an invariant three ordinal-indexed consumers rely on must
        /// have one statement rather than two that can drift.</para></summary>
        internal void AdoptGeometry(TileGeometryBuffers geometry)
        {
            TileLayerGeometryAdoption.Validate(
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
        /// <para><c>default</c> (<c>IsCreated == false</c>) for a layer with no features, allocating
        /// nothing.</para></summary>
        internal NativeArray<uint> FeatureTagWords { get; private set; }

        private bool _featureTagsAdopted;

        /// <summary>Takes ownership of this layer's flattened tag-word buffer. <b>Callable exactly once</b> —
        /// unlike <see cref="AdoptGeometry"/> there is no feature-column lockstep check here, because
        /// <see cref="FeatureTagWords"/>'s length is a WORD count, not a feature count: per-feature
        /// <c>(offset, count)</c> pairs live on the stores themselves (never on the layer), so there is no
        /// per-feature column on this buffer for a mismatch to corrupt.</summary>
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
        /// the layer frees it in <see cref="Dispose"/>. <c>default</c> (<c>IsCreated == false</c>) for a
        /// layer with no values, allocating nothing.</para></summary>
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

        /// <summary>IR C1 P3: frees every layer's geometry. The tile owns its layers, the layers own their
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
