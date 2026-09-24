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
    /// Symbol <see cref="IRenderLayer"/>: <see cref="RenderLayerBuild.FramePlaced"/> (rebuilt every frame from
    /// placement) / <see cref="DrawPersistence.Persistent"/>. It owns the per-layer text and icon material
    /// clones and is drawn by <see cref="Placement.WorldSymbolRenderer"/>. Collision stays global; only the draw
    /// is per layer. NOT an <see cref="IFadeableRenderLayer"/>: the symbol shaders have no <c>_Opacity</c>, and
    /// symbols carry their own per-symbol fade and zoom gate (<c>SymbolPlacementSystem</c>).
    /// </summary>
    internal sealed class SymbolRenderLayer : IRenderLayer
    {
        private static readonly int TextColorId = Shader.PropertyToID("_TextColor");
        private static readonly int HaloColorId = Shader.PropertyToID("_HaloColor");

        public MapRenderer.Core.Style.StyleLayer StyleLayer   { get; private set; }
        public RenderLayerBuild                  Build        => RenderLayerBuild.FramePlaced;
        public DrawPersistence                   Persistence  => DrawPersistence.Persistent;
        public int                               DrawIndex    { get; }
        public int                               TransitioningCount => _applier?.TransitioningCount ?? 0;

        /// <summary>The text sits at <see cref="LayerSubSlot.Above"/>, over this layer's own icon at
        /// <see cref="LayerSubSlot.Base"/>. Non-obvious why: both shaders are <c>ZWrite Off</c> /
        /// <c>ZTest Always</c> and a road shield's icon and text are coplanar, so only <c>renderQueue</c>
        /// keeps the badge under the number it frames.</summary>
        public LayerSubSlot MaterialSubSlot => LayerSubSlot.Above;

        public ShadowCastingMode CastShadows => ShadowCastingMode.Off;

        /// <summary>The layer's primary drawn material — <see cref="WorldTextMaterial"/>. <c>null</c> iff
        /// <c>MapMaterialSet.SymbolTextWorld</c> is unassigned (the slot↔subsystem-ordinal 1:1 mapping still
        /// requires the layer to take its slot; it just never presents). Its <c>renderQueue</c> is
        /// written by <see cref="RenderLayerSet.Build"/> like every other layer's <see cref="Material"/>, at
        /// this layer's <see cref="LayerSubSlot.Above"/> sub-slot.</summary>
        public Material Material => WorldTextMaterial;

        /// <summary>The owned <c>Map/Symbol/TextWorld</c> clone of <c>MapMaterialSet.SymbolTextWorld</c>, colour
        /// tints bound; the layer's <see cref="Material"/>. <c>null</c> iff that base is unassigned, which
        /// <c>MapMaterialSet.Validate()</c> rejects, so only a set that skipped Validate reaches it.</summary>
        public Material WorldTextMaterial { get; }

        /// <summary>The owned <c>Map/Symbol/IconWorld</c> clone of
        /// <c>MapMaterialSet.SymbolIconWorld</c>. <c>null</c> iff that base is unassigned (optional, warns);
        /// then world icons stay hidden. <see cref="RenderLayerSet.Build"/> never reads it, so
        /// <see cref="Create"/> writes its <c>renderQueue</c> at <see cref="LayerSubSlot.Base"/>.</summary>
        public Material WorldIconMaterial { get; }

        /// <summary>The typed parsed symbol layer — MapView's source-fetch derivation reads this
        /// without re-walking <c>style.Layers</c>. Moved forward by <see cref="Restyle"/>.</summary>
        public SymbolStyle.StyleLayer SymbolLayer { get; private set; }

        /// <summary>Carries <c>_TextColor</c>/<c>_HaloColor</c> across a restyle ease (mirrors
        /// <see cref="FillRenderLayer"/>). <c>null</c> when <see cref="WorldTextMaterial"/> is null.</summary>
        private readonly ZoomStyleApplier _applier;

        private SymbolRenderLayer(SymbolStyle.StyleLayer layer,
            Material worldTextMaterial, Material worldIconMaterial, int drawIndex,
            ZoomStyleApplier applier)
        {
            StyleLayer        = layer;
            SymbolLayer       = layer;
            WorldTextMaterial = worldTextMaterial;
            WorldIconMaterial = worldIconMaterial;
            DrawIndex         = drawIndex;
            _applier          = applier;
        }

        /// <summary>Never returns null (unlike Fill/Line's <c>TryCreate</c>): the 1:1 slot↔subsystem-ordinal
        /// mapping and the source-fetch derivation need every Source-bearing symbol layer to take its slot.
        /// With <c>SymbolTextWorld</c> unassigned, <see cref="Material"/> stays <c>null</c> (warns) and the
        /// slot never presents. <see cref="WorldIconMaterial"/> is independently optional.</summary>
        /// <param name="initialZoom">Unused: construction binds only CONSTANT paint. Kept so every layer
        /// kind shares one creation shape.</param>
        public static SymbolRenderLayer Create(
            SymbolStyle.StyleLayer layer, MapMaterialSet settings, double initialZoom, int drawIndex)
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
            }

            Material baseWorldIconMat = settings != null ? settings.SymbolIconWorld : null;
            Material worldIconMat = null;
            if (baseWorldIconMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolIconWorld unassigned — world icons will not render.");
            else
            {
                worldIconMat = baseWorldIconMat.CloneWithParent();
                worldIconMat.name = $"MapSymbolIconWorld_{layer.Id}";
                // Build never writes this queue. Base is explicit, so the call site shows that the icon draws
                // strictly below its own layer's text (Above).
                worldIconMat.renderQueue = LayerDrawOrder.QueueFor(drawIndex, LayerSubSlot.Base);
            }

            return new SymbolRenderLayer(layer, worldTextMat, worldIconMat, drawIndex, applier);
        }

        /// <summary>
        /// Binds <c>_TextColor</c> and <c>_HaloColor</c> onto <paramref name="applier"/>. Non-local: each is
        /// the CONSTANT arm of a two-carrier split whose other arm is the vertex COLOR stream, so
        /// <c>SymbolFeatureExtractor.EvaluatePaint</c> must stay the exact complement of
        /// <see cref="SymbolTextColorCarrier"/>. The halo's width and blur are NOT here — they ride the
        /// vertex stream per feature (<c>WorldSymbolRenderer.Emit</c>).
        /// </summary>
        private static void BindTextPaint(Material material, ZoomStyleApplier applier, SymbolStyle.PaintProperties paint)
        {
            BindColorTint(material, applier, paint.Color, TextColorId);
            BindColorTint(material, applier, paint.HaloColor, HaloColorId);
        }

        /// <summary>
        /// Binds one colour tint: a Constant <paramref name="color"/> rides <paramref name="propertyId"/>,
        /// every other kind leaves the uniform at identity white and bakes into the vertex stream instead.
        /// NO manual gamma conversion belongs here — both uniforms are Color-TYPED, so Unity converts
        /// sRGB→linear on upload itself — converting here too applies the curve twice.
        /// </summary>
        private static void BindColorTint(Material material, ZoomStyleApplier applier,
            StyleProperty<MapRenderer.Core.Expressions.Color> color, int propertyId)
        {
            if (!SymbolTextColorCarrier.RidesUniform(color))
            {
                // The clone inherits the base asset's value — defend against an edited base.
                material.SetColor(propertyId, Color.white);
                return;
            }

            // A Constant's Evaluate cannot throw. Alpha is pinned to 1: the colour's alpha always rides the
            // vertex stream, and the shader leaves both uniforms' .a unread.
            var c = color.Evaluate(0.0); // MapRenderer.Core.Expressions.Color (sRGB)
            applier.BindColor(
                new StyleProperty<MapRenderer.Core.Expressions.Color>(
                    new MapRenderer.Core.Expressions.Color(c.R, c.G, c.B, 1.0)),
                propertyId);
        }

        /// <summary>Re-evaluates every QUEUED binding at the live zoom and device-pixel ratio, every frame
        /// — this is what carries a restyle's colour ease. A settled Constant binding is not queued and is
        /// never re-pushed.</summary>
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
