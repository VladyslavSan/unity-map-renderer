using UnityEngine;

namespace MapRenderer.App.Menu
{
    /// <summary>Settings page: runtime debug-UI preferences. Currently the UI scale — auto-detected from the
    /// display on the first frame, then tweakable here with a live slider (IMGUI has no DPI awareness of its
    /// own, so this is how the menu stays legible across displays). "Reset" returns to the auto-detected value.</summary>
    internal sealed class SettingsPage : IMenuPage
    {
        /// <inheritdoc/>
        public string Title => "Settings";

        /// <inheritdoc/>
        public void Draw(MenuOverlay menu)
        {
            GUILayout.Label($"UI scale: {menu.UiScale:F2}×  (auto-detected, adjustable)");
            menu.UiScale = GUILayout.HorizontalSlider(menu.UiScale, 0.5f, 4f);
            GUILayout.Space(8f);
            if (GUILayout.Button("Reset to auto-detected")) menu.UiScale = menu.AutoDetectUiScale();
        }
    }
}
