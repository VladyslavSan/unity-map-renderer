using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// The MVT implementation of Waist 1's producer seam: runs <see cref="MvtDecodeJob"/> over one layer's
    /// already-flat geometry command buffer and returns the decoded rings.
    ///
    /// <para>The payload — the command buffer plus the tile address and extent they are quantized against —
    /// is captured at construction, because the formats behind <see cref="ITileGeometryMaterializer"/> do not
    /// share an input shape. This object is therefore the sole authority for the buffer's
    /// <c>Tile</c>/<c>Extent</c>; see the interface doc.</para>
    ///
    /// <para><b>2a — the input is a native-flat buffer, not per-feature arrays.</b> Until 2a this took an
    /// <c>IReadOnlyList&lt;uint[]&gt;</c> — one managed command array per feature — and copied/flattened it
    /// into a native buffer here on every call. <c>MvtDecoder</c> now flattens directly off the wire into the
    /// exact same native shape <see cref="MvtDecodeJob"/> consumes, so this constructor takes it as-is: no
    /// managed per-feature array and no copy exist anywhere in the decode path. A test that still authors
    /// fixtures as <c>uint[]</c> per feature flattens them via
    /// <c>MvtGeometryMaterializerTestFactory</c> (test assembly) before constructing this type.</para>
    ///
    /// <para><b>The three command buffers are BORROWED, never disposed here.</b> The caller (production:
    /// <c>MvtDecoder.DecodeLayer</c>; tests: whatever flattened them) owns them and is responsible for
    /// disposal — this mirrors <see cref="MvtDecodeJob"/>'s own <c>[ReadOnly]</c> attribute on the same
    /// arrays. This is why <b>ownership transfers on return</b> still holds for the OUTPUT buffer only
    /// (interface contract): nothing here is cached, each call mints a fresh output buffer by re-running the
    /// job over the same borrowed input, so one materializer may legitimately be materialized more than once
    /// (<c>Materialize_TransfersOwnership_AndMintsAFreshBufferPerCall</c> does exactly that).</para>
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
        /// <param name="commands">Every feature's MVT geometry command words, concatenated in feature order —
        /// the same flat buffer <see cref="MvtDecodeJob"/> reads. BORROWED: the caller disposes it, before or
        /// after this instance is materialized (never touched outside a <see cref="Materialize"/> call).</param>
        /// <param name="featureOffsets">Per-feature start offset into <paramref name="commands"/>, index-aligned
        /// with <paramref name="featureGeometryTypes"/>. Its length is this materializer's feature count.
        /// BORROWED, same lifetime contract as <paramref name="commands"/>.</param>
        /// <param name="featureLengths">Per-feature command-word count. A zero length is legal — zero
        /// commands, the flat-buffer equivalent of the old "null <c>uint[]</c> element". BORROWED, same
        /// lifetime contract as <paramref name="commands"/>.</param>
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

            // The kind column is a SECOND list joined to the offsets/lengths columns by position. Validated
            // BEFORE anything is allocated, exactly as PathGeometryMaterializer does: a mismatch would
            // mis-classify every ring rather than fail loudly, and a throw after Allocate would strand the
            // output buffer no caller can reach (the INPUT buffers are borrowed, so they are never at risk
            // here — the caller's own finally frees them regardless of how this call exits).
            if (_featureGeometryTypes == null || _featureGeometryTypes.Count != featureCount)
                throw new ArgumentException(
                    $"featureGeometryTypes must have one entry per feature ({featureCount}); got " +
                    $"{_featureGeometryTypes?.Count ?? -1}. RingFeatureIdx joins rings to this column by " +
                    "position, so a mismatch mis-classifies every ring rather than failing loudly.",
                    nameof(_featureGeometryTypes));

            // Exact sizing: walk every command exactly as MvtDecodeJob does to pre-count the rings and
            // vertices it will emit (S06 item a). This makes under-allocation — and thus the in-job OOB write
            // — impossible for ANY input, including a malformed multi-point MoveTo.
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
            }.Run(); // Run (not Schedule) so the pipeline is callable off the main thread (S89 D2 worker path)

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
