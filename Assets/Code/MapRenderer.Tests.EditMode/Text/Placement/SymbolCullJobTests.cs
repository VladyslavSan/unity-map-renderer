// Unity EditMode only — needs the job runtime (NativeArray / IJobParallelFor). NOT registered in core-tests.csproj.
// NOTE: EditMode batch runs the job Burst-compiled; this differential validates that the Burst SymbolCullJob and
// an independent managed reference reach the SAME per-record verdict. Dropped/Departing/Coverage/Zoom are pure
// integer/branch logic ⇒ bit-identical; Horizon/Distance carry double-precision `dot` math where Burst MAY
// FMA-reorder ⇒ a ULP flip is possible ONLY at the cull boundary, so those fixture records sit CLEARLY on one
// side of their threshold (never at it) — same hazard LabelStageJobTests documents.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// <see cref="SymbolCullJob"/> — the Burst port of <c>LabelPlacementSystem.GatherSymbolPoints</c>'s Cull pass
    /// — must produce the SAME per-record <see cref="GatherTrigger"/> verdict, in the SAME chain-priority order
    /// (dropped → departing → coverage → zoom → horizon → distance → none), as an independent managed reference.
    /// </summary>
    [TestFixture]
    public class SymbolCullJobTests
    {
        // Shared per-frame scalars for every record in the fixture (one job dispatch = one frame's cull).
        private static readonly double3  SceneOriginRender    = double3.zero;
        private static readonly float3x3 Rebase               = float3x3.identity;
        private static readonly double3  CameraRelative       = double3.zero;
        // A globe centred 1000m "in front of" the camera along -z, radius 500 (radius² = 250,000) — chosen so the
        // Horizon/Distance fixture anchors below sit CLEARLY (not near) the threshold on each side.
        private static readonly double3  GlobeCentreRelative  = new double3(0, 0, -1000);
        private const double GlobeRadiusSq     = 500.0 * 500.0;
        private const double LabelCullDistance = 500.0;
        private const int    SlotCount = 3;

        // Record indices — named so the fixture and the expectation table read as one thing.
        private const int Dropped        = 0;
        private const int Departing      = 1;
        private const int Coverage       = 2;
        private const int ZoomPoint      = 3;
        private const int ZoomCurved     = 4;
        private const int Horizon        = 5;
        private const int Distance       = 6;
        private const int Kept           = 7;
        private const int OrderDropped   = 8; // Dropped AND Departing both set — pins Dropped-first
        private const int OrderCoverage  = 9; // Coverage AND Zoom both true — pins Coverage-before-Zoom
        private const int GrownListTail  = 10; // Slot >= SlotCount, stale `false` only reachable via SlotVisible.Length
        private const int RecordCount    = 11;

        private static readonly GatherTrigger[] Expected =
        {
            GatherTrigger.Dropped, GatherTrigger.Departing, GatherTrigger.Coverage,
            GatherTrigger.Zoom, GatherTrigger.Zoom,
            GatherTrigger.Horizon, GatherTrigger.Distance, GatherTrigger.None,
            GatherTrigger.Dropped, GatherTrigger.Coverage, GatherTrigger.None,
        };

        // ── Fixture construction ─────────────────────────────────────────────────────────────────────────────

        private static byte[] RecordDropped() => Flags(Dropped, OrderDropped);
        private static byte[] RecordDeparting() => Flags(Departing, OrderDropped);
        private static byte[] RecordCoverageFading() => Flags(Coverage, OrderCoverage);

        private static byte[] Flags(params int[] set)
        {
            var a = new byte[RecordCount];
            foreach (int i in set) a[i] = 1;
            return a;
        }

        // RepAnchor: only Horizon/Distance/Kept actually gate on geometry (every other record short-circuits
        // earlier in the chain, so its anchor is never read) — those three are placed CLEARLY on their side of
        // the Horizon/Distance thresholds; everyone else gets the safe (0,0,0) anchor for tidiness.
        private static double3[] RepAnchor()
        {
            var a = new double3[RecordCount];
            for (int i = 0; i < RecordCount; i++) a[i] = double3.zero;
            a[Horizon]  = new double3(0, 0, -2000); // far side of the horizon plane — clearly hidden
            a[Distance] = new double3(2000, 0, 0);  // clearly beyond LabelCullDistance, clearly NOT horizon-hidden
            return a;
        }

        // Kinds/Detail address one of two per-kind detail arrays (Points/Curveds), mirroring the mirror's own
        // Kinds/Detail split. Unused (short-circuited) records get an arbitrary valid index (Point, detail 0).
        private static byte[] Kinds()
        {
            var a = new byte[RecordCount]; // 0 == LabelRecordKind.Point everywhere by default
            a[ZoomCurved] = (byte)LabelRecordKind.Curved;
            return a;
        }

        private static int[] Detail()
        {
            var a = new int[RecordCount];
            a[ZoomPoint]     = 0; // Points[0].Slot == a slot with SlotVisible == false
            a[ZoomCurved]    = 0; // Curveds[0].Slot == a slot with SlotVisible == false
            a[Horizon]       = 1; // Points[1].Slot == a VISIBLE slot (must clear the zoom gate to reach Horizon)
            a[Distance]      = 1;
            a[Kept]          = 1;
            a[OrderCoverage] = 0; // Slot with SlotVisible == false — would ALSO be Zoom if the chain reordered
            a[GrownListTail] = 2; // Points[2].Slot == 4, only in-range if bounded by SlotVisible.Length (bug)
            return a;
        }

        private static PointStageInput[] Points() => new[]
        {
            new PointStageInput { Slot = 0 }, // gated (SlotVisible[0] == false)
            new PointStageInput { Slot = 2 }, // visible (SlotVisible[2] == true)
            new PointStageInput { Slot = 4 }, // out of SlotCount (3) — only reachable via a Length-bound bug
        };

        private static CurvedStageInput[] Curveds() => new[]
        {
            new CurvedStageInput { Slot = 1 }, // gated (SlotVisible[1] == false)
        };

        // Grown-only list LONGER than SlotCount (3): index 4 carries a stale `false` a Length-bound bug would
        // read as "gated", where the correct SlotCount-bound reading never looks at it (slot 4 >= SlotCount).
        private static bool[] SlotVisible() => new[] { false, false, true, true, false };

        // ── The independent managed reference — re-implements the chain, NOT a call into SymbolCullJob ─────────

        private static GatherTrigger ManagedVerdict(int r, byte[] dropped, byte[] departing, byte[] coverage,
            double3[] repAnchor, byte[] kinds, int[] detail, PointStageInput[] points, CurvedStageInput[] curveds,
            bool[] slotVisible)
        {
            if (dropped[r] != 0) return GatherTrigger.Dropped;
            if (departing[r] != 0) return GatherTrigger.Departing;
            if (coverage[r] != 0) return GatherTrigger.Coverage;
            if (IsOutOfLiveZoom(r, kinds, detail, points, curveds, slotVisible)) return GatherTrigger.Zoom;
            if (HorizonCull.IsHiddenBeyondHorizon(repAnchor[r], SceneOriginRender, Rebase, CameraRelative,
                                                   GlobeCentreRelative, GlobeRadiusSq))
                return GatherTrigger.Horizon;
            if (LabelFarPlaneCull.IsCulled(repAnchor[r], SceneOriginRender, Rebase, CameraRelative, LabelCullDistance))
                return GatherTrigger.Distance;
            return GatherTrigger.None;
        }

        // Bound by SlotCount, NOT slotVisible.Length — the correctness point GrownListTail pins.
        private static bool IsOutOfLiveZoom(int r, byte[] kinds, int[] detail, PointStageInput[] points,
            CurvedStageInput[] curveds, bool[] slotVisible)
        {
            if (SlotCount == 0) return false;
            int d = detail[r];
            int slot = kinds[r] == (byte)LabelRecordKind.Point ? points[d].Slot : curveds[d].Slot;
            return slot >= 0 && slot < SlotCount && !slotVisible[slot];
        }

        // ── The Burst arm — SymbolCullJob.Run() over the same fixture ───────────────────────────────────────

        private static GatherTrigger[] NativeVerdicts(byte[] dropped, byte[] departing, byte[] coverage,
            double3[] repAnchor, byte[] kinds, int[] detail, PointStageInput[] points, CurvedStageInput[] curveds,
            bool[] slotVisible)
        {
            const Allocator alloc = Allocator.TempJob;
            NativeArray<T> From<T>(T[] src) where T : unmanaged
            {
                var a = new NativeArray<T>(src.Length, alloc);
                for (int i = 0; i < src.Length; i++) a[i] = src[i];
                return a;
            }

            var nDropped = From(dropped); var nDeparting = From(departing); var nCoverage = From(coverage);
            var nAnchor = From(repAnchor); var nKinds = From(kinds); var nDetail = From(detail);
            var nPoints = From(points); var nCurveds = From(curveds); var nSlotVisible = From(slotVisible);
            var outTrigger = new NativeArray<GatherTrigger>(RecordCount, alloc);
            try
            {
                new SymbolCullJob
                {
                    RecordDropped = nDropped, RecordDeparting = nDeparting, RecordCoverageFading = nCoverage,
                    RepAnchor = nAnchor, Kinds = nKinds, Detail = nDetail, Points = nPoints, Curveds = nCurveds,
                    SlotVisible = nSlotVisible,
                    SceneOriginRender = SceneOriginRender, Rebase = Rebase, CameraRelative = CameraRelative,
                    GlobeCentreRelative = GlobeCentreRelative, GlobeRadiusSq = GlobeRadiusSq,
                    LabelCullDistance = LabelCullDistance, SlotCount = SlotCount,
                    OutTrigger = outTrigger,
                }.Run(RecordCount);

                return outTrigger.ToArray();
            }
            finally
            {
                nDropped.Dispose(); nDeparting.Dispose(); nCoverage.Dispose(); nAnchor.Dispose(); nKinds.Dispose();
                nDetail.Dispose(); nPoints.Dispose(); nCurveds.Dispose(); nSlotVisible.Dispose(); outTrigger.Dispose();
            }
        }

        [Test]
        public void Cull_MatchesManagedReference_EveryVerdictAndOrder()
        {
            byte[] dropped = RecordDropped(), departing = RecordDeparting(), coverage = RecordCoverageFading();
            double3[] repAnchor = RepAnchor();
            byte[] kinds = Kinds();
            int[] detail = Detail();
            PointStageInput[] points = Points();
            CurvedStageInput[] curveds = Curveds();
            bool[] slotVisible = SlotVisible();

            var managed = new GatherTrigger[RecordCount];
            for (int r = 0; r < RecordCount; r++)
                managed[r] = ManagedVerdict(r, dropped, departing, coverage, repAnchor, kinds, detail, points, curveds, slotVisible);

            // Sanity: the managed reference itself must match the fixture's stated expectation table — otherwise
            // a fixture bug (not a job bug) would silently pass by having both arms agree on the WRONG answer.
            CollectionAssert.AreEqual(Expected, managed, "managed reference vs. the fixture's expectation table");

            GatherTrigger[] native = NativeVerdicts(dropped, departing, coverage, repAnchor, kinds, detail, points, curveds, slotVisible);
            CollectionAssert.AreEqual(managed, native, "SymbolCullJob vs. the independent managed reference");
        }
    }
}
