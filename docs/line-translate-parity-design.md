# line-translate parity — design proposal

**Status:** **BUILT AND GATED, 2026-07-29** — gate 1772/1772, every new tooth RED-verified against the
un-fixed shader (§6.1). Written out of the `feat/fill-parity` epic (`docs/fill-parity-design.md` §P5),
which left two findings unfolded rather than widen its own scope. The design reasoning below is kept as
written — including two reversals it went through — because the *way* it went wrong is reusable.

**One sentence:** `fill-translate` now has a correct screen-pixel implementation and `line-translate` does
not; porting fill's shape onto the line fixes four defects at once and is the forcing function for
deciding how the shared px→world measurement is kept in step under S66.

---

## 1. Why this is not the consistency chore it was first reported as

It was flagged to the maintainer as *"line-translate uses one px→world scalar for both axes and the
opposite y-sign from fill"* — which reads as a refinement. Reading `Line_VertexExtrude.hlsl` against the
now-correct `Fill_VertexModify.hlsl` turns up five independent defects, and the worst is a **unit error**:

| # | Defect | Site | Effect |
|---|---|---|---|
| 1 | `pxToWorld` is left at `1.0` unless `_WidthIsPixels > 0.5` | `Line_VertexExtrude.hlsl:76–94` | **A layer with a world-unit `line-width` applies `line-translate` as raw METRES.** Off by the px→world factor — orders of magnitude, zoom-dependent |
| 2 | The scale is measured along `unitDir_WS` — the line's *across* direction — not along the translate axes | `:93` | Wrong magnitude even when it is applied; the across direction is unrelated to the offset direction |
| 3 | Offset is hardcoded into world `XZ`: `float3(x, 0, y)` | `:120` | Violates the no-flat-ground-assumption rule; silently wrong under a globe projection |
| 4 | `+y` maps to `+Z` (north) | `:120` | Spec says *"negatives indicate left and up"*, so `+y` is **down/south**. Fill's `-north` is the correct one |
| 5 | `_LineTranslateAnchor` is parsed, bound, and never read | shader has no branch | `"viewport"` silently behaves as `"map"` (already 🟠 in `maplibre-spec.md`) |

Defect 1 alone justifies the work. Defects 2–5 are then nearly free, because the fix for 1 is *"adopt the
structure fill already has"* — which brings the per-axis measurement, the mesh frame, the sign, and the
anchor branch with it.

## 2. What the gate actually covers today

`LinePaintSnapshotTests.LineTranslate_NonZero_ShiftsRibbonByExpectedPixels` is the only tooth. It is blind
to every defect above, for reasons worth recording:

| Defect | Why the tooth cannot see it |
|---|---|
| 1 — unit error | The fixture sets `_WidthIsPixels = 1f` (`:106`), so `pxToWorld` has **never** been `1.0` in this test. The world-width path is untested |
| 2 — wrong axis | Top-down orthographic camera: every direction projects equally, so one scalar and per-axis measurement agree exactly |
| 3 — hardcoded XZ | Mercator-only fixture, where world XZ *is* the tangent plane |
| 4 — sign | The assertion is `Math.Abs(shiftedCenterRow - baseCenterRow)`. **Magnitude only** — the direction has never been pinned |
| 5 — anchor | No test sets `_LineTranslateAnchor` |

Two corroborating smells in the same test: its rationale comment (`:281`) still explains the maths in terms
of `_MetersPerPixel`, a uniform **S104 deleted**, and the fixture still calls `mat.SetFloat("_MetersPerPixel", …)`
(`:107`) into nothing. The test kept passing across S104 because it measures a magnitude that happened to
survive the rewrite — a textbook disarmed tooth: healthy-looking, structurally unable to fail.

**Consequence for the fix:** flipping the sign does **not** require touching this assertion, so no part of
this work looks like re-baking a snapshot to go green. The assertion should be *strengthened* to a signed
delta as part of the change — and that strengthening RED-verified against the un-fixed shader.

## 3. Priority — correctness-for-later, not a visible bug

`grep -c line-translate Assets/StreamingAssets/Fixtures/liberty.json` → **0**. The demo style does not use
the property (it uses `fill-translate` once). Nothing on screen today is wrong because of this. It is a
latent unit error waiting for the first style that sets it, plus a globe-projection landmine.

That argues for scheduling it deliberately rather than urgently — but *against* leaving it as a comment,
because the failure mode is silent and the next person to write `line-translate: [16, -8]` will see it
displace geometry by kilometres and have no reason to suspect the shader.

## 4. Proposed change — one work item

Port `Fill_VertexModify.hlsl`'s `MapVertexModify` structure into the line:

