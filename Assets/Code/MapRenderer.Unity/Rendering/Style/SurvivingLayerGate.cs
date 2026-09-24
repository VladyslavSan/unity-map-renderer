using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using SymbolProperty = MapRenderer.Core.Style.Symbol.PropertyNames;

namespace MapRenderer.Unity.Rendering.Style
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
        /// Every key is free at any non-data-driven kind EXCEPT the two symbol colours, which defer to the
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
        /// Root minus layers: covers sprite/glyphs/sources/light/terrain/name/version and every unknown
        /// root key in one comparison. sprite matters most: the in-place path never re-fetches it.
        /// </summary>
        internal static bool RootMatches(StyleDocument oldStyle, StyleDocument newStyle)
        {
            if (oldStyle == null || newStyle == null) return false; // fail closed, as everything here does
            return JsonCanonical.Write(WithoutMember(oldStyle.Root, "layers"))
                == JsonCanonical.Write(WithoutMember(newStyle.Root, "layers"));
        }

        internal static bool LayerSurvives(StyleLayer oldLayer, StyleLayer newLayer)
        {
            HashSet<string> free = FreelyTransitionableKeys(oldLayer, newLayer);
            return Signature(oldLayer, free) == Signature(newLayer, free);
        }

        /// <summary>
        /// The paint keys this pair may drop from its signature: a transitionable key, present in BOTH paints
        /// (presence changes the binding set), non-data-driven on BOTH sides, and for the symbol colours equal
        /// in alpha (<see cref="ConstantAlphaMatches"/>). Non-obvious why: a data-driven colour is baked, so a
        /// change between two <c>["get",…]</c> colours that passed would leave every tile on the old colour.
        /// A parse failure counts as data-driven (fail closed).
        /// </summary>
        private static HashSet<string> FreelyTransitionableKeys(StyleLayer oldLayer, StyleLayer newLayer)
        {
            var free = new HashSet<string>();
            JsonValue oldPaint = oldLayer.Raw?.Get("paint");
            JsonValue newPaint = newLayer.Raw?.Get("paint");
            if (oldPaint == null || newPaint == null) return free;

            foreach (string key in TransitionablePaintKeys)
            {
                if (!oldPaint.TryGet(key, out JsonValue oldValue)) continue;
                if (!newPaint.TryGet(key, out JsonValue newValue)) continue;
                if (!IsFreeAtKind(key, oldValue) || !IsFreeAtKind(key, newValue)) continue;
                if (IsSymbolColor(key) && !ConstantAlphaMatches(oldValue, newValue)) continue;
                free.Add(key);
            }
            return free;
        }

        /// <summary>The two symbol paint colours that ride a uniform only at <c>Constant</c> — see
        /// <see cref="SymbolTextColorCarrier"/>.</summary>
        private static bool IsSymbolColor(string key)
            => key == SymbolProperty.TextColor || key == SymbolProperty.TextHaloColor;

        /// <summary>
        /// True iff two Constant symbol-colour expressions have the SAME alpha. RGB rides a uniform, but
        /// alpha rides the vertex COLOR stream (<c>SymbolFeatureExtractor.StreamRgba</c>), which the in-place
        /// path never re-bakes. Halo alpha also decides whether a halo run is emitted
        /// (<c>WorldSymbolRenderer.Emit</c>).
        /// <see cref="IsFreeAtKind"/> has proven both Constant, so <c>Evaluate</c> cannot throw.
        /// </summary>
        private static bool ConstantAlphaMatches(JsonValue oldValue, JsonValue newValue)
        {
            double oldAlpha = ExpressionParser.Parse(oldValue).Evaluate(new EvaluationContext(0.0)).AsColorCoerced().A;
            double newAlpha = ExpressionParser.Parse(newValue).Evaluate(new EvaluationContext(0.0)).AsColorCoerced().A;
            return oldAlpha == newAlpha;
        }

        /// <summary>
        /// A key is free only at the kinds whose value the in-place path can MOVE: by default
        /// <c>!DependsOnFeature</c> (a re-bound uniform). The symbol colours defer to
        /// <see cref="SymbolTextColorCarrier.RidesUniform(ExpressionKind)"/>, because at other kinds they bake
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

            return IsSymbolColor(key)
                ? SymbolTextColorCarrier.RidesUniform(kind)
                : !ExpressionKinds.DependsOnFeature(kind);
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
