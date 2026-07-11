// Engine-free despite living under Assets/Code/MapRenderer.Unity/Text (like StyledSymbolTileBuilder): it
// references only MapRenderer.Core types (LabelInstance, TileId) — NO `using UnityEngine`. Co-located with
// the symbol subsystem that owns it, and compiled by BOTH the Unity runner and Tools/core-tests (the
// matching <Compile Include> lives in Tools/core-tests/core-tests.csproj) so its lifecycle logic — the part
// with the subtle async/cache races — gets the fast headless tests. Do NOT add a UnityEngine reference.

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

        // Max cached (out-of-cover) tiles kept warm. ALWAYS finite — a caller-supplied <= 0 ("unbounded" mesh
        // cache) is clamped to HardCacheCap, never left uncapped: an unbounded warm-label FIFO would grow with
        // every tile ever visited (the "always bound loops/collections, even at a laughably large value"
        // principle — see the always-bound-loops lesson).
        private const int HardCacheCap = 4096;
        private readonly int _cacheCap;
        private int _genCounter;

        // B-1: monotonic version of the COLLECTED label set — bumped by every mutation that could change what
        // CollectInto emits (BeginBuild / CompleteBuild-commit / Release / Restore / Clear). A NO-OP
        // ReconcileActiveSet does NOT bump it (it only calls Release/Restore for actual moves), so a static
        // frame's version is stable → the LabelPlacementSystem static-frame skip can trust "unchanged". Errs
        // toward OVER-bumping (a spurious bump only costs a missed skip; a missed bump would freeze stale labels).
        private long _version;

        public SymbolTileLabelStore(int cacheCap)
            => _cacheCap = cacheCap > 0 && cacheCap < HardCacheCap ? cacheCap : HardCacheCap;

        /// <summary>B-1: monotonic version of the collected label set (see <see cref="_version"/>).</summary>
        public long Version => _version;

        /// <summary>Active (in-cover) tile count — test/telemetry.</summary>
        public int ActiveTileCount => _active.Count;

        /// <summary>Cached (out-of-cover, kept-warm) tile count — test/telemetry.</summary>
        public int CachedTileCount => _cachedIndex.Count;

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
            _version++; // B-1: an active entry appeared/was re-generated
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
            _version++; // B-1: this tile's labels changed (membership may be unchanged — the case LoadedRevision missed)
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
                _version++; // B-1: an active tile left the collected set
            }
            else if (!transferredToCache && RemoveCached(key))
            {
                // not active but a stale cached copy existed → dropped it (cached tiles aren't collected, so this
                // does NOT change CollectInto's output — but over-bumping is safe and keeps the rule simple).
                _version++;
            }
        }

        /// <summary>A tile re-entered cover via a prepared-cache HIT (no fetch): move its kept-warm labels back
        /// to the active set so they render again. A no-op if nothing was cached for it.</summary>
        public void Restore(Key key)
        {
            if (RemoveCached(key, out Entry e)) { _active[key] = e; _version++; } // B-1: labels re-entered the set
        }

        // A-3: reused cross-tile dedup index (main-thread CollectInto only; not reentrant) — keyed by the
        // stable (quantized-anchor, layer, text) identity, value = the winning label + its tile zoom/key for the
        // finest-zoom-wins tiebreak. Reused so the per-frame dedup is allocation-free at capacity.
        private readonly Dictionary<CrossTileLabelKey, DedupEntry> _dedup =
            new Dictionary<CrossTileLabelKey, DedupEntry>();
        private struct DedupEntry { public LabelInstance Label; public int Z; public long TileKey; }

        /// <summary>Aggregate every ACTIVE tile's labels into <paramref name="output"/> for this frame's
        /// placement pass. Cached (out-of-cover) tiles are deliberately excluded — they must not render.
        ///
        /// <para>A-3: when <paramref name="quantizeMeters"/> &gt; 0, POINT labels are DEDUPED across tiles by
        /// their <see cref="CrossTileLabelKey"/> — the same symbol present in a parent + child tile during a
        /// zoom transition collapses to ONE (the finest tile zoom wins; ties broken by lowest tile key), so it
        /// is not double-drawn and its identity is stable across the swap. Line labels are passed through
        /// undeduped (per-anchor line identity is a follow-up). <paramref name="quantizeMeters"/> ≤ 0 disables
        /// dedup entirely (every active label is emitted, order-preserving — the pre-A-3 behaviour).</para></summary>
        public void CollectInto(List<LabelInstance> output, double quantizeMeters = 0.0)
        {
            output.Clear();
            if (quantizeMeters <= 0.0)
            {
                foreach (KeyValuePair<Key, Entry> kv in _active)
                    if (kv.Value.Labels != null) output.AddRange(kv.Value.Labels);
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

                    var key = CrossTileLabelKey.For(label.AnchorRender, label.MaterialIndex, label.Text, quantizeMeters);
                    int z = (int)(label.TileKey >> 44); // PackTileKey: z in the high bits (finest zoom wins)
                    if (!_dedup.TryGetValue(key, out DedupEntry cur)
                        || z > cur.Z || (z == cur.Z && label.TileKey < cur.TileKey))
                        _dedup[key] = new DedupEntry { Label = label, Z = z, TileKey = label.TileKey };
                }
            }
            foreach (KeyValuePair<CrossTileLabelKey, DedupEntry> kv in _dedup) output.Add(kv.Value.Label);
        }

        /// <summary>Drop everything (a restyle purges the mesh cache too).</summary>
        public void Clear()
        {
            _active.Clear();
            _cachedIndex.Clear();
            _cachedOrder.Clear();
            _version++; // B-1: a restyle purged everything
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
        public void ReconcileActiveSet(IReadOnlyList<Key> loaded, bool keepWarmOnRelease)
        {
            _loadedScratch.Clear();
            for (int i = 0; i < loaded.Count; i++) _loadedScratch.Add(loaded[i]);

            // Release actives that left the loaded set (collect first — cannot mutate _active while iterating).
            _reconcileRelease.Clear();
            foreach (KeyValuePair<Key, Entry> kv in _active)
                if (!_loadedScratch.Contains(kv.Key)) _reconcileRelease.Add(kv.Key);
            for (int i = 0; i < _reconcileRelease.Count; i++)
                Release(_reconcileRelease[i], keepWarmOnRelease);

            // Restore cached tiles that re-entered the loaded set (a cache hit — no rebuild is coming).
            _reconcileRestore.Clear();
            for (int i = 0; i < loaded.Count; i++)
                if (_cachedIndex.ContainsKey(loaded[i])) _reconcileRestore.Add(loaded[i]);
            for (int i = 0; i < _reconcileRestore.Count; i++)
                Restore(_reconcileRestore[i]);
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
