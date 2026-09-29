// One construction facade for the eight style-property types, engine-free for Tools/core-tests. It names no
// `Color`, so it never picks which of the suite's two Color bindings a call site meant.

using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;
using Background = MapRenderer.Unity.Style.Background;
using Fill = MapRenderer.Unity.Style.Fill;
using FillExtrusion = MapRenderer.Unity.Style.FillExtrusion;
using Line = MapRenderer.Unity.Style.Line;
using SymbolStyle = MapRenderer.Unity.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Builds a style-family's paint/layout properties from a JSON literal, or the spec defaults when
    /// <paramref name="json"/> is absent. One method per (kind, family) pair that occurs in the suite —
    /// no FillExtrusion layout or Background layout, because no test needs one.
    /// </summary>
    public static class TestStyle
    {
        /// <summary>Parses a whole style document from its JSON text with the host defaults.</summary>
        public static StyleDocument Document(string json) => StyleParser.Parse(json);

        /// <summary>Parses <c>fill</c> paint properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static Fill.PaintProperties FillPaint(string json = null)
            => Fill.PaintProperties.Parse(json != null ? JsonParser.Parse(json) : null);

        /// <summary>Parses <c>fill</c> layout properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static Fill.LayoutProperties FillLayout(string json = null)
            => Fill.LayoutProperties.Parse(json != null ? JsonParser.Parse(json) : null);

        /// <summary>Parses <c>line</c> paint properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static Line.PaintProperties LinePaint(string json = null)
            => Line.PaintProperties.Parse(json != null ? JsonParser.Parse(json) : null);

        /// <summary>Parses <c>line</c> layout properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static Line.LayoutProperties LineLayout(string json = null)
            => Line.LayoutProperties.Parse(json != null ? JsonParser.Parse(json) : null);

        /// <summary>Parses <c>symbol</c> paint properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static SymbolStyle.PaintProperties SymbolPaint(string json = null)
            => SymbolStyle.PaintProperties.Parse(json != null ? JsonParser.Parse(json) : null);

        /// <summary>Parses <c>symbol</c> layout properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static SymbolStyle.LayoutProperties SymbolLayout(string json = null)
            => SymbolStyle.LayoutProperties.Parse(json != null ? JsonParser.Parse(json) : null);

        /// <summary>Parses <c>fill-extrusion</c> paint properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static FillExtrusion.PaintProperties FillExtrusionPaint(string json = null)
            => FillExtrusion.PaintProperties.Parse(json != null ? JsonParser.Parse(json) : null);

        /// <summary>Parses <c>background</c> paint properties, or the defaults when <paramref name="json"/> is null.</summary>
        public static Background.PaintProperties BackgroundPaint(string json = null)
            => Background.PaintProperties.Parse(json != null ? JsonParser.Parse(json) : null);
    }
}
