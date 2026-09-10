// Unlit rendering mode epic, stage 1 — Map/FillUnlit shader twin structural acceptance teeth.
//
// T1 (property-parity / silent-bind guard): every ShaderProperties.PropertyId that
//   MaterialFactory.BindFillPaintToApplier binds (MaterialFactory.cs:59-98) must (a) exist on the UNLIT
//   base material via Material.HasProperty AND (b) be a real UnityPerMaterial CBUFFER member of
//   Fill_UnlitInput.hlsl. Both halves are load-bearing and NEITHER alone suffices: HasProperty reads the
//   .shader Properties BLOCK, so a prop present there but absent (or #define-shadowed) in the CBUFFER
//   passes (a) yet the bound value never reaches the shader — the real silent-bind no-op. (This exact
//   defect once shipped: _Opacity kept in the Properties block but replaced by `#define _Opacity 1.0` in
//   the CBUFFER; HasProperty stayed true, opacity was silently pinned to 1. Check (b) is what catches it.)
// T2 (shared-vertex-layout structural): FillUnlit's forward Attributes semantics must be a SUBSET of what
//   StyledFillTileBuilder actually emits (its FillVertexDescriptors array) — the load-bearing invariant
//   that there is one mesh, one vertex layout, consumed by both the Lit and Unlit twins by construction.
// T3 (no-lighting structural): Fill_UnlitForwardPass.hlsl must reference none of SAMPLE_GI /
//   UniversalFragmentPBR / OUTPUT_SH4 — the textual proof that the fragment dropped lighting rather than
//   merely gating it behind an always-off keyword.
//
// Nothing SELECTS Map/FillUnlit at runtime yet (mode-selection plumbing is a later stage) — these teeth
// are the structural proof the twin is correct in isolation; visual confirmation is the maintainer's.
//
// Clean-room: structural/text-parse only, no MapLibre source referenced.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class MapFillUnlitMaterialTests
    {
        private const string ShaderName = "Map/FillUnlit";

        // ── T1 — property parity / silent-bind guard ────────────────────────────────────────────
        [Test]
        public void FillUnlit_BoundPaintProperties_ExistOnUnlitMaterial()
        {
            var shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null,
                $"Shader '{ShaderName}' not found — FillUnlit.shader missing or failed to compile.");

            var mat = new Material(shader);
            try
            {
                // The exact set MaterialFactory.BindFillPaintToApplier binds (re-derived by reading that
                // method, MaterialFactory.cs:59-98 — not copied from the stage brief). Each carries its
                // PropertyId (for HasProperty), its C# identifier (for messages), and its shader-name
                // string constant (for the CBUFFER-membership check). Opacity is the shared registry; the
                // rest are Fill-only. None of the seven is a texture, so all belong in UnityPerMaterial.
                var boundProps = new (int id, string name, string shaderName)[]
                {
                    (ShaderProperties.PropertyId.Opacity,                  nameof(ShaderProperties.PropertyId.Opacity),                  ShaderProperties.PropertyNames.Opacity),
                    (ShaderProperties.Fill.PropertyId.FillOutlineColor,    nameof(ShaderProperties.Fill.PropertyId.FillOutlineColor),    ShaderProperties.Fill.PropertyNames.FillOutlineColor),
                    (ShaderProperties.Fill.PropertyId.FillAntialias,       nameof(ShaderProperties.Fill.PropertyId.FillAntialias),       ShaderProperties.Fill.PropertyNames.FillAntialias),
                    (ShaderProperties.Fill.PropertyId.FillTranslate,       nameof(ShaderProperties.Fill.PropertyId.FillTranslate),       ShaderProperties.Fill.PropertyNames.FillTranslate),
                    (ShaderProperties.Fill.PropertyId.FillTranslateAnchor, nameof(ShaderProperties.Fill.PropertyId.FillTranslateAnchor), ShaderProperties.Fill.PropertyNames.FillTranslateAnchor),
                    (ShaderProperties.Fill.PropertyId.FillPattern,         nameof(ShaderProperties.Fill.PropertyId.FillPattern),         ShaderProperties.Fill.PropertyNames.FillPattern),
                    (ShaderProperties.Fill.PropertyId.PatternRect,         nameof(ShaderProperties.Fill.PropertyId.PatternRect),         ShaderProperties.Fill.PropertyNames.PatternRect),
                };

                // (a) Properties-block presence: a bind onto a property the shader doesn't declare at all
                // is a silent no-op. HasProperty reads the .shader Properties BLOCK.
                var missingProperty = new List<string>();
                foreach (var (id, name, _) in boundProps)
                    if (!mat.HasProperty(id))
                        missingProperty.Add(name);

                Assert.That(missingProperty, Is.Empty,
                    "Map/FillUnlit's Properties block is missing properties MaterialFactory.BindFillPaintToApplier " +
                    $"binds — a bind onto these would be a SILENT no-op: {string.Join(", ", missingProperty)}");

                // (b) CBUFFER membership: HasProperty passes on a Properties-block entry EVEN WHEN the
                // UnityPerMaterial member is absent or #define-shadowed — the shader then reads a literal,
                // not the bound value, a silent no-op (a) cannot see. Assert each bound prop is a real
                // CBUFFER member so the value the bind writes actually reaches the shader.
                HashSet<string> cbufferMembers = ShaderPropertyParser.ParseCbufferMembers(
                    ShaderPropertyParser.MapShaderPath("Fill_UnlitInput.hlsl"));

                var notInCbuffer = new List<string>();
                foreach (var (_, name, shaderName) in boundProps)
                    if (!cbufferMembers.Contains(shaderName))
                        notInCbuffer.Add($"{name} ({shaderName})");

                Assert.That(notInCbuffer, Is.Empty,
                    "Map/FillUnlit's UnityPerMaterial CBUFFER (Fill_UnlitInput.hlsl) is missing bound paint " +
                    "properties — a bind writes a material value the shader never reads (a dropped or " +
                    $"#define-shadowed member): {string.Join(", ", notInCbuffer)}. HasProperty cannot see this.");
            }
            finally { Object.DestroyImmediate(mat); }
        }

        // ── T2 — shared-vertex-layout structural ────────────────────────────────────────────────
        [Test]
        public void FillUnlitForwardPass_AttributesSemantics_AreSubsetOfBuilderEmittedStreams()
        {
            // The mesh-emitted semantic set — StyledFillTileBuilder's FillVertexDescriptors
            // (StyledFillTileBuilder.cs): Position, Normal, Tangent, Color, TexCoord0, TexCoord3.
            // Background reuses this same builder (BackgroundQuad), so the same set covers both.
            var emittedByBuilder = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "POSITION", "NORMAL", "TANGENT", "COLOR", "TEXCOORD0", "TEXCOORD3",
            };

            string path = ShaderPropertyParser.MapShaderPath("Fill_UnlitForwardPass.hlsl");
            var declared = ShaderPropertyParser.ParseStructSemantics(path, "Attributes");

            Assert.That(declared, Is.Not.Empty,
                "Parsed zero semantics from Map/FillUnlit's forward Attributes struct — regex or file drifted.");

            var extra = new List<string>();
            foreach (string semantic in declared)
                if (!emittedByBuilder.Contains(semantic))
                    extra.Add(semantic);

            Assert.That(extra, Is.Empty,
                "Map/FillUnlit's forward Attributes require semantics StyledFillTileBuilder never emits: " +
                $"{string.Join(", ", extra)}. The unlit twin must consume the SAME mesh the Lit twin does — " +
                "a vertex-layout fork is impossible by construction (unlit rendering mode epic §1).");
        }

        // ── T3 — no-lighting structural ─────────────────────────────────────────────────────────
        [Test]
        public void FillUnlitForwardPass_ReferencesNoLightingCalls()
        {
            string path = ShaderPropertyParser.MapShaderPath("Fill_UnlitForwardPass.hlsl");
            Assume.That(File.Exists(path), Is.True, $"{path} not found.");
            // Scan CODE only — the mirror's provenance header names these tokens to document their ABSENCE
            // ("no UniversalFragmentPBR/SAMPLE_GI — UniversalFragmentUnlit composes the colour instead"),
            // which is exactly the self-documenting prose a naive Contains() would false-positive on. Strip
            // comments first so only a real lighting CALL trips the tooth.
            string text = ShaderPropertyParser.StripHlslComments(File.ReadAllText(path));

            var forbidden = new[] { "SAMPLE_GI", "UniversalFragmentPBR", "OUTPUT_SH4" };
            var found = new List<string>();
            foreach (string token in forbidden)
                if (text.Contains(token))
                    found.Add(token);

            Assert.That(found, Is.Empty,
                $"Fill_UnlitForwardPass.hlsl references lighting call(s) it must not: {string.Join(", ", found)}. " +
                "The unlit fragment must drop lighting entirely, not merely guard it behind a keyword.");
        }
    }
}
