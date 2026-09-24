using UnityEngine;
using MapRenderer.Core.Lifetime;

namespace MapRenderer.Unity.Common
{
    /// <summary>
    /// A <see cref="GameObject"/> with one <see cref="MeshFilter"/>/<see cref="MeshRenderer"/> pair — the shape
    /// both GameObject-based draw paths pool (<c>WorldSymbolRenderer</c>, <c>Backend.GameObjects.TileRenderer</c>).
    /// It decides nothing about use (parenting, naming, shadows, hideFlags, visibility), even where both callers
    /// agree: a shared type that guesses a setting is wrong for some caller. A class because
    /// <see cref="UnityEngine.Pool.ObjectPool{T}"/> requires <c>T : class</c>. Main-thread only.
    /// </summary>
    internal sealed class MeshNode : VerifiedDisposable
    {
        /// <summary>Builds the node and its two components, and NOTHING else — no shadow mode, no
        /// <c>hideFlags</c>, no initial <c>enabled</c> state. Set what you need once, in your pool's
        /// <c>createFunc</c>: it costs one write per CREATED node, not per rent, and it keeps the decision
        /// where the answer is actually known.</summary>
        internal MeshNode(string name)
        {
            // The components-in-constructor overload: reached only on a pool MISS, so this is
            // expressiveness (the object always has both components), not a hot-path saving.
            GameObject = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            Filter     = GameObject.GetComponent<MeshFilter>();
            Renderer   = GameObject.GetComponent<MeshRenderer>();
        }

        internal GameObject   GameObject { get; }
        internal MeshFilter   Filter     { get; }
        internal MeshRenderer Renderer   { get; }

        /// <summary>This node's transform — the thing callers parent, place and name.</summary>
        internal Transform Transform => GameObject.transform;

        /// <summary>
        /// Drops every reference to the tenancy that just ended, leaving the node safe to park. It clears
        /// <see cref="MeshFilter.sharedMesh"/> because the Mesh owner (TileManager or a symbol slot) may destroy
        /// it right after release, and a kept binding carries a destroyed Mesh into the next tenancy.
        /// Does not reparent — <c>SetParent(null)</c> promotes the node to a live scene-root object, not a
        /// detached one; the owner must park it under its own inactive root.
        /// </summary>
        internal void Release()
        {
            Filter.sharedMesh       = null;
            Renderer.sharedMaterial = null;
            Renderer.enabled        = false;
        }

        /// <summary>Parents the node at local identity under <paramref name="parent"/> and names it. The
        /// caller binds mesh/material and enables the renderer — this only places it.</summary>
        internal void AttachAt(Transform parent, string name)
        {
            GameObject.name = name;
            Transform.SetParent(parent, worldPositionStays: false);
            Transform.localPosition = Vector3.zero;
            Transform.localRotation = Quaternion.identity;
        }

        /// <summary>Destroys the node's <see cref="GameObject"/> and both components. Runs at most once
        /// (<see cref="VerifiedDisposable"/> owns the guard). The Editor-only finalizer reports a node dropped
        /// without it: a node that neither a pool nor a live tree owns is a leak. Safe when a scene
        /// teardown already destroyed the GameObject, because <c>DestroySafely</c> is null-tolerant.</summary>
        protected override void DoDispose() => GameObject.DestroySafely();
    }
}
