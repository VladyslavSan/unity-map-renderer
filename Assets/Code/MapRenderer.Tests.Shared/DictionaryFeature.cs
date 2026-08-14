// A test double, so it lives in the test assembly rather than in engine-free Core (where it shipped
// until the test-only-code cleanup). Engine-free, so core-tests compiles it too.

using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A simple in-memory <see cref="IFeature"/> built from a dictionary — lets the expression engine and
    /// filter layer be exercised against synthetic features instead of decoded MVT ones.
    ///
    /// <para>Also implements <see cref="ITileCommandStreamFeature"/> (an optional <see cref="Geometry"/>
    /// command stream), so the same double serves the mesh builders and can be handed to
    /// <c>MvtGeometryMaterializer</c> through <c>InMemoryTileLayer</c>. Leave <see cref="Geometry"/> null for
    /// pure expression/filter tests: a null stream materializes as zero commands, so such a feature
    /// contributes no ring, exactly as a real geometry-less feature does.
    /// <b>IR C1 P3:</b> the interface this used to name was production's <c>IMvtGeometryCarrier</c>, deleted
    /// when the decoded LAYER took ownership of geometry; the replacement is test-assembly-only.</para>
    /// </summary>
    public sealed class DictionaryFeature : IFeature, ITileCommandStreamFeature
    {
        private readonly Dictionary<string, Value> _properties;

        public TileGeometryType GeometryType { get; }
        public bool HasId { get; }
        public Value Id { get; }

        /// <summary>MVT command-stream geometry, or null when this double is only standing in for the
        /// property bag.</summary>
        public uint[] Geometry { get; }

        public DictionaryFeature(
            IReadOnlyDictionary<string, Value> properties = null,
            TileGeometryType geometryType = TileGeometryType.Unknown,
            bool hasId = false,
            Value id = default,
            uint[] geometry = null)
        {
            Geometry = geometry;
            _properties = new Dictionary<string, Value>();
            if (properties != null)
                foreach (var kv in properties)
                    _properties[kv.Key] = kv.Value;
            GeometryType = geometryType;
            HasId = hasId;
            Id = hasId ? id : Value.Null;
        }

        public bool TryGetProperty(string name, out Value value)
            => _properties.TryGetValue(name ?? string.Empty, out value);

        public IReadOnlyDictionary<string, Value> Properties => _properties;
    }
}
