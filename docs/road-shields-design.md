# Road shields — design (SSOT)

Liberty's three road-shield layers (`highway-shield-non-us`, `highway-shield-us-interstate`,
`road_shield_us`) place a road number inside a pre-sized sprite badge (`road_1..road_6`,
`us-interstate_1..6`, `us-highway_…`/`us-state_…`, keyed by `ref_length`). This doc states the design
that makes that render correctly: how `symbol-placement` and alignment resolve for these layers, how a
centred icon+text pair places and drops as one symbol, how the sprite-atlas fetch race is closed, and how
the number sits inside the badge.

Clean-room throughout: derived from the public MapLibre Style Spec, this repo's source, and an empirical
probe over a committed fixture. No MapLibre source was read (`docs/maplibre-spec.md`).

> **Section numbers below are stable identifiers, not a table of contents.** `§3`, `§5`–`§7`, and `§9`–`§11`
> (with `§4` and `§8` absent) are cited by number from `docs/maplibre-spec.md`, `docs/labels-and-symbols-design.md`,
> and XML doc comments across `MapRenderer.Core`/`MapRenderer.Unity`/the EditMode test tree. Do not
> renumber without updating every one of those citations first.

---

## 1. The three shield layers

Verified against `Assets/StreamingAssets/Fixtures/liberty.json`. All three share `source-layer:
transportation_name`, `text-field: ["to-string", ["get","ref"]]`, `text-rotation-alignment: viewport`,
`icon-rotation-alignment: viewport`, `symbol-spacing: 200`, `text-size: 10`, `icon-size: 1`, and a
`["<=", ["get","ref_length"], 6]` + LineString/MultiLineString filter. They differ where it matters:

| Layer | minzoom | `symbol-placement` | `icon-image` resolves | `network` filter |
|---|---|---|---|---|
| `highway-shield-non-us` | 8 | `["step",["zoom"],"point",11,"line"]` | `road_<ref_length>` | **not** in {us-highway, us-interstate, us-state} |
| `highway-shield-us-interstate` | 7 | `["step",["zoom"],"point",7,"line",8,"line"]` | `us-interstate_<ref_length>` | == us-interstate |
| `road_shield_us` | 9 | `["step",["zoom"],"point",11,"line"]` | `us-highway_…` / `us-state_…` | in {us-highway, us-state} |

The interstate layer's step boundary is **z7**, not z11 like the other two, and its icon name comes from
a `concat` over `["get","network"]` rather than a literal prefix. Both differences matter to `§3` D1 and
the acceptance tests that exercise this table.

### Ruled out — do not re-investigate

| Suspect | Why it is not the cause |
|---|---|
| `icon-text-fit` | Liberty uses **pre-sized** sprites `road_1..road_6` keyed by `ref_length`, so the sprite already fits its number. Text-fit is never needed for this style. |
| Sprite availability | All 18 shield sprites exist in the live openfreemap sheet. |
| `concat` / number formatting | `Value.FormatNumber` uses `"R"`, so `5` → `"5"`, and the resolved icon names hit the atlas. |
| The style `filter` | All ten Berlin-fixture shield features pass it. |

---

## 2. The six defects, indexed to their fixes

Six independent defects sat in series between "the style authors a shield layer" and "the shield
renders." Each is fixed by exactly one decision below; none of the six is a full fix on its own — the
decisions in `§3` and `§9`–`§10` are the intended reading order.

| Defect | What it was | Fixed by |
|---|---|---|
| G1 | `symbol-placement` was read as a plain string, so an expression-valued (`step`) placement always degraded to `Point` | `§3` D1 |
| G2 | Point placement dropped every LineString feature outright | `§3` D2 |
| G3 | Icon emission was fenced off entirely under line placement | `§3` D4 |
| G4 | Line placement always produced a curved along-line label, ignoring `viewport` rotation-alignment | `§3` D3/D4 |
| G5 | A centred icon and its text were two independent collision candidates, so the icon could lose while its number kept drawing | `§10` (D8–D10; the interim mitigation at old `§3` D5 is retired) |
| G6 | The sprite atlas fetch is async and independent of tile build; a tile built before it resolved committed with no icons and never re-built | `§3` D6 |
| G7 | A symbol layer's icon and text quad shared one render queue with no draw-order tiebreak | `§9` D7 |
| G8 | A centred text block was centred on its line box, not on the glyphs' ink, so the number sat below the badge's optical centre | `§11` D12 |

---

## 3. Decisions

### D1 — `symbol-placement` is a **build-zoom**-evaluated `StyleProperty<SymbolPlacement>` (fixes G1)

