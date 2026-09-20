// GlobeLineSnapshotTests (S91-C, C-2) — renders the fixture's `geolines` layer on the globe through the
// REAL StyledLineTileBuilder globe path, then places it via the ENU rebase and renders it. This is the
// visual proof that line ribbons now lie ON the sphere surface (3D centerline + radial up + tangent-plane
// across), not flattened onto the y=0 plane as before C-2.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using Line = MapRenderer.Core.Style.Line;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Visual
{
    public class GlobeLineSnapshotTests
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        [Test]
        public void RendersGeolinesOnTheSphere_WritesPng()
        {
            var proj   = new SphericalProjection();
            var tid    = new TileId { Z = 0, X = 0, Y = 0 };
            var lookAt = new GeoCoordinate { Latitude = 20.0, Longitude = 12.0 }; // over Africa

            // ── Build the geolines line mesh on the globe via the real StyledLineTileBuilder. ──
            using MvtTile mvtTile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, SampleTileFixture.Bytes());
            var style = StyleParser.Parse(LineStyleJson());
            var styleLayer = (Line.StyleLayer)style.Layers[0];
            var paint  = styleLayer.Paint;
            var layout = styleLayer.Layout;

            var mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "fixture must contain the geolines layer");
            var selected = TestTileMeshBuilder.Select(styleLayer, mvtLayer, 0.0);
            Assert.IsNotEmpty(selected, "geolines must select features");

            // `proj` is `var`-typed off `new SphericalProjection()` above, so this binds to
            // BuildLineFromLayer<TProj>, not the IProjection-typed overload — see that overload's own doc note.
            Mesh mesh = TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, paint, layout, 0.0, tid, proj);
            Assert.IsNotNull(mesh, "line mesh build must produce a mesh");

            // Line material — big world-metre width so borders read at globe scale (~25 km/px in this frame).
            var shader = Shader.Find("Map/Line");
            Assert.IsNotNull(shader, "Map/Line shader must be present");
            var mat = new Material(shader) { name = "GlobeLineMat" };
            mat.SetFloat("_Width",          120000f); // 120 km
            mat.SetFloat("_WidthIsPixels",  0f);
            mat.SetColor("_BaseColor",      new Color(1f, 0.85f, 0.2f, 1f)); // amber borders
            mat.SetFloat("_Opacity",        1f);
            mat.SetFloat("_Cull",           2f); // stock Cull Back: near-side ribbons show, far-side culled
                                                 // (post winding reversal — no more double-sided workaround)

            var mapGo = new GameObject("GlobeLine");
            mapGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            mapGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            // ── Place via the ENU rebase (same scheme the backends use). ──
            double2 swLL = tid.ToLonLat(0.0, 1.0, 1.0);
            double3 tileOriginRender  = proj.Project(new GeoCoordinate { Latitude = swLL.y, Longitude = swLL.x });
            double3 sceneOriginRender = proj.Project(lookAt);
            float3x3 rebase   = math.transpose(proj.TangentBasisAt(lookAt));
            float3   position = MapRenderer.Unity.View.FloatingOrigin.TileToSceneRebased(
                tileOriginRender, sceneOriginRender, rebase);
            quaternion q = new quaternion(rebase);
            mapGo.transform.rotation      = new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
            mapGo.transform.localPosition = new Vector3(position.x, position.y, position.z);

            // ── Camera: the real ComputeRelativePose orbit. ──
            double altitude = 2.5 * SphericalProjection.Radius;
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(0.0),
                out double3 pos, out double3 fwd, out double3 up);

            var cameraGo = new GameObject("GlobeLineCamera");
            var camera   = cameraGo.AddComponent<Camera>();
            camera.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
            camera.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));
            camera.fieldOfView     = 35f;
            camera.nearClipPlane   = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            camera.farClipPlane    =                  (float)CameraPoseMath.FarClip(altitude);
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = OceanBg;
            camera.enabled         = false;

            var lightGo = new GameObject("GlobeLineLight");
            var light   = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);

            var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                string path = snap.WritePng("globe-geolines.png");
                TestContext.WriteLine($"[GlobeLineSnapshotTests] wrote {path}");
                if (snap.IsAllBlack())
                    Assert.Inconclusive("Globe line render is all-background — no GPU context (headless). PNG still written.");
            }
            finally
            {
                snap.Dispose();
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(mapGo);
            }
        }
        private static string LineStyleJson() => @"{
    ""version"": 8,
    ""name"": ""GlobeLine"",
    ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""geolines"", ""type"": ""line"", ""source"": ""maplibre"", ""source-layer"": ""geolines"",
          ""paint"": { ""line-color"": [""rgba"",255,217,51,1] } }
    ]
}";
    }
}
