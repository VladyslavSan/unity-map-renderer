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
    /// <para>Outputs are pre-sized by the caller to the batch's worst case (<c>MaxBoxes/MaxQuads/MaxCandidates</c>)
    /// and declared as fixed-length <see cref="NativeArray{T}"/>s, which genuinely cannot grow mid-run — this job's
    /// caller doesn't know the exact per-record counts up front either, so it sizes to the worst case instead.
    /// (Contrast <see cref="SymbolGatherJob"/>, one stage upstream of this one: its OWN pass 1 computes the
    /// EXACT per-pool sizes before pass 2 needs them, so its outputs are <c>NativeList{T}</c>s resized once,
    /// in-job — a single-shot, bounded resize, not per-element growth. Not a contradiction between the two jobs;
    /// each picked the container that fits what its caller can size.) The dynamic per-frame values (projected
    /// screen/depth/valid) arrive as native arrays resolved on the main thread before the job; A-5 incumbency (R2)
    /// is resolved HERE instead, against the caller's <see cref="Placed"/> set — the point arm inline, the curved
    /// arm into the <see cref="AnchorWasPlaced"/> scratch this job fills.</para>
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
        // Stage AC (curved-world): the SAME gathered world polyline Screen was projected FROM — index-aligned
        // 1:1 with Screen/Depth/Valid (LabelPlacementSystem._symbolPoints). The curved arm slices it at the
        // SAME (off, wc) as the screen path so a glyph's world anchor/tangent sample the identical (segment, t)
        // the screen arc walk resolves. Point arm never reads this.
        public NativeArray<double3> WorldPointsRender;
        // A-5 incumbency per global anchor-fade index — JOB-OWNED scratch: filled here (below) from
        // AnchorFadeIds + Placed, not resolved by the caller. Sized by the caller to _mFadeCount.
        public NativeArray<byte>   AnchorWasPlaced;
        public float   Bearing;
        public double2 Viewport;

        // A-5 incumbency: last frame's collision survivors, keyed by fade id. Read from Burst — the reason
        // LabelPlacementSystem._placedLastFrame is a NativeHashSet at all. NEVER stored across frames by the
        // caller (see LabelPlacementSystem.RunStageJob).
        public NativeHashSet<long>.ReadOnly Placed;

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

            // A-5 (R2): resolve every anchor fade id against the placed-set HERE, in Burst, instead of on the main
            // thread. Whole-range fill (not per-record): the culled records' entries are then defined, and this is
            // byte-identical to the managed loop it replaces.
            for (int i = 0; i < AnchorWasPlaced.Length; i++)
                AnchorWasPlaced[i] = (byte)(Placed.Contains(AnchorFadeIds[i]) ? 1 : 0);

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
                    s.WasPlacedLastFrame = Placed.Contains(s.FadeId); // s is the Points[d] copy; FadeId is never patched

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

                    ReadOnlySpan<float2>  screen = Screen.AsSpan().Slice(off, wc);
                    ReadOnlySpan<float>   depth  = Depth.AsSpan().Slice(off, wc);
                    ReadOnlySpan<byte>    valid  = Valid.AsSpan().Slice(off, wc);
                    ReadOnlySpan<double3> world  = WorldPointsRender.AsSpan().Slice(off, wc);
                    ReadOnlySpan<CurvedGlyph> glyphs  = Glyphs.AsSpan().Slice(CurvedGlyphStart[d], CurvedGlyphCount[d]);
                    ReadOnlySpan<LineAnchor>  anchors = Anchors.AsSpan().Slice(CurvedAnchorStart[d], anchorCount);
                    ReadOnlySpan<long> fadeIds   = AnchorFadeIds.AsSpan().Slice(fadeStart, anchorCount + 1);
                    ReadOnlySpan<byte> wasPlaced = AnchorWasPlaced.AsSpan().Slice(fadeStart, anchorCount + 1);

                    candidateCount += LabelStagingMath.StageCurved(in s, screen, depth, valid, world, glyphs, anchors,
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
