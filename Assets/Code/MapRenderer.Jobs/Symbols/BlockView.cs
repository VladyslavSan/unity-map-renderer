using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// A blittable, non-owning VIEW over one <c>SymbolTileBlock</c>'s 20 <c>Allocator.Persistent</c>
    /// arrays — indexable from a Burst job without a managed call, in <c>SymbolTileBlock</c>'s field order.
    /// Non-local invariant: each field wraps a raw pointer and owns nothing, so a view is valid only during
    /// the synchronous gather that built it (<c>SymbolPlacementSystem.BuildBlockViews</c> + the following
    /// <c>SymbolGatherJob.Run()</c>) and must never be stored across a frame boundary.
    /// </summary>
    public struct BlockView
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
        // Index-parallel to WorldPoints (same WorldStart/WorldCount slice) — the unit surface normal at
        // each world point. Written by the gather copy; no downstream Burst reader consumes it yet.
        public UnsafeList<float3>      WorldUps;
        public UnsafeList<long>        AnchorFadeIds;
    }
}
