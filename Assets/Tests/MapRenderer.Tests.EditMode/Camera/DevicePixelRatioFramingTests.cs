// Unity EditMode only — the device→logical conversion has ONE home, and the tile-cover
// framing viewport is the SAME definition the camera altitude frames from.
//
// Teeth covered:
//   T3-3 (MapView leg) — a non-positive DevicePixelRatio yields a FINITE framing viewport (the stage's one
//        named behaviour change). Observed on BuildTileSelectionConfig() DIRECTLY, never through
//        LateUpdate/Tick: with an unguarded ratio the infinite viewport NaN-poisons the frustum planes so
//        every tile intersects while the LOD ratio collapses to 0, and the planar cover then enumerates the
//        whole quadtree — the RED would arrive as a gate timeout instead of a failure.
//   T3-4 — at dpr 2 the selector's framing viewport IS MapCamera.ViewportLogicalPx, exactly. The only
//        tile-selection coverage at dpr ≠ 1 anywhere in the suite. It pins that LateUpdate refreshes the
//        camera's ratio AT ALL, but NOT the ordering of the refresh against the build — see the method's
//        own doc. The ordering is held by the code shape, not by this tooth.
//   T3-6 — the implausible-ratio fallback exists in exactly one production file. The guard is a
//        plausibility band, so the sweep predicate matches that band.

