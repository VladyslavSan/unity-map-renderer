using System;
using System.Globalization;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The spec's type coercion rules, used by <c>to-number</c>/<c>to-boolean</c>/<c>to-string</c>/
    /// <c>to-color</c> and by truthiness in <c>case</c>/<c>all</c>/<c>any</c>/<c>!</c>. Rules are taken from
    /// the public Style Spec "Types" section, NOT inferred from ECMAScript (they diverge in places).
    /// </summary>
    public static class Coercions
    {
        /// <summary>
        /// <c>to-boolean</c>: false for empty string, 0, false, null, and NaN; true for everything else.
        /// (Spec "to-boolean".) Also the truthiness used by <c>case</c>/<c>all</c>/<c>any</c>/<c>!</c>.
        /// </summary>
        public static bool ToBoolean(Value v)
        {
            switch (v.Type)
            {
                case ValueType.Boolean: return v.AsBool();
                case ValueType.Null: return false;
                case ValueType.Number:
                    double n = v.AsNumber();
                    return n != 0.0 && !double.IsNaN(n);
                case ValueType.String:
                    return v.AsString().Length != 0;
                default:
                    // color, array, object are always truthy.
                    return true;
            }
        }

        /// <summary>
        /// <c>to-number</c> of a single value (spec): null and false -> 0; true -> 1; a string parsed as a
        /// JSON number (leading/trailing whitespace allowed) -> that number; otherwise an error. A number
        /// passes through. <c>to-number</c> with multiple args tries each in turn and returns the first
        /// that succeeds (handled by the op node).
        /// </summary>
        public static bool TryToNumber(Value v, out double result)
        {
            switch (v.Type)
            {
                case ValueType.Number:
                    result = v.AsNumber();
                    return true;
                case ValueType.Null:
                case ValueType.Boolean:
                    result = (v.Type == ValueType.Boolean && v.AsBool()) ? 1.0 : 0.0;
                    return true;
                case ValueType.String:
                    // Spec: a string is converted via ECMAScript ToNumber. ToNumber("") and an all-
                    // whitespace string are 0 (NOT an error). A non-numeric, non-empty string is an error.
                    string s = v.AsString().Trim();
                    if (s.Length == 0) { result = 0.0; return true; }
                    if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out result))
                        return true;
                    result = 0.0;
                    return false;
                default:
                    result = 0.0;
                    return false;
            }
        }

        /// <summary>
        /// <c>to-string</c> of any value (spec): null -> ""; boolean -> "true"/"false"; number ->
        /// ECMAScript NumberToString; color -> "rgba(r,g,b,a)"; array/object -> JSON.stringify. Never an
        /// error.
        /// </summary>
        public static string ToStringValue(Value v) => v.ToDisplayString();

        /// <summary>
        /// <c>to-color</c> of a single value (spec): a color passes through; a string is parsed as a CSS
        /// color; an array [r,g,b] or [r,g,b,a] of numbers (0..255 channels, 0..1 alpha) becomes a color;
        /// otherwise an error. Multi-arg first-success is handled by the op node.
        /// </summary>
        public static bool TryToColor(Value v, out Color result)
        {
            switch (v.Type)
            {
                case ValueType.Color:
                    result = v.AsColor();
                    return true;
                case ValueType.String:
                    return ColorParser.TryParse(v.AsString(), out result);
                case ValueType.Array:
                    var arr = v.AsArray();
                    if ((arr.Count == 3 || arr.Count == 4))
                    {
                        double r, g, b, a = 1.0;
                        if (arr[0].Type == ValueType.Number && arr[1].Type == ValueType.Number &&
                            arr[2].Type == ValueType.Number &&
                            (arr.Count == 3 || arr[3].Type == ValueType.Number))
                        {
                            r = arr[0].AsNumber(); g = arr[1].AsNumber(); b = arr[2].AsNumber();
                            if (arr.Count == 4) a = arr[3].AsNumber();
                            result = Color.From255(r, g, b, a);
                            return true;
                        }
                    }
                    result = default;
                    return false;
                default:
                    result = default;
                    return false;
            }
        }
    }
}
