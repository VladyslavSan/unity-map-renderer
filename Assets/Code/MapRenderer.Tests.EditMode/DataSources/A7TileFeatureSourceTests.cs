// Epic A / A7 acceptance: the raised source interface
// (ITileFeatureSource.GetTile -> SharedDisposable<IDecodedTile>). F-4 proves the EAGER-decode decision:
// GetTile decodes inside its own task, so a malformed-MVT fetch faults the task itself and mints no handle
// at all — the exact inversion of the lazy contract it replaced.
//
// EditMode half: the pure async-Task unit teeth (direct source.GetTile calls — no MapView, no settle-poll).
// The F-2 byteless-source-through-MapView tooth (which drives the real cover→build→settle loop) lives in
// the PlayMode half (MapRenderer.Tests.PlayMode.DataSources.A7TileFeatureSourceTests).

using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.DataSources
{
    [TestFixture]
    public class A7TileFeatureSourceTests
    {
        // Off-main, matching production's desktop policy — these teeth exercise GetTile's own contract, not
        // the scheduler choice.
        private static readonly IWorkScheduler Scheduler = new ThreadPoolWorkScheduler();

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
        /// </summary>
        [Test]
        public async Task GetTile_MalformedBytes_FaultsTheTask_AndMintsNoHandle()
        {
            var byteSource = TestDataSource.FromBytes(MalformedMvtBytes);
            using var source = new MvtTileFeatureSource(byteSource, Scheduler);

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
        /// </summary>
        [Test]
        public async Task GetTile_HandsBackAnAlreadyDecodedTile_ThatTheCallerOwns()
        {
            byte[] fixtureBytes = System.IO.File.ReadAllBytes(
                System.IO.Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes"));
            var byteSource = TestDataSource.FromBytes(fixtureBytes);
            using var source = new MvtTileFeatureSource(byteSource, Scheduler);

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
            using var source = new MvtTileFeatureSource(byteSource, Scheduler);

            SharedDisposable<IDecodedTile> handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 });

            Assert.IsNull(handle, "an absent tile (HasData == false) must map to a null handle — the " +
                "coordinator's null-for-absent contract (Epic A / A7 §G-4).");
        }
    }
}
