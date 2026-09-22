// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Text shaper: pure clean-room C#, no HarfBuzz. Pipeline: split the
    /// source string into codepoints -> detect direction -> for an RTL run, apply Arabic joining
    /// (<see cref="ArabicJoining"/>) to reach presentation-form atlas codepoints -> attach advances from
    /// <see cref="IGlyphMetricsProvider"/> -> reorder to visual order (<see cref="BidiReorder"/>).
    ///
    /// Render-path-agnostic: no <c>Mesh</c>/<c>MeshData</c>/<c>IRenderLayer</c>, no anchor/offset/quad
    /// assumptions — layout owns those. Not on any per-frame hot path; shaping runs at tile-layout time.
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
        /// No-GC caller-buffer overload: writes positioned glyphs directly into
        /// <paramref name="output"/> (cleared first; reused across calls by the caller) instead of
        /// allocating a <see cref="ShapedRun"/>. Guaranteed zero managed allocation on the steady LTR
        /// path (the common map-symbol case: Latin/Cyrillic/Greek/digits/punctuation) once
        /// <paramref name="output"/>'s backing capacity has stabilized from a prior call — no
        /// intermediate codepoint/cluster lists, no <see cref="ShapedRun"/> class instance.
        ///
        /// <para>
        /// RTL (Arabic joining + bidi) still runs through the existing allocating helpers internally
        /// (<see cref="ArabicJoining"/>/<see cref="BidiReorder"/> — out of the zero-alloc scope, which
        /// is the steady/cached path only) but still avoids the <see cref="ShapedRun"/> allocation by
        /// copying its glyphs into the caller's buffer.
        /// </para>
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
        /// Detects a single strong direction for the whole run. A run mixing strong RTL
        /// and strong LTR codepoints throws — full UAX #9 (mixed-direction) bidi is a deferred
        /// follow-up (see <see cref="BidiReorder"/>). Direction-neutral text (digits/punctuation/
        /// whitespace only) defaults to left-to-right.
        ///
        /// Reads codepoints directly off <paramref name="text"/> (no intermediate list): this scan must be
        /// allocation-free so the caller-buffer <see cref="Shape(in ShapingRequest, List{PositionedGlyph})"/>
        /// overload has no per-call heap traffic on its steady LTR path.
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
