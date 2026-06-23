using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.View;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S53b ECS render backend (internal, IDisposable) — the engine for <c>RenderBackend.Entities</c>.
    ///
    /// Each tile-layer draw item is an <see cref="Entity"/> rendered by Entities Graphics (which runs on
    /// <see cref="UnityEngine.Rendering.BatchRendererGroup"/> under the hood). Unlike the raw-BRG backend
    /// (<see cref="BrgTileRenderer"/>, which hand-packs a struct-of-arrays GraphicsBuffer), Entities
    /// Graphics owns the instance data: we create one entity per (tile, layer) carrying the layer's
    /// shared <see cref="Material"/> + the tile's <see cref="Mesh"/> + a <see cref="LocalToWorld"/>.
    /// The per-entity advantage is debuggability — each draw item is inspectable/disable-able in the
    /// Entities Hierarchy (the reason the project goes past raw BRG; see the S53 epic).
    ///
    /// Hierarchy: a tile's layer entities are <see cref="Parent"/>ed under one named root entity
    /// (<c>"Tile z/x/y"</c>) per tile, so the Entities Hierarchy shows a per-tile tree instead of a flat
    /// list. The root carries the <see cref="LocalTransform"/>; layer entities carry
    /// <see cref="LocalTransform.Identity"/>, so <c>LocalToWorldSystem</c> derives each layer's world
    /// matrix from its root — <see cref="Rebuild"/> writes one transform per tile, not per layer. The
    /// root is non-rendered and is destroyed once its last layer is removed.
    ///
    /// Styling is per-layer (the shared material, written each frame by <c>ZoomStyleApplier</c>) plus
    /// per-feature (vertex colours baked into the mesh), so no per-instance material-property override
    /// components are needed — Entities Graphics reads the live material. <paramref name="layerMaterials"/>
    /// is the flattened layer-material list (fills in declared order, then lines), so
    /// <c>materialIndex</c> matches <see cref="BrgTileRenderer.AddTileLayer"/>.
    ///
    /// World lifecycle: this owns a <see cref="World"/> created on construction (automatic bootstrap is
    /// disabled project-wide via <c>UNITY_DISABLE_AUTOMATIC_SYSTEM_BOOTSTRAP</c>, so this is the only
    /// world and we own its disposal). <see cref="Rebuild"/> must be called once per frame to refresh
    /// the floating-origin matrices and tick the Entities-Graphics systems; the GPU submission itself
    /// happens during the camera's render (SRP culling drives EG's BRG culling callback).
    ///
    /// Clean-room: design follows the Entities Graphics runtime-entity-creation documentation and the
    /// existing BRG backend's tile-origin math (<see cref="FloatingOrigin.TileLocalToScene"/>).
    /// </summary>
    internal sealed class EntitiesTileRenderer : IInstancedTileBackend
    {
        // One draw item = one layer entity (a child of its tile's root entity).
        private struct ItemRec
        {
            public Entity Entity;
            public TileId TileId;   // which tile root this layer hangs under
        }

        // One per live tile: the named parent entity its layer entities are grouped under, so the
        // Entities Hierarchy shows a per-tile tree instead of a flat list. Carries the tile's mercator
        // origin so Rebuild can reposition the whole subtree by writing only the root's transform.
        private struct RootRec
        {
            public Entity  Root;
            public double2 TileOriginMerc;
            public int     ChildCount;     // layer entities parented to this root; root dies when it hits 0
        }

        private readonly List<Material>             _layerMaterials = new List<Material>();
        private readonly Dictionary<int, ItemRec>   _items          = new Dictionary<int, ItemRec>();
        private readonly Dictionary<TileId, RootRec> _tileRoots      = new Dictionary<TileId, RootRec>();

        private World         _world;
        private EntityManager _em;
        private readonly World _prevDefaultWorld;
        private ComponentSystemBase _initGroup, _simGroup, _presGroup;
        private int  _nextHandle;
        private bool _disposed;

        // ── Profiler markers — split the per-frame EG drive so a MapView.Update spike is attributable ──
        // RootTransforms: the per-tile LocalTransform/LocalToWorld writes (scales with tile count).
        // InitGroup/SimGroup/PresGroup: the three system-group ticks. PresGroup runs EntitiesGraphicsSystem
        // (instance-data upload + BRG batch (re)registration) and is the usual culprit when tiles churn.
        private static readonly ProfilerMarker PmRootTransforms = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.ECS.RootTransforms");
        private static readonly ProfilerMarker PmInitGroup      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.ECS.InitGroup");
        private static readonly ProfilerMarker PmSimGroup       = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.ECS.SimGroup");
        private static readonly ProfilerMarker PmPresGroup      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.ECS.PresGroup");

        // ── AddTileLayer sub-phases (nested under MapRenderer.Tile.AddLayer) ──
        // The per-tile-load spike on the render thread is hypothesised to be EG batch registration. Split
        // AddTileLayer so the live profiler attributes the cost to its real source:
        //   Root     — GetOrCreateRoot (creates the tile-root entity on first layer of a tile).
        //   Register — new RenderMeshArray + RenderMeshUtility.AddComponents — the EG mesh/material batch
        //              registration. PRIME SUSPECT for the zoom stall (per-tile RenderMeshArray, see follow-ups).
        //   Parent   — the Parent+LocalTransform structural change + the LocalToWorld/bounds sets.
        private static readonly ProfilerMarker PmAddRoot     = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.AddLayer.Root");
        private static readonly ProfilerMarker PmAddRegister = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.AddLayer.Register");
        private static readonly ProfilerMarker PmAddParent   = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.AddLayer.Parent");

        // Last scene origin seen by Rebuild. A new tile-layer is consumed AFTER the frame's Rebuild
        // has already run (MapView.Tick: InstancedRebuild → TileManager.Tick → AddTileLayer), so without
        // this we'd create the entity at LocalToWorld.identity (world origin) and it would render there
        // for one frame until the NEXT Rebuild repositioned it — the zoom "blink in the corner". Caching
        // the origin lets AddTileLayer place the entity correctly the instant it is created.
        private double2 _lastSceneOrigin;
        private bool    _hasSceneOrigin;

        public EntitiesTileRenderer(IReadOnlyList<Material> layerMaterials)
        {
            if (layerMaterials == null) throw new ArgumentNullException(nameof(layerMaterials));
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);

            _world           = DefaultWorldInitialization.Initialize("MapEntitiesWorld", editorWorld: false);
            _prevDefaultWorld = World.DefaultGameObjectInjectionWorld;
            World.DefaultGameObjectInjectionWorld = _world; // Entities Graphics reads the default world.
            _em = _world.EntityManager;

            _initGroup = _world.GetExistingSystemManaged<InitializationSystemGroup>();
            _simGroup  = _world.GetExistingSystemManaged<SimulationSystemGroup>();
            _presGroup = _world.GetExistingSystemManaged<PresentationSystemGroup>(); // contains EntitiesGraphicsSystem
        }

        // ── Test / debug observability ──────────────────────────────────────────────────────────

        /// <summary>Number of currently registered draw items (layer entities).</summary>
        public int DrawItemCount => _items.Count;

        /// <summary>Number of live tile root entities (one per tile that has ≥1 layer).</summary>
        public int TileRootCount => _tileRoots.Count;

        /// <summary>True if a root entity is live for <paramref name="tileId"/>.</summary>
        public bool TileRootExists(TileId tileId)
            => !_disposed && _tileRoots.TryGetValue(tileId, out var r) && _em.Exists(r.Root);

        /// <summary>
        /// Number of layer entities the transform system has linked under <paramref name="tileId"/>'s root
        /// (read from the root's <see cref="Child"/> buffer, which <see cref="ParentSystem"/> maintains).
        /// Returns -1 if the root or its Child buffer does not exist yet (no Rebuild/tick has run). This is
        /// the structural proxy for "the Entities Hierarchy groups these layers under the tile."
        /// </summary>
        public int RootChildBufferCount(TileId tileId)
        {
            if (_disposed || !_tileRoots.TryGetValue(tileId, out var r) || !_em.Exists(r.Root)) return -1;
            if (!_em.HasComponent<Child>(r.Root)) return -1;
            return _em.GetBuffer<Child>(r.Root).Length;
        }

        /// <summary>True if the layer entity for <paramref name="handle"/> is parented to the root of
        /// <paramref name="tileId"/> (via its <see cref="Parent"/> component).</summary>
        public bool IsParentedToTileRoot(int handle, TileId tileId)
        {
            if (_disposed || !_items.TryGetValue(handle, out var item) || !_em.Exists(item.Entity)) return false;
            if (!_tileRoots.TryGetValue(tileId, out var r) || !_em.HasComponent<Parent>(item.Entity)) return false;
            return _em.GetComponentData<Parent>(item.Entity).Value == r.Root;
        }

