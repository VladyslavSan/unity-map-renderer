# Coordinates, Projections & Low-Level Math Rules

Foundational math the whole renderer builds on. Two projection modes are first-class:

- **Web Mercator (planar)** — EPSG:3857; the whole world on a flat square plane. The default map view.
- **ECEF globe** — Earth-Centered, Earth-Fixed 3D Cartesian; full spherical/ellipsoidal Earth.

Geometry has **one source of truth — geodetic (lon, lat, height)** — and *projection is a pure function
applied late*. Both modes feed the same mesh pipeline and shaders.

---

## 1. Constants (WGS84)
```
a   = 6378137.0                  # semi-major axis (m)
f   = 1 / 298.257223563          # flattening
b   = a * (1 - f) = 6356752.314245   # semi-minor axis (m)
e2  = f * (2 - f) = 0.00669437999014 # first eccentricity squared
R   = a = 6378137.0              # Web Mercator radius: SPHERICAL formula on WGS84 datum (uses a, ignores e2)
```
Web Mercator world extent: `±R·π ≈ ±20037508.342789 m`. Latitude limit: `±85.05112878°` (makes it square).

## 2. Web Mercator (planar)
Forward (λ, φ in radians):
```
x = R · λ
y = R · ln(tan(π/4 + φ/2))   ( = R · atanh(sin φ) )
```
Inverse:
```
λ = x / R
φ = 2·atan(exp(y / R)) − π/2     (Gudermannian)
```
> **EPSG:3857 quirk:** inputs are **WGS84 geodetic** lon/lat, but the projection math is **spherical**
> (`R = a`, eccentricity ignored). So **Mercator ignores `e2`; ECEF (§3) uses it.** Same lon/lat → two
> different projections — never cross the constants between them.

## 3. Geodetic ↔ ECEF (globe)
Forward — geodetic (λ, φ, h) → ECEF (X, Y, Z), meters:
```
N = a / sqrt(1 − e2·sin²φ)            # prime vertical radius of curvature
X = (N + h)·cosφ·cosλ
Y = (N + h)·cosφ·sinλ
Z = (N·(1 − e2) + h)·sinφ
```
Inverse — ECEF → geodetic: `λ = atan2(Y, X)`; latitude/height via **Bowring's** closed-form (or iterate).
Reference-only; the live path is geodetic → render space (forward), not the reverse.

Local **ENU tangent frame** at (λ, φ) — needed for line extrusion and globe lighting normals:
```
up    = (cosφ·cosλ, cosφ·sinλ, sinφ)   # geodetic normal
east  = (−sinλ, cosλ, 0)
north = cross(up, east)
```

## 4. Tile coordinates → world
MVT feature point `(px, py)` in tile space (`0..E`, `E = 4096`, origin top-left), tile `z/x/y`:
```
u = (x + px/E) / 2^z          # [0,1] longitude fraction
v = (y + py/E) / 2^z          # [0,1] latitude fraction (from north)
lon = u·360 − 180
lat = atan(sinh(π·(1 − 2v))) · 180/π
```
Then `lon/lat → Mercator` (§2) or `→ ECEF` (§3) depending on mode.

## 5. Render space & precision (RTC / floating origin) — mandatory in BOTH modes
- All core math in **float64** (`double` / `Unity.Mathematics double3`).
- Pick a **reference origin `O` (double)** near the camera (per-tile origin, or a scene origin rebased as
  the camera moves). GPU vertices are `(P_double − O_double)` cast to **float32**; the world/model matrix
  carries `O`. (Classic Cesium **Relative-To-Center**.)
- Why mandatory: planar coords reach ±20 M m, globe ±6.37 M m — float32 alone jitters badly at world scale.

## 6. Projection interface — ONE abstraction, a STRUCT usable in Burst (LOCKED principle)

