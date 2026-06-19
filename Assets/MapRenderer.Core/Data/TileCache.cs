using System;
using System.Collections.Generic;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.Data
{
    /// <summary>
    /// A capacity-bounded LRU (Least-Recently-Used) in-memory cache keyed by <see cref="TileId"/>.
    /// Thread-safe: all public operations are guarded by a single lock.
    /// <para>
    /// Implementation: a <see cref="Dictionary{TileId,LinkedListNode}"/> for O(1) keyed lookup +
    /// a <see cref="LinkedList{T}"/> that tracks recency (head = MRU, tail = LRU).
    /// On a cache hit, the node is moved to head. On insert beyond capacity, the tail (LRU) is
    /// evicted before insertion.
    /// </para>
    /// </summary>
    public sealed class TileCache
    {
        private readonly int _capacity;
        private readonly Dictionary<TileId, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry>                         _list;
        private readonly object                                         _lock = new object();

        /// <summary>Maximum number of entries the cache can hold.</summary>
        public int Capacity => _capacity;

        /// <summary>Current number of entries in the cache.</summary>
        public int Count
        {
            get { lock (_lock) { return _map.Count; } }
        }

        public TileCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");
            _capacity = capacity;
            _map      = new Dictionary<TileId, LinkedListNode<CacheEntry>>(capacity);
            _list     = new LinkedList<CacheEntry>();
        }

        /// <summary>
        /// Attempts to get a cached <see cref="TileResponse"/> for <paramref name="id"/>.
        /// On a hit, promotes the entry to MRU. Returns <c>true</c> on hit.
        /// </summary>
        public bool TryGet(TileId id, out TileResponse response)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(id, out var node))
                {
                    // Promote to MRU (head of list).
                    _list.Remove(node);
                    _list.AddFirst(node);
                    response = node.Value.Response;
                    return true;
                }
            }
            response = default;
            return false;
        }

        /// <summary>
        /// Inserts or updates a cached response for <paramref name="id"/>. If updating an
        /// existing entry, promotes it to MRU. If inserting beyond capacity, evicts the LRU entry.
        /// </summary>
        public void Put(TileId id, TileResponse response)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(id, out var existing))
                {
                    // Update value + promote.
                    existing.Value = new CacheEntry(id, response);
                    _list.Remove(existing);
                    _list.AddFirst(existing);
                    return;
                }

                // Evict LRU if at capacity.
                if (_map.Count >= _capacity)
                {
                    var lruNode = _list.Last;
                    if (lruNode != null)
                    {
                        _list.RemoveLast();
                        _map.Remove(lruNode.Value.Id);
                    }
                }

                var newNode = new LinkedListNode<CacheEntry>(new CacheEntry(id, response));
                _list.AddFirst(newNode);
                _map[id] = newNode;
            }
        }

        /// <summary>
        /// Returns <c>true</c> if <paramref name="id"/> is present in the cache. Does NOT affect
        /// recency (use <see cref="TryGet"/> for a recency-promoting existence check).
        /// </summary>
        public bool ContainsKey(TileId id)
        {
            lock (_lock)
            {
                return _map.ContainsKey(id);
            }
        }

        /// <summary>Removes a specific entry from the cache if present.</summary>
        public void Remove(TileId id)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(id, out var node))
                {
                    _list.Remove(node);
                    _map.Remove(id);
                }
            }
        }

        /// <summary>Removes all entries from the cache.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _list.Clear();
                _map.Clear();
            }
        }

        // -----------------------------------------------------------------------------------------
        // Internal
        // -----------------------------------------------------------------------------------------

        private struct CacheEntry
        {
            public TileId       Id;
            public TileResponse Response;

            public CacheEntry(TileId id, TileResponse response)
            {
                Id       = id;
                Response = response;
            }
        }
    }
}
