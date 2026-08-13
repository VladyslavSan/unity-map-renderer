using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// A tile's decode threw. Raised only by <see cref="TileDecodeDispatch.DecodeAsync"/>, so it is the one
    /// exception type that means "the bytes (or the dataset) are bad", as distinct from "the fetch failed".
    ///
    /// <para><b>Why a distinct type and not the raw decoder exception.</b> Under eager decode the fault
    /// arrives at the coordinator on the <c>GetTile</c> task — the same channel a 5xx arrives on. Routing
    /// both into one throttled counter would let a genuinely malformed tile hide behind 64 unrelated network
    /// errors and would log it as "tile fetch failed", which is a lie. This type is what lets
    /// <c>TileManager.TakeDecodeFromFetch</c> tell the two apart and give the decode fault its own bounded
    /// log. The original exception is preserved as <see cref="System.Exception.InnerException"/>.</para>
    /// </summary>
    internal sealed class TileDecodeException : System.Exception
    {
        internal TileDecodeException(TileId id, System.Exception inner)
            : base($"decode failed for tile {id}: {inner.Message}", inner) { }
    }
}
