// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Turns a <see cref="ShapedRun"/> + atlas metrics + <see cref="TextLayoutOptions"/> into symbol-local,
    /// anchor-relative <see cref="SymbolQuad"/>s in baked-pixel space (<see cref="OneEm"/> = 24px), with no
    /// text-size (placement applies <c>text-size/24</c>); point placement only. Y is up; line n's origin is
    /// <c>-n * LineHeightEm * OneEm</c>, the font's ascent reference, with the baseline
    /// <see cref="GlyphSdf.BaselineBelowReferencePx"/> below it. <c>Top</c>/<c>Bottom</c> anchor the layout-box
    /// edges; <c>Centre</c> anchors the optical centre (<c>docs/road-shields-design.md</c>). Non-obvious why:
    /// one forward pass bakes each line's justify shift as the line ends and a final pass adds the block-wide
    /// anchor + offset, so no per-line width array is needed and the no-wrap path allocates nothing.
    /// </summary>
    public static class TextQuadLayout
    {
        /// <summary>The baked-pixel size MapLibre bakes glyph-PBFs at; every em-valued <see cref="TextLayoutOptions"/> property converts via this.</summary>
        public const float OneEm = 24f;

        /// <summary><see cref="TextLayoutOptions.MaxWidthEm"/> fallback for a non-positive (e.g. zero-valued <see cref="TextLayoutOptions"/>) value.</summary>
        private const float DefaultMaxWidthEm = 10f;

        /// <summary><see cref="TextLayoutOptions.LineHeightEm"/> fallback for a non-positive (e.g. zero-valued <see cref="TextLayoutOptions"/>) value.</summary>
        private const float DefaultLineHeightEm = 1.2f;

        /// <summary>
        /// How far below a line's reference origin its OPTICAL centre sits: the baseline
        /// (<see cref="GlyphSdf.BaselineBelowReferencePx"/>) less half a cap height, exactly
        /// <c>26 − 8.5 = 17.5</c> baked px. The line-box midpoint sits higher, because the box top carries the
        /// ascent slack. Non-local invariant: <see cref="VerticalAnchorShiftPx"/>'s Centre case and
        /// <see cref="CurvedTextLayout"/> both apply it, so curved and centred point symbols align the same way.
        /// </summary>
        internal const float OpticalCentreBelowReferencePx = GlyphSdf.BaselineBelowReferencePx - 0.5f * GlyphSdf.NominalCapHeightEm * OneEm;

        /// <summary>
        /// The three cases <see cref="TextAnchor"/>'s vertical component ever resolves to. Kept as a
        /// dedicated enum rather than a bare 0/0.5/1 <c>float</c>, because
        /// <see cref="VerticalAnchor.Centre"/> is NOT the midpoint of <see cref="VerticalAnchor.Top"/>
        /// and <see cref="VerticalAnchor.Bottom"/> — a bare lerp factor would invite that very
        /// interpolation bug.
        /// </summary>
        private enum VerticalAnchor
        {
            Top,
            Centre,
            Bottom,
        }

        /// <summary>
        /// No-alloc caller-buffer overload (mirrors <see cref="CodepointTextShaper.Shape(in ShapingRequest, List{PositionedGlyph})"/>):
        /// clears and writes into <paramref name="output"/> instead of allocating a <c>List</c>.
        /// Guaranteed zero managed allocation on the steady no-wrap path once <paramref name="output"/>'s
        /// backing capacity has stabilized from a prior call (see <c>TextQuadLayoutAllocTests</c>, Unity-only).
        /// </summary>
        public static TextLayoutBounds Layout(ShapedRun run, IGlyphAtlasView atlas, in TextLayoutOptions options, List<SymbolQuad> output)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (atlas == null) throw new ArgumentNullException(nameof(atlas));
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();

            IReadOnlyList<PositionedGlyph> glyphs = run.Glyphs;
            int glyphCount = glyphs.Count;

            float maxWidthEm = options.MaxWidthEm > 0f ? options.MaxWidthEm : DefaultMaxWidthEm;
            float lineHeightEm = options.LineHeightEm > 0f ? options.LineHeightEm : DefaultLineHeightEm;
            float maxWidthPx = maxWidthEm * OneEm;
            float lineHeightPx = lineHeightEm * OneEm;
            float letterPx = options.LetterSpacingEm * OneEm;

            (float hAlign, VerticalAnchor vertical) = ResolveAlignFactors(options.Anchor);
            TextJustify resolvedJustify = ResolveJustify(options.Justify, options.Anchor);
            float justifyFactor = resolvedJustify switch
            {
                TextJustify.Left => 0f,
                TextJustify.Right => 1f,
                _ => 0.5f,
            };

            // RTL runs arrive in visual order and stay single-line: multi-line RTL wrap would need the
            // logical order the shaper does not expose.
            bool singleLine = run.Direction == TextDirection.RightToLeft;

            int lineIndex = 0;
            int lineOutputStart = 0;
            float penX = 0f;
            float baselineY = 0f;
            float currentLineWidth = 0f;
            bool lineHasContent = false;
            float blockWidth = 0f;

            int i = 0;
            if (singleLine)
            {
                while (i < glyphCount)
                {
                    float advance = PlaceGlyph(glyphs[i], atlas, penX, baselineY, lineIndex, output, out bool isWhitespace);
                    penX += advance;
                    if (!isWhitespace) currentLineWidth = penX;
                    penX += letterPx;
                    i++;
                }
            }
            else
            {
                while (i < glyphCount)
                {
                    bool tokenIsWhitespace = ClassifyWhitespace(glyphs[i], atlas);
                    if (!tokenIsWhitespace)
                    {
                        int wordEnd = i;
                        while (wordEnd < glyphCount && !ClassifyWhitespace(glyphs[wordEnd], atlas)) wordEnd++;

                        while (i < wordEnd)
                        {
                            float advance = PlaceGlyph(glyphs[i], atlas, penX, baselineY, lineIndex, output, out _);
                            penX += advance;
                            currentLineWidth = penX;
                            penX += letterPx;
                            i++;
                        }
                        lineHasContent = true;
                    }
                    else
                    {
                        int wsEnd = i;
                        while (wsEnd < glyphCount && ClassifyWhitespace(glyphs[wsEnd], atlas)) wsEnd++;
                        int nextWordEnd = wsEnd;
                        while (nextWordEnd < glyphCount && !ClassifyWhitespace(glyphs[nextWordEnd], atlas)) nextWordEnd++;

                        float wsWidth = MeasureRange(glyphs, i, wsEnd, atlas, letterPx);
                        float nextWordWidth = MeasureRange(glyphs, wsEnd, nextWordEnd, atlas, letterPx);
                        bool wouldOverflow = lineHasContent && nextWordEnd > wsEnd
                            && (currentLineWidth + letterPx + wsWidth + letterPx + nextWordWidth) > maxWidthPx;

                        if (wouldOverflow)
                        {
                            // Greedy word-wrap: the breaking space is dropped
                            // (never placed, doesn't advance the pen on either line).
                            ApplyJustifyToLine(output, lineOutputStart, currentLineWidth, justifyFactor);
                            blockWidth = math.max(blockWidth, currentLineWidth);

                            lineIndex++;
                            lineOutputStart = output.Count;
                            penX = 0f;
                            baselineY = -lineIndex * lineHeightPx;
                            currentLineWidth = 0f;
                            lineHasContent = false;
                            i = wsEnd;
                        }
                        else
                        {
                            while (i < wsEnd)
                            {
                                float advance = PlaceGlyph(glyphs[i], atlas, penX, baselineY, lineIndex, output, out _);
                                penX += advance;
                                penX += letterPx;
                                i++;
                            }
                        }
                    }
                }
            }

            // Finalize the last (or only) line.
            ApplyJustifyToLine(output, lineOutputStart, currentLineWidth, justifyFactor);
            blockWidth = math.max(blockWidth, currentLineWidth);
            int lineCount = lineIndex + 1;

            float2 offsetShiftEm = options.RadialOffset != 0f
                ? ComputeRadialOffset(hAlign, vertical, options.RadialOffset)
                : options.Offset;
            float2 offsetShift = offsetShiftEm * OneEm;

            // The per-line justify corrections baked in above plus this block-wide constant give every
            // quad its full anchor + justify + offset shift (see the class doc).
            float globalX = blockWidth * (justifyFactor - hAlign) + offsetShift.x;
            float globalY = VerticalAnchorShiftPx(vertical, lineCount, lineHeightPx) + offsetShift.y;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;

            for (int k = 0; k < output.Count; k++)
            {
                SymbolQuad q = output[k];
                float2 topLeft = new float2(q.TopLeft.x + globalX, q.TopLeft.y + globalY);
                float2 bottomRight = new float2(q.BottomRight.x + globalX, q.BottomRight.y + globalY);

                output[k] = new SymbolQuad
                {
                    TopLeft = topLeft,
                    BottomRight = bottomRight,
                    UvTopLeft = q.UvTopLeft,
                    UvBottomRight = q.UvBottomRight,
                    LineIndex = q.LineIndex,
                    Page = q.Page,
                };

                any = true;
                minX = math.min(minX, topLeft.x);
                maxX = math.max(maxX, bottomRight.x);
                minY = math.min(minY, bottomRight.y);
                maxY = math.max(maxY, topLeft.y);
            }

            float2 boundsMin = any ? new float2(minX, minY) : float2.zero;
            float2 boundsMax = any ? new float2(maxX, maxY) : float2.zero;

            return new TextLayoutBounds { Min = boundsMin, Max = boundsMax, LineCount = lineCount };
        }

        /// <summary>
        /// Missing-entry policy: a codepoint absent from the atlas (notdef) emits
        /// no quad and falls back to the shaped glyph's own <see cref="PositionedGlyph.XAdvance"/> —
        /// present glyphs use the atlas entry's integer <see cref="GlyphAtlasEntry.Advance"/> as the
        /// canonical pen step (cross-check: XAdvance ≈ round(Advance) for a resolved glyph).
        /// </summary>
        private static float PlaceGlyph(PositionedGlyph glyph, IGlyphAtlasView atlas, float penX, float baselineY, int lineIndex, List<SymbolQuad> output, out bool isWhitespace)
        {
            if (!atlas.TryGetEntry(glyph.FontId, glyph.AtlasCodepoint, out GlyphAtlasEntry entry))
            {
                isWhitespace = false;
                return glyph.XAdvance;
            }

            isWhitespace = IsWhitespaceEntry(entry);
            if (!isWhitespace)
            {
                float2 cellSize = entry.CellSize;
                // The glyph-PBF `Top` metric is top-referenced, so anchor the cell's TOP edge at
                // `baselineY + Top + Buffer` and grow down: no-descender glyphs then share one baseline.
                float leftX = penX + entry.Left - GlyphSdf.Buffer;
                float cellTopY = baselineY + entry.Top + GlyphSdf.Buffer;
                float2 atlasSize = atlas.Size;
                float2 uvMin = entry.AtlasOrigin / atlasSize;
                float2 uvMax = (entry.AtlasOrigin + cellSize) / atlasSize;

                output.Add(new SymbolQuad
                {
                    TopLeft = new float2(leftX, cellTopY),
                    BottomRight = new float2(leftX + cellSize.x, cellTopY - cellSize.y),
                    UvTopLeft = uvMin,
                    UvBottomRight = uvMax,
                    LineIndex = lineIndex,
                    Page = entry.Page,
                });
            }

            return entry.Advance;
        }

        /// <summary>
        /// Whitespace classification: NOT <see cref="SdfGlyph.HasBitmap"/> (the atlas entry does not
        /// carry that flag) — a padded SDF cell no larger than the bare buffer border on
        /// either axis (space's Cell(6,6) at Buffer=3) carries no visible glyph.
        /// </summary>
        private static bool IsWhitespaceEntry(in GlyphAtlasEntry entry)
            => entry.CellSize.x <= 2 * GlyphSdf.Buffer && entry.CellSize.y <= 2 * GlyphSdf.Buffer;

        private static bool ClassifyWhitespace(PositionedGlyph glyph, IGlyphAtlasView atlas)
            => atlas.TryGetEntry(glyph.FontId, glyph.AtlasCodepoint, out GlyphAtlasEntry entry) && IsWhitespaceEntry(entry);

        /// <summary>Lookahead-only Σadvance + interior-letter-spacing measurement over [start,end) — no placement, no output mutation.</summary>
        private static float MeasureRange(IReadOnlyList<PositionedGlyph> glyphs, int start, int end, IGlyphAtlasView atlas, float letterPx)
        {
            float width = 0f;
            for (int k = start; k < end; k++)
            {
                float advance = atlas.TryGetEntry(glyphs[k].FontId, glyphs[k].AtlasCodepoint, out GlyphAtlasEntry entry) ? entry.Advance : glyphs[k].XAdvance;
                width += advance;
                if (k < end - 1) width += letterPx;
            }
            return width;
        }

        /// <summary>Bakes -justifyFactor*lineWidth into every quad already emitted for the line starting at output index <paramref name="lineOutputStart"/>.</summary>
        private static void ApplyJustifyToLine(List<SymbolQuad> output, int lineOutputStart, float lineWidth, float justifyFactor)
        {
            if (justifyFactor == 0f) return;

            float deltaX = -justifyFactor * lineWidth;
            for (int k = lineOutputStart; k < output.Count; k++)
            {
                SymbolQuad q = output[k];
                output[k] = new SymbolQuad
                {
                    TopLeft = new float2(q.TopLeft.x + deltaX, q.TopLeft.y),
                    BottomRight = new float2(q.BottomRight.x + deltaX, q.BottomRight.y),
                    UvTopLeft = q.UvTopLeft,
                    UvBottomRight = q.UvBottomRight,
                    LineIndex = q.LineIndex,
                    Page = q.Page,
                };
            }
        }

        /// <summary>hAlign: Left*=0, Right*=1, else .5. vertical: Top*-&gt;Top, Bottom*-&gt;Bottom, else Centre.</summary>
        private static (float hAlign, VerticalAnchor vertical) ResolveAlignFactors(TextAnchor anchor)
        {
            float hAlign = anchor switch
            {
                TextAnchor.Left or TextAnchor.TopLeft or TextAnchor.BottomLeft => 0f,
                TextAnchor.Right or TextAnchor.TopRight or TextAnchor.BottomRight => 1f,
                _ => 0.5f,
            };
            VerticalAnchor vertical = anchor switch
            {
                TextAnchor.Top or TextAnchor.TopLeft or TextAnchor.TopRight => VerticalAnchor.Top,
                TextAnchor.Bottom or TextAnchor.BottomLeft or TextAnchor.BottomRight => VerticalAnchor.Bottom,
                _ => VerticalAnchor.Centre,
            };
            return (hAlign, vertical);
        }

        /// <summary>
        /// The whole vertical anchoring rule. <see cref="VerticalAnchor.Top"/> and <see cref="VerticalAnchor.Bottom"/>
        /// anchor the layout-box edges; <see cref="VerticalAnchor.Centre"/> anchors the midpoint of the first and
        /// last lines' optical centres, hence <c>(lineCount - 1)</c>, so the block moves by one constant
        /// (<see cref="OpticalCentreBelowReferencePx"/>) whatever the line count.
        /// </summary>
        private static float VerticalAnchorShiftPx(VerticalAnchor vertical, int lineCount, float lineHeightPx) => vertical switch
        {
            VerticalAnchor.Top => 0f,
            VerticalAnchor.Bottom => lineCount * lineHeightPx,
            _ => OpticalCentreBelowReferencePx + (lineCount - 1) * lineHeightPx * 0.5f,
        };

        /// <summary>Auto resolves from the anchor: Left*-&gt;Left, Right*-&gt;Right, else Center.</summary>
        private static TextJustify ResolveJustify(TextJustify justify, TextAnchor anchor)
        {
            if (justify != TextJustify.Auto) return justify;
            return anchor switch
            {
                TextAnchor.Left or TextAnchor.TopLeft or TextAnchor.BottomLeft => TextJustify.Left,
                TextAnchor.Right or TextAnchor.TopRight or TextAnchor.BottomRight => TextJustify.Right,
                _ => TextJustify.Center,
            };
        }

        /// <summary>
        /// Radial offset (ems) from the anchor: an axis anchor pushes along its axis, a corner anchor splits
        /// it diagonally (RadialOffset/sqrt2 per axis), and Center gives (0,0). Signs push away from the
        /// anchored edge: a Left anchor pushes +x, a Top anchor pushes -y. Pinned by a golden.
        /// </summary>
        private static float2 ComputeRadialOffset(float hAlign, VerticalAnchor vertical, float radialOffsetEm)
        {
            float xSign = hAlign == 0f ? 1f : (hAlign == 1f ? -1f : 0f);
            float ySign = vertical == VerticalAnchor.Top ? -1f : (vertical == VerticalAnchor.Bottom ? 1f : 0f);

            if (xSign != 0f && ySign != 0f)
            {
                float diag = radialOffsetEm / math.SQRT2;
                return new float2(xSign * diag, ySign * diag);
            }
            return new float2(xSign * radialOffsetEm, ySign * radialOffsetEm);
        }
    }
}
