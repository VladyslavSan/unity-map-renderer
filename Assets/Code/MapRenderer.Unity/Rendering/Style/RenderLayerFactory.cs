using UnityEngine;
using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using Symbol = MapRenderer.Core.Style.Symbol;
using Background = MapRenderer.Core.Style.Background;

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
    /// <para>Genuinely unpainted / unsupported-for-now types (raster, circle, unknown, and — for now —
    /// fill-extrusion) return <c>null</c> — they take no slot in the ordered <see cref="RenderLayerSet"/>.
    /// Background and symbol DO take a slot as of E1 (D7 global numbering); every painted kind is
    /// material-bearing when its base material is configured (symbol as of E2/D11, background as of E3) —
    /// the queue write in <see cref="RenderLayerSet.Build"/> fires for them like any tile-mesh layer.</para>
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
                // The Source != null guard mirrors SymbolLabelSubsystem.SetStyle's skip so the slot-taking
                // symbol layers stay exactly the set the subsystem manages — the 1:1 slot↔subsystem-ordinal
                // mapping E2 relies on. Create never returns null (unlike TryCreate) — see its own doc.
                // Only the GameObject-bearing kinds (symbol presenter, background quad) receive the Hierarchy
                // parent; fill/line have no scene object and ignore it.
                Symbol.StyleLayer s when s.Source != null   => SymbolRenderLayer.Create(s, settings, initialZoom, drawIndex, parent),
                Background.StyleLayer b                     => BackgroundRenderLayer.Create(b, settings, initialZoom, drawIndex, parent),
                _                                            => null,
            };
    }
}
