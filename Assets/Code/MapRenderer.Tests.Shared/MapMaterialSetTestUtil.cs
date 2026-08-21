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
        /// <summary>Loads the committed LIT production <see cref="MapMaterialSet"/> (fails if missing/unassigned).
        /// Resolved by asset TYPE + <see cref="MapMaterialSet.RenderMode"/> via
        /// <see cref="AssetDatabase.FindAssets(string)"/> — never a hardcoded path/name, so renaming or moving
        /// the asset (as the unlit epic's Lit/Unlit split did) cannot break the tests.</summary>
        internal static MapMaterialSet Load()
        {
            MapMaterialSet set = null;
            var litPaths = new System.Collections.Generic.List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(MapMaterialSet)))
            {
                var path      = AssetDatabase.GUIDToAssetPath(guid);
                var candidate = AssetDatabase.LoadAssetAtPath<MapMaterialSet>(path);
                if (candidate != null && candidate.RenderMode == RenderMode.Lit)
                {
                    set = candidate;
                    litPaths.Add(path);
                }
            }
            Assert.AreEqual(1, litPaths.Count,
                $"Expected exactly one committed Lit MapMaterialSet in the project; found {litPaths.Count} " +
                $"[{string.Join(", ", litPaths)}].");
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
