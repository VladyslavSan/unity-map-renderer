using System;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Lever C step 3b: the per-frame label STAGING loop run as one Burst <see cref="IJob"/> over native mirrors of
    /// the <c>SymbolLabelBatch</c> SoA. It calls the SAME <see cref="LabelStagingMath"/> functions the managed path
    /// does — over <c>NativeArray.AsSpan()</c> slices — so it is byte-identical (the full EditMode gate is the
    /// teeth); Burst just SIMD-compiles the transcendental-heavy per-glyph geometry and drops the managed-call
    /// overhead. ONE job, not a fan-out: the candidate ordinal is assigned in record order (each record's
    /// <c>candidateCount</c> depends on all prior), so the loop is inherently serial — like <see cref="LabelCollisionJob"/>.
    ///
    /// <para>Outputs are pre-sized by the caller to the batch's worst case (<c>MaxBoxes/MaxQuads/MaxCandidates</c>) —
    /// Burst cannot grow a container mid-run. The dynamic per-frame values (projected screen/depth/valid, last-frame
    /// incumbency) arrive as native arrays resolved on the main thread before the job.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct LabelStageJob : IJob
    {
        // ── batch mirror (per-label records, in collected order) ──
        public NativeArray<byte> Kinds;       // 0 = point, 1 = curved
        public NativeArray<int>  Detail;      // index into Points / Curveds
        public NativeArray<int>  WorldCount;  // path length (curved) — the projected-path slice length
        public int Count;

        // ── point details ──
        public NativeArray<PointStageInput> Points;         // stable fields; dynamic patched per record below
        public NativeArray<int> PointQuadStart, PointQuadCount;

        // ── curved details ──
        public NativeArray<CurvedStageInput> Curveds;
        public NativeArray<int> CurvedGlyphStart, CurvedGlyphCount;
        public NativeArray<int> CurvedAnchorStart, CurvedAnchorCount, CurvedAnchorFadeStart;

        // ── flat pools ──
        public NativeArray<SymbolQuad>  Quads;
        public NativeArray<CurvedGlyph> Glyphs;
        public NativeArray<LineAnchor>  Anchors;
        public NativeArray<long>        AnchorFadeIds;

        // ── per-frame ──
        public NativeArray<int>    PointOffset;      // gather output; -1 = culled → skipped
        public NativeArray<float2> Screen;
        public NativeArray<float>  Depth;
        public NativeArray<byte>   Valid;
        public NativeArray<byte>   PointWasPlaced;   // A-5 incumbency per point detail
        public NativeArray<byte>   AnchorWasPlaced;  // A-5 incumbency per global anchor-fade index
        public float   Bearing;
        public double2 Viewport;

        // ── caller-owned scratch (>= max WorldCount) ──
        public NativeArray<float2> PathScratch;
        public NativeArray<float>  CumScratch;

        // ── outputs (pre-sized to the batch worst case) + counts ──
        public NativeArray<LabelBox>       Boxes;
        public NativeArray<PlacedQuad>     StagedQuads;
        public NativeArray<LabelCandidate> Candidates;
        public NativeArray<CandidateEmit>  Emit;
        public NativeArray<int>            OutCounts; // [0]=candidateCount [1]=boxCount [2]=quadCount

        public void Execute()
        {
            Span<LabelBox>       boxes = Boxes.AsSpan();
            Span<PlacedQuad>     quads = StagedQuads.AsSpan();
            Span<LabelCandidate> cands = Candidates.AsSpan();
            Span<CandidateEmit>  emit  = Emit.AsSpan();
            Span<float2>         path  = PathScratch.AsSpan();
            Span<float>          cum   = CumScratch.AsSpan();

            int candidateCount = 0, boxCount = 0, quadCount = 0;
            for (int r = 0; r < Count; r++)
            {
                int off = PointOffset[r];
                if (off < 0) continue; // B-3-distance-culled
                int d = Detail[r];

                if (Kinds[r] == 0)
                {
                    PointStageInput s = Points[d];
                    s.ScreenPx = Screen[off];
                    s.Depth = Depth[off];
                    s.Projected = Valid[off] != 0;
                    s.WasPlacedLastFrame = PointWasPlaced[d] != 0;

                    ReadOnlySpan<SymbolQuad> quadSpan = Quads.AsSpan().Slice(PointQuadStart[d], PointQuadCount[d]);
                    candidateCount += LabelStagingMath.StagePoint(in s, quadSpan, Bearing, Viewport, candidateCount,
                        boxes, ref boxCount, quads, ref quadCount, cands, emit);
                }
                else
                {
                    int wc = WorldCount[r];
                    int anchorCount = CurvedAnchorCount[d];
                    int fadeStart   = CurvedAnchorFadeStart[d];
                    CurvedStageInput s = Curveds[d];

                    ReadOnlySpan<float2> screen = Screen.AsSpan().Slice(off, wc);
                    ReadOnlySpan<float>  depth  = Depth.AsSpan().Slice(off, wc);
                    ReadOnlySpan<byte>   valid  = Valid.AsSpan().Slice(off, wc);
                    ReadOnlySpan<CurvedGlyph> glyphs  = Glyphs.AsSpan().Slice(CurvedGlyphStart[d], CurvedGlyphCount[d]);
                    ReadOnlySpan<LineAnchor>  anchors = Anchors.AsSpan().Slice(CurvedAnchorStart[d], anchorCount);
                    ReadOnlySpan<long> fadeIds   = AnchorFadeIds.AsSpan().Slice(fadeStart, anchorCount + 1);
                    ReadOnlySpan<byte> wasPlaced = AnchorWasPlaced.AsSpan().Slice(fadeStart, anchorCount + 1);

                    candidateCount += LabelStagingMath.StageCurved(in s, screen, depth, valid, glyphs, anchors,
                        fadeIds, wasPlaced, path.Slice(0, wc), cum.Slice(0, wc), Bearing, candidateCount,
                        boxes, ref boxCount, quads, ref quadCount, cands, emit);
                }
            }

            OutCounts[0] = candidateCount;
            OutCounts[1] = boxCount;
            OutCounts[2] = quadCount;
        }
    }
}
