// Test-only helper: builds a fill mesh from the fixture via StyledFillTileBuilder and creates a lit
// material.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.Rendering.Materials;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
namespace MapRenderer.Tests
{
    /// <summary>
    /// Builds a fill <see cref="Mesh"/> from the committed fixture tile via
    /// <see cref="StyledFillTileBuilder.BuildMesh"/> on a new <see cref="GameObject"/>, scaled and centred to
    /// fit <c>viewSize</c> world units. The returned material carries every shader property
    /// (<c>_BaseColor</c>, <c>_Opacity</c>, …). Callers must <c>Object.DestroyImmediate</c> the GameObject.
    /// </summary>
    internal static class FillSceneHelper
    {
        private const float DefaultViewSize = 100f;

        // ── Fixture loading ──────────────────────────────────────────────────────
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
            string layerName = "countries",
            IProjection projection = null, // null ⇒ WebMercator; pass a SphericalProjection for a globe
            bool fitToView = true,         // false ⇒ identity transform; the caller places the GO itself
            // Test-only oracle knob — see SyncMeshWrite.Fill. Production never sets it; a fixture that
            // measures the boundary band's own contribution renders the same scene with and without it.
            bool suppressBoundaryBand = false)
        {
            byte[] bytes = SampleTileFixture.Bytes();
            using MvtTile mvtTile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, bytes);

            // Build a minimal StyleLayer matching the layer name + optional color expression.
            var styleLayerJson = BuildStyleLayerJson(layerName, fillColorExpression);
            var style = StyleParser.Parse(styleLayerJson);
            var fillStyleLayer = style.Layers[0];
            var paint = ((Fill.StyleLayer)fillStyleLayer).Paint;

            var mvtLayer = SourceLayerResolver.ResolveTileLayer(fillStyleLayer, mvtTile);
            if (mvtLayer == null)
            {
                // Layer not found — return a GO with no mesh so tests can check for null.
                var emptyGo = new GameObject("FillSceneHelper_Empty");
                emptyGo.AddComponent<MeshFilter>();
                emptyGo.AddComponent<MeshRenderer>();
                return (emptyGo, null);
            }

            var selected = TestTileMeshBuilder.Select(fillStyleLayer, mvtLayer, styleZoom);

            // The builder derives the origin from (id, projection); the decode above used the same id.
            // The LAYER's own buffer is borrowed.
            Mesh mesh = TestTileMeshBuilder.BuildFillFromLayer(
                mvtLayer, selected, paint, styleZoom, new TileId { Z = 0, X = 0, Y = 0 }, projection,
                suppressBoundaryBand: suppressBoundaryBand);

            var mapGo = new GameObject("FillSceneHelper");
            var mf = mapGo.AddComponent<MeshFilter>();
            var mr = mapGo.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;

            // A PLAIN material, not a Material Variant: a runtime keyword toggle (e.g. _NORMALMAP) has no
            // effect on a variant clone. The painter contract restores the per-layer state (white, ZWrite off).
            var fillShader = MapMaterialSetTestUtil.Load().FillMaterial.shader;
            var mat = new Material(fillShader) { name = "FillSceneHelper_Fill" };
            FillMaterialTweaker.ApplyPainterContract(mat);
            var applier = new MapRenderer.Unity.Rendering.Style.ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new MapRenderer.Unity.Rendering.Style.StyleFrameInputs(styleZoom, 1.0, 0.0));
            mr.sharedMaterial = mat;

            // FitToView: scale+center so the mesh fits in viewSize world units. Skipped when the caller
            // drives placement itself (a real camera-relative ENU rebase).
            if (mesh != null && fitToView) FitToView(mapGo.transform, mesh, viewSize);

            return (mapGo, mat);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void FitToView(Transform t, Mesh mesh, float viewSize)
        {
            Bounds b   = mesh.bounds;
            // Use all three dims: a flat Mercator mesh has size.y ≈ 0, but a globe (ECEF) mesh spans Y too
            // and must be fit in 3D.
            float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z), 1e-6f);
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
