using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using Unity.Mathematics;

namespace MapRenderer.Core.GeoJson
{
    /// <summary>
    /// How a dataset is cut into tiles; a <c>readonly struct</c>, so <c>in</c> costs no defensive copy. Every
    /// member is a source-level parameter, so <see cref="Extent"/> is chosen rather than inherited from MVT.
    /// Build from <see cref="Default"/>: a <c>default(GeoJsonSliceOptions)</c> has <c>Extent = 0</c> and is
    /// rejected.
    /// </summary>
    public readonly struct GeoJsonSliceOptions
    {
        /// <summary>4096 — the MVT authoring convention, adopted here because it sets the positional
        /// resolution of every declarative fixture (~0.6 m at z14).</summary>
        public const double DefaultExtent = 4096.0;

        /// <summary>64 tile units at <see cref="TileBufferClip.ReferenceExtent"/> — the OpenMapTiles standard
        /// buffer this repo already documents at <c>Tiles/TileBufferClip.cs</c>. Chosen so the slicer's
        /// window and the fill pipeline's clip window are IDENTICAL at the standard setting, which makes the
        /// pipeline clip a provable no-op on GeoJSON tiles rather than merely a harmless one.</summary>
        public const double DefaultBufferAtReferenceExtent = 64.0;

        /// <summary>Tile-local coordinate range: <c>[0, Extent]</c> is the tile proper.</summary>
        public double Extent { get; init; }

        /// <summary>
        /// Buffer margin on every side, in tile units at <see cref="TileBufferClip.ReferenceExtent"/>,
        /// rescaled to <see cref="Extent"/> as <see cref="TileBufferClip.TryWindow"/> does
        /// (<c>b = keep · extent / 4096</c>). 0 cuts at the tile boundary; negative and NaN degrade to 0.
        /// Non-local invariant: the default <c>MapViewConfig.FillTileBufferClip</c> (0.0) still clips every
        /// fill layer to <c>[0, extent]</c>, so a buffered GeoJSON fill is cut back like an MVT fill. For both
        /// formats, a line or point inside the margin is emitted by both neighbouring tiles.
        /// </summary>
        public double BufferAtReferenceExtent { get; init; }

        /// <summary>
        /// Douglas–Peucker-style tolerance, in tile units. Only 0 is supported: <see cref="Validate"/>
        /// rejects any other value, and <see cref="GeoJsonTileSlicer.Slice"/> throws
        /// <see cref="NotSupportedException"/>. It throws rather than being ignored, so an unimplemented
        /// setting is observable; a fixture wants the exact authored position.
        /// </summary>
        public double SimplifyTolerance { get; init; }

        public static GeoJsonSliceOptions Default => new GeoJsonSliceOptions
        {
            Extent                  = DefaultExtent,
            BufferAtReferenceExtent = DefaultBufferAtReferenceExtent,
            SimplifyTolerance       = 0.0
        };

        /// <summary>
        /// Throws unless <see cref="Extent"/> is a whole number in <c>(0, uint.MaxValue]</c> and
        /// <see cref="SimplifyTolerance"/> is 0; <see cref="Window"/> handles every buffer value.
        /// Non-obvious why: a decoded layer carries the extent as a <c>uint</c>
        /// (<c>GeoJsonTileLayer.Extent</c>) and this is a <c>double</c>, so a fractional or larger extent
        /// makes the two disagree. The upper bound also rejects +∞, whose infinite window slices away every
        /// vertex; NaN fails every comparison. <c>Slice</c> and every source that keeps options call this;
        /// <c>Slice</c> checks the tolerance first, so its caller sees <see cref="NotSupportedException"/>.
        /// </summary>
        public void Validate()
        {
            if (!(Extent > 0.0 && Extent <= uint.MaxValue && Extent == math.floor(Extent)))
                throw new ArgumentOutOfRangeException(
                    "options", Extent,
                    "GeoJsonSliceOptions.Extent must be a whole number of tile units in (0, 4294967295] — " +
                    "the domain a decoded layer's uint extent can represent (build from " +
                    "GeoJsonSliceOptions.Default).");

            if (SimplifyTolerance != 0.0)
                throw new ArgumentOutOfRangeException(
                    "options", SimplifyTolerance,
                    "GeoJsonSliceOptions.SimplifyTolerance is not implemented; v1 slices at the authored " +
                    "resolution, so 0 is the only value in its domain (build from " +
                    "GeoJsonSliceOptions.Default).");
        }

        /// <summary>
        /// The inclusive clip window in tile units, <c>[−b, Extent + b]²</c>, computed with the same
        /// arithmetic in the same order as <see cref="TileBufferClip.TryWindow"/>, so the two are bit-equal.
        /// It carries the same input guards: a negative margin would erode into the tile, and a NaN one would
        /// make an all-NaN window that drops every vertex, so both degrade to the tile boundary.
        /// </summary>
        public void Window(out double2 min, out double2 max)
        {
            double buffer = double.IsNaN(BufferAtReferenceExtent)
                ? 0.0
                : math.max(0.0, BufferAtReferenceExtent);

            double keep = buffer * Extent / TileBufferClip.ReferenceExtent;
            min = new double2(-keep, -keep);
            max = new double2(Extent + keep, Extent + keep);
        }

        /// <summary>
        /// The <see cref="Window"/> in unit-square terms for a tile: its corners grown by the buffer margin.
        /// <see cref="GeoJsonTileSlicer.Slice"/>'s per-feature bbox reject and the source's O(1) emptiness probe
        /// share it, so the probe cannot reject a tile the slicer would keep.
        /// </summary>
        public void UnitSquareWindow(TileId tile, out double2 min, out double2 max)
        {
            Window(out double2 windowMin, out double2 _);

            double n      = math.pow(2.0, tile.Z);
            double margin = -windowMin.x / (Extent * n);

            double2 tileMin = WebMercatorTiling.UnitSquareTileMin(tile);
            double2 tileMax = WebMercatorTiling.UnitSquareTileMax(tile);
            min = new double2(tileMin.x - margin, tileMin.y - margin);
            max = new double2(tileMax.x + margin, tileMax.y + margin);
        }
    }

