// Unity EditMode only — S108 Stage 3: the device→logical conversion has ONE home, and the tile-cover
// framing viewport is the SAME definition the camera altitude frames from.
//
// Teeth covered:
//   T3-3 (MapView leg) — a non-positive DevicePixelRatio yields a FINITE framing viewport (the stage's one
//        named behaviour change). Observed on BuildTileSelectionConfig() DIRECTLY, never through
//        LateUpdate/Tick: against the pre-S108 code the infinite viewport NaN-poisons the frustum planes so
//        every tile intersects while the LOD ratio collapses to 0, and the planar cover then enumerates the
//        whole quadtree — the RED would arrive as a gate timeout instead of a failure.
//   T3-4 — at dpr 2 the selector's framing viewport IS MapCamera.ViewportLogicalPx, exactly. The only
//        tile-selection coverage at dpr ≠ 1 anywhere in the suite. It pins that LateUpdate refreshes the
//        camera's ratio AT ALL, but NOT the ordering of the refresh against the build — see the method's
//        own doc; the ordering is held by construction, recorded as design-doc §6.1 finding 7.
//   T3-6 — the implausible-ratio fallback exists in exactly one production file. (S109 widened the guard
//        from "non-positive" to a plausibility band, so the sweep predicate moved with it.)

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
    public class DevicePixelRatioFramingTests
    {
        // A deterministic, EVEN square viewport: the exact-equality legs below compare against vp/2, so the
        // framebuffer size is pinned here rather than inherited from the helper's default.
        private const int TestViewportPx = 1080;

        // ── T3-4 — one definition, shared by the altitude and the cover framing ─────────────────

        /// <summary>
        /// S108 T3-4: the tile selector's framing viewport and the camera's altitude framing must be the SAME
        /// quantity — <see cref="MapRenderer.Unity.Rendering.Map.MapCamera.ViewportLogicalPx"/> — so render
        /// framing and selection framing cannot diverge (the S86/S92 "render far == selection far" invariant).
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
            var go   = new GameObject("MapView_S108_Framing");
            var view = go.AddComponent<MapView>();
            try
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
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── T3-3 (MapView leg) — the ONE named behaviour change ─────────────────────────────────

        /// <summary>
        /// S108 T3-3: a non-positive <c>DevicePixelRatio</c> must produce a FINITE framing viewport (the dpr-1
        /// fallback), not <c>+∞</c>. Reachable through <c>MapViewConfig.DevicePixelRatio</c>, a plain
        /// serialized field nothing validates.
        ///
        /// <para><b>Deliberately does not call <c>LateUpdate</c>.</b> Against the pre-S108 body the framing
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
            var go   = new GameObject("MapView_S108_FramingGuard");
            var view = go.AddComponent<MapView>();
            try
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
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── T3-6 — the implausible-ratio fallback has exactly one home ──────────────────────────

        /// <summary>
        /// S108 T3-6: D2 ("the ratio is applied ONCE, at the conversion") made structural for the device→logical
        /// direction. Before this stage the fallback was written out by hand in four production files and had
        /// already diverged three ways — two sites carried no fallback at all. Sweeps production sources for a
        /// ratio guard and requires the only match to be <c>DeviceScaling.cs</c>.
        ///
        /// <para>The predicate is deliberately narrow (the ratio's name, then a comparison against a number or
        /// a named bound): a bare <c>&gt; 0.0 ? … : 1.0</c> sweep matches four unrelated production sites that
        /// have nothing to do with display density. The test assembly is excluded — it lives under the same
        /// root and legitimately quotes the guard when explaining it. Note the sweep reads comments too, so
        /// production prose in <b>any</b> swept file must never put the ratio's name next to a comparison
        /// operator — say "outside the plausible band", never the expression itself. That fence binds
        /// <c>MapViewConfig</c>'s tooltip and <c>MapHost</c>'s comment as much as it binds
        /// <c>DeviceScaling</c>'s own doc.</para>
        ///
        /// <para>S109 STRENGTHENED the predicate rather than merely porting it to the plausibility band. The
        /// previous <c>devicepixelratio[^;]*&gt;\s*0</c> spanned everything up to the next semicolon, so it
        /// matched at <c>DevicePixelRatioFromDpi(double screenDpi)</c> and landed on the unrelated
        /// <c>Debug.Assert(screenDpi &gt; 0.0)</c> three lines below: the positive leg would have passed even
        /// with <c>SafeRatio</c>'s guard deleted outright. The replacement requires the operator to be
        /// ADJACENT to the ratio's name, which that form cannot satisfy. It is wider in operator reach
        /// (<c>&lt;</c>/<c>&lt;=</c>/<c>&gt;=</c> now count, so a hand-written <c>… &lt;= 0.0</c> copy is
        /// caught, which the old one missed entirely) and narrower in span; the net is a strengthening.
        /// <c>IgnoreCase</c> is deliberately ABSENT — with it, the <c>[0-9A-Z]</c> tail starts matching the
        /// <c>s</c> of a following <c>&lt;see cref=…</c>, which would make the sweep sensitive to prose that
        /// merely names the ratio near an XML tag.</para>
        /// </summary>
        [Test]
        public void RatioFallback_HasExactlyOneHome_InDeviceScaling()
        {
            string root       = Directory.GetParent(Application.dataPath)!.FullName;
            string codeRoot   = Path.Combine(root, "Assets", "Code");
            string testAssembly = Path.Combine(codeRoot, "MapRenderer.Tests.EditMode") + Path.DirectorySeparatorChar;
            var guard = new Regex(@"[dD]evicePixelRatio\s*(<=|>=|<|>)\s*[0-9A-Z]");

            var offenders = new List<string>();
            bool foundTheOneHome = false;

            foreach (string path in Directory.GetFiles(codeRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (path.StartsWith(testAssembly)) continue;
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
                "DeviceScaling.cs). Hand-written copies diverge: before S108 the camera's logical viewport " +
                "and the selector's framing viewport carried none at all and went infinite. NOTE the sweep " +
                "reads COMMENTS too — if a named file carries no such code, its prose spells the guard out " +
                "and the fix is one word of wording there, not a change to DeviceScaling.cs. Offenders: " +
                string.Join(", ", offenders));
        }
    }
}
