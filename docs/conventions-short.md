# Coding conventions — short index

The **short, summary** form of this codebase's coding conventions — one entry per rule, enough to follow it
without opening anything else. For the *why* — rationale, tables, examples, gotchas — see the matching
section (same title) in **[`conventions.md`](conventions.md)**, the canonical human-facing reference.

This file is the summary `AGENTS.md` imports, so agents always carry the rules without carrying the whole
essay. It is grouped into topic chapters; each rule is a bolded lead line with its details as sub-entries.
Keep the two files in sync: when a rule changes, edit `conventions.md` and update the matching entry here.

---

## Math & numeric types

- **`Unity.Mathematics` for all math (types *and* functions); `System.Math` and `UnityEngine.Mathf` are
  prohibited.**
  - *Types:* `float2/3/4`, `double2/3`, `int2/3`, `quaternion`, `float4x4` — **not** `UnityEngine.Vector2/3/4`
    / `Quaternion` / `Matrix4x4` for our own math or storage. `Core` is engine-free, so `UnityEngine.Vector*`
    is forbidden there outright.
  - *Sole type exception:* a Unity boundary API that *demands* a `VectorN` (mesh/material/transform) — convert
    at that call site, never upstream.
  - *Functions:* the `math.*` free functions (`math.sin`, `math.sqrt`, `math.abs`, `math.pow`, `math.min/max`,
    …) — **not** `System.Math.*` (banned in production). `UnityEngine.Mathf.*` is banned on the same terms:
    `Mathf.Max` → `math.max`, `Mathf.Clamp01` → `math.saturate`, `Mathf.Deg2Rad/Rad2Deg` → the `Angle` type.
  - *Precision traps:* for double-precision π/e use **`math.PI_DBL` / `math.E_DBL`**, not `math.PI` (a
    single-precision float that injects ~1e-7 error and breaks `const double` initializers). `Mathf` is
    float-only, so a `Mathf` call inside a `double` expression has already narrowed — check precision when
    migrating, don't just swap the token.
  - *Out of scope:* vendored `ThirdParty/` code.

- **Angles are an `Angle` value type, not a bare `double`.**
  - The `* math.PI_DBL / 180.0` conversion lives **once**, inside `Angle.cs`; every trig site reads
    `.Sin`/`.Cos`/`.Radians` off the struct.
  - Construction is explicit — `Angle.FromDegrees`/`FromRadians`, no implicit `double` conversion.
  - Camera orientation params (`Heading`, `Tilt`) are `ConstrainedAngle` (an `Angle` + `[lo, hi]` +
    `AngleConstraint{Clamp,Wrap}`) that enforce their range at construction. *(S68)*

## Types & data modeling

- **Pass large read-only structs by `in`.**
  - A method that only *reads* a struct param bigger than ~16 bytes (camera state, eval contexts, descriptors)
    takes it `in` — a read-only reference, no per-call copy, intent explicit. Prior art: `in EvaluationContext`.
  - **Gate: `in` ⟺ `readonly struct`** — on a non-readonly struct, member access through `in` forces a
    *defensive copy* per read (worse than by-value).
  - Small structs (`float3`, `double2`, `TileId`) stay by value.

- **Data carriers: object-initializer construction; geo coords are `(Latitude, Longitude)`.**
  - Plain data carriers expose `init`-only auto-properties and are built with named members
    (`new GeoCoordinate { Latitude = …, Longitude = … }`), not positional ctors.
  - Spell names out (`Latitude`, not `Lat`).
  - Geodetic types are **latitude-first** `(Latitude, Longitude[, Altitude])`; the lon-first swap happens only
    at the projection boundary.
  - `init` needs the one-line `IsExternalInit` polyfill per assembly under Unity.

## Geometry & meshing

- **Type-explicit builder naming.**
  - A type that builds/owns a single geometry kind names it explicitly (`StyledFillTileBuilder`,
    `StyledLineTileBuilder`).
  - Generic names (`MeshBuilder`, `TileMeshFactory`) are reserved for genuinely type-agnostic dispatchers.

