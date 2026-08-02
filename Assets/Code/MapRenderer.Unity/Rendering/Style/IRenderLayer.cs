using System;
using UnityEngine;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// One runtime render object per declared, renderable style layer — the uniform base over EVERY
    /// painted layer kind (fill, line, symbol, background, and later fill-extrusion/raster), not just the
    /// static-geometry ones (the render-layer model). Every layer declares
    /// its lifetime class (<see cref="Build"/>), who re-draws it each render (<see cref="Persistence"/>),
    /// and its global slot in the painter's chain (<see cref="DrawIndex"/>) — three orthogonal axes, never
    /// collapsed. The static-geometry contract (mesh-from-features build) now lives on the capability
    /// interface <see cref="ITileMeshRenderLayer"/>, not here.
    ///
    /// <para>Replaces the fill-vs-line split (the retired <c>StyledLayerSet._fills</c>/<c>_lines</c>):
    /// render layers now live in ONE ordered <see cref="RenderLayerSet"/> where
    /// <c>index == draw order == material index</c> (ARCHITECTURE §"Layer ordering" — "the style is an
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

        /// <summary>The global draw slot, set once by <see cref="RenderLayerSet.Build"/>; immutable
        /// afterwards. This layer's queue band is <c>LayerDrawOrder.QueueFor(DrawIndex, subSlot)</c> for each
        /// sub-slot it uses (G7/D7) — NOT a bare <c>TransparentQueue + DrawIndex</c> any more.</summary>
        int DrawIndex { get; }

        /// <summary>Which sub-slot of this layer's queue band (see <see cref="DrawIndex"/>) its primary
        /// <see cref="Material"/> occupies — <see cref="LayerSubSlot.Base"/> for a single-material layer
        /// (fill, line, background), <see cref="LayerSubSlot.Above"/> for a layer whose <see cref="Material"/>
        /// must draw over another material it owns (the symbol layer's text, over its own icon).</summary>
        LayerSubSlot MaterialSubSlot { get; }

        /// <summary>The per-layer GPU <see cref="Material"/> instance; its <c>renderQueue</c> encodes the
        /// declared draw order. Owned by this layer (destroyed on <see cref="IDisposable.Dispose"/>),
        /// referenced (not owned) by the render backend. May be <c>null</c> when that slot's own base
        /// material is unconfigured (<see cref="Materials.MapMaterialSet"/>) — a null-material layer still
        /// takes a draw slot, but <see cref="RenderLayerSet.Build"/> skips its queue write and no backend
        /// ever receives an <c>AddTileLayer</c> for it.</summary>
        Material Material { get; }

        /// <summary>Push this layer's per-frame uniforms. Called once per layer per frame from
        /// <see cref="RenderLayerSet.ApplyZoom"/> (the alloc-free hot path). No ground resolution is threaded
        /// through: the shader converts a px-valued width with the <c>_MapFrameMetersPerDevicePixel</c> global
        /// that <see cref="Map.MapCamera.SyncToCamera"/> measures off the camera (S116). That global is metres
        /// per DEVICE pixel, so <paramref name="devicePixelRatio"/> is what converts the style's logical px
        /// into the same basis here (S107; see <see cref="ZoomStyleApplier.BindDevicePixelFloat"/>) — the two
        /// halves have to agree or every px width is off by exactly the ratio.</summary>
        /// <param name="zoom">The current map zoom level.</param>
        /// <param name="devicePixelRatio">Physical ÷ logical px for this frame's panel.</param>
        void ApplyZoom(double zoom, double devicePixelRatio);
    }
}
