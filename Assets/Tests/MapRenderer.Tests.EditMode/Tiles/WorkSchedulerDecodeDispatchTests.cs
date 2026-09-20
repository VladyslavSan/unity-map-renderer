// Stage-1 acceptance: TileDecodeDispatch.DecodeAsync driven directly with an injected IWorkScheduler, over
// the real committed MVT fixture. Thread identity is read from the DECODE BODY via LeaseProbeDecoder's
// per-decode ThreadId, not from await-resumption, so these teeth don't depend on continuation-thread
// semantics — a shallow implementation (return the UniTask directly / always dispatch / marshal to main)
// cannot pass all three.

using System;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class WorkSchedulerDecodeDispatchTests
    {
        private static readonly TileId SomeTile = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>A decoder whose <see cref="Decode"/> always throws — the fault-path regression: a
        /// decoder throw must fault the returned <c>UniTask</c> with <see cref="TileDecodeException"/> and
        /// mint no <see cref="SharedDisposable{T}"/>, under EITHER scheduler policy.</summary>
        private sealed class ThrowingDecoder : ITileDecoder
        {
            public IDecodedTile Decode(TileId id, byte[] bytes) => throw new InvalidOperationException("boom");
        }

        // Tooth 1/2 deliberately do NOT `await` — an async-Task test method resumes wherever the runner's
        // SynchronizationContext/continuation lands, which is not necessarily this method's own thread, so
        // `caller` read after an await would be an assumption about runner scheduling, not a fact about the
        // scheduler under test. Instead: capture `caller` synchronously, kick the decode, then park on
        // WaitOffPlayerLoop (the same off-PlayerLoop wait tooth 3 pins) and read the result with
        // GetAwaiter().GetResult() — mirrors BurstJobRunOffMainSpikeTests.RunOnWorker.

        [Test]
        public void InlineScheduler_RunsDecodeOnCallingThread()
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            var probe = new LeaseProbeDecoder();

            UniTask<SharedDisposable<IDecodedTile>> task = TileDecodeDispatch.DecodeAsync(
                SomeTile, SampleTileFixture.Bytes(), probe, new InlineWorkScheduler()).Preserve();
            Assert.IsTrue(task.WaitOffPlayerLoop(10000), "sanity: Inline must already be terminal by return");
            SharedDisposable<IDecodedTile> handle = task.GetAwaiter().GetResult();

            Assert.IsNotNull(handle, "sanity: a real fixture must decode to a non-null handle");
            Assert.AreEqual(caller, probe.ThreadIdOfDecode(0),
                "Inline must run the decode body ON THE CALLING THREAD, with zero dispatch — the WebGL-" +
                "correct behaviour. A dispatch to any other thread (e.g. via ThreadPool.QueueUserWorkItem) " +
                "would record a different thread id here.");
            handle.Release();
        }

        [Test]
        public void ThreadPoolScheduler_RunsDecodeOffCallingThread()
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            var probe = new LeaseProbeDecoder();

            UniTask<SharedDisposable<IDecodedTile>> task = TileDecodeDispatch.DecodeAsync(
                SomeTile, SampleTileFixture.Bytes(), probe, new ThreadPoolWorkScheduler()).Preserve();
            Assert.IsTrue(task.WaitOffPlayerLoop(10000), "sanity: the decode must complete within the timeout");
            SharedDisposable<IDecodedTile> handle = task.GetAwaiter().GetResult();

            Assert.IsNotNull(handle, "sanity: a real fixture must decode to a non-null handle");
            Assert.AreNotEqual(caller, probe.ThreadIdOfDecode(0),
                "ThreadPool must NOT run the decode body on the calling thread — reproducing today's " +
                "UniTask.RunOnThreadPool parallelism. A decode recorded on the caller's own thread id means " +
                "the scheduler dispatched nowhere.");
            handle.Release();
        }

        [Test]
        public void ThreadPoolScheduler_CompletesOffThePlayerLoop()
        {
            var probe = new LeaseProbeDecoder();

            UniTask<SharedDisposable<IDecodedTile>> task = TileDecodeDispatch.DecodeAsync(
                SomeTile, SampleTileFixture.Bytes(), probe, new ThreadPoolWorkScheduler()).Preserve();

            // The exact wait TileManager.DrainMeshBuilds/DoDispose use (§G-1): parks on a kernel event with
            // NO PlayerLoop pumping. A bridge that marshalled the UniTaskCompletionSource's completion via
            // UniTask.SwitchToMainThread() would post the continuation to the PlayerLoop instead of firing
            // it on the pool thread, and this would time out.
            bool completed = task.WaitOffPlayerLoop(10000);

            Assert.IsTrue(completed,
                "the decode must complete OFF the PlayerLoop — TileManager's drain/dispose spins wait exactly " +
                "this way, with the PlayerLoop never pumped, and would deadlock against a bridge that " +
                "marshalled completion to the main thread.");
            task.GetAwaiter().GetResult().Release();
        }

        [TestCase(false, TestName = "DecoderFault_UnderInlineScheduler_FaultsWithTileDecodeException_AndMintsNoHandle")]
        [TestCase(true, TestName = "DecoderFault_UnderThreadPoolScheduler_FaultsWithTileDecodeException_AndMintsNoHandle")]
        public async Task DecoderFault_FaultsWithTileDecodeException_AndMintsNoHandle(bool useThreadPool)
        {
            IWorkScheduler scheduler = useThreadPool ? new ThreadPoolWorkScheduler() : new InlineWorkScheduler();

            Exception thrown = null;
            SharedDisposable<IDecodedTile> handle = null;
            try
            {
                handle = await TileDecodeDispatch.DecodeAsync(SomeTile, null, new ThrowingDecoder(), scheduler);
            }
            catch (Exception ex) { thrown = ex; }

            Assert.IsNull(handle,
                "a decoder throw must fault the TASK and mint no SharedDisposable — a failed decode can " +
                "never leak a reference (IR C1 P3).");
            Assert.IsInstanceOf<TileDecodeException>(thrown,
                "the fault must be wrapped as a TileDecodeException, unchanged by the scheduler policy.");
            Assert.IsInstanceOf<InvalidOperationException>(thrown.InnerException,
                "…with the decoder's own exception preserved underneath.");
        }
    }
}
