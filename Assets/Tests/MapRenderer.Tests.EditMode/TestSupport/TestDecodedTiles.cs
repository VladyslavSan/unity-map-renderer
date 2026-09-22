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
    /// ends.
    ///
    /// <para><b>Why this exists.</b> A decoded tile now owns <c>Allocator.Persistent</c> buffers, so
    /// the ~10 symbol/line fixture files that used to build a plain managed <c>ITileLayer</c> would each start
    /// leaking native memory unless every one of their ~60 helper call sites grew a <c>using</c>. Leak
    /// detection is off in the batch gate, so those leaks would be <b>invisible</b> — precisely the failure
    /// class this helper exists to prevent. One tracked factory plus a one-line <c>[TearDown]</c> per fixture keeps the
    /// helpers <c>static</c> and the call sites unchanged, and makes the release a property of the fixture
    /// rather than of each author's memory.</para>
    ///
    /// <para>Test-thread only: EditMode fixtures build their tiles synchronously in the test body, even when
    /// they later hand them to a pool task.</para>
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
