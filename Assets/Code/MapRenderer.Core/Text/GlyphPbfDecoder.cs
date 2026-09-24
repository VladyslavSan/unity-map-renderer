using System.Collections.Generic;
using MapRenderer.Core.Protobuf;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Decodes a MapLibre glyph-PBF range response (<c>{fontstack}/{range}.pbf</c>) into
    /// <see cref="GlyphPbfRange"/> with <see cref="ProtobufReader"/>, per the public wire schema: glyphs
    /// { stacks = 1 }; fontstack { name = 1; range = 2; glyphs = 3 }; glyph { id = 1; bitmap = 2; width = 3;
    /// height = 4; sint32 left = 5; sint32 top = 6; advance = 7 }. The zigzag <c>left</c>/<c>top</c> decode
    /// via <see cref="ProtobufReader.ReadSInt64"/>; a missing <c>bitmap</c> decodes to null.
    /// </summary>
    public static class GlyphPbfDecoder
    {
        // glyphs message
        private const int GlyphsStacks = 1;

        // fontstack message
        private const int FontStackName = 1;
        private const int FontStackRange = 2;
        private const int FontStackGlyphList = 3;

        // glyph message
        private const int GlyphId = 1;
        private const int GlyphBitmap = 2;
        private const int GlyphWidth = 3;
        private const int GlyphHeight = 4;
        private const int GlyphLeft = 5;
        private const int GlyphTop = 6;
        private const int GlyphAdvance = 7;

        public static GlyphPbfRange Decode(byte[] data)
        {
            var stacks = new List<FontStackGlyphs>();
            var r = new ProtobufReader(data);
            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                if (field == GlyphsStacks && wt == 2)
                {
                    var (s, e) = r.ReadLengthDelimited();
                    stacks.Add(DecodeFontStack(r.Slice(s, e)));
                }
                else
                {
                    r.SkipField(wt);
                }
            }
            return new GlyphPbfRange { Stacks = stacks };
        }

        private static FontStackGlyphs DecodeFontStack(ProtobufReader r)
        {
            string name = null;
            string range = null;
            var glyphs = new Dictionary<uint, SdfGlyph>();

            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case FontStackName when wt == 2:
                        name = r.ReadString();
                        break;
                    case FontStackRange when wt == 2:
                        range = r.ReadString();
                        break;
                    case FontStackGlyphList when wt == 2:
                    {
                        var (s, e) = r.ReadLengthDelimited();
                        SdfGlyph glyph = DecodeGlyph(r.Slice(s, e));
                        glyphs[glyph.Codepoint] = glyph;
                        break;
                    }
                    default:
                        r.SkipField(wt);
                        break;
                }
            }

            var (start, end) = ParseRange(range);
            return new FontStackGlyphs
            {
                Name = name,
                RangeStart = start,
                RangeEnd = end,
                Glyphs = glyphs,
            };
        }

        private static SdfGlyph DecodeGlyph(ProtobufReader r)
        {
            uint id = 0;
            byte[] bitmap = null;
            int width = 0, height = 0, left = 0, top = 0, advance = 0;

            while (r.HasMore)
            {
                uint tag = r.ReadTag();
                int field = ProtobufReader.FieldNumber(tag);
                int wt = ProtobufReader.WireType(tag);
                switch (field)
                {
                    case GlyphId when wt == 0:
                        id = r.ReadUInt32();
                        break;
                    case GlyphBitmap when wt == 2:
                        bitmap = r.ReadBytes();
                        break;
                    case GlyphWidth when wt == 0:
                        width = (int)r.ReadUInt32();
                        break;
                    case GlyphHeight when wt == 0:
                        height = (int)r.ReadUInt32();
                        break;
                    case GlyphLeft when wt == 0:
                        left = (int)r.ReadSInt64();
                        break;
                    case GlyphTop when wt == 0:
                        top = (int)r.ReadSInt64();
                        break;
                    case GlyphAdvance when wt == 0:
                        advance = (int)r.ReadUInt32();
                        break;
                    default:
                        r.SkipField(wt);
                        break;
                }
            }

            return new SdfGlyph
            {
                Codepoint = id,
                Width = width,
                Height = height,
                Left = left,
                Top = top,
                Advance = advance,
                Bitmap = bitmap,
            };
        }

        /// <summary>Parses the PBF "start-end" range string (e.g. "0-255") into inclusive bounds.
        /// Tolerant of a malformed/absent range: returns (0, 0) rather than throwing.</summary>
        private static (int start, int end) ParseRange(string range)
        {
            if (string.IsNullOrEmpty(range)) return (0, 0);
            int dash = range.IndexOf('-');
            if (dash < 0) return (0, 0);
            int.TryParse(range.Substring(0, dash), out int start);
            int.TryParse(range.Substring(dash + 1), out int end);
            return (start, end);
        }
    }
}
