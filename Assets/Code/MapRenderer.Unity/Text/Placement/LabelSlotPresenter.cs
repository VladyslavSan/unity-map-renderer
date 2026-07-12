using System;
using UnityEngine;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>One persistent screen-space billboard presenter: a hidden-from-Hierarchy GameObject with a
    /// MeshFilter/MeshRenderer that Unity redraws every camera render (option (c), design §5) — the
    /// replacement for the retired per-frame Graphics.RenderMesh submit (and with it, the Editor blink:
    /// Game-View repaints without the player loop still redraw scene renderers). The Mesh is rewritten in
    /// place by LabelPlacementSystem each Tick; Present only compare-assigns the references and toggles
    /// visibility, so a steady frame costs zero engine calls and zero alloc. MAIN THREAD only.</summary>
    internal sealed class LabelSlotPresenter : IDisposable
    {
        private GameObject   _go;        // lazy — created on the first visible Present
        private MeshFilter   _filter;
        private MeshRenderer _renderer;
        private readonly string _name;
        private readonly Transform _parent;   // shared "Map Render Layers" root (null ⇒ scene root)

        public LabelSlotPresenter(string name, Transform parent = null)
        {
            _name = name;
            _parent = parent;
        }

        /// <summary>Whether this slot is currently drawing (the renderer exists and is enabled). Test
        /// surface — §7.10's demo/production flip regression pins that a slot never has two presenters
        /// enabled at once.</summary>
        internal bool Enabled => _renderer != null && _renderer.enabled;

        /// <summary>Bind (idempotently) and show, or hide. Creates the GameObject lazily on the first
        /// visible call: parented under the shared "Map Render Layers" root, shadowCastingMode Off,
        /// receiveShadows false (mirrors the GameObjects tile backend, Backend/GameObjects/TileRenderer.cs:216-219),
        /// hideFlags DontSave — a runtime artifact never serialised into a scene/build, but VISIBLE and
        /// inspectable in the Hierarchy (unlike HideAndDontSave, which also hid it).
        ///
        /// <para>Two lifecycle rules: (a) <paramref name="mesh"/>/<paramref name="material"/> are borrowed,
        /// never destroyed here — their owners (LabelPlacementSystem / this layer) dispose them. (b) the
        /// hidden GO is identity-transform ON PURPOSE — the Map/Symbol vertex shader ignores
        /// object-to-world entirely (screen px → clip via <c>_ScreenParamsLogical</c>), the exact property
        /// <c>SymbolAtlasOrientationSnapshotTests</c> already relies on (its header, :36-41).</para></summary>
        public void Present(Mesh mesh, Material material, bool visible)
        {
            if (!visible) { if (_renderer != null) _renderer.enabled = false; return; }
            if (_go == null)
            {
                _go = new GameObject(_name) { hideFlags = HideFlags.DontSave };
                _go.transform.SetParent(_parent, false); // local identity under the identity root ⇒ world identity
                _filter   = _go.AddComponent<MeshFilter>();
                _renderer = _go.AddComponent<MeshRenderer>();
                _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderer.receiveShadows    = false;
            }
            if (_filter.sharedMesh != mesh)          _filter.sharedMesh = mesh;
            if (_renderer.sharedMaterial != material) _renderer.sharedMaterial = material;
            _renderer.enabled = true;
        }

        public void Dispose() { _go.DestroySafely(); _go = null; _filter = null; _renderer = null; }
    }
}
