// S35 acceptance test (tooth 1, STRUCTURAL) — MapLitShaderGUI base type.
//
// Asserts that the shared material inspector DERIVES from URP's BaseShaderGUI, NOT from the raw
// UnityEditor.ShaderGUI. This is the loop-closable half of S35: the manual gate (tooth 4 — the
// inspector actually rendering URP's full Lit UI plus the Map Paint section) cannot be verified
// headlessly and is reported as outstanding-manual, never claimed as passed here.
//
// The base type is checked against the actual URP type objects (the test asmdef references the URP
// editor assembly), not by string — URP declares BaseShaderGUI in namespace `UnityEditor`, so a
// FullName string would be `UnityEditor.BaseShaderGUI` and ambiguous with the raw ShaderGUI family.
// Comparing Type objects is unambiguous. The compile gate (tooth 2) is the editor assembly itself
// compiling against the URP editor asmdef reference.
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor;                 // raw ShaderGUI
using MapRenderer.Unity.Editor;    // MapLitShaderGUI
using UrpBaseShaderGUI = UnityEditor.BaseShaderGUI; // URP's public, subclass-designed material GUI base
#endif

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MapLitShaderGUITests
    {
#if UNITY_EDITOR
        [Test]
        public void MapLitShaderGUI_DerivesFromUrpBaseShaderGUI()
        {
            var baseType = typeof(MapLitShaderGUI).BaseType;
            Assert.That(baseType, Is.Not.Null, "MapLitShaderGUI must have a base type.");
            Assert.That(baseType, Is.EqualTo(typeof(UrpBaseShaderGUI)),
                "MapLitShaderGUI must DERIVE from URP's BaseShaderGUI (S35), not the raw UnityEditor.ShaderGUI. " +
                $"Found immediate base '{baseType.FullName}'.");
        }

        [Test]
        public void MapLitShaderGUI_DoesNotDeriveDirectlyFromRawShaderGUI()
        {
            var baseType = typeof(MapLitShaderGUI).BaseType;
            Assert.That(baseType, Is.Not.Null, "MapLitShaderGUI must have a base type.");
            Assert.That(baseType, Is.Not.EqualTo(typeof(ShaderGUI)),
                "MapLitShaderGUI must not derive directly from raw UnityEditor.ShaderGUI — it must go through " +
                "URP's BaseShaderGUI so the inspector gains URP's full Lit UI and keyword sync.");
            // Sanity: it must still BE a ShaderGUI (URP's BaseShaderGUI : ShaderGUI), just not directly.
            Assert.That(typeof(ShaderGUI).IsAssignableFrom(typeof(MapLitShaderGUI)), Is.True,
                "MapLitShaderGUI must ultimately be a ShaderGUI (URP's BaseShaderGUI derives from it).");
        }
#else
        [Test]
        public void MapLitShaderGUI_DerivesFromUrpBaseShaderGUI()
        {
            Assert.Inconclusive("MapLitShaderGUI is an editor-only type (EditMode tests only).");
        }

        [Test]
        public void MapLitShaderGUI_DoesNotDeriveDirectlyFromRawShaderGUI()
        {
            Assert.Inconclusive("MapLitShaderGUI is an editor-only type (EditMode tests only).");
        }
#endif
    }
}
