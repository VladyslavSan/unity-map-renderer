// Unity EditMode only — SymbolGatherPlan / SymbolTileLabelBlock / LabelPlacementSystem.GatherIntoMirror all use
// Unity.Collections; reached via InternalsVisibleTo("MapRenderer.Tests.EditMode"). NOT in core-tests.csproj.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Is = UnityEngine.TestTools.Constraints.Is; // Is.Not.AllocatingGCMemory() — the tooth-#2 gather alloc guard

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 2 (design §5 B) — THE ORDER-PARITY TOOTH (#3). The production per-frame
    /// path replaced <c>SymbolLabelBatchBuilder.Build</c> (managed SoA) with a NATIVE GATHER
    /// (<see cref="LabelPlacementSystem.GatherIntoMirror"/>) that compacts each winner's pre-baked
    /// <see cref="SymbolTileLabelBlock"/> slice into the placement job's native mirror. This test proves the gather
    /// is BYTE-IDENTICAL to the <c>Build</c> oracle over the SAME collected+filtered list — the exact invariant the
    /// GPU snapshot suite depends on, localized to a field-by-field comparison so a spine bug (winner order, the
    /// <c>(blockId, localIndex)</c> mapping, or the <c>FilterActive</c> lockstep permute) surfaces HERE, not as a
    /// vague snapshot flip.
    ///
    /// <para>Fixture: a MULTI-TILE set with BOTH curved and point labels, a tile the coverage filter FADES, a tile
    /// it DROPS (so the active-range compaction genuinely moves elements), and a DEPARTING tile (so the
    /// departing-tail shift runs). The oracle is <c>Build</c> over the SAME post-<c>FilterActive</c> list using the
    /// SAME projection the blocks were baked with (so every per-tile RTC origin matches).</para>
    /// </summary>
    [TestFixture]
    public class SymbolGatherParityTests
    {
        // Identity projection matrices: every corner projects (clip.w == 1 > 0) so a HUGE minCoverage forces every
        // tile below threshold deterministically — Keep/Fade/Drop is then driven purely by coverageAbovePrev, no
        // camera framing needed (the filter's classification, not its exact coverage number, is what we exercise).
        private static readonly float4x4 IdViewProj = float4x4.identity;
        private static readonly float3x3 IdRebase = float3x3.identity;
        private static readonly double2 Viewport = new double2(100, 100);
        // Dominates any projected tile-quad coverage (identity projection of z5 Mercator corners tops out ~1e14),
        // so every tile is deterministically "below threshold" → Keep/Fade/Drop is driven purely by coverageAbovePrev.
        private const double HugeMinCoverage = 1e30;

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
                AnchorRender = anchor, Placement = SymbolPlacement.Point, Layout = OneQuad(u), Paint = LabelPaint.Default,
                TextSizePx = 20f, PaddingPx = 2f, SortKey = 0f, Text = text, FeatureIndex = feature, TileKey = tileKey,
            };

        private static LabelInstance CurvedLabel(double3 anchor, string text, int feature, long tileKey)
            => new LabelInstance
            {
                AnchorRender = anchor, Placement = SymbolPlacement.Line, Text = text, Paint = LabelPaint.Default,
                TextSizePx = 20f, PaddingPx = 2f, SortKey = 1f, FeatureIndex = feature, TileKey = tileKey,
                MaxAngleDeg = 45f, KeepUpright = true,
                PathRender = new[] { new double3(0, 0, 0) + anchor, new double3(10, 0, 0) + anchor, new double3(20, 0, 0) + anchor },
                LineAnchors = new[] { new LineAnchor(0, 0.5f) },
                CurvedGlyphs = new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 3f, Cell = OneQuad(0.3f).Quads[0] },
                    new CurvedGlyph { ArcCenter = 9f, Cell = OneQuad(0.5f).Quads[0] },
                },
            };

        private static SymbolTileLabelStore.Key Key(TileId t) => new SymbolTileLabelStore.Key("s", t);

        // Bake + commit one tile's labels as a native block (the SAME projection/tileOrigin the oracle Build resolves).
        private static void SeedTile(SymbolTileLabelStore store, TileId tile, List<LabelInstance> labels)
        {
            int gen = store.BeginBuild(Key(tile));
            SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(labels, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, labels, block), "sanity: block committed");
        }

        private static long Tk(TileId t) => MapRenderer.Core.Style.Symbol.SymbolFeatureExtractor.PackTileKey(t);

        // Build the whole production winner-plan pipeline into `plan`, and the reference oracle SoA via Build over
        // the SAME post-FilterActive list. Returns the gathered mirror (materialized as a batch) via `lps`.
        //
        // D1 note: this test stays on the FilterActive COMPACTION seam deliberately (advisor-reviewed) — its
        // purpose is winner ORDER parity (the (blockId, localIndex) mapping / lockstep permute), orthogonal to
        // D1's coverage-mask work. Post-FilterActive there are no Drops left in `collected` (they were physically
        // removed, same as before D1), so `isDeparting`/`decisions` are derived LOCALLY from the already-known
        // `activeCount`/`coverageFadingTiles` split to feed the new `SymbolGatherPlan.Build` signature — bit-for-bit
        // what the old activeCount/coverageFadingTiles-based Build produced (Dropped is all-zero: nothing here was
        // ever Dropped by construction). D1-#2 (the genuinely new resident-Drop/mask path) is a separate test.
        private static void RunPipeline(SymbolTileLabelStore store, LabelPlacementSystem lps, SymbolGatherPlan plan,
            HashSet<long> coverageAbovePrev, bool skipPermute, int perturbWinner, int perturbLocalIndex,
            out SymbolLabelBatch oracle, out SymbolLabelBatch gathered, out int culled)
        {
            var collected = new List<LabelInstance>();
            var planBlockId = new List<int>();
            var planLocalIndex = new List<int>();
            var planIsDeparting = new List<byte>();
            store.CollectInto(collected, planBlockId, planLocalIndex, planIsDeparting, quantizeMeters: 1.0, out int activeCount);

            var coverageAboveThisFrame = new HashSet<long>();
            var coverageDepartingUntil = new Dictionary<long, double>();
            var coverageFadingTiles = new HashSet<long>();
            var tileDecision = new Dictionary<long, byte>();
            // skipPermute passes null plan args → the collected list is filtered but the plan arrays are NOT permuted
            // (they stay full-length, desynced from the shorter post-filter list) — the RED-verify (b) defect.
            activeCount = LabelTileCoverageFilter.FilterActive(collected, activeCount, P,
                double3.zero, IdViewProj, Viewport, IdRebase, HugeMinCoverage,
                coverageAbovePrev, coverageAboveThisFrame, coverageDepartingUntil, coverageFadingTiles,
                now: 20.0, graceSeconds: 1000.0, tileDecision, out culled,
                skipPermute ? null : planBlockId, skipPermute ? null : planLocalIndex);

            if (perturbWinner >= 0) planLocalIndex[perturbWinner] = perturbLocalIndex; // RED-verify (a)

            // Derive the new Build signature's per-record arrays from the post-FilterActive split (see header note).
            int n = collected.Count;
            var isDeparting = new List<byte>(n);
            var decisions = new List<byte>(n);
            for (int i = 0; i < n; i++)
            {
                isDeparting.Add((byte)(i >= activeCount ? 1 : 0));
                bool fading = i < activeCount && coverageFadingTiles.Contains(collected[i].TileKey);
                decisions.Add(fading ? LabelTileCoverageFilter.Fade : LabelTileCoverageFilter.Keep);
            }

            plan.Build(planBlockId, planLocalIndex, collected, isDeparting, decisions, store.OrderedBlocks);
            lps.GatherIntoMirror(plan);
            gathered = new SymbolLabelBatch();
            lps.CopyMirrorInto(gathered);

            oracle = new SymbolLabelBatch();
            SymbolLabelBatchBuilder.Build(oracle, collected, slotCount: 1, P, activeCount, coverageFadingTiles);
        }

        // A 4-tile fixture: A (point + curved, faded), B (point, faded), C (point, DROPPED), D (point, DEPARTING).
        private static SymbolTileLabelStore Seed(out HashSet<long> coverageAbovePrev,
            out TileId a, out TileId b, out TileId c, out TileId d)
        {
            a = new TileId { Z = 5, X = 16, Y = 16 };
            b = new TileId { Z = 5, X = 17, Y = 16 };
            c = new TileId { Z = 5, X = 18, Y = 16 };
            d = new TileId { Z = 5, X = 19, Y = 16 };

            var store = new SymbolTileLabelStore(cacheCap: 16);
            SeedTile(store, a, new List<LabelInstance>
            {
                PointLabel(new double3(100, 0, 200), "a", 1, Tk(a), 0.1f),
                CurvedLabel(new double3(120, 0, 220), "ac", 2, Tk(a)),
            });
            SeedTile(store, b, new List<LabelInstance> { PointLabel(new double3(300, 0, 400), "b", 3, Tk(b), 0.15f) });
            SeedTile(store, c, new List<LabelInstance> { PointLabel(new double3(500, 0, 600), "c", 4, Tk(c), 0.2f) });
            SeedTile(store, d, new List<LabelInstance> { PointLabel(new double3(700, 0, 800), "d", 5, Tk(d), 0.25f) });

            // D leaves cover with a grace window → cached + departing (collected after the active split).
            store.ReconcileActiveSet(
                new List<SymbolTileLabelStore.Key> { Key(a), Key(b), Key(c) },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 1000.0);

            // A, B were above threshold last frame → they FADE; C never was → it DROPS.
            coverageAbovePrev = new HashSet<long> { Tk(a), Tk(b) };
            return store;
        }

        // Owns the LPS + the throwaway Unity resources the fixture creates. LabelPlacementSystem.Dispose frees only
        // its CLONED material, so this disposes the base material + render texture (+ camera GO) explicitly — no leak.
        private sealed class LpsHarness : IDisposable
        {
            public readonly LabelPlacementSystem Lps;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public LpsHarness()
            {
                _camGo = new GameObject("GatherParity_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(64, 64, 0);
                uCam.targetTexture = _rt;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 0, Longitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                // GatherIntoMirror/CopyMirrorInto touch neither the camera nor the material; a material silences the ctor warn.
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Lps = new LabelPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Lps.Dispose();                                    // frees the cloned material + native scratch
                UnityEngine.Object.DestroyImmediate(_camGo);      // destroy the Camera FIRST so the RT is no longer
                UnityEngine.Object.DestroyImmediate(_rt);         // its targetTexture (else Unity logs an Error → test fail)
                UnityEngine.Object.DestroyImmediate(_baseMaterial); // the base clone source (Lps.Dispose never sees it)
            }
        }

        [Test]
        public void Gather_MatchesBuildOracle_FieldByField()
        {
            SymbolTileLabelStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: -1, perturbLocalIndex: -1,
                    out SymbolLabelBatch oracle, out SymbolLabelBatch gathered, out int culled);

                Assert.AreEqual(1, culled, "precondition: tile C is Dropped (so the compaction moves elements)");
                Assert.AreEqual(4, oracle.Count, "precondition: A.curved + A.point + B.point (active) + D.point (departing)");
                Assert.AreEqual(1, oracle.CurvedCount, "precondition: exactly the one curved label (A)");
                Assert.AreEqual(3, oracle.PointCount, "precondition: A.point + B.point + D.point");

                string diff = FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the Build oracle — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // RED-verify (a): perturb ONE winner's localIndex — the gather reads the wrong record; parity MUST break.
        [Test]
        public void Gather_DetectsPerturbedLocalIndex()
        {
            SymbolTileLabelStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Winner 0 is A.curved (localIndex 1 in block A); force it to read A's OTHER record (the point, index 0).
                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: 0, perturbLocalIndex: 0,
                    out SymbolLabelBatch oracle, out SymbolLabelBatch gathered, out _);
                Assert.IsNotNull(FirstDifference(oracle, gathered),
                    "a perturbed winner localIndex must diverge from the oracle — the parity comparison has teeth");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // RED-verify (b): skip the FilterActive parallel permute — the plan arrays desync from the post-filter list;
        // the gather reads misaligned winners; parity MUST break.
        [Test]
        public void Gather_DetectsSkippedFilterPermute()
        {
            SymbolTileLabelStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                RunPipeline(store, harness.Lps, plan, above, skipPermute: true, perturbWinner: -1, perturbLocalIndex: -1,
                    out SymbolLabelBatch oracle, out SymbolLabelBatch gathered, out _);
                Assert.IsNotNull(FirstDifference(oracle, gathered),
                    "an un-permuted plan desyncs from the post-filter list — the gather must diverge from the oracle");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Tooth #2 (gather path): once warm, a second GatherIntoMirror allocates ZERO managed garbage — the mirror
        // native lists + plan are reused (the whole point of moving the SoA to a build-time bake). Mirrors the
        // SymbolLabelSubsystemPumpTests.CurrentBatch_Warm_… idiom, one level down on the gather itself.
        [Test]
        public void GatherIntoMirror_Warm_AllocatesNoGCMemory()
        {
            SymbolTileLabelStore store = Seed(out HashSet<long> above, out _, out _, out _, out _);
            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Build the plan ONCE (collect + filter + plan.Build may allocate first-touch — outside the measure),
                // then materialize the gather so its native lists reach steady capacity.
                RunPipeline(store, harness.Lps, plan, above, skipPermute: false, perturbWinner: -1, perturbLocalIndex: -1,
                    out _, out _, out _);
                harness.Lps.GatherIntoMirror(plan); // extra warm-up (first-touch native growth already done above)

                Assert.That(() => { harness.Lps.GatherIntoMirror(plan); }, Is.Not.AllocatingGCMemory(),
                    "a warm GatherIntoMirror must allocate ZERO managed garbage — native lists + plan are reused");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Returns the first field that differs between two batches (up to each count), or null if byte-identical.
        private static string FirstDifference(SymbolLabelBatch o, SymbolLabelBatch g)
        {
            if (o.Count != g.Count) return $"Count {o.Count} vs {g.Count}";
            if (o.PointCount != g.PointCount) return $"PointCount {o.PointCount} vs {g.PointCount}";
            if (o.CurvedCount != g.CurvedCount) return $"CurvedCount {o.CurvedCount} vs {g.CurvedCount}";
            if (o.QuadCount != g.QuadCount) return $"QuadCount {o.QuadCount} vs {g.QuadCount}";
            if (o.GlyphCount != g.GlyphCount) return $"GlyphCount {o.GlyphCount} vs {g.GlyphCount}";
            if (o.AnchorCount != g.AnchorCount) return $"AnchorCount {o.AnchorCount} vs {g.AnchorCount}";
            if (o.WorldPointCount != g.WorldPointCount) return $"WorldPointCount {o.WorldPointCount} vs {g.WorldPointCount}";
            if (o.AnchorFadeCount != g.AnchorFadeCount) return $"AnchorFadeCount {o.AnchorFadeCount} vs {g.AnchorFadeCount}";
            if (o.MaxBoxes != g.MaxBoxes) return $"MaxBoxes {o.MaxBoxes} vs {g.MaxBoxes}";
            if (o.MaxQuads != g.MaxQuads) return $"MaxQuads {o.MaxQuads} vs {g.MaxQuads}";
            if (o.MaxCandidates != g.MaxCandidates) return $"MaxCandidates {o.MaxCandidates} vs {g.MaxCandidates}";

            for (int i = 0; i < o.Count; i++)
            {
                if (o.Kinds[i] != g.Kinds[i]) return $"Kinds[{i}] {o.Kinds[i]} vs {g.Kinds[i]}";
                if (o.Detail[i] != g.Detail[i]) return $"Detail[{i}] {o.Detail[i]} vs {g.Detail[i]}";
                if (o.WorldStart[i] != g.WorldStart[i]) return $"WorldStart[{i}] {o.WorldStart[i]} vs {g.WorldStart[i]}";
                if (o.WorldCount[i] != g.WorldCount[i]) return $"WorldCount[{i}] {o.WorldCount[i]} vs {g.WorldCount[i]}";
                if (!o.RepAnchor[i].Equals(g.RepAnchor[i])) return $"RepAnchor[{i}]";
                if (o.RecordDeparting[i] != g.RecordDeparting[i]) return $"RecordDeparting[{i}] {o.RecordDeparting[i]} vs {g.RecordDeparting[i]}";
                if (o.RecordCoverageFading[i] != g.RecordCoverageFading[i]) return $"RecordCoverageFading[{i}] {o.RecordCoverageFading[i]} vs {g.RecordCoverageFading[i]}";
            }
            for (int i = 0; i < o.PointCount; i++)
            {
                if (!o.Points[i].Equals(g.Points[i])) return $"Points[{i}]";
                if (o.PointQuadStart[i] != g.PointQuadStart[i]) return $"PointQuadStart[{i}] {o.PointQuadStart[i]} vs {g.PointQuadStart[i]}";
                if (o.PointQuadCount[i] != g.PointQuadCount[i]) return $"PointQuadCount[{i}] {o.PointQuadCount[i]} vs {g.PointQuadCount[i]}";
            }
            for (int i = 0; i < o.CurvedCount; i++)
            {
                if (!o.Curveds[i].Equals(g.Curveds[i])) return $"Curveds[{i}]";
                if (o.CurvedGlyphStart[i] != g.CurvedGlyphStart[i]) return $"CurvedGlyphStart[{i}]";
                if (o.CurvedGlyphCount[i] != g.CurvedGlyphCount[i]) return $"CurvedGlyphCount[{i}]";
                if (o.CurvedAnchorStart[i] != g.CurvedAnchorStart[i]) return $"CurvedAnchorStart[{i}]";
                if (o.CurvedAnchorCount[i] != g.CurvedAnchorCount[i]) return $"CurvedAnchorCount[{i}]";
                if (o.CurvedAnchorFadeStart[i] != g.CurvedAnchorFadeStart[i]) return $"CurvedAnchorFadeStart[{i}]";
            }
            for (int i = 0; i < o.QuadCount; i++) if (!o.Quads[i].Equals(g.Quads[i])) return $"Quads[{i}]";
            for (int i = 0; i < o.GlyphCount; i++) if (!o.Glyphs[i].Equals(g.Glyphs[i])) return $"Glyphs[{i}]";
            for (int i = 0; i < o.AnchorCount; i++) if (!o.Anchors[i].Equals(g.Anchors[i])) return $"Anchors[{i}]";
            for (int i = 0; i < o.WorldPointCount; i++) if (!o.WorldPoints[i].Equals(g.WorldPoints[i])) return $"WorldPoints[{i}]";
            for (int i = 0; i < o.AnchorFadeCount; i++) if (o.AnchorFadeIds[i] != g.AnchorFadeIds[i]) return $"AnchorFadeIds[{i}]";
            return null;
        }
    }
}