- **Geometry producers declare their output winding; boundaries convert.**
  - A triangle-producing type (`Earcut`, `LineTessellator`, `LineRibbonJob`, `GlobeFillSubdivideJob`) states
    its output winding + coordinate space in its XML summary.
  - There is **one canonical winding** (CCW in tile space); the producer never bakes the render convention.
  - The Unity-front reversal for stock Cull Back happens at **one** boundary per mesh kind
    (`StyledFill`/`StyledLineTileBuilder`) — same "convert at the Unity boundary, never upstream" rule as
    `double3`→`Vector3`.
  - Keeps `Core` engine-free and the parity oracles hashing canonical winding. Cause + full contract in
    `docs/coordinates-and-projections.md` §7.1; pinned by `GlobeFill`/`GlobeLineWindingTests`.

## Memory, performance & lifetime

- **New data-plane code is born native — nativizing later is not the plan.** Write a new feature's data plane
  over blittable structs + `NativeArray`/`NativeList`/`NativeHashMap` from the first commit. Symbols and MVT
  decode both went managed-first and had to be rewritten; the representation answers (tagged unions, flat
  string pool, native per-feature columns) are solved and reusable.
  - *Discriminator (structural, not a perf guess):* **data plane** = recurs per tile/feature/vertex/glyph/frame
    or is read inside a job → **native**. **Control plane** = once per style load / user action / lifecycle
    transition, holds refs or `UnityEngine.Object` → **managed is correct** (native there costs legibility and
    disposal safety, buys nothing).
  - *The test:* "will this be read inside a job, or loop over scene-sized data?"
  - A managed capture on the data plane is **disqualifying**, not just slow — a body closing over a
    `Dictionary`/class ref cannot become an `IJob` until rewritten.
  - Sits **above** the ladder below and does not replace its top rung: best is still to allocate *nothing*.

- **Column layout is an access-pattern call, not an AoS/SoA preference.** Fields read together at the same
  index → one struct/column; a field streamed alone over a range → its own column. *The test:* "what does
  one iteration touch?" — walk the loop bodies, not the field list.
  - `EarcutJob`'s `Vx`/`Vy` (two `NativeArray<double>`, always accessed at the same index, every write
    de-interleaving an already-`double2` source) merged into one `NativeArray<double2> Verts` —
    `FillTriangulationBuffers.FlatWorkVerts`. Contrast `Next` in the *same* file, which correctly stays its
    own column: `scan = Next[scan]` streams it alone on every ring walk, touching no position field at all.
  - **Co-access is necessary but not sufficient.** If a field's only same-index partner is itself streamed
    alone somewhere, merging into it taxes that stream for the newcomer's benefit. `EarcutJob`'s `IsEar` and
    `IsBridgeCopy` read at the same index as `Removed` on every touch, yet stay their own columns because
    `Removed` is walked alone by three `Next`-only ring scans (`:297`, `:629` in `CountRing`, `:391-403`).
  - **Not yet enforced across the codebase** — new/touched code only; no prior split-column elsewhere has
    been swept against this test.