    /// <summary>A dataset projected ONCE into the unit square, with a per-feature bounding box. Projection is
    /// zoom-independent — only the final affine map into a tile depends on z/x/y — so this is the whole of
    /// the work that can be shared across tiles, and slicing any tile at any zoom is then a bbox reject plus
    /// an affine map plus a clip.</summary>
    public sealed class GeoJsonProjectedDataset
    {
        private GeoJsonProjectedDataset(IReadOnlyList<ProjectedFeature> features, double2 bboxMin, double2 bboxMax)
        {
            Features = features;
            BboxMin  = bboxMin;
            BboxMax  = bboxMax;
        }

        internal IReadOnlyList<ProjectedFeature> Features { get; }

        /// <summary>The union of every feature's unit-square bounding box; inverted (<c>+∞</c>/<c>−∞</c>)
        /// for a dataset with no located feature, so an intersection test rejects everything.
        /// The source's O(1) emptiness probe answers "absent" for a tile whose
        /// <see cref="GeoJsonSliceOptions.UnitSquareWindow"/> misses it; an intersecting tile may still slice to
        /// nothing.</summary>
        public double2 BboxMin { get; }

        /// <inheritdoc cref="BboxMin"/>
        public double2 BboxMax { get; }

        public static GeoJsonProjectedDataset Project(GeoJsonDataset dataset)
        {
            if (dataset == null) throw new ArgumentNullException(nameof(dataset));

            var projected = new List<ProjectedFeature>(dataset.Features.Count);
            double2 datasetLo = new double2(double.PositiveInfinity, double.PositiveInfinity);
            double2 datasetHi = new double2(double.NegativeInfinity, double.NegativeInfinity);

            for (int f = 0; f < dataset.Features.Count; f++)
            {
                GeoJsonFeature source = dataset.Features[f];

                var paths = new List<List<double2>>(source.Paths.Count);
                // An empty feature (RFC §3.2's unlocated feature, or an empty coordinates array) gets an
                // INVERTED bbox, so the reject test below discards it without a special case.
                double2 lo = new double2(double.PositiveInfinity, double.PositiveInfinity);
                double2 hi = new double2(double.NegativeInfinity, double.NegativeInfinity);

                for (int p = 0; p < source.Paths.Count; p++)
                {
                    IReadOnlyList<GeoCoordinate> geodetic = source.Paths[p];
                    var path = new List<double2>(geodetic.Count);
                    for (int i = 0; i < geodetic.Count; i++)
                    {
                        double2 unit = WebMercatorTiling.UnitSquareFromLonLat(geodetic[i]);
                        path.Add(unit);
                        lo = math.min(lo, unit);
                        hi = math.max(hi, unit);
                    }
                    paths.Add(path);
                }

                // The dataset box is the union of the per-feature boxes, folded here rather than per vertex:
                // an unlocated feature contributes its INVERTED box, which `min`/`max` absorb to nothing.
                datasetLo = math.min(datasetLo, lo);
                datasetHi = math.max(datasetHi, hi);

                projected.Add(new ProjectedFeature
                {
                    Source  = source,
                    Paths   = paths,
                    BboxMin = lo,
                    BboxMax = hi
                });
            }

            return new GeoJsonProjectedDataset(projected, datasetLo, datasetHi);
        }
    }

