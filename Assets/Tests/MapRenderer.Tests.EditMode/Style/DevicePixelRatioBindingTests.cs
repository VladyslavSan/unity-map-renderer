#if UNITY_EDITOR
// Unity EditMode only — real Materials via MaterialFactory / MapMaterialSet.
// NOT registered in Tools/core-tests/core-tests.csproj.
//
// S107 Stage 2, T4 — table-driven over the px-valued style surface, split by HOW each property is observed.
//
// The universally-true invariant, and the reason this file's title is not "every px property is multiplied":
//
//     For each px-valued property, the quantity it controls, MEASURED IN DEVICE PIXELS, is linear in dpr.
//
// For the material-bound family (line-width/-gap-width/-offset/-blur, the halo pair, the two translates)
// that lands as "the uniform doubles at dpr 2", because their shader consumers measure the physical
// framebuffer. For text-size / text-padding / icon-padding it does NOT: their consumers already divide by
// the LOGICAL viewport, so the value must stay untouched and the device footprint doubles anyway. A row
// asserting "padding is scaled" would be WRONG, and is deliberately absent.
//
// These uniform readbacks pin the REGRESSION (a property silently dropping out of the conversion); they
// cannot judge whether "device" was the right space for it — only the rendered ratios in
// DevicePixelRatioSnapshotTests can, which is why that fixture is the load-bearing one.

