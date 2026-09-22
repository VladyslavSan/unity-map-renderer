using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using Unity.Mathematics;

namespace MapRenderer.Core.GeoJson
{
    /// <summary>
    /// How a dataset is cut into tiles. A <c>readonly struct</c> so it can be taken <c>in</c> without the
    /// per-read defensive copy a mutable struct would force.
    ///
    /// <para>Every member is a SOURCE-level parameter: nothing forces the MVT conventions on a synthesised
    /// source, so <see cref="Extent"/> is chosen rather than inherited. Build from
    /// <see cref="Default"/> — a <c>default(GeoJsonSliceOptions)</c> has <c>Extent = 0</c> and is
    /// rejected.</para>
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
        /// Buffer margin kept on every side, authored in tile units at
        /// <see cref="TileBufferClip.ReferenceExtent"/> and rescaled to <see cref="Extent"/> by the same
        /// <c>b = keep · extent / 4096</c> formula <see cref="TileBufferClip.TryWindow"/> uses. Set 0 to cut
        /// exactly at the tile boundary; negative and NaN both degrade to that in <see cref="Window"/>.
        ///
        /// <para><b>There is no GeoJSON-specific fill seam.</b> At the production configuration
        /// <c>MapViewConfig.FillTileBufferClip</c> defaults to <c>0.0</c>, and
        /// <c>TileBufferClip.FromInspectorUnits(0.0)</c> takes the <c>KeepTileUnits(0.0)</c> branch, whose
        /// <c>IsEnabled</c> is <b>true</b> with a keep of 0 — only a NEGATIVE knob disables it. So the fill
        /// pipeline clips every layer to <c>[0, extent]</c>, MVT and GeoJSON alike, and a GeoJSON fill sliced
        /// at buffer 64 is cut back by the same job that cuts a server-buffered MVT fill.</para>
        ///
        /// <para>What is NOT clipped, identically for both formats, is the <b>line</b> and <b>symbol</b>
        /// path: a polyline or a point inside the margin is emitted by both neighbouring tiles.</para>
        /// </summary>
        public double BufferAtReferenceExtent { get; init; }

        /// <summary>
        /// Douglas–Peucker-style tolerance, in tile units. <b>0 is the only supported value in v1</b>; any
        /// other is rejected by <see cref="Validate"/> where the options are accepted, and — for a caller
        /// that reaches <see cref="GeoJsonTileSlicer.Slice"/> with options it never handed to a source — by
        /// that method's own <see cref="NotSupportedException"/>.
        /// Present-but-throwing rather than absent so the deferral is an observable, tested state: a
        /// silently-ignored parameter is indistinguishable from an implemented one. Simplification is an
        /// optimisation, and a fixture wants the EXACT authored position.
        /// </summary>
        public double SimplifyTolerance { get; init; }

        public static GeoJsonSliceOptions Default => new GeoJsonSliceOptions
        {
            Extent                  = DefaultExtent,
            BufferAtReferenceExtent = DefaultBufferAtReferenceExtent,
            SimplifyTolerance       = 0.0
        };

        /// <summary>
        /// Throws unless every option is one this slicer can honour: <see cref="Extent"/> a WHOLE NUMBER in
        /// <c>(0, uint.MaxValue]</c>, and <see cref="SimplifyTolerance"/> exactly 0.
        /// <see cref="BufferAtReferenceExtent"/> is unchecked — <see cref="Window"/> degrades
        /// NaN and every negative to the tile boundary, so its whole domain is honourable.
        ///
        /// <para><b>Why integrality, and not a silent round.</b> Tile-local coordinates are quantized to
        /// integers, and downstream the extent is carried in two forms: as the <c>double</c> it is here, and
        /// as the whole number a decoded tile layer reports (the wire format's extent field is an unsigned
        /// integer). A fractional extent makes those two disagree — 4096.5 arrives as both 4096.5 and 4096 —
        /// so every consumer that joins them is off by a fraction of a tile with nothing to notice. There is
        /// no authoring intent a fractional extent expresses, so it is rejected at the door rather than
        /// rounded into a value nobody asked for.</para>
        ///
        /// <para><b>Why the upper bound is <c>uint.MaxValue</c>, and why it is what rejects infinity.</b>
        /// The pair of readings above is the whole point, so the domain of this option is the domain of the
        /// narrower reading: a decoded layer carries the extent as a <c>uint</c>
        /// (<c>GeoJsonTileLayer.Extent</c>) while the materializer keeps this <c>double</c>. An integral
        /// value above <c>uint.MaxValue</c> passes the integrality clause and then narrows by an UNCHECKED
        /// conversion, so the layer reports one extent and the geometry another — the same defect the
        /// integrality clause closes, through a different door. <c>double.PositiveInfinity</c> is both
        /// positive and equal to its own <c>floor</c>, so the upper bound is also the only clause that
        /// rejects it, and it has to be rejected here rather than downstream: <see cref="Window"/> would
        /// make <c>∞ − ∞</c>, and a NaN window keeps every tile and then slices away every vertex. NaN
        /// itself fails all three comparisons. <c>uint.MaxValue</c> is ADMITTED rather than capped at some
        /// smaller "sensible" extent: extent is a per-source authoring choice, and what this validator owes
        /// is representability, not taste.</para>
        ///
        /// <para><b>Why the tolerance is validated HERE.</b> A non-zero <see cref="SimplifyTolerance"/> is
        /// unimplemented, so it is as unusable as a zero extent, and this validator states the whole domain
        /// of the options it validates. <see cref="GeoJsonTileSlicer.Slice"/> keeps its own tolerance guard,
        /// a DUPLICATE of this arm — <c>Slice</c> calls <c>Validate()</c> unconditionally three lines later,
        /// so deleting it would reject the same options here instead. It is kept because it runs FIRST, so
        /// the exception a direct <c>Slice</c> caller sees stays a <see cref="NotSupportedException"/> rather
        /// than this <see cref="ArgumentOutOfRangeException"/>. Unifying the two types is a behaviour change
        /// on a public API, out of scope here.</para>
        ///
        /// <para>Called by <see cref="GeoJsonTileSlicer.Slice"/> and by every source that retains options to
        /// slice with later — the point of validating from one place is that "the window is NaN and every
        /// decode faults" cannot be discovered a scope at a time.</para>
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
        /// The inclusive clip window in tile units — <c>[−b, Extent + b]²</c>. Deliberately the same
        /// arithmetic, in the same order, as <see cref="TileBufferClip.TryWindow"/>, so the two windows are
        /// bit-equal and not merely close. The operand ORDER is in fact free while
        /// <see cref="TileBufferClip.ReferenceExtent"/> is a power of two — dividing by it is an exact binary
        /// scaling — but keeping it identical is what makes the equality readable without that argument, and
        /// what survives a change of reference extent.
        ///
        /// <para><b>Including that type's two input guards</b>, which a window advertising itself as the
        /// same window has to carry as well. A NEGATIVE margin would erode INTO the tile
        /// (<c>min &gt; 0</c>, <c>max &lt; Extent</c>) instead of cutting at its boundary, silently losing
        /// geometry the tile owns. A NaN one is worse — <c>math.max(0, NaN)</c> is NaN, so it would make an
        /// all-NaN window against which every vertex tests outside and the whole map slices away to nothing.
        /// Both degrade to "cut exactly at the tile boundary", which is why NaN is rejected explicitly
        /// rather than clamped.</para>
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
        /// The same window as <see cref="Window"/>, expressed in UNIT-SQUARE terms for a given tile: the
        /// tile's own corners grown by the buffer margin. Two callers need exactly this box and it must be
        /// exactly the same box in both — <see cref="GeoJsonTileSlicer.Slice"/>'s per-feature bbox reject,
        /// which runs before any per-vertex work, and the source-level O(1) emptiness probe that decides
        /// whether a tile is worth a decode handle at all. A second copy of the arithmetic would let the
        /// probe reject a tile the slicer would have kept.
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

