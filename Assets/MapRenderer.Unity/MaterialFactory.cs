using UnityEngine;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Builds the per-style-layer base <see cref="Material"/> instances for fills and lines and binds their
    /// constant/zoom paint properties to a <see cref="ZoomStyleApplier"/>. Pure and engine-only — no tile,
    /// scheduler, or floating-origin state.
    ///
    /// <para>Step 1 of the MapView decomposition (extracted verbatim from MapView's private statics). In the
    /// eventual ECS shape this is the "bake style layer → GPU material" system; isolating it here makes it
    /// unit-testable and keeps MapView focused on the tile loop.</para>
    /// </summary>
    internal static class MaterialFactory
    {
        /// <summary>Creates a base fill Material for a style layer.</summary>
        public static Material CreateFillMaterial()
        {
            var shader = Shader.Find("MapRenderer/Fill");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "MapView_Fill" };
                mat.SetColor("_MapColor",   Color.white);
                mat.SetColor("_BaseColor",  Color.white);
                mat.SetFloat("_Opacity",    1f);
                mat.SetFloat("_Metallic",   0f);
                mat.SetFloat("_Smoothness", 0f);
                mat.SetFloat("_ZWrite",     0f);
                return mat;
            }
            Debug.LogWarning("[MaterialFactory] MapRenderer/Fill shader not found — using Sprites/Default fallback.");
            return new Material(Shader.Find("Sprites/Default")) { name = "MapView_Fallback" };
        }

        /// <summary>
        /// Binds constant/zoom paint properties from <paramref name="paint"/> to the material.
        /// </summary>
        public static void BindFillPaintToApplier(FillPaint paint, ZoomStyleApplier applier, Material mat)
        {
            if (paint.Opacity != null)
                applier.BindFloat(paint.Opacity, "_Opacity");

            if (paint.OutlineColor != null && !paint.OutlineColorIsFallback)
                applier.BindColor(paint.OutlineColor, "_FillOutlineColor");

            if (paint.Antialias != null)
                applier.BindFloat(paint.Antialias, "_FillAntialias");

            float tx = (float)paint.TranslateX.EvaluateNumber(0.0);
            float ty = (float)paint.TranslateY.EvaluateNumber(0.0);
            mat.SetVector("_FillTranslate", new Vector4(tx, ty, 0f, 0f));

            if (paint.TranslateAnchor != null)
                applier.BindFloat(paint.TranslateAnchor, "_FillTranslateAnchor");
        }

        /// <summary>
        /// Creates a base line Material for a style layer using the MapRenderer/Line shader.
        /// </summary>
        public static Material CreateLineMaterial()
        {
            var shader = Shader.Find("MapRenderer/Line");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "MapView_Line" };
                mat.SetColor("_MapColor",         Color.white);
                mat.SetColor("_BaseColor",         Color.white);
                mat.SetFloat("_Opacity",           1f);
                mat.SetFloat("_Width",             2f);
                mat.SetFloat("_WidthIsPixels",     1f); // line-width is in pixels per spec
                mat.SetFloat("_MetersPerPixel",    1f);
                mat.SetFloat("_Blur",              1f);
                mat.SetFloat("_GapWidth",          0f);
                mat.SetVector("_LineTranslate",    Vector4.zero);
                mat.SetFloat("_LineTranslateAnchor", 0f);
                mat.SetFloat("_LinePattern",       0f);
                // S43: line-dasharray defaults — solid identity (_DashCount=0).
                mat.SetVector("_DashArray",        Vector4.zero);
                mat.SetFloat("_DashCount",         0f);
                // S44: line-offset default — no perpendicular shift.
                mat.SetFloat("_LineOffset",        0f);
                mat.SetFloat("_Metallic",          0f);
                mat.SetFloat("_Smoothness",        0f);
                mat.SetFloat("_ZWrite",            0f);
                return mat;
            }
            Debug.LogWarning("[MaterialFactory] MapRenderer/Line shader not found — using Sprites/Default fallback.");
            return new Material(Shader.Find("Sprites/Default")) { name = "MapView_LineFallback" };
        }

        /// <summary>
        /// Binds constant/zoom line paint properties from <paramref name="paint"/> to the material.
        /// Data-driven properties (Feature/Composite color) are handled by StyledLineTileBuilder bake;
        /// only Constant/Zoom-kind properties are bound here as uniforms.
        /// </summary>
        public static void BindLinePaintToApplier(LinePaint paint, ZoomStyleApplier applier, Material mat)
        {
            // line-color: bind only for non-data-driven (Constant/Zoom). Data-driven → vertex bake.
            if (paint.Color != null)
                applier.BindColor(paint.Color, "_MapColor");

            // line-opacity.
            if (paint.Opacity != null)
                applier.BindFloat(paint.Opacity, "_Opacity");

            // line-width (in pixels per MapLibre spec).
            // Convention (data-driven width): when WidthKind depends on feature, the evaluated width
            // is baked into WidthScale (stream 3) by StyledLineTileBuilder. Set _Width = 1.0 so
            // the shader formula (_Width × WidthScale) yields the full baked width directly.
            // For Constant/Zoom kind, Width is non-null → bind normally as a uniform.
            if (MapRenderer.Core.Expressions.ExpressionKinds.DependsOnFeature(paint.WidthKind))
                mat.SetFloat("_Width", 1f); // base = 1; evaluated width baked into WidthScale per feature
            else if (paint.Width != null)
                applier.BindFloat(paint.Width, "_Width");
            // Ensure WidthIsPixels=1 so the shader interprets width as pixels.
            mat.SetFloat("_WidthIsPixels", 1f);

            // line-blur.
            if (paint.Blur != null)
                applier.BindFloat(paint.Blur, "_Blur");

            // line-gap-width.
            if (paint.GapWidth != null)
                applier.BindFloat(paint.GapWidth, "_GapWidth");

            // line-offset (S44).
            if (paint.Offset != null)
                applier.BindFloat(paint.Offset, "_LineOffset");

            // line-translate: constant components baked into material vector.
            float tx = (float)paint.TranslateX.EvaluateNumber(0.0);
            float ty = (float)paint.TranslateY.EvaluateNumber(0.0);
            mat.SetVector("_LineTranslate", new Vector4(tx, ty, 0f, 0f));

            // line-translate-anchor.
            if (paint.TranslateAnchor != null)
                applier.BindFloat(paint.TranslateAnchor, "_LineTranslateAnchor");

            // line-pattern hook: set flag; solid fallback until S17.
            // S14_LINE_PATTERN_HOOK: _LinePattern=1 signals a pattern layer; renders solid _MapColor fallback.
            mat.SetFloat("_LinePattern", paint.PatternName != null ? 1f : 0f);

            // S43: line-dasharray initial bind (constant or first zoom-step evaluation at zoom=0).
            // Per-frame re-evaluation for zoom-step patterns is done by ApplyLineDashArray in ApplyZoom.
            // Feature-dependent dasharray is out of scope; constant + zoom-step are the supported forms.
            // S43_DEFER_LIVE_ZOOM_STEP: zoom-step dasharray re-evaluates per-frame via ApplyLineDashArray;
            // static bind here is for constant arrays only (zoom=0 is a safe initial value).
            ApplyLineDashArray(paint, mat, 0.0);
        }

        /// <summary>
        /// S43: Evaluates the line-dasharray expression at <paramref name="zoom"/> and sets
        /// <c>_DashArray</c>/<c>_DashCount</c> on the material. Called both at bind time and
        /// per-frame (for zoom-step patterns). When absent or degenerate, sets _DashCount=0
        /// (solid identity — no change to rendering path).
        ///
        /// Not routed through ZoomStyleApplier (scalar/color only). Array evaluation uses
        /// <see cref="LineDash.TryEvaluateDashArray"/> directly.
        /// </summary>
        public static void ApplyLineDashArray(LinePaint paint, Material mat, double zoom)
        {
            if (!paint.HasDashArray)
            {
                // No dasharray: ensure solid identity (guard against stale values).
                mat.SetVector("_DashArray", Vector4.zero);
                mat.SetFloat("_DashCount",  0f);
                return;
            }

            if (LineDash.TryEvaluateDashArray(paint.DashArrayJson, zoom, out float[] pattern))
            {
                var (x, y, z, w, count) = LineDash.Pack(pattern);
                mat.SetVector("_DashArray", new Vector4(x, y, z, w));
                mat.SetFloat("_DashCount",  count);
            }
            else
            {
                // Parse failed: solid fallback.
                mat.SetVector("_DashArray", Vector4.zero);
                mat.SetFloat("_DashCount",  0f);
            }
        }
    }
}
