using System;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// One runtime render object per declared, renderable style layer — the uniform base over EVERY
    /// painted layer kind (fill, line, symbol, background, and later fill-extrusion/raster), not just the
    /// static-geometry ones (the render-layer model). Every layer declares
    /// its lifetime class (<see cref="Build"/>), who re-draws it each render (<see cref="Persistence"/>),
    /// its global SLOT in the layer set (<see cref="DrawIndex"/>), and whether it casts shadows
    /// (<see cref="CastShadows"/>) — four orthogonal axes, never collapsed. The static-geometry contract
    /// (mesh-from-features build) now lives on the capability interface
    /// <see cref="ITileMeshRenderLayer"/>, not here.
    ///
    /// <para>Replaces the fill-vs-line split (the retired <c>StyledLayerSet._fills</c>/<c>_lines</c>):
    /// render layers now live in ONE <see cref="RenderLayerSet"/> where <c>index == SLOT == material
    /// index</c> and draw order rides <c>renderQueue</c> (ARCHITECTURE §"Layer ordering" — "the style is an
    /// ordered list of layers"). Adding a layer type = one <see cref="IRenderLayer"/> implementation + one
    /// <see cref="RenderLayerFactory"/> arm; the layer set, the backends, and the tile consume loop are
    /// untouched.</para>
    /// </summary>
    internal interface IRenderLayer : IDisposable
    {
        /// <summary>The parsed style data (source, filter, source-layer, id) this layer renders from.</summary>
        StyleLayer StyleLayer { get; }

        /// <summary>The lifetime class — which loop feeds this layer's geometry (D6).</summary>
        RenderLayerBuild Build { get; }

        /// <summary>Who re-draws this layer each camera render (D6).</summary>
        DrawPersistence Persistence { get; }

        /// <summary>This layer's <b>slot</b> — the backend <c>materialIndex</c>, the
        /// <c>LoadedTile.MaterialIndices</c> entry, and <c>PreparedKey</c>'s layer id. Set once by
        /// <see cref="RenderLayerSet.Build"/> and <b>stable across a restyle</b> for a surviving layer
        /// (<see cref="RenderLayerSet.TryRestyleInPlace"/>). No longer the queue input — see
        /// <see cref="SetDrawOrder"/> for that.</summary>
        int DrawIndex { get; }

        /// <summary>Whether this layer's geometry is drawn into the shadow map. A per-render-KIND decision,
        /// not a style property: only <c>fill-extrusion</c> returns <see cref="ShadowCastingMode.On"/>,
        /// because ground-draped kinds (fill, line, background) are coplanar with the surface they would
        /// shadow — casting there is an acne source and buys nothing — and symbols are camera-facing
        /// billboards whose cast shadow would be a floating dark quad. Receiving is unconditional for tile
        /// geometry and is not part of this axis.
        ///
        /// <para>Non-local invariant: <see cref="Backend.ITileRenderBackend"/>'s three implementations
        /// TRANSPORT this value verbatim, indexed by <see cref="DrawIndex"/>, and must never re-derive it
        /// from the layer type or the material — that is what keeps the three backends from drifting
        /// apart.</para></summary>
        ShadowCastingMode CastShadows { get; }

        /// <summary>Which sub-slot of this layer's queue band (see <see cref="SetDrawOrder"/>) its primary
        /// <see cref="Material"/> occupies — <see cref="LayerSubSlot.Base"/> for a single-material layer
        /// (fill, line, background), <see cref="LayerSubSlot.Above"/> for a layer whose <see cref="Material"/>
        /// must draw over another material it owns (the symbol layer's text, over its own icon).</summary>
        LayerSubSlot MaterialSubSlot { get; }

        /// <summary>The per-layer GPU <see cref="Material"/> instance; its <c>renderQueue</c> encodes the
        /// declared draw order. Owned by this layer (destroyed on <see cref="IDisposable.Dispose"/>),
        /// referenced (not owned) by the render backend. May be <c>null</c> when that slot's own base
        /// material is unconfigured (<see cref="Materials.MapMaterialSet"/>) — a null-material layer still
        /// takes a slot, but <see cref="RenderLayerSet.Build"/> skips its queue write and no backend
        /// ever receives an <c>AddTileLayer</c> for it.</summary>
        Material Material { get; }

        /// <summary>Push this layer's per-frame uniforms. Called once per layer per frame from
        /// <see cref="RenderLayerSet.ApplyZoom"/> (the alloc-free hot path). No ground resolution is threaded
        /// through: the shader converts a px-valued width with the <c>_MapFrameMetersPerDevicePixel</c> global
        /// that <see cref="Map.MapCamera.SyncToCamera"/> measures off the camera (S116). That global is metres
        /// per DEVICE pixel, so <paramref name="inputs"/>' device-pixel ratio is what converts the style's
        /// logical px into the same basis here (S107; see <see cref="ZoomStyleApplier.BindDevicePixelFloat"/>)
        /// — the two halves have to agree or every px width is off by exactly the ratio.</summary>
        /// <param name="inputs">The live zoom, device-pixel ratio, and wall clock for this frame.</param>
        void ApplyZoom(in StyleFrameInputs inputs);

        /// <summary>How many of this layer's uniform bindings are currently easing (0 when settled, or
        /// when this layer's applier is null because its base material is unconfigured). No production
        /// consumer — it stays on the interface because the cheaper seam (a test-assembly extension
        /// summing each applier's own count) would need every layer to expose its private applier, a
        /// bigger production footprint than this one <c>int</c>.</summary>
        int TransitioningCount { get; }

        /// <summary>
        /// Re-target this layer's uniform bindings at <paramref name="layer"/> — a new style layer whose
        /// mesh-affecting content the caller has already proven identical to this one's.
        /// </summary>
        /// <param name="layer">The new style's layer at the same index (<see cref="SurvivingLayerGate"/>).</param>
        /// <param name="transition">The duration/delay to ease newly-differing uniforms over.</param>
        /// <param name="nowSeconds">The restyle's wall-clock instant (armed-at, before any delay).</param>
        void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds);

        /// <summary>Stamps this layer's queue band from <paramref name="declaredOrder"/> — its position in
        /// the CURRENT document's <c>layers</c> array, which may differ from <see cref="DrawIndex"/> (the
        /// slot) after a partial-survival reorder. A no-op when <see cref="Material"/> is null (that slot's
        /// own base material is unconfigured).</summary>
        /// <param name="declaredOrder">This layer's index in the new document's declared layer order.</param>
        void SetDrawOrder(int declaredOrder);
    }
}
