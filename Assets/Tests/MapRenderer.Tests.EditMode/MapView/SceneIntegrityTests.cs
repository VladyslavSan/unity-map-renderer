// Every committed .unity scene must open headless with ZERO missing-script components, so a moved or
// renamed MonoBehaviour whose m_Script GUID fails to resolve reds here. It opens scenes and never saves them.

using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapRenderer.Tests.MapViews
{
    [TestFixture]
    public class SceneIntegrityTests
    {
        [TearDown]
        public void TearDown()
        {
            // Leave a clean empty scene so we don't hold a project scene open for subsequent tests.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void AllCommittedScenes_HaveNoMissingScripts()
        {
            // Project scenes only — FindAssets also returns scenes inside read-only packages
            // (e.g. com.unity.entities test subscenes), which OpenScene refuses to open.
            var scenePaths = new System.Collections.Generic.List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:SceneAsset"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.StartsWith("Assets/")) scenePaths.Add(p);
            }
            Assert.That(scenePaths, Is.Not.Empty,
                "Expected at least one .unity scene under Assets/ to validate.");

            var report = new StringBuilder();
            int totalMissing = 0;

            foreach (string path in scenePaths)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(includeInactive: true))
                    {
                        int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                        if (missing > 0)
                        {
                            totalMissing += missing;
                            report.AppendLine($"  {missing} missing script(s): {path} → '{HierarchyPath(t)}'");
                        }
                    }
                }
            }

            Assert.That(totalMissing, Is.Zero,
                $"Found {totalMissing} component(s) with a missing/unresolvable script across committed scenes:\n{report}");
        }

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
