// Unity EditMode only — off-screen GPU render of the REAL icon draw path to a PNG artifact. NOT registered
// in core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D/SpriteSheet).
//
// This is the machine-checkable form of the I5b/I6 "on-screen eyeball": it renders four DISTINCT, deliberately
// ASYMMETRIC demo sprites (a committed fixture — an up-triangle, an "F", a down-arrow, a ring) through the REAL
// LabelPlacementSystem.Tick → Map/Symbol/IconWorld shader → row-flipped SpriteSheet texture, reads the framebuffer
// back, and writes Logs/snapshots/symbol-icons.png. Asymmetric shapes make any vertical flip / horizontal
// mirror visible (a symmetric square could not). The test asserts the frame is non-blank and that the four
// icons' saturated colors are all present (each distinct sprite actually sampled); the human-facing check is
// the saved PNG.
//
// READBACK IS VERTICALLY MIRRORED vs ON-SCREEN (Unity's render-to-texture Y-flip; the shipping path's
// backbuffer blit flips Y, a direct camera→RT readback lacks it — see SymbolAtlasOrientationSnapshotTests'
// header). So this un-mirrors the readback before writing the PNG, so the artifact matches on-screen truth.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolIconRenderSnapshotTests
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string file)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "sprites", file));

        private static string LoadFixtureText(string file)
            => File.ReadAllText(Path.Combine(Application.dataPath, "Fixtures", "sprites", file));

        private static byte[] LoadGlyphFixture(string file)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", file));

        // Minimal metrics provider over one already-appended atlas (mirrors SymbolAtlasOrientationSnapshotTests).
        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;
            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f; return false;
            }
        }

        [Test]
        public void DemoIcons_RenderThroughRealIconPath_WritesPng()
        {
            // 1. Real sprite sheet via the I4 path (LoadImage + the row-flip that matches the glyph-atlas
            //    orientation contract) — the committed asymmetric demo fixture.
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            int2 sheetSize = sheet.View.Size;

            // 2. Overhead camera, white background so the saturated icon colors stand out.
            var camGo = new GameObject("IconRender_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // Icons are drawn with vertex color = white so SAMPLE(_MainTex) * color shows the sprite's true
            // RGBA (LabelPaint.Default is BLACK text ink — it would render every icon black).
            var whitePaint = new LabelPaint
            {
                TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
                HaloColor = default, HaloWidthPx = 0f, HaloBlurPx = 0f,
            };

            // Spread the four sprites across the frame (ground offsets from the look-at; fraction kept modest so
            // they stay comfortably on-frame under the straight-down camera — see the 'A' orientation test).
            double altitude = uCam.transform.position.y;
            double k = altitude * 0.20; // spread the icons toward the corners, clear of the central reference 'A'
            var placements = new (string name, double east, double north)[]
            {
                ("tri-up",     -k,  k),
                ("f-glyph",     k,  k),
                ("arrow-down", -k, -k),
                ("ring",        k, -k),
            };

            // Epic A / A1 Risk R1: a realistic containing tile keeps the world-anchored bake float32-safe
            // (TileKey=0 is ~2e7m away — see SymbolAtlasOrientationSnapshotTests' identical note).
            long tileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            var labels = new List<LabelInstance>();
            for (int i = 0; i < placements.Length; i++)
            {
                (string name, double east, double north) = placements[i];
                // The REPACKED index — SpriteSheet relocates every sprite into its own padded cell, so the
                // raw parsed rect no longer describes the texture being bound below.
                Assert.IsTrue(sheet.View.Index.TryGetSprite(name, out SpriteEntry entry),
                    $"fixture must define sprite '{name}'");
                SymbolQuad quad = IconQuadLayout.Layout(
                    entry, sheetSize, iconSize: 2.0f, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
                labels.Add(new LabelInstance
                {
                    AnchorRender = frame.SceneOriginRender + new double3(east, 0.0, north),
                    Layout = IconQuadLayout.ToLayoutResult(quad, IconQuadLayout.SkirtPx(entry, 2.0f)),
                    Kind = LabelKind.Icon,
                    Paint = whitePaint,
                    TextSizePx = TextQuadLayout.OneEm, // scale 1 — matches the real StyledSymbolTileBuilder icon path
                    IconImage = name,                  // the I6 cross-tile identity discriminant
                    AllowOverlap = true,               // render diagnostic — never collision-cull
                    SortKey = 0f,
                    FeatureIndex = i,
                    TileKey = tileKey,
                });
            }

            // Reference: a REAL text glyph 'A' (known upright on-screen — pinned by SymbolAtlasOrientation-
            // SnapshotTests) rendered at CENTER, so the icons' orientation can be compared against a
            // ground-truth glyph in the same frame (same camera, same un-mirror). If 'A' is upright and an
            // icon is not, the icon path has a real flip.
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadGlyphFixture("0-255.pbf.bytes")).Stacks[0];
            var glyphAtlas = new GlyphAtlas();
            glyphAtlas.Append(stack.Glyphs[65u]);
            GlyphAtlasTexture atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(glyphAtlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(glyphAtlas) });
            TextLayoutResult glyphLayout = TextQuadLayout.Layout(run, glyphAtlas, TextLayoutOptions.Default);
            labels.Add(new LabelInstance
            {
                AnchorRender = frame.SceneOriginRender,
                Layout = glyphLayout,
                Paint = LabelPaint.Default, // black 'A' on white — the upright reference
                TextSizePx = 90f,
                AllowOverlap = true,
                SortKey = 0f,
                FeatureIndex = 99,
                TileKey = tileKey,
            });

            // Epic A / A1: point text + icons draw through the world path (D7) — the ONLY draw path since
            // commit 1 retired the screen materials/path.
            var system = new LabelPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            var snap = new SnapshotRenderer(Size, Size);
            // Every label here is Point placement (icons carry Kind = Icon, not Placement = Line), so the
            // production collect's curved-then-points split cannot reorder them — see
            // SymbolPlanMirrorParityTests for the mixed-kind case where it does.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(labels), atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(labels), atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                Assert.AreEqual(labels.Count, plan.CollectedCount,
                    "precondition: the cross-tile dedup (fixed 4 m grid) must not merge any of these — a short " +
                    "count here would show up below as missing ink rather than as a placement bug.");
                Assert.AreEqual(5, system.LastQuadCount,
                    "all four icon quads + the reference 'A' glyph quad must place (each a 1-quad point candidate; none culled).");

                snap.Render(uCam);
                if (snap.IsAllBlack())
                    Assert.Inconclusive("render is all-black — no GPU context in this batch session (see SnapshotRenderer.IsAllBlack).");

                byte[] raw = snap.RawPixels; // RGBA32, row-major (the raw camera→RT readback).

                // Write the human-facing artifact so it matches the ON-SCREEN orientation. The raw readback is
                // the vertical MIRROR of on-screen (Unity's render-to-texture Y-flip); LoadRawTextureData's
                // bottom-left-origin upload re-flips it, so EncodeToPNG here lands on-screen-upright. (Feeding it
                // an already-un-mirrored buffer would double-flip — the trap this comment guards against.)
                string dir = SnapshotRenderer.GetSnapshotsDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "symbol-icons.png");
                var outTex = new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false);
                outTex.LoadRawTextureData(raw);
                outTex.Apply(updateMipmaps: false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(outTex));
                Object.DestroyImmediate(outTex);
                TestContext.Out.WriteLine($"wrote icon render snapshot: {path}");

                // Analyze the ON-SCREEN frame (un-mirror the readback) — the SAME frame SymbolAtlasOrientation-
                // SnapshotTests uses to prove text is upright, so the icon orientation is compared like-for-like.
                byte[] px = (byte[])raw.Clone();
                FlipRowsVertically(px, Size, Size);

                // Machine check: each distinct sprite actually sampled → its dominant hue must be present.
                // (red triangle, green F, blue arrow, orange ring — count pixels clearly of each hue.)
                CountHues(px, Size, Size, out int red, out int green, out int blue, out int orange);
                Assert.Greater(red, 100, "the red up-triangle sprite must be visible (sampled).");
                Assert.Greater(green, 100, "the green 'F' sprite must be visible (sampled).");
                Assert.Greater(blue, 100, "the blue down-arrow sprite must be visible (sampled).");
                Assert.Greater(orange, 60, "the orange ring sprite must be visible (sampled).");

                // ORIENTATION GUARD (this is the tooth the on-screen eyeball was owed for): the 'tri-up' sprite
                // is an apex-at-TOP triangle, so on screen (the un-mirrored buffer) its ink must be NARROWER at
                // the top than the bottom. A vertically-flipped icon path (the exact bug the SpriteSheet row-flip
                // caused) inverts this — the triangle would point down and this assertion goes RED.
                RedTriangleWidths(px, Size, Size, out float topWidth, out float bottomWidth);
                Assert.Greater(bottomWidth, topWidth * 1.5f,
                    $"the 'tri-up' icon must render apex-UP (upright, matching text): its bottom third " +
                    $"(width {bottomWidth:F1}px) must be clearly wider than its top third ({topWidth:F1}px). " +
                    $"A reversed ratio means the icon render path is vertically flipped (the I4 SpriteSheet " +
                    $"row-flip regression).");
            }
            finally
            {
                snap.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                sheet.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // A6 (P-B) — the ICON shader's along-line TANGENT branch, the one thing no CPU readback can prove:
        // the rotation happens in the vertex shader, from the projected world Tangent. So render the SAME
        // deliberately NON-SQUARE icon quad as an along-line icon on a HORIZONTAL road and on a VERTICAL
        // one, and assert the ink's long axis follows the road. Against an icon pass that ignores
        // tangentOS (its state before P-B) both renders are identical wide bars — RED on the vertical case.
        //
        // Not a golden: the assertion is a RELATION between two renders of the same content, so it needs no
        // committed pixel signature and cannot enshrine a wrong rotation the way a minted golden could.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        // Half-extents chosen 4:1 so the aspect flip dwarfs any AA-level jitter at either orientation.
        private const float LineIconHalfWidthPx = 100f;
        private const float LineIconHalfHeightPx = 25f;

        [Test]
        public void AlongLineIcon_RotatesToTheLineTangent_InkLongAxisFollowsTheRoad()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            try
            {
                Assert.IsTrue(sheet.View.Index.TryGetSprite("arrow-down", out SpriteEntry entry),
                    "precondition: the demo fixture must define the asymmetric 'arrow-down' sprite");

                // The sprite's real UV rect, taken FROM IconQuadLayout rather than restated here, on a
                // deliberately WIDE cell — the cell footprint, not the sprite's aspect, is what makes the
                // orientation legible. Restating the formula would mean this fixture silently disagrees with
                // the production rect whenever that rect changes (it now spans the sprite's padded cell).
                SymbolQuad laidOut = IconQuadLayout.Layout(
                    entry, sheet.View.Size, 1f, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
                var cell = new SymbolQuad
                {
                    TopLeft = new float2(-LineIconHalfWidthPx, LineIconHalfHeightPx),
                    BottomRight = new float2(LineIconHalfWidthPx, -LineIconHalfHeightPx),
                    UvTopLeft = laidOut.UvTopLeft,
                    UvBottomRight = laidOut.UvBottomRight,
                    LineIndex = 0,
                };

                SymbolInk horizontal = MeasureAlongLineIconInk(
                    sheet, cell, "arrow-down", lineAngleDeg: 0f, iconRotateDeg: 0f);
                SymbolInk vertical = MeasureAlongLineIconInk(
                    sheet, cell, "arrow-down", lineAngleDeg: 90f, iconRotateDeg: 0f);

                // Precondition: the horizontal case IS the un-rotated shape, so it must read as a wide bar.
                // (This arm alone cannot discriminate — it passes with or without the tangent branch — which
                // is precisely why the vertical arm is the tooth.)
                Assert.Greater(horizontal.BoxWidth, horizontal.BoxHeight * 2,
                    $"precondition: on a horizontal road the icon must read WIDE " +
                    $"({horizontal.BoxWidth}x{horizontal.BoxHeight} px).");

                Assert.Greater(vertical.BoxHeight, vertical.BoxWidth * 2,
                    $"on a VERTICAL road the same icon must read TALL " +
                    $"({vertical.BoxWidth}x{vertical.BoxHeight} px). " +
                    $"A wide bar here means the icon pass ignored tangentOS and drew the quad unrotated — " +
                    $"every one-way arrow would point screen-right regardless of the road it sits on.");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // A6 (P-B) — the SIGN of the two rotations, which the aspect tooth above cannot see. A bounding box
        // is direction-blind: a quad turned +90° and one turned −90° are the same tall box, and 180° (what
        // `road_one_way_arrow_opposite` asks for) is its own inverse. A sign error therefore survives both
        // the tangent tooth and the icon-rotate teeth while misorienting every arrow on a DIAGONAL road —
        // the common case in real OSM data. This tooth renders at 45°, where sign IS observable, and
        // measures WHERE the ink mass sits rather than how big its box is.
        //
        // ── WHY A CENTROID, AND WHY 45° ──────────────────────────────────────────────────────────────
        // Every rotation in this pipeline acts on the cell's corner offsets ABOUT THE LABEL ANCHOR, so the
        // ink centroid taken about that anchor is an exactly rotation-equivariant observable: rotating the
        // draw by φ rotates that vector by φ, no matter what the shape is. An ink bounding box is not — it
        // only records extent. 45° is the smallest road bearing at which +φ and −φ are distinguishable.
        //
        // ── THE FRAME, CALIBRATED RATHER THAN ASSUMED ────────────────────────────────────────────────
        // Which way "positive" turns in the analysed framebuffer depends on the readback's row order and on
        // whether Unity flipped the projection for the render target — the exact convention this codebase
        // has been burned by before, and NOT something to assume. So the tangent arm calibrates it, using a
        // rotation whose physical sense is known independently of any frame:
        //   · the test road turns from due-EAST to NORTH-EAST — i.e. +45° COUNTER-CLOCKWISE on the map
        //     (east = +X, north = +Z, camera north-up at heading 0);
        //   · a map-aligned line icon follows its road (that is the feature, and the shader derives the
        //     angle in the very frame it applies it, so it holds whatever the frame is);
        //   · therefore whatever signed rotation the ink centroid shows between those two renders IS the
        //     buffer's representation of +45° counter-clockwise on the map.
        // Every claim below is then read off that calibration, which is what makes them frame-independent.
        //
        // ── THE ARMS ─────────────────────────────────────────────────────────────────────────────────
        //   A  road 0°,  icon-rotate 0°   — the un-rotated reference, p₀
        //   B  road 45°, icon-rotate 0°   — must be p₀ turned +45° (the calibration, and the tangent tooth)
        //   C  road 45°, icon-rotate 90°  — differs from B by icon-rotate ALONE, so B→C isolates it.
        // MapLibre defines icon-rotate as "rotates the icon CLOCKWISE", so B→C must be −90° in the sense
        // calibrated above. 90° is used deliberately: 180°, the value `road_one_way_arrow_opposite` asks
        // for and the only value the existing icon-rotate teeth use, is its own inverse and so can never
        // expose a sign error.
        //
        // ── p₀'s OWN DIRECTION (derived from the committed fixture, not from a run) ───────────────────
        // The icon path renders a sprite upright and un-mirrored — pinned by the apex-UP 'tri-up' assertion
        // in the test above, in this same buffer. 'f-glyph' is an "F", so its ink mass sits toward the TOP
        // and the LEFT of its cell: on the committed sprite the ink centroid is at cell px (13.42, 13.33)
        // against a 32-px cell centre of (15.5, 15.5). Scaled onto a square cell of half-extent H that is
        // (−0.130·H, +0.136·H) — up-left, ≈134°, |p₀| ≈ 0.19·H.
        //
        // SPRITE: 'f-glyph', not the 'arrow-down' the aspect test uses. Measured on the committed fixture,
        // arrow-down's ink centroid sits 0.25 px (of 32) from its cell centre — the shape is very nearly
        // centroid-symmetric, so it carries no usable direction signal for a centroid measure however far
        // it is rotated. The "F" is the fixture's genuinely two-dimensional asymmetry, and a DIAGONAL p₀ is
        // what makes the arms land on clearly separated axes.
        //
        // Not a golden: every assertion is either a derived quadrant or a RELATION between two renders of
        // the same content, so no committed pixel signature can enshrine a wrong rotation.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        private const string SignToothSprite = "f-glyph";
        private const float SignToothHalfExtentPx = 100f;

        [Test]
        public void AlongLineIcon_TangentSign_InkTurnsTheSameWayTheRoadTurns()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            try
            {
                SymbolQuad cell = SignToothCell(sheet.View, SignToothHalfExtentPx);
                SymbolInk baseline = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 0f, iconRotateDeg: 0f);
                SymbolInk road45 = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 45f, iconRotateDeg: 0f);

                AssertAnchorIsTheViewportCentre(baseline);

                // The DERIVED baseline direction: an upright, un-mirrored "F" carries its ink mass up and to
                // the left of its cell centre. Asserting it pins the absolute frame — a mirrored icon path
                // would put the mass on the other side, and would also silently invert every sign below.
                float2 p0 = baseline.CentroidFromViewportCentrePx;
                Assert.Less(p0.x, -6f,
                    $"the un-rotated 'F' must carry its ink mass LEFT of the anchor (offset {p0} px). " +
                    $"A positive x here means the icon path draws the sprite horizontally MIRRORED.");
                Assert.Greater(p0.y, 6f,
                    $"the un-rotated 'F' must carry its ink mass ABOVE the anchor (offset {p0} px). " +
                    $"A negative y here means the icon path draws the sprite vertically flipped.");

                AssertRotatedBy(p0, road45.CentroidFromViewportCentrePx, expectedDeg: 45f,
                    "the projected line TANGENT turns the icon the wrong way: the road turned 45° " +
                    "counter-clockwise (east → north-east) and the icon must turn with it. A reversed sign " +
                    "here mirrors every one-way arrow across its road on every diagonal — while leaving the " +
                    "0°/90° aspect tooth above perfectly green, since a mirrored bar is the same bar");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        [Test]
        public void AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            try
            {
                SymbolQuad cell = SignToothCell(sheet.View, SignToothHalfExtentPx);
                SymbolInk baseline = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 0f, iconRotateDeg: 0f);
                SymbolInk road45 = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 45f, iconRotateDeg: 0f);
                SymbolInk road45Rotated90 = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 45f, iconRotateDeg: 90f);

                AssertAnchorIsTheViewportCentre(baseline);

                // CALIBRATION, not a duplicate of the tangent tooth: this is what fixes the SENSE of the
                // measured angle below, by pinning the buffer's +45° against a rotation whose physical
                // direction is known — the road swinging 45° counter-clockwise on the map, which the icon
                // follows. Without it a "+90°" reading below could not be called clockwise or otherwise.
                AssertRotatedBy(baseline.CentroidFromViewportCentrePx, road45.CentroidFromViewportCentrePx, expectedDeg: 45f,
                    "calibration: the icon must follow its road, so this measures the buffer's rendering of " +
                    "+45° counter-clockwise on the map. Nothing below can be interpreted without it");

                // The two 45° renders differ ONLY in icon-rotate, so this delta IS the icon-rotate term.
                // MapLibre: "icon-rotate — Rotates the icon CLOCKWISE." Clockwise is negative in the sense
                // just calibrated, so +90° of icon-rotate must show as −90°.
                AssertRotatedBy(road45.CentroidFromViewportCentrePx, road45Rotated90.CentroidFromViewportCentrePx,
                    expectedDeg: -90f,
                    "icon-rotate turns the icon the WRONG WAY: MapLibre defines icon-rotate as clockwise, " +
                    "and so does every doc comment on the value's way in (SymbolLabel.IconRotateRadians, " +
                    "LabelStageInputs, CandidateEmit.ExtraRotationRadians) — but a positive icon-rotate is " +
                    "rendering counter-clockwise. Note 180°, the only value the other icon-rotate tests use " +
                    "and the value road_one_way_arrow_opposite asks for, is its own inverse and cannot show " +
                    "this. The sense conversion is LabelBearing.IconRotationRadians, the ONE flip point, " +
                    "shared with the point path — a regression there breaks both paths at once");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // LabelBearing.MapAlignedSign — the map-BEARING sign, the last of this file's three rotation signs.
        // It was documented as "not headlessly testable … chosen, not derived … deferred to an eyeball
        // pass", on the grounds that every headless test runs at bearing 0 where map- and viewport-alignment
        // coincide. That premise is wrong: a headless camera takes a heading like any other, and the sibling
        // constant IconRotationRadians — which carried the same "assumed correct" status until it was
        // rendered at a discriminating angle — turned out to be inverted by exactly 180°. So this renders it.
        //
        // ── THE DERIVATION (written from the contract BEFORE the render, per the icon-sign method) ────
        // LabelBearing's stated contract: "+1 = a map heading of θ (CW from north) turns map-aligned labels
        // by +θ in the screen's (y-up) frame." What SHOULD happen is fixed independently by what
        // rotation-alignment:map MEANS — the label is glued to the map plane, so it turns exactly as the map
        // turns on screen, no more and no less. Following that through:
        //   · CameraProperties.Heading is degrees CW from north, and CameraPoseMath derives the camera's
        //     up-vector from the heading direction in the horizontal plane (at heading 0 the camera sits
        //     south of the look-at, looking north). So at heading θ, screen-UP is map bearing θ and
        //     screen-RIGHT is bearing θ+90.
        //   · A map direction of bearing β therefore lands at screen angle 90° − β + θ, measured CCW from
        //     screen-right in a y-up frame (check: β = θ+90 → 0°, β = θ → 90°). Map-EAST, β = 90°, lands at
        //     θ — so raising the heading to θ turns everything drawn on the map by +θ COUNTER-CLOCKWISE on
        //     screen, and a map-aligned label must turn +θ with it.
        //   · The staging frame the sign feeds is positive-CCW-on-screen. That is MEASURED, not assumed —
        //     it is what AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen above pins.
        //   · BillboardRotationRadians hands the quad MapAlignedSign · θ. Matching +θ ⇒ MapAlignedSign = +1.
        // DERIVED EXPECTATION: at heading 45° the map-aligned icon's ink turns +45° (counter-clockwise on
        // screen) — the same way, and by the same amount, as the map underneath it.
        //
        // ── WHY 45°, AND WHY A CENTROID ──────────────────────────────────────────────────────────────
        // Same reasons as the icon-rotate tooth above: 0° cannot separate map from viewport alignment at
        // all, 180° is its own inverse, an axis-aligned bearing cannot separate +θ from −θ, and an ink
        // BOUNDING BOX is direction-blind while the ink centroid taken about the anchor is exactly
        // rotation-equivariant.
        //
        // ── THE MAP'S OWN TURN, MEASURED RATHER THAN ASSUMED ─────────────────────────────────────────
        // "The label turns +45°" is only half the contract; the other half is "…the same way the map does",
        // and asserting that against a NUMBER would smuggle the camera derivation in as an assumption. So
        // two extra arms render a VIEWPORT-aligned probe icon at a deliberately off-centre anchor, placed
        // due map-EAST of the look-at. Its quad never rotates, so its ink box centre tracks its anchor, and
        // where that anchor lands on screen IS the camera's rendering of map-east — pure projection, with no
        // label-rotation math in it at all. The tooth then asserts BOTH that the probe swings +45° (the
        // camera derivation, so a camera-side sign error reports as itself rather than as a label bug) and
        // that the label's turn MATCHES the probe's within a few degrees (the contract proper).
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        private const float MapBearingToothHeadingDeg = 45f;

        /// <summary>The map-east probe's anchor offset, as a fraction of the camera's altitude — ~133 px
        /// from the viewport centre at this fixture's 60° FOV and 512 px square target. Far enough out that
        /// the ~1 px gap between the "F"'s ink box centre and its cell centre is under a degree of bearing
        /// error, close enough that the probe stays comfortably on-frame at every heading.</summary>
        private const double MapEastProbeAnchorFractionOfAltitude = 0.30;

        /// <summary>Small enough that the off-centre probe never approaches the frame edge, and small enough
        /// that its ink box centre is a tight proxy for its anchor.</summary>
        private const float MapEastProbeHalfExtentPx = 30f;

        [Test]
        public void MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            try
            {
                SymbolInk northUp = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: 0f, AlignmentMode.Map, anchorEastFractionOfAltitude: 0.0);
                SymbolInk underBearing = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: MapBearingToothHeadingDeg, AlignmentMode.Map, anchorEastFractionOfAltitude: 0.0);
                SymbolInk mapEastNorthUp = MeasurePointIconInk(sheet, MapEastProbeHalfExtentPx,
                    headingDeg: 0f, AlignmentMode.Viewport, MapEastProbeAnchorFractionOfAltitude);
                SymbolInk mapEastUnderBearing = MeasurePointIconInk(sheet, MapEastProbeHalfExtentPx,
                    headingDeg: MapBearingToothHeadingDeg, AlignmentMode.Viewport, MapEastProbeAnchorFractionOfAltitude);

                AssertAnchorIsTheViewportCentre(northUp);

                // Same absolute-frame pin as the tangent tooth: an upright, un-mirrored "F" carries its ink
                // mass up and to the LEFT. It fixes which way the buffer's y runs before any angle is read.
                float2 unrotated = northUp.CentroidFromViewportCentrePx;
                Assert.Less(unrotated.x, -6f,
                    $"the un-rotated 'F' must carry its ink mass LEFT of the anchor (offset {unrotated} px).");
                Assert.Greater(unrotated.y, 6f,
                    $"the un-rotated 'F' must carry its ink mass ABOVE the anchor (offset {unrotated} px).");

                // The probe's anchor at heading 0: due map-east, so it must sit to the screen RIGHT. This
                // also proves the offset actually reached the frame at a usable size, before it is used as
                // a bearing reference.
                float2 mapEastAtNorthUpPx = InkBoxCentreFromViewportCentrePx(mapEastNorthUp);
                Assert.Greater(mapEastAtNorthUpPx.x, 60f,
                    $"precondition: at heading 0 (north up) an anchor due map-EAST of the look-at must land " +
                    $"well to the RIGHT of the viewport centre — measured {mapEastAtNorthUpPx} px.");
                Assert.Less(math.abs(mapEastAtNorthUpPx.y), 20f,
                    $"precondition: at heading 0 that same anchor must land level with the viewport centre " +
                    $"— measured {mapEastAtNorthUpPx} px.");

                float2 mapEastUnderBearingPx = InkBoxCentreFromViewportCentrePx(mapEastUnderBearing);
                AssertRotatedBy(mapEastAtNorthUpPx, mapEastUnderBearingPx, expectedDeg: MapBearingToothHeadingDeg,
                    "CAMERA, not LabelBearing: raising the heading to 45° (CW from north) must swing the map " +
                    "45° COUNTER-CLOCKWISE on screen, because heading θ puts map bearing θ at screen-up. This " +
                    "arm contains no label-rotation math — only where the camera projects an off-centre " +
                    "anchor — so a failure HERE is a camera-pose finding and says nothing about MapAlignedSign");

                float labelTurnDeg = SignedRotationDeg(unrotated, underBearing.CentroidFromViewportCentrePx);
                float mapTurnDeg = SignedRotationDeg(mapEastAtNorthUpPx, mapEastUnderBearingPx);

                AssertRotatedBy(unrotated, underBearing.CentroidFromViewportCentrePx,
                    expectedDeg: MapBearingToothHeadingDeg,
                    "LabelBearing.MapAlignedSign turns map-aligned labels the WRONG WAY: a heading of 45° " +
                    "(CW from north) must turn a rotation-alignment:map label +45° counter-clockwise on " +
                    "screen, with the map. A -1 sign turns it 45° the other way — 90° of error, invisible at " +
                    "bearing 0 (where map and viewport alignment coincide) and invisible at 180° (its own " +
                    "inverse), which is why this renders at 45");

                // The contract proper — "glued to the map" is a RELATION, and this is the only assertion
                // that states it without routing through a derived number.
                Assert.Less(math.abs(labelTurnDeg - mapTurnDeg), 8f,
                    $"rotation-alignment:map means the label is glued to the map plane, so its on-screen " +
                    $"turn must equal the map's: the label turned {labelTurnDeg:F1}° while the map turned " +
                    $"{mapTurnDeg:F1}° under the same 45° heading.");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        [Test]
        public void ViewportAlignedPointIcon_StaysUnturned_UnderAnActiveBearing()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            try
            {
                SymbolInk northUp = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: 0f, AlignmentMode.Viewport, anchorEastFractionOfAltitude: 0.0);
                SymbolInk underBearing = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: MapBearingToothHeadingDeg, AlignmentMode.Viewport, anchorEastFractionOfAltitude: 0.0);

                AssertAnchorIsTheViewportCentre(northUp);

                // The control that makes the tooth above attributable: it shows the 45° turn measured there
                // comes from the ALIGNMENT MODE and nothing else — not from the camera pose, not from the
                // world-billboard construction, both of which are identical across these two renders.
                AssertRotatedBy(northUp.CentroidFromViewportCentrePx, underBearing.CentroidFromViewportCentrePx,
                    expectedDeg: 0f,
                    "rotation-alignment:viewport must ignore the map bearing entirely — the billboard stays " +
                    "screen-fixed however the map is turned under it. A non-zero turn here means the bearing " +
                    "is leaking past BillboardRotationRadians' alignment branch");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        /// <summary>The sign teeth's sprite and cell: a SQUARE cell (so the sprite is not distorted and the
        /// rendered angles are the cell's own), large enough that the ~0.19-of-half-extent centroid offset is
        /// tens of px — far above rasterization jitter.</summary>
        private static SymbolQuad SignToothCell(SpriteAtlasView view, float halfExtentPx)
        {
            // The REPACKED view, and IconQuadLayout's own UV rect rather than a restatement of it: the sprite
            // is relocated by SpriteSheet's padded repack, and its drawn rect spans its padded cell.
            Assert.IsTrue(view.Index.TryGetSprite(SignToothSprite, out SpriteEntry entry),
                $"precondition: the demo fixture must define the two-dimensionally asymmetric '{SignToothSprite}' sprite");
            SymbolQuad laidOut = IconQuadLayout.Layout(
                entry, view.Size, 1f, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
            return new SymbolQuad
            {
                TopLeft = new float2(-halfExtentPx, halfExtentPx),
                BottomRight = new float2(halfExtentPx, -halfExtentPx),
                UvTopLeft = laidOut.UvTopLeft,
                UvBottomRight = laidOut.UvBottomRight,
                LineIndex = 0,
            };
        }

        /// <summary>Both sign teeth measure about the label's anchor, which this fixture puts at the viewport
        /// centre: the camera looks straight down at <c>lookAt</c>, and the label's single cell sits at the
        /// path's arc midpoint, which IS <c>lookAt</c> (the path is lookAt ± dir·halfLen and the anchor is
        /// segment 0 at t = 0.5). Confirmed rather than assumed — the cell is symmetric about the anchor and
        /// the "F"'s ink box is centred in its cell to within 0.5 px of 32, so on the UN-ROTATED arm the ink
        /// box centre must land on the anchor.</summary>
        private static void AssertAnchorIsTheViewportCentre(SymbolInk baseline)
        {
            float2 boxCentreFromAnchor = InkBoxCentreFromViewportCentrePx(baseline);
            Assert.Less(math.length(boxCentreFromAnchor), 12f,
                $"precondition: the un-rotated icon's ink box must be centred on the label anchor (the " +
                $"viewport centre) — measured {math.length(boxCentreFromAnchor):F1} px away at " +
                $"{boxCentreFromAnchor}. Every offset in this test is taken about that point.");
        }

        /// <summary>Where the ink BOX's centre sits relative to the viewport centre, in the same y-up screen
        /// frame — the difference of the carrier's two offsets, since both are taken from the same centroid.
        /// For an UN-ROTATED symbol this locates the label's anchor on screen (the box is centred on it),
        /// which is what lets a render at an off-centre anchor report where the camera put that anchor.</summary>
        private static float2 InkBoxCentreFromViewportCentrePx(SymbolInk ink)
            => ink.CentroidFromViewportCentrePx - ink.CentroidFromInkBoxPx;

        /// <summary>Asserts <paramref name="rotated"/> is <paramref name="expectedDeg"/> around from
        /// <paramref name="reference"/>, positive being counter-clockwise in the analysed buffer (which the
        /// tangent arm calibrates against a known counter-clockwise turn on the map). The rotation is an
        /// isometry about the anchor, so the length must survive it too. 15° of slack: the failure modes
        /// this separates are 90° apart, while rasterizing a rotated shape moves a ~19 px centroid by well
        /// under a pixel.</summary>
        private static void AssertRotatedBy(float2 reference, float2 rotated, float expectedDeg, string because)
        {
            float measuredDeg = SignedRotationDeg(reference, rotated);
            string seen = $"expected {expectedDeg:F0}° but the ink turned {measuredDeg:F1}° " +
                          $"({reference} px → {rotated} px, both about the anchor).";
            Assert.Less(math.abs(measuredDeg - expectedDeg), 15f, $"{because} — {seen}");
            Assert.That(math.length(rotated), Is.EqualTo(math.length(reference)).Within(30f).Percent,
                $"a rotation about the anchor preserves the offset's LENGTH — {seen}");
        }

        /// <summary>The signed angle from <paramref name="reference"/> to <paramref name="rotated"/> in
        /// (−180, 180] degrees, positive counter-clockwise in the analysed (y-up) buffer.</summary>
        private static float SignedRotationDeg(float2 reference, float2 rotated)
        {
            float deg = math.degrees(math.atan2(rotated.y, rotated.x) - math.atan2(reference.y, reference.x));
            return deg - 360f * math.round(deg / 360f);
        }

        /// <summary>One icon render — along-line or point — measured on the un-mirrored (on-screen)
        /// framebuffer. Both offsets are in a Y-UP screen frame (x right, y up); screen rows grow downward,
        /// so the row term is negated on the way in.
        /// <para>Plain <c>{ get; set; }</c>, not <c>init</c>: MapRenderer.Tests.EditMode has no
        /// IsExternalInit polyfill of its own — the same call this assembly's other test-owned carriers make
        /// (see <c>A6NonMvtDecoderTests.FixtureTileLayer</c>).</para></summary>
        private struct SymbolInk
        {
            /// <summary>Ink bounding-box size in px — the direction-BLIND measure.</summary>
            public int BoxWidth { get; set; }
            public int BoxHeight { get; set; }

            /// <summary>Ink centroid minus the VIEWPORT CENTRE — which is the label's anchor on every arm
            /// that anchors at the look-at, making this the direction-BEARING measure a rotation about the
            /// anchor acts on exactly. Named for the fixed reference rather than for the anchor because the
            /// map-bearing tooth also renders a DELIBERATELY off-centre anchor, where the two differ.</summary>
            public float2 CentroidFromViewportCentrePx { get; set; }

            /// <summary>Ink centroid minus its own bounding-box centre. Reference-free (it needs no camera
            /// assumption), which is what makes it the cross-check that locates the anchor: subtracting it
            /// from <see cref="CentroidFromViewportCentrePx"/> leaves the ink box's own offset from the
            /// viewport centre (<see cref="InkBoxCentreFromViewportCentrePx"/>).</summary>
            public float2 CentroidFromInkBoxPx { get; set; }
        }

        // Renders ONE along-line icon of sprite `iconImage`, carrying icon-rotate `iconRotateDeg`, on a road
        // at `lineAngleDeg` (0 = east/screen-horizontal, 90 = north/screen-vertical at heading 0) through the
        // real LabelPlacementSystem → Map/Symbol/IconWorld, and measures its on-screen ink.
        private static SymbolInk MeasureAlongLineIconInk(SpriteSheet sheet, in SymbolQuad cell,
            string iconImage, float lineAngleDeg, float iconRotateDeg)
        {
            var camGo = new GameObject("AlongLineIconTangent_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                zoom: 12.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14);

            // A road through the look-at at the requested bearing (same construction as
            // WorldCurvedAbRenderSnapshotTests.ShortLineAt — east = +X, north = +Z at zero heading/tilt).
            double altitude = uCam.transform.position.y;
            double rad = math.radians(lineAngleDeg);
            double3 dir = new double3(math.cos(rad), 0.0, math.sin(rad));
            double halfLen = altitude * 0.02;

            var label = new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                Kind = LabelKind.Icon,
                PathRender = new[] { frame.SceneOriginRender - dir * halfLen, frame.SceneOriginRender + dir * halfLen },
                LineAnchors = new[] { new LineAnchor(0, 0.5f) },
                CurvedGlyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = cell } },
                IconImage = iconImage,
                IconRotateRadians = math.radians(iconRotateDeg),
                // White vertex color so SAMPLE(_MainTex) * color shows the sprite's own hue (LabelPaint.Default
                // is black text ink — see the four-icon test above).
                Paint = new LabelPaint
                {
                    TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
                    HaloColor = default, HaloWidthPx = 0f, HaloBlurPx = 0f,
                },
                TextSizePx = TextQuadLayout.OneEm, // scale 1 — the cell's baked px ARE screen px
                MaxAngleDeg = 180f,
                KeepUpright = false,
                AllowOverlap = true, // render diagnostic — never collision-cull
                SortKey = 0f,
                FeatureIndex = 0,
                TileKey = tileKey,
            };

            var system = new LabelPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            // A real (if tiny) glyph atlas is REQUIRED even for an icon-only scene: LabelPlacementSystem
            // gates its whole staging pass on `atlas?.Texture != null`, so a null one stages nothing at all.
            GlyphAtlasTexture atlasTexture = BuildTinyGlyphAtlasTexture();
            var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(new[] { label }), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(new[] { label }), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                Assert.AreEqual(1, system.LastQuadCount,
                    $"DIAGNOSTIC precondition ({lineAngleDeg} deg): the along-line icon must place exactly one quad.");

                snap.Render(uCam);
                if (snap.IsAllBlack())
                    Assert.Inconclusive("render is all-black — no GPU context in this batch session (see SnapshotRenderer.IsAllBlack).");

                byte[] px = (byte[])snap.RawPixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, Size, Size); // readback is mirrored vs on-screen
                WorldSymbolInkAnalysis.AnalyzeInk(px, Size, Size,
                    out int minRow, out int maxRow, out int minCol, out int maxCol,
                    out float centroidRow, out float centroidCol, out int ink);
                Assert.Greater(ink, 200, $"({lineAngleDeg} deg) the icon must render meaningful ink, not a blank frame.");

                // Row/col (top-left origin, rows growing DOWN) → a y-up screen frame about the viewport
                // centre, which is where this fixture's camera puts the label anchor (see the sign tooth's
                // reference-point precondition, which is what proves it rather than assuming it). Pixel
                // centres are index+0.5, so the centre of a Size-wide viewport is index (Size−1)/2.
                float viewportCentre = (Size - 1) * 0.5f;
                float2 inkBoxCentre = new float2((minCol + maxCol) * 0.5f, (minRow + maxRow) * 0.5f);
                var measured = new SymbolInk
                {
                    BoxWidth = maxCol - minCol + 1,
                    BoxHeight = maxRow - minRow + 1,
                    CentroidFromViewportCentrePx = new float2(centroidCol - viewportCentre, viewportCentre - centroidRow),
                    CentroidFromInkBoxPx = new float2(centroidCol - inkBoxCentre.x, inkBoxCentre.y - centroidRow),
                };
                TestContext.Out.WriteLine(
                    $"along-line icon '{iconImage}' @ road {lineAngleDeg} deg, icon-rotate {iconRotateDeg} deg: " +
                    $"ink box {measured.BoxWidth}x{measured.BoxHeight} px ({ink} px), " +
                    $"centroid {measured.CentroidFromViewportCentrePx} px from the viewport centre, " +
                    $"{measured.CentroidFromInkBoxPx} px from the ink box centre");
                return measured;
            }
            finally
            {
                snap.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // Renders ONE POINT icon of sprite `SignToothSprite` in a square cell of `halfExtentPx`, with the
        // given `rotationAlignment`, under a camera at `headingDeg`, anchored `anchorEastFractionOfAltitude`
        // of the camera's altitude due map-EAST of the look-at (0 = at the look-at, i.e. the viewport
        // centre) — through the real LabelPlacementSystem → Map/Symbol/IconWorld — and measures its
        // on-screen ink. The along-line sibling above shares everything but the label: this one carries a
        // Layout + AnchorRender (the point path) instead of a PathRender + CurvedGlyphs, and a heading
        // instead of a road bearing.
        private static SymbolInk MeasurePointIconInk(SpriteSheet sheet, float halfExtentPx,
            float headingDeg, AlignmentMode rotationAlignment, double anchorEastFractionOfAltitude)
        {
            var camGo = new GameObject("MapAlignedPointIcon_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                zoom: 12.0, heading: headingDeg, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14);

            // East = +X in render space (the same convention the along-line road above is built on). The
            // camera orbits the look-at, so at tilt 0 its height above it is transform.position.y whatever
            // the heading.
            double altitude = uCam.transform.position.y;
            SymbolQuad cell = SignToothCell(sheet.View, halfExtentPx);

            var label = new LabelInstance
            {
                AnchorRender = frame.SceneOriginRender + new double3(altitude * anchorEastFractionOfAltitude, 0.0, 0.0),
                // The cell's footprint is a deliberately fixed square, not the sprite's own rect, so it
                // carries no skirt to remove.
                Layout = IconQuadLayout.ToLayoutResult(cell, skirtPx: 0f),
                Kind = LabelKind.Icon,
                IconImage = SignToothSprite,
                RotationAlignment = rotationAlignment,
                // White vertex color so SAMPLE(_MainTex) * color shows the sprite's own hue (LabelPaint.Default
                // is black text ink — see the four-icon test above).
                Paint = new LabelPaint
                {
                    TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
                    HaloColor = default, HaloWidthPx = 0f, HaloBlurPx = 0f,
                },
                TextSizePx = TextQuadLayout.OneEm, // scale 1 — the cell's baked px ARE screen px
                AllowOverlap = true, // render diagnostic — never collision-cull
                SortKey = 0f,
                FeatureIndex = 0,
                TileKey = tileKey,
            };

            var system = new LabelPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            // A real (if tiny) glyph atlas is REQUIRED even for an icon-only scene — see the along-line
            // sibling's identical note.
            GlyphAtlasTexture atlasTexture = BuildTinyGlyphAtlasTexture();
            var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, plan.Build(new[] { label }), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(new[] { label }), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                Assert.AreEqual(1, system.LastQuadCount,
                    $"DIAGNOSTIC precondition (heading {headingDeg} deg, {rotationAlignment}): the point icon " +
                    $"must place exactly one quad.");

                snap.Render(uCam);
                if (snap.IsAllBlack())
                    Assert.Inconclusive("render is all-black — no GPU context in this batch session (see SnapshotRenderer.IsAllBlack).");

                byte[] px = (byte[])snap.RawPixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, Size, Size); // readback is mirrored vs on-screen
                WorldSymbolInkAnalysis.AnalyzeInk(px, Size, Size,
                    out int minRow, out int maxRow, out int minCol, out int maxCol,
                    out float centroidRow, out float centroidCol, out int ink);
                Assert.Greater(ink, 200,
                    $"(heading {headingDeg} deg) the icon must render meaningful ink, not a blank frame.");

                float viewportCentre = (Size - 1) * 0.5f;
                float2 inkBoxCentre = new float2((minCol + maxCol) * 0.5f, (minRow + maxRow) * 0.5f);
                var measured = new SymbolInk
                {
                    BoxWidth = maxCol - minCol + 1,
                    BoxHeight = maxRow - minRow + 1,
                    CentroidFromViewportCentrePx = new float2(centroidCol - viewportCentre, viewportCentre - centroidRow),
                    CentroidFromInkBoxPx = new float2(centroidCol - inkBoxCentre.x, inkBoxCentre.y - centroidRow),
                };
                TestContext.Out.WriteLine(
                    $"point icon '{SignToothSprite}' @ heading {headingDeg} deg, {rotationAlignment}, anchor " +
                    $"{anchorEastFractionOfAltitude:F2}·altitude east: ink box {measured.BoxWidth}x{measured.BoxHeight} px " +
                    $"({ink} px), centroid {measured.CentroidFromViewportCentrePx} px from the viewport centre, " +
                    $"ink box centre {InkBoxCentreFromViewportCentrePx(measured)} px from it");
                return measured;
            }
            finally
            {
                snap.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // The minimum a Tick needs to stage anything at all (see the gate note above) — one real glyph
        // uploaded to a GlyphAtlasTexture. Nothing in the A6 scene ever samples it.
        private static GlyphAtlasTexture BuildTinyGlyphAtlasTexture()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadGlyphFixture("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u]);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        /// <summary>Vertically mirrors an RGBA32 row-major buffer in place (row r ↔ row height-1-r).</summary>
        private static void FlipRowsVertically(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            var tmp = new byte[stride];
            for (int r = 0; r < height / 2; r++)
            {
                int top = r * stride;
                int bot = (height - 1 - r) * stride;
                System.Array.Copy(rgba, top, tmp, 0, stride);
                System.Array.Copy(rgba, bot, rgba, top, stride);
                System.Array.Copy(tmp, 0, rgba, bot, stride);
            }
        }

        /// <summary>Tally pixels whose color is dominantly red / green / blue / orange (each demo sprite's hue),
        /// on the white background. A hue counts when its channel(s) clearly dominate and it is not near-white.</summary>
        private static void CountHues(byte[] rgba, int width, int height,
            out int red, out int green, out int blue, out int orange)
        {
            red = green = blue = orange = 0;
            for (int i = 0; i < width * height; i++)
            {
                int b = i * 4;
                int r = rgba[b], g = rgba[b + 1], bl = rgba[b + 2];
                if (r > 230 && g > 230 && bl > 230) continue; // white background
                if (r > 150 && g < 120 && bl < 120) red++;
                else if (g > 140 && r < 130 && bl < 130) green++;
                else if (bl > 150 && r < 130 && g < 150) blue++;
                else if (r > 180 && g > 110 && g < 200 && bl < 100) orange++;
            }
        }

        /// <summary>Average horizontal extent of the RED (tri-up) sprite's ink in the top third vs the bottom
        /// third of its row span. Red is unique to the up-triangle, so no masking of other sprites is needed.</summary>
        private static void RedTriangleWidths(byte[] rgba, int width, int height, out float topWidth, out float bottomWidth)
        {
            var rowMin = new int[height];
            var rowMax = new int[height];
            var rowHas = new bool[height];
            for (int r = 0; r < height; r++) { rowMin[r] = int.MaxValue; rowMax[r] = int.MinValue; }

            int minRow = int.MaxValue, maxRow = int.MinValue;
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int b = (row * width + col) * 4;
                    if (rgba[b] > 150 && rgba[b + 1] < 120 && rgba[b + 2] < 120) // red (tri-up)
                    {
                        rowHas[row] = true;
                        if (col < rowMin[row]) rowMin[row] = col;
                        if (col > rowMax[row]) rowMax[row] = col;
                        if (row < minRow) minRow = row;
                        if (row > maxRow) maxRow = row;
                    }
                }
            }

            if (maxRow < minRow) { topWidth = 0f; bottomWidth = 0f; return; }
            int span = maxRow - minRow + 1;
            int third = math.max(1, span / 3);
            topWidth = AvgWidth(rowMin, rowMax, rowHas, minRow, minRow + third);
            bottomWidth = AvgWidth(rowMin, rowMax, rowHas, maxRow - third, maxRow);
        }

        private static float AvgWidth(int[] rowMin, int[] rowMax, bool[] rowHas, int startRow, int endRow)
        {
            float sum = 0f; int count = 0;
            for (int r = startRow; r <= endRow; r++)
            {
                if (!rowHas[r]) continue;
                sum += rowMax[r] - rowMin[r] + 1; count++;
            }
            return count == 0 ? 0f : sum / count;
        }
    }
}
