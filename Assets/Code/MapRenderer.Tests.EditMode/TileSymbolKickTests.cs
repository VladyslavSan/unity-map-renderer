// Epic A / A5b acceptance teeth (F-1..F-6): the feed swap —
// TileManager's per-tile kick drives the symbol worker pass (ISymbolTileWorkerFactory/ISymbolTileWorkerPass),
// retiring the parallel SymbolTileBytesReady push. A spy factory/pass pair is installed directly on
// TileManager.SymbolWorkerFactory, bypassing the real SymbolLabelSubsystem/glyph pipeline entirely — these
// teeth are about the FEED (who drives the symbol worker, over which decode, on which thread, under which
// cap), not label content (that is SymbolProcessorParityTests' job).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class TileSymbolKickTests
    {
        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>Spy <see cref="ISymbolTileWorkerFactory"/> — records every <c>TryBeginBuild</c> call
        /// (source, tile) and every pass it issues. <see cref="ParticipatesFor"/> controls which sources get
        /// a non-null pass (default: every source); <see cref="PassFactory"/> lets a test substitute the
        /// issued pass's behaviour (e.g. a throwing spy for F-3).</summary>
        private sealed class SpySymbolTileWorkerFactory : ISymbolTileWorkerFactory
        {
            public readonly List<(string SourceId, TileId Tile)> BeginBuildCalls = new();
            public readonly List<SpySymbolTileWorkerPass> IssuedPasses = new();
            public Func<string, bool> ParticipatesFor = _ => true;
            public Func<string, TileId, SpySymbolTileWorkerPass> PassFactory;

            public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
            {
                BeginBuildCalls.Add((sourceId, tile));
                if (!ParticipatesFor(sourceId)) return null;
                SpySymbolTileWorkerPass pass = PassFactory != null
                    ? PassFactory(sourceId, tile)
                    : new SpySymbolTileWorkerPass(sourceId, tile);
                IssuedPasses.Add(pass);
                return pass;
            }
        }

        /// <summary>Spy <see cref="ISymbolTileWorkerPass"/> — records whether/with-which-decode
        /// <c>RunWorkerAndHandoff</c> ran. <see cref="OnRun"/> lets F-3 install a deliberately
        /// contract-violating throw (no internal try/catch of its own — the whole point is to prove
        /// TileManager's OUTER lambda-level guard, not this spy's own safety).</summary>
        private sealed class SpySymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            public readonly string SourceId;
            public readonly TileId Tile;
            public bool Ran;
            public SharedDisposable<IDecodedTile> ReceivedDecode;
            public Action OnRun;

            public SpySymbolTileWorkerPass(string sourceId, TileId tile) { SourceId = sourceId; Tile = tile; }

            public void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)
            {
                Ran = true;
                ReceivedDecode = decode;
                OnRun?.Invoke();
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>One source ("s") with BOTH a fill layer (over the fixture's "countries") and a symbol
        /// layer (over the fixture's "centroids") — so a single kicked tile exercises mesh AND symbol
        /// together (F-2's decode-sharing, F-3's fault-domain wrap).</summary>
        private static StyleDocument FillAndSymbolStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""s"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16 } }
            ]
        }");

        /// <summary>TWO distinct sources: "symsrc" has ONLY a symbol layer (dense mesh = 0), "meshsrc" has
        /// ONLY a fill layer — Q6/F-5's symbol-only vs mesh-only split.</summary>
        private static StyleDocument TwoSourceStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""layers"": [
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""symsrc"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16 } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""meshsrc"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <param name="preparedCacheEnabled">Set BEFORE <c>WithTestCamera()</c> — that call constructs
        /// <c>MapView</c>/<c>TileManager</c>, which reads <c>PreparedCache.Enabled</c> once at construction
        /// (mirrors <c>S82PreparedCacheTests</c>' documented ordering requirement).</param>
        private static (GameObject go, MapView view) NewView(int zoom, bool preparedCacheEnabled = true)
        {
            var go   = new GameObject("MapView_TileSymbolKick");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = zoom;
            view.Config.TileSelection.MaxZoom = zoom;
            view.Config.PreparedCache.Enabled = preparedCacheEnabled;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            return (go, view);
        }

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        /// <summary>Mirrors S51DisposalLeakGuardTests' helper — counts alive <see cref="Mesh"/> objects
        /// (over-counts editor built-ins; compare deltas, not absolutes).</summary>
        private static int CountMeshObjects() => Resources.FindObjectsOfTypeAll<Mesh>().Length;

        private static string SourcePath(params string[] relative)
            => Path.Combine(Application.dataPath, "Code", Path.Combine(relative));

        private static int CountOccurrences(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ── F-1: the feed swap is real (structural) — genuinely RED pre-A5b (all three symbols exist today) ──

        [Test]
        public void F1_FeedSwapIsReal_NoResidualPushSymbols()
        {
            string tileManagerSrc = File.ReadAllText(SourcePath("MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs"));
            string subsystemSrc   = File.ReadAllText(SourcePath("MapRenderer.Unity", "Text", "SymbolLabelSubsystem.cs"));

            Assert.AreEqual(0, CountOccurrences(tileManagerSrc, "SymbolTileBytesReady"),
                "TileManager must contain ZERO SymbolTileBytesReady occurrences — the push feed is retired.");
            Assert.IsTrue(tileManagerSrc.Contains("ISymbolTileWorkerFactory SymbolWorkerFactory"),
                "TileManager must hold the factory field SymbolWorkerFactory.");
            Assert.IsTrue(tileManagerSrc.Contains("ISymbolTileWorkerPass symbolPass"),
                "KickMeshBuild's signature must carry an ISymbolTileWorkerPass parameter.");

            Assert.AreEqual(0, CountOccurrences(subsystemSrc, "OnTileBytesReady"),
                "SymbolLabelSubsystem must contain ZERO OnTileBytesReady occurrences — the push entry is retired.");
            Assert.AreEqual(0, CountOccurrences(subsystemSrc, "_buildQueue"),
                "SymbolLabelSubsystem must contain ZERO _buildQueue occurrences — the build-start queue is retired.");
            // Asked of the TYPE SYSTEM, not the source text. The previous form grepped for
            // ": ISymbolTileWorkerFactory", which pinned the declaration's spelling rather than the
            // relationship — and C# requires a base class to be listed first, so simply giving the subsystem
            // a base class broke it while the interface was still very much implemented.
            Assert.IsTrue(typeof(ISymbolTileWorkerFactory).IsAssignableFrom(typeof(SymbolLabelSubsystem)),
                "SymbolLabelSubsystem must implement ISymbolTileWorkerFactory.");
        }

        // ── F-2: symbol rides the kick, sharing ONE decode (behavioural, spy factory) ──────────────────

        [UnityTest]
        public IEnumerator F2_SymbolRidesKick_SharingOneDecode()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var (go, view) = NewView(zoom: 0); // z0 = one tile — a clean single-decode measurement
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndSymbolStyle());

                const int recorderCapacity = 64;
                using var decodeRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "MapRenderer.Tile.Decode", capacity: recorderCapacity,
                    options: ProfilerRecorderOptions.SumAllSamplesInFrame);

                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "sanity: the tile must settle.");
                yield return null; yield return null; // let the profiler commit accumulated samples

                Assert.AreEqual(1, spy.BeginBuildCalls.Count, "TryBeginBuild must be called exactly once, at the kick.");
                Assert.AreEqual(1, spy.IssuedPasses.Count, "sanity: the source has a symbol layer — a pass must issue.");
                Assert.IsTrue(spy.IssuedPasses[0].Ran, "RunWorkerAndHandoff must have run.");
                Assert.IsNotNull(spy.IssuedPasses[0].ReceivedDecode, "sanity: the pass received a decode.");

                // Defensive clamp: the recorder is a `recorderCapacity`-frame ring buffer, and .Count can
                // momentarily reflect an in-flight write past that bound (a live ProfilerRecorder timing
                // artifact, unrelated to this tooth's claim) — the decode itself happens once, early, and
                // survives well within the last `recorderCapacity` frames regardless of how many frames
                // PumpUntilSettled took overall.
                long decodeHits = 0;
                int  sampleCount = Math.Min(decodeRecorder.Count, recorderCapacity);
                for (int i = 0; i < sampleCount; i++) decodeHits += decodeRecorder.GetSample(i).Count;
                Assert.AreEqual(1, decodeHits,
                    "F-2 DECISIVE: the tile's MVT bytes must decode exactly ONCE across mesh+symbol in the SAME " +
                    "kick task (MapRenderer.Tile.Decode fires once per decode; a symbol pass fed a fresh/second " +
                    "decode, or not driven at kick at all, would leave this at 0 or 2).");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── F-3: two fault domains — a contract-violating symbol throw never strands a mesh array ───────
        //
        // Corrected after dual review: AllTilesSettled() is NOT decisive here. Without the lambda wrap, the
        // kick task's RunOnThreadPool body throws (from RunWorkerAndHandoff), so the UniTask<MeshBuildResult>
        // itself transitions to Faulted — but ConsumeMeshBuild has its OWN faulted-task guard, independent of
        // this lambda, that still settles the tile (Built=true) via a SILENT-DISCARD path: it never reaches
        // UploadMesh (the built fill geometry is thrown away, never registered with the backend) and never
        // disposes the kick-allocated writable MeshDataArray (a genuine native-memory leak). So the tile
        // DOES settle either way — "never settle" was never the accurate failure mode. The decisive
        // observable is MeshDataPayload.DebugLiveAllocCount (mirrors S51DisposalLeakGuardTests' pattern):
        // WITH the wrap, the task always completes with a real MeshBuildResult, so consume runs normally
        // (fill geometry registered, array disposed); WITHOUT it, the array leaks past settle. RED-verified
        // (see the A5b stage report): before=0/after=1 without the wrap, 0/0 with it.
        [UnityTest]
        public IEnumerator F3_SymbolFaultNeverStrandsMeshArray_LambdaWrapCatches()
        {
            long allocBefore = MeshDataPayload.DebugLiveAllocCount;
            long meshBefore  = CountMeshObjects();

            var src  = TestDataSource.FromBytes(FixtureBytes());
            var (go, view) = NewView(zoom: 0);
            var spy = new SpySymbolTileWorkerFactory
            {
                PassFactory = (sourceId, tile) => new SpySymbolTileWorkerPass(sourceId, tile)
                {
                    OnRun = () => throw new InvalidOperationException("F-3 deliberately contract-violating spy")
                }
            };
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndSymbolStyle());
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(),
                    "sanity: the tile reaches Built either way — ConsumeMeshBuild's faulted-task guard settles " +
                    "it via silent-discard even without the wrap. Settling alone does NOT prove the wrap works " +
                    "(see the DECISIVE alloc-count assertion below).");
                Assert.AreEqual(1, spy.IssuedPasses.Count, "sanity: the throwing pass was issued.");
                Assert.IsTrue(spy.IssuedPasses[0].Ran, "sanity: RunWorkerAndHandoff actually ran (and threw).");

                // DECISIVE: the ONE assertion that discriminates "consumed normally" (wrap present) from
                // "silently discarded + leaked" (wrap absent) — AllTilesSettled()/CountMeshObjects cannot see
                // the difference, since the silent-discard path never creates a managed Mesh object at all.
                long allocAfterSettle = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(allocBefore, allocAfterSettle,
                    $"F-3 DECISIVE: the kick-allocated writable MeshDataArray must be disposed by settle time " +
                    $"(baseline={allocBefore}, after={allocAfterSettle}). Without the lambda wrap, " +
                    "RunWorkerAndHandoff's throw faults the whole RunOnThreadPool body, so ConsumeMeshBuild's " +
                    "faulted-task guard takes the silent-discard path (Built=true, but no UploadMesh, no " +
                    "dispose) — a genuine native leak this assertion catches where a Mesh-object leak guard " +
                    "cannot (nothing is ever registered on that path).");

                // Corroborating: the fill layer's geometry was actually REGISTERED — proving normal consume
                // ran (not silent-discard, which never reaches UploadMesh/backend registration).
                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "F-3: the fill layer's mesh must have been registered with the " +
                    "backend — proving the normal consume path ran, not the silent-discard path.");
                Assert.GreaterOrEqual(meshes.Length, 1, "sanity: at least one layer slot (the fill layer).");
                Assert.IsNotNull(meshes[0], "the fill mesh itself must be non-null — real geometry reached the backend.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }

            long meshAfter = CountMeshObjects();
            Assert.AreEqual(meshBefore, meshAfter,
                "F-3: no orphaned Mesh object after the throwing-spy cycle (confirmatory — the S51 leak-guard " +
                "pattern applied to the managed side; not decisive on its own, see the alloc-count assertion above).");
        }

        // ── F-4: DrainMeshBuilds stays symbol-silent (§Q-Drain KEEP) ───────────────────────────────────

        [Test]
        public void F4_DrainMeshBuilds_NeverDrivesSymbolFactory()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var (go, view) = NewView(zoom: 0);
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndSymbolStyle());

                // ONE LateUpdate creates the record + starts the fetch. The kick block can NEVER fire on this
                // same call (it requires lt.Decode already set from a PRIOR PumpPending call, and this
                // record's Decode starts null) — so TryBeginBuild is provably unreached so far.
                view.LateUpdate();
                Assert.AreEqual(0, spy.BeginBuildCalls.Count, "sanity: the kick cannot have fired yet.");

                // Settle entirely via DrainMeshBuilds — never through PumpPending's kick block again.
                view.DrainMeshBuilds();

                Assert.IsTrue(view.AllTilesSettled(), "sanity: drain must fully settle the tile.");
                Assert.AreEqual(0, spy.BeginBuildCalls.Count,
                    "F-4 DECISIVE: TryBeginBuild must NEVER fire from a DrainMeshBuilds call site — symbolPass " +
                    "is computed ONLY at the PumpPending kick site (§Q-Drain); drain always passes the default " +
                    "(null), keeping it symbol-silent by construction.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── F-5: a symbol-only source kicks; a mesh-only source runs no symbol pass ────────────────────

        [UnityTest]
        public IEnumerator F5_SymbolOnlySourceKicks_MeshOnlySourceRunsNoSymbolPass()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            // Pre-existing S82 edge case (found while developing this tooth, NOT an A5b regression, out of
            // this stage's scope to fix): the prepared-cache probe's "every dense layer id is cached" check
            // (TileManager.cs, the allCached loop) is VACUOUSLY true for a source with ZERO dense mesh
            // layers — a symbol-only source — so with the cache enabled it takes the BuildTileFromCache path
            // on every cover entry (never fetches, never reaches the kick block at all). Disabled here so
            // this tooth isolates its OWN concern (Q6: does the kick fire for a symbol-only source that DOES
            // go through the normal fetch→kick pipeline), not the unrelated S82 probe gap.
            var (go, view) = NewView(zoom: 0, preparedCacheEnabled: false);
            var spy = new SpySymbolTileWorkerFactory { ParticipatesFor = sourceId => sourceId == "symsrc" };
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: TwoSourceStyle());
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "sanity: both sources' tiles must settle.");

                var loadedKeys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(loadedKeys);
                string diag = "loaded=[" + string.Join(", ", loadedKeys.ConvertAll(k => $"{k.SourceId}:{k.Tile}")) +
                    "] beginBuildCalls=[" + string.Join(", ", spy.BeginBuildCalls.ConvertAll(c => $"{c.SourceId}:{c.Tile}")) + "]";
                Assert.AreEqual(2, view.LoadedTileCount(),
                    $"sanity: both sources must each produce exactly one loaded z0 tile. {diag}");

                Assert.IsTrue(spy.BeginBuildCalls.Exists(c => c.SourceId == "symsrc"),
                    $"F-5: TryBeginBuild must fire for the symbol-only source (dense mesh = 0 does not gate the kick). {diag}");
                Assert.IsTrue(spy.BeginBuildCalls.Exists(c => c.SourceId == "meshsrc"),
                    "sanity: TileManager calls TryBeginBuild for EVERY kicked source — participation is the " +
                    $"factory's decision, not a TileManager-side dense>0 gate. {diag}");

                SpySymbolTileWorkerPass symPass = spy.IssuedPasses.Find(p => p.SourceId == "symsrc");
                Assert.IsNotNull(symPass, "a pass must have been issued for the symbol-only source.");
                Assert.IsTrue(symPass.Ran, "RunWorkerAndHandoff must have run for the symbol-only source's pass.");

                Assert.IsFalse(spy.IssuedPasses.Exists(p => p.SourceId == "meshsrc"),
                    "F-5: no symbol pass may be issued/run for the mesh-only source.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── S82 regression (merge-step follow-up): a symbol-only source must STILL fetch+kick with the
        //    prepared cache ENABLED (the DEFAULT). Pre-fix, the cover loop seeded `allCached = true` and its
        //    dense-layer loop never ran for a zero-dense source, so `allCached` stayed vacuously true on every
        //    cover entry → BuildTileFromCache forever → never fetched, never kicked → labels never built with
        //    the cache on. RED pre-fix (symsrc never appears in BeginBuildCalls); GREEN after seeding
        //    `allCached = denseLayerIds.Count > 0`. This is F-5's twin WITHOUT the cache disabled — F-5 had to
        //    set preparedCacheEnabled:false precisely to dodge this gap; here it stays on, so the gap is the tooth.
        [UnityTest]
        public IEnumerator S82_SymbolOnlySource_FetchesAndKicks_WithPreparedCacheEnabled()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var (go, view) = NewView(zoom: 0, preparedCacheEnabled: true); // the DEFAULT — the gap's trigger
            var spy = new SpySymbolTileWorkerFactory { ParticipatesFor = sourceId => sourceId == "symsrc" };
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: TwoSourceStyle());
                yield return PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "sanity: both sources' tiles must settle.");

                Assert.IsTrue(spy.BeginBuildCalls.Exists(c => c.SourceId == "symsrc"),
                    "S82 REGRESSION: a symbol-only source (zero dense mesh layers) must fetch+kick even with " +
                    "the prepared cache ENABLED — pre-fix the vacuous `allCached` short-circuit sent it down " +
                    "BuildTileFromCache on every cover entry, so it never fetched and its labels never built " +
                    "with the cache on (the default).");

                SpySymbolTileWorkerPass symPass = spy.IssuedPasses.Find(p => p.SourceId == "symsrc");
                Assert.IsNotNull(symPass, "a pass must have been issued for the symbol-only source.");
                Assert.IsTrue(symPass.Ran, "RunWorkerAndHandoff must have run for the symbol-only source's pass.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── F-6: a tile condemned before its kick never attempts a symbol build ────────────────────────

        [Test]
        public void F6_DepartedBeforeKick_NeverBeginsSymbolBuild()
        {
            var src  = TestDataSource.FromBytes(FixtureBytes());
            var go   = new GameObject("MapView_TileSymbolKick_F6");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5; // known 9-tile z5 cover
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 1; // trickle kicks — most tiles stay un-kicked after the observe tick
            view.Config.MaxReleasesPerTick   = 1; // trickle releases — the condemned window stays observable
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillAndSymbolStyle());

                view.LateUpdate();          // Tick 1: cover created (9 tiles), fetches requested
                Thread.Sleep(5);
                view.LateUpdate();          // Tick 2: fetches observed (if their decodes have landed) → lt.Decode
                                            // set, 0 kicked. Under the eager decode a fetch task also carries a
                                            // decode, so a slow tick may observe fewer of them here — the
                                            // assertion below does not depend on how many were observed.
                Assert.AreEqual(0, spy.BeginBuildCalls.Count, "sanity: nothing kicked before the tile has a prior lt.Decode.");

                var oldKeys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(oldKeys);
                Assert.GreaterOrEqual(oldKeys.Count, 6, "need a multi-tile cover for the departure to be non-vacuous.");
                var oldTiles = oldKeys.ConvertAll(k => k.Tile);

                // Pan far away — the NEXT recompute condemns every old tile. PumpPending runs BEFORE that
                // recompute within the SAME Tick, so at most ONE old tile can race ahead and get kicked before
                // its condemnation registers (matching mesh's own pre-existing race, Stall #2) — the point of
                // this tooth is that the OTHER old tiles (the vast majority) never reach TryBeginBuild.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 150.0, Latitude = 70.0 });
                for (int f = 0; f < 300 && view.ReleaseQueueDepth() > 0; f++) { view.LateUpdate(); Thread.Sleep(1); }
                Assert.AreEqual(0, view.ReleaseQueueDepth(), "sanity: the departing backlog must fully drain.");

                int oldTilesKicked = spy.BeginBuildCalls.FindAll(c => oldTiles.Contains(c.Tile)).Count;
                Assert.LessOrEqual(oldTilesKicked, 1,
                    "F-6 DECISIVE: a tile condemned before its kick must never reach TryBeginBuild — at most the " +
                    "single unavoidable race-window tile (kicked the SAME tick its condemnation registers, " +
                    "before the recompute runs) may appear; a missing _releaseQueued skip would eventually kick " +
                    $"ALL {oldTiles.Count} departed tiles while the release budget trickles them out.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
