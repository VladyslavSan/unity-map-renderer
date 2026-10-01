using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A tile source whose fetches wait on gates a test controls, so a test decides when each tile is ready. Every fetch
    /// answers the sample tile. Build one with <see cref="Open"/>, <see cref="Closed"/> or <see cref="HoldingEachRequest"/>.
    /// </summary>
    internal sealed class GatedTileSource
    {
        private readonly object _lock = new object();
        private readonly byte[] _bytes = SampleTileFixture.Bytes();
        private readonly bool _holdEachRequest;
        private readonly UniTaskCompletionSource<bool> _all;
        private readonly Dictionary<int, UniTaskCompletionSource<bool>> _zoomGates = new Dictionary<int, UniTaskCompletionSource<bool>>();
        private readonly Dictionary<TileId, UniTaskCompletionSource<bool>> _tileGates = new Dictionary<TileId, UniTaskCompletionSource<bool>>();
        private readonly List<UniTaskCompletionSource<bool>> _requestGates = new List<UniTaskCompletionSource<bool>>();

        /// <summary>The source to wire into a view.</summary>
        public TestDataSource Source { get; }

        /// <summary>Every tile the source was asked for, in order. A tile asked for twice shows twice.</summary>
        public List<TileId> Requested { get; } = new List<TileId>();

        private GatedTileSource(bool closed, bool holdEachRequest)
        {
            _holdEachRequest = holdEachRequest;
            if (closed) _all = new UniTaskCompletionSource<bool>();
            Source = new TestDataSource((id, ct) => FetchAsync(id));
        }

        /// <summary>A source whose fetches pass at once, until a tile or zoom gate closes.</summary>
        public static GatedTileSource Open() => new GatedTileSource(closed: false, holdEachRequest: false);

        /// <summary>A source whose fetches all wait, until <see cref="OpenAll"/>.</summary>
        public static GatedTileSource Closed() => new GatedTileSource(closed: true, holdEachRequest: false);

        /// <summary>A source whose every request waits on its own gate, until <see cref="ReleaseRequested"/> frees the requests made so far.</summary>
        public static GatedTileSource HoldingEachRequest() => new GatedTileSource(closed: false, holdEachRequest: true);

        private async UniTask<TileResponse> FetchAsync(TileId tile)
        {
            UniTaskCompletionSource<bool> request = null;
            UniTaskCompletionSource<bool> zone;
            lock (_lock)
            {
                Requested.Add(tile);
                if (_holdEachRequest)
                {
                    request = new UniTaskCompletionSource<bool>();
                    _requestGates.Add(request);
                }

                _zoomGates.TryGetValue(tile.Z, out zone);
            }

            if (request != null) await request.Task;
            if (_all != null) await _all.Task;
            if (zone != null) await zone.Task;

            // Looked up after the zoom gate opens, so a tile gate closed while the tile waited still holds it.
            UniTaskCompletionSource<bool> single;
            lock (_lock) _tileGates.TryGetValue(tile, out single);
            if (single != null) await single.Task;
            return new TileResponse(_bytes, TileEncoding.Mvt);
        }

        /// <summary>Opens the gate of a <see cref="Closed"/> source, for the fetches pending and every later one.</summary>
        public void OpenAll()
        {
            _all?.TrySetResult(true);
        }

        /// <summary>Frees every request made so far, including a tile requested twice. A request made later waits on its own gate
        /// (see <see cref="HoldingEachRequest"/>).</summary>
        public void ReleaseRequested()
        {
            List<UniTaskCompletionSource<bool>> snapshot;
            lock (_lock) snapshot = new List<UniTaskCompletionSource<bool>>(_requestGates);
            foreach (var gate in snapshot) gate.TrySetResult(true);
        }

        /// <summary>Holds back the fetches of every tile at <paramref name="zoom"/> requested from now on, until <see cref="OpenZoom"/>.</summary>
        public void CloseZoom(int zoom)
        {
            lock (_lock) _zoomGates[zoom] = new UniTaskCompletionSource<bool>();
        }

        /// <summary>Opens the zoom gate.</summary>
        public void OpenZoom(int zoom)
        {
            UniTaskCompletionSource<bool> gate;
            lock (_lock) _zoomGates.Remove(zoom, out gate);
            gate?.TrySetResult(true);
        }

        /// <summary>Holds back the fetch of one tile requested from now on, until <see cref="OpenTile"/>.</summary>
        public void CloseTile(TileId tile)
        {
            lock (_lock) _tileGates[tile] = new UniTaskCompletionSource<bool>();
        }

        /// <summary>Opens the tile gate.</summary>
        public void OpenTile(TileId tile)
        {
            UniTaskCompletionSource<bool> gate;
            lock (_lock) _tileGates.Remove(tile, out gate);
            gate?.TrySetResult(true);
        }

        /// <summary>Opens every gate, so a failing test leaves no fetch hung in teardown.</summary>
        public void OpenEverything()
        {
            OpenAll();
            ReleaseRequested();
            List<UniTaskCompletionSource<bool>> gates;
            lock (_lock)
            {
                gates = new List<UniTaskCompletionSource<bool>>(_zoomGates.Values);
                gates.AddRange(_tileGates.Values);
                _zoomGates.Clear();
                _tileGates.Clear();
            }

            foreach (var gate in gates) gate.TrySetResult(true);
        }
    }
}
