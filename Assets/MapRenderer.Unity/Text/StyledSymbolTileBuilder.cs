// Engine-free despite living under Assets/MapRenderer.Unity/Text (like GlyphManager): references only
// MapRenderer.Core.* + UniTask, NO `using UnityEngine`. It is co-located with GlyphManager (which it needs,
// and which is in this assembly) and is compiled by BOTH the Unity EditMode runner and Tools/core-tests
// (the matching <Compile Include> lives in Tools/core-tests/core-tests.csproj). Do NOT add a UnityEngine
// reference — that would break the fast core-tests build and this file's headless A4 gate.

using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S105 Slice 3 — turns one decoded MVT tile's symbol layers into rendered-ready
    /// <see cref="LabelInstance"/>s, the label analogue of <c>StyledLineTileBuilder</c> (but it emits labels,
    /// never a <c>Mesh</c> — symbols are the placed-every-frame class, S20 T5). The Core half
    /// (<see cref="SymbolFeatureExtractor"/>) already did the worker-safe work (select → resolve text →
    /// project anchor → <see cref="SymbolLabel"/>); this adds the Unity-side shaping the labels need to
    /// render.
    ///
    /// <para><b>Deferred two-pass build (the locked threading model).</b> Symbols do NOT have to complete in
    /// one synchronous pass. <see cref="BuildAsync"/> first REQUESTS every glyph range the tile's labels
    /// need (<see cref="GlyphManager.EnsureFontStackRangeAsync"/> — the async fetch/decode/atlas-append),
    /// and only THEN, once the glyphs are in the shared atlas, shapes + lays out each label and emits the
    /// <see cref="LabelInstance"/>. So a tile whose glyphs are still fetching produces its labels a frame or
    /// two later rather than stalling the tile-consume critical path.</para>
    /// </summary>
    public sealed class StyledSymbolTileBuilder
    {
        private readonly GlyphManager _glyphManager;
        private readonly CodepointTextShaper _shaper = new CodepointTextShaper();

        /// <param name="glyphManager">The shared production glyph manager (one atlas across all symbol layers).</param>
        public StyledSymbolTileBuilder(GlyphManager glyphManager)
        {
            _glyphManager = glyphManager ?? throw new System.ArgumentNullException(nameof(glyphManager));
        }

        /// <summary>
        /// Build every symbol label of <paramref name="symbolLayers"/> over <paramref name="tile"/> and
        /// append them to <paramref name="output"/> (caller-owned — the per-tile label list of F5's
        /// lifecycle). Pass 1 requests every glyph (async); pass 2, once they're in the shared atlas,
        /// shapes + lays out + emits. Emission order matches <see cref="SymbolFeatureExtractor"/>'s per-tile
        /// ordinal, so <see cref="LabelInstance.FeatureIndex"/> stays the stable S20 tiebreak.
        ///
        /// <para>Correct incremental layout relies on a FIXED-size shared atlas (the subsystem builds the
        /// production atlas with a fixed dimension): the atlas <c>Size</c> never changes as glyphs append,
        /// so a tile laid out early keeps valid UVs when a later tile adds glyphs. A growing atlas would
        /// invalidate earlier tiles' UVs — see the glyph-atlas-uv-growth-staleness lesson.</para>
        /// </summary>
        /// <param name="materialIndices">Optional per-layer owning-material index (parallel to
        /// <paramref name="symbolLayers"/>) stamped onto each label's <see cref="LabelInstance.MaterialIndex"/>
        /// for the per-layer draw grouping (S105 F1). Null → all 0 (single-material / demo path).</param>
        public async UniTask BuildAsync(
            MvtTile tile,
            TileId tileId,
            IReadOnlyList<StyleLayer> symbolLayers,
            double zoom,
            IProjection projection,
            List<LabelInstance> output,
            IReadOnlyList<int> materialIndices = null,
            CancellationToken ct = default)
        {
            if (tile == null || symbolLayers == null || projection == null || output == null) return;

            var extracted = new List<SymbolLabel>();
            for (int l = 0; l < symbolLayers.Count; l++)
            {
                StyleLayer layer = symbolLayers[l];
                if (layer == null) continue;

                extracted.Clear();
                SymbolFeatureExtractor.Extract(layer, tile, tileId, zoom, projection, extracted);
                if (extracted.Count == 0) continue;

                int materialIndex = (materialIndices != null && l < materialIndices.Count) ? materialIndices[l] : 0;
                var fontStack = new FontStack { Names = layer.Layout.TextFont };

                // Pass 1 — REQUEST every glyph the labels need (async fetch/decode/atlas-append). Each
                // (fontStack, range) is fetched at most once (GlyphManager caches), so repeated codepoints
                // and repeated names are cheap.
                for (int i = 0; i < extracted.Count; i++)
                {
                    string text = extracted[i].Text;
                    for (int c = 0; c < text.Length; c++)
                        await _glyphManager.EnsureFontStackRangeAsync(fontStack, text[c], ct);
                }

                // Pass 2 — the glyphs are in the shared atlas: shape + lay out + emit.
                FontStackResolver resolver = _glyphManager.CreateResolver(fontStack);
                for (int i = 0; i < extracted.Count; i++)
                {
                    SymbolLabel s = extracted[i];
                    ShapedRun run = _shaper.Shape(new ShapingRequest
                    {
                        Text = s.Text,
                        FontStack = fontStack,
                        Metrics = resolver,
                    });
                    TextLayoutResult layout = TextQuadLayout.Layout(run, _glyphManager.Atlas, TextLayoutOptions.Default);

                    output.Add(new LabelInstance
                    {
                        AnchorRender = s.AnchorRender,
                        Layout = layout,
                        Paint = s.Paint,
                        TextSizePx = s.TextSizePx,
                        PaddingPx = s.PaddingPx,
                        SortKey = s.SortKey,
                        FeatureIndex = s.FeatureIndex,
                        TileKey = s.TileKey,
                        AllowOverlap = s.AllowOverlap,
                        IgnorePlacement = s.IgnorePlacement,
                        MaterialIndex = materialIndex,
                    });
                }
            }
        }
    }
}