using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Backend;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using Line = MapRenderer.Core.Style.Line;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    public class DevicePixelRatioBindingTests : BaseTestFixture
    {
        private const double Zoom = 8.0;

        /// <summary>Every px-valued line paint property at once, each a distinct value so a row cannot pass
        /// by reading its neighbour's uniform.</summary>
        private const string LinePaintStyleJson = @"{
    ""version"": 8,
    ""layers"": [
        { ""id"": ""road"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""l"",
          ""paint"": {
              ""line-width"": 6,
              ""line-gap-width"": 3,
              ""line-offset"": 4,
              ""line-blur"": 2,
              ""line-translate"": [5, -7]
          } }
    ]
}";

        /// <summary>Data-driven width: the evaluated width is baked per-vertex by StyledLineTileBuilder, so
        /// the uniform carries the BASE only. That base is a px quantity like any other.</summary>
        private const string DataDrivenWidthStyleJson = @"{
    ""version"": 8,
    ""layers"": [
        { ""id"": ""road"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""l"",
          ""paint"": { ""line-width"": [""get"", ""w""] } }
    ]
}";

        private const string FillTranslateStyleJson = @"{
    ""version"": 8,
    ""layers"": [
        { ""id"": ""ground"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""l"",
          ""paint"": { ""fill-translate"": [9, -11] } }
    ]
}";

        private static T FirstLayer<T>(string json) where T : StyleLayer
            => (T)StyleParser.Parse(json).Layers[0];

        // ── Line: the four device-space float uniforms ───────────────────────────────────────────

        /// <summary>
        /// <b>T4 (line float rows).</b> Each of <c>_Width</c> / <c>_GapWidth</c> / <c>_LineOffset</c> /
        /// <c>_Blur</c> reads EXACTLY twice its dpr-1 value after <c>ApplyZoom(z, 2.0)</c>, and exactly the
        /// styled logical value at dpr 1 (the stage invariant, on the same material).
        /// </summary>
        [Test]
        public void LinePxUniforms_DoubleAtDpr2_AndAreStyledLogicalValuesAtDpr1()
        {
            var paint = FirstLayer<Line.StyleLayer>(LinePaintStyleJson).Paint;
            Material mat = Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            Assert.IsNotNull(mat, "Map/Line base material must be configured.");
            {
                var applier = new ZoomStyleApplier(mat);
                MaterialFactory.BindLinePaintToApplier(paint, applier, mat);

                (string name, int id, float styled)[] rows =
                {
                    ("line-width",     ShaderProperties.Line.PropertyId.Width,      6f),
                    ("line-gap-width", ShaderProperties.Line.PropertyId.GapWidth,   3f),
                    ("line-offset",    ShaderProperties.Line.PropertyId.LineOffset, 4f),
                    ("line-blur",      ShaderProperties.Line.PropertyId.Blur,       2f),
                };

                applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));
                foreach (var (name, id, styled) in rows)
                    Assert.That(mat.GetFloat(id), Is.EqualTo(styled).Within(1e-4f),
                        $"{name} at dpr 1 must be the styled logical value {styled} — dpr 1 is the IDENTITY, " +
                        "and every pre-existing golden in the suite depends on it.");

                applier.ApplyZoom(new StyleFrameInputs(Zoom, 2.0, 0.0));
                foreach (var (name, id, styled) in rows)
                    Assert.That(mat.GetFloat(id), Is.EqualTo(2f * styled).Within(1e-4f),
                        $"{name} must read {2f * styled} at dpr 2 (styled {styled} logical px × 2). Its shader " +
                        "consumer measures against _ScreenParams — the PHYSICAL framebuffer — so an unscaled " +
                        "value renders the road at its literal screen width on every panel density.");

                // Back to 1: the ratio is a live per-frame input, not a one-way latch. A binding that took the
                // bind-time constant shortcut would be frozen at whichever ratio it first saw.
                applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));
                foreach (var (name, id, styled) in rows)
                    Assert.That(mat.GetFloat(id), Is.EqualTo(styled).Within(1e-4f),
                        $"{name} must return to {styled} when the ratio returns to 1 (a window dragged back " +
                        "off a high-DPI panel).");
            }
        }

        /// <summary>
        /// <b>T4 (the data-driven row).</b> When <c>line-width</c> depends on the feature, the evaluated width
        /// is baked into the per-vertex <c>WidthScale</c> stream and the uniform carries the base, so
        /// <c>widthWorld = _Width × widthScale × pxToWorld</c>. The base is 1 at dpr 1 and must be <b>2</b> at
        /// dpr 2 — scaling the mesh bake instead would put the ratio inside the geometry, where a live ratio
        /// change could not reach it and <c>PreparedTileCache</c> would serve it stale.
        ///
        /// <para>This is the row that catches an implementation which converted only the non-data-driven
        /// branch: every other line row would still be green.</para>
        /// </summary>
        [Test]
        public void DataDrivenLineWidth_BaseUniform_IsTheRatioNotOne()
        {
            var paint = FirstLayer<Line.StyleLayer>(DataDrivenWidthStyleJson).Paint;
            Assert.IsTrue(paint.Width.DependsOnFeature,
                "precondition: line-width must parse as data-driven, or this row silently tests the " +
                "constant branch that the previous test already covers.");

            Material mat = Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            {
                var applier = new ZoomStyleApplier(mat);
                MaterialFactory.BindLinePaintToApplier(paint, applier, mat);
                int widthId = ShaderProperties.Line.PropertyId.Width;

                applier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));
                Assert.That(mat.GetFloat(widthId), Is.EqualTo(1f).Within(1e-4f),
                    "data-driven width: the base must be 1 at dpr 1 so the baked per-vertex width passes " +
                    "through unchanged (the pre-existing convention, byte-identical).");

                applier.ApplyZoom(new StyleFrameInputs(Zoom, 2.0, 0.0));
                Assert.That(mat.GetFloat(widthId), Is.EqualTo(2f).Within(1e-4f),
                    "data-driven width: the base must be 2 at dpr 2, so widthWorld = 2 × bakedPx × pxToWorld. " +
                    "Reading 1 here means the data-driven branch was left out of the conversion and every " +
                    "feature-styled road stays at its literal screen width.");
            }
        }

        // ── The two translates ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>T4 (translate rows).</b> <c>line-translate</c> and <c>fill-translate</c> are px offsets applied
        /// through the same <c>MapPixelsToWorld</c> call as the widths, so they sit in the identical device
        /// space and scale identically. Both components, including the negative one — a conversion applied to
        /// <c>x</c> only would pass a magnitude-blind check.
        /// </summary>
        [Test]
        public void TranslateUniforms_ScaleBothComponentsWithDpr()
        {
            Material lineMat = Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            Material fillMat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            {
                var lineApplier = new ZoomStyleApplier(lineMat);
                MaterialFactory.BindLinePaintToApplier(
                    FirstLayer<Line.StyleLayer>(LinePaintStyleJson).Paint, lineApplier, lineMat);
                var fillApplier = new ZoomStyleApplier(fillMat);
                MaterialFactory.BindFillPaintToApplier(
                    FirstLayer<Fill.StyleLayer>(FillTranslateStyleJson).Paint, fillApplier, fillMat);

                int lineId = ShaderProperties.Line.PropertyId.LineTranslate;
                int fillId = ShaderProperties.Fill.PropertyId.FillTranslate;

                void AssertTranslate(Material mat, int id, string what, float x, float y, double dpr)
                {
                    Vector4 v = mat.GetVector(id);
                    Assert.That(v.x, Is.EqualTo(x).Within(1e-4f),
                        $"{what}.x must be {x} at dpr {dpr} (got {v.x}).");
                    Assert.That(v.y, Is.EqualTo(y).Within(1e-4f),
                        $"{what}.y must be {y} at dpr {dpr} (got {v.y}) — a conversion applied to x only " +
                        "would pass a magnitude-blind check but skew every translated layer.");
                }

                lineApplier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));
                fillApplier.ApplyZoom(new StyleFrameInputs(Zoom, 1.0, 0.0));
                AssertTranslate(lineMat, lineId, "line-translate", 5f, -7f, 1.0);
                AssertTranslate(fillMat, fillId, "fill-translate", 9f, -11f, 1.0);

                lineApplier.ApplyZoom(new StyleFrameInputs(Zoom, 2.0, 0.0));
                fillApplier.ApplyZoom(new StyleFrameInputs(Zoom, 2.0, 0.0));
                // Both are consumed by the same MapPixelsToWorld call the widths are, so they are device px.
                AssertTranslate(lineMat, lineId, "line-translate", 10f, -14f, 2.0);
                AssertTranslate(fillMat, fillId, "fill-translate", 18f, -22f, 2.0);
            }
        }

        // ── The halo pair: moved out ──────────────────────────────────────────────────────────────

        // text-halo-width/-blur are no longer material uniforms to read back. They are evaluated PER FEATURE
        // onto the vertex stream and take the logical→device conversion at emit, against the LIVE ratio — so
        // the tooth has to read the emitted mesh. It lives in
        // SymbolHaloEmitTests.HaloWidthAndBlur_ScaleTogetherWithDevicePixelRatio, which asserts the same two
        // claims this arm did: both terms scale, and they scale TOGETHER.
        //
        // Kept from the old arm because it is still true and still worth not re-learning: do NOT rewrite this
        // as a RENDERED softness ratio. The halo's rendered transition is (_SdfAaDevicePx + blur) — an AA
        // constant in device px summed with the style's blur — so that ratio is not 2 even on a correct build.

        // A zoom-expression text-halo-width no longer tracks the live zoom through a uniform either: like
        // every other text-* paint term it is evaluated PER FEATURE at tile build (SymbolFeatureExtractor),
        // so it moves when the tile is rebuilt, not every frame. The T5 tooth that read _HaloWidthPx back off
        // the material went with the uniform.

        // ── The BRG arm (design-doc C2) ──────────────────────────────────────────────────────────

        /// <summary>
        /// <b>T4 (backend row).</b> The three render backends inherit a material-level conversion for free,
        /// because <c>BrgTileRenderer.PackMaterialProps</c> reads every property back off the
        /// <see cref="Material"/>. This converts that argument into a tooth: after a dpr-2 <c>ApplyZoom</c>,
        /// the BRG-packed <c>_Width</c> equals the material's — which is the scaled value, not the styled one.
        /// </summary>
        [Test]
        public void BrgPackedWidth_InheritsTheDeviceConversion()
        {
            var style = StyleParser.Parse(LinePaintStyleJson);
            using var set   = new RenderLayerSet();
            set.Build(style, Zoom, MapMaterialSetTestUtil.Load());
            Assert.That(set.Count, Is.EqualTo(1), "line-only style must build exactly one render layer.");

            set.ApplyZoom(new StyleFrameInputs(Zoom, 2.0, 0.0));

            Material mat    = set[0].Material;
            int      widthId = ShaderProperties.Line.PropertyId.Width;
            float    matWidth = mat.GetFloat(widthId);
            Assert.That(matWidth, Is.EqualTo(12f).Within(1e-3f),
                "precondition: the layer material's _Width must already be the dpr-2 value (6 × 2). " +
                "Without this the backend assertion below would pass for the wrong reason.");

            using var brg  = new BrgTileRenderer(new[] { mat });
            var mesh = Track(new Mesh());
            {
                int handle = brg.AddTileLayer(mesh, double3.zero, 0, new TileId { Z = 0, X = 0, Y = 0 });
                brg.Rebuild(SceneFrame.Mercator(double2.zero));

                float packedWidth = brg.GetInstancePropValue(handle, widthId);
                Assert.That(packedWidth, Is.Not.NaN,
                    "GetInstancePropValue(_Width) returned NaN — no plan entry for _Width.");
                Assert.That(packedWidth, Is.EqualTo(matWidth).Within(1e-3f),
                    $"BRG packed _Width ({packedWidth}) must equal the material's ({matWidth}). The backends " +
                    "read the material, so a conversion done at the material level is inherited by all three — " +
                    "this pins that, rather than leaving it as an argument.");
            }
        }

        // ── The LOGICAL family: padding must NOT be scaled ───────────────────────────────────────

        /// <summary>
        /// <b>T4 (logical rows).</b> <c>text-padding</c> and <c>icon-padding</c> are collided in
        /// <c>SymbolStagingMath</c> against anchors that <c>SymbolProjectionJob</c> projects with
        /// <see cref="MapRenderer.Unity.Rendering.Map.MapCamera.ViewportLogicalPx"/> — the SAME space
        /// <c>text-size</c> and the glyph bounds live in. One space, logical, factor 1: the value reaching the
        /// collision grid must be the styled number, and the staging path must contain no device conversion
        /// at all.
        ///
        /// <para>Their device footprint still doubles at dpr 2 — via the logical viewport halving, exactly
        /// like <c>text-size</c> (measured in <c>DevicePixelRatioSnapshotTests</c>). That is the invariant
        /// holding, not an exception to it. A row asserting "padding is scaled" would be wrong.</para>
        /// </summary>
        [Test]
        public void TextAndIconPadding_StayLogical_NoDeviceConversionInTheStagingPath()
        {
            // The staging/extraction path must not acquire a device-pixel conversion. Source-level because
            // that is the only observation point: nothing downstream of here takes a ratio to compare against.
            foreach (string relativePath in new[]
                     {
                         "Assets/Code/MapRenderer.Unity/Text/SymbolFeatureExtractor.cs",
                         "Assets/Code/MapRenderer.Core/Text/Placement/SymbolStagingMath.cs",
                         "Assets/Code/MapRenderer.Jobs/Symbols/SymbolProjectionJob.cs",
                     })
            {
                string path = Path.Combine(
                    Directory.GetParent(Application.dataPath)!.FullName, relativePath);
                FileAssert.Exists(path, $"the padding path's source must exist at {relativePath}.");
                string source = File.ReadAllText(path);

                Assert.That(source, Does.Not.Contain("LogicalToDevicePx"),
                    $"{relativePath} must not convert to device px: label padding, text-size and the glyph " +
                    "bounds are collided in ONE space — the ViewportLogicalPx space SymbolProjectionJob " +
                    "projects anchors into. Multiplying padding there would collide logical boxes against " +
                    "device-px padding and over-reject labels on a dense panel.");
                Assert.That(source, Does.Not.Contain("DeviceToLogicalPx"),
                    $"{relativePath} must not convert FROM device px either (S108). Named separately from " +
                    "LogicalToDevicePx above because it is not a substring of it — without this line the " +
                    "staging path could acquire the new conversion without tripping the fence.");
                Assert.That(source, Does.Not.Contain("DevicePixelRatio"),
                    $"{relativePath} must not read a device-pixel ratio at all — the division it needs has " +
                    "already happened, once, in MapCamera.ViewportLogicalPx.");
            }
        }
    }
}
#endif
