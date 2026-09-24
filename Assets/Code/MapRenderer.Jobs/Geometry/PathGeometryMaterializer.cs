using System;
using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Jobs.Geometry
{
    /// <summary>
    /// Waist 1's producer for geometry that is already a list of paths (the background quad, a sliced GeoJSON
    /// feature), beside <see cref="MvtGeometryMaterializer"/>. It flattens and copies features and paths in
    /// order, with no transform, filter, rewind or reorder; <c>RingFeatureIdx</c> indexes the supplied list.
    /// Non-local invariant: paths must be tile-local, Y-down, in <c>[−b, extent + b]</c> (<c>b</c> = the
    /// producer's buffer), because ring assembly and earcut thresholds assume tile-integer scale. Each call
    /// returns a fresh buffer that the caller owns.
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

            // Non-local invariant: early-out on the FEATURE count, as MvtGeometryMaterializer does, so a layer
            // with features but no rings still carries its kind column and FeatureCount matches its Features.
            if (featureCount == 0)
                return default;

            // Validated BEFORE Allocate: a throw after it would leak the Persistent arrays, which no caller
            // can dispose.
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
