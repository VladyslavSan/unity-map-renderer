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
    /// The <see cref="ITileWorkerThenMainLayerProcessor"/> adapter around one symbol STYLE LAYER's share of
    /// a symbol tile build — one instance per symbol style layer per (source, tile) build (matching the
    /// mesh side's per-layer granularity). Parity-safe because
    /// <see cref="StyledSymbolTileBuilder.ExtractLayers"/> and <see cref="StyledSymbolTileBuilder.Shape"/>
    /// are already per-layer loops, so N single-layer processors invoked in SLOT order reproduce the
    /// identical call sequence and symbol order as one N-layer call. Worker step =
    /// <see cref="StyledSymbolTileBuilder.ExtractLayers"/>; main tail =
    /// <see cref="StyledSymbolTileBuilder.Shape"/>, appending into the build's SHARED
    /// <see cref="SymbolTileBuffer"/>. Every processor of one build writes into the SAME instance (the
    /// pairing-adjacency rule), so <c>SymbolTileStore.CompleteBuild</c> receives one buffer in symbol order,
    /// with no concat step.
    /// </summary>
    internal sealed class TileSymbolLayerProcessor : ITileWorkerThenMainLayerProcessor
    {
        private readonly StyledSymbolTileBuilder _builder;
        // Single-element wrappers for ExtractLayers' list-shaped parameters, hoisted ONCE at construction
        // (main thread, build start), so ProcessOnWorker allocates nothing beyond what ExtractLayers itself
        // allocates.
        private readonly SymbolStyle.StyleLayer[] _layerWrapper;
        private readonly int[]                    _materialIndexWrapper;
        private readonly SymbolTileBuffer       _sharedBuffer;
        // Forwarded verbatim to ExtractLayers' spriteAtlas param (docs/road-shields-design.md): a build's
        // worker step only ever runs once SymbolSubsystem.SpritesSettled is true — TryBeginBuild PARKS a
        // build kicked before the sprite fetch settles instead of constructing this processor. Non-null here
        // whenever the style resolved a sheet; null (and inert — every downstream icon path already guards
        // on it) for a style with no 'sprite' URL, an absent sheet, or a fetch that never resolved before
        // SpriteFetchDeadlineSeconds elapsed ("settled" then means "gave up waiting").
        private readonly SpriteAtlasView _spriteAtlas;

        // Set by ProcessOnWorker; null if the worker step never ran (an earlier processor in the same
        // dense pass faulted) — CompleteOnMain's no-op path relies on Shape returning early on a null
        // extraction list.
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
