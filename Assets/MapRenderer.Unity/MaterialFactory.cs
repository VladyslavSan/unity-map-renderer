using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using Line = MapRenderer.Core.Style.Line;
using Fill = MapRenderer.Core.Style.Fill;
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
    ///
    /// <para>S60: all bindings now take <see cref="StyleProperty{T}"/> (replaces <c>PaintPropertyEvaluator</c>).
    /// Data-driven guard: was <c>!= null</c>; now <c>!prop.DependsOnFeature</c>. Translate: was
    /// TranslateX/Y pair; now <c>Translate.Evaluate(0.0)</c> returning <c>double2</c>.</para>
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
        /// S60: StyleProperty&lt;T&gt; replaces old PaintPropertyEvaluator pairs; null-guard →
        /// <c>!prop.DependsOnFeature</c>; TranslateX/Y → <c>Translate.Evaluate(0.0)</c>.
        /// </summary>
        public static void BindFillPaintToApplier(Fill.PaintProperties paint, ZoomStyleApplier applier, Material mat)
        {
            // fill-opacity: bind for Constant/Zoom (data-driven → vertex bake, not supported for fill yet).
            if (!paint.Opacity.DependsOnFeature)
                applier.BindFloat(paint.Opacity, "_Opacity");

            // fill-outline-color: bind only when explicitly set (not a fallback) and non-data-driven.
            if (!paint.OutlineColorIsFallback && !paint.OutlineColor.DependsOnFeature)
                applier.BindColor(paint.OutlineColor, "_FillOutlineColor");

            // fill-antialias.
            if (!paint.Antialias.DependsOnFeature)
                applier.BindFloat(paint.Antialias, "_FillAntialias");

            // fill-translate: collapsed to double2 — extract x/y and set as Vector4.
            var t = paint.Translate.Evaluate(0.0);
            mat.SetVector("_FillTranslate", new Vector4((float)t.x, (float)t.y, 0f, 0f));

            // fill-translate-anchor.
            if (!paint.TranslateAnchor.DependsOnFeature)
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
        ///
        /// S60: StyleProperty&lt;T&gt; replaces PaintPropertyEvaluator pairs; data-driven gate →
        /// <c>.DependsOnFeature</c>; Translate collapsed to <c>double2</c>.
        /// </summary>
        public static void BindLinePaintToApplier(Line.PaintProperties paint, ZoomStyleApplier applier, Material mat)
        {
            // line-color: bind only for non-data-driven (Constant/Zoom). Data-driven → vertex bake.
            // The color rides the standard _BaseColor (S58 collapsed the redundant _MapColor into it).
            if (!paint.Color.DependsOnFeature)
                applier.BindColor(paint.Color, ShaderProperties.BaseColor);

            // line-opacity.
            if (!paint.Opacity.DependsOnFeature)
                applier.BindFloat(paint.Opacity, "_Opacity");

            // line-width (in pixels per MapLibre spec).
            // Convention (data-driven width): when Width depends on feature, the evaluated width
            // is baked into WidthScale (stream 3) by StyledLineTileBuilder. Set _Width = 1.0 so
            // the shader formula (_Width × WidthScale) yields the full baked width directly.
            // For Constant/Zoom kind, bind normally as a uniform.
            if (paint.Width.DependsOnFeature)
                mat.SetFloat("_Width", 1f); // base = 1; evaluated width baked into WidthScale per feature
            else
                applier.BindFloat(paint.Width, "_Width");
            // Ensure WidthIsPixels=1 so the shader interprets width as pixels.
            mat.SetFloat("_WidthIsPixels", 1f);

            // line-blur.
            if (!paint.Blur.DependsOnFeature)
                applier.BindFloat(paint.Blur, "_Blur");

            // line-gap-width.
            if (!paint.GapWidth.DependsOnFeature)
                applier.BindFloat(paint.GapWidth, "_GapWidth");

            // line-offset (S44).
            if (!paint.Offset.DependsOnFeature)
                applier.BindFloat(paint.Offset, "_LineOffset");

            // line-translate: collapsed to double2 — extract x/y and set as Vector4.
            // Unity boundary cast: double2 → (float)x/(float)y then pack into Vector4.
            var t = paint.Translate.Evaluate(0.0);
            mat.SetVector("_LineTranslate", new Vector4((float)t.x, (float)t.y, 0f, 0f));

            // line-translate-anchor.
            if (!paint.TranslateAnchor.DependsOnFeature)
                applier.BindFloat(paint.TranslateAnchor, "_LineTranslateAnchor");

            // line-pattern hook: set flag; solid fallback until S17.
            mat.SetFloat("_LinePattern", paint.PatternName != null ? 1f : 0f);

            // S43: line-dasharray initial bind (constant or first zoom-step evaluation at zoom=0).
            ApplyLineDashArray(paint, mat, 0.0);
        }

        /// <summary>
        /// Evaluates the line-dasharray expression at <paramref name="zoom"/> and sets
        /// <c>_DashArray</c>/<c>_DashCount</c> on the material. Called at bind time and — only for a
        /// zoom-dependent dasharray (see <see cref="StyledLayerSet"/>) — per frame. When absent or
        /// degenerate, sets _DashCount=0 (solid identity — no change to rendering path).
        ///
        /// S60: uses the new alloc-free <see cref="Line.LineDash.TryEvaluatePattern"/> that returns
        /// <c>float4 + int count</c> instead of <c>float[]</c>.
        /// </summary>
        public static void ApplyLineDashArray(Line.PaintProperties paint, Material mat, double zoom)
        {
            if (Line.LineDash.TryEvaluatePattern(paint.DashArray, zoom, out var packed, out int count))
            {
                // Unity boundary cast: float4 → Vector4 (at the SetVector call site, not upstream).
                mat.SetVector("_DashArray", new Vector4(packed.x, packed.y, packed.z, packed.w));
                mat.SetFloat("_DashCount",  count);
            }
            else
            {
                // No / unsupported / degenerate dasharray: solid identity.
                mat.SetVector("_DashArray", Vector4.zero);
                mat.SetFloat("_DashCount",  0f);
            }
        }
    }
}
