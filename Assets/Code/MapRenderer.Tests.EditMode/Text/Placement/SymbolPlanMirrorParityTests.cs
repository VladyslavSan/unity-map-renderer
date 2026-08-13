// Unity EditMode only — real Camera/Material + Unity.Collections. NOT registered in core-tests.csproj.
//
// TestSymbolPlan is what the migrated label render fixtures depend on, so what it builds must agree
// field-for-field with the ORACLE — SymbolLabelBatchBuilder over the same flat list, the reference the
// block baker was proven against (they share BuildPointInput/BuildCurvedInput, so they cannot drift).
//
// Why compare at the native mirror and not at rendered pixels: a divergence introduced by the
// store/bake/collect layer — a re-derived anchor, a reordered collect, a dedup that ate a label — shows up
// here as a NAMED field difference. The same divergence in a snapshot shows up as "some pixels moved",
// which is not separable from a harness bug.
//
// SymbolGatherParityTests pins this equality for the SUBSYSTEM's plan; this pins it for the test helper's.
//
// ── FINDING (2026-07-26): the collect reorders records relative to the oracle, by design ────────────────
// The first run of this probe reported "Kinds[0] Point vs Curved" for a [point, curved, point] input. Not a
// helper artifact: SymbolLabelReconciler.Run — which the production collect goes through and a flat-list
// build does not — emits CURVED labels during the tile scan and POINT winners afterwards, in _dedup
// first-insertion order (two segments; see its "Byte-identical" doc para). The oracle walks the list in
// order.
//
// So no mixed-kind probe can match on order. Rather than tune it away, the divergence is pinned by name
// (MixedKinds_ProductionEmitsCurvedBeforePoints), and per-record CONTENT parity — the part that was
// actually open — is pinned on single-kind sets, where the two orders coincide and a field diff therefore
// means a real content difference rather than a permutation.
//
// This is also what bounded the fixture migration: single-label and all-Point fixtures provably cannot be
// affected, so every one of the seven was checkable in advance instead of by trial.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolPlanMirrorParityTests
    {
        private static readonly WebMercatorProjection P = new WebMercatorProjection();

        private static TextLayoutResult OneQuad(float u) => new TextLayoutResult
        {
            Quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1,
        };

        private static LabelInstance PointLabel(double3 anchor, string text, int feature, long tileKey, float u)
            => new LabelInstance
            {
                AnchorRender = anchor, Layout = OneQuad(u), Paint = LabelPaint.Default,
                TextSizePx = 32f, SortKey = 0f, FeatureIndex = feature, TileKey = tileKey, Text = text,
            };

        private static LabelInstance CurvedLabel(double3 anchor, string text, int feature, long tileKey)
            => new LabelInstance
            {
                AnchorRender = anchor, Layout = OneQuad(0.3f), Paint = LabelPaint.Default,
                TextSizePx = 32f, SortKey = 0f, FeatureIndex = feature, TileKey = tileKey, Text = text,
                Placement = SymbolPlacement.Line,
                LineAnchors = new[] { new LineAnchor(0, 0.5f) },
                CurvedGlyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = OneQuad(0.3f).Quads[0] } },
            };

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12, Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static long KeyA(out TileId tile)
        {
            tile = TestTileKeys.Containing(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 }, zoom: 12);
            return LabelTileKey.Pack(tile);
        }

        // Two tiles, so at least one block bakes a nonzero AnchorLocal, and two labels in the first tile, so
        // intra-block local indices are exercised rather than always being 0.
        private static List<LabelInstance> AllPointLabels(double3 origin)
        {
            long keyA = KeyA(out TileId tileA);
            long keyB = LabelTileKey.Pack(
                new TileId { X = tileA.X + 1, Y = tileA.Y, Z = tileA.Z });

            return new List<LabelInstance>
            {
                PointLabel(origin + new double3(0, 0, 0), "a", 1, keyA, 0.10f),
                PointLabel(origin + new double3(120, 0, 0), "b", 2, keyA, 0.15f),
                PointLabel(origin + new double3(0, 0, 240), "c", 3, keyB, 0.20f),
            };
        }

        private static List<LabelInstance> AllCurvedLabels(double3 origin)
        {
            long keyA = KeyA(out TileId tileA);
            long keyB = LabelTileKey.Pack(
                new TileId { X = tileA.X + 1, Y = tileA.Y, Z = tileA.Z });

            return new List<LabelInstance>
            {
                CurvedLabel(origin + new double3(0, 0, 0), "a", 1, keyA),
                CurvedLabel(origin + new double3(120, 0, 0), "b", 2, keyA),
                CurvedLabel(origin + new double3(0, 0, 240), "c", 3, keyB),
            };
        }

        private static List<LabelInstance> MixedLabels(double3 origin)
        {
            long keyA = KeyA(out TileId tileA);
            long keyB = LabelTileKey.Pack(
                new TileId { X = tileA.X + 1, Y = tileA.Y, Z = tileA.Z });

            return new List<LabelInstance>
            {
                PointLabel(origin + new double3(0, 0, 0), "a", 1, keyA, 0.10f),
                CurvedLabel(origin + new double3(120, 0, 0), "c", 2, keyA),
                PointLabel(origin + new double3(0, 0, 240), "b", 3, keyB, 0.15f),
            };
        }

        // Owns the throwaway Unity resources plus one system per path. Deliberately NOT shared with the render
        // fixtures — each of those keeps its own camera/RT/atlas, since that is what its baseline was captured
        // against.
        private sealed class TwoPathHarness : System.IDisposable
        {
            public readonly LabelPlacementSystem Plan;
            public readonly SceneFrame Frame;
            public readonly double3 Origin;
            public readonly GlyphAtlasTexture Atlas;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public TwoPathHarness()
            {
                _camGo = new GameObject("PlanParity_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(256, 256, 0);
                uCam.targetTexture = _rt;
                var lookAt = new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt, zoom: 12.0, heading: 0.0, tilt: 0.0),
                    projection: P);
                Origin = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Plan = new LabelPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Plan.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_baseMaterial);
                Object.DestroyImmediate(_camGo);
                Object.DestroyImmediate(_rt);
            }
        }

        // Builds the ORACLE (SymbolLabelBatchBuilder over the flat list, the reference the block baker was
        // proven against — it shares BuildPointInput/BuildCurvedInput with the baker so the two cannot drift)
        // and, separately, the plan-filled native mirror. Step 5b: the demo Tick overload is gone, so the
        // oracle is taken straight from the builder rather than by ticking a second system — the retired
        // overload only ever copied that same batch into the mirror verbatim, so this is the same reference,
        // one hop shorter. Same shape as SymbolGatherParityTests.
        private static void OracleAndPlanMirror(TwoPathHarness h, List<LabelInstance> labels,
            out SymbolLabelBatch oracle, out SymbolLabelBatch planMirror)
        {
            oracle = new SymbolLabelBatch();
            SymbolLabelBatchBuilder.Build(oracle, labels, 1, P);

            planMirror = new SymbolLabelBatch();
            using var plan = new TestSymbolPlan(P);
            SymbolGatherPlan built = plan.Build(labels);
            Assert.AreEqual(labels.Count, plan.CollectedCount,
                "precondition: the store's collect must yield every input label — a short count means the " +
                "helper's grouping or the cross-tile dedup dropped one, which would make any mirror diff " +
                "below unreadable.");

            h.Plan.Tick(in h.Frame, built, h.Atlas);
            h.Plan.CopyMirrorInto(planMirror);
        }

        private static LabelRecordKind[] Kinds(SymbolLabelBatch mirror)
        {
            var kinds = new LabelRecordKind[mirror.Count];
            System.Array.Copy(mirror.Kinds, kinds, mirror.Count);
            return kinds;
        }

        // ── Content parity, on single-kind sets where the two orders coincide ────────────────────────────

        [Test]
        public void AllPointLabels_PlanMirror_IsFieldIdenticalToDemoMirror()
        {
            using var h = new TwoPathHarness();
            List<LabelInstance> labels = AllPointLabels(h.Origin);
            OracleAndPlanMirror(h, labels, out SymbolLabelBatch demo, out SymbolLabelBatch plan);

            Assert.AreEqual(labels.Count, demo.Count, "precondition: the demo mirror holds every label.");
            Assert.AreEqual(labels.Count, demo.PointCount, "precondition: every record is a point record.");

            string diff = SymbolLabelBatchDiff.FirstDifference(demo, plan);
            Assert.IsNull(diff,
                "over an all-point set the production collect emits in input order, so order is not a confound " +
                $"here and any field difference is a real content difference — first difference: {diff}");
        }

        [Test]
        public void AllCurvedLabels_PlanMirror_IsFieldIdenticalToDemoMirror()
        {
            using var h = new TwoPathHarness();
            List<LabelInstance> labels = AllCurvedLabels(h.Origin);
            OracleAndPlanMirror(h, labels, out SymbolLabelBatch demo, out SymbolLabelBatch plan);

            Assert.AreEqual(labels.Count, demo.Count, "precondition: the demo mirror holds every label.");
            Assert.AreEqual(labels.Count, demo.CurvedCount, "precondition: every record is a curved record.");

            string diff = SymbolLabelBatchDiff.FirstDifference(demo, plan);
            Assert.IsNull(diff,
                "curved labels emit during the tile scan on BOTH paths, so order is not a confound here and any " +
                $"field difference is a real content difference — first difference: {diff}");
        }

        // ── The divergence itself, pinned by name ────────────────────────────────────────────────────────

        [Test]
        public void MixedKinds_ProductionEmitsCurvedBeforePoints_UnlikeTheDemoPath()
        {
            using var h = new TwoPathHarness();
            List<LabelInstance> labels = MixedLabels(h.Origin); // point, curved, point — in that order
            OracleAndPlanMirror(h, labels, out SymbolLabelBatch demo, out SymbolLabelBatch plan);

            Assert.AreEqual(
                new[] { LabelRecordKind.Point, LabelRecordKind.Curved, LabelRecordKind.Point },
                Kinds(demo),
                "the builder oracle preserves input-list order (SymbolLabelBatchBuilder.Build walks the list).");

            Assert.AreEqual(
                new[] { LabelRecordKind.Curved, LabelRecordKind.Point, LabelRecordKind.Point },
                Kinds(plan),
                "the production path emits curved labels during the tile scan and point winners afterwards " +
                "(SymbolLabelReconciler.Run's two segments) — so it does NOT preserve input order. This is the " +
                "divergence that makes a mixed-kind fixture's record order change when it is migrated off the " +
                "demo overload; it is asserted rather than worked around so a later change to either path is " +
                "forced to restate it deliberately.");

            Assert.AreEqual(demo.Count, plan.Count,
                "the divergence must be a permutation only — the same records, reordered, not a different set.");
            Assert.AreEqual(demo.PointCount, plan.PointCount, "same number of point records either way.");
            Assert.AreEqual(demo.CurvedCount, plan.CurvedCount, "same number of curved records either way.");
        }
    }
}
