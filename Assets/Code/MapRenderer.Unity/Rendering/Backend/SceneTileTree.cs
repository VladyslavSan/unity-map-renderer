using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>
    /// The generic root → per-tile-container scene-organization unit shared by every GameObject-based draw
    /// path that groups its output by tile: one backend-root <see cref="GameObject"/>, one named container
    /// (<c>"Tile z/x/y"</c>) per live <see cref="TileId"/>, positioned + oriented by the floating-origin
    /// rebase and refreshed <b>once per tile per frame</b> — never per child. Callers attach their own
    /// per-tile children (a layer mesh, a label layer node, …) under <see cref="GetOrCreateTileNode"/>'s
    /// returned <see cref="Transform"/> and register/release them via <see cref="AddChild"/> /
    /// <see cref="ReleaseChildFrom"/> so the container is torn down once its last child is gone.
    ///
    /// <para>Extracted from <see cref="GameObjects.TileRenderer"/> (the original "good tree": backend root →
    /// per-tile container → per-layer child, <c>GetOrCreateContainer</c> + the <c>Rebuild</c> transform loop
    /// + the blink-fix cache + the container refcount teardown) so it can be reused verbatim by a second
    /// caller — the world-anchored label draw path, which owns its OWN instance ("Map Labels" root) so
    /// labels are organized identically to tile fills regardless of which tile backend is drawing them
    /// (Entities/BRG draw tile fills GameObject-free, so there is no tile-backend container to piggyback on;
    /// see the label-draw-backend-rework design §5).</para>
    /// </summary>
    internal sealed class SceneTileTree : IDisposable
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
        private GameObject _root;

        // Last scene frame seen by Rebuild — identical blink-fix rationale to GameObjects.TileRenderer:
        // GetOrCreateTileNode may be called AFTER Rebuild within the same frame; caching the frame lets a
        // freshly-created container be positioned immediately instead of blinking at the world origin for a
        // frame until the NEXT Rebuild repositions it.
        private SceneFrame _lastFrame;
        private bool       _hasSceneOrigin;

        public SceneTileTree(string rootName) => _root = new GameObject(rootName);

        /// <summary>The tree root's transform (null after <see cref="Dispose"/>).</summary>
        public Transform Root => _root != null ? _root.transform : null;

        /// <summary>Number of live tile containers (one per tile that has ≥1 registered child).</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>The container transform for <paramref name="tileId"/>, or null if no live container.</summary>
        public Transform Container(TileId tileId)
            => _nodes.TryGetValue(tileId, out var n) && n.Go != null ? n.Go.transform : null;

        private float3 InitialScenePos(double3 tileOriginRender)
            => _hasSceneOrigin
                ? FloatingOrigin.TileToSceneRebased(tileOriginRender, _lastFrame.SceneOriginRender, _lastFrame.Rebase)
                : float3.zero;

        private quaternion InitialSceneRot()
            => _hasSceneOrigin ? new quaternion(_lastFrame.Rebase) : quaternion.identity;

        /// <summary>
        /// Returns the existing container for <paramref name="tileId"/>, or creates one parented under the
        /// tree root, named <c>"Tile z/x/y"</c> and positioned + oriented at the tile's current scene
        /// placement. The caller is responsible for parenting its own child(ren) under the returned
        /// <see cref="Transform"/> and calling <see cref="AddChild"/> once per child registered.
        /// </summary>
        public Transform GetOrCreateTileNode(TileId tileId, double3 tileOriginRender)
        {
            if (_nodes.TryGetValue(tileId, out var rec)) return rec.Go.transform;

            var go = new GameObject($"Tile {tileId}");
            go.transform.SetParent(_root.transform, worldPositionStays: false);
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
                rec.Go.DestroySafely();
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
            _lastFrame      = frame;
            _hasSceneOrigin = true;

            quaternion rot = new quaternion(frame.Rebase); // same orientation for every tile (identity for Mercator)
            foreach (var kv in _nodes)
            {
                if (kv.Value.Go == null) continue;
                float3 pos = FloatingOrigin.TileToSceneRebased(kv.Value.TileOriginRender, frame.SceneOriginRender, frame.Rebase);
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

        /// <summary>Destroys the root (and with it every container and its callers' children). Idempotent.</summary>
        public void Dispose()
        {
            if (_root == null) return;
            _nodes.Clear();
            _root.DestroySafely();
            _root = null;
        }
    }
}
