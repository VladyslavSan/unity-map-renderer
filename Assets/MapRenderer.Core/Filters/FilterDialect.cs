using MapRenderer.Core.Json;

namespace MapRenderer.Core.Filters
{
    /// <summary>
    /// Determines whether a filter <see cref="JsonValue"/> should be treated as a MapLibre expression
    /// (routed directly to <see cref="Expressions.ExpressionParser"/>) or as a legacy filter (translated
    /// first by <see cref="LegacyFilterTranslator"/>).
    ///
    /// Dialect detection rules (from the public MapLibre Style Spec "Other filter" section):
    /// <list type="bullet">
    ///   <item>Non-array → legacy (bare true/false or null); handled before this predicate in CompiledFilter.</item>
    ///   <item>Empty array or first element non-string → expression (parse error surfaced by ExpressionParser).</item>
    ///   <item>Operators only valid in legacy syntax (<c>!has</c>, <c>!in</c>, <c>none</c>) → always legacy.</item>
    ///   <item>Operators only valid in expression syntax (<c>get</c>, <c>!</c>, <c>match</c>, <c>case</c>,
    ///     math ops, <c>zoom</c>, etc.) → always expression.</item>
    ///   <item>Overlapping operators (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>,
    ///     <c>&gt;=</c>, <c>in</c>, <c>has</c>, <c>all</c>, <c>any</c>): disambiguate by operand form —
    ///     if any child operand is itself an array, it's an expression; otherwise legacy.</item>
    /// </list>
    ///
    /// Clean-room: rule table derived from the public MapLibre Style Spec, not MapLibre source.
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

            // Overlapping operators: ==, !=, <, <=, >, >=, in, has, all, any
            // Disambiguate by operand form: if any non-first-element argument is itself an array,
            // treat it as expression (a bare key string cannot be an array; an expression can be).
            // For all/any: expression form if every child is itself an array (expression sub-filter).
            if (op == "all" || op == "any")
            {
                // all/any with no children: ambiguous, treat as expression (no-op AllExpression returns true)
                if (items.Count == 1) return true;
                // Legacy: each child is itself a legacy filter array (e.g. ["==","key","val"], ["has","key"]).
                // Expression: at least one child is an expression (e.g. ["==",["get","k"],val]).
                // Rule: recursively call IsExpressionFilter on each child. If any child routes as expression,
                //       the parent all/any is also expression.
                for (int i = 1; i < items.Count; i++)
                {
                    if (IsExpressionFilter(items[i])) return true;
                }
                return false;
            }

            // For ==, !=, <, <=, >, >=: legacy form is ["op", "key", value] (exactly 3 elements,
            // with arg[0] a bare string key and arg[1] a scalar). Expression form if arg[0] is an array
            // or arg[1] is an array, or element count != 3.
            if (op == "==" || op == "!=" || op == "<" || op == "<=" || op == ">" || op == ">=")
            {
                // Must have exactly 2 args (3 elements total) for legacy form.
                if (items.Count != 3) return true; // expression (different arity)
                // If either operand is an array, it's an expression.
                if (items[1].IsArray || items[2].IsArray) return true;
                return false; // legacy: ["op","key",value]
            }

            // For "in": legacy form is ["in", key, v1, v2, ...] (3+ elements; key is bare string;
            // values are scalars). Expression form is ["in", needle, haystackArrayExpr] (exactly 3 elements,
            // but haystack is an array expression, not a string).
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