    internal sealed class ProjectedFeature
    {
        public GeoJsonFeature Source { get; init; }

        /// <summary>Unit-square paths, in the parsed feature's path order.</summary>
        public List<List<double2>> Paths { get; init; }

        public double2 BboxMin { get; init; }
        public double2 BboxMax { get; init; }
    }

    /// <summary>One feature's geometry as it falls inside one tile. Identity and attributes are carried BY
    /// REFERENCE to the parsed feature — slicing never copies or reinterprets them.</summary>
    public sealed class SlicedFeature
    {
        public GeoJsonFeature Source { get; init; }

        /// <summary>Tile-local paths. For polygons the per-polygon grouping is flattened away; ring role is
        /// carried by winding, which is what the downstream assembler classifies on.</summary>
        public IReadOnlyList<IReadOnlyList<double2>> Paths { get; init; }
    }

    /// <summary>The geometry of one tile. A tile with nothing in it yields an EMPTY feature list — the
    /// null-vs-empty question at the source boundary belongs to the source, not here.</summary>
    public sealed class TileSlice
    {
        public TileId Tile { get; init; }
        public double Extent { get; init; }
        public IReadOnlyList<SlicedFeature> Features { get; init; }
    }

    /// <summary>
    /// Cuts a projected GeoJSON dataset into one tile's geometry: bbox reject → affine map into tile-local
    /// coordinates → clip to the buffered window → quantize. Output is tile-local <c>double2</c> at
    /// <see cref="GeoJsonSliceOptions.Extent"/>, origin top-left, Y down; rings are implicitly closed.
    /// Non-local invariant: exteriors are positive shoelace and holes negative, the MVT convention
    /// <c>RingAssemblyJob</c> classifies against, because both clippers keep the winding
    /// <see cref="GeoJsonParser"/> normalised. Slicing is lazy and stateless, O(features) per tile with no
    /// spatial index; eager pyramid slicing would loop over up to <c>4^z</c> tiles. Rounding runs after
    /// clipping, so an on-boundary vertex stays on an integer edge (error ≤ 0.5 tile units); the assembler's
    /// area filters and earcut's cure → split → drop cascade handle a ring that rounding collapses.
    /// </summary>
    public static class GeoJsonTileSlicer
    {
        public static TileSlice Slice(
            GeoJsonProjectedDataset dataset, TileId tile, in GeoJsonSliceOptions options)
        {
            if (dataset == null) throw new ArgumentNullException(nameof(dataset));

            if (options.SimplifyTolerance != 0.0)
                throw new NotSupportedException(
                    "GeoJsonSliceOptions.SimplifyTolerance is not implemented; v1 slices at the authored " +
                    "resolution. The only supported value is 0.");

            options.Validate();

            options.Window(out double2 windowMin, out double2 windowMax);

            // The same window in unit-square terms, so the bbox reject runs before any per-vertex work.
            options.UnitSquareWindow(tile, out double2 unitMin, out double2 unitMax);

            var features = new List<SlicedFeature>();

            foreach (ProjectedFeature feature in dataset.Features)
            {
                if (feature.BboxMax.x < unitMin.x || feature.BboxMin.x > unitMax.x ||
                    feature.BboxMax.y < unitMin.y || feature.BboxMin.y > unitMax.y)
                    continue;

                List<IReadOnlyList<double2>> paths =
                    ClipFeature(feature, tile, options, windowMin, windowMax);

                if (paths.Count == 0) continue;

                features.Add(new SlicedFeature { Source = feature.Source, Paths = paths });
            }

            return new TileSlice { Tile = tile, Extent = options.Extent, Features = features };
        }

