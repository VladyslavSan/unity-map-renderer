// Unity EditMode only — drives the real MapView/TileManager Tick loop (admission + PumpPending). The
// FAST Core-only monotonicity/determinism teeth for the priority MATH itself live in TilePriorityTests
// (shared with Tools/core-tests); this file proves the PLUMBING — that TileManager actually admits/builds
// in priority order, respects the concurrency cap, never cancels an in-flight tile, re-prioritizes on a
// recompute, and honors the configured strategy end to end. Test names carry no stage IDs.
//
// T1/T2 are RED-verified against pre-stage HEAD (no concurrency cap, no priority order existed at all —
// admission was an unconditional immediate fetch in cover-descent order, and the paint-order kick ran over
// a plain Dictionary enumeration). T3-T5/T7 exercise genuinely NEW mechanisms the old code has no
// equivalent of.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class TileManagerLoadPriorityTests
    {
        private const int TestViewportPx = 1080; // matches WithTestCamera's default square viewport

        private static CameraProperties Cam(double lon, double lat, double zoom, double heading = 0.0,
            double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, heading, tilt);

        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""TileLoadPriority"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                }
            ]
        }");

        /// <summary>The lon/lat of a tile's CENTER (px=0.5, py=0.5) — used as camera LookAt so the tile's
        /// ground-distance-to-LookAt priority key is exactly 0 (the unique minimum, no boundary ambiguity).</summary>
        private static (double lon, double lat) CenterOf(TileId tile)
        {
            double2 ll = tile.ToLonLat(0.5, 0.5, 1.0);
            return (ll.x, ll.y);
        }

        /// <summary>A per-tile GATED data source: every fetch stays pending until <see cref="Release"/> is
        /// called for that specific tile — deterministic control over admission/consume timing (no reliance
        /// on ThreadPool wall-clock races).</summary>
        private sealed class GatedSource
        {
            private readonly Dictionary<TileId, UniTaskCompletionSource<TileResponse>> _gates = new();
            public readonly TestDataSource Source;

            public GatedSource()
            {
                Source = new TestDataSource((id, ct) =>
                {
                    var g = new UniTaskCompletionSource<TileResponse>();
                    lock (_gates) _gates[id] = g;
                    return g.Task;
                });
            }

            /// <summary>Completes every gate opened SO FAR with real tile bytes (idempotent — already-
            /// completed gates just no-op on TrySetResult).</summary>
            public void ReleaseAll()
            {
                List<UniTaskCompletionSource<TileResponse>> snapshot;
                lock (_gates) snapshot = new List<UniTaskCompletionSource<TileResponse>>(_gates.Values);
                foreach (var g in snapshot)
                    g.TrySetResult(new TileResponse(SampleTileFixture.Bytes(), TileEncoding.Mvt));
            }
        }

        // ── T1: WHICH tile builds first, by IDENTITY, under a 1-kick-per-tick budget ────────────────

        /// <summary>
        /// RED-verify: on pre-stage HEAD, PumpPending's kick loop ran over <c>_toRelease</c> in whatever
        /// order <c>Dictionary&lt;LoadedKey, LoadedTile&gt;</c> enumerated it — insertion order early,
        /// drifting after evictions — with no notion of "center".
        ///
        /// <para>Admitting the WHOLE cover in one Tick already inserts <c>_loaded</c> in priority order (A
        /// first), so a fixture that never disturbs that insertion order cannot tell "PumpPending sorts too"
        /// apart from "PumpPending just reads Dictionary order, which happens to already agree" — a shallow
        /// admission-only fix would pass a vacuous version of this test. This fixture defeats that: it admits
        /// the whole cover centered on tile A (insertion order == A-priority), then PANS to tile B — a
        /// DIFFERENT, already-admitted tile — BEFORE anything is kicked. The two explicit
        /// <c>MeshBuildsKickedLastTick() == 0</c> guards below prove the pan lands before any kick, so the
        /// assertion that follows can only pass if PumpPending re-sorts its OWN kick order by the CURRENT
        /// (B-centered) priority — insertion order alone (stale, A-centered) would still kick A.</para>
        /// </summary>
        [Test]
        public void CenterTileBuildsFirst_ByIdentity_UnderASingleBuildKickPerTick()
        {
            var tileA = new TileId { Z = 5, X = 12, Y = 13 };
            var tileB = new TileId { Z = 5, X = 13, Y = 12 }; // diagonal neighbor of A — stays in cover
            (double lonA, double latA) = CenterOf(tileA);
            (double lonB, double latB) = CenterOf(tileB);

            var gated = new GatedSource();
            var go    = new GameObject("T1_CenterFirst");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 1;   // the load-bearing cap
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 64;  // uncapped-in-practice admission — isolates the kick/paint order

            try
            {
                view.LoadTestStyle(gated.Source, Cam(lonA, latA, 5.0), style: FillStyle());

                // Tick 1: admits the WHOLE cover (insertion order == A-priority order). Every fetch is held
                // PENDING by the gate, so nothing can be kicked yet regardless of ThreadPool timing. (An
                // earlier instant-bytes source let fetches race to completion in a nondeterministic order, so
                // PumpPending could kick a non-center tile whose fetch happened to land first — an
                // intermittent failure this gated fixture removes.)
                view.LateUpdate();
                Assert.GreaterOrEqual(view.LoadedTileCount(), 5,
                    "sanity: need a genuinely multi-tile cover for the cap to mean anything.");
                Assert.AreEqual(0, view.MeshBuildsKickedLastTick(),
                    "guard: nothing may be kicked yet — the pan below must land strictly BEFORE the first kick.");

                var loadedBefore = new List<TileId>();
                view.CollectLoadedTileIds(loadedBefore);
                Assert.Contains(tileB, loadedBefore,
                    "precondition: B must already be admitted (in _loaded) before the pan.");

                // Pan onto B — still before anything kicks (second guard, right below).
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = latB, Longitude = lonB });
                view.LateUpdate();
                Assert.AreEqual(0, view.MeshBuildsKickedLastTick(),
                    "guard: still nothing kicked after the pan — if this fires, the fixture raced ahead of " +
                    "the pan and the test below would be vacuous.");

                // NOW complete every fetch at once — so by the first kicking tick EVERY cover tile is
                // fetch-complete and PumpPending's priority sort ranks the FULL set, not just whichever fetch
                // won a race. With all candidates present, the B-centered sort must kick B first.
                gated.ReleaseAll();

                // Pump to the FIRST tick that kicks anything at all.
                int frames = 0;
                while (view.MeshBuildsKickedLastTick() == 0 && frames++ < 3000)
                {
                    view.LateUpdate();
                    if (view.MeshBuildsKickedLastTick() > 0) break;
                    view.AwaitInFlightMeshBuilds();
                }

                Assert.AreEqual(1, view.MeshBuildsKickedLastTick(),
                    "precondition: exactly one kick must fire on the first kicking tick (cap=1).");

                // Let that ONE kicked build complete and consume on the NEXT tick, without giving a second
                // kick's build time to complete too.
                view.AwaitInFlightMeshBuilds();
                view.LateUpdate();

                Assert.IsTrue(view.TryGetBuiltTile(tileB),
                    "tile B — the NEW center after the pan — must be the FIRST tile built under a " +
                    "1-kick-per-tick cap, even though A was admitted (inserted) first.");
                Assert.IsFalse(view.TryGetBuiltTile(tileA),
                    "tile A must NOT be built yet — it is no longer the highest priority after the pan.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "sanity: the cover has more than one tile — only B should be built yet.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── T2: concurrency cap respected across a full (eventually-settling) load ──────────────────

        /// <summary>
        /// RED-verify: pre-stage HEAD has no concurrency cap anywhere — a cover-wide cover/zoom transition
        /// fetches every newly-entering tile in one Tick, so the active (admitted, not-yet-Built) set jumps
        /// straight to the full cover size. A per-tile GATE holds every fetch open (never completing) so the
        /// active set can only GROW via admission, never shrink via completion — isolating the cap.
        /// </summary>
        [Test]
        public void ActiveLoadCount_NeverExceedsTheConcurrencyCap_AcrossAFullLoad()
        {
            const int cap = 4;
            var gated = new GatedSource();
            var go    = new GameObject("T2_ConcurrencyCap");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 6;
            view.Config.TileSelection.MaxZoom = 6; // a cover clearly larger than the cap
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = cap;

            try
            {
                view.LoadTestStyle(gated.Source, Cam(0, 0, 6.0), style: FillStyle());

                int maxObservedActive = 0;
                for (int f = 0; f < 20; f++)
                {
                    view.LateUpdate();
                    int active = view.ActiveLoadCount();
                    Assert.LessOrEqual(active, cap, $"tick {f}: active load count must never exceed the cap.");
                    if (active > maxObservedActive) maxObservedActive = active;
                }

                Assert.Greater(view.LoadedTileCount() + view.DesiredCount(), cap,
                    "sanity: the cover must be LARGER than the cap, or the cap was never genuinely tested.");
                Assert.AreEqual(cap, maxObservedActive,
                    "the cap must actually BIND — with every fetch gated open, admission must climb to " +
                    "exactly the cap and stop there (not fewer — a starved cap is also a failure).");
                Assert.AreEqual(cap, view.ActiveLoadCount(),
                    "with nothing completing, the active set must sit AT the cap, not below it.");

                // Release every gate (as new ones open too) and drive to full settle — the cap must throttle
                // the load, never permanently stall it.
                for (int f = 0; f < 500 && !view.AllTilesSettled(); f++)
                {
                    view.LateUpdate();
                    gated.ReleaseAll();
                    view.AwaitInFlightMeshBuilds();
                    Assert.LessOrEqual(view.ActiveLoadCount(), cap,
                        $"tick {f} (settling): active load count must never exceed the cap.");
                }

                Assert.IsTrue(view.AllTilesSettled(),
                    "the cover must eventually fully settle despite the concurrency cap.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── T3: no-cancel-in-flight on a recompute that keeps the tile visible ──────────────────────

        /// <summary>
        /// An admitted, in-flight tile that stays visible across a cover recompute must NOT be released and
        /// re-fetched: its per-tile fetch cancellation token must never fire. A sub-tile camera nudge dirties
        /// the cover key (forcing a recompute) without changing the selected tile SET.
        /// </summary>
        [Test]
        public void AdmittedInFlightTile_SurvivesARecompute_ThatKeepsItVisible()
        {
            var canceled = new HashSet<TileId>();
            var gates    = new Dictionary<TileId, UniTaskCompletionSource<TileResponse>>();
            var src = new TestDataSource((id, ct) =>
            {
                var g = new UniTaskCompletionSource<TileResponse>();
                lock (gates) gates[id] = g;
                ct.Register(() => { lock (canceled) canceled.Add(id); g.TrySetCanceled(ct); });
                return g.Task;
            });

            var centerTile = new TileId { Z = 5, X = 12, Y = 13 };
            (double lon, double lat) = CenterOf(centerTile);

            var go   = new GameObject("T3_NoCancelInFlight");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick     = 64;
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64; // admit the whole cover — every tile is "in-flight"

            try
            {
                view.LoadTestStyle(src, Cam(lon, lat, 5.0), style: FillStyle());
                view.LateUpdate(); // admits the cover (all gated — nothing completes)
                Assert.IsTrue(view.TryGetBuiltTile(centerTile) == false && view.LoadedTileCount() > 0,
                    "sanity: the cover must be admitted (in-flight), not yet built.");
                int loadedBefore = view.LoadedTileCount();

                // Sub-tile nudge: dirties _coverDirty (exact cover-key equality trips) without changing the
                // selected tile set (far smaller than any tile in a z5 cover).
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = lat + 1e-7, Longitude = lon - 1e-7 });
                view.LateUpdate();

                Assert.IsFalse(canceled.Contains(centerTile),
                    "an admitted tile that stays visible across a recompute must NEVER have its fetch " +
                    "cancelled (no release + re-fetch behind new center tiles).");
                Assert.AreEqual(loadedBefore, view.LoadedTileCount(),
                    "the loaded set must be unchanged — the recompute must not drop and re-admit anything " +
                    "still visible.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── T4: re-prioritization head — a recompute promotes the NEW center ────────────────────────

        /// <summary>
        /// With admission capped to 1 (so most of the cover stays in the not-yet-admitted desired list), a
        /// recompute that moves LookAt onto a previously-peripheral tile must promote THAT tile to the head
        /// of the desired list on the SAME tick — proving re-prioritization isn't deferred to "next slot
        /// free" (AdmitFromDesired sorts before checking capacity, not after).
        /// </summary>
        [Test]
        public void RecomputeThatMovesTheCenter_PromotesTheNewCenter_ToTheDesiredHead()
        {
            var gated = new GatedSource();
            var centerA = new TileId { Z = 5, X = 12, Y = 13 };
            var centerB = new TileId { Z = 5, X = 13, Y = 12 }; // diagonal neighbor — was peripheral to A
            (double lonA, double latA) = CenterOf(centerA);
            (double lonB, double latB) = CenterOf(centerB);

            var go   = new GameObject("T4_Repriority");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 1; // only ONE tile ever admits — the rest stays desired

            try
            {
                view.LoadTestStyle(gated.Source, Cam(lonA, latA, 5.0), style: FillStyle());
                view.LateUpdate();

                Assert.AreEqual(1, view.ActiveLoadCount(), "precondition: exactly one slot admitted.");
                Assert.Greater(view.DesiredCount(), 1,
                    "sanity: several tiles must remain desired for re-prioritization to be observable.");

                var desiredBefore = new List<TileId>();
                view.CollectDesiredTileIds(desiredBefore);
                Assert.IsTrue(desiredBefore.Contains(centerB),
                    "precondition: B must be among the not-yet-admitted desired tiles before the pan.");

                // Pan LookAt onto B (previously peripheral). The sole admitted slot is gated (held forever
                // by A, which is still visible in the new cover too — no cancel), so B stays UNADMITTED.
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = latB, Longitude = lonB });
                view.LateUpdate();

                Assert.AreEqual(centerB, view.DesiredHeadTile(),
                    "after a recompute that moves LookAt onto B, B must be the HEAD of the desired list " +
                    "(ground-distance-to-LookAt key == 0 for the tile AT LookAt) — re-sorted THIS tick, not " +
                    "deferred until a slot frees.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── T5: strategy toggle is honored end to end, and the two strategies genuinely diverge ────

        /// <summary>
        /// Under a real tilt, GroundDistanceToLookAt and CameraDistance can disagree about which tile is
        /// closest. This derives the expected argmin under EACH strategy directly from the production
        /// <see cref="TilePriority.Key"/> math (already pinned independently by the Core-only
        /// TilePriorityTests) over the SAME cover TileManager actually assembles, then asserts (a) the two
        /// strategies' derived answers genuinely differ — the fixture is discriminating, not vacuous — and
        /// (b) switching <c>PriorityStrategy</c> changes which tile TileManager actually admits first,
        /// matching the derived answer in both cases. This is a WIRING tooth, not a re-test of the math.
        /// </summary>
        [Test]
        public void PriorityStrategyToggle_ChangesTheFirstAdmittedTile_UnderTilt()
        {
            var centerTile = new TileId { Z = 6, X = 30, Y = 25 };
            (double lon, double lat) = CenterOf(centerTile);
            const double tilt = 60.0;

            // ── Run A: GroundDistanceToLookAt (default) ──
            TileId admittedGround = RunToFirstAdmission(lon, lat, tilt, TilePriorityStrategy.GroundDistanceToLookAt,
                out List<TileId> cover);

            // ── Run B: CameraDistance ──
            TileId admittedCamera = RunToFirstAdmission(lon, lat, tilt, TilePriorityStrategy.CameraDistance,
                out _);

            // Derive the expected argmin under each strategy independently, over the SAME cover, using the
            // exact production math (not a re-implementation) — matches FrustumTileSelector's render frame.
            var cam    = Cam(lon, lat, 6.0, heading: 0.0, tilt: tilt);
            var proj   = new WebMercatorProjection();
            var vp     = new double2(TestViewportPx, TestViewportPx);
            var ctxG   = TilePriorityContext.From(in cam, vp, proj, TilePriorityStrategy.GroundDistanceToLookAt);
            var ctxC   = TilePriorityContext.From(in cam, vp, proj, TilePriorityStrategy.CameraDistance);

            TileId expectedGround = ArgMin(cover, in ctxG);
            TileId expectedCamera = ArgMin(cover, in ctxC);

            Assert.AreNotEqual(expectedGround, expectedCamera,
                "fixture sanity: at tilt=60 the two strategies must derive DIFFERENT argmins over this " +
                "cover, or this fixture cannot discriminate the toggle at all.");

            Assert.AreEqual(expectedGround, admittedGround,
                "GroundDistanceToLookAt: TileManager's actual first-admitted tile must match the derived argmin.");
            Assert.AreEqual(expectedCamera, admittedCamera,
                "CameraDistance: TileManager's actual first-admitted tile must match the derived argmin.");
            Assert.AreNotEqual(admittedGround, admittedCamera,
                "switching PriorityStrategy must change WHICH tile TileManager admits first.");
        }

        private static TileId ArgMin(List<TileId> tiles, in TilePriorityContext ctx)
        {
            TileId best    = tiles[0];
            double bestKey = TilePriority.Key(in best, in ctx);
            for (int i = 1; i < tiles.Count; i++)
            {
                TileId candidate = tiles[i];
                double k         = TilePriority.Key(in candidate, in ctx);
                if (k < bestKey) { bestKey = k; best = candidate; }
            }
            return best;
        }

        /// <summary>Loads a fresh view capped to a single admission slot (gated — never completes), pumps
        /// one Tick, and returns the SOLE admitted tile plus the full cover (admitted ∪ desired).</summary>
        private static TileId RunToFirstAdmission(double lon, double lat, double tilt,
            TilePriorityStrategy strategy, out List<TileId> cover)
        {
            var gated = new GatedSource();
            var go    = new GameObject("T5_StrategyToggle");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom  = 6;
            view.Config.TileSelection.MaxZoom  = 6;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 1;
            view.Config.PriorityStrategy          = strategy;

            try
            {
                view.LoadTestStyle(gated.Source, Cam(lon, lat, 6.0, heading: 0.0, tilt: tilt), style: FillStyle());
                view.LateUpdate();

                Assert.AreEqual(1, view.ActiveLoadCount(), "precondition: exactly one slot admitted.");
                var loaded = new List<TileId>();
                view.CollectLoadedTileIds(loaded);

                cover = new List<TileId>(loaded);
                var desired = new List<TileId>();
                view.CollectDesiredTileIds(desired);
                cover.AddRange(desired);
                Assert.GreaterOrEqual(cover.Count, 5, "sanity: need a genuinely multi-tile cover.");

                return loaded[0];
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── T7: the admission/sort path stays allocation-free under sustained churn ─────────────────

        /// <summary>
        /// A capped, gated load keeps the desired list populated across many Ticks (nothing ever completes,
        /// so <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.AdmitFromDesired"/>'s sort+admit-attempt
        /// runs every Tick against a non-empty list) — the scenario the steady-state zero-alloc tooth in
        /// MapViewLiveLoopTests doesn't reach (there, admission is uncapped and settles in one Tick).
        /// </summary>
        [Test]
        public void AdmissionAndPrioritySort_UnderSustainedChurn_DoesNotAllocateGCMemory()
        {
            var gated = new GatedSource();
            var go    = new GameObject("T7_ZeroAlloc");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend                = RenderBackend.Brg; // zero-alloc path
            view.Config.TileSelection.MinZoom  = 6;
            view.Config.TileSelection.MaxZoom  = 6;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 3; // capped — desired stays populated (gated, never frees)

            try
            {
                view.LoadTestStyle(gated.Source, Cam(0, 0, 6.0), style: FillStyle());
                view.LateUpdate(); // first Tick — grows the reused scratch buffers to steady capacity
                Assert.Greater(view.DesiredCount(), 0,
                    "sanity: the desired list must stay non-empty (gated fetches never free a slot) for " +
                    "the sort/admit-attempt path to actually run every Tick.");

                const int N = 30;
                Assert.That(() => { for (int i = 0; i < N; i++) view.LateUpdate(); }, Is.Not.AllocatingGCMemory(),
                    $"AdmitFromDesired's priority sort + admit-attempt must not allocate across {N} Ticks " +
                    "of sustained desired-list churn (capped, nothing completing).");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
