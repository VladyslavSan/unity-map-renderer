// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolLabelSubsystem, and drives
// the glyph atlas Texture2D upload. NOT registered in core-tests.csproj.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests; // TestGlyphSource
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Epic A / A3 acceptance tooth #3 (docs/per-layer-tile-processing-a3-plan.md §F — the PRIMARY semantic
    /// tooth): the differential — production <see cref="SymbolLabelSubsystem"/> output, driven through the
    /// A3 processor machinery, deep-equals a single-pass <see cref="StyledSymbolTileBuilder.BuildAsync"/>
    /// ORACLE fed the same bytes/style/camera-zoom/projection over a SECOND, independent builder/glyph
    /// pipeline. THREE symbol layers — one on a different source (so the "s"-source layers' GLOBAL indices
    /// {1,2} diverge from their within-build ordinals {0,1}, §F-3(ii)) and two "s"-source layers with a
    /// ZOOM-INTERPOLATED text-size (so the tile's integer zoom vs the captured camera zoom, §F-3(i), yield
    /// observably different sizes) — so the per-layer processor split, material-index stamping, and
    /// cross-layer label order are all exercised, and EACH §F-3 falsifier is a genuine (non-coincidental)
    /// divergence, not just a hypothetical one. See <c>SetUp</c>'s comment for the layout.
    ///
    /// <para>RED-verified against the un-rewired (pre-A3) subsystem for the STRUCTURAL delegation teeth
    /// (see the A3 stage report). Empirically, THIS differential passes unmodified against a structurally
    /// faithful pre-A3 <c>BuildTileAsync</c> too — pre-A3 already calls
    /// <c>ExtractLayers</c>/<c>ShapeAsync</c> with the same zoom/projection/global-material-indices the
    /// oracle uses, so it computes IDENTICAL values. Its proven role (RED-verified by injecting each §F-3
    /// falsifier into a scratch copy of the post-A3 <c>BuildTileAsync</c> — see the A3 stage report) is a
    /// WRONG-A3-REWIRE falsifier, not a pre/post-A3 discriminator.</para>
    ///
    /// <para>Falsifiers this tooth catches (§F-3): (i) <c>ctx.Zoom</c> fed the tile's integer zoom instead of
    /// the captured camera zoom; (ii) material indices remapped to per-build ordinals instead of global
    /// <c>layerIndices</c>; (iii) per-layer split reordering or a dropped/collapsed layer; (iv) the main
    /// tail skipped entirely (zero labels).</para>
    /// </summary>
    [TestFixture]
    public class SymbolProcessorParityTests
    {
        private const string FontName = "LatinFont";
        private const string SourceId = "s";
        private static readonly TileId Tile = new TileId { Z = 3, X = 0, Y = 0 };

        // THREE symbol layers, declared in an order that makes both falsifiers this differential exists to
        // catch actually falsifiable (dev-side strengthening after the advisor flagged the original
        // two-layer/constant-text-size style as hollow for exactly the two plumbing bugs §F-3(i)/(ii) name):
        //  - "labels-other" (a DIFFERENT source, "other") occupies GLOBAL index 0, so the two "s"-source
        //    layers get GLOBAL indices {1, 2} while their WITHIN-BUILD ordinals (k in BuildTileAsync's loop)
        //    are {0, 1} — global-index ≠ ordinal, so a material-index-remap bug (§F-3(ii): stamping `k`
        //    instead of the global `layerIndices[k]`) produces an observably different MaterialIndex.
        //  - "labels-a"/"labels-b" (source "s", over the fixture's "centroids") use a ZOOM-INTERPOLATED
        //    text-size, so evaluating at the tile's INTEGER zoom (3) instead of the captured CAMERA zoom
        //    (5.0, §F-3(i)) yields an observably different TextSizePx/glyph-quad geometry, not an
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
        private SymbolLabelSubsystem _subsystem;
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

            _subsystem = new SymbolLabelSubsystem(_mapCamera);
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

        /// <summary>A5b drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
        /// thread, then <c>RunWorkerAndHandoff</c> fire-and-forget on the pool. Replaces the retired
        /// <c>OnTileBytesReady</c> push.</summary>
        private void DriveTileBytesReady(TileId tile)
        {
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, tile);
            if (pass == null) return; // mirrors OnTileBytesReady's no-op guard (no _builder / no layers for source)
            UniTask.RunOnThreadPool(() => pass.RunWorkerAndHandoff(new SharedTileDecode(_tileBytes, new MvtTileDecoder()))).Forget();
        }

        /// <summary>Drives the REAL production subsystem to a committed label set — same bytes, same
        /// style, same glyph fixture as the oracle below. Only "s"-source bytes are pushed — "labels-other"
        /// (a different source) is never built; it exists solely to make the "s" layers' GLOBAL indices
        /// {1,2} diverge from their within-build ordinals {0,1} (§F-3(ii) falsifier).</summary>
        private IEnumerator DriveProductionBuild(List<LabelInstance> output)
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
                output.Clear();
                _subsystem.CollectInto(output);
                if (output.Count > 0) yield break;
                yield return null;
            }
            Assert.Fail("production build did not commit labels within 200 pumped frames");
        }

        /// <summary>Builds the ORACLE label set: the pre-A3-shaped single pass
        /// (<see cref="StyledSymbolTileBuilder.BuildAsync"/>) over a SECOND, independent glyph pipeline fed
        /// the SAME ranges — atlas state is equivalent but independent, so this is not self-referential with
        /// the processor machinery A3 changes (only <c>ExtractLayers</c>/<c>ShapeAsync</c>, which A3 does not
        /// modify, are shared).</summary>
        private List<LabelInstance> BuildOracle()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            // Match SymbolLabelSubsystem.SetStyle's atlas dimension EXACTLY (its AtlasDimension = 4096,
            // clamped to the GPU max) — a different atlas size packs glyphs at different cells, so their
            // normalized UVs would differ from production for a reason that has NOTHING to do with A3
            // (a test-harness artifact, not a real divergence).
            int dim = math.min(SystemInfo.maxTextureSize, 4096);
            using var oracleGlyphManager = new GlyphManager(TestGlyphSource.FromRanges(ranges), new GlyphAtlas(dim, dim));
            var oracleBuilder = new StyledSymbolTileBuilder(oracleGlyphManager);

            MvtTile mvt = MvtDecoder.Decode(_tileBytes);
            double zoom = _mapCamera.CurrentProperties.Zoom; // same captured camera zoom the production build uses
            var projection = _mapCamera.Projection;

            var oracle = new List<LabelInstance>();
            // BuildAsync completes synchronously here: TestGlyphSource resolves via UniTask.FromResult and
            // neither BuildAsync/ExtractLayers/ShapeAsync forces a thread hop — no real async suspension.
            // _sSourceLayers/_sSourceGlobalIndices mirror exactly what BuildTileAsync passes for the "s"
            // build: the "s"-source layers in declared order, stamped with their GLOBAL indices {1,2}.
            oracleBuilder.BuildAsync(mvt, Tile, _sSourceLayers, zoom, projection, oracle, _sSourceGlobalIndices)
                .GetAwaiter().GetResult();
            return oracle;
        }

        [UnityTest]
        public IEnumerator ProductionBuild_MatchesSinglePassOracle_LabelForLabel()
        {
            var production = new List<LabelInstance>();
            yield return DriveProductionBuild(production);

            List<LabelInstance> oracle = BuildOracle();

            Assert.Greater(oracle.Count, 0, "sanity: the fixture + the 's'-source layers must yield labels at all");
            Assert.AreEqual(oracle.Count, production.Count, "candidate COUNT must match the oracle");

            for (int i = 0; i < oracle.Count; i++)
                AssertLabelsEqual(oracle[i], production[i], i);
        }

        [UnityTest]
        public IEnumerator ProductionBatch_MatchesOracleBatch_RecordAndQuadContent()
        {
            var production = new List<LabelInstance>();
            yield return DriveProductionBuild(production);
            List<LabelInstance> oracle = BuildOracle();

            int slotCount = _allStyleSymbolLayers.Count; // mirrors production CurrentBatch's _allSymbolLayers.Count
            var oracleBatch = new SymbolLabelBatch();
            var productionBatch = new SymbolLabelBatch();
            SymbolLabelBatchBuilder.Build(oracleBatch, oracle, slotCount, _mapCamera.Projection);
            SymbolLabelBatchBuilder.Build(productionBatch, production, slotCount, _mapCamera.Projection);

            Assert.AreEqual(oracleBatch.Count, productionBatch.Count, "record count");
            CollectionAssert.AreEqual(Trim(oracleBatch.Kinds, oracleBatch.Count), Trim(productionBatch.Kinds, productionBatch.Count), "Kinds");
            CollectionAssert.AreEqual(Trim(oracleBatch.Detail, oracleBatch.Count), Trim(productionBatch.Detail, productionBatch.Count), "Detail");

            Assert.AreEqual(oracleBatch.PointCount, productionBatch.PointCount, "point count");
            CollectionAssert.AreEqual(Trim(oracleBatch.PointQuadStart, oracleBatch.PointCount), Trim(productionBatch.PointQuadStart, productionBatch.PointCount), "PointQuadStart");
            CollectionAssert.AreEqual(Trim(oracleBatch.PointQuadCount, oracleBatch.PointCount), Trim(productionBatch.PointQuadCount, productionBatch.PointCount), "PointQuadCount");
            CollectionAssert.AreEqual(Trim(oracleBatch.Points, oracleBatch.PointCount), Trim(productionBatch.Points, productionBatch.PointCount), "Points (stable stage input fields)");

            Assert.AreEqual(oracleBatch.CurvedCount, productionBatch.CurvedCount, "curved count");
            CollectionAssert.AreEqual(Trim(oracleBatch.Curveds, oracleBatch.CurvedCount), Trim(productionBatch.Curveds, productionBatch.CurvedCount), "Curveds");

            Assert.AreEqual(oracleBatch.QuadCount, productionBatch.QuadCount, "quad count");
            CollectionAssert.AreEqual(Trim(oracleBatch.Quads, oracleBatch.QuadCount), Trim(productionBatch.Quads, productionBatch.QuadCount), "Quads (glyph geometry)");
        }

        /// <summary>Epic A / A3 acceptance tooth #4 (§F — "the tail actually runs" tooth): a glyph-fetch
        /// delegate reachable ONLY from <see cref="StyledSymbolTileBuilder.ShapeAsync"/> (the tail) records
        /// the thread it runs on; together with the existing, unmodified
        /// <c>SymbolDecodeAndExtract_RunOffTheMainThread</c> recorder (which pins the WORKER half off main),
        /// this proves the phase split runs on the right threads AND in the right order — labels only commit
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
            var labels = new List<LabelInstance>();
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                labels.Clear();
                _subsystem.CollectInto(labels);
                if (labels.Count > 0) break;
                yield return null;
            }

            Assert.Greater(labels.Count, 0, "sanity: the build actually committed labels (the tail ran)");
            Assert.Greater(observedThreadIds.Count, 0, "sanity: the glyph-fetch delegate — reachable only from the tail — fired at all");
            foreach (int id in observedThreadIds)
                Assert.AreEqual(mainThreadId, id, "the glyph-fetch delegate must run on the MAIN thread (the tail), never the worker pool");
        }

        private static T[] Trim<T>(T[] array, int count)
        {
            var result = new T[count];
            System.Array.Copy(array, result, count);
            return result;
        }

        private static void AssertLabelsEqual(LabelInstance expected, LabelInstance actual, int index)
        {
            string at = $" at index {index}";
            Assert.AreEqual(expected.AnchorRender, actual.AnchorRender, "AnchorRender" + at);
            Assert.AreEqual(expected.Placement, actual.Placement, "Placement" + at);
            AssertLayoutEqual(expected.Layout, actual.Layout, at);
            CollectionAssert.AreEqual(expected.PathRender, actual.PathRender, "PathRender" + at);
            CollectionAssert.AreEqual(expected.LineAnchors, actual.LineAnchors, "LineAnchors" + at);
            AssertCurvedGlyphsEqual(expected.CurvedGlyphs, actual.CurvedGlyphs, at);
            Assert.AreEqual(expected.Text, actual.Text, "Text" + at);
            Assert.AreEqual(expected.Paint, actual.Paint, "Paint" + at);
            Assert.AreEqual(expected.TextSizePx, actual.TextSizePx, "TextSizePx" + at);
            Assert.AreEqual(expected.PaddingPx, actual.PaddingPx, "PaddingPx" + at);
            Assert.AreEqual(expected.SortKey, actual.SortKey, "SortKey" + at);
            Assert.AreEqual(expected.MaxAngleDeg, actual.MaxAngleDeg, "MaxAngleDeg" + at);
            Assert.AreEqual(expected.KeepUpright, actual.KeepUpright, "KeepUpright" + at);
            Assert.AreEqual(expected.FeatureIndex, actual.FeatureIndex, "FeatureIndex" + at);
            Assert.AreEqual(expected.TileKey, actual.TileKey, "TileKey" + at);
            Assert.AreEqual(expected.MaterialIndex, actual.MaterialIndex, "MaterialIndex" + at);
            Assert.AreEqual(expected.AllowOverlap, actual.AllowOverlap, "AllowOverlap" + at);
            Assert.AreEqual(expected.IgnorePlacement, actual.IgnorePlacement, "IgnorePlacement" + at);
            Assert.AreEqual(expected.TranslatePx, actual.TranslatePx, "TranslatePx" + at);
            Assert.AreEqual(expected.TranslateAnchor, actual.TranslateAnchor, "TranslateAnchor" + at);
            Assert.AreEqual(expected.RotationAlignment, actual.RotationAlignment, "RotationAlignment" + at);
        }

        private static void AssertLayoutEqual(TextLayoutResult expected, TextLayoutResult actual, string at)
        {
            if (expected == null || actual == null)
            {
                Assert.AreEqual(expected == null, actual == null, "Layout null-ness" + at);
                return;
            }
            Assert.AreEqual(expected.BoundsMin, actual.BoundsMin, "Layout.BoundsMin" + at);
            Assert.AreEqual(expected.BoundsMax, actual.BoundsMax, "Layout.BoundsMax" + at);
            Assert.AreEqual(expected.LineCount, actual.LineCount, "Layout.LineCount" + at);
            CollectionAssert.AreEqual(
                new List<SymbolQuad>(expected.Quads ?? System.Array.Empty<SymbolQuad>()),
                new List<SymbolQuad>(actual.Quads ?? System.Array.Empty<SymbolQuad>()),
                "Layout.Quads" + at);
        }

        private static void AssertCurvedGlyphsEqual(IReadOnlyList<CurvedGlyph> expected, IReadOnlyList<CurvedGlyph> actual, string at)
        {
            if (expected == null || actual == null)
            {
                Assert.AreEqual(expected == null, actual == null, "CurvedGlyphs null-ness" + at);
                return;
            }
            CollectionAssert.AreEqual(new List<CurvedGlyph>(expected), new List<CurvedGlyph>(actual), "CurvedGlyphs" + at);
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
}
