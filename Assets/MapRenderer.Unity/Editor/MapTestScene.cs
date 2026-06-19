using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapRenderer.Unity.Editor
{
    /// <summary>
    /// One-click generator for a committed visual-test scene. Builds a scene with the country fills
    /// already decoded, triangulated, framed top-down, and saved to Assets/Scenes/ — so checking a
    /// fill change is "open scene → look" instead of a manual setup ritual.
    /// </summary>
    public static class MapTestScene
    {
        private const string ScenePath = "Assets/Scenes/FillTest.unity";
        private const string FixturePath = "Assets/Fixtures/sample-tile.bytes";

        [MenuItem("MapRenderer/Create Fill Test Scene")]
        public static void Create()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Top-down orthographic camera framing a ~100-unit map centered at the origin.
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 200f, 0f);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.orthographic = true;
                cam.orthographicSize = 70f;
                cam.farClipPlane = 1000f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.11f, 0.15f, 1f);
            }

            // Add a directional light so the MapRenderer/Fill (URP Lit) material renders brightly.
            // Without a light, fills look near-black — misleading for a "look" workflow.
            var lightGo  = new GameObject("DirectionalLight");
            var light    = lightGo.AddComponent<Light>();
            light.type       = LightType.Directional;
            light.intensity  = 1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, 30f, 0f); // angled down toward XZ fill

            var go = new GameObject("MapFill");
            var boot = go.AddComponent<MapFillBootstrap>(); // RequireComponent adds MeshFilter + MeshRenderer
            boot.Tile = AssetDatabase.LoadAssetAtPath<TextAsset>(FixturePath);
            // Wire the committed MapFill.mat template so the scene uses the live template material
            // (review comment #2: MapFillBootstrap must use MapFill.mat, not the new-Material fallback).
            var templateMat = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/MapRenderer.Unity/Materials/MapFill.mat");
            if (templateMat != null)
                boot.FillMaterial = templateMat;
            else
                Debug.LogWarning("[MapTestScene] MapFill.mat not found — FillMaterial left null. " +
                                 "Re-import Assets to fix.");
            boot.FitToView = true;
            boot.ViewSize = 100f;
            boot.Build(); // build now so the mesh is visible without entering Play mode

            Selection.activeGameObject = go;
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[MapTestScene] Created {ScenePath}. Country fills are built and framed; " +
                      "reopen the scene any time to re-check (no Play needed).");
        }
    }
}
