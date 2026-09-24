// Non-obvious why: keep this file out of Tools/core-tests — it depends on Unity.Collections, and a
// Collections shim there would let the fast loop pass the ownership bugs it exists to catch.

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
    /// Turns one decoded tile's symbol layers into <see cref="ShapedSymbol"/>s (symbols, never a <c>Mesh</c>).
    /// Non-local invariant: a build runs <see cref="CollectRequiredRanges"/>, then
    /// <see cref="EnsureGlyphRangesAsync"/> (the one suspension point), then <see cref="Shape"/>, so a tile
    /// whose glyphs are still fetching emits its symbols later without stalling tile consume, and shaping
    /// never interleaves with an atlas append.
    /// </summary>
    public sealed class StyledSymbolTileBuilder
    {
        private readonly GlyphManager _glyphManager;
        private readonly CodepointTextShaper _shaper = new CodepointTextShaper();
        /// <summary>The table <see cref="Shape"/> interns Text/IconImage into at SHAPE time. Production passes
        /// the store's own, so ids stay stable across tiles for the style's life; a caller that omits one
        /// (tests, the demo path) gets a private table. <c>internal</c> for the fixture that recomputes an
        /// emitted symbol's ids, mirroring <see cref="Text.SymbolTileStore.StringTable"/>.</summary>
        internal SymbolStringTable StringTable { get; }

        /// <summary>Monotonic count of symbols skipped because their build threw a non-cancellation exception
        /// (e.g. deferred mixed-direction bidi). Surfaced as SymbolSubsystem telemetry. Main-thread
        /// only (Shape is the main tail) — no synchronization.</summary>
        internal int SkippedSymbolCount { get; private set; }

        /// <summary>Type+message of the most recent skip, for the subsystem's throttled diagnostic log
        /// (which cannot see the swallowed exception). Null until the first skip.</summary>
        internal string LastSkipReason { get; private set; }

        /// <param name="glyphManager">The shared production glyph manager (one atlas across all symbol layers).</param>
        /// <param name="stringTable">The table <see cref="Shape"/> interns Text/IconImage into. Null mints a
        /// private table; production passes the owning <c>SymbolTileStore</c>'s table.</param>
        public StyledSymbolTileBuilder(GlyphManager glyphManager, SymbolStringTable stringTable = null)
        {
            _glyphManager = glyphManager ?? throw new System.ArgumentNullException(nameof(glyphManager));
            StringTable = stringTable ?? new SymbolStringTable();
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
        /// WORKER-SAFE: SELECT + project each symbol layer's <see cref="SymbolFeature"/>s off the main
        /// thread. Touches only <see cref="SymbolFeatureExtractor"/> + immutable parsed layers + the stateless
        /// <see cref="IProjection"/> — no glyph cache, no atlas, no <c>UnityEngine.Object</c> — so the caller
        /// may run it on the thread pool. Returns the shaping inputs <see cref="Shape"/> consumes on main.
        /// </summary>
        /// <param name="spriteAtlas">Forwarded to <see cref="SymbolFeatureExtractor.Extract"/>; <c>null</c> (the
        /// default) yields no icon symbols.</param>
        /// <remarks>No store parameter and no private fallback store. Every layer's geometry is
        /// read off the decoded tile itself (<c>ITileLayer.Geometry</c>), so N symbol layers naming one
        /// source-layer read ONE buffer with nothing threaded through and nothing to dispose — and they share
        /// it with the mesh layers of the same kick.</remarks>
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

        /// <summary>Extract then shape in one call, for tests and the demo path; the live path runs
        /// <see cref="ExtractLayers"/> on a worker and the rest on main. Emission order matches the
        /// extractor's per-tile ordinal, so <see cref="ShapedSymbol.FeatureIndex"/> stays a stable tiebreak.
        /// Non-local invariant: the shared atlas has a fixed size, so UVs baked early stay valid as later
        /// tiles append glyphs.</summary>
        /// <param name="materialIndices">Per-layer material index (parallel to <paramref name="symbolLayers"/>)
        /// stamped onto each <see cref="ShapedSymbol.MaterialIndex"/>. Null → all 0.</param>
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
        /// Pure and synchronous, worker or main: appends every distinct <c>(fontName, rangeStart)</c> the
        /// symbols need to <paramref name="into"/> in first-encounter order. Non-local invariant: the atlas is
        /// an insertion-order shelf packer, so a reorder moves glyph UVs and diffs every text snapshot.
        /// Limitation: the set comes from UTF-16 code units, not the Arabic presentation forms the shaper
        /// resolves; changing that changes the fetched set and the rendered output.
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

                // An icon symbol carries no Text (null) — skip it here BEFORE dereferencing .Length, or
                // an icon-only layer NREs on its very first symbol.
                for (int i = 0; i < extracted.Count; i++)
                {
                    if (extracted[i].Kind == SymbolKind.Icon) continue;
                    string text = extracted[i].Text;
                    // Limitation: UTF-16 code units, not codepoints, so surrogate pairs are not handled. A
                    // codepoint walk changes the requested set.
                    for (int c = 0; c < text.Length; c++)
                    {
                        int rangeStart = FontStackResolver.ComputeRangeStart(text[c]);
                        // All stack names: the winning font is known only after the cache is populated, so
                        // collecting only the first name would kill fallback fonts.
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
        /// Main thread, synchronous: shapes, lays out and emits each <see cref="ShapedSymbol"/> into
        /// <paramref name="buffer"/> in the extractor's per-tile order. It reads the glyph atlas that
        /// <see cref="EnsureGlyphRangesAsync"/> filled and never mutates it. Non-local invariant: the loop has
        /// no cancellation check; the caller's trailing <c>ThrowIfCancellationRequested</c> is the only guard
        /// against committing a partial buffer.
        /// </summary>
        public void Shape(
            List<ExtractedLayer> extractedLayers, SymbolTileBuffer buffer, CancellationToken ct = default)
        {
            if (extractedLayers == null || buffer == null) return;

            // Per-call locals, not fields: SymbolSubsystem shares one builder across tails, so two Shape
            // calls can interleave on the main thread. `buffer` is per-build and shared by its layers.
            var glyphQuads = new List<PositionedGlyph>();
            // Per-symbol temps: the layout overloads Clear() their output, so passing the build-wide pools
            // directly would erase earlier symbols. Each result is copied into `buffer`.
            var quadCorners = new List<SymbolQuad>();
            var curvedPlacements = new List<CurvedGlyph>();

            for (int el = 0; el < extractedLayers.Count; el++)
            {
                ExtractedLayer    layerEx       = extractedLayers[el];
                List<SymbolFeature> extracted     = layerEx.Symbols;
                int               materialIndex = layerEx.MaterialIndex;
                FontStack         fontStack     = layerEx.FontStack;

                // Built lazily on the first TEXT symbol: an icon-only layer may carry no text-font at all.
                FontStackResolver resolver = null;
                for (int i = 0; i < extracted.Count; i++)
                {
                    try
                    {
                        SymbolFeature s = extracted[i];
                        if (s.Kind == SymbolKind.Icon && s.Placement == SymbolPlacement.Point)
                        {
                            // A pre-laid-out quad, no shaping. The quad keeps the transparent border; the
                            // bounds (the collision box) inset by it, so placement runs on the ink.
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
                                IconImageId = StringTable.Intern(s.IconImage), // cross-tile icon identity, interned
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
                            // A LINE icon is a one-glyph curved symbol: its cell is centred on 0, which is the
                            // CurvedGlyph.Cell contract. TextSizePx = OneEm keeps the cell scale at 1.
                            int iconGlyphStart = buffer.Glyphs.Count;
                            // CellSkirt insets the collision box and chord probe; the drawn cell keeps the
                            // border. Every text glyph leaves it 0.
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
                                IconImageId = StringTable.Intern(s.IconImage), // cross-tile icon identity, interned
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
                                // The resolved icon-pitch-alignment — the curved arm's world-arc predicate.
                                PitchAlignment = s.PitchAlignment,
                            });
                            continue;
                        }

                        resolver ??= _glyphManager.CreateResolver(fontStack);
                        // Fills the reused glyphQuads. The wrapping ShapedRun still allocates: the layouts
                        // take a ShapedRun and have no caller-buffer variant.
                        TextDirection direction = _shaper.Shape(new ShapingRequest
                        {
                            Text = s.Text,
                            FontStack = fontStack,
                            Metrics = resolver,
                        }, glyphQuads);
                        ShapedRun run = new ShapedRun { Glyphs = glyphQuads, Direction = direction };
                        if (s.Placement == SymbolPlacement.Point)
                        {
                            // Writes into the reused quadCorners, then copies into buffer's build-wide Quads.
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
                                TextId = StringTable.Intern(s.Text), // cross-tile identity, interned
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
                            // Curved along-line symbol, oriented by the line tangent each frame, so it takes
                            // no point-layout options. Same reused-then-copied pattern as above.
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
                                AnchorStart = textAnchorStart, AnchorCount = textAnchorCount, // build-time zoom-invariant anchors
                                PathStart = textPathStart, PathCount = textPathCount,
                                TextId = StringTable.Intern(s.Text), // carried for parity (line symbols are excluded from dedup in v1)
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
                                // The resolved text-pitch-alignment — the curved arm's world-arc predicate.
                                PitchAlignment = s.PitchAlignment,
                            });
                        }
                    }
                    catch (System.Exception ex) when (!(ex is System.OperationCanceledException) && !ct.IsCancellationRequested)
                    {
                        // One symbol's failure skips only that symbol. AddSymbol is last in every branch, so a
                        // throw leaves at most an orphaned pool range, never a record pointing at bad data.
                        SkippedSymbolCount++;
                        LastSkipReason = ex.GetType().Name + ": " + ex.Message;
                    }
                }
            }
        }
    }
}
