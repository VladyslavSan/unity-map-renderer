// Unity EditMode only — real GameObject/Transform. NOT registered in core-tests.csproj.
//
// Namespace is MapRenderer.Tests (not .Visual) deliberately: C# resolves extension methods only through
// the call site's ENCLOSING namespaces, and callers live in both MapRenderer.Tests and
// MapRenderer.Tests.Visual. The parent namespace is the one both can see.
//
// Observability for the GameObject backend, living in the TEST assembly rather than on the production
// class. These five were `public` members on Backend.GameObjects.TileRenderer under a "Test / debug
// observability" banner, with ZERO production callers between them — the shape the conventions rule out
// ("a member that exists solely for a test does not belong on the production class"). They read the
// backend's `_items`/`_tree`, broadened private -> internal, which IS the sanctioned footprint.
//
// They also drop the post-dispose leniency the production versions carried (null / 0 / NaN). That
// existed so a test could read a torn-down backend; using an object after Dispose is a bug rather than
// a case to accommodate, so these now fault like any other post-dispose access.

using UnityEngine;
using MapRenderer.Core.Geo;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests
{
    internal static class GameObjectTileRendererTestExtensions
    {
        /// <summary>Number of currently registered draw items (layer GameObjects).</summary>
        internal static int DrawItemCount(this GameObjectTileRenderer renderer) => renderer._items.Count;

        /// <summary>Number of live tile containers (one per tile that has ≥1 layer).</summary>
        internal static int ContainerCount(this GameObjectTileRenderer renderer) => renderer._tree.NodeCount;

        /// <summary>The backend root's transform — the live Hierarchy entry point.</summary>
        internal static Transform Root(this GameObjectTileRenderer renderer) => renderer._tree.Root;

        /// <summary>The container transform for <paramref name="tileId"/>, or null if no live container.</summary>
        internal static Transform Container(this GameObjectTileRenderer renderer, TileId tileId)
            => renderer._tree.Container(tileId);

        /// <summary>Whether the draw item's layer child is submitted for drawing — the GameObject arm of
        /// the per-slot draw gate (<c>ITileRenderBackend.SetLayerVisible</c>). Throws for an unknown
        /// handle.</summary>
        internal static bool IsItemDrawn(this GameObjectTileRenderer renderer, int handle)
            => renderer._items[handle].Node.Renderer.enabled;

        /// <summary>
        /// World-space translation (X, Z) of the draw item's owning container, or (NaN, NaN) for an
        /// unknown/dead handle. The backend root sits at the world origin, so a container's position is its
        /// scene placement and a layer child (parented at the container origin) shares it. GPU-independent —
        /// reads the live transform. Mirrors the instanced backends' <c>GetInstanceTranslation</c> so the
        /// floating-origin tests are parallel.
        /// </summary>
        internal static (float x, float z) GetInstanceTranslation(this GameObjectTileRenderer renderer, int handle)
        {
            if (!renderer._items.TryGetValue(handle, out var item) || item.Node.GameObject == null)
                return (float.NaN, float.NaN);
            Vector3 t = item.Node.Transform.position;
            return (t.x, t.z);
        }
    }
}
