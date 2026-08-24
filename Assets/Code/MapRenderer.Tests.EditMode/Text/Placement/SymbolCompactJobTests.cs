// Unity EditMode only — needs the job runtime (NativeArray/NativeList/NativeHashSet/NativeHashMap/IJob). NOT
// registered in core-tests.csproj.
// Unlike SymbolCullJobTests, this is NOT a ULP-hazard differential: Compact is integer branch + verbatim
// double3/float3 COPY + a single float compare (`> FadeEpsilon`) — no `dot`, no FMA-reorderable math — so
// Burst and managed are bit-identical here. Fade opacities still sit CLEARLY above/below FadeEpsilon (0.5/0.7
// alive, 0.0/absent dead) for readability, not because of any precision risk.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// <see cref="SymbolCompactJob"/> — the Burst port of <c>SymbolPlacementSystem.GatherSymbolPoints</c>'s
    /// Compact pass — must produce the SAME <c>_stagePointOffset</c> / kept-point pools / <c>_forceFadeOut</c>
    /// membership / per-trigger counters, in the SAME record order, as an independent managed reference.
    /// </summary>
    [TestFixture]
    public class SymbolCompactJobTests
    {
        private const float FadeEpsilon = 1e-3f;

        // Record indices — named so the fixture and the expectation table read as one thing.
        private const int KeptNoneA           = 0;
        private const int DroppedRec          = 1;
        private const int DepartingDead       = 2;
        private const int CoverageDead        = 3;
        private const int ZoomDeadA           = 4;
        private const int HorizonDead         = 5;
        private const int DistanceDead        = 6;
        private const int DepartingAlive      = 7;
        private const int CoverageCurvedAlive = 8;
        private const int ZoomCurvedDead      = 9;
        private const int KeptNoneB           = 10;
        private const int SymbolCount         = 11;

        // Detail == record index everywhere (Detail[r] = r) — a deliberate simplification: it still exercises
        // every read path (PointDetails / CurvedAnchorFadeStart / CurvedAnchorCount are all indexed by Detail,
        // not by r directly), while keeping the fixture legible.
        private static readonly GatherTrigger[] Trigger =
        {
            GatherTrigger.None, GatherTrigger.Dropped, GatherTrigger.Departing, GatherTrigger.Coverage,
            GatherTrigger.Zoom, GatherTrigger.Horizon, GatherTrigger.Distance, GatherTrigger.Departing,
            GatherTrigger.Coverage, GatherTrigger.Zoom, GatherTrigger.None,
        };

        private static SymbolPlacementKind[] Kinds()
        {
            var a = new SymbolPlacementKind[SymbolCount]; // Point (0) everywhere by default
            a[CoverageCurvedAlive] = SymbolPlacementKind.Curved;
            a[ZoomCurvedDead]      = SymbolPlacementKind.Curved;
            return a;
        }

        private static int[] Detail()
        {
            var a = new int[SymbolCount];
            for (int r = 0; r < SymbolCount; r++) a[r] = r;
            return a;
        }

        // Point-kind FadeIds, indexed by Detail (== r for point records). Only the "fade-triggered" records
        // (2-7) are ever read — 0/1/10 short-circuit before MarkFadeOutIfAlive; 8/9 are Curved.
        private static long[] PointFadeIds()
        {
            var a = new long[SymbolCount];
            a[DepartingDead]  = 2001;
            a[CoverageDead]   = 3001;
            a[ZoomDeadA]      = 4001;
            a[HorizonDead]    = 5001;
            a[DistanceDead]   = 6001;
            a[DepartingAlive] = 7001;
            return a;
        }

        private static PointStageInput[] PointDetails(long[] pointFadeIds)
        {
            var a = new PointStageInput[SymbolCount];
            for (int r = 0; r < SymbolCount; r++) a[r] = new PointStageInput { FadeId = pointFadeIds[r] };
            return a;
        }

        // Curved-kind anchor slices, indexed by Detail (== r). CoverageCurvedAlive has 2 anchors (+ 1 trailing
        // centred-fallback id = 3 ids read); ZoomCurvedDead has 1 anchor (+ fallback = 2 ids read).
        private static int[] CurvedAnchorFadeStart()
        {
            var a = new int[SymbolCount];
            a[CoverageCurvedAlive] = 100;
            a[ZoomCurvedDead]      = 200;
            return a;
        }

        private static int[] CurvedAnchorCount()
        {
            var a = new int[SymbolCount];
            a[CoverageCurvedAlive] = 2;
            a[ZoomCurvedDead]      = 1;
            return a;
        }

        // Flat FadeIds pool the curved slices above index into. CoverageCurvedAlive: one anchor alive (8101),
        // the other anchor + the centred fallback dead. ZoomCurvedDead: anchor + fallback both dead.
        private static long[] FadeIds()
        {
            var a = new long[210];
            a[100] = 8100; a[101] = 8101; a[102] = 8102; // CoverageCurvedAlive: anchor, anchor(alive), fallback
            a[200] = 9100; a[201] = 9101;                 // ZoomCurvedDead: anchor, fallback
            return a;
        }

        private static Dictionary<long, float> FadeOpacity() => new Dictionary<long, float>
        {
            [7001] = 0.5f, // DepartingAlive's point FadeId — clearly alive
            [8101] = 0.7f, // CoverageCurvedAlive's one live anchor — clearly alive
            // every other FadeId referenced by the fixture is ABSENT ⇒ reads as dead (TryGetValue false).
        };

        // World-point pool: irregular WorldStart per kept record (NOT the cumulative kept-vertex count, and
        // not derivable from r) + a point/up pattern that differs per component (point=(r,v,0), up=(r,v,1)) so
        // a swapped-source or wrong-array read is visible. Only kept records (0,7,8,10) are ever read; the
        // skipped records' spans are never touched, so they are left at the zeroed default.
        private static int[] WorldStart()
        {
            var a = new int[SymbolCount];
            a[KeptNoneA]           = 6;
            a[DepartingAlive]      = 0;
            a[CoverageCurvedAlive] = 10;
            a[KeptNoneB]           = 2;
            return a;
        }

        private static int[] WorldCount()
        {
            var a = new int[SymbolCount];
            a[KeptNoneA]           = 2;
            a[DepartingAlive]      = 1;
            a[CoverageCurvedAlive] = 3;
            a[KeptNoneB]           = 2;
            return a;
        }

        private const int WorldPoolSize = 13;

        private static double3[] WorldPoints()
        {
            var a = new double3[WorldPoolSize];
            a[6] = new double3(0, 0, 0); a[7] = new double3(0, 1, 0);       // KeptNoneA v0/v1
            a[0] = new double3(7, 0, 0);                                    // DepartingAlive v0
            a[10] = new double3(8, 0, 0); a[11] = new double3(8, 1, 0); a[12] = new double3(8, 2, 0); // CoverageCurvedAlive v0-2
            a[2] = new double3(10, 0, 0); a[3] = new double3(10, 1, 0);     // KeptNoneB v0/v1
            return a;
        }

        private static float3[] WorldUps()
        {
            var a = new float3[WorldPoolSize];
            a[6] = new float3(0, 0, 1); a[7] = new float3(0, 1, 1);
            a[0] = new float3(7, 0, 1);
            a[10] = new float3(8, 0, 1); a[11] = new float3(8, 1, 1); a[12] = new float3(8, 2, 1);
            a[2] = new float3(10, 0, 1); a[3] = new float3(10, 1, 1);
            return a;
        }

        // ── Expected sanity table (asserted against the managed reference FIRST — Cull's fixture-bug guard) ────

        private static readonly int[] ExpectedOffset =
        {
            0, -1, -1, -1, -1, -1, -1, 2, 3, -1, 6,
        };

        private static readonly double3[] ExpectedPoints =
        {
            new double3(0, 0, 0), new double3(0, 1, 0),                                // KeptNoneA
            new double3(7, 0, 0),                                                      // DepartingAlive
            new double3(8, 0, 0), new double3(8, 1, 0), new double3(8, 2, 0),           // CoverageCurvedAlive
            new double3(10, 0, 0), new double3(10, 1, 0),                              // KeptNoneB
        };

        private static readonly float3[] ExpectedUps =
        {
            new float3(0, 0, 1), new float3(0, 1, 1),
            new float3(7, 0, 1),
            new float3(8, 0, 1), new float3(8, 1, 1), new float3(8, 2, 1),
            new float3(10, 0, 1), new float3(10, 1, 1),
        };

        private static readonly HashSet<long> ExpectedForceFadeOut = new HashSet<long>
        {
            7001, 8100, 8101, 8102, 9100, 9101,
        };

        private const int ExpectedDeparting = 1; // DepartingDead only — DepartingAlive is kept, not counted
        private const int ExpectedCoverage  = 1; // CoverageDead only — CoverageCurvedAlive is kept, not counted
        private const int ExpectedZoom      = 2; // ZoomDeadA + ZoomCurvedDead
        private const int ExpectedHorizon   = 1; // HorizonDead
        private const int ExpectedDistance  = 1; // DistanceDead

        // ── The independent managed reference — re-implements the Compact loop, NOT a call into the job ────────

        private static bool ManagedTryForceFadeOut(long fadeId, Dictionary<long, float> fadeOpacity,
            HashSet<long> forceFadeOut, float fadeEpsilon)
        {
            if (!(fadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > fadeEpsilon)) return false;
            forceFadeOut.Add(fadeId);
            return true;
        }

        private static bool ManagedMarkFadeOutIfAlive(int r, SymbolPlacementKind[] kinds, int[] detail, long[] pointFadeIds,
            int[] curvedAnchorFadeStart, int[] curvedAnchorCount, long[] fadeIds, Dictionary<long, float> fadeOpacity,
            HashSet<long> forceFadeOut, float fadeEpsilon)
        {
            int d = detail[r];
            if (kinds[r] == SymbolPlacementKind.Point)
                return ManagedTryForceFadeOut(pointFadeIds[d], fadeOpacity, forceFadeOut, fadeEpsilon);

            bool alive = false;
            int fadeStart = curvedAnchorFadeStart[d];
            int fadeCount = curvedAnchorCount[d] + 1; // + trailing centred-fallback fade id
            for (int i = 0; i < fadeCount; i++)
            {
                long fadeId = fadeIds[fadeStart + i];
                forceFadeOut.Add(fadeId);
                if (fadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > fadeEpsilon) alive = true;
            }
            return alive;
        }

        private static void ManagedCompact(SymbolPlacementKind[] kinds, int[] detail, long[] pointFadeIds,
            int[] curvedAnchorFadeStart, int[] curvedAnchorCount, long[] fadeIds, int[] worldStart, int[] worldCount,
            double3[] worldPoints, float3[] worldUps, Dictionary<long, float> fadeOpacity, float fadeEpsilon,
            int[] outOffset, List<double3> outPoints, List<float3> outUps, HashSet<long> outForceFadeOut, int[] outCounts)
        {
            for (int r = 0; r < SymbolCount; r++)
            {
                GatherTrigger t = Trigger[r];
                if (t == GatherTrigger.Dropped) { outOffset[r] = -1; continue; }

                if (t != GatherTrigger.None && !ManagedMarkFadeOutIfAlive(r, kinds, detail, pointFadeIds,
                        curvedAnchorFadeStart, curvedAnchorCount, fadeIds, fadeOpacity, outForceFadeOut, fadeEpsilon))
                {
                    outCounts[(int)t]++;
                    outOffset[r] = -1;
                    continue;
                }

                outOffset[r] = outPoints.Count;
                int ws = worldStart[r], wc = worldCount[r];
                for (int v = 0; v < wc; v++)
                {
                    outPoints.Add(worldPoints[ws + v]);
                    outUps.Add(worldUps[ws + v]);
                }
            }
        }

        // ── The Burst arm — SymbolCompactJob.Run() over the same fixture ────────────────────────────────────

        private static (int[] offset, double3[] points, float3[] ups, HashSet<long> forceFadeOut, int[] counts)
            NativeCompact(SymbolPlacementKind[] kinds, int[] detail, PointStageInput[] pointDetails, int[] curvedAnchorFadeStart,
                int[] curvedAnchorCount, long[] fadeIds, int[] worldStart, int[] worldCount, double3[] worldPoints,
                float3[] worldUps, Dictionary<long, float> fadeOpacity)
        {
            const Allocator alloc = Allocator.TempJob;
            NativeArray<T> From<T>(T[] src) where T : unmanaged
            {
                var a = new NativeArray<T>(src.Length, alloc);
                for (int i = 0; i < src.Length; i++) a[i] = src[i];
                return a;
            }

            var nTrigger = From(Trigger);
            var nKinds = From(kinds);
            var nDetail = From(detail);
            var nPointDetails = From(pointDetails);
            var nCurvedAnchorFadeStart = From(curvedAnchorFadeStart);
            var nCurvedAnchorCount = From(curvedAnchorCount);
            var nFadeIds = From(fadeIds);
            var nWorldStart = From(worldStart);
            var nWorldCount = From(worldCount);
            var nWorldPoints = From(worldPoints);
            var nWorldUps = From(worldUps);

            var nFadeOpacity = new NativeHashMap<long, float>(fadeOpacity.Count, alloc);
            foreach (var kv in fadeOpacity) nFadeOpacity.Add(kv.Key, kv.Value);

            var nStageOffset = new NativeArray<int>(SymbolCount, alloc);
            for (int i = 0; i < SymbolCount; i++) nStageOffset[i] = int.MinValue; // poison, NOT zero — see field doc

            var nOutPoints = new NativeList<double3>(alloc);
            var nOutUps = new NativeList<float3>(alloc);
            var nForceFadeOut = new NativeHashSet<long>(16, alloc);
            var nCounts = new NativeArray<int>((int)GatherTrigger.Dropped + 1, alloc); // ClearMemory (default) → fresh zeros

            try
            {
                new SymbolCompactJob
                {
                    Trigger = nTrigger, Kinds = nKinds, Detail = nDetail, PointDetails = nPointDetails,
                    CurvedAnchorFadeStart = nCurvedAnchorFadeStart, CurvedAnchorCount = nCurvedAnchorCount,
                    FadeIds = nFadeIds, WorldStart = nWorldStart, WorldCount = nWorldCount,
                    WorldPoints = nWorldPoints, WorldUps = nWorldUps, FadeOpacity = nFadeOpacity,
                    FadeEpsilon = FadeEpsilon, Count = SymbolCount,
                    StageOffset = nStageOffset, OutPoints = nOutPoints, OutUps = nOutUps,
                    ForceFadeOut = nForceFadeOut, Counts = nCounts,
                }.Run();

                var forceFadeOut = new HashSet<long>();
                foreach (long id in nForceFadeOut) forceFadeOut.Add(id);

                return (nStageOffset.ToArray(), nOutPoints.AsArray().ToArray(), nOutUps.AsArray().ToArray(),
                    forceFadeOut, nCounts.ToArray());
            }
            finally
            {
                nTrigger.Dispose(); nKinds.Dispose(); nDetail.Dispose(); nPointDetails.Dispose();
                nCurvedAnchorFadeStart.Dispose(); nCurvedAnchorCount.Dispose(); nFadeIds.Dispose();
                nWorldStart.Dispose(); nWorldCount.Dispose(); nWorldPoints.Dispose(); nWorldUps.Dispose();
                nFadeOpacity.Dispose(); nStageOffset.Dispose(); nOutPoints.Dispose(); nOutUps.Dispose();
                nForceFadeOut.Dispose(); nCounts.Dispose();
            }
        }

        [Test]
        public void Compact_MatchesManagedReference_EveryOutputAndRunningOffset()
        {
            SymbolPlacementKind[] kinds = Kinds();
            int[] detail = Detail();
            long[] pointFadeIds = PointFadeIds();
            PointStageInput[] pointDetails = PointDetails(pointFadeIds);
            int[] curvedAnchorFadeStart = CurvedAnchorFadeStart();
            int[] curvedAnchorCount = CurvedAnchorCount();
            long[] fadeIds = FadeIds();
            int[] worldStart = WorldStart();
            int[] worldCount = WorldCount();
            double3[] worldPoints = WorldPoints();
            float3[] worldUps = WorldUps();
            Dictionary<long, float> fadeOpacity = FadeOpacity();

            var managedOffset = new int[SymbolCount];
            for (int i = 0; i < SymbolCount; i++) managedOffset[i] = int.MinValue; // poison — see NativeCompact
            var managedPoints = new List<double3>();
            var managedUps = new List<float3>();
            var managedForceFadeOut = new HashSet<long>();
            var managedCounts = new int[(int)GatherTrigger.Dropped + 1];

            ManagedCompact(kinds, detail, pointFadeIds, curvedAnchorFadeStart, curvedAnchorCount, fadeIds,
                worldStart, worldCount, worldPoints, worldUps, fadeOpacity, FadeEpsilon,
                managedOffset, managedPoints, managedUps, managedForceFadeOut, managedCounts);

            // Sanity: the managed reference itself must match the fixture's stated expectation table — otherwise
            // a fixture bug (not a job bug) would silently pass by having both arms agree on the WRONG answer.
            CollectionAssert.AreEqual(ExpectedOffset, managedOffset, "managed reference vs. Expected offsets");
            CollectionAssert.AreEqual(ExpectedPoints, managedPoints, "managed reference vs. Expected points");
            CollectionAssert.AreEqual(ExpectedUps, managedUps, "managed reference vs. Expected ups");
            CollectionAssert.AreEquivalent(ExpectedForceFadeOut, managedForceFadeOut, "managed reference vs. Expected forceFadeOut");
            Assert.AreEqual(ExpectedDeparting, managedCounts[(int)GatherTrigger.Departing], "managed Departing count");
            Assert.AreEqual(ExpectedCoverage, managedCounts[(int)GatherTrigger.Coverage], "managed Coverage count");
            Assert.AreEqual(ExpectedZoom, managedCounts[(int)GatherTrigger.Zoom], "managed Zoom count");
            Assert.AreEqual(ExpectedHorizon, managedCounts[(int)GatherTrigger.Horizon], "managed Horizon count");
            Assert.AreEqual(ExpectedDistance, managedCounts[(int)GatherTrigger.Distance], "managed Distance count");

            var (nativeOffset, nativePoints, nativeUps, nativeForceFadeOut, nativeCounts) = NativeCompact(
                kinds, detail, pointDetails, curvedAnchorFadeStart, curvedAnchorCount, fadeIds,
                worldStart, worldCount, worldPoints, worldUps, fadeOpacity);

            CollectionAssert.AreEqual(managedOffset, nativeOffset, "SymbolCompactJob vs. managed reference — offsets");
            CollectionAssert.AreEqual(managedPoints, nativePoints, "SymbolCompactJob vs. managed reference — points");
            CollectionAssert.AreEqual(managedUps, nativeUps, "SymbolCompactJob vs. managed reference — ups");
            CollectionAssert.AreEquivalent(managedForceFadeOut, nativeForceFadeOut, "SymbolCompactJob vs. managed reference — forceFadeOut");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Departing], nativeCounts[(int)GatherTrigger.Departing], "native Departing count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Coverage], nativeCounts[(int)GatherTrigger.Coverage], "native Coverage count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Zoom], nativeCounts[(int)GatherTrigger.Zoom], "native Zoom count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Horizon], nativeCounts[(int)GatherTrigger.Horizon], "native Horizon count");
            Assert.AreEqual(managedCounts[(int)GatherTrigger.Distance], nativeCounts[(int)GatherTrigger.Distance], "native Distance count");
        }
    }
}
