# Coding conventions

Generic, cross-cutting conventions for the codebase. These are the canonical, expanded versions of the
short rules listed under **Editing conventions** in `AGENTS.md` — when a rule needs more than one line of
explanation or an example, it lives here and `AGENTS.md` points at it.

Project-specific design docs live alongside this one: `ARCHITECTURE.md` (the big picture),
`docs/coordinates-and-projections.md` (the math foundations), `docs/lessons-learned.md` (hard-won
engineering gotchas). This file is for *how we write code*, not *what the system does*.

---

## Math types: `Unity.Mathematics` only

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

### `System.Math` is banned

Starting S62, `System.Math.*` is **banned in all production `.cs` files** (`Core/`, `Jobs/`, `Unity/`).
Use the equivalent `Unity.Mathematics.math.*` free function instead.

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

*(Established S62.)*

---

## Pass large read-only structs by `in`

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

---

## Data carriers: object-initializer construction; geo coords are `(Latitude, Longitude)`

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

---

## Test code must not bloat the production codebase

If a member exists solely to satisfy a test, it does not belong in the production class.

- **`internal` + `InternalsVisibleTo`** — the only acceptable production footprint: broaden a `private`
  member to `internal` when a test needs it. Nothing else changes on the production type.
- **Extension methods in the test assembly** — computed accessors, adapters, drain/settle helpers: add
  them as `static` extension methods in the test assembly (e.g. `MapViewTestExtensions`), forwarding
  through `internal` members. No production-class changes required.
- **Not allowed:** `public` members with no production caller; members annotated `// for testing only`;
  test setup helpers or factories inside production classes.

*(Established 865cf5f — MapView / TileManager test-surface refactor.)*

---

## Angles are an `Angle` value type, not a bare `double`

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

## Type-explicit builder naming

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

## Mesh lifetime & ownership: data is a value type, the `Mesh` is a single-owner class

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
