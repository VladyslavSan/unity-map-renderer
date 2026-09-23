# Tile pipeline — the per-(tile, source) lifecycle, its budgets, and restyle survival (design / SSOT)

**Status:** the model below ships. `TileManager` owns one record per (tile, source), drives it through
fetch → build → consume → release under per-frame budgets, and keeps a surviving layer's slot across a
restyle. Read with `docs/async-architecture.md` § "Disposal & cancellation contract" (the rules those exit
paths obey), `docs/job-scheduling-design.md` (the Burst graph the build step schedules; its § "What this design
owns from `docs/tile-pipeline-design.md`" states which part of the build seam that design owns), `docs/per-layer-tile-processing-design.md` (the per-layer
processor a kick fans out to), and `docs/smooth-transitions-design.md` (the fades the draw gate reads).

Hard constraints throughout: Core stays engine-free; UniTask only; `UnityEngine.Object` create/destroy and
`Mesh.AllocateWritableMeshData` / `ApplyAndDisposeWritableMeshData` are main-thread only; a `Mesh` has a
single owner and a transfer nulls the source; the steady-state Tick allocates no managed memory
(`MapView_SteadyStateTick_DoesNotAllocateGCMemory`).

---

## 1. The itch

A frame must stay inside its budget while tiles arrive, build, upload and leave. Two properties of the work
make that hard.

**The lifecycle is one state machine, and it does not split.** A `LoadedTile` record carries an in-flight
fetch, an in-flight build, the built meshes, the backend draw handles, and async exit paths that can fire in
any order. Spreading that across classes is how a double free happens, so the record and every transition
over it stay in one place. What sits *beside* it — the source registry, the release-time holding pens — is
separable and is separated.

**A bound on one path is not a bound on the frame.** A cap on mesh upload leaves every other per-tile and
per-layer main-thread cost free to spike: the release of a whole cover on a zoom-out, the cover descent while
the camera is still, the symbol build burst, the structural change each consumed mesh costs the ECS backend.
Each of those needs a budget, a gate, or a cheaper operation — not a tuning value.

## 2. Where main-thread work is bounded

| work | bound | mechanism |
|---|---|---|
| tiles active at once (request → consumed) | `MaxConcurrentTileLoads` (12) | `TileManager.AdmitFromDesired` holds the rest in the priority-ordered desired list |
| mesh builds started | `MaxMeshBuildsPerTick` (2) | `TileManager.PumpPending` |
| tile-layer meshes uploaded and registered | `MaxConsumesPerTick` (4) | `PumpPending`; consume is resumable mesh by mesh |
| vertices uploaded | `MaxVerticesPerTick` | `PumpPending` |
| records released | `MaxReleasesPerTick` (4) | `TileManager.DrainReleaseQueue` over the deferred queue (§4.4) |
| cover descent + diff | camera or viewport movement | `CoverKeyGate.IsDirty` (§4.1) |
| symbol builds started | one per frame | the symbol pump, which also carries the coalesced atlas upload |

The rate caps bound work *started or finished* per frame; `MaxConcurrentTileLoads` bounds the *active set*.
They are different quantities and a cover-wide zoom transition needs both.

Two costs carry no per-frame bound, each for its own reason.

- **The full-rebuild restyle** tears down every record and rebuilds the backend in one frame. It is rare and
  user-initiated, and the accepted price of never having to re-derive which loaded tile survives a new
  material set. §7 is what keeps most restyles off that path.
- **Garbage collection.** Mono's collector stops every thread, so a managed allocation on the load path is a
  main-thread cost wherever it was made. No per-frame cap reaches it; the allocation discipline in
  `docs/gc-and-allocation-design.md` is the only lever.

## 3. What may leave `TileManager`

The `LoadedTile` state machine and its exit paths stay (§1), and so does cover-key tracking, which is
cohesive with `Tick`. Three things sit beside the lifecycle rather than inside it.

### 3.1 The boundary with `MapView`

`MapView` keeps the camera, the `RenderLayerSet` and the scene origin. `TileManager` keeps the scheduler,
the loaded-tile table and the in-flight async machinery, and is ticked once per frame. Three things travel
from `MapView` **per call**, so that its inspector-editable config and its rebasing scene origin stay
authoritative: the current `CameraProperties`, the Mercator scene origin for tile placement, and the
tile-selection config. The `RenderLayerSet` is stable for the object's life, so it is injected once at
construction.

### 3.2 The source registry's narrow surface

`SourceRegistry` is slot-keyed and never returns the pipeline object it holds. An indexer that returned it
would hand every caller the `ITileFeatureSource` and the mutable `MinZoom`/`MaxZoom` fields no caller should
touch, and every later need ("just the source id", "is this slot sourceless?") would be met by punching a
property off the returned object instead of by an intention-revealing method. Ten narrow operations ship
instead — `SourceIdOf`, `IsSourceless`, `AdmitsZoom`, `SourceAt`, `ReleaseTile`, `Rebuild` among them — and
`TileProcessingStructureTests.SourceRegistry_SurfaceIsExactlyTenMembers` pins the bound, so a later "just add a getter"
fails loudly rather than reopening the indexer shape.

