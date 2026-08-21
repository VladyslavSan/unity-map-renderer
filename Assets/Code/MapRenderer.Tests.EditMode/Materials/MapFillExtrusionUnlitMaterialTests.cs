// Unlit rendering mode epic, stage 2 — Map/FillExtrusionUnlit shader twin structural acceptance teeth.
//
// T1 (property-parity / silent-bind guard): every ShaderProperties.PropertyId that
//   MaterialFactory.BindFillExtrusionPaintToApplier binds (MaterialFactory.cs:148-179) must (a) exist on
//   the UNLIT base material via Material.HasProperty AND (b) be a real UnityPerMaterial CBUFFER member of
//   FillExtrusion_UnlitInput.hlsl. Both halves are load-bearing and NEITHER alone suffices — see
//   MapFillUnlitMaterialTests' T1 for the exact defect this catches (a Properties-block entry surviving
//   while the CBUFFER member is dropped or #define-shadowed; HasProperty alone cannot see it).
// T2 (shared-vertex-layout structural): FillExtrusionUnlit's forward Attributes semantics must be a SUBSET
//   of what StyledFillExtrusionTileBuilder actually emits (its VertexDescriptors array,
//   StyledFillExtrusionTileBuilder.cs:114-121) — the load-bearing invariant that there is one mesh, one
//   vertex layout, consumed by both the Lit and Unlit twins by construction.
// T3 (no-lighting structural): FillExtrusion_UnlitForwardPass.hlsl must reference none of SAMPLE_GI /
//   UniversalFragmentPBR / OUTPUT_SH4 — the textual proof that the fragment dropped lighting rather than
//   merely gating it behind an always-off keyword.
// T4 (depth-regime): the elevated-3D contract — ZWrite ON after FillExtrusionTweaker.ApplyElevatedContract —
//   must survive on the UNLIT base material exactly as it does on the Lit one (MaterialTweakerTests'
//   FillExtrusionTweaker_ApplyElevatedContract_SetsDepthWriteOpaqueAndWhiteIdentity is the Lit-shader
//   sibling of this tooth). Buildings must keep occupying the depth buffer under unlit — Unlit ≠ 2D.
//   SCOPE NOTE: this only proves the shared tweaker still reaches an unlit-shader material — it CANNOT
//   discriminate a shader that stops binding _ZWrite to the ShaderLab ZWrite command (Unity's Material
//   system exposes _ZWrite/_Cull/_SrcBlend/etc. as material properties regardless of Properties-block
//   declaration, so HasProperty(_ZWrite) cannot be made false by editing this shader — confirmed
//   empirically, see the dev report). T4b below is the tooth that actually catches THAT regression.
// T4b (render-state binding, structural): FillExtrusionUnlit.shader's forward-pass render state must bind
//   ZWrite to the [_ZWrite] material property, not a hardcoded literal — the thing T4 cannot see. A
//   hardcoded `ZWrite Off` would leave every Material-API check in T4 green while the GPU never receives
//   the elevated contract's ZWrite-On assertion at all.
//
// Nothing SELECTS Map/FillExtrusionUnlit at runtime yet (mode-selection plumbing is a later stage) — these
// teeth are the structural proof the twin is correct in isolation; visual confirmation is the maintainer's.
//
// Clean-room: structural/text-parse only, no MapLibre source referenced.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class MapFillExtrusionUnlitMaterialTests
    {
        private const string ShaderName = "Map/FillExtrusionUnlit";

        // ── T1 — property parity / silent-bind guard ────────────────────────────────────────────
        [Test]
        public void FillExtrusionUnlit_BoundPaintProperties_ExistOnUnlitMaterial()
        {
            var shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null,
                $"Shader '{ShaderName}' not found — FillExtrusionUnlit.shader missing or failed to compile.");

            var mat = new Material(shader);
            try
            {
                // The exact set MaterialFactory.BindFillExtrusionPaintToApplier binds (re-derived by reading
                // that method, MaterialFactory.cs:148-179 — not copied from the stage brief).
                var boundProps = new (int id, string name, string shaderName)[]
                {
                    (ShaderProperties.PropertyId.BaseColor,                                nameof(ShaderProperties.PropertyId.BaseColor),                                ShaderProperties.PropertyNames.BaseColor),
                    (ShaderProperties.PropertyId.Opacity,                                  nameof(ShaderProperties.PropertyId.Opacity),                                  ShaderProperties.PropertyNames.Opacity),
                    (ShaderProperties.FillExtrusion.PropertyId.ExtrusionHeight,             nameof(ShaderProperties.FillExtrusion.PropertyId.ExtrusionHeight),             ShaderProperties.FillExtrusion.PropertyNames.ExtrusionHeight),
                    (ShaderProperties.FillExtrusion.PropertyId.ExtrusionBase,               nameof(ShaderProperties.FillExtrusion.PropertyId.ExtrusionBase),               ShaderProperties.FillExtrusion.PropertyNames.ExtrusionBase),
                    (ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslate,       nameof(ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslate),       ShaderProperties.FillExtrusion.PropertyNames.FillExtrusionTranslate),
                    (ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslateAnchor, nameof(ShaderProperties.FillExtrusion.PropertyId.FillExtrusionTranslateAnchor), ShaderProperties.FillExtrusion.PropertyNames.FillExtrusionTranslateAnchor),
                };

                // (a) Properties-block presence: a bind onto a property the shader doesn't declare at all
                // is a silent no-op. HasProperty reads the .shader Properties BLOCK.
                var missingProperty = new List<string>();
                foreach (var (id, name, _) in boundProps)
                    if (!mat.HasProperty(id))
                        missingProperty.Add(name);

                Assert.That(missingProperty, Is.Empty,
                    "Map/FillExtrusionUnlit's Properties block is missing properties " +
                    "MaterialFactory.BindFillExtrusionPaintToApplier binds — a bind onto these would be a " +
                    $"SILENT no-op: {string.Join(", ", missingProperty)}");

                // (b) CBUFFER membership: HasProperty passes on a Properties-block entry EVEN WHEN the
                // UnityPerMaterial member is absent or #define-shadowed — the shader then reads a literal,
                // not the bound value, a silent no-op (a) cannot see. Assert each bound prop is a real
                // CBUFFER member so the value the bind writes actually reaches the shader.
                HashSet<string> cbufferMembers = ShaderPropertyParser.ParseCbufferMembers(
                    ShaderPropertyParser.MapShaderPath("FillExtrusion_UnlitInput.hlsl"));

                var notInCbuffer = new List<string>();
                foreach (var (_, name, shaderName) in boundProps)
                    if (!cbufferMembers.Contains(shaderName))
                        notInCbuffer.Add($"{name} ({shaderName})");

                Assert.That(notInCbuffer, Is.Empty,
                    "Map/FillExtrusionUnlit's UnityPerMaterial CBUFFER (FillExtrusion_UnlitInput.hlsl) is " +
                    "missing bound paint properties — a bind writes a material value the shader never reads " +
                    $"(a dropped or #define-shadowed member): {string.Join(", ", notInCbuffer)}. " +
                    "HasProperty cannot see this.");
            }
            finally { Object.DestroyImmediate(mat); }
        }

        // ── T2 — shared-vertex-layout structural ────────────────────────────────────────────────
        [Test]
        public void FillExtrusionUnlitForwardPass_AttributesSemantics_AreSubsetOfBuilderEmittedStreams()
        {
            // The mesh-emitted semantic set — StyledFillExtrusionTileBuilder's VertexDescriptors
            // (StyledFillExtrusionTileBuilder.cs:114-121): Position, Normal, Tangent, Color, TexCoord3,
            // TexCoord4. UNLIKE Fill's builder, TEXCOORD0-2 are deliberately NOT supplied (that file's own
            // comment) — the unlit forward pass must not require them either.
            var emittedByBuilder = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "POSITION", "NORMAL", "TANGENT", "COLOR", "TEXCOORD3", "TEXCOORD4",
            };

            string path = ShaderPropertyParser.MapShaderPath("FillExtrusion_UnlitForwardPass.hlsl");
            var declared = ShaderPropertyParser.ParseStructSemantics(path, "Attributes");

            Assert.That(declared, Is.Not.Empty,
                "Parsed zero semantics from Map/FillExtrusionUnlit's forward Attributes struct — regex or file drifted.");

            var extra = new List<string>();
            foreach (string semantic in declared)
                if (!emittedByBuilder.Contains(semantic))
                    extra.Add(semantic);

            Assert.That(extra, Is.Empty,
                "Map/FillExtrusionUnlit's forward Attributes require semantics StyledFillExtrusionTileBuilder " +
                $"never emits: {string.Join(", ", extra)}. The unlit twin must consume the SAME mesh the Lit " +
                "twin does — a vertex-layout fork is impossible by construction (unlit rendering mode epic §1).");
        }

        // ── T3 — no-lighting structural ─────────────────────────────────────────────────────────
        [Test]
        public void FillExtrusionUnlitForwardPass_ReferencesNoLightingCalls()
        {
            string path = ShaderPropertyParser.MapShaderPath("FillExtrusion_UnlitForwardPass.hlsl");
            Assume.That(File.Exists(path), Is.True, $"{path} not found.");
            // Scan CODE only — the mirror's provenance header names these tokens to document their ABSENCE,
            // which is exactly the self-documenting prose a naive Contains() would false-positive on. Strip
            // comments first so only a real lighting CALL trips the tooth.
            string text = ShaderPropertyParser.StripHlslComments(File.ReadAllText(path));

            var forbidden = new[] { "SAMPLE_GI", "UniversalFragmentPBR", "OUTPUT_SH4" };
            var found = new List<string>();
            foreach (string token in forbidden)
                if (text.Contains(token))
                    found.Add(token);

            Assert.That(found, Is.Empty,
                $"FillExtrusion_UnlitForwardPass.hlsl uses full-lighting call(s) it must not: {string.Join(", ", found)}. " +
                "The unlit fragment must not run URP's PBR/GI/SH path (SAMPLE_GI / UniversalFragmentPBR / " +
                "OUTPUT_SH4). A cheap direction-only half-Lambert off the main light IS allowed — and is pinned " +
                "by " + nameof(FillExtrusionUnlitForwardPass_ShadesFacesAgainstMainLight) + ".");
        }

        // ── T5 — the unlit fill-extrusion pass shades faces against the scene's main light ──────────
        // Without this the wall + roof faces render the identical flat colour (a solid block). The pass must
        // carry the world face normal AND modulate albedo by a half-Lambert off the main light's DIRECTION.
        // Structural (headless can't render fragments); RED-verify by deleting the `albedo *=` line or the
        // normalWS carry. Scans CODE only (StripHlslComments) so the shading's own doc comments can't satisfy
        // it. Counterpart to the bootstrap half (DirectionalLightBootstrapTests, which guarantees the light
        // exists); this pins that the shader actually consumes it.
        [Test]
        public void FillExtrusionUnlitForwardPass_ShadesFacesAgainstMainLight()
        {
            string code = ShaderPropertyParser.StripHlslComments(
                File.ReadAllText(ShaderPropertyParser.MapShaderPath("FillExtrusion_UnlitForwardPass.hlsl")));

            Assert.That(code, Does.Contain("output.normalWS = TransformObjectToWorldNormal(input.normalOS)"),
                "Vertex must carry the world-space face normal to the fragment (the shading's input).");
            Assert.That(code, Does.Contain("GetMainLight()"),
                "Fragment must sample the scene's main directional light for the face-shading direction.");
            Assert.That(code, Does.Contain("saturate(dot(normalize(input.normalWS), mainLight.direction))"),
                "Fragment must form an N·L term from the face normal and the main-light direction (half-Lambert base).");
            Assert.That(code, Does.Contain("albedo *= ndl * 0.5h + 0.5h"),
                "Fragment must modulate albedo by the half-Lambert (ndl*0.5+0.5) so faces at different " +
                "orientations differ — otherwise unlit 3D buildings read as flat solid blocks.");
        }

        // ── T4 — depth-regime: the elevated-3D contract must survive on the UNLIT base too ──────
        // NOTE: RED-verified against the shared FillExtrusionTweaker.ApplyElevatedContract (temporarily
        // disabling its SetDepthWrite call), NOT against this shader — see the file header's scope note.
        // Editing FillExtrusionUnlit.shader's Properties block (or even removing every [_ZWrite] reference
        // from its ShaderLab commands) left this test green: Unity's Material system exposes the
        // render-state property names (_ZWrite/_Cull/_SrcBlend/…) regardless of the shader's own
        // declarations, so HasProperty(_ZWrite) cannot be made false from this file. T4b is the tooth that
        // actually discriminates a broken _ZWrite binding.
        [Test]
        public void FillExtrusionUnlit_KeepsZWriteOn_AfterElevatedContract()
        {
            var shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null,
                $"Shader '{ShaderName}' not found — FillExtrusionUnlit.shader missing or failed to compile.");

            var mat = new Material(shader);
            try
            {
                // Simulate a base left in the flat/transparent state (as the Lit-shader sibling tooth does,
                // MaterialTweakerTests.FillExtrusionTweaker_ApplyElevatedContract_SetsDepthWriteOpaqueAndWhiteIdentity)
                // — the elevated contract must OVERRIDE it, on the unlit material exactly as it does on Lit's.
                mat.SetFloat(ShaderProperties.PropertyNames.ZWrite, 0f);

                FillExtrusionTweaker.ApplyElevatedContract(mat);

                Assert.That((int)mat.GetFloat(ShaderProperties.PropertyNames.ZWrite), Is.EqualTo((int)DepthWrite.On),
                    "Map/FillExtrusionUnlit must keep ZWrite ON after the elevated-3D contract — buildings must " +
                    "occupy the depth buffer to self-occlude even under unlit rendering (Unlit ≠ 2D).");
            }
            finally { Object.DestroyImmediate(mat); }
        }

        // ── T4b — render-state binding, structural: catches what T4 (Material-API only) cannot see ──
        [Test]
        public void FillExtrusionUnlit_ForwardRenderState_BindsZWriteToMaterialProperty()
        {
            string path = ShaderPropertyParser.MapShaderPath("FillExtrusionUnlit.shader");
            Assume.That(File.Exists(path), Is.True, $"{path} not found.");
            string text = ShaderPropertyParser.StripHlslComments(File.ReadAllText(path));

            Assert.That(text, Does.Contain("ZWrite [_ZWrite]"),
                "Map/FillExtrusionUnlit's forward-pass render state must bind ZWrite to the [_ZWrite] " +
                "material property. A hardcoded literal (e.g. 'ZWrite Off') would leave " +
                "FillExtrusionTweaker.ApplyElevatedContract's SetDepthWrite call a silent no-op on the GPU " +
                "even though every Material.HasProperty/GetFloat check in T4 stays green — Unity exposes " +
                "_ZWrite as a settable material property regardless of whether the shader's render state " +
                "actually consumes it.");
        }
    }
}
