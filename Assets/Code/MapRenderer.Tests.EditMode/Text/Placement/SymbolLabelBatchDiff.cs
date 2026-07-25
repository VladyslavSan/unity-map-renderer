// Unity EditMode only — SymbolLabelBatch uses Unity.Collections/Unity.Mathematics. NOT in core-tests.csproj.

using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Test-assembly-only comparator (no production surface) for two <see cref="SymbolLabelBatch"/> mirror
    /// snapshots — factored out of <c>SymbolGatherParityTests.FirstDifference</c> (Stage 2 order-parity tooth) so
    /// R1's memoized-gather tests (<c>LabelGatherMemoTests</c>, <c>SymbolLabelReconcileAsyncTests</c>) reuse the
    /// SAME field-by-field comparison instead of duplicating it.
    /// </summary>
    internal static class SymbolLabelBatchDiff
    {
        /// <summary>Returns the first field that differs between two batches (up to each count), or null if
        /// byte-identical.</summary>
        internal static string FirstDifference(SymbolLabelBatch o, SymbolLabelBatch g)
        {
            if (o.Count != g.Count) return $"Count {o.Count} vs {g.Count}";
            if (o.PointCount != g.PointCount) return $"PointCount {o.PointCount} vs {g.PointCount}";
            if (o.CurvedCount != g.CurvedCount) return $"CurvedCount {o.CurvedCount} vs {g.CurvedCount}";
            if (o.QuadCount != g.QuadCount) return $"QuadCount {o.QuadCount} vs {g.QuadCount}";
            if (o.GlyphCount != g.GlyphCount) return $"GlyphCount {o.GlyphCount} vs {g.GlyphCount}";
            if (o.AnchorCount != g.AnchorCount) return $"AnchorCount {o.AnchorCount} vs {g.AnchorCount}";
            if (o.WorldPointCount != g.WorldPointCount) return $"WorldPointCount {o.WorldPointCount} vs {g.WorldPointCount}";
            if (o.AnchorFadeCount != g.AnchorFadeCount) return $"AnchorFadeCount {o.AnchorFadeCount} vs {g.AnchorFadeCount}";
            if (o.MaxBoxes != g.MaxBoxes) return $"MaxBoxes {o.MaxBoxes} vs {g.MaxBoxes}";
            if (o.MaxQuads != g.MaxQuads) return $"MaxQuads {o.MaxQuads} vs {g.MaxQuads}";
            if (o.MaxCandidates != g.MaxCandidates) return $"MaxCandidates {o.MaxCandidates} vs {g.MaxCandidates}";

            for (int i = 0; i < o.Count; i++)
            {
                if (o.Kinds[i] != g.Kinds[i]) return $"Kinds[{i}] {o.Kinds[i]} vs {g.Kinds[i]}";
                if (o.Detail[i] != g.Detail[i]) return $"Detail[{i}] {o.Detail[i]} vs {g.Detail[i]}";
                if (o.WorldStart[i] != g.WorldStart[i]) return $"WorldStart[{i}] {o.WorldStart[i]} vs {g.WorldStart[i]}";
                if (o.WorldCount[i] != g.WorldCount[i]) return $"WorldCount[{i}] {o.WorldCount[i]} vs {g.WorldCount[i]}";
                if (!o.RepAnchor[i].Equals(g.RepAnchor[i])) return $"RepAnchor[{i}]";
                if (o.RecordDeparting[i] != g.RecordDeparting[i]) return $"RecordDeparting[{i}] {o.RecordDeparting[i]} vs {g.RecordDeparting[i]}";
                if (o.RecordCoverageFading[i] != g.RecordCoverageFading[i]) return $"RecordCoverageFading[{i}] {o.RecordCoverageFading[i]} vs {g.RecordCoverageFading[i]}";
            }
            for (int i = 0; i < o.PointCount; i++)
            {
                if (!o.Points[i].Equals(g.Points[i])) return $"Points[{i}]";
                if (o.PointQuadStart[i] != g.PointQuadStart[i]) return $"PointQuadStart[{i}] {o.PointQuadStart[i]} vs {g.PointQuadStart[i]}";
                if (o.PointQuadCount[i] != g.PointQuadCount[i]) return $"PointQuadCount[{i}] {o.PointQuadCount[i]} vs {g.PointQuadCount[i]}";
            }
            for (int i = 0; i < o.CurvedCount; i++)
            {
                if (!o.Curveds[i].Equals(g.Curveds[i])) return $"Curveds[{i}]";
                if (o.CurvedGlyphStart[i] != g.CurvedGlyphStart[i]) return $"CurvedGlyphStart[{i}]";
                if (o.CurvedGlyphCount[i] != g.CurvedGlyphCount[i]) return $"CurvedGlyphCount[{i}]";
                if (o.CurvedAnchorStart[i] != g.CurvedAnchorStart[i]) return $"CurvedAnchorStart[{i}]";
                if (o.CurvedAnchorCount[i] != g.CurvedAnchorCount[i]) return $"CurvedAnchorCount[{i}]";
                if (o.CurvedAnchorFadeStart[i] != g.CurvedAnchorFadeStart[i]) return $"CurvedAnchorFadeStart[{i}]";
            }
            for (int i = 0; i < o.QuadCount; i++) if (!o.Quads[i].Equals(g.Quads[i])) return $"Quads[{i}]";
            for (int i = 0; i < o.GlyphCount; i++) if (!o.Glyphs[i].Equals(g.Glyphs[i])) return $"Glyphs[{i}]";
            for (int i = 0; i < o.AnchorCount; i++) if (!o.Anchors[i].Equals(g.Anchors[i])) return $"Anchors[{i}]";
            for (int i = 0; i < o.WorldPointCount; i++) if (!o.WorldPoints[i].Equals(g.WorldPoints[i])) return $"WorldPoints[{i}]";
            for (int i = 0; i < o.AnchorFadeCount; i++) if (o.AnchorFadeIds[i] != g.AnchorFadeIds[i]) return $"AnchorFadeIds[{i}]";
            return null;
        }
    }
}
