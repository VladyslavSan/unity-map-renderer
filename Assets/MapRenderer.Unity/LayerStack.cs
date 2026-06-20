using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Rendering;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S07 — synthetic multi-layer painter's-algorithm harness.
    ///
    /// Builds N programmatically-constructed, overlapping, coplanar map layers (a mix of fill quads
    /// and line ribbons) on the flat XZ plane and composites them in <b>declared order</b> using the
    /// interim integer-queue mechanism: each layer gets its own Material instance with
    /// <c>renderQueue = base + index</c> (from <see cref="LayerDrawOrder"/>) and <c>_ZWrite = 0</c>.
    /// This stands up the ORDERING MECHANISM with hand-built layers — there is no parsed style yet
    /// (that is S08). The BatchRendererGroup / ScriptableRenderPass target is a follow-up.
    ///
    /// Why this owns draw order (not Unity's automatic sort): all layers live in ONE transparent
    /// queue band (≥2501) with ZWrite off, so Unity's depth/camera-distance tiebreaks are removed.
    /// The only remaining sort is camera distance WITHIN a single queue value — which on a coplanar
    /// plane can reorder disjoint tiles within ONE layer (irrelevant per S07) but never reorders
    /// across layers, because every declared layer has a DISTINCT queue. See <see cref="LayerDrawOrder"/>.
    ///
    /// Each layer is rendered by its own child GameObject (MeshFilter + MeshRenderer). The whole stack
    /// is centred at the world origin on the XZ plane for a top-down ortho camera.
    ///
    /// Per docs/lit-rendering-design.md: NEVER MaterialPropertyBlock — per-layer Material instances.
    /// renderQueue is set at RUNTIME on the material instance (not on a committed .mat) to avoid URP's
    /// ValidateMaterial queue resolution clobbering it on import (the S37 history).
    /// </summary>
    public sealed class LayerStack : MonoBehaviour
    {
        /// <summary>Whether a layer is a solid fill quad or a line ribbon.</summary>
        public enum LayerKind { Fill, Line }

        /// <summary>
        /// Declarative description of one synthetic layer. Geometry is a quad/ribbon covering a
        /// rectangle on the XZ plane; appearance is a flat colour.
        /// </summary>
        public sealed class LayerSpec
        {
            public string    Name;
            public LayerKind Kind;
            public Color     Color;

            // Fill: axis-aligned rectangle [MinX,MaxX] × [MinZ,MaxZ] on the XZ plane.
            // Line: a horizontal ribbon centred on Z = LineZ spanning X ∈ [MinX,MaxX], with the given
            //       half-width (meters). Make this wide + opaque so it densely covers the sample region.
            public float MinX, MaxX, MinZ, MaxZ;
            public float LineZ;
            public float LineHalfWidth;

            public static LayerSpec Fill(string name, Color color, float minX, float maxX, float minZ, float maxZ)
                => new LayerSpec { Name = name, Kind = LayerKind.Fill, Color = color,
                                   MinX = minX, MaxX = maxX, MinZ = minZ, MaxZ = maxZ };

            public static LayerSpec Line(string name, Color color, float minX, float maxX, float lineZ, float halfWidth)
                => new LayerSpec { Name = name, Kind = LayerKind.Line, Color = color,
                                   MinX = minX, MaxX = maxX, LineZ = lineZ, LineHalfWidth = halfWidth };
        }

        /// <summary>
        /// Base render queue for the bottom-most layer. Defaults to Unity's Transparent queue (3000).
        /// Must stay in the transparent band so the painter's ordering is honoured.
        /// </summary>
        public int BaseQueue = LayerDrawOrder.TransparentQueue;

        // The specs in DECLARED order (index 0 = bottom, last = top), the live materials, and the
        // child renderers — index-aligned across all three.
        private readonly List<LayerSpec>     _specs     = new List<LayerSpec>();
        private readonly List<Material>      _materials = new List<Material>();
        private readonly List<MeshRenderer>  _renderers = new List<MeshRenderer>();

        /// <summary>Number of layers currently in the stack.</summary>
        public int LayerCount => _specs.Count;

        /// <summary>Read-only access to the live material instance for layer <paramref name="i"/>.</summary>
        public Material MaterialAt(int i) => _materials[i];

        /// <summary>
        /// Build the stack from the given specs (declared order). Tears down any prior build.
        /// After this, layer i is drawn at <c>renderQueue = BaseQueue + i</c> with <c>_ZWrite = 0</c>,
        /// so higher-index layers composite on top.
        /// </summary>
        public void Build(IReadOnlyList<LayerSpec> specs)
        {
            if (specs == null) throw new ArgumentNullException(nameof(specs));
            Clear();

            foreach (var spec in specs)
            {
                var child = new GameObject(spec.Name);
                child.transform.SetParent(transform, false);

                var mf = child.AddComponent<MeshFilter>();
                var mr = child.AddComponent<MeshRenderer>();

                Mesh mesh;
                Material mat;
                if (spec.Kind == LayerKind.Fill)
                {
                    mesh = BuildFillQuad(spec);
                    mat  = CreateFillMaterial(spec.Color);
                }
                else
                {
                    mesh = BuildLineRibbon(spec);
                    mat  = CreateLineMaterial(spec.Color, spec.LineHalfWidth);
                }

                mf.sharedMesh     = mesh;
                mr.sharedMaterial = mat;
                // No shadows — synthetic flat painter's layers.
                mr.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows       = false;

                _specs.Add(spec);
                _materials.Add(mat);
                _renderers.Add(mr);
            }

            // Assign queues in declared order: queue[i] = BaseQueue + i.
            ApplyDeclaredOrder();
        }

        /// <summary>
        /// Reorder the rendered output WITHOUT rebuilding geometry or changing colours.
        /// <paramref name="order"/> is a permutation of [0, LayerCount): <c>order[k]</c> is the spec
        /// index drawn at painter's-stack position k (position 0 = bottom, last = top). This changes
        /// ONLY each material's renderQueue — isolating "order" as the sole cause of any visual change,
        /// which is the S07 acceptance tooth.
        /// </summary>
        public void SetOrder(IReadOnlyList<int> order)
        {
            if (order == null) throw new ArgumentNullException(nameof(order));
            if (order.Count != _specs.Count)
                throw new ArgumentException(
                    $"order length ({order.Count}) must equal LayerCount ({_specs.Count}).", nameof(order));

            // Validate it is a permutation of 0..N-1.
            var seen = new bool[_specs.Count];
            foreach (int idx in order)
            {
                if (idx < 0 || idx >= _specs.Count)
                    throw new ArgumentOutOfRangeException(nameof(order),
                        $"order contains out-of-range index {idx} (valid 0..{_specs.Count - 1}).");
                if (seen[idx])
                    throw new ArgumentException($"order is not a permutation — index {idx} repeated.", nameof(order));
                seen[idx] = true;
            }

            int[] queues = LayerDrawOrder.ComputeQueues(_specs.Count, BaseQueue);
            for (int position = 0; position < order.Count; position++)
            {
                int specIndex = order[position];
                _materials[specIndex].renderQueue = queues[position];
            }
        }

        /// <summary>Assign queues so the declared order (spec index == stack position) is drawn.</summary>
        public void ApplyDeclaredOrder()
        {
            int[] queues = LayerDrawOrder.ComputeQueues(_specs.Count, BaseQueue);
            for (int i = 0; i < _specs.Count; i++)
                _materials[i].renderQueue = queues[i];
        }

        /// <summary>Destroy all child layer GameObjects and material instances.</summary>
        public void Clear()
        {
            foreach (var mat in _materials)
                if (mat != null) DestroyImmediateSafe(mat);
            _materials.Clear();
            _renderers.Clear();
            _specs.Clear();

            // Destroy child GameObjects created by a previous Build.
            var children = new List<GameObject>();
            for (int i = 0; i < transform.childCount; i++)
                children.Add(transform.GetChild(i).gameObject);
            foreach (var c in children)
                DestroyImmediateSafe(c);
        }

        private void OnDestroy() => Clear();

        // ─── Geometry ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a flat quad on the XZ plane spanning [MinX,MaxX] × [MinZ,MaxZ], normal +Y, UV0 in
        /// [0,1], tangent (1,0,0,1) — matches the Fill shader's UV/TANGENT contract (S34).
        /// </summary>
        private static Mesh BuildFillQuad(LayerSpec s)
        {
            var verts = new Vector3[]
            {
                new Vector3(s.MinX, 0f, s.MinZ),
                new Vector3(s.MaxX, 0f, s.MinZ),
                new Vector3(s.MaxX, 0f, s.MaxZ),
                new Vector3(s.MinX, 0f, s.MaxZ),
            };
            var normals  = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            var uvs      = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            var tangents = new[]
            {
                new Vector4(1,0,0,1), new Vector4(1,0,0,1), new Vector4(1,0,0,1), new Vector4(1,0,0,1),
            };
            // Two-sided rendering (Fill shader is Cull Off), so a single winding suffices.
            var tris = new[] { 0, 1, 2, 0, 2, 3 };

            var mesh = new Mesh { name = $"FillQuad_{s.Name}" };
            mesh.vertices  = verts;
            mesh.normals   = normals;
            mesh.uv        = uvs;
            mesh.tangents  = tangents;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Build a wide line ribbon along Z = LineZ spanning X ∈ [MinX,MaxX], using the real
        /// LineTessellator → LineMeshBuilder path so the layer renders through the MapRenderer/Line
        /// shader (forward-transparent). A wide half-width + opaque colour makes the ribbon densely
        /// cover the sample region (sample its clean interior, away from the fwidth feather).
        /// </summary>
        private static Mesh BuildLineRibbon(LayerSpec s)
        {
            var centerline = new List<double2>
            {
                new double2(s.MinX, s.LineZ),
                new double2(s.MaxX, s.LineZ),
            };
            LineTessellator.Result result = LineTessellator.Triangulate(
                centerline, JoinType.Miter, CapType.Butt, miterLimit: 2.0, roundSegments: 4);

            var builder = new LineMeshBuilder();
            builder.AddLineResult(result);
            return builder.Build();
        }

        // ─── Materials ────────────────────────────────────────────────────────────

        /// <summary>
        /// Create a per-layer Fill material instance. _ZWrite=0 (painter's; the shader now uses
        /// ZWrite [_ZWrite] on its forward pass). Opaque alpha (overpaint) — synthetic fills are solid.
        /// </summary>
        private static Material CreateFillMaterial(Color color)
        {
            var shader = Shader.Find("MapRenderer/Fill");
            if (shader == null)
            {
                Debug.LogWarning("[LayerStack] 'MapRenderer/Fill' shader not found — using Sprites/Default.");
                var fallback = new Material(Shader.Find("Sprites/Default")) { color = color };
                return fallback;
            }

            var mat = new Material(shader) { name = "LayerStackFill" };
            mat.SetColor("_MapColor",   color);
            mat.SetColor("_BaseColor",  Color.white); // neutral base; _MapColor drives albedo
            mat.SetFloat("_Opacity",    1f);
            mat.SetFloat("_Metallic",   0f);
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_ZWrite",     0f);           // painter's algorithm — no depth write
            return mat;
        }

        /// <summary>
        /// Create a per-layer Line material instance. The Line shader already hardcodes ZWrite Off /
        /// Queue=Transparent in ShaderLab; we still set _ZWrite=0 for consistency and a wide width.
        /// </summary>
        private static Material CreateLineMaterial(Color color, float halfWidth)
        {
            var shader = Shader.Find("MapRenderer/Line");
            if (shader == null)
            {
                Debug.LogWarning("[LayerStack] 'MapRenderer/Line' shader not found — using Sprites/Default.");
                var fallback = new Material(Shader.Find("Sprites/Default")) { color = color };
                return fallback;
            }

            var mat = new Material(shader) { name = "LayerStackLine" };
            mat.SetColor("_MapColor",       color);
            mat.SetColor("_BaseColor",      Color.white);
            mat.SetFloat("_Opacity",        1f);
            mat.SetFloat("_Width",          halfWidth);
            mat.SetFloat("_WidthIsPixels",  0f);
            mat.SetFloat("_MetersPerPixel", 1f);
            mat.SetFloat("_Blur",           1f);
            mat.SetFloat("_Metallic",       0f);
            mat.SetFloat("_Smoothness",     0f);
            mat.SetFloat("_ZWrite",         0f);
            return mat;
        }

        private static void DestroyImmediateSafe(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else                       UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
