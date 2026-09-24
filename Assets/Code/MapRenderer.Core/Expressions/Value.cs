using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// A runtime value in the expression type system — the thing expressions produce and consume. It is
    /// separate from the parse-time <see cref="MapRenderer.Core.Json.JsonValue"/> because <c>color</c> is
    /// a first-class runtime type, which a JSON DOM has no notion of. Number is a <see cref="double"/>,
    /// Array an ordered list, and Object a string-keyed map, per the spec's types.
    /// </summary>
    public readonly struct Value : IEquatable<Value>
    {
        public ValueType Type { get; }

        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;
        private readonly Color _color;
        private readonly IReadOnlyList<Value> _array;
        private readonly IReadOnlyDictionary<string, Value> _object;

        private Value(ValueType type, bool b = false, double n = 0, string s = null,
            Color color = default, IReadOnlyList<Value> array = null,
            IReadOnlyDictionary<string, Value> obj = null)
        {
            Type = type;
            _bool = b;
            _number = n;
            _string = s;
            _color = color;
            _array = array;
            _object = obj;
        }

        public static readonly Value Null = new Value(ValueType.Null);

        public static Value Bool(bool v) => new Value(ValueType.Boolean, b: v);
        public static Value Number(double v) => new Value(ValueType.Number, n: v);
        public static Value String(string v) => new Value(ValueType.String, s: v ?? string.Empty);
        public static Value OfColor(Color v) => new Value(ValueType.Color, color: v);
        public static Value Array(IReadOnlyList<Value> items)
            => new Value(ValueType.Array, array: items ?? System.Array.Empty<Value>());
        public static Value Object(IReadOnlyDictionary<string, Value> members)
            => new Value(ValueType.Object, obj: members ?? new Dictionary<string, Value>());

        public bool IsNull => Type == ValueType.Null;

        // ---- typed accessors (throw ExpressionEvaluationException on a mismatch) -------------------

        public bool AsBool()
        {
            if (Type != ValueType.Boolean)
                throw new ExpressionEvaluationException($"Expected boolean but found {ValueTypes.TypeOfName(Type)}.");
            return _bool;
        }

        public double AsNumber()
        {
            if (Type != ValueType.Number)
                throw new ExpressionEvaluationException($"Expected number but found {ValueTypes.TypeOfName(Type)}.");
            return _number;
        }

        public string AsString()
        {
            if (Type != ValueType.String)
                throw new ExpressionEvaluationException($"Expected string but found {ValueTypes.TypeOfName(Type)}.");
            return _string;
        }

        public Color AsColor()
        {
            if (Type != ValueType.Color)
                throw new ExpressionEvaluationException($"Expected color but found {ValueTypes.TypeOfName(Type)}.");
            return _color;
        }

        /// <summary>
        /// Color accessor for color-typed contexts (paint colors, interpolate/match/step color outputs),
        /// applying the spec's <c>to-color</c> coercion: a Color passes through; a CSS color <b>string</b>
        /// (hex / rgb(a) / hsl(a) / named) is parsed. Use it at color seams, where production styles write
        /// string literals; keep <see cref="AsColor"/> for strict type checks.
        /// </summary>
        public Color AsColorCoerced()
        {
            if (Type == ValueType.Color) return _color;
            if (Coercions.TryToColor(this, out Color c)) return c;
            throw new ExpressionEvaluationException(
                $"Expected color (or a color string) but found {ValueTypes.TypeOfName(Type)}.");
        }

        public IReadOnlyList<Value> AsArray()
        {
            if (Type != ValueType.Array)
                throw new ExpressionEvaluationException($"Expected array but found {ValueTypes.TypeOfName(Type)}.");
            return _array;
        }

        public IReadOnlyDictionary<string, Value> AsObject()
        {
            if (Type != ValueType.Object)
                throw new ExpressionEvaluationException($"Expected object but found {ValueTypes.TypeOfName(Type)}.");
            return _object;
        }

        // ---- equality (spec ==/!= deep-equality semantics) ----------------------------------------

        public bool Equals(Value other)
        {
            if (Type != other.Type) return false;
            switch (Type)
            {
                case ValueType.Null: return true;
                case ValueType.Boolean: return _bool == other._bool;
                case ValueType.Number: return _number == other._number;
                case ValueType.String: return string.Equals(_string, other._string, StringComparison.Ordinal);
                case ValueType.Color: return _color.Equals(other._color);
                case ValueType.Array:
                    if (_array.Count != other._array.Count) return false;
                    for (int i = 0; i < _array.Count; i++)
                        if (!_array[i].Equals(other._array[i])) return false;
                    return true;
                case ValueType.Object:
                    if (_object.Count != other._object.Count) return false;
                    foreach (var kv in _object)
                        if (!other._object.TryGetValue(kv.Key, out var ov) || !kv.Value.Equals(ov))
                            return false;
                    return true;
                default: return false;
            }
        }

        public override bool Equals(object obj) => obj is Value v && Equals(v);

        public override int GetHashCode()
        {
            switch (Type)
            {
                case ValueType.Boolean: return _bool.GetHashCode();
                case ValueType.Number: return _number.GetHashCode();
                case ValueType.String: return _string?.GetHashCode() ?? 0;
                case ValueType.Color: return _color.GetHashCode();
                default: return (int)Type;
            }
        }

        // ---- ECMAScript-style number formatting (for to-string) -----------------------------------

        /// <summary>
        /// Render a double the way the spec's <c>to-string</c> expects: integral values without a trailing
        /// ".0", invariant culture, shortest round-trippable form otherwise. (ECMAScript NumberToString.)
        /// </summary>
        public static string FormatNumber(double n)
        {
            if (double.IsNaN(n)) return "NaN";
            if (double.IsPositiveInfinity(n)) return "Infinity";
            if (double.IsNegativeInfinity(n)) return "-Infinity";
            // "R" round-trips; for integral doubles it already omits the decimal point.
            return n.ToString("R", CultureInfo.InvariantCulture);
        }

        // ---- to-string rendering of any value (spec) ----------------------------------------------

        public string ToDisplayString()
        {
            switch (Type)
            {
                case ValueType.Null: return string.Empty;            // spec: null -> ""
                case ValueType.Boolean: return _bool ? "true" : "false";
                case ValueType.Number: return FormatNumber(_number);
                case ValueType.String: return _string;
                case ValueType.Color: return _color.ToRgbaString();
                case ValueType.Array:
                case ValueType.Object:
                    return ToJsonString();                           // spec: arrays/objects -> JSON.stringify
                default: return string.Empty;
            }
        }

        private string ToJsonString()
        {
            var sb = new StringBuilder();
            WriteJson(sb);
            return sb.ToString();
        }

        private void WriteJson(StringBuilder sb)
        {
            switch (Type)
            {
                case ValueType.Null: sb.Append("null"); break;
                case ValueType.Boolean: sb.Append(_bool ? "true" : "false"); break;
                case ValueType.Number: sb.Append(FormatNumber(_number)); break;
                case ValueType.String: WriteJsonString(sb, _string); break;
                case ValueType.Color: WriteJsonString(sb, _color.ToRgbaString()); break;
                case ValueType.Array:
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].WriteJson(sb);
                    }
                    sb.Append(']');
                    break;
                case ValueType.Object:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in _object)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteJsonString(sb, kv.Key);
                        sb.Append(':');
                        kv.Value.WriteJson(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
        }

        public override string ToString() => ToDisplayString();
    }
}
