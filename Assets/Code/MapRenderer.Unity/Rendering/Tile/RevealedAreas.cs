using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using UnityEngine;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>How many shown tiles a record key serves, and the visibility groups revealed on them (their union).</summary>
    internal readonly struct RecordReveal
    {
        public int   Count  { get; init; }
        public ulong Groups { get; init; }
    }

    /// <summary>
    /// What the swap has shown: the shown areas (the revealed cover, Hold and Bridge tiles), the groups revealed on a tile revealed group by
    /// group, and for each record key the shown tiles it serves. The bookkeeping only: it queues nothing, and the manager shows and hides items.
    /// </summary>
    internal sealed class RevealedAreas
    {
        private readonly SourceRegistry _sources;

        /// <summary>The shown areas. Every relative question of the swap reads this set.</summary>
        private readonly HashSet<TileId> _tiles = new();

        /// <summary>For each tile, how many shown tiles lie strictly below it. An entry exists iff the count is above zero.</summary>
        private readonly Dictionary<TileId, int> _below = new();

        /// <summary>The groups revealed so far on a tile revealed group by group. A shown tile with no entry shows every group.</summary>
        private readonly Dictionary<TileId, ulong> _groupMask = new();

        /// <summary>For each serving key, the shown tiles it serves. An entry exists iff the count is above zero, whether or not the record
        /// exists, so a record that registers late reads it at once. It is a function of the areas, the masks and the serving map, so a
        /// record's teardown leaves it alone.</summary>
        private readonly Dictionary<LoadedKey, RecordReveal> _records = new();

        /// <summary>The groups a record showed when the current swap step concealed it.</summary>
        private readonly Dictionary<LoadedKey, ulong> _concealed = new();

        public RevealedAreas(SourceRegistry sources) => _sources = sources;

        /// <summary>True iff <paramref name="tile"/> is a shown area.</summary>
        public bool IsShown(TileId tile) => _tiles.Contains(tile);

        /// <summary>True iff a strict descendant of <paramref name="tile"/> is shown.</summary>
        public bool HasShownDescendant(TileId tile) => _below.ContainsKey(tile);

        /// <summary>True iff a strict ancestor of <paramref name="tile"/> is shown.</summary>
        public bool HasShownAncestor(TileId tile)
        {
            foreach (TileId up in TileAncestry.Ancestors(tile))
                if (_tiles.Contains(up)) return true;

            return false;
        }

        /// <summary>True iff a strict ancestor or descendant of <paramref name="tile"/> is shown.</summary>
        public bool HasShownRelative(TileId tile) => HasShownDescendant(tile) || HasShownAncestor(tile);

        /// <summary>Enumerates the shown areas without allocating. Do not change the areas while enumerating.</summary>
        public HashSet<TileId>.Enumerator GetEnumerator() => _tiles.GetEnumerator();

        /// <summary>The groups revealed on <paramref name="tile"/>: none when it is not shown, all when the swap revealed it whole.</summary>
        public ulong GroupsRevealedOn(TileId tile)
            => !_tiles.Contains(tile) ? 0UL : _groupMask.TryGetValue(tile, out ulong groups) ? groups : ulong.MaxValue;

        /// <summary>True iff <paramref name="key"/> serves at least one shown tile.</summary>
        public bool IsRecordShown(LoadedKey key) => _records.ContainsKey(key);

        /// <summary>The groups revealed on the shown tiles <paramref name="key"/> serves, or none.</summary>
        public ulong GroupsOf(LoadedKey key) => _records.TryGetValue(key, out RecordReveal reveal) ? reveal.Groups : 0UL;

        /// <summary>The groups the record showed when this swap step concealed it, or none.</summary>
        public ulong ConcealedGroups(LoadedKey key) => _concealed.TryGetValue(key, out ulong groups) ? groups : 0UL;

        /// <summary>Clears the records concealed by the previous swap step.</summary>
        public void BeginStep() => _concealed.Clear();

        /// <summary>Fills <paramref name="into"/> with the shown tiles that have revealed some, not all, of <paramref name="allGroups"/>.</summary>
        public void CollectPartial(List<TileId> into, ulong allGroups)
        {
            into.Clear();
            foreach (KeyValuePair<TileId, ulong> mask in _groupMask)
                if ((mask.Value & allGroups) != allGroups) into.Add(mask.Key);
        }

        /// <summary>Marks <paramref name="tile"/> shown and counts it on every record key that serves it. False when it was already shown.</summary>
        public bool Mark(TileId tile)
        {
            if (!_tiles.Add(tile)) return false;
            CountBelow(tile, +1);
            foreach (LoadedKey key in new ServingKeyWalk(_sources, tile))
            {
                _records.TryGetValue(key, out RecordReveal reveal);
                _records[key] = new RecordReveal { Count = reveal.Count + 1, Groups = reveal.Count == 0 ? 0UL : reveal.Groups };
            }

            return true;
        }

        /// <summary>Sets the groups revealed so far on the shown tile <paramref name="tile"/>.</summary>
        public void SetGroupMask(TileId tile, ulong revealed) => _groupMask[tile] = revealed;

        /// <summary>Adds <paramref name="groups"/> to what <paramref name="key"/> shows. Only a key that a shown tile serves has an entry, and
        /// every caller passes one.</summary>
        /// <returns>The groups that were not shown before, or none.</returns>
        public ulong RevealGroups(LoadedKey key, ulong groups)
        {
            if (!_records.TryGetValue(key, out RecordReveal reveal)) return 0UL;
            ulong added = groups & ~reveal.Groups;
            if (added != 0) _records[key] = new RecordReveal { Count = reveal.Count, Groups = reveal.Groups | groups };
            return added;
        }

        /// <summary>Removes <paramref name="tile"/> from the shown areas, with its group mask. False when it was not shown.</summary>
        public bool Remove(TileId tile)
        {
            if (!_tiles.Remove(tile)) return false;
            _groupMask.Remove(tile);
            CountBelow(tile, -1);
            return true;
        }

        /// <summary>Takes one removed tile off <paramref name="key"/>. A key the removed tile served always has an entry.</summary>
        /// <returns>The record's state after the decrement: <c>Count == 0</c> means it served no other shown tile, and <c>Groups</c> is what it showed.</returns>
        public RecordReveal Release(LoadedKey key)
        {
            Debug.Assert(_records.ContainsKey(key));
            if (!_records.TryGetValue(key, out RecordReveal reveal)) return default;
            if (reveal.Count > 1)
            {
                var rest = new RecordReveal { Count = reveal.Count - 1, Groups = reveal.Groups };
                _records[key] = rest;
                return rest;
            }

            _records.Remove(key);
            return new RecordReveal { Count = 0, Groups = reveal.Groups };
        }

        /// <summary>Remembers that the current swap step concealed <paramref name="key"/> while it showed <paramref name="groups"/>.</summary>
        public void NoteConcealed(LoadedKey key, ulong groups) => _concealed[key] = groups;

        /// <summary>Rebuilds the record entries from the shown areas against the current source slots.</summary>
        public void Recount()
        {
            _records.Clear();
            foreach (TileId tile in _tiles)
            {
                ulong tileGroups = GroupsRevealedOn(tile);
                foreach (LoadedKey key in new ServingKeyWalk(_sources, tile))
                {
                    _records.TryGetValue(key, out RecordReveal reveal);
                    _records[key] = new RecordReveal { Count = reveal.Count + 1, Groups = reveal.Groups | tileGroups };
                }
            }
        }

        /// <summary>Moves the group masks to a new group layout, then recounts. A tile revealed whole stays so.</summary>
        public void Remap(VisibilityGroupMap groups, int[] previousGroupOfSlot)
        {
            var tiles = new List<TileId>(_groupMask.Keys);
            foreach (TileId tile in tiles)
                _groupMask[tile] = groups.RemapMask(_groupMask[tile], previousGroupOfSlot);
            Recount();
        }

        /// <summary>Forgets every area and record: the records are gone.</summary>
        public void Clear()
        {
            _tiles.Clear();
            _below.Clear();
            _groupMask.Clear();
            _records.Clear();
            _concealed.Clear();
        }

        /// <summary>Adds <paramref name="delta"/> (+1 or -1) to the shown-descendant count of every strict ancestor of <paramref name="tile"/>.</summary>
        private void CountBelow(TileId tile, int delta)
        {
            foreach (TileId up in TileAncestry.Ancestors(tile))
            {
                _below.TryGetValue(up, out int n);
                n += delta;
                if (n > 0) _below[up] = n; else _below.Remove(up);
            }
        }
    }
}
