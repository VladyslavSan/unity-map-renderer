using UnityEngine;

namespace MapRenderer.Unity.Common
{
    /// <summary>
    /// Cross-cutting helpers for <see cref="UnityEngine.Object"/> lifetime (not rendering-specific).
    /// </summary>
    public static class UnityObjectExtensions
    {
        /// <summary>
        /// Destroys a Unity object with the mode-appropriate call, the single home of the play-vs-edit branch.
        /// <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/> is a no-op-with-error in edit mode, so
        /// edit mode uses <see cref="UnityEngine.Object.DestroyImmediate(UnityEngine.Object, bool)"/>. Play mode
        /// does not use it, because it runs synchronously mid-frame. Null-safe; main-thread only.
        /// </summary>
        /// <param name="obj">The object to destroy; ignored if null/already destroyed.</param>
        /// <param name="allowDestroyingAssets">Forwarded to <c>DestroyImmediate</c> in edit mode only —
        /// pass <c>true</c> for runtime-built meshes Unity may classify as assets. Irrelevant in Play mode.</param>
        public static void DestroySafely(this UnityEngine.Object obj, bool allowDestroyingAssets = false)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj, allowDestroyingAssets);
        }
    }
}
