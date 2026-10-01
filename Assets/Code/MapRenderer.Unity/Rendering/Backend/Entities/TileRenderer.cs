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
using MapRenderer.Unity.Rendering.Layers;

namespace MapRenderer.Unity.Rendering.Backend.Entities
{
    /// <summary>
    /// ECS render backend for <c>RenderBackend.Entities</c>: one Entities Graphics <see cref="Entity"/> per
    /// (tile, layer), parented under a non-rendered <c>"Tile z/x/y"</c> root that carries the tile transform,
    /// so <see cref="Rebuild"/> writes one transform per tile. Entities Graphics reads the live shared material.
    /// Non-local invariant: automatic bootstrap is off project-wide, so this owns the only <see cref="World"/>,
    /// and <see cref="Rebuild"/> must tick its systems once per frame.
    /// </summary>
    internal sealed class TileRenderer : TileRenderBackendBase<TileRenderer.DrawItem>, ITileRenderBackend
    {
        // One draw item = one layer entity under its tile's root entity. Internal so the test assembly's
        // EntitiesTileRendererTestExtensions can read it.
        internal struct DrawItem : IDrawItem
        {
            public Entity      Entity;
            public TileId      TileId;   // which tile root this layer hangs under
            public BatchMeshID MeshId;   // stall #3: the EG-registered mesh id, for UnregisterMesh on removal
            public int         MaterialIndex { get; set; } // the layer slot this entity was created at — lets
                                                           // SetLayerMaterials find every item a retired slot must retire
            public bool        Hidden        { get; set; } // item-level flag (SetItemsVisible); drawn only when also slot-visible
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
        // internal (not private) for the same reason as DrawItem/RootRec — test-assembly observability.
        internal readonly Dictionary<TileId, RootRec> _tileRoots      = new Dictionary<TileId, RootRec>();

        // Persistent list for one RemoveItems() batch: its layer entities plus the tile roots it empties,
        // destroyed in ONE DestroyEntity structural change. Disposed in DoDispose.
        private NativeList<Entity> _destroyList;

        // Persistent list for one SetItemsVisible() batch: the entities whose DisableRendering tag moves.
        private NativeList<Entity> _toggleList;

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
        /// UnregisterMesh in RemoveItems). Must return to 0 after a full load→release (incl. the
        /// prepared-cache round-trip) — catches the ID route's missing-unregister leak trap.</summary>
        internal int RegisteredMeshCount { get; private set; }

        private World         _world;
        internal EntityManager _em;   // internal: the test-assembly observability extensions query through it
        private readonly World _prevDefaultWorld;
        private ComponentSystemBase _initGroup;
        private ComponentSystemBase _simGroup;
        private ComponentSystemBase _presGroup;
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

        /// <param name="layerMaterials">The full-width per-layer material list, indexed by slot.</param>
        /// <param name="layerNames">
        /// Optional per-layer style ids parallel to <paramref name="layerMaterials"/>, used only to name the
        /// layer entities in the Editor's Entities Hierarchy. When null/short, the material name is used.
        /// </param>
        /// <param name="layerShadowModes">Optional per-layer <c>IRenderLayer.CastShadows</c> parallel to
        /// <paramref name="layerMaterials"/>; null/short ⇒ <see cref="ShadowCastingMode.Off"/>.</param>
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

            _world           = DefaultWorldInitialization.Initialize("MapEntitiesWorld", editorWorld: false);
            _prevDefaultWorld = World.DefaultGameObjectInjectionWorld;
            World.DefaultGameObjectInjectionWorld = _world; // Entities Graphics reads the default world.
            _em = _world.EntityManager;
            _destroyList = new NativeList<Entity>(64, Allocator.Persistent);
            _toggleList  = new NativeList<Entity>(64, Allocator.Persistent);

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

            // A retired slot keeps no style-layer name: its material is null and nothing draws into it.
            for (int i = 0; i < _layerNames.Count && i < layerMaterials.Count; i++)
                if (layerMaterials[i] == null) _layerNames[i] = null;

            ReplaceSlotLists(layerMaterials.Count, layerShadowModes);

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
            _em.AddComponent<DisableRendering>(prototype); // every layer entity is born hidden
            _em.AddComponent<Prefab>(prototype); // exclude prototype from rendering/queries; Instantiate strips it
            return prototype;
        }

