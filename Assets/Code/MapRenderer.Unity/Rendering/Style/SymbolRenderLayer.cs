using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Text.Placement;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Symbol <see cref="IRenderLayer"/>: a MapLibre <c>symbol</c> layer as a runtime render object (the
    /// render-layer model). Axes: <see cref="RenderLayerBuild.FramePlaced"/> —
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

        /// <summary>I5b: the owned <c>SymbolIcon</c> clone (no halo — icons have none); <c>null</c> iff
        /// <c>MapMaterialSet.SymbolIcon</c> is unassigned (warned once) — icons just stay hidden, unlike
        /// <see cref="Material"/> this is NOT enforced by <c>MapMaterialSet.Validate()</c> (optional-with-warn,
        /// §I5b plan).</summary>
        public Material IconMaterial { get; }

        /// <summary>The typed parsed symbol layer — MapView's D10 source-fetch derivation reads this
        /// without re-walking <c>style.Layers</c>.</summary>
        public SymbolStyle.StyleLayer SymbolLayer { get; }

        private readonly LabelSlotPresenter _presenter;
        private readonly LabelSlotPresenter _iconPresenter;

        private SymbolRenderLayer(SymbolStyle.StyleLayer layer, Material material, Material iconMaterial,
            int drawIndex, Transform parent)
        {
            StyleLayer     = layer;
            SymbolLayer    = layer;
            Material       = material;
            IconMaterial   = iconMaterial;
            DrawIndex      = drawIndex;
            _presenter     = new LabelSlotPresenter(layer.Id, parent); // Hierarchy name = the style layer id
            _iconPresenter = new LabelSlotPresenter(layer.Id + "_Icon", parent);
        }

        /// <summary>Never returns null (unlike Fill/Line's <c>TryCreate</c>): the slot↔subsystem-ordinal 1:1
        /// mapping (§3.5) and the source-fetch derivation both require every Source-bearing symbol layer to
        /// take its slot even when <c>MapMaterialSet.SymbolText</c> is unassigned — in that case
        /// <see cref="Material"/> stays <c>null</c> (warn once), <see cref="RenderLayerSet.Build"/> skips the
        /// queue write, and <see cref="Present"/> never shows. <see cref="IconMaterial"/> is resolved the
        /// same way from <c>MapMaterialSet.SymbolIcon</c> (I5b) — independently optional, no halo bind.</summary>
        public static SymbolRenderLayer Create(
            SymbolStyle.StyleLayer layer, MapMaterialSet settings, double initialZoom, int drawIndex,
            Transform parent = null)
        {
            Material baseMat = settings != null ? settings.SymbolText : null;
            Material m = null;
            if (baseMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolText unassigned — labels will not render.");
            else
            {
                m = baseMat.CloneWithParent();
                m.name = $"MapSymbolText_{layer.Id}";
                BindHalo(m, layer.Paint, initialZoom);
            }

            Material baseIconMat = settings != null ? settings.SymbolIcon : null;
            Material iconMat = null;
            if (baseIconMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolIcon unassigned — icons will not render.");
            else
            {
                iconMat = baseIconMat.CloneWithParent();
                iconMat.name = $"MapSymbolIcon_{layer.Id}";
            }

            return new SymbolRenderLayer(layer, m, iconMat, drawIndex, parent);
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

        /// <summary>I5b: the icon analogue of <see cref="Present"/> — binds this slot's ICON mesh + this
        /// layer's <see cref="IconMaterial"/> to a SEPARATE persistent renderer (icons and text draw as two
        /// meshes/materials per slot, not one). Syncs <see cref="IconMaterial"/>'s <c>renderQueue</c> to
        /// <see cref="Material"/>'s every call (compare-assign, so a steady frame costs nothing extra) —
        /// <see cref="RenderLayerSet.Build"/> only writes the TEXT material's queue (it doesn't know about
        /// <see cref="IconMaterial"/>), so icons must inherit their layer's painter-order position here
        /// instead of getting their own (which would desync a fill declared between icon and text queues).
        /// <paramref name="visible"/> false hides unconditionally, mirroring <see cref="Present"/>.</summary>
        public void PresentIcon(Mesh mesh, bool visible)
        {
            if (IconMaterial != null && Material != null && IconMaterial.renderQueue != Material.renderQueue)
                IconMaterial.renderQueue = Material.renderQueue;
            _iconPresenter.Present(mesh, IconMaterial, visible);
        }

        /// <summary>Whether this layer's presenter is currently drawing. Test surface — see
        /// <c>LabelSlotPresenter.Enabled</c>.</summary>
        internal bool PresenterVisible => _presenter.Enabled;

        /// <summary>Whether this layer's ICON presenter is currently drawing. Test surface — mirrors
        /// <see cref="PresenterVisible"/> for the icon draw path.</summary>
        internal bool IconPresenterVisible => _iconPresenter.Enabled;

        public void ApplyZoom(double zoom) { } // no-op — zoom-expression halo is a documented follow-up (§7 risk 9)

        public void Dispose()
        {
            _presenter.Dispose();
            _iconPresenter.Dispose();
            if (Material != null) RenderLayerSet.DestroyMaterialInstance(Material);
            if (IconMaterial != null) RenderLayerSet.DestroyMaterialInstance(IconMaterial);
        }
    }
}
