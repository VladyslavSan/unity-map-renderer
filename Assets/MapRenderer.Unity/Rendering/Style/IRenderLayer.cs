using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// One runtime render object per declared, renderable style layer — the "static-geometry" class
    /// (fill, line, and later fill-extrusion). Owns its GPU <see cref="Material"/>, references its parsed
    /// <see cref="MapRenderer.Core.Style.StyleLayer"/> (source / filter / source-layer, for feature
    /// selection), pushes its zoom-dependent uniforms each frame, and builds the mesh from its selected
    /// features into a per-tile payload.
    ///
    /// <para>Replaces the fill-vs-line split (the retired <c>StyledLayerSet._fills</c>/<c>_lines</c>):
    /// render layers now live in ONE ordered <see cref="RenderLayerSet"/> where
    /// <c>index == draw order == material index</c> (ARCHITECTURE §"Layer ordering" — "the style is an
    /// ordered list of layers"). Adding a static layer type = one <see cref="IRenderLayer"/> implementation
    /// + one <see cref="RenderLayerFactory"/> arm; the layer set, the backends, and the tile consume loop
    /// are untouched.</para>
    ///
    /// <para>Symbols/text are deliberately out of scope — per ARCHITECTURE they are a separate,
    /// placed-every-frame path, not a built-mesh static layer.</para>
    /// </summary>
    internal interface IRenderLayer : IDisposable
    {
        /// <summary>The parsed style data (source, filter, source-layer, id) this layer renders from.</summary>
        StyleLayer StyleLayer { get; }

        /// <summary>The per-layer GPU <see cref="Material"/> instance; its <c>renderQueue</c> encodes the
        /// declared draw order. Owned by this layer (destroyed on <see cref="IDisposable.Dispose"/>),
        /// referenced (not owned) by the render backend.</summary>
        Material Material { get; }

        /// <summary>Push this layer's zoom-dependent uniforms for the frame. Called once per layer per frame
        /// from <see cref="RenderLayerSet.ApplyZoom"/> (the alloc-free hot path). Line width is resolved in
        /// screen space (S104) — no metersPerPixel is threaded through any more.</summary>
        void ApplyZoom(double zoom);

        /// <summary>
        /// Off-main-thread: build the mesh from this layer's <b>already-selected</b> features straight into
        /// <paramref name="md"/> — a caller-allocated <c>Mesh.MeshData</c> (allocated on the main thread at
        /// kick; the worker-write path is spike-guarded). <paramref name="extent"/> is the resolved MVT layer
        /// extent (tile units). Reports the written <paramref name="vertexCount"/> (0 = no geometry, with
        /// <paramref name="md"/> left untouched) and the worker-computed <paramref name="bounds"/>. The caller
        /// wraps the writable array in a <see cref="MeshDataPayload"/> and applies it on the main thread.
        /// </summary>
        void WriteInto(
            Mesh.MeshData md, IReadOnlyList<MvtFeature> features, double zoom, double extent,
            TileId id, double3 tileOriginRender, IProjection projection, out int vertexCount, out Bounds bounds);
    }

    /// <summary>
    /// The transitional per-<c>(tile, layer)</c> mesh payload — the single uniform handle the tile
    /// consume loop uploads and disposes, regardless of the producing layer's type. This is what collapses
    /// the two-array <c>MeshBuildResult</c> (fills lane + lines lane) into one ordered payload array.
    ///
    /// <para><b>Reference type on purpose:</b> the concrete handles wrap a NativeArray-backed
    /// <c>struct</c> whose <c>Dispose</c> flips its own <c>IsCreated</c> flag. A boxed struct behind an
    /// interface would be copied, so the flag flip would be lost and a second dispose would double-free /
    /// leak. A class wrapper holds the struct in a mutable field, so <see cref="IDisposable.Dispose"/>
    /// mutates it in place. (Allocated at mesh-build time only — load-time, never the steady-state
    /// per-frame path — so the class allocation is not a no-GC concern.)</para>
    ///
    /// <para>Stage B replaces the innards with <c>Mesh.MeshData</c>; the consume-loop contract
    /// (<see cref="VertexCount"/> + <see cref="Upload"/> + <see cref="IDisposable.Dispose"/>) is unchanged,
    /// so B swaps the handle's implementation without touching its callers.</para>
    /// </summary>
    internal interface IRenderLayerPayload : IDisposable
    {
        /// <summary>Vertex count of the produced geometry — charged against the S87 per-frame vertex budget.</summary>
        int VertexCount { get; }

        /// <summary>The render layer's global draw-order index (== material index). S89 Stage C: the payload
        /// carries its own index so a dense per-<c>(tile, source)</c> result no longer relies on
        /// <c>cursor == materialIndex</c> (which only held for a full-width sparse union).</summary>
        int MaterialIndex { get; }

        /// <summary>Main-thread: upload the payload into a fresh <see cref="Mesh"/>. Returns <c>null</c> when
        /// the payload is empty (defensive — the producer returns null rather than an empty handle).</summary>
        Mesh Upload();
    }
}
