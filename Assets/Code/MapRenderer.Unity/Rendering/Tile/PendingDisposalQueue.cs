using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Owns the three release-time holding pens for work abandoned mid-flight — a prologue build, a
    /// graph build, a fetch — so each is disposed once it settles instead of leaking. Stashed by
    /// <see cref="TileManager.RenderTeardownRecord"/>; drained once per Tick, flushed at teardown.
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
                    continue; // still in-flight; check again next Tick

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

        /// <summary>Teardown: parks on every still-in-flight entry, then disposes it — must not leak on Dispose.</summary>
        public void FlushAll()
        {
            for (int i = 0; i < _prologue.Count; i++)
            {
                var handle = _prologue[i];
                UniTask<Processing.TilePrologueOutput> buildTask = handle.ToUniTask();
                buildTask.WaitOffPlayerLoop(10000);

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
                task.WaitOffPlayerLoop(10000);
                DiscardFetchOutcome(task);
            }

            _fetch.Clear();
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
