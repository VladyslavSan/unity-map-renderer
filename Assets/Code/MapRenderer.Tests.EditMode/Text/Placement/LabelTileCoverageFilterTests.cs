// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// <see cref="LabelTileCoverageFilter.FilterActive"/> — the pre-build compaction that moved the tile-
    /// coverage cull ahead of the SoA batch build, and (REVISION 2) classifies each tile Keep / Fade / Drop
    /// instead of a plain two-way cull so a tile that WAS on screen fades out instead of popping. Real
    /// <see cref="WebMercatorProjection"/> + a diagonal viewProj scaled by <see cref="WebMercator.WorldExtent"/>
    /// so a z=0 tile (spans the WHOLE Mercator square by construction) projects to ~full-viewport NDC
    /// (coverage ~1.0, always kept) and a deep-zoom tile at the same origin projects to a vanishingly small
    /// NDC quad (coverage ~1e-12, always below <see cref="MinCoverage"/>) — no exact-area arithmetic needed,
    /// just a robust big/tiny contrast. The crossing tests reuse the SAME tiny tile but call with a lenient
    /// threshold first (below its own ~1e-11 coverage, so it reads as "kept") to seed the above-set/deadline
    /// state, then the real threshold to exercise the crossing.
    /// </summary>
    [TestFixture]
    public class LabelTileCoverageFilterTests
    {
        private static readonly IProjection Projection = new WebMercatorProjection();
        private static readonly double2 Viewport = new double2(1000, 1000);
        private static readonly double3 SceneOrigin = new double3(0, 0, 0);
        private static readonly float3x3 Rebase = float3x3.identity;

        // Diagonal viewProj: clip.x = local.x / WorldExtent, clip.y = local.z / WorldExtent (north → screen
        // vertical), clip.w = 1 (always in front). local.y (altitude) is always 0 for a surface projection,
        // so its column is left zero.
        private static readonly float4x4 ViewProj = new float4x4(
            new float4((float)(1.0 / WebMercator.WorldExtent), 0, 0, 0),
            new float4(0, 0, 0, 0),
            new float4(0, (float)(1.0 / WebMercator.WorldExtent), 0, 0),
            new float4(0, 0, 0, 1));

        // z=0's single tile spans the WHOLE Mercator square (±WorldExtent on both axes, by construction of
        // MaxLatitude) → ~full-viewport coverage.
        private static readonly long BigTileKey = LabelTileKey.Pack(new TileId { Z = 0, X = 0, Y = 0 });

        // z=19 tile at the same (lon=0, lat=0) origin: side length ≈ 2·WorldExtent / 2^19 ≈ 76m → NDC span
        // ≈ 3.8e-6 → coverage ≈ 1.4e-11. Vanishingly small regardless of threshold.
        private static readonly long TinyTileKey = LabelTileKey.Pack(new TileId { Z = 19, X = 262144, Y = 262144 });

        private const double MinCoverage = 0.05;
        // Below the tiny tile's own ~1.4e-11 coverage — a call at this threshold always reads the tiny tile
        // as "kept", used only to seed the above-set/deadline state ahead of a crossing test.
        private const double LenientThreshold = 1e-12;
        private const double GraceSeconds = 0.5;

        // Fresh, empty cross-frame state for a test that doesn't care about history (a first-touch tile).
        private static (HashSet<long> abovePrev, HashSet<long> aboveThisFrame, Dictionary<long, double> departingUntil,
            HashSet<long> fadingOut, Dictionary<long, byte> decisionScratch) FreshState()
            => (new HashSet<long>(), new HashSet<long>(), new Dictionary<long, double>(), new HashSet<long>(), new Dictionary<long, byte>());

        // Mirrors the subsystem's ping-pong: after a call, this call's AboveThisFrame becomes next call's AbovePrev.
        private static void Swap(ref HashSet<long> abovePrev, ref HashSet<long> aboveThisFrame)
            => (abovePrev, aboveThisFrame) = (aboveThisFrame, abovePrev);

        [Test]
        public void FilterActive_BelowThresholdActiveTile_IsCulled_ActiveCountReduced()
        {
            var labels = new List<LabelInstance>
            {
                new LabelInstance { TileKey = BigTileKey },
                new LabelInstance { TileKey = TinyTileKey },
            };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            int newActive = LabelTileCoverageFilter.FilterActive(
                labels, activeCount: 2, Projection, SceneOrigin, ViewProj, Viewport, Rebase, MinCoverage,
                abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds, decisionScratch,
                out int culledCount);

            Assert.AreEqual(1, newActive, "the tiny tile's label — never seen above threshold — must be dropped, the big tile's kept");
            Assert.AreEqual(1, culledCount);
            Assert.AreEqual(1, labels.Count);
            Assert.AreEqual(BigTileKey, labels[0].TileKey);
        }

        [Test]
        public void FilterActive_DepartingLabelOnSubThresholdTile_Survives()
        {
            // Active: one kept big-tile label. Departing (>= activeCount): one tiny-tile label — must survive
            // untouched even though its tile is well below the coverage threshold (active-only scope fence).
            var departing = new LabelInstance { TileKey = TinyTileKey };
            var labels = new List<LabelInstance>
            {
                new LabelInstance { TileKey = BigTileKey },
                departing,
            };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            int newActive = LabelTileCoverageFilter.FilterActive(
                labels, activeCount: 1, Projection, SceneOrigin, ViewProj, Viewport, Rebase, MinCoverage,
                abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds, decisionScratch,
                out int culledCount);

            Assert.AreEqual(1, newActive, "the active range is untouched by the departing tile's coverage");
            Assert.AreEqual(0, culledCount, "a departing record is never coverage-classified");
            Assert.AreEqual(2, labels.Count, "the departing label must survive, not be dropped");
            Assert.AreSame(departing, labels[1]);
        }

        [Test]
        public void FilterActive_MixedActiveAndDeparting_CompactsStably()
        {
            var keptA = new LabelInstance { TileKey = BigTileKey };
            var culledB = new LabelInstance { TileKey = TinyTileKey };
            var keptC = new LabelInstance { TileKey = BigTileKey };
            var dep0 = new LabelInstance { TileKey = BigTileKey };
            var dep1 = new LabelInstance { TileKey = TinyTileKey };
            var labels = new List<LabelInstance> { keptA, culledB, keptC, dep0, dep1 };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            int newActive = LabelTileCoverageFilter.FilterActive(
                labels, activeCount: 3, Projection, SceneOrigin, ViewProj, Viewport, Rebase, MinCoverage,
                abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds, decisionScratch,
                out int culledCount);

            Assert.AreEqual(2, newActive);
            Assert.AreEqual(1, culledCount);
            Assert.AreEqual(new[] { keptA, keptC, dep0, dep1 }, labels.ToArray(),
                "surviving actives keep their relative order, then the untouched departing tail");
        }

        [Test]
        public void FilterActive_NoProjection_OrNonPositiveThreshold_IsNoOp()
        {
            var labels = new List<LabelInstance>
            {
                new LabelInstance { TileKey = TinyTileKey },
                new LabelInstance { TileKey = TinyTileKey },
            };
            // Pre-seed cross-frame state — the no-op guard must leave it untouched (nothing to reconcile).
            var abovePrev = new HashSet<long> { TinyTileKey };
            var aboveThisFrame = new HashSet<long>();
            var departingUntil = new Dictionary<long, double> { [TinyTileKey] = 10.0 };
            var fadingOut = new HashSet<long>();
            var decisionScratch = new Dictionary<long, byte>();

            int viaNullProjection = LabelTileCoverageFilter.FilterActive(
                labels, activeCount: 2, projection: null, SceneOrigin, ViewProj, Viewport, Rebase, MinCoverage,
                abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds, decisionScratch,
                out int culledViaNullProjection);
            Assert.AreEqual(2, viaNullProjection, "null projection ⇒ cull nothing");
            Assert.AreEqual(0, culledViaNullProjection);
            Assert.AreEqual(2, labels.Count);
            Assert.IsTrue(abovePrev.Contains(TinyTileKey), "no-op guard leaves cross-frame state alone");
            Assert.AreEqual(10.0, departingUntil[TinyTileKey]);

            int viaZeroThreshold = LabelTileCoverageFilter.FilterActive(
                labels, activeCount: 2, Projection, SceneOrigin, ViewProj, Viewport, Rebase, minCoverage: 0.0,
                abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds, decisionScratch,
                out int culledViaZeroThreshold);
            Assert.AreEqual(2, viaZeroThreshold, "threshold 0 ⇒ cull nothing");
            Assert.AreEqual(0, culledViaZeroThreshold);

            int viaNegativeThreshold = LabelTileCoverageFilter.FilterActive(
                labels, activeCount: 2, Projection, SceneOrigin, ViewProj, Viewport, Rebase, minCoverage: -1.0,
                abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds, decisionScratch,
                out int culledViaNegativeThreshold);
            Assert.AreEqual(2, viaNegativeThreshold, "negative threshold ⇒ cull nothing");
            Assert.AreEqual(0, culledViaNegativeThreshold);
        }

        [Test]
        public void FilterActive_AboveThresholdFullViewportTile_IsKept()
        {
            var labels = new List<LabelInstance> { new LabelInstance { TileKey = BigTileKey } };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            int newActive = LabelTileCoverageFilter.FilterActive(
                labels, activeCount: 1, Projection, SceneOrigin, ViewProj, Viewport, Rebase, MinCoverage,
                abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds, decisionScratch,
                out int culledCount);

            Assert.AreEqual(1, newActive);
            Assert.AreEqual(0, culledCount);
            Assert.AreEqual(1, labels.Count);
            Assert.IsTrue(aboveThisFrame.Contains(BigTileKey), "a kept tile is recorded as above-threshold this frame");
        }

        // ── REVISION 2: crossing tests. Each seeds AbovePrev with a lenient-threshold call (the tiny tile reads
        //    as "kept" below its own ~1e-11 coverage), swaps it into AbovePrev like the subsystem does, then the
        //    real threshold's call exercises the crossing. RED against the base (drop-only) FilterActive: it has
        //    no history/deadline params at all and would simply drop every below-threshold active label. ──

        [Test]
        public void FilterActive_CrossingBelow_KeptAndMarkedFading_ForGraceWindow()
        {
            var labels = new List<LabelInstance> { new LabelInstance { TileKey = TinyTileKey } };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            // Frame 1 (lenient threshold): the tiny tile reads as kept — seeds AbovePrev for frame 2's crossing.
            LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport, Rebase,
                LenientThreshold, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds,
                decisionScratch, out int culled1);
            Assert.AreEqual(0, culled1);
            Swap(ref abovePrev, ref aboveThisFrame);

            // Frame 2 (real threshold): now culled by coverage alone — but it was above last frame, so it FADES
            // (kept, marked), not dropped.
            int active2 = LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 1.0, GraceSeconds,
                decisionScratch, out int culled2);

            Assert.AreEqual(1, active2, "a crossing-below tile is KEPT (fading), not dropped");
            Assert.AreEqual(0, culled2, "a fading tile is not counted as culled");
            Assert.IsTrue(fadingOut.Contains(TinyTileKey), "the crossed tile is marked fading for Build to flag");
            Assert.AreEqual(1.0 + GraceSeconds, departingUntil[TinyTileKey], "a fade deadline was stamped now + grace");
        }

        [Test]
        public void FilterActive_NeverAbove_DropsImmediately_NoMark()
        {
            var labels = new List<LabelInstance> { new LabelInstance { TileKey = TinyTileKey } };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            int newActive = LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds,
                decisionScratch, out int culled);

            Assert.AreEqual(0, newActive, "never-visible tile drops on first sight — nothing was on screen to pop");
            Assert.AreEqual(1, culled);
            Assert.AreEqual(0, labels.Count);
            Assert.IsFalse(fadingOut.Contains(TinyTileKey), "a tile never above threshold is never marked fading");
            Assert.IsFalse(departingUntil.ContainsKey(TinyTileKey), "no deadline stamped for an immediate drop");
        }

        [Test]
        public void FilterActive_WithinGrace_StillFades_ThenExpired_Drops()
        {
            var labels = new List<LabelInstance> { new LabelInstance { TileKey = TinyTileKey } };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            // Frame 1 (lenient): kept, seeds AbovePrev.
            LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport, Rebase,
                LenientThreshold, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds,
                decisionScratch, out _);
            Swap(ref abovePrev, ref aboveThisFrame);

            // Frame 2 (real threshold): crosses below — fades, deadline stamped at 1.0 + grace.
            int active2 = LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 1.0, GraceSeconds,
                decisionScratch, out int culled2);
            Assert.AreEqual(1, active2, "still fading — kept");
            Assert.AreEqual(0, culled2);
            double deadline = departingUntil[TinyTileKey];
            Swap(ref abovePrev, ref aboveThisFrame);

            // Frame 3: still within the grace window (now < deadline) and no longer in AbovePrev (frame 2 was
            // Fade, not Keep) — must still fade via the live-deadline branch, NOT the old "drop once below" rule.
            int active3 = LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: deadline - 0.1,
                GraceSeconds, decisionScratch, out int culled3);
            Assert.AreEqual(1, active3, "still within the grace window — keeps fading");
            Assert.AreEqual(0, culled3);
            Assert.IsTrue(fadingOut.Contains(TinyTileKey));
            Assert.AreEqual(deadline, departingUntil[TinyTileKey], "the deadline is NOT renewed while already fading");
            Swap(ref abovePrev, ref aboveThisFrame);

            // Frame 4: grace has now elapsed — drops.
            int active4 = LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: deadline + 0.1,
                GraceSeconds, decisionScratch, out int culled4);
            Assert.AreEqual(0, active4, "grace expired — now drops");
            Assert.AreEqual(1, culled4);
        }

        [Test]
        public void FilterActive_CrossBackAbove_ClearsDeadline()
        {
            var labels = new List<LabelInstance> { new LabelInstance { TileKey = TinyTileKey } };
            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, decisionScratch) = FreshState();

            // Frame 1 (lenient): kept, seeds AbovePrev.
            LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport, Rebase,
                LenientThreshold, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0, GraceSeconds,
                decisionScratch, out _);
            Swap(ref abovePrev, ref aboveThisFrame);

            // Frame 2 (real threshold): crosses below — fades, deadline stamped.
            LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport, Rebase,
                MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 1.0, GraceSeconds,
                decisionScratch, out _);
            Assert.IsTrue(departingUntil.ContainsKey(TinyTileKey));
            Swap(ref abovePrev, ref aboveThisFrame);

            // Frame 3 (lenient again): crosses back above threshold — Keep clears the deadline.
            int active3 = LabelTileCoverageFilter.FilterActive(labels, 1, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, LenientThreshold, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 2.0, GraceSeconds,
                decisionScratch, out int culled3);

            Assert.AreEqual(1, active3);
            Assert.AreEqual(0, culled3);
            Assert.IsFalse(departingUntil.ContainsKey(TinyTileKey), "crossing back above clears the fade deadline");
            Assert.IsFalse(fadingOut.Contains(TinyTileKey), "kept tiles aren't in the fading set");
        }

        // ── D1 (Blocker 2) — LabelTileCoverageFilter.ClassifyActive: the classify-only counterpart that WRITES a
        //    per-record Keep/Fade/Drop decision instead of compacting. D1-#3: its decisions must match which
        //    labels FilterActive would have kept (Keep/Fade survive) vs dropped (Drop), over the SAME fixture +
        //    cross-frame state. Each classifier gets its OWN independent cross-frame state (never shared) — running
        //    them over shared state would let one's side effects perturb the other's result, testing a moving
        //    target instead of "same starting state, same decision". ──

        [Test]
        public void ClassifyActive_MixedActiveAndDeparting_MatchesFilterActive_KeepFadeSurviveDropRemoved()
        {
            // The SAME 5-label shape as FilterActive_MixedActiveAndDeparting_CompactsStably: 3 active (kept A,
            // culled B, kept C) + 2 departing (dep0, dep1) — activeCount = 3.
            var keptA = new LabelInstance { TileKey = BigTileKey };
            var culledB = new LabelInstance { TileKey = TinyTileKey };
            var keptC = new LabelInstance { TileKey = BigTileKey };
            var dep0 = new LabelInstance { TileKey = BigTileKey };
            var dep1 = new LabelInstance { TileKey = TinyTileKey };
            var labelsForFilter = new List<LabelInstance> { keptA, culledB, keptC, dep0, dep1 };
            var labelsForClassify = new List<LabelInstance> { keptA, culledB, keptC, dep0, dep1 };
            var isDeparting = new List<byte> { 0, 0, 0, 1, 1 };

            var (abovePrevF, aboveThisFrameF, departingUntilF, fadingOutF, decisionScratchF) = FreshState();
            var (abovePrevC, aboveThisFrameC, departingUntilC, fadingOutC, decisionScratchC) = FreshState();

            int newActive = LabelTileCoverageFilter.FilterActive(
                labelsForFilter, activeCount: 3, Projection, SceneOrigin, ViewProj, Viewport, Rebase, MinCoverage,
                abovePrevF, aboveThisFrameF, departingUntilF, fadingOutF, now: 0.0, GraceSeconds, decisionScratchF,
                out int culledF);

            var decisions = new List<byte>();
            LabelTileCoverageFilter.ClassifyActive(
                labelsForClassify, isDeparting, Projection, SceneOrigin, ViewProj, Viewport, Rebase, MinCoverage,
                abovePrevC, aboveThisFrameC, departingUntilC, fadingOutC, now: 0.0, GraceSeconds, decisionScratchC,
                decisions, out int culledC);

            Assert.AreEqual(culledF, culledC, "Drop count must match FilterActive's culledCount");
            Assert.AreEqual(LabelTileCoverageFilter.Keep, decisions[0], "keptA survives in both");
            Assert.AreEqual(LabelTileCoverageFilter.Drop, decisions[1], "culledB — FilterActive removed it");
            Assert.AreEqual(LabelTileCoverageFilter.Keep, decisions[2], "keptC survives in both");
            Assert.AreEqual(LabelTileCoverageFilter.Keep, decisions[3], "dep0 — departing, never classified (scope fence)");
            Assert.AreEqual(LabelTileCoverageFilter.Keep, decisions[4], "dep1 — departing, never classified (scope fence)");

            // Cross-check the whole survivor SET: FilterActive's compacted list (post-call, `labelsForFilter` IS the
            // survivor set — active survivors THEN the untouched departing tail) == ClassifyActive's non-Drop records.
            Assert.AreEqual(2, newActive, "sanity: only keptA/keptC survive the active range");
            var survivorsFromClassify = new List<LabelInstance>();
            for (int i = 0; i < labelsForClassify.Count; i++)
                if (decisions[i] != LabelTileCoverageFilter.Drop) survivorsFromClassify.Add(labelsForClassify[i]);
            Assert.AreEqual(labelsForFilter, survivorsFromClassify,
                "ClassifyActive's Keep+Fade set (Drop excluded) must equal FilterActive's compacted survivor list");
        }

        [Test]
        public void ClassifyActive_FadeCrossing_MatchesFilterActive_KeptAndMarkedFading()
        {
            var labelF = new LabelInstance { TileKey = TinyTileKey };
            var labelsF = new List<LabelInstance> { labelF };
            var labelC = new LabelInstance { TileKey = TinyTileKey };
            var labelsC = new List<LabelInstance> { labelC };
            var isDeparting = new List<byte> { 0 };

            var (abovePrevF, aboveThisFrameF, departingUntilF, fadingOutF, decisionScratchF) = FreshState();
            var (abovePrevC, aboveThisFrameC, departingUntilC, fadingOutC, decisionScratchC) = FreshState();

            // Frame 1 (lenient threshold): both read the tile as kept — seeds each side's own AbovePrev independently.
            LabelTileCoverageFilter.FilterActive(labelsF, 1, Projection, SceneOrigin, ViewProj, Viewport, Rebase,
                LenientThreshold, abovePrevF, aboveThisFrameF, departingUntilF, fadingOutF, now: 0.0, GraceSeconds,
                decisionScratchF, out _);
            Swap(ref abovePrevF, ref aboveThisFrameF);

            var decisions1 = new List<byte>();
            LabelTileCoverageFilter.ClassifyActive(labelsC, isDeparting, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, LenientThreshold, abovePrevC, aboveThisFrameC, departingUntilC, fadingOutC, now: 0.0,
                GraceSeconds, decisionScratchC, decisions1, out _);
            Swap(ref abovePrevC, ref aboveThisFrameC);
            Assert.AreEqual(LabelTileCoverageFilter.Keep, decisions1[0], "frame 1: both keep (lenient threshold)");

            // Frame 2 (real threshold): crosses below — FilterActive keeps it (fading), ClassifyActive must decide Fade.
            int active2 = LabelTileCoverageFilter.FilterActive(labelsF, 1, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, MinCoverage, abovePrevF, aboveThisFrameF, departingUntilF, fadingOutF, now: 1.0, GraceSeconds,
                decisionScratchF, out int culled2F);

            var decisions2 = new List<byte>();
            LabelTileCoverageFilter.ClassifyActive(labelsC, isDeparting, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, MinCoverage, abovePrevC, aboveThisFrameC, departingUntilC, fadingOutC, now: 1.0, GraceSeconds,
                decisionScratchC, decisions2, out int culled2C);

            Assert.AreEqual(1, active2, "sanity: FilterActive kept the label (fading, not dropped)");
            Assert.AreEqual(LabelTileCoverageFilter.Fade, decisions2[0],
                "ClassifyActive must decide Fade to match FilterActive's keep-while-fading");
            Assert.AreEqual(culled2F, culled2C, "neither side counts a fading tile as culled");
            Assert.IsTrue(fadingOutC.Contains(TinyTileKey), "ClassifyActive also marks the tile fading");
            Assert.AreEqual(departingUntilF[TinyTileKey], departingUntilC[TinyTileKey],
                "both stamp the identical fade deadline (now + grace)");
        }

        [Test]
        public void ClassifyActive_NoProjection_OrNonPositiveThreshold_IsNoOp_AllKeep()
        {
            var labels = new List<LabelInstance>
            {
                new LabelInstance { TileKey = TinyTileKey },
                new LabelInstance { TileKey = TinyTileKey },
            };
            var isDeparting = new List<byte> { 0, 0 };
            // Pre-seed cross-frame state — the no-op guard must leave it untouched (nothing to reconcile).
            var abovePrev = new HashSet<long> { TinyTileKey };
            var aboveThisFrame = new HashSet<long>();
            var departingUntil = new Dictionary<long, double> { [TinyTileKey] = 10.0 };
            var fadingOut = new HashSet<long>();
            var decisionScratch = new Dictionary<long, byte>();
            var decisions = new List<byte>();

            LabelTileCoverageFilter.ClassifyActive(labels, isDeparting, projection: null, SceneOrigin, ViewProj,
                Viewport, Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0,
                GraceSeconds, decisionScratch, decisions, out int culledViaNullProjection);
            Assert.AreEqual(0, culledViaNullProjection);
            CollectionAssert.AreEqual(
                new[] { LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep }, decisions,
                "null projection ⇒ classify nothing, every record reads Keep");
            Assert.IsTrue(abovePrev.Contains(TinyTileKey), "no-op guard leaves cross-frame state alone");
            Assert.AreEqual(10.0, departingUntil[TinyTileKey]);

            LabelTileCoverageFilter.ClassifyActive(labels, isDeparting, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, minCoverage: 0.0, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0,
                GraceSeconds, decisionScratch, decisions, out int culledViaZeroThreshold);
            Assert.AreEqual(0, culledViaZeroThreshold);
            CollectionAssert.AreEqual(
                new[] { LabelTileCoverageFilter.Keep, LabelTileCoverageFilter.Keep }, decisions,
                "threshold 0 ⇒ classify nothing, every record reads Keep");
        }
    }
}
