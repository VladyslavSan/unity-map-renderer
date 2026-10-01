using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Rendering;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.View;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Backend.GameObjects
{
    /// <summary>
    /// GameObject render backend for <c>RenderBackend.GameObject</c>: each tile-layer draw item is a child
    /// <see cref="GameObject"/> with a <see cref="MeshFilter"/> + <see cref="MeshRenderer"/>, under a per-tile
    /// container, so every tile and layer is a node in the Hierarchy. The layer list is full-width and
    /// slot-aligned with the instanced backends; a null symbol/background slot needs no guard here.
    /// Non-local invariant: <c>TileManager</c> owns the Mesh assets, so this backend destroys GameObjects only.
    /// </summary>
    internal sealed class TileRenderer : TileRenderBackendBase<TileRenderer.DrawItem>, ITileRenderBackend
    {
        // One draw item = one layer child GameObject. Internal because the internal _items field
        // (read by test-assembly extensions) cannot be more accessible than its type.
        internal struct DrawItem : IDrawItem
        {
            public MeshNode Node;
            public TileId   TileId;   // which tile container this layer hangs under
            public int      MaterialIndex { get; set; } // the layer slot this child was bound at — lets
                                                        // SetLayerMaterials find every item a retired slot must retire
            public bool     Hidden        { get; set; } // item-level flag (SetItemsVisible); drawn only when also slot-visible
        }

        private readonly List<Material> _layerMaterials = new List<Material>();
        // Per-layer style id (e.g. "water", "road-primary"), parallel to _layerMaterials. Names each layer
        // GameObject after its style layer in the Hierarchy; empty/short ⇒ fall back to the material name.
        private readonly List<string>   _layerNames     = new List<string>();
        // _items (in the base) and _tree are internal so the test assembly's GameObjectTileRendererTestExtensions can read
        // them, kept off the public surface.

        // The shared per-tile container tree. This backend owns only the per-layer children; the containers,
        // their floating-origin transforms and their refcount teardown belong to the tree.
        internal SceneTileTree _tree;

        // Inactive parent for released layer children; the pool recycles them because each costs two
        // AddComponents and a zoom step replaces the whole cover. ObjectPool does not reparent, and
        // SetParent(null) would make a parked child an active scene root. Non-obvious why: MeshNode.Release
        // drops the mesh because TileManager destroys it right after RemoveItem, so a parked child that kept it
        // would carry a destroyed Mesh into its next tenancy.
        private GameObject _poolRoot;
        private readonly ObjectPool<MeshNode> _layerPool;

        /// <summary>A layer child's per-node settings, applied once at CREATION (not per rent): DontSave keeps
        /// this runtime-built object out of the saved scene. The shadow flags are NOT here — a
        /// pooled node outlives one layer's tenancy and can serve a different slot next rent, so
        /// <see cref="AddTileLayer"/> binds them per rent alongside mesh and material. MeshNode decides none
        /// of it — see its header.</summary>
        private static MeshNode NewLayerNode()
        {
            var node = new MeshNode(PooledLayerName);
            node.GameObject.hideFlags       = HideFlags.DontSave;
            node.Renderer.enabled           = false; // born hidden: SetItemsVisible enables it
            return node;
        }

        /// <summary>Each child is its own <c>Renderer.enabled</c> write.</summary>
        protected override void ApplySlotGate(int slot, bool visible)
        {
            foreach (var kv in _items)
                if (kv.Value.MaterialIndex == slot)
                    kv.Value.Node.Renderer.enabled = visible && !kv.Value.Hidden;
        }

        /// <summary>Each child is its own <c>Renderer.enabled</c> write, so the batch is just the loop.</summary>
        protected override void ApplyItemVisibility(DrawItem item, bool visible, bool slotVisible)
            => item.Node.Renderer.enabled = visible && slotVisible;

        // Placeholder name for a freshly built node; AttachAt renames it per style layer on every rent.
        private const string PooledLayerName = "tile-layer";
        private int _nextHandle;

        public TileRenderer(
            IReadOnlyList<Material> layerMaterials,
            IReadOnlyList<string> layerNames = null,
            IReadOnlyList<ShadowCastingMode> layerShadowModes = null)
            : base(layerShadowModes)
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

        // Test-only observability (DrawItemCount, ContainerCount, Root, …) lives in the test assembly's
        // GameObjectTileRendererTestExtensions, which read _items/_tree and have no post-dispose guard.

        // ── Draw item registration ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Registers a tile-layer mesh as a child GameObject under its tile's container (created on demand
        /// via the shared <see cref="SceneTileTree"/>). Returns a handle for later removal.
        /// <paramref name="materialIndex"/> is the layer's global SLOT, indexing the full-width
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
                    : mat != null ? mat.name : string.Empty;

            MeshNode node = _layerPool.Get();
            node.AttachAt(container, layerName);
            node.Filter.sharedMesh       = mesh;  // sharedMesh: assign, do not clone
            node.Renderer.sharedMaterial = mat;   // sharedMaterial: reference the live layer material
            // Per rent, not per node: the pool recycles a node across layers with different declarations.
            node.Renderer.shadowCastingMode = ShadowModeFor(materialIndex);
            node.Renderer.receiveShadows    = true;
            // No enabled write: MeshNode builds and releases DISABLED, so the item is born hidden.

            _tree.AddChild(tileId);

            int handle = _nextHandle++;
            _items[handle] = new DrawItem { Node = node, TileId = tileId, MaterialIndex = materialIndex, Hidden = true };
            return handle;
        }

        /// <summary>Restyle-time material update — re-points a changed slot's live
        /// <c>sharedMaterial</c>s, and retires (pool-releases) a slot going null; see
        /// `docs/tile-pipeline-design.md`.</summary>
        public void SetLayerMaterials(
            IReadOnlyList<Material> layerMaterials, IReadOnlyList<ShadowCastingMode> layerShadowModes)
        {
            ThrowIfDisposed();

            int oldCount = _layerMaterials.Count;
            var retired  = new List<int>();
            foreach (var kv in _items)
            {
                int mi = kv.Value.MaterialIndex;
                Material newMat = (uint)mi < (uint)layerMaterials.Count ? layerMaterials[mi] : null;
                if (newMat == null) { retired.Add(kv.Key); continue; }
                Material oldMat = mi < oldCount ? _layerMaterials[mi] : null;
                if (!ReferenceEquals(newMat, oldMat))
                    kv.Value.Node.Renderer.sharedMaterial = newMat;
            }

            _layerMaterials.Clear();
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);

            // A retired slot keeps no style-layer name: its material is null and nothing draws into it.
            for (int i = 0; i < _layerNames.Count && i < layerMaterials.Count; i++)
                if (layerMaterials[i] == null) _layerNames[i] = null;

            ReplaceSlotLists(layerMaterials.Count, layerShadowModes);

            if (retired.Count > 0)
                RemoveItems(retired.ToArray());
        }

        /// <summary>
        /// Destroys the draw item's layer GameObject, and releases it from the owning tile container (which
        /// the tree destroys once its last layer is gone). The Mesh asset is NOT destroyed here — the caller
        /// (TileManager) owns the Mesh lifetime. Idempotent for unknown handles.
        /// </summary>
        private void RemoveItem(int handle)
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
        /// <paramref name="frame"/> (origin = look-at) via the shared <see cref="SceneTileTree"/>: one transform
        /// write per tile, since the layer children sit at the container origin. Does no material work, because
        /// <c>ZoomStyleApplier</c> writes the shared materials directly.
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
