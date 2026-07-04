using System;
using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// S82: the composite key for a prepared (built) tile-layer <see cref="Mesh"/> — a style token, the tile
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
    /// S82: a byte-budgeted LRU cache of prepared (built) per-tile-layer <see cref="Mesh"/> objects, keyed by
    /// <see cref="PreparedKey"/> — a structural clone of <see cref="MapRenderer.Core.Data.TileCache"/>
    /// (Dictionary + intrusive <see cref="LinkedList{T}"/> + single lock), specialised for byte-budget
    /// eviction and <see cref="Mesh"/> ownership/destruction instead of count-bounded raw-byte storage.
    ///
    /// <para><b>Ownership — Model B (take-on-hit / put-on-release), single owner at all times.</b> A mesh is
    /// in this cache IFF its tile is currently out of the render cover — the in-cover working copy lives in
    /// <c>TileManager._loaded</c>. <see cref="Put"/> (a Built tile leaving the cover) transfers ownership IN;
    /// <see cref="TryTake"/> (the tile re-entering the cover, a hit) transfers ownership OUT — the entry is
    /// REMOVED, not merely promoted, so an evictable cache entry is never simultaneously a live in-cover draw
    /// item (no pinning needed, and eviction can never destroy a mesh that is being drawn).</para>
    ///
    /// <para><b>Empty-layer completeness.</b> A <c>Mesh</c> value of <see langword="null"/> is a valid entry
    /// — the "this layer produced no geometry for this tile" marker (0 bytes). <see cref="Contains"/> lets the
    /// caller probe per-layer completeness (every dense layerId present, produced-or-marker) before treating a
    /// tile as a full cache hit.</para>
    ///
    /// <para><b>Main-thread only.</b> <c>UnityEngine.Object.Destroy</c>/<c>DestroyImmediate</c> are main-thread
    /// APIs, so every mutating call (<see cref="Put"/>, <see cref="TryTake"/>, <see cref="Dispose"/>) must run
    /// on the Unity main thread — <c>TileManager</c> only ever calls this from <c>Tick</c>/<c>ReleaseTile</c>/
    /// <c>Dispose</c>. The lock is kept for structural parity with <c>TileCache</c>'s template; there is no
    /// worker-thread access to guard against in practice.</para>
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

        // S82: a pre-warmed pool of LinkedListNode<Entry> instances — Put/TryTake/eviction reuse a detached
        // node instead of `new LinkedListNode<Entry>(...)`, so a Tick that evicts a Built tile into this
        // cache (a REAL scenario — a heading/tilt change rotates the viewport quad and can churn which
        // tiles cover a whole-world zoom level even at a constant tile COUNT) stays allocation-free,
        // preserving the pre-existing zero-alloc Tick contract
        // (MapViewLiveLoopTests.MapView_SteadyStateTick_DoesNotAllocateGCMemory — specifically its
        // heading/tilt sub-case, which exercises exactly this ReleaseTile -> TransferBuiltMeshesToCache ->
        // Put path) that S82 must not regress. VERIFIED empirically (not merely asserted): with this pool
        // removed, that test fails deterministically (a `new LinkedListNode<Entry>` allocation on the
        // cover-churn Put) — 3/3 runs, including full isolation (a single Editor launch running only this
        // one test method). This is the "prove it" evidence the S82 review comment asked for; see the
        // developer report for the removal-vs-pass A/B.
        //
        // Pre-filled once at construction to <c>min(countCap, MaxPrewarmedNodes)</c> — the cache can never
        // hold more than countCap entries simultaneously, so that many nodes cover every Put without a
        // fallback allocation in the overwhelmingly common case; a pathological burst beyond the prewarmed
        // count still falls back to `new` rather than fail (correctness over the (already generous) budget).
        private const int MaxPrewarmedNodes = 1024;
        private readonly Stack<LinkedListNode<Entry>> _nodePool;

        /// <summary>Test/telemetry observability — bumped by the caller (TileManager's Tick probe) at the
        /// whole-tile granularity, not per layer here. Plain mutable fields (like <c>ReleasedMidFlightCount</c>'s
        /// sibling counters), never read from the live tile loop's own decisions.</summary>
        internal int Hits;
        internal int Misses;

        /// <summary>S82: cumulative count of LRU-evicted entries (bumped internally, once per entry evicted
        /// out of <see cref="Put"/>'s while-loop — never by the caller). Telemetry only, never read from the
        /// live tile loop's own decisions.</summary>
        internal int Evictions;

        /// <summary>Current estimated bytes held (sum of every live entry's <see cref="EstimateBytes"/>).</summary>
        internal long BytesHeld { get { lock (_lock) return _bytesHeld; } }

        /// <summary>Current entry count (produced meshes AND empty-layer markers).</summary>
        internal int Count { get { lock (_lock) return _map.Count; } }

        /// <summary>S82: the effective byte budget this cache evicts against (the constructor clamps a
        /// non-positive <c>byteBudget</c> argument up to <see cref="long.MaxValue"/> — "unbounded" — so this
        /// is the LIVE applied value, not necessarily the raw configured one).</summary>
        internal long ByteBudget => _byteBudget;

        /// <summary>S82: the effective entry-count cap this cache evicts against (see <see cref="ByteBudget"/>
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
        /// tessellated mesh has a populated stream 0) times vertex count, plus a flat 4 bytes/index (UInt32
        /// index format — see <see cref="Style.MeshDataTessellation.Upload"/>). <see langword="null"/> (the
        /// empty-layer marker) is 0 bytes.</summary>
        private static long EstimateBytes(Mesh m)
        {
            if (m == null) return 0;
            long stride = m.GetVertexBufferStride(0);
            return (long)m.vertexCount * stride + (long)m.GetIndexCount(0) * 4;
        }

        /// <summary>Same main-thread guard <c>TileManager.DestroyTrackedMeshes</c> uses.</summary>
        private static void DestroyMesh(Mesh m)
        {
            if (m == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(m);
            else                       UnityEngine.Object.DestroyImmediate(m, allowDestroyingAssets: true);
        }

        /// <summary>
        /// Destroys every held <see cref="Mesh"/> and empties the cache — reusable afterwards (unlike a true
        /// <see cref="IDisposable"/> teardown). Called by <c>TileManager.SetSources</c> on EVERY call
        /// (first style AND every restyle): a restyle rebuilds <c>RenderLayerSet</c>'s layer indexing, so a
        /// stale entry's <c>layerId</c> may no longer denote the same semantic layer — and pre-S83,
        /// <see cref="StyleToken"/> is a constant default shared by every style, so without this clear a
        /// restyle could get a false HIT serving a PRIOR style's baked geometry under a coincidentally-
        /// matching <c>(tileId, layerId)</c>. Also called by <see cref="Dispose"/> (teardown).
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
