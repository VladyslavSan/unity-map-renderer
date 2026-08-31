// Unity EditMode only — needs a real Camera/Texture2D + the internal SymbolSubsystem, and drives the
// gated sprite fetch through a real UniTask suspend/resume. NOT registered in core-tests.csproj.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
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
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests;
using MapRenderer.Tests.Text.Placement; // BlockColumnHash
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Tooth <b>D2</b> — the sprite-PARKED path end-to-end: the one production site where the decode model's
    /// hardest promise is kept or broken.
    ///
    /// <para><b>What this tooth used to prove, and what it proves now.</b> Under the scoped lease a parked
    /// build was dispatched long after its kick's scope had closed and freed the tile, so it RE-decoded —
    /// one extra decode, accepted deliberately, and measured here. Under the reference count the park takes
    /// its own reference while the kick's is still live, so the tile survives the whole
    /// <c>SetStyle</c>→<c>SpritesSettled</c> window and the drain reads the SAME decoded tile. Two
    /// assertions therefore INVERT — the decode count 2 → 1, and the kick's tile disposed-while-parked
    /// 1 → 0 — and the inversion is the proof the new model works, not a regression. The symbol differential
    /// they exist to protect is unchanged. <b>The fixture keeps its name</b> — the epic docs and the stage
    /// briefs cite it — but "Redecode" is now historical: it names the cost this tooth measured, and then
    /// measured the deletion of.</para>
    ///
    /// <para><b>The differential.</b> Two arms over the same bytes, style, camera and sprite fixture: one
    /// whose sprite fetch is gated open at kick time (parks) and one whose fetch has already settled (never
    /// parks). Their committed baked blocks must be column-equal (<c>BlockColumnHash.AssertColumnsEqual</c>,
    /// 4.4b). A parked build that
    /// produced subtly different geometry — a stale extent, a mis-stamped TileId — moves the symbols; nothing
    /// else in the suite would see it. (A buffer read after FREE surfaces differently: production catches the
    /// throw in <c>RunSymbolWorkerAndHandoff</c> and logs a warning, so the observable is a commit of ZERO
    /// symbols, caught by the count assertion rather than the differential. Both review arms confirmed that
    /// empirically.)</para>
    ///
    /// <para><b>Vacuity guards, both directions.</b> The park is reachable only through
    /// <c>TryBeginBuild</c> while <c>SpritesSettled</c> is false, so a fixture whose sprites happen to be
    /// settled would silently compare un-parked against un-parked and assert nothing. The parked arm
    /// therefore asserts the queue actually received an entry, and that the kick's tile is <b>still alive</b>
    /// while the build sits parked — which is what makes the later read a share rather than a
    /// use-after-free. The oracle arm's mirror guard had to be REPLACED rather than renumbered: it was
    /// "the oracle decoded once" against the parked arm's two, and under the reference count BOTH are one,
    /// so the old form stops discriminating and would go quietly vacuous. Its first replacement — a
    /// disposal-count differential sampled after the pump — did not discriminate either: a forced park on
    /// the settled path enqueues, drains on the very next pump, commits, and leaves that count reading
    /// exactly what "never parked" reads. The guard now reads the pass's OWN park decision at
    /// <c>TryBeginBuild</c> completion, before a frame is pumped, which is the only moment nothing
    /// downstream can undo.</para>
    ///
    /// <para><b>And the parked reference's PRE-CONSUMER exits.</b> Three further teeth at the bottom of the
    /// fixture drive the ways a parked entry can stop being reachable by the mouth meant to consume it — a
    /// restyle whose drain lands between the park's token check and its enqueue, a cancellation with no
    /// drain, and a fault between the dequeue and the worker starting. Each was previously either declared
    /// unreachable or unobserved; each frees an <c>Allocator.Persistent</c> tile or leaks it.</para>
    ///
    /// <para><b>And the drain runs OFF the main thread.</b> The decode can no longer say so — there is only
    /// one, and it is the kick's — so the instrument moved to the EXTRACT: the probe records the thread of
    /// every layer read, and a layer is read only inside a worker pass. Replacing <c>PumpBuilds</c>'
    /// <c>RunOnThreadPool</c> with a direct call would move the whole extract onto the frame thread; the
    /// sibling parity tooth pins the <i>tail</i> on main and cannot see that.</para>
    /// </summary>
    [TestFixture]
    public class SymbolParkedRedecodeTests
    {
        private const string SourceId = "s";
        private const string FontName = "LatinFont";
        private static readonly TileId Tile0 = new TileId { Z = 3, X = 0, Y = 0 };

        // Same shape as SymbolSpriteReadinessTests' style: a root `sprite` URL (so a fetch exists to gate at
        // all) and one symbol layer carrying BOTH icon-image and text-field over the fixture's "centroids"
        // source-layer — icons are what parking exists to wait for, so a text-only style would park for no
        // observable difference.
        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'sprite': 'https://example.invalid/sprite',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'], 'icon-image':'marker' } }
            ]
        }".Replace('\'', '"');

        // The style a restyle lands on in the post-drain-race tooth: no symbol layers and no `glyphs` URL,
        // so SetStyle leaves `_builder` null and PumpBuilds returns at its very first line, FOREVER. That is
        // not an exotic choice — it is what makes the stranded-entry leak permanent rather than merely late,
        // and it is the case the race's fix has to cover.
        private static readonly string NoGlyphStyleJson = @"{ 'version': 8, 'layers': [] }".Replace('\'', '"');

        private GameObject _camGo;
        private RenderTexture _rt;
        private MapCamera _mapCamera;
        private SymbolSubsystem _parkedSubsystem;
        private SymbolSubsystem _oracleSubsystem;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;
        private string _spriteJson;
        private byte[] _spritePng;
        // SpritesSettled's deadline clock, frozen and NEVER advanced (mirrors SymbolSpriteReadinessTests'
        // review fix). The parked arm holds its gate open across 60 pumped frames; on the real
        // realtimeSinceStartup a slow batch machine can cross SpriteFetchDeadlineSeconds inside that window,
        // drain the queue, and fail the "it actually parked" precondition looking exactly like a real
        // regression. Frozen, the only thing that can settle the fetch is the gate this test controls.
        private double _simulatedNow;
        // Production catches a failed symbol build and logs it rather than propagating, on whichever thread
        // the build ran — hence the gate. Only the build-failure message is collected; ambient engine
        // warnings are not this tooth's business.
        private readonly object _logGate = new object();
        private readonly List<string> _buildFailureLogs = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("ParkedRedecode_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            _mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));

            lock (_logGate) _buildFailureLogs.Clear();
            Application.logMessageReceivedThreaded += OnLogMessage;
            _simulatedNow = 1000.0; // arbitrary non-zero start; never advanced by this fixture
            _tileBytes   = ReadBytes("Fixtures", "sample-tile.bytes");
            _latinGlyphs = ReadBytes("Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
            _spriteJson  = File.ReadAllText(Path.Combine(Application.dataPath, "Fixtures", "sprites", "sample-sprite.json"));
            _spritePng   = ReadBytes("Fixtures", "sprites", "sample-sprite.png");
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceivedThreaded -= OnLogMessage;
            _parkedSubsystem?.Dispose();
            _oracleSubsystem?.Dispose();
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        /// <summary>Collects only <c>SymbolSubsystem</c>'s swallowed build-failure warning — the log line
        /// a use-after-free in the parked drain would produce. Fires on the pool thread, hence the gate.</summary>
        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Warning && type != LogType.Error && type != LogType.Exception) return;
            if (condition == null || condition.IndexOf("label build failed", StringComparison.Ordinal) < 0) return;
            lock (_logGate) _buildFailureLogs.Add(condition);
        }

        private static byte[] ReadBytes(params string[] rel)
        {
            var parts = new string[rel.Length + 1];
            parts[0] = Application.dataPath;
            Array.Copy(rel, 0, parts, 1, rel.Length);
            return File.ReadAllBytes(Path.Combine(parts));
        }

        // The lease probe lives in TestSupport/LeaseProbeDecoder.cs — shared with EagerDecodeOwnershipTests
        // so the two fixtures cannot drift into two subtly different instruments.

        // ── Drive helpers ─────────────────────────────────────────────────────────────────────────────

        private SymbolSubsystem NewSubsystem(Func<CancellationToken, UniTask<SpriteResponse>> spriteFetch)
        {
            var subsystem = new SymbolSubsystem(_mapCamera) { NowSecondsOverride = () => _simulatedNow };
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            subsystem.GlyphSourceFactoryOverride  = _ => TestGlyphSource.FromRanges(ranges);
            subsystem.SpriteSourceFactoryOverride = _ => new GatedSpriteSource(spriteFetch);
            StyleDocument style = StyleParser.Parse(StyleJson);
            subsystem.SetStyle(style, ExtractSymbolLayers(style));
            return subsystem;
        }

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        /// <summary>Mirrors <c>TileManager.KickMeshBuild</c> more closely than the other symbol fixtures'
        /// drive helpers do, and the difference is load-bearing here: the real kick decodes on the pool,
        /// owns the ONE reference the lease is born with, reads the tile for the MESH pass, and releases in a
        /// <c>finally</c> — so whether the tile survives the park is decided by whether the park took its own
        /// reference, exactly as in production.
        ///
        /// <para><paramref name="kickTile"/> receives the tile the mesh pass read, so a later assertion can
        /// state that the parked drain read the SAME instance rather than inferring it from a count.</para></summary>
        private static void DriveKick(SymbolSubsystem subsystem, TileId tile, byte[] bytes,
            ITileDecoder decoder, IDecodedTile[] kickTile = null, bool[] parked = null)
        {
            ISymbolTileWorkerPass pass = subsystem.TryBeginBuild(SourceId, tile);
            Assert.IsNotNull(pass, "sanity: the style names this source, so the kick must produce a pass");
            if (parked != null) parked[0] = WasParked(pass);
            UniTask.RunOnThreadPool(() =>
            {
                var decode = new SharedDisposable<IDecodedTile>(decoder.Decode(tile, bytes));
                try
                {
                    IDecodedTile read = decode.Value;   // the mesh pass's read — see the summary above
                    if (kickTile != null) kickTile[0] = read;
                    pass.RunWorkerAndHandoff(decode);   // parks (acquires + enqueues) or runs, per SpritesSettled
                }
                finally { decode.Release(); }
            }).Forget();
        }

        /// <summary>The pass's OWN park decision, read straight off the object <c>TryBeginBuild</c> returned
        /// and before a single frame is pumped.
        ///
        /// <para><b>Why this and not a count.</b> Every count-shaped reading of "did it park" is taken after
        /// the fact and can be produced by a build that parked and then drained: <c>PendingSpriteCount</c>
        /// reads 0 for an arm that DID park, because <c>PumpBuilds</c> empties a settled queue on its first
        /// call, and the disposal count reads 1 for the same reason — the drain releases. A forced park on
        /// the settled path would therefore enqueue, drain, commit, and leave every one of those guards
        /// green. The park decision itself is the only reading taken BEFORE anything can undo it.</para>
        ///
        /// <para>Reflection, not a production seam: the decision is a private field of a private nested
        /// class and exposing it would be a member with no production caller (the test-code-bloat rule).
        /// Reflection into this assembly's internals is already the house instrument — eight-plus fixtures
        /// here do it, including <c>Jobs/DecodedTileOwnershipTests</c>.</para></summary>
        private static bool WasParked(ISymbolTileWorkerPass pass)
        {
            FieldInfo field = pass.GetType().GetField("_parked", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field,
                "SymbolTileWorkerPass._parked must exist — it IS the park decision, and this probe is the " +
                "only reading of it that cannot be faked by a park that already drained. If the field was " +
                "renamed, re-point the probe; do not fall back to a count.");
            return (bool)field.GetValue(pass);
        }

        /// <summary>The subsystem's live build-cancellation source, so a test can cancel WITHOUT draining the
        /// parked queue — the one interleaving production never produces in a single call (every canceller
        /// cancels and drains together) and the one the drain's ct-drop mouth exists for.</summary>
        private static CancellationTokenSource BuildCts(SymbolSubsystem subsystem)
        {
            FieldInfo field = typeof(SymbolSubsystem)
                .GetField("_buildCts", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "SymbolSubsystem._buildCts must exist — it is this drive's only lever");
            return (CancellationTokenSource)field.GetValue(subsystem);
        }

        /// <summary>The source → global-layer-index map. A parked entry carries the very <c>List&lt;int&gt;</c>
        /// instance this dictionary holds, so mutating it after the park is how the drain's
        /// processor-construction step is made to fault without touching production.</summary>
        private static List<int> LayerIndicesOf(SymbolSubsystem subsystem, string sourceId)
        {
            FieldInfo field = typeof(SymbolSubsystem)
                .GetField("_layersBySource", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "SymbolSubsystem._layersBySource must exist");
            var map = (Dictionary<string, List<int>>)field.GetValue(subsystem);
            Assert.IsTrue(map.TryGetValue(sourceId, out List<int> indices), $"no symbol layers for '{sourceId}'");
            return indices;
        }

        // Reader cutover (4.2) / resident-graph shed (4.4b): the retired subsystem.CollectInto managed-list overload
        // deduped ACROSS tiles — this suite only ever commits Tile0, so its replacement reads that ONE tile's
        // baked native block directly (DebugBlockFor — Entry.SymbolPlacementSystem itself is gone as of 4.4b) rather than routing
        // through the cross-tile winner plan the block-vs-block compare below needs.
        private static SymbolTileBlock Collect(SymbolSubsystem subsystem)
            => subsystem.Store().DebugBlockFor(new SymbolTileStore.Key(SourceId, Tile0));

        private static IEnumerator PumpUntilCommitted(SymbolSubsystem subsystem, List<LoadedTileKey> loaded, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                subsystem.ReconcileLoadedTiles(loaded);
                subsystem.PumpBuilds();
                if (Collect(subsystem) != null) yield break;
                yield return null;
            }
        }

        // ── D2 ────────────────────────────────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator AParkedBuild_DecodesOnce_AndCommitsTheSameSymbolsAsAnUnparkedOne()
        {
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var loaded = new List<LoadedTileKey> { new LoadedTileKey(SourceId, Tile0) };

            // ── Arm 1: PARKED. The sprite fetch is held open across the kick, so TryBeginBuild parks.
            var gate = new UniTaskCompletionSource<SpriteResponse>();
            var parkedProbe = new LeaseProbeDecoder();
            var kickTile = new IDecodedTile[1];
            var parkedDecision = new bool[1];
            _parkedSubsystem = NewSubsystem(_ => gate.Task);
            DriveKick(_parkedSubsystem, Tile0, _tileBytes, parkedProbe, kickTile, parkedDecision);

            // Vacuity guard 0 — read at TryBeginBuild completion, before a frame is pumped and before
            // anything downstream can undo it. This is the arm's DEFINING property and the mirror of the
            // oracle arm's guard below; the count-shaped guards that follow all sample after the fact.
            Assert.IsTrue(parkedDecision[0],
                "PRECONDITION: this arm's build must have PARKED at TryBeginBuild — its sprite fetch is held " +
                "open, so SpritesSettled is false and the settled branch must not have been taken. If this " +
                "is false the whole differential compares un-parked against un-parked.");

            for (int f = 0; f < 60; f++)
            {
                _parkedSubsystem.ReconcileLoadedTiles(loaded);
                _parkedSubsystem.PumpBuilds();
                yield return null;
            }

            // Vacuity guard 1 — the arm really parked. Without this the whole test could be comparing two
            // un-parked builds and asserting nothing about the path it exists for.
            Assert.AreEqual(1, _parkedSubsystem.PendingSpriteCount(),
                "PRECONDITION: the build must be sitting in the pending-sprite queue. If it is not, the sprite " +
                "fetch settled before the kick and this differential compares un-parked against un-parked.");
            Assert.IsNull(Collect(_parkedSubsystem), "…and a parked build must commit nothing while gated");

            // Vacuity guard 2, INVERTED — the kick's tile is STILL ALIVE while the build sits parked. This
            // is the fact that makes the drain's read a share rather than a use-after-free, and it is the
            // exact statement the old form denied: it asserted 1 here, because the kick's scope had closed
            // and freed the tile. A 1 now means the park did NOT acquire a reference and the drain is about
            // to read freed memory.
            Assert.AreEqual(1, parkedProbe.DecodeCount, "the kick decoded once (at the fetch, before the lease existed)");
            Assert.AreEqual(0, parkedProbe.DisposedCount,
                "the PARKED REFERENCE is what keeps the kick's tile alive across the whole " +
                "SetStyle->SpritesSettled window. The kick lambda's finally has already released ITS " +
                "reference by now (60 pumped frames), so a disposed tile here means the count reached zero — " +
                "i.e. the park never acquired, and the drain below will read freed Allocator.Persistent " +
                "memory. This is the cost the reference count buys back, stated as the assertion that " +
                "inverted.");
            Assert.IsNotNull(kickTile[0], "sanity: the kick really read the decoded tile");
            Assert.AreSame(parkedProbe.TileOfDecode(0), kickTile[0],
                "sanity: the one decode IS the tile the kick read — so 'the drain read this instance' below " +
                "is a statement about the kick's tile, not about some other decode");

            // Settle the sprites with the real fixture sheet — PumpBuilds now drains the parked entry, over
            // the tile the park's reference kept alive.
            gate.TrySetResult(new SpriteResponse { Json = _spriteJson, Png = _spritePng, HasData = true });
            yield return PumpUntilCommitted(_parkedSubsystem, loaded, 200);

            SymbolTileBlock parked = Collect(_parkedSubsystem);
            Assert.IsNotNull(parked, "the parked build must commit once the sprite fetch settles");
            Assert.AreEqual(0, _parkedSubsystem.PendingSpriteCount(), "…and the pending queue must have drained");

            Assert.AreEqual(1, parkedProbe.DecodeCount,
                "THE NUMBER THIS STAGE BOUGHT: a parked build decodes EXACTLY ONCE. It used to be 2 — the " +
                "drain re-parsed the retained bytes because the kick's scope had already freed the tile — and " +
                "that second decode is what the reference count deletes. A 2 here means the park is no longer " +
                "holding its reference across the sprite-settle window; do not 'restore' this to 2.");

            // Strictly stronger than the count, and the statement no count can make: the drain read the SAME
            // IDecodedTile INSTANCE the kick read. A layer is read only inside a worker pass — DriveKick's
            // mesh read touches `Tile` and never `GetLayer` — so a layer read recorded against decode 0 can
            // only be the parked drain's extract, running over the kick's own tile.
            Assert.Greater(parkedProbe.LayerReadThreadIds.Count, 0,
                "the parked drain's extract must have read the layers OF THE KICK'S TILE (decode 0 is the " +
                "only decode there is). Zero layer reads would mean the extract never ran, and the label " +
                "assertions above would be measuring something else entirely.");

            // The drain's WORKER PHASE stays off the main thread. PumpBuilds dispatches it through
            // SymbolSubsystem.WorkScheduler (ThreadPoolWorkScheduler by default — this fixture never overrides
            // it); turning that into a direct call would move the whole extract onto the main thread — a
            // frame-time regression the sibling parity tooth cannot see, because it pins the TAIL on main and
            // says nothing about the phase before it.
            //
            // THE INSTRUMENT MOVED, and it had to. This used to assert on decode index 1 — the drain's
            // re-decode — and there is no decode 1 any more. The reading is now the EXTRACT's thread, taken
            // from the layer reads recorded above: every one of them happens inside a worker pass, and the
            // only worker pass that runs for this arm is the parked drain's (DriveKick's mesh read touches
            // `Tile` and never a layer). Same property, new reading, its own falsifier.
            Assert.AreEqual(mainThreadId, Thread.CurrentThread.ManagedThreadId,
                "precondition: the assertion below compares against the thread this test body runs on, so " +
                "that thread must still BE the main thread here — otherwise the comparison is against a " +
                "stale id and passes for free");
            foreach (int extractThreadId in parkedProbe.LayerReadThreadIds)
                Assert.AreNotEqual(mainThreadId, extractThreadId,
                    $"the parked drain's worker phase must run OFF the main thread. A layer was read on " +
                    $"thread {extractThreadId}, the main thread is {mainThreadId}. PumpBuilds dispatches the " +
                    "drain through SymbolSubsystem.WorkScheduler (ThreadPoolWorkScheduler by default); a " +
                    "direct call there would put a full extract on the frame thread for every build that ever " +
                    "parked on a sprite fetch.");

            // ── Arm 2: the ORACLE. Same everything, except the sprite fetch has already settled when the
            // kick arrives, so TryBeginBuild builds inline and nothing is ever parked.
            var settled = UniTask.FromResult(new SpriteResponse { Json = _spriteJson, Png = _spritePng, HasData = true });
            var oracleProbe = new LeaseProbeDecoder();
            var oracleDecision = new bool[1];
            _oracleSubsystem = NewSubsystem(_ => settled);
            for (int f = 0; f < 10; f++) { _oracleSubsystem.PumpBuilds(); yield return null; } // let the fetch land
            DriveKick(_oracleSubsystem, Tile0, _tileBytes, oracleProbe, null, oracleDecision);

            // THE MIRROR GUARD, taken at the only moment that can distinguish "never parked" from "parked
            // and drained". Sampled here — at TryBeginBuild completion, before PumpBuilds runs — a forced
            // park on the settled path fails immediately. Sampled after the pump, as the count-shaped
            // guards below are, that same forced park would enqueue, drain on the very next PumpBuilds,
            // commit, and leave DecodeCount == 1, PendingSpriteCount == 0 and DisposedCount == 1: all three
            // green, all three blind. Keep this assertion FIRST and keep it here.
            Assert.IsFalse(oracleDecision[0],
                "PRECONDITION: the oracle arm's sprite fetch was already settled at kick time, so " +
                "TryBeginBuild must have taken the SETTLED branch and parked NOTHING. A true here means both " +
                "arms parked and the label differential below compares parked against parked.");

            yield return PumpUntilCommitted(_oracleSubsystem, loaded, 200);

            SymbolTileBlock unparked = Collect(_oracleSubsystem);

            // Vacuity guard 3, REPLACED — not renumbered. It used to be `oracleProbe.DecodeCount == 1`
            // against the parked arm's 2, and under the reference count BOTH arms decode once: the old form
            // no longer discriminates and would sit here green and vacuous forever. The new differential is
            // the LIFETIME, which still differs sharply between the arms:
            //
            //   parked arm  — DisposedCount 0 immediately after its kick settles (asserted above): the park's
            //                 reference is holding the tile open.
            //   oracle arm  — DisposedCount 1 at the same point: nothing parked, so the kick's finally was
            //                 the last release and the tile is gone.
            //
            // Plus the direct statement that this arm never parked at all. PendingSpriteCount cannot serve
            // alone here in either position: after the pump it reads 0 even for an arm that DID park, because
            // PumpBuilds drains a settled queue on its first call (review arm 1's NIT 2); before the pump it
            // reads 0 for an arm that WOULD park, because the enqueue happens on the pool thread DriveKick
            // only just dispatched. Paired with the dispose count, which no race can undo, it is decisive.
            Assert.AreEqual(1, oracleProbe.DecodeCount,
                "sanity: the oracle arm decoded once (both arms do now — this is no longer the differential)");
            Assert.AreEqual(0, _oracleSubsystem.PendingSpriteCount(),
                "sanity: the queue is empty. NOT the never-parked guard — that is the park-decision " +
                "assertion above; this reads 0 for an arm that parked and then drained, which is exactly " +
                "the case it cannot see.");
            Assert.AreEqual(1, oracleProbe.DisposedCount,
                "PRECONDITION (b) — THE REPLACEMENT DIFFERENTIAL: with nothing parked, the kick's finally was " +
                "the LAST release, so this arm's tile is already freed. The parked arm reads 0 at the " +
                "equivalent point (above) precisely because its park holds a reference. A 0 here would mean " +
                "this arm parked too, and the label differential below would be comparing parked against " +
                "parked and asserting nothing. Do NOT restore the old 1-vs-2 decode-count form: both arms " +
                "decode once now, so it discriminates nothing.");

            // ── The differential.
            Assert.Greater(unparked.Kinds.Length, 0, "sanity: the oracle arm committed labels at all");
            Assert.AreEqual(unparked.Kinds.Length, parked.Kinds.Length,
                "a parked build must commit the SAME NUMBER of labels as one that never parked");
            BlockColumnHash.AssertColumnsEqual(unparked, parked);

            bool anyIcon = false;
            foreach (PointStageInput p in parked.Points) if (p.AtlasKind == SymbolKind.Icon) { anyIcon = true; break; }
            Assert.IsTrue(anyIcon,
                "sanity: the compared labels must include ICONS. Icons are the only part of the output the " +
                "sprite atlas can change, so a text-only differential would be green under a parked drain " +
                "that lost the atlas entirely.");

            // ── Lifetime, asserted LAST. Not stylistic ordering: the pool thread enqueues the handoff and
            // only THEN releases its reference, so the main thread can complete the whole tail and reach an
            // earlier-placed balance assertion in the microseconds before the tile is disposed — a false RED
            // on a loaded machine (review arm 1's NIT 1). By here the pumping above has long since closed
            // that window.
            yield return null;
            Assert.AreEqual(0, parkedProbe.UnbalancedCount,
                $"every decoded tile on the PARKED path must be disposed exactly once — " +
                $"{parkedProbe.UnbalancedCount} of {parkedProbe.DecodeCount} were not. The drain's release of " +
                "the PARK's reference is the only thing that frees this tile's Allocator.Persistent buffers, " +
                "and NativeLeakDetection is off in the batch gate, so this probe is the only instrument that " +
                "can see it leak. A non-zero here is either a missed release (a leak) or a double one (a " +
                "double free), and the count catches both.");
            Assert.AreEqual(0, oracleProbe.UnbalancedCount, "…and the un-parked arm leaks nothing either");

            // A use-after-free inside the worker is swallowed by production into a Debug.LogWarning, so
            // without this the failure would surface only as a bare "0 symbols" count mismatch. This makes it
            // name itself.
            //
            // Watching for THAT message rather than calling LogAssert.NoUnexpectedReceived(): the blanket
            // form fails on ambient engine noise unrelated to the code under test — it tripped on URP's
            // "the output Render Texture must have a depth buffer" warning, emitted by the test camera during
            // the frame pumps. A tooth that reds on the harness is worse than no tooth.
            lock (_logGate)
                CollectionAssert.IsEmpty(_buildFailureLogs,
                    "production swallows a failed symbol build into a warning and commits nothing, so a " +
                    "use-after-free in the parked drain would otherwise show up only as a label-count mismatch. " +
                    "These warnings name the real cause: " + string.Join(" | ", _buildFailureLogs));
        }

        // ── The parked queue's DISCARD mouth ──────────────────────────────────────────────────────────

        /// <summary>
        /// <b>Funnel 4 — a parked build thrown away by a restyle must release the reference it took.</b>
        ///
        /// <para>This is the leak the park's own acquire creates. A parked entry now holds a live decoded
        /// tile across the whole <c>SetStyle</c>→<c>SpritesSettled</c> window — that is the point, and it is
        /// what deletes the re-decode — but a restyle that arrives during that window drops the entry
        /// without ever dispatching it. Dequeueing it is not enough: <c>DrainAndDiscardParkedBuilds</c> has
        /// to dispose the entry's reference, or the tile's <c>Allocator.Persistent</c> buffers are freed by
        /// nothing at all. The funnel exists as one method precisely so this obligation has one home; before
        /// D0 it was two bare <c>while (TryDequeue(out _)) { }</c> loops, i.e. two places to forget it.</para>
        ///
        /// <para><b>RED injection:</b> delete <c>dropped.Decode.Release()</c> from
        /// <c>SymbolSubsystem.DrainAndDiscardParkedBuilds</c>.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator AParkedBuildDroppedByARestyle_ReleasesItsDecodeReference()
        {
            var loaded = new List<LoadedTileKey> { new LoadedTileKey(SourceId, Tile0) };

            var gate = new UniTaskCompletionSource<SpriteResponse>();
            var probe = new LeaseProbeDecoder();
            _parkedSubsystem = NewSubsystem(_ => gate.Task);
            DriveKick(_parkedSubsystem, Tile0, _tileBytes, probe);

            for (int f = 0; f < 60; f++)
            {
                _parkedSubsystem.ReconcileLoadedTiles(loaded);
                _parkedSubsystem.PumpBuilds();
                yield return null;
            }

            // PRECONDITIONS, both halves. Without the first the restyle drops nothing; without the second
            // the entry is holding nothing and "it released" is trivially true.
            Assert.AreEqual(1, _parkedSubsystem.PendingSpriteCount(),
                "PRECONDITION: a build must actually be sitting in the pending-sprite queue for the restyle " +
                "to discard. If the sprite fetch settled before the kick, nothing parked and this tooth " +
                "observes an empty queue being emptied.");
            Assert.AreEqual(1, probe.DecodeCount, "ANTI-VACUITY: the kick really decoded a tile");
            Assert.AreEqual(0, probe.DisposedCount,
                "ANTI-VACUITY: and that tile is still ALIVE — the kick's own reference has long since been " +
                "released (60 pumped frames), so the parked entry's reference is the only thing holding it. " +
                "That is what makes the discard below the ONLY remaining thing that can free it.");

            // The restyle: SetStyle drains the parked queue through the discard funnel.
            StyleDocument restyled = StyleParser.Parse(StyleJson);
            _parkedSubsystem.SetStyle(restyled, ExtractSymbolLayers(restyled));

            Assert.AreEqual(0, _parkedSubsystem.PendingSpriteCount(), "sanity: the restyle emptied the queue");
            Assert.AreEqual(1, probe.DisposedCount,
                "the discarded entry's reference must be RELEASED, freeing the tile. A 0 here is the leak: " +
                "the entry left the queue holding the last reference to Allocator.Persistent buffers, and " +
                "nothing else in the pipeline knows the tile exists.");
            Assert.AreEqual(0, probe.UnbalancedCount,
                "…exactly once — the same count catches a double release, which is what disposing the " +
                "reference in both the discard funnel and the live drain would produce.");

            gate.TrySetResult(new SpriteResponse { Json = _spriteJson, Png = _spritePng, HasData = true });
            yield return null;
        }

        // ── The parked reference's PRE-CONSUMER exits ─────────────────────────────────────────────────
        //
        // Three ways a parked entry's reference can stop being reachable by the mouth that was supposed to
        // consume it. R1 retired the FIRST of these — a post-drain restyle race modelled by a same-thread
        // interposing handle (`AcquireInterposingHandle`, since deleted) — because the atomic park
        // (`TryParkBuild`, gated by `_parkGate`) makes that exact interleaving IMPOSSIBLE: the ct-check and
        // the Acquire() now run behind the SAME lock the abandon-drain takes, so a canceller's cancel-then-
        // drain can never land between them. Its successor is the gate-exclusion rendezvous tooth just
        // below — it proves the STRONGER property (the window is closed, not merely "closed and released in
        // time"). The remaining two are unchanged: the cancellation the drain's ct-drop was written for, and
        // any throw between the dequeue and the worker starting.

        /// <summary>Parks exactly one build on the CALLING thread and hands back the lease whose creator
        /// reference the caller still owns — the deterministic stand-in for <c>DriveKick</c>'s pool task,
        /// used where the tooth needs to know the precise moment the entry became visible to the drain.
        /// Production runs this same call on the pool inside <c>TileManager</c>'s kick lambda; nothing in the
        /// ownership protocol under test depends on which thread it is.</summary>
        private SharedDisposable<IDecodedTile> ParkOneBuild(SymbolSubsystem subsystem, LeaseProbeDecoder probe)
        {
            ISymbolTileWorkerPass pass = subsystem.TryBeginBuild(SourceId, Tile0);
            Assert.IsNotNull(pass, "sanity: the style names this source, so the kick must produce a pass");
            Assert.IsTrue(WasParked(pass),
                "PRECONDITION: the sprite fetch is gated open, so TryBeginBuild must have PARKED. An " +
                "un-parked pass would run inline and there would be no queued reference to strand.");

            var lease = new SharedDisposable<IDecodedTile>(probe.Decode(Tile0, _tileBytes));
            pass.RunWorkerAndHandoff(lease);

            Assert.AreEqual(1, probe.DecodeCount, "ANTI-VACUITY: a tile really was decoded");
            Assert.AreEqual(1, subsystem.PendingSpriteCount(),
                "PRECONDITION: the entry must be in the parked queue — the park's Acquire() only happens on " +
                "the way to the enqueue, so an empty queue means no reference was taken and every balance " +
                "below is trivially satisfied.");
            return lease;
        }

        /// <summary>Reflects <c>SymbolSubsystem._parkGate</c> — the same house instrument as
        /// <see cref="BuildCts"/> — so a test can rendezvous with it directly.</summary>
        private static object ParkGateOf(SymbolSubsystem subsystem)
        {
            FieldInfo field = typeof(SymbolSubsystem)
                .GetField("_parkGate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "SymbolSubsystem._parkGate must exist — the gate this tooth rendezvous locks against");
            return field.GetValue(subsystem);
        }

        /// <summary>Reflects the refcount field (named <c>_refs</c> on both <c>DecodedTileLease</c> — R1 —
        /// and its R2 successor <c>SharedDisposable&lt;T&gt;</c>) directly off <paramref name="handle"/>'s
        /// own runtime type, so this reading survives the type swap untouched: 1 means only the creator's
        /// reference is live (nothing has Acquired), 2 means exactly one more has (Acquire() ran).
        ///
        /// <para>Substitutes for the plan's sketched <c>probe.AcquireCount</c>: a decoder-level probe cannot
        /// see <c>Acquire()</c> at all (it runs on the wrapping HANDLE, never on the <see cref="IDecodedTile"/>
        /// the decoder produces), and a handle-wrapping test double (the retired <c>AcquireInterposingHandle</c>
        /// shape) cannot survive R2 — <c>SharedDisposable&lt;T&gt;</c> is a sealed concrete class with no
        /// interface left to implement. Reading the refcount directly needs neither.</para></summary>
        private static int RefsOf(object handle)
        {
            FieldInfo field = handle.GetType().GetField("_refs", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field,
                "the handle's `_refs` field must exist — the refcount this tooth reads directly, since " +
                "Acquire() has no other externally observable effect without a production-only test seam " +
                "(the plan's F1 Option 2, declined in favour of this zero-footprint reading).");
            return (int)field.GetValue(handle);
        }

        /// <summary>
        /// <b>The gate-exclusion tooth — R1's mandatory RED tooth (decode-refcount plan §6), successor to
        /// the retired <c>AParkEnqueuedAfterItsCancellersDrain_ReleasesItsReference</c>.</b>
        ///
        /// <para>That tooth modelled a race — a same-thread interposing handle running a reentrant restyle
        /// between the park's <c>Acquire()</c> and its <c>Enqueue</c> — that the atomic park
        /// (<c>TryParkBuild</c>, gated by <c>_parkGate</c>) now makes IMPOSSIBLE: both the ct-check and the
        /// <c>Acquire()</c> run behind the SAME lock <c>DrainAndDiscardParkedBuilds</c> takes, so there is no
        /// window left between them for a canceller to land in. Modelling an impossible race would prove
        /// nothing; this tooth proves the stronger replacement property directly — that the park genuinely
        /// cannot acquire a reference while the gate is held elsewhere.</para>
        ///
        /// <para><b>The rendezvous, and why it is deterministic rather than a timing race.</b> The main
        /// thread takes <c>_parkGate</c> BEFORE starting the background park, so the background thread
        /// cannot get past its <c>lock (_parkGate)</c> statement until the main thread releases it — there is
        /// no interleaving in which it could. Polling <see cref="Thread.ThreadState"/> for a SUSTAINED
        /// <c>WaitSleepJoin</c> only waits for the OS to schedule the background thread far enough to reach
        /// that statement; it does not race it. Once observed blocked, reading the refcount is safe under
        /// EITHER the correct code (the background thread cannot have executed <c>Acquire()</c> — it is
        /// behind the very lock it is blocked on) or the injected defect (injection A moves <c>Acquire()</c>
        /// OUTSIDE the gate, so by the time the thread blocks — now only for the Enqueue — the acquire has
        /// ALREADY run). Either way the read is stable at the moment it is taken; nothing races it.</para>
        ///
        /// <para><b>RED injection (verified — see the dev report):</b> move <c>Acquire()</c> (and the
        /// <c>ct</c> check) OUTSIDE <c>lock (_parkGate)</c> in <c>TryParkBuild</c> → <c>RefsOf(lease)</c>
        /// reads 2 while the main thread holds the gate, where this tooth asserts 1 → RED.</para>
        /// </summary>
        [Test]
        public void AParkBlockedByTheAbandonDrainGate_AcquiresNothingUntilItHoldsTheGate()
        {
            var gate  = new UniTaskCompletionSource<SpriteResponse>();
            var probe = new LeaseProbeDecoder();
            _parkedSubsystem = NewSubsystem(_ => gate.Task);

            ISymbolTileWorkerPass pass = _parkedSubsystem.TryBeginBuild(SourceId, Tile0);
            Assert.IsNotNull(pass, "sanity: the style names this source, so the kick must produce a pass");
            Assert.IsTrue(WasParked(pass), "PRECONDITION: the gated sprite fetch must make TryBeginBuild park");

            var lease = new SharedDisposable<IDecodedTile>(probe.Decode(Tile0, _tileBytes));
            object parkGate = ParkGateOf(_parkedSubsystem);

            Thread bg;
            lock (parkGate)
            {
                bg = new Thread(() => pass.RunWorkerAndHandoff(lease)) { IsBackground = true };
                bg.Start();

                // THE RENDEZVOUS (see the summary above for why this is deterministic, not a timing race).
                // Deliberately KEPT out of the zero-busy-wait conversion (design doc "Open findings"): this
                // is not a UniTask completion wait, so there is no kernel event to park WaitOffPlayerLoop on.
                // It polls raw Thread.ThreadState — a transition this process cannot subscribe to — and the
                // Sleep(2) dwell is load-bearing: it confirms the blocked state is SUSTAINED, not transient,
                // which is the precondition the trailing Assert.IsTrue(blocked, ...) needs to be non-vacuous.
                // A signal-before-lock handshake would only prove "about to block", weakening that
                // precondition — the exact disarmed-tooth failure mode this stage exists to avoid.
                bool blocked = false;
                for (int i = 0; i < 2000 && !blocked; i++)
                {
                    if ((bg.ThreadState & ThreadState.WaitSleepJoin) != 0)
                    {
                        Thread.Sleep(2); // sustained, not a transient read between two unrelated waits
                        blocked = (bg.ThreadState & ThreadState.WaitSleepJoin) != 0;
                    }
                    else Thread.Sleep(1);
                }
                Assert.IsTrue(blocked,
                    "PRECONDITION: the background park must be BLOCKED trying to enter _parkGate within the " +
                    "bound — otherwise this tooth never rendezvoused with the gate at all and everything " +
                    "below measures nothing.");

                // THE DISCRIMINATOR: while main holds the gate, the park must not have acquired a reference.
                Assert.AreEqual(1, RefsOf(lease),
                    "the park's Acquire() must not have run while the main thread holds _parkGate — a 2 " +
                    "here means Acquire() happened OUTSIDE the gate (the reopened mouth-5 window: " +
                    "ct-checked, then a canceller's cancel+drain lands, then this acquire+enqueue strands " +
                    "with nothing left to release it).");

                // Cancel while STILL holding the gate — every production canceller cancels strictly before
                // it drains, and this reproduces exactly that ordering against the park still waiting to get in.
                BuildCts(_parkedSubsystem).Cancel();
            } // release the gate

            Assert.IsTrue(bg.Join(2000),
                "the background park must finish once the gate is released — a hang here means TryParkBuild " +
                "is blocked on something other than _parkGate.");

            Assert.AreEqual(0, _parkedSubsystem.PendingSpriteCount(),
                "the park must have REFUSED once it saw the cancelled token inside the gate — nothing enqueued.");
            Assert.AreEqual(1, RefsOf(lease),
                "…and it must have taken NO reference — still just the creator's, held by this test exactly " +
                "as a kick lambda would hold it before its own finally.");

            lease.Release(); // the kick's own reference, dropped exactly as its lambda's finally would drop it
            Assert.AreEqual(1, probe.DisposedCount, "the tile is freed by the one reference that was ever taken");
            Assert.AreEqual(0, probe.UnbalancedCount, "…exactly once");

            gate.TrySetResult(new SpriteResponse { HasData = false });
        }

        /// <summary>
        /// <b>The stress balance test (decode-refcount plan §6, F1) — many concurrent park / cancel / drain
        /// cycles over <see cref="LeaseProbeDecoder"/>, asserting <c>UnbalancedCount == 0</c> under every
        /// interleaving.</b>
        ///
        /// <para>Designed so it can go RED only on a REAL imbalance — a missed release (leak) or a double
        /// release (double-free) — never on timing: it makes no assertion about queue depth or ordering
        /// MID-flight, only the TERMINAL balance once every dispatched pool worker has finished and a final
        /// sweep has discarded whatever is left. Every build parks (the sprite fetch never settles for the
        /// whole test), so every reference this drives ends up either (a) refused by the gate — nothing ever
        /// acquired — (b) acquired and later discarded by one of the interleaved restyles'
        /// <c>DrainAndDiscardParkedBuilds</c> calls, or (c) acquired and left for the FINAL sweep's drain to
        /// discard — never a fourth, unaccounted-for state.</para>
        /// </summary>
        [Test]
        public void ParkCancelDrainStress_NeverImbalancesUnderAnyInterleaving()
        {
            var gate  = new UniTaskCompletionSource<SpriteResponse>(); // never settles — every build parks
            var probe = new LeaseProbeDecoder();
            _parkedSubsystem = NewSubsystem(_ => gate.Task);

            const int iterations = 200;
            var workers = new List<Thread>();

            for (int i = 0; i < iterations; i++)
            {
                ISymbolTileWorkerPass pass = _parkedSubsystem.TryBeginBuild(SourceId, Tile0);
                Assert.IsNotNull(pass, "sanity: the style names this source on every iteration");

                var lease = new SharedDisposable<IDecodedTile>(probe.Decode(Tile0, _tileBytes));
                var worker = new Thread(() =>
                {
                    // Mirrors DriveKick / production's kick lambda: the pool thread's own reference is
                    // released in a finally, exactly as KickMeshBuild's would be — independent of whatever
                    // the park does with its OWN (separate) reference.
                    try { pass.RunWorkerAndHandoff(lease); }
                    finally { lease.Release(); }
                }) { IsBackground = true };
                workers.Add(worker);
                worker.Start();

                // Periodically race a cancel+drain against the in-flight pool workers — the real
                // interleaving TryParkBuild's gate exists to make safe: SetStyle always cancels _buildCts
                // strictly before it drains _pendingSpriteQueue, exactly like every production canceller.
                if ((i & 3) == 0)
                {
                    StyleDocument restyled = StyleParser.Parse(StyleJson);
                    _parkedSubsystem.SetStyle(restyled, ExtractSymbolLayers(restyled));
                }
            }

            // Let every dispatched pool worker finish BEFORE the final sweep, so the terminal balance below
            // is over a CLOSED population — nothing still in flight for the final drain to race.
            foreach (Thread worker in workers)
                Assert.IsTrue(worker.Join(5000), "a stress worker failed to finish within the bound");

            // The final sweep: whatever is still parked is discarded here (refused entries left nothing).
            StyleDocument final = StyleParser.Parse(NoGlyphStyleJson);
            _parkedSubsystem.SetStyle(final, ExtractSymbolLayers(final));

            Assert.AreEqual(probe.DecodeCount, probe.DisposedCount,
                $"every decoded tile must be freed by the end of the stress run — " +
                $"{probe.DecodeCount - probe.DisposedCount} of {probe.DecodeCount} are still alive with no " +
                "owner left to free them.");
            Assert.AreEqual(0, probe.UnbalancedCount,
                $"…and freed EXACTLY once — {probe.UnbalancedCount} of {probe.DecodeCount} were disposed a " +
                "number of times other than one. The same count catches a double release under concurrent " +
                "park/cancel/drain cycles, which a smaller single-threaded tooth cannot exercise.");
        }

        /// <summary>
        /// <b>The drain's ct-drop mouth, driven at runtime.</b>
        ///
        /// <para>This was pinned STRUCTURALLY on the claim that no test could reach it, because every
        /// production canceller drains in the same call it cancels in. The claim is false: the cancellation
        /// source is a private field, and reflecting it to cancel WITHOUT draining is exactly the state a
        /// pool-vs-main race produces — reflection into this assembly is the house instrument, not a
        /// production seam. A count of <c>Dispose()</c> spellings could never see whether this mouth
        /// actually releases; this does.</para>
        ///
        /// <para><b>RED injection:</b> delete <c>pending.Decode.Release()</c> from <c>PumpBuilds</c>'
        /// <c>if (pending.Ct.IsCancellationRequested)</c> arm.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator AParkedEntryCancelledWithoutADrain_IsReleasedByThePumpsCtDrop()
        {
            var gate  = new UniTaskCompletionSource<SpriteResponse>();
            var probe = new LeaseProbeDecoder();
            _parkedSubsystem = NewSubsystem(_ => gate.Task);

            SharedDisposable<IDecodedTile> lease = ParkOneBuild(_parkedSubsystem, probe);
            lease.Release(); // the kick's reference, dropped exactly as its lambda's finally drops it
            Assert.AreEqual(0, probe.DisposedCount,
                "PRECONDITION: the parked entry's reference is now the ONLY one — so whatever frees this " +
                "tile below is the mouth under test and nothing else.");

            // Cancel WITHOUT draining. No production caller does this in one call; a pool-vs-main race does.
            BuildCts(_parkedSubsystem).Cancel();

            gate.TrySetResult(new SpriteResponse { Json = _spriteJson, Png = _spritePng, HasData = true });
            for (int f = 0; f < 300 && _parkedSubsystem.PendingSpriteCount() > 0; f++)
            {
                _parkedSubsystem.PumpBuilds();
                yield return null;
            }

            Assert.AreEqual(0, _parkedSubsystem.PendingSpriteCount(),
                "PRECONDITION: the drain must actually have dequeued the entry — otherwise the sprite fetch " +
                "never settled, the drain never ran, and the balance below says nothing about the ct-drop.");
            Assert.AreEqual(1, probe.DisposedCount,
                "the ct-drop must RELEASE the entry it drops. It removes the last owner from the queue; a " +
                "`continue` without the dispose strands the decoded tile's Allocator.Persistent buffers with " +
                "nothing left in the process that knows the tile exists.");
            Assert.AreEqual(0, probe.UnbalancedCount, "…exactly once");
        }

        /// <summary>
        /// <b>The dequeue → worker-start window: a fault after the entry has left the queue and before the
        /// delegate that owns its release exists.</b>
        ///
        /// <para><c>TryDequeue</c> takes the queue's reference away, and for a dispatched entry the ONLY
        /// release lives inside a lambda that has not started. Everything in between — building one
        /// processor per layer index, the dispatch itself — runs unguarded, so any throw there leaves the
        /// reference with no owner at all. The drive faults the processor construction by mutating the very
        /// <c>List&lt;int&gt;</c> instance the parked entry carries, which is reachable through the
        /// subsystem's private source→layers map.</para>
        ///
        /// <para>The tooth deliberately lets the exception PROPAGATE (production does; swallowing it here
        /// would be a behaviour change, not a fix) and asserts only that the reference was released on the
        /// way out.</para>
        ///
        /// <para><b>RED injection:</b> delete the <c>try/finally</c> ownership guard around the parked
        /// drain's processor construction and dispatch in <c>PumpBuilds</c>.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator AParkedEntryWhoseWorkerCannotBeBuilt_StillReleasesItsReference()
        {
            var gate  = new UniTaskCompletionSource<SpriteResponse>();
            var probe = new LeaseProbeDecoder();
            _parkedSubsystem = NewSubsystem(_ => gate.Task);

            SharedDisposable<IDecodedTile> lease = ParkOneBuild(_parkedSubsystem, probe);
            lease.Release();
            Assert.AreEqual(0, probe.DisposedCount,
                "PRECONDITION: the parked entry's reference is the only one left");

            // The entry carries this exact list instance, so the drain's `_allSymbolLayers[globalIndex]`
            // reads an out-of-range index and throws BETWEEN the dequeue and the dispatch.
            List<int> indices = LayerIndicesOf(_parkedSubsystem, SourceId);
            Assert.AreEqual(1, indices.Count, "sanity: the fixture style has exactly one symbol layer");
            indices[0] = 999;

            gate.TrySetResult(new SpriteResponse { Json = _spriteJson, Png = _spritePng, HasData = true });
            Exception faulted = null;
            for (int f = 0; f < 300 && faulted == null && _parkedSubsystem.PendingSpriteCount() > 0; f++)
            {
                try { _parkedSubsystem.PumpBuilds(); }
                catch (Exception ex) { faulted = ex; }
                yield return null;
            }

            Assert.IsNotNull(faulted,
                "PRECONDITION: the drain must really have faulted after the dequeue. Without a fault there " +
                "is no pre-consumer window to observe and this tooth passes measuring the ordinary path.");
            Assert.AreEqual(0, _parkedSubsystem.PendingSpriteCount(),
                "PRECONDITION: …and the entry had already LEFT the queue when it faulted — that is what " +
                "makes the queue no longer an owner.");
            Assert.AreEqual(1, probe.DisposedCount,
                "a throw between the dequeue and the worker starting must still release the entry's " +
                "reference. The dispatched lambda's finally cannot: it does not exist yet. Without an " +
                "explicit ownership guard over that window the decoded tile is stranded — one dropped " +
                "reference per faulting drain, silently, under Allocator.Persistent.");
            Assert.AreEqual(0, probe.UnbalancedCount, "…exactly once");
        }
    }
}
