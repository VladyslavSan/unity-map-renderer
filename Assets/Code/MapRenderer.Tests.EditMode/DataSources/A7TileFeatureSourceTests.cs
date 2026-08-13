// Epic A / A7 acceptance: the raised source interface
// (ITileFeatureSource.GetTile -> SharedDisposable<IDecodedTile>). F-2 proves a BYTELESS source (no IDataSource, no
// bytes, no FetchAsync) flows through the UNCHANGED per-layer fan-out — the raise is real, not a rename.
// F-4 proves the EAGER-decode decision: GetTile decodes inside its own task, so a malformed-MVT fetch
// faults the task itself and mints no handle at all — the exact inversion of the lazy contract it replaced.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using CoreMapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class A7TileFeatureSourceTests
    {
        // ── Test doubles (kept in the test assembly per convention — no production observability added) ──

        /// <summary>A <see cref="ITileFeatureSource"/> with NO <see cref="MapRenderer.Core.Data.IDataSource"/>,
        /// no bytes, no fetch at all — every <see cref="GetTile"/> call builds a tile and hands back a fresh
        /// <see cref="SharedDisposable{T}"/> over it. The F-2 falsifier: a coordinator still routing through the
        /// byte-centric boundary cannot consume this (compile-impossible, since there is no
        /// <c>IDataSource</c> anywhere to wrap).
        ///
        /// <para><b>A fresh wrapper per call, and a fresh tile with it</b> — the shape production now has, and
        /// a requirement rather than a nicety: the caller owns the ONE reference a wrapper is born with and
        /// releases it, so handing the same instance to two records would double-acquire the same count. The
        /// test double this replaced (<c>EagerHandle</c>, a hand-written no-op-scope handle over a shared
        /// pre-built tile) is exactly what the wrapper IS now, so it is gone.</para></summary>
        private sealed class FakeTileFeatureSource : ITileFeatureSource
        {
            private readonly System.Func<IDecodedTile> _tileFactory;
            public int GetTileCalls { get; private set; }
            public FakeTileFeatureSource(System.Func<IDecodedTile> tileFactory) => _tileFactory = tileFactory;

            public UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default)
            {
                GetTileCalls++;
                return UniTask.FromResult(new SharedDisposable<IDecodedTile>(_tileFactory()));
            }

            public void Release(TileId id) { }
            public int InFlightCount => 0;
            public void Dispose() { }
        }

        private const string FixtureSourceLayerName = "a7-fixture-layer";

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Mirrors <c>MapViewTestExtensions.LoadTestStyle</c>'s body, but wires an
        /// <see cref="ITileFeatureSource"/> DIRECTLY — no <c>IDataSource</c> to wrap. Kept local (not added to
        /// the shared helper) since it exists only to prove the raise accepts a byteless source; every other
        /// test in the suite still drives the byte-centric <c>LoadTestStyle</c> overload unchanged.</summary>
        private static void LoadTestStyleWithFeatureSource(
            MapView view, ITileFeatureSource source, CameraProperties initialView, StyleDocument style)
        {
            CoreMapView mv = view.View;
            mv.Camera.SetProperties(initialView);
            mv.Camera.SyncToCamera();
            mv.Layers.Build(style, mv.Camera.CurrentProperties.Zoom, view.Config.MaterialSet);

            var specs = new List<TileManager.SourceSpec>
            {
                new TileManager.SourceSpec("s", default, 0, int.MaxValue, () => source),
            };
            mv.TileManager.SetSources(specs, view.Config.Backend);
        }

        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                Thread.Sleep(1);
            }
        }

        // ── F-2: a bytes-less ITileFeatureSource flows through the UNCHANGED per-layer fan-out ────────────

        [Test]
        public void ByteLessSource_FlowsThroughTheUnchangedFanOut_ProducesTheFullExtentQuad()
        {
            // The fixture tile: one layer, one feature — the A2 full-extent-ring command stream (the SAME
            // oracle A6NonMvtDecoderTests/TileBackgroundQuadProjectionTests assert decodes to the tile's 4
            // corners), carried by the production InMemoryTileFeature.
            // Built per GetTile call, and OWNED BY THE LEASE that wraps it — the coordinator's release is
            // what frees its Allocator.Persistent geometry, exactly as for a real decode. A single fixture
            // tile shared across calls would be disposed by the first release and read freed by the next.
            IDecodedTile MakeFixtureTile()
            {
                var feature = new InMemoryTileFeature
                {
                    GeometryType = TileGeometryType.Polygon,
                    Geometry     = FullExtentRingCommandStream.Commands,
                };
                // IR C1 P3: the fixture layer owns its geometry, materialized at construction like a decoded one.
                var layer = new InMemoryTileLayer(
                    FixtureSourceLayerName, new TileId { Z = 0, X = 0, Y = 0 }, new IFeature[] { feature },
                    (uint)TileBackgroundLayerProcessor.Extent);
                return new InMemoryDecodedTile(layer);
            }

            var fake = new FakeTileFeatureSource(MakeFixtureTile);

            var style = StyleParser.Parse($@"{{
                ""version"": 8,
                ""layers"": [
                    {{ ""id"": ""fixture-fill"", ""type"": ""fill"", ""source"": ""s"",
                       ""source-layer"": ""{FixtureSourceLayerName}"",
                       ""paint"": {{ ""fill-color"": ""#ffffff"" }} }}
                ]
            }}");

            var go   = new GameObject("A7ByteLessSource");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            try
            {
                LoadTestStyleWithFeatureSource(view, fake, Cam(0, 0, 0.0), style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "sanity: the tile must settle.");
                Assert.Greater(fake.GetTileCalls, 0, "sanity: the byteless source must have been consulted.");

                Mesh[] meshes = view.GetTileMeshes(new TileId { Z = 0, X = 0, Y = 0 });
                Assert.IsNotNull(meshes, "F-2 DECISIVE: a coordinator still routing through the byte-centric " +
                    "boundary cannot consume a source with no IDataSource/bytes/FetchAsync at all — the fill " +
                    "mesh must exist, proving the raise is real.");
                Assert.AreEqual(1, meshes.Length);
                Assert.IsNotNull(meshes[0]);
                Assert.AreEqual(4, meshes[0].vertexCount,
                    "the byteless source's feature must flow through StyledFillTileBuilder unchanged and " +
                    "produce the flat 4-vertex quad (Mercator, no subdivision) — the same oracle " +
                    "A6NonMvtDecoderTests asserts one level down (the runner, not the coordinator).");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── F-4: GetTile decodes EAGERLY — the inversion of the retired lazy tooth ────────────────────────

        // Deliberately malformed as MVT (a truncated length-delimited TileLayers field — MvtDecoder.Decode
        // throws decoding it — same fixture shape used by A6NonMvtDecoderTests).
        private static readonly byte[] MalformedMvtBytes = { 0x1A, 0x64 };

        /// <summary>
        /// <b>T-E2 — the decisive falsifier, inverted.</b> This tooth used to assert that <c>GetTile</c>
        /// completed cleanly over malformed bytes and only faulted at the first <c>GetOrDecode()</c>: the
        /// proof the handle was LAZY. Under the eager decode the parse happens inside the task, so the task
        /// itself faults and <b>no handle is ever minted</b>. The same input, the same seam, the opposite
        /// answer — and the same decisiveness: a lazy implementation would complete this call and hand back
        /// a handle.
        ///
        /// <para>The fault must also arrive as a <c>TileDecodeException</c> and not as a bare decoder
        /// exception, because that type is the only thing that lets the coordinator tell a malformed tile
        /// apart from a 5xx and give it its own bounded log. The original decoder exception is preserved
        /// underneath, so nothing is lost by wrapping.</para>
        /// </summary>
        [Test]
        public async Task GetTile_MalformedBytes_FaultsTheTask_AndMintsNoHandle()
        {
            var byteSource = TestDataSource.FromBytes(MalformedMvtBytes);
            using var source = new MvtTileFeatureSource(byteSource);

            System.Exception thrown = null;
            SharedDisposable<IDecodedTile> handle = null;
            try { handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 }); }
            catch (System.Exception ex) { thrown = ex; }

            Assert.IsNull(handle,
                "F-4 DECISIVE (inverted): an EAGER GetTile decodes at fetch completion, so malformed bytes " +
                "must fault the TASK and produce no handle. A handle here would mean the source deferred the " +
                "decode — the lazy contract this stage deleted, and with it the drop paths that free nothing.");
            Assert.IsInstanceOf<TileDecodeException>(thrown,
                "…and the fault must be a TileDecodeException, not the raw decoder throw: it shares a channel " +
                "with fetch errors now, and only the type distinguishes 'the bytes are bad' from 'the network " +
                "failed'. Collapsing them would let a broken tile hide inside another failure's log throttle.");
            Assert.IsInstanceOf<System.InvalidOperationException>(thrown.InnerException,
                "…with the decoder's own exception preserved underneath, so wrapping costs no diagnosis");
        }

        /// <summary>
        /// <b>T-E1 — the happy path of the same inversion.</b> The awaited task hands back a handle whose
        /// tile is ALREADY built: reading it does no work, cannot fault, and yields the same instance every
        /// time. Paired with the malformed case above (which proves the decode ran inside the task), this
        /// pins that a read is a plain field access rather than a deferred parse.
        ///
        /// <para>R2: the release-then-read anti-vacuity this used to end on is gone —
        /// <c>SharedDisposable{T}.Value</c> is undefended by design (no throw after the last
        /// <see cref="SharedDisposable{T}.Release"/>), so that assertion tested the retired
        /// <c>DecodedTileLease</c>'s own guard, not a property of this seam. It is not "made to pass"; it is
        /// retired with the guard, mirroring <c>DecodedTileLeaseTests</c>' fate (decode-refcount plan §1/§5).</para>
        /// </summary>
        [Test]
        public async Task GetTile_HandsBackAnAlreadyDecodedTile_ThatTheCallerOwns()
        {
            byte[] fixtureBytes = System.IO.File.ReadAllBytes(
                System.IO.Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes"));
            var byteSource = TestDataSource.FromBytes(fixtureBytes);
            using var source = new MvtTileFeatureSource(byteSource);

            SharedDisposable<IDecodedTile> handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 });
            Assert.IsNotNull(handle, "sanity: present bytes must mint a handle");

            IDecodedTile first = handle.Value;
            Assert.IsNotNull(first, "the tile is already decoded — reading it must never return null");
            Assert.AreSame(first, handle.Value,
                "…and a second read must hand back the SAME instance. A lazy handle that decoded per read " +
                "would produce a distinct tile here, and two sets of Allocator.Persistent buffers with one " +
                "owner between them.");

            // The caller owns the one reference GetTile handed over; releasing it is what frees the buffers.
            handle.Release();
        }

        [Test]
        public async Task GetTile_AbsentTile_ReturnsNullHandle()
        {
            // Byte-equivalent to today's TileResponse.HasData == false branch (§G risk 4).
            var byteSource = TestDataSource.Absent();
            using var source = new MvtTileFeatureSource(byteSource);

            SharedDisposable<IDecodedTile> handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 });

            Assert.IsNull(handle, "an absent tile (HasData == false) must map to a null handle — the " +
                "coordinator's null-for-absent contract (Epic A / A7 §G-4).");
        }
    }
}