**There is a single projection abstraction: `IProjection`, implemented by a stateless `readonly struct` per
projection (`WebMercatorProjection` : planar; `SphericalProjection` : globe). There is NOT a second,
hand-authored surface for the Burst jobs — and NOT a second interface for the camera.** The projection math
lives as ONE method (`ProjectPoint`) on the struct, called from BOTH the managed OOP path and the Burst job.
One source of truth ⇒ the managed path and the job path **cannot drift**.

**One interface, two concerns (maintainer decision, S91).** A projection is one concept; splitting its
behaviour across two interfaces would scatter "everything about WebMercator" into two places. So the SAME
`IProjection` carries both the **geometry** methods below AND the **camera-interaction** methods
(`ScreenToGround` / `GroundToScreen` / `ClampValidLatitude` — the camera only needs a few things).

```csharp
// Implemented by a STATELESS readonly struct per projection; chosen ONCE per session.
interface IProjection {
    // ── Geometry (build side) — GeoCoordinate = SURFACE point (no elevation) ──
    ProjectedPoint ProjectPoint(in GeoCoordinate geo); // → { World (pre-RTC), Up } — the shared kernel
    double3 Project(in GeoCoordinate geo);             // convenience → ProjectPoint(geo).World
    double3 UpAt(in GeoCoordinate geo);                // convenience → ProjectPoint(geo).Up
    double  MetersPerUnit { get; }

    // ── Camera interaction (managed side) ──
    GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties cam);
    double2         GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties cam);
    double          ClampValidLatitude(double latitudeDegrees);
    bool            IsFinitePlanarWorld { get; }                                    // Mercator=true, globe=false
    GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in CameraProperties cam); // finite: viewport ⊆ sheet; cyclic: identity
}
// readonly struct WebMercatorProjection : IProjection   // planar
// readonly struct SphericalProjection  : IProjection   // globe
```

**Finite sheet vs cyclic world (the camera constraint).** `IsFinitePlanarWorld` distinguishes a bounded planar
atlas (Web Mercator — hard edges) from a cyclic/closed world (the globe — wraps). The camera asks the projection,
carrying **zero** projection constants itself: `ViewInput` calls `ClampLookAtToWorld` (instead of a longitude
wrap) so the finite Mercator sheet cannot be panned off the viewport, and `CameraPoseMath.MinZoomFloor` picks
`MinZoomToFill` (world fills the viewport, no off-world margin — the shorter axis crops) for a finite world vs
`MinZoomToFit` (fits with margin) for the globe. The globe returns `ClampLookAtToWorld => cam.LookAt` (identity)
and keeps its `MinZoomToFit` floor, so its behaviour is unchanged. This is distinct from `TryGetHorizonOccluder`
(self-occlusion) and `MaxRefineAngleRad` (subdivision) — finiteness is its own fact. *Known follow-ups (deferred):*
(1) the clamp is heading/tilt-conservative (axis-aligned half-span, exact at tilt=0); a rotated/pitched view can
still show a corner sliver off-world. (2) nothing proactively re-clamps `camera.Zoom` upward when the floor
*rises* (a globe→Mercator swap or a viewport shrink) — safely masked today by the "world smaller than viewport →
lock to centre" guard in `ClampLookAtToWorld`, but if it ever reads as "camera briefly locked to centre after a
switch," raise the zoom to the new floor at the swap.

**The struct type IS the discriminator — no enum, no separate Burst copy.** Burst cannot hold a managed class
or do virtual dispatch, but a *struct* implementing an interface is a blittable value type. So the projection
job is generic over the concrete struct:

```csharp
[BurstCompile] struct ProjectPointsJob<TProj> : IJobParallelFor where TProj : struct, IProjection {
    TProj Projection; double3 OriginWorld;
    NativeArray<GeoCoordinate> Points; NativeArray<double3> WorldPositions, Normals;
    void Execute(int i) { var pp = Projection.ProjectPoint(Points[i]);   // Burst devirtualises + inlines
                          WorldPositions[i] = pp.World - OriginWorld; Normals[i] = pp.Up; }
}
```

