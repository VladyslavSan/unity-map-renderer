// Unity EditMode only — the device→logical conversion has ONE home, and the tile-cover
// framing viewport is the SAME definition the camera altitude frames from.

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

        // ── One definition, shared by the altitude and the cover framing ────────────────────────

        /// <summary>
        /// The tile selector's framing viewport and the camera's altitude framing must be the SAME quantity —
        /// <see cref="MapRenderer.Unity.Rendering.Map.MapCamera.ViewportLogicalPx"/> — so render framing and
        /// selection framing cannot diverge. It is the only selection test at a ratio other than 1.
        /// Limitation: it builds the config after <c>LateUpdate</c> returns, so it does not pin the
        /// refresh-before-build order inside <c>LateUpdate</c>; the single production caller holds that.
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

        // ── A non-positive ratio frames finitely ────────────────────────────────────────────────

        /// <summary>
        /// A non-positive <c>DevicePixelRatio</c> must produce a FINITE framing viewport (the dpr-1 fallback),
        /// not <c>+∞</c>. <c>MapViewConfig.DevicePixelRatio</c> is a serialized field nothing validates.
        /// Non-obvious why: the test skips <c>LateUpdate</c>, because an infinite viewport NaN-poisons the
        /// frustum and the planar cover then enumerates the full quadtree, so the RED hangs the gate.
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
                    // Seed BOTH ratio sources in place of the skipped LateUpdate refresh; seeding only one
                    // lets the test pass without the guard running.
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

        // ── The implausible-ratio fallback has exactly one home ─────────────────────────────────

        /// <summary>
        /// "The ratio is applied ONCE, at the conversion", made structural for the device→logical direction:
        /// sweeps <c>Assets/Code</c> for a ratio guard and requires the only match to be <c>DeviceScaling.cs</c>.
        /// Non-local invariant: the sweep reads comments too, so production prose must never put the ratio's
        /// name next to a comparison operator — write "outside the plausible band" instead.
        /// </summary>
        /// <remarks>
        /// Non-obvious why: the operator must be ADJACENT to the name: a span-to-semicolon form matches the unrelated
        /// <c>Debug.Assert(screenDpi &gt; 0.0)</c> under <c>DevicePixelRatioFromDpi</c> and passes with the
        /// guard deleted. <c>IgnoreCase</c> is absent because the <c>[0-9A-Z]</c> tail would then match the
        /// <c>s</c> of a following <c>&lt;see cref=…</c>.
        /// </remarks>
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
