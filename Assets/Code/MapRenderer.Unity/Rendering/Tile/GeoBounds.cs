namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// A style source's declared `bounds` (Style Spec [west, south, east, north], degrees) — converted once
    /// from the style parser's raw <c>double[]</c> at the <c>MapView.BuildSourceSpecs</c> wiring boundary and carried as
    /// a value type from there on. <see cref="HasBounds"/> false (the struct default) means no gate: every
    /// zoom-admitted tile passes.
    /// </summary>
    internal readonly struct GeoBounds
    {
        /// <summary>West longitude, degrees.</summary>
        public double West { get; init; }

        /// <summary>South latitude, degrees.</summary>
        public double South { get; init; }

        /// <summary>East longitude, degrees.</summary>
        public double East { get; init; }

        /// <summary>North latitude, degrees.</summary>
        public double North { get; init; }

        /// <summary>True when this value is a real gate; false (the default) means no bounds at all.</summary>
        public bool HasBounds { get; init; }
    }
}
