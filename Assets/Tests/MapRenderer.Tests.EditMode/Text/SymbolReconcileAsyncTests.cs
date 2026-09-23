// Text/SymbolReconcileAsyncTests.cs — glyph atlas/fetch teeth, the symbol feature-buffer/reconcile pipeline (sync and off-main async), and the road-shields sprite-readiness race.
//
// Roughly pipeline order: glyph atlas allocation/texture/fetch-hoist, then the symbol buffer/reconcile fixtures (address, parity, dedup, material, processor parity, async reconcile, reconciler, shared buffer), then sprite readiness.
//
// Contents:
//   GlyphAtlasAllocTests            — Appending an already-decoded glyph to the atlas, and shaping a cached run into a caller-owned buffer, must allocate ZERO managed garbage on the steady path.
//   GlyphAtlasTextureTests          — Texel-from-texture: uploads a decoded glyph's atlas region via GlyphAtlasTexture and reads a texel on the glyph's edge back from the uploaded Texture2DArray's CPU-side buffer, confirming it matches the source Pixels byte exactly…
//   GlyphPrepareBeforeShapeTests    — Glyph-fetch hoist: a build fetches every glyph range before it shapes,
//                                     and shaping is synchronous (the tail's structural teeth live in SymbolTailPumpTests,
//                                     its behavioural tooth in SymbolSubsystemWorkSchedulerTests).
//   SymbolBufferAddressTests        — the symbol consumer reads its tile address, its extent and its feature count off the BUFFER, not off a caller-supplied argument and not off the layer's feature list.
//   SymbolBufferParityTests         — the teeth on moving SymbolFeatureExtractor off its own managed MvtGeometry.Decode and onto the shared TileGeometryBuffers.
//   SymbolDedupZoomInvarianceTests  — Headline tooth: the cross-tile dedup winner set is a pure function of the tile set — INVARIANT across display zoom.
//   SymbolLayerMaterialTests        — Each symbol style layer gets its OWN material — a distinct SymbolTextWorld clone (NOT one shared material) — with its text-halo-* bound by name.
//   SymbolProcessorParityTests      — the PRIMARY semantic tooth: the differential — production SymbolSubsystem output, driven through the processor machinery, deep-equals a single-pass BuildAsync ORACLE fed the same bytes/style/camera-zoom/projection over…
//   SymbolReconcileAsyncTests       — The OFF-MAIN reconcile state machine, driven end-to-end through the production SymbolSubsystem: off-main dedup, one-in-flight + apply-stale + stale-front-held, fault → no swap + reschedule, restyle drains +…
//   SymbolReconcilerTests           — The off-main SymbolReconciler + the store's native pin guard.
//   SymbolSharedBufferTests         — the symbol extractor buckets rings by the source layer's own feature
//                                     ordinals, not by the selected feature list.
//   SymbolSpriteReadinessTests      — Road-shields (docs/road-shields-design.md) — the sprite-atlas readiness race.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Text;
using System;
using System.IO;
using UnityEngine;
using MapRenderer.Unity.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Tile.Processing;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests; // TestGlyphSource
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Json;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolTileBuffer
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using System.Collections;
using UnityEngine.TestTools;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using Symbol = MapRenderer.Core.Style.Symbol;
using System.Threading;
using Unity.Collections;
using MapRenderer.Tests.Jobs;
using MapRenderer.Core.Text.Sprites;
using Object = UnityEngine.Object;
using StyleLayer = MapRenderer.Core.Style.StyleLayer;


