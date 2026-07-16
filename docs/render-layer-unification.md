# Render-layer unification — design (every layer kind first-class)

Status: **IN PROGRESS** (hand-driven). Author: cleanup epic, 2026-07-01; **extended 2026-07-12** into the
full all-kinds redesign (this revision). Round 1 (fill/line + Burst mesh build, stages A–D2) is **shipped**;
round 2 (symbols / background / raster as first-class render layers, stages E0–E3) is **designed here**.

The goal, restated: `ARCHITECTURE.md` §"Layer ordering" — *"the style is an ordered list of layers,
composited in order."* Round 1 made that true for fill and line. Round 2 makes it true for **everything the
style can paint**: every painted layer gets a uniform place in ONE model along three orthogonal axes —
**build/lifetime** (how its geometry comes to exist), **draw order** (its slot in the global painter's
chain), and **presence** (whether a backend redraws it by itself or an orchestrator must re-issue it every
camera render). The axes are named and kept separate; the model does NOT pretend symbols build like fills.

---

## 0. Shipped so far (round 1 — progress log, preserved record)

- **Stage A — DONE (`92e2e0b`, 2026-07-01).** `IRenderLayer`/`RenderLayerSet` + `RenderLayerFactory`;
  fill/line unified into one ordered `List<IRenderLayer>` (index == draw order == material index);
  `MeshBuildResult` two lanes → one `IRenderLayerPayload[]`; all 3 backends index the one declared-order
  material list; `FillCount+li` flatten + "fills first" comments gone (from code — see §2.8 for the stragglers
  in doc comments). Behaviour-preserving (906 EditMode green). `RenderLayerSetTests` locks: one ordered list,
  interleaved not type-bucketed, monotonic queue, non-renderable takes no slot, factory is sole dispatch.
- **Stage B — DONE (2026-07-01, 907 green).** `Mesh.MeshData` replaced bespoke `LayerMeshData` + the
  hand-rolled 4-stream consume; one `MeshDataPayload` handle per `(tile, layer)`. Spike-verified threading
  rules (`MeshDataThreadWriteSpikeTests`): write on worker OK; `AllocateWritableMeshData` /
  `ApplyAndDisposeWritableMeshData` main-thread only. Choreography locked: allocate at KICK (main), write on
  WORKER, apply per-layer at CONSUME (main, S87-budgeted). Leak guard → `MeshDataPayload.DebugLiveAllocCount`.
- **Stage D1 (line kernel) — DONE (2026-07-02, 915 green).** `LineRibbonJob` = faithful `[BurstCompile]`
  transliteration of managed `LineTessellator.Triangulate` (all joins/caps), bit-comparable to the oracle;
  `LineRibbonJobTests` differential oracle (strict on arithmetic paths, 1e-9 on transcendental round).
  Fill kernels were already oracle-validated strict by `JobifiedPipelineTests`.
- **Stage C (dense per-source produce) — DONE (2026-07-02, 915 green).** Killed the decision-5c full-width
  sparse union. `MeshBuildResult.Payloads` is dense per-`(tile, source)`; each `MeshDataPayload` **carries its
  own `MaterialIndex`**; consume uses `payload.MaterialIndex` with a restyle-shrink guard; `ConsumeCursor` is
  a dense resume index. Snapshots unchanged.
- **Stage D2 (fills, then lines → live Burst) — DONE (2026-07-02, 917/918 green).** Key **reconception vs
  the original §3.4 JobHandle plan**: the GC win is entirely in Phase-1 geometry, so the Burst kernels run
  **synchronously on the existing `UniTask` worker via `.Run()`** — no lifecycle rewire (kick/consume/
  S48/S84/S87 unchanged), spike-proven off-main (`BurstJobRunOffMainSpikeTests`). `StyledFillTileBuilder`
  managed Phase-1 deleted; `StyledLineTileBuilder` swaps per-ring managed tessellation for `LineRibbonJob.Run()`.
  **S89 complete: managed Core geometry (`Earcut`/`PolygonAssembler`/`LineTessellator`) is retired from the
  live path — differential oracle only.** The original §3.4 "UniTask→JobHandle + drain pen" design is
  **superseded** by this reconception; it is NOT pending work.
