using System.Collections.Generic;
using MapRenderer.Core.Json;
using MapRenderer.Core.Expressions;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Line
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
        // Maximum dasharray entries we accept (covers dash, gap, dash-dot, dash-dot-dot).
        // Entries beyond this cap are silently truncated; document in callers.
        public const int MaxEntries = 4;

        // ── Dash coverage: CPU mirror of the HLSL fragment function; keep both in sync ──────────────
        // Returns 1 on a dash and 0 in a gap: a hard step; the GPU adds an fwidth feather at the same edges.
        // metersPerDashUnit is the frame-constant ruler from the class doc; a per-vertex width here would make
        // dashes crawl and depend on tessellation. pattern = on/off lengths in line-width units, "on" first.
        // Solid (1) for a null/empty or odd-length pattern, metersPerDashUnit <= 0, or all entries <= 0.
        public static float DashCoverage(double distanceAlong, double metersPerDashUnit, float[] pattern)
        {
            if (pattern == null || pattern.Length == 0)
                return 1.0f;   // solid identity: no dasharray

            if (metersPerDashUnit <= 0.0)
                return 1.0f;   // degenerate dash unit → solid

            int count = pattern.Length > MaxEntries ? MaxEntries : pattern.Length;

            // Odd-length or single entry → solid identity.
            if (count % 2 != 0)
                return 1.0f;

            // Compute period = sum of all on+off spans.
            double period = 0.0;
            for (int i = 0; i < count; i++)
                period += pattern[i] > 0 ? pattern[i] : 0.0;

            if (period <= 0.0)
                return 1.0f;   // degenerate pattern → solid

            // u = dimensionless position along the line in line-width units.
            // Mirror of: float dashU = distanceAlong / dashMetersPerUnit; in the vertex shader.
            double u = distanceAlong / metersPerDashUnit;

            // Phase = u mod period (always in [0, period)).
            double phase = u % period;
            if (phase < 0.0) phase += period;

            // Walk on/off runs to determine coverage at this phase.
            // Even indices = on-runs; odd indices = off-runs.
            double cursor = 0.0;
            for (int i = 0; i < count; i++)
            {
                double span = pattern[i] > 0 ? pattern[i] : 0.0;
                cursor += span;
                if (phase < cursor)
                    return (i % 2 == 0) ? 1.0f : 0.0f; // even=on, odd=off
            }

            // Should not reach here (phase < period, cursor = period).
            return 1.0f;
        }

        // ── Dasharray as an expression ─────────────────────────────────────────────────────────
        // line-dasharray goes through the expression system: parsed once, then evaluated to a pattern at a zoom.
        // LineRenderLayer re-evaluates it per frame only when it depends on zoom. It is data-constant in the
        // spec: a feature-dependent value errors against the null feature, so TryEvaluatePattern renders solid.

        /// <summary>
        /// Parses a <c>line-dasharray</c> property value into an expression. A dasharray may write its arrays bare
        /// (<c>[2,1]</c>, or the stop outputs of <c>["step",["zoom"],[1,1],10,[2,1]]</c>), which the strict
        /// expression parser rejects, so <see cref="ExpressionParser.WrapBareArrayLiterals"/> wraps them as
        /// <c>["literal", …]</c> at the top level and in an operator's direct arguments. Returns null for a null
        /// or malformed value (→ render solid); never throws.
        /// </summary>
        public static Expression ParseDashArray(JsonValue json)
        {
            JsonValue toParse = ExpressionParser.WrapBareArrayLiterals(json);
            if (toParse == null) return null;
            try { return ExpressionParser.Parse(toParse); }
            catch (ExpressionParseException) { return null; }
        }

        /// <summary>
        /// Evaluates a parsed dasharray expression at <paramref name="zoom"/> into an alloc-free packed pattern of
        /// at most <see cref="MaxEntries"/> entries. Returns false (→ render solid) when the expression is null,
        /// errors, or yields a non-array, empty or non-numeric result. <paramref name="count"/> == 0 also means
        /// solid (odd-length, degenerate or missing pattern). The caller binds <c>_DashArray</c> and
        /// <c>_DashCount</c>.
        /// </summary>
        /// <param name="expr">The parsed dasharray expression (from <see cref="ParseDashArray"/>).</param>
        /// <param name="zoom">The current map zoom level.</param>
        /// <param name="packed">The pattern values packed into x/y/z/w (Unity.Mathematics <c>float4</c>).
        ///   Zero when <paramref name="count"/> == 0.</param>
        /// <param name="count">Number of valid entries in <paramref name="packed"/> (0 or 2 or 4 —
        ///   always even; 0 = solid identity).</param>
        /// <returns>True when a valid even-length pattern was produced; false → render solid.</returns>
        public static bool TryEvaluatePattern(Expression expr, double zoom, out float4 packed, out int count)
        {
            packed = float4.zero;
            count  = 0;

            if (expr == null) return false;
            if (!expr.TryEvaluate(new EvaluationContext(zoom, null), out Value v, out _)) return false;
            if (v.Type != ValueType.Array) return false;

            IReadOnlyList<Value> items = v.AsArray();
            if (items == null || items.Count == 0) return false;

            int n = items.Count > MaxEntries ? MaxEntries : items.Count;

            // Odd-length → solid identity (count = 0 sentinel).
            if (n % 2 != 0) return false;

            float[] vals = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (!Coercions.TryToNumber(items[i], out double d)) return false;
                vals[i] = (float)d;
            }

            packed = new float4(
                n > 0 ? vals[0] : 0f,
                n > 1 ? vals[1] : 0f,
                n > 2 ? vals[2] : 0f,
                n > 3 ? vals[3] : 0f);
            count = n;
            return true;
        }
    }
}
