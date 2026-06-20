namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The runtime types of the MapLibre expression type system (public Style Spec, "Types" section).
    /// The spec's expression value space is: null, boolean, number, string, color, array, object — plus
    /// the "value" supertype used where any of these is accepted. We model that supertype as a parse-time
    /// concept (it never appears as a concrete <see cref="Value"/> tag), so the concrete tags here are the
    /// six listed runtime types.
    ///
    /// Clean-room: type names and the type set are taken from the public Style Spec docs only.
    /// </summary>
    public enum ValueType
    {
        Null,
        Boolean,
        Number,
        String,
        Color,
        Array,
        Object,

        /// <summary>
        /// The spec's <c>value</c> supertype: "any value accepted". Used only as a declared/assertion type
        /// during parsing (e.g. <c>get</c> returns <c>value</c>); a concrete <see cref="Value"/> is never
        /// tagged <c>Value</c>.
        /// </summary>
        Value
    }

    /// <summary>Maps a runtime type to the exact string the <c>typeof</c> expression returns.</summary>
    public static class ValueTypes
    {
        // typeof strings per the Style Spec "Types" section: lowercase type names.
        public static string TypeOfName(ValueType t)
        {
            switch (t)
            {
                case ValueType.Null: return "null";
                case ValueType.Boolean: return "boolean";
                case ValueType.Number: return "number";
                case ValueType.String: return "string";
                case ValueType.Color: return "color";
                case ValueType.Array: return "array";
                case ValueType.Object: return "object";
                case ValueType.Value: return "value";
                default: return "value";
            }
        }
    }
}
