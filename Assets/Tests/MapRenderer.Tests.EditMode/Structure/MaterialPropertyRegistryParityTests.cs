// S78/S79 Material Property Registry Parity Tests — runs in the Unity EditMode test assembly
// (moved from Tools/core-tests, which used test-binary-relative path arithmetic that broke on the
// Assets/Code/ folder move). Paths are now resolved via ShaderPropertyParser's
// AssetDatabase-anchored helpers — move-proof.
//
// Validates the four-way set equality:
//   • sharedCBUFFER (from ShaderProperties/PropertyNames.cs CBUFFER region, 14)
//   • ShaderProperties/Line/PropertyNames.cs (12 line-only: 10 CBUFFER + 2 editor-only keyword drivers),
//     ShaderProperties/Fill/PropertyNames.cs (7 fill-only)
//   • Line_LitInput.hlsl CBUFFER (24 after stripping companions)
//   • Fill_LitInput.hlsl CBUFFER (21 after stripping companions)
//   (line counts dropped by 2 — _MetersPerPixel (S104) + _AaEdgeWidth (line-AA removal) retired)
//   • Line/Fill DOTS blocks (must equal their respective CBUFFER sets)
//   • Line.shader / Fill.shader Properties{} blocks (must be supersets of the registry)
//
// Exact counts: assert exact, never >. Fail loud if a regex silently matches nothing.
//
// History: introduced S78 to lock the registry against both omissions and duplications.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace MapRenderer.Tests.Structure
{
    [TestFixture]
    public class MaterialPropertyRegistryParityTests
    {
        // ── Paths ────────────────────────────────────────────────────────────────────────────────

        private static string RenderingDir        => ShaderPropertyParser.RenderingDir;
        private static string ShaderPropertiesDir => Path.Combine(RenderingDir, "ShaderProperties");
        private static string RenderingLineDir     => Path.Combine(ShaderPropertiesDir, "Line");
        private static string RenderingFillDir     => Path.Combine(ShaderPropertiesDir, "Fill");

        // ── Exact count guards ───────────────────────────────────────────────────────────────────

        [Test]
        public void LineCbufferCount_IsExactly24()
        {
            var set = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));
            Assert.That(set.Count, Is.EqualTo(24),
                $"Line_LitInput.hlsl CBUFFER (after companion strip) must have exactly 24 members. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void FillCbufferCount_IsExactly21()
        {
            var set = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));
            Assert.That(set.Count, Is.EqualTo(21),
                $"Fill_LitInput.hlsl CBUFFER (after companion strip) must have exactly 21 members. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void SharedCbufferRegionCount_IsExactly14()
        {
            var set = ParseSharedCbufferRegion();
            Assert.That(set.Count, Is.EqualTo(14),
                $"PropertyNames.cs CBUFFER region must have exactly 14 const string values. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void LinePropertyNamesCount_IsExactly12()
        {
            var set = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"));
            Assert.That(set.Count, Is.EqualTo(12),
                $"ShaderProperties/Line/PropertyNames.cs must have exactly 12 const string values " +
                $"(10 CBUFFER members + 2 editor-only keyword drivers). " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void LineCbufferRegionCount_IsExactly10()
        {
            // Guards the region split itself: SharedUnionLineNames_EqualsLineCbuffer now reads only the
            // CBUFFER region, so without an exact count here a real CBUFFER property could be dropped into
            // the editor-only region and silently escape the parity assertion.
            var set = ParseLineCbufferRegion();
            Assert.That(set.Count, Is.EqualTo(10),
                $"Line/PropertyNames.cs CBUFFER region must have exactly 10 const string values. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void FillPropertyNamesCount_IsExactly7()
        {
            var set = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));
            Assert.That(set.Count, Is.EqualTo(7),
                $"ShaderProperties/Fill/PropertyNames.cs must have exactly 7 const string values. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void PropertyNamesTotalCount_IsExactly41()
        {
            var set = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"));
            Assert.That(set.Count, Is.EqualTo(41),
                $"ShaderProperties/PropertyNames.cs must have exactly 41 const string values in total " +
                $"(14 CBUFFER + 9 render-state + 18 textures/surface). " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        // ── Set equality: CBUFFER region == shader intersection ──────────────────────────────────

        [Test]
        public void SharedCbufferRegion_EqualsLineFillIntersection()
        {
            // The 14 shared CBUFFER names in PropertyNames.cs must be exactly the intersection of
            // the two shader CBUFFER sets. A mismatch means a prop was declared shared but is only
            // in one shader (or vice versa).
            var shared = ParseSharedCbufferRegion();
            var lineCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));
            var fillCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));

            var sharedFromShaders = new HashSet<string>(lineCbuffer);
            sharedFromShaders.IntersectWith(fillCbuffer);

            var onlyInCs = new HashSet<string>(shared);
            onlyInCs.ExceptWith(sharedFromShaders);
            var onlyInShaders = new HashSet<string>(sharedFromShaders);
            onlyInShaders.ExceptWith(shared);

            Assert.That(onlyInCs, Is.Empty,
                $"PropertyNames CBUFFER region has props not in both shaders: {string.Join(", ", onlyInCs)}");
            Assert.That(onlyInShaders, Is.Empty,
                $"Shader intersection has props missing from PropertyNames CBUFFER region: {string.Join(", ", onlyInShaders)}");
        }

        // ── Union parity: shared ∪ line-only == Line CBUFFER (the big assertion) ─────────────────

        [Test]
        public void SharedUnionLineNames_EqualsLineCbuffer()
        {
            var shared = ParseSharedCbufferRegion();
            var lineNames = ParseLineCbufferRegion();
            var lineCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));

            var union = new HashSet<string>(shared);
            union.UnionWith(lineNames);

            var onlyInUnion = new HashSet<string>(union);
            onlyInUnion.ExceptWith(lineCbuffer);
            var onlyInCbuffer = new HashSet<string>(lineCbuffer);
            onlyInCbuffer.ExceptWith(union);

            Assert.That(onlyInUnion, Is.Empty,
                $"Props in (shared ∪ lineNames) but not in Line CBUFFER: {string.Join(", ", onlyInUnion)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Line CBUFFER but not in (shared ∪ lineNames): {string.Join(", ", onlyInCbuffer)}");
        }

        [Test]
        public void SharedUnionFillNames_EqualsFillCbuffer()
        {
            var shared = ParseSharedCbufferRegion();
            var fillNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));
            var fillCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));

            var union = new HashSet<string>(shared);
            union.UnionWith(fillNames);

            var onlyInUnion = new HashSet<string>(union);
            onlyInUnion.ExceptWith(fillCbuffer);
            var onlyInCbuffer = new HashSet<string>(fillCbuffer);
            onlyInCbuffer.ExceptWith(union);

            Assert.That(onlyInUnion, Is.Empty,
                $"Props in (shared ∪ fillNames) but not in Fill CBUFFER: {string.Join(", ", onlyInUnion)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Fill CBUFFER but not in (shared ∪ fillNames): {string.Join(", ", onlyInCbuffer)}");
        }

        // ── Instanced subset: DOTS block == CBUFFER (cross-check) ───────────────────────────────

        [Test]
        public void LineDots_EqualsLineCbuffer()
        {
            // Line DOTS block keys must equal the Line CBUFFER set (both 24, every CBUFFER prop is instanced).
            var lineDotsKeys = new HashSet<string>(
                ShaderPropertyParser.ParseDotsProps(ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl")).Keys);
            var lineCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));

            var onlyInDots = new HashSet<string>(lineDotsKeys);
            onlyInDots.ExceptWith(lineCbuffer);
            var onlyInCbuffer = new HashSet<string>(lineCbuffer);
            onlyInCbuffer.ExceptWith(lineDotsKeys);

            Assert.That(onlyInDots, Is.Empty,
                $"Props in Line DOTS block but not in Line CBUFFER: {string.Join(", ", onlyInDots)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Line CBUFFER but not in Line DOTS block: {string.Join(", ", onlyInCbuffer)}");
        }

        [Test]
        public void FillDots_EqualsFillCbuffer()
        {
            var fillDotsKeys = new HashSet<string>(
                ShaderPropertyParser.ParseDotsProps(ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl")).Keys);
            var fillCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));

            var onlyInDots = new HashSet<string>(fillDotsKeys);
            onlyInDots.ExceptWith(fillCbuffer);
            var onlyInCbuffer = new HashSet<string>(fillCbuffer);
            onlyInCbuffer.ExceptWith(fillDotsKeys);

            Assert.That(onlyInDots, Is.Empty,
                $"Props in Fill DOTS block but not in Fill CBUFFER: {string.Join(", ", onlyInDots)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Fill CBUFFER but not in Fill DOTS block: {string.Join(", ", onlyInCbuffer)}");
        }

        // ── Shader Properties{} superset: every registry name appears in the shader ─────────────

        [Test]
        public void LineShaderPropertiesBlock_ContainsAllRegistryNames()
        {
            var allShared = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"));
            var lineNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"));
            var lineShaderProps = ShaderPropertyParser.ParseShaderPropertiesBlock(
                ShaderPropertyParser.MapShaderPath("Line.shader"));

            var allRegistry = new HashSet<string>(allShared);
            allRegistry.UnionWith(lineNames);

            var missing = new List<string>();
            foreach (string name in allRegistry)
                if (!lineShaderProps.Contains(name))
                    missing.Add(name);

            Assert.That(missing, Is.Empty,
                $"These registry names are missing from Line.shader Properties{{}}: " +
                string.Join(", ", missing));
        }

        [Test]
        public void FillShaderPropertiesBlock_ContainsAllRegistryNames()
        {
            var allShared = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"));
            var fillNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));
            var fillShaderProps = ShaderPropertyParser.ParseShaderPropertiesBlock(
                ShaderPropertyParser.MapShaderPath("Fill.shader"));

            var allRegistry = new HashSet<string>(allShared);
            allRegistry.UnionWith(fillNames);

            var missing = new List<string>();
            foreach (string name in allRegistry)
                if (!fillShaderProps.Contains(name))
                    missing.Add(name);

            Assert.That(missing, Is.Empty,
                $"These registry names are missing from Fill.shader Properties{{}}: " +
                string.Join(", ", missing));
        }

        // ── No cross-contamination: layer-specific names not in shared CBUFFER region ────────────

        [Test]
        public void LinePropertyNames_HasNoDuplicatesInSharedCbuffer()
        {
            // _BaseColor must be only in shared PropertyNames, not re-declared in Line/PropertyNames.
            var shared = ParseSharedCbufferRegion();
            var lineNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"));

            var overlap = new HashSet<string>(lineNames);
            overlap.IntersectWith(shared);

            Assert.That(overlap, Is.Empty,
                $"ShaderProperties/Line/PropertyNames.cs re-declares names that are already in the shared CBUFFER region " +
                $"(duplication violates the single-source-of-truth rule): {string.Join(", ", overlap)}");
        }

        [Test]
        public void FillPropertyNames_HasNoDuplicatesInSharedCbuffer()
        {
            var shared = ParseSharedCbufferRegion();
            var fillNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));

            var overlap = new HashSet<string>(fillNames);
            overlap.IntersectWith(shared);

            Assert.That(overlap, Is.Empty,
                $"ShaderProperties/Fill/PropertyNames.cs re-declares names that are already in the shared CBUFFER region: " +
                string.Join(", ", overlap));
        }

        // ── Shared helper ────────────────────────────────────────────────────────────────────────

        private static HashSet<string> ParseSharedCbufferRegion()
            => ShaderPropertyParser.ParseRegionValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"),
                "// region: CBUFFER (UnityPerMaterial) — shared instanced members");

        /// <summary>
        /// The CBUFFER region of the LINE registry. Line/PropertyNames.cs also declares editor-only keyword
        /// drivers (Properties{}-only, never CBUFFER members), exactly as the shared registry does for
        /// _ReceiveShadows, so the union parity assertion reads this region rather than every const in the
        /// file. <see cref="LineCbufferRegionCount_IsExactly10"/> pins the region's size.
        /// </summary>
        private static HashSet<string> ParseLineCbufferRegion()
            => ShaderPropertyParser.ParseRegionValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"),
                "// region: CBUFFER (UnityPerMaterial) — line-only instanced members");
    }
}
