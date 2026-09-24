using MapRenderer.Core.Json;

namespace MapRenderer.Core.Filters
{
    /// <summary>
    /// Decides whether a filter <see cref="JsonValue"/> is a MapLibre expression (for
    /// <see cref="Expressions.ExpressionParser"/>) or a legacy filter (for <see cref="LegacyFilterTranslator"/>),
    /// per the Style Spec "Other filter" section. An empty array or a non-string head is an expression;
    /// <c>!has</c>/<c>!in</c>/<c>none</c> are legacy; expression-only operators are expressions. An overlapping
    /// operator (<c>==</c>, <c>in</c>, <c>has</c>, <c>all</c>, …) is decided by its operand form.
    /// </summary>
    public static class FilterDialect
    {
        /// <summary>
        /// Returns <c>true</c> if <paramref name="filter"/> should be treated as an expression filter;
        /// <c>false</c> if it should be translated as a legacy filter.
        ///
        /// Preconditions: filter is non-null and is an array (bare non-array values are handled in
        /// <see cref="CompiledFilter.Compile"/> before reaching this method).
        /// </summary>
        public static bool IsExpressionFilter(JsonValue filter)
        {
            if (filter == null || !filter.IsArray) return false;

            var items = filter.Items;
            if (items.Count == 0) return true; // empty array → pass to parser (will error)
            if (items[0].Kind != JsonKind.String) return true; // non-string op → expression parser handles

            string op = items[0].AsString();

            // These operators exist ONLY in the legacy filter syntax:
            if (op == "!has" || op == "!in" || op == "none")
                return false;

            // These operators exist ONLY in the expression syntax:
            if (IsExpressionOnlyOp(op))
                return true;

            // Overlapping operators (==, !=, <, <=, >, >=, in, has, all, any) disambiguate by operand form:
            // a bare key string cannot be an array, and an expression operand can.
            if (op == "all" || op == "any")
            {
                // all/any with no children: ambiguous, treat as expression (no-op AllExpression returns true)
                if (items.Count == 1) return true;
                // Each child is a sub-filter (legacy ["==","key","val"] or expression ["==",["get","k"],val]);
                // if any child routes as an expression, the parent all/any is an expression too.
                for (int i = 1; i < items.Count; i++)
                {
                    if (IsExpressionFilter(items[i])) return true;
                }
                return false;
            }

            // Comparisons: legacy is ["op", "key", scalar], exactly 3 elements with a bare string key. An array
            // operand or another element count is an expression.
            if (op == "==" || op == "!=" || op == "<" || op == "<=" || op == ">" || op == ">=")
            {
                // Must have exactly 2 args (3 elements total) for legacy form.
                if (items.Count != 3) return true; // expression (different arity)
                // If either operand is an array, it's an expression.
                if (items[1].IsArray || items[2].IsArray) return true;
                return false; // legacy: ["op","key",value]
            }

            // "in": legacy is ["in", key, v1, v2, ...] with a bare string key and scalar values; expression is
            // ["in", needle, haystack], exactly 3 elements with an array-expression haystack.
            if (op == "in")
            {
                if (items.Count < 3) return true; // expression parser handles error
                // Legacy: key is a bare string, rest are scalar values.
                // Expression: needle and/or haystack are arrays (expressions).
                if (items[1].IsArray || items[2].IsArray) return true;
                return false;
            }

            // For "has": legacy form is ["has", key] (2 elements, key is bare string).
            // Expression form ["has", exprKey] or ["has", key, object].
            if (op == "has")
            {
                if (items.Count == 2 && !items[1].IsArray) return false; // legacy: ["has","key"]
                if (items.Count == 3) return true; // expression: ["has","key",object]
                return items.Count != 2 || items[1].IsArray; // expression if array key or wrong arity
            }

            // Unknown op: assume expression (forward-compat; ExpressionParser will surface the error).
            return true;
        }

        /// <summary>Returns true for operators that are expression-only (never appear in legacy filters).</summary>
        private static bool IsExpressionOnlyOp(string op)
        {
            switch (op)
            {
                case "get":
                case "!":
                case "match":
                case "case":
                case "coalesce":
                case "let":
                case "var":
                case "literal":
                case "typeof":
                case "to-number":
                case "to-string":
                case "to-boolean":
                case "to-color":
                case "to-rgba":
                case "at":
                case "length":
                case "step":
                case "interpolate":
                case "interpolate-lab":
                case "interpolate-hcl":
                case "+":
                case "-":
                case "*":
                case "/":
                case "%":
                case "^":
                case "abs":
                case "ceil":
                case "floor":
                case "round":
                case "sqrt":
                case "sin":
                case "cos":
                case "tan":
                case "asin":
                case "acos":
                case "atan":
                case "ln":
                case "log10":
                case "log2":
                case "min":
                case "max":
                case "e":
                case "pi":
                case "ln2":
                case "rgb":
                case "rgba":
                case "properties":
                case "geometry-type":
                case "id":
                case "zoom":
                case "concat":
                case "upcase":
                case "downcase":
                    return true;
                default:
                    return false;
            }
        }
    }
}
