// Unity-side (co-located with the symbol subsystem that owns it): the reader cutover (symbols-async-reconcile
// stage 4.2) makes CaptureSnapshot/OrderedBlocks reference the concrete Unity.Collections-backed
// SymbolTileBlock directly, so this file left the core-tests.csproj fast loop (still compiled + run by
// the Unity EditMode runner). Entry.Block stays System.IDisposable — it is pure dispose-once lifetime
// bookkeeping (commit-overwrite / FIFO-evict / true-release / Clear never need the concrete type), so keeping
// it IDisposable costs nothing and keeps the pin/dispose machinery unchanged.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// The per-<c>(source, tile)</c> symbol bookkeeping backing the map's symbol-symbol pipeline. It mirrors the
    /// tile MESH lifecycle
    /// (<c>TileManager</c> Model B): a tile is either <b>active</b> (in cover — its symbols render) or
    /// <b>cached</b> (out of cover but its meshes are held in the <c>PreparedTileCache</c> — symbols kept warm
    /// but NOT rendered), and moves between the two on release / cache-hit exactly as its meshes do.
    ///
    /// <para><b>Why this exists (the bug it fixes).</b> Symbols are built from a tile's freshly-FETCHED bytes.
    /// When a tile leaves cover its meshes transfer to the prepared cache; when it re-enters via a cache HIT
    /// there is NO fetch — so if symbols were simply dropped on release they would never be rebuilt, and the
    /// tile would show its geometry with no symbols. So release-to-cache KEEPS the symbols here (bounded FIFO,
    /// oldest-released evicted first, sized to the mesh cache's count cap so symbols always outlive their
    /// meshes) and a cache hit restores them.</para>
    ///
    /// <para><b>Async build race.</b> A symbol build awaits glyph fetches, so a tile can be released — moved to
    /// the cached side — WHILE its build is still in flight. <see cref="BeginBuild"/> hands back a generation
    /// token and <see cref="CompleteBuild"/> commits only if that token still owns the entry in EITHER map, so
    /// a released-mid-build tile still gets its symbols (written into the cached entry) rather than silently
    /// losing them, and a superseded build is discarded.</para>
    /// </summary>
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

        // One tile's baked block + the generation of the build that owns the slot (for the stale-guard). Mutable
        // so an in-flight build can write Block into the SAME object after a release has moved it between maps.
        //
        // Symbol-symbol perf Phase 1 / Stage 1 (design §4, §5 B): Block is the tile's baked native SoA slice (a
        // MapRenderer.Unity.Text.Placement.SymbolTileBlock — a NativeArray holder). Held here as plain
        // System.IDisposable — this field is pure dispose-once lifetime bookkeeping (commit-overwrite / FIFO-evict
        // / true-release / Clear never need the concrete type), so it stays IDisposable even though the store
        // itself left the engine-free set at the reader cutover (4.2 — CaptureSnapshot casts to the concrete
        // type once, where the reconciler actually needs the columns). Kept on a stale-survives (BeginBuild pull)
        // / cache-hit move (Restore), disposed at every genuine drop site (commit-overwrite, FIFO-evict,
        // true-release, Clear).
        // Reader cutover (4.2)/id-shed (4.3): the interned text/icon ids now live ONLY on the block (baked by
        // SymbolTileBlockBaker into its TextIds/IconImageIds columns from the shared _stringTable table); the
        // former parallel Entry.TextIds/IconImageIds int[] arrays were redundant once the reconciler read the
        // block, so they are gone. Resident-graph shed (4.4b): SymbolPlacementSystem itself is gone too — Block (baked at commit)
        // is the only per-tile symbol state this entry carries now; DebugBlockFor is its test seam.
        private sealed class Entry
        {
            public int Generation;
            public IDisposable Block;
        }

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

        // Max cached (out-of-cover) tiles kept warm. ALWAYS finite — a caller-supplied <= 0 ("unbounded" mesh
        // cache) is clamped to HardCacheCap, never left uncapped: an unbounded warm-symbol FIFO would grow with
        // every tile ever visited (the "always bound loops/collections, even at a laughably large value"
        // principle — see the always-bound-loops lesson).
        private const int HardCacheCap = 4096;
        private readonly int _cacheCap;
        private int _genCounter;

        // Stage 4a (symbols-async-reconcile): a monotonic "collect-relevant state changed" counter. CollectInto is a
        // PURE function of the loaded tile set (Stage 3 — fixed canonical grid, zoom/camera-independent), so a
        // consumer (SymbolSubsystem.CurrentBatch) that caches the generation it last collected at can REUSE its
        // already-filled buffers on any frame this counter is unchanged — a byte-identical recompute is skipped. Bumped
        // (MarkCollectDirty) at each mutating public entry point that changes active membership, cached/departing
        // membership feeding AppendDeparting, or a tile's symbol content — conditional on an ACTUAL change, never
        // unconditionally at entry (see ReconcileActiveSet, which runs every frame and must NOT dirty a stable cover).
        private int _collectGeneration;

        // Stage 2 (symbols-async-reconcile): store-owned string→int interning for the dedup key. Mutated ONLY in
        // CompleteBuild (main thread), Reset in Clear (the SetStyle boundary). Ids are stable for a style's life;
        // a restyle renumbers (harmless — ids feed only equality/hashing, never a snapshot).
        private readonly SymbolStringTable _stringTable = new SymbolStringTable();

        // Native-representation migration (additive): the store owns the intern table's LIFETIME (Reset in Clear),
        // but the build-time bake also interns into it — SymbolTileBlockBaker.Bake writes each symbol's id into
        // the block's TextIds/IconImageIds columns from THIS table, so the block's ids match the entry's arrays
        // (both main thread, same table, idempotent interning). Exposed to the subsystem's RunTailAsync bake call;
        // not a public API. See SymbolStringTable's threading note.
        internal SymbolStringTable StringTable => _stringTable;

        // Retain-as-departing: a tile released to the cached side within a grace window is still COLLECTED (as
        // departing) so its symbols fade OUT instead of popping when the tile leaves cover. key -> wall-clock expiry
        // (seconds). Invariant: departing ⊆ cached — every "leaves cached" path funnels through RemoveCached, which
        // clears the stamp, so a re-fetched / restored tile drops it automatically. Stamped by ReconcileActiveSet on
        // release, purged at expiry. Empty ⇒ CollectInto emits ACTIVE only (the feature is off when grace ≤ 0).
        private readonly Dictionary<Key, double> _departing = new Dictionary<Key, double>();

        public SymbolTileStore(int cacheCap)
            => _cacheCap = cacheCap > 0 && cacheCap < HardCacheCap ? cacheCap : HardCacheCap;

        /// <summary>Active (in-cover) tile count — test/telemetry.</summary>
        public int ActiveTileCount => _active.Count;

        /// <summary>Cached (out-of-cover, kept-warm) tile count — test/telemetry.</summary>
        public int CachedTileCount => _cachedIndex.Count;

        /// <summary>Departing (left cover, still inside the fade-out grace window) tile count — test/telemetry.</summary>
        public int DepartingTileCount => _departing.Count;

        /// <summary>Stage 4a: the monotonic collect-invalidation generation — read by the subsystem's memo (recompute
        /// CollectInto only when this moved) and by tests. Read-only; bumped internally on every collect-relevant
        /// mutation.</summary>
        public int CollectGeneration => _collectGeneration;

        // Stage 4a: bump the collect generation — a coarse "something collect-relevant changed, recompute" signal (one
        // bump per mutating call covers all state changes within it). CollectInto never calls this (it only reads
        // _active/_departing/_cachedIndex and writes its output/_orderedBlocks), so the bumps never self-trigger.
        private void MarkCollectDirty() => _collectGeneration++;

        /// <summary>
        /// Reserve an active slot for a (re)build of <paramref name="key"/> and return the generation token the
        /// matching <see cref="CompleteBuild"/> must present.
        ///
        /// <para><b>Stale symbols survive the rebuild.</b> If the tile already has an entry (active, or cached and
        /// being pulled active by a re-fetch), its EXISTING symbols are kept on the active side and only the
        /// generation is bumped — so a rebuilding / reappearing tile keeps drawing its last symbols until the new
        /// build commits, rather than flashing empty for the fetch+shape window. A tile first seen this session
        /// starts empty (no symbols yet). The bumped generation still discards any superseded in-flight build.</para>
        /// </summary>
        public int BeginBuild(Key key)
        {
            Entry entry = FindCurrent(key); // active or cached (a re-fetch of an out-of-cover tile)
            RemoveCached(key);              // pull it fully onto the active side; keep its symbols
            int gen = ++_genCounter;
            if (entry == null) entry = new Entry();
            entry.Generation = gen;
            _active[key] = entry;
            // Stage 4a: always bump. A re-fetch pulls a cached/departing tile (with its block) onto the active side —
            // that changes collect. A brand-new (null-Block) tile does NOT, but distinguishing is fragile and an
            // extra byte-identical recompute on a per-tile-build event (never per-frame) is harmless (§1 #1).
            MarkCollectDirty();
            return gen;
        }

        /// <summary>
        /// Commit the baked native <paramref name="block"/> (Phase 1 Stage 1, design §4/§5 B) for
        /// <paramref name="key"/> iff the build identified by <paramref name="gen"/> still owns the tile's entry
        /// (in the active OR cached map — a release may have moved it). Returns true when committed, false when
        /// the build was superseded or the tile was dropped.
        ///
        /// <para>Single-owner swap: on a genuine commit the entry's OLD <see cref="Entry.Block"/> is disposed
        /// before the new one is assigned (never two live blocks for one entry). On a superseded/dropped commit
        /// the CALLER's <paramref name="block"/> is disposed here — the caller never re-owns it, mirroring a
        /// baked block having nowhere to land.</para></summary>
        public bool CompleteBuild(Key key, int gen, IDisposable block = null)
        {
            Entry e = FindCurrent(key);
            if (e == null || e.Generation != gen)
            {
                block?.Dispose(); // superseded or dropped mid-build — this build's bake never lands, never leaks
                return false;
            }
            DisposeOrDefer(e.Block); // single-owner swap: the entry's old block is replaced (deferred iff a snapshot pins it)
            // Id-shed (4.3): no interning here anymore. The block's TextIds/IconImageIds columns (baked from the
            // SAME _stringTable table, in Bake, immediately before this commit) are the sole id source the
            // reconciler reads — re-interning into a parallel Entry array was pure redundancy. _stringTable's
            // lifetime is still store-owned (Reset in Clear); the baker feeds it.
            e.Block = block;
            MarkCollectDirty(); // Stage 4a: commit path only — symbol content landed/replaced (§1 #2). The superseded
            return true;        // early-return above stays UNBUMPED (no store state changed — §1 #2b).
        }

        /// <summary>
        /// A tile left cover. <paramref name="transferredToCache"/> true ⇒ its meshes went to the prepared
        /// cache, so keep its symbols warm on the cached side (evicting the oldest-released if over the cap);
        /// false ⇒ a true eviction (cache disabled / not built), so drop its symbols outright.
        /// </summary>
        public void Release(Key key, bool transferredToCache)
        {
            if (_active.TryGetValue(key, out Entry e))
            {
                _active.Remove(key);
                if (transferredToCache) EnqueueCached(key, e);
                else DisposeOrDefer(e.Block); // true eviction (cache disabled / not built) — drop its block (deferred iff pinned)
                // Stage 4a: one bump for the whole branch (both transferredToCache values — §1 #3/#3b); EnqueueCached's
                // over-cap FIFO-evict sub-change rides it. Inside the `if` so a no-op release (§1 #3d) doesn't dirty.
                MarkCollectDirty();
            }
            else if (!transferredToCache)
            {
                // Not active but a stale cached copy may exist (cache-disabled path) → drop it (true eviction —
                // dispose its block; a stale-survives/cache-hit move never reaches this branch).
                if (RemoveCached(key, out Entry dropped))
                {
                    DisposeOrDefer(dropped.Block);
                    MarkCollectDirty(); // Stage 4a: a departing cached copy dropped → AppendDeparting changes (§1 #3c).
                }
            }
        }

        /// <summary>A tile re-entered cover via a prepared-cache HIT (no fetch): move its kept-warm symbols back
        /// to the active set so they render again. A no-op if nothing was cached for it.</summary>
        public void Restore(Key key)
        {
            // Stage 4a: bump only when the move actually happens (cached → active clears the departing stamp — §1 #4);
            // a no-op restore (nothing cached — §1 #4b) leaves collect unchanged, so it must not dirty.
            if (RemoveCached(key, out Entry e)) { _active[key] = e; MarkCollectDirty(); }
        }

        // Stage 4b: DedupKey + DedupEntry moved to SymbolReconciler.cs (internal top-level, same namespace)
        // so the off-main reconciler and these legacy plain overloads share ONE definition — the relocation is
        // textual only (GetHashCode/Equals unchanged ⇒ the dedup partition, and every byte-identity oracle, holds).
        // §10 D8/D9: the store's OWN `_dedup` field (A-3's per-collect index) was deleted here — its last user
        // (the plain > 0 CollectInto overload) now routes through SymbolReconciler.Run's reused index
        // instead of hand-keeping a third copy of the dedup rules. See CollectInto below.

        // Stage-2: the per-collect ORDERED block list — one entry per scanned tile (with a non-null Block), in
        // the exact deterministic order the plan's BlockId indexes into: the _active scan FIRST, then the
        // departing scan (so a departing/cached tile's block, which winners can still reference, is included).
        // Reader cutover (4.2): concrete SymbolTileBlock — this store left the engine-free set (see this
        // file's header), and every downstream reader (SymbolGatherPlan.Build, SymbolSubsystem's coverage
        // classify) wants the block's own columns (TileKey etc), not a re-cast IDisposable. Stage 4b: a COPY
        // TARGET — the plan-aware CollectInto delegates the scan to SymbolReconciler and copies the
        // result's OrderedBlocks here, so OrderedBlocks still serves the existing tests/oracle unchanged
        // (production reads the subsystem's front-buffer result directly).
        private readonly List<SymbolTileBlock> _orderedBlocks = new List<SymbolTileBlock>();

        /// <summary>Stage-2: the ordered blocks the last plan-aware <see cref="CollectInto(List{int}, List{int},
        /// List{byte}, double, out int)"/> assigned <c>blockId</c>s against — <c>OrderedBlocks[blockId]</c>
        /// is the baked native block a winner's <c>(blockId, localIndex)</c> references. Borrowed refs (the store
        /// owns their disposal); valid until the next plan-aware collect.</summary>
        // Internal (was public): the element type SymbolTileBlock is internal, and every consumer is
        // in-assembly (SymbolSubsystem) or a test via InternalsVisibleTo — a public property over an
        // internal type is CS0053.
        internal IReadOnlyList<SymbolTileBlock> OrderedBlocks => _orderedBlocks;

        /// <summary>Aggregate every ACTIVE tile's winners (as the plan arrays below) for this frame's placement
        /// pass. Cached (out-of-cover) tiles are deliberately excluded — they must not render.
        ///
        /// <para>A-3: POINT symbols are DEDUPED across tiles by their <see cref="CrossTileSymbolKey"/> — the same
        /// symbol present in a parent + child tile during a zoom transition collapses to ONE (the finest tile
        /// zoom wins; ties broken by lowest tile key), so it is not double-drawn and its identity is stable
        /// across the swap. Line symbols are passed through undeduped (per-anchor line identity is a
        /// follow-up).</para>
        ///
        /// <para><b>Stage 3:</b> the dedup grid is the fixed <see cref="CrossTileSymbolKey.CanonicalGridMeters"/>,
        /// decoupled from display zoom, so neither fractional nor integer zoom can perturb the winner set (the
        /// winners are a pure function of the tile set — the Stage-4 memoization prerequisite) and the departing
        /// claim-skip holds across a zoom step (old- and new-band copies of a co-located feature key on the SAME
        /// grid → same cell). <paramref name="quantizeMeters"/> is retained on the signature for callers to state
        /// a grid explicitly (<see cref="CrossTileSymbolKey.CanonicalGridMeters"/> is the honest value to pass);
        /// this method's body never branches on it — dedup always runs (§4.2, the retired no-dedup branch had no
        /// production caller).</para>
        ///
        /// <para><b>Reader cutover (4.2).</b> Winner identity is <c>(blockId, localIndex)</c> — there is no
        /// managed symbol list to hand back. <paramref name="outBlockId"/> indexes <see cref="OrderedBlocks"/>
        /// (assigned in the deterministic scan order — active tiles first, then departing); <paramref name="outLocalIndex"/>
        /// is the winner's RAW position in its tile's block (raw-indexed with inert null slots, so this maps
        /// straight through). <paramref name="outIsDeparting"/> materializes, PER RECORD, exactly what
        /// <paramref name="activeCount"/> already encodes positionally (<c>outIsDeparting[i] == (i &gt;= activeCount)</c>
        /// for every <c>i</c> — active records are still emitted strictly before departing ones): <c>0</c> during the
        /// active scan/curved emit and the winner-dedup emit, <c>1</c> for the departing tail. Decouples a downstream
        /// classifier (<c>SymbolTileCoverageFilter.ClassifyActive</c>) from the prefix-count convention.</para>
        ///
        /// <para><b>Stage 4b:</b> the scan is DELEGATED to <see cref="SymbolReconciler"/> (the ONE dedup impl
        /// the production off-main path also runs) over a <see cref="CaptureSnapshot"/> — so this overload is a
        /// synchronous byte-identical SHIM for the existing tests/oracle; production consumes the subsystem's
        /// front-buffer result instead.</para></summary>
        public void CollectInto(List<int> outBlockId, List<int> outLocalIndex, List<byte> outIsDeparting,
            double quantizeMeters, out int activeCount)
        {
            // Stage 4b: capture → run the shared off-main reconciler → copy out. ONE dedup impl (this shim + the
            // subsystem's per-frame path both run SymbolReconciler.Run), so the existing store/oracle suite is
            // a byte-identical oracle for that impl. CaptureSnapshot PINS each block; this synchronous shim has no
            // async borrow, so it releases the pins immediately after Run (net-zero — nothing is disposed during
            // the call, so OrderedBlocks' refs stay live for the caller).
            CaptureSnapshot(_oracleSnapshot);
            // try/finally so the net-zero-pin guarantee holds even if Run ever faults (it can't today —
            // _oracleReconciler has no gate/fault seam set on this path — but the ReleasePins must be unconditional).
            try { _oracleReconciler.Run(_oracleSnapshot, _oracleResult); }
            finally { ReleasePins(_oracleSnapshot); }

            outBlockId.Clear();     outBlockId.AddRange(_oracleResult.BlockId);
            outLocalIndex.Clear();  outLocalIndex.AddRange(_oracleResult.LocalIndex);
            outIsDeparting.Clear(); outIsDeparting.AddRange(_oracleResult.IsDeparting);
            _orderedBlocks.Clear(); _orderedBlocks.AddRange(_oracleResult.OrderedBlocks);
            activeCount = _oracleResult.ActiveCount;
        }

        // ═══ Stage 4b: main-thread snapshot capture + native-block pin guard (SPEC A) ═══

        // The synchronous plan-aware-CollectInto shim's own reused reconciler + snapshot + result (separate from
        // the subsystem's async instances — each is single-threaded and non-reentrant on its own path).
        private readonly SymbolReconciler _oracleReconciler = new SymbolReconciler();
        private readonly SymbolSnapshot _oracleSnapshot = new SymbolSnapshot();
        private readonly SymbolReconcileResult _oracleResult = new SymbolReconcileResult();

        // Pin guard: how many live snapshots reference each block (front + in-flight back ⇒ up to 2), and the
        // blocks whose real dispose was DEFERRED because they were pinned when a drop site fired. Mutated ONLY by
        // Pin / ReleasePins / DisposeOrDefer. Clear() NEVER wipes _pinCount (SPEC A) — a captured snapshot must
        // still ReleasePins correctly after a drain, so decrements stay matched to their prior Pin.
        private readonly Dictionary<System.IDisposable, int> _pinCount = new Dictionary<System.IDisposable, int>();
        private readonly List<System.IDisposable> _pendingDispose = new List<System.IDisposable>();
        // Reused scratch so ReleasePins decrements each of a snapshot's DISTINCT blocks exactly once.
        private readonly HashSet<System.IDisposable> _releaseScratch = new HashSet<System.IDisposable>();

        /// <summary>Stage 4b (SPEC A): capture the CURRENT collected tile set into <paramref name="into"/> — main
        /// thread — for an off-main <see cref="SymbolReconciler.Run"/>. IDENTICAL scan order to the
        /// plan-aware collect (ACTIVE entries in <c>_active</c> order, then DEPARTING via <c>_cachedIndex</c> in
        /// <c>_departing</c> order — so first-insertion dedup order is preserved). Each slice's block is PINNED so
        /// the store cannot free it while the snapshot is in flight (or displayed); the caller MUST
        /// <see cref="ReleasePins"/> the snapshot when it leaves service.</summary>
        internal void CaptureSnapshot(SymbolSnapshot into)
        {
            into.Clear(); // #3a
            foreach (KeyValuePair<Key, Entry> kv in _active)
            {
                Entry e = kv.Value;
                // Reader cutover (4.2): relocated from `e.SymbolPlacementSystem == null` — the reconciler now reads the block,
                // so the collect-relevant emptiness test is Block itself. SymbolPlacementSystem != null ⇒ Block != null in
                // production (the single commit call site always bakes); a block-less SymbolPlacementSystem-only commit is a
                // test-only state this stage's reworked tests retire (never reachable in production).
                if (e.Block == null) continue;
                TileSlice slice = into.Add();
                slice.Block = (SymbolTileBlock)e.Block; slice.IsDeparting = false;
                Pin(e.Block);
            }
            foreach (KeyValuePair<Key, double> dep in _departing)
            {
                if (!_cachedIndex.TryGetValue(dep.Key, out LinkedListNode<KeyedEntry> node)) continue;
                Entry e = node.Value.Entry;
                if (e.Block == null) continue;
                TileSlice slice = into.Add();
                slice.Block = (SymbolTileBlock)e.Block; slice.IsDeparting = true;
                Pin(e.Block);
            }
        }

        // Pin one snapshot occurrence of a block (null blocks — a symbol-only commit — are inert, never pinned).
        private void Pin(System.IDisposable block)
        {
            if (block == null) return;
            _pinCount[block] = (_pinCount.TryGetValue(block, out int c) ? c : 0) + 1;
        }

        /// <summary>Stage 4b (SPEC A): release the pins <paramref name="snapshot"/> holds — decrement each of its
        /// DISTINCT blocks; at count 0 remove from the pin table and, if a drop site deferred its dispose while it
        /// was pinned, dispose it once here. A block shared with another live snapshot (front + back) survives (its
        /// count stays &gt; 0). An empty snapshot (cold start) is a no-op.</summary>
        internal void ReleasePins(SymbolSnapshot snapshot)
        {
            _releaseScratch.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                System.IDisposable block = snapshot.Slices[i].Block;
                if (block == null || !_releaseScratch.Add(block)) continue; // distinct blocks only
                if (!_pinCount.TryGetValue(block, out int c)) continue;     // defensive: no matching Pin ⇒ skip
                if (--c == 0)
                {
                    _pinCount.Remove(block);
                    int idx = _pendingDispose.IndexOf(block);
                    if (idx >= 0) { _pendingDispose.RemoveAt(idx); block.Dispose(); } // deferred dispose fires now
                }
                else _pinCount[block] = c;
            }
        }

        // Dispose a block at a drop site — UNLESS it is pinned (referenced by a live snapshot), in which case defer
        // the real free until the last ReleasePins drops its count to 0 (never free a block an off-main reconcile
        // is reading). Null-safe (mirrors the former block?.Dispose()).
        private void DisposeOrDefer(System.IDisposable block)
        {
            if (block == null) return;
            if (_pinCount.ContainsKey(block))
            {
                if (!_pendingDispose.Contains(block)) _pendingDispose.Add(block);
            }
            else block.Dispose();
        }

        /// <summary>Drop everything (a restyle purges the mesh cache too) — disposes every active AND cached
        /// entry's block first (a restyle's <c>_store.Clear()</c> is a genuine drop of the whole set, not a
        /// stale-survives/cache-hit move).
        ///
        /// <para>Stage 4b (SPEC A): disposes via <see cref="DisposeOrDefer"/> — the restyle/teardown protocol has
        /// already <see cref="ReleasePins"/>'d the front + back snapshots by the time Clear runs, so every block is
        /// unpinned ⇒ freed immediately. The <c>_pendingDispose</c> flush below is CONDITIONAL — it frees only a
        /// deferred block that is NO LONGER pinned; a still-pinned block stays in <c>_pendingDispose</c> so its
        /// eventual <see cref="ReleasePins"/> frees it (Clear must NEVER free a block a live snapshot still reads —
        /// even on an out-of-order call, this keeps the store self-safe against a use-after-free). <c>_pinCount</c>
        /// is deliberately NOT wiped — a captured snapshot still owes its <see cref="ReleasePins"/>, whose
        /// decrements must stay matched.</para></summary>
        public void Clear()
        {
            foreach (KeyValuePair<Key, Entry> kv in _active) DisposeOrDefer(kv.Value.Block);
            foreach (KeyedEntry keyed in _cachedOrder) DisposeOrDefer(keyed.Entry.Block);
            // Conditional straggler flush: free ONLY the deferred blocks that are no longer pinned; leave a
            // still-pinned one for its ReleasePins (no leak — ReleasePins disposes it at count 0).
            for (int i = _pendingDispose.Count - 1; i >= 0; i--)
            {
                System.IDisposable block = _pendingDispose[i];
                if (_pinCount.ContainsKey(block)) continue; // still pinned ⇒ leave it (a live snapshot owes ReleasePins)
                _pendingDispose.RemoveAt(i);
                block.Dispose();
            }
            _active.Clear();
            _cachedIndex.Clear();
            _cachedOrder.Clear();
            _departing.Clear();
            _stringTable.Reset(); // Stage 2: SetStyle-boundary reset of the interning table (ids restart at 1)
            _orderedBlocks.Clear(); // drop borrowed refs to the now-disposed blocks (Stage-2 gather plan seam)
            MarkCollectDirty(); // Stage 4a: the whole set → empty (restyle); covers the _stringTable.Reset renumber too (§1 #8)
        }

        // Reused reconcile scratch (main-thread, non-reentrant) — keeps ReconcileActiveSet allocation-free in
        // steady state: Clear + Add-at-capacity on the set, and the two move-lists never grow once the cover
        // stabilises (an unchanged loaded set collects nothing). Honours the per-frame no-GC contract.
        private readonly HashSet<Key> _loadedScratch = new HashSet<Key>();
        private readonly List<Key> _reconcileRelease = new List<Key>();
        private readonly List<Key> _reconcileRestore = new List<Key>();

        /// <summary>
        /// A-1 PULL model: reconcile the active set against the tile pipeline's current loaded
        /// <c>(source, tile)</c> membership, replacing the release/restore push-callbacks. For each currently
        /// ACTIVE tile no longer in <paramref name="loaded"/>: release it (kept warm on the cached side iff
        /// <paramref name="keepWarmOnRelease"/> — i.e. the prepared mesh cache is enabled, so a later hit can
        /// restore it without a re-fetch; otherwise dropped). For each loaded tile whose symbols are currently on
        /// the CACHED side: restore it (a prepared-cache re-entry brings the tile back with no fetch, hence no
        /// bytes-ready rebuild to resurrect the symbols). A loaded tile with no entry yet is left untouched — its
        /// build is kicked by the bytes-ready push when its MVT bytes arrive. Idempotent: a second call with the
        /// same loaded set moves nothing.
        /// </summary>
        public void ReconcileActiveSet(IReadOnlyList<Key> loaded, bool keepWarmOnRelease,
            double nowSeconds = 0.0, double departingGraceSeconds = 0.0)
        {
            // Stage 4a: this method deliberately does NOT MarkCollectDirty() directly. It runs EVERY frame (via
            // SymbolSubsystem.ReconcileLoadedTiles), and on a stable loaded set with no expiring departing it moves
            // nothing — so a self-bump would dirty every frame and the memo (CurrentBatch's generation guard) would
            // never fire. Its real effects inherit their bumps from the Release/Restore it calls (§1 #3/#4) and the
            // conditional PurgeExpiredDeparting bump (§1 #7). Do NOT add a bump here. (THE critical correctness point.)
            _loadedScratch.Clear();
            for (int i = 0; i < loaded.Count; i++) _loadedScratch.Add(loaded[i]);

            // Release actives that left the loaded set (collect first — cannot mutate _active while iterating).
            _reconcileRelease.Clear();
            foreach (KeyValuePair<Key, Entry> kv in _active)
                if (!_loadedScratch.Contains(kv.Key)) _reconcileRelease.Add(kv.Key);
            for (int i = 0; i < _reconcileRelease.Count; i++)
            {
                Key key = _reconcileRelease[i];
                Release(key, keepWarmOnRelease);
                // Retain-as-departing: a tile kept warm on release fades out over the grace window. Stamp AFTER
                // Release (whose EnqueueCached ran RemoveCached → cleared any prior stamp), and only if it actually
                // landed on the cached side (keepWarm) and a grace window is set (grace ≤ 0 ⇒ feature off).
                if (departingGraceSeconds > 0.0 && _cachedIndex.ContainsKey(key))
                    _departing[key] = nowSeconds + departingGraceSeconds;
            }

            // Restore cached tiles that re-entered the loaded set (a cache hit — no rebuild is coming). Restore
            // funnels through RemoveCached, which clears the departing stamp → a re-entered tile fades back IN.
            _reconcileRestore.Clear();
            for (int i = 0; i < loaded.Count; i++)
                if (_cachedIndex.ContainsKey(loaded[i])) _reconcileRestore.Add(loaded[i]);
            for (int i = 0; i < _reconcileRestore.Count; i++)
                Restore(_reconcileRestore[i]);

            PurgeExpiredDeparting(nowSeconds);
        }

        // Drop departing stamps whose grace window elapsed (now >= expiry) or whose cached entry was FIFO-evicted
        // (defensive — RemoveCached already unstamps on eviction). A purged tile's symbols are no longer collected as
        // departing, so they stop being staged/faded; the symbol stays warm on the cached side for a later cache hit.
        // Grace exceeds the fade duration (see SymbolSubsystem.DepartingGraceSeconds), so a purge only ever
        // drops an already-faded (invisible) symbol — never mid-fade, which would pop.
        private readonly List<Key> _departingPurgeScratch = new List<Key>();
        private void PurgeExpiredDeparting(double nowSeconds)
        {
            if (_departing.Count == 0) return;
            _departingPurgeScratch.Clear();
            foreach (KeyValuePair<Key, double> kv in _departing)
                if (nowSeconds >= kv.Value || !_cachedIndex.ContainsKey(kv.Key)) _departingPurgeScratch.Add(kv.Key);
            for (int i = 0; i < _departingPurgeScratch.Count; i++) _departing.Remove(_departingPurgeScratch[i]);
            // Stage 4a: bump iff ≥1 departing key was actually removed — those tiles stop being collected as departing,
            // changing AppendDeparting (§1 #7). Runs every frame but usually removes nothing → no bump (memo intact).
            if (_departingPurgeScratch.Count > 0) MarkCollectDirty();
        }

        /// <summary>Test-only: the RAW (undeduped, single-tile) baked block committed for <paramref name="key"/> —
        /// unlike <see cref="CollectInto(List{int},List{int},List{byte},double,out int)"/> (deduped, cross-tile,
        /// block-identity), this returns exactly what <see cref="CompleteBuild"/> committed, or <c>null</c> if
        /// nothing is committed. A content-comparison-test seam that needs one tile's
        /// exact baked columns, not the winner plan — mirrors the established <c>DebugLiveAllocCount</c> pattern
        /// (<see cref="SymbolTileBlock"/>): internal, named Debug-prefixed, no production caller.</summary>
        internal SymbolTileBlock DebugBlockFor(Key key) => (SymbolTileBlock)FindCurrent(key)?.Block;

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
                DisposeOrDefer(oldest.Value.Entry.Block); // over-cap eviction is a genuine drop (deferred iff a snapshot pins it)
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
