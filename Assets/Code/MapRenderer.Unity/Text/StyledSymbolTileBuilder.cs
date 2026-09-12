// No `using UnityEngine` — but this file is NO LONGER compiled by Tools/core-tests and must not be re-added
// to core-tests.csproj (tile-geometry IR B4). It calls SymbolFeatureExtractor.Extract, which now materializes
// a Waist-1 TileGeometryBuffers and therefore depends on Unity.Collections transitively; core-tests has no
// Unity.Collections and deliberately gets no shim (a hand-written one would be a second implementation of
// Collections' ownership semantics, and disposing an AsArray() view there is a SILENT no-op — the fast loop
// would lie about exactly the bug class this epic keeps hitting). Its tests run unchanged in the Unity
// EditMode runner: no coverage was lost, only iteration speed.

using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S105 Slice 3 — turns one decoded MVT tile's symbol layers into rendered-ready
    /// <see cref="ShapedSymbol"/>s, the symbol analogue of <c>StyledLineTileBuilder</c> (but it emits symbols,
    /// never a <c>Mesh</c> — symbols are the placed-every-frame class, S20 T5). The Core half
    /// (<see cref="SymbolFeatureExtractor"/>) already did the worker-safe work (select → resolve text →
    /// project anchor → <see cref="SymbolFeature"/>); this adds the Unity-side shaping the symbols need to
    /// render.
    ///
    /// <para><b>Deferred three-step build (the locked threading model).</b> Symbols do NOT have to complete
    /// in one synchronous pass. <see cref="BuildAsync"/> runs <see cref="CollectRequiredRanges"/> (pure,
    /// sync — gathers every glyph range the tile's symbols need), then <see cref="EnsureGlyphRangesAsync"/>
    /// (the ONE suspension point — the async fetch/decode/atlas-append for that whole set), and only THEN,
    /// once the glyphs are in the shared atlas, <see cref="Shape"/> (sync, no suspension) shapes + lays out
    /// each symbol and emits the <see cref="ShapedSymbol"/>. So a tile whose glyphs are still fetching
    /// produces its symbols a frame or two later rather than stalling the tile-consume critical path, and
    /// shaping itself never interleaves with an atlas append.</para>
    /// </summary>
    public sealed class StyledSymbolTileBuilder
    {
        private readonly GlyphManager _glyphManager;
        private readonly CodepointTextShaper _shaper = new CodepointTextShaper();

        /// <summary>Monotonic count of symbols skipped because their build threw a non-cancellation exception
        /// (e.g. S18's deferred mixed-direction bidi). Surfaced as SymbolSubsystem telemetry. Main-thread
        /// only (Shape is the main tail) — no synchronization.</summary>
        internal int SkippedSymbolCount { get; private set; }

        /// <summary>Type+message of the most recent skip, for the subsystem's throttled diagnostic log
        /// (which cannot see the swallowed exception). Null until the first skip.</summary>
        internal string LastSkipReason { get; private set; }

        /// <param name="glyphManager">The shared production glyph manager (one atlas across all symbol layers).</param>
        public StyledSymbolTileBuilder(GlyphManager glyphManager)
        {
            _glyphManager = glyphManager ?? throw new System.ArgumentNullException(nameof(glyphManager));
        }

        /// <summary>One symbol layer's extracted symbols + the shaping inputs carried to <see cref="Shape"/>.
        /// All fields are engine-free Core types, so the whole list is worker-safe.</summary>
        public readonly struct ExtractedLayer
        {
            public readonly int               MaterialIndex;
            public readonly FontStack         FontStack;
            public readonly List<SymbolFeature> Symbols;
            public ExtractedLayer(int materialIndex, FontStack fontStack, List<SymbolFeature> symbols)
            { MaterialIndex = materialIndex; FontStack = fontStack; Symbols = symbols; }
        }

        /// <summary>
        /// WORKER-SAFE (Stage B): SELECT + project each symbol layer's <see cref="SymbolFeature"/>s off the main
        /// thread. Touches only <see cref="SymbolFeatureExtractor"/> + immutable parsed layers + the stateless
        /// <see cref="IProjection"/> — no glyph cache, no atlas, no <c>UnityEngine.Object</c> — so the caller
        /// may run it on the thread pool. Returns the shaping inputs <see cref="Shape"/> consumes on main.
        /// </summary>
        /// <param name="spriteAtlas">Forwarded verbatim to <see cref="SymbolFeatureExtractor.Extract"/>;
        /// <c>null</c> (the default) yields no icon symbols, so omitting this argument is behaviour-preserving.
        /// Production callers still omit it — icon draw is not yet wired, so it stays inert.</param>
        /// <remarks>IR C1 P3: no store parameter and no private fallback store. Every layer's geometry is
        /// read off the decoded tile itself (<c>ITileLayer.Geometry</c>), so N symbol layers naming one
        /// source-layer read ONE buffer with nothing threaded through and nothing to dispose — and they share
        /// it with the mesh layers of the same kick, which the pass-scoped store never could.</remarks>
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
                var symbols = new List<SymbolFeature>();
                SymbolFeatureExtractor.Extract(layer, tile, tileId, zoom, projection, symbols, spriteAtlas);
                if (symbols.Count == 0) continue;
                int materialIndex = (materialIndices != null && l < materialIndices.Count) ? materialIndices[l] : 0;
                result.Add(new ExtractedLayer(materialIndex, new FontStack { Names = layer.Layout.TextFont }, symbols));
            }
            return result;
        }

        /// <summary>Convenience (tests + the demo path): extract then shape in one call — collects every
        /// needed glyph range, ensures it (async), then shapes + lays out + emits every symbol of <paramref
        /// name="symbolLayers"/> over <paramref name="tile"/> into <paramref name="buffer"/> (caller-owned —
        /// the per-build reused buffer of F5's lifecycle). Emission order matches
        /// <see cref="SymbolFeatureExtractor"/>'s per-tile ordinal, so <see cref="ShapedSymbol.FeatureIndex"/>
        /// stays the stable S20 tiebreak. The subsystem's live path instead runs <see cref="ExtractLayers"/>
        /// on a worker and the collect/ensure/<see cref="Shape"/> sequence on main.
        ///
        /// <para>Correct incremental layout relies on a FIXED-size shared atlas (the subsystem builds the
        /// production atlas with a fixed dimension): the atlas <c>Size</c> never changes as glyphs append,
        /// so a tile laid out early keeps valid UVs when a later tile adds glyphs. A growing atlas would
        /// invalidate earlier tiles' UVs — see the glyph-atlas-uv-growth-staleness lesson.</para></summary>
        /// <param name="materialIndices">Optional per-layer owning-material index (parallel to
        /// <paramref name="symbolLayers"/>) stamped onto each symbol's <see cref="ShapedSymbol.MaterialIndex"/>
        /// for the per-layer draw grouping (S105 F1). Null → all 0 (single-material / demo path).</param>
        public async UniTask BuildAsync(
            IDecodedTile tile,
            TileId tileId,
            IReadOnlyList<StyleLayer> symbolLayers,
            double zoom,
            IProjection projection,
            SymbolTileBuffer buffer,
            IReadOnlyList<int> materialIndices = null,
            CancellationToken ct = default,
            MapRenderer.Core.Text.Sprites.SpriteAtlasView spriteAtlas = null)
        {
            if (buffer == null) return;
            List<ExtractedLayer> extracted = ExtractLayers(tile, tileId, symbolLayers, zoom, projection, materialIndices, spriteAtlas);
            // Per-call locals: BuildAsync has zero production callers (tests + the demo path only), so this
            // allocation is not on the data plane (conventions-short.md, native-first rule's carve-out).
            var ranges = new List<(string FontName, int RangeStart)>();
            var seen = new HashSet<(string FontName, int RangeStart)>();
            CollectRequiredRanges(extracted, ranges, seen);
            await EnsureGlyphRangesAsync(ranges, ct);
            Shape(extracted, buffer, ct);
        }

        /// <summary>
        /// WORKER-OR-MAIN, pure and synchronous: collects every distinct <c>(fontName, rangeStart)</c> pair
        /// <paramref name="extractedLayers"/>' symbols will need once shaped, into <paramref name="into"/> in
        /// FIRST-ENCOUNTER order (layers → symbols → UTF-16 code units → stack names — the exact order the
        /// deferred two-pass build used to fetch in). <b>That order is load-bearing, not stylistic:</b>
        /// <see cref="GlyphAtlas"/> is an insertion-order shelf packer, so this order determines every baked
        /// glyph's UV — reordering it (e.g. emitting from <paramref name="seen"/> instead of <paramref
        /// name="into"/>) diffs every golden snapshot that shapes text. <paramref name="seen"/> is a
        /// caller-owned dedup scope, cleared once per BUILD (not per layer) so the set is build-wide.
        ///
        /// <para>This reproduces the set pass 1 used to REQUEST, not the set the shaper's presentation-form
        /// mapping (Arabic joining) actually RESOLVES — the two differ (a pre-existing, deferred gap),
        /// and this method must not "fix" that: doing so would fetch a different glyph set and change
        /// rendering output.</para>
        /// </summary>
        /// <param name="extractedLayers">This build's <see cref="ExtractLayers"/> output; null is a no-op.</param>
        /// <param name="into">Appended to, in first-encounter order; not cleared by this method.</param>
        /// <param name="seen">The dedup set backing <paramref name="into"/>; not cleared by this method — the
        /// caller controls the dedup scope (one build, or narrower).</param>
        public void CollectRequiredRanges(
            List<ExtractedLayer> extractedLayers,
            List<(string FontName, int RangeStart)> into,
            HashSet<(string FontName, int RangeStart)> seen)
        {
            if (extractedLayers == null || into == null || seen == null) return;

            for (int el = 0; el < extractedLayers.Count; el++)
            {
                ExtractedLayer       layerEx   = extractedLayers[el];
                List<SymbolFeature>  extracted = layerEx.Symbols;
                FontStack            fontStack = layerEx.FontStack;
                if (fontStack?.Names == null) continue;

                // I5a: an icon symbol carries no Text (null) — skip it here BEFORE dereferencing .Length, or
                // an icon-only layer NREs on its very first symbol.
                for (int i = 0; i < extracted.Count; i++)
                {
                    if (extracted[i].Kind == SymbolKind.Icon) continue;
                    string text = extracted[i].Text;
                    // UTF-16 CODE UNIT, not a decoded codepoint — deliberately reproduces the pre-existing
                    // surrogate-pair gap (deferred). Do not "fix" this into a combined-codepoint walk;
                    // that changes the requested set.
                    for (int c = 0; c < text.Length; c++)
                    {
                        int rangeStart = FontStackResolver.ComputeRangeStart(text[c]);
                        // ALL stack names, not just the winner — GlyphManager.EnsureFontStackRangeAsync fetches
                        // every name because which font wins isn't known until FontStackResolver.Resolve runs
                        // against the populated cache; collecting only the first name would kill fallback fonts.
                        for (int n = 0; n < fontStack.Names.Count; n++)
                        {
                            string name = fontStack.Names[n];
                            if (name == null) continue;
                            var key = (name, rangeStart);
                            if (seen.Add(key)) into.Add(key);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// MAIN-THREAD, the ONE surviving suspension point of a symbol build: fetches/decodes/caches/appends
        /// every range in <paramref name="ranges"/> (typically <see cref="CollectRequiredRanges"/>'s build-wide
        /// output) into the shared atlas. Calls <see cref="GlyphManager.EnsureFontRangeAsync"/> directly, NOT
        /// <see cref="GlyphManager.EnsureFontStackRangeAsync"/> — the stack expansion already happened in the
        /// collect step, so re-expanding here would refetch each name once per distinct codepoint again.
        /// </summary>
        /// <param name="ranges">Every <c>(fontName, rangeStart)</c> pair to ensure, in the order to fetch them.</param>
        public async UniTask EnsureGlyphRangesAsync(
            List<(string FontName, int RangeStart)> ranges, CancellationToken ct = default)
        {
            if (ranges == null) return;
            for (int i = 0; i < ranges.Count; i++)
                await _glyphManager.EnsureFontRangeAsync(ranges[i].FontName, ranges[i].RangeStart, ct);
        }

        /// <summary>
        /// MAIN-THREAD, synchronous, no suspension point: shapes + lays out + emits each
        /// <see cref="ShapedSymbol"/> into <paramref name="buffer"/>, reading the shared glyph cache/atlas that
        /// <see cref="EnsureGlyphRangesAsync"/> already finished populating for this build — this method never
        /// mutates the atlas. Reads the shared glyph cache/atlas, so it runs on the main thread (Stage C moves
        /// shaping to a worker via an immutable snapshot). Emission order matches the extractor's per-tile
        /// ordinal (stable FeatureIndex).
        ///
        /// <para><b>No new cancellation checks were added here.</b> This method's only cancellation-observation
        /// point (the deleted fetch <c>await</c>) is gone, so a cancel landing mid-shape now lets the loop run
        /// to completion into a buffer the caller then discards — commit semantics are unchanged, since the
        /// caller's trailing <c>ThrowIfCancellationRequested</c> was, and remains, the sole partial-commit
        /// guard.</para>
        /// </summary>
        public void Shape(
            List<ExtractedLayer> extractedLayers, SymbolTileBuffer buffer, CancellationToken ct = default)
        {
            if (extractedLayers == null || buffer == null) return;

            // PER-CALL locals (not instance fields): SymbolSubsystem shares one _builder across
            // tails whose starts are budgeted per frame but never awaited to completion (RunTailAsync /
            // PumpBuilds), so two Shape calls can be interleaved on the main thread. A local lives in
            // this call's own stack frame, so concurrent calls never share it. `buffer` itself is
            // NOT one of these — it is rented per-build by SymbolSubsystem (the pairing-adjacency rule:
            // every layer processor of ONE build must write into the SAME buffer instance).
            var glyphQuads = new List<PositionedGlyph>();
            // 4.4c (3b): reused per-call temp buffers the no-alloc TextQuadLayout/CurvedTextLayout overloads
            // clear-and-fill per symbol — copied into buffer's own growing pools right after, since those
            // pools accumulate EVERY symbol of the whole build and the no-alloc overloads always Clear() their
            // output first (a build-wide pool passed directly would erase every earlier symbol's quads/glyphs).
            var quadCorners = new List<SymbolQuad>();
            var curvedPlacements = new List<CurvedGlyph>();

            for (int el = 0; el < extractedLayers.Count; el++)
            {
                ExtractedLayer    layerEx       = extractedLayers[el];
                List<SymbolFeature> extracted     = layerEx.Symbols;
                int               materialIndex = layerEx.MaterialIndex;
                FontStack         fontStack     = layerEx.FontStack;

                // The glyphs are already in the shared atlas (EnsureGlyphRangesAsync ran before this call):
                // shape + lay out + emit. I5a: the resolver is only needed by TEXT symbols (an icon-only
                // layer may carry no text-font at all), so it is built lazily on first use rather than
                // unconditionally — an icon-only layer never touches the font stack / GlyphManager resolver
                // machinery.
                FontStackResolver resolver = null;
                for (int i = 0; i < extracted.Count; i++)
                {
                    try
                    {
                        SymbolFeature s = extracted[i];
                        if (s.Kind == SymbolKind.Icon && s.Placement == SymbolPlacement.Point)
                        {
                            // I5a: an icon is a single pre-laid-out quad (SymbolFeatureExtractor already
                            // resolved sprite + icon-size/-offset/-anchor) — no shaping, just the SkirtPx-inset
                            // min/max-corner bounds formula (inlined here, no allocation), so it rides the SAME
                            // point-placement path downstream (§5.4: SymbolPlacementKind.Point + AtlasKind, no
                            // parallel path). The quad carries the transparent border; the bounds (i.e. the
                            // collision box) must not — placement runs on the ink, not on the skirt.
                            float2 iconSkirt = new float2(s.IconSkirtPx, s.IconSkirtPx);
                            float2 iconBoundsMin = math.min(s.IconQuad.TopLeft, s.IconQuad.BottomRight) + iconSkirt;
                            float2 iconBoundsMax = math.max(s.IconQuad.TopLeft, s.IconQuad.BottomRight) - iconSkirt;
                            int iconQuadStart = buffer.Quads.Count;
                            buffer.Quads.Add(s.IconQuad);
                            buffer.AddSymbol(new ShapedSymbol
                            {
                                AnchorRender = s.AnchorRender,
                                UpRender = s.UpRender,
                                Placement = SymbolPlacement.Point,
                                Kind = SymbolKind.Icon,
                                BoundsMin = iconBoundsMin,
                                BoundsMax = iconBoundsMax,
                                QuadStart = iconQuadStart,
                                QuadCount = 1,
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
                                IconRotateRadians = s.IconRotateRadians,
                                PairRole = s.PairRole,
                                PairId = s.PairId,
                                PairOptional = s.PairOptional,
                            });
                            continue;
                        }

                        if (s.Kind == SymbolKind.Icon)
                        {
                            // P-B: a map-resolved LINE icon is the same pre-laid-out quad, but shaped as a
                            // ONE-GLYPH CURVED symbol — the icon cell is already horizontally centred on 0
                            // (icon-anchor: center), which is exactly the CurvedGlyph.Cell contract, so the
                            // whole curved machinery (per-anchor candidates, the arc walk, the rotated
                            // collision box, the baked world tangent) applies with no new symbol kind.
                            // TextSizePx = OneEm ⇒ the curved path's cell scale is 1, matching the point-icon
                            // branch above (IconQuadLayout already baked icon-size in).
                            int iconGlyphStart = buffer.Glyphs.Count;
                            // CellSkirt carries the icon's transparent border into the curved path, which
                            // insets by it for the collision box and the chord probe while the DRAWN cell
                            // keeps it. Every text glyph leaves it 0.
                            buffer.Glyphs.Add(new CurvedGlyph { ArcCenter = 0f, Cell = s.IconQuad, CellSkirt = s.IconSkirtPx });
                            int iconAnchorStart = buffer.AppendAnchors(s.LineAnchors, out int iconAnchorCount);
                            int iconPathStart = buffer.AppendPath(s.PathRender, s.PathUpRender, out int iconPathCount);
                            buffer.AddSymbol(new ShapedSymbol
                            {
                                Placement = s.Placement,
                                Kind = SymbolKind.Icon,
                                GlyphStart = iconGlyphStart, GlyphCount = 1,
                                AnchorStart = iconAnchorStart, AnchorCount = iconAnchorCount,
                                PathStart = iconPathStart, PathCount = iconPathCount,
                                IconImage = s.IconImage, // I6: cross-tile icon identity
                                Paint = s.Paint,
                                TextSizePx = TextQuadLayout.OneEm,
                                PaddingPx = s.PaddingPx,
                                SortKey = s.SortKey,
                                MaxAngleDeg = s.MaxAngleDeg,
                                KeepUpright = false, // icon-keep-upright's spec default — see SymbolFeature.KeepUpright
                                FeatureIndex = s.FeatureIndex,
                                TileKey = s.TileKey,
                                AllowOverlap = s.AllowOverlap,
                                IgnorePlacement = s.IgnorePlacement,
                                MaterialIndex = materialIndex,
                                IconRotateRadians = s.IconRotateRadians,
                                // W1: the resolved icon-pitch-alignment — the curved arm's world-arc predicate.
                                PitchAlignment = s.PitchAlignment,
                            });
                            continue;
                        }

                        resolver ??= _glyphManager.CreateResolver(fontStack);
                        // Zero-alloc overload: fills the reused glyphQuads instead of Shape(in) allocating
                        // its own List<PositionedGlyph>. The wrapping ShapedRun is still a fresh (small)
                        // allocation — TextQuadLayout/CurvedTextLayout.Layout both take a ShapedRun, and
                        // there is no caller-buffer variant of that adapter.
                        TextDirection direction = _shaper.Shape(new ShapingRequest
                        {
                            Text = s.Text,
                            FontStack = fontStack,
                            Metrics = resolver,
                        }, glyphQuads);
                        ShapedRun run = new ShapedRun { Glyphs = glyphQuads, Direction = direction };
                        if (s.Placement == SymbolPlacement.Point)
                        {
                            // Slice A: the per-feature options threaded from the style layer (anchor/offset/justify/
                            // max-width/line-height/letter-spacing/radial-offset). Was hardcoded TextLayoutOptions.Default.
                            // No-alloc overload writes into the reused quadCorners (Clear()-ed internally); copy
                            // its contents into buffer's own build-wide Quads pool right after (3b: quadCorners
                            // itself never allocates once its capacity has stabilized across symbols).
                            TextLayoutBounds bounds = TextQuadLayout.Layout(run, _glyphManager.Atlas, s.LayoutOptions, quadCorners);
                            int textQuadStart = buffer.Quads.Count;
                            for (int q = 0; q < quadCorners.Count; q++) buffer.Quads.Add(quadCorners[q]);
                            buffer.AddSymbol(new ShapedSymbol
                            {
                                AnchorRender = s.AnchorRender,
                                UpRender = s.UpRender,
                                Placement = SymbolPlacement.Point,
                                Kind = SymbolKind.Text,
                                BoundsMin = bounds.Min,
                                BoundsMax = bounds.Max,
                                QuadStart = textQuadStart,
                                QuadCount = quadCorners.Count,
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
                                PairOptional = s.PairOptional,
                            });
                        }
                        else
                        {
                            // #5: curved along-line symbol — per-glyph layout placed on the projected line each
                            // frame. Orientation is the line tangent, so no point-layout options / rotation-alignment.
                            // Same reused-then-copied pattern as the point-text branch above.
                            CurvedTextLayout.Layout(run, _glyphManager.Atlas, curvedPlacements);
                            int textGlyphStart = buffer.Glyphs.Count;
                            for (int g = 0; g < curvedPlacements.Count; g++) buffer.Glyphs.Add(curvedPlacements[g]);
                            int textAnchorStart = buffer.AppendAnchors(s.LineAnchors, out int textAnchorCount);
                            int textPathStart = buffer.AppendPath(s.PathRender, s.PathUpRender, out int textPathCount);
                            buffer.AddSymbol(new ShapedSymbol
                            {
                                Placement = s.Placement,
                                Kind = SymbolKind.Text,
                                GlyphStart = textGlyphStart, GlyphCount = curvedPlacements.Count,
                                AnchorStart = textAnchorStart, AnchorCount = textAnchorCount, // A-2: build-time zoom-invariant anchors
                                PathStart = textPathStart, PathCount = textPathCount,
                                Text = s.Text, // A-3: carried for parity (line symbols are excluded from dedup in v1)
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
                                // W1: the resolved text-pitch-alignment — the curved arm's world-arc predicate.
                                PitchAlignment = s.PitchAlignment,
                            });
                        }
                    }
                    catch (System.Exception ex) when (!(ex is System.OperationCanceledException) && !ct.IsCancellationRequested)
                    {
                        // Layer 1 robustness: one symbol's build failure (e.g. S18's deferred mixed-direction bidi
                        // NotSupportedException) must never blank the whole tile. Skip THIS symbol; the rest still
                        // build and commit. buffer.AddSymbol is the last statement of every guarded emit branch,
                        // so no partial RECORD was added — a throw before it can leave at most an orphaned tail
                        // range in a pool (Quads/Glyphs/...), never a symbol referencing invalid data.
                        SkippedSymbolCount++;
                        LastSkipReason = ex.GetType().Name + ": " + ex.Message;
                    }
                }
            }
        }
    }
}