1. **Extract `LinePixelsToWorld(centerWS, dirWS)`** from the inline block at `:79–93`, parameterized by
   direction (fill's `FillPixelsToWorld` is already exactly this). The existing width/gap/offset callers
   pass `unitDir_WS` and are byte-identical afterwards — *that is the stage's invariant, and it is
   snapshot-checkable.*
2. **Rewrite the translate block** to fill's form: per-axis measurement, `axisRight`/`axisDown` from a
   frame, offset applied unconditionally of `_WidthIsPixels`.
3. **Add the `_LineTranslateAnchor` branch** — the `UNITY_MATRIX_I_V` basis-column path, copied from fill.
   Takes `line-translate-anchor` from 🟠 to ✅ at near-zero marginal cost, so it folds in here rather than
   being listed as separate work.
4. **Early-out on `all(abs(_LineTranslate.xy) < 1e-6)`** before any projection round-trip, as fill does.
   Without it every line vertex in every scene pays two extra `TransformWorldToHClip` pairs for a property
   almost no style sets.

### 4.1 The east/north frame — the one genuinely open question

Fill takes its "map"-anchor frame from the mesh (`normalOS` + `tangentOS`) rather than a hardcoded axis.
The line has `normalOS` but **no east stream** — its `tangentOS` is *derived along the line* for normal
mapping, so it cannot serve.

A first pass at this proposal claimed the gap was cosmetic, on the grounds that `StyledFillTileBuilder`
writes a constant object +X tangent — i.e. that fill's "mesh-supplied frame" was a constant dressed up in a
vertex stream, which the line could reproduce with `TransformObjectToWorldDir(float3(1,0,0))`.
**That is wrong, and the constant is only the Mercator branch.** `:315` writes `FlatTangent` and its own
comment says *"globe → subdivided path"*; `:392` is that path, writing per-vertex geodetic east from
`GlobeFillSubdivider` (`Projection.TangentBasisAt(geo).c0`). S91-C did this deliberately — a constant
tangent lights and textures a curved fill wrong — and `GlobeFillTangentTests` pins that globe tangents
*vary*, are unit-length, and are perpendicular to the geodetic normal.

#### What the line actually lacks — two needs, not one

A second misreading, worth recording because it changes the options. The line does **not** lack a
per-vertex frame. `StyledLineTileBuilder:227/247` writes `projection.ProjectPoint(geo).Up` per point — real
geodetic up on the globe — and `LineRibbonJob` builds `across = normalize(cross(along, up))` against that
same up. With the shader-derived `along`, the line has a complete, correct, per-vertex orthonormal tangent
frame, free. (`Line_VertexExtrude.hlsl:30` still describes the NORMAL stream as a *"constant +Y lighting
normal"* — **stale** since the globe work, and a contributing cause of both misreadings here. Fix that
comment whenever this file is next touched.)

Separate the needs:

- **The tangent plane** — the line has it exactly, per-vertex, at zero cost. Nothing is missing.
- **An azimuth reference within that plane** — which way is north. This is the *only* gap: the line's basis
  is rotated by the road's own heading, which varies per segment and is unrelated to north.

That reframing kills the 3-vector stream. **Inside a known orthonormal frame, east is one angle, not three
floats** — and it need not even be an angle. `LineRibbonJob` already calls the projection per point for
`Up`, the same seam where fill gets east from `TangentBasisAt(geo).c0`; it can store
`(dot(east, across), dot(east, along))` — exactly `(cos θ, sin θ)`, two dot products, no trig. The shader
reconstructs `east = c·across + s·along` in two multiply-adds. This can ride the existing `widthScale`
stream (TEXCOORD2, float→float3) rather than adding an attribute.

#### Why the line's own axes cannot be used directly

Worth stating explicitly, because it is the tempting shortcut: expressing the offset in the line's own
`across`/`along` axes would mean *"shift each road perpendicular to itself"* — which is **`line-offset`**
(`Line_VertexExtrude.hlsl:111`), a different spec property already implemented here. The two exist
separately in the spec precisely because one is road-relative and the other map-relative. `line-translate`
is defined against north; the line's basis is rotated from north by an arbitrary per-segment angle.

| Option | Plane | Azimuth | Cost |
|---|---|---|---|
| **(b) tile-frame east projected onto the vertex plane** | exact | error `≈ Δλ·sin(φ)` — see below | **free** |
| (a′) `(cos θ, sin θ)` widening the `widthScale` descriptor | exact | exact | +8 B/vertex (+12.5%) |
| ~~(a) east 3-vector stream~~ | exact | exact | +12 B/vertex — **dominated by (a′), off the table** |
| ~~(c) `cross(polarAxis, up)`, axis as a projection-emitted uniform~~ | — | exact | free — **rejected on convention, see below** |

#### Decision: (b), with a z0–z1 degeneracy guard

`east ≈ normalize(eastRef − up·dot(up, eastRef))`, projected onto the vertex's own up so the offset stays in
the true tangent plane and the residual is purely azimuthal — no out-of-plane component.

**`eastRef` is the world +X axis, not a per-tile vector.** `IProjection.cs:43` documents the tangent basis
as *columns c0=east, c1=up, c2=north*, and the backend rebases tiles by its **transpose** taken at the
camera's look-at point — `SceneTileTree.cs:192`, *"same orientation for every tile"*. So after the rebase,
world +X **is** east at the look-at point (+Y up, +Z north) under both projections; Mercator's rebase is
the identity, making it globally true there. Two consequences: the shader needs no object-space transform
at all, and because one rebase serves every tile the reference frame is **continuous across the scene —
there is no per-tile seam.**

**The residual, computed rather than asserted.** Pure *latitude* separation contributes **zero** error:
east at any point on a meridian is perpendicular to that meridian's plane, and so is the reference east, so
projecting it onto the vertex's tangent plane lands on true east exactly. All error comes from longitude,
and it is meridian convergence:

> error ≈ Δλ · sin(φ), where Δλ is the vertex's longitude separation from the **camera's look-at point**

That bound is *angular distance from screen centre*, not tile size — zero at the centre of the view,
growing toward the limb, and shrinking with the visible extent:

| visible extent (≈ zoom) | Δλ at the screen edge | error @ 60°N | error @ equator |
|---|---|---|---|
| z12 | 0.09° | 0.08° | 0 |
| z8 | 1.4° | 1.2° | 0 |
| z4 | 22.5° | ~19° (≈5 px on a 16 px offset) | 0 |
| z2 | 90° | ~78° | 0 |

So the honest statement is **"exact at screen centre and on Mercator everywhere; negligible from z5 up at
any latitude; meaningful only toward the limb of a zoomed-out globe at high latitude"** — not "unreliable
at z0–z4". Collapse (up ∥ eastRef) needs the vertex ~90° from the look-at point, i.e. the very limb at
z0–z1, so the guard avoids a NaN rather than buying correctness; world +Z is perpendicular to up exactly
there, which makes it the natural in-plane fallback.

Against that, (a′) costs 12.5% of every line vertex, forever, in every tile, to correct a few degrees of
direction in a property **no style in the repo sets** (§3).

**Two earlier drafts of this section got this wrong in opposite directions; the failure mode is worth
keeping.** The first chose (b) on the reason *"exact on Mercator, the only projection shipping by
default"* — false: `OpenStreetMapLiberty.unity`, the demo actually eyeballed, serializes `UseGlobe: 1`
(`Bootstrapper.cs:95` still initializes `UseGlobe = false`; the scene's value wins — **fix that stale
initializer**). The second then flipped to (a′) on that correction — but *still* without computing the
error, merely re-labelling it "the zoom range a globe app opens at". Both drafts argued from a premise
about the projection instead of from the magnitude. The quantification above is what actually decides it,
and it should have come first.

**Why not (c), which is exact and free.** With the sphere's polar axis as a projection-emitted uniform,
`east = normalize(cross(axis, up))` is exact at zero per-vertex cost, falling back to `tileEast` for a
planar projection (where that is exact). Rejected on convention, not correctness: it puts *"surfaces are
spheres"* into the shader, whereas fill deliberately bakes east into the vertex stream so the shader knows
nothing about the projection ("the per-vertex frame is mesh-supplied; no projection math in shaders").
Since (b) is adequate, there is no reason to spend that convention here.

**Upgrade trigger for (a′):** a style that actually sets `line-translate`, viewed on the globe, at z≤4, at
high latitude. All four conditions — it is a narrow corner, and the shader comment should say so alongside
the `Δλ·sin(φ)` form so the limitation is a recorded decision rather than a silent artefact.

**Scope note:** this affects only the `"map"` anchor. The `"viewport"` branch uses camera basis columns and
is exact under every projection at every zoom.

## 5. The duplication question — verified duplication, do not reopen S66

**Superseded (S23 I2a):** the trigger below fired — FillExtrusion (S23 I2b) needed the same measurement as
a third carrier — and S66 was reopened deliberately rather than accumulating a third sentinel block. The
block now lives once, at `Shaders/Map/PixelsToWorld.hlsl`, included as `../PixelsToWorld.hlsl` by every
carrier (following the `SymbolWorldPitchAlign.hlsl` precedent, itself already shared the same way). The
sentinel mechanism and `ShaderStructureTests.SharedShaderBlocks_AreIdenticalAcrossLayers` described below
are retired; `MapLayerFiles_DoNotIncludeCommonFolder` was repurposed to
`MapLayerFiles_ShareOnlyViaSanctionedInclude` (still forbids `Common/`, now also caps cross-folder
includes to the one sanctioned path). The analysis below is kept as the historical record of why
duplication was chosen first.

`FillPixelsToWorld` and the line's inline measurement are the same twenty lines. S66 made every layer
folder self-contained and `ShaderStructureTests.MapLayerFiles_DoNotIncludeCommonFolder` pins it; the fill
epic hit this, backed a `Map/Common/MapScreenSpace.hlsl` extraction out entirely, and duplicated with a
comment instead.

Three options, and the recommendation:

| Option | Verdict |
|---|---|
| Keep hand-syncing with a comment (today) | Works until it doesn't. The comment is the only thing preventing drift, and defects 1–4 above *are* drift — the two copies already disagree |
| A `Map/Shared/` folder | **No.** The structural test only forbids the literal string `Common/`, so this would pass — which makes it routing around the rule, not satisfying it. Declined once already |
| **Verified duplication** | **Recommended.** Keep both copies; delimit the shared block with sentinel comments in each file; add a structural test that extracts *between the sentinels* and asserts character-identity |

Verified duplication keeps S66's actual property (no cross-layer include topology; a change for one layer
cannot silently alter another's silhouette) while making drift a **test failure instead of a code review
hope**. Deliberate divergence stays possible — you delete the sentinels and the test stops applying, which
is a visible, reviewable act.

Mechanism matters: the test must extract between sentinels. A whole-file diff can't work and a substring
search rots on the first reformat. This is not a novel device in this repo — the fill epic tripped five
existing structural parity guards (CBUFFER field count, `PropertyNames` count, struct fields,
`FloatsPerInstance`, metadata count). One more is idiomatic.

**If a third layer ever needs px→world, that is the moment to reopen S66 properly** — not to accumulate a
third sentinel block.

## 6. Teeth

- **Invariant, stage 1:** extracting `LinePixelsToWorld` is behaviour-preserving ⇒ every existing line
  snapshot is byte-identical. A diff means the extraction changed something.
- **Defect 1:** a test with `_WidthIsPixels = 0` and a non-zero `line-translate` — asserts the shift is the
  same on-screen pixel count as the `_WidthIsPixels = 1` case. Fails hard against today's shader.
- **Defect 4:** tooth #4's `Math.Abs` becomes a signed delta. RED-verify against the un-fixed shader.
- **Defect 5:** a `_LineTranslateAnchor = 1` case under a rotated camera, where map and viewport anchors
  must disagree. Under the current top-down fixture they coincide, so the camera must be tilted or rotated
  for the assertion to mean anything.
- **§4.1 frame:** if (b) is taken, a globe test asserting the map-anchored offset stays in the vertex's
  tangent plane (`dot(offset, up) ≈ 0`) — that is the part option (b) guarantees exactly, as distinct from
  the azimuth, which it only approximates. Pins the refinement rather than the approximation.
- **Duplication:** the sentinel-identity test, RED-verified by perturbing one copy.

### 6.1 RED-verify results (2026-07-29)

Two defects injected into `Line_VertexExtrude.hlsl` at once, then the gate run: **4 failures, exactly the
4 predicted**, with the arithmetic matching the derivation.

| Injected | Test | Observed | Predicted |
|---|---|---|---|
| the original translate line (`float3(x·px, 0, y·px)`, one scalar, no anchor) | signed direction | `Δrow = +30` | `+30` — inverted |
| ″ | width-units | `Δrow = 110` | `30 / 0.2734 ≈ 110` |
| ″ | translate-anchor | map `Δrow = −30`, matching viewport | anchor ignored ⇒ both anchors agree |
| `// Fallback` → `// Fallback DRIFT-PROBE` in the line's shared block | shared-block identity | DRIFTED | textual drift caught |

Two notes on constructing this, both of which nearly produced a fake pass:

- The first idea for the drift probe was to change the `0.02` reference constant. **That cancels** — `refMag`
  and the measured `refPx` scale together, so the returned ratio is unchanged and the "defect" would have
  been a silent no-op. A comment word is the right probe for a test whose job is detecting *textual* drift.
- The RED run also caught a flaw in this work's own diagnostics: the width-units message predicted a delta
  of `−110` where the real failure is `+110`. Magnitude right, sign hint misleading. Fixed to state the
  magnitude and accept either sign.

### Hazard carried forward from the fill epic

`SnapshotRenderer.RawPixels` is **bottom-left** origin. That exact trap made a *correct* southward
`fill-translate` read as a shader bug for a full debugging cycle during P5. The same sign judgment is about
to be made for the line — check the origin convention before concluding a direction is wrong.

## 7. Deferred / out of scope

- `line-pattern` (❌), `line-gradient` (❌), `line-sort-key` (❌) — unrelated, larger.
- `line-miter-limit` / `line-round-limit` (🟠, parsed but not passed to the job) — same file, different
  concern; do not fold in.
- Any reopening of S66's self-containment rule.
