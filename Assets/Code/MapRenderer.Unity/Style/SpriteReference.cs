namespace MapRenderer.Unity.Style
{
    /// <summary>
    /// One entry of the Style Spec's root <c>sprite</c>, array form: an id plus its sheet's base URL. A
    /// string-form <c>sprite</c> parses to one entry with id <c>"default"</c> (<see cref="StyleParser"/>).
    /// <c>"default"</c> names stay unprefixed in the merged atlas; every other id prefixes its names
    /// <c>id:name</c>.
    /// </summary>
    public sealed class SpriteReference
    {
        /// <summary>The sprite id (Style Spec <c>sprite[].id</c>).</summary>
        public string Id;

        /// <summary>The sheet's base URL (Style Spec <c>sprite[].url</c>), before the density suffix.</summary>
        public string Url;
    }
}
