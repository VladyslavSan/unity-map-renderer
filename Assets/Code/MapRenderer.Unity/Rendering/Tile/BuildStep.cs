namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// A <see cref="TileManager.LoadedTile"/>'s in-flight mesh-build step (job-scheduling-design.md).
    /// <see cref="None"/> — no build in flight. <see cref="Prologue"/> — a managed
    /// <c>IWorkScheduler</c> body is in flight and <c>LoadedTile.MeshBuildTask</c> is valid (source tiles
    /// only; a background tile starts at <see cref="Measure"/>). <see cref="Measure"/> / <see cref="Write"/>
    /// — the two graph steps; <c>LoadedTile.Graph</c> is valid.
    ///
    /// <para>This enum is the guard that makes <c>MeshBuildTask</c>'s "a <c>default</c> <c>WorkHandle</c>
    /// throws on every member" safe — every reader checks <c>Step == Prologue</c> before touching
    /// <c>MeshBuildTask</c>.</para>
    /// </summary>
    internal enum BuildStep
    {
        None = 0,
        Prologue,
        Measure,
        Write,
    }
}