**The three-consumer agreement invariant.** The kick, the prepared-cache probe and the release transfer must
agree on what "this tile's complete prepared set" means; a disagreement serves a partial tile as a complete
cache hit. Today each of the three calls `TileManager.ComputeDenseLayerIds` over one shared scratch list,
which is never captured across a thread boundary. A per-pipeline cached array, computed once per style load,
would make the agreement structural rather than repeated — it is not built, and the scratch discipline is
what holds the invariant in its place.

### 3.3 Release-time holding pens

Work abandoned mid-flight has no result yet to dispose, so it is stashed and disposed when it settles.
`PendingDisposalQueue` owns three pens — a prologue build, a graph build, a fetch — filled from the single
site that abandons a record, `TileManager.RenderTeardownRecord`, polled once per Tick and flushed at
teardown. Both drain methods complete the graph arm's job handles synchronously, so both are main-thread
only. The pens are a lifetime concern rather than a lifecycle one, which is why they are the part that
leaves.

## 4. The per-frame budgets

### 4.1 The Tick order, and what the cover gate does not gate

The cover descent, the desired-list merge and the leaving-tile enqueue run only when `CoverKeyGate.IsDirty`
— the camera or viewport moved since the last commit. Admission, `PumpPending` and `DrainReleaseQueue` run
every Tick, in or out of that gate, so a backlog drains while the camera is still.

The gate does not read the pending count. A pending tile forcing the descent every frame buys nothing: the
request loop keys off `_loaded` membership and finds nothing new on an unchanged cover, and the release loop
keys off cover membership and finds nothing leaving. Those are the frames already under consume load, so the
descent they would pay for is the one that costs most.

### 4.2 The budget-zero asymmetry

`PumpPending` reads a cap of `0` two different ways. The build and vertex caps treat it as *uncapped*, so an
unset config field is harmless. The consume cap is used directly, so `0` *blocks* consume — which is how a
test builds a backlog. The two readings of the same value are live; §12 carries the open question.

### 4.3 The paint-order seam

`PumpPending` sorts its work list by the priority context before the processing loop. Without the sort a
corner tile wins the per-Tick kick and consume race from `Dictionary` enumeration order even though
admission is already priority-ordered — the cover fills from its edges and the middle stays white. The sort
is as load-bearing as the admission gate. It reuses the shared `_toRelease` scratch field, which is sound
because the list is filled and fully consumed inside this one single-threaded call and is never live across
calls.

### 4.4 Deferred release, and re-validation at the dequeue

A zoom-out or a fast pan takes N tiles out of cover at once, and each record costs L entity destroys, L cache
puts and a mesh-destroy burst. Releasing them in the frame they leave is the mirror image of an unbudgeted
consume, so the Tick **enqueues** instead: a record leaving cover joins `_releaseQueue`, deduped by
`_releaseQueued`. `DrainReleaseQueue` frees up to `MaxReleasesPerTick` records per Tick.

Each dequeue is **re-validated against the live cover** before it spends budget. A key whose tile is back in
`_coverSet` (the camera panned back during the one-to-three-frame linger) or whose record is already gone (a
restyle cleared it) is dropped without a release. That turns pan-out-pan-back from destroy-and-refetch churn
into a no-op.

A queued record stays in `_loaded` and is still pumped while it waits, so its in-flight work proceeds to the
pens of §3.3. The kick branch skips a record in `_releaseQueued`: a condemned tile does not start a build.
Both containers are pre-sized and the steady state never touches them.

### 4.5 One structural change per consumed mesh, and per released record

Each consumed mesh costs the Entities backend a structural change, and each released record costs one per
layer. Both are bounded by the caps of §2, so both must also be cheap per unit.

**On the way in**, `AddTileLayer` instantiates a prototype entity and sets an ID-based `MaterialMeshInfo`
built from `EntitiesGraphicsSystem.RegisterMesh` and `RegisterMaterial` — EG's own intended fast path, one
structural change plus a registry add. There are two prototypes, one per `ShadowCastingMode`, because shadow
casting lives in `RenderFilterSettings` — an `ISharedComponentData`, so writing it per entity would itself be
a structural change per layer, which is the cost this path exists to avoid. Both prototypes share one
`RenderMeshArray` **value**, inert and ID-overridden, so no instance ever creates a per-entity array.
Registration obliges its mirror:
`RemoveItem` and `RemoveItems` must call `UnregisterMesh`, or EG's registry grows without bound holding
destroyed meshes' ids. Materials unregister at disposal, and only while the world is still alive — world
disposal tears down EG's registries wholesale, so that call is for symmetry rather than correctness.

