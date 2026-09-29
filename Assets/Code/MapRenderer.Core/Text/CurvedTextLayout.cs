// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Lays out a shaped run for CURVED along-line placement: one <see cref="CurvedGlyph"/> per visible glyph,
    /// with its <see cref="CurvedGlyph.ArcCenter"/> and a cell centred on the path (on the arc centre, and
    /// vertically on the anchor's shift from <see cref="TextQuadLayout.VerticalAnchorShiftPx"/>). It is one
    /// un-wrapped forward pass with no justify. The block anchor and the text offset set the across-line shift
    /// here and give the along-line shift; the projected line gives position and orientation.
    /// </summary>
    public static class CurvedTextLayout
    {
        /// <summary>Allocating overload — returns a fresh list. For the per-frame path prefer the caller-buffer
        /// overload; this layout is build-time (cached on the symbol), so the allocation is once per symbol.</summary>
        public static List<CurvedGlyph> Layout(ShapedRun run, IGlyphAtlasView atlas, float letterSpacingEm = 0f)
        {
            var output = new List<CurvedGlyph>(run?.Glyphs?.Count ?? 0);
            _ = Layout(run, atlas, output, letterSpacingEm);
            return output;
        }

        /// <summary>Caller-buffer overload: clears and writes into <paramref name="output"/>.
        /// <paramref name="letterSpacingEm"/> is <c>text-letter-spacing</c>, added after every glyph's advance
        /// (including notdef), mirroring <see cref="TextQuadLayout"/>. <paramref name="options"/> supplies
        /// <c>text-anchor</c>, <c>text-offset</c> or <c>text-radial-offset</c>, and the line height; a non-positive
        /// line height takes the default.</summary>
        /// <returns>The signed baked-px shift along the line from the run's centre to where the anchor and the
        /// offset put it; 0 for a centred run without an offset.</returns>
        public static float Layout(ShapedRun run, IGlyphAtlasView atlas, List<CurvedGlyph> output, float letterSpacingEm = 0f,
            in TextLayoutOptions options = default)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (atlas == null) throw new ArgumentNullException(nameof(atlas));
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();

            IReadOnlyList<PositionedGlyph> glyphs = run.Glyphs;
            float2 atlasSize = atlas.Size;
            float penX = 0f;
            float runWidth = 0f;
            float letterPx = letterSpacingEm * TextQuadLayout.OneEm;
            (float hAlign, TextQuadLayout.VerticalAnchor vertical) = TextQuadLayout.ResolveAlignFactors(options.Anchor);
            float lineHeightPx = (options.LineHeightEm > 0f ? options.LineHeightEm : TextQuadLayout.DefaultLineHeightEm)
                * TextQuadLayout.OneEm;
            float2 offsetPx = TextQuadLayout.ResolveOffsetEm(hAlign, vertical, in options) * TextQuadLayout.OneEm;
            float verticalShiftPx = TextQuadLayout.VerticalAnchorShiftPx(vertical, 1, lineHeightPx) + offsetPx.y;

            bool reorder = run.Levels is { Count: > 0 };
            Span<int> visual = !reorder ? default
                : glyphs.Count <= TextQuadLayout.MaxStackGlyphs ? stackalloc int[glyphs.Count] : new int[glyphs.Count];
            if (reorder) BidiReorder.ReorderSegments(glyphs, run.Levels, 0, glyphs.Count, visual);
            for (int i = 0; i < glyphs.Count; i++)
            {
                PositionedGlyph glyph = glyphs[reorder ? visual[i] : i];
                if (!atlas.TryGetEntry(glyph.FontId, glyph.AtlasCodepoint, out GlyphAtlasEntry entry))
                {
                    // notdef: no quad, fall back to the shaped advance (mirrors TextQuadLayout).
                    penX += glyph.XAdvance;
                    runWidth = penX;
                    penX += letterPx;
                    continue;
                }

                float advance = entry.Advance;
                bool isWhitespace = IsWhitespaceEntry(entry);
                if (!isWhitespace)
                {
                    float2 cellSize = entry.CellSize;
                    float arcCenter = penX + advance * 0.5f;

                    // TextQuadLayout.PlaceGlyph's cell, centred on arcCenter and shifted by the anchor's
                    // vertical shift, so glyphs keep their relative baselines.
                    float leftX = penX + entry.Left - GlyphSdf.Buffer;
                    float cellTopY = entry.Top + GlyphSdf.Buffer + verticalShiftPx;
                    float2 topLeft = new float2(leftX - arcCenter, cellTopY);
                    float2 bottomRight = new float2(leftX + cellSize.x - arcCenter, cellTopY - cellSize.y);

                    float2 uvMin = (float2)entry.AtlasOrigin / atlasSize;
                    float2 uvMax = ((float2)entry.AtlasOrigin + cellSize) / atlasSize;

                    output.Add(new CurvedGlyph
                    {
                        ArcCenter = arcCenter,
                        Cell = new SymbolQuad
                        {
                            TopLeft = topLeft,
                            BottomRight = bottomRight,
                            UvTopLeft = uvMin,
                            UvBottomRight = uvMax,
                            LineIndex = 0,
                            Page = entry.Page,
                        },
                    });
                }

                penX += advance;
                if (!isWhitespace) runWidth = penX;
                penX += letterPx;
            }

            if (output.Count == 0) return 0f;
            float runCentre = (output[0].ArcCenter + output[output.Count - 1].ArcCenter) * 0.5f;
            float anchorShift = hAlign == 0.5f ? 0f : runCentre - hAlign * runWidth;
            return anchorShift + offsetPx.x;
        }

        // Mirrors TextQuadLayout: a cell no larger than the bare buffer border on either axis carries no
        // visible glyph (space's Cell(6,6) at Buffer=3).
        private static bool IsWhitespaceEntry(in GlyphAtlasEntry entry)
            => entry.CellSize.x <= 2 * GlyphSdf.Buffer && entry.CellSize.y <= 2 * GlyphSdf.Buffer;
    }
}
