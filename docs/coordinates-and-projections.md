# Coordinates, Projections & Low-Level Math Rules

Foundational math the whole renderer builds on. Two projection modes are first-class:

- **Web Mercator (planar)** — EPSG:3857; the whole world on a flat square plane. The default map view.
- **ECEF globe** — Earth-Centered, Earth-Fixed 3D Cartesian; full spherical/ellipsoidal Earth.

Geometry has **one source of truth — geodetic (lon, lat, height)** — and *projection is a pure function
applied late*. Both modes feed the same geometry pipeline and shaders.

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

## 6. Projection interface
```csharp
interface IProjection {
    double3 Project(double lon, double lat, double height); // → render space (pre-RTC)
    double3 UpAt(double lon, double lat);                    // local up (extrusion / normals)
    double   MetersPerUnit { get; }                          // scale bookkeeping
    bool     IsOccluded(double3 renderPos, Camera cam);      // globe horizon cull; planar = false
}
// WebMercatorProjection : planar ;  EcefProjection : globe
```

## 7. Axis conventions (Unity is left-handed, Y-up) — LOCKED defaults
- **ECEF → Unity:** `unity = (X, Z, Y)` (swap Y/Z; flips right-handed Z-up → left-handed Y-up).
- **Mercator plane → Unity:** east → `+X`, north → `+Z`, elevation/height → `+Y`.
- Front-face winding: **CCW**. Globe surface normal for lighting = geodetic `up` (§3).

## 8. Low-level rules (invariants)
1. **Geodetic is the source of truth.** Projection is a pure, late-applied function — never store
   pre-projected positions as canonical.
2. **float64 core, float32 GPU (origin-relative).** No absolute world positions ever reach float32.
3. **RTC/floating origin always on** (both modes).
4. **Line width extrudes along the in-surface perpendicular:** `extrudeDir = normalize(cross(up, tangent))`.
   Planar: `up = +Z` ⇒ the 2D perpendicular. Globe: `up = geodetic normal` ⇒ ribbon hugs the surface.
   (Width itself is meters, converted to pixels per the styling model — see ARCHITECTURE.md §2.)
5. **Globe needs curvature subdivision:** large straight primitives (ocean/country fills, long lines) must
   be subdivided so chords don't cut through the sphere — threshold by angular span / sagitta tolerance.
   Planar mode: no subdivision.
6. **Consistent winding & normals** as in §7; verify no mirroring across the ECEF→Unity handedness flip.

## 9. DECISION PENDING — where projection is applied
- **CPU at build:** bake final positions into the mesh. Simple shader; **projection switch = full rebuild**;
  no smooth morph.
- **GPU per-vertex:** store stable intermediate (tile-local / geodetic), project in the vertex shader.
  Enables switch + **animated Mercator↔globe morph** (MapLibre-style) with no rebuild; more shader math,
  needs per-tile relative origins for precision.

Leaning **GPU per-vertex** (field-standard, enables the morph). CPU-build acceptable if a discrete toggle
is enough for v1. Resolve before locking the vertex format and shader contract.

---

## References
- **Web Mercator (EPSG:3857):** https://wiki.openstreetmap.org/wiki/Web_Mercator — confirms WGS84 datum
  + spherical Mercator projection.
- **ECEF:** https://en.wikipedia.org/wiki/Earth-centered,_Earth-fixed_coordinate_system — confirms axes
  (X→prime meridian, Y→90°E, Z→north pole), origin = Earth center of mass, right-handed, meters; inverse
  via Bowring (no closed form for lat/height).
