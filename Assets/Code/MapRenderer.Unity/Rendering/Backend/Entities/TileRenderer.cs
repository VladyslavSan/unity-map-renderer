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
using MapRenderer.Unity.View;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Backend.Entities
{
    /// <summary>
    /// ECS render backend for <c>RenderBackend.Entities</c>: one Entities Graphics <see cref="Entity"/> per
    /// (tile, layer), parented under a non-rendered <c>"Tile z/x/y"</c> root that carries the tile transform,
    /// so <see cref="Rebuild"/> writes one transform per tile. Entities Graphics reads the live shared material.
    /// Non-local invariant: automatic bootstrap is off project-wide, so this owns the only <see cref="World"/>,
    /// and <see cref="Rebuild"/> must tick its systems once per frame.
    /// </summary>
    internal sealed class TileRenderer : VerifiedDisposable, ITileRenderBackend
    {
        // One draw item = one layer entity under its tile's root entity. Internal so the test assembly's
        // EntitiesTileRendererTestExtensions can read it.
        internal struct ItemRec
        {
            public Entity      Entity;
            public TileId      TileId;   // which tile root this layer hangs under
            public BatchMeshID MeshId;   // stall #3: the EG-registered mesh id, for UnregisterMesh on removal
            public int         MaterialIndex; // the layer slot this entity was created at — lets
                                               // SetLayerMaterials find every item a retired slot must retire
        }

        // One per live tile: the named parent of its layer entities. Rebuild moves the whole subtree by writing
        // only the root's transform from TileOriginRender.
        internal struct RootRec
        {
            public Entity  Root;
            public double3 TileOriginRender;
            public int     ChildCount;     // layer entities parented to this root; root dies when it hits 0
        }

        private readonly List<Material>             _layerMaterials = new List<Material>();
        // Per-layer style id (e.g. "water"), parallel to _layerMaterials. It names each layer entity in the
        // Entities Hierarchy; empty ⇒ the shared material name.
        private readonly List<string>               _layerNames     = new List<string>();
        // Per-layer shadow-cast declaration, parallel to _layerMaterials — IRenderLayer.CastShadows, carried
        // verbatim from TileManager.LayerShadowModes. Absent or short ⇒ Off, identically in all three backends.
        private readonly List<ShadowCastingMode>    _layerShadowModes = new List<ShadowCastingMode>();
        // Per-layer draw gate (SetLayerVisible), parallel to _layerMaterials; false ⇒ the slot's entities carry
        // DisableRendering. Absent or short ⇒ visible, identically in all three backends.
        private readonly List<bool>                _layerVisible     = new List<bool>();
        // internal (not private) for the same reason as ItemRec/RootRec — test-assembly observability.
        internal readonly Dictionary<int, ItemRec>    _items          = new Dictionary<int, ItemRec>();
        internal readonly Dictionary<TileId, RootRec> _tileRoots      = new Dictionary<TileId, RootRec>();

        // Persistent list for one RemoveItems() batch: its layer entities plus the tile roots it empties,
        // destroyed in ONE DestroyEntity structural change. Disposed in DoDispose.
        private NativeList<Entity> _destroyList;

        // ── ID-based layer creation: materials register once, meshes on add, no RenderMeshArray per entity ──
        // An ID-based MaterialMeshInfo points each entity at them; shared prototypes avoid per-entity migration.
        private EntitiesGraphicsSystem                 _eg;             // from `using Unity.Rendering` — NOT qualified (Unity.Rendering collides with MapRenderer.Unity.Rendering)
        private BatchMaterialID[]                      _materialIds;   // one per layer material, registered once
        // One Prefab-tagged prototype per shadow-cast mode: RenderFilterSettings is a shared component, so
        // choosing at Instantiate time avoids a per-entity SetSharedComponent structural change.
        private Entity                                 _layerPrototypeNoCast; // Instantiated for ShadowCastingMode.Off slots
        private Entity                                 _layerPrototypeCast;   // Instantiated for ShadowCastingMode.On slots
        private Mesh                                   _prototypeMesh;  // inert placeholder mesh for the prototypes' RenderMeshArray

        /// <summary>Distinct RenderMeshArray VALUES constructed (the prototypes share ONE). It must stay ≤1
        /// no matter how many layers are added, and the two shadow-mode prototypes do not move it —
        /// RenderMeshArray equality is content-hashed, so both resolve to the same shared-component index.
        /// Pinned by
        /// <c>EntitiesTileRendererTests.AddTileLayer_IdRoute_NoPerEntityArray_BalancedMeshRegistration</c>.</summary>
        internal int RenderMeshArraysCreated { get; private set; }

        /// <summary>Live EG-registered meshes (inc on RegisterMesh in AddTileLayer, dec on
        /// UnregisterMesh in RemoveItem/RemoveItems). Must return to 0 after a full load→release (incl. the
        /// prepared-cache round-trip) — catches the ID route's missing-unregister leak trap.</summary>
        internal int RegisteredMeshCount { get; private set; }

        private World         _world;
        internal EntityManager _em;   // internal: the test-assembly observability extensions query through it
        private readonly World _prevDefaultWorld;
        private ComponentSystemBase _initGroup, _simGroup, _presGroup;
        private int  _nextHandle;

        /// <summary>Profiler marker name constants (SSOT) for the Entities backend — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // Per-frame drive: RootTransforms = per-tile transform writes; Init/Sim/PresGroup = the system-group
            // ticks. PresGroup runs EntitiesGraphicsSystem (instance upload + BRG batch registration).
            internal const string RootTransforms = "MapRenderer.ECS.RootTransforms";
            internal const string InitGroup      = "MapRenderer.ECS.InitGroup";
            internal const string SimGroup       = "MapRenderer.ECS.SimGroup";
            internal const string PresGroup      = "MapRenderer.ECS.PresGroup";

            // AddTileLayer sub-phases under MapRenderer.Tile.AddLayer: Root = GetOrCreateRoot, Register = prototype
            // Instantiate + EG mesh registration, Parent = Parent/LocalTransform + LocalToWorld/bounds writes.
            internal const string AddLayerRoot     = "MapRenderer.Tile.AddLayer.Root";
            internal const string AddLayerRegister = "MapRenderer.Tile.AddLayer.Register";
            internal const string AddLayerParent   = "MapRenderer.Tile.AddLayer.Parent";
        }

        private static readonly ProfilerMarker PmRootTransforms =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.RootTransforms);
        private static readonly ProfilerMarker PmInitGroup =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.InitGroup);
        private static readonly ProfilerMarker PmSimGroup =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SimGroup);
        private static readonly ProfilerMarker PmPresGroup =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.PresGroup);

        private static readonly ProfilerMarker PmAddRoot =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.AddLayerRoot);
        private static readonly ProfilerMarker PmAddRegister =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.AddLayerRegister);
        private static readonly ProfilerMarker PmAddParent =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.AddLayerParent);

        // Last frame seen by Rebuild. An entity added after Rebuild in the same frame is placed from it at
        // once, instead of blinking at the world origin until the next Rebuild.
        private SceneFrame _lastFrame;
        private bool       _hasSceneOrigin;

        /// <param name="layerMaterials">The full-width per-layer material list, indexed by slot.</param>
        /// <param name="layerNames">
        /// Optional per-layer style ids parallel to <paramref name="layerMaterials"/>, used only to name the
        /// layer entities in the Editor's Entities Hierarchy. When null/short, the material name is used.
        /// </param>
        /// <param name="layerShadowModes">Optional per-layer <c>Style.IRenderLayer.CastShadows</c> parallel to
        /// <paramref name="layerMaterials"/>; null/short ⇒ <see cref="ShadowCastingMode.Off"/>.</param>
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

            _world           = DefaultWorldInitialization.Initialize("MapEntitiesWorld", editorWorld: false);
            _prevDefaultWorld = World.DefaultGameObjectInjectionWorld;
            World.DefaultGameObjectInjectionWorld = _world; // Entities Graphics reads the default world.
            _em = _world.EntityManager;
            _destroyList = new NativeList<Entity>(64, Allocator.Persistent);

            _initGroup = _world.GetExistingSystemManaged<InitializationSystemGroup>();
            _simGroup  = _world.GetExistingSystemManaged<SimulationSystemGroup>();
            _presGroup = _world.GetExistingSystemManaged<PresentationSystemGroup>(); // contains EntitiesGraphicsSystem

            // Register each layer material ONCE with EG (stable BatchMaterialID); meshes register
            // per-add. Then build the single layer prototype every AddTileLayer instantiates.
            _eg = _world.GetExistingSystemManaged<EntitiesGraphicsSystem>();
            _materialIds = new BatchMaterialID[_layerMaterials.Count];
            for (int i = 0; i < _layerMaterials.Count; i++)
                _materialIds[i] = _layerMaterials[i] != null ? _eg.RegisterMaterial(_layerMaterials[i]) : default;
            BuildLayerPrototype();
        }

        /// <summary>Restyle-time material update — retires a slot's entities (one batched
        /// <see cref="RemoveItems"/>, including an immediate EG mesh unregister — UNLIKE BRG) when its
        /// material goes null; see `docs/tile-pipeline-design.md`.</summary>
        public void SetLayerMaterials(
            IReadOnlyList<Material> layerMaterials, IReadOnlyList<ShadowCastingMode> layerShadowModes)
        {
            ThrowIfDisposed();

            int oldCount = _layerMaterials.Count;
            var newMaterialIds = new BatchMaterialID[layerMaterials.Count];
            for (int i = 0; i < layerMaterials.Count; i++)
            {
                Material mat    = layerMaterials[i];
                Material oldMat = i < oldCount ? _layerMaterials[i] : null;
                if (ReferenceEquals(mat, oldMat))
                {
                    newMaterialIds[i] = i < _materialIds.Length ? _materialIds[i] : default;
                    continue;
                }
                // Reference-null, NOT `!=` (Unity's fake-null hides a DESTROYED material — see
                // docs/tile-pipeline-design.md's SetLayerMaterials note).
                if (i < oldCount && !ReferenceEquals(oldMat, null) && i < _materialIds.Length)
                    _eg.UnregisterMaterial(_materialIds[i]);
                newMaterialIds[i] = mat != null ? _eg.RegisterMaterial(mat) : default;
            }
            _materialIds = newMaterialIds;

            _layerMaterials.Clear();
            for (int i = 0; i < layerMaterials.Count; i++) _layerMaterials.Add(layerMaterials[i]);

            _layerShadowModes.Clear();
            if (layerShadowModes != null)
                for (int i = 0; i < layerShadowModes.Count; i++) _layerShadowModes.Add(layerShadowModes[i]);

            var retired = new List<int>();
            foreach (var kv in _items)
            {
                int mi = kv.Value.MaterialIndex;
                if ((uint)mi >= (uint)_layerMaterials.Count || _layerMaterials[mi] == null)
                    retired.Add(kv.Key);
            }
            if (retired.Count > 0)
                RemoveItems(retired.ToArray());
        }

        /// <summary>
        /// Builds the two Prefab layer-entity prototypes, one per shadow-cast mode: EG's render components plus
        /// Parent/LocalTransform. Every AddTileLayer instantiates one, sharing its archetype and its single
        /// inert RenderMeshArray value, which exists only because <see cref="RenderMeshUtility.AddComponents"/>
        /// requires one. That array is seeded with the first non-null material, because slot 0 may be a null
        /// background slot.
        /// </summary>
        private void BuildLayerPrototype()
        {
            int seedIndex = -1;
            for (int i = 0; i < _layerMaterials.Count; i++)
                if (_layerMaterials[i] != null) { seedIndex = i; break; }
            if (seedIndex < 0) return; // no tile-mesh layers → AddTileLayer never called; no prototype needed

            _prototypeMesh = new Mesh { name = "MapLayerPrototype(inert)" };
            var rma = new RenderMeshArray(new Material[] { _layerMaterials[seedIndex] }, new Mesh[] { _prototypeMesh });

            _layerPrototypeNoCast = NewLayerPrototype(rma, ShadowCastingMode.Off);
            _layerPrototypeCast   = NewLayerPrototype(rma, ShadowCastingMode.On);
            RenderMeshArraysCreated = 1; // ONE array VALUE, shared by both prototypes and every instance
        }

        /// <summary>One Prefab-tagged layer prototype over the shared <paramref name="rma"/>, filtered to
        /// <paramref name="cast"/>. Receiving is unconditional for tile geometry — building shadows landing on
        /// roads and ground fills is the visible half of the feature.</summary>
        /// <param name="rma">The single inert RenderMeshArray value both prototypes share.</param>
        /// <param name="cast">This prototype's shadow-cast mode.</param>
        /// <returns>The prototype entity to <c>Instantiate</c> per layer.</returns>
        private Entity NewLayerPrototype(RenderMeshArray rma, ShadowCastingMode cast)
        {
            var desc = new RenderMeshDescription(cast, receiveShadows: true);
            Entity prototype = _em.CreateEntity();
            RenderMeshUtility.AddComponents(
                prototype, _em, desc, rma, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            _em.AddComponent(prototype,
                new ComponentTypeSet(ComponentType.ReadWrite<Parent>(), ComponentType.ReadWrite<LocalTransform>()));
            _em.AddComponent<Prefab>(prototype); // exclude prototype from rendering/queries; Instantiate strips it
            return prototype;
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
            if (_layerVisible[slot] == visible) return; // unchanged ⇒ no structural change
            _layerVisible[slot] = visible;

            int n = 0;
            foreach (var kv in _items) if (kv.Value.MaterialIndex == slot) n++;
            if (n == 0) return;

            var affected = new NativeArray<Entity>(n, Allocator.Temp);
            try
            {
                int w = 0;
                foreach (var kv in _items)
                    if (kv.Value.MaterialIndex == slot) affected[w++] = kv.Value.Entity;

                // ONE structural change for the whole slot, not one per entity — the same batching reason
                // RemoveItems destroys its entities in a single DestroyEntity(NativeArray) call.
                if (visible) _em.RemoveComponent<DisableRendering>(affected);
                else         _em.AddComponent<DisableRendering>(affected);
            }
            finally { affected.Dispose(); }
        }

        // ── Instrumentation counters ────────────────────────────────────────────────────────────
        // Tests read them, but this class writes them. Test-only queries live in EntitiesTileRendererTestExtensions.

        /// <summary>Number of batched DestroyEntity structural changes performed by the LAST
        /// <see cref="RemoveItems"/> call (0 or 1 — the whole batch is one structural change). A shallow
        /// loop-over-<see cref="RemoveItem"/> implementation leaves this 0. Pinned by
        /// <c>EntitiesTileRendererTests.RemoveItems_DestroysWholeRecord_InOneBatchedStructuralChange</c>.</summary>
        internal int DestroyEntityBatchesLastRemove { get; private set; }

        /// <summary>Entities destroyed by the last <see cref="RemoveItems"/> batch (the record's
        /// layer entities plus any tile root the batch emptied).</summary>
        internal int EntitiesDestroyedLastRemove { get; private set; }

        /// <summary>
        /// XZ scene-space bounding box covering all live tile entities (each entity's
        /// <see cref="LocalToWorld"/> translation, plus <paramref name="tileSizeWorld"/> for the tile's
        /// mesh extent beyond its origin). Used by tests to frame a camera that sees all entities (there
        /// are no child GameObjects to bound). Returns <c>default</c> when empty. Mirrors
        /// <see cref="Backend.BRG.TileRenderer.ComputeSceneBounds"/>.
        /// </summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld)
        {
            if (_items.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);

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
        /// is the layer's global SLOT, indexing the full-width material list, matching
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
                // ID route: one Instantiate of the shared-archetype prototype, then register the mesh with EG
                // and point the entity at (meshId, materialId).
                e      = _em.Instantiate(ShadowModeFor(materialIndex) == ShadowCastingMode.Off
                    ? _layerPrototypeNoCast
                    : _layerPrototypeCast);
                meshId = _eg.RegisterMesh(mesh);
                RegisteredMeshCount++;
                // This EG version has the (materialID, meshID) ctor, not a factory. EG batches by these ids and
                // ignores the inert RenderMeshArray copied from the prototype.
                _em.SetComponentData(e, new MaterialMeshInfo(_materialIds[materialIndex], meshId));
            }

            using (PmAddParent.Auto())
            {
            // Parent + LocalTransform come from the prototype's archetype, so these sets cause no migration.
            // LocalToWorldSystem then derives this entity's LocalToWorld from the root on each Rebuild tick.
            _em.SetComponentData(e, new Parent { Value = root });
            _em.SetComponentData(e, LocalTransform.Identity);

            // Set LocalToWorld directly: this entity renders before its first transform tick, so this avoids an
            // origin blink. The next Rebuild's LocalToWorldSystem re-derives the same value from the root.
            _em.SetComponentData(e, new LocalToWorld
            {
                Value = float4x4.TRS(InitialScenePos(tileOriginRender), InitialSceneRot(), new float3(1f))
            });

            // RenderBounds drives EG frustum culling, so it mirrors mesh.bounds, which the builders compute in the
            // vertices' frame. Non-obvious why: a fixed box is too small at low zoom, where a tile spans several
            // million metres, so EG culls the whole tile. A zero-size mesh bound falls back to a never-cull box.
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

            // An item added into an already-hidden slot must not draw until the gate lifts (the prototypes
            // carry no DisableRendering, so this is the only place that state reaches a fresh instance).
            if (!Visible(materialIndex)) _em.AddComponent<DisableRendering>(e);

            var rec = _tileRoots[tileId];
            rec.ChildCount++;
            _tileRoots[tileId] = rec;

            int handle = _nextHandle++;
            _items[handle] = new ItemRec { Entity = e, TileId = tileId, MeshId = meshId, MaterialIndex = materialIndex };
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
        /// Removes a whole record's layer entities (and any tile root the batch empties) in ONE
        /// <c>EntityManager.DestroyEntity(NativeArray&lt;Entity&gt;)</c> structural change instead of L+1 — one
        /// structural change per layer would make a burst of tile releases spike the frame. Same bookkeeping as
        /// <see cref="RemoveItem"/> (child-count decrement, root-dies-at-0), just collected then destroyed once.
        /// Idempotent for unknown handles. The Mesh assets are NOT destroyed here — TileManager owns them.
        /// </summary>
        public void RemoveItems(ReadOnlySpan<int> handles)
        {
            DestroyEntityBatchesLastRemove = 0;
            EntitiesDestroyedLastRemove    = 0;
            if (IsDisposed) return;

            // Non-local invariant: Play-mode Stop disposes every World before MapViewComponent.OnDestroy, so the
            // entities are gone while this backend is not. Touching _em would throw and abort TileManager's
            // teardown mid-loop, leaking every subsystem disposed after it.
            if (_world is not { IsCreated: true }) return;

            _destroyList.Clear();
            for (int i = 0; i < handles.Length; i++)
            {
                if (!_items.TryGetValue(handles[i], out var item)) continue; // idempotent unknown handle
                if (_em.Exists(item.Entity)) _destroyList.Add(item.Entity);
                _items.Remove(handles[i]);
                _eg.UnregisterMesh(item.MeshId); // stall #3: ID route requires explicit unregister
                RegisteredMeshCount--;

                if (_tileRoots.TryGetValue(item.TileId, out var root))
                {
                    root.ChildCount--;
                    if (root.ChildCount <= 0)
                    {
                        if (_em.Exists(root.Root)) _destroyList.Add(root.Root);
                        _tileRoots.Remove(item.TileId);
                    }
                    else
                    {
                        _tileRoots[item.TileId] = root;
                    }
                }
            }

            if (_destroyList.Length > 0)
            {
                _em.DestroyEntity(_destroyList.AsArray());
                DestroyEntityBatchesLastRemove = 1;
                EntitiesDestroyedLastRemove    = _destroyList.Length;
            }
        }

        // ── Per-frame rebuild ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Once per frame: writes each tile root's <see cref="LocalTransform"/> and <see cref="LocalToWorld"/>
        /// from <paramref name="frame"/> (origin = look-at), then ticks the system groups, so
        /// <see cref="LocalToWorldSystem"/> derives each layer entity's transform from its root and EG uploads
        /// the instance data before the camera renders. Setting the root's <see cref="LocalToWorld"/> too keeps
        /// reads correct before the tick. The root loop does no managed allocation or structural change.
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

            // Bootstrap is disabled, so tick the groups here: Simulation runs TransformSystemGroup, Presentation
            // runs EntitiesGraphicsSystem. The draw itself is emitted during the camera's render via SRP culling.
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
            _destroyList.Dispose();
            _items.Clear();
            _tileRoots.Clear();
            if (_world != null && _world.IsCreated)
            {
                if (World.DefaultGameObjectInjectionWorld == _world)
                    World.DefaultGameObjectInjectionWorld = _prevDefaultWorld;
                _world.Dispose(); // destroys all entities + the EG world state (incl. its mesh/material registries)
            }
            _world = null;

            // World disposal tears down EG's registries, so no Unregister* is needed. The placeholder Mesh is
            // a UnityEngine.Object this class created, and nothing frees it unless it is destroyed here.
            if (_prototypeMesh != null)
            {
                _prototypeMesh.DestroySafely(allowDestroyingAssets: true);
                _prototypeMesh = null;
            }
        }
    }
}
