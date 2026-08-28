using MapRenderer.Core.Expressions;
using Unity.Burst;

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
    [BurstCompile]
    internal readonly struct NativeValue
    {
        internal readonly ValueType Type { get; }
        internal readonly int       StringId;
        internal readonly double    Number;

        private NativeValue(ValueType type, double number = 0.0, int stringId = -1)
        {
            Number   = number;
            Type     = type;
            StringId = stringId;
        }

        internal static readonly NativeValue Null = new (ValueType.Null);
        internal static          NativeValue Bool(bool     v)        => new(ValueType.Boolean, v ? 1.0 : 0.0);
        internal static          NativeValue Numeric(double v)        => new(ValueType.Number, v);
        internal static          NativeValue String(int    stringId) => new(ValueType.String, stringId: stringId);

        /// <summary>
        /// Non-throwing boolean read for a <see cref="ValueType.Boolean"/>-typed value — the VM's
        /// counterpart of <see cref="Value.AsBool"/>, whose throw a Burst job cannot use. Callers check
        /// <see cref="Type"/> themselves and thread <c>NativeFilterError.NonBoolean</c> instead of ever
        /// reading this blind; this accessor is meaningless (not an error) on a non-Boolean value.
        /// </summary>
        internal bool BoolValue => Number != 0.0;

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
                case ValueType.Null:    return true;
                case ValueType.Boolean: return a.BoolValue == b.BoolValue;
                case ValueType.Number:  return a.Number    == b.Number;
                case ValueType.String:  return a.StringId  == b.StringId;
                default:                return false;
            }
        }

        /// <summary>
        /// Mirrors <c>DecisionOps.CompareValues</c>'s type gate for the shapes the VM's compile-time
        /// restriction (one <c>get</c> operand, one number literal — see <see cref="NativeFilterCompiler"/>)
        /// can ever produce: true only when both operands are <see cref="ValueType.Number"/>, with
        /// <paramref name="cmp"/> set via <see cref="System.Double.CompareTo(double)"/> (not raw
        /// <c>&lt;</c>, to match managed's <c>CompareTo</c> exactly); otherwise false and
        /// <paramref name="cmp"/> is 0 — the type-mismatch case managed throws on and
        /// <c>CompiledFilter</c> catches into an exclude. String-vs-string ordering is unreachable under
        /// that restriction, so it is not modeled here. Never throws.
        /// </summary>
        internal static bool TryCompare(in NativeValue a, in NativeValue b, out int cmp)
        {
            if (a.Type != ValueType.Number || b.Type != ValueType.Number)
            {
                cmp = 0;
                return false;
            }

            cmp = a.Number.CompareTo(b.Number);
            return true;
        }
    }
}