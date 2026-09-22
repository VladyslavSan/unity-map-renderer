// No `using UnityEngine` — but NOT engine-free any more, and NOT a core-tests file. It consumes
// Unity.Collections transitively through TileGeometryBuffers (the Waist-1 buffer it materializes and reads),
// so it cannot compile in Tools/core-tests and must not be re-added to core-tests.csproj. That dependency is
// why it lives in MapRenderer.Unity at all: MapRenderer.Core references only
// Unity.Mathematics + UniTask, and adding Unity.Collections there would erase the Core/Jobs split
// (Core keeps the managed evaluation surface; Jobs owns the blittable geometry).
//
// Mirrors StyledLineTileBuilder's select -> materialize -> project pattern, but emits pre-shaping
// SymbolFeatures instead of a Mesh.

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// Extracts <see cref="SymbolFeature"/>s from a decoded MVT tile for one symbol style
    /// layer. Reuses the existing seams: <see cref="FeatureSelector.SelectFeatures"/> (source-layer resolve
    /// + filter), the Waist-1 tile-geometry buffer (below), and
    /// <see cref="TileId.ToLonLat"/> → <see cref="IProjection.Project"/> (tile → geo → render space, PRE-RTC).
    /// Point AND LineString geometry (a LineString anchors at its mid arc-length under
    /// point placement, and at along-line anchors under a viewport-resolved line placement — see
    /// <see cref="AlignmentResolution"/>); Polygon is never accepted. One symbol per anchor (a MultiPoint
    /// feature emits one symbol per point; a viewport-resolved line emits one symbol per along-line anchor).
    /// A MAP-resolved line ICON instead emits ONE curved symbol per path, whose single glyph is the icon
    /// quad — the anchors ride inside it rather than each becoming their own symbol.
    /// Clean-room — the shaping step is Unity-side.
    ///
    /// <para><b>Geometry.</b> Instead of decoding each feature's command
    /// stream for itself, this reads each feature's path spans out of the source-layer's tile-local
    /// <see cref="TileGeometryBuffers"/> (Waist 1). That buffer is <see cref="ITileLayer.Geometry"/>
    /// — the layer's own, minted once inside the decode — and it is <b>BORROWED</b>: the decoded tile owns
    /// it and frees it when its decode scope closes, so this method must never dispose it. (The buffer is
    /// array-backed, so a second free here would be a loud double free, not a quiet leak.) Because the buffer
    /// spans the WHOLE layer, per-feature side data is keyed on <see cref="SelectedTileFeature.Ordinal"/>,
    /// which is what <c>RingFeatureIdx</c> names.</para>
    ///
    /// <para><b>Landmine #5 — the fused-<c>RingAssemblyJob</c> fence.</b> Symbol has no polygon, hole, area or
    /// triangulation concept and rejects Polygon features outright, so it must never call
    /// <c>FillMeshGraph.Schedule</c>, never schedule or consume <c>RingAssemblyJob</c>/<c>RingClipJob</c>/
    /// <c>EarcutJob</c>/<c>GlobeFillSubdivideJob</c>, and never apply an area/shoelace test to a symbol path —
    /// a Point feature's 1-point path has no area at all and a straight road has exactly zero.</para>
    ///
    /// <para><b>Landmine #2 — symbol is the consumer that finally OBSERVES the unfiltered buffer.</b> The
    /// point branch below has <b>no path-length filter at all</b>: a 1-point path is a real, rendered symbol.
    /// The line branch filters <c>&lt; 2</c> and fill filters <c>&lt; 3</c> — three consumers, three
    /// thresholds, one unfiltered buffer. A short-ring filter in the Waist-1 materializer, in
    /// <c>MvtDecodeJob</c> or in any shared stage would delete every Point-feature symbol here, which is why
    /// no such filter may ever be fused upstream.</para>
    /// </summary>
    public static class SymbolFeatureExtractor
    {
        /// <summary>
        /// Append every extracted symbol of <paramref name="layer"/> over <paramref name="tile"/> to
        /// <paramref name="output"/>. Features whose <c>text-field</c> resolves to null/empty AND whose
        /// <c>icon-image</c> resolves to nothing are skipped; Polygon features are always ignored (LineString
        /// IS accepted — see the class doc). <paramref name="output"/> is appended to, never cleared.
        /// </summary>
        /// <param name="layer">The symbol style layer (a non-symbol layer is a no-op).</param>
        /// <param name="tile">The decoded tile.</param>
        /// <param name="tileId">
        /// <b>IGNORED — do not add a reader.</b> The address this method projects through and packs into the
        /// <c>TileKey</c> is the resolved layer's own <c>ITileLayer.Geometry.Tile</c>, stamped at the decode;
        /// a caller-supplied address here would be a second, driftable copy of that value
        /// (<see cref="ITileDecoder.Decode"/> enters the address into the pipeline exactly ONCE, at the fetch).
        /// </param>
        /// <param name="zoom">Current zoom, for evaluating zoom-dependent text-size/sort-key/paint AND the
        /// build-zoom-evaluated <c>symbol-placement</c> (frozen for this tile's lifetime,
        /// never re-evaluated per frame).</param>
        /// <param name="projection">Geo → render-space projection.</param>
        /// <param name="output">Caller-owned list the extracted symbols are appended to.</param>
        /// <param name="spriteAtlas">
        /// The sprite sheet <c>icon-image</c> resolves against; <c>null</c> (the default) yields NO icon
        /// symbols regardless of the layer's <c>icon-*</c> properties, so a caller that omits this argument
        /// never produces icons. Icons resolve at EVERY placement: point, line with
        /// <c>icon-rotation-alignment</c> resolving to <c>viewport</c> (the upright-at-anchor case), and line
        /// with it resolving to <c>map</c> (the along-line case, emitted as a one-glyph curved symbol —
        /// <c>road_one_way_arrow*</c>).
        /// </param>
        public static void Extract(
            MapRenderer.Core.Style.StyleLayer layer,
            IDecodedTile                      tile,
            TileId                            tileId,
            double                            zoom,
            IProjection                       projection,
            List<SymbolFeature>                 output,
            SpriteAtlasView                   spriteAtlas = null)
        {
            if (!(layer is MapRenderer.Core.Style.Symbol.StyleLayer symbolLayer) || tile == null || projection == null || output == null)
                return;

            // NOTE: layer minzoom/maxzoom is deliberately NOT gated here. Tile DATA tops out at a max source zoom
            // (e.g. z14 for OpenFreeMap), so at higher camera zooms those tiles are OVERZOOMED (reused, not
            // rebuilt) — gating at build time would freeze layer visibility at the build zoom and hide layers
            // (poi_r1/r7/r20 @ minzoom 15/16/17) that MapLibre reveals as you zoom past the data level. The gate
            // therefore lives at DISPLAY time against the LIVE camera zoom (see StyleLayer.IsVisibleAtZoom, applied
            // per-frame in SymbolPlacementSystem) so overzoomed data still turns layers on/off correctly.

            // Fully qualified rather than a `using MapRenderer.Core.Style;`: that namespace also holds the
            // BASE StyleLayer, which a using would make ambiguous with the symbol StyleLayer below.
            ITileLayer tileLayer = MapRenderer.Jobs.Tiles.SourceLayerResolver.ResolveTileLayer(layer, tile);
            if (tileLayer == null) return;

            // The BUFFER is the sole authority for the address and the extent it was quantized against —
            // read together, off one object, so no pair of them can drift.
            //
            // Nothing created ⇒ no rings ⇒ no symbol is reachable (every emit below walks the bucketed ring
            // order), so this returns the same empty result the loop would — and it is what makes reading
            // Tile/Extent off the buffer safe, since `default` carries neither. Mirrors line's own guard,
            // StyledLineTileBuilder.BuildLayerInput's `!geometry.IsCreated` early return.
            TileGeometryBuffers geometry = tileLayer.Geometry;
            if (!geometry.IsCreated) return;
            TileId tileAddress = geometry.Tile;
            double extent      = geometry.Extent;

            // The ORDINAL-returning selection overload, taking the already-resolved layer. The
            // ordinals are what the shared buffer's RingFeatureIdx names; resolving the layer a second time
            // inside the selector would be a second chance to resolve it differently. Selection order is
            // unchanged — the selector appends in Features order, so selected order IS decode order.
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(layer, tileLayer, zoom, selected);
            // Nothing selected ⇒ no symbol can be emitted. The decode already materialized
            // every layer, so this no longer avoids any work upstream — it is the plain early-out it reads
            // as, mirroring TileMeshLayerProcessor's `selected.Count > 0`.
            if (selected.Count == 0) return;
            long                        tileKey  = SymbolTileKey.Pack(tileAddress);
            int                         ordinal  = 0;

            LayoutProperties layout = symbolLayer.Layout;
            PaintProperties  paint  = symbolLayer.Paint;

            // text-translate is a constant px offset (not feature-dependent) — stamp it onto every symbol.
            // Stored y-down (as authored); the y-flip happens at placement.
            float2 translatePx = paint.Translate;

            // symbol-placement is expression-capable but evaluated ONCE here, at the tile's build zoom —
            // never per frame, never re-evaluated as the camera crosses a step boundary (an accepted known
            // limit). TryEvaluate degrades to Point on a malformed/data-driven
            // expression rather than throwing (symbol-placement is never data-driven in a real style).
            SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(zoom, null, out SymbolPlacement evaluatedPlacement)
                ? evaluatedPlacement : SymbolPlacement.Point;
            bool isLine = placement != SymbolPlacement.Point;

            // Resolve rotation-alignment against placement ONCE per layer (both are plain parsed enums, not
            // feature-dependent — MapLibre's alignment keys are never data-driven). Under LINE placement, a
            // symbol whose alignment resolves AWAY from Map is NOT curved — MapLibre lays it out as an
            // ordinary upright (viewport) block at each along-line anchor, the road-shield look.
            AlignmentMode textAlign    = AlignmentResolution.Resolve(layout.TextRotationAlignment, placement);
            AlignmentMode iconAlign    = AlignmentResolution.Resolve(layout.IconRotationAlignment, placement);
            // The PITCH twins, resolved on the SAME once-per-layer terms (the spec's pitch `auto` defers
            // to the RESOLVED rotation alignment, which is what ResolvePitch encodes). Unlike the rotation
            // values above — recorded as authored and re-resolved downstream — these are stamped RESOLVED
            // onto the emitted symbol, because the curved staging arm consumes them and has no placement in
            // hand to resolve `auto` against.
            AlignmentMode textPitch    = AlignmentResolution.ResolvePitch(
                layout.TextPitchAlignment, layout.TextRotationAlignment, placement);
            AlignmentMode iconPitch    = AlignmentResolution.ResolvePitch(
                layout.IconPitchAlignment, layout.IconRotationAlignment, placement);
            bool          textAtAnchors = isLine && textAlign != AlignmentMode.Map;
            bool          iconAtAnchors = isLine && iconAlign != AlignmentMode.Map;
            // The third icon mode. A MAP-resolved line icon rides the along-line anchors AND rotates to
            // the local line tangent — emitted as a one-glyph curved symbol (see EmitAlongLineIcon).
            bool          iconAlongLine = isLine && iconAlign == AlignmentMode.Map;

            // Waist 1: the WHOLE source layer, decoded ONCE into the layer's own tile-local
            // buffer and BORROWED from it. `RingFeatureIdx[r]` therefore indexes `tileLayer.Features` — the
            // layer's own ordinal — not this layer's selected list, which is why every per-feature array below
            // is sized to the LAYER and addressed by `SelectedTileFeature.Ordinal`. An array sized to the
            // selected count would silently mis-bucket (and, whenever the highest selected ordinal exceeds
            // that count, index out of range).
            //
            // Sized from `geometry.FeatureCount`, as LINE already sizes its three columns
            // (StyledLineTileBuilder). `RingFeatureIdx`'s values ARE indices into the buffer's own feature
            // column, so that column's length is the domain being bucketed; `Features.Count` is a different
            // list that merely happens to hold the same count. For MVT it always does (OrdinalDomainTests
            // clause B pins the lockstep); the two consumers now agree on one source of truth rather than on
            // an invariant only one writer maintains.
            int layerFeatureCount = geometry.FeatureCount;

            // RingFeatureIdx joins each ring back to its feature's LAYER ORDINAL. Bucket ONCE, ascending in r,
            // so each feature's paths keep DECODE ORDER (landmine #4 — the extractor's per-tile `ordinal` is
            // the stable FeatureIndex tiebreak, so path order is observable output, not an implementation
            // detail). Contiguity is NOT assumed: a counting sort is stable and correct either way.
            // RingCount is the DECODED count, not RingCapacity; RingOffsets carries a trailing sentinel.
            // Rank 3 GC fix: the counting-sort scratch is native now (no per-tile managed garbage). `using var`
            // — construction and disposal are one statement per handle, so a constructor throw partway through
            // leaves nothing stranded and there is no hand-rolled finally to keep in sync. Allocator.Persistent,
            // NOT TempJob: this extract runs off-main and can span >4 main-thread frames, tripping TempJob's
            // 4-frame lifetime check. ClearMemory (the
            // default 2-arg ctor) is REQUIRED — ringStart is accumulated from 0 via ringStart[idx+1]++.
            // `cursor` MUST stay a separate buffer: ringStart is read again in the feature loop (pathCount =
            // ringStart[f+1]-ringStart[f]) while cursor is mutated here.
            using var ringStart = new NativeArray<int>(layerFeatureCount + 1, Allocator.Persistent);
            using var ringOrder = new NativeArray<int>(geometry.RingCount, Allocator.Persistent);
            using var cursor    = new NativeArray<int>(layerFeatureCount + 1, Allocator.Persistent);
            // A `using`-declared local is read-only for index-ASSIGNMENT (CS1654) — reads through
            // ringStart/ringOrder/cursor below are unaffected; only writes need a plain-local alias.
            // GetSubArray(0, Length) is a normal method call returning a NativeArray<T> VIEW over the same
            // memory, assignable to a non-readonly local.
            NativeArray<int> ringStartWritable = ringStart.GetSubArray(0, ringStart.Length);
            NativeArray<int> ringOrderWritable = ringOrder.GetSubArray(0, ringOrder.Length);
            NativeArray<int> cursorWritable    = cursor.GetSubArray(0, cursor.Length);
            for (int r = 0; r < geometry.RingCount; r++) ringStartWritable[geometry.RingFeatureIdx[r] + 1]++;
            for (int i = 0; i < layerFeatureCount; i++) ringStartWritable[i + 1] += ringStartWritable[i];
            NativeArray<int>.Copy(ringStartWritable, cursorWritable);
            for (int r = 0; r < geometry.RingCount; r++) ringOrderWritable[cursorWritable[geometry.RingFeatureIdx[r]]++] = r;

            // Walks the SELECTION, in selection order — which is decode order, because the selector appends in
            // Features order — and addresses the bucketed rings by each entry's layer ordinal. That pairing is
            // what keeps both the emitted symbol sequence and the `ordinal` counter below byte-identical to the
            // plain "iterate the selected list" loop.
            for (int si = 0; si < selected.Count; si++)
            {
                int      f       = selected[si].Ordinal;
                IFeature feature = selected[si].Feature;
                // Point placement ALSO accepts a LineString feature (one anchor at its mid arc-length,
                // below) — the shields' "point" step branch runs over LineString road geometry. Polygon
                // stays unaccepted at every placement.
                if (isLine
                        ? feature.GeometryType != TileGeometryType.LineString
                        : (feature.GeometryType != TileGeometryType.Point &&
                           feature.GeometryType != TileGeometryType.LineString))
                    continue;

                // The feature IS an IFeature (the neutral carrier implements it directly) — no adapter alloc.
                // Text and icon are INDEPENDENT — a feature may resolve either, both, or neither. Only when
                // NEITHER resolves is the feature skipped.
                string text = TextFieldResolver.Resolve(layout.TextField, feature);
                if (text != null)
                {
                    // text-transform: case-fold the resolved symbol before it is shaped downstream.
                    text = layout.TextTransform.Apply(text);
                }

                // Icons resolve at every placement — point, a viewport-resolved line (iconAtAnchors) and a
                // map-resolved line (iconAlongLine). Only resolved when the caller supplied a sprite atlas;
                // a null atlas never produces icons.
                bool        hasIcon   = false;
                SpriteEntry iconEntry = default;
                // At feature scope because the resolved sprite name is the icon's cross-tile identity,
                // needed at the icon-emit site below (SymbolFeature.IconImage).
                string iconImage = null;
                // There is no map-aligned fence here, only a third emit SHAPE: `iconAlongLine` routes to
                // EmitAlongLineIcon below, where the icon rides the same along-line anchors as the viewport
                // case but rotates to the projected tangent instead of staying screen-upright.
                if ((!isLine || iconAtAnchors || iconAlongLine) && spriteAtlas != null)
                {
                    iconImage = IconImageResolver.Resolve(layout.IconImage, feature);
                    if (iconImage != null)
                        hasIcon = spriteAtlas.Index.TryGetSprite(iconImage, out iconEntry);
                }

                if (text == null && !hasIcon) continue; // neither a text symbol nor an icon → nothing to emit

                // The PAIRING predicate is "this feature resolved BOTH a text and an icon", nothing more.
                // The two halves are stamped ONE placement instance downstream (SymbolPairing /
                // StagePointPair). There are no extra conjuncts (text/icon anchor == Center, zero text/icon
                // offset, zero radial offset): MapLibre's model is an INSTANCE of icon + text placed
                // together, not two symbols that happen to coincide, so a bottom-anchored city name is as
                // much one instance with its dot as a shield's centred ref is with its badge.
                //
                // Coincident boxes were never what made pairing work. Each half's anchor/offset is baked
                // ANCHOR-RELATIVE upstream — TextQuadLayout.Layout folds text-anchor/-offset/-radial-offset
                // into every quad before measuring the block's TextLayoutBounds.Min/Max, and IconQuadLayout.Layout
                // does the same for icon-anchor/-offset — so SymbolBox.Build's `anchor + baked bounds` puts
                // each half exactly where the style asked, and SymbolStagingMath.StagePointPair appending both
                // halves at the OWNER's ScreenPx stays correct with no per-half placement plumbing. A
                // non-centred pair's two boxes are simply DISJOINT; each is still collision-tested on its own
                // (the candidate reserves no union box spanning the gap between them).
                //
                // icon-optional/text-optional do NOT appear in this predicate, and "pair only when both are
                // false" would be wrong: `text-optional` means "this instance may render icon-only",
                // which presupposes the instance. liberty's `airport` sets it and nothing else — un-paired,
                // its halves would be collision-tested independently and the text could place with the icon
                // culled, the one outcome the flag forbids. Optionality remains a per-BOX verdict inside the
                // test-all-then-insert collision loop (SymbolCandidate.OptionalBoxMask).
                bool pairedInstance = hasIcon && text != null;

                // Per-feature evaluated style (zoom + feature — safe for constant/zoom/data-driven).
                float      textSize   = layout.TextSize.Evaluate(zoom, feature);
                float      padding    = layout.TextPadding.Evaluate(zoom, feature);
                float      sortKey    = layout.SymbolSortKey.Evaluate(zoom, feature);
                float      spacing    = math.max(1f, layout.SymbolSpacing.Evaluate(zoom, feature)); // px, >= 1 (spec)
                float      maxAngle   = layout.TextMaxAngle.Evaluate(zoom, feature);                // degrees (#6)
                SymbolPaint symbolPaint = EvaluatePaint(paint, zoom, feature);

                // The icon quad/paint are feature-constant (icon-size/-padding/-opacity don't vary per
                // point within a MultiPoint feature) — build once here, stamp onto every point symbol below.
                SymbolQuad iconQuad    = default;
                SymbolPaint iconPaint   = default;
                float      iconPadding = 0f;
                float      iconRotateRadians = 0f;
                float      iconSkirtPx = 0f;
                if (hasIcon)
                {
                    float iconSize = layout.IconSize.Evaluate(zoom, feature);
                    iconPadding = layout.IconPadding.Evaluate(zoom, feature);
                    // degrees→radians ONCE, here — every downstream site reads radians. Build-zoom
                    // frozen for this tile's lifetime, the same accepted limit as every other icon property.
                    // Unit conversion only: the value keeps MapLibre's clockwise-positive SENSE all the way
                    // down, and enters the staging frame's opposite sense once, at
                    // SymbolBearing.IconRotationRadians (which is below both icon emit shapes, so one flip
                    // covers the point path and the along-line path alike).
                    iconRotateRadians = math.radians(layout.IconRotate.Evaluate(zoom, feature));
                    float iconOpacity = paint.IconOpacity.Evaluate(zoom, feature);
                    iconQuad = IconQuadLayout.Layout(iconEntry, spriteAtlas.Size, iconSize, layout.IconAnchor,
                        layout.IconOffset);
                    // The border baked into iconQuad, carried alongside it: every consumer that needs the
                    // CONTENT box back (collision, placement) subtracts exactly this.
                    iconSkirtPx = IconQuadLayout.SkirtPx(iconEntry, iconSize);
                    iconPaint = new SymbolPaint
                    {
                        TextColor   = new float4(1f, 1f, 1f, 1f),
                        Opacity     = iconOpacity,
                        HaloColor   = new float4(1f, 1f, 1f, 1f),
                        HaloWidthPx = 0f,
                        HaloBlurPx  = 0f,
                    };
                }

                // This feature's decoded paths, as a span of the bucketed ring order (decode order preserved).
                int pathCount = ringStart[f + 1] - ringStart[f];

                // LayoutOptions is only built when a point-style text symbol can actually be emitted
                // (point placement, or a viewport-resolved line — the curved branch never uses it).
                // AnchorEmitContext itself is built INSIDE each branch below rather than once
                // here — the two branches' contexts differ in two fields (Text/HasIcon suppression under the
                // line branch's map-aligned fence) and building them separately removes both the line
                // branch's dead construction (a curved-only feature never reads a context at all) and the
                // silent-divergence hazard of two hand-maintained initializers that must agree.
                TextLayoutOptions layoutOptions = !isLine || textAtAnchors
                    ? TextLayoutOptionsBuilder.Build(layout, zoom, feature)
                    : default;

                if (isLine)
                {
                    // The along-line icon's per-FEATURE values, built ONCE and read once per path below.
                    // Built here, not hoisted above the branch: only a map-resolved
                    // line icon reads it, so a text-only or viewport-resolved line constructs nothing.
                    AlongLineIconContext alongLineIconCtx = iconAlongLine && hasIcon
                        ? new AlongLineIconContext
                        {
                            IconQuad = iconQuad,
                            IconSkirtPx = iconSkirtPx,
                            IconImage = iconImage,
                            PaddingPx = iconPadding,
                            Paint = iconPaint,
                            AllowOverlap = layout.IconAllowOverlap,
                            IgnorePlacement = layout.IconIgnorePlacement,
                            RotationAlignment = layout.IconRotationAlignment,
                            PitchAlignment = iconPitch, // RESOLVED, unlike RotationAlignment above
                            SortKey = sortKey,
                            SpacingPx = spacing,
                            MaxAngleDeg = maxAngle,
                            IconRotateRadians = iconRotateRadians,
                        }
                        : default;

                    for (int p = 0; p < pathCount; p++)
                    {
                        IReadOnlyList<double2> path = CopyRing(geometry, ringOrder[ringStart[f] + p]);
                        if (path.Count < 2) continue; // need at least one segment to place along
                        // Anchors computed ONCE here in TILE space (zoom-invariant). symbol-spacing is px
                        // at the tile's on-screen size (512 logical px per tile at integer zoom), so px → tile
                        // units is `spacing · extent / TilePixelSize`. Projection-agnostic: uses only the layer
                        // extent + the 512 convention, no projection scale.
                        double spacingTileUnits = spacing * extent / WebMercator.TilePixelSize;

                        // S4: subdivide the tile-local path ONCE so ProjectPath/anchor-resolve and
                        // LineAnchorPlacement.Compute both index against the SAME finer sequence — never
                        // subdivide only one of the two, or LineAnchor.Segment silently desyncs from
                        // PathRender (docs/labels-and-symbols-design.md). On a flat projection
                        // (MaxRefineAngleRad == ∞, e.g. Mercator) this bypasses LineCurvatureSubdivision.Subdivide
                        // entirely and passes the ORIGINAL path straight through — the live Mercator
                        // byte-identity guarantee (zero-alloc, unchanged behaviour).
                        double                 maxRefineAngleRad = projection.MaxRefineAngleRad;
                        IReadOnlyList<double2> densePath;
                        if (double.IsPositiveInfinity(maxRefineAngleRad))
                        {
                            densePath = path;
                        }
                        else
                        {
                            // Per-vertex surface normal drives the split metric (the projected arc a segment
                            // subtends); re-derives geo per vertex, mirroring the mesh path's own re-projection
                            // of the subdivided points.
                            var ups = new double3[path.Count];
                            for (int i = 0; i < path.Count; i++)
                            {
                                double2 lonLat = tileAddress.ToLonLat(path[i].x, path[i].y, extent);
                                ups[i] = projection.ProjectPoint(
                                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x }).Up;
                            }

                            densePath = LineCurvatureSubdivision.Subdivide(path, ups, maxRefineAngleRad);
                        }

                        // Anchors computed once, shared by BOTH sub-branches below.
                        LineAnchor[] anchors = LineAnchorPlacement.Compute(densePath, spacingTileUnits, placement);

                        // The same single-world clip the point path applies, here over the LINE branch's
                        // shared anchors — so an anchor in the MVT buffer strip is emitted by the tile that
                        // OWNS it and no other.
                        anchors = KeepAnchorsInsideTile(anchors, densePath, extent);
                        if (anchors.Length == 0) continue; // every anchor belongs to a neighbour — nothing here

                        // Curved text — only when this symbol is NOT upright-at-anchors (map-aligned, or
                        // line-center's textAlign resolves Map).
                        if (text != null && !textAtAnchors)
                        {
                            double3[] textPathRender = ProjectPath(densePath, tileAddress, extent, projection, out double3[] textPathUps);
                            output.Add(new SymbolFeature
                            {
                                Placement       = placement,
                                PathRender      = textPathRender,
                                PathUpRender    = textPathUps,
                                LineAnchors     = anchors,
                                Text            = text,
                                TextSizePx      = textSize,
                                PaddingPx       = padding,
                                SortKey         = sortKey,
                                SpacingPx       = spacing,
                                MaxAngleDeg     = maxAngle,
                                KeepUpright     = layout.TextKeepUpright,
                                AllowOverlap    = layout.TextAllowOverlap,
                                IgnorePlacement = layout.TextIgnorePlacement,
                                FeatureIndex    = ordinal++,
                                TileKey         = tileKey,
                                Paint           = symbolPaint,
                                TranslatePx     = translatePx,
                                TranslateAnchor = paint.TranslateAnchor,
                                PitchAlignment  = textPitch, // RESOLVED — selects the world-metre arc walk
                            });
                        }

                        // A MAP-resolved line icon is a ONE-GLYPH CURVED symbol, riding the SAME anchors
                        // the curved text above uses. Its own ProjectPath call (rather than sharing the text
                        // branch's array) keeps each symbol the sole owner of its path; the only cost is a
                        // second projection on a layer carrying map-aligned text AND a map-aligned icon,
                        // which no shipped style does.
                        if (iconAlongLine && hasIcon)
                        {
                            double3[] iconPathRender = ProjectPath(densePath, tileAddress, extent, projection, out double3[] iconPathUps);
                            EmitAlongLineIcon(placement, iconPathRender, iconPathUps,
                                anchors, in alongLineIconCtx, tileKey, ref ordinal, output);
                        }

                        // Upright-at-anchors — text and/or icon emitted as ordinary POINT symbols at each
                        // along-line anchor (the road-shield look). Suppress whichever side didn't resolve to
                        // viewport (Map-aligned text/icon on the SAME feature keeps its OWN emit path/fence).
                        if (textAtAnchors || iconAtAnchors)
                        {
                            // Built here, not hoisted: the side that did NOT resolve to viewport is
                            // suppressed by the map-aligned fence.
                            var anchorCtx = new AnchorEmitContext
                            {
                                Text = textAtAnchors ? text : null,
                                TextSizePx = textSize,
                                PaddingPx = padding,
                                LayoutOptions = layoutOptions,
                                Paint = symbolPaint,
                                TextAllowOverlap = layout.TextAllowOverlap,
                                TextIgnorePlacement = layout.TextIgnorePlacement,
                                TextRotationAlignment = layout.TextRotationAlignment,
                                HasIcon = iconAtAnchors && hasIcon,
                                IconQuad = iconQuad,
                                IconSkirtPx = iconSkirtPx,
                                IconImage = iconImage,
                                IconPaddingPx = iconPadding,
                                IconPaint = iconPaint,
                                IconAllowOverlap = layout.IconAllowOverlap,
                                IconIgnorePlacement = layout.IconIgnorePlacement,
                                IconRotationAlignment = layout.IconRotationAlignment,
                                IconRotateRadians = iconRotateRadians,
                                IconOptional = layout.IconOptional,
                                TextOptional = layout.TextOptional,
                                SortKey = sortKey,
                                TranslatePx = translatePx,
                                TranslateAnchor = paint.TranslateAnchor,
                                // `pairedInstance` is computed from the UN-suppressed text/hasIcon, so on this
                                // branch it must be re-gated on BOTH fences that can suppress a half. Icon
                                // side: `hasIcon` can be true with `iconAtAnchors` false (the along-line
                                // case), which would stamp a viewport-aligned text Rider against a PairId no
                                // emitted symbol owns. Text side: a map-aligned text leaves `Text`
                                // null here, which would stamp the icon Owner with no Rider ever following.
                                // With both conjuncts EmitAtAnchor's "PairedInstance implies both halves" is
                                // true by construction rather than by convention. Byte-identical wherever
                                // both sides resolve to viewport — which is every shipped shield layer.
                                PairedInstance = pairedInstance && iconAtAnchors && textAtAnchors,
                            };
                            for (int a = 0; a < anchors.Length; a++)
                            {
                                LineAnchor lineAnchor = anchors[a];
                                double2 tp = math.lerp(densePath[lineAnchor.Segment], densePath[lineAnchor.Segment + 1], lineAnchor.T);
                                EmitAtAnchor(tp, tileAddress, extent, projection, in anchorCtx, tileKey, ref ordinal, output);
                            }
                        }
                    }
                }
                else
                {
                    // Built ONCE per feature, read once per anchor by EmitAtAnchor. Built inside this
                    // branch because only the point branch reads it.
                    var ctx = new AnchorEmitContext
                    {
                        Text = text,
                        TextSizePx = textSize,
                        PaddingPx = padding,
                        LayoutOptions = layoutOptions,
                        Paint = symbolPaint,
                        TextAllowOverlap = layout.TextAllowOverlap,
                        TextIgnorePlacement = layout.TextIgnorePlacement,
                        TextRotationAlignment = layout.TextRotationAlignment,
                        HasIcon = hasIcon,
                        IconQuad = iconQuad,
                        IconSkirtPx = iconSkirtPx,
                        IconImage = iconImage,
                        IconPaddingPx = iconPadding,
                        IconPaint = iconPaint,
                        IconAllowOverlap = layout.IconAllowOverlap,
                        IconIgnorePlacement = layout.IconIgnorePlacement,
                        IconRotationAlignment = layout.IconRotationAlignment,
                        IconRotateRadians = iconRotateRadians,
                        IconOptional = layout.IconOptional,
                        TextOptional = layout.TextOptional,
                        SortKey = sortKey,
                        TranslatePx = translatePx,
                        TranslateAnchor = paint.TranslateAnchor,
                        PairedInstance = pairedInstance,
                    };
                    for (int p = 0; p < pathCount; p++)
                    {
                        IReadOnlyList<double2> path = CopyRing(geometry, ringOrder[ringStart[f] + p]);
                        // A Point feature anchors at every vertex; a LineString
                        // feature under POINT placement anchors ONCE, at the path's mid arc-length — reusing
                        // LineAnchorPlacement.Compute(_, _, LineCenter), the same "middle of this tile-space
                        // path" topology the LINE branch above already computes. Anchors are resolved on the
                        // BUFFERED DECODED path (unclipped — the same input the curved branch uses); the
                        // existing [0, extent) single-world clip is then applied to the RESOLVED anchor point,
                        // unchanged (docs/road-shields-design.md — the clip contract).
                        IReadOnlyList<double2> anchorPoints;
                        if (feature.GeometryType == TileGeometryType.LineString)
                        {
                            LineAnchor[] midArc = LineAnchorPlacement.Compute(path, 0.0, SymbolPlacement.LineCenter);
                            if (midArc.Length == 0) continue; // degenerate (< 2 points / zero-length) — no anchor
                            LineAnchor a = midArc[0];
                            anchorPoints = new[] { math.lerp(path[a.Segment], path[a.Segment + 1], a.T) };
                        }
                        else
                        {
                            anchorPoints = path;
                        }

                        for (int i = 0; i < anchorPoints.Count; i++)
                            EmitAtAnchor(anchorPoints[i], tileAddress, extent, projection, in ctx, tileKey, ref ordinal, output);
                    }
                }
            }

            // The buffer read in the loop above is BORROWED from the decoded tile (the layer owns it and frees
            // it when its decode scope closes), so it is NEVER disposed here — only this method's own native
            // counting-sort scratch is (via the `using var` declarations above), never geometry (that would be
            // a double free; pinned by the SymbolExtractorStructureTests borrow tooth).
        }

        /// <summary>Copies ring <paramref name="r"/>'s tile-local span out of the shared buffer into a managed
        /// array — the same shape (and the same allocation count) the retired managed decoder produced per
        /// path, so every downstream consumer (<see cref="LineCurvatureSubdivision.Subdivide"/> /
        /// <see cref="LineAnchorPlacement.Compute"/> / <see cref="KeepAnchorsInsideTile"/> /
        /// <see cref="ProjectPath"/>) is byte-for-byte unchanged. TILE-LOCAL, never geodetic, never projected
        /// (THE NAMED FENCE).
        /// <para><paramref name="geometry"/> is passed BY VALUE, not <c>in</c>:
        /// <see cref="TileGeometryBuffers"/> is not a <c>readonly struct</c>, so <c>in</c> would force a
        /// defensive copy per field read (docs/conventions-short.md's <c>in</c> ⟺ <c>readonly struct</c>
        /// gate).</para></summary>
        private static double2[] CopyRing(TileGeometryBuffers geometry, int r)
        {
            int start = geometry.RingOffsets[r];
            int n     = geometry.RingOffsets[r + 1] - start;
            var ring  = new double2[n];
            for (int k = 0; k < n; k++) ring[k] = geometry.Vertices[start + k];
            return ring;
        }

        /// <summary>The per-FEATURE values every anchor of that feature stamps onto its
        /// symbols — evaluated once in <see cref="Extract"/>'s feature loop, read once per anchor by
        /// <see cref="EmitAtAnchor"/>. readonly struct + <c>in</c> per docs/conventions-short.md (bigger than
        /// ~16 bytes, read-only at the call site).</summary>
        private readonly struct AnchorEmitContext
        {
            // text side (Text == null ⇒ emit no text symbol)
            public string             Text { get; init; }
            public float              TextSizePx { get; init; }
            public float              PaddingPx { get; init; }
            public TextLayoutOptions  LayoutOptions { get; init; }
            public SymbolPaint         Paint { get; init; }
            public bool               TextAllowOverlap { get; init; }
            public bool               TextIgnorePlacement { get; init; }
            public AlignmentMode      TextRotationAlignment { get; init; }
            // icon side (HasIcon == false ⇒ emit no icon symbol)
            public bool               HasIcon { get; init; }
            public SymbolQuad         IconQuad { get; init; }
            /// <summary>Baked-px transparent border inside <see cref="IconQuad"/>, per side.</summary>
            public float              IconSkirtPx { get; init; }
            public string             IconImage { get; init; }
            public float              IconPaddingPx { get; init; }
            public SymbolPaint         IconPaint { get; init; }
            public bool               IconAllowOverlap { get; init; }
            public bool               IconIgnorePlacement { get; init; }
            public AlignmentMode      IconRotationAlignment { get; init; }
            /// <summary><c>icon-rotate</c> in radians — stamped on the ICON half only; text is never
            /// rotated by it.</summary>
            public float              IconRotateRadians { get; init; }
            /// <summary><c>icon-optional</c> — stamped on the ICON half's
            /// <see cref="SymbolFeature.PairOptional"/>: the icon is the droppable one, so its text partner can
            /// place without it.</summary>
            public bool               IconOptional { get; init; }
            /// <summary><c>text-optional</c> — stamped on the TEXT half's
            /// <see cref="SymbolFeature.PairOptional"/>: the text is the droppable one, so its icon partner can
            /// place without it.</summary>
            public bool               TextOptional { get; init; }
            // shared
            public float              SortKey { get; init; }
            public float2             TranslatePx { get; init; }
            public TextTranslateAnchor TranslateAnchor { get; init; }
            /// <summary>True when this feature resolved BOTH a text and an icon and both are
            /// emitted at this anchor — the two halves are ONE placement instance, centred or not. The icon
            /// is stamped <see cref="SymbolPairRole.Owner"/> (emitted first) and the text
            /// <see cref="SymbolPairRole.Rider"/>, sharing a <c>PairId</c>; whether the proposed pair actually
            /// holds is decided downstream by <see cref="Placement.SymbolPairing"/>. Otherwise the pre-pairing
            /// order (text then icon) is unchanged and both halves carry
            /// <see cref="SymbolPairRole.None"/>.</summary>
            public bool               PairedInstance { get; init; }
        }

        /// <summary>The per-FEATURE icon values every along-line icon symbol of that feature stamps —
        /// evaluated once in <see cref="Extract"/>'s feature loop, read once per decoded path by
        /// <see cref="EmitAlongLineIcon"/>. The icon-side analogue of <see cref="AnchorEmitContext"/>, kept
        /// separate because the two describe different emit SHAPES: that one carries a point block's
        /// text+icon halves, this one a single curved glyph. readonly struct + <c>in</c> per
        /// docs/conventions-short.md.</summary>
        private readonly struct AlongLineIconContext
        {
            public SymbolQuad    IconQuad { get; init; }
            /// <summary>Baked-px transparent border inside <see cref="IconQuad"/>, per side.</summary>
            public float         IconSkirtPx { get; init; }
            public string        IconImage { get; init; }
            public float         PaddingPx { get; init; }
            public SymbolPaint    Paint { get; init; }
            public bool          AllowOverlap { get; init; }
            public bool          IgnorePlacement { get; init; }
            public AlignmentMode RotationAlignment { get; init; }
            /// <summary>The RESOLVED <c>icon-pitch-alignment</c>, unlike
            /// <see cref="RotationAlignment"/> beside it (recorded as authored). Consumed: it selects the
            /// world-metre arc walk in the curved staging arm.</summary>
            public AlignmentMode PitchAlignment { get; init; }
            public float         SortKey { get; init; }
            public float         SpacingPx { get; init; }
            public float         MaxAngleDeg { get; init; }
            /// <summary><c>icon-rotate</c> in radians, composed on top of the along-line tangent.</summary>
            public float         IconRotateRadians { get; init; }
        }

        /// <summary>The along-line twin of <see cref="EmitAtAnchor"/>'s single-world <c>[0, extent)</c>
        /// clip — returns the anchors of <paramref name="anchors"/> whose resolved tile-space point
        /// (<c>lerp(tilePath[Segment], tilePath[Segment+1], T)</c>, the same expression every consumer resolves
        /// an anchor with) lies inside this tile, in the original along-line arc order.
        ///
        /// <para>The bound is HALF-OPEN because tile coordinates are per-tile: world position
        /// <c>x == extent</c> in tile T is <c>x == 0</c> in tile T+1. An inclusive upper bound duplicates every
        /// anchor sitting on a shared edge and an exclusive lower bound orphans it; only <c>[0, extent)</c>
        /// makes adjacent tiles' anchor sets a true PARTITION of world space — exactly one owner per position,
        /// no gaps. That is why this is a hard 0 and deliberately not
        /// <c>MapViewConfig.FillTileBufferClip</c>: a buffer is a MARGIN for geometry (a wider polygon, a
        /// cosmetic cost), whereas anchor assignment is an OWNERSHIP partition, and a non-zero margin on a
        /// partition means two tiles both emit the strip — the doubled-arrow defect this closes.</para>
        ///
        /// <para>It filters ANCHORS, never the path: the path stays whole because clipping a polyline turns a
        /// join into a cap, and the per-frame arc walk needs the vertices beyond the surviving anchors.</para></summary>
        private static LineAnchor[] KeepAnchorsInsideTile(
            LineAnchor[] anchors, IReadOnlyList<double2> tilePath, double extent)
        {
            int kept = 0;
            for (int i = 0; i < anchors.Length; i++)
                if (IsAnchorInsideTile(anchors[i], tilePath, extent)) kept++;

            // The overwhelmingly common case: hand the input array straight back, so "unchanged" is literally
            // unchanged (zero alloc, same instance).
            if (kept == anchors.Length) return anchors;
            if (kept == 0) return System.Array.Empty<LineAnchor>();

            var inside = new LineAnchor[kept];
            int w = 0;
            for (int i = 0; i < anchors.Length; i++)
                if (IsAnchorInsideTile(anchors[i], tilePath, extent)) inside[w++] = anchors[i];
            return inside;
        }

        // The exact complement of EmitAtAnchor's early-return test — the same four comparisons in the same
        // order, so the line branch's clip and the point branch's cannot drift into an overlap.
        private static bool IsAnchorInsideTile(
            LineAnchor anchor, IReadOnlyList<double2> tilePath, double extent)
        {
            double2 p = math.lerp(tilePath[anchor.Segment], tilePath[anchor.Segment + 1], anchor.T);
            return p.x >= 0.0 && p.x < extent && p.y >= 0.0 && p.y < extent;
        }

        /// <summary>Emits ONE along-line icon symbol for a decoded path — a curved symbol whose single
        /// glyph is the icon quad, riding <paramref name="anchors"/> (the SAME array the curved-text branch
        /// walks) and rotated per-frame to the projected line tangent.
        ///
        /// <para><c>KeepUpright</c> is hard-false, not threaded: <c>icon-keep-upright</c>'s spec default is
        /// <c>false</c> (unlike <c>text-keep-upright</c>), and for a one-way arrow that default is the only
        /// correct behaviour — the arrow encodes the road's direction of travel, so flipping it to read
        /// "upright" would point it the wrong way. See docs/labels-and-symbols-design.md.</para>
        ///
        /// <para>Never paired: a pair is proposed only in <see cref="EmitAtAnchor"/>, on the point path, so
        /// this symbol's <c>PairRole</c> stays <see cref="SymbolPairRole.None"/> structurally — the "a curved
        /// symbol is never paired" fence holds with no guard here.</para></summary>
        private static void EmitAlongLineIcon(
            SymbolPlacement placement, double3[] pathRender, double3[] pathUpRender, LineAnchor[] anchors,
            in AlongLineIconContext ctx, long tileKey, ref int ordinal, List<SymbolFeature> output)
        {
            output.Add(new SymbolFeature
            {
                Placement         = placement,
                Kind              = SymbolKind.Icon,
                PathRender        = pathRender,
                PathUpRender      = pathUpRender,
                LineAnchors       = anchors,
                IconQuad          = ctx.IconQuad,
                IconSkirtPx       = ctx.IconSkirtPx,
                IconImage         = ctx.IconImage,
                PaddingPx         = ctx.PaddingPx,
                SortKey           = ctx.SortKey,
                SpacingPx         = ctx.SpacingPx,
                // Structurally inert at one glyph (the max-angle gate is `g > 0`-guarded), carried rather
                // than replaced by a sentinel so the field means the same thing on every curved symbol.
                MaxAngleDeg       = ctx.MaxAngleDeg,
                KeepUpright       = false,
                AllowOverlap      = ctx.AllowOverlap,
                IgnorePlacement   = ctx.IgnorePlacement,
                // Recorded for record fidelity, NOT consumed: the curved path takes its orientation from the
                // line tangent, so — exactly like the curved-text emit above — the builder does not forward
                // this onto the record. It says what the style asked for, nothing downstream reads it.
                RotationAlignment = ctx.RotationAlignment,
                // The pitch twin IS forwarded and IS read — it selects StageCurved's world arc walk.
                PitchAlignment    = ctx.PitchAlignment,
                IconRotateRadians = ctx.IconRotateRadians,
                Paint             = ctx.Paint,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
            });
        }

        /// <summary>Resolves one tile-space anchor point to a symbol anchor and emits its text/icon
        /// symbols per <paramref name="ctx"/> — the single emit site shared by point-placement anchors AND
        /// line-placement upright-at-anchor symbols. Applies the single-world <c>[0, extent)</c> clip —
        /// an anchor outside the tile is a source world-copy/buffer duplicate, dropped so the owning tile
        /// emits it exactly once.</summary>
        private static void EmitAtAnchor(
            double2 tilePoint, TileId tileId, double extent, IProjection projection,
            in AnchorEmitContext ctx, long tileKey, ref int ordinal, List<SymbolFeature> output)
        {
            if (tilePoint.x < 0.0 || tilePoint.x >= extent || tilePoint.y < 0.0 || tilePoint.y >= extent) return;
            double2 lonLat = tileId.ToLonLat(tilePoint.x, tilePoint.y, extent);
            ProjectedPoint pp = projection.ProjectPoint(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            double3 anchor = pp.World;

            if (ctx.PairedInstance)
            {
                // The pair's PairId is the OWNER's (icon's) FeatureIndex, captured before either
                // emitter advances `ordinal`. PairedInstance implies both HasIcon and Text != null — every
                // caller re-gates the predicate on the same fences that suppress a half (the line branch on
                // `iconAtAnchors && textAtAnchors`) — so both emitters below always run together here.
                int pairId = ordinal;
                if (ctx.HasIcon) EmitIcon(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.Owner, pairId);
                if (ctx.Text != null) EmitText(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.Rider, pairId);
            }
            else
            {
                if (ctx.Text != null) EmitText(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.None, 0);
                if (ctx.HasIcon) EmitIcon(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, SymbolPairRole.None, 0);
            }
        }

        private static void EmitText(
            double3 anchor, double3 up, long tileKey, ref int ordinal, List<SymbolFeature> output, in AnchorEmitContext ctx,
            SymbolPairRole pairRole, int pairId)
        {
            output.Add(new SymbolFeature
            {
                AnchorRender      = anchor,
                UpRender          = up,
                Placement         = SymbolPlacement.Point,
                Text              = ctx.Text,
                TextSizePx        = ctx.TextSizePx,
                PaddingPx         = ctx.PaddingPx,
                SortKey           = ctx.SortKey,
                AllowOverlap      = ctx.TextAllowOverlap,
                IgnorePlacement   = ctx.TextIgnorePlacement,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
                Paint             = ctx.Paint,
                LayoutOptions     = ctx.LayoutOptions,
                TranslatePx       = ctx.TranslatePx,
                TranslateAnchor   = ctx.TranslateAnchor,
                RotationAlignment = ctx.TextRotationAlignment,
                PairRole          = pairRole,
                PairId            = pairId,
                PairOptional      = ctx.TextOptional,
            });
        }

        private static void EmitIcon(
            double3 anchor, double3 up, long tileKey, ref int ordinal, List<SymbolFeature> output, in AnchorEmitContext ctx,
            SymbolPairRole pairRole, int pairId)
        {
            output.Add(new SymbolFeature
            {
                AnchorRender      = anchor,
                UpRender          = up,
                Placement         = SymbolPlacement.Point,
                Kind              = SymbolKind.Icon,
                IconQuad          = ctx.IconQuad,
                IconSkirtPx       = ctx.IconSkirtPx,
                IconImage         = ctx.IconImage,
                PaddingPx         = ctx.IconPaddingPx,
                SortKey           = ctx.SortKey,
                AllowOverlap      = ctx.IconAllowOverlap,
                IgnorePlacement   = ctx.IconIgnorePlacement,
                RotationAlignment = ctx.IconRotationAlignment,
                IconRotateRadians = ctx.IconRotateRadians,
                Paint             = ctx.IconPaint,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
                PairRole          = pairRole,
                PairId            = pairId,
                PairOptional      = ctx.IconOptional,
            });
        }

        // Project a tile-local line string to render-space (PRE-RTC) vertices, plus the index-parallel
        // unit surface normal at each vertex.
        private static double3[] ProjectPath(
            IReadOnlyList<double2> path, TileId tileId, double extent, IProjection projection, out double3[] ups)
        {
            var pts = new double3[path.Count];
            ups = new double3[path.Count];
            for (int i = 0; i < path.Count; i++)
            {
                double2 lonLat = tileId.ToLonLat(path[i].x, path[i].y, extent);
                ProjectedPoint pp = projection.ProjectPoint(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                pts[i] = pp.World;
                ups[i] = pp.Up;
            }

            return pts;
        }

        private static SymbolPaint EvaluatePaint(PaintProperties paint, double zoom, IFeature feature)
        {
            return new SymbolPaint
            {
                TextColor   = StreamRgba(paint.Color, zoom, feature),
                Opacity     = paint.Opacity.Evaluate(zoom, feature),
                HaloColor   = StreamRgba(paint.HaloColor, zoom, feature),
                HaloWidthPx = paint.HaloWidth.Evaluate(zoom, feature),
                HaloBlurPx  = paint.HaloBlur.Evaluate(zoom, feature),
            };
        }

        /// <summary>Evaluates one colour for the vertex COLOR stream — the complement of
        /// <c>SymbolRenderLayer.BindColorTint</c>: a CONSTANT rides the uniform, so the stream stays white
        /// and the shader's uniform x vertex product is the colour ONCE. Alpha is untouched either way, it
        /// has no uniform carrier.</summary>
        private static float4 StreamRgba(StyleProperty<Color> color, double zoom, IFeature feature)
        {
            float4 rgba = ToFloat4(color.Evaluate(zoom, feature));
            return SymbolTextColorCarrier.RidesUniform(color) ? new float4(1f, 1f, 1f, rgba.w) : rgba;
        }

        private static float4 ToFloat4(in Color c)
        {
            return new float4((float)c.R, (float)c.G, (float)c.B, (float)c.A);
        }
    }
}