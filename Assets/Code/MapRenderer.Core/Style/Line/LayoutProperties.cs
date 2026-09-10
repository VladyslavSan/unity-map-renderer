using MapRenderer.Core.Geometry;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// The parsed MapLibre line <b>layout</b> properties for a single line style layer — the join/cap/limit
    /// knobs baked at build time, split out of the former <c>LinePaint</c>. Read from the layer's
    /// <c>layout</c> sub-tree via <see cref="PropertyNames"/>. Engine-free; clean-room (public Style Spec).
    ///
    /// Join and Cap are parsed once at construction into their typed enum values so the tessellator can
    /// consume them directly (no per-build string switch). <c>MiterLimit</c> and <c>RoundLimit</c> remain
    /// plain <c>double</c> because <see cref="LineTessellator.Triangulate"/> consumes <c>double</c>
    /// (changing to <c>float</c> would introduce a precision delta with no spec justification).
    /// </summary>
    public sealed class LayoutProperties
    {
        /// <summary>
        /// line-join: how two line segments join at a corner. Default <see cref="JoinType.Miter"/>.
        /// Parsed once at construction from the layout JSON string via <see cref="PropertyNames.JoinMiter"/>/
        /// <see cref="PropertyNames.JoinRound"/>/<see cref="PropertyNames.JoinBevel"/>; the tessellator
        /// consumes the enum directly.
        /// </summary>
        public JoinType Join { get; init; }

        /// <summary>
        /// line-cap: how line endpoints are drawn. Default <see cref="CapType.Butt"/>.
        /// Parsed once at construction from the layout JSON string via <see cref="PropertyNames.CapButt"/>/
        /// <see cref="PropertyNames.CapRound"/>/<see cref="PropertyNames.CapSquare"/>; the tessellator
        /// consumes the enum directly.
        /// </summary>
        public CapType Cap { get; init; }

        /// <summary>line-miter-limit: miter-to-bevel fallback threshold. Default 2. Units: ratio (1/cos(θ/2)).</summary>
        public double MiterLimit { get; init; }

        /// <summary>line-round-limit: round-to-miter fallback threshold. Default 1.05. Units: ratio.</summary>
        public double RoundLimit { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private LayoutProperties() { }

        /// <summary>Parse the line layout properties from a layer's <c>layout</c> sub-tree.</summary>
        /// <param name="layout">The raw <c>layout</c> JSON sub-tree, or <c>null</c> for all spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static LayoutProperties Parse(JsonValue layout)
        {
            string joinStr = layout?.Get(PropertyNames.LineJoin)?.AsString(PropertyNames.JoinMiter) ?? PropertyNames.JoinMiter;
            string capStr  = layout?.Get(PropertyNames.LineCap)?.AsString(PropertyNames.CapButt) ?? PropertyNames.CapButt;

            return new LayoutProperties
            {
                Join       = ParseJoin(joinStr),
                Cap        = ParseCap(capStr),
                MiterLimit = layout?.Get(PropertyNames.LineMiterLimit)?.AsDouble(2.0)  ?? 2.0,
                RoundLimit = layout?.Get(PropertyNames.LineRoundLimit)?.AsDouble(1.05) ?? 1.05,
            };
        }

        // ── String → enum helpers (single source of truth; uses PropertyNames value consts) ──

        private static JoinType ParseJoin(string s)
        {
            return s switch
            {
                PropertyNames.JoinRound => JoinType.Round,
                PropertyNames.JoinBevel => JoinType.Bevel,
                _                       => JoinType.Miter
            };
        }

        private static CapType ParseCap(string s)
        {
            return s switch
            {
                PropertyNames.CapRound  => CapType.Round,
                PropertyNames.CapSquare => CapType.Square,
                _                       => CapType.Butt
            };
        }
    }
}
