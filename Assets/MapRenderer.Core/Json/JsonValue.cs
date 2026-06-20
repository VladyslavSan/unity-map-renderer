using System.Collections.Generic;
using System.Globalization;

namespace MapRenderer.Core.Json
{
    /// <summary>The kind of value a <see cref="JsonValue"/> holds.</summary>
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// A minimal, dependency-free JSON DOM node (clean-room; no engine, no external libs — same
    /// posture as <c>Mvt/ProtobufReader.cs</c>). Holds parsed JSON verbatim, which is what lets the
    /// style model retain unknown / forward-compat sub-trees (<c>paint</c>/<c>layout</c>/<c>filter</c>
    /// and tolerated root keys) without typing or discarding them.
    ///
    /// Numbers are stored as <see cref="double"/> (JSON has a single numeric type); accessors expose
    /// integer/bool/string views. All accessors are tolerant: a type mismatch returns the supplied
    /// fallback rather than throwing, so callers stay forward-compatible.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; }

        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;
        private readonly List<JsonValue> _array;
        private readonly Dictionary<string, JsonValue> _object;

        private JsonValue(JsonKind kind)
        {
            Kind = kind;
        }

        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);

        // Factory methods (typed payload is set via the private value constructors below).
        public static JsonValue OfBool(bool v) => new JsonValue(v);
        public static JsonValue OfNumber(double v) => new JsonValue(v);
        public static JsonValue OfString(string v) => new JsonValue(v ?? string.Empty);
        public static JsonValue OfArray(List<JsonValue> items) => new JsonValue(items ?? new List<JsonValue>());
        public static JsonValue OfObject(Dictionary<string, JsonValue> members)
            => new JsonValue(members ?? new Dictionary<string, JsonValue>());

        private JsonValue(bool v) : this(JsonKind.Bool) { _bool = v; }
        private JsonValue(double v) : this(JsonKind.Number) { _number = v; }
        private JsonValue(string v) : this(JsonKind.String) { _string = v; }
        private JsonValue(List<JsonValue> v) : this(JsonKind.Array) { _array = v; }
        private JsonValue(Dictionary<string, JsonValue> v) : this(JsonKind.Object) { _object = v; }

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;

        // ---- scalar accessors -------------------------------------------------------------------

        public bool AsBool(bool fallback = false) => Kind == JsonKind.Bool ? _bool : fallback;

        public double AsDouble(double fallback = 0.0) => Kind == JsonKind.Number ? _number : fallback;

        public int AsInt(int fallback = 0)
            => Kind == JsonKind.Number ? (int)System.Math.Round(_number) : fallback;

        public string AsString(string fallback = null)
            => Kind == JsonKind.String ? _string : fallback;

        // ---- container accessors ----------------------------------------------------------------

        /// <summary>The array elements (empty for non-arrays). Never null.</summary>
        public IReadOnlyList<JsonValue> Items
            => _array ?? (IReadOnlyList<JsonValue>)System.Array.Empty<JsonValue>();

        /// <summary>The object members (empty for non-objects). Never null.</summary>
        public IReadOnlyDictionary<string, JsonValue> Members
            => _object ?? EmptyObject;

        private static readonly Dictionary<string, JsonValue> EmptyObject = new Dictionary<string, JsonValue>();

        /// <summary>True and yields the member when this is an object containing <paramref name="key"/>.</summary>
        public bool TryGet(string key, out JsonValue value)
        {
            if (_object != null && key != null && _object.TryGetValue(key, out value))
                return true;
            value = null;
            return false;
        }

        /// <summary>The member for <paramref name="key"/>, or null if absent / not an object.</summary>
        public JsonValue Get(string key)
            => TryGet(key, out var v) ? v : null;

        /// <summary>Member as string (or fallback if absent / not a string).</summary>
        public string GetString(string key, string fallback = null)
            => TryGet(key, out var v) ? v.AsString(fallback) : fallback;

        /// <summary>Member as double (or fallback if absent / not a number).</summary>
        public double GetDouble(string key, double fallback = 0.0)
            => TryGet(key, out var v) && v.Kind == JsonKind.Number ? v.AsDouble() : fallback;

        /// <summary>Member as int (or fallback if absent / not a number).</summary>
        public int GetInt(string key, int fallback = 0)
            => TryGet(key, out var v) && v.Kind == JsonKind.Number ? v.AsInt() : fallback;

        /// <summary>
        /// Member as a nullable double: null when the key is absent or not a number (used for
        /// layer <c>minzoom</c>/<c>maxzoom</c>, which the spec lists with no default).
        /// </summary>
        public double? GetNullableDouble(string key)
            => TryGet(key, out var v) && v.Kind == JsonKind.Number ? v.AsDouble() : (double?)null;

        public override string ToString()
        {
            switch (Kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return _bool ? "true" : "false";
                case JsonKind.Number: return _number.ToString(CultureInfo.InvariantCulture);
                case JsonKind.String: return _string;
                case JsonKind.Array: return $"[{_array.Count} items]";
                case JsonKind.Object: return $"{{{_object.Count} members}}";
                default: return "?";
            }
        }
    }
}
