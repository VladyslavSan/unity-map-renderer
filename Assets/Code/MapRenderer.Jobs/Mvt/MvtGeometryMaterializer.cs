using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The MVT implementation of Waist 1's producer seam: runs <see cref="MvtDecodeJob"/> over one layer's
    /// flat native command buffer, captured at construction with the tile address and extent it is sole
    /// authority for. Non-local invariant: the three command buffers are borrowed and the caller disposes
    /// them; only the output buffer transfers on return, and each call mints a fresh one, so one instance
    /// may be materialized more than once.
    /// </summary>
    public sealed class MvtGeometryMaterializer : ITileGeometryMaterializer
    {
        private readonly TileId                          _tile;
        private readonly double                          _extent;
        private readonly IReadOnlyList<TileGeometryType> _featureGeometryTypes;
        private readonly NativeArray<uint>               _commands;
        private readonly NativeArray<int>                _featureOffsets;
        private readonly NativeArray<int>                _featureLengths;

        /// <param name="tile">The slippy-map address whose tile-local space the streams are expressed in.</param>
        /// <param name="extent">The quantization range of those coordinates (MVT extent, typically 4096).</param>
        /// <param name="featureGeometryTypes">Each feature's declared geometry kind, read from the source's own
        /// declaration and never inferred from the coordinates (interface contract, "Kind, not shape").</param>
        /// <param name="commands">Every feature's MVT command words, in feature order. Borrowed: read only inside
        /// <see cref="Materialize"/>; the caller disposes it.</param>
        /// <param name="featureOffsets">Per-feature start offset into <paramref name="commands"/>, aligned with
        /// <paramref name="featureGeometryTypes"/>; its length is the feature count. Borrowed.</param>
        /// <param name="featureLengths">Per-feature command-word count; zero is legal and emits no rings.
        /// Borrowed.</param>
        public MvtGeometryMaterializer(
            TileId tile, double extent,
            IReadOnlyList<TileGeometryType> featureGeometryTypes,
            NativeArray<uint> commands, NativeArray<int> featureOffsets, NativeArray<int> featureLengths)
        {
            _tile                 = tile;
            _extent               = extent;
            _featureGeometryTypes = featureGeometryTypes;
            _commands             = commands;
            _featureOffsets       = featureOffsets;
            _featureLengths       = featureLengths;
        }

        public TileGeometryBuffers Materialize()
        {
            int featureCount = _featureOffsets.IsCreated ? _featureOffsets.Length : 0;

            if (featureCount == 0)
                return default;

            // Validate the kind column before any allocation: a count mismatch mis-classifies every ring, and a
            // throw after Allocate strands the output buffer.
            if (_featureGeometryTypes == null || _featureGeometryTypes.Count != featureCount)
                throw new ArgumentException(
                    $"featureGeometryTypes must have one entry per feature ({featureCount}); got " +
                    $"{_featureGeometryTypes?.Count ?? -1}. RingFeatureIdx joins rings to this column by " +
                    "position, so a mismatch mis-classifies every ring rather than failing loudly.",
                    nameof(_featureGeometryTypes));

            // Pre-count rings and vertices with the same command walk as MvtDecodeJob, so no input, even a
            // malformed multi-point MoveTo, causes an out-of-range write in the job.
            FillMeshPipeline.PrecountRingsAndVertices(
                _commands, _featureOffsets, _featureLengths, out int exactRings, out int exactVertices);

            // ── Allocate decode output buffers. ───────────────────────────────────────────────────
            var geometry = TileGeometryBuffers.Allocate(_tile, _extent, featureCount, exactRings, exactVertices);
            var ringCountArr = new NativeArray<int>(1,              Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var vertCountArr = new NativeArray<int>(1,              Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Kind comes from the source's own declaration, never inferred from the coordinates (interface
            // contract, "Kind, not shape").
            for (int fi = 0; fi < featureCount; fi++)
                geometry.FeatureGeometryType[fi] = _featureGeometryTypes[fi];

            // ── Decode. ────────────────────────────────────────────────────────────────────────────
            new MvtDecodeJob
            {
                Commands            = _commands,
                FeatureOffsets      = _featureOffsets,
                FeatureLengths      = _featureLengths,
                OutVertices         = geometry.Vertices,
                OutRingOffsets      = geometry.RingOffsets,
                OutRingFeatureIndex = geometry.RingFeatureIdx,
                OutRingCount        = ringCountArr,
                OutVertexCount      = vertCountArr,
            }.Run(); // Run, not Schedule, so the pipeline is callable off the main thread

            geometry.RingCount   = ringCountArr[0];
            geometry.VertexCount = vertCountArr[0];
            ringCountArr.Dispose();
            vertCountArr.Dispose();

            // Backstop against a sizing-vs-decode desync; compare with the local capacities, not the buffer
            // lengths (see TileGeometryBuffers.RingCount). Limitation: no test reaches this path; the catch
            // frees the allocated buffer, so the owner-on-every-exit-path contract holds by reading the code.
            try
            {
                FillMeshPipeline.EnsureCapacity(geometry.RingCount, exactRings, "ring");
                FillMeshPipeline.EnsureCapacity(geometry.VertexCount, exactVertices, "decoded vertex");
            }
            catch { geometry.Dispose(); throw; }

            return geometry;
        }
    }
}
