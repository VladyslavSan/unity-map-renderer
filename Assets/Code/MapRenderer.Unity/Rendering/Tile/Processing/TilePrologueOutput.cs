using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// job-scheduling-design.md §8 stage 3: <see cref="TileLayerProcessorRunner.RunWorkerPass"/>'s return —
    /// the prologue's hand-off into <see cref="TileBuildGraph.ScheduleMeasureFromDecode"/>. Named in the
    /// <see cref="FillGraphOutput"/> /
    /// <see cref="Meshing.MeshWriteOutput"/> family — a per-step output struct returned uncompleted-or-owned
    /// to a single caller.
    /// </summary>
    internal struct TilePrologueOutput
    {
        /// <summary>Dense over the kick's <c>ITileMeshRenderLayer</c>s, in SLOT order: a rented
        /// <see cref="ILayerMeshBuild"/> for a layer with graph work, and <c>null</c> for a layer that
        /// produced nothing (an unsupported phase, or a processor never reached after an earlier one
        /// faulted).</summary>
        public ILayerMeshBuild[] Layers;

        /// <summary>The kick's OWN decode reference — set by <c>TileManager.KickMeshBuild</c>, never by the
        /// runner. Released exactly once: by <see cref="TileBuildGraph.Dispose"/> once handed to
        /// <c>ScheduleMeasureFromDecode</c>, or by THIS <see cref="Dispose"/> from a pen drain when it never
        /// got that far (a fault before scheduling, or a released-before-scheduled tile).</summary>
        public SharedDisposable<IDecodedTile> Decode;

        /// <summary>Frees every layer's own build (null-tolerant) and releases <see cref="Decode"/> — the
        /// pen path for a prologue that completed but was never handed to
        /// <see cref="TileBuildGraph.ScheduleMeasureFromDecode"/>.</summary>
        internal void Dispose()
        {
            if (Layers != null)
                for (int i = 0; i < Layers.Length; i++)
                    Layers[i]?.Dispose();
            Decode?.Release();
        }
    }
}