**On the way out**, `ITileRenderBackend.RemoveItems` removes many draw items in one backend operation. The Entities backend
implements it as a single `EntityManager.DestroyEntity` over a persistent scratch list holding every layer
entity plus any tile root the batch emptied — one structural change per released record rather than L+1. BRG
and GameObjects fall back to a `RemoveItem` loop, and every implementation is idempotent for an unknown
handle. `RenderTeardownRecord` unregisters through it. With a release budget of 4 and 30 layers a worst frame
costs at most 4 structural changes instead of 450.

## 5. The bake — what a prepared tile is a function of

A tile's mesh is baked at the tile's **integer** zoom (`TileId.Z`), never at the fractional camera zoom, and
exactly once per load — a record is never re-baked while it is `Built`. Call sites take `CameraProperties`
for other reasons; the bake does not read it.

**This is the bake-parameter SSOT.** The prepared artifact is a pure function of the closed set:

> `{ Tile, Zoom = id.Z, origin = f(tile, projection), projection, bufferClip }` + the typed paint and layout
> (the style content **and** `MapViewConfig.FillAntialiasing`) + the built layer numbering.

The rule that licenses `PreparedTileCache` holding no purge of its own: **a new bake input either enters the
cache token or gets its own purge.** Today's inputs split across the two mechanisms. Content,
`FillAntialiasing` and the built layer numbering fold into `TileManager.CurrentStyle`'s `StyleToken`
(`MapView.SetStyle`, keyed through `JsonCanonical.CacheKey`). The clip window has its own diff-and-purge in
`TileManager.TickCore`, where a changed `BufferClip` clears the prepared cache directly. `Zoom = id.Z` and
the projection are session-constant, so neither needs a token component or a purge.

**`MapViewConfig.MaterialSet` is assumed baked into the scene and constant for the session.** Nothing
enforces it: `MaterialSet` is a serialized public field any caller could reassign, and test setup does. The
assumption is load-bearing. Layer numbering is a function of the style's layer set **and** the material set,
so a material set that could change mid-session would let identical style content produce different
numbering, and any cache of baked geometry would then have to track numbering separately from content.
Because it cannot change, the token derives from style content alone. Stating the assumption is what removes
the whole mechanism — the alternative is a signature fold, a per-token signature map and a conditional purge,
none of which is reachable.

**Why the layer-numbering fold is per-index, not a count.** `MapView.LayerNumbering` folds each
`(index, StyleLayer.Id)` pair rather than `RenderLayerSet.Count`. `MapMaterialSet.Validate` requires
`FillMaterial`, `LineMaterial` and `SymbolTextWorld`, so `FillExtrusionMaterial` is the one material whose
absence makes a layer lose its slot: `FillExtrusionRenderLayer.TryCreate` returns null and
`RenderLayerSet.Build` skips it. Symbol and background always take their declared slot whatever the material
config, because `RenderLayerFactory` routes them through a `Create` that never returns null, so the layers
after them keep their numbering. A material-set mutation therefore has exactly one degree of freedom — it can
change the built layer **count**, never **permute** at a fixed count, since every other skip reason is
content-driven and content already sits in the token. A count-only fold is behaviourally equivalent today,
but on a property of today's `Validate`, not on a declared invariant. A second slot-dropping material field —
of any layer kind, because dense ids index the full layer list, so a non-mesh slot vanishing shifts every mesh
layer after it — would make permutation reachable, and a count fold would then serve one layer's mesh under
another's material, silently. The per-index fold costs one `StringBuilder` per style load, on a path that
already awaits.

### 5.1 Cache transfer is scoped to eviction, never to restyle

`TileManager.ReleaseTile` transfers a fully-built tile's meshes into `PreparedTileCache`. That transfer lives
in `ReleaseTile` — a genuine eviction — and is **not** folded into `RenderTeardownRecord`, which `SetSources`
also calls for every record on a restyle. Caching those meshes there would file them under whatever
`CurrentStyle` held at that moment, so a later hit on a coincidentally-matching `(tileId, layerId)` could
serve stale-style geometry. Never transferring on that path sidesteps it, and a restyle keeps its
always-destroy behaviour. With the cache disabled the transfer is skipped and `RenderTeardownRecord`
destroys the record's meshes exactly as it did before the cache existed.

### 5.2 A hit must be complete on both sides

The same release that banks the meshes also drops the tile from the byte `TileCache`, through
`SourceRegistry.ReleaseTile`. A prepared-cache hit and a byte-cache hit are therefore mutually exclusive: for
exactly the tiles the mesh cache can serve, a refused hit costs a network round trip, not just a decode.

A hit marks the record `Built` and `FetchCompleted`, so `PumpPending` never reaches it and its symbol build
never kicks. `AdmitTile` therefore requires the symbol store to still hold a committed block for the tile
(`ISymbolTileWorkerFactory.SymbolsCachedFor`) before it serves one. Without that check a full-rebuild restyle
— which clears the symbol store while the mesh cache keeps its entries under their content token — re-shows a
panned-back tile as geometry with every label permanently gone. The remedy is always to make the hit
predicate stricter, never to loosen what a hit implies.

## 6. Restyle: the full rebuild