`LayoutProperties.SymbolPlacement` is a `StyleProperty<SymbolPlacement>`, not a bare enum, matching the
file's own convention that a zoom-capable property is `StyleProperty<T>`. The projector is
`v => ParsePlacement(v.ToDisplayString())`: an absent or unrecognized value degrades to `Point`, and a
non-string expression result degrades the same way rather than throwing. `StepExpression` supports string
outputs directly, so no expression-engine change was needed.

**Build zoom, not display zoom.** The extractor evaluates placement **once, at the tile's build zoom**,
and the result is frozen into the emitted labels for that tile's lifetime. Layer *visibility* is
deliberately re-evaluated at display zoom against the live camera (`SymbolFeatureExtractor`), because
tiles are overzoomed rather than rebuilt; placement is not. This matches every other build-zoom-evaluated
property (`text-size`, `symbol-sort-key`; see `docs/maplibre-spec.md`), but shields straddle a step
boundary, so this is the one property where the staleness is visible.

**Accepted limitation.** While the camera crosses a step boundary — z11 for `highway-shield-non-us` and
`road_shield_us`, z7 for `highway-shield-us-interstate` — the same style layer runs two placement modes at
once: tiles built below the boundary keep one mid-arc shield per path, tiles built above it show repeated
shields at `symbol-spacing`, and older tiles change density and position only when eventually rebuilt. A
real fix needs display-zoom re-evaluation of `symbol-placement` (`§6`).

### D2 — point placement on a LineString anchors at **mid-arc** (fixes G2)

Under `symbol-placement: point`, a LineString feature yields one label per line string, anchored at the
line's **mid arc-length**, via `LineAnchorPlacement.Compute(path, _, SymbolPlacement.LineCenter)`. The
Style Spec's "the label is placed at the point where the geometry is located" is underspecified for a
line; mid-arc is this renderer's choice, made because it does not pile shields onto tile edges the way a
first-vertex anchor would. A future parity screenshot disagreeing with upstream is the one line to change.

**Anchor-clip semantics.** Anchors are computed on the **buffered source path exactly as decoded** by
`MvtGeometry.Decode`, not on a path clipped to the tile — the same input the curved path already uses, so
the anchor topology matches D4's line-anchor topology exactly. The existing single-world clip (a resolved
anchor outside `[0, extent)` is a world-copy/buffer duplicate) applies **per resolved anchor**, unchanged.

Two accepted consequences:

- **A buffer-dominated path can lose its only shield.** Under point placement there is exactly one anchor,
  so the clip is all-or-nothing: a path running from `x = -1000` to `x = 100` has its mid-arc at
  `x ≈ -450`, outside `[0, extent)`, and emits nothing even though it has an in-tile segment. The
  neighbouring tile's independently buffered copy has a different midpoint, so cross-tile recovery is not
  guaranteed. This is confined to point placement — **below** each layer's step boundary.
- **The mid-arc anchor is not invariant under a change of source clipping or buffering** — arc length is a
  function of the buffered path, so re-tiling or a different buffer moves it. The property that holds is
  "deterministic for a given decoded path", not "stable under tile clipping".

Under line placement the same clip applies per anchor, which is milder: anchors at `spacing·(k+0.5)`
falling in the buffer drop, in-tile ones survive — the already-recorded cross-tile shield limit (`§6`).

**Fence: LineString only, never Polygon.** Liberty's `place` / `airport` / `poi_transit` symbol layers
carry no geometry-type filter and rely on their source layer being point-only. Widening point placement to
accept LineString does not add labels to those layers, because their OpenMapTiles source layers carry no
line geometry — but widening to Polygon would, since MapLibre supports point placement on a polygon via
its centroid. That gap is deliberate (`§6`).

### D3 — alignment resolution: `auto` → `map` under line placement, `viewport` under point

`Core/Text/AlignmentResolution.Resolve(AlignmentMode, SymbolPlacement)` resolves the spec's `auto` default
to `map` for `line`/`line-center` and to `viewport` for `point`. This is not cosmetic:
`waterway_line_label`, `water_name_line_label` and `road_one_way_arrow*` leave rotation-alignment unset —
if `auto` resolved to viewport under line placement, those layers would flip from curved to upright.

### D4 — line placement + viewport-resolved alignment ⇒ **upright labels at the line anchors** (fixes G3+G4)

Under `symbol-placement: line` with rotation-alignment resolving to `viewport`, the extractor emits, per
computed `LineAnchor`, ordinary **point-placement** labels (text and/or icon) at the anchor's projected
position, instead of one curved label per path. This is the road-shield look, and it is also what MapLibre
does for this combination.

- Icons under line placement are the existing point-icon path at a different anchor — staging, collision,
  world-anchored draw, cross-tile dedup, fade identity and the sprite atlas all work unchanged, with no new
  code path.
