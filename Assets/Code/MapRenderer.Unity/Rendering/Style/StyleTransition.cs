namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// How long a restyled uniform binding takes to ease from its old value to its new one. Not read
    /// from the style document: no <c>&lt;name&gt;-transition</c> and no root <c>transition</c> block is
    /// parsed (epic decision 0a). The value comes from <see cref="Map.MapView.StyleTransition"/>.
    /// </summary>
    public readonly struct StyleTransition
    {
        /// <summary>How long the ease itself runs, once started.</summary>
        public double DurationSeconds { get; init; }

        /// <summary>How long to hold the old value, at the old zoom, before the ease starts.</summary>
        public double DelaySeconds { get; init; }

        /// <summary>The style-transitions epic's default: 300 ms, no delay.</summary>
        public static StyleTransition Default => new StyleTransition { DurationSeconds = 0.30, DelaySeconds = 0.0 };

        /// <summary>No delay, no ease — a restyled binding snaps to its new value in the same frame.</summary>
        public static StyleTransition Instant => new StyleTransition { DurationSeconds = 0.0, DelaySeconds = 0.0 };

        /// <summary>True when neither a delay nor an ease would be observable — the unanimated path.</summary>
        public bool IsInstant => DurationSeconds <= 0.0 && DelaySeconds <= 0.0;
    }
}
