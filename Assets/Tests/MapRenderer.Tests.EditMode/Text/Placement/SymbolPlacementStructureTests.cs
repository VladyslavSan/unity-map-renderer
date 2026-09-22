// Text/Placement/SymbolPlacementStructureTests.cs — the per-frame symbol placement structure, the Burst StageJob differential, and the world-billboard RTC (render-then-composite) algebra seam.
//
// No single production area dominates this small file; kept in the order the topic-and-lane pack assembled them.
//
// Contents:
//   SymbolPlacementStructureTests  — S20 T5: symbols are the per-frame path, never the static tile path.
//   SymbolStageJobTests            — Lever C step 3b: the Burst StageJob must make the same placement decisions as the managed SymbolStagingMath reference it wraps — the differential the codebase requires for a Burst numeric port (cf.
//   WorldBillboardRtcAlgebraTests  — A0 T1: pins the "two-term-RTC-vs-single-narrow" seam — the world-anchored path composes a screen position via TWO float32-narrowed terms (mesh-baked AnchorLocal = anchorRender-tileOriginRender, plus the per-frame tile transform…

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Jobs.Symbols;
using MapRenderer.Unity.View;


namespace MapRenderer.Tests.Text.Placement
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolPlacementStructureTests — symbols are the per-frame path, never the static tile path
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S20 T5: symbols are the per-frame path, never the static tile path.
    /// (a) STRUCTURAL — a grep guard: nothing under the symbol-placement source tree calls
    ///     <c>ITileRenderBackend.AddTileLayer</c>.
    /// (b) BEHAVIORAL — the billboard buffer is rebuilt from scratch every <see cref="SymbolPlacementSystem.Tick"/>,
    ///     not cached/accumulated across calls.
    /// </summary>
    [TestFixture]
    public class SymbolPlacementStructureTests : BaseTestFixture
    {
        // ── (a) Structural: grep guard ───────────────────────────────────────────────────────────

        /// <summary>The ONLY legitimate <c>AddTileLayer(</c> call sites in the assembly: the backend
        /// interface's declaration, its three backend implementations, and the static per-tile pipeline's
        /// one caller. Scanning ONLY <c>Text/Placement</c> (the prior scope) misses a violation routed
        /// through an indirection defined elsewhere in the assembly — a wrapper method the symbol-placement
        /// code calls that itself calls <c>AddTileLayer(</c> never contains the literal string inside
        /// <c>Text/Placement</c> at all. Scanning the whole assembly against this allowlist catches that
        /// wrapper wherever it lands, while the five known-legitimate files stay silent.</summary>
        private static readonly string[] AllowedAddTileLayerCallers =
        {
            Path.Combine("Rendering", "Backend", "ITileRenderBackend.cs"),
            Path.Combine("Rendering", "Backend", "BRG", "TileRenderer.cs"),
            Path.Combine("Rendering", "Backend", "GameObjects", "TileRenderer.cs"),
            Path.Combine("Rendering", "Backend", "Entities", "TileRenderer.cs"),
            Path.Combine("Rendering", "Tile", "TileManager.cs"),
        };

        [Test]
        public void SourceTree_NeverReferencesAddTileLayer()
        {
            string dir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            DirectoryAssert.Exists(dir);

            // Match the CALL form "AddTileLayer(" (no space before the paren) so a doc comment discussing
            // the constraint in prose ("must never call AddTileLayer (the static ... path)") is not itself
            // flagged as an offender -- only an actual invocation is.
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (!text.Contains("AddTileLayer(")) continue;

                bool allowed = false;
                foreach (string allowedSuffix in AllowedAddTileLayerCallers)
                    if (file.EndsWith(allowedSuffix, System.StringComparison.Ordinal)) { allowed = true; break; }

                if (!allowed) offenders.Add(file);
            }

            Assert.IsEmpty(offenders,
                "AddTileLayer (the static per-(tile,layer) mesh path) must be called ONLY by the backend " +
                "implementations and the static pipeline's own TileManager caller -- the per-frame label " +
                "placement path (T5), and every other file in the assembly, must reach it never, not even " +
                "through an indirection. Offending files:\n" + string.Join("\n", offenders));
        }

        // ── E2: Graphics.RenderMesh is retired — symbols draw via persistent per-slot MeshRenderers now
        //    (SymbolSlotPresenter), never an immediate-mode per-frame submission (design §5 option (c)). ──
        [Test]
        public void SourceTree_NeverReferencesGraphicsRenderMesh()
        {
            string dir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            DirectoryAssert.Exists(dir);

            // Match the CALL form "Graphics.RenderMesh(" (no space before the paren) so prose discussing the
            // retired mechanism (doc comments, shader README) is not itself flagged -- only an actual call is.
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (text.Contains("Graphics.RenderMesh("))
                {
                    offenders.Add(file);
                }
            }

            Assert.IsEmpty(offenders,
                "Graphics.RenderMesh was retired in E2 (the render-layer model) " +
                "-- symbol labels draw via persistent per-slot MeshRenderers (SymbolSlotPresenter), redrawn by " +
                "Unity on its own, never re-issued from an immediate-mode call. Offending files:\n" +
                string.Join("\n", offenders));
        }

        // ── (b) Behavioral: rebuilt every Tick, never cached/accumulated ─────────────────────────

        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph
            {
                Codepoint = 65,
                Width = 10,
                Height = 10,
                Left = 0,
                Top = 8,
                Advance = 12,
                Bitmap = new byte[16 * 16],
            };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph, 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // AnchorRender is render-space PRE-RTC (the same space projection.Project(geo) emits) -- NOT a
        // small local offset. It must be built relative to the frame's SceneOriginRender (a large absolute
        // Mercator coordinate), or TryProjectAnchor's rebase lands it far outside the viewport and every
        // symbol is silently culled (the bug this comment fixes: LastQuadCount was 0, not 2/1).
        private static void AddSymbol(SymbolTileBuffer buffer, int featureIndex, double3 sceneOriginRender,
            float2 translatePx = default, AlignmentMode rotationAlignment = AlignmentMode.Auto)
        {
            var quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f),
                    BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f),
                    UvBottomRight = new float2(0.4f, 0.4f),
                    LineIndex = 0,
                },
            };
            TestSymbolTileBuffer.AddPoint(buffer,
                sceneOriginRender + new double3(featureIndex * 5.0, 0.0, featureIndex * 2.0),
                quads, float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default, textSizePx: 24f, sortKey: 0f, featureIndex: featureIndex, tileKey: 0L,
                // AllowOverlap: this test asserts the buffer is REBUILT-not-accumulated every Tick (2/1/2
                // quads), which is orthogonal to Slice-2 collision. Without it, whether the two nearby
                // anchors' boxes overlap (and one gets culled) is projection-dependent and would make the
                // quad-count assertion flaky. Collision itself is covered by SymbolCollisionTests (T1).
                allowOverlap: true,
                translatePx: translatePx, rotationAlignment: rotationAlignment);
        }

        [Test]
        public void Tick_AdvancesTickCount_AndRebuildsQuadCountFromScratchEveryCall()
        {
            var camGo = Track(new GameObject("SymbolStructure_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };

            using var atlasTexture = BuildTinyAtlasTexture();
            var twoSymbols = new SymbolTileBuffer();
            AddSymbol(twoSymbols, 0, frame.SceneOriginRender);
            AddSymbol(twoSymbols, 1, frame.SceneOriginRender);
            var oneSymbol = new SymbolTileBuffer();
            AddSymbol(oneSymbol, 0, frame.SceneOriginRender);

            using var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            {
                Assert.AreEqual(0, system.TickCount, "TickCount starts at 0 before any Tick.");

                // R3 (deferred collision, design §10.3): the collision verdict a Tick's emit reads is the one
                // HARVESTED at that Tick's top — i.e. the collision scheduled at the end of the PREVIOUS Tick,
                // over the PREVIOUS Tick's candidates (§2.6). A candidate-set change (twoSymbols → oneSymbol →
                // twoSymbols) therefore needs TWO Ticks before its placement can be asserted, so each step below
                // duplicates its Tick call (same args) — TickCount advances TWICE per assertion step (2, 4, 6),
                // NOT because of a fudged expectation, but because it counts Ticks and this fixture now ticks
                // twice per step. The quad-count expectations (2 / 1 / 2) — the actual subject of this test — do
                // NOT move.
                system.TickSymbols(in frame, twoSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, twoSymbols, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(2, system.TickCount);
                Assert.AreEqual(2, system.LastQuadCount, "2 labels x 1 quad each = 2 placed quads.");

                // Rebuilt from scratch, not accumulated: ticking with FEWER symbols must report FEWER quads,
                // not the sum of every Tick so far (which would prove a cache/append bug).
                system.TickSymbols(in frame, oneSymbol, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, oneSymbol, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(4, system.TickCount);
                Assert.AreEqual(1, system.LastQuadCount,
                    "a Tick with 1 label must report 1 placed quad -- NOT 3 (2 from the prior Tick + 1), " +
                    "which would mean the buffer is cached/appended instead of rebuilt every Tick.");

                system.TickSymbols(in frame, twoSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, twoSymbols, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(6, system.TickCount);
                Assert.AreEqual(2, system.LastQuadCount);
            }
        }

        // ── Slice C: text-translate shifts the placed billboard vertices in screen space (guards that
        //    SymbolPlacementSystem.Tick actually applies SymbolTranslate — the fast SymbolTranslateTests only
        //    cover the pure delta math; this proves Tick calls it). ──
        [Test]
        public void Tick_TextTranslate_ShiftsEveryPlacedVertex_ByScreenDelta()
        {
            var camGo = Track(new GameObject("SymbolTranslate_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            // MapLibre text-translate [7,3] = right 7, DOWN 3 → screen (y-up) delta (+7, -3), depth unchanged.
            var translate = new float2(7f, 3f);
            var baseline = new SymbolTileBuffer();
            AddSymbol(baseline, 0, frame.SceneOriginRender);
            var moved = new SymbolTileBuffer();
            AddSymbol(moved, 0, frame.SceneOriginRender, translate);

            // Epic A / A1: this symbol is a POINT (default Placement) — it now draws through the world path.
            // The screen-space translate no longer shows up as a delta on system.Mesh's Position (screen px);
            // D4 carries it as an ADDITIVE, UNROTATED Offset delta instead (BillboardMath.BuildWorldQuad),
            // with the SAME A0-F2 Y-negation as the glyph corner — so the sign convention differs from the
            // OLD path's raw screen-vertex delta (see BuildWorldQuad's doc for the derivation).
            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            {
                // R3: the collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick (§2.6) —
                // duplicate each candidate-set's Tick call before reading its placement.
                system.TickSymbols(in frame, baseline, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, baseline, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(1, system.LastQuadCount, "one label, one quad");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh0), "the world slot mesh must exist.");
                WorldMeshReadback.Read(mesh0, out WorldBillboardVertex[] v0, out _);

                system.TickSymbols(in frame, moved, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, moved, atlasTexture, mapCamera.Projection);
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mesh1), "the world slot mesh must exist.");
                WorldMeshReadback.Read(mesh1, out WorldBillboardVertex[] v1, out _);

                Assert.AreEqual(v0.Length, v1.Length, "same vertex count (only the translate changed)");
                Assert.Greater(v0.Length, 0, "the label placed at least one quad");
                for (int i = 0; i < v0.Length; i++)
                {
                    Assert.AreEqual(translate.x, v1[i].Offset.x - v0[i].Offset.x, 1e-3f, $"vertex {i}: +tx in Offset.x");
                    Assert.AreEqual(translate.y, v1[i].Offset.y - v0[i].Offset.y, 1e-3f, $"vertex {i}: +ty in Offset.y (D4's SAME Y-negation as the corner, applied to both — the two negations cancel back to +ty)");
                    Assert.AreEqual(v0[i].AnchorLocal, v1[i].AnchorLocal, "the anchor itself is untouched by a screen-space translate — only Offset moves");
                }
            }
        }

        // ── #4: text-rotation-alignment:map rotates the billboard with the map bearing (guards that Tick
        //    threads symbol.RotationAlignment + the bearing into BillboardMath — the axis-aligned viewport
        //    billboard becomes a rotated quad under a non-zero heading). ──
        [Test]
        public void Tick_RotationAlignmentMap_RotatesBillboard_UnderBearing_ViewportStaysAxisAligned()
        {
            var camGo = Track(new GameObject("SymbolRotation_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            // A non-zero heading (45°) — at bearing 0 map and viewport coincide, so the rotation is only
            // observable with an active bearing.
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 45.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            var viewportSymbols = new SymbolTileBuffer();
            AddSymbol(viewportSymbols, 0, frame.SceneOriginRender, default, AlignmentMode.Viewport);
            var mapSymbols = new SymbolTileBuffer();
            AddSymbol(mapSymbols, 0, frame.SceneOriginRender, default, AlignmentMode.Map);

            // Epic A / A1: this symbol is a POINT — it now draws through the world path. The rotation is baked
            // into Offset (D3 — every frame, byte-equivalent to the old path while A1's emit runs every
            // Tick) rather than a raw screen-vertex position, so this reads Offset off the world mesh
            // instead of system.Mesh.vertices. Winding is IDENTICAL to the old path (BuildWorldQuad mirrors
            // BuildQuad's TL/TR/BR/BL corner order).
            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                system.TickSymbols(in frame, viewportSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, viewportSymbols, atlasTexture, mapCamera.Projection);
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh viewportMesh), "the world slot mesh must exist.");
                WorldMeshReadback.Read(viewportMesh, out WorldBillboardVertex[] vp, out _); // v[0]=TL, v[1]=TR, v[2]=BR, v[3]=BL
                Assert.AreEqual(4, vp.Length, "one quad → 4 verts");

                system.TickSymbols(in frame, mapSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, mapSymbols, atlasTexture, mapCamera.Projection);
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh mapMesh), "the world slot mesh must exist.");
                WorldMeshReadback.Read(mapMesh, out WorldBillboardVertex[] mp, out _);

                // Viewport: top edge horizontal (axis-aligned billboard) — TL.Offset.y == TR.Offset.y.
                Assert.AreEqual(vp[0].Offset.y, vp[1].Offset.y, 1e-3f, "viewport billboard's top edge stays horizontal");
                // Map under a 45° bearing: the quad is rotated, so the top edge is NOT horizontal.
                Assert.That(math.abs(mp[0].Offset.y - mp[1].Offset.y), Is.GreaterThan(1f),
                    "rotation-alignment:map must rotate the billboard under a non-zero bearing (top edge no longer horizontal)");
                // And it genuinely differs from the viewport placement (rotation actually applied).
                Assert.That(math.abs(mp[1].Offset.x - vp[1].Offset.x) + math.abs(mp[1].Offset.y - vp[1].Offset.y), Is.GreaterThan(1f),
                    "map- and viewport-aligned billboards must differ under a non-zero bearing");
            }
        }

        // ── #5 B2: a curved (symbol-placement:line-center) symbol emits one quad per glyph, distributed along
        //    the projected line (guards that Tick projects the path, walks it, and places per-glyph quads —
        //    line symbols have a null Layout so the point path emits nothing for them). ──
        private static CurvedGlyph MakeCurvedGlyph(float arcCenter) => new CurvedGlyph
        {
            ArcCenter = arcCenter,
            Cell = new SymbolQuad
            {
                TopLeft = new float2(-5f, 8f),
                BottomRight = new float2(5f, -2f),
                UvTopLeft = new float2(0.1f, 0.1f),
                UvBottomRight = new float2(0.4f, 0.4f),
                LineIndex = 0,
            },
        };

        // A-2: a curved symbol carries pre-computed LineAnchors (stable (segment,t) topology). These helpers
        // build them from the RENDER-space test path by arc length — the same topology the extractor derives in
        // tile space (for these straight/L test lines the segment structure is identical). AnchorAt gives the
        // anchor at a fraction of total arc length; BandAnchors gives `n` anchors evenly within [lo,hi].
        private static LineAnchor AnchorAt(double3[] path, double frac)
        {
            int n = path.Length;
            var cum = new double[n];
            for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + math.length(path[i] - path[i - 1]);
            double total = cum[n - 1];
            double arc = math.clamp(frac, 0.0, 1.0) * total;
            if (arc <= 0.0) return new LineAnchor(0, 0f);
            if (arc >= total) return new LineAnchor(n - 2, 1f);
            int seg = 0;
            for (int i = 1; i < n; i++) if (cum[i] >= arc) { seg = i - 1; break; }
            double segLen = cum[seg + 1] - cum[seg];
            float t = segLen > 0.0 ? (float)((arc - cum[seg]) / segLen) : 0f;
            return new LineAnchor(seg, t);
        }

        private static LineAnchor[] BandAnchors(double3[] path, int n, double lo, double hi)
        {
            var a = new LineAnchor[n];
            for (int i = 0; i < n; i++) a[i] = AnchorAt(path, lo + (hi - lo) * (i + 0.5) / n);
            return a;
        }

        [Test]
        public void Tick_LineCenterPlacement_EmitsGlyphsDistributedAlongTheProjectedLine()
        {
            var camGo = Track(new GameObject("LineText_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            // A straight line along a latitude, projected from real geo endpoints so it spans a WIDE on-screen
            // segment (render-space units are huge, so a small render offset would project shorter than the
            // symbol and be correctly skipped). Midpoint (lon 20) projects to ~screen center.
            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 30.0 });
            var path = new double3[] { a, b };
            // 3 glyphs at arc-centers 0/24/48 baked px; text-size 24 (scale 1) → ~48px arc span, centered.
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, new[] { AnchorAt(path, 0.5) }, path,
                placement: SymbolPlacement.LineCenter, paint: SymbolPaint.Default, textSizePx: 24f,
                maxAngleDeg: 45f, keepUpright: true, featureIndex: 0, tileKey: 0L);

            // Stage AC (curved-world): curved now routes to the world sink like point/icon — needs its own
            // world base material for the world text slot to actually build+present (mirrors A1's point
            // migration, e.g. WorldPointEmitRenderTests).
            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            system.SymbolMaxDistanceFraction = double.PositiveInfinity; // structural test, not about distance culling: place symbols beyond the far plane without them being culled
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.TickSymbols(in frame, buffer, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, buffer, atlasTexture, mapCamera.Projection);

                Assert.AreEqual(3, system.LastQuadCount, "one quad per curved glyph (line label placed, not dropped)");

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh worldMesh), "the world text slot must exist (curved now draws through the world path).");
                WorldMeshReadback.Read(worldMesh, out WorldBillboardVertex[] v, out _);
                Assert.AreEqual(12, v.Length, "3 glyphs x 4 verts");

                // The glyphs are DISTRIBUTED along the line, not stacked at one point: each glyph's world
                // AnchorLocal (Stage AC — the Level-1 RTC bake) differs from its neighbours', so the 3D
                // spread of all corners' AnchorLocal exceeds a small threshold (robust to the projected
                // line's world orientation; the world path no longer has a single screen-px anchor to spread).
                float3 minA = new float3(float.MaxValue), maxA = new float3(float.MinValue);
                foreach (WorldBillboardVertex vv in v)
                {
                    minA = math.min(minA, vv.AnchorLocal);
                    maxA = math.max(maxA, vv.AnchorLocal);
                }
                float spread = math.length(maxA - minA);
                Assert.Greater(spread, 1f, "glyphs spread along the projected line (distinct world anchors), not stacked at one point");
            }
        }

        // ── #5 B4: symbol-placement:line REPEATS the symbol along the line at symbol-spacing px, whereas
        //    line-center places exactly one. Over the SAME wide projected line, `line` emits several symbols
        //    (a multiple of the 3 glyphs) while `line-center` emits one — proving the repetition is driven by
        //    the placement mode + spacing, not the geometry. allow-overlap isolates repetition from collision. ──
        [Test]
        public void Tick_LinePlacement_RepeatsSymbolAlongLine_WhereLineCenterPlacesOne()
        {
            var camGo = Track(new GameObject("LineRepeat_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            // A WIDE constant-latitude line (lon 5→35, centred on the camera's lon 20) → a long on-screen arc,
            // so a small symbol-spacing fits many repeats.
            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 5.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 35.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            // A-2: line-center → one centred anchor; line → several build-time anchors along the line (the
            // repetition is now driven by the pre-computed anchor set, not a per-frame screen-spacing walk).
            SymbolTileBuffer Curved(SymbolPlacement placement)
            {
                var s = new SymbolTileBuffer();
                LineAnchor[] anchors = placement == SymbolPlacement.LineCenter
                    ? new[] { AnchorAt(path, 0.5) }
                    : BandAnchors(path, 6, 0.1, 0.9);
                TestSymbolTileBuffer.AddCurved(s, glyphs, anchors, path,
                    placement: placement, paint: SymbolPaint.Default, textSizePx: 24f, maxAngleDeg: 45f,
                    keepUpright: true,
                    allowOverlap: true,    // isolate the repetition from collision suppression
                    featureIndex: 0, tileKey: 0L);
                return s;
            }

            using var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            system.SymbolMaxDistanceFraction = double.PositiveInfinity; // structural test, not about distance culling: place symbols beyond the far plane without them being culled
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                var centerSymbols = Curved(SymbolPlacement.LineCenter);
                system.TickSymbols(in frame, centerSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, centerSymbols, atlasTexture, mapCamera.Projection);
                int centerQuads = system.LastQuadCount;
                Assert.AreEqual(3, centerQuads, "line-center places exactly ONE label (3 glyphs) on the wide line");

                var lineSymbols = Curved(SymbolPlacement.Line);
                system.TickSymbols(in frame, lineSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, lineSymbols, atlasTexture, mapCamera.Projection);
                int lineQuads = system.LastQuadCount;

                Assert.AreEqual(0, lineQuads % 3, "every placed `line` repeat is a full 3-glyph label");
                Assert.Greater(lineQuads, centerQuads,
                    "symbol-placement:line repeats along the line — strictly more labels than the single line-center one");
                Assert.GreaterOrEqual(lineQuads / 3, 2, "at least two repeats fit on the wide line at 40px spacing");
            }
        }

        // ── A-2 defence-in-depth: build-time anchor placement is already capped (see LineAnchorPlacementTests),
        //    but the per-frame walk ALSO clamps the anchor iteration to MaxAnchorsPerLine (256) so a caller that
        //    somehow hands a huge anchor array can never spin an unbounded loop. Feed 300 anchors, all inside the
        //    fit band (allow-overlap, so none are dropped by collision) → exactly 256 place, not 300. ──
        [Test]
        public void Tick_LinePlacement_AnchorCount_IsHardCapped()
        {
            var camGo = Track(new GameObject("LineCap_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 5.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 35.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, BandAnchors(path, 300, 0.2, 0.8), path, // 300 anchors, all inside the fit band
                placement: SymbolPlacement.Line, paint: SymbolPaint.Default, textSizePx: 24f, maxAngleDeg: 45f,
                keepUpright: true,
                allowOverlap: true,   // don't let collision mask the cap — count the staged anchors directly
                featureIndex: 0, tileKey: 0L);

            using var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            system.SymbolMaxDistanceFraction = double.PositiveInfinity; // structural test, not about distance culling: place symbols beyond the far plane without them being culled
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                system.TickSymbols(in frame, buffer, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, buffer, atlasTexture, mapCamera.Projection);
                // 300 fitting anchors are clamped to MaxAnchorsPerLine (256) → exactly 256 × 3 glyphs; without
                // the clamp all 300 would place (900 quads). Mirrors the private cap constant.
                Assert.AreEqual(256 * 3, system.LastQuadCount, "the per-frame anchor iteration is hard-clamped to 256");
            }
        }

        // ── #6: text-max-angle drops a curved symbol whose glyphs would bend around a sharp corner more than the
        //    allowed adjacent-glyph angle — the SAME corner geometry places with a permissive angle, drops with
        //    a strict one. ──
        [Test]
        public void Tick_TextMaxAngle_DropsSymbolBendingRoundASharpCorner()
        {
            var camGo = Track(new GameObject("MaxAngle_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            // An L-shaped line: east (lon 10→20 at lat 20) then north (lat 20→30 at lon 20) — a ~90° screen
            // corner at the midpoint, where a centred symbol's glyphs straddle the bend.
            double3 p0 = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            double3 p1 = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 });
            double3 p2 = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 20.0 });
            var path = new double3[] { p0, p1, p2 };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            SymbolTileBuffer Curved(float maxAngleDeg)
            {
                var s = new SymbolTileBuffer();
                // Anchor pinned to the corner vertex (end of segment 0) so the symbol's glyphs deterministically
                // straddle the ~90° bend — the geometry that text-max-angle must drop.
                TestSymbolTileBuffer.AddCurved(s, glyphs, new[] { new LineAnchor(0, 1f) }, path,
                    placement: SymbolPlacement.LineCenter, paint: SymbolPaint.Default, textSizePx: 24f,
                    maxAngleDeg: maxAngleDeg, keepUpright: true, featureIndex: 0, tileKey: 0L);
                return s;
            }

            using var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                var permissive = Curved(170f);
                system.TickSymbols(in frame, permissive, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, permissive, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(3, system.LastQuadCount, "a permissive text-max-angle places the label round the corner");

                var strict = Curved(40f);
                system.TickSymbols(in frame, strict, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, strict, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(0, system.LastQuadCount,
                    "text-max-angle:40 drops the label — the ~90° corner exceeds the allowed adjacent-glyph angle");
            }
        }

        // ── #6: text-keep-upright changes how a right-to-left line symbol is laid out (reversed walk + glyph
        //    flip vs. following the raw line direction) — both place 3 glyphs, but the meshes differ. ──
        [Test]
        public void Tick_TextKeepUpright_ChangesLayoutOfARightToLeftLine()
        {
            var camGo = Track(new GameObject("KeepUpright_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            // A right-to-LEFT line (lon 30 → lon 10): its center tangent points west, so keep-upright flips it.
            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 30.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            SymbolTileBuffer Curved(bool keepUpright)
            {
                var s = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddCurved(s, glyphs, new[] { AnchorAt(path, 0.5) }, path,
                    placement: SymbolPlacement.LineCenter, paint: SymbolPaint.Default, textSizePx: 24f,
                    maxAngleDeg: 45f, keepUpright: keepUpright, featureIndex: 0, tileKey: 0L);
                return s;
            }

            // Stage AC (curved-world): curved now routes to the world sink — needs a world base material for
            // the world text slot to build+present (mirrors the LineCenter-distribution tooth above).
            using var system = new SymbolPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            system.SymbolMaxDistanceFraction = double.PositiveInfinity; // structural test, not about distance culling: place symbols beyond the far plane without them being culled
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                var uprightSymbols = Curved(true);
                system.TickSymbols(in frame, uprightSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, uprightSymbols, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(3, system.LastQuadCount, "keep-upright:true still places all 3 glyphs");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh uprightMesh), "the world text slot must exist.");
                WorldMeshReadback.Read(uprightMesh, out WorldBillboardVertex[] upright, out _);

                var rawSymbols = Curved(false);
                system.TickSymbols(in frame, rawSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, rawSymbols, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(3, system.LastQuadCount, "keep-upright:false still places all 3 glyphs");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, SymbolKind.Text, out Mesh rawMesh), "the world text slot must still exist.");
                WorldMeshReadback.Read(rawMesh, out WorldBillboardVertex[] raw, out _);

                // The flag has a real effect: the reversed arc walk (different world anchor per glyph) + the
                // negated world Tangent lay the symbol out differently — compare BOTH the new baked fields.
                Assert.AreEqual(upright.Length, raw.Length, "same glyph count → same vertex count");
                bool anyDiff = false;
                for (int i = 0; i < upright.Length && !anyDiff; i++)
                    if (math.length(upright[i].AnchorLocal - raw[i].AnchorLocal) > 0.5f
                        || math.length(upright[i].Tangent - raw[i].Tangent) > 0.1f) anyDiff = true;
                Assert.IsTrue(anyDiff,
                    "keep-upright:true must lay a right-to-left label out differently than keep-upright:false");
            }
        }

        // ── #5 B3 END-TO-END: two OVERLAPPING curved symbols (same line, allow-overlap OFF) collide through the
        //    real rotated-glyph box → candidate → CollisionJob → emit chain — only the lower-sort-key one
        //    places. This is the decisive proof that curved symbols actually collide (every other curved Tick
        //    test is single-symbol or allow-overlap); it also exercises SymbolBox.BuildRotatedGlyph for real. ──
        [Test]
        public void Tick_TwoOverlappingCurvedSymbols_OnlyLowerSortKeyPlaces()
        {
            var camGo = Track(new GameObject("CurvedCollide_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                Rebase = float3x3.identity,
            };
            using var atlasTexture = BuildTinyAtlasTexture();

            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 30.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            // Two symbols centred on the SAME projected line → their glyph boxes coincide. featureIndex differs so
            // the placement order is total; sort keys pick the winner.
            void AddCurved(SymbolTileBuffer s, int featureIndex, float sortKey, bool allowOverlap)
                => TestSymbolTileBuffer.AddCurved(s, glyphs, new[] { AnchorAt(path, 0.5) }, path,
                    placement: SymbolPlacement.LineCenter, paint: SymbolPaint.Default, textSizePx: 24f,
                    paddingPx: 2f, maxAngleDeg: 45f, keepUpright: true, sortKey: sortKey,
                    allowOverlap: allowOverlap, featureIndex: featureIndex, tileKey: 0L);

            using var system = new SymbolPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            system.SymbolMaxDistanceFraction = double.PositiveInfinity; // structural test, not about distance culling: place symbols beyond the far plane without them being culled
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                // Collision ON: the two coincident symbols fight → only the lower-key one survives (3 glyphs).
                var collidingSymbols = new SymbolTileBuffer();
                AddCurved(collidingSymbols, featureIndex: 0, sortKey: 20f, allowOverlap: false);
                AddCurved(collidingSymbols, featureIndex: 1, sortKey: 10f, allowOverlap: false);
                system.TickSymbols(in frame, collidingSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, collidingSymbols, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(glyphs.Count, system.LastQuadCount,
                    "two overlapping curved labels collide — only the lower-sort-key one places (not both)");

                // Control: allow-overlap ON → both place (6 glyphs), proving the drop above was collision, not a
                // spurious single-symbol limit.
                var overlapAllowedSymbols = new SymbolTileBuffer();
                AddCurved(overlapAllowedSymbols, featureIndex: 0, sortKey: 20f, allowOverlap: true);
                AddCurved(overlapAllowedSymbols, featureIndex: 1, sortKey: 10f, allowOverlap: true);
                system.TickSymbols(in frame, overlapAllowedSymbols, atlasTexture, mapCamera.Projection);
                system.TickSymbols(in frame, overlapAllowedSymbols, atlasTexture, mapCamera.Projection);
                Assert.AreEqual(glyphs.Count * 2, system.LastQuadCount,
                    "with allow-overlap both coincident curved labels place — so the collision was real above");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStageJobTests — the Burst StageJob must match its managed SymbolStagingMath reference
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lever C step 3b: the Burst <see cref="StageJob"/> must make the same placement decisions as the managed
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
                // StageJob.View), so the differential compares the same transform on each side. The
                // bend cases below pass `default`, which keeps them byte-identical to their pre-W3 form;
                // BurstStage_MatchesManaged_MapPitchedCurved passes a real one and is what covers W3's
                // projected-corner arithmetic (F-W3-5, discharged).
                new float2[screen.Length], new float[screen.Length], bearing, view, 0,
                boxes, ref bc, quads, ref qc, cands, emit, ref ec);
            return new Result { Staged = staged, BoxCount = bc, QuadCount = qc, Boxes = boxes, Quads = quads, Candidates = cands };
        }

        // The Burst path: StageJob over a one-record curved batch mirror.
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
            // R2: AnchorWasPlaced is now JOB-OWNED scratch — StageJob.Execute fills it from AnchorFadeIds +
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

            new StageJob
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
                // ASYMMETRY the two arms must bridge explicitly: StageJob.Execute PATCHES
                // s.MetresPerLogicalPixel from this per-frame field (StageJob.cs:178), so the value
                // stored on the CurvedStageInput is IGNORED here while the managed arm reads it straight off
                // `s`. Setting it only on `s` gives the managed arm a live ruler and the Burst arm a zero
                // one, so they silently take different branches and the differential fails on geometry
                // rather than on the real cause. Default 0 keeps the bend cases byte-identical.
                MetresPerLogicalPixel = metresPerLogicalPixel,
                PathPoints = path, CumulativeLengths = cum,
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
            // StageJob resolves incumbency through in production (both arms resolve off one set). A
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
        /// W3 teeth read boxes produced by <c>StageJob</c>, which is
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // WorldBillboardRtcAlgebraTests — the world-anchored path composes via two float32-narrowed terms
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class WorldBillboardRtcAlgebraTests : BaseTestFixture
    {
        private const int ViewportSize = 1024;

        // Glyph-corner offsets (logical px, y-up — mirrors BillboardMath's anchor-relative corner
        // convention): TL/TR/BR/BL of a nominal 40x20px glyph box around the anchor.
        private static readonly float2[] GlyphCornerOffsets =
        {
            new float2(-20f,  10f), // top-left
            new float2( 20f,  10f), // top-right
            new float2( 20f, -10f), // bottom-right
            new float2(-20f, -10f), // bottom-left
        };

        // A few look-at locations, including one far from Mercator's absolute (0,0) origin (near the
        // antimeridian) — the RTC cancellation must hold regardless of the ABSOLUTE Mercator magnitude,
        // per FloatingOrigin's doc (precision is governed by camera-to-sceneOrigin distance, not by how
        // far sceneOrigin sits from Mercator's own origin).
        private static readonly GeoCoordinate[] LookAts =
        {
            new GeoCoordinate { Latitude = 30.0,  Longitude = 30.0 },
            new GeoCoordinate { Latitude = 0.0,   Longitude = 0.0 },
            new GeoCoordinate { Latitude = -45.0, Longitude = -60.0 },
            new GeoCoordinate { Latitude = 60.0,  Longitude = 170.0 },
        };

        private static readonly int[] TileZooms = { 2, 8, 14, 20 };

        // Epic A / A1 (Codex minor — z0/z1 note, design §11 A1 D2): a z0/z1 tile spans a quarter-to-whole
        // Earth, so its AnchorLocal magnitude approaches the "wrong tile" regime — a RELAXED bound (not the
        // 0.5px used for z2-20 above) pins that the on-screen error stays BOUNDED (sub-pixel) rather than
        // diverging, the accepted low-zoom known-limit.
        private static readonly int[] LowZoomTileZooms = { 0, 1 };
        private const float LowZoomBoundPx = 2.0f;

        [Test]
        public void WorldPathCornerScreenPosition_MatchesOldPathAcrossZoomsCornersAndTileOrigins()
        {
            var camGo = Track(new GameObject("WorldBillboardRtcAlgebra_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            {
                uCam.targetTexture = new RenderTexture(ViewportSize, ViewportSize, 0);
                var projection = new WebMercatorProjection();

                foreach (GeoCoordinate lookAt in LookAts)
                {
                    var mapCamera = new MapCamera(uCam, new CameraProperties(
                        new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                        zoom: 10.0, heading: 0.0, tilt: 0.0), projection: projection);

                    double3 sceneOriginRender = mapCamera.Projection.Project(lookAt);
                    var frame = new SceneFrame { SceneOriginRender = sceneOriginRender, Rebase = float3x3.identity };

                    double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                    float4x4 viewProj = math.mul(
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));

                    // A small, deterministic offset from the look-at (mirrors
                    // SymbolAtlasOrientationSnapshotTests' pattern) so the anchor is not exactly screen-center.
                    double altitude = uCam.transform.position.y;
                    double3 anchorRender = sceneOriginRender + new double3(altitude * 0.01, 0.0, altitude * 0.02);

                    foreach (int tileZoom in TileZooms)
                    {
                        // Two tile origins per zoom: the tile actually containing the anchor (the realistic
                        // case) and a neighbor tile that does NOT contain it (proves the cancellation doesn't
                        // depend on containment).
                        TileId containing = TileContaining(lookAt, tileZoom);
                        TileId neighbor = new TileId { X = containing.X + 1, Y = containing.Y, Z = tileZoom };

                        foreach (TileId tile in new[] { containing, neighbor })
                        {
                            double3 tileOriginRender = TileRenderOrigin.Project(tile, projection);

                            foreach (float2 offsetPx in GlyphCornerOffsets)
                            {
                                float2 newScreenPx = ProjectWorldPathCorner(
                                    anchorRender, tileOriginRender, sceneOriginRender, viewProj, viewportLogicalPx, offsetPx);

                                Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                                        anchorRender, sceneOriginRender, viewProj, viewportLogicalPx, float3x3.identity,
                                        out float2 oldAnchorScreenPx, out _),
                                    $"anchor must project in front of the camera (lookAt {lookAt.Latitude},{lookAt.Longitude}, tileZoom {tileZoom})");
                                float2 oldScreenPx = oldAnchorScreenPx + offsetPx; // OLD path: BillboardMath adds the corner offset directly

                                Assert.That(newScreenPx.x, Is.EqualTo(oldScreenPx.x).Within(0.5f),
                                    $"X mismatch: lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={tile} offset={offsetPx}");
                                Assert.That(newScreenPx.y, Is.EqualTo(oldScreenPx.y).Within(0.5f),
                                    $"Y mismatch: lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={tile} offset={offsetPx}");
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void WorldPathCornerScreenPosition_MatchesOldPathAtZ0Z1_WithinRelaxedBound()
        {
            var camGo = Track(new GameObject("WorldBillboardRtcAlgebraLowZoom_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            {
                uCam.targetTexture = new RenderTexture(ViewportSize, ViewportSize, 0);
                var projection = new WebMercatorProjection();

                foreach (GeoCoordinate lookAt in LookAts)
                {
                    var mapCamera = new MapCamera(uCam, new CameraProperties(
                        new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                        zoom: 10.0, heading: 0.0, tilt: 0.0), projection: projection);

                    double3 sceneOriginRender = mapCamera.Projection.Project(lookAt);
                    var frame = new SceneFrame { SceneOriginRender = sceneOriginRender, Rebase = float3x3.identity };

                    double2 viewportLogicalPx = mapCamera.ViewportPx / mapCamera.DevicePixelRatio;
                    float4x4 viewProj = math.mul(
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.projectionMatrix),
                        SymbolPlacementSystem.ToFloat4x4(mapCamera.Camera.worldToCameraMatrix));

                    double altitude = uCam.transform.position.y;
                    double3 anchorRender = sceneOriginRender + new double3(altitude * 0.01, 0.0, altitude * 0.02);

                    foreach (int tileZoom in LowZoomTileZooms)
                    {
                        TileId containing = TileContaining(lookAt, tileZoom);
                        double3 tileOriginRender = TileRenderOrigin.Project(containing, projection);

                        foreach (float2 offsetPx in GlyphCornerOffsets)
                        {
                            float2 newScreenPx = ProjectWorldPathCorner(
                                anchorRender, tileOriginRender, sceneOriginRender, viewProj, viewportLogicalPx, offsetPx);

                            Assert.IsTrue(SymbolScreenProjection.TryProjectPoint(
                                    anchorRender, sceneOriginRender, viewProj, viewportLogicalPx, float3x3.identity,
                                    out float2 oldAnchorScreenPx, out _),
                                $"anchor must project in front of the camera (lookAt {lookAt.Latitude},{lookAt.Longitude}, tileZoom {tileZoom})");
                            float2 oldScreenPx = oldAnchorScreenPx + offsetPx;

                            Assert.That(newScreenPx.x, Is.EqualTo(oldScreenPx.x).Within(LowZoomBoundPx),
                                $"X mismatch (relaxed low-zoom bound): lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={containing} offset={offsetPx}");
                            Assert.That(newScreenPx.y, Is.EqualTo(oldScreenPx.y).Within(LowZoomBoundPx),
                                $"Y mismatch (relaxed low-zoom bound): lookAt=({lookAt.Latitude},{lookAt.Longitude}) tileZoom={tileZoom} tile={containing} offset={offsetPx}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Reproduces the GPU's world-anchored composition in managed code: Level-1 bake
        /// (<c>AnchorLocal = anchorRender − tileOriginRender</c>, narrowed to float32), Level-2 per-frame
        /// transform (<see cref="FloatingOrigin.TileToSceneRebased"/>), the stock MVP multiply, and the
        /// SAME clip-space offset the vertex shader applies
        /// (<c>clip.xy += offsetPx / _ScreenParamsLogical.xy * 2 * clip.w</c> — <c>SymbolTextWorld_ForwardPass.hlsl</c>).
        /// </summary>
        private static float2 ProjectWorldPathCorner(
            in double3 anchorRender, in double3 tileOriginRender, in double3 sceneOriginRender,
            in float4x4 viewProj, in double2 viewportLogicalPx, in float2 offsetPx)
        {
            // Level 1: mesh-baked, tile-origin-relative (manual per-component narrow — mirrors
            // FloatingOrigin.TileToSceneRebased's own narrowing convention).
            float3 anchorLocal = new float3(
                (float)(anchorRender.x - tileOriginRender.x),
                (float)(anchorRender.y - tileOriginRender.y),
                (float)(anchorRender.z - tileOriginRender.z));

            // Level 2: per-frame tile transform (identity rebase ⇒ Mercator).
            float3 tileScenePos = FloatingOrigin.TileToSceneRebased(tileOriginRender, sceneOriginRender, float3x3.identity);

            // unity_ObjectToWorld * float4(AnchorLocal, 1) with an identity rotation ⇒ AnchorLocal + T.
            float3 worldPos = anchorLocal + tileScenePos;

            float4 clip = math.mul(viewProj, new float4(worldPos, 1f));

            float viewportX = (float)viewportLogicalPx.x;
            float viewportY = (float)viewportLogicalPx.y;

            // The pinned shader line: clip.xy += off / _ScreenParamsLogical.xy * 2.0 * clip.w (_ScreenParamsLogical
            // is set to viewportLogicalPx — see WorldBillboardMeshBuilder/SymbolPlacementSystem's identical convention).
            clip.x += offsetPx.x / viewportX * 2.0f * clip.w;
            clip.y += offsetPx.y / viewportY * 2.0f * clip.w;

            float ndcX = clip.x / clip.w;
            float ndcY = clip.y / clip.w;
            return new float2((ndcX * 0.5f + 0.5f) * viewportX, (ndcY * 0.5f + 0.5f) * viewportY);
        }

        /// <summary>Standard slippy-map lon/lat → tile x/y at <paramref name="zoom"/> (Web Mercator).</summary>
        private static TileId TileContaining(in GeoCoordinate geo, int zoom)
        {
            double n = math.pow(2.0, zoom);
            double latRad = geo.Latitude * math.PI_DBL / 180.0;
            double x = (geo.Longitude + 180.0) / 360.0 * n;
            double y = (1.0 - math.log(math.tan(latRad) + 1.0 / math.cos(latRad)) / math.PI_DBL) / 2.0 * n;
            return new TileId { X = (int)math.floor(x), Y = (int)math.floor(y), Z = zoom };
        }
    }
}
