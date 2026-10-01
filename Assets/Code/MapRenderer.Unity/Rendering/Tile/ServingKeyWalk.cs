using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>The struct enumerator behind <c>TileManager.ServingKeys</c>. It is mutable and <c>GetEnumerator</c>
    /// returns a copy, so never call <c>MoveNext</c> on a stored value.</summary>
    internal struct ServingKeyWalk
    {
        private readonly SourceRegistry _sources;
        private readonly TileId _tile;
        private int _slot;
        private LoadedKey _current;

        public ServingKeyWalk(SourceRegistry sources, TileId tile)
        {
            _sources = sources;
            _tile    = tile;
            _slot    = -1;
            _current = default;
        }

        public LoadedKey Current => _current;

        public ServingKeyWalk GetEnumerator() => this;

        public bool MoveNext()
        {
            while (++_slot < _sources.Count)
            {
                if (!_sources.AdmitsTile(_slot, _tile)) continue;
                _current = new LoadedKey(_sources.ServingTile(_slot, _tile), _slot);
                return true;
            }

            return false;
        }
    }
}
