# Tools/core-tests/Shim — Unity.Mathematics subset (own code, no UCL)

## What this is

A minimal, TEST-ONLY mirror of a `Unity.Mathematics` subset so the real Core `.cs` files
compile under plain `dotnet test` without the Unity Editor and without taking on the Unity
Companion License (UCL). It is **own code** — not a copy or vendoring of Unity.Mathematics.

## Why it exists

`MapRenderer.Core` is engine-free (no `UnityEngine` dependency) but does use
`Unity.Mathematics` types (`double2`, `double3`, `math.*`). Running the fast
`dotnet test` loop requires those types to be available at compile time. Rather than
vendoring the real `Unity.Mathematics` package (UCL, not on NuGet, would double-compile
under Unity), we maintain a tiny shim here that stubs exactly the members Core references.

The real `Unity.Mathematics` runs inside the Unity EditMode gate (`./Tools/run-tests.sh`),
which is the **decisive numeric check** — it verifies that `math.*` functions produce
values identical to `System.Math.*` for the migrated sites (the shim delegates to
`System.Math` and cannot detect a divergence; only the EditMode gate can).

## Structure

```
Shim/
  README.md         — this file
  Types/            — one struct per file, matching the Unity.Mathematics public shapes
    double2.cs
    double3.cs
    float3.cs
    float3x3.cs
    float4.cs
  Constants.cs      — math.PI_DBL, math.E_DBL, math.PI (const — required for const expressions)
  Trigonometry.cs   — sin/cos/tan/asin/acos/atan/atan2/sinh/exp/log/log2/log10/sqrt/pow
  VectorMath.cs     — cross/dot/normalize etc.
  Common.cs         — min/max/abs/floor/ceil/round/sign/clamp (scalar and double2 overloads)
```

## How to extend

When adding a new Core file to `core-tests.csproj` that references a `math.*` member not
yet stubbed, the build will fail with CS0117 ("'math' does not contain a definition for
'foo'"). To fix:

1. Find the right domain file (trigonometry → `Trigonometry.cs`, arithmetic → `Common.cs`,
   vector → `VectorMath.cs`).
2. Add a one-line method to the `static partial class math` in that file, delegating to
   `System.Math`:
   ```csharp
   public static double foo(double x) => System.Math.Foo(x);
   ```
3. Re-run `dotnet test Tools/core-tests` to confirm green.

## License note

This shim is original code written for this project. It is NOT Unity.Mathematics and carries
NO Unity Companion License obligation. Do NOT copy actual Unity.Mathematics source here.
