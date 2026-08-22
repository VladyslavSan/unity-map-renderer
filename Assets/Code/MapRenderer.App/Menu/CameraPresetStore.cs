using System.Globalization;
using UnityEngine;

namespace MapRenderer.App.Menu
{
    /// <summary>A saved camera pose — the full set of values needed to restore a view via
    /// <c>MapCamera.Apply(CameraPropertiesUpdate)</c>. FOV is captured for completeness; the load path applies
    /// LookAt/zoom/heading/tilt (the pose the user framed), leaving the lens as the live camera's.</summary>
    internal struct CameraPreset
    {
        /// <summary>LookAt latitude (degrees).</summary>
        public double Latitude;
        /// <summary>LookAt longitude (degrees).</summary>
        public double Longitude;
        /// <summary>Canonical MapLibre zoom.</summary>
        public double Zoom;
        /// <summary>Heading / bearing (degrees CW from north).</summary>
        public double Heading;
        /// <summary>Tilt / pitch from top-down (degrees).</summary>
        public double Tilt;
        /// <summary>Vertical field of view (degrees) at capture time.</summary>
        public double FovDeg;
    }

    /// <summary>
    /// PlayerPrefs-backed camera-preset slots for the debug menu (UMR-78) — the app-local persistence behind the
    /// <see cref="CameraPresetsPage"/>. One string entry per slot, so presets survive a Play-session restart.
    ///
    /// <para>Poses are serialized field-by-field with the round-trip-exact <c>G17</c> format under the invariant
    /// culture — NOT <c>JsonUtility</c>, whose double formatting is lossy and would silently degrade lat/lon
    /// precision. The serialize/deserialize pair is <c>internal static</c> so an EditMode test pins the exact
    /// round-trip directly.</para>
    /// </summary>
    internal sealed class CameraPresetStore
    {
        /// <summary>The production PlayerPrefs key namespace for the demo's preset slots. A TEST must pass its own
        /// prefix (see <paramref name="keyPrefix"/>): editor PlayerPrefs are a single per-project store shared with
        /// Play mode, so a test that cleared slots under THIS prefix would delete the user's real saved presets on
        /// every gate run — the "presets keep resetting" bug this constant's isolation fixes.</summary>
        internal const string DefaultKeyPrefix = "mapdemo.debugmenu.camslot.";

        private readonly string _keyPrefix;

        /// <summary>Number of preset slots this store manages.</summary>
        public int SlotCount { get; }

        /// <summary>Create a store over <paramref name="slotCount"/> slots (default 10).</summary>
        /// <param name="slotCount">How many slots to expose.</param>
        /// <param name="keyPrefix">The PlayerPrefs key namespace. Defaults to the production
        /// <see cref="DefaultKeyPrefix"/>; a test MUST override it so its clears never touch real presets.</param>
        public CameraPresetStore(int slotCount = 10, string keyPrefix = DefaultKeyPrefix)
        {
            SlotCount = slotCount;
            _keyPrefix = keyPrefix;
        }

        private string Key(int slot) => _keyPrefix + slot.ToString(CultureInfo.InvariantCulture);

        /// <summary>True if <paramref name="slot"/> holds a saved preset.</summary>
        /// <param name="slot">Slot index.</param>
        public bool Has(int slot) => PlayerPrefs.HasKey(Key(slot));

        /// <summary>Write <paramref name="preset"/> into <paramref name="slot"/> (overwriting any existing) and flush.</summary>
        /// <param name="slot">Slot index.</param>
        /// <param name="preset">The pose to store.</param>
        public void Save(int slot, in CameraPreset preset)
        {
            PlayerPrefs.SetString(Key(slot), Serialize(preset));
            PlayerPrefs.Save();
        }

        /// <summary>Read the preset in <paramref name="slot"/>; false (and default) if empty or corrupt.</summary>
        /// <param name="slot">Slot index.</param>
        /// <param name="preset">The loaded pose, on success.</param>
        public bool TryLoad(int slot, out CameraPreset preset)
        {
            preset = default;
            return PlayerPrefs.HasKey(Key(slot)) && TryDeserialize(PlayerPrefs.GetString(Key(slot)), out preset);
        }

        /// <summary>Delete <paramref name="slot"/>'s preset (if any) and flush.</summary>
        /// <param name="slot">Slot index.</param>
        public void Clear(int slot)
        {
            PlayerPrefs.DeleteKey(Key(slot));
            PlayerPrefs.Save();
        }

        /// <summary>Serialize a pose to the persisted string form: six <c>G17</c> doubles joined by <c>;</c>.</summary>
        /// <param name="p">The pose.</param>
        internal static string Serialize(in CameraPreset p) => string.Join(";", new[]
        {
            D(p.Latitude), D(p.Longitude), D(p.Zoom), D(p.Heading), D(p.Tilt), D(p.FovDeg),
        });

        /// <summary>Parse a string produced by <see cref="Serialize"/>. False (and default) on any malformed input
        /// — wrong field count or a non-numeric field — so a corrupt PlayerPrefs entry degrades to "empty slot".</summary>
        /// <param name="s">The persisted string.</param>
        /// <param name="p">The parsed pose, on success.</param>
        internal static bool TryDeserialize(string s, out CameraPreset p)
        {
            p = default;
            if (string.IsNullOrEmpty(s)) return false;
            string[] f = s.Split(';');
            if (f.Length < 6) return false;
            if (!P(f[0], out double lat) || !P(f[1], out double lon) || !P(f[2], out double zoom) ||
                !P(f[3], out double heading) || !P(f[4], out double tilt) || !P(f[5], out double fov))
                return false;
            p = new CameraPreset
            {
                Latitude = lat, Longitude = lon, Zoom = zoom, Heading = heading, Tilt = tilt, FovDeg = fov,
            };
            return true;
        }

        private static string D(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

        private static bool P(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
