// GlobeBackendSnapshotTests (S91-C, Slice 2) — proves the globe places correctly through a REAL render
// backend, not just the hand-wired snapshot. This is the coverage the Slice-1 review flagged as missing:
// the per-tile rebase-rotation branch (backends set rotation = quaternion(rebase) + position =
// TileToSceneRebased) is exercised in production code but no test drove a NON-identity rebase.
//
// Drives the GameObjects backend (its transform hierarchy is GPU-independent, so the placement is asserted
// numerically even headless), then renders it through the SAME CameraPoseMath.ComputeRelativePose orbit the live
// MapCamera uses — a correctly-oriented globe means the backend wiring matches the math proven in
// GlobePlacementTests.

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Backend;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

namespace MapRenderer.Tests.Visual
{
    public class GlobeBackendSnapshotTests
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        [Test]
        public void GameObjectBackend_PlacesAndRendersGlobe()
        {
            var proj   = new SphericalProjection();
            var tid    = new TileId { Z = 0, X = 0, Y = 0 };
            var lookAt = new GeoCoordinate { Latitude = 20.0, Longitude = 12.0 }; // over Africa

            // Globe-baked fill mesh (vertices ECEF, relative to the tile's projected SW corner).
            var (meshGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"rgba\",95,165,95,1]", projection: proj, fitToView: false);
            Mesh mesh = meshGo.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(meshGo); // keep the mesh + material, drop the helper GO
            if (mat != null) mat.SetFloat("_Cull", 2f); // back-face cull → near hemisphere only

            // The SAME projected SW-corner origin the mesh was baked against (Level-1 == Level-2 origin).
            double3 tileOriginRender  = TileRenderOrigin.Project(tid, proj);
            double3 sceneOriginRender = proj.Project(lookAt);
            float3x3 rebase           = math.transpose(proj.TangentBasisAt(lookAt));
            var frame                 = new SceneFrame { SceneOriginRender = sceneOriginRender, Rebase = rebase };

            var backend = new GameObjectTileRenderer(new[] { mat });
            GameObject cameraGo = null, lightGo = null;
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                backend.AddTileLayer(mesh, tileOriginRender, 0, tid);
                backend.Rebuild(frame);

                // ── Numerical proof: the backend applied the globe rebase (rotation + rebased position). ──
                Transform container = backend.Container(tid);
                Assert.IsNotNull(container, "backend must create the tile container");

                float3 expectedPos = FloatingOrigin.TileToSceneRebased(tileOriginRender, sceneOriginRender, rebase);
                Vector3 pos = container.localPosition;
                Assert.AreEqual(expectedPos.x, pos.x, 1f, "container X == TileToSceneRebased.x");
                Assert.AreEqual(expectedPos.y, pos.y, 1f, "container Y == TileToSceneRebased.y (non-zero on the globe)");
                Assert.AreEqual(expectedPos.z, pos.z, 1f, "container Z == TileToSceneRebased.z");
                // NOT identity — this is the branch Slice 1 left untested (Mercator would give pos.y == 0).
                Assert.Greater(math.abs(pos.y), 1f, "globe placement must lift the tile off the XZ plane");

                quaternion expectedRot = new quaternion(rebase);
                Quaternion rot = container.localRotation;
                Assert.AreEqual(expectedRot.value.x, rot.x, 1e-4f, "container rotation.x == quaternion(rebase)");
                Assert.AreEqual(expectedRot.value.y, rot.y, 1e-4f, "container rotation.y == quaternion(rebase)");
                Assert.AreEqual(expectedRot.value.z, rot.z, 1e-4f, "container rotation.z == quaternion(rebase)");
                Assert.AreEqual(expectedRot.value.w, rot.w, 1e-4f, "container rotation.w == quaternion(rebase)");
                Assert.AreNotEqual(quaternion.identity.value.x, rot.x, "globe rotation must not be identity");

                // ── Visual proof: render the backend's globe through the real ComputeRelativePose orbit. ──
                double altitude = 2.5 * SphericalProjection.Radius;
                CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(0.0),
                    out double3 cpos, out double3 fwd, out double3 up);

                cameraGo   = new GameObject("GlobeBackendCamera");
                var camera = cameraGo.AddComponent<Camera>();
                camera.transform.position = new Vector3((float)cpos.x, (float)cpos.y, (float)cpos.z);
                camera.transform.rotation = Quaternion.LookRotation(
                    new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                    new Vector3((float)up.x,  (float)up.y,  (float)up.z));
                camera.fieldOfView     = 35f;
                camera.nearClipPlane   = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
                camera.farClipPlane    =                  (float)CameraPoseMath.FarClip(altitude);
                camera.clearFlags      = CameraClearFlags.SolidColor;
                camera.backgroundColor = OceanBg;
                camera.enabled         = false;

                lightGo     = new GameObject("GlobeBackendLight");
                var light   = lightGo.AddComponent<Light>();
                light.type      = LightType.Directional;
                light.intensity = 1.2f;
                lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);

                snap.Render(camera);
                string path = snap.WritePng("globe-backend-countries.png");
                TestContext.WriteLine($"[GlobeBackendSnapshotTests] wrote {path}");
                if (snap.IsAllBlack())
                    Assert.Inconclusive("Globe render is all-background — no GPU context (headless). PNG still written.");
            }
            finally
            {
                snap.Dispose();
                backend.Dispose();
                if (cameraGo != null) Object.DestroyImmediate(cameraGo);
                if (lightGo  != null) Object.DestroyImmediate(lightGo);
                if (mat != null) Object.DestroyImmediate(mat);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
