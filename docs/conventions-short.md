# Coding conventions — short index

The **short, summary** form of this codebase's coding conventions — one entry per rule, enough to follow it
without opening anything else. For the *why* — rationale, tables, examples, gotchas — see the matching
section (same title) in **[`conventions.md`](conventions.md)**, the canonical human-facing reference.

This file is the summary `AGENTS.md` imports, so agents always carry the rules without carrying the whole
essay. Keep the two in sync: when a rule changes, edit `conventions.md` and update the matching line here.

---

- **`Unity.Mathematics` for all math (types *and* functions); `System.Math` and `UnityEngine.Mathf` are
  prohibited.**
  - *Types:* `float2/3/4`, `double2/3`, `int2/3`, `quaternion`, `float4x4` — **not** `UnityEngine.Vector2/3/4`
    / `Quaternion` / `Matrix4x4` for our own math or storage. `Core` is engine-free, so `UnityEngine.Vector*`
    is forbidden there outright. Sole exception: a Unity boundary API that *demands* a `VectorN`
    (mesh/material/transform) — convert at that call site, never upstream.
  - *Functions:* the `math.*` free functions — `math.sin`, `math.sqrt`, `math.abs`, `math.pow`,
    `math.min/max`, … — **not** `System.Math.*` (banned in production). For double-precision π/e use
    **`math.PI_DBL` / `math.E_DBL`**, not `math.PI` (a single-precision float that silently injects ~1e-7
    error and breaks `const double` initializers). **`UnityEngine.Mathf.*` is banned on the same terms** —
    `Mathf.Max` → `math.max`, `Mathf.Clamp01` → `math.saturate`, `Mathf.Deg2Rad/Rad2Deg` → the `Angle` type.
    `Mathf` is float-only, so a `Mathf` call inside a `double` expression has already narrowed the value —
    check precision when migrating, don't just swap the token. Vendored `ThirdParty/` code is out of scope.

- **Pass large read-only structs by `in`.** A method that only *reads* a struct param bigger than ~16 bytes
  (camera state, eval contexts, descriptors) takes it `in` — a read-only reference, no per-call copy, intent
  explicit. **Gate: `in` ⟺ `readonly struct`** — on a non-readonly struct, member access through `in`
  forces a *defensive copy* per read (worse than by-value). Small structs (`float3`, `double2`, `TileId`)
  stay by value. Prior art: `in EvaluationContext`.

- **Data carriers: object-initializer construction; geo coords are `(Latitude, Longitude)`.** Plain data
  carriers expose `init`-only auto-properties and are built with named members
  (`new GeoCoordinate { Latitude = …, Longitude = … }`), not positional ctors. Spell names out (`Latitude`,
  not `Lat`). Geodetic types are **latitude-first** `(Latitude, Longitude[, Altitude])`; the lon-first swap
  happens only at the projection boundary. (`init` needs the one-line `IsExternalInit` polyfill per
  assembly under Unity.)

- **Angles are an `Angle` value type, not a bare `double`.** The `* math.PI_DBL / 180.0` conversion
  lives **once**, inside `Angle.cs`; every trig site reads `.Sin`/`.Cos`/`.Radians` off the struct.
  Camera orientation params (`Heading`, `Tilt`) are `ConstrainedAngle` (an `Angle` + `[lo, hi]` +
  `AngleConstraint{Clamp,Wrap}` strategy) that enforce their range at construction. `Angle` uses
  explicit `FromDegrees`/`FromRadians` factories — no implicit `double` conversion. *(S68)*

- **Type-explicit builder naming.** A type that builds/owns a single geometry kind names it explicitly
  (`StyledFillTileBuilder`, `StyledLineTileBuilder`); generic names (`MeshBuilder`, `TileMeshFactory`) are
  reserved for genuinely type-agnostic dispatchers.

- **Geometry producers declare their output winding; boundaries convert.** A triangle-producing type (`Earcut`,
  `LineTessellator`, `LineRibbonJob`, `GlobeFillSubdivideJob`) states its output winding + coordinate space in
  its XML summary. There is **one canonical winding** (CCW in tile space); the producer never bakes the render
  convention. The Unity-front reversal for stock Cull Back happens at **one** boundary per mesh kind
  (`StyledFill`/`StyledLineTileBuilder`) — same "convert at the Unity boundary, never upstream" rule as
  `double3`→`Vector3`. Keeps `Core` engine-free and the parity oracles hashing canonical winding. Cause + full
  contract in `docs/coordinates-and-projections.md` §7.1; pinned by `GlobeFill`/`GlobeLineWindingTests`.

- **Test code must not bloat the production codebase.** A member that exists solely for a test does not
  belong on the production class. Allowed footprint: broaden `private` → `internal` (+ `InternalsVisibleTo`),
  or put computed accessors/adapters as extension methods in the **test** assembly. Not allowed: `public`
  members with no production caller, `// for testing only` members, test helpers/factories inside production
  classes.

- **Mesh lifetime & ownership: data is a value type, the `Mesh` is a single-owner class.** Blittable geometry
  *data* (`NativeArray`/`Mesh.MeshData`/`LayerMeshData`) are **value-type structs** the jobs write, disposed
  deterministically at the `ApplyAndDisposeWritableMeshData` boundary — never held, never a dispose-once guard.
  The `Mesh` GPU *resource* is a **reference-type class** created/destroyed **main-thread only** and held by
  **exactly one owner** (`TileManager._loaded` in cover / `PreparedTileCache` out of cover — Model B); a
  transfer **nulls the source** so it's destroyed once (the double-free guard); teardown = destroy meshes →
  dispose backend. Dispose-guard machinery + the `CountMeshObjects` leak baseline touch only the **class**
  side; structs stay trivial. Full contract (exit paths, cancellation, teeth) in
  **`docs/async-architecture.md` §"Disposal & cancellation contract"**.
