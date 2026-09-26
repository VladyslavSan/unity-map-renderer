using System;
using System.Collections.Generic;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Test-only "how many uniform bindings are still easing" reader for
    /// <see cref="ZoomStyleApplier"/>, each concrete <see cref="IRenderLayer"/>, and
    /// <see cref="RenderLayerSet"/> (via <c>InternalsVisibleTo</c>) — no production code reads this count,
    /// so it lives here instead of on any of the three. Extension members cannot be properties, so call
    /// sites read <c>layer.TransitioningCount()</c> etc.
    /// </summary>
    internal static class RenderLayerTestExtensions
    {
        /// <summary>Entries currently easing, across all four of the applier's typed binding lists.</summary>
        public static int TransitioningCount(this ZoomStyleApplier applier)
            => CountEasing(applier._floatBindings) + CountEasing(applier._colorBindings)
             + CountEasing(applier._devicePixelFloatBindings) + CountEasing(applier._devicePixelVectorBindings);

        /// <summary>Entries in one typed binding list whose <c>Origin</c> is non-null (still easing).</summary>
        private static int CountEasing<T>(List<ZoomStyleApplier.Binding<T>> list)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Origin != null) n++;
            return n;
        }

        /// <summary>This layer's own applier, read off its concrete type — 0 for
        /// <see cref="TombstoneRenderLayer"/> or any layer whose base material is unconfigured (a null
        /// <c>Applier</c>). Throws for an unlisted kind, so a new layer kind reads as UNKNOWN, never as
        /// silently settled.</summary>
        public static int TransitioningCount(this IRenderLayer layer)
        {
            ZoomStyleApplier applier = layer switch
            {
                FillRenderLayer f => f.Applier,
                LineRenderLayer l => l.Applier,
                FillExtrusionRenderLayer fe => fe.Applier,
                BackgroundRenderLayer b => b.Applier,
                SymbolRenderLayer s => s.Applier,
                TombstoneRenderLayer => null,
                _ => throw new NotSupportedException(
                    $"RenderLayerTestExtensions.TransitioningCount does not know layer kind {layer.GetType().Name}."),
            };
            return applier?.TransitioningCount() ?? 0;
        }

        /// <summary>Summed across every layer currently in the set.</summary>
        public static int TransitioningCount(this RenderLayerSet set)
        {
            int n = 0;
            for (int i = 0; i < set.Count; i++)
                n += set[i].TransitioningCount();
            return n;
        }
    }
}
