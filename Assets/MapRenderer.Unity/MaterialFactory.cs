using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering;

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
        /// <summary>
        /// Creates a per-style-layer fill Material by cloning <paramref name="settings"/>'s base fill
        /// material (a Material Variant in the Editor — see <see cref="MaterialExtensions.CloneWithParent"/>),
        /// so the base asset is the single editable source of styling. The clone inherits the base's
        /// import-baked keywords; <see cref="FillMaterialTweaker.ApplyPainterContract"/> then re-asserts the
        /// code-owned render-state + color-identity contract.
        ///
        /// <para>Returns <c>null</c> (with a warning) when no config / base material is assigned — there is
        /// no shader-name fallback (S58 retired <c>Shader.Find</c>); production wires a <see cref="MapMaterialSet"/>.</para>
        /// </summary>
        public static Material CreateFillMaterial(MapMaterialSet settings)
        {
            Material baseMat = settings != null ? settings.FillMaterial : null;
            if (baseMat == null)
            {
                Debug.LogWarning("[MaterialFactory] No fill base material configured (MapMaterialSet.FillMaterial " +
                                 "is null) — fill layers will not render. Assign a Material Set on the map component.");
                return null;
            }

            var mat = baseMat.CloneWithParent();
            mat.name = "MapView_Fill";
            FillMaterialTweaker.ApplyPainterContract(mat);
            return mat;
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
        /// Creates a per-style-layer line Material by cloning <paramref name="settings"/>'s base line
        /// material (a Material Variant in the Editor — see <see cref="MaterialExtensions.CloneWithParent"/>),
        /// so the base asset is the single editable source of styling. The clone inherits the base's
        /// import-baked keywords; <see cref="LineMaterialTweaker.ApplyPainterContract"/> then re-asserts the
        /// code-owned render-state + color-identity contract.
        ///
        /// <para>Returns <c>null</c> (with a warning) when no config / base material is assigned — there is
        /// no shader-name fallback (S58 retired <c>Shader.Find</c>); production wires a <see cref="MapMaterialSet"/>.</para>
        /// </summary>
        public static Material CreateLineMaterial(MapMaterialSet settings)
        {
            Material baseMat = settings != null ? settings.LineMaterial : null;
            if (baseMat == null)
            {
                Debug.LogWarning("[MaterialFactory] No line base material configured (MapMaterialSet.LineMaterial " +
                                 "is null) — line layers will not render. Assign a Material Set on the map component.");
                return null;
            }

            var mat = baseMat.CloneWithParent();
            mat.name = "MapView_Line";
            LineMaterialTweaker.ApplyPainterContract(mat);
            return mat;
        }

        /// <summary>
        /// Binds constant/zoom line paint properties from <paramref name="paint"/> to the material.
        /// Data-driven properties (Feature/Composite color) are handled by StyledLineTileBuilder bake;
        /// only Constant/Zoom-kind properties are bound here as uniforms.
        /// </summary>
        public static void BindLinePaintToApplier(LinePaint paint, ZoomStyleApplier applier, Material mat)
        {
            // line-color: bind only for non-data-driven (Constant/Zoom). Data-driven → vertex bake.
            // The color rides the standard _BaseColor (S58 collapsed the redundant _MapColor into it).
            if (paint.Color != null)
                applier.BindColor(paint.Color, ShaderProperties.BaseColor);

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
            // S14_LINE_PATTERN_HOOK: _LinePattern=1 signals a pattern layer; renders solid _BaseColor fallback.
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
