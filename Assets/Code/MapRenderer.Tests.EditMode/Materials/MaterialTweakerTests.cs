// S58 acceptance — the split of material setup into runtime vs editor:
//   1. RUNTIME tweakers (Fill/Line) own only the render-state contract: ApplyPainterContract sets depth +
//      per-type blend + white colour identity from scratch.
//   2. EDITOR keyword sync lives in the shader GUIs' ValidateMaterial (BaseShaderGUI + LitShaderGUI), the
//      clean-room counterpart of URP's SetMaterialKeywords. It derives shader-feature keywords from the
//      MATERIAL's property values (never a MaterialProperty) and runs the editor-only FixupEmissiveFlag.
//   3. On the committed .mat baseline (no maps, black emission, opaque) the derived keyword set is EMPTY —
//      proving the keyword sync is behaviour-neutral (parity).

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using MapRenderer.Unity.Editor;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using LineMaterialTweaker = MapRenderer.Unity.Rendering.Materials.LineTweaker;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class MaterialTweakerTests
    {
        private static Material NewFill() => new Material(Shader.Find("Map/Fill"));
        private static Material NewLine() => new Material(Shader.Find("Map/Line"));

        private static void AssertWhite(Color c, string what)
        {
            Assert.AreEqual(1f, c.r, 1e-4f, $"{what}.r");
            Assert.AreEqual(1f, c.g, 1e-4f, $"{what}.g");
            Assert.AreEqual(1f, c.b, 1e-4f, $"{what}.b");
            Assert.AreEqual(1f, c.a, 1e-4f, $"{what}.a");
        }

        // ── 1. Painter contract: render state + white identity ──
        [Test]
        public void FillTweaker_ApplyPainterContract_SetsDepthOffAndWhiteIdentity()
        {
            var m = NewFill();
            try
            {
                m.SetFloat(ShaderProperties.PropertyNames.ZWrite, 1f);           // simulate an opaque-authored base
                m.SetColor(ShaderProperties.PropertyNames.BaseColor, Color.red); // simulate a tinted base
                FillMaterialTweaker.ApplyPainterContract(m);

                Assert.AreEqual(0f, m.GetFloat(ShaderProperties.PropertyNames.ZWrite), "painter contract → ZWrite off");
                Assert.AreEqual((int)CompareFunction.LessEqual, (int)m.GetFloat(ShaderProperties.PropertyNames.ZTest),
                    "painter contract → ZTest LEqual");
                Assert.AreEqual((int)FillMaterialTweaker.DefaultSrcRGBBlend,
                    (int)m.GetFloat(ShaderProperties.PropertyNames.SrcBlend));
                Assert.AreEqual((int)FillMaterialTweaker.DefaultDstRGBBlend,
                    (int)m.GetFloat(ShaderProperties.PropertyNames.DstBlend));
                Assert.AreEqual((int)FillMaterialTweaker.DefaultSrcAlphaBlend,
                    (int)m.GetFloat(ShaderProperties.PropertyNames.SrcBlendAlpha));
                Assert.AreEqual((int)FillMaterialTweaker.DefaultDstRGBBlend,
                    (int)m.GetFloat(ShaderProperties.PropertyNames.DstBlendAlpha));
                AssertWhite(m.GetColor(ShaderProperties.PropertyNames.BaseColor), "_BaseColor");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        [Test]
        public void LineTweaker_ApplyPainterContract_SetsDepthOffAndWhiteIdentity()
        {
            var m = NewLine();
            try
            {
                m.SetFloat(ShaderProperties.PropertyNames.ZWrite, 1f);
                LineMaterialTweaker.ApplyPainterContract(m);
                Assert.AreEqual(0f, m.GetFloat(ShaderProperties.PropertyNames.ZWrite), "painter contract → ZWrite off");
                Assert.AreEqual((int)BlendMode.SrcAlpha, (int)m.GetFloat(ShaderProperties.PropertyNames.SrcBlend),
                    "line contract → straight-alpha SrcAlpha blend (NOT the premultiplied One the .mat may carry)");
                Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, (int)m.GetFloat(ShaderProperties.PropertyNames.DstBlend),
                    "line contract → straight-alpha OneMinusSrcAlpha blend");
                AssertWhite(m.GetColor(ShaderProperties.PropertyNames.BaseColor), "_BaseColor");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        // ── 2. Editor keyword sync (the shader GUIs' ValidateMaterial) reads the material ──
        // Emission is driven by the material's GI emissive flags (URP's mechanism), not the colour directly.
        // ValidateMaterial first runs the editor-only MaterialEditor.FixupEmissiveFlag, which reconciles the
        // flag with the emission colour, then derives the keyword from (flags & AnyEmissive). So the OFF state
        // must come from the flag itself (EmissiveIsBlack, no Baked/Realtime bit); the ON state needs a
        // Baked/Realtime intent with a non-black colour (else Fixup would re-flag it black).
        [Test]
        public void FillShaderGUI_ValidateMaterial_EmissionTogglesFromGI()
        {
            var m = NewFill();
            try
            {
                m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                new FillShaderGUI().ValidateMaterial(m);
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.Emission), "EmissiveIsBlack/black → _EMISSION off");

                m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.white);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                new FillShaderGUI().ValidateMaterial(m);
                Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.Emission), "BakedEmissive/white → _EMISSION on");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        [Test]
        public void LineShaderGUI_ValidateMaterial_EmissionTogglesFromGI()
        {
            var m = NewLine();
            try
            {
                m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.white);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.Emission), "RealtimeEmissive/white → _EMISSION on");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        // ── 2b. The line's own keyword: _EdgeAntialiasing → _EDGE_ANTIALIASING_OFF ──
        [Test]
        public void LineShaderGUI_ValidateMaterial_SyncsEdgeAntialiasingKeyword()
        {
            // _EdgeAntialiasing is declared [ToggleUI], which is UI-only and attaches NO keyword.
            // LineShaderGUI.ValidateMaterial is the ONLY thing that turns the float into
            // _EDGE_ANTIALIASING_OFF — delete that override and the shipped AA toggle is completely inert
            // while every other test in the repo stays green. This is its only guard.
            var m = NewLine();
            try
            {
                m.SetFloat(ShaderProperties.Line.PropertyNames.EdgeAntialiasing, 0f);
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.EdgeAntialiasingOff),
                    "_EdgeAntialiasing = 0 → _EDGE_ANTIALIASING_OFF must be set.");

                // The POLARITY tooth. _OFF polarity is a build-correctness requirement, not a style choice:
                // a shader_feature_local variant is stripped from a player build unless some material in the
                // build declares the keyword, so the SHIPPING (AA-on) state must carry no keyword. An
                // inverted sync would satisfy the clause above, look right in the Editor, and silently drop
                // antialiasing from the player build.
                m.SetFloat(ShaderProperties.Line.PropertyNames.EdgeAntialiasing, 1f);
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.EdgeAntialiasingOff),
                    "_EdgeAntialiasing = 1 (the shipping default) → _EDGE_ANTIALIASING_OFF must be CLEAR.");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        [Test]
        public void LineShaderGUI_ValidateMaterial_OnCommittedBase_LeavesAntialiasingOn()
        {
            // The committed MapLine.mat carries `_EdgeAntialiasing: 1` and no m_ShaderKeywords entry, and
            // every per-layer material is a new Material(base) copy that inherits the keyword set. So the
            // shipped state — and therefore every line the map draws — must survive the sync with AA ON.
            var m = new Material(MapMaterialSetTestUtil.Load().LineMaterial);
            try
            {
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.EdgeAntialiasingOff),
                    "the committed line base must render AA-ON: _EDGE_ANTIALIASING_OFF must be clear " +
                    "after the editor keyword sync.");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        // ── 2c. The hairline strategy selector → _HAIRLINE_HARD ──
        [Test]
        public void LineShaderGUI_ValidateMaterial_SyncsHairlineStrategyKeyword()
        {
            // _HairlineStrategy is declared [Enum(...)], a UI-only drawer that attaches NO keyword — exactly
            // like [ToggleUI] on _EdgeAntialiasing. LineShaderGUI.ValidateMaterial is the only thing that
            // turns the selector into _HAIRLINE_HARD; without it the whole selector is inert and every other
            // test in the repo stays green.
            var m = NewLine();
            try
            {
                m.SetFloat(ShaderProperties.Line.PropertyNames.HairlineStrategy, 1f);
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.HairlineHard),
                    "_HairlineStrategy = 1 (Hard) → _HAIRLINE_HARD must be set.");

                // Back to Default. This direction is the strip-safety one: `_` is the shipping member of the
                // keyword set, so the variant every player build compiles is the one carrying NO keyword.
                // A sync that latched would ship a variant that can be stripped.
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineSolidCore),
                    "strategy 1 must not also set _HAIRLINE_SOLID_CORE — a material carrying both keywords " +
                    "compiles a variant nobody reasoned about.");

                m.SetFloat(ShaderProperties.Line.PropertyNames.HairlineStrategy, 2f);
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.HairlineSolidCore),
                    "_HairlineStrategy = 2 (SolidCore) → _HAIRLINE_SOLID_CORE must be set.");
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineHard),
                    "strategy 2 must CLEAR _HAIRLINE_HARD — the two are mutually exclusive.");

                // Back to Default. This direction is the strip-safety one: `_` is the shipping member of the
                // keyword set, so the variant every player build compiles is the one carrying NO keyword.
                m.SetFloat(ShaderProperties.Line.PropertyNames.HairlineStrategy, 0f);
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineHard),
                    "_HairlineStrategy = 0 (Default, the shipping state) → _HAIRLINE_HARD must be CLEAR.");
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineSolidCore),
                    "_HairlineStrategy = 0 → _HAIRLINE_SOLID_CORE must be CLEAR.");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        [Test]
        public void LineShaderGUI_ValidateMaterial_OnCommittedBase_LeavesHairlineDefault()
        {
            // Every per-layer material is a new Material(base) copy and Unity's copy ctor carries the keyword
            // set, so whatever the committed base declares propagates to every line the map draws. This fails
            // the moment someone saves a strategy into MapLine.mat.
            var m = new Material(MapMaterialSetTestUtil.Load().LineMaterial);
            try
            {
                new LineShaderGUI().ValidateMaterial(m);
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineHard),
                    "the committed line base must ship the Default strategy: _HAIRLINE_HARD must be clear " +
                    "after the editor keyword sync.");
                Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineSolidCore),
                    "the committed line base must ship the Default strategy: _HAIRLINE_SOLID_CORE must be " +
                    "clear after the editor keyword sync.");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }

        // ── 3. ValidateMaterial is behaviour-neutral on the committed base (parity) ──
        [Test]
        public void FillShaderGUI_ValidateMaterial_OnCommittedBase_LeavesKeywordStateUnchanged()
        {
            // Parity tooth: running the editor keyword sync on a clone of the COMMITTED base fill .mat must
            // not FLIP any feature keyword — the runtime clone relies on the import-baked state, so a
            // derivation that disagreed with the asset would change how production renders.
            //
            // Asserted as before == after rather than against a hardcoded expected set. It used to assert
            // "empty", which was true only while the base happened to be opaque with no maps; when the base
            // legitimately became transparent (fills need _SURFACE_TYPE_TRANSPARENT or URP's OutputAlpha
            // discards the fragment alpha), that spelling failed even though the invariant it was named for
            // still held. Comparing to the asset's own state expresses the intent and survives the base's
            // look changing again.
            //
            // Uses the committed .mat rather than `new Material(shader)`, whose float/texture defaults are
            // import-state-dependent (a fresh material's `= "white"` 2D defaults read as non-null textures
            // once loaded, and its _Surface default proved unstable across shader reimports).
            var feature = new[]
            {
                ShaderKeywords.Emission, ShaderKeywords.NormalMap,
                ShaderKeywords.MetallicSpecGlossMap, ShaderKeywords.OcclusionMap,
                ShaderKeywords.SurfaceTypeTransparent, ShaderKeywords.AlphaTestOn,
            };

            var m = new Material(MapMaterialSetTestUtil.Load().FillMaterial);
            try
            {
                var before = new List<string>();
                foreach (var kw in feature)
                    if (m.IsKeywordEnabled(kw)) before.Add(kw);

                new FillShaderGUI().ValidateMaterial(m);

                var after = new List<string>();
                foreach (var kw in feature)
                    if (m.IsKeywordEnabled(kw)) after.Add(kw);

                CollectionAssert.AreEquivalent(before, after,
                    $"ValidateMaterial must not flip the committed base's feature keywords. " +
                    $"Before: [{string.Join(", ", before)}] After: [{string.Join(", ", after)}]");

                // The fill base is transparent by design — if this ever reads false, fill alpha (opacity,
                // fill-color alpha, fill-pattern masks) is silently discarded by OutputAlpha().
                Assert.Contains(ShaderKeywords.SurfaceTypeTransparent, after,
                    "the committed fill base must declare _SURFACE_TYPE_TRANSPARENT — without it URP forces " +
                    "the fragment alpha to 1 and every fill renders solid.");
            }
            finally
            {
                Object.DestroyImmediate(m);
            }
        }
    }
}