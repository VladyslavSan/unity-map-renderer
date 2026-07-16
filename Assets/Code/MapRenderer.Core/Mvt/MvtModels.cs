using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Core.Mvt
{
    /// <summary>
    /// A decoded MVT feature. Carries the geometry type, the raw geometry command stream, and
    /// the decoded property bag (resolved from the layer's key/value tables via the feature's tag
    /// pairs) plus the optional feature id (field 1, uint64).
    ///
    /// Properties are stored as <see cref="Value"/> using the Expressions type system so the filter
    /// and expression layers can consume them directly without an extra conversion step.
    ///
    /// Epic A / A6: implements <see cref="ITileFeature"/> (hence <see cref="IFeature"/>) explicitly —
    /// the fold of the retired <c>MvtFeatureAdapter</c>, verbatim (design §B-2). The public fields above
    /// are untouched (decode still writes them); the explicit members below are the only new surface.
    /// </summary>
    public sealed class MvtFeature : ITileFeature
    {
        /// <summary>Geometry type (MVT Feature.type field).</summary>
        public TileGeometryType GeometryType;

        /// <summary>Raw command stream; decode with <see cref="MvtGeometry.Decode"/>.</summary>
        public uint[] Geometry;

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

        // ── ITileFeature / IFeature — the retired MvtFeatureAdapter's semantics, folded verbatim ──────

        TileGeometryType IFeature.GeometryType => GeometryType;
        bool             IFeature.HasId        => HasId;
        uint[]           ITileFeature.Geometry => Geometry;

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
    /// (field 3, ordered), the decoded value table (field 4, ordered), and the feature list.
    /// </summary>
    public sealed class MvtLayer : ITileLayer
    {
        public string Name;
        public uint Extent = 4096;
        public uint Version = 1;
        public readonly List<MvtFeature> Features = new List<MvtFeature>();

        /// <summary>Layer key table (MVT Layer field 3): string keys in declaration order.</summary>
        public readonly List<string> Keys = new List<string>();

        /// <summary>
        /// Layer value table (MVT Layer field 4): decoded variant values in declaration order.
        /// String, float, double, int, uint, sint, bool variants are all mapped to
        /// <see cref="Value"/> (String → Value.String; numerics → Value.Number; bool → Value.Bool).
        /// </summary>
        public readonly List<Value> Values = new List<Value>();

        // ── ITileLayer — zero-copy: List<MvtFeature> satisfies IReadOnlyList<ITileFeature> by
        // IReadOnlyList<out T> covariance (MvtFeature : ITileFeature), so this is a forward, not a copy. ──
        string                       ITileLayer.Name    => Name;
        uint                         ITileLayer.Extent  => Extent;
        IReadOnlyList<ITileFeature>  ITileLayer.Features => Features;
    }

    public sealed class MvtTile : IDecodedTile
    {
        public readonly List<MvtLayer> Layers = new List<MvtLayer>();

        public MvtLayer GetLayer(string name)
        {
            foreach (var l in Layers)
                if (l.Name == name) return l;
            return null;
        }

        ITileLayer IDecodedTile.GetLayer(string name) => GetLayer(name);
    }
}
