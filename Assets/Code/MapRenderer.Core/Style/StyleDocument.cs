using System.Collections.Generic;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// The parsed root of a MapLibre Style document (Style Spec root). Typed common fields plus the
    /// ordered layer list and the named sources. Tolerated root keys the renderer does not yet model
    /// (<c>center</c>/<c>zoom</c>/<c>bearing</c>/<c>pitch</c>/<c>light</c>/<c>terrain</c>/
    /// <c>projection</c>/<c>metadata</c>/…) are preserved on <see cref="Root"/> as raw JSON rather than
    /// thrown, for forward-compat and later stages.
    ///
    /// Layer order is significant (painter's algorithm — see <c>Rendering/LayerDrawOrder.cs</c>); the
    /// <see cref="Layers"/> list preserves the declared order.
    /// </summary>
    public sealed class StyleDocument
    {
        /// <summary>Style spec version (root <c>version</c>; spec requires 8). 0 if absent (tolerated).</summary>
        public int Version;

        /// <summary>Human-readable style name (root <c>name</c>), or null.</summary>
        public string Name;

        /// <summary>Sprite sheet base URL (root <c>sprite</c>), or null.</summary>
        public string Sprite;

        /// <summary>Glyph PBF URL template (root <c>glyphs</c>), or null.</summary>
        public string Glyphs;

        /// <summary>Named sources (root <c>sources</c>), keyed by source id. Never null.</summary>
        public readonly Dictionary<string, SourceDefinition> Sources = new Dictionary<string, SourceDefinition>();

        /// <summary>Layers in declared (paint) order (root <c>layers</c>). Never null.</summary>
        public readonly List<StyleLayer> Layers = new List<StyleLayer>();

        /// <summary>The full original root JSON object (preserves all unknown/forward-compat keys).</summary>
        public JsonValue Root;

        /// <summary>Looks up a source by id; null if not present.</summary>
        public SourceDefinition GetSource(string id)
        {
            if (id != null && Sources.TryGetValue(id, out var s)) return s;
            return null;
        }
    }
}
