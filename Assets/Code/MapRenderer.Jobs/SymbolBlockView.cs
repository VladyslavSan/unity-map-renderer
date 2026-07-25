using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst-gather Stage 1 (docs/symbol-label-perf-design.md §10.9): a blittable, non-owning VIEW over one
    /// <c>SymbolTileLabelBlock</c>'s 19 <c>Allocator.Persistent</c> arrays (<c>MapRenderer.Unity.Text.Placement</c>
    /// — that type is engine-Unity-side and can't itself be referenced from a Burst job in this Jobs-only
    /// assembly), so <see cref="SymbolGatherJob"/> can index a block's fields without a managed call.
    ///
    /// <para><b>Lifetime — read this before touching a view.</b> Every <see cref="UnsafeList{T}"/> field is
    /// built from a raw pointer (<c>UnsafeList(T* ptr, int length)</c>, no allocator) — it owns nothing and
    /// <c>Dispose()</c> on it is a no-op. A view is valid only for the duration of the SYNCHRONOUS gather that
    /// built it (<c>LabelPlacementSystem.BuildBlockViews</c> + the immediately following
    /// <c>SymbolGatherJob.Run()</c>) — it must never be stored across a frame boundary. Stage 2 (the async
    /// gather) changes this: at that point the block a view points into can be disposed while the view is
    /// still live unless the owning snapshot is pinned independently of the front pin (design doc §2.4) — that
    /// obligation is Stage 2's, not this struct's; this struct only carries the shape.</para>
    ///
    /// <para>Field order mirrors <c>SymbolTileLabelBlock.cs:46-78</c> so the two read side by side. Per-block
    /// pool COUNTS (<c>PointCount</c>/<c>CurvedCount</c>/<c>QuadCount</c>/… ) are deliberately omitted — the
    /// gather only ever indexes a block's arrays by a winner's own <c>LocalIndex</c>/<c>Detail</c>/<c>*Start</c>,
    /// never by a block-level pool count.</para>
    /// </summary>
    public struct SymbolBlockView
    {
        // ── per-label records, RAW list order ──
        public UnsafeList<byte>    Kinds;
        public UnsafeList<int>     Detail;
        public UnsafeList<int>     WorldStart;
        public UnsafeList<int>     WorldCount;
        public UnsafeList<double3> RepAnchor;

        // ── point details ──
        public UnsafeList<PointStageInput> Points;
        public UnsafeList<int> PointQuadStart;
        public UnsafeList<int> PointQuadCount;

        // ── curved details ──
        public UnsafeList<CurvedStageInput> Curveds;
        public UnsafeList<int> CurvedGlyphStart;
        public UnsafeList<int> CurvedGlyphCount;
        public UnsafeList<int> CurvedAnchorStart;
        public UnsafeList<int> CurvedAnchorCount;
        public UnsafeList<int> CurvedAnchorFadeStart;

        // ── flat pools ──
        public UnsafeList<SymbolQuad>  Quads;
        public UnsafeList<CurvedGlyph> Glyphs;
        public UnsafeList<LineAnchor>  Anchors;
        public UnsafeList<double3>     WorldPoints;
        public UnsafeList<long>        AnchorFadeIds;
    }
}
