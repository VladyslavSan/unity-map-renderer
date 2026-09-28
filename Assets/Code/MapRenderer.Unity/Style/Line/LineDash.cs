using MapRenderer.Core.Json;
using MapRenderer.Core.Expressions;
using Unity.Mathematics;

namespace MapRenderer.Unity.Style.Line
{
    /// <summary>
    /// <c>line-dasharray</c> — engine-free helpers for the in-shader dash: a modulo over cumulative dash-pattern
    /// length. The fragment shader feathers the on/off edges with fwidth; round dash-caps are not implemented.
    /// Dash lengths are line-width units, so dashes scale with line-width. The unit is FIXED FOR THE FRAME (the
    /// styled width at the frame's ground resolution), so the pattern stays anchored to the ground as the
    /// camera moves. The single source of the dash function; HLSL mirror: <c>Line_VertexExtrude.hlsl</c>.
    /// </summary>
    public static class LineDash
    {
        // Slots the vertex/fragment stage carries (two float4 uniforms, _DashArray + _DashArray2).
        // A pattern with more entries than this draws solid; it is never truncated.
        public const int N = 8;

        // ── Dash coverage: CPU mirror of the HLSL fragment function; keep both in sync ──────────────
        // Non-local invariant: like the HLSL, returns 1 on a dash and 0 in a gap (the GPU adds an fwidth feather).
        // pattern = on/off lengths in line-width units, "on" first.
        // Solid (1) for a null/empty pattern, more than N entries, metersPerDashUnit <= 0, or all entries <= 0.
        //
        // An ODD-length pattern repeats over a period of 2P (P = sum of entries): the second half is the same
        // entries with on/off PARITY FLIPPED, so a lone [2] behaves like [2,2] and never doubles its own array.
        public static float DashCoverage(double distanceAlong, double metersPerDashUnit, float[] pattern)
        {
            if (pattern == null || pattern.Length == 0)
                return 1.0f;   // solid identity: no dasharray

            if (metersPerDashUnit <= 0.0)
                return 1.0f;   // degenerate dash unit → solid

            if (pattern.Length > N)
                return 1.0f;   // past N entries → solid, never truncated

            int count = pattern.Length;

            // P = sum of all on+off spans. An even count's period IS P; an odd count's is 2P (see above).
            double p = 0.0;
            for (int i = 0; i < count; i++)
                p += pattern[i] > 0 ? pattern[i] : 0.0;

            if (p <= 0.0)
                return 1.0f;   // degenerate pattern → solid

            bool odd = (count % 2) != 0;
            double period = odd ? 2.0 * p : p;

            // u = dimensionless position along the line in line-width units.
            // Mirror of: float dashU = distanceAlong / dashMetersPerUnit; in the vertex shader.
            double u = distanceAlong / metersPerDashUnit;

            // Phase = u mod period (always in [0, period)).
            double phase = u % period;
            if (phase < 0.0) phase += period;

            // In the second half of an odd-length period, re-map onto the same entries and flip parity.
            int flip = 0;
            if (odd && phase >= p) { phase -= p; flip = 1; }

            // Walk on/off runs to determine coverage at this phase.
            // Even (index + flip) = on-run; odd = off-run.
            double cursor = 0.0;
            for (int i = 0; i < count; i++)
            {
                double span = pattern[i] > 0 ? pattern[i] : 0.0;
                cursor += span;
                if (phase < cursor)
                    return ((i + flip) % 2 == 0) ? 1.0f : 0.0f;
            }

            // Should not reach here (phase < period, cursor = period).
            return 1.0f;
        }

        // ── Dasharray as an expression ─────────────────────────────────────────────────────────
        // Non-local invariant: LineRenderLayer re-evaluates the parsed dasharray per frame only when it depends on
        // zoom. A feature-dependent value errors against the null feature, so TryEvaluatePattern renders solid.

        /// <summary>
        /// Parses a <c>line-dasharray</c> property value into an expression. A dasharray may write its arrays bare
        /// (<c>[2,1]</c>, or the stop outputs of <c>["step",["zoom"],[1,1],10,[2,1]]</c>), which the strict
        /// expression parser rejects, so <see cref="ExpressionParser.WrapBareArrayLiterals"/> wraps them as
        /// <c>["literal", …]</c> at the top level and in an operator's direct arguments. Returns null for a null
        /// value (→ render solid). A malformed value throws <see cref="ExpressionParseException"/>.
        /// </summary>
        public static Expression ParseDashArray(JsonValue json)
        {
            JsonValue toParse = ExpressionParser.WrapBareArrayLiterals(json);
            return toParse == null ? null : ExpressionParser.Parse(toParse, interpolatable: false);
        }

        /// <summary>
        /// Evaluates a parsed dasharray expression at <paramref name="zoom"/> into an alloc-free packed pattern of
        /// up to <see cref="N"/> entries, split across two <c>float4</c>s. Returns false (→ render solid) when
        /// the expression is null, errors, yields a non-array, empty or non-numeric result, or has more than
        /// <see cref="N"/> entries (never truncated). The caller binds <c>_DashArray</c>/<c>_DashArray2</c>
        /// and <c>_DashCount</c>.
        /// </summary>
        /// <param name="expr">The parsed dasharray expression (from <see cref="ParseDashArray"/>).</param>
        /// <param name="zoom">The current map zoom level.</param>
        /// <param name="packedLo">Entries 0-3. Zero when <paramref name="count"/> == 0.</param>
        /// <param name="packedHi">Entries 4-7. Zero when <paramref name="count"/> &lt;= 4.</param>
        /// <param name="count">Number of valid entries (0 = solid identity; an odd count repeats — see
        /// <see cref="DashCoverage"/>).</param>
        /// <returns>True when a pattern of 1-<see cref="N"/> entries was produced; false → render solid.</returns>
        public static bool TryEvaluatePattern(Expression expr, double zoom, out float4 packedLo, out float4 packedHi, out int count)
        {
            packedLo = float4.zero;
            packedHi = float4.zero;
            count    = 0;

            if (expr == null) return false;

            // Zero-alloc fast path (UMR-222): a Zoom-kind dasharray is re-evaluated every frame
            // (LineRenderLayer re-applies it whenever DashArrayKind depends on zoom), so a zoom-interpolated
            // pattern must never build a Value array. Covers every expression kind — the base
            // Expression.TryEvaluateNumberArray unpacks a literal/step result directly; only
            // InterpolateExpression's override avoids its own Lerp allocation. A destination sized to
            // exactly N doubles as the entry-count cap: TryEvaluateNumberArray already returns false —
            // never truncating — when the source array holds more than N entries.
            System.Span<double> raw = stackalloc double[N];
            if (!expr.TryEvaluateNumberArray(new EvaluationContext(zoom, null), raw, out int n)) return false;
            if (n == 0) return false;

            // Clamp negative entries to 0, matching DashCoverage's own clamp — so the CPU mirror and the
            // packed values the shader reads never disagree over a malformed negative span.
            System.Span<float> vals = stackalloc float[N];
            for (int i = 0; i < n; i++) vals[i] = raw[i] > 0.0 ? (float)raw[i] : 0f;

            packedLo = new float4(
                n > 0 ? vals[0] : 0f, n > 1 ? vals[1] : 0f, n > 2 ? vals[2] : 0f, n > 3 ? vals[3] : 0f);
            packedHi = new float4(
                n > 4 ? vals[4] : 0f, n > 5 ? vals[5] : 0f, n > 6 ? vals[6] : 0f, n > 7 ? vals[7] : 0f);
            count = n;
            return true;
        }
    }
}
