namespace MapRenderer.Core.Style.Background
{
    /// <summary>
    /// The single source of the MapLibre <c>background-*</c> style key strings. Every other class in
    /// <c>Style.Background</c> references these constants; no <c>"background-…"</c> literal lives
    /// elsewhere (enforced by a test, mirroring Fill). Background has no layout keys, so all keys here
    /// are paint. Clean-room: public Style Spec.
    /// </summary>
    public static class PropertyNames
    {
        public const string BackgroundColor   = "background-color";
        public const string BackgroundOpacity = "background-opacity";
        public const string BackgroundPattern = "background-pattern";
    }
}
