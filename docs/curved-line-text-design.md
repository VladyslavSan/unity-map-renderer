# Curved along-line text (`symbol-placement: line` / `line-center`) — design

> The design for roadmap #5. Per-glyph curved text that follows a line feature's geometry, the real
> subsystem (Option B), not a straight-block approximation. Clean-room from the public MapLibre Style Spec.

## What it must do (spec-confirmed)

- **`symbol-placement: line`** — repeat the label along the line at **`symbol-spacing`** intervals
  (**pixels**, default 250 — "distance between two symbol anchors").
- **`symbol-placement: line-center`** — exactly **one** label at the center of the line.
- Each label's glyphs **follow the line curve**: every glyph is placed at its along-line arc position and
  **rotated to the local tangent** (reusing the #4 `BillboardMath` per-quad rotation).
- **`text-keep-upright`** (default **true**) — flip the label's walk direction so text never renders
  upside-down. Essential: without it, ~half of all line labels are upside-down. In scope for #5.
- **`text-max-angle`** (degrees, default 45) — drop a label whose adjacent-glyph tangent change exceeds
  it. Deferred to **#6** (the label still places without it; it just isn't dropped on hairpins yet).
- Line-placed text is **single-line** (no wrapping — `text-max-width` does not apply on a line).

## Why it's inherently screen-space (the load-bearing constraint)

`symbol-spacing` and the tangent are **pixels / screen-space**. A build-time fixed anchor count would make
spacing drift with zoom (a bug, not an approximation), and a build-time render-space tangent is only correct
at bearing-0/no-tilt. So placement — project the line, walk it in pixels, place+rotate each glyph — happens
**per frame** in `LabelPlacementSystem`, exactly like point-label projection already does. Build time only
extracts the line geometry and shapes the text.

## Data flow

```
BUILD (Core, per tile, once)                    PER FRAME (LabelPlacementSystem, screen-space)
─────────────────────────                       ────────────────────────────────────────────
extract LineString feature                      project the render-space path → screen polyline
  → carry the render-space PATH on the label      (per-vertex TryProjectAnchor; drop if any behind camera)
  → placement mode (line / line-center)         choose anchor arc-distances:
  → symbol-spacing (px)                            line-center → [totalLen/2]
shape text (existing CodepointTextShaper)         line        → every symbol-spacing px, centered
CurvedTextLayout(run, atlas):                   for each anchor, for each glyph:
  → per glyph: (arcCenter, centeredCell)          screenPt, tangent = walker.At(anchorArc + glyphArcCenter)
  single-line, glyph cell centered on its         emit PlacedQuad{ AnchorScreenPx = screenPt,
  own pen origin                                                    RotationRadians = tangentAngle,
                                                                    Quad = centeredCell }
                                                keep-upright: if the label's net screen direction is
                                                  right-to-left, walk the arc reversed
```

The reuse that makes this tractable: `PlacedQuad` already carries **per-quad** `AnchorScreenPx` +
`RotationRadians` (built in #4). So the Burst billboard job needs **no change** — a curved label is just N
`PlacedQuad`s with N different anchors/rotations instead of N sharing one. All the new work is (a) two pure
Core helpers and (b) the per-frame walk in `LabelPlacementSystem`.

## New Core pieces (engine-free, headless-tested)

1. **`PolylineArcWalker`** — over a screen-space `float2` polyline: `TotalLength`, and
   `At(arcDistance) → (float2 point, float tangentRadians)` by walking cumulative segment lengths and
   lerping within the containing segment; tangent = the segment direction (`atan2`). Clamps to the ends.
   Pure; red-proof with known polylines (straight → constant tangent; L-bend → the two segment tangents).

2. **`CurvedTextLayout`** — `Layout(ShapedRun, IGlyphAtlasView) → IReadOnlyList<CurvedGlyph>` where
   `CurvedGlyph { float ArcCenter; SymbolQuad Cell }`. Single forward pass over the shaped glyphs (mirrors
   `TextQuadLayout.PlaceGlyph`), but each glyph's cell is placed relative to **its own** pen origin and
   centered **horizontally only** on the glyph's advance-center, while `Top`/`Bottom` stay **baseline-
   relative** (NOT vertically centered — vertical centering would make ascenders/descenders straddle the
   line instead of riding above the baseline). `ArcCenter` = cumulative advance to that center (in baked px;
   the placement scales by `text-size / OneEm`). No anchor/justify/offset block shift (those are point-
   placement concepts). This baseline-center anchoring is exactly what lets the Burst job stay unchanged:
   `BuildQuad` rotates the cell about `anchorScreenPx`, so that point being the glyph's baseline-center makes
   the tangent rotation correct. Pure; red-proof with a 2-glyph run (known advances → known centers).

## Placement (`LabelPlacementSystem`, per frame)

- `SymbolLabel` / `LabelInstance` gain a **`PathRender` (`double3[]`)** for line labels (null for point
  labels — the existing `AnchorRender` path is untouched) plus `SymbolPlacement` + `SymbolSpacingPx`.
- Project each path vertex with the existing `TryProjectAnchor`; if any vertex is behind the camera, skip
  the label (first cut — partial-visibility clipping is a refinement).
- Build a `PolylineArcWalker` over the projected screen polyline.
- Anchor arc-distances: `line-center` → one at `TotalLength/2`; `line` → centered multiples of
  `symbol-spacing` covering the line. A label needs `labelWidthPx ≤ TotalLength` or it's skipped.
- **keep-upright:** compute the label's net direction (tangent at its center, or first→last glyph screen
  delta); if it points predominantly leftward (would be upside-down), walk the arc **reversed** for that
  label so glyphs read left-to-right.
- Emit one `PlacedQuad` per glyph (anchor = screen point at `anchorArc + glyphArcCenter*scale`, rotation =
  tangent there). The Burst job and shader are unchanged.

**Double-rotation trap (interaction with #4).** A curved glyph's rotation is the projected-line **tangent
only**. The projection already baked the bearing in (path vertices go through the bearing-aware camera), and
line placement defaults `rotation-alignment: auto → map` — which is exactly when #4's
`LabelBearing.BillboardRotationRadians` adds the bearing term. So the curved path must set
`PlacedQuad.RotationRadians = tangent` and **must NOT** also route through `BillboardRotationRadians` (tangent
+ bearing = double rotation). Point labels keep the #4 path; curved labels take the tangent branch instead.

**Zero per-frame alloc (T4).** `LabelPlacementSystem` is militantly no-per-frame-GC. The per-frame walk —
project the path, build the walker, emit quads — must run over **reused** scratch (a persistent projected-
polyline `NativeList`/array grown geometrically, a reused walker), never a fresh `float2[]`/`List` per label
per frame. `CurvedTextLayout` stays build-time (its per-glyph result is cached on the label), so only the
walk is per-frame, and it must stay allocation-free like the existing point-projection pass.

## Collision (the one genuinely-new placement concern)

Point labels submit **one** `LabelBox` (AABB) to `LabelCollision.SelectSurvivors`. A long curved label's
single AABB would be enormous (bounding a whole road) → almost nothing places. So a curved label submits a
**per-glyph box set** and is placed iff its boxes don't collide with already-placed boxes (MapLibre uses
per-glyph collision circles; per-glyph AABBs are the tractable analogue). This extends the collision model
from one-box-per-label to **N-boxes-per-label (all-or-nothing)** — the largest single change in this epic and
its own sub-slice.

## Sub-slices (each gated + committed on `feat/symbol-line-placement`)

- **B1 — Core helpers. ✅ DONE** (`31b996f4`). `PolylineArcWalker` + `CurvedTextLayout` + `CurvedGlyph`.
  Pure, fully headless red-proofed. No wiring yet.
- **B2 — line-center placement (single label, no new collision yet). ✅ DONE** (`2df27972`). Extract LineString → carry path;
  `LabelInstance` path fields; `LabelPlacementSystem` walks the projected line and emits per-glyph quads for
  `line-center`; basic keep-upright. **Collision: curved labels BYPASS it entirely (treated as
  allow-overlap) — always place, never suppress others.** NOT a bounding-AABB: a curved label's AABB bounds
  the whole projected road, so it would place almost no line labels AND suppress nearby point labels that
  render fine today (a real regression masked by a green single-label Tick test — the exact "did I break it?"
  trap). Overlapping curved labels are a truthful "collision not done yet"; missing labels are misleading.
  B3 replaces the bypass with real per-glyph collision. EditMode Tick teeth: glyphs land on the projected
  line, rotated to the tangent.
- **B3 — per-glyph collision. ✅ DONE.** New `LabelCandidate` (a contiguous box range) + a UNIFIED
  `LabelCollision.SelectSurvivors(candidates, boxes, …)`: a point label is a 1-box candidate, a curved label an
  N-glyph-box candidate; **all-or-nothing** (test every box, then insert every box) so one colliding glyph drops
  the whole label and a placed label blocks across its whole run. Candidates sort, boxes stay put (grid stores
  absolute indices; adjacent glyph boxes never self-block). `LabelPlacementSystem.Tick` unified: one
  project-and-stage pass builds candidates + boxes + staged quads (point via `LabelBox.Build`, curved via
  `LabelBox.BuildRotatedGlyph`), one greedy pass, emit = copy each survivor's staged quads. Curved labels now
  RESPECT collision (no more bypass); point path is byte-parity (differential + snapshot tests lock it).
- **B4 — `symbol-placement: line` repetition. ✅ DONE.** `symbol-spacing` parsed (StyleProperty<float>, spec
  default 250, min 1) → `SymbolLabel`/`LabelInstance.SpacingPx`. `LabelPlacementSystem.StageCurvedLabel` now
  stages ONE candidate per along-line anchor: `line` repeats at fixed `symbol-spacing` px from a half-spacing
  offset (anchors only where the whole label fits between the line ends; a short-but-long-enough line falls back
  to one centred label), `line-center` stays a single centred anchor. Each repeat is an independent
  collision candidate (keep-upright decided per anchor). Candidate scratch grows past one-per-label
  (`EnsureCandidateSlot`).
- **#6 — `text-max-angle` + `text-keep-upright`. ✅ DONE.** Both parsed (max-angle StyleProperty<float>
  default 45°, keep-upright bool default true) + threaded. `StageCurvedAnchor` drops a label at an anchor when
  the line tangent turns by more than `text-max-angle` between any adjacent glyph pair (rolls the pools back —
  the label is not bent illegibly round a corner); `text-keep-upright:false` disables the right-to-left flip.

## Visual-verify items (batched, like the #4 sign)

- Tangent is screen-space; correct under bearing/tilt because it's taken from **projected** screen points —
  but the overall look under rotation/tilt still wants a maintainer eyeball.
- keep-upright flip threshold and the `line` spacing/centering are see-it-to-believe-it.
