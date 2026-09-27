using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;

namespace MapRenderer.Unity.Style
{
    /// <summary>
    /// A style layer's <c>filter</c> (Style Spec <c>layers[].filter</c>), parsed once at style load.
    /// <see cref="Parse"/> never throws: a malformed filter yields an instance whose <see cref="Compiled"/>
    /// is <see langword="null"/> and <see cref="Error"/> carries the parse message. <see cref="Raw"/> keeps
    /// the authored JSON, which <c>NativeFilterCompiler</c> compiles from directly.
    /// </summary>
    public sealed class LayerFilter
    {
        /// <summary>The authored filter JSON, unparsed — <c>NativeFilterCompiler</c>'s input.</summary>
        internal JsonValue Raw { get; }

        /// <summary>The compiled managed filter, or <see langword="null"/> iff <see cref="Error"/> is set.</summary>
        internal CompiledFilter Compiled { get; }

        /// <summary>The failed compile's message, or <see langword="null"/> when the filter compiled.</summary>
        internal string Error { get; }

        private LayerFilter(JsonValue raw, CompiledFilter compiled, string error)
        {
            Raw = raw;
            Compiled = compiled;
            Error = error;
        }

        /// <summary>Parses <paramref name="json"/> (a layer's <c>filter</c> sub-tree). Returns
        /// <see langword="null"/> for a null/absent filter; otherwise never throws.</summary>
        internal static LayerFilter Parse(JsonValue json)
        {
            if (json == null || json.IsNull)
                return null;

            try
            {
                return new LayerFilter(json, CompiledFilter.Compile(json), error: null);
            }
            catch (ExpressionParseException ex)
            {
                return new LayerFilter(json, compiled: null, ex.Message);
            }
        }
    }
}
