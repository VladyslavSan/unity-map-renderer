using UnityEngine;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using Symbol = MapRenderer.Core.Style.Symbol;
using Background = MapRenderer.Core.Style.Background;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Maps a parsed <see cref="StyleLayer"/> subtype to its runtime <see cref="IRenderLayer"/>. This is the
    /// ONE registry the render pipeline consults: adding a layer type is one new <see cref="IRenderLayer"/>
    /// class + one arm here — the layer set, the backends, and the tile consume loop need no edits (S89
    /// extensibility tooth). <paramref name="drawIndex"/> is the global slot the layer will occupy if
    /// created (D7) — threaded straight into the concrete ctor/<c>TryCreate</c>/<c>Create</c>, immutable
    /// afterwards.
    ///
    /// <para>Genuinely unpainted / unsupported-for-now types (raster, circle, unknown) return <c>null</c> —
    /// they take no slot in the ordered <see cref="RenderLayerSet"/>. Background and symbol DO take a slot
    /// as of E1 (D7 global numbering); every painted kind is material-bearing when its base material is
    /// configured (symbol as of E2/D11, background as of E3, fill-extrusion as of S23 I1 — a flat
    /// placeholder reusing the fill base material) — the queue write in <see cref="RenderLayerSet.Build"/>
    /// fires for them like any tile-mesh layer.</para>
    /// </summary>
    internal static class RenderLayerFactory
    {
        /// <summary>Creates the render layer for <paramref name="layer"/> at global slot
        /// <paramref name="drawIndex"/>, or <c>null</c> when the type is genuinely unpainted, or (for
        /// tile-mesh kinds) the material set is unconfigured (the concrete <c>TryCreate</c> returns null and
        /// the factory propagates it).</summary>
        public static IRenderLayer Create(
            StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex,
            Transform parent = null)
            => layer switch
            {
                Fill.StyleLayer f                          => FillRenderLayer.TryCreate(f, settings, initialZoom, drawIndex),
                Line.StyleLayer l                           => LineRenderLayer.TryCreate(l, settings, initialZoom, drawIndex),
                // The Source != null guard mirrors SymbolSubsystem.SetStyle's skip so the slot-taking
                // symbol layers stay exactly the set the subsystem manages — the 1:1 slot↔subsystem-ordinal
                // mapping E2 relies on. Create never returns null (unlike TryCreate) — see its own doc.
                // Only the GameObject-bearing symbol presenter receives the Hierarchy parent; fill/line/
                // background have no scene object and take no parent (A2: background's quads are backend-owned).
                Symbol.StyleLayer s when s.Source != null   => SymbolRenderLayer.Create(s, settings, initialZoom, drawIndex, parent),
                Background.StyleLayer b                     => BackgroundRenderLayer.Create(b, settings, initialZoom, drawIndex),
                FillExtrusion.StyleLayer fe                 => FillExtrusionRenderLayer.TryCreate(fe, settings, initialZoom, drawIndex),
                _                                            => null,
            };

        /// <summary>
        /// Epic A / A2 (design §E step 5, HIGH c): the ONE registry of "which style layers fetch MVT tiles" —
        /// <see cref="Map.MapView.BuildSourceSpecs"/> derives its source-ids from this predicate instead of
        /// re-walking <c>style.Layers</c> with an ad-hoc <c>is</c>-check. <see langword="true"/> iff
        /// <paramref name="layer"/> is an MVT-fetching kind (fill, line, symbol, fill-extrusion) AND declares a non-empty
        /// <c>source</c> — background is source-less by design (excluded here, not just by having no
        /// <c>Source</c>), and raster/circle/hillshade/unknown are unsupported-for-now (excluded so no
        /// non-MVT bytes are ever pushed through the MVT decode). Uses <see cref="StyleLayer.LayerType"/>
        /// (never <c>.Type</c> — no such member). A pure predicate: never touches the active
        /// <see cref="RenderLayerSet"/> or any material state.
        /// </summary>
        internal static bool TryGetFetchSource(StyleLayer layer, out string sourceId)
        {
            if (layer != null
                && layer.LayerType is StyleLayerType.Fill or StyleLayerType.Line or StyleLayerType.Symbol
                    or StyleLayerType.FillExtrusion
                && !string.IsNullOrEmpty(layer.Source))
            {
                sourceId = layer.Source;
                return true;
            }
            sourceId = null;
            return false;
        }
    }
}