- **Scope decision (2026-07-01, locked, for the record):** one epic A→B→C→D1→D2, Burst included ("all-in,
  D now"); the A+B-only alternative was considered and rejected with the full cost visible.
- **Round-1 "Why" (resolved, kept for the record):** `StyledLayerSet` split `_fills`/`_lines` against the
  declared order; `materialIndex` was a second fills-then-lines ordering hand-rolled in TileManager + all 3
  backends; `MeshBuildResult` carried two typed arrays; per-source produce was full-width sparse (5c); mesh
  build was managed with a hand-rolled 4-stream consume; the geometry stack was managed-only. All six were
  one root — fill and line modeled as two parallel typed lanes — and all six are fixed above.
- **Round-1 locked decisions (still binding):**
  - **D1** — `IRenderLayer` (Unity runtime render object) / `StyleLayer` (Core parsed data) split.
  - **D2** — one epic, internally sequenced, `Visual/` snapshot parity + EditMode green throughout.
  - **D3** — full Burst Job System for static mesh build (shipped as `.Run()`-on-worker, see D2 note).
  - **D4** — the layer owns its `VertexAttributeDescriptor[]` (the byte-for-byte job↔MeshData↔shader contract).
  - **D5** — `Unity.Collections` throughout the blittable path.

The shipped fill/line build pipeline (managed decode/select → blittable inputs → Burst kernels `.Run()` on
the worker → `Mesh.MeshData` → budgeted main-thread apply → `AddTileLayer`) is **sound, green reality**.
Round 2 builds ON it and does not reopen it.

---

## 1. Why, round 2 (the current mess: everything that isn't fill/line is a bolt-on)

Verified against source 2026-07-12. The render-layer model covers exactly two of the style's painted kinds;
each other kind is handled ad-hoc, outside the model, with its own private answer (or no answer) to draw
order, presence, and lifecycle:

1. ~~**Symbol layers take no draw slot.**~~ **FIXED (E1/E2).** `RenderLayerFactory.Create` returns `null` for anything but
   `Fill.StyleLayer`/`Line.StyleLayer` (`Rendering/Style/RenderLayerFactory.cs`), and `RenderLayerSet.Build`
   skips null — so symbol layers are invisible to the one ordered list and the `renderQueue = TransparentQueue
   + drawIndex` numbering (`RenderLayerSet.Build`, line ~65). Their per-layer materials are cloned by
   `SymbolLabelSubsystem.SetStyle` (halo bound via `BindHalo`; **no `renderQueue` write anywhere in `Text/`**).
   *(Preserved record of the pre-E1 state — E1 gave symbols a slot (D7), E2 made them material-bearing (D11)
   and wrote their queue like every other layer.)*
2. ~~**All symbols pin to Overlay 4000, one z-group on top.**~~ **FIXED (E2).** `Shaders/Map/Symbol/Text/SymbolText.shader`
   declares `"Queue" = "Overlay"` (4000) + ZTest Always, and its own header admits the debt: *"(S105 will
   place symbol layers at their real per-style draw position; this is the demo's 'labels on top of
   everything' default.)"* Consequences: a fill/line declared ABOVE a symbol layer can never occlude its
   labels (wrong vs the spec's painter order), and order AMONG symbol layers is undefined (equal queue,
   URP's remaining tiebreak is camera distance — near-equal for screen-space billboards).
   *(Preserved record — E2's `RenderLayerSet.Build` now overrides `renderQueue` per production symbol
   material to `TransparentQueue + DrawIndex`; the Overlay tag survives only as the demo/no-style default,
   see §3.5. Tooth §6.1/§6.2 lock this via `SymbolLayerOrderSnapshotTests`.)*
3. ~~**Presence gap — the Editor "blink."**~~ **FIXED (E2).** Labels draw via immediate-mode `Graphics.RenderMesh`, submitted
   once per frame from `LabelPlacementSystem.Tick` → `BuildAndSubmit` → `SubmitDraw`, driven by
   `MapView.LateUpdate` step 3. In the Editor the Game View repaints on mouse/UI **without running the player
   loop**, so those repaints get no re-submission → labels wink out while tiles (persistent backends) stay.
   Confirmed Editor-only (a build never blinked). No production code subscribes
   `RenderPipelineManager.beginCameraRendering` (grep: the only hit is a test-comment).
   *(Preserved record — E2 retired `Graphics.RenderMesh`/`SubmitDraw` entirely; labels now draw through
   persistent per-slot `MeshRenderer`s (`LabelSlotPresenter`, option (c), §5) Unity redraws on its own, so
   there is nothing left to "not re-submit." Manual Editor verify still owed — tooth §6.4.)*
4. ~~**Background is a hardcoded camera hack.**~~ **FIXED (E3).** A style's `background` layer parsed to the
   base `StyleLayer` (`StyleLayerType.Background`) and its `paint` was **never read** (no `background-color`
   consumer existed in production). Instead `Bootstrapper.Wire` set `mainCam.backgroundColor` to a hardcoded
   light-blue "sky" (`Rendering/Map/Bootstrapper.cs` ~line 148). The style's declared background color was
   ignored; a mid-stack background (legal per spec) was unrepresentable.
   *(Preserved record — E3 parses `background-color`/`background-opacity` into a typed
   `Background.PaintProperties` (Core, the Fill pattern) and gives `BackgroundRenderLayer` a real material
   (a fill-base clone) + a static world-cap ground quad on a persistent `MeshRenderer`, so a style's declared
   background renders at its declared draw slot — mid-stack included. The `Bootstrapper` sky survives only
   as the above-horizon clear / no-style default, §3.6. Tooth §6.5 locks this via `BackgroundSnapshotTests`.)*
5. **Raster has no path at all.** `StyleParser` recognizes raster sources (`SourceType.Raster`) and
   `StyleLayerType.Raster` parses, but no layer, no fetch, no draw — `Bootstrapper`'s header: *"silently
   skipped."*
6. **Layer-kind dispatch is scattered across three parallel type-switches** over the same declared list:
   `RenderLayerFactory.Create` (fill/line), `MapView.BuildSourceSpecs` (`is Fill.StyleLayer || is
   Line.StyleLayer || is Symbol.StyleLayer` — hand-tested to decide which sources to fetch), and
   `SymbolLabelSubsystem.SetStyle` (re-walks `style.Layers` for `Symbol.StyleLayer`). Adding a kind means
   finding all three — the exact "one registry" extensibility tooth the factory was built to protect.
7. ~~**Two label-material owners outside the model.**~~ **FIXED (E2, D11).** `SymbolLabelSubsystem` owns the per-symbol-layer clones
   (`_layerMaterials`); `LabelPlacementSystem` owns a separate default clone (`_material`). Neither is the
   render-layer model, which is supposed to be where per-layer materials live (Stage A's own rule:
   "layers own their materials").
   *(Preserved record — the per-layer clone + halo bind moved into `SymbolRenderLayer` (D11); the subsystem
   owns nothing material-related now. `LabelPlacementSystem._material` SURVIVES as the demo/no-style-seam
   default — that one is a deliberate non-goal, §4 — not a second production owner.)*
8. **Stale round-1 lies survive in doc comments.** `ITileRenderBackend.AddTileLayer`'s doc still says
   *"materialIndex indexes the flattened layer-material list (fills in declared order, then lines)"*
   (`Rendering/Backend/ITileRenderBackend.cs` ~line 23); `Backend.BRG.TileRenderer.AddTileLayer` and the
   GameObjects backend header repeat it. The flatten died in Stage A; the comments didn't.

All of 1–7 are the same root as round 1, one level up: **draw order, presence, and lifecycle registration
are answered per-kind, ad-hoc, instead of once by the model.**

## 2. Locked decisions (round 2 — D6 onward; D1–D5 in §0 still bind)

- **D6 — Three orthogonal axes on `IRenderLayer`, never collapsed.** Every painted layer declares
  (a) its **build kind** — `TileMesh` (built once per tile, Burst pipeline, backend-registered) /
  `FramePlaced` (rebuilt every frame from screen-space placement) / `ViewGeometry` (synthesized from the
  view, no tile data); (b) its **draw persistence** — `Persistent` (a backend redraws it every render on its
  own) / `Immediate` (an orchestrator must re-issue it every camera render); (c) its **global draw index**.
  These are independent: the table in §3.1 pins each concrete kind on each axis. `ARCHITECTURE.md`'s "two
  geometry classes" stands — it is the build/lifetime axis, and it stays real (symbol build is per-frame
  collision, NOT the Burst mesh pipeline; that is not changing).
- **D7 — One global draw-order index over ALL painted layers.** `RenderLayerSet.Build` walks `style.Layers`
  once and numbers every painted layer — fill, line, symbol, background, (raster, fill-extrusion when they
  land) — `renderQueue = LayerDrawOrder.TransparentQueue + drawIndex`. Symbol/background/raster positions
  therefore leave the right gaps in the fill/line offsets. Only genuinely unpainted kinds (unknown,
  unsupported-for-now) and unconfigured-material layers take no slot.
- **D8 — Global symbol collision, per-layer symbol draw** (MapLibre semantics). Collision stays ONE
  cross-layer pass (`LabelCollisionJob` over all candidates); each symbol layer draws its own survivors at
  its own queue via its existing per-slot mesh/material. The per-material-slot emit already maps
  1 slot ↔ 1 symbol layer ↔ 1 material — **no slot restructure**.
- **D9 — One orchestrator owns the single `beginCameraRendering` subscription.** It re-issues all Immediate
  layers in draw-index order, gated to the map camera (never SceneView/other), unsubscribed on teardown.
  Not per-layer subscriptions (reentrancy + teardown hazards). Building (placement, collision, mesh write)
  stays in `LateUpdate`; only the SUBMIT moves per-render.
- **D10 — Layer-kind dispatch has exactly one registry.** `RenderLayerFactory` becomes the only
  type-switch; `MapView.BuildSourceSpecs` and `SymbolLabelSubsystem` derive "which sources to fetch" /
  "which layers are mine" from the built `RenderLayerSet`, not from re-walking `style.Layers` with their own
  `is` checks (kills §1.6).
- **D11 — Per-layer materials live on the layer object, one owner.** `SymbolRenderLayer` owns its material
  clone (halo bind moves into it); `SymbolLabelSubsystem` consumes the layer set's materials instead of
  cloning its own (kills §1.7). Fill/line already work this way (Stage A).
- **D12 — The draw-mechanism decision (§5) gates all Immediate-draw wiring.** No E2+ code before the E0
  prototype answers whether `Graphics.RenderMesh` + `renderQueue` from the per-render callback is
  deterministic and viable, or whether Immediate draws must route through a
  `CommandBuffer`/`ScriptableRenderPass`.

## 3. Target architecture

### 3.1 The three axes, pinned per kind

| Layer kind | Build (lifetime) | Presence | Draw slot | Today (verified) |
|---|---|---|---|---|
| **Fill** | `TileMesh` — once per `(tile,layer)`, Burst kernels, `Mesh.MeshData` | `Persistent` — backend redraws (BRG `OnPerformCulling` / EG entities / MeshRenderers) | global index | shipped; slot among fill/line only |
| **Line** | `TileMesh` (same) | `Persistent` | global index | shipped; same |
| **Fill-extrusion** *(future)* | `TileMesh` (+ ZWrite on — depth-slice note, §7) | `Persistent` | global index | factory returns null |
| **Symbol/text** | `FramePlaced` — global collision → per-slot billboard mesh rebuilt every `Tick` | `Persistent` — per E0's chosen option (c, §5): a persistent per-slot MeshRenderer swaps its mesh each `Tick`, so the backend redraws it with no orchestrator (this flips the pre-E0 `Immediate` cell above; §5 superseded it) | global index **per symbol layer** | **✅ E2 shipped:** `SymbolRenderLayer` owns its material (D11) + a persistent `LabelSlotPresenter`; `renderQueue = TransparentQueue + DrawIndex` like any other layer; `Graphics.RenderMesh`/Overlay-4000 pin retired (demo/no-style fallback only) |
| **Background** | ~~`ViewGeometry`~~ — **SUPERSEDED by Epic A / A2 (2026-07-13):** `TileMesh`, per-covered-tile, via `TileBackgroundLayerProcessor` (source-less) | `Persistent` — E3's single world-cap `MeshRenderer` is retired; the backend now redraws one quad per covered tile, same as fill/line | global index | **A2 shipped:** `BackgroundRenderLayer` owns only the material; per-tile geometry is produced by the source-less processor and registered with the active `ITileRenderBackend`; the Mercator-only gate is DELETED (globe renders a correctly curved background across the covered tile pyramid, §7.6 polar-cap gap still open) — see the E3 row directly below (§3.6) for the preserved historical record of what E3 originally shipped |
| **Raster** *(future)* | `TileMesh` — per-tile textured quad | `Persistent` | global index | nothing |

The build kinds genuinely differ and the model **names** the difference instead of hiding it: `TileMesh`
layers participate in the tile produce/consume loop and the `ITileRenderBackend`; `FramePlaced` layers
participate in the per-frame placement loop. (The former `ViewGeometry` kind — a self-built mesh refreshed
on view/style change, background's only user — is REMOVED as of Epic A / A2: background is `TileMesh` now,
so the axis collapses to `{ TileMesh, FramePlaced }`.) What is UNIFORM across all of them is registration
(one factory), draw order (one index), presence (one enum + one orchestrator), material ownership, and
`ApplyZoom`.

### 3.2 Interface shape

`IRenderLayer` splits into a base (the uniform axes) plus per-build-kind capability interfaces. The hoist of
`WriteInto` out of the base is mechanical — Fill/Line implementations and their callers keep their shipped
bodies verbatim:

```csharp
// Historical (E1–E3) snippet, preserved as designed. Epic A / A2 (2026-07-13) REMOVED ViewGeometry —
// RenderLayerBuild is now { TileMesh, FramePlaced } (background collapsed into TileMesh; see §3.6).
internal enum RenderLayerBuild { TileMesh, FramePlaced, ViewGeometry }
internal enum DrawPersistence  { Persistent, Immediate }

internal interface IRenderLayer : IDisposable
{
    StyleLayer       StyleLayer  { get; }
    RenderLayerBuild Build       { get; }   // lifetime class — which loop feeds it
    DrawPersistence  Persistence { get; }   // who re-draws it each render
    int              DrawIndex   { get; }   // set once by RenderLayerSet.Build; renderQueue = TransparentQueue + DrawIndex
    Material         Material    { get; }   // owned by the layer (Stage A rule), queue encodes DrawIndex
    void ApplyZoom(double zoom);
}

// TileMesh capability — today's WriteInto, hoisted verbatim (fill, line; later fill-extrusion, raster).
internal interface ITileMeshRenderLayer : IRenderLayer
{
    void WriteInto(Mesh.MeshData md, IReadOnlyList<MvtFeature> features, double zoom, double extent,
        TileId id, double3 tileOriginRender, IProjection projection, out int vertexCount, out Bounds bounds);
}

// Immediate capability — called by the orchestrator inside beginCameraRendering (map camera only).
internal interface IImmediateRenderLayer : IRenderLayer
{
    void SubmitDraw(Camera camera);   // exact signature depends on the §5 mechanism decision
}
```

Concrete: `FillRenderLayer`, `LineRenderLayer` (`TileMesh`+`Persistent`, unchanged mechanics);
`SymbolRenderLayer` (`FramePlaced`+`Immediate`, §3.5); `BackgroundRenderLayer` (`ViewGeometry`+`Persistent`,
§3.6); later `RasterRenderLayer`, `FillExtrusionRenderLayer` (`TileMesh`+`Persistent`).
**Adding a kind = one class + one `RenderLayerFactory` arm** — no edits to the layer set, the backends, the
tile consume loop, or the orchestrator (they all operate on the axes, not the concrete types).

### 3.3 One global draw order (the numbering change, precisely)

Today (`RenderLayerSet.Build`): only fill/line increment `drawIndex`; everything else `continue`s. Example,
a liberty-like interleave — declared `background, fill(land), line(road), symbol(road-label),
fill(building), symbol(place-label)`:

```
today:   land=3000  road=3001  building=3002        road-label=4000  place-label=4000  background=camera hack
                                                     └── both Overlay-pinned above everything, mutual order undefined
target:  background=3000  land=3001  road=3002  road-label=3003  building=3004  place-label=3005
                                                     └── building fill occludes road labels; place labels on top — as declared
```

Mechanics:
- `RenderLayerSet.Build` numbers **every** layer the factory returns (D7); the tile-mesh layers' queues
  shift up by the count of preceding non-tile layers. `LayerDrawOrder`'s monotonic-queue + 5000-ceiling
  contract is unchanged (a >2000-painted-layer style still throws rather than saturating).
- `index == draw order == material index` **still holds** — the list simply contains all painted layers
  now. `TileManager`'s produce loop filters `layer is ITileMeshRenderLayer` when snapshotting layers for a
  kick; Stage C's `payload.MaterialIndex` (the payload carries its own global index) already makes consume
  indifferent to gaps, and the existing `(uint)materialIndex >= _layers.Count` restyle-shrink guard stays.
- **Backends get the full-width material list aligned to the global index, with null at non-tile slots**
  (those slots never receive `AddTileLayer` — the tile produce path never emits payloads for them). Two
  concrete backend touches this forces (verified against source):
  - `Backend.BRG.TileRenderer`'s ctor calls `_brg.RegisterMaterial(mat)` for every list entry — it must
    skip nulls (register lazily or keep an invalid id at null slots; `AddTileLayer` already range-checks).
  - `Backend.Entities.TileRenderer.BuildLayerPrototype` seeds its prototype's `RenderMeshArray` with
    `_layerMaterials[0]` — it must seed with the **first non-null** entry (slot 0 may be a background layer).
- The stale "fills then lines" doc comments (§1.8) die in the same commit that changes the meaning of
  `materialIndex` — `AddTileLayer(mesh, origin, layerIndex, tileId)`'s `layerIndex` is documented as **the
  global draw slot**.

### 3.4 Presence: `DrawPersistence` + the single orchestrator

- **Persistent** — the backend re-draws every render with no per-render help: BRG's `OnPerformCulling`
  emits draw commands each cull; Entities Graphics renders its entities; GameObject `MeshRenderer`s render
  themselves. All three tile backends are Persistent (verified). The orchestrator ignores these layers.
- **Immediate** — someone must re-issue the draw for every camera render. Today's symbol submit
  (`Graphics.RenderMesh` once per `LateUpdate`) is an Immediate draw with nobody re-issuing it — hence the
  Editor blink (§1.3).

**`ImmediateDrawOrchestrator`** (new, owned by `MapView`; constructed with the `RenderLayerSet` + the
`MapCamera`):

- Subscribes `RenderPipelineManager.beginCameraRendering` **once** at construction; unsubscribes in
  `Dispose` (called from `MapView.Teardown`).
- Callback: `if (camera != _mapCamera.Camera) return;` then iterate the set's `Immediate` layers in
  ascending `DrawIndex` and call `SubmitDraw(camera)`. Alloc-free (a cached filtered list, rebuilt on
  `RenderLayerSet.Build`).
- **The label submit moves out of `LabelPlacementSystem`'s `LateUpdate` call stack** — specifically out of
  `BuildAndSubmit`/`SubmitDraw` — into this callback. `Tick` keeps doing everything up to and including the
  per-slot mesh write (project → collide → fade/emit → `SymbolBillboardJob` → mesh upload); the built slot
  meshes are then submitted per render, correct in Editor AND build. One build, N submits.
- This fixes presence for **every** Immediate layer uniformly — symbols and background ride the same hook.

### 3.5 Symbols: global collision, per-layer draw (D8)

- **Collision is untouched.** One `SymbolLabelBatch` per frame, one `LabelStageJob`, one global
  `LabelCollisionJob` across every symbol layer's candidates — a road name and a city name keep competing
  in one greedy pass. The survivor set must be bit-identical before/after (tooth §6.3).
- **Draw slots already exist.** `LabelPlacementSystem` partitions survivor quads by
  `LabelInstance.MaterialIndex` into per-slot meshes (`_slotMeshes[g]`) drawn with per-layer materials —
  slot `g` IS the g-th declared symbol layer (both `SymbolLabelSubsystem.SetStyle` and `RenderLayerSet.Build`
  walk `style.Layers` in declared order, so the symbol-local ordinal maps 1:1 to a `SymbolRenderLayer`).
  `SymbolRenderLayer` g exposes slot g's mesh handoff as its `Present(mesh, visible)` (E2 — a persistent
  `LabelSlotPresenter`, not a per-frame submit call).
