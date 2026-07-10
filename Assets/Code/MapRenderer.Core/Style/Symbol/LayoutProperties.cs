using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// The parsed MapLibre symbol <b>layout</b> properties for a single symbol style layer. Read from the
    /// layer's <c>layout</c> sub-tree via <see cref="PropertyNames"/>. Engine-free; clean-room (public Style
    /// Spec §symbol layout). TEXT-ONLY.
    ///
    /// <para>Zoom-capable numeric properties (<see cref="TextSize"/>, <see cref="SymbolSortKey"/>,
    /// <see cref="TextPadding"/>, <see cref="TextMaxWidth"/>) are <see cref="StyleProperty{T}"/> so they can
    /// be re-evaluated per frame; the small enum/flag/array knobs (<see cref="SymbolPlacement"/>,
    /// <see cref="TextAllowOverlap"/>, <see cref="TextAnchor"/>, <see cref="TextOffset"/>,
    /// <see cref="TextFont"/>) are parsed once as plain typed values (mirroring <c>Line.LayoutProperties</c>'s
    /// Join/Cap-as-enum convention — the codebase encodes constant layout flags directly, not as
    /// <c>StyleProperty&lt;bool&gt;</c>). <see cref="TextField"/> stays a raw <see cref="JsonValue"/> — it is
    /// per-feature-resolved by <see cref="TextFieldResolver"/>, not a scalar style value.</para>
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

        /// <summary>text-anchor: anchor position for the label block. Default "center". Consumed by S19 layout.</summary>
        public string TextAnchor { get; }

        /// <summary>text-offset: [x, y] offset in ems from the anchor. Default [0, 0]. Consumed by S19 layout.</summary>
        public float2 TextOffset { get; }

        /// <summary>text-justify: multi-line justification. Default "center". Consumed by S19 layout.</summary>
        public string TextJustify { get; }

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

            TextAnchor = layout?.Get(PropertyNames.TextAnchor)?.AsString("center") ?? "center";
            TextJustify = layout?.Get(PropertyNames.TextJustify)?.AsString("center") ?? "center";
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
    }
}
