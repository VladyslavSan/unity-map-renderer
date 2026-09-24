using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Fill <see cref="ITileMeshRenderLayer"/>: a MapLibre <c>fill</c> layer as a runtime render object.
    /// Wraps <see cref="Meshing.StyledFillTileBuilder"/>.
    /// Axes (design "Axis pinning"): <see cref="RenderLayerBuild.TileMesh"/> / <see cref="DrawPersistence.Persistent"/>.
    /// </summary>
    internal sealed class FillRenderLayer : ITileMeshRenderLayer, ISpriteConsumerRenderLayer, IFadeableRenderLayer
    {
        /// <summary>Profiler marker name constants (SSOT) for this layer's telemetry — referenced by the
        /// <see cref="ProfilerMarker"/> field below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // Nested under MapRenderer.View.ApplyZoom — the fill applier loop (expression eval → SetFloat/
            // SetColor), scales with fill-layer count. Fires once per fill layer.
            internal const string ApplyZoomFills = "MapRenderer.View.ApplyZoom.Fills";
        }

        private static readonly ProfilerMarker PmZoomFills =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoomFills);

        private Fill.PaintProperties  _paint;

        private Fill.LayoutProperties _layout;
        private readonly ZoomStyleApplier      _applier;

        // The sprite's resolved rect + logical size. What reaches the shader is this run through
        // Fill.FillPattern.RepeatsPerWorldUnit for the live zoom and the chosen sizing — see PushPatternScale.
        private Fill.FillPattern.Resolution _pattern;
        private bool                        _patternResolved;
        private double                      _lastZoom;

        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; private set; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
        public int                               DrawIndex   { get; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public ShadowCastingMode                 CastShadows => ShadowCastingMode.Off;
        public Material                          Material    { get; }
        public int                               TransitioningCount => _applier.TransitioningCount;

        private FillRenderLayer(
            Fill.StyleLayer layer, Material material, Fill.PaintProperties paint,
            Fill.LayoutProperties layout, ZoomStyleApplier applier, int drawIndex, double initialZoom)
        {
            StyleLayer = layer;
            Material   = material;
            _paint     = paint;
            _layout    = layout;
            _applier   = applier;
            DrawIndex  = drawIndex;
            // Seed the pattern-scale zoom: TryCreate bypasses ApplyZoom, and a zoom of 0 gives WorldAbsolute a
            // whole-world repeat count for a sprite that resolves before the first Tick.
            _lastZoom  = initialZoom;
        }

        /// <summary>
        /// Builds a fill render layer from a parsed <see cref="Fill.StyleLayer"/>, applying the initial
        /// zoom's uniforms. Returns <c>null</c> when the material set is unconfigured (the
        /// <see cref="Materials.MaterialFactory"/> warns and returns a null material) — the caller skips
        /// the layer: <see cref="RenderLayerSet.Build"/> records it as <c>LayerSkipReason.MaterialUnconfigured</c>.
        /// </summary>
        public static FillRenderLayer TryCreate(
            Fill.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateFillMaterial(settings);
            if (mat == null) return null;

            // Read ONCE, handed to both carriers (BindFillPaintToApplier here, _paint for the mesh bake) —
            // one DependsOnFeature reading makes the two guards exact complements: never both, never neither.
            Fill.PaintProperties paint = layer.Paint;
            var applier = new ZoomStyleApplier(mat);
            // BEFORE the paint bind: a Constant opacity is pushed once at bind time and then skipped
            // forever, so an unscaled bind-time push would make a seeded fade of 0 invisible.
            applier.SeedFade(layer.IsVisibleAtZoom(initialZoom) ? 1f : 0f);
            Materials.MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            // Seeded at dpr 1 — the live ratio arrives with the first ApplyZoom, before any frame draws
            // (RenderLayerSet.ApplyZoom's contract).
            applier.ApplyZoom(new StyleFrameInputs(initialZoom, 1.0, 0.0));
            return new FillRenderLayer(layer, mat, paint, layer.Layout, applier, drawIndex, initialZoom);
        }

        /// <inheritdoc cref="IFadeableRenderLayer.FadesGradually"/>
        public bool FadesGradually => true;

        /// <inheritdoc cref="IFadeableRenderLayer.SetFade"/>
        public void SetFade(float amount) => _applier.SetFade(amount);

        /// <inheritdoc cref="IFadeableRenderLayer.PaintsSomething"/>
        public bool PaintsSomething => !_applier.EffectiveOpacityIsZero;

        public void ApplyZoom(in StyleFrameInputs inputs)
        {
            using (PmZoomFills.Auto())
            {
                _applier.ApplyZoom(inputs);
                _lastZoom = inputs.Zoom;
                PushPatternScale();
            }
        }

        /// <summary>
        /// Re-targets this layer's uniform bindings at <paramref name="layer"/> — the survivor gate has
        /// already proven its mesh-affecting content unchanged, so only the paint/layout carriers and the
        /// applier's bindings need to move.
        /// </summary>
        public void Restyle(MapRenderer.Core.Style.StyleLayer layer, in StyleTransition transition, double nowSeconds)
        {
            var typed = (Fill.StyleLayer)layer;
            StyleLayer = typed;
            _paint     = typed.Paint;
            _layout    = typed.Layout;
            _applier.SetTransition(transition, nowSeconds);
            Materials.MaterialFactory.BindFillPaintToApplier(_paint, _applier, Material);
        }

        /// <inheritdoc cref="IRenderLayer.SetDrawOrder"/>
        public void SetDrawOrder(int declaredOrder)
        {
            if (Material != null)
                Material.renderQueue = LayerDrawOrder.QueueFor(declaredOrder, MaterialSubSlot);
        }

        /// <summary>
        /// Pushes <c>_PatternScale</c> — repetitions per WORLD UNIT — for the current zoom; the arithmetic lives
        /// in <c>Fill.FillPattern.RepeatsPerWorldUnit</c>. It runs per frame because under
        /// <see cref="Fill.FillPatternSizing.ScreenRelative"/> the value follows the live zoom. No tile zoom
        /// enters it: stream 1 carries world units (<c>StyledFillTileBuilder.PatternCoord</c>), not a tile fraction.
        /// </summary>
        private void PushPatternScale()
        {
            if (!_patternResolved) return;

            // Sizing comes from the parsed style (Fill.PaintProperties), the single source of truth.
            double2 repeats = Fill.FillPattern.RepeatsPerWorldUnit(
                _pattern, _paint.PatternSizing, _lastZoom, _paint.PatternWorldPeriodMetres);

            // Unity boundary cast: double2 → Vector4 at the SetVector call site, never upstream.
            Material.SetVector(ShaderProperties.Fill.PropertyId.PatternScale,
                new Vector4((float)repeats.x, (float)repeats.y, 0f, 0f));
        }

        /// <summary>
        /// Resolves this layer's <c>fill-pattern</c> against the style's sprite sheet. A layer with no
        /// <c>fill-pattern</c> ignores the call. An unresolved pattern (no sheet yet, or an absent name) binds
        /// a ZERO-AREA <c>_PatternRect</c>, which the shader clips. Per the Style Spec the layer is then not
        /// painted; a fallback to <c>fill-color</c> would paint it the spec default, opaque black.
        /// </summary>
        public void SetSprites(SpriteAtlasView atlas, Texture2D texture)
        {
            if (_paint.PatternName == null) return;

            Material.SetTexture(ShaderProperties.Fill.TexturePropertyId.PatternMap, texture);

            // A resolvable name with no texture to sample is still unresolved — both halves are needed.
            if (texture == null || !Fill.FillPattern.TryResolve(_paint.PatternName, atlas, out var pattern))
            {
                _patternResolved = false;
                Material.SetVector(ShaderProperties.Fill.PropertyId.PatternRect, Vector4.zero);
                return;
            }

            // Unity boundary cast: double4/double2 → Vector4 at the SetVector call site, never upstream.
            Material.SetVector(ShaderProperties.Fill.PropertyId.PatternRect,
                new Vector4((float)pattern.Rect.x, (float)pattern.Rect.y,
                            (float)pattern.Rect.z, (float)pattern.Rect.w));

            // Pushed here as well as in ApplyZoom: the sheet lands AFTER MapView's ApplyZoom for this frame,
            // so waiting would leave one frame at the unscaled repeat count.
            _pattern         = pattern;
            _patternResolved = true;
            PushPatternScale();
        }

        // The graph is the only mesher — rents the build the graph's write step consumes; FillMeshGraph
        // does the mesh write.
        public Meshing.ILayerMeshBuild BuildGraphRequest(
            IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
            in TileLayerProcessContext context, int materialIndex, string payloadName)
        {
            FillMeshPipeline.LayerInput input = Meshing.StyledFillTileBuilder.BuildLayerInput(
                selected, geometry, _paint, context.Zoom, context.TileOriginRender, out var colors,
                context.Projection, _layout, context.BufferClip, context.Buffers);
            // The emptiness gate. Every BuildLayerInput empty path returns `default` with every `out`
            // column left `default`, so the null path has nothing to dispose.
            if (!input.RingVisitOrder.IsCreated) return null;
            return Meshing.FillLayerBuild.Rent(input, colors, materialIndex, payloadName);
        }

        public void Dispose() => RenderLayerSet.DestroyMaterialInstance(Material);
    }
}
