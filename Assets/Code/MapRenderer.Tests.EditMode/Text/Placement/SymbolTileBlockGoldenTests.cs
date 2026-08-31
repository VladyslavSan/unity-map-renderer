// Unity EditMode only — SymbolTileBlock/Baker need Unity.Collections' NativeArray, and the produced-
// fixture test drives a real Camera/Material/Shader through StyledSymbolTileBuilder.BuildAsync. NOT
// registered in core-tests.csproj (see BlockColumnHash's header for why this whole area stays EditMode-only).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests; // TestGlyphSource
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Symbol-symbol perf Phase 1 / Stage 1 (design §4, §5 B; <c>step4.4-plan.md</c> §4.4a): the
    /// block-byte-identity golden — pins <see cref="SymbolTileBlockBaker.Bake"/>'s output shape so a
    /// later refactor of the bake path (4.4b sheds the resident graph, 4.4c retypes <c>Shape</c> onto
    /// reused buffer) cannot silently change what gets baked, only how it gets there.
    ///
    /// <para><b>Two fixtures, two golden strategies.</b> <see cref="Bake_HandBuiltHazardFixture_MatchesExplicitGolden"/>
    /// hand-builds a <see cref="SymbolTileBuffer"/> (via <see cref="TestSymbolTileBuffer"/>) that deliberately
    /// exercises every §4.4a hazard (see its own doc) and asserts EXPLICIT, hand-derivable expected values per
    /// column — a real oracle, not a value
    /// pasted from a first run (<c>handed-down-formula-is-never-re-derived</c>). But because its input never
    /// passes through <c>StyledSymbolTileBuilder.Shape</c>, it CANNOT catch a 4.4c regression in that
    /// tail's own emit sites (the four record-append calls and the quad-layout
    /// migration) — only <see cref="Bake_ProducedFixture_MatchesCommittedGoldenHash"/> (built the same way
    /// <c>SymbolProcessorParityTests.BuildOracle</c> is) exercises that path, so BOTH fixtures are required,
    /// not a convenience duplication of one.</para>
    /// </summary>
    [TestFixture]
    public class SymbolTileBlockGoldenTests
    {
        // ── field-count guard: BlockColumnHash's per-struct hashers are hand-written, one line per field —
        // a field silently added to one of these structs without a matching hash/compare line would hash
        // FEWER fields than the type has and never notice. Pin the count so that drift fails the gate instead.
        [Test]
        public void HashedStructs_FieldCounts_MatchHasherCoverage()
        {
            Assert.AreEqual(26, PublicFieldCount<PointStageInput>(),
                "PointStageInput gained/lost a field — update BlockColumnHash.HashPointStageInput/AssertPointStageInputEqual to match, THEN update this count");
            Assert.AreEqual(18, PublicFieldCount<CurvedStageInput>(),
                "CurvedStageInput gained/lost a field — update BlockColumnHash.HashCurvedStageInput/AssertCurvedStageInputEqual to match, THEN update this count");
            Assert.AreEqual(6, PublicPropertyCount<SymbolQuad>(),
                "SymbolQuad gained/lost a property — update BlockColumnHash.HashSymbolQuad/AssertSymbolQuadEqual to match, THEN update this count");
            Assert.AreEqual(3, PublicPropertyCount<CurvedGlyph>(),
                "CurvedGlyph gained/lost a property — update BlockColumnHash.HashCurvedGlyph/AssertCurvedGlyphEqual to match, THEN update this count");
            Assert.AreEqual(2, PublicFieldCount<LineAnchor>(),
                "LineAnchor gained/lost a field — update BlockColumnHash.HashLineAnchor/AssertLineAnchorEqual to match, THEN update this count");
        }

        private static int PublicFieldCount<T>() => typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance).Length;
        private static int PublicPropertyCount<T>() => typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length;

        // ── the hand-built hazard fixture ──
        // Raw list (8 dense slots), designed so EVERY §4.4a hazard is present and unambiguous:
        //   i0 PointA (layer 0)              — normal point, 1 quad
        //   i1 PointB (layer 0, LAST)        — normal point, 2 quads
        //   i2 CurvedC (layer 1, first)      — curved record
        //   i3 PointD (layer 1)              — a POINT WITH ZERO QUADS
        //   i4 DanglingOwner (layer 1)       — proposed Owner whose "rider" (i5) has the WRONG role — SymbolPairing
        //                                       must DISSOLVE this back to None (the half-built-pair fence)
        //   i5 PlainF (layer 2, first)       — plain point (also i4's would-be rider — wrong role, no PairId set)
        //   i6 PairOwner (layer 2)           — resolved pair owner (icon)
        //   i7 PairRider (layer 2, LAST)     — resolved pair rider (text); the pair is the TAIL of layer 2's
        //                                       span — i.e. layer 2 emits nothing after this pair (the "owner
        //                                       is the last record of its layer" hazard).
        private const long TileKeyValue = 777L;
        private static readonly double3 TileOrigin = new double3(1000.0, 0.0, 2000.0);
        private static readonly double3 UpRenderConst = new double3(0.0, 1.0, 0.0);

        private static SymbolQuad MakeQuad(float offsetX) => new SymbolQuad
        {
            TopLeft = new float2(-8f + offsetX, 8f), BottomRight = new float2(8f + offsetX, -8f),
            UvTopLeft = new float2(0f, 0f), UvBottomRight = new float2(0.5f, 0.5f), LineIndex = 0,
        };

        private static List<SymbolQuad> MakeQuads(int count)
        {
            var quads = new List<SymbolQuad>();
            for (int i = 0; i < count; i++) quads.Add(MakeQuad(i));
            return quads;
        }

        // Migration bridge note (TestSymbolTileBuffer migration): AddPointSlot/AddCurvedSlot mirror the field set
        // MakePoint/MakeCurved used to carry on a hand-built per-symbol managed carrier, now appended straight into
        // a SymbolTileBuffer via TestSymbolTileBuffer — see that type's default-value contract doc for why every
        // omitted parameter below is byte-identical to the carrier it replaces.
        private static void AddPointSlot(SymbolTileBuffer buffer,
            int featureIndex, int materialIndex, string text, string icon, int quadCount,
            SymbolPairRole pairRole = SymbolPairRole.None, int pairId = 0)
        {
            TestSymbolTileBuffer.AddPoint(buffer,
                anchorRender: new double3(1000.0 + featureIndex * 10, 0.0, 2000.0 + featureIndex * 5),
                quads: MakeQuads(quadCount),
                boundsMin: new float2(-8f, -8f), boundsMax: new float2(8f, 8f),
                text: text, up: UpRenderConst, iconImage: icon,
                materialIndex: materialIndex, textSizePx: 16f, paddingPx: 2f, sortKey: featureIndex,
                featureIndex: featureIndex, tileKey: TileKeyValue,
                pairRole: pairRole, pairId: pairId, paint: SymbolPaint.Default);
        }

        private static void AddCurvedSlot(SymbolTileBuffer buffer, int featureIndex, int materialIndex, string text)
        {
            TestSymbolTileBuffer.AddCurved(buffer,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 4f, Cell = MakeQuad(0), CellSkirt = 0f },
                    new CurvedGlyph { ArcCenter = 8f, Cell = MakeQuad(1), CellSkirt = 0f },
                },
                anchors: new[] { new LineAnchor(0, 0.5f) },
                path: new[] { new double3(0, 0, 0), new double3(10, 0, 0), new double3(20, 0, 0) },
                pathUp: new[] { UpRenderConst, UpRenderConst, UpRenderConst },
                anchorRender: new double3(1000.0, 0.0, 2000.0),
                text: text, materialIndex: materialIndex, textSizePx: 16f, paddingPx: 2f, sortKey: 1f,
                maxAngleDeg: 45f, keepUpright: true,
                featureIndex: featureIndex, tileKey: TileKeyValue, paint: SymbolPaint.Default);
        }

        private static SymbolTileBuffer BuildHazardFixture()
        {
            var buffer = new SymbolTileBuffer();
            AddPointSlot(buffer, 0, 0, "Alpha", null, 1);                                  // i0
            AddPointSlot(buffer, 1, 0, "Beta", null, 2);                                   // i1 last of layer 0
            AddCurvedSlot(buffer, 0, 1, "Curved");                                         // i2 curved, first of layer 1
            AddPointSlot(buffer, 1, 1, "Empty", null, 0);                                  // i3 zero quads
            AddPointSlot(buffer, 2, 1, "DanglingOwner", null, 1, SymbolPairRole.Owner, 7);   // i4 dangling owner
            AddPointSlot(buffer, 0, 2, "PlainF", null, 1);                                  // i5 layer 2 first; wrong-role "rider"
            AddPointSlot(buffer, 1, 2, "PairOwner", "shield", 1, SymbolPairRole.Owner, 42);  // i6 resolved pair owner
            AddPointSlot(buffer, 2, 2, "PairRider", null, 1, SymbolPairRole.Rider, 42);      // i7 resolved pair rider, LAST
            return buffer;
        }

        private static List<SymbolQuad> QuadsOf(SymbolTileBuffer buffer, int i) =>
            buffer.Quads.GetRange(buffer.Symbols[i].QuadStart, buffer.Symbols[i].QuadCount);

        [Test]
        public void Bake_HandBuiltHazardFixture_MatchesExplicitGolden()
        {
            SymbolTileBuffer buffer = BuildHazardFixture();
            var stringTable = new SymbolStringTable();
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, 4, in TileOrigin, stringTable);
            try
            {
                // ── raw-order columns (Length == 9, including the null slot) ──
                Assert.AreEqual(8, block.Kinds.Length, "raw list length");
                CollectionAssert.AreEqual(
                    new[] { SymbolPlacementKind.Point, SymbolPlacementKind.Point, SymbolPlacementKind.Curved,
                        SymbolPlacementKind.Point, SymbolPlacementKind.Point, SymbolPlacementKind.Point, SymbolPlacementKind.Point, SymbolPlacementKind.Point },
                    ToArray(block.Kinds), "Kinds");
                CollectionAssert.AreEqual(new[] { 0, 1, 0, 2, 3, 4, 5, 6 }, ToArray(block.Detail), "Detail");
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 5, 6, 7, 8, 9 }, ToArray(block.WorldStart), "WorldStart");
                CollectionAssert.AreEqual(new[] { 1, 1, 3, 1, 1, 1, 1, 1 }, ToArray(block.WorldCount), "WorldCount");
                CollectionAssert.AreEqual(new[] { 0, 0, 1, 1, 1, 2, 2, 2 }, ToArray(block.MaterialIndexes), "MaterialIndexes");
                CollectionAssert.AreEqual(
                    new[] { SymbolPairRole.None, SymbolPairRole.None, SymbolPairRole.None, SymbolPairRole.None,
                        SymbolPairRole.None, SymbolPairRole.None, SymbolPairRole.Owner, SymbolPairRole.Rider },
                    ToArray(block.PairRoles), "PairRoles — the dangling owner (i4) DISSOLVES to None; the resolved pair (i6/i7) alone survives");

                var expectedRepAnchor = new[]
                {
                    buffer.Symbols[0].AnchorRender, buffer.Symbols[1].AnchorRender,
                    buffer.Path[buffer.Symbols[2].PathStart + 1],
                    buffer.Symbols[3].AnchorRender, buffer.Symbols[4].AnchorRender, buffer.Symbols[5].AnchorRender,
                    buffer.Symbols[6].AnchorRender, buffer.Symbols[7].AnchorRender,
                };
                CollectionAssert.AreEqual(expectedRepAnchor, ToArray(block.RepAnchor), "RepAnchor (curved: path[pathLen/2] = path[1])");

                // Bake order: Text before IconImage, per symbol, raw index order (SymbolStringTable.cs's own contract).
                CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 9 }, ToArray(block.TextIds), "TextIds");
                CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 0, 0, 8, 0 }, ToArray(block.IconImageIds), "IconImageIds ('shield' on i6 takes id 8, between i6's Text=7 and i7's Text=9)");

                // ── point pool (7 slots: every point record — every raw slot except i2 the curved) ──
                Assert.AreEqual(7, block.Points.Length);
                CollectionAssert.AreEqual(new[] { 0, 1, 3, 3, 4, 5, 6 }, ToArray(block.PointQuadStart), "PointQuadStart");
                CollectionAssert.AreEqual(new[] { 1, 2, 0, 1, 1, 1, 1 }, ToArray(block.PointQuadCount), "PointQuadCount — slot2 (i3) is the ZERO-QUAD hazard");

                var expectedPoints = new[]
                {
                    // `buffer.Symbols[i]` is a List indexer (returns by value, not by ref), so an explicit `in`
                    // is CS8156 — passing it plain binds to the `in` parameter via a compiler temp instead.
                    SymbolTileBlockBaker.BuildPointInput(buffer.Symbols[0], 4, TileOrigin, SymbolPairRole.None),
                    SymbolTileBlockBaker.BuildPointInput(buffer.Symbols[1], 4, TileOrigin, SymbolPairRole.None),
                    SymbolTileBlockBaker.BuildPointInput(buffer.Symbols[3], 4, TileOrigin, SymbolPairRole.None),
                    SymbolTileBlockBaker.BuildPointInput(buffer.Symbols[4], 4, TileOrigin, SymbolPairRole.None), // dissolved
                    SymbolTileBlockBaker.BuildPointInput(buffer.Symbols[5], 4, TileOrigin, SymbolPairRole.None),
                    SymbolTileBlockBaker.BuildPointInput(buffer.Symbols[6], 4, TileOrigin, SymbolPairRole.Owner),
                    SymbolTileBlockBaker.BuildPointInput(buffer.Symbols[7], 4, TileOrigin, SymbolPairRole.Rider),
                };
                for (int i = 0; i < expectedPoints.Length; i++)
                    Assert.AreEqual(expectedPoints[i], block.Points[i], $"Points[{i}]");

                // ── curved pool (1 slot: i2) ──
                Assert.AreEqual(1, block.Curveds.Length);
                CollectionAssert.AreEqual(new[] { 0 }, ToArray(block.CurvedGlyphStart), "CurvedGlyphStart");
                CollectionAssert.AreEqual(new[] { 2 }, ToArray(block.CurvedGlyphCount), "CurvedGlyphCount");
                CollectionAssert.AreEqual(new[] { 0 }, ToArray(block.CurvedAnchorStart), "CurvedAnchorStart");
                CollectionAssert.AreEqual(new[] { 1 }, ToArray(block.CurvedAnchorCount), "CurvedAnchorCount");
                CollectionAssert.AreEqual(new[] { 0 }, ToArray(block.CurvedAnchorFadeStart), "CurvedAnchorFadeStart");
                Assert.AreEqual(SymbolTileBlockBaker.BuildCurvedInput(buffer.Symbols[2], 4, TileOrigin), block.Curveds[0], "Curveds[0]");

                // ── flat pools — expected values read straight off the SOURCE symbols, in the same raw-index
                // order Fill walks (a point contributes its Quads, a curved its glyphs/anchors/path) — an
                // INDEPENDENT restatement of the input, not a re-derivation of Fill's math. ──
                var expectedQuads = new List<SymbolQuad>();
                expectedQuads.AddRange(QuadsOf(buffer, 0));
                expectedQuads.AddRange(QuadsOf(buffer, 1));
                expectedQuads.AddRange(QuadsOf(buffer, 4));
                expectedQuads.AddRange(QuadsOf(buffer, 5));
                expectedQuads.AddRange(QuadsOf(buffer, 6));
                expectedQuads.AddRange(QuadsOf(buffer, 7));
                Assert.AreEqual(7, block.Quads.Length, "labels[3] (i3) contributes ZERO quads — the hazard");
                CollectionAssert.AreEqual(expectedQuads, ToArray(block.Quads), "Quads");

                ShapedSymbol curvedSymbol = buffer.Symbols[2];
                CollectionAssert.AreEqual(
                    buffer.Glyphs.GetRange(curvedSymbol.GlyphStart, curvedSymbol.GlyphCount), ToArray(block.Glyphs), "Glyphs");
                CollectionAssert.AreEqual(
                    buffer.Anchors.GetRange(curvedSymbol.AnchorStart, curvedSymbol.AnchorCount), ToArray(block.Anchors), "Anchors");

                var expectedWorldPoints = new List<double3> { buffer.Symbols[0].AnchorRender, buffer.Symbols[1].AnchorRender };
                expectedWorldPoints.AddRange(buffer.Path.GetRange(curvedSymbol.PathStart, curvedSymbol.PathCount));
                expectedWorldPoints.Add(buffer.Symbols[3].AnchorRender);
                expectedWorldPoints.Add(buffer.Symbols[4].AnchorRender);
                expectedWorldPoints.Add(buffer.Symbols[5].AnchorRender);
                expectedWorldPoints.Add(buffer.Symbols[6].AnchorRender);
                expectedWorldPoints.Add(buffer.Symbols[7].AnchorRender);
                Assert.AreEqual(10, block.WorldPoints.Length);
                CollectionAssert.AreEqual(expectedWorldPoints, ToArray(block.WorldPoints), "WorldPoints");

                float3 up = SymbolTileBlockBaker.NarrowUp(UpRenderConst);
                var expectedWorldUps = new[] { up, up, up, up, up, up, up, up, up, up }; // every symbol here shares UpRenderConst
                CollectionAssert.AreEqual(expectedWorldUps, ToArray(block.WorldUps), "WorldUps");

                Assert.AreEqual(2, block.AnchorFadeIds.Length, "1 anchor + 1 trailing fallback");
                Assert.AreEqual(SymbolStagingMath.LineFadeId(TileKeyValue, 1, 0, 0), block.AnchorFadeIds[0], "AnchorFadeIds[0] (the one real anchor)");
                Assert.AreEqual(SymbolStagingMath.LineFadeId(TileKeyValue, 1, 0, -1), block.AnchorFadeIds[1], "AnchorFadeIds[1] (trailing centred fallback)");

                // ── staging upper bounds: every point record (i0,i1,i3,i4,i5,i6,i7 — 7) contributes exactly 1
                // box/candidate regardless of its quad count (i3's zero-quad hazard still counts), plus curved's
                // (anchor+fallback = 2 placements) * 2 glyphs. ──
                Assert.AreEqual(11, block.MaxBoxes, "7 point boxes + 2 placements * 2 curved glyphs");
                Assert.AreEqual(11, block.MaxQuads, "1+2+0+1+1+1+1 (=7) point quads + 2 placements * 2 curved glyphs");
                Assert.AreEqual(9, block.MaxCandidates, "7 point candidates (i3's zero-quad record still counts) + 2 curved placements");
                Assert.AreEqual(TileKeyValue, block.TileKey);
            }
            finally { block.Dispose(); }
        }

        /// <summary>Bake determinism: baking the SAME hazard fixture twice (fresh, independent
        /// <see cref="SymbolStringTable"/> tables) must yield COLUMN-EQUAL blocks — the guard's second half,
        /// exercising <see cref="BlockColumnHash.AssertColumnsEqual"/> itself (including its TextIds/
        /// IconImageIds equivalence-class check) against real baked data before 4.4b leans on it for the
        /// prod-vs-oracle block compare.</summary>
        [Test]
        public void Bake_SameHazardFixtureTwice_ProducesColumnEqualBlocks()
        {
            SymbolTileBlock blockA = SymbolTileBlockBaker.Bake(
                BuildHazardFixture(), 4, in TileOrigin, new SymbolStringTable());
            SymbolTileBlock blockB = SymbolTileBlockBaker.Bake(
                BuildHazardFixture(), 4, in TileOrigin, new SymbolStringTable());
            try
            {
                BlockColumnHash.AssertColumnsEqual(blockA, blockB);
                Assert.AreEqual(BlockColumnHash.Hash(blockA).ToString(), BlockColumnHash.Hash(blockB).ToString(),
                    "two independent bakes of the identical input must digest identically (TextIds/IconImageIds excluded by design)");
            }
            finally { blockA.Dispose(); blockB.Dispose(); }
        }

        private static T[] ToArray<T>(NativeArray<T> array) where T : struct => array.ToArray();

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // ── the PRODUCED fixture: an independent StyledSymbolTileBuilder.BuildAsync, as
        // SymbolProcessorParityTests.BuildOracle does — the ONLY fixture that exercises Shape's own
        // emit sites (see the type doc). Cannot hand-derive glyph UVs / zoom-interpolated TextSizePx, so
        // this pins a HASH, not explicit values — see CapturedGoldenHash's doc for how to fill it in.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        private const string FontName = "LatinFont";
        private static readonly TileId Tile = new TileId { Z = 3, X = 0, Y = 0 };

        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels-a', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'] } },
                { 'id':'labels-b', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':20, 'text-font':['LatinFont'] } }
            ]
        }".Replace('\'', '"');

        private GameObject _camGo;
        private RenderTexture _rt;
        private MapCamera _mapCamera;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SymbolGolden_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            _mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));

            _tileBytes = LoadUp("Assets", "Fixtures", "sample-tile.bytes");
            _latinGlyphs = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
        }

        [TearDown]
        public void TearDown()
        {
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        /// <summary>Mirrors <c>SymbolProcessorParityTests.BuildOracle</c> exactly (same fixture bytes, same
        /// glyph fixture, same single-pass <c>BuildAsync</c>) but with the atlas dimension pinned to a HARD
        /// 4096 rather than <c>math.min(SystemInfo.maxTextureSize, 4096)</c> — the committed golden hash
        /// must not move on a machine whose GPU reports a smaller max texture size, so a small-GPU CI runner
        /// gets a clear assertion message instead of a mystery hash mismatch.</summary>
        private SymbolTileBuffer BuildProducedFixture()
        {
            Assert.GreaterOrEqual(SystemInfo.maxTextureSize, 4096,
                "this fixture pins the glyph atlas at a hard 4096 — a smaller GPU max would silently repack glyphs and move the golden hash for a reason that has nothing to do with the bake");

            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            using var glyphManager = new GlyphManager(TestGlyphSource.FromRanges(ranges), new GlyphAtlas(4096, 4096));
            var builder = new StyledSymbolTileBuilder(glyphManager);

            using MvtTile mvt = MvtDecoder.Decode(Tile, _tileBytes);
            var style = StyleParser.Parse(StyleJson);
            var symbolLayers = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) symbolLayers.Add(symbol);
            var globalIndices = new List<int>();
            for (int i = 0; i < symbolLayers.Count; i++) globalIndices.Add(i);

            var output = new SymbolTileBuffer();
            builder.BuildAsync(mvt, Tile, symbolLayers, _mapCamera.CurrentProperties.Zoom, _mapCamera.Projection, output, globalIndices)
                .GetAwaiter().GetResult();
            return output;
        }

        /// <summary>Committed golden — EMPTY until the orchestrator's first gate run. This fixture's exact
        /// column digests cannot be hand-derived (glyph UVs, zoom-interpolated TextSizePx, projected
        /// doubles) — so the assertion below deliberately compares against a sentinel that CANNOT match,
        /// and prints <c>ColumnHashes.ToString</c>'s full per-column dump in the
        /// failure message. Paste that dump here (replacing the sentinel) to make this a real, committed,
        /// fixed-oracle golden. Once pasted, DO NOT re-bake a snapshot to go green on a later divergence —
        /// a moved digest means behaviour changed; find and fix the cause instead
        /// (`snapshot-moved-suspect-the-fixture` / `never re-bake a snapshot to go green`).</summary>
        private const string CapturedGoldenHash =
            "Kinds=-13491713 Detail=1233695479 WorldStart=1233695479 WorldCount=-1252581121 RepAnchor=515797947 " +
            "MaterialIndexes=-2021596801 PairRoles=-13491713 Points=1107115275 " +
            "PointQuadStart=-1852567463 PointQuadCount=648814877 Curveds=527 CurvedGlyphStart=527 " +
            "CurvedGlyphCount=527 CurvedAnchorStart=527 CurvedAnchorCount=527 CurvedAnchorFadeStart=527 " +
            "Quads=-1354314655 Glyphs=527 Anchors=527 WorldPoints=515797947 WorldUps=2133991935 " +
            "AnchorFadeIds=527 MaxBoxes=496 MaxQuads=4242 MaxCandidates=496 TileKey=12288";

        [Test]
        public void Bake_ProducedFixture_MatchesCommittedGoldenHash()
        {
            SymbolTileBuffer oracle = BuildProducedFixture();
            Assert.Greater(oracle.Symbols.Count, 0, "sanity: the fixture + style must yield labels at all");

            SymbolTileBlock block = SymbolTileBlockBaker.Bake(oracle, 2, in double3.zero, new SymbolStringTable());
            try
            {
                BlockColumnHash.ColumnHashes actual = BlockColumnHash.Hash(block);
                Assert.AreEqual(CapturedGoldenHash, actual.ToString(),
                    "golden not yet captured — paste the ACTUAL digest above (printed here) into CapturedGoldenHash, " +
                    "then re-run to confirm it goes green. A later failure here (after capture) means the bake's " +
                    "OUTPUT changed — do not re-bake the golden to silence it; find the cause.");
            }
            finally { block.Dispose(); }
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
}
