using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Common;
using Background = MapRenderer.Core.Style.Background;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Background <see cref="IRenderLayer"/>: a source-less per-covered-tile layer, built once per tile like
    /// <see cref="ITileMeshRenderLayer"/> but NOT one — it has no features to select, so
    /// <see cref="Tile.Processing.BackgroundQuad"/> makes one full-tile quad per covered tile directly. Every
    /// flat layer is ZWrite-off (<see cref="Materials.BaseTweaker.ApplyBaseContract"/>), so <c>renderQueue</c>
    /// alone decides the composite. See docs/meshing-design.md § "Background: a real layer, not a camera hack".
    /// </summary>
    internal sealed class BackgroundRenderLayer : IRenderLayer, IFadeableRenderLayer
    {
        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; private set; }
        public int                               DrawIndex   { get; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public ShadowCastingMode                 CastShadows => ShadowCastingMode.Off;
        public Material                          Material    { get; } // owned fill-base clone; null iff FillMaterial unassigned (slot kept, never shows)

        // internal, not private: MapRenderer.Tests.Shared's RenderLayerTestExtensions reads it to sum
        // still-easing bindings — no reader outside this class. Null iff Material null.
        internal ZoomStyleApplier Applier { get; }

        private BackgroundRenderLayer(
            MapRenderer.Core.Style.StyleLayer layer, Material material, ZoomStyleApplier applier, int drawIndex)
        {
            StyleLayer = layer;
            Material   = material;
            Applier    = applier;
            DrawIndex  = drawIndex;
        }

        /// <summary>Never returns null (the pattern of <see cref="SymbolRenderLayer.Create"/>): background
        /// always takes its declared slot, so the layers above it keep their queues. Unconfigured ⇒
        /// <see cref="Material"/> null (<see cref="Materials.MaterialFactory"/> warns) and it never shows. It
        /// takes no <c>parent</c>, because it owns no scene GameObject.</summary>
        public static BackgroundRenderLayer Create(
            Background.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateBackgroundMaterial(settings);
            if (mat == null)
                return new BackgroundRenderLayer(layer, null, null, drawIndex);

            Background.PaintProperties paint = layer.Paint;
            var applier = new ZoomStyleApplier(mat);
            // BEFORE the paint bind: a Constant opacity is pushed once at bind time and then skipped
            // forever, so an unscaled bind-time push would make a seeded fade of 0 invisible.
            applier.SeedFade(layer.IsVisibleAtZoom(initialZoom) ? 1f : 0f);
            Materials.MaterialFactory.BindBackgroundPaintToApplier(paint, applier, mat);
            // Seeded at dpr 1; the live ratio arrives with the first ApplyZoom, before any frame draws
            // (RenderLayerSet.ApplyZoom). Background has no px-valued paint, so the ratio is inert here.
            applier.ApplyZoom(new StyleFrameInputs(initialZoom, 1.0, 0.0));

            return new BackgroundRenderLayer(layer, mat, applier, drawIndex);
        }

        // zoom-expression background-color/opacity; no px-valued paint, so the ratio is inert.
        /// <inheritdoc cref="IFadeableRenderLayer.FadesGradually"/>
        public bool FadesGradually => true;

        /// <inheritdoc cref="IFadeableRenderLayer.SetFade"/>
        public void SetFade(float amount) => Applier?.SetFade(amount);

        /// <inheritdoc cref="IFadeableRenderLayer.PaintsSomething"/>
        public bool PaintsSomething => !Applier?.EffectiveOpacityIsZero ?? true;

        public void ApplyZoom(in StyleFrameInputs inputs) => Applier?.ApplyZoom(inputs);

        /// <summary>
        /// Re-targets this layer's uniform bindings at <paramref name="layer"/> — the survivor gate has
        /// already proven its mesh-affecting content unchanged. A no-op when this slot has no material
        /// (unconfigured background, see <see cref="Create"/>).
        /// </summary>
        public void Restyle(MapRenderer.Core.Style.StyleLayer layer, in StyleTransition transition, double nowSeconds)
        {
            StyleLayer = layer;
            if (Applier == null) return;
            var typed = (Background.StyleLayer)layer;
            Applier.SetTransition(transition, nowSeconds);
            Materials.MaterialFactory.BindBackgroundPaintToApplier(typed.Paint, Applier, Material);
        }

        /// <inheritdoc cref="IRenderLayer.SetDrawOrder"/>
        public void SetDrawOrder(int declaredOrder)
        {
            if (Material != null)
                Material.renderQueue = LayerDrawOrder.QueueFor(declaredOrder, MaterialSubSlot);
        }

        /// <summary>No GameObject/Mesh to destroy — background owns only its material.</summary>
        public void Dispose()
        {
            Material.DestroySafely();
        }
    }
}
