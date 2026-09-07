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
    /// Wraps the managed <see cref="Meshing.StyledLineTileBuilder"/> (unchanged). Line width in pixel mode
    /// is resolved in screen space by the shader (S104); the per-frame work here is a zoom-dependent
    /// dasharray re-evaluated in <see cref="ApplyZoom"/>.
    /// Axes (design §"Axis pinning"): <see cref="RenderLayerBuild.TileMesh"/> / <see cref="DrawPersistence.Persistent"/>.
    /// </summary>
    internal sealed class LineRenderLayer : ITileMeshRenderLayer
    {
        /// <summary>Profiler marker name constants (SSOT) for this layer's telemetry — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // Nested under MapRenderer.View.ApplyZoom — the line applier loop (eval + per-line dash re-eval).
            // LineDash isolates the per-line zoom-step dasharray re-evaluation. Preserved verbatim from the
            // retired StyledLayerSet so the S46 greppable-marker set is intact.
            internal const string ApplyZoomLines    = "MapRenderer.View.ApplyZoom.Lines";
            internal const string ApplyZoomLineDash = "MapRenderer.View.ApplyZoom.LineDash";
        }

        private static readonly ProfilerMarker PmZoomLines =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoomLines);
        private static readonly ProfilerMarker PmZoomLineDash =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoomLineDash);

        private readonly Line.PaintProperties  _paint;
        private readonly Line.LayoutProperties _layout;
        private readonly ZoomStyleApplier      _applier;

        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
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
            _applier   = applier;
            DrawIndex  = drawIndex;
        }

        /// <summary>
        /// Builds a line render layer from a parsed <see cref="Line.StyleLayer"/>, applying the initial
        /// zoom's uniforms. Returns <c>null</c> when the material set is unconfigured (the
        /// <see cref="Materials.MaterialFactory"/> warns and returns a null material) — the caller skips
        /// the layer, exactly as the old <c>StyledLayerSet.Build</c> did.
        /// </summary>
        public static LineRenderLayer TryCreate(
            Line.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateLineMaterial(settings);
            if (mat == null) return null;

            Line.PaintProperties  paint  = layer.Paint;
            Line.LayoutProperties layout = layer.Layout;
            var applier = new ZoomStyleApplier(mat);
            Materials.MaterialFactory.BindLinePaintToApplier(paint, applier, mat);
            // Seeded at dpr 1 — the live ratio arrives with the first ApplyZoom, before any frame draws
            // (RenderLayerSet.ApplyZoom's contract).
            applier.ApplyZoom(initialZoom, 1.0);
            return new LineRenderLayer(layer, mat, paint, layout, applier, drawIndex);
        }

        public void ApplyZoom(double zoom, double devicePixelRatio)
        {
            using (PmZoomLines.Auto())
            {
                _applier.ApplyZoom(zoom, devicePixelRatio);

                // Re-evaluate the dasharray per frame ONLY when its expression depends on zoom (the engine's
                // classification). A constant dash (the common case) is set once at bind time and skipped
                // here — no per-frame eval, no allocation.
                if (ExpressionKinds.DependsOnZoom(_paint.DashArrayKind))
                    using (PmZoomLineDash.Auto())
                        Materials.MaterialFactory.ApplyLineDashArray(_paint, Material, zoom);
            }
        }

        // job-scheduling-design.md §8 stage 5: builds the prologue's graph build — the graph's write step
        // does the mesh write. `context.BufferClip` is never read here, BY DECISION (see
        // ITileMeshRenderLayer.BuildGraphRequest): clipping the input polyline turns the join at the
        // boundary vertex into a cap, trading the seam band for a seam notch.
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