- **Hot-path allocations: none → native → pooled (a descending ladder).** In any loop that recurs per
  feature / vertex / glyph / tile-build / frame, a managed `new` is not local: Unity's GC is
  **stop-the-world**, so one worker-thread allocation freezes every thread mid-frame. Take the highest rung:
  - **(1) allocate nothing** — reuse, hoist out of the loop, `FixedList*Bytes<T>` or `stackalloc` (unmanaged)
    for a small bounded collection.
  - **(2) unmanaged elements → native containers** (`NativeArray`/`NativeList`, off the GC heap), `Allocator`
    by lifetime+thread — **off-main scratch = `Persistent`** (TempJob's 4-frame cap counts *main-thread*
    frames, which an off-main build overruns), main-thread scratch = `Temp`/`TempJob`.
  - **(3) managed elements that can't be native** (hold refs, e.g. `Value`) **→ pool, never `new` per call** —
    `ArrayPool<T>.Shared` for simple array temporaries (returns **oversized** arrays, so the consumer takes a
    length/`Span` and never trusts `.Length`); `UnityEngine.Pool` (`ListPool`/`ObjectPool`) for **main-thread**
    managed collections/objects (**not thread-safe** — off-main paths need a per-thread pool); a
    **`[ThreadStatic]` free-list of whole arrays** for reentrant/cross-thread hot paths (prior art
    `EvalArgBuffers`, `TileBuildScratch`). Every pool pairs `Rent`/`Return` in a `finally` so a throw can't
    drain it, and pools only **scoped scratch, never a borrowed container** (releasing a `List` a consumer
    still holds clears it under them). `UnityEngine.Pool` is engine-only, but that never keeps code in Core —
    placement follows architecture, not the fast-test loop (ARCHITECTURE.md §2).
  - **Prove it:** a GC-elimination change ships a **zero-alloc tooth, RED-verified**; the EditMode meter is
    `Is.Not.AllocatingGCMemory()` (`GetAllocatedBytesForCurrentThread()` is dead there — vacuous), the
    thread-local byte delta works only in `Tools/core-tests`.
  - **Out of scope:** cold paths (parse, static init, throw-path `$"…"`).