- Curved text is untouched for every map-aligned layer (`highway-name-*` set `text-rotation-alignment: map`
  explicitly; `waterway_line_label` / `water_name_line_label` resolve `auto` → map per D3).
- The anchors are the same `LineAnchorPlacement` topology the curved path already computes, so
  `symbol-spacing` keeps its meaning — the reason D2 keeps anchors on the buffered path.

A map-aligned line icon (`road_one_way_arrow` / `road_one_way_arrow_opposite`, alignment `auto` → map) is
emitted as a **one-glyph curved label**: the icon quad rides the curved path's per-anchor staging and is
rotated to the line tangent by the icon shader. An `icon-anchor: center` quad is already a
`CurvedGlyph.Cell`, so this needs no new record kind, gather, or oracle — see
`docs/labels-and-symbols-design.md` §6. The viewport-resolved case above is a separate, untouched path.

### D5 — retired; superseded by `§10`

An interim mitigation lived here (icon owns the collision box, text rides as a non-blocking passenger).
`§10` (D8–D10) replaced it with true one-instance pairing and retracts D5's accepted deviations — the
bare-number and independent-fade behaviour it describes no longer exist. Kept as a name because other
comments in the tree still say "supersedes D5"; its content is `§10`.

### D6 — a symbol build **waits for the sprite fetch to settle**; it never commits icon-starved (fixes G6)

The sprite atlas is fetched once per style, asynchronously and independently of tile loading. A symbol
build that starts before the fetch settles must not commit without icons and then never self-heal — no
tile re-kick path exists for an already-built, still-in-cover tile.

**The mechanism.** `SymbolSubsystem.SetStyle` starts the fetch and keeps its result as a `.Preserve()`d
`UniTask`. Readiness is `SpritesSettled`: the fetch task has left `Pending`. `TryBeginBuild` (main thread)
checks it; if not settled, the build **parks** — its decode and captured context go onto a second
`ConcurrentQueue` instead of running the extract. `PumpBuilds` (main thread, every frame) drains that queue
once `SpritesSettled`, constructing each build's layer processors with the now-live sprite atlas and
dispatching the worker phase exactly as the kick would have.

Properties that make this correct:

- **Settled means "reached a terminal state", not "non-null".** That includes no `sprite` URL, a 404/204, a
  fault, and cancellation. A style with no sprites never parks: the fetch is already terminal before
  `SetStyle` returns. A "wait for the atlas to be non-null" predicate would hang forever on a 404 — the
  obvious wrong version of this fix.
- **Every thread-boundary decision stays on main.** Readiness is read only in `TryBeginBuild` and
  `PumpBuilds`; the pool side only enqueues into a `ConcurrentQueue`, the same safe-publication carrier the
  existing handoff queue already is. No volatile flag, no lock.
- **Cancellation is unchanged.** Parked entries carry the build's cancellation token and reserved store
  slot, and are drained wherever the existing handoff queue is drained.
- **The cost is a bounded, one-off latency,** not a stall: labels for tiles kicked during the fetch window
  appear once the sheet lands (one HTTP request, parallel with the tile fetches). Retained decodes are
  bounded by the tiles kicked in that window, and their tails still trickle at the existing per-frame
  build budget.

**The deadline bound, and what it changes about the invariant.** "No tile ever commits icon-starved" holds
**while the fetch is still in flight** — true for every normal terminal path, all of which settle in well
under the bound. It does not hold once the fetch has been waited on past `SymbolSubsystem.SpriteFetchDeadlineSeconds`
(an `internal const`, currently 8s): `UnityWebRequestSpriteSource` sets no HTTP timeout, so a genuinely hung
endpoint (connects, never responds) would otherwise park every affected build forever, holding an unbounded
pending queue. Once the deadline trips — measured against a test-overridable clock,
`SymbolSubsystem.NowSecondsOverride` — `SpritesSettled` goes true regardless of the fetch's own status, and
the pending drain dispatches every parked build with whatever atlas state exists: a deliberate, bounded
fallback to the pre-D6 floor (possibly icon-starved, never self-healing for that tile) instead of a
permanent stall. So the precise invariant is: no tile commits icon-starved before the deadline; after it,
an icon-starved commit is the accepted degradation for a fetch that never terminates.

**Tuning the deadline.** 8s is a starting value, not a measured one. It may be raised (10–15s) for a
legitimately slow sprite endpoint — over-waiting only costs the already-broken hung-endpoint case. It must
not be lowered: under-waiting costs the normal-but-slightly-slow case, which is the exact bug D6 exists to
fix.

