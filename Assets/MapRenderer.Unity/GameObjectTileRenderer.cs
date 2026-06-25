using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.View;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Unity
{
    /// <summary>
    /// GameObject render backend (internal, IDisposable) — the engine for <c>RenderBackend.GameObject</c>.
    ///
    /// The simplest, most debuggable backend: each tile-layer draw item is a child <see cref="GameObject"/>
    /// carrying a <see cref="MeshFilter"/> + <see cref="MeshRenderer"/> (drawn by URP's SRP Batcher),
    /// grouped under a per-tile container GameObject (<c>"Tile z/x/y"</c>) under a single backend root.
    /// Unlike the instanced backends (<see cref="BrgTileRenderer"/>, <see cref="EntitiesTileRenderer"/>) it
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
    /// material work — same as the instanced backends. <paramref name="layerMaterials"/> is the flattened
    /// layer-material list (fills in declared order, then lines), so <c>materialIndex</c> matches
    /// <see cref="BrgTileRenderer.AddTileLayer"/> / <see cref="EntitiesTileRenderer.AddTileLayer"/>.
    ///
    /// Mesh lifetime: this backend creates and destroys only GameObjects. The Mesh assets are owned by
    /// <c>TileManager</c> (its S51 leak guard) and must NOT be destroyed here — <see cref="RemoveItem"/> and
    /// <see cref="Dispose"/> destroy GameObjects only.
    ///
    /// Clean-room: design follows the floating-origin tile math (<see cref="FloatingOrigin.TileLocalToScene"/>)
    /// shared with the instanced backends.
    /// </summary>
    internal sealed class GameObjectTileRenderer : ITileRenderBackend
    {
        // One draw item = one layer child GameObject (a child of its tile's container).
        private struct ItemRec
        {
            public GameObject Go;
            public TileId     TileId;   // which tile container this layer hangs under
        }

        // One per live tile: the named container its layer children are grouped under, so the scene
        // Hierarchy shows a per-tile tree. Carries the tile's mercator origin so Rebuild can reposition the
        // whole subtree by writing only the container transform.
        private struct ContainerRec
        {
            public GameObject Go;
            public double2    TileOriginMerc;
            public int        ChildCount;   // layer children; container dies when this hits 0
        }

        private readonly List<Material> _layerMaterials = new List<Material>();
        // Per-layer style id (e.g. "water", "road-primary"), parallel to _layerMaterials. Names each layer
        // GameObject after its style layer in the Hierarchy; empty/short ⇒ fall back to the material name.
        private readonly List<string>   _layerNames     = new List<string>();
        private readonly Dictionary<int, ItemRec>         _items      = new Dictionary<int, ItemRec>();
        private readonly Dictionary<TileId, ContainerRec> _containers = new Dictionary<TileId, ContainerRec>();

        private GameObject _root;
        private int  _nextHandle;
        private bool _disposed;

        // Last scene origin seen by Rebuild — identical blink-fix rationale to EntitiesTileRenderer: a
        // tile-layer is consumed AFTER the frame's Rebuild (MapView.Tick: InstancedRebuild → TileManager.Tick
        // → AddTileLayer), so without caching the origin a fresh container would be created at the world
        // origin and render there for one frame until the NEXT Rebuild repositioned it.
        private double2 _lastSceneOrigin;
        private bool    _hasSceneOrigin;

        public GameObjectTileRenderer(IReadOnlyList<Material> layerMaterials, IReadOnlyList<string> layerNames = null)
        {
            if (layerMaterials == null) throw new ArgumentNullException(nameof(layerMaterials));
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);
            if (layerNames != null)
                for (int i = 0; i < layerNames.Count; i++) _layerNames.Add(layerNames[i]);

            _root = new GameObject("MapTiles (GameObject backend)");
        }

        // ── Test / debug observability ──────────────────────────────────────────────────────────

        /// <summary>Number of currently registered draw items (layer GameObjects).</summary>
        public int DrawItemCount => _items.Count;

        /// <summary>Number of live tile containers (one per tile that has ≥1 layer).</summary>
        public int ContainerCount => _containers.Count;

        /// <summary>True once <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>The backend root's transform (null after dispose). Tests read the live Hierarchy through it.</summary>
        public Transform Root => _root != null ? _root.transform : null;

        /// <summary>The container transform for <paramref name="tileId"/>, or null if no live container.</summary>
        public Transform Container(TileId tileId)
            => !_disposed && _containers.TryGetValue(tileId, out var c) && c.Go != null ? c.Go.transform : null;

        /// <summary>
        /// World-space translation (X, Z) of the draw item's owning container, or (NaN, NaN) for an
        /// unknown/dead handle. The backend root sits at the world origin, so a container's position is its
        /// scene placement and a layer child (parented at the container origin) shares it. GPU-independent —
        /// reads the live transform. Mirrors the instanced backends' <c>GetInstanceTranslation</c> so the
        /// floating-origin tests are parallel.
        /// </summary>
        public (float x, float z) GetInstanceTranslation(int handle)
        {
            if (_disposed || !_items.TryGetValue(handle, out var item) || item.Go == null)
                return (float.NaN, float.NaN);
            Vector3 t = item.Go.transform.position;
            return (t.x, t.z);
        }

        /// <summary>
        /// XZ scene-space bounding box covering all live tile containers (each container's position, plus
        /// <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin). Used by tests to
        /// frame a camera that sees all tiles. Returns <c>default</c> when empty. Mirrors
        /// <see cref="EntitiesTileRenderer.ComputeSceneBounds"/>.
        /// </summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld)
        {
            if (_disposed || _containers.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var kv in _containers)
            {
                if (kv.Value.Go == null) continue;
                Vector3 p = kv.Value.Go.transform.position;
                if (p.x < minX) minX = p.x;
                if (p.x + tileSizeWorld > maxX) maxX = p.x + tileSizeWorld;
                if (p.z < minZ) minZ = p.z;
                if (p.z + tileSizeWorld > maxZ) maxZ = p.z + tileSizeWorld;
            }
            if (minX == float.MaxValue) return new Bounds(Vector3.zero, Vector3.zero);
            float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            return new Bounds(new Vector3(cx, 0f, cz), new Vector3(maxX - minX, 1f, maxZ - minZ));
        }

        // ── Draw item registration ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the scene-space position for <paramref name="tileOriginMerc"/> using the last
        /// <see cref="Rebuild"/> origin, or the world origin if no Rebuild has run yet (in which case the
        /// next Rebuild fixes it). Used to place freshly-created containers without an origin "blink".
        /// </summary>
        private float3 InitialScenePos(double2 tileOriginMerc)
            => _hasSceneOrigin ? FloatingOrigin.TileLocalToScene(tileOriginMerc, _lastSceneOrigin) : float3.zero;

        /// <summary>
        /// Returns the existing container for <paramref name="tileId"/>, or creates one parented under the
        /// backend root, named <c>"Tile z/x/y"</c> and positioned at the tile's current scene placement.
        /// </summary>
        private GameObject GetOrCreateContainer(TileId tileId, double2 tileOriginMerc)
        {
            if (_containers.TryGetValue(tileId, out var rec)) return rec.Go;

            var go = new GameObject($"Tile {tileId}");
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            float3 pos = InitialScenePos(tileOriginMerc);
            go.transform.localPosition = new Vector3(pos.x, pos.y, pos.z);

            _containers[tileId] = new ContainerRec { Go = go, TileOriginMerc = tileOriginMerc, ChildCount = 0 };
            return go;
        }

        /// <summary>
        /// Registers a tile-layer mesh as a child GameObject under its tile's container (created on demand).
        /// Returns a handle for later removal. <paramref name="materialIndex"/> indexes the flattened
        /// layer-material list (fills then lines), matching the instanced backends.
        /// </summary>
        public int AddTileLayer(Mesh mesh, double2 tileOriginMerc, int materialIndex, TileId tileId)
        {
            if (_disposed)    throw new ObjectDisposedException(nameof(GameObjectTileRenderer));
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if ((uint)materialIndex >= (uint)_layerMaterials.Count)
                throw new ArgumentOutOfRangeException(nameof(materialIndex));

            Material   mat       = _layerMaterials[materialIndex];
            GameObject container = GetOrCreateContainer(tileId, tileOriginMerc);

            // Name the child after its style layer ("water", "road-primary", …) so the Hierarchy reads
            // cleanly; fall back to the (shared) material name when no style id is available.
            string layerName = (uint)materialIndex < (uint)_layerNames.Count
                && !string.IsNullOrEmpty(_layerNames[materialIndex])
                    ? _layerNames[materialIndex]
                    : mat.name;

            var layerGo = new GameObject(layerName);
            layerGo.transform.SetParent(container.transform, worldPositionStays: false);
            layerGo.transform.localPosition = Vector3.zero;

            var mf = layerGo.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;                       // sharedMesh: assign, do not clone

            var mr = layerGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial    = mat;                 // sharedMaterial: reference the live layer material
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            var rec = _containers[tileId];
            rec.ChildCount++;
            _containers[tileId] = rec;

            int handle = _nextHandle++;
            _items[handle] = new ItemRec { Go = layerGo, TileId = tileId };
            return handle;
        }

        /// <summary>
        /// Destroys the draw item's layer GameObject, and destroys the owning tile container once its last
        /// layer is gone. The Mesh asset is NOT destroyed here — the caller (TileManager) owns the Mesh
        /// lifetime. Idempotent for unknown handles.
        /// </summary>
        public void RemoveItem(int handle)
        {
            if (_disposed) return;
            if (!_items.TryGetValue(handle, out var item)) return;

            DestroyGo(item.Go);
            _items.Remove(handle);

            if (_containers.TryGetValue(item.TileId, out var container))
            {
                container.ChildCount--;
                if (container.ChildCount <= 0)
                {
                    DestroyGo(container.Go);
                    _containers.Remove(item.TileId);
                }
                else
                {
                    _containers[item.TileId] = container;
                }
            }
        }

        // ── Per-frame rebuild ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Refreshes each tile container's <c>localPosition</c> from <paramref name="sceneOrigin"/>
        /// (origin ≡ look-at; camera-relative rendering). One transform write per tile, not per layer — the
        /// layer children sit at the container origin and move with it. Does no material work
        /// (<c>ZoomStyleApplier</c> mutates the shared materials live, same as the instanced backends).
        /// </summary>
        public void Rebuild(double2 sceneOrigin)
        {
            if (_disposed) return;

            // Cache so a tile-layer consumed later this frame (after this Rebuild) is created already
            // positioned, instead of blinking at the world origin for a frame.
            _lastSceneOrigin = sceneOrigin;
            _hasSceneOrigin  = true;

            foreach (var kv in _containers)
            {
                if (kv.Value.Go == null) continue;
                float3 pos = FloatingOrigin.TileLocalToScene(kv.Value.TileOriginMerc, sceneOrigin);
                kv.Value.Go.transform.localPosition = new Vector3(pos.x, pos.y, pos.z);
            }
        }

        // ── Teardown ────────────────────────────────────────────────────────────────────────────────

        private static void DestroyGo(GameObject go)
        {
            if (go == null) return;
            // Qualify Object: `using System;` (for the Argument*Exception types) makes a bare `Object`
            // ambiguous with System.Object.
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else                       UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>
        /// Destroys the backend root (and with it every container + layer child). Does NOT destroy Mesh
        /// assets — TileManager owns those. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _items.Clear();
            _containers.Clear();
            DestroyGo(_root); // destroys all containers + their layer children
            _root = null;
        }
    }
}
