using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;

namespace MapRenderer.Unity.Rendering.Tile
{
    internal sealed partial class TileManager
    {
        /// <summary>True iff <paramref name="tile"/> lies strictly between a shown tile and a cover tile, on one ancestry chain, in either direction.</summary>
        private bool IsBetweenHoldAndCover(TileId tile)
        {
            if (_coverIndex.HasDescendantInCover(tile)) return HasShownAncestor(tile);
            return _shownBelow.ContainsKey(tile) && _coverIndex.HasAncestorInCover(tile);
        }

        /// <summary>True iff some source has a record for <paramref name="tile"/>.</summary>
        private bool HasRecord(TileId tile)
        {
            // Every slot, admitted or not: an overzoomed tile outside a source's bounds is still served by its maxzoom ancestor's record.
            for (int slot = 0; slot < _sources.Count; slot++)
                if (_loaded.ContainsKey(new LoadedKey(_sources.ServingTile(slot, tile), slot))) return true;
            return false;
        }

        /// <summary>True iff every source that serves <paramref name="tile"/> has its record built, or rebuilding with its previous geometry
        /// still registered, and not waiting to retry, the background included. A missing record is not ready; a source that does not
        /// serve the tile counts for nothing. An absent or undecodable tile is built, so it is ready and empty.</summary>
        private bool IsReady(TileId tile)
        {
            foreach (LoadedKey key in ServingKeys(tile))
            {
                if (!_loaded.TryGetValue(key, out LoadedTile lt)) return false;
                if (!(lt.Built || (lt.Rebaking && lt.OldDrawHandles != null)) || lt.WaitingRetry) return false;
            }

            return true;
        }

        /// <summary>Reveals the next ready groups of a cover tile, in order: a group shows once every earlier group has. A group is ready when no
        /// record that serves the tile still holds one of its payloads. The first group revealed marks the tile shown. A group whose records
        /// this step just concealed for another tile shows at once, so a record shared by overzoom does not blink across a pan.</summary>
        private bool RevealReadyGroups(TileId tile, ulong revealed)
        {
            bool any     = false;
            bool blocked = false;
            for (int group = 0; group < _groups.GroupCount; group++)
            {
                ulong bit = 1UL << group;
                if ((revealed & bit) != 0) continue;
                if (!GroupCarried(tile, bit) && (blocked || !GroupReady(tile, bit)))
                {
                    blocked = true;
                    continue;
                }

                MarkTileShown(tile);
                revealed |= bit;
                _groupMask[tile] = revealed;
                foreach (LoadedKey key in ServingKeys(tile))
                    RevealGroups(key, bit);

                any = true;
            }

            return any;
        }

        /// <summary>True iff the group <paramref name="bit"/> has records for <paramref name="tile"/> and this swap step concealed every one of them
        /// while it showed that group, so a group a record never showed is not carried ahead of the groups before it.</summary>
        private bool GroupCarried(TileId tile, ulong bit)
        {
            bool any = false;
            foreach (LoadedKey key in ServingKeys(tile))
            {
                if ((_groups.GroupsOfSource(key.Slot) & bit) == 0) continue;
                if (!_concealedThisStep.TryGetValue(key, out ulong shown) || (shown & bit) == 0) return false;
                any = true;
            }

            return any;
        }

        /// <summary>True iff no record that serves <paramref name="tile"/> still holds a payload of the group <paramref name="bit"/>. A record
        /// that is missing, or has not reached its write step, holds every group of its source. One waiting to retry after a network fault holds
        /// none, so a dead source never keeps another source's groups hidden.</summary>
        private bool GroupReady(TileId tile, ulong bit)
        {
            foreach (LoadedKey key in ServingKeys(tile))
                if ((PendingGroups(key) & bit) != 0) return false;

            return true;
        }

        private ulong PendingGroups(LoadedKey key)
        {
            if (!_loaded.TryGetValue(key, out LoadedTile lt)) return _groups.GroupsOfSource(key.Slot);
            if (lt.WaitingRetry || lt.Built) return 0;
            return lt.GroupsDerived ? lt.PendingGroups : _groups.GroupsOfSource(key.Slot);
        }

