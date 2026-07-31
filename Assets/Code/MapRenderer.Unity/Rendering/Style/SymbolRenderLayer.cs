using UnityEngine;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.View;
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
    ///
    /// <para><b>G7/D7 (Stage 2, road-shields):</b> the icon and text are coplanar at the same anchor
    /// (D5 centres the shield's number on its sprite) and both shaders are <c>ZWrite Off</c> /
    /// <c>ZTest Always</c> — there is no depth arbitration, only submission order via <c>renderQueue</c>.
    /// So the icon owns this layer's <see cref="LayerSubSlot.Base"/> sub-slot and the text owns
    /// <see cref="LayerSubSlot.Above"/> — the badge always draws under the number it frames, never over
    /// it.</para>
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

        /// <summary>The text sits at <see cref="LayerSubSlot.Above"/>: <see cref="Material"/> IS
        /// <see cref="WorldTextMaterial"/>, and it must draw over this layer's own
        /// <see cref="WorldIconMaterial"/> (at <see cref="LayerSubSlot.Base"/>) — otherwise the badge paints
        /// out the number it frames (G7/D7).</summary>
        public LayerSubSlot MaterialSubSlot => LayerSubSlot.Above;

        /// <summary>The layer's primary drawn material — <see cref="WorldTextMaterial"/>. <c>null</c> iff
        /// <c>MapMaterialSet.SymbolTextWorld</c> is unassigned (the slot↔subsystem-ordinal 1:1 mapping still
        /// requires the layer to take its slot, §3.5; it just never presents). Its <c>renderQueue</c> is
        /// written by <see cref="RenderLayerSet.Build"/> like every other layer's <see cref="Material"/>, at
        /// this layer's <see cref="LayerSubSlot.Above"/> sub-slot (G7/D7).</summary>
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
        /// <see cref="RenderLayerSet.Build"/> time, so it has no free ride. It owns this layer's
        /// <see cref="LayerSubSlot.Base"/> sub-slot, strictly below <see cref="WorldTextMaterial"/>'s
        /// <see cref="LayerSubSlot.Above"/> (G7/D7).</summary>
        public Material WorldIconMaterial { get; }

        /// <summary>The typed parsed symbol layer — MapView's D10 source-fetch derivation reads this
        /// without re-walking <c>style.Layers</c>.</summary>
        public SymbolStyle.StyleLayer SymbolLayer { get; }

        /// <summary>The zoom <see cref="BindHalo"/> was evaluated at, frozen at <see cref="Create"/>. A
        /// dpr-driven re-bind re-evaluates HERE, not at the live zoom — re-evaluating at the live zoom would
        /// silently implement the zoom-expression halo that is a documented follow-up (design §7 risk 9),
        /// which could move a render at dpr 1 and break the stage invariant while looking like a feature.</summary>
        private readonly double _haloZoom;

        /// <summary>The device-pixel ratio the halo uniforms currently carry. Seeded to 1 (which is what
        /// <see cref="Create"/> binds at), so the re-bind branch is dead at dpr 1 — byte-identical.</summary>
        private double _haloDpr = 1.0;

        private SymbolRenderLayer(SymbolStyle.StyleLayer layer,
            Material worldTextMaterial, Material worldIconMaterial, int drawIndex, Transform parent,
            double haloZoom)
        {
            StyleLayer        = layer;
            SymbolLayer       = layer;
            WorldTextMaterial = worldTextMaterial;
            WorldIconMaterial = worldIconMaterial;
            DrawIndex         = drawIndex;
            _haloZoom         = haloZoom;
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
                // Seeded at dpr 1, like every other layer kind's bind-time seed. Unlike fill/line this layer
                // has no applier to re-run, so the live ratio arrives via the first ApplyZoom — which
                // MapView issues immediately after RenderLayerSet.Build, before any frame draws.
                BindHalo(worldTextMat, layer.Paint, initialZoom, 1.0);
            }

            Material baseWorldIconMat = settings != null ? settings.SymbolIconWorld : null;
            Material worldIconMat = null;
            if (baseWorldIconMat == null)
                Debug.LogWarning("[SymbolRenderLayer] MapMaterialSet.SymbolIconWorld unassigned — world icons will not render.");
            else
            {
                worldIconMat = baseWorldIconMat.CloneWithParent();
                worldIconMat.name = $"MapSymbolIconWorld_{layer.Id}";
                // §0.1: no Build-time free ride like WorldTextMaterial/Material. Base explicitly (not the
                // default arg) so the pairing with the text's Above sub-slot is visible at the call site —
                // G7/D7: the icon must draw strictly below its own layer's text.
                worldIconMat.renderQueue = LayerDrawOrder.QueueFor(drawIndex, LayerSubSlot.Base);
            }

            return new SymbolRenderLayer(layer, worldTextMat, worldIconMat, drawIndex, parent, initialZoom);
        }

        private static void BindHalo(Material material, SymbolStyle.PaintProperties paint, double zoom,
            double devicePixelRatio)
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
                // S107: both halo terms are added to a signed distance the SDF shader carries in DEVICE px
                // (screenDist, from an fwidth-derived screen scale), so both take the px→device conversion.
                // The pair scales TOGETHER — splitting it would render the halo inconsistently at dpr ≠ 1.
                material.SetFloat(HaloWidthId,
                    (float)DeviceScaling.LogicalToDevicePx(paint.HaloWidth.Evaluate(zoom), PixelSpace.Device, devicePixelRatio));
                material.SetFloat(HaloBlurId,
                    (float)DeviceScaling.LogicalToDevicePx(paint.HaloBlur.Evaluate(zoom), PixelSpace.Device, devicePixelRatio));
            }
            catch (System.ArgumentException)
            {
                // data-driven halo not supported yet — keep the base material's halo.
            }
        }

        /// <summary>Re-binds the halo ONLY when the device-pixel ratio changed, and re-evaluates at the
        /// frozen <see cref="_haloZoom"/> — see that field for why the live zoom must not be used. The
        /// zoom-expression halo remains a documented follow-up (design §7 risk 9); at a steady ratio this
        /// stays the no-op it has always been.</summary>
        public void ApplyZoom(double zoom, double devicePixelRatio)
        {
            if (devicePixelRatio == _haloDpr || WorldTextMaterial == null) return;
            _haloDpr = devicePixelRatio;
            BindHalo(WorldTextMaterial, SymbolLayer.Paint, _haloZoom, devicePixelRatio);
        }

        public void Dispose()
        {
            if (WorldTextMaterial != null) RenderLayerSet.DestroyMaterialInstance(WorldTextMaterial); // also destroys Material (alias)
            if (WorldIconMaterial != null) RenderLayerSet.DestroyMaterialInstance(WorldIconMaterial);
        }
    }
}
