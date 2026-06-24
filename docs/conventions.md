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
