using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Unity.Rendering.Meshing;
// S54: MapFillBootstrap retired; FillSceneHelper replaces it.

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Headless visual snapshot tests: render the S02 world-fill map to an off-screen
    /// <c>RenderTexture</c>, write PNGs to <c>Logs/snapshots/</c>, and run a tolerant
    /// coverage assertion.
    ///
    /// Why off-screen: <c>Camera.Render()</c> with <c>camera.targetTexture</c> set renders to the RT,
    /// NOT to the screen/framebuffer. This works in batchmode without a display (subject to the GPU
    /// context being available — see the all-black guard below).
    ///
    /// Acceptance criteria (S30):
    ///   - PNGs are written under <c>Logs/snapshots/</c>.
    ///   - The world-fill render passes the tolerant coverage assertion.
    ///   - The blank-render control FAILS the coverage assertion (gate has teeth).
    ///   - Written PNGs are human/agent-readable artefacts (no in-tree golden baselines needed).
    ///
    /// GPU-context guard:
    ///   If BOTH the world-fill render AND the blank-control render come back all-black, this
    ///   indicates EditMode batchmode could not obtain a GPU context. The test marks itself
    ///   <c>Inconclusive</c> (not a failure) and directs the runner to re-run as PlayMode
    ///   (<c>./Tools/run-tests.sh PlayMode</c>).
    ///
    /// Background colour: a distinctive dark slate (not black) so all-black reads back as "no GPU"
    /// rather than "background only", which is critical to the guard logic.
    ///
    /// Camera setup: mirrors <c>MapTestScene.cs</c> exactly — top-down ortho, solid-colour clear,
    /// camera at Y=200 looking straight down, orthographicSize=70.
    /// </summary>
    [TestFixture]
    public class WorldFillSnapshotTests
    {
        // Snapshot resolution — 512×512 is a good balance of detail vs render time.
        private const int SnapW = 512;
        private const int SnapH = 512;

        // Background: distinctive dark slate (NOT black) so all-black = "no GPU context".
        private static readonly Color BgColor     = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte  BgR8        = (byte)(0.10f * 255 + 0.5f); // 26
        private static readonly byte  BgG8        = (byte)(0.11f * 255 + 0.5f); // 28
        private static readonly byte  BgB8        = (byte)(0.15f * 255 + 0.5f); // 38

        // Coverage thresholds — deliberately wide to be GPU/driver/Unity-tolerant.
        private const float MinFill    = 0.10f; // at least 10% fill pixels
        private const float MaxFill    = 0.85f; // at most 85% fill pixels
        private const int   MinBuckets = 8;     // at least 8 of 64 grid cells hit

        // -----------------------------------------------------------------------------------------
        // Test 1: Render world fill, write PNG, assert coverage passes.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RendersWorldFill_WritesPng_AndPassesCoverage()
        {
            // Build the scene objects.
            var (mapGo, camera, cameraGo) = BuildScene();
            using var snap = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                snap.Render(camera);

                // Write the world-fill PNG — this is the human/agent-readable artefact.
                string pngPath = snap.WritePng("world-fill.png");
                Assert.IsTrue(File.Exists(pngPath),
                    $"PNG must exist after WritePng: {pngPath}");
                var fi = new FileInfo(pngPath);
                Assert.That(fi.Length, Is.GreaterThan(500L),
                    "PNG file must be non-trivial (> 500 bytes); a 0-byte or header-only file " +
                    "indicates an encode failure.");
                Debug.Log($"[SnapshotTest] World-fill PNG written: {pngPath} ({fi.Length} bytes)");

                // GPU-context guard: if the render is all-black, check whether the camera clear
                // colour is also unreadable by running a blank-camera render.
                if (snap.IsAllBlack())
                {
                    // Render a second time with no map — if THAT is also all-black, no GPU context.
                    using var blankSnap = RenderBlank(BgColor);
                    if (blankSnap.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "Both the world-fill render and the blank-control render came back " +
                            "all-black. This indicates EditMode batchmode has no GPU context on " +
                            "this machine. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                // Run the tolerant coverage assertion.
                byte[] pixels = snap.RawPixels;
                SnapshotVerdict verdict = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log(
                    $"[SnapshotTest] World-fill coverage: filled={verdict.FilledFraction:P1}, " +
                    $"bg={verdict.BackgroundFraction:P1}, buckets={verdict.DistinctRegionBucketsHit}/64, " +
                    $"isBlank={verdict.IsBlank}, isUniform={verdict.IsUniform}");

                Assert.IsFalse(verdict.IsBlank,
                    "World-fill render must not be detected as blank. " +
                    "A blank result means the mesh was not built or the camera does not frame it.");
                Assert.IsFalse(verdict.IsUniform,
                    "World-fill render must not be uniform. " +
                    "A uniform result means the fill covers the entire frame or something is wrong.");
                Assert.That(verdict.FilledFraction,
                    Is.InRange(MinFill, MaxFill),
                    $"Fill fraction {verdict.FilledFraction:P1} must be in [{MinFill:P0}, {MaxFill:P0}]. " +
                    "If the fill is outside this band the camera may not frame the map correctly, " +
                    "or the mesh was not built.");
                Assert.That(verdict.DistinctRegionBucketsHit,
                    Is.GreaterThanOrEqualTo(MinBuckets),
                    $"Expected >= {MinBuckets} grid buckets hit, got {verdict.DistinctRegionBucketsHit}. " +
                    "The fill must be spatially spread across the frame, not concentrated in one corner.");
                Assert.IsTrue(verdict.Passes(MinFill, MaxFill, MinBuckets),
                    "World-fill render must pass the overall coverage gate.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Test 2: Blank render (no map object) must FAIL coverage — gate has teeth.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void BlankRender_FailsCoverage()
        {
            var (cameraGo, camera) = BuildCamera();
            using var snap = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                snap.Render(camera);

                // Write the blank-control PNG for inspection.
                string pngPath = snap.WritePng("blank-control.png");
                Assert.IsTrue(File.Exists(pngPath),
                    $"Blank-control PNG must exist after WritePng: {pngPath}");
                Debug.Log($"[SnapshotTest] Blank-control PNG written: {pngPath}");

                // GPU-context guard: if this render is all-black, it's a context failure,
                // not a real blank render. Skip the gate rather than producing a misleading pass.
                if (snap.IsAllBlack())
                {
                    Assert.Inconclusive(
                        "Blank-control render came back all-black. This indicates no GPU context " +
                        "in EditMode batchmode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                    return;
                }

                byte[] pixels = snap.RawPixels;
                SnapshotVerdict verdict = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log(
                    $"[SnapshotTest] Blank-control coverage: filled={verdict.FilledFraction:P1}, " +
                    $"bg={verdict.BackgroundFraction:P1}, isBlank={verdict.IsBlank}");

                // This is the negative control: a camera with no map should render only the
                // background colour → IsBlank == true, Passes() == false.
                Assert.IsTrue(verdict.IsBlank,
                    $"Blank render (no map) must be detected as blank " +
                    $"(bg fraction={verdict.BackgroundFraction:P1}, filled={verdict.FilledFraction:P1}). " +
                    "If this fails, there is an unexpected object in the scene or the background colour " +
                    "is being misread.");
                Assert.IsFalse(verdict.Passes(MinFill, MaxFill, MinBuckets),
                    "Blank render must fail the coverage gate — this validates that the gate has teeth " +
                    "and cannot be trivially fooled by an empty render.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Scene helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a camera GameObject configured to mirror <c>MapTestScene.cs</c>:
        /// top-down orthographic, solid-colour clear with the dark-slate background.
        /// </summary>
        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("SnapshotCamera");
            var camera = go.AddComponent<Camera>();

            // Top-down orthographic — exactly as in MapTestScene.
            camera.transform.position = new Vector3(0f, 200f, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = 70f;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;

            // Disable in-scene cameras so they don't interfere.
            camera.enabled = false;

            return (go, camera);
        }

        /// <summary>
        /// Builds the fill scene: FillSceneHelper + camera + directional light, ready to render.
        /// Returns (mapGameObject, camera, cameraGameObject). Caller must destroy both GOs.
        ///
        /// S54: MapFillBootstrap retired; uses FillSceneHelper (StyledFillTileBuilder-backed).
        /// Directional light: required because Map/Fill (URP Lit) renders near-black
        /// at ambient-only.
        /// </summary>
        private static (GameObject mapGo, Camera camera, GameObject cameraGo) BuildScene()
        {
            var (cameraGo, camera) = BuildCamera();

            // Add a directional light so the Lit material renders brightly enough for coverage.
            var lightGo = new GameObject("SceneLight");
            var light   = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            // Build the map fill via FillSceneHelper (StyledFillTileBuilder + fixture tile).
            var (mapGo, _) = FillSceneHelper.BuildFillGo(viewSize: 100f);

            // Attach the light to mapGo for unified cleanup (caller destroys mapGo + cameraGo).
            lightGo.transform.SetParent(mapGo.transform);

            return (mapGo, camera, cameraGo);
        }

        /// <summary>
        /// Renders an empty camera (no map object) and returns the snapshot renderer.
        /// Used by the GPU-context guard in <see cref="RendersWorldFill_WritesPng_AndPassesCoverage"/>.
        /// Caller must dispose the returned renderer.
        /// </summary>
        private static SnapshotRenderer RenderBlank(Color bgColor)
        {
            var (cameraGo, camera) = BuildCamera();
            camera.backgroundColor = bgColor;

            var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
            }
            return snap;
        }
    }
}
