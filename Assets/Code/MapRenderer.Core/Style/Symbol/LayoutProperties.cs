using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// The parsed MapLibre symbol <b>layout</b> properties for a single symbol style layer. Read from the
    /// layer's <c>layout</c> sub-tree via <see cref="PropertyNames"/>. Engine-free; clean-room (public Style
    /// Spec §symbol layout). TEXT-ONLY.
    ///
    /// <para>Zoom-capable numeric properties (<see cref="TextSize"/>, <see cref="SymbolSortKey"/>,
    /// <see cref="TextPadding"/>, <see cref="TextMaxWidth"/>, <see cref="TextLineHeight"/>,
    /// <see cref="TextLetterSpacing"/>, <see cref="TextRadialOffset"/>) are <see cref="StyleProperty{T}"/> so
    /// they can be re-evaluated per frame; the small enum/flag knobs (<see cref="SymbolPlacement"/>,
    /// <see cref="TextAllowOverlap"/>, <see cref="TextAnchor"/>, <see cref="TextJustify"/>,
    /// <see cref="TextOffset"/>, <see cref="TextFont"/>) are parsed once as plain typed values (mirroring
    /// <c>Line.LayoutProperties</c>'s Join/Cap-as-enum convention — the codebase encodes constant layout flags
    /// directly, not as <c>StyleProperty&lt;bool&gt;</c>). <see cref="TextField"/> stays a raw
    /// <see cref="JsonValue"/> — it is per-feature-resolved by <see cref="TextFieldResolver"/>, not a scalar
    /// style value. <see cref="TextAnchor"/>/<see cref="TextJustify"/> are the <c>Core.Text</c> enums
    /// (string→enum at parse); <see cref="TextLayoutOptionsBuilder"/> assembles them plus the em metrics into
    /// a <see cref="Text.TextLayoutOptions"/> per feature.</para>
    /// </summary>
    public sealed class LayoutProperties
    {
        /// <summary>text-field: the raw value (a <c>{token}</c> string or an expression array), or null when
        /// absent. Resolved per feature by <see cref="TextFieldResolver.Resolve"/> — NOT a scalar here.</summary>
        public JsonValue TextField { get; }

        /// <summary>text-font: the font stack. Default <c>["Open Sans Regular", "Arial Unicode MS Regular"]</c> (spec).</summary>
        public string[] TextFont { get; }

        /// <summary>text-size: glyph size in pixels. Default 16. Zoom-capable.</summary>
        public StyleProperty<float> TextSize { get; }

        /// <summary>text-max-width: wrap width in ems. Default 10. Zoom-capable.</summary>
        public StyleProperty<float> TextMaxWidth { get; }

        /// <summary>text-line-height: line-to-line baseline spacing in ems. Default 1.2. Zoom-capable.</summary>
        public StyleProperty<float> TextLineHeight { get; }

        /// <summary>text-letter-spacing: extra pen advance between glyphs in ems. Default 0. Zoom-capable.</summary>
        public StyleProperty<float> TextLetterSpacing { get; }

        /// <summary>text-radial-offset: radial offset in ems, resolved from the anchor. Default 0. Zoom-capable.
        /// Overrides <see cref="TextOffset"/> when non-zero (see <c>TextQuadLayout</c>).</summary>
        public StyleProperty<float> TextRadialOffset { get; }

        /// <summary>symbol-placement: "point" (default), "line", or "line-center". S20 renders point only.</summary>
        public string SymbolPlacement { get; }

        /// <summary>symbol-sort-key: greedy placement priority (lower placed first). Default 0. Zoom-capable.</summary>
        public StyleProperty<float> SymbolSortKey { get; }

        /// <summary>text-allow-overlap: skip collision, always place. Default false.</summary>
        public bool TextAllowOverlap { get; }

        /// <summary>text-ignore-placement: place but don't block others. Default false.</summary>
        public bool TextIgnorePlacement { get; }

        /// <summary>text-padding: collision-box growth in pixels. Default 2 (spec). Zoom-capable.</summary>
        public StyleProperty<float> TextPadding { get; }

        /// <summary>text-anchor: anchor position for the label block. Default <see cref="Text.TextAnchor.Center"/>.
        /// An unrecognized/malformed value degrades to the spec default (center).</summary>
        public TextAnchor TextAnchor { get; }

        /// <summary>text-offset: [x, y] offset in ems from the anchor, in MapLibre's raw y-DOWN convention
        /// (positive y = down). Default [0, 0]. <b>Constant only</b> (parsed as a plain <see cref="float2"/>,
        /// not zoom/data-driven). The y-up reconcile happens in <see cref="TextLayoutOptionsBuilder"/>.</summary>
        public float2 TextOffset { get; }

        /// <summary>text-justify: multi-line justification. Spec default "center" (NOT the enum's zero value
        /// <c>Auto</c> — <c>Auto</c> is only the explicit opt-in that resolves from the anchor at layout time).
        /// An unrecognized/malformed value degrades to the spec default (center).</summary>
        public TextJustify TextJustify { get; }

        /// <summary>text-transform: case transform applied to the resolved label before shaping. Default
        /// <see cref="Text.TextTransform.None"/>. <b>Constant only</b> (parsed once as a plain enum, not
        /// zoom/data-driven). An unrecognized/malformed value degrades to none.</summary>
        public TextTransform TextTransform { get; }

        /// <summary>text-rotation-alignment: whether the label rotates with the map (<c>map</c>) or stays
        /// screen-aligned (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/> (→ viewport for the
        /// point placement emitted today). Consumed by the placement billboard rotation (#4).</summary>
        public AlignmentMode TextRotationAlignment { get; }

        /// <summary>text-pitch-alignment: whether the label lies flat on the map (<c>map</c>) or faces the
        /// camera (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/>. <b>Parsed but its <c>map</c>
        /// (ground-flat) behaviour is not yet consumed</b> — that needs a world-space text path (deferred to
        /// its own stage); point placement resolves auto→viewport (the current billboard) regardless.</summary>
        public AlignmentMode TextPitchAlignment { get; }

        /// <summary>Convenience: parse from a style layer's <c>LayoutJson</c>.</summary>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public LayoutProperties(MapRenderer.Core.Style.StyleLayer layer)
            : this((layer ?? throw new System.ArgumentNullException(nameof(layer))).LayoutJson) { }

        /// <summary>Parse the symbol layout properties from the layer's <c>layout</c> sub-tree (may be null → defaults).</summary>
        public LayoutProperties(JsonValue layout)
        {
            TextField = layout?.Get(PropertyNames.TextField); // raw; resolved per feature

            TextFont = ParseFontStack(layout?.Get(PropertyNames.TextFont));

            JsonValue textSizeJson = layout?.Get(PropertyNames.TextSize);
            TextSize = textSizeJson != null
                ? new StyleProperty<float>(textSizeJson, 16f, v => (float)v.AsNumber())
                : new StyleProperty<float>(16f);

            JsonValue maxWidthJson = layout?.Get(PropertyNames.TextMaxWidth);
            TextMaxWidth = maxWidthJson != null
                ? new StyleProperty<float>(maxWidthJson, 10f, v => (float)v.AsNumber())
                : new StyleProperty<float>(10f);

            JsonValue lineHeightJson = layout?.Get(PropertyNames.TextLineHeight);
            TextLineHeight = lineHeightJson != null
                ? new StyleProperty<float>(lineHeightJson, 1.2f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1.2f);

            JsonValue letterSpacingJson = layout?.Get(PropertyNames.TextLetterSpacing);
            TextLetterSpacing = letterSpacingJson != null
                ? new StyleProperty<float>(letterSpacingJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            JsonValue radialOffsetJson = layout?.Get(PropertyNames.TextRadialOffset);
            TextRadialOffset = radialOffsetJson != null
                ? new StyleProperty<float>(radialOffsetJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            SymbolPlacement = layout?.Get(PropertyNames.SymbolPlacement)?.AsString(PropertyNames.PlacementPoint)
                              ?? PropertyNames.PlacementPoint;

            JsonValue sortKeyJson = layout?.Get(PropertyNames.SymbolSortKey);
            SymbolSortKey = sortKeyJson != null
                ? new StyleProperty<float>(sortKeyJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            TextAllowOverlap = layout?.Get(PropertyNames.TextAllowOverlap)?.AsBool(false) ?? false;
            TextIgnorePlacement = layout?.Get(PropertyNames.TextIgnorePlacement)?.AsBool(false) ?? false;

            JsonValue paddingJson = layout?.Get(PropertyNames.TextPadding);
            TextPadding = paddingJson != null
                ? new StyleProperty<float>(paddingJson, 2f, v => (float)v.AsNumber())
                : new StyleProperty<float>(2f);

            TextAnchor = ParseAnchor(layout?.Get(PropertyNames.TextAnchor)?.AsString(null));
            TextJustify = ParseJustify(layout?.Get(PropertyNames.TextJustify)?.AsString(null));
            TextTransform = ParseTransform(layout?.Get(PropertyNames.TextTransform)?.AsString(null));
            TextRotationAlignment = ParseAlignment(layout?.Get(PropertyNames.TextRotationAlignment)?.AsString(null));
            TextPitchAlignment = ParseAlignment(layout?.Get(PropertyNames.TextPitchAlignment)?.AsString(null));
            TextOffset = ParseOffset(layout?.Get(PropertyNames.TextOffset));
        }

        // Spec default font stack when text-font is absent or malformed.
        private static readonly string[] DefaultFontStack = { "Open Sans Regular", "Arial Unicode MS Regular" };

        private static string[] ParseFontStack(JsonValue json)
        {
            if (json == null || !json.IsArray || json.Items.Count == 0) return DefaultFontStack;
            var result = new string[json.Items.Count];
            for (int i = 0; i < json.Items.Count; i++)
                result[i] = json.Items[i].AsString(null);
            return result;
        }

        private static float2 ParseOffset(JsonValue json)
        {
            if (json == null || !json.IsArray || json.Items.Count < 2) return float2.zero;
            return new float2((float)json.Items[0].AsDouble(0.0), (float)json.Items[1].AsDouble(0.0));
        }

        // Spec string -> enum. An absent/unrecognized value degrades to the spec default (center / auto's
        // resolve target), matching the enums' zero-value-is-spec-default rationale.
        private static TextAnchor ParseAnchor(string s) => s switch
        {
            PropertyNames.AnchorLeft        => Text.TextAnchor.Left,
            PropertyNames.AnchorRight       => Text.TextAnchor.Right,
            PropertyNames.AnchorTop         => Text.TextAnchor.Top,
            PropertyNames.AnchorBottom      => Text.TextAnchor.Bottom,
            PropertyNames.AnchorTopLeft     => Text.TextAnchor.TopLeft,
            PropertyNames.AnchorTopRight    => Text.TextAnchor.TopRight,
            PropertyNames.AnchorBottomLeft  => Text.TextAnchor.BottomLeft,
            PropertyNames.AnchorBottomRight => Text.TextAnchor.BottomRight,
            _                               => Text.TextAnchor.Center,
        };

        // Spec default is "center" (NOT the enum zero-value Auto — Auto is only the explicit opt-in that
        // resolves from the anchor at layout time). Absent/unrecognized -> center.
        private static TextJustify ParseJustify(string s) => s switch
        {
            PropertyNames.JustifyAuto  => Text.TextJustify.Auto,
            PropertyNames.JustifyLeft  => Text.TextJustify.Left,
            PropertyNames.JustifyRight => Text.TextJustify.Right,
            _                          => Text.TextJustify.Center,
        };

        // Spec default "none"; absent/unrecognized -> none (leave the text as-is).
        private static TextTransform ParseTransform(string s) => s switch
        {
            PropertyNames.TransformUppercase => Text.TextTransform.Uppercase,
            PropertyNames.TransformLowercase => Text.TextTransform.Lowercase,
            _                                => Text.TextTransform.None,
        };

        // Spec default "auto"; absent/unrecognized -> auto.
        private static AlignmentMode ParseAlignment(string s) => s switch
        {
            PropertyNames.AlignMap      => AlignmentMode.Map,
            PropertyNames.AlignViewport => AlignmentMode.Viewport,
            _                           => AlignmentMode.Auto,
        };
    }
}