Pinned by tests in `Assets/Tests/MapRenderer.Tests.EditMode/Text/SymbolReconcileAsyncTests.cs` (the merged
home of the former `SymbolSpriteReadinessTests.cs`; `[Unity]` — `SymbolSubsystem` is engine-bound and hops
to the main thread before constructing a `Texture2D`) and the
`GatedSpriteSource` test double in `Assets/Tests/MapRenderer.Tests.EditMode/TestSupport/GatedSpriteSource.cs`.

---

## 5. Test coverage

Stage-1 behaviour (D1–D6) is pinned in
`Assets/Tests/MapRenderer.Tests.EditMode/Style/SymbolShieldExtractionTests.cs` (placement-per-zoom, the
icon+text pair shape, the two US-only shield layers selecting zero Berlin-fixture features) and in
`Assets/Tests/MapRenderer.Tests.EditMode/Text/SymbolReconcileAsyncTests.cs` (the merged home of the former
`SymbolSpriteReadinessTests.cs`; the sprite-fetch gate, including the deadline fallback). Alignment
resolution (D3) is pinned alongside
`Core/Text/AlignmentResolution.cs`. Acceptance detail — exact assertions, counts and injected defects —
lives with those tests, not here.

---

## 6. Deferred / open

| Item | Status |
|---|---|
| **Point placement on Polygon** (centroid / pole of inaccessibility) | Deliberate gap (D2) — would add labels to unguarded `place` / `airport` / `poi_transit` layers. |
| **Display-zoom re-evaluation of `symbol-placement`** | Open. The D1 accepted limitation — the visible step-boundary pop. Belongs with the wider camera-property re-evaluation work. |
| **Mid-arc anchors over the tile-clipped path** | Open. Would recover the buffer-dominated short paths D2 loses, but needs a polyline clip and would desync D2's anchors from the same `LineAnchorPlacement` topology D4 relies on. |
| **Rebuilding tiles on a sprite-atlas change** | Open only as a general mechanism. D6 makes it unnecessary for the startup race, since the atlas changes at most once per style; a general "invalidate these tiles" API does not exist today. |
| **`icon-text-fit`** | Not needed by liberty (pre-sized `road_N` sprites) and unrelated to this design. |
| **Cross-tile shield dedup along a line** | Open. A road crossing a tile boundary gets independent anchors per tile, so a shield can repeat or gap near the seam — the same class of limitation as the existing cross-tile line-label behaviour. |
| **A curved (along-line) label paired with an icon** | A paired instance only exists on the point path (`docs/labels-and-symbols-design.md` §6.1: "Pairing stays out structurally"). |
| **The block's top/bottom edge definition** (text centring, `§11`) | Open — see D12's "why not touched" in `§11`. Measured but unconfirmed: a `top`-anchored label's ink starts about 9 baked px (0.375 em) below its anchor, so `poi_r*` / `airport` text plausibly sits lower than MapLibre's by roughly that much. Wants a maintainer eyeball before it becomes a decision. |
| **Deriving `GlyphSdf.BaselineBelowReferencePx` per font stack** instead of a constant (`§11`) | The glyph-PBF format carries no font-level metrics, so every consumer must assume one; 26 is measured from the committed fixture. A font baked against a different ascent needs the glyph-atlas view to expose a per-stack metric, plus a policy for stacks with no baseline-resting reference glyph (CJK-only labels). |
| **Re-tuning `GlyphSdf.NominalCapHeightEm` per script** (`§11`) | `17/24` is measured from Latin Noto Sans; no reported symptom for other scripts. |
| **`text-variable-anchor`, `text-writing-mode`, vertical CJK** (`§11`) | Untouched by and unaffected by this design. |

---

## 7. Grounding (touch points)

Core: `Style/Symbol/LayoutProperties` (`SymbolPlacement`, `ParsePlacement`, `ParseAlignment`),
`Style/Symbol/IconImageResolver`, `Text/AlignmentMode` (+ `AlignmentResolution`),
`Text/Placement/LineAnchorPlacement` / `LineAnchor`, `Text/Placement/SymbolCollision`,
`Text/Placement/SymbolStagingMath`. Unity: `Text/SymbolFeatureExtractor` (the placement/geometry gate, the
icon path, the line and point branches), `Text/SymbolSubsystem` (`TryBeginBuild`, `PumpBuilds`,
`FetchSpriteSheetAsync` — D6), `Rendering/Tile/Processing/TileSymbolLayerProcessor` (the atlas is a ctor
arg — D6 defers its construction), `Text/StyledSymbolTileBuilder` (the point vs. curved branches),
`Text/Placement/WorldSymbolRenderer`. Style: `Assets/StreamingAssets/Fixtures/liberty.json` (the 3 shield
layers). Related: `docs/labels-and-symbols-design.md` §3 (curved along-line text) and §5 (icon support —
the I3 fence this lifts), `docs/maplibre-spec.md` (symbol support matrix).

