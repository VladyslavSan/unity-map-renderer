using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A1: the per-<c>(tile, source)</c> worker-pass inputs shared by every
    /// <see cref="ITileLayerProcessor"/> invoked for one mesh build kick — the values
    /// <c>TileManager.KickMeshBuild</c> used to close over directly (<c>id</c>, the tile's integer zoom, its
    /// projected render origin, and the active projection). Larger than 16 bytes and read-only, so callers
    /// take it by <c>in</c> per the project's struct-passing convention.
    /// </summary>
    internal readonly struct TileLayerProcessContext
    {
        /// <summary>The tile address being built.</summary>
        public TileId Tile { get; init; }

        /// <summary>The evaluation zoom for THIS worker pass — the source differs by cadence, not a single
        /// project-wide rule. Mesh passes (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>/
        /// <see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/>) bake at the tile's INTEGER zoom
        /// (<c>id.Z</c> — S82 Decision 2, unchanged). The symbol pass
        /// (<see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>) evaluates at the fractional CAMERA
        /// zoom captured at build start (pre-A3 parity — symbol layout/paint always evaluated at display
        /// zoom). A4 keeps BOTH meanings deliberately (design doc §B Q4): the shared decode feed
        /// (<see cref="IDecodedTileHandle"/>) carries only <c>{bytes → IDecodedTile}</c>, no zoom, no context, so
        /// sharing the decoded tile across cadences cannot conflate their zoom sources — reconciling the
        /// two belongs to the stage that merges the cadences themselves (A5+), not to the feed.</summary>
        public double Zoom { get; init; }

        /// <summary>S91-C: the tile's SW-corner projected render origin — shared by the mesh bake and the
        /// tile transform.</summary>
        public double3 TileOriginRender { get; init; }

        /// <summary>The active pixel↔ground projection for this worker pass (S91-C: cached once per Tick).</summary>
        public IProjection Projection { get; init; }
    }
}
