using System;
using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// The composite key for a prepared (built) tile-layer <see cref="Mesh"/> — a style token, the tile
    /// address, and the global material index (layerId) of the render layer the mesh belongs to. Value-type +
    /// <see cref="IEquatable{T}"/> so a <see cref="Dictionary{TKey,TValue}"/> keyed by it is zero-boxing
    /// (mirrors <c>TileManager.LoadedKey</c>).
    /// </summary>
    internal readonly struct PreparedKey : IEquatable<PreparedKey>
    {
        public readonly StyleToken Style;
        public readonly TileId     Tile;
        public readonly int        LayerId;

        public PreparedKey(StyleToken style, TileId tile, int layerId)
        {
            Style   = style;
            Tile    = tile;
            LayerId = layerId;
        }

        public bool Equals(PreparedKey other)
            => LayerId == other.LayerId && Tile.Equals(other.Tile) && Style.Equals(other.Style);
        public override bool Equals(object obj) => obj is PreparedKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Style.GetHashCode();
                h = h * 31 + Tile.GetHashCode();
                h = h * 31 + LayerId;
                return h;
            }
        }
    }

    /// <summary>
    /// A byte-budgeted LRU cache of prepared per-tile-layer <see cref="Mesh"/> objects, keyed by
    /// <see cref="PreparedKey"/>. It has the shape of <see cref="MapRenderer.Core.Data.TileCache"/> and owns
    /// and destroys the meshes it holds. Non-local invariant: a mesh is here only while its tile is out of
    /// the render cover (Model B). <see cref="Put"/> takes ownership in; <see cref="TryTake"/> removes the
    /// entry, so eviction never destroys a mesh that is drawn. A <see langword="null"/> mesh marks a layer
    /// with no geometry; <see cref="Contains"/> probes per-layer completeness before a full hit.
    /// Mutating calls run on the main thread only, because <c>Destroy</c> is main-thread; the lock only
    /// mirrors <c>TileCache</c>.
    /// </summary>
    internal sealed class PreparedTileCache : VerifiedDisposable
    {
        private struct Entry
        {
            public PreparedKey Key;
            public Mesh        Mesh;  // null = "empty-layer" completeness marker (0 bytes)
            public long        Bytes;
        }

        private readonly long   _byteBudget;
        private readonly int    _countCap;
        private readonly Dictionary<PreparedKey, LinkedListNode<Entry>> _map;
        private readonly LinkedList<Entry> _list; // head = MRU, tail = LRU
        private readonly object _lock = new object();
        private long _bytesHeld;

        // Non-obvious why: a heading/tilt change churns which tiles cover the view at a constant tile count,
        // so a Tick puts Built tiles here. Reused nodes keep that Tick allocation-free, as the heading/tilt
        // case of MapViewLiveLoopTests.MapView_SteadyStateTick_DoesNotAllocateGCMemory asserts.
        // The pool holds min(countCap, MaxPrewarmedNodes) nodes; a larger burst falls back to `new`.
        private const int MaxPrewarmedNodes = 1024;
        private readonly Stack<LinkedListNode<Entry>> _nodePool;

        /// <summary>Test/telemetry observability — bumped by the caller (TileManager's Tick probe) at the
        /// whole-tile granularity, not per layer here. Plain mutable fields (like <c>ReleasedMidFlightCount</c>'s
        /// sibling counters), never read from the live tile loop's own decisions.</summary>
        internal int Hits;
        internal int Misses;

        /// <summary>Cumulative count of LRU-evicted entries (bumped internally, once per entry evicted
        /// out of <see cref="Put"/>'s while-loop — never by the caller). Telemetry only, never read from the
        /// live tile loop's own decisions.</summary>
        internal int Evictions;

        /// <summary>Current estimated bytes held (sum of every live entry's <see cref="EstimateBytes"/>).</summary>
        internal long BytesHeld { get { lock (_lock) return _bytesHeld; } }

        /// <summary>Current entry count (produced meshes AND empty-layer markers).</summary>
        internal int Count { get { lock (_lock) return _map.Count; } }

        /// <summary>The effective byte budget this cache evicts against (the constructor clamps a
        /// non-positive <c>byteBudget</c> argument up to <see cref="long.MaxValue"/> — "unbounded" — so this
        /// is the LIVE applied value, not necessarily the raw configured one).</summary>
        internal long ByteBudget => _byteBudget;

        /// <summary>The effective entry-count cap this cache evicts against (see <see cref="ByteBudget"/>
        /// — a non-positive <c>countCap</c> argument clamps up to <see cref="int.MaxValue"/>).</summary>
        internal int MaxCount => _countCap;

        /// <param name="byteBudget">Evict LRU while held bytes exceed this. &lt;= 0 ⇒ effectively unbounded
        /// by bytes (the count cap still applies).</param>
        /// <param name="countCap">Belt-and-suspenders entry-count cap. &lt;= 0 ⇒ effectively unbounded by
        /// count (the byte budget still applies).</param>
        internal PreparedTileCache(long byteBudget, int countCap)
        {
            _byteBudget = byteBudget > 0 ? byteBudget : long.MaxValue;
            _countCap   = countCap   > 0 ? countCap   : int.MaxValue;
            _map        = new Dictionary<PreparedKey, LinkedListNode<Entry>>(256);
            _list       = new LinkedList<Entry>();

            int prewarm = _countCap < MaxPrewarmedNodes ? _countCap : MaxPrewarmedNodes;
            _nodePool = new Stack<LinkedListNode<Entry>>(prewarm);
            for (int i = 0; i < prewarm; i++)
                _nodePool.Push(new LinkedListNode<Entry>(default));
        }

        /// <summary>Pop a pooled (detached) node, or allocate one on pool exhaustion (only reachable when a
        /// single burst evicts/inserts more than <see cref="MaxPrewarmedNodes"/> distinct entries at once —
        /// correctness over the steady-state zero-alloc budget in that pathological case).</summary>
        private LinkedListNode<Entry> RentNode(in Entry value)
        {
            LinkedListNode<Entry> node = _nodePool.Count > 0 ? _nodePool.Pop() : new LinkedListNode<Entry>(default);
            node.Value = value;
            return node;
        }

        /// <summary>Detach-and-pool a node removed from <see cref="_list"/>/<see cref="_map"/> (clears its
        /// Value so a held Mesh reference doesn't linger in the pool) so a future Put can reuse it instead of
        /// allocating.</summary>
        private void ReturnNode(LinkedListNode<Entry> node)
        {
            node.Value = default;
            _nodePool.Push(node);
        }

        /// <summary>Recency-neutral presence probe (does NOT promote to MRU, does NOT remove) — used by the
        /// completeness check across a tile's dense layerId set before committing to a hit.</summary>
        internal bool Contains(in PreparedKey key)
        {
            lock (_lock) return _map.ContainsKey(key);
        }

        /// <summary>
        /// HIT: removes the entry (single-owner Model B — see class remarks) and hands its <see cref="Mesh"/>
        /// out (may be <see langword="null"/>, the empty-layer marker — still a valid hit). Returns
        /// <see langword="false"/> on a miss (<paramref name="mesh"/> left <see langword="default"/>). Does
        /// NOT bump <see cref="Hits"/>/<see cref="Misses"/> — the caller counts once per whole-tile probe, not
        /// once per layer.
        /// </summary>
        internal bool TryTake(in PreparedKey key, out Mesh mesh)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _map.Remove(key);
                    _bytesHeld -= node.Value.Bytes;
                    mesh = node.Value.Mesh;
                    ReturnNode(node);
                    return true;
                }
            }
            mesh = default;
            return false;
        }

        /// <summary>
        /// Inserts (taking ownership of) a prepared <paramref name="mesh"/> for <paramref name="key"/>
        /// (<see langword="null"/> = empty-layer marker, 0 bytes). A key collision destroys the previous
        /// entry's mesh before replacing — safe: a colliding Put means the prior entry was never taken out via
        /// <see cref="TryTake"/>, so nothing else can still reference it. Evicts LRU entries (destroying their
        /// meshes) while over the byte budget or count cap.
        /// </summary>
        internal void Put(in PreparedKey key, Mesh mesh)
        {
            long bytes = EstimateBytes(mesh);
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _bytesHeld -= existing.Value.Bytes;
                    DestroyMesh(existing.Value.Mesh);
                    _list.Remove(existing);
                    _map.Remove(key);
                    ReturnNode(existing);
                }

                var node = RentNode(new Entry { Key = key, Mesh = mesh, Bytes = bytes });
                _list.AddFirst(node);
                _map[key] = node;
                _bytesHeld += bytes;

                while ((_bytesHeld > _byteBudget || _map.Count > _countCap) && _list.Last != null)
                {
                    var lru = _list.Last;
                    _list.RemoveLast();
                    _map.Remove(lru.Value.Key);
                    _bytesHeld -= lru.Value.Bytes;
                    DestroyMesh(lru.Value.Mesh);
                    ReturnNode(lru);
                    Evictions++;
                }
            }
        }

        /// <summary>Estimated VRAM bytes for a prepared mesh — vertex buffer (stream 0's stride, since every
        /// built mesh has a populated stream 0) times vertex count, plus a flat 4 bytes/index (UInt32
        /// index format — see <see cref="Style.MeshDataPayload.Upload"/>). <see langword="null"/> (the
        /// empty-layer marker) is 0 bytes.</summary>
        private static long EstimateBytes(Mesh m)
        {
            if (m == null) return 0;
            long stride = m.GetVertexBufferStride(0);
            return (long)m.vertexCount * stride + (long)m.GetIndexCount(0) * 4;
        }

        /// <summary>Same main-thread guard <c>TileManager.DestroyTrackedMeshes</c> uses.</summary>
        private static void DestroyMesh(Mesh m) => m.DestroySafely(allowDestroyingAssets: true);

        /// <summary>
        /// Destroys every held <see cref="Mesh"/> and empties the cache — reusable afterwards (unlike a true
        /// <see cref="IDisposable"/> teardown). Called by <see cref="Dispose"/> (teardown) and by
        /// <c>TileManager</c>'s tile-buffer-clip purge (a bake-parameter change, unrelated to restyle).
        /// </summary>
        internal void Clear()
        {
            lock (_lock)
            {
                // Return every node to the pool (not just discard) — a restyle-triggered Clear() shouldn't
                // force the FIRST post-restyle Puts to fall back to fresh allocation.
                foreach (var node in _map.Values)
                {
                    DestroyMesh(node.Value.Mesh);
                    node.Value = default;
                    _nodePool.Push(node);
                }
                _map.Clear();
                _list.Clear();
                _bytesHeld = 0;
            }
        }

        /// <summary>Destroys every held <see cref="Mesh"/> and clears the cache. Called by
        /// <c>TileManager.Dispose</c> AFTER the <c>_loaded</c> mesh-destroy loop and BEFORE the backend is
        /// disposed — the same "destroy meshes → dispose backend" ordering the rest of teardown honours.
        /// Idempotent (the base's disposed guard makes a repeat call a no-op).</summary>
        protected override void DoDispose() => Clear();
    }
}
