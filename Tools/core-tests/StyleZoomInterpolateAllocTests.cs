// Engine-free (pure Core/Unity.Style types + NUnit) — a core-tests-ONLY file (lives in Tools/core-tests/, NOT
// the Assets EditMode tree, so Unity never compiles it). It stays out of EditMode on purpose:
// GC.GetAllocatedBytesForCurrentThread reads a constant 0 in the Unity Mono EditMode runner
// (docs/gc-and-allocation-design.md § 6), so this byte-delta tooth can only discriminate in the CoreCLR
// dotnet runner.

using System;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Style
{
    /// <summary>StyleProperty&lt;T&gt;'s zoom-interpolate evaluation path, swept over many zoom values, must
    /// not allocate GC heap memory per call.</summary>
    [TestFixture]
    public class StyleZoomInterpolateAllocTests
    {
        private static StyleProperty<float> NumProp(string json)
            => new StyleProperty<float>(JsonParser.Parse(json), 0f, v => (float)v.AsNumber());

        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        [Test]
        public void ZoomInterpolateNumber_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom number interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateColor_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom color interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateNumber_WrappedWithNumberAssertion_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"number\",[\"zoom\"]],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "AssertExpression wrapping zoom must not allocate.");
        }
    }
}
