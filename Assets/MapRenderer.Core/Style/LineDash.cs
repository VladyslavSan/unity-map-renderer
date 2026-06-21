using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// S43: line-dasharray — engine-free helpers for the in-shader modulo dash mechanism.
    ///
    /// Design decision D1: in-shader modulo over cumulative dash-pattern length. Cheap and exact
    /// for simple patterns; no LUT/SDF texture needed. AA is handled in the fragment shader via
    /// fwidth feather on the on/off transition edges. Round dash-caps deferred to a follow-up.
    ///
    /// Design decision D2: dash lengths are line-width units (distanceAlong / widthM), so dashes
    /// scale automatically with line-width and remain zoom-stable. The CPU mirror in this file
    /// returns the same ratio formula that the HLSL fragment uses.
    ///
    /// This file is the single source of truth for the dash function. The HLSL mirror lives in
    /// MapLineForwardPass.hlsl (search "S43 dash" to find the corresponding fragment code).
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
        // the same transition edges (see MapLineForwardPass.hlsl, "S43 dash: fwidth feather").
        //
        // Parameters:
        //   distanceAlong — cumulative arc length along the line in world meters.
        //   widthM        — line width in world meters (same px→m conversion used for extrusion).
        //   pattern       — on/off alternating lengths in line-width units (same units as dashU).
        //                   pattern[0] = first on-length, pattern[1] = first off-length, ...
        //                   Odd-length arrays → treat as solid (documented below).
        //
        // Returns 1.0 for solid identity when:
        //   • pattern is null or empty           → no dasharray set, render solid.
        //   • widthM ≤ 0                         → degenerate, render solid.
        //   • pattern has odd length / length==1 → ambiguous spec; render solid.
        //   • all pattern entries are <= 0       → degenerate, render solid.
        //
        // Note on [1]: a single-entry array [1] has odd length → solid identity.
        // This matches the stage acceptance tooth 1 which explicitly lists [1] as the solid control.
        // A single entry cannot define both an on and an off span, so solid is the correct fallback.
        //
        // Mirror note: this function must produce the same on/off result as the HLSL fragment
        // (MapLineForwardPass.hlsl). Keep both in sync on any arithmetic change.
        public static float DashCoverage(double distanceAlong, double widthM, float[] pattern)
        {
            if (pattern == null || pattern.Length == 0)
                return 1.0f;   // solid identity: no dasharray

            if (widthM <= 0.0)
                return 1.0f;   // degenerate width → solid

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
            // Mirror of: float dashU = distanceAlong / widthM; in the vertex shader.
            double u = distanceAlong / widthM;

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

        // ── Dasharray parse / evaluate ────────────────────────────────────────────────────────
        //
        // Supports the two spec-allowed expression forms:
        //   1. Constant array: ["literal", [2, 1]]  or bare JSON array [2, 1]
        //   2. Zoom step:      ["step", ["zoom"], <base>, z0, [d0,g0], z1, [d1,g1], ...]
        //
        // At most MaxEntries values are retained; extras are silently truncated.
        // Returns false when the JSON value is null, not an array/step, or yields an empty array.
        //
        // Zoom-step re-evaluation: for a zoom-step dasharray, call TryEvaluateDashArray again
        // per frame with the current zoom. For S43 this is sufficient; fully data-driven
        // (feature-dependent) dasharrays are out of scope.
        public static bool TryEvaluateDashArray(JsonValue json, double zoom, out float[] pattern)
        {
            pattern = null;

            if (json == null)
                return false;

            if (!json.IsArray || json.Items.Count == 0)
                return false;

            // Case 1: expression array — first element is a string op.
            if (json.Items[0].Kind == MapRenderer.Core.Json.JsonKind.String)
            {
                string op = json.Items[0].AsString(null);

                if (op == "literal" && json.Items.Count >= 2)
                {
                    // ["literal", [2, 1]]
                    return TryParseArrayLiteral(json.Items[1], out pattern);
                }

                if (op == "step" && json.Items.Count >= 3)
                {
                    // ["step", ["zoom"], <default-array>, z0, <arr0>, z1, <arr1>, ...]
                    // Items: 0=step, 1=input(zoom), 2=default, 3=z0, 4=arr0, ...
                    // Pairs after default are (threshold, output) starting at index 3.
                    // Evaluate: find last threshold <= zoom; if none, use default (index 2).
                    JsonValue selected = json.Items[2]; // default
                    int i = 3;
                    while (i + 1 < json.Items.Count)
                    {
                        double threshold = json.Items[i].AsDouble(double.MaxValue);
                        if (zoom >= threshold)
                            selected = json.Items[i + 1];
                        else
                            break;
                        i += 2;
                    }
                    return TryParseArrayLiteral(selected, out pattern);
                }

                // Unknown string op (e.g. future expression type) — no support.
                return false;
            }

            // Case 2: bare JSON array of numbers (constant) e.g. [2, 1].
            return TryParseArrayLiteral(json, out pattern);
        }

        // ── Pack / unpack for shader upload ──────────────────────────────────────────────────
        //
        // Packs up to MaxEntries=4 pattern values into a float4 vector for the shader's
        // _DashArray property, and returns the count for _DashCount.
        // Caller: mat.SetVector("_DashArray", packed); mat.SetFloat("_DashCount", count);
        public static (float x, float y, float z, float w, float count) Pack(float[] pattern)
        {
            if (pattern == null || pattern.Length == 0)
                return (0f, 0f, 0f, 0f, 0f);

            int n = pattern.Length > MaxEntries ? MaxEntries : pattern.Length;
            // Odd-length → solid identity (count = 0 sentinel).
            if (n % 2 != 0)
                return (0f, 0f, 0f, 0f, 0f);

            float x = n > 0 ? pattern[0] : 0f;
            float y = n > 1 ? pattern[1] : 0f;
            float z = n > 2 ? pattern[2] : 0f;
            float w = n > 3 ? pattern[3] : 0f;
            return (x, y, z, w, (float)n);
        }

        // ── Private helpers ───────────────────────────────────────────────────────────────────

        private static bool TryParseArrayLiteral(JsonValue json, out float[] pattern)
        {
            pattern = null;
            if (json == null || !json.IsArray || json.Items.Count == 0)
                return false;

            int count = json.Items.Count > MaxEntries ? MaxEntries : json.Items.Count;
            var arr = new float[count];
            for (int i = 0; i < count; i++)
                arr[i] = (float)json.Items[i].AsDouble(0.0);

            pattern = arr;
            return true;
        }
    }
}
