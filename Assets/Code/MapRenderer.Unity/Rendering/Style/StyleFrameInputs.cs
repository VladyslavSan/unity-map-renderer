namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The per-frame inputs a <see cref="IRenderLayer.ApplyZoom"/> call needs. 40 bytes — passed
    /// <c>in</c> everywhere. Bundling the current camera zoom, the device-pixel ratio, the wall
    /// clock, and the layer fade's transition in one struct means the next per-frame input
    /// (pitch/bearing) costs no further call-site churn.
    /// </summary>
    public readonly struct StyleFrameInputs
    {
        /// <summary>The current map zoom level.</summary>
        public double Zoom { get; }

        /// <summary>Physical ÷ logical px for the panel this frame.</summary>
        public double DevicePixelRatio { get; }

        /// <summary>The wall clock a running style transition eases against.</summary>
        public double NowSeconds { get; }

        /// <summary>How long <see cref="RenderLayerSet.ApplyZoom"/>'s layer fade takes to ease across a
        /// zoom bound this frame. Defaults to <see cref="StyleTransition.Default"/> when the caller
        /// passes none — the value every construction site got for free before this field existed.</summary>
        public StyleTransition Transition { get; }

        public StyleFrameInputs(
            double zoom, double devicePixelRatio, double nowSeconds, StyleTransition? transition = null)
        {
            Zoom = zoom;
            DevicePixelRatio = devicePixelRatio;
            NowSeconds = nowSeconds;
            Transition = transition ?? StyleTransition.Default;
        }
    }
}
