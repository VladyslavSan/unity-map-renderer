using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.View;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>
    /// The generic root → per-tile-container scene-organization unit shared by every GameObject-based draw
    /// path that groups its output by tile: one backend-root <see cref="GameObject"/>, one named container
    /// (<c>"Tile z/x/y"</c>) per live <see cref="TileId"/>, positioned + oriented by the floating-origin
    /// rebase and refreshed <b>once per tile per frame</b> — never per child. Callers attach their own
    /// per-tile children (a layer mesh, a symbol layer node, …) under <see cref="GetOrCreateTileNode"/>'s
    /// returned <see cref="Transform"/> and register/release them via <see cref="AddChild"/> /
    /// <see cref="ReleaseChildFrom"/> so the container is torn down once its last child is gone.
    ///
    /// <para>Extracted from <see cref="GameObjects.TileRenderer"/> (the original "good tree": backend root →
    /// per-tile container → per-layer child, <c>GetOrCreateContainer</c> + the <c>Rebuild</c> transform loop
    /// + the blink-fix cache + the container refcount teardown) so it can be reused verbatim by a second
    /// caller — the world-anchored symbol draw path, which owns its OWN instance ("Map Symbols" root) so
    /// symbols are organized identically to tile fills regardless of which tile backend is drawing them
    /// (Entities/BRG draw tile fills GameObject-free, so there is no tile-backend container to piggyback on;
    /// see the symbol-draw-backend-rework design §5).</para>
    /// </summary>
    internal sealed class SceneTileTree : VerifiedDisposable
    {
        // One per live tile: the container its callers' children are grouped under, so the scene Hierarchy
        // shows a per-tile tree. Carries the tile's projected SW-corner render origin so Rebuild can
        // reposition the whole subtree by writing only the container transform, plus a child refcount so the
        // container is destroyed once its last caller-registered child is gone.
        private struct TileNode
        {
            public GameObject Go;
            public double3    TileOriginRender;
            public int        ChildCount;
        }

        private readonly Dictionary<TileId, TileNode> _nodes = new Dictionary<TileId, TileNode>();
        private          GameObject                   _root;

        // Last scene frame seen by Rebuild, null until the first one — identical blink-fix rationale to
        // GameObjects.TileRenderer: GetOrCreateTileNode may be called AFTER Rebuild within the same frame;
        // caching the frame lets a freshly-created container be positioned immediately instead of blinking at
        // the world origin for a frame until the NEXT Rebuild repositions it.
        private SceneFrame? _lastFrame;

        // Tile containers recycle rather than churn: a zoom step replaces the WHOLE cover at once, so the
        // create/destroy burst is per-transition, not per-frame. Bare GameObjects (no components), so the win
        // here is smaller than the symbol path's leaves — and the per-rent `$"Tile {tileId}"` name is not saved
        // either (a container is named for the tile it holds, so it renames on every rent).
        //
        // A released container parks under _poolRoot, an INACTIVE root of this tree's own. The reparent is
        // mandatory: ObjectPool is scene-unaware — Release only files the reference away — so without it the
        // GameObject stays under _root, and `Root`'s child set is this type's published meaning ("the live
        // tiles"). It must not be `SetParent(null)` either: that promotes the container to a SCENE-ROOT
        // object, live in the Hierarchy and still active, keeping its last tenancy's name ("Tile 14/8192/5461")
        // so it is indistinguishable from a live container — a debugging hazard in the very backend whose
        // purpose is Inspector debuggability.
        //
        // _poolRoot is a CHILD of _root rather than a second scene root: everything this tree owns then sits
        // under one top-level object. NodeCount, not _root.childCount, is the live-tile quantity — see the
        // note on NodeCount.
        private GameObject _poolRoot;

        private readonly ObjectPool<GameObject> _containerPool;

        public SceneTileTree(string rootName)
        {
            // HideFlags.DontSave on everything this tree owns: it is all built at runtime from tiles and has
            // no business being serialized into a scene. It also means Unity will not destroy these on scene
            // load — teardown is Dispose's job, which VerifiedDisposable's finalizer reports if it is missed.
            _root     = new GameObject(rootName)          { hideFlags = HideFlags.DontSave };
            _poolRoot = new GameObject("(container pool)") { hideFlags = HideFlags.DontSave };
            _poolRoot.transform.SetParent(_root.transform, worldPositionStays: false);
            _poolRoot.SetActive(false);

            _containerPool = new ObjectPool<GameObject>(
                createFunc: () => new GameObject { hideFlags = HideFlags.DontSave },
                actionOnGet: null, // the rent site reparents — it is the only caller that knows the parent
                actionOnRelease: go => go.transform.SetParent(_poolRoot.transform, worldPositionStays: false),
                actionOnDestroy: go => go.DestroySafely(),
                collectionCheck: true, // a double-release would hand one container to two tiles
                defaultCapacity: 32,
                maxSize: 512);
        }

        /// <summary>The tree root's transform. THROWS <see cref="System.ObjectDisposedException"/> after
        /// <see cref="VerifiedDisposable.Dispose"/> rather than returning null — reading the tree of a torn-down
        /// backend is a caller bug, and a silent null only defers the NRE to whoever dereferences it. A caller
        /// that legitimately outlives the tree nulls its own reference and guards with <c>?.</c> instead
        /// (<see cref="GameObjects.TileRenderer.Root"/>, whose own "null after dispose" contract is preserved
        /// that way).</summary>
        public Transform Root
        {
            get
            {
                ThrowIfDisposed();
                return _root.transform;
            }
        }

        /// <summary>Number of live tile containers (one per tile that has ≥1 registered child). This — not
        /// <c>Root.childCount</c> — is the live-tile count: the root also carries the inactive
        /// <c>"(container pool)"</c> node that recycled containers park under.</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>The container transform for <paramref name="tileId"/>, or null if no live container.</summary>
        public Transform Container(TileId tileId)
        {
            return _nodes.TryGetValue(tileId, out var n) && n.Go != null ? n.Go.transform : null;
        }

        private float3 InitialScenePos(double3 tileOriginRender)
        {
            return _lastFrame is { } frame
                ? FloatingOrigin.TileToSceneRebased(tileOriginRender, frame.SceneOriginRender, frame.Rebase)
                : float3.zero;
        }

        private quaternion InitialSceneRot()
        {
            return _lastFrame is { } frame ? new quaternion(frame.Rebase) : quaternion.identity;
        }

        /// <summary>
        /// Returns the existing container for <paramref name="tileId"/>, or creates one parented under the
        /// tree root, named <c>"Tile z/x/y"</c> and positioned + oriented at the tile's current scene
        /// placement. The caller is responsible for parenting its own child(ren) under the returned
        /// <see cref="Transform"/> and calling <see cref="AddChild"/> once per child registered.
        /// </summary>
        public Transform GetOrCreateTileNode(TileId tileId, double3 tileOriginRender)
        {
            if (_nodes.TryGetValue(tileId, out var rec)) return rec.Go.transform;

            GameObject go = _containerPool.Get();
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            go.name = $"Tile {tileId}";
            float3 pos = InitialScenePos(tileOriginRender);
            go.transform.localPosition = new Vector3(pos.x, pos.y, pos.z);
            go.transform.localRotation = InitialSceneRot(); // identity for Mercator; per-frame rebase for the globe

            _nodes[tileId] = new TileNode { Go = go, TileOriginRender = tileOriginRender, ChildCount = 0 };
            return go.transform;
        }

        /// <summary>Registers one more child under <paramref name="tileId"/>'s container (must already
        /// exist — call after <see cref="GetOrCreateTileNode"/>). Keeps the container's refcount in sync so
        /// <see cref="ReleaseChildFrom"/> knows when the container is empty.</summary>
        public void AddChild(TileId tileId)
        {
            var rec = _nodes[tileId];
            rec.ChildCount++;
            _nodes[tileId] = rec;
        }

        /// <summary>Decrements <paramref name="tileId"/>'s container child count, destroying the container
        /// once it hits zero (an evicted tile leaves no empty node). Idempotent for an unknown tile id. The
        /// caller destroys its own child GameObject(s) separately — this only manages the container.</summary>
        public void ReleaseChildFrom(TileId tileId)
        {
            if (!_nodes.TryGetValue(tileId, out var rec)) return;

            rec.ChildCount--;
            if (rec.ChildCount <= 0)
            {
                // Null-guarded: Go can be destroyed out from under us (Rebuild tolerates it too), and unlike
                // the DestroySafely this replaced, ObjectPool.Release would fault on it.
                if (rec.Go != null) _containerPool.Release(rec.Go);
                _nodes.Remove(tileId);
            }
            else
            {
                _nodes[tileId] = rec;
            }
        }

        /// <summary>
        /// Refreshes each tile container's <c>localPosition</c> and <c>localRotation</c> from
        /// <paramref name="frame"/> (origin ≡ look-at; camera-relative rendering). One transform write per
        /// tile, not per child — every child sits at the container origin (local identity) and moves with it.
        /// </summary>
        public void Rebuild(in SceneFrame frame)
        {
            // Cache so a tile node created later this frame (after this Rebuild) is created already
            // positioned, instead of blinking at the world origin for a frame.
            _lastFrame = frame;

            quaternion rot = new quaternion(frame.Rebase); // same orientation for every tile (identity for Mercator)
            foreach (var kv in _nodes)
            {
                if (kv.Value.Go == null) continue;
                float3 pos = FloatingOrigin.TileToSceneRebased(kv.Value.TileOriginRender, frame.SceneOriginRender,
                    frame.Rebase);
                kv.Value.Go.transform.localPosition = new Vector3(pos.x, pos.y, pos.z);
                kv.Value.Go.transform.localRotation = rot;
            }
        }

        /// <summary>XZ scene-space bounding box covering every live tile container's position, expanded by
        /// <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin. Returns a
        /// zero-sized box centered at the origin when empty.</summary>
        public Bounds ComputeSceneBounds(float tileSizeWorld)
        {
            if (_nodes.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var kv in _nodes)
            {
                if (kv.Value.Go == null) continue;
                Vector3 p                            = kv.Value.Go.transform.position;
                if (p.x                 < minX) minX = p.x;
                if (p.x + tileSizeWorld > maxX) maxX = p.x + tileSizeWorld;
                if (p.z                 < minZ) minZ = p.z;
                if (p.z + tileSizeWorld > maxZ) maxZ = p.z + tileSizeWorld;
            }

            if (minX == float.MaxValue) return new Bounds(Vector3.zero, Vector3.zero);
            float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            return new Bounds(new Vector3(cx, 0f, cz), new Vector3(maxX - minX, 1f, maxZ - minZ));
        }

        /// <summary>Destroys the root (and with it every LIVE container and its callers' children), then the
        /// pool's detached containers. Runs at most once — <see cref="VerifiedDisposable"/> owns the
        /// idempotency guard that this used to hand-roll as <c>if (_root == null) return</c>, and adds the
        /// Editor-only finalizer that reports a tree dropped without Dispose.</summary>
        protected override void DoDispose()
        {
            _nodes.Clear();
            _root.DestroySafely();
            _root = null;

            _containerPool.Clear(); // actionOnDestroy per parked container (_poolRoot dies with _root above)
            _poolRoot = null;
        }
    }
}
