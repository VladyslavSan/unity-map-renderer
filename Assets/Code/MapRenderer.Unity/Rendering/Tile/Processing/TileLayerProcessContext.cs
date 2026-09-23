using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The per-<c>(tile, source)</c> worker-pass inputs shared by every
    /// <see cref="ITileLayerProcessor"/> invoked for one mesh build kick: <c>id</c>, the tile's integer
    /// zoom, its projected render origin, and the active projection. Larger than 16 bytes and read-only, so callers
    /// take it by <c>in</c> per the project's struct-passing convention.
    /// </summary>
    internal readonly struct TileLayerProcessContext
    {
        /// <summary>The tile address being built.</summary>
        public TileId Tile { get; init; }

        /// <summary>The evaluation zoom for THIS worker pass — the source differs by cadence. The mesh pass
        /// (<see cref="TileLayerProcessorRunner.RunWorkerPass"/>) and a background tile's graph kick bake at
        /// the tile's INTEGER zoom (<c>id.Z</c>); the symbol pass
        /// (<see cref="TileLayerProcessorRunner.RunSymbolWorkerPass"/>) evaluates at the fractional CAMERA
        /// zoom captured at build start, because symbol layout/paint is evaluated at display zoom. The
        /// shared decode feed (<c>SharedDisposable{IDecodedTile}</c>) carries no zoom, so sharing the
        /// decoded tile across cadences cannot conflate their zoom sources.</summary>
        public double Zoom { get; init; }

        /// <summary>The tile's SW-corner projected render origin — shared by the mesh bake and the
        /// tile transform.</summary>
        public double3 TileOriginRender { get; init; }

        /// <summary>The active pixel↔ground projection for this worker pass, cached once per Tick.</summary>
        public IProjection Projection { get; init; }

        /// <summary>How much of the tile's MVT buffer the fill mesh keeps — the single global knob, read live
        /// off <c>MapViewConfig</c> each Tick. <c>default</c> ⇒ disabled ⇒ the whole buffer is drawn.</summary>
        public TileBufferClip BufferClip { get; init; }

        /// <summary>This build's rented <see cref="TileBuildBuffers"/> — populated by
        /// <see cref="TileLayerProcessorRunner.RunWorkerPass"/> for the duration of its worker pass,
        /// <c>null</c> everywhere else (tests, the symbol cadence, any context built outside that entry). A
        /// processor that reads a <c>null</c> Buffers must fall back to allocating its own — never assume
        /// this is non-null.</summary>
        public TileBuildBuffers Buffers { get; init; }
    }
}
