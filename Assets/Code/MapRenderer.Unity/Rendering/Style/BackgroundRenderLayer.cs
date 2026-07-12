using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Common;
using Background = MapRenderer.Core.Style.Background;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Background <see cref="IRenderLayer"/>: a style's <c>background</c> layer as a runtime render object
    /// (design <c>docs/render-layer-unification.md</c> §3.6). Axes: <see cref="RenderLayerBuild.ViewGeometry"/>
    /// — synthesized from the view alone, no tile data — / <see cref="DrawPersistence.Persistent"/> — E3
    /// flips §3.1's pre-E0 Immediate cell (E0's option (c) collapsed the orchestrator; a Persistent
    /// MeshRenderer is the proven-headless path, plan decision 3).
    ///
    /// <para>Owns: a material clone (a FILL-base clone at its global queue — the fill shader's flat lit
    /// path IS the ground look, <see cref="Materials.MaterialFactory.CreateBackgroundMaterial"/>), one
    /// world-cap ground quad <see cref="Mesh"/> (STATIC — built once, never rebuilt: the camera-relative
    /// render origin already tracks the look-at every frame, so a quad centred on the identity transform
    /// is always centred under the camera for free, plan decision 2), and a persistent
    /// <see cref="GameObject"/>/<see cref="MeshRenderer"/> that Unity redraws every camera render with no
    /// orchestrator (the <see cref="Text.Placement.LabelSlotPresenter"/> creation idiom, build-once instead
    /// of per-Tick).</para>
    ///
    /// <para>Coplanar y=0 with fills/lines BY DESIGN (plan decision 1): every flat layer is ZWrite-off
    /// (<see cref="Materials.BaseTweaker.ApplyBaseContract"/>), so painter order via <c>renderQueue</c>
    /// alone decides the composite — do NOT lift the quad by an epsilon. <see cref="SetVisible"/> is
    /// <see cref="Map.MapView"/>'s Mercator-only gate: a flat world quad is wrong on the sphere (it would
    /// slice through the globe), so a curved (globe) projection hides this layer entirely — the real
    /// projection-aware globe background is a documented follow-up (§7.6).</para>
    /// </summary>
    internal sealed class BackgroundRenderLayer : IRenderLayer
    {
        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.ViewGeometry;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
        public int                               DrawIndex   { get; }
        public Material                          Material    { get; } // owned fill-base clone; null iff FillMaterial unassigned (slot kept, never shows)

        private readonly ZoomStyleApplier _applier; // null iff Material null
        private Mesh         _mesh;                 // owned world-cap ground quad
        private GameObject   _go;                   // owned persistent renderer (DontSave — visible in Hierarchy)
        private MeshRenderer _renderer;
        private readonly Transform _parent;          // shared "Map Render Layers" root (null ⇒ scene root)
        private bool _projectionVisible = true;      // MapView's Mercator-only gate; default visible for sets built outside MapView (e.g. tests)

        private BackgroundRenderLayer(
            MapRenderer.Core.Style.StyleLayer layer, Material material, ZoomStyleApplier applier, int drawIndex,
            Transform parent)
        {
            StyleLayer = layer;
            Material   = material;
            _applier   = applier;
            DrawIndex  = drawIndex;
            _parent    = parent;
        }

        /// <summary>Never returns null (the Symbol Create pattern, <see cref="SymbolRenderLayer.Create"/>):
        /// background always takes its declared slot so the layers above it keep their queues regardless of
        /// material config; unconfigured ⇒ <see cref="Material"/> null (<see cref="Materials.MaterialFactory"/>
        /// warns), no geometry, never shows.</summary>
        public static BackgroundRenderLayer Create(
            Background.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex,
            Transform parent = null)
        {
            Material mat = Materials.MaterialFactory.CreateBackgroundMaterial(settings);
            if (mat == null)
                return new BackgroundRenderLayer(layer, null, null, drawIndex, parent);

            Background.PaintProperties paint = layer.Paint;
            var applier = new ZoomStyleApplier(mat);
            Materials.MaterialFactory.BindBackgroundPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(initialZoom);

            var result = new BackgroundRenderLayer(layer, mat, applier, drawIndex, parent);
            result.BuildQuadAndPresenter(layer.Id);
            return result;
        }

        /// <summary>Builds the world-cap ground quad and its persistent renderer. Attribute set copied
        /// from <c>LayerOrderSnapshotTests.BuildFillQuad</c> (normals/tangents/uv/white-vertex-colours are
        /// all load-bearing — a positions-only quad renders NOTHING with the fill shader in headless
        /// EditMode) with ONE correction: the committed <c>MapFill.mat</c> sets <c>_Cull=1</c> (Front) —
        /// production fill meshes (earcut-derived) come out wound so that convention keeps them; a
        /// hand-built quad wound the "naive" way (the order <c>BuildFillQuad</c> uses) is front-facing
        /// under Unity's standard convention and gets exactly the triangles <c>_Cull=1</c> discards —
        /// invisible regardless of scale or camera. Triangles below are wound the OPPOSITE way so the fill
        /// material's real cull state renders them. Half-extent <c>2 · WebMercator.WorldExtent</c> (the
        /// world HALF-width): from any in-world look-at, the farthest world point is at most one full world
        /// width away per axis, so the quad covers the whole Mercator square plus overhang, matching
        /// MapLibre's "paints the whole canvas region" background. Float32 verts at this scale have ~4 m
        /// precision — irrelevant for a flat solid colour.</summary>
        private void BuildQuadAndPresenter(string layerId)
        {
            float half = (float)(2.0 * WebMercator.WorldExtent);

            _mesh = new Mesh { name = "MapBackgroundQuad" };
            _mesh.vertices = new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3( half, 0f, -half),
                new Vector3( half, 0f,  half),
                new Vector3(-half, 0f,  half),
            };
            _mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            _mesh.tangents = new[]
            {
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
            };
            _mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            _mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; // reversed vs BuildFillQuad — see _Cull note above
            _mesh.RecalculateBounds();

            // GO creation mirrors LabelSlotPresenter.Present's block: a runtime artifact never serialised
            // into a scene/build (DontSave) but VISIBLE in the Hierarchy, parented under the shared
            // "Map Render Layers" root — built ONCE here (the quad is static), not lazily/per-Tick like a
            // label slot. Local identity under the identity root ⇒ world identity (the fill shader's
            // object-to-world assumption).
            _go = new GameObject(layerId) { hideFlags = HideFlags.DontSave }; // Hierarchy name = the style layer id
            _go.transform.SetParent(_parent, false);
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows    = false;
            _renderer.sharedMaterial    = Material;
            _renderer.enabled           = _projectionVisible;
        }

        /// <summary>MapView's Mercator-only gate (plan decision 4): hides the flat world quad under a
        /// self-occluding (globe) projection. Once per restyle; the projection is a session constant.
        /// Default visible — a set built outside MapView (e.g. tests) shows without any gate call.</summary>
        public void SetVisible(bool visible)
        {
            _projectionVisible = visible;
            if (_renderer != null) _renderer.enabled = visible && Material != null;
        }

        public void ApplyZoom(double zoom) => _applier?.ApplyZoom(zoom); // zoom-expression background-color/opacity

        /// <summary>GameObject first, then mesh, then material — a MeshRenderer whose sharedMesh died
        /// renders pink in edit mode (E2 risk 3), so destroy the renderer before what it references.</summary>
        public void Dispose()
        {
            _go.DestroySafely();
            _go = null;
            _renderer = null;
            _mesh.DestroySafely(allowDestroyingAssets: true);
            _mesh = null;
            Material.DestroySafely();
        }
    }
}
