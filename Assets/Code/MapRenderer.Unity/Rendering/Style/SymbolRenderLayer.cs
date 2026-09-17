using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Text.Placement;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Symbol <see cref="IRenderLayer"/>: a MapLibre <c>symbol</c> layer as a runtime render object (the
    /// render-layer model). Axes: <see cref="RenderLayerBuild.FramePlaced"/> —
    /// rebuilt every frame from per-symbol placement, NOT the Burst tile-mesh pipeline — /
    /// <see cref="DrawPersistence.Persistent"/> — per Epic A / A1, this layer's material is bound + presented
    /// by <see cref="Placement.WorldSymbolRenderer"/> (the world-anchored draw path), redrawn by Unity every
    /// camera render with no orchestrator.
    ///
    /// <para>Owns (D11, migrated here from <c>SymbolSubsystem</c>): the per-layer <see cref="WorldTextMaterial"/>/
    /// <see cref="WorldIconMaterial"/> clones and the halo + text-colour binds. <see cref="Material"/> IS
    /// <see cref="WorldTextMaterial"/> — the old screen-space <c>SymbolText</c> clone (and its
    /// <c>IconMaterial</c> sibling) is retired; its <c>renderQueue</c> is written by
    /// <see cref="RenderLayerSet.Build"/> like every other layer (replacing the shader's Overlay-4000
    /// default) for free, since <c>Build</c> reads <see cref="IRenderLayer.Material"/>. The world icon's
    /// queue has no such free ride (nothing else reads a symbol layer's icon material at Build time), so
    /// <see cref="Create"/> writes it directly. Collision stays global (D8) — only the DRAW is per-layer,
    /// via the material <see cref="Placement.WorldSymbolRenderer.EndFrame"/> resolves for this layer's
    /// slot.</para>
    ///
    /// <para><b>G7/D7 (Stage 2, road-shields):</b> the icon and text are coplanar at the same anchor
    /// (D5 centres the shield's number on its sprite) and both shaders are <c>ZWrite Off</c> /
    /// <c>ZTest Always</c> — there is no depth arbitration, only submission order via <c>renderQueue</c>.
    /// So the icon owns this layer's <see cref="LayerSubSlot.Base"/> sub-slot and the text owns
    /// <see cref="LayerSubSlot.Above"/> — the badge always draws under the number it frames, never over
    /// it.</para>
    ///
    /// <para>Deliberately NOT an <see cref="IFadeableRenderLayer"/>: the symbol shaders declare no
    /// <c>_Opacity</c> for a LAYER fade to ride, symbols already carry their own per-symbol fade, and their
    /// zoom/visibility gate is <c>SymbolPlacementSystem</c>'s existing <c>IsVisibleAtZoom</c> reads.</para>
    /// </summary>
    internal sealed class SymbolRenderLayer : IRenderLayer
    {
        private static readonly int HaloColorId  = Shader.PropertyToID("_HaloColor");
        private static readonly int HaloWidthId  = Shader.PropertyToID("_HaloWidthPx");
        private static readonly int HaloBlurId   = Shader.PropertyToID("_HaloBlurPx");
        private static readonly int TextColorId  = Shader.PropertyToID("_TextColor");

        public MapRenderer.Core.Style.StyleLayer StyleLayer   { get; private set; }
        public RenderLayerBuild                  Build        => RenderLayerBuild.FramePlaced;
        public DrawPersistence                   Persistence  => DrawPersistence.Persistent;
        public int                               DrawIndex    { get; }
        public int                               TransitioningCount => _applier?.TransitioningCount ?? 0;

        /// <summary>The text sits at <see cref="LayerSubSlot.Above"/>: <see cref="Material"/> IS
        /// <see cref="WorldTextMaterial"/>, and it must draw over this layer's own
        /// <see cref="WorldIconMaterial"/> (at <see cref="LayerSubSlot.Base"/>) — otherwise the badge paints
        /// out the number it frames (G7/D7).</summary>
        public LayerSubSlot MaterialSubSlot => LayerSubSlot.Above;

        public ShadowCastingMode CastShadows => ShadowCastingMode.Off;

        /// <summary>The layer's primary drawn material — <see cref="WorldTextMaterial"/>. <c>null</c> iff
        /// <c>MapMaterialSet.SymbolTextWorld</c> is unassigned (the slot↔subsystem-ordinal 1:1 mapping still
        /// requires the layer to take its slot, §3.5; it just never presents). Its <c>renderQueue</c> is
        /// written by <see cref="RenderLayerSet.Build"/> like every other layer's <see cref="Material"/>, at
        /// this layer's <see cref="LayerSubSlot.Above"/> sub-slot (G7/D7).</summary>
        public Material Material => WorldTextMaterial;

        /// <summary>Epic A / A1 (design §11 A1 D5): the owned <c>Map/Symbol/TextWorld</c> clone of
        /// <c>MapMaterialSet.SymbolTextWorld</c>, halo bound — the world-anchored point-text draw path's
        /// per-layer material, and the layer's <see cref="Material"/>. <c>null</c> iff
        /// <c>MapMaterialSet.SymbolTextWorld</c> is unassigned (REQUIRED — enforced by
        /// <c>MapMaterialSet.Validate()</c>, so this is null only for a throwaway/test <c>MapMaterialSet</c>
        /// that skipped Validate).</summary>
        public Material WorldTextMaterial { get; }

        /// <summary>Epic A / A1 (design §11 A1 D5): the owned <c>Map/Symbol/IconWorld</c> clone of
        /// <c>MapMaterialSet.SymbolIconWorld</c> (no halo). <c>null</c> iff
        /// <c>MapMaterialSet.SymbolIconWorld</c> is unassigned (optional-with-warn) — world icons stay
        /// hidden, text is unaffected. Its <c>renderQueue</c> is written directly by <see cref="Create"/> —
        /// unlike <see cref="WorldTextMaterial"/>, nothing reads a symbol layer's icon material at
        /// <see cref="RenderLayerSet.Build"/> time, so it has no free ride. It owns this layer's
        /// <see cref="LayerSubSlot.Base"/> sub-slot, strictly below <see cref="WorldTextMaterial"/>'s
        /// <see cref="LayerSubSlot.Above"/> (G7/D7).</summary>
        public Material WorldIconMaterial { get; }

        /// <summary>The typed parsed symbol layer — MapView's D10 source-fetch derivation reads this
        /// without re-walking <c>style.Layers</c>. Moved forward by <see cref="Restyle"/>.</summary>
        public SymbolStyle.StyleLayer SymbolLayer { get; private set; }

        /// <summary>Re-binds <c>_TextColor</c>/<c>_HaloColor</c>/<c>_HaloWidthPx</c>/<c>_HaloBlurPx</c> at
        /// the live zoom every frame (mirrors <see cref="FillRenderLayer"/>). <c>null</c> when
        /// <see cref="WorldTextMaterial"/> is null.</summary>
        private readonly ZoomStyleApplier _applier;

        private SymbolRenderLayer(SymbolStyle.StyleLayer layer,
            Material worldTextMaterial, Material worldIconMaterial, int drawIndex, Transform parent,
            ZoomStyleApplier applier)
        {
            StyleLayer        = layer;
            SymbolLayer       = layer;
            WorldTextMaterial = worldTextMaterial;
            WorldIconMaterial = worldIconMaterial;
            DrawIndex         = drawIndex;
            _applier          = applier;
        }

        /// <summary>Never returns null (unlike Fill/Line's <c>TryCreate</c>): the slot↔subsystem-ordinal 1:1
        /// mapping (§3.5) and the source-fetch derivation both require every Source-bearing symbol layer to
        /// take its slot even when <c>MapMaterialSet.SymbolTextWorld</c> is unassigned — in that case
        /// <see cref="Material"/> stays <c>null</c> (warn once), <see cref="RenderLayerSet.Build"/> skips the
        /// queue write, and this layer's slot never presents. <see cref="WorldIconMaterial"/> is resolved the
        /// same way from <c>MapMaterialSet.SymbolIconWorld</c> — independently optional, no halo bind, its
        /// queue written here directly (§0.1) rather than by <c>Build</c>.</summary>
        public static SymbolRenderLayer Create(
            SymbolStyle.StyleLayer layer, MapMaterialSet settings, double initialZoom, int drawIndex,
            Transform parent = null)
        {
            Material baseWorldTextMat = settings != null ? settings.SymbolTextWorld : null;
            Material worldTextMat = null;
            ZoomStyleApplier applier = null;
            if (baseWorldTextMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolTextWorld unassigned — labels will not render.");
            else
            {
                worldTextMat = baseWorldTextMat.CloneWithParent();
                worldTextMat.name = $"MapSymbolTextWorld_{layer.Id}";
                applier = new ZoomStyleApplier(worldTextMat);
                BindTextPaint(worldTextMat, applier, layer.Paint);
                // Seeded at dpr 1, like every other layer kind's bind-time seed — without this the halo px
                // uniforms are never pushed at construction (BindDevicePixelFloat never pushes at bind time).
                applier.ApplyZoom(new StyleFrameInputs(initialZoom, 1.0, 0.0));
            }

            Material baseWorldIconMat = settings != null ? settings.SymbolIconWorld : null;
            Material worldIconMat = null;
            if (baseWorldIconMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolIconWorld unassigned — world icons will not render.");
            else
            {
                worldIconMat = baseWorldIconMat.CloneWithParent();
                worldIconMat.name = $"MapSymbolIconWorld_{layer.Id}";
                // §0.1: no Build-time free ride like WorldTextMaterial/Material. Base explicitly (not the
                // default arg) so the pairing with the text's Above sub-slot is visible at the call site —
                // G7/D7: the icon must draw strictly below its own layer's text.
                worldIconMat.renderQueue = LayerDrawOrder.QueueFor(drawIndex, LayerSubSlot.Base);
            }

            return new SymbolRenderLayer(layer, worldTextMat, worldIconMat, drawIndex, parent, applier);
        }

        /// <summary>
        /// Binds <c>_TextColor</c>/<c>_HaloColor</c>/<c>_HaloWidthPx</c>/<c>_HaloBlurPx</c> onto
        /// <paramref name="applier"/>. Non-local: a CONSTANT <c>text-color</c> rides the uniform and every
        /// other kind bakes into the vertex COLOR stream instead, so <c>SymbolFeatureExtractor.EvaluatePaint</c>
        /// must stay the exact complement of the guard below; and NO manual gamma conversion belongs here —
        /// <c>_TextColor</c>/<c>_HaloColor</c> are Color-TYPED, so Unity converts sRGB→linear on upload itself.
        /// </summary>
        private static void BindTextPaint(Material material, ZoomStyleApplier applier, SymbolStyle.PaintProperties paint)
        {
            if (SymbolTextColorCarrier.RidesUniform(paint.Color))
            {
                // A Constant expression's Evaluate(zoom) cannot throw. Alpha is pinned to 1 — the uniform is
                // RGB-only; text-color's own alpha rides the vertex opacity stream regardless of which
                // carrier holds RGB (the shader declares _TextColor.a unread, guarding against a future read
                // applying opacity twice).
                var c = paint.Color.Evaluate(0.0); // MapRenderer.Core.Expressions.Color (sRGB)
                applier.BindColor(
                    new StyleProperty<MapRenderer.Core.Expressions.Color>(
                        new MapRenderer.Core.Expressions.Color(c.R, c.G, c.B, 1.0)),
                    TextColorId);
            }
            else
            {
                // The clone inherits the base asset's _TextColor — defend against an edited base.
                material.SetColor(TextColorId, Color.white);
            }

            if (!paint.HaloColor.DependsOnFeature)
                applier.BindColor(paint.HaloColor, HaloColorId);
            // S107: both halo terms are added to a signed distance the SDF shader carries in DEVICE px, and
            // scale TOGETHER — both take BindDevicePixelFloat's px→device conversion.
            if (!paint.HaloWidth.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.HaloWidth, HaloWidthId);
            if (!paint.HaloBlur.DependsOnFeature)
                applier.BindDevicePixelFloat(paint.HaloBlur, HaloBlurId);
        }

        /// <summary>Re-evaluates every QUEUED binding at the live zoom and device-pixel ratio, every frame
        /// — the halo no longer freezes at the style-load zoom (SSOT Stage 5 criterion 1). A settled
        /// Constant binding is not queued and is never re-pushed.</summary>
        public void ApplyZoom(in StyleFrameInputs inputs) => _applier?.ApplyZoom(inputs);

        /// <summary>
        /// Re-targets this layer's uniform bindings at <paramref name="layer"/> — the survivor gate has
        /// already proven the layer's mesh-affecting content unchanged (<see cref="SurvivingLayerGate"/>),
        /// so only <c>StyleLayer</c>/<c>SymbolLayer</c> and the applier's bindings move.
        /// </summary>
        public void Restyle(MapRenderer.Core.Style.StyleLayer layer, in StyleTransition transition, double nowSeconds)
        {
            var typed = (SymbolStyle.StyleLayer)layer;
            StyleLayer  = typed;
            SymbolLayer = typed;
            if (_applier == null) return;
            _applier.SetTransition(transition, nowSeconds);
            BindTextPaint(WorldTextMaterial, _applier, typed.Paint);
        }

        /// <summary>Re-stamps BOTH materials — <see cref="Material"/> at <see cref="LayerSubSlot.Above"/>
        /// AND <see cref="WorldIconMaterial"/> at <see cref="LayerSubSlot.Base"/>, whose <see cref="Create"/>
        /// write gets no <see cref="RenderLayerSet.Build"/> free ride. Missing the icon here is how a reorder
        /// puts a symbol layer's text and icon in different bands.</summary>
        /// <param name="declaredOrder">This layer's index in the new document's declared layer order.</param>
        public void SetDrawOrder(int declaredOrder)
        {
            if (WorldTextMaterial != null)
                WorldTextMaterial.renderQueue = LayerDrawOrder.QueueFor(declaredOrder, LayerSubSlot.Above);
            if (WorldIconMaterial != null)
                WorldIconMaterial.renderQueue = LayerDrawOrder.QueueFor(declaredOrder, LayerSubSlot.Base);
        }

        public void Dispose()
        {
            if (WorldTextMaterial != null) RenderLayerSet.DestroyMaterialInstance(WorldTextMaterial); // also destroys Material (alias)
            if (WorldIconMaterial != null) RenderLayerSet.DestroyMaterialInstance(WorldIconMaterial);
        }
    }
}
