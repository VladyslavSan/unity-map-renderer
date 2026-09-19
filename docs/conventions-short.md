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

- **A member that is just get/set is ONE auto-property, never a field plus a forwarding accessor.**
  - `internal SymbolStringTable StringTable { get; }`, not `private readonly _stringTable` plus
    `internal StringTable => _stringTable`. The compiler already gives an auto-property a backing field;
    the explicit one adds a second name for the same thing and a line of noise at every rename.
  - Defaults go in a property **initializer** (`{ get; set; } = new X();`), not a field initializer.
  - *An explicit backing field is EARNED only when the accessor does work a plain auto-property cannot:*
    laziness (`_x ??= …`), change-notification, a computed value, or validation a caller can actually
    trigger. A **defensive** guard no caller can reach is not work — drop it and use an initializer.
    Set-only is not a reason either: `{ get; set; }` with an unread getter is fine.
  - *Raising visibility for a test* (`private` → `internal` + `InternalsVisibleTo`) is the moment this
    rule is usually broken — raise the property itself rather than wrapping the field in a new accessor.

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
  - A triangle-producing type (`EarcutJob`, `LineTessellator`, `RibbonJob`, `GlobeFillSubdivideJob`) states
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
    `TriangulationBuffers.FlatWorkVerts`. Contrast `Next` in the *same* file, which correctly stays its
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

- **A `using`-declared native container rejects an index write (CS1654).** Reads, `.Add()`, `.Resize()`,
  and pass-by-value still compile; `x[i] = v` does not, because neither container is a `readonly struct`.
  Fix: take a plain, non-`using` writable view over the same allocation and write through that —
  `.GetSubArray(0, x.Length)` for a `NativeArray<T>`, `.AsArray()` for a `NativeList<T>` — re-fetching a
  `NativeList` view after every `Resize`.

- **A per-frame `ValueTuple` comparison key tops out at 7 elements.** At the 8th, the generic shape
  changes — the tail becomes a nested `ValueTuple<T8>` (`TRest`) — and constructing or comparing that
  shape allocates under Unity's Mono; comparing field-by-field instead of `==` does not fix it. Past 7
  fields, replace the tuple with a `readonly struct` implementing `IEquatable<T>`, compared via
  `key.Equals(prev)` (prior art: `MapView.SelectorInputs`).

- **`Allocator.Temp` containers cannot be scheduled with `.Run()`.** The Collections safety system
  rejects a `Temp`-allocated container passed to a `.Run()` job — `.Run()` counts as scheduling even
  though it executes inline. Use `Allocator.TempJob` for per-call scratch, or a persistent field allocated
  once and reused for once-per-tick scratch.

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

- **Every loop is bounded (`always-bound-loops`). A count derived from runtime data gets a named ceiling.**
  - Any `for`/`while` whose iteration count comes from projection, geometry, arc length, distance or
    style/user data takes a named `MaxX` constant. A real value is a handful; the cap only guards the
    pathological case, so pick it high — the requirement is *finite*, not tight.
  - **Clamp in float and `math.min(..., cap)` BEFORE any `(int)` cast** — a huge float overflows the int
    to garbage or a negative.
  - Guard the source too: reject non-physical projected coordinates and non-finite lengths. Write the
    test as `!(x < Max)` so it also catches NaN.
  - *What it prevents:* a line vertex at the camera near plane projects to a near-infinite screen
    coordinate, so `projectedLineLength / symbol-spacing` produced millions of anchors and hung the scene.
  - Prior art: `SymbolStagingMath.MaxAnchorsPerLine`, `SymbolScreenProjection.MaxProjectedPx`,
    `LayerInput.MaxOutputVertices`.

## Naming

- **Symbol / Text / Icon is the partition for the symbol subsystem. "Label" is not a name.**
  - A symbol is a placed text AND/OR icon, so `symbol` is the umbrella term. Touches only text → `Text*`.
    Only icon → `Icon*`. Touches both, either, or the placed unit as such → `Symbol*`. A total partition.
  - **"Label" is eliminated**: colloquially it means "text label", so it wrongly narrows a type that acts
    on the whole text+icon box. "marker" is a point-icon subset, never the umbrella.
  - Applied in full 2026-08-24 — the rule new code is held to, not a migration to run. Legitimate
    survivors are not violations: Unity's own `GUI.Label`, style-layer data strings, `LiteralLabel`.

- **Names carry meaning; filler words do not.**
  - `Scratch`, `Data`, `Info`, `Manager`, `Helper`, `Util`, `Temp`, `Stuff` may never be the part of a name
    that carries the meaning. Name for what the thing holds or does.
  - **Never name a shared type after one of its consumers** — `EarcutScratch` was used by four jobs; naming
    it for one was wrong, not just vague. It is `TriangulationBuffers`.
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

- **Descriptive names, not positional or abbreviated.**
  - Name a variable, parameter, or out-param for its role, not its position — `topLeft/topRight/…`, not
    `v0/v1/v2/v3` (`BillboardMath.BuildWorldQuad`). Use known-position names (corner, edge) when several
    things map to fixed positions.
  - A `using X = Namespace.Type;` alias must be spelled out too — `using SymbolStyle = …Symbol;`, never
    an abbreviation. Add an alias only to break a real collision, and keep it descriptive.

