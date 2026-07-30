# Road shields — diagnosis & design (SSOT)

Liberty's three road-shield layers (`highway-shield-non-us`, `highway-shield-us-interstate`,
`road_shield_us`) render **nothing** — not the shield sprite, not even the road number. This doc is the
single source of truth for why, what we decided to do about it, what we deliberately did **not** do, and
what remains open.

Clean-room throughout: everything below is derived from the public MapLibre Style Spec, this repo's source,
and an empirical probe over a committed fixture. **No MapLibre source was read** (`docs/maplibre-spec.md`).

---

## 0. Revision — what the adversarial pass changed (2026-07-29)

The first draft of this doc claimed **five** gates and asserted that fixing all five makes shields render at
z13. An adversarial review of the implementation plan falsified that claim in two places and found three
defective acceptance teeth. The reversals, recorded here rather than silently absorbed:

| Was | Is now | Why |
|---|---|---|
| Five gates, extractor-only scope | **Six gates** — **G6**, the sprite-atlas readiness race, is IN scope (**D6**) | A tile committed before the async sprite fetch resolves is permanently icon-free. The extractor fix alone therefore does **not** make shields render in the demo — the very claim this epic exists to deliver. Verified in source: `SymbolLabelSubsystem.TryBeginBuild:433` captures `_spriteAtlas` (may be null); `FetchSpriteSheetAsync:651-652` assigns it with no invalidation; the doc at `:252-255` admits the tile only "self-heals on its next rebuild". |
| D5 "the centred icon+text pair is **one symbol**"; the G5 row said D5 **fixes** G5 | D5 is a **named approximation** with a new, strictly-narrower harm profile; G5 is **mitigated**, not fixed | No flag-only formulation makes two independent collision candidates place/drop atomically. Claiming "one symbol" overstated it. The passenger text now also carries `IgnorePlacement = true`, so it can never *block* an unrelated label — the one harm that was removable at zero cost. It can still survive alone (a bare number), which is now a **pinned, tested** decision rather than a latent surprise. |
| T9 "both boxes survive `LabelCollision`" was the G5 tooth | T9 is **replaced** — it was **disarmed** | `LabelCollision.cs:72` is `bool place = boxes[i].AllowOverlap;`, so a hand-built box with `AllowOverlap = true` already survives against **un-fixed** code. T9 tested existing `AllowOverlap` semantics, never the extractor. The replacement runs the **extractor** and feeds its real output to `LabelCollision`. |
| D2: mid-arc is "stable under tile clipping" | Claim **dropped**. Anchor-clip semantics are now stated explicitly (§3 D2) with a recorded loss case and a tooth | Arc length is a function of the *buffered* decoded path; changing the clip changes the midpoint by definition. A buffer-dominated path can lose its only anchor to the `[0, extent)` clip. |
| Teeth over one shield layer (`highway-shield-non-us`) | All **three** shield layers, via synthetic `transportation_name` features + atlas entries | The Berlin fixture carries no US-network features, so `highway-shield-us-interstate` / `road_shield_us` were entirely untested — and their `icon-image` expressions and step stops differ. |
| `symbol-placement` described as "zoom-evaluated" | Always qualified as **build-zoom**-evaluated, and pinned by a tooth | The property is evaluated once, at the tile's build zoom; an overzoomed tile keeps its old placement. This is the visible z11 pop. |

Four attack axes were tried and **held** — see §8.

---

## 1. The evidence

A probe ran the real `SymbolFeatureExtractor` over the real fixture
`Assets/Fixtures/boundary-9-274-168.pbf.bytes` with the real liberty `highway-shield-non-us` layer and a
synthetic atlas holding `road_1..road_6`:

```
[A] selected features = 10          -> all LineString; filter + data fine
[B] parsed symbol-placement = Point -> the step expression was never evaluated
[C] labels emitted (as-shipped) = 0
[D] forced placement="line" -> total=20  text=20  icon=0
[E] icon-image -> 'road_5'/'road_4'/'road_3'  inAtlas=True  (all 10 features)
```

### Ruled out — do not re-investigate

| Suspect | Why it is not the cause |
|---|---|
| `icon-text-fit` | Liberty uses **pre-sized** sprites `road_1..road_6` keyed by `ref_length`, so the sprite already fits its number. Text-fit is never needed for this style. |
| Sprite availability | All 18 shield sprites exist in the live openfreemap sheet. |
| `concat` / number formatting | `Value.FormatNumber` uses `"R"`, so `5` → `"5"`; probe [E] resolves real names that hit the atlas. |
| The style `filter` | 10/10 features selected (probe [A]). |

### The three layers, as authored

Verified against `Assets/StreamingAssets/Fixtures/liberty.json`. All three share `source-layer:
transportation_name`, `text-field: ["to-string", ["get","ref"]]`, `text-rotation-alignment: viewport`,
`icon-rotation-alignment: viewport`, `symbol-spacing: 200`, `text-size: 10`, `icon-size: 1`, and a
`["<=", ["get","ref_length"], 6]` + LineString/MultiLineString filter. They differ where it matters:

| Layer | minzoom | `symbol-placement` | `icon-image` resolves | `network` filter |
|---|---|---|---|---|
| `highway-shield-non-us` | 8 | `["step",["zoom"],"point",11,"line"]` | `road_<ref_length>` | **not** in {us-highway, us-interstate, us-state} |
| `highway-shield-us-interstate` | 7 | `["step",["zoom"],"point",7,"line",8,"line"]` | `us-interstate_<ref_length>` | == us-interstate |
| `road_shield_us` | 9 | `["step",["zoom"],"point",11,"line"]` | `us-highway_…` / `us-state_…` | in {us-highway, us-state} |

Two consequences the first draft missed: the interstate layer's step boundary is **z7**, not z11 (so a
z11-only tooth never exercises its expression), and its icon names come from a *different* `concat` shape
(`["get","network"]` rather than a literal prefix). Both are now covered — §5 T1/T3.

---

## 2. The six gates

Six independent defects sit in series. Fixing any subset short of all six still renders no shield (or
renders something visibly worse than today).

