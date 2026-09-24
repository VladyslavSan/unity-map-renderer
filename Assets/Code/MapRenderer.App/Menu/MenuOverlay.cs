using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;

using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App.Menu
{
    /// <summary>
    /// Toggleable, page-based debug GUI for the MapDemo app; not part of the render path. A draggable IMGUI
    /// window shows the top of a page stack: a root menu of buttons, each opening a sub-page (<see cref="IMenuPage"/>)
    /// with a Back button. Open/close with <see cref="ToggleKey"/> (default backquote <c>`</c>) or a hotspot in the
    /// top-right screen corner. The key comes from IMGUI <c>Event.current</c>, so either Unity input backend works.
    /// </summary>
    public sealed class MenuOverlay : MonoBehaviour
    {
        /// <summary>The MapView this menu operates on (camera presets, symbol diagnostics). Wired at runtime by
        /// MapHost — a runtime reference, not a serialized Inspector field.</summary>
        public MapViewComponent Map { get; set; }

        /// <summary>Key that shows/hides the menu. Read via IMGUI events, so any KeyCode works under any input backend.</summary>
        [Tooltip("Key that shows/hides the menu (default backquote `). Works under any input backend.")]
        public KeyCode ToggleKey = KeyCode.BackQuote;

        /// <summary>Also toggle by clicking a small hotspot in the top-right screen corner (for builds/touch).</summary>
        [Tooltip("Also toggle by clicking a small hotspot in the top-right screen corner.")]
        public bool TopRightCornerToggle = true;

        /// <summary>The menu window rectangle in LOGICAL (pre-scale) pixels (draggable at runtime).</summary>
        [Tooltip("Menu window rectangle in logical pixels (draggable at runtime).")]
        public Rect WindowRect = new Rect(20f, 20f, 360f, 470f);

        // Runtime UI scale, because IMGUI has no DPI awareness. Auto-detected on the first frame, then set live by
        // the Settings page; 0 = not yet initialized.
        private float _uiScale;

        /// <summary>The live UI scale, read/written by the Settings page (<see cref="SettingsPage"/>). Clamped to
        /// a sane band on set; initialized from <see cref="AutoDetectUiScale"/> on the first <see cref="OnGUI"/>.</summary>
        internal float UiScale
        {
            get => _uiScale;
            set => _uiScale = math.clamp(value, 0.25f, 6f);
        }

        // The navigation stack — Peek() is the visible page. Never empty once OnEnable has run (root is pushed).
        private readonly Stack<IMenuPage> _stack = new Stack<IMenuPage>();
        private bool _visible;

        // Arbitrary, stable window id — only needs to be unique among concurrent GUI.Window calls in the scene.
        private const int WindowId = 0x0DEB6;

        /// <summary>The map component pages act on (camera / symbols). Null until wired / before Play.</summary>
        internal MapViewComponent MapComponent => Map;

        /// <summary>Open a sub-page (pushes it onto the navigation stack; a null page is ignored).</summary>
        /// <param name="page">The page to show.</param>
        internal void Push(IMenuPage page) { if (page != null) _stack.Push(page); }

        /// <summary>Return to the previous page. No-op at the root (the stack never drops below one page).</summary>
        internal void Pop() { if (_stack.Count > 1) _stack.Pop(); }

        // Seed the root page. OnEnable (not the ctor) because a MonoBehaviour's fields are Inspector-assigned
        // after construction, and re-enabling should not wipe an already-built stack.
        private void OnEnable()
        {
            if (_stack.Count == 0) _stack.Push(new RootMenuPage());
        }

        private void OnGUI()
        {
            // Everything below is in logical px = device px / scale; IMGUI maps mouse input by the inverse, so
            // clicks still land. The matrix is restored on every path, including hidden.
            if (_uiScale <= 0f) _uiScale = AutoDetectUiScale(); // lazy auto-detect on the first frame
            float scale = _uiScale;
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            HandleToggleKey();
            if (TopRightCornerToggle) DrawCornerHotspot(scale);
            if (_visible)
            {
                if (_stack.Count == 0) _stack.Push(new RootMenuPage()); // defensive — OnEnable normally seeds this
                WindowRect = GUI.Window(WindowId, WindowRect, DrawWindow, _stack.Peek().Title);
            }

            GUI.matrix = prevMatrix;
        }

        /// <summary>Auto-detect a legible UI scale from display density (Screen.dpi / 96, clamped to a sane
        /// band), or a default when the platform reports no density (Screen.dpi can be 0). The Settings page's
        /// "reset to auto" button also calls this.</summary>
        internal float AutoDetectUiScale()
        {
            float dpi = Screen.dpi;
            return dpi > 1f ? math.clamp(dpi / 96f, 1f, 3f) : 1.5f;
        }

        // Toggle on the ToggleKey's KeyDown. OnGUI runs every frame regardless of _visible, so this catches the
        // key while the menu is hidden too. Event.Use() consumes it so it doesn't also reach the game.
        private void HandleToggleKey()
        {
            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == ToggleKey)
            {
                _visible = !_visible;
                e.Use();
            }
        }

        // A small always-present button in the top-right corner (absolute rect — outside any GUILayout group).
        // GUI.matrix is scaled, so work in logical px: divide the device width by the scale to reach the corner.
        private void DrawCornerHotspot(float scale)
        {
            float logicalWidth = Screen.width / scale;
            var r = new Rect(logicalWidth - 30f, 4f, 26f, 22f);
            if (GUI.Button(r, "≡")) _visible = !_visible;
        }

        // The window callback. Chrome (Back / Close) + the current page's content. State changes are DEFERRED to
        // the end so the control set stays identical across this frame's Layout and Repaint passes (IMGUI rule).
        private void DrawWindow(int id)
        {
            IMenuPage page = _stack.Peek();

            bool back = false, close = false;
            using (new GUILayout.HorizontalScope())
            {
                if (_stack.Count > 1) back = GUILayout.Button("< Back", GUILayout.Width(72f));
                GUILayout.FlexibleSpace();
                close = GUILayout.Button("Close", GUILayout.Width(60f));
            }
            GUILayout.Space(4f);

            page.Draw(this);

            GUI.DragWindow(new Rect(0f, 0f, WindowRect.width, 20f));

            if (back) Pop();
            if (close) _visible = false;
        }
    }
}
