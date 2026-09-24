using System.Collections.Generic;
using System.Threading;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The <see cref="ITileWorkerThenMainLayerProcessor"/> for one symbol style layer of a (source, tile) build.
    /// Worker step: <see cref="StyledSymbolTileBuilder.ExtractLayers"/>; main tail:
    /// <see cref="StyledSymbolTileBuilder.Shape"/>. Both are per-layer loops, so N processors in SLOT order give
    /// the same symbol order as one N-layer call. Every processor of a build appends to the same shared
    /// <see cref="SymbolTileBuffer"/>, so <c>SymbolTileStore.CompleteBuild</c> receives one buffer, no concat.
    /// </summary>
    internal sealed class TileSymbolLayerProcessor : ITileWorkerThenMainLayerProcessor
    {
        private readonly StyledSymbolTileBuilder _builder;
        // Single-element wrappers for ExtractLayers' list parameters, built once at construction, so
        // ProcessOnWorker allocates nothing beyond what ExtractLayers allocates.
        private readonly SymbolStyle.StyleLayer[] _layerWrapper;
        private readonly int[]                    _materialIndexWrapper;
        private readonly SymbolTileBuffer       _sharedBuffer;
        // Non-local invariant: TryBeginBuild parks a build until SymbolSubsystem.SpritesSettled, so this is
        // non-null whenever the style resolved a sheet. Null (inert downstream) means no 'sprite' URL, no
        // sheet, or a fetch past SpriteFetchDeadlineSeconds (docs/road-shields-design.md).
        private readonly SpriteAtlasView _spriteAtlas;

        // Null if the worker step never ran (an earlier processor faulted); CompleteOnMain's no-op path
        // relies on Shape returning early on a null extraction list.
        private List<StyledSymbolTileBuilder.ExtractedLayer> _extracted;

        /// <param name="spriteAtlas">Forwarded verbatim to <see cref="StyledSymbolTileBuilder.ExtractLayers"/>;
        /// null (the default) yields no icon symbols, so omitting this argument is behaviour-preserving.</param>
        public TileSymbolLayerProcessor(
            StyledSymbolTileBuilder builder, SymbolStyle.StyleLayer layer, int materialIndex,
            SymbolTileBuffer sharedBuffer, SpriteAtlasView spriteAtlas = null)
        {
            _builder              = builder;
            _layerWrapper         = new[] { layer };
            _materialIndexWrapper = new[] { materialIndex };
            _sharedBuffer        = sharedBuffer;
            _spriteAtlas          = spriteAtlas;
        }

        public LayerPhase Phase => LayerPhase.WorkerThenMain;

        /// <summary>WORKER-SAFE: SELECT + project this layer's <see cref="SymbolStyle.StyleLayer"/>
        /// off the main thread.</summary>
        /// <remarks>No store parameter. The extractor reads each symbol layer's source-layer buffer
        /// off the decoded tile itself, so every symbol layer of this build — and every mesh layer of the
        /// same kick — reads the SAME buffer per source-layer with nothing to thread through.</remarks>
        public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
        {
            _extracted = _builder.ExtractLayers(
                tile, context.Tile, _layerWrapper, context.Zoom, context.Projection, _materialIndexWrapper,
                _spriteAtlas);
        }

        /// <summary>MAIN-THREAD tail, synchronous: shape + lay out this layer's extraction and append into
        /// the build's shared output list. A null <see cref="_extracted"/> (worker never ran) is the no-op
        /// path — <c>Shape</c> already returns early on a null input.</summary>
        public void CompleteOnMain(CancellationToken ct)
            => _builder.Shape(_extracted, _sharedBuffer, ct);

        /// <summary>MAIN-THREAD, after the worker step (<see cref="ProcessOnWorker"/> must already have run):
        /// collects this layer's glyph-range requests into the build-wide <paramref name="into"/>/<paramref
        /// name="seen"/> pair. Called from <see cref="SymbolSubsystem"/>'s tail, before any processor's
        /// <see cref="CompleteOnMain"/> — the caller establishing that precondition. Exists on this concrete
        /// class only; not part of <see cref="ITileWorkerThenMainLayerProcessor"/>.</summary>
        public void CollectRequiredRanges(
            List<(string FontName, int RangeStart)> into, HashSet<(string FontName, int RangeStart)> seen)
            => _builder.CollectRequiredRanges(_extracted, into, seen);
    }
}
