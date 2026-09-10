#if UNITY_EDITOR
// S58: MaterialFactory.Create* now require a MapMaterialSet (the legacy Shader.Find fallback was
// retired). Tests that build live map materials load the committed PRODUCTION config asset here, so
// they exercise the real baseline rather than synthesizing one.
using NUnit.Framework;
using UnityEditor;
using MapRenderer.Unity.Rendering.Materials;
namespace MapRenderer.Tests
{
    internal static class MapMaterialSetTestUtil
    {
        /// <summary>Loads a committed production <see cref="MapMaterialSet"/> (fails if missing/unassigned).
        /// Resolved by asset TYPE + <see cref="MapMaterialSet.RenderMode"/> via
        /// <see cref="AssetDatabase.FindAssets(string)"/> — never a hardcoded path/name, so renaming or moving
        /// the asset (as the unlit epic's Lit/Unlit split did) cannot break the tests.</summary>
        /// <param name="mode">Which committed set to load; defaults to <see cref="RenderMode.Lit"/>, the
        /// mode every caller predating the parameter asked for implicitly.</param>
        internal static MapMaterialSet Load(RenderMode mode = RenderMode.Lit)
        {
            MapMaterialSet set = null;
            var matchingPaths = new System.Collections.Generic.List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(MapMaterialSet)))
            {
                var path      = AssetDatabase.GUIDToAssetPath(guid);
                var candidate = AssetDatabase.LoadAssetAtPath<MapMaterialSet>(path);
                if (candidate != null && candidate.RenderMode == mode)
                {
                    set = candidate;
                    matchingPaths.Add(path);
                }
            }
            Assert.AreEqual(1, matchingPaths.Count,
                $"Expected exactly one committed {mode} MapMaterialSet in the project; found {matchingPaths.Count} " +
                $"[{string.Join(", ", matchingPaths)}].");
            Assert.IsNotNull(set.FillMaterial, "MapMaterialSet.FillMaterial must be assigned for tests.");
            Assert.IsNotNull(set.LineMaterial, "MapMaterialSet.LineMaterial must be assigned for tests.");
            // SymbolTextWorld is REQUIRED (MapMaterialSet.Validate) — the only point-text draw path after the
            // screen-space path was retired.
            Assert.IsNotNull(set.SymbolTextWorld, "MapMaterialSet.SymbolTextWorld must be assigned for tests.");
            return set;
        }
    }
}
#endif
