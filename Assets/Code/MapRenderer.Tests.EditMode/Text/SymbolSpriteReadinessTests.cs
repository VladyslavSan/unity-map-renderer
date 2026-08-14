// Unity EditMode only — needs a real Camera/Texture2D + the internal SymbolLabelSubsystem, and drives the
// gated sprite fetch through a real UniTask suspend/resume. NOT registered in core-tests.csproj.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// D6 (road-shields, docs/road-shields-design.md §3 D6) — the sprite-atlas readiness race. Before D6, a
    /// symbol build kicked while the style's sprite fetch is still pending captures a null
    /// <c>_spriteAtlas</c> and commits icon-starved forever (no invalidation path exists). D6 makes
    /// <see cref="SymbolLabelSubsystem.TryBeginBuild"/> PARK such a build instead — it commits NOTHING while
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
        // SymbolLabelSubsystemPumpTests).
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
        private SymbolLabelSubsystem _subsystem;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;
        private string _spriteJson;
        private byte[] _spritePng;
        private int _tryBeginBuildCalls;
        // Review fix: EVERY test in this fixture now controls SpritesSettled's deadline clock explicitly,
        // frozen here rather than left on the real UnityEngine.Time.realtimeSinceStartup. T10/T11 never
        // advance it (they must never cross SpriteFetchDeadlineSeconds), so their "still gated" assertions
        // no longer race real wall-clock time against the 8s deadline on a slow/loaded CI machine — a
        // deadline trip mid-test would otherwise fail looking exactly like a real D6 regression. Only the
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

            _subsystem = new SymbolLabelSubsystem(mapCamera);
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

        /// <summary>Mirrors SymbolLabelSubsystemPumpTests.DriveTileBytesReady, but counts TryBeginBuild calls.
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

        private int LabelCount()
        {
            var labels = new List<LabelInstance>();
            _subsystem.CollectInto(labels);
            return labels.Count;
        }

        private List<LabelInstance> Labels()
        {
            var labels = new List<LabelInstance>();
            _subsystem.CollectInto(labels);
            return labels;
        }

        // ── T10 ────────────────────────────────────────────────────────────────────────────────────────
        [UnityTest]
        public IEnumerator SpriteAtlasResolvesLate_TileGainsIcons()
        {
            var gate = new UniTaskCompletionSource<SpriteResponse>();
            SetGatedStyle(_ => gate.Task);

            var loaded = new List<LoadedTileKey> { Key(Tile0) };
            DriveOnce(Tile0);

            // Gate held: pump many frames — the tile must NOT commit anything (D6: no icon-starved commit).
            for (int f = 0; f < 60; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                yield return null;
            }
            Assert.AreEqual(0, LabelCount(), "D6: a build kicked before the sprite fetch settles must commit NOTHING while gated");
            Assert.AreEqual(1, _tryBeginBuildCalls, "TryBeginBuild must be called exactly once — no restyle, no re-kick");

            // Release the gate with real fixture sprite data — pump frames so the parked build drains.
            gate.TrySetResult(new SpriteResponse { Json = _spriteJson, Png = _spritePng, HasData = true });

            int committed = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                committed = LabelCount();
                if (committed > 0) break;
                yield return null;
            }
            Assert.AreEqual(1, _tryBeginBuildCalls, "still exactly one TryBeginBuild call — no second kick, no pan/zoom/restyle");
            Assert.Greater(committed, 0, "the SAME tile's labels must eventually commit once the sprite settles");

            List<LabelInstance> labels = Labels();
            bool anyIcon = false, anyText = false;
            foreach (LabelInstance l in labels)
            {
                if (l.Kind == LabelKind.Icon) anyIcon = true;
                if (l.Kind == LabelKind.Text) anyText = true;
            }
            Assert.IsTrue(anyIcon, "the committed tile must now contain icon labels (the whole point of D6)");
            Assert.IsTrue(anyText, "the committed tile must also contain its text labels");
        }

        // ── T11 ────────────────────────────────────────────────────────────────────────────────────────
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
            Assert.AreEqual(0, LabelCount(), "still gated — nothing committed yet");

            // Resolve ABSENT (404/204) — the "settled ≠ non-null" guard: waiting on _spriteAtlas != null
            // would hang forever here; SpritesSettled must still flip on an absent sheet.
            gate.TrySetResult(SpriteResponse.Absent());

            int committed = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                committed = LabelCount();
                if (committed > 0) break;
                yield return null;
            }
            Assert.Greater(committed, 0, "an absent sprite sheet must NOT stall the parked build forever — its text labels must still commit");

            List<LabelInstance> labels = Labels();
            bool anyText = false, anyIcon = false;
            foreach (LabelInstance l in labels)
            {
                if (l.Kind == LabelKind.Text) anyText = true;
                if (l.Kind == LabelKind.Icon) anyIcon = true;
            }
            Assert.IsTrue(anyText, "the parked build's text labels must commit despite the absent sheet");
            Assert.IsFalse(anyIcon, "no atlas ever resolved, so no icon can resolve either — text-only is the correct, inert outcome");
        }

        // ── T16 (docs/road-shields-design.md §5) — D6 review follow-up (REQUIRED 1): the deadline bound on
        //    a genuinely hung fetch ────────────────────────────────────────────────────────────────────
        // Distinct from T11: T11's gate resolves (absent), reaching a terminal Status quickly, so it never
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
            Assert.AreEqual(0, LabelCount(), "before the deadline elapses, a hung fetch must still park (no icon-starved commit)");
            Assert.AreEqual(1, _subsystem.PendingSpriteCount(), "the build must be sitting in the pending queue while parked");

            // Cross the deadline WITHOUT the gate ever resolving — the fetch is still Pending.
            _simulatedNow += SymbolLabelSubsystem.SpriteFetchDeadlineSeconds + 1.0;

            int committed = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                committed = LabelCount();
                if (committed > 0) break;
                yield return null;
            }
            Assert.Greater(committed, 0,
                "once the deadline elapses, a permanently-hung fetch must fall back to dispatching with " +
                "whatever atlas state exists (null -> text-only) — never park forever");
            Assert.AreEqual(0, _subsystem.PendingSpriteCount(),
                "the pending queue must actually DRAIN once the deadline trips, not merely let one build " +
                "through while the backlog keeps growing");

            List<LabelInstance> labels = Labels();
            bool anyText = false, anyIcon = false;
            foreach (LabelInstance l in labels)
            {
                if (l.Kind == LabelKind.Text) anyText = true;
                if (l.Kind == LabelKind.Icon) anyIcon = true;
            }
            Assert.IsTrue(anyText, "the deadline fallback must still commit the tile's text labels");
            Assert.IsFalse(anyIcon, "the sprite atlas never resolved (the fetch is still Pending), so no icon can resolve either");
        }
    }
}
