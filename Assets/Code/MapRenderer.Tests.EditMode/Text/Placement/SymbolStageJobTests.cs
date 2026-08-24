// Unity EditMode only — needs the job runtime (NativeArray / IJob). NOT registered in core-tests.csproj.
// NOTE: EditMode batch runs the job Burst-compiled; this differential validates that the Burst SymbolStageJob and
// the managed SymbolStagingMath reference make the SAME placement DECISIONS. Unlike SymbolCollisionJob (integer/branch
// logic → bit-identical), staging has float trig (sincos/atan2 per glyph), so Burst may differ ~1 ULP from Mono.
// The hazard is a 1-ULP flip at a text-max-angle / span-fit boundary changing WHICH anchors stage — that shows up
// as a different staged/box/quad COUNT (asserted EXACT) or candidate integer field, not as sub-ULP geometry noise
// (asserted within a tight tolerance). Runs over bent lines whose curvature sits near text-max-angle.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Lever C step 3b: the Burst <see cref="SymbolStageJob"/> must make the same placement decisions as the managed
    /// <see cref="SymbolStagingMath"/> reference it wraps — the differential the codebase requires for a Burst numeric
    /// port (cf. <see cref="SymbolCollisionJobTests"/>).
    /// </summary>
    [TestFixture]
    public class SymbolStageJobTests
    {
        private const float GeomTol = 1e-2f; // px — absorbs Burst-vs-Mono ULP trig noise; a real flip moves counts

        private struct Result
        {
            public int Staged, BoxCount, QuadCount;
            public SymbolBox[] Boxes; public PlacedQuad[] Quads; public SymbolCandidate[] Candidates;
        }

        // The managed reference: SymbolStagingMath.StageCurved over one curved symbol.
        private static Result Managed(in CurvedStageInput s, float2[] screen, float[] depth, byte[] valid,
            double3[] world, float3[] worldUps, CurvedGlyph[] glyphs, LineAnchor[] anchors, long[] fadeIds, byte[] wasPlaced, float bearing,
            SymbolViewTransform view = default)
        {
            int maxBoxes = (anchors.Length + 1) * glyphs.Length + 1;
            var boxes = new SymbolBox[maxBoxes]; var quads = new PlacedQuad[maxBoxes];
            var cands = new SymbolCandidate[anchors.Length + 1]; var emit = new CandidateEmit[anchors.Length + 1];
            int bc = 0, qc = 0, ec = 0;
            int staged = SymbolStagingMath.StageCurved(in s, screen, depth, valid, world, worldUps, glyphs, anchors, fadeIds, wasPlaced,
                // W3: `view` reaches BOTH arms identically (Native assigns the same value to
                // SymbolStageJob.View), so the differential compares the same transform on each side. The
                // bend cases below pass `default`, which keeps them byte-identical to their pre-W3 form;
                // BurstStage_MatchesManaged_MapPitchedCurved passes a real one and is what covers W3's
                // projected-corner arithmetic (F-W3-5, discharged).
                new float2[screen.Length], new float[screen.Length], bearing, view, 0,
                boxes, ref bc, quads, ref qc, cands, emit, ref ec);
            return new Result { Staged = staged, BoxCount = bc, QuadCount = qc, Boxes = boxes, Quads = quads, Candidates = cands };
        }

        // The Burst path: SymbolStageJob over a one-record curved batch mirror.
        private static Result Native(in CurvedStageInput s, float2[] screen, float[] depth, byte[] valid,
            double3[] world, float3[] worldUps, CurvedGlyph[] glyphs, LineAnchor[] anchors, long[] fadeIds, byte[] wasPlaced, float bearing,
            SymbolViewTransform view = default, float metresPerLogicalPixel = 0f)
        {
            int pathLen = screen.Length;
            int maxBoxes = (anchors.Length + 1) * glyphs.Length + 1;
            var alloc = Allocator.TempJob;

            NativeArray<T> One<T>(T v) where T : unmanaged { var a = new NativeArray<T>(1, alloc); a[0] = v; return a; }
            NativeArray<T> From<T>(T[] src) where T : unmanaged { var a = new NativeArray<T>(src.Length, alloc); for (int i = 0; i < src.Length; i++) a[i] = src[i]; return a; }

            var kinds = One<SymbolPlacementKind>(SymbolPlacementKind.Curved); var detail = One(0); var worldCount = One(pathLen);
            var points = new NativeArray<PointStageInput>(1, alloc); var pqs = One(0); var pqc = One(0);
            var curveds = One(s);
            var cgs = One(0); var cgc = One(glyphs.Length); var cas = One(0); var cac = One(anchors.Length); var cafs = One(0);
            var nQuads = new NativeArray<SymbolQuad>(1, alloc);
            var nGlyphs = From(glyphs); var nAnchors = From(anchors); var nFade = From(fadeIds);
            var pointOffset = One(0); var nScreen = From(screen); var nDepth = From(depth); var nValid = From(valid);
            var nWorld = From(world); // Stage AC (curved-world): the gathered world polyline, index-aligned with Screen
            var nWorldUps = From(worldUps); // P2: index-parallel unit surface normals
            // R2: AnchorWasPlaced is now JOB-OWNED scratch — SymbolStageJob.Execute fills it from AnchorFadeIds +
            // Placed. Allocated ZERO-FILLED (NativeArrayOptions.ClearMemory is the default), which is the property
            // doing the work here: a MISSING fill loop reads as not-placed and fails the differential rather than
            // matching by luck. Placed is the native incumbency set the job resolves against, built from the
            // harness's (fadeIds, wasPlaced) ground truth — mirrors SymbolPlacementSystem._placedLastFrame.
            var awp = new NativeArray<byte>(fadeIds.Length, alloc);
            var placed = new NativeHashSet<long>(math.max(1, fadeIds.Length), alloc);
            for (int i = 0; i < fadeIds.Length; i++) if (wasPlaced[i] != 0) placed.Add(fadeIds[i]);
            // Stage C: the job's per-half drop carry. This harness stages a CURVED symbol, which never reads it —
            // but every NativeContainer field on a job must be constructed at schedule time, so it is allocated
            // empty rather than left default.
            var droppedHalves = new NativeHashMap<long, byte>(1, alloc);
            var path = new NativeArray<float2>(pathLen, alloc); var cum = new NativeArray<float>(pathLen, alloc);
            var oBoxes = new NativeArray<SymbolBox>(maxBoxes, alloc); var oQuads = new NativeArray<PlacedQuad>(maxBoxes, alloc);
            var oCands = new NativeArray<SymbolCandidate>(anchors.Length + 1, alloc); var oEmit = new NativeArray<CandidateEmit>(anchors.Length + 1, alloc);
            var counts = new NativeArray<int>(4, alloc);

            new SymbolStageJob
            {
                Kinds = kinds, Detail = detail, WorldCount = worldCount, Count = 1,
                Points = points, PointQuadStart = pqs, PointQuadCount = pqc,
                Curveds = curveds, CurvedGlyphStart = cgs, CurvedGlyphCount = cgc,
                CurvedAnchorStart = cas, CurvedAnchorCount = cac, CurvedAnchorFadeStart = cafs,
                Quads = nQuads, Glyphs = nGlyphs, Anchors = nAnchors, AnchorFadeIds = nFade,
                PointOffset = pointOffset, Screen = nScreen, Depth = nDepth, Valid = nValid, WorldPointsRender = nWorld,
                WorldUpsRender = nWorldUps,
                AnchorWasPlaced = awp, Placed = placed.AsReadOnly(), DroppedHalves = droppedHalves.AsReadOnly(),
                Bearing = bearing, Viewport = new double2(1920, 1080), View = view,
                // ASYMMETRY the two arms must bridge explicitly: SymbolStageJob.Execute PATCHES
                // s.MetresPerLogicalPixel from this per-frame field (SymbolStageJob.cs:178), so the value
                // stored on the CurvedStageInput is IGNORED here while the managed arm reads it straight off
                // `s`. Setting it only on `s` gives the managed arm a live ruler and the Burst arm a zero
                // one, so they silently take different branches and the differential fails on geometry
                // rather than on the real cause. Default 0 keeps the bend cases byte-identical.
                MetresPerLogicalPixel = metresPerLogicalPixel,
                PathScratch = path, CumScratch = cum,
                Boxes = oBoxes, StagedQuads = oQuads, Candidates = oCands, Emit = oEmit, OutCounts = counts,
            }.Run();

            var r = new Result
            {
                Staged = counts[0], BoxCount = counts[1], QuadCount = counts[2],
                Boxes = oBoxes.ToArray(), Quads = oQuads.ToArray(), Candidates = oCands.ToArray(),
            };
            kinds.Dispose(); detail.Dispose(); worldCount.Dispose(); points.Dispose(); pqs.Dispose(); pqc.Dispose();
            curveds.Dispose(); cgs.Dispose(); cgc.Dispose(); cas.Dispose(); cac.Dispose(); cafs.Dispose();
            nQuads.Dispose(); nGlyphs.Dispose(); nAnchors.Dispose(); nFade.Dispose();
            pointOffset.Dispose(); nScreen.Dispose(); nDepth.Dispose(); nValid.Dispose(); nWorld.Dispose(); nWorldUps.Dispose(); awp.Dispose(); placed.Dispose(); droppedHalves.Dispose();
            path.Dispose(); cum.Dispose(); oBoxes.Dispose(); oQuads.Dispose(); oCands.Dispose(); oEmit.Dispose(); counts.Dispose();
            return r;
        }

        // A curved symbol along a polyline with a bend of `bendDeg` at its middle, `maxAngleDeg` = text-max-angle.
        [Test]
        public void BurstStage_MatchesManaged_CurvedBends(
            [Values(0f, 10f, 29f, 31f, 44f, 46f, 90f)] float bendDeg,
            [Values(30f, 45f)] float maxAngleDeg,
            // R2: anchorIncumbent exercises WasPlacedLastFrame != false, which NO prior case here did. This
            // proves index-resolution PLUMBING (the job's relocated fill loop resolves the right fade id to the
            // right boolean) — StageCurved itself never branches on wasPlaced, it only stores it onto the
            // candidate (SymbolStagingMath.cs:305), so this is not a staging-math sensitivity tooth.
            [Values(false, true)] bool anchorIncumbent)
        {
            // A 3-vertex line: straight run, then a bend of bendDeg. Glyphs span the joint so the per-glyph tangent
            // delta straddles maxAngleDeg near the boundary values (29/31, 44/46).
            float rad = math.radians(bendDeg);
            var screen = new[] { new float2(0, 0), new float2(60, 0), new float2(60 + 60 * math.cos(rad), 60 * math.sin(rad)) };
            var depth  = new[] { 0.5f, 0.5f, 0.5f };
            var valid  = new byte[] { 1, 1, 1 };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 40f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 55f, Cell = Cell() }, // straddles the joint at arc 60
                new CurvedGlyph { ArcCenter = 70f, Cell = Cell() },
            };
            var anchors = new[] { new LineAnchor(0, 1f) }; // anchor at the joint (arc 60)
            var fadeIds = new[] { 111L, 222L };            // anchor + centred fallback
            // R2: derive wasPlaced from a NativeHashSet<long> of incumbent fade ids — the SAME lookup shape
            // SymbolStageJob resolves incumbency through in production (both arms resolve off one set). A
            // hand-typed byte[] literal could hand duplicate fade ids inconsistent bytes, an input production can
            // never produce; deriving both arms' input from one set keeps this oracle testing the staging math
            // (well, the plumbing — see the anchorIncumbent comment above), not an impossible input.
            var incumbentFadeIds = anchorIncumbent ? new[] { fadeIds[0] } : System.Array.Empty<long>(); // build-time anchor incumbent, centred fallback not
            var placed = new NativeHashSet<long>(math.max(1, incumbentFadeIds.Length), Allocator.Temp);
            foreach (long id in incumbentFadeIds) placed.Add(id);
            var wasPlaced = new byte[fadeIds.Length];
            for (int i = 0; i < fadeIds.Length; i++) wasPlaced[i] = (byte)(placed.Contains(fadeIds[i]) ? 1 : 0);
            placed.Dispose();
            // Stage AC (curved-world): the gathered WORLD polyline the screen path was projected from — same
            // bend, embedded in the render-space XZ plane (east=X, north=Z), at a nontrivial (nonzero, large)
            // tile origin so the T-ULP bake below exercises a real double-narrow, not a degenerate zero.
            var world = new double3[screen.Length];
            for (int i = 0; i < screen.Length; i++) world[i] = new double3(screen[i].x, 0.0, screen[i].y);
            // P2: a genuinely varying, non-uniform up per vertex — exercises SampleUp's lerp/normalize on both
            // the Burst and managed paths, so a Burst-vs-Mono divergence there would show up as a differential.
            var worldUps = new float3[screen.Length];
            for (int i = 0; i < screen.Length; i++)
                worldUps[i] = math.normalize(new float3(0.1f * i, 1f, 0.05f * i));
            var tileOriginRender = new double3(1_250_000.0, 0.0, -430_000.0);
            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 2f, SortKey = 0f, FeatureIndex = 1, TileKey = 5,
                Slot = 0, TranslateAnchor = TextTranslateAnchor.Viewport, MaxAngleDeg = maxAngleDeg,
                KeepUpright = true, Color = new float4(1, 1, 1, 1), TileOriginRender = tileOriginRender,
            };

            Result m = Managed(in s, screen, depth, valid, world, worldUps, glyphs, anchors, fadeIds, wasPlaced, 0f);
            Result n = Native(in s, screen, depth, valid, world, worldUps, glyphs, anchors, fadeIds, wasPlaced, 0f);

            // Placement DECISIONS must be identical (a ULP flip at the bend would change these).
            Assert.AreEqual(m.Staged, n.Staged, $"staged count (bend {bendDeg}, maxAngle {maxAngleDeg})");
            Assert.AreEqual(m.BoxCount, n.BoxCount, "box count");
            Assert.AreEqual(m.QuadCount, n.QuadCount, "quad count");
            for (int i = 0; i < m.Staged; i++)
            {
                Assert.AreEqual(m.Candidates[i].BoxStart, n.Candidates[i].BoxStart, "candidate BoxStart");
                Assert.AreEqual(m.Candidates[i].BoxCount, n.Candidates[i].BoxCount, "candidate BoxCount");
                Assert.AreEqual(m.Candidates[i].FadeId, n.Candidates[i].FadeId, "candidate FadeId");
                // R2: the tooth for anchorIncumbent — proves the job's relocated fill loop resolved the right
                // fade id to the right incumbency boolean (index-resolution plumbing, not staging math).
                Assert.AreEqual(m.Candidates[i].WasPlacedLastFrame, n.Candidates[i].WasPlacedLastFrame, "candidate WasPlacedLastFrame");
            }
            // Geometry must match within a tight tolerance (ULP trig noise only).
            for (int i = 0; i < m.BoxCount; i++)
            {
                Assert.AreEqual(m.Boxes[i].Min.x, n.Boxes[i].Min.x, GeomTol);
                Assert.AreEqual(m.Boxes[i].Min.y, n.Boxes[i].Min.y, GeomTol);
                Assert.AreEqual(m.Boxes[i].Max.x, n.Boxes[i].Max.x, GeomTol);
                Assert.AreEqual(m.Boxes[i].Max.y, n.Boxes[i].Max.y, GeomTol);
            }
            for (int i = 0; i < m.QuadCount; i++)
            {
                Assert.AreEqual(m.Quads[i].AnchorScreenPx.x, n.Quads[i].AnchorScreenPx.x, GeomTol);
                Assert.AreEqual(m.Quads[i].AnchorScreenPx.y, n.Quads[i].AnchorScreenPx.y, GeomTol);
                Assert.AreEqual(m.Quads[i].RotationRadians, n.Quads[i].RotationRadians, 1e-4f);

                // Stage AC T-ULP: the new baked float3 fields come from a double3 subtract + normalize
                // (rsqrt) computed in BOTH the managed and the Burst path — a REAL cross-backend numeric
                // tolerance (Burst's rsqrt/atan2 diverge from Mono by ULPs), not a snapshot re-bake. Must be
                // compared (never left untested), within a STATED bound: AnchorLocal at GeomTol (same as the
                // screen anchor above — it's the same class of narrowed-double bake); Tangent (a unit vector)
                // at a small absolute tolerance.
                Assert.AreEqual(m.Quads[i].AnchorLocal.x, n.Quads[i].AnchorLocal.x, GeomTol, "AnchorLocal.x");
                Assert.AreEqual(m.Quads[i].AnchorLocal.y, n.Quads[i].AnchorLocal.y, GeomTol, "AnchorLocal.y");
                Assert.AreEqual(m.Quads[i].AnchorLocal.z, n.Quads[i].AnchorLocal.z, GeomTol, "AnchorLocal.z");
                Assert.AreEqual(m.Quads[i].Tangent.x, n.Quads[i].Tangent.x, 1e-5f, "Tangent.x");
                Assert.AreEqual(m.Quads[i].Tangent.y, n.Quads[i].Tangent.y, 1e-5f, "Tangent.y");
                Assert.AreEqual(m.Quads[i].Tangent.z, n.Quads[i].Tangent.z, 1e-5f, "Tangent.z");

                // P2: SurfaceUp is the same class of Burst-vs-Mono normalize(lerp(...)) computation as Tangent
                // above — compared at the same tolerance so a divergence in SampleUp shows up here too.
                Assert.AreEqual(m.Quads[i].SurfaceUp.x, n.Quads[i].SurfaceUp.x, 1e-5f, "SurfaceUp.x");
                Assert.AreEqual(m.Quads[i].SurfaceUp.y, n.Quads[i].SurfaceUp.y, 1e-5f, "SurfaceUp.y");
                Assert.AreEqual(m.Quads[i].SurfaceUp.z, n.Quads[i].SurfaceUp.z, 1e-5f, "SurfaceUp.z");
            }
        }

        /// <summary>
        /// <b>F-W3-5 — Burst-vs-managed BIT parity for W3's projected-corner box.</b> The bend cases above
        /// leave <c>PitchAlignment</c> at <c>Auto</c> with a zero ruler, so W3's branch is unreachable on
        /// BOTH arms and they agree by not running it. This case makes it reachable: map pitch, a live
        /// ruler, and a usable view transform handed identically to each side.
        ///
        /// <para><b>What is actually at stake.</b> The Burst path itself is already exercised — the rendered
        /// W3 teeth read boxes produced by <c>SymbolStageJob</c>, which is
        /// <c>[BurstCompile(CompileSynchronously = true)]</c> and invoked through <c>.Run()</c>, checked
        /// against an independent oracle. What was missing is managed-vs-Burst EQUALITY over the new
        /// arithmetic: a <c>double3</c> corner accumulation, a Gram-Schmidt <c>normalize</c>, a
        /// <c>float4x4</c> multiply and the perspective divide. Burst's rsqrt and fused multiply-add diverge
        /// from Mono by ULPs, which is exactly the class this differential exists to catch.</para>
        ///
        /// <para><b>The view transform is built by hand, not from a camera.</b> <c>float4x4.LookAt</c> is a
        /// camera-to-world transform with +Z forward while <c>PerspectiveFov</c> expects the camera looking
        /// down -Z; composing them the obvious way yields <c>clip.w &lt;= 0</c> on every corner, both arms
        /// fall back to the screen box, and the tooth passes while testing nothing. A literal matrix with a
        /// stated non-unit w row avoids that entirely — the w row is what makes the divide real rather than
        /// a division by one.</para>
        /// </summary>
        [Test]
        public void BurstStage_MatchesManaged_MapPitchedCurved()
        {
            var screen = new[] { new float2(0, 0), new float2(60, 0), new float2(120, 40) };
            var depth  = new[] { 0.5f, 0.5f, 0.5f };
            var valid  = new byte[] { 1, 1, 1 };
            var glyphs = new[]
            {
                new CurvedGlyph { ArcCenter = 40f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 55f, Cell = Cell() },
                new CurvedGlyph { ArcCenter = 70f, Cell = Cell() },
            };
            var anchors = new[] { new LineAnchor(0, 1f) };
            var fadeIds = new[] { 111L, 222L };
            var wasPlaced = new byte[fadeIds.Length];

            var tileOriginRender = new double3(1_250_000.0, 0.0, -430_000.0);
            // The WORLD path must be in METRES, and long enough to hold the symbol: under map pitch the walk
            // is world arc length, so a glyph's ArcCenter is scaled by MetresPerLogicalPixel (40..70 baked
            // becomes 2000..3500 m at this ruler). A path built 1:1 from the screen path is ~132 m and the
            // symbol simply does not fit, which reads as "did not stage" rather than as a failure.
            const float Mpp = 50f;        // the ruler, used for BOTH the world path scale and both arms
            var world = new double3[screen.Length];
            for (int i = 0; i < screen.Length; i++)
                world[i] = tileOriginRender + new double3(screen[i].x * Mpp, 0.0, screen[i].y * Mpp);
            // Non-uniform per-vertex ups, so SampleUp's lerp/normalize and the Gram-Schmidt both run with a
            // varying operand rather than a constant one.
            var worldUps = new float3[screen.Length];
            for (int i = 0; i < screen.Length; i++)
                worldUps[i] = math.normalize(new float3(0.1f * i, 1f, 0.05f * i));

            // Scale render metres into a sane NDC range; the last row gives a genuine non-unit w.
            var view = new SymbolViewTransform
            {
                SceneOriginRender = tileOriginRender,
                Rebase = float3x3.identity,
                ViewProj = new float4x4(
                    0.01f, 0f,    0f,     0f,
                    0f,    0.01f, 0f,     0f,
                    0f,    0f,    0.01f,  0f,
                    0f,    0f,    0.001f, 1f),
                ViewportLogicalPx = new double2(512, 512),
            };
            Assert.That(view.IsUsable, Is.True, "precondition: the view transform must be usable.");

            var s = new CurvedStageInput
            {
                TextSizePx = TextQuadLayout.OneEm, PaddingPx = 2f, SortKey = 0f, FeatureIndex = 1, TileKey = 5,
                Slot = 0, TranslateAnchor = TextTranslateAnchor.Viewport, MaxAngleDeg = 45f,
                KeepUpright = true, Color = new float4(1, 1, 1, 1), TileOriginRender = tileOriginRender,
                PitchAlignment = AlignmentMode.Map, MetresPerLogicalPixel = Mpp,
            };

            Result m = Managed(in s, screen, depth, valid, world, worldUps, glyphs, anchors, fadeIds, wasPlaced, 0f, view);
            // The ruler must be handed to the job explicitly — see the note at the job initializer in Native().
            Result n = Native(in s, screen, depth, valid, world, worldUps, glyphs, anchors, fadeIds, wasPlaced, 0f, view, Mpp);

            Assert.That(m.Staged, Is.GreaterThan(0), "precondition: the label must stage.");
            Assert.AreEqual(m.Staged, n.Staged, "staged count");
            Assert.AreEqual(m.BoxCount, n.BoxCount, "box count");

            // NON-VACUITY: the projected branch must actually have been taken, or this whole tooth is the
            // bend cases again under a different name. The pre-W3 screen box for this cell is
            // TextSizePx/OneEm * 6 baked px + 2 px padding = 8 px half-height; a metre-sized cell at this
            // ruler and matrix is far larger, so a box that still measures ~8 px means the fallback ran.
            Result screenArm = Managed(in s, screen, depth, valid, world, worldUps, glyphs, anchors, fadeIds, wasPlaced, 0f);
            float projectedH = m.Boxes[0].Max.y - m.Boxes[0].Min.y;
            float screenH    = screenArm.Boxes[0].Max.y - screenArm.Boxes[0].Min.y;
            TestContext.WriteLine($"F-W3-5  projected box height={projectedH:F4} px   screen-box height={screenH:F4} px");
            Assert.That(math.abs(projectedH - screenH), Is.GreaterThan(1f),
                $"precondition (non-vacuity): the projected branch must be reachable — projected height " +
                $"{projectedH} vs screen-box height {screenH}. If these match, the parity below is comparing " +
                $"the fallback on both arms and covers nothing of W3.");

            for (int i = 0; i < m.BoxCount; i++)
            {
                Assert.AreEqual(m.Boxes[i].Min.x, n.Boxes[i].Min.x, GeomTol, $"box[{i}].Min.x");
                Assert.AreEqual(m.Boxes[i].Min.y, n.Boxes[i].Min.y, GeomTol, $"box[{i}].Min.y");
                Assert.AreEqual(m.Boxes[i].Max.x, n.Boxes[i].Max.x, GeomTol, $"box[{i}].Max.x");
                Assert.AreEqual(m.Boxes[i].Max.y, n.Boxes[i].Max.y, GeomTol, $"box[{i}].Max.y");
            }
        }

        private static SymbolQuad Cell() => new SymbolQuad
        {
            TopLeft = new float2(-6, 6), BottomRight = new float2(6, -6),
            UvTopLeft = float2.zero, UvBottomRight = new float2(1, 1), LineIndex = 0,
        };
    }
}
