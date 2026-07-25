// Unity EditMode only — reads source files under Application.dataPath + needs a real Camera/Mesh. NOT
// registered in core-tests.csproj.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 T5: labels are the per-frame path, never the static tile path.
    /// (a) STRUCTURAL — a grep guard: nothing under the label-placement source tree calls
    ///     <c>ITileRenderBackend.AddTileLayer</c>.
    /// (b) BEHAVIORAL — the billboard buffer is rebuilt from scratch every <see cref="LabelPlacementSystem.Tick"/>,
    ///     not cached/accumulated across calls.
    /// </summary>
    [TestFixture]
    public class LabelPlacementStructureTests
    {
        // ── (a) Structural: grep guard ───────────────────────────────────────────────────────────

        [Test]
        public void SourceTree_NeverReferencesAddTileLayer()
        {
            string dir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Text", "Placement");
            Assert.IsTrue(Directory.Exists(dir), $"expected the label-placement source directory to exist at {dir}");

            // Match the CALL form "AddTileLayer(" (no space before the paren) so a doc comment discussing
            // the constraint in prose ("must never call AddTileLayer (the static ... path)") is not itself
            // flagged as an offender -- only an actual invocation is.
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (text.Contains("AddTileLayer("))
                {
                    offenders.Add(file);
                }
            }

            Assert.IsEmpty(offenders,
                "The per-frame label placement path must NEVER call AddTileLayer (the static per-(tile,layer) " +
                "mesh path) -- it is a structurally separate submission path (T5). Offending files:\n" +
                string.Join("\n", offenders));
        }

        // ── E2: Graphics.RenderMesh is retired — labels draw via persistent per-slot MeshRenderers now
        //    (LabelSlotPresenter), never an immediate-mode per-frame submission (design §5 option (c)). ──
        [Test]
        public void SourceTree_NeverReferencesGraphicsRenderMesh()
        {
            string dir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            Assert.IsTrue(Directory.Exists(dir), $"expected the MapRenderer.Unity source directory to exist at {dir}");

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
                "-- symbol labels draw via persistent per-slot MeshRenderers (LabelSlotPresenter), redrawn by " +
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
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        // AnchorRender is render-space PRE-RTC (the same space projection.Project(geo) emits) -- NOT a
        // small local offset. It must be built relative to the frame's SceneOriginRender (a large absolute
        // Mercator coordinate), or TryProjectAnchor's rebase lands it far outside the viewport and every
        // label is silently culled (the bug this comment fixes: LastQuadCount was 0, not 2/1).
        private static LabelInstance MakeLabel(int featureIndex, double3 sceneOriginRender, float2 translatePx = default,
            AlignmentMode rotationAlignment = AlignmentMode.Auto)
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
            var layout = new TextLayoutResult { Quads = quads, BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1 };
            return new LabelInstance
            {
                AnchorRender = sceneOriginRender + new double3(featureIndex * 5.0, 0.0, featureIndex * 2.0),
                Layout = layout,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                SortKey = 0f,
                FeatureIndex = featureIndex,
                TileKey = 0L,
                // AllowOverlap: this test asserts the buffer is REBUILT-not-accumulated every Tick (2/1/2
                // quads), which is orthogonal to Slice-2 collision. Without it, whether the two nearby
                // anchors' boxes overlap (and one gets culled) is projection-dependent and would make the
                // quad-count assertion flaky. Collision itself is covered by LabelCollisionTests (T1).
                AllowOverlap = true,
                TranslatePx = translatePx,
                RotationAlignment = rotationAlignment,
            };
        }

        [Test]
        public void Tick_AdvancesTickCount_AndRebuildsQuadCountFromScratchEveryCall()
        {
            var camGo = new GameObject("LabelStructure_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);

            var atlasTexture = BuildTinyAtlasTexture();
            var twoLabels = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender), MakeLabel(1, frame.SceneOriginRender) };
            var oneLabel = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender) };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                Assert.AreEqual(0, system.TickCount, "TickCount starts at 0 before any Tick.");

                // R3 (deferred collision, design §10.3): the collision verdict a Tick's emit reads is the one
                // HARVESTED at that Tick's top — i.e. the collision scheduled at the end of the PREVIOUS Tick,
                // over the PREVIOUS Tick's candidates (§2.6). A candidate-set change (twoLabels → oneLabel →
                // twoLabels) therefore needs TWO Ticks before its placement can be asserted, so each step below
                // duplicates its Tick call (same args) — TickCount advances TWICE per assertion step (2, 4, 6),
                // NOT because of a fudged expectation, but because it counts Ticks and this fixture now ticks
                // twice per step. The quad-count expectations (2 / 1 / 2) — the actual subject of this test — do
                // NOT move.
                system.Tick(in frame, twoLabels, atlasTexture);
                system.Tick(in frame, twoLabels, atlasTexture);
                Assert.AreEqual(2, system.TickCount);
                Assert.AreEqual(2, system.LastQuadCount, "2 labels x 1 quad each = 2 placed quads.");

                // Rebuilt from scratch, not accumulated: ticking with FEWER labels must report FEWER quads,
                // not the sum of every Tick so far (which would prove a cache/append bug).
                system.Tick(in frame, oneLabel, atlasTexture);
                system.Tick(in frame, oneLabel, atlasTexture);
                Assert.AreEqual(4, system.TickCount);
                Assert.AreEqual(1, system.LastQuadCount,
                    "a Tick with 1 label must report 1 placed quad -- NOT 3 (2 from the prior Tick + 1), " +
                    "which would mean the buffer is cached/appended instead of rebuilt every Tick.");

                system.Tick(in frame, twoLabels, atlasTexture);
                system.Tick(in frame, twoLabels, atlasTexture);
                Assert.AreEqual(6, system.TickCount);
                Assert.AreEqual(2, system.LastQuadCount);
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── Slice C: text-translate shifts the placed billboard vertices in screen space (guards that
        //    LabelPlacementSystem.Tick actually applies LabelTranslate — the fast LabelTranslateTests only
        //    cover the pure delta math; this proves Tick calls it). ──
        [Test]
        public void Tick_TextTranslate_ShiftsEveryPlacedVertex_ByScreenDelta()
        {
            var camGo = new GameObject("LabelTranslate_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            // MapLibre text-translate [7,3] = right 7, DOWN 3 → screen (y-up) delta (+7, -3), depth unchanged.
            var translate = new float2(7f, 3f);
            var baseline = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender) };
            var moved    = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender, translate) };

            // Epic A / A1: this label is a POINT (default Placement) — it now draws through the world path.
            // The screen-space translate no longer shows up as a delta on system.Mesh's Position (screen px);
            // D4 carries it as an ADDITIVE, UNROTATED OffsetPx delta instead (BillboardMath.BuildWorldQuad),
            // with the SAME A0-F2 Y-negation as the glyph corner — so the sign convention differs from the
            // OLD path's raw screen-vertex delta (see BuildWorldQuad's doc for the derivation).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: the collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick (§2.6) —
                // duplicate each candidate-set's Tick call before reading its placement.
                system.Tick(in frame, baseline, atlasTexture);
                system.Tick(in frame, baseline, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "one label, one quad");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh mesh0), "the world slot mesh must exist.");
                WorldMeshReadback.Read(mesh0, out WorldBillboardVertex[] v0, out _);

                system.Tick(in frame, moved, atlasTexture);
                system.Tick(in frame, moved, atlasTexture);
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh mesh1), "the world slot mesh must exist.");
                WorldMeshReadback.Read(mesh1, out WorldBillboardVertex[] v1, out _);

                Assert.AreEqual(v0.Length, v1.Length, "same vertex count (only the translate changed)");
                Assert.Greater(v0.Length, 0, "the label placed at least one quad");
                for (int i = 0; i < v0.Length; i++)
                {
                    Assert.AreEqual(translate.x, v1[i].OffsetPx.x - v0[i].OffsetPx.x, 1e-3f, $"vertex {i}: +tx in OffsetPx.x");
                    Assert.AreEqual(translate.y, v1[i].OffsetPx.y - v0[i].OffsetPx.y, 1e-3f, $"vertex {i}: +ty in OffsetPx.y (D4's SAME Y-negation as the corner, applied to both — the two negations cancel back to +ty)");
                    Assert.AreEqual(v0[i].AnchorLocal, v1[i].AnchorLocal, "the anchor itself is untouched by a screen-space translate — only OffsetPx moves");
                }
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── #4: text-rotation-alignment:map rotates the billboard with the map bearing (guards that Tick
        //    threads label.RotationAlignment + the bearing into BillboardMath — the axis-aligned viewport
        //    billboard becomes a rotated quad under a non-zero heading). ──
        [Test]
        public void Tick_RotationAlignmentMap_RotatesBillboard_UnderBearing_ViewportStaysAxisAligned()
        {
            var camGo = new GameObject("LabelRotation_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            // A non-zero heading (45°) — at bearing 0 map and viewport coincide, so the rotation is only
            // observable with an active bearing.
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 45.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            var viewportLabels = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender, default, AlignmentMode.Viewport) };
            var mapLabels      = new List<LabelInstance> { MakeLabel(0, frame.SceneOriginRender, default, AlignmentMode.Map) };

            // Epic A / A1: this label is a POINT — it now draws through the world path. The rotation is baked
            // into OffsetPx (D3 — every frame, byte-equivalent to the old path while A1's emit runs every
            // Tick) rather than a raw screen-vertex position, so this reads OffsetPx off the world mesh
            // instead of system.Mesh.vertices. Winding is IDENTICAL to the old path (BuildWorldQuad mirrors
            // BuildQuad's TL/TR/BR/BL corner order).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                system.Tick(in frame, viewportLabels, atlasTexture);
                system.Tick(in frame, viewportLabels, atlasTexture);
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh viewportMesh), "the world slot mesh must exist.");
                WorldMeshReadback.Read(viewportMesh, out WorldBillboardVertex[] vp, out _); // v[0]=TL, v[1]=TR, v[2]=BR, v[3]=BL
                Assert.AreEqual(4, vp.Length, "one quad → 4 verts");

                system.Tick(in frame, mapLabels, atlasTexture);
                system.Tick(in frame, mapLabels, atlasTexture);
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh mapMesh), "the world slot mesh must exist.");
                WorldMeshReadback.Read(mapMesh, out WorldBillboardVertex[] mp, out _);

                // Viewport: top edge horizontal (axis-aligned billboard) — TL.OffsetPx.y == TR.OffsetPx.y.
                Assert.AreEqual(vp[0].OffsetPx.y, vp[1].OffsetPx.y, 1e-3f, "viewport billboard's top edge stays horizontal");
                // Map under a 45° bearing: the quad is rotated, so the top edge is NOT horizontal.
                Assert.That(math.abs(mp[0].OffsetPx.y - mp[1].OffsetPx.y), Is.GreaterThan(1f),
                    "rotation-alignment:map must rotate the billboard under a non-zero bearing (top edge no longer horizontal)");
                // And it genuinely differs from the viewport placement (rotation actually applied).
                Assert.That(math.abs(mp[1].OffsetPx.x - vp[1].OffsetPx.x) + math.abs(mp[1].OffsetPx.y - vp[1].OffsetPx.y), Is.GreaterThan(1f),
                    "map- and viewport-aligned billboards must differ under a non-zero bearing");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── #5 B2: a curved (symbol-placement:line-center) label emits one quad per glyph, distributed along
        //    the projected line (guards that Tick projects the path, walks it, and places per-glyph quads —
        //    line labels have a null Layout so the point path emits nothing for them). ──
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

        // A-2: a curved label carries pre-computed LineAnchors (stable (segment,t) topology). These helpers
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
            var camGo = new GameObject("LineText_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            // A straight line along a latitude, projected from real geo endpoints so it spans a WIDE on-screen
            // segment (render-space units are huge, so a small render offset would project shorter than the
            // label and be correctly skipped). Midpoint (lon 20) projects to ~screen center.
            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 30.0 });
            var path = new double3[] { a, b };
            // 3 glyphs at arc-centers 0/24/48 baked px; text-size 24 (scale 1) → ~48px arc span, centered.
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            var lineLabel = new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                PathRender = path,
                LineAnchors = new[] { AnchorAt(path, 0.5) },
                CurvedGlyphs = glyphs,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                MaxAngleDeg = 45f,
                KeepUpright = true,
                FeatureIndex = 0,
                TileKey = 0L,
            };

            // Stage AC (curved-world): curved now routes to the world sink like point/icon — needs its own
            // world base material for the world text slot to actually build+present (mirrors A1's point
            // migration, e.g. WorldPointEmitRenderTests).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                var labels = new List<LabelInstance> { lineLabel };
                system.Tick(in frame, labels, atlasTexture);
                system.Tick(in frame, labels, atlasTexture);

                Assert.AreEqual(3, system.LastQuadCount, "one quad per curved glyph (line label placed, not dropped)");

                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh worldMesh), "the world text slot must exist (curved now draws through the world path).");
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
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── #5 B4: symbol-placement:line REPEATS the label along the line at symbol-spacing px, whereas
        //    line-center places exactly one. Over the SAME wide projected line, `line` emits several labels
        //    (a multiple of the 3 glyphs) while `line-center` emits one — proving the repetition is driven by
        //    the placement mode + spacing, not the geometry. allow-overlap isolates repetition from collision. ──
        [Test]
        public void Tick_LinePlacement_RepeatsLabelAlongLine_WhereLineCenterPlacesOne()
        {
            var camGo = new GameObject("LineRepeat_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            // A WIDE constant-latitude line (lon 5→35, centred on the camera's lon 20) → a long on-screen arc,
            // so a small symbol-spacing fits many repeats.
            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 5.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 35.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            // A-2: line-center → one centred anchor; line → several build-time anchors along the line (the
            // repetition is now driven by the pre-computed anchor set, not a per-frame screen-spacing walk).
            LabelInstance Curved(SymbolPlacement placement) => new LabelInstance
            {
                Placement = placement,
                PathRender = path,
                LineAnchors = placement == SymbolPlacement.LineCenter
                    ? new[] { AnchorAt(path, 0.5) }
                    : BandAnchors(path, 6, 0.1, 0.9),
                CurvedGlyphs = glyphs,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                MaxAngleDeg = 45f,
                KeepUpright = true,
                AllowOverlap = true,    // isolate the repetition from collision suppression
                FeatureIndex = 0,
                TileKey = 0L,
            };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                var centerLabels = new List<LabelInstance> { Curved(SymbolPlacement.LineCenter) };
                system.Tick(in frame, centerLabels, atlasTexture);
                system.Tick(in frame, centerLabels, atlasTexture);
                int centerQuads = system.LastQuadCount;
                Assert.AreEqual(3, centerQuads, "line-center places exactly ONE label (3 glyphs) on the wide line");

                var lineLabels = new List<LabelInstance> { Curved(SymbolPlacement.Line) };
                system.Tick(in frame, lineLabels, atlasTexture);
                system.Tick(in frame, lineLabels, atlasTexture);
                int lineQuads = system.LastQuadCount;

                Assert.AreEqual(0, lineQuads % 3, "every placed `line` repeat is a full 3-glyph label");
                Assert.Greater(lineQuads, centerQuads,
                    "symbol-placement:line repeats along the line — strictly more labels than the single line-center one");
                Assert.GreaterOrEqual(lineQuads / 3, 2, "at least two repeats fit on the wide line at 40px spacing");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── A-2 defence-in-depth: build-time anchor placement is already capped (see LineAnchorPlacementTests),
        //    but the per-frame walk ALSO clamps the anchor iteration to MaxAnchorsPerLine (256) so a caller that
        //    somehow hands a huge anchor array can never spin an unbounded loop. Feed 300 anchors, all inside the
        //    fit band (allow-overlap, so none are dropped by collision) → exactly 256 place, not 300. ──
        [Test]
        public void Tick_LinePlacement_AnchorCount_IsHardCapped()
        {
            var camGo = new GameObject("LineCap_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 5.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 35.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            var label = new LabelInstance
            {
                Placement = SymbolPlacement.Line,
                PathRender = path,
                LineAnchors = BandAnchors(path, 300, 0.2, 0.8), // 300 anchors, all inside the fit band
                CurvedGlyphs = glyphs,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                MaxAngleDeg = 45f,
                KeepUpright = true,
                AllowOverlap = true,   // don't let collision mask the cap — count the staged anchors directly
                FeatureIndex = 0,
                TileKey = 0L,
            };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
                var labels = new List<LabelInstance> { label };
                system.Tick(in frame, labels, atlasTexture);
                system.Tick(in frame, labels, atlasTexture);
                // 300 fitting anchors are clamped to MaxAnchorsPerLine (256) → exactly 256 × 3 glyphs; without
                // the clamp all 300 would place (900 quads). Mirrors the private cap constant.
                Assert.AreEqual(256 * 3, system.LastQuadCount, "the per-frame anchor iteration is hard-clamped to 256");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── #6: text-max-angle drops a curved label whose glyphs would bend around a sharp corner more than the
        //    allowed adjacent-glyph angle — the SAME corner geometry places with a permissive angle, drops with
        //    a strict one. ──
        [Test]
        public void Tick_TextMaxAngle_DropsLabelBendingRoundASharpCorner()
        {
            var camGo = new GameObject("MaxAngle_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            // An L-shaped line: east (lon 10→20 at lat 20) then north (lat 20→30 at lon 20) — a ~90° screen
            // corner at the midpoint, where a centred label's glyphs straddle the bend.
            double3 p0 = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            double3 p1 = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 });
            double3 p2 = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 20.0 });
            var path = new double3[] { p0, p1, p2 };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            LabelInstance Curved(float maxAngleDeg) => new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                PathRender = path,
                // Anchor pinned to the corner vertex (end of segment 0) so the label's glyphs deterministically
                // straddle the ~90° bend — the geometry that text-max-angle must drop.
                LineAnchors = new[] { new LineAnchor(0, 1f) },
                CurvedGlyphs = glyphs,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                MaxAngleDeg = maxAngleDeg,
                KeepUpright = true,
                FeatureIndex = 0,
                TileKey = 0L,
            };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                var permissive = new List<LabelInstance> { Curved(170f) };
                system.Tick(in frame, permissive, atlasTexture);
                system.Tick(in frame, permissive, atlasTexture);
                Assert.AreEqual(3, system.LastQuadCount, "a permissive text-max-angle places the label round the corner");

                var strict = new List<LabelInstance> { Curved(40f) };
                system.Tick(in frame, strict, atlasTexture);
                system.Tick(in frame, strict, atlasTexture);
                Assert.AreEqual(0, system.LastQuadCount,
                    "text-max-angle:40 drops the label — the ~90° corner exceeds the allowed adjacent-glyph angle");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── #6: text-keep-upright changes how a right-to-left line label is laid out (reversed walk + glyph
        //    flip vs. following the raw line direction) — both place 3 glyphs, but the meshes differ. ──
        [Test]
        public void Tick_TextKeepUpright_ChangesLayoutOfARightToLeftLine()
        {
            var camGo = new GameObject("KeepUpright_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            // A right-to-LEFT line (lon 30 → lon 10): its center tangent points west, so keep-upright flips it.
            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 30.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            LabelInstance Curved(bool keepUpright) => new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                PathRender = path,
                LineAnchors = new[] { AnchorAt(path, 0.5) },
                CurvedGlyphs = glyphs,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                MaxAngleDeg = 45f,
                KeepUpright = keepUpright,
                FeatureIndex = 0,
                TileKey = 0L,
            };

            // Stage AC (curved-world): curved now routes to the world sink — needs a world base material for
            // the world text slot to build+present (mirrors the LineCenter-distribution tooth above).
            var system = new LabelPlacementSystem(mapCamera,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                var uprightLabels = new List<LabelInstance> { Curved(true) };
                system.Tick(in frame, uprightLabels, atlasTexture);
                system.Tick(in frame, uprightLabels, atlasTexture);
                Assert.AreEqual(3, system.LastQuadCount, "keep-upright:true still places all 3 glyphs");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh uprightMesh), "the world text slot must exist.");
                WorldMeshReadback.Read(uprightMesh, out WorldBillboardVertex[] upright, out _);

                var rawLabels = new List<LabelInstance> { Curved(false) };
                system.Tick(in frame, rawLabels, atlasTexture);
                system.Tick(in frame, rawLabels, atlasTexture);
                Assert.AreEqual(3, system.LastQuadCount, "keep-upright:false still places all 3 glyphs");
                Assert.IsTrue(system.TryGetWorldSlotMesh(0L, 0, LabelKind.Text, out Mesh rawMesh), "the world text slot must still exist.");
                WorldMeshReadback.Read(rawMesh, out WorldBillboardVertex[] raw, out _);

                // The flag has a real effect: the reversed arc walk (different world anchor per glyph) + the
                // negated world Tangent lay the label out differently — compare BOTH the new baked fields.
                Assert.AreEqual(upright.Length, raw.Length, "same glyph count → same vertex count");
                bool anyDiff = false;
                for (int i = 0; i < upright.Length && !anyDiff; i++)
                    if (math.length(upright[i].AnchorLocal - raw[i].AnchorLocal) > 0.5f
                        || math.length(upright[i].Tangent - raw[i].Tangent) > 0.1f) anyDiff = true;
                Assert.IsTrue(anyDiff,
                    "keep-upright:true must lay a right-to-left label out differently than keep-upright:false");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        // ── #5 B3 END-TO-END: two OVERLAPPING curved labels (same line, allow-overlap OFF) collide through the
        //    real rotated-glyph box → candidate → SelectSurvivors → emit chain — only the lower-sort-key one
        //    places. This is the decisive proof that curved labels actually collide (every other curved Tick
        //    test is single-label or allow-overlap); it also exercises LabelBox.BuildRotatedGlyph for real. ──
        [Test]
        public void Tick_TwoOverlappingCurvedLabels_OnlyLowerSortKeyPlaces()
        {
            var camGo = new GameObject("CurvedCollide_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(320, 240, 0);
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 20.0, Longitude = 20.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 20.0 }),
                float3x3.identity);
            var atlasTexture = BuildTinyAtlasTexture();

            double3 a = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 10.0 });
            double3 b = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 20.0, Longitude = 30.0 });
            var path = new double3[] { a, b };
            var glyphs = new List<CurvedGlyph> { MakeCurvedGlyph(0f), MakeCurvedGlyph(24f), MakeCurvedGlyph(48f) };

            // Two labels centred on the SAME projected line → their glyph boxes coincide. featureIndex differs so
            // the placement order is total; sort keys pick the winner.
            LabelInstance Curved(int featureIndex, float sortKey, bool allowOverlap) => new LabelInstance
            {
                Placement = SymbolPlacement.LineCenter,
                PathRender = path,
                LineAnchors = new[] { AnchorAt(path, 0.5) },
                CurvedGlyphs = glyphs,
                Paint = LabelPaint.Default,
                TextSizePx = 24f,
                PaddingPx = 2f,
                MaxAngleDeg = 45f,
                KeepUpright = true,
                SortKey = sortKey,
                AllowOverlap = allowOverlap,
                FeatureIndex = featureIndex,
                TileKey = 0L,
            };

            var system = new LabelPlacementSystem(mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            try
            {
                // R3: duplicate each candidate-set's Tick call — the verdict is harvested one Tick late (§2.6).
                // Collision ON: the two coincident labels fight → only the lower-key one survives (3 glyphs).
                var collidingLabels = new List<LabelInstance>
                {
                    Curved(featureIndex: 0, sortKey: 20f, allowOverlap: false),
                    Curved(featureIndex: 1, sortKey: 10f, allowOverlap: false),
                };
                system.Tick(in frame, collidingLabels, atlasTexture);
                system.Tick(in frame, collidingLabels, atlasTexture);
                Assert.AreEqual(glyphs.Count, system.LastQuadCount,
                    "two overlapping curved labels collide — only the lower-sort-key one places (not both)");

                // Control: allow-overlap ON → both place (6 glyphs), proving the drop above was collision, not a
                // spurious single-label limit.
                var overlapAllowedLabels = new List<LabelInstance>
                {
                    Curved(featureIndex: 0, sortKey: 20f, allowOverlap: true),
                    Curved(featureIndex: 1, sortKey: 10f, allowOverlap: true),
                };
                system.Tick(in frame, overlapAllowedLabels, atlasTexture);
                system.Tick(in frame, overlapAllowedLabels, atlasTexture);
                Assert.AreEqual(glyphs.Count * 2, system.LastQuadCount,
                    "with allow-overlap both coincident curved labels place — so the collision was real above");
            }
            finally
            {
                system.Dispose();
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }
    }
}
