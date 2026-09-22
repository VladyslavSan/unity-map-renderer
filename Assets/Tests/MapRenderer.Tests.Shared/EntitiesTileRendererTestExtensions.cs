// Unity EditMode only — real World/EntityManager. NOT registered in core-tests.csproj.
//
// Namespace is MapRenderer.Tests (not .Visual) deliberately, matching GameObjectTileRendererTestExtensions:
// C# resolves extension methods only through the call site's ENCLOSING namespaces, so the parent namespace
// is reachable from both. Today's callers all sit in MapRenderer.Tests.Visual; the parent costs nothing and
// keeps the two backends' observability files symmetrical.
//
// Observability for the Entities backend, living in the TEST assembly rather than on the production class.
// These nine sat on Backend.Entities.TileRenderer under a "Test / debug observability" banner with ZERO
// production callers between them — the shape the conventions rule out ("a member that exists solely for a
// test does not belong on the production class"). They read the backend's `_items`/`_tileRoots`/`_em`,
// broadened private -> internal, which IS the sanctioned footprint.
//
// They also drop the post-dispose leniency the production versions carried (-1 / false / NaN). That existed
// so a test could read a torn-down backend; using an object after Dispose is a bug rather than a case to
// accommodate. Note the failure MODE changed with the location: post-dispose `_em` refers to a destroyed
// World, so these now throw from inside EntityManager instead of returning a sentinel.
//
// GetTileRootName is not here — it had no callers in either assembly and was deleted outright.
//
// The two Editor-only members lost their `#if UNITY_EDITOR`: this assembly's .asmdef carries
// defineConstraints:["UNITY_INCLUDE_TESTS"], so it never compiles into a release player and the guard
// was unconditionally true here. The guard still matters on the production SIDE, where the names are
// written.

using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using MapRenderer.Core.Geo;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;

namespace MapRenderer.Tests
{
    internal static class EntitiesTileRendererTestExtensions
    {
        /// <summary>Number of currently registered draw items (layer entities).</summary>
        internal static int DrawItemCount(this EntitiesTileRenderer renderer) => renderer._items.Count;

        /// <summary>The draw item entity's <see cref="RenderFilterSettings"/> shadow pair — the shared
        /// component EG filters shadow-pass rendering by. This is the Entities arm of the cross-backend
        /// shadow-transport tooth; it reads what the prototype chosen at <c>Instantiate</c> baked in.
        /// Throws for an unknown handle, like the other post-dispose-strict readers here.</summary>
        internal static (UnityEngine.Rendering.ShadowCastingMode cast, bool receive) GetShadowFilter(
            this EntitiesTileRenderer renderer, int handle)
        {
            var f = renderer._em.GetSharedComponentManaged<RenderFilterSettings>(renderer._items[handle].Entity);
            return (f.ShadowCastingMode, f.ReceiveShadows);
        }

        /// <summary>Whether the draw item's entity is submitted for drawing — the Entities arm of the
        /// per-slot draw gate (<c>ITileRenderBackend.SetLayerVisible</c>), which rides EG's
        /// <see cref="DisableRendering"/> tag. Throws for an unknown handle.</summary>
        internal static bool IsItemDrawn(this EntitiesTileRenderer renderer, int handle)
            => !renderer._em.HasComponent<DisableRendering>(renderer._items[handle].Entity);

        /// <summary>True when NO live draw item at <paramref name="materialIndex"/> is gated out. Reads
        /// every item rather than one handle, so a gate that reached only some of a slot's entities fails
        /// it.</summary>
        internal static bool AllItemsDrawnAtSlot(this EntitiesTileRenderer renderer, int materialIndex)
        {
            foreach (var kv in renderer._items)
                if (kv.Value.MaterialIndex == materialIndex
                    && renderer._em.HasComponent<DisableRendering>(kv.Value.Entity))
                    return false;
            return true;
        }

        /// <summary>Live draw items registered at <paramref name="materialIndex"/> — the anti-vacuity count
        /// for <see cref="AllItemsDrawnAtSlot"/>, which is trivially true of a slot with no items.</summary>
        internal static int ItemCountAtSlot(this EntitiesTileRenderer renderer, int materialIndex)
        {
            int n = 0;
            foreach (var kv in renderer._items) if (kv.Value.MaterialIndex == materialIndex) n++;
            return n;
        }

