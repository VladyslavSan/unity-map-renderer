using System.Runtime.CompilerServices;
using System.Text;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// Resolves a symbol layer's <c>text-field</c> to a concrete string for one feature; single-section only
    /// (no multi-section <c>["format", …]</c>). A token string expands <c>{prop}</c> against the feature's
    /// properties (an unknown token → empty). An expression array is parsed by <see cref="ExpressionParser"/>,
    /// evaluated, and rendered to string (spec <c>to-string</c>). Returns <c>null</c> when the field is absent
    /// or the text is empty/whitespace, so a missing property skips the feature instead of emitting a blank.
    /// </summary>
    public static class TextFieldResolver
    {
        /// <summary>
        /// Resolve <paramref name="textField"/> (a layer's raw <c>text-field</c> JSON) for
        /// <paramref name="feature"/>. Returns the label string, or <c>null</c> to skip the feature.
        /// </summary>
        public static string Resolve(JsonValue textField, IFeature feature)
        {
            if (textField == null || feature == null) return null;

            string resolved;
            if (textField.Kind == JsonKind.String)
            {
                resolved = ExpandTokens(textField.AsString(string.Empty), feature);
            }
            else if (textField.IsArray)
            {
                resolved = EvaluateExpression(textField, feature);
            }
            else
            {
                // A bare number/bool text-field is unusual; render it via to-string for robustness.
                resolved = textField.Kind == JsonKind.Number || textField.Kind == JsonKind.Bool
                    ? textField.AsString(null)
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

        // Parse + evaluate an expression-form text-field. Any parse/eval failure → null (skip).
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
        // Non-obvious why: Resolve() runs per selected feature, and a layer's expression-array `text-field` is
        // the same JsonValue node on every call. The memo keys on that node, as FeatureSelector.FilterFor does,
        // not on the mutable StyleLayer. The table is thread-safe for off-main tile processing; a racing double
        // parse is harmless because Parse is pure. A malformed expression throws and caches nothing.
        private static readonly ConditionalWeakTable<JsonValue, Expression> ParsedExpressions =
            new ConditionalWeakTable<JsonValue, Expression>();

        // Hoisted so the lookup allocates no delegate per call.
        private static readonly ConditionalWeakTable<JsonValue, Expression>.CreateValueCallback ParseCallback =
            json => ExpressionParser.Parse(json);
    }
}
