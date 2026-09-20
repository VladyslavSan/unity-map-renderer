// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolSubsystem, and drives
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
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests; // TestGlyphSource
using MapRenderer.Tests.Text.Placement; // BlockColumnHash
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Epic A / A3 acceptance tooth #3 (the PRIMARY semantic
    /// tooth): the differential — production <see cref="SymbolSubsystem"/> output, driven through the
    /// A3 processor machinery, deep-equals a single-pass <see cref="StyledSymbolTileBuilder.BuildAsync"/>
    /// ORACLE fed the same bytes/style/camera-zoom/projection over a SECOND, independent builder/glyph
    /// pipeline. THREE symbol layers — one on a different source (so the "s"-source layers' GLOBAL indices
    /// {1,2} diverge from their within-build ordinals {0,1}, §F-3(ii)) and two "s"-source layers with a
    /// ZOOM-INTERPOLATED text-size (so the tile's integer zoom vs the captured camera zoom, §F-3(i), yield
    /// observably different sizes) — so the per-layer processor split, material-index stamping, and
    /// cross-layer symbol order are all exercised, and EACH §F-3 falsifier is a genuine (non-coincidental)
    /// divergence, not just a hypothetical one. See <c>SetUp</c>'s comment for the layout.
    ///
    /// <para>RED-verified against the un-rewired (pre-A3) subsystem for the STRUCTURAL delegation teeth
    /// (see the A3 stage report). Empirically, THIS differential passes unmodified against a structurally
    /// faithful pre-A3 <c>BuildTileAsync</c> too — pre-A3 already calls
    /// <c>ExtractLayers</c>/<c>Shape</c> with the same zoom/projection/global-material-indices the
    /// oracle uses, so it computes IDENTICAL values. Its proven role (RED-verified by injecting each §F-3
    /// falsifier into a scratch copy of the post-A3 <c>BuildTileAsync</c> — see the A3 stage report) is a
    /// WRONG-A3-REWIRE falsifier, not a pre/post-A3 discriminator.</para>
    ///
    /// <para>Falsifiers this tooth catches (§F-3): (i) <c>ctx.Zoom</c> fed the tile's integer zoom instead of
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
        // two-layer/constant-text-size style as hollow for exactly the two plumbing bugs §F-3(i)/(ii) name):
        //  - "symbols-other" (a DIFFERENT source, "other") occupies GLOBAL index 0, so the two "s"-source
        //    layers get GLOBAL indices {1, 2} while their WITHIN-BUILD ordinals (k in BuildTileAsync's loop)
        //    are {0, 1} — global-index ≠ ordinal, so a material-index-remap bug (§F-3(ii): stamping `k`
        //    instead of the global `layerIndices[k]`) produces an observably different MaterialIndex.
        //  - "symbols-a"/"symbols-b" (source "s", over the fixture's "centroids") use a ZOOM-INTERPOLATED
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

        /// <summary>A5b drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
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

        // Reader cutover (4.2) / resident-graph shed (4.4b): the retired subsystem.CollectInto managed-list overload
        // deduped ACROSS tiles — this fixture only ever commits ONE tile (SourceId/Tile), so the replacement reads
        // that tile's baked native block directly (DebugBlockFor — Entry.SymbolPlacementSystem itself is gone as of 4.4b) rather
        // than routing through the cross-tile winner plan the deep block-vs-block compare below needs.
        private static readonly SymbolTileStore.Key ProductionKey = new SymbolTileStore.Key(SourceId, Tile);

        /// <summary>Drives the REAL production subsystem until it commits a baked block — same bytes, same
        /// style, same glyph fixture as the oracle below. Only "s"-source bytes are pushed — "symbols-other"
        /// (a different source) is never built; it exists solely to make the "s" layers' GLOBAL indices
        /// {1,2} diverge from their within-build ordinals {0,1} (§F-3(ii) falsifier).</summary>
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

        /// <summary>Builds the ORACLE symbol set: the pre-A3-shaped single pass
        /// (<see cref="StyledSymbolTileBuilder.BuildAsync"/>) over a SECOND, independent glyph pipeline fed
        /// the SAME ranges — atlas state is equivalent but independent, so this is not self-referential with
        /// the processor machinery A3 changes (only <c>ExtractLayers</c>/<c>Shape</c>, which A3 does not
        /// modify, are shared).
        /// <para><b>That independence premise EXPIRED at IR stage B4</b>, which modifies exactly
        /// <c>ExtractLayers</c>/<c>Shape</c>. This fixture is therefore no longer independent of the
        /// symbol geometry path, and must not be cited as the oracle for a change to it —
        /// <c>SymbolBufferParityTests</c> is that oracle. Recorded here rather than only in the newer file
        /// because a stale independence claim left where a reader finds it is precisely how this epic
        /// disarmed a structural tooth once already.</para></summary>
        private SymbolTileBuffer BuildOracle()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            // Match SymbolSubsystem.SetStyle's atlas dimension EXACTLY (its AtlasDimension = 4096,
            // clamped to the GPU max) — a different atlas size packs glyphs at different cells, so their
            // normalized UVs would differ from production for a reason that has NOTHING to do with A3
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

        /// <summary>Epic A / A3 acceptance tooth #4 (§F — "the tail actually runs" tooth): a glyph-fetch
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
}
