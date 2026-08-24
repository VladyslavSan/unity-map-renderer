using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.View;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Backend.GameObjects
{
    /// <summary>
    /// GameObject render backend (internal, IDisposable) — the engine for <c>RenderBackend.GameObject</c>.
    ///
    /// The simplest, most debuggable backend: each tile-layer draw item is a child <see cref="GameObject"/>
    /// carrying a <see cref="MeshFilter"/> + <see cref="MeshRenderer"/> (drawn by URP's SRP Batcher),
    /// grouped under a per-tile container GameObject (<c>"Tile z/x/y"</c>) under a single backend root.
    /// Unlike the instanced backends (<see cref="Backend.BRG.TileRenderer"/>, <see cref="Backend.Entities.TileRenderer"/>) it
    /// registers no batches and uploads no per-instance buffers — it leans on Unity's stock renderer. The
    /// per-GameObject advantage is debuggability: every tile / layer is a node in the scene Hierarchy,
    /// selectable and toggle-able in the Inspector. (This is the path the project began on; retired in S53c
    /// once the Entities Hierarchy covered the same debug need, and restored here as an explicit opt-in.)
    ///
    /// Hierarchy: backend root → per-tile container (<c>"Tile z/x/y"</c>, carries the floating-origin
    /// transform) → per-layer child (<c>"water"</c>, <c>"road-primary"</c>, …, identity local transform).
    /// <see cref="Rebuild"/> writes one transform per tile (the container's <c>localPosition</c>), not one
    /// per layer; the layer children sit at the container origin so they move with it. The container is
    /// destroyed once its last layer is removed (an evicted tile leaves no empty node).
    ///
    /// Styling is per-layer (the shared material, written each frame by <c>ZoomStyleApplier</c> directly on
    /// the Material) plus per-feature (vertex colours baked into the mesh), so <see cref="Rebuild"/> does no
    /// material work — same as the instanced backends. <paramref name="layerMaterials"/> is the FULL-WIDTH,
    /// global-draw-slot-aligned material list (fill/line/symbol/background in one declared order, §3.3), so
    /// <c>materialIndex</c> matches <see cref="BRG.TileRenderer.AddTileLayer"/> /
    /// <see cref="Entities.TileRenderer.AddTileLayer"/>. E1 audit: this ctor only STORES the list (no
    /// registration, no index-0 seed), so a null entry at a symbol/background slot needs no guard here —
    /// verified safe (design risk 3).
    ///
    /// Mesh lifetime: this backend creates and destroys only GameObjects. The Mesh assets are owned by
    /// <c>TileManager</c> (its S51 leak guard) and must NOT be destroyed here — <see cref="RemoveItem"/> and
    /// <see cref="Dispose"/> destroy GameObjects only.
    ///
    /// Clean-room: design follows the floating-origin tile math (<see cref="FloatingOrigin.TileLocalToScene"/>)
    /// shared with the instanced backends.
    /// </summary>
    internal sealed class TileRenderer : VerifiedDisposable, ITileRenderBackend
    {
        // One draw item = one layer child GameObject (a child of its tile's container).
        // internal, not private: _items is internal for the test-assembly observability extensions, and a
        // field cannot be more accessible than its type.
        internal struct ItemRec
        {
            public MeshNode Node;
            public TileId   TileId;   // which tile container this layer hangs under
        }

        private readonly List<Material> _layerMaterials = new List<Material>();
        // Per-layer style id (e.g. "water", "road-primary"), parallel to _layerMaterials. Names each layer
        // GameObject after its style layer in the Hierarchy; empty/short ⇒ fall back to the material name.
        private readonly List<string>   _layerNames     = new List<string>();
        // internal (not private): the test assembly's GameObjectTileRendererTestExtensions reads these
        // for observability that used to sit on this class as public members.
        internal readonly Dictionary<int, ItemRec> _items = new Dictionary<int, ItemRec>();

        // The shared root → per-tile-container tree (Backend.SceneTileTree) — this backend owns the
        // per-layer child (the MeshFilter/MeshRenderer draw item) side only; the tile container itself,
        // its floating-origin transform, and its refcount teardown are the tree's job.
        internal SceneTileTree _tree;

        // Layer children recycle, for the same reason the symbol leaves do (see WorldSymbolRenderer): each one
        // costs new GameObject + AddComponent<MeshFilter> + AddComponent<MeshRenderer>, the AddComponents
        // dominating, and a zoom step replaces the WHOLE cover at once. This backend churns HARDER than the
        // symbol path — one child per tile LAYER, not per (layer, kind) symbol slot.
        //
        // A released child parks under _poolRoot, which is INACTIVE. ObjectPool is scene-unaware, so without
        // a reparent the child stays under its tile container; and SetParent(null) is not the answer either —
        // that promotes it to a SCENE-ROOT object, live in the Hierarchy, in the one backend whose whole
        // purpose is Inspector debuggability. MeshNode.Release drops mesh/material and disables the renderer;
        // dropping the mesh is load-bearing, since TileManager owns Mesh lifetime and destroys it right after
        // RemoveItem, so a parked child holding the reference would carry a destroyed Mesh into its next
        // tenancy.
        private GameObject _poolRoot;
        private readonly ObjectPool<MeshNode> _layerPool;

        /// <summary>A layer child's per-node settings, applied once at CREATION (not per rent): map geometry
        /// casts and receives no shadows, and DontSave keeps this runtime-built object out of the saved
        /// scene. MeshNode decides none of it — see its header.</summary>
        private static MeshNode NewLayerNode()
        {
            var node = new MeshNode(PooledLayerName);
            node.GameObject.hideFlags       = HideFlags.DontSave;
            node.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            node.Renderer.receiveShadows    = false;
            node.Renderer.enabled           = false; // AddTileLayer enables once mesh + material are bound
            return node;
        }

        // Placeholder name for a freshly built node; AttachAt renames it per style layer on every rent.
        private const string PooledLayerName = "tile-layer";
        private int _nextHandle;

        public TileRenderer(IReadOnlyList<Material> layerMaterials, IReadOnlyList<string> layerNames = null)
        {
            if (layerMaterials == null) throw new ArgumentNullException(nameof(layerMaterials));
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);
            if (layerNames != null)
                for (int i = 0; i < layerNames.Count; i++) _layerNames.Add(layerNames[i]);

            _tree     = new SceneTileTree("MapTiles (GameObject backend)");
            _poolRoot = new GameObject("(layer pool)") { hideFlags = HideFlags.DontSave };
            _poolRoot.transform.SetParent(_tree.Root, worldPositionStays: false);
            _poolRoot.SetActive(false);

            _layerPool = new ObjectPool<MeshNode>(
                createFunc: NewLayerNode,
                actionOnGet: null, // AddTileLayer attaches and names — only it knows the container and layer id
                actionOnRelease: node =>
                {
                    node.Release();
                    node.Transform.SetParent(_poolRoot.transform, worldPositionStays: false);
                },
                actionOnDestroy: node => node.Dispose(),
                collectionCheck: true, // a double-release would hand one child to two draw items
                defaultCapacity: 64,
                maxSize: 1024);
        }

        // Draw-item / container / root observability used to live here, under a "Test / debug observability"
        // banner — DrawItemCount, ContainerCount, Root, Container and GetInstanceTranslation, none with a
        // production caller. They are now extension methods in the test assembly
        // (GameObjectTileRendererTestExtensions), reading _items/_tree via InternalsVisibleTo — the footprint
        // the conventions sanction for test-only surface. Their post-dispose leniency (null / 0 / NaN) went
        // with them: it existed only so a test could read a torn-down backend, which is a thing that should
        // not happen rather than a thing to accommodate.

        /// <summary>
        /// XZ scene-space bounding box covering all live tile containers (each container's position, plus
        /// <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin). Returns
        /// <c>default</c> when empty. Mirrors <see cref="Entities.TileRenderer.ComputeSceneBounds"/>.
        /// <see cref="ITileRenderBackend"/> surface, so it stays here — but it no longer answers after
        /// disposal; <see cref="SceneTileTree"/> throws, which is the contract.
        /// </summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld) => _tree.ComputeSceneBounds(tileSizeWorld);

        // ── Draw item registration ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a tile-layer mesh as a child GameObject under its tile's container (created on demand
        /// via the shared <see cref="SceneTileTree"/>). Returns a handle for later removal.
        /// <paramref name="materialIndex"/> is the layer's global draw slot, indexing the full-width
        /// material list, matching the instanced backends; non-tile-mesh slots are null and never receive
        /// this call.
        /// </summary>
        public int AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId)
        {
            ThrowIfDisposed();
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if ((uint)materialIndex >= (uint)_layerMaterials.Count)
                throw new ArgumentOutOfRangeException(nameof(materialIndex));

            Material  mat       = _layerMaterials[materialIndex];
            Transform container = _tree.GetOrCreateTileNode(tileId, tileOriginRender);

            // Name the child after its style layer ("water", "road-primary", …) so the Hierarchy reads
            // cleanly; fall back to the (shared) material name when no style id is available.
            string layerName = (uint)materialIndex < (uint)_layerNames.Count
                && !string.IsNullOrEmpty(_layerNames[materialIndex])
                    ? _layerNames[materialIndex]
                    : mat.name;

            MeshNode node = _layerPool.Get();
            node.AttachAt(container, layerName);
            node.Filter.sharedMesh       = mesh;  // sharedMesh: assign, do not clone
            node.Renderer.sharedMaterial = mat;   // sharedMaterial: reference the live layer material
            node.Renderer.enabled        = true;  // MeshNode builds and releases disabled

            _tree.AddChild(tileId);

            int handle = _nextHandle++;
            _items[handle] = new ItemRec { Node = node, TileId = tileId };
            return handle;
        }

        /// <summary>
        /// Destroys the draw item's layer GameObject, and releases it from the owning tile container (which
        /// the tree destroys once its last layer is gone). The Mesh asset is NOT destroyed here — the caller
        /// (TileManager) owns the Mesh lifetime. Idempotent for unknown handles.
        /// </summary>
        public void RemoveItem(int handle)
        {
            if (IsDisposed) return;
            if (!_items.TryGetValue(handle, out var item)) return;

            _layerPool.Release(item.Node);
            _items.Remove(handle);
            _tree.ReleaseChildFrom(item.TileId);
        }

        /// <summary>GameObject removal (Object.Destroy per child + container) has no batchable structural cost;
        /// the loop is the implementation. Inspector-debug backend only. See <see cref="ITileRenderBackend.RemoveItems"/>.</summary>
        public void RemoveItems(ReadOnlySpan<int> handles)
        {
            if (IsDisposed) return;
            for (int i = 0; i < handles.Length; i++) RemoveItem(handles[i]);
        }

        // ── Per-frame rebuild ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Refreshes each tile container's <c>localPosition</c> and <c>localRotation</c> from
        /// <paramref name="frame"/> (origin ≡ look-at; camera-relative rendering) via the shared
        /// <see cref="SceneTileTree"/>. One transform write per tile, not per layer — the layer children sit
        /// at the container origin and move with it. Does no material work (<c>ZoomStyleApplier</c> mutates
        /// the shared materials live, same as the instanced backends). For Mercator the rebase is identity,
        /// so this reduces to the pre-S91 translation write.
        /// </summary>
        public void Rebuild(in SceneFrame frame)
        {
            if (IsDisposed) return;
            _tree.Rebuild(in frame);
        }

        // ── Teardown ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Destroys the backend root (and with it every container + LIVE layer child), then the pool's
        /// detached children. Does NOT destroy Mesh assets — TileManager owns those. Idempotent.
        /// </summary>
        protected override void DoDispose()
        {
            // The tree destroys the layer children's GameObjects, but each MeshNode WRAPPER is owned here and
            // must be disposed or it is reported as a leak (MeshNode.DoDispose).
            foreach (var kv in _items) kv.Value.Node?.Dispose();
            _items.Clear();
            _tree.Dispose(); // destroys all containers + their layer children
            _tree = null;
            _layerPool.Clear(); // actionOnDestroy per parked child — those are under _poolRoot, not _tree
            _poolRoot = null; // destroyed with the tree root above
        }
    }
}
