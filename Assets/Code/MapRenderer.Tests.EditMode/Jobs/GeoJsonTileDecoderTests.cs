// Unity EditMode only — the real GeoJsonTileDecoder allocates Allocator.Persistent native buffers through
// PathGeometryMaterializer. NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// GeoJSON S2 — the decoder half: <b>T2</b> (a geojson tile answers with its sole layer whatever
    /// <c>source-layer</c> says, and moving that accommodation out of <c>SourceLayerResolver</c> left the MVT
    /// answer where it was) and <b>T4</b> (the sliced layer's ordinal domain — one slot per feature, and the
    /// slots ADDRESS the buffer).
    ///
    /// <para><b>Why T2 needs a hand-encoded MVT tile.</b> The resolver used to short-circuit an empty
    /// <c>source-layer</c> to null; S2 deleted that and let each tile answer for its own format. Over any
    /// real MVT fixture the replacement guard is INERT — <c>GetLayer("")</c> compares against layer names and
    /// no real layer is named <c>""</c>, so the arm passes with or without it. The discriminating input is a
    /// layer whose <c>name</c> field is ABSENT, which decodes to a <b>null</b> name and which a style layer
    /// with no <c>source-layer</c> (also null) would therefore match.</para>
    /// </summary>
    [TestFixture]
    public class GeoJsonTileDecoderTests
    {
        private static readonly TileId WorldTile = new TileId { Z = 0, X = 0, Y = 0 };
        private static readonly TileId MvtTileId = new TileId { Z = 4, X = 3, Y = 6 };

        // Two disjoint rectangles in tile-local units at the default extent, WEST first. The gap between them
        // is what makes "which ring is filed under which ordinal" answerable from the coordinates alone.
        private const double WestMin = 512.0,  WestMax = 1536.0;
        private const double EastMin = 2560.0, EastMax = 3584.0;

        // The tile the clipping fixture slices, and the two z1 neighbours that hold the features it discards.
        private static readonly TileId SliceTile      = new TileId { Z = 1, X = 0, Y = 0 };
        private static readonly TileId EastNeighbour  = new TileId { Z = 1, X = 1, Y = 0 };
        private static readonly TileId SouthNeighbour = new TileId { Z = 1, X = 1, Y = 1 };

        private const string ClippedEast  = "clipped-east";
        private const string SurvivingWest = "west";
        private const string ClippedSouth  = "clipped-south-east";
        private const string SurvivingEast = "east";

        // ── Fixture builders ──────────────────────────────────────────────────────────────────────────

        /// <summary>An axis-aligned rectangle spanning the given tile-local range of the world tile, authored
        /// in lon/lat (so the test states its input the way a style document would).</summary>
        private static string RectangleAt(double min, double max)
        {
            double2 nw = WorldTile.ToLonLat(min, min, GeoJsonSliceOptions.DefaultExtent);
            double2 se = WorldTile.ToLonLat(max, max, GeoJsonSliceOptions.DefaultExtent);
            // Tile-local Y grows SOUTHWARD, so the small-Y corner carries the NORTH latitude.
            return GeoJsonTestFixtures.Feature(
                "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(nw.x, se.y, se.x, nw.y)}]");
        }

        /// <summary>The same rectangle, in the tile-local units of an arbitrary tile and carrying a
        /// <c>name</c> property. The name is what makes "which features survived" answerable: a decoder that
        /// took a PREFIX of the dataset instead of the slice keeps every count right and every ordinal in
        /// range, and only the identity of the surviving features gives it away.</summary>
        private static string NamedRectangleIn(TileId tile, double min, double max, string name)
        {
            double2 nw = tile.ToLonLat(min, min, GeoJsonSliceOptions.DefaultExtent);
            double2 se = tile.ToLonLat(max, max, GeoJsonSliceOptions.DefaultExtent);
            return GeoJsonTestFixtures.Feature(
                "Polygon", $"[{GeoJsonTestFixtures.RectangleRing(nw.x, se.y, se.x, nw.y)}]",
                properties: $"{{\"name\":\"{name}\"}}");
        }

        /// <summary>The <c>name</c> a fixture feature carries, or a loud placeholder.</summary>
        private static string NameOf(IFeature feature)
            => feature.TryGetProperty("name", out Value name) ? name.AsString() : "<no name>";

        private static GeoJsonTileDecoder DecoderOver(string collectionJson)
            => new GeoJsonTileDecoder(
                GeoJsonProjectedDataset.Project(GeoJsonParser.Parse(collectionJson)),
                GeoJsonSliceOptions.Default);

        /// <summary>The two-rectangle dataset: feature 0 WEST, feature 1 EAST, in authoring order.</summary>
        private static GeoJsonTileDecoder TwoRectangleDecoder()
            => DecoderOver(GeoJsonTestFixtures.Collection(
                RectangleAt(WestMin, WestMax), RectangleAt(EastMin, EastMax)));

        /// <summary>
        /// The CLIPPING fixture: four features, of which the tile sliced (<see cref="SliceTile"/>) keeps only
        /// the second and the fourth. The surviving list is therefore a <b>proper, non-prefix</b> subset of
        /// the dataset's — the shape T4 needs, because a decoder that built <c>Features</c> from the first
        /// <i>n</i> of the DATASET while taking geometry from the slice produces the right counts, the right
        /// ordinal range, and the wrong features.
        ///
        /// <para>The two survivors keep the WEST/EAST layout the addressing tooth argues from; the two
        /// discards live in neighbouring z1 tiles, a full tile away from a window that extends only
        /// 64/4096 of a tile past the edge.</para>
        /// </summary>
        private static GeoJsonTileDecoder ClippingDecoder()
            => DecoderOver(GeoJsonTestFixtures.Collection(
                NamedRectangleIn(EastNeighbour,  WestMin,  WestMax,  ClippedEast),
                NamedRectangleIn(SliceTile,      WestMin,  WestMax,  SurvivingWest),
                NamedRectangleIn(SouthNeighbour, EastMin,  EastMax,  ClippedSouth),
                NamedRectangleIn(SliceTile,      EastMin,  EastMax,  SurvivingEast)));

        private static StyleLayer LayerWithSourceLayer(string sourceLayer)
            => new StyleLayer { Id = "probe", Source = "s", SourceLayer = sourceLayer };

        // ── T2 · a geojson tile has one layer and does not match names ────────────────────────────────

        /// <summary>
        /// <b>T2</b> — the Style Spec makes <c>source-layer</c> "required for vector sources" and unused for
        /// geojson ones, so a geojson tile has nothing to match a name against: absent, empty and bogus all
        /// answer with the sole layer.
        ///
        /// <para>The bogus arm is the one worth stating out loud, because it is the consequence rather than
        /// the requirement: a style naming a <c>source-layer</c> over a geojson source still renders. That is
        /// the spec's answer; match-or-null would give a fixture author a silent-empty failure mode for a key
        /// the spec says is ignored.</para>
        /// </summary>
        [Test]
        public void T2_AGeoJsonTileIgnoresTheSourceLayerNameEntirely()
        {
            using IDecodedTile tile = TwoRectangleDecoder().Decode(WorldTile, null);

            // Fetched by the layer's OWN name deliberately: it is the one argument a name-matching tile
            // would also answer, so each arm below fails on its own claim rather than on this precondition.
            ITileLayer sole = tile.GetLayer(GeoJsonTileLayer.WellKnownName);
            Assert.IsNotNull(sole, "precondition: the fixture must slice to a layer at all");
            Assert.AreEqual(2, sole.Features.Count, "precondition: both authored rectangles fall in the tile");

            Assert.AreSame(sole, tile.GetLayer(null),
                "an ABSENT source-layer must resolve the sole layer. This is the spec-conformant shape for a " +
                "geojson source — the key is unused — and it is the one that used to select nothing.");
            Assert.AreSame(sole, tile.GetLayer(""),
                "…and so must an EMPTY one: a geojson tile has no sub-layers to disambiguate, so there is " +
                "nothing for a name to select between");
            Assert.AreSame(sole, tile.GetLayer("anything-at-all"),
                "…and so must a BOGUS one. The name is not consulted; returning null for an unrecognised " +
                "name would invent a match-or-nothing rule the spec does not have.");
        }

        /// <summary>
        /// <b>T2 arm A</b> — the recorded bug, at the production seam: a spec-conformant geojson style layer
        /// omits <c>source-layer</c>, and <see cref="SourceLayerResolver.ResolveTileLayer"/> must still
        /// resolve it. Before S2 the resolver short-circuited a null/empty <c>source-layer</c> to null, so
        /// such a layer selected zero features and rendered NOTHING, silently.
        /// </summary>
        [Test]
        public void T2A_AStyleLayerWithNoSourceLayer_ResolvesAGeoJsonTilesSoleLayer()
        {
            using IDecodedTile tile = TwoRectangleDecoder().Decode(WorldTile, null);

            Assert.IsNotNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(null), tile),
                "a spec-conformant geojson style layer omits source-layer, and must still resolve. " +
                "Returning null here is the recorded bug: the layer renders NOTHING, silently.");
            Assert.IsNotNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(string.Empty), tile),
                "…and an explicitly empty one is the same claim through the other half of the old " +
                "short-circuit's condition");
        }

        /// <summary>
        /// <b>T2 arm B, the anti-vacuity half.</b> A NAMED <c>source-layer</c> still resolves on an MVT tile.
        /// Without it, its sibling below is satisfied by "MVT resolves nothing, ever" — which would be a
        /// total regression of the vector path passing as a guard working.
        /// </summary>
        [Test]
        public void T2B_ANamedSourceLayer_StillResolvesOnAnMvtTile()
        {
            byte[] bytes = MvtBytes.Tile(MvtBytes.Layer("places", MvtBytes.PointFeature(10, 20)));
            using MvtTile tile = MvtDecoder.Decode(MvtTileId, bytes);

            ITileLayer resolved = SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer("places"), tile);
            Assert.IsNotNull(resolved, "a named source-layer must still resolve on an MVT tile — the whole " +
                                       "vector path depends on it");
            Assert.AreEqual("places", resolved.Name, "…and it must be the layer that was named");
            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer("absent"), tile),
                "…while a name no layer carries still selects nothing");
        }

        /// <summary>
        /// <b>T2 arm B</b> — moving the accommodation into the tile must not turn "no <c>source-layer</c>"
        /// into "the nameless layer" for a background or raster style layer over an MVT source.
        ///
        /// <para><b>The input is the whole tooth.</b> Over an ordinary fixture this claim is INERT: no real
        /// layer is named <c>""</c> or null, so the name loop finds nothing with or without the guard. The
        /// tile here is hand-encoded to hold exactly the two shapes that discriminate — a layer whose
        /// <c>name</c> field is ABSENT (decodes to <c>Name == null</c>, which <c>l.Name == null</c> matches)
        /// and one whose name is present and EMPTY (which <c>l.Name == ""</c> matches).</para>
        /// </summary>
        [Test]
        public void T2B_AStyleLayerWithNoSourceLayer_StillSelectsNothingFromAnMvtTile()
        {
            byte[] bytes = MvtBytes.Tile(
                MvtBytes.NamelessLayer(MvtBytes.PointFeature(10, 20)),   // name field ABSENT ⇒ Name == null
                MvtBytes.Layer("", MvtBytes.PointFeature(30, 40)));      // name present and EMPTY

            using MvtTile tile = MvtDecoder.Decode(MvtTileId, bytes);

            // Precondition: the tile really carries the two shapes. Without this the arms below could pass
            // because the decoder dropped the layers, not because the guard held.
            Assert.AreEqual(2, tile.Layers.Count, "precondition: both hand-encoded layers must decode");
            Assert.IsNull(tile.Layers[0].Name,
                "precondition: an absent `name` field must leave Name NULL — that is the input that " +
                "discriminates, and a decoder defaulting it to \"\" would make this test inert");
            Assert.AreEqual(string.Empty, tile.Layers[1].Name, "precondition: …and the empty-named sibling");

            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(null), tile),
                "an MVT tile must answer NULL for an absent source-layer. The resolver no longer " +
                "short-circuits, so this is now the TILE's claim — moving it must not turn 'no " +
                "source-layer' into 'the nameless layer' for a background or raster style layer.");
            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(LayerWithSourceLayer(string.Empty), tile),
                "…and the same through the empty-name half of the old condition");
        }

        // ── T4 · the ordinal domain of a sliced layer ─────────────────────────────────────────────────

        /// <summary>
        /// <b>T4</b> — over a tile that CLIPS features away, the layer lists exactly the SURVIVORS, and its
        /// ordinal domain is positions in that list.
        ///
        /// <para><b>The fixture is the load-bearing part.</b> Two of the dataset's four features fall in
        /// neighbouring tiles, so the layer's feature list is a proper, <b>non-prefix</b> subset of the
        /// dataset's. That is what discriminates the realistic defect — <c>Features</c> built from the
        /// dataset while <c>Geometry</c> comes from the slice — in the form the adoption guard cannot see:
        /// taking the first <i>n</i> of the dataset keeps <c>FeatureCount == Features.Count</c>, keeps every
        /// ordinal in range, and files every per-feature bake against the wrong feature. The named-survivor
        /// arm is the only one here that can fail on it.</para>
        ///
        /// <para><b>What the other two arms are, honestly.</b> The count arm cannot fail:
        /// <c>TileLayerGeometryAdoption.Validate</c> throws on exactly this mismatch inside
        /// <c>AdoptGeometry</c>, i.e. during <c>Decode</c>, so a violating decode never returns a layer to
        /// assert against — measured, not reasoned. The ordinal-range arm cannot fail either:
        /// <c>FeatureSelector</c> assigns <c>Ordinal = i</c> over <c>layer.Features</c>, and the guard has
        /// already pinned that count. Both are kept as executable statements of the domain, but the claim
        /// they look like they are pinning lives in the guard, whose own teeth are
        /// <c>WaistOneProducerAgreementTests</c> — that is where a RED for it belongs, not here.</para>
        /// </summary>
        [Test]
        public void T4_ASlicedGeoJsonLayer_ListsOnlyTheSurvivingFeatures_AndItsOrdinalsAddressTheBuffer()
        {
            GeoJsonTileDecoder decoder = ClippingDecoder();
            using IDecodedTile tile = decoder.Decode(SliceTile, null);
            ITileLayer layer = tile.GetLayer(GeoJsonTileLayer.WellKnownName);
            Assert.IsNotNull(layer, "precondition: the fixture slices to a layer");
            Assert.AreEqual(2, layer.Features.Count,
                "precondition: exactly two of the four authored features fall in this tile — the list must " +
                "be a PROPER subset, or nothing below can tell a slice from a dataset");

            // The whole point of the fixture: the survivors are the dataset's features 1 and 3, so a decoder
            // taking a PREFIX of the dataset would produce the same count and different features.
            Assert.AreEqual(SurvivingWest, NameOf(layer.Features[0]),
                "the layer's feature 0 must be the WESTERN survivor. The dataset's feature 0 is clipped " +
                "away, so a Features list built from the dataset (or from its first n) puts a feature this " +
                "tile does not contain at ordinal 0 — every per-feature bake then lands on a neighbour, " +
                "with the counts and the ordinal range still perfectly in order.");
            Assert.AreEqual(SurvivingEast, NameOf(layer.Features[1]),
                "…and feature 1 the EASTERN survivor. Asserting only the first would accept a list that " +
                "starts right and then drifts — which is exactly what a prefix of the dataset does here.");

            TileGeometryBuffers geometry = layer.Geometry;
            Assert.IsTrue(geometry.IsCreated, "precondition: the decode really materialized a buffer");
            Assert.AreEqual(2, geometry.RingCount, "precondition: one exterior ring per surviving rectangle");
            Assert.AreEqual(layer.Features.Count, geometry.FeatureCount,
                "the per-feature column must have exactly one slot per feature of the layer. STRUCTURALLY " +
                "PINNED: the adoption guard throws on this mismatch during Decode, so this line states the " +
                "domain rather than testing it — see WaistOneProducerAgreementTests for the guard's teeth.");

            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(LayerWithSourceLayer(null), layer, zoom: 0.0, selected);
            Assert.AreEqual(2, selected.Count, "precondition: a filter-less style layer selects every feature");

            foreach (SelectedTileFeature s in selected)
            {
                Assert.GreaterOrEqual(s.Ordinal, 0,
                    $"ordinal {s.Ordinal} must be a position in Features, and a negative one indexes " +
                    "nothing — stated for the same reason as the upper bound below, and equally structural");
                Assert.Less(s.Ordinal, geometry.FeatureCount,
                    $"ordinal {s.Ordinal} must address the buffer this layer owns (FeatureCount " +
                    $"{geometry.FeatureCount}). STRUCTURALLY PINNED as well: FeatureSelector assigns " +
                    "Ordinal = i over the same list the guard sized the column against. The claim that " +
                    "actually discriminates is the named-survivor pair above, and WHICH ring sits under " +
                    "which ordinal is the sibling test.");
            }
        }

        /// <summary>
        /// <b>T4</b> — an empty slice yields a tile with <b>zero</b> layers, not a layer with zero features.
        ///
        /// <para><b>This is the tooth that observes the DECODER.</b> Its sibling in <c>GeoJsonSourceTests</c>
        /// (<c>T1_AnEmptyInlineDataset_RendersNothing</c>) is satisfied upstream, by the source's emptiness
        /// probe, and stays GREEN against a decoder injected to emit a full-extent quad whenever its slice is
        /// empty — measured. This one reds on it. Do not delete it as a duplicate of that arm.</para>
        /// </summary>
        [Test]
        public void T4_AnEmptySlice_YieldsATileWithNoLayerAtAll()
        {
            // A dataset that exists but falls entirely outside the tile asked for: the slice is empty for
            // this tile while the decoder still has real work to reject, which "no features at all" would not
            // exercise.
            GeoJsonTileDecoder decoder = TwoRectangleDecoder();
            var farAway = new TileId { Z = 4, X = 15, Y = 15 };

            using IDecodedTile tile = decoder.Decode(farAway, null);

            Assert.IsNull(tile.GetLayer(null),
                "an empty slice must yield ZERO layers. A layer holding zero features would still be handed " +
                "to every consumer and would make FeatureCount == Features.Count true vacuously — and, more " +
                "to the point, 'my sole layer' must actually exist to be returned.");
            Assert.IsNull(tile.GetLayer(GeoJsonTileLayer.WellKnownName), "…by any name, since none is matched");
        }

        /// <summary>
        /// <b>T4</b> — the addressing claim, not the counting one: each ring must be filed under the ordinal
        /// of the feature that produced it.
        ///
        /// <para>Counts agreeing is cheap; a producer that permuted the path column keeps every count right
        /// and shifts every per-feature bake onto a neighbour. The fixture makes that visible with a
        /// monotone-X argument: the SURVIVING feature 0 is authored WEST of the surviving feature 1 and the
        /// two are disjoint, so the ring under ordinal 1 must lie strictly east of the ring under ordinal 0.
        /// It runs over the clipping fixture, where the survivors are the dataset's features 1 and 3 — the
        /// argument is unchanged and the input is strictly stronger.</para>
        /// </summary>
        [Test]
        public void T4_EveryRingIsFiledUnderItsOwnFeaturesOrdinal()
        {
            using IDecodedTile tile = ClippingDecoder().Decode(SliceTile, null);
            ITileLayer layer = tile.GetLayer(GeoJsonTileLayer.WellKnownName);
            Assert.IsNotNull(layer, "precondition: the fixture slices to a layer");

            // Over the CLIPPING fixture, so the monotone-X argument runs against a proper, non-prefix subset
            // of the dataset — a strictly stronger input for it, and the layout it needs is unchanged: the
            // two survivors are still one western and one eastern rectangle, disjoint, in that order.
            Assert.AreEqual(SurvivingWest, NameOf(layer.Features[0]),
                "precondition: ordinal 0 is the western survivor…");
            Assert.AreEqual(SurvivingEast, NameOf(layer.Features[1]),
                "precondition: …and ordinal 1 the eastern one, which is what makes an out-of-order X below " +
                "a filing error rather than a fixture that authored them the other way round");

            TileGeometryBuffers geometry = layer.Geometry;
            Assert.AreEqual(2, geometry.RingCount, "precondition: exactly one ring per surviving feature");
            Assert.AreEqual(2, geometry.FeatureCount, "precondition: …over two features");

            double westernmostOfOrdinal0 = MinRingX(geometry, ordinal: 0);
            double westernmostOfOrdinal1 = MinRingX(geometry, ordinal: 1);

            // Derived from the fixture, not from a run: the authored gap between the two rectangles.
            Assert.Greater(westernmostOfOrdinal1, WestMax,
                "the ring under ordinal 1 must be east of the one under ordinal 0, because feature 1 is the " +
                "EASTERN rectangle and the two are disjoint. A permuted path column shows up here as an " +
                "out-of-order X while every count stays correct.");
            Assert.Less(westernmostOfOrdinal0, EastMin,
                "…and symmetrically, the ring under ordinal 0 must be the western one — asserting only one " +
                "side would accept a producer that filed both rings under the same feature");
        }

        /// <summary>The smallest tile-local X over every ring filed under <paramref name="ordinal"/>. Fails
        /// loudly when no ring is: a producer that renumbered ring→feature densely over the features that
        /// happen to carry rings leaves the counts right and the addressing one slot out.</summary>
        private static double MinRingX(TileGeometryBuffers geometry, int ordinal)
        {
            double min = double.PositiveInfinity;
            for (int r = 0; r < geometry.RingCount; r++)
            {
                if (geometry.RingFeatureIdx[r] != ordinal) continue;
                for (int v = geometry.RingOffsets[r]; v < geometry.RingOffsets[r + 1]; v++)
                    min = math.min(min, geometry.Vertices[v].x);
            }

            Assert.IsFalse(double.IsPositiveInfinity(min),
                $"no ring is filed under ordinal {ordinal}. RingFeatureIdx names the feature's position in " +
                "ITileLayer.Features, so a ring-bearing feature always has one.");
            return min;
        }
    }
}
