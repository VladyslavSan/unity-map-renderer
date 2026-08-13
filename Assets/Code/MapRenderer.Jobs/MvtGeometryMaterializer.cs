using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// The MVT implementation of Waist 1's producer seam: flattens one layer's per-feature geometry command
    /// streams into native buffers, runs <see cref="MvtDecodeJob"/> over them, and returns the decoded rings.
    ///
    /// <para>The payload — the command streams plus the tile address and extent they are quantized against —
    /// is captured at construction, because the formats behind <see cref="ITileGeometryMaterializer"/> do not
    /// share an input shape. This object is therefore the sole authority for the buffer's
    /// <c>Tile</c>/<c>Extent</c>; see the interface doc.</para>
    ///
    /// <para><b>IR C1 P3 — the input is raw commands, not features.</b> Until P3 this took an
    /// <c>IReadOnlyList&lt;IFeature&gt;</c> and cast each element to the sidecar interface
    /// <c>IMvtGeometryCarrier</c> to reach the bytes. That sidecar existed only because the geometry did not
    /// belong to anything; now the decoded layer owns its buffer and calls this producer <i>while it still
    /// holds its own command streams</i>, so the bytes are passed directly. The parameter list is deliberately
    /// the same shape as <see cref="PathGeometryMaterializer"/>'s — <c>(tile, extent, kinds, geometry)</c> —
    /// so the two producers of Waist 1 read alike, and it inherits that sibling's explicit length guard in
    /// place of the retired cast.</para>
    ///
    /// <para><b>Ownership transfers on return</b> (interface contract). Nothing here is cached: each call mints
    /// a fresh buffer, so one materializer may legitimately be materialized more than once.</para>
    /// </summary>
    public sealed class MvtGeometryMaterializer : ITileGeometryMaterializer
    {
        private readonly TileId                          _tile;
        private readonly double                          _extent;
        private readonly IReadOnlyList<TileGeometryType> _featureGeometryTypes;
        private readonly IReadOnlyList<uint[]>           _featureCommands;

        /// <param name="tile">The slippy-map address whose tile-local space the streams are expressed in.</param>
        /// <param name="extent">The quantization range of those coordinates (MVT extent, typically 4096).</param>
        /// <param name="featureGeometryTypes">Each feature's declared geometry kind, read from the source's own
        /// declaration and never inferred from the coordinates (interface contract, "Kind, not shape").</param>
        /// <param name="featureCommands">Each feature's MVT geometry command stream, index-aligned with
        /// <paramref name="featureGeometryTypes"/>, in the order <c>RingFeatureIdx</c> joins back through.
        /// A null element is legal — zero commands.</param>
        public MvtGeometryMaterializer(
            TileId tile, double extent,
            IReadOnlyList<TileGeometryType> featureGeometryTypes,
            IReadOnlyList<uint[]> featureCommands)
        {
            _tile                 = tile;
            _extent               = extent;
            _featureGeometryTypes = featureGeometryTypes;
            _featureCommands      = featureCommands;
        }

        public TileGeometryBuffers Materialize()
        {
            int featureCount = _featureCommands == null ? 0 : _featureCommands.Count;

            if (featureCount == 0)
                return default;

            // The kind column is a SECOND list joined to the command list by position. Validated BEFORE
            // anything is allocated, exactly as PathGeometryMaterializer does: a mismatch would mis-classify
            // every ring rather than fail loudly, and a throw after Allocate would strand four
            // Allocator.Persistent arrays no caller can reach.
            if (_featureGeometryTypes == null || _featureGeometryTypes.Count != featureCount)
                throw new ArgumentException(
                    $"featureGeometryTypes must have one entry per feature ({featureCount}); got " +
                    $"{_featureGeometryTypes?.Count ?? -1}. RingFeatureIdx joins rings to this column by " +
                    "position, so a mismatch mis-classifies every ring rather than failing loudly.",
                    nameof(_featureGeometryTypes));

            // The command streams, in feature order. Copied into a List so the exact-sizing pre-pass and the
            // flatten loop below walk the SAME sequence.
            var features = new List<uint[]>(featureCount);
            for (int fi = 0; fi < featureCount; fi++)
                features.Add(_featureCommands[fi]); // null is fine — zero commands

            // ── Pre-pass: flatten polygon commands → NativeArrays. ─────────────────────────────
            int totalCommands = 0;
            for (int fi = 0; fi < featureCount; fi++)
                totalCommands += features[fi]?.Length ?? 0;

            // Exact sizing: walk every command exactly as MvtDecodeJob does to pre-count the rings and
            // vertices it will emit (S06 item a). This makes under-allocation — and thus the in-job OOB write
            // — impossible for ANY input, including a malformed multi-point MoveTo.
            FillMeshPipeline.PrecountRingsAndVertices(features, out int exactRings, out int exactVertices);

            // Note: not using 'using var' because C# 8+ makes 'using var' NativeArrays read-only
            // (CS1654), preventing index assignment. Dispose manually below.
            var commands    = new NativeArray<uint>(totalCommands, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featOffsets = new NativeArray<int>(featureCount,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var featLengths = new NativeArray<int>(featureCount,   Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            int cmdPos = 0;
            for (int fi = 0; fi < featureCount; fi++)
            {
                uint[] geom = features[fi];
                int len = geom?.Length ?? 0;
                featOffsets[fi] = cmdPos;
                featLengths[fi] = len;
                if (geom != null)
                    for (int k = 0; k < len; k++)
                        commands[cmdPos + k] = geom[k];
                cmdPos += len;
            }

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
                Commands            = commands,
                FeatureOffsets      = featOffsets,
                FeatureLengths      = featLengths,
                OutVertices         = geometry.Vertices,
                OutRingOffsets      = geometry.RingOffsets,
                OutRingFeatureIndex = geometry.RingFeatureIdx,
                OutRingCount        = ringCountArr,
                OutVertexCount      = vertCountArr,
            }.Run(); // Run (not Schedule) so the pipeline is callable off the main thread (S89 D2 worker path)

            commands.Dispose();
            featOffsets.Dispose();
            featLengths.Dispose();

            geometry.RingCount   = ringCountArr[0];
            geometry.VertexCount = vertCountArr[0];
            ringCountArr.Dispose();
            vertCountArr.Dispose();

            // Never-fired backstop: with exact PrecountRingsAndVertices sizing the decode job's reported
            // ring/vertex counts equal the buffer capacities, so these cannot trip. Kept as defense-in-depth
            // against a future sizing-vs-decode desync. (S06 gated item a; see EnsureCapacity doc.) Compared
            // against the local capacities, never against the buffer lengths — see TileGeometryBuffers.RingCount.
            //
            // The buffer is already minted when these run, so the throw would strand it. The path is
            // unreachable by construction, hence no behavioural test can force it — the catch is there so the
            // "owner on every exit path" contract holds by reading the code, not by arguing reachability.
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
