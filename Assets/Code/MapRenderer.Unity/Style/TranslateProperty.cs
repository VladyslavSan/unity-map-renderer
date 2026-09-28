using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Unity.Mathematics;

namespace MapRenderer.Unity.Style
{
    /// <summary>
    /// Parses a <c>*-translate</c> paint value ([x, y] px) through the expression engine, so Constant AND
    /// Zoom kinds both survive. Shared by <c>Fill</c>/<c>Line</c>/<c>FillExtrusion</c>/<c>Symbol</c> paint,
    /// which all give it the same default.
    /// </summary>
    internal static class TranslateProperty
    {
        private static readonly double2 Default = new double2(0.0, 0.0);

        /// <summary>
        /// Parses one present <c>*-translate</c> value. <c>WrapBareArrayLiterals</c> admits the constant
        /// bare <c>[x, y]</c> form, which the strict parser rejects. Feature/Composite (data-driven) is
        /// spec-invalid for a layer-level property and gives <c>[0, 0]</c>. A malformed value throws
        /// <see cref="ExpressionParseException"/> or <see cref="ExpressionEvaluationException"/>, which
        /// <see cref="PropertyReader"/> turns into the same default plus an error.
        /// </summary>
        /// <param name="json">The property's raw JSON value. Must be non-null; the caller applies
        /// <c>[0, 0]</c> when the key is absent.</param>
        internal static StyleProperty<double2> Parse(JsonValue json)
        {
            JsonValue wrapped = ExpressionParser.WrapBareArrayLiterals(json);
            var candidate = new StyleProperty<double2>(wrapped, Default,
                v =>
                {
                    var a = v.AsArray();
                    if (a.Count < 2) throw new ExpressionEvaluationException("A translate needs two numbers.");
                    return new double2(a[0].AsNumber(), a[1].AsNumber());
                },
                // Zero-alloc fast path: a Zoom-kind translate is re-evaluated every frame
                // (ZoomStyleApplier's device-pixel-vector bindings always push), so it must never build
                // a Value array (UMR-222). A short array (e.g. a malformed zoom-interpolated [5]) must
                // fall back to Default here too — this runs uncaught inside ApplyZoom, every frame.
                span => span.Length < 2 ? Default : new double2(span[0], span[1]));
            return candidate.DependsOnFeature ? new StyleProperty<double2>(Default) : candidate;
        }
    }
}
