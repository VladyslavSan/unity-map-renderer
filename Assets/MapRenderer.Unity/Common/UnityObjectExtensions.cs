using UnityEngine;

namespace MapRenderer.Unity.Common
{
    /// <summary>
    /// Cross-cutting helpers for <see cref="UnityEngine.Object"/> lifetime (not rendering-specific).
    /// </summary>
    public static class UnityObjectExtensions
    {
        /// <summary>
        /// Destroys a Unity object with the mode-appropriate call — the single home for the
        /// play-vs-edit branch that was previously copy-pasted across the mesh/material/texture/GameObject
        /// teardown paths.
        ///
        /// <para><b>Why the branch is unavoidable:</b> <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/>
        /// is deferred and is a no-op-with-error in edit mode, so the headless EditMode gate (which tears
        /// objects down outside Play) leaks them and trips the mesh-leak baseline;
        /// <see cref="UnityEngine.Object.DestroyImmediate(UnityEngine.Object, bool)"/> works in edit mode but
        /// is discouraged at runtime (synchronous mid-frame). So: <c>Destroy</c> in Play, <c>DestroyImmediate</c>
        /// in edit.</para>
        ///
        /// <para>Null-safe (Unity's overloaded <c>==</c> also treats already-destroyed objects as null).
        /// Main-thread only, like every <c>UnityEngine.Object</c> mutation.</para>
        /// </summary>
        /// <param name="obj">The object to destroy; ignored if null/already destroyed.</param>
        /// <param name="allowDestroyingAssets">Forwarded to <c>DestroyImmediate</c> in edit mode only —
        /// pass <c>true</c> for runtime-built meshes that Unity may classify as assets (mirrors the prior
        /// <c>TileManager</c>/<c>PreparedTileCache</c> mesh teardown). Irrelevant in Play mode.</param>
        public static void DestroySafely(this UnityEngine.Object obj, bool allowDestroyingAssets = false)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj, allowDestroyingAssets);
        }
    }
}
