// Unity EditMode only — tests that require NativeArray (engine API) or the NUnit GC-alloc recorder
// (UnityEngine.TestTools.Constraints). NOT included in Tools/core-tests.
//
// S61 acceptance:
//   T4-alloc: Forward(span,out) allocates 0 bytes when caller owns the buffers.
//   T5-NativeArray: GeoCoordinate3D is usable as NativeArray<T> element type (blittable).
//   T4-NativeArray: NativeArray<GeoCoordinate3D>.AsReadOnlySpan() bridges correctly to Forward.

using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Projection
{
    [TestFixture]
    public class ProjectionMathUnityTests
    {
        // ── T5 — GeoCoordinate3D usable as NativeArray element type ──────────────────────────────

        /// <summary>
        /// T5: GeoCoordinate3D is blittable and works as NativeArray&lt;GeoCoordinate3D&gt; element.
        /// Verifies T5's "usable as NativeArray element type" requirement.
        /// </summary>
        [Test]
        public void GeoCoordinate3D_UsableAsNativeArrayElement()
        {
            // If GeoCoordinate3D is not blittable, this line throws InvalidOperationException.
            var arr = new NativeArray<GeoCoordinate3D>(3, Allocator.Temp);
            try
            {
                arr[0] = new GeoCoordinate3D { Longitude = 0.0,  Latitude = 0.0,  Altitude = 0.0 };
                arr[1] = new GeoCoordinate3D { Longitude = 13.4, Latitude = 52.5, Altitude = 100.0 };
                arr[2] = new GeoCoordinate3D { Longitude = -90.0, Latitude = 45.0, Altitude = 0.0 };

                Assert.AreEqual(0.0,   arr[0].Longitude,      1e-9, "arr[0].Longitude");
                Assert.AreEqual(13.4,  arr[1].Longitude,      1e-9, "arr[1].Longitude");
                Assert.AreEqual(52.5,  arr[1].Latitude,      1e-9, "arr[1].Latitude");
                Assert.AreEqual(100.0, arr[1].Altitude, 1e-9, "arr[1].Altitude");
            }
            finally
            {
                arr.Dispose();
            }
        }

        /// <summary>
        /// T5: GeoCoordinate usable as NativeArray element type (surface struct).
        /// </summary>
        [Test]
        public void GeoCoordinate_UsableAsNativeArrayElement()
        {
            var arr = new NativeArray<GeoCoordinate>(2, Allocator.Temp);
            try
            {
                arr[0] = new GeoCoordinate { Longitude = 13.4,  Latitude = 52.5 };
                arr[1] = new GeoCoordinate { Longitude = -90.0, Latitude = 45.0 };

                Assert.AreEqual(13.4, arr[0].Longitude, 1e-9);
                Assert.AreEqual(52.5, arr[0].Latitude, 1e-9);
            }
            finally
            {
                arr.Dispose();
            }
        }

        // ── T4-alloc — batch Forward allocates 0 bytes (NUnit GC recorder) ──────────────────────

        /// <summary>
        /// T4 (alloc): WebMercator.Forward(ReadOnlySpan, Span) with caller-owned NativeArrays
        /// allocates ZERO bytes on the GC heap. Uses NUnit GC-alloc recorder (NOT raw System.GC
        /// counters — per the allocation-measurement lesson).
        /// </summary>
        [Test]
        public void WebMercator_BatchForward_AllocatesZero()
        {
            var src = new NativeArray<GeoCoordinate3D>(5, Allocator.Persistent);
            var dst = new NativeArray<double3>(5, Allocator.Persistent);
            try
            {
                src[0] = new GeoCoordinate3D { Longitude = 0.0,   Latitude = 0.0,   Altitude = 0.0 };
                src[1] = new GeoCoordinate3D { Longitude = 13.4,  Latitude = 52.5,  Altitude = 0.0 };
                src[2] = new GeoCoordinate3D { Longitude = 45.0,  Latitude = 45.0,  Altitude = 0.0 };
                src[3] = new GeoCoordinate3D { Longitude = -90.0, Latitude = -30.0, Altitude = 0.0 };
                src[4] = new GeoCoordinate3D { Longitude = 0.0,   Latitude = 85.0,  Altitude = 0.0 };

                // The GC-alloc recorder constraint asserts 0 bytes allocated during the delegate.
                Assert.That(() =>
                {
                    WebMercator.Forward(src.AsReadOnlySpan(), dst.AsSpan());
                }, Is.Not.AllocatingGCMemory());
            }
            finally
            {
                src.Dispose();
                dst.Dispose();
            }
        }

        /// <summary>
        /// T4 (alloc): Ecef.Forward(ReadOnlySpan, Span) with caller-owned NativeArrays
        /// allocates ZERO bytes on the GC heap.
        /// </summary>
        [Test]
        public void Ecef_BatchForward_AllocatesZero()
        {
            var src = new NativeArray<GeoCoordinate3D>(5, Allocator.Persistent);
            var dst = new NativeArray<double3>(5, Allocator.Persistent);
            try
            {
                src[0] = new GeoCoordinate3D { Longitude = 0.0,   Latitude = 0.0,   Altitude = 0.0 };
                src[1] = new GeoCoordinate3D { Longitude = 90.0,  Latitude = 45.0,  Altitude = 0.0 };
                src[2] = new GeoCoordinate3D { Longitude = 0.0,   Latitude = 90.0,  Altitude = 0.0 }; // pole
                src[3] = new GeoCoordinate3D { Longitude = 180.0, Latitude = 0.0,   Altitude = 0.0 }; // antipodal
                src[4] = new GeoCoordinate3D { Longitude = -45.0, Latitude = -60.0, Altitude = 500.0 };

                Assert.That(() =>
                {
                    Ecef.Forward(src.AsReadOnlySpan(), dst.AsSpan());
                }, Is.Not.AllocatingGCMemory());
            }
            finally
            {
                src.Dispose();
                dst.Dispose();
            }
        }

        // ── T4-NativeArray: AsReadOnlySpan bridge correctness ────────────────────────────────────

        /// <summary>
        /// T4 (bridge): NativeArray&lt;GeoCoordinate3D&gt;.AsReadOnlySpan() + Forward(span,dst)
        /// produces the same values as scalar Forward for each element.
        /// </summary>
        [Test]
        public void WebMercator_NativeArrayBatch_MatchesScalar()
        {
            var src = new NativeArray<GeoCoordinate3D>(4, Allocator.Temp);
            var dst = new NativeArray<double3>(4, Allocator.Temp);
            try
            {
                src[0] = new GeoCoordinate3D { Longitude = 0.0,    Latitude = 0.0,  Altitude = 0.0 };
                src[1] = new GeoCoordinate3D { Longitude = 13.4,   Latitude = 52.5, Altitude = 0.0 };
                src[2] = new GeoCoordinate3D { Longitude = -180.0, Latitude = 0.0,  Altitude = 0.0 };
                src[3] = new GeoCoordinate3D { Longitude = 0.0,    Latitude = 85.0, Altitude = 0.0 };

                WebMercator.Forward(src.AsReadOnlySpan(), dst.AsSpan());

                for (int i = 0; i < src.Length; i++)
                {
                    double3 s = WebMercator.Forward(src[i]);
                    Assert.AreEqual(s.x, dst[i].x, 0.0, $"[{i}].x");
                    Assert.AreEqual(s.y, dst[i].y, 0.0, $"[{i}].y");
                    Assert.AreEqual(s.z, dst[i].z, 0.0, $"[{i}].z");
                }
            }
            finally
            {
                src.Dispose();
                dst.Dispose();
            }
        }
    }
}
