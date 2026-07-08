using System.Text;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// S105 F2 — resolves a symbol layer's <c>text-field</c> to a concrete label string for one feature.
    /// Two forms, single-section only (multi-section <c>["format", …]</c> is deferred):
    /// <list type="bullet">
    /// <item><b>Token string</b> (the legacy sugar): a plain string with <c>{prop}</c> tokens expanded
    /// against the feature's properties — <c>"{NAME}"</c> → the <c>NAME</c> property; an unknown token → an
    /// empty substring; a literal with no braces → itself.</item>
    /// <item><b>Expression array</b>: <c>["get",…]</c>/<c>["coalesce",…]</c>/<c>["concat",…]</c> parsed by the
    /// existing <see cref="ExpressionParser"/> and evaluated, then rendered to string (spec <c>to-string</c>).</item>
    /// </list>
    /// Returns <c>null</c> when NO label should be produced — the field is absent, or the resolved text is
    /// empty/whitespace (a missing property must SKIP the feature, not emit a blank label). Engine-free;
    /// clean-room (public Style Spec — <c>text-field</c> token syntax + expressions).
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
                Expression expr = ExpressionParser.Parse(expressionJson);
                Value result = expr.Evaluate(new EvaluationContext(0.0, feature));
                return result.IsNull ? null : result.ToDisplayString();
            }
            catch
            {
                return null;
            }
        }
    }
}
