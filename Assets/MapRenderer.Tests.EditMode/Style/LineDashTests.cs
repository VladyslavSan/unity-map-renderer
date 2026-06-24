// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using Unity.Mathematics;
using Line = MapRenderer.Core.Style.Line;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S43 / S60 — <see cref="Line.LineDash"/>: dash coverage function, zoom-stability, width
    /// coupling, dasharray parse/eval, and shader structure (tooth 4 — greppable assertion).
    ///
    /// S60: <c>TryEvaluatePattern</c> now returns <c>float4 packed + int count</c> (alloc-free);
    /// <c>Pack</c> deleted. Tests updated accordingly. <c>DashCoverage(float[])</c> remains for
    /// the CPU mirror. All passing count preserved.
    /// </summary>
    [TestFixture]
    public class LineDashTests
    {
        // ── Tooth 1 (DECISIVE): Dash ratio 2:1 for [2, 1] ────────────────────────────────────

        [Test]
        public void DashCoverage_Pattern_2_1_ProducesCorrectOnOffRatio()
        {
            float[] pattern = new float[] { 2f, 1f };
            double widthM = 1.0;

            int onCount = 0, offCount = 0;
            int totalSamples = 30000;
            for (int i = 0; i < totalSamples; i++)
            {
                double dist = (double)i / totalSamples * 30.0;
                float cov = Line.LineDash.DashCoverage(dist, widthM, pattern);
                if (cov >= 0.5f) onCount++;
                else offCount++;
            }

            double ratio = (double)onCount / offCount;
            Assert.That(ratio, Is.InRange(1.9, 2.1),
                $"Expected on:off ratio ≈ 2:1 for [2,1], got {ratio:F4} (on={onCount}, off={offCount})");
        }

        [Test]
        public void DashCoverage_SolidControl_SingleEntry_AllOn()
        {
            float[] pattern = new float[] { 1f };
            for (int i = 0; i < 100; i++)
            {
                float cov = Line.LineDash.DashCoverage(i * 0.17, 1.0, pattern);
                Assert.That(cov, Is.EqualTo(1.0f),
                    $"[1] (odd-length) must be solid identity, got {cov} at dist={i * 0.17}");
            }
        }

        [Test]
        public void DashCoverage_NullPattern_AllOn()
        {
            for (int i = 0; i < 20; i++)
            {
                float cov = Line.LineDash.DashCoverage(i * 0.5, 1.0, null);
                Assert.That(cov, Is.EqualTo(1.0f));
            }
        }

        [Test]
        public void DashCoverage_EmptyPattern_AllOn()
        {
            for (int i = 0; i < 20; i++)
            {
                float cov = Line.LineDash.DashCoverage(i * 0.5, 1.0, new float[0]);
                Assert.That(cov, Is.EqualTo(1.0f));
            }
        }

        [Test]
        public void DashCoverage_DegenerateWidthM_AllOn()
        {
            float[] pattern = new float[] { 2f, 1f };
            Assert.That(Line.LineDash.DashCoverage(5.0, 0.0, pattern), Is.EqualTo(1.0f));
            Assert.That(Line.LineDash.DashCoverage(5.0, -1.0, pattern), Is.EqualTo(1.0f));
        }

        // ── Tooth 1 (continued): On/Off regions are hard binary ──────────────────────────────

        [Test]
        public void DashCoverage_Pattern_2_1_HardBinaryAtCenterOfRuns()
        {
            float[] pattern = new float[] { 2f, 1f };
            double widthM = 1.0;

            Assert.That(Line.LineDash.DashCoverage(1.0, widthM, pattern), Is.EqualTo(1.0f),
                "Center of on-run (distU=1) must be 1.0");
            Assert.That(Line.LineDash.DashCoverage(2.5, widthM, pattern), Is.EqualTo(0.0f),
                "Center of off-run (distU=2.5) must be 0.0");
            Assert.That(Line.LineDash.DashCoverage(4.0, widthM, pattern), Is.EqualTo(1.0f),
                "Center of second on-run (distU=4) must be 1.0");
            Assert.That(Line.LineDash.DashCoverage(5.5, widthM, pattern), Is.EqualTo(0.0f),
                "Center of second off-run (distU=5.5) must be 0.0");
        }

        // ── Tooth 2: Zoom-stable ──────────────────────────────────────────────────────────────

        [Test]
        public void DashCoverage_ZoomStable_CycleCountTimesWidthMIsConstantAcrossZoom()
        {
            double zoom1 = 14.0;
            double zoom2 = 15.0;
            const double widthPx = 4.0;
            double mpp1 = CameraPoseMath.MetersPerPixel(zoom1);
            double mpp2 = CameraPoseMath.MetersPerPixel(zoom2);
            double widthM_z1 = widthPx * mpp1;
            double widthM_z2 = widthPx * mpp2;

            Assert.That(widthM_z1, Is.Not.EqualTo(widthM_z2).Within(1e-6),
                "widthM at zoom14 and zoom15 must differ.");
            Assert.That(widthM_z2, Is.LessThan(widthM_z1),
                "Higher zoom must yield smaller widthM.");

            float[] pattern = new float[] { 2f, 1f };
            double totalDistM = 100.0;
            const int samples = 100000;

            int cycles1 = CountCycles(totalDistM, widthM_z1, pattern, samples);
            int cycles2 = CountCycles(totalDistM, widthM_z2, pattern, samples);

            double product1 = cycles1 * widthM_z1;
            double product2 = cycles2 * widthM_z2;
            double relErr = Math.Abs(product1 - product2) / ((product1 + product2) * 0.5);
            Assert.That(relErr, Is.LessThanOrEqualTo(0.05),
                $"cycles*widthM must be zoom-stable (no drift). " +
                $"z14: cycles={cycles1} widthM={widthM_z1:F4} product={product1:F4}; " +
                $"z15: cycles={cycles2} widthM={widthM_z2:F4} product={product2:F4}; " +
                $"relErr={relErr:P2}");
        }

        [Test]
        public void DashCoverage_ZoomStable_CycleCountScalesWithWidthM()
        {
            float[] pattern = new float[] { 2f, 1f };
            double totalDistM = 60.0;
            double widthM_narrow = 1.0;
            double widthM_wide   = 2.0;

            int cycles_narrow = CountCycles(totalDistM, widthM_narrow, pattern, 10000);
            int cycles_wide   = CountCycles(totalDistM, widthM_wide,   pattern, 10000);

            Assert.That(cycles_wide * 2, Is.EqualTo(cycles_narrow),
                $"Doubling widthM must halve cycle count: narrow={cycles_narrow}, wide={cycles_wide}");
        }

        // ── Tooth 3: Width-coupled ────────────────────────────────────────────────────────────

        [Test]
        public void DashCoverage_WidthCoupled_DoublingWidthMDoublesOnOffLengths()
        {
            float[] pattern = new float[] { 2f, 1f };

            Assert.That(Line.LineDash.DashCoverage(1.0, 1.0, pattern), Is.EqualTo(1.0f));
            Assert.That(Line.LineDash.DashCoverage(2.5, 1.0, pattern), Is.EqualTo(0.0f));
            Assert.That(Line.LineDash.DashCoverage(2.0, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, on-run center at dist=2 must be 1.0");
            Assert.That(Line.LineDash.DashCoverage(5.0, 2.0, pattern), Is.EqualTo(0.0f),
                "At 2x width, off-run center at dist=5 must be 0.0");
            Assert.That(Line.LineDash.DashCoverage(3.0, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, dist=3 still in on-run");
            Assert.That(Line.LineDash.DashCoverage(3.9, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, dist=3.9 still in on-run (boundary)");
        }

        // ── Tooth 5 (non-regression): solid control via DashCount=0 identity ─────────────────

        [Test]
        public void DashCoverage_OddLength_Is_SolidIdentity()
        {
            float[] odd1 = new float[] { 1f };
            float[] odd3 = new float[] { 2f, 1f, 3f };

            for (int i = 0; i < 20; i++)
            {
                Assert.That(Line.LineDash.DashCoverage(i * 0.7, 1.0, odd1), Is.EqualTo(1.0f),
                    "[1] must be solid identity");
                Assert.That(Line.LineDash.DashCoverage(i * 0.7, 1.0, odd3), Is.EqualTo(1.0f),
                    "[2,1,3] (odd) must be solid identity");
            }
        }

        // ── S60: TryEvaluatePattern → float4 + count (alloc-free, replaces Pack) ─────────────

        [Test]
        public void TryEvaluatePattern_TwoEntry_PackedCorrectly()
        {
            // [2, 1] → packed.x=2, packed.y=1, count=2
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[2, 1]"));
            Assert.IsNotNull(expr, "ParseDashArray must succeed for [2,1].");

            bool ok = Line.LineDash.TryEvaluatePattern(expr, 14.0, out float4 packed, out int count);
            Assert.IsTrue(ok, "TryEvaluatePattern must return true for a valid [2,1] pattern.");
            Assert.AreEqual(2, count, "Count must be 2 for a two-entry pattern.");
            Assert.AreEqual(2f, packed.x, 1e-6f, "packed.x must be 2 (first dash length).");
            Assert.AreEqual(1f, packed.y, 1e-6f, "packed.y must be 1 (first gap length).");
            Assert.AreEqual(0f, packed.z, 1e-6f, "packed.z must be 0 (unused).");
            Assert.AreEqual(0f, packed.w, 1e-6f, "packed.w must be 0 (unused).");
        }

        [Test]
        public void TryEvaluatePattern_FourEntry_PackedCorrectly()
        {
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[4, 1, 1, 1]"));
            bool ok = Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            Assert.IsTrue(ok);
            Assert.AreEqual(4, count);
            Assert.AreEqual(4f, packed.x, 1e-6f);
            Assert.AreEqual(1f, packed.y, 1e-6f);
            Assert.AreEqual(1f, packed.z, 1e-6f);
            Assert.AreEqual(1f, packed.w, 1e-6f);
        }

        [Test]
        public void TryEvaluatePattern_OddEntry_ReturnsFalse_CountZero()
        {
            // [2, 1, 3] is odd-length → solid identity → false, count=0
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[2, 1, 3]"));
            bool ok = Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            Assert.IsFalse(ok, "Odd-length must return false (solid identity).");
            Assert.AreEqual(0, count, "Count must be 0 for odd-length (solid identity sentinel).");
        }

        [Test]
        public void TryEvaluatePattern_Null_ReturnsFalse()
        {
            bool ok = Line.LineDash.TryEvaluatePattern(null, 10.0, out float4 packed, out int count);
            Assert.IsFalse(ok, "Null expression must return false.");
            Assert.AreEqual(0, count);
        }

        [Test]
        public void TryEvaluatePattern_CapAt4Entries()
        {
            // 5-entry → truncated to 4 (even) → count=4, ok=true
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[1, 1, 1, 1, 1]"));
            bool ok = Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            // After truncation to 4 entries (even), count=4
            Assert.AreEqual(4, count, "5 entries truncated to 4 (even) → count=4");
        }

        // ── ParseDashArray + TryEvaluatePattern: expression system integration ────────────────

        private static MapRenderer.Core.Expressions.Expression DashExpr(string json)
            => Line.LineDash.ParseDashArray(JsonParser.Parse(json));

        [Test]
        public void ParseDashArray_ConstantBareArray_IsConstantKind_AndEvaluates()
        {
            var expr = DashExpr("[2, 1]");
            Assert.That(expr, Is.Not.Null);
            Assert.That(expr.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant),
                "A bare constant dasharray must classify as Constant.");

            bool ok = Line.LineDash.TryEvaluatePattern(expr, 14.0, out float4 packed, out int count);
            Assert.That(ok, Is.True);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(packed.x, Is.EqualTo(2f));
            Assert.That(packed.y, Is.EqualTo(1f));
        }

        [Test]
        public void ParseDashArray_LiteralExpression_IsConstantKind()
        {
            var expr = DashExpr("[\"literal\", [4, 2]]");
            Assert.That(expr.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant));
            Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(packed.x, Is.EqualTo(4f));
            Assert.That(packed.y, Is.EqualTo(2f));
        }

        [Test]
        public void ParseDashArray_ZoomStep_IsZoomKind_AndPicksCorrectStep()
        {
            var expr = DashExpr("[\"step\", [\"zoom\"], [1,1], 10, [2,1], 14, [4,1]]");
            Assert.That(expr.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Zoom),
                "A zoom-step dasharray must classify as Zoom.");

            Line.LineDash.TryEvaluatePattern(expr, 9.0,  out float4 p9, out _);
            Assert.That(p9.x, Is.EqualTo(1f), "zoom=9: default [1,1]");

            Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 p10, out _);
            Assert.That(p10.x, Is.EqualTo(2f), "zoom=10: [2,1]");

            Line.LineDash.TryEvaluatePattern(expr, 14.0, out float4 p14, out _);
            Assert.That(p14.x, Is.EqualTo(4f), "zoom=14: [4,1]");

            Line.LineDash.TryEvaluatePattern(expr, 15.0, out float4 p15, out _);
            Assert.That(p15.x, Is.EqualTo(4f), "zoom=15: still [4,1]");
        }

        [Test]
        public void ParseDashArray_Null_ReturnsNull_AndEvaluatesToSolid()
        {
            Assert.That(Line.LineDash.ParseDashArray(null), Is.Null);
            bool ok = Line.LineDash.TryEvaluatePattern(null, 10.0, out float4 packed, out int count);
            Assert.That(ok, Is.False);
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void ParseDashArray_DataDriven_DegradesToSolid()
        {
            var expr = DashExpr("[\"match\", [\"get\", \"cls\"], \"a\", [2,1], [4,2]]");
            Assert.That(MapRenderer.Core.Expressions.ExpressionKinds.DependsOnFeature(expr.Kind), Is.True);
            Assert.That(Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count), Is.False,
                "Data-driven dasharray must not crash — it degrades to solid (no pattern).");
            Assert.That(count, Is.EqualTo(0));
        }

        // ── Tooth 4 (GREPPABLE): Shader consumes per-vertex sideAndDist.y as dashU ────────────

        [Test]
        public void ShaderStructure_MapLineForwardPass_ConsumesDistanceAlongAsPerVertexAttribute()
        {
            string[] starts = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory,
            };

            string hlslPath = null;
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir,
                        "Assets", "MapRenderer.Unity", "Shaders", "Map", "Line", "MapLineForwardPass.hlsl");
                    if (File.Exists(candidate)) { hlslPath = candidate; break; }
                    dir = Directory.GetParent(dir)?.FullName;
                }
                if (hlslPath != null) break;
            }

            Assert.That(hlslPath, Is.Not.Null,
                $"MapLineForwardPass.hlsl not found. Tried walking up 16 levels from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
            string text = File.ReadAllText(hlslPath);

            Assert.That(text, Does.Contain("sideAndDist.y"),
                "Vertex shader must read input.sideAndDist.y (per-vertex distanceAlong) for dashU.");
            Assert.That(text, Does.Contain("output.dashU"),
                "Vertex shader must write output.dashU.");
            Assert.That(text, Does.Contain("input.dashU"),
                "Fragment shader must read input.dashU.");
            Assert.That(text, Does.Contain("_DashCount"),
                "Shader must have _DashCount guard for solid identity.");
        }

        // ── LinePaint.HasDashArray parse integration ──────────────────────────────────────────

        private static MapRenderer.Core.Style.StyleLayer MakeDashLayer(string paintJson)
        {
            return new MapRenderer.Core.Style.StyleLayer
            {
                Id          = "test-dash",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Line,
                SourceLayer = "roads",
                PaintJson   = paintJson != null ? JsonParser.Parse(paintJson) : null,
            };
        }

        [Test]
        public void LinePaint_HasDashArray_FalseWhenAbsent()
        {
            var layer = MakeDashLayer("{\"line-width\":2}");
            var paint = new Line.PaintProperties(layer);
            Assert.That(paint.HasDashArray, Is.False);
            Assert.That(paint.DashArray, Is.Null);
        }

        [Test]
        public void LinePaint_HasDashArray_TrueWhenPresent()
        {
            var layer = MakeDashLayer("{\"line-dasharray\":[2,1]}");
            var paint = new Line.PaintProperties(layer);
            Assert.That(paint.HasDashArray, Is.True);
            Assert.That(paint.DashArray, Is.Not.Null);
            Assert.That(paint.DashArrayKind,
                Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant));
        }

        [Test]
        public void LinePaint_HasDashArray_IsNotInertFallback()
        {
            var layer = MakeDashLayer("{\"line-dasharray\":[2,1]}");
            var paint = new Line.PaintProperties(layer);
            Assert.That(paint.IsInertFallback, Is.False,
                "Line layer with line-dasharray must not be IsInertFallback.");
        }

        // ── Helper: count on-cycles over a line span ──────────────────────────────────────────

        private static int CountCycles(double totalDistM, double widthM, float[] pattern, int samples)
        {
            bool wasOn = false;
            int transitionCount = 0;
            for (int i = 0; i < samples; i++)
            {
                double dist = (double)i / samples * totalDistM;
                bool isOn = Line.LineDash.DashCoverage(dist, widthM, pattern) >= 0.5f;
                if (i > 0 && wasOn != isOn)
                    transitionCount++;
                wasOn = isOn;
            }
            return (transitionCount + 1) / 2;
        }
    }
}
