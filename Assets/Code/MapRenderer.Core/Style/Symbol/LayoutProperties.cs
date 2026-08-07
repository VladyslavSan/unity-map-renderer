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

        /// <summary>symbol-placement: <see cref="Text.SymbolPlacement.Point"/> (default),
        /// <see cref="Text.SymbolPlacement.Line"/>, or <see cref="Text.SymbolPlacement.LineCenter"/>.
        /// BUILD-ZOOM-evaluated (road-shields D1): the extractor evaluates this ONCE, at the tile's build
        /// zoom, and the result is frozen into that tile's labels for its lifetime — it is never
        /// re-evaluated as the camera crosses a step boundary (the accepted, pinned known limit; see
        /// docs/road-shields-design.md §3 D1). An absent property degrades to point at CONSTRUCTION; a
        /// present-but-non-string EXPRESSION RESULT degrades to point at EVALUATION (the extractor's
        /// <c>TryEvaluate</c> call). A structurally MALFORMED expression (invalid JSON shape) still throws
        /// from <see cref="ExpressionParser.Parse"/> here at construction — consistent with every
        /// other <see cref="StyleProperty{T}"/> in this file, not a degrade-on-parse-failure contract.</summary>
        public StyleProperty<SymbolPlacement> SymbolPlacement { get; }

        /// <summary>symbol-sort-key: greedy placement priority (lower placed first). Default 0. Zoom-capable.</summary>
        public StyleProperty<float> SymbolSortKey { get; }

        /// <summary>symbol-spacing: distance in PIXELS between repeated labels along a line
        /// (<see cref="Text.SymbolPlacement.Line"/> only — ignored for point/line-center). Default 250 (spec),
        /// minimum 1. Zoom-capable.</summary>
        public StyleProperty<float> SymbolSpacing { get; }

        /// <summary>text-max-angle: maximum DEGREE change between adjacent characters on a curved line label;
        /// a label whose along-line curvature exceeds this at any glyph pair is dropped at that anchor (line /
        /// line-center only). Default 45 (spec). Zoom-capable.</summary>
        public StyleProperty<float> TextMaxAngle { get; }

        /// <summary>text-keep-upright: when true (default), a curved line label that would read right-to-left is
        /// walked reversed + flipped so it stays upright/left-to-right; when false the glyphs follow the raw
        /// line direction (may render upside-down). line / line-center only.</summary>
        public bool TextKeepUpright { get; }

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
        /// camera (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/>. <c>auto</c> resolves via
        /// <see cref="AlignmentResolution.ResolvePitch"/> against the RESOLVED
        /// <see cref="TextRotationAlignment"/> — so under <see cref="SymbolPlacement.Line"/> /
        /// <see cref="SymbolPlacement.LineCenter"/> placement it resolves to <c>map</c>, matching that
        /// resolved rotation alignment; under <see cref="SymbolPlacement.Point"/> with an
        /// auto-auto pair it resolves to <c>viewport</c> (today's billboard).
        ///
        /// <para><b>CONSUMED as of W1, on the CURVED (along-line) arm only.</b>
        /// <see cref="SymbolFeatureExtractor"/> resolves this once per layer and stamps it onto the emitted
        /// label; under <see cref="AlignmentMode.Map"/> <c>LabelStagingMath.StageCurved</c> lays the label out
        /// in WORLD ARC LENGTH rather than screen px, so a glyph advance is a fixed world size and spacing
        /// foreshortens with depth. This is not a dormant key: every shipped line-symbol layer resolves to
        /// <c>map</c> here (an explicit or auto-auto <see cref="TextRotationAlignment"/> under line
        /// placement), so it selects the world-metre layout for all of them.</para>
        ///
        /// <para><b>The POINT arm does NOT consume it yet</b> — do not infer otherwise from the above. A
        /// map-pitched point label still billboards; the ground-flat point path is a later stage. Glyph SIZE
        /// is likewise still screen-constant on both arms (W1 moved the layout, not the render).</para></summary>
        public AlignmentMode TextPitchAlignment { get; }

        /// <summary>icon-image: the raw value (a <c>{token}</c> string or an expression array), or null when
        /// absent. Per-feature-resolved; NOT a scalar here — icon sprite resolution is a later stage.</summary>
        public JsonValue IconImage { get; }

        /// <summary>icon-size: scale factor applied to the sprite's logical size. Default 1. Zoom-capable.</summary>
        public StyleProperty<float> IconSize { get; }

        /// <summary>icon-offset: [x, y] offset from the anchor, in units of the icon's own (unscaled) size.
        /// Default [0, 0]. <b>Constant only</b> (parsed as a plain <see cref="float2"/>, not zoom/data-driven).</summary>
        public float2 IconOffset { get; }

        /// <summary>icon-rotate: clockwise rotation in DEGREES, composed on top of whatever the icon's
        /// rotation-alignment already produced (the map bearing under <c>map</c>, the line tangent under line
        /// placement, nothing under <c>viewport</c>). Default 0. Zoom-capable.</summary>
        public StyleProperty<float> IconRotate { get; }

        /// <summary>icon-anchor: anchor position for the icon. Default <see cref="Text.TextAnchor.Center"/>.
        /// An unrecognized/malformed value degrades to the spec default (center).</summary>
        public TextAnchor IconAnchor { get; }

        /// <summary>icon-rotation-alignment: whether the icon rotates with the map (<c>map</c>) or stays
        /// screen-aligned (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/>.</summary>
        public AlignmentMode IconRotationAlignment { get; }

        /// <summary>icon-pitch-alignment: whether the icon lies flat on the map (<c>map</c>) or faces the
        /// camera (<c>viewport</c>). Default <see cref="AlignmentMode.Auto"/>. <c>auto</c> resolves via
        /// <see cref="AlignmentResolution.ResolvePitch"/> against the RESOLVED
        /// <see cref="IconRotationAlignment"/> — mirrors <see cref="TextPitchAlignment"/>.
        ///
        /// <para><b>CONSUMED as of W1, on the CURVED (along-line) arm only</b> — the same wiring and the same
        /// fence as <see cref="TextPitchAlignment"/>, which states both in full. For icons that arm is the
        /// one-glyph along-line label a MAP-resolved line icon emits (<c>road_one_way_arrow*</c> and
        /// friends); a POINT icon still billboards.</para></summary>
        public AlignmentMode IconPitchAlignment { get; }

        /// <summary>icon-allow-overlap: skip collision, always place. Default false.</summary>
        public bool IconAllowOverlap { get; }

        /// <summary>icon-ignore-placement: place but don't block others. Default false.</summary>
        public bool IconIgnorePlacement { get; }

        /// <summary>icon-optional: when true, the TEXT half of an icon+text pair may place even if the icon
        /// cannot. Default false ⇒ the two halves place or drop together. Only meaningful on a paired symbol
        /// (see <c>SymbolFeatureExtractor</c>'s pairing predicate); ignored on a lone icon.</summary>
        public bool IconOptional { get; }

        /// <summary>text-optional: when true, the ICON half of an icon+text pair may place even if the text
        /// cannot. Default false ⇒ the two halves place or drop together. Only meaningful on a paired symbol;
        /// ignored on a lone text label.</summary>
        public bool TextOptional { get; }

        /// <summary>icon-padding: collision-box growth in pixels. Default 2 (spec). Zoom-capable.</summary>
        public StyleProperty<float> IconPadding { get; }

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

            JsonValue placementJson = layout?.Get(PropertyNames.SymbolPlacement);
            SymbolPlacement = placementJson != null
                ? new StyleProperty<SymbolPlacement>(placementJson, Text.SymbolPlacement.Point,
                    v => ParsePlacement(v.ToDisplayString()))
                : new StyleProperty<SymbolPlacement>(Text.SymbolPlacement.Point);

            JsonValue sortKeyJson = layout?.Get(PropertyNames.SymbolSortKey);
            SymbolSortKey = sortKeyJson != null
                ? new StyleProperty<float>(sortKeyJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            JsonValue spacingJson = layout?.Get(PropertyNames.SymbolSpacing);
            SymbolSpacing = spacingJson != null
                ? new StyleProperty<float>(spacingJson, 250f, v => (float)v.AsNumber())
                : new StyleProperty<float>(250f);

            JsonValue maxAngleJson = layout?.Get(PropertyNames.TextMaxAngle);
            TextMaxAngle = maxAngleJson != null
                ? new StyleProperty<float>(maxAngleJson, 45f, v => (float)v.AsNumber())
                : new StyleProperty<float>(45f);

            TextKeepUpright = layout?.Get(PropertyNames.TextKeepUpright)?.AsBool(true) ?? true;

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

            IconImage = layout?.Get(PropertyNames.IconImage); // raw; resolved per feature

            JsonValue iconSizeJson = layout?.Get(PropertyNames.IconSize);
            IconSize = iconSizeJson != null
                ? new StyleProperty<float>(iconSizeJson, 1f, v => (float)v.AsNumber())
                : new StyleProperty<float>(1f);

            JsonValue iconRotateJson = layout?.Get(PropertyNames.IconRotate);
            IconRotate = iconRotateJson != null
                ? new StyleProperty<float>(iconRotateJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);

            IconOffset = ParseOffset(layout?.Get(PropertyNames.IconOffset));

            IconAnchor = ParseAnchor(layout?.Get(PropertyNames.IconAnchor)?.AsString(null));
            IconRotationAlignment = ParseAlignment(layout?.Get(PropertyNames.IconRotationAlignment)?.AsString(null));
            IconPitchAlignment = ParseAlignment(layout?.Get(PropertyNames.IconPitchAlignment)?.AsString(null));

            IconAllowOverlap = layout?.Get(PropertyNames.IconAllowOverlap)?.AsBool(false) ?? false;
            IconIgnorePlacement = layout?.Get(PropertyNames.IconIgnorePlacement)?.AsBool(false) ?? false;
            IconOptional = layout?.Get(PropertyNames.IconOptional)?.AsBool(false) ?? false;
            TextOptional = layout?.Get(PropertyNames.TextOptional)?.AsBool(false) ?? false;

            JsonValue iconPaddingJson = layout?.Get(PropertyNames.IconPadding);
            IconPadding = iconPaddingJson != null
                ? new StyleProperty<float>(iconPaddingJson, 2f, v => (float)v.AsNumber())
                : new StyleProperty<float>(2f);
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
