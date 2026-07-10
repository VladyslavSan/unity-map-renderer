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
        private const string AssetPath = "Assets/Settings/Map/MapMaterialSet.asset";

        /// <summary>Loads the committed production <see cref="MapMaterialSet"/> (fails if missing/unassigned).</summary>
        internal static MapMaterialSet Load()
        {
            var set = AssetDatabase.LoadAssetAtPath<MapMaterialSet>(AssetPath);
            Assert.IsNotNull(set, $"MapMaterialSet asset missing at '{AssetPath}'.");
            Assert.IsNotNull(set.FillMaterial, "MapMaterialSet.FillMaterial must be assigned for tests.");
            Assert.IsNotNull(set.LineMaterial, "MapMaterialSet.LineMaterial must be assigned for tests.");
            return set;
        }
    }
}
#endif
