using System.Collections.Generic;
using MapRenderer.Core.Json;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Core.Filters
{
    /// <summary>
    /// Translates a legacy filter array into an expression tree (Style Spec "Other filter" section).
    /// <c>has</c> passes through; <c>["op","key",v]</c> → <c>["op",keyExpr(key),v]</c>; <c>in</c> wraps
    /// its values in <c>["literal",[…]]</c>; <c>!has</c>/<c>!in</c> wrap the positive form in <c>"!"</c>;
    /// <c>all</c>/<c>any</c> translate each child; <c>none</c> → <c>["!",["any",…]]</c>. keyExpr: <c>"$type"</c>
    /// → <c>["geometry-type"]</c>, <c>"$id"</c> → <c>["id"]</c>, any other key → <c>["get",key]</c>.
    /// </summary>
    public static class LegacyFilterTranslator
    {
        /// <summary>
        /// Translates <paramref name="legacy"/> into an equivalent expression <see cref="JsonValue"/>.
        /// </summary>
        /// <exception cref="ExpressionParseException">If the legacy filter is malformed.</exception>
        public static JsonValue Translate(JsonValue legacy)
        {
            if (legacy == null || !legacy.IsArray)
                throw new ExpressionParseException("Legacy filter must be a non-null array.");

            var items = legacy.Items;
            if (items.Count == 0)
                throw new ExpressionParseException("Legacy filter array must not be empty.");

            if (items[0].Kind != JsonKind.String)
                throw new ExpressionParseException("Legacy filter operator must be a string.");

            string op = items[0].AsString();

            switch (op)
            {
                case "has":
                {
                    // ["has","key"] -> ["has","key"] (expression "has" with one string arg is valid)
                    if (items.Count != 2 || items[1].Kind != JsonKind.String)
                        throw new ExpressionParseException(
                            $"Legacy \"has\" expects [\"has\", string-key], got {items.Count} element(s).");
                    return MakeArray(JsonValue.OfString("has"), items[1]);
                }

                case "!has":
                {
                    // ["!has","key"] -> ["!",["has","key"]]
                    if (items.Count != 2 || items[1].Kind != JsonKind.String)
                        throw new ExpressionParseException(
                            $"Legacy \"!has\" expects [\"!has\", string-key], got {items.Count} element(s).");
                    var inner = MakeArray(JsonValue.OfString("has"), items[1]);
                    return MakeArray(JsonValue.OfString("!"), inner);
                }

                case "==":
                case "!=":
                case "<":
                case "<=":
                case ">":
                case ">=":
                {
                    // ["op","key",value] -> ["op",keyExpr(key),value]
                    if (items.Count != 3)
                        throw new ExpressionParseException(
                            $"Legacy \"{op}\" expects 3 elements, got {items.Count}.");
                    if (items[1].Kind != JsonKind.String)
                        throw new ExpressionParseException(
                            $"Legacy \"{op}\" key must be a string, got {items[1].Kind}.");
                    return MakeArray(JsonValue.OfString(op), KeyExpr(items[1].AsString()), items[2]);
                }

                case "in":
                {
                    // ["in","key",v1,v2,...] -> ["in",keyExpr(key),["literal",[v1,v2,...]]]
                    if (items.Count < 3)
                        throw new ExpressionParseException(
                            $"Legacy \"in\" expects at least 3 elements, got {items.Count}.");
                    if (items[1].Kind != JsonKind.String)
                        throw new ExpressionParseException(
                            "Legacy \"in\" key must be a string.");
                    var values = BuildValueArray(items, 2);
                    var literal = MakeLiteral(values);
                    return MakeArray(JsonValue.OfString("in"), KeyExpr(items[1].AsString()), literal);
                }

                case "!in":
                {
                    // ["!in","key",v1,...] -> ["!",["in",keyExpr(key),["literal",[...]]]]
                    if (items.Count < 3)
                        throw new ExpressionParseException(
                            $"Legacy \"!in\" expects at least 3 elements, got {items.Count}.");
                    if (items[1].Kind != JsonKind.String)
                        throw new ExpressionParseException(
                            "Legacy \"!in\" key must be a string.");
                    var values = BuildValueArray(items, 2);
                    var literal = MakeLiteral(values);
                    var inner = MakeArray(JsonValue.OfString("in"), KeyExpr(items[1].AsString()), literal);
                    return MakeArray(JsonValue.OfString("!"), inner);
                }

                case "all":
                {
                    // ["all",f1,...] -> ["all",translate(f1),...]
                    var list = new List<JsonValue> { JsonValue.OfString("all") };
                    for (int i = 1; i < items.Count; i++)
                        list.Add(Translate(items[i]));
                    return JsonValue.OfArray(list);
                }

                case "any":
                {
                    // ["any",f1,...] -> ["any",translate(f1),...]
                    var list = new List<JsonValue> { JsonValue.OfString("any") };
                    for (int i = 1; i < items.Count; i++)
                        list.Add(Translate(items[i]));
                    return JsonValue.OfArray(list);
                }

                case "none":
                {
                    // ["none",f1,...] -> ["!",["any",translate(f1),...]]
                    var anyList = new List<JsonValue> { JsonValue.OfString("any") };
                    for (int i = 1; i < items.Count; i++)
                        anyList.Add(Translate(items[i]));
                    var anyExpr = JsonValue.OfArray(anyList);
                    return MakeArray(JsonValue.OfString("!"), anyExpr);
                }

                default:
                    throw new ExpressionParseException(
                        $"Unknown legacy filter operator \"{op}\".");
            }
        }

        // ---- Helpers -----------------------------------------------------------------------

        /// <summary>
        /// Maps a legacy filter key to the equivalent expression:
        /// "$type" → ["geometry-type"], "$id" → ["id"], other → ["get","key"].
        /// </summary>
        private static JsonValue KeyExpr(string key)
        {
            if (key == "$type")
                return MakeArray(JsonValue.OfString("geometry-type"));
            if (key == "$id")
                return MakeArray(JsonValue.OfString("id"));
            return MakeArray(JsonValue.OfString("get"), JsonValue.OfString(key));
        }

        /// <summary>Builds a JSON array from <paramref name="start"/> index of <paramref name="items"/>.</summary>
        private static JsonValue BuildValueArray(System.Collections.Generic.IReadOnlyList<JsonValue> items, int start)
        {
            var arr = new List<JsonValue>();
            for (int i = start; i < items.Count; i++)
                arr.Add(items[i]);
            return JsonValue.OfArray(arr);
        }

        /// <summary>Wraps a value array in <c>["literal", array]</c>.</summary>
        private static JsonValue MakeLiteral(JsonValue valueArray)
        {
            return MakeArray(JsonValue.OfString("literal"), valueArray);
        }

        /// <summary>Constructs a <see cref="JsonValue"/> array from the given elements.</summary>
        private static JsonValue MakeArray(params JsonValue[] elements)
        {
            var list = new List<JsonValue>(elements);
            return JsonValue.OfArray(list);
        }
    }
}
