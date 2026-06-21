// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S43 — <see cref="LineDash"/>: dash coverage function, zoom-stability, width coupling,
    /// dasharray parse/eval, pack/unpack, and shader structure (tooth 4 — greppable assertion).
    ///
    /// All CPU-side tests. The in-shader formula mirrors these (MapLineForwardPass.hlsl).
    /// </summary>
    [TestFixture]
    public class LineDashTests
    {
        // ── Tooth 1 (DECISIVE): Dash ratio 2:1 for [2, 1] ────────────────────────────────────

        [Test]
        public void DashCoverage_Pattern_2_1_ProducesCorrectOnOffRatio()
        {
            // For pattern [2, 1], one cycle = 3 units. On=2/3, Off=1/3 of any full cycle.
            // Sample densely over multiple cycles and measure the on/off run-length ratio.
            float[] pattern = new float[] { 2f, 1f };
            double widthM = 1.0;

            int onCount = 0, offCount = 0;
            int totalSamples = 30000;
            // Sample distanceAlong from 0 to 10 cycles worth (period=3, widthM=1, so total=30).
            for (int i = 0; i < totalSamples; i++)
            {
                double dist = (double)i / totalSamples * 30.0;
                float cov = LineDash.DashCoverage(dist, widthM, pattern);
                if (cov >= 0.5f) onCount++;
                else offCount++;
            }

            // Expected: onCount/offCount ≈ 2.0 (2:1 ratio).
            // Allow 5% tolerance on the ratio to account for floating-point boundary effects.
            double ratio = (double)onCount / offCount;
            Assert.That(ratio, Is.InRange(1.9, 2.1),
                $"Expected on:off ratio ≈ 2:1 for [2,1], got {ratio:F4} (on={onCount}, off={offCount})");
        }

        [Test]
        public void DashCoverage_SolidControl_SingleEntry_AllOn()
        {
            // [1] is an odd-length array → treated as solid identity (no gap).
            float[] pattern = new float[] { 1f };
            for (int i = 0; i < 100; i++)
            {
                float cov = LineDash.DashCoverage(i * 0.17, 1.0, pattern);
                Assert.That(cov, Is.EqualTo(1.0f),
                    $"[1] (odd-length) must be solid identity, got {cov} at dist={i * 0.17}");
            }
        }

        [Test]
        public void DashCoverage_NullPattern_AllOn()
        {
            // null pattern → solid identity.
            for (int i = 0; i < 20; i++)
            {
                float cov = LineDash.DashCoverage(i * 0.5, 1.0, null);
                Assert.That(cov, Is.EqualTo(1.0f));
            }
        }

        [Test]
        public void DashCoverage_EmptyPattern_AllOn()
        {
            // empty pattern → solid identity.
            for (int i = 0; i < 20; i++)
            {
                float cov = LineDash.DashCoverage(i * 0.5, 1.0, new float[0]);
                Assert.That(cov, Is.EqualTo(1.0f));
            }
        }

        [Test]
        public void DashCoverage_DegenerateWidthM_AllOn()
        {
            // widthM <= 0 → solid identity.
            float[] pattern = new float[] { 2f, 1f };
            Assert.That(LineDash.DashCoverage(5.0, 0.0, pattern), Is.EqualTo(1.0f));
            Assert.That(LineDash.DashCoverage(5.0, -1.0, pattern), Is.EqualTo(1.0f));
        }

        // ── Tooth 1 (continued): On/Off regions are hard binary (no uniform alpha) ─────────────

        [Test]
        public void DashCoverage_Pattern_2_1_HardBinaryAtCenterOfRuns()
        {
            // At the center of "on" runs → 1.0. At the center of "off" runs → 0.0.
            float[] pattern = new float[] { 2f, 1f };
            double widthM = 1.0;
            // period = 3. On: [0,2), Off: [2,3).

            // Center of on-run at distU = 0 + 1.0 = 1.0 → coverage=1.
            Assert.That(LineDash.DashCoverage(1.0, widthM, pattern), Is.EqualTo(1.0f),
                "Center of on-run (distU=1) must be 1.0");

            // Center of off-run at distU = 2 + 0.5 = 2.5 → coverage=0.
            Assert.That(LineDash.DashCoverage(2.5, widthM, pattern), Is.EqualTo(0.0f),
                "Center of off-run (distU=2.5) must be 0.0");

            // Second cycle: on=3..5, off=5..6.
            Assert.That(LineDash.DashCoverage(4.0, widthM, pattern), Is.EqualTo(1.0f),
                "Center of second on-run (distU=4) must be 1.0");
            Assert.That(LineDash.DashCoverage(5.5, widthM, pattern), Is.EqualTo(0.0f),
                "Center of second off-run (distU=5.5) must be 0.0");
        }

        // ── Tooth 2: Zoom-stable (dash cycle count tracks widthM, no drift under zoom) ──────────
        //
        // The px→m formula (S43 D2 / CameraPoseMath.MetersPerPixel):
        //   metersPerPixel = EarthCircumference / (TilePixelSize * 2^zoom)
        // widthM = widthPx * metersPerPixel.
        //
        // Zoom-stability means: the dash cycle count over a fixed world segment times widthM is
        // constant. Doubling zoom halves metersPerPixel → halves widthM → doubles cycle count,
        // but cycles * widthM stays fixed. This is the "no-drift" invariant.
        //
        // The test binds to CameraPoseMath.MetersPerPixel (single source of truth) rather than
        // hardcoding constants, and guards against tautology by asserting widthM_z1 != widthM_z2.

        [Test]
        public void DashCoverage_ZoomStable_CycleCountTimesWidthMIsConstantAcrossZoom()
        {
            // Two distinct zoom levels that produce genuinely different metersPerPixel.
            double zoom1 = 14.0;
            double zoom2 = 15.0;  // one zoom step higher: mpp halves, so widthM halves.

            // A constant pixel-width (e.g. 4px road line) is multiplied by mpp to give world-meters.
            const double widthPx = 4.0;
            double mpp1 = CameraPoseMath.MetersPerPixel(zoom1);
            double mpp2 = CameraPoseMath.MetersPerPixel(zoom2);

            double widthM_z1 = widthPx * mpp1;
            double widthM_z2 = widthPx * mpp2;

            // Anti-tautology guard: the two widths MUST differ.
            Assert.That(widthM_z1, Is.Not.EqualTo(widthM_z2).Within(1e-6),
                "widthM at zoom14 and zoom15 must differ — a constant input would make this test a no-op.");

            // Sanity: higher zoom → smaller metersPerPixel → smaller widthM.
            Assert.That(widthM_z2, Is.LessThan(widthM_z1),
                "Higher zoom must yield smaller widthM (mpp shrinks as 2^zoom grows).");

            float[] pattern = new float[] { 2f, 1f };

            // A fixed world-length segment (100 m at zoom 14 — ~10× the widthM so we get
            // enough cycles for a meaningful count and ±1 rounding stays well under 5%).
            double totalDistM = 100.0;
            const int samples = 100000;

            int cycles1 = CountCycles(totalDistM, widthM_z1, pattern, samples);
            int cycles2 = CountCycles(totalDistM, widthM_z2, pattern, samples);

            // No-drift invariant: cycles * widthM must be (approximately) the same at both zooms.
            // (cycles ~ totalDistM / widthM / period, so cycles*widthM ~ totalDistM/period = const.)
            double product1 = cycles1 * widthM_z1;
            double product2 = cycles2 * widthM_z2;

            // Allow 5% relative tolerance for ±1 cycle quantisation.
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
            // When widthM doubles (same px-width at half resolution), dash cycle count halves
            // (dashes are twice as large in world space). This is the correct zoom behavior:
            // the dash count over a fixed world distance shrinks proportionally with widthM.
            float[] pattern = new float[] { 2f, 1f };
            double totalDistM = 60.0;
            double widthM_narrow = 1.0;
            double widthM_wide   = 2.0;

            int cycles_narrow = CountCycles(totalDistM, widthM_narrow, pattern, 10000);
            int cycles_wide   = CountCycles(totalDistM, widthM_wide,   pattern, 10000);

            // With widthM doubled: dashU = dist/widthM is halved → half as many cycles.
            Assert.That(cycles_wide * 2, Is.EqualTo(cycles_narrow),
                $"Doubling widthM must halve cycle count: narrow={cycles_narrow}, wide={cycles_wide}");
        }

        // ── Tooth 3: Width-coupled (doubling line-width doubles on/off lengths) ────────────────

        [Test]
        public void DashCoverage_WidthCoupled_DoublingWidthMDoublesOnOffLengths()
        {
            // Dash at widthM=1: on-run spans [0,2), off-run [2,3) in dashU.
            // Dash at widthM=2: same dashU, so on-run spans [0,4) in dist-along, off [4,6).
            float[] pattern = new float[] { 2f, 1f };

            // At widthM=1: on-center at dist=1 → 1.0, off-center at dist=2.5 → 0.0.
            Assert.That(LineDash.DashCoverage(1.0, 1.0, pattern), Is.EqualTo(1.0f));
            Assert.That(LineDash.DashCoverage(2.5, 1.0, pattern), Is.EqualTo(0.0f));

            // At widthM=2: on-run extends to dist=4 (dashU=2), off from dist=4 to 6 (dashU=2..3).
            // Center of on-run at dist=2 (dashU=1) → 1.0.
            // Center of off-run at dist=5 (dashU=2.5) → 0.0.
            Assert.That(LineDash.DashCoverage(2.0, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, on-run center at dist=2 must be 1.0");
            Assert.That(LineDash.DashCoverage(5.0, 2.0, pattern), Is.EqualTo(0.0f),
                "At 2x width, off-run center at dist=5 must be 0.0");

            // Doubling: on-run ends at dist=4 (not dist=2), proving 2x on-length.
            Assert.That(LineDash.DashCoverage(3.0, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, dist=3 still in on-run");
            Assert.That(LineDash.DashCoverage(3.9, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, dist=3.9 still in on-run (boundary)");
        }

        // ── Tooth 5 (non-regression): solid control via DashCount=0 identity ─────────────────

        [Test]
        public void DashCoverage_OddLength_Is_SolidIdentity()
        {
            // Per spec acceptance: odd-length → solid identity. Tests [1] and [3].
            float[] odd1 = new float[] { 1f };
            float[] odd3 = new float[] { 2f, 1f, 3f };

            for (int i = 0; i < 20; i++)
            {
                Assert.That(LineDash.DashCoverage(i * 0.7, 1.0, odd1), Is.EqualTo(1.0f),
                    "[1] must be solid identity");
                Assert.That(LineDash.DashCoverage(i * 0.7, 1.0, odd3), Is.EqualTo(1.0f),
                    "[2,1,3] (odd) must be solid identity");
            }
        }

        // ── Pack / unpack for shader ──────────────────────────────────────────────────────────

        [Test]
        public void LineDash_Pack_TwoEntry_Correct()
        {
            var (x, y, z, w, count) = LineDash.Pack(new float[] { 2f, 1f });
            Assert.That(x,     Is.EqualTo(2f));
            Assert.That(y,     Is.EqualTo(1f));
            Assert.That(z,     Is.EqualTo(0f));
            Assert.That(w,     Is.EqualTo(0f));
            Assert.That(count, Is.EqualTo(2f));
        }

        [Test]
        public void LineDash_Pack_FourEntry_Correct()
        {
            var (x, y, z, w, count) = LineDash.Pack(new float[] { 4f, 1f, 1f, 1f });
            Assert.That(x, Is.EqualTo(4f));
            Assert.That(y, Is.EqualTo(1f));
            Assert.That(z, Is.EqualTo(1f));
            Assert.That(w, Is.EqualTo(1f));
            Assert.That(count, Is.EqualTo(4f));
        }

        [Test]
        public void LineDash_Pack_OddEntry_ReturnsSolidIdentity()
        {
            var (x, y, z, w, count) = LineDash.Pack(new float[] { 2f, 1f, 3f });
            Assert.That(count, Is.EqualTo(0f), "Odd-length must produce count=0 (solid identity)");
        }

        [Test]
        public void LineDash_Pack_Null_ReturnsSolidIdentity()
        {
            var (x, y, z, w, count) = LineDash.Pack(null);
            Assert.That(count, Is.EqualTo(0f));
        }

        [Test]
        public void LineDash_Pack_CapAt4Entries()
        {
            // 5-entry → truncate to 4 (first 4 are even count → count=4, valid pattern).
            // The "odd-length" check applies AFTER truncation: first min(5, MaxEntries)=4 entries,
            // which is even → valid, count=4. Doc note: extra entries beyond MaxEntries are discarded.
            var (_, _, _, _, count5) = LineDash.Pack(new float[] { 1f, 1f, 1f, 1f, 1f });
            Assert.That(count5, Is.EqualTo(4f), "5 entries truncated to 4 (even) → count=4");

            // 6-entry → truncate to 4.
            var (x, y, z, w, count6) = LineDash.Pack(new float[] { 2f, 1f, 3f, 1f, 4f, 1f });
            Assert.That(count6, Is.EqualTo(4f), "6 entries truncated to 4");
            Assert.That(x, Is.EqualTo(2f));
            Assert.That(y, Is.EqualTo(1f));
            Assert.That(z, Is.EqualTo(3f));
            Assert.That(w, Is.EqualTo(1f));
        }

        // ── TryEvaluateDashArray: constant array parse ────────────────────────────────────────

        [Test]
        public void TryEvaluateDashArray_ConstantBareArray_Parsed()
        {
            JsonValue json = JsonParser.Parse("[2, 1]");
            bool ok = LineDash.TryEvaluateDashArray(json, 14.0, out float[] pattern);
            Assert.That(ok, Is.True);
            Assert.That(pattern, Is.Not.Null);
            Assert.That(pattern.Length, Is.EqualTo(2));
            Assert.That(pattern[0], Is.EqualTo(2f));
            Assert.That(pattern[1], Is.EqualTo(1f));
        }

        [Test]
        public void TryEvaluateDashArray_LiteralExpression_Parsed()
        {
            JsonValue json = JsonParser.Parse("[\"literal\", [4, 2]]");
            bool ok = LineDash.TryEvaluateDashArray(json, 10.0, out float[] pattern);
            Assert.That(ok, Is.True);
            Assert.That(pattern[0], Is.EqualTo(4f));
            Assert.That(pattern[1], Is.EqualTo(2f));
        }

        [Test]
        public void TryEvaluateDashArray_ZoomStep_PicksCorrectStep()
        {
            // ["step", ["zoom"], [1,1], 10, [2,1], 14, [4,1]]
            // At zoom=9 → default [1,1]
            // At zoom=10 → [2,1]
            // At zoom=14 → [4,1]
            JsonValue json = JsonParser.Parse(
                "[\"step\", [\"zoom\"], [1,1], 10, [2,1], 14, [4,1]]");

            LineDash.TryEvaluateDashArray(json, 9.0, out float[] p9);
            Assert.That(p9[0], Is.EqualTo(1f), "zoom=9: default [1,1]");

            LineDash.TryEvaluateDashArray(json, 10.0, out float[] p10);
            Assert.That(p10[0], Is.EqualTo(2f), "zoom=10: [2,1]");

            LineDash.TryEvaluateDashArray(json, 14.0, out float[] p14);
            Assert.That(p14[0], Is.EqualTo(4f), "zoom=14: [4,1]");

            LineDash.TryEvaluateDashArray(json, 15.0, out float[] p15);
            Assert.That(p15[0], Is.EqualTo(4f), "zoom=15: still [4,1]");
        }

        [Test]
        public void TryEvaluateDashArray_Null_ReturnsFalse()
        {
            bool ok = LineDash.TryEvaluateDashArray(null, 10.0, out float[] pattern);
            Assert.That(ok, Is.False);
            Assert.That(pattern, Is.Null);
        }

        // ── Tooth 4 (GREPPABLE): Shader consumes per-vertex sideAndDist.y as dashU ────────────
        //
        // Prevents a "constant dashU" implementation that would produce uniform alpha (not real dashes).
        // The vertex must read input.sideAndDist.y and produce output.dashU;
        // the fragment must consume input.dashU (not a constant).

        [Test]
        public void ShaderStructure_MapLineForwardPass_ConsumesDistanceAlongAsPerVertexAttribute()
        {
            // Locate MapLineForwardPass.hlsl by walking up from known starting points.
            // Unity batch mode: cwd = project root; dotnet test: AppContext.BaseDirectory near repo root.
            string[] starts = new[]
            {
                Directory.GetCurrentDirectory(),  // Unity batch-mode: cwd = project root
                AppContext.BaseDirectory,         // dotnet test output dir
            };

            string hlslPath = null;
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir,
                        "Assets", "MapRenderer.Unity", "Shaders", "MapLineForwardPass.hlsl");
                    if (File.Exists(candidate)) { hlslPath = candidate; break; }
                    dir = Directory.GetParent(dir)?.FullName;
                }
                if (hlslPath != null) break;
            }

            Assert.That(hlslPath, Is.Not.Null,
                $"MapLineForwardPass.hlsl not found. Tried walking up 16 levels from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
            string text = File.ReadAllText(hlslPath);

            // Tooth 4a: vertex reads sideAndDist.y (the per-vertex distanceAlong attribute).
            Assert.That(text, Does.Contain("sideAndDist.y"),
                "Vertex shader must read input.sideAndDist.y (per-vertex distanceAlong) for dashU.");

            // Tooth 4b: vertex assigns to output.dashU (not a constant).
            Assert.That(text, Does.Contain("output.dashU"),
                "Vertex shader must write output.dashU (computed from per-vertex distanceAlong).");

            // Tooth 4c: fragment consumes input.dashU (not a constant).
            Assert.That(text, Does.Contain("input.dashU"),
                "Fragment shader must read input.dashU (the interpolated per-vertex varying).");

            // Tooth 4d: _DashCount identity guard must be present.
            Assert.That(text, Does.Contain("_DashCount"),
                "Shader must have _DashCount guard for solid identity (S14 non-regression).");
        }

        // ── LinePaint.HasDashArray parse integration ──────────────────────────────────────────

        // Helper: build a StyleLayer for line tests (mirrors LinePaintTests.MakeLineLayer pattern).
        private static MapRenderer.Core.Style.StyleLayer MakeDashLayer(string paintJson)
        {
            return new MapRenderer.Core.Style.StyleLayer
            {
                Id          = "test-dash",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Line,
                SourceLayer = "roads",
                Paint       = paintJson != null ? JsonParser.Parse(paintJson) : null,
            };
        }

        [Test]
        public void LinePaint_HasDashArray_FalseWhenAbsent()
        {
            // A layer with no line-dasharray: HasDashArray must be false.
            var layer = MakeDashLayer("{\"line-width\":2}");
            var paint = new LinePaint(layer);
            Assert.That(paint.HasDashArray, Is.False);
            Assert.That(paint.DashArrayJson, Is.Null);
        }

        [Test]
        public void LinePaint_HasDashArray_TrueWhenPresent()
        {
            var layer = MakeDashLayer("{\"line-dasharray\":[2,1]}");
            var paint = new LinePaint(layer);
            Assert.That(paint.HasDashArray, Is.True);
            Assert.That(paint.DashArrayJson, Is.Not.Null);
        }

        [Test]
        public void LinePaint_HasDashArray_IsNotInertFallback()
        {
            // A layer with only line-dasharray: anyPresent=true → IsInertFallback=false.
            var layer = MakeDashLayer("{\"line-dasharray\":[2,1]}");
            var paint = new LinePaint(layer);
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
                bool isOn = LineDash.DashCoverage(dist, widthM, pattern) >= 0.5f;
                if (i > 0 && wasOn != isOn)
                    transitionCount++;
                wasOn = isOn;
            }
            // Each cycle has 2 transitions (on→off, off→on); also counts trailing half-cycle.
            return (transitionCount + 1) / 2;
        }
    }
}
