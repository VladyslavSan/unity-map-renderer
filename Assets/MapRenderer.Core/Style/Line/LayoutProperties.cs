using MapRenderer.Core.Geometry;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// The parsed MapLibre line <b>layout</b> properties for a single line style layer — the join/cap/limit
    /// knobs baked at tessellate time, split out of the former <c>LinePaint</c>. Read from the layer's
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
        public JoinType Join { get; }

        /// <summary>
        /// line-cap: how line endpoints are drawn. Default <see cref="CapType.Butt"/>.
        /// Parsed once at construction from the layout JSON string via <see cref="PropertyNames.CapButt"/>/
        /// <see cref="PropertyNames.CapRound"/>/<see cref="PropertyNames.CapSquare"/>; the tessellator
        /// consumes the enum directly.
        /// </summary>
        public CapType Cap { get; }

        /// <summary>line-miter-limit: miter-to-bevel fallback threshold. Default 2. Units: ratio (1/cos(θ/2)).</summary>
        public double MiterLimit { get; }

        /// <summary>line-round-limit: round-to-miter fallback threshold. Default 1.05. Units: ratio.</summary>
        public double RoundLimit { get; }

        /// <summary>Convenience: parse the layout properties from a style layer's <c>LayoutJson</c>.</summary>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public LayoutProperties(MapRenderer.Core.Style.StyleLayer layer)
            : this((layer ?? throw new System.ArgumentNullException(nameof(layer))).LayoutJson) { }

        /// <summary>Parse the line layout properties from the layer's <c>layout</c> sub-tree (may be null → defaults).</summary>
        public LayoutProperties(JsonValue layout)
        {
            string joinStr = layout?.Get(PropertyNames.LineJoin)?.AsString(PropertyNames.JoinMiter) ?? PropertyNames.JoinMiter;
            Join = ParseJoin(joinStr);

            string capStr = layout?.Get(PropertyNames.LineCap)?.AsString(PropertyNames.CapButt) ?? PropertyNames.CapButt;
            Cap = ParseCap(capStr);

            MiterLimit = layout?.Get(PropertyNames.LineMiterLimit)?.AsDouble(2.0)  ?? 2.0;
            RoundLimit = layout?.Get(PropertyNames.LineRoundLimit)?.AsDouble(1.05) ?? 1.05;
        }

        // ── String → enum helpers (single source of truth; uses PropertyNames value consts) ──

        private static JoinType ParseJoin(string s)
        {
            if (s == PropertyNames.JoinRound) return JoinType.Round;
            if (s == PropertyNames.JoinBevel) return JoinType.Bevel;
            return JoinType.Miter; // default (also the JoinMiter value)
        }

        private static CapType ParseCap(string s)
        {
            if (s == PropertyNames.CapRound)  return CapType.Round;
            if (s == PropertyNames.CapSquare) return CapType.Square;
            return CapType.Butt; // default (also the CapButt value)
        }
    }
}
