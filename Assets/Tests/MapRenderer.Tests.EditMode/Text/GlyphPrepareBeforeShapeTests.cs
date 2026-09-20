// Unity EditMode only. Mirrors StyledSymbolTileBuilderTests' fixture-loading idiom — it drives
// StyledSymbolTileBuilder, whose SymbolFeatureExtractor.Extract call depends on Unity.Collections
// transitively, so this cannot run in Tools/core-tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests; // TestGlyphSource

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Glyph-fetch hoist T1–T4 (T5 lives in <see cref="SymbolTailPumpTests"/> — its two
    /// structural halves need SOURCE FILES this fixture-driven suite has no reason to touch, and its
    /// behavioural half needs the subsystem harness in <c>SymbolSubsystemWorkSchedulerTests</c>).
    ///
    /// <para><b>Every tooth here needs ≥ 2 distinct <c>(fontName, rangeStart)</c> keys</b>
    /// — a fixture that lacks the keys an oracle reads makes it vacuous. Every OTHER symbol fixture in this repo
    /// is single-font/single-range, so it cannot observe either the interleave this stage removes or a
    /// collect-order shuffle. T1/T2 below deliberately use two font names (T1) or two distinct ranges (T2)
    /// so the tooth is falsifiable, not just green-by-construction.</para>
    /// </summary>
    [TestFixture]
    public class GlyphPrepareBeforeShapeTests
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

        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static SymbolStyle.StyleLayer CentroidsLayer(string id, string fontName) => new SymbolStyle.StyleLayer
        {
            Id = id,
            LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
            SourceLayer = "centroids",
            Paint = SymbolStyle.PaintProperties.Parse(null),
            Layout = SymbolStyle.LayoutProperties.Parse(JsonParser.Parse(
                "{\"text-field\":\"{NAME}\",\"text-size\":16,\"text-font\":[\"" + fontName + "\"]}")),
        };

        private static SymbolStyle.SymbolFeature PointSymbol(string text) => new SymbolStyle.SymbolFeature
        {
            Text = text,
            Placement = SymbolPlacement.Point,
            LayoutOptions = TextLayoutOptions.Default,
            TextSizePx = 16f,
        };

        // ── T1 — the fetch happens once per tile, before any shaping ──────────────────────────────────

        /// <summary>T1 (the primary behavioural tooth): every glyph-range fetch for a build completes before
        /// that build shapes its first symbol. Two style layers over the SAME source-layer with DIFFERENT
        /// <c>text-font</c> names ("FontA"/"FontB") give two distinct <c>(fontName, rangeStart)</c> keys from
        /// Latin fixture text alone — a single-font fixture cannot see the interleave this stage removes.
        /// <para><b>RED injection:</b> restore the interleave — move the collect+ensure back inside a
        /// per-layer loop (re-create the pre-hoist pass 1 for one layer at a time) — layer B's fetch then
        /// records a non-zero <c>Symbols.Count</c>.</para></summary>
        [Test]
        public async Task EveryGlyphFetchPrecedesTheFirstShapedSymbol()
        {
            byte[] latin = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            var buffer = new SymbolTileBuffer();
            var fetches = new List<(string FontName, int RangeStart, int SymbolsAtFetchTime)>();
            var source = new TestGlyphSource((fontName, rangeStart, ct) =>
            {
                fetches.Add((fontName, rangeStart, buffer.Symbols.Count));
                return UniTask.FromResult(new GlyphRangeResponse(latin));
            });
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            using MvtTile tile = MvtDecoder.Decode(FixtureTile, LoadUp("Assets", "Fixtures", "sample-tile.bytes"));
            var projection = new WebMercatorProjection();
            var layers = new List<SymbolStyle.StyleLayer>
            {
                CentroidsLayer("labels-a", "FontA"),
                CentroidsLayer("labels-b", "FontB"),
            };

            await builder.BuildAsync(tile, FixtureTile, layers, 0.0, projection, buffer);

            Assert.GreaterOrEqual(fetches.Count, 2,
                "sanity: two differently-named font stacks over Latin text must drive at least two distinct " +
                "(fontName, rangeStart) fetches — a fixture that drives none proves nothing.");
            foreach (var fetch in fetches)
                Assert.AreEqual(0, fetch.SymbolsAtFetchTime,
                    $"fetch of ({fetch.FontName}, {fetch.RangeStart}) observed {fetch.SymbolsAtFetchTime} " +
                    "already-shaped symbols — every fetch must happen BEFORE the build shapes its first symbol.");
            Assert.Greater(buffer.Symbols.Count, 0,
                "sanity: the build must actually shape symbols, or the zero-symbols-at-fetch-time check above " +
                "is vacuously true.");
        }

        // ── T2 — collected order is first-encounter order (atlas packing depends on it) ───────────────

        /// <summary>T2: <see cref="GlyphAtlas"/> is an insertion-order shelf packer, so the order
        /// <see cref="StyledSymbolTileBuilder.CollectRequiredRanges"/> emits ranges in IS the fetch order IS
        /// the atlas layout IS every baked glyph's UV. One layer's text spans TWO distinct rangeStarts (Latin
        /// + Arabic) over a TWO-name stack, so the code-unit-outer/name-inner nesting is genuinely
        /// falsifiable — a text that collapses to one shared rangeStart (e.g. all-ASCII) would produce the
        /// SAME emitted order under either nesting and prove nothing.
        /// <para><b>RED injection:</b> swap the stack-name loop above the code-unit loop — the emitted order
        /// becomes name-major instead of code-unit-major and reddens this test. (Emitting from the dedup set
        /// instead of the ordered list is the plan's other named injection; left to the developer's own
        /// RED-verify run since <c>HashSet&lt;T&gt;</c> enumeration order is an implementation detail this
        /// fixture does not need to pin down to falsify the nesting order.)</para></summary>
        [Test]
        public void CollectedRangesAreInFirstEncounterOrder()
        {
            using var manager = new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));
            var builder = new StyledSymbolTileBuilder(manager);

            // U+0628 (Arabic beh) -> rangeStart (0x0628 / 256) * 256 = 1536 — DIFFERENT from 'A'/(rangeStart 0),
            // so code-unit-outer vs name-outer nesting produce genuinely different sequences (see doc above).
            var layer1 = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { "FontA", "FontB" } },
                new List<SymbolStyle.SymbolFeature> { PointSymbol("Aب") });
            var layer2 = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { "FontC" } },
                new List<SymbolStyle.SymbolFeature> { PointSymbol("ب") });
            var extractedLayers = new List<StyledSymbolTileBuilder.ExtractedLayer> { layer1, layer2 };

            var ranges = new List<(string FontName, int RangeStart)>();
            var seen = new HashSet<(string FontName, int RangeStart)>();
            builder.CollectRequiredRanges(extractedLayers, ranges, seen);

            var expected = new List<(string FontName, int RangeStart)>
            {
                ("FontA", 0), ("FontB", 0), ("FontA", 1536), ("FontB", 1536), ("FontC", 1536),
            };
            CollectionAssert.AreEqual(expected, ranges,
                "collected ranges must be in FIRST-ENCOUNTER order (layer -> symbol -> code unit -> stack " +
                "name) with no duplicates — a reorder here silently diffs every baked glyph's UV.");
        }

        // ── T3 — Shape mutates no glyph atlas ──────────────────────────────────────────────────────────

        /// <summary>T3: the invariant step 3 (dispatching Shape off-main) rests on — its own tooth, not an
        /// inference from "no ensure during shaping". Builds extracted layers with non-empty text, DELIBERATELY
        /// skips the ensure step, and asserts <see cref="StyledSymbolTileBuilder.Shape"/> neither grows the
        /// atlas nor fetches anything.
        /// <para><b>RED injection:</b> re-add the pre-hoist pass-1 fetch loop inside <c>Shape</c> (making it
        /// <c>async</c> again) — the atlas then grows during shaping.</para></summary>
        [Test]
        public void ShapeDoesNotAppendToTheAtlas()
        {
            byte[] latin = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            var source = new TestGlyphSource((fontName, rangeStart, ct) =>
                UniTask.FromResult(new GlyphRangeResponse(latin))); // WOULD serve real glyphs, if ever asked
            using var manager = new GlyphManager(source);
            var builder = new StyledSymbolTileBuilder(manager);

            var symbols = new List<SymbolStyle.SymbolFeature> { PointSymbol("Aruba") };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(
                0, new FontStack { Names = new[] { "LatinFont" } }, symbols);
            var extractedLayers = new List<StyledSymbolTileBuilder.ExtractedLayer> { layer };

            Assert.AreEqual(0, manager.Atlas.Count, "PRECONDITION: nothing fetched/appended before Shape runs.");

            var output = new SymbolTileBuffer();
            builder.Shape(extractedLayers, output); // deliberately skips EnsureGlyphRangesAsync

            Assert.Greater(output.Symbols.Count, 0,
                "sanity: Shape must actually have done work, or the atlas/fetch-count checks below are " +
                "vacuously true of an early-returning Shape too.");
            Assert.AreEqual(0, manager.Atlas.Count, "Shape must never append to the glyph atlas.");
            Assert.AreEqual(0, source.FetchCount, "Shape must never fetch a glyph range either.");
        }

        // ── T4 (reflection half) — CompleteOnMain / Shape are genuinely synchronous ───────────────────

        /// <summary>T4 reflection half: neither the interface contract nor its concrete implementors are an
        /// async method in disguise. The compiler emits <see cref="AsyncStateMachineAttribute"/> on every
        /// <c>async</c> method — including <c>async void</c> — so this catches a form a bare <c>void</c>
        /// return type does not. Complements <c>SymbolTailPumpTests.RunTailAsync_AwaitsOnlyTheGlyphPrepare_BeforeTheShapeLoop</c>'s
        /// structural half — a different instrument reading a different thing
        /// — one instrument's blind spot does not transfer to another.
        /// <para><b>RED injection:</b> mark <c>StyledSymbolTileBuilder.Shape</c> <c>async void</c> with any
        /// <c>await</c> in it.</para></summary>
        [Test]
        public void TheMainTailHasNoSuspensionPoint()
        {
            MethodInfo completeOnMainIface = typeof(ITileWorkerThenMainLayerProcessor).GetMethod("CompleteOnMain");
            Assert.IsNotNull(completeOnMainIface, "sanity: the interface method must exist.");
            Assert.AreEqual(typeof(void), completeOnMainIface.ReturnType,
                "ITileWorkerThenMainLayerProcessor.CompleteOnMain must return void — no suspension point.");

            MethodInfo completeOnMainImpl = typeof(TileSymbolLayerProcessor).GetMethod(
                "CompleteOnMain", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(completeOnMainImpl, "sanity: the concrete implementor must exist.");
            Assert.IsNull(completeOnMainImpl.GetCustomAttribute<AsyncStateMachineAttribute>(),
                "TileSymbolLayerProcessor.CompleteOnMain must not be an async method in disguise — the " +
                "compiler emits AsyncStateMachineAttribute on every async method, including 'async void'.");

            MethodInfo shape = typeof(StyledSymbolTileBuilder).GetMethod(
                "Shape", BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(shape, "sanity: StyledSymbolTileBuilder.Shape must exist.");
            Assert.IsNull(shape.GetCustomAttribute<AsyncStateMachineAttribute>(),
                "StyledSymbolTileBuilder.Shape must not be an async method in disguise.");
        }
    }
}