- **Test file/class/method names must not carry a stage identifier.**
  - `ThrottleTests`, never `S55ThrottleTests` — a stage id is ephemeral bookkeeping a later reader cannot
    decode, and it turns the suite into a changelog. Same principle as the commit-scope rule. Fine as
    provenance inside a doc comment; the ban is on identifiers that show up in test output.

- **A namespace segment must not equal a bare `UnityEngine` type name.**
  - Inside `namespace …Foo.Material`, bare `Material` resolves to the *namespace*, forcing
    `UnityEngine.Material` everywhere inside it (`CS0118`). Pluralize or use an `-ing` form —
    `Materials`, `Meshing`, `GameObjects` — and audit a proposed segment with
    `grep -rE 'UnityEngine\.<Name>\b'` first. Same failure on an unqualified `using` that imports a name
    (e.g. `CameraProperties`) this repo also defines (`CS0104`) — qualify the new import, don't alias the
    incumbent.

## Documentation & tests

- **A short XML doc on EVERY member; nothing that restates the body. Hard line limits below.**
  - **Write in [ASD-STE100 Simplified Technical English](https://www.asd-ste100.org/).** One idea per
    sentence, active voice, present tense, a plain approved word over a clever one. No words that perform
    rigour instead of delivering it — *deliberately, by construction, precisely, exactly as much, surfaced
    loudly, note that*. Delete any sentence a reader would lose no FACT by losing.
  - **Class/method `<summary>`: 5 lines MAX.** Exceptions allowed, but must be **highly justified** — and
    in most cases the detail belongs in `docs/*-design.md` instead, with at most a pointer here.
  - **`<param>`/`<returns>`: 2 lines MAX each.**
  - **Inline `//` comments: 2 lines MAX.** Same exception rule, same remark: over two lines is nearly always
    narrating the code below, recounting history, or arguing a decision that belongs in a design doc.
  - Every member gets a `<summary>`. Add `<param>`/`<returns>` when the name and type do not already answer
    it — not as ceremony on every signature.
  - Past the limits, prose is allowed only for a **non-local invariant** (a protocol/lifetime/ordering fact
    no single body reveals), a **non-obvious why**, or a **limitation no tooth can observe** — and the
    exemption must be stated in **ONE plain sentence**. The exemption licenses the FACT, never the verbosity.
  - **`<see cref>` points OUTWARD** — counterpart, matching release site, the caller that establishes the
    precondition — **never at a callee the body already names** (use `<c>Name</c>` for an incidental mention,
    which creates no reference).
  - Design narrative, rationale and rejected alternatives live in `docs/*-design.md`, not in the file.
  - **Gate:** any block over its limit must name which of the three reasons applies, in one sentence, or be
    cut back. A block being exempt from CUTTING never exempts it from being READABLE.
  - Two anti-patterns worth naming: a **self-cancelling contrast** ("X has this hazard; Y does not, but Y
    is just as bad") resolves to nothing — state the hazard once; and **prose restating a `<param>` or
    `<exception>` tag** that is already present is pure duplication.
  - **No repo-wide boilerplate in a file-level doc.** A provenance note earns its place ONLY when it names a
    source the reader would otherwise have to guess — `ArabicJoining`'s "from PUBLIC Unicode data files",
    `EarthConstants`' "(IAU/WGS-84)", `Color`'s "no MapLibre output to match, so no parity oracle". A bare
    "Clean-room: no MapLibre source read." names nothing, is unverifiable, and **implies the files without
    it are not clean-room** — the opposite of what it intends. The claim is already authoritative in
    `ARCHITECTURE.md` § "Clean-room hygiene" and `THIRD-PARTY-NOTICES.txt`; one home is enough.

- **A code comment must not bake in a profiling number.**
  - A specific ms figure or "X IS the cost" goes stale the moment the regime shifts, and a stale number
    reads as durable fact to the next reader. A comment may state a *structural* fact that stays true
    (camera-independent vs per-frame); a measurement belongs in a dated design doc instead.

- **A prose mention or `<see cref>` must be earned by real code coupling.**
  - A file that only names `Foo` in a comment and never uses the `Foo` symbol should cut the mention — an
    IDE already navigates the real symbol, and the mention rots into a stale reference the moment `Foo` is
    renamed.
  - The same boundary runs the other way: a wording/naming convention governs prose we author, never a
    citation of a real file, type, or section title — rewriting a citation's words only breaks the
    reference, it renames nothing.

- **Image/golden test fixtures go in a `~`-suffixed folder.**
  - Every fixture here loads by file path, so Unity importing it as an asset is a pure side effect. For an
    image fixture that side effect is harmful — a plain `.png` under `Assets/` imports as a pixel-mutated
    `Texture2D` plus an unread `.meta`. Put it under a folder ending in `~`
    (`Assets/Fixtures/visual-references~/`) — Unity's importer ignores it entirely, while
    `File.ReadAllBytes` and a file browser still see the original bytes.

- **Test code must not bloat the production codebase.** A member that exists solely for a test does not belong
  on the production class.
  - Allowed footprint: broaden `private` → `internal` (+ `InternalsVisibleTo`), or put computed
    accessors/adapters as extension methods in the **test** assembly.
  - Not allowed: `public` members with no production caller, `// for testing only` members, test
    helpers/factories inside production classes.
