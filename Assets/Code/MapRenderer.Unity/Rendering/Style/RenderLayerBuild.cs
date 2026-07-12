namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The lifetime class of an <see cref="IRenderLayer"/>'s geometry — which loop feeds it (design
    /// <c>docs/render-layer-unification.md</c> §2 D6). Orthogonal to <see cref="DrawPersistence"/> (who
    /// re-draws it) and <see cref="IRenderLayer.DrawIndex"/> (its slot in the global draw order).
    /// </summary>
    internal enum RenderLayerBuild
    {
        /// <summary>Built once per <c>(tile, layer)</c> by the Burst mesh pipeline, registered with a
        /// <see cref="Backend.ITileRenderBackend"/> (fill, line; later fill-extrusion, raster).</summary>
        TileMesh,

        /// <summary>Rebuilt every frame from screen-space placement (symbol/text collision + layout).</summary>
        FramePlaced,

        /// <summary>Synthesized from the view alone, no tile data (background).</summary>
        ViewGeometry,
    }
}