| # | Where | What it does |
|---|---|---|
| **G1** | `Core/Style/Symbol/LayoutProperties.cs:182` | `symbol-placement` is read via `.AsString(null)`. The shields' step expression is a JSON **array**, so `AsString` yields `null` and the property degrades to `Point` at every zoom. Only these 3 symbol layers use an expression here; the other 7 line-symbol layers use a literal `"line"` and work. |
| **G2** | `Core/Style/Symbol/SymbolFeatureExtractor.cs:87` | `feature.GeometryType != wantGeometry` drops every LineString when placement is `Point`. Shield features are all LineString ⇒ 0 labels (probe [C]). |
| **G3** | `SymbolFeatureExtractor.cs:107` | `if (!isLine && spriteAtlas != null)` — icons are skipped entirely under line placement (an I3 scope fence, documented in `Extract`'s XML doc). So even with G1+G2 fixed, at z ≥ 11 the shield sprite never emits (probe [D]: `icon=0`). |
| **G4** | `SymbolFeatureExtractor.cs:151` + `Core/Text/Placement/LabelStagingMath.cs:289` | Line placement **always** produces a curved along-line label. `RotationAlignment` is stamped only on the point path (`:253`, `:270`), `CurvedStageInput` carries no alignment field, and `StageCurvedAnchor` computes `rotation = tangent + flip` unconditionally. The shields' `text-rotation-alignment: viewport` / `icon-rotation-alignment: viewport` are therefore ignored: road numbers would curve along the road instead of standing upright inside a shield. |
| **G5** | `Core/Text/Placement/LabelCollision.cs:33` (`ComparePlacementOrder`) + `SymbolFeatureExtractor.cs:235/257` | A shield's number sits **centred on** its sprite, so the text box and the icon box always overlap. The extractor emits text at ordinal *N* and icon at *N+1*; both carry `symbol-sort-key` 0 and neither sets `*-allow-overlap` / `*-ignore-placement`; the greedy order is `(sortKey, FeatureIndex, TileKey)`. So **the text always places and the icon is always dropped** — at every zoom, under both placements. Without addressing G5 the fix ships bare road numbers, which is the reported bug. **D5 mitigates this; it does not fix it** — see D5. |
| **G6** | `Unity/Text/SymbolLabelSubsystem.cs:433` (kick-time capture) + `:651-652` (late assignment) | The style's sprite sheet is fetched **asynchronously and independently** of tile loading. `TryBeginBuild` captures `_spriteAtlas` at kick time; it is `null` until the fetch resolves, and a build with a null atlas extracts **no icon labels at all**. When the fetch completes, nothing invalidates the tiles already built — the property doc at `:252-255` admits the tile only "self-heals on its next rebuild", and there is **no re-kick path** for an already-built, still-in-cover tile (verified: `TryBeginBuild` is called from exactly one line, `TileManager.cs:1434`, reached only when a record enters `_loaded` with a ready decode; `InvalidateCover()` skips loaded records; `ReconcileLoadedTiles` never rebuilds; a prepared-cache re-entry restores the *old* icon-free labels verbatim). At demo startup the tile fetch commonly beats the sprite fetch, so the z13 view shows bare numbers **permanently**, with G1–G5 all fixed. |

> **G5 and G6 were not in the original brief.** G5 was found while sizing G3/G4; G6 was found by the
> adversarial review. Both are load-bearing: G1–G4 alone produce numbers with no shields, and G1–G5
> without G6 produce numbers with no shields *at startup, which is the only view the maintainer sees*.

---

## 3. Decisions

### D1 — `symbol-placement` becomes a **build-zoom**-evaluated `StyleProperty<SymbolPlacement>` (fixes G1)

`LayoutProperties.SymbolPlacement` changes type from the bare enum to `StyleProperty<SymbolPlacement>`,
matching the file's own stated convention ("zoom-capable properties are `StyleProperty<T>`"). The projector
is `v => ParsePlacement(v.ToDisplayString())`, so the existing "absent / unrecognized → point" contract is
preserved verbatim, and a non-string expression result degrades to point rather than throwing.
`StepExpression` already supports string outputs (`ExpressionParser.ParseStep` folds any expression as an
output; `Value.String` round-trips through `ToDisplayString`), so no expression-engine work is needed.

**Say "build zoom", never just "zoom-capable".** The extractor evaluates placement **once, at the tile's
build zoom**, and the result is frozen into the emitted labels for that tile's lifetime.
`SymbolFeatureExtractor.cs:58` establishes that layer *visibility* is deliberately re-evaluated at
**display** zoom against the live camera, precisely because tiles are overzoomed rather than rebuilt.
Placement is not. This is consistent with `text-size`, `symbol-sort-key` and every other
build-zoom-evaluated property (see `docs/maplibre-spec.md`), but shields straddle a step boundary, so this
is the one property where the staleness is user-visible.

**Recorded consequence (accepted):** while the camera crosses a step boundary the *same style layer runs
two placement modes at once* — tiles built below it keep one mid-arc shield per path, tiles built above it
show repeated shields at `symbol-spacing` — and the older tiles visibly change density and position when
they are eventually rebuilt. Boundaries: z11 for `highway-shield-non-us` and `road_shield_us`, z7 for
`highway-shield-us-interstate`. This is **pinned by a tooth** (§5 T15), not left as prose, and a real fix
is a display-zoom re-evaluation stage (§6). The demo's initial z13 build is unaffected — it is a fresh
build above every boundary.

### D2 — point placement on a LineString anchors at **mid-arc** (fixes G2)

Under `symbol-placement: point`, a LineString feature now yields one label per line string, anchored at the
line's **mid arc-length**, reusing `LineAnchorPlacement.Compute(path, _, SymbolPlacement.LineCenter)` — an
existing abstraction that already answers exactly this question ("where is the middle of this tile-space
path"), rather than a second, parallel walk.

The Style Spec defines `point` as "the label is placed at the point where the geometry is located", which is
underspecified for a line; this is **our** choice, made on merit: mid-arc does not pile shields onto tile
edges the way a first-vertex anchor would. **Deviation risk:** upstream may anchor elsewhere (e.g. the
first vertex); if a parity screenshot disagrees, this is the one line to change.

**Anchor-clip semantics — stated explicitly (the claim the first draft got wrong).** Anchors are computed
on the **buffered source path exactly as decoded** by `MvtGeometry.Decode` — *not* on a path clipped to the
tile. That is deliberate: it is the same input the curved path already uses (`SymbolFeatureExtractor.cs:146`),
so D4's "the anchors are the same topology the curved path computes" property holds byte-for-byte, and no
new polyline-clipping operation enters the stage. The existing single-world clip
(`SymbolFeatureExtractor.cs:228` — a resolved anchor outside `[0, extent)` is a world-copy/buffer duplicate)
is then applied **per resolved anchor**, unchanged.

Two consequences, both accepted and both pinned by teeth (§5 T14):

- **A buffer-dominated path can lose its only shield.** Under *point* placement there is exactly one
  anchor, so the clip is all-or-nothing: a path running from `x = -1000` to `x = 100` has its mid-arc at
  `x ≈ -450`, outside `[0, extent)`, and emits nothing — even though it has an in-tile segment. The
  neighbouring tile's independently clipped/buffered copy has a different start and a different midpoint,
  so cross-tile recovery is not guaranteed. Confined to point placement, i.e. **below** each layer's step
  boundary; at the demo's z13 all three layers are on the line path.
- **The mid-arc anchor is not invariant under a change of source clipping/buffering.** Arc length is a
  function of the buffered path, so re-tiling or a different buffer moves it. The first draft's "stable
  under tile clipping" claim is **withdrawn**; the real property is "deterministic for a given decoded
  path", which is all the teeth need.

Under *line* placement the same clip applies per anchor, which is milder: anchors at `spacing·(k+0.5)`
falling in the buffer drop, in-tile ones survive. The resulting near-seam repeat/gap is the already-recorded
cross-tile shield limit (§6).

**Fence: LineString only, NOT Polygon.** Liberty's `place` / `airport` / `poi_transit` symbol layers carry
**no** geometry-type filter and rely on their source layer being point-only. Widening point placement to
accept Polygon would put new labels on those layers; widening to LineString does not, because OpenMapTiles
`place` / `aerodrome_label` / `poi` carry no line geometry. (`water_name_point_label` and `poi_r1/r7/r20`
*are* geometry-type guarded.) MapLibre does support point placement on polygons via a centroid — that is a
**deliberate, recorded gap**, not an oversight.

### D3 — alignment resolution: `auto` → `map` under line placement, `viewport` under point

New engine-free helper `Core/Text/AlignmentResolution.Resolve(AlignmentMode, SymbolPlacement)`. The spec's
`auto` default resolves to `map` for `line`/`line-center` and to `viewport` for `point`.

This is not cosmetic: `waterway_line_label`, `water_name_line_label` and `road_one_way_arrow*` leave
rotation-alignment **unset**. If `auto` resolved to viewport under line placement, those layers would flip
from curved to upright. The resolver is pinned by a test, not a comment.

### D4 — line placement + viewport-resolved alignment ⇒ **upright labels at the line anchors** (fixes G3+G4)

The reframe of G4: this is *not* "stamp `RotationAlignment` onto curved labels". Under `symbol-placement:
line` with rotation-alignment resolving to `viewport`, MapLibre places symbols **along** the line at
`symbol-spacing` intervals but lays each one out as an ordinary upright block — which is exactly the road-shield
look. So the extractor emits, per computed `LineAnchor`, ordinary **point-placement** labels (text and/or
icon) at the anchor's projected position, instead of one curved label per path.

Consequences, all of them wins:

- G3 dissolves. Icons under line placement are no longer a new code path — they are the existing point-icon
  path at a different anchor, so staging, collision, world-anchored draw, cross-tile dedup, fade identity and
  the sprite atlas all work unchanged.
- Curved text is untouched for every map-aligned layer (`highway-name-*` set `text-rotation-alignment: map`
  explicitly; `waterway_line_label` / `water_name_line_label` resolve `auto` → map per D3).
- The anchors are the same `LineAnchorPlacement` topology the curved path already computes, so
  `symbol-spacing` keeps its meaning. This is why D2 keeps anchors on the buffered path.

**~~Fence: map-aligned line icons stay unbuilt.~~ LIFTED by P-B** (see
`docs/labels-and-symbols-design.md` §6). `road_one_way_arrow` / `road_one_way_arrow_opposite` (icon-only,
`symbol-placement: line`, alignment `auto` → **map**, minzoom 16) are now emitted as **one-glyph curved
labels** — the icon quad rides the curved path's per-anchor staging and is rotated to the line tangent by
the icon shader's ported Stage-AC branch. The fence's premise ("a genuinely new staging path") turned out
to be false: an `icon-anchor: center` quad IS a `CurvedGlyph.Cell`, so no new record kind, no new gather,
no new oracle. T8's "a map-aligned line icon never emits" assertion is REVERSED there (tooth A2), not
re-baked. The viewport-resolved case (D4, below) is untouched.

### D5 — the centred pair: the icon owns the collision, the text is a **non-blocking passenger** (mitigates G5)

**This is a named approximation, not a fix, and not "one symbol".** MapLibre treats a symbol's icon and text
as **one instance** (`icon-optional` / `text-optional` default false ⇒ they place or drop together, with a
combined box). This codebase models them as two independent collision candidates, and
`WorldLabelRenderer.Emit` keys its mesh slot by `(TileKey, Slot, AtlasKind)`, so a single candidate cannot
span the glyph atlas and the sprite atlas. Real pairing therefore means either a candidate-level instance
link or a per-**quad** `AtlasKind` with a split emit — both reach into the Burst staging/collision path and
the SoA batch. Too much for this stage. **No flag-only, extractor-only formulation can make two independent
candidates place and drop atomically** — that is a proven property of the collision model, not a gap in
effort, and it is why the claim is downgraded rather than the scope widened.

**What we do instead**, scoped by a principled predicate: when a feature yields **both** a text and an icon
label at the same anchor **and the text is centred on the icon** — `text-anchor` = center (the default),
`text-offset` = 0, `text-radial-offset` = 0, `icon-anchor` = center, `icon-offset` = 0 — the pair is treated
as icon-owns-collision:

- the **icon** is emitted **first** and is the collision owner (normal box, normal blocking);
- the **text** rides along with **both** `AllowOverlap = true` **and** `IgnorePlacement = true`.

`AllowOverlap` alone was the first draft's choice and it was strictly worse. `LabelCollision.cs:72-83`
places an `AllowOverlap` candidate unconditionally **and still inserts it as a blocker** unless it ignores
placement (`:79`, `:123`). So an `AllowOverlap`-only passenger could *drop unrelated later labels* — it
changed the survivor set for labels that have nothing to do with the shield. `IgnorePlacement = true` costs
nothing, is authored by no shield layer today, and removes that harm entirely: the passenger text is placed
unconditionally and blocks no-one.

**Recorded deviations — the accepted, tested behaviour:**

- If the icon is dropped by collision, its number still shows: a **bare road number**. MapLibre would drop
  both. This is the residual harm that no flag can remove, and it is pinned by a three-candidate tooth
  (§5 T12) so it is a recorded decision rather than a latent surprise.
- The passenger text can never be dropped by collision, so a shield's number can overlap a neighbouring
  label in a dense scene. It is also a survivor every frame, so the fade/incumbency machinery treats it as
  continuously placed rather than fading out with its icon.
- The icon and text keep **independent** cross-tile identities (`SymbolTileLabelBlockBaker.cs:48-76`,
  `CrossTileLabelKey.cs:10-16`). Both dedup scans normally pick the same finest tile, so the pair stays
  together; D6 removes the one mechanism (a tile with text but no icons) that could have made them diverge.

**Predicate breadth — attacked and held.** The predicate selects **exactly** the three shield layers across
liberty as authored, and nothing else: every other layer carrying both `icon-image` and `text-field` offsets
or re-anchors its text (`poi_r*` `text-offset [0, 0.6]` + anchor top, `poi_transit` `[0.9, 0]` + anchor
left, `airport` `[0, 0.6]` + top, `label_town/village` anchor bottom, `label_city*` anchor bottom +
offset). Independently re-checked by the adversarial pass (§8). So no existing layer's emit order,
`FeatureIndex` ordinals or fade ids change. Note `text-radial-offset` is evaluated per feature, so a
*future* style could author an expression evaluating to 0 and trip the predicate — no liberty layer authors
it at all.

Proper symbol-instance pairing is §6.

### D6 — a symbol build **waits for the sprite fetch to settle**; it never commits icon-starved (fixes G6)

The defect is a race, so the fix is an ordering constraint, not an invalidation protocol. Three mechanisms
were considered; the chosen one is the least invasive that is also correct.

| Mechanism | Rejected because |
|---|---|
| Rebuild the affected tiles when the atlas lands | No decode survives the kick (`SharedTileDecode`'s own doc: retention is plain GC reachability, dropped with the completing kick task), and `TileScheduler.Release` evicts bytes. A rebuild therefore needs a new TileManager re-kick API, a re-fetch, a re-decode and a per-frame budget — a second epic, and it churns meshes for a label-only problem. |
| Block `MapView.SetStyle` on the sprite fetch before wiring sources | Simple, but a slow or hung sprite endpoint would then blank the **entire** map — fills and lines included — for a defect that only affects labels. |
| **Park the symbol worker phase, holding the decode, until the fetch settles** ✅ | Self-contained in `SymbolLabelSubsystem`; no TileManager change, no re-fetch, no mesh churn, extraction still runs off-main. |

**The mechanism.** `SymbolLabelSubsystem.SetStyle` already starts the fetch
(`FetchSpriteSheetAsync(style, _buildCts.Token)`, `:378`). Keep its result as a `.Preserve()`d `UniTask`
and read readiness the way this class already reads its reconcile worker
(`_reconcileTask.Status == UniTaskStatus.Pending`, `:819`):

```
private bool SpritesSettled => _spriteFetchTask.Status != UniTaskStatus.Pending;
```

`TryBeginBuild` (main thread, where `_spriteAtlas` is already read) checks it. If **not** settled, the
returned pass **parks** on the pool thread — enqueueing its decode and captured context into a second
`ConcurrentQueue` instead of running the extract. `PumpBuilds` (main thread, every frame) drains that queue
once `SpritesSettled`, constructing each build's `TileSymbolLayerProcessor[]` **then**, with the live
`_spriteAtlas`, and dispatching the worker phase to the pool exactly as the kick would have.

Four properties that make this correct rather than merely plausible:

- **Settled ≠ non-null.** The predicate is "the fetch reached a terminal state", which includes *no
  `sprite` URL*, *404/204* (`!resp.HasData`), a fault, and cancellation — every path
  `FetchSpriteSheetAsync` already handles. A style with no sprites therefore **never parks**: the method
  returns before its first `await` when `SpriteSourceFactory.Create` yields null, so the task is already
  terminal when `SetStyle` returns, and `default(UniTask).Status` is `Succeeded` before any style is set.
  A "wait for `_spriteAtlas != null`" predicate would hang forever on a 404 — that is the obvious wrong
  version of this fix, and it has its own tooth (§5 T11).
- **Every thread-boundary decision stays on main.** Readiness is read in `TryBeginBuild` and `PumpBuilds`,
  both main-thread; the pool side only enqueues into a `ConcurrentQueue`, the same safe-publication carrier
  `_handoffQueue` already is (`:183`). No volatile flag, no lock.
- **Cancellation is unchanged.** Parked entries carry the build's `_buildCts` token and their reserved
  store slot; `SetStyle`/`DoDispose` drain the pending queue exactly where they drain `_handoffQueue`, and
  the slots die with `_store.Clear()`.
- **The cost is a bounded, one-off latency**, not a stall: labels for tiles kicked during the fetch window
  appear when the sheet lands (one HTTP request, in parallel with the tile fetches). The retained decodes
  are bounded by the tiles kicked in that window — the initial cover — and are memory those kicks already
  held. Their tails still trickle at `MaxBuildsPerFrame`, so the existing budget paces the main-thread half
  with no new knob. Same class of accepted tradeoff as A5b's tail budget (`:510-514`).

**Review addendum — the bound (`SpriteFetchDeadlineSeconds`), and the qualifier it puts on the invariant
above.** "No tile is ever committed icon-starved" holds **while the fetch is still in flight** — that is
the property D6 exists to guarantee, and it is unconditional for every normal terminal path (resolved,
absent, faulted, cancelled, no `sprite` URL), all of which settle in well under the bound. It does NOT hold
once the fetch has been waited on past a bounded deadline: `UnityWebRequestSpriteSource` sets no HTTP
timeout, so a genuinely hung endpoint (connects, never responds — NOT the 404/204 path) would otherwise
leave `_spriteFetchTask` `Pending` forever, making the parking PERMANENT — zero symbol labels ever, for the
whole style, plus an unbounded `_pendingSpriteQueue` (every parked build retains its
`IDecodedTileHandle`). `SpriteFetchDeadlineSeconds` (**8s**, `internal const`
`SymbolLabelSubsystem.SpriteFetchDeadlineSeconds`) bounds that: `SpritesSettled` also goes true once that
many seconds have elapsed since the fetch started (`SymbolLabelSubsystem.NowSeconds`, a test-overridable
clock — `NowSecondsOverride` — mirroring `GlyphSourceFactoryOverride`/`SpriteSourceFactoryOverride`),
regardless of the fetch's own status. Once tripped, `PumpBuilds`' existing pending-drain (already tolerant
of a null `_spriteAtlas` — the T11 path) dispatches every parked build with whatever atlas state exists — a
**deliberate, bounded fallback to the PRE-D6 floor** (possibly icon-starved, never self-healing for that
tile) instead of a permanent stall. So the precise invariant is: no tile commits icon-starved before the
deadline; after it, an icon-starved commit is the accepted, tested degradation for a fetch that never
terminates, not an oversight (§5 T16 pins it).

**Tuning the deadline (maintainer note).** 8s is a starting value, not a measured one. It may be **raised**
(10–15s) if a real deployment's sprite endpoint is legitimately slow — over-waiting only costs the
already-broken hung-endpoint case, which is rare. It must **not** be lowered: under-waiting costs the
normal-but-slightly-slow case, which is the exact bug D6 exists to fix (a tile committing icon-free because
the atlas hadn't arrived yet). Prefer raising it over adding a second knob.

**Deliberate deviation from the brief's phrasing.** The brief asked for "an already-committed tile gains its
icons after a delayed sprite source resolves". Under D6 no tile is committed icon-starved **while the fetch
is in flight**, so the tooth asserts the stronger property: the tile commits **nothing** while the source is
gated, and commits **with icons** once it resolves — with no pan, zoom, restyle or second kick (§5 T10).
That falsifies the un-fixed behaviour more decisively than the literal form, which could be satisfied by a
rebuild that flickers. (The one case where a tile DOES commit icon-starved — the deadline fallback — is a
distinct, separately-pinned decision, §5 T16, not a hole in this claim.)

---

## 4. Why all six ship together

The demo scenes (`Assets/Scenes/Demo/MapDemo/MapDemo.unity:492`,
`Assets/Scenes/Demo/MapDemo/OpenStreetMapLiberty.unity:492`) start at `InitialZoom: 13`, Berlin. At z13 the
step yields `"line"` for all three layers. So:

- G1+G2 alone → the maintainer's actual view still shows nothing new at z13 (line placement, icons fenced),
  and at z ≤ 10 shows curved-free numbers with no shield.
- G1+G2+G3 without G4 → shields upright at anchors but numbers curving along the road: the number is not in
  the shield.
- G1–G4 without G5 → every shield sprite loses the collision to its own number: bare numbers, which is
  indistinguishable from a new bug.
- G1–G5 without G6 → at demo startup the tiles win the race against the sprite fetch, so the extractor
  resolves **no icons at all**: bare numbers again, permanently, in the only view the maintainer opens.

One stage, six gates.

---

## 5. Invariant & acceptance

**Invariant:** no symbol layer other than the three shield layers changes its output, and no style without
a sprite sheet changes its build timing. Concretely: every layer whose rotation-alignment resolves to `map`
under line placement still emits exactly one curved label per path with the same field values; every
point-placement layer keeps its emit order (text then icon) and its `FeatureIndex` ordinals (D5's predicate
is false for all of them); a style with no `sprite` URL never parks a build (D6); no snapshot is re-baked.

**Teeth.** Engine-free unless marked **[Unity]**; engine-free ones are compiled by both the Unity EditMode
runner and `Tools/core-tests`. Counts are structural or independently derived in-test — never baked totals.

| Tooth | Assertion | Fails against un-fixed code because |
|---|---|---|
| T1 | each of the three shield layers' `SymbolPlacement` evaluates `Point` below its own step stop and `Line` above it — non-us and `road_shield_us` at z10/z11, interstate at **z6/z7**; a literal-`"line"` layer is `Line` at both | it is always `Point` |
| T2 | `Extract` at a below-step zoom over the fixture yields > 0 labels, all `Placement == Point`, icon count == text count, **both > 0** | yields 0 |
| T3 | at z13 **`iconCount > 0`** (asserted explicitly, not as a universal over a possibly-empty set), every icon label `Kind == Icon`, and each shield layer's icon names match its own expression: `road_N` / `us-interstate_N` / `us-highway_N`\|`us-state_N` | yields 0 icons |
| T4 | at z13 the shield layer's labels are `Placement == Point` at line anchors with `PathRender == null`, not one curved label per path | one curved label per path |
| T5 | the shield layer's output is consecutive **(icon, text) pairs**: for every pair, icon `Kind == Icon` then text `Kind == Text`, `iconFeatureIndex + 1 == textFeatureIndex`, identical `AnchorRender`, text `AllowOverlap && IgnorePlacement`, icon neither. **No global `AnchorRender`-uniqueness assumption** — two features may legitimately share an anchor | text is emitted first, both flags false |
| T6 | `AlignmentResolution.Resolve` — `(Auto, Line)`/`(Auto, LineCenter)` == `Map`, `(Auto, Point)` == `Viewport`, `Map`/`Viewport` pass through | the helper does not exist (guards the fix) |
| T7 | map-aligned line layer, **non-empty output** required: `labels.Count` equals the independently counted number of eligible decoded paths (≥ 2 points), and one identified record matches field-for-field on paint, `SpacingPx`, `MaxAngleDeg`, `KeepUpright`, `AllowOverlap`, `IgnorePlacement`, `TranslatePx`, `TranslateAnchor`, `SortKey`, contiguous `FeatureIndex` ordinals, `PathRender.Length`, non-empty `LineAnchors` | — (guards the invariant; a regression that drops every map-aligned label now fails) |
| T8 | a map-aligned **icon** line layer emits zero icon labels with an atlas supplied (the surviving fence), with > 0 features selected | — (guards the fence) |
| T9 | **extractor → collision**: run the real extractor over a centred-pair tile, build `LabelBox`es from its emitted labels' own flags/ordinals with two overlapping boxes, run `LabelCollision.SelectSurvivors`; assert both survive **and** that the emitted order is icon-then-text | un-fixed emits text-then-icon with both flags false, so the collision drops the icon. (The old hand-built version of this tooth was **disarmed** — see §0) |
| T12 | **three candidates**: a higher-priority blocker A, the shield icon I, its passenger text T, all overlapping, plus a later label B. Assert A places, **I is dropped**, **T places anyway** (the accepted bare number), and **B still places** (T does not block it) | the `IgnorePlacement`-less version drops B; pins the D5 approximation as a decision |
| T13 | the two US shield layers select **zero** features from the Berlin fixture | — (records *why* synthetic features exist; alarms if the fixture is ever swapped) |
| T14 | anchor-clip semantics: a synthetic path from `x = -1000` to `x = 100` under point placement emits **zero** labels (mid-arc in the buffer — the accepted loss); a path whose mid-arc is in-tile emits exactly one, at the mid-arc point; under line placement, only the in-tile anchors of an edge-crossing path emit | — (pins the semantics D2 now states) |
| T15 | the same synthetic tile extracted at zoom 10.9 vs 11.0 yields the point-placement shape vs the line-placement shape — placement is a function of the **passed build zoom** only | — (pins the D1 known limit as behaviour, not prose) |
| T10 **[Unity]** | a gated sprite source: drive one tile build; assert **no labels committed** while gated and **exactly one** `TryBeginBuild` call; release the gate, pump; assert the same tile's labels now contain `Kind == Icon` — no restyle, no pan/zoom, no re-kick | un-fixed commits text-only immediately and never gains icons |
| T11 **[Unity]** | the same drive, but the gate resolves with `HasData == false` (a 404): the parked build still drains and commits its **text** labels | catches the obvious wrong fix (waiting on `_spriteAtlas != null`), which hangs forever |
| T16 **[Unity]** | (added post-review, REQUIRED 1) a gate that is **never** resolved — a NEVER-completed `UniTaskCompletionSource<SpriteResponse>`, simulating a hung endpoint with no HTTP timeout: drive one tile build; assert no labels commit and the pending queue holds it while under `SpriteFetchDeadlineSeconds`; advance the (test-overridden) clock past the deadline WITHOUT ever resolving the gate; assert the build now commits (text, no icon) **and** the pending queue drains to zero | before the deadline existed, this scenario parked forever — zero labels ever, unbounded `_pendingSpriteQueue` growth |

T10/T11/T16 are **[Unity]-only by necessity**: `SymbolLabelSubsystem` is engine-bound (`Texture2D`, `Camera`,
`Debug`) and `FetchSpriteSheetAsync` hops `UniTask.SwitchToMainThread()` before constructing a `Texture2D`,
so they must be `[UnityTest]` and must **not** be added to `Tools/core-tests/core-tests.csproj`. All three
freeze `SymbolLabelSubsystem.NowSecondsOverride` in `[SetUp]` so their frame-pump loops never race real
wall-clock time against `SpriteFetchDeadlineSeconds` — a deadline trip mid-test on a slow CI machine would
otherwise fail looking exactly like a real D6 regression.

---

## 6. Deferred / open

| Item | Why it is out of scope |
|---|---|
| ~~**True symbol-instance pairing**~~ | **NO LONGER DEFERRED — this is §10 (stage 3).** The row's original reasoning (an instance link *or* a per-quad `AtlasKind` split emit) turned out to be a false dilemma: the pair is one `LabelCandidate` spanning both halves' boxes with a per-candidate EMIT RANGE, so `WorldLabelRenderer.Emit` is not touched at all. D5/T12's accepted bare number is **reversed** there. |
| ~~**Map-aligned line icons** (`road_one_way_arrow*`)~~ | **NO LONGER DEFERRED — built as P-B** (`docs/labels-and-symbols-design.md` §6). Emitted as one-glyph curved labels + `icon-rotate`; D4's fence is lifted and T8 is reversed into tooth A2. |
| **Point placement on Polygon** (centroid / pole of inaccessibility) | Would add labels to unguarded `place` / `airport` / `poi_transit` layers. Deliberate gap (D2). |
| **Display-zoom re-evaluation of `symbol-placement`** | The D1 known limit — the visible step-boundary pop, pinned by T15. Belongs with the wider "camera-property re-evaluation" epic. |
| **Mid-arc anchors over the tile-clipped path** | Would recover the buffer-dominated short paths T14 pins as lost, but needs a polyline clip and would desync D4's anchors from the curved path's. |
| **Rebuilding tiles on a sprite-atlas change** | D6 makes it unnecessary for the startup race (the only live case: the atlas changes exactly once per style). A general "invalidate these tiles" TileManager API is a separate epic — none exists today. |
| **`icon-text-fit`** | Not needed by liberty (pre-sized `road_N` sprites) and unrelated to this bug. |
| **Cross-tile shield dedup along a line** | A road crossing a tile boundary gets independent anchors per tile, so a shield can repeat or gap near the seam. Same class of known limit as the existing cross-tile line-label behaviour. |
| ~~Maintainer eyeball at z6/z7 (interstate), z10/z11 (non-us) and z13 on `OpenStreetMapLiberty.unity`~~ **DISCHARGED 2026-07-30 — shields render.** | Headless teeth cannot see a rasterized shield, and the step-boundary pop (T15) is a *visual* judgement about whether the accepted limit is tolerable. |

---

## 7. Grounding (touch points)

Core: `Style/Symbol/LayoutProperties` (`SymbolPlacement`, `ParsePlacement`, `ParseAlignment`),
`Style/Symbol/SymbolFeatureExtractor` (`Extract` — the placement/geometry gate, the icon fence, the line and
point branches), `Style/Symbol/IconImageResolver`, `Text/AlignmentMode` (+ the new `AlignmentResolution`),
`Text/Placement/LineAnchorPlacement` / `LineAnchor`, `Text/Placement/LabelCollision`,
`Text/Placement/LabelStagingMath` (`StageCurvedAnchor`). Unity: `Text/SymbolLabelSubsystem`
(`TryBeginBuild`, `SymbolTileWorkerPass.RunWorkerAndHandoff`, `PumpBuilds`, `FetchSpriteSheetAsync` — D6),
`Rendering/Tile/Processing/TileSymbolLayerProcessor` (the atlas is a ctor arg — D6 defers its construction),
`Text/StyledSymbolTileBuilder` (the `Placement == Point` vs curved branches),
`Text/Placement/WorldLabelRenderer` (`Emit`'s `(TileKey, Slot, AtlasKind)` slot key — the constraint behind
D5). Style: `Assets/StreamingAssets/Fixtures/liberty.json` (the 3 shield layers). Related:
`docs/labels-and-symbols-design.md` §3 (curved along-line text) and §5 (icon support — the I3 fence this
lifts), `docs/maplibre-spec.md` (symbol support matrix).

---

## 8. Confirmed by the adversarial pass (attacked, held)

Recorded so a later reader knows these axes were probed and did not move:

- **D5's predicate breadth is correct for current liberty.** Independently re-checked layer by layer over
  every layer carrying both `icon-image` and `text-field`: each non-shield one has a non-center text anchor
  or a non-zero offset (`poi_r*` `liberty.json:4366-4425`, `poi_transit` `:4663-4711`, the place labels
  `:5360-5421`, `:5444-5505`, `:5612-5679`). No current-liberty false positive exists.
- **D1's reader migration is complete.** A repository-wide search finds exactly one production reader of
  `LayoutProperties.SymbolPlacement` (`SymbolFeatureExtractor`) plus `SymbolStyleLayerTests`. Nothing else
  breaks on the type change.
- **D2's reuse of `LineCenter` is mechanically safe.** `LineAnchorPlacement.Compute` returns one valid
  `(Segment, T)` for every non-degenerate path and an empty array for a degenerate one. The defect the
  review found was the *semantic* clipping claim, now fixed — not an invalid segment index.
- **D4's `Auto → Map` guard is the right branch order.** Resolving alignment *before* choosing the emit
  shape is what stops liberty's unset-alignment waterways and one-way arrows from flipping to
  viewport-upright. No current liberty non-shield icon+text layer uses line placement with viewport
  alignment.
- **`AllowOverlap` plumbing is inert.** The flag rewrites no sort keys, fade ids, dedup keys or SoA layout;
  its only effects are the survivor bit and (absent `IgnorePlacement`) blocking — which is exactly why D5
  now sets both.

---

## 9. Stage 2 — G7: a symbol layer's icon and text share one render queue

Everything above landed as one stage (`b6590feb`). The maintainer then eyeballed the demo: **the shields do
render**, but wrongly — and *stably* wrong, no flicker. Two visually distinct symptoms, from two independent
causes. Only the first is this stage.

| Symptom seen | Cause | Status |
|---|---|---|
| The **badge alone** — the shield sprite paints over its own road number | **G7**, below: icon and text draw at the *same* `renderQueue`, coplanar, ZWrite off ⇒ no tiebreak, and when the icon happens to draw last it covers the number | **fixed by this stage (D7)** |
| The **number alone** — no badge behind it | The **G5 / D5 collision residual**: the icon and its passenger text are two independent collision candidates, and the icon can still lose. §2 G5 and §3 D5 state this | **NOT this stage — fixed by §10 (stage 3)** |

> **Read this before the next eyeball.** After this stage some shields will *still* show as a bare number.
> That is the documented D5 approximation, not a regression and not a half-applied fix. What must be gone
> after this stage is the opposite symptom: a badge with its number painted out.

### G7 — the defect, in source

| Site | What it does |
|---|---|
| `Core/Rendering/LayerDrawOrder.cs:55` | `QueueFor(drawIndex) => TransparentQueue + drawIndex` — stride **1**: layer *i* owns exactly one queue value, `3000 + i`. |
| `Unity/Rendering/Style/RenderLayerSet.cs:97` | `layer.Material.renderQueue = LayerDrawOrder.QueueFor(drawIndex)` — the one generic per-layer write. |
| `Unity/Rendering/Style/SymbolRenderLayer.cs:43` | `Material => WorldTextMaterial` — a symbol layer's `Material` **is** its world-text material, so the line above sets the *text* queue. |
| `Unity/Rendering/Style/SymbolRenderLayer.cs:105` | `worldIconMat.renderQueue = LayerDrawOrder.QueueFor(drawIndex)` — the **same value** as the text. |

A shield's icon quad and text quad are coplanar at the same anchor (D5 centres the number on the sprite),
both transparent, both ZWrite off. With identical queues Unity's remaining tiebreaks (camera distance, then
internal renderer order) decide — deterministically per frame for a fixed scene, hence a *stable* wrong
result rather than flicker. When the icon resolves after the text, the sprite's opaque badge paints over the
number it exists to frame.

The mechanism has no way to express "below its own layer's text but above every layer beneath": under stride
1 the only value between layer *i*'s text and layer *i−1*'s is layer *i*'s own — there isn't one.

### D7 — a layer owns a **band** of sub-slots; symbols put the icon under their own text

`LayerDrawOrder` stays the one formula home (its stated role). It gains a named sub-slot concept instead of
a bare `* 2`:

```csharp
public enum LayerSubSlot { Base = 0, Above = 1 }      // ordering role WITHIN one layer's band
public const int SubSlotsPerLayer = 2;                // == the enum's value count == the queue stride
public const int QueueCeiling     = 5000;             // Unity clamps renderQueue to [0, 5000]

public static int QueueFor(int drawIndex, LayerSubSlot subSlot = LayerSubSlot.Base)
    => TransparentQueue + drawIndex * SubSlotsPerLayer + (int)subSlot;
```

- Layer *i* owns the contiguous band `[base + i·S, base + i·S + S−1]`. Every sub-slot of layer *i* is
  strictly below every sub-slot of layer *i+1* — the cross-layer painter's guarantee is *strengthened*, never
  relaxed.
- **Symbols: icon = `Base`, text = `Above`.** The badge draws under the number it frames.
- Fill / line / background use `Base` only. Their relative order is arithmetically unchanged (a strictly
  increasing map `i ↦ base + i·S`), so nothing about the existing composite moves.
- A future kind that needs a third sub-slot (fill-extrusion's side/cap, say) adds an enum value and bumps
  `SubSlotsPerLayer`; no caller re-derives arithmetic.

**Why `Above`, not `Overlay`.** This enum lives in the render-queue domain, where `Overlay` already means
Unity's `RenderQueue.Overlay` (4000) — the very pin `SymbolRenderLayer` was freed from in E2/D11. `Above`
says the one thing that matters: above its **own layer's** `Base`, still inside its own band.

**Which write site moves.** `RenderLayerSet.Build` keeps its single generic write and asks the layer which
sub-slot its primary `Material` occupies — a new `IRenderLayer.MaterialSubSlot` (`Base` for fill/line/
background, `Above` for symbol). `SymbolRenderLayer.Create` keeps writing the icon, now explicitly at
`QueueFor(drawIndex, LayerSubSlot.Base)`.
*Rejected:* moving the text write into `SymbolRenderLayer.Create` alongside the icon. It reads tidier but
makes `Build`'s write dead for one kind, lets the two drift, and breaks the uniform "Build assigns every
slot's queue" contract that `RenderLayerSetTests.Build_QueueShift_*` pins.
*Also rejected:* a capability interface probed with `is` (the `ISpriteConsumerRenderLayer` idiom). Draw order
is a property **every** layer has, not a capability some have; hiding a global ordering invariant behind a
type test is the wrong shape even though it touches fewer files.

**Ceiling.** The check must cover the band's **top**, or the last layer's `Above` slot would saturate at 5000
and silently collapse onto its own `Base` — exactly the failure `ComputeQueues`' own comment refuses to
allow. Condition: `baseQueue + layerCount·S − 1 ≤ 5000`. At base 3000, S=2: **1000 layers** is the maximum
(top slot 4999); 1001 would need 5001 and throws. `QueueFor` gains the same guard (today it validates
nothing and would silently overflow). Liberty declares **111** layers, ~9× headroom.

### Invariant

Relative order **across** layers is unchanged for every layer kind; the only behavioural delta is that a
symbol layer's icon now draws strictly under its own text. Concretely: for any two declared layers *i < j*,
every queue value of *i* is still strictly less than every queue value of *j*; the composite of a
fill/line/background-only style is byte-identical; **`LayerOrderSnapshotTests` gets no behavioural hunk** (one
stale comment number at `:124` is the sole permitted edit) **and its snapshot does not change.**

### Teeth

Engine-free unless marked **[Unity]**. The engine-free ones extend the existing `LayerDrawOrderTests`
fixture, which `Tools/core-tests/core-tests.csproj:209` already compiles — **no csproj edit is needed**, and
needing one would mean the code landed in the wrong file.

| Tooth | Assertion | Goes RED against |
|---|---|---|
| N1 | `QueueFor(i, Base) < QueueFor(i, Above)` over a range of *i* | today's code (equal), and injection **I-A** (`Above = 0`) |
| N2 | bands are disjoint and ordered: `QueueFor(i, Above) < QueueFor(i+1, Base)`, and both sub-slots lie inside `[QueueFor(i, Base), QueueFor(i, Base) + SubSlotsPerLayer − 1]` | injection **I-B** (`SubSlotsPerLayer = 1`, `Above = 1`) — the naive "just add 1 for text" fix, which overflows into the next layer |
| N3 | `ComputeQueues(n)[i] == QueueFor(i, Base)` — one formula home, no drift between the batch and single forms | injection **I-C** (leave `ComputeQueues` un-strided) |
| N4 | `SubSlotsPerLayer == Enum.GetValues(typeof(LayerSubSlot)).Length`, and the values are contiguous `0..S−1` | a future value added without reserving a slot for it |
| N5 | uniform stride + band start: `ComputeQueues(n, b)[0] == b` and `q[i+1] − q[i] == SubSlotsPerLayer` (read off the named constant, not a literal), at the default and a custom base | any stride irregularity; **replaces** the two exact-value tests (see below) |
| N6 | `SubSlotsPerLayer == 2`, asserted **once**, with the reason (icon + text) | a silent stride change — the constant is pinned in exactly one place, not smeared across baked value lists |
| N7 | ceiling: `ComputeQueues(1000, 3000)` OK (top sub-slot 4999), `ComputeQueues(1001, 3000)` throws (would need 5001); `QueueFor` throws on a `drawIndex` whose `Above` slot exceeds `QueueCeiling`, and on a negative index | **today's code, with no injection at all** — `ComputeQueues(1001, 3000)` does not throw now |
| N8 | every queue still `>= TransparentBandStart` (existing tooth, kept) | a band that drifts out of the transparent range |
| U1 **[Unity]** | two symbol layers: for each slot *i*, `WorldIconMaterial.renderQueue == QueueFor(i, Base)`, `WorldTextMaterial.renderQueue == QueueFor(i, Above)`, icon **strictly less than** text, both inside slot *i*'s band | today's code (equal), and injection **I-A** |
| U2 **[Unity]** | cross-layer, same two symbol layers: `layer0.WorldTextMaterial.renderQueue < layer1.WorldIconMaterial.renderQueue` | injection **I-B** (they collide at one value) |
| U3 **[Unity]** | interleaved bg/fill/symbol/line/fill style: each slot's `Material.renderQueue == QueueFor(i, thatLayer's MaterialSubSlot)`, strictly increasing across slots, and the symbol slot's text still below the next slot's base | a symbol layer whose text escapes its own band |
| U4 **[Unity]** | **guard, not a red test:** `LayerOrderSnapshotTests.cs` has **no behavioural hunk** in `git diff` — no assertion, no input, no baseline touched (one stale *comment* value is exempt, below) — and passes | — a changed snapshot means inter-layer order moved, i.e. the fix broke the thing it must preserve |

Every tooth above asserts an integer queue value or a file's absence from a diff. They prove the
**mechanism** — icon strictly below its own text, bands disjoint. That the **badge no longer paints over its
number** is confirmable only by eye: **maintainer eyeball at z13 on `OpenStreetMapLiberty.unity`**, same
standing as §6's Stage-1 eyeball row. A green gate here is necessary, not sufficient.

### Confirmed by the adversarial pass on this stage (attacked, held)

The load-bearing premise — *"a queue delta between two coplanar symbol quads reorders the paint"* — was
attacked directly and survived **more strongly** than §9 originally claimed:

- **Zero depth arbitration.** Both `Map/Symbol/IconWorld` and `Map/Symbol/TextWorld` are `ZWrite Off` **and**
  `ZTest Always` — not merely "ZWrite off"; there is no depth path at all, so submission order alone paints.
  (This retires the "premise to re-verify" note the plan carried.)
- **No submission path bypasses `material.renderQueue`.** Labels draw through a plain `MeshRenderer` tree
  (`WorldLabelRenderer`), explicitly **not** an `ITileRenderBackend`, so no BRG/command-buffer path can
  reorder them; and the project overrides neither `SortingCriteria` nor `TransparencySortMode`.
- **Live GPU proof already exists.** `SymbolLayerOrderSnapshotTests` reads back pixels showing that swapping
  `renderQueue` between two symbol **text** materials flips the composite — the same mechanism D7 uses for
  icon-vs-text.
- **The edit surface is closed.** The four `IRenderLayer` implementers are exhaustive, and there is no third
  production caller of `QueueFor` / `ComputeQueues` beyond `RenderLayerSet.Build` and `SymbolRenderLayer.Create`.
- **The two test replacements are genuine strengthenings**, not relabeled re-bakes (independently checked —
  see "corrections, not re-bakes" below).

**RED-verification procedure** (per `docs/lessons-learned.md`'s "a green test can be degenerate"): a brand-new
API cannot "fail to compile" its way to a RED. After implementing, the developer injects each defect, records
which teeth go red, and reverts:

- **I-A** — `Above = 0`: N1, U1 must go RED (this is the production bug reproduced at the sub-slot level).
- **I-B** — `SubSlotsPerLayer = 1` (keeping `Above = 1`): N2, N6, U2 must go RED.
- **I-C** — `ComputeQueues` left at `baseQueue + i`: N3 must go RED.
- N7 needs no injection: run it against the pre-fix tree.

### The two existing tests that must change — corrections, not re-bakes

Called out because both look like the thing this repo forbids.

1. **`RenderLayerSetTests.cs:189,191`** currently asserts `WorldTextMaterial.renderQueue == QueueFor(i)`
   **and** `WorldIconMaterial.renderQueue == QueueFor(i)` — i.e. it *pins G7*. It was written to prove both
   queues are set at Build time with no Tick (the §0.1 commit-2 deletion); that intent survives, but the
   asserted value was wrong. The replacement (U1) keeps the no-Tick intent and adds the ordering the old pair
   asserted away: icon `== QueueFor(i, Base)`, text `== QueueFor(i, Above)`, **icon < text**. A reviewer
   distinguishes this from a re-bake by the added strict inequality — a re-bake would restate equality at new
   numbers and still pass with icon above text.
2. **`LayerDrawOrderTests.Queues_AreExactly_BasePlusIndex_{DefaultBase,CustomBase}`** pin
   `q[i] == base + i` with hard-coded `3000..3004`. Restating them as `3000, 3002, 3004…` would be a pure
   re-bake with no added falsifying power. They are replaced by N5 (band start + uniform stride, expressed
   through `SubSlotsPerLayer`) plus N6 (the constant pinned once, with its reason) — strictly stronger: N5
   catches a non-uniform stride, which an exact-value list cannot distinguish from a deliberate change.
   `Queues_AreStrictlyMonotonicIncreasing_AndDistinct` and the band/zero/negative/base-below-band tests are
   kept as-is.
   **`LayerDrawOrderTests.LayerCountExceedingQueueCeiling_Throws`** changes its numbers (2001/2002 → 1000/1001)
   — that is a mechanical consequence of the stride, and N7 states the arithmetic that produced each.

### Prose that asserts the old formula (must move with the code, or the SSOT lies)

`ARCHITECTURE.md:171` (`material.renderQueue = base + layerIndex`) · `docs/meshing-design.md:467, 489, 502` ·
`LayerDrawOrder.cs:14-17, 51-53, 66` · `IRenderLayer.cs:35` · `RenderLayerSet.cs:70-72` ·
`SymbolRenderLayer.cs:20-26, 56-58` · `BrgBackendSnapshotTests.cs:228` (comment only — its assertions at
`:261-273` are "non-decreasing" + "inside the band", both of which survive) ·
**`Visual/LayerOrderSnapshotTests.cs:124`** (comment only — "the TOP layer (red fill, queue **3002**)" is the
stride-1 value of `ComputeQueues(3)`'s top slot; under stride 2 it is **3004**. The assertions are relational,
so the test still passes — but the comment would state a false number, which is exactly the drift the two
citations above are audited for. This is the **one** narrow exception to "that file is not edited": a
comment-value fix, nothing else — no assertion, no input, no baseline, no behavioural hunk).

### Deferred — explicitly NOT this stage

| Item | Why |
|---|---|
| **The G5/D5 collision residual** (the "only the number" symptom) | Needs true symbol-instance pairing — §6, unchanged. Attempting it here would conflate two independent fixes in one revertible commit. |
| **Switching `SymbolLayerOrderSnapshotTests`' hand-written `TransparentQueue + N` queues to `QueueFor`** | Those writes construct an *ordering scenario*, not an assertion about the production formula; they order correctly under any stride. Editing the inputs of a GPU snapshot test for zero contract gain is the riskiest available edit. Comment-only note allowed: with `drawIndex: 1`, `Create` now writes the icon at `QueueFor(1, Base) = 3002` while the test hand-writes text to 3001 — inert (that scene renders no icon label), but a reader will trip on it. |
| **Deleting `ComputeQueues`** (only test callers today) | `LayerOrderSnapshotTests:85` depends on it, and touching that file is forbidden by the invariant. It stays as the batch form of the same formula, pinned no-drift by N3. |
| **Per-quad draw ordering inside one symbol layer** (e.g. halo under glyph) | The band has room, but nothing needs it; a sub-slot must be claimed by a real requirement. |
| **The BRG / ScriptableRenderPass draw-order target** | Still the deferred replacement for the whole integer-queue interim (ARCHITECTURE §2). Widening the stride shortens the interim's runway from ~2000 to ~1000 layers; liberty uses 111. |
| **Whole-band validation at the ceiling boundary** | `QueueFor` validates only the sub-slot it is asked for, not the whole band its layer's kind will need. At exactly `drawIndex = 1000, base = 3000`, `QueueFor(1000, Base)` = 5000 passes while `QueueFor(1000, Above)` = 5001 throws — so a **symbol** layer at that index writes its icon queue, then throws from `Build`'s later text write, leaving `Build` mid-loop with a mutated-but-unlisted layer whose material is never disposed (a leak). Recorded, not fixed: liberty declares 111 layers, ~9× under the boundary, and the honest fix is band-aware validation (`Build` reserving the layer's full band up front), which belongs with the BRG target above rather than this stage. No tooth covers it. |

---

## 10. Stage 3 — G5: a centred icon+text symbol is ONE placement instance

Stage 2 removed the "badge alone" symptom. The remaining one is **"only the number, no badge"** — the G5/D5
residual, which §3 D5 named an approximation and §6 deferred. This stage fixes it, and retires D5.

D5's harm is not one bug but three, all from the same root — **an icon and its text are independent
entities through three systems, and D5 patched only the first, one-directionally**:

| System | Today | Consequence |
|---|---|---|
| Collision (`LabelCollision.ComparePlacementOrder` + the greedy) | two candidates, ordinals *N* (icon) and *N+1* (text); the text is force-fed `AllowOverlap` + `IgnorePlacement` so it always places | the icon can lose to a higher-priority label while its number still draws — **the bare number** |
| Cross-tile dedup (`DedupKey` in `SymbolLabelReconciler.Run`) | icon key `(cell, layer, ∅, iconImage)`, text key `(cell, layer, text, ∅)` — two independent finest-zoom scans | the two halves can be selected from **different tiles**, so the number sits metres off its badge |
| Fade (`LabelPlacementSystem.PointFadeId` → `_fadeOpacity` / `_placedLastFrame`) | one opacity record per half | the halves ease in/out **independently** |

MapLibre treats a symbol's icon and text as ONE instance: `icon-optional` / `text-optional` default false ⇒
they place or drop together, with a combined box.

### D8 — the pair is ONE `LabelCandidate` over BOTH boxes, with a per-candidate EMIT RANGE

The blocker every prior pass recorded — *"`WorldLabelRenderer.Emit` keys its mesh slot by
`(TileKey, Slot, AtlasKind)`, so one candidate cannot span the glyph atlas and the sprite atlas"* — is real
and is **verified still true** (`WorldLabelRenderer.cs:216`). Its stated consequence ("so pairing needs
either an instance link on `LabelCandidate` or a per-quad `AtlasKind` with a split emit") is a false
dilemma. A candidate does not have to own exactly one `CandidateEmit`:

- `LabelCandidate` already spans a **contiguous range of boxes** (`BoxStart`/`BoxCount`) and
  `LabelCollision.SelectSurvivors` already places it **all-or-nothing**: every box is TESTED before ANY is
  inserted, and a placed candidate inserts them all. That is *exactly* MapLibre's combined-box semantics —
  and it is the machinery curved along-line labels already run on.
- So the pair becomes **one candidate with two boxes** (icon AABB, text AABB — each keeping its own
  `*-padding`), and `LabelCandidate` gains `EmitStart`/`EmitCount` — a range into the staged
  `CandidateEmit` pool, mirroring `BoxStart`/`BoxCount`. The pair emits **two** `CandidateEmit`s, each with
  its own `Slot`/`AtlasKind`/quad range. **`WorldLabelRenderer` is not touched.**

**Rejected — per-quad `AtlasKind` + a split emit.** Merging the two halves' quads into ONE emit needs
per-QUAD `Color`, `TextSizePx`, `PaddingPx` and `TranslatePx` (the halves differ in all four:
`text-color`/`icon-opacity`, text size vs. the pre-baked icon scale, `text-padding`/`icon-padding`, and
`text-translate` vs. the icon's own untranslated anchor — `SymbolFeatureExtractor.EmitIconLabel:460` leaves
`TranslatePx` at zero, so the two halves already differ). `PlacedQuad` carries colour and size per quad, but `StagePoint` fills
them from ONE `PointStageInput` — so this route means a per-quad style table through the Burst staging SoA.
Strictly more blast radius for strictly less fidelity.

**Rejected — an instance link honoured inside the collision greedy** (candidate *B* inherits candidate
*A*'s survivor bit). The follower cannot insert its box in time to block anything (its verdict is only
known after the greedy has passed it), so the text stops participating in collision at all; only the
owner's box is ever tested; and it needs a `LabelIndex → survivor` scratch plus a second pass inside the
per-frame collision job. Weaker semantics, more risk, in the hottest path.

**Rejected — a shared `FadeId` + an AND-harvest** (both halves stay independent candidates; a fade id
counts as placed only if EVERY candidate carrying it survived). This is the cheap-looking option and it is
**broken**: the two boxes overlap by construction, so the icon places, inserts its box, and then the text
collides with **its own partner** — AND-harvest would drop every shield. Test-all-then-insert (the multi-box
candidate) is the only formulation immune to that self-block.

### D9 — identity: the rider has NO identity of its own (no key is widened)

The pair's owner is the **icon** — it is already emitted first (D5), it holds the lower `FeatureIndex`, and
its `(cell, layer, iconImage)` key is the one the badge already dedups on. The text becomes its **rider**:

- **One candidate ⇒ one `FadeId`** — the owner's, unchanged. Both halves draw at that one opacity, so they
  can no longer ease apart. `PointFadeId` is **not modified**.
- **One dedup entry** — the reconciler skips riders in its scan and emits a winning owner's rider
  immediately after it (same block, same tile). `CrossTileLabelKey` and `DedupKey` are **not modified**.

Not widening the keys is the point: a lone text label and a lone icon label keep byte-identical identities,
so the "icon-only and text-only labels are unaffected" invariant is structural rather than argued. It also
gives the right cross-tile answer for free — a tile holding the *complete* pair and a finer tile holding
only the icon share ONE key, so finest-zoom-wins picks a whole symbol from one tile instead of assembling a
Frankenstein from two.

Both rules land in **one** implementation. `SymbolLabelReconciler.Run` is already the single dedup impl the
production path runs; `SymbolTileLabelStore`'s plain `CollectInto` overload still carries a hand-written
third copy of the scan with **zero non-test callers**, so its dedup branch is **routed through the
reconciler** here rather than being taught D9's rules a third time (its `quantizeMeters ≤ 0` no-dedup branch
stays — it is live test surface and needs no pairing work, since nothing is deduped and list order already
keeps owner→rider adjacent). Plain-vs-plan parity then holds by construction instead of by hand.

**A rider never outlives its owner.** In the DEPARTING scan a claim-skipped owner (an active copy already
shows the symbol) must take its rider with it, or the plan carries an orphan rider that stages nothing — the
departing tile's number would vanish from the plan while its badge is drawn by the active copy. Pinned by
tooth P13, which also asserts the general structural invariant: in the reconcile output, every `Rider` is
immediately preceded by its own `Owner`.

**Recorded deviation:** a rider has no key, so a *lone text* copy of the same symbol in ANOTHER tile is no
longer deduped against a pair's text and both are emitted. It needs one tile with a complete pair and
another with a text-only copy of it (an icon that failed to resolve there — the window D6 closed), the two
overlap exactly, and the collision pass drops the loser. Accepted.

### D10 — where the pair is decided, and how it survives a half-built label

The extractor already computes the `centredPair` predicate (§3 D5) and already emits icon-then-text
adjacently (`SymbolFeatureExtractor.EmitAtAnchor:425`). It now stamps a `LabelPairRole`
(`Owner`/`Rider`/`None`) + a `PairId` on the two `SymbolLabel`s instead of forcing the passenger's overlap
flags — **the D5 forcing is deleted**; a pair's text carries its authored `text-allow-overlap` /
`text-ignore-placement` again, and the pair candidate's flags are the AND of the two halves'.

The roles are a *proposal*, not a fact: `StyledSymbolTileBuilder` **skips** a label whose shaping throws
(per-label isolation, `:254`), and the baker tolerates `null` slots — so a rider can go missing. One shared
engine-free resolver, `LabelPairing`, decides the truth from a list: index *i* is a paired owner iff
`labels[i+1]` exists, is non-null, is a `Rider`, and matches on `PairId` (+ `TileKey`/`MaterialIndex` for
`LabelInstance`, since `FeatureIndex` restarts per layer). A half-built pair **dissolves into two ordinary
labels** — never an owner bound to a stranger. The baker and the reconciler both call it, so they cannot
disagree.

Downstream the pairing is already resolved: `PointStageInput` carries only the resolved `PairRole`, and the
owner's rider is **the next point record** — which holds because the reconciler emits them adjacently and
the gather compacts point records in winner order (`SymbolGatherJob.cs:143`). The stage job checks
`Points[d+1].PairRole == Rider` before reading it and stages the owner ALONE if it doesn't (degrade to a
badge with no number — never a bare number, never a stranger); a debug assert on the mirror-rebuild path
surfaces the impossible case loudly.

**Rejected — folding both halves into one block record** (a `PointPair` record kind, or a second `Points`
entry addressed from the owner's record). It removes the adjacency contract, but adds a record kind or a
pool to `SymbolTileLabelBlock`, the baker, **`SymbolGatherJob`** (new remap), the mirror, the batch, and the
parity oracle. The adjacency route changes the gather by **zero lines**.

### D11 — `icon-optional` / `text-optional`: DEFERRED, not shipped in this stage

> **Status: NOT IMPLEMENTED.** Neither property is parsed anywhere in `MapRenderer.Core` (grep-verified at
> review). The design below is the agreed shape for when it lands; tooth **P9 does not exist**. Recorded here
> rather than deleted because the decision — approximate by outcome, not by MapLibre's retry mechanism — was
> reached and reviewed, and re-deriving it later would be wasted work. Safe to defer: every centred pair in
> liberty leaves both properties at their `false` default, so pairing behaves identically today either way.

The intended shape — both parsed into `LayoutProperties` (plain `bool`, spec default false, same shape as
`icon-allow-overlap`). A centred pair is only formed when **both are false**; if either is true the two
halves stay independent labels — MapLibre's escape hatch, approximated by its outcome ("this half may place
without the other") rather than by its mechanism (try the pair, then retry without the optional half, which
needs a placement retry pass we do not have). No liberty layer is affected: the five that declare them
(`airport`, `label_village`/`_town`/`_city`/`_city_capital`) all anchor their text top or bottom, so they
never trip the centred predicate.

### Invariant

**Only a centred icon+text pair changes behaviour.** Concretely: `CrossTileLabelKey`, `DedupKey`,
`PointFadeId` and `WorldLabelRenderer` are unmodified; a text-only or icon-only label produces a
byte-identical `PointStageInput` (`FadeId` included) and a candidate with `BoxCount == 1`,
`EmitCount == 1`, `EmitStart == LabelIndex`; every curved label is untouched; no `poi_*` / `place` /
`airport` / `label_*` layer changes its emit order, `FeatureIndex` ordinals, identity or placement; **no
snapshot is re-baked.**

**Performance invariant** (the label Stage/Emit loop is the profiled per-frame hot spot —
`docs/symbol-label-perf-design.md`): the pair costs strictly LESS per frame than today — ONE candidate
instead of two means one fewer sort element, one fewer grid query set, one fewer fade lookup and one fewer
`EaseFade`. No new per-frame managed allocation, no new pass, no new buffer, no new job. The existing
`MaxCandidates` pre-size stays a valid bound for the emit pool (a pair reserves 2 across its two records and
uses exactly 2), so `PreSizeStageOutputs` is unchanged.

### The D5 teeth this stage REVERSES (a semantics change, not a re-bake)

Called out because they look like the thing this repo forbids. Both pinned the D5 approximation as an
accepted behaviour; that decision is now withdrawn, so restating them would be dishonest.

1. **`SymbolShieldExtractionTests.CentredPair_LosesToBlocker_TextSurvivesButBlocksNothing`** (T12) asserts
   the icon drops, **the text places anyway** (the accepted bare number) and blocks nothing. Replaced by a
   tooth asserting the icon and the text drop **together** — the exact inverse. A reviewer distinguishes
   this from a re-bake by that inversion: the new tooth cannot pass against the old code.
2. **`CentredPair_EmitsAdjacentIconThenText`** (T5) and
   **`SymbolFeatureExtractorIconTests.Extract_TextAndIcon_YieldsTwoLabels_CentredPair_IconFirstThenText`**
   assert the passenger text carries forced `AllowOverlap && IgnorePlacement`. The forcing is deleted, so
   they now assert the AUTHORED flags plus the pair roles. Strictly stronger (they gain the role
   assertions); RED against today's tree at the flag lines, with no new API involved.
3. `CentredPair_ExtractorOutput_ChangesCollisionSurvivors` (T9) keeps its intent (real extractor output →
   real collision) but its expectation moves from "both survive" to "one candidate spanning both boxes".

### Teeth

Engine-free (compiled by BOTH runners) unless marked **[Unity]**. Full RED procedure + the per-stage split
live in the implementation plan; the falsifiable claims are:

| # | Assertion | Goes RED against |
|---|---|---|
| P1 | the extractor stamps `Owner` on the icon / `Rider` on the text of a centred pair and `None` on every non-centred icon+text feature (which keeps text-then-icon order) | today's code (no roles); a shallow impl that stamps every icon |
| P2 | `LabelPairing` dissolves a pair whose rider is null / missing / `PairId`-mismatched, and never pairs `[Owner(A), Rider(B)]` | a naive adjacency-only resolver |
| P3 | **the bare number is gone**: real extractor → real bake → real staging yields ONE candidate, `BoxCount == 2`, `EmitCount == 2`, emits `(Icon, Text)`; with a higher-priority blocker over the icon box, `SelectSurvivors` drops the pair and **zero text quads** are emitted | today (two candidates; the text survives) — the inverse of T12 |
| P4 | a placed pair BLOCKS through both boxes: a later label overlapping only the TEXT box is dropped | today (the passenger ignores placement) |
| P5 | both halves fade as one: the pair has a single `FadeId` == the icon's existing `PointFadeId`, and the text half contributes no candidate and no second fade record | today (two ids, two records) |
| P6 | cross-tile: tile A holds the complete pair, finer tile B holds only its icon ⇒ every emitted label comes from ONE tile, and no rider is emitted without its owner | today (icon from B, text from A — two tile keys) |
| P7 | icon-only / text-only / curved labels: byte-identical `PointStageInput` incl. `FadeId`, and `BoxCount == EmitCount == 1` with `EmitStart == LabelIndex` | any identity-key widening |
| P8 | the staged stream stays well-formed with a pair present: `TryFindRangeTilingViolation` false, fade ids unique | an impl that also stages the rider as its own candidate |
| ~~P9~~ | **NOT BUILT** — `icon-optional` / `text-optional` are unparsed; D11 is deferred, so this tooth does not exist. Listed only so a reader diffing the teeth table against the test files does not go hunting for it. | — |
| P10 **[Unity]** | a Tick over a scene containing a pair draws quads into BOTH the text and the icon world mesh of that `(tile, slot)`, and `LastCandidateCount` is one LOWER per pair than the pre-change count | an emit loop that reads only `EmitStart` |
| P11 **[Unity]** | no new per-frame managed allocation: steady-state Ticks over a pair-bearing scene are `Is.Not.AllocatingGCMemory` (extends `LabelPlacementAllocTests`) | any per-frame allocation added to the pair path |
| P12 **[Unity]** | the gather/bake parity oracle (`SymbolLabelBatchBuilder` vs. `SymbolTileLabelBlockBaker`, `SymbolGatherParityTests`) still matches field-for-field on a fixture containing a pair | oracle drift |
| P13 | **no orphan rider**, INTRA-tile: an active tile and a departing tile hold the same pair ⇒ the departing owner is claim-skipped and its rider drops **with it** (output = the active tile's 2 labels, one `TileKey`); and, over the whole output of both scans, every `Rider` is immediately preceded by its matching `Owner`. Re-run with the departing tile alone ⇒ its pair survives intact | dropping the departing scan's owner-decision carry (an orphan rider reaches the plan); and a naive "drop every departing rider" fix |

Every tooth above asserts a count, a flag or an identity. That the badge and its number now **survive or
vanish together on screen** is confirmable only by eye: **maintainer eyeball at z13 on
`OpenStreetMapLiberty.unity`** — same standing as §6's and §9's eyeball rows. After this stage neither
symptom (bare number, badge-over-number) may remain.

### Confirmed by the adversarial pass on this stage (attacked, held)

D8's load-bearing premise — *"a candidate does not have to own exactly one `CandidateEmit`, and the
multi-box candidate already gives MapLibre's combined box"* — was attacked against the working tree (not
against this prose) and held on every axis. Recorded so a later reader knows these were probed:

- **The "false dilemma" call is itself confirmed.** The reviewer that first raised the
  instance-link-or-per-quad-`AtlasKind` dilemma **retracted it**: `WorldLabelRenderer.Emit` keys its slot
  off `CandidateEmit`'s own fields, never off `LabelCandidate`, so an emit RANGE leaves the draw side
  untouched. Every prior pass had accepted the dilemma as a constraint.
- **All-or-nothing is real, not assumed.** `LabelCollision.SelectSurvivors:216-234` tests every box in the
  range before inserting ANY — the property that makes two overlapping boxes in ONE candidate legal and
  two independent candidates self-blocking. Curved along-line labels already ship on it.
- **`MaxCandidates` covers the emit pool exactly**; the derivation (a pair reserves 2 across its two records
  and uses exactly 2) was re-checked independently, so `PreSizeStageOutputs` genuinely needs no change.
- **"The rider is the next point record" is a property, not an assertion:** `SymbolGatherJob`'s
  point-compaction preserves winner-list order, and the reconciler emits owner→rider adjacently.
- **`CentredPair` is structurally unreachable from the curved path**, so "a curved label is never paired"
  needs no guard beyond the fence.
- **P3/P4 close exactly the gap T12 recorded as accepted** — this eliminates the bare-number symptom rather
  than relabelling it.

### Deferred — explicitly NOT this stage

| Item | Why |
|---|---|
| **Pairing every icon+text symbol, not just CENTRED ones** (the true MapLibre instance model) | `poi_*`, `label_city/town/village`, `airport`, `poi_transit` all pair in MapLibre (they declare `icon-optional: false`) but offset or re-anchor their text. Pairing them changes their placement and forces snapshot re-bakes — outside this stage's invariant, and it wants its own eyeball. The centred predicate stays the fence. |
| **MapLibre's optional FALLBACK semantics** (place the pair; if it fails, retry without the optional half) | Needs a placement retry pass. D11 approximates by outcome. |
| **A curved (along-line) label paired with an icon** | Line-placement icons are still fenced to the viewport-aligned upright case (D4/T8); a pair only exists on the point path. |
| **A union AABB instead of two boxes** | Two boxes are strictly more faithful and cost the same. |
| **Instances with more than two halves** | Nothing needs it; `EmitStart`/`EmitCount` already generalises if something ever does. |
| **Removing the "rider is the next point record" adjacency contract** (a `PointPair` block record) | See D10's rejection: it buys robustness the resolver + the job's guard + the debug assert already provide, at the cost of a `SymbolGatherJob` change. |

---

## 11. Stage 4 — the number sits BELOW the centre of its shield

Stages 1–3 make the badge and its number place, draw and drop as one symbol. The maintainer's eyeball then
found the third and last shield defect: the pair is together, but the **number sits slightly below the
centre of the badge**. MapLibre centres it.

Measured, not inferred — both layout paths run engine-free over the committed `NotoSansRegular`
`0-255.pbf.bytes` fixture, digit `'5'`, `TextLayoutOptions.Default` (anchor `center`):

```
[ICON] IconQuadLayout, TextAnchor.Center   ink band [-10.0, +10.0]   centre   0.0
[TEXT] TextQuadLayout,  TextAnchor.Center  ink band [-11.6,  +5.4]   centre  -3.1     <- 3.1 baked px LOW
```

At the shields' `text-size: 10` that is `3.1 × 10/24 = 1.3` screen px — "slightly low", exactly as reported.

### G8 — a centred text block is centred on a LINE BOX that the glyphs do not fill symmetrically

Three facts from source and from the fixture, in the order that produces the defect:

| # | Site | Fact |
|---|---|---|
| 1 | `Core/Text/TextQuadLayout.cs:230-267` (`PlaceGlyph`) | A glyph cell's TOP is anchored at `baselineY + entry.Top + GlyphSdf.Buffer` and grows DOWN. The glyph-PBF `Top` is **top-referenced and negative** (`'5'` → `Top = -9`), so the local `baselineY` is **not** a baseline — it is the font's **ascent reference line**, and the ink hangs below it. |
| 2 | the fixture | For every baseline-resting Latin glyph, `Height - Top == 26` (129 of 189 bitmap-bearing glyphs in `0-255`; descenders give 30/32, above-baseline marks 19/23). So the **typographic baseline sits 26 baked px below the reference line**, and a cap/digit's ink occupies `[-26, -9]` — centre **-17.5**. This is the same constancy `PlaceGlyph`'s own comment already relies on. **Scope: measured over Latin `NotoSansRegular/0-255`.** The other three shipped ranges (Arabic + presentation forms + variation selectors — no CJK/Cyrillic ships here) have a modal `Height - Top` of **27**, so a label in those scripts centres 1 baked px off (0.04 screen px @ size 10). Inert today — no liberty layer centres those ranges — but the constant's XML doc MUST state this Latin scoping, the way `NominalCapHeightEm`'s already does, so a future reader does not assume it is font-universal. |
| 3 | `TextQuadLayout.cs:179,190` | `blockHeight = lineCount * lineHeightPx`; `globalY = vAlign * blockHeight`. The block is *assumed* to span `y ∈ [-blockHeight, 0]`, so centring shifts by `+14.4` (1.2 em) and puts the ink centre at `-17.5 + 14.4 = -3.1`. |

So the block box's top edge is the ascent reference (which carries the font's accent slack — the tallest ink
in the range, `'À'`, only reaches `-4`) and its bottom edge is *whatever one line-height leaves over*
(`-28.8`, while a baseline-resting glyph stops at `-26` and `'g'` overshoots to `-32`). The midpoint of that
box is not the midpoint of the ink. `IconQuadLayout.Layout:36-39` has no such problem: a sprite's box **is**
its ink, so `vAlign = 0.5` centres it exactly. The two paths disagree by construction, and the shield shows
the disagreement.

A second, independent symptom of the same root, pinned by a tooth here: **a single-line centred label moves
when `text-line-height` changes.** `globalY = 0.5 · lineCount · lineHeightPx` depends on the line height even
at `lineCount == 1`, though line-height is a line-*stacking* property with nothing to stack.

### D12 — `Center` positions the block's OPTICAL CENTRE; `Top`/`Bottom` keep the box edges they have

Vertical anchoring is split into the three cases it always had, and only the centre case changes:

```
Top     : globalY = 0                                        (unchanged — block top edge at the anchor)
Bottom  : globalY = lineCount * lineHeightPx                  (unchanged — block bottom edge at the anchor)
Centre  : globalY = OpticalCentreBelowReferencePx + (lineCount - 1) * lineHeightPx * 0.5
```

with, as named convention constants (not literals at the use site):

```
GlyphSdf.BaselineBelowReferencePx = 26     // fact 2 above — where the baseline actually is
GlyphSdf.NominalCapHeightEm       = 17/24  // Noto Sans measures a 17 baked-px cap over a 24 px em
OpticalCentreBelowReferencePx     = BaselineBelowReferencePx - 0.5 * NominalCapHeightEm * OneEm   // 17.5
```

The cap-height literal is written as `17f / 24f` rather than a decimal so it carries its own derivation. The
em conversion then **cancels exactly** — `0.5 · (17/24) · 24 = 8.5` — so `OpticalCentreBelowReferencePx` is
exactly `26 - 8.5 = 17.5`, not an approximation, which is why the teeth can pin it as a clean hand-derived
literal.

The centred block's optical centre is the midpoint between the FIRST line's optical centre and the LAST
line's, which is why the multi-line term is `(lineCount - 1)/2` line-heights rather than `lineCount/2`: line
spacing is untouched, the whole block simply moves **up by a constant 3.1 baked px (0.129 em)** at every line
count and every line height.

**Which metric, and why not the others.** The metric is the **cap band** — `[baseline, baseline + nominal cap
height]` — taken from convention constants, never from the string:

- *Rejected: the ink bounds of the glyphs actually present.* It is the obvious reading of "centre the ink",
  and it is unstable: over the fixture, a cap/digit run centres at `-17.5`, an x-height run (`"x"`) at
  `-19.5`, a descender run (`"Ag"`) at `-20.5`. `"5"` and `"5g"` would centre 3 px apart, and a label would
  jump when its text changed. Pinned against by tooth **V6**.
- *Rejected: the em box above the baseline* (`[baseline, baseline + 1 em]`, centre `-14`). Barely moves the
  0.4 px off today's `-14.4` — it does not fix the defect, because caps occupy the *lower* ~70 % of the em
  box, which is precisely why the number reads low.
- *Rejected: the ascender/descender midpoint.* With a nominal 0.75/0.25 em split the centre lands at `-20`,
  overshooting the other way — a digit would sit 2.5 baked px **high**, the same magnitude of error we are
  fixing.
- *Chosen: half a cap height above the baseline* → `-17.5`, which **is** the fixture's measured cap-band
  centre: taking the cap height as the measured `17/24` em makes the chosen metric and the measurement agree
  exactly, with no residual. (An earlier draft used a round `0.7` em nominal and landed at `-17.6`, 0.1 baked
  px off; the maintainer chose the measured ratio instead, since `17f/24f` states where the number comes from
  where `0.7f` needs prose to explain it.) The whole-font ink band of the `0-255` range (`[-32, -4]`, centre
  `-18`) agrees within 0.5 px, so the choice is not sensitive — what matters is that it is
  *content-independent* and lands within a half pixel.

**Why `Top`/`Bottom` are deliberately NOT touched — the honest reason.** The same root makes the block's top
and bottom *edges* suspect too (a `text-anchor: top` label's ink starts 9 baked px = 0.375 em below the
anchor, so liberty's `poi_r*` / `airport` text plausibly sits ~0.3 em lower than MapLibre's). But: the
centring requirement pins only the **difference** `Ascent - Descent` (it must equal the cap height); the
**absolute** values of ascent and descent are constrained by nothing we can measure and by no reported
symptom. Redefining the box edges therefore means guessing two numbers and moving every top-/bottom-anchored
label in every style on that guess. We measured the centre; we ship the centre. §11's deferred table records
the edge question with its number so the maintainer can call it after an eyeball.

**Consequence: `Center` is no longer the arithmetic midpoint of `Top` and `Bottom`.** That is the design, not
a leak: the layout box's top edge carries the font's ascent slack, so the box midpoint is not the optical
centre — the same distinction CSS draws between a line box and `vertical-align: middle`. It is stated in
`TextQuadLayout`'s class doc, and `ResolveAlignFactors` stops returning a bare `vAlign` float (0/0.5/1, which
invited the lerp) in favour of a three-valued vertical anchor, so no caller can re-derive the midpoint by
accident. `ComputeRadialOffset`'s sign mapping is unchanged (`Top → -1`, `Bottom → +1`, centre → 0).

**`IconQuadLayout` is not changed** — it already centres ink on ink. Its class doc claims it "reuses the SAME
anchor hAlign/vAlign factors" as `TextQuadLayout`; that sentence is corrected in place to say where and why
the vertical halves now differ, or the next reader will "restore consistency" and re-break this.

### What moves, and by how much

Every label whose resolved `text-anchor` has a vertical-centre component — `center` (the default), `left`,
`right` — moves **UP by 0.133 × text-size screen px**. Nothing else moves. Over liberty as authored:

| Layer(s) | text-anchor | moves | at their `text-size` |
|---|---|---|---|
| the 3 shield layers | center (default) | ✅ up | 1.33 px @ 10 — **the fix** |
| `label_country_1/2/3` | center | ✅ up | ≤ 2.27 px @ 17 |
| `label_state`, `water_name_point_label` | center | ✅ up | ≤ 1.87 px @ 14 |
| `label_other` | center | ✅ up | ≤ 1.33 px @ 10 (its `text-size` interpolates 9→10 and clamps at 10; the `12` in liberty is the ZOOM breakpoint, not a size) |
| `poi_transit` | left (vertical centre) | ✅ up | 1.60 px @ 12 |
| `poi_r1/r7/r20`, `airport` | top | ❌ unchanged | — |
| `label_village/town/city/city_capital` | bottom | ❌ unchanged | — |
| `waterway_line_label`, `water_name_line_label`, `highway-name-*` | center, but **curved** | ❌ unchanged | `CurvedTextLayout` has no block anchor at all |

The collision box moves with the quads by construction — `LabelBox.Build` is the single site and takes
`TextLayoutResult.BoundsMin/Max` (`LabelStagingMath.cs:146`). For a §10 pair this is inert: the two boxes are
tested **all-or-nothing** in one candidate, so the pair still places or drops atomically; only the text half's
screen rect shifts up by the same 0.133 em, which cannot change the pair's own verdict.

### Invariant

Non-centre vertical anchors (`top`, `bottom`, and the four corners) are **byte-identical**; horizontal
anchoring, justify, wrap, letter-spacing, `text-offset`, `text-radial-offset`, RTL and every curved label **on
the map** are **byte-identical**; `IconQuadLayout`, `LabelBox`, the collision/pairing/fade/dedup path and every
Unity type are **unmodified**. Two existing test files change because they encode the old vertical formula
(below); a third — `WorldCurvedAbRenderSnapshotTests` — moves for a *different* reason found during the gate
and recorded below, and its move was admitted only after a measured translation proof. Any *fourth* test that
goes red is a real regression and must be diagnosed, never adjusted.

> **Correction to this invariant as originally written.** It claimed "**no snapshot is re-baked**" and
> "exactly two existing test files change". Both were wrong, and the reason is instructive: the curved-label
> byte-identity claim is true of *production* (`CurvedTextLayout` has no block anchor), but one curved
> **test** borrows the point layout as a fixture quad factory and so inherits the centre anchor. The claim was
> reasoned from the production call graph without auditing what the tests construct. See the re-mint record.

### Teeth

Engine-free (compiled by BOTH runners) unless marked. Full RED procedure in the implementation plan.

| # | Assertion | Goes RED against |
|---|---|---|
| V1 | over the real fixture, a `Center`-anchored single-line digit run's **ink** band (cell inset by `GlyphSdf.Buffer`) has `|centre| ≤ 0.5` baked px | today (`-3.1`) |
| V2 | icon and text agree: `IconQuadLayout.Layout(Center)`'s ink centre and V1's text ink centre coincide within 0.5 baked px — the shield claim itself | today (3.1 apart) |
| V3 | **line-height independence**: for a single-line `Center` run, `LineHeightEm` 1.2 vs 2.0 moves no quad by any amount | today (9.6 px), **and** any "subtract a constant from the old formula" impl |
| V4 | multi-line: a 2-line `Center` block's line-0 and line-1 ink centres are symmetric about `y = 0` (mean within 0.5 px), and their separation is exactly `lineHeightPx` | today (mean `-3.1`); an impl that centres only the first line |
| V5 | `Top`/`Bottom` goldens hand-computed from atlas entries: `Top` puts the block's top edge at `y = 0`, `Bottom` puts its bottom edge there; the corner anchors match their pure-axis pair | any impl that "fixes" the box edges too |
| V6 | **content independence**: the quad of a shared glyph is at the same `y` in `"5"`, `"50"`, `"5g"` and `"5x"` | an ink-bounds metric (the rejected reading) |
| V7 | `Left` and `Right` shift vertically **exactly** as `Center` does | a fix applied to `TextAnchor.Center` alone |
| V8 | `text-offset` / `text-radial-offset` deltas, justify, wrap and RTL are unchanged (the existing S19 T3/T4 tests pass **verbatim**) | a fix folded into the offset term |
| V9 | the constant is pinned to the FIXTURE: a baseline-resting reference glyph's own metrics reproduce `GlyphSdf.BaselineBelowReferencePx` (`bare height − Top`; the committed `'5'` gives `17 − (−9) = 26`) | a fixture regenerated from a font baked against a different ascent (27) — which today leaves **every other tooth green** while every centred label sits one baked px low |

V1–V4 and V7 are RED against the pre-fix tree. V5, V6, V8 and V9 are **guards** (green before and after) and
are RED-verified by injection, not by the pre-fix tree — the plan names the injection for each. V9's was run:
setting `BaselineBelowReferencePx` to `27f` turns it RED with "measured 26 from bare height 17 and Top -9",
and the constant was restored.

The teeth live in `TextVerticalCentringTests` as V0–V7 and V9 — V0 being a precondition on that file's own
ink-band helper, and V8 being the one row that is *not* there (it asserts the existing S19 offset/justify/RTL
tests still pass verbatim, so it lives in those files).

That the number now looks centred **inside the badge** is confirmable only by eye: **maintainer eyeball at
z13 on `OpenStreetMapLiberty.unity`**, same standing as §6/§9/§10's eyeball rows.

### The two existing tests that must change — corrections, not re-bakes

Both currently pin the defective formula, so restating them at new numbers would be exactly the re-bake this
repo forbids. What makes each a correction:

1. **`TextQuadLayoutTests.Layout_SingleLineCenterAnchor_MatchesHandComputedGolden:96`** hand-computes
   `anchorShift = (-0.5·lineWidth, +0.5·blockHeight)`. Only the **y** term changes, to the hand-derived
   literal `17.5` (with its derivation, `26 - 0.5·(17/24)·24`, in a comment — a golden must not be written in
   terms of the production constants it exists to check). The x term, the UV assertions and the whole
   `Layout_Teeth_*` companion stay verbatim. This test stays a formula golden; the *property* the formula
   exists for is asserted separately by V1 in the new `TextVerticalCentringTests` (the plan had folded V1
   into this test — keeping the two apart leaves the hand-computed golden checking exact quad geometry and
   the ink-centre property pinned where the rest of the centring teeth live).
2. **`TextAnchorOffsetJustifyTests`** — `Anchor_SingleLine_ShiftsByExactBlockBboxDelta:117` and
   `Anchor_TwoLineRun_VerticalDeltaUsesLineCountTimesLineHeight:156,161,164`. The `BottomRight - TopLeft`
   assertions (`= blockHeight`) and their "not a per-line anchor" teeth stay **verbatim** — that is what
   proves `Top`/`Bottom` did not move. The `Center - TopLeft` y term moves to the new formula, and the
   two-line test is renamed to say what it now pins (bottom uses `lineCount · lineHeight`, centre uses the
   line-span midpoint). A reviewer tells this from a re-bake by the untouched `BottomRight` half and by V3,
   which no restatement of the old formula can pass.

### The third test that changed — `WorldCurvedAbRenderSnapshotTests`, a measured baseline move

The gate came back **1827 total / 1822 passed / 5 failed**, all eight V1–V8 teeth green and all five failures
in `WorldCurvedAbRenderSnapshotTests` — a GPU ink-signature tooth for **curved** labels, which this stage's
invariant said could not move. It moved for a reason the invariant did not anticipate, and the five goldens
were re-minted only after the move was proven to be a pure translation.

**Why a curved tooth is sensitive to a block-anchor change at all.** `StyledSymbolTileBuilder` has two
mutually exclusive branches: point labels go through `TextQuadLayout.Layout(s.LayoutOptions)`, curved/line
labels through `CurvedTextLayout.Layout(run, atlas)` — which takes no options and no anchor. So the
production claim stands: **no curved label on the map moves.** But this test's fixture
(`BuildGlyphF`) manufactures its single glyph cell with `TextQuadLayout.Layout(run, atlas,
TextLayoutOptions.Default)`, using the *point* layout as a quad factory, and `TextLayoutOptions.Default.Anchor`
is `Center` — exactly the branch D12 redefines. The re-anchored cell is then fed to the curved render, so the
change arrives as a constant translation of the rendered quad.

**Predicted before measuring.** Layout shift `(26 − 0.5·0.7·24) − (0.5·1·1.2·24) = 17.6 − 14.4 = +3.2` baked
px; screen scale `TextSizePx/OneEm = 160/24 = 6.667`; **predicted 21.33 px** along label-local "up".

| case | Δrow | Δcol | \|Δ\| | bbox H×W (old → new) | ink (old → new) |
|---|---|---|---|---|---|
| 0° | −21.20 | +0.00 | **21.20** | 113×63 → 113×63 | 2924 → 2920 (−0.1%) |
| 45° | −14.70 | −15.00 | **21.00** | 121×85 → 121×85 | 2857 → 2823 (−1.2%) |
| 90° | +0.20 | −21.40 | **21.40** | 63×113 → 63×113 | 2918 → 2884 (−1.2%) |
| GlobeRebase | −10.50 | +18.70 | **21.45** | 81×126 → 81×126 | 2888 → 2891 (+0.1%) |
| NonzeroAnchorLocal | −14.70 | −15.00 | **21.00** | 121×85 → 121×85 | 2857 → 2823 (−1.2%) |

**Why this is a baseline move and not a re-bake over a drift** — four independent checks, all measured:

1. **Magnitude matches prediction** in every case: 21.00–21.45 against a predicted 21.33, worst error 0.35 px.
2. **Direction is the label's own up-axis** in every case: 0° shifts in row only, 90° in column only, 45° on
   the bisector at exactly 15.0/15.0. `GlobeRebase` — the one case whose row delta (−10.5) looked 30% short —
   reconciles once the column is read too: the tangent-basis rebase rotates label-local "up" off screen-up, so
   the magnitude sits in the (+18.7) column component. Reading the row alone was the misleading view; the
   row-first assertion order is why only the row delta was visible from the failure message.
3. **Shape is untouched.** All ten bbox dimensions are preserved with a delta of **zero** px — not "within AA
   jitter", exactly equal — and the 0°/90° axis-swapped pair (113×63 / 63×113) still holds. Ink counts move
   ≤1.2%, the sub-pixel AA resampling expected of a translated glyph. A translation cannot change shape or
   rotation sense, which is the property this tooth exists to guard; it stays armed, and neither
   `GoldenCentroidTolerancePx` nor `GoldenBboxTolerancePx` was loosened.
4. **Attribution is closed.** The fixture was re-run against the pre-D12 centre formula with the rest of the
   stage-4 tree in place: it reproduced the **old goldens digit-for-digit** on every centroid and every bbox
   edge. So the entire delta is caused by D12 and by nothing else — no environment drift, no second effect,
   and no possibility that a real regression is hiding inside a legitimate-looking move.

The fixture was deliberately **not** refactored off `TextQuadLayout`: any other anchor re-mints these goldens
just the same, and `Top` would move the ink ~96 px and risk the framing. A comment at the `BuildGlyphF` call
site records that the fixture quad inherits point-layout anchoring, so the next anchor change expects this.

### Deferred — explicitly NOT this stage

| Item | Why |
|---|---|
| **The block's top/bottom EDGE definition** (`text-anchor: top/bottom`, incl. the corners) | Same root; unconstrained by anything measured (see D12). Recorded number: a `top`-anchored label's ink starts 9 baked px (0.375 em) below the anchor, so `poi_r*` / `airport` text plausibly sits ~0.3 em (≈4.5 px @ 12) lower than MapLibre's. Wants a maintainer eyeball first, then its own stage. |
| **Curved (along-line) label vertical placement** | `CurvedTextLayout.cs:60` keeps `cellTopY = entry.Top + Buffer`, i.e. y stays reference-relative with no centring at all, so a curved label's ink hangs 9–26 baked px BELOW its path (`highway-name-*`, `waterway_line_label`). Whether that matches MapLibre is unverified and is a *different* question from block anchoring; `CurvedTextLayoutTests:68` pins the current behaviour deliberately. Not touched, not re-baked. |
| **Deriving `BaselineBelowReferencePx` per font stack** instead of a constant | The constant is a property of the glyph-PBF baking (the PBF carries **no** font-level metrics, so every consumer must assume one); 26 is measured from the committed fixture and pinned by **V9**, which re-derives it from a baseline-resting glyph's own metrics rather than restating it. A font baked against a different ascent would need `IGlyphAtlasView` to expose a per-stack metric plus a policy for stacks with no baseline-resting reference glyph (CJK-only labels) — a real improvement, an unrelated amount of surface. |
| **`text-variable-anchor`, `text-writing-mode`, vertical CJK** | Untouched by this stage and unaffected by it. |
| **Re-tuning `NominalCapHeightEm` per script** | `17/24` em is measured from Latin Noto Sans. Non-Latin runs centre by the same band; no reported symptom, and V6's content-independence is what keeps it predictable. |

### Followups recorded at review (not acted on)

| Item | Standing |
|---|---|
| **A recalled MapLibre `SHAPING_DEFAULT_OFFSET = -17` vs this stage's 17.5** — a 0.5 baked px residual. | **RECALLED, NOT VERIFIED — and deliberately not to be verified.** This repo is clean-room (`ARCHITECTURE.md` §4): the MapLibre source is not to be consulted, so this cannot be checked without violating that, and a recalled constant is not evidence. Recorded only so a future reader does not mistake it for a new discovery. **Do not act on it.** 0.5 px is also at the edge of the ±0.5 px band the teeth already tolerate and an order below the 3.1 px defect this stage fixed. |
| **`WorldCurvedAbRenderSnapshotTests` is the suite's ONLY absolute vertical-position tooth.** | Every other `Center`-anchored render test asserts *relatively* (deltas between anchors, symmetry about the anchor, independence from line-height/content), so a future change to vertical layout is caught in absolute terms by exactly one test — and that one is a curved fixture that only sees point anchoring by borrowing `TextQuadLayout` as a quad factory. That is a thin and slightly accidental net. Noted, not fixed: adding an absolute point-render tooth is its own stage, and this stage must not grow a new render golden while re-minting one. |

Item (c) from the review — deriving `BaselineBelowReferencePx` per font stack, and the `IGlyphAtlasView` +
CJK-policy surface it needs — is **already** recorded in the deferred table above and is not restated here.
