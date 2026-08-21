// Unlit rendering mode epic, stage 3 — Map/LineUnlit shader twin structural acceptance teeth.
//
// T1 (property-parity / silent-bind guard): every ShaderProperties.PropertyId that
//   MaterialFactory.BindLinePaintToApplier binds or directly sets (MaterialFactory.cs:259-320) must (a)
//   exist on the UNLIT base material via Material.HasProperty AND (b) be a real UnityPerMaterial CBUFFER
//   member of Line_UnlitInput.hlsl. Both halves are load-bearing and NEITHER alone suffices — see
//   MapFillUnlitMaterialTests' T1 for the exact defect this catches (a Properties-block entry surviving
//   while the CBUFFER member is dropped or #define-shadowed; HasProperty alone cannot see it).
// T2 (shared-vertex-layout structural): LineUnlit's forward pass consumes LineAttributes — the SAME struct
//   every line pass (Lit and Unlit) shares, defined once in Line_VertexExtrude.hlsl (reused VERBATIM) — so
//   this asserts THAT struct's semantics are a SUBSET of what StyledLineTileBuilder actually emits
//   (LineVertexDescriptors, StyledLineTileBuilder.cs) — the load-bearing invariant that there is one mesh,
//   one vertex layout, consumed by both twins by construction. UNLIKE Fill/FillExtrusion, the line builder
//   never emits TANGENT.
// T3 (no-lighting structural): Line_UnlitForwardPass.hlsl must reference none of SAMPLE_GI /
//   UniversalFragmentPBR / OUTPUT_SH4 — the textual proof that the fragment dropped lighting rather than
//   merely gating it behind an always-off keyword.
// T4 (AA-preserved, S3's signature tooth): line antialiasing is NOT lighting — it is the LineCoverage(...)
//   straddle/gap/blur/dash formula owned by the shared Line_VertexExtrude.hlsl — so unlike T3, the unlit
//   forward pass MUST reference it. Asserts (a) Line_UnlitForwardPass.hlsl's code calls LineCoverage( and
//   (b) LineUnlit.shader declares the same AA/hairline keyword pragmas the Lit twin's ForwardLit pass does
//   (_EDGE_ANTIALIASING_OFF, _HAIRLINE_SOLID_CORE, _HAIRLINE_HARD). RED-verify (a) by stubbing the call site
//   to a literal (e.g. `float coverage = 1.0;`) — the failure mode a shallow "flat unlit line" impl would
//   actually ship, since a flat fragment technically compiles and renders something plausible without ever
//   touching LineCoverage. RED-verify (b) by deleting one of the three keyword pragmas.
//
// Nothing SELECTS Map/LineUnlit at runtime yet (mode-selection plumbing is a later stage) — these teeth are
// the structural proof the twin is correct in isolation; visual AA/dash fidelity is the maintainer's.
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
    public class MapLineUnlitMaterialTests
    {
        private const string ShaderName = "Map/LineUnlit";

        // ── T1 — property parity / silent-bind guard ────────────────────────────────────────────
        [Test]
        public void LineUnlit_BoundPaintProperties_ExistOnUnlitMaterial()
        {
            var shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null,
                $"Shader '{ShaderName}' not found — LineUnlit.shader missing or failed to compile.");

            var mat = new Material(shader);
            try
            {
                // The exact set MaterialFactory.BindLinePaintToApplier binds or directly Sets (re-derived by
                // reading that method, MaterialFactory.cs:259-320 — not copied from the stage brief): the
                // shared BaseColor/Opacity, then the device-px family, then the two mode/dash flags it sets
                // directly (WidthIsPixels via mat.SetFloat; DashArray/DashCount via ApplyLineDashArray).
                var boundProps = new (int id, string name, string shaderName)[]
                {
                    (ShaderProperties.PropertyId.BaseColor,                nameof(ShaderProperties.PropertyId.BaseColor),                ShaderProperties.PropertyNames.BaseColor),
                    (ShaderProperties.PropertyId.Opacity,                  nameof(ShaderProperties.PropertyId.Opacity),                  ShaderProperties.PropertyNames.Opacity),
                    (ShaderProperties.Line.PropertyId.Width,               nameof(ShaderProperties.Line.PropertyId.Width),               ShaderProperties.Line.PropertyNames.Width),
                    (ShaderProperties.Line.PropertyId.WidthIsPixels,       nameof(ShaderProperties.Line.PropertyId.WidthIsPixels),       ShaderProperties.Line.PropertyNames.WidthIsPixels),
                    (ShaderProperties.Line.PropertyId.Blur,                nameof(ShaderProperties.Line.PropertyId.Blur),                ShaderProperties.Line.PropertyNames.Blur),
                    (ShaderProperties.Line.PropertyId.GapWidth,            nameof(ShaderProperties.Line.PropertyId.GapWidth),            ShaderProperties.Line.PropertyNames.GapWidth),
                    (ShaderProperties.Line.PropertyId.LineOffset,          nameof(ShaderProperties.Line.PropertyId.LineOffset),          ShaderProperties.Line.PropertyNames.LineOffset),
                    (ShaderProperties.Line.PropertyId.LineTranslate,       nameof(ShaderProperties.Line.PropertyId.LineTranslate),       ShaderProperties.Line.PropertyNames.LineTranslate),
                    (ShaderProperties.Line.PropertyId.LineTranslateAnchor, nameof(ShaderProperties.Line.PropertyId.LineTranslateAnchor), ShaderProperties.Line.PropertyNames.LineTranslateAnchor),
                    (ShaderProperties.Line.PropertyId.LinePattern,         nameof(ShaderProperties.Line.PropertyId.LinePattern),         ShaderProperties.Line.PropertyNames.LinePattern),
                    (ShaderProperties.Line.PropertyId.DashArray,           nameof(ShaderProperties.Line.PropertyId.DashArray),           ShaderProperties.Line.PropertyNames.DashArray),
                    (ShaderProperties.Line.PropertyId.DashCount,           nameof(ShaderProperties.Line.PropertyId.DashCount),           ShaderProperties.Line.PropertyNames.DashCount),
                };

                // (a) Properties-block presence: a bind onto a property the shader doesn't declare at all
                // is a silent no-op. HasProperty reads the .shader Properties BLOCK.
                var missingProperty = new List<string>();
                foreach (var (id, name, _) in boundProps)
                    if (!mat.HasProperty(id))
                        missingProperty.Add(name);

                Assert.That(missingProperty, Is.Empty,
                    "Map/LineUnlit's Properties block is missing properties MaterialFactory.BindLinePaintToApplier " +
                    $"binds — a bind onto these would be a SILENT no-op: {string.Join(", ", missingProperty)}");

                // (b) CBUFFER membership: HasProperty passes on a Properties-block entry EVEN WHEN the
                // UnityPerMaterial member is absent or #define-shadowed — the shader then reads a literal,
                // not the bound value, a silent no-op (a) cannot see. Assert each bound prop is a real
                // CBUFFER member so the value the bind writes actually reaches the shader.
                HashSet<string> cbufferMembers = ShaderPropertyParser.ParseCbufferMembers(
                    ShaderPropertyParser.MapShaderPath("Line_UnlitInput.hlsl"));

                var notInCbuffer = new List<string>();
                foreach (var (_, name, shaderName) in boundProps)
                    if (!cbufferMembers.Contains(shaderName))
                        notInCbuffer.Add($"{name} ({shaderName})");

                Assert.That(notInCbuffer, Is.Empty,
                    "Map/LineUnlit's UnityPerMaterial CBUFFER (Line_UnlitInput.hlsl) is missing bound paint " +
                    "properties — a bind writes a material value the shader never reads (a dropped or " +
                    $"#define-shadowed member): {string.Join(", ", notInCbuffer)}. HasProperty cannot see this.");
            }
            finally { Object.DestroyImmediate(mat); }
        }

        // ── T2 — shared-vertex-layout structural ────────────────────────────────────────────────
        [Test]
        public void LineUnlit_AttributesSemantics_AreSubsetOfBuilderEmittedStreams()
        {
            // The mesh-emitted semantic set — StyledLineTileBuilder's LineVertexDescriptors
            // (StyledLineTileBuilder.cs): Position, Normal, Color, TexCoord0, TexCoord1, TexCoord2. UNLIKE
            // Fill/FillExtrusion's builders, TANGENT is never emitted — the line derives its own tangent
            // frame in Line_VertexExtrude.hlsl instead (see that file's Line_VertexExtrude doc).
            var emittedByBuilder = new HashSet<string>(System.StringComparer.Ordinal)
            {
                "POSITION", "NORMAL", "COLOR", "TEXCOORD0", "TEXCOORD1", "TEXCOORD2",
            };

            // LineAttributes is defined ONCE, in Line_VertexExtrude.hlsl — every line pass (Lit and Unlit)
            // reuses it verbatim; LineUnlit's own forward-pass file does not redeclare it.
            string path = ShaderPropertyParser.MapShaderPath("Line_VertexExtrude.hlsl");
            var declared = ShaderPropertyParser.ParseStructSemantics(path, "LineAttributes");

            Assert.That(declared, Is.Not.Empty,
                "Parsed zero semantics from LineAttributes — regex or file drifted.");

            var extra = new List<string>();
            foreach (string semantic in declared)
                if (!emittedByBuilder.Contains(semantic))
                    extra.Add(semantic);

            Assert.That(extra, Is.Empty,
                $"LineAttributes requires semantics StyledLineTileBuilder never emits: {string.Join(", ", extra)}. " +
                "The unlit twin must consume the SAME mesh the Lit twin does — a vertex-layout fork is " +
                "impossible by construction (unlit rendering mode epic §1).");
        }

        // ── T3 — no-lighting structural ─────────────────────────────────────────────────────────
        [Test]
        public void LineUnlitForwardPass_ReferencesNoLightingCalls()
        {
            string path = ShaderPropertyParser.MapShaderPath("Line_UnlitForwardPass.hlsl");
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
                $"Line_UnlitForwardPass.hlsl references lighting call(s) it must not: {string.Join(", ", found)}. " +
                "The unlit fragment must drop lighting entirely, not merely guard it behind a keyword.");
        }

        // ── T4 — AA-preserved (S3's signature tooth) ────────────────────────────────────────────
        [Test]
        public void LineUnlit_PreservesLineAntialiasing()
        {
            // (a) The forward pass's CODE must call LineCoverage( — line antialiasing (straddle AA, gap-hole
            // cut, opt-in blur, dash coverage) lives entirely in that function (Line_VertexExtrude.hlsl,
            // reused verbatim); a flat/stubbed fragment would compile and render *something* without ever
            // reaching it, which is exactly the defect this tooth exists to catch.
            string forwardPassPath = ShaderPropertyParser.MapShaderPath("Line_UnlitForwardPass.hlsl");
            Assume.That(File.Exists(forwardPassPath), Is.True, $"{forwardPassPath} not found.");
            string forwardPassText = ShaderPropertyParser.StripHlslComments(File.ReadAllText(forwardPassPath));

            Assert.That(forwardPassText, Does.Contain("LineCoverage("),
                "Line_UnlitForwardPass.hlsl must call LineCoverage(...) — line AA is NOT lighting and must " +
                "survive the unlit fragment. A stubbed/hardcoded coverage value would compile and render " +
                "plausibly while silently dropping antialiasing, the gap-hole cut, line-blur and dashing.");

            // (b) The shader must declare the SAME AA/hairline keyword pragmas the Lit twin's ForwardLit
            // pass does — without them the vertex-stage extrusion and fragment-stage coverage formula can
            // never diverge from their compiled-out defaults, silently pinning every unlit line to one
            // hairline strategy regardless of what the style/material asks for.
            string shaderPath = ShaderPropertyParser.MapShaderPath("LineUnlit.shader");
            Assume.That(File.Exists(shaderPath), Is.True, $"{shaderPath} not found.");
            string shaderText = ShaderPropertyParser.StripHlslComments(File.ReadAllText(shaderPath));

            var requiredKeywordTokens = new[] { "_EDGE_ANTIALIASING_OFF", "_HAIRLINE_SOLID_CORE", "_HAIRLINE_HARD" };
            var missingKeywords = new List<string>();
            foreach (string token in requiredKeywordTokens)
                if (!shaderText.Contains(token))
                    missingKeywords.Add(token);

            Assert.That(missingKeywords, Is.Empty,
                "Map/LineUnlit is missing AA/hairline keyword pragma(s) the Lit twin declares: " +
                $"{string.Join(", ", missingKeywords)}. Without these the unlit twin's AA/hairline behaviour " +
                "cannot track the Lit twin's.");
        }
    }
}
