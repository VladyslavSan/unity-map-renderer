namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The lifetime class of an <see cref="IRenderLayer"/>'s geometry — which loop feeds it (design
    /// <c>docs/render-layer-unification.md</c> §2 D6). Orthogonal to <see cref="DrawPersistence"/> (who
    /// re-draws it) and <see cref="IRenderLayer.DrawIndex"/> (its slot in the global draw order).
    ///
    /// Epic A / A2: the former view-synthesized-geometry member (background's pre-A2 self-owned world-cap
    /// quad) is REMOVED — background is now a per-covered-tile <see cref="TileMesh"/> layer (a source-less
    /// <see cref="Tile.Processing.TileBackgroundLayerProcessor"/> registered with the backend like fill/line),
    /// so the axis collapses to exactly these two members (design §B, "the RenderLayerBuild enum decision").
    /// </summary>
    internal enum RenderLayerBuild
    {
        /// <summary>Built once per <c>(tile, layer)</c> by the Burst mesh pipeline, registered with a
        /// <see cref="Backend.ITileRenderBackend"/> (fill, line, background; later fill-extrusion, raster).</summary>
        TileMesh,

        /// <summary>Rebuilt every frame from screen-space placement (symbol/text collision + layout).</summary>
        FramePlaced,
    }
}
