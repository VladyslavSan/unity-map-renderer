namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Surface blend category. Values match the <c>_Surface</c> material float (0 = Opaque,
    /// 1 = Transparent). Drives the <c>_SURFACE_TYPE_TRANSPARENT</c> keyword via the tweaker. (S58)
    /// </summary>
    public enum SurfaceType
    {
        Opaque = 0,
        Transparent = 1,
    }
}