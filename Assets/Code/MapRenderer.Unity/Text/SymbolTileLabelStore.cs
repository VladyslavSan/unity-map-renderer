// Engine-free despite living under Assets/Code/MapRenderer.Unity/Text (like StyledSymbolTileBuilder): it
// references only MapRenderer.Core types (LabelInstance, TileId) — NO `using UnityEngine`. Co-located with
// the symbol subsystem that owns it, and compiled by BOTH the Unity runner and Tools/core-tests (the
// matching <Compile Include> lives in Tools/core-tests/core-tests.csproj) so its lifecycle logic — the part
// with the subtle async/cache races — gets the fast headless tests. Do NOT add a UnityEngine reference.

using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// The per-<c>(source, tile)</c> label bookkeeping for <see cref="SymbolLabelSubsystem"/>, split out as a
    /// pure engine-free unit so its races are headless-testable. It mirrors the tile MESH lifecycle
    /// (<c>TileManager</c> Model B): a tile is either <b>active</b> (in cover — its labels render) or
    /// <b>cached</b> (out of cover but its meshes are held in the <c>PreparedTileCache</c> — labels kept warm
    /// but NOT rendered), and moves between the two on release / cache-hit exactly as its meshes do.
    ///
    /// <para><b>Why this exists (the bug it fixes).</b> Labels are built from a tile's freshly-FETCHED bytes.
    /// When a tile leaves cover its meshes transfer to the prepared cache; when it re-enters via a cache HIT
    /// there is NO fetch — so if labels were simply dropped on release they would never be rebuilt, and the
    /// tile would show its geometry with no labels. So release-to-cache KEEPS the labels here (bounded FIFO,
    /// oldest-released evicted first, sized to the mesh cache's count cap so labels always outlive their
    /// meshes) and a cache hit restores them.</para>
    ///
    /// <para><b>Async build race.</b> A label build awaits glyph fetches, so a tile can be released — moved to
    /// the cached side — WHILE its build is still in flight. <see cref="BeginBuild"/> hands back a generation
    /// token and <see cref="CompleteBuild"/> commits only if that token still owns the entry in EITHER map, so
    /// a released-mid-build tile still gets its labels (written into the cached entry) rather than silently
    /// losing them, and a superseded build is discarded.</para>
    /// </summary>
    public sealed class SymbolTileLabelStore
    {
        public readonly struct Key : System.IEquatable<Key>
        {
            public readonly string SourceId;
            public readonly TileId Tile;
            public Key(string sourceId, TileId tile) { SourceId = sourceId; Tile = tile; }
            public bool Equals(Key other) => SourceId == other.SourceId && Tile.Equals(other.Tile);
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => (SourceId?.GetHashCode() ?? 0) * 397 ^ Tile.GetHashCode();
        }

        // One tile's labels + the generation of the build that owns the slot (for the stale-guard). Mutable so
        // an in-flight build can write Labels into the SAME object after a release has moved it between maps.
        private sealed class Entry { public int Generation; public List<LabelInstance> Labels; }

        private readonly Dictionary<Key, Entry> _active = new Dictionary<Key, Entry>();
        // Cached (out-of-cover) tiles as a FIFO: the LinkedList is release order (head = oldest released), the
        // dictionary is the O(1) index. Insertion order already mirrors the mesh cache's eviction order among
        // out-of-cover tiles (both are touched only on release + hit), so no recency bump is needed.
        private readonly Dictionary<Key, LinkedListNode<KeyedEntry>> _cachedIndex =
            new Dictionary<Key, LinkedListNode<KeyedEntry>>();
        private readonly LinkedList<KeyedEntry> _cachedOrder = new LinkedList<KeyedEntry>();
        private readonly struct KeyedEntry
        {
            public readonly Key Key;
            public readonly Entry Entry;
            public KeyedEntry(Key key, Entry entry) { Key = key; Entry = entry; }
        }

        // Max cached (out-of-cover) tiles kept warm; <= 0 == unbounded (mirrors a count-unbounded mesh cache).
        private readonly int _cacheCap;
        private int _genCounter;

        public SymbolTileLabelStore(int cacheCap) { _cacheCap = cacheCap; }

        /// <summary>Active (in-cover) tile count — test/telemetry.</summary>
        public int ActiveTileCount => _active.Count;

        /// <summary>Cached (out-of-cover, kept-warm) tile count — test/telemetry.</summary>
        public int CachedTileCount => _cachedIndex.Count;

        /// <summary>
        /// Reserve an active slot for a (re)build of <paramref name="key"/> and return the generation token the
        /// matching <see cref="CompleteBuild"/> must present. A fresh fetch supersedes any cached copy of the
        /// tile (its bytes changed / it fully missed the cache), so the cached entry is dropped here.
        /// </summary>
        public int BeginBuild(Key key)
        {
            RemoveCached(key);
            int gen = ++_genCounter;
            _active[key] = new Entry { Generation = gen, Labels = null };
            return gen;
        }

        /// <summary>
        /// Commit <paramref name="labels"/> for <paramref name="key"/> iff the build identified by
        /// <paramref name="gen"/> still owns the tile's entry (in the active OR cached map — a release may have
        /// moved it). Returns true when committed, false when the build was superseded or the tile was dropped.
        /// </summary>
        public bool CompleteBuild(Key key, int gen, List<LabelInstance> labels)
        {
            Entry e = FindCurrent(key);
            if (e == null || e.Generation != gen) return false; // superseded or dropped mid-build
            e.Labels = labels;
            return true;
        }

        /// <summary>
        /// A tile left cover. <paramref name="transferredToCache"/> true ⇒ its meshes went to the prepared
        /// cache, so keep its labels warm on the cached side (evicting the oldest-released if over the cap);
        /// false ⇒ a true eviction (cache disabled / not built), so drop its labels outright.
        /// </summary>
        public void Release(Key key, bool transferredToCache)
        {
            if (_active.TryGetValue(key, out Entry e))
            {
                _active.Remove(key);
                if (transferredToCache) EnqueueCached(key, e);
            }
            else if (!transferredToCache)
            {
                RemoveCached(key); // not active but a stale cached copy exists → drop it
            }
        }

        /// <summary>A tile re-entered cover via a prepared-cache HIT (no fetch): move its kept-warm labels back
        /// to the active set so they render again. A no-op if nothing was cached for it.</summary>
        public void Restore(Key key)
        {
            if (RemoveCached(key, out Entry e)) _active[key] = e;
        }

        /// <summary>Aggregate every ACTIVE tile's labels into <paramref name="output"/> for this frame's
        /// placement pass. Cached (out-of-cover) tiles are deliberately excluded — they must not render.</summary>
        public void CollectInto(List<LabelInstance> output)
        {
            output.Clear();
            foreach (KeyValuePair<Key, Entry> kv in _active)
                if (kv.Value.Labels != null) output.AddRange(kv.Value.Labels);
        }

        /// <summary>Drop everything (a restyle purges the mesh cache too).</summary>
        public void Clear()
        {
            _active.Clear();
            _cachedIndex.Clear();
            _cachedOrder.Clear();
        }

        private Entry FindCurrent(Key key)
        {
            if (_active.TryGetValue(key, out Entry a)) return a;
            if (_cachedIndex.TryGetValue(key, out LinkedListNode<KeyedEntry> node)) return node.Value.Entry;
            return null;
        }

        private void EnqueueCached(Key key, Entry entry)
        {
            RemoveCached(key); // keep active/cached disjoint (defensive — a re-release shouldn't double-insert)
            LinkedListNode<KeyedEntry> node = _cachedOrder.AddLast(new KeyedEntry(key, entry));
            _cachedIndex[key] = node;
            if (_cacheCap > 0 && _cachedIndex.Count > _cacheCap)
            {
                LinkedListNode<KeyedEntry> oldest = _cachedOrder.First; // FIFO: evict the oldest-released
                _cachedOrder.RemoveFirst();
                _cachedIndex.Remove(oldest.Value.Key);
            }
        }

        private bool RemoveCached(Key key) => RemoveCached(key, out _);

        private bool RemoveCached(Key key, out Entry entry)
        {
            if (_cachedIndex.TryGetValue(key, out LinkedListNode<KeyedEntry> node))
            {
                entry = node.Value.Entry;
                _cachedOrder.Remove(node);
                _cachedIndex.Remove(key);
                return true;
            }
            entry = null;
            return false;
        }
    }
}
