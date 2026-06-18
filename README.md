# unity-map-renderer

A **Unity-native** engine for rendering MapLibre-style *layered* vector maps from vector tiles, built
on **DOTS (ECS + Burst + Jobs)** for performance. Pluggable **bring-your-own data source** layer.

Not a Cesium-style photorealistic 3D-tiles renderer — this is closer to Mapbox/MapLibre: vector tiles
+ style-driven layered rendering, implemented clean-room from the open specs.

> **Status:** early architecture / pre-spike. See [`ARCHITECTURE.md`](./ARCHITECTURE.md) for the full
> design, decisions, and roadmap.

## At a glance
- **Target:** Unity-native first (single engine).
- **Stack:** C# + DOTS — ECS for entity/render management, Burst + Job System for the data-parallel
  hot path (decode, tessellation, label placement), `NativeArray` / `Unity.Mathematics` to stay GC-free.
- **Data:** Mapbox Vector Tiles (MVT) now; MapLibre Tiles (MLT) and raster later. Any source via a
  small fetch interface.
- **Approach:** spec-first clean-room (MVT / MLT / MapLibre Style Spec). Own the architecture and the
  differentiators; vendor solved subfields (triangulation, text shaping) as permissive dependencies.

## License
Proprietary — all rights reserved. Not open source.
