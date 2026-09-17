// Unity EditMode only — real MvtDecoder output (Allocator.Persistent buffers) plus the Unity-side
// TileMeshLayerProcessor. NOT registered in core-tests.csproj.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR C1 — tooth <b>B</b>'s missing half: the <b>selection</b>/buffer relation, where B closed only the
    /// <b>TileId</b>/buffer one (P2 recorded finding 3).
    ///
    /// <para><b>The hazard as recorded.</b> Three consumers size a per-feature array and index it by
    /// <see cref="SelectedTileFeature.Ordinal"/> — fill and line by <c>geometry.FeatureCount</c>, symbol by
    /// <c>tileLayer.Features.Count</c> — and nothing checks <c>max(Ordinal) &lt; FeatureCount</c>. Two
    /// different notions of "how many features", addressed by one ordinal.</para>
    ///
    /// <para><b>They are the same number, and this fixture pins WHY rather than that they happen to match.</b>
    /// Production has exactly one <see cref="ITileLayer"/> — <see cref="MvtLayer"/> — and
    /// <c>MvtDecoder.DecodeLayer</c> appends to <c>layer.Features</c> and to the command list it hands
    /// <see cref="MvtGeometryMaterializer"/> in the <i>same</i> switch arm, once per feature message. The
    /// materializer then sizes <c>FeatureGeometryType</c> to that command count. The lockstep is the
    /// invariant; the bound is only its consequence, which is why a bound check would be the wrong
    /// instrument — a selection borrowed from a SHORTER sibling layer yields in-range ordinals and would sail
    /// straight through one while mis-attributing every colour.</para>
    ///
    /// <para><b>The load-bearing case is the feature that contributes NO rings</b> (clause A): it is the only
    /// input shape under which "one slot per feature" and "one slot per feature that has geometry" differ, so
    /// a decoder that ever compacted such features out would break the ordinal domain and nothing over the
    /// committed corpus need notice — measured, not assumed (see clause A). Clause B is the same invariant
    /// over real multi-layer fixtures; clause C is the pairing itself, at the mesh-path consumer.</para>
    /// </summary>
    [TestFixture]
    public class OrdinalDomainTests
    {
        private static readonly TileId Tile = new TileId { Z = 4, X = 3, Y = 6 };

        // ── A — a feature that contributes no rings still OWNS its ordinal ─────────────────────────────

        /// <summary>
        /// A hand-encoded MVT layer of four features, the first TWO of which produce no rings, decoded by the
        /// real <see cref="MvtDecoder"/>.
        ///
        /// <para><b>Two ring-less shapes, deliberately, because they are not equally defensible.</b>
        /// Ordinal 1 carries a <c>geometry</c> field that is <b>present and empty</b> — unambiguously
        /// spec-conformant (MVT 2.1 §4.2 requires the field; it says nothing about a minimum length), and it
        /// reaches the materializer as <c>new uint[0]</c>. Ordinal 0 <b>omits the field entirely</b>, which
        /// the spec does <i>not</i> sanction: it is input this decoder <i>accepts</i> (<c>DecodeFeature</c>
        /// returns a <c>null</c> stream for it) rather than input it is required to handle, and it reaches
        /// the materializer as <c>null</c>. Both are here because the two travel different paths through
        /// <c>MvtGeometryMaterializer</c>'s <c>?.Length ?? 0</c> arithmetic, so a compaction keyed on one is
        /// invisible to the other — see the RED note below. The load-bearing claim rests on the
        /// <b>spec-conformant</b> one; the out-of-spec one only pins that a decoder must not compact away
        /// what it already chose to accept.</para>
        ///
        /// <para><b>Production configuration:</b> the bytes are synthetic, everything that reads them is not
        /// — real decoder, real <see cref="MvtGeometryMaterializer"/>, real <see cref="MvtLayer"/>, real
        /// <see cref="FeatureSelector"/>. Neither ring-less shape appears in any committed fixture, which is
        /// not an accident of the corpus but the reason this clause has to hand-encode its own input: RED row
        /// R2 compacted every ring-less feature out of the decoder and clause B, over two real multi-layer
        /// tiles, stayed GREEN.</para>
        ///
        /// <para><b>Catches</b> any future "skip the features with nothing to draw" compaction in the decoder
        /// or the materializer: it would leave <c>FeatureCount</c> short of <c>Features.Count</c> and — worse,
        /// because it is silent — slide every later feature's rings onto a lower ordinal, so a style layer's
        /// baked colours and widths would land on its neighbours' geometry.</para>
        /// </summary>
        [Test]
        public void AFeatureWithNoGeometry_StillOccupiesItsOrdinalSlot()
        {
            byte[] bytes = MvtBytes.Tile(MvtBytes.Layer("places",
                MvtBytes.AttributeOnlyPointFeature(),           // ordinal 0 — geometry field ABSENT  ⇒ null
                MvtBytes.EmptyGeometryPointFeature(),           // ordinal 1 — geometry field EMPTY   ⇒ uint[0]
                MvtBytes.PointFeature(tileX: 10, tileY: 20),    // ordinal 2
                MvtBytes.PointFeature(tileX: 30, tileY: 40)));  // ordinal 3

            using MvtTile tile = MvtDecoder.Decode(Tile, bytes);
            MvtLayer layer = tile.GetLayer("places");
            Assert.IsNotNull(layer, "precondition: the hand-encoded layer must decode at all");

            // Precondition — the decoder kept both ring-less features as FEATURES. If it dropped them here
            // the two counts would agree at 2 and the clause below would be vacuous.
            Assert.AreEqual(4, layer.Features.Count,
                "precondition: all four features must survive the decode, including the two that carry no " +
                "rings — each is still a filterable, expression-evaluable record");

            TileGeometryBuffers geometry = layer.Geometry;
            Assert.AreEqual(4, geometry.FeatureCount,
                "the ordinal domain must span EVERY feature of the layer, not just the ones that produced " +
                "rings. `Ordinal` counts positions in ITileLayer.Features; FeatureGeometryType is the column " +
                "that count is joined against, and fill/line size their per-feature arrays by its length.");
            Assert.AreEqual(2, geometry.RingCount,
                "precondition: exactly the two point features produced a ring — a third would mean one of " +
                "the ring-less features is contributing geometry after all");

            // …and the ordinals are not merely COUNTED right, they ADDRESS right: the last feature's ring
            // must be filed under ordinal 3, not under the 1 a compacting decoder would give it.
            double2 atOrdinal3 = FirstVertexOfFeature(geometry, ordinal: 3);
            Assert.AreEqual(30.0, atOrdinal3.x, 1e-9,
                "the ring of the feature at ordinal 3 must be filed under RingFeatureIdx == 3. A decoder " +
                "that compacted the ring-less features away would file it under 1 — the counts would " +
                "still look plausible and every feature's paint would silently shift two neighbours over.");
            Assert.AreEqual(40.0, atOrdinal3.y, 1e-9, "…and its y");

            double2 atOrdinal2 = FirstVertexOfFeature(geometry, ordinal: 2);
            Assert.AreEqual(10.0, atOrdinal2.x, 1e-9, "…and its neighbour under ordinal 2");
            Assert.AreEqual(20.0, atOrdinal2.y, 1e-9, "…and its y");

            Assert.IsFalse(HasAnyRing(geometry, ordinal: 0),
                "…and the absent-geometry feature owns a slot with no rings in it — a slot is not a ring");
            Assert.IsFalse(HasAnyRing(geometry, ordinal: 1),
                "…nor does the empty-geometry one, which is the SPEC-CONFORMANT half of the same claim");

            // The selection walks the same list the ordinals index, so the bound the recorded finding asked
            // about falls out. Asserted through the real selector, not by arithmetic on the counts above.
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(MatchAllLayer("places"), layer, zoom: 4.0, selected);
            Assert.AreEqual(4, selected.Count, "precondition: a filter-less style layer selects every feature");
            AssertOrdinalsAddressTheBuffer(selected, geometry, "places");
        }

        // ── B — the invariant over the real corpus ─────────────────────────────────────────────────────

        /// <summary>
        /// <c>Geometry.FeatureCount == Features.Count</c> for <b>every</b> layer of two real multi-layer
        /// fixtures — unconditionally, with no "or the buffer is uncreated" escape clause, because the one
        /// early-out in <see cref="MvtGeometryMaterializer"/> (<c>featureCount == 0 ⇒ default</c>) fires
        /// exactly when the feature list is empty too, and <c>default.FeatureCount</c> is 0.
        ///
        /// <para>Clause A proves the rule on the input shape that discriminates; this proves the decoder
        /// applies it across a real corpus — dozens of layers, mixed kinds, real extents — rather than only
        /// on a three-feature tile a test wrote.</para>
        /// </summary>
        [Test]
        public void EveryRealDecodedLayer_HasOneOrdinalSlotPerFeature()
        {
            var addresses = new (TileId id, string fixture)[]
            {
                (new TileId { Z = 6, X = 32,  Y = 20  }, "water-6-32-20.pbf.bytes"),
                (new TileId { Z = 9, X = 274, Y = 168 }, "boundary-9-274-168.pbf.bytes"),
            };

            int checkedLayers = 0;
            int checkedNonEmpty = 0;
            foreach ((TileId id, string fixture) in addresses)
            {
                using MvtTile tile = MvtDecoder.Decode(id, Fixture(fixture));
                foreach (MvtLayer layer in tile.Layers)
                {
                    checkedLayers++;
                    Assert.AreEqual(layer.Features.Count, layer.Geometry.FeatureCount,
                        $"{fixture}/{layer.Name}: the buffer's per-feature column must have exactly one slot " +
                        "per feature of the layer. These are the two counts SelectedTileFeature.Ordinal is " +
                        "used against — symbol sizes by the left one, fill and line by the right one — so " +
                        "the moment they disagree the same ordinal means two different things.");

                    if (layer.Features.Count == 0) continue;
                    checkedNonEmpty++;

                    var selected = new List<SelectedTileFeature>();
                    FeatureSelector.SelectFeatures(MatchAllLayer(layer.Name), layer, id.Z, selected);
                    AssertOrdinalsAddressTheBuffer(selected, layer.Geometry, $"{fixture}/{layer.Name}");
                }
            }

            // Non-vacuity: real multi-layer tiles were really walked, not an empty layer list.
            Assert.Greater(checkedLayers, 4,
                "precondition: at least five layers across the two fixtures, or this loop asserted nothing");
            Assert.Greater(checkedNonEmpty, 3,
                "precondition: …and at least four of them carry features, so the selector arm ran");
        }

        // ── C — the pairing, at the production consumer ────────────────────────────────────────────────

        /// <summary>
        /// The buffer a mesh layer is handed belongs to the <b>same</b> source-layer its ordinals came from.
        ///
        /// <para><b>Why a two-layer fixture with DIFFERENT feature counts.</b> The mispairing this closes is
        /// "layer A's selection meets layer B's buffer", and it is invisible on a tile whose layers are the
        /// same length: the ordinals stay in range and only the attribution is wrong. Here "roads" has two
        /// features and "places" has five, so a buffer from the wrong layer is a different
        /// <c>FeatureCount</c> — a number the tooth can read.</para>
        ///
        /// <para><b>Production configuration:</b> the real <see cref="TileMeshLayerProcessor"/>, which is the
        /// only production caller of <c>ITileMeshRenderLayer.BuildGraphRequest</c> and therefore the one site
        /// <i>on the mesh path</i> where the selection and the buffer are chosen. It resolves the
        /// source-layer once and takes both off that single local; this tooth is what makes that structural,
        /// rather than a comment.</para>
        ///
        /// <para><b>Scope, stated so it is not over-read:</b> the mesh path is not the only production site
        /// that chooses both. <c>SymbolFeatureExtractor.Extract</c> does too, and it is the consumer that
        /// sizes by <c>Features.Count</c> rather than <c>FeatureCount</c>. Its pairing is the same
        /// resolve-once shape and is equally safe today, but it is <b>not covered here</b> — a clause over
        /// the extractor would be a near-copy of this one. Its distinctive hazard (the two counts
        /// disagreeing) IS covered, by clause B.</para>
        /// </summary>
        [Test]
        public void TheMeshProcessor_PairsASelectionWithItsOwnLayersBuffer()
        {
            byte[] bytes = MvtBytes.Tile(
                MvtBytes.Layer("roads",
                    MvtBytes.PointFeature(11, 12),
                    MvtBytes.PointFeature(13, 14)),
                MvtBytes.Layer("places",
                    MvtBytes.PointFeature(21, 22),
                    MvtBytes.PointFeature(23, 24),
                    MvtBytes.PointFeature(25, 26),
                    MvtBytes.PointFeature(27, 28),
                    MvtBytes.PointFeature(29, 30)));

            using MvtTile tile = MvtDecoder.Decode(Tile, bytes);
            Assert.AreEqual(2, tile.GetLayer("roads").Features.Count, "precondition: the two layers must differ");
            Assert.AreEqual(5, tile.GetLayer("places").Features.Count, "…in feature count, or C cannot discriminate");

            var renderLayer = new RecordingTileMeshRenderLayer(
                new StyleLayer { Id = "roads-line", Source = "s", SourceLayer = "roads" });
            var context = new TileLayerProcessContext
            {
                Tile             = Tile,
                Zoom             = 4.0,
                TileOriginRender = double3.zero,
                Projection       = new WebMercatorProjection(),
            };

            TileMeshLayerProcessor processor = TileMeshLayerProcessor.AllocateForKick(renderLayer, materialIndex: 0);
            try
            {
                processor.ProcessOnWorker(tile, in context);

                Assert.AreEqual(1, renderLayer.BuildGraphRequestCallCount,
                    "precondition: BuildGraphRequest must have been reached — an unreached layer records " +
                    "nothing and every assertion below would be comparing defaults");
                Assert.AreEqual(2, renderLayer.ObservedFeatureCount,
                    "the buffer handed to BuildGraphRequest must be the ROADS layer's (2 features), not the " +
                    "places layer's (5). The ordinals in the selection index the source-layer the style layer " +
                    "names; a buffer from any other layer makes every per-feature array a mis-attribution — " +
                    "and, when the other layer is the shorter one, an in-range and therefore silent one.");
                AssertOrdinalsAddressTheBuffer(
                    renderLayer.ObservedSelection, renderLayer.ObservedGeometry, "roads via TileMeshLayerProcessor");
            }
            finally
            {
                processor.Release();
            }
        }

        // ── Shared assertion ──────────────────────────────────────────────────────────────────────────

        private static void AssertOrdinalsAddressTheBuffer(
            IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry, string where)
        {
            Assert.Greater(selected.Count, 0, $"{where}: precondition — an empty selection asserts nothing");

            int maxOrdinal = -1;
            for (int i = 0; i < selected.Count; i++)
                if (selected[i].Ordinal > maxOrdinal) maxOrdinal = selected[i].Ordinal;

            Assert.Less(maxOrdinal, geometry.FeatureCount,
                $"{where}: every selected ordinal must be a valid index into the buffer it is used against. " +
                $"Highest ordinal {maxOrdinal}, buffer FeatureCount {geometry.FeatureCount}. Fill and line " +
                "write `featureColors[selected.Ordinal]` into an array of exactly that length.");
        }

        // ── Buffer readers ────────────────────────────────────────────────────────────────────────────

        private static double2 FirstVertexOfFeature(TileGeometryBuffers geometry, int ordinal)
        {
            for (int r = 0; r < geometry.RingCount; r++)
                if (geometry.RingFeatureIdx[r] == ordinal)
                    return geometry.Vertices[geometry.RingOffsets[r]];

            Assert.Fail(
                $"no ring is filed under ordinal {ordinal}. RingFeatureIdx must name the feature's position " +
                "in ITileLayer.Features, so a ring-bearing feature always has one. A producer that " +
                "renumbered ring→feature densely over the features that happen to carry rings would leave " +
                "the count right and the addressing one slot out — every per-feature bake landing on a " +
                $"neighbour. RingFeatureIdx over {geometry.RingCount} rings: {RingFeatureIdxOf(geometry)}");
            return default;
        }

        private static string RingFeatureIdxOf(TileGeometryBuffers geometry)
        {
            var values = new List<int>(geometry.RingCount);
            for (int r = 0; r < geometry.RingCount; r++) values.Add(geometry.RingFeatureIdx[r]);
            return string.Join(", ", values);
        }

        private static bool HasAnyRing(TileGeometryBuffers geometry, int ordinal)
        {
            for (int r = 0; r < geometry.RingCount; r++)
                if (geometry.RingFeatureIdx[r] == ordinal) return true;
            return false;
        }

        private static StyleLayer MatchAllLayer(string sourceLayer) =>
            new StyleLayer { Id = $"select-all-{sourceLayer}", Source = "s", SourceLayer = sourceLayer };

        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        // ── Test doubles ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Captures what the processor actually handed <c>BuildGraphRequest</c> — the selection and
        /// the buffer, the two things clause C exists to compare — and builds no request (§3.5-analogous:
        /// <c>selected</c>/<c>geometry</c> are BuildGraphRequest parameters too, so recording them needs no
        /// new production observability).</summary>
        private sealed class RecordingTileMeshRenderLayer : ITileMeshRenderLayer
        {
            public int                       BuildGraphRequestCallCount { get; private set; }
            public IReadOnlyList<SelectedTileFeature> ObservedSelection { get; private set; }
            public TileGeometryBuffers       ObservedGeometry     { get; private set; }
            public int                       ObservedFeatureCount => ObservedGeometry.FeatureCount;

            public RecordingTileMeshRenderLayer(StyleLayer styleLayer) => StyleLayer = styleLayer;

            public StyleLayer       StyleLayer      { get; }
            public RenderLayerBuild Build           => RenderLayerBuild.TileMesh;
            public DrawPersistence  Persistence     => DrawPersistence.Persistent;
            public int              DrawIndex       => 0;
            public LayerSubSlot     MaterialSubSlot => LayerSubSlot.Base;
            public UnityEngine.Rendering.ShadowCastingMode CastShadows => UnityEngine.Rendering.ShadowCastingMode.Off;
            public Material         Material        => null;
            public void ApplyZoom(in StyleFrameInputs inputs) { }
            public int TransitioningCount => 0;
            public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
            public void SetDrawOrder(int declaredOrder) { }
            public void Dispose() { }

            public ILayerMeshBuild BuildGraphRequest(
                IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                in TileLayerProcessContext context, int materialIndex, string payloadName)
            {
                BuildGraphRequestCallCount++;
                ObservedSelection = selected;
                ObservedGeometry  = geometry;
                return null;
            }
        }
    }
}
