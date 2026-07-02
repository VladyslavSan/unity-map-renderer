// S54: test-only helper that replaces MapFillBootstrap in snapshot tests.
// Builds a fill mesh from the fixture via StyledFillTileBuilder and creates a lit material.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.Rendering.Materials;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Meshing;
namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Replaces the retired <c>MapFillBootstrap</c> in snapshot tests (S54).
    ///
    /// Builds a fill <see cref="Mesh"/> from the committed fixture tile via
    /// <see cref="StyledFillTileBuilder.BuildMesh"/>, attaches it to a new
    /// <see cref="GameObject"/> with a <c>MeshFilter</c> + <c>MeshRenderer</c>, and
    /// scales/centres the transform so the mesh fits in <c>viewSize</c> world units (matching
    /// the old <see cref="MapFillBootstrap.FitToView"/> behaviour).
    ///
    /// The returned material is created from <c>MaterialFactory.CreateFillMaterial</c> so
    /// all shader properties (<c>_BaseColor</c>, <c>_Opacity</c>, etc.) are available.
    ///
    /// Callers must <c>Object.DestroyImmediate</c> the returned GameObject when done.
    /// </summary>
    internal static class FillSceneHelper
    {
        private const float DefaultViewSize = 100f;

        // ── Fixture loading ──────────────────────────────────────────────────────

        internal static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        // ── Single-layer fill GO ─────────────────────────────────────────────────

        /// <summary>
        /// Build a fill GameObject from the fixture using the <c>countries</c> layer.
        /// Optionally pass a data-driven <paramref name="fillColorExpression"/> (null for white).
        /// Returns <c>(mapGo, liveMaterial)</c>. mapGo has MeshFilter + MeshRenderer attached.
        /// Material default: <c>_BaseColor=white</c>, <c>_Opacity=1</c>.
        /// The transform is scaled/centred to <paramref name="viewSize"/> world units (FitToView).
        /// </summary>
        public static (GameObject mapGo, Material liveMaterial) BuildFillGo(
            string fillColorExpression = null,
            double styleZoom = 0.0,
            float viewSize = DefaultViewSize,
            string layerName = "countries")
        {
            byte[] bytes = FixtureBytes();
            MvtTile mvtTile = MvtDecoder.Decode(bytes);

            // Build a minimal StyleLayer matching the layer name + optional color expression.
            var styleLayerJson = BuildStyleLayerJson(layerName, fillColorExpression);
            var style = StyleParser.Parse(styleLayerJson);
            var fillStyleLayer = style.Layers[0];
            var paint = new Fill.PaintProperties(fillStyleLayer);

            var mvtLayer = SourceLayerResolver.ResolveMvtLayer(fillStyleLayer, mvtTile);
            if (mvtLayer == null)
            {
                // Layer not found — return a GO with no mesh so tests can check for null.
                var emptyGo = new GameObject("FillSceneHelper_Empty");
                emptyGo.AddComponent<MeshFilter>();
                emptyGo.AddComponent<MeshRenderer>();
                return (emptyGo, null);
            }

            var features = FeatureSelector.SelectFeatures(fillStyleLayer, mvtTile, styleZoom);

            var (bMin, _) = new TileId { Z = 0, X = 0, Y = 0 }.MercatorBounds();
            var origin = new double2(bMin.x, bMin.y);

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                features, paint, styleZoom, mvtLayer.Extent, new TileId { Z = 0, X = 0, Y = 0 }, origin);

            var mapGo = new GameObject("FillSceneHelper");
            var mf = mapGo.AddComponent<MeshFilter>();
            var mr = mapGo.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;

            // Material — a PLAIN material on the Fill shader (NOT a Material Variant): this snapshot helper
            // toggles local shader keywords at runtime (e.g. _NORMALMAP), which does not take effect on a
            // runtime variant clone. Apply the painter contract via the tweaker to reproduce the per-layer
            // material state (white identity, ZWrite off) without the variant linkage. (S58.)
            var fillShader = MapMaterialSetTestUtil.Load().FillMaterial.shader;
            var mat = new Material(fillShader) { name = "FillSceneHelper_Fill" };
            FillMaterialTweaker.ApplyPainterContract(mat);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Opacity",  1f);
            mr.sharedMaterial = mat;

            // FitToView: scale+center so the mesh fits in viewSize world units (same as MapFillBootstrap).
            if (mesh != null) FitToView(mapGo.transform, mesh, viewSize);

            return (mapGo, mat);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void FitToView(Transform t, Mesh mesh, float viewSize)
        {
            Bounds b   = mesh.bounds;
            float maxDim = Mathf.Max(b.size.x, b.size.z, 1e-6f);
            float scale  = viewSize / maxDim;
            t.localScale    = Vector3.one * scale;
            t.localPosition = -b.center * scale;
        }

        /// <summary>
        /// Builds a minimal StyleParser-compatible JSON with one fill layer so that
        /// <see cref="FeatureSelector.SelectFeatures"/> picks up the right source layer.
        /// When <paramref name="colorExpr"/> is non-null it is embedded as <c>fill-color</c>.
        /// </summary>
        private static string BuildStyleLayerJson(string layerName, string colorExpr)
        {
            string fillColor = colorExpr != null
                ? colorExpr
                : "[\"rgba\",200,200,200,1]";

            return @"{
    ""version"": 8,
    ""name"": ""FillSceneHelper"",
    ""sources"": {
        ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
    },
    ""layers"": [
        {
            ""id"": """ + layerName + @""",
            ""type"": ""fill"",
            ""source"": ""maplibre"",
            ""source-layer"": """ + layerName + @""",
            ""paint"": {
                ""fill-color"": " + fillColor + @"
            }
        }
    ]
}";
        }
    }
}
