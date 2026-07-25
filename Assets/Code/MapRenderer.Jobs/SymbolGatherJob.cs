using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Burst-gather Stage 1 (docs/symbol-label-perf-design.md §10.9): a LINE-FOR-LINE Burst transliteration of
    /// <c>LabelPlacementSystem.GatherIntoMirror</c>'s two managed loops (<c>LabelPlacementSystem.cs:1050-1157</c>)
    /// — compact every winner's pre-baked <see cref="SymbolBlockView"/> slice into one contiguous set of native
    /// mirror pools, remapping every <c>Detail</c>/<c>*Start</c> field by the running pool offset. Run
    /// SYNCHRONOUSLY (<c>.Run()</c>) inside <c>PmGather</c>, strictly before <c>TickCore</c> reads a single
    /// element — no double-buffering, no swap, no resize under a reader (unchanged from the managed gather).
    ///
    /// <para><b>Byte-identical, by construction.</b> Every value written is either an integer running offset or
    /// a struct copied element-for-element from a block's own array — no new floating-point arithmetic is
    /// introduced anywhere. <c>SymbolGatherParityTests.Gather_MatchesBuildOracle_FieldByField</c> is the teeth.</para>
    ///
    /// <para>Resizes the caller's <c>Allocator.Persistent</c> mirror <see cref="NativeList{T}"/> outputs itself
    /// (pass 1 totals the per-pool sizes; pass 2 fills) — the same "grow an externally-owned persistent list
    /// from inside a Burst <c>IJob</c>" shape as <c>GlobeFillSubdivideJob{TProj}.Execute</c>'s <c>OutVerts.Add</c>
    /// (<c>GlobeFillSubdivider.cs:172-173</c>, list allocated by <c>StyledFillTileBuilder.cs:268</c>) — established,
    /// shipped practice in this codebase; not the <see cref="LabelStageJob"/> shape (that job's outputs are
    /// PRE-sized by the caller because ITS caller doesn't know the counts either — here pass 1 computes them
    /// before pass 2 needs the resize, so growth is bounded and single-shot, not per-element).</para>
    ///
    /// <para>Does NOT write the three per-record masks (<c>RecordDeparting</c>/<c>RecordCoverageFading</c>/
    /// <c>RecordDropped</c>) — those are the per-frame overrides <c>WritePerFrameMasks</c> rewrites every Tick
    /// (memo hit or not) and are resized/filled by the caller, outside this job.</para>
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
        [ReadOnly] public NativeArray<SymbolBlockView> BlockViews; // one view per plan.Blocks[b], built by the caller
        [ReadOnly] public NativeArray<int> BlockId;                // plan.BlockId — winner r -> BlockViews index
        [ReadOnly] public NativeArray<int> LocalIndex;             // plan.LocalIndex — winner r -> raw record within that block
        public int WinnerCount;

        // ── outputs: the mirror pools (record order == winner order; NOT the three per-frame masks) ──
        public NativeList<byte> MKinds;
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

        // ── output counts (see the Count* consts above) ──
        public NativeArray<int> OutCounts;

        public void Execute()
        {
            int winners = WinnerCount;

            // Pass 1 (mirrors LabelPlacementSystem.cs:1050-1070): total per-pool sizes, so each mirror list is
            // resized ONCE.
            int records = winners, points = 0, curveds = 0, quads = 0, glyphs = 0, anchors = 0, fades = 0, worlds = 0;
            for (int r = 0; r < winners; r++)
            {
                SymbolBlockView block = BlockViews[BlockId[r]];
                int li = LocalIndex[r];
                int detail = block.Detail[li];
                if (block.Kinds[li] == (byte)SymbolLabelBatch.Kind.Point)
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

            NativeArray<SymbolQuad>  dstQuads   = MQuads.AsArray();
            NativeArray<CurvedGlyph> dstGlyphs  = MGlyphs.AsArray();
            NativeArray<LineAnchor>  dstAnchors = MAnchors.AsArray();
            NativeArray<long>        dstFades   = MFadeIds.AsArray();
            NativeArray<double3>     dstWorlds  = MWorldPoints.AsArray();

            int mPoint = 0, mCurved = 0, mQuad = 0, mGlyph = 0, mAnchor = 0, mFade = 0, mWorld = 0;
            int maxBoxes = 0, maxQuads = 0, maxCandidates = 0;

            // Pass 2 (mirrors :1092-1150): fill, remapping every Detail/*Start by the running pool offset.
            for (int r = 0; r < winners; r++)
            {
                SymbolBlockView block = BlockViews[BlockId[r]];
                int li = LocalIndex[r];
                int detail = block.Detail[li];
                int worldStartSrc = block.WorldStart[li], worldCount = block.WorldCount[li];
                int worldStart = mWorld;
                if (worldCount > 0) CopyView(block.WorldPoints, worldStartSrc, dstWorlds, mWorld, worldCount);
                mWorld += worldCount;

                if (block.Kinds[li] == (byte)SymbolLabelBatch.Kind.Point)
                {
                    int quadStartSrc = block.PointQuadStart[detail], quadCount = block.PointQuadCount[detail];
                    int quadStart = mQuad;
                    if (quadCount > 0) CopyView(block.Quads, quadStartSrc, dstQuads, mQuad, quadCount);
                    mQuad += quadCount;

                    int slot = mPoint++;
                    MPoints[slot] = block.Points[detail];
                    MPointQuadStart[slot] = quadStart; MPointQuadCount[slot] = quadCount;

                    MKinds[r] = (byte)SymbolLabelBatch.Kind.Point; MDetail[r] = slot;
                    MWorldStart[r] = worldStart; MWorldCount[r] = worldCount; MRepAnchor[r] = block.RepAnchor[li];

                    maxBoxes += 1; maxQuads += quadCount; maxCandidates += 1; // mirrors SymbolLabelBatch.AddPoint
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

                    // No count > 0 guard here, deliberately (mirrors :1135): fadeCount = anchorCount + 1 >= 1 always.
                    int fadeStartSrc = block.CurvedAnchorFadeStart[detail], fadeCount = anchorCount + 1;
                    int fadeStart = mFade;
                    CopyView(block.AnchorFadeIds, fadeStartSrc, dstFades, mFade, fadeCount);
                    mFade += fadeCount;

                    int slot = mCurved++;
                    MCurveds[slot] = block.Curveds[detail];
                    MCurvedGlyphStart[slot] = glyphStart; MCurvedGlyphCount[slot] = glyphCount;
                    MCurvedAnchorStart[slot] = anchorStart; MCurvedAnchorCount[slot] = anchorCount;
                    MCurvedAnchorFadeStart[slot] = fadeStart;

                    MKinds[r] = (byte)SymbolLabelBatch.Kind.Curved; MDetail[r] = slot;
                    MWorldStart[r] = worldStart; MWorldCount[r] = worldCount; MRepAnchor[r] = block.RepAnchor[li];

                    int placements = anchorCount + 1; // mirrors SymbolLabelBatch.AddCurved
                    maxBoxes += placements * glyphCount; maxQuads += placements * glyphCount; maxCandidates += placements;
                }
            }

            OutCounts[CountPoint] = mPoint; OutCounts[CountCurved] = mCurved;
            OutCounts[CountQuad] = mQuad; OutCounts[CountGlyph] = mGlyph; OutCounts[CountAnchor] = mAnchor;
            OutCounts[CountFade] = mFade; OutCounts[CountWorldPoint] = mWorld;
            OutCounts[CountMaxBoxes] = maxBoxes; OutCounts[CountMaxQuads] = maxQuads; OutCounts[CountMaxCandidates] = maxCandidates;
        }

        // Element-wise stand-in for NativeArray<T>.Copy over a non-owning UnsafeList view (a view has no
        // NativeArray to hand NativeArray<T>.Copy) — same source/dest/start/count contract, called only where
        // the caller already checked count > 0 (Quads/Glyphs/Anchors/WorldPoints) or where the source is known
        // non-empty by construction (AnchorFadeIds' fadeCount = anchorCount + 1 >= 1, no guard — see call site).
        // By VALUE, not `in`: UnsafeList<T> is not a readonly struct and its `this[int]` getter isn't
        // readonly-annotated, so `in` would force a defensive copy per element in this hot per-winner loop —
        // the repo's `in ⟺ readonly struct` gate (docs/conventions-short.md).
        private static void CopyView<T>(UnsafeList<T> src, int srcStart, NativeArray<T> dst, int dstStart, int count)
            where T : unmanaged
        {
            for (int i = 0; i < count; i++) dst[dstStart + i] = src[srcStart + i];
        }
    }
}
