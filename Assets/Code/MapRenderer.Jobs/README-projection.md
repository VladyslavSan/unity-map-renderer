# Projection

Turning tile-local coordinates into render-space world positions, for whichever projection the session is
running. Small, and load-bearing: every vertex the renderer draws passes through here.

`docs/coordinates-and-projections.md` is the maths SSOT — read it before changing anything in this
directory. This file is the map.

## The chain

```
  tile-local double2 in [0, Extent], Y-down          (TileGeometryBuffers.Vertices)
        │
        │  TileToGeoJob                 tile-local → geodetic (lat/lon)
        ▼
  GeoCoordinate
        │
        │  ProjectPointsJob<TProj>      geodetic → render-space world, relative to a
        ▼                               floating origin (RTC), per the chosen projection
  double3 world positions (+ per-vertex surface normal on a curved projection)
```

`ProjectionDispatch` is the seam between the two halves of that: callers hold an `IProjection` (an
interface), Burst needs a concrete struct type parameter, and the dispatch is the switch that turns one
into the other — `RunTyped<TProj>` / `ScheduleTyped<TProj>` per known projection.

## Why the enumeration is closed

The dispatch is a `switch` over concrete projection types, and adding one means adding a case **and** a
`RegisterGenericJobType` line. That is deliberate: Burst compiles a separate specialisation per type
parameter, so a projection that is never named here is never compiled, and a generic fallback would either
not exist at runtime or silently fall back to managed code — a performance cliff rather than a visible
failure.

The generic entry points are `internal` so a test can drive a projection production never registers (the
right-handed-sphere winding tooth does exactly that), without that projection existing in the shipped
switch.

## Null is not a projection

A null `IProjection` used to mean "planar default" and was resolved in several places independently.
It now resolves **once**, where a projection first enters the system, and both dispatch entry points
**throw** on null rather than defaulting. That is why: two arms each carrying their own `?? DefaultProjection`
is exactly what lets them disagree without any test noticing.

## Files

| file | role |
|---|---|
| `TileToGeoJob.cs` | tile-local → geodetic |
| `ProjectPointsJob.cs` | geodetic → render-space world, generic over the projection struct |
| `ProjectionDispatch.cs` | the `IProjection` → concrete-type switch, run and scheduled forms |
