// Engine-free: no UnityEngine dependency (lives in MapRenderer.Core.Text.Placement, compiled by
// Tools/core-tests too). Plain managed collections only.

using System.Collections.Generic;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A per-style string→int interning table, so the cross-tile dedup key partitions by an integer text/icon
    /// id instead of hashing a <c>string</c> per frame. Ordinal keys and a monotonic counter from 1 (0 = null)
    /// make id equality match ordinal string equality, as <c>CrossTileSymbolKey.Equals</c> requires.
    /// Non-local invariant: the store owns it and resets it at <c>Clear</c> (SetStyle); only
    /// <c>StyledSymbolTileBuilder.Shape</c> fills it, on the main thread, and the per-frame
    /// <c>CollectInto</c> reads only frozen id arrays, so no lock is needed. A restyle renumbers.
    /// </summary>
    public sealed class SymbolStringTable
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
