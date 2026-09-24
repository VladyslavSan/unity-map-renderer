// Unity EditMode only — the tiles it tracks own NativeArray buffers. NOT registered in core-tests.csproj.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Builds synthetic <see cref="InMemoryDecodedTile"/>s and keeps them alive until the test that made them
    /// ends. Non-obvious why: a decoded tile owns <c>Allocator.Persistent</c> buffers, and leak detection is
    /// off in the batch gate, so a missed release is invisible; a one-line <c>[TearDown]</c> per fixture makes
    /// the release a property of the fixture, not of each helper call site.
    /// Test-thread only: fixtures build tiles in the test body, even when a pool task consumes them.
    /// </summary>
    public static class TestDecodedTiles
    {
        private static readonly List<IDisposable> Live = new List<IDisposable>();

        /// <summary>A one-layer decoded tile, tracked for release at <see cref="DisposeAll"/>.</summary>
        public static InMemoryDecodedTile Of(
            string layerName, TileId tile, IReadOnlyList<IFeature> features, uint extent = 4096)
            => Track(new InMemoryDecodedTile(new InMemoryTileLayer(layerName, tile, features, extent)));

        /// <summary>A multi-layer decoded tile, tracked for release at <see cref="DisposeAll"/>.</summary>
        public static InMemoryDecodedTile OfLayers(params InMemoryTileLayer[] layers)
            => Track(new InMemoryDecodedTile(layers));

        /// <summary>Tracks an externally built tile (any <see cref="IDecodedTile"/> — a real
        /// <c>MvtTile</c> decoded from a fixture is disposable for the same reason).</summary>
        public static T Track<T>(T tile) where T : IDecodedTile
        {
            Live.Add(tile);
            return tile;
        }

        /// <summary>Frees every tracked tile. Call from a fixture's <c>[TearDown]</c>.</summary>
        public static void DisposeAll()
        {
            for (int i = 0; i < Live.Count; i++) Live[i].Dispose();
            Live.Clear();
        }
    }
}
