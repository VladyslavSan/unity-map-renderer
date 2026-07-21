using UnityEngine;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Text.Placement;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Symbol <see cref="IRenderLayer"/>: a MapLibre <c>symbol</c> layer as a runtime render object (the
    /// render-layer model). Axes: <see cref="RenderLayerBuild.FramePlaced"/> —
    /// rebuilt every frame from per-label placement, NOT the Burst tile-mesh pipeline — /
    /// <see cref="DrawPersistence.Persistent"/> — per Epic A / A1, this layer's material is bound + presented
    /// by <see cref="Placement.WorldLabelRenderer"/> (the world-anchored draw path), redrawn by Unity every
    /// camera render with no orchestrator.
    ///
    /// <para>Owns (D11, migrated here from <c>SymbolLabelSubsystem</c>): the per-layer <see cref="WorldTextMaterial"/>/
    /// <see cref="WorldIconMaterial"/> clones and the halo bind. <see cref="Material"/> IS
    /// <see cref="WorldTextMaterial"/> — the old screen-space <c>SymbolText</c> clone (and its
    /// <c>IconMaterial</c> sibling) is retired; its <c>renderQueue</c> is written by
    /// <see cref="RenderLayerSet.Build"/> like every other layer (replacing the shader's Overlay-4000
    /// default) for free, since <c>Build</c> reads <see cref="IRenderLayer.Material"/>. The world icon's
    /// queue has no such free ride (nothing else reads a symbol layer's icon material at Build time), so
    /// <see cref="Create"/> writes it directly. Collision stays global (D8) — only the DRAW is per-layer,
    /// via the material <see cref="Placement.WorldLabelRenderer.EndFrame"/> resolves for this layer's
    /// slot.</para>
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

        /// <summary>The layer's primary drawn material — <see cref="WorldTextMaterial"/>. <c>null</c> iff
        /// <c>MapMaterialSet.SymbolTextWorld</c> is unassigned (the slot↔subsystem-ordinal 1:1 mapping still
        /// requires the layer to take its slot, §3.5; it just never presents). Its <c>renderQueue</c> is
        /// written by <see cref="RenderLayerSet.Build"/> like every other layer's <see cref="Material"/>.</summary>
        public Material Material => WorldTextMaterial;

        /// <summary>Epic A / A1 (design §11 A1 D5): the owned <c>Map/Symbol/TextWorld</c> clone of
        /// <c>MapMaterialSet.SymbolTextWorld</c>, halo bound — the world-anchored point-text draw path's
        /// per-layer material, and the layer's <see cref="Material"/>. <c>null</c> iff
        /// <c>MapMaterialSet.SymbolTextWorld</c> is unassigned (REQUIRED — enforced by
        /// <c>MapMaterialSet.Validate()</c>, so this is null only for a throwaway/test <c>MapMaterialSet</c>
        /// that skipped Validate).</summary>
        public Material WorldTextMaterial { get; }

        /// <summary>Epic A / A1 (design §11 A1 D5): the owned <c>Map/Symbol/IconWorld</c> clone of
        /// <c>MapMaterialSet.SymbolIconWorld</c> (no halo). <c>null</c> iff
        /// <c>MapMaterialSet.SymbolIconWorld</c> is unassigned (optional-with-warn) — world icons stay
        /// hidden, text is unaffected. Its <c>renderQueue</c> is written directly by <see cref="Create"/> —
        /// unlike <see cref="WorldTextMaterial"/>, nothing reads a symbol layer's icon material at
        /// <see cref="RenderLayerSet.Build"/> time, so it has no free ride.</summary>
        public Material WorldIconMaterial { get; }

        /// <summary>The typed parsed symbol layer — MapView's D10 source-fetch derivation reads this
        /// without re-walking <c>style.Layers</c>.</summary>
        public SymbolStyle.StyleLayer SymbolLayer { get; }

        private SymbolRenderLayer(SymbolStyle.StyleLayer layer,
            Material worldTextMaterial, Material worldIconMaterial, int drawIndex, Transform parent)
        {
            StyleLayer        = layer;
            SymbolLayer       = layer;
            WorldTextMaterial = worldTextMaterial;
            WorldIconMaterial = worldIconMaterial;
            DrawIndex         = drawIndex;
        }

        /// <summary>Never returns null (unlike Fill/Line's <c>TryCreate</c>): the slot↔subsystem-ordinal 1:1
        /// mapping (§3.5) and the source-fetch derivation both require every Source-bearing symbol layer to
        /// take its slot even when <c>MapMaterialSet.SymbolTextWorld</c> is unassigned — in that case
        /// <see cref="Material"/> stays <c>null</c> (warn once), <see cref="RenderLayerSet.Build"/> skips the
        /// queue write, and this layer's slot never presents. <see cref="WorldIconMaterial"/> is resolved the
        /// same way from <c>MapMaterialSet.SymbolIconWorld</c> — independently optional, no halo bind, its
        /// queue written here directly (§0.1) rather than by <c>Build</c>.</summary>
        public static SymbolRenderLayer Create(
            SymbolStyle.StyleLayer layer, MapMaterialSet settings, double initialZoom, int drawIndex,
            Transform parent = null)
        {
            Material baseWorldTextMat = settings != null ? settings.SymbolTextWorld : null;
            Material worldTextMat = null;
            if (baseWorldTextMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolTextWorld unassigned — labels will not render.");
            else
            {
                worldTextMat = baseWorldTextMat.CloneWithParent();
                worldTextMat.name = $"MapSymbolTextWorld_{layer.Id}";
                BindHalo(worldTextMat, layer.Paint, initialZoom);
            }

            Material baseWorldIconMat = settings != null ? settings.SymbolIconWorld : null;
            Material worldIconMat = null;
            if (baseWorldIconMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolIconWorld unassigned — world icons will not render.");
            else
            {
                worldIconMat = baseWorldIconMat.CloneWithParent();
                worldIconMat.name = $"MapSymbolIconWorld_{layer.Id}";
                worldIconMat.renderQueue = LayerDrawOrder.QueueFor(drawIndex); // §0.1: no Build-time free ride like WorldTextMaterial/Material
            }

            return new SymbolRenderLayer(layer, worldTextMat, worldIconMat, drawIndex, parent);
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

        public void ApplyZoom(double zoom) { } // no-op — zoom-expression halo is a documented follow-up (§7 risk 9)

        public void Dispose()
        {
            if (WorldTextMaterial != null) RenderLayerSet.DestroyMaterialInstance(WorldTextMaterial); // also destroys Material (alias)
            if (WorldIconMaterial != null) RenderLayerSet.DestroyMaterialInstance(WorldIconMaterial);
        }
    }
}
