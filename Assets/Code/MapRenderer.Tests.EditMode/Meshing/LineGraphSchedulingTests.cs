// job-scheduling-design.md §8 stage 5 (line graph) — acceptance teeth (a), (b), (d), (e), (g), (h).
// Teeth (c) and (f), which need the full TileManager/MapView pump, live in LineGraphKickTests.cs.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style;
using LineStyleLayer = MapRenderer.Core.Style.Line.StyleLayer;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;
using MapRenderer.Unity.Rendering.Meshing;
using MvtCommandStream = MapRenderer.Tests.Jobs.MvtCommandStream;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class LineGraphSchedulingTests
    {
        // ── Shared synthetic-fixture helpers ──────────────────────────────────────────────────────

        private static readonly TileId SyntheticTile = new TileId { Z = 10, X = 300, Y = 380 };
        private const double SyntheticExtent = 4096.0;

        /// <summary>Builds a <see cref="LayerInput"/> directly from ring point lists, bypassing MVT
        /// decode — every ring is a selected LineString feature, one feature per ring. Caller disposes the
        /// returned <see cref="LayerInput"/> (via its own <c>Dispose</c> convention: the request's
        /// owning caller frees <c>FeatureSelected</c>) and the returned <c>geometry</c>.</summary>
        private static (LayerInput input, TileGeometryBuffers geometry) SyntheticLineInput(
            IProjection projection, int maxOutputVertices, params double2[][] rings)
        {
            int ringCount = rings.Length;
            int totalVerts = 0;
            foreach (var r in rings) totalVerts += r.Length;

            var geometry = TileGeometryBuffers.Allocate(
                SyntheticTile, SyntheticExtent, featureCount: ringCount, maxRings: ringCount, maxVertices: totalVerts);
            int cursor = 0;
            for (int r = 0; r < ringCount; r++)
            {
                geometry.FeatureGeometryType[r] = TileGeometryType.LineString;
                geometry.RingFeatureIdx[r] = r;
                geometry.RingOffsets[r] = cursor;
                foreach (double2 p in rings[r]) geometry.Vertices[cursor++] = p;
            }
            geometry.RingOffsets[ringCount] = cursor;
            geometry.RingCount = ringCount;
            geometry.VertexCount = cursor;

            var featSelected = new NativeArray<bool>(ringCount, Allocator.Persistent);
            for (int i = 0; i < ringCount; i++) featSelected[i] = true;

            double3 origin = TileRenderOrigin.Project(SyntheticTile, projection);
            var input = new LayerInput
            {
                Geometry = geometry, FeatureSelected = featSelected, OriginRender = origin,
                Projection = projection, Join = JoinType.Miter, Cap = CapType.Butt,
                MiterLimit = 2.0, RoundLimit = 1.05, RoundSegments = 4,
                MaxOutputVertices = maxOutputVertices,
            };
            return (input, geometry);
        }

        private static void DisposeSynthetic(LayerInput input, TileGeometryBuffers geometry)
        {
            if (input.FeatureSelected.IsCreated) input.FeatureSelected.Dispose();
            geometry.Dispose();
        }

        // ── Tooth (b): the ring gate is the line gate, through the GRAPH ─────────────────────────────

        /// <summary>
        /// (b) job-scheduling-design.md §8 stage 5 — extends <c>StyledLineBufferParityTests</c> T2's mixed
        /// buffer (a polygon ring, a selected LineString, an exterior/hole pair) with the two cases it
        /// lacked, retargeted at <see cref="LineMeshGraph.Schedule"/> instead of the managed seam: a
        /// 2-POINT LineString (selected — line's own <c>&gt;= 2</c> threshold, never fill's <c>&gt;= 3</c>)
        /// and an UNSELECTED LineString. Only the selected LineStrings (the mixed-kind ring and the 2-point
        /// one) may produce ribbon geometry.
        /// </summary>
        [Test]
        public void LineMeshGraph_RibbonsOnlySelectedLineStrings_TwoPointRingIncluded()
        {
            var polygonRing    = MvtCommandStream.Ring(1000, 1000, 2000, 1000, 2000, 2000, 1000, 2000);
            var lineRing       = MvtCommandStream.Ring(2600, 1200, 2800, 1600, 2900, 2400);
            var twoPointRing   = new[] { new double2(500, 500), new double2(900, 900) };
            var unselectedRing = MvtCommandStream.Ring(3200, 800, 3400, 1200, 3500, 1600);

            // ordinals: 0 = polygon (SELECTED, wrong kind), 1 = line (SELECTED), 2 = twoPoint (SELECTED),
            // 3 = unselected line (NOT selected).
            var geometry = TileGeometryBuffers.Allocate(
                SyntheticTile, SyntheticExtent, featureCount: 4, maxRings: 4,
                maxVertices: polygonRing.Count + lineRing.Count + twoPointRing.Length + unselectedRing.Count);
            int cursor = 0;
            void WriteRing(int ordinal, TileGeometryType kind, IReadOnlyList<double2> pts)
            {
                geometry.FeatureGeometryType[ordinal] = kind;
                geometry.RingFeatureIdx[geometry.RingCount] = ordinal;
                geometry.RingOffsets[geometry.RingCount] = cursor;
                foreach (double2 p in pts) geometry.Vertices[cursor++] = p;
                geometry.RingCount++;
            }
            WriteRing(0, TileGeometryType.Polygon,    polygonRing);
            WriteRing(1, TileGeometryType.LineString, lineRing);
            WriteRing(2, TileGeometryType.LineString, twoPointRing);
            WriteRing(3, TileGeometryType.LineString, unselectedRing);
            geometry.RingOffsets[geometry.RingCount] = cursor;
            geometry.VertexCount = cursor;

            var featSelected = new NativeArray<bool>(4, Allocator.Persistent);
            featSelected[0] = true; featSelected[1] = true; featSelected[2] = true; featSelected[3] = false;

            var projection = new WebMercatorProjection();
            var input = new LayerInput
            {
                Geometry = geometry, FeatureSelected = featSelected,
                OriginRender = TileRenderOrigin.Project(SyntheticTile, projection), Projection = projection,
                Join = JoinType.Miter, Cap = CapType.Butt, MiterLimit = 2.0, RoundLimit = 1.05, RoundSegments = 4,
                MaxOutputVertices = LineMeshGraph.DefaultMaxOutputVertices,
            };

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.Greater(output.Vertices.Length, 0,
                    "precondition: the selected LineStrings must produce ribbon geometry");

                // Exactly two feature ordinals may appear among the produced vertices: 1 (the mixed-kind
                // ring) and 2 (the two-point ring). Ordinal 0 (polygon, wrong kind) and 3 (unselected) must
                // never appear.
                bool sawFeature1 = false, sawFeature2 = false;
                for (int i = 0; i < output.VertexFeatureIdx.Length; i++)
                {
                    int f = output.VertexFeatureIdx[i];
                    Assert.IsFalse(f == 0, "the polygon ring (wrong kind) must never contribute a vertex");
                    Assert.IsFalse(f == 3, "the unselected LineString must never contribute a vertex");
                    if (f == 1) sawFeature1 = true;
                    if (f == 2) sawFeature2 = true;
                }
                Assert.IsTrue(sawFeature1, "the selected mixed-kind LineString must ribbon");
                Assert.IsTrue(sawFeature2, "the selected 2-point LineString must ribbon — line's own >= 2 threshold");
            }
            finally
            {
                output.Dispose();
                featSelected.Dispose();
                geometry.Dispose();
            }
        }

        // ── Tooth (d): dash phase resets per ring ────────────────────────────────────────────────────

        /// <summary>
        /// (d) Over a two-ring synthetic line layer, the graph's <see cref="LineRibbonVertex.DistanceAlong"/>
        /// restarts at 0 at the first vertex of the SECOND ring and its maximum equals that ring's own arc
        /// length — never the running total across both. Two rings is the minimum that can distinguish this
        /// (one ring makes the assertion vacuous).
        /// </summary>
        [Test]
        public void LineMeshGraph_DistanceAlong_ResetsPerRing_NotAccumulatedAcrossRings()
        {
            // Two straight rings of very different lengths in tile-space X (Y constant), placed far apart so
            // they never touch: ring 0 is 10 tile-units, ring 1 is 1000 — a 100x ratio. Asserted as a RATIO
            // (never an absolute bound): DistanceAlong is measured in projected WORLD-space metres, not raw
            // tile units, and the tile→world scale factor (zoom/extent-dependent) would make a hardcoded
            // absolute threshold either wrong or accidentally load-bearing on that factor.
            var ring0 = new[] { new double2(500, 500), new double2(510, 500) };
            var ring1 = new[] { new double2(2000, 500), new double2(3000, 500) };

            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, ring0, ring1);

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.Greater(output.Vertices.Length, 0, "precondition: both rings must ribbon");

                double ring0Max = 0.0, ring1Max = 0.0;
                bool sawRing0Zero = false, sawRing1Zero = false;
                for (int i = 0; i < output.Vertices.Length; i++)
                {
                    int f = output.VertexFeatureIdx[i];
                    double d = output.Vertices[i].DistanceAlong;
                    if (f == 0) { ring0Max = math.max(ring0Max, d); if (d == 0.0) sawRing0Zero = true; }
                    else        { ring1Max = math.max(ring1Max, d); if (d == 0.0) sawRing1Zero = true; }
                }

                Assert.Greater(ring0Max, 0.0, "precondition: ring 0's own arc must be non-degenerate");

                // THIS is the assertion that actually discriminates the accumulation bug: a seeded ring 1
                // (running total carried over from ring 0) never has a vertex at EXACTLY 0, whatever ring 0's
                // own magnitude was. The ratio check below does NOT discriminate it — corrected here after
                // review found the earlier comment claimed otherwise: with seeding, ring1Max_buggy ≈
                // ring0Max + ring1's own (real) arc, which still clears a generous ratio floor over ring0Max,
                // so a ratio assertion alone would pass under the bug too. sawRing1Zero is the tooth.
                Assert.IsTrue(sawRing0Zero, "precondition: ring 0 must have a vertex at DistanceAlong == 0");
                Assert.IsTrue(sawRing1Zero,
                    "ring 1's DistanceAlong must restart at 0 — an accumulating bug would start it at " +
                    "ring 0's own max instead, never exactly 0");

                // Sanity check only, NOT a bug discriminator (see above): confirms the fixture's two rings
                // are genuinely very different lengths, so "both magnitudes happen to be similar" cannot be
                // why sawRing1Zero passed.
                Assert.Greater(ring1Max, ring0Max * 5.0,
                    $"precondition: ring 1 ({ring1Max}) must be substantially longer than ring 0 ({ring0Max}), " +
                    "or this fixture does not actually distinguish 'ring 1's own arc' from 'ring 0's'.");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
        }

        // ── Tooth (e): the vertex ceiling binds and settles as zero-vertex ───────────────────────────

        /// <summary>
        /// (e) A synthetic ring whose ribbon vertex count exceeds a deliberately tiny
        /// <see cref="LayerInput.MaxOutputVertices"/> (passed explicitly, never the production
        /// constant) trips <see cref="LineGraphCounts.ErrorLineVertexCapacity"/> and the graph produces NO
        /// vertices for it — the always-bound-loops backstop.
        /// </summary>
        [Test]
        public void LineMeshGraph_VertexCeiling_Binds_AndSettlesAsZeroVertex()
        {
            // A long zig-zag ring — round joins so each interior point emits a real (roundSegments+~7)-vertex
            // fan, comfortably exceeding a ceiling of 8.
            var pts = new double2[40];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new double2(500 + i * 20, 500 + (i % 2) * 200);

            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, maxOutputVertices: 8, pts);
            input.Join = JoinType.Round; input.RoundSegments = 8;

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();

                Assert.AreEqual(LineGraphCounts.ErrorLineVertexCapacity, output.Error.Value,
                    "a ring whose ribbon vertex count exceeds MaxOutputVertices must set the capacity error");
                // The contract (RibbonAggregateJob's own append-loop guard) checks BEFORE appending a ring
                // that would push the total over MaxOutputVertices, so a correct run never exceeds it — here,
                // with the fixture's one ring already exceeding 8 by itself, the correct result is exactly 0,
                // never a partial write. LessOrEqual(8), not a loose upper bound: an implementation that flags
                // the error and then appends anyway (e.g. writing 47 of the ring's vertices before checking)
                // must fail this, which is exactly what this tooth exists to catch.
                Assert.LessOrEqual(output.Vertices.Length, 8,
                    "the append loop must have STOPPED at the ceiling, not merely flagged the error after " +
                    "writing everything anyway");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
        }

        /// <summary>Sibling to the RED case above: the SAME ring under a generous ceiling produces real
        /// geometry with NO error — confirms the tiny ceiling in the primary tooth is what trips the flag,
        /// not something else about this fixture.</summary>
        [Test]
        public void LineMeshGraph_VertexCeiling_GenerousCeiling_ProducesRealGeometry_NoError()
        {
            var pts = new double2[40];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new double2(500 + i * 20, 500 + (i % 2) * 200);

            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, pts);
            input.Join = JoinType.Round; input.RoundSegments = 8;

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.GreaterOrEqual(output.Vertices.Length, 8 + 40,
                    "under a generous ceiling the SAME ring must produce at least as much geometry as the " +
                    "capped run was cut off at — confirms the cap, not the fixture, was the RED cause above");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
        }

        // ── Tooth (g): winding, through the graph's generic entry ───────────────────────────────────

        /// <summary>
        /// (g) <c>RightHandedSphereProjectionWindingTests</c>' decisive projection, driven through
        /// <see cref="LineMeshGraph.ScheduleTyped{TProj}"/> — a generic entry Burst reaches with NOTHING
        /// registered for this projection type — must still wind the same as flat Mercator. Mirrors that
        /// test's own reconstruction (<c>GlobeLineWindingTests.RibbonWindingSign</c>).
        /// </summary>
        [Test]
        public void LineMeshGraph_RightHandedCurvedProjection_ThroughGenericScheduleTyped_WindsSameAsMercator()
        {
            // A curved-enough polyline (several segments spanning real angular distance) so the winding
            // reconstruction has non-degenerate join triangles to measure.
            var pts = new double2[6];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new double2(500 + i * 500, 500 + math.sin(i) * 400);

            var flatProjection = new WebMercatorProjection();
            (LayerInput flatInput, TileGeometryBuffers flatGeometry) =
                SyntheticLineInput(flatProjection, LineMeshGraph.DefaultMaxOutputVertices, pts);

            var rhProjection = new RightHandedSphereTestProjection();
            (LayerInput rhInput, TileGeometryBuffers rhGeometry) =
                SyntheticLineInput(rhProjection, LineMeshGraph.DefaultMaxOutputVertices, pts);

            LineGraphOutput flatOutput = default, rhOutput = default;
            try
            {
                flatOutput = LineMeshGraph.Schedule(flatInput);
                flatOutput.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, flatOutput.Error.Value);
                Assert.Greater(flatOutput.Vertices.Length, 0);

                // The decisive call: the generic entry point, bypassing Schedule's closed switch — the
                // shape a projection Burst never registered generically needs.
                rhOutput = LineMeshGraph.ScheduleTyped(rhInput, rhProjection, default);
                rhOutput.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, rhOutput.Error.Value);
                Assert.Greater(rhOutput.Vertices.Length, 0);

                var (flatSign, flatUniformity) = RibbonWindingSign(flatOutput);
                var (rhSign, rhUniformity) = RibbonWindingSign(rhOutput);

                Assert.Greater(flatUniformity, 0.99, "Mercator ribbon winding must be uniform");
                Assert.Greater(rhUniformity, 0.99, "right-handed curved ribbon winding must be uniform");
                Assert.AreEqual(flatSign, rhSign,
                    "a RIGHT-handed curved projection must wind the SAME as Mercator relative to the surface " +
                    "normal — winding is derived from cross(along, up), not from curvature/handedness.");
            }
            finally
            {
                flatOutput.Dispose(); rhOutput.Dispose();
                DisposeSynthetic(flatInput, flatGeometry);
                DisposeSynthetic(rhInput, rhGeometry);
            }
        }

        /// <summary>Tallies the sign of the angle between each triangle's face normal and its surface
        /// normal, over the graph's own pre-write ribbon vertices/indices — the graph-side twin of
        /// <c>GlobeLineWindingTests.RibbonWindingSign</c> (which reads a finished <c>Mesh</c>).</summary>
        private static (int sign, double uniformity) RibbonWindingSign(LineGraphOutput output)
        {
            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < output.Indices.Length; i += 3)
            {
                int ia = output.Indices[i], ib = output.Indices[i + 1], ic = output.Indices[i + 2];
                LineRibbonVertex va = output.Vertices[ia], vb = output.Vertices[ib], vc = output.Vertices[ic];
                double3 pa = va.Position, pb = vb.Position, pc = vc.Position;
                double d = math.max(math.distance(pa, pb), math.max(math.distance(pb, pc), math.distance(pc, pa)));
                if (d < 1e-6) continue;
                double w = 0.05 * d;
                double3 ea = pa + va.Across * w, eb = pb + vb.Across * w, ec = pc + vc.Across * w;
                double3 g = math.cross(eb - ea, ec - ea);
                double3 n = va.Up;
                double gm = math.length(g), nm = math.length(n);
                if (gm <= 0.0 || nm <= 0.0) continue;
                double cos = math.dot(g, n) / (gm * nm);
                if (math.abs(cos) < 0.5) continue;
                if (cos > 0.0) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate ribbon triangles to measure");
            int sign = pos >= neg ? 1 : -1;
            return (sign, (double)math.max(pos, neg) / counted);
        }

        // ── Tooth (h): lifetime — the pen at every step, and the counters return to zero ─────────────

        /// <summary>
        /// (h) A line request's owned columns are counted live at <see cref="LineMeshGraph.Schedule"/> and
        /// freed at <see cref="LineGraphOutput.Dispose"/> — <see cref="LineGraphOutput.DebugLiveCount"/>
        /// returns to baseline, and <see cref="LineGraphOutput.DebugBuffersAllocated"/> /
        /// <see cref="LineGraphOutput.DebugBufferDisposeNodes"/> stay paired (the non-vacuity witness: both
        /// must have ADVANCED by the same nonzero amount, not merely stayed equal at their starting value).
        /// </summary>
        [Test]
        public void LineGraphOutput_Dispose_ReturnsLiveCountToBaseline_BuffersPaired()
        {
            var ring = new[] { new double2(500, 500), new double2(900, 900), new double2(1200, 600) };
            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, ring);

            long liveBaseline = LineGraphOutput.DebugLiveCount;
            long allocBaseline = LineGraphOutput.DebugBuffersAllocated;
            long disposeBaseline = LineGraphOutput.DebugBufferDisposeNodes;

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                Assert.Greater(LineGraphOutput.DebugLiveCount, liveBaseline,
                    "non-vacuity: Schedule must have counted its output containers live before completion");

                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.Greater(output.Vertices.Length, 0, "precondition: real geometry, or the pen below proves nothing");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }

            Assert.AreEqual(liveBaseline, LineGraphOutput.DebugLiveCount,
                "Dispose must free every output container Schedule counted live");
            long allocDelta = LineGraphOutput.DebugBuffersAllocated - allocBaseline;
            long disposeDelta = LineGraphOutput.DebugBufferDisposeNodes - disposeBaseline;
            Assert.Greater(allocDelta, 0, "non-vacuity: real scratch buffers must have been allocated");
            Assert.AreEqual(allocDelta, disposeDelta,
                "every scratch buffer NewBuffer<T> allocated must have a matching ScheduleDispose<T> node");
        }

        /// <summary>Regression injection target for the RED half of tooth (h) — see the RED-verification
        /// note this test's own report cites. Each iteration completes and disposes its own output before
        /// the next starts (no handle held across iterations); a future developer can reproduce the RED by
        /// holding <c>Error</c> undisposed inside <see cref="LineGraphOutput.Dispose"/> (comment out one
        /// Dispose line) and re-running.</summary>
        [Test]
        public void LineGraphOutput_RepeatedSchedule_NeverLeaksAcrossCalls()
        {
            var ring = new[] { new double2(500, 500), new double2(900, 900) };
            var projection = new WebMercatorProjection();

            long liveBaseline = LineGraphOutput.DebugLiveCount;
            for (int iter = 0; iter < 5; iter++)
            {
                (LayerInput input, TileGeometryBuffers geometry) =
                    SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, ring);
                LineGraphOutput output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
            Assert.AreEqual(liveBaseline, LineGraphOutput.DebugLiveCount,
                "five schedule/dispose cycles must return the live count to baseline — a per-call leak would " +
                "accumulate visibly here even if any single cycle's own before/after looked balanced.");
        }
    }

    /// <summary>A curved, RIGHT-handed test projection — the mirror of <see cref="SphericalProjection"/>'s
    /// left-handed axis swap (un-swapped ECEF: <c>World = (x, y, z)</c>, det(TangentBasis) = +1). Only the
    /// geometry-side members are real; camera-interaction members throw. A LOCAL copy of
    /// <c>RightHandedSphereProjection</c> (Globe/RightHandedSphereProjectionWindingTests.cs) rather than a
    /// shared reference — that type lives in a different namespace and this file's own decisive property
    /// (never registered for Burst) does not depend on sharing the instance.</summary>
    internal readonly struct RightHandedSphereTestProjection : IProjection
    {
        public const double Radius = EarthConstants.A;

        public ProjectedPoint ProjectPoint(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);
            double upX = cosPhi * cosLam, upY = cosPhi * sinLam, upZ = sinPhi;
            return new ProjectedPoint
            {
                World = new double3(upX * Radius, upY * Radius, upZ * Radius),
                Up    = new double3(upX, upY, upZ),
            };
        }

        public double3 Project(in GeoCoordinate geo) => ProjectPoint(geo).World;
        public double3 UpAt(in GeoCoordinate geo)    => ProjectPoint(geo).Up;

        public float3x3 TangentBasisAt(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);
            double3 up   = new double3(cosPhi * cosLam, cosPhi * sinLam, sinPhi);
            double3 east = new double3(-sinLam, cosLam, 0.0);
            double3 north = math.cross(up, east);
            return new float3x3((float3)east, (float3)up, (float3)north);
        }

        public double MetersPerUnit     => 1.0;
        public double MaxRefineAngleRad => SphericalProjection.MaxCurveSegmentRad;

        public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
        {
            renderCentre = default; radius = 0.0; return false;
        }

        public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in MapRenderer.Core.View.Camera.CameraProperties camera)
            => throw new NotSupportedException("RightHandedSphereTestProjection is a geometry-only test double.");
        public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in MapRenderer.Core.View.Camera.CameraProperties camera)
            => throw new NotSupportedException("RightHandedSphereTestProjection is a geometry-only test double.");
        public double ClampValidLatitude(double latitudeDegrees) => math.clamp(latitudeDegrees, -90.0, 90.0);
        public bool IsFinitePlanarWorld => false;
        public GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in MapRenderer.Core.View.Camera.CameraProperties camera)
            => throw new NotSupportedException("RightHandedSphereTestProjection is a geometry-only test double.");
    }
}
