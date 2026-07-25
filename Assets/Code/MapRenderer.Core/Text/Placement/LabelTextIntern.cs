// Engine-free: no UnityEngine dependency (lives in MapRenderer.Core.Text.Placement, compiled by
// Tools/core-tests too). Plain managed collections only.

using System.Collections.Generic;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Stage 2 (labels-async-reconcile): a per-style string→int interning table so the cross-tile dedup key
    /// (<see cref="MapRenderer.Unity.Text.SymbolTileLabelStore"/>'s <c>DedupKey</c>) can partition labels by an
    /// integer text/icon id instead of the label's <c>string</c> — killing the per-frame
    /// <c>string.GetHashCode</c> on the dedup hot path (<c>CollectInto</c>).
    ///
    /// <para><b>Bijection within a lifetime.</b> Backed by a <see cref="System.StringComparer.Ordinal"/>
    /// <see cref="Dictionary{TKey,TValue}"/> with a monotonic counter from <c>1</c> (<c>0</c> reserved for
    /// <c>null</c>): a new string always takes a fresh id, an existing string always returns its stored id, and
    /// ORDINAL comparison ⇒ <c>id-equality ⟺ ordinal-string-equality</c> — exactly matching
    /// <c>CrossTileLabelKey.Equals</c>'s <c>Text == other.Text</c> ordinal semantics. This bijection is the
    /// property the dedup partition-preservation rests on (different string ⇒ different id).</para>
    ///
    /// <para><b>Lifetime &amp; threading.</b> Owned by the store, populated ONLY in
    /// <c>CompleteBuild</c> (main thread), <see cref="Reset"/> at the store's <c>Clear</c> (the SetStyle
    /// boundary). Ids are stable for a style's whole life and never reused within it; a restyle renumbers
    /// (harmless — ids feed only equality/hashing, never a snapshot). The per-frame <c>CollectInto</c> reads
    /// only the frozen per-entry id arrays, never this table, so it needs no locking.</para>
    /// </summary>
    public sealed class LabelTextIntern
    {
        private readonly Dictionary<string, int> _ids = new Dictionary<string, int>(System.StringComparer.Ordinal);
        private int _next = 1; // 0 reserved for null (a null Text/IconImage is inert, never a winner)

        /// <summary>Distinct interned strings so far — test-only (Core grants InternalsVisibleTo).</summary>
        internal int Count => _ids.Count;

        /// <summary>The stable id for <paramref name="s"/> within this table's lifetime: <c>0</c> for
        /// <c>null</c>, else an idempotent monotonic id (same string ⇒ same id, new string ⇒ fresh id).</summary>
        public int Intern(string s)
        {
            if (s == null) return 0;
            if (_ids.TryGetValue(s, out int id)) return id;
            id = _next++;
            _ids[s] = id;
            return id;
        }

        /// <summary>Drop every mapping and restart ids at <c>1</c> (the SetStyle-boundary reset). A string
        /// interned after a reset gets a fresh id from <c>1</c> again.</summary>
        public void Reset()
        {
            _ids.Clear();
            _next = 1;
        }
    }
}
