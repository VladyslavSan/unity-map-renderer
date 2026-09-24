namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The lifetime class of an <see cref="IRenderLayer"/>'s geometry — which loop feeds it (the
    /// render-layer model). Orthogonal to <see cref="DrawPersistence"/> (who re-draws it) and
    /// <see cref="IRenderLayer.DrawIndex"/> (its stable slot). Background is a source-less
    /// <see cref="TileMesh"/> layer (<c>TileManager.KickSourcelessBackground</c>), so two members suffice.
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
