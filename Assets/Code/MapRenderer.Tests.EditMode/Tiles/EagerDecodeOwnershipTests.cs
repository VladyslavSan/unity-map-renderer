// Unity EditMode only — drives a real TileManager through MapView over the committed fixture, with a fake
// ITileFeatureSource carrying a probe decoder. NOT registered in core-tests.csproj.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using CoreMapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// <b>The leak class of the eager decode, one test per abandonment funnel.</b>
    ///
    /// <para>Eager decode trades a re-decode for a leak. Under the retired scoped lease every drop path —
    /// a never-kicked record, a discarded fetch, a restyle, teardown — dropped a handle that had never
    /// decoded, so there was nothing to free and not one of those paths needed an edit. Now the tile is
    /// already built and holding <c>Allocator.Persistent</c> buffers by the time any of them can drop it, so
    /// every one of them is an OWNER with a release in it. These tests are what makes that claim
    /// falsifiable.</para>
    ///
    /// <para><b>One test per funnel, not one combined test.</b> A single "nothing leaks" case could not name
    /// WHICH funnel failed, and a leak in one of four is exactly the failure mode that survives review.</para>
    ///
    /// <para><b>Every case asserts <c>UnbalancedCount == 0</c> AND that a decode actually happened.</b> The
    /// probe counts a tile as unbalanced when its dispose count is anything other than one, so it catches a
    /// double release as well as a missed one — but over a fixture that never decoded it is trivially zero.
    /// The anti-vacuity precondition is therefore load-bearing, and in most cases it is stronger than a bare
    /// count: the tiles are asserted ALIVE at the point just before the funnel runs, so the only thing that
    /// can have freed them by the end is the funnel under test.</para>
    ///
    /// <para><b>No production observability was added for any of this.</b> The instrument is a probe
    /// <c>ITileDecoder</c> injected through a fake <c>ITileFeatureSource</c> at <c>SetSources</c>'
    /// <c>CreateSource</c> seam, which already exists — see <c>LeaseProbeDecoder</c>.</para>
    ///
    /// <para><b>The two release sites this fixture used to hand to a structural clause are runtime-covered
    /// now, and the clauses that remain pin something else.</b> <c>PumpBuilds</c>' parked-drain ct-drop is
    /// driven for real in <c>SymbolParkedRedecodeTests</c> — reflecting the private cancellation source and
    /// cancelling WITHOUT draining reproduces exactly the pool-vs-main interleaving that was called
    /// unreachable — and <c>KickMeshBuild</c>'s main-thread prologue no longer HAS a release to pin: its
    /// catch was deleted rather than tested, because the caller has not given up its reference at that point
    /// and releasing there was an over-release, not a leak guard. What <c>TileProcessingStructureTests</c>
    /// keeps is the residue no runtime test can reach: that both kick call sites feed the record's own field
    /// and null it only afterwards, that the parked dispatch takes no cancellation token, and that
    /// <c>RenderTeardownRecord</c> disarms before it fires.</para>
    /// </summary>
    [TestFixture]
    public class EagerDecodeOwnershipTests
    {
        private const string SourceId = "s";

        // The z5 cover around (0, 0) — 9 tiles, the same cover TileSymbolKickTests drives.
        private const int CoverZoom = 5;
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""layers"": [
                { ""id"":""fill"", ""type"":""fill"", ""source"":""s"", ""source-layer"":""countries"",
                  ""paint"": { ""fill-color"": ""#ffffff"" } }
            ]
        }");

        /// <summary>
        /// A fake source that decodes through <see cref="TileDecodeDispatch"/> exactly as production does —
        /// same pool hop, same lease, same one reference handed to the caller — with a probe decoder inside
        /// it and two switches the drives need.
        ///
        /// <para><see cref="Serving"/> is what keeps each tooth's arithmetic honest: once it goes false every
        /// later <see cref="GetTile"/> answers null, so a cover change cannot quietly decode a SECOND set of
        /// tiles whose references are still legitimately held at assertion time and would read as unbalanced.
        /// The tooth is then measuring exactly the tiles it set up.</para>
        ///
        /// <para><see cref="Gate"/>, when set, suspends the fetch before the decode — the only way to reach
        /// the "released while its fetch was still in flight" funnel.</para>
        /// </summary>
        private sealed class ProbeFeatureSource : ITileFeatureSource
        {
            private readonly byte[] _bytes;
            private static readonly IWorkScheduler Scheduler = new ThreadPoolWorkScheduler();

            internal readonly LeaseProbeDecoder Probe = new LeaseProbeDecoder();
            internal bool Serving = true;
            internal UniTaskCompletionSource Gate;

            internal ProbeFeatureSource(byte[] bytes) => _bytes = bytes;

            public UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default)
            {
                if (!Serving) return UniTask.FromResult<SharedDisposable<IDecodedTile>>(null);
                return Fetch(id);
            }

            private async UniTask<SharedDisposable<IDecodedTile>> Fetch(TileId id)
            {
                UniTaskCompletionSource gate = Gate;
                if (gate != null) await gate.Task;
                return await TileDecodeDispatch.DecodeAsync(id, _bytes, Probe, Scheduler);
            }

            public void Release(TileId id) { }
            public int InFlightCount => 0;
            public void Dispose() { }
        }

        /// <summary>Wires an <see cref="ITileFeatureSource"/> DIRECTLY through the production
        /// <c>SetSources</c> entry — the same seam <c>A7TileFeatureSourceTests</c> uses, and the reason no
        /// production observability is needed for any of this.</summary>
        private static void LoadStyleWithSource(MapView view, ITileFeatureSource source, CameraProperties cam)
        {
            CoreMapView mv = view.View;
            mv.Camera.SetProperties(cam);
            mv.Camera.SyncToCamera();
            mv.Layers.Build(FillStyle(), mv.Camera.CurrentProperties.Zoom, view.Config.MaterialSet);
            var specs = new List<TileManager.SourceSpec>
            {
                new TileManager.SourceSpec(SourceId, default, 0, int.MaxValue, () => source),
            };
            mv.TileManager.SetSources(specs, view.Config.Backend);
        }

        /// <summary>A view whose mesh-build cap is ONE, so a multi-tile cover is fetched and decoded far
        /// faster than it is kicked and most records sit there holding a decoded tile they never dispatch —
        /// the state the never-kicked funnels exist for, and the state that had no analogue at all under the
        /// scoped lease.
        ///
        /// <para><b>Not zero.</b> <c>TileManager.PumpPending</c> reads a cap of 0 as UNLIMITED
        /// (<c>maxMeshBuildsPerTick &gt; 0 ? … : int.MaxValue</c>), so setting it to zero kicks
        /// <i>everything</i> and every reference is transferred and released by a lambda — the exact opposite
        /// of the state under test, and a way for all four teeth here to pass while measuring nothing.</para></summary>
        private static MapView NewViewWithSlowKicks(string name)
        {
            var go   = new GameObject(name);
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = CoverZoom;
            view.Config.TileSelection.MaxZoom = CoverZoom;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxReleasesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 1; // one kick per tick against a 16-tile cover
            return view;
        }

        /// <summary>
        /// Drives the cover into the exact state the never-kicked funnels exist for — every tile fetched,
        /// decoded and sitting in its record, un-kicked — and asserts that state before handing back the
        /// number of tiles being held.
        ///
        /// <para><b>The gate is what makes it deterministic, and it is not optional.</b> The obvious drive —
        /// pump ticks until the decodes land — spreads fetch completion across many ticks, and a tick that
        /// observes a fetch also kicks one. Over the ~100 ticks a 16-tile cover can take, every tile ends up
        /// kicked and its reference transferred to a pool lambda, so the funnel under test has nothing left
        /// to release and the tooth passes measuring nothing. (That is not hypothetical: the first version of
        /// this fixture did exactly that.) Gating the fetch instead separates the two: one tick to create the
        /// records and request, the gate opened OFF-tick so every decode lands with no pump running, then
        /// exactly ONE observe tick — which at a cap of one kick per tick can dispatch at most one of
        /// them.</para>
        ///
        /// <para>The anti-vacuity is the returned count, not a bare "something decoded": a fixture that
        /// decoded and then freed everything by some other path is exactly as blind as one that decoded
        /// nothing.</para>
        /// </summary>
        private static int PumpUntilDecodedAndHeld(MapView view, ProbeFeatureSource fake)
        {
            Assert.IsNotNull(fake.Gate, "this drive requires a gated source — see the summary");

            view.LateUpdate(); // records created, every fetch requested and suspended before its decode
            int loaded = view.LoadedTileCount();
            Assert.GreaterOrEqual(loaded, 4, "sanity: a multi-tile cover, or the funnel has nothing to do");
            Assert.AreEqual(0, fake.Probe.DecodeCount, "sanity: the gate really held every fetch before its decode");

            fake.Gate.TrySetResult(); // decode every tile with NO tick running, so nothing can be kicked
            fake.Probe.WaitUntil(() => fake.Probe.DecodeCount >= loaded, 10000);
            Assert.AreEqual(loaded, fake.Probe.DecodeCount,
                "ANTI-VACUITY: every cover tile must really have decoded. UnbalancedCount over zero decodes " +
                "is trivially zero, so without this every tooth in this fixture passes against a broken funnel.");

            view.LateUpdate(); // THE observe tick: each completed fetch lands in its record's lt.Decode

            Assert.AreEqual(0, view.ReleasedMidFetchCount(),
                "PRECONDITION: no record has gone to the mid-flight FETCH pen, so every held reference is " +
                "held by a RECORD. Otherwise some of the releases counted below would belong to " +
                "DiscardFetchOutcome — a different funnel with its own tooth — and this one would be " +
                "measuring the wrong thing.");

            int held = fake.Probe.DecodeCount - fake.Probe.DisposedCount;
            Assert.AreEqual(loaded, held,
                $"ANTI-VACUITY: EVERY decoded tile must still be ALIVE and held by its RECORD when the funnel " +
                $"runs ({held} of {fake.Probe.DecodeCount} are). Under R1 a kick no longer steals the record's " +
                "reference — KickMeshBuild takes its OWN separate one (decode.Acquire()) and RenderTeardownRecord " +
                "alone ends the record's ownership, kicked or not — so even the single tile the observe tick may " +
                "kick at a cap of one per tick is still held here, and nothing has reached a final release yet " +
                "(DisposedCount is 0). The retired scoped-lease model transferred the record's reference to the " +
                "kick's pool lambda and nulled the field, which is why this once had to tolerate loaded - 1.");
            return held;
        }

        /// <summary>Waits, bounded, for the funnel's releases to land, then asserts the balance. The wait is
        /// for the at-most-one kicked tile's lambda, not for the funnel — a reference the funnel failed to
        /// release is never released, however long this waits, so the bound cannot mask the defect.
        ///
        /// <para><paramref name="expectedDecodes"/> pins the POPULATION. Left at −1 the wait and the
        /// assertion both compare <c>DisposedCount</c> against a <c>DecodeCount</c> that is still moving,
        /// which is safe only where the caller has already pinned the decode count to a fixed number
        /// (<see cref="PumpUntilDecodedAndHeld"/> does, with its <c>AreEqual(loaded, DecodeCount)</c>). Where
        /// decodes are still landing — abandoned fetches resolving one by one — a moving equality can be
        /// TRUE the instant the first one is released while the rest have not decoded yet, and every later
        /// tile is then invisible to this balance AND to <c>UnbalancedCount</c>. Pass the fixed count
        /// there.</para></summary>
        private static void AssertEveryTileFreedExactlyOnce(ProbeFeatureSource fake, string funnel,
            int expectedDecodes = -1)
        {
            if (expectedDecodes >= 0)
            {
                fake.Probe.WaitUntil(() => fake.Probe.DisposedCount >= expectedDecodes, 10000);
                Assert.AreEqual(expectedDecodes, fake.Probe.DecodeCount,
                    $"POPULATION: exactly {expectedDecodes} tiles must have decoded for {funnel} — " +
                    $"{fake.Probe.DecodeCount} did. A count below it means the drive finished while decodes " +
                    "were still in flight, so the balance below is being asserted over a partial population; " +
                    "a count above it means something else decoded and the tooth is measuring the wrong tiles.");
            }
            else
            {
                // No new assert here: 234 only ever `break`s, and the fall-through Asserts immediately below
                // (DecodeCount == DisposedCount, then UnbalancedCount == 0) ARE its trailing teeth — the wait
                // cannot mask a defect that would fail either of them.
                fake.Probe.WaitUntil(() => fake.Probe.DisposedCount >= fake.Probe.DecodeCount, 10000);
            }

            Assert.AreEqual(fake.Probe.DecodeCount, fake.Probe.DisposedCount,
                $"{funnel} must free EVERY decoded tile — {fake.Probe.DecodeCount - fake.Probe.DisposedCount} " +
                "of them are still alive with no owner left to free them. Under Allocator.Persistent that is " +
                "a native leak, and NativeLeakDetection is off in the batch gate.");
            Assert.AreEqual(0, fake.Probe.UnbalancedCount,
                $"…and free each exactly ONCE — {fake.Probe.UnbalancedCount} of {fake.Probe.DecodeCount} were " +
                "disposed a number of times other than one. The same count catches a DOUBLE release, which " +
                "is the other half of getting the reference count wrong.");
        }

        // ── T-D1: a record evicted before its kick ────────────────────────────────────────────────────

        /// <summary>
        /// The wholly new path — a tile fetched, decoded, and then evicted by a cover change before it ever
        /// kicked. Under the scoped lease this record held a handle that had never decoded; under the
        /// reference count it holds a live tile, and <c>RenderTeardownRecord</c> is the only thing that
        /// frees it.
        ///
        /// <para><b>RED injection:</b> delete <c>lt.Decode?.Release()</c> from
        /// <c>TileManager.RenderTeardownRecord</c>.</para>
        /// </summary>
        [Test]
        public void ARecordEvictedBeforeItsKick_ReleasesItsDecode()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_Evicted");
            try
            {
                LoadStyleWithSource(view, fake, Cam(0, 0, CoverZoom));
                PumpUntilDecodedAndHeld(view, fake);

                // Stop serving BEFORE the pan: the destination cover would otherwise decode a second set of
                // tiles whose references are still legitimately held when the assertion runs, and a held tile
                // reads as unbalanced. The tooth measures the tiles it set up, and only those.
                fake.Serving = false;

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 150.0, Latitude = 70.0 });
                for (int f = 0; f < 600; f++)
                {
                    view.LateUpdate();
                    if (f > 2 && view.ReleaseQueueDepth() == 0 &&
                        fake.Probe.DisposedCount >= fake.Probe.DecodeCount) break;
                }
                Assert.AreEqual(0, view.ReleaseQueueDepth(), "sanity: the departing backlog must fully drain");

                AssertEveryTileFreedExactlyOnce(fake, "eviction (RenderTeardownRecord)");
            }
            finally { fake.Gate?.TrySetResult(); view.Teardown(); Object.DestroyImmediate(view.gameObject); }
        }

        // ── T-D2: a restyle ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The same funnel reached through <c>SetSources</c>, which tears down every record wholesale on a
        /// restyle. Distinct from eviction because it runs through a different caller with a different
        /// struct-copy shape (<c>foreach</c> over <c>_loaded</c> + <c>Clear</c>, not
        /// <c>TryGetValue</c> + <c>Remove</c>), and because a restyle is the one path that discards records
        /// the camera never left.
        ///
        /// <para><b>RED injection:</b> the same site as T-D1, observed through the restyle path.</para>
        /// </summary>
        [Test]
        public void ARestyle_ReleasesEveryUnkickedRecordsDecode()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_Restyle");
            try
            {
                LoadStyleWithSource(view, fake, Cam(0, 0, CoverZoom));
                PumpUntilDecodedAndHeld(view, fake);

                int midFetchBefore = view.ReleasedMidFetchCount();

                // Restyle onto a source that serves nothing, so the rebuilt cover decodes no second set.
                var inert = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Serving = false };
                LoadStyleWithSource(view, inert, Cam(0, 0, CoverZoom));

                Assert.AreEqual(midFetchBefore, view.ReleasedMidFetchCount(),
                    "PRECONDITION: the restyle must not have stashed any record in the mid-flight fetch pen " +
                    "— every torn-down record had already observed its fetch, so RenderTeardownRecord's " +
                    "release is what freed its tile, not DiscardFetchOutcome on a later tick");

                AssertEveryTileFreedExactlyOnce(fake, "restyle (SetSources → RenderTeardownRecord)");
                Assert.AreEqual(0, inert.Probe.DecodeCount, "sanity: the replacement source really served nothing");
            }
            finally { fake.Gate?.TrySetResult(); view.Teardown(); Object.DestroyImmediate(view.gameObject); }
        }

        // ── T-D3: teardown ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Teardown, and the reason D0 routed <c>DoDispose</c> through the funnel instead of leaving it with
        /// its own hand-rolled loops: a per-record obligation added to the funnel would otherwise be missed
        /// by exactly one of the four paths, and it would be the one no test drives twice.
        ///
        /// <para><b>RED injection — "SKIP A RECORD", not "remove the call".</b> Put a <c>continue</c> at the
        /// top of <c>DoDispose</c>'s <c>_loaded</c> pass, so the funnel call stays textually present and the
        /// structure test that scans for it stays green. Deleting the call is the easy injection and it
        /// proves less: it would also trip the static clause, so it cannot tell whether this RUNTIME tooth
        /// discriminates at all.</para>
        /// </summary>
        [Test]
        public void Teardown_ReleasesEveryUnkickedRecordsDecode()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_Teardown");
            bool tornDown = false;
            try
            {
                LoadStyleWithSource(view, fake, Cam(0, 0, CoverZoom));
                PumpUntilDecodedAndHeld(view, fake);

                view.Teardown();
                tornDown = true;

                AssertEveryTileFreedExactlyOnce(fake, "teardown (DoDispose → RenderTeardownRecord)");
            }
            finally
            {
                fake.Gate?.TrySetResult();
                if (!tornDown) view.Teardown();
                Object.DestroyImmediate(view.gameObject);
            }
        }

        // ── T-D4: a fetch discarded mid-flight ────────────────────────────────────────────────────────

        /// <summary>
        /// The second funnel: a tile released while its <c>GetTile</c> was still in flight. The record goes
        /// into the mid-flight fetch pen, the fetch then completes — and under the eager decode "completes"
        /// means a tile was DECODED for a record that no longer exists. Observing the outcome is no longer
        /// enough; <c>DiscardFetchOutcome</c> has to release it.
        ///
        /// <para><b>RED injection:</b> make <c>DiscardFetchOutcome</c> observe without releasing (drop the
        /// <c>?.Release()</c> from its <c>GetResult()</c> call).</para>
        /// </summary>
        [Test]
        public void AFetchDiscardedMidFlight_ReleasesTheDecodeItCompletesWith()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_MidFlight");
            try
            {
                LoadStyleWithSource(view, fake, Cam(0, 0, CoverZoom));

                // Tick until the cover is fully created. The gate holds every fetch, so no decode can race
                // these ticks — and a partially-created cover would leave later tiles requesting AFTER the
                // pan, so the tooth would measure two or three tiles instead of the whole cover.
                int loaded = 0;
                for (int f = 0; f < 60; f++)
                {
                    view.LateUpdate();
                    if (view.LoadedTileCount() == loaded && loaded >= 4) break;
                    loaded = view.LoadedTileCount();
                }
                Assert.GreaterOrEqual(view.LoadedTileCount(), 4,
                    "sanity: a multi-tile cover, so the mid-flight release is not a one-tile coincidence");
                Assert.AreEqual(0, fake.Probe.DecodeCount,
                    "PRECONDITION: nothing may have decoded yet — the gate holds every fetch before its " +
                    "decode, which is what makes the release below a genuine MID-FLIGHT one");

                // Stop serving, then pan: the destination cover's fetches resolve to null instead of piling
                // more gated tasks onto the pen.
                fake.Serving = false;
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 150.0, Latitude = 70.0 });
                for (int f = 0; f < 600; f++)
                {
                    view.LateUpdate();
                    if (f > 2 && view.ReleaseQueueDepth() == 0) break;
                }

                // THE FIXED POPULATION, read once and never re-read. Every record stashed on the
                // !FetchCompleted arm carries a gated fetch that will decode exactly one tile when the gate
                // opens, and `Serving = false` above guarantees no other fetch can decode — so this number
                // IS the set of tiles the funnel owes a release for, fixed before any of them lands.
                int abandoned = view.ReleasedMidFetchCount();
                Assert.Greater(abandoned, 0,
                    "PRECONDITION: records really were released while their fetch was in flight — this is the " +
                    "counter RenderTeardownRecord bumps on the !FetchCompleted arm, and without it the drain " +
                    "below would have nothing to discard and the tooth would pass vacuously");

                // Now let the abandoned fetches finish. Each one DECODES a tile for a record that is gone.
                // Waiting for ALL of them, not for the first: the drain loop below stops on a
                // DisposedCount/DecodeCount equality, and with decodes still landing that equality can be
                // true the moment the FIRST abandoned tile is released — the rest then decode after every
                // assertion has run and leak unobserved, invisible to the balance and to UnbalancedCount
                // alike. The bound cannot mask a defect: a tile the funnel never releases never arrives.
                fake.Gate.TrySetResult();
                fake.Probe.WaitUntil(() => fake.Probe.DecodeCount >= abandoned, 10000);
                Assert.AreEqual(abandoned, fake.Probe.DecodeCount,
                    $"ANTI-VACUITY, against a FIXED population: all {abandoned} abandoned fetches must really " +
                    $"have decoded before the balance is asserted ({fake.Probe.DecodeCount} did). If they did " +
                    "not, nothing was ever at risk and UnbalancedCount is trivially zero.");

                // DrainPendingFetchDisposal runs once per Tick and routes each completed task through the
                // single abandonment funnel. Driven against the FIXED count, not the moving DecodeCount.
                for (int f = 0; f < 600 && fake.Probe.DisposedCount < abandoned; f++)
                {
                    view.LateUpdate();
                }

                AssertEveryTileFreedExactlyOnce(fake, "the mid-flight fetch pen (DiscardFetchOutcome)", abandoned);
            }
            finally
            {
                fake.Gate?.TrySetResult();
                view.Teardown();
                Object.DestroyImmediate(view.gameObject);
            }
        }
    }
}
