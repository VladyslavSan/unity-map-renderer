// S58 acceptance (STRUCTURAL) — the map material inspectors subclass the RAW UnityEditor.ShaderGUI.
//
// Replaces the S35 test that asserted MapLitShaderGUI : URP BaseShaderGUI. S58 retired the
// UCL-derived MapLitShaderGUI; the inspectors now subclass the raw ShaderGUI directly (no URP
// editor dependency — the test asmdef no longer references Unity.RenderPipelines.Universal.Editor).
// The inspector LAYOUT itself is a manual in-editor check; this only pins the type hierarchy.
using NUnit.Framework;
#if UNITY_EDITOR
using UnityEditor;                 // raw ShaderGUI
using MapRenderer.Unity.Editor;    // MapShaderGUI / FillShaderGUI / LineShaderGUI
#endif

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MapShaderGUITests
    {
#if UNITY_EDITOR
        [Test]
        public void BaseShaderGUI_SubclassesRawShaderGUI_Directly()
        {
            Assert.AreEqual(typeof(ShaderGUI), typeof(MapRenderer.Unity.Editor.BaseShaderGUI).BaseType,
                "Our BaseShaderGUI must subclass the RAW UnityEditor.ShaderGUI directly (S58) — it is our own " +
                "type, distinct from URP's UnityEditor.BaseShaderGUI (no longer referenced).");
        }

        [Test]
        public void Hierarchy_IsBase_Lit_Feature()
        {
            // Three-level hierarchy mirroring URP's BaseShaderGUI → LitShader → feature:
            //   BaseShaderGUI (Surface Options/Inputs/Advanced) → LitShaderGUI (Detail) → Fill/LineShaderGUI.
            Assert.AreEqual(typeof(MapRenderer.Unity.Editor.BaseShaderGUI), typeof(LitShaderGUI).BaseType,
                "LitShaderGUI must derive from our BaseShaderGUI.");
            Assert.AreEqual(typeof(LitShaderGUI), typeof(FillShaderGUI).BaseType,
                "FillShaderGUI must derive from LitShaderGUI.");
            Assert.AreEqual(typeof(LitShaderGUI), typeof(LineShaderGUI).BaseType,
                "LineShaderGUI must derive from LitShaderGUI.");
            Assert.IsTrue(typeof(ShaderGUI).IsAssignableFrom(typeof(FillShaderGUI)));
            Assert.IsTrue(typeof(ShaderGUI).IsAssignableFrom(typeof(LineShaderGUI)));
        }
#else
        [Test]
        public void MapShaderGUI_SubclassesRawShaderGUI_Directly()
            => Assert.Inconclusive("ShaderGUI types are editor-only (EditMode tests only).");
#endif
    }
}
