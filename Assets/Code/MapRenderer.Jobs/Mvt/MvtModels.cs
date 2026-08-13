using System;
using System.Collections.Generic;
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
    public sealed class MvtFeature : IFeature
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
        /// Decoded properties resolved from the layer's key/value tables. Populated by
        /// <see cref="MvtDecoder"/> after the full layer message is read (two-pass: raw tags are
        /// held until keys/values are complete, then resolved). Always non-null.
        /// </summary>
        public Dictionary<string, Value> Properties = new Dictionary<string, Value>();

        // ── IFeature — the retired MvtFeatureAdapter's semantics, folded verbatim ────────────────────

        TileGeometryType IFeature.GeometryType => GeometryType;
        bool             IFeature.HasId        => HasId;

        /// <remarks>Returns <see cref="Value.Number"/> of the uint64 id cast to double. Values larger
        /// than 2^53 lose precision at this boundary; the raw <see cref="Id"/> field preserves the full
        /// value for any future non-double consumer. (MvtFeatureAdapter.cs:42, folded verbatim.)</remarks>
        Value IFeature.Id => HasId ? Value.Number((double)Id) : Value.Null;

        bool IFeature.TryGetProperty(string name, out Value value)
        {
            if (Properties != null && Properties.TryGetValue(name, out value))
                return true;
            value = Value.Null;
            return false;
        }

        IReadOnlyDictionary<string, Value> IFeature.Properties =>
            (IReadOnlyDictionary<string, Value>)Properties ?? new Dictionary<string, Value>();
    }

    /// <summary>
    /// A decoded MVT layer. Carries the layer name, extent, version, the decoded key table
    /// (field 3, ordered), the decoded value table (field 4, ordered), the feature list — and, since
    /// IR C1 P3, <b>this layer's decoded geometry</b> (<see cref="Geometry"/>).
    /// </summary>
    public sealed class MvtLayer : ITileLayer, IDisposable
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

        /// <summary>Frees this layer's buffer. Idempotent — but only because the disposed struct is written
        /// BACK: <see cref="TileGeometryBuffers.Dispose"/> clears its own <c>IsCreated</c> to make the second
        /// call a no-op, and a property getter hands out a COPY, so <c>Geometry.Dispose()</c> would free the
        /// arrays and then leave this layer still claiming to own them — a double free on the next call.</summary>
        public void Dispose()
        {
            TileGeometryBuffers geometry = Geometry;
            geometry.Dispose();
            Geometry = geometry;
        }

        /// <summary>Layer key table (MVT Layer field 3): string keys in declaration order.</summary>
        public readonly List<string> Keys = new List<string>();

        /// <summary>
        /// Layer value table (MVT Layer field 4): decoded variant values in declaration order.
        /// String, float, double, int, uint, sint, bool variants are all mapped to
        /// <see cref="Value"/> (String → Value.String; numerics → Value.Number; bool → Value.Bool).
        /// </summary>
        public readonly List<Value> Values = new List<Value>();

        // ── ITileLayer — zero-copy: List<MvtFeature> satisfies IReadOnlyList<IFeature> by
        // IReadOnlyList<out T> covariance (MvtFeature : IFeature), so this is a forward, not a copy. ──
        string                       ITileLayer.Name    => Name;
        uint                         ITileLayer.Extent  => Extent;
        IReadOnlyList<IFeature>      ITileLayer.Features => Features;
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
