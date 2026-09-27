using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The one place a <see cref="TileId"/> becomes a fetch URI. Substitutes <c>{z}</c>, <c>{x}</c>,
    /// <c>{y}</c> into <see cref="Template"/>; a <c>"tms"</c> scheme flips only the substituted <c>y</c>
    /// (<c>y' = (1&lt;&lt;z) - 1 - y</c>), so every other tile identity stays XYZ.
    /// </summary>
    internal readonly struct TileUrlTemplate
    {
        /// <summary>The URL/path template, with <c>{z}</c>/<c>{x}</c>/<c>{y}</c> tokens.</summary>
        public string Template { get; init; }

        /// <summary>True when the source declares <c>scheme: "tms"</c>.</summary>
        public bool Tms { get; init; }

        /// <summary>Resolves <paramref name="id"/> to a concrete fetch URI, flipping <c>y</c> first when
        /// <see cref="Tms"/> is set.</summary>
        public string Resolve(TileId id)
        {
            int y = Tms ? (1 << id.Z) - 1 - id.Y : id.Y;
            return Template
                .Replace("{z}", id.Z.ToString())
                .Replace("{x}", id.X.ToString())
                .Replace("{y}", y.ToString());
        }
    }
}
