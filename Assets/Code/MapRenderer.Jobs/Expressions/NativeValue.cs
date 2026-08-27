using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Expressions
{
    /// <summary>
    /// A blittable, filter-only runtime value for the native filter VM — the Burst-safe counterpart of
    /// the managed <see cref="Value"/>. Field order mirrors <see cref="Mvt.MvtValueNative"/> (double
    /// first). Only the tags a <see cref="NativeFilterCompiler"/>-accepted filter can ever produce are
    /// modeled: <see cref="ValueType.Null"/>, <see cref="ValueType.Boolean"/> (1.0/0.0 in the number
    /// field), <see cref="ValueType.Number"/>, and <see cref="ValueType.String"/> (an id into a
    /// per-tile-layer value-string table, never inline bytes). Color/Array/Object never occur — no
    /// accepted filter's op subset can produce them.
    /// </summary>
    internal readonly struct NativeValue
    {
        private readonly double _number;
        internal ValueType Type { get; }
        private readonly int _stringId;

        private NativeValue(ValueType type, double number = 0.0, int stringId = -1)
        {
            _number = number;
            Type = type;
            _stringId = stringId;
        }

        internal static readonly NativeValue Null = new NativeValue(ValueType.Null);
        internal static NativeValue Bool(bool v) => new NativeValue(ValueType.Boolean, v ? 1.0 : 0.0);
        internal static NativeValue Number(double v) => new NativeValue(ValueType.Number, v);
        internal static NativeValue String(int stringId) => new NativeValue(ValueType.String, stringId: stringId);

        /// <summary>
        /// Non-throwing boolean read for a <see cref="ValueType.Boolean"/>-typed value — the VM's
        /// counterpart of <see cref="Value.AsBool"/>, whose throw a Burst job cannot use. Callers check
        /// <see cref="Type"/> themselves and thread <c>NativeFilterError.NonBoolean</c> instead of ever
        /// reading this blind; this accessor is meaningless (not an error) on a non-Boolean value.
        /// </summary>
        internal bool BoolValue => _number != 0.0;

        /// <summary>
        /// Mirrors <see cref="Value.Equals(Value)"/> exactly: tag first (a type mismatch is unequal, never
        /// an error), then <c>Null==Null</c>, Boolean compared as bool, Number by value, String by id
        /// (the space a per-tile-layer rebind — <c>MapRenderer.Jobs.Mvt.NativeFilterRebind.Rebind</c> —
        /// resolves into; see its duplicate-value-string refusal). Never throws.
        /// </summary>
        internal static bool NativeEquals(in NativeValue a, in NativeValue b)
        {
            if (a.Type != b.Type) return false;
            switch (a.Type)
            {
                case ValueType.Null: return true;
                case ValueType.Boolean: return a.BoolValue == b.BoolValue;
                case ValueType.Number: return a._number == b._number;
                case ValueType.String: return a._stringId == b._stringId;
                default: return false;
            }
        }
    }
}
