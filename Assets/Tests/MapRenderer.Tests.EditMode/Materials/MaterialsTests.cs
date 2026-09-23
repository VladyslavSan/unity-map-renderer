// Materials/MaterialsTests.cs — material asset templates, unlit shader twins, editor shader GUI
// hierarchy, render-state/tweaker contracts, and paint-color carrier composition.
//
// MapMaterialSetValidationTests.cs stays its own file: its `using System;` (for InvalidOperationException)
// would collide with the bare `Object.DestroyImmediate` calls here (System.Object vs UnityEngine.Object,
// CS0104) — see docs/conventions-short.md's "Plain-import collisions" note.
//
// Contents:
//   MapFillMaterialTests                — the committed MapFill.mat/MapLine.mat template
//                                          assets (shader reference, paint + URP Lit properties).
//   MapFillUnlitMaterialTests           — Map/FillUnlit shader twin
//                                          structural acceptance teeth.
//   MapFillExtrusionUnlitMaterialTests  — Map/FillExtrusionUnlit shader
//                                          twin structural acceptance teeth.
//   MapLineUnlitMaterialTests           — Map/LineUnlit shader twin
//                                          structural acceptance teeth.
//   MapShaderGUITests                   — the map material inspectors subclass the raw
//                                          UnityEditor.ShaderGUI hierarchy directly.
//   MaterialExtensionsTests             — MaterialExtensions.CloneWithParent contract (independent clone,
//                                          copied values, Editor variant link).
//   MaterialRenderStateTests            — the typed render-state layer (ZWrite/ZTest/Cull/Blend) round-trips
//                                          onto the underlying ShaderLab int properties.
//   MaterialTweakerTests                — runtime painter/elevated-3D contracts plus the editor
//                                          keyword sync (ValidateMaterial).
//   PaintColorSingleApplyTests          — a paint colour has exactly one carrier (uniform xor vertex
//                                          stream); the fragment must not apply it twice.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Editor;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using Color = UnityEngine.Color;
using Line = MapRenderer.Core.Style.Line;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using FillMaterialTweaker = MapRenderer.Unity.Rendering.Materials.FillTweaker;
using LineMaterialTweaker = MapRenderer.Unity.Rendering.Materials.LineTweaker;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MapRenderer.Tests.Materials
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // MapFillMaterialTests — committed MapFill.mat/MapLine.mat template asset acceptance
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MapFillMaterialTests
    {
        private const string MatPath     = "Assets/Code/MapRenderer.Unity/Materials/Map/Fill/Lit/Fill.mat";
        private const string ShaderName  = "Map/Fill";
        private const string LineMatPath = "Assets/Code/MapRenderer.Unity/Materials/Map/Line/Lit/Line.mat";

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
                "The committed template material asset is required.");
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
                "Check that the GUID in MapFill.mat matches Assets/Code/MapRenderer.Unity/Shaders/Map/Fill/Fill.shader.meta.");
        }

        [Test]
        public void MapFillMat_HasBaseColorProperty()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            Assume.That(mat, Is.Not.Null, "MapFill.mat not found.");

            // _BaseColor is the map paint property (MapLibre-style fill-color knob).
            // Named _BaseColor, not _Color, so URP's legacy _BaseColor alias has no bare _Color to
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
            Assert.That(metallic, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f),
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
            Assert.That(mat.HasProperty("_BaseColor"), Is.True);
        }

        [Test]
        public void MapLineMat_ResolvesToTransparentQueue()
        {
            // Regression guard. The line is transparent
            // (lit rendering — Queue=Transparent>=2501 drives the
            // painter's-algorithm coplanar fill/line ordering; ZWrite Off). With no URP
            // BaseShaderGUI, the queue is NOT auto-resolved from _Surface/_QueueControl on import
            // (our raw-ShaderGUI ValidateMaterial only syncs keywords — it never touches renderQueue).
            // The transparent queue now comes from MapLine.mat's serialized custom render queue (3000)
            // and the SubShader's Queue=Transparent tag. Material.renderQueue returns the resolved value,
            // so this asserts the import outcome directly: a fresh batch import keeps the line transparent.
            var mat = AssetDatabase.LoadAssetAtPath<Material>(LineMatPath);
            Assume.That(mat, Is.Not.Null, "MapLine.mat not found — run MapLineMat_Exists first.");

            Assert.That(mat.renderQueue, Is.GreaterThanOrEqualTo(2501),
                $"MapLine.mat must resolve to the Transparent render queue (>=2501) after a fresh "      +
                $"batch import, got {mat.renderQueue}. The queue comes from the material's "   +
                "serialized custom render queue (3000) + the Line SubShader Queue=Transparent tag — "    +
                "the raw-ShaderGUI no longer recomputes it. A value of 2000 means the custom queue was " +
                "lost.");
        }
