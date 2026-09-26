using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Core.Expressions;
using Line = MapRenderer.Core.Style.Line;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

using MapRenderer.Jobs.Lines;
namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Line <see cref="ITileMeshRenderLayer"/>: a MapLibre <c>line</c> layer as a runtime render object.
    /// Wraps <see cref="Meshing.StyledLineTileBuilder"/>. Line width in pixel mode is resolved in screen
    /// space by the shader; the per-frame work here is a zoom-dependent dasharray re-evaluated in
    /// <see cref="ApplyZoom"/>.
    /// </summary>
    internal sealed class LineRenderLayer : ITileMeshRenderLayer, IFadeableRenderLayer
    {
        /// <summary>Profiler marker name constants (SSOT) for this layer's telemetry — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // Nested under MapRenderer.View.ApplyZoom — the line applier loop (eval + per-line dash re-eval).
            // LineDash isolates the per-line zoom-step dasharray re-evaluation.
            internal const string ApplyZoomLines    = "MapRenderer.View.ApplyZoom.Lines";
            internal const string ApplyZoomLineDash = "MapRenderer.View.ApplyZoom.LineDash";
        }

        private static readonly ProfilerMarker PmZoomLines =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoomLines);
        private static readonly ProfilerMarker PmZoomLineDash =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoomLineDash);

        private Line.PaintProperties  _paint;
        private Line.LayoutProperties _layout;

        // internal, not private: MapRenderer.Tests.Shared's RenderLayerTestExtensions reads it to sum
        // still-easing bindings — no reader outside this class.
        internal ZoomStyleApplier Applier { get; }

        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; private set; }
        public int                               DrawIndex   { get; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public ShadowCastingMode                 CastShadows => ShadowCastingMode.Off;
        public Material                          Material    { get; }

        private LineRenderLayer(
            Line.StyleLayer layer, Material material, Line.PaintProperties paint,
            Line.LayoutProperties layout, ZoomStyleApplier applier, int drawIndex)
        {
            StyleLayer = layer;
            Material   = material;
            _paint     = paint;
            _layout    = layout;
            Applier    = applier;
            DrawIndex  = drawIndex;
        }

        /// <summary>
        /// Builds a line render layer from a parsed <see cref="Line.StyleLayer"/>, applying the initial
        /// zoom's uniforms. Returns <c>null</c> when the material set is unconfigured (the
        /// <see cref="Materials.MaterialFactory"/> warns and returns a null material) — the caller skips
        /// the layer: <see cref="RenderLayerSet.Build"/> records it as <c>LayerSkipReason.MaterialUnconfigured</c>.
        /// </summary>
        public static LineRenderLayer TryCreate(
            Line.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateLineMaterial(settings);
            if (mat == null) return null;

            Line.PaintProperties  paint  = layer.Paint;
            Line.LayoutProperties layout = layer.Layout;
            var applier = new ZoomStyleApplier(mat);
            // BEFORE the paint bind: a Constant opacity is pushed once at bind time and then skipped
            // forever, so an unscaled bind-time push would make a seeded fade of 0 invisible.
            applier.SeedFade(layer.IsVisibleAtZoom(initialZoom) ? 1f : 0f);
            Materials.MaterialFactory.BindLinePaintToApplier(paint, applier, mat);
            // Seeded at dpr 1 — the live ratio arrives with the first ApplyZoom, before any frame draws
            // (RenderLayerSet.ApplyZoom's contract).
            applier.ApplyZoom(new StyleFrameInputs(initialZoom, 1.0, 0.0));
            return new LineRenderLayer(layer, mat, paint, layout, applier, drawIndex);
        }

        /// <inheritdoc cref="IFadeableRenderLayer.FadesGradually"/>
        public bool FadesGradually => true;

        /// <inheritdoc cref="IFadeableRenderLayer.SetFade"/>
        public void SetFade(float amount) => Applier.SetFade(amount);

        /// <inheritdoc cref="IFadeableRenderLayer.PaintsSomething"/>
        public bool PaintsSomething => !Applier.EffectiveOpacityIsZero;

        public void ApplyZoom(in StyleFrameInputs inputs)
        {
            using (PmZoomLines.Auto())
            {
                Applier.ApplyZoom(inputs);

                // Re-evaluate the dasharray per frame ONLY when it depends on zoom. A constant dash is set
                // once at bind time, so it costs no per-frame eval or allocation.
                if (ExpressionKinds.DependsOnZoom(_paint.DashArrayKind))
                    using (PmZoomLineDash.Auto())
                        Materials.MaterialFactory.ApplyLineDashArray(_paint, Material, inputs.Zoom);
            }
        }

        /// <summary>
        /// Re-targets this layer's uniform bindings at <paramref name="layer"/> — the survivor gate has
        /// already proven its mesh-affecting content unchanged. The dash array is re-applied even though
        /// the gate proves <c>line-dasharray</c> unchanged: it feeds the per-frame branch above off
        /// <c>_paint</c>, which this swaps.
        /// </summary>
        public void Restyle(MapRenderer.Core.Style.StyleLayer layer, in StyleTransition transition, double nowSeconds)
        {
            var typed = (Line.StyleLayer)layer;
            StyleLayer = typed;
            _paint     = typed.Paint;
            _layout    = typed.Layout;
            Applier.SetTransition(transition, nowSeconds);
            Materials.MaterialFactory.BindLinePaintToApplier(_paint, Applier, Material);
        }

        /// <inheritdoc cref="IRenderLayer.SetDrawOrder"/>
        public void SetDrawOrder(int declaredOrder)
        {
            if (Material != null)
                Material.renderQueue = LayerDrawOrder.QueueFor(declaredOrder, MaterialSubSlot);
        }

        // `context.BufferClip` is not read here (see ITileMeshRenderLayer.BuildGraphRequest): clipping the
        // polyline turns the join at the boundary vertex into a cap, a seam notch instead of a seam band.
        public Meshing.ILayerMeshBuild BuildGraphRequest(
            IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
            in TileLayerProcessContext context, int materialIndex, string payloadName)
        {
            LayerInput input = Meshing.StyledLineTileBuilder.BuildLayerInput(
                selected, geometry, _paint, _layout, context.Zoom, context.TileOriginRender,
                out var colors, out var widths, context.Projection);
            // The relocated emptiness gate — see FillRenderLayer.BuildGraphRequest's own comment; line's own
            // discriminator is FeatureSelected, not RingVisitOrder (LayerInput has no such field).
            if (!input.FeatureSelected.IsCreated) return null;
            return Meshing.LineLayerBuild.Rent(input, colors, widths, materialIndex, payloadName);
        }

        public void Dispose() => RenderLayerSet.DestroyMaterialInstance(Material);
    }
}
