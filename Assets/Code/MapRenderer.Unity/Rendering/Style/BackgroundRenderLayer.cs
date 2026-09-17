using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Common;
using Background = MapRenderer.Core.Style.Background;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Background <see cref="IRenderLayer"/>: a style's <c>background</c> layer as a runtime render object
    /// (the render-layer model). Epic A / A2: background is now a source-less
    /// per-covered-tile <see cref="RenderLayerBuild.TileMesh"/> layer — <see cref="Tile.Processing.BackgroundQuad"/>
    /// synthesizes one full-tile-extent quad per tile in the camera cover, projected through the same
    /// <see cref="MapRenderer.Core.Geo.IProjection"/> fill/line use, and registered with the active
    /// <see cref="Backend.ITileRenderBackend"/> at this layer's <see cref="DrawIndex"/> — replacing E3's
    /// interim self-owned world-cap quad (one static <see cref="Mesh"/> + a persistent <see cref="GameObject"/>
    /// /<see cref="MeshRenderer"/>, gated to Mercator-only via <c>SetVisible</c>). This layer no longer owns
    /// any geometry/scene object — it owns ONLY the material; the backend owns every per-tile mesh, so the
    /// Mercator-only gate is gone (the globe now renders a correctly curved background across the covered
    /// tile pyramid — design §C).
    ///
    /// <para>Owns: a material clone (a FILL-base clone at its global queue — the fill shader's flat lit path
    /// IS the ground look, <see cref="Materials.MaterialFactory.CreateBackgroundMaterial"/>). <see cref="Persistence"/>
    /// stays <see cref="DrawPersistence.Persistent"/> (the backend redraws each tile's quad every camera
    /// render with no orchestrator, same as fill/line).</para>
    ///
    /// <para>Coplanar y=0 with fills/lines BY DESIGN: every flat layer is ZWrite-off
    /// (<see cref="Materials.BaseTweaker.ApplyBaseContract"/>), so painter order via <c>renderQueue</c> alone
    /// decides the composite.</para>
    /// </summary>
    internal sealed class BackgroundRenderLayer : IRenderLayer, IFadeableRenderLayer
    {
        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; private set; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
        public int                               DrawIndex   { get; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public ShadowCastingMode                 CastShadows => ShadowCastingMode.Off;
        public Material                          Material    { get; } // owned fill-base clone; null iff FillMaterial unassigned (slot kept, never shows)
        public int                               TransitioningCount => _applier?.TransitioningCount ?? 0;

        private readonly ZoomStyleApplier _applier; // null iff Material null

        private BackgroundRenderLayer(
            MapRenderer.Core.Style.StyleLayer layer, Material material, ZoomStyleApplier applier, int drawIndex)
        {
            StyleLayer = layer;
            Material   = material;
            _applier   = applier;
            DrawIndex  = drawIndex;
        }

        /// <summary>Never returns null (the Symbol Create pattern, <see cref="SymbolRenderLayer.Create"/>):
        /// background always takes its declared slot so the layers above it keep their queues regardless of
        /// material config; unconfigured ⇒ <see cref="Material"/> null (<see cref="Materials.MaterialFactory"/>
        /// warns), no geometry, never shows. Takes no Hierarchy <c>parent</c> — A2: background owns no scene
        /// GameObject any more (the backend owns every per-tile quad), so only the GameObject-bearing symbol
        /// arm of <see cref="RenderLayerFactory"/> forwards one.</summary>
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
            // Seeded at dpr 1 — the live ratio arrives with the first ApplyZoom, before any frame draws
            // (RenderLayerSet.ApplyZoom's contract). Background has no px-valued paint, so the ratio is
            // inert here; it is threaded for interface uniformity.
            applier.ApplyZoom(new StyleFrameInputs(initialZoom, 1.0, 0.0));

            return new BackgroundRenderLayer(layer, mat, applier, drawIndex);
        }

        // zoom-expression background-color/opacity; no px-valued paint, so the ratio is inert.
        /// <inheritdoc cref="IFadeableRenderLayer.FadesGradually"/>
        public bool FadesGradually => true;

        /// <inheritdoc cref="IFadeableRenderLayer.SetFade"/>
        public void SetFade(float amount) => _applier?.SetFade(amount);

        /// <inheritdoc cref="IFadeableRenderLayer.PaintsSomething"/>
        public bool PaintsSomething => !_applier?.EffectiveOpacityIsZero ?? true;

        public void ApplyZoom(in StyleFrameInputs inputs) => _applier?.ApplyZoom(inputs);

        /// <summary>
        /// Re-targets this layer's uniform bindings at <paramref name="layer"/> — the survivor gate has
        /// already proven its mesh-affecting content unchanged. A no-op when this slot has no material
        /// (unconfigured background, see <see cref="Create"/>).
        /// </summary>
        public void Restyle(MapRenderer.Core.Style.StyleLayer layer, in StyleTransition transition, double nowSeconds)
        {
            StyleLayer = layer;
            if (_applier == null) return;
            var typed = (Background.StyleLayer)layer;
            _applier.SetTransition(transition, nowSeconds);
            Materials.MaterialFactory.BindBackgroundPaintToApplier(typed.Paint, _applier, Material);
        }

        /// <inheritdoc cref="IRenderLayer.SetDrawOrder"/>
        public void SetDrawOrder(int declaredOrder)
        {
            if (Material != null)
                Material.renderQueue = LayerDrawOrder.QueueFor(declaredOrder, MaterialSubSlot);
        }

        /// <summary>A2: no GameObject/Mesh to destroy — background owns only its material.</summary>
        public void Dispose()
        {
            Material.DestroySafely();
        }
    }
}
