namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// A <see cref="TileManager.LoadedTile"/>'s in-flight mesh-build step (job-scheduling-design.md).
    /// <see cref="None"/>: no build. <see cref="Prologue"/>: a managed <c>IWorkScheduler</c> body runs and
    /// <c>LoadedTile.MeshBuildTask</c> is valid (source tiles only; a background tile starts at Measure).
    /// <see cref="Measure"/> / <see cref="Write"/>: <c>LoadedTile.Graph</c> is valid. A <c>default</c>
    /// <c>WorkHandle</c> throws on every member, so every reader checks <c>Step == Prologue</c> first.
    /// </summary>
    internal enum BuildStep
    {
        None = 0,
        Prologue,
        Measure,
        Write,
    }
}