        private static List<IReadOnlyList<double2>> ClipFeature(
            ProjectedFeature feature, TileId tile, in GeoJsonSliceOptions options,
            double2 windowMin, double2 windowMax)
        {
            var output = new List<IReadOnlyList<double2>>();

            switch (feature.Source.GeometryType)
            {
                case TileGeometryType.Point:
                    // A point is in one tile before the margin and in up to four with it. That duplication lets
                    // a symbol near a seam be placed from either tile.
                    foreach (List<double2> path in feature.Paths)
                    {
                        if (path.Count == 0) continue;
                        double2 p = ToTileLocal(path[0], tile, options.Extent);
                        if (p.x < windowMin.x || p.x > windowMax.x || p.y < windowMin.y || p.y > windowMax.y)
                            continue;
                        output.Add(new List<double2> { Quantize(p) });
                    }
                    break;

                case TileGeometryType.LineString:
                    foreach (List<double2> path in feature.Paths)
                    {
                        List<double2> local = ToTileLocal(path, tile, options.Extent);
                        foreach (List<double2> piece in
                                 PolylineWindowClipper.Clip(local, windowMin, windowMax))
                            output.Add(Quantize(piece));
                    }
                    break;

                case TileGeometryType.Polygon:
                    ClipPolygons(feature, tile, options, windowMin, windowMax, output);
                    break;

                case TileGeometryType.Unknown:
                    // RFC §3.2's unlocated feature (`"geometry": null`) has no geometry, so it contributes
                    // to no tile; the empty path list is intended.
                    break;
            }

            return output;
        }

        /// <summary>
        /// Clips polygon-by-polygon rather than ring-by-ring, so a polygon whose exterior clips away takes
        /// its holes with it. An orphaned hole would otherwise become the feature's FIRST ring downstream,
        /// establish the exterior sign itself, and render as an inverted patch. Geometrically the case cannot
        /// arise for well-formed input — a hole lies inside its exterior — but it can for malformed input,
        /// and the failure mode is silent-wrong.
        /// </summary>
        private static void ClipPolygons(
            ProjectedFeature feature, TileId tile, in GeoJsonSliceOptions options,
            double2 windowMin, double2 windowMax, List<IReadOnlyList<double2>> output)
        {
            IReadOnlyList<int> ringCounts = feature.Source.PolygonRingCounts;
            int ring = 0;

            for (int p = 0; p < ringCounts.Count; p++)
            {
                int ringCount = ringCounts[p];

                List<double2> exterior = RingWindowClipper.Clip(
                    ToTileLocal(feature.Paths[ring], tile, options.Extent), windowMin, windowMax);

                if (exterior != null)
                {
                    output.Add(Quantize(exterior));

                    for (int h = 1; h < ringCount; h++)
                    {
                        List<double2> hole = RingWindowClipper.Clip(
                            ToTileLocal(feature.Paths[ring + h], tile, options.Extent),
                            windowMin, windowMax);
                        if (hole != null) output.Add(Quantize(hole));
                    }
                }

                ring += ringCount;
            }
        }

        private static double2 ToTileLocal(double2 unitSquare, TileId tile, double extent)
            => WebMercatorTiling.TileLocal(unitSquare, tile, extent);

        private static List<double2> ToTileLocal(List<double2> unitSquare, TileId tile, double extent)
        {
            var local = new List<double2>(unitSquare.Count);
            for (int i = 0; i < unitSquare.Count; i++)
                local.Add(WebMercatorTiling.TileLocal(unitSquare[i], tile, extent));
            return local;
        }

        private static double2 Quantize(double2 p) => new double2(math.round(p.x), math.round(p.y));

        private static List<double2> Quantize(List<double2> path)
        {
            for (int i = 0; i < path.Count; i++)
                path[i] = Quantize(path[i]);
            return path;
        }
    }
}
