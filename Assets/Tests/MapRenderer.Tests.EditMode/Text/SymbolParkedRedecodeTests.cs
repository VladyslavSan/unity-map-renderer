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
    /// The sprite-PARKED path end-to-end. The park takes its own tile reference while the kick's is still
    /// live, so the tile survives the <c>SetStyle</c>→<c>SpritesSettled</c> window and the drain reads the
    /// SAME decoded tile: one decode, and the kick's tile stays alive while parked. The fixture keeps
    /// "Redecode" in its name because other files cite it; the name is the cost this fixture shows is absent.
    /// </summary>
    /// <remarks>
    /// Non-obvious why: a parked build with a stale extent or a mis-stamped TileId moves the symbols and no
    /// other test sees it, so a parked arm and a never-parked arm must commit column-equal blocks. Each arm
    /// reads its own park decision at <c>TryBeginBuild</c>, because every count reads the same for "never
    /// parked" and "parked, then drained".
    /// </remarks>
    [TestFixture]
    public class SymbolParkedRedecodeTests
    {
        private const string SourceId = "s";
        private const string FontName = "LatinFont";
        private static readonly TileId Tile0 = new TileId { Z = 3, X = 0, Y = 0 };

        // A root `sprite` URL gives the test a fetch to gate. The layer carries an icon because icons are
        // what parking waits for; a text-only style would park with no observable difference.
        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'sprite': 'https://example.invalid/sprite',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'], 'icon-image':'marker' } }
            ]
        }".Replace('\'', '"');

        // No symbol layers and no `glyphs` URL: SetStyle leaves `_builder` null and PumpBuilds returns at once,
        // so only the discard funnel can free a parked entry and a stranded one leaks permanently, not late.
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
        // SpritesSettled's deadline clock, frozen: on a real clock a slow machine can cross
        // SpriteFetchDeadlineSeconds in 60 frames and settle the fetch, so only the test's gate may settle it.
        private double _simulatedNow;
        // Production logs a failed symbol build on whichever thread it ran, hence the lock. Only the
        // build-failure message is collected, not ambient engine warnings.
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

        /// <summary>Mirrors <c>TileManager.KickMeshBuild</c>: decodes on the pool, owns the lease's first
        /// reference, reads the tile for the mesh pass, and releases in a <c>finally</c>. So the tile
        /// survives the park only if the park took its own reference, as in production.
        /// <paramref name="kickTile"/> receives the tile the mesh pass read, so an assertion can check that
        /// the parked drain read the SAME instance.</summary>
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

        /// <summary>The pass's OWN park decision, read off the object <c>TryBeginBuild</c> returned, before
        /// a frame is pumped. Non-obvious why: every count reads the same for a build that parked and then
        /// drained (<c>PendingSpriteCount</c> 0, disposal count 1), so only this reading precedes any undo.
        /// It uses reflection because the field is private to a private nested class, and exposing it
        /// would add a member with no production caller.</summary>
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

        // The subsystem reads ONE tile's baked native block directly (DebugBlockFor); this suite only ever
        // commits Tile0, so it does not need the cross-tile winner plan the block-vs-block compare below uses.
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

        // ── The parked path ───────────────────────────────────────────────────────────────────────────

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

            // Vacuity guard 0 — read before a frame is pumped, so nothing downstream can undo it. It mirrors
            // the oracle arm's guard below; the count-shaped guards that follow all sample after the fact.
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

            // Vacuity guard 2 — the kick's tile is STILL ALIVE while parked, so the drain's read is a share, not
            // a use-after-free. A disposal here means the park did not acquire a reference.
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

            // The drain read the kick's own tile: only a worker pass reads a layer (DriveKick's mesh read never
            // calls GetLayer), so a layer read against decode 0 is the parked drain's extract.
            Assert.Greater(parkedProbe.LayerReadThreadIds.Count, 0,
                "the parked drain's extract must have read the layers OF THE KICK'S TILE (decode 0 is the " +
                "only decode there is). Zero layer reads would mean the extract never ran, and the label " +
                "assertions above would be measuring something else entirely.");

            // The drain's worker phase stays off the main thread. The layer reads above are its only thread
            // record; the sibling parity tooth pins only the TAIL on main and cannot see an extract moved there.
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

            // THE MIRROR GUARD: only before PumpBuilds runs does "never parked" differ from "parked and drained".
            // After the pump every count reads the same for both. Keep this assertion FIRST and keep it here.
            Assert.IsFalse(oracleDecision[0],
                "PRECONDITION: the oracle arm's sprite fetch was already settled at kick time, so " +
                "TryBeginBuild must have taken the SETTLED branch and parked NOTHING. A true here means both " +
                "arms parked and the label differential below compares parked against parked.");

            yield return PumpUntilCommitted(_oracleSubsystem, loaded, 200);

            SymbolTileBlock unparked = Collect(_oracleSubsystem);

            // Vacuity guard 3: both arms decode once, so the decode count cannot tell them apart. The tile's
            // lifetime can: the parked arm reads DisposedCount 0 after its kick, the oracle arm reads 1.
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

            // ── Lifetime, asserted LAST: the pool thread enqueues the handoff BEFORE it releases, so an earlier
            // balance check can run before the dispose and red falsely on a loaded machine.
            yield return null;
            Assert.AreEqual(0, parkedProbe.UnbalancedCount,
                $"every decoded tile on the PARKED path must be disposed exactly once — " +
                $"{parkedProbe.UnbalancedCount} of {parkedProbe.DecodeCount} were not. The drain's release of " +
                "the PARK's reference is the only thing that frees this tile's Allocator.Persistent buffers, " +
                "and NativeLeakDetection is off in the batch gate, so this probe is the only instrument that " +
                "can see it leak. A non-zero here is either a missed release (a leak) or a double one (a " +
                "double free), and the count catches both.");
            Assert.AreEqual(0, oracleProbe.UnbalancedCount, "…and the un-parked arm leaks nothing either");

            // Production turns a worker use-after-free into a Debug.LogWarning; this names it. Not the blanket
            // LogAssert.NoUnexpectedReceived(): the test camera emits unrelated URP depth-buffer warnings.
            lock (_logGate)
                CollectionAssert.IsEmpty(_buildFailureLogs,
                    "production swallows a failed symbol build into a warning and commits nothing, so a " +
                    "use-after-free in the parked drain would otherwise show up only as a label-count mismatch. " +
                    "These warnings name the real cause: " + string.Join(" | ", _buildFailureLogs));
        }

        // ── The parked queue's DISCARD mouth ──────────────────────────────────────────────────────────

        /// <summary>
        /// <b>A parked build thrown away by a restyle must release the reference it took.</b> A parked entry
        /// holds a live decoded tile across the <c>SetStyle</c>→<c>SpritesSettled</c> window, and a restyle
        /// drops it without a dispatch. <c>DrainAndDiscardParkedBuilds</c> must release the entry's reference,
        /// or nothing frees the tile's <c>Allocator.Persistent</c> buffers.
        /// <para><b>RED injection:</b> delete <c>dropped.Decode.Release()</c> from that method.</para>
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
        // The tests below drive three exits: a gate-blocked park, a drainless cancel, a dequeue-to-worker throw.

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

        /// <summary>Reflects the <c>_refs</c> refcount off <paramref name="handle"/>'s runtime type: 1 means
        /// only the creator's reference is live, 2 means one <c>Acquire()</c> ran. Non-obvious why: a decoder
        /// probe cannot see <c>Acquire()</c>, which runs on the handle, and <c>SharedDisposable&lt;T&gt;</c> is
        /// a sealed class that no test double can wrap.</summary>
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
        /// <b>The park cannot acquire a reference while <c>_parkGate</c> is held elsewhere.</b>
        /// <c>TryParkBuild</c> runs its ct-check and <c>Acquire()</c> behind the same lock
        /// <c>DrainAndDiscardParkedBuilds</c> takes, so no canceller can land between them.
        /// <para><b>RED injection:</b> move <c>Acquire()</c> and the ct-check outside the lock →
        /// <c>RefsOf(lease)</c> reads 2 while the main thread holds the gate.</para>
        /// </summary>
        /// <remarks>
        /// Non-obvious why: the rendezvous is deterministic, as the main thread takes the gate BEFORE it starts the
        /// background park, so polling for a sustained <c>WaitSleepJoin</c> only waits for the OS to schedule
        /// that thread. Once it blocks, the refcount is stable: behind the lock with the correct code, or
        /// already acquired with the injected defect.
        /// </remarks>
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

                // THE RENDEZVOUS: a poll, because a ThreadState change has no event to wait on. The Sleep(2) dwell
                // confirms the block is SUSTAINED; a signal-before-lock handshake proves only "about to block".
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
        /// <b>Many concurrent park / cancel / drain cycles leave <c>UnbalancedCount == 0</c>.</b> It asserts
        /// only the TERMINAL balance, after every worker finished and a final sweep ran, so only a real leak or
        /// double release can red it, not timing. Every build parks, so each reference is refused by the gate,
        /// discarded by an interleaved restyle, or discarded by the final sweep.
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
                    // Like the production kick lambda, the worker releases its own reference in a finally,
                    // whatever the park does with its separate one.
                    try { pass.RunWorkerAndHandoff(lease); }
                    finally { lease.Release(); }
                }) { IsBackground = true };
                workers.Add(worker);
                worker.Start();

                // Race a restyle against the in-flight workers: SetStyle cancels _buildCts, then drains the
                // parked queue — the interleaving TryParkBuild's gate makes safe.
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
        /// <b>The drain's ct-drop mouth releases the entry it drops.</b> Every production canceller drains in
        /// the same call, so the test reflects the private cancellation source and cancels WITHOUT a drain —
        /// the state a pool-vs-main race produces.
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
        /// <b>A fault after the dequeue and before the worker exists still releases the reference.</b> The
        /// only other release lives in a lambda that has not started. The drive mutates the layer-index list
        /// the parked entry carries, so processor construction throws; the exception propagates, as in production.
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