#if UNITY_EDITOR
        /// <summary>Editor-only: the debug name assigned to the tile root, e.g. <c>"Tile 14/8192/5461"</c>.</summary>
        public string GetTileRootName(TileId tileId)
            => _tileRoots.TryGetValue(tileId, out var r) && _em.Exists(r.Root) ? _em.GetName(r.Root) : null;
#endif

        /// <summary>True once <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>True if the draw item <paramref name="handle"/> still has a live entity.</summary>
        public bool EntityExists(int handle)
            => !_disposed && _items.TryGetValue(handle, out var rec) && _em.Exists(rec.Entity);

        /// <summary>
        /// Returns the world-space translation (X, Z) of the draw item's entity from the last
        /// <see cref="Rebuild"/>. GPU-independent — reads the entity's <see cref="LocalToWorld"/>.
        /// Returns (NaN, NaN) for an unknown/dead handle.
        /// </summary>
        public (float x, float z) GetInstanceTranslation(int handle)
        {
            if (_disposed || !_items.TryGetValue(handle, out var rec) || !_em.Exists(rec.Entity))
                return (float.NaN, float.NaN);
            float3 t = _em.GetComponentData<LocalToWorld>(rec.Entity).Position;
            return (t.x, t.z);
        }

        /// <summary>
        /// XZ scene-space bounding box covering all live tile entities (each entity's
        /// <see cref="LocalToWorld"/> translation, plus <paramref name="tileSizeWorld"/> for the tile's
        /// mesh extent beyond its origin). Used by tests to frame a camera that sees all entities (there
        /// are no child GameObjects to bound). Returns <c>default</c> when empty. Mirrors
        /// <see cref="BrgTileRenderer.ComputeSceneBounds"/>.
        /// </summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld)
        {
            if (_disposed || _items.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var kv in _items)
            {
                if (!_em.Exists(kv.Value.Entity)) continue;
                float3 p = _em.GetComponentData<LocalToWorld>(kv.Value.Entity).Position;
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
        /// next Rebuild fixes it). Used to place freshly-created entities without an origin "blink".
        /// </summary>
        private float3 InitialScenePos(double2 tileOriginMerc)
            => _hasSceneOrigin ? FloatingOrigin.TileLocalToScene(tileOriginMerc, _lastSceneOrigin) : float3.zero;

#if UNITY_EDITOR
        // FixedString64Bytes holds ≤61 UTF-8 bytes and its string ctor THROWS on overflow — truncate
        // defensively (entity names are an editor-only debug aid for the Entities Hierarchy).
        private static FixedString64Bytes ToEntityName(string s)
        {
            if (string.IsNullOrEmpty(s)) return default;
            if (s.Length > 48) s = s.Substring(0, 48);
            return new FixedString64Bytes(s);
        }
#endif

        /// <summary>
        /// Returns the existing root entity for <paramref name="tileId"/>, or creates one. The root is a
        /// non-rendered, named (<c>"Tile z/x/y"</c>) parent carrying <see cref="LocalTransform"/> +
        /// <see cref="LocalToWorld"/>; its layer entities hang off it via <see cref="Parent"/> so the
        /// Entities Hierarchy groups them. Rebuild moves the whole tile by writing only this transform.
        /// </summary>
        private Entity GetOrCreateRoot(TileId tileId, double2 tileOriginMerc)
        {
            if (_tileRoots.TryGetValue(tileId, out var rec)) return rec.Root;

            float3 pos  = InitialScenePos(tileOriginMerc);
            Entity root = _em.CreateEntity();
            _em.AddComponentData(root, LocalTransform.FromPosition(pos));
            _em.AddComponentData(root, new LocalToWorld { Value = float4x4.Translate(pos) });
#if UNITY_EDITOR
            _em.SetName(root, ToEntityName($"Tile {tileId}"));
#endif
            _tileRoots[tileId] = new RootRec { Root = root, TileOriginMerc = tileOriginMerc, ChildCount = 0 };
            return root;
        }

        /// <summary>
        /// Registers a tile-layer mesh as an Entities-Graphics entity, parented under its tile's root
        /// entity (created on demand). Returns a handle for later removal. <paramref name="materialIndex"/>
        /// indexes the flattened layer-material list (fills then lines), matching
        /// <see cref="BrgTileRenderer.AddTileLayer"/>.
        /// </summary>
        public int AddTileLayer(Mesh mesh, double2 tileOriginMerc, int materialIndex, TileId tileId)
        {
            if (_disposed)    throw new ObjectDisposedException(nameof(EntitiesTileRenderer));
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if ((uint)materialIndex >= (uint)_layerMaterials.Count)
                throw new ArgumentOutOfRangeException(nameof(materialIndex));

            Material mat  = _layerMaterials[materialIndex];
            Entity   root;
            using (PmAddRoot.Auto())
                root = GetOrCreateRoot(tileId, tileOriginMerc);

            Entity e;
            using (PmAddRegister.Auto())
            {
                var desc = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
                var rma  = new RenderMeshArray(new Material[] { mat }, new Mesh[] { mesh });

                e = _em.CreateEntity();
                RenderMeshUtility.AddComponents(
                    e, _em, desc, rma, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            }

            using (PmAddParent.Auto())
            {
            // Parent under the tile root with an identity local transform: LocalToWorldSystem then
            // computes this entity's LocalToWorld = root.LocalToWorld each Rebuild tick. (RenderMeshUtility
            // adds LocalToWorld but NOT Parent/LocalTransform — we add those so the transform system drives us.)
            // Add both in ONE structural change (ComponentTypeSet) instead of two separate AddComponentData
            // calls: each add migrates the entity to a new archetype/chunk, and tile churn during a zoom makes
            // that per-entity cost a measured spike (MapRenderer.Tile.AddLayer). One migration, then set values.
            _em.AddComponent(e, new ComponentTypeSet(ComponentType.ReadWrite<Parent>(), ComponentType.ReadWrite<LocalTransform>()));
            _em.SetComponentData(e, new Parent { Value = root });
            _em.SetComponentData(e, LocalTransform.Identity);

            // Set LocalToWorld directly for the frame BEFORE the first transform tick (the consume happens
            // after this frame's Rebuild), so the tile renders at the right place immediately — no origin
            // blink. The next Rebuild's LocalToWorldSystem re-derives the identical value from the root.
            // (LocalToWorld is already present from RenderMeshUtility.AddComponents, so this is a set, not a
            // migration.)
            _em.SetComponentData(e, new LocalToWorld { Value = float4x4.Translate(InitialScenePos(tileOriginMerc)) });

            // Generous bounds: floating-origin keeps tiles near the origin and the camera frames them,
            // so we never want frustum culling to silently drop a tile in a headless single-shot render.
            if (_em.HasComponent<RenderBounds>(e))
                _em.SetComponentData(e, new RenderBounds
                {
                    Value = new AABB { Center = float3.zero, Extents = new float3(1e6f) }
                });
#if UNITY_EDITOR
            _em.SetName(e, ToEntityName(mat.name));
#endif
            }

            var rec = _tileRoots[tileId];
            rec.ChildCount++;
            _tileRoots[tileId] = rec;

            int handle = _nextHandle++;
            _items[handle] = new ItemRec { Entity = e, TileId = tileId };
            return handle;
        }

        /// <summary>
        /// Destroys the draw item's layer entity, and destroys the owning tile root once its last layer
        /// is gone (so an evicted tile leaves no empty node in the Hierarchy). The Mesh asset is NOT
        /// destroyed here — the caller (TileManager) owns the Mesh lifetime. Idempotent for unknown handles.
        /// </summary>
        public void RemoveItem(int handle)
        {
            if (_disposed) return;
            if (!_items.TryGetValue(handle, out var item)) return;

            if (_em.Exists(item.Entity)) _em.DestroyEntity(item.Entity);
            _items.Remove(handle);

            if (_tileRoots.TryGetValue(item.TileId, out var root))
            {
                root.ChildCount--;
                if (root.ChildCount <= 0)
                {
                    if (_em.Exists(root.Root)) _em.DestroyEntity(root.Root);
                    _tileRoots.Remove(item.TileId);
                }
                else
                {
                    _tileRoots[item.TileId] = root;
                }
            }
        }

        // ── Per-frame rebuild ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Refreshes each tile ROOT's transform from <paramref name="sceneOrigin"/> (origin ≡ look-at;
        /// camera-relative rendering) and ticks the Entities-Graphics systems so the instance data is
        /// uploaded before the camera renders. Must be called once per frame on the Entities backend.
        ///
        /// Only the per-tile root transforms are written here (one write per tile, not per layer); the
        /// ticked <c>TransformSystemGroup</c> (<see cref="ParentSystem"/> → <see cref="LocalToWorldSystem"/>,
        /// both under <see cref="SimulationSystemGroup"/>) then derives every child layer entity's
        /// <see cref="LocalToWorld"/> from its root. Both root <see cref="LocalTransform"/> and
        /// <see cref="LocalToWorld"/> are set so reads (bounds / translation probes) are correct even
        /// before the tick.
        ///
        /// Steady-state allocation-free at the managed level: the refresh is a struct-enumerator loop over
        /// the root map with in-place <c>SetComponentData</c> (no structural change). Entities Graphics'
        /// own system update uses native/temp allocations, not managed GC.
        /// </summary>
        public void Rebuild(double2 sceneOrigin)
        {
            if (_disposed) return;

            // Cache so a tile-layer consumed later this frame (after this Rebuild) can be created
            // already positioned, instead of blinking at the world origin for a frame.
            _lastSceneOrigin = sceneOrigin;
            _hasSceneOrigin  = true;

            using (PmRootTransforms.Auto())
            {
                foreach (var kv in _tileRoots)
                {
                    Entity root = kv.Value.Root;
                    if (!_em.Exists(root)) continue;
                    float3 pos = FloatingOrigin.TileLocalToScene(kv.Value.TileOriginMerc, sceneOrigin);
                    _em.SetComponentData(root, LocalTransform.FromPosition(pos));
                    _em.SetComponentData(root, new LocalToWorld { Value = float4x4.Translate(pos) });
                }
            }

            // Drive the Entities-Graphics systems (no automatic player-loop tick — bootstrap disabled).
            // SimulationSystemGroup contains TransformSystemGroup (parents → child LocalToWorld);
            // PresentationSystemGroup contains EntitiesGraphicsSystem (uploads instance data + registers
            // the BRG batch); the actual draw is emitted during the camera's render via SRP culling.
            // Markered separately so the profiler shows which group owns a per-frame spike.
            using (PmInitGroup.Auto())
                _initGroup?.Update();
            using (PmSimGroup.Auto())
                _simGroup?.Update();
            using (PmPresGroup.Auto())
                _presGroup?.Update();
        }

        // ── Teardown ────────────────────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _items.Clear();
            _tileRoots.Clear();
            if (_world != null && _world.IsCreated)
            {
                if (World.DefaultGameObjectInjectionWorld == _world)
                    World.DefaultGameObjectInjectionWorld = _prevDefaultWorld;
                _world.Dispose(); // destroys all entities + the EG world state
            }
            _world = null;
        }
    }
}
