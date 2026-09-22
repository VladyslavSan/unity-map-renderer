using System;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// One runtime render object per declared, renderable style layer — the uniform base over every
    /// painted layer kind (fill, line, symbol, background, fill-extrusion). Each layer declares its
    /// lifetime class (<see cref="Build"/>), who re-draws it each render (<see cref="Persistence"/>), its
    /// global SLOT (<see cref="DrawIndex"/>), and whether it casts shadows (<see cref="CastShadows"/>). The
    /// static-geometry contract lives on <see cref="ITileMeshRenderLayer"/>, not here.
    /// </summary>
    internal interface IRenderLayer : IDisposable
    {
        /// <summary>The parsed style data (source, filter, source-layer, id) this layer renders from.</summary>
        StyleLayer StyleLayer { get; }

        /// <summary>The lifetime class — which loop feeds this layer's geometry.</summary>
        RenderLayerBuild Build { get; }

        /// <summary>Who re-draws this layer each camera render.</summary>
        DrawPersistence Persistence { get; }

        /// <summary>This layer's <b>slot</b> — the backend <c>materialIndex</c>, the
        /// <c>LoadedTile.MaterialIndices</c> entry, and <c>PreparedKey</c>'s layer id. Set once by
        /// <see cref="RenderLayerSet.Build"/> and <b>stable across a restyle</b> for a surviving layer
        /// (<see cref="RenderLayerSet.TryRestyleInPlace"/>). No longer the queue input — see
        /// <see cref="SetDrawOrder"/> for that.</summary>
        int DrawIndex { get; }

        /// <summary>Whether this layer's geometry is drawn into the shadow map. A per-render-KIND decision,
        /// not a style property: only <c>fill-extrusion</c> returns <see cref="ShadowCastingMode.On"/> —
        /// ground-draped kinds (fill, line, background) are coplanar with the surface they would shadow
        /// (an acne source, no benefit), and symbols are camera-facing billboards whose shadow would be a
        /// floating dark quad. Receiving is unconditional for tile geometry, separate from this axis.
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
        /// referenced (not owned) by the render backend. <c>null</c> when that slot's own base material is
        /// unconfigured (<see cref="Materials.MapMaterialSet"/>) — <see cref="RenderLayerSet.Build"/> then
        /// skips its queue write, and no backend ever receives an <c>AddTileLayer</c> for it.</summary>
        Material Material { get; }

        /// <summary>Push this layer's per-frame uniforms. Called once per layer per frame from
        /// <see cref="RenderLayerSet.ApplyZoom"/> (the alloc-free hot path). Non-local invariant: the shader
        /// converts a px-valued width using the <c>_MapFrameMetersPerDevicePixel</c> global
        /// (<see cref="Map.MapCamera.SyncToCamera"/>, metres per DEVICE pixel), so <paramref name="inputs"/>'
        /// device-pixel ratio must agree with it, or every px width is off by that ratio.</summary>
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
