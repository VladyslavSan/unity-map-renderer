// Unity EditMode only — Stage G-V0 (fill), extended by G-V1 (symbol text). The declarative visual-test
// authoring kit.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// The LAYER half of the kit. G-V0 shipped fill-only (plan §1 decision 5, §10 scope fence); G-V1 adds
// SymbolTextVisualLayer (point labels only) as a sibling VisualLayer subclass — no VisualScene JSON-assembly
// change (VisualScene's glyph wiring + readiness spin ARE new, but the "layers" array assembly itself did
// not change shape). A line layer kind is still a future VisualLayer subclass.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A style layer bound to a source BY ID — the composer never resolves the binding itself; a dangling
    /// id is left for production <c>MapView.SetStyle</c> to skip, exactly as it resolves a real style
    /// (<c>MapView.cs:303-313</c>) — that is what makes T-Binding an executable seam.
    /// </summary>
    internal abstract class VisualLayer
    {
        /// <summary>The style-JSON layer id.</summary>
        public string Id { get; }

        /// <summary>The bound source id, stored VERBATIM — including a dangling one. Set via <see cref="Source"/>.</summary>
        public string SourceId { get; protected set; }

        /// <param name="id">The style-JSON layer id.</param>
        protected VisualLayer(string id) => Id = id;

        /// <summary>This layer's full style-JSON layer object (the <c>"layers"</c> array entry).</summary>
        public abstract string ToLayerJson();

        /// <summary>A new fill layer with the given id.</summary>
        public static FillVisualLayer Fill(string id) => new FillVisualLayer(id);

        /// <summary>A new <c>type: "symbol"</c> text layer with the given id.</summary>
        public static SymbolTextVisualLayer SymbolText(string id) => new SymbolTextVisualLayer(id);
    }

    /// <summary>A <c>type: "fill"</c> <see cref="VisualLayer"/>.</summary>
    internal sealed class FillVisualLayer : VisualLayer
    {
        // Style-Spec default (Style Spec "fill-color", default "#000000"); a caller that never calls
        // Color()/ColorExpression() still gets a well-formed paint block.
        private string _colorJson = "\"#000000\"";
        private double? _opacity;

        /// <param name="id">The style-JSON layer id (forwarded to the base).</param>
        internal FillVisualLayer(string id) : base(id) { }

        /// <summary>Binds this layer to <paramref name="sourceId"/> — stored verbatim, never validated here.</summary>
        public FillVisualLayer Source(string sourceId)
        {
            SourceId = sourceId;
            return this;
        }

        /// <summary><c>fill-color</c> as a hex literal, e.g. <c>"#ff0000"</c> (pass the hex WITHOUT quotes;
        /// this method quotes it).</summary>
        public FillVisualLayer Color(string hexColor)
        {
            _colorJson = $"\"{hexColor}\"";
            return this;
        }

        /// <summary><c>fill-color</c> as a Style-Spec expression, e.g. <c>["rgba",255,0,0,1]</c> (pass the
        /// raw JSON array text verbatim — the T-Parse falsifier arm's whole point is routing this through
        /// the REAL expression evaluator rather than a hex-only shortcut, plan §6/§8.6).</summary>
        public FillVisualLayer ColorExpression(string rawExpressionJson)
        {
            _colorJson = rawExpressionJson;
            return this;
        }

        /// <summary><c>fill-opacity</c>. Omitted from the emitted paint block (Style-Spec default 1) unless set.</summary>
        public FillVisualLayer Opacity(double opacity)
        {
            _opacity = opacity;
            return this;
        }

        /// <inheritdoc/>
        public override string ToLayerJson()
        {
            var paint = new List<string> { $"\"fill-color\":{_colorJson}" };
            if (_opacity.HasValue)
                paint.Add($"\"fill-opacity\":{_opacity.Value.ToString(CultureInfo.InvariantCulture)}");

            return $"{{\"id\":\"{Id}\",\"type\":\"fill\",\"source\":\"{SourceId}\"," +
                   $"\"paint\":{{{string.Join(",", paint)}}}}}";
        }
    }

    /// <summary>A <c>type: "symbol"</c> text <see cref="VisualLayer"/> — point labels only (G-V1 scope
    /// fence; no icons, no line/curved placement). No <c>"source-layer"</c> is emitted: an inline-geojson
    /// source resolves its implicit layer the same way the fill layer does
    /// (<c>SourceLayerResolver.ResolveTileLayer</c>), so binding is <see cref="Source"/> alone.</summary>
    internal sealed class SymbolTextVisualLayer : VisualLayer
    {
        private string _textFieldJson = "\"{name}\"";
        private double _textSizePx = 16.0;
        private string _textFontJson = "[\"Fixture Font\"]";
        private string _textColorJson;
        // Style-Spec default `text-color` is `#000000`, invisible on this kit's dark background (docs
        // Risk R2) — always emitted so a caller that forgets Color() still gets a background-discriminable
        // frame, rather than a silently vacuous positive control.
        private const string DefaultTextColorJson = "\"#ffffff\"";

        /// <param name="id">The style-JSON layer id (forwarded to the base).</param>
        internal SymbolTextVisualLayer(string id) : base(id) { }

        /// <summary>Binds this layer to <paramref name="sourceId"/> — stored verbatim, never validated here
        /// (mirrors <see cref="FillVisualLayer.Source"/>).</summary>
        public SymbolTextVisualLayer Source(string sourceId)
        {
            SourceId = sourceId;
            return this;
        }

        /// <summary><c>text-field</c> as a property-reference token, e.g. <c>TextField("name")</c> emits
        /// <c>"text-field":"{name}"</c> — the real <see cref="MapRenderer.Core.Style.Symbol.TextFieldResolver"/>
        /// token-expansion path, not a literal string. Default <c>"{name}"</c>.</summary>
        public SymbolTextVisualLayer TextField(string prop)
        {
            _textFieldJson = $"\"{{{prop}}}\"";
            return this;
        }

        /// <summary><c>text-size</c>, in style px.</summary>
        public SymbolTextVisualLayer TextSize(double px)
        {
            _textSizePx = px;
            return this;
        }

        /// <summary><c>text-font</c> as a single-name font stack, e.g. <c>TextFont("Fixture Font")</c>
        /// emits <c>"text-font":["Fixture Font"]</c> — the font name a fixture's
        /// <c>VisualScene.Glyphs(fontName, …)</c> must key its <c>TestGlyphSource</c> range under.</summary>
        public SymbolTextVisualLayer TextFont(string fontName)
        {
            _textFontJson = $"[\"{fontName}\"]";
            return this;
        }

        /// <summary><c>text-color</c> as a hex literal, e.g. <c>"#ffffff"</c> (pass the hex WITHOUT quotes;
        /// this method quotes it).</summary>
        public SymbolTextVisualLayer TextColor(string hexColor)
        {
            _textColorJson = $"\"{hexColor}\"";
            return this;
        }

        /// <inheritdoc/>
        public override string ToLayerJson()
        {
            var layout = new List<string>
            {
                $"\"text-field\":{_textFieldJson}",
                $"\"text-size\":{_textSizePx.ToString(CultureInfo.InvariantCulture)}",
                $"\"text-font\":{_textFontJson}",
                // Always on (plan Risk/lessons `flaky-tilesymbolkick-settle`): the dedup/collision machinery
                // at coarse zoom silently drops labels a fixture needs both of to render.
                "\"text-allow-overlap\":true",
            };
            string paintColorJson = _textColorJson ?? DefaultTextColorJson;

            return $"{{\"id\":\"{Id}\",\"type\":\"symbol\",\"source\":\"{SourceId}\"," +
                   $"\"layout\":{{{string.Join(",", layout)}}}," +
                   $"\"paint\":{{\"text-color\":{paintColorJson}}}}}";
        }
    }
}
#endif // UNITY_EDITOR