        /// <summary>Runs every Update after the pump, and at the end of the drain. It hides a shown tile whose area left the view,
        /// replaces a shown tile by the ready tiles that cover it, shows a ready ancestor over shown descendants, and shows a cover
        /// tile that has no shown relative. Its shows and hides reach the backend in the one <see cref="FlushVisibility"/>.</summary>
        private void SwapStep()
        {
            if (Instanced == null) return;
            bool changed = false;
            _concealedThisStep.Clear();

            // 1. A shown tile with no relative in the cover: its area left the view.
            _swapTiles.Clear();
            foreach (TileId t in _revealedTiles) _swapTiles.Add(t);
            for (int i = 0; i < _swapTiles.Count; i++)
            {
                TileId tile = _swapTiles[i];
                if (_coverIndex.Contains(tile) || _coverIndex.HasRelativeInCover(tile)) continue;
                ConcealTile(tile);
                changed = true;
            }

            // 2. Zoom out: a ready ancestor in the cover, or a hidden Bridge below it, shows whole and hides the tiles under it.
            _swapTiles.Clear();
            foreach (TileId t in _revealedTiles) if (!_coverIndex.Contains(t)) _swapTiles.Add(t);
            for (int i = 0; i < _swapTiles.Count; i++)
            {
                TileId shown = _swapTiles[i];
                if (!IsShown(shown) || !TryFindCoveringAncestor(shown, out TileId ancestor)) continue;
                RevealTile(ancestor);
                for (int j = 0; j < _swapTiles.Count; j++)
                    if (IsShown(_swapTiles[j]) && TileAncestry.IsStrictAncestor(ancestor, _swapTiles[j])) ConcealTile(_swapTiles[j]);
                changed = true;
            }

            // 3. Zoom in: a shown tile whose every cover area is covered by ready tiles gives way to them.
            _swapTiles.Clear();
            foreach (TileId t in _revealedTiles) if (_coverIndex.HasDescendantInCover(t)) _swapTiles.Add(t);
            for (int i = 0; i < _swapTiles.Count; i++)
            {
                TileId shown = _swapTiles[i];
                if (!IsShown(shown)) continue;
                _swapCovering.Clear();
                if (!ChildrenCovered(shown)) continue;
                for (int j = 0; j < _swapCovering.Count; j++) RevealTile(_swapCovering[j]);
                ConcealTile(shown);
                changed = true;
            }

            // 4. A cover tile with no shown relative shows its next ready groups: a new area, a swing-back, or a record registered under a
            // relative since hidden. It starts once something is registered, and ends when every group shows.
            for (int i = 0; i < _selection.Cover.Count; i++)
            {
                TileId tile  = _selection.Cover[i];
                ulong  have  = GroupsRevealed(tile);
                if ((have & _groups.AllGroups) == _groups.AllGroups || HasShownRelative(tile)) continue;
                if (have == 0 && !(HasDrawHandles(tile) || IsReady(tile))) continue; // a ready tile with no items is shown too, so its labels draw
                if (RevealReadyGroups(tile, have)) changed = true;
            }

            // A held or bridged tile that left the cover part-revealed keeps revealing its groups as its records finish.
            _partialTiles.Clear();
            foreach (var kv in _groupMask)
                if ((kv.Value & _groups.AllGroups) != _groups.AllGroups) _partialTiles.Add(kv.Key);
            for (int i = 0; i < _partialTiles.Count; i++)
            {
                TileId tile = _partialTiles[i];
                if (!HasShownRelative(tile) && RevealReadyGroups(tile, _groupMask[tile])) changed = true;
            }

            if (changed) RecomputeRoles();
        }

        /// <summary>The shallowest ancestor of <paramref name="shown"/> that has a record, is not fully shown, and is ready, looking up to the
        /// cover tile above it. Between them it takes any loaded tile, a Bridge or a prepared one. Fails when no ancestor is in the cover.</summary>
        private bool TryFindCoveringAncestor(TileId shown, out TileId ancestor)
        {
            ancestor = default;
            bool found = false;
            foreach (TileId up in TileAncestry.Ancestors(shown))
            {
                bool inCover = _coverIndex.Contains(up);
                if ((inCover || HasRecord(up)) && !IsShown(up) && IsReady(up))
                {
                    ancestor = up;
                    found    = true;
                }

                if (inCover) return found;
            }

            return false; // no ancestor in the cover: the tile is held for descendants, which step 3 handles
        }

        /// <summary>True iff each child area of <paramref name="tile"/> is covered, filling <see cref="_swapCovering"/> with the ready tiles that cover it.</summary>
        private bool ChildrenCovered(TileId tile)
        {
            for (int child = 0; child < TileAncestry.ChildCount; child++)
                if (!AreaCovered(TileAncestry.Child(tile, child))) return false;

            return true;
        }

        /// <summary>An area is covered when it has no relative in the cover, or when its shallowest ready cover or Bridge tile exists, or when all four of its children are covered.</summary>
        private bool AreaCovered(TileId tile)
        {
            bool inCover = _coverIndex.Contains(tile);
            bool above   = _coverIndex.HasDescendantInCover(tile);
            if (!inCover && !above) return true;
            if ((inCover || HasRecord(tile)) && IsReady(tile))
            {
                _swapCovering.Add(tile);
                return true;
            }

            return above && ChildrenCovered(tile);
        }

        private bool HasDrawHandles(TileId tile)
        {
            // Every slot, admitted or not: an overzoomed tile outside a source's bounds is still served by its maxzoom ancestor's record.
            for (int slot = 0; slot < _sources.Count; slot++)
                if (_loaded.TryGetValue(new LoadedKey(_sources.ServingTile(slot, tile), slot), out LoadedTile lt) && (lt.DrawHandles != null || lt.OldDrawHandles != null)) return true;
            return false;
        }
    }
}
