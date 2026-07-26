using UnityEngine;
using MapRenderer.Core.Lifetime;

namespace MapRenderer.Unity.Common
{
    /// <summary>
    /// A <see cref="GameObject"/> that draws one mesh: it and its <see cref="MeshFilter"/> /
    /// <see cref="MeshRenderer"/>, resolved once at construction and held as properties.
    ///
    /// <para><b>What this is for.</b> Both GameObject-based draw paths — the world-label leaves
    /// (<c>WorldLabelRenderer</c>) and the tile backend's per-layer children
    /// (<c>Backend.GameObjects.TileRenderer</c>) — pool exactly this shape, and both used to express it as an
    /// unenforced handshake: a <c>createFunc</c> that happened to <c>AddComponent</c> both, and rent/release
    /// sites that <c>GetComponent</c> them back and null-checked the result. Making it a type turns that
    /// handshake into a guarantee (the properties are non-null by construction, so the null checks go), and
    /// collapses two near-identical create/clear pairs into one.</para>
    ///
    /// <para><b>State, not policy.</b> This deliberately owns nothing about how a node is USED — no
    /// parenting, no naming, no shadow mode, no <c>hideFlags</c>, no initial visibility. A shared type that
    /// guessed those would be wrong for someone: a label leaf sits at local identity under a layer node,
    /// while a tile layer child is named per style layer and placed under a tile container. A pool wrapping
    /// this type's *behaviour* was tried and removed for exactly that reason, and the constructor then
    /// briefly repeated the mistake with three renderer settings, which is why this paragraph enumerates
    /// what it does NOT decide.</para>
    ///
    /// <para>Both current callers happen to agree on some of those (shadows off,
    /// <see cref="HideFlags.DontSave"/> — every runtime-built map object carries it). That agreement is not
    /// a reason to move the settings in here: "both callers want it today" is precisely the reasoning that
    /// put a TRS reset into the retired pool wrapper, where a third caller then didn't.</para>
    ///
    /// <para>A class rather than a struct because <see cref="UnityEngine.Pool.ObjectPool{T}"/> constrains
    /// <c>T : class</c> — which is also the intended home for instances of this type. Main-thread only
    /// (touches the scene graph).</para>
    /// </summary>
    internal sealed class MeshNode : VerifiedDisposable
    {
        /// <summary>Builds the node and its two components, and NOTHING else — no shadow mode, no
        /// <c>hideFlags</c>, no initial <c>enabled</c> state. Set what you need once, in your pool's
        /// <c>createFunc</c>: it costs one write per CREATED node, not per rent, and it keeps the decision
        /// where the answer is actually known.</summary>
        internal MeshNode(string name)
        {
            // The components-in-constructor overload: one construction that carries the component set, rather
            // than a bare GameObject followed by two AddComponent calls. It says "this object always has
            // these two" in the expression that creates it — which is the whole claim this type exists to
            // make. (Only reached on a pool MISS, so read it as expressiveness, not a hot-path saving.)
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
        /// Drops every reference to the tenancy that just ended, leaving the node safe to park.
        ///
        /// <para>Clearing <see cref="MeshFilter.sharedMesh"/> is not hygiene, it is required: neither draw
        /// path owns its Mesh (TileManager owns tile meshes; a label slot destroys its own), and both may
        /// destroy it immediately after releasing the node — so a node that kept the binding would carry a
        /// DESTROYED Mesh into its next tenancy.</para>
        ///
        /// <para>Does NOT reparent. WHERE a released node goes is the owner's decision and it is a real one:
        /// <c>SetParent(null)</c> looks like "detached" but actually promotes the node to a SCENE-ROOT
        /// object — live in the Hierarchy, still active, and (if it carries
        /// <see cref="HideFlags.DontSave"/>) surviving scene unload. Owners park under their own inactive
        /// root instead; see any <c>actionOnRelease</c> in this codebase.</para>
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

        /// <summary>Destroys the node's <see cref="GameObject"/> (and with it both components). Runs at most
        /// once — <see cref="VerifiedDisposable"/> owns the guard — and the Editor-only finalizer reports a
        /// node dropped without it. That report is the point: a node is owned either by a pool or by the
        /// caller's live tree, and one that reaches neither is a leak nobody would otherwise notice.
        ///
        /// <para>Safe when the GameObject is ALREADY destroyed (a caller that tears down its scene tree
        /// wholesale kills the object, then disposes the wrapper) — <c>DestroySafely</c> is null-tolerant.</para></summary>
        protected override void DoDispose() => GameObject.DestroySafely();
    }
}
