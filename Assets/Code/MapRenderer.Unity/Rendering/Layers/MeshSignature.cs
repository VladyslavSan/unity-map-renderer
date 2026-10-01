using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;
using SymbolProperty = MapRenderer.Unity.Style.Symbol.PropertyNames;

namespace MapRenderer.Unity.Rendering.Layers
{
    /// <summary>
    /// The part of a style that decides a tile's mesh bytes, as canonical JSON. Two documents with equal signatures bake equal meshes. The restyle
    /// gate (<see cref="SurvivingLayerGate"/>) and the prepared-cache token (<c>MapView.SetStyle</c>) both read it, so what the cache may reuse
    /// and what a restyle may keep in place never disagree. An unknown key or a value that does not parse stays in the signature, so a doubt
    /// splits the cache and refuses the in-place restyle.
    /// </summary>
    internal static class MeshSignature
    {
        /// <summary>
        /// A key is listed only when it is BOTH bound to a uniform AND re-bound on restyle.
        /// <c>text-halo-width</c>/<c>-blur</c> ride the vertex stream, which the in-place path never re-bakes.
        /// Every key is free at any non-data-driven kind EXCEPT <c>text-color</c>, which defers to the
        /// narrower <see cref="SymbolTextColorCarrier.RidesUniform(ExpressionKind)"/>.
        /// </summary>
        private static readonly HashSet<string> TransitionablePaintKeys = new HashSet<string>
        {
            "fill-color", "fill-opacity", "fill-outline-color", "fill-translate", "fill-translate-anchor",
            "line-color", "line-opacity", "line-width", "line-blur", "line-gap-width", "line-offset",
            "line-translate", "line-translate-anchor",
            "fill-extrusion-color", "fill-extrusion-opacity", "fill-extrusion-height",
            "fill-extrusion-base", "fill-extrusion-translate", "fill-extrusion-translate-anchor",
            "background-color", "background-opacity",
            SymbolProperty.TextColor, SymbolProperty.TextHaloColor,
        };

        /// <summary>
        /// The root keys no tile mesh bakes. <c>light</c> and <c>sky</c> are free because <c>MapView.SetStyle</c> re-applies them on every
        /// restyle. <c>name</c> and <c>metadata</c> are documentation. Everything else stays: <c>sprite</c> and <c>glyphs</c> feed symbol
        /// meshes, and the in-place path never re-fetches them.
        /// </summary>
        private static readonly string[] RootFreeKeys = { "layers", "light", "sky", "name", "metadata" };

        /// <summary>The whole document: the root signature and the signature of each of <paramref name="layers"/>, in order.</summary>
        internal static JsonValue Document(StyleDocument style, IEnumerable<StyleLayer> layers)
        {
            var signatures = new List<JsonValue>();
            foreach (StyleLayer layer in layers) signatures.Add(Layer(layer));
            return JsonValue.OfObject(new Dictionary<string, JsonValue>
            {
                ["root"]   = Root(style),
                ["layers"] = JsonValue.OfArray(signatures),
            });
        }

        /// <summary>The document root without the keys in <see cref="RootFreeKeys"/>.</summary>
        internal static JsonValue Root(StyleDocument style) => WithoutMembers(style.Root, RootFreeKeys);

        /// <summary>
        /// The raw layer without the draw-gate keys (<c>minzoom</c>, <c>maxzoom</c>, <c>layout.visibility</c>), and with each uniform paint value
        /// replaced by a marker, so presence still counts. A symbol colour keeps its alpha, because alpha rides the vertex stream. The
        /// <c>fill-color</c> of a pattern layer is left out: an absent one binds white.
        /// </summary>
        internal static JsonValue Layer(StyleLayer layer)
        {
            JsonValue raw = WithoutDrawGateKeys(layer?.Raw);
            JsonValue paint = raw != null && raw.IsObject ? raw.Get("paint") : null;
            if (paint == null || !paint.IsObject) return raw;

            bool pattern = paint.Get("fill-pattern")?.AsString(null) != null;
            var members = new Dictionary<string, JsonValue>();
            foreach (KeyValuePair<string, JsonValue> member in paint.Members)
            {
                if (!TransitionablePaintKeys.Contains(member.Key) || !IsFreeAtKind(member.Key, member.Value)) { members[member.Key] = member.Value; continue; }
                if (member.Key == "fill-color" && pattern) continue;
                members[member.Key] = IsSymbolColor(member.Key) ? AlphaOf(member.Value) : UniformMarker;
            }

            return WithMember(raw, "paint", JsonValue.OfObject(members));
        }

        private static readonly JsonValue UniformMarker = JsonValue.OfString("uniform");

