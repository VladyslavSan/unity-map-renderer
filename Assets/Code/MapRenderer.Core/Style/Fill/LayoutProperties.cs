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
        public StyleProperty<float> SortKey { get; }

        /// <summary>True when <c>fill-sort-key</c> was absent — every feature sorts equal, so the builder can
        /// skip the sort entirely and keep the source's declared feature order byte-for-byte.</summary>
        public bool SortKeyIsDefault { get; }

        /// <summary>Convenience: parse the layout properties from a style layer's <c>LayoutJson</c>.</summary>
        /// <exception cref="System.ArgumentNullException">If <paramref name="layer"/> is null.</exception>
        public LayoutProperties(MapRenderer.Core.Style.StyleLayer layer)
            : this((layer ?? throw new System.ArgumentNullException(nameof(layer))).LayoutJson) { }

        /// <summary>Parse the fill layout properties from the layer's <c>layout</c> sub-tree (may be null →
        /// spec defaults).</summary>
        public LayoutProperties(JsonValue layout)
        {
            JsonValue sortKeyJson = layout?.Get(PropertyNames.FillSortKey);
            SortKeyIsDefault = (sortKeyJson == null);
            SortKey = sortKeyJson != null
                ? new StyleProperty<float>(sortKeyJson, 0f, v => (float)v.AsNumber())
                : new StyleProperty<float>(0f);
        }
    }
}