`TileManager.SetSources` is the `MapView.SetStyle` full-rebuild entry point and handles both the first call
and a restyle in one pass.

1. **Render-teardown every existing record** — destroy meshes, unregister draw items, stash in-flight work in
   the pens — and clear `_loaded`. The old records reference the old layer indexing and the old backend, so
   they must go. This touches no scheduler and no cache, so a kept source's warm cache survives.
2. **Diff the registry by `SourceKey`.** A spec whose `(SourceId, Key)` matches an existing pipeline keeps
   that pipeline instance, with its source, scheduler and cache, so already-fetched bytes are reused. A new or
   changed spec builds a fresh pipeline. A pipeline matched by no spec is torn down, disposing its scheduler
   and its source if it owns one.
3. **Rebuild the backend** (the material list changed) and re-arm cover selection. The next Tick re-requests
   the cover and hits the kept caches.

The teardown loop is the one mutation site that repeats within a single `SetStyle`. `MapView.CommitProbe` is
a test-only `Action<CommitPhase>`, null in production, that fires after each of this arm's mutation sites in
commit order — so the order is a defined, observable property of the rebuild rather than an incidental one.

## 7. Partial-survival restyle — slot vs draw order, the tombstone, the three exits

A restyle that reorders or removes one layer must not rebuild the other hundred. That is possible only
because a layer's **slot** and its **draw order** are separate quantities. One list index carrying declared
order, draw order, material index and backend slot at once cannot express a reorder or a removal without
collapsing all four into a fresh `0..N-1` sequence — which forces the full rebuild of §6, every tile's mesh
for every layer.

**Slot vs draw order.** `IRenderLayer.DrawIndex` is purely the **slot**: the backend `materialIndex`, the
`LoadedTile.MaterialIndices` entry, `PreparedKey`'s layer id. `RenderLayerSet.Build` sets it once and it is
stable across a restyle for a surviving layer. **Draw order** is a separate quantity — the layer's position in
the current document's `layers` array — written only into `Material.renderQueue` through
`IRenderLayer.SetDrawOrder` (`LayerDrawOrder.QueueFor`). A fresh `Build` is the only place the two coincide,
because it has no survivors yet; a reorder moves draw order without moving any slot.

**The E1 null placeholder.** A symbol or background slot whose base material is unconfigured stores a `null`
entry in each backend's full-width material list — a placeholder, not registered with the engine — so the list
stays full-width aligned. `AddTileLayer` is never called for that index either way.

**The tombstone.** A removed layer's slot becomes a `TombstoneRenderLayer`: material-less, mesh-less,
implementing neither `ITileMeshRenderLayer` nor `ISpriteConsumerRenderLayer`, and never `null`. Every site
that already tolerated a null `Material`, and every site that would throw on a raw null, skips it unchanged.
One site does need an edit: `TileManager.ConsumeMeshBuild`'s guard over an already-kicked build, whose
payload carries the slot it was built for. The list width never shrinks, so its `materialIndex < Count` check
cannot see a retirement and it tests for `TombstoneRenderLayer` itself. `ITileMeshRenderLayer` is the wrong
predicate there — a background layer implements neither interface yet does register a per-tile quad, so that
test would drop every background payload.

**`RenderLayerSet.TryRestyleInPlace`'s three exits.** The diff is **id-keyed**, not the by-reference,
same-index walk it replaced, which cannot express a removal or a reorder at all. Walk the new document's
layers in order and look each id up among the old document's rendered layers:

1. **Not found, and not an unchanged never-rendered layer either** — a genuinely added layer, or one whose
   never-rendered content changed → refuse the whole restyle and fall through to the full rebuild. Letting an
   addition survive in place needs a per-layer mesh build onto an already-loaded record, which does not exist.
2. **Found, but its mesh-affecting signature changed** (`SurvivingLayerGate.LayerSurvives`) → refuse, same
   fallthrough.
3. **Found and survives** → keep its slot, `Restyle` its uniform bindings, `SetDrawOrder` it to its new
   declared position.

The old side is keyed **twice**, and both maps are needed. The slot map is built over `_layers`, which holds
only the layers `Build` could render and is therefore shorter than `oldStyle.Layers` whenever any layer was
skipped — one of `liberty.json`'s 111 is a `raster`; a rendered layer whose `StyleLayer.Id` is null refuses
the whole restyle rather than risk pairing the wrong layer. The second map is over `oldStyle.Layers` and
exists only so an unchanged never-rendered layer can be told apart from a genuine addition at exit 1. Without
it, that raster layer would pin every liberty restyle to a full rebuild.

Any old slot no exit-3 claim reached is a removal, tombstoned in a **second pass** after every layer is
classified, so a mid-walk refusal leaves `_layers` completely unmodified. That is the method's contract:
`false` means nothing changed.

