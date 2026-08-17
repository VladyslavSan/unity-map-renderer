// No `using UnityEngine` — but NOT engine-free any more, and NOT a core-tests file. It consumes
// Unity.Collections transitively through TileGeometryBuffers (the Waist-1 buffer it materializes and reads),
// so it cannot compile in Tools/core-tests and must not be re-added to core-tests.csproj. That dependency is
// why it lives in MapRenderer.Unity at all (tile-geometry IR B4): MapRenderer.Core references only
// Unity.Mathematics + UniTask, and adding Unity.Collections there would erase the Core/Jobs split
// (Core keeps the managed evaluation surface; Jobs owns the blittable geometry).
//
// Mirrors StyledLineTileBuilder's select -> materialize -> project pattern, but emits pre-shaping
// SymbolLabels instead of a Mesh.

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S105 Slice 2 (A3) — extracts <see cref="SymbolLabel"/>s from a decoded MVT tile for one symbol style
    /// layer. Reuses the existing seams: <see cref="FeatureSelector.SelectFeatures"/> (source-layer resolve
    /// + filter), the Waist-1 tile-geometry buffer (below), and
    /// <see cref="TileId.ToLonLat"/> → <see cref="IProjection.Project"/> (tile → geo → render space, PRE-RTC).
    /// Point AND LineString geometry (road-shields D2/D4: a LineString anchors at its mid arc-length under
    /// point placement, and at along-line anchors under a viewport-resolved line placement — see
    /// <see cref="AlignmentResolution"/>); Polygon is never accepted. One label per anchor (a MultiPoint
    /// feature emits one label per point; a viewport-resolved line emits one label per along-line anchor).
    /// A MAP-resolved line ICON (P-B) instead emits ONE curved label per path, whose single glyph is the icon
    /// quad — the anchors ride inside it rather than each becoming their own label.
    /// Clean-room — the shaping step is Unity-side (Slice 3).
    ///
    /// <para><b>Geometry (tile-geometry IR B4; IR C1 P2/P3).</b> Instead of decoding each feature's command
    /// stream for itself, this reads each feature's path spans out of the source-layer's tile-local
    /// <see cref="TileGeometryBuffers"/> (Waist 1). Since P3 that buffer is <see cref="ITileLayer.Geometry"/>
    /// — the layer's own, minted once inside the decode — and it is <b>BORROWED</b>: the decoded tile owns
    /// it and frees it when its decode scope closes, so this method must never dispose it. (The buffer is
    /// array-backed, so a second free here would be a loud double free, not a quiet leak.) Because the buffer
    /// spans the WHOLE layer, per-feature side data is keyed on <see cref="SelectedTileFeature.Ordinal"/>,
    /// which is what <c>RingFeatureIdx</c> names.</para>
    ///
    /// <para><b>Landmine #5 — the fused-<c>RingAssemblyJob</c> fence.</b> Symbol has no polygon, hole, area or
    /// triangulation concept and rejects Polygon features outright, so it must never call
    /// <c>FillMeshPipeline.Schedule</c>, never schedule or consume <c>RingAssemblyJob</c>/<c>RingClipJob</c>/
    /// <c>EarcutJob</c>/<c>GlobeFillSubdivideJob</c>, and never apply an area/shoelace test to a symbol path —
    /// a Point feature's 1-point path has no area at all and a straight road has exactly zero.</para>
    ///
    /// <para><b>Landmine #2 — symbol is the consumer that finally OBSERVES the unfiltered buffer.</b> The
    /// point branch below has <b>no path-length filter at all</b>: a 1-point path is a real, rendered label.
    /// The line branch filters <c>&lt; 2</c> and fill filters <c>&lt; 3</c> — three consumers, three
    /// thresholds, one unfiltered buffer. A short-ring filter in the Waist-1 materializer, in
    /// <c>MvtDecodeJob</c> or in any shared stage would delete every Point-feature label here, which is why
    /// no such filter may ever be fused upstream.</para>
    /// </summary>
    public static class SymbolFeatureExtractor
    {
        /// <summary>
        /// Append every extracted label of <paramref name="layer"/> over <paramref name="tile"/> to
        /// <paramref name="output"/>. Features whose <c>text-field</c> resolves to null/empty AND whose
        /// <c>icon-image</c> resolves to nothing are skipped; Polygon features are always ignored (road-shields
        /// D2's fence — LineString is now accepted, see the class doc). <paramref name="output"/> is
        /// caller-owned (cleared? no — appended, mirroring the tile-accumulation lifecycle in F5).
        /// </summary>
        /// <param name="layer">The symbol style layer (a non-symbol layer is a no-op).</param>
        /// <param name="tile">The decoded tile.</param>
        /// <param name="tileId">
        /// <b>IGNORED since the IR C1 fix stage — do not add a reader.</b> The address this method projects
        /// through and packs into the <c>TileKey</c> is the resolved layer's own
        /// <c>ITileLayer.Geometry.Tile</c>, stamped at the decode, exactly as the mesh consumers read it
        /// (<c>StyledLineTileBuilder</c>/<c>StyledFillTileBuilder</c>, whose <c>WriteInto</c> dropped its
        /// <c>TileId</c> parameter in P2/P3). A caller-supplied address is the second copy C1 exists to
        /// remove: <see cref="ITileDecoder.Decode"/>'s doc states the address "enters the pipeline exactly
        /// ONCE, at the fetch", and this seam was the last place that was false. The parameter survives only
        /// because removing it touches ~50 call sites and would delete the mechanism its tooth uses
        /// (<c>SymbolBufferAddressTests</c> corrupts it and requires the output not to move); it is slated
        /// for deletion — see the design doc's recorded leftovers.
        /// </param>
        /// <param name="zoom">Current zoom, for evaluating zoom-dependent text-size/sort-key/paint AND the
        /// build-zoom-evaluated <c>symbol-placement</c> (road-shields D1 — frozen for this tile's lifetime,
        /// never re-evaluated per frame).</param>
        /// <param name="projection">Geo → render-space projection.</param>
        /// <param name="output">Caller-owned list the extracted labels are appended to.</param>
        /// <param name="spriteAtlas">
        /// I3 — the sprite sheet <c>icon-image</c> resolves against; <c>null</c> (the default) yields NO icon
        /// labels regardless of the layer's <c>icon-*</c> properties, so every pre-I3 caller (which omits this
        /// argument) is byte-identical to before I3. Icons resolve at EVERY placement now: point, line with
        /// <c>icon-rotation-alignment</c> resolving to <c>viewport</c> (road-shields D4 — the upright-at-anchor
        /// case), and line with it resolving to <c>map</c> (P-B — the along-line case, emitted as a one-glyph
        /// curved label; <c>road_one_way_arrow*</c>). D4's map-aligned fence is LIFTED, not surviving.
        /// </param>
        public static void Extract(
            MapRenderer.Core.Style.StyleLayer layer,
            IDecodedTile                      tile,
            TileId                            tileId,
            double                            zoom,
            IProjection                       projection,
            List<SymbolLabel>                 output,
            SpriteAtlasView                   spriteAtlas = null)
        {
            if (!(layer is MapRenderer.Core.Style.Symbol.StyleLayer symbolLayer) || tile == null || projection == null || output == null)
                return;

            // NOTE: layer minzoom/maxzoom is deliberately NOT gated here. Tile DATA tops out at a max source zoom
            // (e.g. z14 for OpenFreeMap), so at higher camera zooms those tiles are OVERZOOMED (reused, not
            // rebuilt) — gating at build time would freeze layer visibility at the build zoom and hide layers
            // (poi_r1/r7/r20 @ minzoom 15/16/17) that MapLibre reveals as you zoom past the data level. The gate
            // therefore lives at DISPLAY time against the LIVE camera zoom (see StyleLayer.IsVisibleAtZoom, applied
            // per-frame in LabelPlacementSystem) so overzoomed data still turns layers on/off correctly.

            // Fully qualified rather than a `using MapRenderer.Core.Style;`: that namespace also holds the
            // BASE StyleLayer, which a using would make ambiguous with the symbol StyleLayer below.
            ITileLayer tileLayer = MapRenderer.Jobs.Tiles.SourceLayerResolver.ResolveTileLayer(layer, tile);
            if (tileLayer == null) return;

            // IR C1 fix stage: the BUFFER is the sole authority for the address and the extent it was
            // quantized against — read together, off one object, so no pair of them can drift. Both used to
            // come from elsewhere (the `tileId` parameter and `tileLayer.Extent`), which is the second-copy
            // shape P2/P3 removed from the mesh consumers and left standing here alone.
            //
            // Nothing created ⇒ no rings ⇒ no label is reachable (every emit below walks the bucketed ring
            // order), so this returns the same empty result the loop would — and it is what makes reading
            // Tile/Extent off the buffer safe, since `default` carries neither. Mirrors line's own guard,
            // StyledLineTileBuilder.WriteInto's `!geometry.IsCreated` early return.
            TileGeometryBuffers geometry = tileLayer.Geometry;
            if (!geometry.IsCreated) return;
            TileId tileAddress = geometry.Tile;
            double extent      = geometry.Extent;

            // IR C1 P2: the ORDINAL-returning selection overload, taking the already-resolved layer. The
            // ordinals are what the shared buffer's RingFeatureIdx names; resolving the layer a second time
            // inside the selector would be a second chance to resolve it differently. Selection order is
            // unchanged — the selector appends in Features order, so selected order IS decode order.
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(layer, tileLayer, zoom, selected);
            // Nothing selected ⇒ no label can be emitted. Since IR C1 P3 the decode already materialized
            // every layer, so this no longer avoids any work upstream — it is the plain early-out it reads
            // as, mirroring TileMeshLayerProcessor's `selected.Count > 0`.
            if (selected.Count == 0) return;
            long                        tileKey  = LabelTileKey.Pack(tileAddress);
            int                         ordinal  = 0;

            LayoutProperties layout = symbolLayer.Layout;
            PaintProperties  paint  = symbolLayer.Paint;

            // text-translate is a constant px offset (not feature-dependent) — stamp it onto every label.
            // Stored y-down (as authored); the y-flip happens at placement.
            float2 translatePx = paint.Translate;

            // D1 (road-shields): symbol-placement is now expression-capable but evaluated ONCE here, at the
            // tile's build zoom — never per frame, never re-evaluated as the camera crosses a step boundary
            // (the accepted D1 known limit). TryEvaluate degrades to Point on a malformed/data-driven
            // expression rather than throwing (symbol-placement is never data-driven in a real style).
            SymbolPlacement placement = layout.SymbolPlacement.TryEvaluate(zoom, null, out SymbolPlacement evaluatedPlacement)
                ? evaluatedPlacement : SymbolPlacement.Point;
            bool isLine = placement != SymbolPlacement.Point;

            // D3/D4 (road-shields): resolve rotation-alignment against placement ONCE per layer (both are
            // plain parsed enums, not feature-dependent — MapLibre's alignment keys are never data-driven).
            // D4's reframe of G3/G4: under LINE placement, a label whose alignment resolves AWAY from Map is
            // NOT curved — MapLibre lays it out as an ordinary upright (viewport) block at each along-line
            // anchor, exactly the road-shield look. Map-aligned line labels (the pre-shields behaviour) are
            // untouched — this only lifts the icon fence / switches emit shape for the viewport-resolved case.
            AlignmentMode textAlign    = AlignmentResolution.Resolve(layout.TextRotationAlignment, placement);
            AlignmentMode iconAlign    = AlignmentResolution.Resolve(layout.IconRotationAlignment, placement);
            // W1: the PITCH twins, resolved on the SAME once-per-layer terms (the spec's pitch `auto` defers
            // to the RESOLVED rotation alignment, which is what ResolvePitch encodes). Unlike the rotation
            // values above — recorded as authored and re-resolved downstream — these are stamped RESOLVED
            // onto the emitted label, because the curved staging arm consumes them and has no placement in
            // hand to resolve `auto` against.
            AlignmentMode textPitch    = AlignmentResolution.ResolvePitch(
                layout.TextPitchAlignment, layout.TextRotationAlignment, placement);
            AlignmentMode iconPitch    = AlignmentResolution.ResolvePitch(
                layout.IconPitchAlignment, layout.IconRotationAlignment, placement);
            bool          textAtAnchors = isLine && textAlign != AlignmentMode.Map;
            bool          iconAtAnchors = isLine && iconAlign != AlignmentMode.Map;
            // P-B: the third icon mode. A MAP-resolved line icon rides the along-line anchors AND rotates to
            // the local line tangent — emitted as a one-glyph curved label (see EmitAlongLineIcon).
            bool          iconAlongLine = isLine && iconAlign == AlignmentMode.Map;

            // Waist 1 (IR C1 P2/P3): the WHOLE source layer, decoded ONCE into the layer's own tile-local
            // buffer and BORROWED from it. `RingFeatureIdx[r]` therefore indexes `tileLayer.Features` — the
            // layer's own ordinal — not this layer's selected list, which is why every per-feature array below
            // is sized to the LAYER and addressed by `SelectedTileFeature.Ordinal`. An array sized to the
            // selected count would silently mis-bucket (and, whenever the highest selected ordinal exceeds
            // that count, index out of range).
            //
            // IR C1 fix stage: sized from `geometry.FeatureCount`, as LINE already sizes its three columns
            // (StyledLineTileBuilder). `RingFeatureIdx`'s values ARE indices into the buffer's own feature
            // column, so that column's length is the domain being bucketed; `Features.Count` is a different
            // list that merely happens to hold the same count. For MVT it always does (OrdinalDomainTests
            // clause B pins the lockstep); the two consumers now agree on one source of truth rather than on
            // an invariant only one writer maintains.
            int layerFeatureCount = geometry.FeatureCount;

            // RingFeatureIdx joins each ring back to its feature's LAYER ORDINAL. Bucket ONCE, ascending in r,
            // so each feature's paths keep DECODE ORDER (landmine #4 — the extractor's per-tile `ordinal` is
            // the stable S20 FeatureIndex tiebreak, so path order is observable output, not an implementation
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
            // what keeps both the emitted label sequence and the `ordinal` counter below byte-identical to the
            // pre-P2 "iterate the selected list" loop.
            for (int si = 0; si < selected.Count; si++)
            {
                int      f       = selected[si].Ordinal;
                IFeature feature = selected[si].Feature;
                // D2 (road-shields): point placement now ALSO accepts a LineString feature (one anchor at
                // its mid arc-length, below) — the shields' "point" step branch runs over LineString road
                // geometry. Polygon stays unaccepted at every placement (the D2 fence).
                if (isLine
                        ? feature.GeometryType != TileGeometryType.LineString
                        : (feature.GeometryType != TileGeometryType.Point &&
                           feature.GeometryType != TileGeometryType.LineString))
                    continue;

                // A6: the feature IS an IFeature (the neutral carrier implements it directly) — no adapter alloc.
                // I3: text and icon are INDEPENDENT — a feature may resolve either, both, or neither. Only
                // when NEITHER resolves is the feature skipped (the pre-I3 "text==null -> skip" rule is the
                // isLine-or-null-atlas special case of this, so line/text-only-atlas behaviour is unchanged).
                string text = TextFieldResolver.Resolve(layout.TextField, feature);
                if (text != null)
                {
                    // text-transform (Slice B): case-fold the resolved label before it is shaped downstream.
                    text = layout.TextTransform.Apply(text);
                }

                // I3/D4/P-B: icons resolve at every placement — point, a viewport-resolved line
                // (iconAtAnchors) and, since P-B, a map-resolved line (iconAlongLine). Only resolved when the
                // caller supplied a sprite atlas — a null atlas (every pre-I3 caller) never produces icons.
                bool        hasIcon   = false;
                SpriteEntry iconEntry = default;
                // I6: hoisted to feature scope (was block-local + discarded) — the resolved sprite name is the
                // icon's cross-tile identity, needed at the icon-emit site below (SymbolLabel.IconImage).
                string iconImage = null;
                // P-B: D4's map-aligned fence (which this gate used to express as `!isLine || iconAtAnchors`)
                // is LIFTED. What replaced it is a third emit SHAPE, not a third fence: `iconAlongLine` routes
                // to EmitAlongLineIcon below, where the icon rides the same along-line anchors as the
                // viewport case but rotates to the projected tangent instead of staying screen-upright.
                if ((!isLine || iconAtAnchors || iconAlongLine) && spriteAtlas != null)
                {
                    iconImage = IconImageResolver.Resolve(layout.IconImage, feature);
                    if (iconImage != null)
                        hasIcon = spriteAtlas.Index.TryGetSprite(iconImage, out iconEntry);
                }

                if (text == null && !hasIcon) continue; // neither a text label nor an icon → nothing to emit

                // P-A (D-PA-1): the PAIRING predicate is "this feature resolved BOTH a text and an icon",
                // nothing more. The two halves are stamped ONE placement instance downstream (LabelPairing /
                // StagePointPair), not the old D5 icon-owns-collision approximation (the forced overlap flags
                // were D5's mechanism; D5 is retired — see §10). §10 D8/D9's five extra conjuncts (text/icon
                // anchor == Center, zero text/icon offset, zero radial offset) are RETIRED: MapLibre's model
                // is an INSTANCE of icon + text placed together, not two symbols that happen to coincide, so a
                // bottom-anchored city name is as much one instance with its dot as a shield's centred ref is
                // with its badge. That gap is what let a dot place while its own name was culled.
                //
                // Coincident boxes were never what made pairing work. Each half's anchor/offset is baked
                // ANCHOR-RELATIVE upstream — TextQuadLayout.Layout folds text-anchor/-offset/-radial-offset
                // into every quad before measuring TextLayoutResult.BoundsMin/Max, and IconQuadLayout.Layout
                // does the same for icon-anchor/-offset — so LabelBox.Build's `anchor + baked bounds` puts
                // each half exactly where the style asked, and LabelStagingMath.StagePointPair appending both
                // halves at the OWNER's ScreenPx stays correct with no per-half placement plumbing. A
                // non-centred pair's two boxes are simply DISJOINT; each is still collision-tested on its own
                // (the candidate reserves no union box spanning the gap between them).
                //
                // D-PA-3: icon-optional/text-optional deliberately still do NOT appear here, and D11 ("pair
                // only when both are false") stays refuted — but for a NEW reason. Stage C's argument was that
                // a CENTRED pair's boxes overlap by construction, so un-pairing makes the halves mutually
                // exclusive rather than independent; that does not transfer to disjoint boxes. The argument
                // that does is the INSTANCE one: `text-optional` means "this instance may render icon-only",
                // which presupposes the instance. liberty's `airport` sets it and nothing else — un-paired,
                // its halves would be collision-tested independently and the text could place with the icon
                // culled, the one outcome the flag forbids. Optionality remains a per-BOX verdict inside the
                // test-all-then-insert collision loop (LabelCandidate.OptionalBoxMask).
                bool pairedInstance = hasIcon && text != null;

                // Per-feature evaluated style (zoom + feature — safe for constant/zoom/data-driven).
                float      textSize   = layout.TextSize.Evaluate(zoom, feature);
                float      padding    = layout.TextPadding.Evaluate(zoom, feature);
                float      sortKey    = layout.SymbolSortKey.Evaluate(zoom, feature);
                float      spacing    = math.max(1f, layout.SymbolSpacing.Evaluate(zoom, feature)); // px, >= 1 (spec)
                float      maxAngle   = layout.TextMaxAngle.Evaluate(zoom, feature);                // degrees (#6)
                LabelPaint labelPaint = EvaluatePaint(paint, zoom, feature);

                // I3: the icon quad/paint are feature-constant (icon-size/-padding/-opacity don't vary per
                // point within a MultiPoint feature) — build once here, stamp onto every point label below.
                SymbolQuad iconQuad    = default;
                LabelPaint iconPaint   = default;
                float      iconPadding = 0f;
                float      iconRotateRadians = 0f;
                float      iconSkirtPx = 0f;
                if (hasIcon)
                {
                    float iconSize = layout.IconSize.Evaluate(zoom, feature);
                    iconPadding = layout.IconPadding.Evaluate(zoom, feature);
                    // P-B: degrees→radians ONCE, here — every downstream site reads radians. Build-zoom
                    // frozen for this tile's lifetime, the same accepted limit as every other icon property.
                    // Unit conversion only: the value keeps MapLibre's clockwise-positive SENSE all the way
                    // down, and enters the staging frame's opposite sense once, at
                    // LabelBearing.IconRotationRadians (which is below both icon emit shapes, so one flip
                    // covers the point path and the along-line path alike).
                    iconRotateRadians = math.radians(layout.IconRotate.Evaluate(zoom, feature));
                    float iconOpacity = paint.IconOpacity.Evaluate(zoom, feature);
                    iconQuad = IconQuadLayout.Layout(iconEntry, spriteAtlas.Size, iconSize, layout.IconAnchor,
                        layout.IconOffset);
                    // The border baked into iconQuad, carried alongside it: every consumer that needs the
                    // CONTENT box back (collision, placement) subtracts exactly this.
                    iconSkirtPx = IconQuadLayout.SkirtPx(iconEntry, iconSize);
                    iconPaint = new LabelPaint
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

                // D4/NIT: LayoutOptions is only built when a point-style text label can actually be emitted
                // (point placement, or a viewport-resolved line — the curved branch never uses it).
                // AnchorEmitContext itself is built INSIDE each branch below (review NIT 4) rather than once
                // here — the two branches' contexts differ in two fields (Text/HasIcon suppression under the
                // line branch's map-aligned fence) and building them separately removes both the line
                // branch's dead construction (a curved-only feature never reads a context at all) and the
                // silent-divergence hazard of two hand-maintained initializers that must agree.
                TextLayoutOptions layoutOptions = !isLine || textAtAnchors
                    ? TextLayoutOptionsBuilder.Build(layout, zoom, feature)
                    : default;

                if (isLine)
                {
                    // P-B: the along-line icon's per-FEATURE values, built ONCE and read once per path below.
                    // Built here (not hoisted above the branch — the NIT-4 precedent): only a map-resolved
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
                            PitchAlignment = iconPitch, // W1: RESOLVED, unlike RotationAlignment above
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
                        // A-2: anchors computed ONCE here in TILE space (zoom-invariant). symbol-spacing is px
                        // at the tile's on-screen size (512 logical px per tile at integer zoom), so px → tile
                        // units is `spacing · extent / TilePixelSize`. Projection-agnostic: uses only the layer
                        // extent + the 512 convention, no projection scale.
                        double spacingTileUnits = spacing * extent / WebMercator.TilePixelSize;

                        // S4: subdivide the tile-local path ONCE so ProjectPath/anchor-resolve and
                        // LineAnchorPlacement.Compute both index against the SAME finer sequence — never
                        // subdivide only one of the two, or LineAnchor.Segment silently desyncs from
                        // PathRender (docs/labels-and-symbols-design.md §4). On a flat projection
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

                        // D4/A-2: anchors computed once, shared by BOTH sub-branches below (today computed
                        // inline with identical arguments — byte-identical).
                        LineAnchor[] anchors = LineAnchorPlacement.Compute(densePath, spacingTileUnits, placement);

                        // KL-A1: the single-world clip the point path has had since D2, now applied to the LINE
                        // branch's shared anchors — so an anchor in the MVT buffer strip is emitted by the tile
                        // that OWNS it and no other.
                        anchors = KeepAnchorsInsideTile(anchors, densePath, extent);
                        if (anchors.Length == 0) continue; // every anchor belongs to a neighbour — nothing here

                        // Curved text — the pre-shields path, unchanged, but only when this label is NOT
                        // upright-at-anchors (map-aligned, or line-center's textAlign resolves Map by D3).
                        if (text != null && !textAtAnchors)
                        {
                            double3[] textPathRender = ProjectPath(densePath, tileAddress, extent, projection, out double3[] textPathUps);
                            output.Add(new SymbolLabel
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
                                Paint           = labelPaint,
                                TranslatePx     = translatePx,
                                TranslateAnchor = paint.TranslateAnchor,
                                PitchAlignment  = textPitch, // W1: RESOLVED — selects the world-metre arc walk
                            });
                        }

                        // P-B: a MAP-resolved line icon is a ONE-GLYPH CURVED label, riding the SAME anchors
                        // the curved text above uses. Its own ProjectPath call (rather than sharing the text
                        // branch's array) keeps each label the sole owner of its path; the only cost is a
                        // second projection on a layer carrying map-aligned text AND a map-aligned icon,
                        // which no shipped style does.
                        if (iconAlongLine && hasIcon)
                        {
                            double3[] iconPathRender = ProjectPath(densePath, tileAddress, extent, projection, out double3[] iconPathUps);
                            EmitAlongLineIcon(placement, iconPathRender, iconPathUps,
                                anchors, in alongLineIconCtx, tileKey, ref ordinal, output);
                        }

                        // D4: upright-at-anchors — text and/or icon emitted as ordinary POINT labels at each
                        // along-line anchor (the road-shield look). Suppress whichever side didn't resolve to
                        // viewport (Map-aligned text/icon on the SAME feature keeps its OWN emit path/fence).
                        if (textAtAnchors || iconAtAnchors)
                        {
                            // Built here (not hoisted — NIT 4): the side that did NOT resolve to viewport is
                            // suppressed (the map-aligned fence, D4).
                            var anchorCtx = new AnchorEmitContext
                            {
                                Text = textAtAnchors ? text : null,
                                TextSizePx = textSize,
                                PaddingPx = padding,
                                LayoutOptions = layoutOptions,
                                Paint = labelPaint,
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
                                // side: P-B made `hasIcon` true with `iconAtAnchors` false (the along-line
                                // case), which would stamp a viewport-aligned text Rider against a PairId no
                                // emitted label owns. Text side (D-PA-4): a map-aligned text leaves `Text`
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
                    // Built ONCE per feature, read once per anchor by EmitAtAnchor (NIT 4 — was hoisted above
                    // both branches; moved here since only the point branch reads it).
                    var ctx = new AnchorEmitContext
                    {
                        Text = text,
                        TextSizePx = textSize,
                        PaddingPx = padding,
                        LayoutOptions = layoutOptions,
                        Paint = labelPaint,
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
                        // D2 (road-shields): a Point feature anchors at every vertex (unchanged); a LineString
                        // feature under POINT placement anchors ONCE, at the path's mid arc-length — reusing
                        // LineAnchorPlacement.Compute(_, _, LineCenter), the same "middle of this tile-space
                        // path" topology the LINE branch above already computes. Anchors are resolved on the
                        // BUFFERED DECODED path (unclipped — the same input the curved branch uses); the
                        // existing [0, extent) single-world clip is then applied to the RESOLVED anchor point,
                        // unchanged (docs/road-shields-design.md §3 D2 — the clip contract).
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

        /// <summary>D4 (road-shields): the per-FEATURE values every anchor of that feature stamps onto its
        /// labels — evaluated once in <see cref="Extract"/>'s feature loop, read once per anchor by
        /// <see cref="EmitAtAnchor"/>. readonly struct + <c>in</c> per docs/conventions-short.md (bigger than
        /// ~16 bytes, read-only at the call site).</summary>
        private readonly struct AnchorEmitContext
        {
            // text side (Text == null ⇒ emit no text label)
            public string             Text { get; init; }
            public float              TextSizePx { get; init; }
            public float              PaddingPx { get; init; }
            public TextLayoutOptions  LayoutOptions { get; init; }
            public LabelPaint         Paint { get; init; }
            public bool               TextAllowOverlap { get; init; }
            public bool               TextIgnorePlacement { get; init; }
            public AlignmentMode      TextRotationAlignment { get; init; }
            // icon side (HasIcon == false ⇒ emit no icon label)
            public bool               HasIcon { get; init; }
            public SymbolQuad         IconQuad { get; init; }
            /// <summary>Baked-px transparent border inside <see cref="IconQuad"/>, per side.</summary>
            public float              IconSkirtPx { get; init; }
            public string             IconImage { get; init; }
            public float              IconPaddingPx { get; init; }
            public LabelPaint         IconPaint { get; init; }
            public bool               IconAllowOverlap { get; init; }
            public bool               IconIgnorePlacement { get; init; }
            public AlignmentMode      IconRotationAlignment { get; init; }
            /// <summary>P-B <c>icon-rotate</c> in radians — stamped on the ICON half only; text is never
            /// rotated by it.</summary>
            public float              IconRotateRadians { get; init; }
            /// <summary>Stage C <c>icon-optional</c> — stamped on the ICON half's
            /// <see cref="SymbolLabel.PairOptional"/>: the icon is the droppable one, so its text partner can
            /// place without it.</summary>
            public bool               IconOptional { get; init; }
            /// <summary>Stage C <c>text-optional</c> — stamped on the TEXT half's
            /// <see cref="SymbolLabel.PairOptional"/>: the text is the droppable one, so its icon partner can
            /// place without it.</summary>
            public bool               TextOptional { get; init; }
            // shared
            public float              SortKey { get; init; }
            public float2             TranslatePx { get; init; }
            public TextTranslateAnchor TranslateAnchor { get; init; }
            /// <summary>§10 D10 / P-A: true when this feature resolved BOTH a text and an icon and both are
            /// emitted at this anchor — the two halves are ONE placement instance, centred or not. The icon
            /// is stamped <see cref="LabelPairRole.Owner"/> (emitted first) and the text
            /// <see cref="LabelPairRole.Rider"/>, sharing a <c>PairId</c>; whether the proposed pair actually
            /// holds is decided downstream by <see cref="Placement.LabelPairing"/>. Otherwise the pre-pairing
            /// order (text then icon) is unchanged and both halves carry
            /// <see cref="LabelPairRole.None"/>.</summary>
            public bool               PairedInstance { get; init; }
        }

        /// <summary>P-B: the per-FEATURE icon values every along-line icon label of that feature stamps —
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
            public LabelPaint    Paint { get; init; }
            public bool          AllowOverlap { get; init; }
            public bool          IgnorePlacement { get; init; }
            public AlignmentMode RotationAlignment { get; init; }
            /// <summary>W1 — the RESOLVED <c>icon-pitch-alignment</c>, unlike
            /// <see cref="RotationAlignment"/> beside it (recorded as authored). Consumed: it selects the
            /// world-metre arc walk in the curved staging arm.</summary>
            public AlignmentMode PitchAlignment { get; init; }
            public float         SortKey { get; init; }
            public float         SpacingPx { get; init; }
            public float         MaxAngleDeg { get; init; }
            /// <summary>P-B <c>icon-rotate</c> in radians, composed on top of the along-line tangent.</summary>
            public float         IconRotateRadians { get; init; }
        }

        /// <summary>KL-A1: the along-line twin of <see cref="EmitAtAnchor"/>'s single-world <c>[0, extent)</c>
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

        /// <summary>P-B: emits ONE along-line icon label for a decoded path — a curved label whose single
        /// glyph is the icon quad, riding <paramref name="anchors"/> (the SAME array the curved-text branch
        /// walks) and rotated per-frame to the projected line tangent.
        ///
        /// <para><c>KeepUpright</c> is hard-false, not threaded: <c>icon-keep-upright</c>'s spec default is
        /// <c>false</c> (unlike <c>text-keep-upright</c>), and for a one-way arrow that default is the only
        /// correct behaviour — the arrow encodes the road's direction of travel, so flipping it to read
        /// "upright" would point it the wrong way. See docs/labels-and-symbols-design.md.</para>
        ///
        /// <para>Never paired: a pair is proposed only in <see cref="EmitAtAnchor"/>, on the point path, so
        /// this label's <c>PairRole</c> stays <see cref="LabelPairRole.None"/> structurally — §10's "a curved
        /// label is never paired" fence holds with no guard here.</para></summary>
        private static void EmitAlongLineIcon(
            SymbolPlacement placement, double3[] pathRender, double3[] pathUpRender, LineAnchor[] anchors,
            in AlongLineIconContext ctx, long tileKey, ref int ordinal, List<SymbolLabel> output)
        {
            output.Add(new SymbolLabel
            {
                Placement         = placement,
                Kind              = LabelKind.Icon,
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
                // than replaced by a sentinel so the field means the same thing on every curved label.
                MaxAngleDeg       = ctx.MaxAngleDeg,
                KeepUpright       = false,
                AllowOverlap      = ctx.AllowOverlap,
                IgnorePlacement   = ctx.IgnorePlacement,
                // Recorded for record fidelity, NOT consumed: the curved path takes its orientation from the
                // line tangent, so — exactly like the curved-text emit above — the builder does not forward
                // this onto the LabelInstance. It says what the style asked for, nothing downstream reads it.
                RotationAlignment = ctx.RotationAlignment,
                // W1: the pitch twin IS forwarded and IS read — it selects StageCurved's world arc walk.
                PitchAlignment    = ctx.PitchAlignment,
                IconRotateRadians = ctx.IconRotateRadians,
                Paint             = ctx.Paint,
                FeatureIndex      = ordinal++,
                TileKey           = tileKey,
            });
        }

        /// <summary>D2+D4: resolves one tile-space anchor point to a label anchor and emits its text/icon
        /// labels per <paramref name="ctx"/> — the single emit site shared by point-placement anchors AND
        /// line-placement upright-at-anchor labels. Applies the single-world <c>[0, extent)</c> clip (D2) —
        /// an anchor outside the tile is a source world-copy/buffer duplicate, dropped so the owning tile
        /// emits it exactly once.</summary>
        private static void EmitAtAnchor(
            double2 tilePoint, TileId tileId, double extent, IProjection projection,
            in AnchorEmitContext ctx, long tileKey, ref int ordinal, List<SymbolLabel> output)
        {
            if (tilePoint.x < 0.0 || tilePoint.x >= extent || tilePoint.y < 0.0 || tilePoint.y >= extent) return;
            double2 lonLat = tileId.ToLonLat(tilePoint.x, tilePoint.y, extent);
            ProjectedPoint pp = projection.ProjectPoint(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            double3 anchor = pp.World;

            if (ctx.PairedInstance)
            {
                // §10 D10: the pair's PairId is the OWNER's (icon's) FeatureIndex, captured before either
                // emitter advances `ordinal`. PairedInstance implies both HasIcon and Text != null — every
                // caller re-gates the predicate on the same fences that suppress a half (the line branch on
                // `iconAtAnchors && textAtAnchors`) — so both emitters below always run together here.
                int pairId = ordinal;
                if (ctx.HasIcon) EmitIconLabel(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, LabelPairRole.Owner, pairId);
                if (ctx.Text != null) EmitTextLabel(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, LabelPairRole.Rider, pairId);
            }
            else
            {
                if (ctx.Text != null) EmitTextLabel(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, LabelPairRole.None, 0);
                if (ctx.HasIcon) EmitIconLabel(anchor, pp.Up, tileKey, ref ordinal, output, in ctx, LabelPairRole.None, 0);
            }
        }

        private static void EmitTextLabel(
            double3 anchor, double3 up, long tileKey, ref int ordinal, List<SymbolLabel> output, in AnchorEmitContext ctx,
            LabelPairRole pairRole, int pairId)
        {
            output.Add(new SymbolLabel
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

        private static void EmitIconLabel(
            double3 anchor, double3 up, long tileKey, ref int ordinal, List<SymbolLabel> output, in AnchorEmitContext ctx,
            LabelPairRole pairRole, int pairId)
        {
            output.Add(new SymbolLabel
            {
                AnchorRender      = anchor,
                UpRender          = up,
                Placement         = SymbolPlacement.Point,
                Kind              = LabelKind.Icon,
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

        // Project a tile-local line string to render-space (PRE-RTC) vertices, plus (P2) the parallel,
        // index-parallel unit surface normal at each vertex.
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

        private static LabelPaint EvaluatePaint(PaintProperties paint, double zoom, IFeature feature)
        {
            Color textColor = paint.Color.Evaluate(zoom, feature);
            Color haloColor = paint.HaloColor.Evaluate(zoom, feature);
            return new LabelPaint
            {
                TextColor   = ToFloat4(textColor),
                Opacity     = paint.Opacity.Evaluate(zoom, feature),
                HaloColor   = ToFloat4(haloColor),
                HaloWidthPx = paint.HaloWidth.Evaluate(zoom, feature),
                HaloBlurPx  = paint.HaloBlur.Evaluate(zoom, feature),
            };
        }

        private static float4 ToFloat4(in Color c)
        {
            return new float4((float)c.R, (float)c.G, (float)c.B, (float)c.A);
        }
    }
}