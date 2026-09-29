using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Owns the three release-time holding pens for work abandoned mid-flight — a prologue build, a
    /// graph build, a fetch — so each is disposed once it settles instead of leaking. Stashed by
    /// <see cref="TileManager.RenderTeardownRecord"/>; drained once per Update, flushed at teardown.
    /// <para><b>Main thread only.</b> Both drain methods complete the graph arm's job handles
    /// synchronously — call only from the main thread.</para></summary>
    internal sealed class PendingDisposalQueue
    {
        /// <summary>Prologue builds released mid-flight — their result's NativeArrays don't exist yet, so
        /// disposal waits for the task to complete.</summary>
        private readonly List<WorkHandle<Processing.TilePrologueOutput>> _prologue = new(8);

        /// <summary>The graph arm's twin of <see cref="_prologue"/> — a build released in flight has no
        /// result yet to dispose.</summary>
        private readonly List<Processing.TileBuildGraph> _graph = new(8);

        /// <summary>A released tile's in-flight fetch. It must stay observed, or its finalizer floods the
        /// console on fault.</summary>
        private readonly List<UniTask<SharedDisposable<IDecodedTile>>> _fetch = new(8);

        /// <summary>Stashes an in-flight prologue build. Never call with a <c>default(WorkHandle{T})</c> —
        /// the caller nulls its own <c>lt.MeshBuildTask</c> right after this call.</summary>
        public void StashPrologue(WorkHandle<Processing.TilePrologueOutput> handle) => _prologue.Add(handle);

        /// <summary>Stashes an in-flight graph build. The caller nulls its own <c>lt.Graph</c> right after
        /// this call — that null is the double-free guard, and it stays at the call site.</summary>
        public void StashGraph(Processing.TileBuildGraph graph) => _graph.Add(graph);

        /// <summary>Stashes an in-flight fetch. Must already be <c>.Preserve()</c>d — completion is
        /// observed later, off the PlayerLoop, not at stash time.</summary>
        public void StashFetch(UniTask<SharedDisposable<IDecodedTile>> task) => _fetch.Add(task);

        /// <summary>Non-blocking poll: disposes every pen entry that has completed, leaving the rest for the next call.</summary>
        public void DrainCompleted()
        {
            // Iterate backwards so removal doesn't shift indices.
            for (int i = _prologue.Count - 1; i >= 0; i--)
            {
                var handle = _prologue[i];
                if (!handle.IsCompleted)
                    continue; // still in-flight; check again next Update

                if (handle.IsSucceeded)
                    handle.GetResult().Dispose();
                // A faulted or cancelled handle exposes no result to dispose; the body's own catch owns what it allocated.

                _prologue.RemoveAt(i);
            }

            // A Burst job cannot fault, so IsStepComplete is the only gate.
            for (int i = _graph.Count - 1; i >= 0; i--)
            {
                var graph = _graph[i];
                if (!graph.IsStepComplete)
                    continue;

                graph.Dispose();
                _graph.RemoveAt(i);
            }

            for (int i = _fetch.Count - 1; i >= 0; i--)
            {
                if (!_fetch[i].Status.IsCompleted())
                    continue;

                DiscardFetchOutcome(_fetch[i]); // released → swallow silently
                _fetch.RemoveAt(i);
            }
        }

        /// <summary>Teardown: parks on every still-in-flight entry, then disposes it. It disposes every entry
        /// that settles, then throws once if any did not settle in time.</summary>
        /// <param name="timeoutMs">How long to park on each entry. An entry that does not settle is left
        /// undisposed, because its task still owns what it will produce.</param>
        /// <exception cref="System.TimeoutException">A prologue build or a fetch did not settle.</exception>
        public void FlushAll(int timeoutMs = 10000)
        {
            int hungPrologues = 0;
            int hungFetches = 0;

            for (int i = 0; i < _prologue.Count; i++)
            {
                var handle = _prologue[i];
                UniTask<Processing.TilePrologueOutput> buildTask = handle.ToUniTask();
                if (!buildTask.WaitOffPlayerLoop(timeoutMs))
                {
                    hungPrologues++;
                    continue;
                }

                if (handle.IsSucceeded)
                    handle.GetResult().Dispose();
            }

            _prologue.Clear();

            // Complete() runs a not-yet-started job inline, no timeout needed.
            foreach (var graph in _graph)
                graph.Dispose();
            _graph.Clear();

            for (int i = 0; i < _fetch.Count; i++)
            {
                var task = _fetch[i];
                if (!task.WaitOffPlayerLoop(timeoutMs))
                {
                    hungFetches++; // a pending task has no outcome to observe yet
                    continue;
                }

                DiscardFetchOutcome(task);
            }

            _fetch.Clear();

            if (hungPrologues + hungFetches > 0)
                throw new System.TimeoutException(
                    $"PendingDisposalQueue.FlushAll: {hungPrologues} prologue build(s) and {hungFetches} fetch(es) " +
                    $"did not settle within {timeoutMs}ms. Their native buffers are not released. A hung task " +
                    "cannot be re-parked, so teardown fails loud instead of retrying.");
        }

        /// <summary>Observes a completed fetch task's outcome exactly once and throws it away — for a tile
        /// nobody wants. Every outcome is swallowed silently: a 5xx here is noise, not news.
        /// Precondition: <c>req.Status.IsCompleted()</c> — calling this on a pending task throws.
        /// <see langword="void"/> is load-bearing: no caller can bind, retain or re-consume what the
        /// fetch produced; ownership of the observation is enforced by the signature.</summary>
        private void DiscardFetchOutcome(UniTask<SharedDisposable<IDecodedTile>> req)
        {
            try
            {
                // Release() frees the decode's Allocator.Persistent buffers.
                req.GetAwaiter().GetResult()?.Release();
            }
            catch (System.Exception)
            {
                // Cancelled or faulted — expected on an abandoned tile; a faulted task has no lease to free.
            }
        }
    }
}
