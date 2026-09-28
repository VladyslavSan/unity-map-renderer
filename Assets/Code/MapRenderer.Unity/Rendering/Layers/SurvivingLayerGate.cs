using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;
using SymbolProperty = MapRenderer.Unity.Style.Symbol.PropertyNames;

namespace MapRenderer.Unity.Rendering.Layers
{
    /// <summary>The restyle-in-place gate's two predicates: <see cref="RootMatches"/> over everything
    /// outside <c>layers</c>, and <see cref="LayerSurvives"/> over ONE pair, which
    /// <c>RenderLayerSet.TryRestyleInPlace</c> applies per id-matched pair. Compares canonical raw layer
    /// JSON minus the freely-transitionable paint values. An unknown paint or root key, or a parse
    /// failure, stays inside the comparison, so the gate refuses rather than mis-transition (fail closed).</summary>
    internal static class SurvivingLayerGate
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
        /// Root minus <c>layers</c>, <c>light</c> and <c>sky</c>: covers sprite/glyphs/sources/terrain/name/version
        /// and every unknown root key in one comparison. sprite matters most: the in-place path never re-fetches
        /// it. <c>light</c> and <c>sky</c> are free because <c>MapView.SetStyle</c> re-applies them on every
        /// restyle and no tile mesh bakes them.
        /// </summary>
        internal static bool RootMatches(StyleDocument oldStyle, StyleDocument newStyle)
        {
            if (oldStyle == null || newStyle == null) return false; // fail closed, as everything here does
            return JsonCanonical.Write(WithoutMembers(oldStyle.Root, RootFreeKeys))
                == JsonCanonical.Write(WithoutMembers(newStyle.Root, RootFreeKeys));
        }

        /// <summary>The root keys <see cref="RootMatches"/> leaves out of its comparison.</summary>
        private static readonly string[] RootFreeKeys = { "layers", "light", "sky" };

        internal static bool LayerSurvives(StyleLayer oldLayer, StyleLayer newLayer)
        {
            HashSet<string> free = FreelyTransitionableKeys(oldLayer, newLayer);
            return Signature(oldLayer, free) == Signature(newLayer, free);
        }

        /// <summary>
        /// The paint keys this pair may drop from its signature: a transitionable key, present in BOTH paints
        /// (presence changes the binding set), non-data-driven on BOTH sides, and for the symbol colours equal
        /// in alpha (<see cref="AlphaMatches"/>). Non-obvious why: a data-driven colour is baked, so a
        /// change between two <c>["get",…]</c> colours that passed would leave every tile on the old colour.
        /// A parse failure counts as data-driven (fail closed). <see cref="PatternTintMayAppear"/> is the one
        /// exception to "present in both".
        /// </summary>
        private static HashSet<string> FreelyTransitionableKeys(StyleLayer oldLayer, StyleLayer newLayer)
        {
            var free = new HashSet<string>();
            JsonValue oldPaint = oldLayer.Raw?.Get("paint");
            JsonValue newPaint = newLayer.Raw?.Get("paint");
            if (oldPaint == null || newPaint == null) return free;

            foreach (string key in TransitionablePaintKeys)
            {
                bool hasOld = oldPaint.TryGet(key, out JsonValue oldValue);
                bool hasNew = newPaint.TryGet(key, out JsonValue newValue);
                if (hasOld != hasNew && PatternTintMayAppear(key, oldPaint, newPaint)
                    && IsFreeAtKind(key, hasOld ? oldValue : newValue))
                {
                    free.Add(key);
                    continue;
                }
                if (!hasOld || !hasNew) continue;
                if (!IsFreeAtKind(key, oldValue) || !IsFreeAtKind(key, newValue)) continue;
                if (IsSymbolColor(key) && !AlphaMatches(oldValue, newValue)) continue;
                free.Add(key);
            }
            return free;
        }

        /// <summary>
        /// True when <c>fill-color</c> may appear or vanish in place: both paints set <c>fill-pattern</c> to a
        /// sprite name, the pattern-layer test <c>PaintProperties.Parse</c> uses. An absent fill-color on a pattern
        /// layer binds white, so the binding set does not change. The signature keeps <c>fill-pattern</c>, so a
        /// pattern that differs still refuses.
        /// </summary>
        private static bool PatternTintMayAppear(string key, JsonValue oldPaint, JsonValue newPaint)
            => key == "fill-color" && IsPatternName(oldPaint.Get("fill-pattern")) && IsPatternName(newPaint.Get("fill-pattern"));

        /// <summary>True when <paramref name="pattern"/> is a sprite-name string.</summary>
        private static bool IsPatternName(JsonValue pattern) => pattern?.AsString(null) != null;

        /// <summary>The two symbol paint colours whose alpha rides the vertex stream — see
        /// <see cref="SymbolTextColorCarrier"/>.</summary>
        private static bool IsSymbolColor(string key)
            => key == SymbolProperty.TextColor || key == SymbolProperty.TextHaloColor;

