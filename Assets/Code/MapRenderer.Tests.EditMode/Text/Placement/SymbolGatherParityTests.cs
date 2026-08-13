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

        // Burst-gather Stage 1 (§6.2 widening): an EMPTY quad list — a point label that contributes zero quads,
        // exercising SymbolGatherJob's `quadCount > 0` guard (a zero-length source array can yield a null Ptr).
        private static TextLayoutResult ZeroQuad() => new TextLayoutResult
        {
            Quads = new List<SymbolQuad>(), BoundsMin = float2.zero, BoundsMax = float2.zero, LineCount = 1,
        };

        // Burst-gather Stage 1 (§6.2 widening): a parameterized CurvedLabel so a test can hand it an EMPTY
        // glyph list or an EMPTY anchor array — exercising the `glyphCount > 0` / `anchorCount > 0` guards and,
        // for the empty-anchor case, the UNGUARDED fade copy (fadeCount = anchorCount + 1 = 1, no guard).
        private static LabelInstance CurvedLabelCustom(double3 anchor, string text, int feature, long tileKey,
            List<CurvedGlyph> glyphs, LineAnchor[] anchors)
            => new LabelInstance
            {
                AnchorRender = anchor, Placement = SymbolPlacement.Line, Text = text, Paint = LabelPaint.Default,
                TextSizePx = 20f, PaddingPx = 2f, SortKey = 1f, FeatureIndex = feature, TileKey = tileKey,
                MaxAngleDeg = 45f, KeepUpright = true,
                PathRender = new[] { new double3(0, 0, 0) + anchor, new double3(10, 0, 0) + anchor, new double3(20, 0, 0) + anchor },
                LineAnchors = anchors,
                CurvedGlyphs = glyphs,
            };

        private static SymbolTileLabelStore.Key Key(TileId t) => new SymbolTileLabelStore.Key("s", t);

        // Bake + commit one tile's labels as a native block (the SAME projection/tileOrigin the oracle Build resolves).
        private static void SeedTile(SymbolTileLabelStore store, TileId tile, List<LabelInstance> labels)
        {
            int gen = store.BeginBuild(Key(tile));
            SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(labels, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, labels, block), "sanity: block committed");
        }

        private static long Tk(TileId t) => LabelTileKey.Pack(t);

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

            plan.Build(planBlockId, planLocalIndex, collected, isDeparting, decisions, store.OrderedBlocks, winnerSetVersion: 0);
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

                string diff = SymbolLabelBatchDiff.FirstDifference(oracle, gathered);
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
                Assert.IsNotNull(SymbolLabelBatchDiff.FirstDifference(oracle, gathered),
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
                Assert.IsNotNull(SymbolLabelBatchDiff.FirstDifference(oracle, gathered),
                    "an un-permuted plan desyncs from the post-filter list — the gather must diverge from the oracle");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Tooth #2 (gather path): once warm, a second HEAVY GatherIntoMirror allocates ZERO managed garbage — the
        // mirror native lists + plan are reused (the whole point of moving the SoA to a build-time bake). Mirrors
        // the SymbolLabelSubsystemPumpTests.CurrentBatch_Warm_… idiom, one level down on the gather itself.
        // R1: post-memo, a same-version GatherIntoMirror is a memo HIT, not the heavy path this test's message
        // claims — bump WinnerSetVersion immediately before the measured call to force a real rebuild (the honest
        // "force a rebuild" knob: the field is internal, so the test can assign it). The memo-HIT alloc guard is
        // LabelGatherMemoTests.GatherIntoMirror_MemoHit_AllocatesNoGCMemory (T6), kept separate.
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

                plan.WinnerSetVersion++; // force the measured call to take the heavy (rebuild) path, not a memo hit
                // §6.4 strengthening: the version bump above is a PRECONDITION this test's own claim ("measures the
                // heavy rebuild path") rests on — nothing previously asserted it actually engaged. A MirrorRebuildCount
                // delta makes that load-bearing, so this test can never silently degrade into measuring a memo hit.
                int rebuildsBefore = harness.Lps.MirrorRebuildCount;
                Assert.That(() => { harness.Lps.GatherIntoMirror(plan); }, Is.Not.AllocatingGCMemory(),
                    "a warm GatherIntoMirror must allocate ZERO managed garbage — native lists + plan are reused");
                Assert.AreEqual(rebuildsBefore + 1, harness.Lps.MirrorRebuildCount,
                    "precondition: the measured call must take the heavy rebuild path (WinnerSetVersion bump), not a memo hit");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // §6.2 — widen the parity fixture: a Burst-only running-offset bug only manifests when MULTIPLE winners
        // share a block, or a ZERO-SIZED slice sits between two non-empty ones — the original 4-tile fixture (one
        // winner per block) cannot catch either. Hand-built (no store/FilterActive — full control over winner
        // order and which raw slot is null), this fixture exercises all four gaps in one shot:
        //  - block A: >= 3 winners, with a NULL slot BETWEEN the first two real ones (the null-slot invariant —
        //    localIndex 1 is never itself a winner, but it must not perturb localIndex 2's block-pool slot);
        //  - a point label with ZERO quads (Layout.Quads empty) — the `quadCount > 0` guard;
        //  - a curved label with ZERO glyphs, and one with ZERO LineAnchors — the `glyphCount > 0` /
        //    `anchorCount > 0` guards AND the unguarded fade copy (fadeCount = anchorCount + 1 = 1, no guard);
        //  - >= 2 blocks interleaved in winner order (A, B, A, B, A) so the per-block switch and the running
        //    mirror cursors are exercised together, not block-at-a-time.
        [Test]
        public void Gather_MatchesBuildOracle_FieldByField_MultiWinnerInterleavedBlocks()
        {
            var a = new TileId { Z = 5, X = 20, Y = 16 };
            var b = new TileId { Z = 5, X = 21, Y = 16 };
            double3 originA = TileRenderOrigin.Project(a, P);
            double3 originB = TileRenderOrigin.Project(b, P);
            long tkA = Tk(a), tkB = Tk(b);

            // Block A raw slots: [0] point ZERO quads (real) — [1] null (inert) — [2] point normal #1, 1 quad
            // (real) — [3] point normal #2, 1 DIFFERENT quad (real) — [4] curved normal #1, 2 glyphs + 1 anchor
            // (real) — [5] curved ZERO glyphs (real) — [6] curved normal #2, 2 DIFFERENT glyphs + 1 DIFFERENT
            // anchor (real). Two quad-bearing points AND two glyph/anchor-bearing curveds in the SAME block is
            // what makes the SECOND of each pair's source offset within the block's OWN pool genuinely non-zero
            // (each #1 occupies the pool's slot 0; the zero-content records before/between contribute nothing to
            // the running offset) — review found the original point-only widening left the curved arm's three
            // source-offset remaps (glyph/anchor/fade start) untestable, since every fixture in the repo has at
            // most one curved label per block. #1 and #2 carry DIFFERENT glyph/anchor content so a swapped
            // source offset reads detectably wrong data, not coincidentally-correct data.
            var aPointZeroQuads = new LabelInstance
            {
                AnchorRender = new double3(10, 0, 10), Placement = SymbolPlacement.Point, Layout = ZeroQuad(),
                Paint = LabelPaint.Default, TextSizePx = 20f, PaddingPx = 2f, SortKey = 0f, Text = "a0",
                FeatureIndex = 10, TileKey = tkA,
            };
            LabelInstance aPointNormal1 = PointLabel(new double3(30, 0, 30), "a2", 12, tkA, 0.4f);
            LabelInstance aPointNormal2 = PointLabel(new double3(35, 0, 35), "a3", 13, tkA, 0.45f);
            LabelInstance aCurvedNormal1 = CurvedLabelCustom(new double3(40, 0, 40), "a4", 16, tkA,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 1f, Cell = OneQuad(0.1f).Quads[0] },
                    new CurvedGlyph { ArcCenter = 2f, Cell = OneQuad(0.15f).Quads[0] },
                },
                anchors: new[] { new LineAnchor(0, 0.5f) });
            LabelInstance aCurvedZeroGlyphs = CurvedLabelCustom(new double3(41, 0, 41), "a5", 17, tkA,
                glyphs: new List<CurvedGlyph>(), anchors: new[] { new LineAnchor(0, 0.55f) });
            LabelInstance aCurvedNormal2 = CurvedLabelCustom(new double3(42, 0, 42), "a6", 18, tkA,
                glyphs: new List<CurvedGlyph>
                {
                    new CurvedGlyph { ArcCenter = 5f, Cell = OneQuad(0.8f).Quads[0] },
                    new CurvedGlyph { ArcCenter = 6f, Cell = OneQuad(0.85f).Quads[0] },
                },
                anchors: new[] { new LineAnchor(0, 0.75f) });
            var aLabels = new List<LabelInstance>
                { aPointZeroQuads, null, aPointNormal1, aPointNormal2, aCurvedNormal1, aCurvedZeroGlyphs, aCurvedNormal2 };

            // Block B raw slots: [0] curved ZERO anchors (real) — [1] point normal (real).
            LabelInstance bCurvedZeroAnchors = CurvedLabelCustom(new double3(50, 0, 50), "b0", 14, tkB,
                glyphs: new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 3f, Cell = OneQuad(0.6f).Quads[0] } },
                anchors: Array.Empty<LineAnchor>());
            LabelInstance bPointNormal = PointLabel(new double3(60, 0, 60), "b1", 15, tkB, 0.7f);
            var bLabels = new List<LabelInstance> { bCurvedZeroAnchors, bPointNormal };

            SymbolTileLabelBlock blockA = SymbolTileLabelBlockBaker.Bake(aLabels, slotCount: 1, originA);
            SymbolTileLabelBlock blockB = SymbolTileLabelBlockBaker.Bake(bLabels, slotCount: 1, originB);

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                // Winner order A(li0), B(li0), A(li2), B(li1), A(li3), A(li4), A(li5), A(li6) — interleaved
                // A,B,A,B,A,A,A,A; A's second winner (li2) sits right after the null at li1.
                var blockId = new List<int> { 0, 1, 0, 1, 0, 0, 0, 0 };
                var localIndex = new List<int> { 0, 0, 2, 1, 3, 4, 5, 6 };
                var collected = new List<LabelInstance>
                {
                    aPointZeroQuads, bCurvedZeroAnchors, aPointNormal1, bPointNormal,
                    aPointNormal2, aCurvedNormal1, aCurvedZeroGlyphs, aCurvedNormal2,
                };
                var isDeparting = new List<byte> { 0, 0, 0, 0, 0, 0, 0, 0 };
                var decisions = new List<byte>
                {
                    LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep,
                    LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep,
                    LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep,
                };
                var orderedBlocks = new List<IDisposable> { blockA, blockB };

                plan.Build(blockId, localIndex, collected, isDeparting, decisions, orderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan);
                var gathered = new SymbolLabelBatch();
                harness.Lps.CopyMirrorInto(gathered);

                var oracle = new SymbolLabelBatch();
                SymbolLabelBatchBuilder.Build(oracle, collected, slotCount: 1, P, activeCount: collected.Count, coverageFadingTiles: null);

                Assert.AreEqual(8, oracle.Count, "precondition: 8 winners collected");
                Assert.AreEqual(4, oracle.PointCount, "precondition: aPointZeroQuads + aPointNormal1 + bPointNormal + aPointNormal2");
                Assert.AreEqual(4, oracle.CurvedCount, "precondition: bCurvedZeroAnchors + aCurvedNormal1 + aCurvedZeroGlyphs + aCurvedNormal2");
                Assert.AreEqual(0, oracle.PointQuadCount[0], "precondition: the first-processed point (aPointZeroQuads) has zero quads");
                Assert.AreEqual(0, oracle.CurvedAnchorCount[0], "precondition: the first-processed curved (bCurvedZeroAnchors) has zero anchors");
                Assert.AreEqual(0, oracle.CurvedGlyphCount[2], "precondition: the third-processed curved (aCurvedZeroGlyphs) has zero glyphs");
                // Block-level preconditions (not oracle-derived proxies — see the review's N4 finding): pin the
                // REAL property D3-class defects need to diverge on, not a coincidental stand-in.
                int aPointNormal2Detail = blockA.Detail[3];
                Assert.AreNotEqual(0, blockA.PointQuadStart[aPointNormal2Detail],
                    "precondition: aPointNormal2's source quad offset within block A's own pool is genuinely non-zero");
                int aCurvedNormal2Detail = blockA.Detail[6];
                Assert.AreNotEqual(0, blockA.CurvedGlyphStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source glyph offset within block A's own pool is genuinely non-zero");
                Assert.AreNotEqual(0, blockA.CurvedAnchorStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source anchor offset within block A's own pool is genuinely non-zero");
                Assert.AreNotEqual(0, blockA.CurvedAnchorFadeStart[aCurvedNormal2Detail],
                    "precondition: aCurvedNormal2's source fade offset within block A's own pool is genuinely non-zero");

                string diff = SymbolLabelBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the Build oracle on the widened fixture — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); blockA.Dispose(); blockB.Dispose(); }
        }

        // ── §10 D8/D9 P12: bake/gather parity holds on a pair-bearing fixture — the baker resolves PairRole
        //    over the TILE list (SymbolTileLabelBlockBaker.Fill), the oracle over the WINNER list
        //    (SymbolLabelBatchBuilder.Build); both intact here, so they must agree field-for-field. ──
        [Test]
        public void Gather_MatchesBuildOracle_FieldByField_CentredPair()
        {
            var a = new TileId { Z = 5, X = 22, Y = 16 };
            double3 originA = TileRenderOrigin.Project(a, P);
            long tkA = Tk(a);

            var iconQuads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f),
                    UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
                },
            };
            var icon = new LabelInstance
            {
                AnchorRender = new double3(70, 0, 70), Placement = SymbolPlacement.Point,
                Layout = new TextLayoutResult { Quads = iconQuads, BoundsMin = new float2(-8f, -8f), BoundsMax = new float2(8f, 8f), LineCount = 1 },
                Kind = LabelKind.Icon, IconImage = "shield", Paint = LabelPaint.Default, TextSizePx = TextQuadLayout.OneEm,
                SortKey = 0f, FeatureIndex = 20, TileKey = tkA,
                PairRole = LabelPairRole.Owner, PairId = 20,
            };
            var text = new LabelInstance
            {
                AnchorRender = new double3(70, 0, 70), Placement = SymbolPlacement.Point, Layout = OneQuad(0.9f),
                Paint = LabelPaint.Default, TextSizePx = 20f, PaddingPx = 2f, SortKey = 0f, Text = "42",
                FeatureIndex = 21, TileKey = tkA,
                PairRole = LabelPairRole.Rider, PairId = 20,
            };

            var labels = new List<LabelInstance> { icon, text };
            SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(labels, slotCount: 1, originA);

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                var blockId = new List<int> { 0, 0 };
                var localIndex = new List<int> { 0, 1 };
                var collected = new List<LabelInstance> { icon, text };
                var isDeparting = new List<byte> { 0, 0 };
                var decisions = new List<byte> { LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep };
                var orderedBlocks = new List<IDisposable> { block };

                plan.Build(blockId, localIndex, collected, isDeparting, decisions, orderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan);
                var gathered = new SymbolLabelBatch();
                harness.Lps.CopyMirrorInto(gathered);

                var oracle = new SymbolLabelBatch();
                SymbolLabelBatchBuilder.Build(oracle, collected, slotCount: 1, P, activeCount: collected.Count, coverageFadingTiles: null);

                Assert.AreEqual(2, oracle.PointCount, "precondition: icon + text, both point-placement");
                Assert.AreEqual(LabelPairRole.Owner, oracle.Points[0].PairRole, "precondition: the icon resolves as Owner in the oracle");
                Assert.AreEqual(LabelPairRole.Rider, oracle.Points[1].PairRole, "precondition: the text resolves as Rider in the oracle");

                string diff = SymbolLabelBatchDiff.FirstDifference(oracle, gathered);
                Assert.IsNull(diff, $"gather must be byte-identical to the Build oracle on a pair-bearing fixture — first difference: {diff}");
            }
            finally { plan.Dispose(); harness.Dispose(); block.Dispose(); }
        }
    }
}
