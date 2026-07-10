// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// S19: turns a <see cref="ShapedRun"/> + atlas metrics + <see cref="TextLayoutOptions"/> into
    /// label-local, anchor-relative <see cref="SymbolQuad"/>s in baked-pixel space (<see cref="OneEm"/>
    /// = 24px — the fixed size MapLibre bakes glyph-PBFs at). Deliberately takes NO text-size parameter
    /// (T8a): layout is size-independent, S20 applies the zoom-dependent <c>text-size/24</c> screen
    /// scale and builds the actual <c>Mesh</c>/vertices per frame. Point placement only
    /// (<c>symbol-placement: line</c> is a follow-up, S19 stage doc §2.3).
    ///
    /// <para>
    /// <b>Baseline convention:</b> y-up; line 0's baseline is at y=0, line n's baseline is at
    /// <c>-n * LineHeightEm * OneEm</c>. The unanchored block spans <c>y ∈ [-blockHeight, 0]</c> before
    /// <see cref="TextLayoutOptions.Anchor"/>/<see cref="TextLayoutOptions.Offset"/> translate it.
    /// </para>
    ///
    /// <para>
    /// <b>Algorithm shape (why no auxiliary line-width array):</b> anchor/justify need each line's own
    /// trimmed width and the block's max line width, but only the max is known once ALL lines have been
    /// scanned. Rather than buffer per-line widths, this walks the glyphs in a SINGLE forward pass,
    /// baking the justify shift into a line's already-emitted quads the instant that line ends (its own
    /// width is known then), and defers only the two block-wide constants (anchor + offset, which don't
    /// vary per line) to one final O(quadCount) pass over the already-emitted <c>output</c> list. Every
    /// step here is an in-place List index read/write or scalar arithmetic — no heap allocation on the
    /// no-wrap steady path (see <c>TextQuadLayoutAllocTests</c>, Unity-only).
    /// </para>
    /// </summary>
    public static class TextQuadLayout
    {
        /// <summary>The baked-pixel size MapLibre bakes glyph-PBFs at; every em-valued <see cref="TextLayoutOptions"/> property converts via this.</summary>
        public const float OneEm = 24f;

        /// <summary><see cref="TextLayoutOptions.MaxWidthEm"/> fallback for a non-positive (e.g. zero-valued <see cref="TextLayoutOptions"/>) value.</summary>
        private const float DefaultMaxWidthEm = 10f;

        /// <summary><see cref="TextLayoutOptions.LineHeightEm"/> fallback for a non-positive (e.g. zero-valued <see cref="TextLayoutOptions"/>) value.</summary>
        private const float DefaultLineHeightEm = 1.2f;

        /// <summary>Allocating overload: lays out <paramref name="run"/> and returns a new <see cref="TextLayoutResult"/> (quads + block bbox + line count).</summary>
        public static TextLayoutResult Layout(ShapedRun run, IGlyphAtlasView atlas, in TextLayoutOptions options)
        {
            var quads = new List<SymbolQuad>(run?.Glyphs?.Count ?? 0);
            TextLayoutBounds bounds = Layout(run, atlas, in options, quads);
            return new TextLayoutResult
            {
                Quads = quads,
                BoundsMin = bounds.Min,
                BoundsMax = bounds.Max,
                LineCount = bounds.LineCount,
            };
        }

        /// <summary>
        /// No-alloc caller-buffer overload (mirrors <see cref="CodepointTextShaper.Shape(in ShapingRequest, List{PositionedGlyph})"/>):
        /// clears and writes into <paramref name="output"/> instead of allocating a <see cref="TextLayoutResult"/>/<c>List</c>.
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

            (float hAlign, float vAlign) = ResolveAlignFactors(options.Anchor);
            TextJustify resolvedJustify = ResolveJustify(options.Justify, options.Anchor);
            float justifyFactor = resolvedJustify switch
            {
                TextJustify.Left => 0f,
                TextJustify.Right => 1f,
                _ => 0.5f,
            };

            // Decision 4 (S19 stage doc): RTL runs are already visual-order (S18) and are single-line
            // only in S19 — multi-line RTL wrap needs logical order S18 doesn't currently expose.
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
                            // Greedy word-wrap (decision 3 / plan (e)): the breaking space is dropped
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
            float blockHeight = lineCount * lineHeightPx;

            float2 offsetShiftEm = options.RadialOffset != 0f
                ? ComputeRadialOffset(hAlign, vAlign, options.RadialOffset)
                : options.Offset;
            float2 offsetShift = offsetShiftEm * OneEm;

            // The per-line justify correction (baked in above, per line, as soon as each line's own
            // width was known) plus this single block-wide constant reproduces the full
            // anchor+justify+offset shift for every quad — see the class doc's algorithm-shape note.
            float globalX = blockWidth * (justifyFactor - hAlign) + offsetShift.x;
            float globalY = vAlign * blockHeight + offsetShift.y;

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
        /// Missing-entry policy (S18 §6.3 mirrored): a codepoint absent from the atlas (notdef) emits
        /// no quad and falls back to the shaped glyph's own <see cref="PositionedGlyph.XAdvance"/> —
        /// present glyphs use the atlas entry's integer <see cref="GlyphAtlasEntry.Advance"/> as the
        /// canonical pen step (cross-check: XAdvance ≈ round(Advance) for a resolved glyph).
        /// </summary>
        private static float PlaceGlyph(PositionedGlyph glyph, IGlyphAtlasView atlas, float penX, float baselineY, int lineIndex, List<SymbolQuad> output, out bool isWhitespace)
        {
            if (!atlas.TryGetEntry(glyph.AtlasCodepoint, out GlyphAtlasEntry entry))
            {
                isWhitespace = false;
                return glyph.XAdvance;
            }

            isWhitespace = IsWhitespaceEntry(entry);
            if (!isWhitespace)
            {
                float2 cellSize = entry.CellSize;
                // The glyph-PBF `Top` metric is TOP-referenced (the glyph's top edge sits `-Top` below a
                // fixed top/ascent reference, so `-Top + Height` is constant across a font's baseline-resting
                // glyphs). So anchor the cell's TOP edge at `baselineY + Top + Buffer` and grow DOWNWARD by
                // the cell height — every no-descender glyph's bottom then lands on the same baseline
                // regardless of its own height (short x-height letters and tall caps/ascenders align), and
                // descenders extend below it. (Anchoring the BOTTOM at `baselineY + Top + Buffer` and growing
                // up — the earlier bug — made short letters droop below the baseline.)
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
                });
            }

            return entry.Advance;
        }

        /// <summary>
        /// Whitespace classification (plan (h)): NOT <see cref="SdfGlyph.HasBitmap"/> (S18's atlas
        /// entry doesn't carry that flag) — a padded SDF cell no larger than the bare buffer border on
        /// either axis (space's Cell(6,6) at Buffer=3) carries no visible glyph.
        /// </summary>
        private static bool IsWhitespaceEntry(in GlyphAtlasEntry entry)
            => entry.CellSize.x <= 2 * GlyphSdf.Buffer && entry.CellSize.y <= 2 * GlyphSdf.Buffer;

        private static bool ClassifyWhitespace(PositionedGlyph glyph, IGlyphAtlasView atlas)
            => atlas.TryGetEntry(glyph.AtlasCodepoint, out GlyphAtlasEntry entry) && IsWhitespaceEntry(entry);

        /// <summary>Lookahead-only Σadvance + interior-letter-spacing measurement over [start,end) — no placement, no output mutation.</summary>
        private static float MeasureRange(IReadOnlyList<PositionedGlyph> glyphs, int start, int end, IGlyphAtlasView atlas, float letterPx)
        {
            float width = 0f;
            for (int k = start; k < end; k++)
            {
                float advance = atlas.TryGetEntry(glyphs[k].AtlasCodepoint, out GlyphAtlasEntry entry) ? entry.Advance : glyphs[k].XAdvance;
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
                };
            }
        }

        /// <summary>hAlign: Left*=0, Right*=1, else .5. vAlign: Top*=0, Bottom*=1, else .5 (plan (b)).</summary>
        private static (float hAlign, float vAlign) ResolveAlignFactors(TextAnchor anchor)
        {
            float hAlign = anchor switch
            {
                TextAnchor.Left or TextAnchor.TopLeft or TextAnchor.BottomLeft => 0f,
                TextAnchor.Right or TextAnchor.TopRight or TextAnchor.BottomRight => 1f,
                _ => 0.5f,
            };
            float vAlign = anchor switch
            {
                TextAnchor.Top or TextAnchor.TopLeft or TextAnchor.TopRight => 0f,
                TextAnchor.Bottom or TextAnchor.BottomLeft or TextAnchor.BottomRight => 1f,
                _ => 0.5f,
            };
            return (hAlign, vAlign);
        }

        /// <summary>Auto resolves from anchor (plan (d)): Left*-&gt;Left, Right*-&gt;Right, else Center.</summary>
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
        /// Radial offset (ems), resolved from the anchor (plan (c)): pure axis anchors (Left/Right/
        /// Top/Bottom) push straight along that axis; corner anchors split into a diagonal
        /// (RadialOffset/sqrt2 on each axis, preserving total magnitude); Center has no direction (0,0).
        /// Signs push AWAY from the anchored edge — e.g. a Left anchor (block's left edge at the
        /// anchor, text extending +x) pushes further +x; a Top anchor (text extending -y) pushes
        /// further -y. Self-pinned by the T3 golden (no MapLibre source consulted); flagged for the
        /// S20 visual reconcile.
        /// </summary>
        private static float2 ComputeRadialOffset(float hAlign, float vAlign, float radialOffsetEm)
        {
            float xSign = hAlign == 0f ? 1f : (hAlign == 1f ? -1f : 0f);
            float ySign = vAlign == 0f ? -1f : (vAlign == 1f ? 1f : 0f);

            if (xSign != 0f && ySign != 0f)
            {
                float diag = radialOffsetEm / math.SQRT2;
                return new float2(xSign * diag, ySign * diag);
            }
            return new float2(xSign * radialOffsetEm, ySign * radialOffsetEm);
        }
    }
}
