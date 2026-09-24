// Shared test instrument — used by SymbolParkedRedecodeTests and EagerDecodeOwnershipTests. Unity EditMode
// only (it wraps the real MvtTileDecoder over the committed fixture). NOT in core-tests.csproj.

using System;
using System.Collections.Generic;
using System.Threading;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Wraps a REAL <see cref="ITileDecoder"/> — so the output under test is genuine production output, not
    /// a stub's — while counting decodes and recording each decoded tile's disposal.
    /// Non-obvious why: leak detection is off in the batch gate, so counting who disposed what is the only
    /// instrument here that can fail on an <c>Allocator.Persistent</c> leak. The probe enters through a fake
    /// <c>ITileFeatureSource</c> at the <c>SourceSpec.CreateSource</c> seam, so production needs no counter.
    /// </summary>
    internal sealed class LeaseProbeDecoder : ITileDecoder
    {
        private readonly ITileDecoder _inner;
        private readonly object _gate = new object();
        private readonly List<CountingTile> _decoded = new List<CountingTile>();

        internal LeaseProbeDecoder() : this(new MvtTileDecoder()) { }
        internal LeaseProbeDecoder(ITileDecoder inner) => _inner = inner;

        internal int DecodeCount { get { lock (_gate) return _decoded.Count; } }

        /// <summary>Decoded tiles that were never disposed, or were disposed more than once — either is a
        /// broken lease, and under <c>Allocator.Persistent</c> the first is a native leak.</summary>
        internal int UnbalancedCount
        {
            get
            {
                lock (_gate)
                {
                    int bad = 0;
                    foreach (CountingTile t in _decoded) if (t.DisposeCount != 1) bad++;
                    return bad;
                }
            }
        }

        /// <summary>How many decoded tiles have been disposed at least once.</summary>
        internal int DisposedCount
        {
            get { lock (_gate) { int n = 0; foreach (CountingTile t in _decoded) if (t.DisposeCount > 0) n++; return n; } }
        }

        /// <summary>The managed thread each decode ran on, in decode order.</summary>
        internal int ThreadIdOfDecode(int index) { lock (_gate) return _decoded[index].ThreadId; }

        /// <summary>The decoded tile instance produced by decode <paramref name="index"/> — for an
        /// <c>AreSame</c> that no count can make.</summary>
        internal IDecodedTile TileOfDecode(int index) { lock (_gate) return _decoded[index]; }

        /// <summary>The managed threads on which the tile's LAYERS were read, in call order, across every
        /// decoded tile. A layer read happens inside a worker pass's extract, and the decode step itself does
        /// not discriminate by thread, so this is the instrument for "which thread did the extract run on".</summary>
        internal IReadOnlyList<int> LayerReadThreadIds
        {
            get
            {
                lock (_gate)
                {
                    var all = new List<int>();
                    foreach (CountingTile t in _decoded) all.AddRange(t.LayerReadThreadIds);
                    return all;
                }
            }
        }

        public IDecodedTile Decode(TileId id, byte[] bytes)
        {
            var tile = new CountingTile(_inner.Decode(id, bytes), Thread.CurrentThread.ManagedThreadId, _gate);
            lock (_gate)
            {
                _decoded.Add(tile);
                Monitor.PulseAll(_gate);
            }
            return tile;
        }

        /// <summary>Blocks the calling thread until <paramref name="predicate"/> is true, or
        /// <paramref name="timeoutMs"/> elapses. <c>Decode</c>/<c>CountingTile.Dispose</c> pulse <c>_gate</c> on
        /// every count change. The predicate may read the counters, which re-lock <c>_gate</c>: the C#
        /// <c>lock</c> is reentrant, so that is safe inside this method's own lock.</summary>
        /// <param name="predicate">The condition to wait for; re-checked on every pulse.</param>
        /// <param name="timeoutMs">The maximum time to wait, in milliseconds.</param>
        /// <returns>True if <paramref name="predicate"/> became true within <paramref name="timeoutMs"/>;
        /// otherwise the predicate's final (false) value.</returns>
        internal bool WaitUntil(Func<bool> predicate, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            lock (_gate)
            {
                while (!predicate())
                {
                    int remaining = deadline - Environment.TickCount;
                    if (remaining <= 0) return predicate();
                    Monitor.Wait(_gate, remaining);
                }
                return true;
            }
        }

        private sealed class CountingTile : IDecodedTile
        {
            private readonly IDecodedTile _inner;
            private readonly object _readGate = new object();
            private readonly object _parentGate;
            private readonly List<int> _layerReadThreadIds = new List<int>();
            private int _disposeCount;

            internal CountingTile(IDecodedTile inner, int threadId, object parentGate)
            {
                _inner      = inner;
                ThreadId    = threadId;
                _parentGate = parentGate;
            }
            internal int ThreadId { get; }
            internal int DisposeCount => Volatile.Read(ref _disposeCount);
            internal IReadOnlyList<int> LayerReadThreadIds
            {
                get { lock (_readGate) return _layerReadThreadIds.ToArray(); }
            }

            public ITileLayer GetLayer(string name)
            {
                lock (_readGate) _layerReadThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
                return _inner.GetLayer(name);
            }

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
                _inner.Dispose();
                // Runs on arbitrary threads; pulse the PARENT's gate (not _readGate) so a WaitUntil parked
                // on DisposeCount/UnbalancedCount/DisposedCount wakes and re-checks.
                lock (_parentGate) Monitor.PulseAll(_parentGate);
            }
        }
    }
}
