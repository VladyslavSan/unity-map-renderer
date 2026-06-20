using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Mvt;

namespace MapRenderer.Core.Filters
{
    /// <summary>
    /// Adapts a decoded <see cref="MvtFeature"/> to the <see cref="IFeature"/> surface required by the
    /// expression evaluator and the filter layer.
    ///
    /// All three feature-data dimensions are now live:
    /// <list type="bullet">
    ///   <item><see cref="GeometryType"/> — decoded MVT geometry type.</item>
    ///   <item><see cref="Properties"/> / <see cref="TryGetProperty"/> — decoded per-feature properties
    ///     resolved from the layer's key/value tables (S39).</item>
    ///   <item><see cref="HasId"/> / <see cref="Id"/> — decoded MVT feature id (field 1, uint64, S39).
    ///     HasId is set by field-1 presence (id=0 is a valid id); Id is exposed as Value.Number(double)
    ///     for the expression layer.</item>
    /// </list>
    /// </summary>
    public sealed class MvtFeatureAdapter : IFeature
    {
        private readonly MvtFeature _feature;

        public MvtFeatureAdapter(MvtFeature feature)
        {
            _feature = feature;
        }

        /// <inheritdoc/>
        public MvtGeometryType GeometryType => _feature.GeometryType;

        /// <inheritdoc/>
        public bool HasId => _feature.HasId;

        /// <inheritdoc/>
        /// <remarks>
        /// Returns <see cref="Value.Number"/> of the uint64 id cast to double. Values larger than
        /// 2^53 lose precision at this boundary; the raw <c>ulong</c> on <see cref="MvtFeature.Id"/>
        /// preserves the full value for any future non-double consumer.
        /// </remarks>
        public Value Id => _feature.HasId ? Value.Number((double)_feature.Id) : Value.Null;

        /// <inheritdoc/>
        public bool TryGetProperty(string name, out Value value)
        {
            if (_feature.Properties != null && _feature.Properties.TryGetValue(name, out value))
                return true;
            value = Value.Null;
            return false;
        }

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, Value> Properties =>
            (IReadOnlyDictionary<string, Value>)_feature.Properties
            ?? new Dictionary<string, Value>();
    }
}
