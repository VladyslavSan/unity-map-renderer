// Unity EditMode and PlayMode: test-assembly reads over TileManager's internal state.
//
// Non-obvious why: C# finds extension methods only through the call site's enclosing namespaces, so this file uses the
// parent namespace MapRenderer.Tests, which every test namespace can see. Each member answers per RECORD tile, not per
// cover tile: under overzoom one record serves several cover tiles.

using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests
{
    internal static class TileManagerTestExtensions
    {
        /// <summary>The in-flight fetch count summed across every source pipeline.</summary>
        internal static int InFlightCount(this TileManager manager) => manager._sources.TotalInFlight;

        /// <summary>Pipelines that own a feature source, excluding the synthetic background pipeline. Zero is the only positive
        /// signal that a source was skipped: not throwing, fetching or rendering all look the same otherwise.</summary>
        internal static int WiredFeatureSourceCount(this TileManager manager) => manager._sources.RealSourceCount;

        /// <summary>Loaded or loading (tile, source) records in the cover, not counting those waiting for their release.</summary>
        internal static int LoadedTileCount(this TileManager manager) => manager.CaptureTelemetry().LoadedTileCount;

        /// <summary>The active set the concurrency cap bounds: admitted, not-yet-built records in the cover.</summary>
        internal static int ActiveLoadCount(this TileManager manager) => manager.CountActiveLoads();

        /// <summary>(tile, source) keys wanting to load but not yet admitted.</summary>
        internal static int DesiredCount(this TileManager manager) => manager._desired.Count;

        /// <summary>The head of the not-yet-admitted desired list, or the default tile id when it is empty.</summary>
        internal static TileId DesiredHeadTile(this TileManager manager)
            => manager._desired.Count > 0 ? manager._desired[0].Tile : default;

        /// <summary>The tile of each record that serves a cover tile, as a tile id (may repeat across sources). A record a swap took
        /// out of the cover keeps its old role until its release drains, so the role is not the test.</summary>
        internal static void CollectLoadedTileIds(this TileManager manager, List<TileId> into)
        {
            into.Clear();
            foreach (var kv in manager._loaded)
                if (manager._coverIndex.Serves(kv.Key)) into.Add(kv.Key.Tile);
        }

        /// <summary>Every desired-but-not-admitted tile id, in priority order.</summary>
        internal static void CollectDesiredTileIds(this TileManager manager, List<TileId> into)
        {
            into.Clear();
            for (int i = 0; i < manager._desired.Count; i++) into.Add(manager._desired[i].Tile);
        }

        /// <summary>The tile ids of the last selected cover.</summary>
        internal static void CollectCoverTileIds(this TileManager manager, List<TileId> into)
        {
            into.Clear();
            into.AddRange(manager._selection.Cover);
        }

        /// <summary>Records waiting in the deferred-release queue.</summary>
        internal static int ReleaseQueueDepth(this TileManager manager) => manager._releaseQueue.Count;

        /// <summary>The live BRG renderer, or null when not on the BRG backend or before <c>SetSources</c>.</summary>
        internal static BrgTileRenderer BrgRenderer(this TileManager manager) => manager.Instanced as BrgTileRenderer;

        /// <summary>The live Entities-Graphics renderer, or null when not on the Entities backend or before <c>SetSources</c>.</summary>
        internal static EntitiesTileRenderer EntitiesRenderer(this TileManager manager) => manager.Instanced as EntitiesTileRenderer;

        /// <summary>The live GameObject renderer, or null when not on the GameObject backend or before <c>SetSources</c>.</summary>
        internal static GameObjectTileRenderer GameObjectRenderer(this TileManager manager) => manager.Instanced as GameObjectTileRenderer;

        /// <summary>True iff the record tile <paramref name="id"/> has at least one source record, all its records are built, and the
        /// union produced geometry.</summary>
        internal static bool TryGetBuiltTile(this TileManager manager, TileId id)
        {
            bool any = false;
            bool anyGeom = false;
            foreach (var kv in manager._loaded)
            {
                if (!kv.Key.Tile.Equals(id) || kv.Value.Role != TileManager.TileRole.Display) continue;
                any = true;
                if (!kv.Value.Built) return false;
                if (kv.Value.DrawHandles != null || kv.Value.Meshes != null) anyGeom = true;
            }

            return any && anyGeom;
        }

        /// <summary>The mesh assets of a loaded record tile: the union across its source records in pipeline-slot order, or null when it
        /// has no geometry. Backend-agnostic.</summary>
        internal static Mesh[] GetTileMeshes(this TileManager manager, TileId id)
        {
            List<Mesh> all = null;
            for (int s = 0; s < manager._sources.Count; s++)
            {
                if (manager._loaded.TryGetValue(new LoadedKey(id, s), out var lt) && lt.Meshes != null)
                {
                    all ??= new List<Mesh>(8);
                    all.AddRange(lt.Meshes);
                }
            }

            return all?.ToArray();
        }

        /// <summary>The global material index of each mesh in <see cref="GetTileMeshes"/>, in the same order.</summary>
        internal static int[] GetTileMaterialIndices(this TileManager manager, TileId id)
        {
            List<int> all = null;
            for (int s = 0; s < manager._sources.Count; s++)
            {
                if (manager._loaded.TryGetValue(new LoadedKey(id, s), out var lt) && lt.MaterialIndices != null)
                {
                    all ??= new List<int>(8);
                    all.AddRange(lt.MaterialIndices);
                }
            }

            return all?.ToArray();
        }

        /// <summary>True once every loaded tile, in any role, has finished building and both desired lists are empty. A check on the
        /// loaded records alone would miss cap-deferred tiles, which have no record until admitted.</summary>
        internal static bool AllTilesSettled(this TileManager manager)
        {
            if (manager._desired.Count > 0 || manager._prepareDesired.Count > 0) return false;

            foreach (var kv in manager._loaded)
                if (!kv.Value.Built) return false;

            return true;
        }

        /// <summary>Waits for in-flight fetch and mesh-build tasks on the loaded tiles, parking off the PlayerLoop rather than polling.
        /// It consumes and kicks nothing: a caller that needs the mesh built must still run <c>LateUpdate</c>. Never re-park a
        /// still-pending fetch, which double-registers the task's single continuation slot.</summary>
        /// <param name="timeoutMs">Maximum wait per task, in milliseconds.</param>
        /// <exception cref="System.TimeoutException">A task did not finish within <paramref name="timeoutMs"/>.</exception>
        internal static void AwaitInFlightMeshBuilds(this TileManager manager, int timeoutMs)
        {
            foreach (var kv in manager._loaded)
            {
                TileManager.LoadedTile lt = kv.Value;
                if (lt.Built) continue;

                bool completed;
                if (!lt.FetchCompleted)
                    completed = lt.Request.WaitOffPlayerLoop(timeoutMs);
                else if (lt.Step == BuildStep.Prologue)
                {
                    UniTask<TilePrologueOutput> buildTask = lt.MeshBuildTask.ToUniTask();
                    completed = buildTask.WaitOffPlayerLoop(timeoutMs);
                }
                // Complete() advances no step and consumes nothing; it cannot throw TimeoutException, since a graph has no PlayerLoop to dead-end on.
                else if (lt.Step == BuildStep.Measure || lt.Step == BuildStep.Write)
                {
                    lt.Graph.Complete();
                    completed = true;
                }
                else
                    continue; // fetch observed but not yet kicked (cap-deferred): no in-flight task to park on

                if (!completed)
                    throw new System.TimeoutException(
                        $"AwaitInFlightMeshBuilds: tile {kv.Key.Tile} did not complete within {timeoutMs}ms — " +
                        "a hung fetch or mesh-build task. Re-parking a still-pending task would double-register " +
                        "its single continuation, so this fails loud instead of retrying.");
            }
        }
    }
}