        /// <summary>
        /// True iff two symbol-colour expressions carry the SAME alpha at every zoom, because alpha rides the
        /// vertex COLOR stream, which the in-place path never re-bakes. Two Constants compare by evaluation;
        /// otherwise each colour literal becomes its alpha and the JSON is compared (alpha interpolates
        /// independently of the colour space). Fails closed: an <c>["rgba",…]</c> stop compares as written,
        /// and a colour that cannot be read (a malformed value loads with its default) returns false.
        /// </summary>
        private static bool AlphaMatches(JsonValue oldValue, JsonValue newValue)
        {
            Expression oldExpr = ExpressionParser.Parse(oldValue);
            Expression newExpr = ExpressionParser.Parse(newValue);
            if (oldExpr.Kind == ExpressionKind.Constant && newExpr.Kind == ExpressionKind.Constant)
            {
                try
                {
                    var context = new EvaluationContext(0.0);
                    return oldExpr.Evaluate(context).AsColorCoerced().A == newExpr.Evaluate(context).AsColorCoerced().A;
                }
                catch (ExpressionEvaluationException)
                {
                    return false; // unreadable colour => not free => full rebuild
                }
            }
            return JsonCanonical.Write(WithColorsAsAlpha(oldValue)) == JsonCanonical.Write(WithColorsAsAlpha(newValue));
        }

        /// <summary>Rewrites a colour-string literal to <c>"alpha:"</c> plus its alpha, but only at a colour OUTPUT
        /// position: a bare string, or the default and stop outputs of an array <c>interpolate*</c>/<c>step</c>
        /// expression (recursing into those). Everything else, comparison operands and match labels included,
        /// stays as written, so two expressions that differ there never compare equal. A legacy <c>{"stops"}</c>
        /// object is not rewritten either, so it compares as written.</summary>
        private static JsonValue WithColorsAsAlpha(JsonValue value)
        {
            if (value == null) return null;
            if (!value.IsArray)
            {
                string text = value.AsString(null);
                return text != null && ColorParser.TryParse(text, out Color color)
                    ? JsonValue.OfString("alpha:" + color.A.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    : value;
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
        /// A key is free only at the kinds whose value the in-place path can MOVE: by default
        /// <c>!DependsOnFeature</c> (a re-bound uniform). <c>text-color</c> defers to
        /// <see cref="SymbolTextColorCarrier.RidesUniform(ExpressionKind)"/>, because at other kinds it bakes
        /// into the vertex stream, which the in-place path never re-bakes. A parse failure is not free.
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
                return false; // parse failure => not free => refuse
            }

            if (key == SymbolProperty.TextHaloColor) return SymbolTextColorCarrier.HaloRidesUniform(kind);
            if (key == SymbolProperty.TextColor) return SymbolTextColorCarrier.RidesUniform(kind);
            return !ExpressionKinds.DependsOnFeature(kind);
        }

        private static string Signature(StyleLayer layer, HashSet<string> freePaintKeys)
        {
            JsonValue raw = WithoutDrawGateKeys(layer.Raw);
            if (raw == null || !raw.IsObject || freePaintKeys.Count == 0)
                return JsonCanonical.Write(raw);

            JsonValue paint = raw.Get("paint");
            if (paint == null) return JsonCanonical.Write(raw);

            return JsonCanonical.Write(WithMember(raw, "paint", WithoutMembers(paint, freePaintKeys)));
        }

        /// <summary>
        /// Drop the three draw-gate keys — <c>minzoom</c>, <c>maxzoom</c> and <c>layout.visibility</c> — so a
        /// restyle differing only in them survives in place. Unconditional, unlike a paint key: none of the
        /// three can be data-driven, and no binding set, mesh or <c>PreparedKey</c> depends on them.
        ///
        /// <para>Over the limit for one non-local ordering fact: this must run BEFORE
        /// <see cref="Signature"/>'s two early returns, or every pair that takes one keeps its gate keys.</para>
        /// </summary>
        private static JsonValue WithoutDrawGateKeys(JsonValue raw)
        {
            if (raw == null || !raw.IsObject) return raw;

            JsonValue reduced = WithoutMember(WithoutMember(raw, "minzoom"), "maxzoom");

            // ONE disjunctive condition: a missing `layout` must not gain a null member, and a layout emptied
            // by the strip must not stay `{}` — either leaves "no layout -> visibility:none" still differing.
            JsonValue layout = reduced.Get("layout");
            JsonValue stripped = WithoutMember(layout, "visibility");
            return layout == null || stripped == null || !stripped.IsObject || stripped.Members.Count == 0
                ? WithoutMember(reduced, "layout")
                : WithMember(reduced, "layout", stripped);
        }

        private static JsonValue WithoutMember(JsonValue obj, string key)
        {
            if (obj == null || !obj.IsObject) return obj;
            var members = new Dictionary<string, JsonValue>(obj.Members);
            members.Remove(key);
            return JsonValue.OfObject(members);
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
