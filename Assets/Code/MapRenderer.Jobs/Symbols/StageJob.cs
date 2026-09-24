using System;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// The per-frame symbol STAGING loop run as one Burst <see cref="IJob"/> over native mirrors of the
    /// <c>SymbolBatch</c> SoA, calling <see cref="SymbolStagingMath"/> over <c>NativeArray.AsSpan()</c> slices.
    /// It is one serial job, because each record's candidate ordinal depends on all prior records. The caller
    /// pre-sizes the outputs to the batch worst case. Incumbency resolves HERE against <see cref="Placed"/>:
    /// the point arm inline, the curved arm via <see cref="AnchorWasPlaced"/>.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct StageJob : IJob
    {
        // ── batch mirror (per-symbol records, in collected order) ──
        public NativeArray<SymbolPlacementKind> Kinds;   // point / curved
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
        // The gathered world polyline Screen was projected FROM, index-aligned with Screen/Depth/Valid. The curved
        // arm slices it at the screen path's (off, wc), so world and screen samples share one (segment, t).
        public NativeArray<double3> WorldPointsRender;
        // Unit surface normal per gathered world point, index-parallel to WorldPointsRender. Patched into
        // PointStageInput.SurfaceUp on the point arm, sliced for StageCurved on the curved arm.
        public NativeArray<float3>  WorldUpsRender;
        // JOB-OWNED incumbency scratch per global anchor-fade index, filled below from AnchorFadeIds + Placed.
        // The caller sizes it to _mirrorFadeCount.
        public NativeArray<byte>   AnchorWasPlaced;
        public float   Bearing;
        public double2 Viewport;
        // This frame's metres per LOGICAL screen pixel, already recombined by SymbolPlacementSystem.Tick
        // (MetresPerDevicePixel × DevicePixelRatio). Patched into each curved record below.
        public float   MetresPerLogicalPixel;
        // This frame's view transform, which projects render-space points for the map-pitched collision box.
        // StageCurved only; a default value selects the screen-space box (SymbolViewTransform.IsUsable).
        public SymbolViewTransform View;

        // Incumbency: last frame's collision survivors, keyed by fade id. The caller never stores it across
        // frames (see SymbolPlacementSystem.RunStageJob).
        public NativeHashSet<long>.ReadOnly Placed;

        // Last frame's per-half collision verdict for optional pairs, keyed by the pair's FadeId
        // (SymbolPlacementSystem._droppedHalvesLastFrame). Probed only for a pair with an optional half.
        public NativeHashMap<long, byte>.ReadOnly DroppedHalves;

        // ── caller-owned scratch (>= max WorldCount) ──
        public NativeArray<float2> PathPoints;
        public NativeArray<float>  CumulativeLengths;

        // ── outputs (pre-sized to the batch worst case) + counts ──
        public NativeArray<SymbolBox>       Boxes;
        public NativeArray<PlacedQuad>     StagedQuads;
        public NativeArray<SymbolCandidate> Candidates;
        public NativeArray<CandidateEmit>  Emit;
        public NativeArray<int>            OutCounts; // [0]=candidateCount [1]=boxCount [2]=quadCount [3]=emitCount

        public void Execute()
        {
            Span<SymbolBox>       boxes = Boxes.AsSpan();
            Span<PlacedQuad>     quads = StagedQuads.AsSpan();
            Span<SymbolCandidate> cands = Candidates.AsSpan();
            Span<CandidateEmit>  emit  = Emit.AsSpan();
            Span<float2>         path  = PathPoints.AsSpan();
            Span<float>          cum   = CumulativeLengths.AsSpan();

            // Resolve every anchor fade id against the placed-set HERE, in Burst, not on the main thread.
            // The fill covers the whole range, not just live records, so a culled record's entry is defined.
            for (int i = 0; i < AnchorWasPlaced.Length; i++)
                AnchorWasPlaced[i] = (byte)(Placed.Contains(AnchorFadeIds[i]) ? 1 : 0);

            int candidateCount = 0, boxCount = 0, quadCount = 0, emitCount = 0;
            for (int r = 0; r < Count; r++)
            {
                int off = PointOffset[r];
                if (off < 0) continue; // distance-culled
                int d = Detail[r];

                if (Kinds[r] == SymbolPlacementKind.Point)
                {
                    PointStageInput s = Points[d];

                    // A RIDER stages nothing on its own: its owner stages its box, quads and emit below, so the
                    // pair cannot self-block, and a culled owner drops the rider too (the pair's atomic cull).
                    if (s.PairRole == SymbolPairRole.Rider) continue;

                    s.ScreenPx = Screen[off];
                    s.Depth = Depth[off];
                    s.Projected = Valid[off] != 0;
                    s.SurfaceUp = WorldUpsRender[off]; // per-frame patched, like ScreenPx/Depth/Projected
                    s.WasPlacedLastFrame = Placed.Contains(s.FadeId); // s is the Points[d] copy; FadeId is never patched

                    ReadOnlySpan<SymbolQuad> quadSpan = Quads.AsSpan().Slice(PointQuadStart[d], PointQuadCount[d]);

                    // An owner whose rider is the NEXT point record (the reconciler emits them adjacently and the
                    // gather keeps winner order) stages as ONE pair. A broken adjacency degrades to a lone badge.
                    if (s.PairRole == SymbolPairRole.Owner && d + 1 < Points.Length && Points[d + 1].PairRole == SymbolPairRole.Rider)
                    {
                        PointStageInput rider = Points[d + 1];
                        ReadOnlySpan<SymbolQuad> riderQuadSpan = Quads.AsSpan().Slice(PointQuadStart[d + 1], PointQuadCount[d + 1]);
                        // Only an optional pair can carry a per-half verdict, so the hash probe is gated on
                        // the flags rather than run for every pair.
                        byte droppedHalvesLastFrame = 0;
                        if (s.PairOptional || rider.PairOptional)
                            DroppedHalves.TryGetValue(s.FadeId, out droppedHalvesLastFrame);
                        candidateCount += SymbolStagingMath.StagePointPair(in s, in rider, quadSpan, riderQuadSpan,
                            Bearing, Viewport, candidateCount,
                            boxes, ref boxCount, quads, ref quadCount, cands, emit, ref emitCount,
                            droppedHalvesLastFrame);
                    }
                    else
                    {
                        candidateCount += SymbolStagingMath.StagePoint(in s, quadSpan, Bearing, Viewport, candidateCount,
                            boxes, ref boxCount, quads, ref quadCount, cands, emit, ref emitCount);
                    }
                }
                else
                {
                    int wc = WorldCount[r];
                    int anchorCount = CurvedAnchorCount[d];
                    int fadeStart   = CurvedAnchorFadeStart[d];
                    CurvedStageInput s = Curveds[d];
                    s.MetresPerLogicalPixel = MetresPerLogicalPixel; // per-frame patch (see the field's doc)

                    ReadOnlySpan<float2>  screen = Screen.AsSpan().Slice(off, wc);
                    ReadOnlySpan<float>   depth  = Depth.AsSpan().Slice(off, wc);
                    ReadOnlySpan<byte>    valid  = Valid.AsSpan().Slice(off, wc);
                    ReadOnlySpan<double3> world  = WorldPointsRender.AsSpan().Slice(off, wc);
                    ReadOnlySpan<float3>  worldUps = WorldUpsRender.AsSpan().Slice(off, wc);
                    ReadOnlySpan<CurvedGlyph> glyphs  = Glyphs.AsSpan().Slice(CurvedGlyphStart[d], CurvedGlyphCount[d]);
                    ReadOnlySpan<LineAnchor>  anchors = Anchors.AsSpan().Slice(CurvedAnchorStart[d], anchorCount);
                    ReadOnlySpan<long> fadeIds   = AnchorFadeIds.AsSpan().Slice(fadeStart, anchorCount + 1);
                    ReadOnlySpan<byte> wasPlaced = AnchorWasPlaced.AsSpan().Slice(fadeStart, anchorCount + 1);

                    candidateCount += SymbolStagingMath.StageCurved(in s, screen, depth, valid, world, worldUps, glyphs, anchors,
                        fadeIds, wasPlaced, path.Slice(0, wc), cum.Slice(0, wc), Bearing, in View, candidateCount,
                        boxes, ref boxCount, quads, ref quadCount, cands, emit, ref emitCount);
                }
            }

            OutCounts[0] = candidateCount;
            OutCounts[1] = boxCount;
            OutCounts[2] = quadCount;
            OutCounts[3] = emitCount;
        }
    }
}
