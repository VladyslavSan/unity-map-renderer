// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Symbol-symbol perf Phase 1 / Stage 1 (design §4, §5 B): one build's per-symbol placement input, held as
    /// a REUSED buffer instead of a fresh managed carrier list per tile — <see cref="Symbols"/>
    /// (one <see cref="ShapedSymbol"/> per successfully-shaped symbol; the list is dense, since a per-symbol build
    /// failure is skipped rather than recorded)
    /// plus the pooled variable-length sub-lists every symbol's spans index into: <see cref="Quads"/> (point
    /// text/icon), <see cref="Glyphs"/> (curved text/icon), <see cref="Anchors"/> (curved along-line anchors),
    /// <see cref="Path"/>/<see cref="PathUp"/> (curved world path, index-parallel).
    ///
    /// <para><b>Lifetime — one instance per IN-FLIGHT build, not a shared field.</b> Builds interleave on the
    /// main thread (<c>SymbolSubsystem</c>'s tail pump awaits glyph fetches), so a single reused instance
    /// shared across concurrent builds would corrupt both — this type is deliberately NOT a singleton;
    /// <c>SymbolSubsystem</c> pools whole instances per build, <see cref="Clear"/>ing and returning one
    /// only once its bake has consumed it.</para>
    ///
    /// <para><b>Pairing adjacency.</b> One build's ENTIRE symbol set — every layer's contribution — must land in
    /// the SAME <see cref="SymbolTileBuffer"/>, in emission order, or <see cref="SymbolPairing"/>'s
    /// owner-at-<c>i+1</c> resolution breaks.</para>
    /// </summary>
    public sealed class SymbolTileBuffer
    {
        /// <summary>One symbol per raw symbol slot, in build emission order — <c>SymbolTileBlockBaker</c>
        /// (Unity assembly) walks this 1:1 into the block's raw-order columns.</summary>
        public List<ShapedSymbol> Symbols { get; } = new List<ShapedSymbol>();

        /// <summary>Pooled point-text/point-icon quads — a symbol's <see cref="ShapedSymbol.QuadStart"/>/
        /// <see cref="ShapedSymbol.QuadCount"/> span indexes here.</summary>
        public List<SymbolQuad> Quads { get; } = new List<SymbolQuad>();

        /// <summary>Pooled curved-text/curved-icon glyph cells — a symbol's <see cref="ShapedSymbol.GlyphStart"/>/
        /// <see cref="ShapedSymbol.GlyphCount"/> span indexes here.</summary>
        public List<CurvedGlyph> Glyphs { get; } = new List<CurvedGlyph>();

        /// <summary>Pooled curved along-line anchors (build-time topology, A-2) — a symbol's
        /// <see cref="ShapedSymbol.AnchorStart"/>/<see cref="ShapedSymbol.AnchorCount"/> span indexes here.
        /// UNCLAMPED (the baker's own <c>SymbolStagingMath.MaxAnchorsPerLine</c> clamp is applied at bake time,
        /// exactly as it was applied over the raw per-symbol carrier's <c>LineAnchors</c> array before).</summary>
        public List<LineAnchor> Anchors { get; } = new List<LineAnchor>();

        /// <summary>Pooled curved world (pre-RTC render-space) path vertices — a symbol's
        /// <see cref="ShapedSymbol.PathStart"/>/<see cref="ShapedSymbol.PathCount"/> span indexes here.
        /// Index-parallel with <see cref="PathUp"/>.</summary>
        public List<double3> Path { get; } = new List<double3>();

        /// <summary>Pooled curved world path up-vectors, index-parallel with <see cref="Path"/> — padded with
        /// <see cref="double3.zero"/> for any index a caller's own up-array left short; see <see cref="AppendPath"/>.</summary>
        public List<double3> PathUp { get; } = new List<double3>();

        /// <summary>Rewinds every list to empty WITHOUT releasing backing capacity, so a pooled instance's
        /// second build reuses the same arrays (the 3b zero-alloc win) instead of re-allocating them.</summary>
        public void Clear()
        {
            Symbols.Clear();
            Quads.Clear();
            Glyphs.Clear();
            Anchors.Clear();
            Path.Clear();
            PathUp.Clear();
        }

        /// <summary>Appends one raw symbol slot. Callers append this symbol's quad/glyph/anchor/path spans into
        /// the pools ABOVE first, so the symbol's <c>*Start</c> fields are already resolved.</summary>
        public void AddSymbol(in ShapedSymbol symbol) => Symbols.Add(symbol);

        /// <summary>Appends <paramref name="quads"/> (may be null) onto <see cref="Quads"/>; returns the span
        /// a symbol's <see cref="ShapedSymbol.QuadStart"/>/<see cref="ShapedSymbol.QuadCount"/> should carry.</summary>
        public int AppendQuads(IReadOnlyList<SymbolQuad> quads, out int count)
        {
            int start = Quads.Count;
            count = quads?.Count ?? 0;
            for (int i = 0; i < count; i++) Quads.Add(quads[i]);
            return start;
        }

        /// <summary>Appends <paramref name="glyphs"/> (may be null) onto <see cref="Glyphs"/>; returns the span
        /// a symbol's <see cref="ShapedSymbol.GlyphStart"/>/<see cref="ShapedSymbol.GlyphCount"/> should carry.</summary>
        public int AppendGlyphs(IReadOnlyList<CurvedGlyph> glyphs, out int count)
        {
            int start = Glyphs.Count;
            count = glyphs?.Count ?? 0;
            for (int i = 0; i < count; i++) Glyphs.Add(glyphs[i]);
            return start;
        }

        /// <summary>Appends <paramref name="anchors"/> (may be null) onto <see cref="Anchors"/>, UNCLAMPED (the
        /// baker applies <c>SymbolStagingMath.MaxAnchorsPerLine</c> at bake time); returns the span a symbol's
        /// <see cref="ShapedSymbol.AnchorStart"/>/<see cref="ShapedSymbol.AnchorCount"/> should carry.</summary>
        public int AppendAnchors(LineAnchor[] anchors, out int count)
        {
            int start = Anchors.Count;
            count = anchors?.Length ?? 0;
            for (int i = 0; i < count; i++) Anchors.Add(anchors[i]);
            return start;
        }

        /// <summary>Appends <paramref name="path"/> (may be null) onto <see cref="Path"/> and a length-matched
        /// <paramref name="pathUp"/> onto <see cref="PathUp"/>, padding any short/missing up-vector with
        /// <see cref="double3.zero"/> (a bug signal downstream, not a supported state) so the two pools stay
        /// index-parallel. Returns the
        /// span a symbol's <see cref="ShapedSymbol.PathStart"/>/<see cref="ShapedSymbol.PathCount"/> should
        /// carry.</summary>
        public int AppendPath(double3[] path, double3[] pathUp, out int count)
        {
            int start = Path.Count;
            count = path?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                Path.Add(path[i]);
                PathUp.Add(pathUp != null && i < pathUp.Length ? pathUp[i] : double3.zero);
            }
            return start;
        }
    }
}
