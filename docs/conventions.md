# Coding conventions

Generic, cross-cutting conventions for the codebase. These are the canonical, expanded versions of the
short rules listed under **Editing conventions** in `AGENTS.md` — when a rule needs more than one line of
explanation or an example, it lives here and `AGENTS.md` points at it.

Project-specific design docs live alongside this one: `ARCHITECTURE.md` (the big picture),
`docs/coordinates-and-projections.md` (the math foundations), `docs/lessons-learned.md` (hard-won
engineering gotchas). This file is for *how we write code*, not *what the system does*.

---

## Math & numeric types

### Math types: `Unity.Mathematics` only

Use **`Unity.Mathematics`** for all vector / matrix / quaternion math:

- `float2` / `float3` / `float4`, `double2` / `double3`, `int2` / `int3`
- `quaternion`, `float4x4` / `double4x4`
- the free functions in `math.*` (`math.normalize`, `math.dot`, `math.lerp`, …)

Do **not** use `UnityEngine.Vector2` / `Vector3` / `Vector4` / `Quaternion` / `Matrix4x4` for our own
computation or storage.

**Why:**
- **`Core` is engine-free.** `Assets/Code/MapRenderer.Core/` must not reference `UnityEngine` at all — it is a
  plain managed library that compiles and runs in the `Tools/core-tests` `dotnet` project (no Unity). Its
  only "engine" dependency is `Unity.Mathematics`, which the `core-tests` shim (`Tools/core-tests/Shim.cs`)
  mirrors. So in `Core`, `UnityEngine.Vector*` is not merely discouraged — it breaks the engine-free build.
- **Consistency + Burst.** `Unity.Mathematics` is the type system the Jobs/Burst code already speaks
  (`double2` alone appears 100+ times). Mixing in `UnityEngine.Vector*` forces conversion noise at every
  boundary and pessimizes Burst codegen.

**The one exception — Unity boundary APIs.** Some Unity APIs *require* a `VectorN`/`Quaternion`:
`Mesh.vertices`/`normals`/`uv`, `Material.SetVector`, `Transform.position`/`rotation`,
`Camera`/`Bounds`, gizmo/debug-draw calls. Convert **at that call site** and keep everything upstream in
`Unity.Mathematics`. The conversions are implicit or one-liners:

```csharp
// Core / Jobs / our own logic — Unity.Mathematics throughout:
float3 worldPos = ComputeVertex(...);          // not Vector3

// At the Unity boundary, convert where the API demands it:
mesh.SetVertexBufferData(verts, ...);          // verts are float3 — preferred, no conversion
material.SetVector("_Translate", (Vector4)new float4(tx, ty, 0, 0));  // SetVector needs Vector4
transform.position = (float3)origin;           // float3 → Vector3 is implicit
```

Keep the `VectorN` surface as thin as possible: convert in, convert out, never let it leak back upstream
into `Core` or the Jobs layer.

*(Established S60.)*

#### `System.Math` and `UnityEngine.Mathf` are banned

Starting S62, `System.Math.*` is **banned in all production `.cs` files** (`Core/`, `Jobs/`, `Unity/`).
Use the equivalent `Unity.Mathematics.math.*` free function instead.

**`UnityEngine.Mathf.*` is banned on the same terms** — it is the same rule wearing the engine's badge, and
it was the surviving exception S62 chose not to chase (`Core` cannot reference `UnityEngine` at all, so this
only ever bit `Unity/`). Same replacements as the table below, lower-cased: `Mathf.Max` → `math.max`,
`Mathf.Clamp01` → `math.saturate`, `Mathf.Deg2Rad`/`Rad2Deg` → the `Angle` type (see below), and so on.
Two traps specific to `Mathf`: every member is **`float`-only**, so a `Mathf` call sitting in a `double`
expression has already narrowed the value — check the surrounding precision when you migrate rather than
swapping the token; and `Mathf.Approximately` has no `math.*` equivalent, so write the epsilon comparison
explicitly. Vendored third-party code under `Assets/Code/ThirdParty/` is out of scope, as always.

| Banned | Replacement | Notes |
|--------|-------------|-------|
| `Math.Sin(x)` | `math.sin(x)` | All trig: `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`, `sinh` |
| `Math.Exp(x)` | `math.exp(x)` | |
| `Math.Log(x)` | `math.log(x)` | Natural log (1-arg) |
| `Math.Log(x, 2.0)` | `math.log2(x)` | Do NOT use 2-arg `math.log` |
| `Math.Log10(x)` | `math.log10(x)` | |
| `Math.Sqrt(x)` | `math.sqrt(x)` | |
| `Math.Pow(x, y)` | `math.pow(x, y)` | |
| `Math.Abs(x)` | `math.abs(x)` | `double` and `int` overloads both exist |
| `Math.Floor(x)` | `math.floor(x)` | |
| `Math.Ceiling(x)` | `math.ceil(x)` | |
| `Math.Round(x)` | `math.round(x)` | Banker's rounding (same default) |
| `Math.Min(a,b)` | `math.min(a, b)` | `double`, `float`, `int` overloads all exist |
| `Math.Max(a,b)` | `math.max(a, b)` | `double`, `float`, `int` overloads all exist |
| `Math.PI` | `math.PI_DBL` | **Use `PI_DBL` (double), NOT `math.PI` (float)** |
| `Math.E` | `math.E_DBL` | **Use `E_DBL` (double), NOT `math.E` (float)** |
| `Math.Cbrt(t)` | `math.pow(t, 1.0/3.0)` | Only valid for `t >= 0` (all production sites are) |

**Why `PI_DBL` not `math.PI`:** `math.PI` is a **single-precision float** constant. In double-precision
contexts (Mercator, ECEF, camera math), using it silently introduces ~1.2×10⁻⁷ relative error and
causes const-expression compile failures when used in `const double` field initializers (e.g. `WebMercator.WorldExtent`). Always use `math.PI_DBL` for `double` contexts.

**Test code:** The `Tools/core-tests` shim stubs all the math.* members above by delegating to
`System.Math`. The shim cannot detect `log2`/`round` divergence — only the EditMode Unity gate does.

*(Established S62; extended to `Mathf` 2026-08-03, when the last production `Mathf` call —
`MapCamera.SyncToCamera`'s near-clip floor — was migrated to `math.max`. The ban is documented, not
test-enforced: `System.Math` never had a structure test either, and the honest reason both hold is that
the list of offenders is a one-line grep away.)*

