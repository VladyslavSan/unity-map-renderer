using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// The Burst gather (docs/symbol-label-perf-design.md): compacts each winner's <see cref="BlockView"/> slice
    /// into contiguous native mirror pools, remapping each <c>Detail</c>/<c>*Start</c> field by the pool offset.
    /// It runs through <c>.Run()</c> before <c>TickCore</c> reads any element. Pass 1 totals the pool sizes and
    /// resizes the caller's mirror lists once; pass 2 fills. <c>WritePerFrameMasks</c>, not this job, writes
    /// the per-frame masks (<c>SymbolDeparting</c>/<c>SymbolCoverageFading</c>/<c>SymbolDropped</c>).
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct SymbolGatherJob : IJob
    {
        // OutCounts indices — named so the caller and this job agree without bare literals.
        public const int CountPoint        = 0;
        public const int CountCurved       = 1;
        public const int CountQuad         = 2;
        public const int CountGlyph        = 3;
        public const int CountAnchor       = 4;
        public const int CountFade         = 5;
        public const int CountWorldPoint   = 6;
        public const int CountMaxBoxes     = 7;
        public const int CountMaxQuads     = 8;
        public const int CountMaxCandidates = 9;
        /// <summary><see cref="OutCounts"/>'s required length.</summary>
        public const int CountLength = 10;

        // ── inputs ──
        [ReadOnly] public NativeArray<BlockView> BlockViews; // one view per plan.Blocks[b], built by the caller
        [ReadOnly] public NativeArray<int> BlockId;                // plan.BlockId — winner r -> BlockViews index
        [ReadOnly] public NativeArray<int> LocalIndex;             // plan.LocalIndex — winner r -> raw record within that block
        public int WinnerCount;

        // ── outputs: the mirror pools (record order == winner order; NOT the three per-frame masks) ──
        public NativeList<SymbolPlacementKind> MKinds;
        public NativeList<int>  MDetail, MWorldCount, MWorldStart;
        public NativeList<double3> MRepAnchor;
        public NativeList<PointStageInput> MPoints;
        public NativeList<int> MPointQuadStart, MPointQuadCount;
        public NativeList<CurvedStageInput> MCurveds;
        public NativeList<int> MCurvedGlyphStart, MCurvedGlyphCount, MCurvedAnchorStart, MCurvedAnchorCount, MCurvedAnchorFadeStart;
        public NativeList<SymbolQuad>  MQuads;
        public NativeList<CurvedGlyph> MGlyphs;
        public NativeList<LineAnchor>  MAnchors;
        public NativeList<long>        MFadeIds;
        public NativeList<double3>     MWorldPoints;
        // Index-parallel to MWorldPoints (same MWorldStart/MWorldCount slice) — the unit surface normal at
        // each world point. Copied in lockstep with MWorldPoints below; no downstream reader consumes it yet.
        public NativeList<float3>      MWorldUps;

        // ── output counts (see the Count* consts above) ──
        public NativeArray<int> OutCounts;

        public void Execute()
        {
            int winners = WinnerCount;

            // Pass 1: total the per-pool sizes, so each mirror list is resized ONCE.
            int records = winners, points = 0, curveds = 0, quads = 0, glyphs = 0, anchors = 0, fades = 0, worlds = 0;
            for (int r = 0; r < winners; r++)
            {
                BlockView block = BlockViews[BlockId[r]];
                int li = LocalIndex[r];
                int detail = block.Detail[li];
                if (block.Kinds[li] == SymbolPlacementKind.Point)
                {
                    points++;
                    quads += block.PointQuadCount[detail];
                    worlds += block.WorldCount[li];
                }
                else
                {
                    curveds++;
                    glyphs += block.CurvedGlyphCount[detail];
                    int ac = block.CurvedAnchorCount[detail];
                    anchors += ac; fades += ac + 1; // + a trailing fallback slot (mirrors CurvedAnchorFadeStart)
                    worlds += block.WorldCount[li];
                }
            }

            // Resize (mirrors :1072-1084) — every list but the three per-frame masks.
            MKinds.ResizeUninitialized(records); MDetail.ResizeUninitialized(records);
            MWorldCount.ResizeUninitialized(records); MWorldStart.ResizeUninitialized(records);
            MRepAnchor.ResizeUninitialized(records);
            MPoints.ResizeUninitialized(points); MPointQuadStart.ResizeUninitialized(points); MPointQuadCount.ResizeUninitialized(points);
            MCurveds.ResizeUninitialized(curveds);
            MCurvedGlyphStart.ResizeUninitialized(curveds); MCurvedGlyphCount.ResizeUninitialized(curveds);
            MCurvedAnchorStart.ResizeUninitialized(curveds); MCurvedAnchorCount.ResizeUninitialized(curveds);
            MCurvedAnchorFadeStart.ResizeUninitialized(curveds);
            MQuads.ResizeUninitialized(quads); MGlyphs.ResizeUninitialized(glyphs);
            MAnchors.ResizeUninitialized(anchors); MFadeIds.ResizeUninitialized(fades);
            MWorldPoints.ResizeUninitialized(worlds);
            MWorldUps.ResizeUninitialized(worlds);

            NativeArray<SymbolQuad>  dstQuads   = MQuads.AsArray();
            NativeArray<CurvedGlyph> dstGlyphs  = MGlyphs.AsArray();
            NativeArray<LineAnchor>  dstAnchors = MAnchors.AsArray();
            NativeArray<long>        dstFades   = MFadeIds.AsArray();
            NativeArray<double3>     dstWorlds  = MWorldPoints.AsArray();
            NativeArray<float3>      dstWorldUps = MWorldUps.AsArray();

            int mPoint = 0, mCurved = 0, mQuad = 0, mGlyph = 0, mAnchor = 0, mFade = 0, mWorld = 0;
            int maxBoxes = 0, maxQuads = 0, maxCandidates = 0;

            // Pass 2 (mirrors :1092-1150): fill, remapping every Detail/*Start by the running pool offset.
            for (int r = 0; r < winners; r++)
            {
                BlockView block = BlockViews[BlockId[r]];
                int li = LocalIndex[r];
                int detail = block.Detail[li];
                int worldStartSrc = block.WorldStart[li], worldCount = block.WorldCount[li];
                int worldStart = mWorld;
                if (worldCount > 0)
                {
                    CopyView(block.WorldPoints, worldStartSrc, dstWorlds, mWorld, worldCount);
                    CopyView(block.WorldUps, worldStartSrc, dstWorldUps, mWorld, worldCount);
                }
                mWorld += worldCount;

                if (block.Kinds[li] == SymbolPlacementKind.Point)
                {
                    int quadStartSrc = block.PointQuadStart[detail], quadCount = block.PointQuadCount[detail];
                    int quadStart = mQuad;
                    if (quadCount > 0) CopyView(block.Quads, quadStartSrc, dstQuads, mQuad, quadCount);
                    mQuad += quadCount;

                    int slot = mPoint++;
                    MPoints[slot] = block.Points[detail];
                    MPointQuadStart[slot] = quadStart; MPointQuadCount[slot] = quadCount;

                    MKinds[r] = SymbolPlacementKind.Point; MDetail[r] = slot;
                    MWorldStart[r] = worldStart; MWorldCount[r] = worldCount; MRepAnchor[r] = block.RepAnchor[li];

                    maxBoxes += 1; maxQuads += quadCount; maxCandidates += 1; // mirrors SymbolBatch.AddPoint
                }
                else
                {
                    int glyphStartSrc = block.CurvedGlyphStart[detail], glyphCount = block.CurvedGlyphCount[detail];
                    int glyphStart = mGlyph;
                    if (glyphCount > 0) CopyView(block.Glyphs, glyphStartSrc, dstGlyphs, mGlyph, glyphCount);
                    mGlyph += glyphCount;

                    int anchorStartSrc = block.CurvedAnchorStart[detail], anchorCount = block.CurvedAnchorCount[detail];
                    int anchorStart = mAnchor;
                    if (anchorCount > 0) CopyView(block.Anchors, anchorStartSrc, dstAnchors, mAnchor, anchorCount);
                    mAnchor += anchorCount;

                    // No count > 0 guard here: fadeCount = anchorCount + 1 >= 1 always.
                    int fadeStartSrc = block.CurvedAnchorFadeStart[detail], fadeCount = anchorCount + 1;
                    int fadeStart = mFade;
                    CopyView(block.AnchorFadeIds, fadeStartSrc, dstFades, mFade, fadeCount);
                    mFade += fadeCount;

                    int slot = mCurved++;
                    MCurveds[slot] = block.Curveds[detail];
                    MCurvedGlyphStart[slot] = glyphStart; MCurvedGlyphCount[slot] = glyphCount;
                    MCurvedAnchorStart[slot] = anchorStart; MCurvedAnchorCount[slot] = anchorCount;
                    MCurvedAnchorFadeStart[slot] = fadeStart;

                    MKinds[r] = SymbolPlacementKind.Curved; MDetail[r] = slot;
                    MWorldStart[r] = worldStart; MWorldCount[r] = worldCount; MRepAnchor[r] = block.RepAnchor[li];

                    int placements = anchorCount + 1; // mirrors SymbolBatch.AddCurved
                    maxBoxes += placements * glyphCount; maxQuads += placements * glyphCount; maxCandidates += placements;
                }
            }

            OutCounts[CountPoint] = mPoint; OutCounts[CountCurved] = mCurved;
            OutCounts[CountQuad] = mQuad; OutCounts[CountGlyph] = mGlyph; OutCounts[CountAnchor] = mAnchor;
            OutCounts[CountFade] = mFade; OutCounts[CountWorldPoint] = mWorld;
            OutCounts[CountMaxBoxes] = maxBoxes; OutCounts[CountMaxQuads] = maxQuads; OutCounts[CountMaxCandidates] = maxCandidates;
        }

        // NativeArray<T>.Copy over a non-owning UnsafeList view, element-wise, with the same contract.
        // Non-obvious why: `src` is by value, because UnsafeList<T> is not a readonly struct and `in` would
        // force a defensive copy per element.
        private static void CopyView<T>(UnsafeList<T> src, int srcStart, NativeArray<T> dst, int dstStart, int count)
            where T : unmanaged
        {
            for (int i = 0; i < count; i++) dst[dstStart + i] = src[srcStart + i];
        }
    }
}
