using System.Collections.Generic;
using MapRenderer.Core.Json;
using MapRenderer.Core.Expressions;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// S43: line-dasharray — engine-free helpers for the in-shader modulo dash mechanism.
    ///
    /// Design decision D1: in-shader modulo over cumulative dash-pattern length. Cheap and exact
    /// for simple patterns; no LUT/SDF texture needed. AA is handled in the fragment shader via
    /// fwidth feather on the on/off transition edges. Round dash-caps deferred to a follow-up.
    ///
    /// Design decision D2 (restated at S110): dash lengths are line-width units
    /// (distanceAlong / metersPerDashUnit), so dashes scale automatically with line-width and remain
    /// zoom-stable. The unit is FIXED FOR THE FRAME — it is the styled width measured with the frame's
    /// ground resolution, not with each vertex's own screen measurement — so the pattern is anchored to the
    /// ground and foreshortens with the road instead of sliding along it as the camera moves. The CPU
    /// mirror in this file returns the same ratio formula that the HLSL fragment uses.
    ///
    /// This file is the single source of truth for the dash function. The HLSL mirror lives in
    /// Assets/Code/MapRenderer.Unity/Shaders/Map/Line/Line_VertexExtrude.hlsl (search "S43 dash" to find
    /// the corresponding fragment code).
    ///
    /// Engine-free: no UnityEngine references. Runs in both dotnet core-tests and Unity EditMode.
    /// Clean-room: dash semantics from the public MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class LineDash
    {
        // Maximum dasharray entries we accept (covers dash, gap, dash-dot, dash-dot-dot).
        // Entries beyond this cap are silently truncated; document in callers.
        public const int MaxEntries = 4;

        // ── Dash coverage (CPU mirror of the HLSL fragment function) ─────────────────────────
        //
        // Returns 1.0 when the fragment is on a "dash-on" region, 0.0 on a "dash-off" gap.
        // This is the hard (non-AA) binary step; the GPU fragment adds fwidth feather around
        // the same transition edges (see Line_VertexExtrude.hlsl, "S43 dash: fwidth feather").
        //
        // Parameters:
        //   distanceAlong    — cumulative arc length along the line in world meters.
        //   metersPerDashUnit — metres of road per dash unit: the styled line width in world metres,
        //                   measured with the FRAME-CONSTANT ruler (MetersPerPixel(zoom)/dpr × the styled
        //                   device-px width). Deliberately NOT "the line's width in world metres at this
        //                   point on screen" — since S110 that is a different, per-vertex quantity, and
        //                   using it here is what made dashes crawl, skew and depend on tessellation.
        //   pattern       — on/off alternating lengths in line-width units (same units as dashU).
        //                   pattern[0] = first on-length, pattern[1] = first off-length, ...
        //                   Odd-length arrays → treat as solid (documented below).
        //
        // Returns 1.0 for solid identity when:
        //   • pattern is null or empty           → no dasharray set, render solid.
        //   • metersPerDashUnit ≤ 0              → degenerate, render solid.
        //   • pattern has odd length / length==1 → ambiguous spec; render solid.
        //   • all pattern entries are <= 0       → degenerate, render solid.
        //
        // Note on [1]: a single-entry array [1] has odd length → solid identity.
        // This matches the stage acceptance tooth 1 which explicitly lists [1] as the solid control.
        // A single entry cannot define both an on and an off span, so solid is the correct fallback.
        //
        // Mirror note: this function must produce the same on/off result as the HLSL fragment
        // (Assets/Code/MapRenderer.Unity/Shaders/Map/Line/Line_VertexExtrude.hlsl). Keep both in sync on
        // any arithmetic change.
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
        //
        // line-dasharray goes through the expression system like every other paint property: parsed
        // once into an Expression (whose Kind — Constant / Zoom — is computed at parse time), then
        // evaluated to a float[] pattern at a given zoom. There is no bespoke evaluator: a constant
        // dash is a LiteralExpression that evaluates trivially, and StyledLayerSet skips the per-frame
        // re-eval unless the expression DependsOnZoom (so constants are evaluated once at bind).
        //
        // line-dasharray is data-constant in the spec: a feature-dependent value is invalid and
        // degrades to solid naturally (it errors against a null feature → TryEvaluatePattern false).

        /// <summary>
        /// Parse a <c>line-dasharray</c> property value into an expression. A dasharray value may write
        /// its array literals bare (e.g. <c>[2,1]</c>, or the per-stop outputs of
        /// <c>["step",["zoom"],[1,1],10,[2,1]]</c>), but the strict expression parser rejects an array
        /// whose first element is not an operator string. So bare arrays are wrapped as
        /// <c>["literal", …]</c> — at the top level and in an operator's direct arguments (which covers
        /// step / interpolate stop outputs). Returns null for a null or malformed value (→ render
        /// solid; never throws).
        /// </summary>
        public static Expression ParseDashArray(JsonValue json)
        {
            JsonValue toParse = WrapBareArrayLiterals(json);
            if (toParse == null) return null;
            try { return ExpressionParser.Parse(toParse); }
            catch (ExpressionParseException) { return null; }
        }

        // Wrap a bare array (first element not an operator string) as ["literal", array]. For an
        // operator call, do the same to its direct arguments (one level — enough for step/interpolate
        // dash outputs), except "literal" whose argument is a raw value, not an expression.
        private static JsonValue WrapBareArrayLiterals(JsonValue json)
        {
            if (json == null || !json.IsArray || json.Items.Count == 0)
                return json;

            if (json.Items[0].Kind != JsonKind.String)
                return JsonValue.OfArray(new List<JsonValue>(2) { JsonValue.OfString("literal"), json });

            if (json.Items[0].AsString(null) == "literal")
                return json;

            var outItems = new List<JsonValue>(json.Items.Count) { json.Items[0] };
            for (int i = 1; i < json.Items.Count; i++)
            {
                JsonValue a = json.Items[i];
                outItems.Add(a.IsArray && a.Items.Count > 0 && a.Items[0].Kind != JsonKind.String
                    ? JsonValue.OfArray(new List<JsonValue>(2) { JsonValue.OfString("literal"), a })
                    : a);
            }
            return JsonValue.OfArray(outItems);
        }

        /// <summary>
        /// Evaluate a parsed dasharray expression at <paramref name="zoom"/> into an alloc-free packed
        /// pattern (capped at <see cref="MaxEntries"/> = 4 entries). Returns false (→ render solid) when
        /// the expression is null, errors (e.g. a data-driven value against a null feature), or yields a
        /// non-array / empty / non-numeric result.
        ///
        /// Solid identity is signalled by <paramref name="count"/> == 0 (odd-length arrays, degenerate, or
        /// missing pattern). Caller: <c>mat.SetVector("_DashArray",(Vector4)packed); mat.SetFloat("_DashCount",count);</c>
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
