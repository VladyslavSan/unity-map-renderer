using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// A decoded MVT Value sub-message (MVT spec §4.4) — the wire value space only: string, number, bool or
    /// null. <b>Blittable</b> (no managed field), so a layer's whole value table lives in one
    /// <see cref="MvtLayer.Values"/> <c>NativeArray</c> instead of a GC-heap <c>List</c>. A string payload is
    /// NOT carried inline: a private string-id field indexes the layer's <see cref="MvtLayer.ValueStrings"/>
    /// side table, and <see cref="ToValue"/> is the read boundary that reconstitutes the shared expression
    /// <see cref="Value"/> from the pair. See <see cref="MvtDecoder.DecodeValue"/> for the wire-format
    /// mapping this mirrors.
    /// </summary>
    public readonly struct MvtValueNative
    {
        // Field order is deliberate: double FIRST. {double, ValueType(int), int} lays out 8+4+4 = 16 B, no
        // padding. Declaring the enum-backed Type property first (as MvtValue, its managed predecessor, did)
        // would pad to 24 B instead — the double's 8-byte alignment forces a 4 B gap after a lone leading
        // int-sized field. Measured via UnsafeUtility.SizeOf in MvtValueCompactionTests.
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
        /// The native filter VM's Burst-safe counterpart of <see cref="ToValue"/>: reconstitutes this
        /// entry as a <see cref="NativeValue"/> instead of the managed <see cref="Value"/> — no string
        /// table needed, since a <see cref="ValueType.String"/> entry carries its raw <see cref="_stringId"/>
        /// forward rather than resolving bytes (the id space <see cref="NativeFilterProgram.Rebind"/>'s
        /// literal scan also resolves into). Color/Array/Object never occur (this type cannot represent
        /// them), so there is nothing to map for those tags.
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
