namespace MapRenderer.Unity.Rendering.Layers
{
    /// <summary>
    /// An <see cref="IRenderLayer"/> whose draw can be gated by a fade amount — the layer kinds whose
    /// shaders declare <c>_Opacity</c>. A capability interface, not a member on
    /// <see cref="IRenderLayer"/>: the test doubles and the tombstone are excluded with no edit.
    /// <c>SymbolRenderLayer</c> does NOT implement this — see its own remark.
    /// </summary>
    internal interface IFadeableRenderLayer : IRenderLayer
    {
        /// <summary>
        /// Whether the VISIBILITY fade (entering/leaving the zoom range) may take intermediate values.
        /// <c>false</c> means <see cref="RenderLayerSet.UpdateFade"/> arms it Instant, so the layer snaps —
        /// <c>fill-extrusion</c> declares <c>false</c> for every one of its layers. A per-KIND constant,
        /// never per-instance or per-style.
        /// </summary>
        bool FadesGradually { get; }

        /// <summary>
        /// Apply the RESOLVED fade amount — <see cref="RenderLayerSet"/> owns the target, the ease and the
        /// clock, so no transition or timestamp crosses this interface. Called every frame, so an unchanged
        /// amount must cost nothing.
        /// </summary>
        /// <param name="amount">0 (absent) to 1 (fully drawn).</param>
        void SetFade(float amount);

        /// <summary>
        /// True when this layer paints something the framebuffer can show — the predicate the per-slot draw
        /// gate reads (<see cref="Backend.ITileRenderBackend.SetLayerVisible"/>, inverted at that call
        /// site). It folds fade and the authored opacity together, so being out of zoom range and being
        /// authored transparent gate the layer out the same way. A layer mid-fade still reads <c>true</c>:
        /// its draw must still be submitted for there to be anything to blend. Non-local invariant:
        /// <see cref="BackgroundRenderLayer"/> is the one implementer whose applier can be null (base
        /// material unconfigured); it reads <c>true</c> then, fail-open — the gate never retires a draw it cannot read.
        /// </summary>
        bool PaintsSomething { get; }
    }
}
