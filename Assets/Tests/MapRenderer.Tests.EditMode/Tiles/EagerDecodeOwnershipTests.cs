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
using MapRenderer.Unity.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using CoreMapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// <b>The leak class of the eager decode, one test per abandonment funnel.</b> The tile is decoded and
    /// holds <c>Allocator.Persistent</c> buffers before any drop path — a never-kicked record, a discarded
    /// fetch, a restyle, teardown — can drop it, so every one of them owns a release. One test per funnel
    /// names WHICH funnel leaks; a combined "nothing leaks" case could not.
    ///
    /// <para>Non-obvious why: every case also asserts that the tiles decoded and are ALIVE just before the
    /// funnel runs, because <c>UnbalancedCount == 0</c> (a missed or a double release) is trivially zero over
    /// a fixture that never decoded. The probe is a <c>LeaseProbeDecoder</c> behind a fake
    /// <c>ITileFeatureSource</c> at the <c>SetSources</c> seam. <c>SymbolParkedRedecodeTests</c> drives the
    /// parked-drain cancellation drop; <c>TileProcessingStructureTests</c> pins the residue no runtime test
    /// can reach.</para>
    /// </summary>
    [TestFixture]
    public class EagerDecodeOwnershipTests : BaseTestFixture
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
        /// A fake source that decodes through <see cref="TileDecodeDispatch"/> as production does — same pool
        /// hop, same lease, one reference to the caller — with a probe decoder inside. When
        /// <see cref="Serving"/> is false, <see cref="GetTile"/> answers null, so a cover change cannot decode
        /// a second set of still-held tiles that would read as unbalanced. <see cref="Gate"/>, when set,
        /// suspends the fetch before the decode, which is the only way to reach the mid-flight funnel.
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
        /// <c>SetSources</c> entry — the same seam <c>ByteLessTileFeatureSourceTests</c> uses, and the reason no
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

        /// <summary>A view whose mesh-build cap is ONE, so a multi-tile cover decodes far faster than it is
        /// kicked and most records hold a decoded tile they never dispatch — the never-kicked state.
        /// <para>Non-obvious why: the cap is not zero, because <c>TileManager.PumpPending</c> reads 0 as
        /// UNLIMITED (<c>maxMeshBuildsPerTick &gt; 0 ? … : int.MaxValue</c>) and would kick every tile, so
        /// every tooth here would pass while measuring nothing.</para></summary>
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
        /// Drives the cover into the never-kicked state — every tile fetched, decoded and held by its record —
        /// asserts that state, and returns the number of tiles held.
        /// <para>Non-obvious why: the gate is required. Pumping ticks until the decodes land spreads fetch
        /// completion over many ticks, each tick kicks one tile, and the funnel then has nothing left to
        /// release. The gate lets every decode land OFF-tick, then ONE observe tick kicks at most one tile.
        /// The returned count, not "something decoded", is the anti-vacuity.</para>
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
                $"runs ({held} of {fake.Probe.DecodeCount} are). A kick does not take the record's " +
                "reference — KickMeshBuild takes its OWN separate one (decode.Acquire()) and RenderTeardownRecord " +
                "alone ends the record's ownership, kicked or not — so even the single tile the observe tick may " +
                "kick at a cap of one per tick is still held here, and nothing has reached a final release yet " +
                "(DisposedCount is 0). A reading of loaded - 1 means a kick transferred the record's " +
                "reference to its pool lambda and nulled the field.");
            return held;
        }

        /// <summary>Waits, bounded, for the funnel's releases to land, then asserts the balance. A reference
        /// the funnel fails to release never arrives, so the bound cannot mask the defect.</summary>
        /// <param name="expectedDecodes">The fixed decode population. Leave it at −1 only when the caller has
        /// already pinned <c>DecodeCount</c> (<see cref="PumpUntilDecodedAndHeld"/> does).</param>
        /// <remarks>Non-obvious why: while decodes still land, a moving <c>DisposedCount == DecodeCount</c> can
        /// be true after the first release, and every later tile escapes this balance and
        /// <c>UnbalancedCount</c>.</remarks>
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
                // The Asserts below (DecodeCount == DisposedCount, then UnbalancedCount == 0) are this wait's
                // teeth, so the wait cannot mask a defect that fails either of them.
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

        // ── a record evicted before its kick ──────────────────────────────────────────────────────────

        /// <summary>
        /// A tile fetched, decoded, and then evicted by a cover change before it kicks. The record holds a
        /// live tile, and <c>RenderTeardownRecord</c> is the only thing that frees it.
        /// <para><b>RED injection:</b> delete <c>lt.Decode?.Release()</c> from
        /// <c>TileManager.RenderTeardownRecord</c>.</para>
        /// </summary>
        [Test]
        public void ARecordEvictedBeforeItsKick_ReleasesItsDecode()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_Evicted");
            Track(view.gameObject);
            try
            {
                LoadStyleWithSource(view, fake, Cam(0, 0, CoverZoom));
                PumpUntilDecodedAndHeld(view, fake);

                // Stop serving BEFORE the pan: the destination cover would otherwise decode a second set of
                // tiles that are still held when the assertion runs, and a held tile reads as unbalanced.
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
            finally { fake.Gate?.TrySetResult(); view.Teardown(); }
        }

        // ── a restyle ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The same funnel reached through <c>SetSources</c>, which tears down every record on a restyle. It
        /// differs from eviction in its caller and struct-copy shape (<c>foreach</c> over <c>_loaded</c> +
        /// <c>Clear</c>, not <c>TryGetValue</c> + <c>Remove</c>), and it discards records the camera never left.
        /// <para><b>RED injection:</b> the same site as <c>ARecordEvictedBeforeItsKick_ReleasesItsDecode</c>,
        /// observed through the restyle path.</para>
        /// </summary>
        [Test]
        public void ARestyle_ReleasesEveryUnkickedRecordsDecode()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_Restyle");
            Track(view.gameObject);
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
            finally { fake.Gate?.TrySetResult(); view.Teardown(); }
        }

        // ── teardown ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Teardown routes <c>DoDispose</c> through the funnel, so a per-record obligation added to the funnel
        /// reaches all four paths instead of missing the one no test drives twice.
        /// <para><b>RED injection:</b> put a <c>continue</c> at the top of <c>DoDispose</c>'s <c>_loaded</c>
        /// pass. Do not delete the funnel call: that also trips the structure test that scans for it, so it
        /// cannot show that this runtime tooth discriminates.</para>
        /// </summary>
        [Test]
        public void Teardown_ReleasesEveryUnkickedRecordsDecode()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_Teardown");
            Track(view.gameObject);
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
            }
        }

        // ── a fetch discarded mid-flight ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The second funnel: a tile released while its <c>GetTile</c> is still in flight. The record goes
        /// into the mid-flight fetch pen, and the completing fetch DECODES a tile for a record that is
        /// gone, so <c>DiscardFetchOutcome</c> must release it, not only observe it.
        /// <para><b>RED injection:</b> make <c>DiscardFetchOutcome</c> observe without releasing (drop the
        /// <c>?.Release()</c> from its <c>GetResult()</c> call).</para>
        /// </summary>
        [Test]
        public void AFetchDiscardedMidFlight_ReleasesTheDecodeItCompletesWith()
        {
            var fake = new ProbeFeatureSource(SampleTileFixture.Bytes()) { Gate = new UniTaskCompletionSource() };
            MapView view = NewViewWithSlowKicks("EagerDecodeOwnership_MidFlight");
            Track(view.gameObject);
            try
            {
                LoadStyleWithSource(view, fake, Cam(0, 0, CoverZoom));

                // Tick until the whole cover exists (the gate holds every fetch, so no decode races these ticks);
                // a partial cover would request later tiles AFTER the pan and shrink the measured set.
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

                // THE FIXED POPULATION, read once: each record on the !FetchCompleted arm holds one gated fetch
                // that decodes one tile, and `Serving = false` stops every other decode.
                int abandoned = view.ReleasedMidFetchCount();
                Assert.Greater(abandoned, 0,
                    "PRECONDITION: records really were released while their fetch was in flight — this is the " +
                    "counter RenderTeardownRecord bumps on the !FetchCompleted arm, and without it the drain " +
                    "below would have nothing to discard and the tooth would pass vacuously");

                // Let every abandoned fetch DECODE its tile before draining; a drain that starts while decodes
                // still land lets the later tiles decode after the asserts and leak unobserved.
                fake.Gate.TrySetResult();
                fake.Probe.WaitUntil(() => fake.Probe.DecodeCount >= abandoned, 10000);
                Assert.AreEqual(abandoned, fake.Probe.DecodeCount,
                    $"ANTI-VACUITY, against a FIXED population: all {abandoned} abandoned fetches must really " +
                    $"have decoded before the balance is asserted ({fake.Probe.DecodeCount} did). If they did " +
                    "not, nothing was ever at risk and UnbalancedCount is trivially zero.");

                // PendingDisposalQueue.DrainCompleted runs once per Tick and routes each completed task through the
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
            }
        }
    }
}
