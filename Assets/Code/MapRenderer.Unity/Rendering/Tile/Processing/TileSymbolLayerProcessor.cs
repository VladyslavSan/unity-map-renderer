using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A3: the <see cref="ITileWorkerThenMainLayerProcessor"/> adapter around one symbol STYLE
    /// LAYER's share of a symbol tile build — one instance per symbol style layer per (source, tile) build
    /// (matching the mesh side's per-layer granularity). This is parity-safe because
    /// <see cref="StyledSymbolTileBuilder.ExtractLayers"/> and <see cref="StyledSymbolTileBuilder.ShapeAsync"/>
    /// are ALREADY per-layer loops, so N single-layer processors invoked in declared order reproduce the
    /// identical call sequence and label order as one N-layer call.
    ///
    /// <para>Worker step = <see cref="StyledSymbolTileBuilder.ExtractLayers"/> for this one layer (the
    /// moved form of the pre-A3 <c>SymbolLabelSubsystem.BuildTileAsync</c> decode+extract). Main tail =
    /// <see cref="StyledSymbolTileBuilder.ShapeAsync"/> for this layer's extraction, appending into the
    /// build's SHARED <see cref="LabelInstance"/> output list — every processor of one build writes into
    /// the SAME list object, so the committed list is the same shape <c>SymbolTileLabelStore.CompleteBuild</c>
    /// receives today (no concat step, no order ambiguity).</para>
    /// </summary>
    internal sealed class TileSymbolLayerProcessor : ITileWorkerThenMainLayerProcessor
    {
        private readonly StyledSymbolTileBuilder _builder;
        // Single-element wrappers for ExtractLayers' list-shaped parameters, hoisted ONCE at construction
        // (main thread, build start) — ProcessOnWorker allocates nothing beyond what today's per-layer loop
        // already allocates.
        private readonly SymbolStyle.StyleLayer[] _layerWrapper;
        private readonly int[]                    _materialIndexWrapper;
        private readonly List<LabelInstance>      _sharedOutput;
        // I5b: forwarded verbatim to ExtractLayers' spriteAtlas param — null (fetch not resolved yet, or no
        // 'sprite' URL) means this build extracts no icon labels; a tile kicked before the fetch resolves
        // self-heals on its next rebuild once SymbolLabelSubsystem's sheet is set (§I5b plan).
        private readonly SpriteAtlasView _spriteAtlas;

        // Set by ProcessOnWorker; null if the worker step never ran (an earlier processor in the same
        // dense pass faulted) — CompleteOnMainAsync's no-op path relies on ShapeAsync already returning
        // early on a null extraction list.
        private List<StyledSymbolTileBuilder.ExtractedLayer> _extracted;

        /// <param name="spriteAtlas">I5b: forwarded verbatim to <see cref="StyledSymbolTileBuilder.ExtractLayers"/>;
        /// null (the default) yields no icon labels — every pre-I5b caller (which omits this argument) stays
        /// byte-identical.</param>
        public TileSymbolLayerProcessor(
            StyledSymbolTileBuilder builder, SymbolStyle.StyleLayer layer, int materialIndex,
            List<LabelInstance> sharedOutput, SpriteAtlasView spriteAtlas = null)
        {
            _builder              = builder;
            _layerWrapper         = new[] { layer };
            _materialIndexWrapper = new[] { materialIndex };
            _sharedOutput         = sharedOutput;
            _spriteAtlas          = spriteAtlas;
        }

        public LayerPhase Phase => LayerPhase.WorkerThenMain;

        /// <summary>WORKER-SAFE (moved form of the pre-A3 <c>SymbolLabelSubsystem.BuildTileAsync</c>'s
        /// <c>ExtractLayers</c> call, one layer wide): SELECT + project this layer's <see cref="SymbolStyle.StyleLayer"/>
        /// off the main thread.</summary>
        public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
        {
            _extracted = _builder.ExtractLayers(
                tile, context.Tile, _layerWrapper, context.Zoom, context.Projection, _materialIndexWrapper,
                _spriteAtlas);
        }

        /// <summary>MAIN-THREAD tail (moved form of the pre-A3 <c>ShapeAsync</c> call, one layer wide): shape
        /// + lay out this layer's extraction and append into the build's shared output list. A null
        /// <see cref="_extracted"/> (worker never ran) is the no-op path — <c>ShapeAsync</c> already returns
        /// early on a null input.</summary>
        public UniTask CompleteOnMainAsync(CancellationToken ct)
            => _builder.ShapeAsync(_extracted, _sharedOutput, ct);
    }
}