        /// <summary>ONE structural change for the whole slot, not one per entity, for the same reason
        /// <see cref="RemoveItems"/> destroys its entities in a single call. A hidden item already carries
        /// <c>DisableRendering</c>, so it is left alone.</summary>
        protected override void ApplySlotGate(int slot, bool visible)
        {
            if (_world is not { IsCreated: true }) return;
            int n = 0;
            foreach (var kv in _items) if (kv.Value.MaterialIndex == slot && !kv.Value.Hidden) n++;
            if (n == 0) return;

            var affected = new NativeArray<Entity>(n, Allocator.Temp);
            try
            {
                int w = 0;
                foreach (var kv in _items)
                    if (kv.Value.MaterialIndex == slot && !kv.Value.Hidden) affected[w++] = kv.Value.Entity;

                if (visible) _em.RemoveComponent<DisableRendering>(affected);
                else         _em.AddComponent<DisableRendering>(affected);
            }
            finally { affected.Dispose(); }
        }

        /// <summary>Gathers the entities whose <c>DisableRendering</c> tag moves: a slot-gated item already carries it, and an
        /// entity that no longer exists has nothing to move. <see cref="EndItemVisibilityBatch"/> applies them.</summary>
        protected override void ApplyItemVisibility(DrawItem item, bool visible, bool slotVisible)
        {
            if (_world is not { IsCreated: true }) return;
            if (slotVisible && _em.Exists(item.Entity)) _toggleList.Add(item.Entity);
        }

        /// <summary>ONE structural change for the whole batch.</summary>
        protected override void EndItemVisibilityBatch(bool visible)
        {
            try
            {
                if (_world is not { IsCreated: true } || _toggleList.Length == 0) return;
                if (visible) _em.RemoveComponent<DisableRendering>(_toggleList.AsArray());
                else         _em.AddComponent<DisableRendering>(_toggleList.AsArray());
            }
            finally { _toggleList.Clear(); }
        }

        // ── Instrumentation counters ────────────────────────────────────────────────────────────
        // Tests read them, but this class writes them. Test-only queries live in EntitiesTileRendererTestExtensions.

        /// <summary>Number of batched DestroyEntity structural changes performed by the LAST
        /// <see cref="RemoveItems"/> call (0 or 1 — the whole batch is one structural change). A shallow
        /// one-item-at-a-time implementation leaves this 0. Pinned by
        /// <c>EntitiesTileRendererTests.RemoveItems_DestroysWholeRecord_InOneBatchedStructuralChange</c>.</summary>
        internal int DestroyEntityBatchesLastRemove { get; private set; }

        /// <summary>Entities destroyed by the last <see cref="RemoveItems"/> batch (the record's
        /// layer entities plus any tile root the batch emptied).</summary>
        internal int EntitiesDestroyedLastRemove { get; private set; }

        // ── Draw item registration ────────────────────────────────────────────────────────────────

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

            Entity root = _em.CreateEntity(); // Rebuild places it, in the frame that created it
            _em.AddComponentData(root, LocalTransform.Identity);
            _em.AddComponentData(root, new LocalToWorld { Value = float4x4.identity });
#if UNITY_EDITOR
            _em.SetName(root, ToEntityName($"Tile {tileId}"));
#endif
            _tileRoots[tileId] = new RootRec { Root = root, TileOriginRender = tileOriginRender, ChildCount = 0 };
            return root;
        }

        /// <summary>
        /// Registers a tile-layer mesh as an Entities-Graphics entity, parented under its tile's root
        /// entity (created on demand). <see cref="Rebuild"/> positions it, not this call. Returns a handle for later
        /// removal. <paramref name="materialIndex"/> is the layer's global SLOT, indexing the full-width material list, matching
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

            // Identity until the Rebuild of this frame, whose LocalToWorldSystem derives the value from the root.
            _em.SetComponentData(e, new LocalToWorld { Value = float4x4.identity });

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
                    : mat != null ? mat.name : string.Empty;
            _em.SetName(e, ToEntityName(layerName));
#endif
            }

            var rec = _tileRoots[tileId];
            rec.ChildCount++;
            _tileRoots[tileId] = rec;

            int handle = _nextHandle++;
            _items[handle] = new DrawItem
            {
                Entity = e, TileId = tileId, MeshId = meshId, MaterialIndex = materialIndex, Hidden = true
            };
            return handle;
        }

        /// <summary>
        /// Removes a whole record's layer entities (and any tile root the batch empties) in ONE
        /// <c>EntityManager.DestroyEntity(NativeArray&lt;Entity&gt;)</c> structural change instead of L+1 — one
        /// structural change per layer would make a burst of tile releases spike the frame. Bookkeeping: child-count
        /// decrement, root-dies-at-0, collected then destroyed once.
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
            _toggleList.Dispose();
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
