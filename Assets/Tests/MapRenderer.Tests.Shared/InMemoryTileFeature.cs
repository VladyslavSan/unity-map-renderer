// A test double, so it lives in the test assembly rather than in production code. Engine-free, so
// core-tests compiles it too.

using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A minimal, engine-free <see cref="IFeature"/> carrier for synthesized (source-less) features that
    /// express their geometry as an MVT command stream. No id, no properties (its original user, the
    /// background quad, had constant paint that was never data-driven).
    ///
    /// <para><b>Zero production callers as of IR B5</b> — its only one was
    /// <c>BackgroundQuad</c>'s background quad, which now synthesizes tile-local corners
    /// directly instead of hand-authoring MVT commands. IR C1 moved it here, next to
    /// <see cref="DictionaryFeature"/>, when the tile-decode seam left <c>MapRenderer.Core</c>.</para>
    ///
    /// <para>Plain <c>{ get; set; }</c>, not <c>init</c>: MapRenderer.Tests.EditMode has no
    /// <c>IsExternalInit</c> polyfill of its own, matching this assembly's other test-owned carriers.</para>
    /// </summary>
    public sealed class InMemoryTileFeature : IFeature, ITileCommandStreamFeature
    {
        public TileGeometryType GeometryType { get; set; }

        /// <summary>MVT command-stream geometry (<see cref="ITileCommandStreamFeature"/>), or null.</summary>
        public uint[]           Geometry     { get; set; }

        public Value  Id    => Value.Null;

        public bool TryGetProperty(string name, out Value value)
        {
            value = Value.Null;
            return false;
        }

        public IReadOnlyDictionary<string, Value> Properties { get; } = new Dictionary<string, Value>();
    }
}
