// S95 (residual tile-load frame stall) — the MEASURE-FIRST headless half. Does NOT implement either
// candidate fix (sub-tile cover-recompute throttle / requests-per-frame cap) — those are deliberately
// deferred until a maintainer Profiler trace attributes the spike (decision 1). These teeth are
// UNCONDITIONAL guards (decision 2): they must hold on ANY build, fixed or not.
//
// Unity-only (UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() — the ONLY trustworthy
// allocation meter on Unity Mono). Excluded from core-tests.csproj.
//
// Tooth (a): TileScheduler.Request is alloc-free on the cache-hit / in-flight-share fast paths.
// Tooth (b): MapView.LateUpdate's cover-recompute (select descent + request/release diff) is alloc-free at
//            STALL SCALE — a high-zoom (z12+), ScreenSpaceLod, large mixed-zoom cover mirroring
//            TileLoadStressDriver's Berlin sweep — NOT a re-run of MapViewLiveLoopTests' shallow z2 case.
// Tooth (d): CoverRecomputesLastTick sums to N over N sub-tile nudges — a BASELINE documenting today's
//            "every dirty Tick recomputes" behaviour (no throttle exists yet), not the post-fix bound.
//
// Tooth (c) (consume-tick alloc-free) is NOT duplicated here — it is already guaranteed by
// MapViewLiveLoopTests.MapView_SteadyStateTick_DoesNotAllocateGCMemory (asserts
// Is.Not.AllocatingGCMemory() over the budgeted-consume Tick in the all-built steady state, including an
// N=50 sweep). This stage keeps that green rather than re-asserting the same property.

