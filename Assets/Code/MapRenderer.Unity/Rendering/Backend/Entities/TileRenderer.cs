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
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.View;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Backend.Entities
{
    /// <summary>
    /// S53b ECS render backend (internal, IDisposable) — the engine for <c>RenderBackend.Entities</c>.
    ///
    /// Each tile-layer draw item is an <see cref="Entity"/> rendered by Entities Graphics (which runs on
    /// <see cref="UnityEngine.Rendering.BatchRendererGroup"/> under the hood). Unlike the raw-BRG backend
    /// (<see cref="Backend.BRG.TileRenderer"/>, which hand-packs a struct-of-arrays GraphicsBuffer), Entities
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
    /// is the FULL-WIDTH, global-draw-slot-aligned material list (§3.3), so <c>materialIndex</c> matches
    /// <see cref="Backend.BRG.TileRenderer.AddTileLayer"/>.
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
    internal sealed class TileRenderer : VerifiedDisposable, ITileRenderBackend
    {
        // One draw item = one layer entity (a child of its tile's root entity).
        private struct ItemRec
        {
            public Entity      Entity;
            public TileId      TileId;   // which tile root this layer hangs under
            public BatchMeshID MeshId;   // stall #3: the EG-registered mesh id, for UnregisterMesh on removal
        }

        // One per live tile: the named parent entity its layer entities are grouped under, so the
        // Entities Hierarchy shows a per-tile tree instead of a flat list. Carries the tile's projected
        // SW-corner render origin so Rebuild can reposition the whole subtree by writing only the root's transform.
        private struct RootRec
        {
            public Entity  Root;
            public double3 TileOriginRender;
            public int     ChildCount;     // layer entities parented to this root; root dies when it hits 0
        }

        private readonly List<Material>             _layerMaterials = new List<Material>();
        // Per-layer style id (e.g. "water", "road-primary"), parallel to _layerMaterials. Editor-only debug
        // aid: it names each layer entity after its style layer in the Entities Hierarchy (matching the old
        // GameObject backend) instead of the shared material name ("MapView_Fill"). Empty ⇒ fall back to name.
        private readonly List<string>               _layerNames     = new List<string>();
        private readonly Dictionary<int, ItemRec>   _items          = new Dictionary<int, ItemRec>();
        private readonly Dictionary<TileId, RootRec> _tileRoots      = new Dictionary<TileId, RootRec>();

        // Stall #2: reused scratch for one RemoveItems() batch — the record's layer entities plus any tile
        // root the batch empties, destroyed in ONE EntityManager.DestroyEntity(NativeArray) structural change
        // instead of one per layer. Persistent (reused every release); disposed in DoDispose.
        private NativeList<Entity> _destroyScratch;

        // ── Stall #3: ID-based layer creation (avoid the per-entity RenderMeshArray) ──────────────────
        // EG's ID route: register each layer material ONCE + each mesh on add, and point the entity at them
        // via MaterialMeshInfo.FromMeshIDAndMaterialID — no fresh one-element RenderMeshArray shared component
        // (and its batch registration) per consumed mesh. Layer entities are Instantiated from a single
        // prototype so they all share ONE archetype (no per-entity structural migration for the render set).
        private EntitiesGraphicsSystem                 _eg;             // from `using Unity.Rendering` — NOT qualified (Unity.Rendering collides with MapRenderer.Unity.Rendering)
        private BatchMaterialID[]                      _materialIds;   // one per layer material, registered once
        private Entity                                 _layerPrototype; // Prefab-tagged; Instantiated per layer
        private Mesh                                   _prototypeMesh;  // inert placeholder mesh for the prototype's RenderMeshArray

        /// <summary>Stall #3 tooth: RenderMeshArray shared components created (the prototype's ONE). The old
        /// path created one per AddTileLayer; this must stay ≤1 no matter how many layers are added.</summary>
        internal int RenderMeshArraysCreated { get; private set; }

        /// <summary>Stall #3 tooth: live EG-registered meshes (inc on RegisterMesh in AddTileLayer, dec on
        /// UnregisterMesh in RemoveItem/RemoveItems). Must return to 0 after a full load→release (incl. the
        /// prepared-cache round-trip) — catches the ID route's missing-unregister leak trap.</summary>
        internal int RegisteredMeshCount { get; private set; }

        private World         _world;
        private EntityManager _em;
        private readonly World _prevDefaultWorld;
        private ComponentSystemBase _initGroup, _simGroup, _presGroup;
        private int  _nextHandle;

        // ── Profiler markers — split the per-frame EG drive so a per-frame spike is attributable ──
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

        // Last scene frame seen by Rebuild. AddTileLayer may be called AFTER Rebuild within the same frame,
        // so without caching the frame we'd create the entity at LocalToWorld.identity (world origin) and it
        // would render there for one frame until the NEXT Rebuild repositioned it — the zoom "blink in the
        // corner". Caching the frame lets AddTileLayer place the entity correctly the instant it is created.
        private SceneFrame _lastFrame;
        private bool       _hasSceneOrigin;

        /// <param name="layerNames">
        /// Optional per-layer style ids parallel to <paramref name="layerMaterials"/>, used only to name the
        /// layer entities in the Editor's Entities Hierarchy. When null/short, the material name is used.
        /// </param>
        public TileRenderer(IReadOnlyList<Material> layerMaterials, IReadOnlyList<string> layerNames = null)
        {
            if (layerMaterials == null) throw new ArgumentNullException(nameof(layerMaterials));
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);
            if (layerNames != null)
                for (int i = 0; i < layerNames.Count; i++) _layerNames.Add(layerNames[i]);

            _world           = DefaultWorldInitialization.Initialize("MapEntitiesWorld", editorWorld: false);
            _prevDefaultWorld = World.DefaultGameObjectInjectionWorld;
            World.DefaultGameObjectInjectionWorld = _world; // Entities Graphics reads the default world.
            _em = _world.EntityManager;
            _destroyScratch = new NativeList<Entity>(64, Allocator.Persistent);

            _initGroup = _world.GetExistingSystemManaged<InitializationSystemGroup>();
            _simGroup  = _world.GetExistingSystemManaged<SimulationSystemGroup>();
            _presGroup = _world.GetExistingSystemManaged<PresentationSystemGroup>(); // contains EntitiesGraphicsSystem

            // Stall #3: register each layer material ONCE with EG (stable BatchMaterialID); meshes register
            // per-add. Then build the single layer prototype every AddTileLayer instantiates.
            _eg = _world.GetExistingSystemManaged<EntitiesGraphicsSystem>();
            _materialIds = new BatchMaterialID[_layerMaterials.Count];
            for (int i = 0; i < _layerMaterials.Count; i++)
                _materialIds[i] = _layerMaterials[i] != null ? _eg.RegisterMaterial(_layerMaterials[i]) : default;
            BuildLayerPrototype();
        }

        /// <summary>
        /// Stall #3: builds the single layer-entity PROTOTYPE. <see cref="RenderMeshUtility.AddComponents"/>
        /// stamps EG's full render component set (LocalToWorld, RenderBounds, MaterialMeshInfo, and the
        /// RenderMeshArray shared component); we add Parent/LocalTransform (the transform hierarchy) and Prefab
        /// (so the prototype itself never renders and is skipped by EG's queries). Every AddTileLayer
        /// Instantiates this — instances share the prototype's archetype AND its single (inert, ID-overridden)
        /// RenderMeshArray, so no per-entity array or structural migration is created. The placeholder mesh is
        /// empty and never drawn (Prefab); it exists only because AddComponents requires a RenderMeshArray.
        /// E1: seeds the RenderMeshArray with the first NON-null material — slot 0 may be a
        /// symbol/background layer (null Material, §3.3) that AddTileLayer is never called for.
        /// </summary>
        private void BuildLayerPrototype()
        {
            int seedIndex = -1;
            for (int i = 0; i < _layerMaterials.Count; i++)
                if (_layerMaterials[i] != null) { seedIndex = i; break; }
            if (seedIndex < 0) return; // no tile-mesh layers → AddTileLayer never called; no prototype needed

            _prototypeMesh = new Mesh { name = "MapLayerPrototype(inert)" };
            var desc = new RenderMeshDescription(ShadowCastingMode.Off, receiveShadows: false);
            var rma  = new RenderMeshArray(new Material[] { _layerMaterials[seedIndex] }, new Mesh[] { _prototypeMesh });

            _layerPrototype = _em.CreateEntity();
            RenderMeshUtility.AddComponents(
                _layerPrototype, _em, desc, rma, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            _em.AddComponent(_layerPrototype,
                new ComponentTypeSet(ComponentType.ReadWrite<Parent>(), ComponentType.ReadWrite<LocalTransform>()));
            _em.AddComponent<Prefab>(_layerPrototype); // exclude prototype from rendering/queries; Instantiate strips it
            RenderMeshArraysCreated = 1; // ONLY the prototype's — instances share it, none created per layer
        }

        // ── Test / debug observability ──────────────────────────────────────────────────────────

        /// <summary>Number of currently registered draw items (layer entities).</summary>
        public int DrawItemCount => _items.Count;

        /// <summary>Number of live tile root entities (one per tile that has ≥1 layer).</summary>
        public int TileRootCount => _tileRoots.Count;

        /// <summary>Stall #2 tooth: number of batched DestroyEntity structural changes performed by the LAST
        /// <see cref="RemoveItems"/> call (0 or 1 — the whole batch is one structural change). A shallow
        /// loop-over-<see cref="RemoveItem"/> implementation leaves this 0.</summary>
        internal int DestroyEntityBatchesLastRemove { get; private set; }

        /// <summary>Stall #2 tooth: entities destroyed by the last <see cref="RemoveItems"/> batch (the record's
        /// layer entities plus any tile root the batch emptied).</summary>
        internal int EntitiesDestroyedLastRemove { get; private set; }

        /// <summary>True if a root entity is live for <paramref name="tileId"/>.</summary>
        public bool TileRootExists(TileId tileId)
            => !IsDisposed && _tileRoots.TryGetValue(tileId, out var r) && _em.Exists(r.Root);

        /// <summary>
        /// Number of layer entities the transform system has linked under <paramref name="tileId"/>'s root
        /// (read from the root's <see cref="Child"/> buffer, which <see cref="ParentSystem"/> maintains).
        /// Returns -1 if the root or its Child buffer does not exist yet (no Rebuild/tick has run). This is
        /// the structural proxy for "the Entities Hierarchy groups these layers under the tile."
        /// </summary>
        public int RootChildBufferCount(TileId tileId)
        {
            if (IsDisposed || !_tileRoots.TryGetValue(tileId, out var r) || !_em.Exists(r.Root)) return -1;
            if (!_em.HasComponent<Child>(r.Root)) return -1;
            return _em.GetBuffer<Child>(r.Root).Length;
        }

        /// <summary>True if the layer entity for <paramref name="handle"/> is parented to the root of
        /// <paramref name="tileId"/> (via its <see cref="Parent"/> component).</summary>
        public bool IsParentedToTileRoot(int handle, TileId tileId)
        {
            if (IsDisposed || !_items.TryGetValue(handle, out var item) || !_em.Exists(item.Entity)) return false;
            if (!_tileRoots.TryGetValue(tileId, out var r) || !_em.HasComponent<Parent>(item.Entity)) return false;
            return _em.GetComponentData<Parent>(item.Entity).Value == r.Root;
        }

#if UNITY_EDITOR
        /// <summary>Editor-only: the debug name assigned to the tile root, e.g. <c>"Tile 14/8192/5461"</c>.</summary>
        public string GetTileRootName(TileId tileId)
            => _tileRoots.TryGetValue(tileId, out var r) && _em.Exists(r.Root) ? _em.GetName(r.Root) : null;

        /// <summary>Editor-only: the debug name assigned to a layer entity — its style layer id (e.g. "water").</summary>
        public string GetLayerEntityName(int handle)
            => _items.TryGetValue(handle, out var rec) && _em.Exists(rec.Entity) ? _em.GetName(rec.Entity) : null;
#endif

        // IsDisposed is inherited from VerifiedDisposable (public there too — no shadow needed).

        /// <summary>True if the draw item <paramref name="handle"/> still has a live entity.</summary>
        public bool EntityExists(int handle)
            => !IsDisposed && _items.TryGetValue(handle, out var rec) && _em.Exists(rec.Entity);

        /// <summary>
        /// Returns the world-space translation (X, Z) of the draw item's entity from the last
        /// <see cref="Rebuild"/>. GPU-independent — reads the entity's <see cref="LocalToWorld"/>.
        /// Returns (NaN, NaN) for an unknown/dead handle.
        /// </summary>
        public (float x, float z) GetInstanceTranslation(int handle)
        {
            if (IsDisposed || !_items.TryGetValue(handle, out var rec) || !_em.Exists(rec.Entity))
                return (float.NaN, float.NaN);
            float3 t = _em.GetComponentData<LocalToWorld>(rec.Entity).Position;
            return (t.x, t.z);
        }

        /// <summary>
        /// The draw item entity's local <see cref="RenderBounds"/> (the AABB EG frustum-culls against,
        /// before <see cref="LocalToWorld"/>). Test observability for the "tile culled in Game view"
        /// regression: it must ENCLOSE the mesh, not the old fixed { Center=0, Extents=1e6 } box.
        /// Returns two NaN float3s for an unknown/dead handle.
        /// </summary>
        internal (float3 center, float3 extents) GetRenderBoundsLocal(int handle)
        {
            if (IsDisposed || !_items.TryGetValue(handle, out var rec)
                || !_em.Exists(rec.Entity) || !_em.HasComponent<RenderBounds>(rec.Entity))
                return (new float3(float.NaN), new float3(float.NaN));
            AABB b = _em.GetComponentData<RenderBounds>(rec.Entity).Value;
            return (b.Center, b.Extents);
        }

        /// <summary>
        /// XZ scene-space bounding box covering all live tile entities (each entity's
        /// <see cref="LocalToWorld"/> translation, plus <paramref name="tileSizeWorld"/> for the tile's
        /// mesh extent beyond its origin). Used by tests to frame a camera that sees all entities (there
        /// are no child GameObjects to bound). Returns <c>default</c> when empty. Mirrors
        /// <see cref="Backend.BRG.TileRenderer.ComputeSceneBounds"/>.
        /// </summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld)
        {
            if (IsDisposed || _items.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);

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
        /// Returns the scene-space position for <paramref name="tileOriginRender"/> using the last
        /// <see cref="Rebuild"/> frame, or the world origin if no Rebuild has run yet (in which case the
        /// next Rebuild fixes it). Used to place freshly-created entities without an origin "blink".
        /// </summary>
        private float3 InitialScenePos(double3 tileOriginRender)
            => _hasSceneOrigin
                ? FloatingOrigin.TileToSceneRebased(tileOriginRender, _lastFrame.SceneOriginRender, _lastFrame.Rebase)
                : float3.zero;

        /// <summary>The root orientation for a freshly-created tile — the last frame's rebase rotation
        /// (identity for Mercator), or identity if no Rebuild has run yet.</summary>
        private quaternion InitialSceneRot()
            => _hasSceneOrigin ? new quaternion(_lastFrame.Rebase) : quaternion.identity;

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
        private Entity GetOrCreateRoot(TileId tileId, double3 tileOriginRender)
        {
            if (_tileRoots.TryGetValue(tileId, out var rec)) return rec.Root;

            float3     pos = InitialScenePos(tileOriginRender);
            quaternion rot = InitialSceneRot(); // identity for Mercator; per-frame rebase for the globe
            Entity root = _em.CreateEntity();
            _em.AddComponentData(root, LocalTransform.FromPositionRotation(pos, rot));
            _em.AddComponentData(root, new LocalToWorld { Value = float4x4.TRS(pos, rot, new float3(1f)) });
#if UNITY_EDITOR
            _em.SetName(root, ToEntityName($"Tile {tileId}"));
#endif
            _tileRoots[tileId] = new RootRec { Root = root, TileOriginRender = tileOriginRender, ChildCount = 0 };
            return root;
        }

        /// <summary>
        /// Registers a tile-layer mesh as an Entities-Graphics entity, parented under its tile's root
        /// entity (created on demand). Returns a handle for later removal. <paramref name="materialIndex"/>
        /// is the layer's global draw slot, indexing the full-width material list, matching
        /// <see cref="Backend.BRG.TileRenderer.AddTileLayer"/>; non-tile-mesh slots are null and never
        /// receive this call.
        /// </summary>
        public int AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId)
        {
            ThrowIfDisposed();
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if ((uint)materialIndex >= (uint)_layerMaterials.Count)
                throw new ArgumentOutOfRangeException(nameof(materialIndex));

            Material mat  = _layerMaterials[materialIndex];
            Entity   root;
            using (PmAddRoot.Auto())
                root = GetOrCreateRoot(tileId, tileOriginRender);

            Entity      e;
            BatchMeshID meshId;
            using (PmAddRegister.Auto())
            {
                // Stall #3 ID route: Instantiate the shared-archetype prototype (ONE structural op, no fresh
                // RenderMeshArray shared component / batch registration per layer), register the mesh with EG,
                // and point the entity at (meshId, materialId). This replaces the old per-entity
                // CreateEntity + RenderMeshUtility.AddComponents(new RenderMeshArray(...)) — the "prime suspect".
                e      = _em.Instantiate(_layerPrototype);
                meshId = _eg.RegisterMesh(mesh);
                RegisteredMeshCount++;
                // ID-based MaterialMeshInfo (ctor (materialID, meshID) in this EG version — no
                // FromMeshIDAndMaterialID factory) → EG batches by the registered ids, ignoring the inert
                // RenderMeshArray the instance carries from the prototype.
                _em.SetComponentData(e, new MaterialMeshInfo(_materialIds[materialIndex], meshId));
            }

            using (PmAddParent.Auto())
            {
            // Parent + LocalTransform already exist on the instance (copied from the prototype's archetype by
            // Instantiate), so these are pure SetComponentData — no per-entity archetype migration (the old
            // ComponentTypeSet add was the measured MapRenderer.Tile.AddLayer spike). LocalToWorldSystem then
            // computes this entity's LocalToWorld = root.LocalToWorld each Rebuild tick.
            _em.SetComponentData(e, new Parent { Value = root });
            _em.SetComponentData(e, LocalTransform.Identity);

            // Set LocalToWorld directly for the frame BEFORE the first transform tick (the consume happens
            // after this frame's Rebuild), so the tile renders at the right place immediately — no origin
            // blink. The next Rebuild's LocalToWorldSystem re-derives the identical value from the root.
            // (LocalToWorld is already present from RenderMeshUtility.AddComponents, so this is a set, not a
            // migration.) Identity rebase (Mercator) ⇒ TRS == Translate — bit-for-bit the pre-S91 placement.
            _em.SetComponentData(e, new LocalToWorld
            {
                Value = float4x4.TRS(InitialScenePos(tileOriginRender), InitialSceneRot(), new float3(1f))
            });

            // RenderBounds drives EG frustum culling (WorldRenderBounds = this × LocalToWorld). It MUST
            // enclose the mesh: the builders compute a tight, correctly-centred AABB in the same
            // origin-relative frame as the vertices (StyledFillTileBuilder / StyledLineTileBuilder), so we
            // mirror mesh.bounds. The old fixed { Center=0, Extents=1e6 } box was both undersized and
            // off-centre once render units are ECEF metres: at low zoom a tile spans several 1e6 m
            // (Mercator z3≈5e6, globe z0–1 out to R≈6.4e6), so the box hugged one corner and EG culled the
            // whole tile whenever that corner left the frustum — tiles vanished in the Game view (but not
            // Scene view, a wider frustum) at exactly those zooms. A rotation in LocalToWorld only inflates
            // the world AABB (conservative). Fallback: a mesh that arrives with degenerate (zero-size)
            // bounds keeps the generous never-cull box rather than being culled-always.
            if (_em.HasComponent<RenderBounds>(e))
            {
                Bounds mb = mesh.bounds;
                float3 center = new float3(mb.center.x, mb.center.y, mb.center.z);   // Unity Bounds → math at the boundary
                float3 half   = new float3(mb.extents.x, mb.extents.y, mb.extents.z);
                AABB aabb = math.any(half > 0f)
                    ? new AABB { Center = center, Extents = half }
                    : new AABB { Center = float3.zero, Extents = new float3(1e6f) };
                _em.SetComponentData(e, new RenderBounds { Value = aabb });
            }
#if UNITY_EDITOR
            // Name the entity after its style layer ("water", "road-primary", …) so the Entities Hierarchy
            // reads like the old GameObject backend; fall back to the (shared) material name if unavailable.
            string layerName = (uint)materialIndex < (uint)_layerNames.Count
                && !string.IsNullOrEmpty(_layerNames[materialIndex])
                    ? _layerNames[materialIndex]
                    : mat.name;
            _em.SetName(e, ToEntityName(layerName));
#endif
            }

            var rec = _tileRoots[tileId];
            rec.ChildCount++;
            _tileRoots[tileId] = rec;

            int handle = _nextHandle++;
            _items[handle] = new ItemRec { Entity = e, TileId = tileId, MeshId = meshId };
            return handle;
        }

        /// <summary>
        /// Destroys the draw item's layer entity, and destroys the owning tile root once its last layer
        /// is gone (so an evicted tile leaves no empty node in the Hierarchy). The Mesh asset is NOT
        /// destroyed here — the caller (TileManager) owns the Mesh lifetime. Idempotent for unknown handles.
        /// </summary>
        public void RemoveItem(int handle)
        {
            if (IsDisposed) return;
            if (!_items.TryGetValue(handle, out var item)) return;

            if (_em.Exists(item.Entity)) _em.DestroyEntity(item.Entity);
            _items.Remove(handle);
            _eg.UnregisterMesh(item.MeshId); // stall #3: the ID route requires an explicit unregister
            RegisteredMeshCount--;

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

        /// <summary>
        /// Stall #2: removes a whole record's layer entities (and any tile root the batch empties) in ONE
        /// <c>EntityManager.DestroyEntity(NativeArray&lt;Entity&gt;)</c> structural change instead of L+1 — the
        /// per-layer structural-change cost was the release-storm spike. Same bookkeeping as
        /// <see cref="RemoveItem"/> (child-count decrement, root-dies-at-0), just collected then destroyed once.
        /// Idempotent for unknown handles. The Mesh assets are NOT destroyed here — TileManager owns them.
        /// </summary>
        public void RemoveItems(ReadOnlySpan<int> handles)
        {
            DestroyEntityBatchesLastRemove = 0;
            EntitiesDestroyedLastRemove    = 0;
            if (IsDisposed) return;

            _destroyScratch.Clear();
            for (int i = 0; i < handles.Length; i++)
            {
                if (!_items.TryGetValue(handles[i], out var item)) continue; // idempotent unknown handle
                if (_em.Exists(item.Entity)) _destroyScratch.Add(item.Entity);
                _items.Remove(handles[i]);
                _eg.UnregisterMesh(item.MeshId); // stall #3: ID route requires explicit unregister
                RegisteredMeshCount--;

                if (_tileRoots.TryGetValue(item.TileId, out var root))
                {
                    root.ChildCount--;
                    if (root.ChildCount <= 0)
                    {
                        if (_em.Exists(root.Root)) _destroyScratch.Add(root.Root);
                        _tileRoots.Remove(item.TileId);
                    }
                    else
                    {
                        _tileRoots[item.TileId] = root;
                    }
                }
            }

            if (_destroyScratch.Length > 0)
            {
                _em.DestroyEntity(_destroyScratch.AsArray());
                DestroyEntityBatchesLastRemove = 1;
                EntitiesDestroyedLastRemove    = _destroyScratch.Length;
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
        public void Rebuild(in SceneFrame frame)
        {
            if (IsDisposed) return;

            // Cache so a tile-layer consumed later this frame (after this Rebuild) can be created
            // already positioned, instead of blinking at the world origin for a frame.
            _lastFrame      = frame;
            _hasSceneOrigin = true;

            quaternion rot = new quaternion(frame.Rebase); // same orientation for every tile (identity for Mercator)
            using (PmRootTransforms.Auto())
            {
                foreach (var kv in _tileRoots)
                {
                    Entity root = kv.Value.Root;
                    if (!_em.Exists(root)) continue;
                    float3 pos = FloatingOrigin.TileToSceneRebased(kv.Value.TileOriginRender, frame.SceneOriginRender, frame.Rebase);
                    _em.SetComponentData(root, LocalTransform.FromPositionRotation(pos, rot));
                    _em.SetComponentData(root, new LocalToWorld { Value = float4x4.TRS(pos, rot, new float3(1f)) });
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

        protected override void DoDispose()
        {
            if (_destroyScratch.IsCreated) _destroyScratch.Dispose();
            _items.Clear();
            _tileRoots.Clear();
            if (_world != null && _world.IsCreated)
            {
                if (World.DefaultGameObjectInjectionWorld == _world)
                    World.DefaultGameObjectInjectionWorld = _prevDefaultWorld;
                _world.Dispose(); // destroys all entities + the EG world state (incl. its mesh/material registries)
            }
            _world = null;

            // Stall #3: the world disposal tore down EG's registries wholesale, so no explicit Unregister* is
            // needed for correctness. But the prototype's placeholder Mesh is a UnityEngine.Object we created —
            // destroy it explicitly (a Mesh is not freed just because nothing references it).
            if (_prototypeMesh != null)
            {
                _prototypeMesh.DestroySafely(allowDestroyingAssets: true);
                _prototypeMesh = null;
            }
        }
    }
}
