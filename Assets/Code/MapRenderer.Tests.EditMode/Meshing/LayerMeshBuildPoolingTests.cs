// Unity EditMode only (Unity.Collections types). NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Fill;
using MapRenderer.Unity.Rendering.Meshing;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// T3 (the per-layer build-object stage): a pooled <see cref="ILayerMeshBuild"/> instance is never
    /// handed to two renters at once — the hazard <see cref="LayerMeshBuildPool{T}"/> introduces that the
    /// retired struct <c>LayerRequest</c> did not have (§2.2 of the stage's own plan): a class can be
    /// returned to its pool twice (once per redundant <c>Dispose()</c> call), landing the same reference in
    /// the <c>ConcurrentBag</c> twice, so two independent <c>Rent</c> calls then observe the identical
    /// instance. <see cref="FillLayerBuild.Dispose"/>'s own <c>if (_disposed) return;</c> guard is what
    /// prevents it.
    /// </summary>
    [TestFixture]
    public class LayerMeshBuildPoolingTests
    {
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>A minimal, never-scheduled build — <c>RingVisitOrder</c>/<c>FeatureColors</c> are
        /// CREATED (so <c>Dispose()</c> has real columns to free) but zero-length and <c>Geometry</c> stays
        /// uncreated, since a never-scheduled build's <c>Dispose()</c> never reads it (only
        /// <c>TryScheduleWrite</c> does).</summary>
        private static FillLayerBuild RentMinimal(int materialIndex)
        {
            var input = new FillMeshPipeline.LayerInput
            {
                RingVisitOrder = new NativeArray<int>(0, Allocator.Persistent),
                OriginRender   = double3.zero,
                Projection     = new WebMercatorProjection(),
            };
            var featureColors = new NativeArray<Vector4>(0, Allocator.Persistent);
            return FillLayerBuild.Rent(input, featureColors, materialIndex, "probe");
        }

        /// <summary><b>RED:</b> remove the <c>if (_disposed) return;</c> guard from
        /// <see cref="FillLayerBuild.Dispose"/> — the double <c>Dispose()</c> below puts the same instance in
        /// the pool's bag twice, and two of the <c>N</c> rents that follow then return the same reference,
        /// failing the pairwise-distinct assertion. Executed and reverted.</summary>
        [Test]
        public void Dispose_CalledTwice_NeverLandsTheSameInstanceInThePoolTwice()
        {
            FillLayerBuild build = RentMinimal(materialIndex: 0);
            build.Dispose();
            build.Dispose(); // the redundant sweep TileBuildGraph.Dispose()'s own idempotency mirrors

            // Always bound loops: N is a small, fixed constant, not runtime-derived.
            const int N = 32;
            var rented = new List<ILayerMeshBuild>(N);
            try
            {
                for (int i = 0; i < N; i++)
                    rented.Add(RentMinimal(materialIndex: i));

                for (int i = 0; i < N; i++)
                    for (int j = i + 1; j < N; j++)
                        Assert.AreNotSame(rented[i], rented[j],
                            $"Rent() calls {i} and {j} returned the SAME instance — a double-Dispose() landed " +
                            "it in the pool's bag twice, so two independent renters now observe one build.");
            }
            finally
            {
                foreach (ILayerMeshBuild b in rented) b.Dispose();
            }
        }
    }
}
