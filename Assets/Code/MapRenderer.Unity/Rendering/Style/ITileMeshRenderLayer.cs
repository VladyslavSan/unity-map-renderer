using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The <see cref="RenderLayerBuild.TileMesh"/> capability — built once per <c>(tile, layer)</c> by the
    /// Burst mesh pipeline (job-scheduling-design.md) and registered with a
    /// <see cref="Backend.ITileRenderBackend"/>. Symbols/text are NOT this capability — per ARCHITECTURE
    /// they are a separate, placed-every-frame path, not a built-mesh static layer.
    /// </summary>
    internal interface ITileMeshRenderLayer : IRenderLayer
    {
        /// <summary>
        /// Builds this layer's graph build off the already-selected features — the prologue half of the
        /// mesh build; the graph's write step does the rest. Returns <c>null</c> when there is nothing to
        /// build for this layer; otherwise a rented <see cref="ILayerMeshBuild"/> of the layer's own kind
        /// (<see cref="FillLayerBuild"/>/<see cref="FillExtrusionLayerBuild"/>/<see cref="LineLayerBuild"/>)
        /// carrying exactly the columns its kind needs.
        ///
        /// <para><paramref name="selected"/> pairs each feature with its <b>ordinal</b> in the source layer,
        /// and <paramref name="geometry"/> is the whole source layer's tile geometry, materialized once per
        /// worker pass and <b>BORROWED</b> — an implementation must not dispose it, retain it or write to it.
        /// The ordinal is how a per-feature side array joins back to the buffer's <c>RingFeatureIdx</c>. The
        /// tile extent is <c>geometry.Extent</c>; it is not a separate parameter, because a second copy is
        /// what lets a stage quietly substitute a constant.</para>
        ///
        /// <para><b>There is no <c>TileId</c> parameter.</b> The buffer
        /// declares its own tile as <c>geometry.Tile</c>, exactly as it declares its own extent, so a caller
        /// cannot pair a z0 buffer with a z1 address — the mispairing shape does not exist in the signature.</para>
        ///
        /// <para><paramref name="context"/>'s <c>BufferClip</c> is the global tile-buffer clip window.
        /// <b>Fill honours it; every other kind ignores it BY DECISION.</b> Clipping an input polyline at the
        /// tile boundary turns the join at that vertex into a cap — trading the alpha band for a notch at
        /// every seam — so the line equivalent is clipping the tessellated RIBBON, a different and harder
        /// operation that is not attempted here.</para>
        /// </summary>
        /// <param name="selected">This layer's already-selected features, paired with their ordinal in the
        /// source layer.</param>
        /// <param name="geometry">The whole source layer's tile geometry — BORROWED, never disposed or
        /// retained here.</param>
        /// <param name="context">The shared per-kick worker-pass inputs (zoom, origin, projection, clip,
        /// scratch).</param>
        /// <param name="materialIndex">This layer's material slot — threaded into the build's <c>Rent</c>.</param>
        /// <param name="payloadName">This layer's fallback mesh name — threaded into the build's <c>Rent</c>.</param>
        ILayerMeshBuild BuildGraphRequest(
            IReadOnlyList<SelectedTileFeature> selected,
            TileGeometryBuffers                geometry,
            in TileLayerProcessContext         context,
            int                                materialIndex,
            string                             payloadName);
    }
}