---

## 9. Render queue: a layer owns a **band** of sub-slots (fixes G7)

A symbol layer's icon quad and its text quad are coplanar at the same anchor (a centred pair, `§10`), both
transparent, both ZWrite off. If they share one render queue value, Unity's remaining tiebreaks (camera
distance, then internal renderer order) decide which paints last — deterministically per frame for a fixed
scene, so the wrong result is stable rather than flickering, and when the icon resolves after the text its
opaque badge paints over the number it exists to frame.

### D7 — a layer owns a band of sub-slots; a symbol layer's icon sits under its own text

`LayerDrawOrder` stays the one render-queue formula (its stated role) and gains a named sub-slot inside
each layer's band, instead of a bare per-layer value:

```csharp
public enum LayerSubSlot { Base = 0, Above = 1 }      // ordering role WITHIN one layer's band
public const int SubSlotsPerLayer = 2;                // == the enum's value count == the queue stride
public const int QueueCeiling     = 5000;             // Unity clamps renderQueue to [0, 5000]

public static int QueueFor(int drawIndex, LayerSubSlot subSlot = LayerSubSlot.Base)
    => TransparentQueue + drawIndex * SubSlotsPerLayer + (int)subSlot;
```

- Layer *i* owns the contiguous band `[base + i·S, base + i·S + S−1]`. Every sub-slot of layer *i* is
  strictly below every sub-slot of layer *i+1* — the cross-layer draw-order guarantee is strengthened, not
  relaxed.
- **A symbol layer's icon is `Base`, its text is `Above`.** The badge draws under the number it frames.
- Fill / line / background use `Base` only, so their relative order is arithmetically unchanged (a strictly
  increasing map `i ↦ base + i·S`).
- A future kind that needs a third sub-slot adds an enum value and bumps `SubSlotsPerLayer`; no caller
  re-derives the arithmetic.

`RenderLayerSet.Build` keeps its single generic write per layer and asks the layer which sub-slot its
primary `Material` occupies, via `IRenderLayer.MaterialSubSlot` (`Base` for fill/line/background, `Above`
for a symbol layer). `SymbolRenderLayer` writes its icon material explicitly at
`QueueFor(drawIndex, LayerSubSlot.Base)`.

**Rejected: moving the text-queue write into `SymbolRenderLayer` alongside the icon.** It reads tidier but
makes `RenderLayerSet.Build`'s write dead for one layer kind and lets the two drift; the uniform "`Build`
assigns every slot's queue" contract is worth keeping.

**Rejected: a capability interface probed with `is`.** Draw order is a property every layer has, not a
capability some have; hiding a global ordering invariant behind a type test is the wrong shape even though
it touches fewer files.

**Why `Above`, not `Overlay`.** `Overlay` already names Unity's `RenderQueue.Overlay` (4000) in this
domain — a fixed queue value `SymbolRenderLayer` does not use. `Above` says the one thing that matters:
above its own layer's `Base`, still inside its own band.

**Ceiling.** The validity check must cover the band's top, or the last layer's `Above` slot would saturate
at 5000 and silently collapse onto its own `Base`. At base 3000 with stride 2, 1000 layers is the maximum
(top slot 4999); `QueueFor` throws past that bound. Liberty declares 111 layers, roughly 9× headroom.

**Known limitation — whole-band validation at the ceiling boundary.** `QueueFor` validates only the
sub-slot it is asked for, not the whole band a layer's kind will need. At exactly the boundary index, the
`Base` write can succeed while the `Above` write for the same layer throws, leaving a partially-built
layer set with a material never disposed. Liberty is roughly 9× under the boundary; a real fix needs
band-aware validation (reserving the full band up front) and belongs with the render-queue system's
eventual replacement, below.

**Forward context.** The integer render-queue system is an interim; the eventual replacement is a BRG /
`ScriptableRenderPass` draw-order target (`ARCHITECTURE.md` §2). Widening the stride from 1 to 2 halves
that interim's layer-count runway, from ~2000 to ~1000 — liberty uses 111.

### Invariant

For any two declared layers *i < j*, every queue value of layer *i* is strictly less than every queue
value of layer *j*, for every layer kind. A symbol layer's icon queue is always strictly less than its own
text queue. The composite draw order of a fill/line/background-only style is unaffected by the stride
change.

Pinned by `LayerDrawOrderTests` (`Tools/core-tests` and both Unity runners) and the `[Unity]` render-queue
assertions in `Assets/Tests/MapRenderer.Tests.Visual/LayerOrderSnapshotTests.cs`, which asserts
only relative order and is unaffected by the stride value.