**The symbol-removal fence.** A removed layer never reaches `LayerSurvives`, because the classification walk
visits only layers present in the new document. A fourth refusal therefore runs over the unclaimed slots,
still inside the read-only pass — a check placed in the mutation pass would run after the first `Dispose` and
break the unmodified-on-refusal contract. Its predicate is *"this slot's render layer is referenced by a list
the in-place arm does not refresh"*. `MapView._symbolRenderLayers` is the only field outside `RenderLayerSet`
that holds render-layer references — every other site threads them as a parameter — so `SymbolRenderLayer` is
the only kind that qualifies today, and a second such field is what would change the answer. Without the
fence that list outlives the disposed slot and `SymbolPlacementSystem.Tick` resolves materials
`SymbolRenderLayer.Dispose` has destroyed. A removed symbol layer takes the full rebuild instead. A
**reorder** is not fenced: that arm skips the `_symbolRenderLayers` rebuild and `SymbolSubsystem.SetStyle`
together, so the list and the subsystem's slot numbering stay mutually consistent, and removal is the only
class that mutates a slot.

**The record-keep fence.** `TileManager.SetSources` (§6) stays unchanged: a full rebuild replaces every render
layer's material and nothing else re-derives which already-loaded tile needs a fresh mesh, so it tears down
every record. The partial-survival arm calls a separate, narrower entry point,
`TileManager.RestyleSourcesInPlace`, which diffs the registry through `SourceRegistry.Rebuild`'s
old-slot→new-slot map and tears down only a record whose pipeline departed; everything else is re-keyed to its
new slot. `Rebuild` reuses the **synthetic background** pipeline's identity across the diff for the same
reason it reuses a real one's: that pipeline holds no resource, but a fresh instance minted each call would
read as departed on every restyle that has a background, tearing down every record parked there for no change.

`RestyleSourcesInPlace` also always pushes the current per-layer material and shadow lists into the backend
through `ITileRenderBackend.SetLayerMaterials`, even for a pure reorder with no source change, because that is
the only place the BRG backend's cached per-item render queue is re-stamped.

**`MapView.SetStyle`'s in-place branch.** `TileManager.SourcesUnchanged(specs)` is evaluated **before**
`RenderLayerSet.TryRestyleInPlace`, which re-binds every surviving layer's applier and retires a removed
layer's slot: there is no reason to pay that when the source set alone already disqualifies the restyle. That
pre-diff answer is allowed to go stale, because it gates only *entry* to the arm. The one thing the layer diff
can change — a background layer going away, which retires the synthetic source-less pipeline — is re-read
inside by `SourceRegistry.Rebuild(specs, HasBackgroundLayer())` after the diff, so the departed-pipeline
teardown still finds that record.

Once inside the branch, four steps stay skipped because none has anything to redo. `RenderLayerSet.Build`
would discard and rebuild every layer, which is what the arm exists to avoid. `TileManager.CurrentStyle`'s
token folds in the layer numbering, which an in-place restyle never moves — a removal tombstones a slot, it
never shifts one, and a vacated slot's cache entries are unreachable and evicted by budget.
`SymbolSubsystem.SetStyle` rebuilds label state for a changed symbol set, and the removal fence above already
refused every symbol layer this branch could reach. `LogSkippedLayers` would re-log a compatibility summary
`Build` never re-populated.

**No per-frame cost.** `SetLayerMaterials` re-stamps `BRG.TileRenderer.DrawItem.LayerRenderQueue` once, at
restyle time — never a live `material.renderQueue` read inside the per-frame culling callback. A slot whose
material went from a live one to `null` has its live draw items removed in the same call, in all three
backends: a layer removal that touches no source pipeline leaves the departed-pipeline teardown with nothing
to tear down.

**The three backends do not do the same work here**, and a reader comparing them should expect that. BRG drops
the `DrawItem` dictionary entry and nothing else — it has no per-item mesh unregister. The GameObjects backend
parks the child in its pool, likewise with no engine-side unregister. The Entities backend additionally calls
`EntitiesGraphicsSystem.UnregisterMesh` and decrements its registered-mesh count immediately, ahead of the
ordinary tile-release timing that count otherwise tracks. In every backend the tile's `Mesh` asset is
untouched — `TileManager` still owns it and destroys it at ordinary release — so a record briefly holds a
`Mesh` no backend draws: a bounded hold, not a growing leak.

**`SetLayerMaterials` has two implementation traps.**

1. A retiring slot's old material must be compared with `ReferenceEquals(oldMat, null)`, never `oldMat != null`.
   Unity's overloaded `==`/`!=` reports a destroyed object as fake-null, and the old material is already
   destroyed by the time this runs — `TryRestyleInPlace` disposes a removed layer before
   `RestyleSourcesInPlace` reaches the backend. `oldMat != null` would skip the unregister in EditMode, where
   `DestroyImmediate` makes the fake-null appear at once, while still running it in a shipped player, where
   `Destroy` defers to frame end. The two environments would take opposite branches, so an EditMode tooth over
   a registration count would measure a path production never takes.
