using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>
    /// Root → per-tile-container scene tree shared by every GameObject draw path that groups output by tile:
    /// one root, one <c>"Tile z/x/y"</c> container per live <see cref="TileId"/>, placed by the floating-origin
    /// rebase once per tile per frame, never per child. Callers parent children under
    /// <see cref="GetOrCreateTileNode"/> and register them via <see cref="AddChild"/> and
    /// <see cref="ReleaseChildFrom"/>; a container goes with its last child. The symbol path has its own.
    /// </summary>
    internal sealed class SceneTileTree : VerifiedDisposable
    {
        // One per live tile. Rebuild moves the whole subtree by writing only this container's transform from
        // TileOriginRender; ChildCount frees the container when its last registered child is gone.
        private struct TileNode
        {
            public GameObject Go;
            public double3    TileOriginRender;
            public int        ChildCount;
        }

        private readonly Dictionary<TileId, TileNode> _nodes = new Dictionary<TileId, TileNode>();
        private          GameObject                   _root;

        // Last frame seen by Rebuild (null before the first). A container created after Rebuild in the same
        // frame is placed from it at once, instead of blinking at the world origin until the next Rebuild.
        private SceneFrame? _lastFrame;

        // Inactive parent for released containers; the pool recycles them because a zoom step replaces the whole
        // cover at once. Non-obvious why: ObjectPool does not reparent, so a released container left in place
        // stays under _root as a fake live tile, and SetParent(null) makes it an active scene root that keeps
        // its old "Tile z/x/y" name. It is a child of _root so the tree has one top-level object.
        private GameObject _poolRoot;

        private readonly ObjectPool<GameObject> _containerPool;

        public SceneTileTree(string rootName)
        {
            // DontSave: runtime objects never serialize into a scene, and scene load does not destroy them.
            // Teardown is Dispose's job; VerifiedDisposable's finalizer reports a missed one.
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

        /// <summary>The tree root's transform. Throws <see cref="System.ObjectDisposedException"/> after
        /// <see cref="VerifiedDisposable.Dispose"/>, because reading a torn-down tree is a caller bug. A caller
        /// that outlives the tree nulls its own reference and guards with <c>?.</c> instead
        /// (<see cref="GameObjects.TileRenderer.Root"/>).</summary>
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
        /// idempotency guard, and adds the Editor-only finalizer that reports a tree dropped without
        /// Dispose.</summary>
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
