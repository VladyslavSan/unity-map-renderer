using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
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
        private struct ItemRec
        {
            public GameObject Go;
            public TileId     TileId;   // which tile container this layer hangs under
        }

        private readonly List<Material> _layerMaterials = new List<Material>();
        // Per-layer style id (e.g. "water", "road-primary"), parallel to _layerMaterials. Names each layer
        // GameObject after its style layer in the Hierarchy; empty/short ⇒ fall back to the material name.
        private readonly List<string>   _layerNames     = new List<string>();
        private readonly Dictionary<int, ItemRec> _items = new Dictionary<int, ItemRec>();

        // The shared root → per-tile-container tree (Backend.SceneTileTree) — this backend owns the
        // per-layer child (the MeshFilter/MeshRenderer draw item) side only; the tile container itself,
        // its floating-origin transform, and its refcount teardown are the tree's job.
        private SceneTileTree _tree;
        private int _nextHandle;

        public TileRenderer(IReadOnlyList<Material> layerMaterials, IReadOnlyList<string> layerNames = null)
        {
            if (layerMaterials == null) throw new ArgumentNullException(nameof(layerMaterials));
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);
            if (layerNames != null)
                for (int i = 0; i < layerNames.Count; i++) _layerNames.Add(layerNames[i]);

            _tree = new SceneTileTree("MapTiles (GameObject backend)");
        }

        // ── Test / debug observability ──────────────────────────────────────────────────────────

        /// <summary>Number of currently registered draw items (layer GameObjects).</summary>
        public int DrawItemCount => _items.Count;

        /// <summary>Number of live tile containers (one per tile that has ≥1 layer). Zero after dispose
        /// (mirrors <see cref="Root"/>'s null-after-dispose guard — pre-extraction this read <c>_containers.Count</c>,
        /// which is 0 on an empty/disposed dictionary; unguarded <c>_tree</c> access would NRE instead).</summary>
        public int ContainerCount => _tree?.NodeCount ?? 0;

        // IsDisposed is inherited from VerifiedDisposable (public there too — no shadow needed).

        /// <summary>The backend root's transform (null after dispose). Tests read the live Hierarchy through it.</summary>
        public Transform Root => _tree?.Root;

        /// <summary>The container transform for <paramref name="tileId"/>, or null if no live container.</summary>
        public Transform Container(TileId tileId)
            => !IsDisposed ? _tree.Container(tileId) : null;

        /// <summary>
        /// World-space translation (X, Z) of the draw item's owning container, or (NaN, NaN) for an
        /// unknown/dead handle. The backend root sits at the world origin, so a container's position is its
        /// scene placement and a layer child (parented at the container origin) shares it. GPU-independent —
        /// reads the live transform. Mirrors the instanced backends' <c>GetInstanceTranslation</c> so the
        /// floating-origin tests are parallel.
        /// </summary>
        public (float x, float z) GetInstanceTranslation(int handle)
        {
            if (IsDisposed || !_items.TryGetValue(handle, out var item) || item.Go == null)
                return (float.NaN, float.NaN);
            Vector3 t = item.Go.transform.position;
            return (t.x, t.z);
        }

        /// <summary>
        /// XZ scene-space bounding box covering all live tile containers (each container's position, plus
        /// <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin). Used by tests to
        /// frame a camera that sees all tiles. Returns <c>default</c> when empty. Mirrors
        /// <see cref="Entities.TileRenderer.ComputeSceneBounds"/>.
        /// </summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld)
            => IsDisposed ? new Bounds(Vector3.zero, Vector3.zero) : _tree.ComputeSceneBounds(tileSizeWorld);

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

            var layerGo = new GameObject(layerName);
            layerGo.transform.SetParent(container, worldPositionStays: false);
            layerGo.transform.localPosition = Vector3.zero;

            var mf = layerGo.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;                       // sharedMesh: assign, do not clone

            var mr = layerGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial    = mat;                 // sharedMaterial: reference the live layer material
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            _tree.AddChild(tileId);

            int handle = _nextHandle++;
            _items[handle] = new ItemRec { Go = layerGo, TileId = tileId };
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

            DestroyGo(item.Go);
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

        private static void DestroyGo(GameObject go) => go.DestroySafely();

        /// <summary>
        /// Destroys the backend root (and with it every container + layer child). Does NOT destroy Mesh
        /// assets — TileManager owns those. Idempotent.
        /// </summary>
        protected override void DoDispose()
        {
            _items.Clear();
            _tree.Dispose(); // destroys all containers + their layer children
            _tree = null;
        }
    }
}
