using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

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
        /// kick; the worker-write path is spike-guarded). Reports the written <paramref name="vertexCount"/>
        /// (0 = no geometry, with <paramref name="md"/> left untouched) and the worker-computed
        /// <paramref name="bounds"/>. The caller wraps the writable array in a <see cref="MeshDataPayload"/>
        /// and applies it on the main thread.
        ///
        /// <para><paramref name="selected"/> pairs each feature with its <b>ordinal</b> in the source layer,
        /// and <paramref name="geometry"/> is the whole source layer's tile geometry, materialized once per
        /// worker pass and <b>BORROWED</b> — an implementation must not dispose it, retain it or write to it.
        /// The ordinal is how a per-feature side array joins back to the buffer's <c>RingFeatureIdx</c>. The
        /// tile extent is <c>geometry.Extent</c>; it is deliberately not a separate parameter, because a
        /// second copy is what lets a stage quietly substitute a constant.</para>
        ///
        /// <para><b>There is no <c>TileId</c> parameter</b> (B7a review N1, retired in IR C1 P2). The buffer
        /// declares its own tile as <c>geometry.Tile</c>, exactly as it declares its own extent, so a caller
        /// cannot pair a z0 buffer with a z1 address — the mispairing shape does not exist in the signature.
        /// The parameter survived B7a only because LINE still minted its own buffer and had no
        /// <paramref name="geometry"/> to read; P2 converted line and it went with the mint.</para>
        ///
        /// <para><paramref name="clip"/> is the global tile-buffer clip window. <b>Fill honours it; every
        /// other kind ignores it BY DECISION.</b> Clipping an input polyline at the tile boundary turns the
        /// join at that vertex into a cap — trading the alpha band for a notch at every seam — so the line
        /// equivalent is clipping the tessellated RIBBON, a different and harder operation that is
        /// deliberately not attempted here.</para>
        ///
        /// <para><paramref name="scratch"/> (perf/gc-elimination) is this build's rented per-build scratch —
        /// <c>null</c> for a caller with no pool to draw from (tests, direct harness calls). An implementation
        /// that has no use for it (line, today) simply ignores it.</para>
        /// </summary>
        void WriteInto(
            Mesh.MeshData md, IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
            double zoom, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
            TileBuildScratch scratch, out int vertexCount, out Bounds bounds);
    }
}
