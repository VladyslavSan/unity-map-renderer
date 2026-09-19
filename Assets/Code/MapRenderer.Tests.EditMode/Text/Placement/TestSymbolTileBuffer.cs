// Unity EditMode test-assembly seam (not production, not core-tests). The direct buffer-builder every
// hand-build test cluster uses to construct fixtures straight into a SymbolTileBuffer.

using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests; // TestSymbolPlan — <see cref> in CopySymbolInto's doc only.

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Appends hand-built <see cref="ShapedSymbol"/>s straight into a <see cref="SymbolTileBuffer"/> —
    /// mirroring what <c>StyledSymbolTileBuilder.Shape</c>'s point/curved/icon emit branches produce, so a
    /// test fixture can feed <c>SymbolTileBlockBaker.Bake</c> directly.
    ///
    /// <para><b>Default-value contract (READ BEFORE ADDING A PARAMETER).</b> Every optional parameter below
    /// defaults to <c>default(T)</c> of its own type, NOT the "spec" default the corresponding
    /// <see cref="ShapedSymbol"/> field's own XML doc describes (e.g. an omitted <c>keepUpright</c> is
    /// <c>false</c>, not the style-spec's <c>true</c>; an omitted <c>maxAngleDeg</c> is <c>0f</c>, not <c>45</c>;
    /// an omitted <c>paint</c> is <c>default(SymbolPaint)</c> — all-zero — NOT <see cref="SymbolPaint.Default"/>'s
    /// opaque black). This mirrors the retired per-symbol managed carrier class this buffer-builder replaced —
    /// its bare <c>init</c> auto-properties had no initializers either, so a fixture built with it also left
    /// every unset field at <c>default(T)</c>. Do NOT "helpfully" substitute a spec default here — that is what
    /// keeps every fixture in this cluster byte-identical across a refactor of the underlying representation.
    ///
    /// <para>Field → default(T) table (every field not called out below is either a required parameter here —
    /// <c>anchorRender</c>/<c>quads</c>/<c>boundsMin</c>/<c>boundsMax</c> for a point, <c>glyphs</c>/<c>anchors</c>/
    /// <c>path</c> for a curved symbol — or has no scalar counterpart (the pooled span starts/counts)):</para>
    /// <list type="table">
    /// <item><term><c>text</c>/<c>iconImage</c></term><description><c>null</c>, which <see cref="SymbolStringTable.Intern"/>
    /// resolves to <c>TextId</c>/<c>IconImageId</c> = <c>0</c> — still <c>default(int)</c></description></item>
    /// <item><term><c>up</c></term><description><c>double3.zero</c></description></item>
    /// <item><term><c>kind</c></term><description><see cref="SymbolKind.Text"/> (the zero value)</description></item>
    /// <item><term><c>materialIndex</c>/<c>featureIndex</c>/<c>pairId</c></term><description><c>0</c></description></item>
    /// <item><term><c>tileKey</c></term><description><c>0L</c></description></item>
    /// <item><term><c>textSizePx</c>/<c>paddingPx</c>/<c>sortKey</c>/<c>maxAngleDeg</c>/<c>iconRotateRadians</c></term><description><c>0f</c></description></item>
    /// <item><term><c>allowOverlap</c>/<c>ignorePlacement</c>/<c>keepUpright</c>/<c>pairOptional</c></term><description><c>false</c></description></item>
    /// <item><term><c>translatePx</c></term><description><c>float2.zero</c></description></item>
    /// <item><term><c>translateAnchor</c></term><description><see cref="TextTranslateAnchor.Map"/> (the zero value)</description></item>
    /// <item><term><c>rotationAlignment</c>/<c>pitchAlignment</c></term><description><see cref="AlignmentMode.Auto"/> (the zero value)</description></item>
    /// <item><term><c>pairRole</c></term><description><see cref="SymbolPairRole.None"/> (the zero value)</description></item>
    /// <item><term><c>paint</c></term><description><c>default(SymbolPaint)</c> — all-zero, NOT <see cref="SymbolPaint.Default"/></description></item>
    /// </list>
    /// </summary>
    internal static class TestSymbolTileBuffer
    {
        // UMR-87: ShapedSymbol carries interned TextId/IconImageId ints, not raw strings — every AddPoint/
        // AddCurved call below still takes the plain string and interns it. A caller that cares about ids
        // matching ACROSS buffers/calls (e.g. a cross-tile dedup fixture) passes its own SymbolStringTable
        // explicitly; a caller that does not (the overwhelming majority of hand-built single-buffer fixtures)
        // falls back to this ONE shared, never-reset table — never a fresh table per call, which would let two
        // DIFFERENT strings collide onto the same id (both getting 1) across separate Add* calls.
        private static readonly SymbolStringTable DefaultStringTable = new SymbolStringTable();

        /// <summary>Appends one POINT (or icon — see <paramref name="kind"/>) symbol. Every scalar parameter
        /// past <paramref name="boundsMax"/> defaults to <c>default(T)</c> — see the type doc's table.</summary>
        /// <param name="buffer">Build buffer this record's quads and the record itself are appended into.</param>
        /// <param name="anchorRender">The symbol's projected (pre-RTC) feature anchor.</param>
        /// <param name="quads">This symbol's baked-px glyph/icon quads (may be null/empty).</param>
        /// <param name="boundsMin">The laid-out block's anchor-relative bounding box min corner.</param>
        /// <param name="boundsMax">The laid-out block's anchor-relative bounding box max corner.</param>
        /// <param name="stringTable">Interns <paramref name="text"/>/<paramref name="iconImage"/>; null (the
        /// default) uses the shared <see cref="DefaultStringTable"/> — pass an explicit table when the test
        /// needs literal id values or cross-buffer id parity.</param>
        internal static void AddPoint(SymbolTileBuffer buffer, double3 anchorRender, IReadOnlyList<SymbolQuad> quads,
            float2 boundsMin, float2 boundsMax,
            string text = default, double3 up = default, string iconImage = default, SymbolKind kind = default,
            int materialIndex = default, float textSizePx = default, float paddingPx = default, float sortKey = default,
            int featureIndex = default, long tileKey = default, bool allowOverlap = default, bool ignorePlacement = default,
            float2 translatePx = default, TextTranslateAnchor translateAnchor = default,
            AlignmentMode rotationAlignment = default, float iconRotateRadians = default,
            SymbolPairRole pairRole = default, int pairId = default, bool pairOptional = default,
            SymbolPaint paint = default, SymbolStringTable stringTable = null)
        {
            SymbolStringTable table = stringTable ?? DefaultStringTable;
            int quadStart = buffer.AppendQuads(quads, out int quadCount);
            buffer.AddSymbol(new ShapedSymbol
            {
                Placement = SymbolPlacement.Point,
                Kind = kind, MaterialIndex = materialIndex, TextId = table.Intern(text), IconImageId = table.Intern(iconImage),
                AnchorRender = anchorRender, UpRender = up,
                BoundsMin = boundsMin, BoundsMax = boundsMax,
                QuadStart = quadStart, QuadCount = quadCount,
                TextSizePx = textSizePx, PaddingPx = paddingPx, SortKey = sortKey,
                FeatureIndex = featureIndex, TileKey = tileKey,
                AllowOverlap = allowOverlap, IgnorePlacement = ignorePlacement,
                TranslatePx = translatePx, TranslateAnchor = translateAnchor, RotationAlignment = rotationAlignment,
                IconRotateRadians = iconRotateRadians, Paint = paint,
                PairRole = pairRole, PairId = pairId, PairOptional = pairOptional,
            });
        }

        /// <summary>Appends one CURVED (along-line) symbol. Every scalar parameter past <paramref name="path"/>
        /// defaults to <c>default(T)</c> — see the type doc's table. <paramref name="anchorRender"/> is the
        /// rep-anchor fallback the baker reads when <paramref name="path"/> is empty (mirrors
        /// <c>SymbolTileBlockBaker.Fill</c>'s <c>record.AnchorRender</c> read).</summary>
        /// <param name="buffer">Build buffer this record's glyphs/anchors/path and the record itself are appended into.</param>
        /// <param name="glyphs">Per-glyph curved layout cells (may be null/empty).</param>
        /// <param name="anchors">The zoom-invariant along-line anchors (may be null/empty).</param>
        /// <param name="path">The line's render-space vertices, PRE-RTC (may be null/empty).</param>
        /// <param name="pathUp">Index-parallel to <paramref name="path"/>; a short/absent entry pads to <c>double3.zero</c>.</param>
        internal static void AddCurved(SymbolTileBuffer buffer, IReadOnlyList<CurvedGlyph> glyphs, LineAnchor[] anchors,
            double3[] path, double3[] pathUp = default, double3 anchorRender = default,
            SymbolPlacement placement = SymbolPlacement.Line,
            string text = default, double3 up = default, string iconImage = default, SymbolKind kind = default,
            int materialIndex = default, float textSizePx = default, float paddingPx = default, float sortKey = default,
            float maxAngleDeg = default, bool keepUpright = default,
            int featureIndex = default, long tileKey = default, bool allowOverlap = default, bool ignorePlacement = default,
            float2 translatePx = default, TextTranslateAnchor translateAnchor = default,
            AlignmentMode pitchAlignment = default, float iconRotateRadians = default,
            SymbolPaint paint = default, SymbolStringTable stringTable = null)
        {
            SymbolStringTable table = stringTable ?? DefaultStringTable;
            int glyphStart = buffer.AppendGlyphs(glyphs, out int glyphCount);
            int anchorStart = buffer.AppendAnchors(anchors, out int anchorCount);
            int pathStart = buffer.AppendPath(path, pathUp, out int pathCount);

            buffer.AddSymbol(new ShapedSymbol
            {
                Placement = placement,
                Kind = kind, MaterialIndex = materialIndex, TextId = table.Intern(text), IconImageId = table.Intern(iconImage),
                AnchorRender = anchorRender, UpRender = up,
                GlyphStart = glyphStart, GlyphCount = glyphCount,
                AnchorStart = anchorStart, AnchorCount = anchorCount,
                PathStart = pathStart, PathCount = pathCount,
                TextSizePx = textSizePx, PaddingPx = paddingPx, SortKey = sortKey,
                MaxAngleDeg = maxAngleDeg, KeepUpright = keepUpright,
                FeatureIndex = featureIndex, TileKey = tileKey,
                AllowOverlap = allowOverlap, IgnorePlacement = ignorePlacement,
                TranslatePx = translatePx, TranslateAnchor = translateAnchor, PitchAlignment = pitchAlignment,
                IconRotateRadians = iconRotateRadians, Paint = paint,
            });
        }

        /// <summary>Convenience one-shot: a fresh <see cref="SymbolTileBuffer"/> holding a single point symbol
        /// (see <see cref="AddPoint"/> for the parameter contract).</summary>
        internal static SymbolTileBuffer Point(double3 anchorRender, IReadOnlyList<SymbolQuad> quads,
            float2 boundsMin, float2 boundsMax,
            string text = default, double3 up = default, string iconImage = default, SymbolKind kind = default,
            int materialIndex = default, float textSizePx = default, float paddingPx = default, float sortKey = default,
            int featureIndex = default, long tileKey = default, bool allowOverlap = default, bool ignorePlacement = default,
            float2 translatePx = default, TextTranslateAnchor translateAnchor = default,
            AlignmentMode rotationAlignment = default, float iconRotateRadians = default,
            SymbolPairRole pairRole = default, int pairId = default, bool pairOptional = default,
            SymbolPaint paint = default, SymbolStringTable stringTable = null)
        {
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, anchorRender, quads, boundsMin, boundsMax, text, up, iconImage, kind, materialIndex,
                textSizePx, paddingPx, sortKey, featureIndex, tileKey, allowOverlap, ignorePlacement, translatePx,
                translateAnchor, rotationAlignment, iconRotateRadians, pairRole, pairId, pairOptional, paint, stringTable);
            return buffer;
        }

        /// <summary>Convenience one-shot: a fresh <see cref="SymbolTileBuffer"/> holding a single curved symbol
        /// (see <see cref="AddCurved"/> for the parameter contract).</summary>
        internal static SymbolTileBuffer Curved(IReadOnlyList<CurvedGlyph> glyphs, LineAnchor[] anchors,
            double3[] path, double3[] pathUp = default, double3 anchorRender = default,
            SymbolPlacement placement = SymbolPlacement.Line,
            string text = default, double3 up = default, string iconImage = default, SymbolKind kind = default,
            int materialIndex = default, float textSizePx = default, float paddingPx = default, float sortKey = default,
            float maxAngleDeg = default, bool keepUpright = default,
            int featureIndex = default, long tileKey = default, bool allowOverlap = default, bool ignorePlacement = default,
            float2 translatePx = default, TextTranslateAnchor translateAnchor = default,
            AlignmentMode pitchAlignment = default, float iconRotateRadians = default,
            SymbolPaint paint = default, SymbolStringTable stringTable = null)
        {
            var buffer = new SymbolTileBuffer();
            AddCurved(buffer, glyphs, anchors, path, pathUp, anchorRender, placement, text, up, iconImage, kind,
                materialIndex, textSizePx, paddingPx, sortKey, maxAngleDeg, keepUpright, featureIndex, tileKey,
                allowOverlap, ignorePlacement, translatePx, translateAnchor, pitchAlignment, iconRotateRadians, paint,
                stringTable);
            return buffer;
        }

        /// <summary>Copies record <paramref name="i"/> of <paramref name="source"/> — its scalar state AND its
        /// pooled quad/glyph/anchor/path spans — into <paramref name="dest"/>, re-offsetting the spans to dest's
        /// pools. Used to regroup one flat build buffer into per-tile sub-scratches
        /// (<see cref="TestSymbolPlan.Build"/>).</summary>
        internal static void CopySymbolInto(SymbolTileBuffer dest, SymbolTileBuffer source, int i)
        {
            ShapedSymbol r = source.Symbols[i];

            int quadStart = dest.Quads.Count;
            for (int q = 0; q < r.QuadCount; q++) dest.Quads.Add(source.Quads[r.QuadStart + q]);
            int glyphStart = dest.Glyphs.Count;
            for (int g = 0; g < r.GlyphCount; g++) dest.Glyphs.Add(source.Glyphs[r.GlyphStart + g]);
            int anchorStart = dest.Anchors.Count;
            for (int a = 0; a < r.AnchorCount; a++) dest.Anchors.Add(source.Anchors[r.AnchorStart + a]);
            int pathStart = dest.Path.Count;
            for (int v = 0; v < r.PathCount; v++)
            {
                dest.Path.Add(source.Path[r.PathStart + v]);
                dest.PathUp.Add(source.PathUp[r.PathStart + v]);
            }

            dest.AddSymbol(new ShapedSymbol
            {
                Placement = r.Placement, Kind = r.Kind, MaterialIndex = r.MaterialIndex,
                TextId = r.TextId, IconImageId = r.IconImageId, AnchorRender = r.AnchorRender, UpRender = r.UpRender,
                BoundsMin = r.BoundsMin, BoundsMax = r.BoundsMax,
                QuadStart = quadStart, QuadCount = r.QuadCount, GlyphStart = glyphStart, GlyphCount = r.GlyphCount,
                AnchorStart = anchorStart, AnchorCount = r.AnchorCount, PathStart = pathStart, PathCount = r.PathCount,
                TextSizePx = r.TextSizePx, PaddingPx = r.PaddingPx, SortKey = r.SortKey, FeatureIndex = r.FeatureIndex,
                TileKey = r.TileKey, AllowOverlap = r.AllowOverlap, IgnorePlacement = r.IgnorePlacement,
                TranslatePx = r.TranslatePx, TranslateAnchor = r.TranslateAnchor, RotationAlignment = r.RotationAlignment,
                MaxAngleDeg = r.MaxAngleDeg, KeepUpright = r.KeepUpright, IconRotateRadians = r.IconRotateRadians,
                PitchAlignment = r.PitchAlignment, Paint = r.Paint, PairRole = r.PairRole, PairId = r.PairId,
                PairOptional = r.PairOptional,
            });
        }
    }
}
