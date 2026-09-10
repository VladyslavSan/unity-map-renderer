using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Text;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// The parsed MapLibre symbol <b>layout</b> properties for a single symbol style layer. Read from the
    /// layer's <c>layout</c> sub-tree via <see cref="PropertyNames"/>. Engine-free; clean-room (public Style
    /// Spec §symbol layout).
    ///
    /// <para>Zoom-capable numeric properties (<see cref="TextSize"/>, <see cref="SymbolSortKey"/>,
    /// <see cref="TextPadding"/>, <see cref="TextMaxWidth"/>, <see cref="TextLineHeight"/>,
    /// <see cref="TextLetterSpacing"/>, <see cref="TextRadialOffset"/>, <see cref="IconSize"/>,
    /// <see cref="IconRotate"/>, <see cref="IconPadding"/>) are <see cref="StyleProperty{T}"/> so they can be
    /// re-evaluated per frame;
    /// <see cref="SymbolPlacement"/> is also a <see cref="StyleProperty{T}"/> but is BUILD-ZOOM-evaluated
    /// (evaluated once by the extractor, never per frame — see its own doc); the small enum/flag knobs
    /// (<see cref="TextAllowOverlap"/>,
    /// <see cref="TextAnchor"/>, <see cref="TextJustify"/>, <see cref="TextOffset"/>, <see cref="TextFont"/>,
    /// <see cref="IconAnchor"/>, <see cref="IconOffset"/>, <see cref="IconRotationAlignment"/>,
    /// <see cref="IconAllowOverlap"/>, <see cref="IconIgnorePlacement"/>, <see cref="IconOptional"/>,
    /// <see cref="TextOptional"/>) are parsed once as plain typed
    /// values (mirroring <c>Line.LayoutProperties</c>'s Join/Cap-as-enum convention — the codebase encodes
    /// constant layout flags directly, not as <c>StyleProperty&lt;bool&gt;</c>). <see cref="TextField"/> and
    /// <see cref="IconImage"/> stay raw <see cref="JsonValue"/> — <c>TextField</c> is per-feature-resolved by
    /// <see cref="TextFieldResolver"/>, and <c>IconImage</c> is likewise per-feature (a <c>{token}</c> string
    /// or an expression array); neither is a scalar style value here — icon sprite resolution is a later
    /// stage. <see cref="TextAnchor"/>/<see cref="TextJustify"/> are the <c>Core.Text</c> enums (string→enum
    /// at parse); <see cref="TextLayoutOptionsBuilder"/> assembles them plus the em metrics into a
    /// <see cref="Text.TextLayoutOptions"/> per feature.</para>
    /// </summary>
    public sealed class LayoutProperties
    {
        /// <summary>text-field: the raw value (a <c>{token}</c> string or an expression array), or null when
        /// absent. Resolved per feature by <see cref="TextFieldResolver.Resolve"/> — NOT a scalar here.</summary>
        public JsonValue TextField { get; init; }

        /// <summary>text-font: the font stack. Default <c>["Open Sans Regular", "Arial Unicode MS Regular"]</c> (spec).</summary>
        public string[] TextFont { get; init; }

        /// <summary>text-size: glyph size in pixels. Default 16. Zoom-capable.</summary>
        public StyleProperty<float> TextSize { get; init; }

        /// <summary>text-max-width: wrap width in ems. Default 10. Zoom-capable.</summary>
        public StyleProperty<float> TextMaxWidth { get; init; }

        /// <summary>text-line-height: line-to-line baseline spacing in ems. Default 1.2. Zoom-capable.</summary>
        public StyleProperty<float> TextLineHeight { get; init; }

        /// <summary>text-letter-spacing: extra pen advance between glyphs in ems. Default 0. Zoom-capable.</summary>
        public StyleProperty<float> TextLetterSpacing { get; init; }

        /// <summary>text-radial-offset: radial offset in ems, resolved from the anchor. Default 0. Zoom-capable.
        /// Overrides <see cref="TextOffset"/> when non-zero (see <c>TextQuadLayout</c>).</summary>
        public StyleProperty<float> TextRadialOffset { get; init; }

        /// <summary>symbol-placement: <see cref="Text.SymbolPlacement.Point"/> (default),
        /// <see cref="Text.SymbolPlacement.Line"/>, or <see cref="Text.SymbolPlacement.LineCenter"/>.
        /// BUILD-ZOOM-evaluated (road-shields D1): the extractor evaluates this ONCE, at the tile's build
        /// zoom, and the result is frozen into that tile's symbols for its lifetime — it is never
        /// re-evaluated as the camera crosses a step boundary (the accepted, pinned known limit; see
        /// docs/road-shields-design.md §3 D1). An absent property degrades to point at CONSTRUCTION; a
        /// present-but-non-string EXPRESSION RESULT degrades to point at EVALUATION (the extractor's
        /// <c>TryEvaluate</c> call). A structurally MALFORMED expression (invalid JSON shape) still throws
        /// from <see cref="ExpressionParser.Parse"/> here at construction — consistent with every
        /// other <see cref="StyleProperty{T}"/> in this file, not a degrade-on-parse-failure contract.</summary>
        public StyleProperty<SymbolPlacement> SymbolPlacement { get; init; }

        /// <summary>symbol-sort-key: greedy placement priority (lower placed first). Default 0. Zoom-capable.</summary>
        public StyleProperty<float> SymbolSortKey { get; init; }

        /// <summary>symbol-spacing: distance in PIXELS between repeated symbols along a line
        /// (<see cref="Text.SymbolPlacement.Line"/> only — ignored for point/line-center). Default 250 (spec),
        /// minimum 1. Zoom-capable.</summary>
        public StyleProperty<float> SymbolSpacing { get; init; }

        /// <summary>text-max-angle: maximum DEGREE change between adjacent characters on a curved line symbol;
        /// a symbol whose along-line curvature exceeds this at any glyph pair is dropped at that anchor (line /
        /// line-center only). Default 45 (spec). Zoom-capable.</summary>
        public StyleProperty<float> TextMaxAngle { get; init; }

        /// <summary>text-keep-upright: when true (default), a curved line symbol that would read right-to-left is
        /// walked reversed + flipped so it stays upright/left-to-right; when false the glyphs follow the raw
        /// line direction (may render upside-down). line / line-center only.</summary>
        public bool TextKeepUpright { get; init; }

        /// <summary>text-allow-overlap: skip collision, always place. Default false.</summary>
        public bool TextAllowOverlap { get; init; }

        /// <summary>text-ignore-placement: place but don't block others. Default false.</summary>
        public bool TextIgnorePlacement { get; init; }

        /// <summary>text-padding: collision-box growth in pixels. Default 2 (spec). Zoom-capable.</summary>
        public StyleProperty<float> TextPadding { get; init; }

        /// <summary>text-anchor: anchor position for the symbol block. Default <see cref="Text.TextAnchor.Center"/>.
        /// An unrecognized/malformed value degrades to the spec default (center).</summary>
        public TextAnchor TextAnchor { get; init; }

        /// <summary>text-offset: [x, y] offset in ems from the anchor, in MapLibre's raw y-DOWN convention
        /// (positive y = down). Default [0, 0]. <b>Constant only</b> (parsed as a plain <see cref="float2"/>,
        /// not zoom/data-driven). The y-up reconcile happens in <see cref="TextLayoutOptionsBuilder"/>.</summary>
        public float2 TextOffset { get; init; }

        /// <summary>text-justify: multi-line justification. Spec default "center" (NOT the enum's zero value
        /// <c>Auto</c> — <c>Auto</c> is only the explicit opt-in that resolves from the anchor at layout time).
        /// An unrecognized/malformed value degrades to the spec default (center).</summary>
        public TextJustify TextJustify { get; init; }

        /// <summary>text-transform: case transform applied to the resolved symbol before shaping. Default
        /// <see cref="Text.TextTransform.None"/>. <b>Constant only</b> (parsed once as a plain enum, not
        /// zoom/data-driven). An unrecognized/malformed value degrades to none.</summary>
        public TextTransform TextTransform { get; init; }

        /// <summary>text-rotation-alignment: whether the symbol rotates with the map (<c>map</c>) or stays
        /// screen-aligned (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/> (→ viewport for the
        /// point placement emitted today). Consumed by the placement billboard rotation (#4).</summary>
        public AlignmentMode TextRotationAlignment { get; init; }

        /// <summary>text-pitch-alignment: whether the symbol lies flat on the map (<c>map</c>) or faces the
        /// camera (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/>. <c>auto</c> resolves via
        /// <see cref="AlignmentResolution.ResolvePitch"/> against the RESOLVED
        /// <see cref="TextRotationAlignment"/> — so under <see cref="SymbolPlacement.Line"/> /
        /// <see cref="SymbolPlacement.LineCenter"/> placement it resolves to <c>map</c>, matching that
        /// resolved rotation alignment; under <see cref="SymbolPlacement.Point"/> with an
        /// auto-auto pair it resolves to <c>viewport</c> (today's billboard).
        ///
        /// <para><b>CONSUMED as of W1, on the CURVED (along-line) arm only.</b>
        /// <c>SymbolFeatureExtractor</c> resolves this once per layer and stamps it onto the emitted
        /// symbol; under <see cref="AlignmentMode.Map"/> <c>SymbolStagingMath.StageCurved</c> lays the symbol out
        /// in WORLD ARC LENGTH rather than screen px, so a glyph advance is a fixed world size and spacing
        /// foreshortens with depth. This is not a dormant key: every shipped line-symbol layer resolves to
        /// <c>map</c> here (an explicit or auto-auto <see cref="TextRotationAlignment"/> under line
        /// placement), so it selects the world-metre layout for all of them.</para>
        ///
        /// <para><b>The POINT arm does NOT consume it yet</b> — do not infer otherwise from the above. A
        /// map-pitched point symbol still billboards; the ground-flat point path is a later stage. Glyph SIZE
        /// is likewise still screen-constant on both arms (W1 moved the layout, not the render).</para></summary>
        public AlignmentMode TextPitchAlignment { get; init; }

        /// <summary>icon-image: the raw value (a <c>{token}</c> string or an expression array), or null when
        /// absent. Per-feature-resolved; NOT a scalar here — icon sprite resolution is a later stage.</summary>
        public JsonValue IconImage { get; init; }

        /// <summary>icon-size: scale factor applied to the sprite's logical size. Default 1. Zoom-capable.</summary>
        public StyleProperty<float> IconSize { get; init; }

        /// <summary>icon-offset: [x, y] offset from the anchor, in units of the icon's own (unscaled) size.
        /// Default [0, 0]. <b>Constant only</b> (parsed as a plain <see cref="float2"/>, not zoom/data-driven).</summary>
        public float2 IconOffset { get; init; }

        /// <summary>icon-rotate: clockwise rotation in DEGREES, composed on top of whatever the icon's
        /// rotation-alignment already produced (the map bearing under <c>map</c>, the line tangent under line
        /// placement, nothing under <c>viewport</c>). Default 0. Zoom-capable.</summary>
        public StyleProperty<float> IconRotate { get; init; }

        /// <summary>icon-anchor: anchor position for the icon. Default <see cref="Text.TextAnchor.Center"/>.
        /// An unrecognized/malformed value degrades to the spec default (center).</summary>
        public TextAnchor IconAnchor { get; init; }

        /// <summary>icon-rotation-alignment: whether the icon rotates with the map (<c>map</c>) or stays
        /// screen-aligned (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/>.</summary>
        public AlignmentMode IconRotationAlignment { get; init; }

        /// <summary>icon-pitch-alignment: whether the icon lies flat on the map (<c>map</c>) or faces the
        /// camera (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/>. <c>auto</c> resolves via
        /// <see cref="AlignmentResolution.ResolvePitch"/> against the RESOLVED
        /// <see cref="IconRotationAlignment"/> — mirrors <see cref="TextPitchAlignment"/>.
        ///
        /// <para><b>CONSUMED as of W1, on the CURVED (along-line) arm only</b> — the same wiring and the same
        /// fence as <see cref="TextPitchAlignment"/>, which states both in full. For icons that arm is the
        /// one-glyph along-line symbol a MAP-resolved line icon emits (<c>road_one_way_arrow*</c> and
        /// friends); a POINT icon still billboards.</para></summary>
        public AlignmentMode IconPitchAlignment { get; init; }

        /// <summary>icon-allow-overlap: skip collision, always place. Default false.</summary>
        public bool IconAllowOverlap { get; init; }

        /// <summary>icon-ignore-placement: place but don't block others. Default false.</summary>
        public bool IconIgnorePlacement { get; init; }

        /// <summary>icon-optional: when true, the TEXT half of an icon+text pair may place even if the icon
        /// cannot. Default false ⇒ the two halves place or drop together. Only meaningful on a paired symbol
        /// (see <c>SymbolFeatureExtractor</c>'s pairing predicate); ignored on a lone icon.</summary>
        public bool IconOptional { get; init; }

        /// <summary>text-optional: when true, the ICON half of an icon+text pair may place even if the text
        /// cannot. Default false ⇒ the two halves place or drop together. Only meaningful on a paired symbol;
        /// ignored on a lone text symbol.</summary>
        public bool TextOptional { get; init; }

        /// <summary>icon-padding: collision-box growth in pixels. Default 2 (spec). Zoom-capable.</summary>
        public StyleProperty<float> IconPadding { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private LayoutProperties() { }

        /// <summary>Parse the symbol layout properties from a layer's <c>layout</c> sub-tree.</summary>
        /// <param name="layout">The raw <c>layout</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static LayoutProperties Parse(JsonValue layout)
        {
            JsonValue textSizeJson = layout?.Get(PropertyNames.TextSize);
            JsonValue maxWidthJson = layout?.Get(PropertyNames.TextMaxWidth);
            JsonValue lineHeightJson = layout?.Get(PropertyNames.TextLineHeight);
            JsonValue letterSpacingJson = layout?.Get(PropertyNames.TextLetterSpacing);
            JsonValue radialOffsetJson = layout?.Get(PropertyNames.TextRadialOffset);
            JsonValue placementJson = layout?.Get(PropertyNames.SymbolPlacement);
            JsonValue sortKeyJson = layout?.Get(PropertyNames.SymbolSortKey);
            JsonValue spacingJson = layout?.Get(PropertyNames.SymbolSpacing);
            JsonValue maxAngleJson = layout?.Get(PropertyNames.TextMaxAngle);
            JsonValue paddingJson = layout?.Get(PropertyNames.TextPadding);
            JsonValue iconSizeJson = layout?.Get(PropertyNames.IconSize);
            JsonValue iconRotateJson = layout?.Get(PropertyNames.IconRotate);
            JsonValue iconPaddingJson = layout?.Get(PropertyNames.IconPadding);

            return new LayoutProperties
            {
                TextField = layout?.Get(PropertyNames.TextField), // raw; resolved per feature

                TextFont = ParseFontStack(layout?.Get(PropertyNames.TextFont)),

                TextSize = textSizeJson != null
                    ? new StyleProperty<float>(textSizeJson, 16f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(16f),

                TextMaxWidth = maxWidthJson != null
                    ? new StyleProperty<float>(maxWidthJson, 10f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(10f),

                TextLineHeight = lineHeightJson != null
                    ? new StyleProperty<float>(lineHeightJson, 1.2f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(1.2f),

                TextLetterSpacing = letterSpacingJson != null
                    ? new StyleProperty<float>(letterSpacingJson, 0f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(0f),

                TextRadialOffset = radialOffsetJson != null
                    ? new StyleProperty<float>(radialOffsetJson, 0f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(0f),

                SymbolPlacement = placementJson != null
                    ? new StyleProperty<SymbolPlacement>(placementJson, Text.SymbolPlacement.Point,
                        v => ParsePlacement(v.ToDisplayString()))
                    : new StyleProperty<SymbolPlacement>(Text.SymbolPlacement.Point),

                SymbolSortKey = sortKeyJson != null
                    ? new StyleProperty<float>(sortKeyJson, 0f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(0f),

                SymbolSpacing = spacingJson != null
                    ? new StyleProperty<float>(spacingJson, 250f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(250f),

                TextMaxAngle = maxAngleJson != null
                    ? new StyleProperty<float>(maxAngleJson, 45f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(45f),

                TextKeepUpright = layout?.Get(PropertyNames.TextKeepUpright)?.AsBool(true) ?? true,

                TextAllowOverlap = layout?.Get(PropertyNames.TextAllowOverlap)?.AsBool(false) ?? false,
                TextIgnorePlacement = layout?.Get(PropertyNames.TextIgnorePlacement)?.AsBool(false) ?? false,

                TextPadding = paddingJson != null
                    ? new StyleProperty<float>(paddingJson, 2f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(2f),

                TextAnchor = ParseAnchor(layout?.Get(PropertyNames.TextAnchor)?.AsString(null)),
                TextJustify = ParseJustify(layout?.Get(PropertyNames.TextJustify)?.AsString(null)),
                TextTransform = ParseTransform(layout?.Get(PropertyNames.TextTransform)?.AsString(null)),
                TextRotationAlignment = ParseAlignment(layout?.Get(PropertyNames.TextRotationAlignment)?.AsString(null)),
                TextPitchAlignment = ParseAlignment(layout?.Get(PropertyNames.TextPitchAlignment)?.AsString(null)),
                TextOffset = ParseOffset(layout?.Get(PropertyNames.TextOffset)),

                IconImage = layout?.Get(PropertyNames.IconImage), // raw; resolved per feature

                IconSize = iconSizeJson != null
                    ? new StyleProperty<float>(iconSizeJson, 1f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(1f),

                IconRotate = iconRotateJson != null
                    ? new StyleProperty<float>(iconRotateJson, 0f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(0f),

                IconOffset = ParseOffset(layout?.Get(PropertyNames.IconOffset)),

                IconAnchor = ParseAnchor(layout?.Get(PropertyNames.IconAnchor)?.AsString(null)),
                IconRotationAlignment = ParseAlignment(layout?.Get(PropertyNames.IconRotationAlignment)?.AsString(null)),
                IconPitchAlignment = ParseAlignment(layout?.Get(PropertyNames.IconPitchAlignment)?.AsString(null)),

                IconAllowOverlap = layout?.Get(PropertyNames.IconAllowOverlap)?.AsBool(false) ?? false,
                IconIgnorePlacement = layout?.Get(PropertyNames.IconIgnorePlacement)?.AsBool(false) ?? false,
                IconOptional = layout?.Get(PropertyNames.IconOptional)?.AsBool(false) ?? false,
                TextOptional = layout?.Get(PropertyNames.TextOptional)?.AsBool(false) ?? false,

                IconPadding = iconPaddingJson != null
                    ? new StyleProperty<float>(iconPaddingJson, 2f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(2f),
            };
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

        // Spec default "point"; absent/unrecognized -> point.
        private static SymbolPlacement ParsePlacement(string s) => s switch
        {
            PropertyNames.PlacementLine       => Text.SymbolPlacement.Line,
            PropertyNames.PlacementLineCenter => Text.SymbolPlacement.LineCenter,
            _                                 => Text.SymbolPlacement.Point,
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
