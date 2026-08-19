using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// A decoded MVT Value sub-message (MVT spec §4.4) — the wire value space only: string, number,
    /// bool or null. This is a NARROWER domain type than the shared expression <see cref="Value"/> (24 B
    /// vs 72 B — no <c>Color</c>/Array/Object fields), not a duplicate of it: <see cref="Value"/> is
    /// reconstituted at the read boundary via <see cref="ToValue"/>. <see cref="MvtLayer.Values"/> is the
    /// per-layer table this type shrinks.
    /// </summary>
    public readonly struct MvtValue
    {
        public ValueType Type { get; }

        private readonly double _number;
        private readonly string _string;

        private MvtValue(ValueType type, double number = 0, string s = null)
        {
            Type = type;
            _number = number;
            _string = s;
        }

        public static readonly MvtValue Null = new MvtValue(ValueType.Null);

        public static MvtValue String(string v) => new MvtValue(ValueType.String, s: v ?? string.Empty);
        public static MvtValue Number(double v) => new MvtValue(ValueType.Number, number: v);
        public static MvtValue Bool(bool v) => new MvtValue(ValueType.Boolean, number: v ? 1.0 : 0.0);

        // ---- typed accessors (mirror Value's — no type-mismatch guard: MvtDecoder is this type's sole
        // producer and always tags correctly, so the throw-on-mismatch ceremony Value needs for arbitrary
        // expression input would be dead code here). ------------------------------------------------

        public string AsString() => _string;
        public double AsNumber() => _number;
        public bool AsBool() => _number != 0.0;

        /// <summary>
        /// Reconstitutes the shared expression <see cref="Value"/> this table entry stands in for — the
        /// single read-boundary conversion, alloc-free (struct-field copies only). See
        /// <see cref="MvtDecoder.DecodeValue"/> for the wire-format mapping this mirrors.
        /// </summary>
        public Value ToValue()
        {
            switch (Type)
            {
                case ValueType.String: return Value.String(_string);
                case ValueType.Number: return Value.Number(_number);
                case ValueType.Boolean: return Value.Bool(_number != 0.0);
                default: return Value.Null;
            }
        }
    }
}
