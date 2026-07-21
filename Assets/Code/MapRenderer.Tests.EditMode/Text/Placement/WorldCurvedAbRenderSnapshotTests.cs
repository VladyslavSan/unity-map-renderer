// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
// NOT registered in core-tests.csproj. Modeled on WorldSymbolAbRenderSnapshotTests (T2's point A/B) but for
// CURVED (along-line) text — Stage AC's T-AB tooth (curved-world-plan.md §4 T-AB).
//
// CONVERTED (old-screen-path deletion, §6a): this tooth used to render the SAME asymmetric glyph through
// BOTH the OLD screen curved path (a hand-built BillboardMath.BuildQuad mesh through Map/Symbol/Text) and the
// NEW world curved path (a real LabelPlacementSystem.Tick through Map/Symbol/TextWorld), asserting the two
// were pixel-equivalent. The OLD arm's oracle (BuildQuad/BillboardVertex) is retired with the dead render
// path it built for — this tooth now renders ONLY the NEW world path and asserts its ink signature (centroid
// + bounding box, a real shape/orientation fingerprint, not just a coarse centroid) against committed golden
// values.
//
// GOLDEN PROVENANCE (this deletion stage): the goldens were minted from the NEW world render captured during
// this stage's gate, which was VERIFIED render-neutral vs the known-good baseline commit f3e8d81c — the
// deletion removed only BuildQuad (BuildWorldQuad's body and every world-read PlacedQuad field are byte-
// identical; the world emit call is unchanged). So this is a first baseline captured from an unchanged render,
// not a rebake over a drift. The render is deterministic (the 45° and NonzeroAnchorLocal cases mint bit-
// identical signatures). This still catches a FUTURE regression (a later change that shifts/mirrors the glyph
// fails the committed shape), but — unlike the retired A/B — it cannot by itself prove today's rotation SIGN
// is correct (a golden from a wrong-signed render would enshrine the wrong sign). The sign is backed instead
// by: (1) the orchestrator's independent D-H RED-verify (injecting a negated sdir.y made this A/B fail 4/5),
// and (2) a rendered on-screen diagonal-label eyeball — a COMMIT PRECONDITION on this stage (design §14
// "SEQUENCING GATE"), which the maintainer confirmed before the conversion landed.
//
// 'F' (a fully asymmetric glyph — no mirror symmetry in x or y) is used so a wrong rotation sense renders
// visibly different ink (not an aliased 'F'-looking mirror) — a real discriminator, not a tautology against
// its own mint: a future sign regression moves the bounding box/centroid far outside the tight per-run
// tolerance below (headless GPU readback is deterministic run-to-run on the same hardware/driver — the
// tolerance only absorbs legitimate AA-level jitter). Both a 45° diagonal AND a vertical line are swept (a
// diagonal alone leaves a residual sign ambiguity only the vertical resolves — see the design's D-H note).

