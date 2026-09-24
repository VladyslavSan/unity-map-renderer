// Text/Placement/SymbolStagingMathTests.cs — the engine-free symbol staging/placement math: billboard quads, cross-tile identity, horizon/far-plane cull, line anchors, curved-polyline arc math, bearing, candidate tiling, collision order, pairing, screen projection, and the string table (fast lane: engine-free, compiled by Tools/core-tests too).
//
// Grouped by what they exercise: billboard/cross-tile/horizon-cull math, then line-anchor and curved-polyline arc math, then the smaller pure-math fixtures (bearing, candidate tiling, collision order, far-plane cull, pairing, screen projection), then the staging-math family (curved up/vertex, sort-key, core staging), then string table and tile coverage/translate.
//
// Contents:
//   BillboardMathTests                  — BuildWorldQuad golden (element-by-element corners/UVs, up to the Y-negation) + AnchorLocal/Tangent/AlignFlags carry- through.
//   CrossTileIdentityTests              — cross-tile point-symbol identity (CrossTileSymbolKey) — the For-math primitive, engine-free.
//   HorizonCullTests                    — the globe far-side horizon cull — a polar-plane test, EXACT for on-surface points (globe symbol anchors).
//   LineAnchorPlacementTests            — Compute places along-line anchors ONCE in tile space, as stable LineAnchor topology.
//   PolylineArcMathScreenTests          — the geometric core of curved text: walk a screen polyline by arc length, get point + tangent.
//   PolylineArcMathWorldSampleTests     — SegmentAt (the resumable segment search factored out of At) and SampleWorld (the double3 world-polyline sampler at an already-resolved (seg,t)) — the seam SymbolStagingMath.StageCurvedAnchor uses to bake a per-glyph world…
//   SymbolBearingTests                  — the alignment→billboard-rotation mapping.
//   SymbolCandidateRangeTilingTests     — Engine-free (pure Core types + NUnit) — shared verbatim between the Unity EditMode runner and the fast dotnet core-tests project.
//   SymbolCollisionTests                — The one direct tooth for ComparePlacementOrder: the placement order key is (SortKey, WasPlacedLastFrame, FeatureIndex, TileKey, FadeId), each term breaking a tie left by the one before it.
//   SymbolFarPlaneCullTests             — The Core distance math for the pre-projection far-plane symbol cull: a symbol farther from the camera than the cull distance is culled; a non-positive distance disables it; and the per-frame rebase rotation is applied to the anchor (so the cull measures…
//   SymbolPairingTests                  — Engine-free (pure Core types + NUnit) — shared verbatim between the Unity EditMode runner and the fast dotnet core-tests project.
//   SymbolScreenProjectionTests         — TryProjectAnchor golden + culling, over a hand-built view-projection matrix chosen so every value is hand-computable (no live camera needed — the EditMode-only SymbolScreenProjectionUnityTests cross-checks against a REAL…
//   SymbolStagingMathCurvedUpTests      — pins PolylineArcMath.SampleUp's per-glyph sample EXACTLY — a large (90 deg), synthetic per-vertex up span, so the difference is not float noise.
//   SymbolStagingMathCurvedVertexTests  — Root-cause regression for the curved-symbol glyph-overlap defect: a glyph straddling a polyline VERTEX must be rotated by the CHORD across its own footprint (blending the two segment angles either side of the vertex), not the raw single-segment tangent a…
//   SymbolStagingMathSortKeyTests       — SanitizeSortKey normalizes a non-finite baked symbol-sort-key to float.MaxValue (sorts LAST) so ComparePlacementOrder stays a strict total order.
//   SymbolStagingMathTests              — the pure blittable-input staging math extracted from SymbolPlacementSystem.
//   SymbolStringTableTests              — SymbolStringTable — the string→int table that lets the cross-tile dedup key partition by an integer id instead of a string hash.
//   SymbolTileCoverageFilterTests       — ClassifyActive — the per-block tile-coverage classifier that WRITES a per-record Keep / Fade / Drop decision (a tile that WAS on screen fades out instead of popping) without compacting the record list.
//   SymbolTileCoverageTests             — The per-tile screen-coverage pre-cull metric.
//   SymbolTranslateTests                — the pure text-translate screen-delta math (the engine-free half; the SymbolPlacementSystem.Tick integration is proven separately in EditMode).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Geo;
using System.Collections.Generic;
using System;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Unity.View;


