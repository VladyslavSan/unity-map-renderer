using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Test-only accessors that peek at <see cref="MapView"/> internals (granted via
    /// <c>InternalsVisibleTo</c> on MapRenderer.Unity) WITHOUT adding test-support members to MapView's
    /// production API. The test-support surface lives here, in the test assembly, where it belongs.
    /// </summary>
    internal static class MapViewTestExtensions
    {
        /// <summary>Number of fill render bundles MapView built from the style.</summary>
        public static int FillLayerCount(this MapView view) => view.Layers.FillCount;

        /// <summary>Number of line render bundles MapView built from the style.</summary>
        public static int LineLayerCount(this MapView view) => view.Layers.LineCount;
    }
}
