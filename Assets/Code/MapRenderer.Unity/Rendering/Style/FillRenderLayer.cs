using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Fill <see cref="ITileMeshRenderLayer"/>: a MapLibre <c>fill</c> layer as a runtime render object.
    /// Wraps the managed <see cref="Meshing.StyledFillTileBuilder"/> (unchanged) — this is pure indirection
    /// over the existing mesh-building code, so it is behaviour-preserving.
    /// Axes (design §"Axis pinning"): <see cref="RenderLayerBuild.TileMesh"/> / <see cref="DrawPersistence.Persistent"/>.
    /// </summary>
    internal sealed class FillRenderLayer : ITileMeshRenderLayer, ISpriteConsumerRenderLayer
    {
        /// <summary>Profiler marker name constants (SSOT) for this layer's telemetry — referenced by the
        /// <see cref="ProfilerMarker"/> field below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // Nested under MapRenderer.View.ApplyZoom — the fill applier loop (expression eval → SetFloat/
            // SetColor), scales with fill-layer count. Preserved verbatim from the retired StyledLayerSet so
            // the S46 greppable-marker set is intact; it now fires once per fill layer (was once around the
            // whole fill loop).
            internal const string ApplyZoomFills = "MapRenderer.View.ApplyZoom.Fills";
        }

        private static readonly ProfilerMarker PmZoomFills =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoomFills);

        private readonly Fill.PaintProperties  _paint;
        private readonly Fill.LayoutProperties _layout;
        private readonly ZoomStyleApplier      _applier;

        // The sprite's resolved rect + base repeat count. What reaches the shader is this run through
        // Fill.FillPattern.EffectiveRepeats for the live zoom and the chosen sizing — see PushPatternScale.
        private Fill.FillPattern.Resolution _pattern;
        private bool                        _patternResolved;
        private double                      _lastZoom;

        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
        public int                               DrawIndex   { get; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public Material                          Material    { get; }

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
            // Seed the zoom the pattern scale is computed against. TryCreate drives the applier directly
            // rather than through this class's ApplyZoom, so without this _lastZoom would sit at 0 until the
            // first Tick — harmless for ScreenRelative (2^frac(0) == 1) but a whole-world tile span, and so a
            // nonsense repeat count, for WorldAbsolute on any frame that resolves a sprite before that Tick.
            _lastZoom  = initialZoom;
        }

        /// <summary>
        /// Builds a fill render layer from a parsed <see cref="Fill.StyleLayer"/>, applying the initial
        /// zoom's uniforms. Returns <c>null</c> when the material set is unconfigured (the
        /// <see cref="Materials.MaterialFactory"/> warns and returns a null material) — the caller skips
        /// the layer, exactly as the old <c>StyledLayerSet.Build</c> did.
        /// </summary>
        public static FillRenderLayer TryCreate(
            Fill.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateFillMaterial(settings);
            if (mat == null) return null;

            Fill.PaintProperties paint = layer.Paint;
            var applier = new ZoomStyleApplier(mat);
            Materials.MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(initialZoom);
            return new FillRenderLayer(layer, mat, paint, layer.Layout, applier, drawIndex, initialZoom);
        }

        public void ApplyZoom(double zoom)
        {
            using (PmZoomFills.Auto())
            {
                _applier.ApplyZoom(zoom);
                _lastZoom = zoom;
                PushPatternScale();
            }
        }

        /// <summary>
        /// Pushes <c>_PatternScale</c> — repetitions per WORLD UNIT — for the current zoom. The arithmetic
        /// and both sizing modes live in <see cref="Fill.FillPattern.RepeatsPerWorldUnit"/> so they are
        /// engine-free and unit-testable; this is only the per-frame delivery.
        ///
        /// <para>Per-frame like <c>MaterialFactory.ApplyLineDashArray</c>, and for the same reason: under
        /// <see cref="Fill.FillPatternSizing.ScreenRelative"/> the value is a function of the live zoom, not
        /// of the style, so it cannot be bound once. (Under <c>WorldAbsolute</c> it is zoom-independent and
        /// the repeated push is a harmless no-op.)</para>
        ///
        /// <para>No tile enters this calculation. The mesh's stream 1 carries world units rather than a 0..1
        /// tile fraction (<c>StyledFillTileBuilder.PatternCoord</c>), so a tile's own zoom — which differs
        /// from the display zoom under overzoom and under mixed-zoom cover, and which a per-layer uniform
        /// cannot see — no longer enters the pattern's size at all.</para>
        /// </summary>
        private void PushPatternScale()
        {
            if (!_patternResolved) return;

            // Sizing comes from the parsed style (Fill.PaintProperties), not from a settable knob here —
            // the style is the single source of truth, and a set-only-by-tests property would be production
            // code with no production caller.
            double2 repeats = Fill.FillPattern.RepeatsPerWorldUnit(
                _pattern, _paint.PatternSizing, _lastZoom, _paint.PatternWorldPeriodMetres);

            // Unity boundary cast: double2 → Vector4 at the SetVector call site, never upstream.
            Material.SetVector(ShaderProperties.Fill.PropertyId.PatternScale,
                new Vector4((float)repeats.x, (float)repeats.y, 0f, 0f));
        }

        /// <summary>
        /// Resolves this layer's <c>fill-pattern</c> against the style's sprite sheet. A layer with no
        /// <c>fill-pattern</c> ignores the call entirely — its uniforms are already the solid-fill identity.
        ///
        /// <para>A pattern layer that cannot resolve (no sheet yet, or a name absent from the sheet) binds a
        /// ZERO-AREA <c>_PatternRect</c>, which the shader reads as "clip". That is the whole fix for the
        /// black regions: per the Style Spec such a layer is not painted, and in particular does NOT fall
        /// back to <c>fill-color</c>, whose spec default is opaque black — which is what these layers used to
        /// render as, since a <c>fill-pattern</c> layer characteristically declares no <c>fill-color</c>.</para>
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

            // The scale is NOT written straight through: it is the base repeat count at the tile's own zoom,
            // and PushPatternScale applies the live fractional-zoom factor on top. Pushed here as well as in
            // ApplyZoom because the sheet lands mid-frame AFTER MapView has already called ApplyZoom, so
            // waiting would leave one frame at the unscaled repeat count.
            _pattern         = pattern;
            _patternResolved = true;
            PushPatternScale();
        }

        public void WriteInto(
            Mesh.MeshData md, IReadOnlyList<ITileFeature> features, double zoom, double extent,
            TileId id, double3 tileOriginRender, IProjection projection, out int vertexCount, out Bounds bounds)
            => Meshing.StyledFillTileBuilder.WriteMeshData(
                md, features, _paint, zoom, extent, id, tileOriginRender, out vertexCount, out bounds,
                projection, _layout);

        public void Dispose() => RenderLayerSet.DestroyMaterialInstance(Material);
    }
}