        /// <summary>Number of live tile root entities (one per tile that has ≥1 layer).</summary>
        internal static int TileRootCount(this EntitiesTileRenderer renderer) => renderer._tileRoots.Count;

        /// <summary>True if a root entity is live for <paramref name="tileId"/>.</summary>
        internal static bool TileRootExists(this EntitiesTileRenderer renderer, TileId tileId)
            => renderer._tileRoots.TryGetValue(tileId, out var r) && renderer._em.Exists(r.Root);

        /// <summary>
        /// Number of layer entities the transform system has linked under <paramref name="tileId"/>'s root
        /// (read from the root's <see cref="Child"/> buffer, which <see cref="ParentSystem"/> maintains).
        /// Returns -1 if the root or its Child buffer does not exist yet (no Rebuild/tick has run). This is
        /// the structural proxy for "the Entities Hierarchy groups these layers under the tile."
        /// </summary>
        internal static int RootChildBufferCount(this EntitiesTileRenderer renderer, TileId tileId)
        {
            if (!renderer._tileRoots.TryGetValue(tileId, out var r) || !renderer._em.Exists(r.Root)) return -1;
            if (!renderer._em.HasComponent<Child>(r.Root)) return -1;
            return renderer._em.GetBuffer<Child>(r.Root).Length;
        }

        /// <summary>True if the layer entity for <paramref name="handle"/> is parented to the root of
        /// <paramref name="tileId"/> (via its <see cref="Parent"/> component).</summary>
        internal static bool IsParentedToTileRoot(this EntitiesTileRenderer renderer, int handle, TileId tileId)
        {
            if (!renderer._items.TryGetValue(handle, out var item) || !renderer._em.Exists(item.Entity)) return false;
            if (!renderer._tileRoots.TryGetValue(tileId, out var r)
                || !renderer._em.HasComponent<Parent>(item.Entity)) return false;
            return renderer._em.GetComponentData<Parent>(item.Entity).Value == r.Root;
        }

        /// <summary>The debug name assigned to a layer entity — its style layer id (e.g. "water").</summary>
        internal static string GetLayerEntityName(this EntitiesTileRenderer renderer, int handle)
            => renderer._items.TryGetValue(handle, out var rec) && renderer._em.Exists(rec.Entity)
                ? renderer._em.GetName(rec.Entity)
                : null;

        /// <summary>True if the draw item <paramref name="handle"/> still has a live entity.</summary>
        internal static bool EntityExists(this EntitiesTileRenderer renderer, int handle)
            => renderer._items.TryGetValue(handle, out var rec) && renderer._em.Exists(rec.Entity);

        /// <summary>
        /// World-space translation (X, Z) of the draw item's entity from the last <c>Rebuild</c>.
        /// GPU-independent — reads the entity's <see cref="LocalToWorld"/>. Returns (NaN, NaN) for an
        /// unknown/dead handle. Mirrors the other backends' <c>GetInstanceTranslation</c> so the
        /// floating-origin tests are parallel.
        /// </summary>
        internal static (float x, float z) GetInstanceTranslation(this EntitiesTileRenderer renderer, int handle)
        {
            if (!renderer._items.TryGetValue(handle, out var rec) || !renderer._em.Exists(rec.Entity))
                return (float.NaN, float.NaN);
            float3 t = renderer._em.GetComponentData<LocalToWorld>(rec.Entity).Position;
            return (t.x, t.z);
        }

        /// <summary>
        /// The draw item entity's local <see cref="RenderBounds"/> (the AABB EG frustum-culls against,
        /// before <see cref="LocalToWorld"/>). Test observability for the "tile culled in Game view"
        /// regression: it must ENCLOSE the mesh, not the old fixed { Center=0, Extents=1e6 } box.
        /// Returns two NaN float3s for an unknown/dead handle.
        /// </summary>
        internal static (float3 center, float3 extents) GetRenderBoundsLocal(
            this EntitiesTileRenderer renderer, int handle)
        {
            if (!renderer._items.TryGetValue(handle, out var rec)
                || !renderer._em.Exists(rec.Entity)
                || !renderer._em.HasComponent<RenderBounds>(rec.Entity))
                return (new float3(float.NaN), new float3(float.NaN));
            AABB b = renderer._em.GetComponentData<RenderBounds>(rec.Entity).Value;
            return (b.Center, b.Extents);
        }
    }
}
