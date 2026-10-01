using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// What the current cover is made of, for the questions the swap and the role pass ask. <see cref="Rebuild"/> fills it in one fixed
    /// order: the cover's own tiles and their strict ancestors first, then the keys of the records that serve them. A cover tile has no
    /// record of its own under overzoom, so several cover tiles can share one served key.
    /// </summary>
    internal sealed class CoverIndex
    {
        private readonly HashSet<TileId>    _tiles      = new();
        private readonly HashSet<TileId>    _ancestors  = new();
        private readonly HashSet<LoadedKey> _servedKeys = new();

        /// <summary>Replaces the index with the one for <paramref name="cover"/>, read through <paramref name="sources"/>' serving rule.</summary>
        public void Rebuild(IReadOnlyList<TileId> cover, SourceRegistry sources)
        {
            _servedKeys.Clear();
            _tiles.Clear();
            _ancestors.Clear();
            for (int i = 0; i < cover.Count; i++)
            {
                _tiles.Add(cover[i]);
                foreach (TileId up in TileAncestry.Ancestors(cover[i]))
                    if (!_ancestors.Add(up)) break; // its own ancestors are already in
            }

            for (int i = 0; i < cover.Count; i++)
                foreach (LoadedKey key in new ServingKeyWalk(sources, cover[i]))
                    _servedKeys.Add(key);
        }

        /// <summary>Empties the served keys only, and keeps the tiles and ancestors until the next <see cref="Rebuild"/>: a source change
        /// invalidates the keys, but the cover itself is unchanged.</summary>
        public void ForgetServedKeys() => _servedKeys.Clear();

        /// <summary>True iff <paramref name="tile"/> is a cover tile.</summary>
        public bool Contains(TileId tile) => _tiles.Contains(tile);

        /// <summary>True iff a strict descendant of <paramref name="tile"/> is a cover tile.</summary>
        public bool HasDescendantInCover(TileId tile) => _ancestors.Contains(tile);

        /// <summary>True iff a strict ancestor of <paramref name="tile"/> is a cover tile.</summary>
        public bool HasAncestorInCover(TileId tile)
        {
            foreach (TileId up in TileAncestry.Ancestors(tile))
                if (_tiles.Contains(up)) return true;

            return false;
        }

        /// <summary>True iff a strict ancestor or descendant of <paramref name="tile"/> is a cover tile.</summary>
        public bool HasRelativeInCover(TileId tile) => HasDescendantInCover(tile) || HasAncestorInCover(tile);

        /// <summary>True iff the record <paramref name="key"/> serves a cover tile.</summary>
        public bool Serves(LoadedKey key) => _servedKeys.Contains(key);
    }
}
