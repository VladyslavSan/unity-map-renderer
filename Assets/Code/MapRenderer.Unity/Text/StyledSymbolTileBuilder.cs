// Engine-free despite living under Assets/Code/MapRenderer.Unity/Text (like GlyphManager): references only
// MapRenderer.Core.* + UniTask, NO `using UnityEngine`. It is co-located with GlyphManager (which it needs,
// and which is in this assembly) and is compiled by BOTH the Unity EditMode runner and Tools/core-tests
// (the matching <Compile Include> lives in Tools/core-tests/core-tests.csproj). Do NOT add a UnityEngine
// reference — that would break the fast core-tests build and this file's headless A4 gate.

using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Tiles;

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

        /// <summary>Monotonic count of labels skipped because their build threw a non-cancellation exception
        /// (e.g. S18's deferred mixed-direction bidi). Surfaced as SymbolLabelSubsystem telemetry. Main-thread
        /// only (ShapeAsync is the main tail) — no synchronization.</summary>
        internal int SkippedLabelCount { get; private set; }

        /// <summary>Type+message of the most recent skip, for the subsystem's throttled diagnostic log
        /// (which cannot see the swallowed exception). Null until the first skip.</summary>
        internal string LastSkipReason { get; private set; }

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
        /// <summary>One symbol layer's extracted labels + the shaping inputs carried to <see cref="ShapeAsync"/>.
        /// All fields are engine-free Core types, so the whole list is worker-safe.</summary>
        public readonly struct ExtractedLayer
        {
            public readonly int               MaterialIndex;
            public readonly FontStack         FontStack;
            public readonly List<SymbolLabel> Labels;
            public ExtractedLayer(int materialIndex, FontStack fontStack, List<SymbolLabel> labels)
            { MaterialIndex = materialIndex; FontStack = fontStack; Labels = labels; }
        }

        /// <summary>
        /// WORKER-SAFE (Stage B): SELECT + project each symbol layer's <see cref="SymbolLabel"/>s off the main
        /// thread. Touches only <see cref="SymbolFeatureExtractor"/> + immutable parsed layers + the stateless
        /// <see cref="IProjection"/> — no glyph cache, no atlas, no <c>UnityEngine.Object</c> — so the caller
        /// may run it on the thread pool. Returns the shaping inputs <see cref="ShapeAsync"/> consumes on main.
        /// </summary>
        /// <param name="spriteAtlas">I5a — forwarded verbatim to <see cref="SymbolFeatureExtractor.Extract"/>;
        /// <c>null</c> (the default) yields no icon labels, so every pre-I5a caller (which omits this
        /// argument) is byte-identical to before I5a. The production caller (<c>TileSymbolLayerProcessor</c>)
        /// still omits it — icon draw isn't wired until I5b, so it stays inert in prod for now.</param>
        public List<ExtractedLayer> ExtractLayers(
            IDecodedTile tile, TileId tileId, IReadOnlyList<StyleLayer> symbolLayers,
            double zoom, IProjection projection, IReadOnlyList<int> materialIndices = null,
            MapRenderer.Core.Text.Sprites.SpriteAtlasView spriteAtlas = null)
        {
            var result = new List<ExtractedLayer>(symbolLayers?.Count ?? 0);
            if (tile == null || symbolLayers == null || projection == null) return result;
            for (int l = 0; l < symbolLayers.Count; l++)
            {
                StyleLayer layer = symbolLayers[l];
                if (layer == null) continue;
                var labels = new List<SymbolLabel>();
                SymbolFeatureExtractor.Extract(layer, tile, tileId, zoom, projection, labels, spriteAtlas);
                if (labels.Count == 0) continue;
                int materialIndex = (materialIndices != null && l < materialIndices.Count) ? materialIndices[l] : 0;
                result.Add(new ExtractedLayer(materialIndex, new FontStack { Names = layer.Layout.TextFont }, labels));
            }
            return result;
        }

        /// <summary>Convenience (tests + the demo path): extract then shape in one call. The subsystem's live
        /// path instead runs <see cref="ExtractLayers"/> on a worker and <see cref="ShapeAsync"/> on main.</summary>
        public UniTask BuildAsync(
            IDecodedTile tile,
            TileId tileId,
            IReadOnlyList<StyleLayer> symbolLayers,
            double zoom,
            IProjection projection,
            List<LabelInstance> output,
            IReadOnlyList<int> materialIndices = null,
            CancellationToken ct = default,
            MapRenderer.Core.Text.Sprites.SpriteAtlasView spriteAtlas = null)
        {
            if (output == null) return UniTask.CompletedTask;
            return ShapeAsync(ExtractLayers(tile, tileId, symbolLayers, zoom, projection, materialIndices, spriteAtlas), output, ct);
        }

        /// <summary>
        /// MAIN-THREAD: pass 1 requests every glyph range the pre-extracted labels need (async fetch/decode/
        /// atlas-append), then pass 2 shapes + lays out + emits each <see cref="LabelInstance"/>. Reads the
        /// shared glyph cache/atlas, so it runs on the main thread (Stage C moves shaping to a worker via an
        /// immutable snapshot). Emission order matches the extractor's per-tile ordinal (stable FeatureIndex).
        /// </summary>
        public async UniTask ShapeAsync(
            List<ExtractedLayer> extractedLayers, List<LabelInstance> output, CancellationToken ct = default)
        {
            if (extractedLayers == null || output == null) return;

            for (int el = 0; el < extractedLayers.Count; el++)
            {
                ExtractedLayer    layerEx       = extractedLayers[el];
                List<SymbolLabel> extracted     = layerEx.Labels;
                int               materialIndex = layerEx.MaterialIndex;
                FontStack         fontStack     = layerEx.FontStack;

                // Pass 1 — REQUEST every glyph the labels need (async fetch/decode/atlas-append). Each
                // (fontStack, range) is fetched at most once (GlyphManager caches), so repeated codepoints
                // and repeated names are cheap. I5a: an icon label carries no Text (null) — skip it here
                // BEFORE dereferencing .Length, or an icon-only layer NREs on its very first label.
                for (int i = 0; i < extracted.Count; i++)
                {
                    if (extracted[i].Kind == LabelKind.Icon) continue;
                    string text = extracted[i].Text;
                    for (int c = 0; c < text.Length; c++)
                        await _glyphManager.EnsureFontStackRangeAsync(fontStack, text[c], ct);
                }

                // Pass 2 — the glyphs are in the shared atlas: shape + lay out + emit. I5a: the resolver is
                // only needed by TEXT labels (an icon-only layer may carry no text-font at all), so it is
                // built lazily on first use rather than unconditionally — an icon-only layer never touches
                // the font stack / GlyphManager resolver machinery.
                FontStackResolver resolver = null;
                for (int i = 0; i < extracted.Count; i++)
                {
                    try
                    {
                        SymbolLabel s = extracted[i];
                        if (s.Kind == LabelKind.Icon)
                        {
                            // I5a: an icon is a single pre-laid-out quad (SymbolFeatureExtractor already
                            // resolved sprite + icon-size/-offset/-anchor) — no shaping, just wrap it into the
                            // same TextLayoutResult shape the point-text path emits, so it rides the SAME
                            // point-placement path downstream (§5.4: LabelRecordKind.Point + AtlasKind, no parallel path).
                            TextLayoutResult iconLayout = IconQuadLayout.ToLayoutResult(s.IconQuad);
                            output.Add(new LabelInstance
                            {
                                AnchorRender = s.AnchorRender,
                                Placement = SymbolPlacement.Point,
                                Kind = LabelKind.Icon,
                                Layout = iconLayout,
                                IconImage = s.IconImage, // I6: cross-tile icon identity
                                Paint = s.Paint,
                                TextSizePx = TextQuadLayout.OneEm, // scale 1 — IconQuadLayout already baked icon-size in
                                PaddingPx = s.PaddingPx,
                                SortKey = s.SortKey,
                                FeatureIndex = s.FeatureIndex,
                                TileKey = s.TileKey,
                                AllowOverlap = s.AllowOverlap,
                                IgnorePlacement = s.IgnorePlacement,
                                MaterialIndex = materialIndex,
                                RotationAlignment = s.RotationAlignment,
                                PairRole = s.PairRole,
                                PairId = s.PairId,
                            });
                            continue;
                        }

                        resolver ??= _glyphManager.CreateResolver(fontStack);
                        ShapedRun run = _shaper.Shape(new ShapingRequest
                        {
                            Text = s.Text,
                            FontStack = fontStack,
                            Metrics = resolver,
                        });
                        if (s.Placement == SymbolPlacement.Point)
                        {
                            // Slice A: the per-feature options threaded from the style layer (anchor/offset/justify/
                            // max-width/line-height/letter-spacing/radial-offset). Was hardcoded TextLayoutOptions.Default.
                            TextLayoutResult layout = TextQuadLayout.Layout(run, _glyphManager.Atlas, s.LayoutOptions);
                            output.Add(new LabelInstance
                            {
                                AnchorRender = s.AnchorRender,
                                Placement = SymbolPlacement.Point,
                                Layout = layout,
                                Text = s.Text, // A-3: cross-tile identity
                                Paint = s.Paint,
                                TextSizePx = s.TextSizePx,
                                PaddingPx = s.PaddingPx,
                                SortKey = s.SortKey,
                                FeatureIndex = s.FeatureIndex,
                                TileKey = s.TileKey,
                                AllowOverlap = s.AllowOverlap,
                                IgnorePlacement = s.IgnorePlacement,
                                MaterialIndex = materialIndex,
                                TranslatePx = s.TranslatePx,
                                TranslateAnchor = s.TranslateAnchor,
                                RotationAlignment = s.RotationAlignment,
                                PairRole = s.PairRole,
                                PairId = s.PairId,
                            });
                        }
                        else
                        {
                            // #5: curved along-line label — per-glyph layout placed on the projected line each
                            // frame. Orientation is the line tangent, so no point-layout options / rotation-alignment.
                            var curvedGlyphs = CurvedTextLayout.Layout(run, _glyphManager.Atlas);
                            output.Add(new LabelInstance
                            {
                                Placement = s.Placement,
                                PathRender = s.PathRender,
                                LineAnchors = s.LineAnchors, // A-2: carry the build-time zoom-invariant anchors
                                CurvedGlyphs = curvedGlyphs,
                                Text = s.Text, // A-3: carried for parity (line labels are excluded from dedup in v1)
                                Paint = s.Paint,
                                TextSizePx = s.TextSizePx,
                                PaddingPx = s.PaddingPx,
                                SortKey = s.SortKey,
                                MaxAngleDeg = s.MaxAngleDeg,
                                KeepUpright = s.KeepUpright,
                                FeatureIndex = s.FeatureIndex,
                                TileKey = s.TileKey,
                                AllowOverlap = s.AllowOverlap,
                                IgnorePlacement = s.IgnorePlacement,
                                MaterialIndex = materialIndex,
                                TranslatePx = s.TranslatePx,
                                TranslateAnchor = s.TranslateAnchor,
                            });
                        }
                    }
                    catch (System.Exception ex) when (!(ex is System.OperationCanceledException) && !ct.IsCancellationRequested)
                    {
                        // Layer 1 robustness: one label's build failure (e.g. S18's deferred mixed-direction bidi
                        // NotSupportedException) must never blank the whole tile. Skip THIS label; the rest still
                        // build and commit. output.Add is the last statement of the guarded body, so no partial
                        // label was added.
                        SkippedLabelCount++;
                        LastSkipReason = ex.GetType().Name + ": " + ex.Message;
                    }
                }
            }
        }
    }
}
