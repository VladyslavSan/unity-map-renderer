// Unity EditMode only — off-screen GPU render (SnapshotRenderer). NOT registered in core-tests.csproj.
//
// The unlit epic's stated "why", made falsifiable: unlit output is deterministic — independent of light
// direction, intensity and the ambient environment. No committed golden PNG is involved; the oracle is
// the SAME scene rendered under two different lighting environments, which for unlit must come back
// byte-identical. The Lit arm in the same test is the instrument's control.

using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Proves the unlit shading family is <b>lighting-independent</b>: the same fill scene rendered under
    /// two different lighting environments produces byte-identical frames through <c>Map/FillUnlit</c>, and
    /// does NOT through <c>Map/Fill</c>.
    ///
    /// <para><b>Why the Lit arm is in the same test, not a separate one.</b> "The two frames are identical"
    /// passes trivially when the fixture renders nothing, renders a flat frame, or when the lighting
    /// perturbation never reached the render at all. The Lit arm is the control that closes that: it runs
    /// the same geometry, camera and environments, and must come back <i>different</i>. If it does not, the
    /// perturbation is not reaching the GPU and the unlit half proves nothing — a failing instrument, not a
    /// pass. Keeping both arms in one test method is what makes the unlit claim unreadable on its own.</para>
    ///
    /// <para>Two further preconditions run before either claim: the frames must be non-blank and
    /// non-uniform (something actually rendered), and each arm must reproduce its own first environment
    /// byte-for-byte on a repeat render (an off-screen camera render is deterministic here, so a mismatch
    /// means the renderer — not the shading family — is the source of any difference below).</para>
    ///
    /// <para>Batch EditMode on this project does have a GPU context (the lit snapshot fixtures render for
    /// real), but the established convention still applies: if the renders and a blank control are all
    /// black, go Inconclusive and point at PlayMode rather than failing.</para>
    /// </summary>
    [TestFixture]
    public class UnlitLightingIndependenceTests
    {
        private const int   SnapW   = 256;
        private const int   SnapH   = 256;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        /// <summary>Camera clear colour — the dark slate the other fill snapshot fixtures use, so the
        /// coverage analysis has a background that is not black.</summary>
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);

        private const byte BgR8 = 26; // 0.10 * 255
        private const byte BgG8 = 28; // 0.11 * 255
        private const byte BgB8 = 38; // 0.15 * 255

        /// <summary>One lighting environment: a directional light plus the flat ambient term. The two
        /// instances below differ in direction, intensity AND ambient, so a shader that reads any of the
        /// three cannot come out identical across them.</summary>
        private readonly struct LightingEnvironment
        {
            /// <summary>Name used in failure messages and PNG file names.</summary>
            public readonly string Name;

            /// <summary>Rotation applied to the scene's single directional light.</summary>
            public readonly Quaternion LightRotation;

            /// <summary>Directional light intensity.</summary>
            public readonly float LightIntensity;

            /// <summary>Flat ambient colour (<see cref="AmbientMode.Flat"/>).</summary>
            public readonly Color Ambient;

            /// <param name="name">Name used in failure messages and PNG file names.</param>
            /// <param name="lightRotation">Rotation applied to the directional light.</param>
            /// <param name="lightIntensity">Directional light intensity.</param>
            /// <param name="ambient">Flat ambient colour.</param>
            public LightingEnvironment(
                string name, Quaternion lightRotation, float lightIntensity, Color ambient)
            {
                Name           = name;
                LightRotation  = lightRotation;
                LightIntensity = lightIntensity;
                Ambient        = ambient;
            }
        }

        private static readonly LightingEnvironment EnvA = new LightingEnvironment(
            "envA", Quaternion.Euler(50f, 20f, 0f), 2.0f, new Color(0.45f, 0.45f, 0.50f, 1f));

        private static readonly LightingEnvironment EnvB = new LightingEnvironment(
            "envB", Quaternion.Euler(12f, 205f, 0f), 0.25f, Color.black);

        [Test]
        public void UnlitFill_RendersIdenticallyUnderTwoLightingEnvironments_WhereLitFillDoesNot()
        {
            int  prevQuality      = QualitySettings.GetQualityLevel();
            var  prevAmbientMode  = RenderSettings.ambientMode;
            var  prevAmbientLight = RenderSettings.ambientLight;

            GameObject cameraGo = null, mapGo = null, lightGo = null;
            Material   unlitMat = null, litMat = null;
            SnapshotRenderer snap = null;

            try
            {
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode = AmbientMode.Flat;

                Camera camera;
                (cameraGo, camera) = BuildCamera();
                (mapGo, _)         = FillSceneHelper.BuildFillGo();
                var meshRenderer   = mapGo.GetComponent<MeshRenderer>();

                lightGo = new GameObject("UnlitIndependenceDirLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;

                unlitMat = BuildFillMaterial("Map/FillUnlit");
                litMat   = BuildFillMaterial("Map/Fill");

                snap = new SnapshotRenderer(SnapW, SnapH);

                byte[] Capture(Material material, in LightingEnvironment environment)
                {
                    meshRenderer.sharedMaterial  = material;
                    light.transform.rotation     = environment.LightRotation;
                    light.intensity              = environment.LightIntensity;
                    RenderSettings.ambientLight  = environment.Ambient;
                    snap.Render(camera);
                    return (byte[])snap.RawPixels.Clone();
                }

                // A discarded first render per arm absorbs any one-off shader/variant warm-up, so the
                // repeatability precondition below measures the steady state.
                Capture(unlitMat, EnvA);
                byte[] unlitA       = Capture(unlitMat, EnvA);
                byte[] unlitB       = Capture(unlitMat, EnvB);
                byte[] unlitARepeat = Capture(unlitMat, EnvA);

                Capture(litMat, EnvA);
                byte[] litA       = Capture(litMat, EnvA);
                byte[] litB       = Capture(litMat, EnvB);
                byte[] litARepeat = Capture(litMat, EnvA);

                SnapshotRenderer.WritePngFromRgba32(unlitA, SnapW, SnapH, "unlit-independence-unlit-envA.png");
                SnapshotRenderer.WritePngFromRgba32(unlitB, SnapW, SnapH, "unlit-independence-unlit-envB.png");
                SnapshotRenderer.WritePngFromRgba32(litA,   SnapW, SnapH, "unlit-independence-lit-envA.png");
                SnapshotRenderer.WritePngFromRgba32(litB,   SnapW, SnapH, "unlit-independence-lit-envB.png");

                // ── GPU-context guard (established convention) ──────────────────────────────────────
                if (snap.IsAllBlack())
                {
                    meshRenderer.enabled = false;
                    snap.Render(camera);
                    bool blankIsBlackToo = snap.IsAllBlack();
                    meshRenderer.enabled = true;
                    if (blankIsBlackToo)
                    {
                        Assert.Inconclusive(
                            "Fill renders and the blank control are all-black: no GPU context. " +
                            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                // ── Precondition 1: something actually rendered, in both arms ──────────────────────
                AssertFrameIsMapLike(unlitA, "Map/FillUnlit under envA");
                AssertFrameIsMapLike(litA,   "Map/Fill under envA");

                // ── Precondition 2: the renderer itself is repeatable ──────────────────────────────
                // Without this, a difference measured below could be renderer noise rather than the
                // shading family reading the environment.
                Assert.AreEqual(-1, FirstDifference(unlitA, unlitARepeat),
                    "precondition: re-rendering Map/FillUnlit under the SAME environment must reproduce " +
                    "the frame byte-for-byte. It did not, so this fixture cannot attribute any difference " +
                    "to lighting — the renderer is not deterministic here and both claims below are unreadable.");
                Assert.AreEqual(-1, FirstDifference(litA, litARepeat),
                    "precondition: re-rendering Map/Fill under the SAME environment must reproduce the " +
                    "frame byte-for-byte. It did not, so the Lit control below cannot distinguish 'lighting " +
                    "reached the render' from renderer noise.");

                // ── The control, asserted BEFORE the claim it validates ────────────────────────────
                int litDifferingBytes = CountDifferingBytes(litA, litB);
                TestContext.WriteLine(
                    $"[UnlitLightingIndependence] lit differing bytes={litDifferingBytes}, " +
                    $"unlit differing bytes={CountDifferingBytes(unlitA, unlitB)} of {unlitA.Length}");

                Assert.Greater(litDifferingBytes, 0,
                    $"instrument failure, NOT a pass: the SAME scene rendered through Map/Fill under " +
                    $"'{EnvA.Name}' and '{EnvB.Name}' came out byte-identical. Those environments differ in " +
                    "light direction, light intensity and ambient colour, so a lit shader must see them " +
                    "differently. Identical output means the perturbation never reached the render — and " +
                    "the unlit assertion below would then be vacuous, passing for the same reason rather " +
                    "than because unlit is lighting-independent.");

                // ── The claim ─────────────────────────────────────────────────────────────────────
                int unlitDiff = FirstDifference(unlitA, unlitB);
                Assert.AreEqual(-1, unlitDiff,
                    $"Map/FillUnlit must render byte-identically under '{EnvA.Name}' and '{EnvB.Name}' — " +
                    "unlit output is independent of light direction, intensity and the ambient environment, " +
                    "which is what makes it a stable oracle and frees it from the scene's lighting being " +
                    $"set up correctly. First differing byte at index {unlitDiff} " +
                    $"({CountDifferingBytes(unlitA, unlitB)} bytes differ of {unlitA.Length}). " +
                    "Compare Logs/snapshots/unlit-independence-unlit-env{A,B}.png.");
            }
            finally
            {
                snap?.Dispose();
                if (litMat   != null) UnityEngine.Object.DestroyImmediate(litMat);
                if (unlitMat != null) UnityEngine.Object.DestroyImmediate(unlitMat);
                if (lightGo  != null) UnityEngine.Object.DestroyImmediate(lightGo);
                if (mapGo    != null) UnityEngine.Object.DestroyImmediate(mapGo);
                if (cameraGo != null) UnityEngine.Object.DestroyImmediate(cameraGo);
                RenderSettings.ambientLight = prevAmbientLight;
                RenderSettings.ambientMode  = prevAmbientMode;
                QualitySettings.SetQualityLevel(prevQuality, false);
            }
        }

        /// <summary>Top-down orthographic snapshot camera, disabled so only <c>Camera.Render</c> draws it —
        /// same rig as the other fill snapshot fixtures.</summary>
        /// <returns>The camera's <see cref="GameObject"/> (the caller destroys it) and the camera.</returns>
        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("UnlitIndependenceCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSz;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        /// <summary>Builds a fill material on the named shader with the painter contract and paint state the
        /// per-layer material pipeline applies, so the two arms differ ONLY in shading family.</summary>
        /// <param name="shaderName">The shader to instantiate (<c>Map/Fill</c> or <c>Map/FillUnlit</c>).</param>
        /// <returns>The material; the caller destroys it.</returns>
        private static Material BuildFillMaterial(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.IsNotNull(shader,
                $"Shader '{shaderName}' not found — the lit/unlit fill twins must both be importable, or " +
                "this fixture is comparing one family against nothing.");

            var material = new Material(shader) { name = $"UnlitIndependence_{shaderName}" };
            FillTweaker.ApplyPainterContract(material);
            material.SetColor(ShaderProperties.PropertyNames.BaseColor, new Color(0.5f, 0.9f, 0.3f, 1f));
            material.SetFloat(ShaderProperties.PropertyNames.Opacity, 1f);
            return material;
        }

        /// <summary>Asserts a captured frame shows a real map draw — not a blank, all-background or flat
        /// frame, any of which would make a byte-identity comparison pass for the wrong reason.</summary>
        /// <param name="pixels">RGBA32 frame from <see cref="SnapshotRenderer.RawPixels"/>.</param>
        /// <param name="what">Description of the arm, for the failure message.</param>
        private static void AssertFrameIsMapLike(byte[] pixels, string what)
        {
            SnapshotVerdict verdict = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);
            Assert.IsFalse(verdict.IsBlank,
                $"precondition: the {what} frame is blank — a byte-identity comparison over two empty " +
                "frames is vacuous.");
            Assert.IsFalse(verdict.IsUniform,
                $"precondition: the {what} frame is a single flat colour — nothing distinguishable was " +
                "drawn, so identity across environments would prove nothing.");
            Assert.Greater(verdict.FilledFraction, 0.02f,
                $"precondition: the {what} frame must contain a real fill draw (>2% non-background " +
                $"pixels); measured {verdict.FilledFraction:F4}.");
        }

        /// <summary>Index of the first differing byte, or -1 when the buffers are identical.</summary>
        /// <param name="a">First frame.</param>
        /// <param name="b">Second frame.</param>
        /// <returns>The first differing index, or -1.</returns>
        private static int FirstDifference(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException($"frame sizes differ: {a.Length} vs {b.Length}");
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return i;
            return -1;
        }

        /// <summary>How many bytes differ between two frames — the magnitude behind
        /// <see cref="FirstDifference"/>, so a failure message can distinguish one stray pixel from a
        /// wholly different image.</summary>
        /// <param name="a">First frame.</param>
        /// <param name="b">Second frame.</param>
        /// <returns>The count of differing byte positions.</returns>
        private static int CountDifferingBytes(byte[] a, byte[] b)
        {
            int count = 0;
            for (int i = 0; i < math.min(a.Length, b.Length); i++)
                if (a[i] != b[i]) count++;
            return count;
        }
    }
}