- **`if (x.IsCreated) x.Dispose();` — redundant almost everywhere, LOAD-BEARING where the same
  already-disposed instance can be disposed again. The discriminator is "can this release site run twice
  on the SAME, already-disposed instance?" — not "might the value be absent?".**
  - *A never-allocated `default(NativeArray<T>)` disposes cleanly.* Absence is not the hazard, so a guard
    that only protects against "might not exist" is noise. Likewise redundant in front of `NativeList<T>`
    (its `Dispose()` early-returns on `!IsCreated`, `Unity.Collections/NativeList.cs:620-623`) and in front
    of a type with its own struct-level `if (!IsCreated) return;` (`TileGeometryBuffers`, `FillGraphOutput`,
    `TileBuildGraph`'s `_disposed`).
  - *Load-bearing (KEEP):* a raw `NativeArray<T>` **that was allocated, disposed once, and may be disposed
    again** — the copy-mutate-writeback idempotency idiom
    `var x = Field; if (x.IsCreated) x.Dispose(); Field = x;`. A second `Dispose()` on an already-disposed
    array throws; `NativeArray<T>` has no internal early-return, so the guard **is** the idempotency
    mechanism and the writeback is what lets it see the disposed state. Prior art:
    `MvtLayer.Dispose` (`Mvt/MvtModels.cs:196-222`).
  - *Measured, the expensive way:* sweeping all 57 on the assumption they were redundant **reddened 106
    tests**. Exactly **7** were load-bearing (`MvtModels.cs` ×4, `NativeFilterEvaluator.cs` ×2,
    `MvtValueCompactionTests.cs` ×1 — a test that disposes once in its body and again in `finally`).
    The other 50 removals were correct. Delete by the discriminator above, never in bulk.

- **`[ReadOnly]` belongs on a job field whose type contains a native container — including a generic type
  parameter, whose argument may. Elsewhere it is a no-op: harmless where it sits, not worth adding, and never
  worth removing in bulk.**
  - *Why it is a no-op on a scalar:* a job field is a by-value copy, so nothing outside the job can observe a
    write to it — "read-only" already holds unconditionally, attribute or not. Unity's schedule-time
    validation walks **native container** fields; a field with no container in it is never on that walk. Unity
    ships `[ReadOnly] public int Num;` in its own Collections test fixtures — the habit is not a smell.
  - *The trap, and the reason NOT to sweep:* on a **generic** field the declaration site cannot see whether
    the argument holds a container. `FillGatherJob.cs:54`'s `[ReadOnly] public TComparer Comparer;` is
    **load-bearing** — the comparer holds two `NativeArray` fields built from the *same allocation* the job's
    own `Vertices`/`RingOffsets` view, so without it the safety system sees one read-only and one implicitly
    writable alias and throws at schedule (*"two containers may not be the same (aliasing)"*), caught by
    `FillGraphBurstProbeTests`. A mechanical "strip it from anything that isn't a `Native*`" sweep deletes
    exactly that one.
  - *Also generic, inert only for now:* `ProjectPointsJob.cs:45` and `GlobeFillSubdivider.cs:63`
    (`[ReadOnly] public TProj Projection;`) — inert while every projection struct stays stateless,
    load-bearing the moment one holds a container.
  - *Applied:* new code follows the containers-only half. The second half only ever answers "leave it".

- **Mesh lifetime & ownership: data is a value type, the `Mesh` is a single-owner class.**
  - Blittable geometry *data* (`NativeArray`/`Mesh.MeshData`/`LayerMeshData`) are **value-type structs** the
    jobs write, disposed deterministically at the `ApplyAndDisposeWritableMeshData` boundary — never held,
    never a dispose-once guard.
  - The `Mesh` GPU *resource* is a **reference-type class** created/destroyed **main-thread only** and held by
    **exactly one owner** (`TileManager._loaded` in cover / `PreparedTileCache` out of cover — Model B); a
    transfer **nulls the source** so it's destroyed once (the double-free guard); teardown = destroy meshes →
    dispose backend.
  - Dispose-guard machinery + the `CountMeshObjects` leak baseline touch only the **class** side; structs stay
    trivial. Full contract (exit paths, cancellation, teeth) in
    **`docs/async-architecture.md` §"Disposal & cancellation contract"**.

## Naming

- **Names carry meaning; filler words do not.**
  - `Scratch`, `Data`, `Info`, `Manager`, `Helper`, `Util`, `Temp`, `Stuff` may never be the part of a name
    that carries the meaning. Name for what the thing holds or does.
  - **Never name a shared type after one of its consumers** — `EarcutScratch` was used by four jobs; naming
    it for one was wrong, not just vague. It is `FillTriangulationBuffers`.
  - Grouped buffers join the existing family: `TileGeometryBuffers`, `FillGraphOutput`, `EvalArgBuffers`.
  - Keep a qualifier that distinguishes (`FlatWorkVerts` — flattened across the layer's polygons), drop one
    that does not (`FlatScratchWorkVerts`). Test: delete the word — if the name is still unambiguous, it was
    filler.
  - **NOT yet enforced across the codebase — read this as the rule new and touched code is held to.**
    `Scratch` alone still appears ~160 times outside the fill-graph path (symbols, `TileManager`,
    placement, `TileBuildScratch*`), and `PathScratch`/`CumScratch`/`cumScratch`/`keysScratch` are live.
    The fill-graph path was swept 2026-09-03; the rest is an outstanding mechanical sweep. Stating the
    rule without this line made it read as already true — which is how `EarcutScratch` survived one
    rename (the type was fixed, the field it was held in stayed `Scratch`) and how `FlatScratchVx`
    outlived the doc that uses it as the example of what to fix.

## Documentation & tests

- **A short XML doc on EVERY member; nothing that restates the body.**
  - Floor (not optional): a one/two-line `<summary>` + a `<param>` per parameter, plus `<returns>` when the
    summary does not answer it.
  - That floor is normally also the ceiling — past it, write prose only for a **non-local invariant**
    (a protocol/lifetime/ordering fact no single body reveals), a **non-obvious why**, or a **limitation no
    tooth can observe**.
  - **`<see cref>` points OUTWARD** — counterpart, matching release site, the caller that establishes the
    precondition — **never at a callee the body already names** (use `<c>Name</c>` for an incidental mention,
    which creates no reference).
  - Design narrative, rationale and rejected alternatives live in `docs/*-design.md`, not in the file.
  - **Gate:** past summary+params, a doc longer than its member must name which of the three reasons applies,
    or be cut back to the floor.

- **Test code must not bloat the production codebase.** A member that exists solely for a test does not belong
  on the production class.
  - Allowed footprint: broaden `private` → `internal` (+ `InternalsVisibleTo`), or put computed
    accessors/adapters as extension methods in the **test** assembly.
  - Not allowed: `public` members with no production caller, `// for testing only` members, test
    helpers/factories inside production classes.
