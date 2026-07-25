// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.Placement
// and uses Unity.Mathematics types — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, never an inline
// `Unity.Mathematics.X`. Unity-side (needs Unity.Collections' NativeArray — Core stays engine-free).

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 1 (design doc §4, §5 B): bakes one tile's build-time
    /// <c>List&lt;LabelInstance&gt;</c> into a fresh native <see cref="SymbolTileLabelBlock"/> — reproducing
    /// EXACTLY the per-label field math <see cref="SymbolLabelBatchBuilder"/>'s <c>AddPoint</c>/<c>AddCurved</c>
    /// compute for the per-frame oracle, via the shared <see cref="SymbolLabelBatchBuilder.BuildPointInput"/>/
    /// <see cref="SymbolLabelBatchBuilder.BuildCurvedInput"/> helpers so the two paths cannot drift (the
    /// drift-guard the design calls for).
    ///
    /// <para>Runs on the MAIN thread, once per tile commit (<c>SymbolLabelSubsystem.RunTailAsync</c>) — glyph
    /// quads / curved glyphs are only materialized there (the per-layer shape tail), so there is nothing left
    /// to bake off it. No <c>IProjection</c> parameter (bake-safety): every label in one build's list belongs
    /// to the SAME physical tile (one <c>(source, tile)</c> build), so the caller's single
    /// <paramref name="tileOriginRender"/> already IS the launch-time <c>TileRenderOrigin.Project</c> result
    /// folded into every label's <c>AnchorRender</c> — unlike <see cref="SymbolLabelBatchBuilder.Build"/>,
    /// which walks a multi-tile collected set and so keeps its own per-tile origin cache.</para>
    /// </summary>
    internal static class SymbolTileLabelBlockBaker
    {
        /// <summary>Bake <paramref name="labels"/> (one tile's build output — a RAW list that may contain
        /// <c>null</c> slots for a per-label build failure, see <see cref="SymbolTileLabelBlock"/>'s null-slot
        /// invariant) into a fresh <see cref="SymbolTileLabelBlock"/>. <paramref name="slotCount"/> clamps each
        /// label's material slot (mirrors <see cref="SymbolLabelBatchBuilder.Build"/>'s <c>ClampSlot</c>).
        ///
        /// <para>(G) Exception-safety: every array is allocated INTO the returned block; on any exception
        /// mid-bake (allocation or fill), the partially-built block is disposed (frees whatever
        /// <see cref="NativeArray{T}.IsCreated"/>) before the exception is rethrown — never a partial-allocation
        /// leak.</para></summary>
        internal static SymbolTileLabelBlock Bake(List<LabelInstance> labels, int slotCount, in double3 tileOriginRender)
        {
            var block = new SymbolTileLabelBlock();
            try
            {
                int rawCount = labels?.Count ?? 0;
                CountSizes(labels, rawCount, out int pointCount, out int curvedCount, out int quadCount,
                    out int glyphCount, out int anchorCount, out int anchorFadeCount, out int worldPointCount);

                block.Count = rawCount;
                block.Kinds = new NativeArray<byte>(rawCount, Allocator.Persistent);
                block.Detail = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.WorldStart = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.WorldCount = new NativeArray<int>(rawCount, Allocator.Persistent);
                block.RepAnchor = new NativeArray<double3>(rawCount, Allocator.Persistent);

                block.Points = new NativeArray<PointStageInput>(pointCount, Allocator.Persistent);
                block.PointQuadStart = new NativeArray<int>(pointCount, Allocator.Persistent);
                block.PointQuadCount = new NativeArray<int>(pointCount, Allocator.Persistent);

                block.Curveds = new NativeArray<CurvedStageInput>(curvedCount, Allocator.Persistent);
                block.CurvedGlyphStart = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedGlyphCount = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedAnchorStart = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedAnchorCount = new NativeArray<int>(curvedCount, Allocator.Persistent);
                block.CurvedAnchorFadeStart = new NativeArray<int>(curvedCount, Allocator.Persistent);

                block.Quads = new NativeArray<SymbolQuad>(quadCount, Allocator.Persistent);
                block.Glyphs = new NativeArray<CurvedGlyph>(glyphCount, Allocator.Persistent);
                block.Anchors = new NativeArray<LineAnchor>(anchorCount, Allocator.Persistent);
                block.WorldPoints = new NativeArray<double3>(worldPointCount, Allocator.Persistent);
                block.AnchorFadeIds = new NativeArray<long>(anchorFadeCount, Allocator.Persistent);

                Fill(block, labels, rawCount, slotCount, tileOriginRender);
                block.TileKey = ResolveTileKey(labels, rawCount);
                return block;
            }
            catch
            {
                block.Dispose();
                throw;
            }
        }

        // First pass: exact per-array sizes, so every NativeArray below is allocated ONCE at its final size
        // (Burst-safe fixed arrays — no growth). Mirrors AddPoint/AddCurved's contribution to each pool 1:1.
        private static void CountSizes(List<LabelInstance> labels, int rawCount, out int pointCount, out int curvedCount,
            out int quadCount, out int glyphCount, out int anchorCount, out int anchorFadeCount, out int worldPointCount)
        {
            pointCount = 0; curvedCount = 0; quadCount = 0; glyphCount = 0;
            anchorCount = 0; anchorFadeCount = 0; worldPointCount = 0;
            for (int i = 0; i < rawCount; i++)
            {
                LabelInstance label = labels[i];
                if (label == null) { pointCount++; continue; } // inert placeholder — still consumes a Points slot
                if (label.Placement == SymbolPlacement.Point)
                {
                    pointCount++;
                    quadCount += label.Layout?.Quads?.Count ?? 0;
                    worldPointCount += 1; // point anchor → 1 world point
                }
                else
                {
                    curvedCount++;
                    glyphCount += label.CurvedGlyphs?.Count ?? 0;
                    int anchorLen = math.min(label.LineAnchors?.Length ?? 0, LabelStagingMath.MaxAnchorsPerLine);
                    anchorCount += anchorLen;
                    anchorFadeCount += anchorLen + 1; // + the centred fallback
                    worldPointCount += label.PathRender?.Length ?? 0;
                }
            }
        }

        // Second pass: fill every array, running each pool's write cursor forward — the SAME per-label field
        // math as SymbolLabelBatchBuilder.AddPoint/AddCurved (shared via BuildPointInput/BuildCurvedInput), and
        // the SAME Max*/record bookkeeping, just writing into pre-sized NativeArrays instead of growable arrays.
        private static void Fill(SymbolTileLabelBlock block, List<LabelInstance> labels, int rawCount, int slotCount, in double3 tileOriginRender)
        {
            int pointIdx = 0, curvedIdx = 0, quadIdx = 0, glyphIdx = 0, anchorIdx = 0, anchorFadeIdx = 0, worldPointIdx = 0;

            for (int i = 0; i < rawCount; i++)
            {
                LabelInstance label = labels[i];
                if (label == null)
                {
                    // Null-slot invariant (SymbolTileLabelBlock's PIN doc): an inert record — Kind=Point, zero
                    // quad/world contribution, zero Max* share — so localIndex == i for every later label.
                    int slot = pointIdx++;
                    block.Points[slot] = default;
                    block.PointQuadStart[slot] = quadIdx;
                    block.PointQuadCount[slot] = 0;
                    block.Kinds[i] = (byte)SymbolLabelBatch.Kind.Point;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldPointIdx;
                    block.WorldCount[i] = 0;
                    block.RepAnchor[i] = double3.zero;
                    continue;
                }

                if (label.Placement == SymbolPlacement.Point)
                {
                    IReadOnlyList<SymbolQuad> quads = label.Layout?.Quads;
                    int quadCount = quads?.Count ?? 0;
                    int quadStart = quadIdx;
                    for (int q = 0; q < quadCount; q++) block.Quads[quadIdx++] = quads[q];

                    int slot = pointIdx++;
                    block.Points[slot] = SymbolLabelBatchBuilder.BuildPointInput(label, slotCount, tileOriginRender);
                    block.PointQuadStart[slot] = quadStart;
                    block.PointQuadCount[slot] = quadCount;

                    int worldStart = worldPointIdx;
                    block.WorldPoints[worldPointIdx++] = label.AnchorRender;

                    block.Kinds[i] = (byte)SymbolLabelBatch.Kind.Point;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldStart;
                    block.WorldCount[i] = 1;
                    block.RepAnchor[i] = label.AnchorRender;

                    // Mirrors SymbolLabelBatch.AddPoint: one AABB box + its quads, one candidate.
                    block.MaxBoxes += 1; block.MaxQuads += quadCount; block.MaxCandidates += 1;
                }
                else
                {
                    IReadOnlyList<CurvedGlyph> glyphs = label.CurvedGlyphs;
                    int glyphCount = glyphs?.Count ?? 0;
                    int glyphStart = glyphIdx;
                    for (int g = 0; g < glyphCount; g++) block.Glyphs[glyphIdx++] = glyphs[g];

                    LineAnchor[] anchors = label.LineAnchors;
                    int anchorLen = math.min(anchors?.Length ?? 0, LabelStagingMath.MaxAnchorsPerLine);
                    int anchorStart = anchorIdx;
                    for (int a = 0; a < anchorLen; a++) block.Anchors[anchorIdx++] = anchors[a];

                    int anchorFadeStart = anchorFadeIdx;
                    for (int a = 0; a < anchorLen; a++)
                        block.AnchorFadeIds[anchorFadeIdx++] =
                            LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, a);
                    block.AnchorFadeIds[anchorFadeIdx++] =
                        LabelStagingMath.LineFadeId(label.TileKey, label.MaterialIndex, label.FeatureIndex, -1); // fallback

                    int slot = curvedIdx++;
                    block.Curveds[slot] = SymbolLabelBatchBuilder.BuildCurvedInput(label, slotCount, tileOriginRender);
                    block.CurvedGlyphStart[slot] = glyphStart; block.CurvedGlyphCount[slot] = glyphCount;
                    block.CurvedAnchorStart[slot] = anchorStart; block.CurvedAnchorCount[slot] = anchorLen;
                    block.CurvedAnchorFadeStart[slot] = anchorFadeStart;

                    double3[] path = label.PathRender;
                    int pathLen = path?.Length ?? 0;
                    int worldStart = worldPointIdx;
                    for (int v = 0; v < pathLen; v++) block.WorldPoints[worldPointIdx++] = path[v];
                    double3 rep = pathLen > 0 ? path[pathLen / 2] : label.AnchorRender;

                    block.Kinds[i] = (byte)SymbolLabelBatch.Kind.Curved;
                    block.Detail[i] = slot;
                    block.WorldStart[i] = worldStart;
                    block.WorldCount[i] = pathLen;
                    block.RepAnchor[i] = rep;

                    // Mirrors SymbolLabelBatch.AddCurved: worst case every anchor + the centred fallback stages.
                    int placements = anchorLen + 1;
                    block.MaxBoxes += placements * glyphCount; block.MaxQuads += placements * glyphCount;
                    block.MaxCandidates += placements;
                }
            }

            block.PointCount = pointIdx; block.CurvedCount = curvedIdx; block.QuadCount = quadIdx;
            block.GlyphCount = glyphIdx; block.AnchorCount = anchorIdx; block.AnchorFadeCount = anchorFadeIdx;
            block.WorldPointCount = worldPointIdx;
        }

        // Every label in one build's list shares the same physical tile (one (source, tile) build) — the
        // first non-null label's TileKey identifies the whole block; an all-null build (every label failed)
        // has no tile identity to report, so it stays 0 (inert — nothing downstream keys off it in Stage 1).
        private static long ResolveTileKey(List<LabelInstance> labels, int rawCount)
        {
            for (int i = 0; i < rawCount; i++)
                if (labels[i] != null) return labels[i].TileKey;
            return 0;
        }
    }
}
