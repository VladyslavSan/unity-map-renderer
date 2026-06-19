using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Coordinates;
using MapRenderer.Jobs;

namespace MapRenderer.Unity
{
    /// <summary>
    /// MonoBehaviour bootstrap: decodes the sample tile's country fills and renders them as a single
    /// Unity Mesh using an unlit double-sided material.
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
    /// Winding: Cull Off material avoids front/back visibility issues for this spike.
    /// Correct winding is a deliberate follow-up.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class MapFillBootstrap : MonoBehaviour
    {
        [Tooltip("Drag Assets/Fixtures/sample-tile.bytes here. If empty, it is loaded from disk (Editor).")]
        public TextAsset Tile;

        [Tooltip("Layer name to render. Default: countries.")]
        public string LayerName = "countries";

        [Tooltip("URP-compatible unlit material (Cull Off). If null, Sprites/Default is used.")]
        public Material FillMaterial;

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
            MvtLayer layer = tile.GetLayer(LayerName);
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

            var meshBuilder = new MeshBuilder();

            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != MvtGeometryType.Polygon)
                    continue;

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

                    // 8. Add to mesh builder.
                    meshBuilder.AddFeature(verts, triIndices);
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
            if (FillMaterial != null)
            {
                mr.sharedMaterial = FillMaterial;
            }
            else
            {
                // Sprites/Default is URP-safe, unlit, Cull Off — recommended spike material.
                var mat = new Material(Shader.Find("Sprites/Default"));
                mat.color = new Color(0.4f, 0.7f, 0.4f, 1f);
                mr.sharedMaterial = mat;
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
