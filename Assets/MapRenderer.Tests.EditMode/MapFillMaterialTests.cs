// S34 acceptance test — committed MapFill.mat template asset.
//
// Validates that:
//   1. MapFill.mat exists at its required path.
//   2. It references the authoritative MapRenderer/Fill shader (not the deprecated Hidden/ variant).
//   3. Key map paint and URP Lit properties are present with sensible defaults.
//
// This test is GPU-independent (pure asset/property inspection) and always runs in EditMode.
// It acts as the headless gate for review comment #1 ("The .mat asset existing is not exempt").
using NUnit.Framework;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MapFillMaterialTests
    {
        private const string MatPath     = "Assets/MapRenderer.Unity/Materials/MapFill.mat";
        private const string ShaderName  = "MapRenderer/Fill";
        private const string LineMatPath = "Assets/MapRenderer.Unity/Materials/MapLine.mat";

        // Tolerance for the styled RGBA assertions (per channel). Tight enough that a white
        // clobber {1,1,1,1} fails, loose enough for float round-trip through the YAML asset.
        private const float BaseColorTol = 1e-3f;

#if UNITY_EDITOR
        [Test]
        public void MapFillMat_Exists()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            Assert.That(mat, Is.Not.Null,
                $"MapFill.mat must exist at '{MatPath}'. " +
                "S34 acceptance #7 requires the committed template material asset.");
        }

        [Test]
        public void MapFillMat_ReferencesCorrectShader()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            Assume.That(mat, Is.Not.Null, "MapFill.mat not found — run MapFillMat_Exists first.");

            Assert.That(mat.shader, Is.Not.Null,
                "MapFill.mat shader reference must not be null.");
            Assert.That(mat.shader.name, Is.EqualTo(ShaderName),
                $"MapFill.mat must reference shader '{ShaderName}', not '{mat.shader?.name}'. " +
                "Check that the GUID in MapFill.mat matches Assets/MapRenderer.Unity/Shaders/Fill.shader.meta.");
        }

        [Test]
        public void MapFillMat_HasBaseColorProperty()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            Assume.That(mat, Is.Not.Null, "MapFill.mat not found.");

            // _BaseColor is the map paint property (MapLibre-style fill-color knob).
            // Renamed from _Color in S37 so URP's legacy _BaseColor alias has no bare _Color to
            // clobber to {1,1,1} on import/Editor-open. Assert the ACTUAL styled RGBA — a white
            // clobber {1,1,1,1} would fail on r/g/b here.
            Color color = mat.GetColor("_BaseColor");
            Assert.That(color.r, Is.EqualTo(0.4f).Within(BaseColorTol),
                $"MapFill.mat _BaseColor.r must be ~0.4 (got {color.r:F4}). A white clobber → 1.0 fails this.");
            Assert.That(color.g, Is.EqualTo(0.7f).Within(BaseColorTol),
                $"MapFill.mat _BaseColor.g must be ~0.7 (got {color.g:F4}).");
            Assert.That(color.b, Is.EqualTo(0.4f).Within(BaseColorTol),
                $"MapFill.mat _BaseColor.b must be ~0.4 (got {color.b:F4}). A white clobber → 1.0 fails this.");
            Assert.That(color.a, Is.EqualTo(1.0f).Within(BaseColorTol),
                $"MapFill.mat _BaseColor.a must be ~1.0 (got {color.a:F4}).");
        }

        [Test]
        public void MapFillMat_HasOpacityProperty()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            Assume.That(mat, Is.Not.Null, "MapFill.mat not found.");

            float opacity = mat.GetFloat("_Opacity");
            Assert.That(opacity, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f),
                "_Opacity must be present in MapFill.mat in range (0, 1].");
        }

        [Test]
        public void MapFillMat_HasStandardLitProperties()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            Assume.That(mat, Is.Not.Null, "MapFill.mat not found.");

            // Verify core URP Lit properties are present (full surface, not stripped).
            float metallic   = mat.GetFloat("_Metallic");
            float smoothness = mat.GetFloat("_Smoothness");
            Assert.That(metallic,   Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f),
                "_Metallic must be present in MapFill.mat.");
            Assert.That(smoothness, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f),
                "_Smoothness must be present in MapFill.mat.");
        }

        [Test]
        public void MapLineMat_Exists()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(LineMatPath);
            Assert.That(mat, Is.Not.Null,
                $"MapLine.mat must exist at '{LineMatPath}'.");
        }

        [Test]
        public void MapLineMat_HasBaseColorProperty()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(LineMatPath);
            Assume.That(mat, Is.Not.Null, "MapLine.mat not found — run MapLineMat_Exists first.");

            // _BaseColor is the line paint property. Assert the ACTUAL styled RGBA {0.3,0.5,1.0,1}
            // (S37). A URP white clobber {1,1,1,1} would fail on r/g here.
            Color color = mat.GetColor("_BaseColor");
            Assert.That(color.r, Is.EqualTo(0.3f).Within(BaseColorTol),
                $"MapLine.mat _BaseColor.r must be ~0.3 (got {color.r:F4}). A white clobber → 1.0 fails this.");
            Assert.That(color.g, Is.EqualTo(0.5f).Within(BaseColorTol),
                $"MapLine.mat _BaseColor.g must be ~0.5 (got {color.g:F4}). A white clobber → 1.0 fails this.");
            Assert.That(color.b, Is.EqualTo(1.0f).Within(BaseColorTol),
                $"MapLine.mat _BaseColor.b must be ~1.0 (got {color.b:F4}).");
            Assert.That(color.a, Is.EqualTo(1.0f).Within(BaseColorTol),
                $"MapLine.mat _BaseColor.a must be ~1.0 (got {color.a:F4}).");
        }

        [Test]
        public void MapLineMat_ResolvesToTransparentQueue()
        {
            // S37 regression guard, updated for S58. The line is transparent
            // (docs/lit-rendering-design.md §"Line specifics" — Queue=Transparent>=2501 drives the
            // painter's-algorithm coplanar fill/line ordering; ZWrite Off). Since S58 retired URP's
            // BaseShaderGUI, the queue is NO LONGER auto-resolved from _Surface/_QueueControl on import
            // (our raw-ShaderGUI ValidateMaterial only syncs keywords — it never touches renderQueue).
            // The transparent queue now comes from MapLine.mat's serialized custom render queue (3000)
            // and the SubShader's Queue=Transparent tag. Material.renderQueue returns the resolved value,
            // so this asserts the import outcome directly: a fresh batch import keeps the line transparent.
            var mat = AssetDatabase.LoadAssetAtPath<Material>(LineMatPath);
            Assume.That(mat, Is.Not.Null, "MapLine.mat not found — run MapLineMat_Exists first.");

            Assert.That(mat.renderQueue, Is.GreaterThanOrEqualTo(2501),
                $"MapLine.mat must resolve to the Transparent render queue (>=2501) after a fresh " +
                $"batch import, got {mat.renderQueue}. Since S58 the queue comes from the material's " +
                "serialized custom render queue (3000) + the Line SubShader Queue=Transparent tag — " +
                "the raw-ShaderGUI no longer recomputes it. A value of 2000 means the custom queue was " +
                "lost. See docs/lit-rendering-design.md.");
        }
#else
        [Test]
        public void MapFillMat_Exists()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }

        [Test]
        public void MapFillMat_ReferencesCorrectShader()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }

        [Test]
        public void MapFillMat_HasBaseColorProperty()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }

        [Test]
        public void MapFillMat_HasOpacityProperty()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }

        [Test]
        public void MapFillMat_HasStandardLitProperties()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }

        [Test]
        public void MapLineMat_Exists()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }

        [Test]
        public void MapLineMat_HasBaseColorProperty()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }

        [Test]
        public void MapLineMat_ResolvesToTransparentQueue()
        {
            Assert.Inconclusive("Material asset tests require Unity Editor (EditMode only).");
        }
#endif
    }
}