#endif
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapFillUnlitMaterialTests — Map/FillUnlit twin
    // ───────────────────────────────────────────────────────────────────────────────────

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
    //   that there is one mesh, one vertex layout, consumed by both the Lit and Unlit twins.
    // T3 (no-lighting structural): Fill_UnlitForwardPass.hlsl must reference none of SAMPLE_GI /
    //   UniversalFragmentPBR / OUTPUT_SH4 — the textual proof that the fragment dropped lighting rather than
    //   merely gating it behind an always-off keyword.
    //
    // Nothing SELECTS Map/FillUnlit at runtime yet (mode-selection plumbing is a later stage) — these teeth
    // are the structural proof the twin is correct in isolation; visual confirmation is the maintainer's.
    //
    // Clean-room: structural/text-parse only, no MapLibre source referenced.
    [TestFixture]
    public class MapFillUnlitMaterialTests : BaseTestFixture
    {
        private const string ShaderName = "Map/FillUnlit";

        // ── T1 — property parity / silent-bind guard ────────────────────────────────────────────
        [Test]
        public void FillUnlit_BoundPaintProperties_ExistOnUnlitMaterial()
        {
            var shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null,
                $"Shader '{ShaderName}' not found — FillUnlit.shader missing or failed to compile.");

            var mat = Track(new Material(shader));
            // The exact set MaterialFactory.BindFillPaintToApplier binds (re-derived by reading that
            // method, MaterialFactory.cs:59-104 — not copied from the stage brief). Each carries its
            // PropertyId (for HasProperty), its C# identifier (for messages), and its shader-name
            // string constant (for the CBUFFER-membership check). Opacity and BaseColor are the shared
            // registry; the rest are Fill-only. None of the eight is a texture, so all belong in
            // UnityPerMaterial.
            var boundProps = new (int id, string name, string shaderName)[]
            {
                (ShaderProperties.PropertyId.BaseColor,                nameof(ShaderProperties.PropertyId.BaseColor),                ShaderProperties.PropertyNames.BaseColor),
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
                "a vertex-layout fork is impossible by construction (unlit rendering mode).");
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapFillExtrusionUnlitMaterialTests — FillExtrusionUnlit twin
    // ───────────────────────────────────────────────────────────────────────────────────

    // T1 (property-parity / silent-bind guard): every ShaderProperties.PropertyId that
    //   MaterialFactory.BindFillExtrusionPaintToApplier binds (MaterialFactory.cs:148-179) must (a) exist on
    //   the UNLIT base material via Material.HasProperty AND (b) be a real UnityPerMaterial CBUFFER member of
    //   FillExtrusion_UnlitInput.hlsl. Both halves are load-bearing and NEITHER alone suffices — see
    //   MapFillUnlitMaterialTests' T1 for the exact defect this catches (a Properties-block entry surviving
    //   while the CBUFFER member is dropped or #define-shadowed; HasProperty alone cannot see it).
    // T2 (shared-vertex-layout structural): FillExtrusionUnlit's forward Attributes semantics must be a SUBSET
    //   of what StyledFillExtrusionTileBuilder actually emits (its VertexDescriptors array,
    //   StyledFillExtrusionTileBuilder.cs:114-121) — the load-bearing invariant that there is one mesh, one
    //   vertex layout, consumed by both the Lit and Unlit twins.
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
    [TestFixture]
    public class MapFillExtrusionUnlitMaterialTests : BaseTestFixture
    {
        private const string ShaderName = "Map/FillExtrusionUnlit";

        // ── T1 — property parity / silent-bind guard ────────────────────────────────────────────
        [Test]
        public void FillExtrusionUnlit_BoundPaintProperties_ExistOnUnlitMaterial()
        {
            var shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null,
                $"Shader '{ShaderName}' not found — FillExtrusionUnlit.shader missing or failed to compile.");

            var mat = Track(new Material(shader));
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

        // ── T2 — shared-vertex-layout structural ────────────────────────────────────────────────
        [Test]
        public void FillExtrusionUnlitForwardPass_AttributesSemantics_AreSubsetOfBuilderEmittedStreams()
        {
            // The mesh-emitted semantic set — StyledFillExtrusionTileBuilder's VertexDescriptors
            // (StyledFillExtrusionTileBuilder.cs:114-121): Position, Normal, Tangent, Color, TexCoord3,
            // TexCoord4. UNLIKE Fill's builder, TEXCOORD0-2 are NOT supplied (that file's own
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
                "twin does — a vertex-layout fork is impossible by construction (unlit rendering mode).");
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

            var mat = Track(new Material(shader));
            // Simulate a base left in the flat/transparent state (as the Lit-shader sibling tooth does,
            // MaterialTweakerTests.FillExtrusionTweaker_ApplyElevatedContract_SetsDepthWriteOpaqueAndWhiteIdentity)
            // — the elevated contract must OVERRIDE it, on the unlit material exactly as it does on Lit's.
            mat.SetFloat(ShaderProperties.PropertyNames.ZWrite, 0f);

            FillExtrusionTweaker.ApplyElevatedContract(mat);

            Assert.That((int)mat.GetFloat(ShaderProperties.PropertyNames.ZWrite), Is.EqualTo((int)DepthWrite.On),
                "Map/FillExtrusionUnlit must keep ZWrite ON after the elevated-3D contract — buildings must " +
                "occupy the depth buffer to self-occlude even under unlit rendering (Unlit ≠ 2D).");
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapLineUnlitMaterialTests — Map/LineUnlit twin
    // ───────────────────────────────────────────────────────────────────────────────────

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
    //   one vertex layout, consumed by both twins. UNLIKE Fill/FillExtrusion, the line builder
    //   never emits TANGENT.
    // T3 (no-lighting structural): Line_UnlitForwardPass.hlsl must reference none of SAMPLE_GI /
    //   UniversalFragmentPBR / OUTPUT_SH4 — the textual proof that the fragment dropped lighting rather than
    //   merely gating it behind an always-off keyword.
    // T4 (AA-preserved): line antialiasing is NOT lighting — it is the LineCoverage(...)
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
    [TestFixture]
    public class MapLineUnlitMaterialTests : BaseTestFixture
    {
        private const string ShaderName = "Map/LineUnlit";

        // ── T1 — property parity / silent-bind guard ────────────────────────────────────────────
        [Test]
        public void LineUnlit_BoundPaintProperties_ExistOnUnlitMaterial()
        {
            var shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null,
                $"Shader '{ShaderName}' not found — LineUnlit.shader missing or failed to compile.");

            var mat = Track(new Material(shader));
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
                "impossible by construction (unlit rendering mode).");
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

        // ── T4 — AA-preserved ───────────────────────────────────────────────────────────────────
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // MapShaderGUITests — the map material inspectors subclass the raw ShaderGUI directly
    // ───────────────────────────────────────────────────────────────────────────────────

    // STRUCTURAL — the map material inspectors subclass the RAW UnityEditor.ShaderGUI.
    //
    // There is no UCL-derived MapLitShaderGUI: the inspectors subclass the raw ShaderGUI directly (no
    // URP editor dependency — the test asmdef does not reference Unity.RenderPipelines.Universal.Editor).
    // The inspector LAYOUT itself is a manual in-editor check; this only pins the type hierarchy.
    [TestFixture]
    public class MapShaderGUITests
    {
#if UNITY_EDITOR
        [Test]
        public void BaseShaderGUI_SubclassesRawShaderGUI_Directly()
        {
            Assert.AreEqual(typeof(ShaderGUI), typeof(MapRenderer.Unity.Editor.BaseShaderGUI).BaseType,
                "Our BaseShaderGUI must subclass the RAW UnityEditor.ShaderGUI directly — it is our own " +
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
#endif
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MaterialExtensionsTests — MaterialExtensions.CloneWithParent contract
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Contract for <see cref="MaterialExtensions.CloneWithParent"/> — the per-layer material cloning
    /// primitive behind <see cref="MapMaterialSet"/>. A clone must be an independent Material
    /// that copies the source's values; in the Editor it must also be a Material Variant of the source so
    /// live base-material edits propagate during Play.
    /// </summary>
    public class MaterialExtensionsTests : BaseTestFixture
    {
        private static Material MakeMaterial()
        {
            // Use the live line shader (transparent) if present, else any available shader — the test only
            // needs a concrete material; it asserts cloning behaviour, not shader specifics.
            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            Assert.IsNotNull(shader, "No shader available to construct a test material.");
            return new Material(shader);
        }

        [Test]
        public void CloneWithParent_ReturnsDistinctMaterial_WithSameShader()
        {
            var src = Track(MakeMaterial());
            var clone = Track(src.CloneWithParent());

            Assert.AreNotSame(src, clone, "Clone must be a new Material instance, not the source.");
            Assert.AreEqual(src.shader, clone.shader, "Clone must keep the source's shader.");
        }

        [Test]
        public void CloneWithParent_CopiesSourcePropertyValues()
        {
            var src = Track(MakeMaterial());
            const string prop = "_Blur";
            if (src.HasProperty(prop))
                src.SetFloat(prop, 0.42f);

            var clone = Track(src.CloneWithParent());

            if (src.HasProperty(prop))
                Assert.AreEqual(0.42f, clone.GetFloat(prop), 1e-5f,
                    "Clone must copy the source's current property values (new Material(source) semantics).");
        }

#if UNITY_EDITOR
        [Test]
        public void CloneWithParent_InEditor_LinksCloneAsVariantOfSource()
        {
            var src = Track(MakeMaterial());
            var clone = Track(src.CloneWithParent());

            Assert.AreEqual(src, clone.parent,
                "In the Editor the clone must be a Material Variant of the source so base edits propagate live.");
        }
#endif
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MaterialRenderStateTests — the typed render-state layer round-trips onto ShaderLab props
    // ───────────────────────────────────────────────────────────────────────────────────

    // The typed render-state layer maps Unity rendering enums → the underlying ShaderLab
    // int properties (_ZWrite/_ZTest/_Cull/_SrcBlend/_DstBlend/_BlendOp). Pure property round-trip on a
    // Map/Fill material; no GUI, no scene.
    [TestFixture]
    public class MaterialRenderStateTests
    {
        private Material _mat;

        [SetUp]
        public void SetUp()
        {
            var shader = Shader.Find("Map/Fill");
            Assert.IsNotNull(shader, "Map/Fill shader must be present.");
            _mat = new Material(shader);
        }

        [TearDown]
        public void TearDown()
        {
            if (_mat != null) Object.DestroyImmediate(_mat);
        }

        [Test]
        public void SetDepthWrite_WritesZWriteInt_AndRoundTrips()
        {
            _mat.SetDepthWrite(DepthWrite.Off);
            Assert.AreEqual(0f, _mat.GetFloat(ShaderProperties.PropertyNames.ZWrite));
            _mat.SetDepthWrite(DepthWrite.On);
            Assert.AreEqual(1f, _mat.GetFloat(ShaderProperties.PropertyNames.ZWrite));
            Assert.AreEqual(DepthWrite.On, _mat.GetDepthWrite());
        }

        [Test]
        public void SetDepthTest_WritesZTestInt_AndRoundTrips()
        {
            _mat.SetDepthTest(CompareFunction.LessEqual);
            Assert.AreEqual((int)CompareFunction.LessEqual, (int)_mat.GetFloat(ShaderProperties.PropertyNames.ZTest));
            Assert.AreEqual(CompareFunction.LessEqual, _mat.GetDepthTest());
            _mat.SetDepthTest(CompareFunction.Always);
            Assert.AreEqual(CompareFunction.Always, _mat.GetDepthTest());
        }

        [Test]
        public void SetCull_WritesCullInt_AndRoundTrips()
        {
            _mat.SetCull(CullMode.Off);
            Assert.AreEqual((int)CullMode.Off, (int)_mat.GetFloat(ShaderProperties.PropertyNames.CullMode));
            _mat.SetCull(CullMode.Back);
            Assert.AreEqual(CullMode.Back, _mat.GetCull());
        }

        [Test]
        public void SetBlend_WritesSrcDstInts()
        {
            _mat.SetBlend(BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
            Assert.AreEqual((int)BlendMode.SrcAlpha, (int)_mat.GetFloat(ShaderProperties.PropertyNames.SrcBlend));
            Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, (int)_mat.GetFloat(ShaderProperties.PropertyNames.DstBlend));
        }

    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MaterialTweakerTests — runtime painter/elevated contracts + editor keyword sync
    // ───────────────────────────────────────────────────────────────────────────────────

    // The split of material setup into runtime vs editor:
    //   1. RUNTIME tweakers (Fill/Line) own only the render-state contract: ApplyPainterContract sets depth +
    //      per-type blend + white colour identity from scratch.
    //   2. EDITOR keyword sync lives in the shader GUIs' ValidateMaterial (BaseShaderGUI + LitShaderGUI), the
    //      clean-room counterpart of URP's SetMaterialKeywords. It derives shader-feature keywords from the
    //      MATERIAL's property values (never a MaterialProperty) and runs the editor-only FixupEmissiveFlag.
    //   3. On the committed .mat baseline (no maps, black emission, opaque) the derived keyword set is EMPTY —
    //      proving the keyword sync is behaviour-neutral (parity).
    [TestFixture]
    public class MaterialTweakerTests : BaseTestFixture
    {
        private static Material NewFill() => new Material(Shader.Find("Map/Fill"));
        private static Material NewLine() => new Material(Shader.Find("Map/Line"));
        private static Material NewFillExtrusion() => new Material(Shader.Find("Map/FillExtrusion"));

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
            var m = Track(NewFill());
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

        [Test]
        public void LineTweaker_ApplyPainterContract_SetsDepthOffAndWhiteIdentity()
        {
            var m = Track(NewLine());
            m.SetFloat(ShaderProperties.PropertyNames.ZWrite, 1f);
            LineMaterialTweaker.ApplyPainterContract(m);
            Assert.AreEqual(0f, m.GetFloat(ShaderProperties.PropertyNames.ZWrite), "painter contract → ZWrite off");
            Assert.AreEqual((int)BlendMode.SrcAlpha, (int)m.GetFloat(ShaderProperties.PropertyNames.SrcBlend),
                "line contract → straight-alpha SrcAlpha blend (NOT the premultiplied One the .mat may carry)");
            Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, (int)m.GetFloat(ShaderProperties.PropertyNames.DstBlend),
                "line contract → straight-alpha OneMinusSrcAlpha blend");
            AssertWhite(m.GetColor(ShaderProperties.PropertyNames.BaseColor), "_BaseColor");
        }

        // ── 1b. Elevated-3D contract: fill-extrusion is opaque + depth-writing, NOT the flat painter state ──
        // These two are the guard: fill-extrusion is the first geometry that must occupy the depth buffer
        // (so buildings — and a single building's own near/far walls, one mesh, unsortable — occlude via
        // depth, not draw order). Routing it through the FILL painter contract (ZWrite Off + transparent)
        // silently defeats that. RED-verify: revert ApplyElevatedContract to FillTweaker.ApplyPainterContract
        // (or point CreateFillExtrusionMaterial back at it) and both fail.
        [Test]
        public void FillExtrusionTweaker_ApplyElevatedContract_SetsDepthWriteOpaqueAndWhiteIdentity()
        {
            var m = Track(NewFillExtrusion());
            // Simulate a base .mat left in the flat/transparent state — the elevated contract must OVERRIDE it.
            m.SetFloat(ShaderProperties.PropertyNames.ZWrite, 0f);
            m.SetFloat(ShaderProperties.PropertyNames.SrcBlend, (int)BlendMode.SrcAlpha);
            m.SetFloat(ShaderProperties.PropertyNames.DstBlend, (int)BlendMode.OneMinusSrcAlpha);
            m.EnableKeyword(FillMaterialTweaker.SurfaceTypeTransparentKeyword);
            m.SetColor(ShaderProperties.PropertyNames.BaseColor, Color.red);

            FillExtrusionTweaker.ApplyElevatedContract(m);

            Assert.AreEqual((int)DepthWrite.On, (int)m.GetFloat(ShaderProperties.PropertyNames.ZWrite),
                "elevated-3D → ZWrite ON (buildings must occupy the depth buffer to self-occlude).");
            Assert.AreEqual((int)CompareFunction.LessEqual, (int)m.GetFloat(ShaderProperties.PropertyNames.ZTest),
                "elevated-3D → ZTest LEqual.");
            Assert.AreEqual((int)BlendMode.One, (int)m.GetFloat(ShaderProperties.PropertyNames.SrcBlend),
                "elevated-3D → opaque One src blend (NOT the fill's SrcAlpha).");
            Assert.AreEqual((int)BlendMode.Zero, (int)m.GetFloat(ShaderProperties.PropertyNames.DstBlend),
                "elevated-3D → opaque Zero dst blend.");
            Assert.IsFalse(m.IsKeywordEnabled(FillMaterialTweaker.SurfaceTypeTransparentKeyword),
                "elevated-3D → opaque: _SURFACE_TYPE_TRANSPARENT must be DISABLED (else URP alpha-blends the building).");
            AssertWhite(m.GetColor(ShaderProperties.PropertyNames.BaseColor), "_BaseColor");
        }

        [Test]
        public void CreateFillExtrusionMaterial_AppliesElevatedContract_NotFlatPainter()
        {
            // Guards the wiring, not just the tweaker: CreateFillExtrusionMaterial must route through the
            // ELEVATED contract. (The regression was that it called FillTweaker.ApplyPainterContract.)
            var settings = Track(ScriptableObject.CreateInstance<MapMaterialSet>());
            settings.FillExtrusionMaterial = Track(NewFillExtrusion());

            Material mat = Track(MaterialFactory.CreateFillExtrusionMaterial(settings));
            Assert.IsNotNull(mat, "a configured fill-extrusion base must yield a material.");
            Assert.AreEqual((int)DepthWrite.On, (int)mat.GetFloat(ShaderProperties.PropertyNames.ZWrite),
                "CreateFillExtrusionMaterial must apply the elevated contract (ZWrite On), not the flat painter one.");
            Assert.IsFalse(mat.IsKeywordEnabled(FillMaterialTweaker.SurfaceTypeTransparentKeyword),
                "CreateFillExtrusionMaterial must produce an OPAQUE material (no _SURFACE_TYPE_TRANSPARENT).");
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
            var m = Track(NewFill());
            m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.black);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            new FillShaderGUI().ValidateMaterial(m);
            Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.Emission), "EmissiveIsBlack/black → _EMISSION off");

            m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.white);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            new FillShaderGUI().ValidateMaterial(m);
            Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.Emission), "BakedEmissive/white → _EMISSION on");
        }

        [Test]
        public void LineShaderGUI_ValidateMaterial_EmissionTogglesFromGI()
        {
            var m = Track(NewLine());
            m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.white);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            new LineShaderGUI().ValidateMaterial(m);
            Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.Emission), "RealtimeEmissive/white → _EMISSION on");
        }

        [Test]
        public void FillExtrusionShaderGUI_ValidateMaterial_EmissionTogglesFromGI()
        {
            // Fill-extrusion needs a material editor. Map/FillExtrusion mirrors full URP Lit with
            // shader_feature keywords (_EMISSION, _NORMALMAP, …), so without a ShaderGUI running the editor
            // keyword sync those keywords are never derived from the material's properties and features like
            // emission silently do nothing. FillExtrusionShaderGUI declares no keyword of its own, so this
            // exercises that it correctly INHERITS LitShaderGUI's sync.
            var m = Track(NewFillExtrusion());
            m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.black);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            new FillExtrusionShaderGUI().ValidateMaterial(m);
            Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.Emission), "EmissiveIsBlack/black → _EMISSION off");

            m.SetColor(ShaderProperties.PropertyNames.EmissionColor, Color.white);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            new FillExtrusionShaderGUI().ValidateMaterial(m);
            Assert.IsTrue(m.IsKeywordEnabled(ShaderKeywords.Emission), "BakedEmissive/white → _EMISSION on");
        }

        // ── 2b. The line's own keyword: _EdgeAntialiasing → _EDGE_ANTIALIASING_OFF ──
        [Test]
        public void LineShaderGUI_ValidateMaterial_SyncsEdgeAntialiasingKeyword()
        {
            // _EdgeAntialiasing is declared [ToggleUI], which is UI-only and attaches NO keyword.
            // LineShaderGUI.ValidateMaterial is the ONLY thing that turns the float into
            // _EDGE_ANTIALIASING_OFF — delete that override and the shipped AA toggle is completely inert
            // while every other test in the repo stays green. This is its only guard.
            var m = Track(NewLine());
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

        [Test]
        public void LineShaderGUI_ValidateMaterial_OnCommittedBase_LeavesAntialiasingOn()
        {
            // The committed MapLine.mat carries `_EdgeAntialiasing: 1` and no m_ShaderKeywords entry, and
            // every per-layer material is a new Material(base) copy that inherits the keyword set. So the
            // shipped state — and therefore every line the map draws — must survive the sync with AA ON.
            var m = Track(new Material(MapMaterialSetTestUtil.Load().LineMaterial));
            new LineShaderGUI().ValidateMaterial(m);
            Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.EdgeAntialiasingOff),
                "the committed line base must render AA-ON: _EDGE_ANTIALIASING_OFF must be clear " +
                "after the editor keyword sync.");
        }

        // ── 2c. The hairline strategy selector → _HAIRLINE_HARD ──
        [Test]
        public void LineShaderGUI_ValidateMaterial_SyncsHairlineStrategyKeyword()
        {
            // _HairlineStrategy is declared [Enum(...)], a UI-only drawer that attaches NO keyword — exactly
            // like [ToggleUI] on _EdgeAntialiasing. LineShaderGUI.ValidateMaterial is the only thing that
            // turns the selector into _HAIRLINE_HARD; without it the whole selector is inert and every other
            // test in the repo stays green.
            var m = Track(NewLine());
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

        [Test]
        public void LineShaderGUI_ValidateMaterial_OnCommittedBase_LeavesHairlineDefault()
        {
            // Every per-layer material is a new Material(base) copy and Unity's copy ctor carries the keyword
            // set, so whatever the committed base declares propagates to every line the map draws. This fails
            // the moment someone saves a strategy into MapLine.mat.
            var m = Track(new Material(MapMaterialSetTestUtil.Load().LineMaterial));
            new LineShaderGUI().ValidateMaterial(m);
            Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineHard),
                "the committed line base must ship the Default strategy: _HAIRLINE_HARD must be clear " +
                "after the editor keyword sync.");
            Assert.IsFalse(m.IsKeywordEnabled(ShaderKeywords.HairlineSolidCore),
                "the committed line base must ship the Default strategy: _HAIRLINE_SOLID_CORE must be " +
                "clear after the editor keyword sync.");
        }

        // ── 3. ValidateMaterial is behaviour-neutral on the committed base (parity) ──
        [Test]
        public void FillShaderGUI_ValidateMaterial_OnCommittedBase_LeavesKeywordStateUnchanged()
        {
            // Parity tooth: running the editor keyword sync on a clone of the COMMITTED base fill .mat must
            // not FLIP any feature keyword — the runtime clone relies on the import-baked state, so a
            // derivation that disagreed with the asset would change how production renders.
            //
            // Asserted as before == after rather than against a hardcoded expected set: a fixed set like
            // "empty" holds only while the base happens to be opaque with no maps, and breaks the moment
            // the base legitimately becomes transparent (fills need _SURFACE_TYPE_TRANSPARENT or URP's
            // OutputAlpha discards the fragment alpha) even though the underlying invariant still holds.
            // Comparing to the asset's own state expresses the intent and survives the base's look
            // changing again.
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

            var m = Track(new Material(MapMaterialSetTestUtil.Load().FillMaterial));
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
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // PaintColorSingleApplyTests — a paint colour has exactly ONE carrier, not two
    // ───────────────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    // Unity EditMode only — real Materials via MaterialFactory / MapMaterialSet, real meshes via the production
    // builders. NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // A paint colour has exactly TWO carriers and the fragment MULTIPLIES them:
    //
    //     effective = _BaseColor (uniform, bound by MaterialFactory)  ×  COLOR stream (vertex, baked by the builder)
    //
    // so a layer whose colour is written into both renders it SQUARED. `line` and `fill-extrusion` did exactly
    // that for a CONSTANT (non-data-driven) colour: the binder bound it AND the builder baked it. The invariant
    // these teeth pin is one carrier per case — constant/zoom → the uniform, data-driven → the stream, the other
    // side left at white — in ALPHA as well as rgb.
    //
    // `fill` now runs the SAME contract as `line` and `fill-extrusion` — constant/zoom rides `_BaseColor`,
    // data-driven bakes into the stream. `ConstantFillColor_EffectiveColor_MatchesAuthored` below is fill's
    // row of the shared tooth.
    //
    // The fixture colour is MID-TONE. Squaring is invisible at white and near-maximal at mid-grey,
    // which is how this survived — every colour fixture that could have caught it was near-white.
    [TestFixture]
    public class PaintColorSingleApplyTests : BaseTestFixture
    {
        // ── The fixture colour ───────────────────────────────────────────────────────────────────

        /// <summary>#6699CC — sRGB (0.4, 0.6, 0.8), linear ≈ (0.1329, 0.3185, 0.6038). Mid-tone, so the
        /// squared value is ≈ 65–90 8-bit levels away per channel; three DISTINCT channels, so a channel
        /// swap or a single-channel write cannot pass.</summary>
        private const string AuthoredHex = "#6699CC";

        /// <summary>The same colour at alpha 0.5 — squared gives 0.25, an unmistakable 2×.</summary>
        private const string AuthoredRgbaHalfAlpha = "[\"rgba\",102,153,204,0.5]";

        private const float  HalfAlpha = 0.5f;
        private const float  Tol       = 1e-3f;

        private static Color AuthoredSrgb
        {
            get
            {
                Assert.IsTrue(ColorUtility.TryParseHtmlString(AuthoredHex, out Color c), "fixture colour must parse.");
                return c;
            }
        }

        // ── Scene constants ──────────────────────────────────────────────────────────────────────

        private const double Extent = 4096.0;
        private const double Zoom   = 14.0;
        private static readonly TileId Tile = new TileId { Z = 14, X = 8192, Y = 8192 };
        private static readonly double2 LocalOrigin = double2.zero;

        // ── Synthetic MVT geometry (the encoding StyledFillExtrusionMeshTests already uses) ──────

        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));

        /// <summary>A straight two-point LineString across the tile's middle.</summary>
        private static IFeature LineFeature(IReadOnlyDictionary<string, Value> props = null)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.LineString,
                geometry: new uint[]
                {
                    (1u << 3) | 1u, ZigZag(500), ZigZag(2048), // MoveTo(1) → (500, 2048)
                    (1u << 3) | 2u, ZigZag(3000), ZigZag(0),   // LineTo(1) → (3500, 2048)
                });

        /// <summary>A hole-less square polygon footprint, for the fill and fill-extrusion rows.</summary>
        private static IFeature SquareFeature(int x0, IReadOnlyDictionary<string, Value> props = null)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon,
                geometry: new uint[]
                {
                    (1u << 3) | 1u, ZigZag(x0), ZigZag(1000),
                    (3u << 3) | 2u,
                    ZigZag(500),  ZigZag(0),
                    ZigZag(0),    ZigZag(500),
                    ZigZag(-500), ZigZag(0),
                });

        private static IReadOnlyDictionary<string, Value> Cat(string v)
            => new Dictionary<string, Value> { { "cat", Value.String(v) } };

        // ── Reading the two carriers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// What the fragment's <c>_BaseColor</c> holds. Unity's Linear colour space converts a Color-typed
        /// material property from sRGB to linear on UPLOAD, so the CPU-side <c>GetColor</c> read-back is the
        /// sRGB value and <c>.linear</c> is the GPU one.
        /// <para>That conversion is a claim about the engine, not about this repo, so it is MEASURED rather
        /// than assumed — see <c>Visual.PaintColorRenderTests.UniformBoundColor_OverWhiteVertices_RendersAuthoredColor</c>,
        /// which reads the same quantity off a rendered pixel. If the two ever disagree, this composition is
        /// the half that is wrong.</para>
        /// </summary>
        private static Color ShaderBaseColor(Material mat)
            => mat.GetColor(ShaderProperties.PropertyId.BaseColor).linear;

        /// <summary>The single COLOR-stream value the mesh carries, asserting every vertex agrees (a
        /// constant-colour layer that varied per vertex would mean the gate leaked).</summary>
        private static Color StreamColor(Mesh mesh, string what)
        {
            Assert.IsNotNull(mesh, $"{what}: the fixture must produce geometry.");
            var colors = new List<Color>();
            mesh.GetColors(colors);
            Assert.Greater(colors.Count, 0, $"{what}: the mesh must carry a COLOR stream.");
            for (int i = 1; i < colors.Count; i++)
                Assert.AreEqual(colors[0], colors[i],
                    $"{what}: a constant colour must be uniform across all {colors.Count} vertices " +
                    $"(vertex {i} differs) — a varying value means the data-driven bake ran for a constant.");
            return colors[0];
        }

        /// <summary>The distinct COLOR-stream values, quantised to 8 bits per channel.</summary>
        private static HashSet<(int r, int g, int b)> DistinctStreamColors(Mesh mesh, string what)
        {
            Assert.IsNotNull(mesh, $"{what}: the fixture must produce geometry.");
            var colors = new List<Color>();
            mesh.GetColors(colors);
            var distinct = new HashSet<(int, int, int)>();
            foreach (Color c in colors)
                distinct.Add(((int)(c.r * 255f + 0.5f), (int)(c.g * 255f + 0.5f), (int)(c.b * 255f + 0.5f)));
            return distinct;
        }

        private static void AssertRgbEquals(Color expected, Color actual, string what)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(Tol), $"{what}: R. expected={Fmt(expected)} actual={Fmt(actual)}");
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(Tol), $"{what}: G. expected={Fmt(expected)} actual={Fmt(actual)}");
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(Tol), $"{what}: B. expected={Fmt(expected)} actual={Fmt(actual)}");
        }

        private static string Fmt(Color c) => $"({c.r:F4}, {c.g:F4}, {c.b:F4}, {c.a:F4})";

        // ── Building the two carriers through the production paths ───────────────────────────────

        private static Material BoundLineMaterial(Line.PaintProperties paint)
        {
            Material mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/Line base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindLinePaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));
            return mat;
        }

        private static Material BoundFillExtrusionMaterial(FillExtrusion.PaintProperties paint)
        {
            Material mat = MaterialFactory.CreateFillExtrusionMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/FillExtrusion base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillExtrusionPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));
            return mat;
        }

        private static Mesh BuildLineMesh(Line.PaintProperties paint)
            => TestTileMeshBuilder.BuildLine(new[] { LineFeature() }, paint, TestStyle.LineLayout("{}"), Zoom, Extent, Tile, LocalOrigin);

        private static Mesh BuildExtrusionMesh(FillExtrusion.PaintProperties paint)
            => TestTileMeshBuilder.BuildFillExtrusion(new[] { SquareFeature(1000) }, paint, Zoom, Extent, Tile);

        // ── Tooth 1 — the effective rendered colour equals the authored colour ───────────────────

        /// <summary>
        /// Headline. Composes the fragment's own <c>_BaseColor.rgb × vColor.rgb</c> from the two REAL sources
        /// — the production binder's uniform and the production builder's COLOR stream — and asserts the
        /// product is the authored colour, not its square.
        /// </summary>
        [Test]
        public void ConstantLineColor_EffectiveColor_MatchesAuthored()
        {
            var paint = TestStyle.LinePaint($"{{\"line-color\":\"{AuthoredHex}\",\"line-width\":8}}");
            Assert.IsFalse(paint.Color.DependsOnFeature,
                "precondition: the fixture's line-color must parse as constant, or this row tests the " +
                "data-driven branch that DataDrivenLineColor_StillVariesPerFeature already covers.");

            Material mat  = Track(BoundLineMaterial(paint));
            Mesh     mesh = Track(BuildLineMesh(paint));
            Color uniform = ShaderBaseColor(mat);
            Color stream  = StreamColor(mesh, "constant line-color");
            Color effective = new Color(uniform.r * stream.r, uniform.g * stream.g, uniform.b * stream.b, 1f);

            AssertRgbEquals(AuthoredSrgb.linear, effective,
                $"a CONSTANT line-color must reach the fragment exactly once. uniform={Fmt(uniform)} " +
                $"stream={Fmt(stream)}. Both carrying it renders the colour SQUARED");
        }

        /// <summary>Tooth 1, fill-extrusion kind. Same composition, the other defective pair.</summary>
        [Test]
        public void ConstantFillExtrusionColor_EffectiveColor_MatchesAuthored()
        {
            var paint = TestStyle.FillExtrusionPaint($"{{\"fill-extrusion-color\":\"{AuthoredHex}\",\"fill-extrusion-height\":30}}");
            Assert.IsFalse(paint.Color.DependsOnFeature, "precondition: constant fill-extrusion-color.");

            Material mat  = Track(BoundFillExtrusionMaterial(paint));
            Mesh     mesh = Track(BuildExtrusionMesh(paint));
            Color uniform = ShaderBaseColor(mat);
            Color stream  = StreamColor(mesh, "constant fill-extrusion-color");
            Color effective = new Color(uniform.r * stream.r, uniform.g * stream.g, uniform.b * stream.b, 1f);

            AssertRgbEquals(AuthoredSrgb.linear, effective,
                $"a CONSTANT fill-extrusion-color must reach the fragment exactly once. uniform={Fmt(uniform)} " +
                $"stream={Fmt(stream)}. Both carrying it renders the colour SQUARED");
        }

        // ── Tooth 3 — alpha, per kind, both directions ───────────────────────────────────────────

        /// <summary>
        /// fill-extrusion's fragment computes <c>alpha = _BaseColor.a × vColor.a × _Opacity</c>, so a constant
        /// colour's alpha was squared exactly as its rgb was.
        /// </summary>
        [Test]
        public void ConstantFillExtrusionColorAlpha_IsNotAppliedTwice()
        {
            var paint = TestStyle.FillExtrusionPaint(
                $"{{\"fill-extrusion-color\":{AuthoredRgbaHalfAlpha},\"fill-extrusion-height\":30}}");
            Assert.That((float)paint.Color.Evaluate(Zoom).A, Is.EqualTo(HalfAlpha).Within(Tol),
                "precondition: the fixture colour must carry the authored alpha 0.5 — a constant colour is " +
                "not interpolated, so premultiplication cannot have moved it.");

            Material mat  = Track(BoundFillExtrusionMaterial(paint));
            Mesh     mesh = Track(BuildExtrusionMesh(paint));
            float uniformA = mat.GetColor(ShaderProperties.PropertyId.BaseColor).a;
            float streamA  = StreamColor(mesh, "constant fill-extrusion-color alpha").a;
            float opacity  = mat.GetFloat(ShaderProperties.PropertyId.Opacity);
            float composed = uniformA * streamA * opacity;

            Assert.That(composed, Is.EqualTo(HalfAlpha).Within(Tol),
                $"a CONSTANT fill-extrusion-color's ALPHA must reach the fragment exactly once. " +
                $"_BaseColor.a={uniformA:F4} vColor.a={streamA:F4} _Opacity={opacity:F4} → {composed:F4}. " +
                $"0.25 means alpha is squared, the same defect as rgb.");
        }

        /// <summary>
        /// The line fragment REPLACES surface alpha with <c>coverage × _Opacity × vColor.a × _BaseColor.a</c>.
        /// Line alpha was never double-applied — <c>_BaseColor.a</c> did not participate at all — so moving a
        /// constant colour onto the uniform DROPS the authored alpha unless the two fragment tokens gain
        /// <c>× _BaseColor.a</c> with it.
        ///
        /// <para>This row pins the CARRIERS only: the binder puts the authored alpha on the uniform and the
        /// builder leaves the vertex at 1, so the two compose to the authored value. It spells out the
        /// intended formula and therefore CANNOT see whether the fragment actually carries the
        /// <c>_BaseColor.a</c> term — it passes either way once the builder gate lands. The shader half is
        /// observed by
        /// <c>Visual.PaintColorRenderTests.ConstantLineColorAlpha_RenderedPixel_CarriesTheAuthoredAlpha</c>,
        /// which reads the composite instead of composing it.</para>
        /// </summary>
        [Test]
        public void ConstantLineColorAlpha_SurvivesTheUniformPath()
        {
            var paint = TestStyle.LinePaint($"{{\"line-color\":{AuthoredRgbaHalfAlpha},\"line-width\":8}}");
            Assert.That((float)paint.Color.Evaluate(Zoom).A, Is.EqualTo(HalfAlpha).Within(Tol),
                "precondition: the fixture colour must carry the authored alpha 0.5.");

            Material mat  = Track(BoundLineMaterial(paint));
            Mesh     mesh = Track(BuildLineMesh(paint));
            float uniformA = mat.GetColor(ShaderProperties.PropertyId.BaseColor).a;
            float streamA  = StreamColor(mesh, "constant line-color alpha").a;
            float opacity  = mat.GetFloat(ShaderProperties.PropertyId.Opacity);
            float composed = 1f * opacity * streamA * uniformA; // coverage = 1 at the ribbon centre

            Assert.That(composed, Is.EqualTo(HalfAlpha).Within(Tol),
                $"a CONSTANT line-color's authored ALPHA must sit on exactly one carrier. " +
                $"_BaseColor.a={uniformA:F4} vColor.a={streamA:F4} _Opacity={opacity:F4} → {composed:F4}. " +
                $"0.25 means both carriers hold it; a stream alpha below 1 means the bake still runs for " +
                $"a constant colour. Whether the FRAGMENT reads _BaseColor.a is not observable here — " +
                $"see PaintColorRenderTests.ConstantLineColorAlpha_RenderedPixel_CarriesTheAuthoredAlpha.");
        }

        // ── Tooth 4 — the data-driven branch still bakes, and still does NOT bind ────────────────

        /// <summary>
        /// Both halves matter: the stream still varying catches over-gating, and <c>_BaseColor</c> staying
        /// white catches a bind leaking into the data-driven case and re-creating the defect there.
        /// </summary>
        [Test]
        public void DataDrivenLineColor_StillVariesPerFeature()
        {
            const string ddColor = "[\"match\",[\"get\",\"cat\"],\"a\",\"#6699CC\",\"b\",\"#CC9966\",\"#000000\"]";
            var paint = TestStyle.LinePaint($"{{\"line-color\":{ddColor},\"line-width\":8}}");
            Assert.IsTrue(paint.Color.DependsOnFeature, "precondition: the fixture's line-color must be data-driven.");

            Material mat = Track(BoundLineMaterial(paint));
            Mesh mesh = Track(TestTileMeshBuilder.BuildLine(
                new[] { LineFeature(Cat("a")), LineFeature(Cat("b")) },
                paint, TestStyle.LineLayout("{}"), Zoom, Extent, Tile, LocalOrigin));

            Assert.GreaterOrEqual(DistinctStreamColors(mesh, "data-driven line-color").Count, 2,
                "a data-driven line-color must still bake ≥2 distinct COLOR-stream values — gating the " +
                "bake on the WRONG side of DependsOnFeature trades one branch for the other.");

            Color uniform = mat.GetColor(ShaderProperties.PropertyId.BaseColor);
            AssertRgbEquals(Color.white, uniform,
                "a data-driven line-color must leave _BaseColor at the white identity — binding it here " +
                "would tint every feature and re-create the double-apply on the other branch");
        }

        /// <summary>Tooth 4, fill-extrusion kind.</summary>
        [Test]
        public void DataDrivenFillExtrusionColor_StillVariesPerFeature()
        {
            const string ddColor = "[\"match\",[\"get\",\"cat\"],\"a\",\"#6699CC\",\"b\",\"#CC9966\",\"#000000\"]";
            var paint = TestStyle.FillExtrusionPaint($"{{\"fill-extrusion-color\":{ddColor},\"fill-extrusion-height\":30}}");
            Assert.IsTrue(paint.Color.DependsOnFeature, "precondition: data-driven fill-extrusion-color.");

            Material mat = Track(BoundFillExtrusionMaterial(paint));
            Mesh mesh = Track(TestTileMeshBuilder.BuildFillExtrusion(
                new[] { SquareFeature(1000, Cat("a")), SquareFeature(2000, Cat("b")) },
                paint, Zoom, Extent, Tile));

            Assert.GreaterOrEqual(DistinctStreamColors(mesh, "data-driven fill-extrusion-color").Count, 2,
                "a data-driven fill-extrusion-color must still bake ≥2 distinct COLOR-stream values.");

            Color uniform = mat.GetColor(ShaderProperties.PropertyId.BaseColor);
            AssertRgbEquals(Color.white, uniform,
                "a data-driven fill-extrusion-color must leave _BaseColor at the white identity");
        }

        // ── Tooth 5 — `fill` runs the same contract as line/fill-extrusion ───────────────────────

        /// <summary>Tooth 1, fill kind. Same composition as the line/fill-extrusion rows above — fill's
        /// constant/zoom colour rides <c>_BaseColor</c> too, so the same double-apply hazard
        /// applies here and the same headline check pins it.</summary>
        [Test]
        public void ConstantFillColor_EffectiveColor_MatchesAuthored()
        {
            var paint = TestStyle.FillPaint($"{{\"fill-color\":\"{AuthoredHex}\"}}");
            Assert.IsFalse(paint.Color.DependsOnFeature,
                "precondition: the fixture's fill-color must parse as constant, or this row tests the " +
                "data-driven branch that DataDrivenFillColor_StillVariesPerFeature already covers.");

            Material mat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(new[] { SquareFeature(1000) }, paint, Zoom, Extent, Tile));
            Color uniform = ShaderBaseColor(mat);
            Color stream  = StreamColor(mesh, "constant fill-color");
            Color effective = new Color(uniform.r * stream.r, uniform.g * stream.g, uniform.b * stream.b, 1f);

            AssertRgbEquals(AuthoredSrgb.linear, effective,
                $"a CONSTANT fill-color must reach the fragment exactly once. uniform={Fmt(uniform)} " +
                $"stream={Fmt(stream)}. Both carrying it renders the colour SQUARED");
        }

        /// <summary>Tooth 4, fill kind. Same shape as the line/fill-extrusion rows above.</summary>
        [Test]
        public void DataDrivenFillColor_StillVariesPerFeature()
        {
            const string ddColor = "[\"match\",[\"get\",\"cat\"],\"a\",\"#6699CC\",\"b\",\"#CC9966\",\"#000000\"]";
            var paint = TestStyle.FillPaint($"{{\"fill-color\":{ddColor}}}");
            Assert.IsTrue(paint.Color.DependsOnFeature, "precondition: the fixture's fill-color must be data-driven.");

            Material mat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                new[] { SquareFeature(1000, Cat("a")), SquareFeature(2000, Cat("b")) }, paint, Zoom, Extent, Tile));

            Assert.GreaterOrEqual(DistinctStreamColors(mesh, "data-driven fill-color").Count, 2,
                "a data-driven fill-color must still bake ≥2 distinct COLOR-stream values — gating the " +
                "bake on the WRONG side of DependsOnFeature trades one branch for the other.");

            Color uniform = mat.GetColor(ShaderProperties.PropertyId.BaseColor);
            AssertRgbEquals(Color.white, uniform,
                "a data-driven fill-color must leave _BaseColor at the white identity — binding it here " +
                "would tint every feature and re-create the double-apply on the other branch");
        }

        /// <summary>
        /// fill's own wrinkle: a CONSTANT fill-color's alpha rides <c>_BaseColor.a</c>, and a data-driven
        /// fill-opacity is baked into the SAME stream a constant colour would have used. This test is the
        /// mirror of <c>FillSortKeyAndOpacityTests.DataDrivenOpacity_IsTheStreamsOnlyAlphaCarrier</c> —
        /// see that test for the other carrier.
        /// </summary>
        [Test]
        public void ConstantFillColorAlpha_IsNotAppliedTwice()
        {
            var paint = TestStyle.FillPaint($"{{\"fill-color\":{AuthoredRgbaHalfAlpha},\"fill-opacity\":[\"get\",\"op\"]}}");
            Assert.That((float)paint.Color.Evaluate(Zoom).A, Is.EqualTo(HalfAlpha).Within(Tol),
                "precondition: the fixture colour must carry the authored alpha 0.5.");
            Assert.IsTrue(paint.Opacity.DependsOnFeature, "precondition: fill-opacity must be data-driven.");

            Material mat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            var props = new Dictionary<string, Value> { { "op", Value.Number(0.4) } };
            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(new[] { SquareFeature(1000, props) }, paint, Zoom, Extent, Tile));
            float uniformA = mat.GetColor(ShaderProperties.PropertyId.BaseColor).a;
            float streamA  = StreamColor(mesh, "constant fill-color alpha, data-driven opacity").a;
            float opacity  = mat.GetFloat(ShaderProperties.PropertyId.Opacity);
            float composed = uniformA * streamA * opacity;

            Assert.That(composed, Is.EqualTo(0.2f).Within(Tol),
                $"a CONSTANT fill-color's authored ALPHA (0.5) × a data-driven fill-opacity (0.4) must " +
                $"reach the fragment exactly once each. _BaseColor.a={uniformA:F4} vColor.a={streamA:F4} " +
                $"_Opacity={opacity:F4} → {composed:F4}.");
        }

        // ── The premise the line alpha delta rests on ────────────────────────────────────────────

        /// <summary>
        /// <c>Line_LitInput</c> feeds <c>_BaseColor.a</c> through <c>AlphaModulate</c>, which premultiplies the
        /// albedo only when <c>_ALPHAPREMULTIPLY_ON</c> is set. A cloned line material must not carry it, or
        /// the alpha the Lit fragment now re-applies would darken the albedo a second time.
        /// <c>LineTweaker.ApplyPainterContract</c> sets straight-alpha blend factors but does not sync
        /// keywords, so this is a property of the committed base <c>.mat</c> — read it, don't assume it.
        /// </summary>
        [Test]
        public void ClonedLineMaterial_HasNoAlphaPremultiplyKeyword()
        {
            Material mat = Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsFalse(mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"),
                "a line material must composite STRAIGHT alpha. With premultiply on, Line_LitInput's " +
                "AlphaModulate scales the albedo by _BaseColor.a and the fragment's replaced alpha " +
                "applies it again — a constant translucent line would render double-dark.");
        }
    }
#endif
}