2. A caller must call every survivor's `IRenderLayer.SetDrawOrder` **before** `SetLayerMaterials`. The BRG
   render-queue re-stamp reads each slot's material as it is at that moment, so the other order re-stamps the
   old queue.

## 8. The draw gate — a layer draws, or it is not submitted

One rule decides this section: **a layer that paints nothing the framebuffer can show does not reach the
GPU.** Where that is decided depends on whether it can change while the style is loaded.

### Decided once, at construction

Two properties can never turn on later in the session, so `RenderLayerFactory.Create` refuses the layer
outright — no `IRenderLayer`, no slot, no material, no backend registration, and no mesh built for it on any
tile:

| property | skip reason |
|---|---|
| `layout: {"visibility": "none"}` | `LayerSkipReason.Hidden` |
| an opacity that is a **constant** below `ZoomStyleApplier.VisibleOpacityEpsilon` (one 8-bit step) | `LayerSkipReason.FullyTransparent` |

`RenderLayerFactory.TryGetFetchSource` applies the same two tests, so a source whose only readers can never
draw is never fetched and its tiles are never decoded. Both are the author's intent rather than a renderer
limitation, so `MapView.LogSkippedLayers` stays silent about them, as it already does for a source-less symbol
layer.

A **zoom-dependent** or **feature-dependent** opacity is not in that table. Neither reduces to a decision that
holds for the whole session: the first changes with the camera, and the second cannot be represented by any
single per-layer scalar.

### Decided per frame, at submission

What is left varies with the camera, so each remaining layer carries a **fade factor** `p` in `[0,1]`. It is
not a style property. Its target is a boolean from one predicate, `StyleLayer.IsVisibleAtZoom(liveZoom)`,
evaluated per layer per frame in `RenderLayerSet.ApplyZoom`. `p` eases to its target over `StyleTransition`
and multiplies the layer's `_Opacity` at the one `SetFloat` that pushes it.

The gate reads the product, not fade alone:

```
ZoomStyleApplier.EffectiveOpacityIsZero  ==  fade * authoredOpacity < VisibleOpacityEpsilon
```

`IFadeableRenderLayer` re-exposes it and `TileManager.PushLayerDrawGates` pushes it per slot into
`ITileRenderBackend.SetLayerVisible`. That push sits beside **every** `RenderLayerSet.ApplyZoom` — the call
that refreshes both terms — so no frame can render between the two. The authored term is the unscaled value
`ApplyZoom` just pushed (the binding carrying `ScaledByFade`, whose sole writer is
`ZoomStyleApplier.BindOpacity`), so it is never a frame stale; a settled constant keeps its bind-time value,
which is its value at every zoom.

Two properties of that predicate are load-bearing, and each has its own tooth:

- **Settled, not merely heading for zero.** A layer mid-fade still shows something, so it keeps submitting —
  the fade needs something to blend. `fill-extrusion` never takes an intermediate value at all:
  `RenderLayerSet.AdvanceFade` substitutes `StyleTransition.Instant` when
  `FillExtrusionRenderLayer.FadesGradually` is false, because `FillExtrusionTweaker.ApplyElevatedContract`
  blends `One`/`Zero` with depth write on. Alpha is discarded there, so a partly-present building would render
  solid rather than translucent.
- **A feature-dependent opacity fails safe.** All four paint binders in `Materials.MaterialFactory` bind a
  constant `1` when `Opacity.DependsOnFeature`, because the per-feature value is baked into vertex alpha
  instead. The authored term reads 1, so such a layer is never gated out on a value that does not describe it.

**Why fade rides `_Opacity` rather than a dedicated uniform.** `_Opacity` has one binding path
(`ZoomStyleApplier.BindOpacity`), so fade composes with authored opacity at a single site. A dedicated uniform
would cost a shader property, a CBUFFER member and a DOTS instanced property in every one of the three kind
families, and would move every parity count — for a value always multiplied into `_Opacity` anyway.

### How each backend honours the gate

The three differ in mechanism and must agree on outcome — the same shape `IRenderLayer.CastShadows` carries.

| backend | mechanism | cost |
|---|---|---|
| BRG | the slot is skipped in `ComputeEmitOrder`, for the camera and the light view | one list read per item per cull |
| GameObjects | `Renderer.enabled = false` on that slot's layer children | one write per item, on change only |
| Entities | `DisableRendering` added to that slot's entities | one batched structural change, on change only |

`AddTileLayer` consults the gate too: tiles keep finishing while a layer is gated, so an item registered into
an already-gated slot must arrive undrawn. BRG gets that from reading the gate at emit time; the other two
apply it per item as they build it. The gate is two-way — lifting it re-enables the slot — and that return
trip is the half a "the layer disappears" test cannot see, so each backend's tooth asserts it explicitly.

