// Unity EditMode only — needs a real Camera/Mesh/Material (reads the built billboard mesh's vertex alpha).
// NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
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
    /// A-4: the placement layer is a fade state machine. A label eases in/out (its opacity, carried on
    /// <c>PlacedQuad.Color.w</c> → the billboard vertex alpha) instead of popping. Teeth: the default (infinite)
    /// deltaTime snaps to full opacity (byte-parity with pre-A-4); a real small deltaTime fades IN over frames;
    /// a collision-suppressed label fades OUT while still drawn (both visible mid-transition); a stable label
    /// keeps its opacity across frames (no per-frame re-fade — the anti-blink property); and the point fade id
    /// is a FIXED-grid identity (independent of camera zoom, unlike the A-3 display-zoom dedup key).
    /// </summary>
    [TestFixture]
    public class LabelFadeTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static TextLayoutResult OneQuad() => new TextLayoutResult
        {
            Quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            },
            BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1,
        };

        private static LabelInstance Point(double3 anchor, float sortKey, string text, int feature)
            => new LabelInstance
            {
                AnchorRender = anchor, Placement = SymbolPlacement.Point, Layout = OneQuad(), Paint = LabelPaint.Default,
                TextSizePx = 24f, PaddingPx = 2f, SortKey = sortKey, Text = text, FeatureIndex = feature, TileKey = 0L,
            };

        private static float MaxAlpha(Mesh mesh)
        {
            float a = 0f;
            foreach (Color c in mesh.colors) a = math.max(a, c.a);
            return a;
        }

        private sealed class Harness : System.IDisposable
        {
            public readonly LabelPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("LabelFade_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Frame = new SceneFrame(Origin, float3x3.identity);
                Atlas = BuildTinyAtlasTexture();
                System = new LabelPlacementSystem(cam, new Material(Shader.Find("Map/Symbol/Text")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        // ── The default (infinite) deltaTime SNAPS to full opacity — a single-Tick test renders labels exactly
        //    as before A-4 (byte-parity). ──
        [Test]
        public void Tick_DefaultDeltaTime_SnapsToFullOpacity()
        {
            using var h = new Harness();
            var label = Point(h.Origin, 0f, "A", 0);
            h.System.Tick(in h.Frame, new List<LabelInstance> { label }, h.Atlas); // default deltaTime = +inf
            Assert.AreEqual(1, h.System.LastQuadCount, "the label places");
            Assert.Greater(MaxAlpha(h.System.Mesh), 0.99f, "default deltaTime snaps the fade to full opacity");
        }

        // ── A real small deltaTime fades the label IN over successive frames (alpha rises 0 → 1). ──
        [Test]
        public void Tick_SmallDeltaTime_FadesInOverFrames()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            h.System.Tick(in h.Frame, labels, h.Atlas, deltaTime: 0.05f);
            float first = MaxAlpha(h.System.Mesh);
            Assert.Greater(first, 0f, "it has started to appear");
            Assert.Less(first, 0.5f, "…but is only partway in after one 0.05s step (fade ≈ 0.3s)");

            for (int i = 0; i < 20; i++) h.System.Tick(in h.Frame, labels, h.Atlas, deltaTime: 0.05f);
            Assert.Greater(MaxAlpha(h.System.Mesh), 0.99f, "after > 0.3s of steps it is fully faded in");
        }

        // ── A stable label keeps its opacity across frames — it does NOT re-fade every frame (the persistent
        //    record is the anti-blink property; a one-frame flip becomes a sub-perceptual alpha step, not a pop). ──
        [Test]
        public void Tick_StableLabel_KeepsOpacity_NoReFade()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };
            h.System.Tick(in h.Frame, labels, h.Atlas); // snap to full
            h.System.Tick(in h.Frame, labels, h.Atlas, deltaTime: 0.05f); // same id again, small step
            Assert.Greater(MaxAlpha(h.System.Mesh), 0.99f, "a persistent label stays at full opacity — no re-fade");
        }

        // ── A collision-suppressed label fades OUT while still drawn: mid-transition BOTH the fading-out loser
        //    and the fading-in winner are emitted (2 quads), then it settles to just the winner (1). Pre-A-4 the
        //    loser popped instantly (1 quad throughout). ──
        [Test]
        public void Tick_CollisionSuppression_FadesOutWhileStillDrawn()
        {
            using var h = new Harness();
            // A wins alone first (lower sort key = higher priority). Then B (higher priority) appears on the SAME
            // anchor → A is suppressed and must fade out, not vanish.
            var aOnly = new List<LabelInstance> { Point(h.Origin, 10f, "A", 0) };
            var both = new List<LabelInstance>
            {
                Point(h.Origin, 10f, "A", 0),  // was placed → now the loser (fades out)
                Point(h.Origin, 5f, "B", 1),   // higher priority → the winner (fades in)
            };

            h.System.Tick(in h.Frame, aOnly, h.Atlas); // A at full opacity
            Assert.AreEqual(1, h.System.LastQuadCount);

            h.System.Tick(in h.Frame, both, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(2, h.System.LastQuadCount, "mid-transition both draw: A fading out + B fading in");

            for (int i = 0; i < 10; i++) h.System.Tick(in h.Frame, both, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "settled: only the winner B remains, A has faded to 0");
        }

        // ── B-3: a label far past the horizon radius is culled BEFORE projection/collision; the near label
        //    still places. (The Core radius math is pinned in LabelViewDistanceTests; this proves the wiring.) ──
        [Test]
        public void Tick_FarLabel_IsDistanceCulled_WhileNearLabelPlaces()
        {
            using var h = new Harness();
            var near = Point(h.Origin, 0f, "N", 0);
            var far = Point(h.Origin + new double3(1e8, 0, 1e8), 0f, "F", 1); // far past any horizon radius
            h.System.Tick(in h.Frame, new List<LabelInstance> { near, far }, h.Atlas);
            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "the far label is skipped pre-projection");
            Assert.AreEqual(1, h.System.LastQuadCount, "only the near label places");
        }

        // ── A label whose TILE gets coverage-culled while the label itself is still on screen must FADE OUT in
        //    place, not pop. The pre-cull produces no geometry, so before the soft-cull fix the fade record decayed
        //    silently and the label vanished for a frame. Now the gather cull keeps staging a still-visible label
        //    (forcing its fade to 0) until it has faded, then finally skips it. ──
        [Test]
        public void Tick_TileCoverageCulled_FadesOut_InsteadOfPopping()
        {
            using var h = new Harness();
            // A high-zoom tile at the look-at: tiny on screen (well under any coverage threshold) yet in front of
            // the camera, so the tile-coverage cull fires while the label's own anchor is still centre-screen.
            long tileKey = SymbolFeatureExtractor.PackTileKey(new TileId { Z = 14, X = 8647, Y = 7735 });
            var labels = new List<LabelInstance>
            {
                new LabelInstance
                {
                    AnchorRender = h.Origin, Placement = SymbolPlacement.Point, Layout = OneQuad(),
                    Paint = LabelPaint.Default, TextSizePx = 24f, PaddingPx = 2f, SortKey = 0f, Text = "A",
                    FeatureIndex = 0, TileKey = tileKey,
                },
            };

            // 1) Cull disabled → the label places and snaps to full opacity.
            h.System.MinTileScreenCoverage = 0.0;
            h.System.Tick(in h.Frame, labels, h.Atlas); // default dt → snap to full
            Assert.AreEqual(1, h.System.LastQuadCount, "the label places with the cull disabled");
            Assert.Greater(MaxAlpha(h.System.Mesh), 0.99f, "…at full opacity");

            // 2) Enable the cull → the tiny tile is culled, but the on-screen label must FADE (still drawn, dimmer),
            //    not disappear for a frame.
            h.System.MinTileScreenCoverage = 0.05;
            h.System.Tick(in h.Frame, labels, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a culled-but-visible label keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System.Mesh);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is finally dropped (the cull's perf win applies once invisible).
            for (int i = 0; i < 10; i++) h.System.Tick(in h.Frame, labels, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the culled label is fully skipped");
        }

        // ── Retain-as-departing: when a tile leaves cover its labels are flagged DEPARTING (RecordDeparting, set
        //    from CollectInto's active/departing split) so they FADE OUT in place instead of popping — the tile-
        //    UNLOAD analogue of the coverage-cull fade. Simulated here by building the batch with activeCount: the
        //    same label is active first, then departing (activeCount excludes it). ──
        [Test]
        public void Tick_DepartingRecord_FadesOut_InsteadOfPopping()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, sortKey: 0f, text: "A", feature: 0) };

            // 1) Active (activeCount covers the label) → it places and snaps to full opacity.
            var active = new SymbolLabelBatch();
            SymbolLabelBatchBuilder.Build(active, labels, slotCount: 1, projection: null, activeCount: 1);
            h.System.Tick(in h.Frame, active, h.Atlas); // default dt → snap to full
            Assert.AreEqual(1, h.System.LastQuadCount, "the active label places");
            Assert.Greater(MaxAlpha(h.System.Mesh), 0.99f, "…at full opacity");

            // 2) The tile leaves cover → the SAME label is now departing (activeCount: 0 flags every record). It
            //    must keep drawing while it fades, not vanish for a frame.
            var departing = new SymbolLabelBatch();
            SymbolLabelBatchBuilder.Build(departing, labels, slotCount: 1, projection: null, activeCount: 0);
            h.System.Tick(in h.Frame, departing, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a departing-but-visible label keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System.Mesh);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is dropped — counted as departing (not coverage/distance).
            for (int i = 0; i < 10; i++) h.System.Tick(in h.Frame, departing, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the departing label is fully skipped");
            Assert.Greater(h.System.LastDepartingCulledCount, 0, "…and its skip is attributed to departing telemetry");
        }

        // ── The point fade id is a FIXED-grid identity: it collapses anchors within a few metres (a cross-tile
        //    no-op) and separates distinct ones — and it takes NO zoom parameter, so it cannot drift as the
        //    camera zooms (the bug a per-frame display-zoom grid would cause). ──
        [Test]
        public void PointFadeId_FixedGrid_CollapsesNearby_SeparatesFar()
        {
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);
            Assert.AreEqual(LabelPlacementSystem.PointFadeId(a, 0, "T"),
                            LabelPlacementSystem.PointFadeId(a + new double3(2, 0, -2), 0, "T"),
                            "anchors within the fixed fade grid share one identity (the cross-tile no-op)");
            Assert.AreNotEqual(LabelPlacementSystem.PointFadeId(a, 0, "T"),
                               LabelPlacementSystem.PointFadeId(a + new double3(50, 0, 0), 0, "T"),
                               "anchors many metres apart are distinct labels");
            Assert.AreNotEqual(LabelPlacementSystem.PointFadeId(a, 0, "T"),
                               LabelPlacementSystem.PointFadeId(a, 0, "U"),
                               "different text is a different label even at the same anchor");
        }

        // ── I6: the icon analogue — two co-located icon labels (text=null, distinct icon-image) must get
        //    DISTINCT fade ids (pre-fix they'd collide: text==null for both). Same icon-image at the same
        //    anchor shares an id (the seamless no-op icons now get too). A text label's id is unchanged when
        //    iconImage is omitted/explicitly-null (the #1 invariant: guard-skip, not `?? 0`). ──
        [Test]
        public void PointFadeId_IconIdentity_DistinctIconsSeparate_SameIconShares_TextUnaffected()
        {
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);

            Assert.AreNotEqual(LabelPlacementSystem.PointFadeId(a, 0, null, "a"),
                                LabelPlacementSystem.PointFadeId(a, 0, null, "b"),
                                "same cell/layer, text=null, different icon-image → distinct fade ids");

            Assert.AreEqual(LabelPlacementSystem.PointFadeId(a, 0, null, "a"),
                             LabelPlacementSystem.PointFadeId(a, 0, null, "a"),
                             "the same icon-image at the same anchor shares one fade id");

            Assert.AreEqual(LabelPlacementSystem.PointFadeId(a, 0, "Paris"),
                             LabelPlacementSystem.PointFadeId(a, 0, "Paris", null),
                             "a text label's fade id is unchanged whether iconImage is omitted or explicitly null");
        }
    }
}
