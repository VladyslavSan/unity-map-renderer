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
// The fixture colour is deliberately MID-TONE. Squaring is invisible at white and near-maximal at mid-grey,
// which is how this survived — every colour fixture that could have caught it was near-white.

using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using Color = UnityEngine.Color;
using Line = MapRenderer.Core.Style.Line;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class PaintColorSingleApplyTests
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

            Material mat  = BoundLineMaterial(paint);
            Mesh     mesh = BuildLineMesh(paint);
            try
            {
                Color uniform = ShaderBaseColor(mat);
                Color stream  = StreamColor(mesh, "constant line-color");
                Color effective = new Color(uniform.r * stream.r, uniform.g * stream.g, uniform.b * stream.b, 1f);

                AssertRgbEquals(AuthoredSrgb.linear, effective,
                    $"a CONSTANT line-color must reach the fragment exactly once. uniform={Fmt(uniform)} " +
                    $"stream={Fmt(stream)}. Both carrying it renders the colour SQUARED");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        /// <summary>Tooth 1, fill-extrusion kind. Same composition, the other defective pair.</summary>
        [Test]
        public void ConstantFillExtrusionColor_EffectiveColor_MatchesAuthored()
        {
            var paint = TestStyle.FillExtrusionPaint($"{{\"fill-extrusion-color\":\"{AuthoredHex}\",\"fill-extrusion-height\":30}}");
            Assert.IsFalse(paint.Color.DependsOnFeature, "precondition: constant fill-extrusion-color.");

            Material mat  = BoundFillExtrusionMaterial(paint);
            Mesh     mesh = BuildExtrusionMesh(paint);
            try
            {
                Color uniform = ShaderBaseColor(mat);
                Color stream  = StreamColor(mesh, "constant fill-extrusion-color");
                Color effective = new Color(uniform.r * stream.r, uniform.g * stream.g, uniform.b * stream.b, 1f);

                AssertRgbEquals(AuthoredSrgb.linear, effective,
                    $"a CONSTANT fill-extrusion-color must reach the fragment exactly once. uniform={Fmt(uniform)} " +
                    $"stream={Fmt(stream)}. Both carrying it renders the colour SQUARED");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
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

            Material mat  = BoundFillExtrusionMaterial(paint);
            Mesh     mesh = BuildExtrusionMesh(paint);
            try
            {
                float uniformA = mat.GetColor(ShaderProperties.PropertyId.BaseColor).a;
                float streamA  = StreamColor(mesh, "constant fill-extrusion-color alpha").a;
                float opacity  = mat.GetFloat(ShaderProperties.PropertyId.Opacity);
                float composed = uniformA * streamA * opacity;

                Assert.That(composed, Is.EqualTo(HalfAlpha).Within(Tol),
                    $"a CONSTANT fill-extrusion-color's ALPHA must reach the fragment exactly once. " +
                    $"_BaseColor.a={uniformA:F4} vColor.a={streamA:F4} _Opacity={opacity:F4} → {composed:F4}. " +
                    $"0.25 means alpha is squared, the same defect as rgb.");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
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

            Material mat  = BoundLineMaterial(paint);
            Mesh     mesh = BuildLineMesh(paint);
            try
            {
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
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
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

            Material mat = BoundLineMaterial(paint);
            Mesh mesh = TestTileMeshBuilder.BuildLine(
                new[] { LineFeature(Cat("a")), LineFeature(Cat("b")) },
                paint, TestStyle.LineLayout("{}"), Zoom, Extent, Tile, LocalOrigin);
            try
            {
                Assert.GreaterOrEqual(DistinctStreamColors(mesh, "data-driven line-color").Count, 2,
                    "a data-driven line-color must still bake ≥2 distinct COLOR-stream values — gating the " +
                    "bake on the WRONG side of DependsOnFeature trades one branch for the other.");

                Color uniform = mat.GetColor(ShaderProperties.PropertyId.BaseColor);
                AssertRgbEquals(Color.white, uniform,
                    "a data-driven line-color must leave _BaseColor at the white identity — binding it here " +
                    "would tint every feature and re-create the double-apply on the other branch");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        /// <summary>Tooth 4, fill-extrusion kind.</summary>
        [Test]
        public void DataDrivenFillExtrusionColor_StillVariesPerFeature()
        {
            const string ddColor = "[\"match\",[\"get\",\"cat\"],\"a\",\"#6699CC\",\"b\",\"#CC9966\",\"#000000\"]";
            var paint = TestStyle.FillExtrusionPaint($"{{\"fill-extrusion-color\":{ddColor},\"fill-extrusion-height\":30}}");
            Assert.IsTrue(paint.Color.DependsOnFeature, "precondition: data-driven fill-extrusion-color.");

            Material mat = BoundFillExtrusionMaterial(paint);
            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(
                new[] { SquareFeature(1000, Cat("a")), SquareFeature(2000, Cat("b")) },
                paint, Zoom, Extent, Tile);
            try
            {
                Assert.GreaterOrEqual(DistinctStreamColors(mesh, "data-driven fill-extrusion-color").Count, 2,
                    "a data-driven fill-extrusion-color must still bake ≥2 distinct COLOR-stream values.");

                Color uniform = mat.GetColor(ShaderProperties.PropertyId.BaseColor);
                AssertRgbEquals(Color.white, uniform,
                    "a data-driven fill-extrusion-color must leave _BaseColor at the white identity");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        // ── Tooth 5 — `fill` runs the same contract as line/fill-extrusion ───────────────────────

        /// <summary>Tooth 1, fill kind. Same composition as the line/fill-extrusion rows above — fill's
        /// constant/zoom colour now rides <c>_BaseColor</c> too (Stage 1), so the same double-apply hazard
        /// applies here and the same headline check pins it.</summary>
        [Test]
        public void ConstantFillColor_EffectiveColor_MatchesAuthored()
        {
            var paint = TestStyle.FillPaint($"{{\"fill-color\":\"{AuthoredHex}\"}}");
            Assert.IsFalse(paint.Color.DependsOnFeature,
                "precondition: the fixture's fill-color must parse as constant, or this row tests the " +
                "data-driven branch that DataDrivenFillColor_StillVariesPerFeature already covers.");

            Material mat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Mesh mesh = TestTileMeshBuilder.BuildFill(new[] { SquareFeature(1000) }, paint, Zoom, Extent, Tile);
            try
            {
                Color uniform = ShaderBaseColor(mat);
                Color stream  = StreamColor(mesh, "constant fill-color");
                Color effective = new Color(uniform.r * stream.r, uniform.g * stream.g, uniform.b * stream.b, 1f);

                AssertRgbEquals(AuthoredSrgb.linear, effective,
                    $"a CONSTANT fill-color must reach the fragment exactly once. uniform={Fmt(uniform)} " +
                    $"stream={Fmt(stream)}. Both carrying it renders the colour SQUARED");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        /// <summary>Tooth 4, fill kind. Same shape as the line/fill-extrusion rows above.</summary>
        [Test]
        public void DataDrivenFillColor_StillVariesPerFeature()
        {
            const string ddColor = "[\"match\",[\"get\",\"cat\"],\"a\",\"#6699CC\",\"b\",\"#CC9966\",\"#000000\"]";
            var paint = TestStyle.FillPaint($"{{\"fill-color\":{ddColor}}}");
            Assert.IsTrue(paint.Color.DependsOnFeature, "precondition: the fixture's fill-color must be data-driven.");

            Material mat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            Mesh mesh = TestTileMeshBuilder.BuildFill(
                new[] { SquareFeature(1000, Cat("a")), SquareFeature(2000, Cat("b")) }, paint, Zoom, Extent, Tile);
            try
            {
                Assert.GreaterOrEqual(DistinctStreamColors(mesh, "data-driven fill-color").Count, 2,
                    "a data-driven fill-color must still bake ≥2 distinct COLOR-stream values — gating the " +
                    "bake on the WRONG side of DependsOnFeature trades one branch for the other.");

                Color uniform = mat.GetColor(ShaderProperties.PropertyId.BaseColor);
                AssertRgbEquals(Color.white, uniform,
                    "a data-driven fill-color must leave _BaseColor at the white identity — binding it here " +
                    "would tint every feature and re-create the double-apply on the other branch");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        /// <summary>
        /// fill's own P4 wrinkle: a CONSTANT fill-color's alpha rides <c>_BaseColor.a</c>, and a data-driven
        /// fill-opacity is baked into the SAME stream a constant colour would have used. This is where the
        /// product <c>FillSortKeyAndOpacityTests.DataDrivenOpacity_IsTheStreamsOnlyAlphaCarrier</c> used to
        /// assert on one carrier now lives — see that test for the mirror.
        /// </summary>
        [Test]
        public void ConstantFillColorAlpha_IsNotAppliedTwice()
        {
            var paint = TestStyle.FillPaint($"{{\"fill-color\":{AuthoredRgbaHalfAlpha},\"fill-opacity\":[\"get\",\"op\"]}}");
            Assert.That((float)paint.Color.Evaluate(Zoom).A, Is.EqualTo(HalfAlpha).Within(Tol),
                "precondition: the fixture colour must carry the authored alpha 0.5.");
            Assert.IsTrue(paint.Opacity.DependsOnFeature, "precondition: fill-opacity must be data-driven.");

            Material mat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            Assert.IsNotNull(mat, "Map/Fill base material must be configured.");
            var applier = new ZoomStyleApplier(mat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));

            var props = new Dictionary<string, Value> { { "op", Value.Number(0.4) } };
            Mesh mesh = TestTileMeshBuilder.BuildFill(new[] { SquareFeature(1000, props) }, paint, Zoom, Extent, Tile);
            try
            {
                float uniformA = mat.GetColor(ShaderProperties.PropertyId.BaseColor).a;
                float streamA  = StreamColor(mesh, "constant fill-color alpha, data-driven opacity").a;
                float opacity  = mat.GetFloat(ShaderProperties.PropertyId.Opacity);
                float composed = uniformA * streamA * opacity;

                Assert.That(composed, Is.EqualTo(0.2f).Within(Tol),
                    $"a CONSTANT fill-color's authored ALPHA (0.5) × a data-driven fill-opacity (0.4) must " +
                    $"reach the fragment exactly once each. _BaseColor.a={uniformA:F4} vColor.a={streamA:F4} " +
                    $"_Opacity={opacity:F4} → {composed:F4}.");
            }
            finally { Object.DestroyImmediate(mat); if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        // ── The premise the line alpha delta rests on ────────────────────────────────────────────

        /// <summary>
        /// <c>Line_LitInput</c> feeds <c>_BaseColor.a</c> through <c>AlphaModulate</c>, which premultiplies the
        /// albedo only when <c>_ALPHAPREMULTIPLY_ON</c> is set. A cloned line material must not carry it, or
        /// the alpha the Lit fragment now re-applies would darken the albedo a second time.
        /// <c>LineTweaker.ApplyPainterContract</c> sets straight-alpha blend factors but deliberately does not
        /// sync keywords, so this is a property of the committed base <c>.mat</c> — read it, don't assume it.
        /// </summary>
        [Test]
        public void ClonedLineMaterial_HasNoAlphaPremultiplyKeyword()
        {
            Material mat = MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load());
            try
            {
                Assert.IsFalse(mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"),
                    "a line material must composite STRAIGHT alpha. With premultiply on, Line_LitInput's " +
                    "AlphaModulate scales the albedo by _BaseColor.a and the fragment's replaced alpha " +
                    "applies it again — a constant translucent line would render double-dark.");
            }
            finally { Object.DestroyImmediate(mat); }
        }
    }
}
#endif