---

## 10. Symbol pairing: a centred icon+text symbol is ONE placement instance (fixes G5)

MapLibre treats a symbol's icon and text as **one instance**: `icon-optional` / `text-optional` default
false, so they place or drop together, with a combined box. This renderer models that as one
`SymbolCandidate` spanning both halves' boxes.

**Scope note.** The predicate for *which* icon+text symbols pair this way is not shield-specific — it was
generalised beyond shields' centred layout, and its current statement is owned by
`docs/labels-and-symbols-design.md` §7, which wins if this section and that one ever disagree. What follows
is the mechanism a paired shield symbol relies on.

### D8 — the pair is ONE `SymbolCandidate` over BOTH boxes, with a per-candidate EMIT RANGE

`SymbolCandidate` already spans a contiguous range of boxes (`BoxStart`/`BoxCount`), and `CollisionJob`
already places a candidate all-or-nothing: every box is tested before any is inserted, and a placed
candidate inserts them all — exactly MapLibre's combined-box semantics, and the same machinery curved
along-line labels already run on. A paired symbol is one candidate with two boxes (icon AABB, text AABB,
each keeping its own `*-padding`), and `SymbolCandidate` carries `EmitStart`/`EmitCount` — a range into the
staged `CandidateEmit` pool, mirroring `BoxStart`/`BoxCount`. The pair emits two `CandidateEmit`s, each
with its own slot and atlas kind; `WorldSymbolRenderer` is not touched, because its mesh-slot key
(`TileKey`, `Slot`, `AtlasKind`) still addresses one atlas per emit.

