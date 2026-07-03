// GlobeSnapshotTests — renders the z=0 "countries" fixture tile projected onto a SPHERE (SphericalProjection)
// and writes a PNG. The z=0 tile is the whole world in one tile, so this is a full globe of countries in one
// mesh — the first visible proof that the projection pipeline renders a spherical earth (S91-C).
//
// Fills already bake the per-vertex radial normal (from the projection's Up), so the Lit material shades the
// sphere correctly. Positions are ECEF (origin-relative to the tile corner); FitToView frames the 3D bounds.

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Visual
{
    public class GlobeSnapshotTests
    {
        private const int SnapW = 512, SnapH = 512;

        // Ocean-ish clear colour so the sphere reads as an earth (land polygons over water background).
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        [Test]
        public void RendersCountriesOnASphere_WritesPng()
        {
            // Land polygons in green, projected onto the globe.
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: "[\"rgba\",95,165,95,1]",
                viewSize: 2f,                       // globe ≈ 2 units across (radius ≈ 1)
                projection: new SphericalProjection());

            // Back-face cull so only the near hemisphere shows (no far-side bleed through ocean gaps).
            // 0=Off, 1=Front, 2=Back. If the sphere renders inside-out, the tile→sphere winding is flipped.
            if (mat != null) mat.SetFloat("_Cull", 2f);

            // Directional light to shade the sphere (Lit material is near-black at ambient-only).
            var lightGo = new GameObject("GlobeLight");
            var light   = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);
            lightGo.transform.SetParent(mapGo.transform, worldPositionStays: true);

            // Perspective camera outside the globe, looking at the origin (the globe is centred there).
            var cameraGo = new GameObject("GlobeCamera");
            var camera   = cameraGo.AddComponent<Camera>();
            camera.transform.position = new Vector3(1.7f, 1.2f, -3.0f);
            camera.transform.rotation = Quaternion.LookRotation(-camera.transform.position, Vector3.up);
            camera.orthographic    = false;
            camera.fieldOfView     = 35f;
            camera.nearClipPlane   = 0.05f;
            camera.farClipPlane    = 100f;
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = OceanBg;
            camera.enabled         = false;

            var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                string path = snap.WritePng("globe-countries.png");
                TestContext.WriteLine($"[GlobeSnapshotTests] wrote {path}");

                // GPU-context guard: in a headless run with no GPU the render is all-background → Inconclusive,
                // not a failure (matches the other Visual snapshot tests).
                if (snap.IsAllBlack())
                    Assert.Inconclusive("Globe render is all-background — no GPU context (headless). PNG still written.");
            }
            finally
            {
                snap.Dispose();
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo); // light is parented to mapGo
            }
        }
    }
}
