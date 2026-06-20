using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Style;
using MapRenderer.Jobs;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MapRenderer.Unity
{
    /// <summary>
    /// MonoBehaviour bootstrap: decodes the sample tile's country fills and renders them as a single
    /// Unity Mesh using a URP Lit material (MapRenderer/Fill shader).
    ///
    /// Pipeline:
    ///   TextAsset (MVT bytes)
    ///   → MvtDecoder.Decode
    ///   → MvtGeometry.Decode (per feature)
    ///   → PolygonAssembler.Assemble (ring classification)
    ///   → Earcut.Triangulate (ear-clipping, main thread, tile space)
    ///   → ProjectTileVerticesJob (Burst: tile→Mercator→origin-relative float3)
    ///   → MeshBuilder → UnityEngine.Mesh → MeshFilter/MeshRenderer
    ///
    /// Material: MapRenderer/Fill (URP Lit, Cull Off, deferred-eligible).
    ///   Default template: Assets/MapRenderer.Unity/Materials/MapFill.mat.
    ///   Assign FillMaterial to use the template (or an instance) directly — change shader properties
    ///   on that Material to restyle with no mesh rebuild (live restyle, no rebuild).
    ///   Per docs/lit-rendering-design.md: NEVER use MaterialPropertyBlock on batched renderers
    ///   (silently disables SRP Batcher); use per-layer Material instances instead.
    ///
    /// UV0 (S34): tile-space [0,1] UVs supplied to MeshBuilder for texture/normal map sampling.
    /// Tangents (S34): constant float4(1,0,0,1) per vertex for normal map tangent space.
    ///
    /// Vertex COLOR (S12): when FillColorExpression is set, per-feature vertex colors are baked from
    ///   a data-driven paint expression (evaluated against decoded feature properties).  When null/empty,
    ///   vertex colors default to white (S11 zoom-uniform behavior preserved exactly).
    ///
    /// Winding: Cull Off shader avoids front/back visibility issues for this spike.
    /// Correct winding is a deliberate follow-up.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class MapFillBootstrap : MonoBehaviour
    {
        // Path to the committed template material (relative to Assets/).
        private const string TemplateMaterialPath =
            "Assets/MapRenderer.Unity/Materials/MapFill.mat";

        [Tooltip("Drag Assets/Fixtures/sample-tile.bytes here. If empty, it is loaded from disk (Editor).")]
        public TextAsset Tile;

        [Tooltip("Layer name to render. Default: countries.")]
        public string LayerName = "countries";

        [Tooltip("URP Lit fill material (MapRenderer/Fill shader, Cull Off). " +
                 "Defaults to the committed template Assets/MapRenderer.Unity/Materials/MapFill.mat. " +
                 "Change properties on this Material instance to restyle with no mesh rebuild (live restyle). " +
                 "Never assign via MaterialPropertyBlock — use per-layer Material instances " +
                 "(see docs/lit-rendering-design.md).")]
        public Material FillMaterial;

        [Tooltip("S12: Optional data-driven fill-color expression (MapLibre expression JSON). " +
                 "When non-empty, per-feature vertex colors are baked from this expression " +
                 "evaluated against decoded feature properties (e.g. " +
                 "[\\\"match\\\",[\\\"get\\\",\\\"CONTINENT\\\"],\\\"Asia\\\",[\\\"rgba\\\",200,50,50,1],[\\\"rgba\\\",128,128,128,1]]). " +
                 "The baked vertex COLOR is multiplied by _MapColor in-shader (composite data-driven × zoom). " +
                 "When empty, vertex colors default to white and _MapColor drives the fill uniformly (S11).")]
        public string FillColorExpression;

        [Tooltip("S12: Zoom level used when evaluating FillColorExpression (for zoom-dependent stops). " +
                 "Only relevant when FillColorExpression contains zoom-dependent sub-expressions.")]
        public double StyleZoom = 0.0;

        /// <summary>
        /// Called by Unity when the component is first added in the Editor or Reset is selected.
        /// Auto-populates FillMaterial with the committed MapFill.mat template so the template
        /// is the live source of styling by default (without requiring manual inspector drag).
        /// </summary>
        private void Reset()
        {
#if UNITY_EDITOR
            if (FillMaterial == null)
                FillMaterial = AssetDatabase.LoadAssetAtPath<Material>(TemplateMaterialPath);
#endif
        }

        [Tooltip("Center + scale the built map to ViewSize units at the origin for easy viewing. " +
                 "The raw mesh is the z0 world tile in metres (~40,000,000 units) and lies flat in XZ.")]
        public bool FitToView = true;

        [Tooltip("Target size (world units) of the map's largest horizontal dimension when FitToView is on.")]
        public float ViewSize = 100f;

        private void Start() => Build();

        /// <summary>Decode the tile and (re)build the fill mesh. Safe to call from the Editor.</summary>
        [ContextMenu("Build Now")]
        public void Build()
        {
            byte[] bytes = LoadTileBytes();
            if (bytes == null) return;
            Mesh mesh = BuildMesh(bytes);
            if (mesh != null && FitToView) FitTransformToView(mesh);
        }

        private byte[] LoadTileBytes()
        {
            if (Tile != null) return Tile.bytes;
            // Fallback: load from file path (Editor-only).
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            if (File.Exists(path)) return File.ReadAllBytes(path);
            Debug.LogError("[MapFillBootstrap] No Tile asset assigned and fixture not found at: " + path);
            return null;
        }

        private Mesh BuildMesh(byte[] mvtBytes)
        {
            // 1. Decode MVT.
            MvtTile tile = MvtDecoder.Decode(mvtBytes);

            // S13: Use SourceLayerResolver seam to resolve the MVT layer from the style layer name.
            // Construct a minimal StyleLayer from the LayerName field (the architectural seam requires
            // a StyleLayer, not a bare string — consistent with the full style pipeline).
            var styleLayer = new StyleLayer { SourceLayer = LayerName };
            MvtLayer layer = SourceLayerResolver.ResolveMvtLayer(styleLayer, tile);
            if (layer == null)
            {
                Debug.LogError($"[MapFillBootstrap] Layer '{LayerName}' not found.");
                return null;
            }

            double extent = layer.Extent;

            // 2. Tile origin = min-corner Mercator of TileId(0,0,0).
            var tileId = new TileId(0, 0, 0);
            var (bMin, _) = tileId.MercatorBounds();
            double originX = bMin.x;
            double originY = bMin.y;

            // S12: Parse the data-driven fill-color expression once (if supplied).
            // Evaluation runs in the managed path (here) — never inside the Burst MvtDecodeJob.
            DataDrivenPaintEvaluator colorEvaluator = null;
            bool useDataDrivenColor = !string.IsNullOrEmpty(FillColorExpression);
            if (useDataDrivenColor)
            {
                try
                {
                    colorEvaluator = new DataDrivenPaintEvaluator(FillColorExpression);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning(
                        $"[MapFillBootstrap] FillColorExpression parse failed: {ex.Message}. " +
                        "Falling back to white vertex color (S11 uniform mode).");
                    useDataDrivenColor = false;
                }
            }

            var meshBuilder = new MeshBuilder();

            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon)
                    continue;

                // S12: Bake per-feature vertex color once, before the polygon/sub-polygon loop.
                // All sub-polygons of the same feature get the same baked color.
                //
                // Color space (D2): Core Color is sRGB [0,1]. We pass channels directly to
                // UnityEngine.Color here (sRGB float channels). MeshBuilder.Build() then calls
                // Color.linear on each before Mesh.SetColors, converting to linear space. This
                // matches material.SetColor which linearizes sRGB→linear at the material boundary
                // (in Unity's Linear color space project). Without the linearization step in
                // MeshBuilder, the vertex color stream would be in sRGB while _MapColor is in
                // linear — the shader multiply would combine mismatched spaces.
                UnityEngine.Color featureColor = UnityEngine.Color.white;
                if (useDataDrivenColor && colorEvaluator != null)
                {
                    var adapter = new MvtFeatureAdapter(feature);
                    if (colorEvaluator.TryEvaluateColor(StyleZoom, adapter, out MapRenderer.Core.Expressions.Color c))
                        featureColor = new UnityEngine.Color((float)c.R, (float)c.G, (float)c.B, (float)c.A);
                    // On failure: featureColor stays white (neutral — _MapColor drives color uniformly).
                }

                // 3. Decode rings.
                List<List<double2>> rings = MvtGeometry.Decode(feature.Geometry);
                if (rings == null || rings.Count == 0) continue;

                // 4. Assemble polygons (outer + holes).
                List<Polygon> polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    // 5. Earcut in tile space. Result contains both the flat vertex array and indices.
                    Earcut.Result earcutResult = Earcut.Triangulate(polygon.Outer, polygon.Holes);
                    if (earcutResult.Indices == null || earcutResult.Indices.Length == 0) continue;

                    double2[] flatVerts = earcutResult.Vertices;
                    int[] triIndices    = earcutResult.Indices;
                    int vertCount       = flatVerts.Length;

                    // 6. Project via Burst job: tile-space double2 → origin-relative float3.
                    var tileCoords = new NativeArray<double2>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var worldPos   = new NativeArray<float3>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    for (int i = 0; i < vertCount; i++)
                        tileCoords[i] = flatVerts[i];

                    var job = new ProjectTileVerticesJob
                    {
                        TileZ = 0, TileX = 0, TileY = 0,
                        Extent        = extent,
                        OriginMercX   = originX,
                        OriginMercY   = originY,
                        TileCoords    = tileCoords,
                        WorldPositions = worldPos
                    };
                    job.Schedule(vertCount, 64).Complete();

                    // 7. Copy to float3[] for MeshBuilder.
                    var verts = new float3[vertCount];
                    for (int i = 0; i < vertCount; i++)
                        verts[i] = worldPos[i];

                    tileCoords.Dispose();
                    worldPos.Dispose();

                    // 8. Add to mesh builder (S34: UVs + extent; S12: per-feature baked color).
                    meshBuilder.AddFeature(verts, triIndices, flatVerts, extent, featureColor);
                }
            }

            // 9. Build Unity Mesh.
            Mesh mesh = meshBuilder.Build();
            if (mesh == null)
            {
                Debug.LogError("[MapFillBootstrap] No geometry was built.");
                return null;
            }

            GetComponent<MeshFilter>().sharedMesh = mesh;

            var mr = GetComponent<MeshRenderer>();
            Material activeMat;
            if (FillMaterial != null)
            {
                // Instantiate a copy so the committed MapFill.mat template is never mutated by
                // runtime SetColor / EnableKeyword calls (SRP Batcher: use Material instances,
                // never MaterialPropertyBlock on batched renderers — see docs/lit-rendering-design.md).
                activeMat = new Material(FillMaterial) { name = FillMaterial.name };
                mr.sharedMaterial = activeMat;
            }
            else
            {
                // Create a default lit fill material from the MapRenderer/Fill shader.
                // Fallback to Sprites/Default if the shader is not yet compiled (e.g. first import).
                var fillShader = Shader.Find("MapRenderer/Fill");
                if (fillShader != null)
                {
                    activeMat = new Material(fillShader) { name = "MapFill_DefaultLit" };
                    activeMat.SetColor("_MapColor",   new Color(0.4f, 0.7f, 0.4f, 1f));
                    activeMat.SetFloat("_Opacity",    1f);
                    activeMat.SetFloat("_Metallic",   0f);
                    activeMat.SetFloat("_Smoothness", 0.3f);
                }
                else
                {
                    // Shader not yet compiled (first import or missing). Use Sprites/Default as a
                    // transient fallback so the mesh is at least visible. A reimport will fix this.
                    Debug.LogWarning("[MapFillBootstrap] MapRenderer/Fill shader not found — " +
                                     "falling back to Sprites/Default. Reimport Assets to fix.");
                    activeMat = new Material(Shader.Find("Sprites/Default"));
                    activeMat.color = new Color(0.4f, 0.7f, 0.4f, 1f);
                }
                mr.sharedMaterial = activeMat;
            }

            // S13: Bind new fill paint uniforms (constant/zoom path) via FillPaint + ZoomStyleApplier.
            // A default StyleLayer with no Paint object produces inert-fallback defaults (spec defaults).
            // This wires _FillOutlineColor, _FillAntialias, _FillTranslate, _FillTranslateAnchor,
            // _FillPattern to the material. When a full StyleDocument is available (future stage), the
            // StyleLayer.Paint will be populated and override these defaults.
            // Skip for the Sprites/Default fallback (it doesn't have the fill uniforms).
            if (activeMat != null && activeMat.shader != null &&
                activeMat.shader.name != "Sprites/Default")
            {
                try
                {
                    var fillStyleLayer = new StyleLayer { SourceLayer = LayerName };
                    var fillPaint      = new FillPaint(fillStyleLayer);
                    var applier        = new ZoomStyleApplier(activeMat);

                    // Bind constant/zoom properties (only Constant/Zoom kinds go through ZoomStyleApplier).
                    // fill-opacity: constant or zoom-dependent → _Opacity uniform.
                    if (fillPaint.Opacity != null)
                        applier.BindFloat(fillPaint.Opacity, "_Opacity");

                    // fill-outline-color → _FillOutlineColor.
                    if (fillPaint.OutlineColor != null)
                        applier.BindColor(fillPaint.OutlineColor, "_FillOutlineColor");

                    // fill-antialias → _FillAntialias.
                    if (fillPaint.Antialias != null)
                        applier.BindFloat(fillPaint.Antialias, "_FillAntialias");

                    // fill-translate: two scalar components → packed into _FillTranslate.xy.
                    // We bind each component and set the float4 directly for the initial constant case.
                    float tx = (float)fillPaint.TranslateX.EvaluateNumber(StyleZoom);
                    float ty = (float)fillPaint.TranslateY.EvaluateNumber(StyleZoom);
                    activeMat.SetVector("_FillTranslate", new Vector4(tx, ty, 0f, 0f));

                    // fill-translate-anchor → _FillTranslateAnchor.
                    if (fillPaint.TranslateAnchor != null)
                        applier.BindFloat(fillPaint.TranslateAnchor, "_FillTranslateAnchor");

                    // Apply all constant/zoom bindings at current StyleZoom.
                    applier.ApplyZoom(StyleZoom);
                }
                catch (System.Exception ex)
                {
                    // Non-fatal: FillPaint defaults are spec-compliant; uniform binding failure
                    // just means the shader falls back to its own Property defaults.
                    Debug.LogWarning(
                        $"[MapFillBootstrap] FillPaint uniform binding failed: {ex.Message}");
                }
            }

            Debug.Log($"[MapFillBootstrap] Built mesh: {meshBuilder.VertexCount} verts, {meshBuilder.IndexCount / 3} triangles.");
            return mesh;
        }

        /// <summary>
        /// Recenter + uniformly scale this object so the built mesh's horizontal (XZ) extent is
        /// ViewSize units, centered at the world origin — so any default top-down camera frames it
        /// and float precision is good (the raw mesh spans ~40,000,000 metres).
        /// </summary>
        private void FitTransformToView(Mesh mesh)
        {
            Bounds b = mesh.bounds;
            float maxDim = Mathf.Max(b.size.x, b.size.z, 1e-6f);
            float scale = ViewSize / maxDim;
            transform.localScale = Vector3.one * scale;
            transform.localPosition = -b.center * scale;
        }
    }
}
