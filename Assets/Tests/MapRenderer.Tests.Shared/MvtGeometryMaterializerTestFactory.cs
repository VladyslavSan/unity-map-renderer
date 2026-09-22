// Unity EditMode only — NativeArray-backed. NOT registered in core-tests.csproj.

using System;
using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
namespace MapRenderer.Tests
{
    /// <summary>
    /// Test-only adapter between the per-feature <c>uint[]</c> command streams fixtures still author
    /// (<c>MvtCommandStream</c>, <c>MvtFixtureStreams</c>, hand-written literals) and the native-flat
    /// <c>(commands, featureOffsets, featureLengths)</c> shape <see cref="MvtGeometryMaterializer"/>'s
    /// constructor takes. Production (<c>MvtDecoder</c>) builds that shape directly off the wire and has no
    /// need of this; test fixtures find the per-feature-array shape the readable one to author, so this is
    /// the one place that converts between the two.
    /// </summary>
    internal static class MvtGeometryMaterializerTestFactory
    {
        /// <summary>The flattened buffers <see cref="Flatten"/> mints — <c>Allocator.Persistent</c>,
        /// BORROWED by any <see cref="MvtGeometryMaterializer"/> built from them (mirrors the production
        /// ownership contract: the materializer never disposes its inputs). Dispose exactly once, after every
        /// <c>Materialize()</c> call that needed it has run.</summary>
        internal readonly struct FlatGeometry : IDisposable
        {
            public readonly NativeArray<uint> Commands;
            public readonly NativeArray<int> FeatureOffsets;
            public readonly NativeArray<int> FeatureLengths;

            public FlatGeometry(NativeArray<uint> commands, NativeArray<int> featureOffsets, NativeArray<int> featureLengths)
            {
                Commands = commands;
                FeatureOffsets = featureOffsets;
                FeatureLengths = featureLengths;
            }

            public void Dispose()
            {
                Commands.Dispose();
                FeatureOffsets.Dispose();
                FeatureLengths.Dispose();
            }
        }

        /// <summary>Flattens per-feature command arrays into one shared native buffer, the same shape
        /// <c>MvtDecoder.DecodeLayer</c> builds directly off the wire. A null element is legal — zero
        /// commands, matching <see cref="MvtGeometryMaterializer"/>'s documented "null stream" contract.</summary>
        internal static FlatGeometry Flatten(IReadOnlyList<uint[]> featureCommands)
        {
            int featureCount = featureCommands?.Count ?? 0;

            int total = 0;
            for (int fi = 0; fi < featureCount; fi++)
                total += featureCommands[fi]?.Length ?? 0;

            var commands = new NativeArray<uint>(total, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var offsets  = new NativeArray<int>(featureCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var lengths  = new NativeArray<int>(featureCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            int pos = 0;
            for (int fi = 0; fi < featureCount; fi++)
            {
                uint[] geom = featureCommands[fi];
                int len = geom?.Length ?? 0;
                offsets[fi] = pos;
                lengths[fi] = len;
                if (geom != null)
                    for (int k = 0; k < len; k++)
                        commands[pos + k] = geom[k];
                pos += len;
            }

            return new FlatGeometry(commands, offsets, lengths);
        }

        /// <summary>Low-level: flatten and construct, handing the flattened buffers back so the caller can
        /// hold the materializer across more than one <see cref="MvtGeometryMaterializer.Materialize"/> call
        /// (e.g. an ownership-transfer test) before disposing <paramref name="flat"/> itself.</summary>
        internal static MvtGeometryMaterializer Create(
            TileId tile, double extent, IReadOnlyList<TileGeometryType> kinds, IReadOnlyList<uint[]> featureCommands,
            out FlatGeometry flat)
        {
            flat = Flatten(featureCommands);
            return new MvtGeometryMaterializer(tile, extent, kinds, flat.Commands, flat.FeatureOffsets, flat.FeatureLengths);
        }

        /// <summary>One-shot convenience: flatten, construct, materialize once, dispose the flattened
        /// buffers — the shape almost every call site wants.</summary>
        internal static TileGeometryBuffers Materialize(
            TileId tile, double extent, IReadOnlyList<TileGeometryType> kinds, IReadOnlyList<uint[]> featureCommands)
        {
            using FlatGeometry flat = Flatten(featureCommands);
            return new MvtGeometryMaterializer(tile, extent, kinds, flat.Commands, flat.FeatureOffsets, flat.FeatureLengths)
                .Materialize();
        }
    }
}