using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class TileLoadMeasurementTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""S95"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": {
                        ""fill-color"": [""rgba"", 200, 50, 50, 1]
                    }
                }
            ]
        }");

        /// <summary>Pumps Tick() until every loaded tile has settled or a spin budget is hit.</summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
                Thread.Sleep(1);
            }
        }

        // ── Tooth (a): TileScheduler.Request fast paths are alloc-free ─────────────────────────────

        /// <summary>
        /// Cache-hit Request must not allocate. Falsifiable: a shallow re-fetch-every-time
        /// implementation, or one that re-wraps the cached value in a new allocating wrapper instead of
        /// returning <c>UniTask.FromResult(cached)</c> directly, fails. Expected to PASS on current code
        /// (per code inspection of the vendored UniTask — <c>Preserve()</c>/<c>FromResult</c> short-circuit
        /// for a non-null-source task) — this stands as a permanent regression guard.
        /// </summary>
        [Test]
        public void TileScheduler_Request_CacheHit_IsAllocFree()
        {
            byte[] bytes     = SampleTileFixture.Bytes();
            var    src       = TestDataSource.FromBytes(bytes);
            var    cache     = new TileCache(capacity: 16);
            var    scheduler = new TileScheduler(src, cache);
            var    id        = new TileId { Z = 5, X = 10, Y = 11 };

            // Warm-up: the FIRST Request is a genuine new fetch (CancellationTokenSource.CreateLinkedTokenSource
            // + FetchAndCacheAsync(...).Preserve()) — legitimately allocates. Drive it to completion so
            // cache.Put has already run (FetchAndCacheAsync's SwitchToThreadPool + lock + Put happen BEFORE
            // the outer UniTask's IsCompleted flips true — no race with the assertion below).
            var first  = scheduler.Request(id);
            int spins  = 0;
            while (!first.Status.IsCompleted() && spins++ < 10000) Thread.Sleep(1);
            Assert.IsTrue(first.Status.IsCompleted(), "warm-up fetch must complete before measuring the cache-hit path.");

            // Block-bodied lambda (not an expression lambda): Request returns a value, and Assert.That needs
            // a void TestDelegate here, not a Func<UniTask<TileResponse>> — an expression lambda would bind
            // to the wrong Assert.That overload and fail with "actual value must be a TestDelegate".
            Assert.That(() => { scheduler.Request(id); }, Is.Not.AllocatingGCMemory(),
                "Cache-hit Request must not allocate — UniTask.FromResult(cached) must return the memoized " +
                "value directly, never re-wrap it or re-fetch from the source.");
        }

        /// <summary>
        /// In-flight-share Request (two concurrent calls for the same not-yet-settled id) must return the
        /// SAME preserved UniTask with no new allocation on the second call. Falsifiable: a per-call
        /// re-wrap, or a fresh CTS/dictionary entry per call, fails.
        /// </summary>
        [Test]
        public void TileScheduler_Request_InFlightShare_IsAllocFree()
        {
            // Gated source (TileFetchCancellationTests' pattern): stays in-flight until we complete it —
            // FromBytes would complete immediately and could never exercise the in-flight-share branch.
            var gate      = new UniTaskCompletionSource<TileResponse>();
            var src       = new TestDataSource((id, ct) => gate.Task);
            var cache     = new TileCache(capacity: 16);
            var scheduler = new TileScheduler(src, cache);
            var tileId    = new TileId { Z = 5, X = 1, Y = 1 };

            // Warm-up: the FIRST Request creates the in-flight entry (CTS + FetchAndCacheAsync(...).Preserve())
            // — legitimately allocates. It stays pending because the gated source never completes.
            var first = scheduler.Request(tileId);
            Assert.IsFalse(first.Status.IsCompleted(),
                "warm-up call must genuinely be in-flight (source gated) for this tooth to have teeth.");

            // Block-bodied lambda — see the cache-hit tooth above for why (Request returns a value).
            Assert.That(() => { scheduler.Request(tileId); }, Is.Not.AllocatingGCMemory(),
                "In-flight-share Request must return the existing preserved UniTask with zero new allocation.");

            // Cleanup: settle the gate so nothing dangles (no exception — no unobserved-exception concern).
            gate.TrySetResult(TileResponse.Absent(TileEncoding.Mvt));
        }

        // ── Tooth (b): deep-cover select is alloc-free AT STALL SCALE ───────────────────────────────

        /// <summary>
        /// Drives a real <c>ScreenSpaceLod</c> quadtree descent at stall scale (high zoom, real tilt,
        /// Berlin params mirroring <see cref="TileLoadStressDriver"/>) and asserts a sub-tile camera nudge
        /// — same tile set stays selected, but <c>_coverDirty</c> still trips — ticks alloc-free.
        ///
        /// This is NOT a re-run of <c>MapViewLiveLoopTests.MapView_SteadyStateTick_DoesNotAllocateGCMemory</c>
        /// (that test's z2 whole-world cover is real, valuable prior art but never exercises the deep
        /// descent / mixed-zoom fan a tilted high-zoom camera produces — a tilt=0 top-down cover would
        /// degenerate toward that same shallow case, since every tile then sits at ~the same distance from
        /// the camera and the ScreenSpaceLod stop condition stops them all at ~the same zoom).
        /// </summary>
        [Test]
        public void MapView_DeepCoverSelect_SubTileNudge_IsAllocFreeAtStallScale()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_S95_DeepCover");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.Backend                 = RenderBackend.Brg; // zero-alloc contract; avoid EG's intermittent alloc noise
            view.Config.TileSelection.MinZoom                 = 0;
            view.Config.TileSelection.MaxZoom                 = 14;    // matches TileLoadStressDriver.MaxZoom / MapViewConfig's default
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 256;
            view.Config.MaxMeshBuildsPerTick = 256;
            view.Config.MaxVerticesPerTick      = int.MaxValue;

            try
            {
                // Berlin, high zoom, real tilt — TileLoadStressDriver's Berlin params (CenterLatitude=52.52,
                // CenterLongitude=13.405, MaxZoom=14). Tilt=60 (near the [0,90] clamp) is deliberate: at
                // tilt=0 every tile in a top-down footprint sits at ~the same distance from the camera, so
                // ScreenSpaceLodStrategy's stop condition (GroundSize <= ScreenRatio * Distance) stops them
                // all at ~the same zoom — a near-uniform cover that degenerates toward the shallow z2 case.
                // A real tilt makes far tiles recede toward the horizon (stop coarse) while near tiles
                // descend to z14 — a genuine mixed-zoom fan, exercising many more quadtree levels.
                var cam = new CameraProperties(
                    new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52, Altitude = 0 },
                    zoom: 14.0, heading: 0.0, tilt: 60.0);

                view.LoadTestStyle(src, cam, style: style);
                PumpUntilSettled(view, maxFrames: 5000);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // ── Teeth: prove this is genuinely the deep/mixed/large cover the stage demands ──
                // (not a silent re-run of the shallow z2/16-tile case with different numbers).
                var telemetry = view.CaptureTelemetry();
                Assert.GreaterOrEqual(telemetry.CoverMaxZoom, 12,
                    $"Cover must reach z12+ (stall-scale near field) — got CoverMaxZoom={telemetry.CoverMaxZoom}.");
                Assert.IsTrue(telemetry.IsMixedZoom,
                    "Cover must be mixed-zoom (a real ScreenSpaceLod fan) — a uniform-zoom cover would just " +
                    "be the shallow z2 case at a bigger number, proving nothing new.");
                Assert.Greater(telemetry.VisibleTileCount, 30,
                    $"Cover must be large (many quadtree nodes visited) — got VisibleTileCount={telemetry.VisibleTileCount}.");

                // Prime the reused buffers (_cover, _coverSet, _toRelease, the selector's _stack) to steady
                // capacity before measuring — mirrors MapViewLiveLoopTests' priming ticks.
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = 52.52 + 1e-6, Longitude = 13.405 + 1e-6 });
                view.LateUpdate();
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = 52.52,        Longitude = 13.405 });
                view.LateUpdate();

                int loadedBefore = view.LoadedTileCount();

                // Sub-tile nudge (~10 cm) — far smaller than any tile in this cover (the finest, z14, is
                // ~1.5 km even at Berlin's latitude): the same tile set stays selected, but the cover-key
                // exact-equality check in TileManager.Tick still trips _coverDirty.
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = 52.52 + 1e-6, Longitude = 13.405 - 1e-6 });

                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "MapView.LateUpdate must not allocate during a sub-tile nudge over a deep (z12+), mixed-zoom, " +
                    "large ScreenSpaceLod cover — the recompute path (quadtree descent + request/release " +
                    "diff) at STALL SCALE, not the shallow z2 case. A failure means the descent boxes a LOD " +
                    "context, allocates a scratch list per call, or LINQs over the cover.");

                // Guards against a vacuous pass: prove the recompute actually ran (not an early-out) and the
                // cover is unchanged (no edge tile crossed — which would have issued a real, legitimately
                // allocating Request/Release, a different cost than the one this tooth measures).
                Assert.AreEqual(1, view.CoverRecomputesLastTick(),
                    "The measured Tick must have run the full recompute (CoverRecomputesLastTick == 1) — " +
                    "otherwise the alloc-free assertion above passed vacuously via an early-out.");
                Assert.AreEqual(loadedBefore, view.LoadedTileCount(),
                    "The sub-tile nudge must not change the loaded tile set — otherwise the request/release " +
                    "diff legitimately allocated a new fetch, a different, unrelated cost from this tooth.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth (d): CoverRecomputesLastTick baseline discriminator ───────────────────────────────

        /// <summary>
        /// BASELINE, not the post-fix bound: drives N sub-tile camera nudges and asserts
        /// <c>CoverRecomputesLastTick</c> sums to exactly N — today, with no throttle, every dirty Tick
        /// recomputes. This documents current behaviour as a regression guard on the counter itself (if it
        /// ever silently under-reports, this catches it). A future sub-tile-recompute throttle (S95
        /// decision 4a — NOT built by this stage) would make this assertion fail; that fix's own test
        /// (added when it lands) asserts <c>recomputes &lt; N</c> instead — see decision 5 / §4's
        /// "decisive test" note in the stage doc.
        /// </summary>
        [Test]
        public void CoverRecomputesLastTick_SumsToN_ForNSubTileNudges_BaselineNoThrottleYet()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_S95_Counter");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5; // cheap z5 cover — this tooth is about the counter, not descent cost
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must settle before driving the nudge loop.");

                const int N = 8;
                int recomputes = 0;
                double lat = 0.0;
                for (int i = 0; i < N; i++)
                {
                    lat += 1e-6; // sub-tile nudge each iteration — same tile stays selected, _coverDirty trips
                    view.Camera.Apply(new CameraPropertiesUpdate { Latitude = lat });
                    view.LateUpdate();
                    recomputes += view.CoverRecomputesLastTick();
                }

                Assert.AreEqual(N, recomputes,
                    $"Baseline: with no throttle yet, every sub-tile-dirtied Tick must run the full recompute " +
                    $"(CoverRecomputesLastTick must sum to {N} across {N} nudges; got {recomputes}). If this " +
                    "ever reads less than N without an explicit throttle fix landing, the counter itself has " +
                    "a false negative.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── Stall #6 fix: a CLEAN camera with tiles still pending must NOT re-run the cover recompute ──

        /// <summary>
        /// The stall-#6 gate change: the expensive cover recompute (quadtree descent + request/release diff)
        /// is now gated on <c>_coverDirty</c> ALONE — a static camera with tiles still in-flight no longer
        /// forces it every frame. A GATED data source keeps every requested tile pending across ticks (its
        /// fetch only completes on cancellation), so <c>pending &gt; 0</c> holds while the camera stays put.
        ///
        /// <para>Falsifiable: BEFORE the fix the gate was <c>!_coverDirty &amp;&amp; pending == 0</c>, so
        /// <c>pending &gt; 0</c> forced the full recompute (CoverRecomputesLastTick == 1) every frame for zero
        /// effect (the cover set is unchanged → the request/release loops are pure no-ops). This test asserts
        /// 0 across those frames — it FAILS on the pre-fix gate. <c>PumpPending</c> still runs each tick, so
        /// tiles keep progressing; this removes wasted work, it does not stall the pipeline.</para>
        /// </summary>
        [Test]
        public void CoverRecompute_CleanCameraWithPendingTiles_IsSkipped_Stall6Fix()
        {
            // Per-tile gated fetch: stays pending until the tile's own token is cancelled (Release/teardown),
            // so no shared-task double-consume, and teardown cancels each → prompt, no 10 s spin.
            var src  = new TestDataSource((id, ct) =>
            {
                var utcs = new UniTaskCompletionSource<TileResponse>();
                ct.Register(() => utcs.TrySetCanceled(ct));
                return utcs.Task;
            });
            var go   = new GameObject("MapView_S95_CleanPending");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

                // Establish the cover + request the tiles (they then hang on the gated fetch).
                view.LateUpdate();
                Assert.Greater(view.LoadedTileCount(), 0, "cover established → tiles requested on the first tick.");
                Assert.IsFalse(view.AllTilesSettled(), "gated source → requested tiles stay pending across ticks.");

                // Same camera, tiles still pending: the recompute must be skipped every frame.
                for (int i = 0; i < 5; i++)
                {
                    view.LateUpdate();
                    Assert.IsFalse(view.AllTilesSettled(), "sanity: tiles remain pending (tokens not cancelled).");
                    Assert.AreEqual(0, view.CoverRecomputesLastTick(),
                        "clean camera + pending tiles must NOT re-run the cover recompute (was 1 pre-fix: " +
                        "pending>0 forced the descent + request/release diff every frame for zero effect).");
                }
            }
            finally
            {
                view.Teardown(); // cancels each tile's token → the gated fetches complete promptly
                Object.DestroyImmediate(go);
            }
        }
    }
}