Burst **specialises + inlines** `ProjectPointsJob<WebMercatorProjection>` and `<SphericalProjection>` — no
boxing, no branch, no enum. Each concrete instantiation needs a `[assembly: RegisterGenericJobType(...)]` line
for IL2CPP/AOT. The managed side picks the instantiation once per tile (`ProjectionDispatch`: `is
SphericalProjection` → `ProjectPointsJob<SphericalProjection>`) — the one place projections are enumerated. On
the managed side, assigning a struct to `IProjection` boxes it **once per session** (the camera holds it);
negligible. This keeps engine-free `Core` free of `Unity.Burst` (the struct methods carry no Burst attribute;
Burst compiles them transitively via the job's call graph).

**The projection job does ONLY projection.** It takes `GeoCoordinate[]` (SURFACE points — no elevation) →
world + normals. The tile→geodetic step is a separate, projection-independent `TileToGeoJob`, so the projection
job is reusable for any geodetic input (symbols, and later terrain grids). `GeoCoordinate` (2D) encodes the
surface-only scope; **elevated `GeoCoordinate3D` geometry is a future, more-complex path** (a height field
supplies per-vertex altitude, and the shading normal comes from the height *gradient*, not `ProjectPoint`).

**Rejected alternatives (for the record):** an enum + `switch` in the job (works, but a parallel enum to keep
in sync — the struct type already discriminates); a `ProjectionRules`/`ProjectionParams` blittable struct (adds
per-projection state the stateless model doesn't want); Burst `FunctionPointer`s (force `Unity.Burst` +
`AOT.MonoPInvokeCallback`/`UnityEngine` into engine-free `Core`, breaking the fast dotnet core-tests). The
interim `ProjectionMode` enum (S61) is retired. Camera-interaction code (S63) uses `IProjection` **directly**
on the managed side (through the boxed struct; no Burst constraint there).

## 7. Axis conventions (Unity is left-handed, Y-up) — LOCKED defaults
- **ECEF → Unity:** `unity = (X, Z, Y)` (swap Y/Z; flips right-handed Z-up → left-handed Y-up).
- **Mercator plane → Unity:** east → `+X`, north → `+Z`, elevation/height → `+Y`.
- Winding: geometry is **constructed CCW** (canonical earcut/ribbon IR), then **reversed to Unity-front at the
  GPU mesh-write boundary** (`StyledFill`/`StyledLineTileBuilder`) so stock **Cull Back** (`_Cull:2`) keeps the
  camera-facing surface (§7.1). Globe surface normal for lighting = geodetic `up` (§3).
- **Camera tilt is defined relative to the surface normal at `LookAt`.** `tilt = 0°` ⇒ the camera view
  (forward) vector is the **inverse of the earth normal at `LookAt`** (top-down); `tilt = 90°` ⇒ the view is
  **parallel to the surface at `LookAt`** (horizon), and is the limit ⇒ range `[0°, 90°]`. The normal is
  **projection-dependent**: constant `+Y` on Mercator, the geodetic normal `IProjection.UpAt(lon, lat)` (§6)
  on the globe — so on Mercator `tilt=0` is directly overhead (matches `CameraPoseMath.ComputeRelativePose`).
  (`Tilt`/`Heading` become `ConstrainedAngle` camera params — S68.)

### 7.1 Handedness, winding & why the ECEF reflection is load-bearing
*(the general coordinate-systems lesson — settled empirically by the de-reflection experiment, 2026-07-05)*

**The `(X,Z,Y)` swap is a reflection, and that is not a bug.** Swapping two axes has determinant −1.
ECEF is right-handed (Z-up); Unity render space is left-handed (Y-up). Mapping one to the other **must** be an
orientation-reversing (odd) transform — there is no proper rotation from a right-handed to a left-handed frame.
So `render = (X_ecef, Z_ecef, Y_ecef)` is a reflection *by necessity*.

**Why it's load-bearing — the rebase.** Globe geometry renders as `rebase · (project(v) − sceneOrigin)`, where
`rebase = transpose(IProjection.TangentBasisAt(lookAt))` is the per-frame tile-transform rotation (`SceneFrame.Rebase`).
A backend converts it to a quaternion (`new quaternion(frame.Rebase)`) — and **a reflection has no quaternion**, so
`rebase` MUST be a proper rotation (det +1). It is, because the reflection in `ProjectPoint`/`Ecef.Forward` (det −1)
is exactly cancelled by the `(East, Up, North)` **column ordering** of `Ecef.TangentBasis` (also det −1):
`(−1)·(−1) = +1`. The reflection and the column order are a **matched pair**. `GlobePlacementTests` guards this
directly: *"det(B) must be +1 (proper rotation)"*.

**Winding: canonical CCW construction, reversed once at the GPU boundary.** After the rebase, both projections
render in the same right-handed local frame — there is no world-handedness difference to "fix", and both paths
wind **consistently by construction** (S100): fills map all three triangle vertices through one `ProjectPoint`,
and lines tie `across = cross(along, up)` to the same `up` the centerline was projected with — so the canonical
winding is uniform across fills, lines, and every projection, with **no per-projection flip** (`ReversesWinding`
/`IsCurved` were retired — §8 rule 5; there is no handedness/curvature lookup). But that canonical order is
**CCW**, and the ECEF→render reflection makes CCW render back-faces toward the camera. So the **GPU mesh-write
boundary reverses triangle winding once, uniformly** — both `StyledFillTileBuilder` fill writes, the globe
subdivide output, `StyledLineTileBuilder`, and the test `SyntheticLineMesh.Upload` — to yield a genuine
Unity-front face for **stock Cull Back** (`MapFill`/`MapLine` `_Cull:2`, matching the already-stock
`MapSymbolText`). The upstream IR (`FillGraphOutput.TriangleIndices`, the subdivide job output) stays
CCW/convention-neutral so the earcut and globe-subdivide parity oracles hash raw winding. Pinned by
`GlobeFillWindingTests` / `GlobeLineWindingTests` — now an **absolute** check (front face points OUT of the
surface, dominant sign +1) *plus* globe == Mercator, so an inversion can't hide behind a relative-only compare
(the hole that let "front renders as back" ship).

**The de-reflection experiment (rejected).** Hypothesis: make Spherical right-handed like Mercator
(`(X,Z,Y)→(X,Z,−Y)`, a rotation) across `Ecef.Forward` / `TangentBasis` / `ProjectPoint` / `ReconstructView` +
inverse trig, and drop the line flip. Measured, on a branch:
1. **`det(TangentBasis) → −1`** ⇒ `GlobePlacementTests` fails (rebase is no longer a rotation ⇒ the tile-transform
   quaternion is garbage). The reflection is structurally required — confirming the matched pair above.
2. **Winding did not unify — it *swapped*.** A global de-reflection flips *every* mesh-local winding, so the fill
   inverted while the line became correct — you trade one flip for another. `GlobeFillWindingTests` went red
   (globe +1 vs Mercator −1); the line test went green. Net zero. *(This predates S100: winding is now uniform by
   construction, and a single boundary reversal serves both paths — so a de-reflection would just move the one
   reversal, not swap per-path flips. The load-bearing-reflection conclusion is unchanged.)*
3. **Snapshots were unchanged (a render no-op).** The coordinated de-reflection composes straight back through the
   rebase (`rebase' · project'` = `rebase · project`), so the *rendered pixels* are identical — the reflection is an
   internal representation detail, invisible on screen.

**Takeaways (general):**
- A right↔left handedness change is necessarily a reflection; you cannot "rotate it away".
- If a reflection is composed into a downstream **rotation** (here `rebase`), it is load-bearing — moving it flips
  the downstream determinant and breaks any rotation-only consumer (quaternion, `LookRotation`).
- Winding correctness lives with the geometry **construction**, not the projection's world handedness — and
  (S100) it is uniform across paths (`across = cross(along, up)` ties the ribbon to the centerline's `up`), so
  one reversal at the mesh-write boundary serves fills and lines on every projection: no per-path, no
  per-projection flip.
- When handedness reasoning gets slippery, **don't derive the sign — calibrate to a known-good reference and test**
  (both winding tests calibrate the globe against Mercator; snapshots catch a mirror the winding sign can't).

## 8. Low-level rules (invariants)
1. **Geodetic is the source of truth.** Projection is a pure, late-applied function — never store
   pre-projected positions as canonical.
2. **float64 core, float32 GPU (origin-relative).** No absolute world positions ever reach float32.
3. **RTC/floating origin always on** (both modes).
4. **Line width extrudes along the in-surface perpendicular:** `across = normalize(cross(along, up))`.
   Planar: `up = +Y` ⇒ the 2D perpendicular. Globe: `up = geodetic normal` ⇒ ribbon hugs the surface.
   (Width itself is meters, converted to pixels per the styling model — see ARCHITECTURE.md §2.)
   *(Implemented S100 — `LineRibbonJob` builds the ribbon in 3D from a `(point, up)` array. NOTE the sign:
   `cross(along, up)`, NOT `cross(up, along)` — the two differ by a reflection, and only the former winds the
   flat Mercator ribbon like the confirmed-correct 2D reference. The sign is **calibrated** to that oracle,
   not derived from handedness (§7.1); tying `across` to the same `up` the centerline was projected with makes
   winding correct by construction for every projection, so there is no per-projection winding flip.)*
5. **Globe needs curvature subdivision:** large straight primitives (ocean/country fills, long lines) must
   be subdivided so chords don't cut through the sphere — threshold by angular span / sagitta tolerance.
   Planar mode: no subdivision. *(Implemented S100 — the projection declares the tolerance as
   `IProjection.MaxRefineAngleRad`; Mercator returns `∞`, so the split count falls out to zero and "no
   subdivision" is the degenerate value, not a capability flag. Retired `IsCurved`/`ReversesWinding`.)*
6. **Consistent winding & normals** as in §7; verify no mirroring across the ECEF→Unity handedness flip.

## 9. DECIDED (2026-07-02) — CPU-at-build: project once during mesh modelling
**The projection is applied ONCE, on the CPU at mesh-build time** — bake the final projected
positions (origin-relative `float3`) **plus the per-vertex frame** (up / tangent / across) into the mesh.
The shader stays simple (positions are already final; it consumes the mesh-supplied frame, no per-vertex
projection). The `IProjection` stateless `ProjectPoint` math is consumed on the **build/Burst side** (in the
mesh-build worker), not in the shader.

**Rationale (maintainer):** projection is a **launch-time config constant** — chosen once at app start
(Bootstrapper) and held for the session; switching it mid-app is not a supported use case. So the one real
advantage of GPU-per-vertex (the animated Mercator↔globe morph, no rebuild) buys nothing here, and its cost
(per-vertex shader projection + per-tile relative-origin precision handling) is pure overhead. "Model the
mesh once and forget." A projection change, if ever needed, is a full rebuild — acceptable and out of scope.

*(Rejected alternative — GPU-per-vertex:* store a stable geodetic/tile-local intermediate in the mesh and
project in the vertex shader; enables a rebuild-free MapLibre-style morph. Field-standard, but only pays off
when projection is a live runtime toggle — which it is not here.)*

This locks the **vertex format** (final positions + baked frame) and the **shader contract** (frame-consuming,
no projection). Downstream: curvature subdivision (§8.5) is a **build-time pass**; line width extrudes along
the baked `cross(up, tangent)` (§8.4).

---

## References
- **Web Mercator (EPSG:3857):** https://wiki.openstreetmap.org/wiki/Web_Mercator — confirms WGS84 datum
  + spherical Mercator projection.
- **ECEF:** https://en.wikipedia.org/wiki/Earth-centered,_Earth-fixed_coordinate_system — confirms axes
  (X→prime meridian, Y→90°E, Z→north pole), origin = Earth center of mass, right-handed, meters; inverse
  via Bowring (no closed form for lat/height).
