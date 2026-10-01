using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Jobs.Fill;
using MapRenderer.Unity.Jobs.Geometry;
using FillExtrusion = MapRenderer.Unity.Style.FillExtrusion;
using MapRenderer.Unity.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Unity.Rendering.Layers
{
    /// <summary>
    /// Fill-extrusion <see cref="ITileMeshRenderLayer"/>: a <c>fill-extrusion</c> layer as a runtime render
    /// object, ONE material that always blends — opacity 1 is a plain overwrite, so there is no separate
    /// opaque contract (see <c>docs/depth-and-render-regimes-design.md</c> § 6 (E)).
    /// <see cref="Meshing.StyledFillExtrusionTileBuilder"/> builds the roof and walls, extruded in the
    /// vertex shader along a per-vertex <c>sec φ</c>-baked up.
    /// </summary>
    internal sealed class FillExtrusionRenderLayer : ITileMeshRenderLayer, IFadeableRenderLayer
    {
        // internal, not private: MapRenderer.Tests.Shared's RenderLayerTestExtensions reads it to sum
        // still-easing bindings — no reader outside this class.
        internal ZoomStyleApplier Applier { get; }
        private FillExtrusion.PaintProperties _paint;

        public MapRenderer.Unity.Style.StyleLayer StyleLayer  { get; private set; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public ShadowCastingMode                 CastShadows => ShadowCastingMode.On;
        public Material                          Material    { get; }

        private FillExtrusionRenderLayer(
            FillExtrusion.StyleLayer layer, Material material, ZoomStyleApplier applier)
        {
            StyleLayer = layer;
            Material   = material;
            Applier    = applier;
            _paint     = layer.Paint;
        }

        /// <summary>
        /// Builds a fill-extrusion render layer from a parsed <see cref="FillExtrusion.StyleLayer"/>,
        /// applying the initial zoom's uniforms. Returns <c>null</c> when the material set is unconfigured
        /// (the <see cref="Materials.MaterialFactory"/> warns and returns a null material) — the caller
        /// skips the layer, exactly as <see cref="FillRenderLayer.TryCreate"/> does.
        /// </summary>
        /// <param name="layer">The parsed fill-extrusion style layer.</param>
        /// <param name="settings">The material set to clone the FILL-EXTRUSION base material from.</param>
        /// <param name="initialZoom">The zoom to seed the first uniform push at.</param>
        /// <returns>The new render layer, or <c>null</c> when the material set is unconfigured.</returns>
        public static FillExtrusionRenderLayer TryCreate(
            FillExtrusion.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom)
        {
            Material mat = Materials.MaterialFactory.CreateFillExtrusionMaterial(settings);
            if (mat == null) return null;

            var applier = new ZoomStyleApplier(mat);
            // BEFORE the paint bind: a Constant opacity is pushed once at bind time and then skipped
            // forever, so an unscaled bind-time push would make a seeded fade of 0 invisible.
            applier.SeedFade(layer.IsVisibleAtZoom(initialZoom) ? 1f : 0f);
            Materials.MaterialFactory.BindFillExtrusionPaintToApplier(layer.Paint, applier, mat);
            // Seeded at dpr 1 — the live ratio arrives with the first ApplyZoom, before any frame draws
            // (RenderLayerSet.ApplyZoom's contract).
            applier.ApplyZoom(new StyleFrameInputs(initialZoom, 1.0, 0.0));
            return new FillExtrusionRenderLayer(layer, mat, applier);
        }

        /// <summary>A constant <c>false</c>: the visibility fade (entering/leaving the zoom range) snaps
        /// instead of easing (<see cref="RenderLayerSet.UpdateFade"/> arms it Instant). A gradual fade
        /// (<c>true</c>) would work under the always-blend contract but is not built.</summary>
        public bool FadesGradually => false;

        /// <inheritdoc cref="IFadeableRenderLayer.SetFade"/>
        public void SetFade(float amount) => Applier.SetFade(amount);

        /// <inheritdoc cref="IFadeableRenderLayer.PaintsSomething"/>
        public bool PaintsSomething => !Applier.EffectiveOpacityIsZero;

        public void ApplyZoom(in StyleFrameInputs inputs) => Applier.ApplyZoom(inputs);

        /// <summary>
        /// Re-targets this layer's uniform bindings at <paramref name="layer"/> — the survivor gate has
        /// already proven its mesh-affecting content unchanged. Touches no render state: the contract is
        /// constant, applied once at creation.
        /// </summary>
        public void Restyle(MapRenderer.Unity.Style.StyleLayer layer, in StyleTransition transition, double nowSeconds)
        {
            var typed = (FillExtrusion.StyleLayer)layer;
            StyleLayer = typed;
            _paint     = typed.Paint;
            Applier.SetTransition(transition, nowSeconds);
            Materials.MaterialFactory.BindFillExtrusionPaintToApplier(_paint, Applier, Material);
        }

        /// <inheritdoc cref="IRenderLayer.SetDrawOrder"/>
        public void SetDrawOrder(int declaredOrder)
        {
            if (Material != null)
                Material.renderQueue = LayerDrawOrder.QueueFor(declaredOrder, MaterialSubSlot);
        }

        // Mirrors FillRenderLayer.BuildGraphRequest's shape — the graph's write step does the mesh write.
        public Meshing.ILayerMeshBuild BuildGraphRequest(
            IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
            in TileLayerProcessContext context, int materialIndex, string payloadName)
        {
            FillMeshPipeline.LayerInput input = Meshing.StyledFillExtrusionTileBuilder.BuildLayerInput(
                selected, geometry, _paint, context.Zoom, context.TileOriginRender,
                out var colors, out var bake,
                context.Buffers, context.Projection, context.BufferClip);
            // The relocated emptiness gate — see FillRenderLayer.BuildGraphRequest's own comment.
            if (!input.RingVisitOrder.IsCreated) return null;
            return Meshing.FillExtrusionLayerBuild.Rent(input, colors, bake, materialIndex, payloadName);
        }

        public void Dispose() => RenderLayerSet.DestroyMaterialInstance(Material);
    }
}
