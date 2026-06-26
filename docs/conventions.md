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
- **`Core` is engine-free.** `Assets/MapRenderer.Core/` must not reference `UnityEngine` at all — it is a
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

### `System.Math` is banned in production code — use `math.*` from `Unity.Mathematics`

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