- **Per-layer queue replaces the Overlay pin.** Each `SymbolRenderLayer`'s material gets
  `renderQueue = TransparentQueue + DrawIndex` like every other layer; the shader's `"Queue" = "Overlay"`
  default remains only as the demo/no-style fallback. Two symbol layers on one feature (icon-under-text
  later, halo-variant today) now draw in style order because their queues differ.
- **✅ Ownership migration (D11) — shipped E2.** `SymbolRenderLayer.Create` owns the material clone
  (`RenderLayerFactory`'s Symbol arm calls it); `BindHalo` moved from `SymbolLabelSubsystem.SetStyle` into
  it verbatim; `ApplyZoom` is the natural future home for zoom-expression halo, today still a no-op.
  `SymbolLabelSubsystem` dropped `_layerMaterials`/`LayerMaterials` entirely — it needs only the layer
  COUNT (`CurrentBatch`'s slot count) and each layer's `Source`/id for build routing, not the materials
  themselves; `MapView` derives the `SymbolRenderLayer` list from the built `RenderLayerSet` (the same D10
  walk that derives the subsystem's `SymbolStyle.StyleLayer` list) and hands it to `Labels.Tick` directly.
  `LabelPlacementSystem`'s default `_material` stays for the demo/no-style seam (§4 non-goal). Restyle
  order: `MapView.SetStyle` calls `Layers.Build(...)` (disposing the OLD symbol layers' presenters +
  materials) before `_symbols.SetStyle(...)` and refilling `_symbolRenderLayers` — all synchronous, before
  any await, so the next `Labels.Tick` only ever sees live layers (no use-after-destroy).
- **Not changing:** the per-frame placement math, fades, hysteresis, culls, batch SoA, the screen-space
  vertex format, `ZTest Always` (labels are unoccluded by ground geometry per MapLibre point-label
  semantics; with all flat layers ZWrite-off, painter order alone decides occlusion — see §7 for the
  fill-extrusion caveat).

### 3.6 Background: a real layer, not a camera hack — ✅ SHIPPED (E3); geometry model SUPERSEDED (Epic A / A2)

**Epic A / A2 (2026-07-13) update — read this before the E3 record below.** The world-cap-quad geometry
model this section describes is RETIRED. Background is now a source-less **per-covered-tile** `TileMesh`
layer: `TileBackgroundLayerProcessor` synthesizes one full-tile-extent quad per tile in the camera cover
(reusing `StyledFillTileBuilder.WriteMeshData` — earcut-correct winding + globe subdivision for free) and
registers each with the active `ITileRenderBackend`, exactly like fill/line. `BackgroundRenderLayer` now
owns ONLY the material (no `Mesh`/`GameObject`); the Mercator-only `SetVisible` gate described below is
DELETED — the globe now renders a correctly curved background across the covered (Mercator-pyramid) tile
band (the ±85.05°–90° polar caps still have no surface — a pre-existing globe/tile-cover limitation shared
by fill/line, deferred to the globe track, §7.6). See `docs/per-layer-tile-processing-a2-plan.md` and
`docs/per-layer-tile-processing-design.md`'s "A2 landed" note for the full account. The rest of this
section is the **preserved E3 historical record** — what shipped in E3, before A2 replaced the geometry
mechanism (the material-ownership / draw-slot / colour-binding model below is UNCHANGED by A2).

`BackgroundRenderLayer` — `ViewGeometry` + `Persistent` *(E3, as originally shipped — see the A2 update above)*:

- Parses `background-color` / `background-opacity` into a typed `Background.PaintProperties` in Core (the
  Fill pattern); owns a material — a clone of the FILL base (`MaterialFactory.CreateBackgroundMaterial`;
  reusing the fill shader's flat lit path keeps the ground look consistent with fills by construction, and
  avoids a new `MapMaterialSet` asset field that would default null in the committed asset) — at its global
  queue. Colour binds as a uniform (`_BaseColor`/`_Opacity`) over white vertex colours — the LINE-colour
  pattern, not the fill per-vertex bake (background has no features to bake per-vertex from).
- Geometry: ONE static world-cap ground quad (half-extent `2 · WebMercator.WorldExtent`, coplanar y=0 with
  fills/lines — ZWrite-off + `renderQueue` alone composites, the proven mechanism; do NOT epsilon-lift it),
  built ONCE and never rebuilt — the camera-relative render origin already tracks the look-at every frame,
  so a quad centred on the identity transform is always centred under the camera for free. Drawn by a single
  persistent `MeshRenderer` that Unity redraws every camera render with **no orchestrator** (D9 stayed
  collapsed — the pre-E0 `Immediate` cell in §3.1 is superseded; §5's `IImmediateRenderLayer` has no
  implementor yet, kept as the model's vocabulary for a future `CommandBuffer` fallback).
- The `Bootstrapper` hardcoded sky stays only as the above-horizon clear / no-style default; a style WITH a
  background layer wins at the ground (tooth §6.5, `BackgroundSnapshotTests`). Optional later fast-path:
  bottom-slot opaque background → camera clear color (an optimization, not the model — not built).
- Globe: a flat quad is wrong on the sphere (it would slice through the globe) — `MapView.SetStyle` gates
  `BackgroundRenderLayer.SetVisible` on `Camera.Projection.TryGetHorizonOccluder`, hiding the quad entirely
  under a curved (globe) projection. The real projection-aware globe background is still out of scope for
  E3 (Mercator only); noted in §7.6.

### 3.7 Raster + fill-extrusion: reserved seats (unscheduled)

- **Raster** = `TileMesh` + `Persistent`: a quad per tile, textured from a raster source, registered via
  `AddTileLayer` at its global slot. The model change is zero — the OPEN problem is per-tile texture binding
  under the per-layer-material invariant (per-layer materials are shared across tiles; a raster tile needs
  its own texture → texture-array / BRG per-instance texture id / per-tile material — decide in the raster
  stage, §7). Raster source fetch reuses the tile pipeline with a non-MVT decode arm.
- **Fill-extrusion** = `TileMesh` + `Persistent` + ZWrite on. Enters as one `ITileMeshRenderLayer` class +
  one factory arm; the depth-vs-transparent-band interaction (ARCHITECTURE §"3D layers": depth
  range/slice) is its stage's design problem, not this model's.

## 4. What does NOT change (explicit non-goals)

- The shipped fill/line build pipeline (§0) — kick/produce/consume, S87 budget, S48 pen, S84 cancellation,
  `.Run()`-on-worker Burst kernels, `MeshDataPayload`. Untouched.
- Symbol BUILD stays per-frame placement — no folding into the Burst tile-mesh pipeline, ever (D6).
- The three tile backends stay Persistent tile-mesh engines; they learn nothing about symbols or
  backgrounds (null slots aside, §3.3).
- The demo seams (`MapView.LabelInstances`/`LabelAtlas`, `LabelPlacementSystem`'s default material).

## 5. The gate: Immediate-draw mechanism + ordering determinism (E0 — RESOLVED 2026-07-12)

Two coupled unknowns gated E2+. **E0 ran and the ordering half is now settled empirically; the mechanism
decision follows from it: prefer option (c).**

1. **Is cross-layer order deterministic via `renderQueue` alone?** — **RESOLVED: YES on the culled/BRG path.**
   `RenderQueueVsDistanceSnapshotTests` (run from a working tree at the time, NOT committed — see risk 7;
   E2's `SymbolLayerOrderSnapshotTests` tooth 1/2 now re-prove this through the real symbol path and ARE the
   committed permanent regression net) is an ADVERSARIAL probe: two overlapping wide line
   ribbons at different camera depths with `renderQueue` and camera-distance order made to DISAGREE.
   Result (headless EditMode, PASSED): the higher-`renderQueue` ribbon composites on top in BOTH configs —
   Config A `(0.220,1.000,0.220)` = GREEN, the *farther* ribbon winning (impossible under a distance sort);
   Config B (queues swapped) `(1.000,0.220,0.220)` = RED, the winner following the queue. So URP's
   `CommonTransparent = SortingLayer | RenderQueue | BackToFront | …` ranks `renderQueue` ABOVE the distance
   tiebreak, as documented — MeshRenderers (and BRG draw commands, same sorted `DrawRenderers` pass) get
   strict per-layer order from distinct queues. D7's per-layer `renderQueue` is therefore sound.
   *(Scope: this validates the PERSISTENT/culled path. It does NOT test `Graphics.RenderMesh`, which does not
   enter that sorted pass and renders 0 px headless anyway — see unknown 2.)*
2. **Does `Graphics.RenderMesh` from inside `beginCameraRendering` draw for that camera?** — **UNTESTABLE
   headless, and MOOT under option (c).** `SymbolAtlasOrientationSnapshotTests`' header records RenderMesh
   from a `beginCameraRendering` callback rendering **0 px in headless EditMode** (a harness limit — a plain
   URP-Unlit quad does too), so it is a Play-mode-only question. Option (c) sidesteps it entirely by not
   using RenderMesh.

Options (now decided):
- **(c) Symbols as PERSISTENT per-slot MeshRenderers — RECOMMENDED (E0 validates it).** Each `SymbolRenderLayer`
  keeps a persistent `MeshFilter`/`MeshRenderer` GameObject; `Tick` swaps its `mesh` each frame (the built
  slot mesh). `DrawPersistence.Persistent` — Unity redraws it every camera render automatically, so:
  (i) **the Editor blink dies with NO `beginCameraRendering` orchestrator and NO RenderMesh** (D9 collapses);
  (ii) it inherits the just-proven deterministic `renderQueue` ordering against BRG tiles; (iii) it is the
  **proven-headless path**, so teeth 1/2/4 become real snapshot tests instead of manual Editor verifies.
  Cost vs today's immediate mode: a few persistent GameObjects + a per-frame `MeshFilter.mesh` assignment.
- **(a) `RenderMesh` + per-layer `renderQueue` from the orchestrator callback.** Keeps today's submission API
  but rests on unknown 2 (untestable headless, Editor-repaint-fragile) — **superseded by (c).**
- **(b) `CommandBuffer` / URP `ScriptableRenderPass`.** Exact order + presence by construction; the fallback
  if (c) ever hits a wall (e.g. a symbol needing draw state a MeshRenderer can't express). Not needed now.

**Decision:** option **(c)**. E0 proved the only load-bearing unknown (ordering) in its favour, and (c) turns
the blink fix and the order teeth into headless-testable, orchestrator-free mechanics. E2 builds on (c).
This **reshapes D9** (no single `beginCameraRendering` orchestrator; presence comes free from persistent
MeshRenderers) and simplifies `IImmediateRenderLayer` (§3.2) — the "Immediate" capability becomes a
per-frame mesh-swap on a persistent renderer, not a per-render submit callback.

## 6. Acceptance teeth (with teeth — a shallow/wrong impl cannot pass)

1. **✅ Interleaved composite — SHIPPED (E2).** A style declaring a fill ABOVE a symbol layer: snapshot shows the fill
   occluding the labels in the overlap region (mirrors the `LayerOrderSnapshotTests` /
   `BrgBackendSnapshotTests` tooth-2 net; label pixels via the now-real persistent-MeshRenderer path —
   `SymbolLayerOrderSnapshotTests.FillAboveSymbolLayer_OccludesLabels_FillBelow_LabelWins`).
   *Falsifier:* today's Overlay-4000 pin draws labels over the fill — fails.
2. **✅ Symbol-vs-symbol order — SHIPPED (E2).** Two symbol layers placing at the same anchor draw in
   declared order — sampled overlap pixels match the LATER layer, and swapping `renderQueue` flips the
   winner (`SymbolLayerOrderSnapshotTests.TwoSymbolLayers_SameAnchor_LaterDrawIndexWins_SwapFlipsWinner`).
   *Falsifier:* equal-queue undefined order.
3. **✅ Collision parity — SHIPPED (E2).** Same frame/labels: the survivor set (candidate/survivor/quad
   counts) is identical between the demo (no layers) and production (real `SymbolRenderLayer`) paths — a
   differential EditMode test (`SymbolLayerOrderSnapshotTests.CollisionCounts_AreIdentical_...`).
   *Falsifier:* any accidental per-layer collision split changes survivors.
4. **No Editor blink — headless proxy SHIPPED (E2), literal check still MANUAL.** Labels persist across
   Game-View repaints without the player loop — the MECHANISM (a persistent scene renderer redraws without
   a new Tick) is now headless-testable and tested
   (`SymbolLayerOrderSnapshotTests.Presenter_ShowsAcrossRepeatedRenders_HidesWhenTickIsEmpty`); the literal
   Editor-repaint eyeball remains a **manual Editor verify** (Editor-only artifact — see §5 evidence; the
   build never blinked).
5. **✅ Background is the style's — SHIPPED (E3).** A style with `background-color: X` renders X (snapshot
   corner sample), not the Bootstrapper sky; a mid-stack background occludes layers below it and not above
   (snapshot) — `Visual/BackgroundSnapshotTests.BackgroundColor_StyleHonoured_NotCameraClear` +
   `.Background_MidStack_OccludesBelow_IsOccludedByAbove`.
   *Falsifier:* the camera-clear hack ignores X and cannot be mid-stack — fails against pre-E3 code by
   construction.
6. **Global numbering.** `RenderLayerSetTests` extended: symbol/background layers take slots; queues are
   monotonic over ALL painted layers; tile-mesh layers' queues shift by the count of preceding non-tile
   layers (assert exact values for a mixed fixture style).
7. **One registry.** Structural test: no `is Fill.StyleLayer`-style type-switches outside
   `RenderLayerFactory` (grep-tooth over `MapView`/`SymbolLabelSubsystem`, same pattern as
   `LabelPlacementStructureTests`' AddTileLayer grep). *Falsifier:* §1.6's three scattered switches.
8. **✅ Extensibility — SHIPPED (E3).** Adding a NEW layer kind = one class + one factory arm; E3 landed the
   background layer with no edits to `RenderLayerSet` (code), any backend, `TileManager`'s consume loop, or
   an orchestrator (there is none — D9 stayed collapsed); the allowed footprint outside the new Core types +
   the layer class itself was one `RenderLayerFactory` arm, one `StyleParser` case, one `MaterialFactory`
   pair, and one contained `MapView` projection gate (review-checked on the E3 diff).
9. **E1 is behaviour-preserving.** All existing `Visual/` snapshots byte-identical after E1 (symbols still
   draw via the legacy path until E2; only the numbering + interfaces move).

## 7. Risks / open questions

1. **The §5 mechanism gate** — biggest unknown; that is why it is E0 and why (b) is fully sketched.
2. **Immediate draws are headless-invisible** (harness limitation, evidence in §5): every Immediate-draw
   tooth must assert through the MeshRenderer-attach pattern or live Editor verification. Budget for this in
   E2/E3 test design; do not burn time "fixing" 0-pixel headless RenderMesh.
3. **Null slots in backends** (§3.3): BRG ctor `RegisterMaterial` and the Entities prototype's
   `_layerMaterials[0]` are the two verified touch points; audit the GameObject backend's ctor loop too.
   A missed one is a hard NRE on the first background-bearing style — covered by tooth 6's fixture style
   running through all three backends (the existing backend snapshot nets).
4. **Symbol material ownership migration** (D11): the subsystem/layer-set restyle ordering and disposal
   (who destroys the clones when) must keep the existing restyle tests green; watch for a
   use-after-destroy on the frame a restyle lands while labels are mid-fade.
5. **`ZTest Always` on symbol text vs future depth-writing layers.** Fine today (all flat layers ZWrite
   off — painter order alone composites). When fill-extrusion lands (ZWrite on), "labels unoccluded by 3D"
   is MapLibre's point-label default, so `ZTest Always` likely SURVIVES — but line-following labels behind
   buildings deserve a look in that stage.
6. **Background geometry**: far-plane-cap quad precision under tilt at the horizon; globe projection needs
   its own geometry (out of E3 scope — Mercator only; the globe background is a follow-up).
7. **Raster per-tile texture vs per-layer material** (§3.7) — unresolved by design; decide in the raster
   stage (texture array vs per-instance id vs per-tile material clone). The model reserves the seat either
   way.
8. **Queue ceiling**: `LayerDrawOrder.ComputeQueues` throws past 5000; adding symbol/background slots grows
   N but realistic styles (liberty ~120 painted layers) stay far below 2000 — no change needed, noted for
   completeness.
9. **`SymbolRenderLayer.ApplyZoom`**: moving halo binding into the layer makes zoom-expression halos
   (documented first-cut limit in `BindHalo`) cheap to fix — do NOT fix them in E2 (scope creep); leave the
   try/catch behaviour identical and note the follow-up.
10. **KNOWN FOLLOW-UP (post-E2, filed at commit — Opus review of E2):** one `LabelPlacementSystem` is shared
    across the demo↔production tick paths (`MapView.LateUpdate` `:349`/`:354`), and flipping between them
    leaks per-path state. Two symptoms, one root cause. **(1a, pre-existing since E1)** `RefreshBatchMirror`
    keys its stage-mirror skip on `batch.BuildId` alone with no batch-identity guard, so a demo→symbol flip
    reads one frame of stale placement (self-heals next frame). **(1b, introduced by E2)** production
    `PresentSlot` never hides the *fallback* presenter, so a demo label shown before the flip stays enabled
    and double-blends with the layer presenter. **Reachability:** narrow — needs the demo
    `LabelInstances`/`SyntheticLabelSource` seam active with real labels *then* a symbol style load; NOT the
    shipped OpenFreeMap/liberty path (symbols from frame 0, demo `else` branch never runs with real labels).
    **✅ FIXED (2026-07-12, Sonnet dev + Opus review, this branch).** 1a: `RefreshBatchMirror` now guards on
    `ReferenceEquals(batch, _lastBatch) && batch.BuildId == _mirrorBuildId` (new `_lastBatch` field) — a
    different batch instance forces a refresh regardless of BuildId. 1b: `PresentSlot` hides the inactive
    path's presenter every Tick, both directions (production also hides `_fallbackPresenters[slot]`; demo also
    hides the previous Tick's layer presenters via `_lastSymbolLayers`). Regression tests
    `LabelPlacementDemoProductionFlipTests` (RED-verified against the reverted logic, then green).
    **Two review notes on the fix (not defects — for the merge step):**
    - The *demo-branch* half of the 1b hide is defensive dead code in the real `MapView` flow: a restyle's
      `RenderLayerSet.Build` → `ClearLayers()` disposes the old `SymbolRenderLayer` presenters (they self-hide)
      and empties the reused `_symbolRenderLayers` List that `_lastSymbolLayers` aliases, all synchronously
      before any demo Tick — so `_lastSymbolLayers.Count == 0` by then. Harmless, guarded, zero-alloc; kept
      because the asymmetry is correct (the *production*-branch hide of the persistent `_fallbackPresenters`
      IS load-bearing — those survive restyle; layer presenters don't). Consequence:
      `ProductionThenDemo_Flip_HidesLayerPresenter` pins a valid unit invariant but is NOT a live-production
      regression guard (MapView cannot produce a direct flip without a `ClearLayers` between).
    - The fix's correctness leans on the `SetStyle` teardown order (`ClearLayers`-before-flip; same ordering
      the E2 commit flags as "Layers before Labels, load-bearing").
11. **FOLLOW-UP (pre-existing, surfaced by E3's Opus review — not an E3 blocker):** `LayerOrderSnapshotTests`
    builds its hand-made fill quads with the front-facing winding `{0,2,1,0,3,2}`, which under `MapFill.mat`'s
    `_Cull: 1` (Cull Front) is the **invisible-from-above** winding (E3 empirically proved the reverse
    `{0,1,2,0,2,3}` is the visible one). So that test's two fill quads render nothing; only its
    `SyntheticLineMesh` ribbon draws, and its low-variance/clean-composite assertion passes **vacuously** — the
    "fill-on-top-of-line" keystone case it claims to exercise is never actually exercised.
    **✅ FIXED (2026-07-12):** flipped the quad winding to `{0,1,2,0,2,3}` so the fills render, and added a
    **top-layer-dominance** assertion (the composite must be red — the top fill — dominant) so the tooth can
    no longer pass vacuously: a blue/green-dominant region now fails, catching both the winding bug and any
    painter-order regression. The variance/no-z-fight assertion stays, now over three genuinely-visible layers.

## 8. Sequencing (round-2 stages of the one epic)

Each stage independently green: full EditMode suite + `Visual/` snapshot parity (identical where
behaviour-preserving, new snapshots where behaviour intentionally changes). Lowest-risk first; the
mechanism prototype gates all Immediate wiring (D12).

| Stage | Scope | Kills | Gate/teeth | Risk |
|---|---|---|---|---|
| **E0. Mechanism probe — ✅ DONE (2026-07-12)** | `RenderQueueVsDistanceSnapshotTests` (adversarial renderQueue-vs-distance; run but NOT committed, see §7 risk 7 — E2's `SymbolLayerOrderSnapshotTests` is the committed regression net instead). **Result: renderQueue dominates distance on the culled/BRG path → option (c) chosen** (§5) | the (a)/(b)/(c) uncertainty | decision recorded + a permanent regression test; no prod code | Low (done) |
| **E1. Model axes + global numbering — ✅ DONE (2026-07-12)** | `Build`/`Persistence`/`DrawIndex` on `IRenderLayer`; hoist `WriteInto` → `ITileMeshRenderLayer`; factory arms for Symbol/Background returning axis-bearing layers; `RenderLayerSet` numbers ALL painted layers; backends tolerate null slots; TileManager filters `ITileMeshRenderLayer`; D10 single-registry rewire of `BuildSourceSpecs`/subsystem; fix §1.8 stale comments | scattered type-switches (§1.6), stale flatten comments (§1.8) | teeth 6, 7, 9 (byte-identical snapshots — symbols still draw legacy) | Med (done) |
| **E2. Symbols draw at their slot (option (c)) — ✅ DONE (2026-07-12)** | `SymbolRenderLayer` owns material+queue (D11) + a PERSISTENT per-slot `MeshFilter`/`MeshRenderer` (`LabelSlotPresenter`); `Tick` rewrites its mesh in place each frame and hides it when nothing was built; retired `LabelPlacementSystem.BuildAndSubmit`/`SubmitDraw`'s `Graphics.RenderMesh` and the Overlay pin (shader tag stays as the demo/no-style fallback default only). No orchestrator (D9 collapsed) | Overlay z-group (§1.1–1.2), the blink (§1.3), dual material owners (§1.7), the `RenderMesh` submit | teeth 1, 2, 3, 4 — real headless snapshot tests (`SymbolLayerOrderSnapshotTests`) via the persistent renderer | Med (done) |
| **E3. Background layer — ✅ DONE (2026-07-12)** | `Background.PaintProperties` parse (Core, the Fill pattern); `BackgroundRenderLayer` (`ViewGeometry`+`Persistent`, flipped from the pre-E0 `Immediate` cell) owns a fill-base material clone + a static world-cap quad on a persistent `MeshRenderer`; Mercator-only gate (`MapView.SetStyle`); Bootstrapper sky demoted (comments only) to the above-horizon clear / no-style default | the camera hack (§1.4) | teeth 5, 8 — `BackgroundSnapshotTests` (2 tests), `BackgroundPaintTests`, `RenderLayerSetTests` flips | Low-Med (done) |
| **F. Raster** *(OUT OF SCOPE — Epic A payload)* | a raster-source processor; resolve risk 7 | §1.5 | its own stage | — |
| **G. Fill-extrusion** *(OUT OF SCOPE — Epic A payload)* | a `TileMesh` processor + ZWrite | — | its own stage | — |

E1 is deliberately behaviour-preserving and mostly mechanical — it banks the model with zero visual risk.
E2 changes the live label draw path but is de-risked by E0's chosen option (c): a persistent per-slot
MeshRenderer is the proven-headless path, so its teeth are real snapshot tests and its presence/ordering are
mechanical rather than an orchestrator experiment (E2 downgraded High→Med). E3 proves the extensibility
claim on a genuinely new kind.

**Branch scope (decided 2026-07-12).** This branch (`feat/render-layer-unification-r2`) ships **E1–E3**: the
shipped vector style's painted kinds — fill, line (round 1) + symbol (E2) + background (E3) — are now all
first-class in the one ordered model. **F (raster) and G (fill-extrusion) are deferred out of this branch** —
neither is used by the current style, and — decided 2026-07-12 — they are **not standalone epics but payload
of "Epic A" (the tile-pipeline unification)**: once per-layer tile processing + a generic data source land,
raster is a raster-source processor and fill-extrusion is a `TileMesh` processor + ZWrite (raster's
per-tile-texture fork, risk 7, is resolved there). E3's background world-quad is likewise **interim** —
Epic A replaces it with a source-less per-tile processor (projection-correct on the globe). See
`docs/per-layer-tile-processing-design.md` (Epic A) and `docs/projection-globe-track-design.md` (Track B, the
symbol far-side-occlusion + `GroundResolution` globe gaps that do NOT fall out of Epic A).
