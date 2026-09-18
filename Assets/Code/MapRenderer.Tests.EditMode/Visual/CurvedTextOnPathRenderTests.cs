// Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL
// SymbolPlacementSystem.Tick + the real Map/Symbol/TextWorld shader. NOT registered in
// Tools/core-tests/core-tests.csproj (it renders).
//
// Stage W4 — THE HEADLINE ARM: a curved road symbol's ink sits ON the road, at tilt 0.
//
// WHY THIS FILE EXISTS. The maintainer's very first reported defect was that road symbols render "not on the
// road geometry itself but with some offset, below or above the road" — and it reproduces at tilt 0, so it is
// not a pitch defect at all. The cause is a producer one: CurvedTextLayout baked every cell BASELINE-relative
// while the point path applied TextQuadLayout's optical-centre shift. The gate at W2 was structurally blind
// to it: every downstream curved fixture HAND-BUILDS its CurvedGlyph.Cell, and the two rendered curved
// fixtures borrow the POINT layout as a quad factory. These are the first rendered teeth in this epic whose
// cell comes from the REAL curved producer, which is the whole point of them.
//
// WHY TILT 0 IS THE RIGHT POSE, not a weaker one. This is where the maintainer sees the defect, and it is
// also where the reading is cleanest: at tilt 0 with the road at screen angle 0° the road is one screen row,
// so "off the road" is exactly "off in screen rows". No tilt-dependent convexity, no collision-box-vs-world
// question (that is W3), and the arm is falsifiable exactly where the bug was reported.
//
// EXTREMES, NOT CENTROIDS. Both teeth read minRow/maxRow, never a centroid. A centroid is a faithful position
// reading at tilt 0 only (MapPitchedGlyphSizeTiltZeroTests' header states why, and P3a measured a 12.41 px
// convexity gap the moment tilt was non-zero); bounding-box extremes project exactly and carry the reading
// this stage needs — the ink band's MID-ROW — without borrowing that caveat at all.
//
// '5' IS THE GLYPH, AND THAT IS LOAD-BEARING. It is baseline-resting AND exactly one nominal cap height tall
// on the committed fixture (CurvedTextCentringTests' W4-T1 asserts both from the entry itself), so its ink
// band is EXACTLY symmetric about the anchor after the fix. That is what lets T4 carry a derived bound rather
// than a fitted one: the residual is rasterisation + SDF-threshold error only. A glyph with a descender would
// need a per-glyph ink-bounds correction, and the tooth would then be measuring its own arithmetic.
//
// NO METRE LITERALS: every world length here is a multiple of `scene.MetresPerDevicePixel`, the same rule
// TiltedGroundScene's consumers all enforce — a bare metre literal is sub-pixel at this pose.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
// UnityEngine ALSO declares a TextAnchor (the legacy IMGUI alignment enum) — disambiguate explicitly, the
// same way this file's curved-render sibling handles UnityEngine.Rendering.CameraProperties.
using TextAnchor = MapRenderer.Core.Text.TextAnchor;
using MapRenderer.Tests.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class CurvedTextOnPathRenderTests
    {
        private const int   SizePx     = 512;
        private const float TextSizePx = 160f;

        /// <summary>Half-length of the road, as a multiple of the frame ruler. Only has to exceed the chord
        /// probe's half-width (the symbol is ONE glyph, so its arc span is exactly 0 and the spill gate is
        /// trivially satisfied); 200 leaves a wide margin at both ends.</summary>
        private const double RoadHalfLengthRulerUnits = 200.0;

        /// <summary>
        /// Row-agreement bound, in device pixels. DERIVED, not fitted: for '5' the cap band is exactly
        /// symmetric about the anchor in cell coordinates (<c>CurvedTextCentringTests</c> W4-T1, with its
        /// fixture preconditions), so the only residual left in a rendered reading is rasterisation plus the
        /// SDF alpha threshold — sub-pixel on each edge, and the two edges enter the mid-row averaged. 4.0
        /// device px is 0.6 BAKED px at this text size. The defect this file exists for reads
        /// <c>17.5 · 160/24 = 116.67 px</c>, a 29× discrimination margin.
        /// </summary>
        private const double RowTolerancePx = 4.0;

        /// <summary>An ink reading below this is not a rendered glyph — a blank frame, a GPU-context failure
        /// or a quad collapsed to a point would otherwise pass a row test by agreeing about nothing.
        /// Asserted on EVERY arm.</summary>
        private const int InkFloor = 500;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W4-T4 — the ink sits on the road. The oracle is the PROJECTED ROAD ANCHOR, not the cell.
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W4-T4 — a curved symbol's ink is centred on the road it is drawn along, at tilt 0.</b> Proves the
        /// maintainer's reported defect is gone, in the pose they reported it in, measured from a REAL
        /// <see cref="CurvedTextLayout"/> cell rendered through the real placement system and the real shader.
        ///
        /// <para><b>The oracle shares no code with the arm under test.</b> It is the road anchor's own screen
        /// row, obtained by projecting the anchor with the fixture's camera
        /// (<c>UnityCamera.WorldToScreenPoint</c>) and converting to the top-down row convention the ink
        /// analysis reads in. It does not come from the cell, from
        /// <c>TextQuadLayout.OpticalCentreBelowReferencePx</c>, or from anything W4 edited — the recorded
        /// lesson being P3a's rebuilt-T2, where a reference drawn from the arm under test cancelled the very
        /// defect it was meant to expose.</para>
        ///
        /// <para><b>Why the mid-row of the ink bbox is the right measurement.</b> Cell y = 0 IS the point on
        /// the path: both consumers map the cell linearly and homogeneously about the anchor
        /// (<c>BillboardMath.BuildWorldQuad</c>, <c>SymbolBox.BuildRotatedGlyph</c>), so zero maps to zero
        /// under every branch. '5' being exactly cap-height and baseline-resting, its ink band is symmetric
        /// about that zero, and the rendered band's midpoint must therefore land on the anchor's row.</para>
        ///
        /// <para>RED recipe: remove the shift (the pre-W4 code) ⇒ 116.7 px. Apply it twice ⇒ 116.7 px the
        /// other way. Negate it ⇒ 233 px. The measured residual is printed on every run and recorded in the
        /// W4 dev report as a drift baseline.</para>
        /// </summary>
        [Test]
        public void CurvedTextInk_SitsOnTheRoad_AtTiltZero()
        {
            MeasureBothArms(out RowReading curved, out RowReading point, out double anchorRow);

            double delta = curved.MidRow - anchorRow;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W4-T4  curved ink rows=[{0}, {1}]  mid={2:F3}  anchor row={3:F3}  delta={4:F3} px  ink={5}",
                curved.MinRow, curved.MaxRow, curved.MidRow, anchorRow, delta, curved.Ink));

            Assert.That(math.abs(delta), Is.LessThanOrEqualTo(RowTolerancePx),
                $"W4-T4: a curved label's ink must straddle the road it is drawn along. Ink rows " +
                $"[{curved.MinRow}, {curved.MaxRow}], mid-row {curved.MidRow:F3}, road anchor row " +
                $"{anchorRow:F3} — off by {delta:F3} device px (bound {RowTolerancePx}). At this text size " +
                $"a missing optical-centre shift reads 116.67 px and a doubled one reads the same the other " +
                $"way; a residual of a few px instead means the shift is right and something else drifted.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W4-T5 — the rendered half of the point cross-check, and T4's row-convention control
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W4-T5 — a curved symbol and a centre-anchored POINT symbol of the same glyph sit the same way on
        /// the same anchor, rendered.</b> The render-level statement of the requirement that the two producers
        /// have the same optical relationship to their anchor.
        ///
        /// <para><b>Worth its cost even beside W4-T4, for two distinct reasons.</b> (1) The point arm shares
        /// NO code with the curved producer — <c>TextQuadLayout</c>'s centre shift was fixed by a different
        /// stage (§11 D12), is pinned by <c>TextVerticalCentringTests</c>, and is untouched here, so this is
        /// an independent reference rather than a self-derivation. (2) It is INVARIANT to any mistake in
        /// T4's row/flip convention: both arms are read through the identical scan, so a convention error
        /// cancels here and cannot make this tooth green for the wrong reason. T4 and T5 failing together
        /// means the shift is wrong; T4 alone failing means the row convention is.</para>
        ///
        /// <para>Both arms render at the same <see cref="TextSizePx"/>, from the same atlas, at the same
        /// world anchor, in the same scene and camera — the pair differs in the PRODUCER and nothing else.</para>
        ///
        /// <para>RED recipe: identical to W4-T4's — every injection that moves the curved cell moves this by
        /// the same 116.7 px, because the point arm does not move at all.</para>
        /// </summary>
        [Test]
        public void CurvedTextInk_MatchesThePointPathTwin_AtTiltZero()
        {
            MeasureBothArms(out RowReading curved, out RowReading point, out double anchorRow);

            double delta = curved.MidRow - point.MidRow;
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "W4-T5  curved mid={0:F3} (rows [{1}, {2}])  point mid={3:F3} (rows [{4}, {5}])  " +
                "delta={6:F3} px  (anchor row {7:F3})",
                curved.MidRow, curved.MinRow, curved.MaxRow,
                point.MidRow, point.MinRow, point.MaxRow, delta, anchorRow));

            Assert.That(math.abs(delta), Is.LessThanOrEqualTo(RowTolerancePx),
                $"W4-T5: the curved producer and the centre-anchored point producer must put the same glyph " +
                $"in the same place on the same anchor — curved mid-row {curved.MidRow:F3}, point mid-row " +
                $"{point.MidRow:F3}, off by {delta:F3} device px (bound {RowTolerancePx}). This tooth is " +
                $"blind to T4's row convention (both arms carry it), so a failure here is the curved cell's " +
                $"vertical placement and nothing else.");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Harness
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>One arm's rendered vertical ink signature. EXTREMES only — see this file's header.</summary>
        private readonly struct RowReading
        {
            public readonly int MinRow;
            public readonly int MaxRow;
            public readonly int Ink;

            public RowReading(int minRow, int maxRow, int ink)
            {
                MinRow = minRow;
                MaxRow = maxRow;
                Ink    = ink;
            }

            public double MidRow => 0.5 * (MinRow + MaxRow);
        }

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        /// <summary>
        /// Renders the same glyph twice through ONE scene and ONE camera — once as a CURVED along-line symbol
        /// whose cell comes from the real <see cref="CurvedTextLayout"/>, once as a centre-anchored POINT
        /// symbol whose quads come from <see cref="TextQuadLayout"/> — and also returns the road anchor's own
        /// projected screen row.
        ///
        /// <para>'5' is built here rather than reusing <c>WorldCurvedAbRenderSnapshotTests.BuildGlyphF</c>:
        /// that helper hands back a POINT-layout quad for 'F', and this file needs both the curved producer's
        /// own output and a glyph whose ink band is exactly symmetric about the anchor (the header explains
        /// why '5' and not 'F'). The shaped run is built directly from <see cref="PositionedGlyph"/> — both
        /// layouts step the pen by the atlas entry's own advance, so no shaper/metrics adapter is involved.</para>
        /// </summary>
        private static void MeasureBothArms(out RowReading curved, out RowReading point, out double anchorRow)
        {
            var sceneConfig = new TiltedGroundSceneConfig
            {
                TiltDegrees      = 0.0,   // THE pose — see this file's header.
                SizePx           = SizePx,
                DevicePixelRatio = 1.0,
                BackgroundColor  = Color.white, // WorldSymbolInkAnalysis.InkThreshold reads dark ink on white.
                LitAmbient       = false,       // the symbol arm needs no lit recipe.
            };

            TiltedGroundScene    scene    = null;
            GlyphAtlasTexture    texture  = null;
            SymbolPlacementSystem system   = null;
            TestSymbolPlan       plan     = null;
            SnapshotRenderer     snapshot = null;
            try
            {
                scene = TiltedGroundScene.Create(sceneConfig);

                FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
                var atlas = new GlyphAtlas();
                atlas.Append(stack.Glyphs[(uint)'5'], 0);
                texture = new GlyphAtlasTexture();
                texture.Upload(atlas);

                var run = new ShapedRun
                {
                    Glyphs = new List<PositionedGlyph>
                    {
                        new PositionedGlyph { AtlasCodepoint = (uint)'5', XAdvance = 0f, Cluster = 0 },
                    },
                    Direction = TextDirection.LeftToRight,
                };

                var curvedCells = new List<CurvedGlyph>();
                CurvedTextLayout.Layout(run, atlas, curvedCells);
                Assert.AreEqual(1, curvedCells.Count, "precondition: '5' lays out to exactly one curved cell.");

                var pointQuads = new List<SymbolQuad>();
                TextLayoutOptions pointOptions = new TextLayoutOptions
                {
                    Anchor          = TextAnchor.Center,
                    Offset          = float2.zero,
                    RadialOffset    = 0f,
                    Justify         = TextJustify.Center,
                    MaxWidthEm      = 10f,
                    LineHeightEm    = 1.2f,
                    LetterSpacingEm = 0f,
                };
                TextLayoutBounds pointBounds = TextQuadLayout.Layout(run, atlas, in pointOptions, pointQuads);
                Assert.AreEqual(1, pointQuads.Count, "precondition: '5' lays out to exactly one point quad.");
                Assert.AreEqual(1, pointBounds.LineCount, "precondition: the point twin must be single-line.");

                SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
                double3 origin = frame.SceneOriginRender;
                double mpp = scene.MetresPerDevicePixel;

                // The road, in the render-space XZ plane, at screen angle 0° (east = X; the flat local
                // approximation is exact enough at zero tilt and zero heading). At this pose a screen-
                // horizontal road makes "off the road" exactly "off in screen rows". NO metre literals —
                // every length is a multiple of the frame ruler.
                double halfLen = RoadHalfLengthRulerUnits * mpp;
                var dir = new double3(1.0, 0.0, 0.0);
                double3 pathA = origin - dir * halfLen;
                double3 pathB = origin + dir * halfLen;
                var up = new double3(0.0, 1.0, 0.0); // Web-Mercator scene: up IS +Y.

                long tileKey = TestTileKeys.PackedContaining(sceneConfig.LookAt.Surface, zoom: 14);

                system = new SymbolPlacementSystem(scene.MapCam,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                plan = new TestSymbolPlan(scene.MapCam.Projection);
                snapshot = new SnapshotRenderer(SizePx, SizePx);

                var curvedBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddCurved(curvedBuffer, curvedCells, new[] { new LineAnchor(0, 0.5f) },
                    new[] { pathA, pathB }, new[] { up, up },
                    placement: SymbolPlacement.LineCenter,
                    up: up,
                    paint: SymbolPaint.Default,
                    text: "curved",
                    textSizePx: TextSizePx,
                    maxAngleDeg: 180f,
                    keepUpright: false,
                    // P3a's recorded lesson: at coarse zoom the dedup/collision machinery decides who emits
                    // and a fixture silently loses its symbol.
                    allowOverlap: true,
                    featureIndex: 0,
                    tileKey: tileKey);

                var pointBuffer = new SymbolTileBuffer();
                // Placement left at its default (Point) — this is the point emit path, deliberately.
                TestSymbolTileBuffer.AddPoint(pointBuffer, origin, pointQuads, pointBounds.Min, pointBounds.Max,
                    up: up,
                    paint: SymbolPaint.Default,
                    // Distinct from the curved arm's: PointFadeId hashes (AnchorRender, MaterialIndex, Text,
                    // IconImage), and the two arms share an anchor.
                    text: "point",
                    textSizePx: TextSizePx,
                    allowOverlap: true,
                    featureIndex: 1,
                    tileKey: tileKey);

                curved = RenderArm(scene, system, plan, snapshot, texture, in frame, curvedBuffer, "curved");
                point  = RenderArm(scene, system, plan, snapshot, texture, in frame, pointBuffer,  "point");

                // The oracle. The anchor is the road's midpoint, which is the scene origin, which is Unity
                // world Vector3.zero after RTC.
                //
                // The row convention, derived link by link rather than assumed — SnapshotRenderer's own doc
                // said "top-left origin" until P5, when it turned out to be backwards and cost a debugging
                // round: (1) RawPixels is BOTTOM-UP (row 0 is the bottom scanline, Unity's native ReadPixels
                // convention); (2) FlipRowsVertically turns it top-down, which is what AnalyzeInk scans;
                // (3) WorldToScreenPoint's y is bottom-up in device pixels. So a top-down row r is bottom-up
                // row (SizePx-1-r), whose centre is at screen y = SizePx - r - 0.5, giving r = SizePx - 0.5 - y.
                // The half-pixel is two orders below the bound and is written out rather than dropped so the
                // convention is legible. W4-T5 is the control that does not depend on any of this.
                Vector3 anchorScreen = scene.UnityCamera.WorldToScreenPoint(Vector3.zero);
                anchorRow = SizePx - 0.5 - anchorScreen.y;
            }
            finally
            {
                snapshot?.Dispose();
                plan?.Dispose();
                system?.Dispose();
                texture?.Dispose();
                scene?.Dispose();
            }
        }

        private static RowReading RenderArm(
            TiltedGroundScene scene, SymbolPlacementSystem system, TestSymbolPlan plan,
            SnapshotRenderer snapshot, GlyphAtlasTexture atlas, in SceneFrame frame,
            SymbolTileBuffer buffer, string armName)
        {
            // R3: duplicate Tick — the collision verdict is harvested one Tick late.
            system.Tick(in frame, plan.Build(buffer), atlas);
            system.Tick(in frame, plan.Build(buffer), atlas);
            Assert.That(system.LastQuadCount, Is.EqualTo(1),
                $"W4-T4/T5 precondition ({armName}): the label must stage exactly one quad, got " +
                $"{system.LastQuadCount}. A zero means it spilled its road (StageCurved's centerArc ± halfSpan " +
                "gate) or was culled — nothing measured downstream would mean anything.");

            scene.Render(snapshot);
            var pixels = (byte[])snapshot.RawPixels.Clone();
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, SizePx, SizePx);
            WorldSymbolInkAnalysis.AnalyzeInk(pixels, SizePx, SizePx,
                out int minRow, out int maxRow, out _, out _,
                out _, out _, out int ink);

            Assert.That(ink, Is.GreaterThan(InkFloor),
                $"W4-T4/T5 precondition ({armName}): the arm rendered {ink} ink px (floor {InkFloor}) — a " +
                "blank frame, a GPU-context failure or a collapsed quad. An arm agreeing about nothing is " +
                "not a measurement.");

            return new RowReading(minRow, maxRow, ink);
        }
    }
}
#endif
