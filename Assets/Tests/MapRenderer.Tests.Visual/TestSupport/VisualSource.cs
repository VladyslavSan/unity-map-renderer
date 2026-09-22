// Unity EditMode only — the declarative visual-test authoring kit.
// NOT registered in Tools/core-tests/core-tests.csproj (engine-bound: the composer this feeds drives a
// real MapViewComponent).
//
// The extensible SOURCE half of the kit. Shaped so an MVT/URL source kind can slot in later (a new
// VisualSource subclass) without touching VisualScene. Only the inline-geojson kind exists today.

#if UNITY_EDITOR
namespace MapRenderer.Tests
{
    /// <summary>
    /// A named style source the <see cref="VisualScene"/> composer can bind layers to by id. Emits ONE
    /// "sources" entry's VALUE — <see cref="ToSourceJson"/> returns just the <c>{ "type": …, … }</c> object,
    /// not the enclosing <c>"id": …</c> pair (the composer owns the id → value mapping in the style JSON).
    /// </summary>
    internal abstract class VisualSource
    {
        /// <summary>The style-JSON source id this source is bound to. Assigned by
        /// <see cref="VisualScene.Source"/> — a source built via <see cref="GeoJson"/> does not know its own
        /// id until the composer binds it.</summary>
        public string Id { get; internal set; }

        /// <summary>The source's style-JSON VALUE (e.g. <c>{"type":"geojson","data":…}</c>).</summary>
        public abstract string ToSourceJson();
    }

    /// <summary>An inline-GeoJSON <see cref="VisualSource"/> — the only source kind the kit implements.
    /// Emits <c>{"type":"geojson","data":&lt;dataJson&gt;}</c>, exactly
    /// the shape <c>MapView.BuildSourceSpecs</c>' geojson branch requires (an inline JSON OBJECT, never a URL
    /// string).</summary>
    internal sealed class GeoJsonInlineSource : VisualSource
    {
        private readonly string _dataJson;

        /// <param name="dataJson">The inline dataset's raw GeoJSON, spliced verbatim as the source's
        /// <c>"data"</c> value (a FeatureCollection/Feature/geometry object, never a URL string).</param>
        internal GeoJsonInlineSource(string dataJson) => _dataJson = dataJson;

        /// <inheritdoc/>
        public override string ToSourceJson() => $"{{\"type\":\"geojson\",\"data\":{_dataJson}}}";
    }

    /// <summary>Factories for inline-GeoJSON <see cref="VisualSource"/>s. Engine-free JSON assembly,
    /// delegated to <see cref="GeoJsonTestFixtures"/> — the same, already-verified string builders
    /// <c>GeoJsonSourceTests</c> uses, never re-derived here.</summary>
    internal static class GeoJson
    {
        /// <summary>A single-feature FeatureCollection carrying one axis-aligned lon/lat rectangle polygon
        /// (RFC 7946 §3.1.6 right-hand-rule winding).</summary>
        /// <param name="westLon">Western longitude bound, degrees.</param>
        /// <param name="southLat">Southern latitude bound, degrees.</param>
        /// <param name="eastLon">Eastern longitude bound, degrees.</param>
        /// <param name="northLat">Northern latitude bound, degrees.</param>
        public static GeoJsonInlineSource Polygon(double westLon, double southLat, double eastLon, double northLat)
        {
            string ring = GeoJsonTestFixtures.RectangleRing(westLon, southLat, eastLon, northLat);
            string feature = GeoJsonTestFixtures.Feature("Polygon", $"[{ring}]");
            return new GeoJsonInlineSource(GeoJsonTestFixtures.Collection(feature));
        }

        /// <summary>An inline dataset from a caller-supplied raw FeatureCollection JSON string — the escape
        /// hatch for shapes <see cref="Polygon"/> does not build (e.g. an empty collection for a negative
        /// control).</summary>
        public static GeoJsonInlineSource FeatureCollection(string rawFeatureCollectionJson)
            => new GeoJsonInlineSource(rawFeatureCollectionJson);

        /// <summary>A FeatureCollection of Point features, each carrying a single <c>"name"</c> property —
        /// the shape a <c>text-field":"{name}"</c> symbol layer resolves against. An empty
        /// <paramref name="pts"/> is a valid, reachable negative control (an empty-features collection),
        /// via the same <see cref="GeoJsonTestFixtures.Collection"/> the fill negative control uses.</summary>
        /// <param name="pts">Each point's (longitude, latitude, the "name" property value).</param>
        public static GeoJsonInlineSource Points(params (double lon, double lat, string name)[] pts)
        {
            var features = new string[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                (double lon, double lat, string name) = pts[i];
                string coordinates = GeoJsonTestFixtures.Position(lon, lat);
                string properties  = $"{{\"name\":\"{name}\"}}";
                features[i] = GeoJsonTestFixtures.Feature("Point", coordinates, properties);
            }
            return new GeoJsonInlineSource(GeoJsonTestFixtures.Collection(features));
        }
    }
}
#endif // UNITY_EDITOR