**No map fragment pass reads fade, and a structure fence keeps it that way.** A discard on `_Opacity` would be
dead work, since a gated layer never reaches a fragment. The rule is enforced structurally rather than by a
rendered tooth because most of the passes it covers cannot run: of the 18 fragment passes across Fill, Line
and FillExtrusion only **7** can rasterise in the shipped configuration — the four forward passes of Fill and
Line, the two fill-extrusion forward passes, and the fill-extrusion ShadowCaster. The GBuffer passes never run
under Forward+ (`Renderer.asset` is `m_RenderingMode: 2`); DepthOnly and DepthNormals are built with
`RenderQueueRange.opaque` while every map layer sits at queue ≥ 3000; and the Fill and Line ShadowCaster
passes are dropped by all three backends, because those kinds declare `ShadowCastingMode.Off`. Every row of
that split is a configuration rather than a construction, so
`ShaderStructureTests.NoMapPassBody_DiscardsOnTheLayerFadeUniform` is the sole observer for the other 11.

### What the gate costs, and the one thing it gives up

A gated layer costs no vertex work, no draw call, no depth and no shadow — but its mesh still exists, because
the gate acts after the tile is built. Construction-time refusal gives up something else: a skipped layer
cannot **ease** back in. Flipping `visibility` to `visible`, or an opacity from a constant `0` to a constant
`0.8`, changes the built layer set, so §7's id-keyed diff refuses and the full-rebuild arm runs instead of a
fade. Correct, merely not fast, and identical for both skip reasons.

## 9. The build seam this design hands over

`docs/job-scheduling-design.md` owns the two-phase build split and its exact-size allocation: the `BuildStep`
state a record moves through, the rule that a step transition of an admitted tile is uncharged against the
kick cap, and one `MeshDataArray` per non-empty layer sized to that layer's measured count. Its § "What this design
owns from `docs/tile-pipeline-design.md`" states the boundary in full. This design owns what surrounds that
seam — admission, the caps of §2, consume, the cache and release.

There is no managed mesher interface between the two phases. The graph builder plus the stream-write job are
the measure/write split, and `IRenderLayer.WriteInto`, the managed per-layer write entry point that preceded
them, has no callers left.

## 10. Off-main symbol shaping — the shared-atlas hazard

Symbol decode and feature extraction already run off the main thread, kick-driven through
`ISymbolTileWorkerPass.RunWorkerAndHandoff` over the tile's shared decode
(`docs/per-layer-tile-processing-design.md`). Moving **shaping and layout** off as well is not built, and one
hazard governs how it can be.

Shaping reads `GlyphCache` through `FontStackResolver` and `GlyphAtlas` through `IGlyphAtlasView`, while the
main thread may concurrently run *another* tile's first pass — `GlyphCache.Store` plus `GlyphAtlas.Append`,
both `Dictionary` mutations. A worker reading those structures mid-mutation reads torn state.

The shape a fix must take: after this tile's first pass completes on main, every glyph it needs is cached and
appended, and the atlas is fixed-size, so its dimensions are constant. Capture **on the main thread** an
immutable copy of exactly the read surface the shaper and the layouts consume — glyph metrics and atlas
entries for the codepoints this tile's texts reference — then hand that copy to the worker. Atlas and cache
**writes never leave the main thread** and no lock is added. A worker lambda that captures only the snapshot
cannot see the live atlas, which makes the fence a type fact rather than a discipline. The cost is one
snapshot per tile build, on the load path.

## 11. Rejected alternatives

- **Per-chunk mesh output, and a per-layer vertex cap to force it.** The cap was meant to stop one
  100k-vertex layer uploading whole, since `MaxVerticesPerTick` is checked before each layer. Three facts
  kill it. `MaxConsumesPerTick` already bounds meshes uploaded per Tick and consume is already mesh-by-mesh,
  so a rich tile already spreads across frames. The main-thread cost is mostly **per-mesh**, not per-vertex:
  `MeshDataPayload.Upload` does `new Mesh` plus `ApplyAndDisposeWritableMeshData` with validation and bounds
  recalculation disabled, while filling, bounds and index validation are already off-main. So splitting a
  layer into four leaves the uploaded bytes unchanged, quadruples the per-mesh cost, and spends the whole
  consume budget on one layer. Revisiting it needs a measurement first — a marker around the per-mesh
  main-thread work over a low-zoom pan — and it should budget meshes rather than vertices.
- **A `Mesh[]` value in `PreparedTileCache`, so a chunked layer round-trips without a put collision.** It
  existed only because one layer could become more than one mesh. One mesh per layer keeps the current value
  shape correct.
- **A per-entity `RenderMeshArray` for each consumed mesh.** A fresh one-element shared component per mesh
  forces a batch registration per entity on top of `CreateEntity` and a component-type migration — three
  costs where the prototype path of §4.5 pays one structural change plus a registry add.
- **BRG as the ship default.** Entities keeps the Entities-Hierarchy debuggability that is the backend's
  reason to exist, and BRG's own steady-state cost would have to be fixed first: its `Rebuild` sorts every
  item, repacks 76 floats and 33 material-property reads per item, and does a full `SetData` every frame. That
  is a steady cost rather than a spike, and it scales with item count — which is another reason per-chunk mesh
  output (above) is the wrong direction. BRG stays the zero-allocation opt-in.
