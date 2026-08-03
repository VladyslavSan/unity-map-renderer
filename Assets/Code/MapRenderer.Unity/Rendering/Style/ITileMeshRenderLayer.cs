using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The <see cref="RenderLayerBuild.TileMesh"/> capability — built once per <c>(tile, layer)</c> by the
    /// Burst mesh pipeline and registered with a <see cref="Backend.ITileRenderBackend"/>. Today's fill/line
    /// <c>WriteInto</c>, hoisted out of the base interface verbatim (design §3.2); later fill-extrusion and
    /// raster implement this too. Symbols/text are deliberately NOT this capability — per ARCHITECTURE they
    /// are a separate, placed-every-frame path, not a built-mesh static layer.
    /// </summary>
    internal interface ITileMeshRenderLayer : IRenderLayer
    {
        /// <summary>
        /// Off-main-thread: build the mesh from this layer's <b>already-selected</b> features straight into
        /// <paramref name="md"/> — a caller-allocated <c>Mesh.MeshData</c> (allocated on the main thread at
        /// kick; the worker-write path is spike-guarded). <paramref name="extent"/> is the resolved tile
        /// layer extent (tile units). Reports the written <paramref name="vertexCount"/> (0 = no geometry, with
        /// <paramref name="md"/> left untouched) and the worker-computed <paramref name="bounds"/>. The caller
        /// wraps the writable array in a <see cref="MeshDataPayload"/> and applies it on the main thread.
        ///
        /// <para><paramref name="clip"/> is the global tile-buffer clip window. <b>Fill honours it; every
        /// other kind ignores it BY DECISION.</b> Clipping an input polyline at the tile boundary turns the
        /// join at that vertex into a cap — trading the alpha band for a notch at every seam — so the line
        /// equivalent is clipping the tessellated RIBBON, a different and harder operation that is
        /// deliberately not attempted here.</para>
        /// </summary>
        void WriteInto(
            Mesh.MeshData md, IReadOnlyList<ITileFeature> features, double zoom, double extent,
            TileId id, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
            out int vertexCount, out Bounds bounds);
    }
}