using System.Collections.Generic;
using MapRenderer.App;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class DevicePixelRatioFramingTests : BaseTestFixture
    {
        // A deterministic, EVEN square viewport: the exact-equality legs below compare against vp/2, so the
        // framebuffer size is pinned here rather than inherited from the helper's default.
        private const int TestViewportPx = 1080;

        // ── T3-4 — one definition, shared by the altitude and the cover framing ─────────────────

        /// <summary>
        /// T3-4: the tile selector's framing viewport and the camera's altitude framing must be the SAME
        /// quantity — <see cref="MapRenderer.Unity.Rendering.Map.MapCamera.ViewportLogicalPx"/> — so render
        /// framing and selection framing cannot diverge (the "render far == selection far" invariant).
        ///
        /// <para>This is the only place the selection path runs at a ratio other than 1 anywhere in the suite.</para>
        ///
        /// <para><b>What this does NOT pin:</b> <c>MapView.LateUpdate</c>'s internal refresh-before-build
        /// ordering. This test builds the selector config <i>itself</i>, after <c>LateUpdate</c> has returned,
        /// so the camera already carries the config's ratio however the two steps inside were ordered.
        /// Reordering them leaves this test green. The ordering is instead safe by construction —
        /// <c>BuildTileSelectionConfig</c> has a single production caller and nothing between the refresh and
        /// the consume mutates the ratio — and that argument, not this tooth, is what holds it.</para>
        /// </summary>
        [Test]
        public void FramingViewport_IsTheCameraLogicalViewport_AtDpr2()
        {
            var go   = Track(new GameObject("MapView_S108_Framing"));
            var view = go.AddComponent<MapView>();
            {
                view.WithTestCamera(TestViewportPx);
                view.Config.DevicePixelRatio = 2.0;

                view.LateUpdate();

                double2 framing = view.View.BuildTileSelectionConfig().FramingViewportPx;

                Assert.AreEqual(view.Camera.ViewportLogicalPx.x, framing.x, 0.0,
                    "the selector must frame from the SAME logical viewport the altitude frames from — two " +
                    "independent derivations of one quantity are what let render and selection framing drift.");
                Assert.AreEqual(view.Camera.ViewportLogicalPx.y, framing.y, 0.0,
                    "the selector must frame from the SAME logical viewport the altitude frames from.");

                Assert.AreEqual(TestViewportPx / 2.0, framing.x, 0.0,
                    "at dpr 2 the framing viewport is exactly half the physical one — if this is the full " +
                    "width, LateUpdate never refreshed the camera's ratio from the config at all. (A refresh " +
                    "that merely happens LATER than the build inside LateUpdate would NOT be caught here: " +
                    "this test builds the config itself, afterwards.)");
                Assert.AreEqual(TestViewportPx / 2.0, framing.y, 0.0,
                    "at dpr 2 the framing viewport is exactly half the physical one.");
            }
        }

        // ── T3-3 (MapView leg) — the ONE named behaviour change ─────────────────────────────────

        /// <summary>
        /// T3-3: a non-positive <c>DevicePixelRatio</c> must produce a FINITE framing viewport (the dpr-1
        /// fallback), not <c>+∞</c>. Reachable through <c>MapViewConfig.DevicePixelRatio</c>, a plain
        /// serialized field nothing validates.
        ///
        /// <para><b>Does not call <c>LateUpdate</c>.</b> With an unguarded ratio the framing
        /// viewport is <c>+∞</c>, which survives the selector's <c>vp &lt;= 0</c> early-out, drives the
        /// altitude to <c>+∞</c> and the pose to NaN — every frustum-plane test against NaN is false, so
        /// <c>IntersectsAabb</c> accepts every tile, while <c>lodRatio = finite/∞ = 0</c> stops the LOD from
        /// ever terminating the descent. On a planar projection the cover then enumerates the full quadtree;
        /// on the globe it culls everything and renders blank. Observing the config builder directly makes
        /// this tooth fail in milliseconds instead of hanging the gate.</para>
        /// </summary>
        [Test]
        public void FramingViewport_IsFinite_AtNonPositiveDevicePixelRatio()
        {
            var go   = Track(new GameObject("MapView_S108_FramingGuard"));
            var view = go.AddComponent<MapView>();
            {
                view.WithTestCamera(TestViewportPx);

                foreach (double ratio in new[] { 0.0, -2.0 })
                {
                    // BOTH sources, standing in for the LateUpdate refresh this test must not run: the camera
                    // is where the ratio is read from after the stage, the config is where it was read from
                    // before it. Seeding only one would let the tooth pass without the guard ever running.
                    view.Config.DevicePixelRatio  = ratio;
                    view.Camera.DevicePixelRatio  = ratio;

                    double2 framing = view.View.BuildTileSelectionConfig().FramingViewportPx;

                    Assert.IsFalse(double.IsInfinity(framing.x) || double.IsNaN(framing.x)
                                || double.IsInfinity(framing.y) || double.IsNaN(framing.y),
                        $"a ratio of {ratio} must degrade to 1, not send the framing viewport to infinity — " +
                        "an infinite viewport NaN-poisons the frustum planes and the cover stops pruning.");
                    Assert.AreEqual((double)TestViewportPx, framing.x, 0.0,
                        $"a ratio of {ratio} frames from the physical viewport unchanged.");
                    Assert.AreEqual((double)TestViewportPx, framing.y, 0.0,
                        $"a ratio of {ratio} frames from the physical viewport unchanged.");
                }
            }
        }

        // ── T3-6 — the implausible-ratio fallback has exactly one home ──────────────────────────

        /// <summary>
        /// T3-6: "the ratio is applied ONCE, at the conversion", made structural for the device→logical
        /// direction. Written out by hand the fallback had diverged three ways across four production files,
        /// and two of them carried none at all. Sweeps production sources for a
        /// ratio guard and requires the only match to be <c>DeviceScaling.cs</c>.
        ///
        /// <para>The predicate is narrow (the ratio's name, then a comparison against a number or
        /// a named bound): a bare <c>&gt; 0.0 ? … : 1.0</c> sweep matches four unrelated production sites that
        /// have nothing to do with display density. The sweep covers production only: the test tree now lives
        /// outside <c>Assets/Code</c>, so it needs no exclusion and legitimately quotes the guard when
        /// explaining it. The sweep reads comments too, so
        /// production prose in <b>any</b> swept file must never put the ratio's name next to a comparison
        /// operator — say "outside the plausible band", never the expression itself. That fence binds
        /// <c>MapViewConfig</c>'s tooltip and <c>MapHost</c>'s comment as much as it binds
        /// <c>DeviceScaling</c>'s own doc.</para>
        ///
        /// <para>The operator must be ADJACENT to the ratio's name. A span-to-semicolon form
        /// (<c>devicepixelratio[^;]*&gt;\s*0</c>) matches at <c>DevicePixelRatioFromDpi(double screenDpi)</c>
        /// and lands on the unrelated <c>Debug.Assert(screenDpi &gt; 0.0)</c> below it, so its positive leg
        /// passes even with <c>SafeRatio</c>'s guard deleted. The operator set is wide
        /// (<c>&lt;</c>/<c>&lt;=</c>/<c>&gt;=</c> count, so a hand-written <c>… &lt;= 0.0</c> copy is caught).
        /// <c>IgnoreCase</c> is ABSENT: with it, the <c>[0-9A-Z]</c> tail starts matching the <c>s</c> of a
        /// following <c>&lt;see cref=…</c>, making the sweep sensitive to prose that names the ratio near an
        /// XML tag.</para>
        /// </summary>
        [Test]
        public void RatioFallback_HasExactlyOneHome_InDeviceScaling()
        {
            string root       = Directory.GetParent(Application.dataPath)!.FullName;
            string codeRoot   = Path.Combine(root, "Assets", "Code");
            var guard = new Regex(@"[dD]evicePixelRatio\s*(<=|>=|<|>)\s*[0-9A-Z]");

            var offenders = new List<string>();
            bool foundTheOneHome = false;

            foreach (string path in Directory.GetFiles(codeRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (!guard.IsMatch(File.ReadAllText(path))) continue;

                if (Path.GetFileName(path) == "DeviceScaling.cs") foundTheOneHome = true;
                else offenders.Add(path.Substring(codeRoot.Length + 1));
            }

            // Positive leg FIRST: a predicate that matched nothing would otherwise pass vacuously.
            Assert.IsTrue(foundTheOneHome,
                "Unity/View/DeviceScaling.cs must carry the implausible-ratio fallback — if this fails the " +
                "sweep predicate is broken, not the code, and the emptiness below proves nothing. The " +
                "predicate needs the operator ADJACENT to the ratio's name, so three otherwise-natural " +
                "rewrites of SafeRatio defeat it: a property pattern (`ratio is >= X and <= Y`), extra " +
                "parens around the name, and reversed operands (`Min <= ratio`). Fix the guard's form, " +
                "never loosen this sweep.");
            Assert.IsEmpty(offenders,
                "the device-pixel-ratio fallback must exist in exactly one production file (Unity/View/" +
                "DeviceScaling.cs). Hand-written copies diverge: the camera's logical viewport " +
                "and the selector's framing viewport once carried none at all and went infinite. NOTE the sweep " +
                "reads COMMENTS too — if a named file carries no such code, its prose spells the guard out " +
                "and the fix is one word of wording there, not a change to DeviceScaling.cs. Offenders: " +
                string.Join(", ", offenders));
        }
    }
}
