// Unity-side: CaptureSnapshot/OrderedBlocks read SymbolTileBlock's native columns directly, so this file is
// Unity-EditMode-only, not in the engine-free core-tests loop. Entry.Block wraps plain IDisposable, not the
// concrete type — see Entry's comment for why T can't narrow.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>Per-<c>(source, tile)</c> symbol bookkeeping, mirroring the tile MESH lifecycle
    /// (<c>TileManager</c> Model B): a tile is <b>active</b> (in cover, renders) or <b>cached</b> (out of cover,
    /// held warm in the <c>PreparedTileCache</c>, not rendered), moving between the two on release / cache-hit.
    ///
    /// <para>Release-to-cache KEEPS symbols (a cache HIT has no re-fetch to rebuild them) in a bounded FIFO sized
    /// to the mesh cache. A build awaits glyph fetches, so a tile can be released mid-build:
    /// <see cref="BeginBuild"/>/<see cref="CompleteBuild"/> use a generation token so a released-mid-build tile
    /// still commits (into the cached entry) and a superseded build is discarded.</para>
    ///
    /// <para><b>Reference model.</b> A committed block is owned by a <see cref="SharedDisposable{T}"/>; the last
    /// reference out disposes it, exactly once.
    /// <list type="bullet">
    /// <item>The store's entry holds exactly one reference, created with the wrapper at commit
    /// (<see cref="CompleteBuild"/>) and released when the entry stops naming that block — whether the entry is
    /// overwritten or removed from the map. Moves that keep the block (<see cref="BeginBuild"/>'s stale-survive,
    /// <see cref="Restore"/>'s cache-hit) transfer the entry, not the reference, and release nothing.</item>
    /// <item>A snapshot holds one reference per slice, taken in <see cref="SymbolSnapshot.Add"/> and dropped in
    /// <see cref="SymbolSnapshot.Clear"/> — the only two sites permitted to touch <see cref="TileSlice.Pin"/>.
    /// Every path that ends a snapshot's service (<see cref="ReleasePins"/>, or the next
    /// <see cref="CaptureSnapshot"/>, which clears first) goes through <see cref="SymbolSnapshot.Clear"/>, so
    /// acquire/release balance is structural, not a discipline.</item>
    /// </list></para></summary>
    public sealed class SymbolTileStore
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

        // One tile's baked block (a SymbolTileBlock) + the owning build's generation (stale-guard). Mutable so an
        // in-flight build can write Block after a release moved the entry between maps. The wrapper's T is
        // IDisposable, NOT SymbolTileBlock: CompleteBuild is public and SymbolTileBlock is internal (a narrower T
        // would be CS0051), and test doubles (FakeBlock/FakeDisposableBlock) commit plain IDisposables that are
        // not SymbolTileBlock. Kept on stale-survive / cache-hit moves (the entry's reference transfers with it,
        // released nothing); released at every real drop site (commit-overwrite, FIFO-evict, true-release, Clear)
        // — the last reference out disposes the block, see the class doc's reference model.
        private sealed class Entry
        {
            public int Generation;
            public SharedDisposable<IDisposable> Block;
        }

        private readonly Dictionary<Key, Entry> _active = new Dictionary<Key, Entry>();
        // Cached (out-of-cover) tiles as a FIFO: LinkedList = release order (head = oldest), dict = O(1) index.
        // Insertion order already mirrors the mesh cache's eviction order, so no recency bump is needed.
        private readonly Dictionary<Key, LinkedListNode<KeyedEntry>> _cachedIndex =
            new Dictionary<Key, LinkedListNode<KeyedEntry>>();
        private readonly LinkedList<KeyedEntry> _cachedOrder = new LinkedList<KeyedEntry>();
        private readonly struct KeyedEntry
        {
            public readonly Key Key;
            public readonly Entry Entry;
            public KeyedEntry(Key key, Entry entry) { Key = key; Entry = entry; }
        }

        // Max cached tiles kept warm — always finite: a caller's <= 0 ("unbounded") is clamped, never left
        // uncapped (an unbounded warm FIFO would grow with every tile visited; always-bound-loops).
        private const int HardCacheCap = 4096;
        private readonly int _cacheCap;
        private int _genCounter;

        // Monotonic "collect-relevant state changed" counter. CollectInto is a pure function of the loaded set, so
        // a consumer caching the generation it last collected at can reuse its buffers while this is unchanged.
        // Bumped (MarkCollectDirty) only on an ACTUAL collect-relevant change, never unconditionally — see
        // ReconcileActiveSet, which runs every frame and must NOT dirty a stable cover.
        private int _collectGeneration;

        // Store-owned string→int interning for the dedup key. Mutated in CompleteBuild, Reset in Clear (SetStyle).
        // A restyle renumbers — harmless, ids feed only equality/hashing.
        private readonly SymbolStringTable _stringTable = new SymbolStringTable();

        // The store owns the intern table's lifetime (Reset in Clear); the build-time bake interns into this same
        // table. Exposed to the subsystem's tail bake, not public. See SymbolStringTable's threading note.
        internal SymbolStringTable StringTable => _stringTable;

        // Retain-as-departing: a tile released within a grace window is still collected (as departing) so it fades
        // out instead of popping. key -> expiry (s). Invariant: departing ⊆ cached (every path out of cached
        // clears the stamp via RemoveCached). Empty ⇒ CollectInto emits ACTIVE only (off when grace ≤ 0).
        private readonly Dictionary<Key, double> _departing = new Dictionary<Key, double>();

        public SymbolTileStore(int cacheCap)
            => _cacheCap = cacheCap > 0 && cacheCap < HardCacheCap ? cacheCap : HardCacheCap;

        /// <summary>Active (in-cover) tile count — test/telemetry.</summary>
        public int ActiveTileCount => _active.Count;

        /// <summary>Cached (out-of-cover, kept-warm) tile count — test/telemetry.</summary>
        public int CachedTileCount => _cachedIndex.Count;

        /// <summary>Departing (left cover, still inside the fade-out grace window) tile count — test/telemetry.</summary>
        public int DepartingTileCount => _departing.Count;

        /// <summary>The collect-invalidation generation — the subsystem's memo recomputes only when it moves.
        /// Bumped internally on every collect-relevant mutation.</summary>
        public int CollectGeneration => _collectGeneration;

        // Bump the collect generation (one bump covers all state changes in a call). CollectInto never calls this,
        // so the bumps never self-trigger.
        private void MarkCollectDirty() => _collectGeneration++;

        /// <summary>Reserve an active slot for a (re)build of <paramref name="key"/> and return the generation
        /// token <see cref="CompleteBuild"/> must present.
        ///
        /// <para>An existing tile's symbols are kept (only the generation bumps), so a rebuilding/reappearing tile
        /// keeps drawing its last symbols until the new build commits instead of flashing empty.</para></summary>
        public int BeginBuild(Key key)
        {
            Entry entry = FindCurrent(key); // active or cached (a re-fetch of an out-of-cover tile)
            RemoveCached(key);              // pull it fully onto the active side; keep its symbols
            int gen = ++_genCounter;
            if (entry == null) entry = new Entry();
            entry.Generation = gen;
            _active[key] = entry;
            // Always bump: a re-fetch pulls a cached/departing tile onto the active side (changes collect). A new
            // null-Block tile doesn't, but distinguishing is fragile and an extra recompute per build is harmless.
            MarkCollectDirty();
            return gen;
        }

        /// <summary>Commit the baked <paramref name="block"/> for <paramref name="key"/> iff the build
        /// <paramref name="gen"/> still owns the entry (active OR cached — a release may have moved it). Returns
        /// false when superseded/dropped.
        ///
        /// <para>Single-owner swap: on commit the entry's old block is disposed before the new one lands; on a
        /// superseded/dropped commit the caller's <paramref name="block"/> is disposed here.</para></summary>
        public bool CompleteBuild(Key key, int gen, IDisposable block = null)
        {
            Entry e = FindCurrent(key);
            if (e == null || e.Generation != gen)
            {
                block?.Dispose(); // superseded or dropped mid-build — this build's bake never lands, never leaks
                return false;
            }
            e.Block?.Release(); // the entry's own reference; the last one out disposes the superseded block
            e.Block = block == null ? null : new SharedDisposable<IDisposable>(block);
            MarkCollectDirty(); // commit path only; the superseded early-return above stays unbumped
            return true;
        }

        /// <summary>A tile left cover. <paramref name="transferredToCache"/> true ⇒ meshes went to the prepared
        /// cache, so keep its symbols warm (evicting oldest if over cap); false ⇒ true eviction, drop them.</summary>
        public void Release(Key key, bool transferredToCache)
        {
            if (_active.TryGetValue(key, out Entry e))
            {
                _active.Remove(key);
                if (transferredToCache) EnqueueCached(key, e);
                else e.Block?.Release(); // true eviction (cache disabled / not built) — the last reference out disposes it
                // One bump for the whole branch; inside the `if` so a no-op release doesn't dirty.
                MarkCollectDirty();
            }
            else if (!transferredToCache)
            {
                // Not active but a stale cached copy may exist (cache-disabled) → drop it (true eviction).
                if (RemoveCached(key, out Entry dropped))
                {
                    dropped.Block?.Release();
                    MarkCollectDirty(); // a departing cached copy dropped → AppendDeparting changes.
                }
            }
        }

        /// <summary>A tile re-entered cover via a prepared-cache HIT (no fetch): move its kept-warm symbols back
        /// to the active set so they render again. A no-op if nothing was cached for it.</summary>
        public void Restore(Key key)
        {
            // Bump only when the move happens; a no-op restore leaves collect unchanged.
            if (RemoveCached(key, out Entry e)) { _active[key] = e; MarkCollectDirty(); }
        }

        // Per-collect ordered block list — one entry per scanned tile, in the order a winner's BlockId indexes
        // into (active scan first, then departing). A COPY TARGET: the plan-aware CollectInto copies
        // SymbolReconciler's OrderedBlocks here so the existing tests/oracle still work.
        private readonly List<SymbolTileBlock> _orderedBlocks = new List<SymbolTileBlock>();

        /// <summary>The blocks the last plan-aware <see cref="CollectInto(List{int}, List{int}, List{byte},
        /// double, out int)"/> assigned <c>blockId</c>s against. Borrowed refs; valid until the next collect.</summary>
        // Internal (not public): SymbolTileBlock is internal, so a public property over it is CS0053.
        internal IReadOnlyList<SymbolTileBlock> OrderedBlocks => _orderedBlocks;

        /// <summary>Aggregate every ACTIVE tile's winners into the out-params for this frame's placement pass
        /// (cached tiles excluded — they must not render). POINT symbols are deduped across tiles by their
        /// <see cref="CrossTileSymbolKey"/> (finest zoom wins, ties by lowest tile key) on the fixed
        /// <see cref="CrossTileSymbolKey.CanonicalGridMeters"/> grid, so the winner set is zoom-independent; line
        /// symbols pass through. <paramref name="quantizeMeters"/> is retained for callers but never branched on.
        ///
        /// <para>Winner identity is <c>(blockId, localIndex)</c>: <paramref name="outBlockId"/> indexes
        /// <see cref="OrderedBlocks"/>, <paramref name="outLocalIndex"/> is the raw block position;
        /// <paramref name="outIsDeparting"/> is 0 for the first <paramref name="activeCount"/> records, 1 after.
        /// The scan is delegated to <see cref="SymbolReconciler"/> (the production off-main impl), so this overload
        /// is a synchronous byte-identical shim for the tests/oracle.</para></summary>
        public void CollectInto(List<int> outBlockId, List<int> outLocalIndex, List<byte> outIsDeparting,
            double quantizeMeters, out int activeCount)
        {
            // Capture → run the shared off-main reconciler → copy out (the shim and the subsystem's per-frame path
            // both run SymbolReconciler.Run, so the store/oracle suite is a byte-identical oracle). This shim has
            // no async borrow, so it releases the pins right after Run.
            CaptureSnapshot(_oracleSnapshot);
            // try/finally so the pins are released even if Run ever faults.
            try { _oracleReconciler.Run(_oracleSnapshot, _oracleResult); }
            finally { ReleasePins(_oracleSnapshot); }

            outBlockId.Clear();     outBlockId.AddRange(_oracleResult.BlockId);
            outLocalIndex.Clear();  outLocalIndex.AddRange(_oracleResult.LocalIndex);
            outIsDeparting.Clear(); outIsDeparting.AddRange(_oracleResult.IsDeparting);
            _orderedBlocks.Clear(); _orderedBlocks.AddRange(_oracleResult.OrderedBlocks);
            activeCount = _oracleResult.ActiveCount;
        }

        // ═══ Main-thread snapshot capture + native-block pin guard ═══

        // The plan-aware-CollectInto shim's own reused reconciler/snapshot/result (separate from the subsystem's).
        private readonly SymbolReconciler _oracleReconciler = new SymbolReconciler();
        private readonly SymbolSnapshot _oracleSnapshot = new SymbolSnapshot();
        private readonly SymbolReconcileResult _oracleResult = new SymbolReconcileResult();

        /// <summary>Capture the current collected tile set into <paramref name="into"/> (main thread) for an
        /// off-main <see cref="SymbolReconciler.Run"/> — identical scan order to the plan-aware collect. Each
        /// block is referenced; the caller MUST <see cref="ReleasePins"/> the snapshot when it leaves service.</summary>
        internal void CaptureSnapshot(SymbolSnapshot into)
        {
            into.Clear();
            foreach (KeyValuePair<Key, Entry> kv in _active)
            {
                Entry e = kv.Value;
                // A null Block means nothing to render; in production a committed slot always has one.
                if (e.Block == null) continue;
                into.Add(e.Block, isDeparting: false);
            }
            foreach (KeyValuePair<Key, double> dep in _departing)
            {
                if (!_cachedIndex.TryGetValue(dep.Key, out LinkedListNode<KeyedEntry> node)) continue;
                Entry e = node.Value.Entry;
                if (e.Block == null) continue;
                into.Add(e.Block, isDeparting: true);
            }
        }

        /// <summary>Release <paramref name="snapshot"/>'s block references — the snapshot leaves service and is
        /// left empty. A block still referenced by another live snapshot, or by its store entry, survives.
        /// Idempotent: releasing an already-released snapshot releases nothing. A named forward to
        /// <see cref="SymbolSnapshot.Clear"/>, which does the actual release — kept on the store (12 call sites,
        /// and the name every existing test/caller already uses) rather than moved.</summary>
        internal void ReleasePins(SymbolSnapshot snapshot) => snapshot.Clear();

        /// <summary>Drop everything (a restyle purges the mesh cache too) — releases every active AND cached
        /// entry's own reference; a block still referenced by a live snapshot survives until that snapshot's
        /// own release (see the class doc's reference model).</summary>
        public void Clear()
        {
            foreach (KeyValuePair<Key, Entry> kv in _active) kv.Value.Block?.Release();
            foreach (KeyedEntry keyed in _cachedOrder) keyed.Entry.Block?.Release();
            _active.Clear();
            _cachedIndex.Clear();
            _cachedOrder.Clear();
            _departing.Clear();
            _stringTable.Reset(); // SetStyle-boundary reset of the interning table (ids restart at 1)
            _orderedBlocks.Clear(); // drop borrowed refs to the now-disposed blocks
            MarkCollectDirty(); // the whole set → empty (restyle); covers the _stringTable.Reset renumber too
        }

        // Reused reconcile scratch (main-thread) — keeps ReconcileActiveSet alloc-free in steady state (the set +
        // two move-lists never grow once cover stabilises). Per-frame no-GC contract.
        private readonly HashSet<Key> _loadedKeys = new HashSet<Key>();
        private readonly List<Key> _reconcileRelease = new List<Key>();
        private readonly List<Key> _reconcileRestore = new List<Key>();

        /// <summary>PULL model: reconcile the active set against the pipeline's currently <paramref name="loaded"/>
        /// tiles. An active tile no longer loaded is released (kept warm iff <paramref name="keepWarmOnRelease"/>);
        /// a loaded tile currently on the cached side is restored (a cache re-entry has no re-fetch to rebuild it);
        /// a loaded tile with no entry is left for its bytes-ready build. Idempotent.</summary>
        public void ReconcileActiveSet(IReadOnlyList<Key> loaded, bool keepWarmOnRelease,
            double nowSeconds = 0.0, double departingGraceSeconds = 0.0)
        {
            // Deliberately does NOT MarkCollectDirty() here: it runs EVERY frame and on a stable cover moves
            // nothing, so a self-bump would dirty every frame and the memo would never fire. Real effects inherit
            // their bumps from Release/Restore/PurgeExpiredDeparting. (THE critical correctness point.)
            _loadedKeys.Clear();
            for (int i = 0; i < loaded.Count; i++) _loadedKeys.Add(loaded[i]);

            // Release actives that left the loaded set (collect first — cannot mutate _active while iterating).
            _reconcileRelease.Clear();
            foreach (KeyValuePair<Key, Entry> kv in _active)
                if (!_loadedKeys.Contains(kv.Key)) _reconcileRelease.Add(kv.Key);
            for (int i = 0; i < _reconcileRelease.Count; i++)
            {
                Key key = _reconcileRelease[i];
                Release(key, keepWarmOnRelease);
                // Stamp departing AFTER Release (which cleared any prior stamp) and only if it landed on the cached
                // side with a grace window set.
                if (departingGraceSeconds > 0.0 && _cachedIndex.ContainsKey(key))
                    _departing[key] = nowSeconds + departingGraceSeconds;
            }

            // Restore cached tiles that re-entered loaded (RemoveCached clears the departing stamp → fades back in).
            _reconcileRestore.Clear();
            for (int i = 0; i < loaded.Count; i++)
                if (_cachedIndex.ContainsKey(loaded[i])) _reconcileRestore.Add(loaded[i]);
            for (int i = 0; i < _reconcileRestore.Count; i++)
                Restore(_reconcileRestore[i]);

            PurgeExpiredDeparting(nowSeconds);
        }

        // Drop departing stamps whose grace elapsed or whose cached entry was evicted. A purged tile stops being
        // collected as departing but stays warm on the cached side. Grace exceeds the fade duration, so a purge
        // only drops an already-invisible symbol — never mid-fade.
        private readonly List<Key> _departingPurgeKeys = new List<Key>();
        private void PurgeExpiredDeparting(double nowSeconds)
        {
            if (_departing.Count == 0) return;
            _departingPurgeKeys.Clear();
            foreach (KeyValuePair<Key, double> kv in _departing)
                if (nowSeconds >= kv.Value || !_cachedIndex.ContainsKey(kv.Key)) _departingPurgeKeys.Add(kv.Key);
            for (int i = 0; i < _departingPurgeKeys.Count; i++) _departing.Remove(_departingPurgeKeys[i]);
            // Bump iff ≥1 removed — those tiles stop being collected as departing. Usually removes nothing.
            if (_departingPurgeKeys.Count > 0) MarkCollectDirty();
        }

        /// <summary>Whether this tile's symbols are warm — a block is COMMITTED for <paramref name="key"/>,
        /// active or kept-warm. A slot <see cref="BeginBuild"/> reserved but <see cref="CompleteBuild"/> has
        /// not filled reads false, so an admission-time caller fails safe toward a re-fetch.</summary>
        internal bool HasCommittedBlock(Key key) => FindCurrent(key)?.Block != null;

        /// <summary>Test-only: the raw single-tile block committed for <paramref name="key"/> (what
        /// <see cref="CompleteBuild"/> stored), or null. Internal Debug-prefixed seam, no production caller.</summary>
        internal SymbolTileBlock DebugBlockFor(Key key) => (SymbolTileBlock)FindCurrent(key)?.Block?.Value;

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
                _departing.Remove(oldest.Value.Key); // keep departing ⊆ cached
                oldest.Value.Entry.Block?.Release(); // over-cap eviction is a genuine drop — the entry's own reference
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
                _departing.Remove(key); // departing ⊆ cached: any path out of cached (restore / re-fetch) clears it
                return true;
            }
            entry = null;
            return false;
        }
    }
}
