using System;
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Jobs.Geometry
{
    /// <summary>
    /// Waist 1's producer seam for geometry that is already a list of paths rather than a command stream —
    /// the second production producer, alongside <see cref="MvtGeometryMaterializer"/>. A source that emits
    /// coordinates natively (the background quad's four corners; a sliced GeoJSON feature) reaches the
    /// pipeline through here instead of transcoding into MVT commands.
    ///
    /// <para>It is a <b>flatten-and-copy</b>, nothing more: features are walked in order and each feature's
    /// paths in order, so a consumer joins back through <c>RingFeatureIdx</c>, which indexes the supplied
    /// feature list. <b>No transformation, no filtering, no rewind and no reordering</b> — rings shorter than
    /// 3 points are carried through, because filtering is the consuming stage's job (interface contract, "No
    /// ring filtering").</para>
    ///
    /// <para><b>THE NAMED FENCE.</b> The supplied paths must already be <b>tile-local <c>double2</c> in
    /// <c>[−b, extent + b]</c>, Y-down</b> — never geodetic, never projected. Ring assembly's and earcut's
    /// thresholds are calibrated to tile-integer magnitude; degrees are a different scale entirely.
    /// <c>b</c> is the producer's buffer: a tile's own square is <c>[0, extent]</c>, but every buffered
    /// producer overruns it deliberately, MVT wire geometry included, so a fence written as
    /// <c>[0, extent]</c> would declare conforming input out of contract.</para>
    ///
    /// <para><b>Ownership transfers on return</b> (interface contract). Nothing is cached: each call mints a
    /// fresh buffer.</para>
    /// </summary>
    public sealed class PathGeometryMaterializer : ITileGeometryMaterializer
    {
        private readonly TileId                                             _tile;
        private readonly double                                            _extent;
        private readonly IReadOnlyList<TileGeometryType>                   _featureGeometryTypes;
        private readonly IReadOnlyList<IReadOnlyList<IReadOnlyList<double2>>> _featurePaths;

        /// <param name="tile">The slippy-map address whose tile-local space the paths are expressed in.</param>
        /// <param name="extent">The range those coordinates span (typically 4096).</param>
        /// <param name="featureGeometryTypes">Each feature's declared geometry kind, read from the source's own
        /// declaration and never inferred from the coordinates (interface contract, "Kind, not shape").</param>
        /// <param name="featurePaths">Each feature's paths, index-aligned with
        /// <paramref name="featureGeometryTypes"/>.</param>
        public PathGeometryMaterializer(
            TileId tile, double extent,
            IReadOnlyList<TileGeometryType> featureGeometryTypes,
            IReadOnlyList<IReadOnlyList<IReadOnlyList<double2>>> featurePaths)
        {
            _tile                 = tile;
            _extent               = extent;
            _featureGeometryTypes = featureGeometryTypes;
            _featurePaths         = featurePaths;
        }

        public TileGeometryBuffers Materialize()
        {
            IReadOnlyList<IReadOnlyList<IReadOnlyList<double2>>> features = _featurePaths;
            int featureCount = features == null ? 0 : features.Count;

            int ringTotal = 0;
            int vertTotal = 0;
            for (int f = 0; f < featureCount; f++)
            {
                IReadOnlyList<IReadOnlyList<double2>> paths = features[f];
                if (paths == null) continue;
                ringTotal += paths.Count;
                for (int p = 0; p < paths.Count; p++)
                    vertTotal += paths[p].Count;
            }

            // IR C1 fix stage: the early-out is on the FEATURE count, matching
            // <see cref="MvtGeometryMaterializer"/>'s. It used to be `ringTotal == 0`, which is reachable with
            // features PRESENT (every feature carrying no paths — a GeoJSON feature sliced away at this tile,
            // say) and returned `default`: a buffer whose FeatureCount is 0 beside a non-empty
            // ITileLayer.Features. Consumers size their per-feature columns from one and index them by
            // ordinals drawn from the other (StyledLineTileBuilder, SymbolFeatureExtractor), so the two
            // producers of Waist 1 disagreeing on that count is a mis-bucketing — and, once the highest
            // ordinal exceeds the column length, an index-out-of-range — waiting for the first non-MVT
            // ITileLayer. A features-but-no-rings layer now mints a ring-less buffer that still carries the
            // kind column, which is exactly what the MVT sibling produces for the same input.
            if (featureCount == 0)
                return default;

            // The kind column is a SECOND list joined to `features` by position, so a length mismatch would
            // fault the write loop below. Validated BEFORE Allocate, deliberately: after it, four
            // Allocator.Persistent arrays exist and a throw here would strand them with no caller able to
            // dispose them — the one exit path the "caller disposes on every exit path" contract cannot
            // cover. Checked by reading the code rather than by arguing reachability, the same standard
            // MvtGeometryMaterializer's throw-path catch is held to.
            if (_featureGeometryTypes == null || _featureGeometryTypes.Count != featureCount)
                throw new ArgumentException(
                    $"featureGeometryTypes must have one entry per feature ({featureCount}); got " +
                    $"{_featureGeometryTypes?.Count ?? -1}. RingFeatureIdx joins rings to this column by " +
                    "position, so a mismatch mis-classifies every ring rather than failing loudly.",
                    nameof(_featureGeometryTypes));

            var geometry = TileGeometryBuffers.Allocate(_tile, _extent, featureCount, ringTotal, vertTotal);

            int ring   = 0;
            int vertex = 0;
            for (int f = 0; f < featureCount; f++)
            {
                geometry.FeatureGeometryType[f] = _featureGeometryTypes[f];

                IReadOnlyList<IReadOnlyList<double2>> paths = features[f];
                if (paths == null) continue;
                for (int p = 0; p < paths.Count; p++)
                {
                    IReadOnlyList<double2> path = paths[p];
                    geometry.RingOffsets[ring]    = vertex;
                    geometry.RingFeatureIdx[ring] = f;
                    ring++;

                    for (int i = 0; i < path.Count; i++)
                        geometry.Vertices[vertex++] = path[i];
                }
            }

            geometry.RingOffsets[ringTotal] = vertex;   // trailing sentinel
            geometry.RingCount   = ringTotal;
            geometry.VertexCount = vertex;
            return geometry;
        }
    }
}
