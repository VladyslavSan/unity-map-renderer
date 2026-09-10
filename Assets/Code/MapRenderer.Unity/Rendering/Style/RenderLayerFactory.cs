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
    /// <para><b>Supported kinds</b> (paint a slot when configured): <c>background</c>, <c>fill</c>,
    /// <c>line</c>, <c>fill-extrusion</c>, and <c>symbol</c> (only when it declares a <c>source</c> —
    /// a source-less symbol layer has nothing to place, so it takes no slot either, but
    /// that is by design, not a gap). <b>Not yet supported</b> (always <c>null</c>, no slot): <c>raster</c>,
    /// <c>circle</c>, <c>heatmap</c>, <c>hillshade</c>, <c>color-relief</c>, and any unrecognized <c>type</c>
    /// string. Every supported kind is material-bearing when its base material is configured (symbol as of
    /// E2/D11, background as of E3, fill-extrusion as of S23 I1 — a flat placeholder reusing the fill base
    /// material) — the queue write in <see cref="RenderLayerSet.Build"/> fires for them like any tile-mesh
    /// layer; when a supported kind's own base material is unconfigured, the concrete <c>TryCreate</c>
    /// returns <c>null</c> and <see cref="Create"/> propagates it, same as an unsupported kind. UMR-116:
    /// <see cref="Create"/>'s <c>reason</c> out-parameter tells these two apart (plus the by-design symbol
    /// case) — see <see cref="LayerSkipReason"/>.</para>
    /// </summary>
    internal static class RenderLayerFactory
    {
        /// <summary>Creates the render layer for <paramref name="layer"/> at global slot
        /// <paramref name="drawIndex"/>, or <c>null</c> with <paramref name="reason"/> set to why (left at
        /// <see cref="LayerSkipReason.None"/> when a real layer comes back) — see the class doc for which
        /// kinds are supported. UMR-116: <paramref name="reason"/> is what lets
        /// <see cref="RenderLayerSet.Build"/> collect a style-load compatibility summary instead of silently
        /// dropping the layer.</summary>
        public static IRenderLayer Create(
            StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex,
            out LayerSkipReason reason, Transform parent = null)
        {
            (IRenderLayer created, LayerSkipReason skipReason) = layer switch
            {
                Fill.StyleLayer f                          => WithMaterialReason(FillRenderLayer.TryCreate(f, settings, initialZoom, drawIndex)),
                Line.StyleLayer l                           => WithMaterialReason(LineRenderLayer.TryCreate(l, settings, initialZoom, drawIndex)),
                // The Source != null guard mirrors SymbolSubsystem.SetStyle's skip so the slot-taking
                // symbol layers stay exactly the set the subsystem manages — the 1:1 slot↔subsystem-ordinal
                // mapping E2 relies on. Create never returns null (unlike TryCreate) — see its own doc.
                // Only the GameObject-bearing symbol presenter receives the Hierarchy parent; fill/line/
                // background have no scene object and take no parent (A2: background's quads are backend-owned).
                Symbol.StyleLayer s when s.Source != null   => (SymbolRenderLayer.Create(s, settings, initialZoom, drawIndex, parent), LayerSkipReason.None),
                // A source-less symbol layer has nothing to place — by design, not a compatibility gap.
                Symbol.StyleLayer                           => ((IRenderLayer)null, LayerSkipReason.GenuinelyUnpainted),
                Background.StyleLayer b                     => (BackgroundRenderLayer.Create(b, settings, initialZoom, drawIndex), LayerSkipReason.None),
                FillExtrusion.StyleLayer fe                 => WithMaterialReason(FillExtrusionRenderLayer.TryCreate(fe, settings, initialZoom, drawIndex)),
                _                                            => ((IRenderLayer)null, LayerSkipReason.UnsupportedKind),
            };
            reason = skipReason;
            return created;

            // A supported kind's own TryCreate returns null for exactly one reason: MapMaterialSet's base
            // material for that kind is unconfigured (see each TryCreate's doc) — never "unsupported".
            static (IRenderLayer, LayerSkipReason) WithMaterialReason(IRenderLayer l)
                => (l, l == null ? LayerSkipReason.MaterialUnconfigured : LayerSkipReason.None);
        }

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

    /// <summary>Why <see cref="RenderLayerFactory.Create"/> returned <c>null</c> for a style layer —
    /// UMR-116's style-load compatibility summary (<see cref="RenderLayerSet.SkippedLayers"/>) distinguishes
    /// these so a caller never has to infer a misconfiguration from a deliberate, by-design silence.</summary>
    internal enum LayerSkipReason
    {
        /// <summary>Not skipped — a real layer was returned. The zero/default value, so an unreported
        /// reason reads as "nothing to report" rather than as one of the three below.</summary>
        None = 0,

        /// <summary>The layer's <c>type</c> has no renderer yet — <c>raster</c>, <c>circle</c>,
        /// <c>heatmap</c>, <c>hillshade</c>, <c>color-relief</c>, or an unrecognized string. A real
        /// compatibility gap: the style author asked for something this renderer cannot draw.</summary>
        UnsupportedKind,

        /// <summary>The layer's kind IS supported, but its base material is unconfigured on the active
        /// <see cref="Materials.MapMaterialSet"/> — a configuration error, not a missing feature. On the
        /// <c>MapView.SetStyle</c> path this is reachable only through <c>FillExtrusionMaterial</c>:
        /// <c>MapMaterialSet.Validate</c> (called before <see cref="RenderLayerSet.Build"/>) already throws
        /// for an unassigned Fill/Line base, so this reason fires for those two only when
        /// <see cref="RenderLayerFactory.Create"/> is called directly (e.g. a test) against a set that
        /// skipped <c>Validate</c>. Never fires for symbol — <see cref="SymbolRenderLayer.Create"/> never
        /// returns <c>null</c>, so a symbol layer with a source is always on the <see cref="None"/>
        /// arm regardless of material configuration.</summary>
        MaterialUnconfigured,

        /// <summary>The layer is a supported kind that has nothing to paint by design (a <c>symbol</c>
        /// layer with no <c>source</c>) — not a compatibility problem, so a caller should not surface it as
        /// one.</summary>
        GenuinelyUnpainted,
    }
}
