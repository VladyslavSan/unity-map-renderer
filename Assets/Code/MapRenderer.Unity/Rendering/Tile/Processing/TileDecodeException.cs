using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// A tile's decode threw. Raised only by <see cref="TileDecodeDispatch.DecodeAsync"/>, so it is the one
    /// exception type that means "the bytes (or the dataset) are bad", as distinct from "the fetch failed".
    /// Non-obvious why: a decode fault arrives on the <c>GetTile</c> task, the same channel as a 5xx.
    /// This type lets <c>TileManager.TakeDecodeFromFetch</c> give it its own bounded log, not hide it
    /// in the throttled fetch-failure counter. The original exception is the
    /// <see cref="System.Exception.InnerException"/>.
    /// </summary>
    internal sealed class TileDecodeException : System.Exception
    {
        internal TileDecodeException(TileId id, System.Exception inner)
            : base($"decode failed for tile {id}: {inner.Message}", inner) { }
    }
}
