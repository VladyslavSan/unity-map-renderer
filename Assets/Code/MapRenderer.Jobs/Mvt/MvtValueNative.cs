using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// A decoded MVT Value sub-message (MVT spec §4.4): string, number, bool or null. It is blittable, so a
    /// layer's value table lives in one <see cref="MvtLayer.Values"/> <c>NativeArray</c>. A string is a
    /// private id into the layer's <see cref="MvtLayer.ValueStrings"/> side table, and <see cref="ToValue"/>
    /// is the read boundary that rebuilds the expression <see cref="Value"/>. See
    /// <see cref="MvtDecoder.DecodeValue"/> for the wire-format mapping.
    /// </summary>
    public readonly struct MvtValueNative
    {
        // The double comes first, so the struct packs to 16 B; an int-sized field first would pad it to 24 B.
        // MvtValueCompactionTests pins the size.
        private readonly double _number;

        public ValueType Type { get; }

        private readonly int _stringId;

        private MvtValueNative(ValueType type, double number = 0, int stringId = -1)
        {
            _number = number;
            Type = type;
            _stringId = stringId;
        }

        public static readonly MvtValueNative Null = new MvtValueNative(ValueType.Null);

        /// <param name="stringId">Index into the owning layer's <see cref="MvtLayer.ValueStrings"/> table.</param>
        public static MvtValueNative String(int stringId) => new MvtValueNative(ValueType.String, stringId: stringId);
        public static MvtValueNative Number(double v) => new MvtValueNative(ValueType.Number, number: v);
        public static MvtValueNative Bool(bool v) => new MvtValueNative(ValueType.Boolean, number: v ? 1.0 : 0.0);

        /// <summary>
        /// Reconstitutes the shared expression <see cref="Value"/> this table entry stands in for — the
        /// single read-boundary conversion, alloc-free (an index into <paramref name="stringTable"/> plus
        /// struct-field copies). No bounds check: <see cref="MvtDecoder"/> is this type's sole producer and
        /// always sets a valid <see cref="_stringId"/>; the out-of-range tag-pair skip-tolerance lives in the
        /// two callers, which bound-check the value index before ever calling this.
        /// </summary>
        /// <param name="stringTable">The owning layer's <see cref="MvtLayer.ValueStrings"/> side table.</param>
        public Value ToValue(string[] stringTable)
        {
            switch (Type)
            {
                case ValueType.String: return Value.String(stringTable[_stringId]);
                case ValueType.Number: return Value.Number(_number);
                case ValueType.Boolean: return Value.Bool(_number != 0.0);
                default: return Value.Null;
            }
        }

        /// <summary>
        /// The native filter VM's Burst-safe counterpart of <see cref="ToValue"/>, returning a
        /// <see cref="NativeValue"/>. A string entry carries its raw <see cref="_stringId"/> forward, the id
        /// space <see cref="NativeFilterProgram.Rebind"/>'s literal scan also resolves into, so no string
        /// table is needed.
        /// </summary>
        internal NativeValue ToNativeValue()
        {
            switch (Type)
            {
                case ValueType.String: return NativeValue.String(_stringId);
                case ValueType.Number: return NativeValue.Numeric(_number);
                case ValueType.Boolean: return NativeValue.Bool(_number != 0.0);
                default: return NativeValue.Null;
            }
        }
    }
}
