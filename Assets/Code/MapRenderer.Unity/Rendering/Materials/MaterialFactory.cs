using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style;
using Line = MapRenderer.Core.Style.Line;
using Fill = MapRenderer.Core.Style.Fill;
using Background = MapRenderer.Core.Style.Background;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Builds the per-style-layer base <see cref="Material"/> instances for fills, lines, and background
    /// and binds their constant/zoom paint properties to a <see cref="ZoomStyleApplier"/>. Pure and
    /// engine-only — no tile, scheduler, or floating-origin state.
    /// </summary>
    internal static class MaterialFactory
    {
        /// <summary>
        /// Creates a per-style-layer fill Material by cloning <paramref name="settings"/>'s base fill
        /// material (a Material Variant in the Editor), so the base asset is the single editable source of
        /// styling. The clone inherits the base's import-baked keywords; <c>FillTweaker.ApplyPainterContract</c>
        /// then re-asserts the code-owned render-state + color-identity contract. Returns <c>null</c> (with a
        /// warning) when no base material is assigned; there is no shader-name fallback.
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
            FillTweaker.ApplyPainterContract(mat);
            return mat;
        }

        /// <summary>
        /// Binds constant/zoom paint properties from <paramref name="paint"/> to the material.
        /// </summary>
        public static void BindFillPaintToApplier(Fill.PaintProperties paint, Style.ZoomStyleApplier applier, Material mat)
        {
            // fill-color: Constant/Zoom rides _BaseColor, and StyledFillTileBuilder leaves the vertex white; data-driven
            // bakes into the COLOR stream. The fragment multiplies uniform × vertex, so writing both squares the colour.
            if (!paint.Color.DependsOnFeature)
                applier.BindColor(paint.Color, ShaderProperties.PropertyId.BaseColor);

            // fill-opacity: Constant/Zoom rides the _Opacity uniform; data-driven bakes into the COLOR
            // stream's alpha. Non-obvious why: _Opacity MUST pin to 1 (fragment computes
            // alpha *= vColor.a * _Opacity) or the opacity multiplies in twice, and the pin binds the
            // constant 1 rather than a direct SetFloat, so the layer-fade gate stays _Opacity's only writer.
            if (paint.Opacity.DependsOnFeature)
                applier.BindOpacity(new StyleProperty<float>(1f), ShaderProperties.PropertyId.Opacity);
            else
                applier.BindOpacity(paint.Opacity, ShaderProperties.PropertyId.Opacity);

            // fill-outline-color: bind only when explicitly set (not a fallback) and non-data-driven.
            if (!paint.OutlineColorIsFallback && !paint.OutlineColor.DependsOnFeature)
                applier.BindColor(paint.OutlineColor, ShaderProperties.Fill.PropertyId.FillOutlineColor);

            // fill-antialias is NOT bound: no pass reads _FillAntialias (docs/fill-parity-design.md). The
            // property stays declared in the CBUFFER, which MapFillUnlitMaterialTests pins.

            // fill-translate: a px offset through Fill_VertexModify's MapPixelsToWorld, in line-translate's
            // device-px space. It parses as always-Constant, so the per-frame applier cannot animate it.
            applier.BindDevicePixelVector(paint.Translate, ShaderProperties.Fill.PropertyId.FillTranslate);

            // fill-translate-anchor.
            if (!paint.TranslateAnchor.DependsOnFeature)
                applier.BindDiscreteFloat(paint.TranslateAnchor, ShaderProperties.Fill.PropertyId.FillTranslateAnchor);

            // fill-pattern: flag the layer, and start it UNRESOLVED (zero-area rect ⇒ the shader clips).
            // Non-obvious why: the sprite sheet is fetched asynchronously and cannot exist yet at
            // material-build time; FillRenderLayer.SetSprites resolves it once the sheet lands, and a
            // pattern layer must NOT fall back to fill-color, whose spec default is opaque black.
            mat.SetFloat(ShaderProperties.Fill.PropertyId.FillPattern, paint.PatternName != null ? 1f : 0f);
            mat.SetVector(ShaderProperties.Fill.PropertyId.PatternRect, Vector4.zero);
        }

        /// <summary>
        /// Creates a per-style-layer fill-extrusion Material by cloning <paramref name="settings"/>'s
        /// <see cref="MapMaterialSet.FillExtrusionMaterial"/> base, then applies the opaque, depth-writing
        /// <c>FillExtrusionTweaker.ApplyElevatedContract</c>, because buildings must occlude one another and
        /// their own walls. Returns <c>null</c> (with a warning) when no base material is assigned.
        /// </summary>
        public static Material CreateFillExtrusionMaterial(MapMaterialSet settings)
        {
            Material baseMat = settings != null ? settings.FillExtrusionMaterial : null;
            if (baseMat == null)
            {
                Debug.LogWarning("[MaterialFactory] No fill-extrusion base material configured " +
                                 "(MapMaterialSet.FillExtrusionMaterial is null) — fill-extrusion layers will " +
                                 "not render. Assign a Material Set on the map component.");
                return null;
            }

            var mat = baseMat.CloneWithParent();
            mat.name = "MapView_FillExtrusion";
            FillExtrusionTweaker.ApplyElevatedContract(mat);
            return mat;
        }

        /// <summary>
        /// Binds constant/zoom fill-extrusion paint properties from <paramref name="paint"/> to the material.
        /// Non-local invariant: a data-driven height/base uniform is zeroed, because
        /// <see cref="Meshing.StyledFillExtrusionTileBuilder"/> bakes that value per vertex and the VS adds
        /// uniform and bake, so a stray non-zero uniform doubles the elevation. <c>fill-extrusion-color</c>'s
        /// alpha is not special-cased to 1, matching <see cref="BindFillPaintToApplier"/>.
        /// </summary>
        /// <param name="paint">The layer's parsed fill-extrusion paint properties.</param>
        /// <param name="applier">The layer's per-frame zoom→uniform applier; constant bindings apply
        /// immediately, Zoom-kind bindings queue for the next <see cref="Style.ZoomStyleApplier.ApplyZoom"/>.</param>
        /// <param name="mat">The layer's cloned material instance (from <see cref="CreateFillExtrusionMaterial"/>)
        /// to bind onto and defensively reset.</param>
        public static void BindFillExtrusionPaintToApplier(FillExtrusion.PaintProperties paint, Style.ZoomStyleApplier applier, Material mat)
        {
            if (!paint.Color.DependsOnFeature)
                applier.BindColor(paint.Color, ShaderProperties.PropertyId.BaseColor);

            // The else arm is unreachable by any valid style — fill-extrusion-opacity is not data-driven-able
            // — but binding it keeps _Opacity single-writer so a malformed style cannot escape the gate.
            if (!paint.Opacity.DependsOnFeature)
                applier.BindOpacity(paint.Opacity, ShaderProperties.PropertyId.Opacity);
            else
                applier.BindOpacity(new StyleProperty<float>(1f), ShaderProperties.PropertyId.Opacity);

            // fill-extrusion-height / fill-extrusion-base: Constant/Zoom rides the uniform; data-driven bakes
            // per-vertex instead, pinning the uniform to 0 so the VS's additive lerp does not double-count.
            if (!paint.Height.DependsOnFeature)
                applier.BindFloat(paint.Height, ShaderProperties.FillExtrusion.PropertyId.ExtrusionHeight);
            else
                mat.SetFloat(ShaderProperties.FillExtrusion.PropertyId.ExtrusionHeight, 0f);

            if (!paint.Base.DependsOnFeature)
                applier.BindFloat(paint.Base, ShaderProperties.FillExtrusion.PropertyId.ExtrusionBase);
            else
                mat.SetFloat(ShaderProperties.FillExtrusion.PropertyId.ExtrusionBase, 0f);

            // fill-extrusion-translate: a px offset consumed through FillExtrusion_VertexModify's
            // MapPixelsToWorld — same device-px space fill-translate/line-translate live in.
            applier.BindDevicePixelVector(paint.Translate, ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslate);
            if (!paint.TranslateAnchor.DependsOnFeature)
                applier.BindDiscreteFloat(paint.TranslateAnchor, ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslateAnchor);

            // No Fill-pattern defensive reset here (unlike BindBackgroundPaintToApplier): Map/FillExtrusion
            // has its own dedicated CBUFFER, with no _FillPattern/_FillTranslate to inherit from a Fill Variant.
        }

        /// <summary>Background material: a clone of the FILL base (the fill shader's flat lit path IS the
        /// ground look; no separate base asset). Null (warn) when unconfigured, mirroring CreateFillMaterial.</summary>
        public static Material CreateBackgroundMaterial(MapMaterialSet settings)
        {
            Material baseMat = settings != null ? settings.FillMaterial : null;
            if (baseMat == null)
            {
                Debug.LogWarning("[MaterialFactory] No fill base material configured (MapMaterialSet.FillMaterial " +
                                 "is null) — the style's background layer will not render.");
                return null;
            }

            var mat = baseMat.CloneWithParent();
            mat.name = "MapView_Background";
            FillTweaker.ApplyPainterContract(mat);
            return mat;
        }

        /// <summary>
        /// Binds constant/zoom background paint properties from <paramref name="paint"/> to the material —
        /// a UNIFORM colour over WHITE vertex colours (the line-color pattern), not the fill per-vertex
        /// bake. <c>DependsOnFeature</c> is a malformed-style guard: background has no features, so a
        /// data-driven expression is spec-invalid — fall to the material's inherited default rather than
        /// bind it.
        /// </summary>
        public static void BindBackgroundPaintToApplier(Background.PaintProperties paint, Style.ZoomStyleApplier applier, Material mat)
        {
            if (!paint.Color.DependsOnFeature)
                applier.BindColor(paint.Color, ShaderProperties.PropertyId.BaseColor);
            // As fill-extrusion: the else arm is unreachable by any valid style (background has no features),
            // and exists so _Opacity keeps one writer for the layer-fade gate.
            if (!paint.Opacity.DependsOnFeature)
                applier.BindOpacity(paint.Opacity, ShaderProperties.PropertyId.Opacity);
            else
                applier.BindOpacity(new StyleProperty<float>(1f), ShaderProperties.PropertyId.Opacity);

            // Defensive identity — the clone inherits the base .mat's _FillTranslate; assert the spec's
            // "no translate" (background has no fill-translate equivalent).
            mat.SetVector(ShaderProperties.Fill.PropertyId.FillTranslate, Vector4.zero);

            // Load-bearing: a base .mat with _FillPattern=1 would make the fill shader clip the ENTIRE
            // background quad (background-pattern is not implemented).
            mat.SetFloat(ShaderProperties.Fill.PropertyId.FillPattern, 0f);
            mat.SetVector(ShaderProperties.Fill.PropertyId.PatternRect, Vector4.zero);
        }

        /// <summary>
        /// Creates a per-style-layer line Material by cloning <paramref name="settings"/>'s base line
        /// material (a Material Variant in the Editor), so the base asset is the single editable source of
        /// styling. The clone inherits the base's import-baked keywords; <c>LineTweaker.ApplyPainterContract</c>
        /// then re-asserts the code-owned render-state + color-identity contract. Returns <c>null</c> (with a
        /// warning) when no base material is assigned; there is no shader-name fallback.
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
            LineTweaker.ApplyPainterContract(mat);
            return mat;
        }

        /// <summary>
        /// Binds constant/zoom line paint properties from <paramref name="paint"/> to the material.
        /// Data-driven properties (Feature/Composite color) are handled by StyledLineTileBuilder bake;
        /// only Constant/Zoom-kind properties are bound here as uniforms.
        /// </summary>
        public static void BindLinePaintToApplier(Line.PaintProperties paint, Style.ZoomStyleApplier applier, Material mat)
        {
            // line-color: bind only for non-data-driven (Constant/Zoom). Data-driven → vertex bake.
            // The color rides the standard _BaseColor.
            if (!paint.Color.DependsOnFeature)
                applier.BindColor(paint.Color, ShaderProperties.PropertyId.BaseColor);

            // line-opacity. The else arm binds a constant 1, so _Opacity keeps one writer for the
            // layer-fade gate.
            if (!paint.Opacity.DependsOnFeature)
                applier.BindOpacity(paint.Opacity, ShaderProperties.PropertyId.Opacity);
            else
                applier.BindOpacity(new StyleProperty<float>(1f), ShaderProperties.PropertyId.Opacity);

            // ── The device-px family ──────────────────────────────────────────────────────────────
            // Non-local invariant: every line paint property below is a LOGICAL px value the shader measures
            // against the PHYSICAL framebuffer via MapPixelsToWorld. BindDevicePixelFloat is the one conversion;
            // naming the space at the binding site stops a newly-added px property from inheriting the wrong
            // basis. docs/device-pixel-ratio-design.md § "Rejected alternatives" says why the conversion stays
            // on the CPU; § "The px-valued surface" says why a CPU-only round-trip cannot see a basis error here.

            // line-width (in pixels per MapLibre spec). Non-obvious why: data-driven width bakes the
            // evaluated value into WidthScale (stream 3) by StyledLineTileBuilder, and the shader computes
            // _Width × WidthScale × pxToWorld, so the uniform carries the BASE only — binding it as a
            // device-px constant of 1 makes _Width == dpr. Scaling the mesh bake instead would put the
            // ratio inside the geometry, where a live ratio change could not reach it and
            // PreparedTileCache would serve it stale.
            if (paint.Width.DependsOnFeature)
                applier.BindDevicePixelFloat(new StyleProperty<float>(1f), ShaderProperties.Line.PropertyId.Width);
            else
                applier.BindDevicePixelFloat(paint.Width, ShaderProperties.Line.PropertyId.Width);
            // Ensure WidthIsPixels=1 so the shader interprets width as pixels. A MODE FLAG, not a px value —
            // it does not go through the conversion.
            mat.SetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels, 1f);

            // line-blur (MapLibre paint, spec default 0) → _Blur: an OPT-IN soft edge, separate from the straddle AA
            // (default 0 = no-op). Device px: the ramp's upper edge is fwidth(side) × _Blur.
            if (!paint.Blur.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.Blur, ShaderProperties.Line.PropertyId.Blur);

            // line-gap-width.
            if (!paint.GapWidth.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.GapWidth, ShaderProperties.Line.PropertyId.GapWidth);

            // line-offset.
            if (!paint.Offset.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.Offset, ShaderProperties.Line.PropertyId.LineOffset);

            // line-translate: a px offset through the SAME MapPixelsToWorld call as the widths. Parsed as
            // always-Constant, so it cannot animate; the double2 → Vector4 cast happens inside the applier.
            applier.BindDevicePixelVector(paint.Translate, ShaderProperties.Line.PropertyId.LineTranslate);

            // line-translate-anchor.
            if (!paint.TranslateAnchor.DependsOnFeature)
                applier.BindDiscreteFloat(paint.TranslateAnchor, ShaderProperties.Line.PropertyId.LineTranslateAnchor);

            // line-pattern hook: set flag; solid fallback until line patterns are implemented.
            mat.SetFloat(ShaderProperties.Line.PropertyId.LinePattern, paint.PatternName != null ? 1f : 0f);

            // line-dasharray initial bind (constant or first zoom-step evaluation at zoom=0).
            ApplyLineDashArray(paint, mat, 0.0);
        }

        /// <summary>
        /// Evaluates the line-dasharray expression at <paramref name="zoom"/> and sets
        /// <c>_DashArray</c>/<c>_DashCount</c> on the material. Called at bind time and — only for a
        /// zoom-dependent dasharray (see <see cref="RenderLayerSet"/>) — per frame. When absent or
        /// degenerate, sets _DashCount=0 (solid identity — no change to rendering path).
        /// </summary>
        public static void ApplyLineDashArray(Line.PaintProperties paint, Material mat, double zoom)
        {
            if (Line.LineDash.TryEvaluatePattern(paint.DashArray, zoom, out var packed, out int count))
            {
                // Unity boundary cast: float4 → Vector4 (at the SetVector call site, not upstream).
                mat.SetVector(ShaderProperties.Line.PropertyId.DashArray, new Vector4(packed.x, packed.y, packed.z, packed.w));
                mat.SetFloat(ShaderProperties.Line.PropertyId.DashCount,  count);
            }
            else
            {
                // No / unsupported / degenerate dasharray: solid identity.
                mat.SetVector(ShaderProperties.Line.PropertyId.DashArray, Vector4.zero);
                mat.SetFloat(ShaderProperties.Line.PropertyId.DashCount,  0f);
            }
        }
    }
}
