using System.Collections.Generic;
using MapRenderer.Unity.Rendering.Backend;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// The draw handles to show or hide at the next flush. Each handle keeps only its LAST queued call, so a handle shown and then hidden
    /// before a flush is hidden. <see cref="Flush"/> sends the hides, then the shows, in at most one backend call each, so a tile with many
    /// layers costs one batch each way.
    /// </summary>
    internal sealed class VisibilityBatch
    {
        /// <summary>For each handle queued since the last flush, whether its last queued call was a show.</summary>
        private readonly Dictionary<int, bool> _lastQueued = new(64);

        private readonly List<int> _hide = new(32);
        private readonly List<int> _show = new(32);

        /// <summary>Queues one handle to show at the next <see cref="Flush"/>.</summary>
        public void QueueShow(int handle) => _lastQueued[handle] = true;

        /// <summary>Queues <paramref name="handles"/> to hide at the next <see cref="Flush"/>.</summary>
        public void QueueHide(int[] handles)
        {
            if (handles == null) return;
            for (int i = 0; i < handles.Length; i++) _lastQueued[handles[i]] = false;
        }

        /// <summary>Drops every queued handle: they belong to a backend that is gone.</summary>
        public void Clear() => _lastQueued.Clear();

        /// <summary>Hides every queued handle in ONE backend call, then shows every queued handle in ONE more. A null backend drops the
        /// queue, because no backend holds those handles.</summary>
        /// <returns>The number of backend calls made, 0 to 2.</returns>
        public int Flush(ITileRenderBackend backend)
        {
            foreach (KeyValuePair<int, bool> queued in _lastQueued)
                (queued.Value ? _show : _hide).Add(queued.Key);

            int calls = 0;
            if (backend != null)
            {
                if (_hide.Count > 0)
                {
                    backend.SetItemsVisible(_hide, false);
                    calls++;
                }

                if (_show.Count > 0)
                {
                    backend.SetItemsVisible(_show, true);
                    calls++;
                }
            }

            _lastQueued.Clear();
            _hide.Clear();
            _show.Clear();
            return calls;
        }
    }
}
