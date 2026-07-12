using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Text.Placement;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Symbol <see cref="IRenderLayer"/>: a MapLibre <c>symbol</c> layer as a runtime render object (design
    /// <c>docs/render-layer-unification.md</c> §3.5). Axes: <see cref="RenderLayerBuild.FramePlaced"/> —
    /// rebuilt every frame from screen-space label placement, NOT the Burst tile-mesh pipeline — /
    /// <see cref="DrawPersistence.Persistent"/> — per E0 option (c), a persistent per-slot
    /// <see cref="LabelSlotPresenter"/> owned by this layer, rewritten in place and redrawn by Unity every
    /// camera render with no orchestrator.
    ///
    /// <para>Owns (D11, migrated here from <c>SymbolLabelSubsystem</c>): the per-layer <see cref="Material"/>
    /// clone (its <c>renderQueue</c> is written by <see cref="RenderLayerSet.Build"/> like every other
    /// layer, replacing the shader's Overlay-4000 default), the halo bind, and the presenter. Collision stays
    /// global (D8) — only the DRAW is per-layer, via <see cref="Present"/>, called once per <c>Tick</c> from
    /// <see cref="Placement.LabelPlacementSystem"/> with this layer's slot mesh.</para>
    /// </summary>
    internal sealed class SymbolRenderLayer : IRenderLayer
    {
        private static readonly int HaloColorId = Shader.PropertyToID("_HaloColor");
        private static readonly int HaloWidthId = Shader.PropertyToID("_HaloWidthPx");
        private static readonly int HaloBlurId  = Shader.PropertyToID("_HaloBlurPx");

        public MapRenderer.Core.Style.StyleLayer StyleLayer   { get; }
        public RenderLayerBuild                  Build        => RenderLayerBuild.FramePlaced;
        public DrawPersistence                   Persistence  => DrawPersistence.Persistent;
        public int                               DrawIndex    { get; }

        /// <summary>The owned <c>SymbolText</c> clone, halo bound (D11); <c>null</c> iff
        /// <c>MapMaterialSet.SymbolText</c> is unassigned — the layer still takes its slot (the slot↔
        /// subsystem-ordinal 1:1 mapping requires it, §3.5), it just never presents.</summary>
        public Material Material { get; }

        /// <summary>The typed parsed symbol layer — MapView's D10 source-fetch derivation reads this
        /// without re-walking <c>style.Layers</c>.</summary>
        public SymbolStyle.StyleLayer SymbolLayer { get; }

        private readonly LabelSlotPresenter _presenter;

        private SymbolRenderLayer(SymbolStyle.StyleLayer layer, Material material, int drawIndex, Transform parent)
        {
            StyleLayer  = layer;
            SymbolLayer = layer;
            Material    = material;
            DrawIndex   = drawIndex;
            _presenter  = new LabelSlotPresenter(layer.Id, parent); // Hierarchy name = the style layer id
        }

        /// <summary>Never returns null (unlike Fill/Line's <c>TryCreate</c>): the slot↔subsystem-ordinal 1:1
        /// mapping (§3.5) and the source-fetch derivation both require every Source-bearing symbol layer to
        /// take its slot even when <c>MapMaterialSet.SymbolText</c> is unassigned — in that case
        /// <see cref="Material"/> stays <c>null</c> (warn once), <see cref="RenderLayerSet.Build"/> skips the
        /// queue write, and <see cref="Present"/> never shows.</summary>
        public static SymbolRenderLayer Create(
            SymbolStyle.StyleLayer layer, MapMaterialSet settings, double initialZoom, int drawIndex,
            Transform parent = null)
        {
            Material baseMat = settings != null ? settings.SymbolText : null;
            if (baseMat == null)
            {
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolText unassigned — labels will not render.");
                return new SymbolRenderLayer(layer, null, drawIndex, parent);
            }

            Material m = baseMat.CloneWithParent();
            m.name = $"MapSymbolText_{layer.Id}";
            BindHalo(m, layer.Paint, initialZoom);
            return new SymbolRenderLayer(layer, m, drawIndex, parent);
        }

        private static void BindHalo(Material material, SymbolStyle.PaintProperties paint, double zoom)
        {
            // Constant/zoom halo only (the locked first-cut scope). A data-driven (Feature/Composite) halo
            // would throw from Evaluate(zoom) — leave the material's inherited base halo rather than fault
            // the whole style load (data-driven halo is a documented follow-up, design §7 risk 9).
            try
            {
                var haloColor = paint.HaloColor.Evaluate(zoom); // MapRenderer.Core.Expressions.Color (sRGB)
                // sRGB→linear (project is Linear color space; the shader consumes _HaloColor directly, and
                // SetColor uploads raw floats with no gamma conversion) — mirrors the vertex text-color bake
                // in LabelPlacementSystem and StyledFill/LineTileBuilder's Color.linear convention.
                material.SetColor(HaloColorId,
                    new Color((float)haloColor.R, (float)haloColor.G, (float)haloColor.B, (float)haloColor.A).linear);
                material.SetFloat(HaloWidthId, paint.HaloWidth.Evaluate(zoom));
                material.SetFloat(HaloBlurId, paint.HaloBlur.Evaluate(zoom));
            }
            catch (System.ArgumentException)
            {
                // data-driven halo not supported yet — keep the base material's halo.
            }
        }

        /// <summary>Per-Tick draw handoff from <see cref="Placement.LabelPlacementSystem"/>: bind this
        /// slot's mesh + this layer's material to the persistent renderer, or hide it. Main thread
        /// (LateUpdate). <paramref name="visible"/> false hides unconditionally — a frame that built
        /// nothing must not leave last frame's labels frozen on screen (the mirror image of the blink).</summary>
        public void Present(Mesh mesh, bool visible) => _presenter.Present(mesh, Material, visible);

        /// <summary>Whether this layer's presenter is currently drawing. Test surface — see
        /// <c>LabelSlotPresenter.Enabled</c>.</summary>
        internal bool PresenterVisible => _presenter.Enabled;

        public void ApplyZoom(double zoom) { } // no-op — zoom-expression halo is a documented follow-up (§7 risk 9)

        public void Dispose()
        {
            _presenter.Dispose();
            if (Material != null) RenderLayerSet.DestroyMaterialInstance(Material);
        }
    }
}