**Rejected: per-quad `AtlasKind` merged into one emit.** The two halves differ in colour, size, padding and
translate (`text-color`/`icon-opacity`, text size vs. the pre-baked icon scale, `text-padding`/
`icon-padding`, `text-translate` vs. the icon's untranslated anchor). Merging them needs a per-quad style
table through the Burst staging SoA — more blast radius for less fidelity than two boxes on one candidate.

**Rejected: an instance link honoured inside the collision greedy** (a follower candidate inherits its
owner's survivor bit). The follower's box is never tested, so text stops participating in collision at
all — weaker semantics, and it needs a second pass inside the per-frame collision job.

**Rejected: a shared fade id with an AND-harvest** (independent candidates; a fade id counts as placed only
if every candidate carrying it survived). This is broken by construction: the two boxes overlap, so the
icon places, inserts its box, and the text then collides with its own partner — every shield would drop.
Test-all-then-insert (the multi-box candidate) is the only formulation immune to that self-block.

### D9 — identity: the rider has NO identity of its own

The pair's owner is the icon: it is emitted first, holds the lower `FeatureIndex`, and its
`(cell, layer, iconImage)` key is the one the badge already dedups on. The text is its **rider**:

- **One candidate ⇒ one fade id** — the owner's, unchanged. Both halves draw at that one opacity.
- **One dedup entry** — the cross-tile reconciler skips riders in its scan and emits a winning owner's
  rider immediately after it, same block, same tile. `CrossTileSymbolKey` and the dedup key are not
  widened.

Not widening the keys means a lone text label and a lone icon label keep byte-identical identities, so
"icon-only and text-only labels are unaffected" holds structurally. It also gives the right cross-tile
answer for free: a tile holding the complete pair and a finer tile holding only the icon share one key, so
finest-zoom-wins picks a whole symbol from one tile rather than assembling one from two.

**A rider never outlives its owner.** If a claim-skipped owner (an active copy already shows the symbol) is
dropped from a departing tile's scan, its rider is dropped with it — otherwise the plan would carry an
orphan rider that stages nothing while the departing tile's badge is still drawn by the active copy. In the
reconciled output, every rider is immediately preceded by its own owner.

**Accepted deviation.** A rider has no key, so a lone text copy of the same symbol in another tile is not
deduped against a pair's text, and both are emitted. This needs one tile with a complete pair and another
with a text-only copy of it (an icon that failed to resolve there, within the window D6 closes); the two
overlap exactly, and the collision pass drops the loser.

### D10 — where the pair is decided, and how it survives a half-built label

The extractor stamps a `SymbolPairRole` (`Owner`/`Rider`/`None`) plus a `PairId` on the two features it
emits adjacently, instead of forcing the passenger's overlap flags — the D5 forcing is gone, and a pair's
text carries its own authored `text-allow-overlap`/`text-ignore-placement` again; the pair candidate's
flags are the AND of the two halves'.

The roles are a *proposal*, not a fact: per-label shaping isolation can drop a label, and the baker
tolerates a missing slot, so a rider can go missing. `SymbolPairing` is the one engine-free resolver that
decides the truth from a list: index *i* is a paired owner iff `labels[i+1]` exists, is non-null, is a
rider, and matches on `PairId`. A half-built pair dissolves into two ordinary labels — never an owner bound
to a stranger. Both the baker and the reconciler call this one resolver, so they cannot disagree.

Downstream, the pairing is already resolved: a staged point record carries only its resolved `PairRole`,
and an owner's rider is the next point record — which holds because the reconciler emits them adjacently
and the gather compacts point records in winner order. The staging job checks the next record's role
before reading it and stages the owner alone if it is not a rider (a badge with no number — never a bare
number, never a stranger).

**Rejected: folding both halves into one block record.** Removes the adjacency contract, but adds a new
record kind or pool everywhere a point record is addressed (the tile block, the baker, the gather job, the
mirror, the batch, the parity oracle). The adjacency route changes the gather by zero lines.

### D11 — `icon-optional` / `text-optional` are a **per-box collision verdict**, not a pairing gate

Both properties are parsed into `LayoutProperties` as a plain `bool` (spec default false) and threaded
through the pair as `PairOptional` on each half. An earlier design gated pair *formation* on both flags
being false; that design is rejected, not shipped. Its reasoning was that a centred pair's boxes overlap by
construction, so un-pairing makes the halves mutually exclusive — but that does not hold once pairing
covers non-centred, disjoint boxes too (`§10`'s scope note). `text-optional` also names an escape hatch on
the *instance*, which presupposes the pair still forms, rather than a condition on whether it forms at all.
The shipped design instead carries each half's optionality into `SymbolCandidate.OptionalBoxMask` (bit 0 =
owner/icon, bit 1 = rider/text), so the all-or-nothing collision test gains per-box granularity: an optional
box can fail to place while its non-optional partner still does, with no second pass and no widened key. No
liberty **shield** layer sets either property — their text is centre-anchored, so this is inert for shields
specifically, and is recorded here only because it retires the interim guess this section made before the
mechanism existed. Current SSOT for the general mechanism: `docs/labels-and-symbols-design.md` §7 D-PA-3.

### Invariant

Only a paired icon+text symbol changes behaviour relative to two independent labels. `CrossTileSymbolKey`,
the dedup key, the per-point fade id and `WorldSymbolRenderer` are unmodified by pairing; a text-only or
icon-only label produces a byte-identical stage record and a candidate with `BoxCount == 1`,
`EmitCount == 1`, `EmitStart == SymbolIndex`; every curved label is untouched.

**Performance.** A pair costs strictly less per frame than two independent candidates would: one fewer
sort element, one fewer grid query set, one fewer fade lookup, one fewer fade ease. No new per-frame
managed allocation, pass, buffer or job.

Pinned by tests in `Assets/Tests/MapRenderer.Tests.EditMode/Text/Placement/SymbolStagingMathTests.cs` (the
merged home of the former `SymbolPairingTests.cs`; the `SymbolPairing` resolver, compiled by
`Tools/core-tests` too), `SymbolPairWiringTests.cs` (the `[Unity]`
Tick-level wiring check that a pair draws into both world meshes with one fewer candidate per pair — the
tooth other comments in the tree cite as P10), and
`Assets/Tests/MapRenderer.Tests.EditMode/Style/SymbolFeatureExtractorIconTests.cs` (the extractor's role
stamping).

---

## 11. Text vertical centring: `Center` positions the block's optical centre, not its box midpoint (fixes G8)

A centred text block's box is not filled symmetrically by its glyphs, so centring on the box put a
shield's number visibly below the badge's centre.

### Why the box and the ink disagree

A glyph cell's top is anchored at `baselineY + entry.Top + GlyphSdf.Buffer` and grows down. The glyph-PBF
`Top` is top-referenced and negative, so the layout's local `baselineY` is not a baseline — it is the
font's ascent reference line, and the ink hangs below it. For the committed `NotoSansRegular/0-255`
fixture, every baseline-resting Latin glyph has `Height - Top == 26` baked px, so the typographic baseline
sits 26 baked px below the reference line, and a cap or digit's ink occupies `[-26, -9]`. **This constant
is measured over Latin `NotoSansRegular/0-255`;** the other three shipped ranges (Arabic + presentation
forms + variation selectors) have a modal `Height - Top` of 27, so a label in those scripts would centre
about 1 baked px off — inert today, since no liberty layer centres those ranges, but the constant's own XML
doc states the Latin scoping so a future reader does not assume it is font-universal.

The layout box, meanwhile, spans `y ∈ [-blockHeight, 0]` for `blockHeight = lineCount * lineHeightPx`, so
its top edge carries the font's full ascent slack and its bottom edge is whatever one line-height leaves
over — neither edge is where the ink actually is. `IconQuadLayout` has no such problem: a sprite's box **is**
its ink, so centring it is exact. The two paths disagree by construction.

A second symptom of the same root: a single-line centred label's vertical position depended on
`text-line-height`, even though line-height is a line-*stacking* property with nothing to stack at one line.

### D12 — `Center` positions the block's optical centre; `Top`/`Bottom` keep the box edges they have

Vertical anchoring keeps its three cases, and only the centre case changes:

```
Top     : globalY = 0                                        (block top edge at the anchor)
Bottom  : globalY = lineCount * lineHeightPx                  (block bottom edge at the anchor)
Centre  : globalY = OpticalCentreBelowReferencePx + (lineCount - 1) * lineHeightPx * 0.5
```

with named convention constants, not literals at the use site:

```
GlyphSdf.BaselineBelowReferencePx = 26     // measured: where the baseline actually sits
GlyphSdf.NominalCapHeightEm       = 17/24  // Noto Sans measures a 17 baked-px cap over a 24 px em
OpticalCentreBelowReferencePx     = BaselineBelowReferencePx - 0.5 * NominalCapHeightEm * OneEm   // 17.5
```

The cap-height literal is written as `17f / 24f` rather than a decimal so it carries its own derivation;
the em conversion cancels exactly, so `OpticalCentreBelowReferencePx` is exactly `17.5`, not an
approximation. A centred multi-line block's optical centre is the midpoint between the first line's and the
last line's, which is why the multi-line term is `(lineCount - 1)/2` line-heights rather than
`lineCount/2` — line spacing is untouched, the whole block simply moves by the same constant at every line
count and line height.

**The metric is the cap band** — `[baseline, baseline + nominal cap height]`, taken from convention
constants, never from the string being laid out:

- *Rejected: the ink bounds of the glyphs actually present.* Unstable — a cap/digit run, an x-height run
  and a descender run each measure a different ink centre over the fixture, so a label would jump when its
  text changed.
- *Rejected: the em box above the baseline.* Barely moves the old value — caps occupy roughly the lower 70%
  of the em box, which is why the number read low in the first place.
- *Rejected: the ascender/descender midpoint.* Overshoots the other way, by about the same magnitude as the
  defect it would fix.
- *Chosen: half a cap height above the baseline,* because it is content-independent and lands within a
  half pixel of the fixture's measured whole-font ink band.

**Why `Top`/`Bottom` are deliberately not touched.** The same root makes the block's top and bottom
*edges* suspect too — a `text-anchor: top` label's ink starts below the anchor by the same slack. But the
centring requirement pins only the *difference* between ascent and descent (it must equal the cap height);
the absolute values of ascent and descent are constrained by nothing measured and by no reported symptom.
Redefining the edges means guessing two numbers and moving every top/bottom-anchored label in every style
on that guess — an open question, not a decision (`§6`).

**Consequence.** `Center` is no longer the arithmetic midpoint of `Top` and `Bottom` — the layout box's top
edge carries the font's ascent slack, so the box midpoint was never the optical centre, the same
distinction CSS draws between a line box and `vertical-align: middle`. Vertical anchor resolution is a
three-valued anchor, not a lerp-able `vAlign` float, so no caller can re-derive the midpoint by accident.
`IconQuadLayout` is unchanged — it already centres ink on ink.

Non-centre vertical anchors (`top`, `bottom`, the four corners), horizontal anchoring, justify, wrap,
letter-spacing, `text-offset`, `text-radial-offset`, and RTL are unaffected — `CurvedTextLayout` takes no
selectable block anchor at all. **Curved (along-line) labels are not exempt from the centring itself**,
though: `CurvedTextLayout` places every glyph's cell using this same `OpticalCentreBelowReferencePx`
constant (`CurvedTextLayout.cs:65`), so a curved label's vertical position moves with this constant exactly
as a Centre-anchored point label's does — the two paths share one convention, not two. A paired symbol
(`§10`) is unaffected in its placement verdict: the two boxes are still tested all-or-nothing on one
candidate, so only the text half's screen rect shifts, never whether the pair places.

Pinned by `Assets/Tests/MapRenderer.Tests.EditMode/Text/TextVerticalCentringTests.cs` — the merged home of
both the `TextVerticalCentringTests` class (content-independence, line-height independence, the
`Top`/`Bottom` goldens, and the constant re-derived from the fixture's own glyph metrics rather than
restated) and the former `CurvedTextCentringTests.cs`'s `CurvedTextCentringTests` class (icon and text ink
centres coincide within half a baked pixel — the shield claim itself).
