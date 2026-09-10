// Unity EditMode only. It drives StyledSymbolTileBuilder, whose SymbolFeatureExtractor.Extract call since
// tile-geometry IR B4 materializes a Waist-1 TileGeometryBuffers and therefore depends on Unity.Collections —
// so this file left Tools/core-tests (no coverage lost, only speed) and must not be re-added to it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests; // TestGlyphSource

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S105 Slice 3 (A4) — THE decisive test: a parsed symbol layer + the real fixture tile, run through
    /// <see cref="StyledSymbolTileBuilder"/> produces the expected set of
    /// shaped <see cref="ShapedSymbol"/>s. Real style + real tile → correct symbols, no synthetic stand-in.
    /// </summary>
    [TestFixture]
    public class StyledSymbolTileBuilderTests
    {
        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, Path.Combine(relative));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("fixture not found: " + Path.Combine(relative));
        }

        private const string FontName = "LatinFont";
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        // 4.4c: Shape/BuildAsync now write a SymbolTileBuffer instead of a per-symbol managed carrier list —
        // this helper reads back one symbol's quad span (the buffer analogue of `symbol.Layout.Quads`).
        private static List<SymbolQuad> QuadsOf(SymbolTileBuffer buffer, int i)
        {
            ShapedSymbol symbol = buffer.Symbols[i];
            return buffer.Quads.GetRange(symbol.QuadStart, symbol.QuadCount);
        }

        private static GlyphManager BuildGlyphManager()
        {
            byte[] latin = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = latin };
            return new GlyphManager(TestGlyphSource.FromRanges(ranges));
        }

        private static SymbolStyle.StyleLayer CentroidsLayer(string extraLayoutJson = "")
            => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                Paint = SymbolStyle.PaintProperties.Parse(null),
                Layout = SymbolStyle.LayoutProperties.Parse(JsonParser.Parse(
                    "{\"text-field\":\"{NAME}\",\"text-size\":16,\"text-font\":[\"" + FontName + "\"]" + extraLayoutJson + "}")),
            };

        [Test]
        public async Task Build_CentroidsLayer_ShapesRealSymbols_NoSyntheticSource()
        {
            using MvtTile tile = MvtDecoder.Decode(FixtureTile, LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var projection = new WebMercatorProjection();
            SymbolStyle.StyleLayer layer = CentroidsLayer();

            // Independent extractor pass (Slice 2) gives the ground-truth text/anchor/ordinal per symbol.
            var extracted = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, FixtureTile, 0.0, projection, extracted);

            using var manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);
            var buffer = new SymbolTileBuffer();
            await builder.BuildAsync(tile, FixtureTile, new[] { layer }, 0.0, projection, buffer);

            // (a) one ShapedSymbol per extracted symbol, in the same order (FeatureIndex tiebreak preserved).
            Assert.AreEqual(extracted.Count, buffer.Symbols.Count, "one shaped symbol per extracted point label");
            Assert.AreEqual(248, buffer.Symbols.Count, "fixture pin: 248 centroids resolve a non-empty NAME");
            Assert.AreEqual(0, builder.SkippedSymbolCount, "a clean all-LTR build skips nothing (happy-path no-op)");

            for (int i = 0; i < buffer.Symbols.Count; i++)
            {
                ShapedSymbol symbol = buffer.Symbols[i];
                Assert.AreEqual(extracted[i].AnchorRender, symbol.AnchorRender, $"anchor preserved at {i}");
                Assert.AreEqual(extracted[i].FeatureIndex, symbol.FeatureIndex, $"ordinal preserved at {i}");
                Assert.AreEqual(extracted[i].TileKey, symbol.TileKey, $"tile key preserved at {i}");
                Assert.AreEqual(16f, symbol.TextSizePx, 1e-6, $"text-size 16 at {i}");
                Assert.AreEqual(2f, symbol.PaddingPx, 1e-6, $"text-padding default 2 at {i}");

                // Every pure-ASCII, space-free name shapes to exactly one glyph quad per character (all
                // present in the Latin fixture) — a strong tooth that shaping is REAL, not stubbed empty.
                string t = extracted[i].Text;
                if (IsAsciiNoSpace(t))
                    Assert.AreEqual(t.Length, symbol.QuadCount,
                        $"'{t}' must shape to {t.Length} glyph quads");
            }

            // (b) The specific named feature — Aruba — is the first, shaped to 5 glyphs, at its A3 anchor.
            Assert.AreEqual("Aruba", extracted[0].Text, "feature[0] is Aruba");
            Assert.AreEqual(5, buffer.Symbols[0].QuadCount, "Aruba → 5 glyph quads");

            // (c) At least two OTHER named symbols match (so a single hard-coded symbol cannot pass).
            AssertNamedSymbol(extracted, buffer, "Afghanistan", 11);
            AssertNamedSymbol(extracted, buffer, "Angola", 6);
        }

        // ── Slice A: the layout-options wiring is LIVE through the builder (guards StyledSymbolTileBuilder's
        //    TextLayoutOptions.Default -> s.LayoutOptions switch — NOT just the Extract/TextQuadLayout seams,
        //    which the engine tests already cover and which stay green even if line 105 is reverted). ──

        private static async Task<SymbolTileBuffer> BuildSymbols(SymbolStyle.StyleLayer layer, GlyphManager manager)
        {
            using MvtTile tile = MvtDecoder.Decode(FixtureTile, LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var builder = new StyledSymbolTileBuilder(manager);
            var buffer = new SymbolTileBuffer();
            await builder.BuildAsync(tile, FixtureTile, new[] { layer }, 0.0, new WebMercatorProjection(), buffer);
            return buffer;
        }

        [Test]
        public async Task Build_TextOffset_ShiftsEveryQuad_ByEmsTimes24_YDownFlippedToYUp()
        {
            using var manager = BuildGlyphManager();

            SymbolTileBuffer baseline = await BuildSymbols(CentroidsLayer(), manager);
            // text-offset [1,2] ems in MapLibre's y-DOWN convention.
            SymbolTileBuffer shifted = await BuildSymbols(CentroidsLayer(",\"text-offset\":[1,2]"), manager);

            Assert.AreEqual(baseline.Symbols.Count, shifted.Symbols.Count, "same label set");
            Assert.Greater(baseline.Symbols.Count, 0, "sanity: fixture yields labels");

            // Center anchor in both (default), so the anchor term cancels and the per-quad delta isolates the
            // offset. ems -> baked px is x24; the y is NEGATED (y-down text-offset -> y-up layout). So every
            // quad shifts by exactly (1*24, -2*24) = (24, -48). A revert of line 105 to Default makes the
            // "shifted" build ignore text-offset -> delta 0 -> this fails. It also pins the y-flip sign.
            var expected = new float2(24f, -48f);
            List<SymbolQuad> baseQuads = QuadsOf(baseline, 0);   // Aruba
            List<SymbolQuad> shiftQuads = QuadsOf(shifted, 0);
            Assert.AreEqual(baseQuads.Count, shiftQuads.Count);
            Assert.Greater(baseQuads.Count, 0, "Aruba must shape to >0 quads");
            for (int i = 0; i < baseQuads.Count; i++)
            {
                Assert.AreEqual(expected.x, shiftQuads[i].TopLeft.x - baseQuads[i].TopLeft.x, 1e-3f, $"quad {i} TopLeft.x");
                Assert.AreEqual(expected.y, shiftQuads[i].TopLeft.y - baseQuads[i].TopLeft.y, 1e-3f, $"quad {i} TopLeft.y");
                Assert.AreEqual(expected.x, shiftQuads[i].BottomRight.x - baseQuads[i].BottomRight.x, 1e-3f, $"quad {i} BottomRight.x");
                Assert.AreEqual(expected.y, shiftQuads[i].BottomRight.y - baseQuads[i].BottomRight.y, 1e-3f, $"quad {i} BottomRight.y");
            }
        }

        [Test]
        public async Task Build_TextAnchor_TranslatesBlock_ThroughTheBuilder()
        {
            using var manager = BuildGlyphManager();

            // justify held constant (center) across both so the per-line justify term cancels and the delta
            // isolates the pure anchor translation. Left anchor (hAlign=0) vs Center (hAlign=0.5) pushes the
            // block +x by 0.5*blockWidth, with no vertical change (both vAlign=0.5).
            SymbolTileBuffer center = await BuildSymbols(CentroidsLayer(",\"text-justify\":\"center\""), manager);
            SymbolTileBuffer left = await BuildSymbols(CentroidsLayer(",\"text-anchor\":\"left\",\"text-justify\":\"center\""), manager);

            List<SymbolQuad> centerQuads = QuadsOf(center, 0);   // Aruba, single line
            List<SymbolQuad> leftQuads = QuadsOf(left, 0);
            Assert.AreEqual(centerQuads.Count, leftQuads.Count);
            Assert.Greater(centerQuads.Count, 0);

            float dx0 = leftQuads[0].TopLeft.x - centerQuads[0].TopLeft.x;
            Assert.Greater(dx0, 0f, "a Left anchor must push the block +x vs Center (anchor is threaded, not dropped)");
            for (int i = 0; i < centerQuads.Count; i++)
            {
                // block-wide translation: same dx for every quad, and no vertical move.
                Assert.AreEqual(dx0, leftQuads[i].TopLeft.x - centerQuads[i].TopLeft.x, 1e-3f, $"quad {i} dx constant");
                Assert.AreEqual(0f, leftQuads[i].TopLeft.y - centerQuads[i].TopLeft.y, 1e-3f, $"quad {i} no vertical move");
            }
        }

        // ── Per-symbol build isolation: one symbol whose build throws (e.g. S18's deferred mixed-direction
        //    bidi NotSupportedException) must be SKIPPED, never abort the whole tile's symbols. ──

        private static SymbolStyle.SymbolFeature PointSymbol(string text) => new SymbolStyle.SymbolFeature
        {
            Text = text,
            Placement = SymbolPlacement.Point,
            LayoutOptions = TextLayoutOptions.Default,
            TextSizePx = 16f,
        };

        [Test]
        public void Shape_MixedDirectionSymbol_IsSkipped_OtherSymbolsSurvive()
        {
            using var manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);

            // Two plain-LTR symbols straddling one MIXED strong-direction symbol: Latin 'A' (U+0041, strong LTR)
            // + Arabic beh (U+0628, strong RTL) — which CodepointTextShaper rejects (single-run bidi, decision 8).
            // The Arabic range is absent from the Latin fixture ⇒ cached empty in Pass 1 (no throw); the throw
            // lands in Pass 2's shaper exactly as in production.
            var symbols = new List<SymbolStyle.SymbolFeature>
            {
                PointSymbol("Aruba"),
                PointSymbol("Aب"),
                PointSymbol("Angola"),
            };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { FontName } }, symbols);

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            // The whole tile is NOT aborted: the two LTR symbols build; only the mixed one is skipped.
            Assert.AreEqual(2, output.Symbols.Count, "the two LTR labels survive; the mixed label is skipped");
            Assert.AreEqual("Aruba", output.Symbols[0].Text);
            Assert.AreEqual("Angola", output.Symbols[1].Text);
            Assert.AreEqual(1, builder.SkippedSymbolCount, "exactly one label skipped");
            Assert.IsNotNull(builder.LastSkipReason, "skip reason recorded for the throttled diagnostic");
            StringAssert.Contains("NotSupportedException", builder.LastSkipReason);
        }

        [Test]
        public void EnsureGlyphRanges_Cancelled_PropagatesCancellation()
        {
            // A glyph source that OBSERVES the token (FromRanges discards it), so the ensure step's await
            // surfaces the cancel. Pins that a cancelled ensure propagates an OperationCanceledException.
            // (Shape never running as a consequence is pinned by T1/T5c, not here — this body never calls
            // Shape, so an assertion about its output would be true under any implementation.)
            var source = new TestGlyphSource((fontStack, rangeStart, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return UniTask.FromResult(GlyphRangeResponse.Absent());
            });
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            var symbols = new List<SymbolStyle.SymbolFeature> { PointSymbol("Aruba") };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { FontName } }, symbols);
            var extractedLayers = new List<StyledSymbolTileBuilder.ExtractedLayer> { layer };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ranges = new List<(string FontName, int RangeStart)>();
            var seen = new HashSet<(string FontName, int RangeStart)>();
            builder.CollectRequiredRanges(extractedLayers, ranges, seen);

            // CatchAsync (not ThrowsAsync) so the assertion accepts any OperationCanceledException SUBTYPE: the
            // Unity/Mono UniTask path surfaces cancellation as TaskCanceledException (an OCE subclass), the
            // dotnet path as a plain OperationCanceledException. The production filter uses `ex is OCE`, so it
            // correctly excludes both from the per-symbol skip — the test must be equally subtype-tolerant.
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await builder.EnsureGlyphRangesAsync(ranges, cts.Token));
        }

        // ── I5a: icon symbols ride the same Shape loop as text, but must never touch the
        //    shaper/resolver/glyph-fetch machinery (an icon-only layer may carry no text-font at all). ──

        private static SymbolStyle.SymbolFeature Icon(in SymbolQuad iconQuad) => new SymbolStyle.SymbolFeature
        {
            Kind = SymbolKind.Icon,
            IconQuad = iconQuad,
            Placement = SymbolPlacement.Point,
            AnchorRender = default,
            PaddingPx = 3f,
            SortKey = 0f,
        };

        private static readonly SymbolQuad SampleIconQuad = new SymbolQuad
        {
            TopLeft = new float2(-8, 8), BottomRight = new float2(8, -8),
            UvTopLeft = new float2(0.1f, 0.2f), UvBottomRight = new float2(0.3f, 0.4f),
        };

        [Test]
        public void Shape_IconOnlyLayer_YieldsOneIcon_NoGlyphFetch_NoShaping()
        {
            // A glyph source that THROWS if ever asked — an icon-only layer must never reach Pass 1's fetch.
            var source = new TestGlyphSource((fontStack, rangeStart, ct) =>
                throw new InvalidOperationException("icon-only layer must never request a glyph range"));
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            var symbols = new List<SymbolStyle.SymbolFeature> { Icon(SampleIconQuad) };
            // No text-font at all — FontStack.Names left default/empty, mirroring an icon-only style layer.
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(0, new FontStack(), symbols);

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            Assert.AreEqual(1, output.Symbols.Count, "the icon label must still be emitted");
            Assert.AreEqual(0, builder.SkippedSymbolCount, "an icon build must never be skipped");
            ShapedSymbol label = output.Symbols[0];
            Assert.AreEqual(SymbolKind.Icon, label.Kind);
            Assert.AreEqual(1, label.QuadCount, "a sprite is exactly one quad");
            Assert.AreEqual(TextQuadLayout.OneEm, label.TextSizePx, 1e-6, "icon scale must be 1 (OneEm/OneEm)");
            Assert.IsNull(label.Text, "an icon label carries no text");
        }

        [Test]
        public void Shape_MixedTextAndIconLayer_YieldsBothKinds_InOriginalOrder()
        {
            using var manager = BuildGlyphManager();
            var builder = new StyledSymbolTileBuilder(manager);

            var symbols = new List<SymbolStyle.SymbolFeature>
            {
                PointSymbol("Aruba"),
                Icon(SampleIconQuad),
                PointSymbol("Angola"),
            };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { FontName } }, symbols);

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            Assert.AreEqual(3, output.Symbols.Count, "text + icon + text, all three survive");
            Assert.AreEqual(0, builder.SkippedSymbolCount);
            Assert.AreEqual(SymbolKind.Text, output.Symbols[0].Kind); Assert.AreEqual("Aruba", output.Symbols[0].Text);
            Assert.AreEqual(SymbolKind.Icon, output.Symbols[1].Kind); Assert.IsNull(output.Symbols[1].Text);
            Assert.AreEqual(SymbolKind.Text, output.Symbols[2].Kind); Assert.AreEqual("Angola", output.Symbols[2].Text);
        }

        // ── A3 (P-B): a MAP-aligned LINE icon must build as a ONE-GLYPH CURVED instance, not a point one.
        //    The point-icon branch above stays byte-identical (its own tooth is the pair above). ──
        [Test]
        public void Shape_AlongLineIcon_BuildsOneGlyphCurvedInstance_NotAPointInstance()
        {
            var source = new TestGlyphSource((fontStack, rangeStart, ct) =>
                throw new InvalidOperationException("an icon-only layer must never request a glyph range"));
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            var pathRender = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            var alongLine = new SymbolStyle.SymbolFeature
            {
                Kind = SymbolKind.Icon,
                Placement = SymbolPlacement.Line,
                IconQuad = SampleIconQuad,
                PathRender = pathRender,
                LineAnchors = anchors,
                IconImage = "arrow",
                PaddingPx = 3f,
                SortKey = 1.5f,
                MaxAngleDeg = 45f,
                KeepUpright = false,
                IconRotateRadians = math.PI,
                FeatureIndex = 7,
                TileKey = 42L,
            };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(0, new FontStack(),
                new List<SymbolStyle.SymbolFeature> { alongLine });

            var output = new SymbolTileBuffer();
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            Assert.AreEqual(1, output.Symbols.Count);
            ShapedSymbol built = output.Symbols[0];
            // A point-shaped build (the pre-P-B behaviour) would leave GlyphCount 0 and Placement Point.
            Assert.AreEqual(SymbolPlacement.Line, built.Placement, "an along-line icon keeps LINE placement");
            Assert.AreEqual(1, built.GlyphCount, "exactly ONE glyph — the icon quad IS the whole run");
            Assert.AreEqual(0, built.QuadCount, "a curved instance carries no point quads");
            Assert.AreEqual(SymbolKind.Icon, built.Kind, "still an icon (routes to the sprite atlas)");
            CurvedGlyph glyph = output.Glyphs[built.GlyphStart];
            Assert.AreEqual(0f, glyph.ArcCenter, 1e-6f, "a lone cell sits at arc 0");

            // Field-for-field: the cell IS the extractor's icon quad, unmodified.
            SymbolQuad cell = glyph.Cell;
            Assert.AreEqual(SampleIconQuad.TopLeft, cell.TopLeft, "cell TopLeft == the icon quad's");
            Assert.AreEqual(SampleIconQuad.BottomRight, cell.BottomRight, "cell BottomRight == the icon quad's");
            Assert.AreEqual(SampleIconQuad.UvTopLeft, cell.UvTopLeft, "cell UvTopLeft == the icon quad's");
            Assert.AreEqual(SampleIconQuad.UvBottomRight, cell.UvBottomRight, "cell UvBottomRight == the icon quad's");

            Assert.AreEqual(TextQuadLayout.OneEm, built.TextSizePx, 1e-6f,
                "scale 1 — IconQuadLayout already baked icon-size in (matches the point-icon branch)");
            // 4.4c: AppendPath/AppendAnchors COPY into the buffer's own pools (never hold the caller's array
            // reference), so "carried, not rebuilt" is now a VALUE check — still proves the values are copied
            // verbatim, not recomputed from buffer by some other path.
            CollectionAssert.AreEqual(pathRender, output.Path.GetRange(built.PathStart, built.PathCount),
                "the projected path is carried, not rebuilt");
            CollectionAssert.AreEqual(anchors, output.Anchors.GetRange(built.AnchorStart, built.AnchorCount),
                "the build-time anchors are carried, not recomputed");
            Assert.IsFalse(built.KeepUpright, "icon-keep-upright's spec default is false");
            Assert.AreEqual("arrow", built.IconImage);
            Assert.AreEqual(math.PI, built.IconRotateRadians, 1e-6f, "icon-rotate is carried onto the curved instance");
            Assert.AreEqual(7, built.FeatureIndex);
            Assert.AreEqual(42L, built.TileKey);
        }

        // ── 4.4c pairing-adjacency tooth (step4.4-plan.md §4.4c A's TRAP): every layer processor of ONE
        //    build must write into the SAME SymbolTileBuffer, or SymbolPairing's owner-at-i+1 resolution
        //    breaks. TileSymbolLayerProcessor.CompleteOnMain calls Shape ONCE PER LAYER — this
        //    reproduces that shape directly: two Shape calls sharing one buffer, an owner tailing the
        //    FIRST call and its rider heading the SECOND. RED-verify: give the second call its OWN fresh
        //    buffer instead (the violation) — Bake would then see the owner alone (PairRoles[0] dissolves
        //    to None, its rider never in the same block) rather than a resolved pair. ──
        [Test]
        public void Shape_TwoCallsShareOneBuffer_OwnerLastOfFirstCall_RiderFirstOfSecondCall_ResolveAsPair()
        {
            using var manager = new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));
            var builder = new StyledSymbolTileBuilder(manager);

            const int materialIndex = 3; // shared — SymbolPairing's ShapedSymbol overload also requires this to match
            const long tileKey = 99L;
            const int pairId = 7;

            var ownerSymbol = new SymbolStyle.SymbolFeature
            {
                Kind = SymbolKind.Icon, IconQuad = SampleIconQuad, Placement = SymbolPlacement.Point,
                AnchorRender = default, PaddingPx = 3f, SortKey = 0f, TileKey = tileKey,
                PairRole = SymbolPairRole.Owner, PairId = pairId,
            };
            var riderSymbol = new SymbolStyle.SymbolFeature
            {
                Kind = SymbolKind.Icon, IconQuad = SampleIconQuad, Placement = SymbolPlacement.Point,
                AnchorRender = default, PaddingPx = 3f, SortKey = 0f, TileKey = tileKey,
                PairRole = SymbolPairRole.Rider, PairId = pairId,
            };
            var layer1 = new StyledSymbolTileBuilder.ExtractedLayer(
                materialIndex, new FontStack(), new List<SymbolStyle.SymbolFeature> { ownerSymbol });
            var layer2 = new StyledSymbolTileBuilder.ExtractedLayer(
                materialIndex, new FontStack(), new List<SymbolStyle.SymbolFeature> { riderSymbol });

            var buffer = new SymbolTileBuffer();
            // "processor 1" and "processor 2" — mirrors TileSymbolLayerProcessor's one-Shape-call-per-layer
            // shape, both fed the SAME shared buffer (the rule TileSymbolLayerProcessor/TryBeginBuild wire up).
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer1 }, buffer);
            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer2 }, buffer);

            Assert.AreEqual(2, buffer.Symbols.Count, "both calls must land in the SAME buffer");

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, slotCount: 1, double3.zero, new SymbolStringTable());
            try
            {
                Assert.AreEqual(SymbolPairRole.Owner, block.PairRoles[0], "cross-call adjacency: owner resolves");
                Assert.AreEqual(SymbolPairRole.Rider, block.PairRoles[1], "cross-call adjacency: rider resolves");
            }
            finally { block.Dispose(); }
        }

        private static void AssertNamedSymbol(List<SymbolStyle.SymbolFeature> extracted, SymbolTileBuffer buffer,
            string name, int expectedQuads)
        {
            int idx = extracted.FindIndex(e => e.Text == name);
            Assert.Greater(idx, -1, $"fixture must contain '{name}'");
            Assert.AreEqual(expectedQuads, buffer.Symbols[idx].QuadCount, $"'{name}' → {expectedQuads} glyph quads");
        }

        private static bool IsAsciiNoSpace(string s)
        {
            foreach (char c in s)
                if (c <= 32 || c > 126) return false;
            return true;
        }
    }
}
