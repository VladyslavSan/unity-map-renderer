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
    /// global-SLOT-aligned material list (fill/line/symbol/background in one slot order), so
    /// <c>materialIndex</c> matches <see cref="BRG.TileRenderer.AddTileLayer"/> /
    /// <see cref="Entities.TileRenderer.AddTileLayer"/>. This ctor only STORES the list (no registration,
    /// no index-0 seed), so a null entry at a symbol/background slot needs no guard here.
    ///
    /// Mesh lifetime: this backend creates and destroys only GameObjects. The Mesh assets are owned by
    /// <c>TileManager</c> (its leak guard) and must NOT be destroyed here — <see cref="RemoveItem"/> and
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
            public int      MaterialIndex; // the layer slot this child was bound at — lets
                                            // SetLayerMaterials find every item a retired slot must retire
        }

        private readonly List<Material> _layerMaterials = new List<Material>();
        // Per-layer style id (e.g. "water", "road-primary"), parallel to _layerMaterials. Names each layer
        // GameObject after its style layer in the Hierarchy; empty/short ⇒ fall back to the material name.
        private readonly List<string>   _layerNames     = new List<string>();
        // Per-layer shadow-cast declaration, parallel to _layerMaterials — IRenderLayer.CastShadows, carried
        // verbatim from TileManager.LayerShadowModes. Absent or short ⇒ Off (never default(...), never throw);
        // the same fallback the other two backends use, so a divergence is a test failure, not a silent drift.
        private readonly List<ShadowCastingMode> _layerShadowModes = new List<ShadowCastingMode>();
        // Per-layer draw gate (ITileRenderBackend.SetLayerVisible), parallel to _layerMaterials. True ⇒ this
        // slot's children are drawn. Absent or short ⇒ visible, identically in all three backends.
        private readonly List<bool> _layerVisible = new List<bool>();
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

        /// <summary>A layer child's per-node settings, applied once at CREATION (not per rent): DontSave keeps
        /// this runtime-built object out of the saved scene. The shadow flags are deliberately NOT here — a
        /// pooled node outlives one layer's tenancy and can serve a different slot next rent, so
        /// <see cref="AddTileLayer"/> binds them per rent alongside mesh and material. MeshNode decides none
        /// of it — see its header.</summary>
        private static MeshNode NewLayerNode()
        {
            var node = new MeshNode(PooledLayerName);
            node.GameObject.hideFlags       = HideFlags.DontSave;
            node.Renderer.enabled           = false; // AddTileLayer enables once mesh + material are bound
            return node;
        }

        /// <summary>This backend's copy of the shared shadow-mode lookup: the declared mode for
        /// <paramref name="materialIndex"/>, or <see cref="ShadowCastingMode.Off"/> when no list was supplied
        /// or it is short. The fallback must read identically in all three backends
        /// (<see cref="ITileRenderBackend"/>).</summary>
        /// <param name="materialIndex">The layer's global SLOT.</param>
        private ShadowCastingMode ShadowModeFor(int materialIndex)
            => (uint)materialIndex < (uint)_layerShadowModes.Count
                ? _layerShadowModes[materialIndex]
                : ShadowCastingMode.Off;

        /// <summary>True when <paramref name="materialIndex"/>'s slot is visible.</summary>
        /// <param name="materialIndex">The layer's global SLOT.</param>
        private bool Visible(int materialIndex)
            => (uint)materialIndex >= (uint)_layerVisible.Count || _layerVisible[materialIndex];

        /// <inheritdoc cref="ITileRenderBackend.SetLayerVisible"/>
        public void SetLayerVisible(int slot, bool visible)
        {
            if (IsDisposed || slot < 0) return;
            while (_layerVisible.Count <= slot) _layerVisible.Add(true);
            if (_layerVisible[slot] == visible) return; // unchanged ⇒ no walk over the items
            _layerVisible[slot] = visible;

            foreach (var kv in _items)
                if (kv.Value.MaterialIndex == slot)
                    kv.Value.Node.Renderer.enabled = visible;
        }

        // Placeholder name for a freshly built node; AttachAt renames it per style layer on every rent.
        private const string PooledLayerName = "tile-layer";
        private int _nextHandle;

        public TileRenderer(
            IReadOnlyList<Material> layerMaterials,
            IReadOnlyList<string> layerNames = null,
            IReadOnlyList<ShadowCastingMode> layerShadowModes = null)
        {
            if (layerMaterials == null) throw new ArgumentNullException(nameof(layerMaterials));
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);
            if (layerNames != null)
                for (int i = 0; i < layerNames.Count; i++) _layerNames.Add(layerNames[i]);
            if (layerShadowModes != null)
                for (int i = 0; i < layerShadowModes.Count; i++) _layerShadowModes.Add(layerShadowModes[i]);

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
                    : mat.name;

            MeshNode node = _layerPool.Get();
            node.AttachAt(container, layerName);
            node.Filter.sharedMesh       = mesh;  // sharedMesh: assign, do not clone
            node.Renderer.sharedMaterial = mat;   // sharedMaterial: reference the live layer material
            // Per rent, not per node: the pool recycles a node across layers with different declarations.
            node.Renderer.shadowCastingMode = ShadowModeFor(materialIndex);
            node.Renderer.receiveShadows    = true;
            // Not unconditionally true: MeshNode builds and releases DISABLED, and an item added into a
            // slot that is already hidden must stay that way until the gate lifts.
            node.Renderer.enabled        = Visible(materialIndex);

            _tree.AddChild(tileId);

            int handle = _nextHandle++;
            _items[handle] = new ItemRec { Node = node, TileId = tileId, MaterialIndex = materialIndex };
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

            _layerShadowModes.Clear();
            if (layerShadowModes != null)
                for (int i = 0; i < layerShadowModes.Count; i++) _layerShadowModes.Add(layerShadowModes[i]);

            if (retired.Count > 0)
                RemoveItems(retired.ToArray());
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
        /// so this reduces to a translation write.
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