### Angles are an `Angle` value type, not a bare `double`

Never pass or store an angle as a bare `double` in camera code. Use the **`Angle` struct** (Core,
engine-free) instead — it stores degrees internally and is the project's **sole** home of the
`* math.PI_DBL / 180.0` conversion. Every trig call reads `.Sin` / `.Cos` or `.Radians` off the struct
instead of repeating the multiply.

**Construction is always explicit.** Use `Angle.FromDegrees(x)` or `Angle.FromRadians(x)` — the unit
must be visible at the construction site. There is **no implicit `double` conversion** on `Angle`; reading
a value always requires `.Degrees` or `.Radians`.

**Camera orientation params use `ConstrainedAngle`.** `CameraProperties.Heading` and `CameraProperties.Tilt`
are `ConstrainedAngle` — an `Angle` together with a `[lo, hi]` range and an
`AngleConstraint {Clamp, Wrap}` strategy. The stored value is always already in-range; the constraint
is enforced at construction, not on every read. Presets:
- `ConstrainedAngle.Heading(degrees)` — `[0, 360)` Wrap. Replaces the retired
  `CameraProperties.NormalizeHeading`.
- `ConstrainedAngle.Tilt(degrees)` — `[0, 90]` Clamp. `tilt=0` is top-down (camera forward =
  inverse of earth normal); `tilt=90` is parallel to the surface (horizon).
- `ConstrainedAngle.Clamped(degrees, lo, hi)` — runtime `[lo, hi]` Clamp (e.g. a per-call
  `maxPitch` limit distinct from the `[0, 90]` type invariant).

**What not to do:**
- `double heading = props.Heading;` — compile error (no implicit `double`); write
  `props.Heading.Degrees` or `props.Heading.Value` (the `Angle`).
- Open-coding `x * math.PI_DBL / 180.0` anywhere outside `Angle.cs` in camera code — the struct
  provides `Sin`/`Cos`/`Radians` to avoid it.
- Adding angle operators (`Angle + Angle`, `Angle - Angle`) speculatively. Add them only when an
  actual swept call site demands it. `ConstrainedAngle + Angle` does exist (accumulates a delta
  and re-applies the constraint) with its sole swept use in `ViewInput.ApplyTilt`.

**Out of scope for this rule** (explicitly): the 7 non-camera π/180 sites in `WebMercator.cs`,
`Ecef.cs`, `TileId.cs`, `Expressions/Color.cs`, and the geometry builders/Jobs use geo/projection
math, not camera-orientation angles, and are not required to wrap in `Angle`.

*(Established S68.)*

---

## Types & data modeling

### Pass large read-only structs by `in`

When a method only **reads** a struct parameter and that struct is larger than a couple of machine words
(roughly **> 16 bytes**), declare the parameter **`in`**. Passing by value copies the whole struct at every
call; `in` passes a read-only reference (one pointer) instead, and documents at the signature that the
method will not mutate the argument.

```csharp
// CameraProperties is a readonly struct ≈ 48 bytes (GeoCoordinate3D + 3 doubles).
// Smell — copied in full at every call:
public static CameraPropertiesUpdate ApplyZoom(CameraProperties current, double scrollDelta, …)

// Fix — read-only reference, no copy, intent explicit:
public static CameraPropertiesUpdate ApplyZoom(in CameraProperties current, double scrollDelta, …)
```

This is already the house style where it was thought about: `Expression.Evaluate(in EvaluationContext
context)` threads a `readonly struct` by `in` through the whole expression engine. The `ViewInput.Apply*`
and `CameraSystem`/`CameraPoseMath` methods that take `CameraProperties` **by value** are the inconsistency
this rule targets.

**The hard gate — `in` only pays off for a `readonly struct`.** On a struct that is **not** `readonly` (nor
has all-`readonly` members), the compiler must assume any member access *might* mutate, so it makes a hidden
**defensive copy** on *every* member read through the `in` parameter — which is **worse** than passing by
value once. So the rule is `in` ⟺ `readonly struct`. Our data structs (`CameraProperties`, `GeoCoordinate`,
`EvaluationContext`, `TileResponse`, …) are already `readonly struct`, so the gate is satisfied — just keep
them readonly. If a struct you want to pass `in` is *not* readonly, make it readonly first or leave it
by-value; never `in` a mutable struct.

**Small structs stay by value.** For anything ≤ ~16 bytes — `float2`/`float3`, `double2`, `int2`, `TileId`
— the reference indirection costs as much as or more than the copy, so pass by value. The win is for the
chunky aggregates (camera state, evaluation contexts, vertex/material descriptors), not the little ones.

