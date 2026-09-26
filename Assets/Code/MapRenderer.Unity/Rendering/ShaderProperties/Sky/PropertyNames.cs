namespace MapRenderer.Unity.Rendering.ShaderProperties.Sky
{
    /// <summary>
    /// Canonical string names for the <c>Map/Sky</c> shader properties. Use <see cref="PropertyId"/> for
    /// <c>Material.Set/Get/Has</c> calls; use this class only where the Unity API requires a string.
    /// </summary>
    public static class PropertyNames
    {
        /// <summary>sky-color: the colour at and above the top of the blend.</summary>
        public const string SkyColor = "_SkyColor";

        /// <summary>horizon-color: the colour at and below the map edge.</summary>
        public const string HorizonColor = "_HorizonColor";

        /// <summary>sky-horizon-blend: the fraction of the visible sky strip, from the map edge to the top
        /// of the screen, that the blend spans.</summary>
        public const string SkyHorizonBlend = "_SkyHorizonBlend";

        /// <summary>Elevation, in radians, of the view ray to where the rendered map ends; zero or negative.
        /// Pushed per frame.</summary>
        public const string MapEdgeElevation = "_MapEdgeElevation";

        /// <summary>Elevation, in radians, of the view ray through the top of the screen. Pushed per frame.</summary>
        public const string SkyTopElevation = "_SkyTopElevation";
    }
}
