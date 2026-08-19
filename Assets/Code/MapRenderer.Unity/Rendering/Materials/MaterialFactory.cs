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
    ///
    /// <para>Step 1 of the MapView decomposition (extracted verbatim from MapView's private statics). In the
    /// eventual ECS shape this is the "bake style layer → GPU material" system; isolating it here makes it
    /// unit-testable and keeps MapView focused on the tile loop.</para>
    ///
    /// <para>S60: all bindings now take <see cref="StyleProperty{T}"/> (replaces <c>PaintPropertyEvaluator</c>).
    /// Data-driven guard: was <c>!= null</c>; now <c>!prop.DependsOnFeature</c>. Translate: was
    /// TranslateX/Y pair; now <c>Translate</c> as a <c>StyleProperty&lt;double2&gt;</c>, bound through the
    /// applier (S107) rather than evaluated at bind time.</para>
    /// </summary>
    internal static class MaterialFactory
    {
        /// <summary>
        /// Creates a per-style-layer fill Material by cloning <paramref name="settings"/>'s base fill
        /// material (a Material Variant in the Editor — see <see cref="MaterialExtensions.CloneWithParent"/>),
        /// so the base asset is the single editable source of styling. The clone inherits the base's
        /// import-baked keywords; <see cref="FillTweaker.ApplyPainterContract"/> then re-asserts the
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
            FillTweaker.ApplyPainterContract(mat);
            return mat;
        }

        /// <summary>
        /// Binds constant/zoom paint properties from <paramref name="paint"/> to the material.
        /// S60: StyleProperty&lt;T&gt; replaces old PaintPropertyEvaluator pairs; null-guard →
        /// <c>!prop.DependsOnFeature</c>; TranslateX/Y → a single <c>StyleProperty&lt;double2&gt;</c>.
        /// </summary>
        public static void BindFillPaintToApplier(Fill.PaintProperties paint, Style.ZoomStyleApplier applier, Material mat)
        {
            // fill-opacity. Constant/Zoom rides the _Opacity uniform; data-driven is baked per-feature into
            // the COLOR stream's alpha by StyledFillTileBuilder (P4). In the baked case _Opacity MUST be
            // pinned to 1: the fragment computes `alpha *= vColor.a * _Opacity`, so leaving the material's
            // inherited value would multiply the opacity in twice. Same convention as data-driven line-width
            // below (a constant base, evaluated value baked per-vertex) — except that opacity is unitless, so
            // its base is a literal 1, while line-width's base is a px value and therefore carries the
            // device-pixel ratio (S107).
            if (paint.Opacity.DependsOnFeature)
                mat.SetFloat(ShaderProperties.PropertyId.Opacity, 1f);
            else
                applier.BindFloat(paint.Opacity, ShaderProperties.PropertyId.Opacity);

            // fill-outline-color: bind only when explicitly set (not a fallback) and non-data-driven.
            if (!paint.OutlineColorIsFallback && !paint.OutlineColor.DependsOnFeature)
                applier.BindColor(paint.OutlineColor, ShaderProperties.Fill.PropertyId.FillOutlineColor);

            // fill-antialias.
            if (!paint.Antialias.DependsOnFeature)
                applier.BindFloat(paint.Antialias, ShaderProperties.Fill.PropertyId.FillAntialias);

            // fill-translate: a px offset consumed through Fill_VertexModify's MapPixelsToWorld — the same
            // device-px space line-translate lives in (S107), so it takes the same conversion. Also parsed
            // as always-Constant, so — as with line-translate — moving it from a bind-time Evaluate(0.0)
            // to the per-frame applier cannot animate it or throw where it previously could not.
            applier.BindDevicePixelVector(paint.Translate, ShaderProperties.Fill.PropertyId.FillTranslate);

            // fill-translate-anchor.
            if (!paint.TranslateAnchor.DependsOnFeature)
                applier.BindFloat(paint.TranslateAnchor, ShaderProperties.Fill.PropertyId.FillTranslateAnchor);

            // fill-pattern: flag the layer, and start it UNRESOLVED (zero-area rect ⇒ the shader clips).
            // The sprite sheet is fetched asynchronously and cannot exist yet at material-build time, so
            // every pattern layer necessarily starts here; FillRenderLayer.SetSprites resolves it once the
            // sheet lands. Painting nothing until then is the spec behaviour — a pattern layer must NOT fall
            // back to fill-color, whose default is opaque black.
            mat.SetFloat(ShaderProperties.Fill.PropertyId.FillPattern, paint.PatternName != null ? 1f : 0f);
            mat.SetVector(ShaderProperties.Fill.PropertyId.PatternRect, Vector4.zero);
        }

        /// <summary>
        /// S23 I3 — creates a per-style-layer fill-extrusion Material by cloning
        /// <paramref name="settings"/>'s <see cref="MapMaterialSet.FillExtrusionMaterial"/> base (a dedicated
        /// <c>Map/FillExtrusion</c> material, replacing I1's reuse of the flat fill base). Applies
        /// <see cref="FillExtrusionTweaker.ApplyElevatedContract"/> — the ELEVATED-3D contract (opaque,
        /// depth-writing), NOT the flat painter contract the FILL/LINE materials use: buildings are the first
        /// geometry that must occupy the depth buffer to occlude one another and their own walls.
        ///
        /// <para>Returns <c>null</c> (with a warning) when no config / base material is assigned — mirrors
        /// <see cref="CreateFillMaterial"/> / the <c>SymbolIconWorld</c> optional-with-warn precedent.</para>
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
        /// S23 I2b — binds constant/zoom fill-extrusion paint properties from <paramref name="paint"/> to the
        /// dedicated <c>Map/FillExtrusion</c> material (I1 bound onto the reused FILL material; I2b binds
        /// onto <see cref="CreateFillExtrusionMaterial"/>'s own material and its own shader properties).
        ///
        /// <para>Height/Base: Constant/Zoom → the <c>_ExtrusionHeight</c>/<c>_ExtrusionBase</c> uniforms the
        /// VS lerps by (S11 path); Feature/Composite → skipped here AND explicitly zeroed (defensive
        /// identity, same reasoning as the pattern reset below) — <see cref="Meshing.StyledFillExtrusionTileBuilder"/>
        /// bakes the evaluated value per-vertex instead (S12 path), and the VS composes uniform+bake
        /// additively, so a stray non-zero uniform under the baked path would double the elevation.</para>
        ///
        /// <para><c>fill-extrusion-color</c>'s alpha channel is NOT special-cased to 1 — matches
        /// <see cref="BindFillPaintToApplier"/>'s reasoning (opacity multiplies the color's own alpha rather
        /// than overriding it).</para>
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

            if (!paint.Opacity.DependsOnFeature)
                applier.BindFloat(paint.Opacity, ShaderProperties.PropertyId.Opacity);

            // fill-extrusion-height / fill-extrusion-base: Constant/Zoom rides the uniform; data-driven is
            // baked per-vertex by the builder instead, and the uniform is pinned to 0 so the VS's additive
            // `lerp(_ExtrusionBase + bakedBase, _ExtrusionHeight + bakedHeight, t)` does not double-count.
            if (!paint.Height.DependsOnFeature)
                applier.BindFloat(paint.Height, ShaderProperties.FillExtrusion.PropertyId.ExtrusionHeight);
            else
                mat.SetFloat(ShaderProperties.FillExtrusion.PropertyId.ExtrusionHeight, 0f);

            if (!paint.Base.DependsOnFeature)
                applier.BindFloat(paint.Base, ShaderProperties.FillExtrusion.PropertyId.ExtrusionBase);
            else
                mat.SetFloat(ShaderProperties.FillExtrusion.PropertyId.ExtrusionBase, 0f);

            // fill-extrusion-translate: a px offset consumed through FillExtrusion_VertexModify's
            // MapPixelsToWorld — same device-px space fill-translate/line-translate live in (S107).
            applier.BindDevicePixelVector(paint.Translate, ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslate);
            if (!paint.TranslateAnchor.DependsOnFeature)
                applier.BindFloat(paint.TranslateAnchor, ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslateAnchor);

            // No Fill-pattern defensive reset here (unlike BindBackgroundPaintToApplier): Map/FillExtrusion
            // (I2b) is its OWN dedicated shader with its OWN CBUFFER — it declares no _FillPattern/
            // _FillTranslate properties at all, so there is nothing inherited from a Fill Variant to clobber.
            // That defence was I1-only, back when this layer reused the flat FILL material verbatim.
        }

        /// <summary>Background material: a clone of the FILL base (§3.6 — the fill shader's flat lit path IS
        /// the ground look; no separate base asset). Null (warn) when unconfigured, mirroring CreateFillMaterial.</summary>
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
        /// Binds constant/zoom background paint properties from <paramref name="paint"/> to the material.
        /// A UNIFORM colour over WHITE vertex colours — the line-color pattern
        /// (<see cref="BindLinePaintToApplier"/>), NOT the fill per-vertex bake (the quad's verts are white
        /// by construction — see <see cref="Style.BackgroundRenderLayer"/>). <c>DependsOnFeature</c> is a
        /// malformed-style guard: background has no features, so a data-driven expression is spec-invalid —
        /// fall to the material's inherited default rather than bind it.
        /// </summary>
        public static void BindBackgroundPaintToApplier(Background.PaintProperties paint, Style.ZoomStyleApplier applier, Material mat)
        {
            if (!paint.Color.DependsOnFeature)
                applier.BindColor(paint.Color, ShaderProperties.PropertyId.BaseColor);
            if (!paint.Opacity.DependsOnFeature)
                applier.BindFloat(paint.Opacity, ShaderProperties.PropertyId.Opacity);

            // Defensive identity — the clone inherits the base .mat's _FillTranslate; assert the spec's
            // "no translate" (background has no fill-translate equivalent).
            mat.SetVector(ShaderProperties.Fill.PropertyId.FillTranslate, Vector4.zero);

            // Same defence for the pattern uniforms, and now load-bearing rather than merely tidy: a base
            // .mat carrying _FillPattern=1 would make the fill shader clip the ENTIRE background quad
            // (background-pattern is not implemented — see Background.PaintProperties.PatternName).
            mat.SetFloat(ShaderProperties.Fill.PropertyId.FillPattern, 0f);
            mat.SetVector(ShaderProperties.Fill.PropertyId.PatternRect, Vector4.zero);
        }

        /// <summary>
        /// Creates a per-style-layer line Material by cloning <paramref name="settings"/>'s base line
        /// material (a Material Variant in the Editor — see <see cref="MaterialExtensions.CloneWithParent"/>),
        /// so the base asset is the single editable source of styling. The clone inherits the base's
        /// import-baked keywords; <see cref="LineTweaker.ApplyPainterContract"/> then re-asserts the
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
            LineTweaker.ApplyPainterContract(mat);
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
        public static void BindLinePaintToApplier(Line.PaintProperties paint, Style.ZoomStyleApplier applier, Material mat)
        {
            // line-color: bind only for non-data-driven (Constant/Zoom). Data-driven → vertex bake.
            // The color rides the standard _BaseColor (S58 collapsed the redundant _MapColor into it).
            if (!paint.Color.DependsOnFeature)
                applier.BindColor(paint.Color, ShaderProperties.PropertyId.BaseColor);

            // line-opacity.
            if (!paint.Opacity.DependsOnFeature)
                applier.BindFloat(paint.Opacity, ShaderProperties.PropertyId.Opacity);

            // ── The device-px family (S107) ───────────────────────────────────────────────────────
            // Every line paint property below is a LOGICAL px value whose shader consumer measures against
            // _ScreenParams — the PHYSICAL framebuffer — via MapPixelsToWorld. BindDevicePixelFloat is the
            // one conversion; naming the space at the binding site is what stops a newly-added px property
            // from silently inheriting the wrong basis (the defect that cost the whole line family).
            //
            // The conversion stays on the CPU deliberately: MapPixelsToWorld must keep returning metres per
            // DEVICE pixel, because the AA straddle pad and the hairline floor derived from it are genuinely
            // sampling-grid quantities (half a physical pixel is half a physical pixel at any density) and
            // must NOT scale. Neither the WIDTH family nor line-dasharray rides on that measurement any more
            // (S110 for dashes, S116 for width): both pair the device-px _Width bound here with the
            // frame-constant _MapFrameMetersPerDevicePixel, which MapCamera.SyncToCamera measures off the
            // live camera. Both halves carry the dpr, so it cancels and the rendered result is dpr-invariant
            // — which is exactly why a CPU-only round-trip cannot see a basis error here.

            // line-width (in pixels per MapLibre spec).
            // Convention (data-driven width): when Width depends on feature, the evaluated width is baked
            // into WidthScale (stream 3) by StyledLineTileBuilder, and the shader computes
            // _Width × WidthScale × pxToWorld — so the uniform carries the BASE only. Binding that base as a
            // device-px constant of 1 makes _Width == dpr, i.e. widthWorld = dpr × bakedPx × pxToWorld.
            // Scaling the mesh bake instead would put the ratio inside the geometry, where a live ratio
            // change could not reach it and PreparedTileCache would serve it stale.
            if (paint.Width.DependsOnFeature)
                applier.BindDevicePixelFloat(new StyleProperty<float>(1f), ShaderProperties.Line.PropertyId.Width);
            else
                applier.BindDevicePixelFloat(paint.Width, ShaderProperties.Line.PropertyId.Width);
            // Ensure WidthIsPixels=1 so the shader interprets width as pixels. A MODE FLAG, not a px value —
            // it does not go through the conversion.
            mat.SetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels, 1f);

            // line-blur (MapLibre paint, spec default 0) → _Blur: an OPT-IN soft edge (default 0 = hard).
            // NOT antialiasing — edge AA was removed; the shader draws a hard edge and floors thin-line width.
            // Device px: the ramp's upper edge is fwidth(side) × _Blur, a band exactly _Blur device px wide.
            if (!paint.Blur.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.Blur, ShaderProperties.Line.PropertyId.Blur);

            // line-gap-width.
            if (!paint.GapWidth.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.GapWidth, ShaderProperties.Line.PropertyId.GapWidth);

            // line-offset (S44).
            if (!paint.Offset.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.Offset, ShaderProperties.Line.PropertyId.LineOffset);

            // line-translate: a px offset applied through the SAME MapPixelsToWorld call as the widths, so it
            // sits in the identical device space. Parsed as always-Constant (the array components are
            // scalars, not expressions), so moving it from a bind-time Evaluate(0.0) to the per-frame applier
            // cannot animate it. Unity boundary cast (double2 → Vector4) happens inside the applier.
            applier.BindDevicePixelVector(paint.Translate, ShaderProperties.Line.PropertyId.LineTranslate);

            // line-translate-anchor.
            if (!paint.TranslateAnchor.DependsOnFeature)
                applier.BindFloat(paint.TranslateAnchor, ShaderProperties.Line.PropertyId.LineTranslateAnchor);

            // line-pattern hook: set flag; solid fallback until S17.
            mat.SetFloat(ShaderProperties.Line.PropertyId.LinePattern, paint.PatternName != null ? 1f : 0f);

            // S43: line-dasharray initial bind (constant or first zoom-step evaluation at zoom=0).
            ApplyLineDashArray(paint, mat, 0.0);
        }

        /// <summary>
        /// Evaluates the line-dasharray expression at <paramref name="zoom"/> and sets
        /// <c>_DashArray</c>/<c>_DashCount</c> on the material. Called at bind time and — only for a
        /// zoom-dependent dasharray (see <see cref="RenderLayerSet"/>) — per frame. When absent or
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
