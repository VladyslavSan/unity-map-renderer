using System.Collections.Generic;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// The parsed root of a MapLibre Style document: typed common fields, the ordered layer list, and the
    /// named sources. Root keys the renderer does not model (<c>center</c>, <c>light</c>, <c>terrain</c>, …)
    /// survive on <see cref="Root"/> as raw JSON. <see cref="Layers"/> keeps the declared order, which is the
    /// painter's order (<c>Rendering/LayerDrawOrder.cs</c>).
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