namespace MapRenderer.Tests.Text
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // GlyphAtlasAllocTests — appending a decoded glyph and shaping a cached run allocate zero garbage
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class GlyphAtlasAllocTests
    {
        // =========================================================================================
        // (a) Append an already-decoded glyph. Deterministic zero-alloc setup: a WIDE atlas (so many
        //     same-size cells fit on one shelf, never wrapping to a new -- taller-usedHeight -- row)
        //     plus re-appending the SAME codepoint (so the entries-dictionary write is an in-place
        //     overwrite of an EXISTING key, which can never trigger a Dictionary capacity resize).
        //     With both variables pinned, GrowToFit's buffer realloc genuinely never fires after the
        //     first (unmeasured) warm-up append -- no reliance on guessing CLR dictionary growth points.
        // =========================================================================================
        [Test]
        public void Append_AlreadyDecodedGlyph_AllocatesNoGCMemory()
        {
            var atlas = new GlyphAtlas(width: 4096); // wide enough that this glyph's cell never wraps shelves
            SdfGlyph glyph = MakeSyntheticGlyph(codepoint: 1u, width: 4, height: 4, advance: 5);

            // Warm-up: the FIRST Append legitimately allocates (buffer created from empty, dictionary
            // backing arrays initialized on the first Add).
            atlas.Append(glyph, 0);

            // Block-bodied lambda (not an expression lambda): Append returns a value (GlyphAtlasEntry),
            // and Assert.That needs a void TestDelegate here — an expression lambda binds to the wrong
            // overload and fails with "actual value must be a TestDelegate" (see TileLoadMeasurementTests).
            Assert.That(() => { atlas.Append(glyph, 0); }, Is.Not.AllocatingGCMemory(),
                "re-appending an already-decoded glyph (same codepoint, same cell height, same open shelf) " +
                "must not allocate: GrowToFit no-ops once the shelf's used height stops increasing, and " +
                "Dictionary[key]= on an EXISTING key overwrites in place without growing capacity.");
        }

        private static SdfGlyph MakeSyntheticGlyph(uint codepoint, int width, int height, int advance)
        {
            int2 cellSize = new int2(width + 2 * GlyphSdf.Buffer, height + 2 * GlyphSdf.Buffer);
            return new SdfGlyph
            {
                Codepoint = codepoint,
                Width = width,
                Height = height,
                Left = 0,
                Top = 0,
                Advance = advance,
                Bitmap = new byte[cellSize.x * cellSize.y],
            };
        }

        // =========================================================================================
        // (b) Shape a cached run (steady LTR path) into a caller-owned List<PositionedGlyph> buffer.
        //     Warm-up call lets the List's backing array reach its stable capacity; the measured call
        //     reuses it -- CodepointTextShaper.Shape(in request, output) writes PositionedGlyph VALUE
        //     structs directly into `output`, with no intermediate codepoint/cluster lists and no
        //     ShapedRun class allocation on this path (see its doc comment).
        // =========================================================================================
        [Test]
        public void Shape_CachedLatinRun_IntoCallerBuffer_AllocatesNoGCMemory()
        {
            var metrics = new FixedAdvanceMetrics(12f);
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = "Hello Map Symbols", FontStack = null, Metrics = metrics };
            var output = new List<PositionedGlyph>(32);

            // Warm-up: stabilizes `output`'s backing array capacity.
            shaper.Shape(in request, output);

            Assert.That(() => { shaper.Shape(in request, output); }, Is.Not.AllocatingGCMemory(),
                "Shape(in request, output) must not allocate on the steady LTR path once the caller " +
                "buffer's capacity has stabilized from the warm-up call.");
        }

        private sealed class FixedAdvanceMetrics : IGlyphMetricsProvider
        {
            private readonly float _advance;
            public FixedAdvanceMetrics(float advance) => _advance = advance;
            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                advance = _advance;
                return true;
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlyphAtlasTextureTests — a decoded glyph's atlas region round-trips through the uploaded texture
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Texel-from-texture: uploads a decoded glyph's atlas region via
    /// <see cref="GlyphAtlasTexture"/> and reads a texel on the glyph's edge back from the uploaded
    /// <see cref="Texture2DArray"/>'s CPU-side buffer, confirming it matches the source
    /// <see cref="GlyphAtlas.Pixels"/> byte exactly AND is graded (mid-range), not flat 0/255 — the
    /// same "real SDF, not a coverage bitmap" guard <c>SdfDistanceFieldTests</c> applies to the raw
    /// decoded bitmap, applied end-to-end through the GPU-texture upload.
    /// </summary>
    [TestFixture]
    public class GlyphAtlasTextureTests
    {
        // Same mid-band thresholds as SdfDistanceFieldTests: "graded", not pinning an exact width.
        private const int MidBandLo = 32;
        private const int MidBandHi = 223;

        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static SdfGlyph LoadUppercaseA()
            => GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0].Glyphs[65u];

        [Test]
        public void Upload_DecodedGlyph_TexelOnEdgeMatchesCpuPixelsAndIsGraded()
        {
            SdfGlyph a = LoadUppercaseA();
            var atlas = new GlyphAtlas();
            GlyphAtlasEntry entry = atlas.Append(a, 0);

            var atlasTexture = new GlyphAtlasTexture();
            try
            {
                atlasTexture.Upload(atlas);
                Texture2DArray texture = atlasTexture.Texture;

                Assert.IsNotNull(texture, "Upload must create a Texture2DArray once the atlas has packed a glyph");
                Assert.AreEqual(atlas.Size.x, texture.width);
                Assert.AreEqual(atlas.Size.y, texture.height);
                Assert.AreEqual(1, texture.depth, "single-page behaviour is byte-identical — one array layer");
                Assert.AreEqual(0, entry.Page, "every glyph is Page 0 on the single-page path");

                int2 local = FindGradedTexelLocal(a.Bitmap, entry.CellSize);
                byte expected = a.Bitmap[local.y * entry.CellSize.x + local.x];

                // GetPixelData<byte> reads the texture's CPU-side buffer directly (1 byte/texel for
                // No float round-trip, no channel ambiguity if the format ever falls back to
                // Alpha8 — unlike GetPixel().r, which would silently read 0 from an Alpha8 texture).
                var raw = texture.GetPixelData<byte>(0, entry.Page);
                int px = entry.AtlasOrigin.x + local.x;
                int py = entry.AtlasOrigin.y + local.y;
                byte actual = raw[py * texture.width + px];

                Assert.AreEqual(expected, actual,
                    $"texel at cell-local ({local.x},{local.y}) must round-trip byte-exact through the uploaded texture");
                Assert.Greater(actual, MidBandLo, "the edge texel must be graded (mid-range), not flat 0/255 -- an SDF, not a coverage bitmap");
                Assert.Less(actual, MidBandHi, "the edge texel must be graded (mid-range), not flat 0/255 -- an SDF, not a coverage bitmap");
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        [Test]
        public void Upload_EmptyAtlas_IsANoOp()
        {
            var atlas = new GlyphAtlas(); // nothing appended -> Size.y == 0
            var atlasTexture = new GlyphAtlasTexture();

            Assert.DoesNotThrow(() => atlasTexture.Upload(atlas));
            Assert.IsNull(atlasTexture.Texture, "an atlas with nothing packed yet must not create a Texture2DArray");
        }

        [Test]
        public void Upload_AfterGrowth_RecreatesTextureAtNewSize()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            var atlasTexture = new GlyphAtlasTexture();

            try
            {
                atlas.Append(stack.Glyphs[65u], 0); // 'A'
                atlasTexture.Upload(atlas);
                int2 firstSize = new int2(atlasTexture.Texture.width, atlasTexture.Texture.height);

                // Appending enough more glyphs to force the packer/atlas to grow taller.
                foreach (var kv in stack.Glyphs) atlas.Append(kv.Value, 0);
                atlasTexture.Upload(atlas);

                Assert.AreEqual(atlas.Size.x, atlasTexture.Texture.width);
                Assert.AreEqual(atlas.Size.y, atlasTexture.Texture.height);
                Assert.GreaterOrEqual(atlasTexture.Texture.height, firstSize.y, "the re-uploaded texture must cover the grown atlas");
                Assert.AreEqual(1, atlasTexture.Texture.depth, "grow mode never pages — always one array layer");
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // =========================================================================================
        // Multi-page, texture half: a fixed atlas forced to page (a small page + enough glyphs to overflow it)
        // uploads a Texture2DArray with ONE LAYER PER PAGE, and page 1's uploaded bytes match the SOURCE
        // glyph's bitmap at its page-1 origin — proving the second array layer actually carries the
        // overflowed glyph's pixels, not empty/garbage data.
        // =========================================================================================
        [Test]
        public void Upload_FixedAtlasForcedToPage_UploadsOneArrayLayerPerPage()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

            // A page just wide/tall enough for ONE glyph's cell — the second appended glyph overflows to page 1.
            SdfGlyph a = stack.Glyphs[65u]; // 'A'
            SdfGlyph b = stack.Glyphs[66u]; // 'B'
            int2 cellA = a.CellSize, cellB = b.CellSize;
            int pageWidth = math.max(cellA.x, cellB.x);
            int pageHeight = math.max(cellA.y, cellB.y);
            var atlas = new GlyphAtlas(width: pageWidth, fixedHeight: pageHeight);

            GlyphAtlasEntry entryA = atlas.Append(a, 0);
            GlyphAtlasEntry entryB = atlas.Append(b, 0);
            Assert.AreEqual(0, entryA.Page, "fixture precondition: 'A' fits page 0");
            Assert.AreEqual(1, entryB.Page, "fixture precondition: 'B' overflows onto page 1");
            Assert.AreEqual(2, atlas.PageCount);

            var atlasTexture = new GlyphAtlasTexture();
            try
            {
                atlasTexture.Upload(atlas);
                Texture2DArray texture = atlasTexture.Texture;

                Assert.IsNotNull(texture);
                Assert.AreEqual(2, texture.depth, "one Texture2DArray layer per GlyphAtlas page");
                Assert.AreEqual(atlas.Size.x, texture.width);
                Assert.AreEqual(atlas.Size.y, texture.height);

                var layer1 = texture.GetPixelData<byte>(0, 1);
                for (int row = 0; row < cellB.y; row++)
                {
                    for (int col = 0; col < cellB.x; col++)
                    {
                        byte expected = b.Bitmap[row * cellB.x + col];
                        byte actual = layer1[(entryB.AtlasOrigin.y + row) * texture.width + entryB.AtlasOrigin.x + col];
                        Assert.AreEqual(expected, actual, $"layer-1 pixel mismatch at cell-local ({col},{row})");
                    }
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        /// <summary>Finds the first cell-local (x,y) whose CPU bitmap byte falls in the graded mid-band.</summary>
        private static int2 FindGradedTexelLocal(byte[] bitmap, int2 cellSize)
        {
            for (int y = 0; y < cellSize.y; y++)
            {
                for (int x = 0; x < cellSize.x; x++)
                {
                    byte v = bitmap[y * cellSize.x + x];
                    if (v > MidBandLo && v < MidBandHi) return new int2(x, y);
                }
            }
            throw new InvalidOperationException("fixture precondition: 'A' must contain at least one graded mid-band texel");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlyphPrepareBeforeShapeTests — glyph-fetch hoist: every fetch before any shaping
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Glyph-fetch hoist: a build fetches every glyph range before it shapes, and shaping is synchronous.
    /// The tail's structural teeth (<c>RunTailAsync_AwaitsOnlyTheGlyphPrepare_BeforeTheShapeLoop</c>,
    /// <c>CollectRequiredRanges_RunsOnlyInsideRunTailAsync_NeverOnTheWorker</c>,
    /// <c>ReadySymbolTail_NeverCarriesADecodeReference</c>) live in <see cref="SymbolTailPumpTests"/>, because
    /// they need SOURCE FILES this fixture-driven suite has no reason to touch. Its behavioural tooth
    /// (<c>CancelDuringGlyphPrepare_UnwindsBeforeShapeOrCommit_ReleasesTheDecodeExactlyOnce</c>) lives in
    /// <c>SymbolSubsystemWorkSchedulerTests</c>, which has the subsystem harness it needs.
    ///
    /// <para><b>Every tooth here needs ≥ 2 distinct <c>(fontName, rangeStart)</c> keys</b>
    /// — a fixture that lacks the keys an oracle reads makes it vacuous. Every OTHER symbol fixture in this repo
    /// is single-font/single-range, so it cannot observe either a fetch/shape interleave or a
    /// collect-order shuffle. The first two tests below use two font names or two distinct ranges
    /// so the tooth is falsifiable, not just green.</para>
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
            Paint = TestStyle.SymbolPaint(),
            Layout = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\",\"text-size\":16,\"text-font\":[\"" + fontName + "\"]}"),
        };

        private static SymbolStyle.SymbolFeature PointSymbol(string text) => new SymbolStyle.SymbolFeature
        {
            Text = text,
            Placement = SymbolPlacement.Point,
            LayoutOptions = TextLayoutOptions.Default,
            TextSizePx = 16f,
        };

        // ── the fetch happens once per tile, before any shaping ───────────────────────────────────────

        /// <summary>The primary behavioural tooth: every glyph-range fetch for a build completes before
        /// that build shapes its first symbol. Two style layers over the SAME source-layer with DIFFERENT
        /// <c>text-font</c> names ("FontA"/"FontB") give two distinct <c>(fontName, rangeStart)</c> keys from
        /// Latin fixture text alone — a single-font fixture cannot see a fetch/shape interleave.
        /// <para><b>RED injection:</b> interleave them — move the collect+ensure inside a
        /// per-layer loop (fetch and shape one layer at a time) — layer B's fetch then
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

        // ── collected order is first-encounter order (atlas packing depends on it) ────────────────────

        /// <summary><see cref="GlyphAtlas"/> is an insertion-order shelf packer, so the order
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

        // ── Shape mutates no glyph atlas ───────────────────────────────────────────────────────────────

        /// <summary>The invariant that dispatching Shape off-main rests on — its own tooth, not an
        /// inference from "no ensure during shaping". Builds extracted layers with non-empty text, DELIBERATELY
        /// skips the ensure step, and asserts <see cref="StyledSymbolTileBuilder.Shape"/> neither grows the
        /// atlas nor fetches anything.
        /// <para><b>RED injection:</b> add a glyph-range fetch loop inside <c>Shape</c> (making it
        /// <c>async</c>) — the atlas then grows during shaping.</para></summary>
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

        // ── reflection half — CompleteOnMain / Shape are genuinely synchronous ────────────────────────

        /// <summary>Reflection half: neither the interface contract nor its concrete implementors are an
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolBufferAddressTests — the symbol consumer reads tile address/extent/count off the buffer
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The symbol consumer reads its tile address, its extent and its feature
    /// count off the BUFFER</b>, not off a caller-supplied argument and not off the layer's feature list.
    ///
    /// <para>The second address copy is gone from the mesh consumers —
    /// <c>ITileMeshRenderLayer.WriteInto</c> lost its <c>TileId</c> parameter and fill/line read
    /// <c>geometry.Tile</c> — but symbol kept taking one, so <c>MvtDecoder</c>'s claim that the address
    /// "enters the pipeline exactly ONCE, at the fetch" was false for exactly one consumer. These are the
    /// teeth for closing it; each one moves ONE of the three quantities and pins that the output follows the
    /// buffer.</para>
    /// </summary>
    [TestFixture]
    public class SymbolBufferAddressTests
    {
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-tile.bytes not found walking up from cwd/AppContext.");
        }

        private static readonly TileId DecodedAt = new TileId { Z = 0, X = 0, Y = 0 };
        private static readonly TileId WrongTile = new TileId { Z = 1, X = 1, Y = 0 };

        private static SymbolStyle.StyleLayer CentroidsLayer() => new SymbolStyle.StyleLayer
        {
            Id          = "labels",
            LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
            SourceLayer = "centroids",
            Paint       = TestStyle.SymbolPaint(),
            Layout      = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\"}"),
        };

        private static List<SymbolStyle.SymbolFeature> Extract(
            SymbolStyle.StyleLayer layer, IDecodedTile tile, TileId callerSuppliedId)
        {
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, callerSuppliedId, 0.0, new WebMercatorProjection(), symbols);
            return symbols;
        }

        // ── The ADDRESS, in the production configuration ──────────────────────────────────────────────

        /// <summary>
        /// A real <c>MvtTile</c>, decoded at one address and then extracted with a DIFFERENT address handed
        /// to <c>Extract</c>. Nothing synthetic: this is precisely the mispairing the caller-supplied
        /// parameter makes expressible, and the whole reason the mesh seam stopped taking one.
        /// </summary>
        [Test]
        public void TheProjectionUsesTheBuffersOwnAddress_NotTheCallerSuppliedOne()
        {
            byte[] bytes = LoadFixture();
            MvtTile decodedHere  = TestDecodedTiles.Track(MvtDecoder.Decode(DecodedAt, bytes));
            MvtTile decodedThere = TestDecodedTiles.Track(MvtDecoder.Decode(WrongTile, bytes));

            List<SymbolStyle.SymbolFeature> fromHere  = Extract(CentroidsLayer(), decodedHere,  DecodedAt);
            List<SymbolStyle.SymbolFeature> fromThere = Extract(CentroidsLayer(), decodedThere, WrongTile);

            // Anti-vacuity, and it is the whole tooth: if the address did not move the output, corrupting it
            // below would prove nothing. Assert BOTH observable consequences separately — the projected
            // anchor and the packed TileKey — so a change that stops one from depending on the address
            // cannot hide behind the other.
            Assert.Greater(fromHere.Count, 0, "precondition: the fixture must yield symbols at all");
            Assert.AreEqual(fromHere.Count, fromThere.Count, "precondition: the same features are selected either way");
            Assert.AreNotEqual(fromHere[0].AnchorRender.x, fromThere[0].AnchorRender.x,
                "precondition: the tile address must genuinely move the projected anchor");
            Assert.AreNotEqual(fromHere[0].TileKey, fromThere[0].TileKey,
                "precondition: the tile address must genuinely move the packed TileKey");

            // The tooth: same decoded tile (buffer stamped DecodedAt), a CORRUPTED caller-supplied address.
            List<SymbolStyle.SymbolFeature> corruptedCaller = Extract(CentroidsLayer(), decodedHere, WrongTile);

            Assert.AreEqual(fromHere.Count, corruptedCaller.Count);
            for (int i = 0; i < fromHere.Count; i++)
            {
                Assert.AreEqual(fromHere[i].AnchorRender.x, corruptedCaller[i].AnchorRender.x,
                    $"label {i}: the anchor must come from the BUFFER's address, not the caller's");
                Assert.AreEqual(fromHere[i].AnchorRender.y, corruptedCaller[i].AnchorRender.y, $"label {i} (y)");
                Assert.AreEqual(fromHere[i].AnchorRender.z, corruptedCaller[i].AnchorRender.z, $"label {i} (z)");
                Assert.AreEqual(fromHere[i].TileKey, corruptedCaller[i].TileKey,
                    $"label {i}: the TileKey must be packed from the BUFFER's address");
            }
        }

        // ── The EXTENT ────────────────────────────────────────────────────────────────────────────────

        /// <summary>An <see cref="ITileLayer"/> that forwards everything except <see cref="Extent"/>, which
        /// it misreports. <b>Synthetic by necessity</b>: for MVT the layer's extent and its buffer's extent
        /// are the same value by construction (<c>MvtDecoder</c> stamps one into the other), so no
        /// production configuration can tell the two reads apart today. The shape becomes constructible the
        /// moment a second <see cref="ITileLayer"/> producer exists, which is what this pins against.</summary>
        private sealed class ExtentMisreportingLayer : ITileLayer
        {
            private readonly ITileLayer _inner;
            public ExtentMisreportingLayer(ITileLayer inner, uint misreportedExtent)
            {
                _inner = inner;
                Extent = misreportedExtent;
            }

            public string                  Name     => _inner.Name;
            public uint                    Extent   { get; }
            public IReadOnlyList<IFeature> Features => _inner.Features;
            public TileGeometryBuffers     Geometry => _inner.Geometry;
        }

        /// <summary>A one-layer <see cref="IDecodedTile"/> over a supplied layer. Owns nothing — the
        /// wrapped layer's buffer belongs to the tile <see cref="TestDecodedTiles"/> already tracks.</summary>
        private sealed class SingleLayerTile : IDecodedTile
        {
            private readonly ITileLayer _layer;
            public SingleLayerTile(ITileLayer layer) => _layer = layer;
            public ITileLayer GetLayer(string name) => name == _layer.Name ? _layer : null;
            public void Dispose() { }
        }

        /// <summary>Protobuf zigzag ENcode, for hand-building a one-point MVT command stream.</summary>
        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        [Test]
        public void TheProjectionUsesTheBuffersOwnExtent_NotTheLayersDeclaredOne()
        {
            const uint bufferExtent      = 4096;
            const uint misreportedExtent = 2048;

            // One point at (2048, 2048) — dead centre of a 4096 tile, and exactly ON the upper bound of a
            // 2048 one. EmitAtAnchor's single-world clip is `[0, extent)`, so reading the misreported extent
            // does not merely shift the anchor, it DROPS the symbol: a discriminator with no tolerance in it.
            var point = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Point, hasId: false, geometry: new uint[] { (1u) | (1u << 3), ZigZagEncode(2048), ZigZagEncode(2048) });

            InMemoryDecodedTile real = TestDecodedTiles.Of("pts", DecodedAt, new List<IFeature> { point }, bufferExtent);
            ITileLayer          honest = real.GetLayer("pts");

            Assert.AreEqual(bufferExtent, (uint)honest.Geometry.Extent,
                "precondition: the buffer must carry the real extent");

            var layer = new SymbolStyle.StyleLayer
            {
                Id          = "labels",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "pts",
                Paint       = TestStyle.SymbolPaint(),
                Layout      = TestStyle.SymbolLayout("{\"text-field\":\"X\"}"),
            };

            List<SymbolStyle.SymbolFeature> honestSymbols = Extract(layer, real, DecodedAt);
            Assert.AreEqual(1, honestSymbols.Count, "precondition: the honest layer yields exactly one label");

            var lying = new SingleLayerTile(new ExtentMisreportingLayer(honest, misreportedExtent));
            List<SymbolStyle.SymbolFeature> symbols = Extract(layer, lying, DecodedAt);

            Assert.AreEqual(1, symbols.Count,
                "the extent must come from the BUFFER (4096), where the point is mid-tile. Read from the " +
                "layer's declared 2048 instead and the point sits ON the exclusive upper bound of the " +
                "single-world clip, so the label is silently dropped.");
            Assert.AreEqual(honestSymbols[0].AnchorRender.x, symbols[0].AnchorRender.x, "anchor x must not move");
            Assert.AreEqual(honestSymbols[0].AnchorRender.y, symbols[0].AnchorRender.y, "anchor y must not move");
            Assert.AreEqual(honestSymbols[0].AnchorRender.z, symbols[0].AnchorRender.z, "anchor z must not move");
        }

        // ── The FEATURE COUNT the ring buckets are sized from ─────────────────────────────────────────

        /// <summary>An <see cref="ITileLayer"/> exposing a PREFIX of the wrapped layer's features while
        /// forwarding the whole buffer — the mispairing shape <c>MvtLayer</c>'s lockstep rules out for MVT
        /// and nothing structural rules out for a second producer. Synthetic, and deliberately so: the
        /// property under test is "the bucket array is sized to the domain of the index that addresses it",
        /// and no MVT input can separate the two counts.</summary>
        private sealed class TruncatedFeatureListLayer : ITileLayer
        {
            private readonly ITileLayer _inner;
            public TruncatedFeatureListLayer(ITileLayer inner, int exposedCount)
            {
                _inner = inner;
                var kept = new List<IFeature>(exposedCount);
                for (int i = 0; i < exposedCount; i++) kept.Add(inner.Features[i]);
                Features = kept;
            }

            public string                  Name     => _inner.Name;
            public uint                    Extent   => _inner.Extent;
            public IReadOnlyList<IFeature> Features { get; }
            public TileGeometryBuffers     Geometry => _inner.Geometry;
        }

        [Test]
        public void TheRingBucketsAreSizedFromTheBuffersFeatureColumn_NotTheFeatureList()
        {
            var features = new List<IFeature>();
            for (int i = 0; i < 3; i++)
                features.Add(new DictionaryFeature(properties: null, geometryType: TileGeometryType.Point, hasId: false, geometry: new uint[] { (1u) | (1u << 3), ZigZagEncode(100 + i * 100), ZigZagEncode(100) }));

            InMemoryDecodedTile real  = TestDecodedTiles.Of("pts", DecodedAt, features);
            ITileLayer          whole = real.GetLayer("pts");

            Assert.AreEqual(3, whole.Geometry.FeatureCount, "precondition: the buffer's feature column holds 3");
            Assert.AreEqual(3, whole.Geometry.RingCount,    "precondition: one ring per point feature");

            // The highest ring→feature index the buffer carries must EXCEED the truncated list, or the two
            // sizings cannot be told apart.
            int highestRingFeatureIdx = 0;
            for (int r = 0; r < whole.Geometry.RingCount; r++)
                highestRingFeatureIdx = math.max(highestRingFeatureIdx, whole.Geometry.RingFeatureIdx[r]);
            Assert.AreEqual(2, highestRingFeatureIdx,
                "precondition: a ring must be attributed to a feature index beyond the truncated list");

            var truncated = new SingleLayerTile(new TruncatedFeatureListLayer(whole, exposedCount: 2));

            var layer = new SymbolStyle.StyleLayer
            {
                Id          = "labels",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "pts",
                Paint       = TestStyle.SymbolPaint(),
                Layout      = TestStyle.SymbolLayout("{\"text-field\":\"X\"}"),
            };

            // Asserted, not merely observed by the runner catching a throw: the defect's manifestation IS an
            // out-of-range index, so wrapping it makes the failing ASSERTION the one that names the property,
            // rather than a bare stack trace that could be read as an unrelated crash.
            List<SymbolStyle.SymbolFeature> symbols = null;
            Assert.DoesNotThrow(() => symbols = Extract(layer, truncated, DecodedAt),
                "the counting sort must be sized from geometry.FeatureCount — RingFeatureIdx's values index " +
                "the buffer's OWN feature column, so sizing from Features.Count indexes ringStart out of " +
                "range the moment the two disagree (and mis-buckets silently before that).");

            Assert.AreEqual(2, symbols.Count, "one label per exposed feature, bucketed correctly");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolBufferParityTests — moving SymbolFeatureExtractor onto the shared TileGeometryBuffers
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The teeth on <c>SymbolFeatureExtractor</c> reading its paths from the shared
    /// <see cref="TileGeometryBuffers"/> rather than from its own managed <c>MvtGeometry.Decode</c>.
    ///
    /// <para><b>Why a differential oracle, and why NOT <c>SymbolProcessorParityTests</c>.</b> That suite's two
    /// arms BOTH run through <c>StyledSymbolTileBuilder.ExtractLayers</c> → <c>SymbolFeatureExtractor.Extract</c>
    /// — the code that reads the shared buffer — so a defect there lands identically in both and
    /// the tooth cannot disagree about it. It is a valuable REGRESSION tooth, but it is not the acceptance
    /// tooth. <see cref="SymbolPaths_FromTheSharedBuffer_MatchTheManagedDecodeOracle"/> is:
    /// arm A is <c>MvtGeometry.Decode</c>, running live, and arm B is the materializer plus a span read.
    /// The two arms share no helper, so the oracle can
    /// genuinely disagree: at feature f, path p, point i.</para>
    ///
    /// <para><b>Symbol is the consumer that OBSERVES the unfiltered buffer.</b> A short-ring filter in the
    /// shared decode stage reds NOTHING for fill (ring assembly
    /// re-filters); the same holds for line (<c>RibbonJob</c> returns early below 2 points). Symbol's
    /// point branch has <b>no length filter at all</b>, so a 1-point path is a real, rendered symbol —
    /// <see cref="SymbolPointFeature_OnePointPath_StillEmitsOneSymbol"/> is the instrument those two stages
    /// could not build.</para>
    /// </summary>
    [TestFixture]
    public class SymbolBufferParityTests
    {

        /// <summary>A synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this guard exists to catch.</summary>
        [TearDown]
        public void ReleaseFixtureTiles()
        {
            TestDecodedTiles.DisposeAll();
            _decodedFixture = null;
        }
        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double FixtureExtent = 4096.0;
        private const uint   SyntheticExtent = 4096;
        private static readonly TileId SyntheticTileId = new TileId { Z = 1, X = 0, Y = 0 };

        // ── the differential oracle ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The per-feature path list the extractor iterates, read out of the shared buffer, is
        /// element-wise identical to the one the managed decoder produces for the same selected features,
        /// running live in the same process. Everything downstream of that path list
        /// (<c>LineCurvatureSubdivision.Subdivide</c>, <c>LineAnchorPlacement.Compute</c>,
        /// <c>KeepAnchorsInsideTile</c>, <c>ProjectPath</c>, every emit and every style evaluation) does not
        /// depend on where the path list came from, so path-level equality is symbol-level equality; the
        /// symbol extraction suites are the end-to-end confirmation of that implication.
        /// <para>Run over BOTH fixture layers: <c>centroids</c> is the only one carrying 1-point paths (the
        /// case line's oracle could not have) and <c>geolines</c> the only one carrying a feature with more
        /// than one path.</para>
        /// <para>Exact comparison, no tolerance: both decoders accumulate <c>long</c> deltas and emit
        /// <c>(double)</c> of integer magnitudes far inside <c>double</c>'s exact range, so the vertices are
        /// bit-identical. A tolerance here would be a weakened tooth.</para>
        /// <para><b>What this tooth does NOT observe, measured not assumed.</b> Arm B <i>transcribes</i> the
        /// production bucketing and span read rather than calling them, so a defect injected into
        /// <c>Extract</c>'s own <c>ringStart</c>/<c>ringOrder</c>/<c>CopyRing</c> leaves this test green —
        /// RED-verification shows exactly that (reversed within-feature order, an off-by-one path count, and a
        /// zero-length ring copy all reddened dozens of symbol suites and none of them reddened this test).
        /// The claim here is therefore about the DECODERS — that the Burst job and the managed decoder agree
        /// bit-for-bit, in order, unfiltered — which is the substance of the shared-buffer read, and for which
        /// this tooth IS armed (a short-ring filter in the shared stage reds it). Production's own path
        /// ordering is observed by <see cref="SymbolOrder_WithinAFeature_FollowsDecodeOrder"/> below.</para>
        /// </summary>
        [Test]
        public void SymbolPaths_FromTheSharedBuffer_MatchTheManagedDecodeOracle()
        {
            var kindsSeen = new HashSet<TileGeometryType>();
            int sawLengthOne = 0, sawMultiPathFeature = 0, totalPoints = 0, totalEntries = 0;
            bool anyNonZeroFeatureIdx = false;

            foreach (string sourceLayer in new[] { "centroids", "geolines" })
            {
                MvtFixtureStreams.Layer fixture = MvtFixtureStreams.ReadLayer(LoadFixture(), sourceLayer);
                Assert.IsNotNull(fixture, $"the fixture must carry a '{sourceLayer}' layer");
                ITileLayer decodedLayer = DecodedFixture().GetLayer(sourceLayer);
                Assert.IsNotNull(decodedLayer, $"the decoded fixture must carry a '{sourceLayer}' layer");
                IReadOnlyList<IFeature> features = SelectedFeatures(sourceLayer);
                Assert.AreEqual(fixture.Kinds.Count, features.Count,
                    "this fixture's symbol layer has no filter, so the selection IS the whole source-layer — " +
                    "arm A (the independent fixture reader) and arm B (production) must therefore index the " +
                    "same feature ordinals");
                Assert.Greater(features.Count, 0,
                    $"precondition: the '{sourceLayer}' layer must select > 0 features");

                // ── Arm A — the managed decoder, executing now. UNFILTERED: symbol's point branch has no
                //    length filter, so a >= 2 filter here would hide the 1-point case.
                var oracle = new List<(int featureIdx, double2[] points)>();
                for (int fi = 0; fi < features.Count; fi++)
                {
                    List<List<double2>> paths = MvtGeometry.Decode(fixture.Commands[fi]);
                    if (paths == null) continue;
                    foreach (List<double2> path in paths)
                        oracle.Add((fi, path.ToArray()));
                }

                // ── Arm B — the production path: the DECODED LAYER's own buffer (the object a
                //    real consumer borrows), the production bucketing, and a span read. Borrowed, so nothing
                //    is disposed here.
                var actual = new List<(int featureIdx, double2[] points)>();
                TileGeometryBuffers geometry = decodedLayer.Geometry;
                {
                    Assert.IsTrue(geometry.IsCreated, "precondition: the decoded layer carries a buffer");

                    int featureCount = features.Count;
                    var ringStart = new int[featureCount + 1];
                    for (int r = 0; r < geometry.RingCount; r++) ringStart[geometry.RingFeatureIdx[r] + 1]++;
                    for (int i = 0; i < featureCount; i++) ringStart[i + 1] += ringStart[i];
                    var ringOrder = new int[geometry.RingCount];
                    var cursor    = (int[])ringStart.Clone();
                    for (int r = 0; r < geometry.RingCount; r++) ringOrder[cursor[geometry.RingFeatureIdx[r]]++] = r;

                    for (int f = 0; f < featureCount; f++)
                    {
                        int pathCount = ringStart[f + 1] - ringStart[f];
                        for (int p = 0; p < pathCount; p++)
                        {
                            int r     = ringOrder[ringStart[f] + p];
                            int start = geometry.RingOffsets[r];
                            int n     = geometry.RingOffsets[r + 1] - start;
                            var points = new double2[n];
                            for (int k = 0; k < n; k++) points[k] = geometry.Vertices[start + k];
                            actual.Add((f, points));
                        }
                    }
                }

                // ── Accumulate the non-vacuity evidence across both layers (asserted once, below).
                var pathsPerFeature = new Dictionary<int, int>();
                foreach ((int featureIdx, double2[] points) entry in oracle)
                {
                    totalEntries++;
                    totalPoints += entry.points.Length;
                    if (entry.points.Length == 1) sawLengthOne++;
                    if (entry.featureIdx != 0) anyNonZeroFeatureIdx = true;
                    pathsPerFeature.TryGetValue(entry.featureIdx, out int c);
                    pathsPerFeature[entry.featureIdx] = c + 1;
                }
                foreach (KeyValuePair<int, int> kv in pathsPerFeature)
                    if (kv.Value > 1) sawMultiPathFeature++;
                foreach (IFeature feature in features) kindsSeen.Add(feature.GeometryType);

                // ── The comparison.
                Assert.AreEqual(oracle.Count, actual.Count,
                    $"'{sourceLayer}': the shared buffer must yield exactly the same number of paths as the " +
                    "managed decoder — unfiltered, including 1-point paths");

                for (int i = 0; i < oracle.Count; i++)
                {
                    Assert.AreEqual(oracle[i].featureIdx, actual[i].featureIdx,
                        $"'{sourceLayer}': path {i} must belong to the same feature in both arms — " +
                        "feature-then-path order is the contract RingFeatureIdx joins through, and the " +
                        "extractor's per-tile `ordinal` makes it observable output");
                    Assert.AreEqual(oracle[i].points.Length, actual[i].points.Length,
                        $"'{sourceLayer}': path {i} must have the same point count in both arms");

                    double2[] expected = oracle[i].points;
                    double2[] got      = actual[i].points;
                    for (int k = 0; k < expected.Length; k++)
                    {
                        Assert.AreEqual(expected[k].x, got[k].x,
                            $"'{sourceLayer}': path {i} point {k} x must be BIT-identical between the " +
                            "managed and Burst decoders");
                        Assert.AreEqual(expected[k].y, got[k].y,
                            $"'{sourceLayer}': path {i} point {k} y must be BIT-identical between the " +
                            "managed and Burst decoders");
                    }
                }
            }

            // ── Non-vacuity, all measured against the fixture before being asserted: a corpus that could not
            //    tell the arms apart would make every equality above true for the wrong reason.
            Assert.Greater(totalEntries, 200,
                "precondition: the two layers must contribute real path corpora, not a handful");
            Assert.Greater(totalPoints, 50,
                "precondition: the fixture must carry real geometry, not a handful of points");
            Assert.Greater(sawLengthOne, 0,
                "precondition: at least one path of length EXACTLY 1 must be compared — the Point case line's " +
                "oracle could not have, and the case the unfiltered-buffer landmine is about");
            Assert.Greater(sawMultiPathFeature, 0,
                "precondition: at least one feature must contribute MORE THAN ONE path, or path ordering " +
                "WITHIN a feature is not pinned — only ordering across features would be");
            Assert.IsTrue(anyNonZeroFeatureIdx,
                "precondition: the path→feature join must be exercised by a non-zero index");
            Assert.IsTrue(kindsSeen.Contains(TileGeometryType.Point),
                "precondition: Point features must be among those compared");
            Assert.IsTrue(kindsSeen.Contains(TileGeometryType.LineString),
                "precondition: LineString features must be among those compared");
        }

        // ── production's OWN path ordering, through Extract ─────────────────────────────────────────

        /// <summary>
        /// The symbols <c>Extract</c> emits for one feature follow that feature's paths in DECODE ORDER,
        /// and there is one per path. This observes <c>Extract</c>'s own <c>ringStart</c>/<c>ringOrder</c>
        /// bucketing and <c>CopyRing</c>, which the differential oracle cannot: it transcribes both into the
        /// test, so an injection into production leaves it green.
        /// <para>Path order is observable OUTPUT, not an implementation detail: the extractor's per-tile
        /// <c>ordinal</c> becomes <c>SymbolFeature.FeatureIndex</c>, the stable tiebreak, so a reordering
        /// silently changes which symbol wins a collision.</para>
        /// <para>Also pins <c>RingCapacity == RingCount</c> for an MVT-materialized buffer. That identity is
        /// why substituting one for the other in the bucketing is currently an arithmetic no-op
        /// (<c>MvtGeometryMaterializer</c> sizes exactly from <c>PrecountRingsAndVertices</c>) — and it is the
        /// assertion that goes RED on the day a producer starts over-allocating, which is the day the
        /// capacity-vs-count trap becomes real. Without it, that trap has no observer at all.</para>
        /// </summary>
        [Test]
        public void SymbolOrder_WithinAFeature_FollowsDecodeOrder()
        {
            // Three DISTINCT points in one MultiPoint feature ⇒ three 1-point paths in one feature, so the
            // ordering under test is WITHIN a feature, not across features.
            var authored = new[]
            {
                new double2(400, 500), new double2(1600, 1700), new double2(2800, 2900),
            };
            IFeature feature = PointFeature(MultiPointGeometry(authored));

            TileGeometryBuffers geometry = Materialize(new[] { feature }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.AreEqual(3, geometry.RingCount,
                    "precondition: one feature contributing THREE paths — with one path this test could not " +
                    "distinguish an ordering defect from a counting one");
                Assert.AreEqual(geometry.RingCount, geometry.RingCapacity,
                    "MvtGeometryMaterializer sizes EXACTLY (PrecountRingsAndVertices), so capacity equals " +
                    "count for every buffer it mints. This is why iterating RingCapacity instead of RingCount " +
                    "is currently a no-op — and this assertion is what goes RED the day a producer starts " +
                    "over-allocating, i.e. the day that substitution becomes a real out-of-range bug.");
            }
            finally
            {
                geometry.Dispose();
            }

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                PointSymbolLayer(), TileOf(feature), SyntheticTileId, 0.0, new WebMercatorProjection(), symbols);

            Assert.AreEqual(authored.Length, symbols.Count,
                "one label per path — a dropped or duplicated path is a counting defect in the bucketing");

            var projection = new WebMercatorProjection();
            for (int i = 0; i < authored.Length; i++)
            {
                double2 lonLat = SyntheticTileId.ToLonLat(authored[i].x, authored[i].y, SyntheticExtent);
                double3 expected = projection.Project(
                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });

                Assert.AreEqual(expected.x, symbols[i].AnchorRender.x, 1e-6,
                    $"label {i} must be the {i}th AUTHORED path, in decode order — the bucketing is a stable " +
                    "counting sort precisely so a feature's paths keep the order the decoder produced them in, " +
                    "and FeatureIndex (the collision tiebreak) is stamped from that order");
                Assert.AreEqual(expected.y, symbols[i].AnchorRender.y, 1e-6, $"label {i} anchor.y");
                // NOT a second pin on path order: `ordinal++` is stamped at each emit site and symbols are
                // appended in emission order, so this holds for ANY path ordering. It pins emission order
                // only; the anchor comparison above is the clause that discriminates.
                Assert.AreEqual(i, symbols[i].FeatureIndex,
                    $"label {i}'s per-tile ordinal must follow EMISSION order (not path order)");
            }
        }

        // ── the observer line could not build ──────────────────────────────────────────

        /// <summary>
        /// A Point feature whose command stream is a single <c>MoveTo</c> of ONE point still emits
        /// exactly one symbol, at that point's projection. Fill and line cannot observe this: fill
        /// re-filters short rings downstream and line returns early below 2 points, so a
        /// <c>&lt; 2</c>-point filter fused into the shared decode stage is invisible in both. Here it deletes
        /// a rendered symbol.
        /// <para>The control — a <c>MoveTo</c> of THREE points expecting THREE symbols — is what makes "one
        /// symbol" a statement about the boundary value rather than an artefact of the emitter collapsing
        /// anything.</para>
        /// </summary>
        [Test]
        public void SymbolPointFeature_OnePointPath_StillEmitsOneSymbol()
        {
            var onePoint    = new double2(1000, 1500);
            var threePoints = new[] { new double2(600, 700), new double2(1200, 1400), new double2(2400, 2800) };

            IFeature single = PointFeature(MultiPointGeometry(onePoint));
            IFeature triple = PointFeature(MultiPointGeometry(threePoints));

            // Non-vacuity: the buffer really does carry ONE ring whose span is EXACTLY 1, so the tooth is
            // provably at the boundary value rather than on a longer path that a filter would spare.
            TileGeometryBuffers geometry = Materialize(new[] { single }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the single-point selection materialized");
                Assert.AreEqual(1, geometry.RingCount, "precondition: exactly one ring");
                Assert.AreEqual(1, geometry.RingOffsets[1] - geometry.RingOffsets[0],
                    "precondition: that ring's span must be EXACTLY 1 point — the boundary value a " +
                    "< 2 filter in the shared decode stage would delete");
            }
            finally
            {
                geometry.Dispose();
            }

            var oneSymbol = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                PointSymbolLayer(), TileOf(single), SyntheticTileId, 0.0, new WebMercatorProjection(), oneSymbol);
            Assert.AreEqual(1, oneSymbol.Count,
                "a 1-point path is a REAL label: symbol's point branch has no length filter at all, so any " +
                "short-ring filter in MvtGeometryMaterializer, MvtDecodeJob or any shared stage deletes it");

            // …and it is at that point's real projection, not a stub.
            double2 lonLat = SyntheticTileId.ToLonLat(onePoint.x, onePoint.y, SyntheticExtent);
            double3 expected = new WebMercatorProjection().Project(
                new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            Assert.AreEqual(expected.x, oneSymbol[0].AnchorRender.x, 1e-6);
            Assert.AreEqual(expected.y, oneSymbol[0].AnchorRender.y, 1e-6);
            Assert.AreEqual(expected.z, oneSymbol[0].AnchorRender.z, 1e-6);

            // Control: a MultiPoint MoveTo of 3 emits 3 — "one symbol" above is the boundary value, not a
            // collapse.
            var threeSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                PointSymbolLayer(), TileOf(triple), SyntheticTileId, 0.0, new WebMercatorProjection(), threeSymbols);
            Assert.AreEqual(3, threeSymbols.Count,
                "control: a MoveTo of three points is three 1-point paths and therefore three symbols");
        }

        // ── symbol's OWN length threshold, < 2 and not < 3 ──────────────────────────────────────────

        /// <summary>
        /// Under LINE placement, a 2-point polyline places anchors (symbol's line filter is
        /// <c>&lt; 2</c>, not fill's <c>&lt; 3</c>), while a 1-point path under the SAME layer places none.
        /// The pair pins WHICH side of the boundary each length falls on, not merely that geometry appeared.
        /// Three consumers, three thresholds (<c>&lt; 3</c> / <c>&lt; 2</c> / none), one unfiltered buffer.
        /// </summary>
        [Test]
        public void SymbolLineFeature_TwoPointPath_PlacesAnchors()
        {
            IFeature twoPoint = LineFeature(new double2(500, 500), new double2(3500, 3500));
            IFeature onePoint = LineFeature(new double2(500, 500));

            // Non-vacuity: spans of EXACTLY 2 and EXACTLY 1 reach the consumer unfiltered.
            AssertSingleRingSpan(twoPoint, 2);
            AssertSingleRingSpan(onePoint, 1);

            var twoPointSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                LineSymbolLayer(), TileOf(twoPoint), SyntheticTileId, 0.0, new WebMercatorProjection(),
                twoPointSymbols);
            Assert.Greater(twoPointSymbols.Count, 0,
                "a 2-point polyline has exactly one segment to place along — symbol's line filter is < 2, " +
                "not fill's < 3, and moving it to < 3 would silently delete these symbols");

            var onePointSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                LineSymbolLayer(), TileOf(onePoint), SyntheticTileId, 0.0, new WebMercatorProjection(),
                onePointSymbols);
            Assert.AreEqual(0, onePointSymbols.Count,
                "control: a 1-point path has no segment, so the LINE branch's < 2 filter drops it — this is " +
                "what makes the assertion above about the boundary rather than about geometry existing");
        }

        // ── ownership, on the exit paths ────────────────────────────────────────────────────────────

        /// <summary>
        /// The three selections that exercise <c>Extract</c>'s non-obvious buffer states: none throws and
        /// none emits, and each asserts WHY it emitted nothing, so "zero symbols" can never be mistaken for
        /// "the buffer was empty".
        /// <list type="number">
        /// <item>zero selected features ⇒ <c>Materialize</c> returns <c>default</c>, so <c>Dispose()</c> is a
        /// no-op, not a throw;</item>
        /// <item>one Polygon feature with a genuine multi-point ring ⇒ the buffer IS created with
        /// <c>RingCount &gt; 0</c>, so the zero symbols provably came from <b>symbol's kind gate</b>;</item>
        /// <item>one feature whose <c>Geometry</c> is <c>null</c> ⇒ the buffer IS created (feature count ≥ 1)
        /// with <c>RingCount == 0</c>. An earlier plan claimed <c>IsCreated == false</c>
        /// here and being wrong; this asserts what actually holds.</item>
        /// </list>
        /// </summary>
        [Test]
        public void SymbolExtract_EmptyPolygonAndNullGeometrySelections_EmitNothingAndDoNotThrow()
        {
            // (1) zero selected features — the layer resolves, the filter matches nothing.
            IFeature anyPoint = PointFeature(MultiPointGeometry(new double2(100, 100)));
            TileGeometryBuffers empty = Materialize(Array.Empty<IFeature>(), SyntheticTileId, SyntheticExtent);
            Assert.IsFalse(empty.IsCreated,
                "a zero-feature selection must return default(TileGeometryBuffers) — Dispose on it is a no-op");
            Assert.DoesNotThrow(() => empty.Dispose(), "Dispose on a default buffer early-returns");

            var noneSelected = new List<SymbolStyle.SymbolFeature>();
            Assert.DoesNotThrow(() => SymbolFeatureExtractor.Extract(
                PointSymbolLayer("[\"==\",\"nope\",\"nope\"]"), TileOf(anyPoint), SyntheticTileId, 0.0,
                new WebMercatorProjection(), noneSelected));
            Assert.AreEqual(0, noneSelected.Count, "a selection matching nothing emits nothing");

            // (2) an all-Polygon selection — the buffer is real, symbol's kind gate is what rejects it.
            IFeature polygon = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("poly") },
                geometryType: TileGeometryType.Polygon,
                geometry: MultiPointRing(
                    new double2(1000, 1000), new double2(2000, 1000),
                    new double2(2000, 2000), new double2(1000, 2000)));

            TileGeometryBuffers polyBuffer = Materialize(new[] { polygon }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(polyBuffer.IsCreated, "the Polygon selection DOES materialize a buffer");
                Assert.Greater(polyBuffer.RingCount, 0,
                    "…with real rings — so the zero symbols below come from symbol's kind gate, not from an " +
                    "empty buffer. Without this the case would be vacuous.");
            }
            finally
            {
                polyBuffer.Dispose();
            }

            var polygonSymbols = new List<SymbolStyle.SymbolFeature>();
            Assert.DoesNotThrow(() => SymbolFeatureExtractor.Extract(
                PointSymbolLayer(), TileOf(polygon), SyntheticTileId, 0.0, new WebMercatorProjection(),
                polygonSymbols));
            Assert.AreEqual(0, polygonSymbols.Count, "Polygon is never accepted at any placement (the fence)");

            // (3) a feature whose Geometry is null.
            IFeature nullGeometry = new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("ghost") },
                geometryType: TileGeometryType.Point,
                geometry: null);

            TileGeometryBuffers nullBuffer = Materialize(new[] { nullGeometry }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(nullBuffer.IsCreated,
                    "a null Geometry still counts as a feature, so the buffer IS created (feature count >= 1)");
                Assert.AreEqual(0, nullBuffer.RingCount, "…carrying zero rings — the materializer treats null " +
                    "as zero commands");
            }
            finally
            {
                nullBuffer.Dispose();
            }

            var nullSymbols = new List<SymbolStyle.SymbolFeature>();
            Assert.DoesNotThrow(() => SymbolFeatureExtractor.Extract(
                PointSymbolLayer(), TileOf(nullGeometry), SyntheticTileId, 0.0, new WebMercatorProjection(),
                nullSymbols));
            Assert.AreEqual(0, nullSymbols.Count, "no geometry, no anchors, no symbols");
        }

        // ── Fixture + synthetic plumbing ────────────────────────────────────────────────────────────

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-tile.bytes not found walking up from cwd/AppContext.");
        }

        /// <summary>The features a symbol layer over <paramref name="sourceLayer"/> selects — the SAME list,
        /// in the same order, the extractor materializes and indexes <c>RingFeatureIdx</c> against.</summary>
        private static MvtTile _decodedFixture;

        /// <summary>The decoded fixture, ONE instance per test (released by the fixture's TearDown) — arm B
        /// must read the very buffer a production consumer would borrow, not a re-materialization.</summary>
        private static IDecodedTile DecodedFixture()
            => _decodedFixture ??= TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));

        private static IReadOnlyList<IFeature> SelectedFeatures(string sourceLayer)
            => FeatureSelector.SelectFeatures(
                PointSymbolLayer(sourceLayer: sourceLayer), DecodedFixture(), 0.0);

        /// <summary>Arm B's buffer, minted through the REAL producer from synthetic features' command
        /// streams — the same thing the decoder does for a real layer.</summary>
        private static TileGeometryBuffers Materialize(
            IReadOnlyList<IFeature> features, TileId tile, double extent)
            => TestTileMeshBuilder.Materialize(features, tile, extent);

        private static void AssertSingleRingSpan(IFeature feature, int expectedSpan)
        {
            TileGeometryBuffers geometry = Materialize(new[] { feature }, SyntheticTileId, SyntheticExtent);
            try
            {
                Assert.IsTrue(geometry.IsCreated, "precondition: the selection materialized");
                Assert.AreEqual(1, geometry.RingCount, "precondition: exactly one ring");
                Assert.AreEqual(expectedSpan, geometry.RingOffsets[1] - geometry.RingOffsets[0],
                    $"precondition: that ring's span must be EXACTLY {expectedSpan} — the buffer is " +
                    "unfiltered, so the length reaching the consumer is the length authored");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        private static SymbolStyle.StyleLayer PointSymbolLayer(
            string filterJson = null, string sourceLayer = "probe")
            => new SymbolStyle.StyleLayer
            {
                Id          = "b4-point",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = sourceLayer,
                Paint       = TestStyle.SymbolPaint(),
                Layout      = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\"}"),
                Filter      = filterJson != null ? JsonParser.Parse(filterJson) : null,
            };

        private static SymbolStyle.StyleLayer LineSymbolLayer()
            => new SymbolStyle.StyleLayer
            {
                Id          = "b4-line",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "probe",
                Paint       = TestStyle.SymbolPaint(),
                Layout      = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\",\"symbol-placement\":\"line\",\"symbol-spacing\":1}"),
            };

        private static IFeature PointFeature(uint[] geometry)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("probe") },
                geometryType: TileGeometryType.Point,
                geometry: geometry);

        private static IFeature LineFeature(params double2[] points)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["NAME"] = Value.String("probe") },
                geometryType: TileGeometryType.LineString,
                geometry: MultiPointRing(points));

        private static IDecodedTile TileOf(IFeature feature)
            => TestDecodedTiles.Of("probe", SyntheticTileId, new List<IFeature> { feature }, SyntheticExtent);

        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        /// <summary>A single <c>MoveTo</c> of N points — the MultiPoint encoding, which decodes to N separate
        /// ONE-POINT paths (both decoders start a new path per MoveTo point).</summary>
        private static uint[] MultiPointGeometry(params double2[] tilePoints)
        {
            var stream = new List<uint> { 1u | ((uint)tilePoints.Length << 3) }; // MoveTo, count=N
            long cursorX = 0, cursorY = 0;
            foreach (double2 p in tilePoints)
            {
                long x = (long)p.x, y = (long)p.y;
                stream.Add(ZigZagEncode(x - cursorX));
                stream.Add(ZigZagEncode(y - cursorY));
                cursorX = x;
                cursorY = y;
            }
            return stream.ToArray();
        }

        /// <summary>One path of N points: <c>MoveTo</c>×1 + <c>LineTo</c>×(N−1).</summary>
        private static uint[] MultiPointRing(params double2[] tilePoints)
        {
            var stream = new List<uint> { 1u | (1u << 3) }; // MoveTo, count=1
            long cursorX = 0, cursorY = 0;
            void Append(double2 p)
            {
                long x = (long)p.x, y = (long)p.y;
                stream.Add(ZigZagEncode(x - cursorX));
                stream.Add(ZigZagEncode(y - cursorY));
                cursorX = x;
                cursorY = y;
            }
            Append(tilePoints[0]);
            if (tilePoints.Length > 1)
            {
                stream.Add(2u | ((uint)(tilePoints.Length - 1) << 3)); // LineTo, count=N-1
                for (int i = 1; i < tilePoints.Length; i++) Append(tilePoints[i]);
            }
            return stream.ToArray();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolDedupZoomInvarianceTests — the cross-tile dedup winner set is invariant across display zoom
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Headline tooth: the cross-tile dedup winner set is a pure function of the tile set —
    /// INVARIANT across display zoom. The store keys on the fixed <see cref="CrossTileSymbolKey.CanonicalGridMeters"/>,
    /// so the <c>quantizeMeters</c> argument only GATES dedup on/off; its magnitude no longer sets the grid.
    /// Sweeping the gate across a wide zoom range (its old per-frame <see cref="CameraPoseMath.MetersPerPixel"/>
    /// values) must leave the ordered winner list — and the plan arrays — element-for-element identical.
    ///
    /// <para>The single-gate-value case passes with or without the fix, so it would be a degenerate tooth; the
    /// SWEEP is what bites. RED-verified by reverting the store's four <c>DedupKey.For</c> grid inputs back to
    /// <c>quantizeMeters</c>: the winner set then varies across the sweep (a coarse gate merges the split pair,
    /// a fine gate splits it) → the cross-gate assertion fails.</para>
    ///
    /// <para><b>Winner identity is <c>(BlockId, LocalIndex)</c>.</b> There is no managed symbol list to
    /// compare by reference. Comparing the SAME two arrays (blockId/localIndex/isDeparting)
    /// across two DIFFERENT gate values is still a genuine, non-vacuous invariance check: it is not a restatement
    /// of anything a single run computed, it is proof that TWO INDEPENDENT runs (different gate) produced the
    /// SAME winner identities.</para>
    /// </summary>
    [TestFixture]
    public class SymbolDedupZoomInvarianceTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here.
        private long _liveBlocks;
        [SetUp] public void BaselineBlocks() => _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
        [TearDown] public void NoLeakedBlocks() => Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
            "this test baked a block it never disposed — release the snapshot and Clear() the store");

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, string text, int feature, TileId tile) =>
            TestSymbolTileBuffer.AddPoint(buffer, anchor, quads: null, boundsMin: float2.zero, boundsMax: float2.zero,
                text: text, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));

        private readonly struct Snapshot
        {
            public readonly List<int> BlockId;
            public readonly List<int> LocalIndex;
            public readonly List<byte> IsDeparting;
            public readonly int ActiveCount;
            public Snapshot(List<int> blockId, List<int> localIndex, List<byte> isDeparting, int activeCount)
            {
                BlockId = blockId; LocalIndex = localIndex;
                IsDeparting = isDeparting; ActiveCount = activeCount;
            }
        }

        private static Snapshot Collect(SymbolTileStore store, double gate)
        {
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, gate, out int activeCount);
            return new Snapshot(blockId, localIndex, isDeparting, activeCount);
        }

        // ── The winner set + order + plan arrays are IDENTICAL for every gate value across a wide zoom span. ──
        [Test]
        public void WinnerSet_InvariantAcrossDisplayZoom()
        {
            // Fixture: two same-text co-located symbols across two same-z tiles (must MERGE — one 4 m cell), plus
            // two same-text symbols 8 m apart (must SPLIT — distinct 4 m cells). Under the fixed grid the winner
            // set is {merged Co, splitA, splitB} = 3 for every gate. Under a per-zoom grid a coarse gate would
            // collapse the 8 m pair (and, at the coarsest, the Co pair with them), so the count would vary.
            double3 coAnchor = new double3(0, 0, 0);          // a 4 m cell centre
            double3 splitA = new double3(1000, 0, 0);         // cell 250
            double3 splitB = new double3(1008, 0, 0);         // cell 252 — 8 m from splitA, distinct under 4 m

            var t1 = new TileId { Z = 10, X = 500, Y = 400 };
            var t2 = new TileId { Z = 10, X = 501, Y = 400 }; // same band as t1 (finest-z tie → lowest TileKey)

            var store = new SymbolTileStore(cacheCap: 8);
            var k1 = new SymbolTileStore.Key("src", t1);
            var k2 = new SymbolTileStore.Key("src", t2);
            var t1Buffer = new SymbolTileBuffer();
            AddPoint(t1Buffer, coAnchor, "Co", 1, t1);
            AddPoint(t1Buffer, splitA, "Split", 2, t1);
            AddPoint(t1Buffer, splitB, "Split", 3, t1);
            var t2Buffer = new SymbolTileBuffer();
            AddPoint(t2Buffer, coAnchor, "Co", 4, t2); // the co-located twin in the OTHER tile
            SymbolTileBlock block1 = SymbolTileBlockBaker.Bake(
                t1Buffer, slotCount: 1, double3.zero);
            SymbolTileBlock block2 = SymbolTileBlockBaker.Bake(
                t2Buffer, slotCount: 1, double3.zero);
            store.CompleteBuild(k1, store.BeginBuild(k1), block1);
            store.CompleteBuild(k2, store.BeginBuild(k2), block2);

            // The sweep: gate 1.0 plus the old per-frame MetersPerPixel at z5 / z8 / z14 — a >2^9 grid span.
            double[] gates =
            {
                1.0,
                CameraPoseMath.MetersPerPixel(5.0),
                CameraPoseMath.MetersPerPixel(8.0),
                CameraPoseMath.MetersPerPixel(14.0),
            };

            Snapshot baseline = Collect(store, gates[0]);

            // Precondition: the fixture actually exercises BOTH a merge and a split (not a degenerate all-merge/
            // all-distinct set) — 3 winners = {one Co, splitA, splitB}, all active, none departing.
            Assert.AreEqual(3, baseline.BlockId.Count, "sanity: co-located pair merges, 8 m pair splits → 3 winners");
            Assert.AreEqual(3, baseline.ActiveCount, "…all active");
            // Exactly one of the two Co copies wins: (block1, localIndex 0) XOR (block2, localIndex 0).
            bool baselineHasCo1 = false, baselineHasCo2 = false;
            for (int i = 0; i < baseline.BlockId.Count; i++)
            {
                if (baseline.BlockId[i] == 0 && baseline.LocalIndex[i] == 0) baselineHasCo1 = true;
                if (baseline.BlockId[i] == 1 && baseline.LocalIndex[i] == 0) baselineHasCo2 = true;
            }
            Assert.IsTrue(baselineHasCo1 ^ baselineHasCo2, "exactly one Co copy wins the merge");
            // splitA (block0, local1) and splitB (block0, local2) both present — not merged.
            bool hasSplitA = false, hasSplitB = false;
            for (int i = 0; i < baseline.BlockId.Count; i++)
            {
                if (baseline.BlockId[i] == 0 && baseline.LocalIndex[i] == 1) hasSplitA = true;
                if (baseline.BlockId[i] == 0 && baseline.LocalIndex[i] == 2) hasSplitB = true;
            }
            Assert.IsTrue(hasSplitA && hasSplitB, "the 8 m pair both survive");

            for (int g = 1; g < gates.Length; g++)
            {
                Snapshot s = Collect(store, gates[g]);
                Assert.AreEqual(baseline.BlockId.Count, s.BlockId.Count,
                    $"winner count differs at gate {gates[g]} — dedup is NOT zoom-invariant");
                Assert.AreEqual(baseline.ActiveCount, s.ActiveCount, $"active split differs at gate {gates[g]}");
                for (int i = 0; i < baseline.BlockId.Count; i++)
                {
                    Assert.AreEqual(baseline.BlockId[i], s.BlockId[i], $"blockId differs at index {i}, gate {gates[g]}");
                    Assert.AreEqual(baseline.LocalIndex[i], s.LocalIndex[i], $"localIndex differs at index {i}, gate {gates[g]}");
                    Assert.AreEqual(baseline.IsDeparting[i], s.IsDeparting[i], $"isDeparting differs at index {i}, gate {gates[g]}");
                }
            }
            store.Clear();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolLayerMaterialTests — each symbol style layer gets its own material clone
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each symbol style layer gets its OWN material — a distinct
    /// <see cref="MapMaterialSet.SymbolTextWorld"/> clone (NOT one shared material) — with its
    /// <c>text-halo-*</c> bound by name. This is what makes per-layer halo variation possible.
    /// <see cref="SymbolRenderLayer"/> owns the material (per-layer materials live on the layer object,
    /// one owner), so this builds the render layers directly via
    /// <see cref="RenderLayerSet.Build"/> (the same production path <c>MapView.SetStyle</c> drives).
    /// <see cref="IRenderLayer.Material"/> IS <see cref="SymbolRenderLayer.WorldTextMaterial"/>, so the
    /// two are asserted as a single identity.
    /// </summary>
    [TestFixture]
    public class SymbolLayerMaterialTests : BaseTestFixture
    {
        private const string TwoSymbolLayers = @"{
            'version': 8,
            'layers': [
                { 'id':'a', 'type':'symbol', 'source':'s', 'source-layer':'la',
                  'layout': { 'text-field':'{NAME}' }, 'paint': { 'text-halo-width': 1 } },
                { 'id':'b', 'type':'symbol', 'source':'s', 'source-layer':'lb',
                  'layout': { 'text-field':'{NAME}' }, 'paint': { 'text-halo-width': 3 } }
            ]
        }";

        [Test]
        public void Build_PerLayerMaterials_AreDistinctClones()
        {
            var set = Track(ScriptableObject.CreateInstance<MapMaterialSet>());
            set.SymbolTextWorld = Track(new Material(Shader.Find("Map/Symbol/TextWorld")));

            StyleDocument style = StyleParser.Parse(TwoSymbolLayers.Replace('\'', '"'));

            {
                using var layers = new RenderLayerSet();
                layers.Build(style, 5.0, set);

                Assert.AreEqual(2, layers.Count, "one render layer per symbol style layer");

                var symbolLayer0 = (SymbolRenderLayer)layers[0];
                var symbolLayer1 = (SymbolRenderLayer)layers[1];
                Material m0 = symbolLayer0.Material;
                Material m1 = symbolLayer1.Material;
                Assert.IsNotNull(m0);
                Assert.IsNotNull(m1);
                Assert.AreSame(m0, symbolLayer0.WorldTextMaterial, "Material IS WorldTextMaterial.");
                Assert.AreNotSame(m0, m1, "per-layer materials are DISTINCT instances, not one shared material");
                Assert.AreNotSame(set.SymbolTextWorld, m0, "a layer material is a CLONE of the SymbolTextWorld base, not the base asset");

                // No paint assertion here any more: a symbol layer's materials carry engine plumbing only.
                // Every text-* term, text-halo-* included, is evaluated per feature and rides the vertex
                // stream — SymbolHaloEmitTests reads it where it actually lands, on the emitted mesh.
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolProcessorParityTests — production SymbolSubsystem output deep-equals a single-pass oracle
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The PRIMARY semantic
    /// tooth): the differential — production <see cref="SymbolSubsystem"/> output, driven through the
    /// production processor machinery, deep-equals a single-pass <see cref="StyledSymbolTileBuilder.BuildAsync"/>
    /// ORACLE fed the same bytes/style/camera-zoom/projection over a SECOND, independent builder/glyph
    /// pipeline. THREE symbol layers — one on a different source (so the "s"-source layers' GLOBAL indices
    /// {1,2} diverge from their within-build ordinals {0,1}) and two "s"-source layers with a
    /// ZOOM-INTERPOLATED text-size (so the tile's integer zoom vs the captured camera zoom yield
    /// observably different sizes) — so the per-layer processor split, material-index stamping, and
    /// cross-layer symbol order are all exercised, and EACH falsifier is a genuine (non-coincidental)
    /// divergence, not just a hypothetical one. See <c>SetUp</c>'s comment for the layout.
    ///
    /// <para>RED-verified against the un-rewired subsystem for the STRUCTURAL delegation teeth
    /// Empirically, THIS differential passes unmodified against a structurally
    /// faithful un-rewired <c>BuildTileAsync</c> too — it already calls
    /// <c>ExtractLayers</c>/<c>Shape</c> with the same zoom/projection/global-material-indices the
    /// oracle uses, so it computes IDENTICAL values. Its proven role (RED-verified by injecting each
    /// falsifier into a scratch copy of the rewired <c>BuildTileAsync</c>) is a
    /// WRONG-REWIRE falsifier, not a before/after discriminator.</para>
    ///
    /// <para>Falsifiers this tooth catches: (i) <c>ctx.Zoom</c> fed the tile's integer zoom instead of
    /// the captured camera zoom; (ii) material indices remapped to per-build ordinals instead of global
    /// <c>layerIndices</c>; (iii) per-layer split reordering or a dropped/collapsed layer; (iv) the main
    /// tail skipped entirely (zero symbols).</para>
    /// </summary>
    [TestFixture]
    public class SymbolProcessorParityTests
    {
        private const string FontName = "LatinFont";
        private const string SourceId = "s";
        private static readonly TileId Tile = new TileId { Z = 3, X = 0, Y = 0 };

        // THREE symbol layers, declared in an order that makes both falsifiers this differential exists to
        // catch actually falsifiable (dev-side strengthening after the advisor flagged the original
        // two-layer/constant-text-size style as hollow for exactly the two plumbing bugs (i)/(ii) name):
        //  - "symbols-other" (a DIFFERENT source, "other") occupies GLOBAL index 0, so the two "s"-source
        //    layers get GLOBAL indices {1, 2} while their WITHIN-BUILD ordinals (k in BuildTileAsync's loop)
        //    are {0, 1} — global-index ≠ ordinal, so a material-index-remap bug ((ii): stamping `k`
        //    instead of the global `layerIndices[k]`) produces an observably different MaterialIndex.
        //  - "symbols-a"/"symbols-b" (source "s", over the fixture's "centroids") use a ZOOM-INTERPOLATED
        //    text-size, so evaluating at the tile's INTEGER zoom (3) instead of the captured CAMERA zoom
        //    (5.0) yields an observably different TextSizePx/glyph-quad geometry, not an
        //    identical value by coincidence.
        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels-other', 'type':'symbol', 'source':'other', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'] } },
                { 'id':'labels-a', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}',
                              'text-size':['interpolate',['linear'],['zoom'],0,8,10,40],
                              'text-font':['LatinFont'] } },
                { 'id':'labels-b', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}',
                              'text-size':['interpolate',['linear'],['zoom'],0,10,10,50],
                              'text-font':['LatinFont'] } }
            ]
        }".Replace('\'', '"');

        private GameObject _camGo;
        private RenderTexture _rt;
        private MapCamera _mapCamera;
        private SymbolSubsystem _subsystem;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;
        private StyleDocument _style;
        private List<Symbol.StyleLayer> _allStyleSymbolLayers; // ALL layers, declared order — what SetStyle takes
        private List<Symbol.StyleLayer> _sSourceLayers;        // just the "s"-source layers, declared order
        private List<int> _sSourceGlobalIndices;               // their GLOBAL indices within _allStyleSymbolLayers

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SymbolParity_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            _mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));

            _subsystem = new SymbolSubsystem(_mapCamera);
            _tileBytes = LoadUp("Assets", "Fixtures", "sample-tile.bytes");
            _latinGlyphs = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            _style = StyleParser.Parse(StyleJson);
            _allStyleSymbolLayers = ExtractSymbolLayers(_style);

            _sSourceLayers = new List<Symbol.StyleLayer>();
            _sSourceGlobalIndices = new List<int>();
            for (int i = 0; i < _allStyleSymbolLayers.Count; i++)
            {
                if (_allStyleSymbolLayers[i].Source != SourceId) continue;
                _sSourceLayers.Add(_allStyleSymbolLayers[i]);
                _sSourceGlobalIndices.Add(i);
            }
            Assert.AreEqual(new List<int> { 1, 2 }, _sSourceGlobalIndices,
                "sanity: 's' source layers must occupy GLOBAL indices {1,2} (index 0 is 'labels-other')");
        }

        [TearDown]
        public void TearDown()
        {
            _subsystem?.Dispose();
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        private static LoadedTileKey Key(TileId t) => new LoadedTileKey(SourceId, t);

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        /// <summary>Drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
        /// thread, then <c>RunWorkerAndHandoff</c> fire-and-forget on the pool. Replaces the retired
        /// <c>OnTileBytesReady</c> push.</summary>
        private void DriveTileBytesReady(TileId tile)
        {
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, tile);
            if (pass == null) return; // mirrors OnTileBytesReady's no-op guard (no _builder / no layers for source)
            // The drive helper mirrors TileManager.KickMeshBuild: the tile is decoded ON THE POOL, the kick
            // owns the ONE reference the lease is born with, and its `finally` is the matching release —
            // which is what frees the decoded tile's buffers unless a parked build acquired its own.
            byte[] bytes = _tileBytes;
            UniTask.RunOnThreadPool(() =>
            {
                var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(tile, bytes));
                try { pass.RunWorkerAndHandoff(decode); }
                finally { decode.Release(); }
            }).Forget();
        }

        // This fixture only ever commits ONE tile (SourceId/Tile), so it reads that tile's baked native block
        // directly (DebugBlockFor) rather than routing through the cross-tile winner plan; the deep
        // block-vs-block compare below needs the block itself.
        private static readonly SymbolTileStore.Key ProductionKey = new SymbolTileStore.Key(SourceId, Tile);

        /// <summary>Drives the REAL production subsystem until it commits a baked block — same bytes, same
        /// style, same glyph fixture as the oracle below. Only "s"-source bytes are pushed — "symbols-other"
        /// (a different source) is never built; it exists solely to make the "s" layers' GLOBAL indices
        /// {1,2} diverge from their within-build ordinals {0,1} (falsifier (ii)).</summary>
        private IEnumerator DriveProductionBuild()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            _subsystem.SetStyle(_style, _allStyleSymbolLayers);

            var loaded = new List<LoadedTileKey> { Key(Tile) };
            DriveTileBytesReady(Tile);
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                if (_subsystem.Store().DebugBlockFor(ProductionKey) != null) yield break;
                yield return null;
            }
            Assert.Fail("production build did not commit labels within 200 pumped frames");
        }

        /// <summary>Builds the ORACLE symbol set: the un-rewired single pass
        /// (<see cref="StyledSymbolTileBuilder.BuildAsync"/>) over a SECOND, independent glyph pipeline fed
        /// the SAME ranges — atlas state is equivalent but independent, so this is not self-referential with
        /// the processor machinery the rewire changes (only <c>ExtractLayers</c>/<c>Shape</c>, which it does not
        /// modify, are shared).
        /// <para><b>That independence premise has EXPIRED</b>: the shared-buffer move modifies exactly
        /// <c>ExtractLayers</c>/<c>Shape</c>. This fixture is therefore no longer independent of the
        /// symbol geometry path, and must not be cited as the oracle for a change to it —
        /// <c>SymbolBufferParityTests</c> is that oracle. Recorded here rather than only in the newer file
        /// because a stale independence claim left where a reader finds it is how this suite
        /// disarmed a structural tooth once already.</para></summary>
        private SymbolTileBuffer BuildOracle()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            // Match SymbolSubsystem.SetStyle's atlas dimension EXACTLY (its AtlasDimension = 4096,
            // clamped to the GPU max) — a different atlas size packs glyphs at different cells, so their
            // normalized UVs would differ from production for a reason that has NOTHING to do with the rewire
            // (a test-harness artifact, not a real divergence).
            int dim = math.min(SystemInfo.maxTextureSize, 4096);
            using var oracleGlyphManager = new GlyphManager(TestGlyphSource.FromRanges(ranges), new GlyphAtlas(dim, dim));
            var oracleBuilder = new StyledSymbolTileBuilder(oracleGlyphManager);

            using MvtTile mvt = MvtDecoder.Decode(Tile, _tileBytes);
            double zoom = _mapCamera.CurrentProperties.Zoom; // same captured camera zoom the production build uses
            var projection = _mapCamera.Projection;

            var oracle = new SymbolTileBuffer();
            // BuildAsync completes synchronously here: TestGlyphSource resolves via UniTask.FromResult and
            // neither BuildAsync/ExtractLayers/Shape forces a thread hop — no real async suspension.
            // _sSourceLayers/_sSourceGlobalIndices mirror exactly what BuildTileAsync passes for the "s"
            // build: the "s"-source layers in declared order, stamped with their GLOBAL indices {1,2}.
            oracleBuilder.BuildAsync(mvt, Tile, _sSourceLayers, zoom, projection, oracle, _sSourceGlobalIndices)
                .GetAwaiter().GetResult();
            return oracle;
        }

        /// <summary>4.4b: the differential is now BLOCK-vs-block, collapsing the former symbol-for-symbol
        /// (the retired per-symbol carrier's own assert helper) and batch-record comparisons into one — both compared the SAME
        /// underlying committed content, just through two different readers, and
        /// <see cref="BlockColumnHash.AssertColumnsEqual"/> already covers every column either one did (record
        /// kind/detail, point/curved stage inputs, quads) at finer, per-column granularity.</summary>
        [UnityTest]
        public IEnumerator ProductionBuild_MatchesSinglePassOracle_BlockForBlock()
        {
            yield return DriveProductionBuild();
            SymbolTileBlock production = _subsystem.Store().DebugBlockFor(ProductionKey);
            Assert.IsNotNull(production, "sanity: the production build committed a block");

            SymbolTileBuffer oracleSymbols = BuildOracle();
            Assert.Greater(oracleSymbols.Symbols.Count, 0, "sanity: the fixture + the 's'-source layers must yield labels at all");

            int slotCount = _allStyleSymbolLayers.Count; // mirrors production SymbolSubsystem.SlotCount
            double3 origin = TileRenderOrigin.Project(Tile, _mapCamera.Projection); // mirrors RunTailAsync's tail.TileOriginRender
            SymbolTileBlock oracle = SymbolTileBlockBaker.Bake(oracleSymbols, slotCount, in origin);
            try { BlockColumnHash.AssertColumnsEqual(oracle, production); }
            finally { oracle.Dispose(); }
        }

        /// <summary>The "tail actually runs" tooth: a glyph-fetch
        /// delegate reachable ONLY from <see cref="StyledSymbolTileBuilder.Shape"/> (the tail) records
        /// the thread it runs on; together with the existing, unmodified
        /// <c>SymbolDecodeAndExtract_RunOffTheMainThread</c> recorder (which pins the WORKER half off main),
        /// this proves the phase split runs on the right threads AND in the right order — symbols only commit
        /// if the tail ran after the worker step that fed it.</summary>
        [UnityTest]
        public IEnumerator SymbolMainTail_RunsOnMainThread_AfterWorkerPass()
        {
            int mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var observedThreadIds = new List<int>();

            _subsystem.GlyphSourceFactoryOverride = _ => new TestGlyphSource((fontStack, rangeStart, ct) =>
            {
                observedThreadIds.Add(System.Threading.Thread.CurrentThread.ManagedThreadId);
                return Cysharp.Threading.Tasks.UniTask.FromResult(new GlyphRangeResponse(_latinGlyphs));
            });
            _subsystem.SetStyle(_style, _allStyleSymbolLayers);

            var loaded = new List<LoadedTileKey> { Key(Tile) };
            DriveTileBytesReady(Tile);
            int symbolCount = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                symbolCount = _subsystem.Store().DebugBlockFor(ProductionKey)?.Kinds.Length ?? 0;
                if (symbolCount > 0) break;
                yield return null;
            }

            Assert.Greater(symbolCount, 0, "sanity: the build actually committed labels (the tail ran)");
            Assert.Greater(observedThreadIds.Count, 0, "sanity: the glyph-fetch delegate — reachable only from the tail — fired at all");
            foreach (int id in observedThreadIds)
                Assert.AreEqual(mainThreadId, id, "the glyph-fetch delegate must run on the MAIN thread (the tail), never the worker pool");
        }

        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { System.IO.Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory };
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
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolReconcileAsyncTests — the off-main reconcile state machine, driven end-to-end
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The OFF-MAIN reconcile state machine, driven end-to-end through the
    /// production <see cref="SymbolSubsystem"/>: off-main dedup, one-in-flight + apply-stale + stale-front-held,
    /// fault → no swap + reschedule, restyle drains + releases front/back pins, async front == inline-collect
    /// oracle. <see cref="Pin_PreventsNativeUseAfterFree_InGather"/> (store + gather) proves the pin prevents a
    /// native use-after-free. The one-in-flight, fault and pin teeth are RED-verified.
    /// </summary>
    [TestFixture]
    public class SymbolReconcileAsyncTests
    {
        private const string FontName = "LatinFont";
        private const string SourceId = "s";

        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'] } }
            ]
        }".Replace('\'', '"');

        private GameObject _camGo;
        private RenderTexture _rt;
        private SymbolSubsystem _subsystem;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;
        // A gate a gated test hands the reconciler — Set in TearDown so a failed test never hangs the teardown
        // drain (a teardown while GateForTest is held must open the gate first).
        private ManualResetEventSlim _testGate;
        private SymbolGatherPlan _quiescedPlan;

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SymbolReconcile_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));
            _subsystem = new SymbolSubsystem(mapCamera);
            _tileBytes = LoadUp("Assets", "Fixtures", "sample-tile.bytes");
            _latinGlyphs = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
        }

        [TearDown]
        public void TearDown()
        {
            _testGate?.Set(); // open any held reconcile gate FIRST so the Dispose drain can't hang
            _subsystem?.Dispose();
            _testGate = null;
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        private void UseImmediateGlyphs()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            StyleDocument style = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));
        }

        private static LoadedTileKey Key(TileId t) => new LoadedTileKey(SourceId, t);

        private void DriveTileBytesReady(TileId tile)
        {
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, tile);
            if (pass == null) return;
            // The drive helper mirrors TileManager.KickMeshBuild: the tile is decoded ON THE POOL, the kick
            // owns the ONE reference the lease is born with, and its `finally` is the matching release —
            // which is what frees the decoded tile's buffers unless a parked build acquired its own.
            byte[] bytes = _tileBytes;
            UniTask.RunOnThreadPool(() =>
            {
                var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(tile, bytes));
                try { pass.RunWorkerAndHandoff(decode); }
                finally { decode.Release(); }
            }).Forget();
        }

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        private static int DepartingSymbolCount(SymbolGatherPlan plan)
        {
            int n = 0;
            for (int i = 0; i < plan.WinnerCount; i++) if (plan.Departing[i] != 0) n++;
            return n;
        }

        // Pump the production per-frame loop until the reconcile is FULLY quiescent: symbols committed, no pending
        // tail, nothing in flight, and the schedule generation caught up (CollectRecomputeCount stable 2 frames).
        // Leaves the last plan in _quiescedPlan.
        private IEnumerator PumpToQuiescence(List<LoadedTileKey> loaded)
        {
            int stable = 0; int lastRecompute = -1;
            for (int f = 0; f < 400; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                _quiescedPlan = _subsystem.CurrentBatch(default, 0.0);
                bool settled = _quiescedPlan.WinnerCount > 0 && _subsystem.ReadyTailCount() == 0
                               && !_subsystem.ReconcileInFlight() && _subsystem.CollectRecomputeCount == lastRecompute;
                if (settled) { if (++stable >= 2) yield break; } else stable = 0;
                lastRecompute = _subsystem.CollectRecomputeCount;
                yield return null;
            }
            Assert.Fail("did not reach reconcile quiescence within the frame ceiling");
        }

        // ═══ The cross-tile dedup runs OFF the main thread ═══

        [UnityTest]
        public IEnumerator Reconcile_RunsOffTheMainThread()
        {
            UseImmediateGlyphs();
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });

            Assert.Greater(_subsystem.Reconciler().LastRunThreadId, 0, "sanity: a reconcile actually ran");
            Assert.AreNotEqual(mainThreadId, _subsystem.Reconciler().LastRunThreadId,
                "the cross-tile dedup ran OFF the main thread (a thread-pool worker)");
        }

        // ═══ ONE reconcile in flight; the stale FRONT (unchanged in identity) is served while it is pending; the
        //         completed result is applied on pickup even though the store moved on (apply-stale), then a follow-up
        //         reconcile catches the display up. RED-verify: (a) a discard-on-generation-mismatch impl never
        //         applies the stale result → the stale-applied assertion fails; (b) a clear-front-on-schedule breaks
        //         the held-front assertions; the delta==1 pins one-in-flight coalescing. ═══

        [UnityTest]
        public IEnumerator Reconcile_OneInFlight_ServesStaleFront_AppliesStale_ThenReschedules()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            var loaded = new List<LoadedTileKey> { Key(tile) };
            yield return PumpToQuiescence(loaded);
            int aWinners = _quiescedPlan.WinnerCount;
            Assert.AreEqual(0, DepartingSymbolCount(_quiescedPlan), "front A is the active set (non-departing)");

            // Gate the NEXT reconcile so it parks in flight.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.Reconciler().GateForTest = gate;
            int recomputeBefore = _subsystem.CollectRecomputeCount;
            var empty = new List<LoadedTileKey>();

            // Gated frames. TWO distinct generation-advancing events fire WHILE the one reconcile is in flight, so its
            // captured snapshot is genuinely STALE vs the store's current state:
            //   f==0 → A leaves cover (schedules the gated reconcile off the A-DEPARTING snapshot),
            //   f==5 → A re-enters cover (store moves back to A-ACTIVE; the in-flight snapshot is now stale).
            // Throughout, the held FRONT is served unchanged — same WinnerCount AND the same per-record identity
            // (blockId/localIndex/departing), not merely the same count (item 5). Exactly ONE reconcile is scheduled.
            int[] idBlock = null, idLocal = null; byte[] idDep = null;
            for (int f = 0; f < 12; f++)
            {
                if (f == 0) _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);        // event 1: A departs
                else if (f == 5) _subsystem.ReconcileLoadedTiles(loaded, nowSeconds: 101.0);  // event 2: A re-enters (STALE now)
                else _subsystem.ReconcileLoadedTiles(f < 5 ? empty : loaded, nowSeconds: 101.0);
                _subsystem.PumpBuilds();
                SymbolGatherPlan held = _subsystem.CurrentBatch(default, 0.0);
                Assert.IsTrue(_subsystem.ReconcileInFlight(), $"frame {f}: the reconcile is gated in flight");
                Assert.AreEqual(aWinners, held.WinnerCount, $"frame {f}: the stale FRONT A is served unchanged while pending");
                Assert.AreEqual(0, DepartingSymbolCount(held), $"frame {f}: …still the active set (no premature departing swap)");
                if (f == 0) { idBlock = CopyInts(held.BlockId, aWinners); idLocal = CopyInts(held.LocalIndex, aWinners); idDep = CopyBytes(held.Departing, aWinners); }
                else
                    for (int i = 0; i < aWinners; i++)
                    {
                        Assert.AreEqual(idBlock[i], held.BlockId[i], $"frame {f}: held-front blockId[{i}] identity unchanged");
                        Assert.AreEqual(idLocal[i], held.LocalIndex[i], $"frame {f}: held-front localIndex[{i}] identity unchanged");
                        Assert.AreEqual(idDep[i], held.Departing[i], $"frame {f}: held-front departing[{i}] identity unchanged");
                    }
                yield return null;
            }
            Assert.AreEqual(recomputeBefore + 1, _subsystem.CollectRecomputeCount,
                "exactly ONE reconcile was scheduled across all gated frames (coalesced — never a second in-flight worker)");
            int recomputeAfterGate = _subsystem.CollectRecomputeCount;

            // Release the gate → the completed reconcile is applied on pickup. Its captured state is A-DEPARTING
            // (stale), so the front FLIPS TO DEPARTING even though the store is now A-ACTIVE — proving apply-stale,
            // not a recompute-to-current (a discard-on-mismatch impl would never show departing here).
            gate.Set();
            bool staleApplied = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded, nowSeconds: 101.0); // store stays A-active
                _subsystem.PumpBuilds();
                SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                if (p.WinnerCount > 0 && DepartingSymbolCount(p) == p.WinnerCount) { staleApplied = true; break; }
                yield return null;
            }
            Assert.IsTrue(staleApplied,
                "apply-stale: the completed reconcile's STALE (A-departing) result was applied on pickup — NOT discarded/recomputed to the current A-active state");
            Assert.Greater(_subsystem.CollectRecomputeCount, recomputeAfterGate,
                "a follow-up reconcile was scheduled because the store generation advanced during the run (reschedule-after-stale-apply)");

            // The newer (A-active) state eventually appears as the reschedule catches the display up.
            bool caughtUp = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded, nowSeconds: 101.0);
                _subsystem.PumpBuilds();
                SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                if (p.WinnerCount > 0 && DepartingSymbolCount(p) == 0) { caughtUp = true; break; }
                yield return null;
            }
            Assert.IsTrue(caughtUp, "the newer (A-active) state eventually appears after the reschedule catches up");
        }

        private static int[] CopyInts(NativeList<int> src, int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = src[i];
            return a;
        }

        private static byte[] CopyBytes(NativeList<byte> src, int n)
        {
            var a = new byte[n];
            for (int i = 0; i < n; i++) a[i] = src[i];
            return a;
        }

        // ═══ A faulting reconcile (throws AFTER partially populating a MISALIGNED result — Output longer than
        //         BlockId) → the exception is directly OBSERVED (GetResult rethrows), NO swap (a swap would feed the
        //         misaligned partial buffer to SymbolGatherPlan.Build and crash), the old front is held, inFlight
        //         resets, and a later event reschedules + recovers. RED-verify: (a) swap-on-non-success → the
        //         misaligned back reaches the front → the held-front assertion fails (and Build would crash); (b) an
        //         impl that swallows the fault without GetResult → ReconcileFaultObserved stays false. ═══

        [UnityTest]
        public IEnumerator Reconcile_Faults_Observed_NoSwap_FrontHeld_ReschedulesOnNextEvent()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            var loaded = new List<LoadedTileKey> { Key(tile) };
            yield return PumpToQuiescence(loaded);
            int aWinners = _quiescedPlan.WinnerCount;
            int recomputeBefore = _subsystem.CollectRecomputeCount;
            Assert.IsFalse(_subsystem.ReconcileFaultObserved, "precondition: no fault observed yet");

            // Inject a fault into the NEXT reconcile, then a tile event (A leaves cover) to schedule it.
            _subsystem.Reconciler().FaultNextRun = true;
            var empty = new List<LoadedTileKey>();
            bool faultPicked = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (!_subsystem.ReconcileInFlight() && _subsystem.CollectRecomputeCount > recomputeBefore) { faultPicked = true; break; }
                yield return null;
            }
            Assert.IsTrue(faultPicked, "the faulting reconcile was scheduled and picked up (inFlight reset)");
            Assert.IsTrue(_subsystem.ReconcileFaultObserved,
                "the worker exception was OBSERVED directly (GetResult rethrew into the catch) — not merely inferred from inFlight");

            SymbolGatherPlan held = _subsystem.CurrentBatch(default, 0.0);
            Assert.AreEqual(aWinners, held.WinnerCount, "fault → NO swap; the old front A is held (the misaligned partial back never reached Build)");
            Assert.AreEqual(0, DepartingSymbolCount(held), "…and it is still the active set, not the faulted/empty back");

            // A NEW event (advance the clock past the departing grace → purge → gen bump) reschedules; the fault is
            // spent, so it succeeds and the front recovers (A purged from the set → empty).
            bool recovered = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 1000.0);
                _subsystem.PumpBuilds();
                SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                if (p.WinnerCount == 0) { recovered = true; break; }
                yield return null;
            }
            Assert.IsTrue(recovered, "a later tile event rescheduled (fault-free) → the reconcile recovered and the front updated");
        }

        // ═══ A restyle mid-flight drains the in-flight worker, releases the front + back snapshot pins (a block
        //          shared front+back refcounts to 2), and frees every block exactly once — no leak, no double-free,
        //          no missing-key throw. The gate is opened BEFORE SetStyle so the inline drain can't hang. ═══

        [UnityTest]
        public IEnumerator Restyle_DrainsInFlightReconcile_ReleasesFrontAndBackPins_NoLeak()
        {
            UseImmediateGlyphs();
            long before = SymbolTileBlock.DebugLiveAllocCount;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            Assert.Greater(SymbolTileBlock.DebugLiveAllocCount, before, "sanity: the tile baked a live block (front pins it)");

            // Gate the next reconcile; a tile event schedules it — CaptureSnapshot pins the SAME block again (shared
            // front+back ⇒ pin count 2). The worker parks.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.Reconciler().GateForTest = gate;
            var empty = new List<LoadedTileKey>();
            for (int f = 0; f < 20 && !_subsystem.ReconcileInFlight(); f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                yield return null;
            }
            Assert.IsTrue(_subsystem.ReconcileInFlight(), "a reconcile is gated in flight (its back snapshot pins the shared block)");

            // Restyle. Open the gate FIRST so DrainInFlightReconcile observes the worker instead
            // of hanging on it. SetStyle then runs the teardown protocol: cancel → drain → ReleasePins(front) +
            // ReleasePins(back) → Clear.
            gate.Set();
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            yield return null; yield return null;

            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount,
                "restyle drained the in-flight reconcile, released the front + back (shared, refcount 2) pins, and freed every block exactly once — no leak, no double-free");
        }

        // ═══ The drain must JOIN the worker. A restyle/teardown while the reconcile worker is PARKED MID-RUN
        //          must NOT free the block the worker still reads. A drain built on _reconcileHandle.GetResult()
        //          (WorkHandle<bool>'s equivalent of UniTask's GetAwaiter().GetResult()) THROWS "not yet completed"
        //          on a pending handle instead of blocking, so it would return WITHOUT joining and the following
        //          ReleasePins+Clear would dispose the block under Run (native use-after-free) — the overlap
        //          Restyle_DrainsInFlightReconcile_ReleasesFrontAndBackPins_NoLeak avoids by opening the gate
        //          BEFORE SetStyle. Deterministic, no timing margin: with the worker
        //          parked at the gate, drive the teardown core on a BACKGROUND thread — a joining drain
        //          BLOCKS there (block stays alive), a non-joining drain returns and frees it — and read block
        //          liveness from the test thread while the worker is STILL parked (gate unset). The 2 s Wait is
        //          only an upper bound to tell "blocked" from "returned"; the liveness assert has no clock.
        //          RED-verify: make DrainInFlightReconcile call `_reconcileHandle.GetResult()` directly instead
        //          of `_reconcileHandle.ToUniTask().WaitOffPlayerLoop(...)` first → the teardown returns while the
        //          worker is parked and DebugLiveAllocCount drops → both asserts fail.
        [UnityTest]
        public IEnumerator TeardownWhileReconcileWorkerParked_JoinsWorker_DoesNotFreeBlockUnderIt()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            long liveWithBlock = SymbolTileBlock.DebugLiveAllocCount;
            Assert.Greater(liveWithBlock, 0, "sanity: the tile baked a live block the front pins");

            // Gate the next reconcile so its worker PARKS in flight — CaptureSnapshot pins the same block again
            // (shared front+back), and the worker holds that back pin while parked at the top of Run.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.Reconciler().GateForTest = gate;
            var empty = new List<LoadedTileKey>();
            for (int f = 0; f < 20 && !_subsystem.ReconcileInFlight(); f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                yield return null;
            }
            Assert.IsTrue(_subsystem.ReconcileInFlight(), "a reconcile is gated in flight (its back snapshot pins the shared block)");
            // Run consumes+nulls GateForTest immediately BEFORE gate.Wait() (SymbolReconciler.cs), so a null gate
            // proves the worker is PARKED inside Run, past every cancellation check, having read no columns yet.
            Assert.IsTrue(System.Threading.SpinWait.SpinUntil(() => _subsystem.Reconciler().GateForTest == null, 5000),
                "the reconcile worker reached the gate (parked inside Run)");

            // The teardown core (cancel → drain → ReleasePins front+back → Clear) on a BACKGROUND thread.
            // A joining drain blocks on the parked worker HERE (never reaches ReleasePins). A non-joining drain
            // returns at once, then ReleasePins drops the pin to 0 and the block is disposed while the worker is
            // still parked.
            var teardown = System.Threading.Tasks.Task.Run(() => _subsystem.TeardownReconcileForTest());

            bool teardownReturnedWhileParked = teardown.Wait(2000); // upper bound: broken ≈ µs, fixed blocks until we set the gate
            long liveWhileWorkerParked = SymbolTileBlock.DebugLiveAllocCount; // worker still parked (gate unset) ⇒ no clock

            gate.Set();        // release the worker so the fixed teardown can join it and complete
            teardown.Wait();   // let the teardown finish in both builds (never hangs — the gate is open)

            Assert.IsFalse(teardownReturnedWhileParked,
                "the drain must BLOCK until the worker completes — it returned while the worker was still parked mid-Run, so it never joined it (GetResult throws on a pending task instead of waiting)");
            Assert.AreEqual(liveWithBlock, liveWhileWorkerParked,
                "the block a parked reconcile worker still reads must NOT be freed under it: a non-joining drain lets ReleasePins+Clear dispose it mid-Run (native use-after-free)");
        }

        // ═══ The SUCCESS-swap pin release (PickupCompletedReconcile's success branch), independent of
        //          teardown. A rebuild
        //          defers the old block (pinned by the displayed front); when the next reconcile SUCCEEDS and swaps,
        //          the demoted old-front snapshot's ReleasePins frees that now-unreferenced block, returning to
        //          baseline WHILE the subsystem is still live. RED-verify: delete the success-branch ReleasePins
        //          in PickupCompletedReconcile → the demoted front never releases → the rebuilt-over block
        //          stays deferred → this times out. ═══

        [UnityTest]
        public IEnumerator SuccessfulSwap_ReleasesDemotedFrontPins_FreesRebuiltOverBlock()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(loaded);
            long liveWithOneBlock = SymbolTileBlock.DebugLiveAllocCount; // the front pins this tile's block

            // Rebuild the tile → a NEW block is baked; the OLD block is still referenced by the front snapshot, so
            // CompleteBuild's release of the entry's own reference leaves it alive (the snapshot's own reference
            // survives). PHASE 1: wait until the rebuild has committed and the old block is genuinely
            // referenced-but-alive (TWO live blocks) — so the phase-2 return-to-one is a REAL free, not a
            // trivially-already-true baseline.
            DriveTileBytesReady(tile);
            bool deferred = false;
            for (int f = 0; f < 300; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (SymbolTileBlock.DebugLiveAllocCount == liveWithOneBlock + 1) { deferred = true; break; }
                yield return null;
            }
            Assert.IsTrue(deferred, "sanity: the rebuild baked a new block and DEFERRED the old (pinned) one — two live blocks");

            // PHASE 2: the successful reconcile swap for the new generation demotes the old front and
            // releases its pins (PickupCompletedReconcile's success branch) → the deferred old block frees,
            // returning to ONE live block — with no restyle/teardown.
            bool freedBackToBaseline = false;
            for (int f = 0; f < 400; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (_subsystem.ReadyTailCount() == 0 && !_subsystem.ReconcileInFlight()
                    && SymbolTileBlock.DebugLiveAllocCount == liveWithOneBlock) { freedBackToBaseline = true; break; }
                yield return null;
            }
            Assert.IsTrue(freedBackToBaseline,
                "a SUCCESSFUL reconcile swap demoted the old front and released its pins, freeing the deferred rebuilt-over block back to one live block — distinct from teardown");
        }

        // ═══ The FAULT-path pin release (PickupCompletedReconcile's fault branch). A fault captures a back
        //          snapshot that shares the displayed block with the front; the fault path must ReleasePins that
        //          FAILED back, or its pin LEAKS and the block can never be freed. A subsequent restyle
        //          (drain+release+Clear) then frees everything back to baseline. RED-verify: delete the
        //          fault-branch ReleasePins in PickupCompletedReconcile →
        //          the leaked back-pin keeps the block pinned → restyle's Clear can't free it → leak (not baseline). ═══

        [UnityTest]
        public IEnumerator FaultPickup_ReleasesFailedBackPins_NoLeakAfterRestyle()
        {
            UseImmediateGlyphs();
            long before = SymbolTileBlock.DebugLiveAllocCount;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            Assert.Greater(SymbolTileBlock.DebugLiveAllocCount, before, "sanity: the tile baked a live block (front pins it)");

            // Fault the next reconcile; a tile event (A departs) schedules it — the captured back snapshot shares the
            // tile's block with the front (refcount 2). On the fault pickup, the fault branch must ReleasePins the
            // failed back.
            _subsystem.Reconciler().FaultNextRun = true;
            var empty = new List<LoadedTileKey>();
            bool faultObserved = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (_subsystem.ReconcileFaultObserved && !_subsystem.ReconcileInFlight()) { faultObserved = true; break; }
                yield return null;
            }
            Assert.IsTrue(faultObserved, "the fault was observed and its pickup completed");

            // Restyle → drain + ReleasePins(front)+ReleasePins(back) + Clear. WITH the fault-branch release the
            // shared block's back pin was already released, so front-release + Clear free it exactly once →
            // baseline. WITHOUT it the leaked back pin keeps it pinned, so Clear's conditional flush must leave
            // it → a leaked live block.
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            yield return null; yield return null;
            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount,
                "the fault path released the FAILED back snapshot's pins → restyle frees the block exactly once, no leak");
        }

        // ═══ The async production path yields the SAME winner plan as the store's inline CollectInto oracle
        //          over the same quiesced state (minCoverage 0 ⇒ no coverage classify to perturb order). ═══

        [UnityTest]
        public IEnumerator ProductionFront_MatchesInlineCollectOracle()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            SymbolGatherPlan front = _subsystem.CurrentBatch(default, 0.0);

            var oBlk = new List<int>(); var oLoc = new List<int>(); var oDep = new List<byte>();
            _subsystem.Store().CollectInto(oBlk, oLoc, oDep, SymbolSubsystem.DedupEnabled, out int _);

            Assert.Greater(oBlk.Count, 0, "sanity: the oracle collected labels");
            Assert.AreEqual(oBlk.Count, front.WinnerCount, "async front winner count == inline collect oracle");
            for (int i = 0; i < oBlk.Count; i++)
            {
                Assert.AreEqual(oBlk[i], front.BlockId[i], $"blockId[{i}] mismatch (async front vs inline oracle)");
                Assert.AreEqual(oLoc[i], front.LocalIndex[i], $"localIndex[{i}] mismatch");
                Assert.AreEqual(oDep[i], front.Departing[i], $"departing[{i}] mismatch");
            }
        }

        // ═══ A REAL front swap through the production subsystem must invalidate the gather memo — the
        //      end-to-end guard (Memo_VersionChange_Invalidates in SymbolGatherMemoTests only proves the
        //      version-consulted UNIT behaviour; only this proves the key is wired to a real reconcile swap).
        //      Three required properties:
        //        (a) EVENT vs SWAP keying: immediately after the store event (BeginBuild/CompleteBuild), before
        //            the reconcile completes, the mirror must stay a memo HIT and still serve the OLD content —
        //            an implementation keyed on the tile EVENT (e.g. _store.CollectGeneration) would
        //            wrongly rebuild HERE instead of waiting for the swap.
        //        (b) The rebuild must land specifically on the frame PickupCompletedReconcile's swap actually
        //            happens (ReconcileInFlight() observed true, THEN the pickup call flips it false), not
        //            merely "sometime within a settle window".
        //        (c) The new content must be gather-VISIBLE-DIFFERENT at a FIXED WinnerCount — a byte-identical
        //            rebuild (the construction SuccessfulSwap_ReleasesDemotedFrontPins_FreesRebuiltOverBlock
        //            uses) can never fail SymbolBatchDiff regardless of memo correctness.
        //      The tile event commits the replacement DIRECTLY through Store() (BeginBuild+Bake+
        //      CompleteBuild — the SAME two store calls the real async worker path uses; MarkCollectDirty fires
        //      from inside them either way, so this still exercises the real front/back double-buffer + pickup +
        //      apply-stale state machine, only the MVT-decode step is bypassed). BOTH calls run in ONE
        //      synchronous block, no CurrentBatch poll between them, so their two independent CollectGeneration
        //      bumps (SymbolTileStore.BeginBuild and CompleteBuild each "always bump") coalesce
        //      into exactly ONE ScheduleReconcileIfDirty-observed event: a single swap at a known frame, which is
        //      what lets (a)/(b) assert an EXACT frame instead of tolerating the legitimate-but-unpredictable
        //      double-swap a DriveTileBytesReady-driven rebuild can trigger (see Memo_RestyleBetweenTicks_Invalidates'
        //      sibling test and this file's SuccessfulSwap_ReleasesDemotedFrontPins_FreesRebuiltOverBlock, both of
        //      which DO tolerate that). ═══

        [UnityTest]
        public IEnumerator Memo_RealFrontSwap_Invalidates()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(loaded);

            var harness = new LpsHarness();
            try
            {
                SymbolGatherPlan plan = _subsystem.CurrentBatch(default, 0.0); // the subsystem's ONE reused _gatherPlan
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");
                int winnersBefore = plan.WinnerCount;
                Assert.Greater(winnersBefore, 0, "sanity: the tile produced at least one winner");
                var contentBefore = new SymbolBatch(); harness.Lps.CopyMirrorInto(contentBefore);

                // Steady state, no tile event — the memo must engage and stay engaged.
                for (int f = 0; f < 3; f++)
                {
                    _subsystem.ReconcileLoadedTiles(loaded);
                    _subsystem.PumpBuilds();
                    SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                    harness.Lps.GatherIntoMirror(p);
                    Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, $"frame {f}: no tile event — the gather must stay a memo HIT");
                }

                // The real tile event: replace the SAME store key's content with a DIFFERENT, gather-visible
                // payload at the SAME winner count (Codex 1c) — synthetic symbols far from any coordinate the
                // real fixture decodes to, so FirstDifference below is unambiguous regardless of the fixture's
                // own content. BeginBuild+Bake+CompleteBuild run back-to-back (no CurrentBatch poll between
                // them — see the header comment's coalescing argument).
                var replacementBuffer = new SymbolTileBuffer();
                for (int i = 0; i < winnersBefore; i++)
                    AddPointSymbol(replacementBuffer, new double3(9000 + i * 10, 0, 9000), "replacement" + i, 900 + i, Tk(tile), 0.9f);
                int gen = _subsystem.Store().BeginBuild(StoreKey(tile));
                SymbolTileBlock newBlock = SymbolTileBlockBaker.Bake(
                    replacementBuffer, slotCount: 1, TileRenderOrigin.Project(tile, P));
                Assert.IsTrue(_subsystem.Store().CompleteBuild(StoreKey(tile), gen, newBlock), "sanity: replacement block committed");

                // (a) EVENT-keying check: the frame right after the store event (before any reconcile has had a
                // chance to complete) must still be a memo HIT serving the OLD content.
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                SymbolGatherPlan pEvent = _subsystem.CurrentBatch(default, 0.0);
                harness.Lps.GatherIntoMirror(pEvent);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount,
                    "immediately after the store event the mirror must still be a memo HIT — event-keyed (rather " +
                    "than swap-keyed) invalidation would wrongly rebuild here, before the reconcile has even completed");
                var contentAtEvent = new SymbolBatch(); harness.Lps.CopyMirrorInto(contentAtEvent);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(contentBefore, contentAtEvent),
                    "the OLD front's content must still be served this frame — the replacement hasn't been picked up yet");

                // (b) poll until PickupCompletedReconcile's swap actually lands (ReconcileInFlight() observed
                // true beforehand, then the pickup call flips it false this frame), asserting the mirror stays
                // flat WHILE the worker is in flight and rebuilds EXACTLY on the swap frame — not merely
                // "eventually, within a settle window".
                bool sawInFlight = false, swappedThisFrame = false;
                for (int f = 0; f < 400 && !swappedThisFrame; f++)
                {
                    _subsystem.ReconcileLoadedTiles(loaded);
                    _subsystem.PumpBuilds();
                    bool inFlightBefore = _subsystem.ReconcileInFlight();
                    SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0); // PickupCompletedReconcile runs inside this call
                    harness.Lps.GatherIntoMirror(p);
                    if (inFlightBefore) sawInFlight = true;
                    if (harness.Lps.MirrorRebuildCount > 1)
                    {
                        swappedThisFrame = true;
                        Assert.IsTrue(sawInFlight,
                            "the swap must be preceded by an observed in-flight reconcile — proves the memo tracked the SWAP, not just the store event");
                    }
                    else
                    {
                        Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, $"frame {f}: still waiting for pickup — the mirror must stay a memo HIT until the swap lands");
                        yield return null;
                    }
                }
                Assert.IsTrue(swappedThisFrame, "the tile-replacement event must eventually land as a front swap");
                Assert.AreEqual(2, harness.Lps.MirrorRebuildCount,
                    "exactly ONE heavy rebuild for the single, precisely-timed swap — a missing version bump would leave this flat");

                SymbolGatherPlan finalPlan = _subsystem.CurrentBatch(default, 0.0);
                Assert.AreEqual(winnersBefore, finalPlan.WinnerCount, "coupled constraint: WinnerCount must be UNCHANGED across the swap");

                // (c) content genuinely changed — the memo, when it correctly invalidated, picked up the
                // REPLACEMENT, not stale OLD content held over from before the swap.
                var contentAfter = new SymbolBatch(); harness.Lps.CopyMirrorInto(contentAfter);
                Assert.IsNotNull(SymbolBatchDiff.FirstDifference(contentBefore, contentAfter),
                    "post-swap content must DIFFER from pre-swap content — a memo that failed to invalidate would still show the OLD payload");

                // Post-swap frames must go back to memo-hitting (no further event).
                int rebuildAfterSwap = harness.Lps.MirrorRebuildCount;
                for (int f = 0; f < 3; f++)
                {
                    _subsystem.ReconcileLoadedTiles(loaded);
                    _subsystem.PumpBuilds();
                    SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                    harness.Lps.GatherIntoMirror(p);
                    Assert.AreEqual(rebuildAfterSwap, harness.Lps.MirrorRebuildCount, $"post-swap frame {f}: back to a memo HIT");
                }

                // Reference: an independent gather over the SAME final plan content — confirms the held mirror's
                // content is not just "different from before" but EXACTLY the replacement.
                var refHarness = new LpsHarness();
                try
                {
                    refHarness.Lps.GatherIntoMirror(finalPlan);
                    var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                    var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                    Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                        "post-swap the held-mirror system's content must match a fresh gather over the same plan");
                }
                finally { refHarness.Dispose(); }
            }
            finally { harness.Dispose(); }
        }

        // ═══ A restyle (SetStyle) between two production gathers on the SAME plan object must invalidate
        //      the memo, even though the front content collapses to EMPTY. The `_frontSetVersion++` bump is
        //      defense-in-depth (the `plan.WinnerCount == _mirrorCount` predicate term is the actual
        //      crash-prevention for a release player) — with the bump present this test observes no throw and a
        //      clean rebuild; the RED-verify signal for a MISSING bump is a Debug.LogAssertion from
        //      AssertMemoPlanMatchesMirror (NUnit fails a test on an unexpected one), not a content diff or a
        //      throw. ═══

        [UnityTest]
        public IEnumerator Memo_RestyleBetweenTicks_Invalidates()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(loaded);

            var harness = new LpsHarness();
            try
            {
                SymbolGatherPlan plan = _subsystem.CurrentBatch(default, 0.0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");
                Assert.Greater(plan.WinnerCount, 0, "sanity: the tile produced at least one winner before the restyle");

                // Restyle: SetStyle Clear()s _frontResult/_backResult — Step 2.3's bump site.
                StyleDocument restyle = StyleParser.Parse(StyleJson);
                _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));

                SymbolGatherPlan afterRestyle = _subsystem.CurrentBatch(default, 0.0); // SAME _gatherPlan object, now empty
                Assert.DoesNotThrow(() => harness.Lps.GatherIntoMirror(afterRestyle),
                    "a same-object, now-empty plan must rebuild cleanly (no out-of-range read) after a restyle");
                Assert.AreEqual(2, harness.Lps.MirrorRebuildCount,
                    "the restyle must invalidate the memo — a stale memo hit against a zero-length plan would either " +
                    "throw (checked NativeArray.Copy) or silently read garbage (release player)");
                Assert.AreEqual(0, afterRestyle.WinnerCount, "sanity: the front is empty after the restyle");
            }
            finally { harness.Dispose(); }
        }

        // ═══ The pin prevents a native USE-AFTER-FREE — a block a reconcile result still references is NOT freed
        //         at a drop site while a snapshot references it; the gather derefs its NativeArrays safely; the
        //         snapshot's release frees it.
        //         RED-verify: revert the Release true-evict site's `?.Release()` to a direct `?.Value.Dispose()` →
        //         the block frees at the drop site (the "not freed" assertion fails) and the gather derefs freed
        //         memory. ═══

        private static readonly WebMercatorProjection P = new WebMercatorProjection();
        private static SymbolTileStore.Key StoreKey(TileId t) => new SymbolTileStore.Key("s", t);
        private static long Tk(TileId t) => SymbolTileKey.Pack(t);

        private static (List<SymbolQuad> Quads, float2 BoundsMin, float2 BoundsMax) OneQuad(float u) => (
            new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            float2.zero, new float2(18f, 18f));

        private static void AddPointSymbol(SymbolTileBuffer buffer, double3 anchor, string text, int feature, long tileKey, float u)
        {
            var layout = OneQuad(u);
            TestSymbolTileBuffer.AddPoint(buffer, anchor, layout.Quads, layout.BoundsMin, layout.BoundsMax,
                text: text, textSizePx: 20f, paddingPx: 2f, featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);
        }

        [Test]
        public void Pin_PreventsNativeUseAfterFree_InGather()
        {
            var tile = new TileId { Z = 5, X = 16, Y = 16 };
            var store = new SymbolTileStore(cacheCap: 8);
            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, new double3(100, 0, 200), "a", 1, Tk(tile), 0.1f);
            int gen = store.BeginBuild(StoreKey(tile));
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(StoreKey(tile), gen, block), "sanity: block committed");
            long live0 = SymbolTileBlock.DebugLiveAllocCount;

            // Capture (pins the block) + run the reconciler → a result referencing the block via OrderedBlocks.
            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            // A store drop site fires while the snapshot still references the block → the drop must NOT free it
            // (the snapshot's own reference survives), so the gather below can still deref its NativeArrays.
            store.Release(StoreKey(tile), transferredToCache: false); // active → true evict → releases the entry's own ref
            Assert.AreEqual(live0, SymbolTileBlock.DebugLiveAllocCount,
                "the pinned block is NOT freed at the drop site (deferred) — reverting to a direct Dispose fails HERE");

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                var decisions = new List<byte>(result.BlockId.Count);
                for (int i = 0; i < result.BlockId.Count; i++) decisions.Add(SymbolTileCoverageFilter.Keep);
                plan.Build(result.BlockId, result.LocalIndex, result.IsDeparting, decisions, result.OrderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan); // derefs the (still-alive, pinned) block's NativeArrays — a freed block here is a UAF
                var gathered = new SymbolBatch();
                harness.Lps.CopyMirrorInto(gathered);
                Assert.Greater(gathered.Count, 0, "the gather read the pinned block's records with no use-after-free");

                store.ReleasePins(snapshot); // last pin gone → the deferred dispose fires now
                Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                    "releasing the last pin frees the deferred block exactly once");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Owns the LPS + throwaway Unity resources for the gather (mirrors SymbolGatherParityTests.LpsHarness).
        private sealed class LpsHarness : IDisposable
        {
            public readonly SymbolPlacementSystem Lps;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public LpsHarness()
            {
                _camGo = new GameObject("ReconcileUaf_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(64, 64, 0);
                uCam.targetTexture = _rt;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 0, Longitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Lps = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Lps.Dispose();
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(_camGo);
                UnityEngine.Object.DestroyImmediate(_rt);
                UnityEngine.Object.DestroyImmediate(_baseMaterial);
            }
        }

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
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolReconcilerTests — the off-main SymbolReconciler and the store's native pin guard
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The off-main <see cref="SymbolReconciler"/> + the store's native
    /// pin guard. The teeth: the worker is BYTE-IDENTICAL to an independent string oracle; the
    /// disposal-bumps-generation edge; the reused-result SHRINK; the per-site defer/flush TIMING; the
    /// cross-snapshot refcount OVERLAP. The SHRINK, TIMING and OVERLAP teeth are RED-verified against un-guarded code.
    ///
    /// <para><b>Every commit bakes a real block.</b> Every commit that participates in a
    /// <c>CaptureSnapshot</c>/<c>Run</c> call bakes and commits a REAL <see cref="SymbolTileBlock"/> (via
    /// <see cref="Commit"/>) — the reconciler reads the block, so a block-less (<c>Block == null</c>) entry is
    /// not collected at all (see <c>SymbolTileStore.CaptureSnapshot</c>'s guard). Winner identity is
    /// <c>(BlockId, LocalIndex)</c>; where a test needs to know WHICH symbol a winner is, it reads the
    /// winner's <c>OrderedBlocks[BlockId]</c> block's own columns (<c>TileKey</c>/<c>PairRoles</c>/…) —
    /// never a re-derivation of the oracle's own indices, which would just restate them (see the parity
    /// note in <c>Reconciler_Run_MatchesStringOracle_MultiTile</c>).</para>
    /// </summary>
    [TestFixture]
    public class SymbolReconcilerTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here — real
        // blocks via DebugLiveAllocCount, and FakeBlock via the same mirrored counter below.
        private long _liveBlocks;
        private int _liveFakes;
        [SetUp] public void BaselineBlocks()
        {
            _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
            _liveFakes = FakeBlock.LiveCount;
        }
        [TearDown] public void NoLeakedBlocks()
        {
            Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
                "this test baked a block it never disposed — release the snapshot and Clear() the store");
            Assert.AreEqual(_liveFakes, FakeBlock.LiveCount,
                "this test committed a FakeBlock it never disposed — Clear() the store");
        }

        private const double ParityQ = 50.0;

        private sealed class FakeBlock : IDisposable
        {
            // Mirrors SymbolTileBlock.DebugLiveAllocCount's idiom so the fixture's leak-guard TearDown can
            // catch an abandoned fake commit too — every DisposeCount assertion in this fixture is 0 or 1,
            // so decrementing unconditionally on every Dispose() call stays correct.
            internal static int LiveCount;
            public int DisposeCount;
            public FakeBlock() => LiveCount++;
            public void Dispose() { DisposeCount++; LiveCount--; }
        }

        private static SymbolTileStore.Key Key(TileId t) => new SymbolTileStore.Key("src", t);

        // Bakes a REAL block for `buffer` and commits it — the reconciler cutover means every commit a test wants
        // CaptureSnapshot/Run to see needs a real SymbolTileBlock (a block-less entry is no longer collected;
        // see the type doc). Bake does not intern (it copies the ids ShapedSymbol already carries),
        // so there is no table to share here any more.
        private static bool Commit(SymbolTileStore store, SymbolTileStore.Key key, int gen, SymbolTileBuffer buffer)
            => store.CompleteBuild(key, gen, SymbolTileBlockBaker.Bake(buffer, slotCount: 1, double3.zero));

        // ShapedSymbol carries only the INTERNED TextId/IconImageId, not the raw string — but Oracle()
        // below must stay a genuinely STRING-keyed computation (independent of SymbolStringTable.Intern, which
        // is itself exactly what the RED-verification here targets — see Oracle's doc). So Point/Curved record
        // each returned symbol's raw text/icon in this side table, keyed by the (structurally-comparable)
        // ShapedSymbol value itself, for Oracle to read back instead of a now-nonexistent symbol.Text.
        private readonly Dictionary<ShapedSymbol, string> _rawText = new Dictionary<ShapedSymbol, string>();
        private readonly Dictionary<ShapedSymbol, string> _rawIcon = new Dictionary<ShapedSymbol, string>();

        // Appends one point (or icon, via `icon`) symbol into `buffer` and returns the resulting record — so a
        // caller can both group it into its tile's buffer AND hold it for a later assertion, mirroring the
        // pre-migration per-symbol managed carrier local var it replaces. pairRole/pairId default to None/0 —
        // every existing call site (unpaired symbols) is unaffected.
        private ShapedSymbol Point(SymbolTileBuffer buffer, double3 anchor, int layer, string text, string icon,
            int feature, TileId tile, SymbolPairRole pairRole = SymbolPairRole.None, int pairId = 0)
        {
            TestSymbolTileBuffer.AddPoint(buffer, anchor, null, float2.zero, float2.zero,
                text: text, iconImage: icon, materialIndex: layer, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile),
                pairRole: pairRole, pairId: pairId);
            ShapedSymbol symbol = buffer.Symbols[buffer.Symbols.Count - 1];
            _rawText[symbol] = text; _rawIcon[symbol] = icon;
            return symbol;
        }

        private ShapedSymbol Curved(SymbolTileBuffer buffer, string text, int feature, TileId tile)
        {
            TestSymbolTileBuffer.AddCurved(buffer, null, null, null,
                placement: SymbolPlacement.LineCenter, text: text, materialIndex: 0, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));
            ShapedSymbol symbol = buffer.Symbols[buffer.Symbols.Count - 1];
            _rawText[symbol] = text; _rawIcon[symbol] = null;
            return symbol;
        }

        // Independent STRING-keyed oracle — the SAME scan order + finest-zoom rule + per-tile blockId assignment
        // as the reconciler, but keyed on the string CrossTileSymbolKey (NOT the code under test), and walking the
        // fixture's OWN ShapedSymbol lists (NOT the block the reconciler reads) — a genuinely separate
        // computation. activeTiles / departingTiles are in the store's _active / _departing enumeration order.
        // ShapedSymbol itself does not carry the raw string (only the interned TextId/IconImageId,
        // the SAME ints the reconciler's DedupKey reads) — reading those here would make this oracle blind to
        // exactly the bug class it RED-verifies (a broken SymbolStringTable.Intern). So this
        // reads the raw text/icon back from `_rawText`/`_rawIcon` (populated by Point/Curved), never from
        // symbol.TextId/IconImageId.
        private struct OracleOut
        {
            public List<ShapedSymbol> Output; public int ActiveCount;
            public List<int> BlockId; public List<int> LocalIndex; public List<byte> IsDeparting;
        }

        private OracleOut Oracle(List<List<ShapedSymbol>> activeTiles, List<List<ShapedSymbol>> departingTiles)
        {
            var output = new List<ShapedSymbol>(); var blockIds = new List<int>();
            var localIndices = new List<int>(); var isDeparting = new List<byte>();
            var dedup = new Dictionary<CrossTileSymbolKey, (ShapedSymbol symbol, int z, long tileKey, int blockId, int localIndex)>();
            int nextBlock = 0;

            foreach (List<ShapedSymbol> tile in activeTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    ShapedSymbol symbol = tile[i];
                    if (symbol.Placement != SymbolPlacement.Point)
                    {
                        output.Add(symbol); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(0);
                        continue;
                    }
                    var key = CrossTileSymbolKey.For(symbol.AnchorRender, symbol.MaterialIndex, _rawText[symbol], _rawIcon[symbol], CrossTileSymbolKey.CanonicalGridMeters);
                    int z = (int)(symbol.TileKey >> 44);
                    if (!dedup.TryGetValue(key, out var cur) || z > cur.z || (z == cur.z && symbol.TileKey < cur.tileKey))
                        dedup[key] = (symbol, z, symbol.TileKey, myBlock, i);
                }
            }
            foreach (var kv in dedup)
            {
                output.Add(kv.Value.symbol); blockIds.Add(kv.Value.blockId); localIndices.Add(kv.Value.localIndex); isDeparting.Add(0);
            }
            int activeCount = output.Count;

            foreach (List<ShapedSymbol> tile in departingTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    ShapedSymbol symbol = tile[i];
                    if (symbol.Placement == SymbolPlacement.Point)
                    {
                        var key = CrossTileSymbolKey.For(symbol.AnchorRender, symbol.MaterialIndex, _rawText[symbol], _rawIcon[symbol], CrossTileSymbolKey.CanonicalGridMeters);
                        if (dedup.ContainsKey(key)) continue;
                        dedup[key] = (symbol, 0, symbol.TileKey, myBlock, i);
                    }
                    output.Add(symbol); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(1);
                }
            }
            return new OracleOut { Output = output, ActiveCount = activeCount, BlockId = blockIds, LocalIndex = localIndices, IsDeparting = isDeparting };
        }

        // ═══ Reconciler byte-identical (mixed curved / point / icon / departing) ═══

        [Test]
        public void Reconciler_Run_MatchesStringOracle_MultiTile()
        {
            double3 cellShared = new double3(ParityQ * 100.0, 0, ParityQ * 100.0);
            double3 cellB = new double3(ParityQ * 200.0, 0, ParityQ * 200.0);
            double3 cellIcon = new double3(ParityQ * 300.0, 0, ParityQ * 300.0);
            double3 cellUnique = new double3(ParityQ * 400.0, 0, ParityQ * 400.0);

            var tileP = new TileId { Z = 10, X = 500, Y = 400 };
            var tileC = new TileId { Z = 11, X = 1000, Y = 800 };
            var tileM = new TileId { Z = 12, X = 3, Y = 4 };
            var tileDep = new TileId { Z = 11, X = 1000, Y = 801 };

            var pBuffer = new SymbolTileBuffer();
            var parentShared = Point(pBuffer, cellShared + new double3(1, 0, 1), 0, "Shared", null, 1, tileP);
            var cBuffer = new SymbolTileBuffer();
            var childShared = Point(cBuffer, cellShared, 0, "Shared", null, 2, tileC);
            var mBuffer = new SymbolTileBuffer();
            var curved = Curved(mBuffer, "Road", 3, tileM);
            var bAlpha = Point(mBuffer, cellB, 0, "Alpha", null, 4, tileM);
            var bBeta = Point(mBuffer, cellB, 0, "Beta", null, 5, tileM);
            var icoA = Point(mBuffer, cellIcon, 0, null, "ico-a", 6, tileM);
            var icoB = Point(mBuffer, cellIcon, 0, null, "ico-b", 7, tileM);
            var depBuffer = new SymbolTileBuffer();
            var depShared = Point(depBuffer, cellShared, 0, "Shared", null, 8, tileDep);
            var depUnique = Point(depBuffer, cellUnique, 0, "Unique", null, 9, tileDep);

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileP), store.BeginBuild(Key(tileP)), pBuffer);
            Commit(store, Key(tileC), store.BeginBuild(Key(tileC)), cBuffer);
            Commit(store, Key(tileM), store.BeginBuild(Key(tileM)), mBuffer);
            Commit(store, Key(tileDep), store.BeginBuild(Key(tileDep)), depBuffer);
            store.ReconcileActiveSet(
                new List<SymbolTileStore.Key> { Key(tileP), Key(tileC), Key(tileM) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(3, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            OracleOut oracle = Oracle(
                new List<List<ShapedSymbol>> { pBuffer.Symbols, cBuffer.Symbols, mBuffer.Symbols },
                new List<List<ShapedSymbol>> { depBuffer.Symbols });

            Assert.IsTrue(oracle.Output.Contains(childShared) && !oracle.Output.Contains(parentShared),
                "sanity: the fixture exercises the finest-zoom merge");
            Assert.IsFalse(oracle.Output.Contains(depShared), "sanity: the departing twin of the shared identity is claim-skipped");
            Assert.IsTrue(oracle.Output.Contains(icoA) && oracle.Output.Contains(icoB), "sanity: distinct icons both survive");

            // The reconciler has no Output list — winner identity is (BlockId, LocalIndex).
            // Comparing these element-by-element against the INDEPENDENT string oracle's own (blockId, localIndex)
            // IS the parity check (both assign blockId in the SAME per-tile scan order over the SAME source lists,
            // and localIndex == raw list position by the null-slot invariant) — not a restatement of anything the
            // reconciler itself computed.
            Assert.AreEqual(oracle.Output.Count, result.BlockId.Count, "same total emitted count");
            Assert.AreEqual(oracle.ActiveCount, result.ActiveCount, "same active/departing split");
            for (int i = 0; i < oracle.Output.Count; i++)
            {
                Assert.AreEqual(oracle.BlockId[i], result.BlockId[i], $"blockId mismatch at {i}");
                Assert.AreEqual(oracle.LocalIndex[i], result.LocalIndex[i], $"localIndex mismatch at {i}");
                Assert.AreEqual(oracle.IsDeparting[i], result.IsDeparting[i], $"isDeparting mismatch at {i}");
            }
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // ═══ A disposal-causing mutation (over-cap FIFO evict) bumps the collect generation ═══

        [Test]
        public void CollectGeneration_BumpsOnOverCapFifoEvict()
        {
            var store = new SymbolTileStore(cacheCap: 1);
            var a = new TileId { Z = 5, X = 1, Y = 0 };
            var b = new TileId { Z = 5, X = 2, Y = 0 };
            store.CompleteBuild(Key(a), store.BeginBuild(Key(a)), new FakeBlock());
            store.CompleteBuild(Key(b), store.BeginBuild(Key(b)), new FakeBlock());

            store.Release(Key(a), transferredToCache: true); // → cached (cap 1)
            int g0 = store.CollectGeneration;
            store.Release(Key(b), transferredToCache: true); // over cap → evicts a (disposes its block) — must bump
            Assert.AreNotEqual(g0, store.CollectGeneration, "an over-cap FIFO evict changed the collected set → it must bump");
            store.Clear(); // b is still cached (only a was evicted)
        }

        // ═══ The reused RESULT (and index) SHRINKS — {A,B} → {B} → empty ═══
        // RED-verify: strip `result.Clear()` / `_dedup.Clear()` from Run → the reused result accumulates prior
        // runs' records → the counts stop shrinking → this fails.

        [Test]
        public void Reconciler_ReusedResult_Shrinks_AcrossRuns()
        {
            var a = new TileId { Z = 5, X = 1, Y = 0 };
            var b = new TileId { Z = 5, X = 2, Y = 0 };
            var bufferA = new SymbolTileBuffer();
            Point(bufferA, new double3(1000, 0, 1000), 0, "A", null, 1, a);
            var bufferB = new SymbolTileBuffer();
            Point(bufferB, new double3(2000, 0, 2000), 0, "B", null, 2, b);

            var store = new SymbolTileStore(cacheCap: 8);
            Commit(store, Key(a), store.BeginBuild(Key(a)), bufferA);
            Commit(store, Key(b), store.BeginBuild(Key(b)), bufferB);

            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();   // REUSED across all three runs
            var snapshot = new SymbolSnapshot();        // REUSED across all three runs

            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(2, result.BlockId.Count, "{A,B} → two winners");

            // Released here to keep the reference model explicit — CaptureSnapshot would release it anyway
            // (its leading into.Clear() releases whatever the snapshot previously held).
            store.ReleasePins(snapshot);
            store.Release(Key(a), transferredToCache: false); // drop A → active {B}
            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(1, result.BlockId.Count, "{B} → the reused result must SHRINK to one (no stale A)");
            Assert.AreEqual(SymbolTileKey.Pack(b), result.OrderedBlocks[result.BlockId[0]].TileKey, "…and the surviving winner is B's tile");
            Assert.AreEqual(0, result.LocalIndex[0], "…B's only (raw index 0) record");

            store.ReleasePins(snapshot); // release the SECOND capture's pin on B before dropping it — same reason
            store.Release(Key(b), transferredToCache: false); // drop B → empty
            store.CaptureSnapshot(snapshot); reconciler.Run(snapshot, result);
            Assert.AreEqual(0, result.BlockId.Count, "empty set → the reused result must shrink to zero");
            Assert.AreEqual(0, result.LocalIndex.Count, "…and every parallel list too");
            Assert.AreEqual(0, result.OrderedBlocks.Count);
            Assert.AreEqual(0, result.ActiveCount);
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // ═══ Per-site defer/flush TIMING — a block a snapshot still references is NOT disposed at the drop
        //         site; it frees only when the last reference (the snapshot's) is released ═══
        // RED-verify, per site: replace that site's `?.Release()` with `?.Value.Dispose()` → the block disposes
        // at the site itself, so ReleasePins has nothing left to free → the "frees exactly once" assertion
        // fails FOR THAT [Values] CASE ONLY (the `live0` baseline is taken after the drop site runs, so the
        // preceding "unchanged" assert can't observe an already-completed dispose) — four independent,
        // individually discriminating recipes.
        //
        // CaptureSnapshot casts Entry.Block to SymbolTileBlock, so a FakeBlock committed
        // here would throw at capture time — every block under test is a REAL baked block, and disposal is
        // observed via SymbolTileBlock.DebugLiveAllocCount (the established leak-guard idiom, prior art
        // SymbolReconcileAsyncTests.Pin_PreventsNativeUseAfterFree_InGather) rather than a fake's counter.
        // The delta is taken relative to a baseline captured AFTER every block this test creates already exists
        // (including the FifoEvict case's second tile), so only `t`'s own block's alive→disposed transition moves it.

        public enum DeferSite { CommitOverwrite, ReleaseActiveTrueEvict, ReleaseStaleCached, FifoEvict }

        [Test]
        public void DropSite_DefersWhileReferenced_FreesOnReleasePins([Values] DeferSite site)
        {
            var store = new SymbolTileStore(cacheCap: site == DeferSite.FifoEvict ? 1 : 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, new double3(1000, 0, 1000), 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            // For the stale-cached site, the tile must be cached AND departing before capture (CaptureSnapshot only
            // pins active + departing tiles) — reconcile to an empty loaded set with grace stamps it departing.
            if (site == DeferSite.ReleaseStaleCached)
                store.ReconcileActiveSet(new List<SymbolTileStore.Key>(), keepWarmOnRelease: true,
                    nowSeconds: 10.0, departingGraceSeconds: 1000.0);

            // Pin `t`'s block by holding a live snapshot referencing it (as an active slice, or a departing one above).
            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot); // pins t's block

            switch (site)
            {
                case DeferSite.CommitOverwrite:
                {
                    var t2Buffer = new SymbolTileBuffer();
                    Point(t2Buffer, default, 0, "t2", null, 2, t);
                    Commit(store, Key(t), store.BeginBuild(Key(t)), t2Buffer);
                    break;
                }
                case DeferSite.ReleaseActiveTrueEvict:
                    store.Release(Key(t), transferredToCache: false); // active → true evict
                    break;
                case DeferSite.ReleaseStaleCached:
                    store.Release(Key(t), transferredToCache: false); // stale cached copy → drop
                    break;
                case DeferSite.FifoEvict:
                {
                    var u = new TileId { Z = 5, X = 2, Y = 0 };
                    store.Release(Key(t), transferredToCache: true); // t → cached (cap 1)
                    var uBuffer = new SymbolTileBuffer();
                    Point(uBuffer, default, 0, "u", null, 3, u);
                    Commit(store, Key(u), store.BeginBuild(Key(u)), uBuffer);
                    store.Release(Key(u), transferredToCache: true); // over cap → evicts t (disposes its block)
                    break;
                }
            }

            // Baseline AFTER every block this test creates already exists — only t's block is expected to move.
            long live0 = SymbolTileBlock.DebugLiveAllocCount;
            Assert.AreEqual(live0, SymbolTileBlock.DebugLiveAllocCount,
                $"{site}: the drop site must DEFER — t's block is pinned by a live snapshot");
            store.ReleasePins(snapshot);
            Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                $"{site}: ReleasePins drops the pin to 0 → the deferred block frees exactly once");
            store.Clear(); // the CommitOverwrite/FifoEvict sites leave an unpinned live block (t2 / u) behind
        }

        // Genuinely discriminating: Clear() must RELEASE the entry's own reference, not dispose the block
        // outright. At Clear() time the block has exactly two references — the entry's and the snapshot's — so
        // a Clear() that disposes instead of releases frees the block while the snapshot still references it:
        // the exact restyle-mid-reconcile use-after-free this mechanism exists to prevent.
        // RED-verify: replace SymbolTileStore.cs Clear()'s `kv.Value.Block?.Release()` with
        // `kv.Value.Block?.Value.Dispose()` → the block frees at Clear() while the snapshot still references it
        // → the "must NOT dispose" assertion below fails.
        [Test]
        public void Clear_KeepsPinnedDeferredBlockAlive_FreedOnReleasePins()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);                 // pins t's block
            // Track THIS specific block — the global live count can't distinguish it surviving from any other
            // block this test creates, so key the tooth on this block's own array liveness.
            SymbolTileBlock pinnedBlock = snapshot.Slices[0].Block;

            store.Clear(); // teardown WITHOUT ReleasePins — releases the entry's own reference only
            Assert.IsTrue(pinnedBlock.Kinds.IsCreated,
                "Clear must NOT dispose a still-referenced block (self-safe against UAF)");

            store.ReleasePins(snapshot); // the snapshot leaves service → the last reference drops → frees once
            Assert.IsFalse(pinnedBlock.Kinds.IsCreated,
                "ReleasePins frees the (now-unreferenced) block exactly once — no leak");
        }

        // ═══ Cross-snapshot refcount OVERLAP — a block referenced by TWO snapshots survives one release ═══
        // DECLARED SURVIVOR: s1 and s2 are two distinct SymbolSnapshot objects, each holding one slice, so
        // any per-snapshot-dedupe injection is a no-op here. The only injection that isolates cross-snapshot
        // accumulation is "don't acquire/release at all", which also reds
        // ReleasePins_IsIdempotent_SecondReleaseDoesNotFreeUnderAnotherSnapshot,
        // CaptureSnapshot_ReleasesThePreviousCaptureReferences and every
        // DropSite_DefersWhileReferenced_FreesOnReleasePins case — not
        // discriminating. The property is Interlocked.Increment: accumulation across independent acquirers
        // is what the primitive IS, not a discipline this test can falsify in isolation. Kept for its assertions.

        [Test]
        public void Pin_RefcountsAcrossTwoSnapshots_SurvivesSingleRelease()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            var s1 = new SymbolSnapshot(); store.CaptureSnapshot(s1); // pin #1
            var s2 = new SymbolSnapshot(); store.CaptureSnapshot(s2); // pin #2 (count → 2)

            store.Release(Key(t), transferredToCache: false); // drop site → pinned (count 2) → deferred, not disposed
            long live0 = SymbolTileBlock.DebugLiveAllocCount;

            store.ReleasePins(s1);
            Assert.AreEqual(live0, SymbolTileBlock.DebugLiveAllocCount,
                "one release drops the count 2→1 — the block SURVIVES (still referenced)");
            store.ReleasePins(s2);
            Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                "the second release drops 1→0 → freed exactly once");
        }

        // ═══ Releasing the SAME snapshot twice must NOT free a block another live snapshot still
        //          references — the PickupCompletedReconcile → teardown shape in SymbolSubsystem (a demoted
        //          snapshot's pins released once on swap, then again at teardown). Needs a SECOND live
        //          reference (s2) or the tooth is vacuous:
        //          SymbolTileBlock's dispose is already idempotent, so a premature free with only one reference
        //          left is unobservable. ═══
        // RED-verify: move the release loop into SymbolTileStore.ReleasePins (reset the slices in
        // SymbolSnapshot.Clear() WITHOUT releasing Pin first) → the second ReleasePins(s1) decrements s1's slices
        // a second time, the block's refcount hits 0 under s2 → the first assert below fails.

        [Test]
        public void ReleasePins_IsIdempotent_SecondReleaseDoesNotFreeUnderAnotherSnapshot()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer); // refs = 1 (the store's own entry)

            var s1 = new SymbolSnapshot(); store.CaptureSnapshot(s1); // refs = 2
            var s2 = new SymbolSnapshot(); store.CaptureSnapshot(s2); // refs = 3
            // Read the block BEFORE any release — Clear() nulls the slice, so this must be captured first.
            SymbolTileBlock pinned = s1.Slices[0].Block;

            store.Release(Key(t), transferredToCache: false); // the store's own reference drops → refs 2, still alive
            store.ReleasePins(s1); // → refs 1 (s2 only)
            store.ReleasePins(s1); // repeated release of the SAME already-empty snapshot — must be a no-op
            Assert.IsTrue(pinned.Kinds.IsCreated,
                "a repeated ReleasePins on the same snapshot must not free a block s2 still references");
            store.ReleasePins(s2); // → refs 0
            Assert.IsFalse(pinned.Kinds.IsCreated, "the block frees exactly once, on s2's release");
        }

        // ═══ Re-capturing into a reused snapshot releases what it PREVIOUSLY captured, so a block dropped
        //          from the store between two captures is freed by the second capture rather than stranded. ═══
        // One assert: Assert.AreEqual(0, s.Count) would pass whether or not Clear() releases the pin
        // (Release(…, transferredToCache: false) already empties the store, so the re-capture yields Count == 0
        // either way) — riding a vacuous assert on a real one reads as coverage it is not.
        // RED-verify: drop the `Slices[i].Pin.Release()` call from SymbolSnapshot.Clear() → the re-capture
        // resets the slice without releasing its reference → the block's refcount never reaches 0 → this fails.

        [Test]
        public void CaptureSnapshot_ReleasesThePreviousCaptureReferences()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var t = new TileId { Z = 5, X = 1, Y = 0 };
            var buffer = new SymbolTileBuffer();
            Point(buffer, default, 0, "t", null, 1, t);
            Commit(store, Key(t), store.BeginBuild(Key(t)), buffer);

            var s = new SymbolSnapshot();
            store.CaptureSnapshot(s);
            store.Release(Key(t), transferredToCache: false); // store's own reference drops → refs 1 (s only), alive
            long live0 = SymbolTileBlock.DebugLiveAllocCount;

            store.CaptureSnapshot(s); // store is empty → s.Clear() releases the prior capture → refs 0 → freed
            Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                "the re-capture must release what the snapshot previously held");
        }

        // ═══ Cross-tile identity, no Frankenstein pair ════════════════
        // Tile A (coarser) holds the COMPLETE pair; tile B (finer, SAME quantized cell) holds ONLY its icon
        // (its text never resolved there). A rider has no DedupKey of its own, so
        // it can only ride with ITS OWN winning owner: since B's finer icon beats A's icon at the shared
        // (cell, layer, iconImage) key, A's rider is never emitted — not "text from A, icon from B".
        // Pre-fix (independent icon/text keys), this would show TWO tile keys — icon from B, text from A.

        [Test]
        public void CentredPair_CrossTile_NoFrankenstein_OrphanedRiderNeverSurvivesAloneAcrossTiles()
        {
            double3 cell = new double3(ParityQ * 500.0, 0, ParityQ * 500.0);
            var tileA = new TileId { Z = 13, X = 1000, Y = 800 };  // coarser — holds the complete pair
            var tileB = new TileId { Z = 14, X = 2000, Y = 1600 }; // finer — holds ONLY the icon

            var aBuffer = new SymbolTileBuffer();
            Point(aBuffer, cell, 0, null, "shield", 0, tileA, SymbolPairRole.Owner, pairId: 0);
            Point(aBuffer, cell, 0, "42", null, 1, tileA, SymbolPairRole.Rider, pairId: 0);
            var bBuffer = new SymbolTileBuffer();
            Point(bBuffer, cell, 0, null, "shield", 0, tileB); // unpaired lone icon, no text at B

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileA), store.BeginBuild(Key(tileA)), aBuffer);
            Commit(store, Key(tileB), store.BeginBuild(Key(tileB)), bBuffer);
            store.ReconcileActiveSet(new List<SymbolTileStore.Key> { Key(tileA), Key(tileB) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            Assert.AreEqual(1, result.BlockId.Count,
                "only the finer tile's icon survives — the coarser tile's rider (its owner lost the race) is never emitted alone");
            Assert.AreEqual(SymbolTileKey.Pack(tileB), result.OrderedBlocks[result.BlockId[0]].TileKey, "the survivor is tile B's block");
            Assert.AreEqual(0, result.LocalIndex[0], "bIcon is tile B's only (raw index 0) record");
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // ═══ No orphan rider, the INTRA-tile departing case (reconciler step (b)) ════════════════

        [Test]
        public void CentredPair_Departing_ClaimSkippedOwnerTakesRiderWithIt_NoOrphan()
        {
            double3 cell = new double3(ParityQ * 600.0, 0, ParityQ * 600.0);
            var tileActive = new TileId { Z = 13, X = 10, Y = 10 };
            var tileDeparting = new TileId { Z = 13, X = 20, Y = 20 }; // SAME quantized cell, different tile id

            var activeBuffer = new SymbolTileBuffer();
            Point(activeBuffer, cell, 0, null, "shield", 0, tileActive, SymbolPairRole.Owner, pairId: 0);
            Point(activeBuffer, cell, 0, "7", null, 1, tileActive, SymbolPairRole.Rider, pairId: 0);
            var depBuffer = new SymbolTileBuffer();
            Point(depBuffer, cell, 0, null, "shield", 0, tileDeparting, SymbolPairRole.Owner, pairId: 0);
            Point(depBuffer, cell, 0, "7", null, 1, tileDeparting, SymbolPairRole.Rider, pairId: 0);

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileActive), store.BeginBuild(Key(tileActive)), activeBuffer);
            Commit(store, Key(tileDeparting), store.BeginBuild(Key(tileDeparting)), depBuffer);
            // tileDeparting is never in the active set → it leaves cover, kept warm and departing.
            store.ReconcileActiveSet(new List<SymbolTileStore.Key> { Key(tileActive) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);
            Assert.AreEqual(1, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            long tileKeyDeparting = SymbolTileKey.Pack(tileDeparting);

            Assert.AreEqual(2, result.BlockId.Count, "exactly the active tile's 2 labels — the departing pair drops together");
            Assert.AreEqual(2, result.ActiveCount);
            for (int i = 0; i < result.BlockId.Count; i++)
                Assert.AreNotEqual(tileKeyDeparting, result.OrderedBlocks[result.BlockId[i]].TileKey,
                    "no record may carry the claim-skipped departing tile's key");

            // (ii) the general structural invariant over the WHOLE output: every Rider is immediately preceded
            // by its matching Owner (block-level PairRoles column — not Points[Detail], the hazard), sharing
            // the same block and MaterialIndex. PairId itself is not a baked block column (no reader — deleted
            // in 4.1); block+adjacency is the identity check the block-based winner plan can offer.
            for (int i = 0; i < result.BlockId.Count; i++)
            {
                SymbolTileBlock block = result.OrderedBlocks[result.BlockId[i]];
                int localIndex = result.LocalIndex[i];
                if (block.PairRoles[localIndex] != SymbolPairRole.Rider) continue;
                Assert.Greater(i, 0, "a Rider can never be the first output entry");
                SymbolTileBlock prevBlock = result.OrderedBlocks[result.BlockId[i - 1]];
                int prevLocalIndex = result.LocalIndex[i - 1];
                Assert.AreEqual(SymbolPairRole.Owner, prevBlock.PairRoles[prevLocalIndex], $"output[{i - 1}] must be the matching Owner");
                Assert.AreEqual(block.TileKey, prevBlock.TileKey);
                Assert.AreEqual(block.MaterialIndexes[localIndex], prevBlock.MaterialIndexes[prevLocalIndex]);
            }
            store.ReleasePins(snapshot);
            store.Clear();
        }

        // Guards against a naive "drop every departing rider" fix: with NO active competitor, the departing
        // pair alone must still survive intact — it never gets to claim-skip itself.
        [Test]
        public void CentredPair_DepartingAlone_NoActiveCompetitor_SurvivesIntact()
        {
            double3 cell = new double3(ParityQ * 700.0, 0, ParityQ * 700.0);
            var tileDeparting = new TileId { Z = 13, X = 30, Y = 30 };

            var depBuffer = new SymbolTileBuffer();
            Point(depBuffer, cell, 0, null, "shield", 0, tileDeparting, SymbolPairRole.Owner, pairId: 0);
            Point(depBuffer, cell, 0, "9", null, 1, tileDeparting, SymbolPairRole.Rider, pairId: 0);

            var store = new SymbolTileStore(cacheCap: 16);
            Commit(store, Key(tileDeparting), store.BeginBuild(Key(tileDeparting)), depBuffer);
            store.ReconcileActiveSet(new List<SymbolTileStore.Key>(),
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);
            Assert.AreEqual(0, store.ActiveTileCount);
            Assert.AreEqual(1, store.DepartingTileCount);

            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            Assert.AreEqual(2, result.BlockId.Count, "the departing pair, with no active competitor, must survive INTACT");
            SymbolTileBlock block = result.OrderedBlocks[result.BlockId[0]];
            Assert.AreEqual(SymbolPairRole.Owner, block.PairRoles[result.LocalIndex[0]]);
            Assert.AreEqual(SymbolPairRole.Rider, block.PairRoles[result.LocalIndex[1]]);
            Assert.AreEqual(result.BlockId[0], result.BlockId[1], "the pair shares one block");
            Assert.AreEqual(result.LocalIndex[0] + 1, result.LocalIndex[1], "the rider is the raw record immediately after its owner");
            store.ReleasePins(snapshot);
            store.Clear();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolSharedBufferTests — the symbol extractor's ring bucketing re-based onto source-layer ordinals
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The symbol extractor buckets rings by the source layer's own feature ordinals, not by the
    /// <b>selected</b> feature list.
    ///
    /// <para>It drives the production entry point (<see cref="SymbolFeatureExtractor.Extract"/>), so it
    /// measures the symbol sequence production emits.</para>
    ///
    /// <para><b>Why a separate fixture from <c>SymbolBufferParityTests</c>.</b> Its
    /// <c>SymbolPaths_FromTheSharedBuffer_MatchTheManagedDecodeOracle</c> states, in its own doc, that its
    /// arm B <i>transcribes</i> the production bucketing rather than calling it — a defect
    /// injected into <c>Extract</c>'s own <c>ringStart</c>/<c>ringOrder</c> leaves it green. This fixture's whole claim
    /// is about that bucketing, so it must run production and compare against an independent control, not
    /// against a re-implementation.</para>
    ///
    /// <para><b>The production configuration is the one under test</b> (the standing check). The symbol
    /// fixtures in this repo hand the extractor a layer whose features it selects in full, so ordinal == slot
    /// and the re-base is inert. Here the filter admits a <b>strict subset</b> (ordinals
    /// <c>[0, 2, 3, 5]</c> of six); the unselected features <b>carry paths</b>; a <c>Polygon</c> is
    /// <b>selected but undrawable</b>, sitting between selected features; the selected features are
    /// <b>multi-path</b>, so bucketing is load-bearing; and — the discriminator for the specific off-by-N the
    /// plan names — the <b>highest selected ordinal (5) exceeds the selected count (4)</b>, so a
    /// <c>ringStart</c> sized to the selected count cannot even address the last feature.</para>
    /// </summary>
    [TestFixture]
    public class SymbolSharedBufferTests
    {

        /// <summary>A synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this guard exists to catch.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        private const uint Extent = 4096;
        private static readonly TileId Tile = new TileId { Z = 1, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        private const string FilterJson = @"[""!="", ""cls"", ""skip""]";

        /// <summary>
        /// The symbols extracted from the SIX-feature source layer, with only four features selected, are
        /// the same sequence (count, <c>FeatureIndex</c>, text, anchor, placement, kind) as the symbols
        /// extracted from the four selected features alone as their own, unfiltered layer.
        ///
        /// <para><b>Catches:</b> sizing the counting sort's <c>ringStart</c> to the <i>selected</i> count
        /// instead of the layer's feature count (an <c>IndexOutOfRangeException</c> here, because ordinal 5
        /// is addressed — and a silent mis-bucket wherever it is not); driving the feature loop over
        /// selected-list positions while <c>RingFeatureIdx</c> names ordinals (paths land on the wrong
        /// features); and dropping or re-ordering the per-tile <c>FeatureIndex</c> counter, which is the
        /// stable placement tiebreak and therefore observable output.</para>
        /// </summary>
        [Test]
        public void SymbolSharedLayerBuffer_BucketsPathsByOrdinal_MatchingTheSelectedOnlyControl()
        {
            IReadOnlyList<IFeature> layerFeatures = SharedLayerFeatures();
            IReadOnlyList<SelectedTileFeature> selection = SelectFromLayer(layerFeatures);

            // ── Non-vacuity #1: strict, non-identity subset whose HIGHEST ordinal is out of range for any
            //    array sized to the selected count. That inequality is what makes the named off-by-N reachable.
            Assert.AreEqual(6, layerFeatures.Count, "precondition: the source layer carries six features");
            Assert.AreEqual(4, selection.Count,
                "precondition: the filter must admit a STRICT SUBSET of the layer");
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 5 }, Ordinals(selection),
                "precondition: the admitted ordinals must NOT be 0..n-1, or slot and ordinal coincide and " +
                "this fixture is blind to the re-base entirely");
            Assert.Greater(selection[selection.Count - 1].Ordinal, selection.Count - 1,
                "precondition: the highest selected ordinal must EXCEED the selected count — otherwise a " +
                "ringStart sized to the selected count still addresses every feature and the off-by-N the " +
                "plan names is unreachable here");

            // ── Non-vacuity #2: the rejected features really do carry paths, and an undrawable kind really
            //    is interleaved among the selected ones.
            Assert.AreEqual(TileGeometryType.Point, layerFeatures[1].GeometryType,
                "precondition: the unselected feature at ordinal 1 must be a drawable KIND carrying paths — " +
                "an unselected Polygon would be dropped by the kind gate anyway");
            Assert.AreEqual(TileGeometryType.Polygon, layerFeatures[2].GeometryType,
                "precondition: a Polygon must be SELECTED and interleaved, so the kind gate stays " +
                "independently load-bearing");

            List<SymbolStyle.SymbolFeature> shared  = Extract(layerFeatures, FilterJson);
            List<SymbolStyle.SymbolFeature> control = Extract(SelectedOnlyLayer(layerFeatures), null);

            // ── Non-vacuity #3: the control is a real, multi-feature, multi-path extraction. A one-symbol or
            //    single-text control could not tell a permuted attribution from a correct one.
            Assert.AreEqual(5, control.Count,
                "precondition: the control must emit 5 labels — 2 (multi-point 'a') + 1 (line-centre 'b') + " +
                "2 (multi-point 'c'); the selected Polygon emits none");
            Assert.AreEqual(3, DistinctTexts(control),
                "precondition: the control's labels must carry at least three DISTINCT texts, or a " +
                "mis-attributed path is indistinguishable from a correct one");

            Assert.AreEqual(control.Count, shared.Count,
                "the two unselected features' paths and the selected Polygon's rings must contribute EXACTLY " +
                "nothing — a differing label count means the selection gate or the kind gate is missing");

            for (int i = 0; i < control.Count; i++)
            {
                SymbolStyle.SymbolFeature e = control[i];
                SymbolStyle.SymbolFeature a = shared[i];
                Assert.AreEqual(e.Text, a.Text,
                    $"label {i} TEXT — a mismatch is a path bucketed onto the wrong feature");
                Assert.AreEqual(e.FeatureIndex, a.FeatureIndex,
                    $"label {i} FeatureIndex — the per-tile ordinal counter is the stable placement " +
                    "tiebreak, so its sequence is observable output, not an implementation detail");
                Assert.AreEqual(e.Placement, a.Placement, $"label {i} placement");
                Assert.AreEqual(e.Kind, a.Kind, $"label {i} kind");
                Assert.AreEqual(e.AnchorRender.x, a.AnchorRender.x, $"label {i} anchor x");
                Assert.AreEqual(e.AnchorRender.y, a.AnchorRender.y, $"label {i} anchor y");
                Assert.AreEqual(e.AnchorRender.z, a.AnchorRender.z, $"label {i} anchor z");
            }

            // The FeatureIndex sequence is pinned absolutely as well as differentially: a control that had
            // itself drifted would make the comparison above agree on a wrong answer.
            var indices = new List<int>();
            foreach (SymbolStyle.SymbolFeature symbol in shared) indices.Add(symbol.FeatureIndex);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, indices,
                "FeatureIndex counts emitted labels 0..n-1 in emission order, per tile — never the source " +
                "layer's feature ordinal, and never restarted per feature");
        }

        // ── Fixture ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The source layer, in decode order.</summary>
        private static IReadOnlyList<IFeature> SharedLayerFeatures() => new List<IFeature>
        {
            /* 0 */ Feature("a",    TileGeometryType.Point,
                        MvtCommandStream.Ring( 600,  600), MvtCommandStream.Ring(1200,  900)),
            /* 1 */ Feature("skip", TileGeometryType.Point,
                        MvtCommandStream.Ring(1800, 1100), MvtCommandStream.Ring(1900, 1200),
                        MvtCommandStream.Ring(2000, 1300)),
            /* 2 */ Feature("a",    TileGeometryType.Polygon,
                        MvtCommandStream.Ring(2400, 1400, 3000, 1400, 3000, 2000, 2400, 2000)),
            /* 3 */ Feature("b",    TileGeometryType.LineString,
                        MvtCommandStream.Ring( 500, 2200, 1300, 2200, 2100, 2600)),
            /* 4 */ Feature("skip", TileGeometryType.Point, MvtCommandStream.Ring(3100, 2800)),
            /* 5 */ Feature("c",    TileGeometryType.Point,
                        MvtCommandStream.Ring( 800, 3200), MvtCommandStream.Ring(1500, 3500)),
        };

        /// <summary>The four selected features alone, as their own layer — the control arm, where ordinal
        /// == slot (the configuration every pre-existing symbol fixture runs in).</summary>
        private static IReadOnlyList<IFeature> SelectedOnlyLayer(IReadOnlyList<IFeature> layerFeatures)
            => new List<IFeature> { layerFeatures[0], layerFeatures[2], layerFeatures[3], layerFeatures[5] };

        private static List<SymbolStyle.SymbolFeature> Extract(IReadOnlyList<IFeature> features, string filterJson)
        {
            var tile = TestDecodedTiles.Of("probe", Tile, features, Extent);
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                StyleLayer(filterJson), tile, Tile, Zoom, new WebMercatorProjection(), symbols);
            return symbols;
        }

        /// <summary>Runs the REAL selection seam, so the ordinals under test are production's.</summary>
        private static IReadOnlyList<SelectedTileFeature> SelectFromLayer(IReadOnlyList<IFeature> features)
        {
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(
                StyleLayer(FilterJson), TestDecodedTiles.Of("probe", Tile, features, Extent).GetLayer("probe"),
                Zoom, selected);
            return selected;
        }

        private static SymbolStyle.StyleLayer StyleLayer(string filterJson) => new SymbolStyle.StyleLayer
        {
            Id          = "p2-symbol",
            LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
            SourceLayer = "probe",
            Paint       = TestStyle.SymbolPaint(),
            Layout      = TestStyle.SymbolLayout(@"{""text-field"":""{cls}""}"),
            Filter      = filterJson != null ? JsonParser.Parse(filterJson) : null,
        };

        private static IFeature Feature(string cls, TileGeometryType kind, params IReadOnlyList<double2>[] rings)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["cls"] = Value.String(cls) },
                geometryType: kind,
                geometry: MvtCommandStream.Feature(rings));

        private static int[] Ordinals(IReadOnlyList<SelectedTileFeature> selection)
        {
            var ordinals = new int[selection.Count];
            for (int i = 0; i < selection.Count; i++) ordinals[i] = selection[i].Ordinal;
            return ordinals;
        }

        private static int DistinctTexts(IReadOnlyList<SymbolStyle.SymbolFeature> symbols)
        {
            var seen = new HashSet<string>();
            foreach (SymbolStyle.SymbolFeature symbol in symbols) seen.Add(symbol.Text);
            return seen.Count;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolSpriteReadinessTests — the sprite-atlas readiness race
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Road-shields (docs/road-shields-design.md) — the sprite-atlas readiness race. Without it, a
    /// symbol build kicked while the style's sprite fetch is still pending captures a null
    /// <c>_spriteAtlas</c> and commits icon-starved forever (no invalidation path exists). The gate makes
    /// <see cref="SymbolSubsystem.TryBeginBuild"/> PARK such a build instead — it commits NOTHING while
    /// gated, then commits WITH icons once the fetch settles, with no restyle/pan/zoom/re-kick.
    /// </summary>
    [TestFixture]
    public class SymbolSpriteReadinessTests
    {
        private const string SourceId = "s";
        private const string FontName = "LatinFont";
        private static readonly TileId Tile0 = new TileId { Z = 3, X = 0, Y = 0 };

        // Root `sprite` URL + a symbol layer with BOTH icon-image and text-field, over the fixture's
        // "centroids" source-layer (matches Assets/Fixtures/sample-tile.bytes, reused from
        // SymbolSubsystemPumpTests).
        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'sprite': 'https://example.invalid/sprite',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'], 'icon-image':'marker' } }
            ]
        }".Replace('\'', '"');

        private GameObject _camGo;
        private RenderTexture _rt;
        private SymbolSubsystem _subsystem;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;
        private string _spriteJson;
        private byte[] _spritePng;
        private int _tryBeginBuildCalls;
        // EVERY test in this fixture controls SpritesSettled's deadline clock explicitly, frozen here
        // rather than left on the real UnityEngine.Time.realtimeSinceStartup. The late- and absent-resolve
        // tests never advance it (they must never cross SpriteFetchDeadlineSeconds), so their "still gated"
        // assertions do not race real wall-clock time against the 8s deadline on a slow/loaded CI machine — a
        // deadline trip mid-test would otherwise fail looking exactly like a real regression. Only the
        // deadline tooth itself advances this field.
        private double _simulatedNow;

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SpriteReadiness_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));

            _subsystem = new SymbolSubsystem(mapCamera);
            _simulatedNow = 1000.0; // arbitrary non-zero start
            _subsystem.NowSecondsOverride = () => _simulatedNow;
            _tileBytes = ReadBytes("Fixtures", "sample-tile.bytes");
            _latinGlyphs = ReadBytes("Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            _spriteJson = File.ReadAllText(Path.Combine(Application.dataPath, "Fixtures", "sprites", "sample-sprite.json"));
            _spritePng = ReadBytes("Fixtures", "sprites", "sample-sprite.png");
            _tryBeginBuildCalls = 0;
        }

        [TearDown]
        public void TearDown()
        {
            _subsystem?.Dispose();
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        private static byte[] ReadBytes(params string[] rel)
        {
            var parts = new string[rel.Length + 1];
            parts[0] = Application.dataPath;
            Array.Copy(rel, 0, parts, 1, rel.Length);
            return File.ReadAllBytes(Path.Combine(parts));
        }

        private void SetGatedStyle(Func<System.Threading.CancellationToken, UniTask<SpriteResponse>> gatedFetch)
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            _subsystem.SpriteSourceFactoryOverride = _ => new GatedSpriteSource(gatedFetch);
            StyleDocument style = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));
        }

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        /// <summary>Mirrors SymbolSubsystemPumpTests.DriveTileBytesReady, but counts TryBeginBuild calls.
        /// NOTE (review nit): in THIS harness the `_tryBeginBuildCalls == 1` assertions below cannot actually
        /// fail — the counter only increments inside this method, which each test calls exactly once, and
        /// there is no TileManager here to re-kick. They read as a "no re-kick" tooth but assert nothing
        /// about the code under test; kept as executable documentation of the intent (no restyle/pan/zoom
        /// path exists between park and drain that WOULD re-kick), not as a falsifiable regression guard.</summary>
        private void DriveOnce(TileId tile)
        {
            _tryBeginBuildCalls++;
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, tile);
            if (pass == null) return;
            // The drive helper mirrors TileManager.KickMeshBuild: the tile is decoded ON THE POOL, the kick
            // owns the ONE reference the lease is born with, and its `finally` is the matching release —
            // which is what frees the decoded tile's buffers unless a parked build acquired its own.
            byte[] bytes = _tileBytes;
            UniTask.RunOnThreadPool(() =>
            {
                var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(tile, bytes));
                try { pass.RunWorkerAndHandoff(decode); }
                finally { decode.Release(); }
            }).Forget();
        }

        private static LoadedTileKey Key(TileId t) => new LoadedTileKey(SourceId, t);

        // This suite only ever commits Tile0, so it reads that tile's baked native block directly
        // (DebugBlockFor), which carries the per-point AtlasKind discriminator (Points[i].AtlasKind).
        private static readonly SymbolTileStore.Key Tile0Key = new SymbolTileStore.Key(SourceId, Tile0);

        private int SymbolCount() => Block()?.Kinds.Length ?? 0;

        private SymbolTileBlock Block() => _subsystem.Store().DebugBlockFor(Tile0Key);

        // ── the sprite atlas resolves late ─────────────────────────────────────────────────────────────
        [UnityTest]
        public IEnumerator SpriteAtlasResolvesLate_TileGainsIcons()
        {
            var gate = new UniTaskCompletionSource<SpriteResponse>();
            SetGatedStyle(_ => gate.Task);

            var loaded = new List<LoadedTileKey> { Key(Tile0) };
            DriveOnce(Tile0);

            // Gate held: pump many frames — the tile must NOT commit anything (no icon-starved commit).
            for (int f = 0; f < 60; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                yield return null;
            }
            Assert.AreEqual(0, SymbolCount(), "A build kicked before the sprite fetch settles must commit NOTHING while gated");
            Assert.AreEqual(1, _tryBeginBuildCalls, "TryBeginBuild must be called exactly once — no restyle, no re-kick");

            // Release the gate with real fixture sprite data — pump frames so the parked build drains.
            gate.TrySetResult(new SpriteResponse { Json = _spriteJson, Png = _spritePng, HasData = true });

            int committed = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                committed = SymbolCount();
                if (committed > 0) break;
                yield return null;
            }
            Assert.AreEqual(1, _tryBeginBuildCalls, "still exactly one TryBeginBuild call — no second kick, no pan/zoom/restyle");
            Assert.Greater(committed, 0, "the SAME tile's labels must eventually commit once the sprite settles");

            SymbolTileBlock block = Block();
            bool anyIcon = false, anyText = false;
            foreach (PointStageInput p in block.Points)
            {
                if (p.AtlasKind == SymbolKind.Icon) anyIcon = true;
                if (p.AtlasKind == SymbolKind.Text) anyText = true;
            }
            Assert.IsTrue(anyIcon, "the committed tile must now contain icon labels (the whole point of the gate)");
            Assert.IsTrue(anyText, "the committed tile must also contain its text labels");
        }

        // ── the sprite fetch resolves absent ───────────────────────────────────────────────────────────
        [UnityTest]
        public IEnumerator SpriteFetchResolvesAbsent_ParkedBuildStillCommitsText()
        {
            var gate = new UniTaskCompletionSource<SpriteResponse>();
            SetGatedStyle(_ => gate.Task);

            var loaded = new List<LoadedTileKey> { Key(Tile0) };
            DriveOnce(Tile0);

            for (int f = 0; f < 30; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                yield return null;
            }
            Assert.AreEqual(0, SymbolCount(), "still gated — nothing committed yet");

            // Resolve ABSENT (404/204) — the "settled ≠ non-null" guard: waiting on _spriteAtlas != null
            // would hang forever here; SpritesSettled must still flip on an absent sheet.
            gate.TrySetResult(SpriteResponse.Absent());

            int committed = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                committed = SymbolCount();
                if (committed > 0) break;
                yield return null;
            }
            Assert.Greater(committed, 0, "an absent sprite sheet must NOT stall the parked build forever — its text labels must still commit");

            SymbolTileBlock block = Block();
            bool anyText = false, anyIcon = false;
            foreach (PointStageInput p in block.Points)
            {
                if (p.AtlasKind == SymbolKind.Text) anyText = true;
                if (p.AtlasKind == SymbolKind.Icon) anyIcon = true;
            }
            Assert.IsTrue(anyText, "the parked build's text labels must commit despite the absent sheet");
            Assert.IsFalse(anyIcon, "no atlas ever resolved, so no icon can resolve either — text-only is the correct, inert outcome");
        }

        // ── the deadline bound on a genuinely hung fetch (docs/road-shields-design.md § "Decisions", D6) ──
        // Distinct from SpriteFetchResolvesAbsent_ParkedBuildStillCommitsText: its gate resolves (absent),
        // reaching a terminal Status quickly, so it never
        // exercises SpritesSettled's deadline branch at all. This tooth's gate is NEVER resolved — the
        // UniTaskCompletionSource's Task stays Pending for the whole test — simulating a sprite endpoint
        // that accepts the connection and never responds (UnityWebRequestSpriteSource sets no HTTP timeout).
        // Deterministic: NowSecondsOverride (frozen in SetUp, advanced only here) fast-forwards the clock
        // instantly, so this needs no real wait and doesn't depend on [UnityTest]'s frame-pump cadence to
        // cross an 8-second real bound.
        [UnityTest]
        public IEnumerator SpriteFetchNeverResolves_DeadlineDispatchesTextOnly_QueueDrains()
        {
            var gate = new UniTaskCompletionSource<SpriteResponse>(); // never completed
            SetGatedStyle(_ => gate.Task);

            var loaded = new List<LoadedTileKey> { Key(Tile0) };
            DriveOnce(Tile0);

            // Well before the deadline: still parked, nothing committed, one entry queued. Pumped until the
            // entry APPEARS rather than for a fixed ten frames — the kick decodes the tile on the pool before
            // it can park, so the enqueue lands a little later than it used to. The clock is frozen, so extra
            // frames cannot cross the deadline and cannot weaken either assertion below.
            for (int f = 0; f < 300 && _subsystem.PendingSpriteCount() == 0; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                yield return null;
            }
            Assert.AreEqual(0, SymbolCount(), "before the deadline elapses, a hung fetch must still park (no icon-starved commit)");
            Assert.AreEqual(1, _subsystem.PendingSpriteCount(), "the build must be sitting in the pending queue while parked");

            // Cross the deadline WITHOUT the gate ever resolving — the fetch is still Pending.
            _simulatedNow += SymbolSubsystem.SpriteFetchDeadlineSeconds + 1.0;

            int committed = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                committed = SymbolCount();
                if (committed > 0) break;
                yield return null;
            }
            Assert.Greater(committed, 0,
                "once the deadline elapses, a permanently-hung fetch must fall back to dispatching with " +
                "whatever atlas state exists (null -> text-only) — never park forever");
            Assert.AreEqual(0, _subsystem.PendingSpriteCount(),
                "the pending queue must actually DRAIN once the deadline trips, not merely let one build " +
                "through while the backlog keeps growing");

            SymbolTileBlock block = Block();
            bool anyText = false, anyIcon = false;
            foreach (PointStageInput p in block.Points)
            {
                if (p.AtlasKind == SymbolKind.Text) anyText = true;
                if (p.AtlasKind == SymbolKind.Icon) anyIcon = true;
            }
            Assert.IsTrue(anyText, "the deadline fallback must still commit the tile's text labels");
            Assert.IsFalse(anyIcon, "the sprite atlas never resolved (the fetch is still Pending), so no icon can resolve either");
        }
    }
}
