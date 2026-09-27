using System;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Json;

namespace MapRenderer.Unity.Style
{
    /// <summary>
    /// A style source's <c>data</c> key (Style Spec <c>sources[].data</c>), parsed once at style load.
    /// It is a URL string OR an inline GeoJSON object. <see cref="Parse"/> never throws: a malformed
    /// inline object yields an instance whose <see cref="Dataset"/> is <see langword="null"/> and
    /// <see cref="Error"/> carries the parse message.
    /// </summary>
    public sealed class SourcePayload
    {
        /// <summary>The authored <c>data</c> JSON, unparsed — <c>TileManager.SourceKey</c>'s identity input.</summary>
        internal JsonValue Raw { get; }

        /// <summary>The URL string, or <see langword="null"/> when <c>data</c> is not a string.</summary>
        internal string Url { get; }

        /// <summary>The parsed inline dataset, or <see langword="null"/> when <c>data</c> is not an
        /// object, or <see cref="Error"/> is set.</summary>
        internal GeoJsonDataset Dataset { get; }

        /// <summary>The failed inline parse's message, or <see langword="null"/> when it parsed or
        /// <c>data</c> is not an object.</summary>
        internal string Error { get; }

        private SourcePayload(JsonValue raw, string url, GeoJsonDataset dataset, string error)
        {
            Raw = raw;
            Url = url;
            Dataset = dataset;
            Error = error;
        }

        /// <summary>Parses a source's <c>data</c> sub-tree. Returns <see langword="null"/> for an absent
        /// key; a JSON <c>null</c> or a non-object, non-string value yields an instance with every arm
        /// null, so its presence still distinguishes it from an absent key.</summary>
        internal static SourcePayload Parse(JsonValue json)
        {
            if (json == null)
                return null;

            if (json.Kind == JsonKind.String)
                return new SourcePayload(json, json.AsString(), dataset: null, error: null);

            if (!json.IsObject)
                return new SourcePayload(json, url: null, dataset: null, error: null);

            try
            {
                return new SourcePayload(json, url: null, GeoJsonParser.Parse(json), error: null);
            }
            catch (Exception ex)
            {
                // Any throw, not only GeoJsonFormatException: one that escapes fails the whole style load.
                return new SourcePayload(json, url: null, dataset: null, ex.Message);
            }
        }
    }
}
