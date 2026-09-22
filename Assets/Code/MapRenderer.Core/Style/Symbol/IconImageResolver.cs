using System.Runtime.CompilerServices;
using System.Text;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// Resolves a symbol layer's <c>icon-image</c> to a concrete sprite name for one feature. Mirrors
    /// <see cref="TextFieldResolver"/> verbatim (same two forms, same skip policy) — this is the icon
    /// analogue of that resolver, not a different resolution strategy:
    /// <list type="bullet">
    /// <item><b>Token string</b> (the legacy sugar): a plain string with <c>{prop}</c> tokens expanded
    /// against the feature's properties — <c>"{icon}"</c> → the <c>icon</c> property; an unknown token → an
    /// empty substring; a literal with no braces → itself.</item>
    /// <item><b>Expression array</b>: <c>["get",…]</c>/<c>["coalesce",…]</c>/<c>["concat",…]</c> parsed by the
    /// existing <see cref="ExpressionParser"/> and evaluated, then rendered to string (spec <c>to-string</c>).</item>
    /// </list>
    /// Returns <c>null</c> when NO icon should be produced — the field is absent, or the resolved name is
    /// empty/whitespace. Engine-free; clean-room (public Style Spec — <c>icon-image</c> token syntax +
    /// expressions).
    /// </summary>
    public static class IconImageResolver
    {
        /// <summary>
        /// Resolve <paramref name="iconImage"/> (a layer's raw <c>icon-image</c> JSON) for
        /// <paramref name="feature"/>. Returns the sprite name, or <c>null</c> to skip the icon.
        /// </summary>
        public static string Resolve(JsonValue iconImage, IFeature feature)
        {
            if (iconImage == null || feature == null) return null;

            string resolved;
            if (iconImage.Kind == JsonKind.String)
            {
                resolved = ExpandTokens(iconImage.AsString(string.Empty), feature);
            }
            else if (iconImage.IsArray)
            {
                resolved = EvaluateExpression(iconImage, feature);
            }
            else
            {
                // A bare number/bool icon-image is unusual; render it via to-string for robustness.
                resolved = iconImage.Kind == JsonKind.Number || iconImage.Kind == JsonKind.Bool
                    ? iconImage.AsString(null)
                    : null;
            }

            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }

        // Expand every {prop} token against the feature; unknown/absent tokens contribute an empty string.
        private static string ExpandTokens(string template, IFeature feature)
        {
            if (string.IsNullOrEmpty(template)) return template;
            if (template.IndexOf('{') < 0) return template; // literal — no tokens

            var sb = new StringBuilder(template.Length);
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{')
                {
                    int close = template.IndexOf('}', i + 1);
                    if (close < 0)
                    {
                        sb.Append(template, i, template.Length - i); // unterminated brace → literal tail
                        break;
                    }
                    string key = template.Substring(i + 1, close - i - 1);
                    if (feature.TryGetProperty(key, out Value value))
                        sb.Append(value.ToDisplayString()); // spec to-string; null → "" (handled in ToDisplayString)
                    // unknown token → empty substring
                    i = close + 1;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        // Parse + evaluate an expression-form icon-image. Any parse/eval failure → null (skip).
        private static string EvaluateExpression(JsonValue expressionJson, IFeature feature)
        {
            try
            {
                Expression expr = ParsedExpressions.GetValue(expressionJson, ParseCallback);
                Value result = expr.Evaluate(new EvaluationContext(0.0, feature));
                return result.IsNull ? null : result.ToDisplayString();
            }
            catch
            {
                return null;
            }
        }

        // ---- expression-parse memo ---------------------------------------------------------------------
        //
        // Resolve() runs once per selected feature (SymbolFeatureExtractor's selection loop), and the
        // expression-array form of a given layer's `icon-image` is the SAME JsonValue node on every one of
        // those calls — re-parsing it per feature is pure waste. Mirrors FeatureSelector.FilterFor's memo
        // (Assets/Code/MapRenderer.Jobs/Tiles/FeatureSelector.cs) verbatim: a ConditionalWeakTable keyed on
        // the JSON node (not on the owning StyleLayer, which is mutable — see FilterFor's own doc comment
        // for why), so a re-parse only happens if the node itself is replaced. Tile processing runs off the
        // main thread, hence the thread-safe CWT rather than a plain Dictionary; a racing pair of callers may
        // both parse, but only one Expression is published and Parse is pure, so the loser is harmless
        // garbage. A malformed expression throws out of GetValue exactly as it did before memoization, and
        // nothing is cached, so the next call throws too.
        private static readonly ConditionalWeakTable<JsonValue, Expression> ParsedExpressions =
            new ConditionalWeakTable<JsonValue, Expression>();

        // Hoisted so the lookup allocates no delegate per call.
        private static readonly ConditionalWeakTable<JsonValue, Expression>.CreateValueCallback ParseCallback =
            json => ExpressionParser.Parse(json);
    }
}
