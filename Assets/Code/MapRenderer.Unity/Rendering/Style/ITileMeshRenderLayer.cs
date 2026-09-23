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
        /// build for this layer; otherwise a rented <see cref="ILayerMeshBuild"/> of the layer's own kind,
        /// carrying exactly the columns its kind needs. <paramref name="geometry"/> is the whole source
        /// layer's tile geometry, BORROWED — an implementation must not dispose it, retain it or write to
        /// it. There is no <c>TileId</c> or extent parameter: the buffer declares its own <c>geometry.Tile</c>
        /// and <c>geometry.Extent</c>, so a caller cannot pair a z0 buffer with a z1 address, and a stage
        /// cannot substitute a constant extent. The ordinal in <paramref name="selected"/> joins a per-feature
        /// side array back to the buffer's <c>RingFeatureIdx</c>. Non-local invariant:
        /// <paramref name="context"/>'s <c>BufferClip</c> is the global tile-buffer clip window, and only
        /// Fill honours it — clipping an input polyline at the tile boundary turns a join into a cap, so
        /// the line equivalent (clipping the tessellated ribbon) is a different, harder operation not
        /// attempted here.
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
