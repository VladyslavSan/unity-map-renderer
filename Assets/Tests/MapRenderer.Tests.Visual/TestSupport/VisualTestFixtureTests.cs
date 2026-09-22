// Unity EditMode only — RED-verifies the BaseTestFixture/VisualTestFixture restore contract itself,
// independent of NUnit's own fixture scheduling (see VisualTestFixtureRestoreTests below).

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Exposes the inherited PROTECTED <c>DoSetUp</c>/<c>DoTearDown</c> to
    /// <see cref="VisualTestFixtureRestoreTests"/>, which drives them directly rather than through
    /// NUnit's own scheduling — that test is ABOUT the lifecycle mechanics, not a consumer of them.
    /// Forces both knobs on so a call to either hook has something to apply and restore.
    /// </summary>
    internal sealed class ProbeVisualTestFixture : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            QualityLevel = 0,
            AmbientMode  = AmbientMode.Flat,
            AmbientLight = new Color(0.9f, 0.9f, 0.9f, 1f),
            Fog          = false,
        };

        internal void InvokeSetUp()    => DoSetUp();
        internal void InvokeTearDown() => DoTearDown();
    }

    [TestFixture]
    public class VisualTestFixtureRestoreTests
    {
        [Test]
        public void Teardown_RestoresQualityAmbientAndFog_ToTheirEntryValues()
        {
            // Force values OnSetUp will not apply (it forces quality 0 / Flat / fog off) — otherwise
            // the arms below are vacuous whenever the environment already sits at those values.
            QualitySettings.SetQualityLevel(1, false);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = true;

            int enteredQuality = QualitySettings.GetQualityLevel();
            AmbientMode enteredAmbientMode = RenderSettings.ambientMode;
            Color enteredAmbientLight = RenderSettings.ambientLight;
            bool enteredFog = RenderSettings.fog;

            var fixture = new ProbeVisualTestFixture();
            fixture.InvokeSetUp();
            try
            {
                // Positive control: setup actually changed process-global state (not a no-op that would
                // make the restore assertion below pass vacuously).
                Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo(0),
                    "VisualTestFixture.OnSetUp must force quality level 0 while a fixture is active.");
                Assert.That(RenderSettings.ambientMode, Is.EqualTo(AmbientMode.Flat),
                    "VisualTestFixture.OnSetUp must force Flat ambient while a fixture is active.");
                Assert.That(RenderSettings.fog, Is.False,
                    "VisualTestFixture.OnSetUp must force fog off while a fixture is active.");

                // A test body may leave the ambient light at whatever value it rendered with — teardown
                // must restore the ENTRY value regardless, not whatever a test last set.
                RenderSettings.ambientLight = new Color(0.77f, 0.11f, 0.33f, 1f);
            }
            finally
            {
                fixture.InvokeTearDown();
            }

            Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo(enteredQuality),
                "Quality level must be restored to its pre-fixture value after teardown.");
            Assert.That(RenderSettings.ambientMode, Is.EqualTo(enteredAmbientMode),
                "Ambient mode must be restored to its pre-fixture value after teardown.");
            Assert.That(RenderSettings.ambientLight, Is.EqualTo(enteredAmbientLight),
                "Ambient light must be restored to its pre-fixture ENTRY value, not a value a test set.");
            Assert.That(RenderSettings.fog, Is.EqualTo(enteredFog),
                "Fog must be restored to its pre-fixture entry value after teardown.");
        }
    }

    /// <summary>
    /// NUnit-driven half of the RED-verify: proves the runner discovers and invokes the PROTECTED,
    /// inherited <c>[SetUp]</c>/<c>[TearDown]</c> pair, per test — <see cref="VisualTestFixtureRestoreTests"/>
    /// cannot check this, since it calls the methods directly rather than through NUnit's own scan.
    /// </summary>
    [TestFixture]
    public class VisualTestFixtureDiscoveryTests : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            QualityLevel = 0,
            AmbientMode  = AmbientMode.Flat,
        };

        // _Second depends on running AFTER _First within the same fixture instance (today: alphabetical
        // method order) — not engineered around, just named here as the dependency it is.

        [Test]
        public void FixtureState_IsAppliedByTheTimeATestRuns_First()
        {
            AssertStateApplied();
            // Leave a mark a leaked-state bug would carry into the next test.
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f, 1f);
        }

        [Test]
        public void FixtureState_IsAppliedByTheTimeATestRuns_Second()
        {
            AssertStateApplied();
            Assert.That(RenderSettings.ambientLight, Is.Not.EqualTo(new Color(0.55f, 0.55f, 0.55f, 1f)),
                "Ambient light leaked from the previous test — SetUp/TearDown must run around EVERY test.");
        }

        private static void AssertStateApplied()
        {
            Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo(0),
                "If this fails, NUnit did not run the base class's protected [SetUp] — the whole " +
                "restore contract would then pass vacuously (globals never changed).");
            Assert.That(RenderSettings.ambientMode, Is.EqualTo(AmbientMode.Flat));
        }
    }

    /// <summary>The tooth for <see cref="VisualTestFixture.BuildCamera"/>: proves <c>ViewSize</c>,
    /// <c>Background</c> and <c>Distance</c> actually reach the camera, not just that the call
    /// compiles.</summary>
    [TestFixture]
    public class VisualTestFixtureBuildCameraTests : VisualTestFixture
    {
        [Test]
        public void BuildCamera_FramesViewSize_TopDown_AndAppliesBackground()
        {
            var settings = new CameraSettings
            {
                ViewSize   = new float2(200f, 100f),
                Background = new Color(0.2f, 0.4f, 0.6f, 1f),
            };

            var (go, camera) = BuildCamera(settings);
            Track(go);
            {
                Assert.That(camera.orthographic, Is.True, "the snapshot rig is orthographic.");
                Assert.That(camera.orthographicSize, Is.EqualTo(50f).Within(1e-4f),
                    "orthographicSize is HALF the vertical ViewSize (Unity's own convention).");
                Assert.That(camera.aspect, Is.EqualTo(2f).Within(1e-4f),
                    "aspect must reflect ViewSize.x / ViewSize.y (200/100), not Unity's screen aspect.");
                Assert.That(camera.backgroundColor, Is.EqualTo(settings.Background),
                    "the requested clear colour must reach the camera.");
                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
                Assert.That(camera.enabled, Is.False, "the snapshot rig is driven by SnapshotRenderer, never ticked.");

                Vector3 euler = camera.transform.eulerAngles;
                Assert.That(euler.x, Is.EqualTo(90f).Within(1e-3f),
                    "a 90° pitch is what makes this rig TOP-DOWN — the thing 33 call sites hand-wrote.");
                Assert.That(camera.farClipPlane, Is.GreaterThan(camera.transform.position.y),
                    "far-clip must reach past the camera's own distance, or geometry at the origin is clipped.");
            }
        }

        [Test]
        public void BuildCamera_Distance_DefaultsToStandard_ButIsOverridable()
        {
            var settings = new CameraSettings { ViewSize = new float2(100f, 100f), Background = Color.black };

            var (defaultGo, defaultCamera) = BuildCamera(settings);
            Track(defaultGo);
            // NOT scaffolding: a lit shader reads the camera's WORLD POSITION
            // (_WorldSpaceCameraPos) even under an orthographic projection, so a wrong default
            // here silently changes shading rather than failing to compile — see the member doc.
            Assert.That(defaultCamera.transform.position.y, Is.EqualTo(VisualTestFixture.StandardDistance),
                "an omitted Distance must fall back to the standard 200 units 15 of 16 measured rigs use.");

            settings.Distance = 42f;
            var (customGo, customCamera) = BuildCamera(settings);
            Track(customGo);
            Assert.That(customCamera.transform.position.y, Is.EqualTo(42f),
                "an explicit Distance must override the standard default.");
        }
    }
}
