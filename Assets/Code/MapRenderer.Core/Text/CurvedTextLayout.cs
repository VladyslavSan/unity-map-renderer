// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Lays out a shaped run for CURVED along-line placement — one <see cref="CurvedGlyph"/> per visible
    /// glyph, each carrying its along-run <see cref="CurvedGlyph.ArcCenter"/> and a cell centred on the path
    /// (horizontally on that arc center, vertically on the run's optical centre — see
    /// <see cref="TextQuadLayout.OpticalCentreBelowReferencePx"/>). Unlike <see cref="TextQuadLayout"/>
    /// (which lays glyphs into an anchored, justified, possibly-wrapped block for point placement), this is
    /// a single un-wrapped forward pass with NO block anchor / justify / offset — those are point concepts;
    /// a line symbol's position + orientation come from the projected line at placement time.
    /// </summary>
    public static class CurvedTextLayout
    {
        /// <summary>Allocating overload — returns a fresh list. For the per-frame path prefer the caller-buffer
        /// overload; this layout is build-time (cached on the symbol), so the allocation is once per symbol.</summary>
        public static List<CurvedGlyph> Layout(ShapedRun run, IGlyphAtlasView atlas)
        {
            var output = new List<CurvedGlyph>(run?.Glyphs?.Count ?? 0);
            Layout(run, atlas, output);
            return output;
        }

        /// <summary>Caller-buffer overload: clears and writes into <paramref name="output"/>.</summary>
        public static void Layout(ShapedRun run, IGlyphAtlasView atlas, List<CurvedGlyph> output)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (atlas == null) throw new ArgumentNullException(nameof(atlas));
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();

            IReadOnlyList<PositionedGlyph> glyphs = run.Glyphs;
            float2 atlasSize = atlas.Size;
            float penX = 0f;

            for (int i = 0; i < glyphs.Count; i++)
            {
                PositionedGlyph glyph = glyphs[i];
                if (!atlas.TryGetEntry(glyph.FontId, glyph.AtlasCodepoint, out GlyphAtlasEntry entry))
                {
                    // notdef: no quad, fall back to the shaped advance (mirrors TextQuadLayout).
                    penX += glyph.XAdvance;
                    continue;
                }

                float advance = entry.Advance;
                if (!IsWhitespaceEntry(entry))
                {
                    float2 cellSize = entry.CellSize;
                    float arcCenter = penX + advance * 0.5f;

                    // Same TOP-referenced cell as TextQuadLayout.PlaceGlyph, but placed relative to THIS
                    // glyph's own pen origin and centered HORIZONTALLY on arcCenter — and VERTICALLY on the
                    // path, by the same OpticalCentreBelowReferencePx a point symbol's Centre vertical anchor
                    // applies, so a curved and a point symbol of the same string have the same
                    // optical relationship to their anchor. That shift is one constant per LABEL (no `entry`
                    // term), so the run's own typography is untouched: ascenders and descenders keep their
                    // relative offsets instead of each glyph bobbing onto its own ink centre.
                    float leftX = penX + entry.Left - GlyphSdf.Buffer;
                    float cellTopY = entry.Top + GlyphSdf.Buffer + TextQuadLayout.OpticalCentreBelowReferencePx;
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
            }
        }

        // Mirrors TextQuadLayout: a cell no larger than the bare buffer border on either axis carries no
        // visible glyph (space's Cell(6,6) at Buffer=3).
        private static bool IsWhitespaceEntry(in GlyphAtlasEntry entry)
            => entry.CellSize.x <= 2 * GlyphSdf.Buffer && entry.CellSize.y <= 2 * GlyphSdf.Buffer;
    }
}