using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
// UnityEngine.Rendering ALSO declares a CameraProperties type (mesh descriptor namespace collision) —
// disambiguate explicitly rather than qualifying every call site.
using CameraProperties = MapRenderer.Core.View.Camera.CameraProperties;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class WorldCurvedAbRenderSnapshotTests
    {
        private const int Size = 512;
        private const float TextSizePx = 160f;

        // Per-run pixel tolerance. The CENTROID is the sign discriminator (a wrong rotation sign moves it by
        // TENS of pixels — far outside this margin) and is robust (an average over all ink), so it stays
        // tight. The bounding-box EDGES are inherently more AA-fragile (a single stray edge pixel moves an
        // edge), so they get a looser margin — they add shape confirmation, not the sign check.
        private const float GoldenCentroidTolerancePx = 4f;
        private const float GoldenBboxTolerancePx = 8f;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        // 'F' (70) — no mirror symmetry in x or y (unlike 'A'), so a wrong rotation sense cannot alias back
        // to a correct-looking render.
        private static (GlyphAtlasTexture texture, SymbolQuad quad) BuildGlyphF()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[70u]);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "F", Metrics = new AtlasMetrics(atlas) });
            TextLayoutResult layout = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default);
            Assert.AreEqual(1, layout.Quads.Count, "DIAGNOSTIC precondition: a single glyph must lay out to exactly one quad.");
            return (texture, layout.Quads[0]);
        }

        // Mercator, 3 orientations: 0° (horizontal — not decisive alone, kept for a broad sweep), 45°
        // (diagonal), 90° (vertical — resolves the diagonal's residual sign ambiguity, see this file's header).
        [Test]
        public void NewWorldPath_RendersUprightCurvedGlyph_MatchesGolden(
            [Values(0f, 45f, 90f)] float lineAngleDeg)
        {
            var (atlasTexture, quad) = BuildGlyphF();
            try
            {
                var camGo = new GameObject("WorldCurvedAb_TestCamera");
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(Size, Size, 0);
                uCam.clearFlags = CameraClearFlags.SolidColor;
                uCam.backgroundColor = Color.white;
                var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 12.0, heading: 0.0, tilt: 0.0));
                var frame = new SceneFrame(mapCamera.Projection.Project(lookAt), float3x3.identity);
                long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14); // Risk R1: a realistic tile, not TileKey=0

                (double3 pathA, double3 pathB) = ShortLineAt(uCam, frame, lineAngleDeg);

                try
                {
                    byte[] newPixels = RenderNewWorldPath(uCam, mapCamera, in frame, pathA, pathB, quad, atlasTexture, tileKey);

                    // Golden values minted from this render at conversion time (commit f3e8d81c's real GPU
                    // output, byte-identical NEW-path behaviour before/after the old-path deletion).
                    if (lineAngleDeg == 0f)
                        AssertGolden(newPixels, "0°", centroidRow: 256.1f, centroidCol: 250.9f, minRow: 210, maxRow: 323, minCol: 231, maxCol: 294);
                    else if (lineAngleDeg == 45f)
                        AssertGolden(newPixels, "45°", centroidRow: 255.6f, centroidCol: 259.0f, minRow: 195, maxRow: 316, minCol: 215, maxCol: 300);
                    else
                        AssertGolden(newPixels, "90°", centroidRow: 251.1f, centroidCol: 265.0f, minRow: 208, maxRow: 271, minCol: 219, maxCol: 332);
                }
                finally
                {
                    Object.DestroyImmediate(camGo);
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // Tile-corner placement: TileKey names a NEIGHBOR of the containing tile (mirrors
        // WorldPointEmitRenderTests' NEW-F1 nonzero-AnchorLocal pattern) so the per-glyph Level-1 RTC bake is
        // genuinely large (comparable to a whole tile span), not the small in-tile offset the main sweep
        // above already carries incidentally.
        [Test]
        public void NewWorldPath_RendersUprightCurvedGlyph_MatchesGolden_NonzeroAnchorLocal()
        {
            var (atlasTexture, quad) = BuildGlyphF();
            try
            {
                var camGo = new GameObject("WorldCurvedAbTileCorner_TestCamera");
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(Size, Size, 0);
                uCam.clearFlags = CameraClearFlags.SolidColor;
                uCam.backgroundColor = Color.white;
                var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 12.0, heading: 0.0, tilt: 0.0));
                var frame = new SceneFrame(mapCamera.Projection.Project(lookAt), float3x3.identity);

                TileId containing = TestTileKeys.Containing(lookAt, zoom: 14);
                TileId neighbor = new TileId { X = containing.X + 1, Y = containing.Y, Z = containing.Z };
                long tileKey = SymbolFeatureExtractor.PackTileKey(neighbor);

                (double3 pathA, double3 pathB) = ShortLineAt(uCam, frame, 45f);

                try
                {
                    byte[] newPixels = RenderNewWorldPath(uCam, mapCamera, in frame, pathA, pathB, quad, atlasTexture, tileKey);

                    AssertGolden(newPixels, "NonzeroAnchorLocal", centroidRow: 255.6f, centroidCol: 259.0f, minRow: 195, maxRow: 316, minCol: 215, maxCol: 300);
                }
                finally
                {
                    Object.DestroyImmediate(camGo);
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // A globe rebase (nonzero frame.Rebase, SphericalProjection) — the plan's sweep explicitly requires
        // "Mercator + a globe rebase", not Mercator alone (the RTC bake's tile-origin cancellation, and the
        // shader's Jacobian projection, must both still hold once the object-to-world transform carries a
        // real rotation, not just a translation).
        [Test]
        public void NewWorldPath_RendersUprightCurvedGlyph_MatchesGolden_GlobeRebase()
        {
            var (atlasTexture, quad) = BuildGlyphF();
            try
            {
                var camGo = new GameObject("WorldCurvedAbGlobe_TestCamera");
                var uCam = camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(Size, Size, 0);
                uCam.clearFlags = CameraClearFlags.SolidColor;
                uCam.backgroundColor = Color.white;
                var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
                var projection = new SphericalProjection();
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 }, zoom: 12.0, heading: 0.0, tilt: 0.0),
                    projection: projection);
                float3x3 rebase = math.transpose(projection.TangentBasisAt(lookAt));
                var frame = new SceneFrame(projection.Project(lookAt), rebase);
                long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14);

                (double3 pathA, double3 pathB) = ShortLineAt(uCam, frame, 45f);

                try
                {
                    byte[] newPixels = RenderNewWorldPath(uCam, mapCamera, in frame, pathA, pathB, quad, atlasTexture, tileKey);

                    AssertGolden(newPixels, "GlobeRebase", centroidRow: 258.6f, centroidCol: 252.9f, minRow: 221, maxRow: 302, minCol: 186, maxCol: 312);
                }
                finally
                {
                    Object.DestroyImmediate(camGo);
                }
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        // A short line straddling the frame's look-at, oriented at `lineAngleDeg` in the render-space XZ
        // plane (east=X, north=Z — the flat local approximation is valid at zero tilt/heading). Half-length
        // mirrors the SAME altitude-relative magnitude WorldSymbolAbRenderSnapshotTests/WorldLabelMotionTests
        // already validate keeps a point comfortably on-screen (altitude*0.02 total span here).
        private static (double3, double3) ShortLineAt(Camera uCam, in SceneFrame frame, float lineAngleDeg)
        {
            double altitude = uCam.transform.position.y;
            double3 centre = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);
            double rad = math.radians(lineAngleDeg);
            double3 dir3 = new double3(math.cos(rad), 0.0, math.sin(rad));
            double halfLen = altitude * 0.01;
            return (centre - dir3 * halfLen, centre + dir3 * halfLen);
        }

        // Asserts the render's ink signature — centroid AND bounding box (a real shape/orientation
        // fingerprint, not just a coarse centroid) — against the committed golden. A wrong rotation
        // sign/sense rotates the asymmetric 'F' glyph towards a different angle, moving the bounding box far
        // outside GoldenTolerancePx (tens of pixels), not a small AA-level difference.
        // MINT MODE — set true to print the actual ink signature (for capturing a fresh golden) instead of
        // asserting against the committed one below. MUST be false when committed (asserting mode).
        private const bool MintGolden = false;

        private static void AssertGolden(byte[] pixels, string label,
            float centroidRow, float centroidCol, int minRow, int maxRow, int minCol, int maxCol)
        {
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, Size, Size);
            WorldSymbolInkAnalysis.AnalyzeInk(pixels, Size, Size,
                out int actualMinRow, out int actualMaxRow, out int actualMinCol, out int actualMaxCol,
                out float actualCentroidRow, out float actualCentroidCol, out int ink);

            if (MintGolden)
            {
                Assert.Fail($"MINT {label}: centroidRow={actualCentroidRow:F1}f, centroidCol={actualCentroidCol:F1}f, " +
                    $"minRow={actualMinRow}, maxRow={actualMaxRow}, minCol={actualMinCol}, maxCol={actualMaxCol}, ink={ink}");
            }

            Assert.Greater(ink, 30, $"NEW path must render meaningful ink ({label}, not blank/GPU-context-failed).");

            Assert.That(actualCentroidRow, Is.EqualTo(centroidRow).Within(GoldenCentroidTolerancePx), $"ink centroid row must match the committed golden ({label}).");
            Assert.That(actualCentroidCol, Is.EqualTo(centroidCol).Within(GoldenCentroidTolerancePx), $"ink centroid col must match the committed golden ({label}).");
            Assert.That(actualMinRow, Is.EqualTo(minRow).Within(GoldenBboxTolerancePx), $"ink bbox top edge must match the committed golden ({label}).");
            Assert.That(actualMaxRow, Is.EqualTo(maxRow).Within(GoldenBboxTolerancePx), $"ink bbox bottom edge must match the committed golden ({label}).");
            Assert.That(actualMinCol, Is.EqualTo(minCol).Within(GoldenBboxTolerancePx), $"ink bbox left edge must match the committed golden ({label}).");
            Assert.That(actualMaxCol, Is.EqualTo(maxCol).Within(GoldenBboxTolerancePx), $"ink bbox right edge must match the committed golden ({label}).");
        }

        // ── a REAL curved LabelInstance through a REAL LabelPlacementSystem.Tick (curved routes to the world
        //    sink since Stage AC) → Map/Symbol/TextWorld. ───────────────────────────────────────────────────
        private static byte[] RenderNewWorldPath(Camera uCam, MapCamera mapCamera, in SceneFrame frame,
            double3 pathA, double3 pathB, in SymbolQuad quad, GlyphAtlasTexture atlasTexture, long tileKey)
        {
            var label = new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                PathRender = new[] { pathA, pathB },
                LineAnchors = new[] { new LineAnchor(0, 0.5f) },
                CurvedGlyphs = new System.Collections.Generic.List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = quad } },
                Paint = LabelPaint.Default,
                TextSizePx = TextSizePx,
                MaxAngleDeg = 180f,
                KeepUpright = false,
                FeatureIndex = 0,
                TileKey = tileKey,
            };

            var system = new LabelPlacementSystem(mapCamera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                system.Tick(in frame, new[] { label }, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the NEW world path must place the label.");

                using var snap = new SnapshotRenderer(Size, Size);
                snap.Render(uCam);
                return (byte[])snap.RawPixels.Clone();
            }
            finally
            {
                system.Dispose();
            }
        }
    }
}
