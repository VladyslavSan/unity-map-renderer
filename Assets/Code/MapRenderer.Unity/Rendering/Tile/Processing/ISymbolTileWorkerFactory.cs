using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A5b: the symbol-agnostic seam through which <see cref="Tile.TileManager"/> drives the
    /// symbol worker pass from its per-tile kick — the retired parallel push feed
    /// (<c>SymbolTileBytesReady</c>/<c>OnTileBytesReady</c>) is replaced by this factory. TileManager holds
    /// only this interface pair (<c>string</c>/<see cref="TileId"/>/<see cref="SharedDisposable{T}"/> types) —
    /// it never references a label/store/glyph type; the real implementor
    /// (<c>MapRenderer.Unity.Text.SymbolLabelSubsystem</c>) lives on the other side of the seam.
    /// </summary>
    internal interface ISymbolTileWorkerFactory
    {
        /// <summary>MAIN THREAD (kick time, <see cref="Tile.TileManager.PumpPending"/>): begin a symbol
        /// build for <paramref name="sourceId"/>/<paramref name="tile"/> if this source has symbol layers
        /// and the glyph pipeline is live. Returns null for no participation (a mesh-only source, or no
        /// glyph pipeline). Does the main-thread prologue: reserve the store slot + generation, capture
        /// camera zoom + projection, build the per-layer processors + context.</summary>
        ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile);
    }

    /// <summary>The handle <see cref="ISymbolTileWorkerFactory.TryBeginBuild"/> returns — carries one
    /// build's captured state from the kick (main thread) to the kick task (pool thread).</summary>
    internal interface ISymbolTileWorkerPass
    {
        /// <summary>POOL THREAD (inside the mesh kick task, after the mesh pass): run this build's symbol
        /// worker pass over the SAME shared decode, then hand the completed worker phase to the
        /// subsystem's main-thread tail pump (thread-safe). Infallible from the caller's view — owns its
        /// own fault domain, never rethrows (the kick site wraps the call too, belt-and-braces).</summary>
        void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode);
    }
}
