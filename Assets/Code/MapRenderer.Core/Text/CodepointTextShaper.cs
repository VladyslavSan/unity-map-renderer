// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Text shaper in plain C#, no HarfBuzz: split into codepoints -> detect direction -> for an RTL run,
    /// Arabic joining (<see cref="ArabicJoining"/>) -> advances from <see cref="IGlyphMetricsProvider"/> ->
    /// visual order (<see cref="BidiReorder"/>). Layout owns anchors, offsets and quads. Shaping runs at
    /// tile-layout time, not per frame.
    /// </summary>
    public sealed class CodepointTextShaper
    {
        public ShapedRun Shape(in ShapingRequest request)
        {
            string text = request.Text ?? string.Empty;
            TextDirection direction = DetermineDirection(text);

            var glyphs = new List<PositionedGlyph>(text.Length);
            if (direction == TextDirection.RightToLeft)
            {
                var codepoints = new List<uint>(text.Length);
                var clusters = new List<int>(text.Length);
                SplitIntoCodepoints(text, codepoints, clusters);

                IReadOnlyList<ArabicShapedUnit> shaped = ArabicJoining.Shape(codepoints, clusters);
                foreach (ArabicShapedUnit unit in shaped)
                {
                    glyphs.Add(BuildGlyph(unit.AtlasCodepoint, unit.Cluster, request.Metrics));
                }
            }
            else
            {
                AppendLeftToRightGlyphs(text, request.Metrics, glyphs);
            }

            IReadOnlyList<PositionedGlyph> visualOrder = BidiReorder.ToVisualOrder(glyphs, direction);
            return new ShapedRun { Glyphs = visualOrder, Direction = direction };
        }

        /// <summary>
        /// Caller-buffer overload: writes positioned glyphs into <paramref name="output"/> (cleared first)
        /// instead of allocating a <see cref="ShapedRun"/>. The steady LTR path allocates nothing once
        /// <paramref name="output"/>'s capacity has stabilized. RTL still runs through the allocating
        /// <see cref="ArabicJoining"/>/<see cref="BidiReorder"/> helpers.
        /// </summary>
        /// <returns>The resolved direction (mirrors <see cref="ShapedRun.Direction"/>).</returns>
        public TextDirection Shape(in ShapingRequest request, List<PositionedGlyph> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();

            string text = request.Text ?? string.Empty;
            TextDirection direction = DetermineDirection(text);

            if (direction == TextDirection.RightToLeft)
            {
                ShapedRun run = Shape(in request);
                for (int i = 0; i < run.Glyphs.Count; i++)
                {
                    output.Add(run.Glyphs[i]);
                }
                return direction;
            }

            AppendLeftToRightGlyphs(text, request.Metrics, output);
            return direction;
        }

        private static void AppendLeftToRightGlyphs(string text, IGlyphMetricsProvider metrics, List<PositionedGlyph> output)
        {
            for (int i = 0; i < text.Length;)
            {
                int codepoint = char.ConvertToUtf32(text, i);
                int consumed = char.IsSurrogatePair(text, i) ? 2 : 1;
                output.Add(BuildGlyph((uint)codepoint, i, metrics));
                i += consumed;
            }
        }

        private static PositionedGlyph BuildGlyph(uint atlasCodepoint, int cluster, IGlyphMetricsProvider metrics)
        {
            float advance = 0f;
            int fontId = 0;
            if (metrics != null) metrics.TryResolveGlyph(atlasCodepoint, out advance, out fontId);
            return new PositionedGlyph
            {
                AtlasCodepoint = atlasCodepoint,
                FontId = fontId,
                XAdvance = advance,
                YAdvance = 0f,
                XOffset = 0f,
                YOffset = 0f,
                Cluster = cluster,
            };
        }

        private static void SplitIntoCodepoints(string text, List<uint> codepoints, List<int> clusters)
        {
            for (int i = 0; i < text.Length;)
            {
                int codepoint = char.ConvertToUtf32(text, i);
                int consumed = char.IsSurrogatePair(text, i) ? 2 : 1;
                codepoints.Add((uint)codepoint);
                clusters.Add(i);
                i += consumed;
            }
        }

        /// <summary>
        /// Detects a single strong direction for the whole run. A run mixing strong RTL and strong LTR
        /// codepoints throws, because full UAX #9 bidi is not implemented (see <see cref="BidiReorder"/>).
        /// Direction-neutral text defaults to left-to-right. It reads codepoints straight off
        /// <paramref name="text"/>, so the scan stays allocation-free.
        /// </summary>
        private static TextDirection DetermineDirection(string text)
        {
            bool hasRtl = false;
            bool hasLtr = false;
            for (int i = 0; i < text.Length;)
            {
                int codepoint = char.ConvertToUtf32(text, i);
                int consumed = char.IsSurrogatePair(text, i) ? 2 : 1;
                uint c = (uint)codepoint;
                if (IsStrongRightToLeft(c)) hasRtl = true;
                else if (IsStrongLeftToRight(c)) hasLtr = true;
                i += consumed;
            }

            if (hasRtl && hasLtr)
            {
                throw new NotSupportedException(
                    "CodepointTextShaper: mixed strong-direction text (RTL + LTR) is not supported by " +
                    "this single-run bidi. Full UAX #9 bidi is a deferred follow-up that " +
                    "will adopt a managed ICU-derived library.");
            }

            return hasRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight;
        }

        // Rough strong-direction classification by Unicode block — enough for the single-run scope
        // (Arabic-vs-Latin discrimination); NOT a full UAX #9 bidi-class table.
        private static bool IsStrongRightToLeft(uint c)
            => (c >= 0x0590 && c <= 0x08FF)   // Hebrew, Arabic, Syriac, Thaana, NKo, Arabic Extended-A, etc.
            || (c >= 0xFB1D && c <= 0xFDFF)   // Hebrew presentation forms + Arabic Presentation Forms-A
            || (c >= 0xFE70 && c <= 0xFEFF);  // Arabic Presentation Forms-B

        private static bool IsStrongLeftToRight(uint c)
            => (c >= 0x0041 && c <= 0x005A) || (c >= 0x0061 && c <= 0x007A) // Basic Latin letters
            || (c >= 0x00C0 && c <= 0x02AF)   // Latin-1 Supplement / Latin Extended-A/B / IPA
            || (c >= 0x0370 && c <= 0x058F);  // Greek, Cyrillic, Armenian
    }
}