namespace MapRenderer.Tests.Text.Placement
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // BillboardMathTests — BuildWorldQuad golden + AnchorLocal/Tangent/AlignFlags carry-through
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="BillboardMath.BuildWorldQuad"/> golden
    /// (element-by-element corners/UVs, up to the Y-negation) + AnchorLocal/Tangent/AlignFlags carry-
    /// through. <c>BuildWorldQuad</c> is the only production consumer; the screen-space <c>BuildQuad</c>
    /// and the render path it built for are both gone.
    /// </summary>
    [TestFixture]
    public class BillboardMathTests
    {
        // `in default(float3)` isn't addressable (CS8156) — a static readonly field is.
        private static readonly float3 ZeroFloat3 = new float3(0f, 0f, 0f);

        private static SymbolQuad MakeQuad()
            => new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f),
                BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.10f, 0.20f),
                UvBottomRight = new float2(0.30f, 0.50f),
                LineIndex = 0,
            };

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // BuildWorldQuad — the world-anchored sibling of BuildQuad.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        // ── Golden at unit scale, no rotation/translate, anchor 0: the corners are MakeQuad()'s baked-px corners
        //    with Y negated, and AnchorLocal/Page/AlignFlags carry through. ──
        [Test]
        public void BuildWorldQuad_UnitScale_NoRotationNoTranslate_MatchesBuildQuad_WithA0F2YNegation()
        {
            SymbolQuad quad = MakeQuad();
            var anchorLocal = new float3(10f, 20f, 30f);
            var colorRgb = new float3(0.2f, 0.4f, 0.6f);

            // The anchor-relative corners BuildQuad would have produced for MakeQuad() at unit scale, zero
            // rotation, anchor (0,0): TopLeft/BottomRight verbatim, TopRight/BottomLeft cross the two.
            var expectedTl = quad.TopLeft;
            var expectedTr = new float2(quad.BottomRight.x, quad.TopLeft.y);
            var expectedBr = quad.BottomRight;
            var expectedBl = new float2(quad.TopLeft.x, quad.BottomRight.y);

            BillboardMath.BuildWorldQuad(in quad, in anchorLocal, TextQuadLayout.OneEm, in colorRgb, 0f, in float2.zero,
                in ZeroFloat3, in ZeroFloat3, 0f,
                out WorldBillboardVertex worldTl, out WorldBillboardVertex worldTr,
                out WorldBillboardVertex worldBr, out WorldBillboardVertex worldBl);

            foreach ((float2 expectedScreenPx, WorldBillboardVertex world, float2 expectedUv, string label) in new[]
                     {
                         (expectedTl, worldTl, quad.UvTopLeft, "topLeft"),
                         (expectedTr, worldTr, new float2(quad.UvBottomRight.x, quad.UvTopLeft.y), "topRight"),
                         (expectedBr, worldBr, quad.UvBottomRight, "bottomRight"),
                         (expectedBl, worldBl, new float2(quad.UvTopLeft.x, quad.UvBottomRight.y), "bottomLeft"),
                     })
            {
                Assert.AreEqual(expectedScreenPx.x, world.Offset.x, 1e-5f, $"{label}: Offset.x matches BuildQuad's ScreenPx.x (no translate offset here — anchor is 0)");
                Assert.AreEqual(-expectedScreenPx.y, world.Offset.y, 1e-5f, $"{label}: Offset.y is the NEGATION of BuildQuad's ScreenPx.y");
                Assert.AreEqual(expectedUv.x, world.Uv.x, 1e-6f, $"{label}: UV stays attached to its ORIGINAL corner (no flip)");
                Assert.AreEqual(expectedUv.y, world.Uv.y, 1e-6f, $"{label}: UV.y unflipped too");
                Assert.AreEqual(anchorLocal, world.AnchorLocal, $"{label}: AnchorLocal is the same on every corner");
                Assert.AreEqual(0f, world.AlignFlags, $"{label}: AlignFlags stays 0 (viewport-aligned only; the map-bearing bit)");
                Assert.AreEqual(ZeroFloat3, world.Tangent, $"{label}: Tangent stays zero for a point/icon caller (unread by the shader when AlignFlags bit1 is clear)");
            }
        }

        // ── Text-translate is an ADDITIVE, UNROTATED corner offset with the corner's Y-negation: every corner
        //    shifts by translateDeltaPx once. ──
        [Test]
        public void BuildWorldQuad_TranslateDelta_ShiftsEveryCorner_UnrotatedWithSameYNegation()
        {
            SymbolQuad quad = MakeQuad();
            var translateDeltaPx = new float2(7f, -3f);

            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in float2.zero,
                in ZeroFloat3, in ZeroFloat3, 0f,
                out WorldBillboardVertex tl0, out WorldBillboardVertex tr0, out WorldBillboardVertex br0, out WorldBillboardVertex bl0);
            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in translateDeltaPx,
                in ZeroFloat3, in ZeroFloat3, 0f,
                out WorldBillboardVertex tl1, out WorldBillboardVertex tr1, out WorldBillboardVertex br1, out WorldBillboardVertex bl1);

            var expectedDelta = new float2(translateDeltaPx.x, -translateDeltaPx.y); // same negation as the corner
            foreach ((WorldBillboardVertex before, WorldBillboardVertex after, string label) in new[]
                     {
                         (tl0, tl1, "topLeft"), (tr0, tr1, "topRight"), (br0, br1, "bottomRight"), (bl0, bl1, "bottomLeft"),
                     })
            {
                Assert.AreEqual(expectedDelta.x, after.Offset.x - before.Offset.x, 1e-5f, $"{label}: translate delta x");
                Assert.AreEqual(expectedDelta.y, after.Offset.y - before.Offset.y, 1e-5f, $"{label}: translate delta y");
            }
        }

        // ── Colour carries VERBATIM — no sRGB→linear (or any other) conversion. A non-trivial,
        //    non-black/non-white colour in must come out byte-identical, on every corner. ──
        [Test]
        public void BuildWorldQuad_ColorRgb_CarriesVerbatim_NoGammaConversion()
        {
            SymbolQuad quad = MakeQuad();
            var colorRgb = new float3(0.73f, 0.11f, 0.42f); // non-trivial — a double-convert would move this

            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in colorRgb, 0f, in float2.zero,
                in ZeroFloat3, in ZeroFloat3, 0f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out WorldBillboardVertex br, out WorldBillboardVertex bl);

            foreach (WorldBillboardVertex v in new[] { tl, tr, br, bl })
            {
                Assert.AreEqual(colorRgb.x, v.ColorRGB.x, 1e-6f, "R carries verbatim");
                Assert.AreEqual(colorRgb.y, v.ColorRGB.y, 1e-6f, "G carries verbatim");
                Assert.AreEqual(colorRgb.z, v.ColorRGB.z, 1e-6f, "B carries verbatim");
            }
        }

        // ── Winding: TL/TR/BR/BL matches BuildQuad's convention exactly (same corner→attribute mapping) —
        //    WorldSymbolRenderer's index emit (TL,TR,BR / TL,BR,BL) depends on this. ──
        [Test]
        public void BuildWorldQuad_Winding_MatchesBuildQuadConvention()
        {
            SymbolQuad quad = MakeQuad();
            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in float2.zero,
                in ZeroFloat3, in ZeroFloat3, 0f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out WorldBillboardVertex br, out WorldBillboardVertex bl);

            // TR shares BR's baked x / TL's baked y (mirrors BuildQuad's trLocal = (brLocal.x, tlLocal.y)) —
            // after negation, TR.Offset.y == TL.Offset.y (both share the top edge).
            Assert.AreEqual(tl.Offset.y, tr.Offset.y, 1e-5f, "TL/TR share the top edge (same Y)");
            Assert.AreEqual(br.Offset.y, bl.Offset.y, 1e-5f, "BR/BL share the bottom edge (same Y)");
            Assert.AreEqual(tl.Offset.x, bl.Offset.x, 1e-5f, "TL/BL share the left edge (same X)");
            Assert.AreEqual(tr.Offset.x, br.Offset.x, 1e-5f, "TR/BR share the right edge (same X)");
        }

        // ── tangentLocal, surfaceUp and alignFlags ride VERBATIM onto every corner: curved's discriminator
        //    (bit1 set, a nonzero Tangent) must not be dropped or blended per-corner. ──
        [Test]
        public void BuildWorldQuad_TangentLocalAndAlignFlags_CarryVerbatimToEveryCorner()
        {
            SymbolQuad quad = MakeQuad();
            var tangentLocal = new float3(0.6f, 0.0f, 0.8f); // a unit-ish along-line direction
            var surfaceUp = new float3(0.0f, 0.6f, 0.8f);    // distinct from tangentLocal
            const float alongLineAlignFlags = 2f; // D-I bit1

            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in float2.zero,
                in tangentLocal, in surfaceUp, alongLineAlignFlags,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out WorldBillboardVertex br, out WorldBillboardVertex bl);

            foreach ((WorldBillboardVertex v, string label) in new[] { (tl, "topLeft"), (tr, "topRight"), (br, "bottomRight"), (bl, "bottomLeft") })
            {
                Assert.AreEqual(tangentLocal, v.Tangent, $"{label}: Tangent carries verbatim to every corner");
                Assert.AreEqual(surfaceUp, v.Up, $"{label}: Up carries verbatim to every corner");
                Assert.AreEqual(alongLineAlignFlags, v.AlignFlags, $"{label}: AlignFlags carries verbatim to every corner");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // CrossTileIdentityTests — the For-math primitive, engine-free
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cross-tile point-symbol identity (<see cref="CrossTileSymbolKey.For"/>), a general grid primitive
    /// asserted at an explicit <c>quantizeMeters</c>: (1) the grid is COARSE ENOUGH that a point's parent (z10)
    /// and child (z11) reprojections share a cell; (2) FINE ENOUGH that symbols a few cells apart keep
    /// different keys; plus the 3-axis (globe Y), icon, and text parity cases. The store dedup cases live in
    /// <c>CrossTileIdentityStoreTests</c>, because the store needs <c>Unity.Collections</c>.
    /// </summary>
    [TestFixture]
    public class CrossTileIdentityTests
    {
        // ── (1) One location MVT-quantized in a z10 tile and its z11 child lands within ONE display-zoom pixel,
        //    so a grid of GroundResolution at the coarser zoom collapses them. ──
        [Test]
        public void AdjacentZoomReprojection_LandsWithinOneDisplayPixelCell()
        {
            var proj = new WebMercatorProjection();
            const double extent = 4096;
            // z11 (1000,800) is the top-left child of z10 (500,400). Slightly different local coords model the
            // two tiles' independent MVT quantization of the same feature.
            var z11 = new TileId { Z = 11, X = 1000, Y = 800 };
            var z10 = new TileId { Z = 10, X = 500, Y = 400 };
            double2 g11 = z11.ToLonLat(101, 101, extent);
            double2 g10 = z10.ToLonLat(50, 50, extent);
            double3 a11 = proj.Project(new GeoCoordinate { Latitude = g11.y, Longitude = g11.x });
            double3 a10 = proj.Project(new GeoCoordinate { Latitude = g10.y, Longitude = g10.x });

            double q = WebMercator.GroundResolution(10); // one logical px at the coarser (display) zoom
            Assert.Less(math.abs(a10.x - a11.x), q, "the parent/child anchor x-diff is under one display pixel");
            Assert.Less(math.abs(a10.z - a11.z), q, "the parent/child anchor z-diff is under one display pixel");

            // …and 1px-at-MAX-zoom (the doc's original number) is FAR too fine to collapse it — the tooth that
            // proves the granularity had to be display-relative, not a fixed max-zoom grid.
            double qMax = WebMercator.GroundResolution(22);
            Assert.Greater(math.abs(a10.x - a11.x), qMax, "a max-zoom grid would NOT collapse the diff (why display-zoom)");
        }

        // ── On the globe, 30°N and 30°S at one longitude share render X/Z (∝ cosφ, nonzero here) and differ
        //    only in Y (= sinφ·R), so an x/z-only key would collide them into one symbol. ──
        [Test]
        public void GlobeEquatorMirroredAnchors_AreDistinct()
        {
            var proj = new SphericalProjection();
            double3 render30N = proj.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 45.0 });
            double3 render30S = proj.Project(new GeoCoordinate { Latitude = -30.0, Longitude = 45.0 });
            double q = CameraPoseMath.MetersPerPixel(6.0);

            Assert.AreNotEqual(CrossTileSymbolKey.For(render30N, 0, "X", null, q), CrossTileSymbolKey.For(render30S, 0, "X", null, q),
                "30°N and 30°S at the same longitude share render X/Z but must stay distinct labels (the Y axis)");
        }

        // ── (1b)/(2) synthetic mid-cell control: within a cell → same key; a few cells away → different key
        //    (deterministic, no projection-boundary flakiness). ──
        [Test]
        public void Quantization_CollapsesWithinCell_SeparatesBeyond()
        {
            const double q = 50.0;
            double3 mid = new double3(q * 100.0, 0, q * 100.0); // a cell CENTRE (k·q), safe from a round boundary
            double3 near = mid + new double3(3.0, 0, -3.0);     // a few metres → same cell
            double3 far = mid + new double3(3.0 * q, 0, 0);     // 3 cells away → different

            Assert.AreEqual(CrossTileSymbolKey.For(mid, 0, "T", null, q), CrossTileSymbolKey.For(near, 0, "T", null, q),
                "anchors within a grid cell share one identity");
            Assert.AreNotEqual(CrossTileSymbolKey.For(mid, 0, "T", null, q), CrossTileSymbolKey.For(far, 0, "T", null, q),
                "anchors several cells apart are distinct labels");
        }

        // ── (2b) same cell, DIFFERENT text → different identity (full-text equality, not a hash collision). ──
        [Test]
        public void SameCellDifferentText_AreDistinct()
        {
            const double q = 50.0;
            double3 a = new double3(q * 10.5, 0, q * 10.5);
            Assert.AreNotEqual(CrossTileSymbolKey.For(a, 0, "Paris", null, q), CrossTileSymbolKey.For(a, 0, "Lyon", null, q));
            Assert.AreNotEqual(CrossTileSymbolKey.For(a, 0, "Paris", null, q), CrossTileSymbolKey.For(a, 1, "Paris", null, q),
                "same anchor+text on a different layer is a different label");
        }

        // ── (a) two icon symbols (text=null) at the SAME cell/layer but DIFFERENT icon-image must stay
        //    distinct: a key that ignored the icon would collide them on text==null. ──
        [Test]
        public void IconIdentity_DistinctIconImage_AreDistinct()
        {
            const double q = 50.0;
            double3 a = new double3(q * 10.5, 0, q * 10.5);
            var keyA = CrossTileSymbolKey.For(a, 0, null, "sprite-a", q);
            var keyB = CrossTileSymbolKey.For(a, 0, null, "sprite-b", q);
            Assert.AreNotEqual(keyA, keyB, "same cell/layer, different icon-image → distinct identity");
            Assert.AreNotEqual(keyA.GetHashCode(), keyB.GetHashCode(), "…and distinct hashes");
        }

        // ── (b) the SAME icon-image across the parent/child anchor from (1) collapses to one identity, so
        //    icons get the same seamless-swap dedup as text. ──
        [Test]
        public void IconIdentity_SameIconAcrossParentChildAnchor_AreEqual()
        {
            var proj = new WebMercatorProjection();
            const double extent = 4096;
            var z11 = new TileId { Z = 11, X = 1000, Y = 800 };
            var z10 = new TileId { Z = 10, X = 500, Y = 400 };
            double2 g11 = z11.ToLonLat(101, 101, extent);
            double2 g10 = z10.ToLonLat(50, 50, extent);
            double3 a11 = proj.Project(new GeoCoordinate { Latitude = g11.y, Longitude = g11.x });
            double3 a10 = proj.Project(new GeoCoordinate { Latitude = g10.y, Longitude = g10.x });
            double q = WebMercator.GroundResolution(10);

            Assert.AreEqual(CrossTileSymbolKey.For(a10, 0, null, "sprite-a", q), CrossTileSymbolKey.For(a11, 0, null, "sprite-a", q),
                "the same icon reprojected parent→child lands in the same identity cell");
        }

        // ── (c) text parity: two text keys (IconImage explicitly null) are equal + hash equal — the guard-skip
        //    fold must not perturb the text-only path. ──
        [Test]
        public void TextParity_ExplicitNullIconImage_EqualsAndHashesSame()
        {
            const double q = 50.0;
            double3 a = new double3(q * 3.5, 0, q * 3.5);
            var k1 = CrossTileSymbolKey.For(a, 0, "Paris", null, q);
            var k2 = CrossTileSymbolKey.For(a, 0, "Paris", null, q);
            Assert.AreEqual(k1, k2);
            Assert.AreEqual(k1.GetHashCode(), k2.GetHashCode());
        }

    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // HorizonCullTests — the globe far-side horizon cull, exact for on-surface points
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The globe far-side horizon cull — a polar-plane test, EXACT for on-surface points (globe symbol
    /// anchors). Teeth: near-side kept / far-side hidden, cross-checked against an INDEPENDENT ray-sphere
    /// oracle (NOT the horizon half-angle test — that is algebraically identical to
    /// <c>dot &lt; radius²</c> and would be a tautological check); the planar no-op (<c>globeRadiusSq &lt; 0</c>)
    /// is unconditional, no state read.
    /// </summary>
    [TestFixture]
    public class HorizonCullTests
    {
        [Test]
        public void LookAtAnchor_WithCameraOverhead_IsNotHidden()
        {
            // Rebased P = (0,0,0), cam = (0,altitude,0), centre = (0,-R,0): dot(P-C, cam-C) = R·(altitude+R) > R²
            // for any altitude > 0, so the look-at under the camera is never hidden.
            const double r = 6378137.0, altitude = 500.0;
            var origin = new double3(0, 0, 0);
            var cam = new double3(0, altitude, 0);
            var centre = new double3(0, -r, 0);

            bool hidden = HorizonCull.IsHiddenBeyondHorizon(
                origin, origin, float3x3.identity, cam, centre, r * r);

            Assert.IsFalse(hidden, "the look-at anchor sits directly under the camera — never hidden");
        }

        [Test]
        public void PlanarProjection_NegativeRadiusSq_IsAlwaysUnhidden()
        {
            // The Mercator no-op path passes globeRadiusSq = -1, so nonsensical geometry must not flip the
            // unconditional false.
            bool hidden = HorizonCull.IsHiddenBeyondHorizon(
                new double3(1e9, 1e9, 1e9), new double3(-1e9, -1e9, -1e9), float3x3.identity,
                new double3(0, 0, 0), new double3(0, 0, 0), -1.0);

            Assert.IsFalse(hidden, "globeRadiusSq < 0 must always be a no-op");
        }

        // ── Independent oracle: ray-sphere intersection, a different formula from the plane test. p lies ON the
        //    sphere, so cam + t·(p − cam) has a root at t = 1; a NEAR root before t = 1 means the sphere blocks p.
        //    Non-obvious why: for a visible p, tNear == 1 is exact, so the tolerance only has to clear the
        //    float32 rebase noise; 1e-4 does, and stays under the real gap outside the ≥5° collar the sweep keeps.
        private const double OracleEpsilon = 1e-4;

        private static bool RaySphereOccluded(double3 cam, double3 p, double3 centre, double radius)
        {
            double3 d = p - cam;
            double3 oc = cam - centre;
            double a = math.dot(d, d);
            double b = 2.0 * math.dot(oc, d);
            double c = math.dot(oc, oc) - radius * radius;
            double disc = b * b - 4.0 * a * c;
            if (disc < 0.0) return false; // ray misses the sphere entirely (shouldn't happen — p is on it)
            double tNear = (-b - math.sqrt(disc)) / (2.0 * a);
            return tNear < 1.0 - OracleEpsilon;
        }

        // Shared globe rig: look-at at (0°,0°), camera overhead at 2R (horizon half-angle acos(1/3) ≈ 70.53°);
        // heading/tilt 0 keeps it on +Y, matching TryGetHorizonOccluder's (0,-R,0) centre.
        private static void BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender,
            out float3x3 rebase, out double3 camRelative, out double3 centreRelative, out double radiusSq)
        {
            proj = new SphericalProjection();
            var lookAt = new GeoCoordinate { Latitude = 0.0, Longitude = 0.0 };
            double altitude = 2.0 * SphericalProjection.Radius;

            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0), Angle.FromDegrees(0),
                out camRelative, out _, out _);

            sceneOriginRender = proj.Project(lookAt);
            rebase = math.transpose(proj.TangentBasisAt(lookAt));
            proj.TryGetHorizonOccluder(out centreRelative, out double radius);
            radiusSq = radius * radius;
        }

        [Test]
        public void FarAndNearSideAnchors_AgreeWithIndependentRaySphereOracle()
        {
            BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender, out float3x3 rebase,
                out double3 camRelative, out double3 centreRelative, out double radiusSq);
            double radius = math.sqrt(radiusSq);

            // Points near the horizon angle sit on a near-tangent ray, ill-conditioned for ANY numeric method,
            // so the sweep skips a 5° collar; the near-limb test probes just outside it.
            double horizonDeg = math.acos(1.0 / 3.0) * 180.0 / math.PI_DBL;

            bool sawHidden = false, sawVisible = false;
            for (double lon = -180.0; lon <= 180.0; lon += 5.0)
            {
                if (math.abs(math.abs(lon) - horizonDeg) < 5.0) continue; // skip the near-tangent collar

                var geo = new GeoCoordinate { Latitude = 0.0, Longitude = lon };
                double3 anchorRender = proj.Project(geo);

                bool predicate = HorizonCull.IsHiddenBeyondHorizon(
                    anchorRender, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);

                // Rebase the anchor into the SAME frame the oracle needs — mirrors the seam inside HorizonCull.
                double3 local = anchorRender - sceneOriginRender;
                var localF = new float3((float)local.x, (float)local.y, (float)local.z);
                float3 rebasedF = math.mul(rebase, localF);
                var anchorRebased = new double3(rebasedF.x, rebasedF.y, rebasedF.z);

                bool oracle = RaySphereOccluded(camRelative, anchorRebased, centreRelative, radius);

                Assert.AreEqual(oracle, predicate, $"lon={lon}: plane test vs ray-sphere oracle disagree");
                if (predicate) sawHidden = true; else sawVisible = true;
            }

            Assert.IsTrue(sawHidden, "the sweep must include far-side (hidden) longitudes");
            Assert.IsTrue(sawVisible, "the sweep must include near-side (visible) longitudes");
        }

        [Test]
        public void NearLimbAnchors_JustInsideAndJustBeyondHorizon_AgreeWithOracle()
        {
            // A near-limb pair straddling the horizon, just outside the ill-conditioned collar the sweep skips,
            // where both the plane test and the oracle still resolve it.
            BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender, out float3x3 rebase,
                out double3 camRelative, out double3 centreRelative, out double radiusSq);
            double radius = math.sqrt(radiusSq);
            double horizonDeg = math.acos(1.0 / 3.0) * 180.0 / math.PI_DBL;

            foreach (double lon in new[] { horizonDeg - 6.0, horizonDeg + 6.0 })
            {
                double3 anchorRender = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = lon });

                bool predicate = HorizonCull.IsHiddenBeyondHorizon(
                    anchorRender, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);

                double3 local = anchorRender - sceneOriginRender;
                var localF = new float3((float)local.x, (float)local.y, (float)local.z);
                float3 rebasedF = math.mul(rebase, localF);
                var anchorRebased = new double3(rebasedF.x, rebasedF.y, rebasedF.z);
                bool oracle = RaySphereOccluded(camRelative, anchorRebased, centreRelative, radius);

                Assert.AreEqual(oracle, predicate, $"near-limb lon={lon}");
            }

            bool justInside = HorizonCull.IsHiddenBeyondHorizon(
                proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = horizonDeg - 6.0 }),
                sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);
            bool justBeyond = HorizonCull.IsHiddenBeyondHorizon(
                proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = horizonDeg + 6.0 }),
                sceneOriginRender, rebase, camRelative, centreRelative, radiusSq);

            Assert.IsFalse(justInside, "6° inside the horizon — still visible");
            Assert.IsTrue(justBeyond, "6° beyond the horizon — hidden");
        }

        [Test]
        public void Antipode_IsHidden_NearNeighbour_IsVisible()
        {
            BuildOverheadRig(out IProjection proj, out double3 sceneOriginRender, out float3x3 rebase,
                out double3 camRelative, out double3 centreRelative, out double radiusSq);

            double3 antipode = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 180.0 });
            double3 neighbour = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 10.0 });

            Assert.IsTrue(HorizonCull.IsHiddenBeyondHorizon(
                antipode, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq),
                "the antipode is on the far side of the globe — hidden");
            Assert.IsFalse(HorizonCull.IsHiddenBeyondHorizon(
                neighbour, sceneOriginRender, rebase, camRelative, centreRelative, radiusSq),
                "a 10° near-neighbour of the look-at is well within the horizon — visible");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineAnchorPlacementTests — along-line anchors computed once in tile space as stable topology
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="LineAnchorPlacement.Compute"/> places along-line anchors ONCE in tile space, as stable
    /// <see cref="LineAnchor"/> topology. Teeth: anchor positions (spacing walk, segment transitions), the
    /// build-time anti-hang cap, the line-center and short-line degeneracies, and the no-slide property: an
    /// anchor recovers the SAME world point <c>lerp(path[seg], path[seg+1], t)</c> whatever the per-frame
    /// projection, which the cross-tile identity key builds on.
    /// </summary>
    [TestFixture]
    public class LineAnchorPlacementTests
    {
        private static List<double2> Line(params (double x, double y)[] pts)
        {
            var l = new List<double2>(pts.Length);
            foreach (var (x, y) in pts) l.Add(new double2(x, y));
            return l;
        }

        // Recover an anchor's world point from the polyline it indexes (the forward-guard helper).
        private static double2 WorldOf(List<double2> path, LineAnchor a)
            => math.lerp(path[a.Segment], path[a.Segment + 1], a.T);

        // ── Line placement: anchors at spacing·(k+0.5), each recovering the right world point. ──
        [Test]
        public void Line_StraightLine_AnchorsAtHalfSpacingMultiples()
        {
            var path = Line((0, 0), (100, 0));
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 25, SymbolPlacement.Line);

            Assert.AreEqual(4, anchors.Length, "arcs 12.5/37.5/62.5/87.5 fit in a length-100 line; 112.5 does not");
            double[] expectedX = { 12.5, 37.5, 62.5, 87.5 };
            for (int i = 0; i < anchors.Length; i++)
                Assert.AreEqual(expectedX[i], WorldOf(path, anchors[i]).x, 1e-6,
                    $"anchor {i} sits at spacing·({i}+0.5) along the line");
        }

        // ── Segment transitions: an L-bend anchor lands on the correct segment with the correct t. ──
        [Test]
        public void Line_LBend_AnchorsCrossSegmentsCorrectly()
        {
            var path = Line((0, 0), (40, 0), (40, 40)); // total arc length 80
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 20, SymbolPlacement.Line);

            Assert.AreEqual(4, anchors.Length);
            double2[] expected = { new double2(10, 0), new double2(30, 0), new double2(40, 10), new double2(40, 30) };
            for (int i = 0; i < anchors.Length; i++)
            {
                double2 w = WorldOf(path, anchors[i]);
                Assert.AreEqual(expected[i].x, w.x, 1e-6, $"anchor {i} x");
                Assert.AreEqual(expected[i].y, w.y, 1e-6, $"anchor {i} y");
            }
            Assert.AreEqual(0, anchors[1].Segment, "arc 30 is on the first segment");
            Assert.AreEqual(1, anchors[2].Segment, "arc 50 has crossed onto the second segment");
        }

        // ── THE anti-hang tooth (moved here from the old per-frame loop): a pathological huge line / tiny
        //    spacing is capped at MaxAnchors at BUILD time — never an unbounded array. ──
        [Test]
        public void Line_Pathological_CountIsHardCappedAtBuild()
        {
            var path = Line((0, 0), (1e9, 0)); // would be ~1e9 anchors at spacing 1 without the cap
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 1, SymbolPlacement.Line);
            Assert.AreEqual(LineAnchorPlacement.MaxAnchors, anchors.Length, "the count is bounded, not data-derived");
        }

        // ── line-center: exactly one anchor at the mid arc length. ──
        [Test]
        public void LineCenter_SingleMidAnchor()
        {
            var path = Line((0, 0), (100, 0));
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 25, SymbolPlacement.LineCenter);
            Assert.AreEqual(1, anchors.Length);
            Assert.AreEqual(50.0, WorldOf(path, anchors[0]).x, 1e-6, "one anchor at the line midpoint");
        }

        // ── A line shorter than one spacing still yields ONE centred anchor (a placeable line is never empty). ──
        [Test]
        public void Line_ShorterThanSpacing_YieldsOneCentredAnchor()
        {
            var path = Line((0, 0), (10, 0));
            LineAnchor[] anchors = LineAnchorPlacement.Compute(path, spacingTileUnits: 25, SymbolPlacement.Line);
            Assert.AreEqual(1, anchors.Length, "short line → the centred fallback, not zero anchors");
            Assert.AreEqual(5.0, WorldOf(path, anchors[0]).x, 1e-6, "at the line midpoint");
        }

        // ── Degenerate inputs (fewer than 2 points, or zero-length) produce no anchors. ──
        [Test]
        public void Degenerate_ProducesNoAnchors()
        {
            Assert.AreEqual(0, LineAnchorPlacement.Compute(Line((5, 5)), 25, SymbolPlacement.Line).Length,
                "a single point has no segment");
            Assert.AreEqual(0, LineAnchorPlacement.Compute(Line((5, 5), (5, 5)), 25, SymbolPlacement.Line).Length,
                "a zero-length (coincident) line has no arc to place along");
            Assert.AreEqual(0, LineAnchorPlacement.Compute(null, 25, SymbolPlacement.Line).Length,
                "null path is empty, not a throw");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // PolylineArcMathScreenTests — walk a screen polyline by arc length, get point + tangent
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The geometric core of curved text: walk a screen polyline by arc length, get point + tangent.
    /// Drives <see cref="PolylineArcMath"/> directly (the SCREEN/<c>float2</c> path) over a local resumable
    /// cursor, the same idiom <c>SymbolStagingMath</c> uses in production; pairs with the WORLD/<c>double3</c>
    /// side in <see cref="PolylineArcMathWorldSampleTests"/>.
    /// </summary>
    [TestFixture]
    public class PolylineArcMathScreenTests
    {
        private const float Tol = 1e-4f;

        // A resumable arc walk over one polyline: cumulative lengths built once, cursor threaded across
        // repeated At() calls exactly as SymbolStagingMath.cs's local `cursor` is (see :341).
        private struct ArcCursor
        {
            public float2[] Points;
            public float[] Cumulative;
            public int Count;
            public float TotalLength;
            public int Cursor;

            public void At(float arc, out float2 point, out float tangentRadians)
                => PolylineArcMath.At(Points, Cumulative, Count, TotalLength, arc, ref Cursor, out point, out tangentRadians);
        }

        private static ArcCursor Walk(params float2[] pts)
        {
            var cumulative = new float[pts.Length];
            float total = PolylineArcMath.BuildCumulative(pts, pts.Length, cumulative);
            return new ArcCursor { Points = pts, Cumulative = cumulative, Count = pts.Length, TotalLength = total, Cursor = 0 };
        }

        [Test]
        public void StraightLine_TotalLength_PointAndConstantTangent()
        {
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 0), new float2(20, 0));
            Assert.AreEqual(20f, w.TotalLength, Tol);

            w.At(5f, out float2 p, out float tan);
            Assert.AreEqual(5f, p.x, Tol); Assert.AreEqual(0f, p.y, Tol);
            Assert.AreEqual(0f, tan, Tol, "horizontal line → tangent 0");

            w.At(15f, out p, out tan); // second segment, still horizontal
            Assert.AreEqual(15f, p.x, Tol); Assert.AreEqual(0f, tan, Tol);
        }

        [Test]
        public void ClampsBeyondEnds()
        {
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 0));
            w.At(-5f, out float2 p, out _);
            Assert.AreEqual(0f, p.x, Tol);
            w.At(999f, out p, out _);
            Assert.AreEqual(10f, p.x, Tol);
        }

        [Test]
        public void LBend_TangentSwitchesToTheSecondSegment()
        {
            // right 10, then up 10.
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 0), new float2(10, 10));
            Assert.AreEqual(20f, w.TotalLength, Tol);

            w.At(5f, out float2 p, out float tan);   // on segment 0 (horizontal)
            Assert.AreEqual(new float2(5, 0).x, p.x, Tol); Assert.AreEqual(0f, p.y, Tol);
            Assert.AreEqual(0f, tan, Tol);

            w.At(15f, out p, out tan);               // 5 into segment 1 (vertical, +y)
            Assert.AreEqual(10f, p.x, Tol); Assert.AreEqual(5f, p.y, Tol);
            Assert.AreEqual(math.PI / 2f, tan, Tol, "vertical +y segment → tangent +pi/2");
        }

        // Called DIRECTLY at i=0: any arc > 0 routes past a zero-length first segment, so the skip loop is never
        // reached by arc. The 45-degree next segment keeps a broken skip (atan2(0,0) == 0) distinguishable.
        [Test]
        public void DegenerateSegment_DoesNotCollapseTangent()
        {
            var points = new[] { new float2(0, 0), new float2(0, 0), new float2(10, 10) };
            float tangent = PolylineArcMath.SegmentTangent(points, count: 3, i: 0);
            Assert.AreEqual(math.PI / 4f, tangent, Tol,
                "tangent skips the zero-length segment [0,1] and reports [1,2]'s 45-degree direction, not atan2(0,0)");
        }

        [Test]
        public void FortyFiveDegreeSegment_TangentIsQuarterPi()
        {
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 10));
            Assert.AreEqual(math.sqrt(200f), w.TotalLength, 1e-3f);
            w.At(w.TotalLength * 0.5f, out float2 p, out float tan);
            Assert.AreEqual(5f, p.x, Tol); Assert.AreEqual(5f, p.y, Tol);
            Assert.AreEqual(math.PI / 4f, tan, Tol);
        }

        // Three-segment staircase: seg0 →x [0,10], seg1 ↑y [10,20], seg2 →x [20,30]. Distinct segments so a
        // stale cursor would land on the wrong one. Exercises the Lever A resumable cursor.
        private static ArcCursor Staircase() =>
            Walk(new float2(0, 0), new float2(10, 0), new float2(10, 10), new float2(20, 10));

        private static void AssertAt(ref ArcCursor w, float arc, float2 expectPt, float expectTan)
        {
            w.At(arc, out float2 p, out float tan);
            Assert.AreEqual(expectPt.x, p.x, Tol); Assert.AreEqual(expectPt.y, p.y, Tol);
            Assert.AreEqual(expectTan, tan, Tol);
        }

        [Test]
        public void Cursor_OutOfOrderQueries_EachResolvesToTheContainingSegment()
        {
            // A forward jump (25), a big backward jump (5), then a mid jump (15): the resumable cursor must
            // land on the right segment every time, not on wherever the previous query left it.
            ArcCursor w = Staircase();
            Assert.AreEqual(30f, w.TotalLength, Tol);
            // AssertAt takes `w` by `ref`, so w.Cursor moves. The point asserts alone cannot see the cursor,
            // because SegmentAt's guarded walk finds the right segment from ANY starting cursor.
            AssertAt(ref w, 25f, new float2(15, 10), 0f);            // seg2 (→x)
            Assert.AreEqual(2, w.Cursor, "cursor lands on segment 2 for arc 25");
            AssertAt(ref w, 5f,  new float2(5, 0),   0f);            // seg0 (→x), cursor jumps back 2 segments
            Assert.AreEqual(0, w.Cursor, "cursor jumps back to segment 0 for arc 5");
            AssertAt(ref w, 15f, new float2(10, 5),  math.PI / 2f);  // seg1 (↑y)
            Assert.AreEqual(1, w.Cursor, "cursor advances to segment 1 for arc 15");
            AssertAt(ref w, 25f, new float2(15, 10), 0f);            // seg2 again, forward jump
            Assert.AreEqual(2, w.Cursor, "cursor advances forward to segment 2 again for arc 25");
        }

        [Test]
        public void Cursor_ReverseMonotonicSweep_MatchesForwardSweep()
        {
            // A reversed (keep-upright) symbol queries arcs in DECREASING order. `forward` is rebuilt per arc:
            // one shared outside the loop would evolve like `reverse` and agree whatever At() computes.
            ArcCursor reverse = Staircase();
            float[] arcs = { 3f, 8f, 12f, 18f, 22f, 27f };
            for (int i = arcs.Length - 1; i >= 0; i--)
            {
                reverse.At(arcs[i], out float2 rp, out float rt);
                ArcCursor forward = Staircase(); // independent walker, single cold lookup
                forward.At(arcs[i], out float2 fp, out float ft);
                Assert.AreEqual(fp.x, rp.x, Tol); Assert.AreEqual(fp.y, rp.y, Tol);
                Assert.AreEqual(ft, rt, Tol);
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // PolylineArcMathWorldSampleTests — the resumable segment search and double3 world sampler
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="PolylineArcMath.SegmentAt"/> (the resumable segment search) and
    /// <see cref="PolylineArcMath.SampleWorld"/> (the <c>double3</c> sampler at a resolved <c>(seg,t)</c>):
    /// the seam <c>SymbolStagingMath.StageCurvedAnchor</c> uses to bake a per-glyph world anchor at the index
    /// the screen arc walk resolves. Pins the segment-boundary off-by-one and the zero-length-segment skip.
    /// </summary>
    [TestFixture]
    public class PolylineArcMathWorldSampleTests
    {
        private const float Tol = 1e-4f;
        private const double DTol = 1e-9;

        // A 3-vertex staircase: seg0 [0,10] →x, seg1 [10,20] ↑y — cumulative = {0, 10, 20}.
        private static float[] Cumulative() => new[] { 0f, 10f, 20f };

        [Test]
        public void SegmentAt_MidSegment_ResolvesTheContainingSegmentAndT()
        {
            float[] cum = Cumulative();
            int cursor = 0;
            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 5f, ref cursor, out int seg, out float t);
            Assert.AreEqual(0, seg, "arc 5 sits mid segment 0");
            Assert.AreEqual(0.5f, t, Tol);

            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 15f, ref cursor, out seg, out t);
            Assert.AreEqual(1, seg, "arc 15 sits mid segment 1");
            Assert.AreEqual(0.5f, t, Tol);
        }

        // The off-by-one seam: right at a segment boundary the walk must land on the SAME segment on either
        // side of it (a perturbation of ±1 in `seg` would sample the WRONG world edge entirely).
        [Test]
        public void SegmentAt_AtAndAroundASegmentBoundary_DoesNotOffByOne()
        {
            float[] cum = Cumulative();
            int cursor = 0;

            // Exactly at the vertex (arc=10) the walk lands on segment 0 with t=1: the boundary belongs to the
            // segment it terminates, as in PolylineArcMath.At.
            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 10f, ref cursor, out int segAt, out float tAt);
            Assert.AreEqual(0, segAt, "the vertex arc belongs to the segment it terminates");
            Assert.AreEqual(1f, tAt, Tol);

            // Just before / just after the vertex must land on segment 0 / segment 1 respectively — never off
            // by one regardless of which side the resumable cursor last stopped on.
            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 9.999f, ref cursor, out int segBefore, out _);
            Assert.AreEqual(0, segBefore, "just before the vertex is still segment 0");

            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 10.001f, ref cursor, out int segAfter, out _);
            Assert.AreEqual(1, segAfter, "just after the vertex is segment 1, not segment 0 or 2");
        }

        [Test]
        public void SegmentAt_ReverseMonotonicSweep_MatchesForwardSweep()
        {
            // A reversed (keep-upright) symbol queries arcs in DECREASING order — the backward-walking cursor
            // must resolve the same (seg,t) as a query starting fresh at that arc.
            float[] cum = Cumulative();
            float[] arcs = { 2f, 8f, 12f, 18f };
            int reverseCursor = 0;
            for (int i = arcs.Length - 1; i >= 0; i--)
            {
                PolylineArcMath.SegmentAt(cum, 3, 20f, arcs[i], ref reverseCursor, out int rSeg, out float rT);
                int freshCursor = 0;
                PolylineArcMath.SegmentAt(cum, 3, 20f, arcs[i], ref freshCursor, out int fSeg, out float fT);
                Assert.AreEqual(fSeg, rSeg, $"seg mismatch at arc {arcs[i]}");
                Assert.AreEqual(fT, rT, Tol, $"t mismatch at arc {arcs[i]}");
            }
        }

        [Test]
        public void SampleWorld_ReproducesTheExactWorldLerp()
        {
            var world = new[] { new double3(0, 0, 0), new double3(10, 0, 0), new double3(10, 0, 10) };

            PolylineArcMath.SampleWorld(world, count: 3, seg: 0, t: 0.5f, out double3 point, out double3 dir);
            Assert.AreEqual(5.0, point.x, DTol);
            Assert.AreEqual(0.0, point.y, DTol);
            Assert.AreEqual(0.0, point.z, DTol);
            Assert.AreEqual(10.0, dir.x, DTol); Assert.AreEqual(0.0, dir.z, DTol);

            PolylineArcMath.SampleWorld(world, count: 3, seg: 1, t: 0.5f, out point, out dir);
            Assert.AreEqual(10.0, point.x, DTol);
            Assert.AreEqual(0.0, point.y, DTol);
            Assert.AreEqual(5.0, point.z, DTol);
            Assert.AreEqual(0.0, dir.x, DTol); Assert.AreEqual(10.0, dir.z, DTol);
        }

        // A duplicated world vertex (zero-length segment) must be skipped when deriving the fallback direction,
        // not collapse to a zero vector.
        [Test]
        public void SampleWorld_SkipsADegenerateZeroLengthSegment_ForTheFallbackDirection()
        {
            var world = new[] { new double3(0, 0, 0), new double3(0, 0, 0), new double3(10, 0, 0) };

            PolylineArcMath.SampleWorld(world, count: 3, seg: 0, t: 0f, out double3 point, out double3 dir);
            Assert.AreEqual(0.0, point.x, DTol);
            Assert.AreEqual(10.0, dir.x, DTol, "the zero-length segment [0,1] is skipped in favour of [1,2]");
            Assert.AreEqual(0.0, dir.z, DTol);
        }

        // A straight diagonal path: every mid-segment sample must yield the SAME tangent direction (the
        // decisive "no drift along a straight run" property) and land at the analytically exact (seg,t).
        [Test]
        public void SampleWorld_StraightDiagonalPath_ConstantTangentDirection()
        {
            var world = new[] { new double3(0, 0, 0), new double3(20, 0, 20) };
            float[] cum = { 0f, (float)math.sqrt(800.0) };
            float total = cum[1];

            int cursor = 0;
            PolylineArcMath.SegmentAt(cum, 2, total, total * 0.5f, ref cursor, out int seg, out float t);
            Assert.AreEqual(0, seg);
            Assert.AreEqual(0.5f, t, Tol);

            PolylineArcMath.SampleWorld(world, 2, seg, t, out double3 point, out double3 dir);
            Assert.AreEqual(10.0, point.x, 1e-3);
            Assert.AreEqual(10.0, point.z, 1e-3);
            Assert.AreEqual(dir.x, dir.z, DTol, "a 45-degree diagonal has an equal x/z tangent component");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolBearingTests — the alignment-to-billboard-rotation mapping
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The alignment→billboard-rotation mapping: the mode selection (Map → bearing; Viewport/Auto → 0) and
    /// the ×sign relation. These cases stay green against an inverted <c>SymbolBearing.MapAlignedSign</c>;
    /// the rendered tooth
    /// SymbolIconRenderSnapshotTests.MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing pins the sign.
    /// </summary>
    [TestFixture]
    public class SymbolBearingTests
    {
        [Test]
        public void BillboardRotation_Map_IsBearingTimesSign()
        {
            const float bearing = 0.7f;
            Assert.AreEqual(SymbolBearing.MapAlignedSign * bearing,
                SymbolBearing.BillboardRotationRadians(AlignmentMode.Map, bearing), 1e-6f);
        }

        [Test]
        public void BillboardRotation_ViewportAndAuto_AreZero_ForAnyBearing()
        {
            const float bearing = 1.23f;
            Assert.AreEqual(0f, SymbolBearing.BillboardRotationRadians(AlignmentMode.Viewport, bearing), 1e-6f,
                "viewport-aligned text never rotates with the map");
            Assert.AreEqual(0f, SymbolBearing.BillboardRotationRadians(AlignmentMode.Auto, bearing), 1e-6f,
                "auto resolves to viewport for point placement — no rotation");
        }

        [Test]
        public void BillboardRotation_Map_AtBearingZero_IsZero()
        {
            Assert.AreEqual(0f, SymbolBearing.BillboardRotationRadians(AlignmentMode.Map, 0f), 1e-6f,
                "a north-up map does not rotate even map-aligned text");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolCandidateRangeTilingTests — engine-free, shared between EditMode and the fast project
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolCandidateRangeTilingTests
    {
        private static SymbolCandidate Cand(int boxStart, int boxCount) =>
            new SymbolCandidate { BoxStart = boxStart, BoxCount = boxCount };

        [Test]
        public void ContiguousTiling_PointAndCurvedMix_NoViolation()
        {
            // point(1) + curved(3) + point(1) + curved(2) → tiles [0,7) with no gap/overlap.
            var cands = new[] { Cand(0, 1), Cand(1, 3), Cand(4, 1), Cand(5, 2) };
            Assert.IsFalse(SymbolCandidate.TryFindRangeTilingViolation(cands, 7, out int i, out int expected),
                $"contiguous ranges must not report a violation (got index {i}, expected {expected})");
        }

        [Test]
        public void Empty_NoViolation()
        {
            Assert.IsFalse(SymbolCandidate.TryFindRangeTilingViolation(ReadOnlySpan<SymbolCandidate>.Empty, 0, out _, out _));
        }

        [Test]
        public void OverRangeByOne_ReportedAtOffendingCandidate()
        {
            // The original live signature: the last candidate's range ends at boxCount+? — references a box
            // index == boxCount, one past the written pool [0,boxCount).
            var cands = new[] { Cand(0, 1), Cand(1, 1), Cand(2, 1) }; // claims boxes [0,3) but pool has 2
            Assert.IsTrue(SymbolCandidate.TryFindRangeTilingViolation(cands, 2, out int i, out int expected));
            Assert.AreEqual(2, i, "the over-range candidate is the third one");
            Assert.AreEqual(2, expected, "it should have started at offset 2 (which it does) but overruns boxCount=2");
        }

        [Test]
        public void SharedBox_TwoCandidatesSameStart_ReportedAsNonContiguous()
        {
            // Two candidates referencing box 0 (the 'shared box' theory) — the second breaks contiguity: its
            // BoxStart(0) != the running offset(1).
            var cands = new[] { Cand(0, 1), Cand(0, 1) };
            Assert.IsTrue(SymbolCandidate.TryFindRangeTilingViolation(cands, 2, out int i, out int expected));
            Assert.AreEqual(1, i);
            Assert.AreEqual(1, expected, "the second candidate should have started at offset 1, not 0");
        }

        [Test]
        public void Undercover_WellFormedButLeavesGap_ReportedAtLength()
        {
            // Ranges are individually valid and contiguous but stop short of boxCount → a gap.
            var cands = new[] { Cand(0, 1), Cand(1, 1) };
            Assert.IsTrue(SymbolCandidate.TryFindRangeTilingViolation(cands, 5, out int i, out int expected));
            Assert.AreEqual(cands.Length, i, "an under-cover is flagged at index == candidate count");
            Assert.AreEqual(2, expected, "coverage reached only offset 2 of boxCount 5");
        }

        [Test]
        public void NegativeBoxCount_Reported()
        {
            var cands = new[] { Cand(0, 1), Cand(1, -1) };
            Assert.IsTrue(SymbolCandidate.TryFindRangeTilingViolation(cands, 1, out int i, out _));
            Assert.AreEqual(1, i);
        }

        [Test]
        public void FirstCandidateNotAtZero_Reported()
        {
            var cands = new[] { Cand(1, 1) };
            Assert.IsTrue(SymbolCandidate.TryFindRangeTilingViolation(cands, 2, out int i, out int expected));
            Assert.AreEqual(0, i);
            Assert.AreEqual(0, expected, "the first candidate must start at box 0");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolCollisionTests — ComparePlacementOrder's tie-breaking key, term by term
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one direct tooth for <see cref="SymbolCollision.ComparePlacementOrder(in SymbolCandidate,in SymbolCandidate)"/>:
    /// the placement order key is (SortKey, WasPlacedLastFrame, FeatureIndex, TileKey, FadeId), each term
    /// breaking a tie left by the one before it. The other collision properties need the job runtime and
    /// live in <c>CollisionJobPlacementTests</c> and <c>CollisionGridContractTests</c>.
    /// </summary>
    [TestFixture]
    public class SymbolCollisionTests
    {
        private static SymbolCandidate Candidate(float sortKey, bool wasPlacedLastFrame, int featureIndex,
            long tileKey, long fadeId)
            => new SymbolCandidate
            {
                SortKey = sortKey, WasPlacedLastFrame = wasPlacedLastFrame, FeatureIndex = featureIndex,
                TileKey = tileKey, FadeId = fadeId,
            };

        [Test]
        public void ComparePlacementOrder_RanksSortKeyIncumbencyFeatureTileFade()
        {
            // Sort key dominates every tiebreak field.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 1f, wasPlacedLastFrame: false, featureIndex: 9, tileKey: 9, fadeId: 9),
                Candidate(sortKey: 2f, wasPlacedLastFrame: true, featureIndex: 0, tileKey: 0, fadeId: 0)), 0,
                "a lower sort key must order first regardless of every tiebreak field");

            // Equal sort key -> incumbency (hysteresis) breaks the tie.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: true, featureIndex: 9, tileKey: 9, fadeId: 9),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 0, tileKey: 0, fadeId: 0)), 0,
                "equal sort keys order the incumbent first regardless of feature/tile/fade");

            // Equal sort key and incumbency -> feature index breaks the tie.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 0, tileKey: 9, fadeId: 9),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 1, tileKey: 0, fadeId: 0)), 0,
                "equal sort key and incumbency order by feature index (a bare compare would return 0 here)");

            // Equal sort key, incumbency, and feature index -> tile key breaks the tie.
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 9),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 200, fadeId: 0)), 0,
                "equal sort key, incumbency and feature index order by tile key");

            // Equal sort key, incumbency, feature index and tile key -> FadeId breaks the tie — the term that
            // makes the order STRICTLY total for a curved feature's repeated anchors (which share all four).
            Assert.Less(SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 100),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 200)), 0,
                "equal sort key, incumbency, feature index and tile key order by FadeId");

            // Fully equal -> 0.
            Assert.AreEqual(0, SymbolCollision.ComparePlacementOrder(
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 100),
                Candidate(sortKey: 5f, wasPlacedLastFrame: false, featureIndex: 3, tileKey: 100, fadeId: 100)));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolFarPlaneCullTests — the core distance math for the pre-projection far-plane cull
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Core distance math for the pre-projection far-plane symbol cull: a symbol farther from the camera
    /// than the cull distance is culled; a non-positive distance disables it; and the per-frame rebase rotation is
    /// applied to the anchor (so the cull measures the true camera→anchor separation, not a pre-rebase one). The
    /// gather-path WIRING (fraction × far, fade-out-in-place) is pinned separately in the EditMode placement teeth.</summary>
    [TestFixture]
    public class SymbolFarPlaneCullTests
    {
        // Camera at the floating origin (rebased frame), so with an identity rebase the camera→anchor distance is
        // just the anchor's offset from the scene origin — easy to reason about.
        private static readonly double3 Origin = new double3(1000.0, -2000.0, 3000.0);

        /// <summary>An anchor within the cull distance survives; one beyond it is culled. The scene origin is
        /// non-zero to prove the double-subtract runs (a naive |anchor| would mis-measure).</summary>
        [Test]
        public void WithinDistance_Survives_BeyondDistance_Culled()
        {
            var cam = double3.zero;               // camera at the origin in the rebased frame
            var near = Origin + new double3(0.0, 0.0, 300.0); // 300 m from camera
            var far  = Origin + new double3(0.0, 0.0, 700.0); // 700 m from camera

            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(near, Origin, float3x3.identity, cam, 500.0),
                "300 m < 500 m cull distance ⇒ kept");
            Assert.IsTrue(SymbolFarPlaneCull.IsCulled(far, Origin, float3x3.identity, cam, 500.0),
                "700 m > 500 m cull distance ⇒ culled");
        }

        /// <summary>Distance is measured from the CAMERA, not the scene origin: shifting the camera toward a distant
        /// anchor keeps it, and away from a near anchor culls it — the opposite of a look-at-radius measure.</summary>
        [Test]
        public void DistanceIsMeasuredFromCamera_NotOrigin()
        {
            var anchor = Origin + new double3(0.0, 0.0, 700.0); // 700 m from the origin
            var camNear = new double3(0.0, 0.0, 400.0);         // camera 400 m along +Z ⇒ 300 m from anchor
            var camFar  = new double3(0.0, 0.0, -400.0);        // camera 400 m along −Z ⇒ 1100 m from anchor

            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(anchor, Origin, float3x3.identity, camNear, 500.0),
                "camera 300 m from the anchor ⇒ kept, though it is 700 m from the origin");
            Assert.IsTrue(SymbolFarPlaneCull.IsCulled(anchor, Origin, float3x3.identity, camFar, 500.0),
                "camera 1100 m from the anchor ⇒ culled");
        }

        /// <summary>A non-positive cull distance disables the cull (keep every symbol) — the safe fallback for a
        /// mis-wired caller, so it degrades to "cull nothing" rather than culling everything.</summary>
        [Test]
        public void NonPositiveDistance_DisablesCull()
        {
            var farAway = Origin + new double3(1e6, 0.0, 0.0);
            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(farAway, Origin, float3x3.identity, double3.zero, 0.0),
                "0 ⇒ cull disabled");
            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(farAway, Origin, float3x3.identity, double3.zero, -1.0),
                "negative ⇒ cull disabled");
        }

        /// <summary>The rebase rotation is applied to the anchor before the distance is taken. A 180° rotation about
        /// Y (unambiguous — sin = 0, no handedness question) flips the anchor's X across an off-axis camera, moving
        /// it from inside to outside the cull distance. If the rebase were skipped the decision would not change.</summary>
        [Test]
        public void RebaseRotation_IsAppliedToAnchor()
        {
            var cam    = new double3(100.0, 0.0, 0.0);          // off-axis camera (in the rebased frame)
            var anchor = Origin + new double3(400.0, 0.0, 0.0); // local offset (400,0,0)

            // Identity: rebased local (400,0,0), distance to camera (100,0,0) = 300 m.
            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(anchor, Origin, float3x3.identity, cam, 400.0),
                "identity rebase ⇒ 300 m from camera ⇒ kept");

            // Ry(180°): local (400,0,0) → (−400,0,0); distance to (100,0,0) = 500 m ⇒ now culled.
            var ry180 = new float3x3(-1f, 0f, 0f,
                                      0f, 1f, 0f,
                                      0f, 0f, -1f);
            Assert.IsTrue(SymbolFarPlaneCull.IsCulled(anchor, Origin, ry180, cam, 400.0),
                "180° rebase ⇒ 500 m from camera ⇒ culled (proves the rebase is applied)");
        }

        /// <summary>Pins <c>float3x3</c>'s 9-scalar constructor to the real Unity.Mathematics layout
        /// (row-major arguments, column-major storage) with a NON-symmetric matrix. The diagonal
        /// <c>ry180</c> above reads the same under transpose, so only this tooth catches a transposed
        /// constructor in the Tools/core-tests shim.</summary>
        [Test]
        public void Float3x3_NineArgConstructor_IsRowMajorArgsColumnMajorStorage()
        {
            var m = new float3x3(1f, 2f, 3f,
                                  4f, 5f, 6f,
                                  7f, 8f, 9f);

            Assert.That(m.c0.x, Is.EqualTo(1f)); Assert.That(m.c0.y, Is.EqualTo(4f)); Assert.That(m.c0.z, Is.EqualTo(7f));
            Assert.That(m.c1.x, Is.EqualTo(2f)); Assert.That(m.c1.y, Is.EqualTo(5f)); Assert.That(m.c1.z, Is.EqualTo(8f));
            Assert.That(m.c2.x, Is.EqualTo(3f)); Assert.That(m.c2.y, Is.EqualTo(6f)); Assert.That(m.c2.z, Is.EqualTo(9f));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolPairingTests — engine-free, shared between EditMode and the fast project
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolPairingTests
    {
        private static SymbolFeature Symbol(SymbolPairRole role, int pairId) =>
            new SymbolFeature { PairRole = role, PairId = pairId };

        private static ShapedSymbol ShapedSymbol(SymbolPairRole role, int pairId, long tileKey = 0, int materialIndex = 0) =>
            new ShapedSymbol { PairRole = role, PairId = pairId, TileKey = tileKey, MaterialIndex = materialIndex };

        // --- SymbolFeature overload ---

        [Test]
        public void SymbolFeature_IntactPair_Paired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 7), Symbol(SymbolPairRole.Rider, 7) };
            Assert.IsTrue(SymbolPairing.TryGetRider(symbols, 0, out int riderIndex));
            Assert.AreEqual(1, riderIndex);
        }

        [Test]
        public void SymbolFeature_RiderMissing_ListTruncated_NotPaired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 7) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void SymbolFeature_NextSlotNull_NotPaired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 7), null };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void SymbolFeature_MismatchedPairId_NotPaired()
        {
            // [Owner(A), Rider(B)] — the tooth a naive adjacency-only resolver fails.
            var symbols = new[] { Symbol(SymbolPairRole.Owner, 1), Symbol(SymbolPairRole.Rider, 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void SymbolFeature_NotAnOwner_NotPaired()
        {
            var symbols = new[] { Symbol(SymbolPairRole.None, 0), Symbol(SymbolPairRole.Rider, 0) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        // --- ShapedSymbol overload: the live production carrier that Bake resolves ---

        [Test]
        public void ShapedSymbol_IntactPair_Paired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3, tileKey: 9, materialIndex: 2),
                                  ShapedSymbol(SymbolPairRole.Rider, 3, tileKey: 9, materialIndex: 2) };
            Assert.IsTrue(SymbolPairing.TryGetRider(symbols, 0, out int riderIndex));
            Assert.AreEqual(1, riderIndex);
        }

        [Test]
        public void ShapedSymbol_RiderMissing_ListTruncated_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_NextSlotNotRider_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3), ShapedSymbol(SymbolPairRole.None, 3) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_MismatchedPairId_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 1), ShapedSymbol(SymbolPairRole.Rider, 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_MismatchedTileKey_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3, tileKey: 1),
                                  ShapedSymbol(SymbolPairRole.Rider, 3, tileKey: 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        [Test]
        public void ShapedSymbol_MismatchedMaterialIndex_NotPaired()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3, materialIndex: 1),
                                  ShapedSymbol(SymbolPairRole.Rider, 3, materialIndex: 2) };
            Assert.IsFalse(SymbolPairing.TryGetRider(symbols, 0, out _));
        }

        // --- IsRider (the mirror at i-1) ---

        [Test]
        public void IsRider_MatchingOwnerBefore_True()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Owner, 3), ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsTrue(SymbolPairing.IsRider(symbols, 1));
        }

        [Test]
        public void IsRider_OrphanRider_NoOwnerBefore_False()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsFalse(SymbolPairing.IsRider(symbols, 0));
        }

        [Test]
        public void IsRider_PrecedingSymbolIsNotAnOwner_False()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.None, 0), ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsFalse(SymbolPairing.IsRider(symbols, 1));
        }

        [Test]
        public void IsRider_IndexZero_False()
        {
            var symbols = new[] { ShapedSymbol(SymbolPairRole.Rider, 3) };
            Assert.IsFalse(SymbolPairing.IsRider(symbols, 0));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolScreenProjectionTests — TryProjectAnchor golden + culling over a hand-computable matrix
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SymbolScreenProjection.TryProjectAnchor"/> golden + culling over a hand-computable
    /// view-projection matrix; <c>SymbolScreenProjectionUnityTests</c> cross-checks a REAL camera. The matrix
    /// maps local <c>(x,y,z)</c> to clip <c>(x, y, z, z)</c>, so <c>clip.w = local.z</c> stands in for
    /// distance from the camera. That exercises the perspective divide, the behind-camera cull and the
    /// viewport-margin cull.
    /// </summary>
    [TestFixture]
    public class SymbolScreenProjectionTests
    {
        // clip = mul(ViewProj, (x,y,z,1)) = (x, y, z, z) — see the class doc.
        private static readonly float4x4 ViewProj = new float4x4(
            new float4(1f, 0f, 0f, 0f),
            new float4(0f, 1f, 0f, 0f),
            new float4(0f, 0f, 1f, 1f),
            new float4(0f, 0f, 0f, 0f));

        private static readonly double2 Viewport = new double2(200.0, 100.0);

        // ── Golden: a known front-of-camera anchor lands at a hand-computed screen pixel ──
        [Test]
        public void TryProjectAnchor_FrontOfCamera_MatchesHandComputedPixel()
        {
            var renderPos = new double3(2.0, 1.0, 4.0);
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out float depth);

            Assert.IsTrue(ok, "a point in front of the camera and inside the viewport must not be culled");
            // clip=(2,1,4,4) -> ndc=(0.5,0.25,1) -> screenPx=(0.75*200, 0.625*100) = (150, 62.5).
            Assert.AreEqual(150f, screenPx.x, 1e-4f);
            Assert.AreEqual(62.5f, screenPx.y, 1e-4f);
            Assert.AreEqual(1f, depth, 1e-4f);
        }

        // ── The SceneOriginRender rebase is MANDATORY: shifting renderPos and sceneOriginRender by the SAME
        //    offset reproduces the golden pixel, because only their DIFFERENCE matters. ──
        [Test]
        public void TryProjectAnchor_SceneOriginRebase_OnlyTheDifferenceMatters()
        {
            var sceneOrigin = new double3(50.0, 0.0, 50.0);
            var renderPos = new double3(52.0, 1.0, 54.0); // local = renderPos - sceneOrigin = (2,1,4)

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out float depth);

            Assert.IsTrue(ok);
            Assert.AreEqual(150f, screenPx.x, 1e-4f, "rebased local must reproduce the SAME golden pixel as the origin-relative test");
            Assert.AreEqual(62.5f, screenPx.y, 1e-4f);

            // An impl that skipped the rebase would compute clip=(52,1,54,54) -> ndc=(52/54, 1/54, 1), a very
            // different pixel. The golden pixel must NOT be that value.
            float naiveNdcX = 52f / 54f;
            float naivePixelX = (naiveNdcX * 0.5f + 0.5f) * 200f;
            Assert.That(screenPx.x, Is.Not.EqualTo(naivePixelX).Within(0.5f),
                "skipping the SceneOriginRender rebase must NOT reproduce this golden -- the rebase is load-bearing");
        }

        // ── Behind-camera cull: clip.w <= 0 -> culled, regardless of viewport position ──
        [Test]
        public void TryProjectAnchor_BehindCamera_Culled()
        {
            var renderPos = new double3(2.0, 1.0, -4.0); // z <= 0 -> clip.w <= 0 in this test matrix
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out _, out _);

            Assert.IsFalse(ok, "an anchor behind the camera (clip.w <= 0) must be culled");
        }

        [Test]
        public void TryProjectAnchor_AtCameraPlane_Culled()
        {
            var renderPos = new double3(2.0, 1.0, 0.0); // z == 0 -> clip.w == 0 (the <= boundary)
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out _, out _);

            Assert.IsFalse(ok, "clip.w == 0 is the inclusive boundary of the behind-camera cull");
        }

        // ── Outside-viewport cull: in front of the camera (w > 0) but the projected pixel is nowhere near
        //    the viewport -> culled. Distinct code path from the behind-camera cull. ──
        [Test]
        public void TryProjectAnchor_FarOutsideViewport_Culled()
        {
            var renderPos = new double3(1000.0, 1.0, 4.0); // clip.w = 4 > 0 (NOT behind camera)
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out _);

            Assert.IsFalse(ok, "a projected pixel far outside the viewport (even with a positive w) must be culled");
        }

        [Test]
        public void TryProjectAnchor_WellInsideViewport_NotCulled()
        {
            // Dead-center of the viewport -- a positive control proving the margin gate isn't just always-false.
            var renderPos = new double3(0.0, 0.0, 4.0);
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(100f, screenPx.x, 1e-4f);
            Assert.AreEqual(50f, screenPx.y, 1e-4f);
        }

        // ── A NON-identity globe rebase must be applied, not dropped. The anchor is off the look-at: at the
        //    look-at, local = 0 and rebase * 0 = 0, so the test would pass without the rebase. ──
        [Test]
        public void TryProjectPoint_NonIdentityRebase_MatchesMeshOracle_AndDiffersFromNoRebase()
        {
            var proj = new SphericalProjection();
            var lookAt = new GeoCoordinate { Latitude = 45.0, Longitude = 30.0 };
            double3 sceneOrigin = proj.Project(lookAt);
            float3x3 basis = proj.TangentBasisAt(lookAt);
            float3x3 rebase = math.transpose(basis); // matches SceneFrame.Rebase's definition

            // NON-look-at anchor -- local = anchor - sceneOrigin != 0, so the rebase is discriminating.
            var anchorGeo = new GeoCoordinate { Latitude = 47.0, Longitude = 33.0 };
            double3 anchor = proj.Project(anchorGeo);

            var viewProj = float4x4.identity;
            var viewport = new double2(200.0, 100.0);

            // (1) Mesh oracle: FloatingOrigin.TileToSceneRebased does the same "double subtract, narrow to float,
            //     rotate" the seam must do. Its pixel, computed by hand, must match TryProjectPoint's.
            float3 oracleLocal = FloatingOrigin.TileToSceneRebased(anchor, sceneOrigin, rebase);
            float4 oracleClip = math.mul(viewProj, new float4(oracleLocal, 1f));
            float2 oracleScreen = new float2(
                (oracleClip.x / oracleClip.w * 0.5f + 0.5f) * (float)viewport.x,
                (oracleClip.y / oracleClip.w * 0.5f + 0.5f) * (float)viewport.y);

            bool ok = SymbolScreenProjection.TryProjectPoint(
                in anchor, in sceneOrigin, in viewProj, in viewport, rebase, out float2 screenPx, out _);

            Assert.IsTrue(ok, "the anchor must project in front of this identity-viewProj camera");
            Assert.AreEqual(oracleScreen.x, screenPx.x, 1e-3f, "rebased seam pixel must match the mesh-RTC oracle");
            Assert.AreEqual(oracleScreen.y, screenPx.y, 1e-3f, "rebased seam pixel must match the mesh-RTC oracle");

            // (2) Discrimination: the no-rebase (identity) result must differ -- proves the rebase is load-bearing,
            //     not a no-op that happens to cancel out.
            bool okNoRebase = SymbolScreenProjection.TryProjectPoint(
                in anchor, in sceneOrigin, in viewProj, in viewport, float3x3.identity, out float2 screenPxNoRebase, out _);
            Assert.IsTrue(okNoRebase);
            Assert.That(math.distance(screenPx, screenPxNoRebase), Is.GreaterThan(1e-3f),
                "a non-identity rebase must move the projected pixel -- dropping it would silently reproduce the no-rebase result");

            // (3) Rebase self-check (non-circular -- hand-computed targets, independent of transpose's internals):
            //     rebase maps each render basis column back to its own ENU axis.
            AssertApprox(math.mul(rebase, basis.c0), new float3(1f, 0f, 0f), "rebase * basis.c0 must recover East");
            AssertApprox(math.mul(rebase, basis.c1), new float3(0f, 1f, 0f), "rebase * basis.c1 must recover Up");
            AssertApprox(math.mul(rebase, basis.c2), new float3(0f, 0f, 1f), "rebase * basis.c2 must recover North");
        }

        private static void AssertApprox(float3 actual, float3 expected, string message)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-5f, message);
            Assert.AreEqual(expected.y, actual.y, 1e-5f, message);
            Assert.AreEqual(expected.z, actual.z, 1e-5f, message);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStagingMathCurvedUpTests — SampleUp's per-glyph sample, pinned exactly
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolStagingMathCurvedUpTests
    {
        private struct Pools
        {
            public SymbolBox[] Boxes; public int BoxCount;
            public PlacedQuad[] Quads; public int QuadCount;
            public SymbolCandidate[] Candidates; public CandidateEmit[] Emit; public int EmitCount;
            public static Pools New(int maxBoxes = 64, int maxQuads = 64, int maxCandidates = 8) => new Pools
            {
                Boxes = new SymbolBox[maxBoxes], Quads = new PlacedQuad[maxQuads],
                Candidates = new SymbolCandidate[maxCandidates], Emit = new CandidateEmit[maxCandidates],
            };
        }

        private static SymbolQuad Cell() => new SymbolQuad
        {
            TopLeft = new float2(-5f, 8f), BottomRight = new float2(5f, -2f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };

        // A straight 100-px line, congruent in screen and world, so a glyph's (seg,t) samples the world/up
        // arrays at the same fraction. Ups 90 deg apart: (0,1,0) at vertex 0, (1,0,0) at vertex 1.
        [Test]
        public void StageCurved_SurfaceUp_MatchesHandComputedLerpAndNormalize_AtEachGlyphsOwnSample()
        {
            var screenPath = new[] { new float2(0f, 0f), new float2(100f, 0f) };
            var depthPath  = new[] { 0f, 0f };
            var validPath  = new byte[] { 1, 1 };
            var worldPath  = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var worldUpPath = new[] { new float3(0f, 1f, 0f), new float3(1f, 0f, 0f) }; // 90 deg apart

            // 3 glyphs at ArcCenter 0/50/100, scale 1, centred at LineAnchor(0, 0.5) (arc 50), so
            // arc_i = ArcCenter_i and t = 0, 0.5, 1.
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 0f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 50f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell() },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                // The line runs rightward, so KeepUpright's reversal never triggers; false keeps the t/seg
                // mapping simplest to hand-verify.
                MaxAngleDeg = 90f, KeepUpright = false, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { SymbolStagingMath.LineFadeId(1, 0, 1, 0), SymbolStagingMath.LineFadeId(1, 0, 1, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathPoints = new float2[2];
            var cumulativeLengths = new float[2];
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath,
                glyphs, anchors, fadeIds, wasPlaced, pathPoints, cumulativeLengths, bearingRadians: 0f,
                view: default, ordinal: 0, // No view transform ⇒ the screen box, byte-identical
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(3, p.QuadCount, "one quad per glyph");

            // Glyph 0 (t=0): AtWithSegment's arc<=0 branch -> seg=0, t=0 -> SampleUp = lerp(up0, up1, 0) =
            // up0 = (0,1,0), already unit -> no renormalize needed.
            AssertUp(p.Quads[0].SurfaceUp, new float3(0f, 1f, 0f), "glyph 0 (t=0): exactly worldUpPath[0]");

            // Glyph 1 (t=0.5): lerp((0,1,0),(1,0,0),0.5) = (0.5,0.5,0), length = sqrt(0.5) ~ 0.70710678 ->
            // normalized = (0.70710678, 0.70710678, 0).
            float invSqrt2 = 0.70710678f;
            AssertUp(p.Quads[1].SurfaceUp, new float3(invSqrt2, invSqrt2, 0f), "glyph 1 (t=0.5): normalize(lerp) at 45 deg");

            // Glyph 2 (t=1): AtWithSegment's arc>=total branch -> seg=0, t=1 -> SampleUp = lerp(up0,up1,1) =
            // up1 = (1,0,0), already unit.
            AssertUp(p.Quads[2].SurfaceUp, new float3(1f, 0f, 0f), "glyph 2 (t=1): exactly worldUpPath[1]");

            // Every SurfaceUp is unit length (kills a lerp-without-renormalize implementation, which would
            // leave glyph 1 at length sqrt(0.5) ~ 0.707, not 1).
            for (int i = 0; i < p.QuadCount; i++)
                Assert.AreEqual(1.0, math.length(p.Quads[i].SurfaceUp), 1e-5, $"glyph {i}: SurfaceUp must be unit length");
        }

        private static void AssertUp(float3 actual, float3 expected, string message)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-5f, $"{message} (x)");
            Assert.AreEqual(expected.y, actual.y, 1e-5f, $"{message} (y)");
            Assert.AreEqual(expected.z, actual.z, 1e-5f, $"{message} (z)");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStagingMathCurvedVertexTests — a glyph at a polyline vertex rotates by the chord, not one segment
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A glyph straddling a polyline VERTEX rotates by the CHORD across its own footprint, not the raw
    /// single-segment tangent <see cref="PolylineArcMath.At"/> returns. With the segment tangent, a glyph that
    /// straddles a bend collides its inner corner with its neighbour on the concave side. See
    /// <see cref="SymbolStagingMath"/>'s private <c>StageCurvedAnchor</c>.
    /// </summary>
    [TestFixture]
    public class SymbolStagingMathCurvedVertexTests
    {
        private const float Tol = 1e-3f;

        private struct Pools
        {
            public SymbolBox[] Boxes; public int BoxCount;
            public PlacedQuad[] Quads; public int QuadCount;
            public SymbolCandidate[] Candidates; public CandidateEmit[] Emit; public int EmitCount;
            public static Pools New(int maxBoxes = 64, int maxQuads = 256, int maxCandidates = 64) => new Pools
            {
                Boxes = new SymbolBox[maxBoxes], Quads = new PlacedQuad[maxQuads],
                Candidates = new SymbolCandidate[maxCandidates], Emit = new CandidateEmit[maxCandidates],
            };
        }

        private static SymbolQuad Cell(float halfWidth) => new SymbolQuad
        {
            TopLeft = new float2(-halfWidth, 6f), BottomRight = new float2(halfWidth, -6f),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };

        [Test]
        public void StageCurved_GlyphStraddlingVertex_RotatesToBlendedChordAngle_NotRawSegmentAngle()
        {
            // Bent polyline: segment A at 0°, length 100; segment B at +30°, length 100. Vertex at arc=100.
            float bendRad = math.radians(30f);
            var screenPath = new[]
            {
                new float2(0f, 0f),
                new float2(100f, 0f),
                new float2(100f + 100f * math.cos(bendRad), 100f * math.sin(bendRad)),
            };
            var depthPath = new[] { 0f, 0f, 0f };
            var validPath = new byte[] { 1, 1, 1 };
            // World path mirrors the screen path 1:1 (index-aligned) — a flat XZ-plane bend at the same angle.
            var worldPath = new[]
            {
                new double3(0, 0, 0),
                new double3(100, 0, 0),
                new double3(100 + 100 * math.cos(bendRad), 0, 100 * math.sin(bendRad)),
            };
            var worldUpPath = new float3[worldPath.Length]; // unread by this rotation-only tooth

            // 3 glyphs, 40-baked-px advance each, scale 1. Staged at centerArc=100, the 20-px half-wide MIDDLE
            // glyph spans arc [80,120] and straddles the bend symmetrically.
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 20f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(20f) },   // straddles the vertex
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(1, 0f) };  // arc distance 100 == the vertex
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { SymbolStagingMath.LineFadeId(1, 0, 1, 0), SymbolStagingMath.LineFadeId(1, 0, 1, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathPoints = new float2[3];
            var cumulativeLengths = new float[3];
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath, glyphs, anchors,
                fadeIds, wasPlaced, pathPoints, cumulativeLengths, bearingRadians: 0f,
                view: default, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(3, p.QuadCount);

            float straddlingRotation = p.Quads[1].RotationRadians;

            // The chord blends 0° and 30° in proportion to the split around the vertex; this symmetric
            // straddle gives 15°.
            float expectedBlendedDeg = 15f;
            Assert.AreEqual(math.radians(expectedBlendedDeg), straddlingRotation, Tol,
                "straddling glyph should rotate to the blended chord angle, not a raw single-segment angle");

            // Rule out the two single-segment answers this glyph could snap to.
            Assert.That(math.abs(straddlingRotation - 0f) > 0.05f, "must not equal segment A's raw angle (0°)");
            Assert.That(math.abs(straddlingRotation - bendRad) > 0.05f, "must not equal segment B's raw angle (30°)");
        }

        [Test]
        public void StageCurved_StraightLine_RotationStillEqualsSegmentAngle()
        {
            // Sanity: the chord fix must be a no-op when the symbol span contains no vertex — a straight
            // line's chord across any footprint is collinear with the segment, so rotation is unchanged.
            float tiltRad = math.radians(20f);
            float2 direction = new float2(math.cos(tiltRad), math.sin(tiltRad));
            var screenPath = new[] { float2.zero, direction * 200f };
            var depthPath = new[] { 0f, 0f };
            var validPath = new byte[] { 1, 1 };
            // World path mirrors the screen path 1:1 (index-aligned) on the flat XZ plane.
            var worldPath = new[] { double3.zero, new double3(direction.x, 0, direction.y) * 200.0 };
            var worldUpPath = new float3[worldPath.Length]; // unread by this rotation-only tooth
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 20f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(20f) },
                new CurvedGlyph { ArcCenter = 100f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 2, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { SymbolStagingMath.LineFadeId(1, 0, 2, 0), SymbolStagingMath.LineFadeId(1, 0, 2, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathPoints = new float2[2];
            var cumulativeLengths = new float[2];
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath, glyphs, anchors,
                fadeIds, wasPlaced, pathPoints, cumulativeLengths, bearingRadians: 0f,
                view: default, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            for (int q = 0; q < p.QuadCount; q++)
                Assert.AreEqual(tiltRad, p.Quads[q].RotationRadians, Tol);
        }

        // ── Curved TEXT leaves CurvedStageInput.AtlasKind/IconRotateRadians at their defaults, so its emit is a
        //    Text, zero-rotation record. Routing text through the icon arm turns this RED. ──
        [Test]
        public void StageCurved_Text_LeavesTheEmitOnTheGlyphAtlas_WithNoExtraRotation()
        {
            var screenPath = new[] { float2.zero, new float2(400f, 0f) };
            var worldPath = new[] { double3.zero, new double3(400, 0, 0) };
            var worldUpPath = new float3[worldPath.Length]; // unread by this atlas/rotation-only tooth
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 20f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 60f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            // Every icon-specific field left DEFAULT — exactly what BuildCurvedInput produces for a text symbol.
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 4, TileKey = 1, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { SymbolStagingMath.LineFadeId(1, 0, 4, 0), SymbolStagingMath.LineFadeId(1, 0, 4, -1) };
            var p = Pools.New();
            int boxCountBefore = p.BoxCount;

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, new[] { 0f, 0f }, new byte[] { 1, 1 },
                worldPath, worldUpPath, glyphs, anchors, fadeIds, new byte[] { 0, 0 }, new float2[2], new float[2],
                bearingRadians: 0f,
                view: default, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(SymbolKind.Text, p.Emit[0].AtlasKind, "curved text must still sample the GLYPH atlas");
            Assert.AreEqual(0f, p.Emit[0].ExtraRotationRadians, 1e-9f,
                "curved text carries no icon-rotate — the renderer must see the same 0f it used to hardcode");
            Assert.AreEqual(glyphs.Length, p.BoxCount - boxCountBefore, "one box per glyph, unchanged");
        }

        // ── A ONE-GLYPH curved ICON stages one candidate per anchor, routed to the SPRITE atlas
        //    and rotated to the projected tangent — the whole point of reusing the curved path for icons. ──
        [Test]
        public void StageCurved_OneGlyphIcon_StagesPerAnchor_IntoTheIconAtlas_WithARotatedBox()
        {
            // A 45° line, so the staged box is genuinely ROTATED (an axis-aligned line could not discriminate).
            float angle = math.radians(45f);
            float2 dir = new float2(math.cos(angle), math.sin(angle));
            var screenPath = new[] { float2.zero, dir * 600f };
            var depthPath = new[] { 0f, 0f };
            var validPath = new byte[] { 1, 1 };
            var worldPath = new[] { double3.zero, new double3(dir.x, 0, dir.y) * 600.0 };
            var worldUpPath = new float3[worldPath.Length]; // unread by this rotation/atlas tooth

            // A WIDE cell (24 x 12) so the rotated box is measurably wider in x than the cell.
            var iconCell = new SymbolQuad
            {
                TopLeft = new float2(-12f, 6f), BottomRight = new float2(12f, -6f),
                UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
            };
            var glyphs = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = iconCell } };
            var anchors = new[] { new LineAnchor(0, 0.25f), new LineAnchor(0, 0.75f) };
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 9, TileKey = 5, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 45f, KeepUpright = false, Color = new float4(1, 1, 1, 1),
                AtlasKind = SymbolKind.Icon,
            };
            var fadeIds = new[]
            {
                SymbolStagingMath.LineFadeId(5, 0, 9, 0), SymbolStagingMath.LineFadeId(5, 0, 9, 1),
                SymbolStagingMath.LineFadeId(5, 0, 9, -1),
            };
            var wasPlaced = new byte[] { 0, 0, 0 };
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath, glyphs,
                anchors, fadeIds, wasPlaced, new float2[2], new float[2], bearingRadians: 0f,
                view: default, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(anchors.Length, staged, "one candidate per along-line anchor");
            Assert.AreEqual(anchors.Length, p.EmitCount, "one emit per candidate");
            for (int c = 0; c < staged; c++)
            {
                Assert.AreEqual(1, p.Candidates[c].BoxCount, "a one-glyph label has exactly one box");
                Assert.AreEqual(1, p.Candidates[c].EmitCount, "…and exactly one emit");
                // An impl that forgot AtlasKind would route these quads to the GLYPH texture and draw garbage.
                Assert.AreEqual(SymbolKind.Icon, p.Emit[c].AtlasKind, "the emit must sample the SPRITE atlas");
                // An impl that routed icons through StagePoint would leave AlongLine false and lose the tangent.
                Assert.IsTrue(p.Emit[c].AlongLine, "an along-line icon must take the curved/world emit branch");
                Assert.IsTrue(p.Emit[c].IsWorld, "…and the world-anchored draw sink");
            }

            // The staged box IS SymbolBox.BuildRotatedGlyph over the same inputs, not the axis-aligned cell.
            // The unrotated width is the precondition that the 45° case is non-degenerate.
            SymbolBox actual = p.Boxes[0];
            var expected = SymbolBox.BuildRotatedGlyph(p.Quads[0].AnchorScreenPx, iconCell,
                s.TextSizePx, p.Quads[0].RotationRadians, s.PaddingPx, glyphs[0].CellSkirt);
            Assert.AreEqual(expected.Min.x, actual.Min.x, Tol, "box Min.x");
            Assert.AreEqual(expected.Min.y, actual.Min.y, Tol, "box Min.y");
            Assert.AreEqual(expected.Max.x, actual.Max.x, Tol, "box Max.x");
            Assert.AreEqual(expected.Max.y, actual.Max.y, Tol, "box Max.y");

            float unrotatedWidth = iconCell.BottomRight.x - iconCell.TopLeft.x;
            Assert.AreEqual(24f, unrotatedWidth, Tol, "precondition: the cell is 24 px wide unrotated");
            Assert.Greater(actual.Max.x - actual.Min.x, unrotatedWidth + 1f,
                "a 45°-rotated wide cell must bound STRICTLY wider in x than the cell itself");
            Assert.AreEqual(math.radians(45f), p.Quads[0].RotationRadians, Tol,
                "the quad rotates to the 45° line tangent");
        }

        [Test]
        public void StageCurved_TextTranslate_IsCarriedOntoTheWorldEmitDelta()
        {
            // WorldSymbolRenderer.Emit adds emit.TranslateDeltaPx to every world corner, so an emit that left it
            // zero would draw a curved symbol with a nonzero `text-translate` at the UNtranslated position.
            var screenPath = new[] { float2.zero, new float2(200f, 0f) };
            var depthPath = new[] { 0f, 0f };
            var validPath = new byte[] { 1, 1 };
            var worldPath = new[] { double3.zero, new double3(200, 0, 0) };
            var worldUpPath = new float3[worldPath.Length]; // unread by this translate-delta tooth
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 40f, Cell = Cell(10f) },
                new CurvedGlyph { ArcCenter = 80f, Cell = Cell(10f) },
            };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            // Viewport anchor, bearing 0 ⇒ ApplyTranslate delta = (tx, -ty) = (7, 3) for translate (7, -3).
            var translatePx = new float2(7f, -3f);
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 3, TileKey = 1, Slot = 0,
                TranslatePx = translatePx, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 90f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var fadeIds = new[] { SymbolStagingMath.LineFadeId(1, 0, 3, 0), SymbolStagingMath.LineFadeId(1, 0, 3, -1) };
            var wasPlaced = new byte[] { 0, 0 };
            var pathPoints = new float2[2];
            var cumulativeLengths = new float[2];
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath, glyphs, anchors,
                fadeIds, wasPlaced, pathPoints, cumulativeLengths, bearingRadians: 0f,
                view: default, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.IsTrue(p.Emit[0].IsWorld && p.Emit[0].AlongLine, "curved must route to the world sink");
            float2 expectedDelta = SymbolTranslate.ApplyTranslate(float2.zero, translatePx, TextTranslateAnchor.Viewport, 0f);
            Assert.AreEqual(expectedDelta.x, p.Emit[0].TranslateDeltaPx.x, Tol, "world emit must carry text-translate.x");
            Assert.AreEqual(expectedDelta.y, p.Emit[0].TranslateDeltaPx.y, Tol, "world emit must carry text-translate.y");
            Assert.That(math.abs(p.Emit[0].TranslateDeltaPx.x) > Tol || math.abs(p.Emit[0].TranslateDeltaPx.y) > Tol,
                "delta must be nonzero for a nonzero translate (guards the defaulted-to-zero bug)");
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // The curved collision box bounds the icon's INK, not the CurvedGlyph.CellSkirt border the cell draws.
        // A box over the border grows every along-line icon's footprint and changes which symbols win.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(45f)]
        [TestCase(90f)]
        [TestCase(137f)]
        public void BuildRotatedGlyph_WithACellSkirt_EqualsTheSameCellPreShrunkByIt(float rotationDeg)
        {
            const float skirt = 3f;
            var anchor = new float2(120f, 75f);
            float rotation = math.radians(rotationDeg);

            var padded = new SymbolQuad
            {
                TopLeft = new float2(-20f, 9f), BottomRight = new float2(20f, -9f), LineIndex = 0,
            };
            var content = new SymbolQuad
            {
                TopLeft = padded.TopLeft + new float2(skirt, -skirt),
                BottomRight = padded.BottomRight - new float2(skirt, -skirt),
                LineIndex = 0,
            };

            SymbolBox withSkirt = SymbolBox.BuildRotatedGlyph(anchor, padded, 32f, rotation, 4f, skirt);
            SymbolBox preShrunk = SymbolBox.BuildRotatedGlyph(anchor, content, 32f, rotation, 4f, 0f);

            Assert.AreEqual(preShrunk.Min.x, withSkirt.Min.x, Tol, "Min.x");
            Assert.AreEqual(preShrunk.Min.y, withSkirt.Min.y, Tol, "Min.y");
            Assert.AreEqual(preShrunk.Max.x, withSkirt.Max.x, Tol, "Max.x");
            Assert.AreEqual(preShrunk.Max.y, withSkirt.Max.y, Tol, "Max.y");

            // Non-vacuity: the skirt must actually be REMOVED, not merely accepted. Against the same padded
            // cell with skirt 0 the box has to be strictly larger.
            SymbolBox unshrunk = SymbolBox.BuildRotatedGlyph(anchor, padded, 32f, rotation, 4f, 0f);
            Assert.Greater(unshrunk.Max.x - unshrunk.Min.x, (withSkirt.Max.x - withSkirt.Min.x) + Tol,
                "a non-zero cell skirt must SHRINK the box — otherwise this tooth proves nothing");
        }

        /// <summary>Text parity: a text glyph carries <c>CellSkirt == 0</c>, and at 0 the box must be the
        /// plain AABB of the four rotated cell corners plus padding — the pre-skirt formula, independently
        /// recomputed here rather than restated from the implementation.</summary>
        [TestCase(0f)]
        [TestCase(45f)]
        [TestCase(200f)]
        public void BuildRotatedGlyph_AtZeroSkirt_IsThePlainRotatedCornerAabb(float rotationDeg)
        {
            var anchor = new float2(-40f, 12f);
            float rotation = math.radians(rotationDeg);
            const float textSizePx = 48f, paddingPx = 2.5f;
            var cell = new SymbolQuad
            {
                TopLeft = new float2(-7f, 11f), BottomRight = new float2(5f, -3f), LineIndex = 0,
            };

            SymbolBox actual = SymbolBox.BuildRotatedGlyph(anchor, cell, textSizePx, rotation, paddingPx, 0f);

            float scale = textSizePx / TextQuadLayout.OneEm;
            math.sincos(rotation, out float sin, out float cos);
            float2 Rotate(float2 v) => new float2(cos * v.x - sin * v.y, sin * v.x + cos * v.y);
            var corners = new[]
            {
                anchor + Rotate(cell.TopLeft * scale),
                anchor + Rotate(new float2(cell.BottomRight.x, cell.TopLeft.y) * scale),
                anchor + Rotate(cell.BottomRight * scale),
                anchor + Rotate(new float2(cell.TopLeft.x, cell.BottomRight.y) * scale),
            };
            float2 min = corners[0], max = corners[0];
            foreach (float2 c in corners) { min = math.min(min, c); max = math.max(max, c); }

            Assert.AreEqual(min.x - paddingPx, actual.Min.x, Tol, "Min.x");
            Assert.AreEqual(min.y - paddingPx, actual.Min.y, Tol, "Min.y");
            Assert.AreEqual(max.x + paddingPx, actual.Max.x, Tol, "Max.x");
            Assert.AreEqual(max.y + paddingPx, actual.Max.y, Tol, "Max.y");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStagingMathSortKeyTests — SanitizeSortKey normalizes a non-finite key to sort last
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SymbolStagingMath.SanitizeSortKey"/> normalizes a
    /// non-finite baked <c>symbol-sort-key</c> to <c>float.MaxValue</c> (sorts LAST) so
    /// <see cref="SymbolCollision.ComparePlacementOrder(in SymbolCandidate, in SymbolCandidate)"/> stays a strict total
    /// order. A NaN key is intransitive, which makes the unstable heapsort's survivor set depend on input order.
    /// </summary>
    [TestFixture]
    public class SymbolStagingMathSortKeyTests
    {
        [Test]
        public void SanitizeSortKey_NonFinite_NormalizesToMaxValue()
        {
            Assert.AreEqual(float.MaxValue, SymbolStagingMath.SanitizeSortKey(float.NaN),
                "NaN must normalize to float.MaxValue (sorts last — never preempts a good label)");
            Assert.AreEqual(float.MaxValue, SymbolStagingMath.SanitizeSortKey(float.PositiveInfinity),
                "+Inf must normalize to float.MaxValue");
            Assert.AreEqual(float.MaxValue, SymbolStagingMath.SanitizeSortKey(float.NegativeInfinity),
                "-Inf must normalize to float.MaxValue");
        }

        [Test]
        public void SanitizeSortKey_FiniteValues_PassThroughUnchanged()
        {
            // The whole finite range passes through. A strict `< MaxValue` threshold would still pass the NaN/Inf
            // test but would re-sentinel the largest finite key.
            Assert.AreEqual(0f, SymbolStagingMath.SanitizeSortKey(0f), "zero unchanged");
            Assert.AreEqual(1.5f, SymbolStagingMath.SanitizeSortKey(1.5f), "a finite value unchanged");
            Assert.AreEqual(-42f, SymbolStagingMath.SanitizeSortKey(-42f), "a finite negative value unchanged");
            Assert.AreEqual(float.MaxValue, SymbolStagingMath.SanitizeSortKey(float.MaxValue),
                "float.MaxValue is finite (|MaxValue| <= MaxValue) — it must pass through, not be re-sentinelled");
            Assert.AreEqual(float.MinValue, SymbolStagingMath.SanitizeSortKey(float.MinValue),
                "float.MinValue (most-negative finite) is finite and must pass through unchanged");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStagingMathTests — the pure blittable-input staging math extracted from the placement system
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lever C step 1: the pure blittable-input staging math extracted from SymbolPlacementSystem. Hand-computable
    /// cases (axis-aligned point box, horizontal curved line → zero rotation) pin the geometry; the full byte-parity
    /// gate is the engine EditMode Tick tests that now call this same code.
    /// </summary>
    [TestFixture]
    public class SymbolStagingMathTests
    {
        private const float Tol = 1e-4f;

        // Pre-sized output pools + cursors for one staging call (the staging math writes via fixed spans).
        private struct Pools
        {
            public SymbolBox[] Boxes; public int BoxCount;
            public PlacedQuad[] Quads; public int QuadCount;
            public SymbolCandidate[] Candidates; public CandidateEmit[] Emit; public int EmitCount;
            public static Pools New(int maxBoxes = 64, int maxQuads = 256, int maxCandidates = 64) => new Pools
            {
                Boxes = new SymbolBox[maxBoxes], Quads = new PlacedQuad[maxQuads],
                Candidates = new SymbolCandidate[maxCandidates], Emit = new CandidateEmit[maxCandidates],
            };
        }

        private static SymbolQuad Cell(float half) => new SymbolQuad
        {
            TopLeft = new float2(-half, half), BottomRight = new float2(half, -half),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };

        [Test]
        public void StagePoint_AxisAlignedBox_QuadsAndCandidate()
        {
            var s = new PointStageInput
            {
                ScreenPx = new float2(100, 50), Depth = 0.5f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 2f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 7, Slot = 0,
                AllowOverlap = false, IgnorePlacement = false,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                RotationAlignment = AlignmentMode.Viewport, Color = new float4(1, 1, 1, 1),
                FadeId = 123, WasPlacedLastFrame = false,
            };
            var quads = new[] { Cell(6f) };
            var p = Pools.New();

            int staged = SymbolStagingMath.StagePoint(in s, quads, bearingRadians: 0f,
                viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(1, p.BoxCount);
            Assert.AreEqual(1, p.QuadCount);
            // scale = 24/24 = 1 → box = anchor + bounds ± padding.
            Assert.AreEqual(new float2(100 - 12 - 2, 50 - 12 - 2).x, p.Boxes[0].Min.x, Tol);
            Assert.AreEqual(new float2(100 - 12 - 2, 50 - 12 - 2).y, p.Boxes[0].Min.y, Tol);
            Assert.AreEqual(100 + 12 + 2, p.Boxes[0].Max.x, Tol);
            Assert.AreEqual(50 + 12 + 2, p.Boxes[0].Max.y, Tol);

            SymbolCandidate c = p.Candidates[0];
            Assert.AreEqual(0, c.BoxStart); Assert.AreEqual(1, c.BoxCount);
            Assert.AreEqual(1, c.FeatureIndex); Assert.AreEqual(7, c.TileKey);
            Assert.AreEqual(0, c.SymbolIndex); Assert.AreEqual(123, c.FadeId);
            Assert.IsFalse(c.WasPlacedLastFrame);
            // An ORDINARY (unpaired) symbol's candidate satisfies EmitCount == 1, EmitStart == SymbolIndex —
            // one record, one candidate, one emit.
            Assert.AreEqual(1, c.EmitCount, "an ordinary point label has exactly one emit");
            Assert.AreEqual(c.SymbolIndex, c.EmitStart, "EmitStart == SymbolIndex for an unpaired candidate");

            Assert.AreEqual(0, p.Emit[0].QuadStart); Assert.AreEqual(1, p.Emit[0].QuadCount);
            Assert.AreEqual(new float2(100, 50).x, p.Quads[0].AnchorScreenPx.x, Tol);
            Assert.AreEqual(TextQuadLayout.OneEm, p.Quads[0].TextSizePx, Tol);
            Assert.AreEqual(0.5f, p.Quads[0].Depth, Tol);
        }

        // ── icon-rotate on the POINT path, the SIGN tooth. A sign-flipped impl passes the 180° case
        //    (R(pi) == -I is its own inverse), so 90° is the discriminator. ──
        private static PointStageInput RotatedIconInput(float iconRotateRadians, AlignmentMode alignment)
            => new PointStageInput
            {
                ScreenPx = new float2(100, 50), Depth = 0f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 1, TileKey = 7, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                RotationAlignment = alignment, Color = new float4(1, 1, 1, 1),
                AtlasKind = SymbolKind.Icon, IconRotateRadians = iconRotateRadians,
            };

        [Test]
        public void StagePoint_IconRotate_RotatesTheQuadClockwiseOnScreen_AndComposesWithAlignment()
        {
            var quads = new[] { Cell(6f) };

            // (a) Viewport ⇒ icon-rotate is the WHOLE rotation, NEGATED by SymbolBearing.IconRotationRadians:
            // a positive staged angle turns counter-clockwise on screen, and icon-rotate is clockwise-positive.
            PointStageInput viewportInput = RotatedIconInput(math.PI / 2f, AlignmentMode.Viewport);
            var p = Pools.New();
            SymbolStagingMath.StagePoint(in viewportInput, quads,
                bearingRadians: 0.7f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
            Assert.AreEqual(-math.PI / 2f, p.Quads[0].RotationRadians, Tol,
                "viewport + icon-rotate 90 -> exactly -pi/2 (the bearing must not leak in)");

            // (b) Map alignment ⇒ bearing + icon-rotate, one addition, both terms present — and the bearing
            // term is NOT flipped, only the icon-rotate one (a blanket negation would read -0.7 - pi/2).
            PointStageInput mapInput = RotatedIconInput(math.PI / 2f, AlignmentMode.Map);
            var pMap = Pools.New();
            SymbolStagingMath.StagePoint(in mapInput, quads,
                bearingRadians: 0.7f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                pMap.Boxes, ref pMap.BoxCount, pMap.Quads, ref pMap.QuadCount, pMap.Candidates, pMap.Emit, ref pMap.EmitCount);
            Assert.AreEqual(0.7f - math.PI / 2f, pMap.Quads[0].RotationRadians, Tol,
                "map + icon-rotate 90 -> bearing - pi/2 composed");

            // (c) The BOX is NOT rotated by icon-rotate: the point path's box stays the unrotated AABB even
            // under a live bearing.
            Assert.AreEqual(100f - 12f, p.Boxes[0].Min.x, Tol, "the collision box stays the unrotated AABB");
            Assert.AreEqual(100f + 12f, p.Boxes[0].Max.x, Tol);

            // (d) SIGN, observed on the drawn offsets. Non-obvious why: BuildWorldQuad rotates in a y-UP local
            // frame and negates Y, so Offset is y-DOWN and a positive angle shows COUNTER-clockwise on screen.
            // A clockwise +90 icon-rotate is therefore (x, y) -> (-y, x) in Offset; without the negation in
            // SymbolBearing.IconRotationRadians it lands at (y, -x). The derivation alone is ambiguous, so
            // SymbolIconRenderSnapshotTests.AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen settles it.
            SymbolQuad cell = Cell(6f);
            var zeroAnchor = new float3(0f, 0f, 0f);
            var white = new float3(1f, 1f, 1f);
            BillboardMath.BuildWorldQuad(in cell, in zeroAnchor, TextQuadLayout.OneEm, in white, 0f,
                in float2.zero, in zeroAnchor, in zeroAnchor, 0f,
                out _, out WorldBillboardVertex unrotatedTopRight,
                out WorldBillboardVertex unrotatedBottomRight, out _);
            BillboardMath.BuildWorldQuad(in cell, in zeroAnchor, TextQuadLayout.OneEm, in white,
                p.Quads[0].RotationRadians, in float2.zero, in zeroAnchor, in zeroAnchor, 0f,
                out _, out WorldBillboardVertex rotatedTopRight, out _, out _);

            float2 before = unrotatedTopRight.Offset;
            var clockwiseQuarterTurn = new float2(-before.y, before.x);
            Assert.AreEqual(clockwiseQuarterTurn.x, rotatedTopRight.Offset.x, Tol,
                "a +90 deg icon-rotate turns the drawn corner offsets CLOCKWISE on screen, which in the " +
                "y-DOWN Offset frame is (x, y) -> (-y, x)");
            Assert.AreEqual(clockwiseQuarterTurn.y, rotatedTopRight.Offset.y, Tol,
                "…in y too — an un-negated icon-rotate lands at (y, -x), i.e. counter-clockwise on screen");
            // Named-corner cross-check: a clockwise quarter turn moves the TOP-right corner to where the
            // BOTTOM-right corner sat.
            Assert.AreEqual(unrotatedBottomRight.Offset.x, rotatedTopRight.Offset.x, Tol);
            Assert.AreEqual(unrotatedBottomRight.Offset.y, rotatedTopRight.Offset.y, Tol);
            Assert.AreEqual(unrotatedTopRight.Uv.x, rotatedTopRight.Uv.x, Tol, "UVs never rotate with the corners");
            Assert.AreEqual(unrotatedTopRight.Uv.y, rotatedTopRight.Uv.y, Tol);

            // (e) 180 deg negates every corner offset exactly (the liberty `_opposite` case). This case is
            // sign-BLIND — R(pi) == -I is its own inverse — which is why (d) carries the sign coverage.
            PointStageInput flipInput = RotatedIconInput(math.PI, AlignmentMode.Viewport);
            var pFlip = Pools.New();
            SymbolStagingMath.StagePoint(in flipInput, quads,
                bearingRadians: 0f, viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                pFlip.Boxes, ref pFlip.BoxCount, pFlip.Quads, ref pFlip.QuadCount, pFlip.Candidates, pFlip.Emit, ref pFlip.EmitCount);
            BillboardMath.BuildWorldQuad(in cell, in zeroAnchor, TextQuadLayout.OneEm, in white,
                pFlip.Quads[0].RotationRadians, in float2.zero, in zeroAnchor, in zeroAnchor, 0f,
                out _, out WorldBillboardVertex flippedTopRight, out _, out _);
            Assert.AreEqual(-unrotatedTopRight.Offset.x, flippedTopRight.Offset.x, Tol, "180 deg negates x");
            Assert.AreEqual(-unrotatedTopRight.Offset.y, flippedTopRight.Offset.y, Tol, "180 deg negates y");
        }

        // AtlasKind (the icon/text discriminator) must ride through emit[ordinal] unchanged — a plain
        // pass-through, not yet consumed anywhere, but the thread must not silently drop it.
        [Test]
        public void StagePoint_IconAtlasKind_CarriesThroughToEmit()
        {
            var s = new PointStageInput
            {
                ScreenPx = new float2(0, 0), Depth = 0f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 0, TileKey = 0, Slot = 0,
                TranslateAnchor = TextTranslateAnchor.Viewport, RotationAlignment = AlignmentMode.Viewport,
                Color = new float4(1, 1, 1, 1),
                AtlasKind = SymbolKind.Icon,
            };
            var iconQuad = new SymbolQuad
            {
                TopLeft = new float2(-8, 8), BottomRight = new float2(8, -8),
                UvTopLeft = new float2(0.1f, 0.2f), UvBottomRight = new float2(0.3f, 0.4f),
            };
            var quads = new[] { iconQuad };
            var p = Pools.New();

            int staged = SymbolStagingMath.StagePoint(in s, quads, bearingRadians: 0f,
                viewportLogicalPx: new double2(1920, 1080), ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(SymbolKind.Icon, p.Emit[0].AtlasKind, "emit must carry the icon discriminator");
            Assert.AreEqual(iconQuad.UvTopLeft.x, p.Quads[0].Quad.UvTopLeft.x, Tol);
            Assert.AreEqual(iconQuad.UvTopLeft.y, p.Quads[0].Quad.UvTopLeft.y, Tol);
        }

        [Test]
        public void StagePoint_DefaultAtlasKind_IsText()
        {
            var s = new PointStageInput
            {
                ScreenPx = new float2(0, 0), Projected = true,
                BoundsMin = new float2(-1, -1), BoundsMax = new float2(1, 1),
                TextSizePx = TextQuadLayout.OneEm,
                TranslateAnchor = TextTranslateAnchor.Viewport, RotationAlignment = AlignmentMode.Viewport,
                Color = new float4(1, 1, 1, 1),
                // AtlasKind left at its default — no existing caller sets it.
            };
            var quads = new[] { Cell(1f) };
            var p = Pools.New();

            SymbolStagingMath.StagePoint(in s, quads, 0f, new double2(1920, 1080), 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(SymbolKind.Text, p.Emit[0].AtlasKind, "default AtlasKind must be Text (zero value)");
        }

        [Test]
        public void StagePoint_BehindCamera_StagesNothing()
        {
            var s = new PointStageInput { Projected = false };
            var quads = new[] { Cell(6f) };
            var p = Pools.New();
            int staged = SymbolStagingMath.StagePoint(in s, quads, 0f, new double2(1920, 1080), 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
            Assert.AreEqual(0, staged);
            Assert.AreEqual(0, p.BoxCount);
            Assert.AreEqual(0, p.QuadCount);
        }

        [Test]
        public void StageCurved_HorizontalLine_ZeroRotationBoxAtAnchor()
        {
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 3, TileKey = 9, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                MaxAngleDeg = 45f, KeepUpright = true, Color = new float4(1, 1, 1, 1),
            };
            var screenPath = new[] { new float2(0, 0), new float2(100, 0) };
            var depthPath  = new[] { 0.3f, 0.7f };            // mid vertex (index 1) → pathDepth 0.7
            var validPath  = new byte[] { 1, 1 };
            // World path mirrors the screen path 1:1 (index-aligned, per StageJob's contract) — a flat
            // east-only line at Y=Z=0, TileOriginRender left at its float3-zero default.
            var worldPath  = new[] { new double3(0, 0, 0), new double3(100, 0, 0) };
            var worldUpPath = new float3[worldPath.Length]; // unread by this box/tangent tooth
            var glyphs     = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(6f) } };
            var anchors    = new[] { new LineAnchor(0, 0.5f) };  // arc distance 50 along the 100-px line
            long fid = SymbolStagingMath.LineFadeId(9, 0, 3, 0);
            var fadeIds    = new[] { fid, SymbolStagingMath.LineFadeId(9, 0, 3, -1) };
            var wasPlaced  = new byte[] { 0, 0 };
            var pathPoints = new float2[2];
            var cumulativeLengths  = new float[2];
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath, glyphs, anchors,
                fadeIds, wasPlaced, pathPoints, cumulativeLengths, bearingRadians: 0f, view: default, ordinal: 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(1, staged);
            Assert.AreEqual(1, p.BoxCount);
            Assert.AreEqual(1, p.QuadCount);
            // Horizontal line, rotation 0, scale 1, no padding → axis-aligned 12×12 box centred at (50,0).
            Assert.AreEqual(44f, p.Boxes[0].Min.x, Tol); Assert.AreEqual(-6f, p.Boxes[0].Min.y, Tol);
            Assert.AreEqual(56f, p.Boxes[0].Max.x, Tol); Assert.AreEqual(6f, p.Boxes[0].Max.y, Tol);

            SymbolCandidate c = p.Candidates[0];
            Assert.AreEqual(1, c.BoxCount); Assert.AreEqual(fid, c.FadeId);
            // A curved symbol is never paired — EmitCount == 1, EmitStart == SymbolIndex.
            Assert.AreEqual(1, c.EmitCount, "a curved label has exactly one emit");
            Assert.AreEqual(c.SymbolIndex, c.EmitStart, "EmitStart == SymbolIndex for a curved candidate");
            Assert.AreEqual(new float2(50, 0).x, p.Quads[0].AnchorScreenPx.x, Tol);
            Assert.AreEqual(0f, p.Quads[0].AnchorScreenPx.y, Tol);
            Assert.AreEqual(0.7f, p.Quads[0].Depth, Tol);
            // The world sample at the same arc=50 midpoint, narrowed against the default-zero
            // TileOriginRender — AnchorLocal == the world point itself; Tangent is the unit +X direction.
            Assert.AreEqual(50f, p.Quads[0].AnchorLocal.x, Tol);
            Assert.AreEqual(0f, p.Quads[0].AnchorLocal.y, Tol);
            Assert.AreEqual(0f, p.Quads[0].AnchorLocal.z, Tol);
            Assert.AreEqual(1f, p.Quads[0].Tangent.x, Tol);
            Assert.AreEqual(0f, p.Quads[0].Tangent.y, Tol);
        }

        [Test]
        public void StageCurved_SymbolLongerThanLine_StagesNothing()
        {
            var s = new CurvedStageInput { TextSizePx = TextQuadLayout.OneEm, MaxAngleDeg = 45f, KeepUpright = true,
                Color = new float4(1, 1, 1, 1), TranslateAnchor = TextTranslateAnchor.Viewport };
            var screenPath = new[] { new float2(0, 0), new float2(10, 0) };   // 10-px line
            var depthPath  = new[] { 0f, 0f };
            var validPath  = new byte[] { 1, 1 };
            var worldPath  = new[] { new double3(0, 0, 0), new double3(10, 0, 0) };
            var worldUpPath = new float3[worldPath.Length]; // unread by this too-long-to-fit tooth
            // glyph span 0..100 baked-px * scale 1 = 100 px symbol, longer than the 10-px line → never fits.
            var glyphs     = new[] { new CurvedGlyph { ArcCenter = 0f, Cell = Cell(6f) },
                                     new CurvedGlyph { ArcCenter = 100f, Cell = Cell(6f) } };
            var anchors    = new[] { new LineAnchor(0, 0.5f) };
            var fadeIds    = new[] { 0L, 0L };
            var wasPlaced  = new byte[] { 0, 0 };
            var p = Pools.New();

            int staged = SymbolStagingMath.StageCurved(in s, screenPath, depthPath, validPath, worldPath, worldUpPath, glyphs, anchors,
                fadeIds, wasPlaced, new float2[2], new float[2], 0f, default, 0,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            Assert.AreEqual(0, staged);
            Assert.AreEqual(0, p.BoxCount);
        }

        // ── C7 — a masked pair leaves every OTHER candidate shape untouched, and does not perturb
        //    the box-range tiling / fade-id uniqueness the collision + fade layers depend on. ──────────────
        [Test]
        public void OptionalPair_DoesNotLeakIntoLoneOrCurvedCandidates_OrBreakRangeTiling()
        {
            var p = Pools.New();
            var quads = new[] { Cell(6f) };
            int candidateCount = 0;

            PointStageInput Lone(int featureIndex, float2 screenPx, long fadeId, SymbolKind kind) => new PointStageInput
            {
                ScreenPx = screenPx, Depth = 0f, Projected = true,
                BoundsMin = new float2(-12, -12), BoundsMax = new float2(12, 12),
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = featureIndex, TileKey = 7, Slot = 0,
                TranslatePx = float2.zero, TranslateAnchor = TextTranslateAnchor.Viewport,
                RotationAlignment = AlignmentMode.Viewport, Color = new float4(1, 1, 1, 1),
                AtlasKind = kind, FadeId = fadeId,
            };

            PointStageInput lonePoint = Lone(1, new float2(100, 100), 1001L, SymbolKind.Text);
            PointStageInput loneIcon  = Lone(2, new float2(300, 100), 1002L, SymbolKind.Icon);
            candidateCount += SymbolStagingMath.StagePoint(in lonePoint, quads, 0f, new double2(1920, 1080), candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);
            candidateCount += SymbolStagingMath.StagePoint(in loneIcon, quads, 0f, new double2(1920, 1080), candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            var curved = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 0f, SortKey = 0f,
                FeatureIndex = 5, TileKey = 9, Slot = 0,
                TranslateAnchor = TextTranslateAnchor.Viewport, MaxAngleDeg = 45f, KeepUpright = true,
                Color = new float4(1, 1, 1, 1),
            };
            long curvedFadeId = SymbolStagingMath.LineFadeId(9, 0, 5, 0);
            candidateCount += SymbolStagingMath.StageCurved(in curved,
                new[] { new float2(0, 400), new float2(200, 400) }, new[] { 0f, 0f }, new byte[] { 1, 1 },
                new[] { new double3(0, 0, 0), new double3(200, 0, 0) },
                new float3[2], // unread by this pair-shape/range-tiling tooth
                new[] { new CurvedGlyph { ArcCenter = -8f, Cell = Cell(6f) }, new CurvedGlyph { ArcCenter = 8f, Cell = Cell(6f) } },
                new[] { new LineAnchor(0, 0.5f) },
                new[] { curvedFadeId, SymbolStagingMath.LineFadeId(9, 0, 5, -1) }, new byte[] { 0, 0 },
                new float2[2], new float[2], 0f, default, candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount);

            // A BOTH-optional pair with a stale last-frame verdict, the shape most likely to leak. Staged LAST:
            // a pair consumes TWO emits, so any later candidate has EmitStart > SymbolIndex.
            PointStageInput owner = Lone(3, new float2(500, 100), 1003L, SymbolKind.Icon);
            owner.PairOptional = true;
            PointStageInput rider = Lone(4, new float2(500, 100), 1004L, SymbolKind.Text);
            rider.PairOptional = true;
            rider.TranslatePx = new float2(60f, 0f);
            candidateCount += SymbolStagingMath.StagePointPair(in owner, in rider, quads, quads, 0f,
                new double2(1920, 1080), candidateCount,
                p.Boxes, ref p.BoxCount, p.Quads, ref p.QuadCount, p.Candidates, p.Emit, ref p.EmitCount,
                droppedHalvesLastFrame: 0b10);

            Assert.AreEqual(4, candidateCount, "precondition: lone point + lone icon + curved + pair all staged");

            // The three NON-pair candidates keep their pre-stage-C shape exactly.
            foreach (int i in new[] { 0, 1, 2 })
            {
                SymbolCandidate c = p.Candidates[i];
                Assert.AreEqual(0, c.OptionalBoxMask, $"candidate[{i}] must carry no optional mask");
                Assert.AreEqual(0, c.DroppedBoxMask, $"candidate[{i}] must carry no dropped mask");
                Assert.AreEqual(1, c.EmitCount, $"candidate[{i}] must have exactly one emit");
                Assert.AreEqual(c.SymbolIndex, c.EmitStart, $"candidate[{i}]: EmitStart == SymbolIndex");
            }
            Assert.AreEqual(1, p.Candidates[0].BoxCount, "a lone point label is a one-box candidate");
            Assert.AreEqual(1, p.Candidates[1].BoxCount, "a lone icon label is a one-box candidate");
            Assert.AreEqual(2, p.Candidates[2].BoxCount, "the curved label keeps its per-glyph boxes");

            // The pair carries the mask, and the seeded verdict is masked to its optional halves — the RANGE
            // is untouched (shrinking it on a drop would desync the tiling invariant and the node bound).
            SymbolCandidate pair = p.Candidates[3];
            Assert.AreEqual(0b11, pair.OptionalBoxMask, "both halves optional");
            Assert.AreEqual(0b10, pair.DroppedBoxMask, "last frame's per-half verdict is seeded for the emit gate");
            Assert.AreEqual(2, pair.BoxCount, "a dropped half must NOT shrink the box range");
            Assert.AreEqual(2, pair.EmitCount, "a dropped half must NOT shrink the emit range");

            Assert.IsFalse(SymbolCandidate.TryFindRangeTilingViolation(
                    new System.ReadOnlySpan<SymbolCandidate>(p.Candidates, 0, candidateCount), p.BoxCount,
                    out int bad, out int expected),
                $"candidate box ranges must still tile [0,{p.BoxCount}) — candidate[{bad}] expected BoxStart {expected}");

            var seen = new System.Collections.Generic.HashSet<long>();
            for (int i = 0; i < candidateCount; i++)
                Assert.IsTrue(seen.Add(p.Candidates[i].FadeId),
                    $"candidate[{i}]'s FadeId must be unique — a pair contributes ONE id (the owner's), not two");
        }

        // LineFadeId folds in the LAYER: FeatureIndex restarts per layer, so two roads in different layers of one
        // tile share (tileKey, featureIndex, anchorIndex), and colliding ids leave one stuck at partial opacity.
        [Test]
        public void LineFadeId_DiffersByLayer_ForSameTileFeatureAnchor()
        {
            const long tile = 246313140626018L;
            long layerA = SymbolStagingMath.LineFadeId(tile, layerId: 0, featureIndex: 0, anchorIndex: 0);
            long layerB = SymbolStagingMath.LineFadeId(tile, layerId: 1, featureIndex: 0, anchorIndex: 0);
            Assert.AreNotEqual(layerA, layerB, "two layers' feature-0 anchor-0 must not share a fade id");

            // Stable identity: the same (tile, layer, feature, anchor) always hashes identically (cross-frame fade
            // continuity depends on it), and the other dimensions still distinguish.
            Assert.AreEqual(layerA, SymbolStagingMath.LineFadeId(tile, 0, 0, 0), "same key must be stable");
            Assert.AreNotEqual(layerA, SymbolStagingMath.LineFadeId(tile, 0, 1, 0), "feature dimension distinguishes");
            Assert.AreNotEqual(layerA, SymbolStagingMath.LineFadeId(tile, 0, 0, 1), "anchor dimension distinguishes");
            Assert.AreNotEqual(layerA, SymbolStagingMath.LineFadeId(tile + 1, 0, 0, 0), "tile dimension distinguishes");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStringTableTests — the string-to-int table behind the cross-tile dedup key
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SymbolStringTable"/> — the string→int table that lets the
    /// cross-tile dedup key partition by an integer id instead of a string hash. Its BIJECTION within a
    /// lifetime (different string ⇒ different id, ordinal) is the property the dedup partition-preservation
    /// rests on, so it gets its own falsifiable teeth here.
    /// </summary>
    [TestFixture]
    public class SymbolStringTableTests
    {
        // ── Idempotent: the same string always returns the same id (a re-stringTable is a lookup). ──
        [Test]
        public void Intern_SameString_ReturnsSameId()
        {
            var stringTable = new SymbolStringTable();
            int first = stringTable.Intern("Main St");
            Assert.AreEqual(first, stringTable.Intern("Main St"), "the same string interns to a stable id");
            Assert.AreEqual(1, stringTable.Count, "…and does not add a second mapping");
        }

        // ── The perfect-hash property: distinct strings get distinct ids (the bijection tooth). ──
        [Test]
        public void Intern_DistinctStrings_ReturnDistinctIds()
        {
            var stringTable = new SymbolStringTable();
            int a = stringTable.Intern("Paris");
            int b = stringTable.Intern("Lyon");
            int c = stringTable.Intern("Nice");
            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(b, c);
            Assert.AreNotEqual(a, c);
            Assert.AreEqual(3, stringTable.Count, "three distinct strings ⇒ three mappings");
        }

        // ── null → the 0 sentinel (inert: a null Text/IconImage never becomes a winner). ──
        [Test]
        public void Intern_Null_ReturnsZeroSentinel()
        {
            var stringTable = new SymbolStringTable();
            Assert.AreEqual(0, stringTable.Intern(null), "null maps to the 0 sentinel");
            Assert.AreNotEqual(0, stringTable.Intern("x"), "a real string never gets id 0 (reserved)");
        }

        // ── Ordinal, case-SENSITIVE — must match CrossTileSymbolKey.Equals's `Text == other.Text` semantics. An
        //    OrdinalIgnoreCase/culture comparer would fail this (and silently over-merge "Main St" vs "MAIN ST"). ──
        [Test]
        public void Intern_IsOrdinal_CaseSensitive()
        {
            var stringTable = new SymbolStringTable();
            Assert.AreNotEqual(stringTable.Intern("A"), stringTable.Intern("a"), "ordinal ⇒ 'A' and 'a' are distinct ids");
        }

        // ── Reset clears every mapping and restarts ids at 1; a string re-interns consistently thereafter. ──
        [Test]
        public void Reset_ClearsAndRestarts()
        {
            var stringTable = new SymbolStringTable();
            stringTable.Intern("first");
            stringTable.Intern("second");
            Assert.AreEqual(2, stringTable.Count);

            stringTable.Reset();
            Assert.AreEqual(0, stringTable.Count, "Reset drops every mapping");
            Assert.AreEqual(1, stringTable.Intern("fresh"), "…and ids restart at 1");
            Assert.AreEqual(1, stringTable.Intern("fresh"), "…still idempotent after a reset");
        }

        // ── Monotonic: interning N distinct strings yields N distinct ascending ids (1..N), never reused. ──
        [Test]
        public void Ids_Monotonic_NeverReusedWithinLifetime()
        {
            var stringTable = new SymbolStringTable();
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < 50; i++)
            {
                int id = stringTable.Intern("s" + i);
                Assert.AreEqual(i + 1, id, "ids ascend monotonically from 1");
                Assert.IsTrue(seen.Add(id), "…and are never reused within a lifetime");
            }
            Assert.AreEqual(50, stringTable.Count);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTileCoverageFilterTests — ClassifyActive writes Keep/Fade/Drop without compacting
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SymbolTileCoverageFilter.ClassifyActive"/>: the per-block classifier that WRITES a per-record
    /// Keep / Fade / Drop decision (a tile that WAS on screen fades out instead of popping) without compacting.
    /// A diagonal viewProj scaled by <see cref="WebMercator.WorldExtent"/> projects a z=0 tile to ~full
    /// viewport (coverage ~1.0) and a deep-zoom tile to coverage ~1e-12, below <see cref="MinCoverage"/>, so
    /// the tests need a big/tiny contrast only, not exact areas.
    /// </summary>
    [TestFixture]
    public class SymbolTileCoverageFilterTests
    {
        private static readonly IProjection Projection = new WebMercatorProjection();
        private static readonly double2 Viewport = new double2(1000, 1000);
        private static readonly double3 SceneOrigin = new double3(0, 0, 0);
        private static readonly float3x3 Rebase = float3x3.identity;

        // clip.x = local.x / WorldExtent, clip.y = local.z / WorldExtent (north → screen vertical), clip.w = 1.
        // local.y (altitude) is 0 for a surface projection, so its column is zero.
        private static readonly float4x4 ViewProj = new float4x4(
            new float4((float)(1.0 / WebMercator.WorldExtent), 0, 0, 0),
            new float4(0, 0, 0, 0),
            new float4(0, (float)(1.0 / WebMercator.WorldExtent), 0, 0),
            new float4(0, 0, 0, 1));

        // z=0's single tile spans the WHOLE Mercator square (±WorldExtent on both axes, set by
        // MaxLatitude) → ~full-viewport coverage.
        private static readonly long BigTileKey = SymbolTileKey.Pack(new TileId { Z = 0, X = 0, Y = 0 });

        // z=19 tile at the same (lon=0, lat=0) origin: side length ≈ 2·WorldExtent / 2^19 ≈ 76m → NDC span
        // ≈ 3.8e-6 → coverage ≈ 1.4e-11. Vanishingly small regardless of threshold.
        private static readonly long TinyTileKey = SymbolTileKey.Pack(new TileId { Z = 19, X = 262144, Y = 262144 });

        private const double MinCoverage = 0.05;
        private const double GraceSeconds = 0.5;

        // Fresh, empty cross-frame state for a test that doesn't care about history (a first-touch tile).
        private static (HashSet<long> abovePrev, HashSet<long> aboveThisFrame, Dictionary<long, double> departingUntil,
            HashSet<long> fadingOut, Dictionary<long, byte> tileDecisions) FreshState()
            => (new HashSet<long>(), new HashSet<long>(), new Dictionary<long, double>(), new HashSet<long>(), new Dictionary<long, byte>());

        [Test]
        public void ClassifyActive_NoProjection_OrNonPositiveThreshold_IsNoOp_AllKeep()
        {
            // One block, one tile (Tiny), two records on it — fed as the per-block tile keys + per-record block id
            // the subsystem produces (block == tile).
            var blockTileKeys = new List<long> { TinyTileKey };
            var blockId = new List<int> { 0, 0 };
            var isDeparting = new List<byte> { 0, 0 };
            // Pre-seed cross-frame state — the no-op guard must leave it untouched (nothing to reconcile).
            var abovePrev = new HashSet<long> { TinyTileKey };
            var aboveThisFrame = new HashSet<long>();
            var departingUntil = new Dictionary<long, double> { [TinyTileKey] = 10.0 };
            var fadingOut = new HashSet<long>();
            var tileDecisions = new Dictionary<long, byte>();
            var decisions = new List<byte>();
            var blockDecision = new List<byte>();

            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, projection: null, SceneOrigin, ViewProj,
                Viewport, Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0,
                GraceSeconds, tileDecisions, blockDecision, decisions, out int culledViaNullProjection);
            Assert.AreEqual(0, culledViaNullProjection);
            CollectionAssert.AreEqual(
                new[] { SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep }, decisions,
                "null projection ⇒ classify nothing, every record reads Keep");
            Assert.IsTrue(abovePrev.Contains(TinyTileKey), "no-op guard leaves cross-frame state alone");
            Assert.AreEqual(10.0, departingUntil[TinyTileKey]);

            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, Projection, SceneOrigin, ViewProj, Viewport,
                Rebase, minCoverage: 0.0, abovePrev, aboveThisFrame, departingUntil, fadingOut, now: 0.0,
                GraceSeconds, tileDecisions, blockDecision, decisions, out int culledViaZeroThreshold);
            Assert.AreEqual(0, culledViaZeroThreshold);
            CollectionAssert.AreEqual(
                new[] { SymbolTileCoverageFilter.Keep, SymbolTileCoverageFilter.Keep }, decisions,
                "threshold 0 ⇒ classify nothing, every record reads Keep");
        }

        [Test]
        public void ClassifyActive_TwoBlocksShareOneTileKey_CulledCountsRecordsNotBlocks()
        {
            // One block per (source, tile), so two sources over one tile give two blocks sharing a tile key. The
            // tile classifies once, but the Drop count is per RECORD (3), not per block (2) or tile (1).
            var blockTileKeys = new List<long> { TinyTileKey, TinyTileKey }; // two distinct blocks, one tile key
            var blockId = new List<int> { 0, 1, 0 };                          // three records across both blocks
            var isDeparting = new List<byte> { 0, 0, 0 };

            var (abovePrev, aboveThisFrame, departingUntil, fadingOut, tileDecisions) = FreshState();
            var blockDecision = new List<byte>();
            var decisions = new List<byte>();
            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, Projection, SceneOrigin,
                ViewProj, Viewport, Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut,
                now: 0.0, GraceSeconds, tileDecisions, blockDecision, decisions, out int culled);

            CollectionAssert.AreEqual(
                new[] { SymbolTileCoverageFilter.Drop, SymbolTileCoverageFilter.Drop, SymbolTileCoverageFilter.Drop },
                decisions, "every record on the below-coverage tile drops, whichever of the two blocks it rode");
            Assert.AreEqual(3, culled, "culled counts RECORDS (3), not distinct blocks (2) or tiles (1)");
            Assert.AreEqual(1, tileDecisions.Count, "the shared tile key is classified once — both blocks collapse in the scratch");
        }

        [Test]
        public void ClassifyActive_DepartingOnlyBlock_IsNotClassified_LeavesCrossFrameStateUntouched()
        {
            // A departing-only block must touch no cross-frame state, or a tile still in AbovePrev re-enters as Fade
            // where it should Drop. Departing records short-circuit to Keep, so assert the CROSS-FRAME state.
            var blockTileKeys = new List<long> { BigTileKey, TinyTileKey };
            var blockId = new List<int> { 0, 1 };
            var isDeparting = new List<byte> { 0, 1 };

            var abovePrev = new HashSet<long> { TinyTileKey }; // departing tile WAS above last frame
            var aboveThisFrame = new HashSet<long>();
            var departingUntil = new Dictionary<long, double>();
            var fadingOut = new HashSet<long>();
            var tileDecisions = new Dictionary<long, byte>();
            var blockDecision = new List<byte>();
            var decisions = new List<byte>();

            SymbolTileCoverageFilter.ClassifyActive(blockTileKeys, blockId, isDeparting, Projection, SceneOrigin,
                ViewProj, Viewport, Rebase, MinCoverage, abovePrev, aboveThisFrame, departingUntil, fadingOut,
                now: 5.0, GraceSeconds, tileDecisions, blockDecision, decisions, out int culled);

            Assert.AreEqual(SymbolTileCoverageFilter.Keep, decisions[0], "active Big tile is above → Keep");
            Assert.AreEqual(SymbolTileCoverageFilter.Keep, decisions[1], "departing record short-circuits to Keep");
            Assert.AreEqual(0, culled, "nothing dropped");
            Assert.IsTrue(tileDecisions.ContainsKey(BigTileKey), "sanity: the ACTIVE block WAS classified");
            // The fence: the departing-only tile is never classified, so it mutates NO cross-frame state.
            Assert.IsFalse(tileDecisions.ContainsKey(TinyTileKey), "departing-only tile must not be classified at all");
            Assert.IsFalse(departingUntil.ContainsKey(TinyTileKey), "departing-only tile must not get a fade deadline stamped");
            Assert.IsFalse(fadingOut.Contains(TinyTileKey), "departing-only tile must not be marked fading");
            Assert.IsFalse(aboveThisFrame.Contains(TinyTileKey), "departing-only tile must not enter the above set");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTileCoverageTests — the per-tile screen-coverage pre-cull metric
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The per-tile screen-coverage pre-cull metric. Teeth: a full-screen tile reports ~1.0 coverage (survives), a
    /// tiny tile reports its shoelace fraction (culled below the threshold), a behind-camera corner yields +inf
    /// (never culled — straddles the near plane), and a non-positive threshold DISABLES the cull (a mis-wired
    /// caller must not cull everything — mirrors <see cref="SymbolFarPlaneCull"/>).
    /// </summary>
    [TestFixture]
    public class SymbolTileCoverageTests
    {
        private static readonly double2 Viewport = new double2(1000, 1000);
        private static readonly double3 Origin = new double3(0, 0, 0);

        // Identity viewProj + origin 0: a render point (x,y,z) → NDC (x,y) → screen ((x/2+0.5)*W, (y/2+0.5)*H).
        // So a corner ring in NDC directly controls the projected quad's screen area.
        private static readonly float4x4 Identity = float4x4.identity;

        // A viewProj whose 4th column forces clip.w = -1 for every point (mul(m,(x,y,z,1)).w = -1) → every corner
        // reads as behind the camera (clip.w <= 0), so ScreenCoverage returns +inf.
        private static readonly float4x4 AllBehind = new float4x4(
            new float4(1, 0, 0, 0), new float4(0, 1, 0, 0), new float4(0, 0, 1, 0), new float4(0, 0, 0, -1));

        [Test]
        public void ScreenCoverage_FullViewportTile_IsOne()
        {
            // NDC corners (-1,-1),(1,-1),(1,1),(-1,1) → screen (0,0),(1000,0),(1000,1000),(0,1000) → area == viewport.
            double c = SymbolTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            Assert.AreEqual(1.0, c, 1e-6);
            Assert.IsFalse(SymbolTileCoverage.IsCulled(c, 0.05), "a full-screen tile is never culled");
        }

        [Test]
        public void ScreenCoverage_TinyTile_IsSmallFraction_AndCulled()
        {
            // NDC span 0.2 × 0.2 → coverage 0.25 · 0.2 · 0.2 = 0.01 (1% of the screen).
            double c = SymbolTileCoverage.ScreenCoverage(
                new double3(-0.1, -0.1, 0), new double3(0.1, -0.1, 0), new double3(0.1, 0.1, 0), new double3(-0.1, 0.1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            Assert.AreEqual(0.01, c, 1e-6);
            Assert.IsTrue(SymbolTileCoverage.IsCulled(c, 0.05), "1% < 5% → culled");
            Assert.IsFalse(SymbolTileCoverage.IsCulled(c, 0.005), "1% > 0.5% → kept");
        }

        [Test]
        public void ScreenCoverage_WindingIndependent()
        {
            // Reversed ring order (CW vs CCW) flips the shoelace sign; the abs must yield the same coverage.
            double ccw = SymbolTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            double cw = SymbolTileCoverage.ScreenCoverage(
                new double3(-1, 1, 0), new double3(1, 1, 0), new double3(1, -1, 0), new double3(-1, -1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            Assert.AreEqual(ccw, cw, 1e-9);
        }

        [Test]
        public void ScreenCoverage_BehindCamera_IsInfinite_NeverCulled()
        {
            double c = SymbolTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, AllBehind, Viewport, float3x3.identity);
            Assert.IsTrue(double.IsPositiveInfinity(c), "a behind-camera corner → +inf coverage");
            Assert.IsFalse(SymbolTileCoverage.IsCulled(c, 0.05), "+inf coverage is never culled");
        }

        [Test]
        public void ScreenCoverage_DegenerateViewport_IsInfinite()
        {
            double c = SymbolTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, Identity, new double2(0, 0), float3x3.identity);
            Assert.IsTrue(double.IsPositiveInfinity(c), "a zero-area viewport → cull nothing");
        }

        [Test]
        public void IsCulled_NonPositiveThreshold_DisablesTheCull()
        {
            Assert.IsFalse(SymbolTileCoverage.IsCulled(0.0001, 0.0), "threshold 0 → cull disabled (keep all)");
            Assert.IsFalse(SymbolTileCoverage.IsCulled(0.0001, -1.0), "negative threshold → cull disabled");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTranslateTests — the pure text-translate screen-delta math
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pure <c>text-translate</c> screen-delta math: the engine-free half; the
    /// <c>SymbolPlacementSystem.Tick</c> integration is covered in EditMode. The bearing tests assert
    /// structure, not exact coordinates: the map-under-bearing SIGN is SymbolBearing.MapAlignedSign, which
    /// SymbolIconRenderSnapshotTests.MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing pins.
    /// </summary>
    [TestFixture]
    public class SymbolTranslateTests
    {
        private const float Bearing0 = 0f;

        [Test]
        public void ApplyTranslate_Viewport_RightIsPlusX_DownIsMinusY()
        {
            // MapLibre text-translate [5,3] = right 5, DOWN 3. screenPx is y-up (bottom-left origin), so a
            // downward offset is -3 there; x (right) is unchanged.
            float2 result = SymbolTranslate.ApplyTranslate(new float2(100f, 200f), new float2(5f, 3f), TextTranslateAnchor.Viewport, Bearing0);
            Assert.AreEqual(105f, result.x, 1e-6f, "text-translate x (right) adds to screen x");
            Assert.AreEqual(197f, result.y, 1e-6f, "text-translate y (down) SUBTRACTS from screen y (y-up)");
        }

        [Test]
        public void ApplyTranslate_Zero_IsIdentity()
        {
            var screen = new float2(12.5f, -7.25f);
            float2 result = SymbolTranslate.ApplyTranslate(screen, float2.zero, TextTranslateAnchor.Viewport, Bearing0);
            Assert.AreEqual(screen.x, result.x, 1e-6f);
            Assert.AreEqual(screen.y, result.y, 1e-6f);
        }

        [Test]
        public void ApplyTranslate_NegativeComponents_MoveLeftAndUp()
        {
            // [-4,-6] = left 4, UP 6 → screen x-4, screen y+6.
            float2 result = SymbolTranslate.ApplyTranslate(new float2(50f, 50f), new float2(-4f, -6f), TextTranslateAnchor.Viewport, Bearing0);
            Assert.AreEqual(46f, result.x, 1e-6f);
            Assert.AreEqual(56f, result.y, 1e-6f);
        }

        [Test]
        public void ApplyTranslate_MapAndViewport_CoincideAtBearingZero()
        {
            var screen = new float2(100f, 200f);
            var t = new float2(5f, 3f);
            float2 viewport = SymbolTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Viewport, Bearing0);
            float2 map = SymbolTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Map, Bearing0);
            Assert.AreEqual(viewport.x, map.x, 1e-6f, "map and viewport coincide at bearing 0 (north-up)");
            Assert.AreEqual(viewport.y, map.y, 1e-6f);
        }

        [Test]
        public void ApplyTranslate_Map_UnderBearing_RotatesTheOffset_PreservingMagnitude()
        {
            var screen = new float2(100f, 200f);
            var t = new float2(5f, 3f);
            float bearing = math.PI / 2f; // 90°

            float2 viewport = SymbolTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Viewport, bearing);
            float2 map = SymbolTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Map, bearing);

            // Viewport ignores the bearing; map rotates the delta about the anchor.
            float2 viewportDelta = viewport - screen;
            float2 mapDelta = map - screen;
            Assert.AreEqual(math.length(viewportDelta), math.length(mapDelta), 1e-4f, "rotation preserves the offset magnitude");
            Assert.That(math.distance(mapDelta, viewportDelta), Is.GreaterThan(1e-3f),
                "under a non-zero bearing, map-anchored translate must differ from viewport (the delta is rotated)");
        }
    }
}
