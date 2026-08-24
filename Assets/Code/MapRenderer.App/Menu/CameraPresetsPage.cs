using MapRenderer.Core.View.Camera;
using UnityEngine;

using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App.Menu
{
    /// <summary>
    /// Camera-preset page (UMR-78): ~10 slots to Save the live camera pose and Load it back later — the
    /// reproducer for "where did I see that state?" (e.g. the 80k-symbol spike). Select a slot, then Save (store
    /// the current view) / Load (jump the camera to the slot) / Clear. Slots persist via
    /// <see cref="CameraPresetStore"/> (PlayerPrefs), so a saved spot survives a Play-session restart.
    /// </summary>
    internal sealed class CameraPresetsPage : IMenuPage
    {
        private readonly CameraPresetStore _store = new CameraPresetStore(10);
        private int _selected = -1;

        /// <inheritdoc/>
        public string Title => "Camera Presets";

        /// <inheritdoc/>
        public void Draw(MenuOverlay menu)
        {
            MapCamera camera = menu.MapComponent != null ? menu.MapComponent.Camera : null;
            if (camera == null)
            {
                GUILayout.Label("No live MapView (enter Play mode).");
                return;
            }

            GUILayout.Label("Select a slot, then Save (store current view) or Load (jump to it).");

            for (int i = 0; i < _store.SlotCount; i++)
            {
                string label = _store.TryLoad(i, out CameraPreset preset)
                    ? $"Slot {i}:  z{preset.Zoom:F1}  t{preset.Tilt:F0}°  h{preset.Heading:F0}°  " +
                      $"({preset.Latitude:F4}, {preset.Longitude:F4})"
                    : $"Slot {i}:  (empty)";

                // Button-styled toggle: clicking an unselected slot selects it; clicking the selected one keeps it
                // (selection is sticky — there is always a target for Save). Fixed one-control-per-slot layout.
                if (GUILayout.Toggle(_selected == i, label, GUI.skin.button) && _selected != i)
                    _selected = i;
            }

            GUILayout.Space(6f);

            bool hasSelection = _selected >= 0;
            bool occupied = hasSelection && _store.Has(_selected);
            using (new GUILayout.HorizontalScope())
            {
                GUI.enabled = hasSelection;
                if (GUILayout.Button("Save")) SaveTo(_selected, camera);

                GUI.enabled = occupied;
                if (GUILayout.Button("Load")) LoadFrom(_selected, camera);
                if (GUILayout.Button("Clear")) _store.Clear(_selected);

                GUI.enabled = true;
            }
        }

        // Read the live pose and write it into the slot.
        private void SaveTo(int slot, MapCamera camera)
        {
            CameraProperties c = camera.CurrentProperties;
            _store.Save(slot, new CameraPreset
            {
                Latitude = c.LookAt.Latitude,
                Longitude = c.LookAt.Longitude,
                Zoom = c.Zoom,
                Heading = c.Heading.Degrees,
                Tilt = c.Tilt.Degrees,
                FovDeg = c.VerticalFovDeg,
            });
        }

        // Apply the slot to the live camera via the same Apply seam CameraControlPanel uses (instant jump).
        private void LoadFrom(int slot, MapCamera camera)
        {
            if (!_store.TryLoad(slot, out CameraPreset p)) return;
            camera.Apply(new CameraPropertiesUpdate
            {
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                Zoom = p.Zoom,
                Heading = p.Heading,
                Tilt = p.Tilt,
            });
        }
    }
}