        /// <summary>The two symbol paint colours whose alpha rides the vertex stream — see <see cref="SymbolTextColorCarrier"/>.</summary>
        private static bool IsSymbolColor(string key) => key == SymbolProperty.TextColor || key == SymbolProperty.TextHaloColor;

        /// <summary>
        /// A key is free only at the kinds whose value the in-place path can MOVE: by default <c>!DependsOnFeature</c> (a re-bound uniform).
        /// The symbol colours defer to <see cref="SymbolTextColorCarrier"/>, because at other kinds they bake into the vertex stream.
        /// A parse failure is not free.
        /// </summary>
        private static bool IsFreeAtKind(string key, JsonValue expr)
        {
            ExpressionKind kind;
            try
            {
                kind = ExpressionParser.Parse(expr).Kind;
            }
            catch (ExpressionParseException)
            {
                return false; // parse failure => stays in the signature
            }

            if (key == SymbolProperty.TextHaloColor) return SymbolTextColorCarrier.HaloRidesUniform(kind);
            if (key == SymbolProperty.TextColor) return SymbolTextColorCarrier.RidesUniform(kind);
            return !ExpressionKinds.DependsOnFeature(kind);
        }

        /// <summary>
        /// The alpha of a symbol colour at every zoom. A Constant gives its evaluated alpha. Any other expression gives itself with each colour
        /// literal at an output position turned into its alpha. An <c>["rgba",…]</c> stop stays as written, and a colour that cannot be read stays
        /// as written too, so both fail closed.
        /// </summary>
        private static JsonValue AlphaOf(JsonValue value)
        {
            Expression expr = ExpressionParser.Parse(value);
            if (expr.Kind != ExpressionKind.Constant) return WithColorsAsAlpha(value);
            try
            {
                return AlphaMarker(expr.Evaluate(new EvaluationContext(0.0)).AsColorCoerced().A);
            }
            catch (ExpressionEvaluationException)
            {
                return value; // an unreadable colour stays as written
            }
        }

        private static JsonValue AlphaMarker(double alpha)
            => JsonValue.OfString("alpha:" + alpha.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>Rewrites a colour-string literal to its alpha marker, but only at a colour OUTPUT position: a bare string, or the default and
        /// stop outputs of an array <c>interpolate*</c>/<c>step</c> expression (recursing into those). Everything else stays as written. A legacy
        /// <c>{"stops"}</c> object is not rewritten either.</summary>
        private static JsonValue WithColorsAsAlpha(JsonValue value)
        {
            if (value == null) return null;
            if (!value.IsArray)
            {
                string text = value.AsString(null);
                return text != null && ColorParser.TryParse(text, out Color color) ? AlphaMarker(color.A) : value;
            }

            string head = value.Items.Count > 0 ? value.Items[0].AsString(null) : null;
            bool isStep = head == "step";
            if (!isStep && head != "interpolate" && head != "interpolate-hcl" && head != "interpolate-lab") return value;

            var items = new List<JsonValue>(value.Items.Count);
            for (int i = 0; i < value.Items.Count; i++)
            {
                bool isOutput = isStep ? i == 2 || (i >= 4 && i % 2 == 0) : i >= 4 && i % 2 == 0;
                items.Add(isOutput ? WithColorsAsAlpha(value.Items[i]) : value.Items[i]);
            }
            return JsonValue.OfArray(items);
        }

        /// <summary>
        /// Drops the three draw-gate keys, so a layer that differs only in them keeps its signature. None of the three can be data-driven, and no
        /// binding set, mesh or <c>PreparedKey</c> depends on them.
        /// </summary>
        private static JsonValue WithoutDrawGateKeys(JsonValue raw)
        {
            if (raw == null || !raw.IsObject) return raw;

            JsonValue reduced = WithoutMembers(raw, new[] { "minzoom", "maxzoom" });

            // ONE disjunctive condition: a missing `layout` must not gain a null member, and a layout emptied by the strip must not stay `{}`.
            JsonValue layout = reduced.Get("layout");
            JsonValue stripped = WithoutMembers(layout, new[] { "visibility" });
            return layout == null || stripped == null || !stripped.IsObject || stripped.Members.Count == 0
                ? WithoutMembers(reduced, new[] { "layout" })
                : WithMember(reduced, "layout", stripped);
        }

        private static JsonValue WithoutMembers(JsonValue obj, IReadOnlyCollection<string> keys)
        {
            if (obj == null || !obj.IsObject) return obj;
            var members = new Dictionary<string, JsonValue>(obj.Members);
            foreach (string key in keys) members.Remove(key);
            return JsonValue.OfObject(members);
        }

        private static JsonValue WithMember(JsonValue obj, string key, JsonValue value)
        {
            var members = new Dictionary<string, JsonValue>(obj.Members);
            members[key] = value;
            return JsonValue.OfObject(members);
        }
    }
}
