// Unity EditMode only — needs a real Camera/Mesh/Material (asserts on the built billboard mesh + the skip
// engaging). NOT registered in core-tests.csproj (the skip is an engine-integration behaviour, not Core logic).

using System.Collections.Generic;
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
    /// B-1: the static-frame skip. When the collected label set (<c>labelSetVersion</c>), the committed camera,
    /// and the fade state are ALL unchanged since the last real build, <see cref="LabelPlacementSystem.Tick"/>
    /// re-submits the cached per-slot meshes instead of re-projecting — killing the idle Project/Collide cost and
    /// the per-frame-rebuild churn. Teeth (per the B-1 design): the skip actually FIRES (an un-fired skip is
    /// silent dead code — <see cref="LabelPlacementSystem.LastTickSkipped"/> proves it); a forced rebuild of the
    /// same static frame is BYTE-IDENTICAL to what the skip re-submits (so skipping hides no corruption); and each
    /// dimension breaks the skip INDIVIDUALLY (version bump, camera nudge, fade mid-transition) — the last one
    /// keeps rebuilding until the fade settles, then re-skips (no stuck state). The demo/test sentinel version
    /// never skips (byte-parity with pre-B-1).
    /// </summary>
    [TestFixture]
    public class LabelStaticSkipTests
    {
        private const long V = 1L; // a real (non-sentinel) collected-set version

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

        private sealed class Harness : System.IDisposable
        {
            public readonly LabelPlacementSystem System;
            public readonly MapCamera Camera;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("LabelStaticSkip_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                Camera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = Camera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Frame = new SceneFrame(Origin, float3x3.identity);
                Atlas = BuildTinyAtlasTexture();
                System = new LabelPlacementSystem(Camera, new Material(Shader.Find("Map/Symbol/Text")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        // Tick with an explicit (non-sentinel) version — the production skip-eligible path.
        private static void Tick(Harness h, List<LabelInstance> labels, long version, float dt = float.PositiveInfinity)
            => h.System.Tick(in h.Frame, labels, h.Atlas, deltaTime: dt, materials: null, labelSetVersion: version);

        // A flat snapshot of the built billboard geometry — the "bytes" the skip must not change vs a rebuild.
        private static (Vector3[] v, int[] t, Color[] c, Vector2[] uv) Snapshot(Mesh m)
            => (m.vertices, m.triangles, m.colors, m.uv);

        private static void AssertMeshEqual((Vector3[] v, int[] t, Color[] c, Vector2[] uv) a,
                                            (Vector3[] v, int[] t, Color[] c, Vector2[] uv) b, string what)
        {
            Assert.AreEqual(a.v, b.v, $"{what}: vertex positions differ");
            Assert.AreEqual(a.t, b.t, $"{what}: triangle indices differ");
            Assert.AreEqual(a.c, b.c, $"{what}: vertex colors differ");
            Assert.AreEqual(a.uv, b.uv, $"{what}: UVs differ");
        }

        // ── The skip FIRES on an unchanged static frame — and the labels stay drawn (LastQuadCount preserved). ──
        [Test]
        public void Tick_StaticFrame_TakesSkip_AndKeepsDrawing()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            Tick(h, labels, V); // first: real build, fades snapped (default +inf), cache recorded
            Assert.IsFalse(h.System.LastTickSkipped, "the first Tick must build (no cache yet)");
            Assert.AreEqual(1, h.System.LastQuadCount);

            Tick(h, labels, V); // identical set + camera + settled fades → skip
            Assert.IsTrue(h.System.LastTickSkipped, "an unchanged static frame re-submits the cached meshes");
            Assert.AreEqual(1, h.System.LastQuadCount, "the label is still drawn on a skip (count preserved)");
        }

        // ── A forced rebuild of the SAME static frame is BYTE-IDENTICAL to what the skip re-submits — proving the
        //    skip can't hide corruption ("still drawn" would not). A version bump forces the rebuild. ──
        [Test]
        public void Tick_ForcedRebuild_IsByteIdentical_ToCachedFrame()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            Tick(h, labels, V);
            var built = Snapshot(h.System.Mesh);

            Tick(h, labels, V);
            Assert.IsTrue(h.System.LastTickSkipped, "the second identical frame skips");
            AssertMeshEqual(built, Snapshot(h.System.Mesh), "skip must leave the cached mesh untouched");

            Tick(h, labels, V + 1); // different version → forced rebuild of the identical geometry
            Assert.IsFalse(h.System.LastTickSkipped, "a version bump forces a rebuild");
            AssertMeshEqual(built, Snapshot(h.System.Mesh), "the rebuild the skip elides is byte-identical");
        }

        // ── A version bump (the collected label set changed) breaks the skip. ──
        [Test]
        public void Tick_VersionBump_BreaksSkip()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            Tick(h, labels, V);
            Tick(h, labels, V);
            Assert.IsTrue(h.System.LastTickSkipped, "static frame skips");

            Tick(h, labels, V + 1);
            Assert.IsFalse(h.System.LastTickSkipped, "a bumped version rebuilds");
        }

        // ── Nudging one committed-camera field breaks the skip (keyed on CameraProperties, not the viewProj). ──
        [Test]
        public void Tick_CameraNudge_BreaksSkip()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            Tick(h, labels, V);
            Tick(h, labels, V);
            Assert.IsTrue(h.System.LastTickSkipped, "static frame skips");

            var moved = new CameraProperties(
                new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.5, heading: 0.0, tilt: 0.0);
            h.Camera.SetProperties(moved);
            h.Camera.SyncToCamera();

            Tick(h, labels, V);
            Assert.IsFalse(h.System.LastTickSkipped, "a moved camera rebuilds even at the same version");
        }

        // ── A shifted floating-origin scene frame breaks the skip, even at the same version + camera. The frame
        //    is the one build input Tick does not read off the camera (MapView passes it in), so it is keyed
        //    directly — a rebased origin re-projects every anchor and must force a rebuild. ──
        [Test]
        public void Tick_SceneOriginShift_BreaksSkip()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            Tick(h, labels, V);
            Tick(h, labels, V);
            Assert.IsTrue(h.System.LastTickSkipped, "static frame skips");

            var shifted = new SceneFrame(h.Origin + new double3(1000.0, 0.0, 1000.0), float3x3.identity);
            h.System.Tick(in shifted, labels, h.Atlas, deltaTime: float.PositiveInfinity, materials: null, labelSetVersion: V);
            Assert.IsFalse(h.System.LastTickSkipped, "a shifted scene frame rebuilds at the same version + camera");
        }

        // ── A fade in progress breaks the skip and KEEPS rebuilding until it settles — then re-skips (no stuck
        //    state). Drive the fade with a small deltaTime so it eases in over several frames. ──
        [Test]
        public void Tick_FadeInProgress_BreaksSkip_ThenReSkipsWhenSettled()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            Tick(h, labels, V, dt: 0.05f); // first build; the new label starts fading IN (opacity < 1)
            Tick(h, labels, V, dt: 0.05f); // same set+camera, but fade still animating → must NOT skip
            Assert.IsFalse(h.System.LastTickSkipped, "a fade mid-transition keeps rebuilding (mesh alpha changing)");

            // Ease the fade to completion (fade ≈ 0.3s), each frame still rebuilding.
            for (int i = 0; i < 20; i++) Tick(h, labels, V, dt: 0.05f);

            Tick(h, labels, V, dt: 0.05f);
            Assert.IsTrue(h.System.LastTickSkipped, "once the fade settles the static frame re-skips");
        }

        // ── The demo/test sentinel version NEVER skips — byte-parity with the pre-B-1 always-rebuild path. ──
        [Test]
        public void Tick_SentinelVersion_NeverSkips()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            // The 5-arg overload (no version) is the demo path → sentinel.
            h.System.Tick(in h.Frame, labels, h.Atlas);
            h.System.Tick(in h.Frame, labels, h.Atlas);
            Assert.IsFalse(h.System.LastTickSkipped, "the sentinel version rebuilds every frame");
        }
    }
}
