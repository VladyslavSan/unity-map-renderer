using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Unity
{
    /// <summary>
    /// MonoBehaviour bootstrap: builds golden polyline shapes in world-meter space and renders
    /// them using <see cref="LineTessellator"/> → <see cref="LineMeshBuilder"/> → Line shader.
    ///
    /// Golden shapes are authored directly in world meters (not tile space), so there is no
    /// Mercator projection step and the camera can be framed trivially. This makes the width
    /// measurement in snapshot tests exact: <c>widthPx = widthMeters / metersPerPixel</c>.
    ///
    /// Camera setup for golden shapes: ortho camera at (0, 200, 0) looking down (−Y), with
    /// <c>orthographicSize</c> set to frame the shapes. <c>metersPerPixel = 2·orthoSize / pixelH</c>.
    ///
    /// No FitToView: the mesh is already in a known meter coordinate range; FitToView (from
    /// MapFillBootstrap) would apply a ~2.5e-6 world-space scale that corrupts the width check.
    ///
    /// The "no-rebuild" acceptance check: change <see cref="Width"/> on the material (no mesh
    /// rebuild, no <see cref="Build"/> call) and observe the rendered width changes proportionally.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class LineBootstrap : MonoBehaviour
    {
        [Header("Line Style (material uniforms — no mesh rebuild to change)")]
        [Tooltip("Line half-width in meters (canonical unit; WidthIsPixels=false) or pixels.")]
        public float Width = 4f;

        [Tooltip("If true, Width is in screen pixels and MetersPerPixel converts it to meters.")]
        public bool WidthIsPixels = false;

        [Tooltip("Meters per pixel: for ortho camera = 2·orthographicSize / pixelHeight. Set from LineSnapshotTests.")]
        public float MetersPerPixel = 1f;

        [Tooltip("Line colour.")]
        public Color LineColor = new Color(0.2f, 0.6f, 1f, 1f);

        [Tooltip("Opacity (0–1).")]
        [Range(0, 1)]
        public float Opacity = 1f;

        [Tooltip("AA feather multiplier (1 = ~1px, higher = softer).")]
        [Range(0, 4)]
        public float Blur = 1f;

        [Header("Tessellation")]
        public JoinType Join     = JoinType.Miter;
        public CapType  Cap      = CapType.Butt;
        public double   MiterLimit   = 2.0;
        public int      RoundSegments = 4;

        // Cached material reference so Width/color can be changed without rebuilding the mesh.
        private Material _material;

        private void Start() => Build();

        /// <summary>
        /// Build golden test polylines and generate the mesh. Exposed as [ContextMenu] so it
        /// can be called from the Unity Editor inspector without entering play mode.
        ///
        /// Shapes are authored in world meters so the ortho camera frames them at a known scale.
        /// </summary>
        [ContextMenu("Build Now")]
        public void Build()
        {
            List<double2> GoldenHLine()
            {
                // Horizontal segment centred at world origin, 80 m long.
                return new List<double2>
                {
                    new double2(-40, 0),
                    new double2( 40, 0),
                };
            }

            List<double2> GoldenLShape()
            {
                // L-shaped polyline: right-angle turn at origin.
                return new List<double2>
                {
                    new double2(-30, 30),
                    new double2(-30, -30),
                    new double2( 30, -30),
                };
            }

            List<double2> GoldenDiagonal()
            {
                // Diagonal from lower-left to upper-right.
                return new List<double2>
                {
                    new double2(-20, -20),
                    new double2( 20,  20),
                };
            }

            var lines = new List<IReadOnlyList<double2>>
            {
                GoldenHLine(),
                GoldenLShape(),
                GoldenDiagonal(),
            };

            var builder = new LineMeshBuilder();
            foreach (var line in lines)
            {
                LineTessellator.Result result = LineTessellator.Triangulate(
                    line, Join, Cap, MiterLimit, RoundSegments);
                builder.AddLineResult(result);
            }

            Mesh mesh = builder.Build();
            if (mesh == null)
            {
                Debug.LogError("[LineBootstrap] No geometry was built.");
                return;
            }

            GetComponent<MeshFilter>().sharedMesh = mesh;

            // Create or reuse the material.
            _material = CreateLineMaterial();
            var mr = GetComponent<MeshRenderer>();
            mr.sharedMaterial = _material;

            ApplyMaterialUniforms();

            Debug.Log($"[LineBootstrap] Built: {builder.VertexCount} verts, {builder.IndexCount / 3} tris.");
        }

        /// <summary>
        /// Update the material uniforms without rebuilding the mesh.
        /// This is the "no-rebuild" demonstration: changing Width here does not touch the mesh.
        /// </summary>
        public void ApplyMaterialUniforms()
        {
            if (_material == null) return;
            _material.SetFloat("_Width",          Width);
            _material.SetFloat("_WidthIsPixels",  WidthIsPixels ? 1f : 0f);
            _material.SetFloat("_MetersPerPixel", MetersPerPixel);
            _material.SetColor("_Color",          LineColor);
            _material.SetFloat("_Opacity",        Opacity);
            _material.SetFloat("_Blur",           Blur);
        }

        private Material CreateLineMaterial()
        {
            // Try to find the committed Line.shader; fall back to a magenta fallback so the
            // absence of the shader is visible rather than silent.
            var shader = Shader.Find("MapRenderer/Line");
            if (shader == null)
            {
                Debug.LogWarning("[LineBootstrap] 'MapRenderer/Line' shader not found. " +
                                 "Falling back to Sprites/Default.");
                shader = Shader.Find("Sprites/Default");
            }

            var mat = new Material(shader) { name = "LineMaterial" };
            return mat;
        }
    }
}