        /// <summary>The union of every feature's unit-square bounding box — INVERTED
        /// (<c>+∞</c>/<c>−∞</c>) for a dataset with no located feature, so an intersection test rejects
        /// everything with no special case, exactly as the per-feature boxes already do.
        ///
        /// <para>Its consumer is the source's O(1) emptiness probe: a tile whose buffered unit-square window
        /// (<see cref="GeoJsonSliceOptions.UnitSquareWindow"/>) misses this box cannot contain a feature, so
        /// the source can answer "absent" without slicing. Conservative in the safe direction — an
        /// intersecting tile may still slice to nothing.</para></summary>
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
    /// Cuts a projected GeoJSON dataset into one tile's worth of geometry: bbox reject → affine map into
    /// tile-local coordinates → clip to the buffered window → quantize.
    ///
    /// <para><b>Coordinate space and winding (producer declaration).</b> Output is tile-local
    /// <c>double2</c> at <see cref="GeoJsonSliceOptions.Extent"/>, origin top-left, Y down, quantized to
    /// integers. Exterior rings are CW-on-screen (POSITIVE shoelace, standard cross-product sum),
    /// holes CCW (negative) — the MVT convention
    /// <c>RingAssemblyJob</c> classifies against, so a GeoJSON tile is indistinguishable
    /// from an MVT one downstream. Rings are implicitly closed (the first vertex is not repeated). Both
    /// clippers are orientation-preserving, so output winding equals the winding
    /// <see cref="GeoJsonParser"/> normalised. The Unity-front reversal for stock Cull Back stays where it
    /// is, at the mesh-write boundary in <c>StyledFillTileBuilder</c>
    /// (<c>docs/coordinates-and-projections.md</c>).</para>
    ///
    /// <para><b>Lazy, not eager, and stateless.</b> Slicing is a pure function of (dataset, tile, options),
    /// evaluated per requested tile; memoization belongs to the source that calls it. Eager pyramid slicing
    /// would mean enumerating up to <c>4^z</c> tiles for a world-spanning dataset — an unbounded loop that
    /// laziness ELIMINATES rather than caps. Per-slice cost is O(features) via the bbox reject, over a count
    /// fixed at parse time; a spatial index is a later optimisation, invisible at this signature.</para>
    ///
    /// <para><b>Quantization.</b> Rounding to integers happens AFTER clipping, so that when the extent and
    /// the rescaled buffer are integral the window edges land on integers and an intersection vertex placed
    /// exactly on a boundary stays there. Maximum positional error is 0.5 tile units
    /// (<c>WorldExtent / (extent · 2^z)</c> metres ≈ 0.3 m at extent 4096, z14). Rounding can, in
    /// pathological cases, collapse a thin ring or introduce a self-touch; the assembler's <c>rLen &lt; 3</c>
    /// / <c>|area2| &lt; 1</c> filters and earcut's cure → split → drop cascade already handle that.</para>
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
                    // A point is in exactly one tile before the margin; with it, up to four. That duplication
                    // is the point of the margin — it is what lets a symbol near a seam be placed from either
                    // tile.
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
                    // RFC §3.2's UNLOCATED feature — `"geometry": null`, which GeoJsonParser accepts. It
                    // carries identity and properties but no geometry, so it contributes to no tile: the
                    // empty path list is the intended result, not a gap.
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
