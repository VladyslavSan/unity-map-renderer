using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// Wraps an <see cref="IDataSource"/> whose tiles are addressed in the TMS scheme (Y grows northward)
    /// instead of XYZ (Y grows southward). Flips only the fetch address — <c>y' = (1&lt;&lt;z) - 1 - y</c> —
    /// so every other identity (loaded/cache keys, placement) keeps the XYZ tile the caller asked for.
    /// </summary>
    internal sealed class TmsYFlipDataSource : IDataSource
    {
        private readonly IDataSource _inner;

        public TmsYFlipDataSource(IDataSource inner) => _inner = inner;

        public TileEncoding Encoding => _inner.Encoding;

        public UniTask<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default)
        {
            int tmsY = (1 << coord.Z) - 1 - coord.Y;
            return _inner.FetchAsync(new TileId { Z = coord.Z, X = coord.X, Y = tmsY }, ct);
        }

        public void Dispose() => _inner.Dispose();
    }
}
