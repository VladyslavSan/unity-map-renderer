// Engine-free: no UnityEngine dependency.

using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// The parsed MapLibre fill <b>layout</b> properties for a single fill style layer. Read from the
    /// layer's <c>layout</c> sub-tree via <see cref="PropertyNames"/>. Engine-free; clean-room (public
    /// Style Spec, no MapLibre source read).
    ///
    /// <para>Exactly one key today (<c>fill-sort-key</c>), but it is a class rather than a bare property on
    /// <see cref="StyleLayer"/> so it matches <c>Line.LayoutProperties</c>/<c>Symbol.LayoutProperties</c> —
    /// the paint/layout split is the shape every layer kind uses.</para>
    /// </summary>
    public sealed class LayoutProperties
    {
        /// <summary>
        /// fill-sort-key: the within-layer draw order for this layer's features. Default 0.
        /// Zoom- and feature-capable (a <see cref="StyleProperty{T}"/>), so it is evaluated per feature at
        /// build time rather than bound as a uniform.
        ///
        /// <para>Spec ordering: features sort <b>ascending</b>, and a feature with a HIGHER sort key appears
        /// ABOVE one with a lower key.</para>
        /// </summary>
        public StyleProperty<float> SortKey { get; init; }

        /// <summary>True when <c>fill-sort-key</c> was absent — every feature sorts equal, so the builder can
        /// skip the sort entirely and keep the source's declared feature order byte-for-byte.</summary>
        public bool SortKeyIsDefault { get; init; }

        /// <summary>Private: instances come from <see cref="Parse"/>.</summary>
        private LayoutProperties() { }

        /// <summary>Parse the fill layout properties from a layer's <c>layout</c> sub-tree.</summary>
        /// <param name="layout">The raw <c>layout</c> JSON sub-tree, or <c>null</c> for spec defaults.</param>
        /// <returns>A fully-parsed, immutable carrier.</returns>
        public static LayoutProperties Parse(JsonValue layout)
        {
            JsonValue sortKeyJson = layout?.Get(PropertyNames.FillSortKey);
            return new LayoutProperties
            {
                SortKeyIsDefault = (sortKeyJson == null),
                SortKey = sortKeyJson != null
                    ? new StyleProperty<float>(sortKeyJson, 0f, v => (float)v.AsNumber())
                    : new StyleProperty<float>(0f),
            };
        }
    }
}
