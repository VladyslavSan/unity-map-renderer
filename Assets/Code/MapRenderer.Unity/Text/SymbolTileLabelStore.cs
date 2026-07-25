// Engine-free despite living under Assets/Code/MapRenderer.Unity/Text (like StyledSymbolTileBuilder): it
// references only MapRenderer.Core types (LabelInstance, TileId) — NO `using UnityEngine`. Co-located with
// the symbol subsystem that owns it, and compiled by BOTH the Unity runner and Tools/core-tests (the
// matching <Compile Include> lives in Tools/core-tests/core-tests.csproj) so its lifecycle logic — the part
// with the subtle async/cache races — gets the fast headless tests. Do NOT add a UnityEngine reference.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
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
        //
        // Symbol-label perf Phase 1 / Stage 1 (design §4, §5 B): Block is the tile's baked native SoA slice
        // (a MapRenderer.Unity.Text.Placement.SymbolTileLabelBlock — a NativeArray holder that needs
        // Unity.Collections), held here ONLY as System.IDisposable so this engine-free store (compiled by
        // Tools/core-tests too — see this file's header) never references Unity.Collections. Mirrors Labels'
        // lifecycle exactly: kept on a stale-survives (BeginBuild pull) / cache-hit move (Restore), disposed at
        // every genuine drop site (commit-overwrite, FIFO-evict, true-release, Clear).
        // Stage 2 (labels-async-reconcile): TextIds/IconImageIds are the interned text/icon ids PARALLEL to
        // Labels (index-aligned: TextIds[i] is Intern(Labels[i]?.Text)), computed once per label in CompleteBuild
        // so the per-frame cross-tile dedup keys on an integer id, not a string. A null label slot (or null
        // Text/IconImage) → id 0 (inert). They travel with the entry through release/cache/restore exactly as
        // Labels does, so a departing tile's winners key correctly.
        private sealed class Entry
        {
            public int Generation;
            public List<LabelInstance> Labels;
            public int[] TextIds;
            public int[] IconImageIds;
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
        // cache) is clamped to HardCacheCap, never left uncapped: an unbounded warm-label FIFO would grow with
        // every tile ever visited (the "always bound loops/collections, even at a laughably large value"
        // principle — see the always-bound-loops lesson).
        private const int HardCacheCap = 4096;
        private readonly int _cacheCap;
        private int _genCounter;

        // Stage 4a (labels-async-reconcile): a monotonic "collect-relevant state changed" counter. CollectInto is a
        // PURE function of the loaded tile set (Stage 3 — fixed canonical grid, zoom/camera-independent), so a
        // consumer (SymbolLabelSubsystem.CurrentBatch) that caches the generation it last collected at can REUSE its
        // already-filled buffers on any frame this counter is unchanged — a byte-identical recompute is skipped. Bumped
        // (MarkCollectDirty) at each mutating public entry point that changes active membership, cached/departing
        // membership feeding AppendDeparting, or a tile's label content — conditional on an ACTUAL change, never
        // unconditionally at entry (see ReconcileActiveSet, which runs every frame and must NOT dirty a stable cover).
        private int _collectGeneration;

        // Stage 2 (labels-async-reconcile): store-owned string→int interning for the dedup key. Mutated ONLY in
        // CompleteBuild (main thread), Reset in Clear (the SetStyle boundary). Ids are stable for a style's life;
        // a restyle renumbers (harmless — ids feed only equality/hashing, never a snapshot).
        private readonly LabelTextIntern _textIntern = new LabelTextIntern();

        // Retain-as-departing: a tile released to the cached side within a grace window is still COLLECTED (as
        // departing) so its labels fade OUT instead of popping when the tile leaves cover. key -> wall-clock expiry
        // (seconds). Invariant: departing ⊆ cached — every "leaves cached" path funnels through RemoveCached, which
        // clears the stamp, so a re-fetched / restored tile drops it automatically. Stamped by ReconcileActiveSet on
        // release, purged at expiry. Empty ⇒ CollectInto emits ACTIVE only (the feature is off when grace ≤ 0).
        private readonly Dictionary<Key, double> _departing = new Dictionary<Key, double>();

        public SymbolTileLabelStore(int cacheCap)
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
        // _active/_departing/_cachedIndex and writes its output/_dedup/_orderedBlocks), so the bumps never self-trigger.
        private void MarkCollectDirty() => _collectGeneration++;

        /// <summary>
        /// Reserve an active slot for a (re)build of <paramref name="key"/> and return the generation token the
        /// matching <see cref="CompleteBuild"/> must present.
        ///
        /// <para><b>Stale labels survive the rebuild.</b> If the tile already has an entry (active, or cached and
        /// being pulled active by a re-fetch), its EXISTING labels are kept on the active side and only the
        /// generation is bumped — so a rebuilding / reappearing tile keeps drawing its last labels until the new
        /// build commits, rather than flashing empty for the fetch+shape window. A tile first seen this session
        /// starts empty (no labels yet). The bumped generation still discards any superseded in-flight build.</para>
        /// </summary>
        public int BeginBuild(Key key)
        {
            Entry entry = FindCurrent(key); // active or cached (a re-fetch of an out-of-cover tile)
            RemoveCached(key);              // pull it fully onto the active side; keep its labels
            int gen = ++_genCounter;
            if (entry == null) entry = new Entry { Labels = null };
            entry.Generation = gen;
            _active[key] = entry;
            // Stage 4a: always bump. A re-fetch pulls a cached/departing tile (with its labels) onto the active side —
            // that changes collect. A brand-new (null-Labels) tile does NOT, but distinguishing is fragile and an
            // extra byte-identical recompute on a per-tile-build event (never per-frame) is harmless (§1 #1).
            MarkCollectDirty();
            return gen;
        }

        /// <summary>
        /// Commit <paramref name="labels"/> (+ optionally its baked native <paramref name="block"/> — Phase 1
        /// Stage 1, design §4/§5 B) for <paramref name="key"/> iff the build identified by <paramref name="gen"/>
        /// still owns the tile's entry (in the active OR cached map — a release may have moved it). Returns true
        /// when committed, false when the build was superseded or the tile was dropped.
        ///
        /// <para>Single-owner swap: on a genuine commit the entry's OLD <see cref="Entry.Block"/> is disposed
        /// before the new one is assigned (never two live blocks for one entry). On a superseded/dropped commit
        /// the CALLER's <paramref name="block"/> is disposed here — the caller never re-owns it, mirroring a
        /// baked block having nowhere to land.</para></summary>
        public bool CompleteBuild(Key key, int gen, List<LabelInstance> labels, IDisposable block = null)
        {
            Entry e = FindCurrent(key);
            if (e == null || e.Generation != gen)
            {
                block?.Dispose(); // superseded or dropped mid-build — this build's bake never lands, never leaks
                return false;
            }
            DisposeOrDefer(e.Block); // single-owner swap: the entry's old block is replaced (deferred iff a snapshot pins it)
            e.Labels = labels;
            // Stage 2: intern each label's text/icon ONCE here (the single commit choke every collected label
            // passes through), into arrays parallel to Labels, so the per-frame dedup keys on an int, not a
            // string. null label slot / null Text / null IconImage → id 0 (inert, never a winner).
            int n = labels?.Count ?? 0;
            var textIds = new int[n];
            var iconImageIds = new int[n];
            for (int i = 0; i < n; i++)
            {
                LabelInstance lbl = labels[i];
                textIds[i] = _textIntern.Intern(lbl?.Text);
                iconImageIds[i] = _textIntern.Intern(lbl?.IconImage);
            }
            e.TextIds = textIds;
            e.IconImageIds = iconImageIds;
            e.Block = block;
            MarkCollectDirty(); // Stage 4a: commit path only — label content landed/replaced (§1 #2). The superseded
            return true;        // early-return above stays UNBUMPED (no store state changed — §1 #2b).
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

        /// <summary>A tile re-entered cover via a prepared-cache HIT (no fetch): move its kept-warm labels back
        /// to the active set so they render again. A no-op if nothing was cached for it.</summary>
        public void Restore(Key key)
        {
            // Stage 4a: bump only when the move actually happens (cached → active clears the departing stamp — §1 #4);
            // a no-op restore (nothing cached — §1 #4b) leaves collect unchanged, so it must not dirty.
            if (RemoveCached(key, out Entry e)) { _active[key] = e; MarkCollectDirty(); }
        }

        // A-3: reused cross-tile dedup index (main-thread CollectInto only; not reentrant) — keyed by the
        // stable (quantized-anchor, layer, text-id, icon-id) identity, value = the winning label + its tile
        // zoom/key for the finest-zoom-wins tiebreak. Reused so the per-frame dedup is allocation-free at capacity.
        private readonly Dictionary<DedupKey, DedupEntry> _dedup =
            new Dictionary<DedupKey, DedupEntry>();

        // Stage 4b: DedupKey + DedupEntry moved to SymbolLabelReconciler.cs (internal top-level, same namespace)
        // so the off-main reconciler and these legacy plain overloads share ONE definition — the relocation is
        // textual only (GetHashCode/Equals unchanged ⇒ the dedup partition, and every byte-identity oracle, holds).

        // Stage-2: the per-collect ORDERED block list — one entry per scanned tile (with non-null Labels), in the
        // exact deterministic order the plan's BlockId indexes into: the _active scan FIRST, then the departing
        // scan (so a departing/cached tile's block, which winners can still reference, is included). Held as
        // System.IDisposable — the engine-free store never names the Unity-side block type (see the Entry.Block
        // header). Stage 4b: now a COPY TARGET — the plan-aware CollectInto delegates the scan to
        // SymbolLabelReconciler and copies the result's OrderedBlocks here, so OrderedBlocks still serves the
        // existing tests/oracle unchanged (production reads the subsystem's front-buffer result directly).
        private readonly List<System.IDisposable> _orderedBlocks = new List<System.IDisposable>();

        /// <summary>Stage-2: the ordered blocks the last plan-aware <see cref="CollectInto(List{LabelInstance},
        /// List{int}, List{int}, double, out int)"/> assigned <c>blockId</c>s against — <c>OrderedBlocks[blockId]</c>
        /// is the baked native block a winner's <c>(blockId, localIndex)</c> references. Borrowed refs (the store
        /// owns their disposal); valid until the next plan-aware collect.</summary>
        public IReadOnlyList<System.IDisposable> OrderedBlocks => _orderedBlocks;

        /// <summary>Aggregate every ACTIVE tile's labels into <paramref name="output"/> for this frame's
        /// placement pass. Cached (out-of-cover) tiles are deliberately excluded — they must not render.
        ///
        /// <para>A-3: when <paramref name="quantizeMeters"/> &gt; 0, POINT labels are DEDUPED across tiles by
        /// their <see cref="CrossTileLabelKey"/> — the same symbol present in a parent + child tile during a
        /// zoom transition collapses to ONE (the finest tile zoom wins; ties broken by lowest tile key), so it
        /// is not double-drawn and its identity is stable across the swap. Line labels are passed through
        /// undeduped (per-anchor line identity is a follow-up). <paramref name="quantizeMeters"/> ≤ 0 disables
        /// dedup entirely (every active label is emitted, order-preserving — the pre-A-3 behaviour).</para>
        ///
        /// <para><b>Stage 3:</b> <paramref name="quantizeMeters"/> is now a pure on/off GATE (&gt; 0 enables
        /// dedup, ≤ 0 disables) — its MAGNITUDE no longer sets the grid. The dedup grid is the fixed
        /// <see cref="CrossTileLabelKey.CanonicalGridMeters"/>, decoupled from display zoom, so neither fractional
        /// nor integer zoom can perturb the winner set (the winners are a pure function of the tile set — the
        /// Stage-4 memoization prerequisite) and the departing claim-skip holds across a zoom step (old- and
        /// new-band copies of a co-located feature key on the SAME grid → same cell).</para></summary>
        public void CollectInto(List<LabelInstance> output, double quantizeMeters = 0.0)
            => CollectInto(output, quantizeMeters, out _);

        /// <summary>As <see cref="CollectInto(List{LabelInstance}, double)"/>, but also appends DEPARTING labels
        /// (tiles that left cover within the fade-out grace window, retained warm) AFTER the active ones, and reports
        /// the split: <paramref name="activeCount"/> is the number of active labels written first; every label at
        /// index ≥ that is departing (the caller marks those records so they fade OUT instead of popping). A departing
        /// POINT label whose cross-tile identity is already claimed by an active label is skipped — the active copy
        /// still shows, so the label transfers tiles seamlessly with no fade.</summary>
        public void CollectInto(List<LabelInstance> output, double quantizeMeters, out int activeCount)
        {
            output.Clear();
            if (quantizeMeters <= 0.0)
            {
                foreach (KeyValuePair<Key, Entry> kv in _active)
                    if (kv.Value.Labels != null) output.AddRange(kv.Value.Labels);
                activeCount = output.Count;
                AppendDeparting(output, quantizeMeters, claims: null);
                return;
            }

            _dedup.Clear();
            foreach (KeyValuePair<Key, Entry> kv in _active)
            {
                List<LabelInstance> labels = kv.Value.Labels;
                if (labels == null) continue;
                for (int i = 0; i < labels.Count; i++)
                {
                    LabelInstance label = labels[i];
                    if (label == null) continue;
                    // Only point labels carry a cross-tile identity in v1; line labels emit as-is.
                    if (label.Placement != SymbolPlacement.Point) { output.Add(label); continue; }

                    // Stage 2: key on the interned text/icon ids (parallel to Labels, so index i lines up) — an
                    // int equality partition byte-identical to the old string one, minus the per-frame hash.
                    // Stage 3: dedup grid = the fixed CrossTileLabelKey.CanonicalGridMeters (no parent/child overlap
                    // today, design §1.2) — NOT quantizeMeters, which now only GATES dedup on/off (see CollectInto
                    // doc). FUTURE (parent/child overlap, design §6): the same feature reprojects a few metres apart
                    // across bands → a fixed grid can miss the merge; go back to a zoom-scaled grid — pick the COARSER
                    // band's grid for both candidates + finest-zoom-wins (already implemented: DedupEntry.Z / TileKey
                    // tiebreak). Change THIS grid input only.
                    var key = DedupKey.For(label.AnchorRender, label.MaterialIndex, kv.Value.TextIds[i], kv.Value.IconImageIds[i], CrossTileLabelKey.CanonicalGridMeters);
                    int z = (int)(label.TileKey >> 44); // PackTileKey: z in the high bits (finest zoom wins)
                    if (!_dedup.TryGetValue(key, out DedupEntry cur)
                        || z > cur.Z || (z == cur.Z && label.TileKey < cur.TileKey))
                        _dedup[key] = new DedupEntry { Label = label, Z = z, TileKey = label.TileKey };
                }
            }
            foreach (KeyValuePair<DedupKey, DedupEntry> kv in _dedup) output.Add(kv.Value.Label);
            activeCount = output.Count;
            AppendDeparting(output, quantizeMeters, claims: _dedup);
        }

        // Append departing labels (retained past release) AFTER the active split. When deduping (claims != null), a
        // departing POINT label whose cross-tile identity is already CLAIMED — by an active label, or by an earlier
        // departing copy — is skipped (the claimed copy shows / fades; no double-draw). Line labels carry no
        // cross-tile identity → always appended. Iterates _departing (not mutated here); labels come from the warm
        // cached entries (departing ⊆ cached, but guard the lookup defensively).
        private void AppendDeparting(List<LabelInstance> output, double quantizeMeters,
            Dictionary<DedupKey, DedupEntry> claims)
        {
            if (_departing.Count == 0) return;
            foreach (KeyValuePair<Key, double> dep in _departing)
            {
                if (!_cachedIndex.TryGetValue(dep.Key, out LinkedListNode<KeyedEntry> node)) continue;
                Entry entry = node.Value.Entry;
                List<LabelInstance> labels = entry.Labels;
                if (labels == null) continue;
                for (int i = 0; i < labels.Count; i++)
                {
                    LabelInstance label = labels[i];
                    if (label == null) continue;
                    if (claims != null && label.Placement == SymbolPlacement.Point)
                    {
                        // Stage 2: key on the departing entry's own interned ids (index-aligned with its Labels,
                        // both set together in CompleteBuild), so a departing winner keys identically to an active one.
                        // Stage 3: same FIXED CanonicalGridMeters as the active scan — so a co-located feature's
                        // departing (old-band) copy and its active (new-band) copy land in the SAME cell across a zoom
                        // step → this claim-skip fires and the tile swap is a seamless hold, not a fade duplicate.
                        var key = DedupKey.For(label.AnchorRender, label.MaterialIndex, entry.TextIds[i], entry.IconImageIds[i], CrossTileLabelKey.CanonicalGridMeters);
                        if (claims.ContainsKey(key)) continue;                 // active/earlier copy already shows it
                        claims[key] = new DedupEntry { Label = label, Z = 0, TileKey = label.TileKey }; // claim (ContainsKey only)
                    }
                    output.Add(label);
                }
            }
        }

        /// <summary>Stage-2 (symbol-label native gather): as <see cref="CollectInto(List{LabelInstance}, double, out int)"/>,
        /// but ALSO fills the parallel winner-plan arrays <paramref name="outBlockId"/> / <paramref name="outLocalIndex"/>
        /// / <paramref name="outIsDeparting"/> (one entry per emitted label, in lockstep with <paramref name="output"/>)
        /// and builds <see cref="OrderedBlocks"/>, so a downstream native gather can compact each winner's pre-baked
        /// block slice with no per-frame SoA build.
        ///
        /// <para>The <c>blockId</c> indexes <see cref="OrderedBlocks"/> (assigned in the deterministic scan order —
        /// active tiles first, then departing); the <c>localIndex</c> is the winner's RAW position in its tile's
        /// label list (the block is raw-indexed with inert null slots, so this maps straight through). Emit order
        /// is IDENTICAL to the plain overload — active CURVED during the <c>_active</c> scan, active POINT winners
        /// via the <c>_dedup</c> enumeration, then departing — so the plan stays byte-aligned with the collected
        /// list a parity oracle (<c>SymbolLabelBatchBuilder.Build</c>) would walk.</para>
        ///
        /// <para><b>D1 (Blocker 2):</b> <paramref name="outIsDeparting"/> materializes, PER RECORD, exactly what
        /// <paramref name="activeCount"/> already encoded positionally (<c>outIsDeparting[i] == (i &gt;= activeCount)</c>
        /// for every <c>i</c> — active records are still emitted strictly before departing ones here, so this is
        /// byte-identical by construction): <c>0</c> during the active scan/curved emit and the <c>_dedup</c> winner
        /// emit, <c>1</c> for the departing tail.
        /// Decouples a downstream classifier (<c>LabelTileCoverageFilter.ClassifyActive</c>) from the prefix-count
        /// convention, which a future canonical reorder (D2) would invalidate.</para>
        ///
        /// <para><b>Stage 4b:</b> the scan is DELEGATED to <see cref="SymbolLabelReconciler"/> (the ONE dedup impl
        /// the production off-main path also runs) over a <see cref="CaptureSnapshot"/> — so this overload is now a
        /// synchronous byte-identical SHIM for the existing tests/oracle; production consumes the subsystem's
        /// front-buffer result instead. The former <paramref name="quantizeMeters"/> ≤ 0 no-dedup branch is dropped
        /// (it had no caller — the fixed grid gate is always &gt; 0); the param is retained only to gate on.</para></summary>
        public void CollectInto(List<LabelInstance> output, List<int> outBlockId, List<int> outLocalIndex,
            List<byte> outIsDeparting, double quantizeMeters, out int activeCount)
        {
            // Stage 4b: capture → run the shared off-main reconciler → copy out. ONE dedup impl (this shim + the
            // subsystem's per-frame path both run SymbolLabelReconciler.Run), so the existing store/oracle suite is
            // a byte-identical oracle for that impl. CaptureSnapshot PINS each block; this synchronous shim has no
            // async borrow, so it releases the pins immediately after Run (net-zero — nothing is disposed during
            // the call, so OrderedBlocks' refs stay live for the caller).
            CaptureSnapshot(_oracleSnapshot);
            // try/finally so the net-zero-pin guarantee holds even if Run ever faults (it can't today —
            // _oracleReconciler has no gate/fault seam set on this path — but the ReleasePins must be unconditional).
            try { _oracleReconciler.Run(_oracleSnapshot, _oracleResult); }
            finally { ReleasePins(_oracleSnapshot); }

            output.Clear();         output.AddRange(_oracleResult.Output);
            outBlockId.Clear();     outBlockId.AddRange(_oracleResult.BlockId);
            outLocalIndex.Clear();  outLocalIndex.AddRange(_oracleResult.LocalIndex);
            outIsDeparting.Clear(); outIsDeparting.AddRange(_oracleResult.IsDeparting);
            _orderedBlocks.Clear(); _orderedBlocks.AddRange(_oracleResult.OrderedBlocks);
            activeCount = _oracleResult.ActiveCount;
        }

        // ═══ Stage 4b: main-thread snapshot capture + native-block pin guard (SPEC A) ═══

        // The synchronous plan-aware-CollectInto shim's own reused reconciler + snapshot + result (separate from
        // the subsystem's async instances — each is single-threaded and non-reentrant on its own path).
        private readonly SymbolLabelReconciler _oracleReconciler = new SymbolLabelReconciler();
        private readonly SymbolLabelSnapshot _oracleSnapshot = new SymbolLabelSnapshot();
        private readonly SymbolLabelReconcileResult _oracleResult = new SymbolLabelReconcileResult();

        // Pin guard: how many live snapshots reference each block (front + in-flight back ⇒ up to 2), and the
        // blocks whose real dispose was DEFERRED because they were pinned when a drop site fired. Mutated ONLY by
        // Pin / ReleasePins / DisposeOrDefer. Clear() NEVER wipes _pinCount (SPEC A) — a captured snapshot must
        // still ReleasePins correctly after a drain, so decrements stay matched to their prior Pin.
        private readonly Dictionary<System.IDisposable, int> _pinCount = new Dictionary<System.IDisposable, int>();
        private readonly List<System.IDisposable> _pendingDispose = new List<System.IDisposable>();
        // Reused scratch so ReleasePins decrements each of a snapshot's DISTINCT blocks exactly once.
        private readonly HashSet<System.IDisposable> _releaseScratch = new HashSet<System.IDisposable>();

        /// <summary>Stage 4b (SPEC A): capture the CURRENT collected tile set into <paramref name="into"/> — main
        /// thread — for an off-main <see cref="SymbolLabelReconciler.Run"/>. IDENTICAL scan order to the
        /// plan-aware collect (ACTIVE entries in <c>_active</c> order, then DEPARTING via <c>_cachedIndex</c> in
        /// <c>_departing</c> order — so first-insertion dedup order is preserved). Each slice's block is PINNED so
        /// the store cannot free it while the snapshot is in flight (or displayed); the caller MUST
        /// <see cref="ReleasePins"/> the snapshot when it leaves service.</summary>
        internal void CaptureSnapshot(SymbolLabelSnapshot into)
        {
            into.Clear(); // #3a
            foreach (KeyValuePair<Key, Entry> kv in _active)
            {
                Entry e = kv.Value;
                if (e.Labels == null) continue; // matches the collect scan: a null-Labels tile contributes no block
                TileSlice slice = into.Add();
                slice.Labels = e.Labels; slice.TextIds = e.TextIds; slice.IconImageIds = e.IconImageIds;
                slice.Block = e.Block; slice.IsDeparting = false;
                Pin(e.Block);
            }
            foreach (KeyValuePair<Key, double> dep in _departing)
            {
                if (!_cachedIndex.TryGetValue(dep.Key, out LinkedListNode<KeyedEntry> node)) continue;
                Entry e = node.Value.Entry;
                if (e.Labels == null) continue;
                TileSlice slice = into.Add();
                slice.Labels = e.Labels; slice.TextIds = e.TextIds; slice.IconImageIds = e.IconImageIds;
                slice.Block = e.Block; slice.IsDeparting = true;
                Pin(e.Block);
            }
        }

        // Pin one snapshot occurrence of a block (null blocks — a label-only commit — are inert, never pinned).
        private void Pin(System.IDisposable block)
        {
            if (block == null) return;
            _pinCount[block] = (_pinCount.TryGetValue(block, out int c) ? c : 0) + 1;
        }

        /// <summary>Stage 4b (SPEC A): release the pins <paramref name="snapshot"/> holds — decrement each of its
        /// DISTINCT blocks; at count 0 remove from the pin table and, if a drop site deferred its dispose while it
        /// was pinned, dispose it once here. A block shared with another live snapshot (front + back) survives (its
        /// count stays &gt; 0). An empty snapshot (cold start) is a no-op.</summary>
        internal void ReleasePins(SymbolLabelSnapshot snapshot)
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
            _textIntern.Reset(); // Stage 2: SetStyle-boundary reset of the interning table (ids restart at 1)
            _orderedBlocks.Clear(); // drop borrowed refs to the now-disposed blocks (Stage-2 gather plan seam)
            MarkCollectDirty(); // Stage 4a: the whole set → empty (restyle); covers the _textIntern.Reset renumber too (§1 #8)
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
        /// restore it without a re-fetch; otherwise dropped). For each loaded tile whose labels are currently on
        /// the CACHED side: restore it (a prepared-cache re-entry brings the tile back with no fetch, hence no
        /// bytes-ready rebuild to resurrect the labels). A loaded tile with no entry yet is left untouched — its
        /// build is kicked by the bytes-ready push when its MVT bytes arrive. Idempotent: a second call with the
        /// same loaded set moves nothing.
        /// </summary>
        public void ReconcileActiveSet(IReadOnlyList<Key> loaded, bool keepWarmOnRelease,
            double nowSeconds = 0.0, double departingGraceSeconds = 0.0)
        {
            // Stage 4a: this method deliberately does NOT MarkCollectDirty() directly. It runs EVERY frame (via
            // SymbolLabelSubsystem.ReconcileLoadedTiles), and on a stable loaded set with no expiring departing it moves
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
        // (defensive — RemoveCached already unstamps on eviction). A purged tile's labels are no longer collected as
        // departing, so they stop being staged/faded; the label stays warm on the cached side for a later cache hit.
        // Grace exceeds the fade duration (see SymbolLabelSubsystem.DepartingGraceSeconds), so a purge only ever
        // drops an already-faded (invisible) label — never mid-fade, which would pop.
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