- **An indexer on `SourceRegistry`** returning the pipeline object. §3.2.
- **Removing a layer and rebuilding it as the camera crosses its zoom bounds**, instead of gating it (§8).
  The prepared mesh cache would not absorb the re-entry; it would be discarded wholesale on every crossing.
  `PreparedKey` is (`StyleToken`, `TileId`, `LayerId`), and `StyleToken` digests `MapView.LayerNumbering` —
  the `index:id` pairing of the **built** set. A layer set that varies with zoom changes the token at every
  crossing, re-keying every entry for every tile and every layer, not just the layer that moved.
  Independently, `LayerId` is the slot index, so removing a layer mid-list renumbers every later layer and
  their cached meshes are keyed wrong. Each crossing would cost a full re-decode and re-mesh of the whole
  cover, and 38 of `liberty.json`'s 111 layers declare a `minzoom` or a `maxzoom`. The slot model cannot express "absent at this zoom,
  present at another" either: a skipped layer **compacts** the numbering, since `RenderLayerSet.Build`
  increments `drawIndex` only on the real-layer branch. `TombstoneRenderLayer` (§7) removes a layer while
  holding its slot width, which fixes `LayerId` and `LoadedTile.MaterialIndices` but is **not** sufficient —
  the style token would additionally have to stop depending on which layers are currently present. That is a
  separate decision about what the cache key means; today it captures the built numbering so that a style
  whose layer set changed cannot silently reuse meshes.

## 12. Open

- **The budget-zero asymmetry (§4.2).** `MaxConsumesPerTick == 0` blocks consume while the build and vertex
  caps read `0` as uncapped. Unifying them costs the tests their only way to build a backlog, so it needs an
  explicit block-consume seam on the manager in the same change. Until then the same literal means two things
  in one method.
- **Off-main shaping and layout (§10).** Unbuilt. The hazard and the snapshot shape are settled; the entry
  point is not.
- **BRG's per-frame repack (§11).** Dirty-flagging material properties, repacking transforms only on a frame
  change, and a partial `SetData` are the obvious levers. They matter only if BRG is ever reconsidered as the
  default.

## 13. Grounding (file:symbol touch points)

`MapRenderer.Unity/Rendering/Tile/`: `TileManager` (`TickCore`, `AdmitFromDesired`, `PumpPending`,
`ConsumeMeshBuild`, `DrainReleaseQueue`, `ReleaseTile`, `RenderTeardownRecord`, `SetSources`,
`RestyleSourcesInPlace`, `SourcesUnchanged`, `ComputeDenseLayerIds`, `LoadedTile`/`LoadedKey`),
`SourceRegistry` (`Rebuild`, the ten slot-keyed operations), `PendingDisposalQueue` (`DrainCompleted`),
`CoverKeyGate`, `PreparedTileCache` / `PreparedKey`, `StyleToken`,
`Processing/ISymbolTileWorkerFactory` (`SymbolsCachedFor`, `TryBeginBuild`),
`Processing/ISymbolTileWorkerPass` (`RunWorkerAndHandoff`).
`MapRenderer.Unity/Rendering/Map/`: `MapView` (`SetStyle`'s two arms, `LayerNumbering`, `CommitProbe`,
`_symbolRenderLayers`), `MapViewConfig` (the caps, `FillTileBufferClip`, `MaterialSet`).
`MapRenderer.Unity/Rendering/Style/`: `RenderLayerSet` (`Build`, `TryRestyleInPlace`, `ApplyZoom`,
`AdvanceFade`), `TombstoneRenderLayer`, `SurvivingLayerGate`, `RenderLayerFactory` (`Create`,
`TryGetFetchSource`), `LayerSkipReason`, `ZoomStyleApplier` (`BindOpacity`, `EffectiveOpacityIsZero`,
`VisibleOpacityEpsilon`), `LayerDrawOrder.QueueFor`, `IFadeableRenderLayer`,
`FillExtrusionRenderLayer` (`TryCreate`, `FadesGradually`).
`MapRenderer.Unity/Rendering/Backend/`: `ITileRenderBackend` (`AddTileLayer`, `RemoveItem`, `RemoveItems`,
`SetLayerMaterials`, `SetLayerVisible`), `Entities/TileRenderer` (the layer prototypes, `RegisterMesh` /
`UnregisterMesh`), `BRG/TileRenderer` (`ComputeEmitOrder`, `DrawItem.LayerRenderQueue`),
`GameObjects/TileRenderer`.
`MapRenderer.Unity/Rendering/Materials/`: `MapMaterialSet.Validate`, `MaterialFactory`,
`FillExtrusionTweaker.ApplyElevatedContract`.
`MapRenderer.Core/Text/`: `GlyphCache`, `GlyphAtlas`, `FontStackResolver`, `IGlyphAtlasView`.
`MapRenderer.Unity/Text/Placement/`: `SymbolPlacementSystem.Tick`.
