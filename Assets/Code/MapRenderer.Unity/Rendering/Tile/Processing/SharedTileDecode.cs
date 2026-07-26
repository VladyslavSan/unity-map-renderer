using System.Runtime.ExceptionServices;
using Unity.Profiling;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A4 (design §B Q1-Q3, Q6): the decode-once, GC-owned holder threaded through the single
    /// fork point where one fetched <c>byte[]</c> splits into the mesh and symbol worker passes —
    /// <see cref="TileManager.PumpPending"/>'s fetch-observe site mints one instance per fetched (source,
    /// tile) and both cadences read through it instead of each decoding for itself.
    ///
    /// <para>Identity IS the sharing mechanism: both consumers receive the SAME instance from the fork
    /// site, so there is no key, no lookup, no registry. <see cref="GetOrDecode"/> is lazy and
    /// idempotent — whichever cadence's worker pass reaches it first performs the one decode, via the
    /// injected <see cref="ITileDecoder"/> (Epic A / A6 — encoding-driven, no longer hardcoded to MVT),
    /// under <see cref="_gate"/>, which is also the safe-publication barrier for the effectively-immutable
    /// <see cref="IDecodedTile"/> handed to the other cadence's pool thread. A decode fault is cached
    /// (<see cref="ExceptionDispatchInfo"/>) and rethrown — with the SAME instance and original stack — to
    /// every subsequent caller, so a malformed tile is still only decoded (and only fails) once.</para>
    ///
    /// <para><b>No lifetime protocol.</b> <see cref="IDecodedTile"/> is plain managed data (no
    /// <c>IDisposable</c>, no native memory), so retention is plain GC reachability — this class provides
    /// only what the tracing GC cannot (decode-once + thread-safe publication), nothing more. It is
    /// dropped with its last holder exactly on the paths the raw <c>byte[]</c> is dropped on today (a
    /// never-kicked <c>LoadedTile</c> record, a dequeued/dropped/cleared symbol queue entry, a completed
    /// kick task) — no refcount, no <c>Acquire</c>/<c>Release</c>, no clear-on-done.</para>
    /// </summary>
    internal sealed class SharedTileDecode : IDecodedTileHandle
    {
        private readonly object _gate = new object();
        private readonly byte[] _bytes;
        private readonly ITileDecoder _decoder;
        private IDecodedTile _tile;                // decoded lazily, once, under _gate
        private ExceptionDispatchInfo _fault;       // cached decode fault, rethrown to every caller

        /// <summary>Profiler marker name constants (SSOT) for the shared decode — referenced by the
        /// <see cref="ProfilerMarker"/> field below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // Moved from TileManager (A1's "declared but unwired" follow-up, closed here) — the NAME is
            // unchanged, so live profiles taken before the move still line up.
            internal const string TileDecode = "MapRenderer.Tile.Decode";
        }

        private static readonly ProfilerMarker PmTileDecode =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.TileDecode);

        internal SharedTileDecode(byte[] bytes, ITileDecoder decoder)
        {
            _bytes   = bytes;
            _decoder = decoder;
        }

        /// <summary>Returns the decoded <see cref="IDecodedTile"/>, decoding it exactly once. The decode
        /// (and the cached-fault rethrow) happens under <see cref="_gate"/>: a second caller arriving
        /// mid-decode blocks on the lock — the exact semantics it needs, since it wants the result and must
        /// not start a second decode.</summary>
        public IDecodedTile GetOrDecode()
        {
            lock (_gate)
            {
                _fault?.Throw();
                if (_tile != null) return _tile;

                try
                {
                    using (PmTileDecode.Auto())
                        return _tile = _decoder.Decode(_bytes);
                }
                catch (System.Exception ex)
                {
                    _fault = ExceptionDispatchInfo.Capture(ex);
                    throw;
                }
            }
        }
    }
}
