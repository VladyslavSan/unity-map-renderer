using UnityEngine;

namespace MapRenderer.App.Menu
{
    /// <summary>The overlay's landing page: a list of buttons, each opening a sub-page. New pages are added here.</summary>
    internal sealed class RootMenuPage : IMenuPage
    {
        /// <inheritdoc/>
        public string Title => "Debug Menu";

        /// <inheritdoc/>
        public void Draw(MenuOverlay menu)
        {
            if (GUILayout.Button("Camera Presets")) menu.Push(new CameraPresetsPage());
            if (GUILayout.Button("Styles")) menu.Push(new StylesPage());
            if (GUILayout.Button("Diagnostics")) menu.Push(new DiagnosticsPage());
            if (GUILayout.Button("Settings")) menu.Push(new SettingsPage());
        }
    }
}
