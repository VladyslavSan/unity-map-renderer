// Unity EditMode only — needs a real Camera/MapView (BuildSceneFrame) + Mesh (reads billboard vertex alpha).
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
    /// S3: the globe far-side horizon cull as a <c>GatherSymbolPoints</c> fade trigger (peer of the tile/
    /// distance/departing culls) — a two-sided EditMode proof over a REAL <see cref="SphericalProjection"/>
    /// <see cref="MapCamera"/>. Teeth:
    /// <list type="bullet">
    ///   <item>(a) a FRESH (never-seen) far-side anchor is hard-skipped and produces no collision candidate
    ///     — a simple antipode sanity check, heading-independent (see its own header for why);</item>
    ///   <item>(b) a PREVIOUSLY-VISIBLE anchor that rotates behind the horizon EASES OUT (stays staged, fading,
    ///     its fade id force-faded) — never pops;</item>
    ///   <item>(c) the frame-consistency regression pin: a FIXED off-axis (oblique-bearing) anchor's horizon-cull
    ///     firing over THREE headings must match the normal {1,1,0} pattern — an East↔North swap OR a px/pz
    ///     sign flip between <c>ComputeRelativePose</c> and <c>TangentBasisAt</c> changes at least one entry
    ///     (numeric coverage table in the method's header), as does the East/North-dropped degeneracy; an
    ///     antipode can't move at all, so it can't stand in for this
    ///     — <see cref="OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes"/>;</item>
    ///   <item>Mercator is byte-identical: <see cref="IProjection.TryGetHorizonOccluder"/> returns false, so
    ///     <c>globeRadiusSq &lt; 0</c> makes the trigger an unconditional no-op (proven by every unmoved
    ///     Mercator symbol snapshot elsewhere — nothing to re-prove here).</item>
    /// </list>
    ///
    /// <para><b>Footgun avoided (Stage-U carry-over both reviewers flagged):</b> the harness builds its
    /// <see cref="SceneFrame"/> via <see cref="MapView.BuildSceneFrame"/> — the REAL 3-arg path wired off the
    /// live <see cref="MapCamera.CameraRelativePosition"/> — NOT the 2-arg ctor / <c>SceneFrame.Mercator</c>,
    /// which defaults <c>CameraRelativePosition</c> to <c>(0,0,0)</c> (camera at the sphere centre) and would
    /// silently misfire the cull.</para>
    /// </summary>
    [TestFixture]
    public class HorizonCullGatherTests
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

        private static List<SymbolQuad> OneQuad() => new List<SymbolQuad>
        {
            new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
            },
        };

        private static void AddPoint(SymbolTileBuffer buffer, double3 anchor, string text, int feature)
            => TestSymbolTileBuffer.AddPoint(buffer, anchor, OneQuad(), float2.zero, new float2(18f, 18f),
                paint: SymbolPaint.Default, textSizePx: 24f, paddingPx: 2f, sortKey: 0f, text: text,
                featureIndex: feature, tileKey: 0L);

        // Epic A / A1: point symbols draw through the WORLD path now — see SymbolFadeTests.MaxAlpha's identical
        // header for the full rationale (fade opacity rides the world slot's stream-1 Opacity, not
        // system.Mesh's vertex-colour alpha).
        private static float MaxAlpha(SymbolPlacementSystem system, long tileKey = 0L)
            => system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh) ? WorldMeshReadback.MaxOpacity(mesh) : 0f;

        /// <summary>Great-circle destination point from the equator/prime-meridian (0,0) — the fixed look-at
        /// every test in this fixture uses — at compass <paramref name="bearingDeg"/> (CW from north) and
        /// angular <paramref name="distanceDeg"/>. Standard destination-point formula specialized to lat0=0:
        /// <c>lat = asin(sin(d)·cos(b))</c>, <c>lon = atan2(sin(b)·sin(d), cos(d))</c>.</summary>
        private static GeoCoordinate Destination(double bearingDeg, double distanceDeg)
        {
            double bearing  = bearingDeg   * math.PI_DBL / 180.0;
            double distance = distanceDeg  * math.PI_DBL / 180.0;
            double lat = math.asin(math.sin(distance) * math.cos(bearing));
            double lon = math.atan2(math.sin(bearing) * math.sin(distance), math.cos(distance));
            return new GeoCoordinate { Latitude = lat * 180.0 / math.PI_DBL, Longitude = lon * 180.0 / math.PI_DBL };
        }

        private sealed class Harness : System.IDisposable
        {
            public readonly SymbolPlacementSystem System;
            public readonly MapCamera Camera;
            public readonly MapView View;
            public readonly GlyphAtlasTexture Atlas;
            private readonly GameObject _rootGo, _camGo;

            public Harness(CameraProperties initial)
            {
                _rootGo = new GameObject("HorizonCull_TestMapView");
                var component = _rootGo.AddComponent<MapViewComponent>();

                _camGo = new GameObject("HorizonCull_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);

                Camera = new MapCamera(uCam, initial, projection: new SphericalProjection());
                component.SetCamera(Camera);
                View = component.View;

                Atlas = BuildTinyAtlasTexture();
                // Epic A / A1: point symbols now draw through the world path — the demo tick needs its own
                // world base material for a live opacity read (see MaxAlpha's header).
                System = new SymbolPlacementSystem(Camera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            /// <summary>The REAL 3-arg <see cref="MapView.BuildSceneFrame"/> path — see the class header's
            /// "footgun avoided" note. Re-read every call: <see cref="SetProperties"/> changes it.</summary>
            public SceneFrame Frame() => View.BuildSceneFrame(Camera.CurrentProperties);

            /// <summary>Mirrors <c>MapView.LateUpdate</c>'s load-bearing order: <c>SyncToCamera</c> BEFORE
            /// <c>BuildSceneFrame</c>, so <see cref="MapCamera.CameraRelativePosition"/> is fresh when
            /// <see cref="Frame"/> is next called.</summary>
            public void SetProperties(CameraProperties props)
            {
                Camera.SetProperties(props);
                Camera.SyncToCamera();
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_rootGo);
                Object.DestroyImmediate(_camGo);
            }
        }

        /// <summary>Simple far-side sanity check — NOT a frame-consistency pin (see
        /// <see cref="OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes"/> for that). The
        /// antipode of the look-at rebases to exactly <c>(0,−2R,0)</c> for ANY heading — its render-X and
        /// render-Z land on zero identically, so <c>HorizonCull</c>'s <c>dot(pc,cc)</c> reduces to the Y term
        /// alone (heading never enters it). It still proves the hard-skip mechanics (fresh far anchor ⇒ no
        /// quad, no candidate, attributed to the horizon telemetry bucket), just not the East/North wiring.</summary>
        [Test]
        public void FreshFarSideAnchor_IsHardSkipped_AbsentFromCollision()
        {
            var lookAt = new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 };
            var initial = new CameraProperties(lookAt, zoom: 2.0, heading: 45.0, tilt: 45.0);
            using var h = new Harness(initial);

            IProjection proj = h.Camera.Projection;
            double3 farAnchor = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 180.0 }); // antipode

            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, farAnchor, "F", 0);
            SceneFrame frame = h.Frame();
            h.System.TickSymbols(in frame, buffer, h.Atlas, h.Camera.Projection); // fresh — never seen, no live fade to ease out

            Assert.AreEqual(0, h.System.LastQuadCount, "a fresh far-side anchor produces no geometry (hard-skip)");
            Assert.AreEqual(0, h.System.LastCandidateCount, "…and never enters the collision pass");
            Assert.AreEqual(1, h.System.LastHorizonCulledCount, "…attributed to the S3 horizon cull");
            Assert.AreEqual(0, h.System.LastDistanceCulledCount, "the B-3 radius must not preempt the horizon trigger here");
        }

        /// <summary>
        /// THE frame-consistency regression pin — a real one: it goes RED under an East↔North axis swap OR a
        /// single-axis (px or pz) sign flip between <c>CameraPoseMath.ComputeRelativePose</c>'s pose and
        /// <c>Ecef.TangentBasis</c>'s East/North columns, not just the gross "East/North dropped" degeneracy.
        ///
        /// <para><b>Why three headings, not the obvious two.</b> For a FIXED anchor (bearing β, arc-distance θ)
        /// and camera at heading H / tilt T, <c>HorizonCull</c>'s dot reduces to
        /// <c>dot(pc,cc) = R·[cosθ·cy − s·sinθ·cos(H−β)]</c> (<c>s = alt·sinT</c>, <c>cy = alt·cosT + R</c>), so
        /// <b>hidden ⟺ cos(H−β) &gt; K</b>, <c>K = (cosθ·cy − R)/(s·sinθ)</c>. Each frame defect rewrites only the
        /// phase/argument: an E↔N swap (either side) → <c>sin(H+β) &gt; K</c>; a px flip → <c>cos(H+β) &gt; K</c>;
        /// a pz flip → <c>cos(H+β) &lt; −K</c>; East/North dropped → constant. Two headings 180° apart CANNOT
        /// separate all of these (the swap and px flip survive that flip — that was an earlier, weaker version of
        /// this test), and β=45° is degenerate (<c>sin(H+45)≡cos(H−45)</c>, so a swap is invisible). An oblique
        /// β plus THREE headings does separate them.</para>
        ///
        /// <para><b>Config &amp; numeric coverage (simulated against the real production formulas; the normal row
        /// also matches the live gate — see <see cref="FreshFarSideAnchor_IsHardSkipped_AbsentFromCollision"/>'s
        /// β=35 datapoint).</b> β=20°, θ=50°, tilt=45°, zoom=2 ⇒ K≈−0.195, R²≈4.07×10¹³. Horizon-fires
        /// (<c>LastHorizonCulledCount</c>) over headings {0°, 90°, 150°}:
        /// <list type="table">
        ///   <item><term>normal  </term><description>{1, 1, 0}  ← asserted; margins dot−R² = −1.6e13 / −7.5e12 / +6.3e12 (all ≥6e12, non-flaky)</description></item>
        ///   <item><term>E↔N swap</term><description>{1, 1, 1}  differs at 150° → RED</description></item>
        ///   <item><term>px flip </term><description>{1, 0, 0}  differs at 90°  → RED</description></item>
        ///   <item><term>pz flip </term><description>{0, 1, 1}  differs at 0°   → RED</description></item>
        ///   <item><term>E/N drop</term><description>{1, 1, 1}  differs at 150° → RED</description></item>
        /// </list>
        /// So every East/North wiring defect changes at least one of the three asserted outcomes.</para>
        ///
        /// <para>The signal is the horizon-cull decision (<c>LastHorizonCulledCount</c>), not a drawn quad: some
        /// configs leave the anchor behind the tilted camera (a separate downstream cull) which would confound a
        /// quad-count assertion. Frames are built through the REAL <see cref="MapView.BuildSceneFrame"/> path so
        /// the live <c>ComputeRelativePose</c>→<c>TangentBasisAt</c> integration is what's under test.</para>
        /// </summary>
        [Test]
        public void OffAxisAnchor_HorizonCullFiresPerHeading_PinsEastNorthAxes()
        {
            var lookAt = new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 };
            const double bearingDeg = 20.0, distanceDeg = 50.0; // oblique β so a swap/px-flip is visible (β≠0,45,90)
            GeoCoordinate anchorGeo = Destination(bearingDeg, distanceDeg);

            // Normal-code horizon-fire pattern {1,1,0} over these headings; ANY East↔North swap or px/pz sign flip
            // changes at least one entry (see the coverage table in the doc). tilt=45° so the camera leans and the
            // fixed off-axis anchor's occlusion is genuinely heading-dependent through px/pz.
            var cases = new (double headingDeg, int expectedHorizonCulled)[] { (0.0, 1), (90.0, 1), (150.0, 0) };
            foreach (var (headingDeg, expected) in cases)
            {
                using var h = new Harness(new CameraProperties(lookAt, zoom: 2.0, heading: headingDeg, tilt: 45.0));
                double3 anchor = h.Camera.Projection.Project(anchorGeo);
                var buffer = new SymbolTileBuffer();
                AddPoint(buffer, anchor, "A", 0);
                SceneFrame frame = h.Frame();
                h.System.TickSymbols(in frame, buffer, h.Atlas, h.Camera.Projection);

                Assert.AreEqual(expected, h.System.LastHorizonCulledCount,
                    $"heading {headingDeg}°: horizon-cull fire must match the normal {{1,1,0}} pattern — a swap or " +
                    "px/pz sign flip in the ComputeRelativePose↔TangentBasis frame changes this (see coverage table)");
            }
        }

        [Test]
        public void PreviouslyVisibleAnchor_RotatedBehindHorizon_EasesOut_NotPops()
        {
            var origin = new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 };
            var initial = new CameraProperties(origin, zoom: 2.0, heading: 0.0, tilt: 0.0);
            using var h = new Harness(initial);

            IProjection proj = h.Camera.Projection;
            // The SAME geo anchor throughout — only the CAMERA orbits (LookAt moves to the antipode), so the
            // fade id (hashed off this fixed render-space anchor) stays stable across the transition.
            double3 anchor = proj.Project(new GeoCoordinate { Latitude = 0.0, Longitude = 0.0 });
            var buffer = new SymbolTileBuffer();
            AddPoint(buffer, anchor, "A", 0);

            // 1) The camera looks straight at the anchor — visible, snaps to full opacity (default deltaTime).
            // R3: duplicate — the collision verdict is harvested one Tick late (§2.6).
            SceneFrame frame1 = h.Frame();
            h.System.TickSymbols(in frame1, buffer, h.Atlas, h.Camera.Projection);
            h.System.TickSymbols(in frame1, buffer, h.Atlas, h.Camera.Projection);
            Assert.AreEqual(1, h.System.LastQuadCount, "the anchor places while the camera looks at it");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…at full opacity");

            // 2) The camera rotates to look at the ANCHOR'S ANTIPODE — the same anchor is now on the far side.
            //    It must keep drawing while it fades, not vanish for a frame.
            var rotated = new GeoCoordinate3D { Latitude = 0.0, Longitude = 180.0, Altitude = 0.0 };
            h.SetProperties(new CameraProperties(rotated, zoom: 2.0, heading: 0.0, tilt: 0.0));
            SceneFrame frame2 = h.Frame();
            h.System.TickSymbols(in frame2, buffer, h.Atlas, h.Camera.Projection, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a horizon-occluded-but-visible anchor keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is finally dropped — attributed to horizon telemetry.
            for (int i = 0; i < 10; i++)
            {
                SceneFrame frame3 = h.Frame();
                h.System.TickSymbols(in frame3, buffer, h.Atlas, h.Camera.Projection, deltaTime: 0.1f);
            }
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the horizon-occluded anchor is fully skipped");
            Assert.Greater(h.System.LastHorizonCulledCount, 0, "…and its skip is attributed to horizon telemetry");
        }
    }
}
