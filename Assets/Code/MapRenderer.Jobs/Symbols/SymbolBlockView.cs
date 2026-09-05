using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// A blittable, non-owning VIEW over one <c>SymbolTileBlock</c>'s 20 <c>Allocator.Persistent</c>
    /// arrays — indexable from a Burst job without a managed call.
    ///
    /// <para><b>Lifetime — read this before touching a view.</b> Every <see cref="UnsafeList{T}"/> field is
    /// built from a raw pointer (<c>UnsafeList(T* ptr, int length)</c>, no allocator) — it owns nothing and
    /// <c>Dispose()</c> on it is a no-op. A view is valid only for the duration of the SYNCHRONOUS gather that
    /// built it (<c>SymbolPlacementSystem.BuildBlockViews</c> + the immediately following
    /// <c>SymbolGatherJob.Run()</c>) — it must never be stored across a frame boundary.</para>
    ///
    /// <para>Field order mirrors <c>SymbolTileBlock.cs:46-78</c> so the two read side by side. Per-block
    /// pool COUNTS (<c>PointCount</c>/<c>CurvedCount</c>/<c>QuadCount</c>/… ) are deliberately omitted — the
    /// gather only ever indexes a block's arrays by a winner's own <c>LocalIndex</c>/<c>Detail</c>/<c>*Start</c>,
    /// never by a block-level pool count.</para>
    /// </summary>
    public struct SymbolBlockView
    {
        // ── per-symbol records, RAW list order ──
        public UnsafeList<SymbolPlacementKind> Kinds;
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
        // P2: index-parallel to WorldPoints (same WorldStart/WorldCount slice) — the unit surface normal at
        // each world point. Written by the gather copy; not yet consumed by any downstream Burst reader.
        public UnsafeList<float3>      WorldUps;
        public UnsafeList<long>        AnchorFadeIds;
    }
}
