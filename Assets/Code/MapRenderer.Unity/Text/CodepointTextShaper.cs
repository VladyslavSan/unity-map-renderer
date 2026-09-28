// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Text;
using MapRenderer.Unity.Text.Bidi;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Text shaper in plain C#, no HarfBuzz: split into code points -> resolve bidi levels
    /// (<see cref="BidiResolver"/>) -> Arabic joining (<see cref="ArabicJoining"/>) -> mirror paired glyphs on odd
    /// levels (L4) -> advances from <see cref="IGlyphMetricsProvider"/>. It emits glyphs in LOGICAL order with an
    /// embedding level per glyph. The layouts place them in visual order (<see cref="BidiReorder"/>).
    /// An instance holds scratch, so it serves one thread at a time.
    /// </summary>
    public sealed class CodepointTextShaper
    {
        private readonly List<ArabicShapedUnit> _joinedUnits = new List<ArabicShapedUnit>();
        private int[] _codepoints = new int[64];
        private int[] _clusters = new int[64];
        private byte[] _resolvedLevels = new byte[64];

        /// <summary>Allocating overload: shapes <paramref name="request"/> into a fresh <see cref="ShapedRun"/>.</summary>
        public ShapedRun Shape(in ShapingRequest request)
        {
            int length = request.Text?.Length ?? 0;
            var glyphs = new List<PositionedGlyph>(length);
            var levels = new List<byte>(length);
            Shape(in request, glyphs, levels);
            return new ShapedRun { Glyphs = glyphs, Levels = levels };
        }

        /// <summary>
        /// Caller-buffer overload: writes positioned glyphs into <paramref name="output"/> and one level per
        /// glyph into <paramref name="levels"/> (both cleared first). A run whose levels are all 0 leaves
        /// <paramref name="levels"/> empty. Allocates nothing once the capacities have stabilized.
        /// A lone surrogate shapes as U+FFFD.
        /// </summary>
        /// <exception cref="ArgumentException">The text needs bidi resolving and is longer than <see cref="BidiResolver.MaxCodepoints"/> code points.</exception>
        public void Shape(in ShapingRequest request, List<PositionedGlyph> output, List<byte> levels)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (levels == null) throw new ArgumentNullException(nameof(levels));
            output.Clear();
            levels.Clear();

            string text = request.Text ?? string.Empty;
            int count = SplitIntoCodepoints(text);
            BidiResolver.Resolve(new ReadOnlySpan<int>(_codepoints, 0, count), new Span<byte>(_resolvedLevels, 0, count));

            bool anyBidi = false;
            for (int i = 0; i < count && !anyBidi; i++) anyBidi = _resolvedLevels[i] != 0;
            if (!anyBidi)
            {
                for (int i = 0; i < count; i++)
                    output.Add(BuildGlyph((uint)_codepoints[i], _clusters[i], 0, request.Metrics));
                return;
            }

            ArabicJoining.Shape(text, _joinedUnits);
            int cursor = 0;
            for (int i = 0; i < _joinedUnits.Count; i++)
            {
                ArabicShapedUnit unit = _joinedUnits[i];
                while (cursor < count && _clusters[cursor] < unit.Cluster) cursor++;
                byte level = _resolvedLevels[cursor];
                output.Add(BuildGlyph(unit.AtlasCodepoint, unit.Cluster, level, request.Metrics));
                levels.Add(level);
            }
        }

        /// <summary>Decodes UTF-16 <paramref name="text"/> into the code point and cluster scratch. Returns the code point count.</summary>
        private int SplitIntoCodepoints(string text)
        {
            if (_codepoints.Length < text.Length)
            {
                int capacity = text.Length > _codepoints.Length * 2 ? text.Length : _codepoints.Length * 2;
                _codepoints = new int[capacity];
                _clusters = new int[capacity];
                _resolvedLevels = new byte[capacity];
            }

            int count = 0;
            for (int i = 0; i < text.Length;)
            {
                _codepoints[count] = DecodeCodepoint(text, i, out int consumed);
                _clusters[count] = i;
                count++;
                i += consumed;
            }
            return count;
        }

        /// <summary>Decodes the code point at UTF-16 <paramref name="index"/>. A lone surrogate decodes as U+FFFD.</summary>
        internal static int DecodeCodepoint(string text, int index, out int consumed)
        {
            char unit = text[index];
            if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                consumed = 2;
                return char.ConvertToUtf32(unit, text[index + 1]);
            }
            consumed = 1;
            return char.IsSurrogate(unit) ? 0xFFFD : unit;
        }

        /// <summary>
        /// Builds one glyph. On an odd <paramref name="level"/> a mirrored code point (L4) replaces the source
        /// one when the metrics hold a glyph for it; otherwise the source glyph stays.
        /// </summary>
        private static PositionedGlyph BuildGlyph(uint atlasCodepoint, int cluster, byte level, IGlyphMetricsProvider metrics)
        {
            float advance = 0f;
            int fontId = 0;
            if (metrics != null) metrics.TryResolveGlyph(atlasCodepoint, out advance, out fontId);
            if ((level & 1) == 1 && metrics != null)
            {
                uint mirror = (uint)UnicodeBidiData.MirrorOf((int)atlasCodepoint);
                if (mirror != atlasCodepoint && metrics.TryResolveGlyph(mirror, out float mirrorAdvance, out int mirrorFontId))
                {
                    atlasCodepoint = mirror;
                    advance = mirrorAdvance;
                    fontId = mirrorFontId;
                }
            }
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
    }
}
