using Cysharp.Threading.Tasks;
using UnityEngine;

using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App.Menu
{
    /// <summary>
    /// Style-switcher page (UMR-143): a button per committed style; clicking one applies it to the live map
    /// via <see cref="MapViewComponent.SetStyle(string,System.Threading.CancellationToken)"/> — the same
    /// path <see cref="MapHost"/> uses at startup, so a runtime switch exercises real restyle behaviour
    /// (not a scene reload). The currently-applied style is marked.
    /// </summary>
    internal sealed class StylesPage : IMenuPage
    {
        // Label + bare StreamingAssets path, resolved via MapHost.ResolveStyleUri at draw/click time. Add an
        // entry here to make a style selectable — no other wiring needed.
        private static readonly (string Label, string Path)[] Styles =
        {
            ("Liberty",         "Fixtures/liberty.json"),
            ("Liberty (Night)", "Fixtures/liberty-night.json"),
        };

        /// <inheritdoc/>
        public string Title => "Styles";

        /// <inheritdoc/>
        public void Draw(MenuOverlay menu)
        {
            MapViewComponent map = menu.MapComponent;
            if (map == null)
            {
                GUILayout.Label("No live MapView (enter Play mode).");
                return;
            }

            GUILayout.Label("Select a style to apply it to the live map.");

            string current = map.StyleId;
            foreach ((string label, string path) in Styles)
            {
                string uri = MapHost.ResolveStyleUri(path);
                string text = uri == current ? $"* {label}" : label;
                if (GUILayout.Button(text)) map.SetStyle(uri).Forget();
            }
        }
    }
}
