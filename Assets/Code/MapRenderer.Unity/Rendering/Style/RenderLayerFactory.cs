using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using Symbol = MapRenderer.Core.Style.Symbol;
using Background = MapRenderer.Core.Style.Background;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Maps a parsed <see cref="StyleLayer"/> subtype to its runtime <see cref="IRenderLayer"/> — the ONE
    /// registry the render pipeline consults: a new layer type is one <see cref="IRenderLayer"/> class plus one
    /// arm here. It supports <c>background</c>, <c>fill</c>, <c>line</c>, <c>fill-extrusion</c>, and
    /// <c>symbol</c> with a <c>source</c>. A layer it does not create takes no slot, and
    /// <see cref="Create"/>'s <see cref="LayerSkipReason"/> says why.
    /// </summary>
    internal static class RenderLayerFactory
    {
        /// <summary>Creates the render layer for <paramref name="layer"/> at global slot
        /// <paramref name="drawIndex"/>, or <c>null</c> with <paramref name="reason"/> set to why
        /// (<see cref="LayerSkipReason.None"/> for a real layer). <see cref="RenderLayerSet.Build"/> collects
        /// the reasons into a style-load compatibility summary.</summary>
        public static IRenderLayer Create(
            StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex,
            out LayerSkipReason reason)
        {
            // `visibility: none` takes no slot for THIS style load. A restyle can still flip it on in place:
            // SurvivingLayerGate ignores layout.visibility, so it never rebuilds a layer for that alone.
            if (layer is { Visible: false })
            {
                reason = LayerSkipReason.Hidden;
                return null;
            }

            // Same class: an opacity that is a CONSTANT below one 8-bit step cannot rise at any zoom, so
            // the layer is never built either. The per-frame draw gate decides a zoom- or feature-dependent one.
            if (AuthoredFullyTransparent(layer))
            {
                reason = LayerSkipReason.FullyTransparent;
                return null;
            }

            (IRenderLayer created, LayerSkipReason skipReason) = layer switch
            {
                Fill.StyleLayer f                          => WithMaterialReason(FillRenderLayer.TryCreate(f, settings, initialZoom, drawIndex)),
                Line.StyleLayer l                           => WithMaterialReason(LineRenderLayer.TryCreate(l, settings, initialZoom, drawIndex)),
                // Source != null mirrors SymbolSubsystem.SetStyle's skip, which keeps the 1:1 slot↔subsystem
                // ordinal mapping.
                Symbol.StyleLayer s when s.Source != null   => (SymbolRenderLayer.Create(s, settings, initialZoom, drawIndex), LayerSkipReason.None),
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
        /// True when the layer's own opacity is a CONSTANT below one 8-bit step — invisible at every zoom,
        /// so it is not worth constructing. False for a zoom-dependent opacity (the per-frame draw gate
        /// decides that one), for a feature-dependent opacity (no per-layer scalar can represent it, so the
        /// layer must be built and submitted), and for a kind that carries no opacity of its own.
        /// </summary>
        /// <param name="layer">The style layer to classify.</param>
        private static bool AuthoredFullyTransparent(StyleLayer layer)
        {
            StyleProperty<float> opacity = layer switch
            {
                Fill.StyleLayer f           => f.Paint?.Opacity,
                Line.StyleLayer l           => l.Paint?.Opacity,
                Background.StyleLayer b     => b.Paint?.Opacity,
                FillExtrusion.StyleLayer fe => fe.Paint?.Opacity,
                _                           => null,
            };
            return opacity != null
                && !opacity.DependsOnFeature
                && !opacity.IsZoomDependent
                && opacity.Evaluate(0.0) < ZoomStyleApplier.VisibleOpacityEpsilon;
        }

        /// <summary>
        /// The ONE registry of "which style layers fetch MVT tiles"; <see cref="Map.MapView.BuildSourceSpecs"/>
        /// derives its source-ids from it. True iff <paramref name="layer"/> is fill, line, symbol or
        /// fill-extrusion, is visible, is not authored fully-transparent, AND declares a non-empty
        /// <c>source</c>. So nothing fetches a source no drawing layer reads, and non-MVT bytes stay out of
        /// the MVT decode. A pure predicate with no material or layer-set state.
        /// </summary>
        internal static bool TryGetFetchSource(StyleLayer layer, out string sourceId)
        {
            if (layer != null
                && layer.Visible
                && !AuthoredFullyTransparent(layer)
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
    /// the style-load compatibility summary (<see cref="RenderLayerSet.SkippedLayers"/>) distinguishes
    /// these so a caller never has to infer a misconfiguration from a by-design silence.</summary>
    internal enum LayerSkipReason
    {
        /// <summary>Not skipped — a real layer was returned. The zero/default value, so an unreported
        /// reason reads as "nothing to report" rather than as one of the real reasons below.</summary>
        None = 0,

        /// <summary>The layer's <c>type</c> has no renderer yet — <c>raster</c>, <c>circle</c>,
        /// <c>heatmap</c>, <c>hillshade</c>, <c>color-relief</c>, or an unrecognized string. A real
        /// compatibility gap: the style author asked for something this renderer cannot draw.</summary>
        UnsupportedKind,

        /// <summary>The layer's kind IS supported, but its base material is unconfigured on the active
        /// <see cref="Materials.MapMaterialSet"/> — a configuration error, not a missing feature. Through
        /// <c>MapView.SetStyle</c> only <c>FillExtrusionMaterial</c> reaches it, because
        /// <c>MapMaterialSet.Validate</c> throws for an unassigned Fill/Line base. Never for symbol:
        /// <see cref="SymbolRenderLayer.Create"/> never returns <c>null</c>.</summary>
        MaterialUnconfigured,

        /// <summary>The layer is a supported kind that has nothing to paint by design (a <c>symbol</c>
        /// layer with no <c>source</c>) — not a compatibility problem, so a caller should not surface it as
        /// one.</summary>
        GenuinelyUnpainted,

        /// <summary>The layer declares <c>layout: {"visibility": "none"}</c>. Not a compatibility problem
        /// either: the style author asked for nothing to be drawn, so the layer is not constructed for
        /// THIS style load — a restyle can still flip it back on in place (see <see cref="SurvivingLayerGate"/>).</summary>
        Hidden,

        /// <summary>The layer's opacity is a CONSTANT below one 8-bit step, so it paints nothing at every
        /// zoom — the same never-constructed class as <see cref="Hidden"/>, and by design rather than a
        /// compatibility gap. A zoom- or feature-dependent opacity is not this: those layers are built and
        /// the per-frame draw gate decides them.</summary>
        FullyTransparent,
    }
}