**`in` is read-only intent, full stop.** It is the counterpart to `out`/`ref`: use it precisely when the
method must *not* write the argument. Never reach for it to "save a copy" on a parameter you also mutate
(you can't — `in` is readonly), and be aware that passing a property, literal, or implicitly-converted value
to an `in` parameter materializes a hidden temporary — fine for the call sites here, but don't `in` a hot
inner-loop parameter that is always a fresh temporary.

**Burst / Jobs.** `in` is fully supported and idiomatic in Burst code and job structs — the same size gate
applies; prefer it for the larger blittable aggregates passed into `Execute`.

*(Established 2026-06-26 — noticed in S62's `ViewInput` / `CameraProperties` by-value pass; `in
EvaluationContext` is the prior-art that got it right.)*

### Data carriers: object-initializer construction; geo coords are `(Latitude, Longitude)`

**Prefer object initializers over positional constructors.** When a type is a plain data carrier (no
validation or computed construction), expose **`init`-only auto-properties** and construct with named
members — `new GeoCoordinate { Latitude = 52.52, Longitude = 13.40 }` — not a positional ctor. It is
self-documenting and order-proof: arguments you must name cannot be transposed. Add a positional ctor
only when one is *genuinely* needed (validation, or a hot Burst path that measurably benefits). `init`
keeps the type immutable (settable only at construction) and the backing fields stay blittable
(Burst / `NativeArray`-safe).

**`init` needs a polyfill under Unity.** Unity 6's netstandard reference assemblies do not ship
`System.Runtime.CompilerServices.IsExternalInit`, so `init` fails with CS0518 until each assembly that
*defines* init members includes a one-line `internal static class IsExternalInit {}`.

**Spell names out — avoid abbreviations.** Prefer full words to ad-hoc contractions: `Latitude` /
`Longitude`, not `Lat` / `Lon`; `Coordinate`, not `Coord`. Abbreviations save a few keystrokes but cost
readability at every read site. (Established domain acronyms — `Mvt`, `Ecef`, `Id` — and the
`Unity.Mathematics` `math.*` names are fine; this targets hand-shortened identifiers, not vocabulary.)

**Geo coordinates are latitude-first.** Geodetic types order components `(Latitude, Longitude[, Altitude])`
— never longitude-first — matching ISO 6709 / human convention. (Projected output stays `x = lon, y = lat`:
that is the projection *math*, not a naming choice — do the swap at the projection boundary, not in the type.)

*(Established S64.)*

### Prefer a plain auto-property over a field plus forwarding accessor

When a member is only get/set, declare it once as an auto-property with the visibility it needs:

```csharp
internal SymbolStringTable StringTable { get; }
```

not a private backing field plus a property that just forwards to it. The compiler already gives an
auto-property a backing field; a hand-written one adds a second name for the same storage and a line of
noise at every read. Raising a `private` field to `internal` for a test is the moment this rule is
usually broken — reach for the auto-property form, not a field-plus-accessor pair.

**An explicit backing field is earned only when the accessor does work a plain auto-property cannot** —
laziness (`_x ??= …`), change notification, a computed value, or validation a caller can actually
trigger. A defensive guard no caller can reach is not work: drop it and put the default in a property
initializer instead (`{ get; set; } = new X();`). A set-only property with an unread getter needs no
field either — `{ get; set; }` is fine on its own.

*(prior art: `StyledSymbolTileBuilder.StringTable`.)*

---

## Geometry & meshing

### Type-explicit builder naming

A type that **builds or owns a single geometry kind** must name that kind explicitly —
`StyledFillTileBuilder`, `StyledLineTileBuilder` — not a generic `MeshBuilder` or `TileMeshFactory`.
Generic, type-agnostic names are **reserved for genuinely type-agnostic dispatchers**: a `MeshBuilder`
should be a thing that builds *any* mesh by delegating to the kind-specific builders, never a fill-only
builder wearing a generic coat.

**Why:** a generic name on a single-kind builder lies about its scope — a reader (or an agent extending it)
assumes it handles lines too, and bolts line logic onto a fill builder. The retired Gen-1
`MeshBuilder` / `TileMeshFactory` were exactly this offence, and they are why the rule exists.

*(Established S54 — the retired Gen-1 `MeshBuilder` / `TileMeshFactory` were the naming offenders this rule
targets.)*

### Geometry producers declare their output winding; boundaries convert

A type that **produces triangle geometry** (`Earcut`, `LineTessellator`, `RibbonJob`, `GlobeFillSubdivideJob`)
must **state its output winding (CW/CCW) and coordinate space at the API surface** — in the XML summary of the
method or result type, not left for a consumer to reverse-engineer. There is **one canonical winding** for the
whole pipeline (**CCW in tile space**), every producer conforms to it, and the render-facing conversion happens
at **exactly one place per mesh kind**: the GPU mesh-write boundary (`StyledFillTileBuilder` /
`StyledLineTileBuilder`), where the canonical CCW is reversed to Unity-front for stock **Cull Back**.

**The rule in two halves — producers declare, boundaries convert:**
- A producer **never bakes the render convention** into its output. Winding-for-Unity-culling is a consumer-side
  concern (the same "convert at the Unity boundary, never upstream" rule as `double`→`float` / `double3`→`Vector3`).
  Baking it upstream would (a) leak a Unity assumption into engine-free `Core`, and (b) break the parity oracles
  that hash the canonical IR (`JobifiedPipelineTests`, `GlobeSubdivisionJobParityTests`).
- The conversion lives at **one** boundary per kind, stated in a comment that names the reflection cause
  (`docs/coordinates-and-projections.md` §7.1). Don't scatter per-projection or per-path winding flips — the
  winding is uniform by construction, so one reversal serves every projection.

**Why:** the winding a producer emits in 2D tile space is *inverted* by the time it reaches Unity's left-handed
render space (the load-bearing ECEF reflection, §7.1). If each producer's convention isn't stated, every consumer
re-derives the sign by hand — the exact "why do we reverse this?" confusion this rule removes. Pinned by
`GlobeFillWindingTests` / `GlobeLineWindingTests` (absolute: front face points out of the surface).

---

## Memory, performance & lifetime

### New data-plane code is born native

Design a new feature's **data plane** over native containers and blittable structs from the first commit —
do not write it managed and plan to nativize it later. Every hot subsystem here that took the managed-first
route had to be rewritten afterwards (symbols, then MVT decode); the "optimise later" saving has proven
illusory twice, and the representation answers it needed are now solved and reusable — tagged unions for
variants, a flat string pool for text, native columns for per-feature data.

The discriminator is structural, not a performance guess:

- **Data plane → born native.** Anything that recurs per tile / feature / vertex / glyph / frame, or is read
  inside a job. Blittable structs, `NativeArray`/`NativeList`/`NativeHashMap`, index handles, string pools.
- **Control plane → managed is correct.** Runs once per style load, per user action, per lifecycle
  transition; holds references or touches `UnityEngine.Object`. Native containers here buy nothing and cost
  legibility, debuggability and disposal safety.
- **The test:** *"will this be read inside a job, or loop over scene-sized data?"* If yes, native. If it is
  bounded by config size and touched once, managed.

A managed capture on the data plane is **disqualifying**, not merely slow: a body closing over a `Dictionary`
or a class reference cannot become an `IJob` at all until it is rewritten. Honor that up front.

This rule sits *above* the allocation ladder below, and does not replace its top rung: the best data-plane
code still allocates **nothing** per iteration, native or otherwise. See `ARCHITECTURE.md` §2.

### Column layout: one struct per element vs. one column per field

Once a field is native (the rule above), a second question follows: does *this* field get its own
`NativeArray`/`NativeList` column, or does it share a struct with the fields it sits next to? The answer is
an **access-pattern discriminator**, not a blanket preference for array-of-structs or struct-of-arrays:

- **Fields describing one element, read together at the same index → one struct (one column).** If every
  site that touches field A at index `i` also touches field B at index `i` in the same expression or the
  next line, splitting them buys nothing — it costs two cache lines per element instead of one, and doubles
  the index arithmetic for no vectorisation anyone performs.
- **A field streamed alone over a whole range → its own column.** If some pass reads or writes just that
  field across a range while never touching its neighbours, keep it split — merging it into a struct drags
  the unrelated fields through cache on every element and blocks Burst from vectorising the lone field.
- **The test:** *"what does one iteration touch?"* Walk the loop bodies, not the field list. A field with no
  loop that reads it alone is a merge candidate; a field with a loop that touches only it is not.

**Worked example, both directions, from `EarcutJob.cs`.** Its working buffer used to declare `Vx`/`Vy` as
two `NativeArray<double>` columns. Every access in the file touches both at the same index, usually in the
same expression (`Vx[t] = P[s].x; Vy[t] = P[s].y;`) or reads them from an already-interleaved
`NativeArray<double2>` input one line apart — no pass ever streams `Vx` alone. Worse, the split
*de-interleaved* an already-`double2` source array and read it back pairwise on every use. Merged into one
`NativeArray<double2> Verts`, the copy sites collapse to `Verts[t] = P[s];` and `Area2` takes `double2`
parameters instead of six loose doubles. Contrast the same file's `Next`, which correctly stays its own
column: `scan = Next[scan]` streams it alone on every ring walk, so merging position into it would drag the
vertex array through cache on every hop of a loop that never reads it.

**Co-access is necessary but not sufficient.** If a column's only same-index partner is itself streamed
alone somewhere, merging into it taxes that stream for the newcomer's benefit — so the newcomer stays split
despite reading together everywhere. In `EarcutJob.cs`, `IsEar` and `IsBridgeCopy` are read at the same
index as `Removed` on every touch, yet stay their own columns because `Removed` is walked alone by three
`Next`-only ring scans (`EarcutJob.cs:297`, `:629` in `CountRing`, `:391-403`).

**Not yet enforced across the codebase — this is a rule new and touched code is held to.** No prior
guidance on this existed before Vx/Vy was merged; older split-for-no-reason columns elsewhere have not been
swept.

*(Established during the `Vx`/`Vy` merge — see `EarcutJob.cs` and `TriangulationBuffers.FlatWorkVerts`.)*

### Hot-path allocations: none, then native, then pooled

A **hot path** is anything that runs per feature / per vertex / per glyph / per tile-build / per frame — the
loops that recur under pan and zoom. A managed allocation there is not a local cost: Unity's GC is
**stop-the-world**, so a single `new` on a worker thread can freeze *every* thread, main included, mid-frame.
(The rapid-zoom frame-rate stutter was exactly this — per-feature managed objects in off-thread decode and
mesh build.) The rule is a **descending ladder**; take the highest rung the situation allows, and only drop
to the next when the one above is genuinely impossible.

1. **Allocate nothing.** Reuse a buffer you already own, write in place, or hoist the allocation out of the
   loop (once per build, not once per element). For a small bounded collection, `FixedList*Bytes<T>` keeps it
   inline with no heap at all; `stackalloc` does the same for an **unmanaged** element type.

2. **Blittable/unmanaged data → native containers, not managed arrays.** If the elements are unmanaged
   (numbers, `float3`, indices, blittable structs), use `NativeArray<T>` / `NativeList<T>` / the native
   containers — they live **off the GC heap** (so they never trigger a collection) and they cross the
   job/Burst boundary. Choose the `Allocator` by **lifetime and thread**, not by habit:
   - **Off-main build scratch → `Allocator.Persistent`** (disposed via a `using`). `TempJob`'s 4-frame
     lifetime is measured in *main-thread* frames, which an off-main build (a worker spanning many main
     frames under load) overruns — the safety system then logs `deleting an allocation … older than 4
     frames` and can reclaim the buffer mid-build.
   - **Main-thread method scratch → `Allocator.Temp` / `TempJob`**, disposed within the frame.
   - Ownership and disposal follow the mesh-lifetime rule above (data is a value type, disposed
     deterministically at the consume boundary).

3. **Managed data that can't be native → pool it; never `new` per call.** Some element types hold references
   and cannot live in a `NativeArray` — e.g. `Value` carries a `string` / list / dictionary. Don't allocate
   one per call:
   - **Simple, non-reentrant temporaries → `System.Buffers.ArrayPool<T>.Shared`** (`Rent` / `Return` in a
     `finally`). It returns **oversized** arrays, so it is only safe where the consumer takes an explicit
     **length or `Span`** and never trusts `array.Length`.
   - **Managed collections/objects on the MAIN thread → `UnityEngine.Pool`** (`ListPool<T>`, `ObjectPool<T>`,
     `CollectionPool`, `GenericPool`) — the house tool for a `List` / `HashSet` / `Dictionary` / object rented
     and released within a scope (prior art across `MapRenderer.Unity`). It is **not thread-safe** (a bare
     `Stack`), so an **off-main** hot path (decode, mesh build, symbol extract) must use a per-thread pool
     instead — a `[ThreadStatic]` free-list, the `TileBuildScratch` per-build pool — whatever assembly it is
     in. `UnityEngine.Pool` is also engine-only, so engine-free `Core` reaches for `ArrayPool` / a hand-rolled
     pool — but that is a *consequence* of correct placement, never a reason to keep code in Core: per
     `ARCHITECTURE.md` §2, if a type would be materially better with a Unity pool it belongs in Unity/Jobs
     (Core is legacy, not a placement argument).
   - **Pool only scoped scratch, never a borrowed container.** Rent/release must bracket a scope the object
     never escapes. Releasing a `List` another object still references — a decoded layer's feature list handed
     out to consumers — clears it out from under the holder: a correctness bug, not a perf tweak.
   - **Reentrant or cross-thread hot paths → a `[ThreadStatic]` free-list of whole buffers.** When evaluation
     nests (one frame's buffer is still live while a nested frame borrows another) *and* the structure is
     shared across worker threads, a single shared buffer aliases and `ArrayPool` adds bookkeeping you don't
     need. A thread-local **free-list of distinct arrays** gives each live frame its own buffer with no
     cross-thread state. Prior art: `EvalArgBuffers` (the expression-evaluator argument buffers).
   - **Contract for any pool:** every `Rent` is paired with a `Return` in a `finally` — a throw mid-use
     (a failed coercion, a nested evaluation error) must not leak the buffer out of the pool, or the pool
     silently drains back to per-call allocation.

4. **Prove it with a tooth.** A GC-elimination change carries a **zero-allocation test, RED-verified** against
   the un-fixed code (re-inserting the allocation must make the test fail — otherwise the meter is dead, not
   the allocation). The working meter is **runner-specific**: use `Is.Not.AllocatingGCMemory()` (the
   Recorder-based constraint) in the Unity EditMode runner — `GC.GetAllocatedBytesForCurrentThread()` returns
   0 there for any allocation and a byte-delta tooth is vacuous; the thread-local byte delta works only in the
   `Tools/core-tests` real-.NET runner. Warm the exact measured delegate before asserting (the constraint can
   false-positive on the one-shot JIT of a microscopic path).

**Out of scope — cold paths.** Style/expression parse, static-table initialization, one-per-load setup, and
`$"…"` on an exception path run once (or only on failure). Pooling them trades readability for nothing; the
ladder is for the recurring loops only.

### A `using`-declared native container rejects an index write (CS1654)

A `using var`/`using (...)`-declared `NativeArray<T>` or `NativeList<T>` still compiles for a read
(`x[i]` on the right side), `.Add()`, `.Resize()`, and passing it by value — but an index *assignment*
(`x[i] = v`) fails with `CS1654`. Neither container is a `readonly struct` with a `readonly`-annotated
indexer setter, so the compiler cannot prove the write leaves the disposal target alone.

**Fix: take a plain, non-`using` writable view over the same allocation and write through that** —
`.GetSubArray(0, x.Length)` for a `NativeArray<T>`, `.AsArray()` for a `NativeList<T>` — rather than
dropping `using` for a manual `try`/`finally`:

```csharp
using var rankByOrdinal = new NativeArray<int>(count, Allocator.Persistent); // owns + disposes
NativeArray<int> rankByOrdinalWritable = rankByOrdinal.GetSubArray(0, rankByOrdinal.Length);
rankByOrdinalWritable[i] = rank;   // reads may use either local; writes use the view
```

The view is non-copying (same underlying allocation), so a write through it lands on the owned
container. Re-fetch a `NativeList` view after every `Resize` — its length must track the list.

*(prior art: `StyledFillTileBuilder.cs`'s `rankByOrdinalWritable`.)*

### A per-frame `ValueTuple` comparison key tops out at 7 elements

A `ValueTuple` used as a per-frame comparison key is allocation-free at 7 elements. At the 8th, the
generic shape changes: elements 1–7 stay in place and the 8th becomes a nested `ValueTuple<T8>`
(`TRest`), and constructing or comparing that nested shape allocates under Unity's Mono — comparing it
field-by-field instead of with `==` does not avoid this, because the cost is in holding the shape, not in
the comparison operator.

Past 7 fields, remove `ValueTuple` from the path entirely: a `readonly struct` implementing
`IEquatable<T>`, compared with `key.Equals(prev)`, with a plain `bool` held beside it rather than a
`Nullable<T>`. `MapView.SelectorInputs` is the shipped example.

### `Allocator.Temp` containers cannot be scheduled with `.Run()`

A container allocated with `Allocator.Temp` cannot be passed as a job field to a job dispatched with
`.Run()` — the Collections safety system rejects it, because `.Run()` counts as scheduling even though it
executes inline on the main thread. Use `Allocator.TempJob` for a per-call container, or a persistent
field allocated once (in the constructor) and reused, for scratch that runs once per tick.

### `if (x.IsCreated) x.Dispose();` — know whether it is redundant or load-bearing

Both exist in this codebase, and the difference is not cosmetic. **The discriminator is "can this release
site run twice?", not "is the container created?"**

**Redundant — delete it.** The dominant shape is a container allocated *before* a `try` and released once
in the `finally`. If the `finally` is running, the `try` was entered, so the allocation succeeded and
`IsCreated` cannot be false. The guard is worse than noise: it implies a partial-construction failure that
cannot occur, so a reader has to reconstruct what it protects and the answer is nothing. Equally redundant
in front of a type carrying its own struct-level `if (!IsCreated) return;` (`TileGeometryBuffers`,
`FillGraphOutput`) — the call-site guard just duplicates it — and in front of `NativeList<T>`, whose
`Dispose()` really does early-return on `!IsCreated` (`Unity.Collections/NativeList.cs:620-623`).

**Load-bearing — keep it.** A raw `NativeArray<T>` field that was allocated, disposed once, and may be
disposed *again* — the copy-mutate-writeback idempotency idiom:

    NativeArray<uint> tags = FeatureTagWords;
    if (tags.IsCreated) tags.Dispose();
    FeatureTagWords = tags;

A second `Dispose()` on an already-disposed `NativeArray<T>` throws; the type has no internal early-return,
so the guard **is** the idempotency mechanism, and the writeback is what lets the guard observe the disposed
state on the next call. Prior art with the reasoning in its own summary: `MvtLayer.Dispose`
(`Assets/Code/MapRenderer.Jobs/Mvt/MvtModels.cs:196-222`).

**The discriminator is "can this release site run twice on the SAME, already-disposed instance?"** — not
"might this value be absent?". A never-allocated `default(NativeArray<T>)` disposes cleanly, so absence is
not the hazard and a guard defending against it is noise. `TilePrologueOutput.Layers` is the worked example
post the per-layer build-object stage: a `BuildGraphRequest` call that found nothing to build
(`job-scheduling-design.md` §8 stage 5) returns `null` rather than a struct with uncreated fields, so the
slot holds `null`, and its disposal (`Layers[i]?.Dispose()`) skips the call entirely — the null-conditional
operator recognizes absence for a reference type exactly the way an `IsCreated` guard does for a value type,
without duplicating any type's own internal guard, because neither is a *second* dispose.

**This was measured the expensive way, so do not re-derive it.** `lessons-learned.md` carried this rule from
2026-07-03 as a flat "the guard is a bloat antipattern", on the stated premise that
"`NativeArray`/`NativeList`/etc. `.Dispose()` already early-returns on `!IsCreated`". **That premise is false
for `NativeArray`.** Acting on it, a blanket sweep deleted all 57 guards and reddened **106 tests** — every
path disposing a decoded MVT tile twice, across `Tests.Mvt`, `Style.FillPaintTests`,
`Style.SymbolFeatureExtractorTests`, `Text.SymbolBuffer*`. Exactly seven of the 57 were load-bearing (`MvtModels.cs` x4, `NativeFilterEvaluator.cs` x2, and
`MvtValueCompactionTests.cs` x1 — a test disposing once in its body and again in `finally`); the other 50
removals were correct. A rule that is
*mostly* right is the dangerous kind: it survives review because its examples are real, and it fails only
where nobody looked.

### `[ReadOnly]` on a job field — containers only, and never sweep it

> **`[ReadOnly]` belongs on a job field whose type contains a native container — including a generic type
> parameter, whose argument may. Elsewhere it is a no-op: harmless where it sits, not worth adding, and
> never worth removing in bulk.**

**What the attribute actually does.** It *modulates* Unity's schedule-time validation, which is driven by the
field's **type**; it never triggers it. Prior art from the other direction is already in the tree:
`RingAssemblyJob.cs:49-53` records that a `NativeArray` field left at its literal `default` fails that
validation *"even when `[ReadOnly]` and even when never read"*.

**Why a scalar annotation is inert rather than misleading.** A job field is a by-value copy, so nothing
outside the job can observe a write to it — "read-only" holds unconditionally with or without the attribute,
and the attribute cannot add a guarantee that already holds. Unity's own diagnostic for the mechanism is
phrased entirely in terms of containers, and the reflection pass that consumes the attribute is the one
walking native container fields; a field with no container is never on that walk. Unity itself ships
`[ReadOnly] public int Num;` across its Collections job test fixtures. There is no third state: as a scalar
annotation it is not meaningful and it is not actively wrong — `AttributeTargets.Field` simply does not
inspect the field's type.

**The generic exception, which is why there is a rule at all.** On a generic field the declaration site
cannot see whether the type argument holds a container:

- `FillGatherJob.cs:54` — `[ReadOnly] public TComparer Comparer;`. `TComparer` is
  `FillMeshPipeline.HoleRingComparer`, which holds two `NativeArray` fields built from the **same
  allocations** this job's `Vertices`/`RingOffsets` already view. Without the attribute the safety system
  sees one read-only and one implicitly writable alias of that allocation and throws at schedule time —
  `InvalidOperationException`, *"two containers may not be the same (aliasing)"*. Observed by
  `FillGraphBurstProbeTests`. **This attribute is load-bearing and a sweep would delete it.**
- `ProjectPointsJob.cs:45`, `GlobeFillSubdivider.cs:63` — `[ReadOnly] public TProj Projection;`. Inert today
  because every projection struct is stateless; load-bearing the moment one holds a container.

**Sweep verdict: no — not as its own change, and not opportunistically.** A repo-wide audit enumerated every
`[ReadOnly]` under `MapRenderer.Jobs/` and `MapRenderer.Unity/Rendering/Meshing/` (147 real attribute sites
across 27 files; 117 on containers, 30 not). The cost of "tidying" the 30 is a 12-file diff that changes no
behaviour and whose one plausible mechanical form — strip it from anything that isn't a `Native*` —
introduces a real schedule-time throw. Opportunistic removal is *worse* than a deliberate sweep here,
because it spreads that edit while its one dangerous case is invisible unless the editor happens to open
`FillGatherJob`'s doc. If the noise ever genuinely bothers someone it is a five-minute change *then*, on a
codebase that has not meanwhile been half-swept.

### Mesh lifetime & ownership: data is a value type, the `Mesh` is a single-owner class

Two resource classes, kept strictly apart (platform-forced, not stylistic — jobs can't touch a
`UnityEngine.Object`, and only the main thread can create/destroy the GPU resource):

- **Blittable geometry *data* = value-type structs in the job world** (`NativeArray`, `Mesh.MeshData`,
  `LayerMeshData`). Written by Burst/worker jobs, disposed **deterministically at the
  `ApplyAndDisposeWritableMeshData` boundary**, never held past consume — so a job-side struct **never carries
  a dispose-once guard** (a struct copy has its own flag; mutable dispose-state on a value type is a footgun).
- **The `Mesh` GPU *resource* = a reference-type class with exactly ONE owner.** Created/destroyed on the main
  thread only, owned by exactly one place at all times (`TileManager._loaded` in cover, `PreparedTileCache`
  out of cover — **Model B**); an ownership transfer **nulls the source reference** so the mesh is destroyed
  exactly once (that null-on-transfer is the double-free guard). Teardown is always **destroy meshes → dispose
  backend**.
- **Corollary:** dispose-guard machinery (a `VerifiedDisposable`-style base, the `CountMeshObjects` leak
  baseline) touches only the **class** side; struct data stays trivial. Two leak-guard systems, one per class:
  `NativeArray` alloc-vs-dispose (`DebugLiveAllocCount`) for data, `Mesh` created-vs-destroyed
  (`CountMeshObjects`) for the resource. (This is why S82 caches the `Mesh`, not the `NativeArray`.)

**Full contract** — the four disposal exit paths, cancellation-≠-cleanup, and the leak-guard teeth — lives in
**`docs/async-architecture.md` §"Disposal & cancellation contract"**; that doc is canonical, this is the
one-screen summary.

---


### Every loop is bounded

Code comments cite this rule as `always-bound-loops`.

Any `for`/`while` whose iteration count comes from projection, geometry, arc length, distance, or
style/user data takes a named `MaxX` ceiling. A real value is a handful of iterations; the cap exists only
for the pathological case, so pick it high — the requirement is that the count is *finite*, not that the
bound is tight.

The failure this prevents is not hypothetical. A line vertex sitting on the camera near plane projects to
a near-infinite screen coordinate, so `projectedLineLength / symbol-spacing` evaluated to millions of
anchors and the demo scene hung. It reproduced only when the camera sat where a road crossed the near
plane, which is why no ordinary run found it.

Two rules make the ceiling actually hold:

- **Clamp in float, then `math.min(..., cap)`, BEFORE any `(int)` cast.** A float larger than `int.MaxValue`
  casts to garbage or to a negative, so a cast that happens first defeats the clamp.
- **Guard the source as well as the loop.** Reject non-physical projected coordinates and non-finite
  lengths, and write the test as `!(x < Max)` rather than `x >= Max` so that NaN is also rejected.

Prior art: `SymbolStagingMath.MaxAnchorsPerLine`, `SymbolScreenProjection.MaxProjectedPx`,
`LayerInput.MaxOutputVertices`.


## Names carry meaning; filler words do not

A type or field name should tell a reader what the thing **holds or does**. A word that would fit equally
well on any temporary buffer anywhere in the codebase is doing no work, and its presence usually hides that
the name was never chosen.

**Banned as the substance of a name:** `Scratch`, `Data`, `Info`, `Manager`, `Helper`, `Util`, `Temp`,
`Stuff`. They are not forbidden tokens — `TileBuildScratchPool` may keep its name until something else makes
it worth touching — but none of them may be the part of the name that carries the meaning.

**Name for content, in the established shape.** This codebase already has the right idiom for a value that
groups buffers: `TileGeometryBuffers`, `FillGraphOutput`, `EvalArgBuffers`, `KeyBindingBuffers`. A new one
joins that family — `TriangulationBuffers`, not `EarcutScratch`.

**Never name a shared thing after one of its consumers.** `EarcutScratch` was used by four jobs (sizing,
gather, earcut, aggregate); naming it for the third of them was not merely vague but wrong, and it would have
misled every later reader about what may touch it.

**A qualifier that distinguishes is worth keeping.** `FlatWorkVerts` earns `Flat` — the column is flattened
across every polygon of the layer and indexed by an offset table. `FlatScratchWorkVerts` adds nothing with
`Scratch`. Ask of each word: if I deleted it, would the name become ambiguous? If not, it is filler.

**This rule is NOT yet enforced across the codebase — read it as what new and touched code is held to.**
As of 2026-09-03 the fill-graph path has been swept, and `Scratch` alone still appears about 160 times
elsewhere (symbols, `TileManager`, placement, `TileBuildScratch`/`TileBuildScratchPool`), alongside live
`PathScratch`, `CumScratch`, `cumScratch`, `keysScratch`, `valuesScratch`. Clearing the rest is an
outstanding mechanical sweep, deliberately sequenced so it does not move identifiers a stage plan is
citing while a developer executes against it.

Saying this matters, because stating a rule without it makes the rule read as already true. That is how
two of the examples above survived: `EarcutScratch` was renamed to `TriangulationBuffers` while the
field holding it stayed `Scratch` — the type was fixed and the concept was not — and `FlatScratchVx`
outlived by months the very passage that uses it as the specimen of what to fix.

See also *Type-explicit builder naming* and *Descriptive names, not positional* — the same principle at the
type and parameter level.

### Descriptive names, not positional or abbreviated

Name a variable, parameter, or out-parameter for its role, never its position or a shortened form.
`BillboardMath.BuildWorldQuad`'s four `out WorldBillboardVertex` parameters are `topLeft`, `topRight`,
`bottomRight`, `bottomLeft` — not `v0`/`v1`/`v2`/`v3`. When several things map to known positions (quad
corners, box edges), use those names; a positional name hides which corner or edge a reader is looking at.

**A `using X = Namespace.Type;` alias must be spelled out too.** A disambiguating alias is fine —
`using SymbolStyle = MapRenderer.Core.Style.Symbol;` avoids a real collision with the base style
hierarchy — but the alias itself must read as a name, never an abbreviation. Add an alias only to break an
actual collision, and keep it descriptive when you do.

See also *Spell names out* under Data carriers, which states the same principle for type members.

### Test names must not carry a stage identifier

A test file, class, or method name must describe its subject and behaviour — `ThrottleTests`, not
`S55ThrottleTests`. A stage identifier is ephemeral build bookkeeping; a reader with no stage log has no
way to decode it, and it makes the suite read like a changelog instead of a spec. The same principle
already governs a commit's scope (`docs/commit-conventions.md`): legible without looking anything up.

A stage identifier is still fine as provenance inside a doc comment or a design doc — the rule is only
about identifiers that appear in test output.

### Namespace segments must not collide with a bare UnityEngine type name

A namespace segment that equals a bare `UnityEngine` type name breaks every file inside it that also uses
that type: inside `namespace …Rendering.Material`, a bare `Material` resolves to the *namespace*, not the
type (the enclosing namespace wins over `using UnityEngine;`), and every use of `UnityEngine.Material`
inside that namespace then needs full qualification (`CS0118`). Pluralize the segment, or use an `-ing`
form, instead — `Materials`, `Meshing`, `GameObjects` are the collision-free forms already in use here.
Audit a proposed segment with `grep -rE 'UnityEngine\.<Name>\b'` before committing to it.

The same failure hits an unqualified `using`: `UnityEngine.Rendering` and `MapRenderer.Core.View.Camera`
both define a `CameraProperties`, so adding `using UnityEngine.Rendering;` to a file that already uses
this repo's own `CameraProperties` is a `CS0104` ambiguous reference. Qualify the newly-imported name (or
alias it) rather than touching the incumbent.


### Symbol, Text, Icon — and never Label

A symbol is a placed text AND/OR icon, matching the style spec's own `symbol` layer. That makes `symbol`
the umbrella term, and the naming follows it as a total partition:

- Touches only text → `Text*`.
- Touches only icon → `Icon*`.
- Touches both, either, or the placed unit as such (placement, collision, pairing, projection, the
  per-tile containers, the subsystem, the store, the render layers) → `Symbol*`.

**"Label" is eliminated.** Colloquially "label" means "text label", so using it for a type that acts on the
whole text+icon box wrongly narrows what the type does. "marker" is a point-icon subset of symbol, never
the umbrella.

Applied in full on 2026-08-24; this is the rule new code is held to, not a migration still to run. Some
occurrences are legitimate and are not violations: Unity's own `GUI.Label`, style-layer data strings, and
`LiteralLabel`.


## Documentation & tests

### Comments: a short doc on every member, nothing that restates the body

**Every member carries an XML doc, and it is short.** A reader should learn what a member is for, and what
its arguments mean, without opening the body. Add a `<param>` or `<returns>` when the name and type do not
already answer it — not as ceremony on every signature.

**These are limits, not guidance:**

| block | max |
|---|---|
| class / method `<summary>` | **5 lines** |
| `<param>` / `<returns>`, each | **2 lines** |
| inline `//` comment | **2 lines** |

Exceptions are allowed but **must be highly justified**, and in most cases the detail belongs in a
`docs/*-design.md` instead — with at most a pointer left at the code. An exemption licenses the FACT, never
the verbosity: state it in one plain sentence.

**Write in [ASD-STE100 Simplified Technical English](https://www.asd-ste100.org/).** One idea per sentence,
active voice, present tense, a plain word over a clever one. Cut anything that performs rigour instead of
delivering it — *deliberately*, *by construction*, *precisely*, *exactly as much*, *surfaced loudly*, *note
that*. The test for any sentence: **would a reader lose a FACT if it were deleted?** If not, delete it.

Three patterns that fail the test every time, all found in this codebase:

- **Self-cancelling contrast** — "X has this hazard; Y does not share it, but a hang there is exactly as
  much a bug." A paragraph that resolves to nothing. State the hazard once.
- **Restating a tag** — prose in the `<summary>` re-explaining what the `<param>` or `<exception>` tag
  immediately below already says, usually with adverbs added.
- **Repo-wide boilerplate in a file-level doc** — a bare "Clean-room: no MapLibre source read." appears in
  dozens of files. It names no source, nothing verifies it, and repeating it in *some* files implies the
  others are not clean-room. A provenance note earns its place only when it names a source the reader would
  otherwise have to guess: `ArabicJoining`'s "from PUBLIC Unicode data files", `EarthConstants`'
  "(IAU/WGS-84)", `Color`'s "there is no MapLibre output to match". The repo-wide claim already lives in
  `ARCHITECTURE.md` § "Clean-room hygiene" and `THIRD-PARTY-NOTICES.txt`; one home is enough.

A block being exempt from being CUT never exempts it from being READABLE — a 20-line doc that survives on a
genuine invariant is still wrong if 16 of those lines are exposition.

**It is normally also the ceiling.** Past the summary and the params, add prose only for something the body
*cannot* say. Three things qualify, and they are the only three:

- **A non-local invariant** — a protocol, lifetime or ordering fact no single body reveals. A
  `SharedDisposable<IDecodedTile>` cannot know who holds it, so "the creator's reference is released by `TileManager.RenderTeardownRecord`" is
  load-bearing. Four ownership bugs in one stage are the evidence this category is not obvious.
- **A non-obvious *why*** — a contested decision, a constant that looks arbitrary, a branch that exists for a
  defect someone would otherwise "fix" back in.
- **A limitation with no observing tooth** — where the honest answer to *"which test goes RED if this stops
  being deliberate?"* is **none can**, prose is the only carrier. Say that it is deliberate, and why.

**`<see cref>` points OUTWARD, never at a callee.** A method the body calls is already visible on the next
line; a cref to it duplicates the code and floods every Find Usages of that member with documentation hits.
Point at what the reader *cannot* see from here — the counterpart, the matching release site, the caller that
establishes the precondition. (A cref binds, so a rename updates it and a typo is CS1574; that property is
why it is worth using where it carries information, and why it is worth *not* using where it does not.
`<c>Name</c>` renders identically and creates no reference — the right tag for an incidental prose mention.)

**Design narrative belongs in `docs/`, not in the file.** Rationale, rejected alternatives and review history
already have an SSOT (`docs/*-design.md`). Link to it; do not inline it. Carrying the argument alongside the
code is how `DecodedTileLease.cs` reached **93 % comment** and `TileManager.cs` **68 %** across 2 425 lines.

**The gate:** past the summary and params, a doc that runs longer than the member it documents must name
which of the three reasons applies. If you cannot name one, cut it back to the floor.

*(Established 2026-08-10, after the decode-model epic. The over-documentation is a reflex with a real cause —
this repo has been burned by stale and false docs, and `e133181a` exists solely to correct four of them. The
cure is each fact in the right place once, not more prose in every place.)*

### No profiling numbers in code comments

A `//` comment must not bake in a profiling snapshot — a specific millisecond figure, "X IS the cost", a
ranked hot-spot verdict. A measurement is transient: the regime shifts, the code changes, an optimization
lands, and a stale number left in source reads as a durable fact to the next reader, steering their
reasoning the wrong way once it no longer holds.

A comment may still state a *structural* fact that stays true regardless of regime — "this path is
camera-independent; that one runs every frame" is an invariant, not a measurement, and belongs in the
code. A number, or a claim about which path costs more, belongs in a dated design document instead, next
to the capture regime (zoom, camera state, load) that produced it.

### A prose mention earns its place through real code coupling

Do not name-drop a type in a comment, `<summary>`, or `<see cref>` inside a file that has no code
coupling to it. A mention that exists only in prose earns nothing — an IDE already navigates the real
symbol — and it is worse than nothing: renaming the type is a symbol operation that never touches a
comment, so the mention rots into a stale reference to a dead name and inflates the apparent blast radius
of a future rename.

The same boundary runs the other way: a wording or naming convention governs prose we author, and never a
citation of a real file, type, or section title. Retiring a word from our own prose is one edit; applying
that edit to a citation only breaks the reference, because the thing being cited does not get renamed
along with it. Strengthen a citation instead of trimming it — cite the full path (`docs/some-design.md
§4`) rather than a bare, ambiguous section number.

### Image and golden test fixtures live in a `~`-suffixed folder

Every fixture in this codebase loads by file path (`File.ReadAllBytes` against `Application.dataPath`),
never through `Resources.Load` or the AssetDatabase — so importing one as a Unity asset is a pure side
effect of living under `Assets/`. For an image fixture (a golden PNG, any pixel reference) that side
effect is actively harmful: a plain `.png` imports as a `Texture2D` whose imported copy is pixel-mutated
(sRGB, compression, mipmaps) and mints a committed `.meta` file nothing reads.

Put a read-by-path image fixture in a folder whose name ends in `~` (`Assets/Fixtures/visual-references~/`
is the established one) — Unity's importer ignores a `~`-suffixed folder entirely, so there is no import,
no `.meta`, and no lossy copy, while `File.ReadAllBytes` and a plain file browser still see the original
bytes.

### Test code must not bloat the production codebase

If a member exists solely to satisfy a test, it does not belong in the production class.

- **`internal` + `InternalsVisibleTo`** — the only acceptable production footprint: broaden a `private`
  member to `internal` when a test needs it. Nothing else changes on the production type.
- **Extension methods in the test assembly** — computed accessors, adapters, drain/settle helpers: add
  them as `static` extension methods in the test assembly (e.g. `MapViewTestExtensions`), forwarding
  through `internal` members. No production-class changes required.
- **Not allowed:** `public` members with no production caller; members annotated `// for testing only`;
  test setup helpers or factories inside production classes.

*(Established 865cf5f — MapView / TileManager test-surface refactor.)*
